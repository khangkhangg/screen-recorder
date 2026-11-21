use std::time::Duration;

use recorder_core::layout::{CameraShape, Corner, Layout};

#[derive(Debug, Clone, PartialEq)]
pub enum Command {
    List(ListKind),
    Screenshot(ShotArgs),
    Record(RecordArgs),
    Help,
    Version,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum ListKind {
    Monitors,
    Windows,
    Cameras,
    Mics,
}

/// Monitor numbers are 1-based, in the order `list monitors` prints.
#[derive(Debug, Clone, PartialEq)]
pub enum Target {
    Monitor(usize),
    Window(String),
    Region { monitor: usize, x: i32, y: i32, width: i32, height: i32 },
}

#[derive(Debug, Clone, PartialEq)]
pub struct ShotArgs {
    pub target: Target,
    pub out: Option<String>,
    pub copy: bool,
}

#[derive(Debug, Clone, PartialEq)]
pub struct RecordArgs {
    pub target: Target,
    pub out: Option<String>,
    pub fps: i32,
    /// `Some(None)` is `--mic` with no id: the default device.
    pub mic: Option<Option<String>>,
    pub system_audio: bool,
    pub camera: Option<Option<String>>,
    pub layout: Layout,
    pub no_final: bool,
    pub seconds: Option<Duration>,
}

type Args<'a> = std::iter::Peekable<std::slice::Iter<'a, String>>;

pub fn parse(args: &[String]) -> Result<Command, String> {
    let Some(command) = args.first() else {
        return Err("No command given. Run `screenrec --help` to see the commands.".into());
    };
    let mut rest = args[1..].iter().peekable();
    match command.as_str() {
        "--help" | "-h" | "help" => Ok(Command::Help),
        "--version" => Ok(Command::Version),
        "list" => list(&args[1..]),
        "screenshot" => screenshot(&mut rest),
        "record" => record(&mut rest),
        _ => Err(format!("Unknown command \"{command}\". Run `screenrec --help` to see the commands.")),
    }
}

fn list(args: &[String]) -> Result<Command, String> {
    let kind = match args {
        [k] if k == "monitors" => ListKind::Monitors,
        [k] if k == "windows" => ListKind::Windows,
        [k] if k == "cameras" => ListKind::Cameras,
        [k] if k == "mics" => ListKind::Mics,
        [k] => return Err(format!("Unknown list \"{k}\". Use monitors, windows, cameras or mics.")),
        _ => return Err("`list` takes one of monitors, windows, cameras or mics.".into()),
    };
    Ok(Command::List(kind))
}

fn screenshot(args: &mut Args) -> Result<Command, String> {
    let (mut target, mut out, mut copy) = (TargetFlags::default(), None, true);
    let mut seen = Vec::new();
    while let Some(flag) = args.next() {
        once(flag, &mut seen)?;
        if target.take(flag, args)? {
            continue;
        }
        match flag.as_str() {
            "--out" => out = Some(value(flag, args)?),
            "--no-copy" => copy = false,
            _ => return Err(unknown(flag, "screenshot")),
        }
    }
    Ok(Command::Screenshot(ShotArgs { target: target.finish()?, out, copy }))
}

fn record(args: &mut Args) -> Result<Command, String> {
    let mut target = TargetFlags::default();
    let (mut out, mut fps, mut mic, mut system_audio, mut camera, mut no_final, mut seconds) =
        (None, 60, None, false, None, false, None);
    let mut layout = Layout::default();
    layout.camera.visible = false;
    let mut seen = Vec::new();
    while let Some(flag) = args.next() {
        once(flag, &mut seen)?;
        if target.take(flag, args)? {
            continue;
        }
        match flag.as_str() {
            "--out" => out = Some(value(flag, args)?),
            "--fps" => {
                fps = match value(flag, args)?.as_str() {
                    "30" => 30,
                    "60" => 60,
                    v => return Err(format!("--fps must be 30 or 60, not \"{v}\".")),
                }
            }
            "--mic" => mic = Some(optional(args)),
            "--system-audio" => system_audio = true,
            "--camera" => {
                camera = Some(optional(args));
                layout.camera.visible = true;
            }
            "--camera-shape" => {
                layout.camera.shape = match value(flag, args)?.as_str() {
                    "circle" => CameraShape::Circle,
                    "rounded" => CameraShape::RoundedSquare,
                    "square" => CameraShape::Square,
                    v => return Err(format!("--camera-shape must be circle, rounded or square, not \"{v}\".")),
                }
            }
            "--camera-pos" => {
                let v = value(flag, args)?;
                let [x, y, size] = numbers::<f32, 3>(flag, &v, "X,Y,SIZE, such as 0.75,0.7,0.25")?;
                if ![x, y, size].iter().all(|n| (0.0..=1.0).contains(n)) {
                    return Err(format!("--camera-pos takes fractions from 0 to 1, not \"{v}\"."));
                }
                (layout.camera.x, layout.camera.y, layout.camera.size) = (x, y, size);
            }
            "--logo" => {
                layout.logo.path = Some(value(flag, args)?);
                layout.logo.visible = true;
            }
            "--logo-corner" => {
                layout.logo.corner = match value(flag, args)?.as_str() {
                    "tl" => Corner::TopLeft,
                    "tr" => Corner::TopRight,
                    "bl" => Corner::BottomLeft,
                    "br" => Corner::BottomRight,
                    v => return Err(format!("--logo-corner must be tl, tr, bl or br, not \"{v}\".")),
                }
            }
            "--no-final" => no_final = true,
            "--seconds" => {
                let v = value(flag, args)?;
                // A day at most, so the deadline can never overflow.
                match v.parse::<f64>().ok().filter(|n| *n > 0.0 && *n <= 86_400.0) {
                    Some(n) => seconds = Duration::try_from_secs_f64(n).ok(),
                    None => return Err(format!("--seconds needs a number above 0 and at most 86400, not \"{v}\".")),
                }
            }
            _ => return Err(unknown(flag, "record")),
        }
    }
    let target = target.finish()?;
    Ok(Command::Record(RecordArgs { target, out, fps, mic, system_audio, camera, layout, no_final, seconds }))
}

#[derive(Default)]
struct TargetFlags {
    monitor: Option<usize>,
    window: Option<String>,
    region: Option<[i32; 4]>,
}

impl TargetFlags {
    /// Takes a target flag and its value, and returns false for any other flag.
    fn take(&mut self, flag: &str, args: &mut Args) -> Result<bool, String> {
        match flag {
            "--monitor" => {
                let v = value(flag, args)?;
                match v.parse::<usize>() {
                    Ok(n) if n >= 1 => self.monitor = Some(n),
                    _ => return Err(format!("--monitor needs a monitor number from 1 up, not \"{v}\".")),
                }
            }
            "--window" => self.window = Some(value(flag, args)?),
            "--region" => {
                self.region = Some(numbers::<i32, 4>(flag, &value(flag, args)?, "X,Y,W,H, such as 0,0,1280,720")?)
            }
            _ => return Ok(false),
        }
        Ok(true)
    }

    fn finish(self) -> Result<Target, String> {
        match (self.monitor, self.window, self.region) {
            (None, None, None) => {
                Err("A target is required: --monitor N, --window \"TITLE\" or --region X,Y,W,H.".into())
            }
            (_, Some(_), Some(_)) | (Some(_), Some(_), _) => {
                Err("Give only one target: --monitor N, --window \"TITLE\" or --region X,Y,W,H.".into())
            }
            (_, Some(title), None) => Ok(Target::Window(title)),
            (monitor, None, Some([x, y, width, height])) => {
                Ok(Target::Region { monitor: monitor.unwrap_or(1), x, y, width, height })
            }
            (Some(n), None, None) => Ok(Target::Monitor(n)),
        }
    }
}

fn once<'a>(flag: &'a str, seen: &mut Vec<&'a str>) -> Result<(), String> {
    if seen.contains(&flag) {
        return Err(format!("{flag} is given twice."));
    }
    seen.push(flag);
    Ok(())
}

/// A following flag is not a value, so `--out --no-copy` names the missing value.
fn value(flag: &str, args: &mut Args) -> Result<String, String> {
    args.next_if(|a| !a.starts_with("--")).cloned().ok_or_else(|| format!("{flag} needs a value."))
}

/// The value after a flag whose value may be left out, such as `--mic [ID]`.
fn optional(args: &mut Args) -> Option<String> {
    args.next_if(|a| !a.starts_with("--")).cloned()
}

fn numbers<T: std::str::FromStr, const N: usize>(flag: &str, v: &str, form: &str) -> Result<[T; N], String> {
    let parsed: Option<Vec<T>> = v.split(',').map(|n| n.trim().parse().ok()).collect();
    parsed
        .and_then(|p| p.try_into().ok())
        .ok_or_else(|| format!("{flag} needs {form}, not \"{v}\"."))
}

fn unknown(flag: &str, command: &str) -> String {
    format!("Unknown option \"{flag}\" for {command}. Run `screenrec --help` to see the options.")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run(line: &str) -> Result<Command, String> {
        parse(&line.split_whitespace().map(String::from).collect::<Vec<_>>())
    }

    fn err(line: &str) -> String {
        run(line).expect_err(line)
    }

    fn record(target: Target) -> RecordArgs {
        let mut layout = Layout::default();
        layout.camera.visible = false;
        RecordArgs { target, out: None, fps: 60, mic: None, system_audio: false, camera: None, layout, no_final: false, seconds: None }
    }

    #[test]
    fn help_and_version() {
        assert_eq!(Ok(Command::Help), run("--help"));
        assert_eq!(Ok(Command::Version), run("--version"));
    }

    #[test]
    fn list_kinds() {
        assert_eq!(Ok(Command::List(ListKind::Monitors)), run("list monitors"));
        assert_eq!(Ok(Command::List(ListKind::Windows)), run("list windows"));
        assert_eq!(Ok(Command::List(ListKind::Cameras)), run("list cameras"));
        assert_eq!(Ok(Command::List(ListKind::Mics)), run("list mics"));
        assert!(err("list printers").contains("printers"));
        assert!(err("list").contains("monitors"));
    }

    #[test]
    fn screenshot_flags() {
        let shot = ShotArgs { target: Target::Monitor(2), out: Some(r"C:\shots".into()), copy: false };
        assert_eq!(Ok(Command::Screenshot(shot)), run(r"screenshot --monitor 2 --out C:\shots --no-copy"));
        let shot = ShotArgs { target: Target::Region { monitor: 1, x: 0, y: 0, width: 320, height: 200 }, out: None, copy: true };
        assert_eq!(Ok(Command::Screenshot(shot)), run("screenshot --region 0,0,320,200"));
    }

    #[test]
    fn window_title_is_one_argument() {
        let args: Vec<String> = ["screenshot", "--window", "Untitled - Notepad"].map(String::from).into();
        let shot = ShotArgs { target: Target::Window("Untitled - Notepad".into()), out: None, copy: true };
        assert_eq!(Ok(Command::Screenshot(shot)), parse(&args));
    }

    #[test]
    fn record_defaults() {
        assert_eq!(Ok(Command::Record(record(Target::Monitor(1)))), run("record --monitor 1"));
    }

    #[test]
    fn record_all_flags() {
        let mut want = record(Target::Region { monitor: 2, x: 10, y: 20, width: 1280, height: 720 });
        want.out = Some("out".into());
        want.fps = 30;
        want.mic = Some(Some("mic-1".into()));
        want.system_audio = true;
        want.camera = Some(None);
        want.layout.camera.visible = true;
        want.layout.camera.shape = CameraShape::Square;
        want.layout.camera.x = 0.5;
        want.layout.camera.y = 0.25;
        want.layout.camera.size = 0.2;
        want.layout.logo.visible = true;
        want.layout.logo.path = Some("logo.png".into());
        want.layout.logo.corner = Corner::TopLeft;
        want.no_final = true;
        want.seconds = Some(Duration::from_secs_f64(2.5));
        let line = "record --region 10,20,1280,720 --monitor 2 --out out --fps 30 --mic mic-1 --system-audio --camera \
                    --camera-shape square --camera-pos 0.5,0.25,0.2 --logo logo.png --logo-corner tl --no-final --seconds 2.5";
        assert_eq!(Ok(Command::Record(want)), run(line));
    }

    #[test]
    fn mic_without_id_takes_the_default_device() {
        let mut want = record(Target::Monitor(1));
        want.mic = Some(None);
        assert_eq!(Ok(Command::Record(want)), run("record --mic --monitor 1"));
    }

    #[test]
    fn bad_input_names_the_problem() {
        assert!(err("record").contains("target"));
        assert!(err("screenshot --monitor 1 --window Notepad").contains("one target"));
        assert!(err("record --monitor x").contains("\"x\""));
        assert!(err("record --monitor 0").contains("--monitor"));
        assert!(err("record --region 1,2,3").contains("--region"));
        assert!(err("record --monitor 1 --fps 45").contains("45"));
        assert!(err("record --monitor 1 --bogus").contains("--bogus"));
        assert!(err("dance").contains("dance"));
        assert!(err("record --monitor 1 --seconds -1").contains("--seconds"));
        assert!(err("record --monitor 1 --camera-pos 0.5,2,0.2").contains("--camera-pos"));
        assert!(err("record --monitor 1 --camera-shape star").contains("star"));
        assert!(err("record --monitor 1 --logo-corner middle").contains("middle"));
        assert!(err("record --monitor 1 --out").contains("--out"));
        assert!(err("screenshot --monitor 1 --fps 30").contains("--fps"));
    }

    #[test]
    fn seconds_must_be_from_above_0_to_a_day() {
        for v in ["1e20", "-1", "0", "nan", "abc", "86401"] {
            assert!(err(&format!("record --monitor 1 --seconds {v}")).contains("--seconds"), "{v}");
        }
        let mut want = record(Target::Monitor(1));
        want.seconds = Some(Duration::from_secs(86_400));
        assert_eq!(Ok(Command::Record(want)), run("record --monitor 1 --seconds 86400"));
    }

    #[test]
    fn a_flag_given_twice_is_refused() {
        assert!(err("record --monitor 1 --fps 30 --fps 60").contains("--fps is given twice"));
        assert!(err("screenshot --monitor 1 --no-copy --no-copy").contains("--no-copy is given twice"));
        assert!(err("screenshot --monitor 1 --monitor 2").contains("--monitor is given twice"));
    }

    #[test]
    fn a_flag_is_not_taken_as_a_value() {
        assert!(err("screenshot --monitor 1 --out --no-copy").contains("--out needs a value"));
    }
}
