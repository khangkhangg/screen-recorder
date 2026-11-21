use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use windows::core::HSTRING;
use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
use windows::Win32::Foundation::E_ACCESSDENIED;
use windows::Win32::Media::Audio::{
    eCapture, eConsole, eRender, IAudioCaptureClient, IAudioClient, IMMDevice, IMMDeviceEnumerator,
    MMDeviceEnumerator, AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
    AUDCLNT_STREAMFLAGS_LOOPBACK, AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, DEVICE_STATE_ACTIVE, WAVEFORMATEX,
};
use windows::Win32::System::Com::StructuredStorage::{PropVariantClear, PropVariantToBSTR};
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL, COINIT_MULTITHREADED, STGM_READ,
};

use crate::error::{win, Error, Result};

const RATE: u32 = 48_000;
/// 2 seconds of stereo floats.
const CAPACITY: usize = 2 * RATE as usize * 2;
const NO_MIC: &str = "No microphone found on this PC.";
const MIC_BLOCKED: &str = "Microphone access is turned off in Windows privacy settings.";

/// A sound input that the recording session pulls from on its own clock.
pub trait AudioIn: Send {
    /// Copies the oldest buffered 48 kHz stereo floats into `out` and returns how many.
    /// The count is always even, so left and right stay paired.
    fn read(&mut self, out: &mut [f32]) -> usize;
    /// True once the device is gone or capture failed. It stays true.
    fn is_lost(&self) -> bool;
}

struct Shared {
    ring: Mutex<VecDeque<f32>>,
    lost: AtomicBool,
    stop: AtomicBool,
}

/// Captures a microphone or the computer's own sound through WASAPI.
pub struct AudioSource {
    shared: Arc<Shared>,
    thread: Option<JoinHandle<()>>,
}

impl AudioSource {
    /// Opens the microphone with this id, or the default one for `None`.
    pub fn open_mic(id: Option<&str>) -> Result<AudioSource> {
        let id = id.map(HSTRING::from);
        AudioSource::open("microphone capture", 0, move |devices| {
            let device = match &id {
                Some(id) => unsafe { devices.GetDevice(id) },
                None => unsafe { devices.GetDefaultAudioEndpoint(eCapture, eConsole) },
            };
            let device = device.map_err(|_| Error::Device(NO_MIC.into()))?;
            unsafe { device.Activate::<IAudioClient>(CLSCTX_ALL, None) }.map_err(|e| match e.code() {
                E_ACCESSDENIED => Error::Device(MIC_BLOCKED.into()),
                _ => win(e, Error::Device, "Windows could not open the microphone."),
            })
        })
    }

    /// Opens the default output device's sound. It delivers nothing while the computer is silent.
    pub fn open_loopback() -> Result<AudioSource> {
        AudioSource::open("loopback capture", AUDCLNT_STREAMFLAGS_LOOPBACK, |devices| {
            unsafe { devices.GetDefaultAudioEndpoint(eRender, eConsole) }
                .and_then(|device| unsafe { device.Activate::<IAudioClient>(CLSCTX_ALL, None) })
                .map_err(|e| win(e, Error::Device, "Windows could not capture the computer's sound."))
        })
    }

    /// The active microphones as (id, name). Empty when there are none or Windows cannot list them.
    pub fn list_mics() -> Vec<(String, String)> {
        with_com(|| list_mics().unwrap_or_default())
    }

    fn open(
        name: &str,
        flags: u32,
        activate: impl FnOnce(&IMMDeviceEnumerator) -> Result<IAudioClient> + Send + 'static,
    ) -> Result<AudioSource> {
        let shared = Arc::new(Shared {
            ring: Mutex::new(VecDeque::with_capacity(CAPACITY)),
            lost: AtomicBool::new(false),
            stop: AtomicBool::new(false),
        });
        let (ready, opened) = mpsc::channel();
        let worker_shared = shared.clone();
        let thread = thread::Builder::new()
            .name(name.into())
            .spawn(move || {
                with_com(|| {
                    let (client, capture) = match start(flags, activate) {
                        Ok(started) => started,
                        Err(e) => {
                            let _ = ready.send(Err(e));
                            return;
                        }
                    };
                    let _ = ready.send(Ok(()));
                    poll(&capture, &worker_shared);
                    let _ = unsafe { client.Stop() };
                })
            })
            .map_err(Error::Io)?;
        let source = AudioSource { shared, thread: Some(thread) };
        let started = opened.recv().unwrap_or_else(|_| Err(Error::Device("The sound capture stopped unexpectedly.".into())));
        started.map(|()| source)
    }
}

impl AudioIn for AudioSource {
    fn read(&mut self, out: &mut [f32]) -> usize {
        let mut ring = self.shared.ring.lock().unwrap_or_else(|e| e.into_inner());
        let n = out.len().min(ring.len()) & !1;
        for (o, s) in out.iter_mut().zip(ring.drain(..n)) {
            *o = s;
        }
        n
    }

    fn is_lost(&self) -> bool {
        self.shared.lost.load(Ordering::SeqCst)
    }
}

impl Drop for AudioSource {
    fn drop(&mut self) {
        self.shared.stop.store(true, Ordering::SeqCst);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

/// Runs `work` with COM started as multithreaded on this thread, and ends COM after `work` has dropped its objects.
fn with_com<T>(work: impl FnOnce() -> T) -> T {
    let com = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.ok();
    let result = work();
    if com.is_ok() {
        unsafe { CoUninitialize() };
    }
    result
}

fn start(
    flags: u32,
    activate: impl FnOnce(&IMMDeviceEnumerator) -> Result<IAudioClient>,
) -> Result<(IAudioClient, IAudioCaptureClient)> {
    let devices: IMMDeviceEnumerator = unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
        .map_err(|e| win(e, Error::Device, "Windows could not list the sound devices."))?;
    let client = activate(&devices)?;
    let format = WAVEFORMATEX {
        wFormatTag: 3, // IEEE float
        nChannels: 2,
        nSamplesPerSec: RATE,
        nAvgBytesPerSec: RATE * 8,
        nBlockAlign: 8,
        wBitsPerSample: 32,
        cbSize: 0,
    };
    let flags = flags | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    let begin = || -> windows::core::Result<IAudioCaptureClient> {
        // A 100 ms buffer, in 100 ns units.
        unsafe { client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 1_000_000, 0, &format, None) }?;
        let capture = unsafe { client.GetService::<IAudioCaptureClient>() }?;
        unsafe { client.Start() }?;
        Ok(capture)
    };
    let loopback = flags & AUDCLNT_STREAMFLAGS_LOOPBACK != 0;
    let capture = begin().map_err(|e| start_error(e, loopback))?;
    Ok((client, capture))
}

/// Some PCs refuse a blocked microphone only at `Initialize`, not at `Activate`.
fn start_error(e: windows::core::Error, loopback: bool) -> Error {
    if !loopback && e.code() == E_ACCESSDENIED {
        return Error::Device(MIC_BLOCKED.into());
    }
    win(e, Error::Device, "Windows could not start the sound capture.")
}

/// Polls every 10 ms until the source is dropped. Any error ends capture and marks the source lost.
fn poll(capture: &IAudioCaptureClient, shared: &Shared) {
    while !shared.stop.load(Ordering::SeqCst) {
        if drain(capture, shared).is_err() {
            shared.lost.store(true, Ordering::SeqCst);
            return;
        }
        thread::sleep(Duration::from_millis(10));
    }
}

fn drain(capture: &IAudioCaptureClient, shared: &Shared) -> windows::core::Result<()> {
    while unsafe { capture.GetNextPacketSize() }? > 0 {
        let (mut data, mut frames, mut flags) = (std::ptr::null_mut(), 0, 0);
        unsafe { capture.GetBuffer(&mut data, &mut frames, &mut flags, None, None) }?;
        let count = frames as usize * 2;
        let mut ring = shared.ring.lock().unwrap_or_else(|e| e.into_inner());
        if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 {
            ring.extend(std::iter::repeat_n(0.0, count));
        } else {
            // `GetBuffer` gives `frames` frames in the stereo float format set in `Initialize`.
            ring.extend(unsafe { std::slice::from_raw_parts(data as *const f32, count) });
        }
        let excess = ring.len().saturating_sub(CAPACITY);
        ring.drain(..excess);
        drop(ring);
        unsafe { capture.ReleaseBuffer(frames) }?;
    }
    Ok(())
}

fn list_mics() -> windows::core::Result<Vec<(String, String)>> {
    let devices: IMMDeviceEnumerator = unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }?;
    let all = unsafe { devices.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE) }?;
    // A device that fails to describe itself is skipped, so the others stay usable.
    Ok((0..unsafe { all.GetCount() }?).filter_map(|i| unsafe { all.Item(i) }.and_then(|d| describe(&d)).ok()).collect())
}

fn describe(device: &IMMDevice) -> windows::core::Result<(String, String)> {
    let raw = unsafe { device.GetId() }?;
    let id = unsafe { raw.to_string() };
    unsafe { CoTaskMemFree(Some(raw.0 as *const _)) };
    let mut value = unsafe { device.OpenPropertyStore(STGM_READ)?.GetValue(&PKEY_Device_FriendlyName) }?;
    let name = unsafe { PropVariantToBSTR(&value) };
    let _ = unsafe { PropVariantClear(&mut value) };
    Ok((id.map_err(|_| windows::core::Error::empty())?, name?.to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::Win32::Foundation::E_FAIL;

    fn start_text(code: windows::core::HRESULT, loopback: bool) -> String {
        start_error(windows::core::Error::from_hresult(code), loopback).to_string()
    }

    #[test]
    fn blocked_mic_at_start_gives_the_privacy_message() {
        assert_eq!(start_text(E_ACCESSDENIED, false), MIC_BLOCKED);
    }

    #[test]
    fn other_start_failures_keep_the_plain_sentence() {
        for (code, loopback) in [(E_ACCESSDENIED, true), (E_FAIL, false)] {
            let text = start_text(code, loopback);
            assert!(text.starts_with("Windows could not start the sound capture."), "{text}");
        }
    }
}
