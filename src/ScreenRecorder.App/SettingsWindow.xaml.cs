using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace ScreenRecorder.App;

// Every change is applied and saved at once through the bar, which answers whether each hotkey registered.
public sealed partial class SettingsWindow : Window
{
    Settings settings;
    readonly Func<Settings, (bool Record, bool Screenshot)> apply;
    readonly List<string?> webcamIds = [null], micIds = [null];
    bool loading = true, hotkeyBoxFocused;

    readonly Action releaseHotkeys;
    readonly Func<CaptionChoice, Task<bool>> captionsReady;

    public SettingsWindow(Settings current, (bool Record, bool Screenshot) hotkeysOk, Func<Settings, (bool, bool)> apply, Action releaseHotkeys, Func<CaptionChoice, Task<bool>> captionsReady)
    {
        this.captionsReady = captionsReady;
        InitializeComponent();
        if (!NativeMethods.ExcludeFromCapture(WinRT.Interop.WindowNative.GetWindowHandle(this)))
            System.Diagnostics.Debug.WriteLine("Settings window could not be excluded from capture");
        settings = current;
        this.apply = apply;
        this.releaseHotkeys = releaseHotkeys;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        // LostFocus does not fire when another app takes over, so the hotkeys come back on deactivation and go again on return.
        Activated += (_, e) =>
        {
            if (!hotkeyBoxFocused) return;
            if (e.WindowActivationState == WindowActivationState.Deactivated) ShowHotkeyErrors(apply(settings));
            else releaseHotkeys();
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = presenter.IsMaximizable = false;
        Root.Loaded += (_, _) => FitSize();
        FolderText.Text = settings.OutputFolder;
        RecordHotkeyBox.Text = Keys(settings.RecordHotkey);
        ScreenshotHotkeyBox.Text = Keys(settings.ScreenshotHotkey);
        ShowHotkeyErrors(hotkeysOk);
        CountdownSwitch.IsOn = settings.Countdown;
        ClickBox.ItemsSource = Enum.GetNames<ClickEffect>();
        ClickBox.SelectedIndex = Enum.IsDefined(settings.ClickEffect) ? (int)settings.ClickEffect : 0;
        CaptionBox.ItemsSource = new[] { "Off", "English", "Any language (detected)" };
        CaptionBox.SelectedIndex = Enum.IsDefined(settings.Captions) ? (int)settings.Captions : 0;
        ShowRate();
        Root.ActualThemeChanged += (_, _) => ShowRate();
        LoadDevices();
    }

    async void LoadDevices()
    {
        try
        {
            Fill(WebcamBox, webcamIds, await WebcamSource.ListAsync(), settings.WebcamId, "Default camera");
            Fill(MicBox, micIds, await AudioSource.ListMicsAsync(), settings.MicId, "Default microphone");
        }
        catch (Exception e) { Warn($"The device list could not be loaded. {e.Message}"); }
        finally { loading = false; }
    }

    static void Fill(ComboBox box, List<string?> ids, IReadOnlyList<(string Id, string Name)> devices, string? current, string defaultName)
    {
        box.Items.Add(defaultName);
        foreach (var d in devices)
        {
            box.Items.Add(d.Name);
            ids.Add(d.Id);
        }
        box.SelectedIndex = Math.Max(0, ids.IndexOf(current));
    }

    void Save(Settings s)
    {
        if (loading) return;
        settings = s;
        ShowHotkeyErrors(apply(s));
    }

    void ShowHotkeyErrors((bool Record, bool Screenshot) ok)
    {
        RecordHotkeyError.Visibility = ok.Record ? Visibility.Collapsed : Visibility.Visible;
        ScreenshotHotkeyError.Visibility = ok.Screenshot ? Visibility.Collapsed : Visibility.Visible;
        if (Root.XamlRoot is not null) FitSize();
    }

    // One column sized to its content, but never taller than the screen; the rest scrolls.
    void FitSize()
    {
        var scale = Root.XamlRoot.RasterizationScale;
        Root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var maxHeight = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea.Height - (int)(64 * scale);
        AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(Root.DesiredSize.Width * scale), Math.Min((int)Math.Ceiling(Root.DesiredSize.Height * scale), maxHeight)));
    }

    static string Keys(Hotkey h) => h.ToString().Replace("+", " + ");

    async void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await new FolderPicker(AppWindow.Id).PickSingleFolderAsync() is not { } folder) return;
            FolderText.Text = folder.Path;
            Save(settings with { OutputFolder = folder.Path });
        }
        catch (Exception ex) { Warn($"The folder could not be chosen. {ex.Message}"); }
    }

    void Warn(string text) => NativeMethods.ShowWarning(WinRT.Interop.WindowNative.GetWindowHandle(this), text, "Settings");

    // While a hotkey box has focus the bar's hotkeys are released, so pressing the current one reaches the box.
    void Hotkey_GotFocus(object sender, RoutedEventArgs e) { hotkeyBoxFocused = true; releaseHotkeys(); }

    void Hotkey_LostFocus(object sender, RoutedEventArgs e) { hotkeyBoxFocused = false; ShowHotkeyErrors(apply(settings)); }

    public static string? HotkeyText(int vk, bool ctrl, bool shift, bool alt, bool win)
    {
        var key = vk is >= 'A' and <= 'Z' or >= '0' and <= '9' ? ((char)vk).ToString() : vk is >= 0x70 and <= 0x7B ? $"F{vk - 0x6F}" : null;
        return key is null ? null : string.Concat(ctrl ? "Ctrl+" : "", shift ? "Shift+" : "", alt ? "Alt+" : "", win ? "Win+" : "", key);
    }

    // Records the next key combination pressed. Modifier keys alone and Tab pass through.
    void Hotkey_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var text = HotkeyText((int)e.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu),
            Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows));
        if (text is null) return;
        e.Handled = true;
        if (!Hotkey.TryParse(text, out var h)) return;
        var box = (TextBox)sender;
        box.Text = Keys(h);
        Save(box == RecordHotkeyBox ? settings with { RecordHotkey = h } : settings with { ScreenshotHotkey = h });
        releaseHotkeys(); // Save registered them again, and the box still has focus
    }

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void Countdown_Toggled(object sender, RoutedEventArgs e) => Save(settings with { Countdown = CountdownSwitch.IsOn });

    void Click_Changed(object sender, SelectionChangedEventArgs e) => Save(settings with { ClickEffect = (ClickEffect)Math.Max(0, ClickBox.SelectedIndex) });

    // A choice whose files are missing asks to download them first, and goes back to Off when the user says no.
    async void Captions_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        var c = (CaptionChoice)Math.Max(0, CaptionBox.SelectedIndex);
        if (c != CaptionChoice.Off && !await captionsReady(c)) CaptionBox.SelectedIndex = 0;
        else Save(settings with { Captions = c });
    }

    void FrameRate_Click(object sender, RoutedEventArgs e)
    {
        Save(settings with { FrameRate = (string)((Button)sender).Tag == "30" ? 30 : 60 });
        ShowRate();
    }

    void ShowRate()
    {
        foreach (var (b, fps) in new[] { (Rate30, 30), (Rate60, 60) })
        {
            var on = settings.FrameRate == fps;
            SegmentLook.Apply(b, on, SegmentProbe);
            AutomationProperties.SetName(b, $"{fps} frames per second" + (on ? ", selected" : ""));
        }
    }

    void Webcam_Changed(object sender, SelectionChangedEventArgs e) => Save(settings with { WebcamId = webcamIds[Math.Max(0, WebcamBox.SelectedIndex)] });

    void Mic_Changed(object sender, SelectionChangedEventArgs e) => Save(settings with { MicId = micIds[Math.Max(0, MicBox.SelectedIndex)] });
}
