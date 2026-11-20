using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace ScreenRecorder.App;

public sealed partial class BarWindow : Window
{
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder");
    static readonly string SettingsPath = Path.Combine(Folder, "settings.json");
    static readonly string PositionPath = Path.Combine(Folder, "bar-position.json");

    readonly nint hwnd;
    Settings settings = Settings.Load(SettingsPath);

    public CaptureTarget CurrentTarget { get; private set; } = new MonitorTarget(CaptureTarget.PrimaryMonitor());

    public BarWindow()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!NativeMethods.ExcludeFromCapture(hwnd)) System.Diagnostics.Debug.WriteLine("Bar could not be excluded from capture");

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        if (new UISettings().AdvancedEffectsEnabled && !new AccessibilitySettings().HighContrast)
            SystemBackdrop = new DesktopAcrylicBackdrop();
        else
        {
            Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
            Root.BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
            Root.BorderThickness = new Thickness(1);
        }

        CameraToggle.IsChecked = settings.Layout.Camera.Visible;
        MicToggle.IsChecked = settings.Mic;
        SoundToggle.IsChecked = settings.SystemAudio;
        (settings.Layout.Camera.Shape switch
        {
            CameraShape.Circle => CircleItem,
            CameraShape.RoundedSquare => RoundedSquareItem,
            _ => SquareItem,
        }).IsChecked = true;
        RemoveLogoItem.IsEnabled = settings.Layout.Logo.Path is not null;

        cameraOverlay = new CameraOverlayWindow();
        logoOverlay = new LogoOverlayWindow();
        cameraOverlay.Changed += c => Save(settings with { Layout = settings.Layout with { Camera = c } });
        follow = DispatcherQueue.CreateTimer();
        follow.Interval = TimeSpan.FromMilliseconds(100);
        follow.Tick += (_, _) => FollowWindow();
        clock = DispatcherQueue.CreateTimer();
        clock.Interval = TimeSpan.FromMilliseconds(250);
        clock.Tick += (_, _) => ShowElapsed();
        Closed += (_, _) => { follow.Stop(); clock.Stop(); countdownRun++; settingsWindow?.Close(); hotkeys?.Dispose(); cameraOverlay.Close(); logoOverlay.Close(); };
        cameraOverlay.WebcamId = settings.WebcamId;
        // Closing the bar while recording saves the recording first.
        AppWindow.Closing += async (_, e) =>
        {
            // While starting or stopping, closing would leave a running session or an unfinished file.
            if (busy) { e.Cancel = true; return; }
            if (session is null) return;
            e.Cancel = true;
            await StopRecording();
            Close();
        };
        RegisterHotkeys();
        Root.Loaded += (_, _) => { PlaceWindow(); UpdateOverlays(); };
    }

    readonly CameraOverlayWindow cameraOverlay;
    readonly LogoOverlayWindow logoOverlay;
    readonly DispatcherQueueTimer follow;
    RectF? overlayArea;

    static RectF? AreaOf(CaptureTarget target) => TargetPicker.AreaOnScreen(target, NativeMethods.MonitorRect, NativeMethods.WindowRect);

    void UpdateOverlays()
    {
        var layout = settings.Layout;
        overlayArea = AreaOf(CurrentTarget);
        if (overlayArea is { } a && layout.Camera.Visible) cameraOverlay.Show(a, layout); else cameraOverlay.Hide();
        if (overlayArea is { } b && layout.Logo.Visible) logoOverlay.Show(b, layout); else logoOverlay.Hide();
        // keeps running while a window target is minimized, so the overlays come back when it is restored
        if (CurrentTarget is WindowTarget && (layout.Camera.Visible || layout.Logo.Visible)) follow.Start(); else follow.Stop();
    }

    void FollowWindow()
    {
        var area = AreaOf(CurrentTarget);
        if (area == overlayArea) return;
        if (area is null || overlayArea is null) { UpdateOverlays(); return; }
        overlayArea = area;
        cameraOverlay.MoveCaptureArea(area.Value);
        logoOverlay.MoveCaptureArea(area.Value);
    }

    public void BringToFront()
    {
        AppWindow.Show();
        NativeMethods.SetForegroundWindow(hwnd);
    }

    // Sizes the window to its buttons, then puts it where the user left it,
    // or centered near the top of the primary monitor.
    void PlaceWindow()
    {
        FitSize();
        var scale = Root.XamlRoot.RasterizationScale;

        PointInt32? saved = null;
        try
        {
            var p = JsonSerializer.Deserialize<int[]>(File.ReadAllText(PositionPath));
            if (p is [var x, var y] && DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.None) is not null)
                saved = new PointInt32(x, y);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }

        var area = DisplayArea.Primary.WorkArea;
        AppWindow.Move(saved ?? new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (int)(24 * scale)));
    }

    void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        NativeMethods.DragWindow(hwnd);
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PositionPath, JsonSerializer.Serialize(new[] { AppWindow.Position.X, AppWindow.Position.Y }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // position is a convenience, losing it is fine
    }

    void TargetMenu_Opening(object? sender, object e)
    {
        TargetMenu.Items.Clear();

        var monitors = TargetPicker.Monitors();
        if (monitors.Count == 1)
            TargetMenu.Items.Add(Item("Desktop", new MonitorTarget(monitors[0].Hmonitor), "Desktop"));
        else
        {
            var desktop = new MenuFlyoutSubItem { Text = "Desktop" };
            foreach (var m in monitors) desktop.Items.Add(Item(m.Name, new MonitorTarget(m.Hmonitor), m.Name));
            TargetMenu.Items.Add(desktop);
        }

        var window = new MenuFlyoutSubItem { Text = "Window" };
        foreach (var w in TargetPicker.Windows()) window.Items.Add(Item(w.Title, new WindowTarget(w.Hwnd), w.Title));
        if (window.Items.Count == 0) window.Items.Add(new MenuFlyoutItem { Text = "No windows to capture", IsEnabled = false });
        TargetMenu.Items.Add(window);

        var region = new MenuFlyoutItem { Text = "Region" };
        region.Click += async (_, _) =>
        {
            AppWindow.Hide();
            // the overlays sit on top, so they would take the drag meant for the region picker
            cameraOverlay.Hide();
            logoOverlay.Hide();
            try { if (await RegionSelectWindow.PickAsync() is { } picked) SetTarget(picked, "Region"); }
            finally { AppWindow.Show(); UpdateOverlays(); }
        };
        TargetMenu.Items.Add(region);
    }

    MenuFlyoutItem Item(string text, CaptureTarget target, string label)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => SetTarget(target, label);
        return item;
    }

    void SetTarget(CaptureTarget target, string label)
    {
        CurrentTarget = target;
        TargetText.Text = label;
        UpdateOverlays();
    }

    void FitSize()
    {
        var scale = Root.XamlRoot.RasterizationScale;
        Root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(Root.DesiredSize.Width * scale), (int)Math.Ceiling(Root.DesiredSize.Height * scale)));
    }

    void Save(Settings s)
    {
        if (s.Layout != settings.Layout) session?.SetLayout(s.Layout);
        settings = s;
        UpdateOverlays();
        try { settings.Save(SettingsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the choice still holds for this run
    }

    void CameraToggle_Click(object sender, RoutedEventArgs e) =>
        Save(settings with { Layout = settings.Layout with { Camera = settings.Layout.Camera with { Visible = CameraToggle.IsChecked == true } } });

    void MicToggle_Click(object sender, RoutedEventArgs e) => Save(settings with { Mic = MicToggle.IsChecked == true });

    void SoundToggle_Click(object sender, RoutedEventArgs e) => Save(settings with { SystemAudio = SoundToggle.IsChecked == true });

    void Shape_Click(object sender, RoutedEventArgs e) =>
        Save(settings with { Layout = settings.Layout with { Camera = settings.Layout.Camera with { Shape = Enum.Parse<CameraShape>((string)((FrameworkElement)sender).Tag) } } });

    async void PickLogo_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(AppWindow.Id) { FileTypeFilter = { ".png", ".jpg", ".jpeg" } };
        if (await picker.PickSingleFileAsync() is not { } file) return;
        Save(settings with { Layout = settings.Layout with { Logo = settings.Layout.Logo with { Visible = true, Path = file.Path } } });
        RemoveLogoItem.IsEnabled = true;
    }

    void RemoveLogo_Click(object sender, RoutedEventArgs e)
    {
        Save(settings with { Layout = settings.Layout with { Logo = settings.Layout.Logo with { Visible = false, Path = null } } });
        RemoveLogoItem.IsEnabled = false;
    }

    void Screenshot_Click(object sender, RoutedEventArgs e) => TakeScreenshot();

    async void TakeScreenshot()
    {
        try { await Screenshot.CaptureAsync(CurrentTarget, settings.OutputFolder); }
        catch (CaptureUnavailableException ex) { NativeMethods.ShowWarning(hwnd, ex.Message, "Screenshot"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NativeMethods.ShowWarning(hwnd, $"The screenshot could not be saved to {settings.OutputFolder}.", "Screenshot");
        }
    }

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

    void Warn(string text, string caption)
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
            if (p.LooksBlack)
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
        var lines = result.Notes.Where(n => n.StartsWith("webcam lost") || n.StartsWith("mic lost") || n.StartsWith("system sound lost") || n.Contains("disk")).ToList();
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
    }

    // The one place that maps the recorder state to what the bar shows.
    void SetState(RecorderState s)
    {
        state = s;
        bool idle = s == RecorderState.Idle, countdown = s == RecorderState.Countdown, live = !idle && !countdown;
        Visibility Shown(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.IsEnabled = PauseButton.IsEnabled = StopButton.IsEnabled = !busy;
        RecordButton.Visibility = MicToggle.Visibility = SoundToggle.Visibility = ScreenshotButton.Visibility = Shown(!live);
        RecordIcon.Visibility = Shown(!countdown);
        CountdownText.Visibility = Shown(countdown);
        var recordName = countdown ? "Cancel countdown" : "Record";
        ToolTipService.SetToolTip(RecordButton, recordName);
        AutomationProperties.SetName(RecordButton, recordName);
        TimerText.Visibility = PauseButton.Visibility = StopButton.Visibility = Shown(live);
        FpsText.Visibility = Shown(live && showFps);
        var pauseName = s == RecorderState.Paused ? "Resume" : "Pause";
        PauseIcon.Glyph = s == RecorderState.Paused ? "" : "";
        ToolTipService.SetToolTip(PauseButton, pauseName);
        AutomationProperties.SetName(PauseButton, pauseName);
        TargetButton.IsEnabled = LogoButton.IsEnabled = SettingsButton.IsEnabled = MicToggle.IsEnabled = SoundToggle.IsEnabled = idle && !busy;
        if (Root.XamlRoot is not null) FitSize();
    }

    GlobalHotkeys? hotkeys;
    (bool Record, bool Screenshot) hotkeysOk;
    SettingsWindow? settingsWindow;

    (bool, bool) RegisterHotkeys()
    {
        hotkeys?.Dispose();
        hotkeys = new GlobalHotkeys(hwnd);
        return hotkeysOk = (hotkeys.Register(settings.RecordHotkey, ToggleRecord), hotkeys.Register(settings.ScreenshotHotkey, TakeScreenshot));
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(settings, hotkeysOk, ApplySettings, () => hotkeys?.Dispose());
            // LostFocus may not fire when the window closes with a hotkey box focused.
            settingsWindow.Closed += (_, _) => { settingsWindow = null; RegisterHotkeys(); };
        }
        settingsWindow.Activate();
    }

    (bool, bool) ApplySettings(Settings s)
    {
        Save(s);
        cameraOverlay.WebcamId = s.WebcamId;
        return RegisterHotkeys();
    }
}
