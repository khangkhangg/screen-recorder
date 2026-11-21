use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use recorder_core::frame_math::PixelSize;
use windows::core::{h, IInspectable, IUnknown, Interface, Ref};
use windows::Foundation::Metadata::ApiInformation;
use windows::Foundation::TypedEventHandler;
use windows::Graphics::Capture::{Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Graphics::SizeInt32;
use windows::Win32::Devices::Display::{
    DisplayConfigGetDeviceInfo, GetDisplayConfigBufferSizes, QueryDisplayConfig,
    DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
    DISPLAYCONFIG_DEVICE_INFO_HEADER, DISPLAYCONFIG_MODE_INFO, DISPLAYCONFIG_PATH_INFO,
    DISPLAYCONFIG_SDR_WHITE_LEVEL, DISPLAYCONFIG_SOURCE_DEVICE_NAME, QDC_ONLY_ACTIVE_PATHS,
};
use windows::Win32::Foundation::{HWND, LUID};
use windows::Win32::Graphics::Direct2D::Common::{
    D2D1_ALPHA_MODE_IGNORE, D2D1_COMPOSITE_MODE_SOURCE_COPY, D2D1_PIXEL_FORMAT, D2D_RECT_F,
};
use windows::Win32::Graphics::Direct2D::{
    ID2D1ColorContext, ID2D1DeviceContext, ID2D1Effect, ID2D1Image, CLSID_D2D1ColorManagement,
    CLSID_D2D1HdrToneMap, CLSID_D2D1WhiteLevelAdjustment, D2D1_BITMAP_OPTIONS,
    D2D1_BITMAP_OPTIONS_CANNOT_DRAW, D2D1_BITMAP_OPTIONS_NONE, D2D1_BITMAP_OPTIONS_TARGET,
    D2D1_BITMAP_PROPERTIES1, D2D1_BUFFER_PRECISION_16BPC_FLOAT,
    D2D1_COLORMANAGEMENT_PROP_DESTINATION_COLOR_CONTEXT, D2D1_COLORMANAGEMENT_PROP_QUALITY,
    D2D1_COLORMANAGEMENT_PROP_SOURCE_COLOR_CONTEXT, D2D1_COLORMANAGEMENT_QUALITY_BEST,
    D2D1_COLOR_SPACE_SCRGB, D2D1_COLOR_SPACE_SRGB, D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
    D2D1_HDRTONEMAP_DISPLAY_MODE_SDR, D2D1_HDRTONEMAP_PROP_DISPLAY_MODE,
    D2D1_HDRTONEMAP_PROP_INPUT_MAX_LUMINANCE, D2D1_HDRTONEMAP_PROP_OUTPUT_MAX_LUMINANCE,
    D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_PROPERTY_TYPE, D2D1_PROPERTY_TYPE_COLOR_CONTEXT,
    D2D1_PROPERTY_TYPE_ENUM, D2D1_PROPERTY_TYPE_FLOAT,
    D2D1_WHITELEVELADJUSTMENT_PROP_INPUT_WHITE_LEVEL, D2D1_WHITELEVELADJUSTMENT_PROP_OUTPUT_WHITE_LEVEL,
};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11Texture2D, D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_BOX,
    D3D11_CPU_ACCESS_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_READ, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020, DXGI_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM,
    DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIDevice, IDXGIFactory1, IDXGIOutput6, IDXGISurface};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MonitorFromWindow, HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST,
};
use windows::Win32::System::WinRT::Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::Win32::UI::WindowsAndMessaging::{IsIconic, IsWindow};

use crate::error::{win, Error, Result};
use crate::gfx::Device;
use crate::targets::{monitors, CaptureTarget};

const REGION_ERROR: &str = "The selected area is empty, too small or outside the screen.";
/// scRGB puts 1.0 at 80 nits, which is also where standard white sits on an SDR screen.
const SCRGB_WHITE_NITS: f32 = 80.0;
/// Textures a source writes into: the newest frame, one the caller holds, one being written.
const POOL_TEXTURES: usize = 3;

/// A captured frame. It holds a counted reference to its texture, and the source
/// never writes into a texture that anyone else still holds, so it stays valid
/// for as long as the caller keeps it.
#[derive(Clone)]
pub struct Frame {
    pub texture: ID3D11Texture2D,
    pub size: PixelSize,
}

pub trait Screen: Send {
    fn source_size(&self) -> PixelSize;
    fn latest_frame(&mut self) -> Option<Frame>;
    fn is_minimized(&self) -> bool;
    fn is_closed(&self) -> bool;
}

/// Captures a monitor, a window or a region of a monitor into 8-bit BGRA textures.
pub struct ScreenSource {
    target: CaptureTarget,
    shared: Arc<Shared>,
    item: GraphicsCaptureItem,
    pool: Direct3D11CaptureFramePool,
    session: GraphicsCaptureSession,
    closed_token: i64,
    frame_token: i64,
    watcher: Option<(Sender<()>, JoinHandle<()>)>,
}

struct Shared {
    closed: AtomicBool,
    state: Mutex<State>,
    copier: Mutex<Copier>,
}

struct State {
    latest: Option<Frame>,
    textures: Vec<ID3D11Texture2D>,
    size: PixelSize,
}

/// Only the frame handler uses this. Lock it before `State` when both are needed.
struct Copier {
    device: Device,
    format: DirectXPixelFormat,
    pool_size: SizeInt32,
    origin: (i32, i32),
    is_region: bool,
    hdr: Option<Hdr>,
}

/// Turns 16-bit float scRGB frames into standard sRGB: standard content keeps its
/// brightness and highlights above standard white roll off instead of clipping.
struct Hdr {
    context: ID2D1DeviceContext,
    input: ID2D1Effect,
    output: ID2D1Image,
}

impl ScreenSource {
    pub fn create(target: &CaptureTarget, device: &Device) -> Result<ScreenSource> {
        let unavailable = |m: &str| Error::CaptureUnavailable(m.to_string());
        if !GraphicsCaptureSession::IsSupported().unwrap_or(false) {
            return Err(unavailable("Screen capture is not supported on this PC."));
        }
        let region = match *target {
            CaptureTarget::Region { x, y, width, height, .. } => {
                if x < 0 || y < 0 || width < 16 || height < 16 {
                    return Err(unavailable(REGION_ERROR));
                }
                Some((x, y, width, height))
            }
            _ => None,
        };
        let not_recordable = match target {
            CaptureTarget::Window(_) => "This window cannot be recorded.",
            _ => "This screen cannot be recorded.",
        };
        let item = create_item(target).map_err(|e| win(e, Error::CaptureUnavailable, not_recordable))?;
        let item_size = item.Size().map_err(|e| win(e, Error::CaptureUnavailable, not_recordable))?;
        if let Some((x, y, ..)) = region {
            if x >= item_size.Width || y >= item_size.Height {
                return Err(unavailable(REGION_ERROR));
            }
        }

        let monitor = monitor_of(target);
        let hdr = match hdr_peak_nits(monitor) {
            Some(peak) => {
                // shortcut: assumes the Windows default of 80 nits when the display API gives no SDR white level, replace if a PC shows wrong brightness.
                let white = sdr_white_nits(monitor).unwrap_or(SCRGB_WHITE_NITS);
                Some(Hdr::new(device, peak, white).map_err(|e| win(e, Error::Device, "HDR conversion could not be set up."))?)
            }
            None => None,
        };
        let format = if hdr.is_some() {
            DirectXPixelFormat::R16G16B16A16Float
        } else {
            DirectXPixelFormat::B8G8R8A8UIntNormalized
        };
        let start_error = |e| win(e, Error::CaptureUnavailable, "Screen capture could not start.");
        let capture_device = capture_device(device).map_err(start_error)?;
        let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(&capture_device, format, 2, item_size)
            .map_err(start_error)?;
        let size = match region {
            Some((_, _, width, height)) => PixelSize { width, height },
            None => PixelSize { width: item_size.Width, height: item_size.Height },
        };
        let shared = Arc::new(Shared {
            closed: AtomicBool::new(false),
            state: Mutex::new(State { latest: None, textures: Vec::new(), size }),
            copier: Mutex::new(Copier {
                device: device.clone(),
                format,
                pool_size: item_size,
                origin: region.map_or((0, 0), |(x, y, ..)| (x, y)),
                is_region: region.is_some(),
                hdr,
            }),
        });

        let start = || -> windows::core::Result<(GraphicsCaptureSession, i64, i64)> {
            let session = pool.CreateCaptureSession(&item)?;
            session.SetIsCursorCaptureEnabled(true)?;
            if ApiInformation::IsPropertyPresent(h!("Windows.Graphics.Capture.GraphicsCaptureSession"), h!("IsBorderRequired"))? {
                // Windows can refuse this, and then the yellow border simply stays.
                let _ = session.SetIsBorderRequired(false);
            }
            let s = shared.clone();
            let closed_token = item.Closed(&TypedEventHandler::<GraphicsCaptureItem, IInspectable>::new(move |_, _| {
                s.closed.store(true, Ordering::SeqCst);
                Ok(())
            }))?;
            let s = shared.clone();
            let frame_token = pool.FrameArrived(&TypedEventHandler::new(
                move |pool: Ref<Direct3D11CaptureFramePool>, _: Ref<IInspectable>| {
                    // A failed frame stops the recording, so it shows as closed instead of freezing.
                    if pool.ok().and_then(|p| on_frame(&s, p)).is_err() {
                        s.closed.store(true, Ordering::SeqCst);
                    }
                    Ok(())
                },
            ))?;
            session.StartCapture()?;
            Ok((session, closed_token, frame_token))
        };
        let (session, closed_token, frame_token) = match start() {
            Ok(started) => started,
            Err(e) => {
                let _ = pool.Close();
                return Err(start_error(e));
            }
        };

        let (stop, stopped) = mpsc::channel::<()>();
        let s = shared.clone();
        let watched = *target;
        let watcher = thread::spawn(move || {
            // item.Closed did not fire in the C# tests when a window's process ended, so also check every 500 ms.
            while let Err(RecvTimeoutError::Timeout) = stopped.recv_timeout(Duration::from_millis(500)) {
                if !target_exists(&watched) {
                    s.closed.store(true, Ordering::SeqCst);
                    break;
                }
            }
        });

        Ok(ScreenSource {
            target: *target,
            shared,
            item,
            pool,
            session,
            closed_token,
            frame_token,
            watcher: Some((stop, watcher)),
        })
    }
}

impl Screen for ScreenSource {
    fn source_size(&self) -> PixelSize {
        lock(&self.shared.state).size
    }

    fn latest_frame(&mut self) -> Option<Frame> {
        lock(&self.shared.state).latest.clone()
    }

    fn is_minimized(&self) -> bool {
        match self.target {
            CaptureTarget::Window(h) => unsafe { IsIconic(HWND(h as _)) }.as_bool(),
            _ => false,
        }
    }

    fn is_closed(&self) -> bool {
        self.shared.closed.load(Ordering::SeqCst)
    }
}

impl Drop for ScreenSource {
    fn drop(&mut self) {
        if let Some((stop, watcher)) = self.watcher.take() {
            drop(stop);
            let _ = watcher.join();
        }
        let _ = self.item.RemoveClosed(self.closed_token);
        let _ = self.pool.RemoveFrameArrived(self.frame_token);
        let _ = self.session.Close();
        let _ = self.pool.Close();
    }
}

/// Runs on a thread-pool thread for every new frame.
fn on_frame(shared: &Shared, pool: &Direct3D11CaptureFramePool) -> windows::core::Result<()> {
    let Ok(frame) = pool.TryGetNextFrame() else { return Ok(()) };
    let mut copier = lock(&shared.copier);
    let content = frame.ContentSize()?;
    if content != copier.pool_size {
        // This frame still has the old size, so drop it and wait for one at the new size.
        pool.Recreate(&capture_device(&copier.device)?, copier.format, 2, content)?;
        copier.pool_size = content;
        if !copier.is_region {
            let mut state = lock(&shared.state);
            state.size = PixelSize { width: content.Width, height: content.Height };
            state.textures.clear();
        }
        return frame.Close();
    }
    let access: IDirect3DDxgiInterfaceAccess = frame.Surface()?.cast()?;
    let source: ID3D11Texture2D = unsafe { access.GetInterface() }?;
    let (target, size) = {
        let mut state = lock(&shared.state);
        match free_texture(&mut state, &copier.device)? {
            Some(t) => (t, state.size),
            // The caller holds every texture, so skip this frame rather than overwrite one.
            None => return frame.Close(),
        }
    };
    copier.copy(&source, &target, size)?;
    lock(&shared.state).latest = Some(Frame { texture: target, size });
    frame.Close()
}

/// Returns a pool texture that only the pool holds, creating one while the pool is small.
fn free_texture(state: &mut State, device: &Device) -> windows::core::Result<Option<ID3D11Texture2D>> {
    if let Some(t) = state.textures.iter().find(|t| ref_count(t) == 1) {
        return Ok(Some(t.clone()));
    }
    if state.textures.len() >= POOL_TEXTURES {
        return Ok(None);
    }
    let desc = D3D11_TEXTURE2D_DESC {
        Width: state.size.width as u32,
        Height: state.size.height as u32,
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
    state.textures.push(texture.clone());
    Ok(Some(texture))
}

fn ref_count(texture: &ID3D11Texture2D) -> u32 {
    let raw = texture.as_raw();
    // SAFETY: the pointer is alive while we hold `texture`, and AddRef followed by
    // Release leaves the count as it was. Release returns that count.
    unsafe {
        let unknown = IUnknown::from_raw_borrowed(&raw).expect("a live texture is never null");
        (unknown.vtable().AddRef)(raw);
        (unknown.vtable().Release)(raw)
    }
}

impl Copier {
    /// GPU copy, because the capture pool reuses the frame's surface once the frame is closed.
    fn copy(&self, source: &ID3D11Texture2D, target: &ID3D11Texture2D, size: PixelSize) -> windows::core::Result<()> {
        let (x, y) = self.origin;
        let w = size.width.min(self.pool_size.Width - x);
        let h = size.height.min(self.pool_size.Height - y);
        if w <= 0 || h <= 0 {
            return Ok(());
        }
        match &self.hdr {
            Some(hdr) => hdr.draw(source, target, D2D_RECT_F {
                left: x as f32,
                top: y as f32,
                right: (x + w) as f32,
                bottom: (y + h) as f32,
            }),
            None => {
                let area = D3D11_BOX {
                    left: x as u32,
                    top: y as u32,
                    front: 0,
                    right: (x + w) as u32,
                    bottom: (y + h) as u32,
                    back: 1,
                };
                unsafe { self.device.context().CopySubresourceRegion(target, 0, 0, 0, 0, source, 0, Some(&area)) };
                Ok(())
            }
        }
    }
}

impl Hdr {
    fn new(device: &Device, peak_nits: f32, sdr_white_nits: f32) -> windows::core::Result<Hdr> {
        unsafe {
            let context = device.d2d().CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE)?;
            let mut controls = context.GetRenderingControls();
            controls.bufferPrecision = D2D1_BUFFER_PRECISION_16BPC_FLOAT;
            context.SetRenderingControls(&controls);

            // Puts standard white back at 1.0. Measured on this PC, the effect scales by input / output.
            let white = context.CreateEffect(&CLSID_D2D1WhiteLevelAdjustment)?;
            set(&white, D2D1_WHITELEVELADJUSTMENT_PROP_INPUT_WHITE_LEVEL.0, D2D1_PROPERTY_TYPE_FLOAT, &SCRGB_WHITE_NITS.to_le_bytes())?;
            set(&white, D2D1_WHITELEVELADJUSTMENT_PROP_OUTPUT_WHITE_LEVEL.0, D2D1_PROPERTY_TYPE_FLOAT, &sdr_white_nits.to_le_bytes())?;

            // Rolls off highlights above standard white. The tone curve starts bending below its
            // output maximum, so that maximum sits 10% above white: measured here, 1.0 dimmed
            // white to 245 and 1.1 keeps it at 252. The top of the roll-off still clips at 255.
            let tone = context.CreateEffect(&CLSID_D2D1HdrToneMap)?;
            let top = 1.1 * SCRGB_WHITE_NITS;
            let peak = (peak_nits * SCRGB_WHITE_NITS / sdr_white_nits).max(top);
            set(&tone, D2D1_HDRTONEMAP_PROP_INPUT_MAX_LUMINANCE.0, D2D1_PROPERTY_TYPE_FLOAT, &peak.to_le_bytes())?;
            set(&tone, D2D1_HDRTONEMAP_PROP_OUTPUT_MAX_LUMINANCE.0, D2D1_PROPERTY_TYPE_FLOAT, &top.to_le_bytes())?;
            set(&tone, D2D1_HDRTONEMAP_PROP_DISPLAY_MODE.0, D2D1_PROPERTY_TYPE_ENUM, &(D2D1_HDRTONEMAP_DISPLAY_MODE_SDR.0 as u32).to_le_bytes())?;
            tone.SetInput(0, &white.GetOutput()?, true);

            let to_srgb = context.CreateEffect(&CLSID_D2D1ColorManagement)?;
            set_color(&to_srgb, D2D1_COLORMANAGEMENT_PROP_SOURCE_COLOR_CONTEXT.0, &context.CreateColorContext(D2D1_COLOR_SPACE_SCRGB, None)?)?;
            set_color(&to_srgb, D2D1_COLORMANAGEMENT_PROP_DESTINATION_COLOR_CONTEXT.0, &context.CreateColorContext(D2D1_COLOR_SPACE_SRGB, None)?)?;
            set(&to_srgb, D2D1_COLORMANAGEMENT_PROP_QUALITY.0, D2D1_PROPERTY_TYPE_ENUM, &(D2D1_COLORMANAGEMENT_QUALITY_BEST.0 as u32).to_le_bytes())?;
            to_srgb.SetInput(0, &tone.GetOutput()?, true);

            Ok(Hdr { output: to_srgb.GetOutput()?, context, input: white })
        }
    }

    fn draw(&self, source: &ID3D11Texture2D, target: &ID3D11Texture2D, area: D2D_RECT_F) -> windows::core::Result<()> {
        unsafe {
            let input = self.context.CreateBitmapFromDxgiSurface(
                &source.cast::<IDXGISurface>()?,
                Some(&bitmap_properties(DXGI_FORMAT_R16G16B16A16_FLOAT, D2D1_BITMAP_OPTIONS_NONE)),
            )?;
            let output = self.context.CreateBitmapFromDxgiSurface(
                &target.cast::<IDXGISurface>()?,
                Some(&bitmap_properties(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW)),
            )?;
            self.input.SetInput(0, &input, true);
            self.context.SetTarget(&output);
            self.context.BeginDraw();
            self.context.DrawImage(&self.output, None, Some(&area), D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE_SOURCE_COPY);
            let drawn = self.context.EndDraw(None, None);
            // Let go of both surfaces, so the pool can reuse them and the reference count stays exact.
            self.context.SetTarget(None::<&ID2D1Image>);
            self.input.SetInput(0, None::<&ID2D1Image>, false);
            drawn
        }
    }
}

fn bitmap_properties(format: DXGI_FORMAT, options: D2D1_BITMAP_OPTIONS) -> D2D1_BITMAP_PROPERTIES1 {
    D2D1_BITMAP_PROPERTIES1 {
        // GDI-drawn windows leave alpha at 0, and un-premultiplying those pixels turns them white.
        pixelFormat: D2D1_PIXEL_FORMAT { format, alphaMode: D2D1_ALPHA_MODE_IGNORE },
        dpiX: 96.0,
        dpiY: 96.0,
        bitmapOptions: options,
        ..Default::default()
    }
}

unsafe fn set(effect: &ID2D1Effect, index: i32, kind: D2D1_PROPERTY_TYPE, value: &[u8]) -> windows::core::Result<()> {
    unsafe { effect.SetValue(index as u32, kind, value) }
}

/// Interface properties take the pointer itself as their value, and the effect keeps its own reference.
unsafe fn set_color(effect: &ID2D1Effect, index: i32, color: &ID2D1ColorContext) -> windows::core::Result<()> {
    unsafe { set(effect, index, D2D1_PROPERTY_TYPE_COLOR_CONTEXT, &(color.as_raw() as usize).to_ne_bytes()) }
}

fn create_item(target: &CaptureTarget) -> windows::core::Result<GraphicsCaptureItem> {
    let interop = windows::core::factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>()?;
    unsafe {
        match *target {
            CaptureTarget::Window(h) => interop.CreateForWindow(HWND(h as _)),
            CaptureTarget::Monitor(m) | CaptureTarget::Region { monitor: m, .. } => interop.CreateForMonitor(HMONITOR(m as _)),
        }
    }
}

fn capture_device(device: &Device) -> windows::core::Result<IDirect3DDevice> {
    let dxgi: IDXGIDevice = device.d3d().cast()?;
    unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi) }?.cast()
}

fn monitor_of(target: &CaptureTarget) -> HMONITOR {
    match *target {
        CaptureTarget::Window(h) => unsafe { MonitorFromWindow(HWND(h as _), MONITOR_DEFAULTTONEAREST) },
        CaptureTarget::Monitor(m) | CaptureTarget::Region { monitor: m, .. } => HMONITOR(m as _),
    }
}

fn target_exists(target: &CaptureTarget) -> bool {
    match *target {
        CaptureTarget::Window(h) => unsafe { IsWindow(Some(HWND(h as _))) }.as_bool(),
        CaptureTarget::Monitor(m) | CaptureTarget::Region { monitor: m, .. } => monitors().iter().any(|i| i.handle == m),
    }
}

/// The monitor's peak brightness in nits when it runs in HDR, or `None` for SDR.
fn hdr_peak_nits(monitor: HMONITOR) -> Option<f32> {
    let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }.ok()?;
    for adapter in (0..).map_while(|i| unsafe { factory.EnumAdapters1(i) }.ok()) {
        for output in (0..).map_while(|i| unsafe { adapter.EnumOutputs(i) }.ok()) {
            let Ok(desc) = output.cast::<IDXGIOutput6>().and_then(|o| unsafe { o.GetDesc1() }) else { continue };
            if desc.Monitor == monitor {
                return (desc.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020).then_some(desc.MaxLuminance);
            }
        }
    }
    None
}

/// The "SDR content brightness" the user set in Windows HDR settings, in nits.
fn sdr_white_nits(monitor: HMONITOR) -> Option<f32> {
    let mut info = MONITORINFOEXW::default();
    info.monitorInfo.cbSize = size_of::<MONITORINFOEXW>() as u32;
    if !unsafe { GetMonitorInfoW(monitor, &mut info as *mut _ as *mut MONITORINFO) }.as_bool() {
        return None;
    }
    let (mut path_count, mut mode_count) = (0u32, 0u32);
    unsafe { GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &mut path_count, &mut mode_count) }.ok().ok()?;
    let mut paths = vec![DISPLAYCONFIG_PATH_INFO::default(); path_count as usize];
    let mut modes = vec![DISPLAYCONFIG_MODE_INFO::default(); mode_count as usize];
    unsafe {
        QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &mut path_count, paths.as_mut_ptr(), &mut mode_count, modes.as_mut_ptr(), None)
    }
    .ok()
    .ok()?;
    paths.truncate(path_count as usize);
    let header = |kind, size: usize, adapter: LUID, id: u32| DISPLAYCONFIG_DEVICE_INFO_HEADER {
        r#type: kind,
        size: size as u32,
        adapterId: adapter,
        id,
    };
    for path in paths {
        let mut name = DISPLAYCONFIG_SOURCE_DEVICE_NAME {
            header: header(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, size_of::<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), path.sourceInfo.adapterId, path.sourceInfo.id),
            ..Default::default()
        };
        if unsafe { DisplayConfigGetDeviceInfo(&mut name.header) } != 0 || name.viewGdiDeviceName != info.szDevice {
            continue;
        }
        let mut white = DISPLAYCONFIG_SDR_WHITE_LEVEL {
            header: header(DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, size_of::<DISPLAYCONFIG_SDR_WHITE_LEVEL>(), path.targetInfo.adapterId, path.targetInfo.id),
            ..Default::default()
        };
        if unsafe { DisplayConfigGetDeviceInfo(&mut white.header) } == 0 {
            // Windows stores it in thousandths of 80 nits.
            return Some(white.SDRWhiteLevel as f32 * SCRGB_WHITE_NITS / 1000.0);
        }
    }
    None
}

/// Reads back a 16 by 16 grid of pixels only, so it is cheap. Use it before
/// recording starts, never per frame.
pub fn looks_black(frame: &Frame, device: &Device) -> bool {
    sample_grid(frame, device).is_ok_and(|px| px.chunks(4).all(|p| p[..3] == [0, 0, 0]))
}

fn sample_grid(frame: &Frame, device: &Device) -> windows::core::Result<Vec<u8>> {
    const GRID: u32 = 16;
    let desc = D3D11_TEXTURE2D_DESC {
        Width: GRID,
        Height: GRID,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
        Usage: D3D11_USAGE_STAGING,
        CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
        ..Default::default()
    };
    let (w, h) = (frame.size.width.max(1) as u32, frame.size.height.max(1) as u32);
    unsafe {
        let mut staging = None;
        device.d3d().CreateTexture2D(&desc, None, Some(&mut staging))?;
        let staging = staging.ok_or_else(windows::core::Error::empty)?;
        let context = device.context();
        for gy in 0..GRID {
            for gx in 0..GRID {
                // The center of each grid cell.
                let (x, y) = (w * (2 * gx + 1) / (2 * GRID), h * (2 * gy + 1) / (2 * GRID));
                let pixel = D3D11_BOX { left: x, top: y, front: 0, right: x + 1, bottom: y + 1, back: 1 };
                context.CopySubresourceRegion(&staging, 0, gx, gy, 0, &frame.texture, 0, Some(&pixel));
            }
        }
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        context.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))?;
        let mut pixels = Vec::with_capacity((GRID * GRID * 4) as usize);
        for row in 0..GRID {
            let start = (mapped.pData as *const u8).add((row * mapped.RowPitch) as usize);
            pixels.extend_from_slice(std::slice::from_raw_parts(start, (GRID * 4) as usize));
        }
        context.Unmap(&staging, 0);
        Ok(pixels)
    }
}

/// Locking still works after a panic on another thread, because the data stays consistent.
fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}
