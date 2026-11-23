using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;

namespace ScreenRecorder.App;

// Says what will be downloaded, from where, how large and where it is stored. Nothing downloads without Download.
sealed class CaptionConsentWindow : Window
{
    const double FitWidth = 460;
    static CaptionConsentWindow? open;
    readonly TaskCompletionSource<bool> done = new();
    readonly Border root = new() { Padding = new Thickness(24) };
    readonly CaptionChoice choice;
    static bool appClosing;
    bool closed;
    CancellationTokenSource? download;

    // A window open for the other choice answers no to this one.
    public static Task<bool> AskAsync(CaptionService service, CaptionChoice choice)
    {
        if (appClosing || open is not null && open.choice != choice) return Task.FromResult(false);
        if (open is null)
        {
            open = new CaptionConsentWindow(service, choice);
            open.Closed += (_, _) => open = null;
        }
        open.Activate();
        return open.done.Task;
    }

    // The app is quitting: the open window closes, which stops its download, and no new one opens.
    public static void CloseForQuit()
    {
        appClosing = true;
        open?.Close();
    }

    CaptionConsentWindow(CaptionService service, CaptionChoice choice)
    {
        this.choice = choice;
        Title = "Captions";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        BarWindow.ApplyLook(this, root);

        var files = string.Join("\n", service.Missing(choice).Select(f => $"{f.Name}, {f.Size / 1e6:0.0} MB, from {new Uri(f.Url).Host}"));
        var bar = new ProgressBar { Maximum = 1, Visibility = Visibility.Collapsed };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        var ok = new Button { Content = "Download", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = "Cancel" };
        root.Child = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Download the caption files?", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] },
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Captions are made on this PC. Sound and text never leave it. These files are downloaded once:" },
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = files, IsTextSelectionEnabled = true },
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"They are stored in {service.Folder}", IsTextSelectionEnabled = true },
                bar,
                error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
            },
        };
        Content = root;
        root.Loaded += (_, _) => Fit();

        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            error.Visibility = Visibility.Collapsed;
            bar.Value = 0;
            bar.Visibility = Visibility.Visible;
            download = new CancellationTokenSource();
            try
            {
                await service.DownloadAsync(choice, new Progress<double>(p => bar.Value = p), download.Token);
                done.TrySetResult(true);
                if (!closed) Close();
            }
            catch (Exception e) when (!download.IsCancellationRequested)
            {
                error.Text = $"The download failed. {e.Message}";
                error.Visibility = Visibility.Visible;
                bar.Visibility = Visibility.Collapsed;
                ok.Content = "Try again";
                ok.IsEnabled = true;
                Fit();
            }
            // The window was closed: whatever the download threw has no one to show it to.
            catch (Exception) { }
        };
        cancel.Click += (_, _) => Close();
        // Closing stops a running download, which deletes its partial file.
        Closed += (_, _) =>
        {
            closed = true;
            download?.Cancel();
            done.TrySetResult(false);
        };
    }

    void Fit()
    {
        var scale = root.XamlRoot.RasterizationScale;
        root.Measure(new Windows.Foundation.Size(FitWidth, double.PositiveInfinity));
        AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(FitWidth * scale), (int)Math.Ceiling(root.DesiredSize.Height * scale)));
    }
}
