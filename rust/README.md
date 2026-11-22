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

`record` runs until `--seconds` pass, until Ctrl+C, or until the line `stop` arrives on standard input. The lines `pause` and `resume` pause and resume it. It prints `recording` on standard error once it has started. When the environment variable `SCREENREC_TRACE` is set, it also prints the time of each stop step and the late ticks on standard error.

Every command prints 1 JSON object on standard output. Exit code 0 is success, 1 is a failure while running, and 2 is a bad argument or a target that cannot be found.

## Measurements

All numbers were measured on 2026-11-22 on the build PC: Intel UHD Graphics, Core i5-14500HX with 20 logical cores, one 3840x2160 HDR screen at 150% scaling, no camera. The C# column is the C# engine from `src/`, run on the same PC in the same session by `rust/bench/compare.ps1`, with the same settings. Busy content is a window of the bench script that redraws 300 random shapes. While recording at 3840x2160 it was painted 26.4 (C#) to 29.4 (Rust) times a second, median of 10 runs.

| Measure | C# engine | Rust target | Rust result | |
| --- | --- | --- | --- | --- |
| Size of the tool | 176,948,458 bytes, a folder | Under 5 MB, one file | 527,872 bytes, one file (HEAD `aaddb63`) | Hit |
| Start, `screenrec list monitors`, median of 10 | No command-line tool | Under 150 ms | 18.8 ms | Hit |
| Peak memory, 1920x1080 at 60, 5 minutes | 467.9 MB working set, 468.8 MB private | Lower than C# | 385.4 MB working set, 422.8 MB private | Hit |
| 1920x1080 at 60, 5 minutes, dropped frames | 0 of 18,014 | Under 1% | 0 of 18,013 | Hit |
| 1920x1080 at 60, 10 minutes, calm screen, dropped frames, 6 runs | Not run | Under 1% | 50, 1,656, 724, 52, 120 and 1,240 of about 36,000 (up to 4.6%) | **Miss**, cause not found |
| 3840x2160 at 30, 2 minutes, busy, median of 10 alternated runs | 126 of 3,600 (3.5%, range 16 to 308) | At or under 5.7% | 0 (range 0 to 30, highest 0.83%) | Hit. The ranges overlap, so neither engine is shown better |
| Stop time, 1080p60 recordings of 1, 2, 5 and 10 minutes, 3 runs each, then 3 more 10-minute runs | 0.61 s | Under 1 s | 13 of 15 under 1 s. Two 10-minute runs took 3.505 s and 1.291 s. The earlier single 5-minute run took 1.27 s | **Miss** |
| Stop time, 10-second and 2-minute recordings, 4 runs | 0.26 s to 0.41 s | Under 1 s | 0.27 s to 0.34 s | Hit |
| Killed 30 s into a recording | Not run | All files play | All 5 files open, video 29.7 s and 30.0 s, `session.json` parses | Hit |
| HDR gray 128 and white patch in `screen.mp4` | Gray 139, white 255 | Gray 126 to 130, white 250 to 255 | Gray 128, white 255 | Hit |

Stop time is measured from outside, from the `stop` line on standard input until the process exits. Three 5-minute runs stopped in 0.45 to 0.85 s. One of three 10-minute runs stopped in 3.505 s, with `final.mp4` done at 230 ms and `session.json` at 3,242 ms. Three later traced 10-minute runs stopped in 0.559, 1.291 and 0.685 s. In those, finishing the files and `session.json` took 284, 618 and 339 ms, and `session.json` alone took 4 to 9 ms. The rest of each stop time happens outside the traced steps, and its cause is not found.

Processor use while recording 1920x1080 at 60 with busy content was 1.57% of the whole processor for `screenrec` and 2.27% for the C# engine. At 3840x2160 the peak working set was 710 MB to 767 MB for `screenrec` and 880 MB to 903 MB for the C# engine. A 2-minute busy rerun gave Rust 402 to 438 MB working set and 434 to 487 MB private, C# helper 478 to 496 MB and 479 to 495 MB. Working set is lower. Private bytes overlap.

A 10-second 1280x720 recording with computer sound from both engines gave the same file set, the same frame sizes and 612 frames in both `final.mp4` files. The only `session.json` differences were the start time and the dropped frame count (0 for Rust, 20 for C#). Every Rust recording read as constant frame rate. The C# `final.mp4` was not constant in 10 of 10 runs at 3840x2160.

## Limits

- The camera code has never run on a real camera, because the build PC has none. It is checked with stand-ins only.
- HDR highlights brighter than standard white clip to white. They do not roll off.
- Windows 10 is untested, because no Windows 10 PC is available.
- 3840x2160 runs at the edge of this graphics chip. In the 10 alternated 4K runs on the bench window, the C# engine dropped 16 to 308 of about 3,600 frames (0.4% to 8.6%).
- WAV files hold about 3.1 hours of sound.
- Stop time can pass 1 s after a 10-minute recording. It took 3.505 s once and 1.291 s once. The traced steps took at most 618 ms, so the slow part is outside them.
- A 10-minute recording of a calm screen can drop frames, up to 1,656 of about 36,000 (4.6%). In 3 traced runs, the runs with more drops also had more late ticks (11, 18 and 163) and longer ticks (68, 70 and 159 ms). The trace does not show what made the ticks slow.
