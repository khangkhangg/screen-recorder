use std::sync::mpsc;
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use recorder_core::frame_math::PixelSize;
use recorder_win::{
    init_process, looks_black, monitors, CaptureTarget, Device, Error, Frame, MonitorInfo, Screen,
    ScreenSource,
};
use windows::core::w;
use windows::Win32::Foundation::{COLORREF, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11Texture2D, D3D11_BIND_SHADER_RESOURCE, D3D11_BOX, D3D11_CPU_ACCESS_READ,
    D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_READ, D3D11_SUBRESOURCE_DATA, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, CreateSolidBrush, DeleteObject, EndPaint, FillRect, GetStockObject, InvalidateRect,
    HBRUSH, PAINTSTRUCT, WHITE_BRUSH,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, GetWindowLongPtrW, PostMessageW,
    PostQuitMessage, RegisterClassW, SetWindowLongPtrW, ShowWindow, GWLP_USERDATA, MSG,
    SW_SHOWNOACTIVATE, WM_CLOSE, WM_DESTROY, WM_PAINT, WNDCLASSW, WS_EX_NOACTIVATE,
    WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_POPUP,
};

/// Where the region test's window sits, in physical pixels of the primary monitor.
/// Other tests put theirs at 100, 100, so tests running at the same time do not overlap.
const WINDOW_X: i32 = 700;
const WINDOW_Y: i32 = 500;

fn primary() -> MonitorInfo {
    monitors().into_iter().find(|m| m.primary).unwrap()
}

fn wait_until(mut condition: impl FnMut() -> bool, timeout: Duration) {
    let deadline = Instant::now() + timeout;
    while !condition() {
        assert!(Instant::now() < deadline, "Condition not met in time.");
        thread::sleep(Duration::from_millis(20));
    }
}

fn first_frame(src: &mut ScreenSource) -> Frame {
    let mut frame = None;
    wait_until(|| { frame = src.latest_frame(); frame.is_some() }, Duration::from_secs(2));
    frame.unwrap()
}

fn texture_size(t: &ID3D11Texture2D) -> (u32, u32) {
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    unsafe { t.GetDesc(&mut desc) };
    (desc.Width, desc.Height)
}

/// Reads one BGRA pixel back from the GPU.
fn pixel(frame: &Frame, device: &Device, x: u32, y: u32) -> [u8; 4] {
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
    unsafe {
        let mut staging = None;
        device.d3d().CreateTexture2D(&desc, None, Some(&mut staging)).unwrap();
        let staging = staging.unwrap();
        let area = D3D11_BOX { left: x, top: y, front: 0, right: x + 1, bottom: y + 1, back: 1 };
        device.context().CopySubresourceRegion(&staging, 0, 0, 0, 0, &frame.texture, 0, Some(&area));
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        device.context().Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)).unwrap();
        let px = *(mapped.pData as *const [u8; 4]);
        device.context().Unmap(&staging, 0);
        px
    }
}

/// True when every color channel of a BGRA pixel is within 3 of `level`.
fn near(px: [u8; 4], level: u8) -> bool {
    px[..3].iter().all(|c| c.abs_diff(level) <= 3)
}

fn filled(device: &Device, bgra: [u8; 4]) -> Frame {
    let (w, h) = (320, 200);
    let data: Vec<u8> = bgra.iter().copied().cycle().take(w * h * 4).collect();
    let desc = D3D11_TEXTURE2D_DESC {
        Width: w as u32,
        Height: h as u32,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
        ..Default::default()
    };
    let init = D3D11_SUBRESOURCE_DATA { pSysMem: data.as_ptr().cast(), SysMemPitch: (w * 4) as u32, SysMemSlicePitch: 0 };
    let mut texture = None;
    unsafe { device.d3d().CreateTexture2D(&desc, Some(&init), Some(&mut texture)) }.unwrap();
    Frame::new(texture.unwrap(), PixelSize { width: w as i32, height: h as i32 })
}

/// A 200 by 100 window with a gray left half (128 unless changed) and a white right half.
/// It runs its own message loop on its own thread and never takes focus.
struct TestWindow {
    hwnd: isize,
    thread: Option<JoinHandle<()>>,
}

impl TestWindow {
    fn open() -> TestWindow {
        TestWindow::open_at(100, 100)
    }

    fn open_at(x: i32, y: i32) -> TestWindow {
        let (tx, rx) = mpsc::channel();
        let thread = thread::spawn(move || unsafe {
            let class = w!("RecorderWinScreenTest");
            // Fails when another test registered it first, which is fine.
            RegisterClassW(&WNDCLASSW { lpfnWndProc: Some(paint_patches), lpszClassName: class, ..Default::default() });
            let hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                class,
                w!("Screen source test"),
                WS_POPUP,
                x,
                y,
                200,
                100,
                None,
                None,
                None,
                None,
            )
            .unwrap();
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 128);
            let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            tx.send(hwnd.0 as isize).unwrap();
            let mut msg = MSG::default();
            while GetMessageW(&mut msg, None, 0, 0).as_bool() {
                DispatchMessageW(&msg);
            }
        });
        TestWindow { hwnd: rx.recv().unwrap(), thread: Some(thread) }
    }

    /// Repaints the left half in gray `level`.
    fn set_left(&self, level: u8) {
        let hwnd = HWND(self.hwnd as _);
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, level as isize);
            InvalidateRect(Some(hwnd), None, false).unwrap();
        }
    }

    fn close(&mut self) {
        if let Some(t) = self.thread.take() {
            unsafe { PostMessageW(Some(HWND(self.hwnd as _)), WM_CLOSE, WPARAM(0), LPARAM(0)) }.unwrap();
            t.join().unwrap();
        }
    }
}

impl Drop for TestWindow {
    fn drop(&mut self) {
        self.close();
    }
}

extern "system" fn paint_patches(hwnd: HWND, msg: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    unsafe {
        match msg {
            WM_PAINT => {
                let level = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as u32;
                let mut ps = PAINTSTRUCT::default();
                let dc = BeginPaint(hwnd, &mut ps);
                let gray = CreateSolidBrush(COLORREF(level | level << 8 | level << 16));
                FillRect(dc, &RECT { left: 0, top: 0, right: 100, bottom: 100 }, gray);
                let _ = DeleteObject(gray.into());
                FillRect(dc, &RECT { left: 100, top: 0, right: 200, bottom: 100 }, HBRUSH(GetStockObject(WHITE_BRUSH).0));
                let _ = EndPaint(hwnd, &ps);
                LRESULT(0)
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, w, l),
        }
    }
}

#[test]
#[ignore = "needs the desktop"]
fn primary_monitor_gives_a_frame() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let mut src = ScreenSource::create(&CaptureTarget::Monitor(primary().handle), &device).unwrap();
    first_frame(&mut src);
    assert!(src.source_size().width > 0);
}

#[test]
#[ignore = "needs the desktop"]
fn region_is_cropped() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let mut window = TestWindow::open_at(WINDOW_X, WINDOW_Y);
    window.set_left(60);
    // Starts 50 pixels right of and 20 below the window's top-left corner.
    let target = CaptureTarget::Region { monitor: primary().handle, x: WINDOW_X + 50, y: WINDOW_Y + 20, width: 640, height: 360 };
    let mut src = ScreenSource::create(&target, &device).unwrap();
    thread::sleep(Duration::from_millis(500));
    let frame = first_frame(&mut src);
    assert_eq!(PixelSize { width: 640, height: 360 }, src.source_size());
    assert_eq!(PixelSize { width: 640, height: 360 }, frame.size);
    assert_eq!((640, 360), texture_size(&frame.texture));
    // Region pixel (10, 10) is window pixel (60, 30), in the gray half.
    let px = pixel(&frame, &device, 10, 10);
    assert!(near(px, 60), "{px:?}");
    window.close();
}

#[test]
#[ignore = "needs the desktop"]
fn offscreen_region_is_unavailable() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let target = CaptureTarget::Region { monitor: primary().handle, x: 100_000, y: 0, width: 640, height: 360 };
    let result = ScreenSource::create(&target, &device);
    assert!(matches!(result, Err(Error::CaptureUnavailable(_))));
}

#[test]
#[ignore = "needs the desktop"]
fn tiny_region_is_unavailable() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let target = CaptureTarget::Region { monitor: primary().handle, x: 0, y: 0, width: 8, height: 8 };
    let result = ScreenSource::create(&target, &device);
    assert!(matches!(result, Err(Error::CaptureUnavailable(_))));
}

#[test]
#[ignore = "needs the desktop"]
fn region_cropped_to_a_sliver_is_unavailable() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let monitor = primary();
    let target = CaptureTarget::Region { monitor: monitor.handle, x: monitor.width - 1, y: 0, width: 640, height: 360 };
    let result = ScreenSource::create(&target, &device);
    assert!(matches!(result, Err(Error::CaptureUnavailable(_))));
}

#[test]
#[ignore = "needs the desktop"]
fn invalid_window_is_unavailable() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let result = ScreenSource::create(&CaptureTarget::Window(0x1), &device);
    match result {
        Err(Error::CaptureUnavailable(m)) => assert!(m.starts_with("This window cannot be recorded."), "{m}"),
        Err(e) => panic!("wrong error: {e:?}"),
        Ok(_) => panic!("an invalid window was accepted"),
    }
}

#[test]
#[ignore = "needs the desktop"]
fn closing_the_window_closes_the_source() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let mut window = TestWindow::open();
    let src = ScreenSource::create(&CaptureTarget::Window(window.hwnd), &device).unwrap();
    assert!(!src.is_closed());
    window.close();
    wait_until(|| src.is_closed(), Duration::from_secs(2));
}

#[test]
#[ignore = "needs the desktop"]
fn looks_black_detects_a_black_frame() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    assert!(looks_black(&filled(&device, [0, 0, 0, 255]), &device));
    assert!(!looks_black(&filled(&device, [0, 0, 255, 255]), &device));
}

#[test]
#[ignore = "needs the desktop"]
fn window_patches_keep_their_brightness() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let window = TestWindow::open();
    let mut src = ScreenSource::create(&CaptureTarget::Window(window.hwnd), &device).unwrap();
    // The first frames can arrive before the window has painted.
    thread::sleep(Duration::from_millis(500));
    let frame = first_frame(&mut src);
    let gray = pixel(&frame, &device, 50, 50);
    let white = pixel(&frame, &device, 150, 50);
    println!("gray patch reads {gray:?}, white patch reads {white:?} (BGRA)");
    assert!(gray[..3].iter().all(|c| (126..=130).contains(c)), "gray {gray:?}");
    assert!(white[..3].iter().all(|c| *c >= 250), "white {white:?}");
}

#[test]
#[ignore = "needs the desktop"]
fn a_held_frame_is_never_overwritten() {
    init_process().unwrap();
    let device = Device::new().unwrap();
    let window = TestWindow::open();
    window.set_left(40);
    let mut src = ScreenSource::create(&CaptureTarget::Window(window.hwnd), &device).unwrap();
    let mut held = None;
    wait_until(
        || { held = src.latest_frame().filter(|f| near(pixel(f, &device, 50, 50), 40)); held.is_some() },
        Duration::from_secs(2),
    );
    let held = held.unwrap();
    // 6 new frames go through a pool of 3 textures while one is held, so the pool wraps around.
    let levels = [70, 100, 130, 160, 190, 220];
    for level in levels {
        window.set_left(level);
        thread::sleep(Duration::from_millis(50));
        wait_until(
            || src.latest_frame().is_some_and(|f| near(pixel(&f, &device, 50, 50), level)),
            Duration::from_secs(2),
        );
    }
    let px = pixel(&held, &device, 50, 50);
    assert!(near(px, 40), "the held frame changed to {px:?}");
    let latest = src.latest_frame().unwrap();
    assert!(near(pixel(&latest, &device, 50, 50), 220));
}
