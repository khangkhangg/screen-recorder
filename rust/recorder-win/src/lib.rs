mod audio;
mod compositor;
mod error;
mod factory;
#[cfg(any(test, feature = "fakes"))]
pub mod fakes;
mod gfx;
mod screen;
pub mod screenshot;
mod session;
mod targets;
mod video;
mod wav;
mod webcam;

pub use audio::{AudioIn, AudioSource};
pub use compositor::Compositor;
pub use error::{Error, Result};
pub use gfx::Device;
pub use screen::{looks_black, Frame, Screen, ScreenSource};
pub use factory::{RealFactory, SourceFactory};
pub use session::{PreflightResult, RecordingOptions, RecordingResult, RecordingSession};
pub use targets::{find_window, monitors, windows, CaptureTarget, MonitorInfo, WindowInfo};
pub use video::{check_encoders, max_encoder_size, EncoderSupport, VideoOut, VideoWriter};
pub use wav::WavWriter;
pub use webcam::{Camera, WebcamSource};

use ::windows::Win32::Media::MediaFoundation::{MFStartup, MFSTARTUP_FULL, MF_VERSION};
use ::windows::Win32::System::Com::{CoInitializeEx, COINIT_MULTITHREADED};
use ::windows::Win32::UI::HiDpi::{SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2};

/// Call first on the main thread: makes positions physical pixels, joins the COM
/// multithreaded apartment and starts Media Foundation. Safe to call more than once.
pub fn init_process() -> Result<()> {
    // This fails once awareness is set, by an earlier call or a manifest, which is fine.
    let _ = unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    // A repeat call returns S_FALSE, which counts as success.
    unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }
        .ok()
        .map_err(|e| error::win(e, Error::NotSupported, "COM could not start on this thread."))?;
    // Media Foundation counts its starts, so repeats are allowed.
    unsafe { MFStartup(MF_VERSION, MFSTARTUP_FULL) }
        .map_err(|e| error::win(e, Error::Encoder, "Media Foundation could not start."))
}
