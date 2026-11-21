using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ScreenRecorder.Capture;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace ScreenRecorder.App;

// One window covers one monitor, so drag points map to that monitor's pixels.
// It is black at 40% opacity, and its window region leaves out the selection, so the selection shows undimmed.
public sealed partial class RegionSelectWindow : Window
{
    readonly nint hmonitor, hwnd;
    readonly NativeMethods.Rect bounds;
    readonly int width, height;
    readonly TaskCompletionSource<RegionTarget?> done;
    readonly RegionOutlineWindow frame = new();
    Point? start;
    bool closed;

    RegionSelectWindow(nint hmonitor, NativeMethods.Rect bounds, TaskCompletionSource<RegionTarget?> done)
    {
        InitializeComponent();
        (this.hmonitor, this.bounds, this.done) = (hmonitor, bounds, done);
        (width, height) = (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!NativeMethods.ExcludeFromCapture(hwnd)) System.Diagnostics.Debug.WriteLine("Region picker could not be excluded from capture");
        NativeMethods.SetOpacity(hwnd, 102);
        AppWindow.IsShownInSwitchers = false;
        // full screen fills the monitor the window sits on, in that monitor's own scale
        AppWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, 200, 200));
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) Root.Focus(FocusState.Programmatic); };
        Closed += (_, _) => { closed = true; frame.Close(); done.TrySetResult(null); };
        frame.ShowHint(hmonitor, "Drag to select an area. Press Esc to cancel.");
    }

    public static async Task<RegionTarget?> PickAsync()
    {
        var done = new TaskCompletionSource<RegionTarget?>();
        var windows = NativeMethods.Monitors().Select(m => new RegionSelectWindow(m.Hmonitor, m.Bounds, done)).ToList();
        windows.ForEach(w => w.Activate());
        try { return await done.Task; }
        finally { windows.Where(w => !w.closed).ToList().ForEach(w => w.Close()); }
    }

    double Scale => Root.XamlRoot.RasterizationScale;

    void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Root);
        if (p.Properties.IsRightButtonPressed) { done.TrySetResult(null); return; }
        if (!p.Properties.IsLeftButtonPressed) return;
        start = p.Position;
        Root.CapturePointer(e.Pointer);
        Root.Focus(FocusState.Pointer);
        Draw(p.Position);
    }

    void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (start is not null) Draw(e.GetCurrentPoint(Root).Position);
    }

    void Draw(Point b)
    {
        var a = start!.Value;
        int Px(double v, int max) => Math.Clamp((int)Math.Round(v * Scale), 0, max);
        int left = Px(Math.Min(a.X, b.X), width), right = Px(Math.Max(a.X, b.X), width);
        int top = Px(Math.Min(a.Y, b.Y), height), bottom = Px(Math.Max(a.Y, b.Y), height);
        NativeMethods.SetRegion(hwnd, [new(0, 0, width, top), new(0, bottom, width, height - bottom),
            new(0, top, left, bottom - top), new(right, top, width - right, bottom - top)]);
        // same math as the saved region, so the label matches the result
        var r = TargetPicker.ToRegion(hmonitor, a.X, a.Y, b.X, b.Y, Scale, width, height);
        frame.Show(hmonitor, new(bounds.Left + left, bounds.Top + top, right - left, bottom - top), false,
            r is null ? "Too small" : $"{r.Width} x {r.Height}");
    }

    void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (start is not { } a) return;
        var b = e.GetCurrentPoint(Root).Position;
        done.TrySetResult(TargetPicker.ToRegion(hmonitor, a.X, a.Y, b.X, b.Y, Scale, width, height));
    }

    void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) done.TrySetResult(null);
    }
}

// The picker's root, only to show a crosshair pointer, which a UIElement can set only from a subclass.
public sealed partial class CrossCursorControl : UserControl
{
    public CrossCursorControl() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
}
