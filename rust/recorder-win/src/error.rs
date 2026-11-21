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
// File not found, path not found, access denied, write protect, not ready, sharing violation,
// handle disk full, disk full.
const FILE_ERRORS: [u32; 8] = [2, 3, 5, 19, 21, 32, 39, 112];

/// Turns a Windows error into `variant(sentence + system message)`. Never returns `Io`.
pub(crate) fn win(e: windows::core::Error, variant: fn(String) -> Error, sentence: &str) -> Error {
    variant(format!("{sentence} {}", e.message()))
}

/// Like `win`, for file operations: disk and file errors, such as a full disk, become `Io`.
pub(crate) fn win_file(e: windows::core::Error, variant: fn(String) -> Error, sentence: &str) -> Error {
    let code = e.code().0 as u32;
    if (code >> 16) & 0x7FF == FACILITY_WIN32 && FILE_ERRORS.contains(&(code & 0xFFFF)) {
        Error::Io(std::io::Error::from_raw_os_error((code & 0xFFFF) as i32))
    } else {
        win(e, variant, sentence)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use windows::core::HRESULT;

    fn hr(code: u32) -> windows::core::Error {
        windows::core::Error::from_hresult(HRESULT(code as i32))
    }

    #[test]
    fn invalid_arg_keeps_the_callers_sentence() {
        let e = win(hr(0x8007_0057), Error::Device, "The graphics device could not be created.");
        assert!(matches!(&e, Error::Device(_)), "{e:?}");
        let text = e.to_string();
        assert!(text.starts_with("The graphics device could not be created. ") && text.len() > 45, "{text}");
    }

    #[test]
    fn disk_full_in_a_file_operation_becomes_io() {
        let e = win_file(hr(0x8007_0070), Error::Encoder, "The recording could not be written.");
        assert!(matches!(&e, Error::Io(io) if io.raw_os_error() == Some(112)), "{e:?}");
    }

    #[test]
    fn access_denied_outside_files_is_not_io() {
        let e = win(hr(0x8007_0005), Error::Device, "The graphics device could not be created.");
        assert!(matches!(&e, Error::Device(_)), "{e:?}");
    }
}
