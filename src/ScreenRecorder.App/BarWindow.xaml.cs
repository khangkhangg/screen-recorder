using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
        NativeMethods.ExcludeFromCapture(hwnd);

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

        Root.Loaded += (_, _) => PlaceWindow();
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
        var scale = Root.XamlRoot.RasterizationScale;
        Root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new SizeInt32((int)Math.Ceiling(Root.DesiredSize.Width * scale), (int)Math.Ceiling(Root.DesiredSize.Height * scale));
        AppWindow.ResizeClient(size);

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
            var picked = await RegionSelectWindow.PickAsync();
            AppWindow.Show();
            if (picked is not null) SetTarget(picked, "Region");
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
    }

    void Save(Settings s)
    {
        settings = s;
        settings.Save(SettingsPath);
    }

    void CameraToggle_Click(object sender, RoutedEventArgs e) =>
        Save(settings with { Layout = settings.Layout with { Camera = settings.Layout.Camera with { Visible = CameraToggle.IsChecked == true } } });

    void MicToggle_Click(object sender, RoutedEventArgs e) => Save(settings with { Mic = MicToggle.IsChecked == true });

    void SoundToggle_Click(object sender, RoutedEventArgs e) => Save(settings with { SystemAudio = SoundToggle.IsChecked == true });

    void Shape_Click(object sender, RoutedEventArgs e) =>
        Save(settings with { Layout = settings.Layout with { Camera = settings.Layout.Camera with { Shape = Enum.Parse<CameraShape>((string)((FrameworkElement)sender).Tag) } } });

    async void PickLogo_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(AppWindow.Id) { FileTypeFilter = { ".png", ".jpg" } };
        if (await picker.PickSingleFileAsync() is not { } file) return;
        Save(settings with { Layout = settings.Layout with { Logo = settings.Layout.Logo with { Visible = true, Path = file.Path } } });
        RemoveLogoItem.IsEnabled = true;
    }

    void RemoveLogo_Click(object sender, RoutedEventArgs e)
    {
        Save(settings with { Layout = settings.Layout with { Logo = settings.Layout.Logo with { Visible = false, Path = null } } });
        RemoveLogoItem.IsEnabled = false;
    }

    async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        try { await Screenshot.CaptureAsync(CurrentTarget, settings.OutputFolder); }
        catch (CaptureUnavailableException ex) { NativeMethods.ShowWarning(hwnd, ex.Message, "Screenshot"); }
    }
}
