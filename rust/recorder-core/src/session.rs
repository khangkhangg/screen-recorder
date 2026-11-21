use crate::frame_math::PixelSize;
use crate::layout::Layout;
use serde::{Deserialize, Serialize};
use std::fmt;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceEntry {
    pub file: String,
    pub kind: String,
    pub offset_seconds: f64,
    pub width: Option<i32>,
    pub height: Option<i32>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutChange {
    pub at_seconds: f64,
    pub layout: Layout,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PauseEntry {
    pub at_seconds: f64,
    pub duration_seconds: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionFile {
    pub version: i32,
    pub started_at: String,
    pub frame_size: PixelSize,
    pub frame_rate: i32,
    pub target_kind: String,
    pub sources: Vec<SourceEntry>,
    pub layout_changes: Vec<LayoutChange>,
    pub pauses: Vec<PauseEntry>,
    pub dropped_frames: i64,
    pub notes: Vec<String>,
}

#[derive(Debug)]
pub enum SessionError {
    NewerVersion,
    Invalid(String),
}

impl fmt::Display for SessionError {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        match self {
            SessionError::NewerVersion => write!(f, "This session file is newer than this app supports."),
            SessionError::Invalid(why) => write!(f, "This session file cannot be read: {why}."),
        }
    }
}

impl std::error::Error for SessionError {}

pub fn serialize_session(f: &SessionFile) -> String {
    serde_json::to_string_pretty(f).expect("a session file always converts to JSON")
}

pub fn deserialize_session(json: &str) -> Result<SessionFile, SessionError> {
    let value: serde_json::Value = serde_json::from_str(json).map_err(|e| SessionError::Invalid(e.to_string()))?;
    if value.get("version").and_then(|v| v.as_i64()).is_some_and(|v| v > 1) {
        return Err(SessionError::NewerVersion);
    }
    serde_json::from_value(value).map_err(|e| SessionError::Invalid(e.to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::layout::CameraShape;

    const EXAMPLE: &str = r#"
{
  "version": 1,
  "startedAt": "2026-11-21T15:06:39.6718465+07:00",
  "frameSize": {
    "width": 3840,
    "height": 2160
  },
  "frameRate": 30,
  "targetKind": "monitor",
  "sources": [
    {
      "file": "final.mp4",
      "kind": "final",
      "offsetSeconds": 0,
      "width": 3840,
      "height": 2160
    },
    {
      "file": "screen.mp4",
      "kind": "screen",
      "offsetSeconds": 0,
      "width": 1920,
      "height": 1080
    },
    {
      "file": "webcam.mp4",
      "kind": "webcam",
      "offsetSeconds": 0,
      "width": 640,
      "height": 480
    },
    {
      "file": "system.wav",
      "kind": "system",
      "offsetSeconds": 0,
      "width": null,
      "height": null
    }
  ],
  "layoutChanges": [
    {
      "atSeconds": 0,
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
      }
    }
  ],
  "pauses": [],
  "droppedFrames": 26,
  "notes": [
    "screen.mp4 scaled to 1920x1080",
    "webcam lost at 1.1 s"
  ]
}
"#;

    fn sample() -> SessionFile {
        SessionFile {
            version: 1,
            started_at: "2026-11-20T10:00:00+07:00".into(),
            frame_size: PixelSize { width: 1920, height: 1080 },
            frame_rate: 60,
            target_kind: "monitor".into(),
            sources: vec![SourceEntry { file: "screen.mp4".into(), kind: "screen".into(), offset_seconds: 0.0, width: Some(1920), height: Some(1080) }],
            layout_changes: vec![LayoutChange { at_seconds: 5.0, layout: Layout::default() }],
            pauses: vec![],
            dropped_frames: 0,
            notes: vec![],
        }
    }

    #[test]
    fn session_round_trips() {
        let f = sample();
        assert_eq!(f, deserialize_session(&serialize_session(&f)).unwrap());
    }

    #[test]
    fn session_uses_plain_names() {
        let json = serialize_session(&sample());
        assert!(json.contains("\"shape\": \"Circle\""));
        assert!(json.contains("\"version\": 1,"));
        assert_eq!(sample().layout_changes[0].layout.camera.shape, CameraShape::Circle);
    }

    #[test]
    fn newer_version_is_refused() {
        assert!(matches!(deserialize_session("{\"version\": 2}"), Err(SessionError::NewerVersion)));
    }

    #[test]
    fn reads_a_session_file_written_by_the_csharp_app() {
        let example = EXAMPLE.trim().replace("\r\n", "\n");
        let f = deserialize_session(&example).unwrap();
        assert_eq!(f.version, 1);
        let written = serialize_session(&f);
        // .NET writes whole-number doubles as `0`, serde_json as `0.0`, so only these 2 fields differ in text.
        assert_eq!(written.replace("\"offsetSeconds\": 0.0", "\"offsetSeconds\": 0").replace("\"atSeconds\": 0.0", "\"atSeconds\": 0"), example);
        assert_eq!(f, deserialize_session(&written).unwrap());
    }
}
