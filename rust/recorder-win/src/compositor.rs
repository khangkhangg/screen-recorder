use std::path::Path;

use recorder_core::frame_math::{letterbox, PixelSize, RectF};
use recorder_core::layout::{camera_rect, logo_rect, CameraShape, Layout};
use windows::core::{Interface, HSTRING};
use windows::Win32::Foundation::GENERIC_READ;
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_COLOR_F, D2D1_PIXEL_FORMAT, D2D_RECT_F,
};
use windows::Win32::Graphics::Direct2D::{
    ID2D1Bitmap1, ID2D1DeviceContext, ID2D1Image, ID2D1Multithread, D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
    D2D1_BITMAP_OPTIONS_NONE, D2D1_BITMAP_OPTIONS_TARGET, D2D1_BITMAP_PROPERTIES1, D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
    D2D1_INTERPOLATION_MODE_LINEAR, D2D1_ROUNDED_RECT,
};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11Texture2D, D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};
use windows::Win32::Graphics::Dxgi::IDXGISurface;
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory, GUID_WICPixelFormat32bppPBGRA, IWICImagingFactory, WICConvertBitmapSource,
    WICDecodeMetadataCacheOnDemand,
};
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};

use crate::audio::with_com;
use crate::error::{win, Error, Result};
use crate::gfx::Device;
use crate::screen::{bitmap_properties, Frame};

const MAX_LOGO_BYTES: u64 = 20 * 1024 * 1024;
const MAX_LOGO_SIDE: u32 = 8192;
const UNREADABLE_LOGO: &str = "The logo file could not be read as an image.";
const DRAW_ERROR: &str = "The video frame could not be drawn.";
const BLACK: D2D1_COLOR_F = D2D1_COLOR_F { r: 0.0, g: 0.0, b: 0.0, a: 1.0 };

/// A texture the compositor draws into, with its Direct2D view.
struct Target {
    texture: ID3D11Texture2D,
    bitmap: ID2D1Bitmap1,
    size: PixelSize,
}

/// Draws every frame of the finished video: the screen, the logo and the webcam.
/// A returned texture stays valid until the next call of the same method, because each output reuses its own texture.
pub struct Compositor {
    device: Device,
    context: ID2D1DeviceContext,
    lock: ID2D1Multithread,
    screen: Target,
    output: Target,
    camera_file: Option<Target>,
    logo: Option<(ID2D1Bitmap1, PixelSize)>,
}

impl Compositor {
    pub fn new(device: &Device, frame_size: PixelSize, screen_size: PixelSize) -> Result<Compositor> {
        let create = || -> windows::core::Result<Compositor> {
            let context = unsafe { device.d2d().CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE) }?;
            let lock = unsafe { device.d2d().GetFactory() }?.cast()?;
            Ok(Compositor {
                screen: target(device, &context, screen_size)?,
                output: target(device, &context, frame_size)?,
                device: device.clone(),
                context,
                lock,
                camera_file: None,
                logo: None,
            })
        };
        create().map_err(|e| win(e, Error::Device, "The video frames could not be set up for drawing."))
    }

    /// The screen alone, letterboxed into the screen size with black bars.
    pub fn draw_screen(&mut self, screen: &Frame) -> Result<&ID3D11Texture2D> {
        let size = self.screen.size;
        paint(&self.context, &self.lock, &self.screen.bitmap, |dc| draw_letterboxed(dc, screen, size))
            .map_err(|e| win(e, Error::Device, DRAW_ERROR))?;
        Ok(&self.screen.texture)
    }

    /// The screen letterboxed into the frame size, then the logo, then the camera on top.
    pub fn draw_final(&mut self, screen: &Frame, camera: Option<&Frame>, layout: &Layout) -> Result<&ID3D11Texture2D> {
        let size = self.output.size;
        let logo = self.logo.as_ref().filter(|_| layout.logo.visible);
        let camera = camera.filter(|_| layout.camera.visible);
        paint(&self.context, &self.lock, &self.output.bitmap, |dc| {
            draw_letterboxed(dc, screen, size)?;
            if let Some((bitmap, pixels)) = logo {
                let area = rect(logo_rect(&layout.logo, size, *pixels));
                unsafe { dc.DrawBitmap(bitmap, Some(&area), 1.0, D2D1_INTERPOLATION_MODE_LINEAR, None, None) };
            }
            match camera {
                Some(camera) => draw_camera(dc, camera, camera_rect(&layout.camera, size), layout.camera.shape),
                None => Ok(()),
            }
        })
        .map_err(|e| win(e, Error::Device, DRAW_ERROR))?;
        Ok(&self.output.texture)
    }

    /// The full camera frame for `webcam.mp4`, with no mask and no mirror.
    pub fn draw_camera_file(&mut self, camera: &Frame) -> Result<&ID3D11Texture2D> {
        let file = match self.camera_file.take() {
            Some(file) if file.size == camera.size => file,
            _ => target(&self.device, &self.context, camera.size).map_err(|e| win(e, Error::Device, DRAW_ERROR))?,
        };
        let file = self.camera_file.insert(file);
        paint(&self.context, &self.lock, &file.bitmap, |dc| draw_letterboxed(dc, camera, camera.size))
            .map_err(|e| win(e, Error::Device, DRAW_ERROR))?;
        Ok(&file.texture)
    }

    /// Loads the logo, or clears it for `None`. Returns a plain message when the file cannot be used,
    /// and then the logo is cleared too.
    pub fn set_logo(&mut self, path: Option<&Path>) -> Option<String> {
        self.logo = None;
        match self.load_logo(path?) {
            Ok(logo) => {
                self.logo = Some(logo);
                None
            }
            Err(message) => Some(message.into()),
        }
    }

    fn load_logo(&self, path: &Path) -> std::result::Result<(ID2D1Bitmap1, PixelSize), &'static str> {
        let bytes = std::fs::metadata(path).map_err(|_| "The logo file was not found.")?.len();
        if bytes > MAX_LOGO_BYTES {
            return Err("The logo is larger than 20 MB.");
        }
        with_com(|| {
            let wic: IWICImagingFactory = unsafe { CoCreateInstance(&CLSID_WICImagingFactory, None, CLSCTX_INPROC_SERVER) }
                .map_err(|_| UNREADABLE_LOGO)?;
            let decoded = unsafe {
                wic.CreateDecoderFromFilename(&HSTRING::from(path.as_os_str()), None, GENERIC_READ, WICDecodeMetadataCacheOnDemand)
            }
            .and_then(|decoder| unsafe { decoder.GetFrame(0) })
            .map_err(|_| UNREADABLE_LOGO)?;
            let (mut width, mut height) = (0, 0);
            unsafe { decoded.GetSize(&mut width, &mut height) }.map_err(|_| UNREADABLE_LOGO)?;
            if width > MAX_LOGO_SIDE || height > MAX_LOGO_SIDE {
                return Err("The logo is larger than 8192 pixels on a side.");
            }
            let premultiplied =
                unsafe { WICConvertBitmapSource(&GUID_WICPixelFormat32bppPBGRA, &decoded) }.map_err(|_| UNREADABLE_LOGO)?;
            let properties = D2D1_BITMAP_PROPERTIES1 {
                pixelFormat: D2D1_PIXEL_FORMAT { format: DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX: 96.0,
                dpiY: 96.0,
                bitmapOptions: D2D1_BITMAP_OPTIONS_NONE,
                ..Default::default()
            };
            // The upload uses the Direct3D context, which the video writer also uses from its own thread.
            unsafe { self.lock.Enter() };
            let bitmap = unsafe { self.context.CreateBitmapFromWicBitmap(&premultiplied, Some(&properties)) };
            unsafe { self.lock.Leave() };
            let size = PixelSize { width: width as i32, height: height as i32 };
            Ok((bitmap.map_err(|_| UNREADABLE_LOGO)?, size))
        })
    }
}

/// Draws into `target` under the Direct2D lock, because the video writer copies textures
/// on the same Direct3D context from its own thread.
fn paint(
    context: &ID2D1DeviceContext,
    lock: &ID2D1Multithread,
    target: &ID2D1Bitmap1,
    draw: impl FnOnce(&ID2D1DeviceContext) -> windows::core::Result<()>,
) -> windows::core::Result<()> {
    unsafe { lock.Enter() };
    unsafe { context.SetTarget(target) };
    unsafe { context.BeginDraw() };
    unsafe { context.Clear(Some(&BLACK)) };
    let drawn = draw(context);
    let ended = unsafe { context.EndDraw(None, None) };
    unsafe { context.SetTarget(None::<&ID2D1Image>) };
    unsafe { lock.Leave() };
    drawn.and(ended)
}

fn draw_letterboxed(context: &ID2D1DeviceContext, frame: &Frame, size: PixelSize) -> windows::core::Result<()> {
    let bitmap = source(context, frame)?;
    let area = rect(letterbox(frame.size, size));
    unsafe { context.DrawBitmap(&bitmap, Some(&area), 1.0, D2D1_INTERPOLATION_MODE_LINEAR, None, None) };
    Ok(())
}

/// Fills `r` with the centered square of the camera frame, mirrored left to right and masked to `shape`.
fn draw_camera(context: &ID2D1DeviceContext, camera: &Frame, r: RectF, shape: CameraShape) -> windows::core::Result<()> {
    let brush = unsafe { context.CreateBitmapBrush(&source(context, camera)?, None, None) }?;
    let (w, h) = (camera.size.width as f32, camera.size.height as f32);
    let side = w.min(h);
    let scale = r.width / side;
    // Maps camera pixels to the frame: column (w - side) / 2 lands on the right edge of `r`.
    let mirror = [-scale, 0.0, 0.0, scale, r.x + r.width + (w - side) / 2.0 * scale, r.y - (h - side) / 2.0 * scale];
    // `windows` does not re-export its Matrix3x2, which is these 6 floats in this order.
    unsafe { brush.SetTransform(&mirror as *const [f32; 6] as *const _) };
    // A square rounded by half its side is a circle, which needs no `Vector2` center that `windows` does not export.
    let radius = match shape {
        CameraShape::Circle => 0.5,
        CameraShape::RoundedSquare => 0.18,
        CameraShape::Square => 0.0,
    } * r.width;
    let rounded = D2D1_ROUNDED_RECT { rect: rect(r), radiusX: radius, radiusY: radius };
    unsafe { context.FillRoundedRectangle(&rounded, &brush) };
    Ok(())
}

fn source(context: &ID2D1DeviceContext, frame: &Frame) -> windows::core::Result<ID2D1Bitmap1> {
    let properties = bitmap_properties(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_BITMAP_OPTIONS_NONE);
    unsafe { context.CreateBitmapFromDxgiSurface(&frame.texture.cast::<IDXGISurface>()?, Some(&properties)) }
}

fn target(device: &Device, context: &ID2D1DeviceContext, size: PixelSize) -> windows::core::Result<Target> {
    let desc = D3D11_TEXTURE2D_DESC {
        Width: size.width as u32,
        Height: size.height as u32,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
        ..Default::default()
    };
    let mut texture = None;
    unsafe { device.d3d().CreateTexture2D(&desc, None, Some(&mut texture)) }?;
    let texture = texture.ok_or_else(windows::core::Error::empty)?;
    let properties = bitmap_properties(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
    let bitmap = unsafe { context.CreateBitmapFromDxgiSurface(&texture.cast::<IDXGISurface>()?, Some(&properties)) }?;
    Ok(Target { texture, bitmap, size })
}

fn rect(r: RectF) -> D2D_RECT_F {
    D2D_RECT_F { left: r.x, top: r.y, right: r.x + r.width, bottom: r.y + r.height }
}
