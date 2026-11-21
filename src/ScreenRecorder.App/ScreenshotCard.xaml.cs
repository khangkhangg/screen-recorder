using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using ScreenRecorder.Capture;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ScreenRecorder.App;

// The card that appears after a screenshot, with open, copy, share, crop and delete.
public sealed partial class ScreenshotCard : Window
{
    const int CardWidth = 300, CardHeight = 258, Margin = 16;
    readonly nint hwnd;
    readonly DispatcherQueueTimer life, copiedTimer;
    readonly List<CropWindow> crops = [];
    DataTransferManager? share;
    StorageFile? shareFile;
    string path = "";
    bool copied, hover, active, sharing;
    int width, height, shows;

    public ScreenshotCard()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!NativeMethods.ExcludeFromCapture(hwnd)) Debug.WriteLine("Screenshot card could not be excluded from capture");
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        BarWindow.ApplyLook(this, Root);
        life = DispatcherQueue.CreateTimer();
        life.Interval = TimeSpan.FromSeconds(8);
        life.IsRepeating = false;
        life.Tick += (_, _) => Hide();
        copiedTimer = DispatcherQueue.CreateTimer();
        copiedTimer.Interval = TimeSpan.FromSeconds(2);
        copiedTimer.IsRepeating = false;
        copiedTimer.Tick += (_, _) => StatusText.Text = CardMath.StatusText(copied, width, height);
        // Showing the card can raise Activated without giving it focus, so ask Windows which window has it.
        Activated += (_, e) =>
        {
            active = e.WindowActivationState != WindowActivationState.Deactivated && NativeMethods.GetForegroundWindow() == hwnd;
            if (active) sharing = false; // the share panel gives focus back to the card when it closes
            Countdown();
        };
        // Alt+F4 hides the card so the next screenshot can show it again; Close() from code still closes it.
        AppWindow.Closing += (_, e) => { e.Cancel = true; Hide(); };
        Closed += (_, _) => { life.Stop(); copiedTimer.Stop(); foreach (var c in crops.ToList()) c.Close(); };
    }

    // Reads the size first, so the bitmap is decoded only as large as it is shown.
    internal static async Task<(BitmapImage Image, int Width, int Height, (double Width, double Height) Fit)> LoadPicture(string path, double boxWidth, double boxHeight, double scale)
    {
        using var stream = File.OpenRead(path).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
        var fit = CardMath.Fit(w / scale, h / scale, boxWidth, boxHeight);
        stream.Seek(0);
        var image = new BitmapImage { DecodePixelWidth = (int)Math.Ceiling(fit.Width * scale) };
        await image.SetSourceAsync(stream);
        return (image, w, h, fit);
    }

    public async void Show(string path, bool copied)
    {
        var run = ++shows;
        var area = DisplayArea.Primary;
        var scale = NativeMethods.MonitorScale(Win32Interop.GetMonitorFromDisplayId(area.DisplayId));
        try
        {
            var picture = await LoadPicture(path, 280, 150, scale);
            if (run != shows) return;
            (this.path, this.copied, width, height) = (path, copied, picture.Width, picture.Height);
            Thumb.Source = picture.Image;
            (Thumb.Width, Thumb.Height) = picture.Fit;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            Debug.WriteLine(e); // the screenshot is saved; only its card is lost
            return;
        }
        copiedTimer.Stop();
        StatusText.Text = CardMath.StatusText(copied, width, height);
        AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(CardWidth * scale), (int)Math.Ceiling(CardHeight * scale)));
        var margin = (int)Math.Round(Margin * scale);
        var rect = CardMath.CardRect(area.WorkArea, AppWindow.Size.Width, AppWindow.Size.Height, margin);
        AppWindow.Move(new PointInt32(rect.X, rect.Y));
        AppWindow.Show(false); // never takes focus from what the user is typing in
        // The window rectangle includes an invisible border, so place the visible frame once Windows reports it.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (NativeMethods.WindowRect(hwnd) is not { } frame) return;
            var visible = CardMath.CardRect(area.WorkArea, (int)frame.Width, (int)frame.Height, margin);
            AppWindow.Move(new PointInt32(visible.X - ((int)frame.X - AppWindow.Position.X), visible.Y - ((int)frame.Y - AppWindow.Position.Y)));
        });
        Countdown();
    }

    // The 8 seconds pause while the pointer is over the card, it has focus, or the share panel is open.
    void Countdown()
    {
        life.Stop();
        if (!hover && !active && !sharing && AppWindow.IsVisible) life.Start();
    }

    void Hide()
    {
        life.Stop();
        hover = sharing = false;
        AppWindow.Hide();
    }

    // Pointing at the card also ends the share pause, for a share panel that closed without giving focus back.
    void Root_PointerEntered(object sender, PointerRoutedEventArgs e) { hover = true; sharing = false; Countdown(); }

    void Root_PointerExited(object sender, PointerRoutedEventArgs e) { hover = false; Countdown(); }

    void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        Hide();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Hide();

    void Open_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            NativeMethods.ShowWarning(hwnd, "The screenshot could not be opened.", "Screenshot");
            return;
        }
        Hide();
    }

    async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { await Screenshot.CopyToClipboard(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            NativeMethods.ShowWarning(hwnd, "The screenshot could not be copied.", "Screenshot");
            return;
        }
        copied = true;
        StatusText.Text = "Copied";
        copiedTimer.Stop();
        copiedTimer.Start();
    }

    async void Share_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            shareFile = await StorageFile.GetFileFromPathAsync(path);
            if (share is null)
            {
                share = DataTransferManagerInterop.GetForWindow(hwnd);
                share.DataRequested += Share_DataRequested;
            }
            sharing = true;
            Countdown();
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            sharing = false;
            Countdown();
            NativeMethods.ShowWarning(hwnd, "Sharing is not available on this PC.", "Share");
        }
    }

    void Share_DataRequested(DataTransferManager sender, DataRequestedEventArgs e)
    {
        if (shareFile is null) return;
        e.Request.Data.Properties.Title = shareFile.Name;
        e.Request.Data.SetStorageItems([shareFile]);
        // The package is released when the share panel closes, so the countdown can go on.
        e.Request.Data.Destroyed += (_, _) => DispatcherQueue.TryEnqueue(() => { sharing = false; Countdown(); });
    }

    async void Crop_Click(object sender, RoutedEventArgs e)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        Hide();
        var crop = await CropWindow.OpenAsync(path, area, Show);
        if (crop is null) return;
        crops.Add(crop);
        crop.Closed += (_, _) => crops.Remove(crop);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(path) && !NativeMethods.Recycle(path))
        {
            NativeMethods.ShowWarning(hwnd, "The screenshot could not be deleted.", "Screenshot");
            return;
        }
        Hide();
    }
}
