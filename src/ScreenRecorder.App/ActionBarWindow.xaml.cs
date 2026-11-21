using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ScreenRecorder.App;

// The buttons under the selected region. The window never takes focus, so the app the user shows keeps it.
public sealed partial class ActionBarWindow : Window
{
    readonly DispatcherQueueTimer copied;
    nint hmonitor;
    RectInt32 region;
    bool shown;

    public event Action? Copy, Save, Share, Record, Cancel;

    public ActionBarWindow()
    {
        InitializeComponent();
        NativeMethods.MakeOverlay(this, "Region actions");
        // the border gives the rounded corners of the floating bar
        ((OverlappedPresenter)AppWindow.Presenter).SetBorderAndTitleBar(true, false);
        BarWindow.ApplyLook(this, Root);
        copied = DispatcherQueue.CreateTimer();
        copied.Interval = TimeSpan.FromSeconds(1.5);
        copied.IsRepeating = false;
        copied.Tick += (_, _) => ShowCopied(false);
        Closed += (_, _) => copied.Stop();
    }

    public bool Busy
    {
        set { foreach (var b in new[] { CopyButton, SaveButton, ShareButton, RecordButton, CancelButton }) b.IsEnabled = !value; }
    }

    public void Show(nint hmonitor, RectInt32 region)
    {
        if (shown && hmonitor == this.hmonitor && region.Equals(this.region)) return;
        (this.hmonitor, this.region, shown) = (hmonitor, region, true);
        Place();
    }

    public void Hide()
    {
        shown = false;
        AppWindow.Hide();
    }

    public void ShowCopied(bool on = true)
    {
        CopyIcon.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CopyText.Text = on ? "Copied" : "Copy";
        if (on) { copied.Stop(); copied.Start(); }
        if (shown) Place();
    }

    void Place()
    {
        if (NativeMethods.MonitorRect(hmonitor) is not { } m) { Hide(); return; }
        var scale = NativeMethods.MonitorScale(hmonitor);
        Root.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        var size = new SizeInt32((int)Math.Ceiling(Root.DesiredSize.Width * scale), (int)Math.Ceiling(Root.DesiredSize.Height * scale));
        var at = ActionBar.Place(region, new((int)m.X, (int)m.Y, (int)m.Width, (int)m.Height), size, scale);
        // the window has an invisible border at the left, right and bottom, and a 1 pixel visible one at the top
        int border = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        AppWindow.MoveAndResize(new(at.X - border / 2, at.Y, size.Width + border, size.Height + AppWindow.Size.Height - AppWindow.ClientSize.Height));
        AppWindow.Show(false);
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Copy?.Invoke();

    void Save_Click(object sender, RoutedEventArgs e) => Save?.Invoke();

    void Share_Click(object sender, RoutedEventArgs e) => Share?.Invoke();

    void Record_Click(object sender, RoutedEventArgs e) => Record?.Invoke();

    void Cancel_Click(object sender, RoutedEventArgs e) => Cancel?.Invoke();
}
