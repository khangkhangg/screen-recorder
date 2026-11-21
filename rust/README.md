# screenrec

`screenrec.exe` is a command-line screen recorder for Windows, written in Rust. It is Stage 1 of the port of the C# Screen Recorder app, and it has the full recording engine with no screens. Stage 2 adds the app's screens on top of the same engine.

The workspace has 3 crates. `recorder-core` holds the logic that does not depend on Windows and builds on any system. `recorder-win` holds the Windows capture, sound, encoding and screenshot code. `recorder-cli` is the `screenrec` tool.

## Build and test

You need Rust 1.99.1 or newer with the MSVC toolchain, on Windows 11 or Windows 10 version 2004 or newer.

1. Run `cargo build --release -p recorder-cli` in `rust/`. The tool is `rust/target/release/screenrec.exe`, one file with no runtime beside it.
2. Run `cargo test` in `rust/` for the tests that need no desktop.
3. Run `cargo test -- --ignored` in `rust/` on a PC with a desktop and a graphics chip for the capture tests. They record the screen for a few seconds into a temporary folder and delete it.

`rust/bench/compare.ps1` repeats the measurements below and runs the C# engine next to `screenrec`. It opens its own test windows, some of them full screen, for several minutes.

## Usage

```
screenrec list monitors|windows|cameras|mics
screenrec screenshot --monitor N | --window "TITLE" | --region X,Y,W,H [--monitor N] [--out FOLDER] [--no-copy]
screenrec record     --monitor N | --window "TITLE" | --region X,Y,W,H [--monitor N]
                     [--out FOLDER] [--fps 30|60] [--mic [ID]] [--system-audio] [--camera [ID]]
                     [--camera-shape circle|rounded|square] [--camera-pos X,Y,SIZE]
                     [--logo FILE] [--logo-corner tl|tr|bl|br] [--no-final] [--seconds N]
screenrec --help | --version
```

Monitors are numbered from 1. `--window` matches part of a window title, ignoring case. `--region` is in pixels of `--monitor N`, which is monitor 1 when left out. Files go to `Videos\ScreenRecorder` unless `--out` is given. `screenrec` never reads the app's `settings.json`, so the same flags give the same result on any PC.

`record` runs until `--seconds` pass, until Ctrl+C, or until the line `stop` arrives on standard input. The lines `pause` and `resume` pause and resume it. It prints `recording` on standard error once it has started.

Every command prints 1 JSON object on standard output. Exit code 0 is success, 1 is a failure while running, and 2 is a bad argument or a target that cannot be found.

## Measurements

All numbers were measured on 2026-11-22 on the build PC: Intel UHD Graphics, Core i5-14500HX with 20 logical cores, one 3840x2160 HDR screen at 150% scaling, no camera. The C# column is the C# engine from `src/`, run on the same PC in the same session by `rust/bench/compare.ps1`, with the same settings. Busy content is a window of the bench script that redraws 300 random shapes about 60 times a second.

| Measure | C# engine | Rust target | Rust result | |
| --- | --- | --- | --- | --- |
| Size of the tool | 176,948,458 bytes, a folder | Under 5 MB, one file | 525,824 bytes, one file | Hit |
| Start, `screenrec list monitors`, median of 10 | No command-line tool | Under 150 ms | 18.8 ms | Hit |
| Peak memory, 1920x1080 at 60, 5 minutes | 467.9 MB working set, 468.8 MB private | Lower than C# | 385.4 MB working set, 422.8 MB private | Hit |
| 1920x1080 at 60, 5 minutes, dropped frames | 0 of 18,014 | Under 1% | 0 of 18,013 | Hit |
| 3840x2160 at 30, 2 minutes, busy, median of 3 runs | 2.44% (0%, 2.44%, 2.72%) | At or under 5.7% | 0% (0 drops in all 3 runs) | Hit |
| Stop time, 5-minute 1080p recording, 1 run | 0.61 s | Under 1 s | 1.27 s | **Miss** |
| Stop time, 10-second and 2-minute recordings, 4 runs | 0.26 s to 0.41 s | Under 1 s | 0.27 s to 0.34 s | Hit |
| Killed 30 s into a recording | Not run | All files play | All 5 files open, video 29.7 s and 30.0 s, `session.json` parses | Hit |
| HDR gray 128 and white patch in `screen.mp4` | Gray 139, white 255 | Gray 126 to 130, white 250 to 255 | Gray 128, white 255 | Hit |

Stop time is measured from outside, from the `stop` line on standard input until the process exits. The 1.27 s stop happened once, after the longest recording, and it was not repeated.

Processor use while recording 1920x1080 at 60 with busy content was 1.57% of the whole processor for `screenrec` and 2.27% for the C# engine. At 3840x2160 the peak working set was 710 MB to 767 MB for `screenrec` and 880 MB to 903 MB for the C# engine.

A 10-second 1280x720 recording with computer sound from both engines gave the same file set, the same frame sizes and 612 frames in both `final.mp4` files. The only `session.json` differences were the start time and the dropped frame count (0 for Rust, 20 for C#). Every recording of both engines read as constant frame rate.

## Limits

- The camera code has never run on a real camera, because the build PC has none. It is checked with stand-ins only.
- HDR highlights brighter than standard white clip to white. They do not roll off.
- Windows 10 is untested, because no Windows 10 PC is available.
- 3840x2160 runs at the edge of this graphics chip. Earlier measurements with the C# engine dropped 3.8% to 13.9% of frames on a busier screen than the bench window.
- WAV files hold about 3.1 hours of sound.
