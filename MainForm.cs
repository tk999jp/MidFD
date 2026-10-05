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
using MidFD.Runtime;
namespace MidFD;
public partial class MainForm : Form, ICommandPaletteHost, IViewerPreviewUiPort, IViewerModeUiPort, IBrowserWorkspaceSnapshotUiPort, IFileOperationUiPort, ICommandApplicationShellPort
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
    private BreadcrumbPathControl? _breadcrumbPathControl;
    private readonly BrowserInputRouter _browserInputRouter = new();
    private readonly BrowserNamePrefixJumpSession _browserNamePrefixJumpSession = new();
    private readonly BrowserMarkInteractionController _browserMarkInteractionController = new();
    private readonly BrowserFileListRenderer _browserFileListRenderer = new();
    private readonly CommandStateCoordinator _commandStateCoordinator = new();
    private CommandStateCoordinator.CommandUiSnapshot _cachedCommandUiSnapshot;
    private bool _isFunctionBarShiftLayerActive;
    private bool _isFunctionBarCtrlLayerActive;
    private bool _isFunctionBarAltLayerActive;
    private readonly FileOperationDialogCoordinator _fileOperationDialogCoordinator = new();
    private readonly RenameDialogCoordinator _renameDialogCoordinator = new();
    private readonly FileOperationUndoRedoService _fileOperationUndoRedoService = new();
    private readonly UnifiedUndoRedoApplicationCoordinator _unifiedUndoRedoCoordinator;
    private readonly SettingsApplicationCoordinator _settingsCoordinator;
    private FileListColorResolver.ResolvedColors? _resolvedColors;
    // Diagnostic logging: unique ID for each selection change
    private static long _selectionIdCounter = 0;
    private FeatureGateService _featureGate = new(FeatureProfile.Full);
    private FileOperationItemProgressState? _fileOperationItemProgressState;
    private FileOperationProgressDialog? _fileOperationProgressDialog;
    private readonly List<ImageViewerForm> _imageViewers = new(); // 起動中の画像ビューア
    private int _hoveredFuncKeyIndex = -1;
    private int _pressedFuncKeyIndex = -1;
    private int _lastColumnCountKey = 0; // WinFD互換モードでの連続押下判定用
    private Controls.LargeFilePreviewControl _largeFileControl = null!;
    private const int LargeTextInitialScanBytes = 512 * 1024;
    private const int LargeTextInitialLineReadBytes = 512 * 1024;
    private const int LargeTextLongLineVisibleReadBytes = 4096;
    private readonly Stopwatch _largeTextEntryStopwatch = new Stopwatch();
    // Browser モード用（多列表示）プロパティ
    private readonly MarkSummaryRebuildCoordinator _markSummaryRebuildCoordinator;
    private readonly MarkSummaryBulkEffectCoordinator _markSummaryBulkEffectCoordinator = new();
    private readonly MarkOperationEffectCoordinator _markOperationEffectCoordinator = new();
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
    private Font? _filterHeaderEmphasisFont;
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
    private bool _browserOutgoingDragInProgress;
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
    private int _functionBarPreferredHeight = 24;
    private int _lastLoggedCommandHintRowCount = -1;
    private Rectangle _lastLoggedCommandHintBounds = Rectangle.Empty;
    private Size _lastLoggedCommandHintPanelSize = Size.Empty;
    private readonly List<ToolStripItem> _browserOnlyMenuItems = new();
    private readonly List<ToolStripItem> _busyAwareMenuItems = new();
    private readonly Dictionary<ToolStripItem, CommandStateCoordinator.MenuItemStateRule> _menuItemRules = new();
    private bool _suppressBrowserTabSelectionChanged;
    private bool _isSwitchingBrowserTab;
    private Panel? _browserTabHostPanel;
    private BrowserTabStrip? _browserTabStrip;
    private BrowserTabNavigation? _browserTabNavigation;
    private WebBrowser? _markdownBrowser;
    private ToolStripStatusLabel? _markdownModeSpacer;
    private ToolStripStatusLabel? _markdownRenderedModeStatusLabel;
    private ToolStripStatusLabel? _markdownRawModeStatusLabel;
    private DataGridView? _delimitedGrid;
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
    private ToolStripMenuItem? _fileDisplayModeNameExtensionAlignedMenuItem;
    private ToolStripMenuItem? _reloadCurrentDirectoryMenuItem;
    private ContextMenuStrip? _browserTabContextMenu;
    private ContextMenuStrip? _browserTabGroupContextMenu;
    private readonly BrowserTabGroupRuntimeState _browserTabGroupRuntimeState = new();
    private readonly Coordinators.BrowserTabUiCoordinator _browserTabUiCoordinator = new();
    private ToolStripMenuItem? _toggleBrowserTabLockContextMenuItem;
    private ToolStripMenuItem? _toggleBrowserTabReadOnlyContextMenuItem;
    private ToolStripMenuItem? _openBrowserTabFilterLockContextMenuItem;
    private ToolStripMenuItem? _clearBrowserTabFilterLockContextMenuItem;
    private ToolStripMenuItem? _saveCurrentWorkspaceSnapshotContextMenuItem;
    private ToolStripSeparator? _workspaceSnapshotContextMenuSeparator;
    private ToolStripMenuItem? _undoBrowserTabContextMenuItem;
    private ToolStripMenuItem? _redoBrowserTabContextMenuItem;
    private ToolStripMenuItem? _closeBrowserTabContextMenuItem;
    private ToolStripMenuItem? _closeRightBrowserTabsContextMenuItem;
    private ToolStripMenuItem? _closeLeftBrowserTabsContextMenuItem;
    private ToolStripMenuItem? _closeOtherBrowserTabsContextMenuItem;
    private ToolStripMenuItem? _createBrowserTabGroupContextMenuItem;
    private ToolStripMenuItem? _addBrowserTabGroupContextMenuItem;
    private ToolStripMenuItem? _removeBrowserTabGroupContextMenuItem;
    private ToolStripMenuItem? _renameBrowserTabGroupContextMenuItem;
    private string? _browserTabContextCategoryId;
    private Guid? _browserTabContextTabId;
    private Guid? _browserTabGroupContextId;
    private ContextMenuStrip? _browserTabCategoryContextMenu;
    private ToolStripMenuItem? _addBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _moveBrowserTabCategoryLeftContextMenuItem;
    private ToolStripMenuItem? _moveBrowserTabCategoryRightContextMenuItem;
    private ToolStripMenuItem? _renameBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _deleteBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _manageBrowserTabCategoriesContextMenuItem;
    private FileSystemWatcher? _currentDirectoryWatcher;
    private bool _suppressBrowserSelectionChanged;
    private readonly BrowserSelectionIdentityGate _browserSelectionIdentityGate = new();
    private BrowserTabStripCategoryItemKind _browserTabCategoryContextKind = BrowserTabStripCategoryItemKind.Category;
    private DateTime _lastBrowserTabLimitBeepUtc = DateTime.MinValue;
    private bool _isClosingFromEscExitPath;
    private bool _isExitConfirmationPending;
    private readonly MainFormIntegrationSeam? _integrationSeam;
    private bool _syncExternalDropWaitActive;
    private bool _syncExternalDropCloseRequested;
    private bool _syncExternalDropCancelRequested;
    private readonly MouseGestureRecognizer _mouseGestureRecognizer = new();
    private readonly List<Point> _mouseGestureTrailPoints = new();
    private bool _isMouseGestureTrailVisible;
    private const int MouseGestureTrailMinDistance = 4;
    private bool _suppressNextBrowserContextMenu;
    private DateTime _suppressBrowserContextMenuUntilUtc = DateTime.MinValue;
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
    private readonly CommandApplicationCoordinator _commandApplicationCoordinator;
    private readonly BrowserApplicationCoordinator _browserApplicationCoordinator;
    private readonly BrowserNavigationWorkflowApplicationCoordinator _browserNavigationWorkflowApplicationCoordinator;
    private readonly BrowserRefreshWorkflowApplicationCoordinator _browserRefreshWorkflowApplicationCoordinator;
    private readonly BrowserWorkspacePersistenceApplicationCoordinator _browserWorkspacePersistenceApplicationCoordinator;
    private readonly BrowserMarkSlotWorkflowApplicationCoordinator _browserMarkSlotWorkflowApplicationCoordinator;
    private readonly BrowserWorkspaceLifecycleApplicationCoordinator _browserWorkspaceLifecycleApplicationCoordinator;
    private readonly BrowserCategoryWorkflowApplicationCoordinator _browserCategoryWorkflowApplicationCoordinator;
    private readonly BrowserTabWorkflowApplicationCoordinator _browserTabWorkflowApplicationCoordinator;
    private readonly BrowserWorkspaceSnapshotApplicationCoordinator _browserWorkspaceSnapshotApplicationCoordinator;
    private readonly ViewerApplicationCoordinator _viewerApplicationCoordinator;
    private readonly ViewerWorkflowApplicationCoordinator _viewerWorkflowApplicationCoordinator;
    private readonly PreviewApplicationCoordinator _previewApplicationCoordinator = new();
    private readonly ViewerPreviewApplicationCoordinator _viewerPreviewApplicationCoordinator;
    private readonly ViewerModeApplicationCoordinator _viewerModeApplicationCoordinator;
    private readonly FileOperationApplicationCoordinator _fileOperationApplicationCoordinator;
    internal bool AuthorToolsEnabled { get; }
    public MainForm(string? startupProfileOverride = null, bool authorToolsEnabled = false)
        : this(startupProfileOverride, authorToolsEnabled, integrationSeam: null)
    {
    }

    internal MainForm(
        string? startupProfileOverride,
        bool authorToolsEnabled,
        MainFormIntegrationSeam? integrationSeam)
    {
        _integrationSeam = integrationSeam;
        var browserState = new BrowserSessionState();
        var viewerState = new ViewerSessionState();
        var fileOperationState = new FileOperationState();
        var settingsState = new SettingsSessionState();
        _settingsCoordinator = new SettingsApplicationCoordinator(settingsState);
        _settingsCoordinator.SetStartupProfileOverride(startupProfileOverride);
        _browserApplicationCoordinator = new BrowserApplicationCoordinator(
            browserState,
            new BrowserDirectoryApplicationCoordinator(browserState));
        _browserRefreshWorkflowApplicationCoordinator =
            new BrowserRefreshWorkflowApplicationCoordinator(_browserApplicationCoordinator);
        _browserWorkspacePersistenceApplicationCoordinator =
            new BrowserWorkspacePersistenceApplicationCoordinator(
                _browserApplicationCoordinator,
                _settingsCoordinator,
                _browserTabGroupRuntimeState);
        _browserWorkspaceLifecycleApplicationCoordinator =
            new BrowserWorkspaceLifecycleApplicationCoordinator(
                _browserApplicationCoordinator,
                _settingsCoordinator,
                _browserWorkspacePersistenceApplicationCoordinator,
                _browserRefreshWorkflowApplicationCoordinator);
        AuthorToolsEnabled = authorToolsEnabled;
        _viewerApplicationCoordinator = new ViewerApplicationCoordinator(viewerState);
        _viewerWorkflowApplicationCoordinator = new ViewerWorkflowApplicationCoordinator(
            viewerState,
            _settingsCoordinator);
        _viewerPreviewApplicationCoordinator = new ViewerPreviewApplicationCoordinator(
            viewerState,
            _previewApplicationCoordinator,
            _viewerWorkflowApplicationCoordinator,
            _settingsCoordinator);
        _viewerModeApplicationCoordinator = new ViewerModeApplicationCoordinator(
            viewerState,
            _viewerWorkflowApplicationCoordinator,
            _viewerPreviewApplicationCoordinator,
            _browserRefreshWorkflowApplicationCoordinator);
        _browserTabWorkflowApplicationCoordinator =
            new BrowserTabWorkflowApplicationCoordinator(
                _browserApplicationCoordinator,
                _settingsCoordinator,
                _browserWorkspaceLifecycleApplicationCoordinator,
                _browserWorkspacePersistenceApplicationCoordinator,
                _browserRefreshWorkflowApplicationCoordinator,
                _viewerModeApplicationCoordinator,
                this);
        _browserMarkSlotWorkflowApplicationCoordinator =
            new BrowserMarkSlotWorkflowApplicationCoordinator(
                _browserApplicationCoordinator,
                _browserTabWorkflowApplicationCoordinator,
                _browserWorkspacePersistenceApplicationCoordinator);
        _browserCategoryWorkflowApplicationCoordinator =
            new BrowserCategoryWorkflowApplicationCoordinator(
                _browserApplicationCoordinator,
                _settingsCoordinator,
                _browserWorkspaceLifecycleApplicationCoordinator,
                _browserWorkspacePersistenceApplicationCoordinator,
                _browserTabWorkflowApplicationCoordinator);
        _browserNavigationWorkflowApplicationCoordinator =
            new BrowserNavigationWorkflowApplicationCoordinator(
                _browserApplicationCoordinator,
                _browserTabWorkflowApplicationCoordinator,
                _browserRefreshWorkflowApplicationCoordinator,
                _settingsCoordinator);
        _browserWorkspaceSnapshotApplicationCoordinator =
            new BrowserWorkspaceSnapshotApplicationCoordinator(
                _browserApplicationCoordinator,
                _settingsCoordinator,
                _browserWorkspacePersistenceApplicationCoordinator,
                _browserWorkspaceLifecycleApplicationCoordinator,
                _browserTabWorkflowApplicationCoordinator,
                _browserNavigationWorkflowApplicationCoordinator);
        _fileOperationApplicationCoordinator = new FileOperationApplicationCoordinator(
            fileOperationState,
            browserState.Selection,
            _settingsCoordinator,
            _fileOperationUndoRedoService,
            _browserApplicationCoordinator,
            viewerState,
            _browserNavigationWorkflowApplicationCoordinator,
            executionProbe: integrationSeam?.ExecutionProbe);
        _unifiedUndoRedoCoordinator = new UnifiedUndoRedoApplicationCoordinator(
            _fileOperationUndoRedoService,
            _browserTabWorkflowApplicationCoordinator);
        _commandApplicationCoordinator = new CommandApplicationCoordinator(
            _browserApplicationCoordinator,
            _browserNavigationWorkflowApplicationCoordinator,
            _browserRefreshWorkflowApplicationCoordinator,
            _browserTabWorkflowApplicationCoordinator,
            _unifiedUndoRedoCoordinator,
            _browserCategoryWorkflowApplicationCoordinator,
            _browserWorkspacePersistenceApplicationCoordinator,
            _viewerApplicationCoordinator,
            _viewerWorkflowApplicationCoordinator,
            _viewerModeApplicationCoordinator,
            this,
            _fileOperationApplicationCoordinator,
            this,
            this);
        _commandDispatcher = new CommandDispatcher(_commandRegistry);
        InitializeCoreWindowChrome();
        _settingsCoordinator.SaveFailed += HandleSettingsSaveFailed;
        InitializeBrowserFileNameToolTip();
        InitializeFunctionBarToolTip();
        _notificationService = new NotificationService(this.statusLabel, this.messageTimer, ResolveStatusColor);
        _markSummaryRebuildCoordinator = new MarkSummaryRebuildCoordinator(
            BuildMarkSummaryAsync,
            () => NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath),
            () => IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath,
            action => BeginInvoke(action),
            ApplyCompletedMarkSummary);
        LoadSettingsAndApplyProfile();
        InitializePersistenceStores();
        InitializeStartupStoresAndHints();
        _browserWorkspacePersistenceApplicationCoordinator.InitializeMarkSlots(MarkSlotCount);
        InitializePreviewAndLargeTextControls();
        InitializeViewerTextBoxEvents();
        InitializeStartupSessionState();
        InitializeRuntimeTimersAndOverlay();
        string startupPath = integrationSeam?.StartupPathOverride ?? ResolveStartupPath();
        InitializeMainUiSurface();
        ShowSettingsRecoveryNoticeIfNeeded();
        RestoreBrowserStartupState(startupPath);
        if (integrationSeam?.InitialStatusText is string initialStatusText)
        {
            _notificationService.SetDefaultMessage(initialStatusText, applyToVisibleMessage: true);
        }
        WireBrowserInputEvents();
        // SettingsForm entry route regression corrective: Ensure KeyDown is wired and KeyPreview is active
        KeyPreview = true;
        KeyDown -= MainForm_KeyDown;
        KeyDown += MainForm_KeyDown;
        KeyPress -= MainForm_KeyPress;
        KeyPress += MainForm_KeyPress;
        WireHeaderAndFunctionBarEvents();
        WireWindowLifecycleEvents();
        // 初期 FunctionBar 表示
        UpdateFunctionBar();
    }
    private void InitializeCoreWindowChrome()
    {
        InitializeComponent();
        EnableDoubleBuffering(browserPanel);
        this.MinimumSize = new Size(MinimumNormalWindowWidth, MinimumNormalWindowHeight);
        statusStrip.ShowItemToolTips = false;
        statusStrip.Dock = DockStyle.Bottom;
        statusStrip.SizingGrip = false;
        statusStrip.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
        statusStrip.RenderMode = ToolStripRenderMode.System;
        _markdownModeSpacer = new ToolStripStatusLabel
        {
            Alignment = ToolStripItemAlignment.Left,
            Spring = true,
            AutoSize = false,
            Visible = false,
            Overflow = ToolStripItemOverflow.Never
        };
        _markdownRenderedModeStatusLabel = CreateMarkdownModeStatusLabel("Rendered", MarkdownViewerMode.Rendered);
        _markdownRawModeStatusLabel = CreateMarkdownModeStatusLabel("Raw", MarkdownViewerMode.Raw);
        statusStrip.Items.Add(_markdownModeSpacer);
        statusStrip.Items.Add(_markdownRenderedModeStatusLabel);
        statusStrip.Items.Add(_markdownRawModeStatusLabel);
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
    private void LoadSettingsAndApplyProfile()
    {
        _settingsCoordinator.ReloadFromStorage(out SettingsManager.SettingsLoadMetadata settingsLoadMetadata);
        ApplyFeatureProfile(settingsLoadMetadata.IsMouseGesturesExplicit);
    }
    private void InitializePersistenceStores()
    {
        _browserWorkspacePersistenceApplicationCoordinator.InitializeStores();
    }
    private void InitializeStartupStoresAndHints()
    {
        _browserWorkspacePersistenceApplicationCoordinator.InitializeWorkspaceSettings();
        LogService.ApplySettings(_settingsCoordinator.Value.Logging);
        MidFDColors.ApplyTheme(FileListColorResolver.NormalizeCoreTheme(_settingsCoordinator.Value.Appearance?.ColorTheme));
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
            if (_viewerApplicationCoordinator.LargeFileState == null)
            {
                return;
            }
            LogService.Info(
                $"[LargeTextFirstPaint] elapsedMs={_largeTextEntryStopwatch.ElapsedMilliseconds} " +
                $"uiMode={_viewerApplicationCoordinator.Mode} kind={_viewerApplicationCoordinator.CurrentKind} " +
                $"path='{_viewerApplicationCoordinator.LargeFileState.FilePath}' " +
                $"offsets={_viewerApplicationCoordinator.LargeFileState.LineOffsets.Count} " +
                $"isIndexing={_viewerApplicationCoordinator.LargeFileState.IsIndexing} " +
                $"status='{statusLabel?.Text ?? "<null>"}'");
        };
        _largeFileControl.CharacterSelectionAutoScrollRequested += (s, direction) =>
        {
            if (_viewerApplicationCoordinator.LargeFileState == null) return;
            int step = Math.Max(1, _largeFileControl.VisibleLineCount / 4);
            int target = _viewerApplicationCoordinator.LargeFileState.FirstVisibleLine + direction * step;
            _ = NavigateLargeFilePreviewAsync(
                target,
                "CharacterSelectionAutoScroll",
                preserveCharacterSelection: true,
                characterSelectionAutoScrollDirection: direction);
        };
        viewerPanel.Controls.Add(_largeFileControl);
        // Viewer 改行モードの復元
        viewerTextBox.WordWrap = _settingsCoordinator.Value.Preview.ViewerWordWrap;
        viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? ScrollBars.Vertical : ScrollBars.Both;
    }
    private void InitializeViewerTextBoxEvents()
    {
        viewerTextBox.KeyUp += (s, e) =>
        {
            if (IsTextOrBinaryViewerActive())
            {
                ApplyViewerStatusLine();
            }
        };
        viewerTextBox.MouseUp += (s, e) =>
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
            _viewerWorkflowApplicationCoordinator.UpdateTextLineCount(viewerTextBox.Lines.Length);
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
            if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer)
            {
                ApplyViewerStatusLine("messageTimer viewer restore");
            }
        };
    }
    private void InitializeStartupSessionState()
    {
        _browserWorkspaceLifecycleApplicationCoordinator.ApplyStartupSessionState();
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
        _directoryRefreshDebounceTimer.Interval = BrowserRefreshConstants.CurrentDirectoryRefreshDebounceMilliseconds;
        _directoryRefreshDebounceTimer.Tick += (_, _) =>
        {
            _directoryRefreshDebounceTimer.Stop();
            BrowserRefreshProcessExecution execution = _browserRefreshWorkflowApplicationCoordinator.ExecuteDelayedPendingRefreshProcessing(
                _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
                IsCurrentDirectoryBusy(),
                _isExitConfirmationPending || _isClosingFromEscExitPath,
                IsDisposed || Disposing,
                CreateDirectoryLoadOptions(),
                _browserApplicationCoordinator.ColumnCount,
                CaptureBrowserRefreshShellState());
            ApplyPendingRefreshExecution(execution, "DebounceTimer");
        };
        _directoryCountAuditTimer.Interval = _browserRefreshWorkflowApplicationCoordinator.GetAuditIntervalMilliseconds(null);
        _directoryCountAuditTimer.Tick += (_, _) => RunCurrentDirectoryCountAudit();
    }
    private string ResolveStartupPath()
    {
        return _browserWorkspaceLifecycleApplicationCoordinator.ResolveStartupPath(
            Environment.GetCommandLineArgs(),
            Environment.CurrentDirectory);
    }
    private void InitializeMainUiSurface()
    {
        // ウィンドウ位置・サイズの復元
        if (SessionRestorePolicy.ShouldRestoreWindowBounds(_settingsCoordinator.Value.Session))
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
        ApplyBrowserTabStripDisplaySettings();
        InitializeMenuStrip();
        LogAltHintContext("InitializeMenuStrip");
    }
    private void RestoreBrowserStartupState(string startupPath)
    {
        BrowserWorkspaceStartupExecution execution =
            _browserWorkspaceLifecycleApplicationCoordinator.ExecuteStartupRestore(
                startupPath,
                CreateDirectoryLoadOptions(),
                _browserApplicationCoordinator.ColumnCount,
                CaptureBrowserRefreshShellState());
        BrowserWorkspaceStartupRestoreResult restoration = execution.Restoration;
        if (execution.DirectoryLoad is { Succeeded: true } load)
        {
            ApplyDirectoryLoadUi(load);
            ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
        }
        else if (execution.DirectoryLoad?.Error is { } error)
        {
            NotifyDirectoryLoadFailure(error);
        }
        if (restoration.LegacyMarkRestore.HasValue)
        {
            ApplyStartupLegacyMarksUi(restoration.LegacyMarkRestore.Value);
        }
        if (restoration.Restored)
        {
            if (restoration.TabLimitApplied)
            {
                ShowStatusMessage($"保存タブが上限を超えたため、{restoration.MaxTabCount} 個まで復元しました。");
            }
            ShowStatusMessage(restoration.SkippedTabCount > 0
                ? $"前回のタブ {restoration.RestoredTabCount} 件を復元しました（{restoration.SkippedTabCount} 件は見つからず除外）。"
                : $"前回のタブ {restoration.RestoredTabCount} 件を復元しました。");
        }
        else if (restoration.RestoreEnabled && restoration.HadSavedTabs)
        {
            ShowStatusMessage("前回のタブは見つからないため、通常の開始状態で開きました。");
        }
        RefreshBrowserTabHeaders();
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
        this.browserPanel.AsyncDragDrop += BrowserPanel_AsyncDragDrop;
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
        this.FormClosing += (s, e) =>
        {
            if (TryHandleSyncExternalDropCloseRequest("MainForm.FormClosing"))
            {
                e.Cancel = true;
                return;
            }

            ClearUnifiedSearchPreviewReturnContext();
            ClearNameSearchInspectionReturnContext();
            CloseUnifiedSearchSession(restoreSource: false, refreshProjection: false);

            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
            _isExitConfirmationPending = true;
            _settingsCoordinator.SaveFailed -= HandleSettingsSaveFailed;
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
            _filterHeaderEmphasisFont?.Dispose();
            _filterHeaderEmphasisFont = null;
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
            _browserApplicationCoordinator.Dispose();
            _viewerApplicationCoordinator.Dispose();
            _fileOperationApplicationCoordinator.Dispose();
        };
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
            if (this.WindowState != FormWindowState.Minimized)
            {
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
        _ = _fileOperationApplicationCoordinator.StartManagedTrashRetentionCleanupAsync("Startup");

        // 初回表示レイアウト完了直後に確実にフォーカスを置く
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser)
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
    private void SaveWindowSettings()
    {
        if (this.WindowState == FormWindowState.Normal || this.WindowState == FormWindowState.Maximized)
        {
            Rectangle candidate = (this.WindowState == FormWindowState.Normal) ? this.Bounds : this.RestoreBounds;
            if (IsSaneNormalBounds(candidate) && !IsRestoreFloorHitCorruption(candidate) && HasUsableClientArea())
            {
                _settingsCoordinator.SetWindowBounds(candidate.X, candidate.Y, candidate.Width, candidate.Height);
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
                    _settingsCoordinator.SetWindowBounds(
                        fallbackBounds.Value.X,
                        fallbackBounds.Value.Y,
                        fallbackBounds.Value.Width,
                        fallbackBounds.Value.Height);
                    LogService.Info($"[WindowRestoreFloorHit] SaveWindowSettings Applied Fallback. TriggerState={this.WindowState} TriggerBounds={FormatBoundsForLog(candidate)} Source={fallbackSource} Bounds={FormatBoundsForLog(fallbackBounds.Value)}");
                }
                else if (IsSaneNormalBounds(new Rectangle(_settingsCoordinator.Value.Window.X, _settingsCoordinator.Value.Window.Y, _settingsCoordinator.Value.Window.Width, _settingsCoordinator.Value.Window.Height)))
                {
                    // Existing settings are still sane, do not overwrite with corrupted values
                    LogService.Info($"[WindowRestoreFloorHit] SaveWindowSettings Skip. Current settings are still sane. TriggerState={this.WindowState} TriggerBounds={FormatBoundsForLog(candidate)}");
                }
                else
                {
                    // Everything is broken, use default safe
                    _settingsCoordinator.SetWindowBounds(-1, -1, 1024, 768);
                    LogService.Info($"[WindowRestoreFloorHit] SaveWindowSettings Fallback DefaultSafe TriggerState={this.WindowState} TriggerBounds={FormatBoundsForLog(candidate)}");
                }
            }
        }
        _settingsCoordinator.SetWindowState((int)((this.WindowState == FormWindowState.Minimized)
            ? FormWindowState.Normal : this.WindowState));
        IReadOnlyList<string>? pendingEscMarks = _isClosingFromEscExitPath
            ? _browserApplicationCoordinator.Selection.PendingEscExitPersistedMarks
            : null;
        BrowserWorkspaceSessionSaveResult sessionSave = _browserWorkspacePersistenceApplicationCoordinator.SaveSession(
            _browserApplicationCoordinator.CurrentPath,
            _browserApplicationCoordinator.ColumnCount,
            _browserApplicationCoordinator.CurrentSort,
            _browserApplicationCoordinator.SortAscending,
            BuildBrowserTabStateFromCurrentUi(),
            pendingEscMarks);
        bool workspaceSaveSucceeded = sessionSave.WorkspaceSaveSucceeded;
        LogService.Info($"[WindowVisibility] SaveWindowSettings State={this.WindowState} Bounds={FormatBoundsForLog(this.Bounds)} RestoreBounds={FormatBoundsForLog(this.RestoreBounds)} Saved=({_settingsCoordinator.Value.Window.X},{_settingsCoordinator.Value.Window.Y},{_settingsCoordinator.Value.Window.Width},{_settingsCoordinator.Value.Window.Height})");
        SettingsSqliteStore.SettingsSaveResult settingsSaveResult = sessionSave.SettingsSaveResult;
        bool markPersistenceSucceeded = sessionSave.MarkPersistenceSucceeded;
        if (markPersistenceSucceeded)
        {
            ClearPendingEscExitMarkPersistence();
        }
        else
        {
            if (!settingsSaveResult.Succeeded)
            {
                HandleSettingsSaveFailed(settingsSaveResult);
            }
        }
        int browserTabsSavedMarkCount = _settingsCoordinator.Value.Session.RestoreTabsOnStartup && settingsSaveResult.Succeeded
            ? sessionSave.MarkPreparation.MarkedPaths.Count
            : 0;
        int workspaceSavedMarkCount = _settingsCoordinator.Value.Session.RestoreTabsOnStartup && workspaceSaveSucceeded
            ? sessionSave.MarkPreparation.MarkedPaths.Count
            : 0;
        MarkPathValidationMetrics validationMetrics = sessionSave.MarkPreparation.ValidationMetrics;
        LogService.Info(
            $"[MarkPersistenceBoundary] source={sessionSave.MarkPreparation.SourceCount} persisted={sessionSave.MarkPreparation.MarkedPaths.Count} " +
            $"pendingEsc={sessionSave.MarkPreparation.UsedPendingEscSnapshot} validation={sessionSave.MarkPreparation.ValidationCount} " +
            $"parentBatchGroups={validationMetrics.ParentBatchGroups} parentBatchPaths={validationMetrics.ParentBatchPaths} " +
            $"parentBatchHits={validationMetrics.ParentBatchHits} parentBatchMisses={validationMetrics.ParentBatchMisses} " +
            $"parentBatchFailures={validationMetrics.ParentBatchFailures} " +
            $"individualFallbackValidations={validationMetrics.IndividualFallbackValidations} " +
            $"individualFileProbes={validationMetrics.IndividualFileProbes} " +
            $"individualDirectoryProbes={validationMetrics.IndividualDirectoryProbes} " +
            $"browserTabs={browserTabsSavedMarkCount} workspace={workspaceSavedMarkCount} succeeded={markPersistenceSucceeded}");
    }
    private void ApplyStartupLegacyMarksUi(BrowserStartupLegacyMarkRestoreResult result)
    {
        switch (result.Kind)
        {
            case BrowserStartupLegacyMarkRestoreKind.SkippedByWorkspaceAuthority:
                LogService.Info("[MarkPersistence] Legacy persisted marks restore skipped because workspace/per-tab marks are authoritative.");
                return;
            case BrowserStartupLegacyMarkRestoreKind.SkippedDisabled:
                LogService.Info("[MarkPersistence] Restore skipped because persistence is disabled.");
                return;
            case BrowserStartupLegacyMarkRestoreKind.SkippedEmpty:
                LogService.Info("[MarkPersistence] Restore skipped because no persisted marks were found.");
                return;
            case BrowserStartupLegacyMarkRestoreKind.SkippedMissing:
                LogService.Info($"[MarkPersistence] Restore skipped because all persisted paths were missing. Missing={result.SkippedCount}");
                ShowStatusMessage("前回のマークは見つからないため復元しませんでした。");
                return;
            case BrowserStartupLegacyMarkRestoreKind.Restored:
                InvalidateMarkSummaryCache();
                InvalidateRecentMultiMarkIntent();
                ClearPendingEscExitMarkPersistence();
                _browserMarkInteractionController.SyncMarkState(
                    hasMarks: _browserApplicationCoordinator.Selection.Count > 0);
                RefreshMarkUi();
                LogService.Info($"[MarkPersistence] Restored={result.RestoredPaths.Count} Missing={result.SkippedCount} OutOfDir={CountMarksOutsideCurrentDirectory()}");
                ShowStatusMessage(result.SkippedCount > 0
                    ? $"前回のマーク {result.RestoredPaths.Count} 件を復元しました（{result.SkippedCount} 件は見つからず除外）。"
                    : $"前回のマーク {result.RestoredPaths.Count} 件を復元しました。");
                return;
        }
    }
    private void RestoreWindowSettings()
    {
        if (_settingsCoordinator.Value.Window.X != -1)
        {
            this.StartPosition = FormStartPosition.Manual;
            var requestedBounds = new Rectangle(
                _settingsCoordinator.Value.Window.X,
                _settingsCoordinator.Value.Window.Y,
                _settingsCoordinator.Value.Window.Width,
                _settingsCoordinator.Value.Window.Height);
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
            if (_settingsCoordinator.Value.Window.State == FormWindowState.Maximized)
            {
                this.WindowState = FormWindowState.Maximized;
            }
            else if (this.WindowState == FormWindowState.Normal && this.Width < MinimumNormalWindowWidth)
            {
                this.Width = MinimumNormalWindowWidth;
            }
            LogService.Info($"[WindowVisibility] RestoreWindowSettings Requested={FormatBoundsForLog(requestedBounds)} Applied={FormatBoundsForLog(restoredBounds)} State={_settingsCoordinator.Value.Window.State}");
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
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser)
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
        _settingsCoordinator.SaveViewerWordWrap(viewerTextBox.WordWrap);
    }
    private void MainForm_Activated(object? sender, EventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser)
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
        bool show = _settingsCoordinator.Value.Appearance?.ShowBrowserToolbar ?? false;
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
            CreateMenuItem = (text, onClick, browserOnly, requiresIdle, requiresSelection, requiresFile, requiresEditorTarget, requiresExactlyTwoSelection, requiresTwoFiles, shortcutHint, commandId) =>
                CreateMenuItem(text, onClick, browserOnly, requiresIdle, requiresSelection, requiresFile, requiresEditorTarget, requiresExactlyTwoSelection, requiresTwoFiles, shortcutHint, commandId),
            GetBrowserCommandShortcutHint = commandId => ResolveBrowserCommandShortcutHint(commandId),
            IsWorkspaceSnapshotEnabled = () => _featureGate.IsEnabled(FeatureId.WorkspaceSnapshot),
            ExecuteBrowserOpen = () => _ = ExecuteCommandFromUi(CommandIds.BrowserExecute, CommandScope.Browser, "Menu.File.Open"),
            ExecuteBrowserDefaultOpenMarked = () => _ = ExecuteCommandFromUi(CommandIds.BrowserDefaultOpenMarked, CommandScope.Browser, "Menu.File.DefaultOpenMarked"),
            ExecuteAttribute = () => _ = ExecuteCommandFromUi(CommandIds.BrowserChangeAttributes, CommandScope.Browser, "Menu.File.Attributes"),
            ExecuteCopy = () => _ = ExecuteCommandFromUi(CommandIds.FileCopy, CommandScope.Browser, "Menu.File.Copy"),
            ExecuteMove = () => _ = ExecuteCommandFromUi(CommandIds.FileMove, CommandScope.Browser, "Menu.File.Move"),
            ExecuteRename = () => _ = ExecuteCommandFromUi(CommandIds.FileRename, CommandScope.Browser, "Menu.File.Rename"),
            ExecuteDelete = () => _ = ExecuteCommandFromUi(CommandIds.FileDelete, CommandScope.Browser, "Menu.File.Delete"),
            EmptyMidFdManagedTrash = () => _ = ExecuteCommandFromUi(CommandIds.AppEmptyManagedTrash, CommandScope.Global, "Menu.File.EmptyManagedTrash"),
            ExecuteCreateDirectory = () => _ = ExecuteCommandFromUi(CommandIds.BrowserCreateDirectory, CommandScope.Browser, "Menu.File.CreateDirectory"),
            ExecuteCreateFile = () => _ = ExecuteCommandFromUi(CommandIds.BrowserCreateFile, CommandScope.Browser, "Menu.File.CreateFile"),
            CloseMainForm = () => Close(),
            ExecuteSort = () => _ = ExecuteCommandFromUi(CommandIds.BrowserSort, CommandScope.Browser, "Menu.View.Sort"),
            ExecuteFilter = () => _ = ExecuteCommandFromUi(CommandIds.BrowserFilter, CommandScope.Browser, "Menu.View.Filter"),
            ExecuteSearch = () => _ = ExecuteCommandFromUi(CommandIds.BrowserSearch, CommandScope.Browser, "Menu.View.Search"),
            ClearFilter = () => _ = ExecuteCommandFromUi(CommandIds.BrowserFilterClear, CommandScope.Browser, "Menu.View.FilterClear"),
            SetFileDisplayModeNameOnly = () => SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameOnly),
            SetFileDisplayModeNameSize = () => SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameSize),
            SetFileDisplayModeNameSizeDate = () => SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameSizeDate),
            SetFileDisplayModeNameExtensionAligned = () => SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameExtensionAligned),
            UpdateFileDisplayModeMenuChecks = () => UpdateFileDisplayModeMenuChecks(),
            ReloadCurrentDirectory = () => ExecuteCommandFromUi(CommandIds.BrowserReload, CommandScope.Browser, "Menu.View.Reload"),
            ExecutePreviewLaunch = () => _ = ExecuteCommandFromUi(CommandIds.BrowserPreview, CommandScope.Browser, "Menu.View.Preview"),
            ExecuteLogdisk = () => _ = ExecuteCommandFromUi(CommandIds.BrowserLogdisk, CommandScope.Browser, "Menu.View.Logdisk"),
            OpenFileListColorSettings = () => OpenFileListColorSettings(),
            NavigateParent = () => ExecuteCommandFromUi(CommandIds.BrowserNavigateParent, CommandScope.Browser, "Menu.Move.Parent"),
            ExecuteDriveRoot = () => ExecuteDriveRoot(),
            OpenExplorer = () => ExecuteCommandFromUi(CommandIds.BrowserOpenExplorer, CommandScope.Browser, "Menu.Move.OpenExplorer"),
            ExecuteTop = () => _ = ExecuteCommandFromUi(CommandIds.BrowserCursorTop, CommandScope.Browser, "Menu.Move.Top"),
            ExecuteBottom = () => _ = ExecuteCommandFromUi(CommandIds.BrowserCursorBottom, CommandScope.Browser, "Menu.Move.Bottom"),
            ExecuteTreeDialog = () => _ = ExecuteCommandFromUi(CommandIds.BrowserTree, CommandScope.Browser, "Menu.Move.Tree"),
            ExecuteQuickAccess = () => _ = ExecuteCommandFromUi(CommandIds.BrowserQuickAccess, CommandScope.Browser, "Menu.Move.QuickAccess"),
            NavigateBack = () => ExecuteCommandFromUi(CommandIds.BrowserNavigateBack, CommandScope.Browser, "Menu.Move.HistoryBack"),
            NavigateForward = () => ExecuteCommandFromUi(CommandIds.BrowserNavigateForward, CommandScope.Browser, "Menu.Move.HistoryForward"),
            NavigateTabHistoryBack = () => ExecuteCommandFromUi(CommandIds.BrowserTabHistoryBack, CommandScope.Browser, "Menu.Move.TabHistoryBack"),
            NavigateTabHistoryForward = () => ExecuteCommandFromUi(CommandIds.BrowserTabHistoryForward, CommandScope.Browser, "Menu.Move.TabHistoryForward"),
            OpenTabHistoryDialog = () => ExecuteCommandFromUi(CommandIds.BrowserTabHistoryShow, CommandScope.Browser, "Menu.Move.TabHistoryShow"),
            CreateNewBrowserTab = () => _ = ExecuteCommandFromUi(CommandIds.BrowserTabNew, CommandScope.Browser, "Menu.Tab.New"),
            ToggleActiveBrowserTabLock = () => _ = ExecuteCommandFromUi(CommandIds.BrowserTabLock, CommandScope.Browser, "Menu.Tab.Lock"),
            ToggleActiveBrowserTabReadOnly = () => ExecuteCommandFromUi(CommandIds.BrowserTabReadOnlyToggle, CommandScope.Browser, "Menu.BrowserTab.ReadOnly"),
            SelectNextBrowserTab = () => _ = ExecuteCommandFromUi(CommandIds.BrowserTabNext, CommandScope.Browser, "Menu.Tab.Next"),
            SelectPreviousBrowserTab = () => _ = ExecuteCommandFromUi(CommandIds.BrowserTabPrevious, CommandScope.Browser, "Menu.Tab.Previous"),
            CloseCurrentBrowserTab = () => _ = ExecuteCommandFromUi(CommandIds.BrowserTabClose, CommandScope.Browser, "Menu.Tab.Close"),
            ExecutePack = () => _ = ExecuteCommandFromUi(CommandIds.ArchivePack, CommandScope.Browser, "Menu.Tools.Pack"),
            ExecuteUnpack = () => _ = ExecuteCommandFromUi(CommandIds.ArchiveUnpack, CommandScope.Browser, "Menu.Tools.Unpack"),
            ExecuteOpenWithEditor = () => _ = ExecuteCommandFromUi(CommandIds.BrowserOpenExternalEditor, CommandScope.Browser, "Menu.Tools.ExternalEditor"),
            ExecuteOpenWithDiff = () => ExecuteOpenWithDiff(),
            OpenPowerShell = () => ExecuteCommandFromUi(CommandIds.BrowserOpenShell, CommandScope.Browser, "Menu.Tools.OpenShell"),
            CopyFullPath = () => ExecuteCommandFromUi(CommandIds.BrowserCopyFullPath, CommandScope.Browser, "Menu.Tools.CopyFullPath"),
            OpenMarkSlotDialog = () => _ = ExecuteCommandFromUi(CommandIds.BrowserOpenMarkSlot, CommandScope.Browser, "Menu.Tools.MarkSlot"),
            OpenWorkspaceSnapshotDialog = () => OpenWorkspaceSnapshotDialog(),
            ShowSystemInformation = () => OpenSystemInformationFromUi("Menu.Tools.SystemInformation"),
            OpenSettings = () => ExecuteCommandFromUi(CommandIds.AppOpenSettings, CommandScope.Global, "Menu.Tools.Settings"),
            OpenManagedTrashDialog = () => ExecuteCommandFromUi(CommandIds.AppOpenManagedTrash, CommandScope.Global, "Menu.Tools.ManagedTrash"),
            ShowMenuKeyHint = () => _ = ExecuteCommandFromUi(CommandIds.BrowserShowHelp, CommandScope.Browser, "Menu.Help.KeyHint"),
            ShowCommandList = () => _ = ExecuteCommandFromUi(CommandIds.AppOpenCommandList, CommandScope.Global, "Menu.Help.CommandList"),
            ShowVersionInfo = () => ShowVersionInfo()
        };
        MainMenuConstructionCoordinator.BuildResult menuBuildResult = new MainMenuConstructionCoordinator().Build(menuBuildContext);
        _fileDisplayModeNameOnlyMenuItem = menuBuildResult.FileDisplayModeNameOnlyMenuItem;
        _fileDisplayModeNameSizeMenuItem = menuBuildResult.FileDisplayModeNameSizeMenuItem;
        _fileDisplayModeNameSizeDateMenuItem = menuBuildResult.FileDisplayModeNameSizeDateMenuItem;
        _fileDisplayModeNameExtensionAlignedMenuItem = menuBuildResult.FileDisplayModeNameExtensionAlignedMenuItem;
        _reloadCurrentDirectoryMenuItem = menuBuildResult.ReloadCurrentDirectoryMenuItem;
        _toggleBrowserTabLockMenuItem = menuBuildResult.ToggleBrowserTabLockMenuItem;
        _toggleBrowserTabReadOnlyMenuItem = menuBuildResult.ToggleBrowserTabReadOnlyMenuItem;
        ToolStripMenuItem viewMenu = menuBuildResult.ViewMenu;
        ToolStripMenuItem browserTabLayoutMenu = new MidFD.Controls.TightCascadeToolStripMenuItem("タブ表示位置");
        ToolStripMenuItem horizontalBrowserTabLayoutMenuItem = new("横型", null, (_, _) => SetBrowserTabLayoutFromMenu(BrowserTabLayoutMode.Horizontal));
        ToolStripMenuItem verticalBrowserTabLayoutMenuItem = new("縦側", null, (_, _) => SetBrowserTabLayoutFromMenu(BrowserTabLayoutMode.Vertical));
        browserTabLayoutMenu.DropDownItems.Add(horizontalBrowserTabLayoutMenuItem);
        browserTabLayoutMenu.DropDownItems.Add(verticalBrowserTabLayoutMenuItem);
        browserTabLayoutMenu.DropDownOpening += (_, _) =>
        {
            bool vertical = _settingsCoordinator.Value.BrowserTabs?.LayoutMode == BrowserTabLayoutMode.Vertical;
            horizontalBrowserTabLayoutMenuItem.Checked = !vertical;
            verticalBrowserTabLayoutMenuItem.Checked = vertical;
        };
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(browserTabLayoutMenu);
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
        favoritesMenu.DropDownOpening += (s, e) => BuildFavoritesMenu(favoritesMenu, QuickAccessService.GetRegisteredEntries(_browserApplicationCoordinator.Workspace.QuickAccessSnapshot));

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

        string menuPreset = UiThemeResolver.MapFromDisplayColor(_settingsCoordinator.Value.Appearance?.ColorTheme);
        var menuThemeColors = UiThemeResolver.Resolve(menuPreset);
        ApplyMenuStripRenderer(
            FileListColorResolver.NormalizeCoreTheme(_settingsCoordinator.Value.Appearance?.ColorTheme, _settingsCoordinator.Value) == "Light",
            menuThemeColors.ChromeForeColor);

        mainMenuStrip.ContextMenuStrip = new ContextMenuStrip();
        var hideItem = new ToolStripMenuItem("戻る・進む・上へ・更新ボタンを非表示にする");
        hideItem.Click += (s, e) =>
        {
            if (_settingsCoordinator.Value.Appearance != null)
            {
                _settingsCoordinator.SaveShowBrowserToolbar(false);
                UpdateBrowserToolbarVisibility();
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
        if (_settingsCoordinator.Value.Appearance != null)
        {
            var uiThemeColors = UiThemeResolver.Resolve(_settingsCoordinator.Value.Appearance);
            themeBackColor = uiThemeColors.ChromeBackColor;
            themeForeColor = uiThemeColors.ChromeForeColor;
            applyTheme = ApplyDropDownTheme;
        }

        FavoritesMenuPresenter.Build(
            favoritesMenu,
            entries,
            NavigateToPathSafe,
            AddCurrentLocationToFavorites,
            () => _ = ExecuteCommandFromUi(CommandIds.BrowserQuickAccess, CommandScope.Browser, "Favorites.QuickAccess"),
            applyTheme,
            themeBackColor,
            themeForeColor);
    }

    private void AddCurrentLocationToFavorites()
    {
        AddBrowserPathToFavorites(_browserApplicationCoordinator.CurrentPath, _browserApplicationCoordinator.CurrentPath);
    }

    private void AddSelectedBrowserItemToFavorites()
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || IsCurrentDirectoryBusy())
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

        AddBrowserPathToFavorites(itemPath, _browserApplicationCoordinator.CurrentPath);
    }

    private void AddBrowserPathToFavorites(string pathToRegister, string currentPath)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || IsCurrentDirectoryBusy())
        {
            return;
        }

        string initialDisplayName = _browserApplicationCoordinator.CreateQuickAccessDisplayName(pathToRegister);
        QuickAccessLocationDialogResult? dialogResult = QuickAccessLocationDialog.ShowEditor(
            this,
            "QuickAccess 登録",
            currentPath,
            pathToRegister,
            initialDisplayName,
            null,
            _browserApplicationCoordinator.GetQuickAccessCategoryNames(),
            initialUseForTabTitle: false);
        if (dialogResult == null)
        {
            return;
        }

        BrowserQuickAccessRegistrationExecution registration = _browserNavigationWorkflowApplicationCoordinator.ExecuteQuickAccessRegistration(
            dialogResult.DisplayName,
            dialogResult.CategoryName,
            dialogResult.Path,
            dialogResult.UseForTabTitle,
            currentPath,
            BuildBrowserTabStateFromCurrentUi(),
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());
        if (registration.Succeeded)
        {
            RefreshAllBrowserTabTitles();
            ShowStatusMessage(registration.Message);
            return;
        }

        MessageBox.Show(registration.Message, "QuickAccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void NavigateToPathSafe(string path)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser ||
            !CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            return;
        }
        string resolved = _browserApplicationCoordinator.NormalizeDestinationDirectory(path);
        try
        {
            ExecuteConfirmedUserDirectoryNavigation(resolved);
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
                if (!IsDisposed && IsHandleCreated && _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser && browserPanel.Visible)
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
        var menuThemeColors = UiThemeResolver.Resolve(UiThemeResolver.MapFromDisplayColor(_settingsCoordinator.Value.Appearance?.ColorTheme));
        MenuStripPresentationHelper.SynchronizeFontAndLayout(
            mainMenuStrip,
            menuFont,
            FileListColorResolver.NormalizeCoreTheme(_settingsCoordinator.Value.Appearance?.ColorTheme, _settingsCoordinator.Value) == "Light",
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
        string? shortcutHint = null,
        string? commandId = null)
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
        if (requiresSelection || requiresFile || requiresEditorTarget || requiresExactlyTwoSelection || requiresTwoFiles || !string.IsNullOrWhiteSpace(commandId))
        {
            _menuItemRules[item] = new CommandStateCoordinator.MenuItemStateRule(
                requiresSelection,
                requiresFile,
                requiresEditorTarget,
                requiresExactlyTwoSelection,
                requiresTwoFiles,
                commandId);
        }
        return item;
    }
    private void UpdateMenuStripState()
    {
        var snapshot = BuildCommandUiSnapshot();
        List<ToolStripItem> items = _browserOnlyMenuItems
            .Concat(_busyAwareMenuItems)
            .Concat(_menuItemRules.Keys)
            .Distinct()
            .ToList();
        var inputs = items
            .Select(item => new BrowserMenuItemProjectionInput(
                _browserOnlyMenuItems.Contains(item),
                _busyAwareMenuItems.Contains(item),
                _menuItemRules.TryGetValue(item, out CommandStateCoordinator.MenuItemStateRule rule)
                    ? rule
                    : new CommandStateCoordinator.MenuItemStateRule()))
            .ToList();
        IReadOnlyList<bool> states = BrowserMenuProjection.Build(snapshot, inputs);
        for (int index = 0; index < items.Count; index++)
        {
            items[index].Enabled = states[index];
        }
        if (_reloadCurrentDirectoryMenuItem != null)
        {
            _reloadCurrentDirectoryMenuItem.Enabled = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser && !IsCurrentDirectoryBusy();
        }
    }
    private void ClearBrowserTabContextState()
    {
        _browserTabWorkflowApplicationCoordinator.SetContextTabIndex(-1);
        _browserTabContextCategoryId = null;
        _browserTabContextTabId = null;
    }
    private void ClearBrowserTabCategoryContextState()
    {
        _browserCategoryWorkflowApplicationCoordinator.SetContextCategoryId(null);
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
    private void ExecuteDriveRoot()
    {
        BrowserDriveRootNavigationPreparation preparation = _browserNavigationWorkflowApplicationCoordinator.PrepareDriveRoot(
            BuildBrowserTabStateFromCurrentUi());
        if (preparation.LockedRoot.Kind == BrowserLockedRootResolutionKind.Missing)
        {
            ShowStatusMessage("固定タブのルートが見つかりません。通常のルートへ移動します。");
        }
        if (!preparation.TargetChanged || preparation.Navigation is not { } navigationPreparation)
        {
            return;
        }

        if (navigationPreparation.RequiresDerivedTabConfirmation &&
            !ShowBrowserDerivedTabNavigationConfirmation())
        {
            return;
        }

        BrowserDriveRootNavigationExecution execution = _browserNavigationWorkflowApplicationCoordinator.ExecutePreparedDriveRoot(
            preparation,
            BuildBrowserTabStateFromCurrentUi(),
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState(),
            confirmedDerivedTabCreation: true);
        if (execution.Navigation is not { } navigation)
        {
            return;
        }
        _viewerWorkflowApplicationCoordinator.ClearCurrentPreviewTarget();
        if (!navigation.Succeeded || navigation.Load is not { Succeeded: true } load)
        {
            return;
        }
        PrepareDerivedBrowserTabPresentation(navigation.DerivedTabIndex);
        ApplyDirectoryLoadUi(
            load,
            CreateDerivedBrowserTabSelectionCallback(navigation.DerivedTabIndex));
        ApplyDirectoryPostLoadEffects(navigation.PostLoadEffects);
    }
    private CommandStateCoordinator.CommandUiSnapshot BuildCommandUiSnapshot()
    {
        BrowserTabState? activeTab = _browserApplicationCoordinator.Workspace.ActiveTabSnapshot;
        bool isBrowserMode = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser;
        ListViewItem? currentItem = isBrowserMode ? GetCurrentBrowserItem() : null;
        string? currentPath = currentItem?.Tag as string;
        int selectionCount = isBrowserMode ? GetLightweightSelectionCount(currentItem) : 0;
        IReadOnlyList<string> markedPaths = isBrowserMode
            ? _browserApplicationCoordinator.Selection.Snapshot()
            : Array.Empty<string>();
        IReadOnlyDictionary<string, bool>? passivePathKinds = isBrowserMode
            ? _browserApplicationCoordinator.GetPassivePathKinds(
                currentItem?.Tag is string currentItemPath ? new[] { currentItemPath } : null)
            : null;
        SelectionResult? selectionSnapshot = markedPaths.Count > 0
            ? new SelectionResult(markedPaths, hasMarkedSelection: true)
            : currentItem != null && currentItem.Text != ".." && !string.IsNullOrWhiteSpace(currentPath)
                ? new SelectionResult([currentPath!], hasMarkedSelection: false)
                : null;
        _cachedCommandUiSnapshot = BrowserCommandUiProjection.Build(
            isBrowserMode,
            _fileOperationApplicationCoordinator.IsClipboardBusy,
            selectionCount,
            HasTwoFileSelectionForCommandState(selectionCount, passivePathKinds),
            currentItem?.Text,
            currentPath,
            currentItem != null && IsBrowserFileItem(currentItem),
            selectionSnapshot,
            passivePathKinds,
            allowFileSystemFallback: markedPaths.Count == 0,
            canUndo: _unifiedUndoRedoCoordinator.CanUndo,
            canRedo: _unifiedUndoRedoCoordinator.CanRedo,
            filterPattern: _browserApplicationCoordinator.FilterPattern,
            filterDetailActive: BrowserCommandUiProjection.ResolveFilterDetailActive(activeTab));
        return _cachedCommandUiSnapshot;
    }
    private int GetLightweightSelectionCount(ListViewItem? currentItem)
    {
        if (_browserApplicationCoordinator.Selection.Count > 0)
        {
            return _browserApplicationCoordinator.Selection.Count;
        }
        return currentItem != null
            && currentItem.Text != ".."
            && currentItem.Tag is string path
            && !string.IsNullOrWhiteSpace(path)
            ? 1
            : 0;
    }
    private bool HasTwoFileSelectionForCommandState(
        int selectionCount,
        IReadOnlyDictionary<string, bool>? passivePathKinds)
    {
        if (selectionCount != 2 || _browserApplicationCoordinator.Selection.Count != 2)
        {
            return false;
        }
        int checkedCount = 0;
        foreach (string path in _browserApplicationCoordinator.Selection)
        {
            checkedCount++;
            if (checkedCount > 2
                || passivePathKinds?.TryGetValue(path, out bool isDirectory) != true
                || isDirectory)
            {
                return false;
            }
        }
        return checkedCount == 2;
    }
    private CommandStateCoordinator.CommandHintState BuildCommandHintState()
    {
        return _commandStateCoordinator.CreateCommandHintState(
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            Visible,
            Enabled,
            browserPanel.Visible,
            IsMenuStripAltNavigationActive(),
            Focused || ContainsFocus);
    }
    private string CurrentFunctionKeyProfileValue =>
        _settingsCoordinator.Value.Input?.FunctionKeyProfile ?? InputSettings.StandardProfileValue;

    private string ResolveBrowserCommandShortcutHint(string commandId)
    {
        var hints = new List<string>(ResolveBrowserKeyCommandMap()
            .Where(pair => string.Equals(pair.Value, commandId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key));
        FunctionKeyProfile profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        for (int slot = 1; slot <= 12; slot++)
        {
            (string? commandId, string label)[] layers =
            {
                (ResolveFunctionBarCommandIdForHint(profile, slot, isShift: false, isCtrl: false, isAlt: false), $"F{slot}"),
                (ResolveFunctionBarCommandIdForHint(profile, slot, isShift: true, isCtrl: false, isAlt: false), $"Shift+F{slot}"),
                (ResolveFunctionBarCommandIdForHint(profile, slot, isShift: false, isCtrl: true, isAlt: false), $"Ctrl+F{slot}"),
                (ResolveFunctionBarCommandIdForHint(profile, slot, isShift: false, isCtrl: false, isAlt: true), $"Alt+F{slot}")
            };
            foreach ((string? assignedCommandId, string label) in layers)
            {
                if (string.Equals(assignedCommandId, commandId, StringComparison.OrdinalIgnoreCase))
                {
                    hints.Add(label);
                }
            }
        }

        return string.Join(" / ", hints.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private string? ResolveFunctionBarCommandIdForHint(FunctionKeyProfile profile, int slot, bool isShift, bool isCtrl, bool isAlt)
    {
        return FunctionKeyProfileService.ResolveFunctionBarCommandId(
            profile,
            slot,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesStandard,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesFdCompatible,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesShiftStandard,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesShiftFdCompatible,
            isShift,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesCtrlStandard,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesCtrlFdCompatible,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesAltStandard,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesAltFdCompatible,
            isCtrl,
            isAlt);
    }
    private string BuildMenuKeyHintMessage()
    {
        string DescribeCommand(string commandId)
        {
            var definition = _commandRegistry.Find(commandId);
            if (definition == null)
            {
                return string.Empty;
            }

            string hint = ResolveBrowserCommandShortcutHint(commandId);
            return string.IsNullOrWhiteSpace(hint)
                ? definition.DisplayName
                : $"{definition.DisplayName}: {hint}";
        }

        return
            "メニューバーは補助導線です。実効CommandのRegistry表示名と割当を表示します。\n\n" +
            $"{DescribeCommand(CommandIds.BrowserExecute)}\n" +
            $"{DescribeCommand(CommandIds.BrowserDefaultOpen)}\n" +
            $"{DescribeCommand(CommandIds.BrowserOpenCommandDialog)}\n" +
            $"{DescribeCommand(CommandIds.BrowserPreview)}\n" +
            $"{DescribeCommand(CommandIds.FileCopy)}\n" +
            $"{DescribeCommand(CommandIds.FileMove)}\n" +
            $"{DescribeCommand(CommandIds.FileRename)}\n" +
            $"{DescribeCommand(CommandIds.BrowserQuickAccess)}";
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
    void IViewerModeUiPort.CleanupBrowserInteraction()
        => CleanupBrowserRightInteraction(clearContextMenuSuppression: true);

    void IViewerModeUiPort.HideTransientOverlay()
        => HideCommandHintOverlay();

    void IViewerModeUiPort.HideViewerContent()
        => HideViewerContentBeforeExit();

    bool IBrowserWorkspaceSnapshotUiPort.ConfirmRestore(
        WorkspaceSnapshotEntry entry,
        WorkspaceState state)
    {
        DialogResult confirm = MessageBox.Show(
            this,
            $"現在のカテゴリ/タブ構成を、選択したスナップショットで置き換えます。\n\n名前: {entry.Name}\nカテゴリ: {entry.CategoryCount}\nタブ: {entry.TabCount}\nマーク: {entry.MarkedCount}\nアクティブ: {entry.ActivePath}\n\n必要に応じて先に現在状態をスナップショット保存してください。",
            "Workspace スナップショット復元",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return confirm == DialogResult.OK;
    }

    BrowserTabState IBrowserWorkspaceSnapshotUiPort.CaptureCurrentBrowserTabState()
        => BuildBrowserTabStateFromCurrentUi();

    void IBrowserWorkspaceSnapshotUiPort.ApplyBrowserTabRuntimeRestore(
        BrowserWorkspaceRuntimeRestoreExecution runtime)
        => ApplyBrowserTabRuntimeRestore(runtime);

    void IViewerModeUiPort.ApplyPendingBrowserRefresh(BrowserRefreshProcessExecution execution)
        => ApplyPendingRefreshExecution(execution, "ViewerExit");

    PreviewKind IViewerModeUiPort.GetCurrentSelectionKind()
        => GetCurrentSelectionPreviewKind();

    void IViewerModeUiPort.ApplyViewerChrome()
        => ApplyViewerChromeState();

    void IViewerModeUiPort.UpdateFunctionBar()
        => UpdateFunctionBar();

    void IViewerModeUiPort.UpdateMenuState()
        => UpdateMenuStripState();

    void IViewerModeUiPort.ShowBrowserSurface()
        => ShowBrowserSurfaceForMode();

    void IViewerModeUiPort.ShowViewerSurface()
        => ShowViewerSurfaceForMode();

    void IViewerModeUiPort.EnsureStatusBar()
        => EnsureStatusBarVisible();

    void IViewerModeUiPort.RefreshBrowserStatus()
        => RefreshBrowserStatusForMode();

    void IViewerModeUiPort.ApplyViewerStatus()
        => ApplyViewerStatusLine();

    void IViewerModeUiPort.LogViewerLayout(string reason)
        => LogViewerLayoutBounds(reason);

    void IViewerModeUiPort.ClearPreview(string message)
        => ClearPreview(message);

    string? IViewerModeUiPort.GetCurrentPreviewSelectionPath()
        => GetCurrentPreviewSelectionPath();

    private void ShowBrowserSurfaceForMode()
    {
        viewerPanel.Visible = false;
        browserPanel.Visible = true;
        browserPanel.BringToFront();
        browserPanel.Focus();
    }

    private void RefreshBrowserStatusForMode()
    {
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

    private void ShowViewerSurfaceForMode()
    {
        browserPanel.Visible = false;
        fileListView.Visible = false;
        viewerPanel.Visible = true;
        viewerPanel.BringToFront();
        viewerPanel.Focus();
    }

    private void SwitchUIMode(ViewerApplicationMode mode, PreviewKind? previewKindOverride = null)
    {
        _viewerModeApplicationCoordinator.Switch(
            mode,
            previewKindOverride,
            this);
    }
    private bool IsCurrentDirectoryBusy()
    {
        return _fileOperationApplicationCoordinator.IsBusy;
    }
    private bool IsCurrentDirectoryRefreshBlocked()
    {
        return _viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || IsCurrentDirectoryBusy();
    }
    private void ApplyFeatureProfile(bool isMouseGestureExplicit)
    {
        _settingsCoordinator.ApplyRuntimeProfile(isMouseGestureExplicit);
        _featureGate = new FeatureGateService(
            _settingsCoordinator.FeatureProfile,
            _settingsCoordinator.Value.WorkspaceSnapshotEnabledOverride);
        UpdateWorkspaceSnapshotContextMenuAvailability();
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
    private bool GuardClipboardBusy(string? message = null)
    {
        if (_fileOperationApplicationCoordinator.IsClipboardBusy)
        {
            ShowStatusMessage(message ?? FileOperationPresentationHelper.GetBusyBlockedMessage(
                _fileOperationApplicationCoordinator.ActiveOperationName,
                canCancel: _fileOperationApplicationCoordinator.CanCancel,
                isCancelRequested: _fileOperationApplicationCoordinator.CancellationTokenSource?.IsCancellationRequested ?? false));
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
        SettingsRecoveryState? recovery = _settingsCoordinator.CurrentRecoveryState;
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
        bool requestedBefore = _fileOperationApplicationCoordinator.CancellationTokenSource?.IsCancellationRequested ?? false;
        LogService.Info(
            $"[CancelRuntime] Request received. source={source}, thread={Environment.CurrentManagedThreadId}, " +
            $"busy={_fileOperationApplicationCoordinator.IsClipboardBusy}, hasCts={_fileOperationApplicationCoordinator.CancellationTokenSource != null}, alreadyRequested={requestedBefore}, " +
            $"operation={_fileOperationApplicationCoordinator.ActiveOperationName ?? "<unknown>"}, statusVersion={_fileOperationApplicationCoordinator.StatusVersion}");
        if (_fileOperationApplicationCoordinator.CancellationTokenSource == null)
        {
            LogService.Warn($"[CancelRuntime] Request ignored because CTS is null. source={source}");
            return false;
        }
        if (!_fileOperationApplicationCoordinator.CanCancel)
        {
            ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                _fileOperationApplicationCoordinator.ActiveOperationName,
                canCancel: false,
                isCancelRequested: false));
            LogService.Info($"[CancelRuntime] Request consumed without cancellation because the operation is non-cancelable. source={source}");
            return true;
        }
        try
        {
            LogService.Info(
                $"[CancelRuntime] MarkCancelRequested before. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                $"requested={_fileOperationApplicationCoordinator.CancellationTokenSource.IsCancellationRequested}, progressDialog={_fileOperationProgressDialog != null}");
            _fileOperationProgressDialog?.MarkCancelRequested();
            LogService.Info(
                $"[CancelRuntime] MarkCancelRequested after. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                $"requested={_fileOperationApplicationCoordinator.CancellationTokenSource.IsCancellationRequested}, progressDialog={_fileOperationProgressDialog != null}");
            if (!_fileOperationApplicationCoordinator.CancellationTokenSource.IsCancellationRequested)
            {
                LogService.Warn(
                    $"[CancelRuntime] CTS cancel before. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                    $"requested={_fileOperationApplicationCoordinator.CancellationTokenSource.IsCancellationRequested}, operation={_fileOperationApplicationCoordinator.ActiveOperationName ?? "<unknown>"}");
                _fileOperationApplicationCoordinator.RequestCancellation();
                LogService.Warn(
                    $"[CancelRuntime] CTS cancel after. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                    $"requested={_fileOperationApplicationCoordinator.CancellationTokenSource.IsCancellationRequested}, operation={_fileOperationApplicationCoordinator.ActiveOperationName ?? "<unknown>"}");
                LogService.Info($"[FileOperationCancel] Cancel requested. source={source}, operation={_fileOperationApplicationCoordinator.ActiveOperationName ?? "<unknown>"}, statusVersion={_fileOperationApplicationCoordinator.StatusVersion}");
                ShowStatusMessage(FileOperationPresentationHelper.GetCancelRequestedMessage(_fileOperationApplicationCoordinator.ActiveOperationName ?? "ファイル操作"));
            }
            else
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                    _fileOperationApplicationCoordinator.ActiveOperationName,
                    canCancel: true,
                    isCancelRequested: true));
            }
            LogService.Info(
                $"[CancelRuntime] Request completed. source={source}, requested={_fileOperationApplicationCoordinator.CancellationTokenSource.IsCancellationRequested}, " +
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
        return _fileOperationApplicationCoordinator.IsClipboardBusy ||
            _fileOperationApplicationCoordinator.CancellationTokenSource != null ||
            !string.IsNullOrWhiteSpace(_fileOperationApplicationCoordinator.ActiveOperationName) ||
            _fileOperationProgressDialog != null;
    }
    private bool TryRouteActiveFileOperationCancel(string source)
    {
        bool hasActiveContext = HasActiveFileOperationCancelContext();
        LogService.Info(
            $"[CancelRuntime] Active operation cancel route check. source={source}, activeContext={hasActiveContext}, " +
            $"busy={_fileOperationApplicationCoordinator.IsClipboardBusy}, hasCts={_fileOperationApplicationCoordinator.CancellationTokenSource != null}, activeOperation={_fileOperationApplicationCoordinator.ActiveOperationName ?? "<none>"}, " +
            $"thread={Environment.CurrentManagedThreadId}");
        if (!hasActiveContext)
        {
            return false;
        }
        if (_fileOperationApplicationCoordinator.CancellationTokenSource != null && _fileOperationApplicationCoordinator.CanCancel)
        {
            RequestActiveFileOperationCancel(source);
        }
        else
        {
            ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                _fileOperationApplicationCoordinator.ActiveOperationName,
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser) return false;

        bool isCompatible = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) == FunctionKeyProfile.FDCompatible;
        var profile = isCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard;

        // F1〜F12のコマンドスロットの解決
        bool isAltLayer = forcedModifierLayer == Keys.Alt || (forcedModifierLayer == Keys.None && (ModifierKeys & Keys.Alt) != 0);
        bool isCtrlLayer = forcedModifierLayer == Keys.Control || (forcedModifierLayer == Keys.None && !isAltLayer && (ModifierKeys & Keys.Control) != 0);
        bool isShiftLayer = forceShiftLayer || forcedModifierLayer == Keys.Shift || (forcedModifierLayer == Keys.None && !isAltLayer && !isCtrlLayer && (_isFunctionBarShiftLayerActive || (ModifierKeys & Keys.Shift) != 0));
        string? customCmdId = FunctionKeyProfileService.ResolveFunctionBarCommandId(
            profile,
            fKey,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesStandard,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesFdCompatible,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesShiftStandard,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesShiftFdCompatible,
            isShiftLayer,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesCtrlStandard,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesCtrlFdCompatible,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesAltStandard,
            _settingsCoordinator.Value.Input.FunctionBarCommandOverridesAltFdCompatible,
            isCtrlLayer,
            isAltLayer);

        if (FunctionKeyProfileService.IsExplicitUnassigned(customCmdId))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(customCmdId))
        {
            if (GuardClipboardBusy()) return true;

            var cmdDef = _commandRegistry.Find(customCmdId);
            CommandScope scope = cmdDef?.Scope ?? CommandScope.Browser;

            _ = ExecuteCommandFromUi(customCmdId, scope, "FunctionKey");
            return true;
        }

        return false;
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
        SetBrowserGlobalCursorIndex(Math.Max(0, _browserApplicationCoordinator.TotalItemCount - 1));
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser) return false;
        if (_browserNamePrefixJumpSession.IsActive && TryHandleBrowserNamePrefixJumpKeyDown(e))
        {
            return true;
        }
        // WinFDライクな操作: ESC は段階的な「閉じる」
        if (e.KeyCode == Keys.Escape)
        {
            if (TryRouteActiveFileOperationCancel("BrowserKeyDown"))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            if (TabFilterLockService.IsActive(
                    _browserApplicationCoordinator.FilterPattern,
                    GetActiveTabFilterLock()))
            {
                _ = ExecuteCommandFromUi(
                    CommandIds.BrowserFilterClear,
                    CommandScope.Browser,
                    "Browser.KeyDown.Escape.FilterClear");
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            if (_browserApplicationCoordinator.Selection.Count > 0)
            {
                var beforeSnapshot = _browserApplicationCoordinator.Selection.Snapshot();
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
                    $"activeContext={HasActiveFileOperationCancelContext()}, busy={_fileOperationApplicationCoordinator.IsClipboardBusy}, " +
                    $"hasCts={_fileOperationApplicationCoordinator.CancellationTokenSource != null}, requested={_fileOperationApplicationCoordinator.CancellationTokenSource?.IsCancellationRequested ?? false}, " +
                    $"activeOperation={_fileOperationApplicationCoordinator.ActiveOperationName ?? "<none>"}, " +
                    $"markedCount={_browserApplicationCoordinator.Selection.Count}, thread={Environment.CurrentManagedThreadId}");
                var result = MessageBox.Show("終了しますか？", "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                LogService.Warn(
                    $"[CancelProvenance] MidFD browser ESC exit confirm result. result={result}, " +
                    $"activeContext={HasActiveFileOperationCancelContext()}, busy={_fileOperationApplicationCoordinator.IsClipboardBusy}, " +
                    $"hasCts={_fileOperationApplicationCoordinator.CancellationTokenSource != null}, requested={_fileOperationApplicationCoordinator.CancellationTokenSource?.IsCancellationRequested ?? false}, " +
                    $"activeOperation={_fileOperationApplicationCoordinator.ActiveOperationName ?? "<none>"}, thread={Environment.CurrentManagedThreadId}");
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
        if (TryHandleBrowserCmdKeyCustomBindings(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
        {
            ListViewItem? currentItem = GetCurrentBrowserItem();
            var (commandId, selectionSnapshot) = ResolveBrowserEnterCommand(currentItem);
            _ = ExecuteCommandFromUi(commandId, CommandScope.Browser, "Browser.KeyDown.Enter", selectionSnapshot);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
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
        if (e.KeyCode == Keys.Back)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            e.Handled = true;
            e.SuppressKeyPress = true;
        _viewerWorkflowApplicationCoordinator.ClearCurrentPreviewTarget();
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
        // Shift+D / Shift+Delete は旧来の確認付き削除導線として維持する。
        if (e.KeyCode == Keys.D || e.KeyCode == Keys.Delete)
        {
            if (!e.Shift) return false;
            _ = _fileOperationApplicationCoordinator.TryStart(
                FileOperationCommandKind.Delete,
                this,
                permanentDelete: e.Shift);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.H && e.Modifiers == Keys.None)
        {
            return false;
        }
        if (e.KeyCode == Keys.H && e.Modifiers == Keys.Shift)
        {
            _ = ExecuteCommandFromUi(CommandIds.BrowserOpenCommandPrompt, CommandScope.Browser, "Browser.ShiftH.CommandPrompt");
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
        BrowserBulkMarkTransition transition = _browserTabWorkflowApplicationCoordinator.ExecuteToggleBulkMarksAndSync(targets);
        if (transition.Changed)
        {
                ApplyBulkMarkState(
                    transition.NextMarks,
                    transition.RemovedAll
                        ? (includeDirectories ? "UnmarkAllItems" : "UnmarkAllFiles")
                        : (includeDirectories ? "MarkAllItems" : "MarkAllFiles"),
                    targets.Count,
                    0,
                    Stopwatch.StartNew());
        }
    }
    private void MarkBulk(bool includeDirectories)
    {
        var targets = CollectBulkMarkTargetPaths(includeDirectories);
        if (targets.Count == 0)
        {
            return;
        }
        BrowserBulkMarkTransition transition = _browserTabWorkflowApplicationCoordinator.ExecuteMarkBulkAndSync(targets);
        if (transition.Changed)
        {
                ApplyBulkMarkState(
                    transition.NextMarks,
                    includeDirectories ? "MarkAllItems" : "MarkAllFiles",
                    targets.Count,
                    0,
                    Stopwatch.StartNew());
        }
    }
    private void InvertBulkMarks(bool includeDirectories)
    {
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            return;
        }

        var targets = CollectBulkMarkTargetPaths(includeDirectories);
        if (targets.Count == 0)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        BrowserBulkMarkTransition transition = _browserTabWorkflowApplicationCoordinator.ExecuteInvertBulkMarksAndSync(targets);
        if (transition.Changed)
        {
                ApplyBulkMarkState(
                    transition.NextMarks,
                    includeDirectories ? "InvertAllItems" : "InvertAllFiles",
                    targets.Count,
                    stopwatch.ElapsedMilliseconds,
                    stopwatch);
        }
    }
    private IReadOnlyList<string> CollectBulkMarkTargetPaths(bool includeDirectories)
    {
        if (!_browserApplicationCoordinator.Directory.TryGetCurrentSnapshotTargetPaths(
                _browserApplicationCoordinator.CurrentPath,
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
        bool deferSizeResolution = _browserApplicationCoordinator.IsAuxiliaryResolutionDeferred(_browserApplicationCoordinator.CurrentPath)
            || _browserApplicationCoordinator.Selection.Any(_browserApplicationCoordinator.IsUncPath);
        MarkSummaryBulkEffectResult summaryEffects = _markSummaryBulkEffectCoordinator.Execute(
            _browserApplicationCoordinator.Selection.Count,
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
            $"[MarkBulkPerf] {operationName} targets={targetCount} marks={_browserApplicationCoordinator.Selection.Count} " +
            $"buildNext={buildNextMarksMs}ms restore={restoreStopwatch.ElapsedMilliseconds}ms " +
            $"invalidate={repaintStopwatch.ElapsedMilliseconds}ms info={infoStopwatch.ElapsedMilliseconds}ms " +
            $"menu={menuStopwatch.ElapsedMilliseconds}ms intent={intentStopwatch.ElapsedMilliseconds}ms " +
            $"summaryCountOnly={summaryEffects.CountOnlyApplyCount} summarySchedule={summaryEffects.SummaryScheduleCount} summaryInvalidate={summaryEffects.PendingInvalidationCount} " +
            $"total={totalStopwatch.ElapsedMilliseconds}ms");
    }



    /// <summary>
    /// Phase 3-input-cmdkey-nav1: ProcessCmdKey における Browser 文脈のナビゲーション操作を helper 化。
    /// </summary>
    private BrowserFileDisplayMode GetBrowserFileDisplayMode()
    {
        return _settingsCoordinator.Value.Appearance.ResolveFileDisplayMode();
    }
    private void SetBrowserFileDetailDisplayMode(
        BrowserFileDisplayMode mode,
        bool persistTabState = true,
        bool rematerialize = true)
    {
        HideBrowserFileNameToolTip();
        BrowserFileDisplayMode currentMode = GetBrowserFileDisplayMode();
        if (currentMode == mode)
        {
            return;
        }
        _settingsCoordinator.SetBrowserFileDisplayMode(mode);
        browserPanel.Invalidate();
        if (persistTabState)
        {
            CaptureActiveBrowserTabState();
        }
        UpdateFileDisplayModeMenuChecks();
        if (rematerialize)
        {
            RematerializeBrowserPageIfCapacityChanged();
        }
        ShowStatusMessage(mode switch
        {
            BrowserFileDisplayMode.NameSize => "表示モード: サイズ",
            BrowserFileDisplayMode.NameSizeDate => "表示モード: サイズ・更新日時",
            BrowserFileDisplayMode.NameExtensionAligned => "表示モード: 拡張子整列",
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
        if (_fileDisplayModeNameExtensionAlignedMenuItem != null)
        {
            _fileDisplayModeNameExtensionAlignedMenuItem.Checked = mode == BrowserFileDisplayMode.NameExtensionAligned;
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
        string currentPath = _browserApplicationCoordinator.CurrentPath;
        if (_browserApplicationCoordinator.TryGetNetworkRoot(currentPath, out string networkRoot))
        {
            _uncDriveInfoResolver.Schedule(networkRoot, _browserRefreshWorkflowApplicationCoordinator.DirectoryNavigationGeneration, ApplyUncDriveInfo);
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
        if (_browserApplicationCoordinator.TryGetNetworkRoot(currentPath, out string inputRoot))
        {
            hasCachedDriveInfo = _uncDriveInfoResolver.TryGetCached(inputRoot, out driveInfo);
        }
        // 2. 状態を InputState にまとめる
        var state = new HeaderPresentationHelper.InputState
        {
            CurrentPath = currentPath,
            CurrentPathKind = _browserApplicationCoordinator.GetPathKind(currentPath),
            CursorIndex = _browserApplicationCoordinator.CursorIndex,
            ItemCount = _browserApplicationCoordinator.TotalItemCount > 0 ? _browserApplicationCoordinator.TotalItemCount : fileListView.Items.Count,
            ItemsPerPage = itemsPerPage,
            RowsPerColumn = rowsPerColumn,
            ColumnCount = _browserApplicationCoordinator.ColumnCount,
            MarkedFiles = _browserApplicationCoordinator.Selection,
            CachedMarkSummary = GetMarkSummaryForHeader(),
            CachedMarkCount = _browserApplicationCoordinator.Selection.MarkSummaryCacheCount,
            CachedMarkSizeText = _browserApplicationCoordinator.Selection.MarkSummaryCacheSizeText,
            CachedMarkSummaryCompact = _browserApplicationCoordinator.Selection.MarkSummaryCacheCompact,
            HasCurrentMarkSummaryCache = _browserApplicationCoordinator.Selection.MarkSummaryCacheState != MarkSummaryCacheState.Invalid
                && _browserApplicationCoordinator.Selection.MarkSummaryCacheCount == _browserApplicationCoordinator.Selection.Count
                && string.Equals(
                    _browserApplicationCoordinator.Selection.MarkSummaryCachePath,
                    NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath),
                    StringComparison.OrdinalIgnoreCase),
            IsMarkSummaryPending = _browserApplicationCoordinator.Selection.MarkSummaryCacheState == MarkSummaryCacheState.CountOnly
                || _markSummaryRebuildCoordinator.HasPending,
            CurrentItemText = currentItem?.Text,
            CurrentItemPath = currentItem?.Tag as string,
            CurrentItemExtensionText = currentItem != null && currentItem.SubItems.Count > 1 ? currentItem.SubItems[1].Text : null,
            CurrentItemSizeText = currentItem != null && currentItem.SubItems.Count > 2 ? currentItem.SubItems[2].Text : null,
            CurrentItemDateText = currentItem != null && currentItem.SubItems.Count > 3 ? currentItem.SubItems[3].Text : null,
            CurrentItemAttrText = currentItem != null && currentItem.SubItems.Count > 4 ? currentItem.SubItems[4].Text : null,
            CurrentItemIsDirectory = currentItem != null && (currentItem.Text == ".." || (currentItem.SubItems.Count > 1 && string.Equals(currentItem.SubItems[1].Text, "<DIR>", StringComparison.OrdinalIgnoreCase))),
            SortKind = _browserApplicationCoordinator.CurrentSort,
            SortAscending = _browserApplicationCoordinator.SortAscending,
            FilterPattern = _browserApplicationCoordinator.FilterPattern,
            FilterUseRegex = _browserApplicationCoordinator.FilterUseRegex,
            FilterLockSummary = TabFilterLockService.BuildDetailSummary(GetActiveTabFilterLock()),
            ShowExtensions = _settingsCoordinator.Value.Appearance?.ShowExtensions ?? true,
            ShowDirectoryMarker = _settingsCoordinator.Value.Appearance?.ShowDirectoryMarker ?? true,
            ShowItemIcons = _settingsCoordinator.Value.Appearance?.ShowItemIcons ?? true,
            DateFormat = _settingsCoordinator.Value.Appearance?.DateFormat ?? "yyyy-MM-dd HH:mm",
            SizeFormat = _settingsCoordinator.Value.Appearance?.SizeFormat ?? "HumanReadable",
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
        string pathRightText = BrowserHeaderProjection.BuildRightText(display, hasMarks);
        lblSort.Text = pathRightText;
        ApplyFilterHeaderEmphasis(display.FilterActive);
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
        _integrationSeam?.Observer?.OnMarkProjectionUpdated(
            new MainFormMarkProjectionSnapshot(
                _browserApplicationCoordinator.Selection.Snapshot(),
                lblSort.Text,
                statusLabel.Text ?? string.Empty,
                _browserApplicationCoordinator.Selection.MarkSummaryCacheSizeText));
        LogHeaderRightDiag("UpdateInfoPanel", display.MarkCount, display.MarkSizeText, pathRightText, itemRightText);
        LogFontRouteDiag("UpdateInfoPanel:END");
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
                if (IsDisposed || Disposing || generation != _browserRefreshWorkflowApplicationCoordinator.DirectoryNavigationGeneration ||
                    !_browserApplicationCoordinator.TryGetNetworkRoot(_browserApplicationCoordinator.CurrentPath, out string currentRoot) ||
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
        if (_browserApplicationCoordinator.Selection.Count == 0)
        {
            if (_browserApplicationCoordinator.Selection.MarkSummaryCacheState != MarkSummaryCacheState.Complete || _browserApplicationCoordinator.Selection.MarkSummaryCacheCount != 0)
            {
                SetCountOnlyMarkSummaryCache();
            }
            CancelPendingMarkSummaryRebuild();
            return string.Empty;
        }
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath);
        bool deferAuxiliaryResolution = _browserApplicationCoordinator.IsAuxiliaryResolutionDeferred(_browserApplicationCoordinator.CurrentPath) ||
            _browserApplicationCoordinator.Selection.Any(_browserApplicationCoordinator.IsUncPath);
        if (deferAuxiliaryResolution)
        {
            if (_browserApplicationCoordinator.Selection.MarkSummaryCacheState != MarkSummaryCacheState.CountOnly
                || _browserApplicationCoordinator.Selection.MarkSummaryCacheCount != _browserApplicationCoordinator.Selection.Count
                || !string.Equals(_browserApplicationCoordinator.Selection.MarkSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase))
            {
                SetCountOnlyMarkSummaryCache();
            }
            CancelPendingMarkSummaryRebuild();

            _browserApplicationCoordinator.LogMarkSummaryResolutionDeferred(
                _browserApplicationCoordinator.CurrentPath);
            return _browserApplicationCoordinator.Selection.MarkSummaryCache;
        }
        if (_browserApplicationCoordinator.Selection.MarkSummaryCacheState == MarkSummaryCacheState.Complete
            && _browserApplicationCoordinator.Selection.MarkSummaryCacheCount == _browserApplicationCoordinator.Selection.Count
            && string.Equals(_browserApplicationCoordinator.Selection.MarkSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase))
        {
            return _browserApplicationCoordinator.Selection.MarkSummaryCache;
        }
        if (_browserApplicationCoordinator.Selection.MarkSummaryCacheState == MarkSummaryCacheState.Invalid
            || _browserApplicationCoordinator.Selection.MarkSummaryCacheCount != _browserApplicationCoordinator.Selection.Count
            || !string.Equals(_browserApplicationCoordinator.Selection.MarkSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase))
        {
            SetCountOnlyMarkSummaryCache();
        }
        if (!_markSummaryRebuildCoordinator.HasPending)
        {
            ScheduleMarkSummaryRebuild();
        }
        return _browserApplicationCoordinator.Selection.MarkSummaryCache;
    }
    private void InvalidateMarkSummaryCache()
    {
        _browserApplicationCoordinator.InvalidateMarkSummaryCache();
        _markSummaryRebuildCoordinator.Invalidate();
    }

    private void ScheduleMarkSummaryRebuild()
    {
        if (_browserApplicationCoordinator.Selection.Count == 0 || _browserApplicationCoordinator.IsAuxiliaryResolutionDeferred(_browserApplicationCoordinator.CurrentPath) ||
            _browserApplicationCoordinator.Selection.Any(_browserApplicationCoordinator.IsUncPath) ||
            _markSummaryRebuildCoordinator.HasPending ||
            IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath)
        {
            return;
        }
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath);
        IReadOnlyList<string> paths = _browserApplicationCoordinator.Selection.Snapshot();
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
        _browserApplicationCoordinator.SetMarkSummaryComplete(currentDir, paths, result);
        MarkSummaryOrchestrationMetrics metrics = _markSummaryRebuildCoordinator.GetMetrics();
        LogService.Info(
            $"[MarkSummaryOrchestration] marks={paths.Count} schedule={metrics.ScheduleCount} build={metrics.BuildCount} " +
            $"cancel={metrics.CancelCount} superseded={metrics.SupersededCount} apply={metrics.ApplyCount + 1}");
        UpdateInfoPanel();
    }
    private void SetCountOnlyMarkSummaryCache()
    {
        _browserApplicationCoordinator.SetMarkSummaryCountOnly(
            NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath));
    }
    private void SetZeroMarkSummaryCache()
    {
        _browserApplicationCoordinator.SetMarkSummaryZero(
            NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath));
    }
    private bool TryCarryMarkSummaryAcrossDirectoryChange(string previousDirectory)
    {
        string previousDir = NavigationService.NormalizeDirectoryForCompare(previousDirectory);
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath);
        if (_browserApplicationCoordinator.Selection.Count == 0)
        {
            SetZeroMarkSummaryCache();
            return true;
        }
        if (_browserApplicationCoordinator.Selection.MarkSummaryCacheState != MarkSummaryCacheState.Complete ||
            _markSummaryRebuildCoordinator.HasPending ||
            _browserApplicationCoordinator.Selection.MarkSummaryCacheCount != _browserApplicationCoordinator.Selection.Count ||
            !string.Equals(_browserApplicationCoordinator.Selection.MarkSummaryCachePath, previousDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        MarkSummaryExactCache carried = new(
            _browserApplicationCoordinator.Selection.MarkSummaryCacheTotalSize,
            _browserApplicationCoordinator.Selection.MarkSummaryCacheFileCount,
            _browserApplicationCoordinator.CountMarksOutsideCurrentDirectory(currentDir),
            _browserApplicationCoordinator.Selection.Count);
        SetCompleteMarkSummaryCache(currentDir, carried);
        return true;
    }
    private void ApplyMarkColor(ListViewItem item, string fullPath)
    {
        var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settingsCoordinator.Value);
        BrowserFileListRenderer.ApplyItemColors(item, resolved);
    }

    private BrowserFileListRenderer.Options BuildBrowserFileListRenderOptions()
    {
        FileListColorResolver.ResolvedColors colors =
            _resolvedColors ?? FileListColorResolver.ResolveColors(_settingsCoordinator.Value);
        bool filterActive = BrowserFramePresentation.IsFilterFrameEmphasized(
            _browserApplicationCoordinator.FilterPattern,
            GetActiveTabFilterLock());
        Color filterIndicatorColor = BrowserFilterFrameColorResolver.Resolve(
            colors,
            MidFDColors.BorderLine,
            UiThemeResolver.Resolve(_settingsCoordinator.Value.Appearance).AccentColor);
        return new BrowserFileListRenderer.Options(
            colors,
            _settingsCoordinator.Value.Appearance?.ColorTheme,
            _browserApplicationCoordinator.Selection.Snapshot(),
            _settingsCoordinator.Value.Appearance?.UseUnderlineCursor ?? false,
            _settingsCoordinator.Value.Appearance?.ShowItemIcons ?? true,
            _settingsCoordinator.Value.Appearance?.ShowExtensions ?? true,
            _settingsCoordinator.Value.Appearance?.ShowDirectoryMarker ?? true,
            GetBrowserFileDisplayMode(),
            _settingsCoordinator.Value.Appearance?.DateFormat,
            filterActive,
            filterIndicatorColor);
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
    /// 多列Browser表示やViewer中にかかわらず、_browserApplicationCoordinator.CursorIndex を正本とする。
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
        // 3. 内部カーソル位置 (_browserApplicationCoordinator.CursorIndex)
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
        _browserFileListRenderer.DrawSubItem(e, BuildBrowserFileListRenderOptions());
    }
    private void FileListView_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        // 列ヘッダーはシステムデフォルトのまま
        e.DrawDefault = true;
    }
    // ─── Phase 15A: BrowserPanel 多列描画用ロジック ──────────────────────────
    private void BrowserPanel_Paint(object? sender, PaintEventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser) return;
        _browserFileListRenderer.DrawPanel(
            e.Graphics,
            fileListView.Items,
            browserPanel.Width,
            browserPanel.Height,
            browserPanel.Font,
            _browserApplicationCoordinator.PageStartIndex,
            _browserApplicationCoordinator.CursorIndex,
            _browserApplicationCoordinator.ColumnCount,
            BuildBrowserFileListRenderOptions(),
            _integrationSeam?.Observer is { } observer
                ? observer.OnBrowserMarkGlyphsPainted
                : null);
        DrawCommandHintOverlay(e.Graphics);
        DrawMouseGestureTrail(e.Graphics);
        DrawBrowserFilterFrame(e.Graphics);
    }

    private void DrawBrowserFilterFrame(Graphics graphics)
    {
        if (!BrowserFramePresentation.IsFilterFrameEmphasized(
                _browserApplicationCoordinator.FilterPattern,
                GetActiveTabFilterLock()))
        {
            return;
        }

        BrowserFilterFrameGeometry geometry = BrowserFramePresentation.CalculateGeometry(browserPanel.ClientSize);
        FileListColorResolver.ResolvedColors colors =
            _resolvedColors ?? FileListColorResolver.ResolveColors(_settingsCoordinator.Value);
        Color frameColor = BrowserFilterFrameColorResolver.Resolve(
            colors,
            MidFDColors.BorderLine,
            UiThemeResolver.Resolve(_settingsCoordinator.Value.Appearance).AccentColor);
        using var pen = new Pen(frameColor, 1);
        graphics.DrawRectangle(
            pen,
            geometry.OuterLeft,
            geometry.OuterTop,
            geometry.OuterRight - geometry.OuterLeft,
            geometry.OuterBottom - geometry.OuterTop);
        graphics.DrawRectangle(
            pen,
            geometry.InnerLeft,
            geometry.InnerTop,
            geometry.InnerRight - geometry.InnerLeft,
            geometry.InnerBottom - geometry.InnerTop);
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
            var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settingsCoordinator.Value);
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
    private int GetEffectiveBrowserColumnCount()
    {
        return BrowserLayoutProjection.Calculate(
            browserPanel.Width,
            browserPanel.Height,
            HeaderLayoutHelper.GetMeasuredLineHeight(browserPanel.Font, 4),
            _browserApplicationCoordinator.ColumnCount,
            GetBrowserFileDisplayMode()).EffectiveColumnCount;
    }
    private void BrowserPanel_Resize(object? sender, EventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser)
        {
            RematerializeBrowserPageIfCapacityChanged();
            UpdateInfoPanel();
            browserPanel.Invalidate();
        }
    }
    /// <summary>
    /// カスタムカーソル位置(_browserApplicationCoordinator.CursorIndex)を裏側のListViewに同期し、画面再描画とInfoPanel更新を行う。
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

    private void SyncBrowserSelectionForCommandResult()
    {
        int pageLocalCursorIndex = GetBrowserPageLocalCursorIndex();
        if (pageLocalCursorIndex < 0 || pageLocalCursorIndex >= fileListView.Items.Count)
        {
            return;
        }

        _suppressBrowserSelectionChanged = true;
        try
        {
            fileListView.SelectedItems.Clear();
            ListViewItem item = fileListView.Items[pageLocalCursorIndex];
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
        }
        finally
        {
            _suppressBrowserSelectionChanged = false;
        }

        ApplyBrowserSelectionChangedForCommandResult();
        browserPanel.Invalidate();
    }

    // ─── Phase 3-fix1c: マウス基本操作（単クリック/ダブルクリック） ───
    private void BrowserPanel_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.XButton1 || e.Button == MouseButtons.XButton2)
        {
            return;
        }
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser) return;
        ClearPendingEscExitMarkPersistence();
        int newIndex = CalculateBrowserIndexFromPoint(e.X, e.Y);
        int newPageLocalIndex = newIndex - _browserApplicationCoordinator.PageStartIndex;
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
                    _browserApplicationCoordinator.Selection.Count > 0);
                _browserNavigationWorkflowApplicationCoordinator.ApplyCursorSelection(
                    _browserApplicationCoordinator.PageStartIndex + newPageLocalIndex,
                    _browserApplicationCoordinator.ItemsPerPage);
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
                    _browserApplicationCoordinator.Selection.Snapshot(),
                    newPageLocalIndex,
                    fileListView.Items.Count,
                    item.Tag as string,
                    item.Text == "..");
                if (GetBrowserPageLocalCursorIndex() != targetResolution.TargetIndex)
                {
                    _browserNavigationWorkflowApplicationCoordinator.ApplyCursorSelection(
                        _browserApplicationCoordinator.PageStartIndex + targetResolution.TargetIndex,
                        _browserApplicationCoordinator.ItemsPerPage);
                    SyncBrowserSelection();
                }
                ShowBrowserItemContextMenu(e.Location, item, targetResolution);
                return;
            }

            ShowBrowserBlankContextMenu(e.Location);
        }
    }
    private ToolStripMenuItem? Create7ZipMenu(
        SelectionResult res,
        IReadOnlyDictionary<string, bool>? passivePathKinds = null)
    {
        // 7-Zip のベースパスを設定値 -> 自動検索の順で取得
        string? base7zPath = SevenZipService.ResolveCliExecutable(_settingsCoordinator.Value.SevenZip.ExePath);
        if (string.IsNullOrEmpty(base7zPath))
        {
            base7zPath = SevenZipService.FindSevenZip();
        }
        if (string.IsNullOrEmpty(base7zPath)) return null;
        string sevenZipDir = Path.GetDirectoryName(base7zPath) ?? string.Empty;
        if (string.IsNullOrEmpty(sevenZipDir)) return null;
        string sevenZipFM = Path.Combine(sevenZipDir, "7zFM.exe");
        // 展開・圧縮用のバイナリ (GUI版があれば優先使用。なければベースパス)
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
                    _ = ExecuteArchiveExtractAsync(path, dir);
                }));
                menu.DropDownItems.Add(new ToolStripMenuItem($"\"{nameWithoutExt}\\\" に展開", null, (s, e) =>
                {
                    if (GuardReadOnlyBrowserTab("解凍")) return;
                    _ = ExecuteArchiveExtractAsync(path, Path.Combine(dir, nameWithoutExt));
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
        BrowserPassiveSelectionFacts passiveFacts = BrowserPassiveSelectionFacts.Resolve(
            res,
            passivePathKinds,
            allowFileSystemFallback: false);
        bool canHash = passiveFacts.HasHashableSelection;
        var hashMenu = new ToolStripMenuItem("CRC/SHA")
        {
            Enabled = canHash
        };
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("CRC-32", null, (s, e) => _ = ExecuteCommandWithHashAlgorithmFromUi(CommandIds.ArchiveHash, CommandScope.Browser, "Menu.Tools.ArchiveHash.Crc32", SevenZipHashAlgorithm.Crc32)));
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("CRC-64", null, (s, e) => _ = ExecuteCommandWithHashAlgorithmFromUi(CommandIds.ArchiveHash, CommandScope.Browser, "Menu.Tools.ArchiveHash.Crc64", SevenZipHashAlgorithm.Crc64)));
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("SHA-1", null, (s, e) => _ = ExecuteCommandWithHashAlgorithmFromUi(CommandIds.ArchiveHash, CommandScope.Browser, "Menu.Tools.ArchiveHash.Sha1", SevenZipHashAlgorithm.Sha1)));
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("SHA-256", null, (s, e) => _ = ExecuteCommandWithHashAlgorithmFromUi(CommandIds.ArchiveHash, CommandScope.Browser, "Menu.Tools.ArchiveHash.Sha256", SevenZipHashAlgorithm.Sha256)));
        hashMenu.DropDownItems.Add(new ToolStripSeparator());
        hashMenu.DropDownItems.Add(new ToolStripMenuItem("すべて (*)", null, (s, e) => _ = ExecuteCommandWithHashAlgorithmFromUi(CommandIds.ArchiveHash, CommandScope.Browser, "Menu.Tools.ArchiveHash.All", SevenZipHashAlgorithm.All)));
        menu.DropDownItems.Add(hashMenu);
        menu.DropDownItems.Add(new ToolStripSeparator());
        var packItem = new ToolStripMenuItem("圧縮...", null, (s, e) => ExecuteCommandFromUi(CommandIds.ArchivePack, CommandScope.Browser, "Menu.Tools.Pack.Direct"))
        {
            Enabled = !isReadOnly && res.Count > 0
        };
        menu.DropDownItems.Add(packItem);
        var unpackItem = new ToolStripMenuItem("解凍...", null, (s, e) => ExecuteCommandFromUi(CommandIds.ArchiveUnpack, CommandScope.Browser, "Menu.Tools.Unpack.Direct"))
        {
            Enabled = !isReadOnly && res.Count > 0 && res.FullPaths.Any(FileOperationApplicationCoordinator.IsArchiveTarget)
        };
        menu.DropDownItems.Add(unpackItem);
        var packEachFolderItemSub = new ToolStripMenuItem("個別圧縮...", null, (s, e) =>
            _ = _fileOperationApplicationCoordinator.TryStart(
                FileOperationCommandKind.Pack,
                this,
                forcePackEachFolderIndividually: true))
        {
            Enabled = !isReadOnly && FileOperationApplicationCoordinator.CanPackEachFolderIndividually(res)
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
                _fileOperationApplicationCoordinator.TryStartQuickPack(res, this);
            }
        })
        {
            Enabled = !isReadOnly && res.Count > 0
        };
        menu.DropDownItems.Add(quickPackItem);
        return menu;
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
    private bool ExecuteProperties(SelectionResult res)
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
                return true;
            }
            catch (Exception ex)
            {
                LogService.Error($"ExecuteProperties 失敗: {ex.Message}");
                return false;
            }
        }
        else
        {
            ShowStatusMessage("複数プロパティ一括表示は未対応です。");
            return true;
        }
    }
    private void BrowserPanel_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser) return;
        if (e.Button != MouseButtons.Left) return;
        int newIndex = CalculateBrowserIndexFromPoint(e.X, e.Y);
        int newPageLocalIndex = newIndex - _browserApplicationCoordinator.PageStartIndex;
        if (newPageLocalIndex >= 0 && newPageLocalIndex < fileListView.Items.Count)
        {
            _browserNavigationWorkflowApplicationCoordinator.ApplyCursorSelection(newIndex, _browserApplicationCoordinator.ItemsPerPage);
            SyncBrowserSelection();
            ListViewItem item = fileListView.Items[newPageLocalIndex];
            var (commandId, selectionSnapshot) = ResolveBrowserDoubleClickCommand(item);
            _ = ExecuteCommandFromUi(commandId, CommandScope.Browser, "Browser.MouseDoubleClick", selectionSnapshot);
        }
    }

    internal static (string CommandId, SelectionResult? SelectionSnapshot) ResolveBrowserDoubleClickCommand(ListViewItem item)
    {
        if (item.Text == "..")
            return (CommandIds.BrowserNavigateParent, null);

        string? itemPath = item.Tag as string;
        string commandId = itemPath != null && Directory.Exists(itemPath)
            ? CommandIds.BrowserExecute
            : CommandIds.BrowserDefaultOpen;
        // Cursor synchronization does not define the command target; existing marks remain untouched.
        var selection = new SelectionResult(itemPath == null ? Array.Empty<string>() : new[] { itemPath }, hasMarkedSelection: false);
        return (commandId, selection);
    }

    internal static (string CommandId, SelectionResult? SelectionSnapshot) ResolveBrowserEnterCommand(ListViewItem? item)
    {
        if (item == null)
            return (CommandIds.BrowserExecute, null);

        if (item.Text == "..")
            return (CommandIds.BrowserNavigateParent, null);

        string? itemPath = item.Tag as string;
        var selection = new SelectionResult(itemPath == null ? Array.Empty<string>() : new[] { itemPath }, hasMarkedSelection: false);
        return (CommandIds.BrowserExecute, selection);
    }

    private void ToggleBrowserMouseMarkByIndex(int index)
    {
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            return;
        }

        string? path = TryGetMarkableBrowserPathByIndex(index);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_browserApplicationCoordinator.Selection.Contains(path))
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
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            return;
        }

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

        int addedCount = _browserTabWorkflowApplicationCoordinator.AddMarksAndSync(paths);
        if (addedCount > 0)
        {
            ApplyCommittedMarkMutationEffects(addedCount, exactDeltaApplied: false);
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || fileListView.Items.Count == 0) return;
        int itemsPerPage = GetBrowserItemsPerPage();
        if (itemsPerPage <= 0) return;
        int totalItems = _browserApplicationCoordinator.TotalItemCount > 0 ? _browserApplicationCoordinator.TotalItemCount : fileListView.Items.Count;
        int currentPage = _browserApplicationCoordinator.CursorIndex / itemsPerPage;
        int offsetInPage = _browserApplicationCoordinator.CursorIndex % itemsPerPage;
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
        {
            LogService.Info(DragDropDataObjectDiagnosticHelper.GetDiagnosticLog("DragDrop", _viewerApplicationCoordinator.Mode.ToString(), IsActiveBrowserTabReadOnly(), _fileOperationApplicationCoordinator.IsClipboardBusy, false, e.Data, e.Effect, "uiModeNotBrowser"));
            return;
        }
        if (IsActiveBrowserTabReadOnly())
        {
            LogService.Info(DragDropDataObjectDiagnosticHelper.GetDiagnosticLog("DragDrop", _viewerApplicationCoordinator.Mode.ToString(), IsActiveBrowserTabReadOnly(), _fileOperationApplicationCoordinator.IsClipboardBusy, false, e.Data, e.Effect, "readOnlyBlocked"));
        }
        if (GuardReadOnlyBrowserTab("ファイル取り込み")) return;
        bool hasInternalDragArchiveMarker = HasInternalDragArchiveMarker(e.Data);
        int fileDropCount = GetFileDropCount(e.Data);
        LogService.Info($"[DragArchive] DragDrop: internalMarkerPresent={hasInternalDragArchiveMarker}, fileDropCount={fileDropCount}, clipboardBusy={_fileOperationApplicationCoordinator.IsClipboardBusy}");
        LogService.Info(DragDropDataObjectDiagnosticHelper.GetDiagnosticLog("DragDrop", _viewerApplicationCoordinator.Mode.ToString(), IsActiveBrowserTabReadOnly(), _fileOperationApplicationCoordinator.IsClipboardBusy, hasInternalDragArchiveMarker, e.Data, e.Effect, "dropReceived"));
        if (hasInternalDragArchiveMarker)
        {
            return;
        }
        if (_fileOperationApplicationCoordinator.IsClipboardBusy)
        {
            ShowStatusMessage("処理中のため画像取り込みできません。");
            return;
        }
        if (string.IsNullOrEmpty(_browserApplicationCoordinator.CurrentPath)) return;

        // Systematically classify and resolve intent using resolver, respecting remembered decision
        var decision = ResolveIncomingDropDecision(e);
        if (decision.Intent == BrowserDragDropIntent.None)
        {
            ShowStatusMessage("ドロップ不可な操作または状態です。");
            return;
        }

        bool isOutlookAttachmentDrop = OutlookAttachmentDropService.IsOutlookAttachmentDrop(e.Data);
        if (isOutlookAttachmentDrop)
        {
            _integrationSeam?.Observer?.OnOutlookAttachmentDropRouted();
            var attachmentNames = OutlookAttachmentDropService.GetAttachmentNames(e.Data!);
            if (attachmentNames.Count > 0)
            {
                Func<string, OverwriteConfirmResult> confirmOverwrite = (fileName) =>
                {
                    var overwriteMsg = FileOperationPresentationHelper.GetOverwriteConfirmationMessage(fileName);
                    var overwriteResult = MessageBox.Show(overwriteMsg, "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (overwriteResult == DialogResult.Yes) return OverwriteConfirmResult.Yes;
                    if (overwriteResult == DialogResult.No) return OverwriteConfirmResult.No;
                    return OverwriteConfirmResult.Cancel;
                };

                OutlookAttachmentDropResult dropResult = OutlookAttachmentDropService.ProcessDrop(e.Data!, _browserApplicationCoordinator.CurrentPath, confirmOverwrite);
                if (dropResult.AnySucceeded)
                {
                    if (dropResult.AllSucceeded)
                    {
                        ShowStatusMessage("仮想ファイルのコピーが完了しました。");
                    }

                    string? focusTarget = dropResult.SuccessfulFileNames.Count > 0
                        ? dropResult.SuccessfulFileNames[0]
                        : null;
                    LoadDirectory(_browserApplicationCoordinator.CurrentPath, focusTarget);
                }
            }
            return;
        }

        if (TryHandleBrowserFileDrop(e, decision))
        {
            return;
        }

        if (BrowserImageDropService.TryGetImage(e.Data, out var image) && image != null)
        {
            try
            {
                using (image)
                {
                    string savedPath = BrowserImageDropService.SavePngToDirectory(image, _browserApplicationCoordinator.CurrentPath);
                    string fileName = Path.GetFileName(savedPath);
                    LoadDirectory(_browserApplicationCoordinator.CurrentPath, GetCreatedItemFocusTarget(fileName));
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
                string savedPath = BrowserDroppedImageDownloadService.DownloadToDirectory(resolvedImageUrl, _browserApplicationCoordinator.CurrentPath, suggestedFileName);
                string fileName = Path.GetFileName(savedPath);
                LoadDirectory(_browserApplicationCoordinator.CurrentPath, GetCreatedItemFocusTarget(fileName));
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || e.Button != MouseButtons.Right || sender is not Control control)
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser) return;
        if (e.Button == MouseButtons.Right)
        {
            _browserRightStartPoint = ToMainFormClient(browserPanel, e.Location);
            _browserRightItemIndex = -1;
            _browserRightItemPath = null;
            _browserRightSelectionSnapshot = _browserApplicationCoordinator.Selection.Snapshot();
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
                int localIndex = _dragCandidateIndex - _browserApplicationCoordinator.PageStartIndex;
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
        int dragCandidateLocalIndex = _dragCandidateIndex - _browserApplicationCoordinator.PageStartIndex;
        if (dragCandidateLocalIndex >= 0 && dragCandidateLocalIndex < fileListView.Items.Count && _browserApplicationCoordinator.CursorIndex != _dragCandidateIndex)
        {
            InvalidateRecentMultiMarkIntent();
            _browserNavigationWorkflowApplicationCoordinator.ApplyCursorSelection(_dragCandidateIndex, _browserApplicationCoordinator.ItemsPerPage);
            SyncBrowserSelection();
        }
    }
    private void BrowserTabStrip_TabReordered(object? sender, BrowserTabStripReorderEventArgs e)
    {
        BrowserTabStripItem? fromItem = sender is BrowserTabNavigation navigation
            ? navigation.GetTabItem(navigation.SelectedCategoryIndex, e.FromIndex)
            : _browserTabStrip?.GetTabItem(e.FromIndex);
        BrowserTabStripItem? toItem = sender is BrowserTabNavigation targetNavigation
            ? targetNavigation.GetTabItem(targetNavigation.SelectedCategoryIndex, e.ToIndex)
            : _browserTabStrip?.GetTabItem(e.ToIndex);
        if (fromItem?.Kind != BrowserTabStripItemKind.Browser || toItem?.Kind != BrowserTabStripItemKind.Browser) return;

        IReadOnlyList<BrowserTabState> categoryTabs = sender is BrowserTabNavigation
            ? GetBrowserTabContextStates(_browserApplicationCoordinator.Workspace.ActiveCategoryId)
            : _browserApplicationCoordinator.Workspace.TabStates;
        int fromIndex = fromItem.BrowserTabId is { } fromId
            ? GetBrowserTabIndexById(categoryTabs, fromId)
            : e.FromIndex;
        int toIndex = toItem.BrowserTabId is { } toId
            ? GetBrowserTabIndexById(categoryTabs, toId)
            : e.ToIndex;
        if (fromIndex < 0 || toIndex < 0) return;
        if (!_browserTabWorkflowApplicationCoordinator.ReorderTab(
            fromIndex,
            toIndex,
            BuildBrowserTabStateFromCurrentUi()))
        {
            return;
        }
        RefreshBrowserTabHeaders();
        browserPanel.Focus();
        ShowStatusMessage("タブ順を入れ替えました。");
    }
    private void BrowserTabStrip_CategoryReordered(object? sender, BrowserTabStripReorderEventArgs e)
    {
        if (!_browserCategoryWorkflowApplicationCoordinator.ReorderByIndex(
            e.FromIndex,
            e.ToIndex,
            BuildBrowserTabStateFromCurrentUi()))
        {
            return;
        }
        _lastBrowserTabHeaderSnapshotKey = null;
        RefreshBrowserTabHeaders();
        ShowStatusMessage("カテゴリ順を入れ替えました。");
    }
    private void BrowserTabStrip_TabListDropDownOpening(object? sender, Point e)
    {
        ContextMenuStrip menu = new();
        IReadOnlyList<BrowserTabListCategorySnapshot> categories = _browserApplicationCoordinator.Workspace.BuildTabListSnapshot(
            _settingsCoordinator.Value.Session ?? new SessionSettings());

        foreach (BrowserTabListCategorySnapshot category in categories)
        {
            ToolStripMenuItem categoryMenuItem = new(category.DisplayName)
            {
                Checked = category.IsActive
            };

            if (category.Tabs.Count > 0)
            {
                foreach (BrowserTabListTabSnapshot tab in category.Tabs)
                {
                    string tabTitle = string.IsNullOrWhiteSpace(tab.Title) ? "新しいタブ" : tab.Title;
                    if (tab.IsLocked)
                    {
                        tabTitle = "■ " + tabTitle;
                    }
                    if (tab.IsReadOnly)
                    {
                        tabTitle = "[RO] " + tabTitle;
                    }

                    ToolStripMenuItem tabMenuItem = new($"{tab.Index + 1}: {tabTitle}")
                    {
                        Checked = category.IsActive && tab.IsSelected,
                        ToolTipText = tab.CurrentPath
                    };

                    tabMenuItem.Click += (_, _) =>
                    {
                        SwitchBrowserTabCategory(category.CategoryId, tab.Index);
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
                SwitchBrowserTabCategory(category.CategoryId);
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser) return;
        if (_browserOutgoingDragInProgress) return;
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
                : _browserApplicationCoordinator.Selection.Snapshot();
            bool isBlankDrag = _blankDragCandidate && _dragCandidateIndex == -1;
            int dragCandidateLocalIndex = _dragCandidateIndex - _browserApplicationCoordinator.PageStartIndex;
            string? dragCandidatePath = (dragCandidateLocalIndex >= 0 && dragCandidateLocalIndex < fileListView.Items.Count)
                ? fileListView.Items[dragCandidateLocalIndex].Tag as string
                : null;
            if (e.Button == MouseButtons.Right && !string.IsNullOrWhiteSpace(_browserRightItemPath))
            {
                dragCandidatePath = _browserRightItemPath;
            }
            if (dragSelection.Count == 0 && dragCandidateLocalIndex >= 0 && dragCandidateLocalIndex < fileListView.Items.Count)
            {
                if (_browserApplicationCoordinator.CursorIndex != _dragCandidateIndex)
                {
                    _browserNavigationWorkflowApplicationCoordinator.ApplyCursorSelection(_dragCandidateIndex, _browserApplicationCoordinator.ItemsPerPage);
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
                if (_fileOperationApplicationCoordinator.IsClipboardBusy)
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

                var fileOperations = _settingsCoordinator.Value.FileOperations;
                bool isDragArchiveEnabled = fileOperations.EnableDragArchiveHandoff;
                if (isBlankDrag && !isDragArchiveEnabled)
                {
                    _dragStartPoint = Point.Empty;
                    _dragCandidateIndex = -1;
                    _blankDragCandidate = false;
                    _dragArchiveHandoffRequested = false;
                    return;
                }
                if (_browserOutgoingDragInProgress) return;
                _browserOutgoingDragInProgress = true;
                try
                {
                string logMsg = $"[DragArchive] dragPaths.Count={dragPaths.Count}, _browserApplicationCoordinator.Selection.Count={_browserApplicationCoordinator.Selection.Count}, enableDragArchiveHandoff={isDragArchiveEnabled}, includeManifest={fileOperations.IncludeDragZipManifest}, mouseDownModifier={_dragArchiveHandoffRequested}, currentModifier={((Control.ModifierKeys & (Keys.Shift | Keys.Control)) != 0)}, archiveDragRequested={isShiftOrCtrl}, candidatePath='{dragCandidatePath}'";
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
                finally
                {
                    CleanupBrowserDragState(e.Button);
                    _browserOutgoingDragInProgress = false;
                }
                return;
            }
            // 開始した（または条件に合わず開始できなかった）ので状態をクリア
            CleanupBrowserDragState(e.Button);
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

    private void CleanupBrowserDragState(MouseButtons button)
    {
        if (button == MouseButtons.Right)
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || _settingsCoordinator.Value.Input?.EnableMouseGestures != true)
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
        if (string.IsNullOrWhiteSpace(gesture) || _viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || _settingsCoordinator.Value.Input?.EnableMouseGestures != true)
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
                _settingsCoordinator.Value.Input?.MouseGestureCommandMap,
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
    private void RestoreLastClosedBrowserTab()
    {
        BrowserClosedTabRestoreExecution execution = _browserTabWorkflowApplicationCoordinator.ExecuteRestoreLastClosedTab(
            _browserApplicationCoordinator.CurrentPath,
            BuildBrowserTabStateFromCurrentUi(validateMarks: true),
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());
        BrowserClosedTabRestoreTransition transition = execution.Transition;
        if (transition.Kind == BrowserClosedTabRestoreKind.None)
        {
            ShowStatusMessage("Gesture: 復元できる閉じたタブはありません。");
            return;
        }
        if (transition.Kind == BrowserClosedTabRestoreKind.LimitReached)
        {
            ShowStatusMessage($"タブは最大{_browserTabWorkflowApplicationCoordinator.MaxTabCount}個までです。");
            _browserTabStrip?.FlashLimitReached();
            TryPlayBrowserTabLimitBeep();
            return;
        }
        if (!execution.Restored || execution.TabSwitch is not { } tabSwitch)
        {
            return;
        }
        RefreshBrowserTabHeaders();
        ApplyBrowserTabSwitchResult(tabSwitch);
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
        BrowserLayoutMetrics layout = BrowserLayoutProjection.Calculate(
            browserPanel.Width,
            browserPanel.Height,
            itemHeight,
            _browserApplicationCoordinator.ColumnCount,
            GetBrowserFileDisplayMode(),
            BrowserLayoutProjection.GetLeadingPresentationSlotCount(
                TabFilterLockService.IsActive(
                    _browserApplicationCoordinator.FilterPattern,
                    GetActiveTabFilterLock())));
        rowsPerColumn = layout.RowsPerColumn;
        return layout.ItemsPerPage;
    }
    private int GetBrowserItemsPerPageForColumn(int columnCount)
    {
        return CreateBrowserLayoutProjectionInput().GetItemsPerPage(columnCount);
    }
    private int CalculateBrowserIndexFromPoint(int x, int y)
    {
        int itemHeight = HeaderLayoutHelper.GetMeasuredLineHeight(browserPanel.Font, 4);
        BrowserLayoutMetrics layout = BrowserLayoutProjection.Calculate(
            browserPanel.Width,
            browserPanel.Height,
            itemHeight,
            _browserApplicationCoordinator.ColumnCount,
            GetBrowserFileDisplayMode(),
            BrowserLayoutProjection.GetLeadingPresentationSlotCount(
                TabFilterLockService.IsActive(
                    _browserApplicationCoordinator.FilterPattern,
                    GetActiveTabFilterLock())));
        return BrowserLayoutProjection.GetGlobalIndexFromPoint(
            x,
            y,
            _browserApplicationCoordinator.PageStartIndex,
            fileListView.Items.Count,
            layout);
    }
    private void UpdateBrowserFileNameToolTip(Point location)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || fileListView.Items.Count == 0)
        {
            HideBrowserFileNameToolTip();
            return;
        }

        int index = CalculateBrowserIndexFromPoint(location.X, location.Y);
        int pageLocalIndex = index - _browserApplicationCoordinator.PageStartIndex;
        if (pageLocalIndex < 0 || pageLocalIndex >= fileListView.Items.Count)
        {
            HideBrowserFileNameToolTip();
            return;
        }

        if (!TryGetBrowserItemLayoutBounds(index, out Rectangle hoverBounds, out Rectangle nameBounds, out bool? isAlignedNameEllipsized))
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
        if (!(isAlignedNameEllipsized ?? IsBrowserItemNameEllipsized(item, nameBounds)))
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



    private bool TryGetBrowserItemLayoutBounds(int index, out Rectangle hoverBounds, out Rectangle nameBounds)
    {
        return TryGetBrowserItemLayoutBounds(index, out hoverBounds, out nameBounds, out _);
    }

    private bool TryGetBrowserItemLayoutBounds(
        int index,
        out Rectangle hoverBounds,
        out Rectangle nameBounds,
        out bool? isAlignedNameEllipsized)
    {
        using Graphics graphics = browserPanel.CreateGraphics();
        return _browserFileListRenderer.TryGetItemLayoutBounds(
            graphics,
            browserPanel.Width,
            browserPanel.Height,
            browserPanel.Font,
            fileListView.Items,
            index,
            _browserApplicationCoordinator.PageStartIndex,
            _browserApplicationCoordinator.ColumnCount,
            BuildBrowserFileListRenderOptions(),
            out hoverBounds,
            out nameBounds,
            out isAlignedNameEllipsized);
    }
    private bool IsBrowserItemNameEllipsized(ListViewItem item, Rectangle nameBounds)
    {
        using Graphics graphics = browserPanel.CreateGraphics();
        return _browserFileListRenderer.IsItemNameEllipsized(
            graphics,
            item,
            nameBounds,
            BuildBrowserFileListRenderOptions(),
            GetItemFullName(item));
    }
    private bool ExecuteCommandFromUi(string commandId, CommandScope scope, string source, SelectionResult? selectionSnapshot = null, string? categoryId = null, int? contextTabIndex = null, string? contextTargetPath = null, string? fileExtension = null)
        => ExecuteCommandCore(commandId, scope, source, selectionSnapshot, categoryId, contextTabIndex, contextTargetPath, fileExtension, null, null);

    private bool ExecuteCommandFromUiWithRuntimeTarget(
        string commandId,
        CommandScope scope,
        string source,
        Guid runtimeTargetId,
        int? contextTabIndex = null)
        => ExecuteCommandCore(commandId, scope, source, null, null, contextTabIndex, null, null, null, runtimeTargetId);

    private bool ExecuteBrowserTabGroupCommandFromUi(
        string commandId,
        string source,
        string categoryId,
        Guid browserTabId,
        Guid? groupId = null)
        => ExecuteCommandCore(
            commandId,
            CommandScope.Browser,
            source,
            null,
            categoryId,
            null,
            null,
            null,
            null,
            groupId,
            browserTabId);

    private bool ExecuteCommandWithHashAlgorithmFromUi(
        string commandId,
        CommandScope scope,
        string source,
        SevenZipHashAlgorithm hashAlgorithm,
        SelectionResult? selectionSnapshot = null)
        => ExecuteCommandCore(commandId, scope, source, selectionSnapshot, null, null, null, null, hashAlgorithm, null);

    private bool ExecuteCommandCore(
        string commandId,
        CommandScope scope,
        string source,
        SelectionResult? selectionSnapshot,
        string? categoryId,
        int? contextTabIndex,
        string? contextTargetPath,
        string? fileExtension,
        SevenZipHashAlgorithm? hashAlgorithm,
        Guid? runtimeTargetId,
        Guid? contextBrowserTabId = null)
    {
        if (string.Equals(commandId, CommandIds.BrowserPreview, StringComparison.OrdinalIgnoreCase))
        {
            ClearUnifiedSearchPreviewReturnContext();
            ClearNameSearchInspectionReturnContext();
        }
        if (_unifiedSearchSession is { } searchSession
            && string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, searchSession.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(commandId, CommandIds.BrowserTabNext, StringComparison.OrdinalIgnoreCase))
            return NavigateAdjacentBrowserTabIncludingSearch(+1);
        if (_unifiedSearchSession is { } previousSearchSession
            && string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, previousSearchSession.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(commandId, CommandIds.BrowserTabPrevious, StringComparison.OrdinalIgnoreCase))
            return NavigateAdjacentBrowserTabIncludingSearch(-1);
        var context = new CommandExecutionContext
        {
            Scope = scope,
            Source = source,
            SelectionSnapshot = selectionSnapshot,
            ContextTargetPath = contextTargetPath,
            CategoryId = categoryId,
            ContextTabIndex = contextTabIndex,
            ContextBrowserTabId = contextBrowserTabId,
            RuntimeTargetId = runtimeTargetId,
            FileExtension = fileExtension,
            HashAlgorithm = hashAlgorithm
        };
        if (!_commandDispatcher.CanDispatch(commandId, context))
        {
            return false;
        }

        CommandApplicationResult result = _commandApplicationCoordinator.TryExecute(commandId, context);
        if (!result.Handled)
        {
            return false;
        }

        if (!string.Equals(commandId, CommandIds.BrowserNamePrefixJump, StringComparison.OrdinalIgnoreCase))
        {
            ClearBrowserNamePrefixJump();
        }

        return ApplyCommandApplicationResult(result);
    }

    private bool ApplyCommandApplicationResult(CommandApplicationResult result)
    {
        if (!result.Handled)
        {
            return false;
        }

        if (result.InteractionRequest is { } request)
        {
            CommandApplicationInteractionResponse? response = ResolveCommandInteraction(request);
            return response != null && ApplyCommandApplicationResult(
                _commandApplicationCoordinator.Continue(request, response));
        }

        return result.Effect == null || ApplyCommandApplicationEffect(result.Effect);
    }

    private CommandApplicationInteractionResponse? ResolveCommandInteraction(
        CommandApplicationInteractionRequest request)
    {
        switch (request)
        {
            case SortCommandInteractionRequest sort:
            {
                SortDialog.SortResult? result = SortDialog.Show(
                    sort.CurrentSort.ToString(),
                    sort.CurrentAscending);
                if (result == null)
                {
                    return new SortCommandInteractionResponse(false, sort.CurrentSort, sort.CurrentAscending);
                }

                SortKind selectedKind = result.Kind switch
                {
                    "Name" => SortKind.Name,
                    "Ext" => SortKind.Ext,
                    "Size" => SortKind.Size,
                    "Date" => SortKind.Date,
                    _ => sort.CurrentSort
                };
                return new SortCommandInteractionResponse(true, selectedKind, result.Ascending);
            }
            case UnifiedFilterFindCommandInteractionRequest unified:
            {
                UnifiedFilterFindDialogResult? result = UnifiedFilterFindDialog.Show(
                    this,
                    new UnifiedFilterFindInteractionRequest(
                        unified.TargetTabIndex,
                        unified.RootPath,
                        unified.CurrentCriteria.Clone(),
                        unified.InitialMode));
                return result == null
                    ? new UnifiedFilterFindCommandInteractionResponse(false, unified.InitialMode, unified.CurrentCriteria.Clone())
                    : new UnifiedFilterFindCommandInteractionResponse(true, result.Mode, result.Criteria);
            }
            case TreeCommandInteractionRequest tree:
                return new TreeCommandInteractionResponse(TreeDialog.Show(tree.CurrentPath));
            case BrowserTabVisitHistoryCommandInteractionRequest history:
            {
                BrowserTabVisitHistoryDialogResult? result = BrowserTabVisitHistoryDialog.Show(this, history.Items);
                return result == null
                    ? new BrowserTabVisitHistoryCommandInteractionResponse(false, BrowserTabVisitHistoryListDirection.Current, 0)
                    : new BrowserTabVisitHistoryCommandInteractionResponse(true, result.Direction, result.Depth);
            }
            case QuickAccessCommandInteractionRequest quickAccess:
            {
                HideCommandHintOverlay("ResolveCommandInteraction.QuickAccess");
                QuickAccessOpenDiagnostics diagnostics = new(QuickAccessOpenDiagnostics.CreateOperationId());
                diagnostics.LogOpenStart(quickAccess.CurrentPath, quickAccess.Store);
                QuickAccessDialogResult result = QuickAccessDialog.Show(
                    this,
                    quickAccess.Store,
                    quickAccess.CurrentPath,
                    quickAccess.HistoryEntries,
                    diagnostics);
                return new QuickAccessCommandInteractionResponse(
                    result.Action switch
                    {
                        QuickAccessDialogCloseAction.Navigate => CommandQuickAccessAction.Navigate,
                        QuickAccessDialogCloseAction.SaveOnly => CommandQuickAccessAction.SaveOnly,
                        _ => CommandQuickAccessAction.Cancel
                    },
                    result.SelectedEntry,
                    result.UpdatedStore);
            }
            case BrowserDerivedTabNavigationInteractionRequest:
                return new BrowserDerivedTabNavigationInteractionResponse(
                    ShowBrowserDerivedTabNavigationConfirmation());
            case LogdiskCommandInteractionRequest logdisk:
                return new LogdiskCommandInteractionResponse(
                    LogdiskDialog.Show(logdisk.DefaultPath, logdisk.Candidates));
            case ArchiveListCommandInteractionRequest archive:
            {
                using var dialog = new ArchiveListDialog(
                    archive.ArchivePath,
                    archive.Contents.Entries,
                    archive.CurrentPath,
                    archive.IsReadOnly,
                    archive.DateFormat,
                    archive.SizeFormat,
                    archive.Contents.SevenZipPath);
                dialog.ShowDialog(this);
                return new ArchiveListCommandInteractionResponse(dialog.PendingExtractRequest);
            }
            case CategoryRenameCommandInteractionRequest rename:
            {
                string? displayName = SimpleInputDialog.ShowNullable(
                    "カテゴリ名を入力してください。",
                    "カテゴリ名変更",
                    rename.DisplayName);
                return new CategoryRenameCommandInteractionResponse(
                    !string.IsNullOrWhiteSpace(displayName),
                    displayName);
            }
            case CategoryDeleteCommandInteractionRequest delete:
                return new CategoryDeleteCommandInteractionResponse(
                    MessageBox.Show(
                        $"カテゴリ '{delete.DisplayName}' を削除します。よろしいですか？",
                        "カテゴリ削除",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) == DialogResult.OK);
            case TabCloseCategoryRemovalInteractionRequest tabClose:
                return new TabCloseCategoryRemovalInteractionResponse(
                    MessageBox.Show(
                        $"このタブを閉じると、カテゴリ「{tabClose.DisplayName}」も削除されます。\nカテゴリごと削除しますか？",
                        "タブを閉じる",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) == DialogResult.OK);
            default:
                return null;
        }
    }

    CommandApplicationShellState ICommandApplicationShellPort.CaptureState()
    {
        ListViewItem? currentItem = GetCurrentBrowserItem();
        return new CommandApplicationShellState(
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            IsCurrentDirectoryBusy(),
            _browserApplicationCoordinator.CurrentPath,
            BuildBrowserTabStateFromCurrentUi(),
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState(),
            _settingsCoordinator.Value.BrowserTabs.LayoutMode,
            GetBrowserItemsPerPage(),
            fileListView.Items.Count,
            _browserApplicationCoordinator.TotalItemCount,
            currentItem?.Text,
            currentItem?.Tag as string,
            _settingsCoordinator.Value.SevenZip?.ExePath,
            IsActiveBrowserTabReadOnly(),
            _settingsCoordinator.Value.Appearance?.DateFormat,
            _settingsCoordinator.Value.Appearance?.SizeFormat,
            _settingsCoordinator.Value.BrowserTabs.MultiDirectoryOpenConfirmationThreshold);
    }

    IReadOnlyList<string> ICommandApplicationShellPort.GetBulkMarkTargetPaths(bool includeDirectories) =>
        CollectBulkMarkTargetPaths(includeDirectories);

    IReadOnlyList<string> ICommandApplicationShellPort.GetSharedLocationCandidates() =>
        GetSharedLocationCandidates();

    bool ICommandApplicationShellPort.ConfirmMultiDirectoryOpen(int directoryCount, int threshold)
    {
        return MessageBox.Show(
            this,
            $"{directoryCount}件のディレクトリを新しいタブで開きますか？",
            $"複数ディレクトリを開く（{threshold}件以上）",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private bool ApplyBrowserCategorySwitchResult(BrowserCategorySwitchWorkflowResult completion)
    {
        if (completion.Kind == BrowserCategorySwitchWorkflowResultKind.NotAvailable)
        {
            return false;
        }

        if (completion.Kind == BrowserCategorySwitchWorkflowResultKind.NoOp)
        {
            ClearBrowserTabCategoryContextState();
            RefreshBrowserTabHeaders();
            UpdateMenuStripState();
            _browserTabStrip?.Invalidate();
            _browserTabHostPanel?.Invalidate();
            FocusBrowserFileList();
            return true;
        }

        BrowserDirectoryLoadApplicationResult? directoryLoad = completion.DirectoryLoad;
        if (directoryLoad is not { Succeeded: true, Result: not null } || !completion.Committed)
        {
            if (directoryLoad?.Error != null)
            {
                NotifyDirectoryLoadFailure(directoryLoad.Value.Error);
            }
            return false;
        }

        ApplyCompletedBrowserTabMarkState(completion.State!, completion.SkippedMarkCount);
        ClearBrowserTabContextState();
        ClearBrowserTabCategoryContextState();
        _isSwitchingBrowserTab = true;
        try
        {
            ApplyPreparedBrowserTabSwitchDirectoryLoadForCommandResult(
                directoryLoad.Value,
                () =>
                {
                    ApplyBrowserTabCategoryPresentation(completion.TargetTabIndex);
                    FocusBrowserFileList();
                });
        }
        finally
        {
            _isSwitchingBrowserTab = false;
        }

        ApplyDirectoryPostLoadEffects(completion.PostLoadEffects);
        UpdateMenuStripState();
        ShowStatusMessage("カテゴリを切り替えました。");
        return true;
    }

    private bool ApplyBrowserTabSwitchResultForCommandResult(
        BrowserTabSwitchWorkflowResult completion,
        int previousActiveTabIndex = -1,
        int? requestedIndex = null)
    {
        if (completion.Kind == BrowserTabSwitchWorkflowResultKind.NotAvailable)
        {
            if (previousActiveTabIndex >= 0)
            {
                RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
            }
            return false;
        }

        BrowserDirectoryLoadApplicationResult? directoryLoad = completion.DirectoryLoad;
        if (directoryLoad is not { Succeeded: true, Result: not null })
        {
            if (directoryLoad?.Error != null)
            {
                NotifyDirectoryLoadFailure(directoryLoad.Value.Error);
            }
            if (previousActiveTabIndex >= 0)
            {
                RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
            }
            return false;
        }

        if (!completion.Committed)
        {
            if (previousActiveTabIndex >= 0)
            {
                RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
            }
            return false;
        }

        int targetIndex = requestedIndex ?? completion.TargetTabIndex;
        _isSwitchingBrowserTab = true;
        try
        {
            ApplyCompletedBrowserTabMarkState(completion.State!, completion.SkippedMarkCount);
            ApplyPreparedBrowserTabSwitchDirectoryLoadForCommandResult(
                directoryLoad.Value,
                () =>
                {
                    ApplyVisibleBrowserTabSelection(targetIndex);
                    FocusBrowserFileList();
                });
        }
        finally
        {
            _isSwitchingBrowserTab = false;
        }
        ApplyDirectoryPostLoadEffects(completion.PostLoadEffects);
        return true;
    }

    private bool ApplyCommandTabRangeCloseResult(BrowserTabRangeCloseExecution execution, string? scope)
    {
        if (execution.ClosableIndices.Count == 0 || !execution.Closed || execution.Completion is not { } completion)
        {
            return false;
        }

        RefreshBrowserTabHeaders();
        if (!ApplyBrowserTabSwitchResultForCommandResult(completion.Switch))
        {
            return false;
        }

        string message = string.Equals(
            scope,
            "Other",
            StringComparison.Ordinal)
            ? "このタブ以外を閉じました。"
            : scope switch
            {
                "Left" => "左側のタブを閉じました。",
                "Right" => "右側のタブを閉じました。",
                _ => "タブを閉じました。"
            };
        ShowStatusMessage(message);
        return true;
    }

    private bool ApplyCommandTabCloseResult(BrowserTabCloseExecution execution, int tabIndex)
    {
        BrowserTabCloseDecision decision = execution.Decision;
        if (decision.Kind == BrowserTabCloseDecisionKind.Locked)
        {
            ShowStatusMessage("固定タブは閉じられません。先に固定を解除してください。");
            return false;
        }

        if (decision.Kind == BrowserTabCloseDecisionKind.RequiresCategoryRemoval && decision.CategoryId != null)
        {
            BrowserTabCategoryDefinition? category = FindBrowserTabCategoryDefinition(decision.CategoryId);
            if (category == null)
            {
                return false;
            }

            DialogResult confirmation = MessageBox.Show(
                $"このタブを閉じると、カテゴリ「{category.DisplayName}」も削除されます。\nカテゴリごと削除しますか？",
                "タブを閉じる",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.OK)
            {
                return false;
            }

            return false;
        }

        if (decision.Kind is BrowserTabCloseDecisionKind.LastTab or BrowserTabCloseDecisionKind.RequiresCategoryRemoval)
        {
            ShowStatusMessage("最後のタブは閉じられません。");
            return false;
        }

        if (!execution.Closed || execution.Completion is not { } completion)
        {
            return false;
        }

        RefreshBrowserTabHeaders();
        if (!ApplyBrowserTabSwitchResultForCommandResult(completion.Switch))
        {
            return false;
        }
        ShowStatusMessage("タブを閉じました。");
        return true;
    }

    private bool ApplyCommandQuickAccessResult(BrowserQuickAccessNavigationExecution execution)
    {
        BrowserQuickAccessTransition transition = execution.Transition;
        if (transition.StoreUpdated)
        {
            RefreshAllBrowserTabTitles();
            if (!transition.PersistenceSucceeded)
            {
                MessageBox.Show(
                    "QuickAccess を保存できませんでした。ストレージを確認してください。",
                    "QuickAccess",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        if (transition.SaveOnly)
        {
            if (transition.PersistenceSucceeded)
            {
                ShowStatusMessage("QuickAccess を更新しました。");
                return true;
            }

            ShowStatusMessage("QuickAccess を保存できませんでした。ストレージを確認してください。");
            return false;
        }
        if (string.IsNullOrWhiteSpace(transition.NavigationPath))
        {
            return true;
        }

        try
        {
            if (execution.Navigation is not { } navigation)
            {
                return false;
            }
            if (navigation.Kind == BrowserDirectoryNavigationExecutionKind.DirectoryMissing)
            {
                MessageBox.Show(
                    $"指定されたパスが見つかりません: {transition.NavigationPath}",
                    "エラー",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
            if (navigation.Load is { Succeeded: true } load)
            {
                PrepareDerivedBrowserTabPresentation(navigation.DerivedTabIndex);
                ApplyDirectoryLoadUiForCommandResult(
                    load,
                    CreateDerivedBrowserTabSelectionCallback(navigation.DerivedTabIndex));
                ApplyDirectoryPostLoadEffects(navigation.PostLoadEffects);
                return true;
            }
            if (navigation.Error != null)
            {
                NotifyDirectoryLoadFailure(navigation.Error);
            }
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool ApplyCommandLogdiskResult(CommandApplicationEffect effect)
    {
        if (effect.PathEntry is not { } pathEntry)
        {
            return false;
        }
        if (pathEntry.TargetKind == BrowserPathEntryTargetKind.None)
        {
            MessageBox.Show(
                pathEntry.StatusMessage,
                "エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        if (pathEntry.TargetKind != BrowserPathEntryTargetKind.Directory ||
            effect.DirectoryNavigation is not { } navigation)
        {
            return true;
        }
        if (navigation.Load is { Succeeded: true } load)
        {
            PrepareDerivedBrowserTabPresentation(navigation.DerivedTabIndex);
            ApplyDirectoryLoadUiForCommandResult(
                load,
                CreateDerivedBrowserTabSelectionCallback(navigation.DerivedTabIndex));
            ApplyDirectoryPostLoadEffects(navigation.PostLoadEffects);
            return true;
        }
        if (navigation.Error != null)
        {
            NotifyDirectoryLoadFailure(navigation.Error);
        }
        return false;
    }

    private bool ApplyCommandTabCloseConfirmation(
        BrowserTabCloseConfirmationExecution execution,
        string? categoryName)
    {
        if (!execution.Applied)
        {
            return false;
        }

        RefreshBrowserTabHeaders();
        if (execution.Switch is { } switchResult && !ApplyBrowserTabSwitchResultForCommandResult(switchResult))
        {
            return false;
        }
        ShowStatusMessage($"カテゴリを削除しました: {categoryName}");
        return true;
    }

    private bool ApplyCommandApplicationEffect(CommandApplicationEffect effect)
    {
        switch (effect.Kind)
        {
            case CommandApplicationEffectKind.NoOp:
                return true;
            case CommandApplicationEffectKind.CursorNoOp:
                return true;
            case CommandApplicationEffectKind.BeginNamePrefixJump:
                if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || IsCurrentDirectoryBusy())
                {
                    return false;
                }
                _browserNamePrefixJumpSession.Begin();
                ShowBrowserNamePrefixJumpStatus();
                return true;
            case CommandApplicationEffectKind.ApplyDirectoryNavigation:
                if (effect.DirectoryNavigation is not { } directoryNavigation)
                {
                    return false;
                }
                if (effect.BoolValue)
                {
                    ClearPreview();
                }
                if (directoryNavigation.Load is { Succeeded: true } directoryLoad)
                {
                    PrepareDerivedBrowserTabPresentation(directoryNavigation.DerivedTabIndex);
                    if (effect.RefreshPreview)
                    {
                        ApplyDirectoryLoadUiForCommandResult(
                            directoryLoad,
                            CreateDerivedBrowserTabSelectionCallback(directoryNavigation.DerivedTabIndex),
                            applySelectionChanged: ApplyTreeSelectionChanged);
                    }
                    else
                    {
                        ApplyDirectoryLoadUiForCommandResult(
                            directoryLoad,
                            CreateDerivedBrowserTabSelectionCallback(directoryNavigation.DerivedTabIndex));
                    }
                    ApplyDirectoryPostLoadEffects(directoryNavigation.PostLoadEffects);
                    return true;
                }
                if (directoryNavigation.Error != null)
                {
                    NotifyDirectoryLoadFailure(directoryNavigation.Error);
                }
                return false;
            case CommandApplicationEffectKind.ApplyHistoryNavigation:
                if (effect.HistoryNavigation is not { } historyNavigation)
                {
                    return false;
                }
                if (historyNavigation.Load is { Succeeded: true } historyLoad)
                {
                    PrepareDerivedBrowserTabPresentation(historyNavigation.Start.DerivedTabIndex);
                    ApplyDirectoryLoadUiForCommandResult(
                        historyLoad,
                        CreateDerivedBrowserTabSelectionCallback(historyNavigation.Start.DerivedTabIndex));
                    ApplyDirectoryPostLoadEffects(historyNavigation.PostLoadEffects);
                    return true;
                }
                if (historyNavigation.Load?.Error != null)
                {
                    NotifyDirectoryLoadFailure(historyNavigation.Load.Value.Error);
                }
                return false;
            case CommandApplicationEffectKind.ApplyManualRefresh:
                return effect.ManualRefresh is { } manualRefresh &&
                    ApplyManualRefreshExecutionForCommandResult(manualRefresh, "現在ディレクトリを再読込しました。");
            case CommandApplicationEffectKind.ApplyCursorNavigation:
                if (effect.CursorNavigation is not { } cursorNavigation || !cursorNavigation.Cursor.Applied)
                {
                    return false;
                }
                if (cursorNavigation.Load is { Succeeded: true } cursorLoad)
                {
                    ApplyDirectoryLoadUiForCommandResult(cursorLoad);
                    ApplyDirectoryPostLoadEffects(cursorNavigation.PostLoadEffects);
                }
                else
                {
                    SyncBrowserSelectionForCommandResult();
                }
                return true;
            case CommandApplicationEffectKind.ApplyBulkMarks:
                if (effect.BulkMarks is not { } bulkMarks)
                {
                    return false;
                }
                if (bulkMarks.Changed)
                {
                    ApplyBulkMarkState(
                        bulkMarks.NextMarks,
                        bulkMarks.RemovedAll
                            ? (effect.BoolValue ? "UnmarkAllItems" : "UnmarkAllFiles")
                            : (effect.BoolValue ? "MarkAllItems" : "MarkAllFiles"),
                    effect.IntValue,
                    0,
                        Stopwatch.StartNew());
                }
                return true;
            case CommandApplicationEffectKind.ApplyTabCreation:
                if (effect.TabCreation is not { } tabCreation || !tabCreation.Created)
                {
                    return false;
                }
                if (!tabCreation.Activated || tabCreation.Activation is not { } tabActivation)
                {
                    return false;
                }
                RefreshBrowserTabHeaders();
                bool created = ApplyBrowserTabSwitchResultForCommandResult(tabActivation);
                if (created)
                {
                    ShowStatusMessage("新しいタブを作成しました。");
                }
                return created;
            case CommandApplicationEffectKind.ApplyTabCreationBatch:
                if (effect.TabCreationBatch is not { } batch || !batch.PreflightPassed)
                {
                    return false;
                }
                if (!batch.Succeeded)
                {
                    if (batch.RollbackActivation is { } rollback && !ApplyBrowserTabSwitchResultForCommandResult(rollback))
                    {
                        return false;
                    }
                    RefreshBrowserTabHeaders();
                    ShowStatusMessage("複数のディレクトリを開けなかったため、変更を取り消しました。");
                    return false;
                }
                RefreshBrowserTabHeaders();
                foreach (BrowserTabCreationExecution item in batch.Items)
                {
                    if (item.Activation is not { } activation ||
                        !ApplyBrowserTabSwitchResultForCommandResult(activation))
                    {
                        return false;
                    }
                }
                ShowStatusMessage("複数のディレクトリを新しいタブで開きました。");
                return true;
            case CommandApplicationEffectKind.ApplyTabBatchHistory:
                if (effect.TabBatchHistory is not { } history)
                {
                    return false;
                }
                if (history.TabSwitch is { } historySwitch &&
                    !ApplyBrowserTabSwitchResultForCommandResult(historySwitch))
                {
                    return false;
                }
                RefreshBrowserTabHeaders();
                UpdateMenuStripState();
                _browserTabStrip?.Invalidate();
                if (!history.Applied)
                {
                    if (!string.IsNullOrWhiteSpace(history.Reason))
                    {
                        ShowStatusMessage(history.Reason);
                    }
                    return false;
                }
                ShowStatusMessage(history.Kind == BrowserTabBatchHistoryExecutionKind.Redone
                    ? "やり直しました。"
                    : history.Reason ?? "元に戻しました。");
                return true;
            case CommandApplicationEffectKind.ApplyTabSwitch:
                return effect.TabSwitch is { } tabSwitch &&
                    ApplyBrowserTabSwitchResultForCommandResult(tabSwitch);
            case CommandApplicationEffectKind.ApplyCategorySwitch:
                return effect.CategorySwitch is { } categorySwitch &&
                    ApplyBrowserCategorySwitchResult(categorySwitch);
            case CommandApplicationEffectKind.ApplyTabHistoryNavigation:
                if (effect.TabHistoryNavigation is not { } tabHistory)
                {
                    return false;
                }
                if (!tabHistory.Succeeded)
                {
                    if (!string.IsNullOrWhiteSpace(tabHistory.Message))
                    {
                        ShowStatusMessage(tabHistory.Message);
                    }
                    return false;
                }
                if (tabHistory.TabSwitch is { } histTabSwitch)
                {
                    return ApplyBrowserTabSwitchResultForCommandResult(histTabSwitch);
                }
                if (tabHistory.CategorySwitch is { } histCategorySwitch)
                {
                    return ApplyBrowserCategorySwitchResult(histCategorySwitch);
                }
                return true;
            case CommandApplicationEffectKind.ApplyCategoryAdd:
                if (effect.CategoryAdd is not { } categoryAdd || !categoryAdd.Added)
                {
                    return false;
                }
                RefreshBrowserTabHeaders();
                if (!ApplyBrowserCategorySwitchResult(categoryAdd.Switch))
                {
                    return false;
                }
                ShowStatusMessage("カテゴリを追加しました。");
                return true;
            case CommandApplicationEffectKind.ApplyCategoryReorder:
                if (effect.CategoryReorder is not { } categoryReorder || !categoryReorder.Applied)
                {
                    return false;
                }
                RefreshBrowserTabHeaders();
                ShowStatusMessage($"カテゴリを移動しました: {categoryReorder.DisplayName}");
                FocusBrowserFileList();
                return true;
            case CommandApplicationEffectKind.ApplyTabLayout:
                ApplyBrowserTabStripDisplaySettings();
                RefreshBrowserTabHeaders();
                UpdateMenuStripState();
                ShowStatusMessage("タブ表示位置を切り替えました。");
                return true;
            case CommandApplicationEffectKind.CreateBrowserTabGroup:
                return ApplyCreateBrowserTabGroupCommand(effect.IntValue, effect.CategoryId, effect.ContextBrowserTabId);
            case CommandApplicationEffectKind.AddBrowserTabGroupMember:
                return ApplyAddBrowserTabGroupMemberCommand(effect.IntValue, effect.TextValue, effect.CategoryId, effect.ContextBrowserTabId);
            case CommandApplicationEffectKind.RemoveBrowserTabGroupMember:
                return ApplyRemoveBrowserTabGroupMemberCommand(effect.IntValue, effect.CategoryId, effect.ContextBrowserTabId);
            case CommandApplicationEffectKind.RenameBrowserTabGroup:
                return ApplyRenameBrowserTabGroupCommand(effect.IntValue, effect.TextValue);
            case CommandApplicationEffectKind.ViewerCommandCompleted:
                return true;
            case CommandApplicationEffectKind.LaunchExternalMediaPlayback:
                if (string.IsNullOrWhiteSpace(effect.TextValue))
                {
                    return false;
                }
                LaunchMediaPlayback(effect.TextValue, effect.BoolValue);
                return true;
            case CommandApplicationEffectKind.ConfirmExecuteTarget:
                if (string.IsNullOrWhiteSpace(effect.TextValue))
                {
                    return false;
                }
                ExecuteConfirmedFile(effect.TextValue);
                return true;
            case CommandApplicationEffectKind.OpenMediaViewer:
                if (string.IsNullOrWhiteSpace(effect.TextValue) || effect.IntValue == (int)PreviewKind.None)
                {
                    return false;
                }
                OpenImageViewerFromCommand(effect.TextValue, (PreviewKind)effect.IntValue);
                return true;
            case CommandApplicationEffectKind.OpenArchiveFallback:
                if (string.IsNullOrWhiteSpace(effect.TextValue))
                {
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(effect.MessageValue))
                {
                    ShowStatusMessage($"{effect.MessageValue} 関連付けで開きます。");
                }
                OpenPathWithShellAssociation(effect.TextValue);
                return true;
            case CommandApplicationEffectKind.FileOperationStarted:
                return true;
            case CommandApplicationEffectKind.ApplyDefaultOpen:
                if (effect.Selection is { Count: > 1 } batchSelection)
                {
                    int launchedCount = 0;
                    List<string> failedPaths = [];
                    foreach (string path in batchSelection.FullPaths)
                    {
                        if (File.Exists(path) && OpenPathWithShellAssociation(path))
                        {
                            launchedCount++;
                        }
                        else
                        {
                            failedPaths.Add(path);
                        }
                    }
                    if (failedPaths.Count > 0)
                    {
                        string failedNames = string.Join(", ", failedPaths.Select(Path.GetFileName));
                        ShowStatusMessage($"既定アプリ起動: {launchedCount}件成功、{failedPaths.Count}件失敗 ({failedNames})");
                    }
                    else
                    {
                        ShowStatusMessage($"既定アプリで{launchedCount}件を開きました。");
                    }
                    return launchedCount > 0;
                }
                if (string.IsNullOrWhiteSpace(effect.TextValue))
                {
                    return false;
                }
                if (Directory.Exists(effect.TextValue))
                {
                    try
                    {
                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            UseShellExecute = false
                        };
                        startInfo.ArgumentList.Add(effect.TextValue);
                        System.Diagnostics.Process.Start(startInfo);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogService.Error($"ApplyDefaultOpen Explorer起動失敗: {ex.Message}");
                        ShowStatusMessage("起動に失敗しました");
                        return false;
                    }
                }
                OpenPathWithShellAssociation(effect.TextValue);
                return true;
            case CommandApplicationEffectKind.OpenWithDialog:
                if (string.IsNullOrWhiteSpace(effect.TextValue))
                {
                    return false;
                }
                string? openWithError = WindowsOpenWithService.ShowOpenWithDialog(Handle, effect.TextValue);
                if (!string.IsNullOrWhiteSpace(openWithError))
                {
                    ShowStatusMessage(openWithError);
                    return false;
                }
                return true;
            case CommandApplicationEffectKind.ApplyTabLock:
                if (effect.TabLock is not { } tabLock || !tabLock.Applied)
                {
                    return false;
                }
                if (tabLock.Switch is { } lockSwitch && !ApplyBrowserTabSwitchResultForCommandResult(lockSwitch))
                {
                    return false;
                }
                RefreshBrowserTabHeaders();
                ShowStatusMessage(tabLock.Transition.IsLocked
                    ? "現在のタブを固定しました。"
                    : "現在のタブ固定を解除しました。");
                return true;
            case CommandApplicationEffectKind.ApplyTabReadOnly:
                if (effect.TabReadOnly is not { } tabReadOnly || !tabReadOnly.Applied)
                {
                    return false;
                }
                if (tabReadOnly.Switch is { } readOnlySwitch && !ApplyBrowserTabSwitchResultForCommandResult(readOnlySwitch))
                {
                    return false;
                }
                RefreshBrowserTabHeaders();
                ShowStatusMessage(tabReadOnly.Transition.IsReadOnly
                    ? "現在のタブを ReadOnly にしました。"
                    : "現在のタブの ReadOnly を解除しました。");
                return true;
            case CommandApplicationEffectKind.ApplyUnifiedFilter:
                if (effect.UnifiedFilter is not { } unifiedFilter)
                {
                    return false;
                }
                if (unifiedFilter.RefreshStarted)
                {
                    return ApplyManualRefreshExecutionForCommandResult(
                        unifiedFilter.Refresh,
                        "フィルタ条件を適用して再読込しました。");
                }
                RefreshBrowserTabHeaders();
                UpdateMenuStripState();
                _browserTabStrip?.Invalidate();
                return true;
            case CommandApplicationEffectKind.RunUnifiedFilterFind:
                if (effect.UnifiedSearch is not { } unifiedSearch)
                {
                    return false;
                }
                if (IsHandleCreated && !IsDisposed && !Disposing)
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        _ = RunUnifiedFilterFindAsync(unifiedSearch);
                    }));
                }
                return true;
            case CommandApplicationEffectKind.ApplyTabClose:
                return effect.TabClose is { } tabClose &&
                    ApplyCommandTabCloseResult(tabClose, effect.IntValue);
            case CommandApplicationEffectKind.ApplyTabRangeClose:
                return effect.TabRangeClose is { } tabRangeClose && ApplyCommandTabRangeCloseResult(tabRangeClose, effect.TextValue);
            case CommandApplicationEffectKind.ApplyRestoredTab:
                if (effect.RestoredTab is not { } restoredTab || !restoredTab.Restored || restoredTab.TabSwitch is not { } restoredSwitch)
                {
                    return false;
                }
                RefreshBrowserTabHeaders();
                ApplyBrowserTabSwitchResultForCommandResult(restoredSwitch);
                ShowStatusMessage("Gesture: 閉じたタブを復元");
                return true;
            case CommandApplicationEffectKind.ApplyQuickAccess:
                return effect.QuickAccess is { } quickAccess &&
                    ApplyCommandQuickAccessResult(quickAccess);
            case CommandApplicationEffectKind.ApplyLogdisk:
                return ApplyCommandLogdiskResult(effect);
            case CommandApplicationEffectKind.ApplyCategoryRename:
                if (effect.CategoryRename is not { } rename)
                {
                    return false;
                }
                if (!rename.Applied)
                {
                    if (rename.DuplicateName)
                    {
                        MessageBox.Show(
                            "同じ表示名のカテゴリがすでにあります。",
                            "カテゴリ名変更",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    return false;
                }
                RefreshBrowserTabHeaders();
                FocusBrowserFileList();
                ShowStatusMessage($"カテゴリ名を更新しました: {effect.TextValue}");
                return true;
            case CommandApplicationEffectKind.ApplyCategoryDelete:
                if (effect.CategoryDelete is not { } categoryDelete || !categoryDelete.Removed)
                {
                    return false;
                }
                RefreshBrowserTabHeaders();
                if (categoryDelete.Switch is { } tabSwitchAfterDelete && !ApplyBrowserTabSwitchResultForCommandResult(tabSwitchAfterDelete))
                {
                    return false;
                }
                ShowStatusMessage($"カテゴリを削除しました: {effect.TextValue}");
                return true;
            case CommandApplicationEffectKind.ApplyTabCloseConfirmation:
                return effect.TabCloseConfirmation is { } tabCloseConfirmation &&
                    ApplyCommandTabCloseConfirmation(tabCloseConfirmation, effect.TextValue);
            case CommandApplicationEffectKind.ShowMessage:
                if (string.IsNullOrWhiteSpace(effect.TextValue))
                {
                    return false;
                }
                ShowStatusMessage(effect.TextValue);
                return true;
            case CommandApplicationEffectKind.OpenCommandDialog:
                ExecuteShellDialog();
                return true;
            case CommandApplicationEffectKind.OpenPathEntry:
                OpenBrowserPathEntry();
                return true;
            case CommandApplicationEffectKind.OpenExplorer:
                ExecuteOpenCurrentPathInExplorer();
                return true;
            case CommandApplicationEffectKind.RevealInExplorer:
                if (string.IsNullOrWhiteSpace(effect.TextValue))
                {
                    return false;
                }
                ExecuteOpenBrowserItemInExplorer(effect.TextValue);
                return true;
            case CommandApplicationEffectKind.OpenTerminal:
                if (GuardMutationBusy()) return false;
                string workingDirectory = string.IsNullOrWhiteSpace(effect.TextValue)
                    ? _browserApplicationCoordinator.CurrentPath
                    : effect.TextValue;
                if (string.IsNullOrWhiteSpace(workingDirectory))
                {
                    return false;
                }
                OpenTerminalInWorkingDirectory(workingDirectory, effect.ShellKind!.Value);
                return true;
            case CommandApplicationEffectKind.OpenExternalEditor:
                ExecuteOpenWithEditor();
                return true;
            case CommandApplicationEffectKind.CopyFullPaths:
                CopySelectedOrMarkedFullPathsToClipboard();
                return true;
            case CommandApplicationEffectKind.CopyCurrentPath:
                CopyCurrentDirectoryFromHeader();
                return true;
            case CommandApplicationEffectKind.ShowHelp:
                ShowMenuKeyHint();
                return true;
            case CommandApplicationEffectKind.OpenMarkSlot:
                OpenMarkSlotDialog();
                return true;
            case CommandApplicationEffectKind.ManageTabCategories:
                OpenBrowserTabCategoryManager();
                return true;
            case CommandApplicationEffectKind.Properties:
                return ExecuteProperties(ResolveSelection(effect.Selection));
            case CommandApplicationEffectKind.ShowSystemInformation:
                ShowSystemInformationDialog();
                return true;
            case CommandApplicationEffectKind.OpenNewInstance:
                if (GuardMutationBusy()) return false;
                OpenNewInstanceFromCommand(effect.TextValue!);
                return true;
            case CommandApplicationEffectKind.OpenControlPanel:
                if (GuardMutationBusy()) return false;
                OpenControlPanelFromCommand();
                return true;
            case CommandApplicationEffectKind.OpenSettings:
                OpenSettingsForm();
                return true;
            case CommandApplicationEffectKind.OpenCommandLauncher:
                OpenCommandPalette();
                return true;
            case CommandApplicationEffectKind.ShowCommandList:
                ShowCommandList();
                return true;
            case CommandApplicationEffectKind.OpenManagedTrash:
                OpenManagedTrashDialog();
                return true;
            default:
                return false;
        }
    }

    private void OpenNewInstanceFromCommand(string currentPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(currentPath);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            LogService.Error($"NewInstance 起動失敗: {ex.Message}");
        }
    }

    private static void OpenControlPanelFromCommand()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Error($"ControlPanel 起動失敗: {ex.Message}");
        }
    }

    internal static bool TryResolveBrowserTabCommandTarget(CommandExecutionContext context, int activeTabIndex, int tabCount, out int tabIndex)
    {
        tabIndex = context.ContextTabIndex ?? activeTabIndex;
        return tabIndex >= 0 && tabIndex < tabCount;
    }

    private void OpenSystemInformationFromUi(string source)
    {
        _ = ExecuteCommandFromUi(CommandIds.AppOpenSystemInformation, CommandScope.Browser, source);
    }
    private void OpenManagedTrashDialog()
    {
        using var dialog = new Dialogs.ManagedTrashDialog(_settingsCoordinator.Value, _fileOperationApplicationCoordinator);
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
            using var dialog = new Dialogs.SystemInformationDialog(_browserApplicationCoordinator.CurrentPath);
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
        {
            ShowStatusMessage("Command Palette は Browser モードでのみ使用できます。");
            return;
        }
        bool allowUsage = _featureGate.IsEnabled(FeatureId.CommandPaletteUsage);
        var usageState = allowUsage
            ? Services.CommandPaletteUsageStorage.Load()
            : new CommandPaletteUsageState();
        SelectionResult selectionSnapshot = ResolveSelection();
        using var dialog = new Dialogs.CommandPaletteDialog(
            (query, expanded) => Services.CommandPaletteService.BuildPresentation(this, _featureGate, usageState, query, expanded, selectionSnapshot),
            usageState,
            allowUsage ? state => Services.CommandPaletteUsageStorage.Save(state) : _ => true);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (dialog.SelectedCommand is { } selectedCommand)
            {
                if (allowUsage)
                {
                    Services.CommandPaletteUsageStorage.RecordRecent(usageState, selectedCommand.Id);
                    if (!Services.CommandPaletteUsageStorage.Save(usageState))
                    {
                        MessageBox.Show(
                            this,
                            "コマンド利用履歴を保存できませんでした。ストレージを確認してください。",
                            "Command Palette",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
                selectedCommand.Execute();
            }
        }
    }
    CommandRegistry ICommandPaletteHost.GetCommandRegistry() => _commandRegistry;
    void ICommandPaletteLayerHost.ExecuteCommandFromUi(string commandId, CommandScope scope, string source, SelectionResult? selectionSnapshot, SevenZipHashAlgorithm? hashAlgorithm)
    {
        if (hashAlgorithm is { } algorithm)
        {
            _ = ExecuteCommandWithHashAlgorithmFromUi(commandId, scope, source, algorithm, selectionSnapshot);
            return;
        }

        _ = ExecuteCommandFromUi(commandId, scope, source, selectionSnapshot);
    }
    void ICommandPaletteHost.OpenSettingsForm(SettingsForm.InitialTab initialTab) => OpenSettingsForm(initialTab);
    string ICommandPaletteHost.GetCurrentFunctionKeyProfileValue() => CurrentFunctionKeyProfileValue;
    Dictionary<string, List<string>>? ICommandPaletteHost.GetBrowserKeyCommandOverrides()
        => _settingsCoordinator.GetBrowserKeyCommandOverridesSnapshot();
    string ICommandPaletteHost.ResolveKeyBindingText(string commandId)
        => Services.CommandPaletteSearchContext.ResolveKeyBindingText(this, commandId);
    string ICommandPaletteLayerHost.GetCurrentBrowserPath() => _browserApplicationCoordinator.CurrentPath;
    bool ICommandPaletteLayerHost.IsFileOperationBusy => _fileOperationApplicationCoordinator.IsBusy;
    QuickAccessStore ICommandPaletteLayerHost.GetQuickAccessStoreClone() => _browserApplicationCoordinator.Workspace.QuickAccessSnapshot.Clone();
    IReadOnlyList<string> ICommandPaletteLayerHost.GetBackHistorySnapshot() => _browserApplicationCoordinator.GetBackHistorySnapshot();
    IReadOnlyList<string> ICommandPaletteLayerHost.GetForwardHistorySnapshot() => _browserApplicationCoordinator.GetForwardHistorySnapshot();
    MarkSlotStore ICommandPaletteLayerHost.GetMarkSlotStoreClone() => _browserWorkspacePersistenceApplicationCoordinator.GetMarkSlots();
    SelectionResult ICommandPaletteLayerHost.ResolveSelection() => ResolveSelection();
    IReadOnlyDictionary<string, bool> ICommandPaletteLayerHost.GetPassiveSelectionPathKinds()
    {
        SelectionResult selection = ResolveSelection();
        return _browserApplicationCoordinator.GetPassivePathKinds(selection.FullPaths);
    }
    void ICommandPaletteLayerHost.NavigateToPath(string path) => NavigateToPathSafe(path);
    void ICommandPaletteLayerHost.RestoreMarksFromSlot(int slotNumber) => RestoreMarksFromSlot(slotNumber);
    void ICommandPaletteLayerHost.ShowArchiveContents(string archivePath) => ShowArchiveContentsOrFallback(archivePath);
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
            LogAltHint($"WM_SYSCOMMAND Command=0x{command:X} lParam=0x{m.LParam:X} AltOwned={_isExternalToolAltPopupAltOwned} UiMode={_viewerApplicationCoordinator.Mode} ActiveControl={DescribeControl(ActiveControl)}");
            bool isSnapshotTarget = (command == SC_MINIMIZE || command == SC_RESTORE || command == SC_MAXIMIZE || command == SC_SIZE || command == SC_MOVE);
            if (isSnapshotTarget)
            {
                LogSysCommandFloorHitTrace("BeforeBase", command);
                _lastRestoreUtc = DateTime.UtcNow;
            }
            if (command == SC_CLOSE)
            {
                LogService.Warn(
                    $"[CancelRuntime] MainForm WM_SYSCOMMAND close. busy={_fileOperationApplicationCoordinator.IsClipboardBusy}, " +
                    $"hasCts={_fileOperationApplicationCoordinator.CancellationTokenSource != null}, requested={_fileOperationApplicationCoordinator.CancellationTokenSource?.IsCancellationRequested ?? false}, " +
                    $"activeControl={DescribeControl(ActiveControl)}, thread={Environment.CurrentManagedThreadId}");
                if (TryHandleSyncExternalDropCloseRequest("MainForm.WndProc.SC_CLOSE"))
                {
                    LogService.Info("[CancelRuntime] MainForm WM_SYSCOMMAND close deferred until sync external drop completion.");
                    return;
                }
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
            if (command == SC_KEYMENU && _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser)
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
        if (!canShow && _isExternalToolAltPopupAltOwned && _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser && Visible && Enabled && browserPanel.Visible)
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
            _browserApplicationCoordinator.CurrentPath,
            GetSelectedItemFullPathForHeaderCopy(),
            GetSelectedItemNameForHeaderCopy(),
            _browserApplicationCoordinator.Selection.Snapshot());
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
        LaunchExternalTool(selected.Tool);
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
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            return;
        }

        var item = GetCurrentBrowserItem();
        if (item == null) return;
        // .. はマーク対象外
        if (item.Text == "..")
        {
            if (moveNext && _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser)
            {
                int total = _browserApplicationCoordinator.TotalItemCount > 0 ? _browserApplicationCoordinator.TotalItemCount : fileListView.Items.Count;
                if (_browserApplicationCoordinator.CursorIndex < total - 1)
                {
                    SetBrowserGlobalCursorIndex(_browserApplicationCoordinator.CursorIndex + 1);
                }
            }
            return;
        }
        string? fullPath = item.Tag as string;
        if (fullPath != null)
        {
            bool wasMarked = _browserApplicationCoordinator.Selection.Contains(fullPath);
            BrowserSelectionMarkExecution execution = _browserTabWorkflowApplicationCoordinator.ExecuteToggleMark(
                fullPath,
                adding: !wasMarked);
            BrowserSelectionMarkTransition transition = execution.Transition;
            if (transition.Changed)
            {
                ApplyCommittedMarkMutationEffects(1, execution.Commit.ExactDeltaApplied);
            }
            ApplyMarkColor(item, fullPath);
            RefreshMarkUi(); // Phase 2g-fix6.2b: 即時反映 (moveNext:false経路等に対応)
        }
        if (moveNext && _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser)
        {
            int total = _browserApplicationCoordinator.TotalItemCount > 0 ? _browserApplicationCoordinator.TotalItemCount : fileListView.Items.Count;
            if (_browserApplicationCoordinator.CursorIndex < total - 1)
            {
                SetBrowserGlobalCursorIndex(_browserApplicationCoordinator.CursorIndex + 1);
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
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            return false;
        }

        BrowserSelectionMarkExecution execution = _browserTabWorkflowApplicationCoordinator.ExecuteToggleMark(path, adding: true);
        BrowserSelectionMarkTransition transition = execution.Transition;
        if (transition.Changed)
        {
            ApplyCommittedMarkMutationEffects(1, execution.Commit.ExactDeltaApplied);
        }
        return transition.ChangeKind == BrowserSelectionMarkChangeKind.Added;
    }
    private bool UnmarkPath(string path)
    {
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            return false;
        }

        BrowserSelectionMarkExecution execution = _browserTabWorkflowApplicationCoordinator.ExecuteToggleMark(path, adding: false);
        BrowserSelectionMarkTransition transition = execution.Transition;
        if (transition.Changed)
        {
            ApplyCommittedMarkMutationEffects(1, execution.Commit.ExactDeltaApplied);
        }
        return transition.ChangeKind == BrowserSelectionMarkChangeKind.Removed;
    }

    private void SetCompleteMarkSummaryCache(string currentDir, MarkSummaryExactCache updated)
    {
        _browserApplicationCoordinator.SetCompleteMarkSummary(currentDir, updated);
    }

    private void ApplyCommittedMarkMutationEffects(int changedCount, bool exactDeltaApplied)
    {
        _ = _markOperationEffectCoordinator.ExecuteMutation(
            changedCount,
            markCommit: () =>
            {
                InvalidateMarkSummaryCache();
                InvalidateRecentMultiMarkIntent();
                ClearPendingEscExitMarkPersistence();
                _browserMarkInteractionController.SyncMarkState(hasMarks: _browserApplicationCoordinator.Selection.Count > 0);
            },
            activeTabSync: static () => { },
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
        int removedCount = _browserTabWorkflowApplicationCoordinator.RemoveMarksAndSync(paths);
        if (removedCount <= 0)
        {
            return;
        }
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        ClearPendingEscExitMarkPersistence();
        _browserMarkInteractionController.SyncMarkState(hasMarks: _browserApplicationCoordinator.Selection.Count > 0);
        UpdateInfoPanel();
        LogService.Info($"[MoveHotpath] BulkUnmark reason={reason} requested={paths.Count} removed={removedCount}");
    }
    private void ClearMarks(
        bool invalidateRedo = true,
        bool preservePendingEscExitState = false,
        bool updateInfoPanel = true)
    {
        if (_browserApplicationCoordinator.Selection.Count == 0) return;
        _browserTabWorkflowApplicationCoordinator.ClearMarksAndSync();
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        if (!preservePendingEscExitState)
        {
            ClearPendingEscExitMarkPersistence();
        }
        _browserMarkInteractionController.SyncMarkState(hasMarks: false);
        SetZeroMarkSummaryCache();
        if (updateInfoPanel)
        {
            UpdateInfoPanel();
        }
    }
    private void RestoreMarks(IEnumerable<string> paths, bool invalidateRedo = true)
    {
        _browserTabWorkflowApplicationCoordinator.RestoreMarksAndSync(paths);
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        ClearPendingEscExitMarkPersistence();
        _browserMarkInteractionController.SyncMarkState(hasMarks: _browserApplicationCoordinator.Selection.Count > 0);
    }
    private int CountMarksOutsideCurrentDirectory()
        => _browserApplicationCoordinator.Selection.CountOutsideCurrentDirectory(_browserApplicationCoordinator.CurrentPath);
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
        Func<MarkSlotClipboardActionResult>? importClipboardAction =
            AuthorToolsEnabled ? ImportClipboardPathsToCurrentMarks : null;
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
             importClipboardAction,
             showImportResultAction: _integrationSeam?.MarkSlotImportResultDialogOverride ?? ShowMarkSlotImportResult);
        if (_integrationSeam?.MarkSlotDialogShown is { } markSlotDialogShown)
        {
            dialog.Shown += (_, _) => markSlotDialogShown(dialog);
        }
        dialog.ShowDialog(this);
        if (dialog.SuccessfulImportResult?.Success != true)
        {
            return;
        }

        InvalidateMarkSummaryCache();
        UpdateInfoPanel();
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        RefreshBrowserTabHeaders();
        PrimeRecentMultiMarkIntent();
    }
    private MarkSlotDialog.MarkGlobalSummary BuildMarkGlobalSummary()
    {
        BrowserMarkSlotGlobalSummary summary = _browserMarkSlotWorkflowApplicationCoordinator.BuildGlobalSummary(
            BuildBrowserTabStateFromCurrentUi());
        return new MarkSlotDialog.MarkGlobalSummary(
            summary.ActiveTabMarkCount,
            summary.CurrentCategoryMarkCount,
            summary.CurrentCategoryTabCount,
            summary.CurrentCategoryName,
            summary.GlobalMarkCount,
            summary.GlobalCategoryCount,
            summary.GlobalTabCount);
    }
    private void ClearCategoryMarksFromDialog()
    {
        string categoryId = _browserApplicationCoordinator.Workspace.ActiveCategoryId ?? BrowserTabSettings.DefaultCategoryId;
        BrowserMarkPersistenceTransition persistence = _browserTabWorkflowApplicationCoordinator.ClearCategoryMarks(
            categoryId,
            BuildBrowserTabStateFromCurrentUi());
        BrowserMarksClearTransition clear = persistence.Clear;
        if (clear.Changed)
        {
            RefreshMarkUi();
            RefreshBrowserTabHeaders();
            ShowStatusMessage($"カテゴリ '{categoryId}' のマークをすべて解除しました ({clear.ClearedCount}件)。");
        }
    }
    private void ClearGlobalMarksFromDialog()
    {
        BrowserMarkPersistenceTransition persistence = _browserTabWorkflowApplicationCoordinator.ClearAllMarks(
            BuildBrowserTabStateFromCurrentUi());
        BrowserMarksClearTransition clear = persistence.Clear;
        if (clear.Changed)
        {
            RefreshMarkUi();
            RefreshBrowserTabHeaders();
            ShowStatusMessage($"Workspace 全域の全マークを解除しました ({clear.ClearedCount}件)。");
        }
    }
    private void ClearCurrentTabMarksFromDialog()
    {
        int clearedCount = _browserApplicationCoordinator.Selection.Count;
        if (clearedCount <= 0)
        {
            return;
        }
        ClearMarks(invalidateRedo: false);
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        _browserTabWorkflowApplicationCoordinator.PersistAfterMarkMutation(
            BuildBrowserTabStateFromCurrentUi());
        RefreshBrowserTabHeaders();
        ShowStatusMessage($"現在タブのマークをすべて解除しました ({clearedCount}件)。");
    }
    private IReadOnlyList<MarkSlotDialog.MarkListViewItem> BuildMarkSlotDialogItems()
    {
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath);
        return _browserApplicationCoordinator.Selection
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
        return _browserMarkSlotWorkflowApplicationCoordinator.BuildPersistenceSummaryText();
    }
    private IReadOnlyList<MarkSlotDialog.MarkSlotSummaryViewItem> BuildMarkSlotSummaryItems()
    {
        return _browserMarkSlotWorkflowApplicationCoordinator.GetSlots().Slots
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
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath);
        return _browserMarkSlotWorkflowApplicationCoordinator.GetSlot(slotNumber).Paths
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
        BrowserTabState? activeTab = GetActiveBrowserTab();
        if (activeTab == null)
        {
            return string.Empty;
        }
        List<string> currentPaths = _browserApplicationCoordinator.Selection.Snapshot()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        MarkSlotActionResult result = _browserMarkSlotWorkflowApplicationCoordinator.SaveCurrentTab(
            slotNumber,
            displayName,
            currentPaths,
            activeTab,
            GetActiveBrowserTabCategoryDisplayName(),
            GetBrowserTabDisplayName(activeTab),
            MarkSlotCount);
        if (result.Success)
        {
            LogService.Info($"[MarkSlots] Saved Slot={slotNumber} Count={currentPaths.Count}");
        }
        ShowStatusMessage(result.Message);
        return result.Message;
    }
    private string SaveCurrentCategoryMarksToSlot(int slotNumber)
    {
        BrowserMarkSlotSaveAggregation aggregation = BuildCurrentCategoryMarkSlotAggregation();
        MarkSlotEntry slot = _browserMarkSlotWorkflowApplicationCoordinator.GetSlot(slotNumber);
        string defaultName = BrowserMarkSlotWorkflowApplicationCoordinator.BuildDefaultDisplayName(
            slot,
            aggregation.SourceScope,
            aggregation.SourceCategoryName);
        string? displayName = SimpleInputDialog.ShowNullable(
            BuildScopedSlotSavePrompt(slotNumber, slot, aggregation),
            "カテゴリ全マークをスロットへ保存",
            defaultName);
        if (displayName == null)
        {
            return string.Empty;
        }
        MarkSlotActionResult saveResult = _browserMarkSlotWorkflowApplicationCoordinator.SaveAggregation(
            slotNumber,
            displayName,
            aggregation,
            MarkSlotCount);
        string message = saveResult.Message;
        if (saveResult.Success)
        {
            LogService.Info($"[MarkSlots] Saved Slot={slotNumber} Scope={aggregation.SourceScope} Raw={aggregation.RawMarkCount} Unique={aggregation.UniquePathCount}");
        }
        ShowStatusMessage(message);
        return message;
    }
    private string SaveWorkspaceMarksToSlot(int slotNumber)
    {
        BrowserMarkSlotSaveAggregation aggregation = BuildWorkspaceMarkSlotAggregation();
        MarkSlotEntry slot = _browserMarkSlotWorkflowApplicationCoordinator.GetSlot(slotNumber);
        string defaultName = BrowserMarkSlotWorkflowApplicationCoordinator.BuildDefaultDisplayName(
            slot,
            aggregation.SourceScope,
            aggregation.SourceCategoryName);
        string? displayName = SimpleInputDialog.ShowNullable(
            BuildScopedSlotSavePrompt(slotNumber, slot, aggregation),
            "Workspace全マークをスロットへ保存",
            defaultName);
        if (displayName == null)
        {
            return string.Empty;
        }
        MarkSlotActionResult saveResult = _browserMarkSlotWorkflowApplicationCoordinator.SaveAggregation(
            slotNumber,
            displayName,
            aggregation,
            MarkSlotCount);
        string message = saveResult.Message;
        if (saveResult.Success)
        {
            LogService.Info($"[MarkSlots] Saved Slot={slotNumber} Scope={aggregation.SourceScope} Raw={aggregation.RawMarkCount} Unique={aggregation.UniquePathCount}");
        }
        ShowStatusMessage(message);
        return message;
    }
    private MarkSlotActionResult RestoreMarksFromSlot(int slotNumber)
    {
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            const string busyMessage = "ファイル操作中はマークスロットを適用できません。";
            ShowStatusMessage(busyMessage);
            return new MarkSlotActionResult(false, busyMessage);
        }

        MarkSlotActionResult result = _browserMarkSlotWorkflowApplicationCoordinator.RestoreToCurrentTab(
            slotNumber,
            BuildBrowserTabStateFromCurrentUi());
        if (!result.Success)
        {
            return result;
        }
        UpdateInfoPanel();
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        PrimeRecentMultiMarkIntent();
        LogService.Info($"[MarkSlots] Restored Slot={slotNumber}");
        ShowStatusMessage(result.Message);
        return result;
    }
    private MarkSlotClipboardActionResult ImportClipboardPathsToCurrentMarks()
    {
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            const string busyMessage = "ファイル操作中はマークを変更できません。";
            ShowStatusMessage(busyMessage);
            return BrowserMarkSlotWorkflowApplicationCoordinator.BuildClipboardImportFailure(busyMessage, null);
        }

        string? text = _integrationSeam?.MarkSlotClipboardTextOverride;
        if (text == null)
        {
            try
            {
                if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    return BrowserMarkSlotWorkflowApplicationCoordinator.BuildClipboardImportFailure(
                        "Clipboardにテキストがありません。",
                        null);
                }
                text = Clipboard.GetText(TextDataFormat.UnicodeText);
            }
            catch (Exception ex)
            {
                LogService.Error("Clipboard path import failed.", ex);
                return BrowserMarkSlotWorkflowApplicationCoordinator.BuildClipboardImportFailure(
                    "Clipboardを読み取れませんでした。",
                    null);
            }
        }

        string? repositoryRoot = FindRepositoryRoot(_browserApplicationCoordinator.CurrentPath);
        MarkSlotClipboardImportResult importResult = MarkSlotClipboardImportService.Extract(
            text,
            _browserApplicationCoordinator.CurrentPath,
            repositoryRoot,
            AppContext.BaseDirectory);
        if (!importResult.IsSuccess)
        {
            LogService.Warn($"[MarkSlots] KdslResultImportFailed Reason={importResult.FailureReason} Valid={importResult.Paths.Count} Missing={importResult.MissingFileCount} Directory={importResult.DirectoryPathCount} Duplicate={importResult.DuplicatePathCount} Fatal={importResult.FatalCount} IgnoredEarlier={importResult.IgnoredEarlierResultCount}");
            return BrowserMarkSlotWorkflowApplicationCoordinator.BuildClipboardImportFailure(importResult, repositoryRoot);
        }

        MarkSlotClipboardActionResult replacementResult = _browserMarkSlotWorkflowApplicationCoordinator.ApplyClipboardReplacement(
            importResult,
            repositoryRoot,
            BuildBrowserTabStateFromCurrentUi());
        if (!replacementResult.Success)
        {
            LogService.Warn("[MarkSlots] KdslResultImportSkipped Reason=NoValidExistingFiles");
            return replacementResult;
        }

        LogService.Info($"[MarkSlots] KdslResultReplaced CurrentMark Count={replacementResult.Paths.Count} Missing={importResult.MissingFileCount} Directory={importResult.DirectoryPathCount} Duplicate={importResult.DuplicatePathCount} Fatal={importResult.FatalCount} IgnoredEarlier={importResult.IgnoredEarlierResultCount}");
        return replacementResult;
    }

    private void ShowMarkSlotImportResult(MarkSlotClipboardActionResult result)
    {
        if (!result.Success || result.Paths.Count == 0) return;
        if (_integrationSeam?.MarkSlotImportResultDialogOverride is { } dialogOverride)
        {
            dialogOverride(result);
            return;
        }
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
        MarkSlotEntry slot = _browserMarkSlotWorkflowApplicationCoordinator.GetSlot(slotNumber);
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
        if (!_browserMarkSlotWorkflowApplicationCoordinator.TryExportSlot(dialog.FileName, slotNumber, out string errorMessage))
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
        if (!_browserMarkSlotWorkflowApplicationCoordinator.TryImportSlot(dialog.FileName, out MarkSlotEntry? importedSlot, out string errorMessage, out string? warningMessage) ||
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
        MarkSlotActionResult importResult = _browserMarkSlotWorkflowApplicationCoordinator.ImportSlot(
            slotNumber,
            importedSlot,
            MarkSlotCount);
        string message = importResult.Message;
        if (!string.IsNullOrWhiteSpace(warningMessage))
        {
            message += $" / {warningMessage}";
        }
        LogService.Info($"[MarkSlots] Imported Slot={slotNumber} File={dialog.FileName} Count={importedSlot.Paths.Count}");
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
        if (!_browserMarkSlotWorkflowApplicationCoordinator.TryExportAllSlots(dialog.FileName, MarkSlotCount, out string errorMessage))
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
        if (!_browserMarkSlotWorkflowApplicationCoordinator.TryImportAllSlots(dialog.FileName, MarkSlotCount, out MarkSlotStore? importedStore, out string errorMessage, out string? warningMessage) ||
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
        if (!_browserMarkSlotWorkflowApplicationCoordinator.ReplaceSlots(importedStore, MarkSlotCount))
        {
            const string saveError = "全マークスロットを保存できませんでした。ストレージを確認してください。";
            MessageBox.Show(this, saveError, "全マークスロットインポート", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowStatusMessage(saveError);
            return saveError;
        }
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
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            const string busyMessage = "ファイル操作中はマークを変更できません。";
            ShowStatusMessage(busyMessage);
            return busyMessage;
        }

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
        MarkSlotActionResult applyResult = _browserMarkSlotWorkflowApplicationCoordinator.ApplySetOperationToCurrentTab(
            preview,
            BuildBrowserTabStateFromCurrentUi());
        if (!applyResult.Success)
        {
            return applyResult.Message;
        }
        UpdateInfoPanel();
        RefreshVisibleMarkColors();
        RefreshMarkUi();
        RefreshBrowserTabHeaders();
        PrimeRecentMultiMarkIntent();
        LogService.Info($"[MarkSlots] AppliedSlotSetResultToCurrentTab Op={preview.OperationKind} A={preview.SlotANumber} B={preview.SlotBNumber}");
        ShowStatusMessage(applyResult.Message);
        return applyResult.Message;
    }
    private string ToggleCurrentMarksFromDialog(IReadOnlyList<string> paths)
    {
        if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy))
        {
            const string busyMessage = "ファイル操作中はマークを変更できません。";
            ShowStatusMessage(busyMessage);
            return busyMessage;
        }

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
            if (_browserApplicationCoordinator.Selection.Contains(path))
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
        if (ExecuteConfirmedUserDirectoryNavigation(parentDirectory, focusTargetName))
        {
            browserPanel.Focus();
        }
    }
    private string RenameMarkSlot(int slotNumber, string? displayName)
    {
        MarkSlotActionResult result = _browserMarkSlotWorkflowApplicationCoordinator.Rename(
            slotNumber,
            displayName,
            MarkSlotCount);
        LogService.Info($"[MarkSlots] Renamed Slot={slotNumber}");
        ShowStatusMessage(result.Message);
        return result.Message;
    }
    private string DeleteMarkSlot(int slotNumber)
    {
        MarkSlotActionResult result = _browserMarkSlotWorkflowApplicationCoordinator.Delete(slotNumber, MarkSlotCount);
        LogService.Info($"[MarkSlots] Deleted Slot={slotNumber}");
        ShowStatusMessage(result.Message);
        return result.Message;
    }
    private MarkSlotActionResult RemoveMarkSlotItems(int slotNumber, IReadOnlyCollection<string> fullPaths)
    {
        if (fullPaths == null || fullPaths.Count == 0)
        {
            return new MarkSlotActionResult(false, "削除対象のパスが指定されていません。");
        }

        MarkSlotActionResult result = _browserMarkSlotWorkflowApplicationCoordinator.RemoveItems(
            slotNumber,
            fullPaths,
            MarkSlotCount);
        if (result.Success)
        {
            LogService.Info($"[MarkSlots] Removed items from Slot={slotNumber}");
            ShowStatusMessage(result.Message);
        }
        return result;
    }
    private MarkSlotSetOperationPreviewResult BuildMarkSlotSetOperationPreview(int slotANumber, int slotBNumber, string operationKind)
    {
        return _browserMarkSlotWorkflowApplicationCoordinator.BuildSetOperationPreview(
            slotANumber,
            slotBNumber,
            operationKind,
            _browserApplicationCoordinator.CurrentPath);
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
        MarkSlotEntry targetSlot = _browserMarkSlotWorkflowApplicationCoordinator.GetSlot(request.TargetSlotNumber);
        string defaultName = BuildMarkSlotSetOperationDefaultName(request.SlotANumber, request.SlotBNumber, request.OperationKind);
        string? displayName = SimpleInputDialog.ShowNullable(
            $"演算結果 {resultPaths.Count}件をスロット{request.TargetSlotNumber}へ保存します。{Environment.NewLine}表示名を入力してください。",
            "スロット演算結果を保存",
            defaultName,
            new SimpleInputDialog.DisplayOptions(
                SummaryText: "現在タブのマークは変更されません。保存後に反映したい場合は、保存先スロットを選択して復元してください。",
                WarningText: BrowserMarkSlotWorkflowApplicationCoordinator.HasSavedState(targetSlot)
                    ? $"スロット {request.TargetSlotNumber} は上書きされます。"
                    : null));
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
        MarkSlotActionResult saveResult = _browserMarkSlotWorkflowApplicationCoordinator.SaveSetOperation(
            request,
            displayName,
            MarkSlotCount);
        string message = saveResult.Message;
        if (saveResult.Success)
        {
            LogService.Info($"[MarkSlots] SavedSlotSetOperation Target={request.TargetSlotNumber} Op={request.OperationKind} A={request.SlotANumber} B={request.SlotBNumber} Count={resultPaths.Count}");
        }
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
    private static string GetMarkSlotDisplayName(MarkSlotEntry slot)
    {
        return string.IsNullOrWhiteSpace(slot.DisplayName)
            ? $"スロット {slot.SlotNumber}"
            : slot.DisplayName.Trim();
    }
    private static string BuildMarkSlotSetOperationDefaultName(int slotANumber, int slotBNumber, string operationKind)
        => BrowserMarkSlotWorkflowApplicationCoordinator.BuildSetOperationDefaultName(
            slotANumber,
            slotBNumber,
            operationKind);
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
        return _browserApplicationCoordinator.Workspace.ActiveTabIndex >= 0 && _browserApplicationCoordinator.Workspace.ActiveTabIndex < _browserApplicationCoordinator.Workspace.TabCount
            ? _browserApplicationCoordinator.Workspace.TabStates[_browserApplicationCoordinator.Workspace.ActiveTabIndex]
            : null;
    }
    private string GetActiveBrowserTabCategoryDisplayName()
    {
        return _browserApplicationCoordinator.Workspace.CategoryStates
            .FirstOrDefault(category => string.Equals(category.Id, _browserApplicationCoordinator.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName
            ?? (_browserApplicationCoordinator.Workspace.ActiveCategoryId ?? string.Empty);
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
        => BrowserMarkSlotWorkflowApplicationCoordinator.GetOperationLabel(operationKind);
    private BrowserMarkSlotSaveAggregation BuildCurrentCategoryMarkSlotAggregation()
    {
        return _browserMarkSlotWorkflowApplicationCoordinator.BuildCurrentCategoryAggregation(
            BuildBrowserTabStateFromCurrentUi());
    }
    private BrowserMarkSlotSaveAggregation BuildWorkspaceMarkSlotAggregation()
    {
        return _browserMarkSlotWorkflowApplicationCoordinator.BuildWorkspaceAggregation(
            BuildBrowserTabStateFromCurrentUi());
    }
    private string BuildScopedSlotSavePrompt(int slotNumber, MarkSlotEntry slot, BrowserMarkSlotSaveAggregation aggregation)
    {
        string overwriteText = BrowserMarkSlotWorkflowApplicationCoordinator.HasSavedState(slot)
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
    private void BeginPendingEscExitMarkPersistence(IReadOnlyList<string> markedPaths)
    {
        // Preserve pending marks when either restart persistence is enabled or workspace restore is enabled
        bool shouldPreserve = _browserWorkspacePersistenceApplicationCoordinator.ShouldPreservePendingMarks;
        if (!shouldPreserve || markedPaths.Count == 0)
        {
            ClearPendingEscExitMarkPersistence();
            return;
        }
        _browserApplicationCoordinator.SetPendingEscExitPersistedMarks(markedPaths);
        _isClosingFromEscExitPath = false;
    }
    private void ClearPendingEscExitMarkPersistence()
    {
        _browserApplicationCoordinator.SetPendingEscExitPersistedMarks(null);
        _isClosingFromEscExitPath = false;
    }
    private void LaunchMediaPlayback(string fullPath, bool isAudio)
    {
        var launchResult = VideoPlaybackLaunchService.Launch(
            fullPath,
            _settingsCoordinator.Value.Preview?.VideoToolDirectory,
            _settingsCoordinator.Value.Preview?.VideoPlaybackVolumePercent ?? 100,
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

    private ImageViewerForm? GetReusableImageViewer()
    {
        _imageViewers.RemoveAll(v => v.IsDisposed);
        if (!(_settingsCoordinator.Value.Preview?.ReuseImageViewer ?? true))
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
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
    private void OpenImageViewer(string path)
    {
        PreviewKind mediaKind = _viewerWorkflowApplicationCoordinator.ResolveEffectivePreviewKind(path);
        OpenImageViewerCore(path, mediaKind);
    }

    private void OpenImageViewerFromCommand(string path, PreviewKind mediaKind)
    {
        OpenImageViewerCore(path, mediaKind);
    }

    private void OpenImageViewerCore(string path, PreviewKind mediaKind)
    {
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
                    int initialSeconds = _settingsCoordinator.Value.Preview.VideoSkipSeconds;
                    existing.LoadVideoStill(path, _settingsCoordinator.Value.Preview.VideoToolDirectory, initialSeconds, _settingsCoordinator.Value.Preview.VideoPlaybackVolumePercent);
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
        var viewer = new ImageViewerForm(_settingsCoordinator.Value.Preview, _featureGate);
        Rectangle desiredBounds;
        if (_settingsCoordinator.Value.Preview.RememberImageViewerBounds && _settingsCoordinator.Value.Preview.ImageViewerX != -1)
        {
            desiredBounds = new Rectangle(
                _settingsCoordinator.Value.Preview.ImageViewerX,
                _settingsCoordinator.Value.Preview.ImageViewerY,
                _settingsCoordinator.Value.Preview.ImageViewerWidth,
                _settingsCoordinator.Value.Preview.ImageViewerHeight);
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
            int initialSeconds = _settingsCoordinator.Value.Preview.VideoSkipSeconds;
            viewer.LoadVideoStill(path, _settingsCoordinator.Value.Preview.VideoToolDirectory, initialSeconds, _settingsCoordinator.Value.Preview.VideoPlaybackVolumePercent);
        }
        else
        {
            viewer.LoadMedia(path, mediaKind);
        }
        LogService.Info($"[WindowVisibility] NewImageViewer Path={path} Bounds={FormatBoundsForLog(viewer.Bounds)}");
    }

    private void SaveImageViewerBounds(ImageViewerForm viewer, string? reason = null, bool logBounds = false)
    {
        if (!_settingsCoordinator.Value.Preview.RememberImageViewerBounds || viewer.IsDisposed)
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
        _settingsCoordinator.SetImageViewerBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
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
        _ = ExecuteCommandFromUi(CommandIds.BrowserNavigateParent, CommandScope.Browser, "Browser.Backspace");
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
    private IReadOnlyList<string> GetSharedLocationCandidates()
    {
        return BrowserPathEntryCandidateService.BuildCandidates(
            _browserApplicationCoordinator.NavigationSnapshot,
            _browserApplicationCoordinator.Workspace.QuickAccessSnapshot,
            _settingsCoordinator.GetDirectoryMoveHistory());
    }

    private void ApplySortState(SortKind sortKind, bool ascending)
    {
        BrowserManualRefreshExecution execution = _browserNavigationWorkflowApplicationCoordinator.ExecuteSortAndReload(
            sortKind,
            ascending,
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            IsCurrentDirectoryBusy(),
            "現在ディレクトリを再読込しました。",
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());
        ApplyManualRefreshExecution(execution, "現在ディレクトリを再読込しました。");
    }

    private SelectionResult ResolveSelection()
    {
        if (_browserContextMenuSelectionOverride != null)
        {
            return _browserContextMenuSelectionOverride;
        }
        ListViewItem? currentItem = GetCurrentBrowserItem();
        return _browserApplicationCoordinator.ResolveSelection(
            currentItem?.Tag as string,
            currentItem == null || currentItem.Text == "..");
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
    private IReadOnlyList<string> CaptureCurrentMarkedPathSnapshot()
    {
        return _browserApplicationCoordinator.Selection
            .Snapshot()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    private void PrimeRecentMultiMarkIntent()
    {
        IReadOnlyList<string> markedPaths = CaptureCurrentMarkedPathSnapshot();
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || markedPaths.Count <= 1 || string.IsNullOrWhiteSpace(_browserApplicationCoordinator.CurrentPath))
        {
            InvalidateRecentMultiMarkIntent();
            return;
        }
        _browserApplicationCoordinator.SetRecentMultiMarkIntent(
            NavigationService.NormalizeDirectoryForCompare(_browserApplicationCoordinator.CurrentPath),
            _browserApplicationCoordinator.CursorIndex,
            markedPaths);
    }
    private void InvalidateRecentMultiMarkIntent()
    {
        _browserApplicationCoordinator.ClearRecentMultiMarkIntent();
    }
    internal static string BuildMultiMarkGuardMessage(string operationName, string currentName, int markedCount)
        => $"現在はマーク済み項目が {markedCount} 件あります。\n" +
           $"{operationName}する対象を選んでください。\n\n" +
           $"現在行: {currentName}\n" +
           $"マーク済み: {markedCount} 件";
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
            Text = BuildMultiMarkGuardMessage(operationName, currentName, markedCount)
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
            Text = $"マーク済み{markedCount}件(&M)",
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
        if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText && _viewerApplicationCoordinator.LargeFileState != null)
        {
            var state = _viewerApplicationCoordinator.LargeFileState;
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
        string findLabel = string.IsNullOrWhiteSpace(_viewerApplicationCoordinator.SearchKeyword) ? "" : $" | Find:{_viewerApplicationCoordinator.SearchKeyword}";
        return $"[Viewer] Enc:{encLabel} | Wrap:{wrapLabel}{lineLabel}{findLabel} | Enter/Esc:Browser へ戻る";
    }
    private string GetViewerEncodingStatusLabel()
    {
        if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText && _viewerApplicationCoordinator.LargeFileState != null)
        {
            return string.IsNullOrWhiteSpace(_viewerApplicationCoordinator.LargeFileState.DetectedEncodingLabel)
                ? "Unknown"
                : _viewerApplicationCoordinator.LargeFileState.DetectedEncodingLabel;
        }
        if ((_viewerApplicationCoordinator.CurrentKind == PreviewKind.Text
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.Markdown
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.Sqlite)
            && !string.IsNullOrWhiteSpace(_viewerApplicationCoordinator.DetectedEncodingLabel))
        {
            return _viewerApplicationCoordinator.DetectedEncodingLabel;
        }
        return _viewerApplicationCoordinator.EncodingPreference switch
        {
            ViewerEncodingPreference.Utf8 => "UTF-8",
            ViewerEncodingPreference.ShiftJis => "Shift_JIS",
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
        int totalLines = _viewerApplicationCoordinator.TextLineCount;
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
        return _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer
            && viewerTextBox.Visible
            && IsPlainTextBoxViewerKind(_viewerApplicationCoordinator.CurrentKind);
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
        dialog.UpdateProgress(state, _fileOperationApplicationCoordinator.CancellationTokenSource?.IsCancellationRequested ?? false);
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
                canCancel: _fileOperationApplicationCoordinator.CanCancel);
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
    private void ApplyFilterHeaderEmphasis(bool filterActive)
    {
        if (!filterActive)
        {
            if (_filterHeaderEmphasisFont == null)
            {
                return;
            }

            _filterHeaderEmphasisFont.Dispose();
            _filterHeaderEmphasisFont = null;
            lblSort.Font = GetHeaderStatusResponsiveBaseFont();
            return;
        }

        if (_filterHeaderEmphasisFont != null && ReferenceEquals(lblSort.Font, _filterHeaderEmphasisFont))
        {
            return;
        }

        _filterHeaderEmphasisFont?.Dispose();
        Font currentFont = lblSort.Font;
        _filterHeaderEmphasisFont = new Font(currentFont, currentFont.Style | FontStyle.Bold);
        lblSort.Font = _filterHeaderEmphasisFont;
    }
    private void ApplyHeaderStatusFontToControls(Font font)
    {
        _filterHeaderEmphasisFont?.Dispose();
        _filterHeaderEmphasisFont = null;
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
        ApplyFilterHeaderEmphasis(TabFilterLockService.IsActive(
            _browserApplicationCoordinator.FilterPattern,
            GetActiveTabFilterLock()));
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
            $"[LargeTextStatusVisual] Reason={safeReason} elapsedMs={elapsedMs} UiMode={_viewerApplicationCoordinator.Mode} Kind={_viewerApplicationCoordinator.CurrentKind} " +
            $"HasLargeState={_viewerApplicationCoordinator.LargeFileState != null} " +
            $"Enc={_viewerApplicationCoordinator.LargeFileState?.DetectedEncodingLabel ?? "<null>"} " +
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
            $"reqId={reqId} uiMode={_viewerApplicationCoordinator.Mode} kind={kind} " +
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
        if (_viewerApplicationCoordinator.CurrentKind != PreviewKind.LargeText || _viewerApplicationCoordinator.LargeFileState == null)
            return false;
        if (_largeFileControl.TryGetCharacterSelectionRange(out var rawRange))
        {
            var range = NormalizeCharacterSelectionRange(rawRange);
            return await TryCopyLargeFileCharacterSelectionAsync(range, _viewerApplicationCoordinator.Token);
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
        if (_viewerApplicationCoordinator.LargeFileState == null)
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
        long estimatedBytes = EstimateLargeTextSelectionBytes(_viewerApplicationCoordinator.LargeFileState, startLine, endLine);
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
                _viewerApplicationCoordinator.LargeFileState,
                startLine,
                lineCount,
                _viewerWorkflowApplicationCoordinator.ResolveCurrentViewerEncoding(),
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
        if (_viewerApplicationCoordinator.LargeFileState == null)
        {
            return;
        }
        int expectedLineCount = normalized.EndLine - normalized.StartLine + 1;
        LogService.Info(
            $"[LargeTextExport] Start " +
            $"range=({normalized.StartLine}:{normalized.StartColumn})-({normalized.EndLine}:{normalized.EndColumn}) " +
            $"expectedLines={expectedLineCount:N0} " +
            $"totalLines={_viewerApplicationCoordinator.LargeFileState.TotalLines:N0} " +
            $"offsets={_viewerApplicationCoordinator.LargeFileState.LineOffsets.Count:N0} " +
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
        if (_viewerApplicationCoordinator.LargeFileState == null)
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
            _viewerApplicationCoordinator.LargeFileState.DetectedEncoding
                ?? _viewerWorkflowApplicationCoordinator.ResolveCurrentViewerEncoding());
        for (int line = startLine; line <= endLine; line += ChunkLines)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(ChunkLines, endLine - line + 1);
            var lines = await LargeFileLineReaderService.ReadLinesAsync(
                _viewerApplicationCoordinator.LargeFileState,
                line,
                count,
                _viewerWorkflowApplicationCoordinator.ResolveCurrentViewerEncoding(),
                token);
            if (lines.Count != count)
            {
                // ここで読み込み不足をエラーにする (インデックス未完了等のケースを救う)
                throw new IOException(
                    $"LargeText export read count mismatch. requestedStart={line}, requestedCount={count}, actualCount={lines.Count}, offsets={_viewerApplicationCoordinator.LargeFileState.LineOffsets.Count}, totalLines={_viewerApplicationCoordinator.LargeFileState.TotalLines}");
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
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Viewer)
        {
            return false;
        }

        BrowserRefreshProcessRequest refreshRequest = new(
            IsBrowserMode: true,
            IsBusy: IsCurrentDirectoryBusy(),
            IsExitPending: _isExitConfirmationPending || _isClosingFromEscExitPath,
            IsDisposed: IsDisposed || Disposing,
            Options: CreateDirectoryLoadOptions(),
            ColumnCount: _browserApplicationCoordinator.ColumnCount,
            ShellState: CaptureBrowserRefreshShellState());
        ViewerExitApplicationResult exit = _viewerModeApplicationCoordinator.TryExitToBrowser(this, refreshRequest);
        if (!exit.Exited) return false;
        ReturnToUnifiedSearchAfterViewerExit();
        return true;
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
    private bool OpenPathWithShellAssociation(string fullPath)
    {
        string? error = ExternalToolService.OpenWithShellAssociation(
            fullPath,
            _browserApplicationCoordinator.CurrentPath);
        if (error != null)
        {
            ShowStatusMessage(error);
            MessageBox.Show(this, $"関連付けられたアプリで開くことができませんでした。\n理由: {error}", "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        return true;
    }
    private void ExecuteCurrentFileAction(string fullPath)
    {
        ExecuteBrowserOpenRequest(_viewerWorkflowApplicationCoordinator.CreateBrowserOpenRequest(
            fullPath,
            allowExecuteTarget: true));
    }
    private void ShowArchiveContentsOrFallback(string archivePath)
    {
        ArchiveListResult result = ArchiveListService.GetArchiveContents(_settingsCoordinator.Value.SevenZip?.ExePath, archivePath);
        if (result.Success)
        {
            LogService.Info($"Archive contents loaded: {archivePath} Entries={result.Entries.Count}");
            ShowStatusMessage($"archive 内容一覧を表示します: {Path.GetFileName(archivePath)}");
            bool isReadOnly = IsActiveBrowserTabReadOnly();
            using var dialog = new ArchiveListDialog(
                archivePath,
                result.Entries,
                _browserApplicationCoordinator.CurrentPath,
                isReadOnly,
                _settingsCoordinator.Value.Appearance?.DateFormat,
                _settingsCoordinator.Value.Appearance?.SizeFormat,
                _settingsCoordinator.Value.SevenZip?.ExePath);
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
    private async Task ExecuteArchiveExtractAsync(ArchiveExtractRequest request)
    {
        if (GuardReadOnlyBrowserTab("解凍") || GuardMutationBusy("解凍"))
        {
            return;
        }

        _fileOperationApplicationCoordinator.TryStartArchiveExtract(request, this);
        await Task.CompletedTask;
    }

    private async Task ExecuteArchiveExtractAsync(string archivePath, string destinationDirectory)
    {
        if (GuardReadOnlyBrowserTab("解凍") || GuardMutationBusy("解凍"))
        {
            return;
        }

        _fileOperationApplicationCoordinator.TryStartArchiveExtract(
            archivePath,
            destinationDirectory,
            this);
        await Task.CompletedTask;
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
    private async Task UpdateLargeFileVirtualDisplayAsync(
        int reqId,
        int navigationRequestId,
        CancellationToken token,
        bool preserveCharacterSelection = false)
    {
        if (_viewerApplicationCoordinator.LargeFileState == null) return;
        var state = _viewerApplicationCoordinator.LargeFileState;
        try
        {
            var encoding = _viewerWorkflowApplicationCoordinator.ResolveCurrentViewerEncoding();
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
            if (IsCurrentLargeFileNavigationRequest(state, navigationRequestId)
                && _viewerApplicationCoordinator.ActiveRequestId == reqId
                && _viewerApplicationCoordinator.CurrentPreviewTarget == state.FilePath
                && _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer)
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
            if (IsCurrentLargeFileNavigationRequest(state, navigationRequestId) && _viewerApplicationCoordinator.ActiveRequestId == reqId && !token.IsCancellationRequested)
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
        if (_viewerApplicationCoordinator.LargeFileState == null) return;
        // 手動操作や他の移動が走った場合は、予約されていた End ジャンプを解除する
        _viewerWorkflowApplicationCoordinator.SetLargeFilePendingEndAfterIndex(false);
        int max = _largeFileControl.GetMaxFirstVisibleLine();
        int line = Math.Clamp(targetFirstLine, 0, max);
        if (!preserveCharacterSelection && _largeFileControl.HasAnySelection && _viewerApplicationCoordinator.LargeFileState.FirstVisibleLine != line)
        {
            _largeFileControl.ClearSelections();
        }
        // 状態を更新
        _viewerWorkflowApplicationCoordinator.SetLargeFileFirstVisibleLine(line);
        // コントロールのスクロールバー位置を同期 (イベント発火は抑止される)
        _largeFileControl.SetScrollValueSilently(line);
        int reqId = _viewerApplicationCoordinator.CurrentRequestId;
        int navigationRequestId = ++_viewerApplicationCoordinator.LargeFileState.NavigationRequestId;
        // 内容を非同期で更新
        await UpdateLargeFileVirtualDisplayAsync(reqId, navigationRequestId, _viewerApplicationCoordinator.Token, preserveCharacterSelection);
        if (_viewerApplicationCoordinator.LargeFileState is { } state
            && IsCurrentLargeFileNavigationRequest(state, navigationRequestId)
            && characterSelectionAutoScrollDirection != 0)
        {
            _largeFileControl.ExtendCharacterSelectionToVisibleEdge(characterSelectionAutoScrollDirection);
        }
    }

    internal static bool IsCurrentLargeFileNavigationRequest(Models.LargeFilePreviewState state, int requestId)
        => state.NavigationRequestId == requestId;
    private bool IsLargeTextStatusApplyTarget(Models.LargeFilePreviewState state)
    {
        return _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer
            && _viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText
            && ReferenceEquals(_viewerApplicationCoordinator.LargeFileState, state)
            && string.Equals(_viewerApplicationCoordinator.CurrentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase);
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
        if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText)
        {
            LogViewerStatusRoute("ShowStatusMessage", GetViewerStatusLine());
        }
        // Phase: move viewer status to external - internal label no longer used
    }
    private void FileListView_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_browserRefreshWorkflowApplicationCoordinator.IsApplyingDirectoryList || _suppressBrowserSelectionChanged)
        {
            return;
        }
        ApplyBrowserSelectionChanged();
    }

    private void ApplyBrowserSelectionChanged(bool scheduleInfoUpdate = true)
    {
        // マウス操作時の同期: 選択変更を内部状態 (_browserApplicationCoordinator.CursorIndex) に書き戻す
        if (fileListView.SelectedIndices.Count > 0)
        {
            _browserNavigationWorkflowApplicationCoordinator.ApplyCursorSelection(
                _browserApplicationCoordinator.PageStartIndex + fileListView.SelectedIndices[0],
                _browserApplicationCoordinator.ItemsPerPage);
        }

        bool selectionPathChanged = ApplyBrowserSelectionPresentation(scheduleInfoUpdate);
        if (selectionPathChanged)
        {
            RequestPreviewRefresh();
        }
    }

    private void ApplyBrowserSelectionChangedForCommandResult(bool scheduleInfoUpdate = true)
    {
        ApplyBrowserSelectionPresentation(scheduleInfoUpdate);
    }

    private void ApplyTreeSelectionChanged(bool scheduleInfoUpdate = true)
    {
        if (GetCurrentPreviewSelectionPath() is null)
        {
            ApplyBrowserSelectionChangedForCommandResult(scheduleInfoUpdate);
            return;
        }

        ApplyBrowserSelectionChanged(scheduleInfoUpdate);
    }

    private bool ApplyBrowserSelectionPresentation(bool scheduleInfoUpdate)
    {
        // Info/Name 行を debounce 更新 (カーソル移動に伴う補助表示のみ遅延)
        if (scheduleInfoUpdate)
        {
            ScheduleUpdateInfoPanelDebounced();
        }
        // プレビューエンコーディングを Auto にリセット
        _viewerWorkflowApplicationCoordinator.ResetEncodingPreference();
        var currentItem = GetCurrentBrowserItem();
        string? currentPath = currentItem?.Tag as string;
        bool selectionPathChanged = _browserSelectionIdentityGate.TryAccept(currentPath, _browserRefreshWorkflowApplicationCoordinator.DirectoryContentGeneration);
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
        else if (selectionPathChanged && !isImageSelection && (_settingsCoordinator.Value.Preview?.CloseImageViewerOnNonImageSelection ?? false))
        {
            CloseImageViewers();
        }
        long selElapsedMs = (Stopwatch.GetTimestamp() - selStartTime) * 1000 / Stopwatch.Frequency;
        LogService.Info($"[Browser.SelectionChanged.End] selectionId={selectionId} elapsedMs={selElapsedMs}");
        UpdateMenuStripState();

        if (functionBarPanel.Visible)
        {
            functionBarPanel.Invalidate();
        }

        return selectionPathChanged;
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
        _viewerWorkflowApplicationCoordinator.ClearPreviewState();
        // Viewer パネルをクリア
        if (viewerPanel != null)
        {
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
        return _viewerWorkflowApplicationCoordinator.ResolveEffectivePreviewKind(fullPath ?? string.Empty, rawKind);
    }
    /// <summary>パス種別を UNC/DriveLetter/Unknown に分類する。フルパスは返さない。</summary>
    private string GetBrowserSelectionPathKind(string? path)
    {
        return _browserApplicationCoordinator.GetPathKind(path);
    }

    /// <summary>パスのルート部分を診断ログ用に丸めて返す。フルパスは返さない。</summary>
    private string GetBrowserSelectionPathRoot(string? path)
    {
        return _browserApplicationCoordinator.GetPathRoot(path);
    }

    private string? ResolveViewerClickedUrl(string? linkText)
    {
        return ViewerWorkflowApplicationCoordinator.ResolveViewerClickedUrl(
            _viewerApplicationCoordinator.CurrentKind,
            linkText);
    }
    private static bool IsPlainTextBoxViewerKind(PreviewKind kind)
    {
        return ViewerWorkflowApplicationCoordinator.IsPlainTextViewerKind(kind);
    }
    private bool IsLatestPreviewRequest(int reqId, string requestPath, CancellationToken token)
    {
        return _viewerApplicationCoordinator.IsLatestRequest(
            reqId,
            requestPath,
            token,
            GetCurrentPreviewSelectionPath());
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
                BeginInvoke(new Action(async () =>
                {
                    try
                    {

                        if (IsDisposed || !IsHandleCreated)
                        {
                            return;
                        }
                        if (_viewerApplicationCoordinator.LargeFileState != state
                            || _viewerApplicationCoordinator.Mode != ViewerApplicationMode.Viewer
                            || _viewerApplicationCoordinator.CurrentKind != PreviewKind.LargeText
                            || !string.Equals(_viewerApplicationCoordinator.CurrentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            LogService.Info(
                                $"[LargeTextIndexSwap] Skip stale apply reqId={reqId} " +
                                $"statePath='{state.FilePath}' current='{_viewerApplicationCoordinator.CurrentPreviewTarget}'");
                            return;
                        }
                        LogService.Info(
                            $"[LargeTextIndexSwap] Before UI swap reqId={reqId} " +
                            $"visibleOffsets={state.LineOffsets.Count} " +
                            $"builtOffsets={result.LineOffsets.Count}");
                        _viewerWorkflowApplicationCoordinator.CompleteLargeFileIndex(
                            state,
                            result.LineOffsets,
                            result.TotalBytes,
                            _largeFileControl.VisibleLineCount);
                        _largeFileControl.UpdateScrollSettings();
                        ApplyViewerStatusLine("LargeText immutable index swap completed");
                        LogService.Info(
                            $"[LargeTextIndexSwap] After UI swap reqId={reqId} " +
                            $"visibleOffsets={state.LineOffsets.Count} isIndexing={state.IsIndexing}");
                        bool searchHitApplied = await ApplySearchHitToLargeTextPreviewAsync(
                            state, reqId, result.LineOffsets.Count);
                        if (!searchHitApplied && _viewerWorkflowApplicationCoordinator.ConsumeLargeFilePendingEndAfterIndex())
                        {
                            _ = NavigateLargeFilePreviewAsync(
                                _largeFileControl.GetMaxFirstVisibleLine(),
                                "PendingEndAfterIndex");
                        }
                        else if (!searchHitApplied)
                        {
                            _largeFileControl.Invalidate();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Error($"[LargeTextIndexSwap] UI apply failed reqId={reqId}", ex);
                        if (_pendingUnifiedSearchHit is { RequestId: var pendingRequestId } pending
                            && pendingRequestId == reqId
                            && string.Equals(pending.FullPath, state.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            pending.ResultsView.SetActionStatus("LargeTextプレビューの行移動に失敗しました。");
                            _pendingUnifiedSearchHit = null;
                        }
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
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_pendingUnifiedSearchHit is { RequestId: var pendingRequestId } pending
                            && pendingRequestId == reqId
                            && string.Equals(pending.FullPath, state.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            pending.ResultsView.SetActionStatus("LargeTextの行索引を作成できず、ヒット行へ移動できませんでした。");
                            _pendingUnifiedSearchHit = null;
                        }
                    }));
                }
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
        string? exePath = _settingsCoordinator.Value.ExternalTools?.ExternalViewerPath;
        bool allowShellFallback = _settingsCoordinator.Value.ExternalTools?.FallbackToShellWhenViewerMissing ?? true;
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
        var result = ConfiguredEditorService.Open(fullPath, _settingsCoordinator.Value.ExternalTools?.ExternalEditorPath);
        if (result.Message != null) ShowStatusMessage(result.Message);
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
        string? exePath = _settingsCoordinator.Value.ExternalTools?.ExternalDiffPath;
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
        string? error = ExternalToolService.ExecuteShell(_browserApplicationCoordinator.CurrentPath, command);
        if (error != null) ShowStatusMessage(error);
    }
    private void ExecuteShellDialog()
    {
        string initialValue = string.Empty;
        var item = GetCurrentBrowserItem();
        if (item != null && item.Text != ".." && item.Tag is string fullPath && File.Exists(fullPath))
        {
            initialValue = CommandExecutionDialog.BuildSelectedFileCommand(fullPath);
        }
        CommandExecutionRequest? request = CommandExecutionDialog.Show(
            initialValue,
            defaultArguments: string.Empty,
            defaultWorkingDirectory: _browserApplicationCoordinator.CurrentPath);
        if (request == null || string.IsNullOrWhiteSpace(request.Command)) return;
        string? error = ExternalToolService.ExecuteShellCommand(
            request.Command,
            request.Arguments,
            request.WorkingDirectory,
            _browserApplicationCoordinator.CurrentPath);
        if (error != null) ShowStatusMessage(error);
    }
    private void ExecuteBrowserOpenRequest(ViewerOpenRequest? request)
    {
        if (request == null)
        {
            return;
        }

        ViewerOpenRequest openRequest = request.Value;
        switch (openRequest.Route)
        {
            case ViewerOpenRoute.ExecuteTarget:
                ExecuteConfirmedFile(openRequest.FullPath);
                break;
            case ViewerOpenRoute.Archive:
                ShowArchiveContentsOrFallback(openRequest.FullPath);
                break;
            case ViewerOpenRoute.MediaViewer:
                OpenImageViewer(openRequest.FullPath);
                break;
            case ViewerOpenRoute.InternalViewer:
                EnterInternalViewer(openRequest.ViewerKind);
                break;
        }
    }
    private void EnterInternalViewer(PreviewKind kind)
        => SwitchUIMode(ViewerApplicationMode.Viewer, kind);
    private void RequestPreviewRefresh()
    {
        RequestPreviewRefresh(force: false);
    }
    private void RequestPreviewRefresh(bool force, PreviewKind? previewKindOverride = null)
        => _viewerPreviewApplicationCoordinator.RequestRefresh(
            GetCurrentPreviewSelectionPath(),
            force,
            previewKindOverride,
            this);
    private void OpenSettingsForm(SettingsForm.InitialTab initialTab = SettingsForm.InitialTab.Display)
    {
        bool importedSettingsFlow = false;
        try
        {
            LogService.Info($"Opening SettingsForm. initialTab={initialTab}");
            HideTransientOverlaysBeforeModalDialog();
            BrowserWorkspaceRuntimeStateSnapshot runtimeBrowserTabState = CaptureBrowserTabRuntimeStateSnapshot();
            using var form = new SettingsForm(
                _settingsCoordinator.Value,
                _settingsCoordinator.FeatureProfile,
                initialTab,
                _settingsCoordinator);
            form.OpenManagedTrashDialogRequested += (s, e) =>
            {
                OpenManagedTrashDialog();
            };
            bool settingsApplied = false;

            void ApplySavedSettings(bool imported)
            {
                AppSettings previousSettings = _settingsCoordinator.Value.Clone();
                SettingsReloadApplicationResult applied = _settingsCoordinator.ReloadAfterDialog(previousSettings);
                settingsApplied = true;
                if (applied.IsLoggingOnlyChange)
                {
                    LogService.ApplySettings(applied.Value.Logging);
                    ShowStatusMessage("Logging設定を適用しました。");
                    return;
                }

                ApplyFeatureProfile(applied.Metadata.IsMouseGesturesExplicit);
                ApplySettingsAppliedBrowserRuntimeState(runtimeBrowserTabState);
                LogService.ApplySettings(applied.Value.Logging);
                LogFontRouteDiag("SettingsApplied:BeforeApplyFontSettings");
                ApplyFontSettings();
                ApplyColorSettings();
                ApplyBrowserTabStripDisplaySettings();
                RefreshBrowserTabHeaders();
                viewerTextBox.WordWrap = applied.Value.Preview.ViewerWordWrap;
                viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? ScrollBars.Vertical : ScrollBars.Both;
                SetMarkdownViewerMode(applied.Value.Preview.MarkdownViewerMode, save: false);
                RebuildMenuStripAfterSettingsApply();
                UpdateFunctionBar();
                LogFontRouteDiag("SettingsApplied:AfterAll");
                ShowStatusMessage(imported
                    ? "設定をインポートし、現在の設定へ反映しました。"
                    : "設定を適用しました。");
            }

            form.SettingsApplied += (s, e) =>
            {
                ApplySavedSettings(imported: false);
            };
            var result = form.ShowDialog(this);
            importedSettingsFlow = form.ImportedSettingsApplied;
            LogService.Info($"SettingsForm closed. result={result}");
            if (result == DialogResult.OK && !settingsApplied)
            {
                ApplySavedSettings(importedSettingsFlow);
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
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "Workspace Snapshot は設定で無効です。"))
        {
            return;
        }
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage)
        {
            MessageBox.Show(this, "Workspace スナップショットの保存先を初期化できません。", "Workspace スナップショット", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var dialog = new WorkspaceSnapshotDialog(
            _browserWorkspaceSnapshotApplicationCoordinator.LoadEntries,
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
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage)
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
        if (_browserWorkspaceSnapshotApplicationCoordinator.ExistsByName(trimmedName) &&
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
        WorkspaceState state = _browserWorkspaceSnapshotApplicationCoordinator.CaptureCurrentState();
        if (!_browserWorkspaceSnapshotApplicationCoordinator.Save(trimmedName, state, out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "Workspace スナップショット保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"Workspace スナップショットを保存しました: {trimmedName}");
        return true;
    }
    private bool RestoreWorkspaceSnapshot(IWin32Window owner, WorkspaceSnapshotEntry entry)
    {
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage)
        {
            MessageBox.Show(owner, "Workspace スナップショットの保存先を初期化できません。", "Workspace スナップショット復元", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        BrowserWorkspaceSnapshotRestoreApplicationResult result =
            _browserWorkspaceSnapshotApplicationCoordinator.ExecuteRestore(
                entry,
                BuildBrowserTabStateFromCurrentUi(),
                CreateDirectoryLoadOptions(),
                _browserApplicationCoordinator.ColumnCount,
                CaptureBrowserRefreshShellState(),
                this);
        if (result.Succeeded)
        {
            ShowStatusMessage($"Workspace スナップショットを復元しました: {entry.Name}");
            return true;
        }
        if (result.WasCanceled)
        {
            return false;
        }

        Exception restoreError = result.Error ?? new InvalidOperationException("Workspace snapshot restore failed.");
        LogService.Error("Workspace snapshot restore failed.", restoreError);
        MessageBox.Show(owner, $"Workspace スナップショットの復元に失敗しました。\n{restoreError.Message}", "Workspace スナップショット復元", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }
    private bool RenameWorkspaceSnapshot(IWin32Window owner, WorkspaceSnapshotEntry entry)
    {
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage)
        {
            MessageBox.Show(owner, "Workspace スナップショットの保存先を初期化できません。", "Workspace スナップショット名変更", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        string? renamed = SimpleInputDialog.ShowNullable("新しいスナップショット名を入力してください。", "Workspace スナップショット名変更", entry.Name);
        if (renamed == null)
        {
            return false;
        }
        if (!_browserWorkspaceSnapshotApplicationCoordinator.Rename(entry.SnapshotId, renamed, out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "Workspace スナップショット名変更", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"Workspace スナップショット名を変更しました: {renamed.Trim()}");
        return true;
    }
    private bool DeleteWorkspaceSnapshot(IWin32Window owner, WorkspaceSnapshotEntry entry)
    {
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage)
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
        if (!_browserWorkspaceSnapshotApplicationCoordinator.Delete(entry.SnapshotId))
        {
            MessageBox.Show(owner, "スナップショットを削除できませんでした。", "Workspace スナップショット削除", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"Workspace スナップショットを削除しました: {entry.Name}");
        return true;
    }
    private bool ExportWorkspaceSnapshot(IWin32Window owner, WorkspaceSnapshotEntry entry)
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "Workspace Snapshot エクスポートは設定で無効です。"))
        {
            return false;
        }
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage) return false;
        using var sfd = new SaveFileDialog
        {
            Title = "Workspace スナップショットをエクスポート",
            Filter = "MidFD Workspace Snapshot (*.midfd-workspace-snapshot.json)|*.midfd-workspace-snapshot.json|JSON files (*.json)|*.json",
            FileName = $"{entry.Name}.midfd-workspace-snapshot.json"
        };
        if (sfd.ShowDialog(owner) != DialogResult.OK) return false;
        if (!_browserWorkspaceSnapshotApplicationCoordinator.Export(
            entry.SnapshotId,
            new WorkspaceSnapshotMetadata
            {
                Name = entry.Name,
                CreatedAtUtc = entry.CreatedAtUtc,
                UpdatedAtUtc = entry.UpdatedAtUtc
            },
            sfd.FileName,
            out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "エクスポート失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"スナップショットをエクスポートしました: {Path.GetFileName(sfd.FileName)}");
        return true;
    }
    private bool ImportWorkspaceSnapshot(IWin32Window owner)
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "Workspace Snapshot インポートは設定で無効です。"))
        {
            return false;
        }
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage) return false;
        using var ofd = new OpenFileDialog
        {
            Title = "Workspace スナップショットをインポート",
            Filter = "MidFD Workspace Snapshot (*.midfd-workspace-snapshot.json;*.json)|*.midfd-workspace-snapshot.json;*.json"
        };
        if (ofd.ShowDialog(owner) != DialogResult.OK) return false;
        if (!_browserWorkspaceSnapshotApplicationCoordinator.Import(
            ofd.FileName,
            Path.GetFileNameWithoutExtension(ofd.FileName),
            out string importedName,
            out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "インポート失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"スナップショットをインポートしました: {importedName}");
        return true;
    }
    private bool ExportAllWorkspaceSnapshots(IWin32Window owner)
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "Workspace Snapshot 一括エクスポートは設定で無効です。"))
        {
            return false;
        }
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage) return false;
        int snapshotCount = _browserWorkspaceSnapshotApplicationCoordinator.SnapshotCount;
        if (snapshotCount == 0)
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
        if (!_browserWorkspaceSnapshotApplicationCoordinator.ExportAll(sfd.FileName, out int exportedCount, out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "一括エクスポート失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"全 {exportedCount} 件のスナップショットを一括エクスポートしました。");
        return true;
    }
    private bool ImportAllWorkspaceSnapshots(IWin32Window owner)
    {
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "Workspace Snapshot 一括インポートは設定で無効です。"))
        {
            return false;
        }
        if (!_browserWorkspaceSnapshotApplicationCoordinator.HasStorage) return false;
        using var ofd = new OpenFileDialog
        {
            Title = "全 Workspace スナップショットを一括インポート",
            Filter = "MidFD Workspace Snapshot Backup (*.midfd-workspace-backupset.json;*.json)|*.midfd-workspace-backupset.json;*.json"
        };
        if (ofd.ShowDialog(owner) != DialogResult.OK) return false;
        if (!_browserWorkspaceSnapshotApplicationCoordinator.ImportAll(ofd.FileName, out int importedCount, out string errorMessage))
        {
            MessageBox.Show(owner, errorMessage, "一括インポート失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        ShowStatusMessage($"{importedCount} 件のスナップショットを一括インポートしました。");
        return true;
    }
    private QuickAccessCommandContext BuildQuickAccessCommandContext()
    {
        ListViewItem? currentItem = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser ? GetCurrentBrowserItem() : null;
        string? currentItemPath = null;
        string? currentItemName = null;
        bool currentItemIsDirectory = false;
        IReadOnlyList<string> markedPaths = _browserApplicationCoordinator.Selection
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
            CurrentPath = _browserApplicationCoordinator.CurrentPath,
            CurrentItemPath = currentItemPath,
            CurrentItemName = currentItemName,
            CurrentItemIsDirectory = currentItemIsDirectory,
            MarkedPaths = markedPaths
        };
    }
    private void ExecuteHistoryBack()
    {
        ExecuteHistoryNavigation(BrowserHistoryDirection.Back, "戻る履歴がありません。");
    }
    private void ExecuteHistoryForward()
    {
        ExecuteHistoryNavigation(BrowserHistoryDirection.Forward, "進む履歴がありません。");
    }
    private void ExecuteHistoryNavigation(
        BrowserHistoryDirection direction,
        string unavailableMessage)
    {
        BrowserTabState currentState = BuildBrowserTabStateFromCurrentUi();
        BrowserHistoryNavigationExecution execution = _browserNavigationWorkflowApplicationCoordinator.ExecuteHistoryNavigation(
            direction,
            currentState,
            _browserTabWorkflowApplicationCoordinator.MaxTabCount,
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState(),
            confirmedDerivedTabCreation: null);
        BrowserHistoryNavigationStart operation = execution.Start;
        if (operation.RequiresDerivedTabConfirmation)
        {
            if (!ShowBrowserDerivedTabNavigationConfirmation())
            {
                return;
            }
            execution = _browserNavigationWorkflowApplicationCoordinator.ExecuteHistoryNavigation(
                direction,
                currentState,
                _browserTabWorkflowApplicationCoordinator.MaxTabCount,
                CreateDirectoryLoadOptions(),
                _browserApplicationCoordinator.ColumnCount,
                CaptureBrowserRefreshShellState(),
                confirmedDerivedTabCreation: true);
        }
        operation = execution.Start;
        if (execution.Kind == BrowserHistoryNavigationExecutionKind.NotAvailable)
        {
            ShowStatusMessage(unavailableMessage);
            return;
        }
        if (execution.Kind == BrowserHistoryNavigationExecutionKind.NotReady &&
            operation.Kind == BrowserHistoryNavigationStartKind.DirectoryMissing)
        {
            MessageBox.Show(
                $"指定されたパスが見つかりません: {operation.TargetPath}",
                "エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }
        if (execution.Kind == BrowserHistoryNavigationExecutionKind.Failed && execution.Load?.Error != null)
        {
            NotifyDirectoryLoadFailure(execution.Load.Value.Error);
            return;
        }
        if (operation.DerivedTabIndex.HasValue)
        {
            PrepareDerivedBrowserTabPresentation(operation.DerivedTabIndex);
        }

        if (execution.Load is { Succeeded: true } load)
        {
            ApplyDirectoryLoadUi(
                load,
                CreateDerivedBrowserTabSelectionCallback(operation.DerivedTabIndex));
            ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
        }
        if (execution.Load is { Succeeded: true } && !execution.Result.Committed)
        {
            ShowStatusMessage("履歴移動を確定できませんでした。");
        }
    }
    private bool ExecuteDirectoryNavigationRequest(
        BrowserNavigationCoordinator.DirectoryNavigationRequest? request)
    {
        if (request == null)
        {
            return false;
        }
        return ExecuteConfirmedUserDirectoryNavigation(
            request.TargetPath,
            request.FocusTargetName,
            request.IsHistoryNavigation,
            request.SuppressRecent);
    }
    private void HandleFuncKeyClick(int index)
    {
        if (_unifiedSearchSession is { IsActive: true } search)
        {
            switch (index)
            {
                case 0: search.View.ActivateSelected(); break;
                case 1: search.View.ExportResults(); break;
                case 2: CloseUnifiedSearchSession(restoreSource: true); break;
            }
            return;
        }
        // Phase 3-input-alias1: ExecuteFunctionKey 内部で UIMode 判定と GuardClipboardBusy を行う
        // index 0=F1, 1=F2, ... 11=F12
        ExecuteFunctionKey(index + 1);
    }
    private void ApplyFontSettings()
    {
        if (_settingsCoordinator.Value.Fonts == null) return;
        LogFontRouteDiag("ApplyFontSettings:START");
        // Phase 2f-fix2: レイアウト遷移中の中間描画を抑制する
        this.SuspendLayout();
        try
        {
            // ファイラー用
            var filerFamily = _settingsCoordinator.Value.Fonts.FileListFontFamily;
            var filerSize = _settingsCoordinator.Value.Fonts.FileListFontSize;
            var filerFont = new Font(filerFamily, filerSize);
            fileListView.Font = filerFont;
            browserPanel.Font = filerFont;
            if (_browserTabStrip != null)
            {
                _browserTabStrip.Font = new Font("Consolas", _settingsCoordinator.Value.BrowserTabs?.TabFontSize ?? BrowserTabSettings.DefaultTabFontSize, FontStyle.Regular, GraphicsUnit.Point);
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
            var viewerFamily = _settingsCoordinator.Value.Fonts.ViewerFontFamily;
            var viewerSize = _settingsCoordinator.Value.Fonts.ViewerFontSize;
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

    private void HeaderPanel_Paint(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(MidFDColors.BorderLine);
        e.Graphics.DrawLine(pen, 0, 0, Math.Max(0, headerPanel.ClientSize.Width - 1), 0);
    }

    /// <summary>
    /// Phase 2g-fix4b: 指定されたラベルのテキストを ":" で分割し、配色を変えて描画する。
    /// </summary>
    private void DrawRow2ZoneText(Graphics g, Panel zone, Label lbl, Font font)
    {
        var headerColors = HeaderColorPaletteResolver.Resolve(_settingsCoordinator.Value.Appearance);
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
            _browserNavigationWorkflowApplicationCoordinator.ApplyCursorSelection(0, _browserApplicationCoordinator.ItemsPerPage);
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
        _browserNavigationWorkflowApplicationCoordinator.ApplyCursorSelection(
            _browserApplicationCoordinator.PageStartIndex + targetItem.Index,
            _browserApplicationCoordinator.ItemsPerPage);
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
                if (!string.Equals(path, _browserApplicationCoordinator.CurrentPath, StringComparison.OrdinalIgnoreCase))
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

        bool showBreadcrumb = _settingsCoordinator.Value.Appearance?.ShowPathAsBreadcrumb == true;
        lblPath.Visible = !showBreadcrumb;
        if (_breadcrumbPathControl != null)
        {
            _breadcrumbPathControl.Font = lblPath.Font;
            _breadcrumbPathControl.ForeColor = lblPath.ForeColor;
            SyncBreadcrumbPathPresentation();
            _breadcrumbPathControl.Visible = showBreadcrumb;
            if (showBreadcrumb)
            {
                _breadcrumbPathControl.BringToFront();
            }
        }
    }
    private void SyncBreadcrumbPathPresentation()
    {
        _breadcrumbPathControl?.SetPath(_browserApplicationCoordinator.CurrentPath);
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
            sb.Append($" listFontSz={_settingsCoordinator.Value?.Fonts?.FileListFontSize ?? -1}");
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
        return StatusColorResolver.Resolve(kind, _resolvedColors, _settingsCoordinator.Value?.Appearance);
    }
}
