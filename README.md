# Screen Recorder

Screen Recorder takes screenshots and records the screen for app demos. You pick the whole desktop, one app window or a region. The app is built to be small and fast, with a native engine for each system.

It records a finished video and the raw files next to it, so you can edit in any other app. There is no editor inside.

## Status

| Part | Branch | State |
| --- | --- | --- |
| Windows app, C# | [`windows-v1`](../../tree/windows-v1) | Works. This is the app with screens today. |
| Windows engine and command-line tool, Rust | [`rust-port`](../../tree/rust-port) | Stage 1 done. No screens yet. |
| Windows app screens, Rust | not started | Next stage. Replaces the C# app. |
| macOS app | not started | Needs its own engine. |
| Linux app | not started | Needs its own engine. |
| Phone as a second camera (iPhone, Android) | not started | Planned. |
| Control by an AI agent, for scripted demos | not started | Planned. |

The `main` branch holds this page and the design documents. The code is on the 2 branches above.

## What the Windows app does

- Takes a screenshot of the desktop, a window or a region.
- Copies the picture to the clipboard as soon as you pick the target, so you can paste at once.
- Shows the picked region with a frame that you can move and resize.
- Offers Copy, Save, Share and Cancel under the region.
- Shows a card after each saved screenshot, with Open, Copy, Share, Crop and Delete. Delete sends the file to the Recycle Bin.
- Records video with pause and resume, the microphone and the computer's sound.
- Can show a webcam picture and a logo on the recording, in a shape you choose.
- Remembers the last region.

Each recording is one folder with these files:

| File | Content |
| --- | --- |
| `final.mp4` | The finished video, with overlays and sound |
| `screen.mp4` | The screen alone |
| `webcam.mp4` | The camera alone, when a camera is on |
| `mic.wav`, `system.wav` | The microphone and the computer's sound |
| `session.json` | Times, sizes, layout and notes |

On screens larger than 2560x1440, recording runs at 30 frames per second at most. `final.mp4` keeps the full size, and `screen.mp4` is scaled to fit 1920x1080.

## Build the Windows app (C#)

You need Windows 11 and the .NET 10 SDK.

```bash
git checkout windows-v1
```

```bash
dotnet publish src/ScreenRecorder.App -c Release -r win-x64 --self-contained -o dist/ScreenRecorder
```

Start `dist/ScreenRecorder/ScreenRecorder.App.exe`. The folder runs on a PC without .NET installed. It is about 177 MB.

## Build the Rust tool

You need Windows 11 and Rust with the MSVC toolchain.

```bash
git checkout rust-port
```

```bash
cd rust && cargo build --release -p recorder-cli
```

The result is `rust/target/release/screenrec.exe`, one file of about 0.5 MB. It lists monitors and windows, takes screenshots and records, and prints one JSON line for each command. The usage and all measurements are in `rust/README.md` on that branch.

## Known limits

All numbers come from one PC: Intel UHD Graphics, Core i5-14500HX, one 3840x2160 HDR screen.

- The Rust engine misses 2 of its speed targets. Stop took more than 1 second in 2 of 15 runs, with 3.5 seconds as the longest. Recordings of 10 minutes at 1920x1080 and 60 frames per second dropped up to 4.6% of frames, against a target of 1%. The cause is not found yet.
- Recording at 3840x2160 runs at the edge of this graphics chip, and both engines drop frames there. Dropped frames are written as repeats, so the finished video plays, with short stutters.
- The camera code has never run on a real camera. It is checked with stand-ins only.
- HDR highlights brighter than standard white clip to white.
- Windows 10 is untested.

## Design documents

- [Windows recorder design](docs/superpowers/specs/2026-11-20-windows-recorder-design.md)
- [Windows recorder plan](docs/superpowers/plans/2026-11-20-windows-recorder.md)
- The Rust port design and the recording folder format (`docs/session-format.md`) are on the `rust-port` branch.
