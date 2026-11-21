#[path = "common/media_info.rs"]
mod media_info;

use std::fs;
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant, SystemTime};

use media_info::MediaInfo;
use recorder_core::frame_math::PixelSize;
use recorder_core::layout::Layout;
use recorder_core::session::{deserialize_session, SessionFile};
use recorder_win::fakes::{folder_name, FakeFactory, FakeMic, FakeQueue, FakeScreen, FakeWebcam, FakeWriter};
use recorder_win::{
    init_process, monitors, CaptureTarget, EncoderSupport, Error, PreflightResult, RecordingOptions, RecordingResult,
    RecordingSession, VideoOut,
};

/// The test's output folder. Dropping it deletes every recording in it.
struct Root(PathBuf);

impl Drop for Root {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn options() -> (RecordingOptions, Root) {
    static NEXT: AtomicU32 = AtomicU32::new(0);
    init_process().unwrap();
    let root = std::env::temp_dir().join(format!("rs-{}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst)));
    let primary = monitors().into_iter().find(|m| m.primary).unwrap();
    let o = RecordingOptions {
        target: CaptureTarget::Monitor(primary.handle),
        output_root: root.clone(),
        frame_rate: 60,
        webcam_id: None,
        mic: false,
        mic_id: None,
        system_audio: true,
        layout: Layout::default(),
        write_final: true,
    };
    (o, Root(root))
}

fn start(o: &RecordingOptions, f: FakeFactory) -> RecordingSession {
    let p = RecordingSession::preflight_with(o, &f).unwrap();
    RecordingSession::start_with(o.clone(), &p, Box::new(f)).unwrap()
}

/// Fails the test instead of hanging when stop does not return in time.
fn stop_within(s: RecordingSession, limit: Duration) -> RecordingResult {
    let (done, stopped) = mpsc::channel();
    thread::spawn(move || done.send(s.stop()));
    stopped.recv_timeout(limit).expect("stop did not return in time")
}

fn stop(s: RecordingSession) -> RecordingResult {
    stop_within(s, Duration::from_secs(30))
}

fn wait(ms: u64) {
    thread::sleep(Duration::from_millis(ms));
}

fn info(folder: &Path, name: &str) -> MediaInfo {
    media_info::read(&folder.join(name))
}

fn wav_seconds(path: &Path) -> f64 {
    (fs::metadata(path).unwrap().len() as f64 - 44.0) / (48_000.0 * 8.0)
}

fn session(folder: &Path) -> SessionFile {
    deserialize_session(&fs::read_to_string(folder.join("session.json")).unwrap()).unwrap()
}

fn fake_screen(width: i32, height: i32) -> FakeFactory {
    FakeFactory { screen: Some(FakeScreen::new(PixelSize { width, height })), ..FakeFactory::default() }
}

#[test]
#[ignore = "needs the desktop"]
fn two_second_recording_makes_aligned_files() {
    let (o, _root) = options();
    let s = start(&o, FakeFactory::default());
    wait(2000);
    let folder = stop(s).folder;
    assert!(folder.join("final.mp4").exists());
    assert!(folder.join("screen.mp4").exists());
    assert!(!folder.join("webcam.mp4").exists());
    let (a, b) = (info(&folder, "final.mp4").duration, info(&folder, "screen.mp4").duration);
    assert!((a - b).abs() <= 0.05, "final {a} s, screen {b} s");
    assert!(info(&folder, "screen.mp4").constant_frame_rate);
    let sound = info(&folder, "final.mp4").audio_duration;
    assert!((sound - a).abs() <= 0.1, "sound {sound} s, video {a} s");
}

#[test]
#[ignore = "needs the desktop"]
fn pause_is_cut_from_every_file() {
    let (o, _root) = options();
    let s = start(&o, FakeFactory::default());
    wait(1000);
    s.pause();
    wait(2000);
    s.resume();
    wait(1000);
    let folder = stop(s).folder;
    let video = info(&folder, "screen.mp4").duration;
    assert!((1.9..=2.2).contains(&video), "{video}");
    let sound = wav_seconds(&folder.join("system.wav"));
    assert!((1.9..=2.2).contains(&sound), "{sound}");
    assert_eq!(session(&folder).pauses.len(), 1);
}

#[test]
#[ignore = "needs the desktop"]
fn elapsed_stops_growing_while_paused() {
    let (o, _root) = options();
    let s = start(&o, FakeFactory::default());
    wait(500);
    s.pause();
    let paused = s.elapsed();
    wait(500);
    assert_eq!(paused, s.elapsed());
    assert!((0.3..=1.5).contains(&paused.as_secs_f64()), "{paused:?}");
    s.resume();
    wait(300);
    assert!(s.elapsed() > paused);
    stop(s);
}

#[test]
#[ignore = "needs the desktop"]
fn layout_changes_are_logged() {
    let (o, _root) = options();
    let s = start(&o, FakeFactory::default());
    wait(500);
    let mut l = Layout::default();
    l.camera.x = 0.5;
    s.set_layout(l);
    wait(500);
    let folder = stop(s).folder;
    assert_eq!(session(&folder).layout_changes.len(), 2);
}

#[test]
#[ignore = "needs the desktop"]
fn sources_that_are_off_write_no_file() {
    let (o, _root) = options();
    let o = RecordingOptions { mic: false, system_audio: false, ..o };
    let s = start(&o, FakeFactory::default());
    wait(1000);
    let folder = stop(s).folder;
    let wavs = fs::read_dir(&folder).unwrap().filter(|e| e.as_ref().unwrap().path().extension().is_some_and(|x| x == "wav"));
    assert_eq!(wavs.count(), 0);
    assert!(folder.join("screen.mp4").exists());
}

#[test]
#[ignore = "needs the desktop"]
fn write_final_false_skips_final() {
    let (o, _root) = options();
    let s = start(&RecordingOptions { write_final: false, ..o }, FakeFactory::default());
    wait(1000);
    let folder = stop(s).folder;
    assert!(!folder.join("final.mp4").exists());
    assert!(folder.join("screen.mp4").exists());
}

#[test]
#[ignore = "needs the desktop"]
fn lost_webcam_keeps_recording() {
    let (o, _root) = options();
    let cam = FakeWebcam::default();
    let f = FakeFactory { webcam: Some(cam.clone()), ..FakeFactory::default() };
    let s = start(&RecordingOptions { webcam_id: Some("fake".into()), ..o }, f);
    wait(1000);
    cam.lose();
    wait(1000);
    let result = stop(s);
    let video = info(&result.folder, "screen.mp4").duration;
    assert!((1.9..=2.2).contains(&video), "{video}");
    assert!(result.notes.iter().any(|n| n.starts_with("webcam lost")), "{:?}", result.notes);
    assert!(info(&result.folder, "webcam.mp4").frame_count > 0);
}

#[test]
#[ignore = "needs the desktop"]
fn lost_mic_keeps_recording() {
    let (o, _root) = options();
    let mic = FakeMic::default();
    let f = FakeFactory { mic: Some(mic.clone()), ..FakeFactory::default() };
    let s = start(&RecordingOptions { mic: true, ..o }, f);
    wait(1000);
    mic.lose();
    wait(1000);
    let result = stop(s);
    let video = info(&result.folder, "screen.mp4").duration;
    assert!((1.9..=2.2).contains(&video), "{video}");
    assert!(result.notes.iter().any(|n| n.starts_with("mic lost")), "{:?}", result.notes);
    assert!(wav_seconds(&result.folder.join("mic.wav")) > 0.0);
}

#[test]
#[ignore = "needs the desktop"]
fn disk_full_stops_and_keeps_files_playable() {
    let (o, _root) = options();
    let f = FakeFactory {
        wrap: Some(Box::new(|name, w| {
            Ok(if name == "screen.mp4" { Box::new(FakeWriter { inner: w, queue: None, fail_at: Some(60), stall: None }) } else { w })
        })),
        ..FakeFactory::default()
    };
    let s = start(&o, f);
    assert!(s.wait_stopped(Duration::from_secs(10)));
    let result = stop(s);
    assert!(result.notes.iter().any(|n| n.contains("disk")), "{:?}", result.notes);
    assert!(info(&result.folder, "screen.mp4").frame_count > 0);
    assert!(info(&result.folder, "final.mp4").frame_count > 0);
}

#[test]
#[ignore = "needs the desktop"]
fn source_resize_keeps_frame_size() {
    let (o, _root) = options();
    let screen = FakeScreen::default();
    let s = start(&o, FakeFactory { screen: Some(screen.clone()), ..FakeFactory::default() });
    wait(1000);
    screen.set_size(PixelSize { width: 1280, height: 1024 });
    wait(1000);
    let folder = stop(s).folder;
    assert_eq!(1920, info(&folder, "screen.mp4").width);
}

#[test]
#[ignore = "needs the desktop"]
fn webcam_that_fails_to_open_is_skipped() {
    let (o, _root) = options();
    let s = start(&RecordingOptions { webcam_id: Some("busy".into()), ..o }, FakeFactory::default());
    wait(1000);
    let result = stop(s);
    assert!(info(&result.folder, "final.mp4").frame_count > 0);
    assert!(!result.folder.join("webcam.mp4").exists());
    assert!(result.notes.contains(&"webcam lost at 0.0 s".to_string()), "{:?}", result.notes);
}

#[test]
#[ignore = "needs the desktop"]
fn session_json_keeps_only_the_logo_file_name() {
    let (o, _root) = options();
    let logo = std::env::temp_dir().join("logo.png");
    let mut layout = Layout::default();
    layout.logo.visible = true;
    layout.logo.path = Some(logo.to_string_lossy().into_owned());
    let s = start(&RecordingOptions { layout, ..o }, FakeFactory::default());
    wait(500);
    let folder = stop(s).folder;
    let text = fs::read_to_string(folder.join("session.json")).unwrap();
    // The folder as it would appear inside a JSON string, backslashes escaped.
    let dir = logo.parent().unwrap().to_string_lossy().replace('\\', "\\\\");
    assert!(!text.contains(&dir), "{text}");
    let changes = session(&folder).layout_changes;
    assert!(changes.iter().all(|c| c.layout.logo.path.as_deref() == Some("logo.png")), "{changes:?}");
}

#[test]
#[ignore = "needs the desktop"]
fn session_json_records_video_sizes() {
    let (o, _root) = options();
    let s = start(&o, fake_screen(3840, 2160));
    wait(500);
    let folder = stop(s).folder;
    let sources = session(&folder).sources;
    let size = |kind: &str| {
        let e = sources.iter().find(|e| e.kind == kind).unwrap();
        (e.width, e.height)
    };
    assert_eq!((Some(3840), Some(2160)), size("final"));
    assert_eq!((Some(1920), Some(1080)), size("screen"));
    assert_eq!((None, None), size("system"));
}

#[test]
#[ignore = "needs the desktop"]
fn source_closed_stops_and_saves() {
    let (o, _root) = options();
    let screen = FakeScreen::default();
    let s = start(&o, FakeFactory { screen: Some(screen.clone()), ..FakeFactory::default() });
    wait(1000);
    assert!(!s.has_stopped());
    screen.close();
    assert!(s.wait_stopped(Duration::from_secs(10)));
    assert!(s.has_stopped());
    let result = stop(s);
    assert!(info(&result.folder, "screen.mp4").frame_count > 0);
    assert!(info(&result.folder, "final.mp4").frame_count > 0);
    assert!(result.folder.join("session.json").exists());
}

#[test]
#[ignore = "needs the desktop"]
fn slow_final_encoder_drops_final_frames_only() {
    let (o, _root) = options();
    let f = FakeFactory {
        wrap: Some(Box::new(|name, w| {
            // An encoder at half the frame rate falls behind while recording and catches up at stop.
            Ok(if name == "final.mp4" {
                Box::new(FakeWriter { inner: w, queue: Some(Arc::new(FakeQueue::new(30))), fail_at: None, stall: None })
            } else {
                w
            })
        })),
        ..FakeFactory::default()
    };
    // A 1920x1080 region keeps 60 frames per second, which the frame count below assumes.
    let CaptureTarget::Monitor(monitor) = o.target else { unreachable!() };
    let region = CaptureTarget::Region { monitor, x: 0, y: 0, width: 1920, height: 1080 };
    let s = start(&RecordingOptions { target: region, ..o }, f);
    wait(2000);
    let result = stop(s);
    assert!(result.dropped_frames > 0);
    let screen = info(&result.folder, "screen.mp4").frame_count;
    assert!((110..=125).contains(&screen), "{screen}");
    let last = info(&result.folder, "final.mp4");
    assert_eq!(screen, last.frame_count);
    assert!(last.constant_frame_rate);
}

#[test]
#[ignore = "needs the desktop"]
fn large_frame_is_capped_at_30() {
    let (o, _root) = options();
    assert_eq!(30, RecordingSession::preflight_with(&o, &fake_screen(3840, 2160)).unwrap().frame_rate);
}

#[test]
#[ignore = "needs the desktop"]
fn normal_frame_keeps_60() {
    let (o, _root) = options();
    assert_eq!(60, RecordingSession::preflight_with(&o, &fake_screen(1920, 1080)).unwrap().frame_rate);
}

#[test]
#[ignore = "needs the desktop"]
fn session_json_held_open_breaks_nothing() {
    let (o, _root) = options();
    let s = start(&o, fake_screen(1920, 1080));
    let folders: Vec<_> = fs::read_dir(&o.output_root).unwrap().map(|e| e.unwrap().path()).collect();
    assert_eq!(folders.len(), 1, "{folders:?}");
    let folder = &folders[0];
    wait(500);
    {
        let _held = fs::OpenOptions::new().read(true).share_mode(0).open(folder.join("session.json")).unwrap();
        s.pause();
        s.resume();
        wait(500);
    }
    stop_within(s, Duration::from_secs(5));
    assert!(info(folder, "screen.mp4").frame_count > 0);
}

#[test]
#[ignore = "needs the desktop"]
fn failed_start_removes_its_folder() {
    let (o, _root) = options();
    // Records the recording folder when the first writer opens, then fails.
    let seen = Arc::new(Mutex::new(None));
    let (root, seen_in) = (o.output_root.clone(), seen.clone());
    let f = FakeFactory {
        wrap: Some(Box::new(move |name, w| {
            if name != "screen.mp4" {
                return Ok(w);
            }
            let folders: Vec<_> = fs::read_dir(&root).unwrap().map(|e| e.unwrap().path()).collect();
            *seen_in.lock().unwrap() = Some(folders);
            fail(w)
        })),
        ..fake_screen(1920, 1080)
    };
    let p = RecordingSession::preflight_with(&o, &f).unwrap();
    assert!(matches!(RecordingSession::start_with(o.clone(), &p, Box::new(f)), Err(Error::Invalid(_))));
    let seen = seen.lock().unwrap().take().expect("the first writer was never opened");
    assert_eq!(seen.len(), 1, "{seen:?}");
    assert!(!seen[0].exists());
    assert_eq!(fs::read_dir(&o.output_root).unwrap().count(), 0);
}

fn fail(w: Box<dyn VideoOut>) -> recorder_win::Result<Box<dyn VideoOut>> {
    drop(w);
    Err(Error::Invalid("The writer could not open.".into()))
}

#[test]
#[ignore = "needs the desktop"]
fn same_second_starts_get_separate_folders() {
    let (o, _root) = options();
    let f = fake_screen(1920, 1080);
    let p = RecordingSession::preflight_with(&o, &f).unwrap();
    // Folders for the next 3 seconds already exist, so the session has to add " (2)".
    for i in 0..3 {
        fs::create_dir_all(o.output_root.join(folder_name(SystemTime::now() + Duration::from_secs(i)))).unwrap();
    }
    let s = RecordingSession::start_with(o, &p, Box::new(f)).unwrap();
    let folder = stop(s).folder;
    assert!(folder.to_string_lossy().ends_with(" (2)"), "{folder:?}");
    assert!(folder.join("session.json").exists());
}

#[test]
#[ignore = "needs the desktop"]
fn big_frame_scales_the_raw_screen_file() {
    let (o, _root) = options();
    let p = RecordingSession::preflight_with(&o, &fake_screen(3840, 2160)).unwrap();
    assert_eq!(PixelSize { width: 3840, height: 2160 }, p.frame_size);
    assert_eq!(PixelSize { width: 1920, height: 1080 }, p.screen_file_size);
}

#[test]
#[ignore = "needs the desktop"]
fn normal_frame_keeps_the_raw_screen_size() {
    let (o, _root) = options();
    let p = RecordingSession::preflight_with(&o, &fake_screen(1920, 1080)).unwrap();
    assert_eq!(PixelSize { width: 1920, height: 1080 }, p.frame_size);
    assert_eq!(PixelSize { width: 1920, height: 1080 }, p.screen_file_size);
}

#[test]
#[ignore = "needs the desktop"]
fn desktop_4k_keeps_its_frames() {
    let (o, _root) = options();
    let primary = monitors().into_iter().find(|m| m.primary).unwrap();
    if i64::from(primary.width) * i64::from(primary.height) <= 2560 * 1440 {
        println!("skipped: the primary monitor is {}x{}, not larger than 2560x1440", primary.width, primary.height);
        return;
    }
    let o = RecordingOptions { mic: true, ..o };
    let p = RecordingSession::preflight(&o).unwrap();
    let s = RecordingSession::start(o, &p).unwrap();
    wait(10_000);
    let watch = Instant::now();
    let result = stop(s);
    let stop_time = watch.elapsed();
    let last = info(&result.folder, "final.mp4");
    let screen = info(&result.folder, "screen.mp4");
    assert!(result.dropped_frames * 100 < last.frame_count as i64, "{} of {} frames dropped", result.dropped_frames, last.frame_count);
    let size = |s: PixelSize| (s.width as u32, s.height as u32);
    assert_eq!(size(p.frame_size), (last.width, last.height));
    assert_eq!(size(p.screen_file_size), (screen.width, screen.height));
    assert_eq!(screen.frame_count, last.frame_count);
    assert!(stop_time < Duration::from_secs(3), "stop took {stop_time:?}");
}

#[test]
fn output_root_on_a_missing_drive_is_a_disk_error() {
    let Some(drive) = ('D'..='Z').rev().map(|d| format!("{d}:\\")).find(|d| !Path::new(d).exists()) else {
        println!("skipped: every drive letter is in use");
        return;
    };
    let root = Path::new(&drive).join("recordings");
    let o = RecordingOptions {
        target: CaptureTarget::Monitor(0),
        output_root: root.clone(),
        frame_rate: 60,
        webcam_id: None,
        mic: false,
        mic_id: None,
        system_audio: false,
        layout: Layout::default(),
        write_final: true,
    };
    let size = PixelSize { width: 1920, height: 1080 };
    let p = PreflightResult {
        encoders: EncoderSupport::Hardware,
        frame_size: size,
        scaled_down: false,
        looks_black: false,
        frame_rate: 60,
        screen_file_size: size,
    };
    let started = RecordingSession::start_with(o, &p, Box::new(FakeFactory::default()));
    let Err(Error::Io(e)) = started else { panic!("expected a disk error, got {:?}", started.err()) };
    let message = e.to_string();
    assert!(message.starts_with(char::is_uppercase) && message.contains(". "), "{message}");
}

#[test]
#[ignore = "needs the desktop"]
fn frame_rate_outside_1_to_120_is_refused() {
    let (o, _root) = options();
    for rate in [0, 121] {
        let o = RecordingOptions { frame_rate: rate, ..o.clone() };
        let p = RecordingSession::preflight_with(&o, &fake_screen(1920, 1080));
        assert!(matches!(p, Err(Error::Invalid(_))), "{rate}");
    }
    for rate in [1, 120] {
        let o = RecordingOptions { frame_rate: rate, ..o.clone() };
        assert_eq!(rate, RecordingSession::preflight_with(&o, &fake_screen(1920, 1080)).unwrap().frame_rate);
    }
}

#[test]
#[ignore = "needs the desktop"]
fn odd_camera_size_gives_an_even_webcam_file() {
    let (o, _root) = options();
    let f = FakeFactory {
        webcam: Some(FakeWebcam::default()),
        webcam_size: Some(PixelSize { width: 641, height: 481 }),
        ..FakeFactory::default()
    };
    let s = start(&RecordingOptions { webcam_id: Some("fake".into()), ..o }, f);
    wait(1000);
    let result = stop(s);
    let webcam = info(&result.folder, "webcam.mp4");
    assert_eq!((640, 480), (webcam.width, webcam.height));
    assert!(webcam.frame_count > 0);
    assert!(!result.notes.iter().any(|n| n.starts_with("webcam lost")), "{:?}", result.notes);
}

#[test]
#[ignore = "needs the desktop"]
fn failing_webcam_writer_keeps_recording() {
    let (o, _root) = options();
    let f = FakeFactory {
        webcam: Some(FakeWebcam::default()),
        wrap: Some(Box::new(|name, w| {
            Ok(if name == "webcam.mp4" { Box::new(FakeWriter { inner: w, queue: None, fail_at: Some(9), stall: None }) } else { w })
        })),
        ..FakeFactory::default()
    };
    let s = start(&RecordingOptions { webcam_id: Some("fake".into()), ..o }, f);
    wait(2000);
    let result = stop(s);
    assert!(result.notes.iter().any(|n| n.starts_with("webcam lost at")), "{:?}", result.notes);
    let screen = info(&result.folder, "screen.mp4").duration;
    assert!((1.9..=2.2).contains(&screen), "{screen}");
    let last = info(&result.folder, "final.mp4").duration;
    assert!((1.9..=2.2).contains(&last), "{last}");
}

#[test]
#[ignore = "needs the desktop"]
fn stuck_final_encoder_bounds_the_stop() {
    let (o, _root) = options();
    let queue = Arc::new(FakeQueue::new(1));
    let queue_in = queue.clone();
    let f = FakeFactory {
        wrap: Some(Box::new(move |name, w| {
            Ok(if name == "final.mp4" {
                Box::new(FakeWriter { inner: w, queue: Some(queue_in.clone()), fail_at: None, stall: None })
            } else {
                w
            })
        })),
        // A fake screen keeps 60 frames per second, which the queue limit below assumes.
        ..fake_screen(1920, 1080)
    };
    let s = start(&o, f);
    wait(2000);
    let result = stop_within(s, Duration::from_secs(35));
    assert!(queue.most_queued() <= 60, "{} frames queued at once", queue.most_queued());
    assert!(result.notes.iter().any(|n| n.starts_with("final.mp4 is ") && n.ends_with(" frames short")), "{:?}", result.notes);
    assert!(info(&result.folder, "final.mp4").frame_count > 0);
}

#[test]
#[ignore = "needs the desktop"]
fn slow_encoder_start_drops_no_frames() {
    let (o, _root) = options();
    let f = FakeFactory {
        wrap: Some(Box::new(|name, w| {
            let stall = Some(Duration::from_millis(400));
            Ok(if name == "screen.mp4" { Box::new(FakeWriter { inner: w, queue: None, fail_at: None, stall }) } else { w })
        })),
        ..fake_screen(1920, 1080)
    };
    let s = start(&o, f);
    wait(1000);
    let result = stop(s);
    assert_eq!(0, result.dropped_frames);
    let screen = info(&result.folder, "screen.mp4").frame_count;
    assert_eq!(screen, info(&result.folder, "final.mp4").frame_count);
}
