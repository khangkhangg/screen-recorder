use crate::frame_math::{PixelSize, RectF};
use serde::{Deserialize, Serialize, Serializer};

// .NET writes whole-number floats without a decimal point, so both apps write the same text.
fn whole_number<S: Serializer>(v: &f32, s: S) -> Result<S::Ok, S::Error> {
    if v.fract() == 0.0 && v.abs() < 1e7 {
        s.serialize_i64(*v as i64)
    } else {
        s.serialize_f32(*v)
    }
}

const MARGIN: f32 = 0.02;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum CameraShape {
    Circle,
    RoundedSquare,
    Square,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum Corner {
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CameraItem {
    pub visible: bool,
    #[serde(serialize_with = "whole_number")]
    pub x: f32,
    #[serde(serialize_with = "whole_number")]
    pub y: f32,
    #[serde(serialize_with = "whole_number")]
    pub size: f32,
    pub shape: CameraShape,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LogoItem {
    pub visible: bool,
    pub corner: Corner,
    #[serde(serialize_with = "whole_number")]
    pub size: f32,
    pub path: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Layout {
    pub camera: CameraItem,
    pub logo: LogoItem,
}

impl Default for Layout {
    fn default() -> Self {
        Layout {
            camera: CameraItem { visible: true, x: MARGIN, y: 1.0 - MARGIN - 0.25, size: 0.25, shape: CameraShape::Circle },
            logo: LogoItem { visible: false, corner: Corner::BottomRight, size: 0.08, path: None },
        }
    }
}

// Moves a start position into 0..=max, where max is never below 0.
fn clamp(v: f32, max: f32) -> f32 {
    v.max(0.0).min(max.max(0.0))
}

pub fn camera_rect(c: &CameraItem, frame: PixelSize) -> RectF {
    let (fw, fh) = (frame.width as f32, frame.height as f32);
    let side = c.size * fh;
    RectF { x: clamp(c.x * fw, fw - side), y: clamp(c.y * fh, fh - side), width: side, height: side }
}

pub fn logo_rect(l: &LogoItem, frame: PixelSize, logo_pixels: PixelSize) -> RectF {
    let (fw, fh) = (frame.width as f32, frame.height as f32);
    let h = l.size * fh;
    let w = if logo_pixels.height > 0 { h * logo_pixels.width as f32 / logo_pixels.height as f32 } else { 0.0 };
    let m = MARGIN * fh;
    let right = matches!(l.corner, Corner::TopRight | Corner::BottomRight);
    let bottom = matches!(l.corner, Corner::BottomLeft | Corner::BottomRight);
    RectF {
        x: clamp(if right { fw - w - m } else { m }, fw - w),
        y: clamp(if bottom { fh - h - m } else { m }, fh - h),
        width: w,
        height: h,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(width: i32, height: i32) -> PixelSize {
        PixelSize { width, height }
    }

    fn cam(x: f32, y: f32, size: f32, shape: CameraShape) -> CameraItem {
        CameraItem { visible: true, x, y, size, shape }
    }

    fn logo(size: f32) -> LogoItem {
        LogoItem { visible: true, corner: Corner::BottomRight, size, path: Some("x.png".into()) }
    }

    fn near(e: RectF, a: RectF) {
        assert!((e.x - a.x).abs() < 0.01, "x {} vs {}", e.x, a.x);
        assert!((e.y - a.y).abs() < 0.01, "y {} vs {}", e.y, a.y);
        assert!((e.width - a.width).abs() < 0.01, "width {} vs {}", e.width, a.width);
        assert!((e.height - a.height).abs() < 0.01, "height {} vs {}", e.height, a.height);
    }

    #[test]
    fn whole_numbers_are_written_like_dotnet() {
        let json = serde_json::to_string(&cam(0.0, 1.0, 0.25, CameraShape::Circle)).unwrap();
        assert!(json.contains(r#""x":0,"y":1,"size":0.25"#), "{json}");
        for (size, text) in [(0.73, "0.73"), (0.25, "0.25"), (0.08, "0.08")] {
            let json = serde_json::to_string(&logo(size)).unwrap();
            assert!(json.contains(&format!(r#""size":{text}"#)), "{json}");
        }
    }

    #[test]
    fn camera_rect_uses_fractions() {
        let r = camera_rect(&cam(0.1, 0.1, 0.25, CameraShape::Circle), p(1920, 1080));
        near(RectF { x: 192.0, y: 108.0, width: 270.0, height: 270.0 }, r);
    }

    #[test]
    fn camera_rect_is_clamped_inside_frame() {
        let r = camera_rect(&cam(0.95, 0.95, 0.25, CameraShape::Square), p(1920, 1080));
        assert!((r.x + r.width - 1920.0).abs() < 0.01);
        assert!((r.y + r.height - 1080.0).abs() < 0.01);
    }

    #[test]
    fn logo_rect_bottom_right_keeps_proportions() {
        let r = logo_rect(&logo(0.08), p(1920, 1080), p(400, 200));
        near(RectF { x: 1920.0 - 21.6 - 172.8, y: 1080.0 - 21.6 - 86.4, width: 172.8, height: 86.4 }, r);
    }

    fn finite(r: RectF) -> bool {
        [r.x, r.y, r.width, r.height].iter().all(|v| v.is_finite())
    }

    #[test]
    fn logo_rect_with_empty_logo_is_finite() {
        assert!(finite(logo_rect(&logo(0.08), p(1920, 1080), p(0, 0))));
        assert!(finite(logo_rect(&logo(0.08), p(0, 0), p(0, 0))));
    }

    #[test]
    fn camera_rect_larger_than_frame_does_not_panic() {
        let r = camera_rect(&cam(0.1, 0.1, 0.9, CameraShape::Square), p(400, 1080));
        assert!(r.x >= 0.0 && r.y >= 0.0 && r.x <= 400.0 && r.y <= 1080.0);
    }

    #[test]
    fn logo_rect_wider_than_frame_does_not_panic() {
        let r = logo_rect(&logo(0.08), p(200, 1080), p(4000, 100));
        assert!(r.x >= 0.0 && r.y >= 0.0 && r.x <= 200.0 && r.y <= 1080.0);
    }
}
