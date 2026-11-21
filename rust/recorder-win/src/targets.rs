use std::ffi::c_void;
use windows::core::BOOL;
use windows::Win32::Foundation::{HWND, LPARAM, RECT};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED};
use windows::Win32::Graphics::Gdi::{EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW};
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetWindowLongPtrW, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsIconic,
    IsWindowVisible, GWL_EXSTYLE, MONITORINFOF_PRIMARY, WS_EX_TOOLWINDOW,
};

/// A monitor in physical pixels. `device` is the GDI name, such as `\\.\DISPLAY1`, which outlives the handle.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MonitorInfo {
    pub handle: isize,
    pub name: String,
    pub device: String,
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub primary: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WindowInfo {
    pub handle: isize,
    pub title: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureTarget {
    Monitor(isize),
    Window(isize),
    /// x and y are in that monitor's pixels, relative to its top-left corner.
    Region { monitor: isize, x: i32, y: i32, width: i32, height: i32 },
}

impl CaptureTarget {
    pub fn kind(&self) -> &'static str {
        match self {
            CaptureTarget::Monitor(_) => "monitor",
            CaptureTarget::Window(_) => "window",
            CaptureTarget::Region { .. } => "region",
        }
    }
}

/// Monitors with the primary one first, named the way the C# app names them.
pub fn monitors() -> Vec<MonitorInfo> {
    unsafe extern "system" fn add(h: HMONITOR, _: HDC, _: *mut RECT, list: LPARAM) -> BOOL {
        unsafe { (*(list.0 as *mut Vec<HMONITOR>)).push(h) };
        true.into()
    }
    let mut handles: Vec<HMONITOR> = Vec::new();
    // If enumeration fails the list stays empty, which callers treat as no monitors.
    let _ = unsafe { EnumDisplayMonitors(None, None, Some(add), LPARAM(&mut handles as *mut _ as isize)) };
    let mut list: Vec<MonitorInfo> = handles
        .into_iter()
        .filter_map(|h| {
            let mut info = MONITORINFOEXW::default();
            info.monitorInfo.cbSize = size_of::<MONITORINFOEXW>() as u32;
            if !unsafe { GetMonitorInfoW(h, &mut info as *mut _ as *mut MONITORINFO) }.as_bool() {
                return None;
            }
            let r = info.monitorInfo.rcMonitor;
            Some(MonitorInfo {
                handle: h.0 as isize,
                name: String::new(),
                device: utf16(&info.szDevice),
                x: r.left,
                y: r.top,
                width: r.right - r.left,
                height: r.bottom - r.top,
                primary: info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0,
            })
        })
        .collect();
    list.sort_by_key(|m| !m.primary);
    for (i, m) in list.iter_mut().enumerate() {
        m.name = format!("Display {} ({} x {})", i + 1, m.width, m.height);
    }
    list
}

/// Top-level windows in z-order that a person can pick: visible, not minimized, titled,
/// not tool or cloaked windows, and not this process's own.
pub fn windows() -> Vec<WindowInfo> {
    unsafe extern "system" fn add(h: HWND, list: LPARAM) -> BOOL {
        unsafe { (*(list.0 as *mut Vec<HWND>)).push(h) };
        true.into()
    }
    let mut handles: Vec<HWND> = Vec::new();
    // If enumeration fails the list stays empty, which callers treat as no windows.
    let _ = unsafe { EnumWindows(Some(add), LPARAM(&mut handles as *mut _ as isize)) };
    let own = unsafe { GetCurrentProcessId() };
    handles
        .into_iter()
        .filter_map(|h| {
            let mut cloaked = 0u32;
            let _ = unsafe { DwmGetWindowAttribute(h, DWMWA_CLOAKED, &mut cloaked as *mut _ as *mut c_void, 4) };
            let mut pid = 0;
            unsafe { GetWindowThreadProcessId(h, Some(&mut pid)) };
            let tool = unsafe { GetWindowLongPtrW(h, GWL_EXSTYLE) } as u32 & WS_EX_TOOLWINDOW.0 != 0;
            let shown = unsafe { IsWindowVisible(h).as_bool() && !IsIconic(h).as_bool() };
            if !shown || cloaked != 0 || tool || pid == own {
                return None;
            }
            let mut text = vec![0u16; unsafe { GetWindowTextLengthW(h) }.max(0) as usize + 1];
            let n = unsafe { GetWindowTextW(h, &mut text) }.max(0) as usize;
            let title = String::from_utf16_lossy(&text[..n]);
            (!title.trim().is_empty()).then_some(WindowInfo { handle: h.0 as isize, title })
        })
        .collect()
}

/// The one window whose title contains `title_part`, ignoring case. `Err` holds the matches when there are 0 or 2+.
pub fn find_window(title_part: &str) -> Result<WindowInfo, Vec<WindowInfo>> {
    // A blank title would match every window.
    if title_part.trim().is_empty() {
        return Err(Vec::new());
    }
    let part = title_part.to_lowercase();
    let mut found: Vec<WindowInfo> = windows().into_iter().filter(|w| w.title.to_lowercase().contains(&part)).collect();
    if found.len() == 1 {
        Ok(found.remove(0))
    } else {
        Err(found)
    }
}

fn utf16(s: &[u16]) -> String {
    String::from_utf16_lossy(&s[..s.iter().position(|&c| c == 0).unwrap_or(s.len())])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn find_window_with_blank_title_finds_nothing() {
        assert_eq!(Err(vec![]), find_window(""));
        assert_eq!(Err(vec![]), find_window("  "));
    }

    #[test]
    fn kind_names_each_target() {
        assert_eq!("monitor", CaptureTarget::Monitor(1).kind());
        assert_eq!("window", CaptureTarget::Window(1).kind());
        assert_eq!("region", CaptureTarget::Region { monitor: 1, x: 0, y: 0, width: 16, height: 16 }.kind());
    }
}
