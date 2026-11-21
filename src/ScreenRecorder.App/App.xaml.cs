using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ScreenRecorder.App;

public partial class App : Application
{
    BarWindow? bar;

    bool warning;

    public App()
    {
        InitializeComponent();
        // An error in an async void handler should not close the app, because a recording in progress must still be stopped and saved.
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine(e.Exception);
            e.Handled = true;
            if (bar is null || warning) return; // one box at a time, a repeating error does not stack them
            warning = true;
            bar.Warn(e.Message, "Screen Recorder");
            warning = false;
        };
    }

    [STAThread]
    static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var key = AppInstance.FindOrRegisterForKey("ScreenRecorder.Bar");
        if (!key.IsCurrent)
        {
            // lets the first instance take the foreground when it gets the activation
            NativeMethods.AllowSetForegroundWindow(key.ProcessId);
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => key.RedirectActivationToAsync(args).AsTask()).Wait();
            return;
        }
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new App();
            key.Activated += (_, _) => app.bar?.DispatcherQueue.TryEnqueue(app.bar.BringToFront);
        });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bar = new BarWindow();
        bar.Activate();
    }
}
