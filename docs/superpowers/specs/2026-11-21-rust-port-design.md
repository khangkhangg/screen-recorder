# Screen Recorder, Rust port design

Date: 2026-11-21

## Intent

The Windows app works, but it is heavy: a 170 MB folder and 186 MB of memory at idle. It was built in C# with WinUI 3, and it carries the .NET runtime and the Windows App SDK inside its folder. The user's first goal was "as light and as fast as possible", so the user decided to port the app to Rust.

The port keeps every behavior the user approved and drops the weight. The C# app stays as the working reference until the Rust app replaces it.

## Goals

1. The same features and the same look as the C# app at commit `47a535e` on branch `windows-v1`.
2. A small footprint. The targets are an executable under 5 MB, idle memory under 40 MB, and a start under 150 ms (process start until the bar is visible). Each is measured at the end of a stage and reported, hit or miss.
3. Speed no worse than the C# app: 1920x1080 at 60 frames per second with under 1% dropped frames on integrated graphics, and idle processor use under 1%.
4. A code split that lets the later macOS and Linux apps reuse the system-independent part.
5. Recordings and settings stay compatible. The Rust app reads and writes the same `session.json` (see `docs/session-format.md`) and the same `settings.json`, so the user's settings carry over.

## Non-goals

- New features. The agent control feature (a local control channel, a command-line tool and an MCP server, with smooth pointer motion) gets its own design after the port. Stage 1's command-line tool is its base.
- The macOS, Linux and phone apps.
- Fixing the 4K dropped-frame limit of integrated graphics beyond what the user already decided.
- A drawing or annotation editor.

## Anti-goals

The port fails, even if every feature works, when any of these is true.

- The app is not clearly smaller and lighter than the C# app.
- A recording that the C# app would have saved is lost.
- A window of the app appears in a recording or screenshot, even for one frame.
- A feature the user approved is missing or behaves differently, with no word to the user.
- The app stops feeling instant: a visible delay on start, on a click, or while dragging the region frame.

## Constraints

- Rust, stable toolchain, target `x86_64-pc-windows-msvc`. Rust 1.99.1 with the MSVC toolchain is installed on the build PC.
- No interface toolkit. The app draws its own screens (the user's decision).
- Drawing with Direct2D and DirectWrite. Capture with Windows.Graphics.Capture. Encoding with the Media Foundation Sink Writer on the hardware H.264 encoder. Sound with WASAPI. Windows popup menus for menus.
- Dependencies stay minimal: Microsoft's `windows` crate for system calls, and `serde` with `serde_json` for the 2 JSON files. Any other crate needs a stated reason in the plan and must not pull in an async runtime, a GUI toolkit or a media library.
- Windows 11, and Windows 10 version 2004 or newer.
- No account, no network use, no telemetry.
- Frames stay on the graphics chip from capture to file.

## Approaches

For the screens, 2 ways were considered.

- Draw them ourselves on Windows with Direct2D. Smallest (an estimated 2 to 5 MB), most native feel, Windows-only screens. **Picked by the user.**
- A cross-platform Rust interface toolkit. One set of screens for 3 systems, but larger (an estimated 8 to 20 MB), a less native look, system-specific code still needed for the special windows, and license terms on some toolkits.

For the order of work, 3 ways were considered.

- Engine first with a command-line tool, then the screens. The risky part is proven first, real size and speed numbers arrive early, and the command-line tool is the base for agent control. **Picked by the user.**
- Screens first with a stand-in engine. The look arrives early, but the risky part comes last.
- Swap parts inside the C# app one at a time. The app stays usable, but stays at 170 MB until the last step, and the glue between the 2 languages is thrown away.

Keeping C# and compiling it to native code with trimming was also on the table (an estimated 40 to 70 MB). The user chose to go straight to Rust.

## Structure

One Cargo workspace in a new folder `rust/` in this repository.

| Crate | Holds | Depends on the system |
| --- | --- | --- |
| `recorder-core` | Frame math, layout, session file, settings, recording clock, recorder state machine, hotkey text, region frame geometry, action bar and card placement, crop math, file naming | No |
| `recorder-win` | Screen capture, webcam, sound capture, WAV writer, video writer, encoder probe, compositor, recording session, screenshot, recycle | Yes, Windows |
| `recorder-cli` | The command-line tool `screenrec` | Uses the 2 crates above |
| `recorder-app` | The app: bar, picker, region frame, action bar, overlays, preview card, crop window, settings window, hotkeys | Yes, Windows |

`recorder-core` compiles and passes its tests on any system. The later macOS and Linux apps reuse it and replace `recorder-win`.

## Stages

Each stage gets its own plan and ends with something usable and measured.

1. **Engine and command-line tool.** `recorder-core`, `recorder-win` and `recorder-cli`. Detailed below.
2. **The app's screens.** `recorder-app`, to full parity with the C# app. Its own design note confirms the drawing layer and the window setups before the plan is written.
3. **Switch over.** The Start menu shortcut points at the Rust app, the README is rewritten, and the C# projects move to a `legacy/` folder or are removed, as the user decides then.

## Stage 1 in detail

### What it delivers

`screenrec.exe`, a command-line tool that records and takes screenshots with the full engine, with no screens.

```
screenrec list monitors|windows|cameras|mics
screenrec screenshot --monitor N | --window "TITLE" | --region X,Y,W,H [--monitor N] [--out FOLDER] [--no-copy]
screenrec record     --monitor N | --window "TITLE" | --region X,Y,W,H [--monitor N]
                     [--out FOLDER] [--fps 30|60] [--mic [ID]] [--system-audio] [--camera [ID]]
                     [--camera-shape circle|rounded|square] [--camera-pos X,Y,SIZE]
                     [--logo FILE] [--logo-corner tl|tr|bl|br] [--no-final] [--seconds N]
```

- `record` runs until `--seconds` passes, or until Ctrl+C, or until the line `stop` arrives on standard input. The lines `pause` and `resume` on standard input pause and resume. This simple text protocol is what a later control channel builds on.
- Every command prints one JSON object on standard output when it ends: for `record`, the folder, the files, the dropped frame count and the notes. Errors go to standard error with a plain message and a non-zero exit code.
- `--window` matches a window title by case-insensitive substring and fails with a plain message when 0 or more than 1 window matches, listing the matches.

### Behavior it must match

The engine reproduces the C# engine's behavior. The rules, all decided earlier:

- Files per recording: `final.mp4`, `screen.mp4`, `webcam.mp4`, `mic.wav`, `system.wav`, `session.json`, in a folder named `yyyy-MM-dd HH-mm-ss`, with ` (2)` and so on when it exists. Files for sources that are off are not written.
- One shared clock. Constant frame rate. Every file has the same length. Pause cuts the paused time from every file.
- H.264 in fragmented MP4, so a crashed recording still plays. 48,000 Hz stereo sound, 32-bit float WAV, AAC at 192 kbit/s in `final.mp4`.
- Bitrate is width times height times frame rate, divided by 10.
- Window capture keeps its starting frame size and letterboxes after a resize. A closed window stops and saves. A minimized window holds its last frame.
- Frames larger than the encoder accepts are scaled down, with a note.
- Screens above 2560x1440 record at 30 frames per second, `final.mp4` keeps full size and `screen.mp4` is scaled to fit 1920x1080 (the user's decision of 2026-11-21).
- A hardware encoder is required when asked for. With none, the engine records at 30 frames per second with the software encoder and adds a note.
- A slow encoder drops frames from `final.mp4` first, as repeated frames, and never blocks capture. The dropped count is reported.
- A lost webcam, microphone or sound device ends that file cleanly, adds a note, and the recording goes on. A camera that fails to open does not cancel the recording.
- A full disk stops the recording and keeps every file playable.
- `session.json` is rewritten at every change, through a temporary file and a rename, and never holds the full path of the logo.
- The camera is a centered square crop, mirrored, in a circle, rounded square (corner radius 18% of the side) or square. The logo keeps its transparency. Logos over 20 MB or 8192 pixels are refused with a note.
- Screenshots save as PNG and copy to the clipboard. A file name that exists gets a counter.

### Lessons from the C# build that are requirements here

- Sound for `final.mp4` is held back until the video up to that time is written. Without this, a stop took up to 67 seconds.
- The video writer checks that a hardware encoder was really loaded, and does not trust the request.
- Windows can deliver no loopback sound while the PC is silent, so sound is padded from the clock.
- The computer's own sound is captured with WASAPI loopback. The higher-level sound API cannot do it in a desktop app.
- HDR screens are converted to standard colors with Direct2D's HDR tone map effect, which the C# drawing library lacked. The C# app clipped highlights and came out about 4% light in the midtones. The Rust engine should do better, and the result is checked by picture on the build PC's HDR screen.
- The webcam and the sound devices can vanish at any moment, and their loss events arrive on other threads.
- Test recordings go to a temporary folder and are deleted by the test that made them.

### Tests

- `recorder-core`: the C# pure-logic tests are ported one for one, with the same inputs and expected values. There are about 150 of them across the C# Core and App test projects. This is how "the same behavior" is checked.
- `recorder-win`: integration tests that need a real desktop and graphics chip, run on the build PC. They cover the same cases as the C# capture tests, including the fakes for a lost source, a full disk, a resized or closed source and a slow encoder.
- `recorder-cli`: tests that run the built tool for a few seconds and check its JSON output and its files.

### How Stage 1 is judged

Measured on the build PC (Intel UHD Graphics, Core i5-14500HX, one 3840x2160 HDR screen at 150% scaling), next to the C# numbers:

| Measure | C# app | Target for Rust |
| --- | --- | --- |
| Size of the tool | 170 MB folder | Under 5 MB, one file |
| Memory while recording 1080p at 60 | Not measured yet, measure both | Lower than C# |
| 1920x1080 at 60, 5 minutes, dropped frames | 0 of 18,003 | Under 1% |
| 3840x2160 at 30, 2 minutes, busy screen | 3.8% to 13.9% | No worse than C# |
| Stop time | 0.19 to 0.64 s | Under 1 s |
| Crash at 30 s | All files play | All files play |

A recording made by `screenrec` and one made by the C# app with the same settings must be the same in file set, sizes in pixels, frame counts within 1 frame per second of length, and `session.json` fields.

### Calls made after the builder check (designer's calls)

- Settings. `screenrec` never reads `settings.json`. It uses only its flags, with fixed defaults: output folder `Videos\ScreenRecorder`, 60 frames per second, and the microphone, computer sound, camera and logo all off unless a flag turns them on. A script then always gets the same result on any PC. The app in Stage 2 reads and writes `settings.json`.
- Constant frame rate. `final.mp4` is a true constant frame rate file, also when the encoder falls behind: every dropped frame is written as a repeated frame. The C# app wrote at most 2 repeats per tick, so its busy 4K files read as not constant. If true repeats raise the dropped count above the C# numbers, the stage reports it and does not hide it.
- HDR. The engine reads the screen's real standard-white level from Windows and does not assume 200 nits. Standard content, such as windows and text, must match the screen: a mid-gray patch of value 128 reads between 126 and 130 in the recording. Bright HDR highlights roll off and are not clipped to flat white. Both are checked by picture on the build PC's HDR screen.
- 4K pass line. "No worse than C#" means the median dropped-frame share of 3 busy 2-minute runs at 3840x2160 and 30 frames per second is at or under 5.7%.
- Frames in main memory. "Frames stay on the graphics chip" has 3 accepted exceptions, the same as in the C# app: the software encoder fallback, webcam frames that arrive in main memory and are uploaded once, and screenshots that are read back to write a PNG and fill the clipboard.
- The microphone moves from the higher-level sound API to WASAPI, like the computer's sound. This changes the method only.
- Windows 10. No Windows 10 PC is available, so support for Windows 10 version 2004 stays untested, as it is for the C# app. The code guards every call that Windows 10 lacks.
- Size. The tool is built with link-time optimization, abort on panic, size optimization and a statically linked C runtime, so it is one file with no extra runtime file beside it.

### Open point: checking the camera

The build PC has no webcam, and the C# camera code never ran on real hardware. Unless a camera is connected for Stage 1, the camera path is checked with stand-ins only, and the design says so in the stage's report.

## Stage 2 in brief

The screens reproduce what the user approved: the compact bar with the source switch and toggles, the three dot menu, the region picker, the region frame with its title tab, corner marks, move and resize, the action bar, the camera and logo overlays, the screenshot preview card, the crop window, the settings window, global hotkeys, the app icon, single instance, and the remembered region and bar position.

Requirements already known from the C# build:

- Every window is excluded from capture before it is first shown. Tooltips are drawn by the app in its own windows, so they are covered by the same rule. This closes the C# app's first-frame gap.
- Delete moves a file to the Recycle Bin and never removes it for good. The C# app's hard-link guard is the reference, with a link-count check before the spare name is removed, and cleanup of leftover spare names on start.
- Icon glyphs are named constants written as visible escapes.
- The region frame's inside lets every click through. Its grab areas are the only parts that take input.
- A third-party top bar on the build PC stays above topmost windows. The app does not fight it.

Stage 2 gets its own design note for the drawing layer, written after Stage 1, because the measurements from Stage 1 and a short spike on a translucent, click-through, custom-drawn window decide details.

## What is left to the builder

- Module layout inside each crate.
- How the compositor uses Direct3D 11 and Direct2D, as long as frames stay on the graphics chip.
- Whether the webcam is read through Media Foundation's source reader or the WinRT capture API, as long as the behavior rules hold.
- Threading inside the engine, as long as the recording thread is never blocked by `final.mp4`.
- The argument parser: hand-written or a small crate, whichever keeps the tool smallest.
