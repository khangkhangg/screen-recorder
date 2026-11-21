use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use recorder_core::frame_math::PixelSize;
use windows::core::{Interface, BOOL, GUID, HSTRING, PWSTR};
use windows::Win32::Foundation::E_ACCESSDENIED;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_SHADER_RESOURCE, D3D11_SUBRESOURCE_DATA, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Media::MediaFoundation::{
    IMF2DBuffer, IMFActivate, IMFAttributes, IMFMediaSource, IMFMediaType, IMFSourceReader, MFCreateAttributes,
    MFCreateDeviceSource, MFCreateMediaType, MFCreateSourceReaderFromMediaSource, MFEnumDeviceSources,
    MFFrameSourceTypes_Color, MFMediaType_Video, MFVideoFormat_RGB32, MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES,
    MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID,
    MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, MF_MT_FRAME_RATE, MF_MT_FRAME_SIZE, MF_MT_MAJOR_TYPE,
    MF_MT_SUBTYPE, MF_SOURCE_READERF_ENDOFSTREAM, MF_SOURCE_READERF_ERROR, MF_SOURCE_READER_ALL_STREAMS,
    MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,
};
use windows::Win32::System::Com::CoTaskMemFree;

use crate::audio::with_com;
use crate::error::{win, Error, Result};
use crate::gfx::Device;
use crate::screen::Frame;

const BLOCKED: &str = "Camera access is turned off in the Windows privacy settings.";

/// A camera that the recording session pulls frames from on its own clock.
pub trait Camera: Send {
    fn size(&self) -> PixelSize;
    /// The newest frame. A frame the caller holds is never overwritten, so it may keep it as long as it likes.
    fn latest_frame(&mut self) -> Option<Frame>;
    /// True once the camera is unplugged or fails. It stays true.
    fn is_lost(&self) -> bool;
}

struct Shared {
    latest: Mutex<Option<Frame>>,
    lost: AtomicBool,
    stop: AtomicBool,
}

/// Captures a webcam through Media Foundation on its own thread.
pub struct WebcamSource {
    size: PixelSize,
    shared: Arc<Shared>,
    thread: Option<(Receiver<()>, JoinHandle<()>)>,
}

impl WebcamSource {
    /// The connected cameras as (id, name). Empty when there are none or Windows cannot list them.
    pub fn list() -> Vec<(String, String)> {
        with_com(|| cameras().unwrap_or_default())
    }

    /// Opens the camera with this id in the mode closest to 1280x720 at 30 frames per second.
    /// Call `init_process` first.
    pub fn open(id: &str, device: &Device) -> Result<WebcamSource> {
        let shared = Arc::new(Shared { latest: Mutex::new(None), lost: AtomicBool::new(false), stop: AtomicBool::new(false) });
        let (ready, opened) = mpsc::channel();
        let (done, finished) = mpsc::channel::<()>();
        let (id, device, worker_shared) = (HSTRING::from(id), device.clone(), shared.clone());
        let thread = thread::Builder::new()
            .name("camera".into())
            .spawn(move || {
                let _done = done;
                with_com(|| {
                    let source = match device_source(&id) {
                        Ok(source) => source,
                        Err(e) => {
                            let _ = ready.send(Err(camera_error(e, "Windows could not open the camera.")));
                            return;
                        }
                    };
                    match start(&source, &device) {
                        Ok((reader, stream, size, first)) => {
                            *worker_shared.latest.lock().unwrap_or_else(|e| e.into_inner()) = first;
                            let _ = ready.send(Ok(size));
                            run(&reader, stream, size, &device, &worker_shared);
                        }
                        Err(e) => {
                            let _ = ready.send(Err(e));
                        }
                    }
                    // Every way out releases the camera, because Windows keeps it locked for other apps until then.
                    let _ = unsafe { source.Shutdown() };
                })
            })
            .map_err(Error::Io)?;
        match opened.recv().unwrap_or_else(|_| Err(Error::Device("The camera stopped unexpectedly.".into()))) {
            Ok(size) => Ok(WebcamSource { size, shared, thread: Some((finished, thread)) }),
            Err(e) => {
                let _ = thread.join();
                Err(e)
            }
        }
    }
}

impl Camera for WebcamSource {
    fn size(&self) -> PixelSize {
        self.size
    }

    fn latest_frame(&mut self) -> Option<Frame> {
        self.shared.latest.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }

    fn is_lost(&self) -> bool {
        self.shared.lost.load(Ordering::SeqCst)
    }
}

impl Drop for WebcamSource {
    fn drop(&mut self) {
        self.shared.stop.store(true, Ordering::SeqCst);
        if let Some((finished, thread)) = self.thread.take() {
            // A stalled camera can block `ReadSample`, so after 1.5 s the thread is left to end on its own.
            if finished.recv_timeout(Duration::from_millis(1500)) != Err(RecvTimeoutError::Timeout) {
                let _ = thread.join();
            }
        }
    }
}

fn camera_error(e: windows::core::Error, sentence: &str) -> Error {
    if e.code() == E_ACCESSDENIED {
        return Error::Device(BLOCKED.into());
    }
    win(e, Error::Device, sentence)
}

fn video_capture_attributes() -> windows::core::Result<IMFAttributes> {
    let mut attributes = None;
    unsafe { MFCreateAttributes(&mut attributes, 2) }?;
    let attributes = attributes.ok_or_else(windows::core::Error::empty)?;
    unsafe { attributes.SetGUID(&MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID) }?;
    Ok(attributes)
}

fn cameras() -> windows::core::Result<Vec<(String, String)>> {
    let (mut all, mut count) = (std::ptr::null_mut(), 0);
    unsafe { MFEnumDeviceSources(&video_capture_attributes()?, &mut all, &mut count) }?;
    if all.is_null() {
        return Ok(Vec::new());
    }
    // The array and its entries are ours, so each entry is taken out to be released.
    let entries = unsafe { std::slice::from_raw_parts_mut(all, count as usize) };
    let list = entries
        .iter_mut()
        .filter_map(Option::take)
        .filter_map(|a| {
            Some((string(&a, &MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK)?, string(&a, &MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME)?))
        })
        .collect();
    unsafe { CoTaskMemFree(Some(all as *const _)) };
    Ok(list)
}

fn string(activate: &IMFActivate, key: &GUID) -> Option<String> {
    let (mut text, mut length) = (PWSTR::null(), 0);
    unsafe { activate.GetAllocatedString(key, &mut text, &mut length) }.ok()?;
    let value = unsafe { text.to_string() };
    unsafe { CoTaskMemFree(Some(text.0 as *const _)) };
    value.ok()
}

fn device_source(id: &HSTRING) -> windows::core::Result<IMFMediaSource> {
    let attributes = video_capture_attributes()?;
    unsafe { attributes.SetString(&MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, id) }?;
    unsafe { MFCreateDeviceSource(&attributes) }
}

/// Picks the stream and mode, then reads one sample to prove the camera started.
fn start(source: &IMFMediaSource, device: &Device) -> Result<(IMFSourceReader, u32, PixelSize, Option<Frame>)> {
    let open_error = |e| camera_error(e, "Windows could not open the camera.");
    let stream = video_stream(source).map_err(open_error)?.ok_or_else(|| Error::Device("This camera has no color video stream.".into()))?;
    let reader = reader(source).map_err(open_error)?;
    let (mode, size) = best_mode(&reader, stream).ok_or_else(|| Error::Device("This camera has no usable video mode.".into()))?;
    let not_started = |e| camera_error(e, "The camera could not start. Another app may be using it.");
    configure(&reader, stream, &mode).map_err(not_started)?;
    let first = read(&reader, stream, size, device).map_err(not_started)?;
    Ok((reader, stream, size, first))
}

/// The first color video stream, or else the first video stream.
fn video_stream(source: &IMFMediaSource) -> windows::core::Result<Option<u32>> {
    let presentation = unsafe { source.CreatePresentationDescriptor() }?;
    let mut first_video = None;
    for i in 0..unsafe { presentation.GetStreamDescriptorCount() }? {
        let (mut selected, mut stream) = (BOOL::default(), None);
        unsafe { presentation.GetStreamDescriptorByIndex(i, &mut selected, &mut stream) }?;
        let Some(stream) = stream else { continue };
        if unsafe { stream.GetMediaTypeHandler()?.GetMajorType() }? != MFMediaType_Video {
            continue;
        }
        let kinds = unsafe { stream.GetUINT32(&MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES) };
        if kinds.is_ok_and(|k| k & MFFrameSourceTypes_Color.0 as u32 != 0) {
            return Ok(Some(i));
        }
        first_video.get_or_insert(i);
    }
    Ok(first_video)
}

fn reader(source: &IMFMediaSource) -> windows::core::Result<IMFSourceReader> {
    let mut attributes = None;
    unsafe { MFCreateAttributes(&mut attributes, 1) }?;
    let attributes = attributes.ok_or_else(windows::core::Error::empty)?;
    // Lets the reader turn the camera's YUV or MJPG frames into 32-bit BGR.
    unsafe { attributes.SetUINT32(&MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1) }?;
    unsafe { MFCreateSourceReaderFromMediaSource(source, &attributes) }
}

/// The native mode closest to 1280x720, then closest to 30 frames per second.
fn best_mode(reader: &IMFSourceReader, stream: u32) -> Option<(IMFMediaType, PixelSize)> {
    (0..)
        .map_while(|i| unsafe { reader.GetNativeMediaType(stream, i) }.ok())
        .filter_map(|mode| {
            let size = unsafe { mode.GetUINT64(&MF_MT_FRAME_SIZE) }.ok()?;
            let rate = unsafe { mode.GetUINT64(&MF_MT_FRAME_RATE) }.ok()?;
            let size = PixelSize { width: (size >> 32) as i32, height: size as u32 as i32 };
            let fps = (rate >> 32) as f64 / (rate as u32).max(1) as f64;
            let distance = ((size.width - 1280).abs() + (size.height - 720).abs(), ((fps - 30.0).abs() * 1000.0) as i64);
            Some((distance, mode, size))
        })
        .min_by_key(|(distance, ..)| *distance)
        .map(|(_, mode, size)| (mode, size))
}

fn configure(reader: &IMFSourceReader, stream: u32, mode: &IMFMediaType) -> windows::core::Result<()> {
    unsafe { reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS.0 as u32, false) }?;
    unsafe { reader.SetStreamSelection(stream, true) }?;
    unsafe { reader.SetCurrentMediaType(stream, None, mode) }?;
    let output = unsafe { MFCreateMediaType() }?;
    unsafe { output.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video) }?;
    unsafe { output.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_RGB32) }?;
    unsafe { reader.SetCurrentMediaType(stream, None, &output) }
}

/// Reads frames until the source is dropped. Any error ends capture and marks the source lost.
fn run(reader: &IMFSourceReader, stream: u32, size: PixelSize, device: &Device, shared: &Shared) {
    while !shared.stop.load(Ordering::SeqCst) {
        match read(reader, stream, size, device) {
            Ok(Some(frame)) => *shared.latest.lock().unwrap_or_else(|e| e.into_inner()) = Some(frame),
            Ok(None) => {}
            Err(_) => {
                shared.lost.store(true, Ordering::SeqCst);
                return;
            }
        }
    }
}

/// Waits for the next sample. `None` is a gap in the stream, which cameras send now and then.
fn read(reader: &IMFSourceReader, stream: u32, size: PixelSize, device: &Device) -> windows::core::Result<Option<Frame>> {
    let (mut flags, mut sample) = (0, None);
    unsafe { reader.ReadSample(stream, 0, None, Some(&mut flags), None, Some(&mut sample)) }?;
    if flags & (MF_SOURCE_READERF_ERROR.0 | MF_SOURCE_READERF_ENDOFSTREAM.0) as u32 != 0 {
        return Err(windows::core::Error::empty());
    }
    let Some(sample) = sample else { return Ok(None) };
    let image: IMF2DBuffer = unsafe { sample.ConvertToContiguousBuffer() }?.cast()?;
    let row = size.width as usize * 4;
    let mut pixels = vec![0u8; row * size.height as usize];
    let (mut start, mut pitch) = (std::ptr::null_mut(), 0);
    unsafe { image.Lock2D(&mut start, &mut pitch) }?;
    for (y, out) in pixels.chunks_exact_mut(row).enumerate() {
        // `pitch` is negative for bottom-up images, and Media Foundation gives `start` as the top row either way.
        let line = unsafe { std::slice::from_raw_parts(start.offset(y as isize * pitch as isize), row) };
        out.copy_from_slice(line);
    }
    unsafe { image.Unlock2D() }?;
    upload(&pixels, size, device).map(Some)
}

/// Every frame gets a new texture, so a frame a caller holds is never overwritten.
fn upload(pixels: &[u8], size: PixelSize, device: &Device) -> windows::core::Result<Frame> {
    let desc = D3D11_TEXTURE2D_DESC {
        Width: size.width as u32,
        Height: size.height as u32,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
        ..Default::default()
    };
    let init = D3D11_SUBRESOURCE_DATA { pSysMem: pixels.as_ptr().cast(), SysMemPitch: size.width as u32 * 4, SysMemSlicePitch: 0 };
    let mut texture = None;
    // Creating a texture uses the device only, not its context, so it needs no Direct2D lock.
    unsafe { device.d3d().CreateTexture2D(&desc, Some(&init), Some(&mut texture)) }?;
    Ok(Frame::new(texture.ok_or_else(windows::core::Error::empty)?, size))
}
