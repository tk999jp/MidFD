using MidFD.Models;
using MidFD.Configuration;
using MidFD.Services;
using MidFD.Runtime;
using MidFD.Helpers;
using System.Windows.Forms;

namespace MidFD.Commands;

public sealed class CommandExecutionContext
{
    public CommandScope Scope { get; init; }
    public string Source { get; init; } = string.Empty;
    public SelectionResult? SelectionSnapshot { get; init; }
    public string? ContextTargetPath { get; init; }
    public string? CategoryId { get; init; }
    public string? CategoryDisplayName { get; init; }
    public IReadOnlyList<string>? CategoryIds { get; init; }
    public bool CategoryDeleteConfirmed { get; init; }
    public string? CategoryResultMessage { get; init; }
    public int? ContextTabIndex { get; init; }
    public Guid? ContextBrowserTabId { get; init; }
    public Guid? RuntimeTargetId { get; init; }
    public string? FileExtension { get; init; }
    public SevenZipHashAlgorithm? HashAlgorithm { get; init; }
    public HashResultCopyContext? HashResultCopy { get; init; }
    public BrowserTabNavigationCommandContext? BrowserTabNavigation { get; init; }
    public BrowserDirectoryNavigationCommandContext? BrowserDirectoryNavigation { get; init; }
    public BrowserMarkedItemNavigationCommandContext? BrowserMarkedItemNavigation { get; init; }
    public BrowserTabLayoutMode? BrowserTabLayout { get; init; }
    public BrowserTabNavigationWidthCommandContext? BrowserTabNavigationWidth { get; init; }
    public BrowserTabReorderCommandContext? BrowserTabReorder { get; init; }
    public BrowserTabGroupCommandContext? BrowserTabGroup { get; init; }
    public BrowserTabTreeDropCommandContext? BrowserTabTreeDrop { get; init; }
    internal IReadOnlySet<Guid>? BrowserTabGroupBlockedTabIds { get; set; }
    public BrowserTabGroupExpandedCommandContext? BrowserTabGroupExpanded { get; init; }
    public BrowserTabCategoryReorderCommandContext? BrowserTabCategoryReorder { get; init; }
    public SettingsInitialPage? SettingsInitialPage { get; init; }
    public SettingsSaveCommandContext? SettingsSave { get; init; }
    public SettingsTransferCommandContext? SettingsTransfer { get; init; }
    public ExternalToolLaunchCommandContext? ExternalToolLaunch { get; init; }
    public BrowserCommandExecutionCommandContext? BrowserCommandExecution { get; init; }
    public BrowserViewCommandContext? BrowserView { get; init; }
    public BrowserDragOutCommandContext? BrowserDragOut { get; init; }
    public HeaderCopyCommandContext? HeaderCopy { get; init; }
    public SortKind? SortKind { get; init; }
    public bool? SortAscending { get; init; }
    public int? MarkSlotNumber { get; init; }
    public BrowserMarkSlotSaveCommandContext? BrowserMarkSlotSave { get; init; }
    public BrowserMarkSlotMutationCommandContext? BrowserMarkSlotMutation { get; init; }
    public BrowserMarkSlotTransferCommandContext? BrowserMarkSlotTransfer { get; init; }
    public BrowserMarkSlotImportCommandContext? BrowserMarkSlotImport { get; init; }
    public BrowserMarkSlotClipboardImportCommandContext? BrowserMarkSlotClipboardImport { get; init; }
    public BrowserMarkSlotSetOperationPreviewCommandContext? BrowserMarkSlotSetOperationPreview { get; init; }
    public BrowserMarkSlotSetOperationSaveCommandContext? BrowserMarkSlotSetOperationSave { get; init; }
    public MarkSlotSetOperationPreviewResult? BrowserMarkSlotSetOperationApply { get; init; }
    public BrowserMarkClearCommandContext? BrowserMarkClear { get; init; }
    public ArchiveExtractRequest? ArchiveExtract { get; init; }
    internal ArchiveListNavigationCommandContext? ArchiveListNavigation { get; init; }
    internal ArchiveTextPreviewCommandContext? ArchiveTextPreview { get; init; }
    public bool ForcePackEachFolderIndividually { get; init; }
    public bool QuickPackExistingArchive { get; init; }
    internal ManagedTrashCommandContext? ManagedTrash { get; init; }
    internal WorkspaceSnapshotCommandContext? WorkspaceSnapshot { get; init; }
    public FileOperationCommandContext? FileOperation { get; init; }
    public BrowserExternalDropImportCommandContext? BrowserExternalDropImport { get; init; }
    public ImageViewerCommandContext? ImageViewerCommand { get; init; }
    public BrowserCursorNavigationCommandContext? BrowserCursorNavigation { get; init; }
    public UnifiedSearchSessionCommandContext? UnifiedSearchSession { get; init; }
    public UnifiedSearchResumeCommandContext? UnifiedSearchResume { get; init; }
    public UnifiedSearchResultsFilterCommandContext? UnifiedSearchResultsFilter { get; init; }
    public UnifiedSearchHitEditorCommandContext? UnifiedSearchHitEditor { get; init; }
    public UnifiedSearchResultsExportCommandContext? UnifiedSearchResultsExport { get; init; }
    public UnifiedSearchResultActivationCommandContext? UnifiedSearchResultActivation { get; init; }
    public BrowserMarkToggleCommandContext? BrowserMarkToggle { get; init; }
    public BrowserMarkRangeAddCommandContext? BrowserMarkRangeAdd { get; init; }
    public BrowserBulkMarkCommandContext? BrowserBulkMark { get; init; }
    public BrowserQuickAccessEntryCommandContext? BrowserQuickAccessEntry { get; init; }
    public BrowserQuickAccessDraftCommandContext? BrowserQuickAccessDraft { get; init; }
    public TextViewerSearchCommandContext? TextViewerSearch { get; init; }
    public TextViewerExternalEditorCommandContext? TextViewerExternalEditor { get; init; }
    public TextViewerNavigationCommandContext? TextViewerNavigation { get; init; }
    public TextViewerCopyOrigin TextViewerCopyOrigin { get; init; }
    public MarkdownViewerMode? TextViewerMarkdownMode { get; init; }
    public ExternalUrlOpenCommandContext? ExternalUrlOpen { get; init; }
    public CommandPaletteFavoriteToggleCommandContext? CommandPaletteFavoriteToggle { get; init; }
    public ExternalToolDefinitionsSaveCommandContext? ExternalToolDefinitionsSave { get; init; }
    internal BrowserRefreshProcessRequest? TextViewerExit { get; init; }
    public bool TextViewerLargeFileCopy { get; init; }
    public string? TextViewerSelectedText { get; init; }
    public bool TextViewerRenderedSelectionCopy { get; init; }
    public string? TextViewerCopyStatusMessage { get; init; }
    public bool PermanentDelete { get; init; }
}

public enum TextViewerCopyOrigin
{
    Unspecified,
    EmbeddedViewer,
    ArchivePreview
}

public sealed record TextViewerSearchCommandContext(
    string TextSnapshot,
    string Query,
    int StartIndex,
    bool Backward = false,
    bool IsLargeFile = false,
    bool PreserveLargeFileSearchHit = false);

public sealed record TextViewerExternalEditorCommandContext(string TargetPath, PreviewKind DisplayedKind);

public sealed record UnifiedSearchSessionCommandContext(Guid SessionId);

public sealed record UnifiedSearchResumeCommandContext(
    Guid SessionId,
    string SourceCategoryId,
    Guid SourceTabId,
    string RootPath,
    UnifiedFilterFindCriteria Criteria,
    UnifiedFilterFindMode Mode);

public sealed record UnifiedSearchResultsExportCommandContext(
    Guid SessionId,
    UnifiedFilterFindSearchResult Snapshot);

public sealed record UnifiedSearchResultsFilterCommandContext(
    Guid SessionId,
    string FilterText);

public sealed record UnifiedSearchHitEditorCommandContext(
    Guid SessionId,
    string TargetPath,
    UnifiedSearchResultIdentity HitIdentity,
    UnifiedSearchMatch? Hit);

public sealed record UnifiedSearchResultIdentity(
    string FullPath,
    int? HitLineNumber = null,
    int? HitColumnNumber = null,
    string? MatchText = null);

public enum UnifiedSearchResultActivationOrigin
{
    Enter,
    DoubleClick,
    CtrlEnter
}

public sealed record UnifiedSearchResultActivationCommandContext(
    Guid SessionId,
    string SourceCategoryId,
    Guid SourceTabId,
    string TargetPath,
    bool IsDirectory,
    UnifiedFilterFindMode SearchMode,
    UnifiedSearchResultIdentity ResultIdentity,
    UnifiedSearchResultActivationOrigin Origin,
    UnifiedSearchMatch? Hit = null);

public sealed record ExternalToolLaunchCommandContext(
    ExternalToolCommandDefinition Definition,
    ExternalToolExecutionContext ExecutionContext);

internal enum ArchiveListNavigationAction
{
    Parent,
    EnterDirectory
}

internal sealed record ArchiveListNavigationCommandContext(
    Guid SessionId,
    ArchiveListNavigationAction Action,
    string? TargetEntryPath = null);

internal enum ArchiveTextPreviewOperation
{
    Open,
    RefreshExisting
}

internal sealed record ArchiveTextPreviewCommandContext(
    Guid SessionId,
    long NavigationVersion,
    ArchiveTextPreviewOperation Operation,
    string? EntryPath,
    string? RawEntryPath,
    string? DisplayName);

public sealed record BrowserCommandExecutionCommandContext(
    string Command,
    string Arguments,
    string WorkingDirectory,
    string FallbackWorkingDirectory);

public sealed record TextViewerNavigationCommandContext(
    TextViewerNavigationTarget Target,
    int VisibleLineCount,
    int MaxFirstVisibleLine);

public sealed record ExternalUrlOpenCommandContext(Uri? TargetUri);

public sealed record CommandPaletteFavoriteToggleCommandContext(
    string TargetCommandId,
    CommandPaletteUsageState UsageState,
    bool Persist);

public sealed record ExternalToolDefinitionsSaveCommandContext(ExternalToolCommandStore Store);

public enum TextViewerNavigationTarget
{
    Home,
    End,
    PageUp,
    PageDown
}

public sealed record BrowserTabNavigationCommandContext(
    bool IncludeUnifiedSearchResults,
    Guid? UnifiedSearchSessionId = null,
    string? UnifiedSearchSourceCategoryId = null,
    Guid? UnifiedSearchSourceTabId = null,
    bool UnifiedSearchIsActive = false);

public sealed record BrowserDirectoryNavigationCommandContext(
    string TargetPath,
    string? FocusTargetName = null,
    bool IsHistoryNavigation = false,
    bool SuppressRecent = false,
    bool RecordDirectoryMoveHistory = false,
    bool ClearPreview = false,
    bool RefreshPreview = false);

public sealed record BrowserMarkedItemNavigationCommandContext(
    string FullPath,
    bool RequireVisibleTarget = false);

public sealed record BrowserTabReorderCommandContext(
    Guid MovingTabId,
    Guid AnchorTabId,
    bool InsertAfter,
    bool FocusBrowserAfter = true,
    bool NotifyUser = true);

public sealed record BrowserTabNavigationWidthCommandContext(int Width);

public sealed record BrowserTabGroupExpandedCommandContext(Guid GroupId, bool Expanded);

public sealed record BrowserTabGroupCommandContext(
    string CategoryId,
    Guid TabId,
    Guid? GroupId = null,
    string? DisplayName = null);

public sealed record BrowserTabTreeDropCommandContext(
    string SourceCategoryId,
    Guid SourceTabId,
    string DestinationCategoryId,
    Guid? DestinationGroupId,
    Guid? AnchorTabId,
    BrowserTabTreeDropPlacement Placement,
    bool RequiresMembershipChange,
    bool RequiresReorder);

public sealed record BrowserTabCategoryReorderCommandContext(string MovingCategoryId, string AnchorCategoryId, bool InsertAfter);

public enum SettingsInitialPage
{
    Display = 0,
    Color = 1,
    Operation = 2,
    InputAssignment = 3,
    External = 4,
    StartupAndLog = 5
}

public enum SettingsTransferAction
{
    Export,
    ReadImport,
    ApplyImport
}

public sealed record SettingsTransferCommandContext(
    SettingsTransferAction Action,
    string FilePath,
    AppSettings? Settings = null);

public sealed record SettingsSaveCommandContext(AppSettings Settings);

public sealed record BrowserViewCommandContext(
    BrowserFileDisplayMode? DisplayMode = null,
    int? ColumnCount = null);

public sealed record BrowserDragOutCommandContext(
    SelectionResult SelectionSnapshot,
    string? CandidatePath,
    bool IsBlankDrag,
    BrowserDragMouseButton MouseButton,
    BrowserDragModifiers MouseDownModifiers,
    BrowserDragModifiers CurrentModifiers,
    bool EnableDragArchiveHandoff,
    bool IncludeDragZipManifest);

public sealed record HeaderCopyCommandContext(string? CurrentItemPath, string? CurrentItemName);

public sealed record FileOperationCommandContext(
    string? DestinationDirectory = null,
    Action? CompletionCallback = null,
    bool RecordDragCopyUndo = false);

public enum BrowserExternalDropImportKind
{
    OutlookAttachments,
    Image,
    ImageUrl
}

public sealed record BrowserExternalDropImportCommandContext(
    BrowserExternalDropImportKind Kind,
    System.Windows.Forms.IDataObject? DataObject,
    string? DestinationDirectory = null,
    Uri? ImageUri = null,
    string? SuggestedFileName = null,
    Action<Action>? InvokeUi = null,
    Func<string, OverwriteConfirmResult>? ConfirmOverwrite = null,
    bool ProcessAsync = false,
    int TargetCount = 1,
    IReadOnlyList<string>? OutlookAttachmentNames = null);

public sealed record ImageViewerCommandContext(
    Guid ViewerInstanceId,
    int? VideoStillSeekSeconds = null,
    double? VideoPlaybackRate = null);

public sealed record BrowserCursorNavigationCommandContext(int TargetGlobalIndex);

public sealed record BrowserMarkToggleCommandContext(
    bool MoveNext,
    string? TargetPath = null,
    bool? RequestedMarkedState = null,
    bool SkipMissingTargetWhenAdding = false);

public sealed record BrowserMarkRangeAddCommandContext(IReadOnlyList<string> TargetPaths);

public sealed record BrowserBulkMarkCommandContext(bool IncludeDirectories);

public sealed record BrowserQuickAccessEntryCommandContext(QuickAccessEntry Entry);

public sealed record BrowserQuickAccessDraftCommandContext(
    QuickAccessStore Store,
    string? CurrentPath = null,
    QuickAccessEntry? Entry = null,
    string? DisplayName = null,
    string? Path = null,
    string? CategoryName = null,
    bool UseAlias = false,
    bool MoveUp = false,
    BrowserQuickAccessDraftOrderTarget? OrderTarget = null);

public abstract record BrowserQuickAccessDraftOrderTarget;

public sealed record BrowserQuickAccessEntryOrderTarget(QuickAccessEntry Entry)
    : BrowserQuickAccessDraftOrderTarget;

public sealed record BrowserQuickAccessCategoryOrderTarget(string CategoryName)
    : BrowserQuickAccessDraftOrderTarget;

public sealed record BrowserQuickAccessDraftMutationResult(
    bool Succeeded,
    QuickAccessStore UpdatedStore,
    string? NormalizedPath = null,
    string? Message = null);

public enum BrowserMarkSlotSaveScope
{
    CurrentTab,
    CurrentCategory,
    Workspace
}

public sealed record BrowserMarkSlotSaveCommandContext(
    int SlotNumber,
    BrowserMarkSlotSaveScope Scope,
    string? DisplayName = null,
    IReadOnlyList<string>? Paths = null,
    BrowserTabState? ActiveTab = null,
    string? CategoryName = null,
    string? TabDisplayName = null,
    string? SourceScope = null,
    string? SourceScopeLabel = null,
    string? SourceCategoryId = null,
    string? SourceCategoryName = null,
    int CategoryCount = 0,
    int TabCount = 0,
    int RawMarkCount = 0);

public sealed record BrowserMarkSlotMutationCommandContext(
    int SlotNumber,
    string? DisplayName = null,
    IReadOnlyCollection<string>? Paths = null);

public sealed record BrowserMarkSlotTransferCommandContext(
    string? FilePath,
    int? SlotNumber = null);

public sealed record BrowserMarkSlotImportCommandContext(
    int? SlotNumber,
    string? FilePath,
    MarkSlotEntry? ImportedSlot = null,
    MarkSlotStore? ImportedStore = null,
    string? WarningMessage = null);

public sealed record BrowserMarkSlotClipboardImportCommandContext(
    string? ClipboardText,
    string CurrentPath,
    string? RepositoryRoot,
    string? ClipboardError = null);

public sealed record BrowserMarkSlotSetOperationPreviewCommandContext(
    int SlotANumber,
    int SlotBNumber,
    string OperationKind,
    IReadOnlySet<string>? ExistingPaths = null,
    IReadOnlyDictionary<string, bool>? ExistingDirectoryPaths = null);

public sealed record BrowserMarkSlotSetOperationSaveCommandContext(
    MarkSlotSetOperationSaveRequest Request,
    string? DisplayName);

public enum BrowserMarkClearScope
{
    CurrentTab,
    CurrentCategory,
    Workspace
}

public sealed record BrowserMarkClearCommandContext(
    BrowserMarkClearScope Scope,
    BrowserTabState ActiveTab,
    string? CategoryId = null,
    string? StatusMessageOverride = null,
    bool PreservePendingEscExitState = false);

internal enum ImageViewerCommandAction
{
    Open,
    SaveAs,
    Close,
    Copy,
    Paste,
    PasteSelectionStretch,
    CopySvg,
    Quantize,
    Reset,
    RotateRight,
    RotateLeft,
    FlipHorizontal,
    FlipVertical,
    ShowInfo,
    Undo,
    Redo,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    FullscreenToggle,
    FullscreenExit,
    VideoSeekToStart,
    VideoSeekBackward,
    VideoSeekForward,
    VideoSeekToPosition,
    VideoExternalPlay,
    VideoSaveFrame,
    VideoSetPlaybackRate,
    Resize,
    Mosaic,
    Blur,
    Tone,
    PasteSelectionOriginal
}

internal enum ManagedTrashCommandAction
{
    RestoreSelected,
    DeleteSelectedForever,
    Empty,
    CleanMissing
}

internal sealed record ManagedTrashCommandContext(
    ManagedTrashCommandAction Action,
    IReadOnlyList<ManagedTrashRecordView>? SelectedRecords = null);

internal enum WorkspaceSnapshotCommandAction
{
    Open,
    Save,
    Restore,
    Rename,
    Delete,
    Export,
    Import,
    ExportAll,
    ImportAll
}

internal sealed record WorkspaceSnapshotCommandContext(
    WorkspaceSnapshotCommandAction Action,
    IWin32Window? Owner,
    WorkspaceSnapshotEntry? Entry = null,
    string? Name = null,
    string? FilePath = null,
    bool Confirmed = false,
    bool OverwriteConfirmed = false);
