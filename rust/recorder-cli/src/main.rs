mod args;

use std::io::{BufRead, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering::SeqCst};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::thread;
use std::time::{Duration, Instant};

use args::{Command, ListKind, RecordArgs, ShotArgs, Target};
use recorder_win::{
    find_window, init_process, monitors, on_console_stop, screenshot, videos_folder, windows, AudioSource,
    CaptureTarget, EncoderSupport, Error, PreflightResult, RecordingOptions, RecordingResult, RecordingSession,
    WebcamSource,
};
use serde_json::{json, Value};

const USAGE: &str = r#"screenrec records the screen and takes screenshots.

  screenrec list monitors|windows|cameras|mics
  screenrec screenshot --monitor N | --window "TITLE" | --region X,Y,W,H [--monitor N] [--out FOLDER] [--no-copy]
  screenrec record     --monitor N | --window "TITLE" | --region X,Y,W,H [--monitor N]
                       [--out FOLDER] [--fps 30|60] [--mic [ID]] [--system-audio] [--camera [ID]]
                       [--camera-shape circle|rounded|square] [--camera-pos X,Y,SIZE]
                       [--logo FILE] [--logo-corner tl|tr|bl|br] [--no-final] [--seconds N]
  screenrec --help | --version

Monitors are numbered from 1, in the order `list monitors` prints. --window matches part of a
window title, ignoring case. --region is in pixels of --monitor N (default 1). --camera-pos is
in fractions from 0 to 1 of the video. --seconds is at most 86400. A value cannot start with
--, so a window title such as "--foo" cannot be given. Files go to the Videos
folder's ScreenRecorder folder unless --out is given.

`record` runs until --seconds pass, Ctrl+C, or the line `stop` on standard input. The lines
`pause` and `resume` pause and resume it. It prints `recording` on standard error once started.

Every command prints 1 JSON object on standard output, except --help and --version, which print
plain text. Errors print nothing on standard output. Messages go to standard error.
Exit codes: 0 success, 1 failure while running, 2 bad arguments or a target that cannot be found."#;

/// An exit code and a message for standard error.
type Failure = (i32, String);

/// Set by the console handler, and read by the recording loop.
static STOP: AtomicBool = AtomicBool::new(false);
/// Set once the recording's files are finished and its result is printed.
static DONE: AtomicBool = AtomicBool::new(false);

fn main() {
    let argv: Vec<String> = std::env::args().skip(1).collect();
    let code = match args::parse(&argv).map(run) {
        Err(message) => {
            say(&message);
            2
        }
        Ok(Ok(())) => 0,
        Ok(Err((code, message))) => {
            say(&message);
            code
        }
    };
    DONE.store(true, SeqCst);
    std::process::exit(code);
}

/// Writes to standard output. A reader that closed the pipe is not a reason to abort.
fn print(text: &str) {
    let _ = writeln!(std::io::stdout().lock(), "{text}");
}

/// Writes to standard error, and ignores a closed pipe like `print`.
fn say(text: &str) {
    let _ = writeln!(std::io::stderr().lock(), "{text}");
}

fn run(command: Command) -> Result<(), Failure> {
    match command {
        // The 2 exceptions to the 1 JSON object rule: plain text for a person.
        Command::Help => print(USAGE),
        Command::Version => print(&format!("screenrec {}", env!("CARGO_PKG_VERSION"))),
        Command::List(kind) => {
            init_process().map_err(engine)?;
            print(&list(kind).to_string());
        }
        Command::Screenshot(a) => {
            init_process().map_err(engine)?;
            take_screenshot(a)?;
        }
        Command::Record(a) => {
            init_process().map_err(engine)?;
            record(a)?;
        }
    }
    Ok(())
}

/// A target the capture API refuses, such as a region off the screen, is a bad argument.
fn engine(e: Error) -> Failure {
    (if matches!(e, Error::CaptureUnavailable(_)) { 2 } else { 1 }, e.to_string())
}

fn list(kind: ListKind) -> Value {
    let pairs = |list: Vec<(String, String)>| -> Vec<Value> {
        list.into_iter().map(|(id, name)| json!({ "id": id, "name": name })).collect()
    };
    match kind {
        ListKind::Monitors => {
            let list: Vec<Value> = monitors()
                .iter()
                .enumerate()
                .map(|(i, m)| {
                    json!({ "index": i + 1, "name": m.name, "x": m.x, "y": m.y,
                            "width": m.width, "height": m.height, "primary": m.primary })
                })
                .collect();
            json!({ "monitors": list })
        }
        ListKind::Windows => {
            let list: Vec<Value> = windows().iter().map(|w| json!({ "title": w.title })).collect();
            json!({ "windows": list })
        }
        ListKind::Cameras => json!({ "cameras": pairs(WebcamSource::list()) }),
        ListKind::Mics => json!({ "mics": pairs(AudioSource::list_mics()) }),
    }
}

fn capture_target(target: &Target) -> Result<CaptureTarget, Failure> {
    let monitor = |n: usize| {
        let found = monitors().get(n - 1).map(|m| m.handle);
        found.ok_or_else(|| (2, format!("There is no monitor {n}. Run `screenrec list monitors` to see them.")))
    };
    Ok(match target {
        Target::Monitor(n) => CaptureTarget::Monitor(monitor(*n)?),
        &Target::Region { monitor: n, x, y, width, height } => {
            CaptureTarget::Region { monitor: monitor(n)?, x, y, width, height }
        }
        Target::Window(title) => {
            let found = if title.trim().is_empty() { Err(windows()) } else { find_window(title) };
            match found {
                Ok(w) => CaptureTarget::Window(w.handle),
                Err(matches) if matches.is_empty() => {
                    return Err((2, format!("No window matches \"{title}\". Run `screenrec list windows` to see them.")))
                }
                Err(matches) => {
                    let titles: Vec<String> = matches.iter().map(|w| format!("  {}", w.title)).collect();
                    let head = format!("{} windows match \"{title}\". Give more of the title:", matches.len());
                    return Err((2, format!("{head}\n{}", titles.join("\n"))));
                }
            }
        }
    })
}

fn output_root(out: Option<String>) -> PathBuf {
    out.map(PathBuf::from).unwrap_or_else(|| root_from(videos_folder()))
}

/// The default output root from the known Videos folder, or `%USERPROFILE%\Videos` without one.
fn root_from(known_videos: Option<PathBuf>) -> PathBuf {
    let videos = known_videos
        .or_else(|| std::env::var("USERPROFILE").ok().map(|profile| PathBuf::from(profile).join("Videos")));
    videos.unwrap_or_default().join("ScreenRecorder")
}

/// The notes the preflight result calls for, which the tool also prints on standard error.
fn preflight_notes(p: &PreflightResult, requested_fps: i32, write_final: bool) -> Vec<String> {
    let mut notes = Vec::new();
    if p.encoders == EncoderSupport::TooFewHardwareSessions && write_final {
        notes.push("Too few hardware encoder sessions are free, so final.mp4 is not written.".to_string());
    }
    let size = p.frame_size;
    if p.scaled_down {
        notes.push(format!("The video is scaled down to {}x{}, the largest size the encoder takes.", size.width, size.height));
    }
    let big = i64::from(size.width) * i64::from(size.height) > 2560 * 1440;
    if p.encoders != EncoderSupport::SoftwareOnly && p.frame_rate < requested_fps && big {
        notes.push("The screen is larger than 2560x1440, so it records at 30 frames per second.".to_string());
    }
    notes
}

/// The JSON text for a finished recording, or a failure when it saved no files. Notes in
/// `said` were printed on standard error at the start, so the failure leaves them out.
fn outcome(r: &RecordingResult, seconds: f64, said: &[String]) -> Result<String, Failure> {
    if r.files.is_empty() {
        let unsaid: Vec<&str> = r.notes.iter().filter(|n| !said.contains(n)).map(String::as_str).collect();
        return Err((1, format!("The recording saved no files. {}", unsaid.join(" ")).trim_end().to_string()));
    }
    Ok(json!({
        "folder": r.folder, "files": r.files, "droppedFrames": r.dropped_frames, "notes": r.notes,
        "frameRate": r.frame_rate, "frameWidth": r.frame_size.width, "frameHeight": r.frame_size.height,
        "screenWidth": r.screen_file_size.width, "screenHeight": r.screen_file_size.height,
        "seconds": seconds,
    })
    .to_string())
}

fn take_screenshot(a: ShotArgs) -> Result<(), Failure> {
    let target = capture_target(&a.target)?;
    let r = screenshot::capture(&target, &output_root(a.out), a.copy).map_err(engine)?;
    print(&json!({ "path": r.path, "width": r.width, "height": r.height, "copied": r.copied }).to_string());
    Ok(())
}

fn record(a: RecordArgs) -> Result<(), Failure> {
    let target = capture_target(&a.target)?;
    // A missing camera gets an empty id, so the engine notes it and records without it.
    let webcam_id =
        a.camera.map(|id| id.or_else(|| WebcamSource::list().into_iter().next().map(|(id, _)| id)).unwrap_or_default());
    let mut o = RecordingOptions {
        target,
        output_root: output_root(a.out),
        frame_rate: a.fps,
        webcam_id,
        mic: a.mic.is_some(),
        mic_id: a.mic.flatten(),
        system_audio: a.system_audio,
        layout: a.layout,
        write_final: !a.no_final,
        extra_notes: Vec::new(),
    };
    let p = RecordingSession::preflight(&o).map_err(engine)?;
    if p.looks_black && matches!(o.target, CaptureTarget::Window(_)) {
        let message = "This window cannot be captured. It may be protected or running as administrator.";
        return Err((1, message.into()));
    }
    let notes = preflight_notes(&p, o.frame_rate, o.write_final);
    if p.encoders == EncoderSupport::TooFewHardwareSessions {
        o.write_final = false;
    }
    if p.encoders == EncoderSupport::SoftwareOnly {
        // The engine adds its own note for this, so it only goes to standard error.
        say("No hardware encoder, so it records at 30 frames per second with the software encoder.");
    }
    for n in &notes {
        say(n);
    }
    o.extra_notes = notes.clone();

    // Without the handler Ctrl+C still ends the process, so a failure here does not stop the recording.
    let _ = on_console_stop(stop_on_console);
    let session = RecordingSession::start(o, &p).map_err(engine)?;
    say("recording");
    // Counted from here, because `start` spends up to 3 seconds warming up the encoders.
    let deadline = a.seconds.and_then(|s| Instant::now().checked_add(s));

    let lines = read_lines();
    let mut input_open = true;
    loop {
        if STOP.load(SeqCst) || deadline.is_some_and(|d| Instant::now() >= d) || session.has_stopped() {
            break;
        }
        if !input_open {
            session.wait_stopped(Duration::from_millis(50));
            continue;
        }
        match lines.recv_timeout(Duration::from_millis(50)) {
            Ok(line) => match line.trim() {
                "stop" => break,
                "pause" => session.pause(),
                "resume" => session.resume(),
                _ => {}
            },
            Err(RecvTimeoutError::Timeout) => {}
            // End of input is not a stop, so a script can close it and wait for --seconds.
            Err(RecvTimeoutError::Disconnected) => input_open = false,
        }
    }

    let seconds = session.elapsed().as_secs_f64();
    print(&outcome(&session.stop(), seconds, &notes)?);
    Ok(())
}

/// Standard input lines, read on their own thread so the main loop never blocks on them.
fn read_lines() -> mpsc::Receiver<String> {
    let (send, lines) = mpsc::channel();
    thread::spawn(move || {
        for line in std::io::stdin().lock().lines() {
            let Ok(line) = line else { break };
            if send.send(line).is_err() {
                break;
            }
        }
    });
    lines
}

/// Ctrl+C, Ctrl+Break and closing the console stop the recording. This holds the process open
/// until the files are finished, at most 10 seconds. When the console window is closed, Windows
/// ends the process about 5 seconds after the event whatever this waits for, so a slow catch-up
/// of final.mp4 can be cut short. The fragmented MP4 files still play.
fn stop_on_console() {
    STOP.store(true, SeqCst);
    let end = Instant::now() + Duration::from_secs(10);
    while !DONE.load(SeqCst) && Instant::now() < end {
        thread::sleep(Duration::from_millis(50));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use recorder_core::frame_math::PixelSize;

    fn preflight(width: i32, height: i32, frame_rate: i32) -> PreflightResult {
        let size = PixelSize { width, height };
        PreflightResult {
            encoders: EncoderSupport::Hardware,
            frame_size: size,
            scaled_down: false,
            looks_black: false,
            frame_rate,
            screen_file_size: size,
        }
    }

    fn result(files: &[&str], notes: &[&str]) -> RecordingResult {
        let size = PixelSize { width: 1280, height: 720 };
        RecordingResult {
            folder: PathBuf::from(r"C:\rec"),
            files: files.iter().map(|f| f.to_string()).collect(),
            dropped_frames: 0,
            notes: notes.iter().map(|n| n.to_string()).collect(),
            frame_rate: 60,
            frame_size: size,
            screen_file_size: size,
        }
    }

    fn size_note(notes: &[String]) -> bool {
        notes.iter().any(|n| n.contains("2560x1440"))
    }

    #[test]
    fn size_note_only_for_frames_above_2560x1440() {
        assert!(size_note(&preflight_notes(&preflight(3840, 2160, 30), 60, true)));
        assert!(!size_note(&preflight_notes(&preflight(1920, 1080, 30), 60, true)));
        assert!(!size_note(&preflight_notes(&preflight(3840, 2160, 60), 60, true)));
    }

    #[test]
    fn a_recording_with_no_files_is_a_failure_with_no_json() {
        let (code, message) = outcome(&result(&[], &["said at start", "disk full"]), 1.0, &["said at start".into()])
            .expect_err("no files must fail");
        assert_eq!(1, code);
        assert_eq!(1, message.matches("disk full").count(), "{message}");
        assert!(!message.contains("said at start"), "{message}");
        assert!(!message.contains('{'), "{message}");
    }

    #[test]
    fn a_recording_with_files_is_json() {
        let text = outcome(&result(&["screen.mp4"], &[]), 2.0, &[]).unwrap();
        let v: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(1280, v["screenWidth"]);
        assert_eq!(2.0, v["seconds"]);
    }

    #[test]
    fn default_root_is_in_the_known_videos_folder() {
        assert_eq!(PathBuf::from(r"D:\Vids\ScreenRecorder"), root_from(Some(PathBuf::from(r"D:\Vids"))));
        assert_eq!(videos_folder().unwrap().join("ScreenRecorder"), output_root(None));
    }

    #[test]
    fn without_a_known_folder_the_root_is_under_the_profile() {
        let profile = PathBuf::from(std::env::var("USERPROFILE").unwrap());
        assert_eq!(profile.join("Videos").join("ScreenRecorder"), root_from(None));
    }
}
