use crate::error::{win, Error, Result};
use windows::core::Interface;
use windows::Win32::Foundation::{E_POINTER, HMODULE};
use windows::Win32::Graphics::Direct2D::{D2D1CreateFactory, ID2D1Device, ID2D1Factory1, D2D1_FACTORY_TYPE_MULTI_THREADED};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Multithread, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_CREATE_DEVICE_FLAG, D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION,
};
use windows::Win32::Graphics::Dxgi::{IDXGIAdapter, IDXGIDevice};

/// The Direct3D 11 device that capture, drawing and encoding share. Clones share the same device.
#[derive(Clone)]
pub struct Device {
    d3d: ID3D11Device,
    context: ID3D11DeviceContext,
    d2d: ID2D1Device,
    video_support: bool,
}

fn device_error(e: windows::core::Error) -> Error {
    win(e, Error::Device, "The graphics device could not be created.")
}

fn create_d3d(flags: D3D11_CREATE_DEVICE_FLAG) -> windows::core::Result<(ID3D11Device, ID3D11DeviceContext)> {
    let mut d3d = None;
    let mut context = None;
    unsafe {
        D3D11CreateDevice(
            None::<&IDXGIAdapter>,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            flags,
            None,
            D3D11_SDK_VERSION,
            Some(&mut d3d),
            None,
            Some(&mut context),
        )
    }?;
    d3d.zip(context).ok_or_else(|| windows::core::Error::from_hresult(E_POINTER))
}

impl Device {
    pub fn new() -> Result<Device> {
        // Adapters without video support, such as the basic display adapter, refuse the video flag.
        let ((d3d, context), video_support) =
            match create_d3d(D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT) {
                Ok(pair) => (pair, true),
                Err(_) => (create_d3d(D3D11_CREATE_DEVICE_BGRA_SUPPORT).map_err(device_error)?, false),
            };
        // Capture callbacks, the encoder and the recording thread all use the device at once.
        // The return value is only the previous setting.
        let _ = unsafe { d3d.cast::<ID3D11Multithread>().map_err(device_error)?.SetMultithreadProtected(true) };
        let factory: ID2D1Factory1 =
            unsafe { D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, None) }.map_err(device_error)?;
        let dxgi: IDXGIDevice = d3d.cast().map_err(device_error)?;
        let d2d = unsafe { factory.CreateDevice(&dxgi) }.map_err(device_error)?;
        Ok(Device { d3d, context, d2d, video_support })
    }

    /// False when the adapter has no video support, so the encoder must use software.
    pub fn video_support(&self) -> bool {
        self.video_support
    }

    pub fn d3d(&self) -> &ID3D11Device {
        &self.d3d
    }

    pub fn context(&self) -> &ID3D11DeviceContext {
        &self.context
    }

    pub fn d2d(&self) -> &ID2D1Device {
        &self.d2d
    }
}
