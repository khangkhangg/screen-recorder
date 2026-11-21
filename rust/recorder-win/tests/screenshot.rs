use std::fs;
use std::path::{Path, PathBuf};
use std::ptr::null;

use recorder_win::screenshot::{capture, copy_to_clipboard, crop};
use recorder_win::{init_process, monitors, CaptureTarget, Error};
use windows::core::{w, HSTRING};
use windows::Win32::UI::WindowsAndMessaging::{CreateWindowExW, DestroyWindow, HWND_MESSAGE};
use windows::Win32::Foundation::{GENERIC_READ, HANDLE, HGLOBAL};
use windows::Win32::Graphics::Gdi::BITMAPINFOHEADER;
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, GUID_ContainerFormatPng, GUID_WICPixelFormat32bppBGRA, IWICImagingFactory,
    WICBitmapEncoderNoCache, WICConvertBitmapSource, WICDecodeMetadataCacheOnDemand,
};
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};
use windows::Win32::System::Com::StructuredStorage::CreateStreamOnHGlobal;
use windows::Win32::System::DataExchange::{CloseClipboard, GetClipboardData, OpenClipboard};
use windows::Win32::System::Memory::{GlobalLock, GlobalUnlock};
use windows::Win32::System::Ole::CF_DIB;

struct Temp(PathBuf);

impl std::ops::Deref for Temp {
    type Target = PathBuf;
    fn deref(&self) -> &PathBuf {
        &self.0
    }
}

impl Drop for Temp {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn temp_folder(name: &str) -> Temp {
    let p = std::env::temp_dir().join(format!("recorder-win-screenshot-{name}-{}", std::process::id()));
    let _ = fs::remove_dir_all(&p);
    fs::create_dir_all(&p).unwrap();
    Temp(p)
}

fn region(w: i32, h: i32) -> CaptureTarget {
    CaptureTarget::Region { monitor: monitors().into_iter().find(|m| m.primary).unwrap().handle, x: 0, y: 0, width: w, height: h }
}

fn factory() -> IWICImagingFactory {
    unsafe { CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER).unwrap() }
}

fn write_test_png(path: &Path, w: u32, h: u32) {
    let pixels: Vec<u8> = (0..w * h * 4).map(|i| (i % 251) as u8).collect();
    unsafe {
        let stream = CreateStreamOnHGlobal(HGLOBAL::default(), true).unwrap();
        let encoder = factory().CreateEncoder(&GUID_ContainerFormatPng, null()).unwrap();
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
        let mut stat = Default::default();
        stream.Stat(&mut stat, windows::Win32::System::Com::STATFLAG_NONAME).unwrap();
        stream.Seek(0, windows::Win32::System::Com::STREAM_SEEK_SET, None).unwrap();
        let mut bytes = vec![0u8; stat.cbSize as usize];
        stream.Read(bytes.as_mut_ptr().cast(), bytes.len() as u32, None).ok().unwrap();
        fs::write(path, bytes).unwrap();
    }
}

fn png_pixels(path: &Path) -> (u32, u32, Vec<u8>) {
    unsafe {
        let decoder = factory()
            .CreateDecoderFromFilename(&HSTRING::from(path.as_os_str()), None, GENERIC_READ, WICDecodeMetadataCacheOnDemand)
            .unwrap();
        let frame = decoder.GetFrame(0).unwrap();
        let (mut w, mut h) = (0, 0);
        frame.GetSize(&mut w, &mut h).unwrap();
        let mut pixels = vec![0u8; (w * h * 4) as usize];
        WICConvertBitmapSource(&GUID_WICPixelFormat32bppBGRA, &frame).unwrap().CopyPixels(null(), w * 4, &mut pixels).unwrap();
        (w, h, pixels)
    }
}

fn png_size(path: &Path) -> (u32, u32) {
    let (w, h, _) = png_pixels(path);
    (w, h)
}

/// Width and height of the bitmap on the clipboard.
fn clipboard_size() -> (i32, i32, u16) {
    unsafe {
        OpenClipboard(None).unwrap();
        let data: HANDLE = GetClipboardData(CF_DIB.0 as u32).unwrap();
        let h = HGLOBAL(data.0);
        let header = GlobalLock(h) as *const BITMAPINFOHEADER;
        let out = ((*header).biWidth, (*header).biHeight, (*header).biBitCount);
        let _ = GlobalUnlock(h);
        CloseClipboard().unwrap();
        out
    }
}

#[test]
fn crop_writes_a_new_png_and_numbers_repeats() {
    init_process().unwrap();
    let dir = temp_folder("crop");
    let source = dir.join("test.png");
    write_test_png(&source, 100, 80);
    let before = fs::read(&source).unwrap();

    let first = crop(&source, 10, 10, 50, 40).unwrap();
    assert_eq!(dir.join("test (cropped).png"), first);
    assert_eq!((50, 40), png_size(&first));
    // Corners of the crop are the source pixels at (10, 10) and (59, 49).
    let (_, _, src) = png_pixels(&source);
    let (_, _, out) = png_pixels(&first);
    let src_px = |x: usize, y: usize| src[(y * 100 + x) * 4..][..4].to_vec();
    assert_eq!(src_px(10, 10), out[..4]);
    assert_eq!(src_px(59, 49), out[(39 * 50 + 49) * 4..][..4]);
    assert_eq!(before, fs::read(&source).unwrap());

    let second = crop(&source, 10, 10, 50, 40).unwrap();
    assert_eq!(dir.join("test (cropped) (2).png"), second);
}

#[test]
fn crop_outside_the_picture_is_refused_and_writes_nothing() {
    init_process().unwrap();
    let dir = temp_folder("badcrop");
    let source = dir.join("test.png");
    write_test_png(&source, 100, 80);
    for (x, y, w, h) in [(-1, 0, 10, 10), (0, -1, 10, 10), (0, 0, 0, 10), (0, 0, 10, 0), (60, 0, 50, 10), (0, 50, 10, 40), (i32::MAX, 0, 10, 10), (10, 10, i32::MAX, 10), (0, 0, 100, 81), (50, 0, 51, 80)] {
        assert!(matches!(crop(&source, x, y, w, h), Err(Error::Invalid(_))), "{x} {y} {w} {h}");
    }
    assert_eq!(1, fs::read_dir(&*dir).unwrap().count());
}

#[test]
#[ignore = "needs the desktop"]
fn region_screenshot_has_the_region_size() {
    init_process().unwrap();
    let dir = temp_folder("region");
    let shot = capture(&region(320, 200), &dir, false).unwrap();
    assert!(!shot.copied);
    assert_eq!((320, 200), (shot.width, shot.height));
    assert_eq!((320, 200), png_size(&shot.path));
}

#[test]
#[ignore = "needs the desktop"]
fn missing_folder_is_created() {
    init_process().unwrap();
    let dir = temp_folder("missing");
    let shot = capture(&region(320, 200), &dir.join("new").join("sub"), false).unwrap();
    assert!(shot.path.exists());
}

#[test]
#[ignore = "needs the desktop and replaces the clipboard contents"]
fn clipboard_gets_a_bitmap_of_the_region_size() {
    init_process().unwrap();
    let dir = temp_folder("clip");
    let shot = capture(&region(320, 200), &dir, true).unwrap();
    assert!(shot.copied);
    assert_eq!((320, 200, 32), clipboard_size());

    let size = copy_to_clipboard(&region(200, 100)).unwrap();
    assert_eq!((200, 100), (size.width, size.height));
    assert_eq!((200, 100, 32), clipboard_size());
    assert_eq!(1, fs::read_dir(&*dir).unwrap().count());
}

#[test]
#[ignore = "needs the desktop"]
fn busy_clipboard_still_saves_the_file() {
    init_process().unwrap();
    let dir = temp_folder("busy");
    // The clipboard belongs to the thread that opened it, so another thread has to hold it.
    let (opened, opened_rx) = std::sync::mpsc::channel();
    let (release, release_rx) = std::sync::mpsc::channel::<()>();
    let holder = std::thread::spawn(move || unsafe {
        // A clipboard opened without a window can be opened again, so this one needs a window.
        let window = CreateWindowExW(Default::default(), w!("STATIC"), None, Default::default(), 0, 0, 0, 0, Some(HWND_MESSAGE), None, None, None).unwrap();
        OpenClipboard(Some(window)).unwrap();
        opened.send(()).unwrap();
        release_rx.recv().unwrap();
        CloseClipboard().unwrap();
        DestroyWindow(window).unwrap();
    });
    opened_rx.recv().unwrap();
    let shot = capture(&region(320, 200), &dir, true);
    release.send(()).unwrap();
    holder.join().unwrap();
    let shot = shot.unwrap();
    assert!(!shot.copied);
    assert!(shot.path.exists());
}

#[test]
fn crop_ending_exactly_at_the_edge_is_accepted() {
    init_process().unwrap();
    let dir = temp_folder("edge");
    let source = dir.join("test.png");
    write_test_png(&source, 100, 80);
    assert_eq!((50, 80), png_size(&crop(&source, 50, 0, 50, 80).unwrap()));
}
