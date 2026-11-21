use std::path::PathBuf;

use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::UI::Shell::{FOLDERID_Videos, SHGetKnownFolderPath, KF_FLAG_DEFAULT};

/// The user's Videos folder where Windows keeps it, also after it was moved, such as to OneDrive.
pub fn videos_folder() -> Option<PathBuf> {
    let path = unsafe { SHGetKnownFolderPath(&FOLDERID_Videos, KF_FLAG_DEFAULT, None) }.ok()?;
    let text = unsafe { path.to_string() };
    unsafe { CoTaskMemFree(Some(path.0 as _)) };
    text.ok().map(PathBuf::from)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn videos_folder_is_a_full_path() {
        let folder = videos_folder().unwrap();
        assert!(folder.is_absolute(), "{folder:?}");
    }
}
