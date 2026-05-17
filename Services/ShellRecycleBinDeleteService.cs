using System.Diagnostics;
using System.Runtime.InteropServices;
using MidFD.Models;

namespace MidFD.Services;

public static class ShellRecycleBinDeleteService
{
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_SIMPLEPROGRESS = 0x0100;
    private const int S_OK = 0;
    private const int E_ABORT = unchecked((int)0x80004004);
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private const uint DeleteOperationFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SIMPLEPROGRESS;

    public sealed record Progress(
        string Path,
        string Name,
        int ProcessedCount,
        int TotalCount,
        bool IsSuccess);

    public sealed record Result(
        int SuccessCount,
        int FailCount,
        bool IsCanceled,
        bool AnyOperationsAborted,
        int HResult,
        IReadOnlyList<string> SuccessPaths,
        long TotalMs,
        long QueueItemsMs,
        long PerformOperationsMs,
        long CallbackSpanMs,
        long MaxCallbackGapMs);

    public static Task<Result> DeleteToRecycleBinAsync(
        IReadOnlyList<string> paths,
        IntPtr ownerWindow,
        CancellationToken cancellationToken,
        Action<Progress> progress)
    {
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(DeleteToRecycleBinCore(paths, ownerWindow, cancellationToken, progress));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        thread.Name = "MidFD Shell recycle-bin delete";
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static Result DeleteToRecycleBinCore(
        IReadOnlyList<string> paths,
        IntPtr ownerWindow,
        CancellationToken cancellationToken,
        Action<Progress> progress)
    {
        var stopwatch = Stopwatch.StartNew();
        var sink = new ProgressSink(paths.Count, cancellationToken, progress);
        int performHr = S_OK;
        bool anyOperationsAborted = false;
        long queueItemsMs = 0;
        long performOperationsMs = 0;

        IFileOperation? operation = null;
        uint cookie = 0;
        try
        {
            operation = CreateFileOperation();
            int hr = operation.Advise(sink, out cookie);
            ThrowIfFailed(hr, nameof(IFileOperation.Advise));

            if (ownerWindow != IntPtr.Zero)
            {
                hr = operation.SetOwnerWindow(ownerWindow);
                ThrowIfFailed(hr, nameof(IFileOperation.SetOwnerWindow));
            }

            // MidFD already owns delete confirmation. Request Shell progress UI without per-file names.
            hr = operation.SetOperationFlags(DeleteOperationFlags);
            ThrowIfFailed(hr, nameof(IFileOperation.SetOperationFlags));
            LogService.Info($"[ShellDelete] OwnerWindow=0x{ownerWindow.ToInt64():X}, Flags=0x{DeleteOperationFlags:X}");

            long queueStartMs = stopwatch.ElapsedMilliseconds;
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IShellItem? item = null;
                try
                {
                    item = CreateShellItem(path);
                    hr = operation.DeleteItem(item, null);
                    ThrowIfFailed(hr, nameof(IFileOperation.DeleteItem));
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }
            queueItemsMs = stopwatch.ElapsedMilliseconds - queueStartMs;

            long performStartMs = stopwatch.ElapsedMilliseconds;
            performHr = operation.PerformOperations();
            long performEndMs = stopwatch.ElapsedMilliseconds;
            performOperationsMs = performEndMs - performStartMs;
            operation.GetAnyOperationsAborted(out anyOperationsAborted);

            LogService.Info(
                $"[Perf] ShellRecycleBinDelete PerformOperations: {performOperationsMs}ms " +
                $"for {paths.Count} items, callbacks={sink.SuccessItems.Count}, " +
                $"hr=0x{performHr:X8}, aborted={anyOperationsAborted}, " +
                $"callbackSpan={sink.CallbackSpanMs}ms, maxGap={sink.MaxCallbackGapMs}ms");
        }
        finally
        {
            if (operation != null && cookie != 0)
            {
                _ = operation.Unadvise(cookie);
            }

            ReleaseComObject(operation);
        }

        bool isCanceled = cancellationToken.IsCancellationRequested || anyOperationsAborted || performHr == E_ABORT;
        int successCount = sink.SuccessItems.Count;
        int failCount = performHr < 0 && !isCanceled ? Math.Max(1, paths.Count - successCount) : 0;
        return new Result(
            successCount,
            failCount,
            isCanceled,
            anyOperationsAborted,
            performHr,
            sink.SuccessItems.Select(item => item.OriginalPath).ToList(),
            stopwatch.ElapsedMilliseconds,
            queueItemsMs,
            performOperationsMs,
            sink.CallbackSpanMs,
            sink.MaxCallbackGapMs);
    }

    private static IFileOperation CreateFileOperation()
    {
        Type fileOperationType = Type.GetTypeFromCLSID(FileOperationClassId)
            ?? throw new InvalidOperationException("CLSID_FileOperation を取得できません。");
        object instance = Activator.CreateInstance(fileOperationType)
            ?? throw new InvalidOperationException("IFileOperation の初期化に失敗しました。");
        return (IFileOperation)instance;
    }

    private static IShellItem CreateShellItem(string path)
    {
        Guid iid = typeof(IShellItem).GUID;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item);
        ThrowIfFailed(hr, nameof(SHCreateItemFromParsingName));
        return item;
    }

    private static string? GetShellDisplayName(IShellItem? item, SIGDN sigdn)
    {
        if (item == null)
        {
            return null;
        }

        IntPtr displayName = IntPtr.Zero;
        try
        {
            int hr = item.GetDisplayName(sigdn, out displayName);
            if (hr != S_OK || displayName == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(displayName);
        }
        finally
        {
            if (displayName != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(displayName);
            }
        }
    }

    private static void ThrowIfFailed(int hr, string operationName)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    private interface IFileOperation
    {
        [PreserveSig]
        int Advise([MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink pfops, out uint pdwCookie);

        [PreserveSig]
        int Unadvise(uint dwCookie);

        [PreserveSig]
        int SetOperationFlags(uint dwOperationFlags);

        [PreserveSig]
        int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);

        [PreserveSig]
        int SetProgressDialog(IntPtr popd);

        [PreserveSig]
        int SetProperties(IntPtr pproparray);

        [PreserveSig]
        int SetOwnerWindow(IntPtr hwndOwner);

        [PreserveSig]
        int ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem);

        [PreserveSig]
        int ApplyPropertiesToItems(IntPtr punkItems);

        [PreserveSig]
        int RenameItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem,
            [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
            [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);

        [PreserveSig]
        int RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        [PreserveSig]
        int MoveItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem,
            [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName,
            [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);

        [PreserveSig]
        int MoveItems(IntPtr punkItems, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder);

        [PreserveSig]
        int CopyItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem,
            [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName,
            [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);

        [PreserveSig]
        int CopyItems(IntPtr punkItems, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder);

        [PreserveSig]
        int DeleteItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem,
            [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);

        [PreserveSig]
        int DeleteItems(IntPtr punkItems);

        [PreserveSig]
        int NewItem(
            [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder,
            uint dwFileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName,
            [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);

        [PreserveSig]
        int PerformOperations();

        [PreserveSig]
        int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
    private interface IFileOperationProgressSink
    {
        [PreserveSig]
        int StartOperations();

        [PreserveSig]
        int FinishOperations(int hrResult);

        [PreserveSig]
        int PreRenameItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        [PreserveSig]
        int PostRenameItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrRename, [MarshalAs(UnmanagedType.Interface)] IShellItem? psiNewlyCreated);

        [PreserveSig]
        int PreMoveItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

        [PreserveSig]
        int PostMoveItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, [MarshalAs(UnmanagedType.Interface)] IShellItem? psiNewlyCreated);

        [PreserveSig]
        int PreCopyItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

        [PreserveSig]
        int PostCopyItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, [MarshalAs(UnmanagedType.Interface)] IShellItem? psiNewlyCreated);

        [PreserveSig]
        int PreDeleteItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem);

        [PreserveSig]
        int PostDeleteItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, int hrDelete, [MarshalAs(UnmanagedType.Interface)] IShellItem? psiNewlyCreated);

        [PreserveSig]
        int PreNewItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        [PreserveSig]
        int PostNewItem(uint dwFlags, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, uint dwFileAttributes, int hrNew, [MarshalAs(UnmanagedType.Interface)] IShellItem? psiNewItem);

        [PreserveSig]
        int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);

        [PreserveSig]
        int ResetTimer();

        [PreserveSig]
        int PauseTimer();

        [PreserveSig]
        int ResumeTimer();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);

        [PreserveSig]
        int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint hint, out int piOrder);
    }

    private enum SIGDN : uint
    {
        FILESYSPATH = 0x80058000,
        DESKTOPABSOLUTEPARSING = 0x80028000
    }

    private sealed class ProgressSink : IFileOperationProgressSink
    {
        private readonly int _totalCount;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<Progress> _progress;
        private long _firstCallbackMs;
        private long _lastCallbackMs;

        public ProgressSink(int totalCount, CancellationToken cancellationToken, Action<Progress> progress)
        {
            _totalCount = totalCount;
            _cancellationToken = cancellationToken;
            _progress = progress;
            Stopwatch = Stopwatch.StartNew();
        }

        private Stopwatch Stopwatch { get; }
        public List<SuccessItem> SuccessItems { get; } = new();
        public long MaxCallbackGapMs { get; private set; }
        public long CallbackSpanMs => _firstCallbackMs == 0 || _lastCallbackMs == 0 ? 0 : _lastCallbackMs - _firstCallbackMs;

        public int StartOperations() => _cancellationToken.IsCancellationRequested ? E_ABORT : S_OK;

        public int FinishOperations(int hrResult) => S_OK;

        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName) => S_OK;

        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, int hrRename, IShellItem? psiNewlyCreated) => S_OK;

        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => S_OK;

        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) => S_OK;

        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => S_OK;

        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) => S_OK;

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            return _cancellationToken.IsCancellationRequested ? E_ABORT : S_OK;
        }

        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated)
        {
            RegisterCallback();
            string originalPath = GetShellDisplayName(psiItem, SIGDN.FILESYSPATH)
                ?? GetShellDisplayName(psiItem, SIGDN.DESKTOPABSOLUTEPARSING)
                ?? string.Empty;
            string recycleBinPath = GetShellDisplayName(psiNewlyCreated, SIGDN.FILESYSPATH)
                ?? GetShellDisplayName(psiNewlyCreated, SIGDN.DESKTOPABSOLUTEPARSING)
                ?? string.Empty;

            bool isSuccess = hrDelete >= 0 && !string.IsNullOrWhiteSpace(originalPath);
            if (isSuccess)
            {
                var item = new SuccessItem(originalPath, recycleBinPath);
                SuccessItems.Add(item);
                _progress(new Progress(
                    originalPath,
                    Path.GetFileName(originalPath),
                    SuccessItems.Count,
                    _totalCount,
                    true));
            }

            return _cancellationToken.IsCancellationRequested ? E_ABORT : S_OK;
        }

        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName) => S_OK;

        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem) => S_OK;

        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => S_OK;

        public int ResetTimer() => S_OK;

        public int PauseTimer() => S_OK;

        public int ResumeTimer() => S_OK;

        private void RegisterCallback()
        {
            long nowMs = Stopwatch.ElapsedMilliseconds;
            if (_firstCallbackMs == 0)
            {
                _firstCallbackMs = nowMs;
            }

            if (_lastCallbackMs != 0)
            {
                MaxCallbackGapMs = Math.Max(MaxCallbackGapMs, nowMs - _lastCallbackMs);
            }

            _lastCallbackMs = nowMs;
        }
    }

    private sealed record SuccessItem(string OriginalPath, string RecycleBinPath);
}
