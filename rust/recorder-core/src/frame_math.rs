use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PixelSize {
    pub width: i32,
    pub height: i32,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RectF {
    pub x: f32,
    pub y: f32,
    pub width: f32,
    pub height: f32,
}

pub fn to_even(s: PixelSize) -> PixelSize {
    PixelSize { width: s.width & !1, height: s.height & !1 }
}

pub fn fit_within(source: PixelSize, max: PixelSize) -> PixelSize {
    let (mut w, mut h) = (i64::from(source.width), i64::from(source.height));
    let (mw, mh) = (i64::from(max.width), i64::from(max.height));
    if w > mw || h > mh {
        if mw * h <= mh * w {
            h = h * mw / w;
            w = mw;
        } else {
            w = w * mh / h;
            h = mh;
        }
    }
    // Both values are at most the i32 inputs, so the casts cannot truncate.
    to_even(PixelSize { width: w as i32, height: h as i32 })
}

pub fn letterbox(source: PixelSize, frame: PixelSize) -> RectF {
    let (fw, fh) = (frame.width as f32, frame.height as f32);
    let scale = (fw / source.width as f32).min(fh / source.height as f32);
    let (w, h) = (source.width as f32 * scale, source.height as f32 * scale);
    RectF { x: (fw - w) / 2.0, y: (fh - h) / 2.0, width: w, height: h }
}

pub fn bitrate(size: PixelSize, frame_rate: i32) -> i32 {
    (i64::from(size.width) * i64::from(size.height) * i64::from(frame_rate) / 10).min(i64::from(i32::MAX)) as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(width: i32, height: i32) -> PixelSize {
        PixelSize { width, height }
    }

    #[test]
    fn to_even_rounds_down() {
        assert_eq!(p(1920, 1080), to_even(p(1921, 1081)));
    }

    #[test]
    fn fit_within_scales_ultrawide_down() {
        assert_eq!(p(4096, 1728), fit_within(p(5120, 2160), p(4096, 2304)));
    }

    #[test]
    fn fit_within_exact_for_awkward_ratio() {
        assert_eq!(p(1720, 720), fit_within(p(3440, 1440), p(1720, 2304)));
    }

    #[test]
    fn fit_within_never_scales_up() {
        assert_eq!(p(1280, 720), fit_within(p(1280, 720), p(4096, 2304)));
    }

    #[test]
    fn letterbox_adds_side_bars() {
        let r = letterbox(p(500, 500), p(1920, 1080));
        assert_eq!(RectF { x: 420.0, y: 0.0, width: 1080.0, height: 1080.0 }, r);
    }

    #[test]
    fn bitrate_1080p60() {
        assert_eq!(12_441_600, bitrate(p(1920, 1080), 60));
    }

    #[test]
    fn bitrate_saturates_instead_of_overflowing() {
        assert_eq!(i32::MAX, bitrate(p(15360, 8640), 240));
    }
}
