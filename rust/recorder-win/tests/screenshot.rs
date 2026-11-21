use std::fs;
use std::path::{Path, PathBuf};
use std::ptr::null;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use recorder_win::screenshot::{capture, copy_to_clipboard, crop, unique_path};
use recorder_win::{init_process, monitors, CaptureTarget, Error};
use windows::core::{w, HSTRING};
use windows::Win32::UI::WindowsAndMessaging::{CreateWindowExW, DestroyWindow, HWND_MESSAGE};
use windows::Win32::Foundation::{GENERIC_READ, HANDLE, HGLOBAL, HWND};
use windows::Win32::Graphics::Gdi::BITMAPINFOHEADER;
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, GUID_ContainerFormatPng, GUID_WICPixelFormat32bppBGRA, IWICImagingFactory,
    WICBitmapEncoderNoCache, WICDecodeMetadataCacheOnDemand,
};
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};
use windows::Win32::System::Com::StructuredStorage::CreateStreamOnHGlobal;
use windows::Win32::System::DataExchange::{CloseClipboard, GetClipboardData, OpenClipboard};
use windows::Win32::System::Memory::{GlobalLock, GlobalUnlock};
use windows::Win32::System::Ole::CF_DIB;

fn temp_folder(name: &str) -> PathBuf {
    let p = std::env::temp_dir().join(format!("recorder-win-screenshot-{name}-{}", std::process::id()));
    let _ = fs::remove_dir_all(&p);
    fs::create_dir_all(&p).unwrap();
    p
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

fn png_size(path: &Path) -> (u32, u32) {
    unsafe {
        let decoder = factory()
            .CreateDecoderFromFilename(&HSTRING::from(path.as_os_str()), None, GENERIC_READ, WICDecodeMetadataCacheOnDemand)
            .unwrap();
        let (mut w, mut h) = (0, 0);
        decoder.GetFrame(0).unwrap().GetSize(&mut w, &mut h).unwrap();
        (w, h)
    }
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

fn at(secs: u64) -> SystemTime {
    UNIX_EPOCH + Duration::from_secs(secs)
}

#[test]
fn same_second_names_get_a_counter() {
    let dir = temp_folder("names");
    let t = at(1_800_000_000);
    let first = unique_path(&dir, t);
    let name = first.file_name().unwrap().to_str().unwrap();
    assert!(name.starts_with("Screenshot 20") && name.ends_with(".png") && name.len() == "Screenshot 2026-11-20 10-30-00.png".len(), "{name}");
    fs::write(&first, "").unwrap();
    let second = unique_path(&dir, t);
    assert!(second.to_str().unwrap().ends_with(" (2).png"), "{second:?}");
    fs::write(&second, "").unwrap();
    assert!(unique_path(&dir, t).to_str().unwrap().ends_with(" (3).png"));
    fs::remove_dir_all(&dir).unwrap();
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
    assert_eq!(before, fs::read(&source).unwrap());

    let second = crop(&source, 10, 10, 50, 40).unwrap();
    assert_eq!(dir.join("test (cropped) (2).png"), second);
    fs::remove_dir_all(&dir).unwrap();
}

#[test]
fn crop_outside_the_picture_is_refused_and_writes_nothing() {
    init_process().unwrap();
    let dir = temp_folder("badcrop");
    let source = dir.join("test.png");
    write_test_png(&source, 100, 80);
    for (x, y, w, h) in [(-1, 0, 10, 10), (0, 0, 0, 10), (60, 0, 50, 10), (0, 50, 10, 40)] {
        assert!(matches!(crop(&source, x, y, w, h), Err(Error::Invalid(_))), "{x} {y} {w} {h}");
    }
    assert_eq!(1, fs::read_dir(&dir).unwrap().count());
    fs::remove_dir_all(&dir).unwrap();
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
    fs::remove_dir_all(&dir).unwrap();
}

#[test]
#[ignore = "needs the desktop"]
fn missing_folder_is_created() {
    init_process().unwrap();
    let dir = temp_folder("missing");
    let shot = capture(&region(320, 200), &dir.join("new").join("sub"), false).unwrap();
    assert!(shot.path.exists());
    fs::remove_dir_all(&dir).unwrap();
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
    assert_eq!(1, fs::read_dir(&dir).unwrap().count());
    fs::remove_dir_all(&dir).unwrap();
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
    fs::remove_dir_all(&dir).unwrap();
}
