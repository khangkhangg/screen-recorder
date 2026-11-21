use std::ffi::OsStr;
use std::fs::{self, File};
use std::io::{self, Write};
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
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER, STATFLAG_NONAME, STATSTG, STREAM_SEEK_SET};
use windows::Win32::System::DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::System::Ole::CF_DIB;

use crate::error::{win, win_file};
use crate::session::local_stamp;
use crate::{CaptureTarget, Device, Error, Frame, Result, Screen, ScreenSource};

pub struct ScreenshotResult {
    pub path: PathBuf,
    pub copied: bool,
    pub width: i32,
    pub height: i32,
}

/// Saves one PNG of the target in `output_root`, and puts the picture on the clipboard when `copy` is true.
pub fn capture(target: &CaptureTarget, output_root: &Path, copy: bool) -> Result<ScreenshotResult> {
    let (device, frame) = first_frame(target)?;
    save_frame(&device, &frame, output_root, copy)
}

/// Puts a picture of the target on the clipboard and writes no file.
pub fn copy_to_clipboard(target: &CaptureTarget) -> Result<PixelSize> {
    let (device, frame) = first_frame(target)?;
    let (pixels, w, h) = read_frame(&device, &frame)?;
    set_clipboard(&pixels, w, h)?;
    Ok(PixelSize { width: w as i32, height: h as i32 })
}

/// Writes a new PNG of the rectangle beside the source, which is only read.
pub fn crop(source: &Path, x: i32, y: i32, width: i32, height: i32) -> Result<PathBuf> {
    let (pixels, w, h) = decode_png(source)?;
    let (x, y, cw, ch) = (i64::from(x), i64::from(y), i64::from(width), i64::from(height));
    if x < 0 || y < 0 || cw < 1 || ch < 1 || x > i64::from(w) - cw || y > i64::from(h) - ch {
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
    save_new(|| numbered(&stem), &png)
}

/// `Screenshot yyyy-MM-dd HH-mm-ss.png`, with ` (2)`, ` (3)` before the extension when the name is taken.
pub fn unique_path(folder: &Path, time: SystemTime) -> PathBuf {
    let stamp = local_stamp(time).map_or_else(|_| "unknown time".to_string(), |s| s.0);
    numbered(folder.join(format!("Screenshot {stamp}")).as_os_str())
}

fn save_frame(device: &Device, frame: &Frame, output_root: &Path, copy: bool) -> Result<ScreenshotResult> {
    let (pixels, w, h) = read_frame(device, frame)?;
    let png = encode_png(&pixels, w, h)?;
    fs::create_dir_all(output_root).map_err(Error::Io)?;
    let path = save_new(|| unique_path(output_root, SystemTime::now()), &png)?;
    // The saved file is the result, so a clipboard failure must not lose it.
    let copied = copy && set_clipboard(&pixels, w, h).is_ok();
    Ok(ScreenshotResult { path, copied, width: w as i32, height: h as i32 })
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

fn save_new(next: impl FnMut() -> PathBuf, bytes: &[u8]) -> Result<PathBuf> {
    save_new_with(next, |f| f.write_all(bytes))
}

/// Never overwrites. Another program may take a name between the check and the create, so a taken name asks `next` again.
fn save_new_with(mut next: impl FnMut() -> PathBuf, write: impl Fn(&mut File) -> io::Result<()>) -> Result<PathBuf> {
    for _ in 0..100 {
        let path = next();
        let mut file = match File::create_new(&path) {
            Ok(f) => f,
            Err(e) if e.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(Error::Io(e)),
        };
        let written = write(&mut file);
        drop(file);
        if let Err(e) = written {
            let _ = fs::remove_file(&path); // a partial picture is worse than none
            return Err(Error::Io(e));
        }
        return Ok(path);
    }
    Err(Error::Invalid("A free file name for the picture could not be found.".into()))
}

/// Bytes of a BGRA picture, refusing sizes that would overflow or ask for a huge buffer.
fn pixel_bytes(w: u32, h: u32) -> Result<usize> {
    if w > 32_768 || h > 32_768 || u64::from(w) * u64::from(h) > 1 << 28 {
        return Err(Error::Invalid("The picture is too large to handle.".into()));
    }
    Ok(w as usize * h as usize * 4)
}

fn first_frame(target: &CaptureTarget) -> Result<(Device, Frame)> {
    let device = Device::new()?;
    let mut source = ScreenSource::create(target, &device)?;
    let deadline = Instant::now() + Duration::from_secs(2);
    loop {
        if let Some(frame) = source.latest_frame() {
            return Ok((device, frame));
        }
        if Instant::now() > deadline {
            return Err(Error::CaptureUnavailable("No frame arrived within 2 seconds.".into()));
        }
        sleep(Duration::from_millis(20));
    }
}

/// The frame as opaque BGRA rows, with its width and height.
fn read_frame(device: &Device, frame: &Frame) -> Result<(Vec<u8>, u32, u32)> {
    let fail = |e| win(e, Error::Device, "The screen picture could not be read.");
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    unsafe { frame.texture.GetDesc(&mut desc) };
    let bytes = pixel_bytes(desc.Width, desc.Height)?;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    desc.MiscFlags = 0;
    let mut staging = None;
    unsafe { device.d3d().CreateTexture2D(&desc, None, Some(&mut staging)) }.map_err(fail)?;
    let staging = staging.ok_or_else(|| fail(windows::core::Error::empty()))?;
    let context = device.context();
    unsafe { context.CopyResource(&staging, &frame.texture) };
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    unsafe { context.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)) }.map_err(fail)?;
    let row_bytes = desc.Width as usize * 4;
    let mut pixels = Vec::with_capacity(bytes);
    for r in 0..desc.Height as usize {
        // SAFETY: the mapping holds `Height` rows that start `RowPitch` bytes apart and are at least `row_bytes` long.
        let row = unsafe {
            std::slice::from_raw_parts((mapped.pData as *const u8).add(r * mapped.RowPitch as usize), row_bytes)
        };
        pixels.extend_from_slice(row);
    }
    unsafe { context.Unmap(&staging, 0) };
    // Windows draws some captures with alpha 0, which would save as a see-through picture.
    for px in pixels.as_chunks_mut::<4>().0 {
        px[3] = 255;
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
        let stream = unsafe { CreateStreamOnHGlobal(HGLOBAL::default(), true) }?;
        let encoder = unsafe { factory()?.CreateEncoder(&GUID_ContainerFormatPng, std::ptr::null()) }?;
        unsafe { encoder.Initialize(&stream, WICBitmapEncoderNoCache) }?;
        let mut frame = None;
        unsafe { encoder.CreateNewFrame(&mut frame, std::ptr::null_mut()) }?;
        let frame = frame.ok_or_else(windows::core::Error::empty)?;
        unsafe { frame.Initialize(None) }?;
        unsafe { frame.SetSize(w, h) }?;
        let mut format = GUID_WICPixelFormat32bppBGRA;
        unsafe { frame.SetPixelFormat(&mut format) }?;
        unsafe { frame.WritePixels(h, w * 4, bgra) }?;
        unsafe { frame.Commit() }?;
        unsafe { encoder.Commit() }?;
        let mut stat = STATSTG::default();
        unsafe { stream.Stat(&mut stat, STATFLAG_NONAME) }?;
        unsafe { stream.Seek(0, STREAM_SEEK_SET, None) }?;
        let mut out = vec![0u8; stat.cbSize as usize];
        unsafe { stream.Read(out.as_mut_ptr().cast(), out.len() as u32, None) }.ok()?;
        Ok(out)
    };
    run().map_err(fail)
}

fn decode_png(path: &Path) -> Result<(Vec<u8>, u32, u32)> {
    let fail = |e| win_file(e, Error::Invalid, "The picture could not be read.");
    let open = || -> windows::core::Result<_> {
        let name = HSTRING::from(path.as_os_str());
        let decoder = unsafe {
            factory()?.CreateDecoderFromFilename(&name, None, GENERIC_READ, WICDecodeMetadataCacheOnDemand)
        }?;
        let frame = unsafe { decoder.GetFrame(0) }?;
        let bgra = unsafe { WICConvertBitmapSource(&GUID_WICPixelFormat32bppBGRA, &frame) }?;
        let (mut w, mut h) = (0, 0);
        unsafe { bgra.GetSize(&mut w, &mut h) }?;
        Ok((bgra, w, h))
    };
    let (bgra, w, h) = open().map_err(fail)?;
    let mut out = vec![0u8; pixel_bytes(w, h)?];
    unsafe { bgra.CopyPixels(std::ptr::null(), w * 4, &mut out) }.map_err(fail)?;
    Ok((out, w, h))
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

/// A device-independent bitmap in global memory: header, then bottom-up 32-bit rows.
fn dib_memory(bgra: &[u8], w: u32, h: u32) -> Result<HGLOBAL> {
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
    let head = std::mem::size_of::<BITMAPINFOHEADER>();
    let row = w as usize * 4;
    let memory = unsafe { GlobalAlloc(GMEM_MOVEABLE, head + pixel_bytes(w, h)?) }.map_err(fail)?;
    let ptr = unsafe { GlobalLock(memory) } as *mut u8;
    if ptr.is_null() {
        let _ = unsafe { GlobalFree(Some(memory)) };
        return Err(fail(windows::core::Error::from_thread()));
    }
    // SAFETY: the block is `head` plus `h` rows of `row` bytes, and `bgra` holds exactly `h` rows of `row` bytes.
    unsafe {
        std::ptr::copy_nonoverlapping(&header as *const _ as *const u8, ptr, head);
        for (i, src) in bgra.chunks_exact(row).rev().enumerate() {
            std::ptr::copy_nonoverlapping(src.as_ptr(), ptr.add(head + i * row), row);
        }
        let _ = GlobalUnlock(memory);
    }
    Ok(memory)
}

/// The memory is made first, so a failure there leaves the user's clipboard alone.
fn set_clipboard(bgra: &[u8], w: u32, h: u32) -> Result<()> {
    let memory = dib_memory(bgra, w, h)?;
    let put = || -> windows::core::Result<()> {
        let _clipboard = Clipboard::open()?;
        unsafe { EmptyClipboard() }?;
        unsafe { SetClipboardData(CF_DIB.0 as u32, Some(HANDLE(memory.0))) }?;
        Ok(())
    };
    // Once SetClipboardData succeeds Windows owns the memory, so it is freed only on failure.
    put().map_err(|e| {
        let _ = unsafe { GlobalFree(Some(memory)) };
        win(e, Error::Device, "The clipboard could not be used.")
    })
}

#[cfg(test)]
mod tests {
    use std::cell::Cell;

    use windows::Win32::Graphics::Direct3D11::{
        D3D11_BIND_SHADER_RESOURCE, D3D11_SUBRESOURCE_DATA, D3D11_USAGE_DEFAULT,
    };
    use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
    use windows::Win32::System::DataExchange::GetClipboardData;

    use super::*;

    struct Temp(PathBuf);

    impl Temp {
        fn new(name: &str) -> Temp {
            let p = std::env::temp_dir().join(format!("recorder-win-shot-unit-{name}-{}", std::process::id()));
            let _ = fs::remove_dir_all(&p);
            fs::create_dir_all(&p).unwrap();
            Temp(p)
        }
    }

    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn at(secs: u64) -> SystemTime {
        SystemTime::UNIX_EPOCH + Duration::from_secs(secs)
    }

    #[test]
    fn same_second_names_get_a_counter() {
        let dir = Temp::new("names");
        let t = at(1_800_000_000);
        let stamp = local_stamp(t).unwrap().0;
        let first = unique_path(&dir.0, t);
        assert_eq!(dir.0.join(format!("Screenshot {stamp}.png")), first);
        fs::write(&first, "").unwrap();
        let second = unique_path(&dir.0, t);
        assert_eq!(dir.0.join(format!("Screenshot {stamp} (2).png")), second);
        fs::write(&second, "").unwrap();
        assert_eq!(dir.0.join(format!("Screenshot {stamp} (3).png")), unique_path(&dir.0, t));
    }

    #[test]
    fn a_name_taken_after_the_check_moves_to_the_next_counter() {
        let dir = Temp::new("race");
        let taken = dir.0.join("a.png");
        fs::write(&taken, "other").unwrap();
        let free = dir.0.join("a (2).png");
        let calls = Cell::new(0);
        let next = || {
            calls.set(calls.get() + 1);
            if calls.get() == 1 { taken.clone() } else { free.clone() }
        };
        assert_eq!(free, save_new(next, b"mine").unwrap());
        assert_eq!(b"other".to_vec(), fs::read(&taken).unwrap());
        assert_eq!(b"mine".to_vec(), fs::read(&free).unwrap());
    }

    #[test]
    fn a_failed_write_leaves_no_file() {
        let dir = Temp::new("partial");
        let path = dir.0.join("a.png");
        let r = save_new_with(|| path.clone(), |f| {
            f.write_all(b"half")?;
            Err(io::Error::other("disk full"))
        });
        assert!(matches!(r, Err(Error::Io(_))));
        assert!(!path.exists());
    }

    #[test]
    fn huge_sizes_are_refused() {
        assert_eq!(32_000, pixel_bytes(100, 80).unwrap());
        assert_eq!(32_768 * 8192 * 4, pixel_bytes(32_768, 8192).unwrap());
        for (w, h) in [(32_769, 1), (1, 32_769), (16_385, 16_384), (u32::MAX, u32::MAX)] {
            assert!(matches!(pixel_bytes(w, h), Err(Error::Invalid(_))), "{w} {h}");
        }
    }

    fn texture_frame(device: &Device, bgra: [u8; 4]) -> Frame {
        let data: Vec<u8> = bgra.iter().copied().cycle().take(16 * 16 * 4).collect();
        let desc = D3D11_TEXTURE2D_DESC {
            Width: 16,
            Height: 16,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: D3D11_BIND_SHADER_RESOURCE.0 as u32,
            ..Default::default()
        };
        let init = D3D11_SUBRESOURCE_DATA { pSysMem: data.as_ptr().cast(), SysMemPitch: 64, SysMemSlicePitch: 0 };
        let mut texture = None;
        unsafe { device.d3d().CreateTexture2D(&desc, Some(&init), Some(&mut texture)) }.unwrap();
        Frame::new(texture.unwrap(), PixelSize { width: 16, height: 16 })
    }

    #[test]
    #[ignore = "needs the graphics device"]
    fn transparent_pixels_are_saved_opaque() {
        crate::init_process().unwrap();
        let dir = Temp::new("alpha");
        let device = Device::new().unwrap();
        let frame = texture_frame(&device, [10, 20, 30, 0]);
        let shot = save_frame(&device, &frame, &dir.0, false).unwrap();
        let (pixels, w, h) = decode_png(&shot.path).unwrap();
        assert_eq!((16, 16), (w, h));
        assert!(pixels.chunks(4).all(|p| p == [10, 20, 30, 255]));
    }

    #[test]
    #[ignore = "replaces the clipboard contents"]
    fn clipboard_bitmap_has_the_right_row_and_color_order() {
        // 4 wide, 2 high: pixel i is [i, i + 1, i + 2, 255].
        let pixels: Vec<u8> = (0..8u8).flat_map(|i| [i, i + 1, i + 2, 255]).collect();
        set_clipboard(&pixels, 4, 2).unwrap();
        let _clipboard = Clipboard::open().unwrap();
        let handle = unsafe { GetClipboardData(CF_DIB.0 as u32) }.unwrap();
        let memory = HGLOBAL(handle.0);
        let dib = unsafe { std::slice::from_raw_parts(GlobalLock(memory) as *const u8, 40 + 32) };
        // Rows are bottom-up: the picture's top-left pixel is the first of the last row, its bottom-right the last of the first row.
        assert_eq!(&pixels[0..4], &dib[40 + 16..40 + 20]);
        assert_eq!(&pixels[28..32], &dib[40 + 12..40 + 16]);
        let _ = unsafe { GlobalUnlock(memory) };
    }
}
