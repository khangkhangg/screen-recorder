use std::ffi::OsStr;
use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::thread::sleep;
use std::time::{Duration, Instant, SystemTime};

use recorder_core::frame_math::PixelSize;
use windows::core::HSTRING;
use windows::Win32::Foundation::{GENERIC_READ, GlobalFree, HANDLE, HGLOBAL};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CPU_ACCESS_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_READ, D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING,
};
use windows::Win32::Graphics::Gdi::{BITMAPINFOHEADER, BI_RGB};
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, GUID_ContainerFormatPng, GUID_WICPixelFormat32bppBGRA, IWICImagingFactory,
    WICBitmapEncoderNoCache, WICConvertBitmapSource, WICDecodeMetadataCacheOnDemand,
};
use windows::Win32::System::Com::StructuredStorage::CreateStreamOnHGlobal;
use windows::Win32::System::Com::{
    CoCreateInstance, CLSCTX_INPROC_SERVER, STATFLAG_NONAME, STATSTG, STREAM_SEEK_SET,
};
use windows::Win32::System::DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::System::Ole::CF_DIB;

use crate::error::{win, win_file};
use crate::session::local_stamp;
use crate::{CaptureTarget, Device, Error, Result, Screen, ScreenSource};

pub struct ScreenshotResult {
    pub path: PathBuf,
    pub copied: bool,
    pub width: i32,
    pub height: i32,
}

/// Saves one PNG of the target in `output_root`, and puts the picture on the clipboard when `copy` is true.
pub fn capture(target: &CaptureTarget, output_root: &Path, copy: bool) -> Result<ScreenshotResult> {
    let (pixels, w, h) = grab(target)?;
    let png = encode_png(&pixels, w, h)?;
    fs::create_dir_all(output_root).map_err(Error::Io)?;
    let path = unique_path(output_root, SystemTime::now());
    write_new(&path, &png)?;
    // The saved file is the result, so a clipboard failure must not lose it.
    let copied = copy && set_clipboard(&pixels, w, h).is_ok();
    Ok(ScreenshotResult { path, copied, width: w as i32, height: h as i32 })
}

/// Puts a picture of the target on the clipboard and writes no file.
pub fn copy_to_clipboard(target: &CaptureTarget) -> Result<PixelSize> {
    let (pixels, w, h) = grab(target)?;
    set_clipboard(&pixels, w, h)?;
    Ok(PixelSize { width: w as i32, height: h as i32 })
}

/// Writes a new PNG of the rectangle beside the source, which is only read.
pub fn crop(source: &Path, x: i32, y: i32, width: i32, height: i32) -> Result<PathBuf> {
    let (pixels, w, h) = decode_png(source)?;
    let (x, y, cw, ch) = (i64::from(x), i64::from(y), i64::from(width), i64::from(height));
    if x < 0 || y < 0 || cw < 1 || ch < 1 || x + cw > i64::from(w) || y + ch > i64::from(h) {
        return Err(Error::Invalid("The crop area must be at least 1 by 1 pixel and fully inside the picture.".into()));
    }
    let row = w as usize * 4;
    let mut out = Vec::with_capacity(cw as usize * ch as usize * 4);
    for r in y..y + ch {
        let start = r as usize * row + x as usize * 4;
        out.extend_from_slice(&pixels[start..start + cw as usize * 4]);
    }
    let png = encode_png(&out, cw as u32, ch as u32)?;
    let mut stem = source.with_extension("").into_os_string();
    stem.push(" (cropped)");
    let path = numbered(&stem);
    write_new(&path, &png)?;
    Ok(path)
}

/// `Screenshot yyyy-MM-dd HH-mm-ss.png`, with ` (2)`, ` (3)` before the extension when the name is taken.
pub fn unique_path(folder: &Path, time: SystemTime) -> PathBuf {
    let stamp = local_stamp(time).map_or_else(|_| "unknown time".to_string(), |s| s.0);
    numbered(folder.join(format!("Screenshot {stamp}")).as_os_str())
}

fn numbered(stem: &OsStr) -> PathBuf {
    let with = |tail: String| {
        let mut s = stem.to_os_string();
        s.push(tail);
        PathBuf::from(s)
    };
    let mut path = with(".png".into());
    for n in 2.. {
        if !path.exists() {
            break;
        }
        path = with(format!(" ({n}).png"));
    }
    path
}

fn write_new(path: &Path, bytes: &[u8]) -> Result<()> {
    let mut file = File::create_new(path).map_err(Error::Io)?;
    if let Err(e) = file.write_all(bytes) {
        drop(file);
        let _ = fs::remove_file(path); // a partial picture is worse than none
        return Err(Error::Io(e));
    }
    Ok(())
}

/// The first frame of the target as BGRA rows, with its width and height.
fn grab(target: &CaptureTarget) -> Result<(Vec<u8>, u32, u32)> {
    let device = Device::new()?;
    let mut source = ScreenSource::create(target, &device)?;
    let deadline = Instant::now() + Duration::from_secs(2);
    let frame = loop {
        if let Some(f) = source.latest_frame() {
            break f;
        }
        if Instant::now() > deadline {
            return Err(Error::CaptureUnavailable("No frame arrived within 2 seconds.".into()));
        }
        sleep(Duration::from_millis(20));
    };
    let fail = |e| win(e, Error::Device, "The screen picture could not be read.");
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    unsafe { frame.texture.GetDesc(&mut desc) };
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    desc.MiscFlags = 0;
    let mut staging = None;
    unsafe { device.d3d().CreateTexture2D(&desc, None, Some(&mut staging)) }.map_err(fail)?;
    let staging = staging.ok_or_else(|| fail(windows::core::Error::empty()))?;
    let context = device.context();
    let (w, h) = (desc.Width as usize, desc.Height as usize);
    let mut pixels = Vec::with_capacity(w * h * 4);
    unsafe {
        context.CopyResource(&staging, &frame.texture);
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        context.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)).map_err(fail)?;
        for r in 0..h {
            let row = (mapped.pData as *const u8).add(r * mapped.RowPitch as usize);
            pixels.extend_from_slice(std::slice::from_raw_parts(row, w * 4));
        }
        context.Unmap(&staging, 0);
    }
    Ok((pixels, desc.Width, desc.Height))
}

fn factory() -> windows::core::Result<IWICImagingFactory> {
    unsafe { CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER) }
}

/// Encodes in memory, so a failed encode never leaves a file behind.
fn encode_png(bgra: &[u8], w: u32, h: u32) -> Result<Vec<u8>> {
    let fail = |e| win(e, Error::Encoder, "The picture could not be encoded.");
    let run = || -> windows::core::Result<Vec<u8>> {
        unsafe {
            let stream = CreateStreamOnHGlobal(HGLOBAL::default(), true)?;
            let encoder = factory()?.CreateEncoder(&GUID_ContainerFormatPng, std::ptr::null())?;
            encoder.Initialize(&stream, WICBitmapEncoderNoCache)?;
            let mut frame = None;
            encoder.CreateNewFrame(&mut frame, std::ptr::null_mut())?;
            let frame = frame.ok_or_else(windows::core::Error::empty)?;
            frame.Initialize(None)?;
            frame.SetSize(w, h)?;
            let mut format = GUID_WICPixelFormat32bppBGRA;
            frame.SetPixelFormat(&mut format)?;
            frame.WritePixels(h, w * 4, bgra)?;
            frame.Commit()?;
            encoder.Commit()?;
            let mut stat = STATSTG::default();
            stream.Stat(&mut stat, STATFLAG_NONAME)?;
            stream.Seek(0, STREAM_SEEK_SET, None)?;
            let mut out = vec![0u8; stat.cbSize as usize];
            stream.Read(out.as_mut_ptr().cast(), out.len() as u32, None).ok()?;
            Ok(out)
        }
    };
    run().map_err(fail)
}

fn decode_png(path: &Path) -> Result<(Vec<u8>, u32, u32)> {
    let fail = |e| win_file(e, Error::Invalid, "The picture could not be read.");
    let run = || -> windows::core::Result<(Vec<u8>, u32, u32)> {
        unsafe {
            let decoder = factory()?.CreateDecoderFromFilename(
                &HSTRING::from(path.as_os_str()),
                None,
                GENERIC_READ,
                WICDecodeMetadataCacheOnDemand,
            )?;
            let bgra = WICConvertBitmapSource(&GUID_WICPixelFormat32bppBGRA, &decoder.GetFrame(0)?)?;
            let (mut w, mut h) = (0, 0);
            bgra.GetSize(&mut w, &mut h)?;
            let mut out = vec![0u8; w as usize * h as usize * 4];
            bgra.CopyPixels(std::ptr::null(), w * 4, &mut out)?;
            Ok((out, w, h))
        }
    };
    run().map_err(fail)
}

struct Clipboard;

impl Clipboard {
    /// Another program may hold the clipboard for a moment.
    fn open() -> windows::core::Result<Clipboard> {
        let mut tries = 1;
        loop {
            match unsafe { OpenClipboard(None) } {
                Ok(()) => return Ok(Clipboard),
                Err(e) if tries == 10 => return Err(e),
                Err(_) => {
                    tries += 1;
                    sleep(Duration::from_millis(20));
                }
            }
        }
    }
}

impl Drop for Clipboard {
    fn drop(&mut self) {
        let _ = unsafe { CloseClipboard() };
    }
}

/// A device-independent bitmap: header, then bottom-up 32-bit rows.
fn set_clipboard(bgra: &[u8], w: u32, h: u32) -> Result<()> {
    let fail = |e| win(e, Error::Device, "The clipboard could not be used.");
    let header = BITMAPINFOHEADER {
        biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
        biWidth: w as i32,
        biHeight: h as i32,
        biPlanes: 1,
        biBitCount: 32,
        biCompression: BI_RGB.0,
        biSizeImage: w * h * 4,
        ..Default::default()
    };
    let mut dib = unsafe {
        std::slice::from_raw_parts(&header as *const _ as *const u8, std::mem::size_of::<BITMAPINFOHEADER>()).to_vec()
    };
    for row in bgra.chunks(w as usize * 4).rev() {
        dib.extend_from_slice(row);
    }
    let _clipboard = Clipboard::open().map_err(fail)?;
    unsafe {
        EmptyClipboard().map_err(fail)?;
        let memory = GlobalAlloc(GMEM_MOVEABLE, dib.len()).map_err(fail)?;
        let ptr = GlobalLock(memory);
        if ptr.is_null() {
            let _ = GlobalFree(Some(memory));
            return Err(fail(windows::core::Error::from_thread()));
        }
        std::ptr::copy_nonoverlapping(dib.as_ptr(), ptr.cast(), dib.len());
        let _ = GlobalUnlock(memory);
        // Once this succeeds Windows owns the memory, so it is freed only on failure.
        if let Err(e) = SetClipboardData(CF_DIB.0 as u32, Some(HANDLE(memory.0))) {
            let _ = GlobalFree(Some(memory));
            return Err(fail(e));
        }
    }
    Ok(())
}
