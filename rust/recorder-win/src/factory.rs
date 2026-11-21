use std::path::Path;

use recorder_core::frame_math::PixelSize;

use crate::audio::{AudioIn, AudioSource};
use crate::error::Result;
use crate::gfx::Device;
use crate::screen::{Screen, ScreenSource};
use crate::targets::CaptureTarget;
use crate::video::{VideoOut, VideoWriter};
use crate::webcam::{Camera, WebcamSource};

/// Opens every source and writer of a recording, so tests can put fakes in their place.
pub trait SourceFactory: Send {
    fn open_screen(&self, target: &CaptureTarget, device: &Device) -> Result<Box<dyn Screen>>;
    fn open_webcam(&self, id: &str, device: &Device) -> Result<Box<dyn Camera>>;
    fn open_mic(&self, id: Option<&str>) -> Result<Box<dyn AudioIn>>;
    fn open_system_audio(&self) -> Result<Box<dyn AudioIn>>;
    fn open_writer(
        &self,
        path: &Path,
        size: PixelSize,
        frame_rate: i32,
        device: &Device,
        hardware: bool,
        with_audio: bool,
    ) -> Result<Box<dyn VideoOut>>;
}

/// The real screen, camera, sound and encoder.
pub struct RealFactory;

impl SourceFactory for RealFactory {
    fn open_screen(&self, target: &CaptureTarget, device: &Device) -> Result<Box<dyn Screen>> {
        Ok(Box::new(ScreenSource::create(target, device)?))
    }

    fn open_webcam(&self, id: &str, device: &Device) -> Result<Box<dyn Camera>> {
        Ok(Box::new(WebcamSource::open(id, device)?))
    }

    fn open_mic(&self, id: Option<&str>) -> Result<Box<dyn AudioIn>> {
        Ok(Box::new(AudioSource::open_mic(id)?))
    }

    fn open_system_audio(&self) -> Result<Box<dyn AudioIn>> {
        Ok(Box::new(AudioSource::open_loopback()?))
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
        Ok(Box::new(VideoWriter::create(path, size, frame_rate, device, hardware, with_audio)?))
    }
}
