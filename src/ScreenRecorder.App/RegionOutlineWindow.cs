using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ScreenRecorder.App;

// 2 pixel strips around a region and a small label, on a window shaped to just those parts.
// It marks the chosen region, and the region picker uses it for its border, size and hint.
public sealed class RegionOutlineWindow : Window
{
    const int Thickness = 2;
    readonly nint hwnd;
    readonly Grid root = new();
    readonly TextBlock text = new() { Margin = new Thickness(6, 2, 6, 2) };
    readonly Border label = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

    public RegionOutlineWindow()
    {
        label.Child = text;
        root.Children.Add(label);
        Content = root;
        hwnd = NativeMethods.MakeOverlay(this, "Region outline");
        NativeMethods.ClickThrough(hwnd);
        // it sits above the bar, so a close sent to the app's first window must not end it; Close() still does
        AppWindow.Closing += (_, e) => e.Cancel = true;
    }

    static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    // Accent while idle, the Record button's red from the countdown on. A null text hides the label.
    public void Show(nint hmonitor, RectInt32 region, bool red, string? text)
    {
        if (NativeMethods.MonitorRect(hmonitor) is not { } m) { Hide(); return; }
        var scale = NativeMethods.MonitorScale(hmonitor);
        var size = Measure(text, scale);
        var (sides, at) = TargetPicker.Outline(region, new((int)m.X, (int)m.Y, (int)m.Width, (int)m.Height), Thickness, size.Height);
        Draw(sides, text is null ? null : new(at.X, at.Y, size.Width, size.Height), red, scale);
    }

    // The label alone, centred near the top of the monitor.
    public void ShowHint(nint hmonitor, string text)
    {
        if (NativeMethods.MonitorRect(hmonitor) is not { } m) return;
        var scale = NativeMethods.MonitorScale(hmonitor);
        var size = Measure(text, scale);
        Draw([], new((int)(m.X + (m.Width - size.Width) / 2), (int)(m.Y + 48 * scale), size.Width, size.Height), false, scale);
    }

    public void Hide() => AppWindow.Hide();

    SizeInt32 Measure(string? s, double scale)
    {
        text.Text = s ?? "";
        label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return new((int)Math.Ceiling(label.DesiredSize.Width * scale), (int)Math.Ceiling(label.DesiredSize.Height * scale));
    }

    void Draw(RectInt32[] sides, RectInt32? labelRect, bool red, double scale)
    {
        root.Background = label.Background = Resource(red ? "SystemFillColorCriticalBrush" : "AccentFillColorDefaultBrush");
        text.Foreground = Resource("TextOnAccentFillColorPrimaryBrush");
        label.Visibility = labelRect is null ? Visibility.Collapsed : Visibility.Visible;
        RectInt32[] parts = labelRect is { } l ? [.. sides, l] : sides;
        int x = parts.Min(p => p.X), y = parts.Min(p => p.Y);
        int w = parts.Max(p => p.X + p.Width) - x, h = parts.Max(p => p.Y + p.Height) - y;
        if (labelRect is { } at) label.Margin = new Thickness((at.X - x) / scale, (at.Y - y) / scale, 0, 0);
        NativeMethods.PlaceOverlay(AppWindow, hwnd, new RectInt32(x, y, w, h), parts.Select(p => (p, 0)));
        AppWindow.Show(false);
        // once a test run found it had lost its topmost style, so it is set again each time
        NativeMethods.BringToTop(hwnd);
    }
}
