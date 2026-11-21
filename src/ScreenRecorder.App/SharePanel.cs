using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace ScreenRecorder.App;

// The Windows share panel of one window, sharing one picture file at a time.
sealed class SharePanel(nint hwnd)
{
    DataTransferManager? manager;
    StorageFile? file;

    // The panel releases the package when it closes. This runs on a background thread.
    public event Action? Closed;

    public async Task ShowAsync(string path)
    {
        file = await StorageFile.GetFileFromPathAsync(path);
        if (manager is null)
        {
            manager = DataTransferManagerInterop.GetForWindow(hwnd);
            manager.DataRequested += Requested;
        }
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
    }

    void Requested(DataTransferManager sender, DataRequestedEventArgs e)
    {
        if (file is null) return;
        e.Request.Data.Properties.Title = file.Name;
        e.Request.Data.SetStorageItems([file]);
        e.Request.Data.Destroyed += (_, _) => Closed?.Invoke();
    }
}
