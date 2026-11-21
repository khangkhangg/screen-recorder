#[path = "common/media_info.rs"]
mod media_info;

use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicU32, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use recorder_core::frame_math::PixelSize;
use recorder_win::{check_encoders, init_process, max_encoder_size, Device, EncoderSupport, Error, VideoOut, VideoWriter};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11RenderTargetView, ID3D11Texture2D, D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_DEFAULT,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};

const SIZE: PixelSize = PixelSize { width: 1280, height: 720 };
/// Set for the child process of `cut_off_file_still_plays`.
const CHILD_PATH: &str = "RECORDER_VIDEO_TEST_CHILD_PATH";

/// An MP4 path in the temp folder. The file is deleted on drop.
struct TempMp4(PathBuf);

impl TempMp4 {
    fn new() -> TempMp4 {
        static NEXT: AtomicU32 = AtomicU32::new(0);
        let name = format!("recorder-video-{}-{}.mp4", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst));
        TempMp4(std::env::temp_dir().join(name))
    }
}

impl Drop for TempMp4 {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

fn hardware() -> bool {
    check_encoders(1) == EncoderSupport::Hardware
}

/// A 1280 by 720 render target that `paint` fills with one color.
fn canvas(device: &Device) -> (ID3D11Texture2D, ID3D11RenderTargetView) {
    let desc = D3D11_TEXTURE2D_DESC {
        Width: 1280,
        Height: 720,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
        ..Default::default()
    };
    let (mut texture, mut view) = (None, None);
    unsafe { device.d3d().CreateTexture2D(&desc, None, Some(&mut texture)) }.unwrap();
    let texture = texture.unwrap();
    unsafe { device.d3d().CreateRenderTargetView(&texture, None, Some(&mut view)) }.unwrap();
    (texture, view.unwrap())
}

fn paint(device: &Device, view: &ID3D11RenderTargetView, i: i64) {
    unsafe { device.context().ClearRenderTargetView(view, &[1.0, 0.0, (i * 2 % 256) as f32 / 255.0, 1.0]) };
}

/// Returns whether the writer used a hardware encoder.
fn write_solid_frames(path: &Path, count: i64, hardware: bool, with_audio: bool) -> bool {
    let device = Device::new().unwrap();
    let (texture, view) = canvas(&device);
    let mut writer = VideoWriter::create(path, SIZE, 60, &device, hardware, with_audio).unwrap();
    for i in 0..count {
        paint(&device, &view, i);
        writer.write_frame(&texture, i).unwrap();
    }
    if with_audio {
        writer.write_audio(&vec![0.0; 96_000 * 2], 0).unwrap();
    }
    writer.finish().unwrap();
    writer.uses_hardware_encoder()
}

#[test]
#[ignore = "needs the desktop"]
fn writes_constant_60fps() {
    init_process().unwrap();
    let path = TempMp4::new();
    let hardware = hardware();
    assert_eq!(hardware, write_solid_frames(&path.0, 120, hardware, false));
    let info = media_info::read(&path.0);
    assert_eq!(120, info.frame_count);
    assert!((1.98..=2.02).contains(&info.duration), "{info:?}");
    assert_eq!((1280, 720), (info.width, info.height));
    assert!(info.constant_frame_rate);
}

/// Runs only as the child process of `cut_off_file_still_plays`, which kills it.
#[test]
#[ignore = "needs the desktop"]
fn child_writes_300_frames() {
    let Some(path) = std::env::var_os(CHILD_PATH) else { return };
    init_process().unwrap();
    let device = Device::new().unwrap();
    let (texture, view) = canvas(&device);
    let mut writer = VideoWriter::create(Path::new(&path), SIZE, 60, &device, hardware(), false).unwrap();
    for i in 0..300 {
        paint(&device, &view, i);
        writer.write_frame(&texture, i).unwrap();
        if i == 199 {
            println!("wrote 200 frames");
        }
        // Real-time pace, so the encoder keeps up and the file holds most frames when the kill comes.
        thread::sleep(Duration::from_millis(16));
    }
    // Waits to be killed, so neither `finish` nor `Drop` runs.
    loop {
        thread::park();
    }
}

#[test]
#[ignore = "needs the desktop"]
fn cut_off_file_still_plays() {
    init_process().unwrap();
    let path = TempMp4::new();
    let mut child = Command::new(std::env::current_exe().unwrap())
        .args(["child_writes_300_frames", "--exact", "--ignored", "--nocapture"])
        .env(CHILD_PATH, &path.0)
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    let lines = BufReader::new(child.stdout.take().unwrap()).lines();
    let reached = lines.map_while(Result::ok).any(|line| line.contains("wrote 200 frames"));
    let _ = child.kill();
    child.wait().unwrap();
    assert!(reached, "the child stopped before 200 frames");
    let info = media_info::read(&path.0);
    assert!(info.frame_count >= 60, "{info:?}");
}

#[test]
#[ignore = "needs the desktop"]
fn audio_and_video_have_equal_length() {
    init_process().unwrap();
    let path = TempMp4::new();
    write_solid_frames(&path.0, 120, hardware(), true);
    let info = media_info::read(&path.0);
    assert!((info.audio_duration - info.duration).abs() <= 0.05, "{info:?}");
}

#[test]
#[ignore = "needs the desktop"]
fn audio_written_ahead_of_video_is_kept() {
    init_process().unwrap();
    let path = TempMp4::new();
    let device = Device::new().unwrap();
    let (texture, view) = canvas(&device);
    let mut writer = VideoWriter::create(&path.0, SIZE, 60, &device, hardware(), true).unwrap();
    for i in 0..200 {
        writer.write_audio(&[0.0; 480 * 2], i * 480).unwrap();
    }
    for i in 0..120 {
        paint(&device, &view, i);
        writer.write_frame(&texture, i).unwrap();
    }
    let watch = Instant::now();
    writer.finish().unwrap();
    assert!(watch.elapsed() < Duration::from_secs(5), "finish took {:?}", watch.elapsed());
    let info = media_info::read(&path.0);
    assert!((1.95..=2.05).contains(&info.duration), "{info:?}");
    assert!((1.95..=2.05).contains(&info.audio_duration), "{info:?}");
}

#[test]
#[ignore = "needs the desktop"]
fn probe_reports_a_value() {
    init_process().unwrap();
    println!("{:?}, largest size {:?}", check_encoders(3), max_encoder_size());
    assert!(max_encoder_size().width >= 1920);
}

#[test]
#[ignore = "needs the desktop"]
fn software_encoder_works() {
    init_process().unwrap();
    let path = TempMp4::new();
    assert!(!write_solid_frames(&path.0, 30, false, false));
    assert_eq!(30, media_info::read(&path.0).frame_count);
}

#[test]
#[ignore = "needs the desktop"]
fn audio_on_a_video_only_writer_is_an_error() {
    init_process().unwrap();
    let path = TempMp4::new();
    let device = Device::new().unwrap();
    let mut writer = VideoWriter::create(&path.0, SIZE, 60, &device, false, false).unwrap();
    assert!(matches!(writer.write_audio(&[0.0; 2], 0), Err(Error::Invalid(_))));
}
