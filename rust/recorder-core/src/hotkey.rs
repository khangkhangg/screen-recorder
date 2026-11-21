use serde::{Deserialize, Serialize};
use std::fmt;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Hotkey {
    pub ctrl: bool,
    pub shift: bool,
    pub alt: bool,
    pub win: bool,
    pub virtual_key: u32,
}

impl Hotkey {
    /// Reads text like "Ctrl+Shift+R" or "win + F12"; needs at least one modifier and one key.
    pub fn parse(text: &str) -> Option<Hotkey> {
        let parts: Vec<&str> = text.split('+').map(str::trim).collect();
        let (key, modifiers) = parts.split_last()?;
        let mut h = Hotkey { ctrl: false, shift: false, alt: false, win: false, virtual_key: 0 };
        for m in modifiers {
            match m.to_ascii_lowercase().as_str() {
                "ctrl" => h.ctrl = true,
                "shift" => h.shift = true,
                "alt" => h.alt = true,
                "win" => h.win = true,
                _ => return None,
            }
        }
        if !(h.ctrl || h.shift || h.alt || h.win) {
            return None;
        }
        let key = key.to_ascii_uppercase();
        h.virtual_key = match key.as_bytes() {
            [c @ (b'A'..=b'Z' | b'0'..=b'9')] => u32::from(*c),
            [b'F', digits @ ..] if digits.len() <= 2 => match key[1..].parse::<u32>() {
                Ok(n @ 1..=12) => 0x6F + n,
                _ => return None,
            },
            _ => return None,
        };
        Some(h)
    }
}

impl fmt::Display for Hotkey {
    fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
        for (on, name) in [(self.ctrl, "Ctrl+"), (self.shift, "Shift+"), (self.alt, "Alt+"), (self.win, "Win+")] {
            if on {
                f.write_str(name)?;
            }
        }
        match self.virtual_key {
            k @ 0x70..=0x7B => write!(f, "F{}", k - 0x6F),
            k => write!(f, "{}", char::from_u32(k).unwrap_or('?')),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses() {
        for (text, key) in [("Ctrl+Shift+R", 0x52), ("ctrl + alt + 5", 0x35), ("Win+F12", 0x7B)] {
            assert_eq!(Hotkey::parse(text).unwrap().virtual_key, key, "{text}");
        }
    }

    #[test]
    fn round_trips() {
        let h = Hotkey::parse("Win+Alt+Shift+Ctrl+F1").unwrap();
        assert_eq!("Ctrl+Shift+Alt+Win+F1", h.to_string());
    }

    #[test]
    fn rejects() {
        for text in ["R", "Ctrl+", "Ctrl+F13", "Ctrl+RR"] {
            assert!(Hotkey::parse(text).is_none(), "{text}");
        }
    }

    #[test]
    fn json_form_is_unchanged() {
        let json = serde_json::to_string(&Hotkey::parse("Ctrl+Shift+R").unwrap()).unwrap();
        assert_eq!(json, r#"{"ctrl":true,"shift":true,"alt":false,"win":false,"virtualKey":82}"#);
    }
}
