using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ScreenRecorder.App;

namespace ScreenRecorder.App.Tests;

public class RecycleTests
{
    [DllImport("kernel32.dll")]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, nint inBuf, int inSize, nint outBuf, int outSize, out int returned, nint overlapped);

    // IFileOperation needs a single-threaded apartment, like the app's UI thread
    static bool RecycleOnSta(string path)
    {
        var done = false;
        var t = new Thread(() => done = NativeMethods.Recycle(path));
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return done;
    }

    [Fact]
    public void A_file_goes_to_the_recycle_bin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recycle-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "x");
        Assert.True(RecycleOnSta(path));
        Assert.False(File.Exists(path));
        // the bin keeps the original path in a $I file next to the item
        var bin = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", WindowsIdentity.GetCurrent().User!.Value);
        Assert.Contains(Directory.GetFiles(bin, "$I*"), f => File.ReadAllText(f, Encoding.Unicode).Contains(path));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + ".*.keep"));
    }

    [Fact]
    public void A_file_too_big_for_the_recycle_bin_is_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"recycle-big-{Guid.NewGuid():N}.bin");
        try
        {
            // a sparse 1 TB file takes no disk space, but is larger than any Recycle Bin on this PC
            using (var fs = new FileStream(path, FileMode.CreateNew))
            {
                Assert.True(DeviceIoControl(fs.SafeFileHandle, 0x900C4, 0, 0, 0, 0, out _, 0)); // FSCTL_SET_SPARSE
                fs.SetLength(1L << 40);
            }
            // the shell deletes it for good, so it is back only through the second name
            Assert.False(RecycleOnSta(path));
            Assert.True(File.Exists(path));
            Assert.Equal(1L << 40, new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }
}
