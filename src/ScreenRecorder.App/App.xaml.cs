using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ScreenRecorder.App;

public partial class App : Application
{
    BarWindow? bar;

    public App() => InitializeComponent();

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
