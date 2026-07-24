using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Dialogs;
using MidFD.Services;
using MidFD.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Media;
using MidFD.Models;
using MidFD.Helpers;
using MidFD.Commands;
using MidFD.Presentation;
using MidFD.Controls;
using MidFD.Services.TrashManifestStore;
using MidFD.Services.Workspace;
namespace MidFD;
public partial class MainForm : Form, ICommandPaletteLayerHost
{
    // Shell guarded delete is fast for small batches, but progress/cancel timing depends on Shell callbacks.
    // Use the MidFD-controlled path for larger batches so cancel stops before the next item and progress is truthful.
    private const int ShellGuardedRecycleBinDeleteMaxItems = 8;
    private const int ChunkedShellRecycleBinDeleteMinItems = 256;
    private const int ChunkedShellRecycleBinDeleteChunkSize = 64;
    private const int MoveProgressReportChunkSize = 64;
    private const int MoveProgressReportThrottleMilliseconds = 150;
    private const int MoveUnmarkChunkSize = 128;
    private const int MoveUnmarkThrottleMilliseconds = 200;
    private const int LargeTextClipboardCopyMaxLines = 100_000;
    private const int LargeTextClipboardCopyMaxChars = 10_000_000;
    private const long LargeTextClipboardCopyMaxBytesEstimate = 32L * 1024 * 1024; // 32MB
    private const int CurrentDirectoryRefreshDebounceMilliseconds = 750;
    private const int CurrentDirectoryRefreshRetryDelayMilliseconds = 100;
    private const int ExternalDirectoryRefreshBulkThreshold = 64;
    private const int MinimumNormalWindowWidth = 980;
    private const int MinimumNormalWindowHeight = 480;
    private const int MinimumUsableClientAreaHeight = 120;
    private const float HeaderStatusMinimumReadableFontSize = 8f;
    private const int HeaderStatusResponsiveFontDebounceMs = 150;
    // 通常運用の app.log を汚さないため、詳細な header/status 診断は debugger 接続時のみ出す。
    private static readonly bool HeaderStatusFontRouteDiagnosticLoggingEnabled = Debugger.IsAttached;
    private const int HeaderRow2ClockSafetyGap = 8;
    private readonly record struct HeaderRow1FitMetrics(
        int RowWidth,
        int LeftRequiredWidth,
        int ClockReservedWidth,
        int SafetyGap,
        int GuardBand,
        int TotalRequiredWidth,
        int AvailableLeftWidth,
        bool Fits,
        int PageWidth,
        int TotalWidth,
        int UsedWidth,
        int FreeWidth,
        int ClockMeasuredWidth,
        string ClockText,
        string FreeText);
    private static readonly HashSet<string> _executeTargetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".lnk"
    };
    private readonly NavigationService _navigationService;
    private BreadcrumbPathControl? _breadcrumbPathControl;
    private readonly BrowserInputRouter _browserInputRouter = new();
    private readonly BrowserMarkInteractionController _browserMarkInteractionController = new();
    private readonly ViewerInputRouter _viewerInputRouter = new();
    private readonly BrowserNavigationCoordinator _browserNavigationCoordinator = new();
    private readonly ViewerPreviewCoordinator _viewerPreviewCoordinator = new();
    private readonly CommandStateCoordinator _commandStateCoordinator = new();
    private CommandStateCoordinator.CommandUiSnapshot _cachedCommandUiSnapshot;
    private bool _isFunctionBarShiftLayerActive;
    private bool _isFunctionBarCtrlLayerActive;
    private bool _isFunctionBarAltLayerActive;
    private readonly BrowserLoadCoordinator _browserLoadCoordinator = new();
    private readonly FileOperationEntryCoordinator _fileOperationEntryCoordinator = new();
    private readonly FileOperationDialogCoordinator _fileOperationDialogCoordinator = new();
    private readonly FileOperationPostOperationCoordinator _fileOperationPostOperationCoordinator = new();
    private readonly RenameDialogCoordinator _renameDialogCoordinator = new();
    private readonly RenameApplyCoordinator _renameApplyCoordinator = new();
    private readonly MarkSelectionState _markedFiles = new();
    private readonly FileOperationUndoRedoService _fileOperationUndoRedoService = new();
    private AppSettings _settings;
    private FileListColorResolver.ResolvedColors? _resolvedColors;
    private readonly string? _startupProfileOverride;
    private FeatureProfile _featureProfile = FeatureProfile.Full;
    // Diagnostic logging: unique ID for each selection change
    private static long _selectionIdCounter = 0;
    private FeatureGateService _featureGate = new(FeatureProfile.Full);
    private readonly Coordinators.PreviewRequestCoordinator _previewRequestCoordinator = new();
    private readonly Models.FileOperationUiState _fileOpUiState = new();
    private FileOperationItemProgressState? _fileOperationItemProgressState;
    private FileOperationProgressDialog? _fileOperationProgressDialog;
    private FileOperationProgressFallbackForm? _shellDeleteProgressFallback;
    private FileOperationProgressFallbackForm? _undoRedoProgressFallback;
    private FileOperationProgressFallbackForm? _archiveProgressFallback;
    private string? _currentPreviewTarget; // 非同期競合チェック用 (パス)
    private bool _isBrowserAutoPreviewSuppressed;
    private string? _lastBrowserAutoPreviewSuppressedMessage;
    private string? _lastPreviewRequestedPath;
    // _previewRequestInFlight and _previewRequestId moved to PreviewRequestCoordinator
    private int _activePreviewRequestId = 0; // 最新UI反映待ちのリクエストID
    private readonly PreviewPopupForm _previewPopup; // プレビューPopupウィンドウ
    private readonly List<ImageViewerForm> _imageViewers = new(); // 起動中の画像ビューア
    private PreviewKind _currentViewerKind = PreviewKind.None;
    private string _currentViewerDetectedEncodingLabel = string.Empty;
    private enum UIMode { Browser, Viewer }
    private UIMode _uiMode = UIMode.Browser;
    private int _hoveredFuncKeyIndex = -1;
    private int _pressedFuncKeyIndex = -1;
    private enum ViewerEncoding { Auto, UTF8, SJIS }
    private ViewerEncoding _viewerEncodingOverride = ViewerEncoding.Auto;
    private SortKind _currentSort = SortKind.Name;
    private bool _sortAscending = true;
    private string _filterPattern = "";
    private bool _filterUseRegex = false;
    private string _viewerSearchKeyword = "";
    private Models.LargeFilePreviewState? _largeFileState;
    private Controls.LargeFilePreviewControl _largeFileControl = null!;
    private const int LargeTextInitialScanBytes = 512 * 1024;
    private const int LargeTextInitialLineReadBytes = 512 * 1024;
    private const int LargeTextLongLineVisibleReadBytes = 4096;
    private readonly Stopwatch _largeTextEntryStopwatch = new Stopwatch();
    // Browser モード用（多列表示）プロパティ
    private int _columnCount = 3; // 1〜9列 (数字/テンキーで切替)
    private int _lastColumnCountKey = 0; // WinFD互換モードでの連続押下判定用
    private int _browserCursorIndex = 0; // directory全体に対するglobal index
    private int _browserPageStartIndex;
    private int _browserTotalItemCount;
    private MarkSummaryCacheState _markSummaryCacheState = MarkSummaryCacheState.Invalid;
    private string _markSummaryCache = string.Empty;
    private string _markSummaryCachePath = string.Empty;
    private int _markSummaryCacheCount = -1;
    private string _markSummaryCacheSizeText = string.Empty;
    private string _markSummaryCacheCompact = string.Empty;
    private long _markSummaryCacheTotalSize;
    private int _markSummaryCacheFileCount;
    private int _markSummaryCacheOutsideCount;
    private readonly MarkSummaryRebuildCoordinator _markSummaryRebuildCoordinator;
    private readonly MarkSummaryBulkEffectCoordinator _markSummaryBulkEffectCoordinator = new();
    private readonly MarkPersistenceBoundaryCoordinator _markPersistenceBoundaryCoordinator = new();
    private readonly MarkOperationEffectCoordinator _markOperationEffectCoordinator = new();
    private bool _recentMultiMarkIntentActive;
    private string _recentMultiMarkIntentDirectory = string.Empty;
    private int _recentMultiMarkIntentCursorIndex = -1;
    private IReadOnlyList<string> _recentMultiMarkIntentMarkedPaths = Array.Empty<string>();
    // Phase 2g-fix3a: Row 1 専用時計 Timer
    private System.Windows.Forms.Timer? _headerClockTimer;
    private System.Windows.Forms.Timer? _headerStatusResizeDebounceTimer;
    // Phase: Browser UpdateInfoPanel debounce corrective
    // カーソル移動時の補助表示更新を debounce するための Timer と sequence counter。
    // 選択状態・操作対象は即時維持し、UpdateInfoPanel 系の表示更新だけを遅延予約する。
    private System.Windows.Forms.Timer? _updateInfoPanelDebounceTimer;
    private long _updateInfoPanelDebounceSeq = 0;
    private readonly UncDriveInfoResolver _uncDriveInfoResolver = new();
    private Font? _headerPaintFont; // titleHeaderPanel_Paint で使用するフォント保持用
    private Font? _headerStatusResponsiveOwnedFont;
    private Size _lastHeaderStatusResponsiveClientSize = Size.Empty;
    private int _lastHeaderStatusResponsiveDpi;
    private bool _updatingHeaderStatusResponsiveFont;
    private string _lastHeaderResponsiveDiagSnapshot = string.Empty;
    private DateTime _lastHeaderResponsiveDiagUtc = DateTime.MinValue;
    private string _lastHeaderResponsiveStabilizeDiagSnapshot = string.Empty;
    private DateTime _lastHeaderResponsiveStabilizeDiagUtc = DateTime.MinValue;
    // Phase 3-fix2b: Drag-out (MidFD → 外部) 用の状態管理
    private const string InternalDragArchiveFormat = "MidFD.InternalDragArchiveHandoff";
    private const string InternalDragArchiveMarkerValue = "1";
    private Point _dragStartPoint = Point.Empty;
    private int _dragCandidateIndex = -1;
    private bool _blankDragCandidate;
    private bool _dragArchiveHandoffRequested = false;
    private enum BrowserRightInteractionState
    {
        Idle,
        BlankRightPending,
        ItemRightPending,
        HeaderRightPending,
        GestureTracking,
        FileDragTracking
    }

    private BrowserRightInteractionState _browserRightInteractionState;
    private Point _browserRightStartPoint = Point.Empty;
    private int _browserRightItemIndex = -1;
    private string? _browserRightItemPath;
    private IReadOnlyList<string> _browserRightSelectionSnapshot = Array.Empty<string>();
    private Control? _browserRightCaptureControl;
    private readonly HashSet<Control> _headerGestureControls = new();
    private bool _suppressNextHeaderContextMenu;
    private DateTime _suppressHeaderContextMenuUntilUtc = DateTime.MinValue;
    private BrowserIncomingDragDecision? _currentIncomingDragDecision;
    private bool _isClipboardBusy = false;
    private bool _isFileOperationUndoRedoBusy = false;
    private readonly NotificationService _notificationService;
    private DateTime _statusNoticeHoldUntilUtc = DateTime.MinValue;
    private readonly record struct ExternalToolAltHintRow(
        string SlotLabel,
        string Title,
        string ExecutableName,
        string StatusText,
        bool IsLaunchable,
        ExternalToolCommandDefinition Tool);
    private static readonly HashSet<char> ReservedExternalToolAltSlots = new() { 'F', 'V', 'G', 'T', 'H' };
    private ToolStripButton? _btnMenuBack;
    private ToolStripButton? _btnMenuForward;
    private ToolStripButton? _btnMenuUp;
    private ToolStripButton? _btnMenuReload;
    private ToolStripItem? _menuNavSeparator;
    private QuickAccessStore _quickAccessStore;
    private readonly MarkSlotStore _markSlotStore;
    private bool _isAltHintHeld;
    private bool _isExternalToolAltPopupAltOwned;
    private bool _isOpeningMenuStripExplicitly;
    private IReadOnlyList<ExternalToolAltHintRow> _commandHintRows = Array.Empty<ExternalToolAltHintRow>();
    private int _commandHintSelectedIndex = -1;
    private int _commandHintScrollIndex = 0;
    private string _commandHintContextLine1 = string.Empty;
    private string _commandHintContextLine2 = string.Empty;
    private readonly System.Windows.Forms.Timer _commandHintOverlayTimer = new();
    private readonly System.Windows.Forms.Timer _directoryRefreshDebounceTimer = new();
    private readonly System.Windows.Forms.Timer _directoryCountAuditTimer = new();
    private CancellationTokenSource? _directoryCountAuditCts;
    private readonly DirectoryCountAuditGate _directoryCountAuditGate = new();
    private readonly DirectoryCountAuditSchedule _directoryCountAuditSchedule = new();
    private long _directoryNavigationGeneration;
    private long _directoryContentGeneration;
    private int _browserItemsPerPage;
    private int _functionBarPreferredHeight = 24;
    private int _lastLoggedCommandHintRowCount = -1;
    private Rectangle _lastLoggedCommandHintBounds = Rectangle.Empty;
    private Size _lastLoggedCommandHintPanelSize = Size.Empty;
    private readonly List<ToolStripItem> _browserOnlyMenuItems = new();
    private readonly List<ToolStripItem> _busyAwareMenuItems = new();
    private readonly Dictionary<ToolStripItem, CommandStateCoordinator.MenuItemStateRule> _menuItemRules = new();
    private readonly Models.BrowserTabViewState _browserTabViewState = new();
    private readonly Models.BrowserCategoryViewState _categoryViewState = new();
    private bool _suppressBrowserTabSelectionChanged;
    private bool _isSwitchingBrowserTab;
    private Panel? _browserTabHostPanel;
    private BrowserTabStrip? _browserTabStrip;
    private string? _lastBrowserTabHeaderSnapshotKey;
    private const string ReadOnlyBrowserTabBlockedMessage = "このタブは ReadOnly のため、この操作は実行できません。";
    private const int BrowserTabStripMultiRowHeight = 56;
    private const int BrowserTabStripSingleRowHeight = 30;
    private const int MarkSlotCount = 5;
    private ToolStripMenuItem? _toggleBrowserTabLockMenuItem;
    private ToolStripMenuItem? _toggleBrowserTabReadOnlyMenuItem;
    private ToolStripMenuItem? _fileDisplayModeNameOnlyMenuItem;
    private ToolStripMenuItem? _fileDisplayModeNameSizeMenuItem;
    private ToolStripMenuItem? _fileDisplayModeNameSizeDateMenuItem;
    private ToolStripMenuItem? _reloadCurrentDirectoryMenuItem;
    private ToolStripMenuItem? _clearTabFilterLockMenuItem;
    private ContextMenuStrip? _browserTabContextMenu;
    private readonly Coordinators.BrowserTabUiCoordinator _browserTabUiCoordinator = new();
    private ToolStripMenuItem? _toggleBrowserTabLockContextMenuItem;
    private ToolStripMenuItem? _toggleBrowserTabReadOnlyContextMenuItem;
    private ToolStripMenuItem? _openBrowserTabFilterLockContextMenuItem;
    private ToolStripMenuItem? _clearBrowserTabFilterLockContextMenuItem;
    private ToolStripMenuItem? _closeBrowserTabContextMenuItem;
    private ToolStripMenuItem? _closeRightBrowserTabsContextMenuItem;
    private ToolStripMenuItem? _closeLeftBrowserTabsContextMenuItem;
    private ToolStripMenuItem? _closeOtherBrowserTabsContextMenuItem;
    private ContextMenuStrip? _browserTabCategoryContextMenu;
    private ToolStripMenuItem? _addBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _moveBrowserTabCategoryLeftContextMenuItem;
    private ToolStripMenuItem? _moveBrowserTabCategoryRightContextMenuItem;
    private ToolStripMenuItem? _renameBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _deleteBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _manageBrowserTabCategoriesContextMenuItem;
    private FileSystemWatcher? _currentDirectoryWatcher;
    private string? _currentDirectoryWatcherPath;
    private readonly Coordinators.NavigationRefreshCoordinator _navigationRefreshCoordinator = new();
    private readonly PreviewDiagnosticDelayService _previewDiagnosticDelayService = new();
    private bool _currentDirectoryRefreshRetryPending;
    private bool _isApplyingDirectoryList;
    private bool _suppressBrowserSelectionChanged;
    private readonly BrowserSelectionIdentityGate _browserSelectionIdentityGate = new();
    private BrowserTabStripCategoryItemKind _browserTabCategoryContextKind = BrowserTabStripCategoryItemKind.Category;
    private DateTime _lastBrowserTabLimitBeepUtc = DateTime.MinValue;
    private List<string>? _pendingEscExitPersistedMarks;
    private bool _isClosingFromEscExitPath;
    private bool _isExitConfirmationPending;
    private IWorkspaceStateStore? _workspaceStateStore;
    private WorkspaceSnapshotStorage? _workspaceSnapshotStorage;
    private bool _restoredBrowserTabsFromWorkspaceStore;
    private readonly MouseGestureRecognizer _mouseGestureRecognizer = new();
    private readonly List<Point> _mouseGestureTrailPoints = new();
    private bool _isMouseGestureTrailVisible;
    private const int MouseGestureTrailMinDistance = 4;
    private bool _suppressNextBrowserContextMenu;
    private DateTime _suppressBrowserContextMenuUntilUtc = DateTime.MinValue;
    private readonly List<ClosedBrowserTabSnapshot> _closedBrowserTabs = new();
    private const int ClosedBrowserTabHistoryLimit = 10;
    // browser header interaction polish fields
    private bool _headerInteractionInitialized;
    private ToolTip? _headerToolTip;
    private readonly ToolTip _browserFileNameToolTip = new();
    private int _browserFileNameToolTipIndex = -1;
    private string? _browserFileNameToolTipText;
    private string _lastHeaderRightDiagSnapshot = string.Empty;
    private readonly SettingsRecoveryNoticeScheduler _settingsRecoveryNoticeScheduler = new();
    private DateTime _lastHeaderRightDiagUtc = DateTime.MinValue;
    private readonly ToolTip _fKeyToolTip = new();
    private int _fKeyToolTipIndex = -1;
    private ContextMenuStrip? _headerPathContextMenu;
    private ContextMenuStrip? _headerItemContextMenu;
    private ContextMenuStrip? _headerSortContextMenu;
    private readonly Dictionary<SortKind, ToolStripMenuItem> _headerSortKeyItems = new();
    private ToolStripMenuItem? _headerSortAscendingItem;
    private ToolStripMenuItem? _headerSortDescendingItem;
    private ContextMenuStrip? _browserItemContextMenu;
    private ContextMenuStrip? _browserBlankContextMenu;
    private readonly CommandRegistry _commandRegistry = new();
    private readonly CommandDispatcher _commandDispatcher;
    internal bool AuthorToolsEnabled { get; }
    public MainForm(string? startupProfileOverride = null, bool authorToolsEnabled = false)
    {
        _startupProfileOverride = startupProfileOverride;
        AuthorToolsEnabled = authorToolsEnabled;
        _commandDispatcher = new CommandDispatcher(_commandRegistry, TryExecuteRegisteredCommand);
        InitializeCoreWindowChrome();
        SettingsManager.SaveFailed += HandleSettingsSaveFailed;
        InitializeBrowserFileNameToolTip();
        InitializeFunctionBarToolTip();
        _notificationService = new NotificationService(this.statusLabel, this.messageTimer, ResolveStatusColor);
        _navigationService = new NavigationService();
        _markSummaryRebuildCoordinator = new MarkSummaryRebuildCoordinator(
            BuildMarkSummaryAsync,
            () => NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath),
            () => IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath,
            action => BeginInvoke(action),
            ApplyCompletedMarkSummary);
        LoadSettingsAndApplyProfile();
        InitializePersistenceStores();
        InitializeStartupStoresAndHints();
        _markSlotStore = MarkSlotStorage.Load(MarkSlotCount);
        _previewPopup = new PreviewPopupForm();
        InitializePreviewAndLargeTextControls();
        InitializeViewerTextBoxEvents();
        InitializeStartupSessionState();
        InitializeRuntimeTimersAndOverlay();
        string startupPath = ResolveStartupPath();
        InitializeMainUiSurface();
        ShowSettingsRecoveryNoticeIfNeeded();
        RestoreBrowserStartupState(startupPath);
        WireBrowserInputEvents();
        // SettingsForm entry route regression corrective: Ensure KeyDown is wired and KeyPreview is active
        KeyPreview = true;
        KeyDown -= MainForm_KeyDown;
        KeyDown += MainForm_KeyDown;
        WireHeaderAndFunctionBarEvents();
        WireWindowLifecycleEvents();
        // 初期 FunctionBar 表示
        UpdateFunctionBar();
    }
    private void InitializeCoreWindowChrome()
    {
        InitializeComponent();
        this.MinimumSize = new Size(MinimumNormalWindowWidth, MinimumNormalWindowHeight);
        statusStrip.ShowItemToolTips = false;
        statusStrip.Dock = DockStyle.Bottom;
        statusStrip.SizingGrip = false;
        statusStrip.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
        statusStrip.RenderMode = ToolStripRenderMode.System;
        NormalizeStatusLabelLayout();
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon", "MidFD.ico");
            if (File.Exists(iconPath))
            {
                this.Icon = new Icon(iconPath);
            }
        }
        catch
        {
            // アイコン設定失敗時は既定のまま続行
        }
    }
    private void InitializeBrowserFileNameToolTip()
    {
        _browserFileNameToolTip.InitialDelay = 500;
        _browserFileNameToolTip.ReshowDelay = 200;
        _browserFileNameToolTip.AutoPopDelay = 5000;
        _browserFileNameToolTip.ShowAlways = false;
    }
    [MemberNotNull(nameof(_settings))]
    private void LoadSettingsAndApplyProfile()
    {
        _settings = SettingsManager.Load(out SettingsManager.SettingsLoadMetadata settingsLoadMetadata);
        _settings.Input ??= new InputSettings();
        _settings.Input.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(_settings.Input.MouseGestureCommandMap);
        InputSettings.NormalizeAndMigrateFunctionKeyChords(_settings.Input);
        ApplyFeatureProfile(settingsLoadMetadata.IsMouseGesturesExplicit);
    }
    private void InitializePersistenceStores()
    {
        _workspaceStateStore = WorkspaceStateStoreFactory.CreateDefault();
        _workspaceSnapshotStorage = new WorkspaceSnapshotStorage(WorkspaceStateStoreFactory.GetDefaultDbPath());
        MidFdManagedTrashService.Initialize(_settings);
    }
    [MemberNotNull(nameof(_quickAccessStore))]
    private void InitializeStartupStoresAndHints()
    {
        SyncActiveBrowserTabCategoryFromSession();
        LogService.ApplySettings(_settings.Logging);
        MidFDColors.ApplyTheme(FileListColorResolver.NormalizeCoreTheme(_settings.Appearance?.ColorTheme));
        _quickAccessStore = QuickAccessService.LoadOrMigrate(_settings.QuickAccess);
        IReadOnlyList<ExternalToolAltHintRow> startupHintRows = BuildExternalToolAltHintRows();
        string startupFirstHint = startupHintRows.Count > 0
            ? $"{startupHintRows[0].SlotLabel}:{startupHintRows[0].Title}"
            : "<none>";
        LogAltHint($"Startup rows={startupHintRows.Count} first={startupFirstHint}");
        // Phase 36: ヘッダ初期化
        lblTitle.Text = "<< MidFD >>";
        lblClock.Text = DateTime.Now.ToString("yyyy-MM-dd(ddd) HH:mm:ss");
    }
    private void InitializePreviewAndLargeTextControls()
    {
        // Phase: large file preview / single global scrollbar virtual line foundation
        _largeFileControl = new Controls.LargeFilePreviewControl();
        _largeFileControl.Visible = false;
        _largeFileControl.ScrollRequested += (s, line) =>
        {
            _ = NavigateLargeFilePreviewAsync(line, "ScrollRequested");
        };
        _largeFileControl.SelectionChanged += (s, e) =>
        {
            // Selection の可視化は LargeFilePreviewControl 内のハイライトで完結させる。
            // 外側 status の persistent 更新は行わない（選択操作で status が不安定化するため）。
        };
        _largeFileControl.FirstContentPainted += (_, _) =>
        {
            if (_largeFileState == null)
            {
                return;
            }
            LogService.Info(
                $"[LargeTextFirstPaint] elapsedMs={_largeTextEntryStopwatch.ElapsedMilliseconds} " +
                $"uiMode={_uiMode} kind={_currentViewerKind} " +
                $"path='{_largeFileState.FilePath}' " +
                $"offsets={_largeFileState.LineOffsets.Count} " +
                $"isIndexing={_largeFileState.IsIndexing} " +
                $"status='{statusLabel?.Text ?? "<null>"}'");
        };
        _largeFileControl.CharacterSelectionAutoScrollRequested += (s, direction) =>
        {
            if (_largeFileState == null) return;
            int step = Math.Max(1, _largeFileControl.VisibleLineCount / 4);
            int target = _largeFileState.FirstVisibleLine + direction * step;
            _ = NavigateLargeFilePreviewAsync(
                target,
                "CharacterSelectionAutoScroll",
                preserveCharacterSelection: true,
                characterSelectionAutoScrollDirection: direction);
        };
        viewerPanel.Controls.Add(_largeFileControl);
        // 設定の復元
        if (_settings.Preview.X != -1 && _settings.Preview.Y != -1)
        {
            _previewPopup.SetBounds(_settings.Preview.X, _settings.Preview.Y, _settings.Preview.Width, _settings.Preview.Height);
            _previewPopup.IsManuallyPositioned = _settings.Preview.IsManuallyPositioned;
        }
        _previewPopupVisible = _settings.Preview.IsVisible;
        // Viewer 改行モードの復元
        viewerTextBox.WordWrap = _settings.Preview.ViewerWordWrap;
        viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
    }
    private void InitializeViewerTextBoxEvents()
    {
        viewerTextBox.SelectionChanged += (s, e) =>
        {
            if (IsTextOrBinaryViewerActive())
            {
                ApplyViewerStatusLine();
            }
        };
        viewerTextBox.VScroll += (s, e) =>
        {
            if (IsTextOrBinaryViewerActive())
            {
                ApplyViewerStatusLine();
            }
        };
        viewerTextBox.MouseWheel += (s, e) =>
        {
            if (IsTextOrBinaryViewerActive())
            {
                ApplyViewerStatusLine();
            }
        };
        viewerTextBox.TextChanged += (s, e) =>
        {
            if (IsTextOrBinaryViewerActive())
            {
                ApplyViewerStatusLine();
            }
        };
        TextPreviewInteractionHelper.Attach(
            viewerTextBox,
            ShowStatusMessage,
            this,
            showErrorDialog: true,
            resolveClickedUrl: ResolveViewerClickedUrl);

        messageTimer.Tick += (_, _) =>
        {
            if (_uiMode == UIMode.Viewer)
            {
                ApplyViewerStatusLine("messageTimer viewer restore");
            }
        };
    }
    private void InitializeStartupSessionState()
    {
        if (SessionRestorePolicy.ShouldRestoreColumnCount(_settings.Session))
        {
            _columnCount = Math.Clamp(_settings.Session.LastColumnCount, 1, 9);
        }
        if (SessionRestorePolicy.ShouldRestoreSort(_settings.Session))
        {
            _currentSort = _settings.Session.LastSortKind;
            _sortAscending = _settings.Session.LastSortAscending;
        }
    }

    private void InitializeRuntimeTimersAndOverlay()
    {
        KeyUp += MainForm_KeyUp;
        Deactivate += (_, _) =>
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
            LogAltHintContext("Deactivate");
            _isAltHintHeld = false;
            HideCommandHintOverlay();
            UpdateFunctionBarShiftLayerState(false);
            UpdateFunctionBarCtrlLayerState(false);
            UpdateFunctionBarAltLayerState(false);
        };
        _commandHintOverlayTimer.Interval = 50;
        _commandHintOverlayTimer.Tick += (_, _) => RefreshCommandHintOverlayState();
        _commandHintOverlayTimer.Start();
        _directoryRefreshDebounceTimer.Interval = CurrentDirectoryRefreshDebounceMilliseconds;
        _directoryRefreshDebounceTimer.Tick += (_, _) =>
        {
            _directoryRefreshDebounceTimer.Stop();
            _navigationRefreshCoordinator.MarkRefreshDelayCompleted();
            TryProcessPendingCurrentDirectoryRefresh("DebounceTimer");
        };
        _directoryCountAuditTimer.Interval = DirectoryCountAuditSchedule.ActiveIntervalMilliseconds;
        _directoryCountAuditTimer.Tick += (_, _) => RunCurrentDirectoryCountAudit();
    }
    private string ResolveStartupPath()
    {
        // 初期パスの決定 (起動引数 -> 保存されたパス -> カレントディレクトリ)
        string startupPath = Environment.CurrentDirectory;
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Directory.Exists(args[1]))
        {
            startupPath = args[1];
        }
        else if (SessionRestorePolicy.ShouldRestoreStartupFolder(_settings.Session)
            && !string.IsNullOrEmpty(_settings.Session.LastPath)
            && Directory.Exists(_settings.Session.LastPath))
        {
            startupPath = _settings.Session.LastPath;
        }
        return startupPath;
    }
    private void InitializeMainUiSurface()
    {
        // ウィンドウ位置・サイズの復元
        if (SessionRestorePolicy.ShouldRestoreWindowBounds(_settings.Session))
        {
            RestoreWindowSettings();
        }
        // Phase 3-layout-fix6: Resize 配線を ApplyFontSettings より前に移動
        this.functionBarPanel.Resize += (s, e) => LayoutFunctionBar();
        InitializeHeaderDeclutterLayout();
        InitializeHeaderInteractionPolish();
        InitializeHeaderGestureInteraction();
        ApplyFontSettings();
        ApplyColorSettings();
        InitializeBrowserTabControl();
        InitializeMenuStrip();
        LogAltHintContext("InitializeMenuStrip");
    }
    private void RestoreBrowserStartupState(string startupPath)
    {
        bool workspaceRestoreEnabled = SessionRestorePolicy.ShouldRestoreStartupWorkspace(_settings.Session);
        int restoredTabCount = 0;
        int skippedTabCount = 0;
        bool hadSavedTabs = false;
        bool restoredTabs = workspaceRestoreEnabled && TryRestoreBrowserTabsOnStartup(out restoredTabCount, out skippedTabCount, out hadSavedTabs);
        if (!workspaceRestoreEnabled)
        {
            restoredTabCount = 0;
            skippedTabCount = 0;
            hadSavedTabs = false;
        }
        if (!restoredTabs)
        {
            LoadDirectory(startupPath);
            InitializeInitialBrowserTab();
        }
        if (!workspaceRestoreEnabled)
        {
            LogService.Info("[MarkPersistence] Legacy persisted marks restore skipped because workspace restore is disabled.");
        }
        else if (restoredTabs && (_restoredBrowserTabsFromWorkspaceStore || _browserTabViewState.Tabs.Any(tab => tab.MarkedPaths.Count > 0)))
        {
            LogService.Info("[MarkPersistence] Legacy persisted marks restore skipped because workspace/per-tab marks are authoritative.");
        }
        else
        {
            RestorePersistedMarksOnStartup();
            CaptureActiveBrowserTabState();
        }
        if (restoredTabs)
        {
            ShowStatusMessage(skippedTabCount > 0
                ? $"前回のタブ {restoredTabCount} 件を復元しました（{skippedTabCount} 件は見つからず除外）。"
                : $"前回のタブ {restoredTabCount} 件を復元しました。");
        }
        else if (workspaceRestoreEnabled && hadSavedTabs)
        {
            ShowStatusMessage("前回のタブは見つからないため、通常の開始状態で開きました。");
        }
        UpdateMenuStripState();
    }
    private void WireBrowserInputEvents()
    {
        this.fileListView.SelectedIndexChanged += FileListView_SelectedIndexChanged;
        // Phase 3-fix1c: browserPanel に対する基本マウス操作の追加
        this.browserPanel.MouseClick += BrowserPanel_MouseClick;
        this.browserPanel.MouseDoubleClick += BrowserPanel_MouseDoubleClick;
        // Phase 3-fix1d: ホイールスクロールの追加とフォーカス補助
        this.browserPanel.MouseWheel += BrowserPanel_MouseWheel;
        // Phase 3-fix2a: 外部 → MidFD Drag-in (Copy限定)
        this.browserPanel.AllowDrop = true;
        this.browserPanel.DragEnter += BrowserPanel_DragEnter;
        this.browserPanel.DragOver += BrowserPanel_DragOver;
        this.browserPanel.DragLeave += BrowserPanel_DragLeave;
        this.browserPanel.DragDrop += BrowserPanel_DragDrop;
        // Phase 3-fix2b: MidFD → 外部 Drag-out (Copy限定)
        this.browserPanel.MouseDown += BrowserPanel_MouseDown;
        this.browserPanel.MouseMove += BrowserPanel_MouseMove;
        this.browserPanel.MouseUp += BrowserPanel_MouseUp;
        this.browserPanel.MouseLeave += BrowserPanel_MouseLeave;
        this.browserPanel.MouseCaptureChanged += BrowserPanel_CaptureChanged;
        // Phase 3-layout-fix1: BrowserPanel のリサイズ再描画
        this.browserPanel.Resize += BrowserPanel_Resize;
    }
    private void WireWindowLifecycleEvents()
    {
        // popup の初期位置を MainForm の右側に設定する
        this.Load += (s, e) =>
        {
            // 起動時の初期配置あるいはオフスクリーン補正
            // Phase 5-image-preview-fix1.1: 起動時表示が必要な場合、保存座標の有無に関わらず PositionPreviewPopup を通して画面内補正を効かせる
            if (_previewPopupVisible || (_settings.Preview.X == -1 && !_previewPopup.IsManuallyPositioned))
            {
                PositionPreviewPopup();
            }
            // Phase 5-image-preview-fix1: 起動時に論理状態と視覚状態を同期する
            if (_previewPopupVisible)
            {
                _previewPopup.ShowWithoutFocus();
            }
        };
        this.FormClosing += (s, e) =>
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
            _isExitConfirmationPending = true;
            SettingsManager.SaveFailed -= HandleSettingsSaveFailed;
            _directoryRefreshDebounceTimer.Stop();
            StopDirectoryCountAudit(dispose: true);
            _headerStatusResizeDebounceTimer?.Stop();
            _headerStatusResizeDebounceTimer?.Dispose();
            _headerStatusResizeDebounceTimer = null;
            _updateInfoPanelDebounceTimer?.Stop();
            _updateInfoPanelDebounceTimer?.Dispose();
            _updateInfoPanelDebounceTimer = null;
            _uncDriveInfoResolver.Dispose();
            _markSummaryRebuildCoordinator.Dispose();
            _headerStatusResponsiveOwnedFont?.Dispose();
            _headerStatusResponsiveOwnedFont = null;
            CloseFileOperationProgressDialog();
            DisposeCurrentDirectoryWatcher();
            SaveWindowSettings();
            SavePreviewSettings();
            if (_browserItemContextMenu != null)
            {
                _browserItemContextMenu.Close();
                ClearAndDisposeMenuItems(_browserItemContextMenu);
                _browserItemContextMenu.Dispose();
                _browserItemContextMenu = null;
            }
            if (_browserBlankContextMenu != null)
            {
                _browserBlankContextMenu.Close();
                ClearAndDisposeMenuItems(_browserBlankContextMenu);
                _browserBlankContextMenu.Dispose();
                _browserBlankContextMenu = null;
            }
        };
        this.Move += (s, e) => PositionPreviewPopup();
        this.ClientSizeChanged += (s, e) =>
        {
            if (this.WindowState != FormWindowState.Minimized)
            {
                ScheduleHeaderStatusResponsiveFontRecompute("ClientSizeChanged");
            }
        };
        this.ResizeEnd += (s, e) =>
        {
            if (this.WindowState != FormWindowState.Minimized)
            {
                LogHeaderResponsiveStabilizeDiag("Finalize", "ResizeEnd", lblPage?.Font ?? GetHeaderStatusResponsiveBaseFont(), null, skippedReason: "force-final-recompute");
                RecomputeHeaderStatusResponsiveFontNow("ResizeEnd");
            }
        };
        this.Resize += (s, e) =>
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                // MainForm 最小化時は popup も隠す
                _previewPopup.Hide();
            }
            else
            {
                // 復元・最大化時: popup が論理的に表示中ならば再表示する
                // （_previewPopupVisible はユーザーが V で ON にしているかを示す）
                PositionPreviewPopup();
                if (_previewPopupVisible)
                {
                    _previewPopup.ShowWithoutFocus();
                }
                // Window bounds collapse guard: Normal state recovery
                if (this.WindowState == FormWindowState.Normal && !_isApplyingWindowBoundsRecovery)
                {
                    var currentBounds = this.Bounds;
                    bool isCollapsed = IsCollapsedWindowBounds(currentBounds);
                    bool isFloorHit = IsRestoreFloorHitCorruption(currentBounds);
                    bool isClientUnusable = !HasUsableClientArea();
                    if (isCollapsed || isFloorHit || isClientUnusable)
                    {
                        string reason = isCollapsed ? "Collapsed" : (isFloorHit ? "FloorHit" : "ClientUnusable");
                        RecoverCollapsedWindowBounds($"Resize({reason})");
                    }
                    else
                    {
                        TryCaptureCurrentNormalBounds();
                    }
                }

                ScheduleHeaderStatusResponsiveFontRecompute($"Resize:{this.WindowState}");
            }
        };
        this.DpiChanged += (s, e) => ScheduleHeaderStatusResponsiveFontRecompute("DpiChanged");
        this.Activated += MainForm_Activated;
        this.Shown += MainForm_Shown; // Phase 2g-fix6.2c: 初期フォーカス安定化
    }
    private void MainForm_Shown(object? sender, EventArgs e)
    {
        DragArchiveService.CleanupDragArchivesOnStartup(DragArchiveService.GetDragArchiveTempDirectory());
        _ = MidFdManagedTrashService.RunRetentionCleanupAsync(_settings, _fileOperationUndoRedoService, "Startup");

        // 初回表示レイアウト完了直後に確実にフォーカスを置く
        if (_uiMode == UIMode.Browser)
        {
            this.BeginInvoke(new Action(() =>
            {
                // Phase: header stream / initial final relayout corrective follow-up
                // ウィンドウ表示・サイズ確定後の最終レイアウトを保証する
                UpdateInfoPanel();
                EnsureTopLevelWindowVisible(this, "MainFormShown", new Size(160, 120));
                LayoutFunctionBar();
                UpdateFunctionBar();
                functionBarPanel.PerformLayout();
                functionBarPanel.Invalidate();
                ScheduleHeaderStatusResponsiveFontRecompute("ShownPostLayout");
                if (!browserPanel.Focused)
                {
                    browserPanel.Focus();
                }
            }));
        }
    }
    // ユーザーが V キーで ON にしているかどうかの論理状態
    private bool _previewPopupVisible = false;
    private void SaveWindowSettings()
    {
        if (this.WindowState == FormWindowState.Normal || this.WindowState == FormWindowState.Maximized)
        {
            Rectangle candidate = (this.WindowState == FormWindowState.Normal) ? this.Bounds : this.RestoreBounds;
            if (IsSaneNormalBounds(candidate) && !IsRestoreFloorHitCorruption(candidate) && HasUsableClientArea())
            {
                _settings.Window.X = candidate.X;
                _settings.Window.Y = candidate.Y;
                _settings.Window.Width = candidate.Width;
                _settings.Window.Height = candidate.Height;
                LogService.Info($"[WindowVisibility] SaveWindowSettings State={this.WindowState} SaneBounds={FormatBoundsForLog(candidate)}");
            }
            else
            {
                // Use fallback if candidate is collapsed, floor-hit, or unusable
                Rectangle? fallbackBounds = null;
                string fallbackSource = "";
                if (_normalBoundsBeforeMinimize is { } preMin && IsSaneNormalBounds(preMin))
                {
                    fallbackBounds = preMin;
                    fallbackSource = "PreMinimize";
                }
                else if (_restoreBaselineNormalBounds is { } baseline && IsSaneNormalBounds(baseline))
                {
                    fallbackBounds = baseline;
                    fallbackSource = "RestoreBaseline";
                }
                else
                {
                    var wp = new WINDOWPLACEMENT();
                    wp.length = Marshal.SizeOf(wp);
                    if (GetWindowPlacement(this.Handle, ref wp))
                    {
                        Rectangle placementRect = ToRectangle(wp.rcNormalPosition);
                        if (IsSaneNormalBounds(placementRect) && !IsRestoreFloorHitCorruption(placementRect))
                        {
                            fallbackBounds = placementRect;
                            fallbackSource = "PlacementNormal";
                        }
                    }
                }
                if (fallbackBounds == null && _lastKnownGoodNormalBounds is { } lastGood && IsSaneNormalBounds(lastGood))
                {
                    fallbackBounds = lastGood;
                    fallbackSource = "LastKnownGood";
                }
                if (fallbackBounds != null)
                {
                    _settings.Window.X = fallbackBounds.Value.X;
                    _settings.Window.Y = fallbackBounds.Value.Y;
                    _settings.Window.Width = fallbackBounds.Value.Width;
                    _settings.Window.Height = fallbackBounds.Value.Height;
                    LogService.Info($"[WindowRestoreFloorHit] SaveWindowSettings Applied Fallback. TriggerState={this.WindowState} TriggerBounds={FormatBoundsForLog(candidate)} Source={fallbackSource} Bounds={FormatBoundsForLog(fallbackBounds.Value)}");
                }
                else if (IsSaneNormalBounds(new Rectangle(_settings.Window.X, _settings.Window.Y, _settings.Window.Width, _settings.Window.Height)))
                {
                    // Existing settings are still sane, do not overwrite with corrupted values
                    LogService.Info($"[WindowRestoreFloorHit] SaveWindowSettings Skip. Current settings are still sane. TriggerState={this.WindowState} TriggerBounds={FormatBoundsForLog(candidate)}");
                }
                else
                {
                    // Everything is broken, use default safe
                    _settings.Window.X = -1;
                    _settings.Window.Y = -1;
                    _settings.Window.Width = 1024;
                    _settings.Window.Height = 768;
                    LogService.Info($"[WindowRestoreFloorHit] SaveWindowSettings Fallback DefaultSafe TriggerState={this.WindowState} TriggerBounds={FormatBoundsForLog(candidate)}");
                }
            }
        }
        _settings.Window.State = (this.WindowState == FormWindowState.Minimized)
            ? FormWindowState.Normal : this.WindowState;
        BrowserTabState? activeTabState = _browserTabViewState.ActiveTabIndex >= 0
            && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count
                ? _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex]
                : null;
        List<BrowserTabState> dirtyTabsBeforeSave = _browserTabViewState.Tabs
            .Where(static tab => tab.MarksDirty)
            .ToList();
        IReadOnlyList<string>? pendingEscMarks = _isClosingFromEscExitPath
            ? _pendingEscExitPersistedMarks
            : null;
        MarkPersistencePreparation markPreparation = _markPersistenceBoundaryCoordinator.Prepare(
            activeTabState?.MarksDirty ?? false,
            _markedFiles.Snapshot(),
            pendingEscMarks,
            PathExists);
        bool activeMarksWereDirty = activeTabState?.MarksDirty == true || markPreparation.UsedPendingEscSnapshot;
        CaptureActiveBrowserTabState(
            captureMarks: true,
            validateMarks: false,
            markSourceOverride: markPreparation.MarkedPaths,
            markValidationSucceeded: markPreparation.ValidationCount == 1);
        _settings.Session.LastPath = _navigationService.CurrentPath;
        if (!SessionRestorePolicy.ShouldRestoreStartupWorkspace(_settings.Session))
        {
            SavePersistedMarksToSettings(markPreparation.MarkedPaths, markPreparation.UsedPendingEscSnapshot);
        }
        else
        {
            LogService.Info("[MarkPersistence] Legacy persisted marks save skipped because workspace restore is enabled.");
        }
        SaveBrowserTabsToSettings();
        bool workspaceSaveSucceeded = SaveWorkspaceStateStore(captureActiveState: false);
        _settings.Session.LastColumnCount = _columnCount;
        _settings.Session.LastSortKind = _currentSort;
        _settings.Session.LastSortAscending = _sortAscending;
        LogService.Info($"[WindowVisibility] SaveWindowSettings State={this.WindowState} Bounds={FormatBoundsForLog(this.Bounds)} RestoreBounds={FormatBoundsForLog(this.RestoreBounds)} Saved=({_settings.Window.X},{_settings.Window.Y},{_settings.Window.Width},{_settings.Window.Height})");
        SettingsSqliteStore.SettingsSaveResult settingsSaveResult = SettingsManager.TrySave(_settings);
        bool markPersistenceSucceeded = workspaceSaveSucceeded && settingsSaveResult.Succeeded;
        if (activeTabState != null)
        {
            activeTabState.MarksDirty = _markPersistenceBoundaryCoordinator.ShouldRemainDirty(
                activeMarksWereDirty,
                markPreparation.ValidationCount,
                markPersistenceSucceeded);
        }
        if (markPersistenceSucceeded)
        {
            ClearPendingEscExitMarkPersistence();
        }
        else
        {
            foreach (BrowserTabState dirtyTab in dirtyTabsBeforeSave)
            {
                dirtyTab.MarksDirty = true;
            }
            if (!settingsSaveResult.Succeeded)
            {
                HandleSettingsSaveFailed(settingsSaveResult);
            }
        }
        int browserTabsSavedMarkCount = _settings.Session.RestoreTabsOnStartup && settingsSaveResult.Succeeded
            ? markPreparation.MarkedPaths.Count
            : 0;
        int workspaceSavedMarkCount = _settings.Session.RestoreTabsOnStartup && workspaceSaveSucceeded
            ? markPreparation.MarkedPaths.Count
            : 0;
        LogService.Info(
            $"[MarkPersistenceBoundary] source={markPreparation.SourceCount} persisted={markPreparation.MarkedPaths.Count} " +
            $"pendingEsc={markPreparation.UsedPendingEscSnapshot} validation={markPreparation.ValidationCount} " +
            $"browserTabs={browserTabsSavedMarkCount} workspace={workspaceSavedMarkCount} succeeded={markPersistenceSucceeded}");
    }
    private void SavePersistedMarksToSettings(IReadOnlyList<string> persistedPaths, bool usedPendingEscSnapshot)
    {
        _settings.Session ??= new SessionSettings();
        if (!_settings.Session.PersistMarksAcrossRestart)
        {
            LogService.Info("[MarkPersistence] Save skipped because persistence is disabled.");
            return;
        }
        _settings.Session.PersistedMarkedPaths = persistedPaths.ToList();
        string saveMode = usedPendingEscSnapshot
            ? "EscExitSnapshot"
            : "CurrentMarks";
        LogService.Info($"[MarkPersistence] Saved={persistedPaths.Count} Mode={saveMode}");
    }
    private void RestorePersistedMarksOnStartup()
    {
        _settings.Session ??= new SessionSettings();
        if (!_settings.Session.PersistMarksAcrossRestart)
        {
            LogService.Info("[MarkPersistence] Restore skipped because persistence is disabled.");
            return;
        }
        var savedPaths = _settings.Session.PersistedMarkedPaths ?? new List<string>();
        if (savedPaths.Count == 0)
        {
            LogService.Info("[MarkPersistence] Restore skipped because no persisted marks were found.");
            return;
        }
        var restoredPaths = new List<string>();
        int skippedCount = 0;
        foreach (var path in savedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PathExists(path))
            {
                restoredPaths.Add(path);
            }
            else
            {
                skippedCount++;
            }
        }
        if (restoredPaths.Count == 0)
        {
            LogService.Info($"[MarkPersistence] Restore skipped because all persisted paths were missing. Missing={skippedCount}");
            ShowStatusMessage("前回のマークは見つからないため復元しませんでした。");
            return;
        }
        RestoreMarks(restoredPaths, invalidateRedo: false);
        RefreshMarkUi();
        LogService.Info($"[MarkPersistence] Restored={restoredPaths.Count} Missing={skippedCount} OutOfDir={CountMarksOutsideCurrentDirectory()}");
        if (skippedCount > 0)
        {
            ShowStatusMessage($"前回のマーク {restoredPaths.Count} 件を復元しました（{skippedCount} 件は見つからず除外）。");
        }
        else
        {
            ShowStatusMessage($"前回のマーク {restoredPaths.Count} 件を復元しました。");
        }
    }
    private void RestoreWindowSettings()
    {
        if (_settings.Window.X != -1)
        {
            this.StartPosition = FormStartPosition.Manual;
            var requestedBounds = new Rectangle(
                _settings.Window.X,
                _settings.Window.Y,
                _settings.Window.Width,
                _settings.Window.Height);
            Rectangle restoredBounds;
            bool isSuspicious = requestedBounds.Height <= MinimumNormalWindowHeight + 4;
            // Reject collapsed or suspicious (floor-hit poisoned) settings
            if (!IsSaneNormalBounds(requestedBounds) || isSuspicious)
            {
                var primaryArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
                restoredBounds = new Rectangle(primaryArea.X + 100, primaryArea.Y + 100, 1024, 768);
                LogService.Warn($"[WindowRestoreFloorHit] RestoreWindowSettings detected {(isSuspicious ? "suspicious" : "collapsed")} settings {FormatBoundsForLog(requestedBounds)}. Falling back to default {FormatBoundsForLog(restoredBounds)}.");
            }
            else
            {
                restoredBounds = NormalizeWindowBoundsToVisibleArea(requestedBounds, new Size(160, 120));
            }
            this.SetBounds(restoredBounds.X, restoredBounds.Y, restoredBounds.Width, restoredBounds.Height);
            if (_settings.Window.State == FormWindowState.Maximized)
            {
                this.WindowState = FormWindowState.Maximized;
            }
            else if (this.WindowState == FormWindowState.Normal && this.Width < MinimumNormalWindowWidth)
            {
                this.Width = MinimumNormalWindowWidth;
            }
            LogService.Info($"[WindowVisibility] RestoreWindowSettings Requested={FormatBoundsForLog(requestedBounds)} Applied={FormatBoundsForLog(restoredBounds)} State={_settings.Window.State}");
            // Only trust as baseline if it's clearly above the floor
            if (restoredBounds.Height > MinimumNormalWindowHeight + 40)
            {
                _lastKnownGoodNormalBounds = restoredBounds;
                _restoreBaselineNormalBounds = restoredBounds;
            }
        }
    }
    // replace: InitializeBrowserTabControl
    private static Rectangle NormalizeWindowBoundsToVisibleArea(Rectangle desiredBounds, Size minimumVisibleSize)
    {
        int minimumWidth = Math.Max(1, minimumVisibleSize.Width);
        int minimumHeight = Math.Max(1, minimumVisibleSize.Height);
        desiredBounds = new Rectangle(
            desiredBounds.X,
            desiredBounds.Y,
            Math.Max(desiredBounds.Width, minimumWidth),
            Math.Max(desiredBounds.Height, minimumHeight));
        foreach (var screen in Screen.AllScreens)
        {
            var workingArea = screen.WorkingArea;
            var visibleArea = Rectangle.Intersect(desiredBounds, workingArea);
            if (visibleArea.Width >= minimumWidth && visibleArea.Height >= minimumHeight)
            {
                var adjustedBounds = desiredBounds;
                if (adjustedBounds.Width > workingArea.Width) adjustedBounds.Width = workingArea.Width;
                if (adjustedBounds.Height > workingArea.Height) adjustedBounds.Height = workingArea.Height;
                if (adjustedBounds.Right > workingArea.Right) adjustedBounds.X = workingArea.Right - adjustedBounds.Width;
                if (adjustedBounds.Bottom > workingArea.Bottom) adjustedBounds.Y = workingArea.Bottom - adjustedBounds.Height;
                if (adjustedBounds.X < workingArea.Left) adjustedBounds.X = workingArea.Left;
                if (adjustedBounds.Y < workingArea.Top) adjustedBounds.Y = workingArea.Top;
                return adjustedBounds;
            }
        }
        var fallbackArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        int width = Math.Min(desiredBounds.Width, fallbackArea.Width);
        int height = Math.Min(desiredBounds.Height, fallbackArea.Height);
        int x = fallbackArea.Left + Math.Max(0, (fallbackArea.Width - width) / 2);
        int y = fallbackArea.Top + Math.Max(0, (fallbackArea.Height - height) / 2);
        return new Rectangle(x, y, width, height);
    }
    private void EnsureTopLevelWindowVisible(Form form, string logContext, Size minimumVisibleSize)
    {
        if (form.IsDisposed)
        {
            return;
        }
        var originalState = form.WindowState;
        Rectangle beforeBounds = originalState == FormWindowState.Normal
            ? form.Bounds
            : form.RestoreBounds;
        Rectangle adjustedBounds = NormalizeWindowBoundsToVisibleArea(beforeBounds, minimumVisibleSize);
        bool adjusted = adjustedBounds != beforeBounds;
        if (adjusted)
        {
            bool restoreMaximized = originalState == FormWindowState.Maximized;
            if (originalState != FormWindowState.Normal)
            {
                form.WindowState = FormWindowState.Normal;
            }
            form.SetBounds(adjustedBounds.X, adjustedBounds.Y, adjustedBounds.Width, adjustedBounds.Height);
            if (restoreMaximized)
            {
                form.WindowState = FormWindowState.Maximized;
            }
        }
        LogService.Info($"[WindowVisibility] {logContext} State={originalState} Before={FormatBoundsForLog(beforeBounds)} After={FormatBoundsForLog(adjustedBounds)} Adjusted={adjusted}");
    }
    private static string FormatBoundsForLog(Rectangle bounds)
    {
        return WindowPlacementBoundsHelper.FormatBoundsForLog(bounds);
    }
    // ウィンドウ復元時の境界崩れを検出・補正する補助処理
    private static bool IsSaneNormalBounds(Rectangle bounds)
    {
        return WindowPlacementBoundsHelper.IsSaneNormalBounds(
            bounds,
            MinimumNormalWindowWidth,
            MinimumNormalWindowHeight);
    }
    private bool HasUsableClientArea()
    {
        if (_uiMode == UIMode.Browser)
        {
            return browserPanel != null && browserPanel.Height >= MinimumUsableClientAreaHeight;
        }
        else
        {
            return viewerPanel != null && viewerPanel.Height >= MinimumUsableClientAreaHeight;
        }
    }
    private void SavePreviewSettings()
    {
        _settings.Preview.IsVisible = _previewPopupVisible;
        _settings.Preview.X = _previewPopup.Left;
        _settings.Preview.Y = _previewPopup.Top;
        _settings.Preview.Width = _previewPopup.Width;
        _settings.Preview.Height = _previewPopup.Height;
        _settings.Preview.IsManuallyPositioned = _previewPopup.IsManuallyPositioned;
        _settings.Preview.ViewerWordWrap = viewerTextBox.WordWrap;
        SettingsManager.Save(_settings);
    }
    private void MainForm_Activated(object? sender, EventArgs e)
    {
        if (_previewPopupVisible && _previewPopup.Visible)
        {
            // MainForm がアクティブになったとき、popup を非アクティブのまま前面へ
            _previewPopup.BringToFrontOfOwner();
        }
        if (_uiMode == UIMode.Browser)
        {
            // browserPanel にフォーカスを強制回復（遅延実行で確実に本体へ戻す）
            this.BeginInvoke(() =>
            {
                if (!browserPanel.Focused)
                {
                    browserPanel.Focus();
                }
            });
            if (!_isExitConfirmationPending && !_isClosingFromEscExitPath)
            {
                TryProcessPendingCurrentDirectoryRefresh("Activated");
            }
        }
        // Window bounds collapse guard: Activated 譎ゅ↓ collapsed 迥ｶ諷九↑繧牙屓蠕ｩ
        if (this.WindowState == FormWindowState.Normal && !_isApplyingWindowBoundsRecovery)
        {
            if (IsCollapsedWindowBounds(this.Bounds))
            {
                this.BeginInvoke(() => RecoverCollapsedWindowBounds("Activated"));
            }
            else
            {
                TryCaptureCurrentNormalBounds();
            }
        }
    }
    private void LogAltHint(string message)
    {
        if (!HeaderStatusFontRouteDiagnosticLoggingEnabled)
        {
            return;
        }

        LogService.Info($"[AltHint] {message}");
    }
    private void LogBrowserImageImportInfo(string message)
    {
        LogService.Info($"[BrowserImageImport] {message}");
    }
    private void LogBrowserImageImportWarn(string message)
    {
        LogService.Warn($"[BrowserImageImport] {message}");
    }
    private bool IsCommandHintOverlayVisible()
    {
        return _commandHintRows.Count > 0;
    }
    private string DescribeControl(Control? control)
    {
        return control == null
            ? "<null>"
            : $"{control.GetType().Name}:{control.Name}";
    }
    private void LogAltHintContext(string eventName)
    {
        string parent = DescribeControl(mainMenuStrip.Parent);
        bool mainMenuMatches = ReferenceEquals(MainMenuStrip, mainMenuStrip);
        bool menuFocused = mainMenuStrip.Focused;
        bool menuContainsFocus = mainMenuStrip.ContainsFocus;
        LogAltHint($"{eventName} Parent={parent} MainMenuStripMatch={mainMenuMatches} ActiveControl={DescribeControl(ActiveControl)} FormContainsFocus={ContainsFocus} MenuFocused={menuFocused} MenuContainsFocus={menuContainsFocus}");
    }
    private bool IsMenuStripAltNavigationActive()
    {
        if (mainMenuStrip.Focused || mainMenuStrip.ContainsFocus)
        {
            return true;
        }
        foreach (ToolStripItem item in mainMenuStrip.Items)
        {
            if (item.Selected)
            {
                return true;
            }
            if (item is ToolStripDropDownItem dropDownItem && dropDownItem.DropDown.Visible)
            {
                return true;
            }
        }
        return false;
    }
    private Rectangle GetCommandHintOverlayBounds()
    {
        return CommandHintOverlayLayout.GetBounds(
            browserPanel.ClientSize,
            _commandHintRows.Count,
            CommandHintOverlayLayout.DefaultMetrics);
    }
    private void DrawCommandHintOverlay(Graphics g)
    {
        if (_commandHintRows.Count == 0)
        {
            return;
        }
        Rectangle overlayRect = GetCommandHintOverlayBounds();
        if (overlayRect.Width <= 0 || overlayRect.Height <= 0)
        {
            return;
        }
        Size panelSize = browserPanel.ClientSize;
        CommandHintOverlayLayout.Metrics metrics = CommandHintOverlayLayout.DefaultMetrics;
        if (_lastLoggedCommandHintRowCount != _commandHintRows.Count ||
            _lastLoggedCommandHintBounds != overlayRect ||
            _lastLoggedCommandHintPanelSize != panelSize)
        {
            string firstRow = _commandHintRows.Count > 0
                ? $"{_commandHintRows[0].SlotLabel}:{_commandHintRows[0].Title}:{_commandHintRows[0].StatusText}"
                : "<none>";
            LogAltHint($"DrawCommandHintOverlay Bounds={overlayRect} Panel={panelSize} RowCount={_commandHintRows.Count} First={firstRow}");
            _lastLoggedCommandHintRowCount = _commandHintRows.Count;
            _lastLoggedCommandHintBounds = overlayRect;
            _lastLoggedCommandHintPanelSize = panelSize;
        }
        using SolidBrush backgroundBrush = new(Color.FromArgb(238, 0, 0, 0));
        using Pen borderPen = new(MidFDColors.BorderLine);
        using Pen separatorPen = new(Color.FromArgb(0, 120, 120));
        using Font titleFont = new("Consolas", 11F, FontStyle.Bold, GraphicsUnit.Point);
        using Font bodyFont = new("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        g.FillRectangle(backgroundBrush, overlayRect);
        g.DrawRectangle(borderPen, overlayRect);
        int padding = metrics.Padding;
        int contentWidth = overlayRect.Width - (padding * 2);
        int slotWidth = 120;
        int titleWidth = Math.Max(180, (contentWidth * 30) / 100);
        int exeWidth = Math.Max(180, (contentWidth * 28) / 100);
        int statusWidth = Math.Max(108, contentWidth - slotWidth - titleWidth - exeWidth);
        Rectangle titleRect = new(overlayRect.Left + padding, overlayRect.Top + padding - 2, contentWidth, metrics.TitleHeight);
        TextRenderer.DrawText(
            g,
            "External Tool Alt Slot Launcher",
            titleFont,
            titleRect,
            Color.Yellow,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Rectangle explanationRect = new(overlayRect.Left + padding, titleRect.Bottom + metrics.TitleGap, contentWidth, metrics.ExplanationHeight);
        TextRenderer.DrawText(
            g,
            "Alt+英数字は外部ツールの namespace。Alt+F1〜F12 は Function layer と別です。",
            bodyFont,
            explanationRect,
            MidFDColors.ListNormalFore,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        Rectangle contextRect = new(overlayRect.Left + padding, explanationRect.Bottom + 2, contentWidth, metrics.ContextLineHeight);
        TextRenderer.DrawText(
            g,
            string.IsNullOrWhiteSpace(_commandHintContextLine1) ? "Target: (unknown)" : _commandHintContextLine1,
            bodyFont,
            contextRect,
            MidFDColors.ListNormalFore,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Rectangle contextLine2Rect = new(overlayRect.Left + padding, contextRect.Bottom + metrics.ContextLineSpacing, contentWidth, metrics.ContextLineHeight);
        TextRenderer.DrawText(
            g,
            string.IsNullOrWhiteSpace(_commandHintContextLine2)
                ? "Selected: (unknown)"
                : _commandHintContextLine2,
            bodyFont,
            contextLine2Rect,
            MidFDColors.ListNormalFore,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        int headerTop = contextLine2Rect.Bottom + metrics.ContextGap;
        g.DrawLine(separatorPen, overlayRect.Left + padding, headerTop - 4, overlayRect.Right - padding, headerTop - 4);
        Rectangle slotHeaderRect = new(overlayRect.Left + padding, headerTop, slotWidth, metrics.HeaderHeight);
        Rectangle titleHeaderRect = new(slotHeaderRect.Right, headerTop, titleWidth, metrics.HeaderHeight);
        Rectangle exeHeaderRect = new(titleHeaderRect.Right, headerTop, exeWidth, metrics.HeaderHeight);
        Rectangle statusHeaderRect = new(exeHeaderRect.Right, headerTop, statusWidth, metrics.HeaderHeight);
        TextRenderer.DrawText(g, "Slot", bodyFont, slotHeaderRect, Color.Yellow, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Title", bodyFont, titleHeaderRect, Color.Yellow, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Exe", bodyFont, exeHeaderRect, Color.Yellow, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Status", bodyFont, statusHeaderRect, Color.Yellow, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        int rowTop = slotHeaderRect.Bottom + 4;
        int rowHeight = metrics.RowHeight;
        int visibleRows = Math.Max(1, (overlayRect.Bottom - padding - rowTop) / rowHeight);
        int maxScroll = Math.Max(0, _commandHintRows.Count - visibleRows);
        _commandHintScrollIndex = Math.Clamp(_commandHintScrollIndex, 0, maxScroll);
        if (_commandHintSelectedIndex >= 0 && _commandHintSelectedIndex < _commandHintRows.Count)
        {
            if (_commandHintSelectedIndex < _commandHintScrollIndex)
            {
                _commandHintScrollIndex = _commandHintSelectedIndex;
            }
            else if (_commandHintSelectedIndex >= _commandHintScrollIndex + visibleRows)
            {
                _commandHintScrollIndex = _commandHintSelectedIndex - visibleRows + 1;
            }
            _commandHintScrollIndex = Math.Clamp(_commandHintScrollIndex, 0, maxScroll);
        }
        int startIndex = Math.Clamp(_commandHintScrollIndex, 0, Math.Max(0, _commandHintRows.Count - 1));
        int endIndex = Math.Min(_commandHintRows.Count, startIndex + visibleRows);
        for (int i = startIndex; i < endIndex; i++)
        {
            ExternalToolAltHintRow row = _commandHintRows[i];
            int rowIndex = i - startIndex;
            int top = rowTop + (rowIndex * rowHeight);
            Rectangle slotRect = new(overlayRect.Left + padding, top, slotWidth, rowHeight);
            Rectangle titleRectRow = new(slotRect.Right, top, titleWidth, rowHeight);
            Rectangle exeRectRow = new(titleRectRow.Right, top, exeWidth, rowHeight);
            Rectangle statusRectRow = new(exeRectRow.Right, top, statusWidth, rowHeight);
            bool isSelected = i == _commandHintSelectedIndex;
            if (isSelected)
            {
                using SolidBrush selectionBrush = new(Color.FromArgb(120, MidFDColors.ListSelectedBack));
                g.FillRectangle(selectionBrush, new Rectangle(overlayRect.Left + padding - 2, top, contentWidth, rowHeight));
            }
            Color statusColor = row.IsLaunchable ? Color.LightGreen : Color.LightGray;
            TextRenderer.DrawText(g, row.SlotLabel, bodyFont, slotRect, MidFDColors.ListNormalFore, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, row.Title, bodyFont, titleRectRow, MidFDColors.ListNormalFore, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, row.ExecutableName, bodyFont, exeRectRow, Color.White, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, row.StatusText, bodyFont, statusRectRow, statusColor, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (isSelected)
            {
                using Pen selectionBorderPen = new(Color.FromArgb(180, Color.Cyan));
                g.DrawRectangle(selectionBorderPen, new Rectangle(overlayRect.Left + padding - 2, top, contentWidth, rowHeight));
            }
        }
        if (_commandHintRows.Count > visibleRows)
        {
            int remain = _commandHintRows.Count - endIndex;
            Rectangle moreRect = new(overlayRect.Left + padding, rowTop + (visibleRows * rowHeight), contentWidth, rowHeight);
            TextRenderer.DrawText(
                g,
                $"ほか {remain} 件 / ↑↓ で選択 / Enter で起動 / Esc で閉じる",
                bodyFont,
                moreRect,
                Color.Yellow,
                Color.Transparent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        if (_commandHintRows.Count == 0)
        {
            Rectangle emptyRect = new(overlayRect.Left + padding, rowTop, contentWidth, rowHeight);
            TextRenderer.DrawText(
                g,
                "Alt 直起動に割当済みのスロットがありません",
                bodyFont,
                emptyRect,
                MidFDColors.ListNormalFore,
                Color.Transparent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    private void UpdateBrowserToolbarVisibility()
    {
        bool show = _settings.Appearance?.ShowBrowserToolbar ?? false;
        if (_btnMenuBack != null) _btnMenuBack.Visible = show;
        if (_btnMenuForward != null) _btnMenuForward.Visible = show;
        if (_btnMenuUp != null) _btnMenuUp.Visible = show;
        if (_btnMenuReload != null) _btnMenuReload.Visible = show;
        if (_menuNavSeparator != null) _menuNavSeparator.Visible = show;
    }
    private void InitializeMenuStrip()
    {
        mainMenuStrip.Items.Clear();
        _browserOnlyMenuItems.Clear();
        _busyAwareMenuItems.Clear();
        _menuItemRules.Clear();
        var menuBuildContext = new MainMenuConstructionCoordinator.BuildContext
        {
            CreateMenuItem = (text, onClick, browserOnly, requiresIdle, requiresSelection, requiresFile, requiresEditorTarget, requiresExactlyTwoSelection, requiresTwoFiles, shortcutHint) =>
                CreateMenuItem(text, onClick, browserOnly, requiresIdle, requiresSelection, requiresFile, requiresEditorTarget, requiresExactlyTwoSelection, requiresTwoFiles, shortcutHint),
            GetFunctionAwareShortcutHint = (action, defaultShortcut, fdCompatibleShortcut) => GetFunctionAwareShortcutHint(action, defaultShortcut, fdCompatibleShortcut),
            IsWorkspaceSnapshotEnabled = () => _featureGate.IsEnabled(FeatureId.WorkspaceSnapshot),
            ExecuteCurrentFile = () => ExecuteCurrentFile(),
            ExecuteAttribute = () => ExecuteAttribute(),
            ExecuteCopy = () => _ = ExecuteCopy(),
            ExecuteMove = () => _ = ExecuteMove(),
            ExecuteRename = () => ExecuteRename(),
            ExecuteDelete = () => _ = ExecuteDelete(),
            EmptyMidFdManagedTrash = () => EmptyMidFdManagedTrash(),
            ExecuteCreateDirectory = () => ExecuteCreateDirectory(),
            ExecuteCreateFile = () => ExecuteCreateFile(),
            CloseMainForm = () => Close(),
            ExecuteSort = () => ExecuteSort(),
            ExecuteFilter = () => ExecuteFilter(),
            SetFileDisplayModeNameOnly = () => SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameOnly),
            SetFileDisplayModeNameSize = () => SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameSize),
            SetFileDisplayModeNameSizeDate = () => SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameSizeDate),
            UpdateFileDisplayModeMenuChecks = () => UpdateFileDisplayModeMenuChecks(),
            ReloadCurrentDirectory = () => ExecuteCommandFromUi(CommandIds.BrowserReload, CommandScope.Browser, "Menu.View.Reload"),
            OpenActiveTabFilterLockDialog = () => OpenActiveTabFilterLockDialog(),
            ClearActiveTabFilterLock = () => ClearActiveTabFilterLock(),
            ExecutePreviewLaunch = () => ExecutePreviewLaunch(),
            ExecuteLogdisk = () => ExecuteLogdisk(),
            OpenFileListColorSettings = () => OpenFileListColorSettings(),
            NavigateParent = () => ExecuteCommandFromUi(CommandIds.BrowserNavigateParent, CommandScope.Browser, "Menu.Move.Parent"),
            ExecuteDriveRoot = () => ExecuteDriveRoot(),
            OpenExplorer = () => ExecuteCommandFromUi(CommandIds.BrowserOpenExplorer, CommandScope.Browser, "Menu.Move.OpenExplorer"),
            ExecuteTop = () => ExecuteFunctionKey(11),
            ExecuteBottom = () => ExecuteFunctionKey(12),
            ExecuteTreeDialog = () => ExecuteTreeDialog(),
            ExecuteQuickAccess = () => ExecuteQuickAccess(),
            NavigateBack = () => ExecuteCommandFromUi(CommandIds.BrowserNavigateBack, CommandScope.Browser, "Menu.Move.HistoryBack"),
            NavigateForward = () => ExecuteCommandFromUi(CommandIds.BrowserNavigateForward, CommandScope.Browser, "Menu.Move.HistoryForward"),
            CreateNewBrowserTab = () => CreateNewBrowserTab(),
            ToggleActiveBrowserTabLock = () => ToggleActiveBrowserTabLock(),
            ToggleActiveBrowserTabReadOnly = () => ToggleActiveBrowserTabReadOnly(),
            SelectNextBrowserTab = () => SelectAdjacentBrowserTab(+1),
            SelectPreviousBrowserTab = () => SelectAdjacentBrowserTab(-1),
            CloseCurrentBrowserTab = () => CloseCurrentBrowserTab(),
            ExecutePack = () => _ = ExecutePack(),
            ExecuteUnpack = () => _ = ExecuteUnpack(),
            ExecuteOpenWithEditor = () => ExecuteOpenWithEditor(),
            ExecuteOpenWithDiff = () => ExecuteOpenWithDiff(),
            OpenPowerShell = () => ExecuteCommandFromUi(CommandIds.BrowserOpenShell, CommandScope.Browser, "Menu.Tools.OpenShell"),
            CopyFullPath = () => ExecuteCommandFromUi(CommandIds.BrowserCopyFullPath, CommandScope.Browser, "Menu.Tools.CopyFullPath"),
            OpenMarkSlotDialog = () => OpenMarkSlotDialog(),
            OpenWorkspaceSnapshotDialog = () => OpenWorkspaceSnapshotDialog(),
            ShowSystemInformation = () => OpenSystemInformationFromUi("Menu.Tools.SystemInformation"),
            OpenSettings = () => ExecuteCommandFromUi(CommandIds.AppOpenSettings, CommandScope.Global, "Menu.Tools.Settings"),
            OpenManagedTrashDialog = () => ExecuteCommandFromUi(CommandIds.AppOpenManagedTrash, CommandScope.Global, "Menu.Tools.ManagedTrash"),
            ShowMenuKeyHint = () => ShowMenuKeyHint(),
            ShowCommandList = () => ShowCommandList(),
            ShowVersionInfo = () => ShowVersionInfo()
        };
        MainMenuConstructionCoordinator.BuildResult menuBuildResult = new MainMenuConstructionCoordinator().Build(menuBuildContext);
        _fileDisplayModeNameOnlyMenuItem = menuBuildResult.FileDisplayModeNameOnlyMenuItem;
        _fileDisplayModeNameSizeMenuItem = menuBuildResult.FileDisplayModeNameSizeMenuItem;
        _fileDisplayModeNameSizeDateMenuItem = menuBuildResult.FileDisplayModeNameSizeDateMenuItem;
        _reloadCurrentDirectoryMenuItem = menuBuildResult.ReloadCurrentDirectoryMenuItem;
        _clearTabFilterLockMenuItem = menuBuildResult.ClearTabFilterLockMenuItem;
        _toggleBrowserTabLockMenuItem = menuBuildResult.ToggleBrowserTabLockMenuItem;
        _toggleBrowserTabReadOnlyMenuItem = menuBuildResult.ToggleBrowserTabReadOnlyMenuItem;
        ToolStripMenuItem viewMenu = menuBuildResult.ViewMenu;
        ToolStripMenuItem moveMenu = menuBuildResult.MoveMenu;
        moveMenu.DropDownOpening += (s, e) =>
        {
            if (_toggleBrowserTabLockMenuItem != null)
            {
                _toggleBrowserTabLockMenuItem.Text = IsActiveBrowserTabLocked()
                    ? "現在のタブ固定を解除(&K)"
                    : "現在のタブを固定(&K)";
            }
            if (_toggleBrowserTabReadOnlyMenuItem != null)
            {
                _toggleBrowserTabReadOnlyMenuItem.Text = IsActiveBrowserTabReadOnly()
                    ? "現在のタブの ReadOnly を解除(&Y)"
                    : "現在のタブを ReadOnly にする(&Y)";
            }
        };
        ToolStripMenuItem favoritesMenu = new ToolStripMenuItem("お気に入り(&A)") { Name = "favoritesMenu" };
        favoritesMenu.DropDownOpening += (s, e) => BuildFavoritesMenu(favoritesMenu, QuickAccessService.GetRegisteredEntries(_quickAccessStore));

        _btnMenuBack = new ToolStripButton
        {
            Text = "←戻る",
            ToolTipText = "戻る (Alt+Left)",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font("Yu Gothic UI", 9F),
            Padding = new Padding(2, 3, 2, 3),
            Margin = new Padding(0, 0, 0, 0)
        };
        _btnMenuBack.Click += (s, e) => ExecuteCommandFromUi(CommandIds.BrowserNavigateBack, CommandScope.Browser, "Menu.NavigateBack");

        _btnMenuForward = new ToolStripButton
        {
            Text = "→進む",
            ToolTipText = "進む (Alt+Right)",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font("Yu Gothic UI", 9F),
            Padding = new Padding(2, 3, 2, 3),
            Margin = new Padding(0, 0, 0, 0)
        };
        _btnMenuForward.Click += (s, e) => ExecuteCommandFromUi(CommandIds.BrowserNavigateForward, CommandScope.Browser, "Menu.NavigateForward");

        _btnMenuUp = new ToolStripButton
        {
            Text = "↑上へ",
            ToolTipText = "親フォルダへ (Backspace / Alt+Up)",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font("Yu Gothic UI", 9F),
            Padding = new Padding(2, 3, 2, 3),
            Margin = new Padding(0, 0, 0, 0)
        };
        _btnMenuUp.Click += (s, e) => ExecuteCommandFromUi(CommandIds.BrowserNavigateParent, CommandScope.Browser, "Menu.NavigateParent");

        _btnMenuReload = new ToolStripButton
        {
            Text = "↻更新",
            ToolTipText = "再読込 (Ctrl+R / F5)",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Font = new Font("Yu Gothic UI", 9F),
            Padding = new Padding(2, 3, 2, 3),
            Margin = new Padding(0, 0, 0, 0)
        };
        _btnMenuReload.Click += (s, e) => ExecuteCommandFromUi(CommandIds.BrowserReload, CommandScope.Browser, "Menu.Reload");

        _menuNavSeparator = new ToolStripLabel("│")
        {
            ForeColor = Color.FromArgb(80, 128, 128, 128),
            Font = new Font("Yu Gothic UI", 9F),
            Margin = new Padding(4, 0, 4, 0)
        };

        mainMenuStrip.Items.AddRange(new ToolStripItem[]
        {
            _btnMenuBack,
            _btnMenuForward,
            _btnMenuUp,
            _btnMenuReload,
            _menuNavSeparator,
            menuBuildResult.FileMenu,
            menuBuildResult.ViewMenu,
            menuBuildResult.MoveMenu,
            favoritesMenu,
            menuBuildResult.ToolsMenu,
            menuBuildResult.HelpMenu
        });

        string menuPreset = UiThemeResolver.MapFromDisplayColor(_settings.Appearance?.ColorTheme);
        var menuThemeColors = UiThemeResolver.Resolve(menuPreset);
        ApplyMenuStripRenderer(
            FileListColorResolver.NormalizeCoreTheme(_settings.Appearance?.ColorTheme, _settings) == "Light",
            menuThemeColors.ChromeForeColor);

        mainMenuStrip.ContextMenuStrip = new ContextMenuStrip();
        var hideItem = new ToolStripMenuItem("戻る・進む・上へ・更新ボタンを非表示にする");
        hideItem.Click += (s, e) =>
        {
            if (_settings.Appearance != null)
            {
                _settings.Appearance.ShowBrowserToolbar = false;
                UpdateBrowserToolbarVisibility();
                SettingsManager.Save(_settings);
            }
        };
        mainMenuStrip.ContextMenuStrip.Items.Add(hideItem);
        UpdateBrowserToolbarVisibility();

        foreach (ToolStripMenuItem rootMenu in mainMenuStrip.Items.OfType<ToolStripMenuItem>())
        {
            rootMenu.DropDownOpening += (s, e) =>
            {
                RefreshMenuStripRuntimeLayout($"DropDownOpening:{rootMenu.Text}", defer: false);
            };
            rootMenu.DropDownOpened += (s, e) =>
            {
                LogMenuStripLayoutMetrics($"DropDownOpened:{rootMenu.Text}");
            };
        }
        WireMenuStripLifetimeEvents();
        SynchronizeMenuStripFontAndLayout(CreateMenuStripFont());
        LogMenuStripLayoutMetrics("InitializeMenuStrip");
    }
    private void BuildFavoritesMenu(ToolStripMenuItem favoritesMenu, IReadOnlyList<QuickAccessEntry> entries)
    {
        Action<ToolStripDropDownItem, Color, Color>? applyTheme = null;
        Color themeBackColor = Color.Empty;
        Color themeForeColor = Color.Empty;
        if (_settings.Appearance != null)
        {
            var uiThemeColors = UiThemeResolver.Resolve(_settings.Appearance);
            themeBackColor = uiThemeColors.ChromeBackColor;
            themeForeColor = uiThemeColors.ChromeForeColor;
            applyTheme = ApplyDropDownTheme;
        }

        FavoritesMenuPresenter.Build(
            favoritesMenu,
            entries,
            NavigateToPathSafe,
            AddCurrentLocationToFavorites,
            ExecuteQuickAccess,
            applyTheme,
            themeBackColor,
            themeForeColor);
    }

    private void AddCurrentLocationToFavorites()
    {
        AddBrowserPathToFavorites(_navigationService.CurrentPath, _navigationService.CurrentPath);
    }

    private void AddSelectedBrowserItemToFavorites()
    {
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy())
        {
            return;
        }

        ListViewItem? item = GetCurrentBrowserItem();
        if (item == null || item.Text == "..")
        {
            ShowStatusMessage("QuickAccess に登録できる項目がありません。");
            return;
        }

        string? itemPath = item.Tag as string;
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            ShowStatusMessage("QuickAccess に登録できる項目がありません。");
            return;
        }

        AddBrowserPathToFavorites(itemPath, _navigationService.CurrentPath);
    }

    private void AddBrowserPathToFavorites(string pathToRegister, string currentPath)
    {
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy())
        {
            return;
        }

        string initialDisplayName = QuickAccessService.CreateDisplayName(pathToRegister);
        QuickAccessLocationDialogResult? dialogResult = QuickAccessLocationDialog.ShowEditor(
            this,
            "QuickAccess 登録",
            currentPath,
            pathToRegister,
            initialDisplayName,
            null,
            QuickAccessService.GetKnownCategoryNames(_quickAccessStore),
            initialUseForTabTitle: false);
        if (dialogResult == null)
        {
            return;
        }

        if (QuickAccessService.TrySaveManagedLocationEntry(
            _quickAccessStore,
            null,
            dialogResult.DisplayName,
            dialogResult.Path,
            dialogResult.CategoryName,
            dialogResult.UseForTabTitle,
            currentPath,
            out _,
            out string message))
        {
            QuickAccessService.Save(_quickAccessStore);
            RefreshAllBrowserTabTitles();
            ShowStatusMessage(message);
            return;
        }

        MessageBox.Show(message, "QuickAccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void NavigateToPathSafe(string path)
    {
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return;
        string resolved = _navigationService.NormalizeDestinationDirectory(path);
        try
        {
            ExecuteDirectoryNavigationRequest(
                _browserNavigationCoordinator.CreateDirectoryNavigationRequest(resolved),
                onDirectoryMissing: p => MessageBox.Show($"指定されたパスが見つかりません: {p}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void WireMenuStripLifetimeEvents()
    {
        if (mainMenuStrip == null)
        {
            return;
        }
        mainMenuStrip.MenuActivate -= HandleMenuStripMenuActivate;
        mainMenuStrip.MenuDeactivate -= HandleMenuStripMenuDeactivate;
        mainMenuStrip.MenuActivate += HandleMenuStripMenuActivate;
        mainMenuStrip.MenuDeactivate += HandleMenuStripMenuDeactivate;
    }
    private void HandleMenuStripMenuActivate(object? sender, EventArgs e)
    {
        LogAltHint($"MenuActivate AltOwned={_isExternalToolAltPopupAltOwned} OverlayVisible={IsCommandHintOverlayVisible()} ActiveControl={DescribeControl(ActiveControl)}");
        LogAltHintContext("MenuActivate");
        if (_isExternalToolAltPopupAltOwned && !_isOpeningMenuStripExplicitly)
        {
            LogAltHint("MenuActivate ignored because external tool alt popup owns Alt state");
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && IsHandleCreated && _uiMode == UIMode.Browser && browserPanel.Visible)
                {
                    browserPanel.Focus();
                    RefreshCommandHintOverlayState();
                }
            }));
            return;
        }
        _isAltHintHeld = false;
        _isExternalToolAltPopupAltOwned = false;
        HideCommandHintOverlay("MenuActivate");
        UpdateMenuStripState();
        RefreshMenuStripRuntimeLayout("MenuActivate", defer: false);
    }
    private void HandleMenuStripMenuDeactivate(object? sender, EventArgs e)
    {
        LogAltHint($"MenuDeactivate AltOwned={_isExternalToolAltPopupAltOwned} OverlayVisible={IsCommandHintOverlayVisible()} ActiveControl={DescribeControl(ActiveControl)}");
        LogAltHintContext("MenuDeactivate");
        _isOpeningMenuStripExplicitly = false;
        RefreshCommandHintOverlayState();
    }
    private Font CreateMenuStripFont()
    {
        return SystemFonts.MenuFont ?? mainMenuStrip?.Font ?? this.Font;
    }
    private void ApplyMenuStripRenderer(bool isLightPalette, Color commandTextColor)
    {
        MenuStripPresentationHelper.ApplyRenderer(mainMenuStrip, isLightPalette, commandTextColor);
    }

    private void SynchronizeMenuStripFontAndLayout(Font menuFont)
    {
        var menuThemeColors = UiThemeResolver.Resolve(UiThemeResolver.MapFromDisplayColor(_settings.Appearance?.ColorTheme));
        MenuStripPresentationHelper.SynchronizeFontAndLayout(
            mainMenuStrip,
            menuFont,
            FileListColorResolver.NormalizeCoreTheme(_settings.Appearance?.ColorTheme, _settings) == "Light",
            menuThemeColors.ChromeForeColor);
    }
    private void RefreshMenuStripRuntimeLayout(string context, bool defer)
    {
        if (mainMenuStrip == null || !IsHandleCreated)
        {
            return;
        }
        void ApplyLayout()
        {
            if (mainMenuStrip == null || mainMenuStrip.IsDisposed)
            {
                return;
            }
            SynchronizeMenuStripFontAndLayout(CreateMenuStripFont());
            mainMenuStrip.PerformLayout();
            foreach (ToolStripMenuItem rootMenu in mainMenuStrip.Items.OfType<ToolStripMenuItem>())
            {
                rootMenu.DropDown.PerformLayout();
                rootMenu.DropDown.Update();
            }
            mainMenuStrip.Update();
            LogMenuStripLayoutMetrics(context);
        }
        if (defer)
        {
            BeginInvoke((Action)ApplyLayout);
            return;
        }
        ApplyLayout();
    }
    private void RebuildMenuStripAfterSettingsApply()
    {
        if (mainMenuStrip == null || !IsHandleCreated)
        {
            return;
        }
        InitializeMenuStrip();
        UpdateMenuStripState();
        RefreshMenuStripRuntimeLayout("OpenSettingsForm:RebuildImmediate", defer: false);
        BeginInvoke((Action)(() =>
        {
            if (mainMenuStrip == null || mainMenuStrip.IsDisposed)
            {
                return;
            }
            RefreshMenuStripRuntimeLayout("OpenSettingsForm:RebuildDeferred1", defer: false);
            BeginInvoke((Action)(() =>
            {
                if (mainMenuStrip == null || mainMenuStrip.IsDisposed)
                {
                    return;
                }
                RefreshMenuStripRuntimeLayout("OpenSettingsForm:RebuildDeferred2", defer: false);
            }));
        }));
    }
    private void LogMenuStripLayoutMetrics(string context)
    {
        if (mainMenuStrip == null)
        {
            return;
        }
        string menuFont = $"{mainMenuStrip.Font.FontFamily.Name},{mainMenuStrip.Font.SizeInPoints:0.##}pt,{mainMenuStrip.Font.Style}";
        string padding = $"{mainMenuStrip.Padding.Left},{mainMenuStrip.Padding.Top},{mainMenuStrip.Padding.Right},{mainMenuStrip.Padding.Bottom}";
        string rootMetrics = string.Join(" | ", mainMenuStrip.Items
            .OfType<ToolStripMenuItem>()
            .Select(item =>
            {
                Point ownerScreenOrigin = mainMenuStrip.PointToScreen(item.Bounds.Location);
                int ownerScreenTop = ownerScreenOrigin.Y;
                int ownerScreenBottom = ownerScreenOrigin.Y + item.Bounds.Height;
                int dropDownScreenTop = item.DropDown.Visible ? item.DropDown.Bounds.Top : -1;
                string delta = dropDownScreenTop >= 0 ? (dropDownScreenTop - ownerScreenBottom).ToString() : "n/a";
                return $"{item.Text}:OwnerScreenTop={ownerScreenTop},OwnerScreenBottom={ownerScreenBottom},DropDownScreenTop={dropDownScreenTop},Delta={delta}";
            }));
        LogService.Info($"[MenuStripLayout] {context} Font={menuFont} Height={mainMenuStrip.Height} Padding={padding} Metrics={rootMetrics}");
    }
    private ToolStripMenuItem CreateMenuItem(
        string text,
        EventHandler onClick,
        bool browserOnly = false,
        bool requiresIdle = false,
        bool requiresSelection = false,
        bool requiresFile = false,
        bool requiresEditorTarget = false,
        bool requiresExactlyTwoSelection = false,
        bool requiresTwoFiles = false,
        string? shortcutHint = null)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += onClick;
        if (!string.IsNullOrWhiteSpace(shortcutHint))
        {
            item.ShortcutKeyDisplayString = shortcutHint;
        }
        if (browserOnly)
        {
            _browserOnlyMenuItems.Add(item);
        }
        if (requiresIdle)
        {
            _busyAwareMenuItems.Add(item);
        }
        if (requiresSelection || requiresFile || requiresEditorTarget || requiresExactlyTwoSelection || requiresTwoFiles)
        {
            _menuItemRules[item] = new CommandStateCoordinator.MenuItemStateRule(
                requiresSelection,
                requiresFile,
                requiresEditorTarget,
                requiresExactlyTwoSelection,
                requiresTwoFiles);
        }
        return item;
    }
    private void EmptyMidFdManagedTrash()
    {
        DialogResult result = MessageBox.Show(
            "MidFD管理ゴミ箱を空にします。この操作後、MidFDの削除Undo/Redoはできなくなります。よろしいですか？",
            "MidFD管理ゴミ箱を空にする",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
        {
            return;
        }
        try
        {
            MidFdManagedTrashService.EmptyTrash();
            _fileOperationUndoRedoService.ClearTrashDeleteBatches();
            ShowStatusMessage("MidFD管理ゴミ箱を空にしました。");

            string currentPath = _navigationService.CurrentPath;
            if (!string.IsNullOrEmpty(currentPath) && currentPath.Contains(".midfd-trash", StringComparison.OrdinalIgnoreCase))
            {
                ReloadCurrentDirectory("管理ゴミ箱を空にしたため再読込しました。", force: true);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("MidFD managed trash empty failed.", ex);
            ShowStatusMessage($"MidFD管理ゴミ箱を空にできませんでした: {ex.Message}");
        }
    }
    private void UpdateMenuStripState()
    {
        var snapshot = BuildCommandUiSnapshot();
        Dictionary<ToolStripItem, bool> states = _commandStateCoordinator.BuildMenuItemStates(
            snapshot,
            _browserOnlyMenuItems,
            _busyAwareMenuItems,
            _menuItemRules);
        foreach (KeyValuePair<ToolStripItem, bool> pair in states)
        {
            pair.Key.Enabled = pair.Value;
        }
        if (_reloadCurrentDirectoryMenuItem != null)
        {
            _reloadCurrentDirectoryMenuItem.Enabled = _uiMode == UIMode.Browser && !IsCurrentDirectoryBusy();
        }
    }
    private void ClearBrowserTabContextState()
    {
        _browserTabViewState.ContextTabIndex = -1;
    }
    private void ClearBrowserTabCategoryContextState()
    {
        _categoryViewState.ContextCategoryId = null;
        _browserTabCategoryContextKind = BrowserTabStripCategoryItemKind.Category;
    }
    private void DismissTransientContextMenus()
    {
        _browserItemContextMenu?.Close();
        _browserBlankContextMenu?.Close();
        _browserTabContextMenu?.Close();
        _browserTabCategoryContextMenu?.Close();
        ClearBrowserTabContextState();
        ClearBrowserTabCategoryContextState();
    }
    private void ExecuteCreateDirectory()
    {
        if (GuardMutationBusy("フォルダ作成")) return;
        if (GuardReadOnlyBrowserTab("フォルダ作成"))
        {
            return;
        }
        string newDir = SimpleInputDialog.Show("作成するフォルダ名を入力してください:", "フォルダ作成 (K)");
        if (string.IsNullOrWhiteSpace(newDir))
        {
            return;
        }
        try
        {
            string target = Path.Combine(_navigationService.CurrentPath, newDir);
            FileOperationService.CreateDirectoryForUserMutation(target);
            LoadDirectory(_navigationService.CurrentPath, GetCreatedItemFocusTarget(newDir));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void ExecuteCreateFile()
    {
        if (GuardReadOnlyBrowserTab("ファイル作成"))
        {
            return;
        }
        string newFile = SimpleInputDialog.Show("作成するファイル名を入力してください:", "新規ファイル作成 (N)");
        if (string.IsNullOrWhiteSpace(newFile))
        {
            return;
        }
        try
        {
            string target = Path.Combine(_navigationService.CurrentPath, newFile);
            if (!File.Exists(target))
            {
                File.Create(target).Dispose();
                LoadDirectory(_navigationService.CurrentPath, GetCreatedItemFocusTarget(newFile));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void ExecuteDriveRoot()
    {
        string? lockRootPath = GetActiveBrowserTabLockRootPath();
        if (!string.IsNullOrWhiteSpace(lockRootPath))
        {
            if (!Directory.Exists(lockRootPath))
            {
                // 再正規化を試みる (ドライブルートの末尾セパレータ欠落などの補正)
                string normalized = _navigationService.NormalizeDestinationDirectory(lockRootPath);
                if (Directory.Exists(normalized))
                {
                    lockRootPath = normalized;
                    // 可能なら状態も更新しておく
                    var state = GetActiveBrowserTab();
                    if (state != null) state.StartupPath = normalized;
                }
                else
                {
                    ShowStatusMessage("固定タブのルートが見つかりません。通常のルートへ移動します。");
                    lockRootPath = null; // フォールバックへ
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(lockRootPath))
        {
            if (QuickAccessService.PathsEqual(_navigationService.CurrentPath, lockRootPath))
            {
                return;
            }
            _previewPopup.Clear();
            _currentPreviewTarget = null;
            LoadDirectory(lockRootPath);
            return;
        }
        string rootPath = Path.GetPathRoot(_navigationService.CurrentPath) ?? "";
        if (string.IsNullOrEmpty(rootPath) || _navigationService.CurrentPath == rootPath)
        {
            return;
        }
        _previewPopup.Clear();
        _currentPreviewTarget = null;
        if (!PrepareUnlockedTabForLocationChange(rootPath))
        {
            return;
        }
        LoadDirectory(rootPath);
    }
    private CommandStateCoordinator.CommandUiSnapshot BuildCommandUiSnapshot()
    {
        bool isBrowserMode = _uiMode == UIMode.Browser;
        ListViewItem? currentItem = isBrowserMode ? GetCurrentBrowserItem() : null;
        string? currentPath = currentItem?.Tag as string;
        int selectionCount = isBrowserMode ? GetLightweightSelectionCount(currentItem) : 0;

        CommandStateCoordinator.BrowserSelectionKind selectionKind = CommandStateCoordinator.BrowserSelectionKind.None;
        if (isBrowserMode && currentItem != null)
        {
            if (currentItem.Text == "..")
            {
                selectionKind = CommandStateCoordinator.BrowserSelectionKind.ParentDirectory;
            }
            else if (IsBrowserFileItem(currentItem))
            {
                selectionKind = CommandStateCoordinator.BrowserSelectionKind.File;
                if (!string.IsNullOrEmpty(currentPath))
                {
                    string ext = Path.GetExtension(currentPath);
                    if (ArchiveFileTypeHelper.IsArchive(currentPath) ||
                        string.Equals(ext, ".lha", StringComparison.OrdinalIgnoreCase))
                    {
                        selectionKind = CommandStateCoordinator.BrowserSelectionKind.ArchiveCandidate;
                    }
                }
            }
            else
            {
                selectionKind = CommandStateCoordinator.BrowserSelectionKind.Directory;
            }
        }

        _cachedCommandUiSnapshot = _commandStateCoordinator.CreateCommandUiSnapshot(
            isBrowserMode,
            _isClipboardBusy,
            selectionCount,
            HasTwoFileSelectionForCommandState(selectionCount),
            currentItem?.Text,
            currentPath,
            selectionKind);
        return _cachedCommandUiSnapshot;
    }
    private int GetLightweightSelectionCount(ListViewItem? currentItem)
    {
        if (_markedFiles.Count > 0)
        {
            return _markedFiles.Count;
        }
        return currentItem != null
            && currentItem.Text != ".."
            && currentItem.Tag is string path
            && !string.IsNullOrWhiteSpace(path)
            ? 1
            : 0;
    }
    private bool HasTwoFileSelectionForCommandState(int selectionCount)
    {
        if (selectionCount != 2 || _markedFiles.Count != 2)
        {
            return false;
        }
        int checkedCount = 0;
        foreach (string path in _markedFiles)
        {
            checkedCount++;
            if (checkedCount > 2 || !File.Exists(path))
            {
                return false;
            }
        }
        return checkedCount == 2;
    }
    private CommandStateCoordinator.CommandHintState BuildCommandHintState()
    {
        return _commandStateCoordinator.CreateCommandHintState(
            _uiMode == UIMode.Browser,
            Visible,
            Enabled,
            browserPanel.Visible,
            IsMenuStripAltNavigationActive(),
            Focused || ContainsFocus);
    }
    private string CurrentFunctionKeyProfileValue =>
        _settings.Input?.FunctionKeyProfile ?? InputSettings.StandardProfileValue;
    private string GetFunctionAwareShortcutHint(FunctionKeyAction action, string primaryShortcut, string? fdShortcut = null)
    {
        int? fKey = action != FunctionKeyAction.None
            ? FunctionKeyProfileService.ResolveKeyNumber(CurrentFunctionKeyProfileValue, action)
            : null;
        string currentShortcut = primaryShortcut;

        if (fKey.HasValue)
        {
            string functionKeyShortcut = $"F{fKey.Value}";
            if (string.IsNullOrWhiteSpace(currentShortcut))
            {
                currentShortcut = functionKeyShortcut;
            }
            else
            {
                currentShortcut = $"{currentShortcut} / {functionKeyShortcut}";
            }
        }

        if (FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) == FunctionKeyProfile.FDCompatible
            && !string.IsNullOrEmpty(fdShortcut))
        {
            if (string.IsNullOrWhiteSpace(currentShortcut))
            {
                currentShortcut = fdShortcut;
            }
            else
            {
                currentShortcut = $"{currentShortcut} / {fdShortcut}";
            }
        }

        return currentShortcut;
    }
    private bool IsFunctionKeyAssignedToAction(int fKey, FunctionKeyAction expectedAction)
    {
        return FunctionKeyProfileService.ResolveAction(CurrentFunctionKeyProfileValue, fKey) == expectedAction;
    }
    private string BuildMenuKeyHintMessage()
    {
        return
            "メニューバーは補助導線です。\n主な操作は引き続き FunctionBar とキーボードから実行できます。\n\n例:\n" +
            $"Z: 関連付け実行 / Explorer\nE: 外部エディタで開く\n" +
            $"Enter / V: 内蔵Viewer / 画像Viewer\n" +
            "H: PowerShell (現在ディレクトリ) / Shift+H: コマンドプロンプト / X: Execダイアログ\n" +
            $"ダブルクリック: 既定動作で開く\n{GetFunctionAwareShortcutHint(FunctionKeyAction.Copy, "C")}: コピー\nM: 移動\n" +
            $"{GetFunctionAwareShortcutHint(FunctionKeyAction.Rename, "R")}: 名前変更\nQ: QuickAccess（移動ハブ: 登録先 / 別名 / 最近 / 履歴）\n" +
            "Ctrl+M: マーク一覧 / スロット\nCtrl+T: 新しいタブ\nCtrl+L / Pathクリック: パス入力\nタブダブルクリック / タブ右クリック: 現在のタブ固定を切替\n" +
            "Ctrl+Right / Ctrl+Tab: 次のタブ\nCtrl+Left / Ctrl+Shift+Tab: 前のタブ\nCtrl+W: タブを閉じる（固定タブは閉じない）\n" +
            "Alt: Browser の直起動一覧\nAlt+英数字: 外部ツール namespace の直起動\nAlt+F1〜F12: Function layer\n" +
            "O: 設定";
    }
    private void ShowMenuKeyHint()
    {
        MessageBox.Show(
            BuildMenuKeyHintMessage(),
            "キー操作ヒント",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
    private void ShowVersionInfo()
    {
        MessageBox.Show(
            $"MidFD\nMenuStrip を補助導線として導入したビルドです。\n\nVersion: {Application.ProductVersion}",
            "バージョン情報",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
    private void SwitchUIMode(UIMode mode)
    {
        if (mode != UIMode.Browser)
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
        }
        HideCommandHintOverlay();
        if (mode == UIMode.Browser)
        {
            _previewRequestCoordinator.Cancel(); // プレビュー読み込み中なら中断
        }
        _uiMode = mode;
        var lifecyclePlan = _viewerPreviewCoordinator.CreateViewerModeLifecyclePlan(
            mode == UIMode.Browser,
            _currentViewerKind,
            GetCurrentSelectionPreviewKind());
        _currentViewerKind = lifecyclePlan.NextViewerKind;
        ApplyViewerChromeState();
        UpdateFunctionBar(); // FunctionBar の表示更新
        UpdateMenuStripState();
        if (mode == UIMode.Browser)
        {
            viewerPanel.Visible = false;
            browserPanel.Visible = true;
            browserPanel.BringToFront(); // Z順を確実にする
            browserPanel.Focus();
            EnsureStatusBarVisible();
            if (_notificationService != null)
            {
                NormalizeStatusLabelLayout();
                RefreshBrowserStatusSummary();
                NormalizeStatusLabelLayout();
            }
            else
            {
                statusLabel.Text = "Ready.";
            }
        }
        else
        {
            // Viewer モード
            browserPanel.Visible = false; // 明示的に一覧を隠す
            fileListView.Visible = false;
            viewerPanel.Visible = true;
            viewerPanel.BringToFront(); // Viewerを最前面へ
            viewerPanel.Focus();
            EnsureStatusBarVisible();
            ApplyViewerStatusLine();
            LogViewerLayoutBounds("SwitchUIMode Viewer");
            // Phase 3-viewer-fix1: 閲覧開始時に同期的にクリアして残像を防ぐ
            if (lifecyclePlan.ShouldClearPreview)
            {
                ClearPreview(lifecyclePlan.ClearMessage);
            }
            // 閲覧開始時に最新の選択アイテムで更新をかける
            if (lifecyclePlan.ShouldRefreshPreview)
            {
                RequestPreviewRefresh(force: true);
            }
        }
    }
    private bool IsCurrentDirectoryBusy()
    {
        return _isClipboardBusy ||
            _fileOpUiState.Cts != null ||
            !string.IsNullOrWhiteSpace(_fileOpUiState.ActiveOperationName) ||
            _shellDeleteProgressFallback != null ||
            _isFileOperationUndoRedoBusy ||
            _undoRedoProgressFallback != null;
    }
    private bool IsCurrentDirectoryRefreshBlocked()
    {
        return _uiMode != UIMode.Browser || IsCurrentDirectoryBusy();
    }
    private void ApplyFeatureProfile(bool isMouseGestureExplicit)
    {
        _featureProfile = FeatureProfileService.ResolveRuntimeProfile(_startupProfileOverride, _settings.Profile, FeatureProfile.PracticalStable);
        FeatureProfileService.ApplyRuntimeProfile(_settings, _featureProfile, isMouseGestureExplicit);
        _featureGate = new FeatureGateService(_featureProfile);
    }
    private bool GuardFeatureDisabled(FeatureId featureId, string disabledMessage)
    {
        if (_featureGate.IsEnabled(featureId))
        {
            return false;
        }
        ShowStatusMessage(disabledMessage);
        return true;
    }
    private static string NormalizeDirectoryWatchPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : NavigationService.NormalizeDirectoryForCompare(path);
    }
    private bool GuardClipboardBusy(string? message = null)
    {
        if (_isClipboardBusy)
        {
            ShowStatusMessage(message ?? FileOperationPresentationHelper.GetBusyBlockedMessage(
                _fileOpUiState.ActiveOperationName,
                canCancel: _fileOpUiState.Cts != null,
                isCancelRequested: _fileOpUiState.Cts?.IsCancellationRequested ?? false));
            return true;
        }
        return false;
    }
    private bool GuardMutationBusy(string? message = null)
    {
        if (GuardClipboardBusy(message)) return true;
        return false;
    }
    private void HandleSettingsSaveFailed(SettingsSqliteStore.SettingsSaveResult result)
    {
        void ShowFailure() => ShowStatusMessage(result.UserMessage);
        if (IsHandleCreated && InvokeRequired) BeginInvoke((Action)ShowFailure);
        else ShowFailure();
    }
    private void ShowSettingsRecoveryNoticeIfNeeded()
    {
        SettingsRecoveryState? recovery = SettingsManager.CurrentRecoveryState;
        SettingsRecoveryNoticeAction action = _settingsRecoveryNoticeScheduler.Evaluate(recovery != null, IsHandleCreated, IsDisposed || Disposing);
        if (action == SettingsRecoveryNoticeAction.ScheduleShown)
        {
            Shown += HandleDeferredSettingsRecoveryNotice;
            return;
        }
        if (action == SettingsRecoveryNoticeAction.Show) ShowSettingsRecoveryNotice(recovery!);
    }

    private void HandleDeferredSettingsRecoveryNotice(object? sender, EventArgs e)
    {
        Shown -= HandleDeferredSettingsRecoveryNotice;
        ShowSettingsRecoveryNoticeIfNeeded();
    }

    private void ShowSettingsRecoveryNotice(SettingsRecoveryState recovery)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        ShowStatusMessage(recovery.UserMessage);
        BeginInvoke((Action)(() =>
        {
            if (!IsDisposed && !Disposing && IsHandleCreated) MessageBox.Show(this, recovery.UserMessage, "設定の復旧", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }));
    }
    private bool RequestActiveFileOperationCancel(string source)
    {
        bool requestedBefore = _fileOpUiState.Cts?.IsCancellationRequested ?? false;
        LogService.Info(
            $"[CancelRuntime] Request received. source={source}, thread={Environment.CurrentManagedThreadId}, " +
            $"busy={_isClipboardBusy}, hasCts={_fileOpUiState.Cts != null}, alreadyRequested={requestedBefore}, " +
            $"operation={_fileOpUiState.ActiveOperationName ?? "<unknown>"}, statusVersion={_fileOpUiState.StatusVersion}, " +
            $"progressForm={_shellDeleteProgressFallback != null}");
        if (_fileOpUiState.Cts == null)
        {
            LogService.Warn($"[CancelRuntime] Request ignored because CTS is null. source={source}");
            return false;
        }
        try
        {
            LogService.Info(
                $"[CancelRuntime] MarkCancelRequested before. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                $"requested={_fileOpUiState.Cts.IsCancellationRequested}, progressForm={_shellDeleteProgressFallback != null}, progressDialog={_fileOperationProgressDialog != null}");
            _shellDeleteProgressFallback?.MarkCancelRequested();
            _fileOperationProgressDialog?.MarkCancelRequested();
            LogService.Info(
                $"[CancelRuntime] MarkCancelRequested after. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                $"requested={_fileOpUiState.Cts.IsCancellationRequested}, progressForm={_shellDeleteProgressFallback != null}, progressDialog={_fileOperationProgressDialog != null}");
            if (!_fileOpUiState.Cts.IsCancellationRequested)
            {
                LogService.Warn(
                    $"[CancelRuntime] CTS cancel before. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                    $"requested={_fileOpUiState.Cts.IsCancellationRequested}, operation={_fileOpUiState.ActiveOperationName ?? "<unknown>"}");
                _fileOpUiState.CancelRequestedTimestamp = Stopwatch.GetTimestamp();
                _fileOpUiState.Cts.Cancel();
                LogService.Warn(
                    $"[CancelRuntime] CTS cancel after. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                    $"requested={_fileOpUiState.Cts.IsCancellationRequested}, operation={_fileOpUiState.ActiveOperationName ?? "<unknown>"}");
                LogService.Info($"[FileOperationCancel] Cancel requested. source={source}, operation={_fileOpUiState.ActiveOperationName ?? "<unknown>"}, statusVersion={_fileOpUiState.StatusVersion}");
                ShowStatusMessage(FileOperationPresentationHelper.GetCancelRequestedMessage(_fileOpUiState.ActiveOperationName ?? "ファイル操作"));
            }
            else
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                    _fileOpUiState.ActiveOperationName,
                    canCancel: true,
                    isCancelRequested: true));
            }
            LogService.Info(
                $"[CancelRuntime] Request completed. source={source}, requested={_fileOpUiState.Cts.IsCancellationRequested}, " +
                $"thread={Environment.CurrentManagedThreadId}");
        }
        catch (Exception ex)
        {
            LogService.Error($"[CancelRuntime] Request failed. source={source}", ex);
            throw;
        }
        return true;
    }
    private bool HasActiveFileOperationCancelContext()
    {
        return _isClipboardBusy ||
            _fileOpUiState.Cts != null ||
            !string.IsNullOrWhiteSpace(_fileOpUiState.ActiveOperationName) ||
            _shellDeleteProgressFallback != null ||
            _fileOperationProgressDialog != null ||
            _isFileOperationUndoRedoBusy ||
            _undoRedoProgressFallback != null;
    }
    private bool TryRouteActiveFileOperationCancel(string source)
    {
        bool hasActiveContext = HasActiveFileOperationCancelContext();
        LogService.Info(
            $"[CancelRuntime] Active operation cancel route check. source={source}, activeContext={hasActiveContext}, " +
            $"busy={_isClipboardBusy}, hasCts={_fileOpUiState.Cts != null}, activeOperation={_fileOpUiState.ActiveOperationName ?? "<none>"}, " +
            $"shellProgress={_shellDeleteProgressFallback != null}, undoRedoProgress={_undoRedoProgressFallback != null}, " +
            $"thread={Environment.CurrentManagedThreadId}");
        if (!hasActiveContext)
        {
            return false;
        }
        if (_fileOpUiState.Cts != null)
        {
            RequestActiveFileOperationCancel(source);
        }
        else
        {
            ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                _fileOpUiState.ActiveOperationName,
                canCancel: false,
                isCancelRequested: false));
        }
        LogService.Info($"[CancelRuntime] Input consumed by active file operation cancel route. source={source}");
        return true;
    }
    /// <summary>
    /// Phase 3-input-alias1: ファンクションキー (F2-F12) のルーティングを一元管理する。
    /// UIMode 判定と GuardClipboardBusy を内部で自動処理する。
    /// </summary>
    private bool ExecuteFunctionKey(int fKey, bool forceShiftLayer = false, Keys forcedModifierLayer = Keys.None)
    {
        if (_uiMode != UIMode.Browser) return false;

        bool isCompatible = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) == FunctionKeyProfile.FDCompatible;
        var profile = isCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard;

        // F1〜F12のコマンドスロットの解決
        bool isAltLayer = forcedModifierLayer == Keys.Alt || (forcedModifierLayer == Keys.None && (ModifierKeys & Keys.Alt) != 0);
        bool isCtrlLayer = forcedModifierLayer == Keys.Control || (forcedModifierLayer == Keys.None && !isAltLayer && (ModifierKeys & Keys.Control) != 0);
        bool isShiftLayer = forceShiftLayer || forcedModifierLayer == Keys.Shift || (forcedModifierLayer == Keys.None && !isAltLayer && !isCtrlLayer && (_isFunctionBarShiftLayerActive || (ModifierKeys & Keys.Shift) != 0));
        string? customCmdId = FunctionKeyProfileService.ResolveFunctionBarCommandId(
            profile,
            fKey,
            _settings.Input.FunctionBarCommandOverridesStandard,
            _settings.Input.FunctionBarCommandOverridesFdCompatible,
            _settings.Input.FunctionBarCommandOverridesShiftStandard,
            _settings.Input.FunctionBarCommandOverridesShiftFdCompatible,
            isShiftLayer,
            _settings.Input.FunctionBarCommandOverridesCtrlStandard,
            _settings.Input.FunctionBarCommandOverridesCtrlFdCompatible,
            _settings.Input.FunctionBarCommandOverridesAltStandard,
            _settings.Input.FunctionBarCommandOverridesAltFdCompatible,
            isCtrlLayer,
            isAltLayer);

        if (!string.IsNullOrEmpty(customCmdId))
        {
            if (GuardClipboardBusy()) return true;

            var snapshot = _cachedCommandUiSnapshot;

            // WinFD互換での通常F4 (file.delete) の特別扱いを CommandID レベルでも保護
            if (isCompatible && fKey == 4 && customCmdId == CommandIds.FileDelete)
            {
                if (snapshot.SelectionKind == CommandStateCoordinator.BrowserSelectionKind.Directory ||
                    snapshot.SelectionKind == CommandStateCoordinator.BrowserSelectionKind.File ||
                    snapshot.SelectionKind == CommandStateCoordinator.BrowserSelectionKind.ArchiveCandidate)
                {
                    _ = ExecuteDelete(permanent: false);
                }
                return true;
            }

            var cmdDef = _commandRegistry.Find(customCmdId);
            CommandScope scope = cmdDef?.Scope ?? CommandScope.Browser;

            _ = ExecuteCommandFromUi(customCmdId, scope, "FunctionKey");
            return true;
        }

        if (!isCompatible && (isCtrlLayer || isAltLayer))
        {
            return false;
        }

        if (isCompatible)
        {
            // Shift キーがアクティブ、またはキーボードで Shift が押されている場合
            if (isShiftLayer)
            {
                Keys dummyKey = Keys.Shift | (Keys.F1 + (fKey - 1));
                return TryHandleFdCompatibleShortcutAliases(dummyKey);
            }
            if (isCtrlLayer)
            {
                Keys dummyKey = Keys.Control | (Keys.F1 + (fKey - 1));
                return TryHandleFdCompatibleShortcutAliases(dummyKey);
            }
            if (isAltLayer)
            {
                Keys dummyKey = Keys.Alt | (Keys.F1 + (fKey - 1));
                return TryHandleFdCompatibleShortcutAliases(dummyKey);
            }

            // 通常（Shiftなし）の F4 を Delet (削除) として特別扱いする
            if (fKey == 4)
            {
                if (GuardMutationBusy()) return true;

                // ガード条件の確認
                var snapshot = _cachedCommandUiSnapshot;
                if (snapshot.SelectionKind == CommandStateCoordinator.BrowserSelectionKind.Directory ||
                    snapshot.SelectionKind == CommandStateCoordinator.BrowserSelectionKind.File ||
                    snapshot.SelectionKind == CommandStateCoordinator.BrowserSelectionKind.ArchiveCandidate)
                {
                    _ = ExecuteDelete(permanent: false);
                }
                return true;
            }
        }

        FunctionKeyAction action = FunctionKeyProfileService.ResolveAction(CurrentFunctionKeyProfileValue, fKey);
        return ExecuteFunctionKeyAction(action);
    }
    private bool ExecuteFunctionKeyAction(FunctionKeyAction action)
    {
        switch (action)
        {
            case FunctionKeyAction.Help:
                ShowMenuKeyHint();
                return true;
            case FunctionKeyAction.Execute:
                ExecuteCurrentFile();
                return true;
            case FunctionKeyAction.Copy:
                _ = ExecuteCopy();
                return true;
            case FunctionKeyAction.Edit:
                ExecuteOpenWithEditor();
                return true;
            case FunctionKeyAction.Rename:
                ExecuteRename();
                return true;
            case FunctionKeyAction.Reload:
                return ExecuteCurrentDirectoryReloadCommand();
            case FunctionKeyAction.Sort:
                ExecuteSort();
                return true;
            case FunctionKeyAction.Filter:
                ExecuteFilter();
                return true;
            case FunctionKeyAction.Tree:
                ExecuteTreeDialog();
                return true;
            case FunctionKeyAction.Logdisk:
                ExecuteLogdisk();
                return true;
            case FunctionKeyAction.Unpack:
                _ = ExecuteUnpack();
                return true;
            case FunctionKeyAction.Top:
                MoveBrowserCursorToTop();
                return true;
            case FunctionKeyAction.Bottom:
                MoveBrowserCursorToBottom();
                return true;
            case FunctionKeyAction.Menu:
                OpenMenuStripFromKeyboard();
                return true;
            default:
                return false;
        }
    }
    private void MoveBrowserCursorToTop()
    {
        if (fileListView.Items.Count <= 0)
        {
            return;
        }
        SetBrowserGlobalCursorIndex(0);
    }
    private void MoveBrowserCursorToBottom()
    {
        if (fileListView.Items.Count <= 0)
        {
            return;
        }
        SetBrowserGlobalCursorIndex(Math.Max(0, _browserTotalItemCount - 1));
    }
    /// <summary>
    /// Phase 3-input-viewer1: Viewer モード専用の KeyDown 処理を helper 化。
    /// 処理を行った（早期 return すべき）場合は true を返す。
    /// </summary>
    /// <summary>
    /// Phase 3-input-viewer1: Viewer モード専用の ProcessCmdKey 操作を helper 化。
    /// </summary>
    /// <summary>
    /// Phase 3-input-browser1: Browser モード専用の KeyDown 処理を helper 化。
    /// </summary>
    private bool TryHandleBrowserKeyDown(KeyEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return false;
        // WinFDライクな操作: ESC は段階的な「閉じる」
        if (e.KeyCode == Keys.Escape)
        {
            if (TryRouteActiveFileOperationCancel("BrowserKeyDown"))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            if (_previewPopupVisible)
            {
                // プレビュー表示中ならプレビューを閉じる
                TogglePreviewPopup();
            }
            else if (_markedFiles.Count > 0)
            {
                var beforeSnapshot = _markedFiles.Snapshot();
                int clearedCount = beforeSnapshot.Count;
                int outsideCount = CountMarksOutsideCurrentDirectory();
                BeginPendingEscExitMarkPersistence(beforeSnapshot);
                ClearMarks(invalidateRedo: false, preservePendingEscExitState: true);
                RefreshMarkUi();
                string outsideInfo = outsideCount > 0 ? $" (現在ディレクトリ外 {outsideCount} 件を含む)" : "";
                ShowStatusMessage($"{clearedCount} 件のマークを解除しました{outsideInfo}");
            }
            else
            {
                // プレビュー非表示中のみ終了確認 (ESCでキャンセル可能にする)
                _isExitConfirmationPending = true;
                _directoryRefreshDebounceTimer.Stop();
                LogService.Warn(
                    $"[CancelProvenance] MidFD browser ESC exit confirm shown. " +
                    $"activeContext={HasActiveFileOperationCancelContext()}, busy={_isClipboardBusy}, " +
                    $"hasCts={_fileOpUiState.Cts != null}, requested={_fileOpUiState.Cts?.IsCancellationRequested ?? false}, " +
                    $"activeOperation={_fileOpUiState.ActiveOperationName ?? "<none>"}, shellProgress={_shellDeleteProgressFallback != null}, " +
                    $"undoRedoProgress={_undoRedoProgressFallback != null}, previewVisible={_previewPopupVisible}, " +
                    $"markedCount={_markedFiles.Count}, thread={Environment.CurrentManagedThreadId}");
                var result = MessageBox.Show("終了しますか？", "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                LogService.Warn(
                    $"[CancelProvenance] MidFD browser ESC exit confirm result. result={result}, " +
                    $"activeContext={HasActiveFileOperationCancelContext()}, busy={_isClipboardBusy}, " +
                    $"hasCts={_fileOpUiState.Cts != null}, requested={_fileOpUiState.Cts?.IsCancellationRequested ?? false}, " +
                    $"activeOperation={_fileOpUiState.ActiveOperationName ?? "<none>"}, thread={Environment.CurrentManagedThreadId}");
                if (result == DialogResult.Yes)
                {
                    _isClosingFromEscExitPath = true;
                    this.Close();
                }
                else
                {
                    _isExitConfirmationPending = false;
                    ClearPendingEscExitMarkPersistence();
                }
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        ClearPendingEscExitMarkPersistence();
        // ナびゲーションキーや修飾キー単独押しはスルー (警告しない)
        if (IsNavigationOrModifierKey(e.KeyCode))
        {
            return true; // Browser 用 KeyDown 処理の続きへ流さない
        }
        // マーク操作 (Space / Insert)
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Insert)
        {
            ToggleMark(moveNext: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.Enter)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecuteEnter();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.Back)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            e.Handled = true;
            e.SuppressKeyPress = true;
            _previewPopup.Clear();
            _currentPreviewTarget = null;
            ExecuteBackspace();
            return true;
        }
        if (e.Alt || e.Control)
        {
            return false;
        }
        // Shift+D / Shift+Delete は例外的にここで処理するため、それ以外のShiftキー付き入力は弾く
        if (e.Shift && e.KeyCode != Keys.D && e.KeyCode != Keys.Delete)
        {
            return false;
        }
        if (TryHandleBrowserCmdKeyCustomBindings(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // 単キーコマンド群
        if (e.KeyCode == Keys.R && !e.Shift)
        {
            ExecuteRename();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.D || e.KeyCode == Keys.Delete)
        {
            _ = ExecuteDelete(e.Shift);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.C)
        {
            _ = ExecuteCopy();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.M)
        {
            _ = ExecuteMove();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.P)
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            _ = ExecutePack();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.U)
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            _ = ExecuteUnpack();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // E キーで外部エディタ起動を復活。F4+Edit profile の場合も同様。
        if ((e.KeyCode == Keys.E && !e.Shift && !e.Alt && !e.Control) || (e.KeyCode == Keys.F4 && !e.Shift && !e.Alt && !e.Control && IsFunctionKeyAssignedToAction(4, FunctionKeyAction.Edit)))
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            ExecuteOpenWithEditor();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.V)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecutePreviewLaunch();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.O)
        {
            OpenSettingsForm();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.F)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecuteFilter();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.K)
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            ExecuteCreateDirectory();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.L)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecuteLogdisk();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.Q)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecuteQuickAccess();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.N)
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            ExecuteCreateFile();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.S)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecuteSort();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.T)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecuteTreeDialog();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.H && e.Modifiers == Keys.None)
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            OpenTerminalInCurrentDirectory(ShellKind.PowerShell);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.H && e.Modifiers == Keys.Shift)
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            OpenTerminalInCurrentDirectory(ShellKind.CommandPrompt);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.X)
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            ExecuteShellDialog();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.A)
        {
            if (GuardMutationBusy()) { e.Handled = true; return true; }
            ExecuteAttribute();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // \ ルート復帰 (Oem5 または OemBackslash)
        if (e.KeyCode == Keys.Oem5 || e.KeyCode == Keys.OemBackslash || e.KeyCode == (Keys)220 || e.KeyCode == (Keys)226)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecuteDriveRoot();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // 処理されなかったコマンド候補キーは未対応として表示
        ShowStatusMessage($"未対応キーです: {e.KeyCode}");
        e.Handled = true;
        return true;
    }
    /// <summary>
    /// Phase 3-input-cmdkey-mark1: ProcessCmdKey における Browser 文脈の一括マーク操作を helper 化。
    /// </summary>
    private void ToggleBulkMarks(bool includeDirectories)
    {
        var targets = CollectBulkMarkTargetPaths(includeDirectories);
        if (targets.Count == 0)
        {
            return;
        }
        bool allMarked = targets.All(_markedFiles.Contains);
        if (allMarked)
        {
            UnmarkBulkTargets(targets, includeDirectories ? "UnmarkAllItems" : "UnmarkAllFiles");
        }
        else
        {
            MarkBulkTargets(targets, includeDirectories ? "MarkAllItems" : "MarkAllFiles");
        }
    }
    private void MarkBulk(bool includeDirectories)
    {
        var targets = CollectBulkMarkTargetPaths(includeDirectories);
        if (targets.Count == 0)
        {
            return;
        }
        MarkBulkTargets(targets, includeDirectories ? "MarkAllItems" : "MarkAllFiles");
    }
    private void InvertBulkMarks(bool includeDirectories)
    {
        var targets = CollectBulkMarkTargetPaths(includeDirectories);
        if (targets.Count == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        var nextMarks = _markedFiles
            .Where(path => !targetSet.Contains(path))
            .ToList();
        var nextSet = new HashSet<string>(nextMarks, StringComparer.OrdinalIgnoreCase);
        foreach (string path in targets)
        {
            if (!_markedFiles.Contains(path) && nextSet.Add(path))
            {
                nextMarks.Add(path);
            }
        }
        ApplyBulkMarkState(nextMarks, includeDirectories ? "InvertAllItems" : "InvertAllFiles", targets.Count, stopwatch.ElapsedMilliseconds, stopwatch);
    }
    private void MarkBulkTargets(IReadOnlyList<string> targets, string operationName)
    {
        var stopwatch = Stopwatch.StartNew();
        var nextMarks = _markedFiles.Snapshot().ToList();
        var nextSet = new HashSet<string>(nextMarks, StringComparer.OrdinalIgnoreCase);
        foreach (string path in targets)
        {
            if (nextSet.Add(path))
            {
                nextMarks.Add(path);
            }
        }
        ApplyBulkMarkState(nextMarks, operationName, targets.Count, stopwatch.ElapsedMilliseconds, stopwatch);
    }
    private void UnmarkBulkTargets(IReadOnlyList<string> targets, string operationName)
    {
        var stopwatch = Stopwatch.StartNew();
        var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        var nextMarks = _markedFiles
            .Where(path => !targetSet.Contains(path))
            .ToList();
        ApplyBulkMarkState(nextMarks, operationName, targets.Count, stopwatch.ElapsedMilliseconds, stopwatch);
    }
    private IReadOnlyList<string> CollectBulkMarkTargetPaths(bool includeDirectories)
    {
        if (!_browserLoadCoordinator.TryGetCurrentSnapshotTargetPaths(
                _navigationService.CurrentPath,
                includeDirectories,
                out IReadOnlyList<string> paths))
        {
            ShowStatusMessage("現在の一覧情報を取得できないため、一括Markを実行できません。再読込してください。");
            return Array.Empty<string>();
        }
        return paths;
    }
    private static bool IsBrowserFileItem(ListViewItem item)
    {
        return item.SubItems.Count > 2 && !string.IsNullOrEmpty(item.SubItems[2].Text);
    }
    private void ApplyBulkMarkState(
        IReadOnlyList<string> nextMarks,
        string operationName,
        int targetCount,
        long buildNextMarksMs,
        Stopwatch totalStopwatch)
    {
        var restoreStopwatch = Stopwatch.StartNew();
        RestoreMarks(nextMarks);
        bool deferSizeResolution = NetworkPathResolutionPolicy.IsAuxiliaryResolutionDeferred(_navigationService.CurrentPath)
            || _markedFiles.Any(NetworkPathResolutionPolicy.IsUncPath);
        MarkSummaryBulkEffectResult summaryEffects = _markSummaryBulkEffectCoordinator.Execute(
            _markedFiles.Count,
            deferSizeResolution,
            SetCountOnlyMarkSummaryCache,
            ScheduleMarkSummaryRebuild,
            CancelPendingMarkSummaryRebuild);
        restoreStopwatch.Stop();
        var repaintStopwatch = Stopwatch.StartNew();
        browserPanel.Invalidate();
        fileListView.Invalidate();
        repaintStopwatch.Stop();
        var infoStopwatch = Stopwatch.StartNew();
        UpdateInfoPanel();
        infoStopwatch.Stop();
        var menuStopwatch = Stopwatch.StartNew();
        UpdateMenuStripState();
        menuStopwatch.Stop();
        var intentStopwatch = Stopwatch.StartNew();
        InvalidateRecentMultiMarkIntent();
        intentStopwatch.Stop();
        totalStopwatch.Stop();
        LogService.Info(
            $"[MarkBulkPerf] {operationName} targets={targetCount} marks={_markedFiles.Count} " +
            $"buildNext={buildNextMarksMs}ms restore={restoreStopwatch.ElapsedMilliseconds}ms " +
            $"invalidate={repaintStopwatch.ElapsedMilliseconds}ms info={infoStopwatch.ElapsedMilliseconds}ms " +
            $"menu={menuStopwatch.ElapsedMilliseconds}ms intent={intentStopwatch.ElapsedMilliseconds}ms " +
            $"summaryCountOnly={summaryEffects.CountOnlyApplyCount} summarySchedule={summaryEffects.SummaryScheduleCount} summaryInvalidate={summaryEffects.PendingInvalidationCount} " +
            $"total={totalStopwatch.ElapsedMilliseconds}ms");
    }



    /// <summary>
    /// Phase 3-input-cmdkey-nav1: ProcessCmdKey における Browser 文脈のナビゲーション操作を helper 化。
    /// </summary>
    /// <summary>
    /// Phase 3-input-cmdkey-launch1: ProcessCmdKey における Browser 文脈のエピエイリアス系操作 (Fキー / Filter / 再読込) を helper 化。
    /// </summary>

    private bool TryHandleFdCompatibleShortcutAliases(Keys keyData)
    {
        if (FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) != FunctionKeyProfile.FDCompatible)
        {
            return false;
        }

        if (keyData == (Keys.Shift | Keys.F4))
        {
            // Shift+F4 は WinFD ではディレクトリ削除系だが、誤操作リスクが高いため今回非対象。
            return false;
        }

        if (keyData == (Keys.Shift | Keys.F1))
        {
            if (GuardMutationBusy()) return true;
            ExecuteAttribute();
            return true;
        }
        if (keyData == (Keys.Shift | Keys.F3))
        {
            if (GuardMutationBusy()) return true;
            _ = ExecuteMove();
            return true;
        }
        if (keyData == (Keys.Shift | Keys.F5))
        {
            if (GuardMutationBusy()) return true;
            ExecuteCreateDirectory();
            return true;
        }
        if (keyData == (Keys.Shift | Keys.F6))
        {
            if (GuardMutationBusy()) return true;
            OpenTerminalInCurrentDirectory(ShellKind.PowerShell);
            return true;
        }
        if (keyData == (Keys.Shift | Keys.F7))
        {
            return ExecuteCurrentDirectoryReloadCommand();
        }
        if (keyData == (Keys.Shift | Keys.F8) || keyData == (Keys.Shift | Keys.Enter))
        {
            if (GuardMutationBusy()) return true;
            ExecuteOpenWithEditor();
            return true;
        }
        if (keyData == (Keys.Shift | Keys.F9))
        {
            if (GuardClipboardBusy()) return true;
            ExecutePreviewLaunch();
            return true;
        }
        if (keyData == (Keys.Shift | Keys.F10))
        {
            if (GuardMutationBusy()) return true;
            _ = ExecutePack();
            return true;
        }
        if (keyData == (Keys.Alt | Keys.F5))
        {
            OpenSettingsForm();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.C))
        {
            if (GuardClipboardBusy()) return true;
            CopySelectedOrMarkedFullPathsToClipboard();
            return true;
        }

        return false;
    }

    private BrowserFileDisplayMode GetBrowserFileDisplayMode()
    {
        return _settings.Appearance.ResolveFileDisplayMode();
    }
    private void SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode mode)
    {
        HideBrowserFileNameToolTip();
        BrowserFileDisplayMode currentMode = GetBrowserFileDisplayMode();
        if (currentMode == mode)
        {
            return;
        }
        _settings.Appearance.FileDisplayMode = mode;
        _settings.Appearance.ShowFileSizeAndDateInBrowser = mode == BrowserFileDisplayMode.NameSizeDate;
        browserPanel.Invalidate();
        CaptureActiveBrowserTabState();
        UpdateFileDisplayModeMenuChecks();
        RematerializeBrowserPageIfCapacityChanged();
        ShowStatusMessage(mode switch
        {
            BrowserFileDisplayMode.NameSize => "表示モード: サイズ",
            BrowserFileDisplayMode.NameSizeDate => "表示モード: サイズ・更新日時",
            _ => "表示モード: ファイル名のみ"
        });
    }
    private void UpdateFileDisplayModeMenuChecks()
    {
        BrowserFileDisplayMode mode = GetBrowserFileDisplayMode();
        if (_fileDisplayModeNameOnlyMenuItem != null)
        {
            _fileDisplayModeNameOnlyMenuItem.Checked = mode == BrowserFileDisplayMode.NameOnly;
        }
        if (_fileDisplayModeNameSizeMenuItem != null)
        {
            _fileDisplayModeNameSizeMenuItem.Checked = mode == BrowserFileDisplayMode.NameSize;
        }
        if (_fileDisplayModeNameSizeDateMenuItem != null)
        {
            _fileDisplayModeNameSizeDateMenuItem.Checked = mode == BrowserFileDisplayMode.NameSizeDate;
        }
    }
    private void OpenFileListColorSettings()
    {
        OpenSettingsForm(SettingsForm.InitialTab.Color);
    }

    /// <summary>
    /// Phase 3-input-cmdkey-launch1: ProcessCmdKey における Browser 文脈の起動系操作 (外部アプリ / プロパティ) を helper 化。
    /// </summary>
    /// <summary>
    /// Phase 3-input-cmdkey-clipui1: ProcessCmdKey における Browser 文脈のクリップボード操作 (Ctrl+C/X/V) を helper 化。
    /// </summary>
    /// <summary>
    /// Phase 3-input-cmdkey-clipui1: ProcessCmdKey における Browser 文脈の列数設定 (1-9) を helper 化。
    /// </summary>
    /// <summary>
    /// Phase: Browser UpdateInfoPanel debounce corrective
    /// カーソル移動/選択変更に伴う補助表示更新を 150ms debounce して予約する。
    /// latest-wins: 新しい選択変更が来たら前回予約をキャンセルし、最後の選択だけ UpdateInfoPanel を実行する。
    /// 選択状態・操作対象は即時維持。フォーム破棄/終了時はタイマーを安全にキャンセルする。
    /// _updateInfoPanelFiredSeq: 今回の Tick で発火すべき seq を保持し、Tick 時に _updateInfoPanelDebounceSeq と比較する。
    /// </summary>
    private long _updateInfoPanelFiredSeq = 0;
    private void ScheduleUpdateInfoPanelDebounced()
    {
        const int DebounceMs = 150;
        long seq = System.Threading.Interlocked.Increment(ref _updateInfoPanelDebounceSeq);
        LogService.Detail($"[Browser.UpdateInfoPanelDebounce.Schedule] seq={seq} delayMs={DebounceMs}");
        if (_updateInfoPanelDebounceTimer == null)
        {
            _updateInfoPanelDebounceTimer = new System.Windows.Forms.Timer();
            _updateInfoPanelDebounceTimer.Tick += (_, _) =>
            {
                _updateInfoPanelDebounceTimer.Stop();
                long expected = System.Threading.Interlocked.Read(ref _updateInfoPanelFiredSeq);
                long current = System.Threading.Interlocked.Read(ref _updateInfoPanelDebounceSeq);
                if (expected != current)
                {
                    LogService.Detail($"[Browser.UpdateInfoPanelDebounce.Skip] seq={expected} currentSeq={current} canceled=true reason=Superseded");
                    return;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                LogService.Detail($"[Browser.UpdateInfoPanelDebounce.Fire] seq={expected}");
                UpdateInfoPanel();
                sw.Stop();
                LogService.Detail($"[Browser.UpdateInfoPanelDebounce.Fire] seq={expected} elapsedMs={sw.ElapsedMilliseconds} done=true");
            };
        }
        else
        {
            LogService.Detail($"[Browser.UpdateInfoPanelDebounce.Cancel] seq={seq} reason=NewSchedule");
            _updateInfoPanelDebounceTimer.Stop();
        }
        // 発火時に期待する seq を記録してからタイマー起動
        System.Threading.Interlocked.Exchange(ref _updateInfoPanelFiredSeq, seq);
        _updateInfoPanelDebounceTimer.Interval = DebounceMs;
        _updateInfoPanelDebounceTimer.Start();
    }
    /// <summary>
    /// WinFD風の上部情報欄（Info行・Name行）を更新する。
    /// カーソル位置のアイテム情報とマーク/ファイル数を表示する。
    /// </summary>
    private void UpdateInfoPanel()
    {
        LogFontRouteDiag("UpdateInfoPanel:START");
        string currentPath = _navigationService.CurrentPath;
        if (NetworkPathResolutionPolicy.TryGetUncRoot(currentPath, out string uncRoot))
        {
            _uncDriveInfoResolver.Schedule(uncRoot, _directoryNavigationGeneration, ApplyUncDriveInfo);
        }
        else
        {
            _uncDriveInfoResolver.CancelPending();
        }
        // 1. 表示項目の取得
        var currentItem = GetCurrentBrowserItem();
        int itemsPerPage = GetBrowserItemsPerPage(out _, out int rowsPerColumn);
        bool hasCachedDriveInfo = false;
        UncDriveInfoResolver.Result driveInfo = default;
        if (NetworkPathResolutionPolicy.TryGetUncRoot(currentPath, out string inputRoot))
        {
            hasCachedDriveInfo = _uncDriveInfoResolver.TryGetCached(inputRoot, out driveInfo);
        }
        // 2. 状態を InputState にまとめる
        var state = new HeaderPresentationHelper.InputState
        {
            CurrentPath = currentPath,
            CurrentPathKind = NetworkPathResolutionPolicy.GetPathKind(currentPath),
            CursorIndex = _browserCursorIndex,
            ItemCount = _browserTotalItemCount > 0 ? _browserTotalItemCount : fileListView.Items.Count,
            ItemsPerPage = itemsPerPage,
            RowsPerColumn = rowsPerColumn,
            ColumnCount = _columnCount,
            MarkedFiles = _markedFiles,
            CachedMarkSummary = GetMarkSummaryForHeader(),
            CachedMarkCount = _markSummaryCacheCount,
            CachedMarkSizeText = _markSummaryCacheSizeText,
            CachedMarkSummaryCompact = _markSummaryCacheCompact,
            HasCurrentMarkSummaryCache = _markSummaryCacheState != MarkSummaryCacheState.Invalid
                && _markSummaryCacheCount == _markedFiles.Count
                && string.Equals(
                    _markSummaryCachePath,
                    NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath),
                    StringComparison.OrdinalIgnoreCase),
            IsMarkSummaryPending = _markSummaryCacheState == MarkSummaryCacheState.CountOnly
                || _markSummaryRebuildCoordinator.HasPending,
            CurrentItemText = currentItem?.Text,
            CurrentItemPath = currentItem?.Tag as string,
            CurrentItemExtensionText = currentItem != null && currentItem.SubItems.Count > 1 ? currentItem.SubItems[1].Text : null,
            CurrentItemSizeText = currentItem != null && currentItem.SubItems.Count > 2 ? currentItem.SubItems[2].Text : null,
            CurrentItemDateText = currentItem != null && currentItem.SubItems.Count > 3 ? currentItem.SubItems[3].Text : null,
            CurrentItemAttrText = currentItem != null && currentItem.SubItems.Count > 4 ? currentItem.SubItems[4].Text : null,
            CurrentItemIsDirectory = currentItem != null && (currentItem.Text == ".." || (currentItem.SubItems.Count > 1 && string.Equals(currentItem.SubItems[1].Text, "<DIR>", StringComparison.OrdinalIgnoreCase))),
            SortKind = _currentSort,
            SortAscending = _sortAscending,
            FilterPattern = _filterPattern,
            FilterLockSummary = TabFilterLockService.BuildSummary(GetActiveTabFilterLock()),
            ShowExtensions = _settings.Appearance?.ShowExtensions ?? true,
            ShowDirectoryMarker = _settings.Appearance?.ShowDirectoryMarker ?? true,
            ShowItemIcons = _settings.Appearance?.ShowItemIcons ?? true,
            DateFormat = _settings.Appearance?.DateFormat ?? "yyyy-MM-dd HH:mm",
            SizeFormat = _settings.Appearance?.SizeFormat ?? "HumanReadable",
            HasCachedDriveInfo = hasCachedDriveInfo,
            CachedDriveUsed = driveInfo.Used,
            CachedDriveFree = driveInfo.Free
        };
        // 3. 表示文字列の生成をヘルパーに委譲
        var display = HeaderPresentationHelper.Build(state);
        // 4. UI への適用
        lblPage.Text = display.Page;
        lblTotal.Text = display.Total;
        // 【Path行右端】 (lblSort): Mark優先（省略禁止）、なければSort/Filter
        // Px1 header-right-clipping-corrective:
        //   FitMarkSummaryCompact によるMarkSize省略を廃止し、実測幅優先・省略禁止に変更。
        //   右側blockは実文字列測定幅+paddingで確保し、pathRightMaxWidthで切り詰めない。
        bool hasMarks = display.MarkCount > 0 && !string.IsNullOrWhiteSpace(display.MarkSizeText);
        string pathRightText = BuildHeaderRightText(display, hasMarks);
        lblSort.Text = pathRightText;
        lblSort.Visible = !string.IsNullOrWhiteSpace(pathRightText);
        lblSort.Cursor = IsHeaderSortText(pathRightText) ? Cursors.Hand : Cursors.Default;
        // 【Item行右端】 (lblFileStatsEx): Attr Timestamp (常に選択アイテムの情報)
        string itemRightText = display.ItemMetaWithoutSize;
        lblFileStatsEx.Text = itemRightText;
        lblFileStatsEx.Visible = !string.IsNullOrWhiteSpace(itemRightText);
        // 【Corrective】 右側blockの幅を実測幅優先で算出（pathRightMaxWidthによる切り詰めを廃止）
        //   RightBlockPadding: 描画余白として確保する最小ピクセル数
        const int RightBlockPadding = 16;
        int sortWidth = !string.IsNullOrWhiteSpace(pathRightText)
            ? HeaderLayoutHelper.MeasureLabelReservedWidth(lblSort, pathRightText, RightBlockPadding)
            : 0;
        int metaWidth = !string.IsNullOrWhiteSpace(itemRightText)
            ? Math.Max(HeaderLayoutHelper.MeasureLabelReservedWidth(lblFileStatsEx, itemRightText, RightBlockPadding), 180)
            : 0;
        lblSort.Width = sortWidth;
        lblFileStatsEx.Width = metaWidth;
        // 【Corrective】 残り幅を計算し、左側テキストを手動で省略する
        int pathAvailableWidth = infoRow2Panel.ClientSize.Width - (lblSort.Visible ? lblSort.Width : 0) - 8;
        int nameAvailableWidth = infoRow4Panel.ClientSize.Width - (lblFileStatsEx.Visible ? lblFileStatsEx.Width : 0) - 8;
        // Path行左
        lblPath.Text = HeaderLayoutHelper.FitTextWithEllipsis(display.Path, lblPath.Font, pathAvailableWidth);
        ApplyPathDisplayMode();
        // Item行左
        if (display.SelectedItemIsDirectory)
        {
            lblName.Text = HeaderLayoutHelper.FitDirectoryNameHeaderText(display.RawFileName, lblName.Font, nameAvailableWidth);
        }
        else
        {
            lblName.Text = HeaderLayoutHelper.FitFileNameWithSizePreservingExtension(
                display.RawFileName,
                display.SelectedItemSizeText,
                lblName.Font,
                nameAvailableWidth);
        }
        // 不要な個別ラベルは非表示にする
        lblItemAttr.Visible = false;
        lblFileDate.Visible = false;
        lblFileStats.Visible = false;
        lblUsed.Text = display.DriveUsed;
        lblFree.Text = display.DriveFree;
        // レイアウトの再配置
        PositionHeaderLabels();
        // Row 2 は custom paint のため、テキスト更新後に幅再計算と再描画を明示する
        UpdateHeaderInteractionTooltips();
        RefreshHeaderDisplay();
        RefreshBrowserStatusSummary();
        LogHeaderRightDiag("UpdateInfoPanel", display.MarkCount, display.MarkSizeText, pathRightText, itemRightText);
        LogFontRouteDiag("UpdateInfoPanel:END");
    }
    private static string BuildHeaderRightText(HeaderPresentationHelper.DisplayStrings display, bool hasMarks)
    {
        if (!hasMarks)
        {
            return display.SortFilter;
        }

        string markText = $"Mark: {display.MarkCount} MarkSize: {display.MarkSizeText}";
        int sortIndex = display.SortFilter.IndexOf("S:", StringComparison.Ordinal);
        if (sortIndex < 0)
        {
            return string.IsNullOrWhiteSpace(display.SortFilter)
                ? markText
                : $"{display.SortFilter} {markText}";
        }

        string filterText = display.SortFilter[..sortIndex].TrimEnd();
        string sortText = display.SortFilter[sortIndex..].TrimStart();
        return string.IsNullOrWhiteSpace(filterText)
            ? $"{markText} {sortText}"
            : $"{filterText} {markText} {sortText}";
    }

    private void ApplyUncDriveInfo(string root, long generation, UncDriveInfoResolver.Result result, bool succeeded)
    {
        if (IsDisposed || Disposing || _isExitConfirmationPending || !succeeded)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || Disposing || generation != _directoryNavigationGeneration ||
                    !NetworkPathResolutionPolicy.TryGetUncRoot(_navigationService.CurrentPath, out string currentRoot) ||
                    !string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                UpdateInfoPanel();
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }
    private string GetMarkSummaryForHeader()
    {
        if (_markedFiles.Count == 0)
        {
            if (_markSummaryCacheState != MarkSummaryCacheState.Complete || _markSummaryCacheCount != 0)
            {
                SetCountOnlyMarkSummaryCache();
            }
            CancelPendingMarkSummaryRebuild();
            return string.Empty;
        }
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        bool deferAuxiliaryResolution = NetworkPathResolutionPolicy.IsAuxiliaryResolutionDeferred(_navigationService.CurrentPath) ||
            _markedFiles.Any(NetworkPathResolutionPolicy.IsUncPath);
        if (deferAuxiliaryResolution)
        {
            if (_markSummaryCacheState != MarkSummaryCacheState.CountOnly
                || _markSummaryCacheCount != _markedFiles.Count
                || !string.Equals(_markSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase))
            {
                SetCountOnlyMarkSummaryCache();
            }
            CancelPendingMarkSummaryRebuild();

            NetworkPathResolutionPolicy.LogDecision(
                "NetworkPathResolutionDeferral.Skip",
                "HeaderInfo.MarkSummary",
                nameof(GetMarkSummaryForHeader),
                _navigationService.CurrentPath,
                usedCached: true,
                resolvedSync: false,
                reason: "unc-path");
            return _markSummaryCache;
        }
        if (_markSummaryCacheState == MarkSummaryCacheState.Complete
            && _markSummaryCacheCount == _markedFiles.Count
            && string.Equals(_markSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase))
        {
            return _markSummaryCache;
        }
        if (_markSummaryCacheState == MarkSummaryCacheState.Invalid
            || _markSummaryCacheCount != _markedFiles.Count
            || !string.Equals(_markSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase))
        {
            SetCountOnlyMarkSummaryCache();
        }
        if (!_markSummaryRebuildCoordinator.HasPending)
        {
            ScheduleMarkSummaryRebuild();
        }
        return _markSummaryCache;
    }
    private void InvalidateMarkSummaryCache()
    {
        _markSummaryCacheState = MarkSummaryCacheState.Invalid;
        _markSummaryRebuildCoordinator.Invalidate();
    }

    private void ScheduleMarkSummaryRebuild()
    {
        if (_markedFiles.Count == 0 || NetworkPathResolutionPolicy.IsAuxiliaryResolutionDeferred(_navigationService.CurrentPath) ||
            _markedFiles.Any(NetworkPathResolutionPolicy.IsUncPath) ||
            _markSummaryRebuildCoordinator.HasPending ||
            IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath)
        {
            return;
        }
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        IReadOnlyList<string> paths = _markedFiles.Snapshot();
        _ = _markSummaryRebuildCoordinator.Schedule(currentDir, paths);
    }
    private void CancelPendingMarkSummaryRebuild()
    {
        if (_markSummaryRebuildCoordinator.HasPending)
        {
            _markSummaryRebuildCoordinator.Invalidate();
        }
    }
    private static async Task<MarkSummaryBuildResult> BuildMarkSummaryAsync(
        string currentDir,
        IReadOnlyList<string> paths,
        CancellationToken token)
    {
        await Task.Delay(150, token).ConfigureAwait(false);
        long totalSize = 0;
        int fileCount = 0;
        int outsideCount = 0;
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                currentDir,
                StringComparison.OrdinalIgnoreCase))
            {
                outsideCount++;
            }
            try
            {
                if (File.Exists(path))
                {
                    totalSize += new FileInfo(path).Length;
                    fileCount++;
                }
            }
            catch
            {
            }
        }
        return new MarkSummaryBuildResult(totalSize, fileCount, outsideCount);
    }
    private void ApplyCompletedMarkSummary(
        string currentDir,
        IReadOnlyList<string> paths,
        MarkSummaryBuildResult result)
    {
        _markSummaryCacheTotalSize = result.TotalSize;
        _markSummaryCacheFileCount = result.FileCount;
        _markSummaryCacheOutsideCount = result.OutsideCount;
        string size = FileOperationService.FormatSize(result.TotalSize);
        string outside = result.OutsideCount > 0 ? $" Out:{result.OutsideCount}" : string.Empty;
        _markSummaryCache = $"Mark:{paths.Count,3} ({result.FileCount} Files){outside} {size}";
        _markSummaryCacheCount = paths.Count;
        _markSummaryCacheSizeText = size;
        _markSummaryCacheCompact = $"Mark: {paths.Count} MarkSize: {size}";
        _markSummaryCachePath = currentDir;
        _markSummaryCacheState = MarkSummaryCacheState.Complete;
        MarkSummaryOrchestrationMetrics metrics = _markSummaryRebuildCoordinator.GetMetrics();
        LogService.Info(
            $"[MarkSummaryOrchestration] marks={paths.Count} schedule={metrics.ScheduleCount} build={metrics.BuildCount} " +
            $"cancel={metrics.CancelCount} superseded={metrics.SupersededCount} apply={metrics.ApplyCount + 1}");
        UpdateInfoPanel();
    }
    private void SetCountOnlyMarkSummaryCache()
    {
        _markSummaryCache = _markedFiles.Count > 0
            ? $"Mark:{_markedFiles.Count,3}"
            : string.Empty;
        _markSummaryCacheCount = _markedFiles.Count;
        _markSummaryCacheSizeText = string.Empty;
        _markSummaryCacheCompact = _markedFiles.Count > 0
            ? $"Mark: {_markedFiles.Count} MarkSize: ?"
            : string.Empty;
        _markSummaryCachePath = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        _markSummaryCacheState = _markedFiles.Count > 0
            ? MarkSummaryCacheState.CountOnly
            : MarkSummaryCacheState.Complete;
    }
    private void SetZeroMarkSummaryCache()
    {
        _markSummaryCacheTotalSize = 0;
        _markSummaryCacheFileCount = 0;
        _markSummaryCacheOutsideCount = 0;
        _markSummaryCache = string.Empty;
        _markSummaryCacheCount = 0;
        _markSummaryCacheSizeText = string.Empty;
        _markSummaryCacheCompact = string.Empty;
        _markSummaryCachePath = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        _markSummaryCacheState = MarkSummaryCacheState.Complete;
    }
    private bool TryCarryMarkSummaryAcrossDirectoryChange(string previousDirectory)
    {
        string previousDir = NavigationService.NormalizeDirectoryForCompare(previousDirectory);
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        if (_markedFiles.Count == 0)
        {
            SetZeroMarkSummaryCache();
            return true;
        }
        if (_markSummaryCacheState != MarkSummaryCacheState.Complete ||
            _markSummaryRebuildCoordinator.HasPending ||
            _markSummaryCacheCount != _markedFiles.Count ||
            !string.Equals(_markSummaryCachePath, previousDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        MarkSummaryExactCache carried = new(
            _markSummaryCacheTotalSize,
            _markSummaryCacheFileCount,
            MarkSummaryOutsideCountCalculator.Count(_markedFiles, currentDir),
            _markedFiles.Count);
        SetCompleteMarkSummaryCache(currentDir, carried);
        return true;
    }
    private void ApplyMarkColor(ListViewItem item, string fullPath)
    {
        // Phase 2g-fix6.4b: 文字列への '*' 挿入を廃止。描画スロット方式へ移行
        // ここではファイル種別に応じた基本色の再設定のみを行う
        var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settings);
        item.ForeColor = ResolveBrowserItemForeColor(item, fullPath, resolved);
        // 背景色は常に通常色 (Black) を維持。マーク背景塗りは BrowserPanel_Paint 側の
        // 選択状態との組み合わせで処理される。
        item.BackColor = resolved.Background;
    }
    private Color ResolveBrowserItemForeColor(ListViewItem item, string? fullPath, FileListColorResolver.ResolvedColors resolved)
    {
        if (item.Text == "..")
        {
            return resolved.Directory;
        }

        bool isDir = IsDirectoryListItem(item, fullPath);
        if (TryGetAttributesForColor(item, fullPath ?? string.Empty, out FileAttributes attrs))
        {
            return ResolveAttributeColor(attrs, isDir);
        }

        return isDir ? resolved.Directory : resolved.NormalFile;
    }

    private static Color ResolveMarkGlyphColor(Color background, Color preferredColor)
    {
        _ = preferredColor;
        return FileListColorResolver.GetRelativeLuminance(background) > 0.5 ? Color.Black : Color.White;
    }
    private static Color ResolveMouseGestureTrailColor(FileListColorResolver.ResolvedColors resolved)
    {
        Color source = HasColorHue(resolved.Marked) ? resolved.Marked : resolved.Directory;
        if (!HasColorHue(source))
        {
            source = Color.Cyan;
        }

        double backgroundLuminance = FileListColorResolver.GetRelativeLuminance(resolved.Background);
        double sourceLuminance = FileListColorResolver.GetRelativeLuminance(source);
        if (Math.Abs(backgroundLuminance - sourceLuminance) < 0.28)
        {
            source = backgroundLuminance > 0.5
                ? ControlPaint.Dark(source, 0.35f)
                : ControlPaint.Light(source, 0.45f);
        }

        return source;
    }
    private static bool HasColorHue(Color color)
    {
        int range = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        return range >= 24 && color.A > 0;
    }
    private Color ResolveAttributeColor(FileAttributes attrs, bool isDirectory)
    {
        var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settings);
        if (attrs.HasFlag(FileAttributes.System))
            return resolved.System;
        if (attrs.HasFlag(FileAttributes.Hidden))
            return resolved.Hidden;
        if (attrs.HasFlag(FileAttributes.ReadOnly))
            return resolved.ReadOnly;
        return isDirectory ? resolved.Directory : resolved.NormalFile;
    }
    private static bool TryGetAttributesForColor(ListViewItem item, string fullPath, out FileAttributes attrs)
    {
        attrs = FileAttributes.Normal;
        try
        {
            attrs = File.GetAttributes(fullPath);
            return true;
        }
        catch
        {
            if (item.SubItems.Count > 4)
            {
                string code = item.SubItems[4].Text ?? string.Empty;
                if (code.Contains('R')) attrs |= FileAttributes.ReadOnly;
                if (code.Contains('H')) attrs |= FileAttributes.Hidden;
                if (code.Contains('S')) attrs |= FileAttributes.System;
                if (code.Contains('A')) attrs |= FileAttributes.Archive;
                return true;
            }
        }
        return false;
    }
    private string GetItemFullName(ListViewItem item)
    {
        if (item == null) return string.Empty;
        if (item.Text == "..") return "..";
        string name = item.Text;
        if (!IsDirectoryListItem(item) && item.SubItems.Count > 1 && !string.IsNullOrEmpty(item.SubItems[1].Text))
        {
            name += "." + item.SubItems[1].Text;
        }
        return name;
    }
    private bool IsDirectoryListItem(ListViewItem item)
    {
        return item != null && IsDirectoryListItem(item, item.Tag as string);
    }
    private bool IsDirectoryListItem(ListViewItem item, string? fullPath)
    {
        if (item.Text == "..")
        {
            return true;
        }
        if (!string.IsNullOrEmpty(fullPath) && Directory.Exists(fullPath))
        {
            return true;
        }
        return false;
    }
    /// <summary>
    /// 現在の「対象アイテム」を取得する一元化メソッド。
    /// 多列Browser表示やViewer中にかかわらず、_browserCursorIndex を正本とする。
    /// </summary>
    private ListViewItem? GetCurrentBrowserItem()
    {
        if (fileListView.Items.Count == 0) return null;
        // 1. 選択中アイテムがあれば最優先 (Mouse操作・一括処理等への整合)
        if (fileListView.SelectedItems.Count > 0)
        {
            return fileListView.SelectedItems[0];
        }
        // 2. フォーカスアイテムがあれば次点
        if (fileListView.FocusedItem != null)
        {
            return fileListView.FocusedItem;
        }
        // 3. 内部カーソル位置 (_browserCursorIndex)
        int pageLocalCursorIndex = GetBrowserPageLocalCursorIndex();
        if (pageLocalCursorIndex >= 0 && pageLocalCursorIndex < fileListView.Items.Count)
        {
            return fileListView.Items[pageLocalCursorIndex];
        }
        // 万が一のフォールバック
        return fileListView.Items[0];
    }
    // ─── OwnerDraw ハンドラ (選択反転の緩和) ─────────────────────────────
    private void FileListView_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        // DrawSubItem側で描画するのでここでは何もしない
    }
    private void FileListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null) return;
        var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settings);
        bool selected = e.Item.Selected;
        bool useUnderline = _settings.Appearance?.UseUnderlineCursor == true;

        bool isMarked = e.Item.Tag is string fullPath && _markedFiles.Contains(fullPath);

        Color bg = resolved.Background;

        // 選択時でも元のファイル種類・属性色を維持するため、動的に色を解決する
        Color fg = ResolveBrowserItemForeColor(e.Item, e.Item.Tag as string, resolved);

        if (selected && !useUnderline)
        {
            bg = resolved.SelectedBackground;
            fg = FileListColorResolver.ResolveSelectedForegroundForPreset(_settings.Appearance?.ColorTheme, fg, bg);
        }

        using var bgBrush = new SolidBrush(bg);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);
        Font font = e.Item.ListView?.Font ?? SystemFonts.DefaultFont;
        Rectangle textBounds = e.Bounds;
        if (e.ColumnIndex == 0 && isMarked)
        {
            const int markSlotWidth = 15;
            Rectangle markRect = new Rectangle(e.Bounds.X, e.Bounds.Y, markSlotWidth, e.Bounds.Height);
            textBounds = new Rectangle(
                e.Bounds.X + markSlotWidth,
                e.Bounds.Y,
                Math.Max(0, e.Bounds.Width - markSlotWidth),
                e.Bounds.Height);

            Color preferredMarkColor = GetCurrentThemeMarkGlyphColor();
            Color markColor = ResolveMarkGlyphColor(bg, preferredMarkColor);

            TextRenderer.DrawText(
                e.Graphics,
                "*",
                font,
                markRect,
                markColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem?.Text ?? "",
            font,
            textBounds,
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (_settings.Appearance?.UseUnderlineCursor == true && e.Item.Selected && e.ColumnIndex == 0)
        {
            DrawCursorUnderline(e.Graphics, e.Item.Bounds, fg);
        }
    }
    private void FileListView_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        // 列ヘッダーはシステムデフォルトのまま
        e.DrawDefault = true;
    }
    // ─── Phase 15A: BrowserPanel 多列描画用ロジック ──────────────────────────
    private void BrowserPanel_Paint(object? sender, PaintEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settings);
        Graphics g = e.Graphics;
        g.Clear(resolved.Background);
        if (fileListView.Items.Count == 0)
        {
            DrawCommandHintOverlay(g);
            DrawMouseGestureTrail(g);
            return;
        }
        int totalItems = fileListView.Items.Count;
        Font font = browserPanel.Font;
        int itemsPerPage = GetBrowserItemsPerPage(out int itemHeight, out int rowsPerColumn);
        int effectiveColumnCount = GetEffectiveBrowserColumnCount();
        // 列幅の計算
        int colWidth = Math.Max(1, browserPanel.Width / effectiveColumnCount);
        int startIndex = 0;
        int endIndex = fileListView.Items.Count;
        int pageLocalCursorIndex = GetBrowserPageLocalCursorIndex();
        // ページ内のアイテムを描画
        for (int i = startIndex; i < endIndex; i++)
        {
            int pageIndex = i - startIndex;
            int col = pageIndex / rowsPerColumn;
            int row = pageIndex % rowsPerColumn;
            int x = col * colWidth + 5;
            int y = row * itemHeight + 5;
            var item = fileListView.Items[i];
            bool isSelected = (i == pageLocalCursorIndex);
            // 描画領域の矩形
            Rectangle rect = new Rectangle(x, y, colWidth - 10, itemHeight);
            // 描画設定の決定
            Color bg = resolved.Background;

            // 選択時でも元のファイル種類・属性色を維持するため、動的に色を解決する
            Color fg = ResolveBrowserItemForeColor(item, item.Tag as string, resolved);

            // item.Tag にフルパスが入っている前提でマーク状態を判定 (文字列依存からの脱却)
            bool isMarked = _markedFiles.Contains(item.Tag as string ?? string.Empty);
            bool useUnderline = _settings.Appearance?.UseUnderlineCursor == true;
            if (isSelected && !useUnderline)
            {
                bg = resolved.SelectedBackground;
                fg = FileListColorResolver.ResolveSelectedForegroundForPreset(_settings.Appearance?.ColorTheme, fg, bg);
            }
            // 背景描画
            using (SolidBrush bgBrush = new SolidBrush(bg))
            {
                g.FillRectangle(bgBrush, rect);
            }
            // テキスト描画 (WinFD寄せ: Mark Slot を導入し、* とファイル名を分離)
            int iconSize = Math.Clamp((int)Math.Round(font.Height * 0.9), 12, 48);
            bool showItemIcons = _settings.Appearance?.ShowItemIcons ?? true;
            int markSlotWidth = GetBrowserMarkSlotWidth(font, showItemIcons, iconSize);
            Rectangle markRect = new Rectangle(rect.X, rect.Y, markSlotWidth, rect.Height);
            int iconSlotWidth = showItemIcons ? (iconSize + 2) : 0;
            Rectangle iconRect = new Rectangle(rect.X + markSlotWidth, rect.Y + Math.Max(0, (rect.Height - iconSize) / 2), iconSize, iconSize);
            Rectangle textRect = new Rectangle(rect.X + markSlotWidth + iconSlotWidth, rect.Y, rect.Width - markSlotWidth - iconSlotWidth, rect.Height);
            if (isMarked)
            {
                Color preferredMarkColor = GetCurrentThemeMarkGlyphColor();
                Color markColor = ResolveMarkGlyphColor(bg, preferredMarkColor);
                TextRenderer.DrawText(g, "*", font, markRect, markColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            if ((_settings.Appearance?.ShowItemIcons ?? true) && textRect.Width > 24)
            {
                DrawBrowserItemIcon(g, item, iconRect);
            }
            bool detailDrawn = false;
            BrowserFileDisplayMode fileDisplayMode = GetBrowserFileDisplayMode();
            if (fileDisplayMode != BrowserFileDisplayMode.NameOnly)
            {
                detailDrawn = DrawBrowserItemTextWithDetails(g, item, textRect, font, fg, fileDisplayMode);
                if (!detailDrawn && fileDisplayMode == BrowserFileDisplayMode.NameSizeDate)
                {
                    // 狭い列幅では日時欄を省略してサイズ表示まで維持する
                    detailDrawn = DrawBrowserItemTextWithDetails(g, item, textRect, font, fg, BrowserFileDisplayMode.NameSize);
                }
            }
            if (!detailDrawn)
            {
                string text = BuildBrowserDisplayText(item, textRect.Width, font, g);
                TextRenderer.DrawText(g, text, font, textRect, fg, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if ((_settings.Appearance?.UseUnderlineCursor ?? false) && isSelected)
            {
                DrawCursorUnderline(g, rect, fg);
            }
        }
        DrawCommandHintOverlay(g);
        DrawMouseGestureTrail(g);
    }
    private void DrawMouseGestureTrail(Graphics g)
    {
        DrawMouseGestureTrail(g, browserPanel);
    }

    private void DrawMouseGestureTrail(Graphics g, Control surface)
    {
        if (!_isMouseGestureTrailVisible || _mouseGestureTrailPoints.Count < 2)
        {
            return;
        }

        var oldSmoothing = g.SmoothingMode;
        try
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settings);
            Color trailColor = ResolveMouseGestureTrailColor(resolved);
            using var pen = new Pen(Color.FromArgb(220, trailColor), 2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };

            Point[] points = _mouseGestureTrailPoints
                .Select(point => surface.PointToClient(PointToScreen(point)))
                .ToArray();
            if (points.Length >= 2)
            {
                g.DrawLines(pen, points);
            }
        }
        finally
        {
            g.SmoothingMode = oldSmoothing;
        }
    }
    private void DrawCursorUnderline(Graphics graphics, Rectangle bounds, Color underlineColor)
    {
        int y = bounds.Bottom - 2;
        using var underlinePen = new Pen(underlineColor, 1);
        graphics.DrawLine(underlinePen, bounds.Left + 2, y, bounds.Right - 2, y);
    }
    private Color GetCurrentThemeMarkGlyphColor()
    {
        var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settings);
        return resolved.Marked;
    }
    private void DrawBrowserItemIcon(Graphics g, ListViewItem item, Rectangle iconRect)
    {
        try
        {
            string? fullPath = item.Tag as string;
            bool isDirectory = IsDirectoryListItem(item, fullPath);
            using var icon = (Icon)BrowserItemIconProvider.GetIcon(fullPath, isDirectory, iconRect.Width).Clone();
            g.DrawIcon(icon, iconRect);
        }
        catch
        {
            // アイコン取得失敗時は一覧描画を優先して無視する
        }
    }
    private static int GetBrowserMarkSlotWidth(Font font, bool showItemIcons, int iconSize)
    {
        int baseSlotWidth = showItemIcons ? Math.Clamp(iconSize / 2 + 8, 18, 32) : 15;
        int markGlyphWidth = TextRenderer.MeasureText("*", font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        return Math.Max(baseSlotWidth, markGlyphWidth + 8);
    }
    private string BuildBrowserDisplayText(ListViewItem item, int availableWidth, Font font, Graphics g)
    {
        bool isDir = IsDirectoryListItem(item);
        bool showDirectoryMarker = _settings.Appearance?.ShowDirectoryMarker ?? true;
        bool showExtensions = _settings.Appearance?.ShowExtensions ?? true;
        if (isDir)
        {
            if (item.Text == ".." || showDirectoryMarker)
            {
                return FitDirectoryTextPreservingMarker(item.Text, " <DIR>", availableWidth, font, g);
            }
            return FitTextWithTrailingEllipsis(item.Text, availableWidth, font, g);
        }
        string baseName = item.Text;
        string extension = showExtensions && item.SubItems.Count > 1 && !string.IsNullOrEmpty(item.SubItems[1].Text)
            ? "." + item.SubItems[1].Text
            : string.Empty;
        if (string.IsNullOrEmpty(extension))
        {
            return FitTextWithTrailingEllipsis(baseName, availableWidth, font, g);
        }
        return FitFileNamePreservingExtension(baseName, extension, availableWidth, font, g);
    }
    private bool DrawBrowserItemTextWithDetails(Graphics g, ListViewItem item, Rectangle textRect, Font font, Color fg, BrowserFileDisplayMode mode)
    {
        const string nameEllipsis = "...";

        bool isDirectory = IsDirectoryListItem(item);
        bool showExtensions = _settings.Appearance?.ShowExtensions ?? true;

        if (isDirectory && item.Text == "..")
        {
            return false;
        }

        string dateText = item.SubItems.Count > 3 ? NormalizeBrowserDateText(item.SubItems[3].Text) : string.Empty;
        if (DateTime.TryParse(item.SubItems.Count > 3 ? item.SubItems[3].Text : string.Empty, out DateTime parsedDate))
        {
            dateText = FileSystemItemFactory.FormatDisplayDate(parsedDate, _settings.Appearance?.DateFormat);
        }

        string sizeText = isDirectory
            ? "<DIR>"
            : BuildBrowserFileSizeTextCompact(item);

        bool includeDate = mode == BrowserFileDisplayMode.NameSizeDate;
        if (includeDate && string.IsNullOrWhiteSpace(dateText))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(sizeText))
        {
            return false;
        }

        int gapWidth = Math.Max(2, MeasureBrowserTextWidth(g, " ", font));
        string sizeSample = GetBrowserCompactSizeFieldSample();
        string dateSample = GetBrowserDateFieldSample();
        int dateFieldWidth = includeDate
            ? Math.Max(MeasureBrowserTextWidth(g, dateSample, font), MeasureBrowserTextWidth(g, dateText, font))
            : 0;
        int sizeFieldWidth = Math.Max(
            MeasureBrowserTextWidth(g, sizeSample, font),
            Math.Max(MeasureBrowserTextWidth(g, "<DIR>", font), MeasureBrowserTextWidth(g, sizeText, font)));
        int minimumNameWidth = MeasureBrowserTextWidth(g, nameEllipsis, font);
        int requiredWidth = includeDate
            ? minimumNameWidth + sizeFieldWidth + dateFieldWidth + (gapWidth * 2)
            : minimumNameWidth + sizeFieldWidth + gapWidth;

        if (textRect.Width < requiredWidth)
        {
            return false;
        }

        int reservedDetailWidth = includeDate
            ? sizeFieldWidth + dateFieldWidth + (gapWidth * 2)
            : sizeFieldWidth + gapWidth;
        int nameFieldWidth = Math.Max(minimumNameWidth, textRect.Width - reservedDetailWidth);

        Rectangle nameRect = new Rectangle(textRect.X, textRect.Y, nameFieldWidth, textRect.Height);
        Rectangle sizeRect = new Rectangle(nameRect.Right + gapWidth, textRect.Y, sizeFieldWidth, textRect.Height);
        Rectangle dateRect = includeDate
            ? new Rectangle(sizeRect.Right + gapWidth, textRect.Y, dateFieldWidth, textRect.Height)
            : Rectangle.Empty;

        if (nameRect.Width < minimumNameWidth)
        {
            return false;
        }

        string nameText;
        if (isDirectory)
        {
            nameText = FitTextWithTrailingEllipsis(item.Text, nameRect.Width, font, g);
        }
        else
        {
            string baseName = item.Text;
            string extension = showExtensions && item.SubItems.Count > 1 && !string.IsNullOrEmpty(item.SubItems[1].Text)
                ? "." + item.SubItems[1].Text
                : string.Empty;
            nameText = string.IsNullOrEmpty(extension)
                ? FitTextWithTrailingEllipsis(baseName, nameRect.Width, font, g)
                : FitFileNamePreservingExtension(baseName, extension, nameRect.Width, font, g);
        }

        TextRenderer.DrawText(g, nameText, font, nameRect, fg, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, sizeText, font, sizeRect, fg, Color.Transparent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (includeDate)
        {
            TextRenderer.DrawText(g, dateText, font, dateRect, fg, Color.Transparent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
        return true;
    }
    private static string NormalizeBrowserDateText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return DateTime.TryParse(raw, out DateTime parsed)
            ? parsed.ToString("yyyy-MM-dd HH:mm")
            : raw;
    }
    private static string FitFileNamePreservingExtension(string baseName, string extension, int availableWidth, Font font, Graphics g)
    {
        string fullText = baseName + extension;
        if (MeasureBrowserTextWidth(g, fullText, font) <= availableWidth)
        {
            return fullText;
        }
        if (MeasureBrowserTextWidth(g, extension, font) > availableWidth)
        {
            return FitTextWithTrailingEllipsis(fullText, availableWidth, font, g);
        }
        const string ellipsis = "…";
        string minimumCandidate = ellipsis + extension;
        if (MeasureBrowserTextWidth(g, minimumCandidate, font) > availableWidth)
        {
            return FitTextWithTrailingEllipsis(fullText, availableWidth, font, g);
        }
        int low = 0;
        int high = baseName.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = baseName[..mid] + ellipsis + extension;
            if (MeasureBrowserTextWidth(g, candidate, font) <= availableWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }
        return low <= 0
            ? minimumCandidate
            : baseName[..low] + ellipsis + extension;
    }
    private static string FitTextWithTrailingEllipsis(string text, int availableWidth, Font font, Graphics g)
    {
        if (MeasureBrowserTextWidth(g, text, font) <= availableWidth)
        {
            return text;
        }
        const string ellipsis = "…";
        if (MeasureBrowserTextWidth(g, ellipsis, font) > availableWidth)
        {
            return string.Empty;
        }
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = text[..mid] + ellipsis;
            if (MeasureBrowserTextWidth(g, candidate, font) <= availableWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }
        return low <= 0
            ? ellipsis
            : text[..low] + ellipsis;
    }
    private static string FitDirectoryTextPreservingMarker(string baseName, string marker, int availableWidth, Font font, Graphics g)
    {
        string fullText = baseName + marker;
        if (MeasureBrowserTextWidth(g, fullText, font) <= availableWidth)
        {
            return fullText;
        }
        int markerWidth = MeasureBrowserTextWidth(g, marker, font);
        if (markerWidth > availableWidth)
        {
            return FitTextWithTrailingEllipsis(fullText, availableWidth, font, g);
        }
        const string ellipsis = "…";
        string minimumCandidate = ellipsis + marker;
        if (MeasureBrowserTextWidth(g, minimumCandidate, font) > availableWidth)
        {
            return FitTextWithTrailingEllipsis(fullText, availableWidth, font, g);
        }
        int low = 0;
        int high = baseName.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = baseName[..mid] + ellipsis + marker;
            if (MeasureBrowserTextWidth(g, candidate, font) <= availableWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }
        return low <= 0
            ? minimumCandidate
            : baseName[..low] + ellipsis + marker;
    }
    private static int MeasureBrowserTextWidth(Graphics g, string text, Font font)
    {
        return TextRenderer.MeasureText(
            g,
            text,
            font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
    }
    internal static string BuildBrowserFileSizeTextCompact(ListViewItem item)
    {
        return item.SubItems.Count > 2
            ? item.SubItems[2].Text
            : string.Empty;
    }
    private string GetBrowserDateFieldSample()
    {
        DateTime sampleDate = new DateTime(2099, 12, 31, 23, 59, 59);
        return FileSystemItemFactory.FormatDisplayDate(sampleDate, _settings.Appearance?.DateFormat);
    }
    private static string GetBrowserCompactSizeFieldSample() => "999.9PB";
    private int GetEffectiveBrowserColumnCount()
    {
        int desiredColumns = Math.Max(1, _columnCount);
        int minimumColumnWidth = GetMinimumBrowserColumnWidthForMode(GetBrowserFileDisplayMode());
        int maxColumnsByWidth = Math.Max(1, browserPanel.Width / Math.Max(1, minimumColumnWidth));
        return Math.Max(1, Math.Min(desiredColumns, maxColumnsByWidth));
    }
    private static int GetMinimumBrowserColumnWidthForMode(BrowserFileDisplayMode mode)
    {
        return mode switch
        {
            BrowserFileDisplayMode.NameSize => 220,
            BrowserFileDisplayMode.NameSizeDate => 340,
            _ => 140
        };
    }
    private void BrowserPanel_Resize(object? sender, EventArgs e)
    {
        if (_uiMode == UIMode.Browser)
        {
            RematerializeBrowserPageIfCapacityChanged();
            UpdateInfoPanel();
            browserPanel.Invalidate();
        }
    }
    /// <summary>
    /// カスタムカーソル位置(_browserCursorIndex)を裏側のListViewに同期し、画面再描画とInfoPanel更新を行う。
    /// </summary>
    private void SyncBrowserSelection()
    {
        int pageLocalCursorIndex = GetBrowserPageLocalCursorIndex();
        if (pageLocalCursorIndex < 0 || pageLocalCursorIndex >= fileListView.Items.Count)
            return;
        _suppressBrowserSelectionChanged = true;
        try
        {
            fileListView.SelectedItems.Clear();
            var item = fileListView.Items[pageLocalCursorIndex];
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
        }
        finally
        {
            _suppressBrowserSelectionChanged = false;
        }
        ApplyBrowserSelectionChanged();
        // UI描画更新
        browserPanel.Invalidate();
        CaptureActiveBrowserTabState();
    }
    // ─── Phase 3-fix1c: マウス基本操作（単クリック/ダブルクリック） ───
    private void BrowserPanel_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.XButton1 || e.Button == MouseButtons.XButton2)
        {
            return;
        }
        if (_uiMode != UIMode.Browser) return;
        ClearPendingEscExitMarkPersistence();
        int newIndex = CalculateBrowserIndexFromPoint(e.X, e.Y);
        int newPageLocalIndex = newIndex - _browserPageStartIndex;
        if (e.Button == MouseButtons.Left)
        {
            if (newPageLocalIndex >= 0 && newPageLocalIndex < fileListView.Items.Count)
            {
                bool shiftPressed = (ModifierKeys & Keys.Shift) == Keys.Shift;
                bool ctrlPressed = (ModifierKeys & Keys.Control) == Keys.Control;
                int previousCursorIndex = GetBrowserPageLocalCursorIndex();
                BrowserMarkClickDecision clickDecision = _browserMarkInteractionController.ResolveLeftClick(
                    newPageLocalIndex,
                    previousCursorIndex,
                    fileListView.Items.Count,
                    ctrlPressed,
                    shiftPressed,
                    _markedFiles.Count > 0);
                _browserCursorIndex = _browserPageStartIndex + newPageLocalIndex;
                SyncBrowserSelection();
                if (clickDecision.Kind == BrowserMarkClickKind.AddRange)
                {
                    AddBrowserMouseMarkRange(clickDecision.AnchorIndex, newPageLocalIndex);
                }
                else if (clickDecision.Kind == BrowserMarkClickKind.PromotePendingAndToggleSingle)
                {
                    AddBrowserMouseMarkRange(clickDecision.PendingPromotionIndex, clickDecision.PendingPromotionIndex);
                    ToggleBrowserMouseMarkByIndex(newPageLocalIndex);
                }
                else if (clickDecision.Kind == BrowserMarkClickKind.ToggleSingle)
                {
                    ToggleBrowserMouseMarkByIndex(newPageLocalIndex);
                }
            }
            else
            {
                _browserMarkInteractionController.ClearPendingPromotionCandidate();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            if (TryConsumeBrowserContextMenuSuppress()) return;
            bool itemHit = newIndex >= 0
                && TryGetBrowserItemLayoutBounds(newIndex, out Rectangle contextItemBounds, out _)
                && contextItemBounds.Contains(e.Location)
                && newPageLocalIndex >= 0
                && newPageLocalIndex < fileListView.Items.Count;
            if (itemHit)
            {
                var item = fileListView.Items[newPageLocalIndex];
                var targetResolution = BrowserContextMenuTargetResolver.Resolve(
                    _markedFiles.Snapshot(),
                    newPageLocalIndex,
                    fileListView.Items.Count,
                    item.Tag as string,
                    item.Text == "..");
                if (GetBrowserPageLocalCursorIndex() != targetResolution.TargetIndex)
                {
                    _browserCursorIndex = _browserPageStartIndex + targetResolution.TargetIndex;
                    SyncBrowserSelection();
                }
                ShowBrowserItemContextMenu(e.Location, item, targetResolution);
                return;
            }

            ShowBrowserBlankContextMenu(e.Location);
        }
    }
    private ToolStripMenuItem? Create7ZipMenu(SelectionResult res)
    {
        // 7-Zip のベースパスを設定値 -> 自動検索の順で取得
        string? base7zPath = SevenZipService.ResolveCliExecutable(_settings.SevenZip.ExePath);
        if (string.IsNullOrEmpty(base7zPath))
        {
            base7zPath = SevenZipService.FindSevenZip();
        }
        if (string.IsNullOrEmpty(base7zPath)) return null;
        string sevenZipDir = Path.GetDirectoryName(base7zPath) ?? string.Empty;
        if (string.IsNullOrEmpty(sevenZipDir)) return null;
        string sevenZipG = Path.Combine(sevenZipDir, "7zG.exe");
        string sevenZipFM = Path.Combine(sevenZipDir, "7zFM.exe");
        string sevenZipExe = base7zPath; // 7z.exe または 7zG.exe (設定値)
        // 展開・圧縮用のバイナリ (GUI版があれば優先使用。なければベースパス)
        string processingExe = File.Exists(sevenZipG) ? sevenZipG : sevenZipExe;
        var menu = new ToolStripMenuItem("7-Zip");
        if (res.Count == 1)
        {
            string path = res.FirstPath!;
            string ext = Path.GetExtension(path).ToLower();
            bool isArchive = ArchiveFileTypeHelper.IsArchive(path);
            if (isArchive)
            {
                string dir = Path.GetDirectoryName(path) ?? "";
                string nameWithoutExt = Path.GetFileNameWithoutExtension(path);
                menu.DropDownItems.Add(new ToolStripMenuItem("ここに展開", null, (s, e) =>
                {
                    if (GuardReadOnlyBrowserTab("解凍")) return;
                    Run7ZipAndReload(processingExe, $"x \"{path}\" -o\"{dir}\"");
                }));
                menu.DropDownItems.Add(new ToolStripMenuItem($"\"{nameWithoutExt}\\\" に展開", null, (s, e) =>
                {
                    if (GuardReadOnlyBrowserTab("解凍")) return;
                    Run7ZipAndReload(processingExe, $"x \"{path}\" -o\"{Path.Combine(dir, nameWithoutExt)}\"", nameWithoutExt);
                }));
                if (File.Exists(sevenZipFM))
                {
                    menu.DropDownItems.Add(new ToolStripMenuItem("7-Zip File Manager で開く", null, (s, e) =>
                        System.Diagnostics.Process.Start(sevenZipFM, $"\"{path}\"")));
                }
                menu.DropDownItems.Add(new ToolStripSeparator());
            }
        }
        // 7-Zip メニュー内に MidFD の標準圧縮・解凍導線を追加
        bool isReadOnly = IsActiveBrowserTabReadOnly();
        // CRC/SHA 計算サブメニュー
        bool canHash = res.Count > 0 && !res.FullPaths.Any(Directory.Exists);
        var hashMenu = new ToolStripMenuItem("CRC/SHA")
        {
            Enabled = canHash
        };
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("CRC-32", null, async (s, e) => await ExecuteHashAsync(SevenZipHashAlgorithm.Crc32)));
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("CRC-64", null, async (s, e) => await ExecuteHashAsync(SevenZipHashAlgorithm.Crc64)));
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("SHA-1", null, async (s, e) => await ExecuteHashAsync(SevenZipHashAlgorithm.Sha1)));
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("SHA-256", null, async (s, e) => await ExecuteHashAsync(SevenZipHashAlgorithm.Sha256)));
        hashMenu.DropDownItems.Add(new ToolStripSeparator());
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("すべて (*)", null, async (s, e) => await ExecuteHashAsync(SevenZipHashAlgorithm.All)));
        menu.DropDownItems.Add(hashMenu);
        menu.DropDownItems.Add(new ToolStripSeparator());
        var packItem = new ToolStripMenuItem("圧縮...", null, async (s, e) => await ExecutePack())
        {
            Enabled = !isReadOnly && res.Count > 0
        };
        menu.DropDownItems.Add(packItem);
        var unpackItem = new ToolStripMenuItem("解凍...", null, async (s, e) => await ExecuteUnpack())
        {
            Enabled = !isReadOnly && res.Count > 0 && res.FullPaths.Any(IsArchiveTarget)
        };
        menu.DropDownItems.Add(unpackItem);
        var packEachFolderItemSub = new ToolStripMenuItem("個別圧縮...", null, async (s, e) =>
        {
            await ExecutePackEachIndividuallyDirectAsync();
        })
        {
            Enabled = !isReadOnly && CanPackEachFolderIndividually(res)
        };
        menu.DropDownItems.Add(packEachFolderItemSub);
        // 従来の 7z 直接コマンド (クイック圧縮など) も残す場合はここ。
        // ユーザー指示の「推奨配置」を優先し、既存の「圧縮して追加...」は下部へ。
        menu.DropDownItems.Add(new ToolStripSeparator());
        var quickPackItem = new ToolStripMenuItem("圧縮して追加 (7z直接)...", null, (s, e) =>
        {
            if (GuardReadOnlyBrowserTab("圧縮")) return;
            if (res.FullPaths.Any())
            {
                var sb = new System.Text.StringBuilder();
                string archiveDir = Path.GetDirectoryName(res.FirstPath!) ?? "";
                string archiveName = res.Count == 1 ? Path.GetFileNameWithoutExtension(res.FirstPath!) : Path.GetFileName(archiveDir);
                if (string.IsNullOrEmpty(archiveName)) archiveName = "archive";
                string archiveFullName = archiveName + ".zip";
                sb.Append($"a \"{Path.Combine(archiveDir, archiveFullName)}\" ");
                foreach (var p in res.FullPaths)
                {
                    sb.Append($"\"{p}\" ");
                }
                Run7ZipAndReload(processingExe, sb.ToString().TrimEnd(), archiveFullName);
            }
        })
        {
            Enabled = !isReadOnly && res.Count > 0
        };
        menu.DropDownItems.Add(quickPackItem);
        return menu;
    }
    private void Run7ZipAndReload(string exePath, string arguments, string? focusName = null)
    {
        string startPath = _navigationService.CurrentPath;
        try
        {
            var process = Process.Start(exePath, arguments);
            if (process != null)
            {
                Task.Run(() =>
                {
                    process.WaitForExit();
                    this.BeginInvoke(new Action(() =>
                    {
                        if (_navigationService.CurrentPath == startPath)
                        {
                            LoadDirectory(_navigationService.CurrentPath, focusName);
                        }
                    }));
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"7-Zip 実行エラー: {ex.Message}");
            ShowStatusMessage($"7-Zip の起動に失敗しました: {ex.Message}");
        }
    }
    private void ExecuteOpenWith(SelectionResult res)
    {
        if (res.Count == 1)
        {
            string path = res.FirstPath!;
            if (File.Exists(path))
            {
                try
                {
                    System.Diagnostics.Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}");
                }
                catch (Exception ex)
                {
                    LogService.Error($"OpenWith 実行失敗: {ex.Message}");
                    MessageBox.Show(this, $"「プログラムから開く」ダイアログを起動できませんでした。\n理由: {ex.Message}", "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        else
        {
            ShowStatusMessage("複数項目には対応していません。");
        }
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
        public string lpVerb;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
        public string lpFile;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
        public string lpParameters;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }
    private const int SW_SHOW = 5;
    private const uint SEE_MASK_INVOKEIDLIST = 12;
    private void ExecuteProperties(SelectionResult res)
    {
        if (res.Count == 1)
        {
            try
            {
                string path = res.FirstPath!;
                SHELLEXECUTEINFO info = new SHELLEXECUTEINFO();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(info);
                info.lpVerb = "properties";
                info.lpFile = path;
                info.nShow = SW_SHOW;
                info.fMask = SEE_MASK_INVOKEIDLIST;
                ShellExecuteEx(ref info);
            }
            catch (Exception ex)
            {
                LogService.Error($"ExecuteProperties 失敗: {ex.Message}");
            }
        }
        else
        {
            ShowStatusMessage("複数プロパティ一括表示は未対応です。");
        }
    }
    private void BrowserPanel_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        if (e.Button != MouseButtons.Left) return;
        int newIndex = CalculateBrowserIndexFromPoint(e.X, e.Y);
        int newPageLocalIndex = newIndex - _browserPageStartIndex;
        if (newPageLocalIndex >= 0 && newPageLocalIndex < fileListView.Items.Count)
        {
            _browserCursorIndex = newIndex;
            SyncBrowserSelection();
            ExecuteDefaultOpen(); // ダブルクリック専用（既定アプリ等）へ流す
        }
    }
    private void ToggleBrowserMouseMarkByIndex(int index)
    {
        string? path = TryGetMarkableBrowserPathByIndex(index);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_markedFiles.Contains(path))
        {
            UnmarkPath(path);
        }
        else
        {
            MarkPath(path);
        }

        RefreshMarkUi();
        PrimeRecentMultiMarkIntent();
    }
    private void AddBrowserMouseMarkRange(int anchorIndex, int clickedIndex)
    {
        int start = Math.Max(0, Math.Min(anchorIndex, clickedIndex));
        int end = Math.Min(fileListView.Items.Count - 1, Math.Max(anchorIndex, clickedIndex));
        var paths = new List<string>(end - start + 1);
        for (int i = start; i <= end; i++)
        {
            string? path = TryGetMarkableBrowserPathByIndex(i);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            paths.Add(path);
        }

        if (_markedFiles.AddRange(paths) > 0)
        {
            CommitMarkStateChange();
            RefreshMarkUi();
            PrimeRecentMultiMarkIntent();
        }
    }
    private string? TryGetMarkableBrowserPathByIndex(int index)
    {
        if (index < 0 || index >= fileListView.Items.Count)
        {
            return null;
        }

        var item = fileListView.Items[index];
        if (item.Text == "..")
        {
            return null;
        }

        return item.Tag as string;
    }
    private void BrowserPanel_MouseWheel(object? sender, MouseEventArgs e)
    {
        HideBrowserFileNameToolTip();
        if (_uiMode != UIMode.Browser || fileListView.Items.Count == 0) return;
        int itemsPerPage = GetBrowserItemsPerPage();
        if (itemsPerPage <= 0) return;
        int totalItems = _browserTotalItemCount > 0 ? _browserTotalItemCount : fileListView.Items.Count;
        int currentPage = _browserCursorIndex / itemsPerPage;
        int offsetInPage = _browserCursorIndex % itemsPerPage;
        int totalPages = (totalItems + itemsPerPage - 1) / itemsPerPage;
        if (e.Delta > 0) // 上ホイール: 前ページへ
        {
            if (currentPage <= 0) return; // 境界 no-op
            int targetPage = currentPage - 1;
            int targetIndex = targetPage * itemsPerPage + offsetInPage;
            SetBrowserGlobalCursorIndex(Math.Min(totalItems - 1, targetIndex));
        }
        else if (e.Delta < 0) // 下ホイール: 次ページへ
        {
            if (currentPage >= totalPages - 1) return; // 境界 no-op
            int targetPage = currentPage + 1;
            int targetIndex = targetPage * itemsPerPage + offsetInPage;
            SetBrowserGlobalCursorIndex(Math.Min(totalItems - 1, targetIndex));
        }
    }
    // ─── Phase 3-fix2a: 外部 → MidFD Drag-in ───
    private static bool HasInternalDragArchiveMarker(IDataObject? data)
    {
        if (data == null)
        {
            return false;
        }

        if (!data.GetDataPresent(InternalDragArchiveFormat, false))
        {
            return false;
        }

        object? marker = data.GetData(InternalDragArchiveFormat, false);
        return marker is string markerText
            ? string.Equals(markerText, InternalDragArchiveMarkerValue, StringComparison.Ordinal)
            : marker is bool markerFlag && markerFlag;
    }

    private static int GetFileDropCount(IDataObject? data)
    {
        if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
        {
            return 0;
        }

        return data.GetData(DataFormats.FileDrop) is string[] files ? files.Length : 0;
    }

    private void BrowserPanel_DragEnter(object? sender, DragEventArgs e)
    {
        HandleBrowserPanelDragEnterOrOver(e, "DragEnter");
    }

    private void BrowserPanel_DragOver(object? sender, DragEventArgs e)
    {
        HandleBrowserPanelDragEnterOrOver(e, "DragOver");
    }

    private void BrowserPanel_DragLeave(object? sender, EventArgs e)
    {
        HandleBrowserPanelDragLeave();
    }
    private void BrowserPanel_DragDrop(object? sender, DragEventArgs e)
    {
        if (_uiMode != UIMode.Browser)
        {
            LogService.Info(DragDropDataObjectDiagnosticHelper.GetDiagnosticLog("DragDrop", _uiMode.ToString(), IsActiveBrowserTabReadOnly(), _isClipboardBusy, false, e.Data, e.Effect, "uiModeNotBrowser"));
            return;
        }
        if (IsActiveBrowserTabReadOnly())
        {
            LogService.Info(DragDropDataObjectDiagnosticHelper.GetDiagnosticLog("DragDrop", _uiMode.ToString(), IsActiveBrowserTabReadOnly(), _isClipboardBusy, false, e.Data, e.Effect, "readOnlyBlocked"));
        }
        if (GuardReadOnlyBrowserTab("ファイル取り込み")) return;
        bool hasInternalDragArchiveMarker = HasInternalDragArchiveMarker(e.Data);
        int fileDropCount = GetFileDropCount(e.Data);
        LogService.Info($"[DragArchive] DragDrop: internalMarkerPresent={hasInternalDragArchiveMarker}, fileDropCount={fileDropCount}, clipboardBusy={_isClipboardBusy}");
        LogService.Info(DragDropDataObjectDiagnosticHelper.GetDiagnosticLog("DragDrop", _uiMode.ToString(), IsActiveBrowserTabReadOnly(), _isClipboardBusy, hasInternalDragArchiveMarker, e.Data, e.Effect, "dropReceived"));
        if (hasInternalDragArchiveMarker)
        {
            return;
        }
        if (_isClipboardBusy)
        {
            ShowStatusMessage("処理中のため画像取り込みできません。");
            return;
        }
        if (string.IsNullOrEmpty(_navigationService.CurrentPath)) return;

        // Systematically classify and resolve intent using resolver, respecting remembered decision
        var decision = ResolveIncomingDropDecision(e);
        if (decision.Intent == BrowserDragDropIntent.None)
        {
            ShowStatusMessage("ドロップ不可な操作または状態です。");
            return;
        }

        if (TryHandleBrowserFileDrop(e, decision))
        {
            return;
        }

        if (OutlookAttachmentDropService.IsOutlookAttachmentDrop(e.Data))
        {
            var attachmentNames = OutlookAttachmentDropService.GetAttachmentNames(e.Data!);
            if (attachmentNames.Count > 0)
            {
                // Prompts are resolved here for right drag-in. Outlook attachments are copy-only.
                BrowserDropAction action = BrowserDropAction.Copy;
                if (decision.Intent == BrowserDragDropIntent.Prompt)
                {
                    action = ResolveBrowserDropAction(e, decision);
                    if (action == BrowserDropAction.Cancel)
                    {
                        ShowStatusMessage("ドロップ操作はキャンセルされました。");
                        return;
                    }
                }

                string msg = $"{attachmentNames.Count} 件の添付ファイルを現在のディレクトリにコピーしますか？\n宛先: {_navigationService.CurrentPath}";
                var result = ShowDragInCopyConfirmationDialog(msg);
                if (result != DialogResult.Yes)
                {
                    ShowStatusMessage("コピーはキャンセルされました。");
                    return;
                }

                Func<string, OverwriteConfirmResult> confirmOverwrite = (fileName) =>
                {
                    var overwriteMsg = FileOperationPresentationHelper.GetOverwriteConfirmationMessage(fileName);
                    var overwriteResult = MessageBox.Show(overwriteMsg, "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (overwriteResult == DialogResult.Yes) return OverwriteConfirmResult.Yes;
                    if (overwriteResult == DialogResult.No) return OverwriteConfirmResult.No;
                    return OverwriteConfirmResult.Cancel;
                };

                bool dropSuccess = OutlookAttachmentDropService.ProcessDrop(e.Data!, _navigationService.CurrentPath, confirmOverwrite);
                if (dropSuccess)
                {
                    ShowStatusMessage("仮想ファイルのコピーが完了しました。");
                    string? focusTarget = attachmentNames.Count > 0 ? attachmentNames[0] : null;
                    LoadDirectory(_navigationService.CurrentPath, focusTarget);
                }
            }
            return;
        }

        if (BrowserImageDropService.TryGetImage(e.Data, out var image) && image != null)
        {
            try
            {
                using (image)
                {
                    string savedPath = BrowserImageDropService.SavePngToDirectory(image, _navigationService.CurrentPath);
                    string fileName = Path.GetFileName(savedPath);
                    LoadDirectory(_navigationService.CurrentPath, GetCreatedItemFocusTarget(fileName));
                    LogBrowserImageImportInfo($"Source=BrowserDragImage Saved={savedPath}");
                    ShowStatusMessage($"画像を PNG として取り込みました: {fileName}");
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Browser 画像ドロップ取り込みに失敗しました", ex);
                ShowStatusMessage($"画像ドロップ取り込み失敗: {ex.Message}");
            }
            return;
        }

        if (BrowserDropUrlResolverService.TryResolveImageUrl(e.Data, out Uri? imageUrl, out string? suggestedFileName)
            && imageUrl is Uri resolvedImageUrl)
        {
            try
            {
                string savedPath = BrowserDroppedImageDownloadService.DownloadToDirectory(resolvedImageUrl, _navigationService.CurrentPath, suggestedFileName);
                string fileName = Path.GetFileName(savedPath);
                LoadDirectory(_navigationService.CurrentPath, GetCreatedItemFocusTarget(fileName));
                LogBrowserImageImportInfo($"Source=BrowserDropUrl Url={resolvedImageUrl} Saved={savedPath}");
                ShowStatusMessage($"画像URLを保存しました: {fileName}");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden
                || ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                LogBrowserImageImportWarn($"Source=BrowserDropUrlUnauthorized Url={resolvedImageUrl}");
                ShowStatusMessage("画像URL取り込み失敗: この画像は認証付きのため現方式では保存できません。");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("画像レスポンスではありません", StringComparison.Ordinal))
            {
                LogBrowserImageImportWarn($"Source=BrowserDropUrlNonImage Url={resolvedImageUrl} Detail={ex.Message}");
                ShowStatusMessage("画像URL取り込み失敗: 画像レスポンスではありません。");
            }
            catch (Exception ex)
            {
                LogService.Error($"Browser URLドロップ画像取り込み失敗: {resolvedImageUrl}", ex);
                ShowStatusMessage($"画像URL取り込み失敗: {ex.Message}");
            }
            return;
        }

        if (BrowserImageDropService.HasImageData(e.Data))
        {
            LogBrowserImageImportWarn($"Source=BrowserDragUnsupportedImage Data={BrowserImageDropService.DescribeDataObject(e.Data)}");
            ShowStatusMessage("画像ドロップ取り込み失敗: このブラウザの画像ドロップ形式には未対応です。");
            return;
        }

        if (BrowserDropUrlResolverService.HasPotentialUrlData(e.Data))
        {
            LogBrowserImageImportWarn($"Source=BrowserDropUrlUnresolved Data={BrowserImageDropService.DescribeDataObject(e.Data)}");
            ShowStatusMessage("画像ドロップ取り込み失敗: 画像URLを特定できませんでした。");
        }
        RefreshBrowserStatusSummary();
    }
    // ─── Phase 3-fix2b: MidFD → 外部 Drag-out (Copy限定) ───
    private void InitializeHeaderGestureInteraction()
    {
        Control[] controls =
        {
            titleHeaderPanel, lblTitle, topPanel, infoRow2Panel, infoRow4Panel,
            lblPath, lblSort, lblName, lblFileStatsEx, headerPanel,
            headerZone1, headerZone2, headerZone3, headerZone4,
            lblPage, lblTotal, lblUsed, lblFree
        };
        foreach (Control control in controls)
        {
            WireHeaderGestureControl(control);
        }
        if (_breadcrumbPathControl != null)
        {
            WireHeaderGestureControl(_breadcrumbPathControl);
        }
    }

    private void WireHeaderGestureControl(Control control)
    {
        if (!_headerGestureControls.Add(control))
        {
            return;
        }
        control.MouseDown += HeaderGesture_MouseDown;
        control.MouseMove += HeaderGesture_MouseMove;
        control.MouseUp += HeaderGesture_MouseUp;
        control.MouseLeave += HeaderGesture_MouseLeave;
        control.MouseCaptureChanged += HeaderGesture_MouseCaptureChanged;
        control.Paint += HeaderGestureSurface_Paint;
    }

    private void HeaderGesture_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser || e.Button != MouseButtons.Right || sender is not Control control)
        {
            return;
        }

        _browserRightStartPoint = ToMainFormClient(control, e.Location);
        _browserRightInteractionState = BrowserRightInteractionState.HeaderRightPending;
        _browserRightCaptureControl = control;
    }

    private void HeaderGesture_MouseMove(object? sender, MouseEventArgs e)
    {
        if (sender is not Control control || e.Button != MouseButtons.Right)
        {
            return;
        }

        Point formPoint = ToMainFormClient(control, e.Location);
        if (_browserRightInteractionState == BrowserRightInteractionState.HeaderRightPending)
        {
            if (HasExceededBrowserDragThreshold(formPoint))
            {
                BeginBrowserGestureTracking(formPoint, control);
            }
            else
            {
                return;
            }
        }

        if (_browserRightInteractionState == BrowserRightInteractionState.GestureTracking)
        {
            _mouseGestureRecognizer.Update(formPoint);
            AppendMouseGestureTrailPoint(formPoint);
            InvalidateMouseGestureSurfaces();
            ShowMouseGestureInputStatus(_mouseGestureRecognizer.GestureText);
        }
    }

    private void HeaderGesture_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || sender is not Control control)
        {
            return;
        }

        if (_browserRightInteractionState == BrowserRightInteractionState.GestureTracking)
        {
            string gesture = _mouseGestureRecognizer.End(ToMainFormClient(control, e.Location));
            ClearMouseGestureTrail();
            if (!string.IsNullOrEmpty(gesture))
            {
                TryExecuteBrowserMouseGesture(gesture);
            }
        }

        if (_browserRightInteractionState == BrowserRightInteractionState.HeaderRightPending
            || _browserRightInteractionState == BrowserRightInteractionState.GestureTracking)
        {
            CleanupBrowserRightInteraction();
        }
    }

    private void HeaderGesture_MouseLeave(object? sender, EventArgs e)
    {
        if (_browserRightInteractionState == BrowserRightInteractionState.GestureTracking
            && _browserRightCaptureControl?.Capture != true)
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
        }
    }

    private void HeaderGesture_MouseCaptureChanged(object? sender, EventArgs e)
    {
        if (_browserRightInteractionState == BrowserRightInteractionState.GestureTracking
            && _browserRightCaptureControl?.Capture != true)
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
        }
    }

    private void HeaderGestureSurface_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is Control control)
        {
            DrawMouseGestureTrail(e.Graphics, control);
        }
    }

    private void BrowserPanel_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        if (e.Button == MouseButtons.Right)
        {
            _browserRightStartPoint = ToMainFormClient(browserPanel, e.Location);
            _browserRightItemIndex = -1;
            _browserRightItemPath = null;
            _browserRightSelectionSnapshot = _markedFiles.Snapshot();
        }
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
        // ドラッグ開始の「候補」座標とインデックスを保持
        _dragStartPoint = e.Location;
        _dragCandidateIndex = CalculateBrowserIndexFromPoint(e.X, e.Y);
        if (e.Button == MouseButtons.Left)
        {
            bool itemHit = _dragCandidateIndex >= 0
                && TryGetBrowserItemLayoutBounds(_dragCandidateIndex, out Rectangle itemHoverBounds, out _)
                && itemHoverBounds.Contains(e.Location);
            if (!itemHit)
            {
                _dragCandidateIndex = -1;
            }
            _blankDragCandidate = !itemHit;
        }
        else
        {
            bool itemHit = _dragCandidateIndex >= 0
                && TryGetBrowserItemLayoutBounds(_dragCandidateIndex, out Rectangle rightItemBounds, out _)
                && rightItemBounds.Contains(e.Location);
            _blankDragCandidate = !itemHit;
            if (itemHit)
            {
                _browserRightInteractionState = BrowserRightInteractionState.ItemRightPending;
                _browserRightItemIndex = _dragCandidateIndex;
                int localIndex = _dragCandidateIndex - _browserPageStartIndex;
                _browserRightItemPath = localIndex >= 0 && localIndex < fileListView.Items.Count
                    ? fileListView.Items[localIndex].Tag as string
                    : null;
            }
            else
            {
                _browserRightInteractionState = BrowserRightInteractionState.BlankRightPending;
            }
        }
        // MouseDown時点での修飾キー状態（Shift/Ctrl）をキャプチャ
        var mods = Control.ModifierKeys;
        _dragArchiveHandoffRequested = (mods & (Keys.Shift | Keys.Control)) != 0;
        int dragCandidateLocalIndex = _dragCandidateIndex - _browserPageStartIndex;
        if (dragCandidateLocalIndex >= 0 && dragCandidateLocalIndex < fileListView.Items.Count && _browserCursorIndex != _dragCandidateIndex)
        {
            InvalidateRecentMultiMarkIntent();
            _browserCursorIndex = _dragCandidateIndex;
            SyncBrowserSelection();
        }
    }
    private void BrowserTabStrip_TabReordered(object? sender, BrowserTabStripReorderEventArgs e)
    {
        if (e.FromIndex < 0 || e.FromIndex >= _browserTabViewState.Count || e.ToIndex < 0 || e.ToIndex >= _browserTabViewState.Count || e.FromIndex == e.ToIndex)
        {
            return;
        }
        CaptureActiveBrowserTabState();
        BrowserTabState movedTab = _browserTabViewState.Tabs[e.FromIndex];
        BrowserTabState? activeTab = _browserTabViewState.ActiveTabIndex >= 0 && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count
            ? _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex]
            : null;
        BrowserTabState? contextTab = _browserTabViewState.ContextTabIndex >= 0 && _browserTabViewState.ContextTabIndex < _browserTabViewState.Count
            ? _browserTabViewState.Tabs[_browserTabViewState.ContextTabIndex]
            : null;
        _browserTabViewState.RemoveAt(e.FromIndex);
        _browserTabViewState.Insert(e.ToIndex, movedTab);
        if (activeTab != null)
        {
            _browserTabViewState.ActiveTabIndex = _browserTabViewState.IndexOf(activeTab);
        }
        else
        {
            _browserTabViewState.ActiveTabIndex = Math.Clamp(e.ToIndex, 0, _browserTabViewState.Count - 1);
        }
        if (contextTab != null)
        {
            _browserTabViewState.ContextTabIndex = _browserTabViewState.IndexOf(contextTab);
        }
        RefreshBrowserTabHeaders();
        browserPanel.Focus();
        ShowStatusMessage("タブ順を入れ替えました。");
    }
    private void BrowserTabStrip_CategoryReordered(object? sender, BrowserTabStripReorderEventArgs e)
    {
        if (e.FromIndex < 0 || e.FromIndex >= _categoryViewState.Count || e.ToIndex < 0 || e.ToIndex >= _categoryViewState.Count || e.FromIndex == e.ToIndex)
        {
            return;
        }

        BrowserTabCategoryDefinition moved = _categoryViewState.Categories[e.FromIndex];
        _categoryViewState.RemoveAt(e.FromIndex);
        _categoryViewState.Insert(e.ToIndex, moved);

        SyncBrowserTabCategoryDefinitionsToSettings();

        var sessionCategories = _settings.Session.BrowserTabCategories;
        if (sessionCategories != null)
        {
            BrowserTabCategorySessionState? movedSession = sessionCategories.FirstOrDefault(c => c.CategoryId == moved.Id);
            if (movedSession != null)
            {
                int sFrom = sessionCategories.IndexOf(movedSession);
                if (sFrom >= 0)
                {
                    sessionCategories.RemoveAt(sFrom);
                    int targetSIndex = Math.Min(e.ToIndex, sessionCategories.Count);
                    sessionCategories.Insert(targetSIndex, movedSession);
                }
            }
        }

        var snapshot = _settings.Session.BrowserTabRestoreSnapshot;
        if (snapshot != null && snapshot.Categories != null)
        {
            var matchedCat = snapshot.Categories.FirstOrDefault(c => c.Id == moved.Id);
            if (matchedCat != null)
            {
                int snapFrom = snapshot.Categories.IndexOf(matchedCat);
                if (snapFrom >= 0)
                {
                    snapshot.Categories.RemoveAt(snapFrom);
                    int targetSnapIndex = Math.Min(e.ToIndex, snapshot.Categories.Count);
                    snapshot.Categories.Insert(targetSnapIndex, matchedCat);
                }
            }
        }

        _lastBrowserTabHeaderSnapshotKey = null;
        SettingsManager.Save(_settings);
        RefreshBrowserTabHeaders();
        ShowStatusMessage("カテゴリ順を入れ替えました。");
    }
    private void BrowserTabStrip_TabListDropDownOpening(object? sender, Point e)
    {
        ContextMenuStrip menu = new();

        var categoryDefs = _settings.BrowserTabs.Categories ?? new List<BrowserTabCategoryDefinition>();
        var activeCategoryId = _categoryViewState.ActiveCategoryId;

        foreach (var category in categoryDefs)
        {
            ToolStripMenuItem categoryMenuItem = new ToolStripMenuItem(string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName);

            List<BrowserTabState> tabsOfCategory = new();
            int selectedTabIndex = -1;

            if (category.Id == activeCategoryId)
            {
                tabsOfCategory = _browserTabViewState.Tabs.ToList();
                selectedTabIndex = _browserTabViewState.ActiveTabIndex;
                categoryMenuItem.Checked = true;
            }
            else
            {
                var categorySession = _settings.Session.BrowserTabCategories?.FirstOrDefault(c => c.CategoryId == category.Id);
                if (categorySession != null)
                {
                    tabsOfCategory = categorySession.OpenTabs.Select(tabState => new BrowserTabState
                    {
                        Id = tabState.TabId,
                        Title = GetBrowserTabTitle(tabState.CurrentPath),
                        CurrentPath = tabState.CurrentPath,
                        IsLocked = tabState.IsLocked,
                        StartupPath = tabState.StartupPath,
                        IsReadOnly = tabState.IsReadOnly,
                        FilterLock = tabState.FilterLock?.Clone() ?? new TabFilterLockState(),
                        MarkedPaths = tabState.MarkedPaths ?? new List<string>(),
                        Navigation = new NavigationService.NavigationSnapshot
                        {
                            BackHistory = tabState.BackHistory ?? new List<string>(),
                            ForwardHistory = tabState.ForwardHistory ?? new List<string>(),
                            LastVisitedPathByDrive = (tabState.LastVisitedPathByDrive ?? new Dictionary<string, string>())
                                .Where(kvp => !string.IsNullOrEmpty(kvp.Key))
                                .ToDictionary(kvp => kvp.Key[0], kvp => kvp.Value)
                        },
                        FocusTargetName = tabState.FocusTargetName,
                        CursorIndex = tabState.CursorIndex,
                        ColumnCount = tabState.ColumnCount,
                        SortKind = tabState.SortKind,
                        SortAscending = tabState.SortAscending
                    }).ToList();
                    selectedTabIndex = categorySession.ActiveTabIndex;
                }
            }

            if (tabsOfCategory.Count > 0)
            {
                for (int i = 0; i < tabsOfCategory.Count; i++)
                {
                    int tabIndex = i;
                    string catId = category.Id;
                    var tab = tabsOfCategory[i];
                    string tabTitle = string.IsNullOrWhiteSpace(tab.Title) ? "新しいタブ" : tab.Title;
                    if (tab.IsLocked)
                    {
                        tabTitle = "■ " + tabTitle;
                    }
                    if (tab.IsReadOnly)
                    {
                        tabTitle = "[RO] " + tabTitle;
                    }

                    ToolStripMenuItem tabMenuItem = new ToolStripMenuItem($"{i + 1}: {tabTitle}")
                    {
                        Checked = (category.Id == activeCategoryId && i == selectedTabIndex),
                        ToolTipText = tab.CurrentPath
                    };

                    tabMenuItem.Click += (_, _) =>
                    {
                        SwitchBrowserTabCategory(catId);
                        SwitchBrowserTab(tabIndex);
                    };

                    categoryMenuItem.DropDownItems.Add(tabMenuItem);
                }
            }
            else
            {
                ToolStripMenuItem emptyItem = new ToolStripMenuItem("(空のカテゴリ)") { Enabled = false };
                categoryMenuItem.DropDownItems.Add(emptyItem);
            }

            categoryMenuItem.Click += (_, _) =>
            {
                SwitchBrowserTabCategory(category.Id);
            };

            menu.Items.Add(categoryMenuItem);
        }

        if (_browserTabStrip != null)
        {
            menu.Show(_browserTabStrip, e);
        }
    }
    private void BrowserPanel_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        if (e.Button == MouseButtons.None)
        {
            UpdateBrowserFileNameToolTip(e.Location);
        }
        else
        {
            HideBrowserFileNameToolTip();
        }
        if ((e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
            || _dragStartPoint == Point.Empty
            || (_dragCandidateIndex == -1 && !_blankDragCandidate)) return;
        if (e.Button == MouseButtons.Right && _browserRightInteractionState == BrowserRightInteractionState.BlankRightPending)
        {
            if (HasExceededBrowserDragThreshold(ToMainFormClient(browserPanel, e.Location)))
            {
                BeginBrowserGestureTracking(ToMainFormClient(browserPanel, e.Location), browserPanel);
            }
            else
            {
                return;
            }
        }
        if (e.Button == MouseButtons.Right && _browserRightInteractionState == BrowserRightInteractionState.GestureTracking)
        {
            Point formPoint = ToMainFormClient(browserPanel, e.Location);
            _mouseGestureRecognizer.Update(formPoint);
            AppendMouseGestureTrailPoint(formPoint);
            browserPanel.Invalidate();
            ShowMouseGestureInputStatus(_mouseGestureRecognizer.GestureText);
            return;
        }
        if (e.Button == MouseButtons.Right && _browserRightInteractionState == BrowserRightInteractionState.ItemRightPending
            && HasExceededBrowserDragThreshold(ToMainFormClient(browserPanel, e.Location)))
        {
            _browserRightInteractionState = BrowserRightInteractionState.FileDragTracking;
            SuppressNextBrowserContextMenu();
        }
        // OS標準のドラッグ開始しきい値判定 (SystemInformation.DragSize)
        bool exceeded = Math.Abs(e.X - _dragStartPoint.X) > SystemInformation.DragSize.Width ||
                        Math.Abs(e.Y - _dragStartPoint.Y) > SystemInformation.DragSize.Height;
        if (exceeded)
        {
            if (e.Button == MouseButtons.Right && _browserRightInteractionState != BrowserRightInteractionState.FileDragTracking)
            {
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                _browserRightInteractionState = BrowserRightInteractionState.FileDragTracking;
            }
            // ドラッグ対象の確定
            List<string> dragPaths = new List<string>();
            List<string> archiveDragPaths = new List<string>();
            IReadOnlyList<string> dragSelection = e.Button == MouseButtons.Right
                ? _browserRightSelectionSnapshot
                : _markedFiles.Snapshot();
            bool isBlankDrag = _blankDragCandidate && _dragCandidateIndex == -1;
            int dragCandidateLocalIndex = _dragCandidateIndex - _browserPageStartIndex;
            string? dragCandidatePath = (dragCandidateLocalIndex >= 0 && dragCandidateLocalIndex < fileListView.Items.Count)
                ? fileListView.Items[dragCandidateLocalIndex].Tag as string
                : null;
            if (e.Button == MouseButtons.Right && !string.IsNullOrWhiteSpace(_browserRightItemPath))
            {
                dragCandidatePath = _browserRightItemPath;
            }
            if (dragSelection.Count == 0 && dragCandidateLocalIndex >= 0 && dragCandidateLocalIndex < fileListView.Items.Count)
            {
                if (_browserCursorIndex != _dragCandidateIndex)
                {
                    _browserCursorIndex = _dragCandidateIndex;
                    SyncBrowserSelection();
                }
                var item = fileListView.Items[dragCandidateLocalIndex];
                string name = item.Text;
                string? fullPath = item.Tag as string;
                // 親ディレクトリ(..)や無効なパスは除外
                if (name != ".." && !string.IsNullOrEmpty(fullPath))
                {
                    if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    {
                        dragCandidatePath = fullPath;
                    }
                }
            }
            // 通常 FileDrop は既存の対象選択契約を維持する。
            if (!string.IsNullOrWhiteSpace(dragCandidatePath)
                && dragSelection.Count > 1
                && !dragSelection.Contains(dragCandidatePath))
            {
                dragPaths.Add(dragCandidatePath);
            }
            else if (dragSelection.Count > 0)
            {
                dragPaths.AddRange(dragSelection.Where(path => File.Exists(path) || Directory.Exists(path)));
            }
            else if (!string.IsNullOrWhiteSpace(dragCandidatePath))
            {
                dragPaths.Add(dragCandidatePath);
            }
            bool isShiftOrCtrl = _dragArchiveHandoffRequested || (Control.ModifierKeys & (Keys.Shift | Keys.Control)) != 0;
            if (isShiftOrCtrl)
            {
                archiveDragPaths.AddRange(DragTargetResolver.Resolve(dragSelection, dragCandidatePath));
            }
                if (isBlankDrag && (dragSelection.Count == 0 || !isShiftOrCtrl))
                {
                    _dragStartPoint = Point.Empty;
                    _dragCandidateIndex = -1;
                    _blankDragCandidate = false;
                    _dragArchiveHandoffRequested = false;
                    return;
                }
                if (dragPaths.Count > 0 || (isBlankDrag && archiveDragPaths.Count > 0))
                {
                    // Phase 3-keybind-cleanup1.3: Clipboard処理中は開始しない
                if (_isClipboardBusy)
                {
                    if (isBlankDrag)
                    {
                        _dragStartPoint = Point.Empty;
                        _dragCandidateIndex = -1;
                        _blankDragCandidate = false;
                        _dragArchiveHandoffRequested = false;
                    }
                    return;
                }

                var fileOperations = _settings.FileOperations;
                bool isDragArchiveEnabled = fileOperations.EnableDragArchiveHandoff;
                if (isBlankDrag && !isDragArchiveEnabled)
                {
                    _dragStartPoint = Point.Empty;
                    _dragCandidateIndex = -1;
                    _blankDragCandidate = false;
                    _dragArchiveHandoffRequested = false;
                    return;
                }
                string logMsg = $"[DragArchive] dragPaths.Count={dragPaths.Count}, _markedFiles.Count={_markedFiles.Count}, enableDragArchiveHandoff={isDragArchiveEnabled}, includeManifest={fileOperations.IncludeDragZipManifest}, mouseDownModifier={_dragArchiveHandoffRequested}, currentModifier={((Control.ModifierKeys & (Keys.Shift | Keys.Control)) != 0)}, archiveDragRequested={isShiftOrCtrl}, candidatePath='{dragCandidatePath}'";
                LogService.Info(logMsg);

                if (isDragArchiveEnabled && isShiftOrCtrl && archiveDragPaths.Count > 0)
                {
                    string? zipPath = null;
                    string? archiveBaseDirectory = null;
                    var originalCursor = browserPanel.Cursor;
                    try
                    {
                        browserPanel.Cursor = Cursors.WaitCursor;
                        ShowStatusMessage("ドラッグ用ZIPを作成中...");
                        Application.DoEvents(); // UI描画の更新

                        string tempDir = DragArchiveService.GetDragArchiveTempDirectory();
                        DragArchiveService.CleanupDragArchivesBeforeCreation(tempDir);
                        DragArchiveService.DragArchiveInfo archiveInfo = DragArchiveService.GetOrCreateInfoZip(
                            tempDir,
                            archiveDragPaths,
                            fileOperations.IncludeDragZipManifest);
                        zipPath = archiveInfo.ArchivePath;
                        archiveBaseDirectory = archiveInfo.BaseDirectory;

                        long archiveSizeBytes = new FileInfo(zipPath).Length;
                        ShowStatusMessage($"ドラッグ用ZIPを作成しました。{archiveInfo.ItemCount}件 / {FileOperationService.FormatSize(archiveSizeBytes)}");
                        var data = new DataObject();
                        data.SetData(DataFormats.FileDrop, new string[] { zipPath });
                        data.SetData(InternalDragArchiveFormat, false, InternalDragArchiveMarkerValue);

                        bool isRightDrag = (e.Button == MouseButtons.Right);
                        var decision = BrowserOutgoingDragResolver.Resolve(isRightDrag, Control.ModifierKeys, isDragArchive: true);
                        if (decision.HasPreferredEffect)
                        {
                            var preferredEffect = (int)decision.PreferredEffect;
                            var preferredBytes = BitConverter.GetBytes(preferredEffect);
                            var preferredStream = new MemoryStream(preferredBytes);
                            data.SetData("Preferred DropEffect", preferredStream);
                        }

                        // ログにドラッグ準備情報を出力 (キー状態も追跡)
                        bool zipExists = File.Exists(zipPath);
                        string formatsStr = string.Join(", ", data.GetFormats());
                        var startMods = Control.ModifierKeys;
                        LogService.Info($"[DragArchive] Sending drag: baseDirectory='{archiveBaseDirectory}', archivePath='{zipPath}', fileDropCount=1, internalMarkerPresent={HasInternalDragArchiveMarker(data)}, exists={zipExists}, formats=[{formatsStr}], modifierKeys={startMods}, allowedEffects={decision.AllowedEffects}");

                        // Copy|Move でネゴシエーションを開始
                        RefreshBrowserStatusSummary(decision.StatusText);
                        var resultEffect = browserPanel.DoDragDrop(data, decision.AllowedEffects);
                        LogService.Info($"[DragArchive] Drag completed: resultEffect={resultEffect}");
                    }
                    catch (Exception ex)
                    {
                        LogService.Error("ドラッグ用ZIP作成失敗", ex);
                        MessageBox.Show(this, $"ドラッグ用ZIPの作成に失敗しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        ShowStatusMessage("ドラッグ用ZIPの作成に失敗しました。");
                    }
                    finally
                    {
                        browserPanel.Cursor = originalCursor;
                    }
                }
                else
                {
                    // ドラッグ開始
                    var data = new DataObject(DataFormats.FileDrop, dragPaths.ToArray());
                    LogService.Info($"[DragArchive] Sending normal FileDrop: fileDropCount={dragPaths.Count}, internalMarkerPresent={HasInternalDragArchiveMarker(data)}");
                    bool isRightDrag = (e.Button == MouseButtons.Right);
                    var decision = BrowserOutgoingDragResolver.Resolve(isRightDrag, Control.ModifierKeys);
                    if (decision.HasPreferredEffect)
                    {
                        var preferredEffect = (int)decision.PreferredEffect;
                        var preferredBytes = BitConverter.GetBytes(preferredEffect);
                        var preferredStream = new MemoryStream(preferredBytes);
                        data.SetData("Preferred DropEffect", preferredStream);
                    }

                    RefreshBrowserStatusSummary(decision.StatusText);
                    browserPanel.DoDragDrop(data, decision.AllowedEffects);
                }
            }
            // 開始した（または条件に合わず開始できなかった）ので状態をクリア
            if (e.Button == MouseButtons.Right)
            {
                CleanupBrowserRightInteraction();
            }
            else
            {
                _dragStartPoint = Point.Empty;
                _dragCandidateIndex = -1;
                _blankDragCandidate = false;
                _dragArchiveHandoffRequested = false;
            }
        }
    }
    private bool HasExceededBrowserDragThreshold(Point point)
    {
        return Math.Abs(point.X - _browserRightStartPoint.X) > SystemInformation.DragSize.Width
            || Math.Abs(point.Y - _browserRightStartPoint.Y) > SystemInformation.DragSize.Height;
    }

    private Point ToMainFormClient(Control control, Point point)
    {
        return PointToClient(control.PointToScreen(point));
    }

    private void BeginBrowserGestureTracking(Point point, Control captureControl)
    {
        bool isHeaderGesture = _browserRightInteractionState == BrowserRightInteractionState.HeaderRightPending;
        _browserRightInteractionState = BrowserRightInteractionState.GestureTracking;
        _mouseGestureRecognizer.Begin(_browserRightStartPoint);
        _mouseGestureTrailPoints.Clear();
        _mouseGestureTrailPoints.Add(_browserRightStartPoint);
        _isMouseGestureTrailVisible = true;
        _browserRightCaptureControl = captureControl;
        captureControl.Capture = true;
        if (isHeaderGesture)
        {
            SuppressNextHeaderContextMenu();
        }
        else
        {
            SuppressNextBrowserContextMenu();
        }
        _mouseGestureRecognizer.Update(point);
        AppendMouseGestureTrailPoint(point);
        InvalidateMouseGestureSurfaces();
    }

    private void CleanupBrowserRightInteraction(bool clearContextMenuSuppression = false)
    {
        _mouseGestureRecognizer.Cancel();
        ClearMouseGestureTrail();
        _browserRightInteractionState = BrowserRightInteractionState.Idle;
        if (_browserRightCaptureControl?.Capture == true)
        {
            _browserRightCaptureControl.Capture = false;
        }
        _browserRightCaptureControl = null;
        _browserRightStartPoint = Point.Empty;
        _browserRightItemIndex = -1;
        _browserRightItemPath = null;
        _browserRightSelectionSnapshot = Array.Empty<string>();
        _dragStartPoint = Point.Empty;
        _dragCandidateIndex = -1;
        _blankDragCandidate = false;
        _dragArchiveHandoffRequested = false;
        if (clearContextMenuSuppression)
        {
            _suppressNextBrowserContextMenu = false;
            _suppressBrowserContextMenuUntilUtc = DateTime.MinValue;
            _suppressNextHeaderContextMenu = false;
            _suppressHeaderContextMenuUntilUtc = DateTime.MinValue;
        }
        InvalidateMouseGestureSurfaces();
    }

    private void BrowserPanel_MouseLeave(object? sender, EventArgs e)
    {
        HideBrowserFileNameToolTip();
        if (_browserRightInteractionState == BrowserRightInteractionState.GestureTracking && _browserRightCaptureControl?.Capture != true)
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
        }
    }
    private void BrowserPanel_CaptureChanged(object? sender, EventArgs e)
    {
        if (_browserRightCaptureControl?.Capture != true && _browserRightInteractionState == BrowserRightInteractionState.GestureTracking)
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
        }
    }
    private void InvalidateMouseGestureSurfaces()
    {
        browserPanel.Invalidate();
        foreach (Control control in _headerGestureControls)
        {
            control.Invalidate();
        }
    }
    private void BrowserPanel_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.XButton1)
        {
            ExecuteCommandFromUi(CommandIds.BrowserNavigateBack, CommandScope.Browser, "Mouse.XButton1");
            return;
        }
        if (e.Button == MouseButtons.XButton2)
        {
            ExecuteCommandFromUi(CommandIds.BrowserNavigateForward, CommandScope.Browser, "Mouse.XButton2");
            return;
        }
        if (e.Button == MouseButtons.Right && _browserRightInteractionState == BrowserRightInteractionState.GestureTracking)
        {
            string gesture = _mouseGestureRecognizer.End(ToMainFormClient(browserPanel, e.Location));
            ClearMouseGestureTrail();
            if (!string.IsNullOrEmpty(gesture))
            {
                TryExecuteBrowserMouseGesture(gesture);
            }
        }
        if (e.Button == MouseButtons.Right)
        {
            CleanupBrowserRightInteraction();
        }
    }
    private void AppendMouseGestureTrailPoint(Point point)
    {
        if (!_isMouseGestureTrailVisible)
        {
            return;
        }

        if (_mouseGestureTrailPoints.Count == 0)
        {
            _mouseGestureTrailPoints.Add(point);
            return;
        }

        Point last = _mouseGestureTrailPoints[^1];
        int dx = point.X - last.X;
        int dy = point.Y - last.Y;
        if ((dx * dx) + (dy * dy) >= MouseGestureTrailMinDistance * MouseGestureTrailMinDistance)
        {
            _mouseGestureTrailPoints.Add(point);
        }
    }
    private void ClearMouseGestureTrail()
    {
        if (!_isMouseGestureTrailVisible && _mouseGestureTrailPoints.Count == 0)
        {
            return;
        }

        _isMouseGestureTrailVisible = false;
        _mouseGestureTrailPoints.Clear();
        InvalidateMouseGestureSurfaces();
    }
    private void SuppressNextBrowserContextMenu()
    {
        _suppressNextBrowserContextMenu = true;
        _suppressBrowserContextMenuUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
    }
    private void SuppressNextHeaderContextMenu()
    {
        _suppressNextHeaderContextMenu = true;
        _suppressHeaderContextMenuUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
    }
    private bool TryConsumeHeaderContextMenuSuppress()
    {
        if (!_suppressNextHeaderContextMenu || DateTime.UtcNow > _suppressHeaderContextMenuUntilUtc)
        {
            _suppressNextHeaderContextMenu = false;
            _suppressHeaderContextMenuUntilUtc = DateTime.MinValue;
            return false;
        }

        _suppressNextHeaderContextMenu = false;
        _suppressHeaderContextMenuUntilUtc = DateTime.MinValue;
        return true;
    }
    private bool TryConsumeBrowserContextMenuSuppress()
    {
        if (!_suppressNextBrowserContextMenu || DateTime.UtcNow > _suppressBrowserContextMenuUntilUtc)
        {
            _suppressNextBrowserContextMenu = false;
            _suppressBrowserContextMenuUntilUtc = DateTime.MinValue;
            return false;
        }
        _suppressNextBrowserContextMenu = false;
        _suppressBrowserContextMenuUntilUtc = DateTime.MinValue;
        return true;
    }
    private bool TryExecuteBrowserMouseGesture(string gesture)
    {
        if (_uiMode != UIMode.Browser || _settings.Input?.EnableMouseGestures != true)
        {
            return false;
        }

        if (!TryResolveMouseGestureCommandId(gesture, out string commandId))
        {
            ShowStatusMessage($"ジェスチャー未割り当て: {gesture}");
            return true;
        }

        if (string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            ShowStatusMessage($"ジェスチャー無効: {gesture}");
            return true;
        }

        string commandName = ResolveMouseGestureCommandDisplayName(commandId);
        bool executed = ExecuteCommandFromUi(commandId, CommandScope.Browser, $"MouseGesture:{gesture}");
        ShowStatusMessage(executed
            ? $"ジェスチャー実行: {gesture} / {commandName}"
            : $"ジェスチャー未実行: {gesture} / {commandName}");
        return executed;
    }
    private void ShowMouseGestureInputStatus(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture) || _uiMode != UIMode.Browser || _settings.Input?.EnableMouseGestures != true)
        {
            return;
        }

        if (!TryResolveMouseGestureCommandId(gesture, out string commandId))
        {
            ShowStatusMessage($"ジェスチャー入力中: {gesture} / 割り当て: 未割り当て");
            return;
        }

        if (string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            ShowStatusMessage($"ジェスチャー入力中: {gesture} / 割り当て: 無効");
            return;
        }

        ShowStatusMessage($"ジェスチャー入力中: {gesture} / 割り当て: {ResolveMouseGestureCommandDisplayName(commandId)}");
    }
    private string ResolveMouseGestureCommandDisplayName(string commandId)
    {
        if (_commandRegistry.Find(commandId) is { } definition && !string.IsNullOrWhiteSpace(definition.DisplayName))
        {
            return definition.DisplayName;
        }

        return commandId;
    }
    private bool TryResolveMouseGestureCommandId(string gesture, out string commandId)
    {
        if (!MouseGestureCommandResolver.TryResolveCommandId(
                gesture,
                _settings.Input?.MouseGestureCommandMap,
                out commandId))
        {
            return false;
        }

        if (string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_commandRegistry.Find(commandId) is not { } definition)
        {
            return false;
        }

        if (!definition.IsCustomizable || definition.IsDangerous)
        {
            return false;
        }

        if (definition.Scope != CommandScope.Browser && definition.Scope != CommandScope.Global)
        {
            return false;
        }

        return true;
    }
    private void PushClosedBrowserTabSnapshot(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabViewState.Count)
        {
            return;
        }
        _closedBrowserTabs.Add(new ClosedBrowserTabSnapshot
        {
            CategoryId = _categoryViewState.ActiveCategoryId ?? string.Empty,
            TabState = _browserTabViewState.Tabs[tabIndex].Clone()
        });
        if (_closedBrowserTabs.Count > ClosedBrowserTabHistoryLimit)
        {
            _closedBrowserTabs.RemoveAt(0);
        }
    }
    private void RestoreLastClosedBrowserTab()
    {
        if (_closedBrowserTabs.Count == 0)
        {
            ShowStatusMessage("Gesture: 復元できる閉じたタブはありません。");
            return;
        }
        ClosedBrowserTabSnapshot snapshot = _closedBrowserTabs[^1];
        string targetCategoryId = _categoryViewState.Categories.Any(category => string.Equals(category.Id, snapshot.CategoryId, StringComparison.OrdinalIgnoreCase))
            ? snapshot.CategoryId
            : (_categoryViewState.ActiveCategoryId ?? string.Empty);
        if (!string.Equals(targetCategoryId, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            SwitchBrowserTabCategory(targetCategoryId);
        }
        int maxTabCount = GetMaxBrowserTabsPerCategory();
        if (_browserTabViewState.Count >= maxTabCount)
        {
            ShowStatusMessage($"タブは最大{maxTabCount}個までです。");
            _browserTabStrip?.FlashLimitReached();
            TryPlayBrowserTabLimitBeep();
            return;
        }
        _closedBrowserTabs.RemoveAt(_closedBrowserTabs.Count - 1);
        BrowserTabState restored = snapshot.TabState.Clone();
        _browserTabViewState.Add(restored);
        RefreshBrowserTabHeaders();
        _browserTabViewState.ActiveTabIndex = -1;
        SwitchBrowserTab(_browserTabViewState.Count - 1);
        ShowStatusMessage("Gesture: 閉じたタブを復元");
    }
    /// <summary>
    /// browserPanel の1ページあたりの項目数を、現在のフォント高さ・パネル高さ・列数から算出する。
    /// </summary>
    private int GetBrowserItemsPerPage() => GetBrowserItemsPerPage(out _, out _);
    private int GetBrowserItemsPerPage(out int itemHeight, out int rowsPerColumn)
    {
        // Phase 5-ui-visual-fix1.2: 実測ベースの行高を採用
        itemHeight = HeaderLayoutHelper.GetMeasuredLineHeight(browserPanel.Font, 4);
        rowsPerColumn = Math.Max(1, (browserPanel.Height - 10) / itemHeight);
        return GetEffectiveBrowserColumnCount() * rowsPerColumn;
    }
    private int CalculateBrowserIndexFromPoint(int x, int y)
    {
        if (fileListView.Items.Count == 0) return -1;
        int itemsPerPage = GetBrowserItemsPerPage(out int itemHeight, out int rowsPerColumn);
        int effectiveColumnCount = GetEffectiveBrowserColumnCount();
        int colWidth = Math.Max(1, browserPanel.Width / effectiveColumnCount);
        int targetCol = x / colWidth;
        int targetRow = y / itemHeight;
        // 論理的な行・列の範囲外なら無効
        if (targetCol < 0 || targetCol >= effectiveColumnCount || targetRow < 0 || targetRow >= rowsPerColumn)
            return -1;
        int pageIndex = targetCol * rowsPerColumn + targetRow;
        return BrowserPageIndex.ToGlobal(pageIndex, _browserPageStartIndex, fileListView.Items.Count);
    }
    private void UpdateBrowserFileNameToolTip(Point location)
    {
        if (_uiMode != UIMode.Browser || fileListView.Items.Count == 0)
        {
            HideBrowserFileNameToolTip();
            return;
        }

        int index = CalculateBrowserIndexFromPoint(location.X, location.Y);
        int pageLocalIndex = index - _browserPageStartIndex;
        if (pageLocalIndex < 0 || pageLocalIndex >= fileListView.Items.Count)
        {
            HideBrowserFileNameToolTip();
            return;
        }

        if (!TryGetBrowserItemLayoutBounds(index, out Rectangle hoverBounds, out Rectangle nameBounds))
        {
            HideBrowserFileNameToolTip();
            return;
        }

        if (!hoverBounds.Contains(location))
        {
            HideBrowserFileNameToolTip();
            return;
        }

        ListViewItem item = fileListView.Items[pageLocalIndex];
        if (!IsBrowserItemNameEllipsized(item, nameBounds))
        {
            HideBrowserFileNameToolTip();
            return;
        }

        string toolTipText = GetItemFullName(item);
        if (string.IsNullOrWhiteSpace(toolTipText))
        {
            HideBrowserFileNameToolTip();
            return;
        }

        if (_browserFileNameToolTipIndex == index && string.Equals(_browserFileNameToolTipText, toolTipText, StringComparison.Ordinal))
        {
            return;
        }

        HideBrowserFileNameToolTip();
        _browserFileNameToolTip.Show(toolTipText, browserPanel, location.X + 16, location.Y + 20, 5000);
        _browserFileNameToolTipIndex = index;
        _browserFileNameToolTipText = toolTipText;
    }
    private void HideBrowserFileNameToolTip()
    {
        _browserFileNameToolTip.Hide(browserPanel);
        _browserFileNameToolTipIndex = -1;
        _browserFileNameToolTipText = null;
    }



    private string GetActionShortLabel_MainForm(FunctionKeyAction action)
    {
        return action switch
        {
            FunctionKeyAction.Help => "ヘルプ",
            FunctionKeyAction.Rename => "名前変更",
            FunctionKeyAction.Execute => "実行",
            FunctionKeyAction.Copy => "コピー",
            FunctionKeyAction.Edit => "編集",
            FunctionKeyAction.Sort => "ソート",
            FunctionKeyAction.Filter => "フィルタ",
            FunctionKeyAction.Tree => "ツリー",
            FunctionKeyAction.Logdisk => "Logdisk",
            FunctionKeyAction.Unpack => "解凍",
            FunctionKeyAction.Menu => "メニュー",
            FunctionKeyAction.Top => "先頭移動",
            FunctionKeyAction.Bottom => "末尾移動",
            _ => "なし"
        };
    }

    private string GetActionDescription_MainForm(FunctionKeyAction action)
    {
        return action switch
        {
            FunctionKeyAction.Help => "ヘルプ画面を表示します。",
            FunctionKeyAction.Rename => "選択項目を名前変更します。",
            FunctionKeyAction.Execute => "選択項目を実行します。",
            FunctionKeyAction.Copy => "選択項目をコピーします。",
            FunctionKeyAction.Edit => "選択項目を編集します。",
            FunctionKeyAction.Sort => "ソート順の設定を開きます。",
            FunctionKeyAction.Filter => "フィルタ設定を開きます。",
            FunctionKeyAction.Tree => "ツリーダイアログを開きます。",
            FunctionKeyAction.Logdisk => "Logdisk画面を開きます。",
            FunctionKeyAction.Unpack => "アーカイブを解凍します。",
            FunctionKeyAction.Menu => "メインメニューを開きます。",
            FunctionKeyAction.Top => "一覧の先頭に移動します。",
            FunctionKeyAction.Bottom => "一覧の末尾に移動します。",
            _ => "アクションなし"
        };
    }

    private string GetStandardActionLabel_MainForm(int slot)
    {
        return slot switch
        {
            1 => "ヘルプ",
            2 => "名前変更",
            5 => "再読込",
            10 => "メニュー",
            11 => "先頭移動",
            12 => "末尾移動",
            _ => "なし"
        };
    }

    private string GetStandardActionDescription_MainForm(int slot)
    {
        return slot switch
        {
            1 => "ヘルプ画面を表示します。",
            2 => "選択項目を名前変更します。",
            5 => "現在ディレクトリを再読込します。",
            10 => "メインメニューを開きます。",
            11 => "一覧の先頭に移動します。",
            12 => "一覧の末尾に移動します。",
            _ => "未割り当て"
        };
    }

    private bool TryGetBrowserItemLayoutBounds(int index, out Rectangle hoverBounds, out Rectangle nameBounds)
    {
        hoverBounds = Rectangle.Empty;
        nameBounds = Rectangle.Empty;
        int pageLocalIndex = index - _browserPageStartIndex;
        if (pageLocalIndex < 0 || pageLocalIndex >= fileListView.Items.Count)
        {
            return false;
        }

        int itemsPerPage = GetBrowserItemsPerPage(out int itemHeight, out int rowsPerColumn);
        if (itemsPerPage <= 0 || rowsPerColumn <= 0)
        {
            return false;
        }

        int pageIndex = pageLocalIndex;
        int col = pageIndex / rowsPerColumn;
        int row = pageIndex % rowsPerColumn;
        int effectiveColumnCount = GetEffectiveBrowserColumnCount();
        int colWidth = Math.Max(1, browserPanel.Width / effectiveColumnCount);
        Rectangle rect = new Rectangle(col * colWidth + 5, row * itemHeight + 5, colWidth - 10, itemHeight);
        hoverBounds = rect;

        int iconSize = Math.Clamp((int)Math.Round(browserPanel.Font.Height * 0.9), 12, 48);
        bool showItemIcons = _settings.Appearance?.ShowItemIcons ?? true;
        int markSlotWidth = GetBrowserMarkSlotWidth(browserPanel.Font, showItemIcons, iconSize);
        int iconSlotWidth = showItemIcons ? (iconSize + 2) : 0;
        Rectangle textRect = new Rectangle(
            rect.X + markSlotWidth + iconSlotWidth,
            rect.Y,
            rect.Width - markSlotWidth - iconSlotWidth,
            rect.Height);
        if (textRect.Width <= 0)
        {
            return false;
        }

        BrowserFileDisplayMode mode = GetBrowserFileDisplayMode();
        if (mode == BrowserFileDisplayMode.NameOnly)
        {
            nameBounds = textRect;
            return true;
        }

        using Graphics g = browserPanel.CreateGraphics();
        if (!TryCalculateBrowserNameRectForDetail(g, fileListView.Items[pageLocalIndex], textRect, browserPanel.Font, mode, out Rectangle detailNameRect))
        {
            if (mode == BrowserFileDisplayMode.NameSizeDate
                && TryCalculateBrowserNameRectForDetail(g, fileListView.Items[pageLocalIndex], textRect, browserPanel.Font, BrowserFileDisplayMode.NameSize, out detailNameRect))
            {
                nameBounds = detailNameRect;
                return true;
            }

            nameBounds = textRect;
            return true;
        }

        nameBounds = detailNameRect;
        return true;
    }
    private bool TryCalculateBrowserNameRectForDetail(
        Graphics g,
        ListViewItem item,
        Rectangle textRect,
        Font font,
        BrowserFileDisplayMode mode,
        out Rectangle nameRect)
    {
        nameRect = Rectangle.Empty;
        const string nameEllipsis = "...";

        bool includeDate = mode == BrowserFileDisplayMode.NameSizeDate;
        string dateText = item.SubItems.Count > 3 ? NormalizeBrowserDateText(item.SubItems[3].Text) : string.Empty;
        if (DateTime.TryParse(item.SubItems.Count > 3 ? item.SubItems[3].Text : string.Empty, out DateTime parsedDate))
        {
            dateText = FileSystemItemFactory.FormatDisplayDate(parsedDate, _settings.Appearance?.DateFormat);
        }

        if (includeDate && string.IsNullOrWhiteSpace(dateText))
        {
            return false;
        }

        string sizeText = IsDirectoryListItem(item) ? "<DIR>" : BuildBrowserFileSizeTextCompact(item);
        if (string.IsNullOrWhiteSpace(sizeText))
        {
            return false;
        }

        int gapWidth = Math.Max(2, MeasureBrowserTextWidth(g, " ", font));
        string sizeSample = GetBrowserCompactSizeFieldSample();
        string dateSample = GetBrowserDateFieldSample();
        int dateFieldWidth = includeDate
            ? Math.Max(MeasureBrowserTextWidth(g, dateSample, font), MeasureBrowserTextWidth(g, dateText, font))
            : 0;
        int sizeFieldWidth = Math.Max(
            MeasureBrowserTextWidth(g, sizeSample, font),
            Math.Max(MeasureBrowserTextWidth(g, "<DIR>", font), MeasureBrowserTextWidth(g, sizeText, font)));
        int minimumNameWidth = MeasureBrowserTextWidth(g, nameEllipsis, font);
        int requiredWidth = includeDate
            ? minimumNameWidth + sizeFieldWidth + dateFieldWidth + (gapWidth * 2)
            : minimumNameWidth + sizeFieldWidth + gapWidth;
        if (textRect.Width < requiredWidth)
        {
            return false;
        }

        int reservedDetailWidth = includeDate
            ? sizeFieldWidth + dateFieldWidth + (gapWidth * 2)
            : sizeFieldWidth + gapWidth;
        int nameFieldWidth = Math.Max(minimumNameWidth, textRect.Width - reservedDetailWidth);
        if (nameFieldWidth < minimumNameWidth)
        {
            return false;
        }

        nameRect = new Rectangle(textRect.X, textRect.Y, nameFieldWidth, textRect.Height);
        return true;
    }
    private bool IsBrowserItemNameEllipsized(ListViewItem item, Rectangle nameBounds)
    {
        if (nameBounds.Width <= 0)
        {
            return false;
        }

        using Graphics g = browserPanel.CreateGraphics();
        if (IsDirectoryListItem(item))
        {
            if (item.Text == "..")
            {
                return false;
            }

            bool showDirectoryMarker = _settings.Appearance?.ShowDirectoryMarker ?? true;
            BrowserFileDisplayMode mode = GetBrowserFileDisplayMode();
            if (mode == BrowserFileDisplayMode.NameOnly && showDirectoryMarker)
            {
                const string marker = " <DIR>";
                string fullDisplayText = item.Text + marker;
                string fittedDisplayText = FitDirectoryTextPreservingMarker(item.Text, marker, nameBounds.Width, browserPanel.Font, g);
                return !string.Equals(fullDisplayText, fittedDisplayText, StringComparison.Ordinal);
            }

            string fittedDirectoryName = FitTextWithTrailingEllipsis(item.Text, nameBounds.Width, browserPanel.Font, g);
            return !string.Equals(item.Text, fittedDirectoryName, StringComparison.Ordinal);
        }

        string fullName = GetItemFullName(item);
        int textWidth = MeasureBrowserTextWidth(g, fullName, browserPanel.Font);
        return textWidth > nameBounds.Width;
    }
    private bool IsCommandLauncherShortcut(Keys keyData)
    {
        var shortcut = _settings?.Input?.CommandLauncherShortcut ?? "Ctrl+Shift+P";
        return shortcut switch
        {
            "Ctrl+Shift+P" => keyData == (Keys.Control | Keys.Shift | Keys.P),
            "Ctrl+Space" => keyData == (Keys.Control | Keys.Space),
            "None" => false,
            _ => keyData == (Keys.Control | Keys.Shift | Keys.P)
        };
    }
    private bool ExecuteCommandFromUi(string commandId, CommandScope scope, string source, SelectionResult? selectionSnapshot = null)
    {
        return _commandDispatcher.TryExecute(commandId, new CommandExecutionContext
        {
            Scope = scope,
            Source = source,
            SelectionSnapshot = selectionSnapshot
        });
    }

    private void OpenSystemInformationFromUi(string source)
    {
        bool executed = ExecuteCommandFromUi(CommandIds.AppOpenSystemInformation, CommandScope.Browser, source);
        if (executed)
        {
            return;
        }

        LogService.Warn($"[SystemInformation] Command dispatch returned false. Source={source} UiMode={_uiMode}");
        if (_uiMode == UIMode.Browser)
        {
            ShowSystemInformationDialog();
            return;
        }

        ShowStatusMessage("情報画面を開けませんでした。Browser画面で再度お試しください。");
    }
    private bool TryExecuteRegisteredCommand(string commandId, CommandExecutionContext context)
    {
        switch (commandId)
        {
            case CommandIds.BrowserNavigateParent:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteBackspace();
                return true;
            case CommandIds.BrowserNavigateBack:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteHistoryBack();
                return true;
            case CommandIds.BrowserNavigateForward:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteHistoryForward();
                return true;
            case CommandIds.BrowserReload:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteCurrentDirectoryReloadCommand();
                return true;
            case CommandIds.BrowserMarkAllFiles:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ToggleBulkMarks(includeDirectories: false);
                return true;
            case CommandIds.BrowserMarkAllItems:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ToggleBulkMarks(includeDirectories: true);
                return true;
            case CommandIds.BrowserCursorTop:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                MoveBrowserCursorToTop();
                return true;
            case CommandIds.BrowserCursorBottom:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                MoveBrowserCursorToBottom();
                return true;
            case CommandIds.BrowserChangeAttributes:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteAttribute();
                return true;
            case CommandIds.BrowserExecute:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteCurrentFile();
                return true;
            case CommandIds.BrowserCreateDirectory:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteCreateDirectory();
                return true;
            case CommandIds.BrowserCreateFile:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteCreateFile();
                return true;
            case CommandIds.BrowserPathEntryOpen:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                OpenBrowserPathEntry();
                return true;
            case CommandIds.BrowserOpenExplorer:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteOpenCurrentPathInExplorer();
                return true;
            case CommandIds.BrowserOpenShell:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                if (GuardMutationBusy()) return false;
                OpenTerminalInCurrentDirectory(ShellKind.PowerShell);
                return true;
            case CommandIds.BrowserOpenExternalEditor:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteOpenWithEditor();
                return true;
            case CommandIds.BrowserOpenCommandPrompt:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                OpenTerminalInCurrentDirectory(ShellKind.CommandPrompt);
                return true;
            case CommandIds.BrowserPreview:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecutePreviewLaunch();
                return true;
            case CommandIds.BrowserSort:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteSort();
                return true;
            case CommandIds.BrowserFilter:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteFilter();
                return true;
            case CommandIds.BrowserTree:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteTreeDialog();
                return true;
            case CommandIds.BrowserQuickAccess:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteQuickAccess();
                return true;
            case CommandIds.BrowserLogdisk:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteLogdisk();
                return true;
            case CommandIds.ArchivePack:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                _ = ExecutePack(selectionSnapshot: context.SelectionSnapshot);
                return true;
            case CommandIds.ArchiveUnpack:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                _ = ExecuteUnpack(context.SelectionSnapshot);
                return true;
            case CommandIds.BrowserCopyFullPath:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                CopySelectedOrMarkedFullPathsToClipboard();
                return true;
            case CommandIds.BrowserShowHelp:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ShowMenuKeyHint();
                return true;
            case CommandIds.BrowserOpenMarkSlot:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                OpenMarkSlotDialog();
                return true;
            case CommandIds.BrowserTabNew:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                CreateNewBrowserTab();
                return true;
            case CommandIds.BrowserTabNext:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                SelectAdjacentBrowserTab(+1);
                return true;
            case CommandIds.BrowserTabPrevious:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                SelectAdjacentBrowserTab(-1);
                return true;
            case CommandIds.BrowserTabCategoryAdd:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                return AddGeneratedBrowserTabCategory() != null;
            case CommandIds.BrowserTabCategoryRename:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                return RenameActiveBrowserTabCategory();
            case CommandIds.BrowserTabCategoryDelete:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                return DeleteActiveBrowserTabCategory();
            case CommandIds.BrowserTabCategoryMoveLeft:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                return MoveActiveBrowserTabCategory(-1);
            case CommandIds.BrowserTabCategoryMoveRight:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                return MoveActiveBrowserTabCategory(+1);
            case CommandIds.BrowserTabCategoryNext:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                SelectAdjacentBrowserTabCategory(+1);
                return true;
            case CommandIds.BrowserTabCategoryPrevious:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                SelectAdjacentBrowserTabCategory(-1);
                return true;
            case CommandIds.BrowserTabClose:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                CloseCurrentBrowserTab();
                return true;
            case CommandIds.BrowserTabRestoreClosed:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                RestoreLastClosedBrowserTab();
                return true;
            case CommandIds.ClipboardPaste:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteClipboardPaste();
                return true;
            case CommandIds.FileCopy:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                _ = ExecuteCopy(context.SelectionSnapshot);
                return true;
            case CommandIds.FileMove:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                _ = ExecuteMove(context.SelectionSnapshot);
                return true;
            case CommandIds.FileRename:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteRename(context.SelectionSnapshot);
                return true;
            case CommandIds.FileDelete:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                _ = ExecuteDelete(permanent: false, context.SelectionSnapshot);
                return true;
            case CommandIds.EditUndo:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteFileOperationUndo();
                return true;
            case CommandIds.EditRedo:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                ExecuteFileOperationRedo();
                return true;
            case CommandIds.AppOpenSystemInformation:
                if (_uiMode != UIMode.Browser) return false;
                ShowSystemInformationDialog();
                return true;
            case CommandIds.AppOpenNewInstance:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                if (GuardMutationBusy()) return false;
                try
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        UseShellExecute = false
                    };
                    startInfo.ArgumentList.Add(_navigationService.CurrentPath);
                    System.Diagnostics.Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    LogService.Error($"NewInstance 起動失敗: {ex.Message}");
                }
                return true;
            case CommandIds.AppOpenControlPanel:
                if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return false;
                if (GuardMutationBusy()) return false;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogService.Error($"ControlPanel 起動失敗: {ex.Message}");
                }
                return true;
            case CommandIds.BrowserTabFilterLock:
                OpenActiveTabFilterLockDialog();
                return true;
            case CommandIds.BrowserTabLock:
                ToggleActiveBrowserTabLock();
                return true;
            case CommandIds.AppOpenSettings:
                OpenSettingsForm();
                return true;
            case CommandIds.AppOpenCommandLauncher:
                OpenCommandPalette();
                return true;
            case CommandIds.AppOpenCommandList:
                ShowCommandList();
                return true;
            case CommandIds.AppOpenManagedTrash:
                OpenManagedTrashDialog();
                return true;
            default:
                return false;
        }
    }
    private void OpenManagedTrashDialog()
    {
        using var dialog = new Dialogs.ManagedTrashDialog(_settings, _fileOperationUndoRedoService);
        dialog.ShowDialog(this);
    }

    private void ShowCommandList()
    {
        using var dialog = new Dialogs.CommandListDialog(_commandRegistry.GetAll());
        dialog.ShowDialog(this);
    }

    private void ShowSystemInformationDialog()
    {
        try
        {
            using var dialog = new Dialogs.SystemInformationDialog(_navigationService.CurrentPath);
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            LogService.Error("[SystemInformation] Failed to open dialog.", ex);
            MessageBox.Show(
                this,
                $"情報画面を開けませんでした。\n{ex.GetType().Name}: {ex.Message}",
                "情報",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
    private void OpenCommandPalette()
    {
        if (_uiMode != UIMode.Browser)
        {
            ShowStatusMessage("Command Palette は Browser モードでのみ使用できます。");
            return;
        }
        bool allowUsage = _featureGate.IsEnabled(FeatureId.CommandPaletteUsage);
        var usageState = allowUsage
            ? Services.CommandPaletteUsageStorage.Load()
            : new CommandPaletteUsageState();
        SelectionResult selectionSnapshot = InvokeResolveSelection();
        using var dialog = new Dialogs.CommandPaletteDialog(
            (query, expanded) => Services.CommandPaletteService.BuildPresentation(this, _featureGate, usageState, query, expanded, selectionSnapshot),
            usageState,
            allowUsage ? Services.CommandPaletteUsageStorage.Save : _ => { });
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (dialog.SelectedCommand is { } selectedCommand)
            {
                if (allowUsage)
                {
                    Services.CommandPaletteUsageStorage.RecordRecent(usageState, selectedCommand.Id);
                    Services.CommandPaletteUsageStorage.Save(usageState);
                }
                selectedCommand.Execute();
            }
        }
    }
    internal string InvokeGetCurrentBrowserPath() => _navigationService.CurrentPath;
    internal QuickAccessStore InvokeGetQuickAccessStoreClone() => _quickAccessStore.Clone();
    internal IReadOnlyList<string> InvokeGetBackHistorySnapshot() => _navigationService.GetBackHistorySnapshot();
    internal IReadOnlyList<string> InvokeGetForwardHistorySnapshot() => _navigationService.GetForwardHistorySnapshot();
    internal MarkSlotStore InvokeGetMarkSlotStoreClone() => _markSlotStore.Clone();
    internal SelectionResult InvokeResolveSelection() => ResolveSelection();
    internal void InvokeNavigateToPath(string path) => NavigateToPathSafe(path);
    internal MarkSlotActionResult InvokeRestoreMarksFromSlot(int slotNumber) => RestoreMarksFromSlot(slotNumber);
    internal void InvokeShowArchiveContents(string archivePath) => ShowArchiveContentsOrFallback(archivePath);
    internal Task InvokeExecuteArchiveHashAsync(SevenZipHashAlgorithm algorithm) => ExecuteHashAsync(algorithm);
    internal BrowserTabRestoreSnapshot InvokeGetBrowserTabRestoreSnapshot() => EnsureBrowserTabRestoreSnapshot().Clone();
    internal CommandRegistry InvokeGetCommandRegistry() => _commandRegistry;
    internal string InvokeGetCurrentFunctionKeyProfileValue() => CurrentFunctionKeyProfileValue;
    internal Dictionary<string, List<string>>? InvokeGetBrowserKeyCommandOverrides() => _settings.Input?.BrowserKeyCommandOverrides;
    internal bool InvokeExecuteCommandFromUi(string commandId, CommandScope scope, string source, SelectionResult? selectionSnapshot = null) => ExecuteCommandFromUi(commandId, scope, source, selectionSnapshot);
    internal void InvokeShowCommandList() => ShowCommandList();
    internal void InvokeShowSystemInformationDialog() => ShowSystemInformationDialog();
    internal void InvokeOpenControlPanel()
    {
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy()) return;
        if (GuardMutationBusy()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Error($"ControlPanel 起動失敗: {ex.Message}");
        }
    }
    internal void InvokeActivateBrowserTab(string categoryId, Guid tabId)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return;
        }

        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        BrowserTabRestoreCategoryState? category = snapshot.Categories.FirstOrDefault(item => string.Equals(item.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (category == null)
        {
            return;
        }

        int tabIndex = category.OpenTabs.FindIndex(tab => tab.TabId == tabId);
        if (tabIndex < 0)
        {
            return;
        }

        if (!string.Equals(_categoryViewState.ActiveCategoryId, category.Id, StringComparison.OrdinalIgnoreCase))
        {
            SwitchBrowserTabCategory(category.Id);
        }

        SwitchBrowserTab(tabIndex);
    }
    string ICommandPaletteLayerHost.GetCurrentBrowserPath() => InvokeGetCurrentBrowserPath();
    QuickAccessStore ICommandPaletteLayerHost.GetQuickAccessStoreClone() => InvokeGetQuickAccessStoreClone();
    IReadOnlyList<string> ICommandPaletteLayerHost.GetBackHistorySnapshot() => InvokeGetBackHistorySnapshot();
    IReadOnlyList<string> ICommandPaletteLayerHost.GetForwardHistorySnapshot() => InvokeGetForwardHistorySnapshot();
    MarkSlotStore ICommandPaletteLayerHost.GetMarkSlotStoreClone() => InvokeGetMarkSlotStoreClone();
    SelectionResult ICommandPaletteLayerHost.ResolveSelection() => InvokeResolveSelection();
    void ICommandPaletteLayerHost.NavigateToPath(string path) => InvokeNavigateToPath(path);
    void ICommandPaletteLayerHost.RestoreMarksFromSlot(int slotNumber) => InvokeRestoreMarksFromSlot(slotNumber);
    void ICommandPaletteLayerHost.ShowArchiveContents(string archivePath) => InvokeShowArchiveContents(archivePath);
    Task ICommandPaletteLayerHost.ExecuteArchiveHashAsync(SevenZipHashAlgorithm algorithm) => InvokeExecuteArchiveHashAsync(algorithm);
    // Bridge methods for CommandPalette
    internal void InvokeReloadCurrentDirectory() => ReloadCurrentDirectory("コマンドパレットから再読込しました。");
    internal void InvokeCopyCurrentDirectory() => CopyCurrentDirectoryFromHeader();
    internal void InvokeCopySelectedItemFullPath() => CopySelectedItemFullPathFromHeader();
    internal void InvokeOpenExplorer() => ExecuteOpenCurrentPathInExplorer();
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_WINDOWPOSCHANGING)
        {
            WINDOWPOS pos = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS))!;
            // 1. Capture pre-minimize bounds from WM_WINDOWPOSCHANGING (Win+M などの SC_MINIMIZE を通らない経路対策)
            bool isMinimizedPlaceholder = pos.x <= -30000 && pos.y <= -30000;
            if (isMinimizedPlaceholder)
            {
                if (this.WindowState == FormWindowState.Normal && IsSaneNormalBounds(this.Bounds))
                {
                    _normalBoundsBeforeMinimize = this.Bounds;
                    // Record as baseline if it's "truly sane" (clearly above the floor)
                    if (this.Bounds.Height > MinimumNormalWindowHeight + 40)
                    {
                        _restoreBaselineNormalBounds = this.Bounds;
                    }
                    LogService.Info($"[WindowFloorHitIntercept] CapturePreMinimize placeholder only Pos=({pos.x},{pos.y},{pos.cx},{pos.cy}) Bounds={FormatBoundsForLog(this.Bounds)}");
                }
            }
            else
            {
                // 2. Intercept floor-hit candidate during restore (only for normal coordinates)
                if (_isInRestorePlacementWatch && pos.x > -30000 && pos.y > -30000 && pos.cx > 0 && pos.cy > 0)
                {
                    if (pos.cy <= MinimumNormalWindowHeight + 4)
                    {
                        Rectangle? baseline = null;
                        string baselineSource = "";
                        // baseline 優先順位
                        if (_normalBoundsBeforeMinimize is { } preMin && preMin.Height > MinimumNormalWindowHeight + 40)
                        {
                            baseline = preMin;
                            baselineSource = "PreMinimize";
                        }
                        else if (_restoreBaselineNormalBounds is { } restoreBase && restoreBase.Height > MinimumNormalWindowHeight + 40)
                        {
                            baseline = restoreBase;
                            baselineSource = "RestoreBaseline";
                        }
                        else
                        {
                            var wp = new WINDOWPLACEMENT();
                            wp.length = Marshal.SizeOf(wp);
                            if (GetWindowPlacement(this.Handle, ref wp))
                            {
                                Rectangle placementRect = ToRectangle(wp.rcNormalPosition);
                                if (placementRect.Height > MinimumNormalWindowHeight + 40)
                                {
                                    baseline = placementRect;
                                    baselineSource = "PlacementNormal";
                                }
                            }
                        }
                        if (baseline == null && _lastKnownGoodNormalBounds is { } lastGood && lastGood.Height > MinimumNormalWindowHeight + 40)
                        {
                            baseline = lastGood;
                            baselineSource = "LastKnownGood";
                        }
                        if (baseline != null)
                        {
                            Rectangle safeBaseline = baseline.Value;
                            LogService.Warn($"[WindowFloorHitIntercept] Intercepted WM_WINDOWPOSCHANGING floor-hit Candidate=({pos.cx},{pos.cy}) Baseline={FormatBoundsForLog(safeBaseline)} Source={baselineSource}");
                            pos.cx = safeBaseline.Width;
                            pos.cy = safeBaseline.Height;
                            // NOTE: We only fix size here, position is left to OS to avoid side effects with multi-mon setup.
                            Marshal.StructureToPtr(pos, m.LParam, false);
                            LogService.Info($"[WindowFloorHitIntercept] Applied baseline to WINDOWPOS ({pos.cx}x{pos.cy})");
                        }
                        else
                        {
                            LogService.Warn($"[WindowFloorHitIntercept] Candidate floor-hit detected but no sane baseline available. Leave to scheduled repair. Candidate=({pos.cx},{pos.cy})");
                        }
                    }
                }
            }
            bool shouldLog = _isInRestorePlacementWatch ||
                             (pos.cy > 0 && pos.cy <= MinimumNormalWindowHeight + 80 && pos.cy < 600) ||
                             (DateTime.UtcNow - _lastRestoreUtc).TotalSeconds < 2;
            if (shouldLog)
            {
                var wp = new WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf(wp);
                GetWindowPlacement(this.Handle, ref wp);
                LogService.Info($"[WindowFloorHitTrace] Message=WM_WINDOWPOSCHANGING " +
                    $"pos=({pos.x},{pos.y},{pos.cx},{pos.cy}) flags=0x{pos.flags:X} " +
                    $"WindowState={this.WindowState} " +
                    $"Bounds={FormatBoundsForLog(this.Bounds)} " +
                    $"RestoreBounds={FormatBoundsForLog(this.RestoreBounds)} " +
                    $"ClientSize={this.ClientSize.Width}x{this.ClientSize.Height} " +
                    $"MinimumSize={this.MinimumSize.Width}x{this.MinimumSize.Height} " +
                    $"RestoreWatch={_isInRestorePlacementWatch} " +
                    $"PreMinimize={(_normalBoundsBeforeMinimize != null ? FormatBoundsForLog(_normalBoundsBeforeMinimize.Value) : "null")} " +
                    $"RestoreBaseline={(_restoreBaselineNormalBounds != null ? FormatBoundsForLog(_restoreBaselineNormalBounds.Value) : "null")} " +
                    $"LastKnownGood={(_lastKnownGoodNormalBounds != null ? FormatBoundsForLog(_lastKnownGoodNormalBounds.Value) : "null")} " +
                    $"PlacementNormal={wp.rcNormalPosition}");
            }
        }
        else if (m.Msg == WM_WINDOWPOSCHANGED)
        {
            LogWindowPlacementSnapshot("WndProc:WM_WINDOWPOSCHANGED");
            if (!_isApplyingWindowBoundsRecovery && this.WindowState != FormWindowState.Minimized)
            {
                var wp = new WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf(wp);
                if (GetWindowPlacement(this.Handle, ref wp))
                {
                    Rectangle normalRect = ToRectangle(wp.rcNormalPosition);
                    bool isCollapsed = IsCollapsedWindowPlacementNormal(wp);
                    bool isFloorHit = IsRestoreFloorHitCorruption(normalRect);
                    if (isCollapsed || isFloorHit)
                    {
                        Rectangle? repairTarget = null;
                        string source = "";
                        if (_normalBoundsBeforeMinimize is { } preMin && IsSaneNormalBounds(preMin))
                        {
                            repairTarget = preMin;
                            source = "PreMinimize";
                        }
                        else if (_restoreBaselineNormalBounds is { } baseline && IsSaneNormalBounds(baseline))
                        {
                            repairTarget = baseline;
                            source = "RestoreBaseline";
                        }
                        else if (_lastKnownGoodNormalBounds is { } lastGood && IsSaneNormalBounds(lastGood))
                        {
                            repairTarget = lastGood;
                            source = "LastKnownGood";
                        }
                        if (repairTarget != null)
                        {
                            LogService.Warn($"[WindowRestoreFloorHit] Detected corruption (Collapsed={isCollapsed}, FloorHit={isFloorHit}, normal={wp.rcNormalPosition}). Scheduling repair with {source}={FormatBoundsForLog(repairTarget.Value)}");
                            ScheduleRestorePlacementRepair(repairTarget.Value, $"WndProc(Collapsed={isCollapsed},FloorHit={isFloorHit})");
                        }
                    }
                    else if (_isInRestorePlacementWatch && this.WindowState == FormWindowState.Normal && IsSaneNormalBounds(this.Bounds) && this.Bounds.Height > MinimumNormalWindowHeight + 40)
                    {
                        _isInRestorePlacementWatch = false;
                        LogService.Info($"[WindowRestoreFloorHit] End restore watch Reason=SaneBounds Bounds={FormatBoundsForLog(this.Bounds)}");
                    }
                }
            }
        }
        else if (m.Msg == WM_GETMINMAXINFO)
        {
            MinMaxInfo mmi = (MinMaxInfo)m.GetLParam(typeof(MinMaxInfo))!;
            int beforeW = mmi.ptMinTrackSize.x;
            int beforeH = mmi.ptMinTrackSize.y;
            mmi.ptMinTrackSize.x = MinimumNormalWindowWidth;
            mmi.ptMinTrackSize.y = MinimumNormalWindowHeight;
            Marshal.StructureToPtr(mmi, m.LParam, false);
            var wp = new WINDOWPLACEMENT();
            wp.length = Marshal.SizeOf(wp);
            GetWindowPlacement(this.Handle, ref wp);
            Rectangle normalRect = ToRectangle(wp.rcNormalPosition);
            bool shouldLog = _isInRestorePlacementWatch ||
                             (this.Bounds.Height < 600) ||
                             (normalRect.Height < 600) ||
                             (DateTime.UtcNow - _lastRestoreUtc).TotalSeconds < 2;
            if (shouldLog)
            {
                LogService.Info($"[WindowFloorHitTrace] Message=WM_GETMINMAXINFO " +
                    $"BeforeMinTrack={beforeW}x{beforeH} " +
                    $"AfterMinTrack={mmi.ptMinTrackSize.x}x{mmi.ptMinTrackSize.y} " +
                    $"WindowState={this.WindowState} " +
                    $"Bounds={FormatBoundsForLog(this.Bounds)} " +
                    $"RestoreBounds={FormatBoundsForLog(this.RestoreBounds)} " +
                    $"RestoreWatch={_isInRestorePlacementWatch} " +
                    $"PlacementNormal={wp.rcNormalPosition}");
            }
        }
        else if (m.Msg == WM_SIZE)
        {
            int wParam = (int)m.WParam;
            int width = (int)m.LParam & 0xFFFF;
            int height = (int)m.LParam >> 16;
            bool shouldLog = _isInRestorePlacementWatch ||
                             (height > 0 && height <= MinimumNormalWindowHeight + 80 && height < 600) ||
                             (DateTime.UtcNow - _lastRestoreUtc).TotalSeconds < 2;
            if (shouldLog)
            {
                var wp = new WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf(wp);
                GetWindowPlacement(this.Handle, ref wp);
                LogService.Info($"[WindowFloorHitTrace] Message=WM_SIZE " +
                    $"wParam={wParam} width={width} height={height} " +
                    $"WindowState={this.WindowState} " +
                    $"Bounds={FormatBoundsForLog(this.Bounds)} " +
                    $"RestoreBounds={FormatBoundsForLog(this.RestoreBounds)} " +
                    $"ClientSize={this.ClientSize.Width}x{this.ClientSize.Height} " +
                    $"RestoreWatch={_isInRestorePlacementWatch} " +
                    $"PlacementNormal={wp.rcNormalPosition}");
            }
        }
        else if (m.Msg == WM_SHOWWINDOW || m.Msg == WM_ACTIVATE || m.Msg == WM_ACTIVATEAPP)
        {
            string msgName = m.Msg switch {
                WM_SHOWWINDOW => "WM_SHOWWINDOW",
                WM_ACTIVATE => "WM_ACTIVATE",
                WM_ACTIVATEAPP => "WM_ACTIVATEAPP",
                _ => "UNKNOWN"
            };
            var wp = new WINDOWPLACEMENT();
            wp.length = Marshal.SizeOf(wp);
            GetWindowPlacement(this.Handle, ref wp);
            LogService.Info($"[WindowFloorHitTrace] Message={msgName} " +
                $"wParam=0x{m.WParam:X} lParam=0x{m.LParam:X} " +
                $"WindowState={this.WindowState} " +
                $"Visible={this.Visible} " +
                $"Bounds={FormatBoundsForLog(this.Bounds)} " +
                $"PlacementShowCmd={wp.showCmd} " +
                $"PlacementNormal={wp.rcNormalPosition} " +
                $"RestoreWatch={_isInRestorePlacementWatch}");
        }
        Keys keyCode = (Keys)(nint)m.WParam & Keys.KeyCode;
        if (m.Msg == WM_SYSKEYDOWN)
        {
            LogAltHint($"WM_SYSKEYDOWN Key={keyCode} AltHeld={_isAltHintHeld} AltOwned={_isExternalToolAltPopupAltOwned} CanShow={CanShowCommandHintOverlay()} ActiveControl={DescribeControl(ActiveControl)}");
            bool isAltOnlyKey =
                (keyCode == Keys.Menu || keyCode == Keys.LMenu || keyCode == Keys.RMenu) &&
                (ModifierKeys & Keys.Control) != Keys.Control;
            if (isAltOnlyKey && CanShowCommandHintOverlay())
            {
                _isExternalToolAltPopupAltOwned = true;
                _isAltHintHeld = true;
                ShowCommandHintOverlay();
                return;
            }
        }
        if (m.Msg == WM_SYSKEYUP)
        {
            LogAltHint($"WM_SYSKEYUP Key={keyCode} AltHeldBefore={_isAltHintHeld} AltOwnedBefore={_isExternalToolAltPopupAltOwned} ActiveControl={DescribeControl(ActiveControl)}");
            bool isAltKey =
                keyCode == Keys.Menu ||
                keyCode == Keys.LMenu ||
                keyCode == Keys.RMenu;
            if (isAltKey)
            {
                _isAltHintHeld = false;
                _isExternalToolAltPopupAltOwned = false;
                HideCommandHintOverlay();
                if (CanShowCommandHintOverlay())
                {
                    return;
                }
            }
        }
        if (m.Msg == WM_SYSCOMMAND)
        {
            int command = (int)((long)m.WParam & 0xFFF0);
            LogAltHint($"WM_SYSCOMMAND Command=0x{command:X} lParam=0x{m.LParam:X} AltOwned={_isExternalToolAltPopupAltOwned} UiMode={_uiMode} ActiveControl={DescribeControl(ActiveControl)}");
            bool isSnapshotTarget = (command == SC_MINIMIZE || command == SC_RESTORE || command == SC_MAXIMIZE || command == SC_SIZE || command == SC_MOVE);
            if (isSnapshotTarget)
            {
                LogSysCommandFloorHitTrace("BeforeBase", command);
                _lastRestoreUtc = DateTime.UtcNow;
            }
            if (command == SC_CLOSE)
            {
                LogService.Warn(
                    $"[CancelRuntime] MainForm WM_SYSCOMMAND close. busy={_isClipboardBusy}, " +
                    $"hasCts={_fileOpUiState.Cts != null}, requested={_fileOpUiState.Cts?.IsCancellationRequested ?? false}, " +
                    $"activeControl={DescribeControl(ActiveControl)}, thread={Environment.CurrentManagedThreadId}");
                if (TryRouteActiveFileOperationCancel("MainForm.WndProc.SC_CLOSE"))
                {
                    LogService.Info("[CancelRuntime] MainForm WM_SYSCOMMAND close consumed as active operation cancel request.");
                    return;
                }
            }
            if (command == SC_MINIMIZE)
            {
                if (this.WindowState == FormWindowState.Normal && IsSaneNormalBounds(this.Bounds) && HasUsableClientArea())
                {
                    _normalBoundsBeforeMinimize = this.Bounds;
                    LogService.Info($"[WindowRestoreFloorHit] Capture PreMinimizeBounds={FormatBoundsForLog(_normalBoundsBeforeMinimize.Value)}");
                }
            }
            else if (command == SC_RESTORE)
            {
                _lastRestoreUtc = DateTime.UtcNow;
                _isInRestorePlacementWatch = true;
                _restorePlacementRepairCount = 0;
                LogService.Info($"[WindowRestoreFloorHit] Start Restore Watch. PreMinimize={(_normalBoundsBeforeMinimize != null ? FormatBoundsForLog(_normalBoundsBeforeMinimize.Value) : "null")}");
            }
            else if (command == SC_SIZE || command == SC_MOVE)
            {
                if (_isInRestorePlacementWatch)
                {
                    _isInRestorePlacementWatch = false;
                    LogService.Info($"[WindowRestoreFloorHit] End restore watch Reason=ManualSizeMoveCommand Command=0x{command:X} Bounds={FormatBoundsForLog(this.Bounds)}");
                }
            }
            if (command == SC_KEYMENU && _uiMode == UIMode.Browser)
            {
                if (_isExternalToolAltPopupAltOwned)
                {
                    LogAltHint($"WM_SYSCOMMAND SC_KEYMENU suppressed for external tool alt popup lParam=0x{m.LParam:X}");
                    return;
                }
            }
            base.WndProc(ref m);
            if (isSnapshotTarget)
            {
                LogSysCommandFloorHitTrace("AfterBase", command);
            }
            return;
        }
        base.WndProc(ref m);
    }
    private void LogSysCommandFloorHitTrace(string stage, int command)
    {
        var wp = new WINDOWPLACEMENT();
        wp.length = Marshal.SizeOf(wp);
        GetWindowPlacement(this.Handle, ref wp);
        LogService.Info($"[WindowFloorHitTrace] {stage} command=0x{command:X} " +
            $"Bounds={FormatBoundsForLog(this.Bounds)} " +
            $"RestoreBounds={FormatBoundsForLog(this.RestoreBounds)} " +
            $"ClientSize={this.ClientSize.Width}x{this.ClientSize.Height} " +
            $"PlacementShowCmd={wp.showCmd} " +
            $"PlacementNormal={wp.rcNormalPosition} " +
            $"RestoreWatch={_isInRestorePlacementWatch} " +
            $"PreMinimize={(_normalBoundsBeforeMinimize != null ? FormatBoundsForLog(_normalBoundsBeforeMinimize.Value) : "null")} " +
            $"RestoreBaseline={(_restoreBaselineNormalBounds != null ? FormatBoundsForLog(_restoreBaselineNormalBounds.Value) : "null")} " +
            $"LastKnownGood={(_lastKnownGoodNormalBounds != null ? FormatBoundsForLog(_lastKnownGoodNormalBounds.Value) : "null")}");
    }
    private bool CanShowCommandHintOverlay()
    {
        bool canShow = BuildCommandHintState().CanShowOverlay;
        if (!canShow && _isExternalToolAltPopupAltOwned && _uiMode == UIMode.Browser && Visible && Enabled && browserPanel.Visible)
        {
            return true;
        }
        return canShow;
    }
    private bool CanUseCommandLauncherCommands()
    {
        return BuildCommandHintState().CanUseCommandLauncherCommands;
    }
    private bool TryHandleCommandHintOverlayCmdKey(Keys keyData)
    {
        if (!CanShowCommandHintOverlay() || !IsCommandHintOverlayVisible())
        {
            return false;
        }

        Keys keyCode = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;
        LogAltHint($"TryHandleCommandHintOverlayCmdKey KeyData=0x{(int)keyData:X} KeyCode={keyCode} Modifiers={modifiers} Selected={_commandHintSelectedIndex} Scroll={_commandHintScrollIndex}");
        if (keyCode == Keys.Escape)
        {
            _isAltHintHeld = false;
            HideCommandHintOverlay("TryHandleCommandHintOverlayCmdKey:Escape");
            return true;
        }
        if ((modifiers & Keys.Alt) == Keys.Alt && (keyCode == Keys.Left || keyCode == Keys.Right))
        {
            _isAltHintHeld = false;
            _isExternalToolAltPopupAltOwned = false;
            HideCommandHintOverlay("AltHistoryNavigation");
            return false;
        }
        if (keyCode == Keys.Up)
        {
            MoveCommandHintSelection(-1);
            return true;
        }
        if (keyCode == Keys.Down)
        {
            MoveCommandHintSelection(+1);
            return true;
        }
        if (keyCode == Keys.Home)
        {
            SetCommandHintSelection(0);
            return true;
        }
        if (keyCode == Keys.End)
        {
            SetCommandHintSelection(_commandHintRows.Count - 1);
            return true;
        }
        if (keyCode == Keys.PageUp)
        {
            MoveCommandHintSelection(-GetCommandHintVisibleRowCount());
            return true;
        }
        if (keyCode == Keys.PageDown)
        {
            MoveCommandHintSelection(+GetCommandHintVisibleRowCount());
            return true;
        }
        if (keyCode is Keys.Enter or Keys.Space)
        {
            return LaunchSelectedCommandHint();
        }

        return false;
    }
    private bool TryHandleCommandHintOverlayKeyDown(KeyEventArgs e)
    {
        if (!CanShowCommandHintOverlay())
        {
            HideCommandHintOverlay("TryHandleCommandHintOverlayKeyDown:CanShowFalse");
            return false;
        }
        if (!IsCommandHintOverlayVisible())
        {
            return false;
        }
        if (e.KeyCode == Keys.Escape)
        {
            _isAltHintHeld = false;
            HideCommandHintOverlay("TryHandleCommandHintOverlayKeyDown:Escape");
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.Up)
        {
            MoveCommandHintSelection(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.Down)
        {
            MoveCommandHintSelection(+1);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode is Keys.Home)
        {
            SetCommandHintSelection(0);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode is Keys.End)
        {
            SetCommandHintSelection(_commandHintRows.Count - 1);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode is Keys.PageUp)
        {
            MoveCommandHintSelection(-GetCommandHintVisibleRowCount());
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode is Keys.PageDown)
        {
            MoveCommandHintSelection(+GetCommandHintVisibleRowCount());
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            if (LaunchSelectedCommandHint())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
        }
        return false;
    }
    private void RefreshCommandHintOverlayState()
    {
        if (!Visible || !Enabled)
        {
            _isAltHintHeld = false;
            HideCommandHintOverlay("RefreshCommandHintOverlayState:FormNotVisibleOrEnabled");
            return;
        }
        bool shouldShow = _isAltHintHeld && CanShowCommandHintOverlay();
        LogAltHint($"RefreshCommandHintOverlayState OverlayVisible={IsCommandHintOverlayVisible()} ShouldShow={shouldShow} Selected={_commandHintSelectedIndex} Scroll={_commandHintScrollIndex} AltOwned={_isExternalToolAltPopupAltOwned} ExplicitMenu={_isOpeningMenuStripExplicitly}");
        if (!shouldShow)
        {
            HideCommandHintOverlay("RefreshCommandHintOverlayState:ShouldShowFalse");
            return;
        }
        if (IsCommandHintOverlayVisible())
        {
            ShowCommandHintOverlay(preserveSelection: true);
            return;
        }
        ShowCommandHintOverlay();
    }
    private void ShowCommandHintOverlay(bool preserveSelection = false)
    {
        if (!CanShowCommandHintOverlay())
        {
            return;
        }
        int beforeSelected = _commandHintSelectedIndex;
        int beforeScroll = _commandHintScrollIndex;
        string? selectedToolId = null;
        if (preserveSelection && beforeSelected >= 0 && beforeSelected < _commandHintRows.Count)
        {
            selectedToolId = _commandHintRows[beforeSelected].Tool.Id;
        }
        LogAltHint($"ShowCommandHintOverlay Before OverlayVisible={IsCommandHintOverlayVisible()} Preserve={preserveSelection} BeforeSelected={beforeSelected} BeforeScroll={beforeScroll} ActiveControl={DescribeControl(ActiveControl)}");
        ExternalToolExecutionContext context = ExternalToolLaunchCoordinator.BuildExecutionContext(
            _navigationService.CurrentPath,
            GetSelectedItemFullPathForHeaderCopy(),
            GetSelectedItemNameForHeaderCopy(),
            _markedFiles.Snapshot());
        IReadOnlyList<ExternalToolAltHintRow> rows = BuildExternalToolAltHintRows();
        _commandHintRows = rows;
        (_commandHintContextLine1, _commandHintContextLine2) = BuildExternalToolAltContextLines(context);
        if (_commandHintRows.Count == 0)
        {
            _commandHintSelectedIndex = -1;
            _commandHintScrollIndex = 0;
        }
        if (preserveSelection && _commandHintRows.Count > 0)
        {
            int preservedIndex = -1;
            if (!string.IsNullOrWhiteSpace(selectedToolId))
            {
                preservedIndex = _commandHintRows
                    .Select((row, index) => new { row, index })
                    .FirstOrDefault(item => string.Equals(item.row.Tool.Id, selectedToolId, StringComparison.OrdinalIgnoreCase))?.index ?? -1;
            }
            if (preservedIndex >= 0)
            {
                _commandHintSelectedIndex = preservedIndex;
            }
            else
            {
                _commandHintSelectedIndex = Math.Clamp(_commandHintSelectedIndex, 0, _commandHintRows.Count - 1);
            }
            _commandHintScrollIndex = Math.Clamp(_commandHintScrollIndex, 0, Math.Max(0, _commandHintRows.Count - 1));
            EnsureCommandHintSelectionVisible();
        }
        else
        {
            _commandHintSelectedIndex = GetInitialCommandHintSelectionIndex();
            _commandHintScrollIndex = 0;
        }
        browserPanel.Invalidate();
        string firstRow = _commandHintRows.Count > 0
            ? $"{_commandHintRows[0].SlotLabel}:{_commandHintRows[0].Title}:{_commandHintRows[0].StatusText}"
            : "<none>";
        LogAltHint($"ShowCommandHintOverlay After OverlayVisible={IsCommandHintOverlayVisible()} Preserve={preserveSelection} AfterSelected={_commandHintSelectedIndex} AfterScroll={_commandHintScrollIndex} Bounds={GetCommandHintOverlayBounds()} RowCount={_commandHintRows.Count} First={firstRow} BrowserContext={CanShowCommandHintOverlay()}");
    }
    private void HideCommandHintOverlay(string reason = "Unknown")
    {
        if (!IsCommandHintOverlayVisible())
        {
            _commandHintRows = Array.Empty<ExternalToolAltHintRow>();
            _commandHintSelectedIndex = -1;
            _commandHintScrollIndex = 0;
            _commandHintContextLine1 = string.Empty;
            _commandHintContextLine2 = string.Empty;
            _isExternalToolAltPopupAltOwned = false;
            _lastLoggedCommandHintRowCount = -1;
            _lastLoggedCommandHintBounds = Rectangle.Empty;
            _lastLoggedCommandHintPanelSize = Size.Empty;
            return;
        }
        Rectangle overlayBounds = GetCommandHintOverlayBounds();
        LogAltHint($"HideCommandHintOverlay Reason={reason} Bounds={overlayBounds}");
        _commandHintRows = Array.Empty<ExternalToolAltHintRow>();
        _commandHintSelectedIndex = -1;
        _commandHintScrollIndex = 0;
        _commandHintContextLine1 = string.Empty;
        _commandHintContextLine2 = string.Empty;
        _isExternalToolAltPopupAltOwned = false;
        _lastLoggedCommandHintRowCount = -1;
        _lastLoggedCommandHintBounds = Rectangle.Empty;
        _lastLoggedCommandHintPanelSize = Size.Empty;
        browserPanel.Invalidate();
    }
    private bool LaunchSelectedCommandHint()
    {
        if (_commandHintRows.Count == 0)
        {
            return false;
        }
        if (_commandHintSelectedIndex < 0 || _commandHintSelectedIndex >= _commandHintRows.Count)
        {
            return false;
        }

        ExternalToolAltHintRow selected = _commandHintRows[_commandHintSelectedIndex];
        if (!selected.IsLaunchable)
        {
            ShowStatusMessage($"起動不可: {selected.StatusText}");
            return true;
        }

        HideCommandHintOverlay("LaunchSelectedCommandHint");
        InvokeLaunchExternalTool(selected.Tool);
        return true;
    }
    private void MoveCommandHintSelection(int delta)
    {
        if (_commandHintRows.Count == 0)
        {
            return;
        }

        int before = _commandHintSelectedIndex;
        int next = _commandHintSelectedIndex < 0
            ? 0
            : Math.Clamp(_commandHintSelectedIndex + delta, 0, _commandHintRows.Count - 1);
        LogAltHint($"MoveCommandHintSelection Delta={delta} Before={before} Next={next} Scroll={_commandHintScrollIndex}");
        SetCommandHintSelection(next);
    }
    private void SetCommandHintSelection(int index)
    {
        if (_commandHintRows.Count == 0)
        {
            _commandHintSelectedIndex = -1;
            _commandHintScrollIndex = 0;
            browserPanel.Invalidate();
            return;
        }

        int beforeSelected = _commandHintSelectedIndex;
        int beforeScroll = _commandHintScrollIndex;
        _commandHintSelectedIndex = Math.Clamp(index, 0, _commandHintRows.Count - 1);
        EnsureCommandHintSelectionVisible();
        LogAltHint($"SetCommandHintSelection Requested={index} BeforeSelected={beforeSelected} AfterSelected={_commandHintSelectedIndex} BeforeScroll={beforeScroll} AfterScroll={_commandHintScrollIndex}");
        browserPanel.Invalidate();
    }
    private void EnsureCommandHintSelectionVisible()
    {
        if (_commandHintRows.Count == 0 || _commandHintSelectedIndex < 0)
        {
            return;
        }

        int visibleRows = GetCommandHintVisibleRowCount();
        if (visibleRows <= 0)
        {
            return;
        }

        int beforeScroll = _commandHintScrollIndex;
        int maxScroll = Math.Max(0, _commandHintRows.Count - visibleRows);
        if (_commandHintSelectedIndex < _commandHintScrollIndex)
        {
            _commandHintScrollIndex = _commandHintSelectedIndex;
        }
        else if (_commandHintSelectedIndex >= _commandHintScrollIndex + visibleRows)
        {
            _commandHintScrollIndex = _commandHintSelectedIndex - visibleRows + 1;
        }

        _commandHintScrollIndex = Math.Clamp(_commandHintScrollIndex, 0, maxScroll);
        LogAltHint($"EnsureCommandHintSelectionVisible Selected={_commandHintSelectedIndex} VisibleRows={visibleRows} BeforeScroll={beforeScroll} AfterScroll={_commandHintScrollIndex}");
    }
    private int GetInitialCommandHintSelectionIndex()
    {
        if (_commandHintRows.Count == 0)
        {
            return -1;
        }

        int launchableIndex = _commandHintRows
            .Select((row, index) => new { row, index })
            .FirstOrDefault(item => item.row.IsLaunchable)?.index ?? -1;
        return launchableIndex >= 0 ? launchableIndex : 0;
    }
    private int GetCommandHintVisibleRowCount()
    {
        Rectangle overlayRect = GetCommandHintOverlayBounds();
        return CommandHintOverlayLayout.GetVisibleRowCount(
            overlayRect,
            CommandHintOverlayLayout.DefaultMetrics);
    }
    private static (string Line1, string Line2) BuildExternalToolAltContextLines(ExternalToolExecutionContext context)
    {
        string currentDir = string.IsNullOrWhiteSpace(context.CurrentDirectory) ? "(currentDir 未設定)" : context.CurrentDirectory;
        string selected = string.IsNullOrWhiteSpace(context.SelectedPath)
            ? "(selectedPath なし)"
            : (string.IsNullOrWhiteSpace(context.SelectedName) ? context.SelectedPath : $"{context.SelectedName} — {context.SelectedPath}");
        string marked = context.MarkedPaths.Count == 0 ? "Marked: 0" : $"Marked: {context.MarkedPaths.Count}";
        return (
            $"Target: {currentDir}",
            $"Selected: {selected} / {marked} / Alt+英数字 = External tool namespace / Alt+F1〜F12 = Function layer"
        );
    }
    private static string BuildExternalToolAltStatus(
        ExternalToolCommandDefinition tool,
        IReadOnlyDictionary<string, int> slotCounts,
        out string slotLabel,
        out bool isLaunchable)
    {
        slotLabel = "Alt+?";
        isLaunchable = false;

        if (!tool.Enabled)
        {
            return "無効";
        }
        if (string.IsNullOrWhiteSpace(tool.Id))
        {
            return "ID未設定";
        }
        if (!TryNormalizeExternalToolAltSlot(tool.AltSlot, out string normalizedSlot))
        {
            return "スロット未設定";
        }

        slotLabel = $"Alt+{normalizedSlot}";
        if (ReservedExternalToolAltSlots.Contains(normalizedSlot[0]))
        {
            return "予約スロット";
        }
        if (slotCounts.TryGetValue(normalizedSlot, out int slotCount) && slotCount > 1)
        {
            return "重複";
        }
        if (string.IsNullOrWhiteSpace(tool.ExecutablePath))
        {
            return "実行ファイル未設定";
        }

        try
        {
            if (!Path.IsPathRooted(tool.ExecutablePath))
            {
                return "絶対パスではない";
            }

            string normalizedExePath = Path.GetFullPath(tool.ExecutablePath);
            if (!File.Exists(normalizedExePath))
            {
                return "実行ファイルなし";
            }

            isLaunchable = true;
            return "起動可";
        }
        catch
        {
            return "実行ファイル不正";
        }
    }
    private bool TryResolveExternalToolByAltSlot(
        Keys keyData,
        out ExternalToolCommandDefinition? tool,
        out string slotLabel)
    {
        tool = null;
        slotLabel = string.Empty;
        Keys modifiers = keyData & Keys.Modifiers;
        if (modifiers != Keys.Alt)
        {
            return false;
        }
        Keys keyCode = keyData & Keys.KeyCode;
        if (!TryNormalizeExternalToolAltSlot(keyCode, out string normalizedSlot))
        {
            return false;
        }
        char slotChar = normalizedSlot[0];
        if (ReservedExternalToolAltSlots.Contains(slotChar))
        {
            return false;
        }
        var store = ExternalToolCommandStorage.Load();
        if (store?.Tools == null || store.Tools.Count == 0)
        {
            return false;
        }
        var match = store.Tools.FirstOrDefault(t =>
        {
            if (!t.Enabled || string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.ExecutablePath))
            {
                return false;
            }
            return TryNormalizeExternalToolAltSlot(t.AltSlot, out string? toolSlot)
                && string.Equals(toolSlot, normalizedSlot, StringComparison.OrdinalIgnoreCase);
        });
        if (match == null)
        {
            return false;
        }
        slotLabel = $"Alt+{normalizedSlot}";
        tool = match;
        return true;
    }
    private static bool TryNormalizeExternalToolAltSlot(Keys keyCode, out string normalizedSlot)
    {
        normalizedSlot = string.Empty;
        if (keyCode is >= Keys.A and <= Keys.Z)
        {
            normalizedSlot = ((char)('A' + (keyCode - Keys.A))).ToString();
            return true;
        }
        if (keyCode is >= Keys.D0 and <= Keys.D9)
        {
            normalizedSlot = ((char)('0' + (keyCode - Keys.D0))).ToString();
            return true;
        }
        if (keyCode is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            normalizedSlot = ((char)('0' + (keyCode - Keys.NumPad0))).ToString();
            return true;
        }
        return false;
    }
    private static bool TryNormalizeExternalToolAltSlot(string? slot, out string normalizedSlot)
    {
        normalizedSlot = string.Empty;
        if (string.IsNullOrWhiteSpace(slot))
        {
            return false;
        }
        string trimmed = slot.Trim();
        if (trimmed.Length != 1)
        {
            return false;
        }
        char c = char.ToUpperInvariant(trimmed[0]);
        if ((c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9'))
        {
            normalizedSlot = c.ToString();
            return true;
        }
        return false;
    }
    private IReadOnlyList<ExternalToolAltHintRow> BuildExternalToolAltHintRows()
    {
        var store = ExternalToolCommandStorage.Load();
        if (store?.Tools == null || store.Tools.Count == 0)
        {
            return Array.Empty<ExternalToolAltHintRow>();
        }
        var slotCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ExternalToolCommandDefinition tool in store.Tools)
        {
            if (TryNormalizeExternalToolAltSlot(tool.AltSlot, out string slot))
            {
                slotCounts[slot] = slotCounts.TryGetValue(slot, out int count) ? count + 1 : 1;
            }
        }
        var rows = new List<ExternalToolAltHintRow>();
        foreach (ExternalToolCommandDefinition tool in store.Tools)
        {
            string displayName = string.IsNullOrWhiteSpace(tool.DisplayName)
                ? (string.IsNullOrWhiteSpace(tool.Id) ? "(ID未設定)" : tool.Id)
                : tool.DisplayName;
            string executableName = string.IsNullOrWhiteSpace(tool.ExecutablePath)
                ? "(未設定)"
                : Path.GetFileName(tool.ExecutablePath);
            string status = BuildExternalToolAltStatus(tool, slotCounts, out string slotLabel, out bool isLaunchable);
            rows.Add(new ExternalToolAltHintRow(
                slotLabel,
                displayName,
                executableName,
                status,
                isLaunchable,
                tool));
        }
        return rows
            .OrderByDescending(static x => x.IsLaunchable)
            .ThenBy(static x => x.SlotLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    private void ToggleMark(bool moveNext)
    {
        var item = GetCurrentBrowserItem();
        if (item == null) return;
        // .. はマーク対象外
        if (item.Text == "..")
        {
            if (moveNext && _uiMode == UIMode.Browser)
            {
                int total = _browserTotalItemCount > 0 ? _browserTotalItemCount : fileListView.Items.Count;
                if (_browserCursorIndex < total - 1)
                {
                    SetBrowserGlobalCursorIndex(_browserCursorIndex + 1);
                }
            }
            return;
        }
        string? fullPath = item.Tag as string;
        if (fullPath != null)
        {
            if (_markedFiles.Contains(fullPath))
            {
                UnmarkPath(fullPath);
            }
            else
            {
                MarkPath(fullPath);
            }
            ApplyMarkColor(item, fullPath);
            RefreshMarkUi(); // Phase 2g-fix6.2b: 即時反映 (moveNext:false経路等に対応)
        }
        if (moveNext && _uiMode == UIMode.Browser)
        {
            int total = _browserTotalItemCount > 0 ? _browserTotalItemCount : fileListView.Items.Count;
            if (_browserCursorIndex < total - 1)
            {
                SetBrowserGlobalCursorIndex(_browserCursorIndex + 1);
            }
        }
        PrimeRecentMultiMarkIntent();
    }
    private void RefreshMarkUi()
    {
        browserPanel.Invalidate();
    }
    private void RefreshHeaderDisplay()
    {
        LayoutHeaderZones();
        contentFramePanel.Invalidate();
        titleHeaderPanel.Invalidate();
        headerPanel.Invalidate();
        topPanel.Invalidate();
        headerZone1.Invalidate();
        headerZone2.Invalidate();
        headerZone3.Invalidate();
        headerZone4.Invalidate();
    }
    private bool MarkPath(string path)
    {
        MarkSummaryDelta? summaryDelta = TryPrepareMarkSummaryDelta(path, adding: true, out MarkSummaryDelta delta)
            ? delta
            : null;
        bool changed = _markedFiles.Add(path);
        if (changed)
        {
            CommitMarkStateChange(summaryDelta: summaryDelta);
        }
        return changed;
    }
    private bool UnmarkPath(string path)
    {
        MarkSummaryDelta? summaryDelta = TryPrepareMarkSummaryDelta(path, adding: false, out MarkSummaryDelta delta)
            ? delta
            : null;
        bool changed = _markedFiles.Remove(path);
        if (changed)
        {
            CommitMarkStateChange(summaryDelta: summaryDelta);
        }
        return changed;
    }

    private bool TryPrepareMarkSummaryDelta(string path, bool adding, out MarkSummaryDelta delta)
    {
        delta = default;
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        if (NetworkPathResolutionPolicy.IsAuxiliaryResolutionDeferred(_navigationService.CurrentPath) ||
            NetworkPathResolutionPolicy.IsUncPath(path))
        {
            return false;
        }

        MarkSummaryExactCache current = _markedFiles.Count == 0
            ? new MarkSummaryExactCache(0, 0, 0, 0)
            : _markSummaryCacheState == MarkSummaryCacheState.Complete &&
              _markSummaryCacheCount == _markedFiles.Count &&
              string.Equals(_markSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase)
                ? new MarkSummaryExactCache(
                    _markSummaryCacheTotalSize,
                    _markSummaryCacheFileCount,
                    _markSummaryCacheOutsideCount,
                    _markSummaryCacheCount)
                : default;
        if (_markedFiles.Count > 0 && current.MarkCount != _markedFiles.Count)
        {
            return false;
        }

        bool isFile = File.Exists(path);
        if (isFile)
        {
            try
            {
                long fileSize = new FileInfo(path).Length;
                bool isOutside = !string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                    currentDir,
                    StringComparison.OrdinalIgnoreCase);
                int direction = adding ? 1 : -1;
                delta = new MarkSummaryDelta(
                    checked(fileSize * direction),
                    direction,
                    isOutside ? direction : 0,
                    direction);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        bool directoryOutside = !string.Equals(
            NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
            currentDir,
            StringComparison.OrdinalIgnoreCase);
        int directoryDirection = adding ? 1 : -1;
        delta = new MarkSummaryDelta(
            0,
            0,
            directoryOutside ? directoryDirection : 0,
            directoryDirection);
        return true;
    }

    private bool TryApplyMarkSummaryDelta(MarkSummaryDelta delta)
    {
        MarkSummaryExactCache current = _markedFiles.Count == 0
            ? new MarkSummaryExactCache(0, 0, 0, 0)
            : new MarkSummaryExactCache(
                _markSummaryCacheTotalSize,
                _markSummaryCacheFileCount,
                _markSummaryCacheOutsideCount,
                _markSummaryCacheCount);
        if (!MarkSummaryDeltaGate.TryApply(current, delta, _markedFiles.Count, out MarkSummaryExactCache updated))
        {
            return false;
        }

        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        SetCompleteMarkSummaryCache(currentDir, updated);
        return true;
    }

    private void SetCompleteMarkSummaryCache(string currentDir, MarkSummaryExactCache updated)
    {
        _markSummaryCacheTotalSize = updated.TotalSize;
        _markSummaryCacheFileCount = updated.FileCount;
        _markSummaryCacheOutsideCount = updated.OutsideCount;
        _markSummaryCache = updated.MarkCount == 0
            ? string.Empty
            : $"Mark:{updated.MarkCount,3} ({updated.FileCount} Files)" +
              (updated.OutsideCount > 0 ? $" Out:{updated.OutsideCount}" : string.Empty) +
              $" {FileOperationService.FormatSize(updated.TotalSize)}";
        _markSummaryCacheCount = updated.MarkCount;
        _markSummaryCacheSizeText = updated.MarkCount == 0
            ? string.Empty
            : FileOperationService.FormatSize(updated.TotalSize);
        _markSummaryCacheCompact = updated.MarkCount == 0
            ? string.Empty
            : $"Mark: {updated.MarkCount} MarkSize: {_markSummaryCacheSizeText}";
        _markSummaryCachePath = currentDir;
        _markSummaryCacheState = MarkSummaryCacheState.Complete;
    }

    private void CommitMarkStateChange(int changedCount = 1, MarkSummaryDelta? summaryDelta = null)
    {
        bool exactDeltaApplied = false;
        _ = _markOperationEffectCoordinator.ExecuteMutation(
            changedCount: changedCount,
            markCommit: () =>
            {
                InvalidateMarkSummaryCache();
                if (summaryDelta.HasValue)
                {
                    exactDeltaApplied = TryApplyMarkSummaryDelta(summaryDelta.Value);
                }
                InvalidateRecentMultiMarkIntent();
                ClearPendingEscExitMarkPersistence();
                _browserMarkInteractionController.SyncMarkState(hasMarks: _markedFiles.Count > 0);
            },
            activeTabSync: SyncActiveBrowserTabMarksFromCurrentSelection,
            infoUpdateSchedule: () =>
            {
                if (exactDeltaApplied)
                {
                    UpdateInfoPanel();
                }
                else
                {
                    ScheduleUpdateInfoPanelDebounced();
                }
            });
    }
    private void UnmarkPathsInBulk(IReadOnlyList<string> paths, string reason)
    {
        if (paths.Count == 0)
        {
            return;
        }
        int removedCount = _markedFiles.RemoveRange(paths);
        if (removedCount <= 0)
        {
            return;
        }
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        ClearPendingEscExitMarkPersistence();
        _browserMarkInteractionController.SyncMarkState(hasMarks: _markedFiles.Count > 0);
        SyncActiveBrowserTabMarksFromCurrentSelection();
        UpdateInfoPanel();
        LogService.Info($"[MoveHotpath] BulkUnmark reason={reason} requested={paths.Count} removed={removedCount}");
    }
    private void ClearMarks(
        bool invalidateRedo = true,
        bool preservePendingEscExitState = false,
        bool updateInfoPanel = true)
    {
        if (_markedFiles.Count == 0) return;
        _markedFiles.Clear();
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        if (!preservePendingEscExitState)
        {
            ClearPendingEscExitMarkPersistence();
        }
        _browserMarkInteractionController.SyncMarkState(hasMarks: false);
        SyncActiveBrowserTabMarksFromCurrentSelection();
        SetZeroMarkSummaryCache();
        if (updateInfoPanel)
        {
            UpdateInfoPanel();
        }
    }
    private void RestoreMarks(IEnumerable<string> paths, bool invalidateRedo = true)
    {
        _markedFiles.Restore(paths);
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        ClearPendingEscExitMarkPersistence();
        _browserMarkInteractionController.SyncMarkState(hasMarks: _markedFiles.Count > 0);
        SyncActiveBrowserTabMarksFromCurrentSelection();
    }
    private void SyncActiveBrowserTabMarksFromCurrentSelection()
    {
        if (_browserTabViewState.ActiveTabIndex < 0 || _browserTabViewState.ActiveTabIndex >= _browserTabViewState.Count)
        {
            return;
        }
        BrowserTabState activeState = _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex];
        activeState.MarkedPaths = _markedFiles.Snapshot().ToList();
        activeState.MarksDirty = true;
    }
    private int CountMarksOutsideCurrentDirectory()
    {
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        int outsideCount = 0;
        foreach (var path in _markedFiles)
        {
            string? parentDir = Path.GetDirectoryName(path);
            if (!string.Equals(
                NavigationService.NormalizeDirectoryForCompare(parentDir ?? string.Empty),
                currentDir,
                StringComparison.OrdinalIgnoreCase))
            {
                outsideCount++;
            }
        }
        return outsideCount;
    }
    private void RefreshVisibleMarkColors()
    {
        foreach (ListViewItem item in fileListView.Items)
        {
            if (item.Tag is string fullPath)
            {
                ApplyMarkColor(item, fullPath);
            }
        }
        fileListView.Invalidate();
        browserPanel.Invalidate();
    }
    private void OpenMarkSlotDialog()
    {
        HideCommandHintOverlay("OpenMarkSlotDialog");
        using var dialog = new MarkSlotDialog(
            BuildMarkSlotDialogItems,
            BuildMarkSlotSummaryItems,
            BuildMarkSlotContentItems,
            BuildMarkPersistenceSummaryText,
            ToggleCurrentMarksFromDialog,
            NavigateToMarkedItemFromDialog,
            SaveCurrentMarksToSlot,
            SaveCurrentCategoryMarksToSlot,
            SaveWorkspaceMarksToSlot,
            OpenMarkSlotSetOperationDialog,
            _featureGate.IsEnabled(FeatureId.MarkSlotSetOperations),
            ExportMarkSlot,
            ImportMarkSlot,
            ExportAllMarkSlots,
            ImportAllMarkSlots,
            _featureGate.IsEnabled(FeatureId.MarkSlotBackupTransfer),
            RestoreMarksFromSlot,
            RenameMarkSlot,
            DeleteMarkSlot,
            RemoveMarkSlotItems,
            BuildMarkGlobalSummary,
            ClearCategoryMarksFromDialog,
            ClearGlobalMarksFromDialog,
             ClearCurrentTabMarksFromDialog,
             AuthorToolsEnabled ? ImportClipboardPathsToCurrentMarks : null);
        dialog.ShowDialog(this);
    }
    private sealed record MarkSlotSaveAggregationResult(
        string SourceScope,
        string SourceScopeLabel,
        string? SourceCategoryId,
        string? SourceCategoryName,
        int CategoryCount,
        int TabCount,
        int RawMarkCount,
        List<string> Paths)
    {
        public int UniquePathCount => Paths.Count;
    }
    private MarkSlotDialog.MarkGlobalSummary BuildMarkGlobalSummary()
    {
        // 集計前に現在アクティブなカテゴリの状態を同期し、snapshot を最新化する
        // (現在カテゴリ内の非アクティブタブのマーク情報を snapshot へ反映させるため)
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        int activeTabMarkCount = _markedFiles.Count;
        int currentCategoryMarkCount = 0;
        int currentCategoryTabCount = 0;
        int globalMarkCount = 0;
        int globalTabCount = 0;
        int globalCategoryCount = 0;
        string currentCategoryName = "既定";
        var snapshot = _settings.Session?.BrowserTabRestoreSnapshot;
        if (snapshot != null)
        {
            globalCategoryCount = snapshot.Categories.Count;
            foreach (var category in snapshot.Categories)
            {
                bool isCurrentCategory = string.Equals(category.Id, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase);
                if (isCurrentCategory)
                {
                    currentCategoryName = category.DisplayName;
                }
                foreach (var tab in category.OpenTabs)
                {
                    globalTabCount++;
                    int markCount;
                    // 現在のタブは _markedFiles が最新
                    if (isCurrentCategory && tab.TabId == (_browserTabViewState.ActiveTabIndex >= 0 && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count ? _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex].Id : Guid.Empty))
                    {
                        markCount = activeTabMarkCount;
                    }
                    else
                    {
                        markCount = tab.MarkedPaths?.Count ?? 0;
                    }
                    if (markCount > 0)
                    {
                        LogService.Info($"[MarkGlobalSummary] Found marks in Category={category.DisplayName} TabId={tab.TabId} Path={tab.CurrentPath} Count={markCount}");
                    }
                    globalMarkCount += markCount;
                    if (isCurrentCategory)
                    {
                        currentCategoryTabCount++;
                        currentCategoryMarkCount += markCount;
                    }
                }
            }
        }
        else
        {
            // Snapshot がない場合は現在のアクティブタブのみ集計（通常はありえないが安全のため）
            currentCategoryMarkCount = activeTabMarkCount;
            currentCategoryTabCount = 1;
            globalMarkCount = activeTabMarkCount;
            globalTabCount = 1;
            globalCategoryCount = 1;
        }
        return new MarkSlotDialog.MarkGlobalSummary(
            activeTabMarkCount,
            currentCategoryMarkCount,
            currentCategoryTabCount,
            currentCategoryName,
            globalMarkCount,
            globalCategoryCount,
            globalTabCount);
    }
    private void ClearCategoryMarksFromDialog()
    {
        CaptureActiveBrowserTabState();
        bool changed = false;
        int clearedCount = 0;
        // 1. 現在メモリ上で管理されているタブの状態をクリア
        foreach (var tab in _browserTabViewState.Tabs)
        {
            if (tab.MarkedPaths != null && tab.MarkedPaths.Count > 0)
            {
                clearedCount += tab.MarkedPaths.Count;
                tab.MarkedPaths.Clear();
                changed = true;
            }
        }
        // 2. 現在のアクティブタブのマーク管理インスタンスをクリア
        if (_markedFiles.Count > 0)
        {
            ClearMarks(invalidateRedo: false);
            changed = true;
        }
        // 3. Snapshot (BrowserTabRestoreSnapshot) をクリア
        var snapshot = _settings.Session?.BrowserTabRestoreSnapshot;
        if (snapshot != null)
        {
            foreach (var category in snapshot.Categories)
            {
                if (string.Equals(category.Id, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var tab in category.OpenTabs)
                    {
                        if (tab.MarkedPaths != null && tab.MarkedPaths.Count > 0)
                        {
                            tab.MarkedPaths.Clear();
                            changed = true;
                        }
                    }
                }
            }
        }
        // 4. Session mirror (BrowserTabCategories) をクリア
        if (_settings.Session?.BrowserTabCategories != null)
        {
            foreach (var category in _settings.Session.BrowserTabCategories)
            {
                if (string.Equals(category.CategoryId, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var tab in category.OpenTabs)
                    {
                        if (tab.MarkedPaths != null && tab.MarkedPaths.Count > 0)
                        {
                            tab.MarkedPaths.Clear();
                            changed = true;
                        }
                    }
                }
            }
        }
        if (changed)
        {
            StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
            SaveWorkspaceStateStore();
            RefreshMarkUi();
            RefreshBrowserTabHeaders();
            ShowStatusMessage($"カテゴリ '{_categoryViewState.ActiveCategoryId}' のマークをすべて解除しました ({clearedCount}件)。");
        }
    }
    private void ClearGlobalMarksFromDialog()
    {
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        bool changed = false;
        int clearedCount = 0;
        // 1. 現在メモリ上で管理されているタブの状態をクリア
        foreach (var tab in _browserTabViewState.Tabs)
        {
            if (tab.MarkedPaths != null && tab.MarkedPaths.Count > 0)
            {
                clearedCount += tab.MarkedPaths.Count;
                tab.MarkedPaths.Clear();
                changed = true;
            }
        }
        // 2. 現在のアクティブタブのマーク管理インスタンスをクリア
        if (_markedFiles.Count > 0)
        {
            ClearMarks(invalidateRedo: false);
            changed = true;
        }
        // 3. Snapshot (BrowserTabRestoreSnapshot) をクリア
        var snapshot = _settings.Session?.BrowserTabRestoreSnapshot;
        if (snapshot != null)
        {
            foreach (var category in snapshot.Categories)
            {
                foreach (var tab in category.OpenTabs)
                {
                    if (tab.MarkedPaths != null && tab.MarkedPaths.Count > 0)
                    {
                        tab.MarkedPaths.Clear();
                        changed = true;
                    }
                }
            }
        }
        // 4. Session mirror (BrowserTabCategories) をクリア
        if (_settings.Session?.BrowserTabCategories != null)
        {
            foreach (var category in _settings.Session.BrowserTabCategories)
            {
                foreach (var tab in category.OpenTabs)
                {
                    if (tab.MarkedPaths != null && tab.MarkedPaths.Count > 0)
                    {
                        tab.MarkedPaths.Clear();
                        changed = true;
                    }
                }
            }
        }
        // 5. Session mirror (OpenTabs - 旧互換用) をクリア
        if (_settings.Session?.OpenTabs != null)
        {
            foreach (var tab in _settings.Session.OpenTabs)
            {
                if (tab.MarkedPaths != null && tab.MarkedPaths.Count > 0)
                {
                    tab.MarkedPaths.Clear();
                    changed = true;
                }
            }
        }
        if (changed)
        {
            StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
            SaveWorkspaceStateStore();
            RefreshMarkUi();
            RefreshBrowserTabHeaders();
            ShowStatusMessage($"Workspace 全域の全マークを解除しました ({clearedCount}件)。");
        }
    }
    private void ClearCurrentTabMarksFromDialog()
    {
        int clearedCount = _markedFiles.Count;
        if (clearedCount <= 0)
        {
            return;
        }
        ClearMarks(invalidateRedo: false);
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        SaveWorkspaceStateStore();
        RefreshBrowserTabHeaders();
        ShowStatusMessage($"現在タブのマークをすべて解除しました ({clearedCount}件)。");
    }
    private IReadOnlyList<MarkSlotDialog.MarkListViewItem> BuildMarkSlotDialogItems()
    {
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        return _markedFiles
            .Select(path =>
            {
                string? parentDir = Path.GetDirectoryName(path);
                bool isInCurrentDirectory = string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(parentDir ?? string.Empty),
                    currentDir,
                    StringComparison.OrdinalIgnoreCase);
                bool exists = PathExists(path);
                string name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = path;
                }
                return new MarkSlotDialog.MarkListViewItem(name, path, isInCurrentDirectory, exists);
            })
            .OrderByDescending(static item => item.IsInCurrentDirectory)
            .ThenByDescending(static item => item.Exists)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    private string BuildMarkPersistenceSummaryText()
    {
        _settings.Session ??= new SessionSettings();
        int currentCount = _markedFiles.Count;
        int savedCount = _settings.Session.PersistedMarkedPaths?
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() ?? 0;
        if (_settings.Session.PersistMarksAcrossRestart)
        {
            return $"再起動復元: ON / 現在 {currentCount} 件 / 保存済み {savedCount} 件{Environment.NewLine}終了時に保存し、次回起動時は存在する path だけ復元します";
        }
        return $"再起動復元: OFF / 保存済み {savedCount} 件を保持中{Environment.NewLine}ON に戻すまで自動復元しません。";
    }
    private IReadOnlyList<MarkSlotDialog.MarkSlotSummaryViewItem> BuildMarkSlotSummaryItems()
    {
        return _markSlotStore.Slots
            .OrderBy(static slot => slot.SlotNumber)
            .Select(slot => new MarkSlotDialog.MarkSlotSummaryViewItem(
                slot.SlotNumber,
                GetMarkSlotDisplayName(slot),
                slot.Paths.Count,
                slot.SavedAtUtc?.ToLocalTime(),
                GetMarkSlotSourceScopeLabel(slot.SourceScope),
                slot.SourceCategoryName,
                slot.SourceTabDisplayName,
                string.IsNullOrWhiteSpace(slot.SourceScope)))
            .ToList();
    }
    private IReadOnlyList<MarkSlotDialog.MarkListViewItem> BuildMarkSlotContentItems(int slotNumber)
    {
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        return GetOrCreateMarkSlot(slotNumber).Paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                string? parentDir = Path.GetDirectoryName(path);
                bool isInCurrentDirectory = string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(parentDir ?? string.Empty),
                    currentDir,
                    StringComparison.OrdinalIgnoreCase);
                bool exists = PathExists(path);
                return new MarkSlotDialog.MarkListViewItem(
                    Path.GetFileName(path),
                    path,
                    isInCurrentDirectory,
                    exists);
            })
            .OrderBy(static item => item.IsInCurrentDirectory ? 0 : 1)
            .ThenBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static item => item.FullPath, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
    private string SaveCurrentMarksToSlot(int slotNumber, string? displayName)
    {
        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        BrowserTabState? activeTab = GetActiveBrowserTab();
        List<string> currentPaths = _markedFiles.Snapshot()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        slot.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? $"スロット {slotNumber}"
            : displayName.Trim();
        slot.SavedAtUtc = DateTime.UtcNow;
        slot.Paths = currentPaths;
        slot.SourceScope = MarkSlotSourceScopes.CurrentTab;
        slot.SourceCategoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        slot.SourceCategoryName = GetActiveBrowserTabCategoryDisplayName();
        slot.SourceTabId = activeTab?.Id;
        slot.SourceTabDisplayName = GetBrowserTabDisplayName(activeTab);
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);
        string message = $"マークスロット {slotNumber} に保存しました ({currentPaths.Count}件)";
        LogService.Info($"[MarkSlots] Saved Slot={slotNumber} Count={currentPaths.Count}");
        ShowStatusMessage(message);
        return message;
    }
    private string SaveCurrentCategoryMarksToSlot(int slotNumber)
    {
        MarkSlotSaveAggregationResult aggregation = BuildCurrentCategoryMarkSlotAggregation();
        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        string defaultName = BuildDefaultMarkSlotDisplayName(slot, aggregation.SourceScope, aggregation.SourceCategoryName);
        string? displayName = SimpleInputDialog.ShowNullable(
            BuildScopedSlotSavePrompt(slotNumber, slot, aggregation),
            "カテゴリ全マークをスロットへ保存",
            defaultName);
        if (displayName == null)
        {
            return string.Empty;
        }
        slot.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? defaultName
            : displayName.Trim();
        slot.SavedAtUtc = DateTime.UtcNow;
        slot.Paths = aggregation.Paths;
        slot.SourceScope = MarkSlotSourceScopes.CurrentCategory;
        slot.SourceCategoryId = aggregation.SourceCategoryId;
        slot.SourceCategoryName = aggregation.SourceCategoryName;
        slot.SourceTabId = null;
        slot.SourceTabDisplayName = null;
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);
        string message = $"マークスロット {slotNumber} に現在カテゴリの全マークを保存しました (raw {aggregation.RawMarkCount}件 / 保存 {aggregation.UniquePathCount}件)";
        LogService.Info($"[MarkSlots] Saved Slot={slotNumber} Scope={aggregation.SourceScope} Raw={aggregation.RawMarkCount} Unique={aggregation.UniquePathCount}");
        ShowStatusMessage(message);
        return message;
    }
    private string SaveWorkspaceMarksToSlot(int slotNumber)
    {
        MarkSlotSaveAggregationResult aggregation = BuildWorkspaceMarkSlotAggregation();
        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        string defaultName = BuildDefaultMarkSlotDisplayName(slot, aggregation.SourceScope, aggregation.SourceCategoryName);
        string? displayName = SimpleInputDialog.ShowNullable(
            BuildScopedSlotSavePrompt(slotNumber, slot, aggregation),
            "Workspace全マークをスロットへ保存",
            defaultName);
        if (displayName == null)
        {
            return string.Empty;
        }
        slot.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? defaultName
            : displayName.Trim();
        slot.SavedAtUtc = DateTime.UtcNow;
        slot.Paths = aggregation.Paths;
        slot.SourceScope = MarkSlotSourceScopes.Workspace;
        slot.SourceCategoryId = null;
        slot.SourceCategoryName = null;
        slot.SourceTabId = null;
        slot.SourceTabDisplayName = null;
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);
        string message = $"マークスロット {slotNumber} にWorkspace全体の全マークを保存しました (raw {aggregation.RawMarkCount}件 / 保存 {aggregation.UniquePathCount}件)";
        LogService.Info($"[MarkSlots] Saved Slot={slotNumber} Scope={aggregation.SourceScope} Raw={aggregation.RawMarkCount} Unique={aggregation.UniquePathCount}");
        ShowStatusMessage(message);
        return message;
    }
    private MarkSlotActionResult RestoreMarksFromSlot(int slotNumber)
    {
        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        List<string> slotPaths = slot.Paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (slotPaths.Count == 0)
        {
            string emptyMessage = $"マークスロット {slotNumber} は空です。";
            return new MarkSlotActionResult(false, emptyMessage);
        }
        List<string> restoredPaths = new();
        int missingCount = 0;
        foreach (string path in slotPaths)
        {
            if (PathExists(path))
            {
                restoredPaths.Add(path);
            }
            else
            {
                missingCount++;
            }
        }
        ClearMarks(updateInfoPanel: false);
        RestoreMarks(restoredPaths);
        UpdateInfoPanel();
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        PrimeRecentMultiMarkIntent();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        SaveWorkspaceStateStore();
        string message = missingCount > 0
            ? $"マークスロット {slotNumber} を復元しました ({restoredPaths.Count}件 / {missingCount}件見つからず)"
            : $"マークスロット {slotNumber} を復元しました ({restoredPaths.Count}件)";
        LogService.Info($"[MarkSlots] Restored Slot={slotNumber} Count={restoredPaths.Count} Missing={missingCount}");
        ShowStatusMessage(message);
        return new MarkSlotActionResult(true, message);
    }
    private MarkSlotClipboardActionResult ImportClipboardPathsToCurrentMarks()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                return ImportFailure("Clipboardにテキストがありません。", null);
            }
            text = Clipboard.GetText(TextDataFormat.UnicodeText);
        }
        catch (Exception ex)
        {
            LogService.Error("Clipboard path import failed.", ex);
            return ImportFailure("Clipboardを読み取れませんでした。", null);
        }

        string? repositoryRoot = FindRepositoryRoot(_navigationService.CurrentPath);
        MarkSlotClipboardImportResult importResult = MarkSlotClipboardImportService.Extract(
            text,
            _navigationService.CurrentPath,
            repositoryRoot,
            AppContext.BaseDirectory);
        if (!importResult.IsSuccess)
        {
            string failMessage;
            if (importResult.FatalCount > 0)
            {
                failMessage = "変更entryに構文不正またはrepo外pathがあります。現在MarkとMarkSlotは変更していません。";
            }
            else if (importResult.Paths.Count == 0)
            {
                failMessage = $"Mark可能な既存fileがありません（削除・不存在{importResult.MissingFileCount}件、directory{importResult.DirectoryPathCount}件）。現在MarkとMarkSlotは変更していません。";
            }
            else
            {
                string reason = importResult.FailureReason switch
                {
                    MarkSlotClipboardImportFailureReason.KdslResultNotFound => "KDSL_RESULT未検出",
                    MarkSlotClipboardImportFailureReason.KdslResultFenceUnclosed => "KDSL_RESULT fence未閉鎖",
                    MarkSlotClipboardImportFailureReason.ChangeSectionNotFound => "変更section未検出",
                    _ => "取込条件不適合"
                };
                failMessage = $"{reason}。現在MarkとMarkSlotは変更していません。";
            }

            LogService.Warn($"[MarkSlots] KdslResultImportFailed Reason={importResult.FailureReason} Valid={importResult.Paths.Count} Missing={importResult.MissingFileCount} Directory={importResult.DirectoryPathCount} Duplicate={importResult.DuplicatePathCount} Fatal={importResult.FatalCount} IgnoredEarlier={importResult.IgnoredEarlierResultCount}");
            return new MarkSlotClipboardActionResult(
                false,
                failMessage,
                Array.Empty<string>(),
                repositoryRoot,
                importResult.MissingFileCount,
                importResult.DirectoryPathCount,
                importResult.DuplicatePathCount,
                importResult.IgnoredEarlierResultCount);
        }

        IReadOnlyList<string> mergedPaths = _markedFiles.Snapshot()
            .Concat(importResult.Paths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyBulkMarkState(mergedPaths, "KdslResultAddToCurrentMarks", importResult.Paths.Count, 0, Stopwatch.StartNew());
        RefreshBrowserTabHeaders();
        int unresolvedCount = importResult.UnresolvedPaths?.Count ?? 0;
        string unresolvedInfo = unresolvedCount > 0 ? $"（未解決{unresolvedCount}件）" : string.Empty;
        string message = $"RESULTのpathを現在Markへ追加しました（合計{mergedPaths.Count}件）{unresolvedInfo}。MarkSlotは変更していません。";
        LogService.Info($"[MarkSlots] KdslResultAdded CurrentMark Count={mergedPaths.Count} Imported={importResult.Paths.Count} Missing={importResult.MissingFileCount} Directory={importResult.DirectoryPathCount} Duplicate={importResult.DuplicatePathCount} Fatal={importResult.FatalCount} IgnoredEarlier={importResult.IgnoredEarlierResultCount}");
        return new MarkSlotClipboardActionResult(
            true,
            message,
            mergedPaths,
            repositoryRoot,
            importResult.MissingFileCount,
            importResult.DirectoryPathCount,
            importResult.DuplicatePathCount,
            importResult.IgnoredEarlierResultCount,
            importResult.UnresolvedPaths);
    }

    private static MarkSlotClipboardActionResult ImportFailure(string message, string? repositoryRoot) =>
        new(false, message, Array.Empty<string>(), repositoryRoot, 0, 0, 0, 0);

    private void ShowMarkSlotImportResult(MarkSlotClipboardActionResult result)
    {
        if (!result.Success || result.Paths.Count == 0) return;
        using var dialog = new MarkSlotImportResultDialog(result);
        dialog.ShowDialog(this);
    }
    private static string? FindRepositoryRoot(string startPath)
    {
        string fullPath = Path.GetFullPath(startPath);
        DirectoryInfo? directory = File.Exists(fullPath)
            ? Directory.GetParent(fullPath)
            : new DirectoryInfo(fullPath);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }
    private void OpenMarkSlotSetOperationDialog(int preferredSlotNumber)
    {
        if (GuardFeatureDisabled(FeatureId.MarkSlotSetOperations, "標準機能（推奨）では MarkSlot 集合演算は無効です。"))
        {
            return;
        }
        using var dialog = new MarkSlotSetOperationDialog(
            BuildMarkSlotSummaryItems,
            BuildMarkSlotSetOperationPreview,
            SaveMarkSlotSetOperationResult,
            ApplyMarkSlotSetOperationResultToCurrentTab,
            preferredSlotNumber);
        dialog.ShowDialog(this);
    }
    private string ExportMarkSlot(int slotNumber)
    {
        if (GuardFeatureDisabled(FeatureId.MarkSlotBackupTransfer, "標準機能（推奨）では MarkSlot エクスポートは無効です。"))
        {
            return "標準機能（推奨）では MarkSlot エクスポートは無効です。";
        }
        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        if (slot.Paths.Count == 0)
        {
            const string emptyMessage = "空のマークスロットはエクスポートできません。";
            ShowStatusMessage(emptyMessage);
            return emptyMessage;
        }
        using var dialog = new SaveFileDialog
        {
            Title = $"マークスロット {slotNumber} をエクスポート",
            Filter = "Mark Slot Export (*.json)|*.json|すべてのファイル (*.*)|*.*",
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildMarkSlotExportFileName(slot)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return string.Empty;
        }
        if (!MarkSlotStorage.TryExportSlot(dialog.FileName, slot, out string errorMessage))
        {
            MessageBox.Show(this, errorMessage, "マークスロットエクスポート", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowStatusMessage(errorMessage);
            return errorMessage;
        }
        string message = $"マークスロット {slotNumber} をエクスポートしました";
        LogService.Info($"[MarkSlots] Exported Slot={slotNumber} File={dialog.FileName}");
        ShowStatusMessage(message);
        return message;
    }
    private string ImportMarkSlot(int slotNumber)
    {
        if (GuardFeatureDisabled(FeatureId.MarkSlotBackupTransfer, "標準機能（推奨）では MarkSlot インポートは無効です。"))
        {
            return "標準機能（推奨）では MarkSlot インポートは無効です。";
        }
        using var dialog = new OpenFileDialog
        {
            Title = $"マークスロット {slotNumber} へインポート",
            Filter = "Mark Slot Export (*.json)|*.json|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return string.Empty;
        }
        if (!MarkSlotStorage.TryImportSlot(dialog.FileName, out MarkSlotEntry? importedSlot, out string errorMessage, out string? warningMessage) ||
            importedSlot == null)
        {
            MessageBox.Show(this, errorMessage, "マークスロットインポート", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowStatusMessage(errorMessage);
            return errorMessage;
        }
        string confirmMessage = BuildMarkSlotImportConfirmationMessage(slotNumber, importedSlot);
        DialogResult result = MessageBox.Show(
            this,
            confirmMessage,
            "マークスロットインポート確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return string.Empty;
        }
        MarkSlotEntry targetSlot = GetOrCreateMarkSlot(slotNumber);
        targetSlot.DisplayName = GetMarkSlotDisplayName(importedSlot);
        targetSlot.SavedAtUtc = importedSlot.SavedAtUtc;
        targetSlot.Paths = importedSlot.Paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        targetSlot.SourceScope = importedSlot.SourceScope;
        targetSlot.SourceCategoryId = importedSlot.SourceCategoryId;
        targetSlot.SourceCategoryName = importedSlot.SourceCategoryName;
        targetSlot.SourceTabId = importedSlot.SourceTabId;
        targetSlot.SourceTabDisplayName = importedSlot.SourceTabDisplayName;
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);
        string message = $"マークスロット {slotNumber} にインポートしました ({targetSlot.Paths.Count}件)";
        if (!string.IsNullOrWhiteSpace(warningMessage))
        {
            message += $" / {warningMessage}";
        }
        LogService.Info($"[MarkSlots] Imported Slot={slotNumber} File={dialog.FileName} Count={targetSlot.Paths.Count}");
        if (!string.IsNullOrWhiteSpace(warningMessage))
        {
            LogService.Info($"[MarkSlots] ImportWarning Slot={slotNumber} Message={warningMessage}");
        }
        ShowStatusMessage(message);
        return message;
    }
    private string ExportAllMarkSlots()
    {
        if (GuardFeatureDisabled(FeatureId.MarkSlotBackupTransfer, "標準機能（推奨）では MarkSlot 一括エクスポートは無効です。"))
        {
            return "標準機能（推奨）では MarkSlot 一括エクスポートは無効です。";
        }
        using var dialog = new SaveFileDialog
        {
            Title = "全マークスロットをエクスポート",
            Filter = "Mark Slot Backup (*.json)|*.json|すべてのファイル (*.*)|*.*",
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "MidFD-MarkSlots-BackupSet.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return string.Empty;
        }
        if (!MarkSlotStorage.TryExportAllSlots(dialog.FileName, _markSlotStore, MarkSlotCount, out string errorMessage))
        {
            MessageBox.Show(this, errorMessage, "全マークスロットエクスポート", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowStatusMessage(errorMessage);
            return errorMessage;
        }
        string message = $"全マークスロットをエクスポートしました ({MarkSlotCount}スロット)";
        LogService.Info($"[MarkSlots] ExportedAllSlots File={dialog.FileName} SlotCount={MarkSlotCount}");
        ShowStatusMessage(message);
        return message;
    }
    private string ImportAllMarkSlots()
    {
        if (GuardFeatureDisabled(FeatureId.MarkSlotBackupTransfer, "標準機能（推奨）では MarkSlot 一括インポートは無効です。"))
        {
            return "標準機能（推奨）では MarkSlot 一括インポートは無効です。";
        }
        using var dialog = new OpenFileDialog
        {
            Title = "全マークスロットをインポート",
            Filter = "Mark Slot Backup (*.json)|*.json|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return string.Empty;
        }
        if (!MarkSlotStorage.TryImportAllSlots(dialog.FileName, MarkSlotCount, out MarkSlotStore? importedStore, out string errorMessage, out string? warningMessage) ||
            importedStore == null)
        {
            MessageBox.Show(this, errorMessage, "全マークスロットインポート", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowStatusMessage(errorMessage);
            return errorMessage;
        }
        DialogResult result = MessageBox.Show(
            this,
            "このバックアップを全スロットへインポートします。現在の全スロット内容を置き換えます。\n現在タブのマークは自動変更しません。よろしいですか？",
            "全マークスロットインポート確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
        {
            return string.Empty;
        }
        _markSlotStore.Slots = importedStore.Slots
            .OrderBy(static slot => slot.SlotNumber)
            .Select(static slot => slot.Clone())
            .ToList();
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);
        string message = $"全マークスロットをインポートしました ({MarkSlotCount}スロット置換)";
        if (!string.IsNullOrWhiteSpace(warningMessage))
        {
            message += $" / {warningMessage}";
        }
        LogService.Info($"[MarkSlots] ImportedAllSlots File={dialog.FileName} SlotCount={MarkSlotCount}");
        ShowStatusMessage(message);
        return message;
    }
    private string ApplyMarkSlotSetOperationResultToCurrentTab(MarkSlotSetOperationPreviewResult preview)
    {
        if (preview.ResultCount <= 0)
        {
            const string emptyMessage = "0件の演算結果は現在タブへ適用できません。";
            ShowStatusMessage(emptyMessage);
            return emptyMessage;
        }
        DialogResult result = MessageBox.Show(
            this,
            $"演算結果 {preview.ResultCount} 件で現在タブのマークを置換します。よろしいですか？",
            "現在タブへ適用確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return string.Empty;
        }
        List<string> restoredPaths = preview.ResultPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && PathExists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        int missingCount = preview.ResultPaths.Count - restoredPaths.Count;
        ClearMarks(updateInfoPanel: false);
        RestoreMarks(restoredPaths);
        UpdateInfoPanel();
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        RefreshBrowserTabHeaders();
        PrimeRecentMultiMarkIntent();
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        SaveWorkspaceStateStore();
        string message = missingCount > 0
            ? $"演算結果を現在タブへ適用しました ({restoredPaths.Count}件 / {missingCount}件見つからず)"
            : $"演算結果を現在タブへ適用しました ({restoredPaths.Count}件)";
        LogService.Info($"[MarkSlots] AppliedSlotSetResultToCurrentTab Count={restoredPaths.Count} Missing={missingCount} Op={preview.OperationKind} A={preview.SlotANumber} B={preview.SlotBNumber}");
        ShowStatusMessage(message);
        return message;
    }
    private string ToggleCurrentMarksFromDialog(IReadOnlyList<string> paths)
    {
        List<string> targets = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            return string.Empty;
        }
        int markedCount = 0;
        int unmarkedCount = 0;
        int skippedCount = 0;
        foreach (string path in targets)
        {
            if (_markedFiles.Contains(path))
            {
                if (UnmarkPath(path))
                {
                    unmarkedCount++;
                }
            }
            else if (PathExists(path))
            {
                if (MarkPath(path))
                {
                    markedCount++;
                }
            }
            else
            {
                skippedCount++;
            }
        }
        if (markedCount == 0 && unmarkedCount == 0)
        {
            return string.Empty;
        }
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        PrimeRecentMultiMarkIntent();
        string message = BuildMarkToggleStatusMessage(markedCount, unmarkedCount, skippedCount);
        LogService.Info($"[MarkSlots] ToggledCurrentMarks On={markedCount} Off={unmarkedCount} Skipped={skippedCount}");
        ShowStatusMessage(message);
        return message;
    }
    private static string BuildMarkToggleStatusMessage(int markedCount, int unmarkedCount, int skippedCount)
    {
        string message;
        if (markedCount > 0 && unmarkedCount > 0)
        {
            message = $"現在のマークを更新しました (ON {markedCount}件 / OFF {unmarkedCount}件)";
        }
        else if (markedCount > 0)
        {
            message = markedCount == 1
                ? "現在のマークに 1 件付けました"
                : $"現在のマークに {markedCount} 件付けました";
        }
        else
        {
            message = unmarkedCount == 1
                ? "現在のマークから 1 件外しました"
                : $"現在のマークから {unmarkedCount} 件外しました";
        }
        if (skippedCount > 0)
        {
            message += $" ({skippedCount}件は見つからず)";
        }
        return message;
    }
    private void NavigateToMarkedItemFromDialog(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }
        string? focusTargetName = Path.GetFileName(fullPath);
        string? parentDirectory = Path.GetDirectoryName(fullPath);
        if (Directory.Exists(fullPath))
        {
            string? directoryName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parentDirectory) && Directory.Exists(parentDirectory))
            {
                focusTargetName = directoryName;
            }
            else
            {
                parentDirectory = fullPath;
                focusTargetName = null;
            }
        }
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            ShowStatusMessage("対象フォルダが見つかりません。");
            return;
        }
        if (LoadDirectory(parentDirectory, focusTargetName))
        {
            browserPanel.Focus();
        }
    }
    private string RenameMarkSlot(int slotNumber, string? displayName)
    {
        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        slot.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? $"スロット {slotNumber}"
            : displayName.Trim();
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);
        string message = $"マークスロット {slotNumber} の名前を更新しました";
        LogService.Info($"[MarkSlots] Renamed Slot={slotNumber} Name={slot.DisplayName}");
        ShowStatusMessage(message);
        return message;
    }
    private string DeleteMarkSlot(int slotNumber)
    {
        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        slot.DisplayName = $"スロット {slotNumber}";
        slot.SavedAtUtc = null;
        slot.Paths = new List<string>();
        slot.SourceScope = null;
        slot.SourceCategoryId = null;
        slot.SourceCategoryName = null;
        slot.SourceTabId = null;
        slot.SourceTabDisplayName = null;
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);
        string message = $"マークスロット {slotNumber} を削除しました";
        LogService.Info($"[MarkSlots] Deleted Slot={slotNumber}");
        ShowStatusMessage(message);
        return message;
    }
    private MarkSlotActionResult RemoveMarkSlotItems(int slotNumber, IReadOnlyCollection<string> fullPaths)
    {
        if (fullPaths == null || fullPaths.Count == 0)
        {
            return new MarkSlotActionResult(false, "削除対象のパスが指定されていません。");
        }

        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        var targetSet = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
        int initialCount = slot.Paths.Count;

        slot.Paths.RemoveAll(path => targetSet.Contains(path));

        int removedCount = initialCount - slot.Paths.Count;
        if (removedCount == 0)
        {
            return new MarkSlotActionResult(false, "削除対象のパスがスロット内に見つかりません。");
        }

        slot.SavedAtUtc = DateTime.UtcNow;
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);

        string message = $"マークスロット {slotNumber} から {removedCount} 件の項目を削除しました。";
        LogService.Info($"[MarkSlots] Removed items from Slot={slotNumber} Count={removedCount}");
        ShowStatusMessage(message);
        return new MarkSlotActionResult(true, message);
    }
    private MarkSlotSetOperationPreviewResult BuildMarkSlotSetOperationPreview(int slotANumber, int slotBNumber, string operationKind)
    {
        MarkSlotEntry slotA = GetOrCreateMarkSlot(slotANumber);
        MarkSlotEntry slotB = GetOrCreateMarkSlot(slotBNumber);
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        List<string> slotAPaths = slotA.Paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> slotBPaths = slotB.Paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var aSet = new HashSet<string>(slotAPaths, StringComparer.OrdinalIgnoreCase);
        var bSet = new HashSet<string>(slotBPaths, StringComparer.OrdinalIgnoreCase);
        var resultPaths = new List<string>();
        switch (operationKind)
        {
            case MarkSlotSetOperations.And:
                resultPaths.AddRange(slotAPaths.Where(path => bSet.Contains(path)));
                break;
            case MarkSlotSetOperations.AMinusB:
                resultPaths.AddRange(slotAPaths.Where(path => !bSet.Contains(path)));
                break;
            case MarkSlotSetOperations.BMinusA:
                resultPaths.AddRange(slotBPaths.Where(path => !aSet.Contains(path)));
                break;
            case MarkSlotSetOperations.Xor:
                resultPaths.AddRange(slotAPaths.Where(path => !bSet.Contains(path)));
                resultPaths.AddRange(slotBPaths.Where(path => !aSet.Contains(path)));
                break;
            case MarkSlotSetOperations.Or:
            default:
                resultPaths.AddRange(slotAPaths);
                resultPaths.AddRange(slotBPaths.Where(path => !aSet.Contains(path)));
                break;
        }
        resultPaths = resultPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<MarkSlotSetOperationPreviewItem> previewItems = resultPaths
            .Select(path =>
            {
                string? parentDir = Path.GetDirectoryName(path);
                bool isInCurrentDirectory = string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(parentDir ?? string.Empty),
                    currentDir,
                    StringComparison.OrdinalIgnoreCase);
                bool exists = PathExists(path);
                string name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = path;
                }
                return new MarkSlotSetOperationPreviewItem(name, path, isInCurrentDirectory, exists);
            })
            .OrderByDescending(static item => item.IsInCurrentDirectory)
            .ThenByDescending(static item => item.Exists)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int currentDirectoryCount = previewItems.Count(item => item.IsInCurrentDirectory);
        int missingCount = previewItems.Count(item => !item.Exists);
        int outsideCount = previewItems.Count - currentDirectoryCount;
        return new MarkSlotSetOperationPreviewResult(
            slotANumber,
            GetMarkSlotDisplayName(slotA),
            slotAPaths.Count,
            slotBNumber,
            GetMarkSlotDisplayName(slotB),
            slotBPaths.Count,
            operationKind,
            GetMarkSlotSetOperationLabel(operationKind),
            resultPaths,
            previewItems,
            currentDirectoryCount,
            outsideCount,
            missingCount);
    }
    private string SaveMarkSlotSetOperationResult(MarkSlotSetOperationSaveRequest request)
    {
        List<string> resultPaths = request.ResultPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (resultPaths.Count == 0)
        {
            const string emptyMessage = "0件の演算結果は保存できません。";
            ShowStatusMessage(emptyMessage);
            return emptyMessage;
        }
        MarkSlotEntry targetSlot = GetOrCreateMarkSlot(request.TargetSlotNumber);
        string defaultName = BuildMarkSlotSetOperationDefaultName(request.SlotANumber, request.SlotBNumber, request.OperationKind);
        string? displayName = SimpleInputDialog.ShowNullable(
            $"演算結果 {resultPaths.Count}件をスロット{request.TargetSlotNumber}へ保存します。{Environment.NewLine}表示名を入力してください。",
            "スロット演算結果を保存",
            defaultName,
            new SimpleInputDialog.DisplayOptions(
                SummaryText: "現在タブのマークは変更されません。保存後に反映したい場合は、保存先スロットを選択して復元してください。",
                WarningText: HasMarkSlotSavedState(targetSlot) ? $"スロット {request.TargetSlotNumber} は上書きされます。" : null));
        if (displayName == null)
        {
            return string.Empty;
        }
        DialogResult confirm = MessageBox.Show(
            this,
            $"演算結果 {resultPaths.Count}件をスロット{request.TargetSlotNumber}へ保存します。現在タブのマークは変更されません。よろしいですか？",
            "スロット演算結果の保存確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return string.Empty;
        }
        targetSlot.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? defaultName
            : displayName.Trim();
        targetSlot.SavedAtUtc = DateTime.UtcNow;
        targetSlot.Paths = resultPaths;
        targetSlot.SourceScope = MarkSlotSourceScopes.SlotSetOperation;
        targetSlot.SourceCategoryId = null;
        targetSlot.SourceCategoryName = null;
        targetSlot.SourceTabId = null;
        targetSlot.SourceTabDisplayName = null;
        MarkSlotStorage.Save(_markSlotStore, MarkSlotCount);
        string message = $"演算結果をマークスロット {request.TargetSlotNumber} に保存しました ({resultPaths.Count}件)";
        LogService.Info($"[MarkSlots] SavedSlotSetOperation Target={request.TargetSlotNumber} Op={request.OperationKind} A={request.SlotANumber} B={request.SlotBNumber} Count={resultPaths.Count}");
        ShowStatusMessage(message);
        return message;
    }
    private string BuildMarkSlotImportConfirmationMessage(int slotNumber, MarkSlotEntry importedSlot)
    {
        string displayName = GetMarkSlotDisplayName(importedSlot);
        string sourceScopeLabel = GetMarkSlotSourceScopeLabel(importedSlot.SourceScope);
        return
            $"このファイルの Mark Slot をスロット{slotNumber}へインポートします。{Environment.NewLine}{Environment.NewLine}" +
            $"インポート元: {displayName}{Environment.NewLine}" +
            $"件数: {importedSlot.Paths.Count}件{Environment.NewLine}" +
            $"保存元: {sourceScopeLabel}{Environment.NewLine}" +
            "現在のスロット内容は上書きされます。{Environment.NewLine}" +
            "復元は行いません。よろしいですか？{Environment.NewLine}{Environment.NewLine}" +
            "インポート後に現在タブへ反映するには、スロットを選択して復元してください。";
    }
    private MarkSlotEntry GetOrCreateMarkSlot(int slotNumber)
    {
        MarkSlotEntry? slot = _markSlotStore.Slots.FirstOrDefault(candidate => candidate.SlotNumber == slotNumber);
        if (slot != null)
        {
            return slot;
        }
        slot = new MarkSlotEntry
        {
            SlotNumber = slotNumber,
            DisplayName = $"スロット {slotNumber}"
        };
        _markSlotStore.Slots.Add(slot);
        return slot;
    }
    private static string GetMarkSlotDisplayName(MarkSlotEntry slot)
    {
        return string.IsNullOrWhiteSpace(slot.DisplayName)
            ? $"スロット {slot.SlotNumber}"
            : slot.DisplayName.Trim();
    }
    private static string BuildMarkSlotSetOperationDefaultName(int slotANumber, int slotBNumber, string operationKind)
    {
        string operationText = operationKind switch
        {
            MarkSlotSetOperations.And => "AND",
            MarkSlotSetOperations.AMinusB => "-",
            MarkSlotSetOperations.BMinusA => "逆差",
            MarkSlotSetOperations.Xor => "XOR",
            _ => "OR"
        };
        return operationKind switch
        {
            MarkSlotSetOperations.BMinusA => $"Slot{slotBNumber} - Slot{slotANumber}",
            _ when operationText == "-" => $"Slot{slotANumber} - Slot{slotBNumber}",
            _ => $"Slot{slotANumber} {operationText} Slot{slotBNumber}"
        };
    }
    private static string BuildMarkSlotExportFileName(MarkSlotEntry slot)
    {
        string safeDisplayName = BuildSafeMarkSlotFileNamePart(GetMarkSlotDisplayName(slot));
        return $"MidFD-MarkSlot-{slot.SlotNumber}-{safeDisplayName}.json";
    }
    private static string BuildSafeMarkSlotFileNamePart(string value)
    {
        string trimmed = string.IsNullOrWhiteSpace(value) ? "Slot" : value.Trim();
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (char ch in trimmed)
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }
        string sanitized = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "Slot";
        }
        return sanitized.Length > 64
            ? sanitized[..64]
            : sanitized;
    }
    private BrowserTabState? GetActiveBrowserTab()
    {
        return _browserTabViewState.ActiveTabIndex >= 0 && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count
            ? _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex]
            : null;
    }
    private string GetActiveBrowserTabCategoryDisplayName()
    {
        return _categoryViewState.Categories
            .FirstOrDefault(category => string.Equals(category.Id, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName
            ?? (_categoryViewState.ActiveCategoryId ?? string.Empty);
    }
    private string? GetBrowserTabDisplayName(BrowserTabState? tab)
    {
        if (tab == null)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(tab.Title)
            ? GetBrowserTabTitle(tab.CurrentPath)
            : tab.Title;
    }
    private static string GetMarkSlotSourceScopeLabel(string? sourceScope)
    {
        return sourceScope switch
        {
            MarkSlotSourceScopes.CurrentTab => "現在タブ",
            MarkSlotSourceScopes.CurrentCategory => "現在カテゴリ",
            MarkSlotSourceScopes.Workspace => "全Workspace",
            MarkSlotSourceScopes.SlotSetOperation => "スロット演算",
            _ => "不明 / Legacy"
        };
    }
    private static string GetMarkSlotSetOperationLabel(string operationKind)
    {
        return operationKind switch
        {
            MarkSlotSetOperations.And => "AND",
            MarkSlotSetOperations.AMinusB => "A-B",
            MarkSlotSetOperations.BMinusA => "B-A",
            MarkSlotSetOperations.Xor => "XOR",
            _ => "OR"
        };
    }
    private void SyncMarkSlotAggregationSnapshot()
    {
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
    }
    private MarkSlotSaveAggregationResult BuildCurrentCategoryMarkSlotAggregation()
    {
        SyncMarkSlotAggregationSnapshot();
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        string categoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        BrowserTabRestoreCategoryState? categoryState = snapshot.Categories.FirstOrDefault(
            category => string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (categoryState == null)
        {
            return new MarkSlotSaveAggregationResult(
                MarkSlotSourceScopes.CurrentCategory,
                "現在カテゴリ",
                categoryId,
                GetActiveBrowserTabCategoryDisplayName(),
                1,
                0,
                0,
                new List<string>());
        }
        return BuildMarkSlotSaveAggregationFromTabs(
            MarkSlotSourceScopes.CurrentCategory,
            "現在カテゴリ",
            categoryId,
            string.IsNullOrWhiteSpace(categoryState.DisplayName) ? GetActiveBrowserTabCategoryDisplayName() : categoryState.DisplayName,
            1,
            categoryState.OpenTabs);
    }
    private MarkSlotSaveAggregationResult BuildWorkspaceMarkSlotAggregation()
    {
        SyncMarkSlotAggregationSnapshot();
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        List<BrowserTabSessionState> allTabs = snapshot.Categories
            .SelectMany(static category => category.OpenTabs ?? new List<BrowserTabSessionState>())
            .ToList();
        return BuildMarkSlotSaveAggregationFromTabs(
            MarkSlotSourceScopes.Workspace,
            "全Workspace",
            null,
            null,
            snapshot.Categories.Count,
            allTabs);
    }
    private MarkSlotSaveAggregationResult BuildMarkSlotSaveAggregationFromTabs(
        string sourceScope,
        string sourceScopeLabel,
        string? sourceCategoryId,
        string? sourceCategoryName,
        int categoryCount,
        IEnumerable<BrowserTabSessionState> tabs)
    {
        int rawMarkCount = 0;
        int tabCount = 0;
        var uniquePaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BrowserTabSessionState tab in tabs)
        {
            tabCount++;
            foreach (string path in tab.MarkedPaths ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || !PathExists(path))
                {
                    continue;
                }
                rawMarkCount++;
                if (seen.Add(path))
                {
                    uniquePaths.Add(path);
                }
            }
        }
        return new MarkSlotSaveAggregationResult(
            sourceScope,
            sourceScopeLabel,
            sourceCategoryId,
            sourceCategoryName,
            categoryCount,
            tabCount,
            rawMarkCount,
            uniquePaths);
    }
    private string BuildScopedSlotSavePrompt(int slotNumber, MarkSlotEntry slot, MarkSlotSaveAggregationResult aggregation)
    {
        string overwriteText = HasMarkSlotSavedState(slot)
            ? $"既存のスロット {slotNumber} を上書きします。{Environment.NewLine}"
            : string.Empty;
        if (string.Equals(aggregation.SourceScope, MarkSlotSourceScopes.CurrentCategory, StringComparison.Ordinal))
        {
            string categoryName = string.IsNullOrWhiteSpace(aggregation.SourceCategoryName) ? "既定" : aggregation.SourceCategoryName;
            return
                $"{overwriteText}現在カテゴリ「{categoryName}」の全タブのマークをスロット{slotNumber}へ保存します。{Environment.NewLine}" +
                $"対象: {aggregation.TabCount}タブ / raw mark {aggregation.RawMarkCount}件{Environment.NewLine}" +
                $"重複除去後: {aggregation.UniquePathCount} path{Environment.NewLine}" +
                $"復元時は現在タブへ置換復元します。{Environment.NewLine}" +
                "表示名を入力してください。";
        }
        return
            $"{overwriteText}Workspace全体の全カテゴリ / 全タブのマークをスロット{slotNumber}へ保存します。{Environment.NewLine}" +
            $"対象: {aggregation.CategoryCount}カテゴリ / {aggregation.TabCount}タブ / raw mark {aggregation.RawMarkCount}件{Environment.NewLine}" +
            $"重複除去後: {aggregation.UniquePathCount} path{Environment.NewLine}" +
            $"復元時は現在タブへ置換復元します。{Environment.NewLine}" +
            "表示名を入力してください。";
    }
    private static string BuildDefaultMarkSlotDisplayName(MarkSlotEntry slot, string sourceScope, string? categoryName)
    {
        if (!IsDefaultMarkSlotDisplayName(slot))
        {
            return GetMarkSlotDisplayName(slot);
        }
        return sourceScope switch
        {
            var scope when string.Equals(scope, MarkSlotSourceScopes.CurrentCategory, StringComparison.Ordinal)
                => $"{(string.IsNullOrWhiteSpace(categoryName) ? "既定" : categoryName)} 全マーク",
            var scope when string.Equals(scope, MarkSlotSourceScopes.Workspace, StringComparison.Ordinal)
                => "Workspace全マーク",
            _ => $"スロット {slot.SlotNumber}"
        };
    }
    private static bool HasMarkSlotSavedState(MarkSlotEntry slot)
    {
        return slot.Paths.Count > 0 || slot.SavedAtUtc.HasValue || !IsDefaultMarkSlotDisplayName(slot);
    }
    private static bool IsDefaultMarkSlotDisplayName(MarkSlotEntry slot)
    {
        return string.Equals(GetMarkSlotDisplayName(slot), $"スロット {slot.SlotNumber}", StringComparison.CurrentCulture);
    }
    private void BeginPendingEscExitMarkPersistence(IReadOnlyList<string> markedPaths)
    {
        // Preserve pending marks when either restart persistence is enabled or workspace restore is enabled
        bool shouldPreserve = _settings.Session.PersistMarksAcrossRestart || SessionRestorePolicy.ShouldRestoreStartupWorkspace(_settings.Session);
        if (!shouldPreserve || markedPaths.Count == 0)
        {
            ClearPendingEscExitMarkPersistence();
            return;
        }
        _pendingEscExitPersistedMarks = markedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _isClosingFromEscExitPath = false;
    }
    private void ClearPendingEscExitMarkPersistence()
    {
        _pendingEscExitPersistedMarks = null;
        _isClosingFromEscExitPath = false;
    }
    private void ExecuteEnter()
    {
        var item = GetCurrentBrowserItem();
        if (item == null) return;
        if (item.Text == "..")
        {
            ExecuteBackspace();
            return;
        }
        string? fullPath = item.Tag as string;
        if (fullPath == null) return;
        if (Directory.Exists(fullPath))
        {
            ClearPreview(); // ディレクトリ移動前にクリア
            ExecuteDirectoryNavigationRequest(
                _browserNavigationCoordinator.CreateDirectoryNavigationRequest(fullPath));
        }
        else if (File.Exists(fullPath))
        {
            var rawKind = PreviewService.GetPreviewKind(fullPath);
            if (rawKind == PreviewKind.Video)
            {
                bool isAudio = PreviewService.IsSupportedAudioExtension(fullPath);
                bool shouldPlayExternal = isAudio || _settings.Preview?.VideoEnterPlaysExternal == true;
                if (shouldPlayExternal)
                {
                    var launchResult = VideoPlaybackLaunchService.Launch(
                        fullPath,
                        _settings.Preview?.VideoToolDirectory,
                        _settings.Preview?.VideoPlaybackVolumePercent ?? 100,
                        0);
                    if (launchResult.Success)
                    {
                        string mediaType = isAudio ? "音声" : "動画";
                        if (launchResult.UsedFfplay)
                        {
                            ShowStatusMessage($"ffplay.exeで{mediaType}を外部再生しました。音量:{launchResult.AppliedVolumePercent}%");
                        }
                        else
                        {
                            ShowStatusMessage($"ffplay.exeが見つからないため、既定アプリで{mediaType}を開きました。");
                        }
                    }
                    else
                    {
                        MessageBox.Show(this, launchResult.ErrorMessage ?? "外部再生の起動に失敗しました。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                else
                {
                    ExecuteBrowserOpenRequest(CreateBrowserOpenRequest(fullPath, allowExecuteTarget: true));
                }
            }
            else
            {
                ExecuteBrowserOpenRequest(CreateBrowserOpenRequest(fullPath, allowExecuteTarget: true));
            }
        }
    }
    private void ExecutePreviewLaunch()
    {
        var item = GetCurrentBrowserItem();
        if (item == null || item.Text == "..") return;
        string? fullPath = item.Tag as string;
        if (string.IsNullOrEmpty(fullPath)) return;
        if (Directory.Exists(fullPath))
        {
            return;
        }
        ExecuteBrowserOpenRequest(CreateBrowserOpenRequest(fullPath, allowExecuteTarget: false));
    }
    private ImageViewerForm? GetReusableImageViewer()
    {
        _imageViewers.RemoveAll(v => v.IsDisposed);
        if (!(_settings.Preview?.ReuseImageViewer ?? true))
        {
            return null;
        }
        return _imageViewers.FirstOrDefault();
    }
    private void CloseImageViewers()
    {
        var viewers = _imageViewers.Where(v => !v.IsDisposed).ToArray();
        foreach (var viewer in viewers)
        {
            viewer.Close();
        }
    }
    private bool TryCloseImageViewersFromMainEsc(string source)
    {
        if (_uiMode != UIMode.Browser)
        {
            return false;
        }

        _imageViewers.RemoveAll(v => v.IsDisposed);
        var viewers = _imageViewers.Where(v => !v.IsDisposed && v.Visible).ToArray();
        if (viewers.Length == 0)
        {
            return false;
        }

        foreach (var viewer in viewers)
        {
            viewer.Close();
        }

        LogService.Info($"[ImageViewerEscClose] Closed {viewers.Length} image viewer(s). source={source}");
        ShowStatusMessage(viewers.Length == 1
            ? "画像ビューアを閉じました。"
            : $"{viewers.Length} 個の画像ビューアを閉じました。");
        return true;
    }
    /// <summary>
    /// マウスダブルクリック等から呼ばれる、「その項目を既定の方法で開く」処理。
    /// Enterキー(ExecuteEnter)が内蔵Viewer/Previewを優先するのに対し、こちらは Explorer 同様に
    /// OS の既定動作 (directory は開く / file は関連付けで開く) を優先する。
    /// </summary>
    private void ExecuteDefaultOpen()
    {
        var item = GetCurrentBrowserItem();
        if (item == null) return;
        if (item.Text == "..")
        {
            ExecuteBackspace();
            return;
        }
        string? fullPath = item.Tag as string;
        if (fullPath == null) return;
        if (Directory.Exists(fullPath))
        {
            ClearPreview();
            if (!PrepareUnlockedTabForLocationChange(fullPath))
            {
                return;
            }
            LoadDirectory(fullPath);
        }
        else if (File.Exists(fullPath))
        {
            OpenPathWithShellAssociation(fullPath);
        }
    }
    private void OpenImageViewer(string path)
    {
        PreviewKind mediaKind = GetEffectivePreviewKind(path);
        var existing = GetReusableImageViewer();
        if (existing != null)
        {
            var beforeBounds = existing.Bounds;
            var beforeState = existing.WindowState;
            if (existing.WindowState == FormWindowState.Minimized)
            {
                existing.WindowState = FormWindowState.Normal;
            }
            existing.Bounds = NormalizeWindowBoundsToVisibleArea(existing.Bounds, new Size(160, 120));
            if (!string.Equals(existing.CurrentPath, path, StringComparison.OrdinalIgnoreCase) || !existing.HasLoadedImage)
            {
                if (mediaKind == PreviewKind.Video)
                {
                    int initialSeconds = _settings.Preview.VideoSkipSeconds;
                    existing.LoadVideoStill(path, _settings.Preview.VideoToolDirectory, initialSeconds, _settings.Preview.VideoPlaybackVolumePercent);
                }
                else
                {
                    existing.LoadMedia(path, mediaKind);
                }
            }
            existing.Show();
            EnsureTopLevelWindowVisible(existing, "ReuseImageViewerShown", new Size(160, 120));
            existing.BringToFront();
            existing.Activate();
            LogService.Info($"[WindowVisibility] ReuseImageViewer Path={path} BeforeState={beforeState} BeforeBounds={FormatBoundsForLog(beforeBounds)} AfterState={existing.WindowState} AfterBounds={FormatBoundsForLog(existing.Bounds)}");
            return;
        }
        // 新規起動
        var viewer = new ImageViewerForm(_settings.Preview, _featureGate);
        Rectangle desiredBounds;
        if (_settings.Preview.RememberImageViewerBounds && _settings.Preview.ImageViewerX != -1)
        {
            desiredBounds = new Rectangle(
                _settings.Preview.ImageViewerX,
                _settings.Preview.ImageViewerY,
                _settings.Preview.ImageViewerWidth,
                _settings.Preview.ImageViewerHeight);
        }
        else
        {
            desiredBounds = new Rectangle(
                this.Left + 40,
                this.Top + 40,
                viewer.Width,
                viewer.Height);
        }
        desiredBounds = NormalizeWindowBoundsToVisibleArea(desiredBounds, new Size(160, 120));
        viewer.StartPosition = FormStartPosition.Manual;
        viewer.SetBounds(desiredBounds.X, desiredBounds.Y, desiredBounds.Width, desiredBounds.Height);
        viewer.Shown += (s, e) => EnsureTopLevelWindowVisible(viewer, "NewImageViewerShown", new Size(160, 120));
        viewer.Move += (s, e) => SaveImageViewerBounds(viewer);
        viewer.ResizeEnd += (s, e) => SaveImageViewerBounds(viewer, "ResizeEnd", logBounds: true);
        viewer.FormClosed += (s, e) =>
        {
            SaveImageViewerBounds(viewer, "FormClosed", logBounds: true);
            _imageViewers.Remove(viewer);
        };
        viewer.BrowserNavigationRequested += keyData => TryHandleBrowserCmdKeyNavigation(keyData);
        viewer.MarkToggleRequested += () => ToggleMark(moveNext: true);
        _imageViewers.Add(viewer);
        viewer.Show();
        if (mediaKind == PreviewKind.Video)
        {
            int initialSeconds = _settings.Preview.VideoSkipSeconds;
            viewer.LoadVideoStill(path, _settings.Preview.VideoToolDirectory, initialSeconds, _settings.Preview.VideoPlaybackVolumePercent);
        }
        else
        {
            viewer.LoadMedia(path, mediaKind);
        }
        LogService.Info($"[WindowVisibility] NewImageViewer Path={path} Bounds={FormatBoundsForLog(viewer.Bounds)}");
    }

    private void SaveImageViewerBounds(ImageViewerForm viewer, string? reason = null, bool logBounds = false)
    {
        if (!_settings.Preview.RememberImageViewerBounds || viewer.IsDisposed)
        {
            return;
        }
        Rectangle bounds = viewer.WindowState == FormWindowState.Normal
            ? viewer.Bounds
            : viewer.RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }
        _settings.Preview.ImageViewerX = bounds.X;
        _settings.Preview.ImageViewerY = bounds.Y;
        _settings.Preview.ImageViewerWidth = bounds.Width;
        _settings.Preview.ImageViewerHeight = bounds.Height;
        if (logBounds)
        {
            LogService.Info($"[WindowVisibility] SaveImageViewerBounds Reason={reason ?? "Unknown"} State={viewer.WindowState} Saved={FormatBoundsForLog(bounds)} Current={FormatBoundsForLog(viewer.Bounds)} Restore={FormatBoundsForLog(viewer.RestoreBounds)}");
        }
    }
    private void ExecuteZLaunch()
    {
        if (GuardMutationBusy()) return;
        var item = GetCurrentBrowserItem();
        if (item == null || item.Text == "..") return;
        string? fullPath = item.Tag as string;
        if (string.IsNullOrEmpty(fullPath)) return;
        try
        {
            if (Directory.Exists(fullPath))
            {
                // ディレクトリは Explorer で開く (Z の軽量追加)
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add(fullPath);
                System.Diagnostics.Process.Start(startInfo);
            }
            else if (File.Exists(fullPath))
            {
                OpenPathWithShellAssociation(fullPath);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Z-Launch 失敗: {ex.Message}");
            ShowStatusMessage("起動に失敗しました");
            MessageBox.Show(this, $"エクスプローラーを起動できませんでした。\n理由: {ex.Message}", "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void ExecuteBackspace()
    {
        if (TryHandleLockedRootParentNavigation())
        {
            return;
        }
        ExecuteDirectoryNavigationRequest(
            _browserNavigationCoordinator.CreateParentNavigationRequest(_navigationService.CurrentPath));
    }
    private bool TryHandleLockedRootParentNavigation()
    {
        if (_browserTabViewState.ActiveTabIndex < 0 || _browserTabViewState.ActiveTabIndex >= _browserTabViewState.Count)
        {
            return false;
        }
        BrowserTabState state = _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex];
        if (!state.IsLocked || string.IsNullOrWhiteSpace(state.StartupPath))
        {
            return false;
        }
        if (!QuickAccessService.PathsEqual(_navigationService.CurrentPath, state.StartupPath))
        {
            return false;
        }
        DirectoryInfo? parent = Directory.GetParent(_navigationService.CurrentPath);
        if (parent == null || !Directory.Exists(parent.FullName))
        {
            ShowStatusMessage("固定タブの親フォルダが見つかりません。");
            return true;
        }
        if (!ShowLockedRootParentNavigationConfirm())
        {
            ShowStatusMessage("固定タブの範囲外への移動をキャンセルしました。");
            return true;
        }
        if (CreateNewBrowserTab(parent.FullName, showStatusMessage: false))
        {
            ShowStatusMessage("固定タブの親フォルダを新しいタブで開きました。");
        }
        return true;
    }
    private bool ShowLockedRootParentNavigationConfirm()
    {
        using var dialog = new Form
        {
            Text = "固定タブ範囲外",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            ControlBox = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
            Font = SystemFonts.MessageBoxFont
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var icon = new PictureBox
        {
            Image = SystemIcons.Question.ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(0, 2, 12, 0)
        };
        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 2);

        var messageLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Text = "固定タブの範囲外です。親フォルダを新しいタブで開きますか？",
            Margin = new Padding(0, 0, 0, 16)
        };
        layout.Controls.Add(messageLabel, 1, 0);

        var yesButton = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(86, 28),
            Text = "はい(&Y)",
            DialogResult = DialogResult.Yes
        };
        var noButton = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(86, 28),
            Text = "いいえ(&N)",
            DialogResult = DialogResult.No
        };

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0)
        };
        buttonPanel.Controls.Add(noButton);
        buttonPanel.Controls.Add(yesButton);
        layout.Controls.Add(buttonPanel, 1, 1);

        dialog.AcceptButton = yesButton;
        dialog.CancelButton = noButton;
        dialog.Controls.Add(layout);

        return dialog.ShowDialog(this) == DialogResult.Yes;
    }
    private void ExecuteLogdisk()
    {
        if (GuardClipboardBusy()) return;
        string defaultPath = string.IsNullOrWhiteSpace(_navigationService.CurrentPath)
            ? (Path.GetPathRoot(_navigationService.CurrentPath) ?? "C:\\")
            : _navigationService.CurrentPath;
        string? selected = LogdiskDialog.Show(defaultPath, GetSharedLocationCandidates());
        if (!string.IsNullOrWhiteSpace(selected))
        {
            BrowserPathEntryNavigationResult result = BrowserPathEntryNavigationService.Resolve(selected, _navigationService);
            if (result.TargetKind == BrowserPathEntryTargetKind.Directory)
            {
                NavigateToLocationDirectory(result.ResolvedPath);
            }
            else if (result.TargetKind == BrowserPathEntryTargetKind.None)
            {
                MessageBox.Show(result.StatusMessage, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private static bool TryNormalizeDriveOnlyInputToRoot(string input, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();
        if (trimmed.Length == 1 && char.IsLetter(trimmed[0]))
        {
            normalizedPath = $"{char.ToUpperInvariant(trimmed[0])}:\\";
            return true;
        }

        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            normalizedPath = $"{char.ToUpperInvariant(trimmed[0])}:\\";
            return true;
        }

        return false;
    }
    private List<string> GetSharedDirectoryMoveHistory()
    {
        MigrateLegacyMoveDestinationHistory();
        return _settings.Session.DirectoryMoveHistory;
    }

    private IReadOnlyList<string> GetSharedLocationCandidates()
    {
        return BrowserPathEntryCandidateService.BuildCandidates(
            _navigationService,
            _quickAccessStore,
            GetSharedDirectoryMoveHistory());
    }

    private void MigrateLegacyMoveDestinationHistory()
    {
        var session = _settings.Session;
        var legacy = session.MoveDestinationHistory;
        if (legacy == null || legacy.Count == 0)
        {
            return;
        }

        bool changed = false;
        foreach (var path in legacy)
        {
            changed |= AddNormalizedDirectoryHistoryEntry(session.DirectoryMoveHistory, path, maxCount: 30);
        }

        if (changed)
        {
            legacy.Clear();
            SettingsManager.Save(_settings);
        }
    }

    private void AddDirectoryMoveHistory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            MigrateLegacyMoveDestinationHistory();
            if (AddNormalizedDirectoryHistoryEntry(_settings.Session.DirectoryMoveHistory, path, maxCount: 30))
            {
                SettingsManager.Save(_settings);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"AddDirectoryMoveHistory 失敗: {ex.Message}");
        }
    }

    private static bool AddNormalizedDirectoryHistoryEntry(IList<string> history, string path, int maxCount)
    {
        if (history == null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = Path.GetFullPath(path);
        if (!Directory.Exists(normalized))
        {
            return false;
        }

        if (!normalized.EndsWith(Path.DirectorySeparatorChar))
        {
            normalized += Path.DirectorySeparatorChar;
        }

        int index = -1;
        for (int i = 0; i < history.Count; i++)
        {
            if (string.Equals(history[i], normalized, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index == 0)
        {
            return false;
        }

        if (index > 0)
        {
            history.RemoveAt(index);
        }

        history.Insert(0, normalized);
        while (history.Count > maxCount)
        {
            history.RemoveAt(history.Count - 1);
        }

        return true;
    }
    private void ExecuteSort()
    {
        if (GuardClipboardBusy()) return;
        string kindStr = _currentSort.ToString();
        var result = SortDialog.Show(kindStr, _sortAscending);
        if (result != null)
        {
            SortKind selectedKind = result.Kind switch
            {
                "Name" => SortKind.Name,
                "Ext" => SortKind.Ext,
                "Size" => SortKind.Size,
                "Date" => SortKind.Date,
                "DateCreated" => SortKind.DateCreated,
                "DateAccessed" => SortKind.DateAccessed,
                _ => _currentSort
            };
            ApplySortState(selectedKind, result.Ascending);
        }
    }
    private void ApplySortState(SortKind sortKind, bool ascending)
    {
        _currentSort = sortKind;
        _sortAscending = ascending;
        _settings.Session.LastSortKind = _currentSort;
        _settings.Session.LastSortAscending = _sortAscending;
        LoadDirectory(_navigationService.CurrentPath);
    }
    private void ExecuteFilter()
    {
        if (GuardClipboardBusy()) return;
        // 独自のフィルタ入力ダイアログを使用 (Regex オプション込み)
        var result = FilterInputDialog.Show(
            "フィルタパターンを入力してください (空欄で解除):",
            "フィルタ表示 (F/F7)",
            _filterPattern,
            _filterUseRegex);
        if (result != null) // null = Cancel なので前の状態を維持
        {
            _filterPattern = result.Pattern;
            _filterUseRegex = result.UseRegex;
            LoadDirectory(_navigationService.CurrentPath);
        }
    }
    private SelectionResult ResolveSelection()
    {
        if (_browserContextMenuSelectionOverride != null)
        {
            return _browserContextMenuSelectionOverride;
        }
        return SelectionResolver.Resolve(_markedFiles, GetCurrentBrowserItem());
    }
    private SelectionResult ResolveSelection(SelectionResult? selectionSnapshot)
    {
        if (selectionSnapshot is not null && selectionSnapshot.Count > 0)
        {
            return selectionSnapshot;
        }

        return ResolveSelection();
    }
    private enum MultiMarkGuardAction
    {
        CurrentOnly,
        MarkedAll,
        Cancel
    }
    private bool TryGetUnmarkedCurrentItemForMultiMarkGuard(out string currentPath, out string currentName)
    {
        currentPath = string.Empty;
        currentName = string.Empty;
        if (_markedFiles.Count <= 1)
        {
            return false;
        }
        var currentItem = GetCurrentBrowserItem();
        if (currentItem == null || currentItem.Text == "..")
        {
            return false;
        }
        string? path = currentItem.Tag as string;
        if (string.IsNullOrWhiteSpace(path) || _markedFiles.Contains(path))
        {
            return false;
        }
        currentPath = path;
        currentName = currentItem.Text;
        return true;
    }
    private SelectionResult BuildCurrentOnlySelection(string currentPath)
    {
        return new SelectionResult(new[] { currentPath }, false);
    }
    private string BuildSelectionSummaryText(SelectionResult selection)
    {
        string firstName = selection.FirstFileName ?? "(不明)";
        return $"{selection.Count} 件の対象が選択されています。{Environment.NewLine}先頭項目: {firstName}";
    }
    private string? BuildSelectionOutsideCurrentDirectoryWarning(SelectionResult selection)
    {
        if (selection.Count == 0 || string.IsNullOrWhiteSpace(_navigationService.CurrentPath))
        {
            return null;
        }
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        int outsideCount = selection.FullPaths.Count(path =>
            !string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                currentDir,
                StringComparison.OrdinalIgnoreCase));
        if (outsideCount <= 0)
        {
            return null;
        }
        return $"警告: 現在のディレクトリ外の項目を {outsideCount} 件含みます。";
    }
    private IReadOnlyList<string> CaptureCurrentMarkedPathSnapshot()
    {
        return _markedFiles
            .Snapshot()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    private IReadOnlySet<string> CaptureVisibleBrowserPathSet()
    {
        return fileListView.Items
            .Cast<ListViewItem>()
            .Select(item => item.Tag as string)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PathTextIntakeService.CanonicalIdentity(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private void RestoreMarksAfterOperation(
        IReadOnlyList<string> snapshot,
        IReadOnlySet<string> visibleBefore,
        FileOpExitStatus status)
    {
        IReadOnlySet<string> visibleAfter = CaptureVisibleBrowserPathSet();
        IReadOnlyList<string> restored = MarkOperationLifecycleResolver.Reconcile(
            snapshot,
            visibleBefore,
            visibleAfter,
            status,
            PathExists);
        RestoreMarks(restored, invalidateRedo: false);
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        PrimeRecentMultiMarkIntent();
    }
    private void PrimeRecentMultiMarkIntent()
    {
        IReadOnlyList<string> markedPaths = CaptureCurrentMarkedPathSnapshot();
        if (_uiMode != UIMode.Browser || markedPaths.Count <= 1 || string.IsNullOrWhiteSpace(_navigationService.CurrentPath))
        {
            InvalidateRecentMultiMarkIntent();
            return;
        }
        _recentMultiMarkIntentActive = true;
        _recentMultiMarkIntentDirectory = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        _recentMultiMarkIntentCursorIndex = _browserCursorIndex;
        _recentMultiMarkIntentMarkedPaths = markedPaths;
    }
    private void InvalidateRecentMultiMarkIntent()
    {
        _recentMultiMarkIntentActive = false;
        _recentMultiMarkIntentDirectory = string.Empty;
        _recentMultiMarkIntentCursorIndex = -1;
        _recentMultiMarkIntentMarkedPaths = Array.Empty<string>();
    }
    private bool ShouldBypassMultiMarkSelectionAction(SelectionResult selection)
    {
        if (!_recentMultiMarkIntentActive || !selection.HasMarkedSelection || selection.Count <= 1)
        {
            return false;
        }
        if (!string.Equals(
                _recentMultiMarkIntentDirectory,
                NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath),
                StringComparison.OrdinalIgnoreCase) ||
            _recentMultiMarkIntentCursorIndex != _browserCursorIndex)
        {
            InvalidateRecentMultiMarkIntent();
            return false;
        }
        IReadOnlyList<string> markedPaths = CaptureCurrentMarkedPathSnapshot();
        if (markedPaths.Count != _recentMultiMarkIntentMarkedPaths.Count ||
            !markedPaths.SequenceEqual(_recentMultiMarkIntentMarkedPaths, StringComparer.OrdinalIgnoreCase))
        {
            InvalidateRecentMultiMarkIntent();
            return false;
        }
        return true;
    }
    private bool AddBrowserTabFromEntry()
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        int maxTabCount = GetMaxBrowserTabsPerCategory();
        if (_browserTabViewState.Count >= maxTabCount)
        {
            ShowStatusMessage($"タブは最大{maxTabCount}個までです。");
            _browserTabStrip?.FlashLimitReached();
            TryPlayBrowserTabLimitBeep();
            return false;
        }
        CaptureActiveBrowserTabState();
        string categoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        BrowserTabState newState = CreateInitialBrowserTabStateForCategory(categoryId);
        int insertIndex = _browserTabViewState.ActiveTabIndex >= 0 && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count
            ? _browserTabViewState.ActiveTabIndex + 1
            : _browserTabViewState.Count;
        _browserTabViewState.Insert(insertIndex, newState);
        RefreshBrowserTabHeaders();
        _browserTabViewState.ActiveTabIndex = -1;
        SwitchBrowserTab(insertIndex);
        ShowStatusMessage("新しいタブを追加しました。");
        return true;
    }
    private bool TryResolveMultiMarkSelectionAction(string operationName, string cancelStatusMessage, SelectionResult selection, out SelectionResult effectiveSelection)
    {
        effectiveSelection = selection;
        if (!selection.HasMarkedSelection || selection.Count <= 1)
        {
            return true;
        }
        if (ShouldBypassMultiMarkSelectionAction(selection))
        {
            return true;
        }
        if (!TryGetUnmarkedCurrentItemForMultiMarkGuard(out string currentPath, out string currentName))
        {
            return true;
        }
        MultiMarkGuardAction action = ShowMultiMarkGuardActionDialog(operationName, currentName, selection.Count);
        switch (action)
        {
            case MultiMarkGuardAction.CurrentOnly:
                effectiveSelection = BuildCurrentOnlySelection(currentPath);
                return true;
            case MultiMarkGuardAction.MarkedAll:
                return true;
            default:
                ShowStatusMessage(cancelStatusMessage);
                return false;
        }
    }
    private MultiMarkGuardAction ShowMultiMarkGuardActionDialog(string operationName, string currentName, int markedCount)
    {
        using var dialog = new Form
        {
            Text = $"マーク済み項目の{operationName}確認",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(470, 186)
        };
        var messageLabel = new Label
        {
            Left = 16,
            Top = 16,
            Width = 438,
            AutoSize = false,
            Text =
                $"現在はマーク済み項目が {markedCount} 件あります。\n" +
                $"このまま{operationName}すると、現在行の {currentName} だけではなく、マーク済み {markedCount} 件が対象になります。\n\n" +
                "対象を選んでください。"
        };
        var currentOnlyButton = new Button
        {
            Left = 16,
            Top = 120,
            Width = 120,
            Height = 30,
            Text = "現在行だけ(&C)",
            UseMnemonic = true,
            TabIndex = 0
        };
        var markedAllButton = new Button
        {
            Left = 146,
            Top = 120,
            Width = 136,
            Height = 30,
            Text = "マーク済み全件(&M)",
            UseMnemonic = true,
            TabIndex = 1
        };
        var cancelButton = new Button
        {
            Left = 292,
            Top = 120,
            Width = 104,
            Height = 30,
            Text = "キャンセル(&X)",
            UseMnemonic = true,
            DialogResult = DialogResult.Cancel,
            TabIndex = 2
        };
        MultiMarkGuardAction result = MultiMarkGuardAction.Cancel;
        currentOnlyButton.Click += (_, _) =>
        {
            result = MultiMarkGuardAction.CurrentOnly;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        markedAllButton.Click += (_, _) =>
        {
            result = MultiMarkGuardAction.MarkedAll;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        dialog.Controls.Add(messageLabel);
        dialog.Controls.Add(currentOnlyButton);
        dialog.Controls.Add(markedAllButton);
        dialog.Controls.Add(cancelButton);
        messageLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(messageLabel, messageLabel.Width, 88);
        FileOperationDialogLayoutHelper.EnsureBottomButtonRow(
            dialog,
            new[] { currentOnlyButton, markedAllButton, cancelButton },
            messageLabel.Bottom,
            buttonGap: 10,
            contentGap: 14);
        dialog.AcceptButton = currentOnlyButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) => BeginInvoke(new Action(() => cancelButton.Focus()));
        return dialog.ShowDialog(this) == DialogResult.OK
            ? result
            : MultiMarkGuardAction.Cancel;
    }
    /// <summary>
    /// 現在のカーソル位置から下方（後方）へ走査し、「対象リスト(targetPaths)に含まれていない最初のファイル名」を取得する。
    /// Move や Delete 操作後のリロード時に、元のスクロール位置付近を自然に維持するためのヘルパー。
    /// </summary>
    private string? GetNextFocusTarget(List<string> targetPaths)
    {
        if (targetPaths == null || targetPaths.Count == 0) return null;
        if (fileListView.Items.Count == 0) return null;
        var startItem = GetCurrentBrowserItem();
        int startIndex = startItem != null ? startItem.Index : 0;
        for (int i = startIndex; i < fileListView.Items.Count; i++)
        {
            var item = fileListView.Items[i];
            if (item.Text == "..") continue;
            string? path = item.Tag as string;
            if (path != null && !targetPaths.Contains(path))
            {
                return GetItemFullName(item);
            }
        }
        return null;
    }
    private string GetViewerStatusLine()
    {
        string encLabel = GetViewerEncodingStatusLabel();
        string wrapLabel = viewerTextBox.WordWrap ? "ON" : "OFF";
        string lineLabel = GetViewerLineStatus();
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            var state = _largeFileState;
            int endLine = Math.Min(state.FirstVisibleLine + _largeFileControl.VisibleLineCount, state.TotalLines);
            double percent = state.TotalLines > 0 ? (double)state.FirstVisibleLine / state.TotalLines * 100.0 : 0;
            string indexingLabel = state.IsIndexing ? " (indexing...)" : "";
            string hitLabel = state.ActiveSearchHitLine.HasValue
                ? $" | Hit:{state.ActiveSearchHitLine.Value + 1:N0}:{state.ActiveSearchHitColumn + 1:N0}"
                : "";
            string reasonLabel = state.IsLongLineDetected ? " (長大行検出)" : "";
            lineLabel = $" | Lines:{state.FirstVisibleLine + 1:N0}-{endLine:N0}/{state.TotalLines:N0}{indexingLabel} ({percent:F1}%){hitLabel}";
            return $"[Viewer] Enc:{encLabel}{lineLabel}{reasonLabel} | Enter/Esc:Browser へ戻る";
        }
        string findLabel = string.IsNullOrWhiteSpace(_viewerSearchKeyword) ? "" : $" | Find:{_viewerSearchKeyword}";
        return $"[Viewer] Enc:{encLabel} | Wrap:{wrapLabel}{lineLabel}{findLabel} | Enter/Esc:Browser へ戻る";
    }
    private string GetViewerEncodingStatusLabel()
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            return string.IsNullOrWhiteSpace(_largeFileState.DetectedEncodingLabel)
                ? "Unknown"
                : _largeFileState.DetectedEncodingLabel;
        }
        if ((_currentViewerKind == PreviewKind.Text
                || _currentViewerKind == PreviewKind.Markdown
                || _currentViewerKind == PreviewKind.Sqlite)
            && !string.IsNullOrWhiteSpace(_currentViewerDetectedEncodingLabel))
        {
            return _currentViewerDetectedEncodingLabel;
        }
        return _viewerEncodingOverride switch
        {
            ViewerEncoding.UTF8 => "UTF-8",
            ViewerEncoding.SJIS => "Shift_JIS",
            _ => "自動"
        };
    }
    private string GetViewerLineStatus()
    {
        if (!IsTextOrBinaryViewerActive())
        {
            return string.Empty;
        }
        int currentLine = GetViewerCurrentLineNumber();
        int totalLines = Math.Max(1, viewerTextBox.Lines.Length);
        return $" | Line:{currentLine}/{totalLines}";
    }
    private int GetViewerCurrentLineNumber()
    {
        if (!viewerTextBox.Visible)
        {
            return 1;
        }
        int charIndex = viewerTextBox.GetCharIndexFromPosition(new Point(2, 2));
        if (charIndex < 0)
        {
            charIndex = viewerTextBox.SelectionStart;
        }
        return viewerTextBox.GetLineFromCharIndex(charIndex) + 1;
    }
    private bool IsTextOrBinaryViewerActive()
    {
        return _uiMode == UIMode.Viewer
            && viewerTextBox.Visible
            && IsPlainTextBoxViewerKind(_currentViewerKind);
    }
    private void NormalizeStatusLabelLayout()
    {
        if (statusStrip == null || statusStrip.IsDisposed ||
            statusLabel == null || statusLabel.IsDisposed)
        {
            return;
        }
        // 縦方向の欠けを防止するため、フォント高さに基づいて StatusStrip の高さを確保する。
        // 目安としてフォント実測高さ + 6px (上下 3px ずつ) 程度を確保する。最小 24px。
        int measuredTextHeight = TextRenderer.MeasureText(
            "AgjQy|漢/",
            statusLabel.Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
        int desiredHeight = Math.Max(24, measuredTextHeight + 6);
        if (statusStrip.AutoSize || statusStrip.Height != desiredHeight)
        {
            // AutoSize が ON だと Height 指定が効かない場合があるため
            statusStrip.AutoSize = false;
            statusStrip.Height = desiredHeight;
        }
        statusLabel.Alignment = ToolStripItemAlignment.Left;
        statusLabel.Overflow = ToolStripItemOverflow.AsNeeded;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.Margin = new Padding(0, 1, 0, 1);
        statusLabel.Padding = new Padding(0, 1, 0, 1);
        statusLabel.Spring = false;
        statusLabel.AutoSize = true;
    }
    private void UpdateFileOperationItemProgressState(FileOperationItemProgressState state)
    {
        _fileOperationItemProgressState = state;
        if (!state.IsActive)
        {
            CloseFileOperationProgressDialog();
            return;
        }

        var dialog = EnsureFileOperationProgressDialog();
        dialog.UpdateProgress(state, _fileOpUiState.Cts?.IsCancellationRequested ?? false);
        NormalizeStatusLabelLayout();
    }
    private void ClearFileOperationItemProgressState()
    {
        _fileOperationItemProgressState = null;
        CloseFileOperationProgressDialog();
    }
    private FileOperationProgressDialog EnsureFileOperationProgressDialog()
    {
        if (_fileOperationProgressDialog == null || _fileOperationProgressDialog.IsDisposed)
        {
            _fileOperationProgressDialog = new FileOperationProgressDialog(
                () => RequestActiveFileOperationCancel("FileOperationProgressDialog"),
                canCancel: _fileOpUiState.Cts != null);
            PositionFileOperationProgressDialog(_fileOperationProgressDialog);
            _fileOperationProgressDialog.Show(this);
        }
        else if (!_fileOperationProgressDialog.Visible)
        {
            PositionFileOperationProgressDialog(_fileOperationProgressDialog);
            _fileOperationProgressDialog.Show(this);
        }

        return _fileOperationProgressDialog;
    }
    private void PositionFileOperationProgressDialog(FileOperationProgressDialog dialog)
    {
        Rectangle ownerClientBounds = RectangleToScreen(ClientRectangle);
        Rectangle workingArea = Screen.FromRectangle(ownerClientBounds).WorkingArea;
        int centeredX = ownerClientBounds.Left + (ownerClientBounds.Width - dialog.Width) / 2;
        int lowerBiasY = ownerClientBounds.Top + (ownerClientBounds.Height * 2 / 3) - (dialog.Height / 2);
        int bottomBiasY = ownerClientBounds.Bottom - dialog.Height - Math.Max(48, ownerClientBounds.Height / 8);
        int x = Math.Max(
            workingArea.Left,
            Math.Min(centeredX, workingArea.Right - dialog.Width));
        int y = Math.Max(
            workingArea.Top,
            Math.Min(Math.Min(lowerBiasY, bottomBiasY), workingArea.Bottom - dialog.Height));
        dialog.Location = new Point(x, y);
    }
    private void CloseFileOperationProgressDialog()
    {
        var dialog = _fileOperationProgressDialog;
        _fileOperationProgressDialog = null;
        _fileOperationItemProgressState = null;
        if (dialog == null)
        {
            return;
        }
        try
        {
            if (!dialog.IsDisposed)
            {
                dialog.Close();
            }
        }
        catch
        {
            // 進捗ダイアログの後始末失敗は主処理を止めない。
        }
    }
    private Font GetHeaderStatusResponsiveBaseFont()
    {
        if (_headerPaintFont != null)
        {
            return _headerPaintFont;
        }

        if (fileListView?.Font != null)
        {
            return fileListView.Font;
        }

        if (browserPanel?.Font != null)
        {
            return browserPanel.Font;
        }

        return this.Font;
    }
    private void ApplyHeaderStatusFontToControls(Font font)
    {
        lblClock.Font = font;
        lblPath.Font = font;
        if (_breadcrumbPathControl != null)
        {
            _breadcrumbPathControl.Font = font;
        }
        lblSort.Font = font;
        lblItemAttr.Font = font;
        lblFileDate.Font = font;
        lblFileStats.Font = font;
        lblFileStatsEx.Font = font;
        lblName.Font = font;
        lblPage.Font = font;
        lblTotal.Font = font;
        lblUsed.Font = font;
        lblFree.Font = font;
        statusStrip.Font = font;
        statusLabel.Font = font;
    }
    private void ApplyResolvedHeaderStatusFontForCurrentWindow(Font baseFont, Font resolvedFont, string reason)
    {
        ApplyHeaderStatusFontToControls(resolvedFont);

        var headerMetrics = HeaderLayoutHelper.CalculateMetrics(resolvedFont, 4);
        titleHeaderPanel.Height = headerMetrics.TitleHeaderHeight;
        headerPanel.Height = headerMetrics.RowHeight;
        infoRow2Panel.Height = headerMetrics.RowHeight;
        infoRow4Panel.Height = headerMetrics.RowHeight;
        topPanel.Height = headerMetrics.TopPanelHeight;

        UpdateInfoPanel();
        LayoutHeaderZones();
        NormalizeStatusLabelLayout();

        contentFramePanel.Invalidate();
        titleHeaderPanel.Invalidate();
        headerPanel.Invalidate();
        topPanel.Invalidate();
        infoRow2Panel.Invalidate();
        infoRow4Panel.Invalidate();
        statusStrip.Invalidate();

        LogHeaderResponsiveDiag("Apply", reason, baseFont, resolvedFont);
    }
    private void ScheduleHeaderStatusResponsiveFontRecompute(string reason)
    {
        if (Disposing || IsDisposed)
        {
            return;
        }

        _headerStatusResizeDebounceTimer ??= new System.Windows.Forms.Timer
        {
            Interval = HeaderStatusResponsiveFontDebounceMs
        };

        _headerStatusResizeDebounceTimer.Tick -= HeaderStatusResizeDebounceTimer_Tick;
        _headerStatusResizeDebounceTimer.Tick += HeaderStatusResizeDebounceTimer_Tick;
        _headerStatusResizeDebounceTimer.Stop();
        _headerStatusResizeDebounceTimer.Start();
        LogHeaderResponsiveDiag("Schedule", reason, GetHeaderStatusResponsiveBaseFont(), null, scheduled: true);
    }
    private void HeaderStatusResizeDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _headerStatusResizeDebounceTimer?.Stop();
        LogHeaderResponsiveDiag("Tick", "resize-debounce", GetHeaderStatusResponsiveBaseFont(), null);
        RecomputeHeaderStatusResponsiveFontNow("resize-debounce");
    }
    private void ApplyHeaderStatusResponsiveFontWithOwnership(Font baseFont, Font resolvedFont, string reason)
    {
        Font? previousOwnedFont = _headerStatusResponsiveOwnedFont;
        if (ReferenceEquals(resolvedFont, baseFont))
        {
            _headerStatusResponsiveOwnedFont = null;
        }
        else
        {
            _headerStatusResponsiveOwnedFont = resolvedFont;
        }

        ApplyResolvedHeaderStatusFontForCurrentWindow(baseFont, resolvedFont, reason);
        LogHeaderResponsiveStabilizeDiag("Apply", reason, resolvedFont, GetCurrentHeaderRow1FitMetrics(resolvedFont), fontDisposeSuppressed: previousOwnedFont != null && !ReferenceEquals(previousOwnedFont, resolvedFont));
    }
    private void RecomputeHeaderStatusResponsiveFontNow(string reason)
    {
        if (_updatingHeaderStatusResponsiveFont)
        {
            LogHeaderResponsiveDiag("Skip", $"{reason}:reentry", GetHeaderStatusResponsiveBaseFont(), null, skippedReason: "reentry");
            return;
        }

        if (Disposing || IsDisposed || !IsHandleCreated || headerPanel == null || headerPanel.IsDisposed)
        {
            LogHeaderResponsiveDiag("Skip", $"{reason}:invalid", GetHeaderStatusResponsiveBaseFont(), null, skippedReason: "invalid-state");
            return;
        }

        Size clientSize = ClientSize;
        int currentDpi = DeviceDpi;
        bool isResizeReason =
            reason.Contains("resize", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("size", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("dpi", StringComparison.OrdinalIgnoreCase);
        bool forceRecompute =
            reason.Equals("ResizeEnd", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("SettingsApplied", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("SettingsOK", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("DpiChanged", StringComparison.OrdinalIgnoreCase);

        if (clientSize.Width <= 0 || clientSize.Height <= 0 || headerPanel.ClientSize.Width <= 0)
        {
            LogHeaderResponsiveDiag("Skip", $"{reason}:zero-size", GetHeaderStatusResponsiveBaseFont(), null, skippedReason: "zero-size");
            return;
        }

        Font currentAppliedFont = lblPage?.Font ?? GetHeaderStatusResponsiveBaseFont();
        HeaderRow1FitMetrics currentAppliedMetrics = GetCurrentHeaderRow1FitMetrics(currentAppliedFont);

        if (isResizeReason &&
            clientSize == _lastHeaderStatusResponsiveClientSize &&
            currentDpi == _lastHeaderStatusResponsiveDpi &&
            !forceRecompute &&
            currentAppliedMetrics.Fits)
        {
            LogHeaderResponsiveDiag("Skip", $"{reason}:unchanged", GetHeaderStatusResponsiveBaseFont(), null, skippedReason: "same-clientsize");
            LogHeaderResponsiveStabilizeDiag("Skip", reason, currentAppliedFont, currentAppliedMetrics, skippedReason: "same-clientsize-fit-ok");
            return;
        }

        _updatingHeaderStatusResponsiveFont = true;
        try
        {
            Font baseFont = GetHeaderStatusResponsiveBaseFont();
            Font resolvedFont = ResolveAdaptiveHeaderStatusFont(baseFont);
            ApplyHeaderStatusResponsiveFontWithOwnership(baseFont, resolvedFont, reason);
            _lastHeaderStatusResponsiveClientSize = clientSize;
            _lastHeaderStatusResponsiveDpi = currentDpi;

            HeaderRow1FitMetrics postApplyMetrics = GetCurrentHeaderRow1FitMetrics(resolvedFont);
            LogHeaderResponsiveDiag("End", reason, baseFont, resolvedFont);
            LogHeaderResponsiveStabilizeDiag("Apply", reason, resolvedFont, postApplyMetrics, skippedReason: postApplyMetrics.Fits ? "-" : "fit-warning");
        }
        finally
        {
            _updatingHeaderStatusResponsiveFont = false;
        }
    }
    private Font ResolveAdaptiveHeaderStatusFont(Font baseFont)
    {
        if (headerPanel == null || headerPanel.IsDisposed)
        {
            return baseFont;
        }

        int rowWidth = Math.Max(0, headerPanel.ClientSize.Width);
        if (rowWidth <= 0)
        {
            LogAdaptiveFontDiag("EARLY_RETURN", baseFont, rowWidth, baseFont.Size, true);
            return baseFont;
        }

        string clockText = lblClock?.Text ?? string.Empty;
        string pageText = lblPage?.Text ?? string.Empty;
        string totalText = lblTotal?.Text ?? string.Empty;
        string usedText = lblUsed?.Text ?? string.Empty;
        string freeText = lblFree?.Text ?? string.Empty;

        float baseWidth = Math.Max(1, MinimumNormalWindowWidth);
        // Px1 overscale cap: widthRatio は縮小補助のみ (1超えで拡大しない)。
        // 4K fullscreen等の広幅でも header/status font は baseFont.Size を超えない。
        float widthScale = MathF.Min(1f, MathF.Sqrt(Math.Max(0.25f, rowWidth / baseWidth)));
        float ratioTarget = baseFont.Size * widthScale;   // widthScale <= 1 なので ratioTarget <= baseFont.Size
        float minSize = Math.Min(baseFont.Size, HeaderStatusMinimumReadableFontSize);
        float maxSize = baseFont.Size;                    // 上限 = 一覧fontサイズ。widthRatioで拡大しない
        float bestSize = minSize;
        bool fitFound = false;
        HeaderRow1FitMetrics bestFitMetrics = default;

        for (int i = 0; i < 10; i++)
        {
            // i=0: maxSize (= baseFont.Size) から探索開始。fit しなければ下方binary search
            float candidateSize = i == 0
                ? maxSize
                : (minSize + maxSize) / 2f;
            using Font candidateFont = new(baseFont.FontFamily, candidateSize, baseFont.Style, GraphicsUnit.Point);
            HeaderRow1FitMetrics fitMetrics = GetHeaderRow1FitMetrics(candidateFont, rowWidth, pageText, totalText, usedText, freeText, clockText);
            if (fitMetrics.Fits)
            {
                fitFound = true;
                bestSize = candidateSize;
                minSize = candidateSize;
                bestFitMetrics = fitMetrics;
            }
            else
            {
                maxSize = candidateSize;
            }
        }

        if (!fitFound)
        {
            using Font minDiagnosticFont = new(baseFont.FontFamily, minSize, baseFont.Style, GraphicsUnit.Point);
            HeaderRow1FitMetrics minFitMetrics = GetHeaderRow1FitMetrics(
                minDiagnosticFont,
                rowWidth,
                pageText,
                totalText,
                usedText,
                freeText,
                clockText);
            LogAdaptiveFontDiag("ROW1_TOTAL_FIT_NG", baseFont, rowWidth, minSize, false, minSize, maxSize, ratioTarget, widthScale, pageText, clockText);
            LogHeaderResponsiveDiag(
                "Row1TotalFit",
                "fit-ng",
                baseFont,
                null,
                rowWidth: minFitMetrics.RowWidth,
                leftRequiredWidth: minFitMetrics.LeftRequiredWidth,
                rightClockWidth: minFitMetrics.ClockReservedWidth,
                availableLeftWidth: minFitMetrics.AvailableLeftWidth,
                fitResult: false,
                freeMeasuredWidth: minFitMetrics.FreeWidth,
                clockMeasuredWidth: minFitMetrics.ClockMeasuredWidth,
                guardBand: minFitMetrics.GuardBand);
            return new Font(baseFont.FontFamily, minSize, baseFont.Style, GraphicsUnit.Point);
        }

        if (Math.Abs(bestSize - baseFont.Size) < 0.01f)
        {
            LogHeaderResponsiveDiag(
                "Row1TotalFit",
                "base-font-fit",
                baseFont,
                baseFont,
                rowWidth: bestFitMetrics.RowWidth,
                leftRequiredWidth: bestFitMetrics.LeftRequiredWidth,
                rightClockWidth: bestFitMetrics.ClockReservedWidth,
                availableLeftWidth: bestFitMetrics.AvailableLeftWidth,
                fitResult: true,
                freeMeasuredWidth: bestFitMetrics.FreeWidth,
                clockMeasuredWidth: bestFitMetrics.ClockMeasuredWidth,
                guardBand: bestFitMetrics.GuardBand);
            return baseFont;
        }

        LogAdaptiveFontDiag("END", baseFont, rowWidth, bestSize, fitFound, minSize, maxSize, ratioTarget, widthScale, pageText, clockText);
        using Font diagnosticFont = new(baseFont.FontFamily, bestSize, baseFont.Style, GraphicsUnit.Point);
        LogHeaderResponsiveDiag(
            "Row1TotalFit",
            "resolved-fit",
            baseFont,
            diagnosticFont,
            rowWidth: bestFitMetrics.RowWidth,
            leftRequiredWidth: bestFitMetrics.LeftRequiredWidth,
            rightClockWidth: bestFitMetrics.ClockReservedWidth,
            availableLeftWidth: bestFitMetrics.AvailableLeftWidth,
            fitResult: true,
            freeMeasuredWidth: bestFitMetrics.FreeWidth,
            clockMeasuredWidth: bestFitMetrics.ClockMeasuredWidth,
            guardBand: bestFitMetrics.GuardBand);
        return new Font(baseFont.FontFamily, bestSize, baseFont.Style, GraphicsUnit.Point);
    }
    private int GetHeaderRow2LeftRequiredWidth(Font font, string pageText, string totalText, string usedText, string freeText)
    {
        int pageWidth = HeaderLayoutHelper.MeasureRow2SegmentWidth(font, pageText, lblPage);
        int totalWidth = HeaderLayoutHelper.MeasureRow2SegmentWidth(font, totalText, lblTotal);
        int usedWidth = HeaderLayoutHelper.MeasureRow2SegmentWidth(font, usedText, lblUsed);
        int freeWidth = HeaderLayoutHelper.MeasureRow2SegmentWidth(font, freeText, lblFree);
        return pageWidth + totalWidth + usedWidth + freeWidth + (HeaderRow2ClockSafetyGap * 4);
    }
    private int GetHeaderRow1FitGuardPx(Font font)
    {
        int dpiGuard = (int)Math.Ceiling(12f * Math.Max(1, DeviceDpi) / 96f);
        int heightGuard = GetSafeHeaderFontHeight(font) / 2;
        int textGuard = HeaderLayoutHelper.MeasureDisplayWidth("00", font);
        return Math.Max(dpiGuard, Math.Max(heightGuard, textGuard));
    }
    private int GetSafeHeaderFontHeight(Font? font)
    {
        Font safeFont = font ?? SystemFonts.DefaultFont;
        try
        {
            return Math.Max(1, safeFont.Height);
        }
        catch (ArgumentException)
        {
            int fallbackHeight = Math.Max(1, TextRenderer.MeasureText("00", SystemFonts.DefaultFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height);
            LogHeaderResponsiveStabilizeDiag("FontHeightFallback", "GetHeaderRow1FitGuardPx", safeFont, null, skippedReason: "font-height-argument-exception", exceptionPrevented: true);
            return fallbackHeight;
        }
    }
    private HeaderRow1FitMetrics GetHeaderRow1FitMetrics(
        Font font,
        int rowWidth,
        string pageText,
        string totalText,
        string usedText,
        string freeText,
        string clockText)
    {
        int pageWidth = HeaderLayoutHelper.MeasureRow2SegmentWidth(font, pageText, lblPage);
        int totalWidth = HeaderLayoutHelper.MeasureRow2SegmentWidth(font, totalText, lblTotal);
        int usedWidth = HeaderLayoutHelper.MeasureRow2SegmentWidth(font, usedText, lblUsed);
        int freeWidth = HeaderLayoutHelper.MeasureRow2SegmentWidth(font, freeText, lblFree);
        int leftRequiredWidth = pageWidth + totalWidth + usedWidth + freeWidth + (HeaderRow2ClockSafetyGap * 4);
        int clockReservedWidth = GetHeaderClockReservedWidth(font);
        int clockMeasuredWidth = HeaderLayoutHelper.MeasureDisplayWidth(clockText, font);
        int guardBand = GetHeaderRow1FitGuardPx(font);
        int totalRequiredWidth = leftRequiredWidth + clockReservedWidth + HeaderRow2ClockSafetyGap + guardBand;
        int availableLeftWidth = Math.Max(0, rowWidth - clockReservedWidth - HeaderRow2ClockSafetyGap - guardBand);
        bool fits = totalRequiredWidth <= rowWidth && leftRequiredWidth <= availableLeftWidth;
        return new HeaderRow1FitMetrics(
            rowWidth,
            leftRequiredWidth,
            clockReservedWidth,
            HeaderRow2ClockSafetyGap,
            guardBand,
            totalRequiredWidth,
            availableLeftWidth,
            fits,
            pageWidth,
            totalWidth,
            usedWidth,
            freeWidth,
            clockMeasuredWidth,
            clockText,
            freeText);
    }
    private int GetHeaderClockReservedWidth(Font font)
    {
        if (lblClock == null)
        {
            return 0;
        }

        return Math.Max(0, HeaderLayoutHelper.MeasureLabelReservedWidth(lblClock, lblClock.Text, font, HeaderRow2ClockSafetyGap));
    }
    private HeaderRow1FitMetrics GetCurrentHeaderRow1FitMetrics(Font font)
    {
        return GetHeaderRow1FitMetrics(
            font,
            Math.Max(0, headerPanel?.ClientSize.Width ?? 0),
            lblPage?.Text ?? string.Empty,
            lblTotal?.Text ?? string.Empty,
            lblUsed?.Text ?? string.Empty,
            lblFree?.Text ?? string.Empty,
            lblClock?.Text ?? string.Empty);
    }
    private int GetHeaderRow2AvailableLeftWidth(Font font)
    {
        int rowWidth = Math.Max(0, headerPanel?.ClientSize.Width ?? 0);
        HeaderRow1FitMetrics metrics = GetHeaderRow1FitMetrics(
            font,
            rowWidth,
            lblPage?.Text ?? string.Empty,
            lblTotal?.Text ?? string.Empty,
            lblUsed?.Text ?? string.Empty,
            lblFree?.Text ?? string.Empty,
            lblClock?.Text ?? string.Empty);
        return metrics.AvailableLeftWidth;
    }
    private void LogHeaderRow2LayoutDiagnostics(int clockReservedWidth, int zoneAvailableWidth, HeaderLayoutHelper.ZoneWidths widths)
    {
        if (!HeaderStatusFontRouteDiagnosticLoggingEnabled)
        {
            return;
        }

        Font row2Font = lblPage?.Font ?? lblClock?.Font ?? SystemFonts.DefaultFont;
        Rectangle clockBounds = lblClock?.Bounds ?? Rectangle.Empty;
        Font clockFont = lblClock?.Font ?? row2Font;
        string pageText = lblPage?.Text ?? string.Empty;
        string totalText = lblTotal?.Text ?? string.Empty;
        string usedText = lblUsed?.Text ?? string.Empty;
        string freeText = lblFree?.Text ?? string.Empty;
        string clockText = lblClock?.Text ?? string.Empty;
        HeaderRow1FitMetrics metrics = GetHeaderRow1FitMetrics(row2Font, headerPanel.ClientSize.Width, pageText, totalText, usedText, freeText, clockText);
        int clockLeft = Math.Max(0, headerPanel.ClientSize.Width - metrics.ClockReservedWidth);
        int zoneWidthsTotal = widths.Zone1 + widths.Zone2 + widths.Zone3 + widths.Zone4;
        LogService.Info(
            $"[HeaderRow2LayoutDiag] panel={headerPanel.ClientSize} lblClock.Bounds={clockBounds} lblClock.Text='{clockText}' lblClock.Font.Size={clockFont.Size:0.##} " +
            $"clockMeasuredWidth={metrics.ClockMeasuredWidth} clockReservedWidth={metrics.ClockReservedWidth} zoneAvailableWidth={zoneAvailableWidth} " +
            $"pageMeasuredWidth={metrics.PageWidth} totalMeasuredWidth={metrics.TotalWidth} usedMeasuredWidth={metrics.UsedWidth} freeMeasuredWidth={metrics.FreeWidth} leftRequiredWidth={metrics.LeftRequiredWidth} " +
            $"guardBand={metrics.GuardBand} totalRequiredWidth={metrics.TotalRequiredWidth} availableLeftWidth={metrics.AvailableLeftWidth} fitResult={metrics.Fits} " +
            $"zone1={headerZone1.Bounds} zone2={headerZone2.Bounds} zone3={headerZone3.Bounds} zone4={headerZone4.Bounds} " +
            $"zoneTexts=[{pageText}|{totalText}|{usedText}|{freeText}] " +
            $"zoneRights=[{headerZone1.Right},{headerZone2.Right},{headerZone3.Right},{headerZone4.Right}] clockLeft={clockLeft} " +
            $"clockMargin={clockLeft - headerZone4.Right} fits={headerZone4.Right <= clockLeft - metrics.SafetyGap - metrics.GuardBand} " +
            $"zoneWidths=[{widths.Zone1},{widths.Zone2},{widths.Zone3},{widths.Zone4}] zoneWidthsTotal={zoneWidthsTotal}");
    }
    /// <summary>
    /// Phase 5-viewer-status-finefix1: Viewer の状態表示を NotificationService 経由で永続的に適用する。
    /// これにより自動リセットタイマー（"Ready." への復帰）を阻止する。
    /// </summary>
    private void ApplyViewerStatusLine(string reason = "")
    {
        NormalizeStatusLabelLayout();
        string line = GetViewerStatusLine();
        _notificationService.SetPersistent(line);
        NormalizeStatusLabelLayout();
        statusStrip.Invalidate();
        statusStrip.Update();
        LogViewerStatusRoute(reason, line);
    }
    private void LogViewerStatusRoute(string reason, string line)
    {
        string statusText = statusLabel?.Text ?? "<null>";
        string statusVisible = statusStrip != null && statusLabel != null
            ? $"{statusStrip.Visible}/{statusLabel.Visible}"
            : "<null>";
        string safeReason = string.IsNullOrWhiteSpace(reason) ? "-" : reason;
        long elapsedMs = _largeTextEntryStopwatch.IsRunning ? _largeTextEntryStopwatch.ElapsedMilliseconds : -1;
        LogService.Info(
            $"[LargeTextStatusVisual] Reason={safeReason} elapsedMs={elapsedMs} UiMode={_uiMode} Kind={_currentViewerKind} " +
            $"HasLargeState={_largeFileState != null} " +
            $"Enc={_largeFileState?.DetectedEncodingLabel ?? "<null>"} " +
            $"StatusVisible={statusVisible} " +
            $"StatusBounds={statusStrip?.Bounds} LabelBounds={statusLabel?.Bounds} " +
            $"StatusText={statusText} " +
            $"Line={line}");
    }
    private void LogLargeTextEntryTiming(
        string stage,
        Stopwatch sw,
        string path,
        int reqId,
        PreviewKind kind,
        Models.LargeFilePreviewState? state = null,
        string? currentPath = null)
    {
        string statusText = statusLabel?.Text ?? "<null>";
        long largeTextElapsedMs = _largeTextEntryStopwatch.IsRunning ? _largeTextEntryStopwatch.ElapsedMilliseconds : -1;
        LogService.Info(
            $"[LargeTextEntryTiming] {stage} elapsedMs={sw.ElapsedMilliseconds} " +
            $"totalElapsedMs={sw.ElapsedMilliseconds} " +
            $"largeTextElapsedMs={largeTextElapsedMs} " +
            $"reqId={reqId} uiMode={_uiMode} kind={kind} " +
            $"requestPath='{path}' " +
            $"currentPath='{currentPath ?? "<not-read>"}' " +
            $"enc='{state?.DetectedEncodingLabel ?? "<null>"}' " +
            $"hasBom={state?.HasBom.ToString() ?? "<null>"} " +
            $"offsets={state?.LineOffsets.Count ?? -1} " +
            $"isIndexing={state?.IsIndexing.ToString() ?? "<null>"} " +
            $"statusSnapshot='{statusText}'");
    }
    private void LogViewerLayoutBounds(string reason)
    {
        if (statusStrip == null || statusLabel == null
            || outerHostPanel == null || contentFramePanel == null
            || mainAreaPanel == null || viewerPanel == null
            || _largeFileControl == null || viewerTextBox == null || viewerMessageLabel == null)
        {
            return;
        }
        Rectangle ToScreenRect(Control c) => new(c.PointToScreen(Point.Empty), c.Size);
        Rectangle statusRect = ToScreenRect(statusStrip);
        Rectangle largeRect = ToScreenRect(_largeFileControl);
        bool overlapsStatus = largeRect.IntersectsWith(statusRect);
        LogService.Info(
            $"[ViewerLayoutBounds] Reason={reason} " +
            $"FormClient={ClientRectangle} " +
            $"StatusStrip Bounds={statusStrip.Bounds} Screen={statusRect} Visible={statusStrip.Visible} Dock={statusStrip.Dock} Parent={statusStrip.Parent?.Name} " +
            $"Outer Bounds={outerHostPanel.Bounds} Screen={ToScreenRect(outerHostPanel)} Visible={outerHostPanel.Visible} Dock={outerHostPanel.Dock} Parent={outerHostPanel.Parent?.Name} " +
            $"ContentFrame Bounds={contentFramePanel.Bounds} Screen={ToScreenRect(contentFramePanel)} Visible={contentFramePanel.Visible} Dock={contentFramePanel.Dock} Parent={contentFramePanel.Parent?.Name} " +
            $"MainArea Bounds={mainAreaPanel.Bounds} Screen={ToScreenRect(mainAreaPanel)} Visible={mainAreaPanel.Visible} Dock={mainAreaPanel.Dock} Parent={mainAreaPanel.Parent?.Name} " +
            $"ViewerPanel Bounds={viewerPanel.Bounds} Screen={ToScreenRect(viewerPanel)} Visible={viewerPanel.Visible} Dock={viewerPanel.Dock} Parent={viewerPanel.Parent?.Name} " +
            $"LargeControl Bounds={_largeFileControl.Bounds} Screen={largeRect} Visible={_largeFileControl.Visible} Dock={_largeFileControl.Dock} Parent={_largeFileControl.Parent?.Name} " +
            $"ViewerText Bounds={viewerTextBox.Bounds} Screen={ToScreenRect(viewerTextBox)} Visible={viewerTextBox.Visible} Dock={viewerTextBox.Dock} Parent={viewerTextBox.Parent?.Name} " +
            $"ViewerMessage Bounds={viewerMessageLabel.Bounds} Screen={ToScreenRect(viewerMessageLabel)} Visible={viewerMessageLabel.Visible} Dock={viewerMessageLabel.Dock} Parent={viewerMessageLabel.Parent?.Name} " +
            $"StatusText='{statusLabel.Text}' OverlapsStatus={overlapsStatus}");
    }
    private bool TryCopyLargeFileVisibleText()
    {
        _ = TryCopyLargeFileVisibleTextAsync();
        return true;
    }
    private async Task<bool> TryCopyLargeFileVisibleTextAsync()
    {
        if (_currentViewerKind != PreviewKind.LargeText || _largeFileState == null)
            return false;
        if (_largeFileControl.TryGetCharacterSelectionRange(out var rawRange))
        {
            var range = NormalizeCharacterSelectionRange(rawRange);
            return await TryCopyLargeFileCharacterSelectionAsync(range, _previewRequestCoordinator.Token);
        }
        bool hasSelection = _largeFileControl.HasSelectedLines;
        int selectedLineCount = _largeFileControl.SelectedLineCount;
        var text = hasSelection
                ? _largeFileControl.GetSelectedText()
                : _largeFileControl.GetVisibleText();
        if (string.IsNullOrEmpty(text))
        {
            ShowStatusMessage("コピー対象がありません。");
            return true;
        }
        try
        {
            Clipboard.SetText(text);
            if (hasSelection)
            {
                ShowStatusMessage($"選択した {selectedLineCount:N0} 行をコピーしました。");
            }
            else
            {
                ShowStatusMessage("表示中の行をコピーしました。");
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[LargeTextCopy] Failed to copy visible text: {ex.Message}");
            ShowStatusMessage("コピーに失敗しました。");
        }
        return true;
    }
    private static Controls.LargeFilePreviewControl.CharacterSelectionRange NormalizeCharacterSelectionRange(
        Controls.LargeFilePreviewControl.CharacterSelectionRange range)
    {
        if (range.StartLine < range.EndLine)
        {
            return range;
        }
        if (range.StartLine > range.EndLine)
        {
            return new Controls.LargeFilePreviewControl.CharacterSelectionRange(
                range.EndLine,
                range.EndColumn,
                range.StartLine,
                range.StartColumn);
        }
        if (range.StartColumn <= range.EndColumn)
        {
            return range;
        }
        return new Controls.LargeFilePreviewControl.CharacterSelectionRange(
            range.EndLine,
            range.EndColumn,
            range.StartLine,
            range.StartColumn);
    }
    private async Task<bool> TryCopyLargeFileCharacterSelectionAsync(
        Controls.LargeFilePreviewControl.CharacterSelectionRange range,
        CancellationToken token)
    {
        if (_largeFileState == null)
        {
            return false;
        }
        // 引数の range は既に正規化されている前提。
        int startLine = range.StartLine;
        int endLine = range.EndLine;
        int lineCount = endLine - startLine + 1;
        if (lineCount <= 0)
        {
            return false;
        }
        long estimatedBytes = EstimateLargeTextSelectionBytes(_largeFileState, startLine, endLine);
        if (IsLargeTextClipboardCopyTooLarge(lineCount, estimatedBytes))
        {
            var result = ShowLargeTextClipboardCopyConfirmationDialog(lineCount, estimatedBytes);
            if (result == DialogResult.Yes)
            {
                await ExportLargeTextCharacterSelectionAsync(range, estimatedBytes, token);
            }
            else
            {
                ShowStatusMessage("大量コピーをキャンセルしました。");
            }
            return true;
        }
        try
        {
            var lines = await LargeFileLineReaderService.ReadLinesAsync(
                _largeFileState,
                startLine,
                lineCount,
                GetCurrentViewerEncoding(),
                token);
            string selectedText = BuildCharacterSelectionText(range, startLine, lines);
            if (string.IsNullOrEmpty(selectedText))
            {
                return false;
            }
            Clipboard.SetText(selectedText);
            if (Clipboard.ContainsText())
            {
                ShowStatusMessage("選択範囲をコピーしました。");
                return true;
            }
            else
            {
                ShowStatusMessage("コピーに失敗した可能性があります。");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogService.Error($"[LargeTextCopy] Failed to copy character selection: {ex.Message}");
            ShowStatusMessage("コピーに失敗しました。");
            return false;
        }
    }
    private static long EstimateLargeTextSelectionBytes(LargeFilePreviewState state, int startLine, int endLine)
    {
        if (state.LineOffsets.Count == 0)
        {
            return 0;
        }
        int safeStart = Math.Clamp(startLine, 0, state.LineOffsets.Count - 1);
        int safeEnd = Math.Clamp(endLine, 0, state.LineOffsets.Count - 1);
        long startOffset = state.LineOffsets[safeStart];
        long endOffset = safeEnd + 1 < state.LineOffsets.Count
            ? state.LineOffsets[safeEnd + 1]
            : state.TotalBytes;
        return Math.Max(0, endOffset - startOffset);
    }
    private bool IsLargeTextClipboardCopyTooLarge(int lineCount, long estimatedBytes)
    {
        return lineCount > LargeTextClipboardCopyMaxLines
            || estimatedBytes > LargeTextClipboardCopyMaxBytesEstimate;
    }
    private sealed record LargeTextExportResult(
        int ExpectedLineCount,
        int WrittenLineCount,
        int StartLine,
        int EndLine,
        string? FirstWrittenLinePreview,
        string? LastWrittenLinePreview);
    private async Task ExportLargeTextCharacterSelectionAsync(
        Controls.LargeFilePreviewControl.CharacterSelectionRange normalized,
        long estimatedBytes,
        CancellationToken token)
    {
        if (_largeFileState == null)
        {
            return;
        }
        int expectedLineCount = normalized.EndLine - normalized.StartLine + 1;
        LogService.Info(
            $"[LargeTextExport] Start " +
            $"range=({normalized.StartLine}:{normalized.StartColumn})-({normalized.EndLine}:{normalized.EndColumn}) " +
            $"expectedLines={expectedLineCount:N0} " +
            $"totalLines={_largeFileState.TotalLines:N0} " +
            $"offsets={_largeFileState.LineOffsets.Count:N0} " +
            $"estimatedBytes={estimatedBytes:N0}");
        using var dialog = new SaveFileDialog
        {
            Title = "選択範囲を保存",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"large_text_selection_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            ShowStatusMessage("選択範囲の保存をキャンセルしました。");
            return;
        }
        try
        {
            var result = await WriteLargeTextCharacterSelectionToFileAsync(normalized, dialog.FileName, token);
            LogService.Info(
                $"[LargeTextExport] Completed " +
                $"expectedLines={result.ExpectedLineCount:N0} " +
                $"writtenLines={result.WrittenLineCount:N0} " +
                $"range=({result.StartLine})-({result.EndLine}) " +
                $"first='{result.FirstWrittenLinePreview}' " +
                $"last='{result.LastWrittenLinePreview}'");
            if (result.WrittenLineCount != result.ExpectedLineCount)
            {
                ShowStatusMessage("選択範囲の保存が途中で終了しました。");
                MessageBox.Show(
                    $"選択範囲の保存行数が一致しません。\n\n" +
                    $"期待: {result.ExpectedLineCount:N0} 行\n" +
                    $"実際: {result.WrittenLineCount:N0} 行\n\n" +
                    "インデックス作成が完了していない可能性があります。",
                    "LargeText 選択範囲保存",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
            ShowStatusMessage($"選択範囲を保存しました: {Path.GetFileName(dialog.FileName)}");
        }
        catch (OperationCanceledException)
        {
            ShowStatusMessage("選択範囲の保存を中断しました。");
        }
        catch (Exception ex)
        {
            LogService.Error($"[LargeTextCopy] Failed to export character selection: {ex.Message}");
            ShowStatusMessage("選択範囲の保存に失敗しました。");
            MessageBox.Show(
                $"選択範囲の保存に失敗しました。\n{ex.Message}",
                "LargeText 選択範囲保存",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
    private async Task<LargeTextExportResult> WriteLargeTextCharacterSelectionToFileAsync(
        Controls.LargeFilePreviewControl.CharacterSelectionRange normalized,
        string outputPath,
        CancellationToken token)
    {
        if (_largeFileState == null)
        {
            throw new InvalidOperationException("LargeText state is not available.");
        }
        int startLine = normalized.StartLine;
        int endLine = normalized.EndLine;
        int expectedLineCount = endLine - startLine + 1;
        if (expectedLineCount <= 0)
        {
            throw new InvalidOperationException("Invalid selection range.");
        }
        const int ChunkLines = 4096;
        int writtenLineCount = 0;
        string? firstPreview = null;
        string? lastPreview = null;
        using var writer = new StreamWriter(
            outputPath,
            false,
            _largeFileState.DetectedEncoding ?? GetCurrentViewerEncoding());
        for (int line = startLine; line <= endLine; line += ChunkLines)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(ChunkLines, endLine - line + 1);
            var lines = await LargeFileLineReaderService.ReadLinesAsync(
                _largeFileState,
                line,
                count,
                GetCurrentViewerEncoding(),
                token);
            if (lines.Count != count)
            {
                // ここで読み込み不足をエラーにする (インデックス未完了等のケースを救う)
                throw new IOException(
                    $"LargeText export read count mismatch. requestedStart={line}, requestedCount={count}, actualCount={lines.Count}, offsets={_largeFileState.LineOffsets.Count}, totalLines={_largeFileState.TotalLines}");
            }
            for (int i = 0; i < lines.Count; i++)
            {
                int absoluteLine = line + i;
                string text = lines[i] ?? string.Empty;
                int from = absoluteLine == startLine
                    ? Math.Min(normalized.StartColumn, text.Length)
                    : 0;
                int to = absoluteLine == endLine
                    ? Math.Min(normalized.EndColumn, text.Length)
                    : text.Length;
                if (to < from)
                {
                    (from, to) = (to, from);
                }
                string part = to > from
                    ? text.Substring(from, to - from)
                    : string.Empty;
                if (firstPreview == null)
                {
                    firstPreview = part.Length > 80 ? part.Substring(0, 80) : part;
                }
                lastPreview = part.Length > 80 ? part.Substring(0, 80) : part;
                if (part.Length > 0)
                {
                    await writer.WriteAsync(part.AsMemory(), token);
                }
                writtenLineCount++;
                if (absoluteLine < endLine)
                {
                    await writer.WriteLineAsync();
                }
            }
        }
        await writer.FlushAsync(token);
        return new LargeTextExportResult(
            expectedLineCount,
            writtenLineCount,
            startLine,
            endLine,
            firstPreview,
            lastPreview);
    }
    private string BuildCharacterSelectionText(
        Controls.LargeFilePreviewControl.CharacterSelectionRange normalized,
        int loadedStartLine,
        IReadOnlyList<string> lines)
    {
        int startLine = normalized.StartLine;
        int endLine = normalized.EndLine;
        int startColumn = normalized.StartColumn;
        int endColumn = normalized.EndColumn;
        var result = new List<string>();
        int totalChars = 0;
        for (int absoluteLine = startLine; absoluteLine <= endLine; absoluteLine++)
        {
            int index = absoluteLine - loadedStartLine;
            if (index < 0 || index >= lines.Count)
            {
                continue;
            }
            string text = lines[index] ?? string.Empty;
            int from = absoluteLine == startLine ? startColumn : 0;
            int to = absoluteLine == endLine ? endColumn : text.Length;
            from = Math.Clamp(from, 0, text.Length);
            to = Math.Clamp(to, 0, text.Length);
            if (to < from)
            {
                (from, to) = (to, from);
            }
            string part = text.Substring(from, to - from);
            totalChars += part.Length;
            if (totalChars > LargeTextClipboardCopyMaxChars)
            {
                // ここで中断する（上位で検知済みのはずだが、安全のため）
                break;
            }
            result.Add(part);
        }
        return string.Join(Environment.NewLine, result);
    }
    private bool TryExitViewerToBrowser()
    {
        if (_uiMode != UIMode.Viewer)
            return false;
        // モード切り替え前に現在の表示内容を先に隠す (ちらつき抑制)
        HideViewerContentBeforeExit();
        SwitchUIMode(UIMode.Browser);
        TryProcessPendingCurrentDirectoryRefresh("TryExitViewerToBrowser");
        return true;
    }
    private void EnsureBrowserModeBeforeWorkspaceNavigation()
    {
        // プレビュー内容をクリア（Popup等も含む）
        ClearPreview();
        if (_uiMode != UIMode.Viewer)
            return;
        // タブ/カテゴリ切替前に表示内容を消し、Browserモードへ強制復帰させる
        HideViewerContentBeforeExit();
        SwitchUIMode(UIMode.Browser);
    }
    private void HideViewerContentBeforeExit()
    {
        if (viewerPanel == null || viewerPanel.IsDisposed)
            return;
        viewerPanel.SuspendLayout();
        try
        {
            if (_largeFileControl != null)
                _largeFileControl.Visible = false;
            if (viewerTextBox != null)
                viewerTextBox.Visible = false;
            if (viewerPictureBox != null)
                viewerPictureBox.Visible = false;
            if (viewerMessageLabel != null)
                viewerMessageLabel.Visible = false;
        }
        finally
        {
            viewerPanel.ResumeLayout(false);
        }
        viewerPanel.Update(); // 即座に画面から消す
    }
    private void ExecuteCurrentFileAction(string fullPath)
    {
        ExecuteBrowserOpenRequest(CreateBrowserOpenRequest(fullPath, allowExecuteTarget: true));
    }
    private void ExecuteConfirmedFile(string fullPath)
    {
        string fileName = Path.GetFileName(fullPath);
        var result = MessageBox.Show(
            $"{fileName} を実行しますか？",
            "eXecute",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.OK)
        {
            ShowStatusMessage("実行はキャンセルされました。");
            return;
        }
        OpenPathWithShellAssociation(fullPath);
    }
    private void OpenPathWithShellAssociation(string fullPath)
    {
        string? error = ExternalToolService.OpenWithShellAssociation(fullPath);
        if (error != null)
        {
            ShowStatusMessage(error);
            MessageBox.Show(this, $"関連付けられたアプリで開くことができませんでした。\n理由: {error}", "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void ShowArchiveContentsOrFallback(string archivePath)
    {
        ArchiveListResult result = ArchiveListService.GetArchiveContents(_settings.SevenZip?.ExePath, archivePath);
        if (result.Success)
        {
            LogService.Info($"Archive contents loaded: {archivePath} Entries={result.Entries.Count}");
            ShowStatusMessage($"archive 内容一覧を表示します: {Path.GetFileName(archivePath)}");
            bool isReadOnly = IsActiveBrowserTabReadOnly();
            using var dialog = new ArchiveListDialog(
                archivePath,
                result.Entries,
                _navigationService.CurrentPath,
                isReadOnly,
                _settings.Appearance?.DateFormat,
                _settings.Appearance?.SizeFormat,
                _settings.SevenZip?.ExePath);
            dialog.ShowDialog(this);
            if (dialog.PendingExtractRequest != null)
            {
                _ = ExecuteArchiveExtractAsync(dialog.PendingExtractRequest);
            }
            return;
        }
        string fallbackMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? "archive 内容一覧を取得できないため、関連付けで開きます。"
            : $"{result.ErrorMessage} 関連付けで開きます。";
        ShowStatusMessage(fallbackMessage);
        OpenPathWithShellAssociation(archivePath);
    }
    private string BuildMissingSevenZipMessage(string operationLabel)
    {
        return SevenZipService.BuildUnavailableMessage(_settings.SevenZip?.ExePath, operationLabel);
    }
    private bool TryResolveSevenZipPath(string operationLabel, out string exePath)
    {
        exePath = SevenZipService.ResolveExecutable(_settings.SevenZip?.ExePath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            return true;
        }
        string message = BuildMissingSevenZipMessage(operationLabel);
        ShowStatusMessage(message);
        MessageBox.Show(message, "7-Zip が必要です", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
    private async Task ExecuteArchiveExtractAsync(ArchiveExtractRequest request)
    {
        if (GuardReadOnlyBrowserTab("解凍")) return;
        if (GuardMutationBusy("解凍"))
        {
            return;
        }
        ArchiveExtractResult result;
        CancellationToken token = PrepareFileOperation("archive 解凍");
        string archiveName = Path.GetFileName(request.ArchivePath);
        string countLabel = request.ExtractAll
            ? "すべて"
            : $"{request.EntryPaths.Count} 件";
        try
        {
            ShowStatusMessage($"archive 解凍中: {countLabel} / {archiveName}");
            result = await Task.Run(
                () => ArchiveExtractService.ExtractSelection(
                    _settings.SevenZip?.ExePath,
                    request,
                    token,
                    _ =>
                    {
                        if (!IsDisposed && IsHandleCreated)
                        {
                            BeginInvoke(new Action(() => ShowStatusMessage($"archive 解凍中: {countLabel} / {archiveName}")));
                        }
                    }),
                token);
        }
        catch (OperationCanceledException)
        {
            ShowStatusMessage("archive 解凍は中断されました。");
            return;
        }
        finally
        {
            FinalizeFileOperation();
        }
        if (result.Success)
        {
            LogService.Info($"Archive extract succeeded: {archiveName} -> {request.DestinationDirectory}");
            ShowStatusMessage($"archive 解凍完了: {countLabel} / {archiveName}");
            if (request.DestinationDirectory.StartsWith(_navigationService.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                LoadDirectory(_navigationService.CurrentPath);
            }
            return;
        }
        string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? "archive 解凍に失敗しました。"
            : result.ErrorMessage;
        ShowStatusMessage(message);
        MessageBox.Show(message, "archive 解凍", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    private static bool IsExecuteTarget(string fullPath)
    {
        return _executeTargetExtensions.Contains(Path.GetExtension(fullPath));
    }
    private static bool IsArchiveTarget(string fullPath)
    {
        return ArchiveFileTypeHelper.IsArchive(fullPath);
    }
    private async Task ExecuteCopy(SelectionResult? selectionSnapshot = null)
    {
        if (GuardMutationBusy("コピー")) return;
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            _fileOpUiState.ActiveOperationName,
            _fileOpUiState.Cts != null,
            "コピー",
            ResolveSelection(selectionSnapshot),
            "コピー対象がありません。",
            busyOperationName: "Copy",
            isCancelRequested: _fileOpUiState.Cts?.IsCancellationRequested ?? false);
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        var selection = entryPlan.Selection;
        if (!TryResolveMultiMarkSelectionAction("コピー", "コピーをキャンセルしました。", selection, out selection))
        {
            return;
        }
        string selectionSummary = BuildSelectionSummaryText(selection);
        string? outsideWarning = BuildSelectionOutsideCurrentDirectoryWarning(selection);
        // WinFD風にコピー先ディレクトリ名を入力させる
        if (!_fileOperationDialogCoordinator.TrySelectDestinationDirectory(
                this,
                _navigationService,
                "コピー先ディレクトリを入力してください:",
                "Copy",
                "コピー",
                "コピーはキャンセルされました。",
                ShowStatusMessage,
                selectionSummary,
                outsideWarning,
                GetSharedDirectoryMoveHistory(),
                out string destDir,
                out bool copyNeedsCreateDirectory))
        {
            return;
        }
        if (!TryBuildCopyFinalPlan(selection.FullPaths, destDir, out IReadOnlyList<CopyFinalAction> finalCopyPlan))
        {
            ShowStatusMessage("コピーはキャンセルされました。");
            return;
        }
        LinkOperationPlan linkPlan = LinkOperationPlanService.BuildCopyPlan(
            finalCopyPlan.Select(action => new LinkOperationRoot(action.SourcePath, action.DestinationPath)));
        LinkOperationPlan helperLinkPlan = LinkOperationPlanService.BuildCopyPlan(
            finalCopyPlan
                .Where(action => !action.Skip)
                .Select(action => new LinkOperationRoot(action.SourcePath, action.DestinationPath)));
        LinkOperationDecision linkDecision = LinkOperationDecision.Preserve;
        if (helperLinkPlan.Items.Count > 0)
        {
            linkDecision = LinkOperationDecisionDialog.Show(this, linkPlan);
            if (linkDecision == LinkOperationDecision.Cancel)
            {
                ShowStatusMessage("コピーはキャンセルされました。");
                return;
            }
        }
        var excludedLinkSources = linkPlan.Items.Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var partialTopLevelSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var successfulTopLevelLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int preSuccessfulTopLevelLinkCount = 0;
        int preSkippedLinkCount = linkDecision == LinkOperationDecision.Skip
            ? linkPlan.Items.Count(item => item.IsTopLevel) + (linkPlan.Items.Any(item => !item.IsTopLevel) ? 1 : 0)
            : 0;
        int preFailedLinkCount = 0;
        foreach (LinkOperationPlanItem item in linkPlan.Items.Where(item =>
                     linkDecision == LinkOperationDecision.Skip ||
                     finalCopyPlan.Any(action => action.Skip && string.Equals(action.SourcePath, item.SourcePath, StringComparison.OrdinalIgnoreCase))))
        {
            partialTopLevelSources.Add(item.TopLevelSourcePath);
        }
        List<string> createdLinkParents = new();
        if (helperLinkPlan.Items.Count > 0 && linkDecision == LinkOperationDecision.Preserve)
        {
            createdLinkParents = LinkOperationPreparationService.EnsureDestinationParents(helperLinkPlan);
            try
            {
                var helperItems = helperLinkPlan.Items.Select((item, index) => new MidFD.FileOperationHelperProtocol.ElevatedLinkCopyItem
                {
                    ItemId = $"link-{index}",
                    SourcePath = item.SourcePath,
                    DestinationPath = item.DestinationPath,
                    ExpectedKind = item.Kind.ToString()
                }).ToList();
                MidFD.FileOperationHelperProtocol.ElevatedLinkCopyResponse helperResponse = await new ElevatedLinkCopyClient().CopyAsync(helperItems, CancellationToken.None);
                var helperById = helperResponse.Results.ToDictionary(result => result.ItemId, StringComparer.Ordinal);
                for (int index = 0; index < helperLinkPlan.Items.Count; index++)
                {
                    LinkOperationPlanItem item = helperLinkPlan.Items[index];
                    MidFD.FileOperationHelperProtocol.ElevatedLinkCopyResult result = helperById[$"link-{index}"];
                    if (result.Status == "success" && item.IsTopLevel)
                    {
                        successfulTopLevelLinks.Add(item.SourcePath);
                        preSuccessfulTopLevelLinkCount++;
                    }
                    if (result.Status != "success")
                    {
                        partialTopLevelSources.Add(item.TopLevelSourcePath);
                        if (item.IsTopLevel) preFailedLinkCount++;
                        else preFailedLinkCount = Math.Max(1, preFailedLinkCount);
                    }
                }
            }
            catch (ElevatedLinkCopyCanceledException)
            {
                LinkOperationPreparationService.CleanupCreatedParents(createdLinkParents);
                ShowStatusMessage("リンク保持処理はUACキャンセルにより中止されました。");
                return;
            }
            catch (Exception ex)
            {
                LinkOperationPreparationService.CleanupCreatedParents(createdLinkParents);
                _fileOperationDialogCoordinator.ShowOperationError(this, "コピー", "リンク", ex.Message);
                return;
            }
        }
        if (copyNeedsCreateDirectory && GuardReadOnlyBrowserTab("フォルダ作成"))
        {
            LinkOperationPreparationService.CleanupCreatedParents(createdLinkParents);
            return;
        }
        if (!_fileOperationDialogCoordinator.EnsureDestinationDirectory(this, destDir, copyNeedsCreateDirectory))
        {
            LinkOperationPreparationService.CleanupCreatedParents(createdLinkParents);
            return;
        }
        // サブディレクトリや別ディレクトリへのコピーの場合、元ファイルはカレントに残るため
        // コピー操作開始時にフォーカスが当たっていたファイルをそのまま維持する
        string? currentTargetName = null;
        var currentItem = GetCurrentBrowserItem();
        if (currentItem != null && currentItem.Text != "..")
        {
            currentTargetName = currentItem.Text;
            if (!IsDirectoryListItem(currentItem) && currentItem.SubItems.Count > 1 && !string.IsNullOrEmpty(currentItem.SubItems[1].Text))
            {
                currentTargetName += "." + currentItem.SubItems[1].Text;
            }
        }
        int successCount = 0;
        int totalCount = selection.FullPaths.Count;
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;
        int skipCount = 0;
        int failCount = 0;
        // 非同期実行の準備
        if (GuardMutationBusy("コピー")) return;
        var token = PrepareFileOperation(entryPlan.BusyOperationName);
        int copyStatusVersion = _fileOpUiState.StatusVersion;
        ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage("Copy", totalCount, destDir));
        StartFileOperationProgressIndicator("Copy", totalCount);
        IProgress<FileOperationProgress> progress = _fileOperationDialogCoordinator.CreateOperationProgress(
            "Copy",
            message => ShowFileOperationStatusIfCurrent(
                copyStatusVersion,
                (_fileOpUiState.Cts?.IsCancellationRequested ?? false)
                    ? FileOperationPresentationHelper.GetCancelRequestedMessage(_fileOpUiState.ActiveOperationName ?? "Copy")
                    : message),
            p => UpdateFileOperationProgressIndicatorIfCurrent(copyStatusVersion, "Copy", p.ProcessedCount, p.TotalCount));
        try
        {
            var result = await Task.Run(() =>
            {
                int currentSuccess = 0;
                FileOpExitStatus status = FileOpExitStatus.Success;
                int currentSkipCount = 0;
                int currentFailCount = 0;
                foreach (CopyFinalAction action in finalCopyPlan)
                {
                    if (token.IsCancellationRequested)
                    {
                        status = FileOpExitStatus.Canceled;
                        break;
                    }
                    string sourcePath = action.SourcePath;
                    string fileName = Path.GetFileName(action.DestinationPath);
                    if (excludedLinkSources.Contains(sourcePath))
                    {
                        continue;
                    }
                    string destPath = action.DestinationPath;
                    progress.Report(new FileOperationProgress(currentSuccess + 1, totalCount, fileName));
                    if (action.Skip)
                    {
                        currentSkipCount++;
                        continue;
                    }
                    if (action.Merge)
                    {
                        try
                        {
                            CopyCollisionDecision? mergeFileDecision = null;
                            CopyDirectoryIntoExisting(sourcePath, destPath, ref mergeFileDecision, token, excludedLinkSources);
                            currentSuccess++;
                            if (!partialTopLevelSources.Contains(sourcePath))
                                this.Invoke(() => UnmarkPath(sourcePath));
                        }
                        catch (OperationCanceledException)
                        {
                            status = FileOpExitStatus.Canceled;
                            break;
                        }
                        catch (Exception ex)
                        {
                            this.Invoke(() => _fileOperationDialogCoordinator.ShowOperationError(this, "コピー", fileName, ex.Message));
                            currentFailCount++;
                            status = FileOpExitStatus.Error;
                        }
                        continue;
                    }
                    try
                    {
                        FileOperationService.Copy(sourcePath, destPath, excludedLinkSources);
                        currentSuccess++;
                        if (!partialTopLevelSources.Contains(sourcePath))
                            this.Invoke(() => UnmarkPath(sourcePath)); // 成功した分だけマークを外す
                    }
                    catch (Exception ex)
                    {
                        this.Invoke(() => _fileOperationDialogCoordinator.ShowOperationError(this, "コピー", fileName, ex.Message));
                        currentFailCount++;
                        status = FileOpExitStatus.Error;
                        break;
                    }
                }
                return (currentSuccess, status, currentSkipCount, currentFailCount);
            }, token);
            successCount = result.currentSuccess + preSuccessfulTopLevelLinkCount;
            skipCount = result.currentSkipCount + preSkippedLinkCount;
            failCount = result.currentFailCount + preFailedLinkCount;
            foreach (string sourcePath in successfulTopLevelLinks)
            {
                if (!partialTopLevelSources.Contains(sourcePath))
                    UnmarkPath(sourcePath);
            }
            exitStatus = FileOperationPresentationHelper.NormalizeExitStatus(result.status, successCount, selection.Count, skipCount, failCount);
            if (exitStatus == FileOpExitStatus.Success && successCount > 0)
            {
                AddDirectoryMoveHistory(destDir);
            }
        }
        catch (OperationCanceledException)
        {
            exitStatus = FileOpExitStatus.Canceled;
        }
        catch (Exception ex)
        {
            exitStatus = FileOpExitStatus.Error;
            LogService.Error("ExecuteCopy async error", ex);
            _fileOperationDialogCoordinator.ShowUnexpectedOperationError(this, "コピー", ex);
        }
        finally
        {
            HandlePostOperation(_fileOperationPostOperationCoordinator.CreateCopyResult(exitStatus, successCount, selection.Count, currentTargetName, destDir,
                skipCount: skipCount, failCount: failCount));
        }
    }

    private sealed record LinkPreparation(
        LinkOperationPlan Plan,
        HashSet<string> ExcludedSources,
        HashSet<string> SuccessfulSources,
        HashSet<string> SuccessfulTopLevelSources,
        HashSet<string> PartialTopLevelSources,
        int SkipCount,
        int FailCount,
        bool Canceled);

    private async Task<LinkPreparation> PreparePasteLinksAsync(
        IReadOnlyList<LinkOperationRoot> roots,
        bool allowHelper,
        CancellationToken cancellationToken)
    {
        LinkOperationPreparationResult result = await LinkOperationPreparationService.PrepareAsync(
            roots,
            allowHelper,
            plan => LinkOperationDecisionDialog.Show(this, plan),
            LinkOperationPreparationService.EnsureDestinationParents,
            LinkOperationPreparationService.CleanupCreatedParents,
            (items, token) => new ElevatedLinkCopyClient().CopyAsync(items, token),
            "paste-link",
            cancellationToken);
        return new LinkPreparation(
            result.Plan,
            result.ExcludedSources,
            result.SuccessfulSources,
            result.SuccessfulTopLevelSources,
            result.PartialTopLevelSources,
            result.SkipCount,
            result.FailCount,
            result.Canceled);
    }

    private sealed record CopyFinalAction(string SourcePath, string DestinationPath, bool Merge, bool Skip);

    private sealed record PasteFinalAction(
        string SourcePath,
        string DestinationPath,
        bool Merge,
        bool Skip,
        bool OverwriteMove,
        bool UsedRenameCopy,
        string? RenameTargetName);

    private sealed record MoveFinalAction(
        string SourcePath,
        string DestinationPath,
        bool Merge,
        bool Skip,
        bool Overwrite);

    private bool TryBuildCopyFinalPlan(
        IReadOnlyList<string> sources,
        string destinationDirectory,
        out IReadOnlyList<CopyFinalAction> actions)
    {
        var result = new List<CopyFinalAction>();
        CopyCollisionDecision? fileDecision = null;
        DirectoryMergeDecision? directoryDecision = null;
        bool renameSameDirectoryToAll = false;
        foreach (string sourcePath in sources)
        {
            string fileName = Path.GetFileName(sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, fileName);
            bool sourceIsDirectory = !ReparsePointHelper.IsReparsePoint(sourcePath)
                && FileOperationService.IsDirectoryPath(sourcePath);
            bool destinationExists = PathExists(destinationPath);
            bool sameDirectory = string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(sourcePath) ?? string.Empty),
                NavigationService.NormalizeDirectoryForCompare(destinationDirectory),
                StringComparison.OrdinalIgnoreCase);
            if (sameDirectory)
            {
                string originalPath = destinationPath;
                if (!renameSameDirectoryToAll)
                {
                    string suggestedPath = FileOperationService.GetUniquePath(destinationPath);
                    var decision = _fileOperationDialogCoordinator.ConfirmPasteSameDirectory(
                        this,
                        fileName,
                        Path.GetFileName(suggestedPath),
                        sources.Count > 1);
                    if (decision == PasteSameDirectoryConfirmAction.Cancel)
                    {
                        actions = Array.Empty<CopyFinalAction>();
                        return false;
                    }
                    if (decision == PasteSameDirectoryConfirmAction.No)
                    {
                        result.Add(new CopyFinalAction(sourcePath, destinationPath, false, true));
                        continue;
                    }
                    renameSameDirectoryToAll = decision == PasteSameDirectoryConfirmAction.All;
                }
                destinationPath = FileOperationService.GetUniquePath(destinationPath);
                destinationExists = false;
                _ = originalPath;
            }
            if (destinationExists)
            {
                bool destinationIsDirectory = FileOperationService.IsDirectoryPath(destinationPath);
                if (sourceIsDirectory && destinationIsDirectory)
                {
                    if (!TryResolveCopyDirectoryMerge(sourcePath, destinationPath, ref directoryDecision, out bool skipMerge, out bool cancelMerge))
                    {
                        if (cancelMerge)
                        {
                            actions = Array.Empty<CopyFinalAction>();
                            return false;
                        }
                        if (skipMerge)
                        {
                            result.Add(new CopyFinalAction(sourcePath, destinationPath, true, true));
                            continue;
                        }
                    }
                    result.Add(new CopyFinalAction(sourcePath, destinationPath, true, false));
                    continue;
                }
                if (!TryResolveCopyCollision(sourcePath, ref destinationPath, ref fileDecision, out _, out bool skip, out bool cancel))
                {
                    if (cancel)
                    {
                        actions = Array.Empty<CopyFinalAction>();
                        return false;
                    }
                    if (skip)
                    {
                        result.Add(new CopyFinalAction(sourcePath, destinationPath, false, true));
                        continue;
                    }
                }
            }
            result.Add(new CopyFinalAction(sourcePath, destinationPath, false, false));
        }
        actions = result;
        return true;
    }

    private bool TryBuildPasteFinalPlan(
        IReadOnlyList<string> sources,
        string destinationDirectory,
        bool isCut,
        out IReadOnlyList<PasteFinalAction> actions,
        out int renamedCount,
        out string? firstRenamedName,
        out bool canRecordMoveUndoBatch,
        out bool canRecordCreatedFilesUndoBatch)
    {
        var result = new List<PasteFinalAction>();
        CopyCollisionDecision? fileDecision = null;
        DirectoryMergeDecision? directoryDecision = null;
        bool renameSameDirectoryToAll = false;
        renamedCount = 0;
        firstRenamedName = null;
        canRecordMoveUndoBatch = isCut;
        canRecordCreatedFilesUndoBatch = !isCut;
        foreach (string sourcePath in sources)
        {
            string fileName = Path.GetFileName(sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, fileName);
            bool sameDirectory = string.Equals(
                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(sourcePath) ?? string.Empty),
                NavigationService.NormalizeDirectoryForCompare(destinationDirectory),
                StringComparison.OrdinalIgnoreCase);
            if (sameDirectory)
            {
                if (!isCut)
                {
                    if (!renameSameDirectoryToAll)
                    {
                        string suggestedPath = FileOperationService.GetUniquePath(destinationPath);
                        var sameDirDecision = _fileOperationDialogCoordinator.ConfirmPasteSameDirectory(
                            this,
                            fileName,
                            Path.GetFileName(suggestedPath),
                            sources.Count > 1);
                        if (sameDirDecision == PasteSameDirectoryConfirmAction.Cancel)
                        {
                            actions = Array.Empty<PasteFinalAction>();
                            return false;
                        }
                        if (sameDirDecision == PasteSameDirectoryConfirmAction.No)
                        {
                            result.Add(new PasteFinalAction(sourcePath, destinationPath, false, true, false, false, null));
                            continue;
                        }
                        renameSameDirectoryToAll = sameDirDecision == PasteSameDirectoryConfirmAction.All;
                    }
                    destinationPath = FileOperationService.GetUniquePath(destinationPath);
                    renamedCount++;
                    firstRenamedName ??= Path.GetFileName(destinationPath);
                }
                else
                {
                    result.Add(new PasteFinalAction(sourcePath, destinationPath, false, true, false, false, null));
                    canRecordMoveUndoBatch = false;
                    continue;
                }
            }
            bool sourceIsDir = FileOperationService.IsDirectoryContainerPath(sourcePath);
            if (!isCut && sourceIsDir)
            {
                canRecordCreatedFilesUndoBatch = false;
            }
            bool destExists = PathExists(destinationPath);
            if (destExists)
            {
                bool destIsDir = FileOperationService.IsDirectoryContainerPath(destinationPath);
                if (sourceIsDir != destIsDir)
                {
                    _fileOperationDialogCoordinator.ShowTypeMismatchConflict(this, destinationPath);
                    result.Add(new PasteFinalAction(sourcePath, destinationPath, false, true, false, false, null));
                    canRecordMoveUndoBatch = false;
                    canRecordCreatedFilesUndoBatch = false;
                    continue;
                }
                if (sourceIsDir)
                {
                    canRecordMoveUndoBatch = false;
                    canRecordCreatedFilesUndoBatch = false;
                    if (!TryResolvePasteDirectoryMerge(sourcePath, destinationPath, isCut, ref directoryDecision, out bool shouldSkip, out bool shouldCancel))
                    {
                        if (shouldCancel)
                        {
                            actions = Array.Empty<PasteFinalAction>();
                            return false;
                        }
                        if (shouldSkip)
                        {
                            result.Add(new PasteFinalAction(sourcePath, destinationPath, true, true, false, false, null));
                            continue;
                        }
                    }
                    result.Add(new PasteFinalAction(sourcePath, destinationPath, true, false, false, false, null));
                    continue;
                }
                var collisionResolution = _fileOperationDialogCoordinator.ResolvePasteCollision(
                    this,
                    sourcePath,
                    destinationPath,
                    allowRename: !isCut,
                    isCut: isCut,
                    ref fileDecision);
                if (collisionResolution.ShouldCancel)
                {
                    actions = Array.Empty<PasteFinalAction>();
                    return false;
                }
                if (collisionResolution.ShouldSkip)
                {
                    result.Add(new PasteFinalAction(sourcePath, destinationPath, false, true, false, false, null));
                    canRecordMoveUndoBatch = false;
                    canRecordCreatedFilesUndoBatch = false;
                    continue;
                }
                destinationPath = collisionResolution.DestinationPath;
                bool overwriteMove = collisionResolution.OverwriteExisting;
                if (overwriteMove)
                {
                    canRecordMoveUndoBatch = false;
                    canRecordCreatedFilesUndoBatch = false;
                }
                if (collisionResolution.UsedRenameCopy)
                {
                    renamedCount++;
                    firstRenamedName ??= collisionResolution.RenameTargetName ?? Path.GetFileName(destinationPath);
                }
                result.Add(new PasteFinalAction(
                    sourcePath,
                    destinationPath,
                    false,
                    false,
                    overwriteMove,
                    collisionResolution.UsedRenameCopy,
                    collisionResolution.RenameTargetName));
                continue;
            }
            result.Add(new PasteFinalAction(sourcePath, destinationPath, false, false, false, false, null));
        }
        actions = result;
        return true;
    }

    private bool TryBuildMoveFinalPlan(
        IReadOnlyList<string> sources,
        string destinationDirectory,
        out IReadOnlyList<MoveFinalAction> actions,
        out bool canRecordUndoBatch)
    {
        var result = new List<MoveFinalAction>();
        CopyCollisionDecision? fileDecision = null;
        DirectoryMergeDecision? directoryDecision = null;
        canRecordUndoBatch = true;
        foreach (string sourcePath in sources)
        {
            string destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            bool sourceIsDir = Directory.Exists(sourcePath);
            bool destIsDir = Directory.Exists(destinationPath);
            bool destExists = File.Exists(destinationPath) || Directory.Exists(destinationPath);
            CopyCollisionPolicy appliedPolicy = CopyCollisionPolicy.Skip;
            if (destExists)
            {
                if (sourceIsDir && destIsDir)
                {
                    canRecordUndoBatch = false;
                    if (!TryResolveMoveDirectoryMerge(sourcePath, destinationPath, ref directoryDecision, out bool shouldSkip, out bool shouldCancel))
                    {
                        if (shouldCancel)
                        {
                            actions = Array.Empty<MoveFinalAction>();
                            return false;
                        }
                        if (shouldSkip)
                        {
                            result.Add(new MoveFinalAction(sourcePath, destinationPath, true, true, false));
                            continue;
                        }
                    }
                    result.Add(new MoveFinalAction(sourcePath, destinationPath, true, false, false));
                    continue;
                }
                if (!TryResolveCopyCollision(sourcePath, ref destinationPath, ref fileDecision, out appliedPolicy, out bool collisionShouldSkip, out bool collisionShouldCancel))
                {
                    if (collisionShouldCancel)
                    {
                        actions = Array.Empty<MoveFinalAction>();
                        return false;
                    }
                    if (collisionShouldSkip)
                    {
                        result.Add(new MoveFinalAction(sourcePath, destinationPath, false, true, false));
                        canRecordUndoBatch = false;
                        continue;
                    }
                }
            }
            bool overwrite = appliedPolicy == CopyCollisionPolicy.Overwrite;
            if (overwrite)
            {
                canRecordUndoBatch = false;
            }
            result.Add(new MoveFinalAction(sourcePath, destinationPath, false, false, overwrite));
        }
        actions = result;
        return true;
    }

    private async Task ExecuteMove(SelectionResult? selectionSnapshot = null)
    {
        if (GuardMutationBusy("移動")) return;
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            _fileOpUiState.ActiveOperationName,
            _fileOpUiState.Cts != null,
            "移動",
            ResolveSelection(selectionSnapshot),
            "移動対象がありません。",
            busyOperationName: "Move",
            isCancelRequested: _fileOpUiState.Cts?.IsCancellationRequested ?? false);
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        var selection = entryPlan.Selection;
        if (!TryResolveMultiMarkSelectionAction("移動", "移動をキャンセルしました。", selection, out selection))
        {
            return;
        }
        string selectionSummary = BuildSelectionSummaryText(selection);
        string? outsideWarning = BuildSelectionOutsideCurrentDirectoryWarning(selection);
        if (!_fileOperationDialogCoordinator.TrySelectDestinationDirectory(
                this,
                _navigationService,
                "移動先ディレクトリを入力してください:",
                "Move",
                "移動",
                "移動はキャンセルされました。",
                ShowStatusMessage,
                selectionSummary,
                outsideWarning,
                GetSharedDirectoryMoveHistory(),
                out string normalizedDestDir,
                out bool moveNeedsCreateDirectory))
        {
            return;
        }
        if (!_fileOperationDialogCoordinator.EnsureDestinationDirectory(this, normalizedDestDir, moveNeedsCreateDirectory))
        {
            return;
        }
        if (!TryBuildMoveFinalPlan(selection.FullPaths, normalizedDestDir, out IReadOnlyList<MoveFinalAction> finalMovePlan, out bool plannedCanRecordUndoBatch))
        {
            ShowStatusMessage("移動はキャンセルされました。");
            return;
        }
        IReadOnlyList<LinkOperationRoot> moveLinkRoots = finalMovePlan
            .Where(action => !action.Skip)
            .Select(action => new LinkOperationRoot(action.SourcePath, action.DestinationPath))
            .ToList();
        IReadOnlyList<LinkOperationRoot> helperMoveLinkRoots = LinkOperationPreparationService.BuildCrossVolumeMoveRoots(moveLinkRoots);
        LinkPreparation moveLinkPreparation = await PreparePasteLinksAsync(
            helperMoveLinkRoots,
            allowHelper: helperMoveLinkRoots.Count > 0,
            CancellationToken.None);
        if (moveLinkPreparation.Canceled)
        {
            ShowStatusMessage("リンク処理のキャンセルにより移動を中止しました。");
            return;
        }
        // 操作後に一気に一番上まで戻るのを防ぐため、あらかじめ次にフォーカスすべき対象を見つけておく
        string? nextTargetName = GetNextFocusTarget(selection.FullPaths.ToList());
        int successCount = 0;
        int totalCount = selection.FullPaths.Count;
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;
        int aggregateSkipCount = 0;
        int aggregateFailCount = 0;
        int preMoveLinkSuccessCount = moveLinkPreparation.SuccessfulTopLevelSources.Count;
        int preMoveLinkSkipCount = moveLinkPreparation.SkipCount;
        int preMoveLinkFailCount = moveLinkPreparation.FailCount;
        bool shouldClearMarks = true;
        IReadOnlyList<FileOperationUndoRedoItem> moveUndoItems = Array.Empty<FileOperationUndoRedoItem>();
        string? moveResultMessage = null;
        // 非同期実行の準備
        if (GuardMutationBusy("移動")) return;
        var token = PrepareFileOperation(entryPlan.BusyOperationName);
        int moveStatusVersion = _fileOpUiState.StatusVersion;
        ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage("Move", totalCount, normalizedDestDir));
        StartFileOperationProgressIndicator("Move", totalCount);
        IProgress<FileOperationProgress> progress = _fileOperationDialogCoordinator.CreateOperationProgress(
            "Move",
            message => ShowFileOperationStatusIfCurrent(
                moveStatusVersion,
                (_fileOpUiState.Cts?.IsCancellationRequested ?? false)
                    ? FileOperationPresentationHelper.GetCancelRequestedMessage(_fileOpUiState.ActiveOperationName ?? "Move")
                    : message),
            p => UpdateFileOperationProgressIndicatorIfCurrent(moveStatusVersion, "Move", p.ProcessedCount, p.TotalCount));
        try
        {
            var result = await Task.Run(() =>
            {
                int currentSuccess = 0;
                FileOpExitStatus status = FileOpExitStatus.Success;
                int currentSkipCount = 0;
                int currentFailCount = 0;
                bool clearMarks = true;
                bool canRecordUndoBatch = plannedCanRecordUndoBatch;
                if (moveLinkPreparation.Plan.Items.Count > 0)
                    canRecordUndoBatch = false;
                var successfulUndoMoves = new List<(string SourcePath, string DestinationPath)>();
                var unmarkPaths = new List<string>();
                var progressThrottleSw = Stopwatch.StartNew();
                var loopSw = Stopwatch.StartNew();
                bool suppressItemSuccessLogs = totalCount > 100;
                long fileMoveCallTotalMs = 0;
                long fileMoveCallMaxMs = 0;
                long destinationCheckTotalMs = 0;
                long progressReportTotalMs = 0;
                int progressReportCount = 0;
                int collisionCheckCount = 0;
                int collisionDialogCount = 0;
                foreach (MoveFinalAction action in finalMovePlan)
                {
                    if (token.IsCancellationRequested)
                    {
                        status = FileOpExitStatus.Canceled;
                        break;
                    }
                    string sourcePath = action.SourcePath;
                    string fileName = Path.GetFileName(sourcePath);
                    string destPath = action.DestinationPath;
                    if (moveLinkPreparation.ExcludedSources.Contains(sourcePath))
                    {
                        if (moveLinkPreparation.SuccessfulTopLevelSources.Contains(sourcePath))
                        {
                            FileOperationService.Delete(sourcePath);
                        }
                        else
                        {
                            clearMarks = false;
                        }
                        continue;
                    }
                    if (action.Skip)
                    {
                        currentSkipCount++;
                        clearMarks = false;
                        canRecordUndoBatch = false;
                        continue;
                    }
                    int processedCount = currentSuccess + currentSkipCount + currentFailCount;
                    bool shouldReportProgress =
                        processedCount == 0 ||
                        processedCount % MoveProgressReportChunkSize == 0 ||
                        progressThrottleSw.ElapsedMilliseconds >= MoveProgressReportThrottleMilliseconds;
                    if (shouldReportProgress)
                    {
                        var progressReportSw = Stopwatch.StartNew();
                        progress.Report(new FileOperationProgress(processedCount + 1, totalCount, fileName));
                        progressReportSw.Stop();
                        progressReportTotalMs += progressReportSw.ElapsedMilliseconds;
                        progressReportCount++;
                        progressThrottleSw.Restart();
                    }
                    if (action.Merge)
                    {
                        canRecordUndoBatch = false;
                        var directoryMoveSw = Stopwatch.StartNew();
                        CopyCollisionDecision? mergeFileDecision = null;
                        DirectMoveDirectoryIntoExisting(
                            sourcePath,
                            destPath,
                            ref mergeFileDecision,
                            out bool directoryShouldCancel,
                            out int directorySkipCount,
                            out int directoryFailCount,
                            moveLinkPreparation.ExcludedSources,
                            moveLinkPreparation.SuccessfulSources);
                        directoryMoveSw.Stop();
                        fileMoveCallTotalMs += directoryMoveSw.ElapsedMilliseconds;
                        if (directoryMoveSw.ElapsedMilliseconds > fileMoveCallMaxMs)
                        {
                            fileMoveCallMaxMs = directoryMoveSw.ElapsedMilliseconds;
                        }
                        currentSkipCount += directorySkipCount;
                        currentFailCount += directoryFailCount;
                        if (directoryShouldCancel)
                        {
                            status = FileOpExitStatus.Canceled;
                            clearMarks = false;
                            canRecordUndoBatch = false;
                            break;
                        }
                        currentSuccess++;
                        bool sourceStillExists = Directory.Exists(sourcePath) || File.Exists(sourcePath);
                        if (!sourceStillExists)
                        {
                            unmarkPaths.Add(sourcePath);
                        }
                        else
                        {
                            clearMarks = false;
                        }
                        if (directorySkipCount > 0 || directoryFailCount > 0)
                        {
                            clearMarks = false;
                            canRecordUndoBatch = false;
                        }
                        continue;
                    }
                    try
                    {
                        bool overwrite = action.Overwrite;
                        if (overwrite)
                        {
                            canRecordUndoBatch = false;
                        }
                        var moveCallSw = Stopwatch.StartNew();
                        if (!overwrite &&
                            FileOperationService.IsDirectoryContainerPath(sourcePath) &&
                            !FileOperationService.HaveSameStorageRoot(sourcePath, destPath) &&
                            Directory.Exists(destPath))
                        {
                            CopyCollisionDecision? mergeFileDecision = null;
                            DirectMoveDirectoryIntoExisting(
                                sourcePath,
                                destPath,
                                ref mergeFileDecision,
                                out bool directoryShouldCancel,
                                out int directorySkipCount,
                                out int directoryFailCount,
                                moveLinkPreparation.ExcludedSources,
                                moveLinkPreparation.SuccessfulSources);
                            if (directoryShouldCancel)
                            {
                                status = FileOpExitStatus.Canceled;
                                clearMarks = false;
                                canRecordUndoBatch = false;
                                break;
                            }
                            currentSkipCount += directorySkipCount;
                            currentFailCount += directoryFailCount;
                            if (directorySkipCount > 0 || directoryFailCount > 0)
                            {
                                clearMarks = false;
                                canRecordUndoBatch = false;
                            }
                        }
                        else
                        {
                            FileOperationService.Move(sourcePath, destPath, overwrite, suppressLogging: suppressItemSuccessLogs,
                                excludedReparsePaths: moveLinkPreparation.ExcludedSources);
                        }
                        moveCallSw.Stop();
                        fileMoveCallTotalMs += moveCallSw.ElapsedMilliseconds;
                        if (moveCallSw.ElapsedMilliseconds > fileMoveCallMaxMs)
                        {
                            fileMoveCallMaxMs = moveCallSw.ElapsedMilliseconds;
                        }
                        currentSuccess++;
                        if (canRecordUndoBatch)
                        {
                            successfulUndoMoves.Add((sourcePath, destPath));
                        }
                        unmarkPaths.Add(sourcePath);
                    }
                    catch (Exception ex)
                    {
                        this.Invoke(() => _fileOperationDialogCoordinator.ShowOperationError(this, "移動", fileName, ex.Message));
                        currentFailCount++;
                        clearMarks = false;
                        status = FileOpExitStatus.Error;
                        canRecordUndoBatch = false;
                        break;
                    }
                }
                loopSw.Stop();
                progress.Report(new FileOperationProgress(Math.Min(totalCount, currentSuccess + currentSkipCount + currentFailCount), totalCount, "完了"));
                progressReportCount++;
                var undoCreateSw = Stopwatch.StartNew();
                IReadOnlyList<FileOperationUndoRedoItem> currentMoveUndoItems =
                    canRecordUndoBatch &&
                    status == FileOpExitStatus.Success &&
                    currentSuccess == totalCount &&
                    currentSkipCount == 0 &&
                    currentFailCount == 0
                        ? FileOperationUndoRedoService.CreateMoveBatch(successfulUndoMoves)
                        : Array.Empty<FileOperationUndoRedoItem>();
                undoCreateSw.Stop();
                return (
                    currentSuccess,
                    status,
                    currentSkipCount,
                    currentFailCount,
                    clearMarks,
                    currentMoveUndoItems,
                    (IReadOnlyList<string>)unmarkPaths,
                    loopSw.ElapsedMilliseconds,
                    fileMoveCallTotalMs,
                    fileMoveCallMaxMs,
                    destinationCheckTotalMs,
                    progressReportTotalMs,
                    progressReportCount,
                    collisionCheckCount,
                    collisionDialogCount,
                    undoCreateSw.ElapsedMilliseconds);
            }, token);
            successCount = result.currentSuccess + preMoveLinkSuccessCount;
            exitStatus = FileOperationPresentationHelper.NormalizeExitStatus(result.status, successCount, selection.Count,
                result.currentSkipCount + preMoveLinkSkipCount, result.currentFailCount + preMoveLinkFailCount);
            aggregateSkipCount = result.currentSkipCount + preMoveLinkSkipCount;
            aggregateFailCount = result.currentFailCount + preMoveLinkFailCount;
            shouldClearMarks = result.clearMarks;
            if (moveLinkPreparation.PartialTopLevelSources.Count > 0)
                shouldClearMarks = false;
            moveUndoItems = result.currentMoveUndoItems;
            long unmarkApplyMs = 0;
            if (result.Item7.Count > 0)
            {
                var unmarkApplySw = Stopwatch.StartNew();
                UnmarkPathsInBulk(result.Item7, "MoveFinalApply");
                unmarkApplySw.Stop();
                unmarkApplyMs = unmarkApplySw.ElapsedMilliseconds;
            }
            if (moveUndoItems.Count > 0)
            {
                _fileOperationUndoRedoService.RecordBatch(FileOperationUndoRedoOperation.Move, moveUndoItems);
                moveResultMessage = BuildMoveUndoReadyMessage(successCount, selection.Count);
            }
            LogService.Info(
                $"[MoveHotpath] Summary total={selection.Count} success={result.currentSuccess} skip={result.currentSkipCount} fail={result.currentFailCount} " +
                $"canceled={result.status == FileOpExitStatus.Canceled} loopMs={result.Item8} fileMoveCallMsTotal={result.Item9} fileMoveCallMsMax={result.Item10} " +
                $"destinationCheckMs={result.Item11} progressReportMs={result.Item12} progressReportCount={result.Item13} " +
                $"unmarkCollectCount={result.Item7.Count} unmarkApplyMs={unmarkApplyMs} collisionCheckCount={result.Item14} " +
                $"collisionDialogCount={result.Item15} undoCreateMs={result.Item16}");

            if (exitStatus == FileOpExitStatus.Success && successCount > 0)
            {
                AddDirectoryMoveHistory(normalizedDestDir);
            }
        }
        catch (OperationCanceledException)
        {
            exitStatus = FileOpExitStatus.Canceled;
            shouldClearMarks = false;
        }
        catch (Exception ex)
        {
            exitStatus = FileOpExitStatus.Error;
            shouldClearMarks = false;
            LogService.Error("ExecuteMove async error", ex);
            _fileOperationDialogCoordinator.ShowUnexpectedOperationError(this, "移動", ex);
        }
        finally
        {
            HandlePostOperation(_fileOperationPostOperationCoordinator.CreateMoveResult(exitStatus, successCount, selection.Count, nextTargetName, normalizedDestDir,
                shouldClearMarks: shouldClearMarks, customMessage: moveResultMessage, skipCount: aggregateSkipCount, failCount: aggregateFailCount));
        }
    }
    private void ShowArchiveProgressFallback(string operationName, int totalCount)
    {
        CloseArchiveProgressFallback();
        var form = Presentation.FileOperationFallbackUiPresenter.ShowProgressFallback(
            this,
            operationName,
            totalCount,
            () => RequestActiveFileOperationCancel($"{operationName}ProgressFallback"),
            canCancel: true,
            closedCallback: closedForm =>
            {
                if (ReferenceEquals(_archiveProgressFallback, closedForm))
                {
                    _archiveProgressFallback = null;
                }
                ScheduleBrowserFocusReturnAfterFileOperation("ArchiveProgressFallbackClosed");
            });
        _archiveProgressFallback = form;
        form.UpdateState($"{operationName}中", "準備中...", indeterminate: true, _fileOpUiState.Cts?.IsCancellationRequested ?? false);
    }
    private void UpdateArchiveProgressFallbackState(string operationName, string detail, bool indeterminate = true)
    {
        _archiveProgressFallback?.UpdateState(
            $"{operationName}中",
            detail,
            indeterminate,
            _fileOpUiState.Cts?.IsCancellationRequested ?? false);
    }
    private void CompleteArchiveProgressFallback(string message)
    {
        Presentation.FileOperationFallbackUiPresenter.CompleteProgressFallback(_archiveProgressFallback, message);
    }
    private void CloseArchiveProgressFallback()
    {
        Presentation.FileOperationFallbackUiPresenter.CloseProgressFallback(ref _archiveProgressFallback);
    }
    private string BuildPackSelectionSummary(SelectionResult selection)
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
        if (string.IsNullOrEmpty(dirName)) dirName = "archive";
        return dirName + ".zip";
    }
    private static bool CanPackEachFolderIndividually(SelectionResult selection)
    {
        return selection.Count >= 1;
    }
    private async Task ExecutePackEachIndividuallyDirectAsync()
    {
        if (GuardReadOnlyBrowserTab("圧縮")) return;
        var selection = ResolveSelection();
        if (selection.Count == 0)
        {
            ShowStatusMessage("圧縮(Pack)対象がありません。");
            return;
        }
        if (!CanPackEachFolderIndividually(selection))
        {
            return;
        }

        string? exePath = SevenZipService.ResolveExecutable(_settings.SevenZip?.ExePath);
        bool hasSevenZip = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
        bool useFallback = !hasSevenZip;
        bool useTarFallback = useFallback && TarFallbackService.IsAvailable();
        bool useZipFallback = useFallback && !useTarFallback;

        string outputDir = _navigationService.CurrentPath;
        string extension = ".zip"; // ユーザー観測に合わせ原則zip

        // 混在時確認
        var allTargets = selection.FullPaths.ToList();
        var folderTargets = allTargets.Where(Directory.Exists).ToList();
        var fileTargets = allTargets.Where(File.Exists).ToList();
        var targetsToProcess = allTargets;
        if (folderTargets.Any() && fileTargets.Any())
        {
            DialogResult dr = DialogResult.None;
            this.Invoke(() =>
            {
                dr = MessageBox.Show(
                    this,
                    "フォルダとファイルが混在しています。\nファイルも個別に圧縮しますか？\n\n「はい」：ファイルも個別に圧縮します\n「いいえ」：フォルダのみを個別に圧縮します（ファイルは除外）",
                    "個別圧縮の確認",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
            });
            if (dr == DialogResult.Cancel) return;
            if (dr == DialogResult.No)
            {
                targetsToProcess = folderTargets;
            }
        }

        // 衝突チェック
        bool anyCollision = false;
        foreach (var target in targetsToProcess)
        {
            string itemArchivePath = Path.Combine(outputDir, Path.GetFileName(target) + extension);
            if (File.Exists(itemArchivePath))
            {
                anyCollision = true;
                break;
            }
        }
        PackExistingArchiveAction individualCollisionAction = PackExistingArchiveAction.Add;
        if (anyCollision)
        {
            this.Invoke(() =>
            {
                individualCollisionAction = ShowPackExistingArchiveActionDialog(this, "個別圧縮先のアーカイブ");
            });
            if (individualCollisionAction == PackExistingArchiveAction.Cancel)
            {
                ShowStatusMessage("圧縮はキャンセルされました。");
                return;
            }
        }

        if (GuardMutationBusy("圧縮")) return;

        var token = PrepareFileOperation("圧縮");
        ShowArchiveProgressFallback("圧縮", targetsToProcess.Count);
        ShowStatusMessage($"{targetsToProcess.Count} 件の項目を個別圧縮中...");
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;

        try
        {
            exitStatus = await Task.Run(() =>
            {
                int successCount = 0;
                for (int i = 0; i < targetsToProcess.Count; i++)
                {
                    if (token.IsCancellationRequested) return FileOpExitStatus.Canceled;
                    string sourcePath = targetsToProcess[i];
                    string itemName = Path.GetFileName(sourcePath);
                    if (string.IsNullOrEmpty(itemName)) itemName = "item_" + i;
                    string itemArchivePath = Path.Combine(outputDir, itemName + extension);

                    // 上書き選択時は既存ファイルを削除 (個別圧縮では単純削除で対応)
                    if (individualCollisionAction == PackExistingArchiveAction.Overwrite && File.Exists(itemArchivePath))
                    {
                        try { File.Delete(itemArchivePath); }
                        catch (Exception ex) { LogService.Error($"Failed to delete existing archive for overwrite: {itemArchivePath}", ex); }
                    }

                    this.Invoke(() =>
                    {
                        ShowStatusMessage($"個別圧縮中 ({i + 1}/{targetsToProcess.Count}): {itemName}");
                        UpdateArchiveProgressFallbackState("圧縮", $"個別圧縮中 ({i + 1}/{targetsToProcess.Count}): {itemName}");
                    });

                    if (useFallback && !useTarFallback)
                    {
                        ZipFallbackService.Pack(itemArchivePath, new[] { sourcePath });
                        successCount++;
                        continue;
                    }

                    if (useTarFallback)
                    {
                        string? baseDir = Path.GetDirectoryName(sourcePath);
                        string relPath = Path.GetFileName(sourcePath);
                        if (string.IsNullOrEmpty(baseDir)) baseDir = _navigationService.CurrentPath;
                        var tarRes = TarFallbackService.Pack(itemArchivePath, baseDir, new[] { relPath }, token, line =>
                        {
                            ShowStatusMessage($"個別圧縮中 ({i + 1}/{targetsToProcess.Count}): {itemName}");
                            BeginInvoke(new Action(() => UpdateArchiveProgressFallbackState("圧縮", $"個別圧縮中 ({i + 1}/{targetsToProcess.Count}): {itemName}")));
                        });
                        if (tarRes.ExitCode == 0)
                        {
                            successCount++;
                        }
                        else if (!token.IsCancellationRequested)
                        {
                            this.Invoke(() => MessageBox.Show($"{itemName} の圧縮中にエラーが発生しました。\nExitCode: {tarRes.ExitCode}\n\n[エラー出力]\n{tarRes.Error}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                        }
                        continue;
                    }

                    // 7-Zipでの圧縮
                    var sources = new List<string>();
                    if (Directory.Exists(sourcePath))
                    {
                        sources.Add(Path.Combine(sourcePath, "*"));
                    }
                    else
                    {
                        sources.Add(sourcePath);
                    }

                    var itemRequest = new PackRequest
                    {
                        OutputArchivePath = itemArchivePath,
                        Format = PackArchiveFormat.Zip,
                        CompressionLevel = PackCompressionLevel.Normal,
                        SplitSize = "",
                        PackEachFolderIndividually = true
                    };

                    var res = SevenZipService.Pack(exePath!, sources, itemRequest, token, line =>
                    {
                        if (TryExtractSevenZipProgress(line, out string percent))
                        {
                            ShowStatusMessage($"個別圧縮中 ({i + 1}/{targetsToProcess.Count} {percent}%): {itemName}");
                            BeginInvoke(new Action(() =>
                                UpdateArchiveProgressFallbackState("圧縮", $"個別圧縮中 ({i + 1}/{targetsToProcess.Count} {percent}%): {itemName}")));
                        }
                    });
                    if (res.ExitCode == 0)
                    {
                        successCount++;
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        this.Invoke(() => MessageBox.Show($"{itemName} の圧縮中にエラーが発生しました。\nExitCode: {res.ExitCode}\n\n[エラー出力]\n{res.Error}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                    }
                }
                return successCount == targetsToProcess.Count ? FileOpExitStatus.Success : (successCount > 0 ? FileOpExitStatus.Success : FileOpExitStatus.Error);
            }, token);
        }
        catch (OperationCanceledException)
        {
            exitStatus = FileOpExitStatus.Canceled;
        }
        catch (Exception ex)
        {
            exitStatus = FileOpExitStatus.Error;
            LogService.Error("ExecutePackEachIndividuallyDirectAsync async error", ex);
            MessageBox.Show($"圧縮中に予期せぬエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CompleteArchiveProgressFallback(exitStatus == FileOpExitStatus.Success ? "圧縮完了" : exitStatus == FileOpExitStatus.Canceled ? "圧縮中断" : "圧縮失敗");
            FinalizeFileOperation();
            CloseArchiveProgressFallback();
            LoadDirectory(_navigationService.CurrentPath);
            if (exitStatus == FileOpExitStatus.Success)
            {
                ClearMarks();
                ShowStatusMessage("個別圧縮が完了しました。");
            }
            else if (exitStatus == FileOpExitStatus.Canceled)
            {
                ShowStatusMessage("圧縮は中断されました。");
            }
            else
            {
                ShowStatusMessage("圧縮失敗");
            }
        }
    }
    private async Task ExecutePack(bool forcePackEachFolderIndividually = false, SelectionResult? selectionSnapshot = null)
    {
        if (GuardReadOnlyBrowserTab("圧縮")) return;
        var selection = ResolveSelection(selectionSnapshot);
        if (selection.Count == 0)
        {
            ShowStatusMessage("圧縮(Pack)対象がありません。");
            return;
        }
        string defaultName = BuildPackDefaultArchiveName(selection, _navigationService.CurrentPath);
        string? exePath = SevenZipService.ResolveExecutable(_settings.SevenZip?.ExePath);
        string nativeArchivePath = Path.Combine(_navigationService.CurrentPath, defaultName);
        string? cliPath = SevenZipService.ResolveCliExecutable(_settings.SevenZip?.ExePath);
        string? guiPath = SevenZipService.ResolveGuiExecutable(cliPath ?? exePath ?? string.Empty);
        PackDialogRouteDecision route = PackDialogRoutingService.Resolve(
            _settings.SevenZip?.PackDialogMode ?? PackDialogMode.Auto,
            guiPath,
            selection.FullPaths.ToList(),
            nativeArchivePath);
        if (route.Route == PackDialogRoute.Error)
        {
            MessageBox.Show(this, route.ErrorMessage, "圧縮Dialog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowStatusMessage("7-Zip標準Dialogを起動できませんでした。");
            return;
        }
        if (route.IsNative)
        {
            await LaunchNativePackDialogAsync(guiPath!, nativeArchivePath, selection.FullPaths.ToList());
            return;
        }
        string selectionSummary = BuildPackSelectionSummary(selection);
        bool canPackEachFolder = CanPackEachFolderIndividually(selection);
        bool canUseSingleFileOnlyFormats = selection.Count == 1 && !string.IsNullOrWhiteSpace(selection.FirstPath) && File.Exists(selection.FirstPath!);
        bool hasSevenZip = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
        IReadOnlyList<PackArchiveFormat> availableFormats;
        string hintText;
        if (hasSevenZip)
        {
            var formats = new List<PackArchiveFormat>
            {
                PackArchiveFormat.Zip,
                PackArchiveFormat.SevenZip,
                PackArchiveFormat.Tar,
                PackArchiveFormat.Wim
            };
            if (canUseSingleFileOnlyFormats)
            {
                formats.Add(PackArchiveFormat.Xz);
                formats.Add(PackArchiveFormat.GZip);
                formats.Add(PackArchiveFormat.BZip2);
            }
            availableFormats = formats;
            hintText = canUseSingleFileOnlyFormats
                ? "zip / 7z / tar / wim / xz / gzip / bzip2 を扱います。個別圧縮は複数対象のとき有効です。"
                : "zip / 7z / tar / wim を扱います。個別圧縮は複数対象のとき有効です。";
        }
        else if (TarFallbackService.IsAvailable())
        {
            availableFormats = new[]
            {
                PackArchiveFormat.Zip,
                PackArchiveFormat.SevenZip,
                PackArchiveFormat.Tar
            };
            hintText = "7-Zip 不在のため、7z / tar は Windows 標準機能 (tar.exe) で代行します。";
        }
        else
        {
            availableFormats = new[] { PackArchiveFormat.Zip };
            hintText = "7-Zip が見つからないため zip 形式のみ選択可能です。";
        }
        PackExistingArchiveAction collisionAction = PackExistingArchiveAction.Add;
        PackRequest? request = PackDialog.Show(
            this,
            _navigationService.CurrentPath,
            defaultName,
            selectionSummary,
            canPackEachFolder,
            forcePackEachFolderIndividually,
            availableFormats,
            hintText,
            (owner, archivePath) =>
            {
                collisionAction = ShowPackExistingArchiveActionDialog(owner, archivePath);
                return collisionAction;
            });
        if (request == null)
        {
            ShowStatusMessage("圧縮はキャンセルされました。");
            return;
        }
        if ((request.Format == PackArchiveFormat.GZip || request.Format == PackArchiveFormat.BZip2 || request.Format == PackArchiveFormat.Xz) && !canUseSingleFileOnlyFormats)
        {
            MessageBox.Show(
                this,
                "gzip / bzip2 / xz は単一ファイルの圧縮のみ対応です。複数項目またはフォルダを圧縮する場合は zip / 7z / tar / wim を選択してください。",
                "Pack",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            ShowStatusMessage("単一ファイル向け形式のため圧縮を中止しました。");
            return;
        }
        bool useFallback = !hasSevenZip;
        bool useTarFallback = useFallback && (request.Format == PackArchiveFormat.SevenZip || request.Format == PackArchiveFormat.Tar);
        bool useZipFallback = useFallback && request.Format == PackArchiveFormat.Zip;
        if (useFallback && !useTarFallback && !useZipFallback)
        {
            // Basically unreachable due to UI filtering, but as a safety guard:
            string message = BuildMissingSevenZipMessage("archive を圧縮");
            ShowStatusMessage(message);
            MessageBox.Show(message, "7-Zip が必要です", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (useZipFallback)
        {
            ShowStatusMessage("7-Zip が見つからないため、Windows 標準 zip 圧縮で実行します。");
        }
        else if (useTarFallback)
        {
            ShowStatusMessage("7-Zip が見つからないため、Windows 標準機能 (tar.exe) で圧縮します。");
        }
        string archivePath = request.OutputArchivePath;
        IReadOnlyList<string> markSnapshot = CaptureCurrentMarkedPathSnapshot();
        IReadOnlySet<string> visibleBefore = CaptureVisibleBrowserPathSet();
        PackOverwriteBackupSession? overwriteBackup = null;
        string? overwriteCleanupErrorMessage = null;
        if (GuardMutationBusy("圧縮")) return;
        if (collisionAction == PackExistingArchiveAction.Overwrite)
        {
            try
            {
                overwriteBackup = PreparePackOverwriteBackup(archivePath);
            }
            catch (Exception ex)
            {
                LogService.Error("Pack overwrite backup prepare error", ex);
                MessageBox.Show(
                    $"既存 archive の上書き準備に失敗しました:\n{ex.Message}",
                    "Pack",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                ShowStatusMessage("既存 archive の上書き準備に失敗しました。");
                return;
            }
        }
        // 非同期実行の準備
        var token = PrepareFileOperation("圧縮");
        ShowArchiveProgressFallback("圧縮", selection.Count);
        string archiveName = Path.GetFileName(archivePath);
        ShowStatusMessage($"{selection.Count} 件の項目を圧縮中...");
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;
        try
        {
            exitStatus = await Task.Run(() =>
            {
                if (request.PackEachFolderIndividually)
                {
                    if (useZipFallback)
                    {
                        var allTargetsForZipFallback = selection.FullPaths.ToList();
                        string outputDirForZipFallback = Path.GetDirectoryName(archivePath) ?? _navigationService.CurrentPath;
                        string extensionForZipFallback = Path.GetExtension(archivePath);
                        int zipFallbackSuccessCount = 0;
                        for (int i = 0; i < allTargetsForZipFallback.Count; i++)
                        {
                            if (token.IsCancellationRequested)
                            {
                                return FileOpExitStatus.Canceled;
                            }
                            string sourcePath = allTargetsForZipFallback[i];
                            string itemName = Path.GetFileName(sourcePath);
                            if (string.IsNullOrWhiteSpace(itemName))
                            {
                                itemName = $"item_{i}";
                            }
                            string itemArchivePath = Path.Combine(outputDirForZipFallback, itemName + extensionForZipFallback);
                            this.Invoke(() =>
                            {
                                ShowStatusMessage($"個別圧縮中 ({i + 1}/{allTargetsForZipFallback.Count}): {itemName}");
                                UpdateArchiveProgressFallbackState("圧縮", $"個別圧縮中 ({i + 1}/{allTargetsForZipFallback.Count}): {itemName}");
                            });
                            ZipFallbackService.Pack(itemArchivePath, new[] { sourcePath });
                            zipFallbackSuccessCount++;
                        }
                        return zipFallbackSuccessCount == allTargetsForZipFallback.Count
                            ? FileOpExitStatus.Success
                            : FileOpExitStatus.Error;
                    }
                    // 個別圧縮モード: 各項目をループ処理
                    var allTargets = selection.FullPaths.ToList();
                    var folderTargets = allTargets.Where(Directory.Exists).ToList();
                    var fileTargets = allTargets.Where(File.Exists).ToList();
                    var targetsToProcess = allTargets;
                    if (folderTargets.Any() && fileTargets.Any())
                    {
                        DialogResult dr = DialogResult.None;
                        this.Invoke(() =>
                        {
                            dr = MessageBox.Show(
                                "フォルダとファイルが混在しています。\nファイルも個別に圧縮しますか？\n\n「はい」：ファイルも個別に圧縮します\n「いいえ」：フォルダのみを個別に圧縮します（ファイルは除外）",
                                "個別圧縮の確認",
                                MessageBoxButtons.YesNoCancel,
                                MessageBoxIcon.Question);
                        });
                        if (dr == DialogResult.Cancel) return FileOpExitStatus.Canceled;
                        if (dr == DialogResult.No)
                        {
                            targetsToProcess = folderTargets;
                        }
                    }
                    // 個別圧縮時の既存ファイル衝突チェック
                    string outputDir = Path.GetDirectoryName(archivePath) ?? _navigationService.CurrentPath;
                    string extension = Path.GetExtension(archivePath);
                    bool anyCollision = false;
                    foreach (var target in targetsToProcess)
                    {
                        string itemArchivePath = Path.Combine(outputDir, Path.GetFileName(target) + extension);
                        if (File.Exists(itemArchivePath))
                        {
                            anyCollision = true;
                            break;
                        }
                    }
                    PackExistingArchiveAction individualCollisionAction = PackExistingArchiveAction.Add;
                    if (anyCollision)
                    {
                        this.Invoke(() =>
                        {
                            // 代表として最初の衝突ファイルを例示してダイアログを表示
                            individualCollisionAction = ShowPackExistingArchiveActionDialog(this, "個別圧縮先のアーカイブ");
                        });
                        if (individualCollisionAction == PackExistingArchiveAction.Cancel)
                        {
                            return FileOpExitStatus.Canceled;
                        }
                    }
                    int successCount = 0;
                    for (int i = 0; i < targetsToProcess.Count; i++)
                    {
                        if (token.IsCancellationRequested) return FileOpExitStatus.Canceled;
                        string sourcePath = targetsToProcess[i];
                        string itemName = Path.GetFileName(sourcePath);
                        if (string.IsNullOrEmpty(itemName)) itemName = "item_" + i;
                        string itemArchivePath = Path.Combine(outputDir, itemName + extension);
                        // 上書き選択時は既存ファイルを削除 (個別圧縮では単純削除で対応)
                        if (individualCollisionAction == PackExistingArchiveAction.Overwrite && File.Exists(itemArchivePath))
                        {
                            try { File.Delete(itemArchivePath); }
                            catch (Exception ex) { LogService.Error($"Failed to delete existing archive for overwrite: {itemArchivePath}", ex); }
                        }
                        this.Invoke(() =>
                        {
                            ShowStatusMessage($"個別圧縮中 ({i + 1}/{targetsToProcess.Count}): {itemName}");
                            UpdateArchiveProgressFallbackState("圧縮", $"個別圧縮中 ({i + 1}/{targetsToProcess.Count}): {itemName}");
                        });
                        // フォルダの場合は中身のみを対象にする (* を付加)
                        var sources = new List<string>();
                        if (Directory.Exists(sourcePath))
                        {
                            sources.Add(Path.Combine(sourcePath, "*"));
                        }
                        else
                        {
                            sources.Add(sourcePath);
                        }
                        var itemRequest = new PackRequest
                        {
                            OutputArchivePath = itemArchivePath,
                            Format = request.Format,
                            CompressionLevel = request.CompressionLevel,
                            SplitSize = request.SplitSize,
                            PackEachFolderIndividually = true
                        };
                        if (useTarFallback)
                        {
                            // TarFallbackService.Pack(outputPath, baseDirectory, relativePaths, ...)
                            // 個別圧縮時は sourcePath の親を baseDirectory とし、ファイル名のみを相対パスとするのが安全
                            string? baseDir = Path.GetDirectoryName(sourcePath);
                            string relPath = Path.GetFileName(sourcePath);
                            if (string.IsNullOrEmpty(baseDir)) baseDir = _navigationService.CurrentPath;
                            var tarRes = TarFallbackService.Pack(itemArchivePath, baseDir, new[] { relPath }, token, line =>
                            {
                                ShowStatusMessage($"個別圧縮中 ({i + 1}/{targetsToProcess.Count}): {itemName}");
                                BeginInvoke(new Action(() => UpdateArchiveProgressFallbackState("圧縮", $"個別圧縮中 ({i + 1}/{targetsToProcess.Count}): {itemName}")));
                            });
                            if (tarRes.ExitCode == 0)
                            {
                                successCount++;
                            }
                            else if (!token.IsCancellationRequested)
                            {
                                this.Invoke(() => MessageBox.Show($"{itemName} の圧縮中にエラーが発生しました。\nExitCode: {tarRes.ExitCode}\n\n[エラー出力]\n{tarRes.Error}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                            }
                            continue;
                        }
                        var res = SevenZipService.Pack(exePath!, sources, itemRequest, token, line =>
                        {
                            if (TryExtractSevenZipProgress(line, out string percent))
                            {
                                ShowStatusMessage($"個別圧縮中 ({i + 1}/{targetsToProcess.Count} {percent}%): {itemName}");
                                BeginInvoke(new Action(() =>
                                    UpdateArchiveProgressFallbackState("圧縮", $"個別圧縮中 ({i + 1}/{targetsToProcess.Count} {percent}%): {itemName}")));
                            }
                        });
                        if (res.ExitCode == 0)
                        {
                            successCount++;
                        }
                        else if (!token.IsCancellationRequested)
                        {
                            this.Invoke(() => MessageBox.Show($"{itemName} の圧縮中にエラーが発生しました。\nExitCode: {res.ExitCode}\n\n[エラー出力]\n{res.Error}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                        }
                    }
                    return successCount == targetsToProcess.Count ? FileOpExitStatus.Success : (successCount > 0 ? FileOpExitStatus.Success : FileOpExitStatus.Error);
                }
                else
                {
                    // 通常モード: 一括圧縮
                    if (useZipFallback)
                    {
                        ZipFallbackService.Pack(request.OutputArchivePath, selection.FullPaths.ToList());
                        return FileOpExitStatus.Success;
                    }
                    if (useTarFallback)
                    {
                        // 一括圧縮時は現在のパスを baseDirectory とし、選択項目のファイル名のみを相対パスとする
                        string baseDir = _navigationService.CurrentPath;
                        var relPaths = selection.FullPaths.Select(p =>
                        {
                            try { return Path.GetRelativePath(baseDir, p); }
                            catch { return p; } // Fallback to full path if relative path calculation fails
                        }).ToList();
                        var tarRes = TarFallbackService.Pack(request.OutputArchivePath, baseDir, relPaths, token, line =>
                        {
                            ShowStatusMessage($"圧縮中: {archiveName}...");
                            BeginInvoke(new Action(() => UpdateArchiveProgressFallbackState("圧縮", $"圧縮中: {archiveName}...")));
                        });
                        if (tarRes.ExitCode == 0) return FileOpExitStatus.Success;
                        if (token.IsCancellationRequested) return FileOpExitStatus.Canceled;
                        this.Invoke(() => MessageBox.Show($"Windows 標準機能 (tar.exe) での圧縮に失敗しました。\nExitCode: {tarRes.ExitCode}\n\n[エラー出力]\n{tarRes.Error}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                        return FileOpExitStatus.Error;
                    }
                    var res = SevenZipService.Pack(exePath!, selection.FullPaths.ToList(), request, token, line =>
                    {
                        if (TryExtractSevenZipProgress(line, out string percent))
                        {
                            ShowStatusMessage($"圧縮中 ({percent}%): {archiveName}");
                            BeginInvoke(new Action(() => UpdateArchiveProgressFallbackState("圧縮", $"圧縮中 ({percent}%): {archiveName}")));
                        }
                    });
                    if (res.ExitCode == 0) return FileOpExitStatus.Success;
                    if (token.IsCancellationRequested) return FileOpExitStatus.Canceled;
                    this.Invoke(() => MessageBox.Show($"圧縮中にエラーが発生しました。\nExitCode: {res.ExitCode}\n\n[エラー出力]\n{res.Error}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                    return FileOpExitStatus.Error;
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            exitStatus = FileOpExitStatus.Canceled;
        }
        catch (Exception ex)
        {
            exitStatus = FileOpExitStatus.Error;
            LogService.Error("ExecutePack async error", ex);
            MessageBox.Show($"圧縮中に予期せぬエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CompleteArchiveProgressFallback(exitStatus == FileOpExitStatus.Success ? "圧縮完了" : exitStatus == FileOpExitStatus.Canceled ? "圧縮中断" : "圧縮失敗");
            if (overwriteBackup != null)
            {
                try
                {
                    if (exitStatus == FileOpExitStatus.Success)
                    {
                        overwriteBackup.Discard();
                    }
                    else
                    {
                        overwriteBackup.Restore();
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error("Pack overwrite backup finalize error", ex);
                    overwriteCleanupErrorMessage = ex.Message;
                    if (exitStatus == FileOpExitStatus.Success)
                    {
                        MessageBox.Show(
                            $"圧縮は完了しましたが、退避した旧 archive の削除に失敗しました:\n{ex.Message}",
                            "Pack",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                    else
                    {
                        MessageBox.Show(
                            $"圧縮失敗後の旧 archive 復元に失敗しました:\n{ex.Message}",
                            "Pack",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
            FinalizeFileOperation();
            CloseArchiveProgressFallback();
            if (request.PackEachFolderIndividually)
            {
                LoadDirectory(_navigationService.CurrentPath);
            }
            else
            {
                LoadDirectory(_navigationService.CurrentPath, Path.GetFileName(archivePath));
            }
            RestoreMarksAfterOperation(markSnapshot, visibleBefore, exitStatus);
            if (exitStatus == FileOpExitStatus.Success)
            {
                string successMessage = request.PackEachFolderIndividually
                    ? "個別圧縮が完了しました。"
                    : $"圧縮完了: {archiveName}";
                ShowStatusMessage(successMessage);
            }
            else if (exitStatus == FileOpExitStatus.Canceled)
            {
                ShowStatusMessage(string.IsNullOrWhiteSpace(overwriteCleanupErrorMessage)
                    ? "圧縮は中断されました。"
                    : "圧縮は中断されました（旧 archive の復元に失敗）。");
            }
            else
            {
                ShowStatusMessage(string.IsNullOrWhiteSpace(overwriteCleanupErrorMessage)
                    ? "圧縮失敗"
                    : "圧縮失敗（旧 archive の復元に失敗）");
            }
        }
    }

    private async Task LaunchNativePackDialogAsync(string guiPath, string archivePath, IReadOnlyList<string> sourcePaths)
    {
        string currentDirectory = _navigationService.CurrentPath;
        try
        {
            using Process? process = SevenZipService.StartNativePackDialog(guiPath, archivePath, sourcePaths);
            if (process == null)
            {
                throw new InvalidOperationException("7zG.exe のプロセスを開始できませんでした。");
            }

            ShowStatusMessage("7-Zip標準の圧縮Dialogを表示しました。");
            await process.WaitForExitAsync();
            if (string.Equals(_navigationService.CurrentPath, currentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                LoadDirectory(currentDirectory);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Native 7-Zip pack dialog launch error", ex);
            MessageBox.Show(this, $"7-Zip標準の圧縮Dialogを起動できませんでした。\n{ex.Message}", "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowStatusMessage("7-Zip標準Dialogの起動に失敗しました。");
        }
    }
    private PackExistingArchiveAction ShowPackExistingArchiveActionDialog(IWin32Window owner, string archivePath)
    {
        string archiveName = Path.GetFileName(archivePath);
        using var dialog = new Form
        {
            Text = "Pack",
            Width = 560,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font
        };
        var messageLabel = new Label
        {
            Left = 16,
            Top = 16,
            Width = 512,
            Height = 88,
            Text = $"同名の archive がすでに存在します。\n\n{archiveName}\n{archivePath}\n\n追加・上書き・キャンセル から選んでください。"
        };
        var addButton = new Button
        {
            Left = 196,
            Top = 120,
            Width = 92,
            Height = 30,
            Text = "追加(&A)",
            UseMnemonic = true,
            TabIndex = 0
        };
        var overwriteButton = new Button
        {
            Left = 294,
            Top = 120,
            Width = 92,
            Height = 30,
            Text = "上書き(&O)",
            UseMnemonic = true,
            TabIndex = 1
        };
        var cancelButton = new Button
        {
            Left = 392,
            Top = 120,
            Width = 104,
            Height = 30,
            Text = "キャンセル(&C)",
            UseMnemonic = true,
            DialogResult = DialogResult.Cancel,
            TabIndex = 2
        };
        PackExistingArchiveAction result = PackExistingArchiveAction.Cancel;
        addButton.Click += (_, _) =>
        {
            result = PackExistingArchiveAction.Add;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        overwriteButton.Click += (_, _) =>
        {
            result = PackExistingArchiveAction.Overwrite;
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };
        dialog.Controls.Add(messageLabel);
        dialog.Controls.Add(addButton);
        dialog.Controls.Add(overwriteButton);
        dialog.Controls.Add(cancelButton);
        messageLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(messageLabel, messageLabel.Width, 88);
        FileOperationDialogLayoutHelper.EnsureBottomButtonRow(
            dialog,
            new[] { addButton, overwriteButton, cancelButton },
            messageLabel.Bottom,
            buttonGap: 6,
            contentGap: 14);
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) => BeginInvoke(new Action(() => cancelButton.Focus()));
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? result
            : PackExistingArchiveAction.Cancel;
    }
    private PackOverwriteBackupSession PreparePackOverwriteBackup(string archivePath)
    {
        return PackOverwriteBackupSession.Create(SevenZipService.GetPackOutputArtifacts(archivePath));
    }
    private async Task ExecuteHashAsync(SevenZipHashAlgorithm algorithm, SelectionResult? selectionSnapshot = null)
    {
        var selection = ResolveSelection(selectionSnapshot);
        if (selection.Count == 0) return;
        if (selection.FullPaths.Any(Directory.Exists))
        {
            string msg = "ディレクトリのハッシュ計算には対応していません。ファイルのみを選択してください。";
            ShowStatusMessage(msg);
            MessageBox.Show(this, msg, "CRC/SHA 計算", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!TryResolveSevenZipPath("CRC/SHA 計算", out string exePath))
        {
            return;
        }
        string targetSummary = selection.Count == 1
            ? Path.GetFileName(selection.FirstPath!)
            : $"{Path.GetFileName(selection.FirstPath!)} ほか {selection.Count - 1} 件";
        CancellationToken token = PrepareFileOperation("CRC/SHA 計算");
        try
        {
            ShowStatusMessage($"CRC/SHA 計算中: {targetSummary}...");
            var result = await SevenZipService.HashAsync(exePath, selection.FullPaths.ToList(), algorithm, token);
            if (token.IsCancellationRequested)
            {
                ShowStatusMessage("CRC/SHA 計算は中断されました。");
                return;
            }
            if (result.ExitCode == 0)
            {
                ShowStatusMessage("CRC/SHA 計算完了。");
                using var dialog = new Dialogs.HashResultDialog(targetSummary, algorithm, result.Output);
                dialog.ShowDialog(this);
            }
            else
            {
                string errorMsg = string.IsNullOrWhiteSpace(result.Error) ? "計算中にエラーが発生しました。" : result.Error;
                ShowStatusMessage($"CRC/SHA 計算失敗 (ExitCode: {result.ExitCode})");
                MessageBox.Show(this, errorMsg, "CRC/SHA 計算エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("CRC/SHA 計算中に例外が発生しました", ex);
            ShowStatusMessage($"CRC/SHA 計算失敗: {ex.Message}");
        }
        finally
        {
            FinalizeFileOperation();
        }
    }
    private async Task ExecuteUnpack(SelectionResult? selectionSnapshot = null)
    {
        if (GuardReadOnlyBrowserTab("解凍")) return;
        var selection = ResolveSelection(selectionSnapshot);
        // アーカイブファイルのみを抽出
        var archivePaths = selection.FullPaths
            .Where(path => File.Exists(path) && IsArchiveTarget(path))
            .ToList();
        if (archivePaths.Count == 0)
        {
            ShowStatusMessage("解凍(Unpack)可能なアーカイブファイルが選択されていません。");
            return;
        }
        string? exePath = SevenZipService.ResolveExecutable(_settings.SevenZip?.ExePath);
        bool canUseZipFallbackOnly = archivePaths.All(path =>
            string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase));
        bool canUseTarFallbackOnly = archivePaths.All(ArchiveFileTypeHelper.CanUseTarFallbackForUnpack);
        bool useZipFallback = false;
        bool useTarFallback = false;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            if (canUseZipFallbackOnly)
            {
                useZipFallback = true;
                ShowStatusMessage("7-Zip が見つからないため、Windows 標準 zip 解凍で実行します。");
            }
            else if (canUseTarFallbackOnly && TarFallbackService.IsAvailable())
            {
                useTarFallback = true;
                ShowStatusMessage("7-Zip が見つからないため、Windows 標準機能 (tar.exe) で解凍を実行します。");
            }
            else
            {
                string message = BuildMissingSevenZipMessage("archive を解凍");
                ShowStatusMessage(message);
                MessageBox.Show(message, "7-Zip が必要です", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        string archiveDisplayName = archivePaths.Count == 1
            ? Path.GetFileNameWithoutExtension(archivePaths[0])
            : "archive";
        ArchiveExtractDestinationOptions? destinationOptions = ArchiveExtractDestinationDialog.Show(
            this,
            _navigationService.CurrentPath,
            archiveDisplayName);
        if (destinationOptions == null)
        {
            ShowStatusMessage("解凍はキャンセルされました。");
            return;
        }
        string destDir = destinationOptions.BaseDirectory;
        IReadOnlyList<string> markSnapshot = CaptureCurrentMarkedPathSnapshot();
        IReadOnlySet<string> visibleBefore = CaptureVisibleBrowserPathSet();
        if (GuardMutationBusy("解凍")) return;
        // 非同期実行の準備
        var token = PrepareFileOperation("解凍");
        ShowArchiveProgressFallback("解凍", totalCount: archivePaths.Count);
        int successCount = 0;
        int totalCount = archivePaths.Count;
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;
        IProgress<FileOperationProgress> progress = new Progress<FileOperationProgress>(p =>
        {
            ShowStatusMessage($"解凍中 ({p.ProcessedCount}/{p.TotalCount}): {p.CurrentFileName}...");
            UpdateArchiveProgressFallbackState("解凍", $"解凍中 ({p.ProcessedCount}/{p.TotalCount}): {p.CurrentFileName}...");
        });
        try
        {
            var result = await Task.Run(() =>
            {
                int currentSuccess = 0;
                FileOpExitStatus status = FileOpExitStatus.Success;
                foreach (var archivePath in archivePaths)
                {
                    if (token.IsCancellationRequested)
                    {
                        status = FileOpExitStatus.Canceled;
                        break;
                    }
                    string fileName = Path.GetFileName(archivePath);
                    progress.Report(new FileOperationProgress(currentSuccess + 1, totalCount, fileName));
                    try
                    {
                        string actualDestDir = ArchiveExtractService.ResolveDestinationDirectory(
                            destDir,
                            archivePath,
                            destinationOptions.CreateArchiveRootDirectory);
                        if (useZipFallback)
                        {
                            ZipFallbackService.Unpack(archivePath, actualDestDir);
                            currentSuccess++;
                            this.Invoke(() => UnmarkPath(archivePath));
                            continue;
                        }
                        if (useTarFallback)
                        {
                            var tarRes = TarFallbackService.Unpack(archivePath, actualDestDir, null, token, line =>
                            {
                                ShowStatusMessage($"解凍中 ({currentSuccess + 1}/{totalCount}): {fileName}...");
                                BeginInvoke(new Action(() => UpdateArchiveProgressFallbackState("解凍", $"解凍中 ({currentSuccess + 1}/{totalCount}): {fileName}...")));
                            });
                            if (tarRes.ExitCode == 0)
                            {
                                currentSuccess++;
                                this.Invoke(() => UnmarkPath(archivePath));
                                continue;
                            }
                            else
                            {
                                if (token.IsCancellationRequested)
                                {
                                    status = FileOpExitStatus.Canceled;
                                    break;
                                }
                                string errorMsg = tarRes.Error ?? string.Empty;
                                string displayMsg = $"Windows 標準機能 (tar.exe) での解凍に失敗しました。\n";
                                if (errorMsg.Contains("展開先フォルダ"))
                                {
                                    displayMsg += $"{errorMsg}\n\nファイル: {fileName}\nExitCode: {tarRes.ExitCode}";
                                }
                                else
                                {
                                    displayMsg += $"暗号化や分割アーカイブの可能性があります。\n\nファイル: {fileName}\nExitCode: {tarRes.ExitCode}\n\n[エラー出力]\n{errorMsg}";
                                }
                                this.Invoke(() => MessageBox.Show(displayMsg, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                                status = FileOpExitStatus.Error;
                                continue;
                            }
                        }
                        var res = SevenZipService.Unpack(exePath!, archivePath, actualDestDir, token, line =>
                        {
                            if (TryExtractSevenZipProgress(line, out string percent))
                            {
                                ShowStatusMessage($"解凍中 ({currentSuccess + 1}/{totalCount}) [{percent}%]: {fileName}...");
                                BeginInvoke(new Action(() => UpdateArchiveProgressFallbackState("解凍", $"解凍中 ({currentSuccess + 1}/{totalCount}) [{percent}%]: {fileName}...")));
                            }
                        });
                        if (res.ExitCode == 0)
                        {
                            currentSuccess++;
                            this.Invoke(() => UnmarkPath(archivePath)); // 成功した分だけマークを外す
                        }
                        else
                        {
                            if (token.IsCancellationRequested)
                            {
                                status = FileOpExitStatus.Canceled;
                                break;
                            }
                            this.Invoke(() => MessageBox.Show($"解凍中にエラーが発生しました。\nファイル: {fileName}\nExitCode: {res.ExitCode}\n\n[エラー出力]\n{res.Error}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                            status = FileOpExitStatus.Error;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Error($"ExecuteUnpack iteration error: {fileName}", ex);
                        this.Invoke(() => MessageBox.Show($"解凍処理の実行に失敗しました:\n{fileName}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
                        status = FileOpExitStatus.Error;
                        break;
                    }
                }
                return (currentSuccess, status);
            }, token);
            successCount = result.currentSuccess;
            exitStatus = result.status;
        }
        catch (OperationCanceledException)
        {
            exitStatus = FileOpExitStatus.Canceled;
        }
        catch (Exception ex)
        {
            exitStatus = FileOpExitStatus.Error;
            LogService.Error("ExecuteUnpack async error", ex);
            MessageBox.Show($"解凍中に予期せぬエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            CompleteArchiveProgressFallback(exitStatus == FileOpExitStatus.Success ? "解凍完了" : exitStatus == FileOpExitStatus.Canceled ? "解凍中断" : "解凍失敗");
            FinalizeFileOperation();
            CloseArchiveProgressFallback();
            // 解凍先がカレントディレクトリ配下なら再読込、それ以外は読込のみ
            if (destDir.StartsWith(_navigationService.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                LoadDirectory(_navigationService.CurrentPath);
            }
            RestoreMarksAfterOperation(markSnapshot, visibleBefore, exitStatus);
            if (exitStatus == FileOpExitStatus.Success)
            {
                ShowStatusMessage($"解凍完了: {successCount} 件");
            }
            else if (exitStatus == FileOpExitStatus.Canceled)
            {
                ShowStatusMessage($"解凍は中断されました ({successCount}/{totalCount} 件完了)");
            }
            else
            {
                ShowStatusMessage($"解凍失敗または未完了 ({successCount}/{totalCount} 件完了)");
            }
        }
    }
    private sealed class PackOverwriteBackupSession
    {
        private readonly List<(string OriginalPath, string BackupPath)> _movedFiles;
        private PackOverwriteBackupSession(List<(string OriginalPath, string BackupPath)> movedFiles)
        {
            _movedFiles = movedFiles;
        }
        public static PackOverwriteBackupSession Create(IReadOnlyList<string> targetPaths)
        {
            var movedFiles = new List<(string OriginalPath, string BackupPath)>();
            try
            {
                foreach (string originalPath in targetPaths)
                {
                    if (!File.Exists(originalPath))
                    {
                        continue;
                    }
                    string backupPath = BuildBackupPath(originalPath);
                    File.Move(originalPath, backupPath);
                    movedFiles.Add((originalPath, backupPath));
                }
                return new PackOverwriteBackupSession(movedFiles);
            }
            catch
            {
                for (int i = movedFiles.Count - 1; i >= 0; i--)
                {
                    var moved = movedFiles[i];
                    if (File.Exists(moved.BackupPath) && !File.Exists(moved.OriginalPath))
                    {
                        File.Move(moved.BackupPath, moved.OriginalPath);
                    }
                }
                throw;
            }
        }
        public void Restore()
        {
            for (int i = _movedFiles.Count - 1; i >= 0; i--)
            {
                var moved = _movedFiles[i];
                if (File.Exists(moved.OriginalPath))
                {
                    File.Delete(moved.OriginalPath);
                }
                if (File.Exists(moved.BackupPath))
                {
                    File.Move(moved.BackupPath, moved.OriginalPath);
                }
            }
        }
        public void Discard()
        {
            foreach (var moved in _movedFiles)
            {
                if (File.Exists(moved.BackupPath))
                {
                    File.Delete(moved.BackupPath);
                }
            }
        }
        private static string BuildBackupPath(string originalPath)
        {
            string directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
            string fileName = Path.GetFileName(originalPath);
            string backupPath;
            do
            {
                backupPath = Path.Combine(directory, $"{fileName}.midfd-packbak-{Guid.NewGuid():N}");
            }
            while (File.Exists(backupPath));
            return backupPath;
        }
    }
    private async Task UpdateLargeFileVirtualDisplayAsync(
        int reqId,
        CancellationToken token,
        bool preserveCharacterSelection = false)
    {
        if (_largeFileState == null) return;
        var state = _largeFileState;
        try
        {
            var encoding = GetCurrentViewerEncoding();
            int requestedFirstLine = state.FirstVisibleLine;
            int maxLineReadBytes = int.MaxValue;
            if (state.IsIndexing && state.LineOffsets.Count <= 1)
            {
                maxLineReadBytes = LargeTextInitialLineReadBytes;
            }
            else if (state.IsLongLineDetected)
            {
                maxLineReadBytes = LargeTextLongLineVisibleReadBytes;
            }
            var lines = await Services.LargeFileLineReaderService.ReadLinesAsync(
                state,
                requestedFirstLine,
                _largeFileControl.VisibleLineCount,
                encoding,
                token,
                maxLineReadBytes);
            // 表示用に長大行を切り捨て判定 (データ本体は変えず、描画用の flags を作成)
            var truncatedFlags = new List<bool>();
            if (lines != null)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    bool isTruncated = false;
                    if (state.IsLongLineDetected)
                    {
                        int lineIdx = requestedFirstLine + i;
                        if (lineIdx >= 0 && lineIdx < state.LineOffsets.Count)
                        {
                            long startOffset = state.LineOffsets[lineIdx];
                            long nextOffset = (lineIdx + 1 < state.LineOffsets.Count) ? state.LineOffsets[lineIdx + 1] : state.TotalBytes;
                            if (nextOffset - startOffset > maxLineReadBytes)
                            {
                                isTruncated = true;
                            }
                        }
                    }
                    truncatedFlags.Add(isTruncated);
                }
            }
            if (_activePreviewRequestId == reqId && _currentPreviewTarget == state.FilePath && _uiMode == UIMode.Viewer)
            {
                LogViewerLayoutBounds("LargeText before SetVisibleLines");
                viewerMessageLabel.Visible = false;
                viewerPictureBox.Visible = false;
                viewerTextBox.Visible = false;

                _largeFileControl.SetVisibleLines(requestedFirstLine, lines!, truncatedFlags, preserveCharacterSelection);
                _largeFileControl.Visible = true;
                _largeFileControl.Focus();
                _largeFileControl.Update();
                ApplyViewerStatusLine("LargeText visible lines applied");
                LogViewerStatusRoute("LargeText visible lines post-update", GetViewerStatusLine());
                LogViewerLayoutBounds("LargeText after SetVisibleLines");
                BeginInvoke(new Action(() =>
                {
                    if (IsLargeTextStatusApplyTarget(state))
                    {
                        ApplyViewerStatusLine("LargeText deferred final apply");
                        LogViewerLayoutBounds("LargeText deferred final apply");
                    }
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_activePreviewRequestId == reqId && !token.IsCancellationRequested)
            {
                ClearPreview($"Read Error: {ex.Message}", reqId);
            }
        }
    }
    /// <summary>
    /// ラージファイルプレビューの表示位置を変更する。
    /// すべてのキー操作、ホイール、スクロールバー操作はこのメソッドを経由させる。
    /// </summary>
    private async Task NavigateLargeFilePreviewAsync(
        int targetFirstLine,
        string reason,
        bool preserveCharacterSelection = true,
        int characterSelectionAutoScrollDirection = 0)
    {
        if (_largeFileState == null) return;
        // 手動操作や他の移動が走った場合は、予約されていた End ジャンプを解除する
        _largeFileState.PendingEndAfterIndex = false;
        int max = _largeFileControl.GetMaxFirstVisibleLine();
        int line = Math.Clamp(targetFirstLine, 0, max);
        if (!preserveCharacterSelection && _largeFileControl.HasAnySelection && _largeFileState.FirstVisibleLine != line)
        {
            _largeFileControl.ClearSelections();
        }
        // 状態を更新
        _largeFileState.FirstVisibleLine = line;
        // コントロールのスクロールバー位置を同期 (イベント発火は抑止される)
        _largeFileControl.SetScrollValueSilently(line);
        int reqId = _previewRequestCoordinator.CurrentRequestId;
        Interlocked.Exchange(ref _activePreviewRequestId, reqId);
        // 内容を非同期で更新
        await UpdateLargeFileVirtualDisplayAsync(reqId, _previewRequestCoordinator.Token, preserveCharacterSelection);
        if (characterSelectionAutoScrollDirection != 0)
        {
            _largeFileControl.ExtendCharacterSelectionToVisibleEdge(characterSelectionAutoScrollDirection);
        }
    }
    private Encoding GetCurrentViewerEncoding()
    {
        if (_currentViewerKind == PreviewKind.LargeText
            && _largeFileState != null
            && _largeFileState.DetectedEncoding != null)
        {
            return _largeFileState.DetectedEncoding;
        }
        if (_viewerEncodingOverride == ViewerEncoding.UTF8) return Encoding.UTF8;
        if (_viewerEncodingOverride == ViewerEncoding.SJIS)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("shift_jis");
        }
        return Encoding.UTF8; // デフォルト
    }
    private bool IsLargeTextStatusApplyTarget(Models.LargeFilePreviewState state)
    {
        return _uiMode == UIMode.Viewer
            && _currentViewerKind == PreviewKind.LargeText
            && ReferenceEquals(_largeFileState, state)
            && string.Equals(_currentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase);
    }
    private void ExecuteTreeDialog()
    {
        if (GuardClipboardBusy()) return;
        if (_uiMode != UIMode.Browser) return;
        string? selectedPath = TreeDialog.Show(_navigationService.CurrentPath);
        if (!string.IsNullOrEmpty(selectedPath))
        {
            ExecuteDirectoryNavigationRequest(
                _browserNavigationCoordinator.CreateDirectoryNavigationRequest(selectedPath),
                onDirectoryMissing: path => MessageBox.Show($"指定されたパスが見つかりません: {path}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
    }
    private bool IsNavigationOrModifierKey(Keys key)
    {
        switch (key)
        {
            case Keys.Up:
            case Keys.Down:
            case Keys.Left:
            case Keys.Right:
            case Keys.PageUp:
            case Keys.PageDown:
            case Keys.Home:
            case Keys.End:
            case Keys.ShiftKey:
            case Keys.ControlKey:
            case Keys.Menu: // Menu = Alt
            case Keys.LShiftKey:
            case Keys.RShiftKey:
            case Keys.LControlKey:
            case Keys.RControlKey:
            case Keys.LMenu:
            case Keys.RMenu:
            case Keys.Capital:
            case Keys.Scroll:
            case Keys.NumLock:
                return true;
            default:
                return false;
        }
    }
    private void ShowStatusMessage(string message)
    {
        ShowStatusMessage(message, 0);
    }
    private void ShowStatusMessage(string message, int holdMs)
    {
        ShowStatusMessage(message, holdMs, StatusMessageKindClassifier.Classify(message));
    }
    private void ShowStatusMessage(string message, int holdMs, StatusKind kind)
    {
        if (holdMs > 0)
        {
            _statusNoticeHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(holdMs);
        }
        if (_notificationService == null)
        {
            // 初期化前のフォールバック (起動時の読込失敗時等)
            this.statusLabel.Text = message;
            return;
        }
        _notificationService.Show(message, kind);
        if (_currentViewerKind == PreviewKind.LargeText)
        {
            LogViewerStatusRoute("ShowStatusMessage", GetViewerStatusLine());
        }
        // Phase: move viewer status to external - internal label no longer used
    }
    private void FileListView_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_isApplyingDirectoryList || _suppressBrowserSelectionChanged)
        {
            return;
        }
        ApplyBrowserSelectionChanged();
    }

    private void ApplyBrowserSelectionChanged(bool scheduleInfoUpdate = true)
    {
        // マウス操作時の同期: 選択変更を内部状態 (_browserCursorIndex) に書き戻す
        if (fileListView.SelectedIndices.Count > 0)
        {
            _browserCursorIndex = _browserPageStartIndex + fileListView.SelectedIndices[0];
        }
        // Info/Name 行を debounce 更新 (カーソル移動に伴う補助表示のみ遅延)
        if (scheduleInfoUpdate)
        {
            ScheduleUpdateInfoPanelDebounced();
        }
        // プレビューエンコーディングを Auto にリセット
        _viewerEncodingOverride = ViewerEncoding.Auto;
        var currentItem = GetCurrentBrowserItem();
        string? currentPath = currentItem?.Tag as string;
        bool selectionPathChanged = _browserSelectionIdentityGate.TryAccept(currentPath, _directoryContentGeneration);
        PreviewKind currentSelectionKind = GetBrowserSelectionPreviewKind(currentItem, currentPath);
        bool isImageSelection = currentSelectionKind == PreviewKind.Image;
        var viewer = GetReusableImageViewer();
        var selectionId = Interlocked.Increment(ref _selectionIdCounter);
        var selStartTime = Stopwatch.GetTimestamp();
        string diagPathKind = GetBrowserSelectionPathKind(currentPath);
        string diagPathRoot = GetBrowserSelectionPathRoot(currentPath);
        string diagExtension = currentPath != null ? Path.GetExtension(currentPath) : string.Empty;
        LogService.Info(
            $"[Browser.SelectionChanged.Start] selectionId={selectionId}" +
            $" pathKind={diagPathKind} pathRoot={diagPathRoot} extension={diagExtension}" +
            $" previewKind={currentSelectionKind} isImageSelection={isImageSelection} viewerAvailable={viewer != null}");
        if (selectionPathChanged && isImageSelection && viewer != null)
        {
            var loadStartTime = Stopwatch.GetTimestamp();
            LogService.Info($"[Browser.SelectionChanged.ImageViewerLoad.Start] selectionId={selectionId}");
            viewer.LoadMedia(currentPath!, currentSelectionKind, showErrorMessage: false);
            var ensureStartTime = Stopwatch.GetTimestamp();
            viewer.EnsureVisibleAndActivated();
            var loadEndTime = Stopwatch.GetTimestamp();
            long loadMediaElapsedMs = (ensureStartTime - loadStartTime) * 1000 / Stopwatch.Frequency;
            long ensureVisibleElapsedMs = (loadEndTime - ensureStartTime) * 1000 / Stopwatch.Frequency;
            LogService.Info(
                $"[Browser.SelectionChanged.ImageViewerLoad.End] selectionId={selectionId}" +
                $" loadMediaElapsedMs={loadMediaElapsedMs} ensureVisibleElapsedMs={ensureVisibleElapsedMs}" +
                $" pathKind={diagPathKind} pathRoot={diagPathRoot} extension={diagExtension}");
        }
        else if (selectionPathChanged && isImageSelection)
        {
            LogService.Info($"[Browser.SelectionChanged.ImageViewerLoad.Skip] selectionId={selectionId} reason=NoViewer");
        }
        else if (selectionPathChanged && !isImageSelection && (_settings.Preview?.CloseImageViewerOnNonImageSelection ?? false))
        {
            CloseImageViewers();
        }
        long selElapsedMs = (Stopwatch.GetTimestamp() - selStartTime) * 1000 / Stopwatch.Frequency;
        LogService.Info($"[Browser.SelectionChanged.End] selectionId={selectionId} elapsedMs={selElapsedMs}");
        UpdateMenuStripState();
        // Browser自動preview対象のみ事前クリアし、対象外は不要な再描画を避ける
        if (selectionPathChanged && IsBrowserAutoPreviewEligible(currentSelectionKind))
        {
            ResetBrowserAutoPreviewSuppressedState();
            ClearPreview();
        }
        if (selectionPathChanged)
        {
            RequestPreviewRefresh();
        }

        if (functionBarPanel.Visible)
        {
            functionBarPanel.Invalidate();
        }
    }
    /// <summary>
    /// 表示クリア専用メソッド。
    /// キャンセル制御（CancellationToken）には触れず、プレビューポップアップの表示状態のみを更新する。
    /// </summary>
    private void ClearPreview(string message = "No Preview", int reqId = -1)
    {
#if DEBUG
        Debug.WriteLine($"[ClearPreview] Message: '{message}', ReqId: {reqId}");
#endif
        // Image Popup をクリア
        if (_previewPopup.Visible)
        {
            _previewPopup.ShowMessage(message);
        }
        // Viewer パネルをクリア
        if (viewerPanel != null)
        {
            _currentViewerDetectedEncodingLabel = string.Empty;
            viewerTextBox.Clear();
            viewerTextBox.Visible = false;
            if (_largeFileControl != null)
            {
                _largeFileControl.ClearActiveSearchHit();
                _largeFileControl.Visible = false;
            }
            viewerPictureBox.Image?.Dispose();
            viewerPictureBox.Image = null;
            viewerPictureBox.Visible = false;
            viewerMessageLabel.Text = message;
            viewerMessageLabel.Visible = true;
        }
    }
    private string? GetCurrentPreviewSelectionPath()
    {
        var item = GetCurrentBrowserItem();
        if (item == null || item.Text == "..") return null;
        return item.Tag as string;
    }
    private PreviewKind GetBrowserSelectionPreviewKind(ListViewItem? item, string? fullPath)
    {
        bool isDirectory = item == null || item.Text == ".." || !IsBrowserFileItem(item);
        var rawKind = PreviewService.GetPreviewKindShallow(fullPath ?? string.Empty, isDirectory);
        return GetEffectivePreviewKind(fullPath ?? string.Empty, rawKind);
    }
    /// <summary>パス種別を UNC/DriveLetter/Unknown に分類する。フルパスは返さない。</summary>
    private static string GetBrowserSelectionPathKind(string? path)
    {
        return NetworkPathResolutionPolicy.GetPathKind(path);
    }

    /// <summary>パスのルート部分を診断ログ用に丸めて返す。フルパスは返さない。</summary>
    private static string GetBrowserSelectionPathRoot(string? path)
    {
        return NetworkPathResolutionPolicy.GetPathRoot(path);
    }

    private static bool IsBrowserAutoPreviewEligible(PreviewKind kind)
    {
        return kind == PreviewKind.Image;
    }
    private string? ResolveViewerClickedUrl(string? linkText)
    {
        if (_currentViewerKind == PreviewKind.Markdown)
        {
            return MarkdownPreviewService.ResolveClickedUrl(linkText);
        }

        return linkText;
    }
    private static bool IsPlainTextBoxViewerKind(PreviewKind kind)
    {
        return kind == PreviewKind.Text
            || kind == PreviewKind.Markdown
            || kind == PreviewKind.Sqlite
            || kind == PreviewKind.Binary;
    }
    private static string GetBrowserAutoPreviewSuppressedMessage(PreviewKind kind)
    {
        return kind switch
        {
            PreviewKind.Text => "自動プレビューなし\nV / Enter で開きます。",
            PreviewKind.Markdown => "自動プレビューなし\nV / Enter で開きます。",
            PreviewKind.Sqlite => "自動プレビューなし\nV / Enter で開きます。",
            PreviewKind.Binary => "自動プレビューなし\nV / Enter で開きます。",
            PreviewKind.Video => "動画は自動プレビュー対象外です。",
            _ => "プレビュー対象外"
        };
    }
    private void ResetBrowserAutoPreviewSuppressedState()
    {
        _isBrowserAutoPreviewSuppressed = false;
        _lastBrowserAutoPreviewSuppressedMessage = null;
    }
    private void ShowBrowserAutoPreviewSuppressedMessage(string requestPath, PreviewKind kind)
    {
        string message = GetBrowserAutoPreviewSuppressedMessage(kind);
        if (_isBrowserAutoPreviewSuppressed
            && viewerMessageLabel.Visible
            && string.Equals(_lastBrowserAutoPreviewSuppressedMessage, message, StringComparison.Ordinal)
            && string.Equals(viewerMessageLabel.Text, message, StringComparison.Ordinal))
        {
            _currentPreviewTarget = requestPath;
            return;
        }
        ClearPreview(message);
        _currentPreviewTarget = requestPath;
        _isBrowserAutoPreviewSuppressed = true;
        _lastBrowserAutoPreviewSuppressedMessage = message;
    }
    private bool IsCurrentPreviewSelection(string requestPath)
    {
        string? currentPath = GetCurrentPreviewSelectionPath();
        return string.Equals(currentPath, requestPath, StringComparison.OrdinalIgnoreCase);
    }
    private bool IsLatestPreviewRequest(int reqId, string requestPath, CancellationToken token)
    {
        return !token.IsCancellationRequested
            && _activePreviewRequestId == reqId
            && string.Equals(_lastPreviewRequestedPath, requestPath, StringComparison.OrdinalIgnoreCase)
            && IsCurrentPreviewSelection(requestPath);
    }
    private async Task UpdatePreviewAsync(int reqId, string requestPath, CancellationToken token)
    {
        var entrySw = Stopwatch.StartNew();
        string result = "Completed";
        string stage = "Start";
        PreviewKind resolvedKind = PreviewKind.None;
        Exception? failedException = null;
        LogLargeTextEntryTiming(
            "UpdatePreviewAsync start",
            entrySw,
            requestPath,
            reqId,
            PreviewKind.None,
            currentPath: GetCurrentPreviewSelectionPath());
        try
        {
            if (_uiMode == UIMode.Viewer)
            {
                await Task.Yield();
            }
            else
            {
                // 少し待つ(高速スクロール時の過剰な処理を防ぐ)
                await Task.Delay(150, token);
            }
            string? currentPath = GetCurrentPreviewSelectionPath();
            LogLargeTextEntryTiming(
                "after debounce / yield",
                entrySw,
                requestPath,
                reqId,
                PreviewKind.None,
                currentPath: currentPath);
            await _previewDiagnosticDelayService.DelayAsync(
                "Preview",
                requestPath,
                _previewDiagnosticDelayService.PreviewDelayMs,
                token);
            if (!IsLatestPreviewRequest(reqId, requestPath, token))
            {
                result = "Superseded";
                stage = "Debounce";
                LogService.Info(
                    $"[PreviewRequest] skippedReason=Superseded reqId={reqId} " +
                    $"requestPath='{requestPath}' currentPath='{currentPath}' activeReqId={_activePreviewRequestId}");
                return;
            }
            _largeFileState = null;
            string fullPath = requestPath;
            if (Directory.Exists(fullPath))
            {
                result = "SkippedDirectory";
                stage = "DirectoryGuard";
                ClearPreview("プレビュー対象外", reqId);
                return;
            }
            // 【チラつき抑制】 前回と同じターゲットなら、表示クリアをスキップして即表示更新へ向かう
            if (_currentPreviewTarget != fullPath)
            {
                ClearPreview("", reqId);
                _currentPreviewTarget = fullPath;
            }
            await _previewDiagnosticDelayService.DelayAsync(
                "PreviewKind",
                fullPath,
                _previewDiagnosticDelayService.PreviewKindDelayMs,
                token);
            TextPreviewProbeResult? previewProbe;
            (PreviewKind rawKind, previewProbe) = await Task.Run(
                () =>
                {
                    PreviewKind detectedKind = PreviewService.GetPreviewKind(fullPath, out TextPreviewProbeResult? detectedProbe);
                    return (detectedKind, detectedProbe);
                },
                token);
            var kind = GetEffectivePreviewKind(fullPath, rawKind);
            resolvedKind = kind;
            stage = "KindResolved";
            LogLargeTextEntryTiming(
                "after GetPreviewKind",
                entrySw,
                fullPath,
                reqId,
                kind,
                currentPath: GetCurrentPreviewSelectionPath());
            if (!IsLatestPreviewRequest(reqId, fullPath, token))
            {
                result = "Superseded";
                stage = "AfterKind";
                LogService.Info(
                    $"[PreviewRequest] skippedReason=SupersededAfterKind reqId={reqId} " +
                    $"requestPath='{fullPath}' activeReqId={_activePreviewRequestId}");
                return;
            }
            if (kind == PreviewKind.None)
            {
                result = "SkippedUnsupported";
                stage = "KindNone";
                string ext = Path.GetExtension(fullPath);
                ClearPreview($"プレビュー対象外\n{ext}", reqId);
                return;
            }
            ClearPreview("プレビュー読み込み中...", reqId);
#if DEBUG
            Debug.WriteLine($"[ReqId: {reqId}] Loading: {fullPath}");
#endif
            if (kind == PreviewKind.Image)
            {
                await _previewDiagnosticDelayService.DelayAsync(
                    "PreviewOpen:Image",
                    fullPath,
                    _previewDiagnosticDelayService.PreviewOpenDelayMs,
                    token);
                _currentViewerKind = PreviewKind.Image;
                ApplyViewerChromeState();
                if (_previewPopup.Visible)
                {
                    _previewPopup.Hide();
                }
                viewerMessageLabel.Text = "画像は専用画像ビューアで表示します。\nV / Enter で開きます。";
                viewerMessageLabel.Visible = true;
                viewerTextBox.Visible = false;
                viewerPictureBox.Image?.Dispose();
                viewerPictureBox.Image = null;
                viewerPictureBox.Visible = false;
                var openViewer = GetReusableImageViewer();
                if (openViewer != null && !string.Equals(openViewer.CurrentPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    openViewer.LoadMedia(fullPath, PreviewKind.Image, showErrorMessage: false);
                }
            }
            else if (kind == PreviewKind.Video)
            {
                stage = "VideoHint";
                await _previewDiagnosticDelayService.DelayAsync(
                    "PreviewOpen:Video",
                    fullPath,
                    _previewDiagnosticDelayService.PreviewOpenDelayMs,
                    token);
                if (_uiMode == UIMode.Viewer)
                {
                    _ = TryExitViewerToBrowser();
                }
                ClearPreview("Enter/V: 画像プレビューで静止画表示\nCtrl+Enter: 外部再生", reqId);
                ShowStatusMessage("Enter/V: 画像プレビューで静止画表示 / Ctrl+Enter: 外部再生");
                var openViewer = GetReusableImageViewer();
                if (openViewer != null)
                {
                    int initialSeconds = _settings.Preview.VideoSkipSeconds;
                    openViewer.LoadVideoStill(
                        fullPath,
                        _settings.Preview.VideoToolDirectory,
                        initialSeconds,
                        _settings.Preview.VideoPlaybackVolumePercent);
                }
                return;
            }
            else if (kind == PreviewKind.Text)
            {
                stage = "Text";
                await _previewDiagnosticDelayService.DelayAsync(
                    "PreviewOpen:Text",
                    fullPath,
                    _previewDiagnosticDelayService.PreviewOpenDelayMs,
                    token);
                // テキスト系: MainForm 内の viewerTextBox に表示
                // 重い読み込み処理と文字列エンコードをバックグラウンドスレッドへ分離
                var preview = await Task.Run<(string Text, string EncodingLabel)>(() =>
                {
                    int maxBytes = PreviewService.LargeTextThresholdBytes;
                    using (var fs = File.OpenRead(fullPath))
                    {
                        token.ThrowIfCancellationRequested();
                        int bytesToRead = (int)Math.Min(fs.Length, maxBytes);
                        byte[] buffer = new byte[bytesToRead];
                        int readCount = fs.Read(buffer, 0, bytesToRead);
                        token.ThrowIfCancellationRequested();
                        // エンコーディング判定
                        // Phase 3-keybind-cleanup1.2: 手動オーバーライドがある場合は優先
                        if (_viewerEncodingOverride == ViewerEncoding.UTF8)
                        {
                            string manualUtf8 = NormalizeNewlinesForViewerTextBox(System.Text.Encoding.UTF8.GetString(buffer, 0, readCount));
                            return (
                                manualUtf8 + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : ""),
                                "UTF-8 (manual)");
                        }
                        else if (_viewerEncodingOverride == ViewerEncoding.SJIS)
                        {
                            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                            var sjisManual = System.Text.Encoding.GetEncoding("shift_jis");
                            string manualSjis = NormalizeNewlinesForViewerTextBox(sjisManual.GetString(buffer, 0, readCount));
                            return (
                                manualSjis + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : ""),
                                "CP932 (manual)");
                        }
                        // 1. BOMチェック (StreamReader の標準機能に相当する処理)
                        if (readCount >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                        {
                            string utf8Bom = NormalizeNewlinesForViewerTextBox(System.Text.Encoding.UTF8.GetString(buffer, 3, readCount - 3));
                            return (
                                utf8Bom + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : ""),
                                "UTF-8 BOM");
                        }
                        if (readCount >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
                        {
                            string utf16Bom = NormalizeNewlinesForViewerTextBox(System.Text.Encoding.Unicode.GetString(buffer, 2, readCount - 2));
                            return (
                                utf16Bom + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : ""),
                                "UTF-16 LE BOM");
                        }
                        // 2. BOMなし UTF-8 試行 (例外を投げる設定で厳密に判定)
                        try
                        {
                            var utf8Strict = new System.Text.UTF8Encoding(false, true);
                            // 読み込み上限境界でマルチバイト文字が切断されている場合に備え、安全な長さまでトリミングする
                            int safeLength = GetSafeUtf8Length(buffer, readCount);
                            string utf8Result = NormalizeNewlinesForViewerTextBox(utf8Strict.GetString(buffer, 0, safeLength));
                            return (
                                utf8Result + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : ""),
                                "UTF-8");
                        }
                        catch (ArgumentException)
                        {
                            // UTF-8 として不正なバイトシーケンスが含まれる、または依然として不完全な場合は Shift_JIS フォールバックへ
                        }
                        // 3. Shift_JIS (CP932) フォールバック
                        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                        var sjis = System.Text.Encoding.GetEncoding("shift_jis");
                        string sjisText = NormalizeNewlinesForViewerTextBox(sjis.GetString(buffer, 0, readCount));
                        return (
                            sjisText + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : ""),
                            "CP932");
                    }
                }, token);
                // UI 更新前に最新リクエストかチェック (スレッドセーフティ対応)
                if (IsLatestPreviewRequest(reqId, fullPath, token) && _uiMode == UIMode.Viewer)
                {
                    _currentViewerKind = PreviewKind.Text;
                    _currentViewerDetectedEncodingLabel = preview.EncodingLabel;
                    ApplyViewerChromeState();
                    // 1. Popup クリア (テキストは出さない)
                    if (_previewPopup.Visible) _previewPopup.Clear();
                    // 2. Viewer パネル表示
                    viewerMessageLabel.Visible = false;
                    viewerPictureBox.Visible = false;
                    viewerTextBox.Text = preview.Text;
                    viewerTextBox.Visible = true;
                    // Phase 3-viewer-fix1: キーボードスクロールを可能にするためフォーカスを移す
                    viewerTextBox.Focus();
                    // Phase 5-viewer-status-finefix1: 永続表示ヘルパーを使用
                    ApplyViewerStatusLine("Text preview applied");
                }
            }
            else if (kind == PreviewKind.Markdown)
            {
                stage = "Markdown";
                await _previewDiagnosticDelayService.DelayAsync(
                    "PreviewOpen:Markdown",
                    fullPath,
                    _previewDiagnosticDelayService.PreviewOpenDelayMs,
                    token);
                string previewText = await MarkdownPreviewService.GetPreviewAsync(
                    fullPath,
                    PreviewService.LargeTextThresholdBytes,
                    token);
                if (IsLatestPreviewRequest(reqId, fullPath, token) && _uiMode == UIMode.Viewer)
                {
                    _currentViewerKind = PreviewKind.Markdown;
                    _currentViewerDetectedEncodingLabel = "Markdown";
                    ApplyViewerChromeState();
                    if (_previewPopup.Visible) _previewPopup.Clear();
                    viewerMessageLabel.Visible = false;
                    viewerPictureBox.Visible = false;
                    viewerTextBox.Text = previewText;
                    viewerTextBox.Visible = true;
                    viewerTextBox.Focus();
                    ApplyViewerStatusLine("Markdown preview applied");
                }
            }
            else if (kind == PreviewKind.Sqlite)
            {
                stage = "SQLite";
                await _previewDiagnosticDelayService.DelayAsync(
                    "PreviewOpen:SQLite",
                    fullPath,
                    _previewDiagnosticDelayService.PreviewOpenDelayMs,
                    token);
                string previewText = await SqlitePreviewService.GetPreviewAsync(fullPath, token);
                if (IsLatestPreviewRequest(reqId, fullPath, token) && _uiMode == UIMode.Viewer)
                {
                    _currentViewerKind = PreviewKind.Sqlite;
                    _currentViewerDetectedEncodingLabel = "SQLite";
                    ApplyViewerChromeState();
                    if (_previewPopup.Visible) _previewPopup.Clear();
                    viewerMessageLabel.Visible = false;
                    viewerPictureBox.Visible = false;
                    viewerTextBox.Text = previewText;
                    viewerTextBox.Visible = true;
                    viewerTextBox.Focus();
                    ApplyViewerStatusLine("SQLite preview applied");
                }
            }
            else if (kind == PreviewKind.LargeText)
            {
                stage = "LargeText";
                if (_uiMode != UIMode.Viewer)
                {
                    result = "SkippedUnsupported";
                    stage = "LargeTextBrowserSelection";
                    LogLargeTextEntryTiming("LargeText browser selection skipped", entrySw, fullPath, reqId, kind);
                    return;
                }
                LogLargeTextEntryTiming("LargeText branch entered", entrySw, fullPath, reqId, kind);
                // 巨大ファイル: まず先頭を素早く表示し、その裏でインデックス作成
                var state = new Models.LargeFilePreviewState { FilePath = fullPath };
                _largeFileState = state;
                _currentViewerKind = PreviewKind.LargeText;
                ApplyViewerChromeState();
                LogLargeTextEntryTiming("after ApplyViewerChromeState", entrySw, fullPath, reqId, kind, state);
                if (_previewPopup.Visible) _previewPopup.Clear();
                viewerPictureBox.Visible = false;
                viewerTextBox.Visible = false;
                viewerMessageLabel.Text = "LargeText 読み込み中...";
                viewerMessageLabel.Visible = true;
                state.IsIndexing = true;
                _largeFileControl.ResetFirstContentPaintMarker();
                _largeTextEntryStopwatch.Restart();
                ApplyViewerStatusLine("LargeText loading ui shown");
                LogLargeTextEntryTiming("after first ApplyViewerStatusLine", entrySw, fullPath, reqId, kind, state);
                await Task.Yield();
                try
                {
                    await _previewDiagnosticDelayService.DelayAsync(
                        "PreviewOpen:LargeText",
                        fullPath,
                        _previewDiagnosticDelayService.PreviewOpenDelayMs,
                        token);
                    LogLargeTextEntryTiming("before DetectLargeTextEncoding", entrySw, fullPath, reqId, kind, state);
                    TextPreviewProbeResult detected = previewProbe
                        ?? await Task.Run(() => PreviewService.ProbeTextPreview(fullPath), token);
                    state.DetectedEncoding = detected.Encoding;
                    state.DetectedEncodingLabel = detected.EncodingLabel;
                    state.HasBom = detected.HasBom;
                    state.IsBinaryLike = detected.IsBinaryLike;
                    state.IsLongLineDetected = detected.HasLongLine;
                    LogLargeTextEntryTiming("after DetectLargeTextEncoding", entrySw, fullPath, reqId, kind, state);
                    if (!IsLatestPreviewRequest(reqId, fullPath, token) || _uiMode != UIMode.Viewer)
                    {
                        result = "Superseded";
                        stage = "LargeTextAfterDetect";
                        LogService.Info(
                            $"[PreviewRequest] skippedReason=SupersededAfterDetect reqId={reqId} " +
                            $"requestPath='{fullPath}' activeReqId={_activePreviewRequestId}");
                        return;
                    }
                    if (state.IsBinaryLike)
                    {
                        result = "SkippedBinary";
                        stage = "LargeTextBinaryLikeGuard";
                        ClearPreview("LargeText対象外: binary-like file", reqId);
                        ApplyViewerStatusLine("LargeText binary-like guard");
                        ShowStatusMessage("LargeText対象外: binary-like file を検出しました。");
                        return;
                    }
                    if (state.IsEncodingUnsupportedForLargeText)
                    {
                        result = "SkippedUnsupported";
                        stage = "LargeTextUnsupportedEncodingGuard";
                        ClearPreview($"LargeText未対応: {state.DetectedEncodingLabel}", reqId);
                        ApplyViewerStatusLine("LargeText unsupported encoding guard");
                        ShowStatusMessage($"LargeText未対応: {state.DetectedEncodingLabel}");
                        return;
                    }
                    // 先頭数行を素早く読み込む (インデックス作成を待たずに表示するため)
                    LogLargeTextEntryTiming("before ReadFirstLinesQuicklyAsync", entrySw, fullPath, reqId, kind, state);
                    await Services.LargeFileLineReaderService.ReadFirstLinesQuicklyAsync(
                        state,
                        _largeFileControl.VisibleLineCount * 2,
                        token,
                        LargeTextInitialScanBytes);
                    LogLargeTextEntryTiming("after ReadFirstLinesQuicklyAsync", entrySw, fullPath, reqId, kind, state);
                    if (IsLatestPreviewRequest(reqId, fullPath, token) && _uiMode == UIMode.Viewer)
                    {
                        _largeFileControl.SetState(state, state.DetectedEncoding);
                        ApplyViewerStatusLine("LargeText SetState applied");
                        LogLargeTextEntryTiming("after _largeFileControl.SetState", entrySw, fullPath, reqId, kind, state);
                        LogViewerLayoutBounds("LargeText after SetState");
                        ApplyViewerStatusLine("LargeText status re-apply after Detect");
                        // 初回描画を実行
                        LogLargeTextEntryTiming("before UpdateLargeFileVirtualDisplayAsync", entrySw, fullPath, reqId, kind, state);
                        await UpdateLargeFileVirtualDisplayAsync(reqId, token);
                        LogLargeTextEntryTiming("after UpdateLargeFileVirtualDisplayAsync", entrySw, fullPath, reqId, kind, state);
                        ApplyViewerStatusLine("LargeText initial first paint ready");
                        statusStrip.Invalidate();
                        statusStrip.Update();
                        _largeFileControl.Invalidate();
                        _largeFileControl.Update();
                        BeginInvoke(new Action(async () =>
                        {
                            if (!IsLargeTextStatusApplyTarget(state)) return;
                            await Task.Delay(150);
                            if (!IsLargeTextStatusApplyTarget(state)) return;
                            LogLargeTextEntryTiming("after BuildLineIndex deferred start", entrySw, fullPath, reqId, kind, state);
                            StartLargeTextFullIndexAsync(state, reqId, entrySw, fullPath, kind, token);
                        }));
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (IsLatestPreviewRequest(reqId, fullPath, token))
                    {
                        ClearPreview($"Read Error: {ex.Message}", reqId);
                    }
                }
            }
            else if (kind == PreviewKind.Binary)
            {
                stage = "Binary";
                await _previewDiagnosticDelayService.DelayAsync(
                    "PreviewOpen:Binary",
                    fullPath,
                    _previewDiagnosticDelayService.PreviewOpenDelayMs,
                    token);
                // バイナリダンプ: 先頭数KBを読み込んでHexDumpを表示
                // 重い読み込みと文字列結合処理をバックグラウンドスレッドへ分離
                string dumpText = await Task.Run(() =>
                {
                    try
                    {
                        const int hexDumpMaxLength = 4096; // 最大 4KB までダンプ
                        using var fs = File.OpenRead(fullPath);
                        token.ThrowIfCancellationRequested();
                        int len = (int)Math.Min(fs.Length, hexDumpMaxLength);
                        byte[] buf = new byte[len];
                        int read = fs.Read(buf, 0, len);
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"[Binary Dump: {Path.GetFileName(fullPath)} - {(fs.Length > hexDumpMaxLength ? "First 4KB" : $"{read} Bytes")}]\n");
                        for (int i = 0; i < read; i += 16)
                        {
                            // 各行構築のタイミングでもキャンセルを拾えるようにする
                            if (i % 512 == 0) token.ThrowIfCancellationRequested();
                            // アドレス部
                            sb.Append($"{i:X8}  ");
                            // 16進数部
                            for (int j = 0; j < 16; j++)
                            {
                                if (i + j < read) sb.Append($"{buf[i + j]:X2} ");
                                else sb.Append("   ");
                                if (j == 7) sb.Append(" ");
                            }
                            sb.Append(" |");
                            // ASCII文字列表現部
                            for (int j = 0; j < 16; j++)
                            {
                                if (i + j < read)
                                {
                                    byte b = buf[i + j];
                                    sb.Append((b >= 32 && b <= 126) ? (char)b : '.');
                                }
                            }
                            sb.AppendLine("|");
                        }
                        return sb.ToString();
                    }
                    catch (IOException)
                    {
                        return "[プレビュー不可: 使用中またはロックされています]";
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return "[プレビュー不可: アクセス権限がありません]";
                    }
                }, token);
                if (IsLatestPreviewRequest(reqId, fullPath, token))
                {
                    _currentViewerKind = PreviewKind.Binary;
                    ApplyViewerChromeState();
                    if (_previewPopup.Visible) _previewPopup.Clear();
                    viewerMessageLabel.Visible = false;
                    viewerPictureBox.Visible = false;
                    viewerTextBox.Text = dumpText;
                    viewerTextBox.Visible = true;
                    // Phase 3-viewer-fix1: バイナリダンプ時もスクロール可能に
                    viewerTextBox.Focus();
                    // Phase 5-viewer-status-finefix1: 永続表示として設定
                    // Phase 5-ui-layout-fix2: Viewer モードのときだけ表示 (Browser 中の preview で混線しない)
                    if (_uiMode == UIMode.Viewer)
                    {
                        NormalizeStatusLabelLayout();
                        ApplyViewerStatusLine();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = "Canceled";
            stage = "Canceled";
            // Task.Delay や Task.Run 内でのキャンセル。意図した動作なので何もせず終了。
            LogService.Info($"[PreviewRequest] skippedReason=Canceled reqId={reqId} requestPath='{requestPath}'");
        }
        catch (Exception ex)
        {
            result = "Failed";
            stage = "Exception";
            failedException = ex;
#if DEBUG
            Debug.WriteLine($"[ReqId: {reqId}] Preview Error ({ex.GetType().Name}): {ex.Message}");
#endif
            if (IsLatestPreviewRequest(reqId, requestPath, token))
            {
                ClearPreview($"エラー: {ex.Message}", reqId);
            }
        }
        finally
        {
            string? currentPath = GetCurrentPreviewSelectionPath();
            if (failedException != null)
            {
                LogService.Warn(
                    $"[PreviewRequest] failed reqId={reqId} stage='{stage}' result={result} kind={resolvedKind} " +
                    $"elapsedMs={entrySw.ElapsedMilliseconds} requestPath='{requestPath}' currentPath='{currentPath}' " +
                    $"exceptionType='{failedException.GetType().Name}' message='{failedException.Message}'");
            }
            else
            {
                LogService.Info(
                    $"[PreviewRequest] completed reqId={reqId} result={result} stage='{stage}' kind={resolvedKind} " +
                    $"elapsedMs={entrySw.ElapsedMilliseconds} requestPath='{requestPath}' currentPath='{currentPath}'");
            }
            if (_activePreviewRequestId == reqId)
            {
                _previewRequestCoordinator.EndRequest(_activePreviewRequestId);
            }
        }
    }
    private void StartLargeTextFullIndexAsync(
        Models.LargeFilePreviewState state,
        int reqId,
        Stopwatch entrySw,
        string fullPath,
        PreviewKind kind,
        CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                LogService.Info(
                    $"[LargeTextIndexSwap] Before local build reqId={reqId} " +
                    $"visibleOffsets={state.LineOffsets.Count} isIndexing={state.IsIndexing}");
                var result = await Services.LargeFileLineReaderService
                    .BuildLineIndexOffsetsAsync(state.FilePath, token, state.DetectedEncoding);
                LogService.Info(
                    $"[LargeTextIndexSwap] After local build reqId={reqId} " +
                    $"visibleOffsetsStill={state.LineOffsets.Count} " +
                    $"builtOffsets={result.LineOffsets.Count} totalBytes={result.TotalBytes}");
                BeginInvoke(new Action(() =>
                {

                    if (IsDisposed || !IsHandleCreated)
                    {
                        return;
                    }
                    if (_largeFileState != state
                        || _uiMode != UIMode.Viewer
                        || _currentViewerKind != PreviewKind.LargeText
                        || !string.Equals(_currentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        LogService.Info(
                            $"[LargeTextIndexSwap] Skip stale apply reqId={reqId} " +
                            $"statePath='{state.FilePath}' current='{_currentPreviewTarget}'");
                        return;
                    }
                    LogService.Info(
                        $"[LargeTextIndexSwap] Before UI swap reqId={reqId} " +
                        $"visibleOffsets={state.LineOffsets.Count} " +
                        $"builtOffsets={result.LineOffsets.Count}");
                    state.ReplaceLineOffsets(result.LineOffsets, result.TotalBytes);
                    state.IsIndexing = false;
                    _largeFileControl.UpdateScrollSettings();
                    int maxFirstVisibleLine = _largeFileControl.GetMaxFirstVisibleLine();
                    if (state.FirstVisibleLine > maxFirstVisibleLine)
                    {
                        state.FirstVisibleLine = maxFirstVisibleLine;
                    }
                    ApplyViewerStatusLine("LargeText immutable index swap completed");
                    LogService.Info(
                        $"[LargeTextIndexSwap] After UI swap reqId={reqId} " +
                        $"visibleOffsets={state.LineOffsets.Count} isIndexing={state.IsIndexing}");
                    if (state.PendingEndAfterIndex)
                    {
                        state.PendingEndAfterIndex = false;
                        _ = NavigateLargeFilePreviewAsync(
                            _largeFileControl.GetMaxFirstVisibleLine(),
                            "PendingEndAfterIndex");
                    }
                    else
                    {
                        _largeFileControl.Invalidate();
                    }
                }));
            }
            catch (OperationCanceledException)
            {
                LogService.Info(
                    $"[LargeTextIndexSwap] Build canceled reqId={reqId} path='{state.FilePath}'");
            }
            catch (Exception ex)
            {
                LogService.Error($"[LargeTextIndexSwap] BuildLineIndexOffsetsAsync failed reqId={reqId}", ex);
            }
        }, token);
    }
    private void ExecuteOpenWithViewer()
    {
        var item = GetCurrentBrowserItem();
        if (item == null || item.Text == "..") return;
        string? fullPath = item.Tag as string;
        if (fullPath == null || Directory.Exists(fullPath))
        {
            ShowStatusMessage("外部Viewer / 関連付けはファイルのみ対象です。");
            return;
        }
        string? exePath = _settings.ExternalTools?.ExternalViewerPath;
        bool allowShellFallback = _settings.ExternalTools?.FallbackToShellWhenViewerMissing ?? true;
        bool hasConfiguredViewer = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
        if (!hasConfiguredViewer)
        {
            if (allowShellFallback)
            {
                OpenPathWithShellAssociation(fullPath);
            }
            else
            {
                string message = string.IsNullOrWhiteSpace(exePath)
                    ? "外部Viewerが未設定です。設定 > 外部連携で指定するか、関連付けフォールバックを ON にしてください。"
                    : $"外部Viewerが見つかりません。設定 > 外部連携で確認してください: {exePath}";
                ShowStatusMessage(message);
            }
            return;
        }
        string? error = ExternalToolService.OpenWithViewer(exePath!, fullPath);
        if (error != null) ShowStatusMessage(error);
    }
    private void ExecuteOpenWithEditor()
    {
        if (GuardReadOnlyBrowserTab("外部エディタ起動")) return;
        if (GuardMutationBusy("外部エディタ起動")) return;
        var item = GetCurrentBrowserItem();
        if (item == null || item.Text == "..") return;
        string? fullPath = item.Tag as string;
        if (fullPath == null || Directory.Exists(fullPath))
        {
            ShowStatusMessage("外部Editorはファイルのみ対象です。");
            return;
        }
        // --- text gate ---
        // テキスト系ファイル以外（バイナリや画像など）は、外部エディタで開くのではなく内蔵 Viewer 経路へ回す。
        var kind = PreviewService.GetPreviewKind(fullPath);
        if (kind != PreviewKind.Text && kind != PreviewKind.Markdown && kind != PreviewKind.LargeText)
        {
            ExecutePreviewLaunch();
            return;
        }
        // -----------------
        // 手動起動 (E / F4) の場合は拡張子チェックをスキップし、ユーザーの判断を優先する。
        string? exePath = _settings.ExternalTools?.ExternalEditorPath;
        bool hasConfiguredEditor = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
        if (!hasConfiguredEditor)
        {
            // 未設定時は notepad.exe を実体パス解決してフォールバックとして使用する
            exePath = ResolveNotepadPath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                ShowStatusMessage("外部Editorが未設定で、notepad.exe も見つかりませんでした。");
                return;
            }
            string? fallbackError = ExternalToolService.OpenWithEditor(exePath, fullPath);
            if (fallbackError != null)
            {
                ShowStatusMessage($"外部Editorが未設定かつ notepad.exe の起動にも失敗しました: {fallbackError}");
            }
            else
            {
                ShowStatusMessage("外部Editorが未設定のため notepad.exe で開きました。");
            }
            return;
        }
        string? error = ExternalToolService.OpenWithEditor(exePath!, fullPath);
        if (error != null) ShowStatusMessage(error);
    }
    private static string? ResolveNotepadPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        // 最後の手段としてファイル名のみ（ExternalToolService 側で File.Exists チェックされるため、ここを通ると失敗する可能性が高いが、契約上 null でない値を返す試み）
        return "notepad.exe";
    }
    private void ExecuteOpenWithDiff()
    {
        if (GuardClipboardBusy()) return;
        SelectionResult selection = ResolveSelection();
        if (selection.Count != 2)
        {
            ShowStatusMessage("外部Diffはちょうど 2 件選択時のみ使えます。");
            return;
        }
        string leftPath = selection.FullPaths[0];
        string rightPath = selection.FullPaths[1];
        if (!File.Exists(leftPath) || !File.Exists(rightPath))
        {
            ShowStatusMessage("外部Diffはファイル 2 件比較専用です。");
            return;
        }
        string? exePath = _settings.ExternalTools?.ExternalDiffPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            ShowStatusMessage("外部Diffが未設定です。設定 > 外部連携で比較ツールを指定してください。");
            return;
        }
        string? error = ExternalToolService.OpenWithDiff(exePath, leftPath, rightPath);
        if (error != null)
        {
            ShowStatusMessage(error);
            return;
        }
        ShowStatusMessage("外部Diffを起動しました。");
    }
    private void OpenTerminalInCurrentDirectory(ShellKind kind)
    {
        OpenTerminalInWorkingDirectory(_navigationService.CurrentPath, kind);
    }
    private static string? GetBrowserItemWorkingDirectory(string itemPath)
    {
        if (Directory.Exists(itemPath))
        {
            return itemPath;
        }
        if (!File.Exists(itemPath))
        {
            return null;
        }
        string? workingDirectory = Path.GetDirectoryName(itemPath);
        return string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
    }
    private void ExecuteShell()
    {
        // ShowNullable を使い、Cancel 時は null を返す（空入力OK = cmd.exe 起動、入力ありOK = そのコマンドを実行）
        string? command = SimpleInputDialog.ShowNullable("実行するコマンドを入力してください\n(空の場合はコマンドプロンプトを開きます):", "sHell", "");
        if (command == null) return; // Cancel
        string? error = ExternalToolService.ExecuteShell(_navigationService.CurrentPath, command);
        if (error != null) ShowStatusMessage(error);
    }
    /// <summary>
    /// x キーから呼ばれる exec ダイアログ。
    /// 選択ファイルがあれば引用符付きフルパスを初期入力に入れる。
    /// 空入力はキャンセル扱い（cmd ターミナル起動は h/Shift+h の責務）。
    /// </summary>
    private void ExecuteShellDialog()
    {
        // 選択ファイルがあれば初期入力に引用符付きパスを入れる
        string initialValue = "";
        var item = GetCurrentBrowserItem();
        if (item != null && item.Text != ".." && item.Tag is string fullPath && File.Exists(fullPath))
        {
            initialValue = $"\"{fullPath}\"";
        }
        string? command = SimpleInputDialog.ShowNullable(
            "実行するコマンドを入力してください:",
            "eXec",
            initialValue);
        if (string.IsNullOrWhiteSpace(command)) return; // 空入力またはキャンセル
        string? error = ExternalToolService.ExecuteShell(_navigationService.CurrentPath, command);
        if (error != null) ShowStatusMessage(error);
    }
    private void ExecuteCurrentFile()
    {
        if (GuardClipboardBusy()) return;
        var item = GetCurrentBrowserItem();
        if (item == null || item.Text == "..") return;
        string? fullPath = item.Tag as string;
        if (fullPath == null || Directory.Exists(fullPath))
        {
            ShowStatusMessage("実行(eXecute)はファイルのみ対象です。");
            return;
        }
        ExecuteCurrentFileAction(fullPath);
    }
    private void ExecuteAttribute()
    {
        if (GuardReadOnlyBrowserTab("属性変更")) return;
        var selection = ResolveSelection();
        if (selection.Count == 0)
        {
            ShowStatusMessage("属性変更の対象がありません。");
            return;
        }
        var roots = selection.FullPaths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            ShowStatusMessage("属性変更の対象が見つかりません。");
            return;
        }
        string firstPath = roots[0];
        AttributeAggregateState readOnlyState;
        AttributeAggregateState hiddenState;
        AttributeAggregateState systemState;
        AttributeAggregateState archiveState;
        DateTime initialLastWrite;
        DateTime initialCreation;
        DateTime initialAccess;
        try
        {
            readOnlyState = AggregateAttributeState(roots, FileAttributes.ReadOnly);
            hiddenState = AggregateAttributeState(roots, FileAttributes.Hidden);
            systemState = AggregateAttributeState(roots, FileAttributes.System);
            archiveState = AggregateAttributeState(roots, FileAttributes.Archive);

            if (Directory.Exists(firstPath))
            {
                initialLastWrite = Directory.GetLastWriteTime(firstPath);
                initialCreation = Directory.GetCreationTime(firstPath);
                initialAccess = Directory.GetLastAccessTime(firstPath);
            }
            else
            {
                initialLastWrite = File.GetLastWriteTime(firstPath);
                initialCreation = File.GetCreationTime(firstPath);
                initialAccess = File.GetLastAccessTime(firstPath);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"属性情報の取得に失敗しました。\n{ex.Message}", "属性変更", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string targetLabel = roots.Count == 1
            ? Path.GetFileName(firstPath)
            : $"Mark {roots.Count} 件";
        var request = new AttributeDialogRequest(
            targetLabel,
            readOnlyState,
            hiddenState,
            systemState,
            archiveState,
            initialLastWrite,
            initialCreation,
            initialAccess);
        var dialogResult = AttributeDialog.Show(request);
        if (dialogResult is null)
        {
            return;
        }
        var targets = ResolveAttributeTargets(roots, dialogResult.IncludeSubdirectories);
        if (targets.Count == 0)
        {
            ShowStatusMessage("属性変更の適用対象がありません。");
            return;
        }
        RunAttributeUpdate(targets, dialogResult);
    }
    private static AttributeAggregateState AggregateAttributeState(IReadOnlyList<string> paths, FileAttributes targetBit)
    {
        bool anySet = false;
        bool anyClear = false;
        foreach (var path in paths)
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if (attrs.HasFlag(targetBit))
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
                return AttributeAggregateState.Mixed;
            }
        }
        if (anySet) return AttributeAggregateState.AllSet;
        return AttributeAggregateState.AllClear;
    }
    private List<string> ResolveAttributeTargets(IReadOnlyList<string> roots, bool includeSubdirectories)
    {
        var resolved = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!visited.Add(root))
            {
                continue;
            }
            if (!File.Exists(root) && !Directory.Exists(root))
            {
                continue;
            }
            resolved.Add(root);
            if (!includeSubdirectories || !Directory.Exists(root))
            {
                continue;
            }
            TraverseDirectoryForAttributeUpdate(root, resolved, visited);
        }
        return resolved;
    }
    private void TraverseDirectoryForAttributeUpdate(string rootDirectory, List<string> resolved, HashSet<string> visited)
    {
        var stack = new Stack<string>();
        stack.Push(rootDirectory);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(current);
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }
            foreach (var file in files)
            {
                if (visited.Add(file))
                {
                    resolved.Add(file);
                }
            }
            foreach (var directory in directories)
            {
                if (!visited.Add(directory))
                {
                    continue;
                }
                resolved.Add(directory);
                try
                {
                    var attrs = File.GetAttributes(directory);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }
                stack.Push(directory);
            }
        }
    }
    private void RunAttributeUpdate(IReadOnlyList<string> targets, AttributeDialogResult options)
    {
        int totalCount = targets.Count;
        int successCount = 0;
        int failCount = 0;
        var errors = new List<string>();
        FileOperationProgressFallbackForm? progressForm = null;
        bool showProgress = options.IncludeSubdirectories || totalCount >= 64;
        if (showProgress)
        {
            progressForm = Presentation.FileOperationFallbackUiPresenter.ShowProgressFallback(
                this,
                "属性 / 日時変更",
                totalCount,
                requestCancel: null,
                canCancel: false);
            progressForm.UpdateProgress(0, totalCount, "準備中...", cancelRequested: false);
        }
        int progressCounter = 0;
        var lastProgress = DateTime.UtcNow;
        for (int i = 0; i < targets.Count; i++)
        {
            string path = targets[i];
            try
            {
                ApplyAttributesAndTimestamps(path, options);
                successCount++;
            }
            catch (Exception ex)
            {
                failCount++;
                if (errors.Count < 5)
                {
                    errors.Add($"{path}: {ex.Message}");
                }
            }
            if (progressForm != null)
            {
                progressCounter++;
                bool shouldUpdate = progressCounter >= 32 || (DateTime.UtcNow - lastProgress).TotalMilliseconds >= 150 || i == targets.Count - 1;
                if (shouldUpdate)
                {
                    progressCounter = 0;
                    lastProgress = DateTime.UtcNow;
                    progressForm.UpdateProgress(i + 1, totalCount, Path.GetFileName(path), cancelRequested: false);
                    Application.DoEvents();
                }
            }
        }
        if (progressForm != null)
        {
            progressForm.Complete($"完了: 成功 {successCount} 件 / 失敗 {failCount} 件");
            progressForm.Close();
            progressForm.Dispose();
        }
        LoadDirectory(_navigationService.CurrentPath);
        if (failCount == 0)
        {
            ShowStatusMessage($"属性/日時を変更しました。({successCount} 件)");
            return;
        }
        string detail = errors.Count > 0 ? "\n" + string.Join("\n", errors) : string.Empty;
        MessageBox.Show(
            $"属性/日時変更の一部に失敗しました。\n成功: {successCount} 件\n失敗: {failCount} 件{detail}",
            "属性 / 日時変更",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }
    private static void ApplyAttributesAndTimestamps(string path, AttributeDialogResult options)
    {
        var current = File.GetAttributes(path);
        var next = current;
        next = ApplyActionToBit(next, FileAttributes.ReadOnly, options.ReadOnlyAction);
        next = ApplyActionToBit(next, FileAttributes.Hidden, options.HiddenAction);
        next = ApplyActionToBit(next, FileAttributes.System, options.SystemAction);
        next = ApplyActionToBit(next, FileAttributes.Archive, options.ArchiveAction);
        if (next != current)
        {
            File.SetAttributes(path, next);
        }
        bool isDirectory = Directory.Exists(path);
        if (options.ChangeLastWriteTime)
        {
            if (isDirectory) Directory.SetLastWriteTime(path, options.LastWriteTime);
            else File.SetLastWriteTime(path, options.LastWriteTime);
        }
        if (options.ChangeCreationTime)
        {
            if (isDirectory) Directory.SetCreationTime(path, options.CreationTime);
            else File.SetCreationTime(path, options.CreationTime);
        }
        if (options.ChangeLastAccessTime)
        {
            if (isDirectory) Directory.SetLastAccessTime(path, options.LastAccessTime);
            else File.SetLastAccessTime(path, options.LastAccessTime);
        }
    }
    private static FileAttributes ApplyActionToBit(FileAttributes current, FileAttributes bit, AttributeChangeAction action)
    {
        return action switch
        {
            AttributeChangeAction.Set => current | bit,
            AttributeChangeAction.Clear => current & ~bit,
            AttributeChangeAction.Preserve => current,
            _ => current
        };
    }
    /// <summary>V キー: プレビューウィンドウの表示/非表示を切り替える。</summary>
    private void TogglePreviewPopup()
    {
        var plan = _viewerPreviewCoordinator.CreatePreviewPopupTogglePlan(
            _previewPopupVisible,
            _settings.Preview.X != -1,
            _previewPopup.IsManuallyPositioned);
        _previewPopupVisible = plan.NextVisible;
        if (plan.ShouldHide)
        {
#if DEBUG
            Debug.WriteLine("[TogglePreviewPopup] Executing _previewPopup.Hide()");
#endif
            _previewPopup.Hide();
        }
        if (plan.ShouldPosition)
        {
            PositionPreviewPopup();
        }
        if (plan.ShouldShow)
        {
            _previewPopup.ShowWithoutFocus();
        }
        if (plan.ShouldPersist)
        {
            SavePreviewSettings();
        }
        if (plan.ShouldRefresh)
        {
            RequestPreviewRefresh(force: true);
        }
    }
    private ViewerPreviewCoordinator.BrowserOpenRequest? CreateBrowserOpenRequest(string? fullPath, bool allowExecuteTarget)
    {
        return _viewerPreviewCoordinator.CreateBrowserOpenRequest(
            fullPath,
            allowExecuteTarget,
            IsExecuteTarget,
            IsArchiveTarget,
            GetEffectivePreviewKind);
    }
    private void ExecuteBrowserOpenRequest(ViewerPreviewCoordinator.BrowserOpenRequest? request)
    {
        _viewerPreviewCoordinator.ExecuteBrowserOpenRequest(
            request,
            new ViewerPreviewCoordinator.BrowserOpenExecutionContext
            {
                ExecuteConfirmedFile = ExecuteConfirmedFile,
                ShowArchiveContentsOrFallback = ShowArchiveContentsOrFallback,
                OpenMediaViewer = OpenImageViewer,
                EnterInternalViewer = EnterInternalViewer
            });
    }
    private void EnterInternalViewer(PreviewKind kind)
    {
        _currentViewerKind = kind;
        SwitchUIMode(UIMode.Viewer);
    }
    private void RequestPreviewRefresh()
    {
        RequestPreviewRefresh(force: false);
    }
    private void RequestPreviewRefresh(bool force)
    {
        var currentItem = GetCurrentBrowserItem();
        string? requestPath = GetCurrentPreviewSelectionPath();
        if (string.IsNullOrEmpty(requestPath))
        {
            _previewRequestCoordinator.Cancel();
            _lastPreviewRequestedPath = null;
            _previewRequestCoordinator.EndRequest(_activePreviewRequestId);
            ResetBrowserAutoPreviewSuppressedState();
            ClearPreview("選択なしのためプレビューなし");
            return;
        }
        if (!force)
        {
            PreviewKind shallowKind = GetBrowserSelectionPreviewKind(currentItem, requestPath);
            if (!IsBrowserAutoPreviewEligible(shallowKind))
            {
                string skipResult = shallowKind == PreviewKind.Binary ? "SkippedBinary" : "SkippedUnsupported";
                LogService.Info(
                    $"[PreviewRequest] completed reqId=-1 result={skipResult} kind={shallowKind} " +
                    $"requestPath='{requestPath}' currentPath='{GetCurrentPreviewSelectionPath()}' force={force}");
                _previewRequestCoordinator.Cancel();
                _lastPreviewRequestedPath = null;
                _previewRequestCoordinator.EndRequest(_activePreviewRequestId);
                ShowBrowserAutoPreviewSuppressedMessage(requestPath, shallowKind);
                return;
            }
        }
        if (!force
            && string.Equals(_lastPreviewRequestedPath, requestPath, StringComparison.OrdinalIgnoreCase)
            && _previewRequestCoordinator.IsInFlight)
        {
            LogService.Info($"[PreviewRequest] skippedReason=DuplicatePath requestPath='{requestPath}' activeReqId={_activePreviewRequestId}");
            return;
        }
        ResetBrowserAutoPreviewSuppressedState();
        _previewRequestCoordinator.Cancel();
        CancellationToken token = _previewRequestCoordinator.StartNewRequest(out int reqId);
        Interlocked.Exchange(ref _activePreviewRequestId, reqId);
        _lastPreviewRequestedPath = requestPath;
        LogService.Info($"[PreviewRequest] queued reqId={reqId} requestPath='{requestPath}' force={force}");
        _ = UpdatePreviewAsync(reqId, requestPath, token);
    }
    /// <summary>O キー: 設定画面を開く。OK 保存後は _settings を再読込して次のコマンドに反映する。</summary>
    private void OpenSettingsForm(SettingsForm.InitialTab initialTab = SettingsForm.InitialTab.Display)
    {
        bool importedSettingsFlow = false;
        try
        {
            LogService.Info($"Opening SettingsForm. initialTab={initialTab}");
            HideTransientOverlaysBeforeModalDialog();
            BrowserTabRuntimeStateSnapshot runtimeBrowserTabState = CaptureBrowserTabRuntimeStateSnapshot();
            using var form = new SettingsForm(_settings, _featureProfile, initialTab);
            form.OpenManagedTrashDialogRequested += (s, e) =>
            {
                OpenManagedTrashDialog();
            };
            form.SettingsApplied += (s, e) =>
            {
                var reloaded = MidFD.Configuration.SettingsManager.Load(out SettingsManager.SettingsLoadMetadata settingsLoadMetadata);
                _settings.Profile = reloaded.Profile;
                _settings.Input = reloaded.Input ?? new InputSettings();
                _settings.Input.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(_settings.Input.MouseGestureCommandMap);
                InputSettings.NormalizeAndMigrateFunctionKeyChords(_settings.Input);
                _settings.SevenZip = reloaded.SevenZip;
                _settings.ExternalTools = reloaded.ExternalTools;
                _settings.Appearance = reloaded.Appearance;
                _settings.Logging = reloaded.Logging;
                _settings.Preview = reloaded.Preview;
                _settings.FileOperations = reloaded.FileOperations;
                _settings.BrowserTabs = reloaded.BrowserTabs;
                _settings.Fonts = reloaded.Fonts;
                _settings.Session = reloaded.Session;
                ApplyFeatureProfile(settingsLoadMetadata.IsMouseGesturesExplicit);
                RestoreBrowserTabRuntimeStateSnapshot(runtimeBrowserTabState);
                LogService.ApplySettings(_settings.Logging);
                LogFontRouteDiag("SettingsApplied:BeforeApplyFontSettings");
                ApplyFontSettings();
                ApplyColorSettings();
                viewerTextBox.WordWrap = _settings.Preview.ViewerWordWrap;
                viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
                if (SessionRestorePolicy.ShouldRestoreColumnCount(_settings.Session))
                {
                    _columnCount = Math.Clamp(_settings.Session.LastColumnCount, 1, 9);
                }
                if (SessionRestorePolicy.ShouldRestoreSort(_settings.Session))
                {
                    _currentSort = _settings.Session.LastSortKind;
                    _sortAscending = _settings.Session.LastSortAscending;
                }
                LoadDirectory(_navigationService.CurrentPath);
                RebuildMenuStripAfterSettingsApply();
                UpdateFunctionBar();
                LogFontRouteDiag("SettingsApplied:AfterAll");
                ShowStatusMessage("設定を適用しました。");
            };
            var result = form.ShowDialog(this);
            importedSettingsFlow = form.ImportedSettingsApplied;
            LogService.Info($"SettingsForm closed. result={result}");
            if (result == DialogResult.OK)
            {
                // SettingsForm が Save した内容を読み直してインメモリ設定と一致させる
                var reloaded = MidFD.Configuration.SettingsManager.Load(out SettingsManager.SettingsLoadMetadata settingsLoadMetadata);
                _settings.Profile = reloaded.Profile;
                _settings.Input = reloaded.Input ?? new InputSettings();
                _settings.Input.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(_settings.Input.MouseGestureCommandMap);
                InputSettings.NormalizeAndMigrateFunctionKeyChords(_settings.Input);
                _settings.SevenZip = reloaded.SevenZip;
                _settings.ExternalTools = reloaded.ExternalTools;
                _settings.Appearance = reloaded.Appearance;
                _settings.Logging = reloaded.Logging;
                _settings.Preview = reloaded.Preview;
                _settings.FileOperations = reloaded.FileOperations;
                _settings.BrowserTabs = reloaded.BrowserTabs;
                _settings.Fonts = reloaded.Fonts;
                _settings.Session = reloaded.Session;
                ApplyFeatureProfile(settingsLoadMetadata.IsMouseGesturesExplicit);
                RestoreBrowserTabRuntimeStateSnapshot(runtimeBrowserTabState);
                LogService.ApplySettings(_settings.Logging);
                LogFontRouteDiag("SettingsOK:BeforeApplyFontSettings");
                ApplyFontSettings();
                ApplyColorSettings();
                viewerTextBox.WordWrap = _settings.Preview.ViewerWordWrap;
                viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
                if (SessionRestorePolicy.ShouldRestoreColumnCount(_settings.Session))
                {
                    _columnCount = Math.Clamp(_settings.Session.LastColumnCount, 1, 9);
                }
                if (SessionRestorePolicy.ShouldRestoreSort(_settings.Session))
                {
                    _currentSort = _settings.Session.LastSortKind;
                    _sortAscending = _settings.Session.LastSortAscending;
                }
                LoadDirectory(_navigationService.CurrentPath);
                RebuildMenuStripAfterSettingsApply();
                UpdateFunctionBar();
                LogFontRouteDiag("SettingsOK:AfterAll");
                ShowStatusMessage(importedSettingsFlow
                    ? "設定をインポートし、現在の設定へ反映しました。"
                    : "設定を保存しました。");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("SettingsForm open failed.", ex);
            LogService.Error(ex.ToString());
            ShowStatusMessage(importedSettingsFlow
                ? $"設定は保存されましたが、現在の画面への反映に失敗しました: {ex.Message}"
                : $"設定画面を開けませんでした: {ex.Message}");
        }
    }
    private void HideTransientOverlaysBeforeModalDialog()
    {
        HideCommandHintOverlay("OpenSettingsForm");
        HideHeaderTooltipsForModalDialog();
    }
    private void HideHeaderTooltipsForModalDialog()
    {
        if (_headerToolTip == null)
        {
            return;
        }
        _headerToolTip.Hide(this);
        _headerToolTip.Hide(lblPath);
        _headerToolTip.Hide(infoRow2Panel);
        _headerToolTip.Hide(lblName);
        _headerToolTip.Hide(infoRow4Panel);
    }
    private void OpenWorkspaceSnapshotDialog()
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "標準機能（推奨）では Workspace Snapshot は無効です。"))
        {
            return;
        }
        if (_workspaceSnapshotStorage == null)
        {
            MessageBox.Show(this, "Workspace スナップショットの保存先を初期化できません。", "Workspace スナップショット", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var dialog = new WorkspaceSnapshotDialog(
            () => _workspaceSnapshotStorage.LoadEntries(),
            SaveCurrentWorkspaceSnapshot,
            RestoreWorkspaceSnapshot,
            RenameWorkspaceSnapshot,
            DeleteWorkspaceSnapshot,
            ExportWorkspaceSnapshot,
            ImportWorkspaceSnapshot,
            ExportAllWorkspaceSnapshots,
            ImportAllWorkspaceSnapshots);
        dialog.ShowDialog(this);
    }
    private bool SaveCurrentWorkspaceSnapshot(IWin32Window owner)
    {
        if (_workspaceSnapshotStorage == null)
        {
            MessageBox.Show(owner, "Workspace スナップショットの保存先を初期化できません。", "Workspace スナップショット", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        string defaultName = $"Snapshot {DateTime.Now:yyyy-MM-dd HH-mm}";
        string? snapshotName = SimpleInputDialog.ShowNullable(
            "保存するスナップショット名を入力してください。",
            "Workspace スナップショット保存",
            defaultName,
            new SimpleInputDialog.DisplayOptions(
                SummaryText: "現在のカテゴリ / タブ / マーク / タブ固定 / フィルタロックを保存します。"));
        if (snapshotName == null)
        {
            return false;
        }
        string trimmedName = snapshotName.Trim();
        if (trimmedName.Length == 0)
        {
            MessageBox.Show(owner, "スナップショット名を入力してください。", "Workspace スナップショット保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (_workspaceSnapshotStorage.ExistsByName(trimmedName) &&
            MessageBox.Show(
                owner,
                $"同名のスナップショットがすでにあります。上書きしますか？\n\n{trimmedName}",
                "Workspace スナップショット保存",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return false;
        }
        WorkspaceState state = CaptureWorkspaceSnapshotState();
        if (!_workspaceSnapshotStorage.TrySaveSnapshot(trimmedName, state, out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "Workspace スナップショット保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"Workspace スナップショットを保存しました: {trimmedName}");
        return true;
    }
    private bool RestoreWorkspaceSnapshot(IWin32Window owner, WorkspaceSnapshotEntry entry)
    {
        if (_workspaceSnapshotStorage == null)
        {
            MessageBox.Show(owner, "Workspace スナップショットの保存先を初期化できません。", "Workspace スナップショット復元", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (!_workspaceSnapshotStorage.TryLoadSnapshotState(entry.SnapshotId, out WorkspaceState? state, out string errorMessage) || state == null)
        {
            MessageBox.Show(owner, errorMessage, "Workspace スナップショット復元", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        DialogResult confirm = MessageBox.Show(
            owner,
            $"現在のカテゴリ/タブ構成を、選択したスナップショットで置き換えます。\n\n名前: {entry.Name}\nカテゴリ: {entry.CategoryCount}\nタブ: {entry.TabCount}\nマーク: {entry.MarkedCount}\nアクティブ: {entry.ActivePath}\n\n必要に応じて先に現在状態をスナップショット保存してください。",
            "Workspace スナップショット復元",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return false;
        }
        BrowserTabRuntimeStateSnapshot rollbackState = CaptureBrowserTabRuntimeStateSnapshot();
        try
        {
            RestoreBrowserTabRuntimeStateSnapshot(CreateBrowserTabRuntimeStateSnapshot(state));
            CaptureActiveBrowserTabState();
            StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
            SaveWorkspaceStateStore();
            SettingsManager.Save(_settings);
            ShowStatusMessage($"Workspace スナップショットを復元しました: {entry.Name}");
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                RestoreBrowserTabRuntimeStateSnapshot(rollbackState);
                CaptureActiveBrowserTabState();
                StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
                SaveWorkspaceStateStore();
                SettingsManager.Save(_settings);
            }
            catch (Exception rollbackEx)
            {
                LogService.Error("Workspace snapshot rollback failed.", rollbackEx);
            }
            LogService.Error("Workspace snapshot restore failed.", ex);
            MessageBox.Show(owner, $"Workspace スナップショットの復元に失敗しました。\n{ex.Message}", "Workspace スナップショット復元", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
    private bool RenameWorkspaceSnapshot(IWin32Window owner, WorkspaceSnapshotEntry entry)
    {
        if (_workspaceSnapshotStorage == null)
        {
            MessageBox.Show(owner, "Workspace スナップショットの保存先を初期化できません。", "Workspace スナップショット名変更", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        string? renamed = SimpleInputDialog.ShowNullable("新しいスナップショット名を入力してください。", "Workspace スナップショット名変更", entry.Name);
        if (renamed == null)
        {
            return false;
        }
        if (!_workspaceSnapshotStorage.TryRenameSnapshot(entry.SnapshotId, renamed, out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "Workspace スナップショット名変更", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"Workspace スナップショット名を変更しました: {renamed.Trim()}");
        return true;
    }
    private bool DeleteWorkspaceSnapshot(IWin32Window owner, WorkspaceSnapshotEntry entry)
    {
        if (_workspaceSnapshotStorage == null)
        {
            MessageBox.Show(owner, "Workspace スナップショットの保存先を初期化できません。", "Workspace スナップショット削除", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        DialogResult confirm = MessageBox.Show(
            owner,
            $"次のスナップショットを削除します。\n\n{entry.Name}",
            "Workspace スナップショット削除",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return false;
        }
        if (!_workspaceSnapshotStorage.DeleteSnapshot(entry.SnapshotId))
        {
            MessageBox.Show(owner, "スナップショットを削除できませんでした。", "Workspace スナップショット削除", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"Workspace スナップショットを削除しました: {entry.Name}");
        return true;
    }
    private bool ExportWorkspaceSnapshot(IWin32Window owner, WorkspaceSnapshotEntry entry)
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "標準機能（推奨）では Workspace Snapshot エクスポートは無効です。"))
        {
            return false;
        }
        if (_workspaceSnapshotStorage == null) return false;
        if (!_workspaceSnapshotStorage.TryGetSnapshotPayload(entry.SnapshotId, out string? payloadJson, out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "エクスポート失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        using var sfd = new SaveFileDialog
        {
            Title = "Workspace スナップショットをエクスポート",
            Filter = "MidFD Workspace Snapshot (*.midfd-workspace-snapshot.json)|*.midfd-workspace-snapshot.json|JSON files (*.json)|*.json",
            FileName = $"{entry.Name}.midfd-workspace-snapshot.json"
        };
        if (sfd.ShowDialog(owner) != DialogResult.OK) return false;
        try
        {
            var exportFile = new WorkspaceSnapshotExportFile
            {
                Metadata = new WorkspaceSnapshotMetadata
                {
                    Name = entry.Name,
                    CreatedAtUtc = entry.CreatedAtUtc,
                    UpdatedAtUtc = entry.UpdatedAtUtc
                },
                Payload = JsonSerializer.Deserialize<WorkspaceState>(payloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            string json = JsonSerializer.Serialize(exportFile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.WriteAllText(sfd.FileName, json);
            ShowStatusMessage($"スナップショットをエクスポートしました: {Path.GetFileName(sfd.FileName)}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"エクスポートに失敗しました。\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
    private bool ImportWorkspaceSnapshot(IWin32Window owner)
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "標準機能（推奨）では Workspace Snapshot インポートは無効です。"))
        {
            return false;
        }
        if (_workspaceSnapshotStorage == null) return false;
        using var ofd = new OpenFileDialog
        {
            Title = "Workspace スナップショットをインポート",
            Filter = "MidFD Workspace Snapshot (*.midfd-workspace-snapshot.json;*.json)|*.midfd-workspace-snapshot.json;*.json"
        };
        if (ofd.ShowDialog(owner) != DialogResult.OK) return false;
        try
        {
            string json = File.ReadAllText(ofd.FileName);
            var importFile = JsonSerializer.Deserialize<WorkspaceSnapshotExportFile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (importFile?.Kind != "MidFD.WorkspaceSnapshot" || importFile.Payload == null)
            {
                MessageBox.Show(owner, "無効なスナップショットファイルです。", "インポート失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string name = importFile.Metadata?.Name ?? Path.GetFileNameWithoutExtension(ofd.FileName);
            if (_workspaceSnapshotStorage.ExistsByName(name))
            {
                name = $"{name} (imported {DateTime.Now:yyyy-MM-dd HH-mm})";
            }
            if (!_workspaceSnapshotStorage.TrySaveSnapshot(name, importFile.Payload, out string errorMessage))
            {
                MessageBox.Show(owner, errorMessage, "インポート失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            ShowStatusMessage($"スナップショットをインポートしました: {name}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"インポートに失敗しました。\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
    private bool ExportAllWorkspaceSnapshots(IWin32Window owner)
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "標準機能（推奨）では Workspace Snapshot 一括エクスポートは無効です。"))
        {
            return false;
        }
        if (_workspaceSnapshotStorage == null) return false;
        var all = _workspaceSnapshotStorage.LoadAllSnapshotsWithPayload();
        if (all.Count == 0)
        {
            MessageBox.Show(owner, "エクスポートするスナップショットがありません。", "一括エクスポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        using var sfd = new SaveFileDialog
        {
            Title = "全 Workspace スナップショットを一括エクスポート",
            Filter = "MidFD Workspace Snapshot Backup (*.midfd-workspace-backupset.json)|*.midfd-workspace-backupset.json|JSON files (*.json)|*.json",
            FileName = $"MidFD_Workspace_Snapshots_Backup_{DateTime.Now:yyyyMMdd_HHmm}.midfd-workspace-backupset.json"
        };
        if (sfd.ShowDialog(owner) != DialogResult.OK) return false;
        try
        {
            var backupSet = new WorkspaceSnapshotBackupSetFile();
            foreach (var (entry, payloadJson) in all)
            {
                backupSet.Snapshots.Add(new WorkspaceSnapshotExportFile
                {
                    Metadata = new WorkspaceSnapshotMetadata
                    {
                        Name = entry.Name,
                        CreatedAtUtc = entry.CreatedAtUtc,
                        UpdatedAtUtc = entry.UpdatedAtUtc
                    },
                    Payload = JsonSerializer.Deserialize<WorkspaceState>(payloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                });
            }
            string json = JsonSerializer.Serialize(backupSet, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.WriteAllText(sfd.FileName, json);
            ShowStatusMessage($"全 {all.Count} 件のスナップショットを一括エクスポートしました。");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"一括エクスポートに失敗しました。\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
    private bool ImportAllWorkspaceSnapshots(IWin32Window owner)
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "標準機能（推奨）では Workspace Snapshot 一括インポートは無効です。"))
        {
            return false;
        }
        if (_workspaceSnapshotStorage == null) return false;
        using var ofd = new OpenFileDialog
        {
            Title = "全 Workspace スナップショットを一括インポート",
            Filter = "MidFD Workspace Snapshot Backup (*.midfd-workspace-backupset.json;*.json)|*.midfd-workspace-backupset.json;*.json"
        };
        if (ofd.ShowDialog(owner) != DialogResult.OK) return false;
        try
        {
            string json = File.ReadAllText(ofd.FileName);
            var backupSet = JsonSerializer.Deserialize<WorkspaceSnapshotBackupSetFile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (backupSet?.Kind != "MidFD.WorkspaceSnapshotBackupSet" || backupSet.Snapshots == null)
            {
                MessageBox.Show(owner, "無効なバックアップセットファイルです。", "一括インポート失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (backupSet.Snapshots.Count == 0)
            {
                MessageBox.Show(owner, "インポートするスナップショットが含まれていません。", "一括インポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            int successCount = 0;
            foreach (var snapshotFile in backupSet.Snapshots)
            {
                if (snapshotFile.Payload == null) continue;
                string name = snapshotFile.Metadata?.Name ?? "Imported Snapshot";
                if (_workspaceSnapshotStorage.ExistsByName(name))
                {
                    name = $"{name} (backup {DateTime.Now:yyyy-MM-dd HH-mm})";
                }
                if (_workspaceSnapshotStorage.TrySaveSnapshot(name, snapshotFile.Payload, out _))
                {
                    successCount++;
                }
            }
            ShowStatusMessage($"{successCount} 件のスナップショットを一括インポートしました。");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"一括インポートに失敗しました。\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
    private void ExecuteQuickAccess()
    {
        HideCommandHintOverlay("ExecuteQuickAccess");
        var diagnostics = new QuickAccessOpenDiagnostics(QuickAccessOpenDiagnostics.CreateOperationId());
        diagnostics.LogOpenStart(_navigationService.CurrentPath, _quickAccessStore);
        IReadOnlyList<string> backHistory = _navigationService.GetBackHistorySnapshot();
        IReadOnlyList<string> forwardHistory = _navigationService.GetForwardHistorySnapshot();
        IReadOnlyList<QuickAccessEntry> historyEntries = diagnostics.MeasureStep(
            "QuickAccess.BuildHistory",
            () => QuickAccessService.BuildHistoryEntries(backHistory, forwardHistory),
            entries => $"itemCount={entries.Count} backCount={backHistory.Count} forwardCount={forwardHistory.Count} success=success");
        var result = QuickAccessDialog.Show(this, _quickAccessStore, _navigationService.CurrentPath, historyEntries, diagnostics);
        if (result.Action == QuickAccessDialogCloseAction.Cancel)
        {
            return;
        }
        if (result.UpdatedStore != null)
        {
            _quickAccessStore = result.UpdatedStore;
            QuickAccessService.Save(_quickAccessStore);
            RefreshAllBrowserTabTitles();
        }
        if (result.Action == QuickAccessDialogCloseAction.SaveOnly)
        {
            ShowStatusMessage("QuickAccess を更新しました。");
            return;
        }
        if (result.SelectedEntry == null) return;
        if (string.IsNullOrWhiteSpace(result.SelectedEntry.Path)) return;
        string resolved = _navigationService.NormalizeDestinationDirectory(result.SelectedEntry.Path);
        try
        {
            ExecuteDirectoryNavigationRequest(
                _browserNavigationCoordinator.CreateDirectoryNavigationRequest(resolved),
                onDirectoryMissing: path => MessageBox.Show($"指定されたパスが見つかりません: {path}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private QuickAccessCommandContext BuildQuickAccessCommandContext()
    {
        ListViewItem? currentItem = _uiMode == UIMode.Browser ? GetCurrentBrowserItem() : null;
        string? currentItemPath = null;
        string? currentItemName = null;
        bool currentItemIsDirectory = false;
        IReadOnlyList<string> markedPaths = _markedFiles
            .Snapshot()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (currentItem != null && currentItem.Text != "..")
        {
            currentItemPath = currentItem.Tag as string;
            if (!string.IsNullOrWhiteSpace(currentItemPath))
            {
                currentItemName = Path.GetFileName(currentItemPath);
                currentItemIsDirectory = Directory.Exists(currentItemPath);
            }
        }
        return new QuickAccessCommandContext
        {
            CurrentPath = _navigationService.CurrentPath,
            CurrentItemPath = currentItemPath,
            CurrentItemName = currentItemName,
            CurrentItemIsDirectory = currentItemIsDirectory,
            MarkedPaths = markedPaths
        };
    }
    private void ExecuteHistoryBack()
    {
        string? target = _navigationService.PeekBack();
        if (target == null)
        {
            ShowStatusMessage("戻る履歴がありません。");
            return;
        }
        string oldPath = _navigationService.CurrentPath;
        ExecuteDirectoryNavigationRequest(
            _browserNavigationCoordinator.CreateDirectoryNavigationRequest(target, isHistoryNavigation: true),
            onNavigationSucceeded: () =>
            {
                _navigationService.CommitBack(oldPath);
                CaptureActiveBrowserTabState();
            });
    }
    private void ExecuteHistoryForward()
    {
        string? target = _navigationService.PeekForward();
        if (target == null)
        {
            ShowStatusMessage("進む履歴がありません。");
            return;
        }
        string oldPath = _navigationService.CurrentPath;
        ExecuteDirectoryNavigationRequest(
            _browserNavigationCoordinator.CreateDirectoryNavigationRequest(target, isHistoryNavigation: true),
            onNavigationSucceeded: () =>
            {
                _navigationService.CommitForward(oldPath);
                CaptureActiveBrowserTabState();
            });
    }
    private bool ExecuteDirectoryNavigationRequest(
        BrowserNavigationCoordinator.DirectoryNavigationRequest? request,
        Action? onNavigationSucceeded = null,
        Action<string>? onDirectoryMissing = null)
    {
        return _browserNavigationCoordinator.Execute(
            request,
            new BrowserNavigationCoordinator.ExecutionContext
            {
                PrepareUnlockedTabForLocationChange = PrepareUnlockedTabForLocationChange,
                LoadDirectory = LoadDirectory,
                OnNavigationSucceeded = onNavigationSucceeded,
                OnDirectoryMissing = onDirectoryMissing
            });
    }
    private void HandleFuncKeyClick(int index)
    {
        // Phase 3-input-alias1: ExecuteFunctionKey 内部で UIMode 判定と GuardClipboardBusy を行う
        // index 0=F1, 1=F2, ... 11=F12
        ExecuteFunctionKey(index + 1);
    }
    private void ApplyFontSettings()
    {
        if (_settings.Fonts == null) return;
        LogFontRouteDiag("ApplyFontSettings:START");
        // Phase 2f-fix2: レイアウト遷移中の中間描画を抑制する
        this.SuspendLayout();
        try
        {
            // ファイラー用
            var filerFamily = _settings.Fonts.FileListFontFamily;
            var filerSize = _settings.Fonts.FileListFontSize;
            var filerFont = new Font(filerFamily, filerSize);
            fileListView.Font = filerFont;
            browserPanel.Font = filerFont;
            if (_browserTabStrip != null)
            {
                _browserTabStrip.Font = new Font("Consolas", _settings.BrowserTabs?.TabFontSize ?? BrowserTabSettings.DefaultTabFontSize, FontStyle.Regular, GraphicsUnit.Point);
            }
            // 重要行 (FileListFontSize を反映)
            var filerInfoFont = new Font(filerFamily, filerSize);
            _headerPaintFont = filerInfoFont; // Phase 2g-fix3a: Paint 向けに保持
            Font headerStatusFont = ResolveAdaptiveHeaderStatusFont(filerInfoFont);
            Font? previousResponsiveOwnedFont = _headerStatusResponsiveOwnedFont;
            if (ReferenceEquals(headerStatusFont, filerInfoFont))
            {
                _headerStatusResponsiveOwnedFont = null;
            }
            else
            {
                _headerStatusResponsiveOwnedFont = headerStatusFont;
            }
            // Px1 diag: adaptive font result
            LogFontRouteDiag($"ApplyFontSettings:AfterResolve baseSize={filerInfoFont.Size:0.##} resultSize={headerStatusFont.Size:0.##} panelW={headerPanel?.ClientSize.Width ?? -1}");
            // 高さをフォントに合わせて動的に調整
            var functionBarMetrics = HeaderLayoutHelper.CalculateMetrics(filerInfoFont, 4);
            sepBeforeTopPanel.Height = 1;
            sepBeforeTopPanel.Visible = true;
            infoRow2Panel.Visible = true;
            sepAfterRow2.Height = 0;
            sepAfterRow2.Visible = false;
            infoRow3Panel.Height = 0;
            infoRow3Panel.Visible = false;
            sepAfterRow3.Height = 0;
            sepAfterRow3.Visible = false;
            infoRow4Panel.Visible = true;
            sepAfterRow4.Height = 1;
            sepAfterRow4.Visible = true;
            _functionBarPreferredHeight = functionBarMetrics.RowHeight;
            functionBarPanel.Height = functionBarMetrics.RowHeight;
            // Phase 5-ui-layout-fix2: BringToFront ハックは Dock 順が正しければ不要なため削除
            foreach (var lbl in lblFuncKeys)
            {
                lbl.Font = filerInfoFont;
            }
            ApplyResolvedHeaderStatusFontForCurrentWindow(filerInfoFont, headerStatusFont, "ApplyFontSettings:initial");
            LogHeaderResponsiveStabilizeDiag(
                "Apply",
                "ApplyFontSettings:initial",
                headerStatusFont,
                GetCurrentHeaderRow1FitMetrics(headerStatusFont),
                fontDisposeSuppressed: previousResponsiveOwnedFont != null && !ReferenceEquals(previousResponsiveOwnedFont, headerStatusFont));
            SynchronizeMenuStripFontAndLayout(CreateMenuStripFont());
            LogMenuStripLayoutMetrics("ApplyFontSettings");
            // Phase 2g-fix4a: 配色の適用 (定数化)
            ApplyColorSettings();
            // ビューア用
            var viewerFamily = _settings.Fonts.ViewerFontFamily;
            var viewerSize = _settings.Fonts.ViewerFontSize;
            var viewerFont = new Font(viewerFamily, viewerSize);
            viewerTextBox.Font = viewerFont;
            viewerMessageLabel.Font = viewerFont;
            if (_largeFileControl != null)
            {
                _largeFileControl.Font = viewerFont;
            }
            // Phase 2f-fix2: レイアウト確定前にテキストの値を最新化しておく
            LogFontRouteDiag("ApplyFontSettings:BeforeUpdateInfoPanel");
            LogHeaderRightDiag("ApplyFontSettings");
            LogFontRouteDiag("ApplyFontSettings:END");
            // Phase 3-bottom-funcbar-fontsync-fix2: 表示の確実な復帰 (BringToFront は overlay の原因になるため削除)
            LayoutFunctionBar();
            functionBarPanel.Invalidate();
            NormalizeStatusLabelLayout();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ApplyFontSettings error: {ex.Message}");
        }
        finally
        {
            // レイアウトを一括適用
            this.ResumeLayout(true);
            this.PerformLayout();
        }
        // Phase 2f-fix2: 最後に明示的な再描画を要求
        contentFramePanel.Invalidate();
        titleHeaderPanel.Invalidate();
        headerPanel.Invalidate();
        topPanel.Invalidate();
        // Phase 2g-fix4b: Row 2 ゾーンも再描画
        headerZone1.Invalidate();
        headerZone2.Invalidate();
        headerZone3.Invalidate();
        headerZone4.Invalidate();
        RecomputeHeaderStatusResponsiveFontNow("ApplyFontSettings:post-layout");
    }
    /// <summary>
    /// UTF-8 マルチバイト文字の途中で切断されない安全な長さを取得する（バッファ末尾の切り出し境界用）。
    /// </summary>
    private static string NormalizeNewlinesForViewerTextBox(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = text.Replace("\r\n", "\n");
        text = text.Replace('\r', '\n');
        return text.Replace("\n", Environment.NewLine);
    }

    private int GetSafeUtf8Length(byte[] buffer, int length)
    {
        if (length <= 0) return 0;
        // UTF-8 マルチバイトの末尾は最大3バイトまで不完全な可能性がある (4バイト文字の場合)
        // 末尾から最大3バイト遡り、マルチバイト開始バイト (11xxxxxx) を探す
        for (int i = 1; i <= Math.Min(length, 3); i++)
        {
            byte b = buffer[length - i];
            if ((b & 0x80) == 0) return length; // ASCII (0xxxxxxx) なら問題なし
            if ((b & 0xC0) == 0xC0) // マルチバイト開始点 (11xxxxxx)
            {
                int expected;
                if ((b & 0xE0) == 0xC0) expected = 2; // 2バイト形式
                else if ((b & 0xF0) == 0xE0) expected = 3; // 3バイト形式
                else if ((b & 0xF8) == 0xF0) expected = 4; // 4バイト形式
                else return length; // 不明な形式
                // 期待される長さに対して現在のバッファ（i バイト分）が足りなければ、その文字の直前までを有効とする
                return (i < expected) ? (length - i) : length;
            }
            // 継続バイト (10xxxxxx) の場合はさらに前のバイトを確認する
        }
        return length;
    }
    /// <summary>
    /// Phase 2g-fix2: Row 2 の 4 つの Zone (headerZone1..4) の幅を、
    /// 現在のフォントと文字列長に基づいて動的に計算・配分する。
    /// </summary>
    private void LayoutHeaderZones()
    {
        if (headerZone1 == null || headerZone2 == null || headerZone3 == null || headerZone4 == null) return;
        if (!this.IsHandleCreated) return;
        // Px1 diag: LayoutHeaderZones:START log は clock tick毎秒呼出しで大量出力になるため削除
        if (lblClock != null && !lblClock.IsDisposed)
        {
            lblClock.AutoSize = false;
            lblClock.Width = GetHeaderClockReservedWidth(lblClock.Font);
        }

        Font clockFont = lblClock?.Font ?? lblPage.Font;
        HeaderRow1FitMetrics row1FitMetrics = GetHeaderRow1FitMetrics(
            clockFont,
            headerPanel.ClientSize.Width,
            lblPage.Text,
            lblTotal.Text,
            lblUsed.Text,
            lblFree.Text,
            lblClock?.Text ?? string.Empty);
        int zoneAvailableWidth = row1FitMetrics.AvailableLeftWidth;
        var widths = HeaderLayoutHelper.CalculateMeasuredZoneWidths(
            zoneAvailableWidth,
            lblPage.Font,
            lblPage.Text,
            lblTotal.Text,
            lblUsed.Text,
            lblFree.Text,
            lblPage,
            lblTotal,
            lblUsed,
            lblFree,
            HeaderRow2ClockSafetyGap
        );
        headerZone1.Width = widths.Zone1;
        headerZone2.Width = widths.Zone2;
        headerZone3.Width = widths.Zone3;
        headerZone4.Width = widths.Zone4;
        int minimumFormWidth = Math.Max(MinimumNormalWindowWidth, widths.MinimumFormWidth);
        if (this.MinimumSize.Width != minimumFormWidth)
        {
            LogService.Info($"[WindowFloorHitIntercept] MinimumSize width audit: {this.MinimumSize.Width} -> {minimumFormWidth}");
            this.MinimumSize = new Size(minimumFormWidth, this.MinimumSize.Height);
        }
        LogHeaderRow2LayoutDiagnostics(row1FitMetrics.ClockReservedWidth, zoneAvailableWidth, widths);
        // Px1 diag: LayoutHeaderZones:END log は clock tick毎秒呼出しで大量出力になるため削除
    }
    /// <summary>
    /// Phase 34A: ヘッダラベルの配置を動的に計算する。
    /// Phase 34E: separator panel (sepAfterRow1, sepAfterRow4) の配置もここで行う。
    /// Phase 36Z: titleHeaderPanel 等の構造変化に追従。
    /// </summary>
    private void PositionHeaderLabels()
    {
        // 2段目 (headerPanel) の配置責務
        // Phase 2g-fix2: LayoutHeaderZones() により Zone 幅が動的に管理されるため、
        // ここでの個別ラベル Location 操作は行いません。
        LayoutHeaderZones();
    }
    private const TextFormatFlags HeaderTextDrawFlags =
        TextFormatFlags.NoPrefix |
        TextFormatFlags.NoPadding |
        TextFormatFlags.SingleLine |
        TextFormatFlags.Top;
    /// <summary>
    /// Phase 2g-fix5: タイトルと時計の描画予定矩形を計算する共通ヘルパー。
    /// contentFramePanel_Paint (枠線抜き) と titleHeaderPanel_Paint (文字描画) で共有。
    /// </summary>
    private void GetHeaderTitleAndClockBounds(Panel panel, out Rectangle titleRect, out Rectangle clockRect)
    {
        Font font = _headerPaintFont ?? SystemFonts.DefaultFont;
        using (var g = panel.CreateGraphics())
        {
            // タイトル (中央)
            string titleStr = lblTitle.Text;
            var titleSize = TextRenderer.MeasureText(g, titleStr, font, new Size(int.MaxValue, int.MaxValue), HeaderTextDrawFlags);
            int titleX = (panel.Width - titleSize.Width) / 2;
            titleRect = new Rectangle(titleX, 0, titleSize.Width, panel.Height);
            // 時計 (右端 10px)
            string clockStr = lblClock.Text;
            var clockSize = TextRenderer.MeasureText(g, clockStr, font, new Size(int.MaxValue, int.MaxValue), HeaderTextDrawFlags);
            int clockX = panel.Width - clockSize.Width - 10;
            clockRect = new Rectangle(clockX, 0, clockSize.Width, panel.Height);
        }
    }
    private void HeaderZone_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel zone) return;
        e.Graphics.Clear(zone.BackColor);
        Label? lbl = null;
        if (zone == headerZone1) lbl = lblPage;
        else if (zone == headerZone2) lbl = lblTotal;
        else if (zone == headerZone3) lbl = lblUsed;
        else if (zone == headerZone4) lbl = lblFree;
        if (lbl == null) return;
        DrawRow2ZoneText(e.Graphics, zone, lbl, lbl.Font);
    }
    /// <summary>
    /// Phase 2g-fix4b: 指定されたラベルのテキストを ":" で分割し、配色を変えて描画する。
    /// </summary>
    private void DrawRow2ZoneText(Graphics g, Panel zone, Label lbl, Font font)
    {
        var headerColors = HeaderColorPaletteResolver.Resolve(_settings.Appearance);
        string text = lbl.Text;
        int colonIndex = text.IndexOf(':');
        if (colonIndex < 0)
        {
            // フォールバック: 単色描画 (セパレータがない場合)
            TextRenderer.DrawText(g, text, font, zone.ClientRectangle, headerColors.HeaderRow2Fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            return;
        }
        string heading = text.Substring(0, colonIndex + 1); // "Page:"
        string value = text.Substring(colonIndex + 1);     // " 1/ 1"
        // 見出しの幅を計測 (TextRendererを使用して描画位置を正確に合わせる)
        Size headingSize = TextRenderer.MeasureText(g, heading, font, Size.Empty, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        // 見出しの描画
        Rectangle headingRect = new Rectangle(0, 0, headingSize.Width, zone.Height);
        TextRenderer.DrawText(g, heading, font, headingRect, headerColors.HeaderRow2Fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        // 値の描画 (見出しの直後から)
        Rectangle valueRect = new Rectangle(headingSize.Width, 0, zone.Width - headingSize.Width, zone.Height);
        TextRenderer.DrawText(g, value, font, valueRect, headerColors.HeaderRow2Value,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
    /// <summary>
    /// Phase 2g-fix3b: 対象コントロールの DoubleBuffered プロパティを反射を用いて有効化する。
    /// </summary>
    private void EnableDoubleBuffering(Control control)
    {
        var prop = typeof(Control).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        prop?.SetValue(control, true);
    }
    private void lblPath_Click(object sender, EventArgs e)
    {
        OpenBrowserPathEntry();
    }
    private void RestoreSelectionState(string? focusTargetName, int lastIndex, bool isReload)
    {
        if (fileListView.Items.Count == 0)
        {
            _browserCursorIndex = 0;
            return;
        }
        ListViewItem targetItem = fileListView.Items[0];
        bool found = false;
        // 1. 名前による探索
        if (!string.IsNullOrEmpty(focusTargetName))
        {
            foreach (ListViewItem item in fileListView.Items)
            {
                if (GetItemFullName(item).Equals(focusTargetName, StringComparison.OrdinalIgnoreCase))
                {
                    targetItem = item;
                    found = true;
                    break;
                }
            }
        }
        // 2. 名前が見つからない場合のインデックスベース fallback (Reload時のみ)
        if (isReload && !found)
        {
            int safeIndex = Math.Clamp(lastIndex, 0, fileListView.Items.Count - 1);
            targetItem = fileListView.Items[safeIndex];
        }
        // UI 反映
        targetItem.Selected = true;
        if (CanRestoreBrowserFocusAfterFileOperation())
        {
            targetItem.Focused = true;
            targetItem.EnsureVisible();
        }
        _browserCursorIndex = _browserPageStartIndex + targetItem.Index;
    }
    /// <summary>
    /// Phase: header declutter - 構造のワンタイム初期化。
    /// 親子関係、Dock、初期の可視性をここで確定させる。
    /// </summary>
    private void InitializeHeaderDeclutterLayout()
    {
        // 1. infoRow3Panel の廃止
        infoRow3Panel.Visible = false;
        infoRow3Panel.Height = 0;
        // 2. ラベルの再配置 (Reparenting)
        // Row 2 (Path行)
        if (lblSort.Parent != infoRow2Panel)
        {
            lblSort.Parent = infoRow2Panel;
        }
        lblSort.Dock = DockStyle.Right;
        lblSort.TextAlign = ContentAlignment.MiddleRight;
        lblSort.AutoSize = false;
        lblSort.AutoEllipsis = false;
        lblSort.Padding = Padding.Empty;
        lblSort.Margin = Padding.Empty;
        lblPath.AutoSize = false;
        lblPath.AutoEllipsis = true;
        lblPath.Dock = DockStyle.Fill;
        if (_breadcrumbPathControl == null)
        {
            _breadcrumbPathControl = new BreadcrumbPathControl
            {
                Dock = DockStyle.Fill,
                Visible = false,
                Font = lblPath.Font,
                ForeColor = lblPath.ForeColor
            };
            _breadcrumbPathControl.PathSelected += (_, path) =>
            {
                if (!string.Equals(path, _navigationService.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    NavigateToLocationDirectory(path);
                }
            };
            _breadcrumbPathControl.BackgroundSelected += (_, _) => OpenBrowserPathEntry();
            infoRow2Panel.Controls.Add(_breadcrumbPathControl);
            WireHeaderGestureControl(_breadcrumbPathControl);
        }
        if (lblFileStatsEx.Parent != infoRow4Panel)
        {
            lblFileStatsEx.Parent = infoRow4Panel;
        }
        lblFileStatsEx.Dock = DockStyle.Right;
        lblFileStatsEx.TextAlign = ContentAlignment.MiddleRight;
        lblFileStatsEx.AutoSize = false;
        lblFileStatsEx.AutoEllipsis = false;
        lblFileStatsEx.Padding = Padding.Empty;
        lblFileStatsEx.Margin = Padding.Empty;
        lblName.AutoSize = false;
        lblName.AutoEllipsis = true;
        lblName.Dock = DockStyle.Fill;
        lblName.TextAlign = ContentAlignment.MiddleLeft;
        // Row 4 (Name行) - 未使用の旧ラベルは非表示・Dock解除
        lblItemAttr.Visible = false;
        lblItemAttr.Dock = DockStyle.None;
        lblFileDate.Visible = false;
        lblFileDate.Dock = DockStyle.None;
        lblFileStats.Visible = false;
        lblFileStats.Dock = DockStyle.None;
        // 3. 重なり順 (Z-Order) の確定
        // Row 2 の右端からの並び: Mark -> Sort
        lblSort.BringToFront();
        lblFileStatsEx.BringToFront();
        // Fill コントロールを背面へ (残りの領域を占有)
        lblPath.SendToBack();
        _breadcrumbPathControl?.BringToFront();
        lblName.SendToBack();
        this.PerformLayout();
    }

    private void ApplyPathDisplayMode()
    {
        if (_browserPathEntryTextBox?.Visible == true)
        {
            lblPath.Visible = false;
            if (_breadcrumbPathControl != null)
            {
                _breadcrumbPathControl.Visible = false;
            }
            return;
        }

        bool showBreadcrumb = _settings.Appearance?.ShowPathAsBreadcrumb == true;
        lblPath.Visible = !showBreadcrumb;
        if (_breadcrumbPathControl != null)
        {
            _breadcrumbPathControl.Font = lblPath.Font;
            _breadcrumbPathControl.ForeColor = lblPath.ForeColor;
            _breadcrumbPathControl.SetPath(_navigationService.CurrentPath);
            _breadcrumbPathControl.Visible = showBreadcrumb;
            if (showBreadcrumb)
            {
                _breadcrumbPathControl.BringToFront();
            }
        }
    }
    /// <summary>
    /// Px1 header/status font application route diagnostic helper.
    /// app.log へ出力する。
    /// </summary>
    private void LogFontRouteDiag(string eventName, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (!HeaderStatusFontRouteDiagnosticLoggingEnabled)
        {
            return;
        }

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[FontRouteDiag] t={Environment.TickCount64} event={eventName} caller={caller}");
            sb.Append($" clientW={this.ClientSize.Width} clientH={this.ClientSize.Height}");
            if (headerPanel != null && !headerPanel.IsDisposed)
                sb.Append($" headerPanelW={headerPanel.ClientSize.Width}");
            if (fileListView != null && !fileListView.IsDisposed && fileListView.Font != null)
                sb.Append($" filerFont={fileListView.Font.Name}/{fileListView.Font.Size:0.##}/{fileListView.Font.Height}");
            if (browserPanel != null && !browserPanel.IsDisposed && browserPanel.Font != null)
                sb.Append($" browserFont={browserPanel.Font.Name}/{browserPanel.Font.Size:0.##}");
            sb.Append($" headerPaintFont={(_headerPaintFont != null ? $"{_headerPaintFont.Size:0.##}/{_headerPaintFont.Height}" : "null")}");
            if (lblPage?.Font != null) sb.Append($" lblPage.FontSz={lblPage.Font.Size:0.##}");
            if (lblTotal?.Font != null) sb.Append($" lblTotal.FontSz={lblTotal.Font.Size:0.##}");
            if (lblUsed?.Font != null) sb.Append($" lblUsed.FontSz={lblUsed.Font.Size:0.##}");
            if (lblFree?.Font != null) sb.Append($" lblFree.FontSz={lblFree.Font.Size:0.##}");
            if (lblClock?.Font != null) sb.Append($" lblClock.FontSz={lblClock.Font.Size:0.##}");
            if (lblSort?.Font != null) sb.Append($" lblSort.FontSz={lblSort.Font.Size:0.##}");
            if (lblFileStatsEx?.Font != null) sb.Append($" lblFileStatsEx.FontSz={lblFileStatsEx.Font.Size:0.##}");
            if (statusLabel?.Font != null) sb.Append($" statusLabel.FontSz={statusLabel.Font.Size:0.##}");
            sb.Append($" funcBarH={functionBarPanel?.Height ?? -1} funcBarPrefH={_functionBarPreferredHeight}");
            sb.Append($" listFontSz={_settings?.Fonts?.FileListFontSize ?? -1}");
            LogService.Info(sb.ToString());
        }
        catch { /* diagnostic should not throw */ }
    }

    /// <summary>
    /// Px1 header/status font route diagnostic: ResolveAdaptiveHeaderStatusFont の入出力をログする。
    /// </summary>
    private void LogAdaptiveFontDiag(
        string tag,
        Font baseFont,
        float availableWidth,
        float bestSize,
        bool fitFound,
        float minSize = -1,
        float maxSize = -1,
        float ratioTarget = -1,
        float widthRatio = -1,   // 旧称。widthScale対応のため引数名はそのまま維持
        string? pageText = null,
        string? clockText = null)
    {
        if (!HeaderStatusFontRouteDiagnosticLoggingEnabled)
        {
            return;
        }

        LogService.Info(
            $"[AdaptiveFontDiag] {tag} availableW={availableWidth} baseSize={baseFont.Size:0.##} bestSize={bestSize:0.##} fitFound={fitFound} " +
            $"minSize={minSize:0.##} maxSize={maxSize:0.##} ratioTarget={ratioTarget:0.##} widthRatio={widthRatio:0.###} " +
            $"pageChars={pageText?.Length ?? 0} clockChars={clockText?.Length ?? 0}");
    }
    private void LogHeaderResponsiveDiag(
        string eventName,
        string reason,
        Font baseFont,
        Font? resolvedFont,
        bool scheduled = false,
        string? skippedReason = null,
        int? rowWidth = null,
        int? leftRequiredWidth = null,
        int? rightClockWidth = null,
        int? availableLeftWidth = null,
        bool? fitResult = null,
        int? freeMeasuredWidth = null,
        int? clockMeasuredWidth = null,
        int? guardBand = null)
    {
        if (!HeaderStatusFontRouteDiagnosticLoggingEnabled)
        {
            return;
        }

        Size clientSize = ClientSize;
        Size headerClientSize = headerPanel?.ClientSize ?? Size.Empty;
        Font effectiveResolvedFont = resolvedFont ?? lblPage?.Font ?? baseFont;
        Font rowFont = lblPage?.Font ?? effectiveResolvedFont;
        string pageText = lblPage?.Text ?? string.Empty;
        string totalText = lblTotal?.Text ?? string.Empty;
        string usedText = lblUsed?.Text ?? string.Empty;
        string freeText = lblFree?.Text ?? string.Empty;
        int resolvedClockReservedWidth = rightClockWidth
            ?? (lblClock?.Font != null ? GetHeaderClockReservedWidth(lblClock.Font) : GetHeaderClockReservedWidth(effectiveResolvedFont));
        int resolvedLeftRequiredWidth = leftRequiredWidth
            ?? GetHeaderRow2LeftRequiredWidth(
                rowFont,
                pageText,
                totalText,
                usedText,
                freeText);
        int resolvedRowWidth = rowWidth ?? headerClientSize.Width;
        int resolvedGuardBand = guardBand ?? GetHeaderRow1FitGuardPx(rowFont);
        int resolvedAvailableLeftWidth = availableLeftWidth ?? Math.Max(0, resolvedRowWidth - resolvedClockReservedWidth - HeaderRow2ClockSafetyGap - resolvedGuardBand);
        int resolvedTotalRequiredWidth = resolvedLeftRequiredWidth + resolvedClockReservedWidth + HeaderRow2ClockSafetyGap + resolvedGuardBand;
        bool resolvedFitResult = fitResult ?? (resolvedTotalRequiredWidth <= resolvedRowWidth && resolvedLeftRequiredWidth <= resolvedAvailableLeftWidth);
        string clockText = lblClock?.Text ?? string.Empty;
        int resolvedClockMeasuredWidth = clockMeasuredWidth ?? HeaderLayoutHelper.MeasureDisplayWidth(clockText, rowFont);
        int resolvedFreeMeasuredWidth = freeMeasuredWidth ?? HeaderLayoutHelper.MeasureRow2SegmentWidth(rowFont, lblFree?.Text ?? string.Empty, lblFree);
        string markSizeText = HeaderLayoutHelper.ExtractMarkSizeText(lblSort?.Text);
        string snapshot =
            $"{eventName}|{reason}|{clientSize}|{headerClientSize}|{DeviceDpi}|{baseFont.Size:0.##}|{effectiveResolvedFont.Size:0.##}|{resolvedRowWidth}|{resolvedLeftRequiredWidth}|{resolvedClockReservedWidth}|{resolvedGuardBand}|{resolvedAvailableLeftWidth}|{resolvedFitResult}|{scheduled}|{skippedReason}";
        DateTime nowUtc = DateTime.UtcNow;
        if (snapshot == _lastHeaderResponsiveDiagSnapshot && (nowUtc - _lastHeaderResponsiveDiagUtc) < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastHeaderResponsiveDiagSnapshot = snapshot;
        _lastHeaderResponsiveDiagUtc = nowUtc;
        LogService.Info(
            $"[HeaderResponsiveDiag] event={eventName} reason={reason} scheduled={scheduled} skippedReason={skippedReason ?? "-"} " +
            $"ClientSize={clientSize} headerClientSize={headerClientSize} DeviceDpi={DeviceDpi} " +
            $"baseFontSize={baseFont.Size:0.##} resultFontSize={effectiveResolvedFont.Size:0.##} " +
            $"lblPage.FontSz={lblPage?.Font?.Size:0.##} lblClock.FontSz={lblClock?.Font?.Size:0.##} statusLabel.FontSz={statusLabel?.Font?.Size:0.##} " +
            $"rowWidth={resolvedRowWidth} leftRequiredWidth={resolvedLeftRequiredWidth} rightClockWidth={resolvedClockReservedWidth} guardBand={resolvedGuardBand} totalRequiredWidth={resolvedTotalRequiredWidth} availableLeftWidth={resolvedAvailableLeftWidth} fitResult={resolvedFitResult} " +
            $"pageLen={pageText.Length} totalLen={totalText.Length} usedLen={usedText.Length} freeLen={freeText.Length} " +
            $"FreeMeasuredWidth={resolvedFreeMeasuredWidth} clockText='{clockText}' clockMeasuredWidth={resolvedClockMeasuredWidth} MarkSizeText='{markSizeText}'");
    }
    private void LogHeaderResponsiveStabilizeDiag(
        string eventName,
        string reason,
        Font currentFont,
        HeaderRow1FitMetrics? metrics,
        string? skippedReason = null,
        bool fontDisposeSuppressed = false,
        bool exceptionPrevented = false)
    {
        if (!HeaderStatusFontRouteDiagnosticLoggingEnabled)
        {
            return;
        }

        HeaderRow1FitMetrics resolvedMetrics = metrics ?? GetCurrentHeaderRow1FitMetrics(currentFont);
        string snapshot =
            $"{eventName}|{reason}|{ClientSize}|{DeviceDpi}|{currentFont.Size:0.##}|{resolvedMetrics.RowWidth}|{resolvedMetrics.LeftRequiredWidth}|{resolvedMetrics.ClockReservedWidth}|{resolvedMetrics.TotalRequiredWidth}|{resolvedMetrics.Fits}|{skippedReason}|{fontDisposeSuppressed}|{exceptionPrevented}";
        DateTime nowUtc = DateTime.UtcNow;
        if (snapshot == _lastHeaderResponsiveStabilizeDiagSnapshot && (nowUtc - _lastHeaderResponsiveStabilizeDiagUtc) < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastHeaderResponsiveStabilizeDiagSnapshot = snapshot;
        _lastHeaderResponsiveStabilizeDiagUtc = nowUtc;
        LogService.Info(
            $"[HeaderResponsiveStabilizeDiag] event={eventName} reason={reason} ClientSize={ClientSize} DeviceDpi={DeviceDpi} " +
            $"baseFontSize={GetHeaderStatusResponsiveBaseFont().Size:0.##} resolvedFontSize={currentFont.Size:0.##} appliedFontSize={lblPage?.Font?.Size:0.##} " +
            $"rowWidth={resolvedMetrics.RowWidth} leftRequiredWidth={resolvedMetrics.LeftRequiredWidth} clockReservedWidth={resolvedMetrics.ClockReservedWidth} totalRequiredWidth={resolvedMetrics.TotalRequiredWidth} fitResult={resolvedMetrics.Fits} " +
            $"skipReason={skippedReason ?? "-"} fontDisposeSuppressed={fontDisposeSuppressed} exceptionPrevented={exceptionPrevented}");
    }
    private void LogHeaderRightDiag(
        string eventName,
        int markCount = -1,
        string? markSizeText = null,
        string? pathRightText = null,
        string? itemRightText = null,
        int clockReservedWidth = -1)
    {
        if (!HeaderStatusFontRouteDiagnosticLoggingEnabled)
        {
            return;
        }

        string currentPathRightText = pathRightText ?? lblSort?.Text ?? string.Empty;
        string currentItemRightText = itemRightText ?? lblFileStatsEx?.Text ?? string.Empty;
        string currentClockText = lblClock?.Text ?? string.Empty;
        Font sortFont = lblSort?.Font ?? SystemFonts.DefaultFont;
        Font clockFont = lblClock?.Font ?? sortFont;
        int pathRightMeasuredWidth = Math.Max(
            HeaderLayoutHelper.MeasureTextWidth(currentPathRightText, sortFont),
            HeaderLayoutHelper.MeasureControlTextWidth(currentPathRightText, sortFont));
        int clockMeasuredWidth = Math.Max(
            HeaderLayoutHelper.MeasureTextWidth(currentClockText, clockFont),
            HeaderLayoutHelper.MeasureControlTextWidth(currentClockText, clockFont));
        int resolvedClockReservedWidth = clockReservedWidth >= 0
            ? clockReservedWidth
            : GetHeaderClockReservedWidth(clockFont);
        Rectangle sortBounds = lblSort?.Bounds ?? Rectangle.Empty;
        Rectangle itemBounds = lblFileStatsEx?.Bounds ?? Rectangle.Empty;
        Rectangle clockBounds = lblClock?.Bounds ?? Rectangle.Empty;
        Size infoRow2Size = infoRow2Panel?.ClientSize ?? Size.Empty;
        Size headerSize = headerPanel?.ClientSize ?? Size.Empty;
        float fontSize = sortFont.Size;
        bool markSizeMissing = markCount > 0 && string.IsNullOrWhiteSpace(markSizeText);
        bool markValueClipped = lblSort != null && lblSort.Visible && lblSort.Width < pathRightMeasuredWidth;
        bool clockTimeMissing = !string.IsNullOrWhiteSpace(currentClockText) && !currentClockText.Contains(':');
        bool clockValueClipped = lblClock != null && lblClock.Visible && lblClock.Width < clockMeasuredWidth;
        bool anomaly = markSizeMissing || markValueClipped || clockTimeMissing || clockValueClipped;
        if (eventName == "UpdateTitleHeaderClock" && !anomaly)
        {
            return;
        }

        string snapshot =
            $"{eventName}|{markCount}|{markSizeText}|{currentPathRightText}|{currentItemRightText}|{lblSort?.Width}|{sortBounds}|" +
            $"{currentClockText}|{lblClock?.Width}|{clockBounds}|{resolvedClockReservedWidth}|{infoRow2Size}|{headerSize}|{fontSize:0.##}";
        DateTime nowUtc = DateTime.UtcNow;
        if (!anomaly && snapshot == _lastHeaderRightDiagSnapshot && (nowUtc - _lastHeaderRightDiagUtc) < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastHeaderRightDiagSnapshot = snapshot;
        _lastHeaderRightDiagUtc = nowUtc;
        LogService.Info(
            $"[HeaderRightDiag] event={eventName} MarkCount={markCount} MarkSizeText='{markSizeText ?? "<null>"}' " +
            $"pathRightText='{currentPathRightText}' lblSort.Text='{lblSort?.Text ?? string.Empty}' lblSort.Width={lblSort?.Width ?? -1} lblSort.Bounds={sortBounds} " +
            $"pathRightMeasuredWidth={pathRightMeasuredWidth} itemRightText='{currentItemRightText}' lblFileStatsEx.Text='{lblFileStatsEx?.Text ?? string.Empty}' " +
            $"lblFileStatsEx.Width={lblFileStatsEx?.Width ?? -1} lblFileStatsEx.Bounds={itemBounds} " +
            $"lblClock.Text='{currentClockText}' lblClock.Width={lblClock?.Width ?? -1} lblClock.Bounds={clockBounds} " +
            $"clockMeasuredWidth={clockMeasuredWidth} clockReservedWidth={resolvedClockReservedWidth} " +
            $"infoRow2Panel.ClientSize={infoRow2Size} headerPanel.ClientSize={headerSize} fontSize={fontSize:0.##}");
    }

    private DialogResult ShowDragInCopyConfirmationDialog(string message)
    {
        return ConfirmationDialogPresenter.ShowDragInCopyConfirmationDialog(this, message);
    }

    private DialogResult ShowDragInMoveConfirmationDialog(string message)
    {
        return ConfirmationDialogPresenter.ShowDragInMoveConfirmationDialog(this, message);
    }

    private DialogResult ShowLargeTextClipboardCopyConfirmationDialog(int lineCount, long estimatedBytes)
    {
        return ConfirmationDialogPresenter.ShowLargeTextClipboardCopyConfirmationDialog(this, lineCount, estimatedBytes);
    }

    private Color ResolveStatusColor(StatusKind kind)
    {
        return StatusColorResolver.Resolve(kind, _resolvedColors, _settings?.Appearance);
    }
}
