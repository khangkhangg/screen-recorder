use std::path::Path;

use windows::core::HSTRING;
use windows::Win32::Media::MediaFoundation::{
    IMFSourceReader, MFCreateSourceReaderFromURL, MF_MT_FRAME_SIZE, MF_SOURCE_READERF_ENDOFSTREAM, MF_SOURCE_READERF_ERROR,
    MF_SOURCE_READER_ALL_STREAMS, MF_SOURCE_READER_FIRST_AUDIO_STREAM, MF_SOURCE_READER_FIRST_VIDEO_STREAM,
};

/// Durations are in seconds.
#[derive(Debug)]
pub struct MediaInfo {
    pub duration: f64,
    pub frame_count: usize,
    pub width: u32,
    pub height: u32,
    pub constant_frame_rate: bool,
    pub audio_duration: f64,
}

/// Reads the compressed samples without decoding them, so it is fast on long files. Call `init_process` first.
pub fn read(path: &Path) -> MediaInfo {
    let size = unsafe { reader(path).GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM.0 as u32, 0) }
        .and_then(|t| unsafe { t.GetUINT64(&MF_MT_FRAME_SIZE) })
        .unwrap();
    let mut video = samples(path, MF_SOURCE_READER_FIRST_VIDEO_STREAM.0 as u32);
    let has_audio = unsafe { reader(path).GetNativeMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM.0 as u32, 0) }.is_ok();
    let audio = if has_audio { samples(path, MF_SOURCE_READER_FIRST_AUDIO_STREAM.0 as u32) } else { Vec::new() };
    video.sort();
    let constant = video.len() > 1 && video.windows(2).all(|p| (p[1].0 - p[0].0 - (video[1].0 - video[0].0)).abs() <= 1);
    MediaInfo {
        duration: length(&video),
        frame_count: video.len(),
        width: (size >> 32) as u32,
        height: size as u32,
        constant_frame_rate: constant,
        audio_duration: length(&audio),
    }
}

fn reader(path: &Path) -> IMFSourceReader {
    unsafe { MFCreateSourceReaderFromURL(&HSTRING::from(path.as_os_str()), None) }.unwrap()
}

/// The (time, duration) of every sample in the stream, in 100 ns units.
fn samples(path: &Path, stream: u32) -> Vec<(i64, i64)> {
    let reader = reader(path);
    unsafe { reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS.0 as u32, false) }.unwrap();
    unsafe { reader.SetStreamSelection(stream, true) }.unwrap();
    let mut list = Vec::new();
    loop {
        let (mut flags, mut sample) = (0, None);
        unsafe { reader.ReadSample(stream, 0, None, Some(&mut flags), None, Some(&mut sample)) }.unwrap();
        if let Some(s) = sample {
            list.push(unsafe { (s.GetSampleTime().unwrap(), s.GetSampleDuration().unwrap()) });
        }
        if flags & (MF_SOURCE_READERF_ENDOFSTREAM.0 | MF_SOURCE_READERF_ERROR.0) as u32 != 0 {
            return list;
        }
    }
}

fn length(samples: &[(i64, i64)]) -> f64 {
    let end = samples.iter().map(|s| s.0 + s.1).max().unwrap_or(0);
    let start = samples.iter().map(|s| s.0).min().unwrap_or(0);
    (end - start) as f64 / 10_000_000.0
}
