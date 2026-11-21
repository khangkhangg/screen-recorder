use crate::hotkey::Hotkey;
use crate::layout::Layout;
use serde::{Deserialize, Serialize};
use std::path::Path;

// The last region picked: the monitor's device name and the rectangle in that monitor's pixels.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SavedRegion {
    pub monitor: String,
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Settings {
    pub output_folder: String,
    pub record_hotkey: Hotkey,
    pub screenshot_hotkey: Hotkey,
    pub countdown: bool,
    pub frame_rate: i32,
    pub webcam_id: Option<String>,
    pub mic_id: Option<String>,
    pub mic: bool,
    pub system_audio: bool,
    pub layout: Layout,
    pub region: Option<SavedRegion>,
}

fn default_output_folder() -> String {
    match std::env::var("USERPROFILE") {
        Ok(profile) => format!("{profile}\\Videos\\ScreenRecorder"),
        Err(_) => "ScreenRecorder".into(),
    }
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            output_folder: default_output_folder(),
            record_hotkey: Hotkey { ctrl: true, shift: true, alt: false, win: false, virtual_key: 0x52 },
            screenshot_hotkey: Hotkey { ctrl: true, shift: true, alt: false, win: false, virtual_key: 0x53 },
            countdown: true,
            frame_rate: 60,
            webcam_id: None,
            mic_id: None,
            mic: true,
            system_audio: true,
            layout: Layout::default(),
            region: None,
        }
    }
}

// shortcut: a file with a missing required field loads as all defaults, where the C# app only replaces the unusable fields; replace if users hit it.
fn load_from_str(text: &str) -> Settings {
    match serde_json::from_str::<Settings>(text) {
        Ok(s) if s.frame_rate > 0 => s,
        _ => Settings::default(),
    }
}

impl Settings {
    pub fn load(path: &Path) -> Settings {
        std::fs::read_to_string(path).map(|t| load_from_str(&t)).unwrap_or_default()
    }

    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        std::fs::write(path, serde_json::to_string_pretty(self).expect("settings always convert to JSON"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    // Output of the C# `Settings.Default.Save`, with the user's folder name replaced by `someone`.
    const CSHARP_DEFAULT: &str = r#"
{
  "outputFolder": "C:\\Users\\someone\\Videos\\ScreenRecorder",
  "recordHotkey": {
    "ctrl": true,
    "shift": true,
    "alt": false,
    "win": false,
    "virtualKey": 82
  },
  "screenshotHotkey": {
    "ctrl": true,
    "shift": true,
    "alt": false,
    "win": false,
    "virtualKey": 83
  },
  "countdown": true,
  "frameRate": 60,
  "webcamId": null,
  "micId": null,
  "mic": true,
  "systemAudio": true,
  "layout": {
    "camera": {
      "visible": true,
      "x": 0.02,
      "y": 0.73,
      "size": 0.25,
      "shape": "Circle"
    },
    "logo": {
      "visible": false,
      "corner": "BottomRight",
      "size": 0.08,
      "path": null
    }
  },
  "region": null
}"#;

    fn temp(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!("recorder-core-{}-{name}", std::process::id()))
    }

    #[test]
    fn round_trips_and_creates_folder() {
        let dir = temp("round");
        let path = dir.join("settings.json");
        let s = Settings {
            frame_rate: 30,
            mic: false,
            region: Some(SavedRegion { monitor: r"\\.\DISPLAY1".into(), x: 10, y: 20, width: 300, height: 200 }),
            ..Settings::default()
        };
        s.save(&path).unwrap();
        assert_eq!(s, Settings::load(&path));
        std::fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn corrupt_or_missing_gives_default() {
        let path = temp("corrupt.json");
        std::fs::write(&path, "{{{").unwrap();
        assert_eq!(Settings::default(), Settings::load(&path));
        std::fs::remove_file(&path).unwrap();
        assert_eq!(Settings::default(), Settings::load(&path));
    }

    #[test]
    fn empty_object_gives_default() {
        assert_eq!(Settings::default(), load_from_str("{}"));
    }

    #[test]
    fn old_file_without_a_region_still_loads() {
        let old = Settings { frame_rate: 30, ..Settings::default() };
        let mut json = serde_json::to_value(&old).unwrap();
        json.as_object_mut().unwrap().remove("region");
        let loaded = load_from_str(&json.to_string());
        assert_eq!(old, loaded);
        assert!(loaded.region.is_none());
    }

    #[test]
    fn empty_layout_gives_default() {
        let mut json = serde_json::to_value(Settings::default()).unwrap();
        json["layout"] = serde_json::json!({});
        assert_eq!(Settings::default(), load_from_str(&json.to_string()));
    }

    #[test]
    fn reads_a_settings_file_written_by_the_csharp_app() {
        let csharp = CSHARP_DEFAULT.trim().replace("\r\n", "\n");
        let s = load_from_str(&csharp);
        assert_eq!(s.frame_rate, 60);
        assert_eq!(s.record_hotkey, Hotkey { ctrl: true, shift: true, alt: false, win: false, virtual_key: 82 });
        assert_eq!(s.output_folder, r"C:\Users\someone\Videos\ScreenRecorder");
        assert_eq!(serde_json::to_string_pretty(&s).unwrap(), csharp);
    }
}
