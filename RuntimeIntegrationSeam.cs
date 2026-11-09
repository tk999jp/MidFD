using MidFD.Commands;
using MidFD.Dialogs;
using MidFD.Models;
using System.Windows.Forms;

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
        MidFD.Services.IContentSearchEngine? contentSearchEngine = null,
        Func<string, string, bool?>? unifiedSearchResultActivationCommandOverride = null,
        Func<string, bool>? clipboardTextWriter = null,
        Action<IWin32Window?, string>? archiveCopyErrorDialogHandler = null,
        Func<IWin32Window?, string, string, string, string?>? workspaceSnapshotSaveNameDialogHandler = null,
        Action<IWin32Window?, string, string>? workspaceSnapshotSaveNameValidationNoticeHandler = null,
        Func<string, WorkspaceSnapshotCommandContext, bool?>? workspaceSnapshotCommandOverride = null,
        Func<string, CommandExecutionContext, bool?>? browserItemContextCommandOverride = null,
        IExternalDropImportExecutionPort? externalDropImportExecutionPort = null,
        bool suppressStartupMaintenance = false,
        string? imageViewerExecutablePathOverride = null,
        Action<System.Diagnostics.ProcessStartInfo>? imageViewerProcessStartInfoOverride = null,
        Func<int, bool>? allowForegroundWindowOverride = null,
        Action<MidFD.ViewerIpc.ViewerIpcMessage>? imageViewerOutgoingMessageObserver = null,
        Action<Guid>? imageViewerSessionCreated = null,
        Func<System.Diagnostics.ProcessStartInfo, string?>? configuredEditorSearchHitLaunchOverride = null,
        Func<System.Diagnostics.ProcessStartInfo, string?>? configuredEditorLaunchOverride = null,
        Action<string, string, MessageBoxIcon>? markSlotMessageDialogOverride = null,
        Func<bool>? mainFormForegroundForImageViewerRaiseOverride = null)
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
        UnifiedSearchResultActivationCommandOverride = unifiedSearchResultActivationCommandOverride;
        ClipboardTextWriter = clipboardTextWriter;
        ArchiveCopyErrorDialogHandler = archiveCopyErrorDialogHandler;
        WorkspaceSnapshotSaveNameDialogHandler = workspaceSnapshotSaveNameDialogHandler;
        WorkspaceSnapshotSaveNameValidationNoticeHandler = workspaceSnapshotSaveNameValidationNoticeHandler;
        WorkspaceSnapshotCommandOverride = workspaceSnapshotCommandOverride;
        BrowserItemContextCommandOverride = browserItemContextCommandOverride;
        ExternalDropImportExecutionPort = externalDropImportExecutionPort;
        SuppressStartupMaintenance = suppressStartupMaintenance;
        ImageViewerExecutablePathOverride = imageViewerExecutablePathOverride;
        ImageViewerProcessStartInfoOverride = imageViewerProcessStartInfoOverride;
        AllowForegroundWindowOverride = allowForegroundWindowOverride;
        ImageViewerOutgoingMessageObserver = imageViewerOutgoingMessageObserver;
        ImageViewerSessionCreated = imageViewerSessionCreated;
        ConfiguredEditorSearchHitLaunchOverride = configuredEditorSearchHitLaunchOverride;
        ConfiguredEditorLaunchOverride = configuredEditorLaunchOverride;
        MarkSlotMessageDialogOverride = markSlotMessageDialogOverride;
        MainFormForegroundForImageViewerRaiseOverride = mainFormForegroundForImageViewerRaiseOverride;
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
    internal Func<string, string, bool?>? UnifiedSearchResultActivationCommandOverride { get; }
    internal Func<string, bool>? ClipboardTextWriter { get; }
    internal Action<IWin32Window?, string>? ArchiveCopyErrorDialogHandler { get; }
    internal Func<IWin32Window?, string, string, string, string?>? WorkspaceSnapshotSaveNameDialogHandler { get; }
    internal Action<IWin32Window?, string, string>? WorkspaceSnapshotSaveNameValidationNoticeHandler { get; }
    internal Func<string, WorkspaceSnapshotCommandContext, bool?>? WorkspaceSnapshotCommandOverride { get; }
    internal Func<string, CommandExecutionContext, bool?>? BrowserItemContextCommandOverride { get; }
    internal IExternalDropImportExecutionPort? ExternalDropImportExecutionPort { get; }
    internal bool SuppressStartupMaintenance { get; }
    internal string? ImageViewerExecutablePathOverride { get; }
    internal Action<System.Diagnostics.ProcessStartInfo>? ImageViewerProcessStartInfoOverride { get; }
    internal Func<int, bool>? AllowForegroundWindowOverride { get; }
    internal Action<MidFD.ViewerIpc.ViewerIpcMessage>? ImageViewerOutgoingMessageObserver { get; }
    internal Action<Guid>? ImageViewerSessionCreated { get; }
    internal Func<System.Diagnostics.ProcessStartInfo, string?>? ConfiguredEditorSearchHitLaunchOverride { get; }
    internal Func<System.Diagnostics.ProcessStartInfo, string?>? ConfiguredEditorLaunchOverride { get; }
    internal Action<string, string, MessageBoxIcon>? MarkSlotMessageDialogOverride { get; }
    internal Func<bool>? MainFormForegroundForImageViewerRaiseOverride { get; }
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
    void OnCommandDispatched(string commandId);
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
