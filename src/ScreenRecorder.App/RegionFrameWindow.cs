using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ScreenRecorder.Capture;
using Windows.Graphics;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace ScreenRecorder.App;

// The selected region's frame: border, corner marks, edge handles and a title tab. This window draws it and lets
// every click through. A second window at alpha 1 covers the grab areas and takes the drags, because a WinUI window
// cannot have see-through pixels and the grab areas are larger than the drawn parts.
public sealed class RegionFrameWindow : Window
{
    // design/icons/app-glyph-path.txt
    const string Glyph = "M48 32H208A32 32 0 0 1 240 64V160A32 32 0 0 1 208 192H48A32 32 0 0 1 16 160V64A32 32 0 0 1 48 32ZM104 192H152V200H104ZM84 200H172A12 12 0 0 1 172 224H84A12 12 0 0 1 84 200ZM90 112A38 38 0 1 0 166 112A38 38 0 1 0 90 112Z";
    const int VkEscape = 0x1B;
    readonly nint hwnd, inputHwnd;
    readonly Window input = new();
    readonly GripSurface surface = new() { Background = new SolidColorBrush(Colors.Black) };
    readonly Grid root = new();
    readonly StackPanel tab = new()
    {
        Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(8, 0, 10, 0),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };
    readonly Canvas grip = new() { Width = 6, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly Path icon = new() { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Glyph), Stretch = Stretch.Uniform, Width = 16, Height = 16 };
    readonly Ellipse dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock name = new() { Text = "Screen Recorder", FontWeight = FontWeights.Medium }, size = new() { Opacity = 0.75 };
    readonly FontIcon close = new() { Glyph = Glyphs.Cancel, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    readonly DispatcherQueueTimer escape;
    nint hmonitor;
    RectInt32 region, monitor, start;
    FrameLayout? layout;
    bool idle, pressed, dragging;
    double scale;
    string? text;
    FrameGrip held;
    PointInt32 grab;

    public event Action? DragStarted, DragCancelled, CloseRequested;
    public event Action<RegionTarget>? Changed;

    public RegionFrameWindow()
    {
        for (int i = 0; i < 6; i++)
        {
            var d = new Ellipse { Width = 2, Height = 2 };
            Canvas.SetLeft(d, i % 2 * 4);
            Canvas.SetTop(d, i / 2 * 4);
            grip.Children.Add(d);
        }
        foreach (var t in new[] { name, size }) { t.FontSize = 12; t.VerticalAlignment = VerticalAlignment.Center; }
        tab.Children.Add(grip);
        tab.Children.Add(icon);
        tab.Children.Add(dot);
        tab.Children.Add(name);
        tab.Children.Add(size);
        tab.Children.Add(close);
        root.Children.Add(tab);
        Content = root;
        hwnd = NativeMethods.MakeOverlay(this, "Region frame");
        NativeMethods.ClickThrough(hwnd);

        input.Content = surface;
        inputHwnd = NativeMethods.MakeOverlay(input, "Region frame handles");
        // alpha 0 would let the mouse through, alpha 1 cannot be seen
        NativeMethods.SetOpacity(inputHwnd, 1);
        surface.PointerPressed += Pressed;
        surface.PointerMoved += Moved;
        surface.PointerReleased += (_, _) => Released();
        surface.PointerCaptureLost += (_, _) => Released();
        escape = DispatcherQueue.CreateTimer();
        escape.Interval = TimeSpan.FromMilliseconds(30);
        // the frame never takes focus, so Esc is read from the keyboard state
        // and the low bit also catches a press that came and went between two ticks
        escape.Tick += (_, _) => { if ((NativeMethods.GetAsyncKeyState(VkEscape) & 0x8001) != 0) Cancel(); };

        // they sit above the bar, so a close sent to the app's first window must not end them; Close() still does
        AppWindow.Closing += (_, e) => e.Cancel = true;
        input.AppWindow.Closing += (_, e) => e.Cancel = true;
        Closed += (_, _) => { escape.Stop(); input.Close(); };
    }

    static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    static Visibility Shown(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

    // Idle shows the size and takes drags. Other states show the text (countdown or time) in red, and every part lets clicks through.
    public void Show(nint hmonitor, RectInt32 region, bool idle, string? text)
    {
        if (dragging && idle) return;
        Cancel();
        if (layout is not null && hmonitor == this.hmonitor && region.Equals(this.region) && idle == this.idle && text == this.text
            && NativeMethods.MonitorScale(hmonitor) == scale) return;
        if (NativeMethods.MonitorRect(hmonitor) is not { } m) { Hide(); return; }
        (this.hmonitor, this.idle, this.text) = (hmonitor, idle, text);
        monitor = new RectInt32((int)m.X, (int)m.Y, (int)m.Width, (int)m.Height);
        Draw(region);
    }

    // Theme brushes and the monitor scale are read when drawing, so a theme or accent change needs a redraw.
    public void Redraw()
    {
        if (layout is not null) Draw(region);
    }

    // Keeps the grab areas above the action bar, so a handle under it still takes the drag.
    public void Raise() => NativeMethods.BringToTop(inputHwnd);

    public void Hide()
    {
        Cancel();
        layout = null;
        AppWindow.Hide();
        input.AppWindow.Hide();
    }

    void Draw(RectInt32 r)
    {
        region = r;
        scale = NativeMethods.MonitorScale(hmonitor);
        int Px(double dips) => (int)Math.Ceiling(dips * scale);
        double Width(TextBlock t) { t.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity)); return 8 + t.DesiredSize.Width; }

        root.Background = Resource(idle ? "AccentFillColorDefaultBrush" : "SystemFillColorCriticalBrush");
        Shape[] shapes = [icon, dot, .. grip.Children.Cast<Shape>()];
        foreach (var s in shapes) s.Fill = Resource("TextOnAccentFillColorPrimaryBrush");
        name.Foreground = size.Foreground = close.Foreground = Resource("TextOnAccentFillColorPrimaryBrush");
        size.Text = idle ? $"{r.Width} x {r.Height}" : text ?? "";
        grip.Visibility = icon.Visibility = close.Visibility = Shown(idle);
        dot.Visibility = Shown(!idle);
        name.Visibility = size.Visibility = Visibility.Visible; // a collapsed text measures 0 wide
        // DIPs without the texts: padding 18, grip 6 and icon 16 with a gap of 8, or the dot of 8
        double baseWidth = idle ? 48 : 26, nameWidth = idle ? Width(name) : 0, sizeWidth = Width(size);
        var (showName, showSize) = idle ? RegionFrame.TabContent(r.Width, Px(baseWidth), Px(nameWidth), Px(sizeWidth)) : (false, true);
        name.Visibility = Shown(showName);
        size.Visibility = Shown(showSize);
        var l = layout = RegionFrame.Layout(r, monitor, scale, Px(baseWidth + (showName ? nameWidth : 0) + (showSize ? sizeWidth : 0)));

        var t = l.Tab;
        var parts = new List<(RectInt32 Rect, int Round)>
        {
            (t, Px(16)),
            // the side that joins the border is square
            (new RectInt32(t.X, l.TabInside ? t.Y : t.Y + t.Height / 2, t.Width, t.Height - t.Height / 2), 0),
        };
        parts.AddRange(l.Strips.Select(s => (s, 0)));
        parts.AddRange(l.Marks.Select(s => (s, Px(6))));
        if (idle) parts.AddRange(l.Handles.Select(s => (s, Px(10))));
        var box = Box(parts.Select(p => p.Rect));
        tab.Margin = new Thickness((t.X - box.X) / scale, (t.Y - box.Y) / scale, 0, 0);
        tab.Width = t.Width / scale;
        tab.Height = t.Height / scale;

        if (idle)
        {
            NativeMethods.PlaceOverlay(input.AppWindow, inputHwnd, Box(l.Grabs.Select(g => g.Area)), l.Grabs.Select(g => (g.Area, 0)));
            input.AppWindow.Show(false);
            NativeMethods.BringToTop(inputHwnd);
        }
        else input.AppWindow.Hide();
        NativeMethods.PlaceOverlay(AppWindow, hwnd, box, parts);
        AppWindow.Show(false);
        NativeMethods.BringToTop(hwnd);
    }

    static RectInt32 Box(IEnumerable<RectInt32> rects)
    {
        int x = rects.Min(p => p.X), y = rects.Min(p => p.Y);
        return new RectInt32(x, y, rects.Max(p => p.X + p.Width) - x, rects.Max(p => p.Y + p.Height) - y);
    }

    void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (layout is null || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
        NativeMethods.GetCursorPos(out grab);
        held = RegionFrame.HitTest(layout, grab.X, grab.Y);
        if (held == FrameGrip.None) return;
        pressed = true;
        start = region;
        surface.CapturePointer(e.Pointer);
    }

    void Moved(object sender, PointerRoutedEventArgs e)
    {
        NativeMethods.GetCursorPos(out var now);
        if (!pressed)
        {
            if (layout is not null) surface.Show(RegionFrame.HitTest(layout, now.X, now.Y));
            return;
        }
        // while the left button is held, a right press arrives as a move
        if (e.GetCurrentPoint(surface).Properties.IsRightButtonPressed) { Cancel(); return; }
        int dx = now.X - grab.X, dy = now.Y - grab.Y;
        if (!dragging && dx * dx + dy * dy < 16) return;
        if (!dragging)
        {
            dragging = true;
            NativeMethods.GetAsyncKeyState(VkEscape); // clears a press from before the drag
            escape.Start();
            DragStarted?.Invoke();
        }
        Draw(held == FrameGrip.Move ? RegionFrame.Move(start, dx, dy, monitor) : RegionFrame.Resize(start, held, dx, dy, monitor));
    }

    void Released()
    {
        if (!pressed) return;
        var moved = dragging;
        var closing = !moved && idle && held == FrameGrip.Move && layout is not null && RegionFrame.OnTabClose(layout, grab.X, grab.Y);
        End();
        if (closing) { CloseRequested?.Invoke(); return; } // a click on the X of the tab
        if (moved && !region.Equals(start))
            Changed?.Invoke(new RegionTarget(hmonitor, region.X - monitor.X, region.Y - monitor.Y, region.Width, region.Height));
        else if (moved) DragCancelled?.Invoke();
    }

    // Puts the region back where the drag started.
    void Cancel()
    {
        if (!pressed) return;
        var moved = dragging;
        End();
        if (!moved) return;
        Draw(start);
        DragCancelled?.Invoke();
    }

    void End()
    {
        pressed = dragging = false;
        escape.Stop();
        surface.ReleasePointerCaptures();
    }

    // ProtectedCursor can be set only from a subclass.
    sealed class GripSurface : Grid
    {
        FrameGrip? shown;

        public void Show(FrameGrip g)
        {
            if (g == shown) return;
            shown = g;
            ProtectedCursor = InputSystemCursor.Create(
                g == FrameGrip.None ? InputSystemCursorShape.Arrow
                : g == FrameGrip.Move ? InputSystemCursorShape.SizeAll
                : g is FrameGrip.Left or FrameGrip.Right ? InputSystemCursorShape.SizeWestEast
                : g is FrameGrip.Top or FrameGrip.Bottom ? InputSystemCursorShape.SizeNorthSouth
                : g == (FrameGrip.Left | FrameGrip.Top) || g == (FrameGrip.Right | FrameGrip.Bottom) ? InputSystemCursorShape.SizeNorthwestSoutheast
                : InputSystemCursorShape.SizeNortheastSouthwest);
        }
    }
}
