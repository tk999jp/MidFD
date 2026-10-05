using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.FileOperationHelperProtocol;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;
using MidFD.Services.TrashManifestStore;

namespace MidFD.Runtime;

internal enum FileOperationCommandKind
{
    Copy,
    Move,
    Rename,
    Delete,
    Paste,
    Cut,
    Pack,
    Unpack,
    Undo,
    Redo,
    CreateDirectory,
    CreateFile,
    ChangeAttributes,
    ClipboardCopy,
    ArchiveHash,
    EmptyManagedTrash
}

internal enum FileOperationMultiMarkSelectionAction
{
    CurrentOnly,
    MarkedAll,
    Cancel
}

internal readonly record struct FileOperationCommandRequest(
    FileOperationCommandKind Kind,
    SelectionResult? SelectionSnapshot = null,
    bool PermanentDelete = false,
    string? DestinationDirectory = null,
    bool ForcePackEachFolderIndividually = false,
    SevenZipHashAlgorithm? HashAlgorithm = null,
    FileOperationPackRequest? PackRequestOverride = null,
    Action? CompletionCallback = null,
    bool RecordDragCopyUndo = false,
    string? CreateFileExtension = null,
    FileOperationOriginSnapshot? Origin = null);

internal enum FileOperationCollisionPolicy
{
    Cancel,
    Skip,
    Overwrite,
    RenameCopy,
    NewerOnly
}

internal enum FileOperationDirectoryMergePolicy
{
    Cancel,
    Skip,
    Merge
}

internal enum FileOperationSameDirectoryCopyDecision
{
    Cancel,
    Skip,
    CopyWithUniqueName,
    CopyWithUniqueNameForAll
}

internal enum FileOperationCollisionAnswer
{
    Cancel,
    Skip,
    Overwrite,
    RenameCopy,
    NewerOnly
}

internal enum FileOperationDirectoryMergeAnswer
{
    Cancel,
    Skip,
    Merge
}

internal enum FileOperationSameDirectoryCopyAnswer
{
    Cancel,
    Skip,
    CopyWithUniqueName,
    CopyWithUniqueNameForAll
}

/// <summary>Paste専用collision応答。ApplyToAll情報を付加して旧PasteCollisionDialogの契約を復元する。</summary>
internal readonly record struct FileOperationPasteCollisionAnswer(
    FileOperationCollisionAnswer Policy,
    bool ApplyToAll);

/// <summary>Paste専用directory merge応答。ApplyToAll情報を付加して旧PasteFolderMergeDialogの契約を復元する。</summary>
internal readonly record struct FileOperationPasteDirectoryMergeAnswer(
    FileOperationDirectoryMergeAnswer Policy,
    bool ApplyToAll);

internal readonly record struct FileOperationDestinationSelection(
    string Directory,
    bool NeedsCreateDirectory);

internal readonly record struct FileOperationDestinationDialogOptions(
    string OperationName,
    string CurrentPath,
    string SummaryText,
    string? WarningText,
    IReadOnlyList<string>? DirectoryHistory);

internal enum FileOperationPackIndividualTargetAnswer
{
    Cancel,
    IncludeFiles,
    FoldersOnly
}

internal enum FileOperationPackExistingArchiveAnswer
{
    Add,
    Overwrite,
    Cancel
}

internal enum FileOperationPackExistingArchiveAction
{
    Add,
    Overwrite
}

internal readonly record struct FileOperationPackRequest(
    PackRequest? Request,
    IReadOnlyList<string>? SourcePaths = null,
    string? NativeGuiExecutablePath = null,
    string? NativeArchivePath = null,
    string? RouteError = null,
    FileOperationPackExistingArchiveAction ExistingArchiveAction = FileOperationPackExistingArchiveAction.Add)
{
    public bool IsNative => !string.IsNullOrWhiteSpace(NativeGuiExecutablePath);
}

internal readonly record struct FileOperationPackDialogOptions(
    string InitialDirectory,
    string DefaultArchiveName,
    string TargetSummary,
    bool CanPackEachFolderIndividually,
    bool DefaultPackEachFolderIndividually,
    IReadOnlyList<PackArchiveFormat> AvailableFormats,
    string HintText);

internal readonly record struct FileOperationPackDialogResult(
    string OutputArchivePath,
    PackArchiveFormat Format,
    PackCompressionLevel CompressionLevel,
    string? SplitSize,
    bool PackEachFolderIndividually,
    FileOperationPackExistingArchiveAction ExistingArchiveAction);

internal readonly record struct FileOperationRenamePlan(
    bool Canceled,
    IReadOnlyList<RenamePreviewItem> Items);

internal enum FileOperationRenameEntryMode
{
    Cancel,
    SingleStep,
    Bulk
}

internal readonly record struct FileOperationRenameEntryResult(
    bool Confirmed,
    FileOperationRenameEntryMode Mode,
    string SingleStepInitialName);

internal readonly record struct FileOperationRenameSingleResult(
    bool WasCanceled,
    bool WillRename,
    RenamePreviewItem? PreviewItem);

internal readonly record struct FileOperationRenameBatchResult(
    bool Confirmed,
    IReadOnlyList<RenamePreviewItem> Items,
    bool RememberTemplate,
    string LastTemplateCandidate);

internal readonly record struct FileOperationClipboardTransfer(
    IReadOnlyList<string> Paths,
    bool IsCut);

internal readonly record struct FileOperationSelectionContext(
    string? CurrentPath,
    bool IsParentEntry,
    string? CurrentName = null);

internal readonly record struct FileOperationOriginSnapshot(
    string CategoryId,
    Guid TabId,
    string CurrentPath,
    bool IsReadOnly,
    SelectionResult Selection,
    IReadOnlyList<string> MarkedPaths,
    FileOperationSelectionContext SelectionContext);

internal readonly record struct FileOperationPostOperationApplicationResult(
    FileOperationPostOperationCoordinator.PostOperationPlan Plan,
    BrowserDirectoryNavigationExecution? BrowserReload);

internal readonly record struct FileOperationAttributeOptions(
    FileOperationAttributeChangeAction ReadOnlyAction,
    FileOperationAttributeChangeAction HiddenAction,
    FileOperationAttributeChangeAction SystemAction,
    FileOperationAttributeChangeAction ArchiveAction,
    bool ChangeLastWriteTime,
    DateTime LastWriteTime,
    bool ChangeCreationTime,
    DateTime CreationTime,
    bool ChangeLastAccessTime,
    DateTime LastAccessTime,
    bool IncludeSubdirectories);

internal enum FileOperationAttributeChangeAction
{
    Preserve,
    Set,
    Clear
}

internal enum FileOperationAttributeAggregateState
{
    AllClear,
    AllSet,
    Mixed
}

internal readonly record struct FileOperationAttributeDialogOptions(
    string TargetLabel,
    FileOperationAttributeAggregateState ReadOnlyState,
    FileOperationAttributeAggregateState HiddenState,
    FileOperationAttributeAggregateState SystemState,
    FileOperationAttributeAggregateState ArchiveState,
    DateTime InitialLastWriteTime,
    DateTime InitialCreationTime,
    DateTime InitialLastAccessTime);

internal readonly record struct FileOperationAttributeDialogResult(
    FileOperationAttributeChangeAction ReadOnlyAction,
    FileOperationAttributeChangeAction HiddenAction,
    FileOperationAttributeChangeAction SystemAction,
    FileOperationAttributeChangeAction ArchiveAction,
    bool ChangeLastWriteTime,
    DateTime LastWriteTime,
    bool ChangeCreationTime,
    DateTime CreationTime,
    bool ChangeLastAccessTime,
    DateTime LastAccessTime,
    bool IncludeSubdirectories);

internal readonly record struct ManagedTrashOperationFailure(
    string ItemName,
    string Detail);

internal readonly record struct ManagedTrashOperationResult(
    FileOpExitStatus ExitStatus,
    int SuccessCount,
    int TotalCount,
    IReadOnlyList<ManagedTrashOperationFailure> Failures,
    string? ErrorMessage = null);

internal interface IFileOperationUiPort
{
    FileOperationSelectionContext ReadSelectionContext();
    FileOperationMultiMarkSelectionAction ChooseMultiMarkSelection(string operationName, string currentName, int markedCount);
    BrowserPostOperationReloadContext CapturePostOperationReloadContext();
    string? RequestDestinationDirectory(FileOperationDestinationDialogOptions options);
    void ShowDestinationPathError(string message);
    bool ConfirmCreateDirectory(string destinationDirectory);
    bool ConfirmDelete(SelectionResult selection, bool permanentDelete);
    bool ConfirmEmptyManagedTrash();
    FileOperationRenameEntryResult RequestRenameEntry(IReadOnlyList<string> sourcePaths);
    FileOperationRenameSingleResult RequestRenameSingle(
        string sourcePath,
        string? initialValue,
        bool skipInitialPrompt,
        bool showValidationMessage);
    FileOperationRenameBatchResult RequestRenameBatch(
        IReadOnlyList<string> sourcePaths,
        string initialTemplate,
        bool rememberTemplate);
    FileOperationPackDialogResult? RequestPackDialog(FileOperationPackDialogOptions options);
    FileOperationPackIndividualTargetAnswer ChoosePackIndividualTargets(int folderCount, int fileCount);
    FileOperationPackExistingArchiveAnswer ChoosePackExistingArchive(string archivePath);
    ArchiveExtractDestinationOptions? RequestUnpackDestination(
        string currentPath,
        string archiveDisplayName);
    string? RequestNewItemName(bool directory, string? fileExtension);
    FileOperationAttributeDialogResult? RequestAttributeChangeDialog(
        FileOperationAttributeDialogOptions options);
    FileOperationCollisionAnswer ChooseCollision(
        string sourcePath,
        string destinationPath,
        bool allowRename,
        bool isMove,
        ref CopyCollisionDecision? applyToAllDecision);
    FileOperationDirectoryMergeAnswer ChooseDirectoryMerge(
        string sourcePath,
        string destinationPath,
        bool isMove,
        ref DirectoryMergeDecision? applyToAllDecision);
    FileOperationSameDirectoryCopyAnswer ChooseSameDirectoryCopy(string sourcePath, string suggestedDestinationPath, bool showApplyToAll);
    LinkOperationDecision ChooseLinkOperation(LinkOperationPlan plan);
    bool ConfirmBulkPasteMove(IReadOnlyList<string> sourcePaths, string destinationDirectory);
    /// <summary>Paste専用collision dialog（PasteCollisionDialog）を表示する。isCut文言とApplyToAllを持つ。</summary>
    FileOperationPasteCollisionAnswer ChoosePasteCollision(string sourcePath, string destinationPath, bool allowRename, bool isCut, ref CopyCollisionDecision? applyToAllDecision);
    /// <summary>Paste専用directory merge dialog（PasteFolderMergeDialog）を表示する。isCut文言とApplyToAllを持つ。</summary>
    FileOperationPasteDirectoryMergeAnswer ChoosePasteDirectoryMerge(string sourcePath, string destinationPath, bool isCut, ref DirectoryMergeDecision? applyToAllDecision);
    FileOperationClipboardTransfer? ReadClipboardFileTransfer();
    void SetClipboardFileTransfer(IReadOnlyList<string> paths, bool isCut);
    bool TryGetClipboardImage(out System.Drawing.Image? image);
    bool TryGetClipboardText(out string? text);
    ClipboardPasteChoice ChooseClipboardPasteMode();
    DeleteCancelResolution ShowDeleteCancelResolution(int successCount, int pendingCount, int failedCount);
    void ShowTypeMismatchConflict(string conflictPath);

    void ShowStatus(string message);
    void ShowError(string operationName, string targetName, string detail);
    void ShowUnexpectedError(string operationName, Exception exception);
    void ShowHashResult(string targetSummary, SevenZipHashAlgorithm algorithm, string output);
    void BeginOperation(string operationName, int totalCount);
    void ReportProgress(FileOperationProgress progress);
    void CompleteOperation(string message);
    void ClearOperationProgress();
    void ApplyPostOperation(FileOperationPostOperationApplicationResult result);
    void RefreshOperationUi();
}

internal interface IManagedTrashDeleteProgressHook
{
    void OnProcessed(int processedCount);
}

internal interface IManagedTrashDeleteDiagnosticHook
{
    void OnStage(string stage, int successCount, int processedCount, bool cancellationRequested);
}

internal interface IFileOperationPackExecutionPort
{
    (int ExitCode, string Output, string Error) Pack(
        AppSettings settings,
        PackRequest request,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken);
}

/// <summary>
/// FileOperation の mutation と結果分類を所有する application coordinator。
/// UI adapterは <see cref="IFileOperationUiPort" /> 経由でのみ利用する。
/// </summary>
internal sealed class FileOperationApplicationCoordinator : IDisposable
{
    private const int ManagedTrashManifestCheckpointRecordCount = 1000;
    private readonly FileOperationState _fileState;
    private readonly BrowserSelectionApplicationCoordinator _selection;
    private readonly SettingsApplicationCoordinator _settings;
    private readonly FileOperationUndoRedoService _undoRedo;
    private readonly FileOperationPostOperationCoordinator _postOperation = new();
    private readonly BrowserApplicationCoordinator _browser;
    private readonly BrowserNavigationWorkflowApplicationCoordinator _browserNavigation;
    private readonly ViewerSessionState _viewerState;
    private readonly IFileOperationExecutionProbe? _executionProbe;
    private readonly IManagedTrashDeleteProgressHook? _managedTrashDeleteProgressHook;
    private readonly IManagedTrashDeleteDiagnosticHook? _managedTrashDeleteDiagnosticHook;
    private readonly ManagedTrashRestoreDiagnostics? _managedTrashRestoreDiagnostics;
    private readonly ILinkOperationCopyPort _linkCopyPort;
    private readonly IFileOperationPackExecutionPort _packExecutionPort;
    private ManagedTrashDeleteCancellationDiagnostics? _activeManagedTrashDeleteDiagnostics;
    private FileOperationOriginSnapshot? _activeOperationOrigin;

    private readonly record struct ManagedTrashDeleteCancellationSnapshot(
        int SuccessCount,
        int FailureCount,
        int PendingCount,
        int ProcessedCount,
        int ManifestRecordCount,
        int PendingManifestRecordCount,
        bool CancelRequested);

    private sealed class ManagedTrashDeleteCancellationDiagnostics
    {
        private readonly string _operationId;
        private readonly object _sync = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly IManagedTrashDeleteDiagnosticHook? _diagnosticHook;
        private ManagedTrashDeleteCancellationSnapshot _snapshot;

        public ManagedTrashDeleteCancellationDiagnostics(
            string operationId,
            IManagedTrashDeleteDiagnosticHook? diagnosticHook)
        {
            _operationId = operationId;
            _diagnosticHook = diagnosticHook;
        }

        public void UpdateSnapshot(ManagedTrashDeleteCancellationSnapshot snapshot)
        {
            lock (_sync)
            {
                _snapshot = snapshot;
            }
        }

        public void UpdateCancellationRequested(bool cancellationRequested)
        {
            lock (_sync)
            {
                _snapshot = _snapshot with { CancelRequested = cancellationRequested };
            }
        }

        public void Record(string stage)
        {
            try
            {
                ManagedTrashDeleteCancellationSnapshot snapshot;
                lock (_sync)
                {
                    snapshot = _snapshot;
                }
                LogService.Info(
                    $"[DeleteCancelRuntime] stage={stage}, operationId={_operationId}, " +
                    $"elapsedMs={_clock.ElapsedMilliseconds}, threadId={Environment.CurrentManagedThreadId}, " +
                    $"successCount={snapshot.SuccessCount}, failureCount={snapshot.FailureCount}, " +
                    $"pendingCount={snapshot.PendingCount}, processedCount={snapshot.ProcessedCount}, " +
                    $"manifestRecordCount={snapshot.ManifestRecordCount}, " +
                    $"pendingManifestRecordCount={snapshot.PendingManifestRecordCount}, " +
                    $"cancelRequested={snapshot.CancelRequested}");
                _diagnosticHook?.OnStage(
                    stage,
                    snapshot.SuccessCount,
                    snapshot.ProcessedCount,
                    snapshot.CancelRequested);
            }
            catch
            {
                // Diagnostic logging must not change the cancellation path.
            }
        }
    }

    public FileOperationApplicationCoordinator(
        FileOperationState fileState,
        BrowserSelectionApplicationCoordinator selection,
        SettingsApplicationCoordinator settings,
        FileOperationUndoRedoService undoRedo,
        BrowserApplicationCoordinator? browser = null,
        ViewerSessionState? viewerState = null,
        BrowserNavigationWorkflowApplicationCoordinator? browserNavigation = null,
        IManagedTrashDeleteProgressHook? managedTrashDeleteProgressHook = null,
        IManagedTrashDeleteDiagnosticHook? managedTrashDeleteDiagnosticHook = null,
        ManagedTrashRestoreDiagnostics? managedTrashRestoreDiagnostics = null,
        IFileOperationExecutionProbe? executionProbe = null,
        ILinkOperationCopyPort? linkCopyPort = null,
        IFileOperationPackExecutionPort? packExecutionPort = null)
    {
        _fileState = fileState;
        _selection = selection;
        _settings = settings;
        _undoRedo = undoRedo;
        _browser = browser ?? new BrowserApplicationCoordinator();
        _browserNavigation = browserNavigation ?? new BrowserNavigationWorkflowApplicationCoordinator(_browser);
        _viewerState = viewerState ?? new ViewerSessionState();
        _managedTrashDeleteProgressHook = managedTrashDeleteProgressHook;
        _managedTrashDeleteDiagnosticHook = managedTrashDeleteDiagnosticHook;
        _managedTrashRestoreDiagnostics = managedTrashRestoreDiagnostics;
        _executionProbe = executionProbe;
        _linkCopyPort = linkCopyPort ?? new ElevatedLinkCopyClient();
        _packExecutionPort = packExecutionPort ?? new DefaultPackExecutionPort();
    }

    public bool IsBusy => _fileState.IsBusy;
    public bool IsClipboardBusy => _fileState.IsClipboardBusy;
    public CancellationTokenSource? CancellationTokenSource => _fileState.Ui.Cts;
    public bool CanCancel => _fileState.Ui.CanCancel;
    public bool IsCancellationRequested => _fileState.Ui.Cts?.IsCancellationRequested ?? false;
    public string? ActiveOperationName => _fileState.Ui.ActiveOperationName;
    public int StatusVersion => _fileState.Ui.StatusVersion;

    public void SetClipboardBusy(bool value) => _fileState.IsClipboardBusy = value;

    public bool RequestCancellation()
    {
        CancellationTokenSource? cts = _fileState.Ui.Cts;
        if (cts == null)
        {
            return false;
        }

        if (!_fileState.Ui.CanCancel)
        {
            return false;
        }

        if (!cts.IsCancellationRequested)
        {
            _fileState.Ui.CancelRequestedTimestamp = Stopwatch.GetTimestamp();
            _activeManagedTrashDeleteDiagnostics?.Record("CancelRequestEntered");
            cts.Cancel();
            _activeManagedTrashDeleteDiagnostics?.UpdateCancellationRequested(cts.IsCancellationRequested);
            _activeManagedTrashDeleteDiagnostics?.Record("CancelSignalCompleted");
            _activeManagedTrashDeleteDiagnostics?.Record("CancelRequested");
        }
        return true;
    }

    public void Dispose() => _fileState.Dispose();

    public bool TryStart(FileOperationCommandRequest request, IFileOperationUiPort ui)
    {
        if (_viewerState.Mode != ViewerApplicationMode.Browser || !CommandBusyPolicy.CanStartFileOperation(_fileState.IsBusy))
        {
            return false;
        }

        if (IsReadOnlyBlocked(request.Kind))
        {
            ui.ShowStatus(BuildReadOnlyBlockedMessage(request.Kind));
            return false;
        }

        request = CaptureOperationOrigin(request, ui);
        _activeOperationOrigin = request.Origin;
        _ = ExecuteAsync(request, ui);
        return true;
    }

    internal static bool IsBlockedByReadOnly(FileOperationCommandKind kind)
        => kind is
            FileOperationCommandKind.Copy or
            FileOperationCommandKind.Move or
            FileOperationCommandKind.Rename or
            FileOperationCommandKind.Delete or
            FileOperationCommandKind.Paste or
            FileOperationCommandKind.Cut or
            FileOperationCommandKind.Pack or
            FileOperationCommandKind.Unpack or
            FileOperationCommandKind.Undo or
            FileOperationCommandKind.Redo or
            FileOperationCommandKind.CreateDirectory or
            FileOperationCommandKind.CreateFile or
            FileOperationCommandKind.ChangeAttributes;

    internal bool IsReadOnlyBlocked(FileOperationCommandKind kind)
        => (_activeOperationOrigin?.IsReadOnly ?? _browser.Workspace.ActiveTabSnapshot?.IsReadOnly == true) && IsBlockedByReadOnly(kind);

    private static string BuildReadOnlyBlockedMessage(FileOperationCommandKind kind)
    {
        string operation = kind switch
        {
            FileOperationCommandKind.Copy => "コピー",
            FileOperationCommandKind.Move => "移動",
            FileOperationCommandKind.Rename => "リネーム",
            FileOperationCommandKind.Delete => "削除",
            FileOperationCommandKind.Paste => "貼り付け",
            FileOperationCommandKind.Cut => "切り取り",
            FileOperationCommandKind.Pack => "圧縮",
            FileOperationCommandKind.Unpack => "解凍",
            FileOperationCommandKind.Undo => "元に戻す",
            FileOperationCommandKind.Redo => "やり直し",
            FileOperationCommandKind.CreateDirectory => "フォルダ作成",
            FileOperationCommandKind.CreateFile => "ファイル作成",
            FileOperationCommandKind.ChangeAttributes => "属性変更",
            _ => kind.ToString()
        };
        return $"このタブは ReadOnly のため、{operation}は実行できません。";
    }

    public bool TryStart(
        FileOperationCommandKind kind,
        IFileOperationUiPort ui,
        SelectionResult? selectionSnapshot = null,
        bool permanentDelete = false,
        string? destinationDirectory = null,
        bool forcePackEachFolderIndividually = false,
        SevenZipHashAlgorithm? hashAlgorithm = null,
        FileOperationPackRequest? packRequestOverride = null,
        Action? completionCallback = null,
        bool recordDragCopyUndo = false)
        => TryStart(
            new FileOperationCommandRequest(
                kind,
                selectionSnapshot,
                permanentDelete,
                destinationDirectory,
                forcePackEachFolderIndividually,
                hashAlgorithm,
                packRequestOverride,
                completionCallback,
                recordDragCopyUndo),
            ui);

    public void ApplyOperationResult(FileOperationResult result, IFileOperationUiPort ui)
    {
        FileOperationPostOperationCoordinator.PostOperationPlan plan = _postOperation.CreatePlan(
            result,
            _settings.Value.FileOperations?.ReloadAfterFileOperation ?? true,
            CurrentOperationPath);
        ui.ClearOperationProgress();
        if (plan.ShouldClearMarks)
        {
            ClearMarksAfterFileOperation();
        }
        ApplyPostOperationPlan(plan, ui);
    }

    private void ApplyPostOperationPlan(
        FileOperationPostOperationCoordinator.PostOperationPlan plan,
        IFileOperationUiPort ui)
    {
        if (!IsOperationOriginCurrentView())
        {
            return;
        }

        BrowserDirectoryNavigationExecution? browserReload = null;
        if (plan.ShouldReloadCurrentDirectory)
        {
            BrowserPostOperationReloadContext context = ui.CapturePostOperationReloadContext();
            browserReload = _browserNavigation.ExecuteCurrentDirectoryReload(
                plan.NextFocusTarget,
                context);
        }
        ui.ApplyPostOperation(new FileOperationPostOperationApplicationResult(plan, browserReload));
    }

    public FileOperationPackRequest? BuildPackRequest(
        SelectionResult selection,
        bool forcePackEachFolderIndividually,
        string currentPath,
        IFileOperationUiPort ui)
    {
        if (selection.Count == 0)
        {
            ui.ShowStatus("圧縮(Pack)対象がありません。");
            return null;
        }

        string defaultName = BuildPackDefaultArchiveName(selection, currentPath);
        string? sevenZipExecutable = SevenZipService.ResolveExecutable(_settings.Value.SevenZip?.ExePath);
        string? guiExecutable = string.IsNullOrWhiteSpace(sevenZipExecutable)
            ? null
            : SevenZipService.ResolveGuiExecutable(sevenZipExecutable);
        string nativeArchivePath = Path.Combine(currentPath, Path.GetFileName(defaultName));
        PackDialogRouteDecision route = PackDialogRoutingService.Resolve(
            forcePackEachFolderIndividually
                ? PackDialogMode.MidFd
                : _settings.Value.SevenZip?.PackDialogMode ?? PackDialogMode.Auto,
            guiExecutable,
            selection.FullPaths,
            nativeArchivePath);
        if (!string.IsNullOrWhiteSpace(route.ErrorMessage))
        {
            return new FileOperationPackRequest(null, selection.FullPaths, RouteError: route.ErrorMessage);
        }

        if (route.IsNative)
        {
            return new FileOperationPackRequest(
                null,
                selection.FullPaths,
                guiExecutable,
                nativeArchivePath);
        }

        var formats = new List<PackArchiveFormat> { PackArchiveFormat.Zip };
        if (!string.IsNullOrWhiteSpace(sevenZipExecutable))
        {
            formats.AddRange([PackArchiveFormat.SevenZip, PackArchiveFormat.Tar, PackArchiveFormat.Wim]);
            if (selection.Count == 1 && File.Exists(selection.FullPaths[0]))
            {
                formats.AddRange([PackArchiveFormat.GZip, PackArchiveFormat.BZip2, PackArchiveFormat.Xz]);
            }
        }
        else if (TarFallbackService.IsAvailable())
        {
            formats.AddRange([PackArchiveFormat.SevenZip, PackArchiveFormat.Tar]);
        }

        FileOperationPackDialogResult? values = ui.RequestPackDialog(
            new FileOperationPackDialogOptions(
                currentPath,
                defaultName,
                BuildPackSelectionSummary(selection),
                CanPackEachFolderIndividually(selection),
                forcePackEachFolderIndividually,
                formats,
                "出力先と形式を選択してください。"));
        if (values is not { } result)
        {
            return null;
        }

        return new FileOperationPackRequest(
            new PackRequest
            {
                OutputArchivePath = result.OutputArchivePath,
                Format = result.Format,
                CompressionLevel = result.CompressionLevel,
                SplitSize = result.SplitSize,
                PackEachFolderIndividually = result.PackEachFolderIndividually
            },
            selection.FullPaths,
            ExistingArchiveAction: result.ExistingArchiveAction);
    }

    private bool TryBuildDestinationSelection(
        string operationName,
        SelectionResult selection,
        IFileOperationUiPort ui,
        out FileOperationDestinationSelection destination)
    {
        destination = default;
        string operationPath = CurrentOperationPath;
        string? input = ui.RequestDestinationDirectory(new FileOperationDestinationDialogOptions(
            operationName,
            operationPath,
            BuildSelectionSummaryText(selection),
            BuildSelectionOutsideCurrentDirectoryWarning(selection, operationPath),
            _settings.GetDirectoryMoveHistory()));
        if (string.IsNullOrWhiteSpace(input))
        {
            ui.ShowStatus($"{operationName}はキャンセルされました。");
            return false;
        }

        string normalized = _browser.NormalizeDestinationDirectory(input);
        string? validationError = FileOperationPresentationHelper.GetDestinationPathErrorMessage(
            input,
            operationPath,
            normalized,
            operationName);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            ui.ShowDestinationPathError(validationError);
            return false;
        }

        destination = new FileOperationDestinationSelection(
            normalized,
            NeedsCreateDirectory: !Directory.Exists(normalized));
        return true;
    }

    private static string BuildSelectionSummaryText(SelectionResult selection)
    {
        string firstName = selection.FirstFileName ?? "(不明)";
        return $"{selection.Count} 件の対象が選択されています。{Environment.NewLine}先頭項目: {firstName}";
    }

    private static string? BuildSelectionOutsideCurrentDirectoryWarning(
        SelectionResult selection,
        string currentPath)
    {
        if (selection.Count == 0 || string.IsNullOrWhiteSpace(currentPath))
        {
            return null;
        }

        string currentDirectory = NavigationService.NormalizeDirectoryForCompare(currentPath);
        int outsideCount = selection.FullPaths.Count(path =>
            !string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                currentDirectory,
                StringComparison.OrdinalIgnoreCase));
        return outsideCount > 0
            ? $"警告: 現在のディレクトリ外の項目を {outsideCount} 件含みます。"
            : null;
    }

    private SelectionResult? ResolvePackIndividualTargets(SelectionResult selection, IFileOperationUiPort ui)
    {
        var folders = selection.FullPaths.Where(Directory.Exists).ToList();
        var files = selection.FullPaths.Where(File.Exists).ToList();
        if (folders.Count == 0 || files.Count == 0)
        {
            return selection;
        }

        return ui.ChoosePackIndividualTargets(folders.Count, files.Count) switch
        {
            FileOperationPackIndividualTargetAnswer.IncludeFiles => selection,
            FileOperationPackIndividualTargetAnswer.FoldersOnly => new SelectionResult(folders, selection.HasMarkedSelection),
            _ => null
        };
    }

    public IReadOnlyList<ArchiveExtractRequest>? BuildUnpackRequests(
        SelectionResult selection,
        string currentPath,
        IFileOperationUiPort ui)
    {
        List<string> archivePaths = selection.FullPaths
            .Where(path => File.Exists(path) && ArchiveFileTypeHelper.IsArchive(path))
            .ToList();
        if (archivePaths.Count == 0)
        {
            ui.ShowStatus("解凍(Unpack)可能なアーカイブファイルが選択されていません。");
            return null;
        }

        ArchiveExtractDestinationOptions? destination = ui.RequestUnpackDestination(
            currentPath,
            archivePaths.Count == 1 ? Path.GetFileNameWithoutExtension(archivePaths[0]) : "archive");
        if (destination == null)
        {
            return null;
        }

        return archivePaths
            .Select(archivePath => new ArchiveExtractRequest
            {
                ArchivePath = archivePath,
                DestinationDirectory = ArchiveExtractService.ResolveDestinationDirectory(
                    destination.BaseDirectory,
                    archivePath,
                    destination.CreateArchiveRootDirectory),
                ExtractAll = true,
                EntryPaths = Array.Empty<string>()
            })
            .ToArray();
    }

    public FileOperationRenamePlan BuildRenamePlan(
        SelectionResult selection,
        IFileOperationUiPort ui)
    {
        if (selection.Count == 1)
        {
            FileOperationRenameSingleResult single = ui.RequestRenameSingle(
                selection.FirstPath ?? string.Empty,
                initialValue: null,
                skipInitialPrompt: false,
                showValidationMessage: true);
            return new FileOperationRenamePlan(
                single.WasCanceled,
                single.PreviewItem is { } item && single.WillRename
                    ? new[] { item }
                    : Array.Empty<RenamePreviewItem>());
        }

        FileOperationRenameEntryResult entry = ui.RequestRenameEntry(selection.FullPaths);
        if (!entry.Confirmed || entry.Mode == FileOperationRenameEntryMode.Cancel)
        {
            return new FileOperationRenamePlan(true, Array.Empty<RenamePreviewItem>());
        }

        if (entry.Mode == FileOperationRenameEntryMode.SingleStep)
        {
            var items = new List<RenamePreviewItem>();
            string? initialName = entry.SingleStepInitialName;
            foreach (string path in selection.FullPaths)
            {
                FileOperationRenameSingleResult single = ui.RequestRenameSingle(
                    path,
                    initialName,
                    skipInitialPrompt: initialName != null,
                    showValidationMessage: false);
                if (single.WasCanceled)
                {
                    return new FileOperationRenamePlan(true, items);
                }
                if (single.WillRename && single.PreviewItem is { } item)
                {
                    items.Add(item);
                    initialName = null;
                }
            }
            return new FileOperationRenamePlan(false, items);
        }

        string initialTemplate = _settings.Value.Rename.RememberLastTemplate
            && !string.IsNullOrWhiteSpace(_settings.Value.Rename.LastTemplate)
            ? _settings.Value.Rename.LastTemplate
            : "$F$E";
        FileOperationRenameBatchResult batch = ui.RequestRenameBatch(
            selection.FullPaths,
            initialTemplate,
            _settings.Value.Rename.RememberLastTemplate);
        if (!batch.Confirmed)
        {
            return new FileOperationRenamePlan(true, Array.Empty<RenamePreviewItem>());
        }

        _settings.SetRenameTemplate(batch.RememberTemplate, batch.RememberTemplate ? batch.LastTemplateCandidate : null);
        _settings.Save();
        return new FileOperationRenamePlan(false, batch.Items);
    }

    public FileOperationAttributeOptions? BuildAttributeOptions(
        SelectionResult selection,
        IFileOperationUiPort ui)
    {
        List<string> roots = selection.FullPaths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            ui.ShowStatus("属性変更の対象が見つかりません。");
            return null;
        }

        string firstPath = roots[0];
        DateTime lastWrite = File.GetLastWriteTime(firstPath);
        DateTime creation = File.GetCreationTime(firstPath);
        DateTime access = File.GetLastAccessTime(firstPath);
        FileOperationAttributeDialogResult? result = ui.RequestAttributeChangeDialog(
            new FileOperationAttributeDialogOptions(
                roots.Count == 1 ? Path.GetFileName(firstPath) : $"Mark {roots.Count} 件",
                AggregateAttributeState(roots, FileAttributes.ReadOnly),
                AggregateAttributeState(roots, FileAttributes.Hidden),
                AggregateAttributeState(roots, FileAttributes.System),
                AggregateAttributeState(roots, FileAttributes.Archive),
                lastWrite,
                creation,
                access));
        if (result is not { } values)
        {
            return null;
        }

        return new FileOperationAttributeOptions(
            values.ReadOnlyAction,
            values.HiddenAction,
            values.SystemAction,
            values.ArchiveAction,
            values.ChangeLastWriteTime,
            values.LastWriteTime,
            values.ChangeCreationTime,
            values.CreationTime,
            values.ChangeLastAccessTime,
            values.LastAccessTime,
            values.IncludeSubdirectories);
    }

    private static FileOperationAttributeAggregateState AggregateAttributeState(
        IReadOnlyList<string> paths,
        FileAttributes targetBit)
    {
        bool anySet = false;
        bool anyClear = false;
        foreach (string path in paths)
        {
            try
            {
                if (File.GetAttributes(path).HasFlag(targetBit))
                {
                    anySet = true;
                }
                else
                {
                    anyClear = true;
                }
            }
            catch
            {
            }
            if (anySet && anyClear)
            {
                return FileOperationAttributeAggregateState.Mixed;
            }
        }
        return anySet
            ? FileOperationAttributeAggregateState.AllSet
            : FileOperationAttributeAggregateState.AllClear;
    }

    public static bool IsArchiveTarget(string path)
        => ArchiveFileTypeHelper.IsArchive(path);

    public static bool CanPackEachFolderIndividually(SelectionResult selection)
        => selection.Count >= 1;

    private static string BuildPackSelectionSummary(SelectionResult selection)
    {
        if (selection.HasMarkedSelection)
        {
            int directoryCount = selection.FullPaths.Count(Directory.Exists);
            int fileCount = selection.Count - directoryCount;
            if (fileCount > 0 && directoryCount > 0)
            {
                return $"Mark {selection.Count} 件 / ファイル {fileCount} 件 / フォルダ {directoryCount} 件";
            }
            if (directoryCount > 0)
            {
                return $"Mark {selection.Count} 件 / フォルダ {directoryCount} 件";
            }
            return $"Mark {selection.Count} 件";
        }
        return $"選択中 {selection.FirstFileName ?? "(不明)"}";
    }

    private static string BuildPackDefaultArchiveName(SelectionResult selection, string currentPath)
    {
        if (selection.Count == 1)
        {
            return (selection.FirstFileName ?? "archive") + ".zip";
        }
        string dirName = Path.GetFileName(currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(dirName) ? "archive.zip" : dirName + ".zip";
    }

    public ManagedTrashOperationResult RestoreManagedTrash(IReadOnlyList<ManagedTrashRecordView> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        int successCount = 0;
        var failures = new List<ManagedTrashOperationFailure>();
        foreach (ManagedTrashRecordView view in selected)
        {
            if (!view.CanRestore)
            {
                failures.Add(new ManagedTrashOperationFailure(
                    view.Record.OriginalName,
                    "復元できないrecordが選択されています。状態を確認してください。"));
                continue;
            }

            TrashManifestRecord record = view.Record;
            try
            {
                var item = new FileOperationUndoRedoItem
                {
                    BeforePath = record.OriginalPath,
                    BeforeName = record.OriginalName,
                    RecycleBinPath = record.TrashPath,
                    RecycleBinDeletedAtUtc = record.DeletedAtUtc
                };
                MidFdManagedTrashService.RestoreFromTrash(item, skipStatusUpdate: false);
                successCount++;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[FileOperation] Failed to restore managed trash item. path={record.TrashPath}, error={ex.Message}");
                failures.Add(new ManagedTrashOperationFailure(record.OriginalName, ex.Message));
            }
        }

        return CreateManagedTrashResult(successCount, selected.Count, failures);
    }

    public ManagedTrashOperationResult DeleteManagedTrashForever(IReadOnlyList<ManagedTrashRecordView> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        int successCount = 0;
        var failures = new List<ManagedTrashOperationFailure>();
        foreach (ManagedTrashRecordView view in selected)
        {
            if (!view.CanDeletePhysicalItem)
            {
                failures.Add(new ManagedTrashOperationFailure(
                    view.Record.OriginalName,
                    "完全削除できないrecordが選択されています。状態を確認してください。"));
                continue;
            }

            TrashManifestRecord record = view.Record;
            try
            {
                MidFdManagedTrashService.DeleteFromTrashForever(record.TrashPath, _undoRedo);
                successCount++;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[FileOperation] Failed to delete managed trash item forever. path={record.TrashPath}, error={ex.Message}");
                failures.Add(new ManagedTrashOperationFailure(record.OriginalName, ex.Message));
            }
        }

        return CreateManagedTrashResult(successCount, selected.Count, failures);
    }

    public ManagedTrashOperationResult EmptyManagedTrash()
    {
        try
        {
            MidFdManagedTrashService.EmptyTrash();
            _undoRedo.ClearTrashDeleteBatches();
            return new ManagedTrashOperationResult(
                FileOpExitStatus.Success,
                1,
                1,
                Array.Empty<ManagedTrashOperationFailure>());
        }
        catch (Exception ex)
        {
            LogService.Error("Managed trash empty failed.", ex);
            return new ManagedTrashOperationResult(
                FileOpExitStatus.Error,
                0,
                1,
                Array.Empty<ManagedTrashOperationFailure>(),
                ex.Message);
        }
    }

    public ManagedTrashOperationResult CleanMissingManagedTrash()
    {
        try
        {
            int cleanedCount = MidFdManagedTrashService.CleanMissingTrashRecords(_undoRedo);
            return new ManagedTrashOperationResult(
                FileOpExitStatus.Success,
                cleanedCount,
                cleanedCount,
                Array.Empty<ManagedTrashOperationFailure>());
        }
        catch (Exception ex)
        {
            LogService.Error("Managed trash missing-record cleanup failed.", ex);
            return new ManagedTrashOperationResult(
                FileOpExitStatus.Error,
                0,
                1,
                Array.Empty<ManagedTrashOperationFailure>(),
                ex.Message);
        }
    }

    public Task StartManagedTrashRetentionCleanupAsync(string trigger)
        => MidFdManagedTrashService.RunRetentionCleanupAsync(
            _settings.Value,
            _undoRedo,
            trigger);

    private static ManagedTrashOperationResult CreateManagedTrashResult(
        int successCount,
        int totalCount,
        IReadOnlyList<ManagedTrashOperationFailure> failures)
    {
        FileOpExitStatus status = failures.Count == 0
            ? FileOpExitStatus.Success
            : successCount > 0
                ? FileOpExitStatus.PartialSuccess
                : FileOpExitStatus.Error;
        return new ManagedTrashOperationResult(status, successCount, totalCount, failures);
    }

    private async Task ExecuteAsync(FileOperationCommandRequest request, IFileOperationUiPort ui)
    {
        try
        {
            switch (request.Kind)
            {
                case FileOperationCommandKind.Copy:
                    await ExecuteCopyOrMoveAsync(request.SelectionSnapshot, isMove: false, request.DestinationDirectory, ui, request.RecordDragCopyUndo).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.Move:
                    await ExecuteCopyOrMoveAsync(request.SelectionSnapshot, isMove: true, request.DestinationDirectory, ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.Rename:
                    await ExecuteRenameAsync(request.SelectionSnapshot, ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.Delete:
                    await ExecuteDeleteAsync(request.SelectionSnapshot, request.PermanentDelete, ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.Paste:
                    await ExecutePasteAsync(ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.Cut:
                    ExecuteCut(request.SelectionSnapshot, ui);
                    break;
                case FileOperationCommandKind.Undo:
                    await ExecuteUndoRedoAsync(undo: true, ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.Redo:
                    await ExecuteUndoRedoAsync(undo: false, ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.Pack:
                    await ExecutePackAsync(
                        request.SelectionSnapshot,
                        request.ForcePackEachFolderIndividually,
                        request.PackRequestOverride,
                        ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.Unpack:
                    await ExecuteUnpackAsync(request.SelectionSnapshot, ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.CreateDirectory:
                    ExecuteCreateItem(request.SelectionSnapshot, directory: true, ui: ui, fileExtension: null);
                    break;
                case FileOperationCommandKind.CreateFile:
                    ExecuteCreateItem(
                        request.SelectionSnapshot,
                        directory: false,
                        ui: ui,
                        fileExtension: request.CreateFileExtension);
                    break;
                case FileOperationCommandKind.ChangeAttributes:
                    await ExecuteAttributeChangeAsync(request.SelectionSnapshot, ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.ClipboardCopy:
                    ExecuteClipboardCopy(request.SelectionSnapshot, ui);
                    break;
                case FileOperationCommandKind.ArchiveHash:
                    await ExecuteHashAsync(request.SelectionSnapshot, request.HashAlgorithm ?? SevenZipHashAlgorithm.Sha256, ui).ConfigureAwait(true);
                    break;
                case FileOperationCommandKind.EmptyManagedTrash:
                    await ExecuteEmptyManagedTrashAsync(ui).ConfigureAwait(true);
                    break;
                default:
                    ui.ShowStatus($"未対応のFileOperation commandです: {request.Kind}");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            ui.ShowStatus("ファイル操作は中断されました。");
        }
        catch (Exception ex)
        {
            LogService.Error($"FileOperation application workflow failed: {request.Kind}", ex);
            ui.ShowUnexpectedError(request.Kind.ToString(), ex);
        }
        finally
        {
            NotifyCompletion(request.CompletionCallback);
            _activeOperationOrigin = null;
        }
    }

    public bool TryStartArchiveExtract(ArchiveExtractRequest request, IFileOperationUiPort ui)
    {
        if (_viewerState.Mode != ViewerApplicationMode.Browser || !CommandBusyPolicy.CanStartFileOperation(_fileState.IsBusy))
        {
            return false;
        }

        if (IsReadOnlyBlocked(FileOperationCommandKind.Unpack))
        {
            ui.ShowStatus(BuildReadOnlyBlockedMessage(FileOperationCommandKind.Unpack));
            return false;
        }

        _activeOperationOrigin = CaptureOperationOrigin(new FileOperationCommandRequest(
            FileOperationCommandKind.Unpack), ui).Origin;
        _ = ExecuteArchiveExtractAsync(request, ui);
        return true;
    }

    public bool TryStartArchiveExtract(
        string archivePath,
        string destinationDirectory,
        IFileOperationUiPort ui)
        => TryStartArchiveExtract(
            new ArchiveExtractRequest
            {
                ArchivePath = archivePath,
                DestinationDirectory = destinationDirectory,
                ExtractAll = true
            },
            ui);

    public bool TryStartQuickPack(SelectionResult selection, IFileOperationUiPort ui)
    {
        ArgumentNullException.ThrowIfNull(selection);
        string? sevenZipExecutable = SevenZipService.ResolveCliExecutable(_settings.Value.SevenZip?.ExePath);
        if (string.IsNullOrWhiteSpace(sevenZipExecutable))
        {
            ui.ShowStatus("7-Zip が見つからないため、圧縮を開始できません。");
            return false;
        }

        string archiveDirectory = Path.GetDirectoryName(selection.FirstPath ?? string.Empty) ?? CurrentOperationPath;
        string archiveName = selection.Count == 1
            ? Path.GetFileNameWithoutExtension(selection.FirstPath ?? string.Empty)
            : Path.GetFileName(archiveDirectory);
        if (string.IsNullOrEmpty(archiveName))
        {
            archiveName = "archive";
        }

        string archivePath = Path.Combine(archiveDirectory, archiveName + ".zip");
        var request = new FileOperationPackRequest(
            new PackRequest
            {
                OutputArchivePath = archivePath,
                Format = PackArchiveFormat.Zip,
                CompressionLevel = PackCompressionLevel.Normal
            },
            selection.FullPaths,
            ExistingArchiveAction: FileOperationPackExistingArchiveAction.Add);
        return TryStart(
            new FileOperationCommandRequest(
                FileOperationCommandKind.Pack,
                selection,
                PackRequestOverride: request),
            ui);
    }

    private async Task ExecuteArchiveExtractAsync(ArchiveExtractRequest request, IFileOperationUiPort ui)
    {
        CancellationToken token = BeginOperation("archive 解凍");
        ui.BeginOperation("archive 解凍", 1);
        try
        {
            AppSettings settings = _settings.Value;
            ArchiveExtractResult result = await Task.Run(
                () => ArchiveExtractService.ExtractSelection(settings.SevenZip?.ExePath, request, token),
                token).ConfigureAwait(true);
            if (!result.Success)
            {
                ui.ShowError("archive 解凍", Path.GetFileName(request.ArchivePath), result.ErrorMessage ?? "archive 解凍に失敗しました。");
                return;
            }

            ApplyOperationResult(new FileOperationResult(
                "Unpack",
                FileOpExitStatus.Success,
                1,
                1,
                destinationDir: request.DestinationDirectory,
                nextFocusTarget: Path.GetFileName(request.ArchivePath),
                shouldClearMarks: false), ui);
        }
        finally
        {
            try
            {
                EndOperation(ui, "解凍完了");
            }
            finally
            {
                _activeOperationOrigin = null;
            }
        }
    }

    private async Task ExecutePackAsync(
        SelectionResult? snapshot,
        bool forcePackEachFolderIndividually,
        FileOperationPackRequest? packRequestOverride,
        IFileOperationUiPort ui)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            ui.ShowStatus("このタブは ReadOnly のため、圧縮は実行できません。");
            return;
        }

        SelectionResult selection = ResolveSelection(snapshot, ui);
        FileOperationPackRequest? packRequest = packRequestOverride
            ?? BuildPackRequest(selection, forcePackEachFolderIndividually, CurrentOperationPath, ui);
        if (packRequest == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(packRequest.Value.RouteError))
        {
            ui.ShowError("Pack", "7-Zip", packRequest.Value.RouteError!);
            return;
        }

        if (packRequest.Value.IsNative)
        {
            await ExecuteNativePackDialogAsync(packRequest.Value, ui).ConfigureAwait(true);
            return;
        }

        PackRequest? request = packRequest.Value.Request;
        if (request == null)
        {
            return;
        }

        if (request.PackEachFolderIndividually)
        {
            await ExecutePackEachIndividuallyAsync(selection, request, packRequest.Value.ExistingArchiveAction, ui).ConfigureAwait(true);
            return;
        }

        CancellationToken token = BeginOperation("Pack");
        ui.BeginOperation("Pack", selection.Count);
        PackOverwriteBackupSession? overwriteBackup = null;
        bool completed = false;
        try
        {
            AppSettings settings = _settings.Value;
            if (packRequest.Value.ExistingArchiveAction == FileOperationPackExistingArchiveAction.Overwrite)
            {
                overwriteBackup = PackOverwriteBackupSession.Create(
                    SevenZipService.GetPackOutputArtifacts(request.OutputArchivePath));
            }

            await Task.Run(() =>
            {
                (int ExitCode, string Output, string Error) result = _packExecutionPort.Pack(
                    settings,
                    request,
                    selection.FullPaths,
                    token);
                token.ThrowIfCancellationRequested();
                if (result.ExitCode != 0)
                {
                    throw new IOException(string.IsNullOrWhiteSpace(result.Error) ? "archive 圧縮に失敗しました。" : result.Error);
                }
            }, token).ConfigureAwait(true);

            completed = true;
            overwriteBackup?.Discard();
            ApplyOperationResult(new FileOperationResult(
                "Pack",
                FileOpExitStatus.Success,
                1,
                1,
                Path.GetFileName(request.OutputArchivePath),
                CurrentOperationPath,
                shouldClearMarks: false), ui);
        }
        catch (OperationCanceledException)
        {
            ApplyOperationResult(new FileOperationResult("Pack", FileOpExitStatus.Canceled, 0, 1, shouldClearMarks: false), ui);
        }
        catch (Exception ex)
        {
            ui.ShowUnexpectedError("Pack", ex);
            ApplyOperationResult(new FileOperationResult("Pack", FileOpExitStatus.Error, 0, 1, shouldClearMarks: false), ui);
        }
        finally
        {
            if (completed)
            {
                overwriteBackup?.Discard();
            }
            else
            {
                overwriteBackup?.Restore();
            }
            EndOperation(ui, "圧縮完了");
        }
    }

    private async Task ExecutePackEachIndividuallyAsync(
        SelectionResult selection,
        PackRequest request,
        FileOperationPackExistingArchiveAction initialArchiveAction,
        IFileOperationUiPort ui)
    {
        SelectionResult? targetSelection = ResolvePackIndividualTargets(selection, ui);
        if (targetSelection == null || targetSelection.Count == 0)
        {
            return;
        }

        string outputDirectory = Path.GetDirectoryName(request.OutputArchivePath) ?? CurrentOperationPath;
        string extension = Path.GetExtension(request.OutputArchivePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".zip";
        }

        var outputPaths = targetSelection.FullPaths
            .Select(path => Path.Combine(outputDirectory, Path.GetFileName(path) + extension))
            .ToList();
        FileOperationPackExistingArchiveAction archiveAction = initialArchiveAction;
        string? firstExisting = outputPaths.FirstOrDefault(File.Exists);
        if (firstExisting != null)
        {
            FileOperationPackExistingArchiveAnswer answer = ui.ChoosePackExistingArchive(firstExisting);
            if (answer == FileOperationPackExistingArchiveAnswer.Cancel)
            {
                return;
            }
            archiveAction = answer == FileOperationPackExistingArchiveAnswer.Overwrite
                ? FileOperationPackExistingArchiveAction.Overwrite
                : FileOperationPackExistingArchiveAction.Add;
        }

        CancellationToken token = BeginOperation("Pack");
        ui.BeginOperation("Pack", targetSelection.Count);
        PackOverwriteBackupSession? overwriteBackup = null;
        bool completed = false;
        int successCount = 0;
        int failCount = 0;
        try
        {
            AppSettings settings = _settings.Value;
            if (archiveAction == FileOperationPackExistingArchiveAction.Overwrite)
            {
                overwriteBackup = PackOverwriteBackupSession.Create(
                    outputPaths.SelectMany(SevenZipService.GetPackOutputArtifacts).ToArray());
            }

            await Task.Run(() =>
            {
                for (int index = 0; index < targetSelection.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    string sourcePath = targetSelection.FullPaths[index];
                    string outputPath = outputPaths[index];
                    try
                    {
                        PackRequest itemRequest = new()
                        {
                            OutputArchivePath = outputPath,
                            Format = request.Format,
                            CompressionLevel = request.CompressionLevel,
                            SplitSize = request.SplitSize
                        };
                        (int ExitCode, string Output, string Error) result = _packExecutionPort.Pack(
                            settings,
                            itemRequest,
                            new[] { sourcePath },
                            token);
                        token.ThrowIfCancellationRequested();
                        if (result.ExitCode != 0)
                        {
                            throw new IOException(string.IsNullOrWhiteSpace(result.Error) ? "archive 圧縮に失敗しました。" : result.Error);
                        }

                        successCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        ui.ShowError("Pack", Path.GetFileName(sourcePath), ex.Message);
                    }

                    ui.ReportProgress(new FileOperationProgress(index + 1, targetSelection.Count, Path.GetFileName(sourcePath)));
                }
            }, token).ConfigureAwait(true);

            FileOpExitStatus status = FileOperationPresentationHelper.NormalizeExitStatus(
                failCount > 0 ? FileOpExitStatus.Error : FileOpExitStatus.Success,
                successCount,
                targetSelection.Count,
                failCount: failCount);
            completed = status == FileOpExitStatus.Success;
            if (completed)
            {
                overwriteBackup?.Discard();
            }
            ApplyOperationResult(new FileOperationResult(
                "Pack",
                status,
                successCount,
                targetSelection.Count,
                shouldClearMarks: false,
                customMessage: status == FileOpExitStatus.Success ? "個別圧縮が完了しました。" : null,
                failCount: failCount), ui);
        }
        catch (OperationCanceledException)
        {
            ApplyOperationResult(new FileOperationResult(
                "Pack",
                FileOpExitStatus.Canceled,
                successCount,
                targetSelection.Count,
                shouldClearMarks: false,
                failCount: failCount), ui);
        }
        catch (Exception ex)
        {
            ui.ShowUnexpectedError("Pack", ex);
            ApplyOperationResult(new FileOperationResult(
                "Pack",
                FileOpExitStatus.Error,
                successCount,
                targetSelection.Count,
                shouldClearMarks: false,
                failCount: failCount + 1), ui);
        }
        finally
        {
            if (completed)
            {
                overwriteBackup?.Discard();
            }
            else
            {
                overwriteBackup?.Restore();
            }
            EndOperation(ui, "圧縮完了");
        }
    }

    private sealed class DefaultPackExecutionPort : IFileOperationPackExecutionPort
    {
        public (int ExitCode, string Output, string Error) Pack(
            AppSettings settings,
            PackRequest request,
            IReadOnlyList<string> sourcePaths,
            CancellationToken cancellationToken)
            => PackOne(settings, request, sourcePaths, cancellationToken);
    }

    private static (int ExitCode, string Output, string Error) PackOne(
        AppSettings settings,
        PackRequest request,
        IReadOnlyList<string> sourcePaths,
        CancellationToken token)
    {
        string? executable = SevenZipService.ResolveExecutable(settings.SevenZip?.ExePath);
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            return SevenZipService.Pack(executable, sourcePaths.ToList(), request, token);
        }

        if (request.Format == PackArchiveFormat.Zip)
        {
            ZipFallbackService.Pack(request.OutputArchivePath, sourcePaths);
            return (0, string.Empty, string.Empty);
        }

        if ((request.Format == PackArchiveFormat.SevenZip || request.Format == PackArchiveFormat.Tar)
            && TarFallbackService.IsAvailable())
        {
            (string baseDirectory, IReadOnlyList<string> relativePaths) = BuildTarPackSourceSet(sourcePaths);
            return TarFallbackService.Pack(
                request.OutputArchivePath,
                baseDirectory,
                relativePaths,
                token);
        }

        throw new FileNotFoundException(SevenZipService.BuildUnavailableMessage(settings.SevenZip?.ExePath, "archive を圧縮"));
    }

    internal static (string BaseDirectory, IReadOnlyList<string> RelativePaths) BuildTarPackSourceSet(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0)
        {
            throw new InvalidOperationException("archive 圧縮対象がありません。");
        }

        string[] fullPaths = sourcePaths.Select(Path.GetFullPath).ToArray();
        string commonDirectory = Path.GetDirectoryName(fullPaths[0])
            ?? throw new InvalidOperationException("圧縮元ディレクトリを解決できません。");
        foreach (string sourcePath in fullPaths.Skip(1))
        {
            while (!FileOperationService.IsSameOrDescendantPath(commonDirectory, sourcePath))
            {
                DirectoryInfo? parent = Directory.GetParent(commonDirectory);
                if (parent == null)
                {
                    throw new InvalidOperationException("圧縮元の共通ディレクトリを解決できません。");
                }
                commonDirectory = parent.FullName;
            }
        }

        return (commonDirectory, fullPaths.Select(path => Path.GetRelativePath(commonDirectory, path)).ToArray());
    }

    private sealed class PackOverwriteBackupSession
    {
        private readonly IReadOnlyList<(string OriginalPath, string BackupPath)> _movedFiles;

        private PackOverwriteBackupSession(IReadOnlyList<(string OriginalPath, string BackupPath)> movedFiles)
        {
            _movedFiles = movedFiles;
        }

        public static PackOverwriteBackupSession Create(IEnumerable<string> targetPaths)
        {
            var movedFiles = new List<(string OriginalPath, string BackupPath)>();
            try
            {
                foreach (string originalPath in targetPaths
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
                    string backupPath;
                    do
                    {
                        backupPath = Path.Combine(
                            directory,
                            $"{Path.GetFileName(originalPath)}.midfd-packbak-{Guid.NewGuid():N}");
                    }
                    while (File.Exists(backupPath));

                    File.Move(originalPath, backupPath);
                    movedFiles.Add((originalPath, backupPath));
                }

                return new PackOverwriteBackupSession(movedFiles);
            }
            catch
            {
                RestorePaths(movedFiles);
                throw;
            }
        }

        public void Restore() => RestorePaths(_movedFiles);

        public void Discard()
        {
            foreach ((_, string backupPath) in _movedFiles)
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
        }

        private static void RestorePaths(IReadOnlyList<(string OriginalPath, string BackupPath)> movedFiles)
        {
            for (int index = movedFiles.Count - 1; index >= 0; index--)
            {
                (string originalPath, string backupPath) = movedFiles[index];
                if (File.Exists(originalPath))
                {
                    File.Delete(originalPath);
                }
                if (File.Exists(backupPath))
                {
                    File.Move(backupPath, originalPath);
                }
            }
        }
    }

    private async Task ExecuteNativePackDialogAsync(FileOperationPackRequest request, IFileOperationUiPort ui)
    {
        try
        {
            using Process? process = SevenZipService.StartNativePackDialog(
                request.NativeGuiExecutablePath!,
                request.NativeArchivePath!,
                request.SourcePaths ?? Array.Empty<string>());
            if (process == null)
            {
                throw new InvalidOperationException("7zG.exe のプロセスを開始できませんでした。");
            }

            ui.ShowStatus("7-Zip標準の圧縮Dialogを表示しました。");
            await process.WaitForExitAsync().ConfigureAwait(true);
            ApplyPostOperationPlan(FileOperationPostOperationCoordinator.CreateReloadPlan(), ui);
        }
        catch (Exception ex)
        {
            ui.ShowUnexpectedError("Pack", ex);
        }
    }

    private async Task ExecuteUnpackAsync(SelectionResult? snapshot, IFileOperationUiPort ui)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            ui.ShowStatus("このタブは ReadOnly のため、解凍は実行できません。");
            return;
        }

        SelectionResult selection = ResolveSelection(snapshot, ui);
        IReadOnlyList<ArchiveExtractRequest>? requests = BuildUnpackRequests(
            selection,
            CurrentOperationPath,
            ui);
        if (requests == null || requests.Count == 0)
        {
            return;
        }

        CancellationToken token = BeginOperation("Unpack");
        ui.BeginOperation("Unpack", requests.Count);
        int successCount = 0;
        int failCount = 0;
        var unpackedArchives = new List<string>();
        bool unpackMarksApplied = false;
        try
        {
            AppSettings settings = _settings.Value;
            await Task.Run(() =>
            {
                for (int index = 0; index < requests.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    ArchiveExtractRequest request = requests[index];
                    try
                    {
                        ArchiveExtractResult result = ArchiveExtractService.ExtractSelection(
                            settings.SevenZip?.ExePath,
                            request,
                            token);
                        if (!result.Success)
                        {
                            throw new IOException(result.ErrorMessage ?? "archive 解凍に失敗しました。");
                        }

                        successCount++;
                        unpackedArchives.Add(request.ArchivePath);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        ui.ShowError("Unpack", Path.GetFileName(request.ArchivePath), ex.Message);
                    }
                    finally
                    {
                        ui.ReportProgress(new FileOperationProgress(
                            index + 1,
                            requests.Count,
                            Path.GetFileName(request.ArchivePath)));
                    }
                }
            }, token).ConfigureAwait(true);

            RemoveMarksAfterFileOperation(unpackedArchives);
            unpackMarksApplied = true;

            FileOpExitStatus status = FileOperationPresentationHelper.NormalizeExitStatus(
                failCount > 0 ? FileOpExitStatus.Error : FileOpExitStatus.Success,
                successCount,
                requests.Count,
                failCount: failCount);
            ApplyOperationResult(new FileOperationResult(
                "Unpack",
                status,
                successCount,
                requests.Count,
                destinationDir: requests[0].DestinationDirectory,
                shouldClearMarks: false,
                failCount: failCount), ui);
        }
        catch (OperationCanceledException)
        {
            ApplyOperationResult(new FileOperationResult(
                "Unpack",
                FileOpExitStatus.Canceled,
                successCount,
                requests.Count,
                destinationDir: requests[0].DestinationDirectory,
                shouldClearMarks: false,
                failCount: failCount), ui);
        }
        catch (Exception ex)
        {
            ui.ShowUnexpectedError("Unpack", ex);
            ApplyOperationResult(new FileOperationResult(
                "Unpack",
                FileOpExitStatus.Error,
                successCount,
                requests.Count,
                destinationDir: requests[0].DestinationDirectory,
                shouldClearMarks: false,
                failCount: failCount + 1), ui);
        }
        finally
        {
            if (!unpackMarksApplied)
            {
                RemoveMarksAfterFileOperation(unpackedArchives);
            }
            EndOperation(ui, "解凍完了");
        }
    }

    private void ExecuteCreateItem(
        SelectionResult? snapshot,
        bool directory,
        IFileOperationUiPort ui,
        string? fileExtension)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            return;
        }

        string? name = ui.RequestNewItemName(directory, fileExtension);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = NewFileExtensionHelper.ApplyToFileName(name, directory ? null : fileExtension);

        string target = Path.Combine(CurrentOperationPath, name);
        try
        {
            if (directory)
            {
                FileOperationService.CreateDirectoryForUserMutation(target);
            }
            else if (!File.Exists(target))
            {
                File.Create(target).Dispose();
            }
            bool selectCreated = _settings.Value.FileOperations?.SelectCreatedItemAfterCreate ?? true;
            string? focusTarget = selectCreated ? name : null;
            ApplyOperationResult(new FileOperationResult(
                directory ? "フォルダ作成" : "ファイル作成",
                FileOpExitStatus.Success,
                1,
                1,
                focusTarget,
                CurrentOperationPath,
                customMessage: directory ? "フォルダを作成しました。" : "ファイルを作成しました。",
                shouldClearMarks: false), ui);
        }
        catch (Exception ex)
        {
            ui.ShowUnexpectedError(directory ? "フォルダ作成" : "ファイル作成", ex);
        }
    }

    private async Task ExecuteAttributeChangeAsync(SelectionResult? snapshot, IFileOperationUiPort ui)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            ui.ShowStatus("このタブは ReadOnly のため、属性変更は実行できません。");
            return;
        }

        SelectionResult selection = ResolveSelection(snapshot, ui);
        FileOperationAttributeOptions? options = BuildAttributeOptions(selection, ui);
        if (options == null)
        {
            return;
        }

        IReadOnlyList<string> targets = ResolveAttributeTargets(selection.FullPaths, options.Value.IncludeSubdirectories);
        if (targets.Count == 0)
        {
            ui.ShowStatus("属性変更の適用対象がありません。");
            return;
        }

        CancellationToken token = BeginOperation("属性 / 日時変更");
        ui.BeginOperation("属性 / 日時変更", targets.Count);
        int successCount = 0;
        int failCount = 0;
        try
        {
            await Task.Run(() =>
            {
                for (int index = 0; index < targets.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        ApplyAttributesAndTimestamps(targets[index], options.Value);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        ui.ShowError("属性 / 日時変更", Path.GetFileName(targets[index]), ex.Message);
                    }
                    ui.ReportProgress(new FileOperationProgress(index + 1, targets.Count, Path.GetFileName(targets[index])));
                }
            }, token).ConfigureAwait(true);
            FileOpExitStatus status = FileOperationPresentationHelper.NormalizeExitStatus(
                failCount > 0 ? FileOpExitStatus.Error : FileOpExitStatus.Success,
                successCount,
                targets.Count,
                failCount: failCount);
            ApplyOperationResult(new FileOperationResult(
                "属性 / 日時変更",
                status,
                successCount,
                targets.Count,
                destinationDir: CurrentOperationPath,
                shouldClearMarks: false,
                customMessage: $"属性/日時を変更しました。({successCount} 件 / 失敗 {failCount} 件)",
                failCount: failCount), ui);
        }
        finally
        {
            EndOperation(ui, "属性 / 日時変更完了");
        }
    }

    private static IReadOnlyList<string> ResolveAttributeTargets(IReadOnlyList<string> roots, bool includeSubdirectories)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (!visited.Add(root) || (!File.Exists(root) && !Directory.Exists(root))) continue;
            result.Add(root);
            if (!includeSubdirectories || !Directory.Exists(root)) continue;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(current))
                    {
                        if (visited.Add(file)) result.Add(file);
                    }
                    foreach (string directory in Directory.EnumerateDirectories(current))
                    {
                        if (!visited.Add(directory)) continue;
                        result.Add(directory);
                        if (!ReparsePointHelper.IsReparsePoint(directory)) pending.Push(directory);
                    }
                }
                catch
                {
                    // Keep the existing partial-operation contract: unreadable descendants are reported by their parent operation.
                }
            }
        }
        return new ReadOnlyCollection<string>(result);
    }

    private static void ApplyAttributesAndTimestamps(string path, FileOperationAttributeOptions options)
    {
        FileAttributes current = File.GetAttributes(path);
        FileAttributes next = ApplyAttributeAction(current, FileAttributes.ReadOnly, options.ReadOnlyAction);
        next = ApplyAttributeAction(next, FileAttributes.Hidden, options.HiddenAction);
        next = ApplyAttributeAction(next, FileAttributes.System, options.SystemAction);
        next = ApplyAttributeAction(next, FileAttributes.Archive, options.ArchiveAction);
        if (next != current) File.SetAttributes(path, next);
        bool directory = Directory.Exists(path);
        if (options.ChangeLastWriteTime) { if (directory) Directory.SetLastWriteTime(path, options.LastWriteTime); else File.SetLastWriteTime(path, options.LastWriteTime); }
        if (options.ChangeCreationTime) { if (directory) Directory.SetCreationTime(path, options.CreationTime); else File.SetCreationTime(path, options.CreationTime); }
        if (options.ChangeLastAccessTime) { if (directory) Directory.SetLastAccessTime(path, options.LastAccessTime); else File.SetLastAccessTime(path, options.LastAccessTime); }
    }

    private static FileAttributes ApplyAttributeAction(FileAttributes current, FileAttributes bit, FileOperationAttributeChangeAction action)
        => action switch
        {
            FileOperationAttributeChangeAction.Set => current | bit,
            FileOperationAttributeChangeAction.Clear => current & ~bit,
            _ => current
        };

    private async Task ExecuteRenameAsync(SelectionResult? snapshot, IFileOperationUiPort ui)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            return;
        }

        SelectionResult selection = ResolveSelection(snapshot, ui);
        if (selection.Count == 0)
        {
            ui.ShowStatus("リネーム対象がありません。");
            return;
        }

        if (!TryResolveMultiMarkSelectionAction(
                "リネーム",
                "リネームはキャンセルされました。",
                selection,
                ui,
                out selection))
        {
            return;
        }

        FileOperationRenamePlan plan = BuildRenamePlan(selection, ui);
        if (plan.Canceled || plan.Items.Count == 0)
        {
            ui.ShowStatus("リネームはキャンセルされました。");
            return;
        }

        IReadOnlyList<RenamePreviewItem> applicable = plan.Items.Where(item => item.WillRename).ToList();
        if (applicable.Count == 0)
        {
            ui.ShowStatus("変更はありません。");
            return;
        }

        CancellationToken token = BeginOperation("Rename");
        ui.BeginOperation("Rename", applicable.Count);
        var successful = new List<RenamePreviewItem>();
        int failures = 0;
        try
        {
            await Task.Run(() =>
            {
                for (int index = 0; index < applicable.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    RenamePreviewItem item = applicable[index];
                    try
                    {
                        FileOperationService.Rename(item.SourcePath, item.DestinationPath);
                        successful.Add(item);
                        ui.ReportProgress(new FileOperationProgress(index + 1, applicable.Count, item.DestinationName));
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        ui.ShowError("リネーム", item.SourceName, ex.Message);
                        break;
                    }
                }
            }, token).ConfigureAwait(true);

            if (successful.Count > 0)
            {
                _undoRedo.RecordBatch(
                    FileOperationUndoRedoOperation.Rename,
                    FileOperationUndoRedoService.CreateRenameBatch(successful));
            }

            FileOpExitStatus status = FileOperationPresentationHelper.NormalizeExitStatus(
                failures > 0 ? FileOpExitStatus.Error : FileOpExitStatus.Success,
                successful.Count,
                selection.Count,
                skipCount: Math.Max(0, selection.Count - successful.Count - failures),
                failCount: failures);
            ApplyOperationResult(new FileOperationResult(
                "Rename",
                status,
                successful.Count,
                selection.Count,
                successful.LastOrDefault()?.DestinationName,
                shouldClearMarks: status is FileOpExitStatus.Success or FileOpExitStatus.PartialSuccess or FileOpExitStatus.Skipped,
                customMessage: FileOperationPresentationHelper.GetRenameResultStatusMessage(
                    new FileOperationResult("Rename", status, successful.Count, selection.Count, failCount: failures)),
                skipCount: Math.Max(0, selection.Count - successful.Count - failures),
                failCount: failures), ui);
        }
        finally
        {
            EndOperation(ui, "リネーム完了");
        }
    }

    private async Task ExecuteCopyAsync(SelectionResult? snapshot, IFileOperationUiPort ui)
    {
        await ExecuteCopyOrMoveAsync(snapshot, isMove: false, destinationDirectory: null, ui).ConfigureAwait(true);
    }

    private async Task ExecuteMoveAsync(SelectionResult? snapshot, IFileOperationUiPort ui)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            return;
        }

        await ExecuteCopyOrMoveAsync(snapshot, isMove: true, destinationDirectory: null, ui).ConfigureAwait(true);
    }

    private async Task ExecuteHashAsync(
        SelectionResult? snapshot,
        SevenZipHashAlgorithm algorithm,
        IFileOperationUiPort ui)
    {
        SelectionResult selection = ResolveSelection(snapshot, ui);
        if (selection.Count == 0)
        {
            ui.ShowStatus("ハッシュ計算対象がありません。");
            return;
        }
        if (selection.FullPaths.Any(Directory.Exists))
        {
            ui.ShowError("CRC/SHA 計算", "選択項目", "ディレクトリのハッシュ計算には対応していません。ファイルのみを選択してください。");
            return;
        }

        AppSettings settings = _settings.Value;
        string? executable = SevenZipService.ResolveExecutable(settings.SevenZip?.ExePath);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            ui.ShowError("CRC/SHA 計算", "7-Zip", SevenZipService.BuildUnavailableMessage(settings.SevenZip?.ExePath, "CRC/SHA 計算"));
            return;
        }

        string targetSummary = selection.Count == 1
            ? Path.GetFileName(selection.FirstPath ?? string.Empty)
            : $"{Path.GetFileName(selection.FirstPath ?? string.Empty)} ほか {selection.Count - 1} 件";
        CancellationToken token = BeginOperation("CRC/SHA 計算");
        ui.BeginOperation("CRC/SHA 計算", selection.Count);
        try
        {
            (int ExitCode, string Output, string Error) result = await SevenZipService.HashAsync(
                executable,
                selection.FullPaths.ToList(),
                algorithm,
                token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode == 0)
            {
                ui.ShowHashResult(targetSummary, algorithm, result.Output);
                ui.ShowStatus("CRC/SHA 計算完了");
            }
            else
            {
                ui.ShowError("CRC/SHA 計算", targetSummary, string.IsNullOrWhiteSpace(result.Error)
                    ? "計算中にエラーが発生しました。"
                    : result.Error);
            }
        }
        finally
        {
            EndOperation(ui, "CRC/SHA 計算完了");
        }
    }

    private async Task ExecuteCopyOrMoveAsync(
        SelectionResult? snapshot,
        bool isMove,
        string? destinationDirectory,
        IFileOperationUiPort ui,
        bool recordDragCopyUndo = false)
    {
        if (isMove && _activeOperationOrigin?.IsReadOnly == true)
        {
            return;
        }

        SelectionResult selection = ResolveSelection(snapshot, ui);
        string operation = isMove ? "移動" : "コピー";
        if (selection.Count == 0)
        {
            ui.ShowStatus($"{operation}対象がありません。");
            return;
        }

        if (!TryResolveMultiMarkSelectionAction(
                operation,
                $"{operation}はキャンセルされました。",
                selection,
                ui,
                out selection))
        {
            return;
        }

        FileOperationDestinationSelection destination;
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            destination = new FileOperationDestinationSelection(destinationDirectory, NeedsCreateDirectory: false);
        }
        else if (!TryBuildDestinationSelection(operation, selection, ui, out destination))
        {
            return;
        }

        if (destination.NeedsCreateDirectory && !ui.ConfirmCreateDirectory(destination.Directory))
        {
            return;
        }

        bool createdDestinationForOperation = false;
        if (destination.NeedsCreateDirectory)
        {
            Directory.CreateDirectory(destination.Directory);
            createdDestinationForOperation = true;
        }

        var actions = new List<PlannedFileOperation>();
        bool renameSameDirectoryToAll = false;
        CopyCollisionDecision? copyCollisionApplyToAll = null;
        DirectoryMergeDecision? directoryMergeApplyToAll = null;
        foreach (string sourcePath in selection.FullPaths)
        {
            string destinationPath = Path.Combine(destination.Directory, Path.GetFileName(sourcePath));
            if (!TryPlanActions(
                    sourcePath,
                    destinationPath,
                    isMove,
                    selection.Count,
                    ui,
                    ref renameSameDirectoryToAll,
                    ref copyCollisionApplyToAll,
                    ref directoryMergeApplyToAll,
                    actions))
            {
                return;
            }
        }

        CancellationToken token = BeginOperation(isMove ? "Move" : "Copy");
        ui.BeginOperation(isMove ? "Move" : "Copy", actions.Count);
        int successCount = 0;
        int skipCount = actions.Count(action => action.Skip);
        int failCount = 0;
        var undoItems = new List<(string SourcePath, string DestinationPath)>();
        var createdFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? focusTarget = null;
        var successfulSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> selectedSourcePaths = selection.FullPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool sourceMarksApplied = false;
        try
        {
            IReadOnlyList<LinkOperationRoot> finalLinkRoots = BuildFinalLinkRoots(actions, selection.FullPaths);
            IReadOnlyList<LinkOperationRoot> helperRoots = isMove
                ? LinkOperationPreparationService.BuildCrossVolumeMoveRoots(finalLinkRoots)
                : finalLinkRoots;
            LinkOperationPreparationResult linkPreparation = await LinkOperationPreparationService.PrepareAsync(
                helperRoots,
                allowHelper: !isMove || helperRoots.Count > 0,
                ui.ChooseLinkOperation,
                LinkOperationPreparationService.EnsureDestinationParents,
                LinkOperationPreparationService.CleanupCreatedParents,
                _linkCopyPort.CopyAsync,
                isMove ? "move-link" : "copy-link",
                token).ConfigureAwait(true);
            if (linkPreparation.Canceled)
            {
                CleanupOperationCreatedDestination(destination.Directory, createdDestinationForOperation);
                FileOperationResult canceledResult = isMove
                    ? _postOperation.CreateMoveResult(
                        FileOpExitStatus.Canceled,
                        0,
                        actions.Count,
                        null,
                        destination.Directory,
                        shouldClearMarks: false,
                        customMessage: null,
                        skipCount: 0,
                        failCount: 0)
                    : new FileOperationResult(
                        "Copy",
                        FileOpExitStatus.Canceled,
                        0,
                        actions.Count,
                        null,
                        destination.Directory,
                        shouldClearMarks: false,
                        skipCount: 0,
                        failCount: 0);
                ApplyOperationResult(canceledResult, ui);
                return;
            }

            skipCount += linkPreparation.SkipCount;
            failCount += linkPreparation.FailCount;
            var helperActionSources = actions
                .Where(action => !action.Skip)
                .Select(action => action.SourcePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            successCount += linkPreparation.SuccessfulSources.Count(helperActionSources.Contains);
            if (!isMove)
            {
                foreach (string sourcePath in linkPreparation.SuccessfulSources)
                {
                    if (selectedSourcePaths.Contains(sourcePath))
                    {
                        successfulSourcePaths.Add(sourcePath);
                    }
                }
            }
            if (recordDragCopyUndo && !isMove)
            {
                foreach (PlannedFileOperation action in actions.Where(action =>
                    !action.Skip
                    && !action.IsDirectory
                    && linkPreparation.SuccessfulSources.Contains(action.SourcePath)))
                {
                    createdFiles.Add(action.DestinationPath);
                }
            }
            await Task.Run(() =>
            {
                FileOperationCommandKind operationKind = isMove
                    ? FileOperationCommandKind.Move
                    : FileOperationCommandKind.Copy;
                _executionProbe?.OnWorkerStarted(operationKind);
                try
                {
                    _executionProbe?.WaitForWorkerRelease(operationKind, token);
                    for (int index = 0; index < actions.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        PlannedFileOperation action = actions[index];
                        if (action.Skip || linkPreparation.ExcludedSources.Contains(action.SourcePath))
                        {
                            ui.ReportProgress(new FileOperationProgress(index + 1, actions.Count, Path.GetFileName(action.DestinationPath)));
                            continue;
                        }

                        try
                        {
                            if (isMove)
                            {
                                if (!action.IsDirectoryMergeRoot)
                                {
                                    if (action.IsDirectory)
                                    {
                                        FileOperationService.CreateDirectoryForUserMutation(action.DestinationPath);
                                    }
                                    else if (helperRoots.Any(root =>
                                        string.Equals(root.SourcePath, action.SourcePath, StringComparison.OrdinalIgnoreCase)
                                        && Directory.Exists(action.SourcePath)
                                        && !ReparsePointHelper.IsReparsePoint(action.SourcePath)))
                                    {
                                        FileOperationService.Copy(
                                            action.SourcePath,
                                            action.DestinationPath,
                                            excludedReparsePaths: linkPreparation.ExcludedSources);
                                        DeletePreparedCrossVolumeDirectorySource(
                                            action.SourcePath,
                                            linkPreparation.ExcludedSources,
                                            linkPreparation.SuccessfulSources);
                                    }
                                    else
                                    {
                                        FileOperationService.Move(
                                            action.SourcePath,
                                            action.DestinationPath,
                                            action.Overwrite,
                                            cancellationToken: token,
                                            excludedReparsePaths: linkPreparation.ExcludedSources);
                                    }
                                    if (!action.IsDirectory)
                                    {
                                        undoItems.Add((action.SourcePath, action.DestinationPath));
                                    }
                                }
                            }
                            else
                            {
                                if (!action.IsDirectoryMergeRoot)
                                {
                                    if (action.IsDirectory)
                                    {
                                        FileOperationService.CreateDirectoryForUserMutation(action.DestinationPath);
                                    }
                                    else
                                    {
                                        FileOperationService.Copy(
                                            action.SourcePath,
                                            action.DestinationPath,
                                            linkPreparation.ExcludedSources);
                                        if (recordDragCopyUndo)
                                        {
                                            createdFiles.Add(action.DestinationPath);
                                        }
                                    }
                                }
                            }

                            successCount++;
                            focusTarget ??= Path.GetFileName(action.DestinationPath);
                            if (selectedSourcePaths.Contains(action.SourcePath))
                            {
                                successfulSourcePaths.Add(action.SourcePath);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            ui.ShowError(operation, Path.GetFileName(action.SourcePath), ex.Message);
                            break;
                        }
                        finally
                        {
                            ui.ReportProgress(new FileOperationProgress(index + 1, actions.Count, Path.GetFileName(action.DestinationPath)));
                        }
                    }
                }
                finally
                {
                    _executionProbe?.OnWorkerCompleted(operationKind);
                }
            }, token).ConfigureAwait(true);

            if (isMove)
            {
                foreach (LinkOperationPlanItem item in linkPreparation.Plan.Items
                    .Where(item => item.IsTopLevel
                        && linkPreparation.SuccessfulTopLevelSources.Contains(item.TopLevelSourcePath)))
                {
                    int deleteFailures = FileOperationService.DeleteSuccessfulPreparedReparsePointsUnderSource(
                        item.SourcePath,
                        linkPreparation.ExcludedSources,
                        linkPreparation.SuccessfulSources,
                        "FileOperation");
                    failCount += deleteFailures;
                    if (deleteFailures == 0)
                    {
                        if (selectedSourcePaths.Contains(item.SourcePath))
                        {
                            successfulSourcePaths.Add(item.SourcePath);
                        }
                        undoItems.Add((item.SourcePath, item.DestinationPath));
                        focusTarget ??= Path.GetFileName(item.DestinationPath);
                    }
                }

            }

            if (isMove)
            {
                foreach (string sourceRoot in actions
                    .Where(action => action.IsDirectoryMergeRoot)
                    .Select(action => action.SourcePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    failCount += FileOperationService.DeleteSuccessfulPreparedReparsePointsUnderSource(
                        sourceRoot,
                        linkPreparation.ExcludedSources,
                        linkPreparation.SuccessfulSources,
                        "FileOperation");
                    FileOperationService.DeleteEmptyDirectoriesBottomUp(sourceRoot);
                }
            }

            if (isMove && undoItems.Count > 0 && failCount == 0 && skipCount == 0)
            {
                _undoRedo.RecordBatch(FileOperationUndoRedoOperation.Move, FileOperationUndoRedoService.CreateMoveBatch(undoItems));
            }
            else if (!isMove && recordDragCopyUndo && createdFiles.Count > 0 && failCount == 0)
            {
                _undoRedo.RecordBatch(
                    FileOperationUndoRedoOperation.CreateFromPaste,
                    FileOperationUndoRedoService.CreateCreatedFilesBatch(createdFiles));
            }

            FileOpExitStatus status = FileOperationPresentationHelper.NormalizeExitStatus(
                failCount > 0 ? FileOpExitStatus.Error : FileOpExitStatus.Success,
                successCount,
                actions.Count,
                skipCount,
                failCount);
            FileOperationResult result = isMove
                ? _postOperation.CreateMoveResult(status, successCount, actions.Count, focusTarget, destination.Directory, false, null, skipCount, failCount)
                : new FileOperationResult(
                    "Copy",
                    status,
                    successCount,
                    actions.Count,
                    focusTarget,
                    destination.Directory,
                    shouldClearMarks: false,
                    skipCount: skipCount,
                    failCount: failCount);
            RemoveMarksAfterFileOperation(successfulSourcePaths);
            sourceMarksApplied = true;
            ApplyOperationResult(result, ui);
        }
        catch (OperationCanceledException)
        {
            RemoveMarksAfterFileOperation(successfulSourcePaths);
            sourceMarksApplied = true;

            FileOperationResult result = isMove
                ? _postOperation.CreateMoveResult(
                    FileOpExitStatus.Canceled,
                    successCount,
                    actions.Count,
                    focusTarget,
                    destination.Directory,
                    shouldClearMarks: false,
                    customMessage: null,
                    skipCount,
                    failCount)
                : new FileOperationResult(
                    "Copy",
                    FileOpExitStatus.Canceled,
                    successCount,
                    actions.Count,
                    focusTarget,
                    destination.Directory,
                    shouldClearMarks: false,
                    skipCount: skipCount,
                    failCount: failCount);
            ApplyOperationResult(result, ui);
        }
        finally
        {
            if (!sourceMarksApplied)
            {
                RemoveMarksAfterFileOperation(successfulSourcePaths);
            }
            EndOperation(ui, $"{operation}完了");
        }
    }

    private static IReadOnlyList<LinkOperationRoot> BuildFinalLinkRoots(
        IReadOnlyList<PlannedFileOperation> actions,
        IReadOnlyCollection<string> topLevelSources)
    {
        var topLevelSet = topLevelSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = new List<LinkOperationRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlannedFileOperation action in actions.Where(action => !action.Skip))
        {
            if (!ReparsePointHelper.Exists(action.SourcePath))
            {
                continue;
            }

            bool selectedTopLevel = topLevelSet.Contains(action.SourcePath);
            bool isReparsePoint = ReparsePointHelper.IsReparsePoint(action.SourcePath);
            bool isTopLevelDirectory = selectedTopLevel
                && !action.IsDirectoryMergeRoot
                && Directory.Exists(action.SourcePath)
                && !isReparsePoint;
            bool isExpandedChildLink = !selectedTopLevel && isReparsePoint;
            if (!isTopLevelDirectory && !(selectedTopLevel && isReparsePoint) && !isExpandedChildLink)
            {
                continue;
            }

            string key = $"{action.SourcePath}\0{action.DestinationPath}";
            if (seen.Add(key))
            {
                roots.Add(new LinkOperationRoot(action.SourcePath, action.DestinationPath));
            }
        }
        return roots;
    }

    internal static void CleanupOperationCreatedDestination(
        string destinationDirectory,
        bool createdByThisOperation)
    {
        if (!createdByThisOperation || !Directory.Exists(destinationDirectory))
        {
            return;
        }

        try
        {
            if (ReparsePointHelper.IsReparsePoint(destinationDirectory)
                || Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
            {
                return;
            }

            Directory.Delete(destinationDirectory, recursive: false);
        }
        catch
        {
            // Cancellation cleanup is fail-safe: never turn a cancel into a data-loss path.
        }
    }

    internal static void DeletePreparedCrossVolumeDirectorySource(
        string sourceRoot,
        ISet<string> excludedReparsePaths,
        ISet<string> successfulPreparedReparsePaths)
    {
        int deleteFailures = FileOperationService.DeleteSuccessfulPreparedReparsePointsUnderSource(
            sourceRoot,
            excludedReparsePaths,
            successfulPreparedReparsePaths,
            "FileOperation");
        if (deleteFailures > 0)
        {
            throw new IOException("移動元のリンク削除に失敗しました。");
        }

        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string directoryPath in Directory.EnumerateDirectories(current))
            {
                if (FileOperationService.IsDirectoryContainerPath(directoryPath))
                {
                    pending.Push(directoryPath);
                }
            }

            foreach (string filePath in Directory.EnumerateFiles(current))
            {
                if (excludedReparsePaths.Contains(filePath) || ReparsePointHelper.IsReparsePoint(filePath))
                {
                    continue;
                }
                FileOperationService.Delete(filePath);
            }
        }

        FileOperationService.DeleteEmptyDirectoriesBottomUp(sourceRoot);
        if (ReparsePointHelper.Exists(sourceRoot))
        {
            throw new IOException("移動元に未完了のリンクまたは項目が残りました。");
        }
    }

    private void ExecuteClipboardCopy(SelectionResult? snapshot, IFileOperationUiPort ui)
    {
        SelectionResult selection = ResolveSelection(snapshot, ui);
        if (selection.Count == 0)
        {
            ui.ShowStatus("コピー対象がありません。");
            return;
        }

        if (!TryResolveMultiMarkSelectionAction(
                "コピー",
                "コピーはキャンセルされました。",
                selection,
                ui,
                out selection))
        {
            return;
        }

        ui.SetClipboardFileTransfer(selection.FullPaths, isCut: false);
        ui.ShowStatus($"{selection.Count} 件をコピーしました。");
    }

    private async Task ExecuteEmptyManagedTrashAsync(IFileOperationUiPort ui)
    {
        if (!ui.ConfirmEmptyManagedTrash())
        {
            return;
        }

        CancellationToken token = BeginOperation("管理ゴミ箱を空にする");
        ui.BeginOperation("管理ゴミ箱を空にする", 1);
        bool completed = false;
        try
        {
            ManagedTrashOperationResult result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return EmptyManagedTrash();
            }, token).ConfigureAwait(true);
            if (result.ExitStatus == FileOpExitStatus.Error)
            {
                ui.ShowStatus($"MidFD管理ゴミ箱を空にできませんでした: {result.ErrorMessage ?? "不明なエラー"}");
                return;
            }

            completed = true;
            ui.ShowStatus("MidFD管理ゴミ箱を空にしました。");

            if (CurrentOperationPath.Contains(".midfd-trash", StringComparison.OrdinalIgnoreCase))
            {
                ApplyPostOperationPlan(FileOperationPostOperationCoordinator.CreateReloadPlan(), ui);
            }
        }
        finally
        {
            EndOperation(ui, completed ? "管理ゴミ箱を空にしました。" : "管理ゴミ箱処理を終了しました。");
        }
    }

    private async Task ExecuteDeleteAsync(SelectionResult? snapshot, bool permanent, IFileOperationUiPort ui)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            return;
        }

        SelectionResult selection = ResolveSelection(snapshot, ui);
        if (selection.Count == 0)
        {
            ui.ShowStatus("削除対象がありません。");
            return;
        }

        if (!TryResolveMultiMarkSelectionAction(
                permanent ? "完全削除" : "削除",
                "削除はキャンセルされました。",
                selection,
                ui,
                out selection))
        {
            return;
        }

        if (selection.Count > 1)
        {
            selection = new SelectionResult(
                PathNormalizationHelper.FilterParentChildPaths(selection.FullPaths),
                selection.HasMarkedSelection);
        }

        bool shouldConfirm = permanent
            ? (_settings.Value.FileOperations?.ConfirmPermanentDelete ?? true)
            : (_settings.Value.FileOperations?.ConfirmDelete ?? true);
        if (shouldConfirm && !ui.ConfirmDelete(selection, permanent))
        {
            return;
        }

        CancellationToken token = BeginOperation(permanent ? "完全削除" : "削除");
        ui.BeginOperation(permanent ? "完全削除" : "削除", selection.Count);
        AppSettings settings = _settings.Value;
        bool useManagedTrash = !permanent && (settings.FileOperations?.UseMidFdManagedTrash ?? false) && MidFdManagedTrashService.IsAvailable;
        bool manifestBatchStarted = false;
        bool manifestFlushSucceeded = false;
        string managedTrashBatchId = useManagedTrash ? MidFdManagedTrashService.CreateBatchId() : string.Empty;
        int successCount = 0;
        int failCount = 0;
        var undoItems = new List<FileOperationUndoRedoItem>();
        var pendingTrashRecords = new List<TrashManifestRecord>();
        var deletedPaths = new List<string>();
        bool deleteMarksApplied = false;
        bool canceled = false;
        int processedCount = 0;
        int GetManifestRecordCount()
        {
            try
            {
                return MidFdManagedTrashService.GetManifestOperationDiagnostics().RecordCountAfter;
            }
            catch
            {
                return 0;
            }
        }

        ManagedTrashDeleteCancellationDiagnostics? cancellationDiagnostics = useManagedTrash
            ? new ManagedTrashDeleteCancellationDiagnostics(managedTrashBatchId, _managedTrashDeleteDiagnosticHook)
            : null;
        void UpdateCancellationDiagnosticsSnapshot()
        {
            cancellationDiagnostics?.UpdateSnapshot(new ManagedTrashDeleteCancellationSnapshot(
                successCount,
                failCount,
                Math.Max(0, selection.Count - successCount - failCount),
                processedCount,
                GetManifestRecordCount(),
                pendingTrashRecords.Count,
                _fileState.Ui.Cts?.IsCancellationRequested ?? false));
        }
        void RecordCancellationStage(string stage)
        {
            UpdateCancellationDiagnosticsSnapshot();
            cancellationDiagnostics?.Record(stage);
        }
        UpdateCancellationDiagnosticsSnapshot();
        _activeManagedTrashDeleteDiagnostics = cancellationDiagnostics;
        void FlushPendingTrashRecords()
        {
            if (!useManagedTrash || pendingTrashRecords.Count == 0)
            {
                return;
            }

            MidFdManagedTrashService.RegisterNewTrashRecordsPublic(pendingTrashRecords);
            MidFdManagedTrashService.SaveActiveBatch();
            pendingTrashRecords.Clear();
        }
        try
        {
            if (useManagedTrash)
            {
                MidFdManagedTrashService.BeginManifestBatch();
                manifestBatchStarted = true;
            }

            try
            {
                await Task.Run(() =>
                {
                    for (int index = 0; index < selection.FullPaths.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        string path = selection.FullPaths[index];
                        bool processedCountUpdated = false;
                        try
                        {
                            FileOperationUndoRedoItem? undoItem = null;
                            if (useManagedTrash)
                            {
                                undoItem = MidFdManagedTrashService.MoveToTrash(
                                    path,
                                    managedTrashBatchId,
                                    index + 1,
                                    skipRegistration: true,
                                    out TrashManifestRecord? record);
                                if (record != null)
                                {
                                    pendingTrashRecords.Add(record);
                                    if (pendingTrashRecords.Count >= ManagedTrashManifestCheckpointRecordCount)
                                    {
                                        MidFdManagedTrashService.RegisterNewTrashRecordsPublic(pendingTrashRecords);
                                        MidFdManagedTrashService.SaveActiveBatch();
                                        pendingTrashRecords.Clear();
                                    }
                                }
                            }
                            else if (permanent)
                            {
                                FileOperationService.Delete(path);
                            }
                            else
                            {
                                FileOperationService.DeleteToRecycleBin(path);
                            }

                            if (undoItem != null)
                            {
                                undoItems.Add(undoItem);
                            }
                            successCount++;
                            deletedPaths.Add(path);
                            processedCount++;
                            processedCountUpdated = true;
                            UpdateCancellationDiagnosticsSnapshot();
                            _managedTrashDeleteProgressHook?.OnProcessed(successCount);
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            ui.ShowError(permanent ? "完全削除" : "削除", Path.GetFileName(path), ex.Message);
                            break;
                        }
                        finally
                        {
                            if (!processedCountUpdated)
                            {
                                processedCount++;
                                UpdateCancellationDiagnosticsSnapshot();
                            }
                            ui.ReportProgress(new FileOperationProgress(index + 1, selection.Count, Path.GetFileName(path)));
                        }
                    }
                }, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            finally
            {
                RecordCancellationStage("WorkerCanceledOrReturned");
                RecordCancellationStage("PendingManifestFinalizeStart");
                try
                {
                    FlushPendingTrashRecords();
                }
                finally
                {
                    RecordCancellationStage("PendingManifestFinalizeEnd");
                }
            }

            if (token.IsCancellationRequested)
            {
                canceled = true;
            }

            RecordCancellationStage("RemoveMarksStart");
            try
            {
                RemoveMarksAfterFileOperation(deletedPaths);
                deleteMarksApplied = true;
            }
            finally
            {
                RecordCancellationStage("RemoveMarksEnd");
            }

            bool canRecordTrashUndo = useManagedTrash && undoItems.Count > 0;
            string? customCancelMessage = null;
            if (canceled && useManagedTrash && successCount > 0)
            {
                int pendingCount = selection.Count - successCount - failCount;
                RecordCancellationStage("CancelResolutionShowStart");
                DeleteCancelResolution resolution;
                try
                {
                    resolution = ui.ShowDeleteCancelResolution(successCount, pendingCount, failCount);
                }
                finally
                {
                    RecordCancellationStage("CancelResolutionShowReturned");
                }
                if (resolution == DeleteCancelResolution.RestoreNow)
                {
                    int restoreTargetCount = undoItems.Count;
                    ui.BeginOperation("復元", restoreTargetCount);
                    ui.ShowStatus($"{successCount} 件を復元中...");
                    var restoredTrashPaths = new List<string>();
                    ManagedTrashRestoreDiagnostics restoreDiagnostics =
                        _managedTrashRestoreDiagnostics ?? new ManagedTrashRestoreDiagnostics();
                    bool restoreAllSuccess = true;
                    int restoreProcessedCount = 0;
                    RecordCancellationStage("RestoreStart");
                    try
                    {
                        ManagedTrashRestoreLookup restoreLookup =
                            MidFdManagedTrashService.CreateRestoreLookup(restoreDiagnostics);
                        await Task.Run(() =>
                        {
                            foreach (FileOperationUndoRedoItem item in undoItems)
                            {
                                try
                                {
                                    MidFdManagedTrashService.RestoreFromTrashWithDiagnostics(
                                        item,
                                        skipStatusUpdate: true,
                                        suppressLogging: true,
                                        diagnostics: restoreDiagnostics,
                                        restoreLookup: restoreLookup);
                                    restoredTrashPaths.Add(item.RecycleBinPath);
                                }
                                catch (Exception ex)
                                {
                                    restoreAllSuccess = false;
                                    LogService.Error("[DeleteCancelResolution] RestoreNow item error", ex);
                                }
                                finally
                                {
                                    restoreProcessedCount++;
                                    ui.ReportProgress(new FileOperationProgress(
                                        restoreProcessedCount,
                                        restoreTargetCount,
                                        Path.GetFileName(item.BeforePath ?? item.RecycleBinPath ?? string.Empty)));
                                }
                            }
                        }).ConfigureAwait(true);

                        if (restoredTrashPaths.Count > 0)
                        {
                            int updated = 0;
                            var batchStatusSw = Stopwatch.StartNew();
                            try
                            {
                                updated = MidFdManagedTrashService.UpdateRecordStatuses(
                                    restoredTrashPaths,
                                    TrashRecordStatus.Restored);
                                if (updated != restoredTrashPaths.Count)
                                {
                                    restoreAllSuccess = false;
                                    LogService.Error(
                                        $"[DeleteCancelResolution] RestoreNow status update count mismatch. expected={restoredTrashPaths.Count}, actual={updated}");
                                }
                            }
                            catch (Exception ex)
                            {
                                restoreAllSuccess = false;
                                LogService.Error("[DeleteCancelResolution] RestoreNow status update failed.", ex);
                            }
                            finally
                            {
                                batchStatusSw.Stop();
                                restoreDiagnostics.RecordBatchStatusUpdate(batchStatusSw.ElapsedMilliseconds, updated);
                            }
                        }

                        if (restoreDiagnostics.Snapshot().RestoreCount > 0)
                        {
                            LogService.Info(restoreDiagnostics.BuildLogMessage(managedTrashBatchId));
                        }

                        RecordCancellationStage("RestoreProgressSummary");
                    }
                    catch (Exception ex)
                    {
                        restoreAllSuccess = false;
                        LogService.Error("[DeleteCancelResolution] RestoreNow lookup or batch error", ex);
                    }
                    finally
                    {
                        RecordCancellationStage("RestoreEnd");
                    }

                    canRecordTrashUndo = false;
                    undoItems.Clear();
                    customCancelMessage = restoreAllSuccess
                        ? "中断し、削除済みのファイルを復元しました。"
                        : "中断しましたが、一部のファイル復元に失敗しました。";
                }
                else
                {
                    canRecordTrashUndo = true;
                    customCancelMessage = $"中断しました。削除済み {successCount} 件は Ctrl+Z で復元できます。";
                }
            }

            if (canRecordTrashUndo && (failCount == 0 || (canceled && undoItems.Count > 0)))
            {
                _undoRedo.RecordBatch(
                    FileOperationUndoRedoOperation.DeleteToMidFdTrash,
                    FileOperationUndoRedoService.CreateDeleteToTrashBatch(undoItems),
                    isPartialCancellation: canceled);
            }

            FileOpExitStatus status = canceled
                ? FileOpExitStatus.Canceled
                : FileOperationPresentationHelper.NormalizeExitStatus(
                    failCount > 0 ? FileOpExitStatus.Error : FileOpExitStatus.Success,
                    successCount,
                    selection.Count,
                    failCount: failCount);

            FileOperationResult deleteResult = !string.IsNullOrEmpty(customCancelMessage)
                ? new FileOperationResult(
                    "Delete",
                    status,
                    successCount,
                    selection.Count,
                    nextFocusTarget: null,
                    destinationDir: null,
                    shouldClearPreview: true,
                    shouldClearMarks: false,
                    customMessage: customCancelMessage,
                    failCount: failCount)
                : _postOperation.CreateDeleteResult(
                    status,
                    successCount,
                    selection.Count,
                    null,
                    permanent,
                    useManagedTrash && undoItems.Count > 0,
                    failCount);
            RecordCancellationStage("PostOperationStart");
            try
            {
                ApplyOperationResult(deleteResult, ui);
            }
            finally
            {
                RecordCancellationStage("PostOperationEnd");
            }

        }
        finally
        {
            if (!deleteMarksApplied)
            {
                RemoveMarksAfterFileOperation(deletedPaths);
            }
            try
            {
                RecordCancellationStage("FinalManifestStart");
                try
                {
                    if (manifestBatchStarted)
                    {
                        MidFdManagedTrashService.FlushManifestBatch();
                    }
                    manifestFlushSucceeded = true;
                }
                finally
                {
                    RecordCancellationStage("FinalManifestEnd");
                }
            }
            finally
            {
                try
                {
                    EndOperation(ui, permanent ? "完全削除完了" : "削除完了");
                }
                finally
                {
                    if (useManagedTrash && successCount > 0 && manifestFlushSucceeded)
                    {
                        _ = StartManagedTrashRetentionCleanupAsync("DeleteComplete");
                    }
                    RecordCancellationStage("DeleteComplete");
                    _activeManagedTrashDeleteDiagnostics = null;
                }
            }
        }
    }

    private void ExecuteCut(SelectionResult? snapshot, IFileOperationUiPort ui)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            return;
        }

        SelectionResult selection = ResolveSelection(snapshot, ui);
        if (selection.Count == 0)
        {
            ui.ShowStatus("切り取り対象がありません。");
            return;
        }

        ui.SetClipboardFileTransfer(selection.FullPaths, isCut: true);
        ui.ShowStatus($"{selection.Count} 件を切り取りました。");
    }

    private async Task ExecutePasteAsync(IFileOperationUiPort ui)
    {
        if (_activeOperationOrigin?.IsReadOnly == true)
        {
            ui.ShowStatus("このタブは ReadOnly のため、貼り付けは実行できません。");
            return;
        }

        string destinationDirectory = CurrentOperationPath;

        if (string.IsNullOrEmpty(destinationDirectory))
        {
            ui.ShowStatus("貼り付け先フォルダが指定されていません。");
            return;
        }

        bool hasImage = ui.TryGetClipboardImage(out var image);
        FileOperationClipboardTransfer? transfer = ui.ReadClipboardFileTransfer();
        bool hasFileDrop = transfer is { Paths.Count: > 0 };

        if (hasFileDrop && hasImage)
        {
            ClipboardPasteChoice choice = ui.ChooseClipboardPasteMode();
            if (choice == ClipboardPasteChoice.Cancel)
            {
                image?.Dispose();
                ui.ShowStatus("貼り付けはキャンセルされました。");
                return;
            }

            if (choice == ClipboardPasteChoice.ClipboardImage)
            {
                await ExecuteClipboardImagePasteAsync(image!, destinationDirectory, ui).ConfigureAwait(true);
                return;
            }

            image?.Dispose();
        }
        else if (!hasFileDrop && hasImage)
        {
            await ExecuteClipboardImagePasteAsync(image!, destinationDirectory, ui).ConfigureAwait(true);
            return;
        }
        else
        {
            image?.Dispose();
            if (!hasFileDrop)
            {
                if (ui.TryGetClipboardText(out string? text))
                {
                    await ExecuteClipboardTextPasteAsync(text, destinationDirectory, ui).ConfigureAwait(true);
                    return;
                }

                ui.ShowStatus("貼り付けできるファイルがありません。");
                return;
            }
        }

        FileOperationClipboardTransfer clipboard = transfer!.Value;
        IReadOnlyList<string> validPaths = clipboard.Paths
            .Where(path => !string.IsNullOrEmpty(path)
                && path != ".."
                && (File.Exists(path) || Directory.Exists(path)))
            .ToArray();
        if (validPaths.Count == 0)
        {
            ui.ShowStatus("貼り付けできるファイルがありません。");
            return;
        }
        if (clipboard.IsCut
            && validPaths.Count >= 2
            && !ui.ConfirmBulkPasteMove(validPaths, destinationDirectory))
        {
            ui.ShowStatus("貼り付けはキャンセルされました。");
            return;
        }

        var actions = new List<PlannedFileOperation>();
        bool renameSameDirectoryToAll = false;
        CopyCollisionDecision? pasteCollisionApplyToAll = null;
        DirectoryMergeDecision? pasteMergeApplyToAll = null;
        foreach (string sourcePath in validPaths)
        {
            string destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            if (!TryPlanActionsPaste(
                    sourcePath,
                    destinationPath,
                    clipboard.IsCut,
                    validPaths.Count,
                    ui,
                    ref renameSameDirectoryToAll,
                    ref pasteCollisionApplyToAll,
                    ref pasteMergeApplyToAll,
                    actions))
            {
                return;
            }
        }

        await ExecutePlannedPasteAsync(actions, clipboard.IsCut, destinationDirectory, ui).ConfigureAwait(true);
    }

    private async Task ExecuteClipboardImagePasteAsync(System.Drawing.Image image, string destinationDirectory, IFileOperationUiPort ui)
    {
        using (image)
        {
            try
            {
                string savedPath = await Task.Run(() =>
                    ClipboardImagePasteService.SavePngToDirectory(image, destinationDirectory)).ConfigureAwait(true);

                _undoRedo.RecordBatch(
                    FileOperationUndoRedoOperation.CreateFromPaste,
                    FileOperationUndoRedoService.CreateCreatedFilesBatch(new[] { savedPath }));

                bool selectCreated = _settings.Value.FileOperations?.SelectCreatedItemAfterCreate ?? true;
                string? focusTarget = selectCreated ? Path.GetFileName(savedPath) : null;
                ApplyOperationResult(new FileOperationResult(
                    "Paste",
                    FileOpExitStatus.Success,
                    1,
                    1,
                    focusTarget,
                    destinationDirectory,
                    shouldClearMarks: false,
                    customMessage: "画像を PNG として貼り付けました。Ctrl+Z で元に戻せます。"), ui);
            }
            catch (Exception ex)
            {
                ui.ShowUnexpectedError("画像貼り付け", ex);
            }
        }
    }

    private async Task ExecuteClipboardTextPasteAsync(string? text, string destinationDirectory, IFileOperationUiPort ui)
    {
        if (!(_settings.Value.FileOperations?.ClipboardPasteTextAsFileEnabled ?? false))
        {
            ui.ShowStatus("テキスト貼り付けファイル化は設定でOFFです。");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            ui.ShowStatus("空のテキストは貼り付けできません");
            return;
        }

        try
        {
            string savedPath = await Task.Run(() =>
            {
                Directory.CreateDirectory(destinationDirectory);
                string defaultName = $"clipboard_text_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                string fullPath = Path.Combine(destinationDirectory, defaultName);
                string uniquePath = File.Exists(fullPath) || Directory.Exists(fullPath)
                    ? FileOperationService.GetUniquePathStartingAtOne(fullPath)
                    : fullPath;
                File.WriteAllText(uniquePath, text, new System.Text.UTF8Encoding(false));
                return uniquePath;
            }).ConfigureAwait(true);

            _undoRedo.RecordBatch(
                FileOperationUndoRedoOperation.CreateFromPaste,
                FileOperationUndoRedoService.CreateCreatedFilesBatch(new[] { savedPath }));

            bool selectCreated = _settings.Value.FileOperations?.SelectCreatedItemAfterCreate ?? true;
            string? focusTarget = selectCreated ? Path.GetFileName(savedPath) : null;
            ApplyOperationResult(new FileOperationResult(
                "Paste",
                FileOpExitStatus.Success,
                1,
                1,
                focusTarget,
                destinationDirectory,
                shouldClearMarks: false,
                customMessage: "テキストをファイルとして貼り付けました。Ctrl+Z で元に戻せます。"), ui);
        }
        catch (Exception ex)
        {
            ui.ShowUnexpectedError("テキスト貼り付け", ex);
        }
    }

    private async Task ExecutePlannedPasteAsync(
        IReadOnlyList<PlannedFileOperation> actions,
        bool isMove,
        string destinationDirectory,
        IFileOperationUiPort ui)
    {
        CancellationToken token = BeginOperation(isMove ? "貼り付け(移動)" : "貼り付け(コピー)");
        ui.BeginOperation(isMove ? "貼り付け(移動)" : "貼り付け(コピー)", actions.Count);
        int successCount = 0;
        int skipCount = actions.Count(action => action.Skip);
        int failCount = 0;
        var moveUndo = new List<(string SourcePath, string DestinationPath)>();
        var createdFiles = new List<string>();
        var pastedMoveSourcePaths = new List<string>();
        string? focusTarget = null;
        bool pasteMoveMarksApplied = false;
        try
        {
            await Task.Run(() =>
            {
                for (int index = 0; index < actions.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    PlannedFileOperation action = actions[index];
                    try
                    {
                        if (!action.Skip)
                        {
                            if (action.IsDirectoryMergeRoot)
                            {
                                // The merge root already exists; child actions carry the actual work.
                            }
                            else if (action.IsDirectory)
                            {
                                FileOperationService.CreateDirectoryForUserMutation(action.DestinationPath);
                            }
                            else if (isMove)
                            {
                                FileOperationService.Move(action.SourcePath, action.DestinationPath, action.Overwrite, cancellationToken: token);
                                if (!action.IsDirectoryMergeRoot)
                                {
                                    moveUndo.Add((action.SourcePath, action.DestinationPath));
                                }
                            }
                            else
                            {
                                FileOperationService.Copy(action.SourcePath, action.DestinationPath);
                                createdFiles.Add(action.DestinationPath);
                            }
                            successCount++;
                            focusTarget ??= Path.GetFileName(action.DestinationPath);
                            if (isMove)
                            {
                                pastedMoveSourcePaths.Add(action.SourcePath);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        ui.ShowError("貼り付け", Path.GetFileName(action.SourcePath), ex.Message);
                        break;
                    }
                    finally
                    {
                        ui.ReportProgress(new FileOperationProgress(index + 1, actions.Count, Path.GetFileName(action.DestinationPath)));
                    }
                }
            }, token).ConfigureAwait(true);

            if (isMove)
            {
                RemoveMarksAfterFileOperation(pastedMoveSourcePaths);
                pasteMoveMarksApplied = true;
            }

            if (isMove)
            {
                foreach (string sourceRoot in actions
                    .Where(action => action.IsDirectoryMergeRoot)
                    .Select(action => action.SourcePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    FileOperationService.DeleteEmptyDirectoriesBottomUp(sourceRoot);
                }
            }

            if (isMove && moveUndo.Count > 0 && failCount == 0 && skipCount == 0)
            {
                _undoRedo.RecordBatch(FileOperationUndoRedoOperation.Move, FileOperationUndoRedoService.CreateMoveBatch(moveUndo));
            }
            else if (!isMove && createdFiles.Count > 0 && failCount == 0)
            {
                _undoRedo.RecordBatch(FileOperationUndoRedoOperation.CreateFromPaste, FileOperationUndoRedoService.CreateCreatedFilesBatch(createdFiles));
            }

            FileOpExitStatus status = FileOperationPresentationHelper.NormalizeExitStatus(
                failCount > 0 ? FileOpExitStatus.Error : FileOpExitStatus.Success,
                successCount,
                actions.Count,
                skipCount,
                failCount);
            bool selectCreated = _settings.Value.FileOperations?.SelectCreatedItemAfterCreate ?? true;
            if (!isMove && !selectCreated)
            {
                focusTarget = null;
            }
            ApplyOperationResult(new FileOperationResult(
                "Paste",
                status,
                successCount,
                actions.Count,
                focusTarget,
                destinationDirectory,
                shouldClearMarks: false,
                skipCount: skipCount,
                failCount: failCount), ui);
        }
        catch (OperationCanceledException)
        {
            if (isMove)
            {
                RemoveMarksAfterFileOperation(pastedMoveSourcePaths);
                pasteMoveMarksApplied = true;
            }

            ApplyOperationResult(new FileOperationResult(
                "Paste",
                FileOpExitStatus.Canceled,
                successCount,
                actions.Count,
                focusTarget,
                destinationDirectory,
                shouldClearMarks: false,
                skipCount: skipCount,
                failCount: failCount), ui);
        }
        finally
        {
            if (isMove && !pasteMoveMarksApplied)
            {
                RemoveMarksAfterFileOperation(pastedMoveSourcePaths);
            }
            EndOperation(ui, "貼り付け完了");
        }
    }

    private async Task ExecuteUndoRedoAsync(bool undo, IFileOperationUiPort ui)
    {
        bool available = undo
            ? _undoRedo.TryPeekUndo(out FileOperationUndoRedoBatch batch)
            : _undoRedo.TryPeekRedo(out batch);
        if (!available || batch.Items.Count == 0)
        {
            ui.ShowStatus(undo ? "元に戻せるファイル操作がありません" : "やり直せるファイル操作がありません");
            return;
        }

        bool isManagedTrashUndoRedo = batch.Operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash;
        CancellationToken token = BeginOperation(
            undo ? "元に戻す" : "やり直し",
            canCancel: !isManagedTrashUndoRedo);
        ui.BeginOperation(undo ? "元に戻す" : "やり直し", batch.Items.Count);
        try
        {
            (bool success, string? focusTarget, string? error) = await Task.Run(() => TryApplyUndoRedoBatch(batch, undo, token, ui), token).ConfigureAwait(true);
            if (!success)
            {
                ui.ShowStatus(error ?? (undo ? "ファイル操作を元に戻せませんでした。" : "ファイル操作をやり直せませんでした。"));
                return;
            }

            if (undo) _undoRedo.CommitUndo(); else _undoRedo.CommitRedo();
            ApplyOperationResult(new FileOperationResult(
                undo ? "Undo" : "Redo",
                FileOpExitStatus.Success,
                batch.Items.Count,
                batch.Items.Count,
                focusTarget ?? batch.Items.FirstOrDefault()?.BeforeName), ui);
        }
        catch (Exception ex)
        {
            ui.ShowUnexpectedError(undo ? "元に戻す" : "やり直し", ex);
        }
        finally
        {
            EndOperation(ui, undo ? "元に戻しました" : "やり直しました");
        }
    }

    private (bool Success, string? FocusTargetName, string? ErrorMessage) TryApplyUndoRedoBatch(
        FileOperationUndoRedoBatch batch,
        bool undo,
        CancellationToken token,
        IFileOperationUiPort ui)
    {
        if (batch.Items.Count == 0)
        {
            return (false, null, "Undo/Redo 履歴が空です。");
        }

        if (batch.Operation == FileOperationUndoRedoOperation.CreateFromPaste)
        {
            if (!undo)
            {
                return (false, null, "この貼り付けUndoはやり直しに対応していません。");
            }

            foreach (FileOperationUndoRedoItem item in batch.Items)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(item.BeforePath))
                {
                    return (false, null, $"対象が見つからないため続行できません: {item.BeforePath}");
                }

                var info = new FileInfo(item.BeforePath);
                if (info.Length != item.CreatedFileLength || info.LastWriteTimeUtc.Ticks != item.CreatedFileLastWriteTimeUtcTicks)
                {
                    return (false, null, $"作成後に変更されたため続行できません: {item.BeforePath}");
                }
            }

            try
            {
                foreach (FileOperationUndoRedoItem item in batch.Items)
                {
                    token.ThrowIfCancellationRequested();
                    FileOperationService.Delete(item.BeforePath);
                }
            }
            catch (Exception ex)
            {
                _undoRedo.Reset();
                return (false, null, $"{ex.Message} (履歴は安全側で破棄しました)");
            }

            return (true, null, null);
        }

        if (batch.Operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash)
        {
            return TryApplyManagedTrashUndoRedoBatch(batch, undo, token);
        }

        var operations = batch.Items
            .Select(item => undo
                ? (CurrentPath: item.AfterPath, TargetPath: item.BeforePath, TargetName: item.BeforeName)
                : (CurrentPath: item.BeforePath, TargetPath: item.AfterPath, TargetName: item.AfterName))
            .ToList();

        foreach (var operation in operations)
        {
            token.ThrowIfCancellationRequested();
            if (!ReparsePointHelper.Exists(operation.CurrentPath))
            {
                return (false, null, $"対象が見つからないため続行できません: {operation.CurrentPath}");
            }
            if (ReparsePointHelper.Exists(operation.TargetPath))
            {
                return (false, null, $"同名の項目があるため続行できません: {operation.TargetPath}");
            }
        }

        try
        {
            foreach (var operation in Enumerable.Reverse(operations))
            {
                token.ThrowIfCancellationRequested();
                if (batch.Operation == FileOperationUndoRedoOperation.Rename)
                {
                    FileOperationService.Rename(operation.CurrentPath, operation.TargetPath);
                }
                else
                {
                    FileOperationService.Move(operation.CurrentPath, operation.TargetPath, overwrite: false);
                }
            }
        }
        catch (Exception ex)
        {
            _undoRedo.Reset();
            return (false, null, $"{ex.Message} (履歴は安全側で破棄しました)");
        }

        string? focusTargetName = operations
            .Select(operation => operation.TargetPath)
            .FirstOrDefault(path => string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                NavigationService.NormalizeDirectoryForCompare(CurrentOperationPath),
                StringComparison.OrdinalIgnoreCase)) is string focusPath
                ? Path.GetFileName(focusPath)
                : null;

        return (true, focusTargetName, null);
    }

    private (bool Success, string? FocusTargetName, string? ErrorMessage) TryApplyManagedTrashUndoRedoBatch(
        FileOperationUndoRedoBatch batch,
        bool undo,
        CancellationToken token)
    {
            if (undo)
            {
                foreach (FileOperationUndoRedoItem item in batch.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(item.BeforePath) || string.IsNullOrWhiteSpace(item.RecycleBinPath))
                    {
                        return (false, null, "管理ゴミ箱の復元情報が不完全です。");
                    }
                    if (ReparsePointHelper.Exists(item.BeforePath))
                    {
                        return (false, null, $"復元先に同名項目があるため続行できません: {item.BeforePath}");
                    }
                    if (!ReparsePointHelper.Exists(item.RecycleBinPath))
                    {
                        return (false, null, $"管理ゴミ箱内の項目が見つからないため続行できません: {item.RecycleBinPath}");
                    }
                }
            }
            else
            {
                foreach (FileOperationUndoRedoItem item in batch.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ReparsePointHelper.Exists(item.BeforePath))
                    {
                        return (false, null, $"再削除対象が見つからないため続行できません: {item.BeforePath}");
                    }
                }
            }

        var restoredTrashPaths = new List<string>();
        var reDeletedRecords = new List<TrashManifestRecord>();
        bool manifestBatchStarted = false;
        bool success = false;
        string? errorMessage = null;
        try
        {
            token.ThrowIfCancellationRequested();
            MidFdManagedTrashService.BeginManifestBatch();
            manifestBatchStarted = true;
            MidFdManagedTrashService.SetLoggingSuppression(true);
            ManagedTrashRestoreLookup? restoreLookup = undo
                ? MidFdManagedTrashService.CreateRestoreLookup(_managedTrashRestoreDiagnostics)
                : null;

            foreach (FileOperationUndoRedoItem item in batch.Items)
            {
                if (undo)
                {
                    if (_managedTrashRestoreDiagnostics is ManagedTrashRestoreDiagnostics diagnostics)
                    {
                        MidFdManagedTrashService.RestoreFromTrashWithDiagnostics(
                            item,
                            skipStatusUpdate: true,
                            suppressLogging: true,
                            diagnostics,
                            restoreLookup);
                    }
                    else
                    {
                        MidFdManagedTrashService.RestoreFromTrashWithLookup(
                            item,
                            skipStatusUpdate: true,
                            suppressLogging: true,
                            restoreLookup!);
                    }
                    restoredTrashPaths.Add(item.RecycleBinPath);
                }
                else
                {
                    MidFdManagedTrashService.RedoDeleteToTrash(
                        item,
                        out TrashManifestRecord? record,
                        skipRegistration: true,
                        suppressLogging: true);
                    if (record == null)
                    {
                        throw new InvalidOperationException("管理ゴミ箱の再削除manifest recordを生成できませんでした。");
                    }
                    reDeletedRecords.Add(record);
                }
            }
            success = true;
        }
        catch (OperationCanceledException ex)
        {
            errorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            if (manifestBatchStarted)
            {
                try
                {
                    if (restoredTrashPaths.Count > 0)
                    {
                        int updated = 0;
                        var batchStatusSw = Stopwatch.StartNew();
                        try
                        {
                            updated = MidFdManagedTrashService.UpdateRecordStatuses(
                                restoredTrashPaths,
                                TrashRecordStatus.Restored);
                            if (updated != restoredTrashPaths.Count)
                            {
                                success = false;
                                errorMessage ??= $"管理ゴミ箱status更新数が一致しません。expected={restoredTrashPaths.Count}, actual={updated}";
                            }
                        }
                        finally
                        {
                            batchStatusSw.Stop();
                            _managedTrashRestoreDiagnostics?.RecordBatchStatusUpdate(
                                batchStatusSw.ElapsedMilliseconds,
                                updated);
                        }
                    }

                    if (reDeletedRecords.Count > 0)
                    {
                        MidFdManagedTrashService.RegisterNewTrashRecordsPublic(reDeletedRecords);
                    }

                    MidFdManagedTrashService.SaveActiveBatch();
                }
                catch (Exception ex)
                {
                    success = false;
                    errorMessage ??= ex.Message;
                }

                try
                {
                    MidFdManagedTrashService.FlushManifestBatch();
                }
                catch (Exception ex)
                {
                    success = false;
                    errorMessage ??= ex.Message;
                }
            }

            MidFdManagedTrashService.SetLoggingSuppression(false);
        }

        if (!success)
        {
            _undoRedo.Reset();
            return (false, null, $"{errorMessage ?? "管理ゴミ箱のUndo/Redoに失敗しました。"} (履歴は安全側で破棄しました)");
        }

        string? trashFocusTarget = batch.Items
            .Select(item => item.BeforePath)
            .FirstOrDefault(path => string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                NavigationService.NormalizeDirectoryForCompare(CurrentOperationPath),
                StringComparison.OrdinalIgnoreCase)) is string focusPath
                ? Path.GetFileName(focusPath)
                : null;

        return (true, trashFocusTarget, null);
    }

    private bool TryPlanActions(
        string sourcePath,
        string destinationPath,
        bool isMove,
        int selectionCount,
        IFileOperationUiPort ui,
        ref bool renameSameDirectoryToAll,
        ref CopyCollisionDecision? copyCollisionApplyToAll,
        ref DirectoryMergeDecision? directoryMergeApplyToAll,
        ICollection<PlannedFileOperation> actions)
    {
        if (!isMove
            && string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(sourcePath) ?? string.Empty),
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(destinationPath) ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            string suggestedPath = FileOperationService.GetUniquePath(destinationPath);
            if (!renameSameDirectoryToAll)
            {
                FileOperationSameDirectoryCopyDecision decision = ui.ChooseSameDirectoryCopy(
                    sourcePath,
                    suggestedPath,
                    showApplyToAll: selectionCount > 1) switch
                {
                    FileOperationSameDirectoryCopyAnswer.CopyWithUniqueName => FileOperationSameDirectoryCopyDecision.CopyWithUniqueName,
                    FileOperationSameDirectoryCopyAnswer.CopyWithUniqueNameForAll => FileOperationSameDirectoryCopyDecision.CopyWithUniqueNameForAll,
                    FileOperationSameDirectoryCopyAnswer.Skip => FileOperationSameDirectoryCopyDecision.Skip,
                    _ => FileOperationSameDirectoryCopyDecision.Cancel
                };
                if (decision == FileOperationSameDirectoryCopyDecision.Cancel)
                {
                    return false;
                }
                if (decision == FileOperationSameDirectoryCopyDecision.Skip)
                {
                    actions.Add(new PlannedFileOperation(
                        sourcePath,
                        destinationPath,
                        Skip: true,
                        Overwrite: false));
                    return true;
                }
                renameSameDirectoryToAll = decision == FileOperationSameDirectoryCopyDecision.CopyWithUniqueNameForAll;
            }

            actions.Add(new PlannedFileOperation(
                sourcePath,
                FileOperationService.GetUniquePath(destinationPath),
                Skip: false,
                Overwrite: false));
            return true;
        }

        if (ReparsePointHelper.Exists(destinationPath)
            && FileOperationService.IsDirectoryContainerPath(sourcePath)
            && FileOperationService.IsDirectoryContainerPath(destinationPath))
        {
            FileOperationDirectoryMergePolicy merge = ui.ChooseDirectoryMerge(sourcePath, destinationPath, isMove, ref directoryMergeApplyToAll) switch
            {
                FileOperationDirectoryMergeAnswer.Merge => FileOperationDirectoryMergePolicy.Merge,
                FileOperationDirectoryMergeAnswer.Skip => FileOperationDirectoryMergePolicy.Skip,
                _ => FileOperationDirectoryMergePolicy.Cancel
            };
            if (merge == FileOperationDirectoryMergePolicy.Cancel)
            {
                return false;
            }
            if (merge == FileOperationDirectoryMergePolicy.Skip)
            {
                actions.Add(new PlannedFileOperation(
                    sourcePath,
                    destinationPath,
                    Skip: true,
                    Overwrite: false,
                    IsDirectory: true,
                    IsDirectoryMergeRoot: true));
                return true;
            }

            // Directory merge is expanded into typed child actions so each existing
            // file still receives the current collision decision and partial result.
            actions.Add(new PlannedFileOperation(
                sourcePath,
                destinationPath,
                Skip: false,
                Overwrite: false,
                IsDirectory: true,
                IsDirectoryMergeRoot: true));
            foreach (DirectoryCopyPlanEntry entry in FileOperationService.BuildDirectoryCopyPlan(sourcePath, destinationPath))
            {
                if (entry.IsDirectory)
                {
                    actions.Add(new PlannedFileOperation(
                        entry.SourcePath,
                        entry.DestinationPath,
                        Skip: false,
                        Overwrite: false,
                        IsDirectory: true,
                        IsDirectoryMergeRoot: false));
                    continue;
                }

                string childDestination = entry.DestinationPath;
                if (!TryPlanAction(
                        entry.SourcePath,
                        ref childDestination,
                        isMove,
                        ui,
                        ref copyCollisionApplyToAll,
                        ref directoryMergeApplyToAll,
                        out PlannedFileOperation childAction,
                        allowDirectoryMerge: false))
                {
                    return false;
                }
                actions.Add(childAction);
            }
            return true;
        }

        string effectiveDestination = destinationPath;
        if (!TryPlanAction(
                sourcePath,
                ref effectiveDestination,
                isMove,
                ui,
                ref copyCollisionApplyToAll,
                ref directoryMergeApplyToAll,
                out PlannedFileOperation action))
        {
            return false;
        }
        actions.Add(action);
        return true;
    }

    /// <summary>
    /// Paste専用のaction planニング。旧TryBuildPasteFinalPlanの契約を復元し、
    /// PasteCollisionDialog / PasteFolderMergeDialogを使用してApplyToAll情報を保持する。
    /// </summary>
    private bool TryPlanActionsPaste(
        string sourcePath,
        string destinationPath,
        bool isCut,
        int selectionCount,
        IFileOperationUiPort ui,
        ref bool renameSameDirectoryToAll,
        ref CopyCollisionDecision? pasteCollisionApplyToAll,
        ref DirectoryMergeDecision? pasteMergeApplyToAll,
        ICollection<PlannedFileOperation> actions)
    {
        // 同ディレクトリへの貼り付けはChooseSameDirectoryCopy（共通ダイアログ）
        if (!isCut
            && string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(sourcePath) ?? string.Empty),
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(destinationPath) ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            string suggestedPath = FileOperationService.GetUniquePath(destinationPath);
            if (!renameSameDirectoryToAll)
            {
                FileOperationSameDirectoryCopyDecision decision = ui.ChooseSameDirectoryCopy(
                    sourcePath,
                    suggestedPath,
                    showApplyToAll: selectionCount > 1) switch
                {
                    FileOperationSameDirectoryCopyAnswer.CopyWithUniqueName => FileOperationSameDirectoryCopyDecision.CopyWithUniqueName,
                    FileOperationSameDirectoryCopyAnswer.CopyWithUniqueNameForAll => FileOperationSameDirectoryCopyDecision.CopyWithUniqueNameForAll,
                    FileOperationSameDirectoryCopyAnswer.Skip => FileOperationSameDirectoryCopyDecision.Skip,
                    _ => FileOperationSameDirectoryCopyDecision.Cancel
                };
                if (decision == FileOperationSameDirectoryCopyDecision.Cancel)
                {
                    return false;
                }
                if (decision == FileOperationSameDirectoryCopyDecision.Skip)
                {
                    actions.Add(new PlannedFileOperation(sourcePath, destinationPath, Skip: true, Overwrite: false));
                    return true;
                }
                renameSameDirectoryToAll = decision == FileOperationSameDirectoryCopyDecision.CopyWithUniqueNameForAll;
            }

            actions.Add(new PlannedFileOperation(
                sourcePath,
                FileOperationService.GetUniquePath(destinationPath),
                Skip: false,
                Overwrite: false));
            return true;
        }

        // 既存directory mergeはPaste専用dialog（isCut文言 + ApplyToAll）
        if (ReparsePointHelper.Exists(destinationPath)
            && FileOperationService.IsDirectoryContainerPath(sourcePath)
            && FileOperationService.IsDirectoryContainerPath(destinationPath))
        {
            FileOperationDirectoryMergePolicy merge;
            if (pasteMergeApplyToAll != null)
            {
                merge = pasteMergeApplyToAll.Policy switch
                {
                    DirectoryMergePolicy.Merge => FileOperationDirectoryMergePolicy.Merge,
                    DirectoryMergePolicy.Skip => FileOperationDirectoryMergePolicy.Skip,
                    _ => FileOperationDirectoryMergePolicy.Cancel
                };
            }
            else
            {
                FileOperationPasteDirectoryMergeAnswer mergeAnswer =
                    ui.ChoosePasteDirectoryMerge(sourcePath, destinationPath, isCut, ref pasteMergeApplyToAll);
                merge = mergeAnswer.Policy switch
                {
                    FileOperationDirectoryMergeAnswer.Merge => FileOperationDirectoryMergePolicy.Merge,
                    FileOperationDirectoryMergeAnswer.Skip => FileOperationDirectoryMergePolicy.Skip,
                    _ => FileOperationDirectoryMergePolicy.Cancel
                };
            }

            if (merge == FileOperationDirectoryMergePolicy.Cancel)
            {
                return false;
            }
            if (merge == FileOperationDirectoryMergePolicy.Skip)
            {
                actions.Add(new PlannedFileOperation(
                    sourcePath,
                    destinationPath,
                    Skip: true,
                    Overwrite: false,
                    IsDirectory: true,
                    IsDirectoryMergeRoot: true));
                return true;
            }

            actions.Add(new PlannedFileOperation(
                sourcePath,
                destinationPath,
                Skip: false,
                Overwrite: false,
                IsDirectory: true,
                IsDirectoryMergeRoot: true));
            foreach (DirectoryCopyPlanEntry entry in FileOperationService.BuildDirectoryCopyPlan(sourcePath, destinationPath))
            {
                if (entry.IsDirectory)
                {
                    actions.Add(new PlannedFileOperation(
                        entry.SourcePath,
                        entry.DestinationPath,
                        Skip: false,
                        Overwrite: false,
                        IsDirectory: true,
                        IsDirectoryMergeRoot: false));
                    continue;
                }

                string childDestination = entry.DestinationPath;
                if (!TryPlanActionPaste(
                        entry.SourcePath,
                        ref childDestination,
                        isCut,
                        ui,
                        ref pasteCollisionApplyToAll,
                        out PlannedFileOperation childAction,
                        allowDirectoryMerge: false))
                {
                    return false;
                }
                actions.Add(childAction);
            }
            return true;
        }

        string effectiveDestination2 = destinationPath;
        if (!TryPlanActionPaste(sourcePath, ref effectiveDestination2, isCut, ui, ref pasteCollisionApplyToAll, out PlannedFileOperation action2))
        {
            return false;
        }
        actions.Add(action2);
        return true;
    }

    /// <summary>
    /// Paste専用のsingle action planニング。PasteCollisionDialogを使用してisCut文言とApplyToAll情報を保持する。
    /// </summary>
    private bool TryPlanActionPaste(
        string sourcePath,
        ref string destinationPath,
        bool isCut,
        IFileOperationUiPort ui,
        ref CopyCollisionDecision? applyToAllDecision,
        out PlannedFileOperation action,
        bool allowDirectoryMerge = true)
    {
        action = new PlannedFileOperation(sourcePath, destinationPath, Skip: false, Overwrite: false);
        if (string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            action = action with { Skip = true };
            return true;
        }
        if (!ReparsePointHelper.Exists(destinationPath))
        {
            return true;
        }

        bool sourceIsDirectory = FileOperationService.IsDirectoryContainerPath(sourcePath);
        bool destinationIsDirectory = FileOperationService.IsDirectoryContainerPath(destinationPath);
        if (sourceIsDirectory != destinationIsDirectory)
        {
            ui.ShowTypeMismatchConflict(destinationPath);
            action = action with { Skip = true };
            return true;
        }

        if (allowDirectoryMerge && sourceIsDirectory && destinationIsDirectory)
        {
            // directoryはTryPlanActionsPasteで既に処理済みのため、ここではSkipとして扱う
            action = action with { Skip = true };
            return true;
        }

        // Paste専用collision dialog（PasteCollisionDialog）を使用
        FileOperationCollisionPolicy policy;
        if (applyToAllDecision != null)
        {
            policy = applyToAllDecision.Policy switch
            {
                CopyCollisionPolicy.Skip => FileOperationCollisionPolicy.Skip,
                CopyCollisionPolicy.Overwrite => FileOperationCollisionPolicy.Overwrite,
                CopyCollisionPolicy.RenameCopy => FileOperationCollisionPolicy.RenameCopy,
                CopyCollisionPolicy.NewerOnly => FileOperationCollisionPolicy.NewerOnly,
                _ => FileOperationCollisionPolicy.Cancel
            };
        }
        else
        {
            bool allowRename = !isCut;
            FileOperationPasteCollisionAnswer pasteAnswer =
                ui.ChoosePasteCollision(sourcePath, destinationPath, allowRename, isCut, ref applyToAllDecision);
            policy = pasteAnswer.Policy switch
            {
                FileOperationCollisionAnswer.Skip => FileOperationCollisionPolicy.Skip,
                FileOperationCollisionAnswer.Overwrite => FileOperationCollisionPolicy.Overwrite,
                FileOperationCollisionAnswer.RenameCopy => FileOperationCollisionPolicy.RenameCopy,
                FileOperationCollisionAnswer.NewerOnly => FileOperationCollisionPolicy.NewerOnly,
                _ => FileOperationCollisionPolicy.Cancel
            };
        }

        switch (policy)
        {
            case FileOperationCollisionPolicy.Skip:
                action = action with { Skip = true };
                return true;
            case FileOperationCollisionPolicy.Overwrite:
                action = action with { Overwrite = true };
                return true;
            case FileOperationCollisionPolicy.RenameCopy:
                destinationPath = FileOperationService.GetUniquePath(destinationPath);
                action = action with { DestinationPath = destinationPath };
                return true;
            case FileOperationCollisionPolicy.NewerOnly:
                if (File.GetLastWriteTimeUtc(sourcePath) <= File.GetLastWriteTimeUtc(destinationPath))
                {
                    action = action with { Skip = true };
                }
                return true;
            default:
                return false;
        }
    }

    private bool TryPlanAction(
        string sourcePath,
        ref string destinationPath,
        bool isMove,
        IFileOperationUiPort ui,
        ref CopyCollisionDecision? applyToAllDecision,
        ref DirectoryMergeDecision? directoryMergeApplyToAll,
        out PlannedFileOperation action,
        bool allowDirectoryMerge = true)
    {
        action = new PlannedFileOperation(sourcePath, destinationPath, Skip: false, Overwrite: false);
        if (string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            action = action with { Skip = true };
            return true;
        }
        if (!ReparsePointHelper.Exists(destinationPath))
        {
            return true;
        }

        bool sourceIsDirectory = FileOperationService.IsDirectoryContainerPath(sourcePath);
        bool destinationIsDirectory = FileOperationService.IsDirectoryContainerPath(destinationPath);
        if (sourceIsDirectory != destinationIsDirectory)
        {
            ui.ShowTypeMismatchConflict(destinationPath);
            action = action with { Skip = true };
            return true;
        }

        if (allowDirectoryMerge && sourceIsDirectory && destinationIsDirectory)
        {
            FileOperationDirectoryMergePolicy merge = ui.ChooseDirectoryMerge(sourcePath, destinationPath, isMove, ref directoryMergeApplyToAll) switch
            {
                FileOperationDirectoryMergeAnswer.Skip => FileOperationDirectoryMergePolicy.Skip,
                FileOperationDirectoryMergeAnswer.Merge => FileOperationDirectoryMergePolicy.Merge,
                _ => FileOperationDirectoryMergePolicy.Cancel
            };
            if (merge == FileOperationDirectoryMergePolicy.Cancel)
            {
                return false;
            }
            action = action with { Skip = merge == FileOperationDirectoryMergePolicy.Skip };
            return true;
        }

        FileOperationCollisionPolicy policy;
        if (applyToAllDecision != null)
        {
            policy = applyToAllDecision.Policy switch
            {
                CopyCollisionPolicy.Skip => FileOperationCollisionPolicy.Skip,
                CopyCollisionPolicy.Overwrite => FileOperationCollisionPolicy.Overwrite,
                CopyCollisionPolicy.RenameCopy when !isMove => FileOperationCollisionPolicy.RenameCopy,
                CopyCollisionPolicy.NewerOnly => FileOperationCollisionPolicy.NewerOnly,
                _ => FileOperationCollisionPolicy.Cancel
            };
        }
        else
        {
            FileOperationCollisionAnswer answer = ui.ChooseCollision(sourcePath, destinationPath, allowRename: !isMove, isMove, ref applyToAllDecision);
            policy = answer switch
            {
                FileOperationCollisionAnswer.Skip => FileOperationCollisionPolicy.Skip,
                FileOperationCollisionAnswer.Overwrite => FileOperationCollisionPolicy.Overwrite,
                FileOperationCollisionAnswer.RenameCopy => FileOperationCollisionPolicy.RenameCopy,
                FileOperationCollisionAnswer.NewerOnly => FileOperationCollisionPolicy.NewerOnly,
                _ => FileOperationCollisionPolicy.Cancel
            };
        }
        switch (policy)
        {
            case FileOperationCollisionPolicy.Skip:
                action = action with { Skip = true };
                return true;
            case FileOperationCollisionPolicy.Overwrite:
                action = action with { Overwrite = true };
                return true;
            case FileOperationCollisionPolicy.RenameCopy:
                destinationPath = FileOperationService.GetUniquePath(destinationPath);
                action = action with { DestinationPath = destinationPath };
                return true;
            case FileOperationCollisionPolicy.NewerOnly:
                if (File.GetLastWriteTimeUtc(sourcePath) <= File.GetLastWriteTimeUtc(destinationPath))
                {
                    action = action with { Skip = true };
                }
                return true;
            default:
                return false;
        }
    }

    private CancellationToken BeginOperation(string operationName, bool canCancel = true)
    {
        _fileState.Ui.Reset();
        _fileState.IsClipboardBusy = true;
        _fileState.Ui.Cts = new CancellationTokenSource();
        _fileState.Ui.CanCancel = canCancel;
        _fileState.Ui.ActiveOperationName = operationName;
        return _fileState.Ui.Cts.Token;
    }

    private void EndOperation(IFileOperationUiPort ui, string message)
    {
        try
        {
            ui.CompleteOperation(message);
        }
        finally
        {
            _fileState.Ui.Reset();
            _fileState.IsClipboardBusy = false;
            ui.RefreshOperationUi();
        }
    }

    private static void NotifyCompletion(Action? completionCallback)
    {
        if (completionCallback == null)
        {
            return;
        }

        try
        {
            completionCallback();
        }
        catch (Exception ex)
        {
            LogService.Error("FileOperation completion callback failed.", ex);
        }
    }

    private SelectionResult ResolveSelection(SelectionResult? snapshot, IFileOperationUiPort ui)
    {
        if (snapshot != null)
        {
            return snapshot;
        }

        FileOperationSelectionContext context = ui.ReadSelectionContext();
        return _selection.Resolve(context.CurrentPath, context.IsParentEntry);
    }

    private FileOperationCommandRequest CaptureOperationOrigin(
        FileOperationCommandRequest request,
        IFileOperationUiPort ui)
    {
        if (request.Origin.HasValue)
        {
            return request;
        }

        BrowserTabState? activeTab = _browser.Workspace.ActiveTabSnapshot;
        FileOperationSelectionContext selectionContext = ui.ReadSelectionContext();
        SelectionResult resolvedSelection = request.SelectionSnapshot ?? ResolveSelection(null, ui);
        var selectionSnapshot = new SelectionResult(
            resolvedSelection.FullPaths,
            resolvedSelection.HasMarkedSelection);
        IReadOnlyList<string> markedPaths = _selection.Snapshot().ToList();
        var origin = new FileOperationOriginSnapshot(
            _browser.Workspace.ActiveCategoryId,
            activeTab?.Id ?? Guid.Empty,
            activeTab?.CurrentPath ?? _browser.CurrentPath,
            activeTab?.IsReadOnly == true,
            selectionSnapshot,
            markedPaths,
            selectionContext);
        return request with
        {
            SelectionSnapshot = selectionSnapshot,
            Origin = origin
        };
    }

    private string CurrentOperationPath =>
        _activeOperationOrigin?.CurrentPath ?? _browser.CurrentPath;

    private bool IsOperationOriginCurrentTab()
    {
        if (_activeOperationOrigin is not { } origin || origin.TabId == Guid.Empty)
        {
            return true;
        }

        BrowserTabState? activeTab = _browser.Workspace.ActiveTabSnapshot;
        return activeTab?.Id == origin.TabId &&
            string.Equals(
                _browser.Workspace.ActiveCategoryId,
                origin.CategoryId,
                StringComparison.OrdinalIgnoreCase);
    }

    private bool IsOperationOriginCurrentView()
    {
        if (!IsOperationOriginCurrentTab() || _activeOperationOrigin is not { } origin || origin.TabId == Guid.Empty)
        {
            return IsOperationOriginCurrentTab();
        }

        BrowserTabState? activeTab = _browser.Workspace.ActiveTabSnapshot;
        return activeTab != null && string.Equals(
            NavigationService.NormalizeDirectoryForCompare(activeTab.CurrentPath),
            NavigationService.NormalizeDirectoryForCompare(origin.CurrentPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool TryResolveMultiMarkSelectionAction(
        string operationName,
        string cancelStatusMessage,
        SelectionResult selection,
        IFileOperationUiPort ui,
        out SelectionResult effectiveSelection)
    {
        effectiveSelection = selection;
        if (!selection.HasMarkedSelection || selection.Count <= 1)
        {
            return true;
        }

        FileOperationOriginSnapshot? origin = _activeOperationOrigin;
        FileOperationSelectionContext context = origin?.SelectionContext ?? ui.ReadSelectionContext();
        IReadOnlyList<string> markedPaths = origin?.MarkedPaths ?? _selection.Snapshot();
        if (markedPaths.Count <= 1 ||
            context.IsParentEntry ||
            string.IsNullOrWhiteSpace(context.CurrentPath) ||
            markedPaths.Contains(context.CurrentPath, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        string currentDirectory = origin?.CurrentPath ?? _browser.CurrentPath;
        if (string.IsNullOrWhiteSpace(currentDirectory))
        {
            currentDirectory = Path.GetDirectoryName(context.CurrentPath) ?? string.Empty;
        }

        string normalizedCurrentDirectory = NavigationService.NormalizeDirectoryForCompare(currentDirectory);
        bool hasMarkedPathOutsideCurrentDirectory = markedPaths.Any(path =>
        {
            string? parentDirectory = Path.GetDirectoryName(path);
            return !string.Equals(
                NavigationService.NormalizeDirectoryForCompare(parentDirectory ?? string.Empty),
                normalizedCurrentDirectory,
                StringComparison.OrdinalIgnoreCase);
        });
        if (!string.IsNullOrWhiteSpace(currentDirectory) && !hasMarkedPathOutsideCurrentDirectory)
        {
            _ = ShouldBypassRecentMultiMarkIntent();
            return true;
        }

        string currentName = string.IsNullOrWhiteSpace(context.CurrentName)
            ? Path.GetFileName(context.CurrentPath)
            : context.CurrentName;
        FileOperationMultiMarkSelectionAction action = ui.ChooseMultiMarkSelection(
            operationName,
            currentName,
            selection.Count);
        switch (action)
        {
            case FileOperationMultiMarkSelectionAction.CurrentOnly:
                effectiveSelection = new SelectionResult(new[] { context.CurrentPath }, false);
                return true;
            case FileOperationMultiMarkSelectionAction.MarkedAll:
                return true;
            default:
                ui.ShowStatus(cancelStatusMessage);
                return false;
        }
    }

    private bool ShouldBypassRecentMultiMarkIntent()
    {
        if (!_selection.RecentMultiMarkIntentActive ||
            string.IsNullOrWhiteSpace(_browser.CurrentPath) ||
            !string.Equals(
                _selection.RecentMultiMarkIntentDirectory,
                NavigationService.NormalizeDirectoryForCompare(_browser.CurrentPath),
                StringComparison.OrdinalIgnoreCase) ||
            _selection.RecentMultiMarkIntentCursorIndex != _browser.CursorIndex)
        {
            if (_selection.RecentMultiMarkIntentActive)
            {
                _selection.ClearRecentMultiMarkIntent();
            }
            return false;
        }

        IReadOnlyList<string> currentMarks = _selection.Snapshot()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!currentMarks.SequenceEqual(
                _selection.RecentMultiMarkIntentMarkedPaths,
                StringComparer.OrdinalIgnoreCase))
        {
            _selection.ClearRecentMultiMarkIntent();
            return false;
        }

        return true;
    }

    private int RemoveMarksAfterFileOperation(IEnumerable<string> paths)
    {
        string[] markedTargets = paths
            .Where(path => _activeOperationOrigin == null ||
                _activeOperationOrigin.Value.MarkedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (markedTargets.Length == 0)
        {
            return 0;
        }

        int removed = IsOperationOriginCurrentTab()
            ? _selection.RemoveRange(markedTargets)
            : _activeOperationOrigin is { } origin
                ? _browser.Workspace.RemoveMarksFromTab(
                    _settings.Value.Session,
                    origin.CategoryId,
                    origin.TabId,
                    markedTargets)
                : 0;
        if (removed == 0)
        {
            return 0;
        }

        if (IsOperationOriginCurrentTab())
        {
            InvalidateBrowserMarkState();
        }
        return removed;
    }

    private int ClearMarksAfterFileOperation()
    {
        IEnumerable<string> paths = _activeOperationOrigin?.MarkedPaths ?? _selection.Snapshot();
        return RemoveMarksAfterFileOperation(paths);
    }

    private void InvalidateBrowserMarkState()
    {
        _selection.InvalidateMarkSummaryCache();
        _selection.ClearRecentMultiMarkIntent();
        _selection.SetPendingEscExitPersistedMarks(null);
        if (ReferenceEquals(_browser.Selection, _selection))
        {
            _browser.Workspace.SyncActiveTabMarks(_selection.Snapshot());
        }
    }

    private readonly record struct PlannedFileOperation(
        string SourcePath,
        string DestinationPath,
        bool Skip,
        bool Overwrite,
        bool IsDirectory = false,
        bool IsDirectoryMergeRoot = false);
}
