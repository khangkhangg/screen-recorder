use std::collections::VecDeque;
use std::path::Path;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

use recorder_core::frame_math::{bitrate, PixelSize};
use windows::core::{Interface, GUID, HSTRING};
use windows::Win32::Graphics::Direct2D::ID2D1Multithread;
use windows::Win32::Graphics::Direct3D11::{ID3D11Device, ID3D11Texture2D, D3D11_TEXTURE2D_DESC};
use windows::Win32::Media::MediaFoundation::{
    IMF2DBuffer, IMFActivate, IMFDXGIDeviceManager, IMFMediaBuffer, IMFMediaType, IMFSinkWriter, IMFSinkWriterEx,
    IMFTransform, MFAudioFormat_AAC, MFAudioFormat_PCM, MFCreateAttributes, MFCreateDXGIDeviceManager,
    MFCreateDXGISurfaceBuffer, MFCreateMediaType, MFCreateMemoryBuffer, MFCreateSample, MFCreateSinkWriterFromURL,
    MFMediaType_Audio, MFMediaType_Video, MFTEnumEx, MFTranscodeContainerType_FMPEG4, MFVideoFormat_H264,
    MFVideoFormat_NV12, MFVideoFormat_RGB32, MFVideoInterlace_Progressive, MFT_CATEGORY_VIDEO_ENCODER,
    MFT_ENUM_FLAG_HARDWARE, MFT_ENUM_FLAG_SORTANDFILTER, MFT_ENUM_HARDWARE_URL_Attribute,
    MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, MFT_REGISTER_TYPE_INFO, MF_E_SINK_NO_SAMPLES_PROCESSED,
    MF_MT_AUDIO_AVG_BYTES_PER_SECOND, MF_MT_AUDIO_BITS_PER_SAMPLE, MF_MT_AUDIO_BLOCK_ALIGNMENT,
    MF_MT_AUDIO_NUM_CHANNELS, MF_MT_AUDIO_SAMPLES_PER_SECOND, MF_MT_AVG_BITRATE, MF_MT_DEFAULT_STRIDE,
    MF_MT_FRAME_RATE, MF_MT_FRAME_SIZE, MF_MT_INTERLACE_MODE, MF_MT_MAJOR_TYPE, MF_MT_PIXEL_ASPECT_RATIO,
    MF_MT_SUBTYPE, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, MF_SINK_WRITER_D3D_MANAGER, MF_TRANSCODE_CONTAINERTYPE,
    MF_TRANSFORM_ASYNC_UNLOCK,
};
use windows::Win32::System::Com::CoTaskMemFree;

use crate::error::{win, win_file, Error, Result};
use crate::gfx::Device;

const AUDIO_RATE: i64 = 48_000;
const TICKS_PER_SECOND: i64 = 10_000_000;
/// Used when the hardware encoder refuses 8K, which is the largest size it is asked about.
const FALLBACK_SIZE: PixelSize = PixelSize { width: 4096, height: 2304 };

/// Where the recording session sends encoded video and sound.
pub trait VideoOut: Send {
    fn write_frame(&mut self, texture: &ID3D11Texture2D, index: i64) -> Result<()>;
    fn write_audio(&mut self, samples: &[f32], first_sample: i64) -> Result<()>;
    fn frames_behind(&self) -> i64;
    fn finish(&mut self) -> Result<()>;
}

/// Writes H.264 video and AAC audio into a fragmented MP4, so a file cut off by a crash still plays.
pub struct VideoWriter {
    device: Device,
    d2d_lock: ID2D1Multithread,
    with_audio: bool,
    hardware: bool,
    jobs: Option<Sender<Job>>,
    worker: Option<JoinHandle<Result<()>>>,
    shared: Arc<Shared>,
    /// True once an encoder error has been returned to the caller.
    failed: bool,
}

struct Shared {
    behind: AtomicI64,
    error: Mutex<Option<Error>>,
}

enum Job {
    Video(ID3D11Texture2D, i64),
    /// Interleaved 16-bit stereo and the index of its first sample frame.
    Audio(Vec<i16>, i64),
}

impl VideoWriter {
    pub fn create(
        path: &Path,
        size: PixelSize,
        frame_rate: i32,
        device: &Device,
        hardware: bool,
        with_audio: bool,
    ) -> Result<VideoWriter> {
        if frame_rate <= 0 {
            return Err(Error::Invalid("The frame rate must be above 0.".into()));
        }
        if hardware && !device.video_support() {
            return Err(Error::NotSupported(
                "This graphics adapter has no hardware video encoder. Turn off hardware encoding.".into(),
            ));
        }
        let d2d_lock = unsafe { device.d2d().GetFactory() }
            .and_then(|f| f.cast::<ID2D1Multithread>())
            .map_err(|e| win(e, Error::Device, "The graphics device could not be shared with the encoder."))?;
        let shared = Arc::new(Shared { behind: AtomicI64::new(0), error: Mutex::new(None) });
        let (jobs, received) = mpsc::channel();
        let (ready, opened) = mpsc::channel();
        let (path, d3d, worker_shared) = (path.to_path_buf(), device.d3d().clone(), shared.clone());
        let worker = thread::spawn(move || {
            let sink = match Sink::open(&path, size, frame_rate, &d3d, hardware, with_audio) {
                Ok(sink) => sink,
                Err(e) => {
                    let _ = std::fs::remove_file(&path);
                    let _ = ready.send(Err(e));
                    return Ok(());
                }
            };
            let _ = ready.send(Ok(sink.uses_hardware));
            sink.run(received, &worker_shared)
        });
        let uses_hardware = opened.recv().unwrap_or_else(|_| Err(stopped()))?;
        Ok(VideoWriter {
            device: device.clone(),
            d2d_lock,
            with_audio,
            hardware: uses_hardware,
            jobs: Some(jobs),
            worker: Some(worker),
            shared,
            failed: false,
        })
    }

    pub fn uses_hardware_encoder(&self) -> bool {
        self.hardware
    }

    /// Returns an encoder error from the worker once, then a short error on every later call.
    fn check(&mut self) -> Result<()> {
        if self.failed {
            return Err(Error::Encoder("The recording stopped after an earlier error.".into()));
        }
        let error = self.shared.error.lock().unwrap_or_else(|e| e.into_inner()).take();
        self.failed = error.is_some();
        error.map_or(Ok(()), Err)
    }

    fn send(&mut self, job: Job) -> Result<()> {
        let jobs = self.jobs.as_ref().ok_or_else(|| Error::Invalid("The writer is already finished.".into()))?;
        jobs.send(job).map_err(|_| stopped())
    }
}

impl VideoOut for VideoWriter {
    /// Copies the frame on the GPU, because the caller draws the next frame into the same texture.
    fn write_frame(&mut self, texture: &ID3D11Texture2D, index: i64) -> Result<()> {
        self.check()?;
        let mut desc = D3D11_TEXTURE2D_DESC::default();
        unsafe { texture.GetDesc(&mut desc) };
        desc.MiscFlags = 0;
        let mut copy = None;
        unsafe { self.device.d3d().CreateTexture2D(&desc, None, Some(&mut copy)) }
            .and_then(|_| copy.ok_or_else(windows::core::Error::empty))
            .map_err(|e| win(e, Error::Device, "The frame could not be copied."))
            .and_then(|copy| {
                // The capture code draws with Direct2D on this context from another thread.
                unsafe {
                    self.d2d_lock.Enter();
                    self.device.context().CopyResource(&copy, texture);
                    self.d2d_lock.Leave();
                }
                self.shared.behind.fetch_add(1, Ordering::SeqCst);
                self.send(Job::Video(copy, index))
            })
    }

    /// Takes interleaved stereo at 48 kHz.
    fn write_audio(&mut self, samples: &[f32], first_sample: i64) -> Result<()> {
        if !self.with_audio {
            return Err(Error::Invalid("This writer was opened without audio.".into()));
        }
        self.check()?;
        // The AAC encoder takes 16-bit PCM only. The cast saturates out-of-range samples.
        let pcm = samples.iter().map(|s| (s * 32767.0) as i16).collect();
        self.send(Job::Audio(pcm, first_sample))
    }

    fn frames_behind(&self) -> i64 {
        self.shared.behind.load(Ordering::SeqCst)
    }

    fn finish(&mut self) -> Result<()> {
        self.jobs = None;
        let Some(worker) = self.worker.take() else { return Ok(()) };
        let result = worker.join().unwrap_or_else(|_| Err(stopped()));
        self.check()?;
        result
    }
}

impl Drop for VideoWriter {
    fn drop(&mut self) {
        let _ = self.finish();
    }
}

fn stopped() -> Error {
    Error::Encoder("The video encoder stopped unexpectedly.".into())
}

/// Win32 errors such as a full disk become `Error::Io`, so callers handle them like other file errors.
fn write_error(e: windows::core::Error) -> Error {
    win_file(e, Error::Encoder, "The recording could not be written.")
}

/// The Media Foundation side, which lives on the worker thread only.
struct Sink {
    writer: IMFSinkWriter,
    _manager: Option<IMFDXGIDeviceManager>,
    video: u32,
    audio: Option<u32>,
    frame_rate: i64,
    uses_hardware: bool,
    failed: bool,
}

impl Sink {
    fn open(path: &Path, size: PixelSize, frame_rate: i32, d3d: &ID3D11Device, hardware: bool, with_audio: bool) -> Result<Sink> {
        let mut sink = Sink::create(path, size, frame_rate, d3d, hardware, with_audio)
            .map_err(|e| win_file(e, Error::Encoder, "The recording file could not be created."))?;
        sink.uses_hardware = sink.hardware_loaded();
        // The software encoder reads every frame back to the CPU, so the caller must choose it on purpose.
        if hardware && !sink.uses_hardware {
            return Err(Error::NotSupported(
                "The hardware H.264 encoder did not load. Turn off hardware encoding.".into(),
            ));
        }
        Ok(sink)
    }

    fn create(
        path: &Path,
        size: PixelSize,
        frame_rate: i32,
        d3d: &ID3D11Device,
        hardware: bool,
        with_audio: bool,
    ) -> windows::core::Result<Sink> {
        let mut attributes = None;
        unsafe { MFCreateAttributes(&mut attributes, 3) }?;
        let attributes = attributes.ok_or_else(windows::core::Error::empty)?;
        unsafe { attributes.SetGUID(&MF_TRANSCODE_CONTAINERTYPE, &MFTranscodeContainerType_FMPEG4) }?;
        let mut manager = None;
        if hardware {
            let mut token = 0;
            unsafe { MFCreateDXGIDeviceManager(&mut token, &mut manager) }?;
            let m = manager.as_ref().ok_or_else(windows::core::Error::empty)?;
            unsafe {
                m.ResetDevice(d3d, token)?;
                attributes.SetUnknown(&MF_SINK_WRITER_D3D_MANAGER, m)?;
                attributes.SetUINT32(&MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1)?;
            }
        }
        let writer = unsafe { MFCreateSinkWriterFromURL(&HSTRING::from(path.as_os_str()), None, &attributes) }?;
        let output = video_type(&MFVideoFormat_H264, size, frame_rate)?;
        unsafe { output.SetUINT32(&MF_MT_AVG_BITRATE, bitrate(size, frame_rate) as u32) }?;
        let video = unsafe { writer.AddStream(&output) }?;
        let input = video_type(&MFVideoFormat_RGB32, size, frame_rate)?;
        unsafe {
            input.SetUINT32(&MF_MT_DEFAULT_STRIDE, size.width as u32 * 4)?;
            writer.SetInputMediaType(video, &input, None)?;
        }
        let audio = if with_audio {
            let output = audio_type(&MFAudioFormat_AAC)?;
            let input = audio_type(&MFAudioFormat_PCM)?;
            unsafe {
                output.SetUINT32(&MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 192_000 / 8)?;
                let stream = writer.AddStream(&output)?;
                input.SetUINT32(&MF_MT_AUDIO_BLOCK_ALIGNMENT, 4)?;
                input.SetUINT32(&MF_MT_AUDIO_AVG_BYTES_PER_SECOND, AUDIO_RATE as u32 * 4)?;
                writer.SetInputMediaType(stream, &input, None)?;
                Some(stream)
            }
        } else {
            None
        };
        unsafe { writer.BeginWriting() }?;
        let frame_rate = i64::from(frame_rate);
        Ok(Sink { writer, _manager: manager, video, audio, frame_rate, uses_hardware: false, failed: false })
    }

    /// Hardware encoders carry `MFT_ENUM_HARDWARE_URL_Attribute` and software ones do not.
    fn hardware_loaded(&self) -> bool {
        let Ok(writer) = self.writer.cast::<IMFSinkWriterEx>() else { return false };
        for index in 0.. {
            let (mut category, mut transform) = (GUID::zeroed(), None);
            if unsafe { writer.GetTransformForStream(self.video, index, Some(&mut category), &mut transform) }.is_err() {
                return false;
            }
            if let (Some(transform), true) = (transform, category == MFT_CATEGORY_VIDEO_ENCODER) {
                let length = unsafe { transform.GetAttributes().and_then(|a| a.GetStringLength(&MFT_ENUM_HARDWARE_URL_Attribute)) };
                return length.is_ok_and(|n| n > 0);
            }
        }
        false
    }

    /// The sink writer blocks audio that is ahead of the video, while that video waits behind it in this
    /// queue. So audio waits here until video up to its time has been written.
    fn run(mut self, jobs: Receiver<Job>, shared: &Shared) -> Result<()> {
        let mut held: VecDeque<(Vec<i16>, i64)> = VecDeque::new();
        for job in jobs {
            match job {
                Job::Video(texture, index) => {
                    let (time, duration) = (index * TICKS_PER_SECOND / self.frame_rate, TICKS_PER_SECOND / self.frame_rate);
                    self.attempt(shared, |s| s.write_video(&texture, time, duration));
                    shared.behind.fetch_sub(1, Ordering::SeqCst);
                    while held.front().is_some_and(|a| audio_time(a.1) < time + duration) {
                        if let Some((pcm, first)) = held.pop_front() {
                            self.attempt(shared, |s| s.write_audio(&pcm, first));
                        }
                    }
                }
                Job::Audio(pcm, first) => held.push_back((pcm, first)),
            }
        }
        for (pcm, first) in held {
            self.attempt(shared, |s| s.write_audio(&pcm, first));
        }
        if self.failed {
            return Ok(());
        }
        match unsafe { self.writer.Finalize() } {
            // A writer closed before its first sample has nothing to finalize.
            Err(e) if e.code() != MF_E_SINK_NO_SAMPLES_PROCESSED => Err(write_error(e)),
            _ => Ok(()),
        }
    }

    /// Runs one write unless an earlier one failed, and keeps the first error for the caller.
    fn attempt(&mut self, shared: &Shared, write: impl FnOnce(&Sink) -> windows::core::Result<()>) {
        if self.failed {
            return;
        }
        if let Err(e) = write(self) {
            self.failed = true;
            *shared.error.lock().unwrap_or_else(|e| e.into_inner()) = Some(write_error(e));
        }
    }

    fn write_video(&self, texture: &ID3D11Texture2D, time: i64, duration: i64) -> windows::core::Result<()> {
        let buffer = unsafe { MFCreateDXGISurfaceBuffer(&ID3D11Texture2D::IID, texture, 0, false) }?;
        let length = unsafe { buffer.cast::<IMF2DBuffer>()?.GetContiguousLength() }?;
        unsafe { buffer.SetCurrentLength(length) }?;
        self.write(self.video, &buffer, time, duration)
    }

    fn write_audio(&self, pcm: &[i16], first: i64) -> windows::core::Result<()> {
        let Some(stream) = self.audio else { return Ok(()) };
        let bytes = std::mem::size_of_val(pcm);
        let buffer = unsafe { MFCreateMemoryBuffer(bytes as u32) }?;
        let mut data = std::ptr::null_mut();
        unsafe {
            buffer.Lock(&mut data, None, None)?;
            // `Lock` gives a writable block of at least the `bytes` asked for above.
            std::ptr::copy_nonoverlapping(pcm.as_ptr().cast::<u8>(), data, bytes);
            buffer.Unlock()?;
            buffer.SetCurrentLength(bytes as u32)?;
        }
        let frames = pcm.len() as i64 / 2;
        self.write(stream, &buffer, audio_time(first), audio_time(frames))
    }

    fn write(&self, stream: u32, buffer: &IMFMediaBuffer, time: i64, duration: i64) -> windows::core::Result<()> {
        let sample = unsafe { MFCreateSample() }?;
        unsafe {
            sample.AddBuffer(buffer)?;
            sample.SetSampleTime(time)?;
            sample.SetSampleDuration(duration)?;
            self.writer.WriteSample(stream, &sample)
        }
    }
}

/// Converts a count of audio sample frames to 100 ns units.
fn audio_time(frames: i64) -> i64 {
    frames * TICKS_PER_SECOND / AUDIO_RATE
}

fn video_type(subtype: &GUID, size: PixelSize, frame_rate: i32) -> windows::core::Result<IMFMediaType> {
    let t = unsafe { MFCreateMediaType() }?;
    unsafe {
        t.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
        t.SetGUID(&MF_MT_SUBTYPE, subtype)?;
        t.SetUINT64(&MF_MT_FRAME_SIZE, pack(size.width, size.height))?;
        t.SetUINT64(&MF_MT_FRAME_RATE, pack(frame_rate, 1))?;
        t.SetUINT64(&MF_MT_PIXEL_ASPECT_RATIO, pack(1, 1))?;
        t.SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)?;
    }
    Ok(t)
}

fn audio_type(subtype: &GUID) -> windows::core::Result<IMFMediaType> {
    let t = unsafe { MFCreateMediaType() }?;
    unsafe {
        t.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio)?;
        t.SetGUID(&MF_MT_SUBTYPE, subtype)?;
        t.SetUINT32(&MF_MT_AUDIO_NUM_CHANNELS, 2)?;
        t.SetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND, AUDIO_RATE as u32)?;
        t.SetUINT32(&MF_MT_AUDIO_BITS_PER_SAMPLE, 16)?;
    }
    Ok(t)
}

fn pack(high: i32, low: i32) -> u64 {
    (u64::from(high as u32) << 32) | u64::from(low as u32)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EncoderSupport {
    Hardware,
    TooFewHardwareSessions,
    SoftwareOnly,
}

/// Opens the hardware encoder `sessions` times at once at 1080p and closes them again. It encodes no frames.
pub fn check_encoders(sessions: u32) -> EncoderSupport {
    // An activate object returns the same encoder on every call, so each session enumerates its own.
    let mut opened: Vec<Vec<IMFActivate>> = Vec::new();
    let mut open_all = || -> windows::core::Result<EncoderSupport> {
        for _ in 0..sessions {
            opened.push(hardware_encoders()?);
            let Some(encoder) = opened.last().and_then(|list| list.first()) else {
                return Ok(EncoderSupport::SoftwareOnly);
            };
            open(encoder, PixelSize { width: 1920, height: 1080 })?;
        }
        Ok(EncoderSupport::Hardware)
    };
    let support = open_all().unwrap_or(EncoderSupport::TooFewHardwareSessions);
    for encoder in opened.iter().filter_map(|list| list.first()) {
        let _ = unsafe { encoder.ShutdownObject() };
    }
    support
}

/// Media Foundation has no "largest size" attribute, so the encoder reports 8K by accepting it.
pub fn max_encoder_size() -> PixelSize {
    let Some(encoder) = hardware_encoders().ok().and_then(|list| list.into_iter().next()) else { return FALLBACK_SIZE };
    let eight_k = PixelSize { width: 7680, height: 4320 };
    let size = if open(&encoder, eight_k).is_ok() { eight_k } else { FALLBACK_SIZE };
    let _ = unsafe { encoder.ShutdownObject() };
    size
}

fn hardware_encoders() -> windows::core::Result<Vec<IMFActivate>> {
    let output = MFT_REGISTER_TYPE_INFO { guidMajorType: MFMediaType_Video, guidSubtype: MFVideoFormat_H264 };
    let (mut list, mut count) = (std::ptr::null_mut(), 0);
    let flags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER;
    unsafe { MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, None, Some(&output), &mut list, &mut count) }?;
    if list.is_null() {
        return Ok(Vec::new());
    }
    // We own the array and one reference in each entry. `take` moves each reference into the Vec.
    let entries = unsafe { std::slice::from_raw_parts_mut(list, count as usize) };
    let activates = entries.iter_mut().filter_map(Option::take).collect();
    unsafe { CoTaskMemFree(Some(list as *const _)) };
    Ok(activates)
}

/// The activate object keeps the encoder open until `ShutdownObject`, after the caller drops the transform.
fn open(activate: &IMFActivate, size: PixelSize) -> windows::core::Result<IMFTransform> {
    let transform: IMFTransform = unsafe { activate.ActivateObject() }?;
    let t = video_type(&MFVideoFormat_H264, size, 30)?;
    unsafe {
        transform.GetAttributes()?.SetUINT32(&MF_TRANSFORM_ASYNC_UNLOCK, 1)?;
        t.SetUINT32(&MF_MT_AVG_BITRATE, bitrate(size, 30) as u32)?;
        transform.SetOutputType(0, &t, 0)?;
        t.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_NV12)?;
        t.DeleteItem(&MF_MT_AVG_BITRATE)?;
        transform.SetInputType(0, &t, 0)?;
        transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0)?;
    }
    Ok(transform)
}
