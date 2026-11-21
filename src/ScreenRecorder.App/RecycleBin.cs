using System.Runtime.InteropServices;

namespace ScreenRecorder.App;

// Sends a file to the Recycle Bin through IFileOperation, and never deletes it for good.
public static class RecycleBin
{
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem { }

    // Slots this code does not call are listed by name only, to keep the vtable order.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink sink);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(); void SetProgressDialog(); void SetProperties(); void SetOwnerWindow();
        void ApplyPropertiesToItem(); void ApplyPropertiesToItems(); void RenameItem(); void RenameItems();
        void MoveItem(); void MoveItems(); void CopyItem(); void CopyItems();
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems(); void NewItem();
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hr);
        [PreserveSig] int PreRenameItem(uint flags, nint item, nint newName);
        [PreserveSig] int PostRenameItem(uint flags, nint item, nint newName, int hr, nint created);
        [PreserveSig] int PreMoveItem(uint flags, nint item, nint folder, nint newName);
        [PreserveSig] int PostMoveItem(uint flags, nint item, nint folder, nint newName, int hr, nint created);
        [PreserveSig] int PreCopyItem(uint flags, nint item, nint folder, nint newName);
        [PreserveSig] int PostCopyItem(uint flags, nint item, nint folder, nint newName, int hr, nint created);
        [PreserveSig] int PreDeleteItem(uint flags, nint item);
        [PreserveSig] int PostDeleteItem(uint flags, nint item, int hr, nint created);
        [PreserveSig] int PreNewItem(uint flags, nint folder, nint newName);
        [PreserveSig] int PostNewItem(uint flags, nint folder, nint newName, nint template, uint attributes, int hr, nint item);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    // Records whether the shell put the item in the Recycle Bin.
    [ComVisible(true)]
    sealed class DeleteSink : IFileOperationProgressSink
    {
        public bool Recycled;
        public int PostDeleteItem(uint flags, nint item, int hr, nint created) { Recycled = hr >= 0 && created != 0; return 0; } // created is the item in the bin
        public int PreDeleteItem(uint flags, nint item) => 0;
        public int StartOperations() => 0;
        public int FinishOperations(int hr) => 0;
        public int PreRenameItem(uint flags, nint item, nint newName) => 0;
        public int PostRenameItem(uint flags, nint item, nint newName, int hr, nint created) => 0;
        public int PreMoveItem(uint flags, nint item, nint folder, nint newName) => 0;
        public int PostMoveItem(uint flags, nint item, nint folder, nint newName, int hr, nint created) => 0;
        public int PreCopyItem(uint flags, nint item, nint folder, nint newName) => 0;
        public int PostCopyItem(uint flags, nint item, nint folder, nint newName, int hr, nint created) => 0;
        public int PreNewItem(uint flags, nint folder, nint newName) => 0;
        public int PostNewItem(uint flags, nint folder, nint newName, nint template, uint attributes, int hr, nint item) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern IShellItem SHCreateItemFromParsingName(string path, nint bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool CreateHardLinkW(string link, string existing, nint security);

    // Moves the file to the Recycle Bin, or leaves it where it is and returns false, with no dialog.
    // The shell deletes a file for good when it cannot recycle it, even with the recycle flags, so a second
    // name (a hard link) holds the data until the file is in the bin, and puts it back otherwise.
    // Needs a single-threaded apartment, which the UI thread is.
    public static bool Recycle(string path)
    {
        var keep = $"{path}.{Guid.NewGuid():N}.keep";
        if (!CreateHardLinkW(keep, path, 0)) return false; // FAT drives and some shares, which have no Recycle Bin either
        bool recycled = false;
        try { recycled = ShellRecycle(path); }
        finally
        {
            try
            {
                if (File.Exists(path)) File.Delete(keep); // untouched: drop only the second name
                else if (recycled) File.Delete(keep); // the data stays in the bin under the other name
                else File.Move(keep, path); // deleted for good: put it back
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { recycled = false; } // the data is still under one of the names
        }
        return recycled && !File.Exists(path);
    }

    static bool ShellRecycle(string path)
    {
        const uint FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOFX_RECYCLEONDELETE = 0x80000;
        try
        {
            var op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"))!)!;
            try
            {
                op.SetOperationFlags(FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI | FOFX_RECYCLEONDELETE);
                var sink = new DeleteSink();
                op.DeleteItem(SHCreateItemFromParsingName(path, 0, typeof(IShellItem).GUID), sink);
                op.PerformOperations();
                return sink.Recycled && !op.GetAnyOperationsAborted();
            }
            finally { Marshal.ReleaseComObject(op); }
        }
        catch (Exception e) // COM errors arrive as many exception types
        {
            System.Diagnostics.Debug.WriteLine(e);
            return false;
        }
    }
}
