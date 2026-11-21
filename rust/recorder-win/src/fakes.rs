//! Fake sources and writers that the session tests control. Video writers wrap a real one, so files can be read back.
use std::io;
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::SystemTime;

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

/// Wraps a real writer. `behind` replaces its frames_behind, and from frame `fail_at` on every write fails like a full disk.
pub struct FakeWriter {
    pub inner: Box<dyn VideoOut>,
    pub behind: Option<i64>,
    pub fail_at: Option<i64>,
}

impl VideoOut for FakeWriter {
    fn write_frame(&mut self, texture: &ID3D11Texture2D, index: i64) -> Result<()> {
        if self.fail_at.is_some_and(|at| index >= at) {
            return Err(Error::Io(io::Error::other("There is not enough space on the disk.")));
        }
        self.inner.write_frame(texture, index)
    }

    fn write_audio(&mut self, samples: &[f32], first_sample: i64) -> Result<()> {
        self.inner.write_audio(samples, first_sample)
    }

    fn frames_behind(&self) -> i64 {
        self.behind.unwrap_or_else(|| self.inner.frames_behind())
    }

    fn finish(&mut self) -> Result<()> {
        self.inner.finish()
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
        let frame = solid(device, PixelSize { width: 640, height: 480 }, [0, 200, 0, 255])?;
        Ok(Box::new(FakeCamera { frame, lost: webcam.0.clone() }))
    }

    fn open_mic(&self, id: Option<&str>) -> Result<Box<dyn AudioIn>> {
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
