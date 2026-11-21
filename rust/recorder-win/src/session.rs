use std::fs;
use std::io::ErrorKind;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering::SeqCst};
use std::sync::{mpsc, Arc, Condvar, Mutex, MutexGuard};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use recorder_core::clock::RecordingClock;
use recorder_core::frame_math::{fit_within, to_even, PixelSize};
use recorder_core::layout::Layout;
use recorder_core::session::{serialize_session, LayoutChange, SessionFile, SourceEntry};
use windows::Win32::Foundation::{FILETIME, SYSTEMTIME};
use windows::Win32::Graphics::Direct3D11::ID3D11Texture2D;
use windows::Win32::Storage::FileSystem::FileTimeToLocalFileTime;
use windows::Win32::System::Time::FileTimeToSystemTime;

use crate::audio::{with_com, AudioIn};
use crate::compositor::Compositor;
use crate::error::{win, Error, Result};
use crate::factory::{RealFactory, SourceFactory};
use crate::gfx::Device;
use crate::screen::{looks_black, Frame, Screen};
use crate::targets::CaptureTarget;
use crate::video::{check_encoders, max_encoder_size, EncoderSupport, VideoOut};
use crate::wav::WavWriter;
use crate::webcam::{upload, Camera};

const LARGE_FRAME: i64 = 2560 * 1440;
const FULL_HD: PixelSize = PixelSize { width: 1920, height: 1080 };
const AUDIO_RATE: i32 = 48_000;
const CAMERA_RATE: i32 = 30;
const SOFTWARE_RATE: i32 = 30;
const FINAL_MAX_BEHIND: i64 = 30;
const MAX_BEHIND: i64 = 60;
const WARM_UP_LIMIT: Duration = Duration::from_secs(3);
/// How long stop waits for a slow final.mp4 encoder before it leaves the file short.
const CATCH_UP_LIMIT: Duration = Duration::from_secs(30);
/// The mix trails the clock by 100 ms, so a sound packet that arrives a little late is not replaced by silence.
const AUDIO_LAG: i64 = AUDIO_RATE as i64 / 10;
const TICKS_PER_SECOND: i64 = 10_000_000;

#[derive(Debug, Clone)]
pub struct RecordingOptions {
    pub target: CaptureTarget,
    pub output_root: PathBuf,
    pub frame_rate: i32,
    pub webcam_id: Option<String>,
    pub mic: bool,
    pub mic_id: Option<String>,
    pub system_audio: bool,
    pub layout: Layout,
    pub write_final: bool,
    /// The caller's own notes, written into session.json and the result at the start.
    pub extra_notes: Vec<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PreflightResult {
    pub encoders: EncoderSupport,
    pub frame_size: PixelSize,
    pub scaled_down: bool,
    pub looks_black: bool,
    pub frame_rate: i32,
    pub screen_file_size: PixelSize,
}

#[derive(Debug, Clone, PartialEq)]
pub struct RecordingResult {
    pub folder: PathBuf,
    pub files: Vec<String>,
    pub dropped_frames: i64,
    pub notes: Vec<String>,
    /// What the session really used, which differs from the preflight after a software fallback.
    pub frame_rate: i32,
    pub frame_size: PixelSize,
    pub screen_file_size: PixelSize,
}

fn recording_result(folder: &Path, file: &SessionFile, files: Vec<String>, notes: Vec<String>) -> RecordingResult {
    let screen = file.sources.iter().find(|e| e.file == "screen.mp4");
    let screen_file_size = screen
        .and_then(|e| Some(PixelSize { width: e.width?, height: e.height? }))
        .unwrap_or(file.frame_size);
    RecordingResult {
        folder: folder.to_path_buf(),
        files,
        dropped_frames: file.dropped_frames,
        notes,
        frame_rate: file.frame_rate,
        frame_size: file.frame_size,
        screen_file_size,
    }
}

/// What the caller's thread and the recording thread share.
struct Shared {
    folder: PathBuf,
    state: Mutex<State>,
    /// The number of the newest session.json text on disk. Held while writing, so writes never overlap.
    saved: Mutex<u64>,
    stop: AtomicBool,
    closed: AtomicBool,
    result: Mutex<Option<RecordingResult>>,
    done: Condvar,
}

struct State {
    file: SessionFile,
    layout: Layout,
    ended: bool,
    disk_noted: bool,
    /// Counts session.json texts, so an older text never replaces a newer one.
    saves: u64,
}

impl Shared {
    fn state(&self) -> MutexGuard<'_, State> {
        lock(&self.state)
    }

    /// Builds the text under the state lock and writes it after releasing the lock, so a slow disk never holds the lock.
    fn save(&self, mut s: MutexGuard<'_, State>, clock: &RecordingClock) {
        s.file.pauses = clock.pauses();
        s.saves += 1;
        let (text, number) = (serialize_session(&s.file), s.saves);
        drop(s);
        let mut saved = lock(&self.saved);
        if number < *saved {
            return;
        }
        let path = self.folder.join("session.json");
        let temp = self.folder.join("session.json.tmp");
        // A full disk, or another program holding the file open, leaves the last good session.json in place.
        if fs::write(&temp, text).and_then(|()| fs::rename(&temp, &path)).is_ok() {
            *saved = number;
        }
    }

    fn note(&self, text: String, clock: &RecordingClock) {
        let mut s = self.state();
        s.file.notes.push(text);
        self.save(s, clock);
    }
}

/// One recording: a thread draws and writes every file from one clock, so all files share frame and sample indexes.
pub struct RecordingSession {
    shared: Arc<Shared>,
    clock: Arc<RecordingClock>,
    thread: Option<JoinHandle<()>>,
}

impl RecordingSession {
    /// Call `init_process` first.
    pub fn preflight(o: &RecordingOptions) -> Result<PreflightResult> {
        RecordingSession::preflight_with(o, &RealFactory)
    }

    pub fn preflight_with(o: &RecordingOptions, factory: &dyn SourceFactory) -> Result<PreflightResult> {
        check_frame_rate(o.frame_rate)?;
        let files = 1 + u32::from(o.write_final) + u32::from(o.webcam_id.is_some());
        let encoders = check_encoders(files);
        let max = max_encoder_size();
        let device = Device::new()?;
        let mut screen = factory.open_screen(&o.target, &device)?;
        let mut frame = None;
        for _ in 0..100 {
            frame = screen.latest_frame();
            if frame.is_some() {
                break;
            }
            thread::sleep(Duration::from_millis(20));
        }
        let source = screen.source_size();
        let size = fit_within(source, max);
        // The encoders keep up with 4K at 30 frames per second and 1080p at 60.
        let big = i64::from(size.width) * i64::from(size.height) > LARGE_FRAME;
        let frame_rate = if encoders == EncoderSupport::SoftwareOnly || !device.video_support() {
            SOFTWARE_RATE
        } else if big {
            o.frame_rate.min(30)
        } else {
            o.frame_rate
        };
        Ok(PreflightResult {
            encoders,
            frame_size: size,
            scaled_down: size != to_even(source),
            looks_black: frame.is_none_or(|f| looks_black(&f, &device)),
            frame_rate,
            screen_file_size: if big { fit_within(size, FULL_HD) } else { size },
        })
    }

    pub fn start(o: RecordingOptions, p: &PreflightResult) -> Result<RecordingSession> {
        RecordingSession::start_with(o, p, Box::new(RealFactory))
    }

    pub fn start_with(o: RecordingOptions, p: &PreflightResult, factory: Box<dyn SourceFactory>) -> Result<RecordingSession> {
        // A hand-built result is not checked by preflight, and a 0 rate or size would divide by zero later.
        check_frame_rate(p.frame_rate)?;
        if [p.frame_size, p.screen_file_size].iter().any(|s| s.width < 2 || s.height < 2) {
            return Err(Error::Invalid("The video size must be at least 2 by 2 pixels.".into()));
        }
        let (name, started_at) = local_stamp(SystemTime::now())?;
        let folder = create_folder(&o.output_root, &name)?;
        let file = SessionFile {
            version: 1,
            started_at,
            frame_size: p.frame_size,
            frame_rate: p.frame_rate,
            target_kind: o.target.kind().into(),
            sources: Vec::new(),
            layout_changes: vec![LayoutChange { at_seconds: 0.0, layout: logged(&o.layout) }],
            pauses: Vec::new(),
            dropped_frames: 0,
            notes: Vec::new(),
        };
        let shared = Arc::new(Shared {
            folder: folder.clone(),
            state: Mutex::new(State { file, layout: o.layout.clone(), ended: false, disk_noted: false, saves: 0 }),
            saved: Mutex::new(0),
            stop: AtomicBool::new(false),
            closed: AtomicBool::new(false),
            result: Mutex::new(None),
            done: Condvar::new(),
        });
        let (ready, opened) = mpsc::channel();
        let (worker, p) = (shared.clone(), *p);
        let spawned = thread::Builder::new().name("recording".into()).spawn(move || {
            with_com(|| match Recorder::open(&o, &p, factory.as_ref(), worker) {
                Ok(recorder) => {
                    let _ = ready.send(Ok(recorder.clock.clone()));
                    recorder.run();
                }
                Err(e) => {
                    let _ = ready.send(Err(e));
                }
            })
        });
        let error = match spawned {
            Ok(thread) => match opened.recv() {
                Ok(Ok(clock)) => return Ok(RecordingSession { shared, clock, thread: Some(thread) }),
                failed => {
                    // Joining waits until the thread has closed every file it opened, so the folder can go.
                    let _ = thread.join();
                    match failed {
                        Ok(Err(e)) => e,
                        _ => Error::Invalid("The recording could not start.".into()),
                    }
                }
            },
            Err(e) => Error::Io(e),
        };
        let _ = fs::remove_dir_all(&folder);
        Err(error)
    }

    pub fn pause(&self) {
        let s = self.shared.state();
        if s.ended {
            return;
        }
        self.clock.pause();
        self.shared.save(s, &self.clock);
    }

    pub fn resume(&self) {
        let s = self.shared.state();
        if s.ended || self.shared.stop.load(SeqCst) || self.shared.closed.load(SeqCst) {
            return;
        }
        self.clock.resume();
        self.shared.save(s, &self.clock);
    }

    /// The recording thread picks up the new layout, and a new logo, on its next frame.
    pub fn set_layout(&self, l: Layout) {
        let mut s = self.shared.state();
        if s.ended {
            return;
        }
        let at_seconds = self.clock.media_time().as_secs_f64();
        s.file.layout_changes.push(LayoutChange { at_seconds, layout: logged(&l) });
        s.layout = l;
        self.shared.save(s, &self.clock);
    }

    pub fn elapsed(&self) -> Duration {
        self.clock.media_time()
    }

    /// True once every file is finished, after `stop` or when the recording stopped by itself.
    pub fn has_stopped(&self) -> bool {
        lock(&self.shared.result).is_some()
    }

    pub fn wait_stopped(&self, timeout: Duration) -> bool {
        let result = lock(&self.shared.result);
        let waited = self.shared.done.wait_timeout_while(result, timeout, |r| r.is_none());
        let (result, _) = waited.unwrap_or_else(|e| e.into_inner());
        result.is_some()
    }

    /// Finishes every file. After the recording stopped by itself, this returns the same result.
    pub fn stop(mut self) -> RecordingResult {
        self.end();
        let result = lock(&self.shared.result).clone();
        result.unwrap_or_else(|| {
            let s = self.shared.state();
            let mut notes = s.file.notes.clone();
            notes.push("The recording stopped unexpectedly.".into());
            recording_result(&self.shared.folder, &s.file, Vec::new(), notes)
        })
    }

    fn end(&mut self) {
        {
            let _s = self.shared.state();
            self.shared.stop.store(true, SeqCst);
            // Freezing the clock now gives every file the stop time, even while the loop is still busy.
            self.clock.pause();
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

impl Drop for RecordingSession {
    fn drop(&mut self) {
        self.end();
    }
}

struct Track {
    name: &'static str,
    file: &'static str,
    source: Option<Box<dyn AudioIn>>,
    wav: Option<WavWriter>,
}

/// Everything the recording thread owns. Dropping it finishes every file, on every way out of the thread.
struct Recorder {
    shared: Arc<Shared>,
    clock: Arc<RecordingClock>,
    window: bool,
    rate: i32,
    screen: Box<dyn Screen>,
    cam: Option<Box<dyn Camera>>,
    cam_frame: PixelSize,
    tracks: Vec<Track>,
    compositor: Compositor,
    screen_w: Option<Box<dyn VideoOut>>,
    final_w: Option<Box<dyn VideoOut>>,
    cam_w: Option<Box<dyn VideoOut>>,
    last_screen: Frame,
    last_cam: Option<Frame>,
    last_final: Option<ID3D11Texture2D>,
    logo: Option<String>,
    written: i64,
    final_next: i64,
    cam_written: i64,
    samples: i64,
    /// Mixed sound that final.mp4 has not been given yet, starting at sample `final_sound_at`.
    final_sound: Vec<f32>,
    final_sound_at: i64,
    behind_noted: bool,
}

impl Recorder {
    fn open(o: &RecordingOptions, p: &PreflightResult, f: &dyn SourceFactory, shared: Arc<Shared>) -> Result<Recorder> {
        let folder = shared.folder.clone();
        let device = Device::new()?;
        let mut screen = f.open_screen(&o.target, &device)?;
        let mut notes = o.extra_notes.clone();
        // A camera that will not open (busy, unplugged, no permission) only costs the camera, not the recording.
        let mut cam = None;
        if let Some(id) = &o.webcam_id {
            if let Ok(mut c) = f.open_webcam(id, &device) {
                cam = c.latest_frame().map(|frame| (c, frame));
            }
            if cam.is_none() {
                notes.push("webcam lost at 0.0 s".to_string());
            }
        }
        let mut tracks = Vec::new();
        if o.mic {
            tracks.push(Track { name: "mic", file: "mic.wav", source: Some(f.open_mic(o.mic_id.as_deref())?), wav: None });
        }
        if o.system_audio {
            tracks.push(Track { name: "system sound", file: "system.wav", source: Some(f.open_system_audio()?), wav: None });
        }
        let mut compositor = Compositor::new(&device, p.frame_size, p.screen_file_size)?;
        let logo = o.layout.logo.path.clone();
        notes.extend(compositor.set_logo(logo.as_deref().map(Path::new)));
        let black = upload(&[0, 0, 0, 255].repeat(4), PixelSize { width: 2, height: 2 }, &device)
            .map_err(|e| win(e, Error::Device, "The video frames could not be set up."))?;

        let cam_size = cam.as_ref().map(|(_, frame): &(_, Frame)| to_even(frame.size));
        let open_writers = |hardware: bool| -> Result<[Option<Box<dyn VideoOut>>; 3]> {
            let rate = if hardware { p.frame_rate } else { SOFTWARE_RATE };
            let open = |name: &str, size, rate, audio| f.open_writer(&folder.join(name), size, rate, &device, hardware, audio);
            Ok([
                Some(open("screen.mp4", p.screen_file_size, rate, false)?),
                if o.write_final { Some(open("final.mp4", p.frame_size, rate, !tracks.is_empty())?) } else { None },
                match cam_size {
                    Some(size) => Some(open("webcam.mp4", size, CAMERA_RATE, false)?),
                    None => None,
                },
            ])
        };
        let mut hardware = p.encoders != EncoderSupport::SoftwareOnly && device.video_support();
        let writers = match open_writers(hardware) {
            Err(Error::NotSupported(_)) if hardware => {
                // The writers of the failed try finished when they dropped, and a writer will not overwrite a file.
                for name in ["screen.mp4", "final.mp4", "webcam.mp4"] {
                    let _ = fs::remove_file(folder.join(name));
                }
                hardware = false;
                open_writers(false)?
            }
            other => other?,
        };
        let [screen_w, final_w, cam_w] = writers;
        if !hardware {
            notes.push("no hardware encoder, recording at 30 frames per second".into());
        }
        let screen_size = p.screen_file_size;
        if screen_size != p.frame_size {
            notes.push(format!("screen.mp4 scaled to {}x{}", screen_size.width, screen_size.height));
        }
        for t in &mut tracks {
            t.wav = Some(WavWriter::create(&folder.join(t.file))?);
        }

        let video = |file: &str, kind: &str, size: PixelSize| SourceEntry {
            file: file.into(),
            kind: kind.into(),
            offset_seconds: 0.0,
            width: Some(size.width),
            height: Some(size.height),
        };
        let mut sources = Vec::new();
        if final_w.is_some() {
            sources.push(video("final.mp4", "final", p.frame_size));
        }
        sources.push(video("screen.mp4", "screen", screen_size));
        if let Some(size) = cam_size {
            sources.push(video("webcam.mp4", "webcam", size));
        }
        for t in &tracks {
            let kind = t.file.trim_end_matches(".wav");
            sources.push(SourceEntry { file: t.file.into(), kind: kind.into(), offset_seconds: 0.0, width: None, height: None });
        }

        // Wait for the first real frame, so the files do not open on black.
        let mut last_screen = black;
        for _ in 0..50 {
            if let Some(frame) = screen.latest_frame() {
                last_screen = frame;
                break;
            }
            thread::sleep(Duration::from_millis(20));
        }
        let rate = if hardware { p.frame_rate } else { SOFTWARE_RATE };
        let started = Instant::now();
        let clock = Arc::new(RecordingClock::new(rate, Box::new(move || started.elapsed())));
        {
            let mut s = shared.state();
            s.file.frame_rate = rate;
            s.file.sources = sources;
            s.file.notes.extend(notes);
            shared.save(s, &clock);
        }
        let (cam, last_cam) = cam.map_or((None, None), |(c, frame)| (Some(c), Some(frame)));
        let mut recorder = Recorder {
            shared,
            clock,
            window: matches!(o.target, CaptureTarget::Window(_)),
            rate,
            screen,
            cam_frame: last_cam.as_ref().map_or(PixelSize { width: 0, height: 0 }, |f| f.size),
            cam,
            tracks,
            compositor,
            screen_w,
            final_w,
            cam_w,
            last_screen,
            last_cam,
            last_final: None,
            logo,
            written: 0,
            final_next: 0,
            cam_written: 0,
            samples: 0,
            final_sound: Vec::new(),
            final_sound_at: 0,
            behind_noted: false,
        };
        recorder.warm_up()?;
        // Sound captured during the warm-up would make every sound late by the warm-up's length.
        recorder.drain_sound();
        recorder.clock.start();
        Ok(recorder)
    }

    /// Writes the first frame as index 0 to every video file and gives the encoders up to 3 seconds to start,
    /// so their slow start happens before the clock runs and costs no frames.
    fn warm_up(&mut self) -> Result<()> {
        let layout = self.shared.state().layout.clone();
        if let Some(w) = self.screen_w.as_mut() {
            w.write_frame(self.compositor.draw_screen(&self.last_screen)?, 0)?;
        }
        if let Some(w) = self.final_w.as_mut() {
            let texture = self.compositor.draw_final(&self.last_screen, self.last_cam.as_ref(), &layout)?;
            w.write_frame(texture, 0)?;
            self.last_final = Some(texture.clone());
        }
        (self.written, self.final_next) = (1, 1);
        self.write_camera(1)?;
        let deadline = Instant::now() + WARM_UP_LIMIT;
        let busy = |r: &Recorder| [&r.screen_w, &r.final_w, &r.cam_w].iter().any(|w| w.as_ref().is_some_and(|w| w.frames_behind() > 0));
        while busy(self) && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(5));
        }
        Ok(())
    }

    /// Records until stopped or until something fails. Dropping `self` then finishes every file.
    fn run(mut self) {
        if let Err(e) = self.record() {
            let disk = matches!(e, Error::Io(_));
            self.shared.state().disk_noted |= disk;
            self.note(if disk { format!("disk error, recording stopped: {e}") } else { format!("recording stopped: {e}") });
            // Sound trails the video by up to 100 ms, so flush it to the stop time.
            self.clock.pause();
            if !self.tracks.is_empty() {
                let r = self.write_sound(true);
                self.quietly(r);
            }
        }
    }

    fn record(&mut self) -> Result<()> {
        loop {
            let requested = self.shared.stop.load(SeqCst);
            let stop = requested || self.screen.is_closed();
            if stop {
                if !requested {
                    self.shared.closed.store(true, SeqCst);
                    let why = if self.window { "the window was closed" } else { "the screen went away" };
                    self.note(format!("{why}, recording stopped"));
                }
                // Freezing the clock lets the last tick bring every file to the same end time.
                self.clock.pause();
            }
            self.tick(stop)?;
            if stop {
                return Ok(());
            }
            thread::sleep(Duration::from_millis(1));
        }
    }

    fn tick(&mut self, flush: bool) -> Result<()> {
        if self.cam.as_ref().is_some_and(|c| c.is_lost()) {
            self.lose_camera();
        }
        let mut lost = Vec::new();
        for t in &mut self.tracks {
            if t.source.as_ref().is_some_and(|s| s.is_lost()) {
                t.source = None;
                lost.push((t.name, t.wav.take()));
            }
        }
        for (name, wav) in lost {
            self.note(format!("{name} lost at {:.1} s", self.seconds()));
            if let Some(mut wav) = wav {
                wav.finish()?;
            }
        }
        if self.written < self.clock.frames_due() {
            self.write_video()?;
        }
        let cam_due = self.clock.media_time().as_nanos() as i64 / 100 * i64::from(CAMERA_RATE) / TICKS_PER_SECOND;
        self.write_camera(cam_due)?;
        if !self.tracks.is_empty() {
            self.write_sound(flush)?;
        }
        Ok(())
    }

    /// Ends webcam.mp4 where the camera went away or changed size, and records on without the camera.
    fn lose_camera(&mut self) {
        self.note(format!("webcam lost at {:.1} s", self.seconds()));
        self.cam = None;
        self.last_cam = None;
        self.end_camera_file();
    }

    /// The camera file is optional and already has its note, so its finish error does not use up the disk note.
    fn end_camera_file(&mut self) {
        if let Some(mut w) = self.cam_w.take() {
            let _ = w.finish();
        }
    }

    /// Writes webcam.mp4 frames up to `due`. A failed write ends only the camera file, and final.mp4 keeps the camera.
    fn write_camera(&mut self, due: i64) -> Result<()> {
        let (Some(w), Some(frame)) = (self.cam_w.as_mut(), self.last_cam.as_ref()) else { return Ok(()) };
        if self.cam_written >= due {
            return Ok(());
        }
        let texture = self.compositor.draw_camera_file(frame, to_even(self.cam_frame))?;
        if (self.cam_written..due).try_for_each(|i| w.write_frame(texture, i)).is_err() {
            self.note(format!("webcam file stopped at {:.1} s", self.seconds()));
            self.end_camera_file();
            return Ok(());
        }
        self.cam_written = due;
        Ok(())
    }

    fn write_video(&mut self) -> Result<()> {
        self.wait_while_behind(false);
        self.wait_while_behind(true);
        // Read the clock again, so time spent waiting is caught up by repeating the newest frame.
        let due = self.clock.frames_due();
        let first = self.written;
        if !self.screen.is_minimized() {
            if let Some(frame) = self.screen.latest_frame() {
                self.last_screen = frame;
            }
        }
        if let Some(frame) = self.cam.as_mut().and_then(|c| c.latest_frame()) {
            if frame.size == self.cam_frame {
                self.last_cam = Some(frame);
            } else {
                self.lose_camera();
            }
        }
        let layout = self.shared.state().layout.clone();
        if layout.logo.path != self.logo {
            if let Some(error) = self.compositor.set_logo(layout.logo.path.as_deref().map(Path::new)) {
                self.note(error);
            }
            self.logo = layout.logo.path.clone();
        }

        if let Some(w) = self.screen_w.as_mut() {
            let texture = self.compositor.draw_screen(&self.last_screen)?;
            while self.written < due {
                w.write_frame(texture, self.written)?;
                self.written += 1;
            }
        }
        let Some(w) = self.final_w.as_mut() else { return Ok(()) };
        // A hardware encoder holds the shared device lock, so write_frame itself can be slow before frames_behind
        // grows. A late loop skips final.mp4 too, which keeps screen.mp4 on time.
        let late = due - first > 2;
        let mut skipped = due - first;
        if self.last_final.is_none() || (!late && w.frames_behind() <= FINAL_MAX_BEHIND) {
            // Skipped indexes get the previous final frame, at most 2 per tick, so final.mp4 keeps a constant frame rate.
            if let Some(last) = &self.last_final {
                for _ in 0..2 {
                    if self.final_next >= first {
                        break;
                    }
                    w.write_frame(last, self.final_next)?;
                    self.final_next += 1;
                }
            }
            if self.final_next == first {
                let texture = self.compositor.draw_final(&self.last_screen, self.last_cam.as_ref(), &layout)?;
                while self.final_next < due {
                    w.write_frame(texture, self.final_next)?;
                    self.final_next += 1;
                }
                self.last_final = Some(texture.clone());
                skipped = 0;
            }
        }
        if skipped > 0 {
            self.shared.state().file.dropped_frames += skipped;
        }
        Ok(())
    }

    fn wait_while_behind(&mut self, cam: bool) {
        let behind = |r: &Recorder| if cam { &r.cam_w } else { &r.screen_w }.as_ref().map_or(0, |w| w.frames_behind());
        if behind(self) <= MAX_BEHIND {
            return;
        }
        if !self.behind_noted {
            self.behind_noted = true;
            self.note("encoder fell behind".into());
        }
        while behind(self) >= MAX_BEHIND && !self.shared.stop.load(SeqCst) && !self.screen.is_closed() {
            thread::sleep(Duration::from_millis(5));
        }
    }

    fn write_sound(&mut self, flush: bool) -> Result<()> {
        let paused = self.clock.is_paused();
        let target = self.clock.samples_due(AUDIO_RATE) - if flush || paused { 0 } else { AUDIO_LAG };
        if target > self.samples {
            let n = (target - self.samples) as usize * 2;
            let (mut mix, mut buffer) = (vec![0.0f32; n], vec![0.0f32; n]);
            for t in &mut self.tracks {
                // Whatever the source has not delivered stays zero, which pads the file with silence.
                buffer.fill(0.0);
                if let Some(source) = t.source.as_mut() {
                    source.read(&mut buffer);
                }
                if let Some(wav) = t.wav.as_mut() {
                    wav.write(&buffer)?;
                }
                mix.iter_mut().zip(&buffer).for_each(|(m, b)| *m += b);
            }
            if self.final_w.is_some() {
                self.final_sound.extend(mix.iter().map(|&m| mixed(m)));
            }
            self.samples = target;
        }
        self.feed_final_sound(false)?;
        // Sound made while paused is thrown away, so it does not play after resume.
        if paused {
            self.drain_sound();
        }
        Ok(())
    }

    /// Throws away every sample the sound sources hold, and returns how many there were.
    fn drain_sound(&mut self) -> usize {
        let mut drain = [0.0f32; 4096];
        let mut thrown = 0;
        for source in self.tracks.iter_mut().filter_map(|t| t.source.as_mut()) {
            loop {
                let n = source.read(&mut drain);
                if n == 0 {
                    break;
                }
                thrown += n;
            }
        }
        thrown
    }

    /// Gives final.mp4 sound only up to its last frame handed over, or all of it when `all`.
    fn feed_final_sound(&mut self, all: bool) -> Result<()> {
        let Some(w) = self.final_w.as_mut() else { return Ok(()) };
        let end = if all { i64::MAX } else { self.final_next * i64::from(AUDIO_RATE) / i64::from(self.rate) };
        let frames = (end - self.final_sound_at).clamp(0, self.final_sound.len() as i64 / 2);
        if frames > 0 {
            let n = frames as usize * 2;
            w.write_audio(&self.final_sound[..n], self.final_sound_at)?;
            self.final_sound.drain(..n);
            self.final_sound_at += frames;
        }
        Ok(())
    }

    fn seconds(&self) -> f64 {
        self.clock.media_time().as_secs_f64()
    }

    fn note(&self, text: String) {
        self.shared.note(text, &self.clock);
    }

    /// Used while closing: goes on after a failure, and notes the first disk error.
    fn quietly(&self, r: Result<()>) {
        if let Err(Error::Io(e)) = r {
            let noted = std::mem::replace(&mut self.shared.state().disk_noted, true);
            if !noted {
                self.note(format!("disk error while saving: {e}"));
            }
        }
    }
}

// Runs on every exit of the recording thread except an abort, and release builds abort on a panic.
impl Drop for Recorder {
    fn drop(&mut self) {
        self.shared.state().ended = true;
        let deadline = Instant::now() + CATCH_UP_LIMIT;
        let mut r = Ok(());
        if let (Some(w), Some(last)) = (self.final_w.as_mut(), self.last_final.as_ref()) {
            // Waiting while the writer is behind keeps at most MAX_BEHIND repeats queued at once.
            while r.is_ok() && self.final_next < self.written {
                if w.frames_behind() < MAX_BEHIND {
                    r = w.write_frame(last, self.final_next);
                    self.final_next += 1;
                } else if Instant::now() < deadline {
                    thread::sleep(Duration::from_millis(5));
                } else {
                    break;
                }
            }
        }
        let short = self.written - self.final_next;
        if r.is_ok() && short > 0 && self.final_w.is_some() {
            self.note(format!("final.mp4 is {short} frames short"));
        }
        self.quietly(r);
        let r = self.feed_final_sound(short <= 0);
        self.quietly(r);
        for mut w in [self.screen_w.take(), self.final_w.take(), self.cam_w.take()].into_iter().flatten() {
            let r = w.finish();
            self.quietly(r);
        }
        for i in 0..self.tracks.len() {
            self.tracks[i].source = None;
            if let Some(mut wav) = self.tracks[i].wav.take() {
                let r = wav.finish();
                self.quietly(r);
            }
        }
        self.cam = None;
        let folder = &self.shared.folder;
        let s = self.shared.state();
        let files = s.file.sources.iter().map(|e| e.file.clone()).filter(|f| folder.join(f).exists()).collect();
        let result = recording_result(folder, &s.file, files, s.file.notes.clone());
        self.shared.save(s, &self.clock);
        *lock(&self.shared.result) = Some(result);
        self.shared.done.notify_all();
    }
}

fn check_frame_rate(rate: i32) -> Result<()> {
    if (1..=120).contains(&rate) {
        return Ok(());
    }
    Err(Error::Invalid(format!("The frame rate must be from 1 to 120 frames per second, not {rate}.")))
}

/// A NaN from a broken source would pass through `clamp`, so it becomes silence.
fn mixed(m: f32) -> f32 {
    if m.is_nan() {
        0.0
    } else {
        m.clamp(-1.0, 1.0)
    }
}

/// The full logo path holds the Windows user name, and the folder may be shared, so only the file name is written.
fn logged(l: &Layout) -> Layout {
    let mut l = l.clone();
    if let Some(name) = l.logo.path.as_deref().and_then(|p| Path::new(p).file_name()) {
        l.logo.path = Some(name.to_string_lossy().into_owned());
    }
    l
}

/// Two recordings started in the same second get "name (2)" and so on. Checking and creating is one step.
fn create_folder(root: &Path, name: &str) -> Result<PathBuf> {
    // Folders this call makes, deepest first, so a failure deeper down can remove them again.
    let made: Vec<&Path> = root.ancestors().take_while(|p| !p.exists()).collect();
    if let Err(e) = fs::create_dir_all(root) {
        made.iter().for_each(|p| _ = fs::remove_dir(p));
        return Err(Error::Io(e));
    }
    let mut n = 1;
    loop {
        let folder = root.join(if n == 1 { name.to_string() } else { format!("{name} ({n})") });
        match fs::create_dir(&folder) {
            Ok(()) => return Ok(folder),
            Err(e) if e.kind() == ErrorKind::AlreadyExists => n += 1,
            Err(e) => return Err(Error::Io(e)),
        }
    }
}

/// The local time as a folder name, and as .NET writes `DateTimeOffset.Now`, such as `2026-11-21T15:06:39.6718465+07:00`.
pub(crate) fn local_stamp(at: SystemTime) -> Result<(String, String)> {
    // FILETIME counts 100 ns ticks from 1601.
    let utc = 116_444_736_000_000_000 + (at.duration_since(UNIX_EPOCH).unwrap_or_default().as_nanos() / 100) as u64;
    let utc_time = FILETIME { dwLowDateTime: utc as u32, dwHighDateTime: (utc >> 32) as u32 };
    let (mut local_time, mut t) = (FILETIME::default(), SYSTEMTIME::default());
    let failed = |e| win(e, Error::Invalid, "The clock could not be read.");
    unsafe { FileTimeToLocalFileTime(&utc_time, &mut local_time) }.map_err(failed)?;
    unsafe { FileTimeToSystemTime(&local_time, &mut t) }.map_err(failed)?;
    let local = (u64::from(local_time.dwHighDateTime) << 32) | u64::from(local_time.dwLowDateTime);
    let minutes = (local as i64 - utc as i64) / 600_000_000;
    let sign = if minutes < 0 { '-' } else { '+' };
    let (hours, minutes) = (minutes.abs() / 60, minutes.abs() % 60);
    let date = format!("{:04}-{:02}-{:02}", t.wYear, t.wMonth, t.wDay);
    let (h, m, s) = (t.wHour, t.wMinute, t.wSecond);
    Ok((
        format!("{date} {h:02}-{m:02}-{s:02}"),
        format!("{date}T{h:02}:{m:02}:{s:02}.{:07}{sign}{hours:02}:{minutes:02}", local % 10_000_000),
    ))
}

/// Locking still works after a panic on another thread, because the data stays consistent.
fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stamp_matches_dotnet_format() {
        let (name, iso) = local_stamp(SystemTime::now()).unwrap();
        assert_eq!(name.len(), "2026-11-21 15-06-39".len(), "{name}");
        assert_eq!(iso.len(), "2026-11-21T15:06:39.6718465+07:00".len(), "{iso}");
        assert_eq!(&iso[..10], &name[..10]);
        assert_eq!(iso[11..19].replace(':', "-"), name[11..]);
    }

    #[test]
    fn taken_folder_names_get_a_number() {
        let root = std::env::temp_dir().join(format!("recorder-session-unit-{}", std::process::id()));
        let names: Vec<_> = (0..3).map(|_| create_folder(&root, "a").unwrap()).collect();
        fs::remove_dir_all(&root).unwrap();
        assert_eq!(names, [root.join("a"), root.join("a (2)"), root.join("a (3)")]);
    }

    /// A `Shared` with an empty session file, in a new folder named for the test.
    fn test_shared(name: &str) -> (Arc<Shared>, PathBuf) {
        let folder = std::env::temp_dir().join(format!("recorder-session-{name}-{}", std::process::id()));
        fs::create_dir_all(&folder).unwrap();
        let file = SessionFile {
            version: 1,
            started_at: String::new(),
            frame_size: FULL_HD,
            frame_rate: 30,
            target_kind: String::new(),
            sources: Vec::new(),
            layout_changes: Vec::new(),
            pauses: Vec::new(),
            dropped_frames: 0,
            notes: Vec::new(),
        };
        let shared = Arc::new(Shared {
            folder: folder.clone(),
            state: Mutex::new(State { file, layout: Layout::default(), ended: false, disk_noted: false, saves: 0 }),
            saved: Mutex::new(0),
            stop: AtomicBool::new(false),
            closed: AtomicBool::new(false),
            result: Mutex::new(None),
            done: Condvar::new(),
        });
        (shared, folder)
    }

    #[test]
    fn failed_save_does_not_block_an_older_text() {
        let (shared, folder) = test_shared("failed-save");
        let clock = RecordingClock::new(30, Box::new(|| Duration::ZERO));
        // A folder in the temp file's place makes the write fail.
        fs::create_dir(folder.join("session.json.tmp")).unwrap();
        shared.state().saves = 5;
        shared.note("newer".into(), &clock);
        fs::remove_dir(folder.join("session.json.tmp")).unwrap();
        shared.state().saves = 2;
        shared.note("older".into(), &clock);
        let text = fs::read_to_string(folder.join("session.json"));
        fs::remove_dir_all(&folder).unwrap();
        assert!(text.unwrap().contains("older"));
    }

    #[test]
    fn slow_session_json_write_leaves_the_state_lock_free() {
        let (shared, folder) = test_shared("save");
        // Holding the write lock stands in for a disk that takes long to write.
        let writing = lock(&shared.saved);
        let saver = {
            let shared = shared.clone();
            thread::spawn(move || shared.note("a note".into(), &RecordingClock::new(30, Box::new(|| Duration::ZERO))))
        };
        thread::sleep(Duration::from_millis(200));
        assert!(shared.state.try_lock().is_ok());
        drop(writing);
        saver.join().unwrap();
        let text = fs::read_to_string(folder.join("session.json")).unwrap();
        fs::remove_dir_all(&folder).unwrap();
        assert!(text.contains("a note"), "{text}");
    }

    #[test]
    fn mixed_sound_has_no_nan_and_stays_in_range() {
        assert_eq!(0.0, mixed(f32::NAN));
        assert_eq!((1.0, -1.0, 0.5), (mixed(2.0), mixed(-3.0), mixed(0.5)));
    }

    #[test]
    fn failed_folder_leaves_no_parent_folders() {
        let root = std::env::temp_dir().join(format!("recorder-session-parents-{}", std::process::id()));
        // `<` is not allowed in a Windows file name, so the deepest folder fails after its parents were made.
        assert!(create_folder(&root.join("sub").join("a<b"), "a").is_err());
        assert!(!root.exists());
    }

    #[test]
    fn only_the_logo_file_name_is_logged() {
        let mut l = Layout::default();
        l.logo.path = Some(r"C:\Users\someone\logo.png".into());
        assert_eq!(logged(&l).logo.path.as_deref(), Some("logo.png"));
    }
}
