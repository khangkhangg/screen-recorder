using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

// The recording flow of the bar: start, countdown, pause, stop and the messages around them.
public sealed partial class BarWindow
{
    readonly DispatcherQueueTimer clock;
    RecorderState state = RecorderState.Idle;
    RecordingSession? session;
    bool busy, showFps;
    int countdownRun;

    void Record_Click(object sender, RoutedEventArgs e) => ToggleRecord();
    void Stop_Click(object sender, RoutedEventArgs e) => ToggleRecord();
    void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();

    async void ToggleRecord()
    {
        if (busy) return;
        if (state == RecorderState.Idle) await StartRecording();
        else if (state == RecorderState.Countdown) SetState(RecorderStateMachine.Next(state, RecorderCommand.ToggleRecord, settings.Countdown));
        else await StopRecording();
    }

    void TogglePause()
    {
        if (busy || session is null) return;
        SetState(RecorderStateMachine.Next(state, RecorderCommand.TogglePause, settings.Countdown));
        if (state == RecorderState.Paused) session.Pause();
        else session.Resume();
        ShowElapsed();
    }

    // Errors from starting or stopping a recording that the user should see instead of a crash.
    public static bool IsRecordingError(Exception e) =>
        e is CaptureUnavailableException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or COMException;

    // Notes from the recording that the user should see when it stops.
    public static bool IsShownNote(string n) =>
        n.StartsWith("webcam lost") || n.StartsWith("mic lost") || n.StartsWith("system sound lost") || n.Contains("disk") || n.StartsWith("The logo");

    // A black first frame means a protected window only. Monitors and regions can be black for real.
    public static bool BlockedByBlack(CaptureTarget target, bool looksBlack) => looksBlack && target is WindowTarget;

    internal void Warn(string text, string caption)
    {
        // WM_HOTKEY still arrives inside the message box loop, so the busy flag keeps a hotkey from starting a recording there.
        var wasBusy = busy;
        busy = true;
        NativeMethods.ShowWarning(hwnd, text, caption);
        busy = wasBusy;
    }

    void Fail(Exception e)
    {
        NativeMethods.ShowWarning(hwnd, e.Message, "Record");
        state = RecorderStateMachine.Next(state, RecorderCommand.Failed, settings.Countdown);
    }

    async Task StartRecording()
    {
        RecordingOptions options;
        PreflightResult p;
        settingsWindow?.Close(); // its changes would not reach a running recording
        busy = true;
        SetState(state);
        try
        {
            var firstCam = settings.Layout.Camera.Visible && settings.WebcamId is null ? (await WebcamSource.ListAsync()).FirstOrDefault().Id : null;
            options = RecordingOptionsBuilder.Build(settings, CurrentTarget, firstCam);
            p = await RecordingSession.PreflightAsync(options);
            if (BlockedByBlack(CurrentTarget, p.LooksBlack))
            {
                NativeMethods.ShowWarning(hwnd, "This window cannot be captured. It may be protected or running as administrator.", "Record");
                return;
            }
            if (p.Encoders == EncoderSupport.TooFewHardwareSessions)
            {
                if (!NativeMethods.AskYesCancel(hwnd, "This PC cannot encode 3 videos at once. Save the raw files only, without the finished video?", "Record")) return;
                options = options with { WriteFinal = false };
            }
            if (p.Encoders == EncoderSupport.SoftwareOnly)
                NativeMethods.ShowWarning(hwnd, "No hardware video encoder was found. Recording will run at 30 frames per second.", "Record");
            if (p.ScaledDown)
                NativeMethods.ShowWarning(hwnd, $"The captured area is larger than the encoder accepts. The video will be scaled down to {p.FrameSize.Width} x {p.FrameSize.Height}.", "Record");
            showFps = p.FrameRate < options.FrameRate && p.Encoders != EncoderSupport.SoftwareOnly;
            FpsText.Text = $"{p.FrameRate} fps";
        }
        catch (Exception e) when (IsRecordingError(e)) { Fail(e); return; }
        finally { busy = false; SetState(state); }

        SetState(RecorderStateMachine.Next(state, RecorderCommand.ToggleRecord, settings.Countdown));
        var run = ++countdownRun;
        for (var n = 3; n > 0 && state == RecorderState.Countdown; n--)
        {
            CountdownText.Text = n.ToString();
            ShowOutline();
            await Task.Delay(1000);
            if (run != countdownRun) return; // cancelled and started again during this second
        }
        if (state == RecorderState.Countdown) SetState(RecorderStateMachine.Next(state, RecorderCommand.CountdownDone, settings.Countdown));
        if (state != RecorderState.Recording) return;

        busy = true;
        SetState(state);
        try
        {
            var s = await RecordingSession.StartAsync(options, p);
            s.StoppedByItself += r => DispatcherQueue.TryEnqueue(() => StoppedByItself(s, r));
            // A window closed in the first instant fired the event before anyone listened. The queued call runs after busy is cleared.
            if (s.HasStopped) DispatcherQueue.TryEnqueue(async () => StoppedByItself(s, await s.StopAsync()));
            session = s;
            s.SetLayout(settings.Layout); // camera changes made during the countdown and the start
            ShowElapsed();
            clock.Start();
        }
        catch (Exception e) when (IsRecordingError(e)) { Fail(e); }
        finally { busy = false; SetState(state); }
    }

    async Task StopRecording()
    {
        var s = session!;
        busy = true;
        clock.Stop();
        SetState(RecorderStateMachine.Next(state, RecorderCommand.ToggleRecord, settings.Countdown));
        RecordingResult result;
        try
        {
            result = await s.StopAsync();
            await s.DisposeAsync();
        }
        catch (Exception e) when (IsRecordingError(e))
        {
            Warn(e.Message, "Recording");
            return;
        }
        finally { session = null; busy = false; SetState(state); }
        var lines = result.Notes.Where(IsShownNote).ToList();
        if (result.DroppedFrames > 0) lines.Insert(0, $"Dropped frames in the finished video: {result.DroppedFrames}");
        if (lines.Count > 0) Warn(string.Join("\n", lines), "Recording");
        System.Diagnostics.Process.Start("explorer.exe", $"\"{result.Folder}\"");
    }


    void StoppedByItself(RecordingSession s, RecordingResult r)
    {
        if (session != s || busy) return; // a stop by the user is already saving it
        session = null;
        clock.Stop();
        SetState(RecorderStateMachine.Next(state, RecorderCommand.ToggleRecord, settings.Countdown));
        _ = s.DisposeAsync();
        if (r.Notes.Count > 0) Warn(r.Notes[^1], "Recording");
    }

    void ShowElapsed()
    {
        if (session is null) return;
        var t = session.Elapsed;
        TimerText.Text = t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        ShowOutline();
    }
}
