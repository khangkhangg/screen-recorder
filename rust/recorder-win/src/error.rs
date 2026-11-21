use std::fmt;

#[derive(Debug)]
pub enum Error {
    CaptureUnavailable(String),
    Device(String),
    Encoder(String),
    Io(std::io::Error),
    NotSupported(String),
    Invalid(String),
}

pub type Result<T> = std::result::Result<T, Error>;

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        match self {
            Error::Io(e) => e.fmt(f),
            Error::CaptureUnavailable(m)
            | Error::Device(m)
            | Error::Encoder(m)
            | Error::NotSupported(m)
            | Error::Invalid(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Error::Io(e) => Some(e),
            _ => None,
        }
    }
}

const FACILITY_WIN32: u32 = 7;

/// Turns a Windows error into `wrap(message)`, except Win32 errors such as a full disk, which become `Io`.
pub(crate) fn win(e: windows::core::Error, wrap: impl FnOnce(String) -> Error) -> Error {
    let code = e.code().0 as u32;
    if (code >> 16) & 0x7FF == FACILITY_WIN32 {
        Error::Io(std::io::Error::from_raw_os_error((code & 0xFFFF) as i32))
    } else {
        wrap(e.message())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::core::HRESULT;

    #[test]
    fn win32_errors_become_io() {
        let disk_full = windows::core::Error::from_hresult(HRESULT(0x8007_0070_u32 as i32));
        let e = win(disk_full, Error::Device);
        assert!(matches!(&e, Error::Io(io) if io.raw_os_error() == Some(0x70)), "{e:?}");
    }

    #[test]
    fn other_errors_use_the_given_variant() {
        let no_interface = windows::core::Error::from_hresult(HRESULT(0x8000_4002_u32 as i32));
        assert!(matches!(win(no_interface, Error::Device), Error::Device(m) if !m.is_empty()));
    }
}
