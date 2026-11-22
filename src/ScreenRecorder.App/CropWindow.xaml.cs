using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ScreenRecorder.Capture;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace ScreenRecorder.App;

// Crops a screenshot into a new PNG next to it; the original file is never changed.
public sealed partial class CropWindow : Window
{
    const double Pad = 12; // room around the picture, so the outer half of an edge grab area is still on the canvas
    readonly string path;
    readonly int width, height;
    readonly double viewScale;
    readonly Action<string, bool> saved;
    readonly nint hwnd;
    RectInt32 crop, dragRect;
    Point dragStart;
    Grip grip;
    bool saving, closed;

    CropWindow(string path, BitmapImage image, int width, int height, (double Width, double Height) fit, double scale, DisplayArea area, Action<string, bool> saved)
    {
        InitializeComponent();
        (this.path, this.width, this.height, this.saved) = (path, width, height, saved);
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Closed += (_, _) => closed = true;
        if (!NativeMethods.ExcludeFromCapture(hwnd)) Debug.WriteLine("Crop window could not be excluded from capture");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = presenter.IsMaximizable = false; presenter.IsAlwaysOnTop = true; } // above the region frame, which is also on top
        Picture.Source = image;
        (Picture.Width, Picture.Height) = fit;
        Place(Picture, Pad, Pad);
        View.Width = fit.Width + 2 * Pad;
        View.Height = fit.Height + 2 * Pad;
        viewScale = fit.Width / width;
        crop = new RectInt32(0, 0, width, height);
        Update();
        // padding 16 on each side, the 16 gap and the 32 tall buttons
        AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling((Math.Max(View.Width, 300) + 32) * scale), (int)Math.Ceiling((View.Height + 80) * scale)));
        var work = area.WorkArea;
        AppWindow.Move(new PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2, work.Y + (work.Height - AppWindow.Size.Height) / 2));
    }

    // Returns null when the file cannot be read, after telling the user.
    public static async Task<CropWindow?> OpenAsync(string path, DisplayArea area, Action<string, bool> saved)
    {
        var scale = NativeMethods.MonitorScale(Win32Interop.GetMonitorFromDisplayId(area.DisplayId));
        try
        {
            var picture = await ScreenshotCard.LoadPicture(path, 900, 600, scale);
            var window = new CropWindow(path, picture.Image, picture.Width, picture.Height, picture.Fit, scale, area, saved);
            window.Activate();
            return window;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            NativeMethods.ShowWarning(0, "The screenshot could not be opened.", "Crop screenshot");
            return null;
        }
    }

    Rect ToView(RectInt32 r) => new(Pad + r.X * viewScale, Pad + r.Y * viewScale, r.Width * viewScale, r.Height * viewScale);

    static void Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
    }

    void Update()
    {
        var v = ToView(crop);
        Dim.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry { Rect = new Rect(Pad, Pad, Picture.Width, Picture.Height) }, new RectangleGeometry { Rect = v } },
        };
        Place(Outline, v.X, v.Y);
        (Outline.Width, Outline.Height) = (v.Width, v.Height);
        Place(TopLeft, v.Left - 6, v.Top - 6);
        Place(TopRight, v.Right - 6, v.Top - 6);
        Place(BottomLeft, v.Left - 6, v.Bottom - 6);
        Place(BottomRight, v.Right - 6, v.Bottom - 6);
        SizeText.Text = $"{crop.Width} x {crop.Height}";
        // above the rectangle when there is room, else just inside its top left corner
        Place(SizeTag, v.X + (v.Y >= 28 ? 0 : 10), v.Y >= 28 ? v.Y - 28 : v.Y + 10);
    }

    void View_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(View);
        if (!point.Properties.IsLeftButtonPressed) return;
        grip = CropMath.HitTest(ToView(crop), point.Position);
        if (grip == Grip.None) return;
        (dragStart, dragRect) = (point.Position, crop);
        View.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    void View_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (grip == Grip.None) return;
        var p = e.GetCurrentPoint(View).Position;
        crop = CropMath.Drag(dragRect, grip, p.X - dragStart.X, p.Y - dragStart.Y, viewScale, width, height);
        Update();
    }

    void View_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        grip = Grip.None;
        View.ReleasePointerCapture(e.Pointer);
    }

    void View_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => grip = Grip.None;

    void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (FocusManager.GetFocusedElement(Content.XamlRoot) is ButtonBase) return; // the focused button acts on Enter itself
            Save();
        }
        else if (e.Key == VirtualKey.Escape) Close();
        else return;
        e.Handled = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    async void Save()
    {
        if (saving) return;
        saving = true;
        string target;
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            target = Path.Combine(folder, CardMath.CroppedName(Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)), Path.GetFileName(path)));
            await Screenshot.CropAsync(path, target, crop.X, crop.Y, crop.Width, crop.Height);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            saving = false;
            NativeMethods.ShowWarning(hwnd, "The cropped screenshot could not be saved.", "Crop screenshot");
            return;
        }
        if (closed) return; // a recording started and closed the window
        var copied = true;
        try { await Screenshot.CopyToClipboard(target); }
        catch (Exception e) { Debug.WriteLine(e); copied = false; } // the saved file is the result, as with a screenshot
        Close();
        saved(target, copied);
    }
}
