using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace ScreenRecorder.App;

public enum LogoClick { Pick, Show, Hide }

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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
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
        ShowToggles();
        ShowSource("Desktop");
        confirm = DispatcherQueue.CreateTimer();
        confirm.IsRepeating = false;
        confirm.Tick += (_, _) => EndConfirmation();

        cameraOverlay = new CameraOverlayWindow();
        logoOverlay = new LogoOverlayWindow();
        cameraOverlay.Changed += c => Save(settings with { Layout = settings.Layout with { Camera = c } });
        follow = DispatcherQueue.CreateTimer();
        follow.Interval = TimeSpan.FromMilliseconds(100);
        follow.Tick += (_, _) => FollowWindow();
        clock = DispatcherQueue.CreateTimer();
        clock.Interval = TimeSpan.FromMilliseconds(250);
        clock.Tick += (_, _) => ShowElapsed();
        Closed += (_, _) => { follow.Stop(); clock.Stop(); countdownRun++; settingsWindow?.Close(); hotkeys?.Dispose(); cameraOverlay.Close(); logoOverlay.Close(); outline.Close(); };
        cameraOverlay.WebcamId = settings.WebcamId;
        // Closing the bar while recording saves the recording first.
        AppWindow.Closing += (_, e) =>
        {
            // While starting or stopping, closing would leave a running session or an unfinished file.
            if (busy) { e.Cancel = true; return; }
            if (session is null) return;
            e.Cancel = true;
            Quit();
        };
        RegisterHotkeys();
        Root.ActualThemeChanged += (_, _) => ShowSource(sourceLabel);
        Root.Loaded += (_, _) => { PlaceWindow(); UpdateOverlays(); };
    }

    readonly CameraOverlayWindow cameraOverlay;
    readonly LogoOverlayWindow logoOverlay;
    readonly RegionOutlineWindow outline = new();
    readonly DispatcherQueueTimer follow, confirm;
    string sourceLabel = "Desktop";
    string? lastShot;
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
        ShowOutline();
    }

    // Marks the chosen region: accent with its size while idle, red without it from the countdown on.
    void ShowOutline()
    {
        if (CurrentTarget is RegionTarget r && AreaOf(r) is { } a)
            outline.Show(r.Hmonitor, new((int)a.X, (int)a.Y, (int)a.Width, (int)a.Height), state != RecorderState.Idle,
                state == RecorderState.Idle ? $"{r.Width} x {r.Height}" : null);
        else outline.Hide();
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

    void Desktop_Click(object sender, RoutedEventArgs e)
    {
        var monitors = TargetPicker.Monitors();
        if (monitors.Count == 1) { SetTarget(new MonitorTarget(monitors[0].Hmonitor), "Desktop"); return; }
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        foreach (var m in monitors) menu.Items.Add(Item(m.Name, new MonitorTarget(m.Hmonitor), m.Name));
        menu.ShowAt(DesktopSegment);
    }

    void Window_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        foreach (var w in TargetPicker.Windows()) menu.Items.Add(Item(w.Title, new WindowTarget(w.Hwnd), w.Title));
        if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "No windows to capture", IsEnabled = false });
        menu.ShowAt(WindowSegment);
    }

    async void Region_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
        // the overlays sit on top, so they would take the drag meant for the region picker
        cameraOverlay.Hide();
        logoOverlay.Hide();
        outline.Hide();
        try { if (await RegionSelectWindow.PickAsync() is { } picked) SetTarget(picked, "Region"); }
        finally { AppWindow.Show(); UpdateOverlays(); }
    }

    public static int SegmentOf(CaptureTarget target) => target switch { WindowTarget => 1, RegionTarget => 2, _ => 0 };

    // The selected segment looks raised, and its tooltip names the exact monitor or window.
    void ShowSource(string label)
    {
        sourceLabel = label;
        Button[] segments = [DesktopSegment, WindowSegment, RegionSegment];
        string[] names = ["Desktop", "Window", "Region"];
        var selected = SegmentOf(CurrentTarget);
        for (var i = 0; i < segments.Length; i++)
        {
            var on = i == selected;
            SegmentLook.Apply(segments[i], on, SegmentProbe);
            ToolTipService.SetToolTip(segments[i], on ? label : names[i]);
            AutomationProperties.SetName(segments[i], on ? names[i] + ", selected" : names[i]);
        }
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
        ShowSource(label);
        UpdateOverlays();
    }

    void FitSize()
    {
        var scale = Root.XamlRoot.RasterizationScale;
        Root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        // ResizeClient keeps the client at least 123 pixels tall at 150%, so the outer size is set instead
        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(Root.DesiredSize.Width * scale) + AppWindow.Size.Width - AppWindow.ClientSize.Width,
            (int)Math.Ceiling(Root.DesiredSize.Height * scale) + AppWindow.Size.Height - AppWindow.ClientSize.Height));
    }

    void Save(Settings s)
    {
        if (s.Layout != settings.Layout) session?.SetLayout(s.Layout);
        settings = s;
        ShowToggles();
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

    public static LogoClick LogoAction(string? path, bool visible) => path is null ? LogoClick.Pick : visible ? LogoClick.Hide : LogoClick.Show;

    async void LogoToggle_Click(object sender, RoutedEventArgs e)
    {
        var logo = settings.Layout.Logo;
        var action = LogoAction(logo.Path, logo.Visible);
        if (action != LogoClick.Pick) { SaveLogo(logo with { Visible = action == LogoClick.Show }); return; }
        ShowToggles(); // the toggle turns on only when a file is picked
        await PickLogo();
    }

    async void PickLogo_Click(object sender, RoutedEventArgs e) => await PickLogo();

    async Task PickLogo()
    {
        var picker = new FileOpenPicker(AppWindow.Id) { FileTypeFilter = { ".png", ".jpg", ".jpeg" } };
        if (await picker.PickSingleFileAsync() is not { } file) return;
        SaveLogo(settings.Layout.Logo with { Visible = true, Path = file.Path });
    }

    void RemoveLogo_Click(object sender, RoutedEventArgs e) => SaveLogo(settings.Layout.Logo with { Visible = false, Path = null });

    void SaveLogo(LogoItem logo) => Save(settings with { Layout = settings.Layout with { Logo = logo } });

    public static string NameWithState(string name, bool on) => $"{name}, {(on ? "on" : "off")}";

    void ShowToggles()
    {
        LogoToggle.IsChecked = settings.Layout.Logo is { Visible: true, Path: not null };
        RemoveLogoItem.Visibility = settings.Layout.Logo.Path is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (toggle, name) in new[] { (CameraToggle, "Camera"), (LogoToggle, "Logo"), (MicToggle, "Microphone"), (SoundToggle, "Computer sound") })
            AutomationProperties.SetName(toggle, NameWithState(name, toggle.IsChecked == true));
    }

    public static string ConfirmationText(bool copied) => copied ? "Screenshot saved and copied" : "Screenshot saved";

    // While recording only the Screenshot button shows a check, so the timer stays visible.
    void ShowConfirmation(ScreenshotResult shot)
    {
        lastShot = shot.Path;
        confirm.Stop();
        if (state == RecorderState.Idle)
        {
            ConfirmText.Text = ConfirmationText(shot.Copied);
            Bar.Visibility = Visibility.Collapsed;
            Confirmation.Visibility = Visibility.Visible;
            FitSize();
        }
        else LiveScreenshotIcon.Glyph = "\uE73E";
        confirm.Interval = TimeSpan.FromSeconds(state == RecorderState.Idle ? 3 : 1);
        confirm.Start();
    }

    void EndConfirmation()
    {
        confirm.Stop();
        LiveScreenshotIcon.Glyph = "\uE722";
        if (Confirmation.Visibility == Visibility.Collapsed) return;
        Confirmation.Visibility = Visibility.Collapsed;
        Bar.Visibility = Visibility.Visible;
        FitSize();
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{lastShot}\"");
        EndConfirmation();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(settings.OutputFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"The folder {settings.OutputFolder} could not be created.", "Screen Recorder");
            return;
        }
        System.Diagnostics.Process.Start("explorer.exe", $"\"{settings.OutputFolder}\"");
    }

    void Quit_Click(object sender, RoutedEventArgs e) => Quit();

    // The same path as closing the bar, so a running recording is stopped and saved first.
    async void Quit()
    {
        if (busy) return;
        if (session is not null) await StopRecording();
        Close();
    }

    void Screenshot_Click(object sender, RoutedEventArgs e) => TakeScreenshot();

    async void TakeScreenshot()
    {
        try { ShowConfirmation(await Screenshot.CaptureAsync(CurrentTarget, settings.OutputFolder)); }
        catch (CaptureUnavailableException ex) { NativeMethods.ShowWarning(hwnd, ex.Message, "Screenshot"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NativeMethods.ShowWarning(hwnd, $"The screenshot could not be saved to {settings.OutputFolder}.", "Screenshot");
        }
    }

    // The one place that maps the recorder state to what the bar shows.
    void SetState(RecorderState s)
    {
        state = s;
        bool idle = s == RecorderState.Idle, countdown = s == RecorderState.Countdown, live = !idle && !countdown;
        Visibility Shown(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.IsEnabled = PauseButton.IsEnabled = StopButton.IsEnabled = !busy;
        if (!idle && Confirmation.Visibility == Visibility.Visible) EndConfirmation();
        foreach (var c in new UIElement[] { ScreenshotButton, SourceSeparator, SourceSwitch, MicToggle, SoundToggle, MoreSeparator, MoreButton }) c.Visibility = Shown(idle);
        LiveScreenshotButton.Visibility = Shown(!idle);
        RecordButton.Visibility = Shown(!live);
        RecordIcon.Glyph = countdown ? "" : "";
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
        DesktopSegment.IsEnabled = WindowSegment.IsEnabled = RegionSegment.IsEnabled = MoreButton.IsEnabled = MicToggle.IsEnabled = SoundToggle.IsEnabled = idle && !busy;
        if (Root.XamlRoot is not null) FitSize();
        ShowOutline();
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
