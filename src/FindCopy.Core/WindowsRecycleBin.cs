using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FindCopy.Core;

/// <summary>Long-path shell recycling on an STA, with an explicit recycle-only operation.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRecycleBin
{
    internal static bool Move(string path, out string? error)
    {
        bool success = false;
        string? failure = null;
        var thread = new Thread(() =>
        {
            object? instance = null;
            IntPtr item = IntPtr.Zero;
            try
            {
                var clsid = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
                instance = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
                var operation = (IFileOperation)instance;
                // RECYCLEONDELETE requests recycling instead of permanent deletion; WANTNUKEWARNING
                // preserves the shell's warning if the destination cannot support that request.
                Check(operation.SetOperationFlags(0x20000000 | 0x00080000 | 0x00100000 | 0x4000 | 0x0010 | 0x0400 | 0x0004));
                var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                Check(SHCreateItemFromParsingName(WindowsFileSystem.ToExtendedPath(path), IntPtr.Zero, ref iid, out item));
                var sink = new RecycleSink();
                Check(operation.DeleteItem(item, sink));
                Check(operation.PerformOperations());
                Check(operation.GetAnyOperationsAborted(out bool aborted));
                if (aborted || !sink.Recycled) throw new IOException("Корзина не подтвердила перемещение файла");
                success = !File.Exists(path);
                if (!success) failure = "Файл остался в каталоге восстановления";
            }
            catch (Exception ex) { failure = ex.Message; }
            finally
            {
                if (item != IntPtr.Zero) Marshal.Release(item);
                if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            }
        }) { IsBackground = true, Name = "FindCopy Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error = failure;
        return success;
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, out IntPtr item);

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr window);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
        [PreserveSig] int CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
        [PreserveSig] int DeleteItem(IntPtr item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hr);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int hr, IntPtr created);
        [PreserveSig] int PreNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int hr, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleSink : IFileOperationProgressSink
    {
        public bool Recycled;
        public int PreDeleteItem(uint flags, IntPtr item) => (flags & 0x80) != 0 ? 0 : unchecked((int)0x80004004);
        public int PostDeleteItem(uint flags, IntPtr item, int hr, IntPtr created)
        { Recycled = hr >= 0 && created != IntPtr.Zero; return 0; }
        public int StartOperations() => 0;
        public int FinishOperations(int hr) => 0;
        public int PreRenameItem(uint flags, IntPtr item, string name) => 0;
        public int PostRenameItem(uint flags, IntPtr item, string name, int hr, IntPtr created) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, string name, int hr, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, string name, int hr, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr destination, string name) => 0;
        public int PostNewItem(uint flags, IntPtr destination, string name, string template, uint attributes, int hr, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
