# Screen Recorder for Windows

Screen Recorder takes screenshots and records the screen, with your webcam and a logo placed on the picture. You get a finished MP4 plus the raw files, so you can edit them in another app. It runs on Windows 11 and Windows 10 version 2004 or newer, uses no network and has no account.

You pick the whole desktop, one window or a dragged region from a small floating bar. The bar and the camera and logo windows never show up in the raw screen file.

## Build, run and test

You need the .NET 10 SDK.

1. Build everything with `dotnet build ScreenRecorder.slnx`.
2. Run the app with `dotnet run --project src/ScreenRecorder.App`.
3. Run all tests with `dotnet test ScreenRecorder.slnx`.
4. Make a folder that runs on a PC without .NET with `dotnet publish src/ScreenRecorder.App -c Release -r win-x64 --self-contained`. The result is in `src/ScreenRecorder.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`, and you start `ScreenRecorder.App.exe` there.

## Output files

Each recording makes one folder, named by date and time, in `Videos\ScreenRecorder` unless you choose another folder.

| File | Content |
| --- | --- |
| `final.mp4` | Screen with camera and logo baked in, mic and computer sound mixed into one audio track |
| `screen.mp4` | Screen only, no overlays, no sound |
| `webcam.mp4` | Webcam only, full frame, no shape mask |
| `mic.wav` | Microphone only |
| `system.wav` | Computer sound only |
| `session.json` | Sources used, start time offset of each file, layout changes with timestamps |

Files for sources that are off are not written. All files start from one shared clock, and pauses are cut from every file, so the files stay the same length.

Screens larger than 2560x1440 record at 30 frames per second. There `final.mp4` keeps the full size and `screen.mp4` is scaled to fit 1920x1080. Smaller screens record both files at full size at up to 60 frames per second.

## Measured numbers

All numbers come from one laptop with Intel UHD Graphics on a Core i5-14500HX, 20 logical cores, and one built-in 3840x2160 screen running in HDR. The design's reference machine has Intel Iris Xe class graphics, and this laptop stands in for it. The measurements used the published build, with the microphone, computer sound and a logo on and no camera.

| Check | Target | Result |
| --- | --- | --- |
| Start time, process start until the bar shows | under 1 s | median 315 ms over 5 starts (298 to 830 ms) |
| 1920x1080 at 60 fps for 5 minutes, moving content | under 1% dropped | 0 of 18,003 frames dropped |
| 3840x2160 at 30 fps for 2 minutes, moving content | under 1% dropped | **missed**: 3.8%, 5.7% and 13.9% dropped in 3 runs |
| 3840x2160 at 30 fps for 2 minutes, still screen | under 1% dropped | 0.17% dropped (6 of 3,612 frames) |
| App CPU while idle with the bar open | 0% to 1% | 0.05% of all cores over 60 seconds |
| App CPU while recording | none set | 1.5% to 2.7% of all cores |
| Stop, from pressing Stop until the files are done | none set | 0.19 to 0.64 s |

The 4K target is missed on this integrated graphics chip when the screen keeps changing. Its hardware encoder cannot keep up with 3840x2160 frames, so the app drops frames from `final.mp4` and repeats the previous frame in their place. Dropped frames affect `final.mp4` only. In every 4K run `final.mp4` and `screen.mp4` had the same frame count, and the video files and both WAV files were within 0.03 seconds of each other in length. The stop message tells you how many frames were dropped.

A crash keeps the recording playable. When the app was killed 30 seconds into a 4K recording, all 5 files were there, both MP4 files opened with 29.7 seconds of video, both WAV files held 30.0 seconds, and `session.json` parsed.

## Known limits

- WAV files hold about 3.1 hours of sound, so longer recordings lose the end of `mic.wav` and `system.wav`.
- HDR screens are converted to standard colors, which clips the brightest highlights. On the HDR test screen pure colors, black and white came out right, and mid-gray came out about 4% lighter.
- The camera code is untested on real hardware, because the test laptop has no webcam.
- 4K recording drops frames on integrated graphics when the screen changes a lot, as shown above.

These checks still need a person:

- A 10 second desktop recording with camera, logo, microphone and computer sound.
- Overlays sit in the right place in `final.mp4` and are missing from `screen.mp4`.
- Moving the camera during a recording.
- Recording a window while moving and resizing it, then closing it.
- A 5 second pause.
- The record and screenshot hotkeys while another app has focus.
- Clap sync between the files in a video editor.
- Dragging a region, because injected mouse input did not reach the region picker in the automated runs.
