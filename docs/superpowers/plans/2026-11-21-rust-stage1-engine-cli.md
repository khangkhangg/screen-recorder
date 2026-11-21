# Rust Port Stage 1 (Engine and Command-Line Tool) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the recording engine to Rust and ship `screenrec.exe`, a single-file command-line tool under 5 MB that records and takes screenshots exactly as the C# engine does.

**Architecture:** One Cargo workspace in `rust/` with 3 crates. `recorder-core` is pure logic that compiles on any system. `recorder-win` holds Windows capture, sound, encoding and drawing behind small traits, so the session can be tested with fakes. `recorder-cli` parses arguments, runs a session, and prints one JSON object.

**Tech Stack:** Rust stable (1.99.1), target `x86_64-pc-windows-msvc`, the `windows` crate, `serde` and `serde_json`. Windows.Graphics.Capture, Direct3D 11, Direct2D, Media Foundation Sink Writer, WASAPI, Windows Imaging Component.

**Spec:** `docs/superpowers/specs/2026-11-21-rust-port-design.md`

**Reference:** The C# app at commit `47a535e` is the behavior reference. Each task names the C# files to port from. Read them for behavior, and write idiomatic Rust: do not transliterate line by line. `docs/session-format.md` defines `session.json`.

## Global Constraints

- All Rust code lives under `rust/`. Do not change anything under `src/`, `tests/` or `dist/`.
- Crates allowed: `windows`, `serde` (with `derive`), `serde_json`. Any other crate needs the controller's yes. No async runtime, no GUI toolkit, no media library, no argument-parsing crate.
- Turn on only the `windows` crate features each crate uses.
- `recorder-core` has no `windows` dependency and must pass `cargo test -p recorder-core` on any system.
- Release profile in `rust/Cargo.toml`: `opt-level = "z"`, `lto = true`, `codegen-units = 1`, `panic = "abort"`, `strip = true`. `rust/.cargo/config.toml` sets `-C target-feature=+crt-static` for the MSVC target, so the tool is one file.
- `unsafe` is allowed only in `recorder-win` and only around system calls. Each `unsafe` block is as small as the call needs.
- Errors reach the user as plain sentences. One error enum per crate with a hand-written `Display`. No panics on bad input, missing devices or disk errors.
- No account, no network use, no telemetry.
- Frames stay on the graphics chip, with 3 accepted exceptions: the software encoder fallback, webcam frames uploaded once, and screenshot readback.
- H.264 in fragmented MP4, constant frame rate, 48,000 Hz stereo, 32-bit float WAV, AAC at 192 kbit/s.
- Bitrate is `width * height * frame_rate / 10`.
- `screenrec` never reads `settings.json`. Defaults: output folder `Videos\ScreenRecorder`, 60 frames per second, microphone, computer sound, camera and logo off.
- Integration tests that need the desktop are marked `#[ignore]` and run with `cargo test -- --ignored` on the build PC. They write to a temporary folder and delete it.
- Work on branch `rust-port`, created from `windows-v1`.
- Scope note: the design says about 150 ported logic tests. This plan ports the 53 from the C# Core test project. The roughly 100 from the C# App test project (region frame, card, crop, overlay math) move to Stage 2, where the code that uses them is built.

## Review Focus

1. `--window "TITLE"` matches no window or more than 1. The tool exits with code 2 and a plain message that lists the matching titles. Test in Task 11.
2. Ctrl+C during `record`. The recording stops cleanly and every file is finished, as with `stop`. The process is not killed mid-write. Test in Task 11.
3. Standard input is closed or missing, as when a script starts the tool with no input. A closed input is not a `stop`: the recording runs until `--seconds` passes or Ctrl+C. Test in Task 11.
4. The output folder cannot be created or written (missing drive, read-only). The tool exits with code 1 and a plain message, and leaves no partial folder. Test in Task 9.
5. `--region` lies outside the monitor or is under 16 by 16 pixels. The tool exits with code 2 and a plain message. Test in Task 5 for the engine and Task 11 for the tool.

---

## File Structure

```
rust/
  Cargo.toml                 workspace, release profile
  .cargo/config.toml         static C runtime
  recorder-core/src/
    lib.rs
    frame_math.rs            sizes, scaling, bars, bitrate
    layout.rs                camera, logo, shapes, corners
    session.rs               session.json model and JSON
    settings.rs              settings.json model and JSON
    clock.rs                 frame pacing and pause
    state.rs                 recorder state machine
    hotkey.rs                parse and format hotkeys
  recorder-win/src/
    lib.rs
    error.rs                 Error enum with plain messages
    gfx.rs                   Direct3D 11 and Direct2D device
    targets.rs               monitors, windows, CaptureTarget
    screen.rs                Windows.Graphics.Capture source
    video.rs                 Sink Writer, encoder probe
    audio.rs                 WASAPI mic and loopback
    wav.rs                   float WAV writer
    webcam.rs                camera frames
    compositor.rs            draws screen, camera, logo
    session.rs               runs one recording
    screenshot.rs            PNG and clipboard
    fakes.rs                 test doubles (cfg(test) or feature "fakes")
  recorder-win/tests/        integration tests, #[ignore]
  recorder-cli/src/
    main.rs
    args.rs                  hand-written argument parser
    run.rs                   commands, stdin protocol, JSON output
  recorder-cli/tests/        runs the built tool
```

---

### Task 1: Workspace and frame math

**Files:**
- Create: `rust/Cargo.toml`, `rust/.cargo/config.toml`, `rust/recorder-core/Cargo.toml`, `rust/recorder-core/src/lib.rs`, `rust/recorder-core/src/frame_math.rs`
- Port from: `src/ScreenRecorder.Core/FrameMath.cs`, tests from `tests/ScreenRecorder.Core.Tests/FrameMathTests.cs`

**Interfaces:**
- Produces:
  - `pub struct PixelSize { pub width: i32, pub height: i32 }` and `pub struct RectF { pub x: f32, pub y: f32, pub width: f32, pub height: f32 }`, both `Copy, Clone, Debug, PartialEq`, with serde derives that write camelCase field names.
  - `pub fn to_even(s: PixelSize) -> PixelSize`
  - `pub fn fit_within(source: PixelSize, max: PixelSize) -> PixelSize` (integer math, never scales up, even result)
  - `pub fn letterbox(source: PixelSize, frame: PixelSize) -> RectF`
  - `pub fn bitrate(size: PixelSize, frame_rate: i32) -> i32`

- [ ] **Step 1: Create branch `rust-port` from `windows-v1` and the workspace files.** Add `rust/target/` to `.gitignore`.
- [ ] **Step 2: Write the failing tests.** Port every test in `FrameMathTests.cs` with the same inputs and expected values, named in snake_case. They include: `to_even` of 1921x1081 is 1920x1080. `fit_within` of 5120x2160 into 4096x2304 is 4096x1728. 1280x720 into 4096x2304 stays 1280x720. 3440x1440 into 1720x2304 is 1720x720. `letterbox` of 500x500 in 1920x1080 is (420, 0, 1080, 1080). `bitrate` of 1920x1080 at 60 is 12,441,600.
- [ ] **Step 3: Run `cargo test -p recorder-core` in `rust/`.** Expected: compile error, functions not defined.
- [ ] **Step 4: Implement `frame_math.rs`.**
- [ ] **Step 5: Run the tests.** Expected: all pass.
- [ ] **Step 6: Commit** `feat(rust): workspace and frame math`.

---

### Task 2: Layout, session file and settings

**Files:**
- Create: `rust/recorder-core/src/layout.rs`, `session.rs`, `settings.rs`
- Port from: `src/ScreenRecorder.Core/Layout.cs`, `SessionFile.cs`, `Settings.cs`, and their test files. `docs/session-format.md` is the format's definition.

**Interfaces:**
- Consumes: `PixelSize`, `RectF`.
- Produces:
  - `pub enum CameraShape { Circle, RoundedSquare, Square }`, `pub enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }`, serialized as those exact names.
  - `pub struct CameraItem { pub visible: bool, pub x: f32, pub y: f32, pub size: f32, pub shape: CameraShape }`
  - `pub struct LogoItem { pub visible: bool, pub corner: Corner, pub size: f32, pub path: Option<String> }`
  - `pub struct Layout { pub camera: CameraItem, pub logo: LogoItem }` with `Default` equal to the C# `Layout.Default`.
  - `pub fn camera_rect(c: &CameraItem, frame: PixelSize) -> RectF` and `pub fn logo_rect(l: &LogoItem, frame: PixelSize, logo_pixels: PixelSize) -> RectF`, clamped inside the frame and never panicking when the item is larger than the frame.
  - `pub struct SourceEntry { pub file: String, pub kind: String, pub offset_seconds: f64, pub width: Option<i32>, pub height: Option<i32> }`, `pub struct LayoutChange { pub at_seconds: f64, pub layout: Layout }`, `pub struct PauseEntry { pub at_seconds: f64, pub duration_seconds: f64 }`
  - `pub struct SessionFile { version, started_at: String, frame_size, frame_rate, target_kind: String, sources, layout_changes, pauses, dropped_frames: i64, notes: Vec<String> }`. `started_at` is an ISO 8601 string with offset, written exactly as the C# app writes it.
  - `pub fn serialize_session(f: &SessionFile) -> String` (camelCase, indented with 2 spaces) and `pub fn deserialize_session(json: &str) -> Result<SessionFile, SessionError>`. A version above 1 gives `SessionError::NewerVersion`.
  - `pub struct SavedRegion { pub monitor: String, pub x: i32, pub y: i32, pub width: i32, pub height: i32 }`
  - `pub struct Settings { output_folder, record_hotkey: Hotkey, screenshot_hotkey: Hotkey, countdown: bool, frame_rate: i32, webcam_id: Option<String>, mic_id: Option<String>, mic: bool, system_audio: bool, layout: Layout, region: Option<SavedRegion> }` with `Default`, `pub fn load(path: &Path) -> Settings` (defaults on a missing, unreadable or invalid file, including `{}` and `"layout": {}`) and `pub fn save(&self, path: &Path) -> std::io::Result<()>` (creates the folder).
  - `Hotkey` is needed by `Settings`. Define the struct here if Task 3 has not run, and Task 3 adds its parsing.

- [ ] **Step 1: Write the failing tests.** Port every test in `SessionFileTests.cs`, `LayoutTests.cs` and `SettingsTests.cs`. Add 2 compatibility tests that prove the files are interchangeable with the C# app:

```rust
#[test]
fn reads_a_session_file_written_by_the_csharp_app() {
    // the example in docs/session-format.md, pasted as a string
    let f = deserialize_session(EXAMPLE).unwrap();
    assert_eq!(f.version, 1);
    assert_eq!(serialize_session(&f).replace("\r\n", "\n"), EXAMPLE.replace("\r\n", "\n"));
}
#[test]
fn reads_a_settings_file_written_by_the_csharp_app() {
    // a settings.json produced by the C# Settings.Default.Save, pasted as a string
    let s = load_from_str(CSHARP_DEFAULT);
    assert_eq!(s.frame_rate, 60);
    assert_eq!(s.record_hotkey.to_string(), "Ctrl+Shift+R");
}
```

  If byte-identical output is not reachable because of float formatting, assert that the C# file parses and that parsing the Rust output gives an equal value, and say which fields differ in text.
- [ ] **Step 2: Run the tests.** Expected: fail, types not defined.
- [ ] **Step 3: Implement the 3 files.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat(rust): layout, session file and settings`.

---

### Task 3: Recording clock, recorder state and hotkeys

**Files:**
- Create: `rust/recorder-core/src/clock.rs`, `state.rs`, `hotkey.rs`
- Port from: `src/ScreenRecorder.Core/RecordingClock.cs`, `RecorderState.cs`, `Hotkey.cs`, and their test files.

**Interfaces:**
- Produces:
  - `pub struct RecordingClock` with `pub fn new(frame_rate: i32, now: Box<dyn Fn() -> Duration + Send + Sync>) -> Self`, `start`, `pause`, `resume`, `is_paused`, `media_time() -> Duration`, `frames_due() -> i64`, `samples_due(sample_rate: i32) -> i64`, `pauses() -> Vec<PauseEntry>`. All take `&self` and are safe to call from 2 threads. Integer math on 100-nanosecond ticks. A second `start`, a `pause` before `start` and a `resume` when not paused do nothing.
  - `pub enum RecorderState { Idle, Countdown, Recording, Paused }`, `pub enum RecorderCommand { ToggleRecord, CountdownDone, TogglePause, Failed }`, `pub fn next(s: RecorderState, c: RecorderCommand, countdown_enabled: bool) -> RecorderState`.
  - `pub struct Hotkey { pub ctrl: bool, pub shift: bool, pub alt: bool, pub win: bool, pub virtual_key: u32 }`, `pub fn parse(text: &str) -> Option<Hotkey>` (letters, digits, F1 to F12, at least 1 modifier, ignores case and spaces around `+`), and `Display` that writes `Ctrl+Shift+R` with modifiers in the order Ctrl, Shift, Alt, Win. Its JSON form matches the C# record (`ctrl`, `shift`, `alt`, `win`, `virtualKey`).

- [ ] **Step 1: Write the failing tests.** Port every test in `RecordingClockTests.cs`, `RecorderStateTests.cs` and `HotkeyTests.cs`, including `frames_exact_at_boundary` (290 ms at frame rate 100 gives 29) and `start_twice_is_ignored`. Add one test that calls `pause` and `resume` from a second thread 1,000 times while the first thread reads `frames_due`, and asserts the value never decreases.
- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement the 3 files.**
- [ ] **Step 4: Run `cargo test -p recorder-core`.** Expected: every core test passes. Count them and report the number against the 53 C# Core tests.
- [ ] **Step 5: Commit** `feat(rust): recording clock, recorder state and hotkeys`.

---

### Task 4: Graphics device, targets and errors

**Files:**
- Create: `rust/recorder-win/Cargo.toml`, `src/lib.rs`, `src/error.rs`, `src/gfx.rs`, `src/targets.rs`
- Test: `rust/recorder-win/tests/targets.rs`
- Port from: `src/ScreenRecorder.Capture/CaptureTarget.cs`, `src/ScreenRecorder.App/TargetPicker.cs` (the monitor and window listing rules)

**Interfaces:**
- Produces:
  - `pub enum Error { CaptureUnavailable(String), Device(String), Encoder(String), Io(std::io::Error), NotSupported(String), Invalid(String) }` with `Display` that prints the plain message, `std::error::Error`, and `pub type Result<T> = std::result::Result<T, Error>`. A helper turns a `windows::core::Error` into the right variant, mapping disk HRESULTs (facility Win32) to `Io`.
  - `pub struct Device` holding the Direct3D 11 device and context (BGRA support, multithread protection on) and a Direct2D device. `pub fn new() -> Result<Device>`. `Clone` shares the same device. Accessors `d3d()`, `context()`, `d2d()`.
  - `pub struct MonitorInfo { pub handle: isize, pub name: String, pub device: String, pub x: i32, pub y: i32, pub width: i32, pub height: i32, pub primary: bool }` and `pub fn monitors() -> Vec<MonitorInfo>`, primary first.
  - `pub struct WindowInfo { pub handle: isize, pub title: String }` and `pub fn windows() -> Vec<WindowInfo>`. It skips invisible, minimized, untitled, tool and cloaked windows, and windows of the current process.
  - `pub enum CaptureTarget { Monitor(isize), Window(isize), Region { monitor: isize, x: i32, y: i32, width: i32, height: i32 } }` and `pub fn kind(&self) -> &'static str` returning `monitor`, `window` or `region`.
  - `pub fn find_window(title_part: &str) -> std::result::Result<WindowInfo, Vec<WindowInfo>>`. Case-insensitive substring match. `Err` holds the matches (0 or 2 and more).
  - The process is set per-monitor DPI aware (version 2) by a function `pub fn init_process()` that the tool and tests call first, and COM and Media Foundation are started there too.

- [ ] **Step 1: Write the failing tests** (`#[ignore]`, need the desktop): `monitors()` is not empty and its first entry is primary with a positive size. `Device::new()` succeeds. `find_window` with a made-up title returns `Err` with an empty list. `windows()` contains no entry with an empty title.
- [ ] **Step 2: Run `cargo test -p recorder-win -- --ignored`.** Expected: fail to compile.
- [ ] **Step 3: Implement the files.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat(rust): graphics device, targets and errors`.

---

### Task 5: Screen source

**Files:**
- Create: `rust/recorder-win/src/screen.rs`
- Test: `rust/recorder-win/tests/screen.rs`
- Port from: `src/ScreenRecorder.Capture/ScreenSource.cs`

**Interfaces:**
- Consumes: `Device`, `CaptureTarget`, `Error`.
- Produces:
  - `pub trait Screen: Send { fn source_size(&self) -> PixelSize; fn latest_frame(&mut self) -> Option<Frame>; fn is_minimized(&self) -> bool; fn is_closed(&self) -> bool; }`
  - `pub struct Frame { pub texture: ID3D11Texture2D, pub size: PixelSize }`. A `Frame` is a counted reference to a texture the source owns and replaces, so it stays valid while the caller holds it.
  - `pub struct ScreenSource` implementing `Screen`, with `pub fn create(target: &CaptureTarget, device: &Device) -> Result<ScreenSource>`.
  - `pub fn looks_black(frame: &Frame, device: &Device) -> bool`, sampling a 16 by 16 grid.

Rules, all from the C# source:
- A region is a GPU copy of the sub-rectangle. A region with an origin outside the monitor, or under 16 by 16 pixels, or an invalid window, gives `Error::CaptureUnavailable` with a plain message.
- `latest_frame` returns the previous frame again when the screen did not change, and `None` only before the first frame.
- The frame pool is recreated when the content size changes.
- No yellow border where Windows allows it. The cursor is captured.
- `is_closed` becomes true when the window closes, when the monitor goes away (checked every 500 ms), or when the frame handler fails. It never goes back to false.
- HDR: on an HDR monitor, capture in 16-bit float and convert to standard colors with Direct2D's HDR tone map effect and white level adjustment, using the monitor's real SDR white level from `DisplayConfigGetDeviceInfo`. Standard content must come out unchanged.

- [ ] **Step 1: Write the failing tests** (`#[ignore]`): the primary monitor gives a frame within 2 seconds. A region 100,100,640,360 gives frames of exactly 640x360. A region at x 100,000 gives `CaptureUnavailable`. A region of 8x8 gives `CaptureUnavailable`. An invalid window handle gives `CaptureUnavailable`. Closing a window the test created makes `is_closed` true within 2 seconds. `looks_black` is true for a cleared black texture and false for a red one.
- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement `screen.rs`.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: HDR picture check.** If the primary monitor runs in HDR, show a window with a gray patch of value 128 and a white patch, capture 1 frame, read the 2 patches back, and report the values. Pass: gray reads 126 to 130, white reads 250 to 255. Report the numbers either way.
- [ ] **Step 6: Commit** `feat(rust): screen source with HDR conversion`.

---

### Task 6: Video writer and encoder probe

**Files:**
- Create: `rust/recorder-win/src/video.rs`
- Test: `rust/recorder-win/tests/video.rs`, plus a helper `tests/common/media_info.rs` that reads duration, frame count, size and whether every frame has the same duration, with the Media Foundation Source Reader
- Port from: `src/ScreenRecorder.Capture/VideoFileWriter.cs`, `EncoderProbe.cs`

**Interfaces:**
- Produces:
  - `pub trait VideoOut: Send { fn write_frame(&mut self, texture: &ID3D11Texture2D, index: i64) -> Result<()>; fn write_audio(&mut self, samples: &[f32], first_sample: i64) -> Result<()>; fn frames_behind(&self) -> i64; fn finish(&mut self) -> Result<()>; }`
  - `pub struct VideoWriter` implementing `VideoOut`, with `pub fn create(path: &Path, size: PixelSize, frame_rate: i32, device: &Device, hardware: bool, with_audio: bool) -> Result<VideoWriter>` and `pub fn uses_hardware_encoder(&self) -> bool`.
  - `pub enum EncoderSupport { Hardware, TooFewHardwareSessions, SoftwareOnly }`, `pub fn check_encoders(sessions: u32) -> EncoderSupport`, `pub fn max_encoder_size() -> PixelSize` (4096x2304 when the encoder does not say).

Rules, all from the C# source and its review:
- The frame's timestamp is `index * 10_000_000 / frame_rate`, and every frame has the same duration.
- `write_frame` copies the texture on the GPU at once and queues it for a worker thread, so the caller may reuse its texture.
- Fragmented MP4. A file whose process was killed still plays.
- With `hardware: true`, creation fails with `Error::NotSupported` when Windows did not load a hardware encoder. The partial file is deleted and everything is released.
- Sound is held back on the worker until the video up to that time has been written. Every held sample is written before the file is finished.
- `write_audio` on a writer made without audio returns `Error::Invalid`.
- A writer closed with no samples finishes without an error.
- `frames_behind` is cheap to read from another thread, and is always decremented, also when a write fails.
- Disk errors come back as `Error::Io`.
- `Drop` calls `finish` if it was not called, and ignores its error.

- [ ] **Step 1: Write the failing tests** (`#[ignore]`): 120 frames of 1280x720 at 60 give 120 frames, 1.98 to 2.02 seconds, constant frame rate, and `uses_hardware_encoder()` equal to what `check_encoders(1)` says. A child process that writes 300 frames and is killed after 200 leaves a file with at least 60 frames. 2 seconds of frames with 96,000 audio samples give both durations within 0.05 seconds. Audio written 2 seconds ahead of the video is all kept, and `finish` returns within 5 seconds. `hardware: false` writes 30 frames. `write_audio` on a video-only writer is an error. `check_encoders(3)` returns a value.
- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement `video.rs`.**
- [ ] **Step 4: Run the tests.** Expected: pass. If the killed file does not play, stop and report, because the crash rule depends on it.
- [ ] **Step 5: Commit** `feat(rust): H.264 writer and encoder probe`.

---

### Task 7: Sound sources and WAV writer

**Files:**
- Create: `rust/recorder-win/src/audio.rs`, `wav.rs`
- Test: `rust/recorder-win/tests/audio.rs`
- Port from: `src/ScreenRecorder.Capture/AudioSource.cs`, `WasapiInterop.cs`, `WavWriter.cs`

**Interfaces:**
- Produces:
  - `pub trait AudioIn: Send { fn read(&mut self, out: &mut [f32]) -> usize; fn is_lost(&self) -> bool; }`. `read` returns an even count of floats at 48,000 Hz stereo.
  - `pub struct AudioSource` implementing `AudioIn`, with `pub fn open_mic(id: Option<&str>) -> Result<AudioSource>`, `pub fn open_loopback() -> Result<AudioSource>`, and `pub fn list_mics() -> Vec<(String, String)>` (id, name).
  - `pub struct WavWriter` with `pub fn create(path: &Path) -> Result<WavWriter>`, `pub fn write(&mut self, samples: &[f32]) -> Result<()>`, `pub fn finish(&mut self) -> Result<()>`.

Rules:
- Both sources use WASAPI in shared mode: `IAudioClient` with automatic conversion to 48,000 Hz stereo float. Loopback adds the loopback flag on the default render endpoint.
- One capture thread per source fills a ring buffer of 2 seconds. A full buffer drops the oldest samples. Silent packets are zeros.
- A device that is removed or invalidated sets `is_lost` and ends the thread. Dropping the source from any thread, including after a loss, never deadlocks.
- No microphone gives `Error::Device("No microphone found on this PC.")`. Microphone access turned off in Windows privacy settings gives a plain message that says so. An unknown id gives the first message.
- The WAV header is 44 bytes, format tag 3. Its sizes are rewritten once per second of sound, so a cut-off file still opens. `finish` twice is safe. A comment marks the 4 GB limit (about 3.1 hours).

- [ ] **Step 1: Write the failing tests.** Not ignored: a WAV file with 96,000 floats is `44 + 96_000 * 4` bytes with a valid header. A copy taken without `finish` after 3 seconds has a data size that covers at least 2 seconds. `finish` twice is fine. Ignored: loopback opens, and after 300 ms `read` returns an even count of at most 57,600. A made-up mic id gives the plain message. Dropping a source right after opening returns within 2 seconds.
- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement the 2 files.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat(rust): WASAPI sound sources and WAV writer`.

---

### Task 8: Webcam source and compositor

**Files:**
- Create: `rust/recorder-win/src/webcam.rs`, `compositor.rs`
- Test: `rust/recorder-win/tests/compositor.rs`, `tests/webcam.rs`
- Port from: `src/ScreenRecorder.Capture/WebcamSource.cs`, `Compositor.cs`

**Interfaces:**
- Consumes: `Device`, `Frame`, `Layout`, `camera_rect`, `logo_rect`, `letterbox`.
- Produces:
  - `pub trait Camera: Send { fn size(&self) -> PixelSize; fn latest_frame(&mut self) -> Option<Frame>; fn is_lost(&self) -> bool; }`
  - `pub struct WebcamSource` implementing `Camera`, with `pub fn list() -> Vec<(String, String)>` and `pub fn open(id: &str, device: &Device) -> Result<WebcamSource>`. It picks the mode closest to 1280x720 at 30 frames per second, and prefers a color video stream. Every failure path releases the camera.
  - `pub struct Compositor` with `pub fn new(device: &Device, frame_size: PixelSize, screen_size: PixelSize) -> Result<Compositor>`, `pub fn draw_screen(&mut self, screen: &Frame) -> Result<&ID3D11Texture2D>`, `pub fn draw_final(&mut self, screen: &Frame, camera: Option<&Frame>, layout: &Layout) -> Result<&ID3D11Texture2D>`, `pub fn draw_camera_file(&mut self, camera: &Frame) -> Result<&ID3D11Texture2D>` (the full camera frame for `webcam.mp4`, no mask, no mirror), and `pub fn set_logo(&mut self, path: Option<&Path>) -> Option<String>`.

Rules:
- `draw_screen` letterboxes the screen into `screen_size` with black bars. `draw_final` letterboxes it into `frame_size`, then draws the logo, then the camera on top.
- The camera is a centered square crop, mirrored left to right, masked as a circle, a rounded square with a corner radius of 18% of the side, or a square.
- The camera is drawn only when `layout.camera.visible` and a camera frame was passed. The logo is drawn only when `layout.logo.visible` and a logo is loaded.
- `set_logo` decodes with the Windows Imaging Component, keeps transparency, and never fails loudly: it returns a message that starts with "The logo" for a missing file, a corrupt file, a file over 20 MB or a side over 8192 pixels. It checks the file size before decoding. `None` clears the logo.
- All drawing is Direct2D on the shared device. Each output reuses one texture.

- [ ] **Step 1: Write the failing tests** (`#[ignore]`). Port every test in `CompositorTests.cs` with the same rectangles, pixels and a tolerance of 2 per channel: the circle covers the center and not the corner of rect 192,108,270,270. The square covers the corner. A hidden camera and a missing camera frame are not drawn. `draw_screen` of a 500x500 source has black at (10, 540) and the source color at (960, 540), and never shows overlays. The logo sits in its corner and keeps its transparency. The rounded square cuts only the corner. The camera is mirrored. A missing, corrupt and oversized logo each return a message. Add: with `screen_size` 1920x1080 and `frame_size` 3840x2160, `draw_screen` returns a 1920x1080 texture and `draw_final` a 3840x2160 one. `WebcamSource::list()` does not fail.
- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement the 2 files.**
- [ ] **Step 4: Run the tests.** Expected: pass. Say in the report whether a real camera was present and whether `open` ran against it.
- [ ] **Step 5: Commit** `feat(rust): webcam source and compositor`.

---

### Task 9: Recording session

**Files:**
- Create: `rust/recorder-win/src/session.rs`, `fakes.rs`
- Test: `rust/recorder-win/tests/session.rs`
- Port from: `src/ScreenRecorder.Capture/RecordingSession.cs`, `SourceFactory.cs`, and `tests/ScreenRecorder.Capture.Tests/RecordingSessionTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 to 8.
- Produces:
  - `pub struct RecordingOptions { pub target: CaptureTarget, pub output_root: PathBuf, pub frame_rate: i32, pub webcam_id: Option<String>, pub mic: bool, pub mic_id: Option<String>, pub system_audio: bool, pub layout: Layout, pub write_final: bool }`
  - `pub struct PreflightResult { pub encoders: EncoderSupport, pub frame_size: PixelSize, pub scaled_down: bool, pub looks_black: bool, pub frame_rate: i32, pub screen_file_size: PixelSize }`
  - `pub struct RecordingResult { pub folder: PathBuf, pub files: Vec<String>, pub dropped_frames: i64, pub notes: Vec<String> }`
  - `pub trait SourceFactory: Send` with one method per source and writer, each returning the trait objects from Tasks 5 to 8, and `pub struct RealFactory`.
  - `pub struct RecordingSession` with `pub fn preflight(o: &RecordingOptions) -> Result<PreflightResult>`, `pub fn start(o: RecordingOptions, p: &PreflightResult) -> Result<RecordingSession>`, `pub fn start_with(o, p, factory: Box<dyn SourceFactory>) -> Result<RecordingSession>`, `pause(&self)`, `resume(&self)`, `set_layout(&self, l: Layout)`, `elapsed(&self) -> Duration`, `has_stopped(&self) -> bool`, `pub fn stop(self) -> RecordingResult`, and `pub fn wait_stopped(&self, timeout: Duration) -> bool` for callers that poll.

Behavior: the "Behavior it must match" list in the design, and these details from the C# session.
- The frame size is fixed at start. One recording thread loops on `frames_due`.
- Frames above 3,686,400 pixels: frame rate at most 30, and `screen.mp4` is `fit_within(frame_size, 1920x1080)`, with the note `screen.mp4 scaled to WIDTHxHEIGHT`.
- `SoftwareOnly` forces 30. A writer that fails with `NotSupported` makes the session reopen every writer with software encoding at 30 and add the note `no hardware encoder, recording at 30 frames per second`.
- `final.mp4` is skipped when its writer is more than 30 frames behind or when more than 2 frames are due in one tick, and each skipped frame counts as dropped. Every skipped index is later written as a repeat of the previous final frame, so the file keeps a true constant frame rate. `screen.mp4` and `webcam.mp4` wait when more than 60 behind, with the note `encoder fell behind` once.
- Sound is pulled up to `samples_due(48000)`, padded with silence, written to the WAV files, and mixed (sum, then clamp) into `final.mp4`.
- Notes begin with `webcam lost at`, `mic lost at`, `system sound lost at`, start with "The logo" for logo problems, and contain `disk` for a disk failure. A camera that fails to open adds `webcam lost at 0.0 s` and the recording goes on.
- `session.json` is written at start and at every layout change, pause, note and stop, through a temporary file and a rename. A failed write never stops the recording. It holds only the logo's file name. Each video source entry carries its width and height.
- The folder name gets ` (2)`, ` (3)` when it exists. A failed start deletes its folder.
- The finish code always runs: every source and writer is released and every file finished on stop, on self-stop and after an error on the recording thread.
- `stop` after a self-stop returns the same result.

- [ ] **Step 1: Write `fakes.rs`**, with fake sources and writers that can fail on command, and a fake factory. Fake video writers may wrap a real `VideoWriter` so files can be read back.
- [ ] **Step 2: Write the failing tests** (`#[ignore]`). Port every test in `RecordingSessionTests.cs` with the same durations and ranges: aligned files after 2 seconds, pause cut from every file, layout changes logged, sources that are off write no file, `write_final` false, lost webcam, lost mic, disk full, source resize, source closed, slow final encoder, large frame capped at 30, normal frame keeps 60, big frame scales the raw screen file, elapsed does not grow while paused, `session.json` held open by another handle, failed start removes its folder, same-second starts get separate folders, failed camera open continues, logo path not in `session.json`. Add for Review Focus 4: an output root on a drive letter that does not exist gives `Error::Io`, and no folder is left. Add: after a slow final encoder, `final.mp4` reads as constant frame rate and has the same frame count as `screen.mp4`.
- [ ] **Step 3: Run the tests.** Expected: fail.
- [ ] **Step 4: Implement `session.rs`.**
- [ ] **Step 5: Run the session tests 5 times.** Expected: pass every time. A test that fails once is reported with its message, and its assertion is not weakened.
- [ ] **Step 6: Commit** `feat(rust): recording session`.

---

### Task 10: Screenshot

**Files:**
- Create: `rust/recorder-win/src/screenshot.rs`
- Test: `rust/recorder-win/tests/screenshot.rs`
- Port from: `src/ScreenRecorder.Capture/Screenshot.cs`

**Interfaces:**
- Produces:
  - `pub struct ScreenshotResult { pub path: PathBuf, pub copied: bool, pub width: i32, pub height: i32 }`
  - `pub fn capture(target: &CaptureTarget, output_root: &Path, copy: bool) -> Result<ScreenshotResult>`. It waits up to 2 seconds for the first frame, saves `Screenshot yyyy-MM-dd HH-mm-ss.png` at the source's own size, creates the folder, and never overwrites.
  - `pub fn unique_path(folder: &Path, time: SystemTime) -> PathBuf`, adding ` (2)`, ` (3)` before `.png`.
  - `pub fn copy_to_clipboard(target: &CaptureTarget) -> Result<PixelSize>`, which writes no file.
  - `pub fn crop(source: &Path, x: i32, y: i32, width: i32, height: i32) -> Result<PathBuf>`, saving ` (cropped)` beside the original with a counter, never changing the original, and deleting a partial file when saving fails.

Rules: PNG through the Windows Imaging Component. The clipboard gets a device-independent bitmap through the Win32 clipboard calls, which need no window of our own. A failed clipboard copy still returns the saved path with `copied` false. The file is closed before the copy step.

- [ ] **Step 1: Write the failing tests.** Not ignored: `unique_path` gives the plain name, then ` (2)`, then ` (3)`. `crop` of a 100x80 test PNG at 10,10,50,40 writes a 50x40 file named ` (cropped).png`, leaves the original bytes unchanged, and a second crop writes ` (cropped) (2).png`. Ignored: a region screenshot 0,0,320,200 writes a 320x200 PNG. A missing folder is created. After `capture` with `copy` true, the clipboard holds a bitmap of the same size.
- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement `screenshot.rs`.**
- [ ] **Step 4: Run the tests.** Expected: pass. The clipboard test changes the user's clipboard. Say so in the report.
- [ ] **Step 5: Commit** `feat(rust): screenshot to PNG and clipboard`.

---

### Task 11: The command-line tool

**Files:**
- Create: `rust/recorder-cli/Cargo.toml`, `src/main.rs`, `src/args.rs`, `src/run.rs`
- Test: unit tests in `args.rs`, and `rust/recorder-cli/tests/cli.rs` that runs the built tool

**Interfaces:**
- Consumes: `recorder-win` and `recorder-core`.
- Produces the binary `screenrec.exe` and:
  - `pub enum Command { List(ListKind), Screenshot(ShotArgs), Record(RecordArgs), Help, Version }`
  - `pub fn parse(args: &[String]) -> std::result::Result<Command, String>`, where the `Err` string is a plain message.

The commands and flags are exactly those in the design's Stage 1 section. Details:
- A target is required for `screenshot` and `record`: exactly 1 of `--monitor N` (1-based, in the order `list monitors` prints), `--window "TITLE"`, or `--region X,Y,W,H` (pixels of `--monitor N`, default monitor 1).
- `--camera-pos X,Y,SIZE` are fractions from 0 to 1, as in the layout. `--mic` and `--camera` take an optional id from `list`.
- Exit codes: 0 for success, 1 for a failure while running, 2 for bad arguments or a target that cannot be resolved.
- Standard output carries exactly 1 JSON object per run. `list` prints `{"monitors":[...]}` and so on. `screenshot` prints `{"path":...,"width":...,"height":...,"copied":...}`. `record` prints `{"folder":...,"files":[...],"droppedFrames":...,"notes":[...],"frameRate":...,"frameWidth":...,"frameHeight":...,"seconds":...}`. Messages for people go to standard error.
- `record` prints 1 line on standard error when recording has started: `recording`. A script can wait for it.
- While recording, lines on standard input are commands: `stop`, `pause`, `resume`. Unknown lines are ignored. End of input is not a command.
- Ctrl+C and Ctrl+Break stop the recording cleanly through a console control handler. The handler waits for the files to be finished before the process exits.
- A recording that stopped by itself (window closed, disk full) prints its result and exits with code 0 when files were saved, and the notes say why.
- Preflight results that the C# app showed as messages become notes in the JSON and lines on standard error: the scale-down, the 30 frames per second cap, the software encoder. A window that looks black exits with code 1 and the message "This window cannot be captured. It may be protected or running as administrator." Too few hardware encoder sessions records without `final.mp4` and adds a note.
- `--help` prints the usage block. `--version` prints the version.

- [ ] **Step 1: Write the failing unit tests for `parse`:** each command with its flags gives the expected struct. No target, 2 targets, a bad number, `--region 1,2,3`, `--fps 45`, an unknown flag and an unknown command each give an `Err` whose message names the problem.
- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement `args.rs`, then `run.rs` and `main.rs`.**
- [ ] **Step 4: Run the unit tests.** Expected: pass.
- [ ] **Step 5: Write the tool tests** (`#[ignore]`, they run `target/release/screenrec.exe`):
  - `list monitors` exits 0 and prints JSON with at least 1 monitor.
  - `screenshot --region 0,0,320,200 --out TEMP --no-copy` exits 0, and the JSON's path is a 320x200 PNG.
  - `record --region 0,0,1280,720 --seconds 3 --out TEMP --system-audio` exits 0 with `final.mp4`, `screen.mp4`, `system.wav` and `session.json` in the folder, and durations between 2.8 and 3.4 seconds.
  - `record` with `pause` after 1 second, `resume` after 2 more and `stop` after 1 more gives files of 1.8 to 2.4 seconds and 1 pause in `session.json`.
  - `record --window "no such window title 12345"` exits 2, and standard error says no window matches.
  - `record --window ""` with several windows open exits 2 and lists titles.
  - `record --region 100000,0,640,360` exits 2. `--region 0,0,8,8` exits 2.
  - `record` started with standard input closed and `--seconds 2` runs 2 seconds and exits 0.
  - `record` with no `--seconds`, sent Ctrl+Break after 2 seconds (`GenerateConsoleCtrlEvent` to a child in its own process group), exits 0 and `screen.mp4` plays 1.5 to 3 seconds.
  - `record --out Q:\nope` on a drive that does not exist exits 1 with a plain message.
- [ ] **Step 6: Build with `cargo build --release` and run the tool tests.** Expected: pass.
- [ ] **Step 7: Commit** `feat(rust): screenrec command-line tool`.

---

### Task 12: Measure against the C# app

**Files:**
- Create: `rust/README.md` with build, test and usage, and the measured table.
- Create: `rust/bench/compare.ps1`, a script that runs the measurements below so they can be repeated.

- [ ] **Step 1: Size.** Report the size of `rust/target/release/screenrec.exe` in bytes, and confirm with `dumpbin /dependents` or an equivalent that it needs only system DLLs. Target: under 5 MB.
- [ ] **Step 2: Same recording, both engines.** Record a 1280x720 region for 10 seconds with computer sound, once with `screenrec` and once with the C# app's engine (through the C# end-to-end script or a small test). Compare: the set of files, pixel sizes, frame counts (within 10 frames), durations (within 0.3 seconds), and the fields of `session.json`. List every difference.
- [ ] **Step 3: 1920x1080 at 60 for 5 minutes** on a 1920x1080 window with busy content, with logo, microphone and computer sound. Report dropped frames, the 4 durations, stop time, average processor use and peak memory of `screenrec`. Target: under 1% dropped, stop under 1 second.
- [ ] **Step 4: 3840x2160 at 30 for 2 minutes, busy content, 3 runs.** Report dropped frames for each and the median. Target: median at or under 5.7%. Confirm `final.mp4` reads as constant frame rate in each.
- [ ] **Step 5: Memory.** Measure peak working set and private bytes of `screenrec` during Step 3, and of the C# app recording the same thing. Report both.
- [ ] **Step 6: Crash check.** Kill `screenrec` 30 seconds into a recording. Every file must open, the video must play at least 25 seconds, and `session.json` must parse.
- [ ] **Step 7: HDR.** Repeat the gray and white patch check from Task 5 on a recorded frame of `screen.mp4`. Report the values.
- [ ] **Step 8: Camera.** If a camera is connected, record 5 seconds with `--camera` and look at 1 frame of `final.mp4` and of `webcam.mp4` with the Read tool: the shape, the mirror and the position. If no camera is connected, write "camera not checked on hardware" in the README's limits.
- [ ] **Step 9: Write `rust/README.md`** with the usage block, the table of targets against results with misses stated as misses, and the limits. Commit as `docs(rust): stage 1 measurements`.

If a target is missed, stop and report the numbers. Do not tune silently.
