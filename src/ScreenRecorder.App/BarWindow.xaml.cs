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
        NativeMethods.ExcludePopupsFromCapture();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        ApplyLook(this, Root);

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
        confirm.Interval = TimeSpan.FromSeconds(1);
        confirm.Tick += (_, _) => EndConfirmation();

        cameraOverlay = new CameraOverlayWindow();
        card = new ScreenshotCard();
        logoOverlay = new LogoOverlayWindow();
        cameraOverlay.Changed += c => Save(settings with { Layout = settings.Layout with { Camera = c } });
        actionBar = new ActionBarWindow();
        actionBar.Copy += CopyRegion;
        actionBar.Save += TakeScreenshot;
        actionBar.Share += ShareRegion;
        actionBar.Record += ToggleRecord;
        actionBar.Cancel += CancelRegion;
        share = new(hwnd);
        DeleteOldShares();
        // the overlays come back on release, placed for the new region
        frame.DragStarted += () => { dragging = true; cameraOverlay.Hide(); logoOverlay.Hide(); actionBar.Hide(); };
        frame.DragCancelled += () => { dragging = false; UpdateOverlays(); };
        frame.Changed += r => { dragging = false; SetTarget(r, "Region"); Remember(r); };
        follow = DispatcherQueue.CreateTimer();
        follow.Interval = TimeSpan.FromMilliseconds(100);
        follow.Tick += (_, _) => FollowWindow();
        clock = DispatcherQueue.CreateTimer();
        clock.Interval = TimeSpan.FromMilliseconds(250);
        clock.Tick += (_, _) => ShowElapsed();
        Closed += (_, _) => { follow.Stop(); clock.Stop(); countdownRun++; settingsWindow?.Close(); hotkeys?.Dispose(); cameraOverlay.Close(); logoOverlay.Close(); frame.Close(); card.Close(); actionBar.Close(); };
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
        Root.ActualThemeChanged += (_, _) => { ShowSource(sourceLabel); frame.Redraw(); };
        uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(frame.Redraw); // the accent color
        Root.Loaded += (_, _) => { PlaceWindow(); UpdateOverlays(); };
    }

    readonly CameraOverlayWindow cameraOverlay;
    readonly LogoOverlayWindow logoOverlay;
    readonly RegionFrameWindow frame = new();
    readonly UISettings uiSettings = new();
    readonly DispatcherQueueTimer follow, confirm;
    string sourceLabel = "Desktop";
    readonly ScreenshotCard card;
    readonly ActionBarWindow actionBar;
    readonly SharePanel share;
    bool dragging, picking;
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

    // Frames the chosen region: accent with its size while idle, red with the countdown number or the bar's time from then on.
    void ShowOutline()
    {
        if (CurrentTarget is RegionTarget r && AreaOf(r) is { } a)
        {
            var rect = new RectInt32((int)a.X, (int)a.Y, (int)a.Width, (int)a.Height);
            frame.Show(r.Hmonitor, rect, state == RecorderState.Idle, state == RecorderState.Countdown ? CountdownText.Text : TimerText.Text);
            if (!ActionBar.Shows(CurrentTarget, state, dragging, picking)) actionBar.Hide();
            else
            {
                actionBar.Show(r.Hmonitor, rect);
                frame.Raise();
            }
        }
        else
        {
            frame.Hide();
            actionBar.Hide();
        }
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
        var monitors = NativeMethods.Monitors().Select(m => (m.Hmonitor, m.Device, m.Bounds.Right - m.Bounds.Left, m.Bounds.Bottom - m.Bounds.Top));
        if (TargetPicker.RegionClick(CurrentTarget, settings.Region, monitors) is { } saved) { SetTarget(saved, "Region"); return; }
        AppWindow.Hide();
        // the overlays sit on top, so they would take the drag meant for the region picker
        cameraOverlay.Hide();
        logoOverlay.Hide();
        frame.Hide();
        picking = true;
        actionBar.Hide();
        try { if (await RegionSelectWindow.PickAsync() is { } picked) { SetTarget(picked, "Region"); Remember(picked); } }
        finally { picking = false; AppWindow.Show(); UpdateOverlays(); }
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
            ToolTipService.SetToolTip(segments[i], on && i == 2 ? "Region. Click again to select a new area." : on ? label : names[i]);
            AutomationProperties.SetName(segments[i], on ? names[i] + ", selected" : names[i]);
        }
    }

    MenuFlyoutItem Item(string text, CaptureTarget target, string label)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => SetTarget(target, label);
        return item;
    }

    // The region is saved for the next run, by monitor device name because handles change.
    void Remember(RegionTarget r)
    {
        if (NativeMethods.MonitorDevice(r.Hmonitor) is { } device) Save(settings with { Region = new SavedRegion(device, r.X, r.Y, r.Width, r.Height) });
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

    // While recording only the Screenshot button shows a check, so the timer stays visible.
    void ShowConfirmation(ScreenshotResult shot)
    {
        if (state == RecorderState.Idle)
        {
            card.Show(shot.Path, shot.Copied);
            return;
        }
        LiveScreenshotIcon.Glyph = Glyphs.Check;
        confirm.Stop();
        confirm.Start();
    }

    void EndConfirmation()
    {
        confirm.Stop();
        LiveScreenshotIcon.Glyph = Glyphs.Camera;
    }

    // Acrylic, or a solid background with a visible border when transparency is off or high contrast is on.
    internal static void ApplyLook(Window window, Border root)
    {
        if (new UISettings().AdvancedEffectsEnabled && !new AccessibilitySettings().HighContrast)
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
        else
        {
            root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
            root.BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
            root.BorderThickness = new Thickness(1);
        }
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

    async void TakeScreenshot() =>
        await Capture(async () => ShowConfirmation(await Screenshot.CaptureAsync(CurrentTarget, settings.OutputFolder)), settings.OutputFolder);

    // Every screenshot path shows these messages. The action bar waits, so a double click cannot take 2 pictures.
    async Task<bool> Capture(Func<Task> take, string folder)
    {
        actionBar.Busy = true;
        try
        {
            await take();
            return true;
        }
        catch (CaptureUnavailableException ex) { NativeMethods.ShowWarning(hwnd, ex.Message, "Screenshot"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NativeMethods.ShowWarning(hwnd, $"The screenshot could not be saved to {folder}.", "Screenshot");
        }
        finally { actionBar.Busy = false; }
        return false;
    }

    async void CopyRegion() => await Capture(async () => { await Screenshot.CopyAsync(CurrentTarget); actionBar.ShowCopied(); }, settings.OutputFolder);

    async void ShareRegion()
    {
        string path = "";
        if (!await Capture(async () => path = (await Screenshot.CaptureAsync(CurrentTarget, ActionBar.ShareFolder, copyToClipboard: false)).Path, ActionBar.ShareFolder)) return;
        try { await share.ShowAsync(path); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            NativeMethods.ShowWarning(hwnd, "Sharing is not available on this PC.", "Share");
        }
    }

    void CancelRegion()
    {
        var (s, desktop) = ActionBar.Cancel(settings);
        Save(s);
        SetTarget(desktop, "Desktop");
    }

    // Old shared pictures stay for a day, so a share target can still read them.
    static void DeleteOldShares()
    {
        try
        {
            var files = new DirectoryInfo(ActionBar.ShareFolder).EnumerateFiles().Select(f => (f.FullName, f.LastWriteTimeUtc));
            foreach (var path in ActionBar.Stale(files, DateTime.UtcNow).ToList()) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // The one place that maps the recorder state to what the bar shows.
    void SetState(RecorderState s)
    {
        state = s;
        bool idle = s == RecorderState.Idle, countdown = s == RecorderState.Countdown, live = !idle && !countdown;
        if (!idle) card.Dismiss();
        Visibility Shown(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.IsEnabled = PauseButton.IsEnabled = StopButton.IsEnabled = !busy;
        foreach (var c in new UIElement[] { ScreenshotButton, SourceSeparator, SourceSwitch, MicToggle, SoundToggle, MoreSeparator, MoreButton }) c.Visibility = Shown(idle);
        LiveScreenshotButton.Visibility = Shown(!idle);
        RecordButton.Visibility = Shown(!live);
        RecordIcon.Glyph = countdown ? Glyphs.Cancel : Glyphs.Record;
        CountdownText.Visibility = Shown(countdown);
        var recordName = countdown ? "Cancel countdown" : "Record";
        ToolTipService.SetToolTip(RecordButton, recordName);
        AutomationProperties.SetName(RecordButton, recordName);
        TimerText.Visibility = PauseButton.Visibility = StopButton.Visibility = Shown(live);
        FpsText.Visibility = Shown(live && showFps);
        var pauseName = s == RecorderState.Paused ? "Resume" : "Pause";
        PauseIcon.Glyph = s == RecorderState.Paused ? Glyphs.Play : Glyphs.Pause;
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
        Save(s with { Region = settings.Region }); // the settings window holds an older copy of the region
        cameraOverlay.WebcamId = s.WebcamId;
        return RegisterHotkeys();
    }
}
