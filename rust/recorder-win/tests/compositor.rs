use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU32, Ordering};

use recorder_core::frame_math::PixelSize;
use recorder_core::layout::{logo_rect, CameraItem, CameraShape, Corner, Layout, LogoItem};
use recorder_win::{init_process, Compositor, Device, Frame};
use windows::core::HSTRING;
use windows::Win32::Foundation::GENERIC_WRITE;
use windows::Win32::Graphics::Direct3D11::{
    ID3D11Texture2D, D3D11_BIND_SHADER_RESOURCE, D3D11_BOX, D3D11_CPU_ACCESS_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_READ,
    D3D11_SUBRESOURCE_DATA, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, GUID_ContainerFormatPng, GUID_WICPixelFormat32bppBGRA, IWICImagingFactory, WICBitmapEncoderNoCache,
};
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};

const RED: [u8; 4] = [0, 0, 255, 255];
const GREEN: [u8; 4] = [0, 255, 0, 255];
const BLUE: [u8; 4] = [255, 0, 0, 255];
const BLACK: [u8; 4] = [0, 0, 0, 255];
const WHITE: [u8; 4] = [255; 4];
const HD: PixelSize = PixelSize { width: 1920, height: 1080 };

/// A folder in the temp folder for test logos, deleted on drop.
struct TempDir(PathBuf);

impl TempDir {
    fn new() -> TempDir {
        static NEXT: AtomicU32 = AtomicU32::new(0);
        let name = format!("recorder-compositor-{}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst));
        let path = std::env::temp_dir().join(name);
        std::fs::create_dir(&path).unwrap();
        TempDir(path)
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

struct Setup {
    device: Device,
    compositor: Compositor,
    blue: Frame,
    red: Frame,
    dir: TempDir,
}

fn setup() -> Setup {
    init_process().unwrap();
    let device = Device::new().unwrap();
    Setup {
        compositor: Compositor::new(&device, HD, HD).unwrap(),
        blue: solid(&device, 1920, 1080, BLUE),
        red: solid(&device, 1280, 720, RED),
        device,
        dir: TempDir::new(),
    }
}

/// A camera frame whose pixel at column x has the color `color(x)`.
fn image(device: &Device, w: u32, h: u32, color: impl Fn(u32) -> [u8; 4]) -> Frame {
    let data: Vec<u8> = (0..w * h).flat_map(|i| color(i % w)).collect();
    let desc = D3D11_TEXTURE2D_DESC {
        Width: w,
        Height: h,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
        ..Default::default()
    };
    let init = D3D11_SUBRESOURCE_DATA { pSysMem: data.as_ptr().cast(), SysMemPitch: w * 4, SysMemSlicePitch: 0 };
    let mut texture = None;
    unsafe { device.d3d().CreateTexture2D(&desc, Some(&init), Some(&mut texture)) }.unwrap();
    Frame::new(texture.unwrap(), PixelSize { width: w as i32, height: h as i32 })
}

fn solid(device: &Device, w: u32, h: u32, bgra: [u8; 4]) -> Frame {
    image(device, w, h, |_| bgra)
}

/// Reads one BGRA pixel back from the GPU.
fn pixel(device: &Device, texture: &ID3D11Texture2D, x: i32, y: i32) -> [u8; 4] {
    let desc = D3D11_TEXTURE2D_DESC {
        Width: 1,
        Height: 1,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
        Usage: D3D11_USAGE_STAGING,
        CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
        ..Default::default()
    };
    let mut staging = None;
    unsafe { device.d3d().CreateTexture2D(&desc, None, Some(&mut staging)) }.unwrap();
    let staging = staging.unwrap();
    let (x, y) = (x as u32, y as u32);
    let area = D3D11_BOX { left: x, top: y, front: 0, right: x + 1, bottom: y + 1, back: 1 };
    unsafe { device.context().CopySubresourceRegion(&staging, 0, 0, 0, 0, texture, 0, Some(&area)) };
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    unsafe { device.context().Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)) }.unwrap();
    let px = unsafe { *(mapped.pData as *const [u8; 4]) };
    unsafe { device.context().Unmap(&staging, 0) };
    px
}

fn texture_size(t: &ID3D11Texture2D) -> (u32, u32) {
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    unsafe { t.GetDesc(&mut desc) };
    (desc.Width, desc.Height)
}

fn assert_color(expected: [u8; 4], actual: [u8; 4]) {
    let close = expected[..3].iter().zip(&actual[..3]).all(|(e, a)| e.abs_diff(*a) <= 2);
    assert!(close, "expected {expected:?}, got {actual:?}");
}

/// Writes a white PNG with WIC. With `transparent_left`, the left half is fully transparent.
fn png(dir: &Path, w: u32, h: u32, transparent_left: bool) -> PathBuf {
    let path = dir.join(format!("logo-{w}x{h}{}.png", if transparent_left { "-half" } else { "" }));
    let pixels: Vec<u8> = (0..w * h).flat_map(|i| if transparent_left && i % w < w / 2 { [0; 4] } else { WHITE }).collect();
    unsafe {
        let wic: IWICImagingFactory = CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER).unwrap();
        let stream = wic.CreateStream().unwrap();
        stream.InitializeFromFilename(&HSTRING::from(path.as_os_str()), GENERIC_WRITE.0).unwrap();
        let encoder = wic.CreateEncoder(&GUID_ContainerFormatPng, std::ptr::null()).unwrap();
        encoder.Initialize(&stream, WICBitmapEncoderNoCache).unwrap();
        let mut frame = None;
        encoder.CreateNewFrame(&mut frame, std::ptr::null_mut()).unwrap();
        let frame = frame.unwrap();
        frame.Initialize(None).unwrap();
        frame.SetSize(w, h).unwrap();
        let mut format = GUID_WICPixelFormat32bppBGRA;
        frame.SetPixelFormat(&mut format).unwrap();
        frame.WritePixels(h, w * 4, &pixels).unwrap();
        frame.Commit().unwrap();
        encoder.Commit().unwrap();
    }
    path
}

fn with_camera(visible: bool, shape: CameraShape) -> Layout {
    Layout { camera: CameraItem { visible, x: 0.1, y: 0.1, size: 0.25, shape }, ..Layout::default() }
}

fn with_logo(path: &Path) -> Layout {
    let path = Some(path.to_string_lossy().into_owned());
    Layout { logo: LogoItem { visible: true, corner: Corner::BottomRight, size: 0.08, path }, ..Layout::default() }
}

#[test]
fn circle_camera_covers_center_not_corner() {
    let mut s = setup();
    let layout = with_camera(true, CameraShape::Circle); // rect 192,108,270,270
    let out = s.compositor.draw_final(&s.blue, Some(&s.red), &layout).unwrap();
    assert_color(RED, pixel(&s.device, out, 327, 243));
    assert_color(BLUE, pixel(&s.device, out, 194, 110));
}

#[test]
fn square_camera_covers_corner() {
    let mut s = setup();
    let out = s.compositor.draw_final(&s.blue, Some(&s.red), &with_camera(true, CameraShape::Square)).unwrap();
    assert_color(RED, pixel(&s.device, out, 194, 110));
}

#[test]
fn hidden_camera_is_not_drawn() {
    let mut s = setup();
    let out = s.compositor.draw_final(&s.blue, Some(&s.red), &with_camera(false, CameraShape::Square)).unwrap();
    assert_color(BLUE, pixel(&s.device, out, 327, 243));
}

#[test]
fn missing_camera_frame_is_not_drawn() {
    let mut s = setup();
    let out = s.compositor.draw_final(&s.blue, None, &with_camera(true, CameraShape::Square)).unwrap();
    assert_color(BLUE, pixel(&s.device, out, 327, 243));
}

#[test]
fn camera_is_mirrored() {
    let mut s = setup();
    let cam = image(&s.device, 1280, 720, |x| if x < 640 { RED } else { GREEN });
    let out = s.compositor.draw_final(&s.blue, Some(&cam), &with_camera(true, CameraShape::Square)).unwrap();
    assert_color(GREEN, pixel(&s.device, out, 192 + 270 / 4, 243));
    assert_color(RED, pixel(&s.device, out, 192 + 270 * 3 / 4, 243));
}

#[test]
fn draw_screen_letterboxes_small_source() {
    let mut s = setup();
    let green = solid(&s.device, 500, 500, GREEN);
    let out = s.compositor.draw_screen(&green).unwrap();
    assert_color(BLACK, pixel(&s.device, out, 10, 540));
    assert_color(GREEN, pixel(&s.device, out, 960, 540));
}

#[test]
fn draw_screen_has_no_overlays() {
    let mut s = setup();
    assert_eq!(s.compositor.set_logo(Some(&png(&s.dir.0, 400, 200, false))), None);
    let out = s.compositor.draw_screen(&s.blue).unwrap();
    assert_color(BLUE, pixel(&s.device, out, 1900, 1060));
}

#[test]
fn logo_is_drawn_in_its_corner() {
    let mut s = setup();
    let path = png(&s.dir.0, 400, 200, false);
    assert_eq!(s.compositor.set_logo(Some(&path)), None);
    let layout = with_logo(&path);
    let r = logo_rect(&layout.logo, HD, PixelSize { width: 400, height: 200 });
    let out = s.compositor.draw_final(&s.blue, None, &layout).unwrap();
    assert_color(WHITE, pixel(&s.device, out, (r.x + r.width / 2.0) as i32, (r.y + r.height / 2.0) as i32));
}

#[test]
fn rounded_square_camera_cuts_the_corner_only() {
    let mut s = setup();
    let out = s.compositor.draw_final(&s.blue, Some(&s.red), &with_camera(true, CameraShape::RoundedSquare)).unwrap();
    assert_color(BLUE, pixel(&s.device, out, 194, 110));
    assert_color(RED, pixel(&s.device, out, 327, 110));
}

#[test]
fn logo_keeps_its_transparency() {
    let mut s = setup();
    let path = png(&s.dir.0, 400, 200, true);
    assert_eq!(s.compositor.set_logo(Some(&path)), None);
    let layout = with_logo(&path);
    let r = logo_rect(&layout.logo, HD, PixelSize { width: 400, height: 200 });
    let out = s.compositor.draw_final(&s.blue, None, &layout).unwrap();
    let y = (r.y + r.height / 2.0) as i32;
    assert_color(BLUE, pixel(&s.device, out, (r.x + r.width / 4.0) as i32, y));
    assert_color(WHITE, pixel(&s.device, out, (r.x + r.width * 3.0 / 4.0) as i32, y));
}

#[test]
fn missing_logo_returns_message() {
    let mut s = setup();
    let good = png(&s.dir.0, 400, 200, false);
    assert_eq!(s.compositor.set_logo(Some(&good)), None);
    let missing = Path::new(r"C:\nope.png");
    let message = s.compositor.set_logo(Some(missing)).unwrap();
    assert!(message.starts_with("The logo"), "{message}");
    // The failed call also cleared the logo that was loaded before.
    let out = s.compositor.draw_final(&s.blue, None, &with_logo(missing)).unwrap();
    assert_color(BLUE, pixel(&s.device, out, 1900, 1060));
}

#[test]
fn corrupt_logo_returns_message() {
    let mut s = setup();
    let path = s.dir.0.join("bad.png");
    std::fs::write(&path, "hello").unwrap();
    let message = s.compositor.set_logo(Some(&path)).unwrap();
    assert!(message.starts_with("The logo"), "{message}");
}

#[test]
fn oversized_logo_returns_message() {
    let mut s = setup();
    let message = s.compositor.set_logo(Some(&png(&s.dir.0, 9000, 100, false))).unwrap();
    assert!(message.starts_with("The logo"), "{message}");
}

#[test]
fn outputs_have_the_screen_and_frame_sizes() {
    let s = setup();
    let mut compositor = Compositor::new(&s.device, PixelSize { width: 3840, height: 2160 }, HD).unwrap();
    assert_eq!(texture_size(compositor.draw_screen(&s.blue).unwrap()), (1920, 1080));
    let layout = with_camera(true, CameraShape::Circle);
    assert_eq!(texture_size(compositor.draw_final(&s.blue, Some(&s.red), &layout).unwrap()), (3840, 2160));
}

#[test]
fn camera_file_is_the_whole_camera_frame() {
    let mut s = setup();
    let cam = image(&s.device, 1280, 720, |x| if x < 640 { RED } else { GREEN });
    let out = s.compositor.draw_camera_file(&cam).unwrap();
    assert_eq!(texture_size(out), (1280, 720));
    // Not mirrored and not cropped.
    assert_color(RED, pixel(&s.device, out, 5, 5));
    assert_color(GREEN, pixel(&s.device, out, 1275, 715));
}
