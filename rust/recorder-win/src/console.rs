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
/// the process alive while it runs. A second call is refused.
pub fn on_console_stop(handler: fn()) -> Result<()> {
    if HANDLER.set(handler).is_err() {
        return Err(Error::Invalid("The console stop handler is already set.".into()));
    }
    unsafe { SetConsoleCtrlHandler(Some(route), true) }
        .map_err(|e| win(e, Error::Device, "The console stop handler could not be set."))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn first() {}
    fn second() {}

    #[test]
    fn a_second_handler_is_refused() {
        on_console_stop(first).unwrap();
        assert!(matches!(on_console_stop(second), Err(Error::Invalid(_))));
    }
}
