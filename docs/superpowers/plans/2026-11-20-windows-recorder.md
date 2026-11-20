# Windows Screen Recorder v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a light native Windows app that takes screenshots and records the desktop, a window or a region, with a live webcam shape and logo, and saves a finished MP4 plus raw files.

**Architecture:** Three projects. `Core` holds pure logic (frame math, layout, session file, clock, state machine) and has fast unit tests. `Capture` holds everything that touches Windows capture, cameras, sound and encoders, and has integration tests that need a real graphics chip. `App` is a thin WinUI 3 shell with the bar, pickers and overlay windows.

**Tech Stack:** C#, .NET 10, WinUI 3 (Windows App SDK, unpackaged, self-contained), Windows.Graphics.Capture, Win2D for drawing, Media Foundation Sink Writer through Vortice.MediaFoundation, AudioGraph for sound, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-20-windows-recorder-design.md`

## Global Constraints

- Fully native. No web view.
- Target framework `net10.0-windows10.0.19041.0` for `Capture` and `App`, `net10.0` for `Core`.
- Runs on Windows 11 and Windows 10 version 2004 or newer.
- No account, no network use, no telemetry.
- Frames stay on the graphics chip from capture to file. No CPU readback in the recording path.
- H.264 video in MP4, constant frame rate, up to 60 frames per second.
- Sound is 48,000 Hz stereo. Raw sound is 32-bit float WAV. `final.mp4` sound is AAC at 192 kbit/s.
- The bar, the camera overlay and the logo overlay never appear in `screen.mp4`.
- Positions and sizes in the layout are fractions of the captured frame.
- NuGet packages allowed are Microsoft.WindowsAppSDK, Microsoft.Graphics.Win2D, Vortice.MediaFoundation, Vortice.Direct3D11, xunit. Add no others without asking.
- The .NET 10 SDK is not installed on this PC (only the runtime is). Task 1 installs it.

## Review Focus

1. The disk fills up during a recording. The app stops, keeps every file playable, and tells the user. Test in Task 9.
2. The webcam or microphone is unplugged during a recording. That file ends cleanly, the recording continues, and `session.json` gets a note. Test in Task 9.
3. The captured monitor changes resolution or is unplugged during a recording. A resolution change is scaled into the fixed frame, and an unplugged monitor stops and saves. Test in Task 9.
4. The logo file is missing, corrupt or very large. The app records without the logo and shows a message. Test in Task 8.
5. The target window belongs to an app running as administrator, or shows protected video, so capture returns black or fails. The app says so before recording. Test in Task 5.

---

## File Structure

```
ScreenRecorder.sln
src/ScreenRecorder.Core/
  FrameMath.cs          sizes, scaling, bars, bitrate
  Layout.cs             camera, logo, shapes, corners
  SessionFile.cs        session.json model and JSON
  RecordingClock.cs     frame pacing and pause
  RecorderState.cs      idle, countdown, recording, paused
  Hotkey.cs             parse and format hotkeys
  Settings.cs           settings model and JSON
src/ScreenRecorder.Capture/
  CaptureTarget.cs      monitor, window, region
  ScreenSource.cs       Windows.Graphics.Capture
  WebcamSource.cs       camera frames
  AudioSource.cs        mic and computer sound
  WavWriter.cs          float WAV file
  EncoderProbe.cs       hardware encoder check
  VideoFileWriter.cs    H.264 MP4 writer
  Compositor.cs         draws screen, camera, logo
  RecordingSession.cs   runs one recording
  Screenshot.cs         PNG and clipboard
src/ScreenRecorder.App/
  App.xaml(.cs)
  BarWindow.xaml(.cs)           floating bar
  TargetPicker.cs               monitor and window lists
  RegionSelectWindow.xaml(.cs)  drag a region
  CameraOverlayWindow.cs        live camera shape
  LogoOverlayWindow.cs          logo in a corner
  SettingsWindow.xaml(.cs)
  GlobalHotkeys.cs              RegisterHotKey
  NativeMethods.cs              P/Invoke
tests/ScreenRecorder.Core.Tests/
tests/ScreenRecorder.Capture.Tests/
```

---

### Task 1: Solution and frame math

**Files:**
- Create: `ScreenRecorder.sln`, `.gitignore`, `src/ScreenRecorder.Core/ScreenRecorder.Core.csproj`, `src/ScreenRecorder.Core/FrameMath.cs`
- Test: `tests/ScreenRecorder.Core.Tests/FrameMathTests.cs`

**Interfaces:**
- Produces:
  - `record struct PixelSize(int Width, int Height)`
  - `record struct RectF(float X, float Y, float Width, float Height)`
  - `static PixelSize FrameMath.ToEven(PixelSize s)` rounds each side down to an even number.
  - `static PixelSize FrameMath.FitWithin(PixelSize source, PixelSize max)` keeps proportions, never scales up, returns even sizes.
  - `static RectF FrameMath.Letterbox(PixelSize source, PixelSize frame)` returns where `source` is drawn inside `frame`, centered, proportions kept.
  - `static int FrameMath.Bitrate(PixelSize size, int frameRate)` returns `Width * Height * frameRate / 10`.

- [ ] **Step 1: Install the SDK and set up the repo.** Ask the user before running the install.

```bash
winget install Microsoft.DotNet.SDK.10
git init
dotnet new sln -n ScreenRecorder
dotnet new classlib -o src/ScreenRecorder.Core -f net10.0
dotnet new xunit -o tests/ScreenRecorder.Core.Tests -f net10.0
dotnet sln add src/ScreenRecorder.Core tests/ScreenRecorder.Core.Tests
dotnet add tests/ScreenRecorder.Core.Tests reference src/ScreenRecorder.Core
dotnet new gitignore
```

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public void ToEven_rounds_down() =>
    Assert.Equal(new PixelSize(1920, 1080), FrameMath.ToEven(new PixelSize(1921, 1081)));
[Fact] public void FitWithin_scales_ultrawide_down() =>
    Assert.Equal(new PixelSize(4096, 1728), FrameMath.FitWithin(new PixelSize(5120, 2160), new PixelSize(4096, 2304)));
[Fact] public void FitWithin_never_scales_up() =>
    Assert.Equal(new PixelSize(1280, 720), FrameMath.FitWithin(new PixelSize(1280, 720), new PixelSize(4096, 2304)));
[Fact] public void Letterbox_adds_side_bars() =>
    Assert.Equal(new RectF(420, 0, 1080, 1080), FrameMath.Letterbox(new PixelSize(500, 500), new PixelSize(1920, 1080)));
[Fact] public void Bitrate_1080p60() =>
    Assert.Equal(12_441_600, FrameMath.Bitrate(new PixelSize(1920, 1080), 60));
```

- [ ] **Step 3: Run `dotnet test tests/ScreenRecorder.Core.Tests`.** Expected: build fails, `FrameMath` not defined.
- [ ] **Step 4: Implement `FrameMath.cs` with the signatures above.**
- [ ] **Step 5: Run `dotnet test tests/ScreenRecorder.Core.Tests`.** Expected: 5 passed.
- [ ] **Step 6: Commit** with message `feat: solution and frame math`.

---

### Task 2: Layout and session file

**Files:**
- Create: `src/ScreenRecorder.Core/Layout.cs`, `src/ScreenRecorder.Core/SessionFile.cs`
- Test: `tests/ScreenRecorder.Core.Tests/SessionFileTests.cs`, `tests/ScreenRecorder.Core.Tests/LayoutTests.cs`

**Interfaces:**
- Consumes: `PixelSize`, `RectF` from Task 1.
- Produces:
  - `enum CameraShape { Circle, RoundedSquare, Square }`
  - `enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }`
  - `record CameraItem(bool Visible, float X, float Y, float Size, CameraShape Shape)`. `X` and `Y` are the top-left corner as fractions of frame width and height. `Size` is the side length as a fraction of frame height. The camera picture is always a centered square crop.
  - `record LogoItem(bool Visible, Corner Corner, float Size, string? Path)`. `Size` is the logo height as a fraction of frame height. The margin from the edges is 2% of frame height.
  - `record Layout(CameraItem Camera, LogoItem Logo)` with `static Layout Default` (camera visible, circle, size 0.25, bottom-left with 2% margin, logo hidden, bottom-right, size 0.08).
  - `static RectF Layout.CameraRect(CameraItem c, PixelSize frame)` and `static RectF Layout.LogoRect(LogoItem l, PixelSize frame, PixelSize logoPixels)` return pixel rectangles, clamped to stay inside the frame.
  - `record SourceEntry(string File, string Kind, double OffsetSeconds)`. `Kind` is one of `final`, `screen`, `webcam`, `mic`, `system`.
  - `record LayoutChange(double AtSeconds, Layout Layout)`
  - `record PauseEntry(double AtSeconds, double DurationSeconds)`
  - `record SessionFile(int Version, DateTimeOffset StartedAt, PixelSize FrameSize, int FrameRate, string TargetKind, IReadOnlyList<SourceEntry> Sources, IReadOnlyList<LayoutChange> LayoutChanges, IReadOnlyList<PauseEntry> Pauses, long DroppedFrames, IReadOnlyList<string> Notes)`. `TargetKind` is `monitor`, `window` or `region`.
  - `static string SessionJson.Serialize(SessionFile f)` and `static SessionFile SessionJson.Deserialize(string json)`. Use System.Text.Json, camelCase names, enums as strings, indented. `Version` is 1. `Deserialize` throws `NotSupportedException` when the version is higher than 1.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Session_round_trips() {
    var f = new SessionFile(1, DateTimeOffset.Parse("2026-11-20T10:00:00+07:00"), new PixelSize(1920, 1080), 60, "monitor",
        [new("screen.mp4", "screen", 0)], [new(0, Layout.Default)], [new(5.0, 2.5)], 0, []);
    Assert.Equivalent(f, SessionJson.Deserialize(SessionJson.Serialize(f)));
}
[Fact] public void Session_uses_plain_names() {
    var json = SessionJson.Serialize(/* f from above */);
    Assert.Contains("\"shape\": \"Circle\"", json);
    Assert.Contains("\"version\": 1", json);
}
[Fact] public void Newer_version_is_refused() =>
    Assert.Throws<NotSupportedException>(() => SessionJson.Deserialize("{\"version\": 2}"));
[Fact] public void CameraRect_uses_fractions() =>
    Assert.Equal(new RectF(192, 108, 270, 270),
        Layout.CameraRect(new CameraItem(true, 0.1f, 0.1f, 0.25f, CameraShape.Circle), new PixelSize(1920, 1080)));
[Fact] public void CameraRect_is_clamped_inside_frame() {
    var r = Layout.CameraRect(new CameraItem(true, 0.95f, 0.95f, 0.25f, CameraShape.Square), new PixelSize(1920, 1080));
    Assert.Equal(1920, r.X + r.Width); Assert.Equal(1080, r.Y + r.Height);
}
[Fact] public void LogoRect_bottom_right_keeps_proportions() =>
    Assert.Equal(new RectF(1920 - 21.6f - 172.8f, 1080 - 21.6f - 86.4f, 172.8f, 86.4f),
        Layout.LogoRect(new LogoItem(true, Corner.BottomRight, 0.08f, "x.png"), new PixelSize(1920, 1080), new PixelSize(400, 200)));
```

- [ ] **Step 2: Run the tests.** Expected: build fails, types not defined.
- [ ] **Step 3: Implement `Layout.cs` and `SessionFile.cs`.**
- [ ] **Step 4: Run the tests.** Expected: all pass.
- [ ] **Step 5: Commit** `feat: layout and session file format`.

---

### Task 3: Recording clock

**Files:**
- Create: `src/ScreenRecorder.Core/RecordingClock.cs`
- Test: `tests/ScreenRecorder.Core.Tests/RecordingClockTests.cs`

**Interfaces:**
- Consumes: `PauseEntry` from Task 2.
- Produces: `sealed class RecordingClock(int frameRate, Func<TimeSpan> now)` with
  - `void Start()`, `void Pause()`, `void Resume()`
  - `bool IsPaused`
  - `TimeSpan MediaTime`, the time since start with paused time removed.
  - `long FramesDue`, equal to `floor(MediaTime * frameRate)`. The recorder writes frames until its count equals this number, which gives a constant frame rate.
  - `long SamplesDue(int sampleRate)`, equal to `floor(MediaTime * sampleRate)`.
  - `IReadOnlyList<PauseEntry> Pauses`, with `AtSeconds` in media time.

- [ ] **Step 1: Write the failing tests.** Use a fake `now` that the test advances.

```csharp
[Fact] public void Frames_follow_time() { /* start, advance 1.5 s at 60 */ Assert.Equal(90, clock.FramesDue); }
[Fact] public void Pause_cuts_time() {
    /* start, +2 s, Pause, +3 s, Resume, +1 s */
    Assert.Equal(TimeSpan.FromSeconds(3), clock.MediaTime);
    Assert.Equal([new PauseEntry(2.0, 3.0)], clock.Pauses);
}
[Fact] public void Frames_do_not_grow_while_paused() { /* start, +1 s, Pause, +5 s */ Assert.Equal(60, clock.FramesDue); }
[Fact] public void Samples_follow_time() { /* start, +0.5 s */ Assert.Equal(24_000, clock.SamplesDue(48_000)); }
[Fact] public void Pause_twice_is_ignored() { /* Pause, Pause, Resume gives one entry */ }
```

- [ ] **Step 2: Run the tests.** Expected: fail, `RecordingClock` not defined.
- [ ] **Step 3: Implement `RecordingClock.cs`.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat: recording clock with pause`.

---

### Task 4: Recorder state, hotkeys and settings

**Files:**
- Create: `src/ScreenRecorder.Core/RecorderState.cs`, `src/ScreenRecorder.Core/Hotkey.cs`, `src/ScreenRecorder.Core/Settings.cs`
- Test: `tests/ScreenRecorder.Core.Tests/RecorderStateTests.cs`, `HotkeyTests.cs`, `SettingsTests.cs`

**Interfaces:**
- Consumes: `Layout` from Task 2.
- Produces:
  - `enum RecorderState { Idle, Countdown, Recording, Paused }`
  - `enum RecorderCommand { ToggleRecord, CountdownDone, TogglePause, Failed }`
  - `static RecorderState RecorderStateMachine.Next(RecorderState s, RecorderCommand c, bool countdownEnabled)`. `ToggleRecord` from `Idle` goes to `Countdown` or straight to `Recording`. `ToggleRecord` from `Countdown` goes back to `Idle`. `ToggleRecord` from `Recording` or `Paused` goes to `Idle`. `TogglePause` switches `Recording` and `Paused`. `Failed` always goes to `Idle`. Any other pair returns `s` unchanged.
  - `record Hotkey(bool Ctrl, bool Shift, bool Alt, bool Win, uint VirtualKey)` with `static bool TryParse(string text, out Hotkey h)` and `override string ToString()`. Text form is like `Ctrl+Shift+R`. A hotkey with no modifier is refused.
  - `record Settings(string OutputFolder, Hotkey RecordHotkey, Hotkey ScreenshotHotkey, bool Countdown, int FrameRate, string? WebcamId, string? MicId, bool Mic, bool SystemAudio, Layout Layout)` with `static Settings Default` and `static Settings Load(string path)`, `void Save(string path)`.
  - Defaults are `%USERPROFILE%\Videos\ScreenRecorder`, `Ctrl+Shift+R`, `Ctrl+Shift+S`, countdown on (3 seconds), 60 frames per second, mic on, system sound on, `Layout.Default`.
  - `Load` returns `Default` when the file is missing or cannot be parsed.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(RecorderState.Idle, RecorderCommand.ToggleRecord, true, RecorderState.Countdown)]
[InlineData(RecorderState.Idle, RecorderCommand.ToggleRecord, false, RecorderState.Recording)]
[InlineData(RecorderState.Countdown, RecorderCommand.CountdownDone, true, RecorderState.Recording)]
[InlineData(RecorderState.Countdown, RecorderCommand.ToggleRecord, true, RecorderState.Idle)]
[InlineData(RecorderState.Recording, RecorderCommand.TogglePause, true, RecorderState.Paused)]
[InlineData(RecorderState.Paused, RecorderCommand.ToggleRecord, true, RecorderState.Idle)]
[InlineData(RecorderState.Idle, RecorderCommand.TogglePause, true, RecorderState.Idle)]
[InlineData(RecorderState.Recording, RecorderCommand.Failed, true, RecorderState.Idle)]
public void Transitions(RecorderState s, RecorderCommand c, bool cd, RecorderState expected) =>
    Assert.Equal(expected, RecorderStateMachine.Next(s, c, cd));

[Fact] public void Hotkey_round_trips() { Assert.True(Hotkey.TryParse("Ctrl+Shift+R", out var h)); Assert.Equal("Ctrl+Shift+R", h.ToString()); Assert.Equal(0x52u, h.VirtualKey); }
[Fact] public void Hotkey_without_modifier_is_refused() => Assert.False(Hotkey.TryParse("R", out _));
[Fact] public void Settings_round_trip() { /* Save to temp path, Load, Assert.Equivalent */ }
[Fact] public void Corrupt_settings_give_default() { /* write "{{{" to temp path */ Assert.Equivalent(Settings.Default, Settings.Load(path)); }
```

- [ ] **Step 2: Run the tests.** Expected: fail, types not defined.
- [ ] **Step 3: Implement the 3 files.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat: recorder state, hotkeys, settings`.

---

### Task 5: Screen source

**Files:**
- Create: `src/ScreenRecorder.Capture/ScreenRecorder.Capture.csproj`, `CaptureTarget.cs`, `ScreenSource.cs`, `NativeMethods.cs`
- Test: `tests/ScreenRecorder.Capture.Tests/ScreenSourceTests.cs`

**Interfaces:**
- Consumes: `PixelSize` from Task 1.
- Produces:
  - `abstract record CaptureTarget`, `record MonitorTarget(nint Hmonitor)`, `record WindowTarget(nint Hwnd)`, `record RegionTarget(nint Hmonitor, int X, int Y, int Width, int Height)` with the rectangle in that monitor's pixels.
  - `static nint CaptureTarget.PrimaryMonitor()`
  - `sealed class ScreenSource : IDisposable`
    - `static ScreenSource Create(CaptureTarget target, CanvasDevice device)`. Throws `CaptureUnavailableException` with a plain message when the target cannot be captured.
    - `PixelSize SourceSize`, the current size of the captured area after any region crop.
    - `bool TryGetLatestFrame(out CanvasBitmap frame)`. Returns the newest frame, already cropped for regions. Returns the previous frame again when the screen did not change. Returns false only before the first frame.
    - `Windows.Foundation.Rect? WindowBounds`, the screen position of a window target, or null.
    - `bool IsMinimized`
    - `event Action? Closed`, raised when the window closes or the monitor goes away.
  - `static bool ScreenSource.LooksBlack(CanvasBitmap frame)` samples a 16 by 16 grid and returns true when every sample is black. Used only before recording starts, never in the recording loop.

Notes for the implementer:
- Use `GraphicsCaptureItem` interop (`CreateForMonitor`, `CreateForWindow`) and `Direct3D11CaptureFramePool.CreateFreeThreaded`.
- Set `IsBorderRequired = false` and `IsCursorCaptureEnabled = true`. Guard `IsBorderRequired` with `ApiInformation.IsPropertyPresent`, because Windows 10 lacks it.
- Recreate the frame pool when the content size changes, and update `SourceSize`.
- On an HDR monitor, request `R16G16B16A16Float` frames and convert to standard colors with a Direct2D HDR tone map effect before handing out the frame. Check the result by eye on an HDR monitor, because no automated test covers it.

- [ ] **Step 1: Create the project.** Class library, target `net10.0-windows10.0.19041.0`, packages Microsoft.Graphics.Win2D and Vortice.Direct3D11. Create `tests/ScreenRecorder.Capture.Tests` (xunit, same target) and add both to the solution.
- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public async Task Primary_monitor_gives_a_frame() {
    using var src = ScreenSource.Create(new MonitorTarget(CaptureTarget.PrimaryMonitor()), CanvasDevice.GetSharedDevice());
    await WaitUntil(() => src.TryGetLatestFrame(out _), TimeSpan.FromSeconds(2));
    Assert.True(src.SourceSize.Width > 0);
}
[Fact] public async Task Region_is_cropped() {
    using var src = ScreenSource.Create(new RegionTarget(CaptureTarget.PrimaryMonitor(), 100, 100, 640, 360), CanvasDevice.GetSharedDevice());
    await WaitUntil(() => src.TryGetLatestFrame(out _), TimeSpan.FromSeconds(2));
    src.TryGetLatestFrame(out var f);
    Assert.Equal(new PixelSize(640, 360), src.SourceSize);
    Assert.Equal(640u, f.SizeInPixels.Width);
}
[Fact] public async Task Closing_the_window_raises_Closed() { /* start notepad.exe, capture its window, kill it, expect Closed within 2 s */ }
[Fact] public void Invalid_window_throws_plain_error() =>
    Assert.Throws<CaptureUnavailableException>(() => ScreenSource.Create(new WindowTarget(0x1), CanvasDevice.GetSharedDevice()));
[Fact] public void LooksBlack_detects_black_frame() { /* CanvasRenderTarget cleared to black gives true, cleared to red gives false */ }
```

- [ ] **Step 3: Run `dotnet test tests/ScreenRecorder.Capture.Tests`.** Expected: fail, types not defined.
- [ ] **Step 4: Implement the files.**
- [ ] **Step 5: Run the tests.** Expected: pass.
- [ ] **Step 6: Commit** `feat: screen source for monitor, window, region`.

---

### Task 6: Video file writer and encoder probe

**Files:**
- Create: `src/ScreenRecorder.Capture/VideoFileWriter.cs`, `EncoderProbe.cs`
- Test: `tests/ScreenRecorder.Capture.Tests/VideoFileWriterTests.cs`

**Interfaces:**
- Consumes: `PixelSize`, `FrameMath.Bitrate`.
- Produces:
  - `enum EncoderSupport { Hardware, TooFewHardwareSessions, SoftwareOnly }`
  - `static EncoderSupport EncoderProbe.Check(int sessionsNeeded)`. Opens that many hardware H.264 encoders at 1920x1080 and closes them. Runs no speed test.
  - `static PixelSize EncoderProbe.MaxSize()`. Returns the hardware encoder's largest frame size, or 4096x2304 when the encoder does not report one.
  - `sealed class VideoFileWriter : IDisposable`
    - `VideoFileWriter(string path, PixelSize size, int frameRate, CanvasDevice device, bool hardware, bool withAudio)`
    - `void WriteFrame(IDirect3DSurface surface, long frameIndex)`. The timestamp is `frameIndex / frameRate`. Every frame has the same duration.
    - `void WriteAudio(ReadOnlySpan<float> interleavedStereo48k, long firstSampleIndex)`
    - `long FramesBehind`, the count of frames queued but not yet encoded.
    - `void Dispose()` finishes the file.
    - `IOException` from the disk is passed to the caller unchanged.

Notes for the implementer:
- Use the Media Foundation Sink Writer with a DXGI device manager, so textures go to the encoder without a copy.
- Set the container to fragmented MP4, so a file cut off by a crash still plays up to its last fragment.
- Bitrate is `FrameMath.Bitrate(size, frameRate)`. Audio is AAC, 192 kbit/s.

- [ ] **Step 1: Write the failing tests.** Add a test helper `MediaInfo.Read(path)` that returns duration, frame count, width, height and whether the frame rate is constant, using the Media Foundation Source Reader.

```csharp
[Fact] public void Writes_constant_60fps() {
    /* write 120 solid-color 1280x720 frames with indexes 0..119 */
    var info = MediaInfo.Read(path);
    Assert.Equal(120, info.FrameCount);
    Assert.InRange(info.Duration.TotalSeconds, 1.98, 2.02);
    Assert.True(info.ConstantFrameRate);
}
[Fact] public void Cut_off_file_still_plays() {
    /* run a child process that writes 300 frames and is killed after frame 200, without Dispose */
    Assert.True(MediaInfo.Read(path).FrameCount >= 60);
}
[Fact] public void Audio_and_video_have_equal_length() { /* 2 s of frames and 96,000 samples, both durations within 0.05 s */ }
[Fact] public void Probe_reports_a_value() => Assert.True(Enum.IsDefined(EncoderProbe.Check(3)));
[Fact] public void Software_encoder_works() { /* hardware: false, 30 frames, FrameCount == 30 */ }
```

- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement the 2 files.**
- [ ] **Step 4: Run the tests.** Expected: pass. If `Cut_off_file_still_plays` fails with fragmented MP4, stop and report, because the crash anti-goal depends on it.
- [ ] **Step 5: Commit** `feat: H.264 file writer and encoder probe`.

---

### Task 7: Sound sources and WAV writer

**Files:**
- Create: `src/ScreenRecorder.Capture/AudioSource.cs`, `WavWriter.cs`
- Test: `tests/ScreenRecorder.Capture.Tests/WavWriterTests.cs`, `AudioSourceTests.cs`

**Interfaces:**
- Produces:
  - `sealed class AudioSource : IAsyncDisposable`
    - `static Task<IReadOnlyList<(string Id, string Name)>> ListMicsAsync()`
    - `static Task<AudioSource> OpenMicAsync(string? deviceId)`, where null means the default microphone.
    - `static Task<AudioSource> OpenLoopbackAsync()`, the default output device's sound.
    - `int Read(Span<float> interleavedStereo48k)`. Returns the samples copied from an internal ring buffer of 2 seconds. The caller pads with silence when fewer arrive than the clock needs.
    - `event Action? Lost`
  - `sealed class WavWriter : IDisposable`
    - `WavWriter(string path)`, fixed at 48,000 Hz, stereo, 32-bit float.
    - `void Write(ReadOnlySpan<float> interleaved)`
    - Rewrites the header sizes once per second of sound, so a cut-off file still opens.

Notes for the implementer:
- Use `AudioGraph` with a frame output node, and set the graph to 48,000 Hz stereo so Windows resamples.
- Loopback delivers nothing while the computer is silent. That is the reason the caller pads from the clock.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Wav_header_is_valid() {
    /* write 48,000 stereo frames (96,000 floats), dispose */
    Assert.Equal(44 + 96_000 * 4, new FileInfo(path).Length);
    /* bytes 0..3 "RIFF", format tag 3 (float), channels 2, rate 48000 */
}
[Fact] public void Cut_off_wav_has_usable_header() { /* write 3 s, copy the file without Dispose, data size in header covers at least 2 s */ }
[Fact] public async Task Loopback_opens_and_reads_without_error() { /* open, wait 200 ms, Read returns 0 or more */ }
```

- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement the 2 files.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat: mic and computer sound capture, WAV writer`.

---

### Task 8: Webcam source and compositor

**Files:**
- Create: `src/ScreenRecorder.Capture/WebcamSource.cs`, `Compositor.cs`
- Test: `tests/ScreenRecorder.Capture.Tests/CompositorTests.cs`, `WebcamSourceTests.cs`

**Interfaces:**
- Consumes: `Layout`, `Layout.CameraRect`, `Layout.LogoRect`, `FrameMath.Letterbox`.
- Produces:
  - `sealed class WebcamSource : IAsyncDisposable`
    - `static Task<IReadOnlyList<(string Id, string Name)>> ListAsync()`
    - `static Task<WebcamSource> OpenAsync(string deviceId, CanvasDevice device)`. Picks the mode closest to 1280x720 at 30 frames per second.
    - `PixelSize Size`
    - `bool TryGetLatestFrame(out CanvasBitmap frame)`
    - `event Action? Lost`
  - `sealed class Compositor : IDisposable`
    - `Compositor(CanvasDevice device, PixelSize frameSize)`
    - `IDirect3DSurface DrawScreen(CanvasBitmap screen)`. Draws the screen into the fixed frame with `FrameMath.Letterbox`, black bars around it. Used for `screen.mp4`.
    - `IDirect3DSurface DrawFinal(CanvasBitmap screen, CanvasBitmap? camera, Layout layout)`. Draws the screen with the same letterbox as `DrawScreen`, then the logo, then the camera on top. The camera is a centered square crop, mirrored left to right, masked by the shape. `RoundedSquare` has a corner radius of 18% of the side.
    - `string? SetLogo(string? path)`. Loads the logo and returns null, or returns a plain error message and clears the logo. Files over 20 MB or 8192 pixels on a side are refused.
    - Each call reuses one render target per output. The returned surface is valid until the next call of the same method.

- [ ] **Step 1: Write the failing tests.** Add a test helper `Pixel(surface, x, y)` that reads one pixel back. Readback is allowed in tests only.

```csharp
// frame 1920x1080, screen solid blue 1920x1080, camera solid red 1280x720
[Fact] public void Circle_camera_covers_center_not_corner() {
    var layout = Layout.Default with { Camera = new(true, 0.1f, 0.1f, 0.25f, CameraShape.Circle) }; // rect 192,108,270,270
    var s = c.DrawFinal(blue, red, layout);
    Assert.Equal(Red, Pixel(s, 327, 243));   // center of the circle
    Assert.Equal(Blue, Pixel(s, 194, 110));  // corner of the rect, outside the circle
}
[Fact] public void Square_camera_covers_corner() { /* same rect, Square, Pixel(s, 194, 110) is Red */ }
[Fact] public void Hidden_camera_is_not_drawn() { /* Visible false, Pixel(s, 327, 243) is Blue */ }
[Fact] public void Null_camera_frame_is_not_drawn() { /* camera null, Visible true, still Blue, no exception */ }
[Fact] public void DrawScreen_letterboxes_small_source() {
    /* screen 500x500 green */ var s = c.DrawScreen(green);
    Assert.Equal(Black, Pixel(s, 10, 540)); Assert.Equal(Green, Pixel(s, 960, 540));
}
[Fact] public void DrawScreen_has_no_overlays() { /* DrawScreen after SetLogo, logo corner pixel is the screen color */ }
[Fact] public void Logo_is_drawn_in_its_corner() { /* white 400x200 PNG, BottomRight, pixel inside LogoRect is White */ }
[Fact] public void Missing_logo_returns_message() { Assert.NotNull(c.SetLogo(@"C:\nope.png")); /* DrawFinal still works */ }
[Fact] public void Corrupt_logo_returns_message() { /* a .png file holding the text "hello" */ }
[Fact] public void Oversized_logo_returns_message() { /* 9000x100 PNG */ }
[Fact] public async Task Webcam_list_does_not_throw() => Assert.NotNull(await WebcamSource.ListAsync());
```

- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement the 2 files.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat: webcam source and compositor`.

---

### Task 9: Recording session

**Files:**
- Create: `src/ScreenRecorder.Capture/RecordingSession.cs`
- Test: `tests/ScreenRecorder.Capture.Tests/RecordingSessionTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 to 8.
- Produces:
  - `record RecordingOptions(CaptureTarget Target, string OutputRoot, int FrameRate, string? WebcamId, bool Mic, string? MicId, bool SystemAudio, Layout Layout, bool WriteFinal)`
  - `record RecordingResult(string Folder, long DroppedFrames, IReadOnlyList<string> Notes)`
  - `record PreflightResult(EncoderSupport Encoders, PixelSize FrameSize, bool ScaledDown, bool LooksBlack)`
  - `sealed class RecordingSession : IAsyncDisposable`
    - `static Task<PreflightResult> PreflightAsync(RecordingOptions o)`. Checks encoders for the number of video files needed, works out the frame size with `FrameMath.FitWithin(source, EncoderProbe.MaxSize())`, and grabs one frame for `LooksBlack`.
    - `static Task<RecordingSession> StartAsync(RecordingOptions o, PreflightResult p)`
    - `void Pause()`, `void Resume()`, `void SetLayout(Layout l)`
    - `Task<RecordingResult> StopAsync()`
    - `event Action<RecordingResult>? StoppedByItself`, raised when the window closes, the monitor goes away, or the disk fills up.

Behavior the implementer must build:
- The folder is `OutputRoot\yyyy-MM-dd HH-mm-ss`.
- The frame size is fixed at start. One thread loops on `RecordingClock.FramesDue`. For each due frame it takes the latest screen frame and writes `DrawScreen` to `screen.mp4` and `DrawFinal` to `final.mp4`.
- `webcam.mp4` is written at 30 frames per second from the same clock, full camera frame, no mask.
- Sound is pulled from each `AudioSource` up to `RecordingClock.SamplesDue(48000)`, padded with silence, written to `mic.wav` and `system.wav`, and mixed (sum, then clamp to -1..1) into `final.mp4`.
- Files for sources that are off are not created. `final.mp4` is skipped when `WriteFinal` is false.
- When `EncoderSupport` is `SoftwareOnly`, writers use the software encoder and the frame rate is 30.
- When `final.mp4`'s writer reports `FramesBehind` above 30, the session skips drawing `final.mp4` for that frame, repeats the previous final frame index later so the file stays constant frame rate, and counts 1 dropped frame. The capture loop is never blocked by `final.mp4`.
- Pause calls `RecordingClock.Pause`, so no file grows while paused.
- For a window target, a minimized window repeats its last frame. `Closed` stops and saves.
- `session.json` is written at start and rewritten at every layout change, pause, note and stop, so a crash leaves a usable one.
- A lost webcam or sound source ends that file cleanly, adds a note such as `webcam lost at 12.4 s`, and the recording goes on.
- An `IOException` from any writer stops the session, finishes every file that can be finished, adds a note, and raises `StoppedByItself`.

- [ ] **Step 1: Write the failing tests.** Each test records the primary monitor into a temp folder, with `WriteFinal` true and no webcam unless stated. Constructor-inject the sources through an internal `ISourceFactory`, so tests can pass fakes that fail on command.

```csharp
[Fact] public async Task Two_second_recording_makes_aligned_files() {
    /* start, wait 2 s, stop */
    Assert.True(File.Exists(Path.Combine(folder, "final.mp4")));
    Assert.True(File.Exists(Path.Combine(folder, "screen.mp4")));
    Assert.False(File.Exists(Path.Combine(folder, "webcam.mp4")));
    var a = MediaInfo.Read(final).Duration; var b = MediaInfo.Read(screen).Duration;
    Assert.InRange(Math.Abs((a - b).TotalSeconds), 0, 0.05);
    Assert.True(MediaInfo.Read(screen).ConstantFrameRate);
}
[Fact] public async Task Pause_is_cut_from_every_file() {
    /* record 1 s, pause 2 s, resume, record 1 s, stop */
    Assert.InRange(MediaInfo.Read(screen).Duration.TotalSeconds, 1.9, 2.2);
    Assert.InRange(WavSeconds(system), 1.9, 2.2);
    Assert.Single(session.Pauses);
}
[Fact] public async Task Layout_changes_are_logged() { /* SetLayout once, session.json has 2 layoutChanges */ }
[Fact] public async Task Sources_that_are_off_write_no_file() { /* Mic false, SystemAudio false, no .wav files */ }
[Fact] public async Task WriteFinal_false_skips_final() { /* no final.mp4, screen.mp4 exists */ }
[Fact] public async Task Lost_webcam_keeps_recording() {
    /* fake webcam raises Lost after 1 s, stop at 2 s */
    Assert.InRange(MediaInfo.Read(screen).Duration.TotalSeconds, 1.9, 2.2);
    Assert.Contains(result.Notes, n => n.StartsWith("webcam lost"));
    Assert.True(MediaInfo.Read(webcam).FrameCount > 0);
}
[Fact] public async Task Lost_mic_keeps_recording() { /* same with fake mic, note starts with "mic lost" */ }
[Fact] public async Task Disk_full_stops_and_keeps_files_playable() {
    /* fake writer factory throws IOException at frame 60 */
    var result = await stoppedByItself.Task;
    Assert.Contains(result.Notes, n => n.Contains("disk"));
    Assert.True(MediaInfo.Read(screen).FrameCount > 0);
}
[Fact] public async Task Source_resize_keeps_frame_size() {
    /* fake screen source changes SourceSize from 1920x1080 to 1280x1024 after 1 s */
    Assert.Equal(1920, MediaInfo.Read(screen).Width);
}
[Fact] public async Task Source_closed_stops_and_saves() { /* fake screen source raises Closed, StoppedByItself fires, files play */ }
[Fact] public async Task Slow_final_encoder_drops_final_frames_only() {
    /* fake final writer reports FramesBehind = 100 */
    Assert.True(result.DroppedFrames > 0);
    Assert.InRange(MediaInfo.Read(screen).FrameCount, 110, 125);
}
```

- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement `RecordingSession.cs`.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat: recording session`.

---

### Task 10: Screenshot

**Files:**
- Create: `src/ScreenRecorder.Capture/Screenshot.cs`
- Test: `tests/ScreenRecorder.Capture.Tests/ScreenshotTests.cs`

**Interfaces:**
- Consumes: `ScreenSource`, `CaptureTarget`.
- Produces: `static Task<string> Screenshot.CaptureAsync(CaptureTarget target, string outputRoot, bool copyToClipboard = true)`. Saves `Screenshot yyyy-MM-dd HH-mm-ss.png` at the source's own size, with no camera and no logo, and returns the path. Creates the folder when it is missing.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Region_screenshot_has_region_size() {
    var path = await Screenshot.CaptureAsync(new RegionTarget(CaptureTarget.PrimaryMonitor(), 0, 0, 320, 200), temp, copyToClipboard: false);
    /* decode the PNG */ Assert.Equal((320, 200), (width, height));
}
[Fact] public async Task Missing_folder_is_created() { /* temp\new\sub, file exists */ }
```

- [ ] **Step 2: Run the tests.** Expected: fail.
- [ ] **Step 3: Implement `Screenshot.cs`.**
- [ ] **Step 4: Run the tests.** Expected: pass.
- [ ] **Step 5: Commit** `feat: screenshot to PNG and clipboard`.

---

### Task 11: App shell, bar and target picking

**Files:**
- Create: `src/ScreenRecorder.App/ScreenRecorder.App.csproj`, `App.xaml(.cs)`, `BarWindow.xaml(.cs)`, `TargetPicker.cs`, `RegionSelectWindow.xaml(.cs)`, `NativeMethods.cs`

**Interfaces:**
- Consumes: `CaptureTarget` records, `Settings`, `RecorderStateMachine`, `Screenshot.CaptureAsync`.
- Produces:
  - `static IReadOnlyList<(nint Hmonitor, string Name)> TargetPicker.Monitors()`
  - `static IReadOnlyList<(nint Hwnd, string Title)> TargetPicker.Windows()`. Lists visible top-level windows with a title, without this app's own windows.
  - `Task<RegionTarget?> RegionSelectWindow.PickAsync()`. Dims every monitor, lets the user drag a rectangle, returns null on Escape. The result is in the pixels of the monitor that holds the rectangle. A rectangle under 16 by 16 pixels returns null.
  - `static void NativeMethods.ExcludeFromCapture(nint hwnd)`, which calls `SetWindowDisplayAffinity` with `WDA_EXCLUDEFROMCAPTURE` (0x11).
  - `BarWindow.CurrentTarget` of type `CaptureTarget`, default the primary monitor.

Requirements:
- WinUI 3, unpackaged, self-contained, single instance. No splash screen.
- The bar is a small borderless always-on-top window that the user can drag. It is excluded from capture.
- The bar has a target menu (Desktop, Window, Region), toggles for camera, mic and computer sound, a shape menu, a logo button (file picker for PNG and JPG, plus Remove), Record, Screenshot and Settings. In this task the toggles only save to `Settings`. Record is wired in Task 13.
- Screenshot works end to end in this task.
- Settings are stored at `%LOCALAPPDATA%\ScreenRecorder\settings.json`.

- [ ] **Step 1: Create the project** and add it to the solution with references to `Core` and `Capture`.
- [ ] **Step 2: Implement the files.**
- [ ] **Step 3: Verify by hand.** Run `dotnet run --project src/ScreenRecorder.App`. Check each line.
  - The bar appears in under 1 second with no splash.
  - A Desktop screenshot saves a PNG and pastes into Paint, and the bar is not in the picture.
  - A Window screenshot of Notepad has Notepad's size.
  - A Region screenshot matches the dragged rectangle on a second monitor with different scaling, if one is connected.
  - Escape in the region picker cancels.
- [ ] **Step 4: Commit** `feat: app shell, bar, target picking, screenshots`.

---

### Task 12: Camera and logo overlay windows

**Files:**
- Create: `src/ScreenRecorder.App/CameraOverlayWindow.cs`, `LogoOverlayWindow.cs`
- Create: `src/ScreenRecorder.Core/OverlayMath.cs`
- Test: `tests/ScreenRecorder.Core.Tests/OverlayMathTests.cs`

**Interfaces:**
- Consumes: `Layout`, `Layout.CameraRect`, `Layout.LogoRect`, `WebcamSource`, `NativeMethods.ExcludeFromCapture`.
- Produces:
  - `static RectF OverlayMath.ToScreen(RectF rectInFrame, RectF captureAreaOnScreen, PixelSize frame)` maps a frame rectangle to screen pixels.
  - `static CameraItem OverlayMath.FromScreen(RectF cameraOnScreen, RectF captureAreaOnScreen, CameraItem current)` maps a dragged or resized overlay back to fractions, clamped inside the capture area, size between 0.1 and 0.6.
  - `CameraOverlayWindow` with `void Show(RectF captureAreaOnScreen, Layout layout)`, `void Hide()`, `void MoveCaptureArea(RectF captureAreaOnScreen)`, `event Action<CameraItem> Changed`.
  - `LogoOverlayWindow` with `void Show(RectF captureAreaOnScreen, Layout layout)`, `void Hide()`, `void MoveCaptureArea(RectF captureAreaOnScreen)`.

Requirements:
- Both windows are borderless, always on top, never take focus, and are excluded from capture.
- The camera overlay shows the live webcam with the same centered square crop, mirror and shape as `Compositor.DrawFinal`.
- Drag moves the camera. Dragging its edge or the mouse wheel resizes it.
- The overlays show whenever the camera or logo is on, before and during recording, so the user sees the layout first.
- For a window target, a 100 ms timer reads the window's position and calls `MoveCaptureArea`, so the overlays follow the window.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void ToScreen_offsets_and_scales() =>
    Assert.Equal(new RectF(100 + 96, 50 + 54, 135, 135),
        OverlayMath.ToScreen(new RectF(192, 108, 270, 270), new RectF(100, 50, 960, 540), new PixelSize(1920, 1080)));
[Fact] public void FromScreen_round_trips() { /* ToScreen then FromScreen gives X 0.1, Y 0.1, Size 0.25 within 0.001 */ }
[Fact] public void FromScreen_clamps_inside_area() { /* overlay dragged past the right edge, X + Size * frameHeight / frameWidth <= 1 */ }
[Fact] public void FromScreen_limits_size() { /* tiny overlay gives Size 0.1, huge gives 0.6 */ }
```

- [ ] **Step 2: Run the Core tests.** Expected: fail.
- [ ] **Step 3: Implement `OverlayMath.cs`, then the 2 windows.**
- [ ] **Step 4: Run the Core tests.** Expected: pass.
- [ ] **Step 5: Verify by hand.** The camera shows in each of the 3 shapes, drags, resizes, and stays inside a selected region. The logo sits in each of the 4 corners. A screenshot shows neither overlay.
- [ ] **Step 6: Commit** `feat: camera and logo overlay windows`.

---

### Task 13: Recording controls, hotkeys and settings window

**Files:**
- Create: `src/ScreenRecorder.App/GlobalHotkeys.cs`, `SettingsWindow.xaml(.cs)`
- Modify: `src/ScreenRecorder.App/BarWindow.xaml(.cs)`

**Interfaces:**
- Consumes: `RecordingSession`, `RecordingOptions`, `PreflightResult`, `RecorderStateMachine`, `Hotkey`, `Settings`, overlay windows.
- Produces: `sealed class GlobalHotkeys : IDisposable` with `bool Register(Hotkey h, Action onPressed)`, which returns false when another app owns the hotkey.

Requirements:
- Record runs `PreflightAsync` and shows one message before recording for each case that applies.
  - `TooFewHardwareSessions` shows "This PC cannot encode 3 videos at once. Save the raw files only, without the finished video?" with Yes and Cancel. Yes records with `WriteFinal` false.
  - `SoftwareOnly` shows "No hardware video encoder was found. Recording will run at 30 frames per second."
  - `ScaledDown` shows "The captured area is larger than the encoder accepts. The video will be scaled down to WIDTH x HEIGHT."
  - `LooksBlack` shows "This window cannot be captured. It may be protected or running as administrator." and does not record.
- A 3 second countdown shows on the bar when the setting is on. Pressing Record again cancels it.
- While recording, the bar shrinks to a timer, Pause and Stop. The timer shows media time.
- Overlay `Changed` events call `RecordingSession.SetLayout` and save to `Settings`. Shape, camera toggle and logo changes during recording do the same.
- Stop shows the dropped frame count when it is above 0, and opens the recording's folder in Explorer.
- `StoppedByItself` returns the bar to idle and shows the last note.
- Hotkeys call the same code as the buttons. If `Register` returns false, the settings window shows "This hotkey is used by another app."
- The settings window edits the output folder, both hotkeys, the countdown, the frame rate (30 or 60), the webcam and the microphone.

- [ ] **Step 1: Implement the files.**
- [ ] **Step 2: Verify by hand.** Check each line.
  - Record the desktop for 10 seconds with camera, logo, mic and computer sound playing music. The folder holds `final.mp4`, `screen.mp4`, `webcam.mp4`, `mic.wav`, `system.wav`, `session.json`.
  - `final.mp4` shows the camera shape and logo where the overlays were. `screen.mp4` shows no bar, camera or logo.
  - Moving the camera during recording moves it in `final.mp4`.
  - Record a Notepad window, move it and resize it. The overlays follow, and the video shows black bars after the resize.
  - Close Notepad during recording. The recording saves and the bar returns to idle.
  - Pause for 5 seconds. Every file is shorter by 5 seconds.
  - `Ctrl+Shift+R` starts and stops, `Ctrl+Shift+S` takes a screenshot, while another app has focus.
  - Load `screen.mp4`, `webcam.mp4` and both WAV files into a video editor at time 0. A hand clap lines up in picture and sound.
- [ ] **Step 3: Commit** `feat: recording controls, hotkeys, settings`.

---

### Task 14: Speed, crash and release checks

**Files:**
- Create: `README.md` with build, run and test commands and the output file table.
- Modify: `src/ScreenRecorder.App/ScreenRecorder.App.csproj` for the release publish settings.

- [ ] **Step 1: Publish.** Run `dotnet publish src/ScreenRecorder.App -c Release -r win-x64 --self-contained`. Expected: one folder that runs on a PC without .NET installed.
- [ ] **Step 2: Measure on the reference machine** (laptop with Intel Iris Xe class graphics). Record 1920x1080 at 60 frames per second for 5 minutes with camera, logo and both sound sources, while scrolling a web page. Expected: dropped frames under 1% (under 180 of 18,000).
- [ ] **Step 3: Measure 4K.** Record 3840x2160 at 30 frames per second for 2 minutes. Expected: dropped frames under 1%.
- [ ] **Step 4: Crash check.** Start a recording, end `ScreenRecorder.App.exe` in Task Manager after 30 seconds. Expected: `screen.mp4`, `final.mp4` and the WAV files open and play at least 25 seconds, and `session.json` parses.
- [ ] **Step 5: Idle check.** With the bar open and not recording, and camera off, CPU use in Task Manager stays at 0% to 1%.
- [ ] **Step 6: HDR check**, if an HDR monitor is available. Colors in `screen.mp4` match the screen and are not washed out.
- [ ] **Step 7: Write `README.md` and commit** `chore: release settings and readme`.

If a target in Step 2 or 3 is missed, stop and report the numbers. Do not tune silently.
