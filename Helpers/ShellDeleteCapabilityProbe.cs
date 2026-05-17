using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace MidFD.Helpers;

internal static class ShellDeleteCapabilityProbe
{
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOERRORUI = 0x0400;
    private const int S_OK = 0;
    private const int E_ABORT = unchecked((int)0x80004004);
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public static string Run(int cancelAfter, int count)
    {
        var totalStopwatch = Stopwatch.StartNew();
        string root = Path.Combine(Path.GetTempPath(), "MidFDShellDeleteProbe", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);

        var filePaths = Enumerable.Range(1, count)
            .Select(index => Path.Combine(root, $"probe-{index:00}.txt"))
            .ToArray();

        foreach (string path in filePaths)
        {
            File.WriteAllText(path, $"MidFD shell delete probe {Path.GetFileName(path)}");
        }

        long setupCompletedMs = totalStopwatch.ElapsedMilliseconds;
        var sink = new ProbeProgressSink(cancelAfter, totalStopwatch);
        int performHr = S_OK;
        bool anyOperationsAborted = false;
        string? exceptionText = null;
        long operationCreatedMs = 0;
        long adviseCompletedMs = 0;
        long flagsCompletedMs = 0;
        long itemsQueuedMs = 0;
        long performStartedMs = 0;
        long performCompletedMs = 0;
        long getAbortedCompletedMs = 0;

        try
        {
            IFileOperation operation = CreateFileOperation();
            operationCreatedMs = totalStopwatch.ElapsedMilliseconds;
            uint cookie = 0;
            try
            {
                int hr = operation.Advise(sink, out cookie);
                ThrowIfFailed(hr, nameof(IFileOperation.Advise));
                adviseCompletedMs = totalStopwatch.ElapsedMilliseconds;

                hr = operation.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT);
                ThrowIfFailed(hr, nameof(IFileOperation.SetOperationFlags));
                flagsCompletedMs = totalStopwatch.ElapsedMilliseconds;

                foreach (string path in filePaths)
                {
                    IShellItem item = CreateShellItem(path);
                    hr = operation.DeleteItem(item, null);
                    ThrowIfFailed(hr, nameof(IFileOperation.DeleteItem));
                }
                itemsQueuedMs = totalStopwatch.ElapsedMilliseconds;

                performStartedMs = totalStopwatch.ElapsedMilliseconds;
                performHr = operation.PerformOperations();
                performCompletedMs = totalStopwatch.ElapsedMilliseconds;
                operation.GetAnyOperationsAborted(out anyOperationsAborted);
                getAbortedCompletedMs = totalStopwatch.ElapsedMilliseconds;
            }
            finally
            {
                if (cookie != 0)
                {
                    _ = operation.Unadvise(cookie);
                }

                ReleaseComObject(operation);
            }
        }
        catch (Exception ex)
        {
            exceptionText = ex.ToString();
        }

        string reportPath = Path.Combine(root, "shell-delete-probe-report.md");
        File.WriteAllText(reportPath, BuildReport(
            root,
            filePaths,
            cancelAfter,
            performHr,
            anyOperationsAborted,
            sink,
            setupCompletedMs,
            operationCreatedMs,
            adviseCompletedMs,
            flagsCompletedMs,
            itemsQueuedMs,
            performStartedMs,
            performCompletedMs,
            getAbortedCompletedMs,
            exceptionText));
        return reportPath;
    }

    private static string BuildReport(
        string root,
        IReadOnlyList<string> requestedPaths,
        int cancelAfter,
        int performHr,
        bool anyOperationsAborted,
        ProbeProgressSink sink,
        long setupCompletedMs,
        long operationCreatedMs,
        long adviseCompletedMs,
        long flagsCompletedMs,
        long itemsQueuedMs,
        long performStartedMs,
        long performCompletedMs,
        long getAbortedCompletedMs,
        string? exceptionText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MidFD Shell Delete Capability Probe");
        sb.AppendLine();
        sb.AppendLine($"- Root: `{root}`");
        sb.AppendLine($"- Requested count: {requestedPaths.Count}");
        sb.AppendLine($"- Cancel after PostDeleteItem count: {(cancelAfter <= 0 ? "disabled" : cancelAfter)}");
        sb.AppendLine($"- PerformOperations HRESULT: 0x{performHr:X8}");
        sb.AppendLine($"- GetAnyOperationsAborted: {anyOperationsAborted}");
        sb.AppendLine($"- PreDeleteItem count: {sink.PreDeleteEvents.Count}");
        sb.AppendLine($"- PostDeleteItem count: {sink.PostDeleteEvents.Count}");
        sb.AppendLine($"- Successful PostDeleteItem count: {sink.PostDeleteEvents.Count(e => e.IsSuccess)}");
        sb.AppendLine($"- Existing after operation count: {requestedPaths.Count(File.Exists)}");
        sb.AppendLine($"- Setup completed: {setupCompletedMs}ms");
        sb.AppendLine($"- IFileOperation created: {operationCreatedMs}ms");
        sb.AppendLine($"- Advise completed: {adviseCompletedMs}ms");
        sb.AppendLine($"- SetOperationFlags completed: {flagsCompletedMs}ms");
        sb.AppendLine($"- DeleteItem queue completed: {itemsQueuedMs}ms");
        sb.AppendLine($"- PerformOperations started: {performStartedMs}ms");
        sb.AppendLine($"- PerformOperations completed: {performCompletedMs}ms");
        sb.AppendLine($"- PerformOperations duration: {Math.Max(0, performCompletedMs - performStartedMs)}ms");
        sb.AppendLine($"- GetAnyOperationsAborted completed: {getAbortedCompletedMs}ms");
        sb.AppendLine($"- Callback span: {sink.CallbackSpanMs}ms");
        sb.AppendLine($"- Max callback gap: {sink.MaxCallbackGapMs}ms");
        sb.AppendLine($"- Total display name resolve time: {sink.TotalDisplayNameResolveMs}ms");
        sb.AppendLine();

        sb.AppendLine("## Requested paths");
        foreach (string path in requestedPaths)
        {
            sb.AppendLine($"- `{path}` / exists after: {File.Exists(path)}");
        }
        sb.AppendLine();

        sb.AppendLine("## PreDeleteItem events");
        foreach (var item in sink.PreDeleteEvents)
        {
            sb.AppendLine($"- #{item.Sequence}: t={item.ElapsedMs}ms delta={item.DeltaMs}ms flags=0x{item.Flags:X8} resolve={item.ResolveMs}ms item=`{item.ItemDisplayName}`");
        }
        sb.AppendLine();

        sb.AppendLine("## PostDeleteItem events");
        foreach (var item in sink.PostDeleteEvents)
        {
            sb.AppendLine($"- #{item.Sequence}: t={item.ElapsedMs}ms delta={item.DeltaMs}ms flags=0x{item.Flags:X8} hr=0x{item.HResult:X8} success={item.IsSuccess}");
            sb.AppendLine($"  - item: `{item.ItemDisplayName}`");
            sb.AppendLine($"  - newlyCreated fileSystemPath: `{item.NewlyCreatedFileSystemPath ?? "(null)"}`");
            sb.AppendLine($"  - newlyCreated parsingName: `{item.NewlyCreatedParsingName ?? "(null)"}`");
            sb.AppendLine($"  - resolve item/newlyCreated: {item.ItemResolveMs}ms / {item.NewlyCreatedResolveMs}ms");
        }

        if (!string.IsNullOrWhiteSpace(exceptionText))
        {
            sb.AppendLine();
            sb.AppendLine("## Exception");
            sb.AppendLine("```text");
            sb.AppendLine(exceptionText);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static IShellItem CreateShellItem(string path)
    {
        Guid iid = typeof(IShellItem).GUID;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item);
        ThrowIfFailed(hr, nameof(SHCreateItemFromParsingName));
        return item;
    }

    private static IFileOperation CreateFileOperation()
    {
        Type fileOperationType = Type.GetTypeFromCLSID(FileOperationClassId)
            ?? throw new InvalidOperationException("CLSID_FileOperation を取得できません。");
        object instance = Activator.CreateInstance(fileOperationType)
            ?? throw new InvalidOperationException("IFileOperation の初期化に失敗しました。");
        return (IFileOperation)instance;
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
        DESKTOPABSOLUTEPARSING = 0x80028000,
        NORMALDISPLAY = 0
    }

    private sealed class ProbeProgressSink : IFileOperationProgressSink
    {
        private readonly int _cancelAfterPostDeleteCount;

        private readonly Stopwatch _stopwatch;
        private long _lastCallbackMs;

        public ProbeProgressSink(int cancelAfterPostDeleteCount, Stopwatch stopwatch)
        {
            _cancelAfterPostDeleteCount = cancelAfterPostDeleteCount;
            _stopwatch = stopwatch;
        }

        public List<PreDeleteEvent> PreDeleteEvents { get; } = new();
        public List<PostDeleteEvent> PostDeleteEvents { get; } = new();
        public long TotalDisplayNameResolveMs { get; private set; }
        public long MaxCallbackGapMs { get; private set; }
        public long CallbackSpanMs
        {
            get
            {
                var all = PreDeleteEvents.Select(e => e.ElapsedMs)
                    .Concat(PostDeleteEvents.Select(e => e.ElapsedMs))
                    .ToArray();
                return all.Length == 0 ? 0 : all.Max() - all.Min();
            }
        }

        public int StartOperations() => S_OK;

        public int FinishOperations(int hrResult) => S_OK;

        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName) => S_OK;

        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, int hrRename, IShellItem? psiNewlyCreated) => S_OK;

        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => S_OK;

        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) => S_OK;

        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => S_OK;

        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) => S_OK;

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            long elapsedMs = _stopwatch.ElapsedMilliseconds;
            long deltaMs = RegisterCallback(elapsedMs);
            var sw = Stopwatch.StartNew();
            string itemDisplayName =
                GetShellDisplayName(psiItem, SIGDN.FILESYSPATH) ??
                GetShellDisplayName(psiItem, SIGDN.DESKTOPABSOLUTEPARSING) ??
                "(unknown)";
            sw.Stop();
            TotalDisplayNameResolveMs += sw.ElapsedMilliseconds;
            PreDeleteEvents.Add(new PreDeleteEvent(
                PreDeleteEvents.Count + 1,
                elapsedMs,
                deltaMs,
                dwFlags,
                itemDisplayName,
                sw.ElapsedMilliseconds));
            return S_OK;
        }

        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated)
        {
            long elapsedMs = _stopwatch.ElapsedMilliseconds;
            long deltaMs = RegisterCallback(elapsedMs);
            int sequence = PostDeleteEvents.Count + 1;
            var itemSw = Stopwatch.StartNew();
            string itemDisplayName =
                GetShellDisplayName(psiItem, SIGDN.FILESYSPATH) ??
                GetShellDisplayName(psiItem, SIGDN.DESKTOPABSOLUTEPARSING) ??
                "(unknown)";
            itemSw.Stop();
            var newlyCreatedSw = Stopwatch.StartNew();
            string? newlyCreatedFileSystemPath = GetShellDisplayName(psiNewlyCreated, SIGDN.FILESYSPATH);
            string? newlyCreatedParsingName = GetShellDisplayName(psiNewlyCreated, SIGDN.DESKTOPABSOLUTEPARSING);
            newlyCreatedSw.Stop();
            TotalDisplayNameResolveMs += itemSw.ElapsedMilliseconds + newlyCreatedSw.ElapsedMilliseconds;
            PostDeleteEvents.Add(new PostDeleteEvent(
                sequence,
                elapsedMs,
                deltaMs,
                dwFlags,
                hrDelete,
                itemDisplayName,
                newlyCreatedFileSystemPath,
                newlyCreatedParsingName,
                itemSw.ElapsedMilliseconds,
                newlyCreatedSw.ElapsedMilliseconds));

            return _cancelAfterPostDeleteCount > 0 && sequence >= _cancelAfterPostDeleteCount
                ? E_ABORT
                : S_OK;
        }

        private long RegisterCallback(long elapsedMs)
        {
            long deltaMs = _lastCallbackMs == 0 ? 0 : elapsedMs - _lastCallbackMs;
            _lastCallbackMs = elapsedMs;
            MaxCallbackGapMs = Math.Max(MaxCallbackGapMs, deltaMs);
            return deltaMs;
        }

        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName) => S_OK;

        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem) => S_OK;

        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => S_OK;

        public int ResetTimer() => S_OK;

        public int PauseTimer() => S_OK;

        public int ResumeTimer() => S_OK;
    }

    private sealed record PreDeleteEvent(
        int Sequence,
        long ElapsedMs,
        long DeltaMs,
        uint Flags,
        string ItemDisplayName,
        long ResolveMs);

    private sealed record PostDeleteEvent(
        int Sequence,
        long ElapsedMs,
        long DeltaMs,
        uint Flags,
        int HResult,
        string ItemDisplayName,
        string? NewlyCreatedFileSystemPath,
        string? NewlyCreatedParsingName,
        long ItemResolveMs,
        long NewlyCreatedResolveMs)
    {
        public bool IsSuccess => HResult >= 0;
    }
}
