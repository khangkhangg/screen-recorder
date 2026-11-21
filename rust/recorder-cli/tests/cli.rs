//! Tests that run the built `screenrec.exe` and record the real screen for a few seconds.
//! They need a desktop, so they are ignored by default. Run them from `rust/` with:
//!     cargo test --release -p recorder-cli --test cli -- --ignored --test-threads=1
//! `--release` makes cargo build and run the release binary. One test at a time keeps the
//! encoder free for each recording.

use std::fs;
use std::io::{Read, Write};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::thread::sleep;
use std::time::{Duration, Instant};

use serde_json::Value;

const CREATE_NEW_PROCESS_GROUP: u32 = 0x200;
/// 48,000 samples a second, 2 channels, 4-byte floats.
const WAV_BYTES_PER_SECOND: f64 = 48_000.0 * 2.0 * 4.0;

/// A temporary folder that is deleted with every recording in it when the test ends.
struct Temp(PathBuf);

impl Temp {
    fn new(name: &str) -> Temp {
        let path = std::env::temp_dir().join(format!("screenrec-cli-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        Temp(path)
    }

    fn arg(&self) -> &str {
        self.0.to_str().unwrap()
    }
}

impl Drop for Temp {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn screenrec(args: &[&str]) -> Command {
    let mut c = Command::new(env!("CARGO_BIN_EXE_screenrec"));
    c.args(args).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
    c
}

/// Waits for the child, and kills it when it runs past `limit` so a hang fails the test.
fn finish(mut child: Child, limit: Duration) -> Output {
    let end = Instant::now() + limit;
    while child.try_wait().unwrap().is_none() {
        if Instant::now() > end {
            let _ = child.kill();
            panic!("screenrec ran longer than {limit:?}");
        }
        sleep(Duration::from_millis(50));
    }
    child.wait_with_output().unwrap()
}

fn run(args: &[&str]) -> Output {
    finish(screenrec(args).spawn().unwrap(), Duration::from_secs(60))
}

fn code(o: &Output) -> i32 {
    o.status.code().unwrap()
}

fn json(o: &Output) -> Value {
    let text = String::from_utf8_lossy(&o.stdout);
    assert_eq!(1, text.lines().count(), "stdout: {text}");
    serde_json::from_str(&text).unwrap()
}

fn stderr(o: &Output) -> String {
    String::from_utf8_lossy(&o.stderr).into_owned()
}

fn files(v: &Value) -> Vec<String> {
    v["files"].as_array().unwrap().iter().map(|f| f.as_str().unwrap().to_string()).collect()
}

/// Seconds of sound in a WAV file, from the size of its `data` chunk.
fn wav_seconds(path: &Path) -> f64 {
    let bytes = fs::read(path).unwrap();
    let at = bytes.windows(4).position(|w| w == b"data").expect("a data chunk");
    (bytes.len() - at - 8) as f64 / WAV_BYTES_PER_SECOND
}

fn in_range(what: &str, v: f64, low: f64, high: f64) {
    assert!((low..=high).contains(&v), "{what} is {v}, not between {low} and {high}");
}

#[test]
#[ignore]
fn list_monitors_prints_at_least_one() {
    let o = run(&["list", "monitors"]);
    assert_eq!(0, code(&o), "{}", stderr(&o));
    let monitors = json(&o)["monitors"].as_array().unwrap().clone();
    assert!(!monitors.is_empty());
    assert_eq!(1, monitors[0]["index"]);
}

#[test]
#[ignore]
fn screenshot_of_a_region_is_that_size() {
    let temp = Temp::new("shot");
    let o = run(&["screenshot", "--region", "0,0,320,200", "--out", temp.arg(), "--no-copy"]);
    assert_eq!(0, code(&o), "{}", stderr(&o));
    let v = json(&o);
    assert_eq!(false, v["copied"]);
    let png = fs::read(v["path"].as_str().unwrap()).unwrap();
    assert_eq!(b"\x89PNG", &png[..4]);
    // The IHDR chunk holds the width and height as big-endian numbers.
    assert_eq!(320, u32::from_be_bytes(png[16..20].try_into().unwrap()));
    assert_eq!(200, u32::from_be_bytes(png[20..24].try_into().unwrap()));
}

#[test]
#[ignore]
fn record_for_three_seconds() {
    let temp = Temp::new("three");
    let o = run(&["record", "--region", "0,0,1280,720", "--seconds", "3", "--out", temp.arg(), "--system-audio"]);
    assert_eq!(0, code(&o), "{}", stderr(&o));
    assert!(stderr(&o).lines().any(|l| l == "recording"), "{}", stderr(&o));
    let v = json(&o);
    for name in ["final.mp4", "screen.mp4", "system.wav"] {
        assert!(files(&v).iter().any(|f| f == name), "{name} missing from {v}");
    }
    let folder = PathBuf::from(v["folder"].as_str().unwrap());
    // The engine's file list holds the media files only.
    assert!(folder.join("session.json").exists());
    assert!(folder.starts_with(&temp.0));
    in_range("seconds", v["seconds"].as_f64().unwrap(), 2.8, 3.4);
    in_range("system.wav seconds", wav_seconds(&folder.join("system.wav")), 2.8, 3.4);
    assert_eq!(1280, v["frameWidth"]);
    assert_eq!(720, v["frameHeight"]);
}

#[test]
#[ignore]
fn pause_resume_and_stop_from_standard_input() {
    let temp = Temp::new("pause");
    let mut child = screenrec(&["record", "--region", "0,0,640,360", "--out", temp.arg(), "--system-audio"])
        .stdin(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = child.stdin.take().unwrap();
    wait_for_recording(&mut child);
    sleep(Duration::from_secs(1));
    writeln!(input, "pause").unwrap();
    sleep(Duration::from_secs(2));
    writeln!(input, "resume").unwrap();
    sleep(Duration::from_secs(1));
    writeln!(input, "stop").unwrap();
    let o = finish(child, Duration::from_secs(30));
    assert_eq!(0, code(&o), "{}", stderr(&o));
    let v = json(&o);
    let folder = PathBuf::from(v["folder"].as_str().unwrap());
    in_range("seconds", v["seconds"].as_f64().unwrap(), 1.8, 2.4);
    in_range("system.wav seconds", wav_seconds(&folder.join("system.wav")), 1.8, 2.4);
    let session: Value = serde_json::from_str(&fs::read_to_string(folder.join("session.json")).unwrap()).unwrap();
    assert_eq!(1, session["pauses"].as_array().unwrap().len(), "{session}");
}

/// Reads standard error until the `recording` line, which a script waits for.
fn wait_for_recording(child: &mut Child) {
    let mut err = child.stderr.take().unwrap();
    let mut seen = Vec::new();
    let mut byte = [0u8];
    while !String::from_utf8_lossy(&seen).lines().any(|l| l == "recording") {
        assert_eq!(1, err.read(&mut byte).unwrap(), "stderr ended: {}", String::from_utf8_lossy(&seen));
        seen.push(byte[0]);
    }
    // The pipe stays open in a thread, so later lines do not block the child.
    std::thread::spawn(move || std::io::copy(&mut err, &mut std::io::sink()));
}

#[test]
#[ignore]
fn window_that_does_not_exist_is_a_bad_target() {
    let o = run(&["record", "--window", "no such window title 12345", "--seconds", "1"]);
    assert_eq!(2, code(&o));
    assert!(stderr(&o).contains("No window matches"), "{}", stderr(&o));
    assert!(o.stdout.is_empty());
}

#[test]
#[ignore]
fn empty_window_title_lists_the_windows() {
    let o = run(&["record", "--window", "", "--seconds", "1"]);
    assert_eq!(2, code(&o));
    let titles = json(&run(&["list", "windows"]))["windows"].as_array().unwrap().clone();
    assert!(titles.len() >= 2, "this test needs 2 or more open windows");
    assert!(stderr(&o).contains(titles[0]["title"].as_str().unwrap()), "{}", stderr(&o));
}

#[test]
#[ignore]
fn regions_off_the_screen_or_too_small_are_bad_targets() {
    let temp = Temp::new("region");
    for region in ["100000,0,640,360", "0,0,8,8"] {
        let o = run(&["record", "--region", region, "--seconds", "1", "--out", temp.arg()]);
        assert_eq!(2, code(&o), "{region}: {}", stderr(&o));
    }
}

#[test]
#[ignore]
fn closed_input_is_not_a_stop() {
    let temp = Temp::new("closed");
    let start = Instant::now();
    let o = run(&["record", "--region", "0,0,640,360", "--seconds", "2", "--out", temp.arg()]);
    assert_eq!(0, code(&o), "{}", stderr(&o));
    assert!(start.elapsed() >= Duration::from_secs(2));
    in_range("seconds", json(&o)["seconds"].as_f64().unwrap(), 1.8, 2.4);
}

#[test]
#[ignore]
fn ctrl_break_stops_cleanly() {
    use windows::Win32::System::Console::{GenerateConsoleCtrlEvent, CTRL_BREAK_EVENT};
    let temp = Temp::new("break");
    let mut child = screenrec(&["record", "--region", "0,0,640,360", "--out", temp.arg()])
        .creation_flags(CREATE_NEW_PROCESS_GROUP)
        .spawn()
        .unwrap();
    wait_for_recording(&mut child);
    sleep(Duration::from_secs(2));
    // The child leads its own process group, so the event reaches only it.
    unsafe { GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, child.id()) }.unwrap();
    let o = finish(child, Duration::from_secs(30));
    assert_eq!(0, code(&o));
    let v = json(&o);
    assert!(files(&v).iter().any(|f| f == "screen.mp4"), "{v}");
    in_range("seconds", v["seconds"].as_f64().unwrap(), 1.5, 3.0);
}

#[test]
#[ignore]
fn missing_drive_is_a_failure() {
    let o = run(&["record", "--region", "0,0,640,360", "--seconds", "1", "--out", r"Q:\nope"]);
    assert_eq!(1, code(&o), "{}", stderr(&o));
    assert!(!stderr(&o).trim().is_empty());
}
