using Microsoft.UI.Input;
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
    bool loading = true;

    readonly Action releaseHotkeys;

    public SettingsWindow(Settings current, (bool Record, bool Screenshot) hotkeysOk, Func<Settings, (bool, bool)> apply, Action releaseHotkeys)
    {
        InitializeComponent();
        settings = current;
        this.apply = apply;
        this.releaseHotkeys = releaseHotkeys;
        Root.Loaded += (_, _) =>
        {
            var scale = Root.XamlRoot.RasterizationScale;
            AppWindow.Resize(new SizeInt32((int)(460 * scale), (int)(620 * scale)));
        };
        FolderText.Text = settings.OutputFolder;
        RecordHotkeyBox.Text = settings.RecordHotkey.ToString();
        ScreenshotHotkeyBox.Text = settings.ScreenshotHotkey.ToString();
        ShowHotkeyErrors(hotkeysOk);
        CountdownSwitch.IsOn = settings.Countdown;
        FrameRateBox.SelectedIndex = settings.FrameRate == 30 ? 0 : 1;
        LoadDevices();
    }

    async void LoadDevices()
    {
        try
        {
            Fill(WebcamBox, webcamIds, await WebcamSource.ListAsync(), settings.WebcamId);
            Fill(MicBox, micIds, await AudioSource.ListMicsAsync(), settings.MicId);
        }
        catch (Exception e) { Warn($"The device list could not be loaded. {e.Message}"); }
        finally { loading = false; }
    }

    static void Fill(ComboBox box, List<string?> ids, IReadOnlyList<(string Id, string Name)> devices, string? current)
    {
        box.Items.Add("Default");
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
    }

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
    void Hotkey_GotFocus(object sender, RoutedEventArgs e) => releaseHotkeys();

    void Hotkey_LostFocus(object sender, RoutedEventArgs e) => ShowHotkeyErrors(apply(settings));

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
        box.Text = h.ToString();
        Save(box == RecordHotkeyBox ? settings with { RecordHotkey = h } : settings with { ScreenshotHotkey = h });
        releaseHotkeys(); // Save registered them again, and the box still has focus
    }

    static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void Countdown_Toggled(object sender, RoutedEventArgs e) => Save(settings with { Countdown = CountdownSwitch.IsOn });

    void FrameRate_Changed(object sender, SelectionChangedEventArgs e) => Save(settings with { FrameRate = FrameRateBox.SelectedIndex == 0 ? 30 : 60 });

    void Webcam_Changed(object sender, SelectionChangedEventArgs e) => Save(settings with { WebcamId = webcamIds[Math.Max(0, WebcamBox.SelectedIndex)] });

    void Mic_Changed(object sender, SelectionChangedEventArgs e) => Save(settings with { MicId = micIds[Math.Max(0, MicBox.SelectedIndex)] });
}
