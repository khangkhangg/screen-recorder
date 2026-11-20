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
public sealed partial class RegionSelectWindow : Window
{
    readonly nint hmonitor;
    readonly int width, height;
    readonly TaskCompletionSource<RegionTarget?> done;
    Point? start;
    bool closed;

    RegionSelectWindow(nint hmonitor, NativeMethods.Rect bounds, TaskCompletionSource<RegionTarget?> done)
    {
        InitializeComponent();
        (this.hmonitor, this.done) = (hmonitor, done);
        (width, height) = (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!NativeMethods.ExcludeFromCapture(hwnd)) System.Diagnostics.Debug.WriteLine("Region picker could not be excluded from capture");
        NativeMethods.SetOpacity(hwnd, 110);
        AppWindow.IsShownInSwitchers = false;
        // full screen fills the monitor the window sits on, in that monitor's own scale
        AppWindow.MoveAndResize(new RectInt32(bounds.Left, bounds.Top, 200, 200));
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) Root.Focus(FocusState.Programmatic); };
        Closed += (_, _) => { closed = true; done.TrySetResult(null); };
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
        start = e.GetCurrentPoint(Root).Position;
        Root.CapturePointer(e.Pointer);
        Root.Focus(FocusState.Pointer);
    }

    void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (start is not { } a) return;
        var b = e.GetCurrentPoint(Root).Position;
        Selection.Width = Math.Abs(b.X - a.X);
        Selection.Height = Math.Abs(b.Y - a.Y);
        Canvas.SetLeft(Selection, Math.Min(a.X, b.X));
        Canvas.SetTop(Selection, Math.Min(a.Y, b.Y));
        // same math as the saved region, so the label matches the result
        SizeText.Text = TargetPicker.ToRegion(hmonitor, a.X, a.Y, b.X, b.Y, Scale, width, height) is { } r
            ? $"{r.Width} x {r.Height}" : "Too small";
        Canvas.SetLeft(SizeLabel, b.X + 12);
        Canvas.SetTop(SizeLabel, b.Y + 12);
        Selection.Visibility = SizeLabel.Visibility = Visibility.Visible;
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
