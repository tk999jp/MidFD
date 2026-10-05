using MidFD.Dialogs;
using MidFD.Models;

namespace MidFD.Runtime;

internal sealed class MainFormIntegrationSeam
{
    internal MainFormIntegrationSeam(
        IFileOperationExecutionProbe? executionProbe = null,
        IMainFormIntegrationObserver? observer = null,
        IMainFormDialogOverride? dialogOverride = null,
        string? startupPathOverride = null,
        uint syncDropWaitTimeoutMilliseconds = uint.MaxValue,
        string? initialStatusText = null,
        string? markSlotClipboardTextOverride = null,
        Action<MarkSlotDialog>? markSlotDialogShown = null,
        Action<MarkSlotClipboardActionResult>? markSlotImportResultDialogOverride = null,
        Func<string, bool>? browserKeyCommandOverride = null,
        MidFD.Services.IContentSearchEngine? contentSearchEngine = null)
    {
        ExecutionProbe = executionProbe;
        Observer = observer;
        DialogOverride = dialogOverride;
        StartupPathOverride = startupPathOverride;
        SyncDropWaitTimeoutMilliseconds = syncDropWaitTimeoutMilliseconds;
        InitialStatusText = initialStatusText;
        MarkSlotClipboardTextOverride = markSlotClipboardTextOverride;
        MarkSlotDialogShown = markSlotDialogShown;
        MarkSlotImportResultDialogOverride = markSlotImportResultDialogOverride;
        BrowserKeyCommandOverride = browserKeyCommandOverride;
        ContentSearchEngine = contentSearchEngine;
    }

    internal IFileOperationExecutionProbe? ExecutionProbe { get; }
    internal IMainFormIntegrationObserver? Observer { get; }
    internal IMainFormDialogOverride? DialogOverride { get; }
    internal string? StartupPathOverride { get; }
    internal uint SyncDropWaitTimeoutMilliseconds { get; }
    internal string? InitialStatusText { get; }
    internal string? MarkSlotClipboardTextOverride { get; }
    internal Action<MarkSlotDialog>? MarkSlotDialogShown { get; }
    internal Action<MarkSlotClipboardActionResult>? MarkSlotImportResultDialogOverride { get; }
    internal Func<string, bool>? BrowserKeyCommandOverride { get; }
    internal MidFD.Services.IContentSearchEngine? ContentSearchEngine { get; }
}

internal sealed record MainFormMarkProjectionSnapshot(
    IReadOnlyList<string> MarkedPaths,
    string HeaderText,
    string StatusText,
    string MarkSizeText);

internal interface IFileOperationExecutionProbe
{
    void OnWorkerStarted(FileOperationCommandKind kind);
    void WaitForWorkerRelease(FileOperationCommandKind kind, CancellationToken cancellationToken);
    void OnWorkerCompleted(FileOperationCommandKind kind);
}

internal interface IMainFormIntegrationObserver
{
    void OnCloseCancellationRequested();
    void OnExternalDropCompletion();
    void OnOutlookAttachmentDropRouted();
    void OnOperationStarted(string operationName, int totalCount);
    void OnProgress(FileOperationProgress progress);
    void OnOperationCompleted(string message);
    void OnOperationError(string operationName, string targetName, string detail);
    void OnUnexpectedOperationError(string operationName, Exception exception);
    void OnPostOperationApplied();
    void OnOperationUiRefreshed();
    void OnMarkProjectionUpdated(MainFormMarkProjectionSnapshot snapshot);
    void OnBrowserMarkGlyphsPainted(IReadOnlyList<string> markedPaths);
}

internal interface IMainFormDialogOverride
{
    bool TryChooseCollision(
        string sourcePath,
        string destinationPath,
        bool allowRename,
        bool isMove,
        out FileOperationCollisionAnswer answer);

    bool TryChooseDirectoryMerge(
        string sourcePath,
        string destinationPath,
        bool isMove,
        out FileOperationDirectoryMergeAnswer answer);

    bool TryHandleOperationError(string operationName, string targetName, string detail);
    bool TryHandleUnexpectedOperationError(string operationName, Exception exception);
}
