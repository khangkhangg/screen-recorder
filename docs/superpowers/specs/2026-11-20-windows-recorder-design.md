# Windows screen recorder, version 1 design

Date: 2026-11-20

## Intent

People who make app demos need to take a screenshot or record the screen quickly, with their face and a logo on the picture. The app must be as light and fast as possible. Users edit in other apps, so this app gives them a finished video and the raw files, and has no editor.

This is app 1 of 5. The order is Windows, Android phone camera, macOS, iPhone camera, Linux. Each app gets its own design. This document covers Windows only.

## Goals

Items 1 to 5 were decided by the user. Items 6 to 10 are the designer's proposals, which the user accepted.

1. Screenshot or record the whole desktop, one app window, or a dragged region.
2. Show the webcam live on the capture. The user can move it, resize it, turn it off, and pick its shape (circle, rounded square, square).
3. Show a logo image that the user picks, placed in a corner. The user can turn it off.
4. Save one finished MP4 with the camera and logo baked in, plus separate raw files.
5. No editor.
6. Record the microphone and the computer's sound.
7. Record at up to 60 frames per second at the captured area's own resolution, encoded on the graphics chip.
8. Screenshots save as PNG and copy to the clipboard.
9. Controls are one small floating bar plus global hotkeys. The bar never appears in the recording.
10. The layout and session format is written down so the macOS and Linux apps can use the same one.

## Non-goals for version 1

- Phone camera. It comes with the Android app in step 2. Keep the layout able to hold a second camera item later.
- Drawing or annotation on screenshots.
- Trimming, cutting or any editing.
- Streaming, cloud upload or sharing links.
- Cursor effects, zoom effects, key press display.
- GIF export.

## Anti-goals

The app fails, even if every feature works, when any of these is true.

- Recording drops frames or makes the demoed app feel slow on a normal laptop.
- The finished MP4 and the raw files are out of sync with each other.
- A crash loses the whole recording.
- The app's own bar or overlay windows show up in the raw screen file.
- The app feels heavy to open, such as a splash screen or a slow start.

## Constraints

- Fully native Windows app. No web view for the app's screens.
- C# with WinUI 3 (Windows App SDK).
- Capture with Windows.Graphics.Capture.
- Encode H.264 with the graphics chip's encoder through Media Foundation.
- Windows 11, and Windows 10 version 2004 or newer (designer's call, because hiding a window from capture needs 2004).
- No account, no network use, no telemetry.

## Approaches

For the code base, 3 ways were considered.

- Fully native per system. C# and WinUI 3 on Windows, Swift and SwiftUI on macOS. Frames stay on the graphics chip from capture to file, which is the fastest path. The cost is that layout code is written once per system. **Picked by the user.**
- Shared Rust core with thin native screens. Layout code is written once, but passing frames between Rust and each system's encoder without copies is hard, and the bugs are slow to find.
- Electron or Tauri. Dropped, because both put a web page inside the app and the user asked for native.

For the output, 3 ways were considered.

- Separate raw files only, with the live layout as a preview. Lightest.
- Finished MP4 plus separate raw files. **Picked by the user.** It costs up to 3 video encodes at once.
- Finished MP4 only.

## How it works

### Sources

- Screen. One monitor, one window, or a region. A region is a monitor capture cropped to the dragged rectangle.
- Webcam. Any camera Windows lists. Optional.
- Microphone. Optional.
- Computer sound (loopback). Optional.
- Logo. A PNG or JPG file the user picks. Optional.

### Live layout

The camera and the logo are small overlay windows on the screen, inside the captured area. The user drags and resizes the camera, and picks a corner for the logo. These windows and the control bar are excluded from screen capture, so the raw screen file stays clean.

The app draws the camera and logo into the finished video itself, at the same place the user sees them. Positions and sizes are stored as fractions of the captured frame, so they hold for any resolution.

The user can change the layout while recording, and the finished video follows.

### Files

Each recording makes one folder, named by date and time, in a folder the user chooses (default is `Videos\ScreenRecorder`).

| File | Content |
| --- | --- |
| `final.mp4` | Screen with camera and logo baked in, mic and computer sound mixed into one audio track |
| `screen.mp4` | Screen only, no overlays, no sound |
| `webcam.mp4` | Webcam only, full frame, no shape mask |
| `mic.wav` | Microphone only |
| `system.wav` | Computer sound only |
| `session.json` | Sources used, start time offset of each file, layout changes with timestamps |

All files start from one shared clock, and `session.json` records any offset, so an editor can line them up. Files for sources that are off are not written.

`session.json` is the shared format for the later macOS and Linux apps. The designer's call is that its fields use plain names and fractions, with a version number.

### Fallback on weak hardware

If the graphics chip cannot run 3 encodes at once, the app says so before recording and offers to save the raw files only, without `final.mp4`.

When recording starts, the app checks that a hardware H.264 encoder exists and that 3 encoders open. It runs no speed benchmark. With no hardware encoder at all, the app warns and records with the software encoder at 30 frames per second.

If encoding falls behind during a recording, the app drops frames from `final.mp4` first and never slows the capture or the demoed app. It reports the dropped frame count when the recording stops.

### Edge cases (designer's calls)

- Video files have a constant frame rate. A clock drives the frames and repeats the last one when the screen did not change, because many editors drift out of sync on variable frame rate video.
- Pause cuts the paused time from every file, so all files stay the same length and aligned. `session.json` lists the pause points.
- In window mode, the frame size is fixed at the window's size when recording starts. If the window is resized, its picture is scaled to fit with black bars, keeping its proportions. The camera and logo follow the window when it moves. A minimized window holds its last frame. A closed window stops and saves the recording.
- If the captured area is larger than the encoder accepts, the app scales it down to fit, keeping its proportions, and says so before recording.
- HDR screens are converted to standard colors, so the video does not look washed out.
- The reference machine is a laptop with Intel Iris Xe class built-in graphics. The target there is 1920x1080 at 60 frames per second with under 1% dropped frames, and 4K at 30.
- On screens larger than 2560x1440 the recording runs at 30 frames per second, and `screen.mp4` is scaled to fit 1920x1080 while `final.mp4` keeps the full size. The user decided this on 2026-11-21, because integrated graphics cannot encode 2 files of 3840x2160 at once.

### Screenshot

The user picks desktop, window or region the same way as for recording. The PNG is saved to the output folder and copied to the clipboard. The camera and logo are not part of screenshots (designer's call).

### Controls

- Floating bar with the target picker (desktop, window, region), camera on or off, shape, mic, computer sound, logo, record, screenshot, and settings.
- Global hotkeys for start or stop recording and for screenshot. Defaults are the designer's call and the user can change them.
- A 3 second countdown before recording starts, which the user can turn off (designer's call).
- While recording, the bar shrinks to a timer with stop and pause.

## Left to the builder

- The exact Media Foundation and Direct3D plumbing, as long as frames stay on the graphics chip.
- Whether drawing uses Win2D or Direct3D directly.
- The file writing method that keeps a crashed recording playable.
- Project layout, test approach and installer type.
- Visual design of the bar, within Windows 11 look and feel.
