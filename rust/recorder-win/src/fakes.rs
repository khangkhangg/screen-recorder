//! Fake sources and writers that the session tests control. Video writers wrap a real one, so files can be read back.
use std::io;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime};

use recorder_core::frame_math::PixelSize;
use windows::Win32::Graphics::Direct3D11::ID3D11Texture2D;

use crate::error::win;
use crate::session::local_stamp;
use crate::webcam::upload;
use crate::{AudioIn, Camera, CaptureTarget, Device, Error, Frame, RealFactory, Result, Screen, SourceFactory, VideoOut};

/// A frame of one BGRA colour on `device`.
fn solid(device: &Device, size: PixelSize, bgra: [u8; 4]) -> Result<Frame> {
    let pixels = bgra.repeat(size.width.max(0) as usize * size.height.max(0) as usize);
    upload(&pixels, size, device).map_err(|e| win(e, Error::Device, "The fake frame could not be made."))
}

/// A red screen. Clones share the size and the closed flag, so a test can change them while a session records.
#[derive(Clone)]
pub struct FakeScreen(Arc<(Mutex<PixelSize>, AtomicBool)>);

impl Default for FakeScreen {
    fn default() -> Self {
        FakeScreen::new(PixelSize { width: 1920, height: 1080 })
    }
}

impl FakeScreen {
    pub fn new(size: PixelSize) -> FakeScreen {
        FakeScreen(Arc::new((Mutex::new(size), AtomicBool::new(false))))
    }

    pub fn set_size(&self, size: PixelSize) {
        *self.0 .0.lock().unwrap_or_else(|e| e.into_inner()) = size;
    }

    pub fn close(&self) {
        self.0 .1.store(true, Ordering::SeqCst);
    }

    fn size(&self) -> PixelSize {
        *self.0 .0.lock().unwrap_or_else(|e| e.into_inner())
    }
}

struct FakeScreenSource {
    fake: FakeScreen,
    device: Device,
    frame: Option<Frame>,
}

impl Screen for FakeScreenSource {
    fn source_size(&self) -> PixelSize {
        self.fake.size()
    }

    fn latest_frame(&mut self) -> Option<Frame> {
        let size = self.fake.size();
        if !matches!(&self.frame, Some(f) if f.size == size) {
            self.frame = solid(&self.device, size, [0, 0, 200, 255]).ok();
        }
        self.frame.clone()
    }

    fn is_minimized(&self) -> bool {
        false
    }

    fn is_closed(&self) -> bool {
        self.fake.0 .1.load(Ordering::SeqCst)
    }
}

/// A 640 by 480 camera. Clones share the lost flag.
#[derive(Clone, Default)]
pub struct FakeWebcam(Arc<AtomicBool>);

impl FakeWebcam {
    pub fn lose(&self) {
        self.0.store(true, Ordering::SeqCst);
    }
}

struct FakeCamera {
    frame: Frame,
    lost: Arc<AtomicBool>,
}

impl Camera for FakeCamera {
    fn size(&self) -> PixelSize {
        self.frame.size
    }

    fn latest_frame(&mut self) -> Option<Frame> {
        Some(self.frame.clone())
    }

    fn is_lost(&self) -> bool {
        self.lost.load(Ordering::SeqCst)
    }
}

/// A microphone that delivers nothing. Clones share the lost flag.
#[derive(Clone, Default)]
pub struct FakeMic(Arc<AtomicBool>);

impl FakeMic {
    pub fn lose(&self) {
        self.0.store(true, Ordering::SeqCst);
    }
}

impl AudioIn for FakeMic {
    fn read(&mut self, _out: &mut [f32]) -> usize {
        0
    }

    fn is_lost(&self) -> bool {
        self.0.load(Ordering::SeqCst)
    }
}

/// One second of 48 kHz stereo floats.
const SOUND_SECOND: usize = 96_000;

/// A microphone that already holds 1 second of samples of 0.5 when it opens, then delivers -0.5 at the real rate.
pub struct BacklogMic {
    opened: Instant,
    given: usize,
}

impl AudioIn for BacklogMic {
    fn read(&mut self, out: &mut [f32]) -> usize {
        let ready = (SOUND_SECOND + (self.opened.elapsed().as_secs_f64() * SOUND_SECOND as f64) as usize) & !1;
        let n = out.len().min(ready.saturating_sub(self.given)) & !1;
        for (i, o) in out[..n].iter_mut().enumerate() {
            *o = if self.given + i < SOUND_SECOND { 0.5 } else { -0.5 };
        }
        self.given += n;
        n
    }

    fn is_lost(&self) -> bool {
        false
    }
}

/// Wraps a real writer. `queue` replaces its frames_behind, from frame `fail_at` on every write fails like a full disk,
/// and writing frame 0 first sleeps for `stall`, like an encoder that starts up slowly.
/// `failed` is set by a failed write, and then `finish` fails too, as the real writer does.
pub struct FakeWriter {
    pub inner: Box<dyn VideoOut>,
    pub queue: Option<Arc<FakeQueue>>,
    pub fail_at: Option<i64>,
    pub stall: Option<Duration>,
    pub failed: bool,
}

fn disk_full() -> Error {
    Error::Io(io::Error::other("There is not enough space on the disk."))
}

/// An encoder that finishes `rate` frames per second, so frames sent faster wait in its queue.
pub struct FakeQueue {
    rate: f64,
    /// When the queue will be empty, and the most frames ever queued at once.
    state: Mutex<(Instant, i64)>,
}

impl FakeQueue {
    pub fn new(rate: u32) -> Self {
        FakeQueue { rate: f64::from(rate), state: Mutex::new((Instant::now(), 0)) }
    }

    pub fn most_queued(&self) -> i64 {
        self.state.lock().unwrap_or_else(|e| e.into_inner()).1
    }

    fn queued(&self, empty_at: Instant) -> i64 {
        (empty_at.saturating_duration_since(Instant::now()).as_secs_f64() * self.rate).ceil() as i64
    }

    fn push(&self) {
        let mut s = self.state.lock().unwrap_or_else(|e| e.into_inner());
        s.0 = s.0.max(Instant::now()) + Duration::from_secs_f64(1.0 / self.rate);
        s.1 = s.1.max(self.queued(s.0));
    }
}

impl VideoOut for FakeWriter {
    fn write_frame(&mut self, texture: &ID3D11Texture2D, index: i64) -> Result<()> {
        if self.fail_at.is_some_and(|at| index >= at) {
            self.failed = true;
            return Err(disk_full());
        }
        if let (0, Some(stall)) = (index, self.stall) {
            std::thread::sleep(stall);
        }
        if let Some(queue) = &self.queue {
            queue.push();
        }
        self.inner.write_frame(texture, index)
    }

    fn write_audio(&mut self, samples: &[f32], first_sample: i64) -> Result<()> {
        self.inner.write_audio(samples, first_sample)
    }

    fn frames_behind(&self) -> i64 {
        match &self.queue {
            Some(queue) => queue.queued(queue.state.lock().unwrap_or_else(|e| e.into_inner()).0),
            None => self.inner.frames_behind(),
        }
    }

    fn finish(&mut self) -> Result<()> {
        let r = self.inner.finish();
        if self.failed {
            return Err(disk_full());
        }
        r
    }
}

/// Gets the file name and the real writer, and returns the writer the session uses.
pub type Wrap = Box<dyn Fn(&str, Box<dyn VideoOut>) -> Result<Box<dyn VideoOut>> + Send>;

/// Real sources and writers except where a fake is set. Without a fake webcam, opening one fails like a busy camera.
#[derive(Default)]
pub struct FakeFactory {
    pub screen: Option<FakeScreen>,
    pub webcam: Option<FakeWebcam>,
    pub mic: Option<FakeMic>,
    pub wrap: Option<Wrap>,
    /// The fake webcam's frame size, 640x480 when not set.
    pub webcam_size: Option<PixelSize>,
    /// Opens a `BacklogMic` as the microphone.
    pub backlog_mic: bool,
}

impl SourceFactory for FakeFactory {
    fn open_screen(&self, target: &CaptureTarget, device: &Device) -> Result<Box<dyn Screen>> {
        match &self.screen {
            Some(fake) => Ok(Box::new(FakeScreenSource { fake: fake.clone(), device: device.clone(), frame: None })),
            None => RealFactory.open_screen(target, device),
        }
    }

    fn open_webcam(&self, _id: &str, device: &Device) -> Result<Box<dyn Camera>> {
        let Some(webcam) = &self.webcam else { return Err(Error::Device("Camera in use.".into())) };
        let size = self.webcam_size.unwrap_or(PixelSize { width: 640, height: 480 });
        let frame = solid(device, size, [0, 200, 0, 255])?;
        Ok(Box::new(FakeCamera { frame, lost: webcam.0.clone() }))
    }

    fn open_mic(&self, id: Option<&str>) -> Result<Box<dyn AudioIn>> {
        if self.backlog_mic {
            return Ok(Box::new(BacklogMic { opened: Instant::now(), given: 0 }));
        }
        match &self.mic {
            Some(mic) => Ok(Box::new(mic.clone())),
            None => RealFactory.open_mic(id),
        }
    }

    fn open_system_audio(&self) -> Result<Box<dyn AudioIn>> {
        RealFactory.open_system_audio()
    }

    fn open_writer(
        &self,
        path: &Path,
        size: PixelSize,
        frame_rate: i32,
        device: &Device,
        hardware: bool,
        with_audio: bool,
    ) -> Result<Box<dyn VideoOut>> {
        let writer = RealFactory.open_writer(path, size, frame_rate, device, hardware, with_audio)?;
        let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        match &self.wrap {
            Some(wrap) => wrap(&name, writer),
            None => Ok(writer),
        }
    }
}

/// The folder name a session started at `at` would get, before any " (2)".
pub fn folder_name(at: SystemTime) -> String {
    local_stamp(at).map(|(name, _)| name).unwrap_or_default()
}
