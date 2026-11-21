use std::sync::OnceLock;

use windows::core::BOOL;
use windows::Win32::System::Console::SetConsoleCtrlHandler;

use crate::error::{win, Error, Result};

static HANDLER: OnceLock<fn()> = OnceLock::new();

unsafe extern "system" fn route(_event: u32) -> BOOL {
    if let Some(handler) = HANDLER.get() {
        handler();
    }
    true.into()
}

/// Runs `handler` on its own thread for Ctrl+C, Ctrl+Break and closing the console, and keeps
/// the process alive while it runs. Only the first handler counts.
pub fn on_console_stop(handler: fn()) -> Result<()> {
    let _ = HANDLER.set(handler);
    unsafe { SetConsoleCtrlHandler(Some(route), true) }
        .map_err(|e| win(e, Error::Device, "The console stop handler could not be set."))
}
