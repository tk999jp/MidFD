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
using System.Runtime.InteropServices;
using System.Media;
using MidFD.Models;
using MidFD.Helpers;
using MidFD.Services.TrashManifestStore;
using MidFD.Services.Workspace;
namespace MidFD;
public partial class MainForm : Form
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
    private const int CurrentDirectoryRefreshDebounceMilliseconds = 300;
    private const int CurrentDirectoryRefreshRetryDelayMilliseconds = 100;
    private const int WM_SIZE = 0x0005;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_SHOWWINDOW = 0x0018;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_SIZE = 0xF000;
    private const int SC_MOVE = 0xF010;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_MAXIMIZE = 0xF030;
    private const int SC_CLOSE = 0xF060;
    private const int SC_RESTORE = 0xF120;
    private const int SC_KEYMENU = 0xF100;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int SW_RESTORE = 9;
    private const int MinimumNormalWindowWidth = 200;
    private const int MinimumNormalWindowHeight = 480;
    private const int MinimumUsableClientAreaHeight = 120;
    private Rectangle? _lastKnownGoodNormalBounds;
    private bool _isApplyingWindowBoundsRecovery;
    private Rectangle? _normalBoundsBeforeMinimize;
    private DateTime _lastRestoreUtc = DateTime.MinValue;
    private bool _isInRestorePlacementWatch;
    private Rectangle? _restoreBaselineNormalBounds;
    private bool _restorePlacementRepairScheduled;
    private int _restorePlacementRepairCount;
    private Rectangle? _pendingRestoreRepairBounds;
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public int flags;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, [In] ref WINDOWPLACEMENT lpwndpl);
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
        public override string ToString() => $"({left},{top},{right},{bottom}) {right - left}x{bottom - top}";
    }
#pragma warning disable CS0649 // Win32 API 構造体のフィールドへの代入警告を抑制
    private struct POINT
    {
        public int x;
        public int y;
        public override string ToString() => $"({x},{y})";
    }
    private struct MinMaxInfo
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
#pragma warning restore CS0649
    private static readonly HashSet<string> _executeTargetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".lnk"
    };
    private readonly NavigationService _navigationService;
    private readonly BrowserInputRouter _browserInputRouter = new();
    private readonly ViewerInputRouter _viewerInputRouter = new();
    private readonly BrowserNavigationCoordinator _browserNavigationCoordinator = new();
    private readonly ViewerPreviewCoordinator _viewerPreviewCoordinator = new();
    private readonly CommandStateCoordinator _commandStateCoordinator = new();
    private readonly BrowserLoadCoordinator _browserLoadCoordinator = new();
    private readonly FileOperationEntryCoordinator _fileOperationEntryCoordinator = new();
    private readonly FileOperationDialogCoordinator _fileOperationDialogCoordinator = new();
    private readonly FileOperationPostOperationCoordinator _fileOperationPostOperationCoordinator = new();
    private readonly RenameDialogCoordinator _renameDialogCoordinator = new();
    private readonly RenameApplyCoordinator _renameApplyCoordinator = new();
    private readonly MarkSelectionState _markedFiles = new();
    private readonly FileOperationUndoRedoService _fileOperationUndoRedoService = new();
    private AppSettings _settings;
    private readonly string? _startupProfileOverride;
    private FeatureProfile _featureProfile = FeatureProfile.Full;
    private FeatureGateService _featureGate = new(FeatureProfile.Full);
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _fileOpCts; // Phase 3-fileop-async1: コピー等の非同期操作用
    private long _fileOperationCancelRequestedTimestamp;
    private string? _activeFileOperationName;
    private int _fileOperationStatusVersion;
    private FileOperationProgressFallbackForm? _shellDeleteProgressFallback;
    private FileOperationProgressFallbackForm? _undoRedoProgressFallback;
    private FileOperationProgressFallbackForm? _archiveProgressFallback;
    private string? _currentPreviewTarget; // 非同期競合チェック用 (パス)
    private bool _isBrowserAutoPreviewSuppressed;
    private string? _lastBrowserAutoPreviewSuppressedMessage;
    private string? _lastPreviewRequestedPath;
    private bool _previewRequestInFlight;
    private int _previewRequestId = 0;
    private int _activePreviewRequestId = 0; // 最新UI反映待ちのリクエストID
    private readonly PreviewPopupForm _previewPopup; // プレビューPopupウィンドウ
    private readonly List<ImageViewerForm> _imageViewers = new(); // 起動中の画像ビューア
    private PreviewKind _currentViewerKind = PreviewKind.None;
    private string _currentViewerDetectedEncodingLabel = string.Empty;
    private enum UIMode { Browser, Viewer }
    private UIMode _uiMode = UIMode.Browser;
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
    private int _browserCursorIndex = 0; // 現在フォーカスを持つアイテムのインデックス
    private bool _markSummaryDirty = true;
    private string _markSummaryCache = string.Empty;
    private string _markSummaryCachePath = string.Empty;
    private int _markSummaryCacheCount = -1;
    private bool _recentMultiMarkIntentActive;
    private string _recentMultiMarkIntentDirectory = string.Empty;
    private int _recentMultiMarkIntentCursorIndex = -1;
    private IReadOnlyList<string> _recentMultiMarkIntentMarkedPaths = Array.Empty<string>();
    // Phase 2g-fix3a: Row 1 専用時計 Timer
    private System.Windows.Forms.Timer? _headerClockTimer;
    private Font? _headerPaintFont; // titleHeaderPanel_Paint で使用するフォント保持用
    // Phase 3-fix2b: Drag-out (MidFD → 外部) 用の状態管理
    private Point _dragStartPoint = Point.Empty;
    private int _dragCandidateIndex = -1;
    private bool _isClipboardBusy = false;
    private bool _isFileOperationUndoRedoBusy = false;
    private readonly NotificationService _notificationService;
    private DateTime _statusNoticeHoldUntilUtc = DateTime.MinValue;
    private readonly record struct ExternalToolAltHintRow(string SlotLabel, string Title, string ExecutableName);
    private static readonly HashSet<char> ReservedExternalToolAltSlots = new() { 'F', 'V', 'G', 'T', 'H' };
    private QuickAccessStore _quickAccessStore;
    private readonly MarkSlotStore _markSlotStore;
    private bool _isAltHintHeld;
    private IReadOnlyList<ExternalToolAltHintRow> _commandHintRows = Array.Empty<ExternalToolAltHintRow>();
    private readonly System.Windows.Forms.Timer _commandHintOverlayTimer = new();
    private readonly System.Windows.Forms.Timer _directoryRefreshDebounceTimer = new();
    private int _functionBarPreferredHeight = 24;
    private int _lastLoggedCommandHintRowCount = -1;
    private Rectangle _lastLoggedCommandHintBounds = Rectangle.Empty;
    private Size _lastLoggedCommandHintPanelSize = Size.Empty;
    private readonly List<ToolStripItem> _browserOnlyMenuItems = new();
    private readonly List<ToolStripItem> _busyAwareMenuItems = new();
    private readonly Dictionary<ToolStripItem, CommandStateCoordinator.MenuItemStateRule> _menuItemRules = new();
    private readonly List<BrowserTabState> _browserTabs = new();
    private readonly List<BrowserTabCategoryDefinition> _browserTabCategories = new();
    private int _activeBrowserTabIndex = -1;
    private string _activeBrowserTabCategoryId = BrowserTabSettings.DefaultCategoryId;
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
    private ToolStripMenuItem? _reloadCurrentDirectoryMenuItem;
    private ToolStripMenuItem? _clearTabFilterLockMenuItem;
    private ContextMenuStrip? _browserTabContextMenu;
    private ToolStripMenuItem? _toggleBrowserTabLockContextMenuItem;
    private ToolStripMenuItem? _toggleBrowserTabReadOnlyContextMenuItem;
    private ToolStripMenuItem? _openBrowserTabFilterLockContextMenuItem;
    private ToolStripMenuItem? _clearBrowserTabFilterLockContextMenuItem;
    private ToolStripMenuItem? _closeBrowserTabContextMenuItem;
    private ToolStripMenuItem? _closeRightBrowserTabsContextMenuItem;
    private ToolStripMenuItem? _closeLeftBrowserTabsContextMenuItem;
    private ToolStripMenuItem? _closeOtherBrowserTabsContextMenuItem;
    private int _browserTabContextIndex = -1;
    private ContextMenuStrip? _browserTabCategoryContextMenu;
    private ToolStripMenuItem? _addBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _moveBrowserTabCategoryLeftContextMenuItem;
    private ToolStripMenuItem? _moveBrowserTabCategoryRightContextMenuItem;
    private ToolStripMenuItem? _renameBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _deleteBrowserTabCategoryContextMenuItem;
    private ToolStripMenuItem? _manageBrowserTabCategoriesContextMenuItem;
    private FileSystemWatcher? _currentDirectoryWatcher;
    private string? _currentDirectoryWatcherPath;
    private bool _pendingExternalDirectoryRefresh;
    private string? _pendingExternalDirectoryRefreshPath;
    private string _pendingExternalDirectoryRefreshReason = "外部変更";
    private bool _isApplyingExternalDirectoryRefresh;
    private bool _currentDirectoryRefreshRetryPending;
    private string? _browserTabCategoryContextCategoryId;
    private BrowserTabStripCategoryItemKind _browserTabCategoryContextKind = BrowserTabStripCategoryItemKind.Category;
    private DateTime _lastBrowserTabLimitBeepUtc = DateTime.MinValue;
    private List<string>? _pendingEscExitPersistedMarks;
    private bool _isClosingFromEscExitPath;
    private IWorkspaceStateStore? _workspaceStateStore;
    private WorkspaceSnapshotStorage? _workspaceSnapshotStorage;
    private bool _restoredBrowserTabsFromWorkspaceStore;
    private readonly MouseGestureRecognizer _mouseGestureRecognizer = new();
    private bool _suppressNextBrowserContextMenu;
    private DateTime _suppressBrowserContextMenuUntilUtc = DateTime.MinValue;
    private readonly List<ClosedBrowserTabSnapshot> _closedBrowserTabs = new();
    private const int ClosedBrowserTabHistoryLimit = 10;
    // browser header interaction polish fields
    private bool _headerInteractionInitialized;
    private ToolTip? _headerToolTip;
    private ContextMenuStrip? _headerPathContextMenu;
    private ContextMenuStrip? _headerItemContextMenu;
    public MainForm(string? startupProfileOverride = null)
    {
        _startupProfileOverride = startupProfileOverride;
        InitializeComponent();
        this.MinimumSize = new Size(MinimumNormalWindowWidth, MinimumNormalWindowHeight);
        statusStrip.ShowItemToolTips = false;
        NormalizeStatusLabelLayout();
        statusStrip.Resize += (_, _) => NormalizeStatusLabelLayout();
        // Phase 3-mainform-status1.1: 初期化失敗・読込失敗メッセージを安全に出すため前倒し
        _notificationService = new NotificationService(this.statusLabel, this.messageTimer);
        // Phase 3-mainform-nav1: ナビゲーション状態管理の初期化
        _navigationService = new NavigationService();
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
        _settings = SettingsManager.Load(out SettingsManager.SettingsLoadMetadata settingsLoadMetadata);
        _settings.Input ??= new InputSettings();
        ApplyFeatureProfile(settingsLoadMetadata.IsMouseGesturesExplicit);
        _workspaceStateStore = WorkspaceStateStoreFactory.CreateDefault();
        _workspaceSnapshotStorage = new WorkspaceSnapshotStorage(WorkspaceStateStoreFactory.GetDefaultDbPath());
        MidFdManagedTrashService.Initialize(_settings);
        SyncActiveBrowserTabCategoryFromSession();
        LogService.ApplySettings(_settings.Logging);
        MidFDColors.ApplyTheme(_settings.Appearance?.ColorTheme);
        _quickAccessStore = QuickAccessService.LoadOrMigrate(_settings.QuickAccess);
        _markSlotStore = MarkSlotStorage.Load(MarkSlotCount);
        IReadOnlyList<ExternalToolAltHintRow> startupHintRows = BuildExternalToolAltHintRows();
        string startupFirstHint = startupHintRows.Count > 0
            ? $"{startupHintRows[0].SlotLabel}:{startupHintRows[0].Title}"
            : "<none>";
        LogAltHint($"Startup rows={startupHintRows.Count} first={startupFirstHint}");
        // Phase 36: ヘッダ初期化
        lblTitle.Text = "<< MidFD >>";
        lblClock.Text = DateTime.Now.ToString("yyyy-MM-dd(ddd) HH:mm:ss");
        _previewPopup = new PreviewPopupForm();
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
        messageTimer.Tick += (_, _) =>
        {
            if (_uiMode == UIMode.Viewer)
            {
                ApplyViewerStatusLine("messageTimer viewer restore");
            }
        };
        if (_settings.Session.RestoreColumnCount)
        {
            _columnCount = Math.Clamp(_settings.Session.LastColumnCount, 1, 9);
        }
        if (_settings.Session.RestoreSort)
        {
            _currentSort = _settings.Session.LastSortKind;
            _sortAscending = _settings.Session.LastSortAscending;
        }
        KeyUp += MainForm_KeyUp;
        Deactivate += (_, _) =>
        {
            LogAltHintContext("Deactivate");
            _isAltHintHeld = false;
            HideCommandHintOverlay();
        };
        _commandHintOverlayTimer.Interval = 50;
        _commandHintOverlayTimer.Tick += (_, _) => RefreshCommandHintOverlayState();
        _commandHintOverlayTimer.Start();
        _directoryRefreshDebounceTimer.Interval = CurrentDirectoryRefreshDebounceMilliseconds;
        _directoryRefreshDebounceTimer.Tick += (_, _) =>
        {
            _directoryRefreshDebounceTimer.Stop();
            TryProcessPendingCurrentDirectoryRefresh("DebounceTimer");
        };
        // 初期パスの決定 (起動引数 -> 保存されたパス -> カレントディレクトリ)
        string startupPath = Environment.CurrentDirectory;
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Directory.Exists(args[1]))
        {
            startupPath = args[1];
        }
        else if (!_settings.Session.RestoreTabsOnStartup &&
            _settings.Session.RestoreLastPath
            && !string.IsNullOrEmpty(_settings.Session.LastPath)
            && Directory.Exists(_settings.Session.LastPath))
        {
            startupPath = _settings.Session.LastPath;
        }
        // ウィンドウ位置・サイズの復元
        if (_settings.Session.RestoreWindowBounds)
        {
            RestoreWindowSettings();
        }
        // Phase 3-layout-fix6: Resize 配線を ApplyFontSettings より前に移動
        this.functionBarPanel.Resize += (s, e) => LayoutFunctionBar();
        InitializeHeaderDeclutterLayout();
        InitializeHeaderInteractionPolish();
        ApplyFontSettings();
        ApplyColorSettings();
        InitializeBrowserTabControl();
        InitializeMenuStrip();
        LogAltHintContext("InitializeMenuStrip");
        bool restoredTabs = TryRestoreBrowserTabsOnStartup(out int restoredTabCount, out int skippedTabCount, out bool hadSavedTabs);
        if (!restoredTabs)
        {
            LoadDirectory(startupPath);
            InitializeInitialBrowserTab();
        }
        if (!_settings.Session.RestoreTabsOnStartup)
        {
            LogService.Info("[MarkPersistence] Legacy persisted marks restore skipped because workspace restore is disabled.");
        }
        else if (restoredTabs && (_restoredBrowserTabsFromWorkspaceStore || _browserTabs.Any(tab => tab.MarkedPaths.Count > 0)))
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
        else if (_settings.Session.RestoreTabsOnStartup && hadSavedTabs)
        {
            ShowStatusMessage("前回のタブは見つからないため、通常の開始状態で開きました。");
        }
        UpdateMenuStripState();
        this.fileListView.SelectedIndexChanged += FileListView_SelectedIndexChanged;
        // Phase 3-fix1c: browserPanel に対する基本マウス操作の追加
        this.browserPanel.MouseClick += BrowserPanel_MouseClick;
        this.browserPanel.MouseDoubleClick += BrowserPanel_MouseDoubleClick;
        // Phase 3-fix1d: ホイールスクロールの追加とフォーカス補助
        this.browserPanel.MouseWheel += BrowserPanel_MouseWheel;
        this.browserPanel.MouseEnter += (s, e) => { if (_uiMode == UIMode.Browser) this.browserPanel.Focus(); };
        // Phase 3-fix2a: 外部 → MidFD Drag-in (Copy限定)
        this.browserPanel.AllowDrop = true;
        this.browserPanel.DragEnter += BrowserPanel_DragEnter;
        this.browserPanel.DragDrop += BrowserPanel_DragDrop;
        // Phase 3-fix2b: MidFD → 外部 Drag-out (Copy限定)
        this.browserPanel.MouseDown += BrowserPanel_MouseDown;
        this.browserPanel.MouseMove += BrowserPanel_MouseMove;
        this.browserPanel.MouseUp += BrowserPanel_MouseUp;
        // Phase 5-funcbar-click-fix1: FunctionBar のクリック復旧 (描画セグメント判定)
        this.functionBarPanel.MouseClick += FunctionBarPanel_MouseClick;
        // Phase 2g-fix2: ウィンドウリサイズ時にも Row 2 の Zone 幅を再計算する
        this.headerPanel.Resize += (s, e) => LayoutHeaderZones();
        // Phase 2g-fix3a: Row 1 時計更新 Timer を開始
        StartHeaderClockTimer();
        // Phase 2g-fix3b: Row 1 の再描画責務分離と局所ちらつき低減
        EnableDoubleBuffering(this.titleHeaderPanel);
        EnableDoubleBuffering(this.contentFramePanel);
        this.titleHeaderPanel.Resize += (s, e) =>
        {
            this.titleHeaderPanel.Invalidate();
            this.contentFramePanel.Invalidate();
        };
        this.contentFramePanel.Resize += (s, e) => this.contentFramePanel.Invalidate();
        // Phase 2g-fix4b.1: Row 2 の Custom Paint 配線
        headerZone1.Paint += HeaderZone_Paint;
        headerZone2.Paint += HeaderZone_Paint;
        headerZone3.Paint += HeaderZone_Paint;
        headerZone4.Paint += HeaderZone_Paint;
        // Zone自体のちらつきを抑える
        EnableDoubleBuffering(headerZone1);
        EnableDoubleBuffering(headerZone2);
        EnableDoubleBuffering(headerZone3);
        EnableDoubleBuffering(headerZone4);
        EnableDoubleBuffering(browserPanel);
        // Phase 3-layout-fix1: BrowserPanel のリサイズ再描画
        this.browserPanel.Resize += BrowserPanel_Resize;
        // Phase 3-bottom-funcbar-click1: FunctionBar のラベルクリック配線
        for (int i = 0; i < lblFuncKeys.Length; i++)
        {
            int index = i; // クロージャ用
            lblFuncKeys[i].Click += (s, e) => HandleFuncKeyClick(index);
        }
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
            _directoryRefreshDebounceTimer.Stop();
            DisposeCurrentDirectoryWatcher();
            SaveWindowSettings();
            SavePreviewSettings();
        };
        this.Move += (s, e) => PositionPreviewPopup();
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
            }
        };
        this.Activated += MainForm_Activated;
        this.Shown += MainForm_Shown; // Phase 2g-fix6.2c: 初期フォーカス安定化
        // 初期 FunctionBar 表示
        UpdateFunctionBar();
    }
    private void MainForm_Shown(object? sender, EventArgs e)
    {
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
        CaptureActiveBrowserTabState();
        _settings.Session.LastPath = _navigationService.CurrentPath;
        if (!_settings.Session.RestoreTabsOnStartup)
        {
            SavePersistedMarksToSettings();
        }
        else
        {
            LogService.Info("[MarkPersistence] Legacy persisted marks save skipped because workspace restore is enabled.");
        }
        SaveBrowserTabsToSettings();
        SaveWorkspaceStateStore();
        _settings.Session.LastColumnCount = _columnCount;
        _settings.Session.LastSortKind = _currentSort;
        _settings.Session.LastSortAscending = _sortAscending;
        LogService.Info($"[WindowVisibility] SaveWindowSettings State={this.WindowState} Bounds={FormatBoundsForLog(this.Bounds)} RestoreBounds={FormatBoundsForLog(this.RestoreBounds)} Saved=({_settings.Window.X},{_settings.Window.Y},{_settings.Window.Width},{_settings.Window.Height})");
        SettingsManager.Save(_settings);
    }
    private void SavePersistedMarksToSettings()
    {
        _settings.Session ??= new SessionSettings();
        if (!_settings.Session.PersistMarksAcrossRestart)
        {
            LogService.Info("[MarkPersistence] Save skipped because persistence is disabled.");
            return;
        }
        var sourcePaths = (_isClosingFromEscExitPath && _pendingEscExitPersistedMarks is { Count: > 0 })
            ? _pendingEscExitPersistedMarks
            : _markedFiles.Snapshot();
        var persistedPaths = sourcePaths
            .Where(PathExists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.Session.PersistedMarkedPaths = persistedPaths;
        string saveMode = (_isClosingFromEscExitPath && _pendingEscExitPersistedMarks is { Count: > 0 })
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
private void InitializeBrowserTabControl()
{
    _browserTabHostPanel = new Panel
    {
        Dock = DockStyle.Top,
        Height = GetBrowserTabStripHostHeight(),
        BackColor = MidFDColors.ListNormalBack,
        Margin = Padding.Empty,
        Name = "browserTabHostPanel",
        Padding = Padding.Empty
    };
    _browserTabHostPanel.Resize += (s, e) => LayoutBrowserTabControlWithinHost();
    _browserTabStrip = new BrowserTabStrip
    {
        Height = GetBrowserTabStripHostHeight(),
        Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point),
        Name = "browserTabStrip",
        BackColor = MidFDColors.ListNormalBack,
        ForeColor = MidFDColors.ListNormalFore,
        TabStop = false,
        PreferredTabWidth = 140,
        ActiveTabBackColor = MidFDColors.ListSelectedBack,
        InactiveTabBackColor = MidFDColors.ListNormalBack,
        TabBorderColor = MidFDColors.BorderLine,
        ActiveTabTextColor = _settings.Appearance?.ColorTheme == "Light" ? Color.Black : Color.Yellow,
        InactiveTabTextColor = MidFDColors.ListNormalFore,
        ShowCategoryRow = ShouldShowBrowserTabCategoryRow()
    };
    _browserTabStrip.Anchor = AnchorStyles.Top | AnchorStyles.Left;
    _browserTabStrip.CategoryClicked += BrowserTabStrip_CategoryClicked;
    _browserTabStrip.AddTabClicked += BrowserTabStrip_AddTabClicked;
    _browserTabStrip.SelectedIndexChanged += BrowserTabStrip_SelectedIndexChanged;
    _browserTabStrip.TabReordered += BrowserTabStrip_TabReordered;
    _browserTabStrip.TabDoubleClicked += BrowserTabStrip_TabDoubleClicked;
    _browserTabStrip.TabRightClicked += BrowserTabStrip_TabRightClicked;
    _browserTabHostPanel.Controls.Add(_browserTabStrip);
    outerHostPanel.Controls.Add(_browserTabHostPanel);
    outerHostPanel.Controls.SetChildIndex(_browserTabHostPanel, 1);
    LayoutBrowserTabControlWithinHost();
}
    private bool ShouldShowBrowserTabCategoryRow()
    {
        return _settings.Appearance?.ShowBrowserTabCategoryRow ?? true;
    }
    private int GetBrowserTabStripHostHeight()
    {
        return ShouldShowBrowserTabCategoryRow()
            ? BrowserTabStripMultiRowHeight
            : BrowserTabStripSingleRowHeight;
    }
    private void ApplyBrowserTabStripDisplaySettings()
    {
        int targetHeight = GetBrowserTabStripHostHeight();
        if (_browserTabHostPanel != null)
        {
            _browserTabHostPanel.Height = targetHeight;
        }
        if (_browserTabStrip != null)
        {
            _browserTabStrip.ShowCategoryRow = ShouldShowBrowserTabCategoryRow();
            _browserTabStrip.Height = targetHeight;
        }
        LayoutBrowserTabControlWithinHost();
    }
    private void InitializeInitialBrowserTab()
    {
        var initialState = BuildBrowserTabStateFromCurrentUi();
        _browserTabs.Clear();
        _browserTabs.Add(initialState);
        _activeBrowserTabIndex = 0;
        RefreshBrowserTabHeaders();
        if (_browserTabStrip != null && _browserTabs.Count > 0)
        {
            _suppressBrowserTabSelectionChanged = true;
            _browserTabStrip.SelectedIndex = 0;
            _suppressBrowserTabSelectionChanged = false;
        }
    }
    private void EnsureBrowserTabCategoryConfiguration()
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        _settings.Session ??= new SessionSettings();
        _browserTabCategories.Clear();
        var normalizedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BrowserTabCategoryDefinition category in _settings.BrowserTabs.Categories ?? Enumerable.Empty<BrowserTabCategoryDefinition>())
        {
            string normalizedId = NormalizeBrowserTabCategoryId(category.Id);
            if (!normalizedIds.Add(normalizedId))
            {
                continue;
            }
            _browserTabCategories.Add(new BrowserTabCategoryDefinition
            {
                Id = normalizedId,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName.Trim()
            });
        }
        if (_browserTabCategories.Count == 0)
        {
            _browserTabCategories.Add(CreateDefaultBrowserTabCategoryDefinition());
        }
        _settings.BrowserTabs.Categories = _browserTabCategories
            .Select(static category => category.Clone())
            .ToList();
        _settings.Session.ActiveBrowserTabCategoryId = ResolveExistingBrowserTabCategoryId(_settings.Session.ActiveBrowserTabCategoryId);
    }
    private void SyncActiveBrowserTabCategoryFromSession()
    {
        EnsureBrowserTabCategoryConfiguration();
        string sessionCategoryId = _settings.Session.BrowserTabRestoreSnapshot?.ActiveCategoryId
            ?? _settings.Session.ActiveBrowserTabCategoryId;
        _activeBrowserTabCategoryId = ResolveExistingBrowserTabCategoryId(sessionCategoryId);
    }
    private string NormalizeBrowserTabCategoryId(string? categoryId)
    {
        string trimmed = string.IsNullOrWhiteSpace(categoryId)
            ? BrowserTabSettings.DefaultCategoryId
            : categoryId.Trim();
        return string.Equals(trimmed, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase)
            ? BrowserTabSettings.DefaultCategoryId
            : trimmed;
    }
    private string ResolveExistingBrowserTabCategoryId(string? categoryId)
    {
        string normalizedId = NormalizeBrowserTabCategoryId(categoryId);
        if (_browserTabCategories.Any(category => string.Equals(category.Id, normalizedId, StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedId;
        }
        return _browserTabCategories.FirstOrDefault()?.Id ?? BrowserTabSettings.DefaultCategoryId;
    }
    private static BrowserTabCategoryDefinition CreateDefaultBrowserTabCategoryDefinition()
    {
        return new BrowserTabCategoryDefinition
        {
            Id = BrowserTabSettings.DefaultCategoryId,
            DisplayName = "既定"
        };
    }
    private BrowserTabCategoryDefinition EnsureAtLeastOneBrowserTabCategoryAfterDeletion()
    {
        if (_browserTabCategories.Count > 0)
        {
            return _browserTabCategories[0];
        }
        string displayName = GenerateNextBrowserTabCategoryDisplayName();
        var generatedCategory = new BrowserTabCategoryDefinition
        {
            Id = CreateUniqueBrowserTabCategoryId(displayName),
            DisplayName = displayName
        };
        _browserTabCategories.Add(generatedCategory);
        return generatedCategory;
    }
    private sealed class BrowserTabRuntimeStateSnapshot
    {
        public List<BrowserTabCategoryDefinition> CategoryDefinitions { get; init; } = new();
        public BrowserTabRestoreSnapshot RestoreSnapshot { get; init; } = new();
        public string ActiveCategoryId { get; init; } = BrowserTabSettings.DefaultCategoryId;
    }
    private void SyncBrowserTabCategoryDefinitionsToSettings()
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        _settings.BrowserTabs.Categories = _browserTabCategories
            .Select(static category => category.Clone())
            .ToList();
    }
    private BrowserTabRuntimeStateSnapshot CaptureBrowserTabRuntimeStateSnapshot()
    {
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        return new BrowserTabRuntimeStateSnapshot
        {
            CategoryDefinitions = _browserTabCategories
                .Select(static category => category.Clone())
                .ToList(),
            RestoreSnapshot = EnsureBrowserTabRestoreSnapshot().Clone(),
            ActiveCategoryId = ResolveExistingBrowserTabCategoryId(_activeBrowserTabCategoryId)
        };
    }
    private static List<BrowserTabCategorySessionState> BuildCategorySessionStatesFromSnapshot(BrowserTabRestoreSnapshot snapshot)
    {
        return snapshot.Categories
            .Where(static category => category != null && !string.IsNullOrWhiteSpace(category.Id))
            .Select(static category => new BrowserTabCategorySessionState
            {
                CategoryId = category.Id,
                ActiveTabIndex = category.ActiveTabIndex,
                OpenTabs = category.OpenTabs.Select(static tab => tab.Clone()).ToList()
            })
            .ToList();
    }
    private void RestoreBrowserTabRuntimeStateSnapshot(BrowserTabRuntimeStateSnapshot runtimeState)
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        _settings.Session ??= new SessionSettings();
        _settings.BrowserTabs.Categories = runtimeState.CategoryDefinitions
            .Select(static category => category.Clone())
            .ToList();
        _settings.Session.BrowserTabRestoreSnapshot = runtimeState.RestoreSnapshot.Clone();
        EnsureBrowserTabCategoryConfiguration();
        _activeBrowserTabCategoryId = ResolveExistingBrowserTabCategoryId(runtimeState.ActiveCategoryId);
        _settings.Session.ActiveBrowserTabCategoryId = _activeBrowserTabCategoryId;
        _settings.Session.BrowserTabCategories = BuildCategorySessionStatesFromSnapshot(_settings.Session.BrowserTabRestoreSnapshot);
        BrowserTabRestoreCategoryState? activeCategoryState = FindBrowserTabRestoreCategoryState(_activeBrowserTabCategoryId);
        _settings.Session.OpenTabs = activeCategoryState?.OpenTabs.Select(static tab => tab.Clone()).ToList()
            ?? new List<BrowserTabSessionState>();
        _settings.Session.ActiveTabIndex = activeCategoryState?.ActiveTabIndex ?? 0;
        List<BrowserTabState> targetTabs = LoadBrowserTabsForCategory(_activeBrowserTabCategoryId);
        int targetIndex = Math.Clamp(
            ResolveBrowserTabCategoryActiveIndex(_activeBrowserTabCategoryId, targetTabs.Count),
            0,
            Math.Max(0, targetTabs.Count - 1));
        _browserTabs.Clear();
        _browserTabs.AddRange(targetTabs);
        _browserTabContextIndex = -1;
        RefreshBrowserTabHeaders();
        if (_browserTabs.Count > 0)
        {
            _activeBrowserTabIndex = -1;
            SwitchBrowserTab(targetIndex);
        }
        else
        {
            _activeBrowserTabIndex = -1;
        }
        RefreshBrowserTabHeaders();
        _browserTabStrip?.Invalidate();
        _browserTabHostPanel?.Invalidate();
    }
    private static List<BrowserTabCategoryDefinition> BuildBrowserTabCategoryDefinitionsFromSnapshot(BrowserTabRestoreSnapshot snapshot)
    {
        return snapshot.Categories
            .Where(static category => category != null && !string.IsNullOrWhiteSpace(category.Id))
            .Select(static category => new BrowserTabCategoryDefinition
            {
                Id = category.Id,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName
            })
            .ToList();
    }
    private static BrowserTabRuntimeStateSnapshot CreateBrowserTabRuntimeStateSnapshot(WorkspaceState workspaceState)
    {
        BrowserTabRestoreSnapshot snapshot = workspaceState.RestoreSnapshot.Clone();
        return new BrowserTabRuntimeStateSnapshot
        {
            CategoryDefinitions = BuildBrowserTabCategoryDefinitionsFromSnapshot(snapshot),
            RestoreSnapshot = snapshot,
            ActiveCategoryId = string.IsNullOrWhiteSpace(snapshot.ActiveCategoryId)
                ? BrowserTabSettings.DefaultCategoryId
                : snapshot.ActiveCategoryId
        };
    }
    private WorkspaceState CaptureWorkspaceSnapshotState()
    {
        BrowserTabRuntimeStateSnapshot runtimeState = CaptureBrowserTabRuntimeStateSnapshot();
        return new WorkspaceState
        {
            RestoreSnapshot = runtimeState.RestoreSnapshot.Clone(),
            SavedAtUtc = DateTime.UtcNow
        };
    }
    private BrowserTabRestoreSnapshot EnsureBrowserTabRestoreSnapshot()
    {
        _settings.Session ??= new SessionSettings();
        BrowserTabRestoreSnapshot snapshot = (_settings.Session.BrowserTabRestoreSnapshot ?? new BrowserTabRestoreSnapshot()).Clone();
        var existingStates = snapshot.Categories
            .Where(static category => category != null)
            .GroupBy(category => NormalizeBrowserTabCategoryId(category.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Clone(),
                StringComparer.OrdinalIgnoreCase);
        var normalizedSnapshot = new BrowserTabRestoreSnapshot
        {
            ActiveCategoryId = ResolveExistingBrowserTabCategoryId(snapshot.ActiveCategoryId)
        };
        foreach (BrowserTabCategoryDefinition category in _browserTabCategories)
        {
            string categoryId = NormalizeBrowserTabCategoryId(category.Id);
            existingStates.TryGetValue(categoryId, out BrowserTabRestoreCategoryState? existingState);
            normalizedSnapshot.Categories.Add(new BrowserTabRestoreCategoryState
            {
                Id = categoryId,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName.Trim(),
                ActiveTabIndex = existingState?.ActiveTabIndex ?? 0,
                OpenTabs = existingState?.OpenTabs.Select(static tab => tab.Clone()).ToList() ?? new List<BrowserTabSessionState>()
            });
        }
        if (normalizedSnapshot.Categories.Count == 0)
        {
            normalizedSnapshot.Categories.Add(new BrowserTabRestoreCategoryState
            {
                Id = BrowserTabSettings.DefaultCategoryId,
                DisplayName = "既定"
            });
        }
        normalizedSnapshot.ActiveCategoryId = ResolveExistingBrowserTabCategoryId(normalizedSnapshot.ActiveCategoryId);
        _settings.Session.BrowserTabRestoreSnapshot = normalizedSnapshot;
        return normalizedSnapshot;
    }
    private BrowserTabRestoreCategoryState? FindBrowserTabRestoreCategoryState(string categoryId)
    {
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        string resolvedCategoryId = ResolveExistingBrowserTabCategoryId(categoryId);
        return snapshot.Categories.FirstOrDefault(
            category => string.Equals(category.Id, resolvedCategoryId, StringComparison.OrdinalIgnoreCase));
    }
    private string CreateUniqueBrowserTabCategoryId(string displayName)
    {
        string baseId = Regex.Replace(displayName.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "category";
        }
        if (string.Equals(baseId, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            baseId = "category";
        }
        string candidate = baseId;
        int suffix = 2;
        while (_browserTabCategories.Any(category => string.Equals(category.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix}";
            suffix++;
        }
        return candidate;
    }
    private void SaveBrowserTabsToSettings()
    {
        EnsureBrowserTabCategoryConfiguration();
        _settings.Session ??= new SessionSettings();
        if (!_settings.Session.RestoreTabsOnStartup)
        {
            _settings.Session.ClearBrowserTabRestoreState();
            LogService.Info("[BrowserTabs] Save cleared because tab restore is disabled.");
            return;
        }
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        string activeCategoryId = ResolveExistingBrowserTabCategoryId(_activeBrowserTabCategoryId);
        BrowserTabRestoreCategoryState? activeCategoryState = FindBrowserTabRestoreCategoryState(activeCategoryId);
        int activeTabIndex = activeCategoryState?.ActiveTabIndex ?? 0;
        int tabCount = activeCategoryState?.OpenTabs?.Count ?? 0;
        LogService.Info($"[BrowserTabs] Saved Category={activeCategoryId} Tabs={tabCount} ActiveIndex={activeTabIndex}");
    }
    private void SaveWorkspaceStateStore()
    {
        if (_workspaceStateStore == null)
        {
            return;
        }
        try
        {
            if (!_settings.Session.RestoreTabsOnStartup)
            {
                _workspaceStateStore.Clear();
                LogService.Info("[WorkspaceStore] Cleared because workspace restore is disabled.");
                return;
            }
            BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot().Clone();
            _workspaceStateStore.Save(WorkspaceStateMigrationService.FromSessionSnapshot(snapshot));
            LogService.Info($"[WorkspaceStore] Saved categories={snapshot.Categories.Count} active={snapshot.ActiveCategoryId}");
        }
        catch (Exception ex)
        {
            LogService.Error("Workspace state save failed. Session snapshot fallback remains available.", ex);
        }
    }
    private bool TryLoadWorkspaceStateStore(out BrowserTabRestoreSnapshot? snapshot)
    {
        snapshot = null;
        if (_workspaceStateStore == null)
        {
            return false;
        }
        try
        {
            WorkspaceState? workspaceState = _workspaceStateStore.Load();
            if (workspaceState?.RestoreSnapshot?.Categories is not { Count: > 0 })
            {
                LogService.Info("[WorkspaceStore] No workspace restore state found.");
                return false;
            }
            snapshot = workspaceState.RestoreSnapshot.Clone();
            LogService.Info($"[WorkspaceStore] Loaded categories={snapshot.Categories.Count} active={snapshot.ActiveCategoryId}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Workspace state load failed. Falling back to SessionSettings snapshot.", ex);
            return false;
        }
    }
    private void ApplyWorkspaceRestoreSnapshotToSettings(BrowserTabRestoreSnapshot snapshot)
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        _settings.Session ??= new SessionSettings();
        _settings.Session.BrowserTabRestoreSnapshot = snapshot.Clone();
        _settings.BrowserTabs.Categories = snapshot.Categories
            .Select(static category => new BrowserTabCategoryDefinition
            {
                Id = string.IsNullOrWhiteSpace(category.Id) ? BrowserTabSettings.DefaultCategoryId : category.Id,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName
            })
            .ToList();
        _settings.Session.BrowserTabCategories = BuildCategorySessionStatesFromSnapshot(snapshot);
        _settings.Session.ActiveBrowserTabCategoryId = string.IsNullOrWhiteSpace(snapshot.ActiveCategoryId)
            ? BrowserTabSettings.DefaultCategoryId
            : snapshot.ActiveCategoryId;
        BrowserTabRestoreCategoryState? activeCategory = snapshot.Categories.FirstOrDefault(
            category => string.Equals(category.Id, _settings.Session.ActiveBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Categories.FirstOrDefault();
        _settings.Session.OpenTabs = activeCategory?.OpenTabs.Select(static tab => tab.Clone()).ToList()
            ?? new List<BrowserTabSessionState>();
        _settings.Session.ActiveTabIndex = activeCategory?.ActiveTabIndex ?? 0;
    }
    private List<BrowserTabSessionState> SerializeBrowserTabsForSession(IReadOnlyList<BrowserTabState> sourceTabs, out int activeTabIndex)
    {
        IReadOnlyList<BrowserTabState> limitedTabs = sourceTabs;
        int maxTabCount = GetMaxBrowserTabsPerCategory();
        if (limitedTabs.Count > maxTabCount)
        {
            LogService.Warn($"[BrowserTabs] Save source exceeded max tabs. Source={limitedTabs.Count} Max={maxTabCount}");
            limitedTabs = limitedTabs.Take(maxTabCount).ToList();
            ShowStatusMessage($"タブ数が上限を超えたため、{maxTabCount} 個まで保存しました。");
        }
        List<BrowserTabSessionState> serializedTabs = limitedTabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.CurrentPath))
            .Select(CreateBrowserTabSessionState)
            .ToList();
        activeTabIndex = serializedTabs.Count == 0
            ? 0
            : Math.Clamp(_activeBrowserTabIndex, 0, serializedTabs.Count - 1);
        return serializedTabs;
    }
    private void StoreActiveBrowserTabCategorySessionState(bool updateCompatibilityMirror)
    {
        EnsureBrowserTabCategoryConfiguration();
        _settings.Session ??= new SessionSettings();
        string activeCategoryId = ResolveExistingBrowserTabCategoryId(_activeBrowserTabCategoryId);
        List<BrowserTabSessionState> serializedTabs = SerializeBrowserTabsForSession(_browserTabs, out int activeTabIndex);
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        snapshot.ActiveCategoryId = activeCategoryId;
        BrowserTabRestoreCategoryState? categoryState = snapshot.Categories.FirstOrDefault(
            category => string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase));
        if (categoryState == null)
        {
            categoryState = new BrowserTabRestoreCategoryState
            {
                Id = activeCategoryId,
                DisplayName = _browserTabCategories
                    .FirstOrDefault(category => string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase))
                    ?.DisplayName ?? activeCategoryId
            };
            snapshot.Categories.Add(categoryState);
        }
        categoryState.DisplayName = _browserTabCategories
            .FirstOrDefault(category => string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? categoryState.DisplayName;
        categoryState.ActiveTabIndex = activeTabIndex;
        categoryState.OpenTabs = serializedTabs.Select(static tab => tab.Clone()).ToList();
        _settings.Session.BrowserTabRestoreSnapshot = snapshot;
        if (updateCompatibilityMirror)
        {
            _settings.Session.ActiveBrowserTabCategoryId = activeCategoryId;
            _settings.Session.BrowserTabCategories = UpsertBrowserTabCategorySessionState(
                _settings.Session.BrowserTabCategories,
                new BrowserTabCategorySessionState
                {
                    CategoryId = activeCategoryId,
                    OpenTabs = serializedTabs.Select(static tab => tab.Clone()).ToList(),
                    ActiveTabIndex = activeTabIndex
                });
            _settings.Session.OpenTabs = serializedTabs.Select(static tab => tab.Clone()).ToList();
            _settings.Session.ActiveTabIndex = activeTabIndex;
        }
        LogService.Info(
            $"[BrowserTabCategory] Store Category={activeCategoryId} Tabs={serializedTabs.Count} ActiveIndex={activeTabIndex} " +
            $"MirrorUpdated={updateCompatibilityMirror}");
    }
    private static List<BrowserTabCategorySessionState> UpsertBrowserTabCategorySessionState(
        IEnumerable<BrowserTabCategorySessionState>? existingStates,
        BrowserTabCategorySessionState updatedState)
    {
        var mergedStates = new List<BrowserTabCategorySessionState>();
        bool replaced = false;
        foreach (BrowserTabCategorySessionState state in existingStates ?? Enumerable.Empty<BrowserTabCategorySessionState>())
        {
            if (state == null || string.IsNullOrWhiteSpace(state.CategoryId))
            {
                continue;
            }
            if (string.Equals(state.CategoryId, updatedState.CategoryId, StringComparison.OrdinalIgnoreCase))
            {
                mergedStates.Add(updatedState.Clone());
                replaced = true;
            }
            else
            {
                mergedStates.Add(state.Clone());
            }
        }
        if (!replaced)
        {
            mergedStates.Add(updatedState.Clone());
        }
        return mergedStates;
    }
    private static BrowserTabSessionState CreateBrowserTabSessionState(BrowserTabState tabState)
    {
        NavigationService.NavigationSnapshot navigation = tabState.Navigation ?? new NavigationService.NavigationSnapshot();
        return new BrowserTabSessionState
        {
            TabId = tabState.Id == Guid.Empty ? Guid.NewGuid() : tabState.Id,
            CurrentPath = tabState.CurrentPath,
            IsLocked = tabState.IsLocked,
            StartupPath = tabState.StartupPath,
            IsReadOnly = tabState.IsReadOnly,
            FilterLock = tabState.FilterLock?.Clone() ?? new TabFilterLockState(),
            MarkedPaths = CreatePersistableMarkedPaths(tabState.MarkedPaths, out _),
            BackHistory = navigation.BackHistory.ToList(),
            ForwardHistory = navigation.ForwardHistory.ToList(),
            LastVisitedPathByDrive = navigation.LastVisitedPathByDrive.ToDictionary(
                static pair => pair.Key.ToString(),
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            FocusTargetName = tabState.FocusTargetName,
            CursorIndex = tabState.CursorIndex,
            ColumnCount = tabState.ColumnCount,
            SortKind = tabState.SortKind,
            SortAscending = tabState.SortAscending
        };
    }
    private bool TryRestoreBrowserTabsOnStartup(out int restoredTabCount, out int skippedTabCount, out bool hadSavedTabs)
    {
        restoredTabCount = 0;
        skippedTabCount = 0;
        hadSavedTabs = false;
        EnsureBrowserTabCategoryConfiguration();
        _settings.Session ??= new SessionSettings();
        if (!_settings.Session.RestoreTabsOnStartup)
        {
            LogService.Info("[BrowserTabs] Restore skipped because tab restore is disabled.");
            _restoredBrowserTabsFromWorkspaceStore = false;
            return false;
        }
        bool workspaceStoreLoaded = TryLoadWorkspaceStateStore(out BrowserTabRestoreSnapshot? workspaceSnapshot);
        _restoredBrowserTabsFromWorkspaceStore = workspaceStoreLoaded;
        if (workspaceSnapshot != null)
        {
            ApplyWorkspaceRestoreSnapshotToSettings(workspaceSnapshot);
        }
        EnsureBrowserTabCategoryConfiguration();
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        string restoredCategoryId = ResolveExistingBrowserTabCategoryId(snapshot.ActiveCategoryId);
        List<BrowserTabSessionState> savedTabs = GetBrowserTabSessionStatesForRestore(ref restoredCategoryId);
        hadSavedTabs = savedTabs.Count > 0;
        if (!hadSavedTabs)
        {
            LogService.Info("[BrowserTabs] Restore skipped because no saved tabs were found.");
            return false;
        }
        int maxTabCount = GetMaxBrowserTabsPerCategory();
        if (savedTabs.Count > maxTabCount)
        {
            LogService.Warn($"[BrowserTabs] Restore source exceeded max tabs. Source={savedTabs.Count} Max={maxTabCount}");
            savedTabs = savedTabs.Take(maxTabCount).ToList();
            ShowStatusMessage($"保存タブが上限を超えたため、{maxTabCount} 個まで復元しました。");
        }
        var restoredTabs = new List<BrowserTabState>();
        foreach (BrowserTabSessionState sessionTab in savedTabs)
        {
            if (!TryCreateBrowserTabStateFromSession(sessionTab, out BrowserTabState? restoredTab))
            {
                skippedTabCount++;
                continue;
            }
            restoredTabs.Add(restoredTab!);
        }
        if (restoredTabs.Count == 0)
        {
            LogService.Info($"[BrowserTabs] Restore skipped because all saved tabs were unavailable. Missing={skippedTabCount}");
            return false;
        }
        _browserTabs.Clear();
        _browserTabs.AddRange(restoredTabs);
        restoredTabCount = restoredTabs.Count;
        _activeBrowserTabCategoryId = restoredCategoryId;
        _settings.Session.ActiveBrowserTabCategoryId = restoredCategoryId;
        int targetIndex = ResolveBrowserTabCategoryActiveIndex(restoredCategoryId, restoredTabs.Count);
        _activeBrowserTabIndex = targetIndex;
        RefreshBrowserTabHeaders();
        _activeBrowserTabIndex = -1;
        SwitchBrowserTab(targetIndex);
        LogService.Info($"[BrowserTabs] Restored Category={restoredCategoryId} Tabs={restoredTabCount} Missing={skippedTabCount} ActiveIndex={targetIndex}");
        if (!workspaceStoreLoaded)
        {
            SaveWorkspaceStateStore();
        }
        return true;
    }
    private List<BrowserTabSessionState> GetBrowserTabSessionStatesForRestore(ref string restoredCategoryId)
    {
        string requestedCategoryId = restoredCategoryId;
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        List<BrowserTabRestoreCategoryState> categoryStates = snapshot.Categories
            .Where(static state => state != null && !string.IsNullOrWhiteSpace(state.Id))
            .Select(static state => state.Clone())
            .ToList();
        BrowserTabRestoreCategoryState? activeCategoryState = categoryStates.FirstOrDefault(
            state => string.Equals(state.Id, requestedCategoryId, StringComparison.OrdinalIgnoreCase));
        if (activeCategoryState == null)
        {
            activeCategoryState = categoryStates.FirstOrDefault(state => state.OpenTabs.Count > 0)
                ?? categoryStates.FirstOrDefault();
        }
        if (activeCategoryState != null && activeCategoryState.OpenTabs.Count > 0)
        {
            restoredCategoryId = ResolveExistingBrowserTabCategoryId(activeCategoryState.Id);
            return activeCategoryState.OpenTabs.Select(static tab => tab.Clone()).ToList();
        }
        restoredCategoryId = BrowserTabSettings.DefaultCategoryId;
        return new List<BrowserTabSessionState>();
    }
    private int ResolveBrowserTabCategoryActiveIndex(string categoryId, int restoredTabCount)
    {
        if (restoredTabCount <= 0)
        {
            return 0;
        }
        BrowserTabRestoreCategoryState? categoryState = FindBrowserTabRestoreCategoryState(categoryId);
        if (categoryState != null && categoryState.OpenTabs.Count > 0)
        {
            return Math.Clamp(categoryState.ActiveTabIndex, 0, restoredTabCount - 1);
        }
        return 0;
    }
    private int GetActiveBrowserTabCategoryIndex()
    {
        if (_browserTabCategories.Count == 0)
        {
            return -1;
        }
        int categoryIndex = _browserTabCategories.FindIndex(
            category => string.Equals(category.Id, _activeBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase));
        return categoryIndex >= 0 ? categoryIndex : 0;
    }
    private List<BrowserTabState> LoadBrowserTabsForCategory(string categoryId)
    {
        EnsureBrowserTabCategoryConfiguration();
        _settings.Session ??= new SessionSettings();
        BrowserTabRestoreCategoryState? categoryState = FindBrowserTabRestoreCategoryState(categoryId);
        var restoredTabs = new List<BrowserTabState>();
        int sessionTabCount = categoryState?.OpenTabs?.Count ?? 0;
        foreach (BrowserTabSessionState sessionTab in categoryState?.OpenTabs ?? Enumerable.Empty<BrowserTabSessionState>())
        {
            if (TryCreateBrowserTabStateFromSession(sessionTab, out BrowserTabState? restoredTab))
            {
                restoredTabs.Add(restoredTab!);
            }
        }
        bool usedFallback = false;
        string fallbackReason = "None";
        if (restoredTabs.Count == 0)
        {
            BrowserTabState fallbackState = CreateInitialBrowserTabStateForCategory(categoryId);
            restoredTabs.Add(fallbackState);
            usedFallback = true;
            fallbackReason = sessionTabCount == 0 ? "InitializeCategory" : "RestoreUnavailable";
        }
        LogService.Info(
            $"[BrowserTabCategory] Load Category={categoryId} SessionTabs={sessionTabCount} RestoredTabs={restoredTabs.Count} " +
            $"UsedFallback={usedFallback} FallbackReason={fallbackReason} CurrentUiPath={_navigationService.CurrentPath}");
        return restoredTabs;
    }
    private BrowserTabState CreateInitialBrowserTabStateForCategory(string categoryId)
    {
        string initialPath = _navigationService.CurrentPath;
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
        {
            initialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
        {
            initialPath = AppContext.BaseDirectory;
        }
        string resolvedCategoryId = ResolveExistingBrowserTabCategoryId(categoryId);
        LogService.Info(
            $"[BrowserTabCategory] InitializeCategory Category={resolvedCategoryId} InitialPath={initialPath} " +
            $"CurrentUiPath={_navigationService.CurrentPath}");
        return new BrowserTabState
        {
            Title = GetBrowserTabTitle(initialPath),
            CurrentPath = initialPath,
            IsLocked = false,
            Navigation = new NavigationService.NavigationSnapshot
            {
                CurrentPath = initialPath,
                BackHistory = Array.Empty<string>(),
                ForwardHistory = Array.Empty<string>(),
                LastVisitedPathByDrive = new Dictionary<char, string>()
            },
            FocusTargetName = null,
            CursorIndex = 0,
            ColumnCount = Math.Clamp(_columnCount, 1, 9),
            SortKind = _currentSort,
            SortAscending = _sortAscending
        };
    }
    private void SwitchBrowserTabCategory(string categoryId)
    {
        EnsureBrowserModeBeforeWorkspaceNavigation();
        string targetCategoryId = ResolveExistingBrowserTabCategoryId(categoryId);
        LogService.Info(
            $"[BrowserTabCategory] Switch Requested={categoryId} Resolved={targetCategoryId} ActiveBefore={_activeBrowserTabCategoryId} " +
            $"TabsBefore={_browserTabs.Count} ActiveIndexBefore={_activeBrowserTabIndex}");
        if (string.Equals(targetCategoryId, _activeBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            RefreshBrowserTabHeaders();
            _browserTabStrip?.Invalidate();
            _browserTabHostPanel?.Invalidate();
            browserPanel.Focus();
            LogService.Info($"[BrowserTabCategory] Switch skipped because target category was already active: {targetCategoryId}");
            return;
        }
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        List<BrowserTabState> targetTabs = LoadBrowserTabsForCategory(targetCategoryId);
        int targetIndex = Math.Clamp(ResolveBrowserTabCategoryActiveIndex(targetCategoryId, targetTabs.Count), 0, Math.Max(0, targetTabs.Count - 1));
        LogService.Info($"[BrowserTabCategory] Switch loaded Category={targetCategoryId} Tabs={targetTabs.Count} TargetIndex={targetIndex}");
        _browserTabs.Clear();
        _browserTabs.AddRange(targetTabs);
        _activeBrowserTabCategoryId = targetCategoryId;
        _browserTabContextIndex = -1;
        RefreshBrowserTabHeaders();
        if (_browserTabs.Count > 0)
        {
            _activeBrowserTabIndex = -1;
            SwitchBrowserTab(targetIndex);
        }
        else
        {
            _activeBrowserTabIndex = -1;
        }
        RefreshBrowserTabHeaders();
        _browserTabStrip?.Invalidate();
        _browserTabHostPanel?.Invalidate();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        browserPanel.Focus();
        LogService.Info(
            $"[BrowserTabCategory] Switch applied ActiveAfter={_activeBrowserTabCategoryId} TabsAfter={_browserTabs.Count} " +
            $"ActiveIndexAfter={_activeBrowserTabIndex}");
        ShowStatusMessage($"カテゴリを切り替えました: {_browserTabCategories[GetActiveBrowserTabCategoryIndex()].DisplayName}");
    }
    private void SelectAdjacentBrowserTabCategory(int delta)
    {
        if (GuardClipboardBusy())
        {
            return;
        }
        if (!ShouldShowBrowserTabCategoryRow())
        {
            return;
        }
        EnsureBrowserTabCategoryConfiguration();
        if (_browserTabCategories.Count <= 1)
        {
            return;
        }
        int currentIndex = GetActiveBrowserTabCategoryIndex();
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }
        int nextIndex = (currentIndex + delta + _browserTabCategories.Count) % _browserTabCategories.Count;
        if (nextIndex == currentIndex)
        {
            return;
        }
        LogService.Info(
            $"[BrowserTabCategory] SelectAdjacent Delta={delta} CurrentIndex={currentIndex} NextIndex={nextIndex} " +
            $"CategoryCount={_browserTabCategories.Count} ActiveCategory={_activeBrowserTabCategoryId}");
        SwitchBrowserTabCategory(_browserTabCategories[nextIndex].Id);
    }
    private bool TryResolveBrowserTabRestorePath(BrowserTabSessionState sessionTab, out string restorePath)
    {
        restorePath = string.Empty;
        string currentPath = sessionTab.CurrentPath ?? string.Empty;
        string startupPath = sessionTab.StartupPath ?? string.Empty;
        if (sessionTab.IsLocked && !string.IsNullOrWhiteSpace(startupPath) && Directory.Exists(startupPath))
        {
            // ロックタブでも、現在パスがロックルート配下なら現在パスを優先して復元する
            if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath) &&
                IsPathUnderBrowserTabStartupPath(currentPath, new BrowserTabState { StartupPath = startupPath }))
            {
                restorePath = currentPath;
            }
            else
            {
                restorePath = startupPath;
            }
            return true;
        }
        if (Directory.Exists(currentPath))
        {
            restorePath = currentPath;
            if (sessionTab.IsLocked && !string.IsNullOrWhiteSpace(startupPath))
            {
                LogService.Warn($"[BrowserTabs] Locked startup path missing. StartupPath={startupPath} Fallback={restorePath}");
                ShowStatusMessage("固定タブの起動元が見つからないため、最後の場所を開きました。");
            }
            return true;
        }
        if (sessionTab.IsLocked && TryFindExistingParentDirectory(startupPath, out string parentPath))
        {
            restorePath = parentPath;
            LogService.Warn($"[BrowserTabs] Locked startup/current path missing. StartupPath={startupPath} CurrentPath={currentPath} Fallback={restorePath}");
            ShowStatusMessage("固定タブの起動元が見つからないため、親フォルダを開きました。");
            return true;
        }
        if (sessionTab.IsLocked)
        {
            restorePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(restorePath) || !Directory.Exists(restorePath))
            {
                restorePath = AppContext.BaseDirectory;
            }
            LogService.Warn($"[BrowserTabs] Locked tab restore fallback used. StartupPath={startupPath} CurrentPath={currentPath} Fallback={restorePath}");
            ShowStatusMessage("固定タブの起動元が見つからないため、代替フォルダを開きました。");
            return Directory.Exists(restorePath);
        }
        return false;
    }
    private static bool TryFindExistingParentDirectory(string? path, out string parentPath)
    {
        parentPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string? candidate = path;
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                parentPath = candidate;
                return true;
            }
        }
        return false;
    }
    private bool TryCreateBrowserTabStateFromSession(BrowserTabSessionState sessionTab, out BrowserTabState? restoredTab)
    {
        restoredTab = null;
        if (sessionTab == null || !TryResolveBrowserTabRestorePath(sessionTab, out string restorePath))
        {
            return false;
        }
        var backHistory = (sessionTab.BackHistory ?? new List<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .ToList();
        var forwardHistory = (sessionTab.ForwardHistory ?? new List<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .ToList();
        var lastVisitedByDrive = new Dictionary<char, string>();
        foreach ((string driveKey, string path) in sessionTab.LastVisitedPathByDrive ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(driveKey) || driveKey.Length != 1 || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }
            lastVisitedByDrive[driveKey[0]] = path;
        }
        restoredTab = new BrowserTabState
        {
            Id = sessionTab.TabId == Guid.Empty ? Guid.NewGuid() : sessionTab.TabId,
            Title = GetBrowserTabTitle(restorePath),
            CurrentPath = restorePath,
            IsLocked = sessionTab.IsLocked,
            StartupPath = sessionTab.StartupPath ?? string.Empty,
            IsReadOnly = sessionTab.IsReadOnly,
            FilterLock = sessionTab.FilterLock?.Clone() ?? new TabFilterLockState(),
            MarkedPaths = CreatePersistableMarkedPaths(sessionTab.MarkedPaths, out int skippedMarkCount),
            Navigation = new NavigationService.NavigationSnapshot
            {
                CurrentPath = restorePath,
                BackHistory = backHistory,
                ForwardHistory = forwardHistory,
                LastVisitedPathByDrive = lastVisitedByDrive
            },
            FocusTargetName = sessionTab.FocusTargetName,
            CursorIndex = Math.Max(0, sessionTab.CursorIndex),
            ColumnCount = Math.Clamp(sessionTab.ColumnCount, 1, 9),
            SortKind = sessionTab.SortKind,
            SortAscending = sessionTab.SortAscending
        };
        if (skippedMarkCount > 0)
        {
            LogService.Info($"[BrowserTabs] Pruned stale restored marks. TabId={restoredTab.Id} Missing={skippedMarkCount}");
        }
        return true;
    }
    private void BrowserTabStrip_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressBrowserTabSelectionChanged || _browserTabStrip == null)
        {
            return;
        }
        int newIndex = _browserTabStrip.SelectedIndex;
        if (newIndex >= 0 && newIndex < _browserTabs.Count)
        {
            SwitchBrowserTab(newIndex);
        }
    }
    private void BrowserTabStrip_CategoryClicked(object? sender, BrowserTabStripCategoryEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ShowBrowserTabCategoryContextMenu(e);
            return;
        }
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        if (e.Kind == BrowserTabStripCategoryItemKind.ManageEntry)
        {
            AddGeneratedBrowserTabCategory();
            return;
        }
        SwitchBrowserTabCategory(e.CategoryId);
    }
    private void BrowserTabStrip_AddTabClicked(object? sender, EventArgs e)
    {
        AddBrowserTabFromEntry();
    }
    private IReadOnlyList<BrowserTabCategoryDefinition> GetBrowserTabCategoryDefinitionsForDialog()
    {
        return _browserTabCategories
            .Select(static category => category.Clone())
            .ToList();
    }
    private void OpenBrowserTabCategoryManager()
    {
        EnsureBrowserTabCategoryConfiguration();
        using var dialog = new CategoryManageDialog(
            GetBrowserTabCategoryDefinitionsForDialog,
            PromptAndAddBrowserTabCategory,
            RenameBrowserTabCategory,
            DeleteBrowserTabCategory,
            DeleteBrowserTabCategories);
        dialog.ShowDialog(this);
        RefreshBrowserTabHeaders();
        browserPanel.Focus();
    }
    private string GenerateNextBrowserTabCategoryDisplayName()
    {
        for (int i = 1; ; i++)
        {
            string candidate = $"カテゴリ{i}";
            if (!_browserTabCategories.Any(category => string.Equals(category.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }
    private string? AddGeneratedBrowserTabCategory()
    {
        return AddBrowserTabCategoryCore(GenerateNextBrowserTabCategoryDisplayName());
    }
    private string? PromptAndAddBrowserTabCategory()
    {
        string? displayName = SimpleInputDialog.ShowNullable("新しいカテゴリ名を入力してください。", "カテゴリ追加", "");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }
        return AddBrowserTabCategoryCore(displayName);
    }
    private string? AddBrowserTabCategoryCore(string displayName)
    {
        string trimmedName = displayName.Trim();
        if (_browserTabCategories.Any(category => string.Equals(category.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("同じ表示名のカテゴリがすでにあります。", "カテゴリ追加", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        string newCategoryId = CreateUniqueBrowserTabCategoryId(trimmedName);
        _browserTabCategories.Add(new BrowserTabCategoryDefinition
        {
            Id = newCategoryId,
            DisplayName = trimmedName
        });
        SyncBrowserTabCategoryDefinitionsToSettings();
        EnsureBrowserTabRestoreSnapshot();
        SwitchBrowserTabCategory(newCategoryId);
        SettingsManager.Save(_settings);
        ShowStatusMessage($"カテゴリを追加しました: {trimmedName}");
        return trimmedName;
    }
    private BrowserTabCategoryDefinition? FindBrowserTabCategoryDefinition(string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return null;
        }
        return _browserTabCategories.FirstOrDefault(
            category => string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase));
    }
    private void EnsureBrowserTabCategoryContextMenu()
    {
        if (_browserTabCategoryContextMenu != null)
        {
            return;
        }
        _browserTabCategoryContextMenu = new ContextMenuStrip();
        _addBrowserTabCategoryContextMenuItem = new ToolStripMenuItem("カテゴリ追加");
        _addBrowserTabCategoryContextMenuItem.ShortcutKeyDisplayString = "Ctrl+Shift+N";
        _addBrowserTabCategoryContextMenuItem.Click += (_, _) => AddGeneratedBrowserTabCategory();
        _moveBrowserTabCategoryLeftContextMenuItem = new ToolStripMenuItem("左へ移動");
        _moveBrowserTabCategoryLeftContextMenuItem.ShortcutKeyDisplayString = "Ctrl+Alt+Left";
        _moveBrowserTabCategoryLeftContextMenuItem.Click += (_, _) => MoveBrowserTabCategoryFromContext(-1);
        _moveBrowserTabCategoryRightContextMenuItem = new ToolStripMenuItem("右へ移動");
        _moveBrowserTabCategoryRightContextMenuItem.ShortcutKeyDisplayString = "Ctrl+Alt+Right";
        _moveBrowserTabCategoryRightContextMenuItem.Click += (_, _) => MoveBrowserTabCategoryFromContext(+1);
        _renameBrowserTabCategoryContextMenuItem = new ToolStripMenuItem("名前変更");
        _renameBrowserTabCategoryContextMenuItem.Click += (_, _) => RenameBrowserTabCategoryFromContext();
        _deleteBrowserTabCategoryContextMenuItem = new ToolStripMenuItem("削除");
        _deleteBrowserTabCategoryContextMenuItem.Click += (_, _) => DeleteBrowserTabCategoryFromContext();
        _manageBrowserTabCategoriesContextMenuItem = new ToolStripMenuItem("カテゴリ管理...");
        _manageBrowserTabCategoriesContextMenuItem.Click += (_, _) => OpenBrowserTabCategoryManager();
        _browserTabCategoryContextMenu.Items.AddRange(
        [
            _addBrowserTabCategoryContextMenuItem,
            new ToolStripSeparator(),
            _moveBrowserTabCategoryLeftContextMenuItem,
            _moveBrowserTabCategoryRightContextMenuItem,
            _renameBrowserTabCategoryContextMenuItem,
            _deleteBrowserTabCategoryContextMenuItem,
            new ToolStripSeparator(),
            _manageBrowserTabCategoriesContextMenuItem
        ]);
    }
    private void ShowBrowserTabCategoryContextMenu(BrowserTabStripCategoryEventArgs e)
    {
        if (_browserTabStrip == null)
        {
            return;
        }
        EnsureBrowserTabCategoryConfiguration();
        EnsureBrowserTabCategoryContextMenu();
        _browserTabCategoryContextCategoryId = e.Kind == BrowserTabStripCategoryItemKind.ManageEntry ? null : e.CategoryId;
        _browserTabCategoryContextKind = e.Kind;
        BrowserTabCategoryDefinition? targetCategory = FindBrowserTabCategoryDefinition(_browserTabCategoryContextCategoryId);
        int targetIndex = targetCategory == null
            ? -1
            : _browserTabCategories.FindIndex(category => string.Equals(category.Id, targetCategory.Id, StringComparison.OrdinalIgnoreCase));
        bool canMoveLeft = targetIndex > 0;
        bool canMoveRight = targetIndex >= 0 && targetIndex < _browserTabCategories.Count - 1;
        bool hasTargetCategory = targetCategory != null;
        if (_moveBrowserTabCategoryLeftContextMenuItem != null)
        {
            _moveBrowserTabCategoryLeftContextMenuItem.Visible = hasTargetCategory;
            _moveBrowserTabCategoryLeftContextMenuItem.Enabled = canMoveLeft;
        }
        if (_moveBrowserTabCategoryRightContextMenuItem != null)
        {
            _moveBrowserTabCategoryRightContextMenuItem.Visible = hasTargetCategory;
            _moveBrowserTabCategoryRightContextMenuItem.Enabled = canMoveRight;
        }
        if (_renameBrowserTabCategoryContextMenuItem != null)
        {
            _renameBrowserTabCategoryContextMenuItem.Visible = hasTargetCategory;
            _renameBrowserTabCategoryContextMenuItem.Enabled = hasTargetCategory;
        }
        if (_deleteBrowserTabCategoryContextMenuItem != null)
        {
            _deleteBrowserTabCategoryContextMenuItem.Visible = hasTargetCategory;
            _deleteBrowserTabCategoryContextMenuItem.Enabled = hasTargetCategory;
        }
        if (_browserTabCategoryContextMenu != null && _browserTabCategoryContextMenu.Items.Count >= 7)
        {
            _browserTabCategoryContextMenu.Items[1].Visible = hasTargetCategory;
            _browserTabCategoryContextMenu.Items[6].Visible = hasTargetCategory;
        }
        _browserTabCategoryContextMenu?.Show(_browserTabStrip, e.Location);
    }
    private void MoveBrowserTabCategoryFromContext(int delta)
    {
        if (!string.IsNullOrWhiteSpace(_browserTabCategoryContextCategoryId))
        {
            MoveBrowserTabCategory(_browserTabCategoryContextCategoryId, delta);
        }
    }
    private void RenameBrowserTabCategoryFromContext()
    {
        BrowserTabCategoryDefinition? target = FindBrowserTabCategoryDefinition(_browserTabCategoryContextCategoryId);
        if (target != null)
        {
            RenameBrowserTabCategory(target);
        }
    }
    private void DeleteBrowserTabCategoryFromContext()
    {
        BrowserTabCategoryDefinition? target = FindBrowserTabCategoryDefinition(_browserTabCategoryContextCategoryId);
        if (target != null)
        {
            DeleteBrowserTabCategory(target);
        }
    }
    private string? MoveBrowserTabCategory(string categoryId, int delta)
    {
        if (delta == 0)
        {
            return null;
        }
        int currentIndex = _browserTabCategories.FindIndex(category => string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            return null;
        }
        int targetIndex = currentIndex + delta;
        if (targetIndex < 0 || targetIndex >= _browserTabCategories.Count)
        {
            return null;
        }
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        BrowserTabCategoryDefinition movedCategory = _browserTabCategories[currentIndex];
        _browserTabCategories.RemoveAt(currentIndex);
        _browserTabCategories.Insert(targetIndex, movedCategory);
        SyncBrowserTabCategoryDefinitionsToSettings();
        EnsureBrowserTabRestoreSnapshot();
        RefreshBrowserTabHeaders();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        SettingsManager.Save(_settings);
        string direction = delta < 0 ? "左" : "右";
        ShowStatusMessage($"カテゴリを{direction}へ移動しました: {movedCategory.DisplayName}");
        browserPanel.Focus();
        return movedCategory.DisplayName;
    }
    private string? RenameBrowserTabCategory(BrowserTabCategoryDefinition category)
    {
        BrowserTabCategoryDefinition? target = _browserTabCategories.FirstOrDefault(
            existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return null;
        }
        string? renamed = SimpleInputDialog.ShowNullable("カテゴリ名を入力してください。", "カテゴリ名変更", target.DisplayName);
        if (string.IsNullOrWhiteSpace(renamed))
        {
            return null;
        }
        string trimmedName = renamed.Trim();
        if (_browserTabCategories.Any(existing =>
                !string.Equals(existing.Id, target.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("同じ表示名のカテゴリがすでにあります。", "カテゴリ名変更", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        target.DisplayName = trimmedName;
        SyncBrowserTabCategoryDefinitionsToSettings();
        EnsureBrowserTabRestoreSnapshot();
        SettingsManager.Save(_settings);
        RefreshBrowserTabHeaders();
        ShowStatusMessage($"カテゴリ名を更新しました: {trimmedName}");
        return trimmedName;
    }
    private string? DeleteBrowserTabCategory(BrowserTabCategoryDefinition category)
    {
        BrowserTabCategoryDefinition? target = _browserTabCategories.FirstOrDefault(
            existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return null;
        }
        DialogResult confirm = MessageBox.Show(
            $"カテゴリ '{target.DisplayName}' を削除します。よろしいですか？",
            "カテゴリ削除",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return null;
        }
        return DeleteBrowserTabCategoriesCore([target], $"カテゴリを削除しました: {target.DisplayName}");
    }
    private string? DeleteBrowserTabCategories(IReadOnlyList<BrowserTabCategoryDefinition> categories)
    {
        List<BrowserTabCategoryDefinition> targets = categories
            .Where(category => category != null)
            .Select(category =>
                _browserTabCategories.FirstOrDefault(existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase)))
            .Where(static category => category != null)
            .Select(static category => category!)
            .GroupBy(category => category.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (targets.Count == 0)
        {
            return null;
        }
        if (targets.Count == 1)
        {
            return DeleteBrowserTabCategory(targets[0]);
        }
        string summary = string.Join("、", targets.Select(target => target.DisplayName));
        DialogResult confirm = MessageBox.Show(
            $"マークした {targets.Count} 件のカテゴリを削除します。よろしいですか？{Environment.NewLine}{summary}",
            "カテゴリ一括削除",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return null;
        }
        return DeleteBrowserTabCategoriesCore(targets, $"カテゴリを削除しました: {targets.Count} 件");
    }
    private string? DeleteBrowserTabCategoriesCore(IReadOnlyList<BrowserTabCategoryDefinition> targets, string successMessage)
    {
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        HashSet<string> targetIds = targets
            .Select(target => target.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _browserTabCategories.RemoveAll(existing => targetIds.Contains(existing.Id));
        BrowserTabCategoryDefinition? recoveredCategory = null;
        if (_browserTabCategories.Count == 0)
        {
            recoveredCategory = EnsureAtLeastOneBrowserTabCategoryAfterDeletion();
        }
        SyncBrowserTabCategoryDefinitionsToSettings();
        EnsureBrowserTabRestoreSnapshot();
        if (targetIds.Contains(_activeBrowserTabCategoryId) || recoveredCategory != null)
        {
            string fallbackCategoryId = recoveredCategory?.Id
                ?? ResolveExistingBrowserTabCategoryId(_activeBrowserTabCategoryId);
            List<BrowserTabState> targetTabs = LoadBrowserTabsForCategory(fallbackCategoryId);
            int targetIndex = Math.Clamp(ResolveBrowserTabCategoryActiveIndex(fallbackCategoryId, targetTabs.Count), 0, Math.Max(0, targetTabs.Count - 1));
            _browserTabs.Clear();
            _browserTabs.AddRange(targetTabs);
            _activeBrowserTabCategoryId = fallbackCategoryId;
            _browserTabContextIndex = -1;
            RefreshBrowserTabHeaders();
            if (_browserTabs.Count > 0)
            {
                _activeBrowserTabIndex = -1;
                SwitchBrowserTab(targetIndex);
            }
            else
            {
                _activeBrowserTabIndex = -1;
            }
            StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        }
        else
        {
            RefreshBrowserTabHeaders();
        }
        SettingsManager.Save(_settings);
        ShowStatusMessage(successMessage);
        return successMessage;
    }
    private void LayoutBrowserTabControlWithinHost()
    {
        if (_browserTabStrip == null || _browserTabHostPanel == null)
        {
            return;
        }
        int hostWidth = Math.Max(0, _browserTabHostPanel.ClientSize.Width);
        _browserTabStrip.Bounds = new Rectangle(
            0,
            0,
            Math.Max(1, hostWidth),
            Math.Max(1, _browserTabHostPanel.ClientSize.Height));
    }
    private BrowserTabState BuildBrowserTabStateFromCurrentUi()
    {
        string currentPath = _navigationService.CurrentPath;
        BrowserTabState? activeState = _activeBrowserTabIndex >= 0 && _activeBrowserTabIndex < _browserTabs.Count
            ? _browserTabs[_activeBrowserTabIndex]
            : null;
        bool isLocked = activeState?.IsLocked ?? false;
        return new BrowserTabState
        {
            Title = GetBrowserTabTitle(currentPath),
            CurrentPath = currentPath,
            IsLocked = isLocked,
            StartupPath = activeState?.StartupPath ?? string.Empty,
            IsReadOnly = activeState?.IsReadOnly ?? false,
            FilterLock = activeState?.FilterLock?.Clone() ?? new TabFilterLockState(),
            MarkedPaths = CreatePersistableMarkedPaths(_markedFiles.Snapshot(), out _),
            Navigation = _navigationService.CaptureState(),
            FocusTargetName = GetCurrentBrowserItem() is ListViewItem item ? GetItemFullName(item) : null,
            CursorIndex = _browserCursorIndex,
            ColumnCount = _columnCount,
            SortKind = _currentSort,
            SortAscending = _sortAscending
        };
    }
    private void CaptureActiveBrowserTabState(bool captureMarks = true)
    {
        if (_activeBrowserTabIndex < 0 || _activeBrowserTabIndex >= _browserTabs.Count)
        {
            return;
        }
        BrowserTabState currentState = _browserTabs[_activeBrowserTabIndex];
        BrowserTabState latestState = BuildBrowserTabStateFromCurrentUi();
        currentState.Title = latestState.Title;
        currentState.CurrentPath = latestState.CurrentPath;
        currentState.Navigation = latestState.Navigation;
        currentState.FocusTargetName = latestState.FocusTargetName;
        currentState.CursorIndex = latestState.CursorIndex;
        currentState.ColumnCount = latestState.ColumnCount;
        currentState.SortKind = latestState.SortKind;
        currentState.SortAscending = latestState.SortAscending;
        currentState.IsLocked = latestState.IsLocked;
        // StartupPath の不変性を保護 (ロック中の場合は既存の StartupPath を優先)
        if (!currentState.IsLocked || string.IsNullOrWhiteSpace(currentState.StartupPath))
        {
            currentState.StartupPath = latestState.StartupPath;
        }
        currentState.IsReadOnly = latestState.IsReadOnly;
        currentState.FilterLock = latestState.FilterLock.Clone();
        if (captureMarks)
        {
            currentState.MarkedPaths = latestState.MarkedPaths;
        }
        RefreshBrowserTabHeaders();
    }
    private void RestoreMarksForBrowserTab(BrowserTabState state)
    {
        List<string> restoredMarks = CreatePersistableMarkedPaths(state.MarkedPaths, out int skippedCount);
        if (skippedCount > 0)
        {
            LogService.Info($"[BrowserTabs] Pruned stale per-tab marks. TabId={state.Id} Missing={skippedCount}");
        }
        state.MarkedPaths = restoredMarks;
        RestoreMarks(restoredMarks, invalidateRedo: false);
        RefreshMarkUi();
    }
    private void RefreshBrowserTabHeaders()
    {
        if (_browserTabStrip == null)
        {
            return;
        }
        _suppressBrowserTabSelectionChanged = true;
        try
        {
            ApplyBrowserTabStripDisplaySettings();
            bool showCategoryRow = ShouldShowBrowserTabCategoryRow();
            int activeCategoryIndex = GetActiveBrowserTabCategoryIndex();
            var stripCategories = _browserTabCategories
                .Select(category => new BrowserTabStripCategoryItem(
                    category.Id,
                    string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName,
                    BuildBrowserTabCategoryToolTip(category)))
                .ToList();
            if (showCategoryRow)
            {
                stripCategories.Add(new BrowserTabStripCategoryItem(
                    BrowserTabStrip.ManageCategoriesEntryId,
                    "+ カテゴリ",
                    "新しいカテゴリを追加します。",
                    BrowserTabStripCategoryItemKind.ManageEntry));
            }
            var stripTabs = _browserTabs
                .Select((state, i) => new BrowserTabStripItem(
                    BuildBrowserTabHeaderText(state, i),
                    BuildBrowserTabToolTip(state)))
                .ToList();
            string snapshotKey = BuildBrowserTabHeaderSnapshotKey(
                showCategoryRow,
                activeCategoryIndex,
                _activeBrowserTabIndex,
                stripCategories,
                stripTabs);
            if (string.Equals(_lastBrowserTabHeaderSnapshotKey, snapshotKey, StringComparison.Ordinal))
            {
                return;
            }
            _lastBrowserTabHeaderSnapshotKey = snapshotKey;
            LogService.Info(
                $"[BrowserTabCategory] RefreshHeaders ActiveCategory={_activeBrowserTabCategoryId} BrowserTabs={_browserTabs.Count} " +
                $"ActiveIndex={_activeBrowserTabIndex} StripCategories={stripCategories.Count} StripTabs={stripTabs.Count} " +
                $"ShowCategoryRow={showCategoryRow}");
            _browserTabStrip.SetCategories(stripCategories, activeCategoryIndex);
            _browserTabStrip.SetTabs(stripTabs);
            if (_activeBrowserTabIndex >= 0 && _activeBrowserTabIndex < _browserTabs.Count)
            {
                _browserTabStrip.SelectedIndex = _activeBrowserTabIndex;
            }
            LayoutBrowserTabControlWithinHost();
        }
        finally
        {
            _suppressBrowserTabSelectionChanged = false;
        }
    }
    private static string BuildBrowserTabHeaderSnapshotKey(
        bool showCategoryRow,
        int activeCategoryIndex,
        int activeTabIndex,
        IReadOnlyList<BrowserTabStripCategoryItem> categories,
        IReadOnlyList<BrowserTabStripItem> tabs)
    {
        var sb = new StringBuilder();
        AppendSnapshotField(sb, showCategoryRow ? "1" : "0");
        AppendSnapshotField(sb, activeCategoryIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendSnapshotField(sb, activeTabIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendSnapshotField(sb, categories.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (BrowserTabStripCategoryItem category in categories)
        {
            AppendSnapshotField(sb, category.CategoryId);
            AppendSnapshotField(sb, category.Text);
            AppendSnapshotField(sb, category.ToolTipText ?? string.Empty);
            AppendSnapshotField(sb, category.Kind.ToString());
        }
        AppendSnapshotField(sb, tabs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (BrowserTabStripItem tab in tabs)
        {
            AppendSnapshotField(sb, tab.Text);
            AppendSnapshotField(sb, tab.ToolTipText ?? string.Empty);
        }
        return sb.ToString();
    }
    private static void AppendSnapshotField(StringBuilder sb, string value)
    {
        sb.Append(value.Length);
        sb.Append(':');
        sb.Append(value);
        sb.Append('|');
    }
    private string BuildBrowserTabCategoryToolTip(BrowserTabCategoryDefinition category)
    {
        string name = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName.Trim();
        return string.Equals(category.Id, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase)
            ? $"カテゴリ: {name}"
            : $"カテゴリ: {name}{Environment.NewLine}ID: {category.Id}";
    }
    private string GetBrowserTabTitle(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "新しいタブ";
        }
        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            normalizedPath = path;
        }
        string? aliasDisplayName = QuickAccessService.FindAliasDisplayName(_quickAccessStore, normalizedPath);
        if (!string.IsNullOrWhiteSpace(aliasDisplayName))
        {
            return aliasDisplayName;
        }
        string? root = null;
        try
        {
            root = Path.GetPathRoot(normalizedPath);
        }
        catch
        {
            root = null;
        }
        if (!string.IsNullOrWhiteSpace(root))
        {
            string normalizedRoot = EnsureTrailingDirectorySeparator(root);
            string trimmedPath = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string trimmedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedRoot;
            }
            string relative = trimmedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? trimmedPath.Substring(normalizedRoot.Length)
                : trimmedPath;
            string[] segments = relative
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return normalizedRoot;
            }
            if (segments.Length == 1)
            {
                return $"{normalizedRoot}{segments[0]}{Path.DirectorySeparatorChar}";
            }
            return $"{normalizedRoot}…{Path.DirectorySeparatorChar}{segments[^1]}{Path.DirectorySeparatorChar}";
        }
        string fallback = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(fallback);
        return !string.IsNullOrWhiteSpace(name) ? name : path;
    }
    private static string EnsureTrailingDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }
        char lastChar = path[^1];
        if (lastChar == Path.DirectorySeparatorChar || lastChar == Path.AltDirectorySeparatorChar)
        {
            return path;
        }
        return path + Path.DirectorySeparatorChar;
    }
    private bool CreateNewBrowserTab(string? initialPath = null, bool showStatusMessage = true)
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        int maxTabCount = GetMaxBrowserTabsPerCategory();
        if (_browserTabs.Count >= maxTabCount)
        {
            ShowStatusMessage($"タブは最大{maxTabCount}個までです。");
            _browserTabStrip?.FlashLimitReached();
            TryPlayBrowserTabLimitBeep();
            return false;
        }
        CaptureActiveBrowserTabState();
        BrowserTabState newState = BuildBrowserTabStateFromCurrentUi();
        newState.IsLocked = false;
        newState.StartupPath = string.Empty;
        newState.IsReadOnly = false;
        newState.MarkedPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            newState.CurrentPath = initialPath;
            newState.Navigation = new NavigationService.NavigationSnapshot
            {
                CurrentPath = initialPath,
                BackHistory = Array.Empty<string>(),
                ForwardHistory = Array.Empty<string>(),
                LastVisitedPathByDrive = new Dictionary<char, string>()
            };
            newState.FocusTargetName = null;
            newState.CursorIndex = 0;
            newState.Title = GetBrowserTabTitle(initialPath);
        }
        _browserTabs.Add(newState);
        int newIndex = _browserTabs.Count - 1;
        RefreshBrowserTabHeaders();
        _activeBrowserTabIndex = -1;
        SwitchBrowserTab(newIndex);
        if (showStatusMessage)
        {
            ShowStatusMessage("新しいタブを作成しました。");
        }
        return true;
    }
    private string BuildBrowserTabHeaderText(BrowserTabState state, int index)
    {
        string title = string.IsNullOrWhiteSpace(state.Title) ? $"Tab {index + 1}" : state.Title;
        string lockedPrefix = state.IsLocked ? "■ " : string.Empty;
        string readOnlyPrefix = state.IsReadOnly ? "[RO] " : string.Empty;
        return $"{lockedPrefix}{readOnlyPrefix}{title}";
    }
    private string BuildBrowserTabToolTip(BrowserTabState state)
    {
        var lines = new List<string>();
        lines.Add(state.IsLocked ? "状態: 固定タブ" : "状態: 通常タブ");
        lines.Add(state.IsReadOnly ? "ReadOnly: 有効" : "ReadOnly: 無効");
        string title = string.IsNullOrWhiteSpace(state.Title) ? "新しいタブ" : state.Title;
        lines.Add($"見出し: {title}");
        if (!string.IsNullOrWhiteSpace(state.CurrentPath))
        {
            lines.Add($"場所: {state.CurrentPath}");
        }
        if (state.IsLocked && !string.IsNullOrWhiteSpace(state.StartupPath))
        {
            lines.Add($"起動元: {state.StartupPath}");
        }
        return string.Join(Environment.NewLine, lines.Where(static line => !string.IsNullOrWhiteSpace(line)));
    }
    private void RefreshAllBrowserTabTitles()
    {
        foreach (BrowserTabState state in _browserTabs)
        {
            state.Title = GetBrowserTabTitle(state.CurrentPath);
        }
        RefreshBrowserTabHeaders();
    }
    private bool IsActiveBrowserTabLocked()
    {
        return _activeBrowserTabIndex >= 0
            && _activeBrowserTabIndex < _browserTabs.Count
            && _browserTabs[_activeBrowserTabIndex].IsLocked;
    }
    private int GetMaxBrowserTabsPerCategory()
    {
        int configuredMax = _settings.BrowserTabs?.MaxTabsPerCategory ?? BrowserTabSettings.DefaultMaxTabsPerCategory;
        return Math.Clamp(configuredMax, 1, BrowserTabSettings.SafetyMaxTabsPerCategory);
    }
    private bool IsActiveBrowserTabReadOnly()
    {
        return _activeBrowserTabIndex >= 0
            && _activeBrowserTabIndex < _browserTabs.Count
            && _browserTabs[_activeBrowserTabIndex].IsReadOnly;
    }
    private bool GuardReadOnlyBrowserTab(string? operationName = null)
    {
        if (!IsActiveBrowserTabReadOnly())
        {
            return false;
        }
        string message = string.IsNullOrWhiteSpace(operationName)
            ? ReadOnlyBrowserTabBlockedMessage
            : $"このタブは ReadOnly のため、{operationName}は実行できません。";
        ShowStatusMessage(message, 2000);
        return true;
    }
    private void ToggleActiveBrowserTabLock()
    {
        ToggleBrowserTabLock(_activeBrowserTabIndex);
    }
    private void ToggleActiveBrowserTabReadOnly()
    {
        ToggleBrowserTabReadOnly(_activeBrowserTabIndex);
    }
    private TabFilterLockState GetActiveTabFilterLock()
    {
        if (_activeBrowserTabIndex < 0 || _activeBrowserTabIndex >= _browserTabs.Count)
        {
            return TabFilterLockState.Disabled();
        }
        return _browserTabs[_activeBrowserTabIndex].FilterLock;
    }
    private bool HasActiveTabFilterLock()
    {
        var lockState = GetActiveTabFilterLock();
        return lockState.Enabled && lockState.HasAnyCondition;
    }
    private void OpenActiveTabFilterLockDialog()
    {
        OpenTabFilterLockDialog(_activeBrowserTabIndex);
    }
    private void OpenTabFilterLockDialog(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabs.Count) return;
        var tab = _browserTabs[tabIndex];
        using var dialog = new TabFilterLockDialog(tab.FilterLock);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            tab.FilterLock = dialog.ResultState;
            if (tabIndex == _activeBrowserTabIndex)
            {
                ExecuteCurrentDirectoryReloadCommand();
            }
            else
            {
                _browserTabStrip?.Invalidate();
            }
        }
    }
    private void ClearActiveTabFilterLock()
    {
        ClearTabFilterLock(_activeBrowserTabIndex);
    }
    private void ClearTabFilterLock(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabs.Count) return;
        var tab = _browserTabs[tabIndex];
        tab.FilterLock = TabFilterLockState.Disabled();
        if (tabIndex == _activeBrowserTabIndex)
        {
            ExecuteCurrentDirectoryReloadCommand();
        }
        else
        {
            _browserTabStrip?.Invalidate();
        }
    }
    private void ToggleBrowserTabLock(int tabIndex, bool showStatusMessage = true)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabs.Count)
        {
            return;
        }
        if (_activeBrowserTabIndex != tabIndex)
        {
            SwitchBrowserTab(tabIndex);
        }
        BrowserTabState state = _browserTabs[tabIndex];
        state.IsLocked = !state.IsLocked;
        if (state.IsLocked)
        {
            if (string.IsNullOrWhiteSpace(state.StartupPath))
            {
                string rawPath = Directory.Exists(_navigationService.CurrentPath)
                    ? _navigationService.CurrentPath
                    : state.CurrentPath;
                state.StartupPath = _navigationService.NormalizeDestinationDirectory(rawPath);
            }
        }
        else
        {
            state.StartupPath = string.Empty;
        }
        RefreshBrowserTabHeaders();
        if (showStatusMessage)
        {
            ShowStatusMessage(state.IsLocked
                ? "現在のタブを固定しました。"
                : "現在のタブ固定を解除しました。");
        }
    }
    private void ToggleBrowserTabReadOnly(int tabIndex, bool showStatusMessage = true)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabs.Count)
        {
            return;
        }
        if (_activeBrowserTabIndex != tabIndex)
        {
            SwitchBrowserTab(tabIndex);
        }
        BrowserTabState state = _browserTabs[tabIndex];
        state.IsReadOnly = !state.IsReadOnly;
        RefreshBrowserTabHeaders();
        if (showStatusMessage)
        {
            ShowStatusMessage(state.IsReadOnly
                ? "現在のタブを ReadOnly にしました。"
                : "現在のタブの ReadOnly を解除しました。");
        }
    }
    private bool PrepareUnlockedTabForLocationChange(string? targetPath = null)
    {
        if (!IsActiveBrowserTabLocked())
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(targetPath) && QuickAccessService.PathsEqual(targetPath, _navigationService.CurrentPath))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(targetPath)
            && _activeBrowserTabIndex >= 0
            && _activeBrowserTabIndex < _browserTabs.Count
            && IsPathUnderBrowserTabStartupPath(targetPath, _browserTabs[_activeBrowserTabIndex]))
        {
            return true;
        }
        if (!CreateNewBrowserTab(showStatusMessage: false))
        {
            return false;
        }
        ShowStatusMessage("固定タブから派生タブを作成しました。");
        return true;
    }
    private static bool IsPathUnderBrowserTabStartupPath(string targetPath, BrowserTabState state)
    {
        string startupPath = state.StartupPath;
        if (string.IsNullOrWhiteSpace(startupPath) || !Directory.Exists(startupPath))
        {
            startupPath = state.CurrentPath;
        }
        if (string.IsNullOrWhiteSpace(startupPath))
        {
            return false;
        }
        try
        {
            string normalizedStartup = EnsureTrailingDirectorySeparator(Path.GetFullPath(startupPath));
            string normalizedTarget = Path.GetFullPath(targetPath);
            return normalizedTarget.StartsWith(normalizedStartup, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    normalizedTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    normalizedStartup.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
    private void TryPlayBrowserTabLimitBeep()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastBrowserTabLimitBeepUtc).TotalMilliseconds < 1200)
        {
            return;
        }
        _lastBrowserTabLimitBeepUtc = nowUtc;
        try
        {
            SystemSounds.Beep.Play();
        }
        catch
        {
            // 既定音が使えない環境では無音で続行する
        }
    }
    private bool TryCloseBrowserTab(int tabIndex, bool showStatusMessage = true)
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        if (tabIndex < 0 || tabIndex >= _browserTabs.Count)
        {
            return false;
        }
        if (_browserTabs[tabIndex].IsLocked)
        {
            if (showStatusMessage)
            {
                ShowStatusMessage("固定タブは閉じられません。先に固定を解除してください。");
            }
            return false;
        }
        if (_browserTabs.Count <= 1)
        {
            if (showStatusMessage)
            {
                ShowStatusMessage("最後のタブは閉じられません。");
            }
            return false;
        }
        if (_activeBrowserTabIndex != tabIndex)
        {
            SwitchBrowserTab(tabIndex);
        }
        int closingIndex = tabIndex;
        PushClosedBrowserTabSnapshot(closingIndex);
        _browserTabs.RemoveAt(closingIndex);
        int targetIndex = Math.Clamp(closingIndex - 1, 0, _browserTabs.Count - 1);
        RefreshBrowserTabHeaders();
        _activeBrowserTabIndex = -1;
        SwitchBrowserTab(targetIndex);
        if (showStatusMessage)
        {
            ShowStatusMessage("タブを閉じました。");
        }
        return true;
    }
    private void CloseCurrentBrowserTab()
    {
        TryCloseBrowserTab(_activeBrowserTabIndex);
    }
    private bool CloseBrowserTabRange(IReadOnlyList<int> tabIndices, int preferredTabIndex, string successMessage, string nothingToCloseMessage)
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        if (preferredTabIndex < 0 || preferredTabIndex >= _browserTabs.Count)
        {
            return false;
        }
        var closableIndices = tabIndices
            .Distinct()
            .Where(index => index >= 0 && index < _browserTabs.Count && !_browserTabs[index].IsLocked)
            .OrderByDescending(index => index)
            .ToList();
        if (closableIndices.Count == 0)
        {
            ShowStatusMessage(nothingToCloseMessage);
            return false;
        }
        BrowserTabState preferredTab = _browserTabs[preferredTabIndex];
        if (_activeBrowserTabIndex != preferredTabIndex)
        {
            SwitchBrowserTab(preferredTabIndex);
        }
        foreach (int index in closableIndices)
        {
            _browserTabs.RemoveAt(index);
        }
        RefreshBrowserTabHeaders();
        int targetIndex = _browserTabs.IndexOf(preferredTab);
        if (targetIndex < 0)
        {
            targetIndex = Math.Clamp(preferredTabIndex, 0, _browserTabs.Count - 1);
        }
        _activeBrowserTabIndex = -1;
        SwitchBrowserTab(targetIndex);
        ShowStatusMessage(successMessage);
        return true;
    }
    private void CloseBrowserTabsToRight(int tabIndex)
    {
        var tabIndices = Enumerable.Range(tabIndex + 1, Math.Max(0, _browserTabs.Count - tabIndex - 1)).ToList();
        CloseBrowserTabRange(tabIndices, tabIndex, "右側のタブを閉じました。", "閉じられる右側タブはありません。");
    }
    private void CloseBrowserTabsToLeft(int tabIndex)
    {
        var tabIndices = Enumerable.Range(0, Math.Max(0, tabIndex)).ToList();
        CloseBrowserTabRange(tabIndices, tabIndex, "左側のタブを閉じました。", "閉じられる左側タブはありません。");
    }
    private void CloseOtherBrowserTabs(int tabIndex)
    {
        var tabIndices = Enumerable.Range(0, _browserTabs.Count)
            .Where(index => index != tabIndex)
            .ToList();
        CloseBrowserTabRange(tabIndices, tabIndex, "このタブ以外を閉じました。", "閉じられる他タブはありません。");
    }
    private int CountClosableBrowserTabs(Func<int, bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < _browserTabs.Count; i++)
        {
            if (predicate(i) && !_browserTabs[i].IsLocked)
            {
                count++;
            }
        }
        return count;
    }
    private void BrowserTabStrip_TabDoubleClicked(object? sender, BrowserTabStripMouseEventArgs e)
    {
        ToggleBrowserTabLock(e.TabIndex);
    }
    private void BrowserTabStrip_TabRightClicked(object? sender, BrowserTabStripMouseEventArgs e)
    {
        if (_browserTabStrip == null || e.TabIndex < 0 || e.TabIndex >= _browserTabs.Count)
        {
            return;
        }
        _browserTabContextIndex = e.TabIndex;
        if (_activeBrowserTabIndex != e.TabIndex)
        {
            SwitchBrowserTab(e.TabIndex);
        }
        EnsureBrowserTabContextMenu();
        UpdateBrowserTabContextMenuItems(e.TabIndex);
        _browserTabContextMenu?.Show(_browserTabStrip, e.Location);
    }
    private void EnsureBrowserTabContextMenu()
    {
        if (_browserTabContextMenu != null)
        {
            return;
        }
        _browserTabContextMenu = new ContextMenuStrip();
        _toggleBrowserTabLockContextMenuItem = new ToolStripMenuItem();
        _toggleBrowserTabLockContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabContextIndex < 0)
            {
                return;
            }
            ToggleBrowserTabLock(_browserTabContextIndex);
        };
        _toggleBrowserTabReadOnlyContextMenuItem = new ToolStripMenuItem();
        _toggleBrowserTabReadOnlyContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabContextIndex < 0)
            {
                return;
            }
            ToggleBrowserTabReadOnly(_browserTabContextIndex);
        };
        _openBrowserTabFilterLockContextMenuItem = new ToolStripMenuItem("フィルタロック...(&L)");
        _openBrowserTabFilterLockContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabContextIndex < 0) return;
            OpenTabFilterLockDialog(_browserTabContextIndex);
        };
        _clearBrowserTabFilterLockContextMenuItem = new ToolStripMenuItem("フィルタロックを解除(&U)");
        _clearBrowserTabFilterLockContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabContextIndex < 0) return;
            ClearTabFilterLock(_browserTabContextIndex);
        };
        _closeBrowserTabContextMenuItem = new ToolStripMenuItem("このタブを閉じる");
        _closeBrowserTabContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabContextIndex < 0)
            {
                return;
            }
            TryCloseBrowserTab(_browserTabContextIndex);
        };
        _closeRightBrowserTabsContextMenuItem = new ToolStripMenuItem("右側の全てのタブを閉じる");
        _closeRightBrowserTabsContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabContextIndex < 0)
            {
                return;
            }
            CloseBrowserTabsToRight(_browserTabContextIndex);
        };
        _closeLeftBrowserTabsContextMenuItem = new ToolStripMenuItem("左側の全てのタブを閉じる");
        _closeLeftBrowserTabsContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabContextIndex < 0)
            {
                return;
            }
            CloseBrowserTabsToLeft(_browserTabContextIndex);
        };
        _closeOtherBrowserTabsContextMenuItem = new ToolStripMenuItem("このタブ以外を閉じる");
        _closeOtherBrowserTabsContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabContextIndex < 0)
            {
                return;
            }
            CloseOtherBrowserTabs(_browserTabContextIndex);
        };
        _browserTabContextMenu.Items.Add(_toggleBrowserTabLockContextMenuItem);
        _browserTabContextMenu.Items.Add(_toggleBrowserTabReadOnlyContextMenuItem);
        _browserTabContextMenu.Items.Add(new ToolStripSeparator());
        _browserTabContextMenu.Items.Add(_openBrowserTabFilterLockContextMenuItem);
        _browserTabContextMenu.Items.Add(_clearBrowserTabFilterLockContextMenuItem);
        _browserTabContextMenu.Items.Add(new ToolStripSeparator());
        _browserTabContextMenu.Items.Add(_closeBrowserTabContextMenuItem);
        _browserTabContextMenu.Items.Add(_closeRightBrowserTabsContextMenuItem);
        _browserTabContextMenu.Items.Add(_closeLeftBrowserTabsContextMenuItem);
        _browserTabContextMenu.Items.Add(_closeOtherBrowserTabsContextMenuItem);
    }
    private void UpdateBrowserTabContextMenuItems(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabs.Count)
        {
            return;
        }
        BrowserTabState state = _browserTabs[tabIndex];
        if (_toggleBrowserTabLockContextMenuItem != null)
        {
            _toggleBrowserTabLockContextMenuItem.Text = state.IsLocked
                ? "このタブの固定を解除"
                : "このタブを固定";
        }
        if (_toggleBrowserTabReadOnlyContextMenuItem != null)
        {
            _toggleBrowserTabReadOnlyContextMenuItem.Text = state.IsReadOnly
                ? "このタブの ReadOnly を解除"
                : "このタブを ReadOnly にする";
        }
        if (_clearBrowserTabFilterLockContextMenuItem != null)
        {
            _clearBrowserTabFilterLockContextMenuItem.Enabled = state.FilterLock.Enabled && state.FilterLock.HasAnyCondition;
        }
        if (_closeBrowserTabContextMenuItem != null)
        {
            _closeBrowserTabContextMenuItem.Text = state.IsLocked
                ? "このタブを閉じる（固定中は不可）"
                : "このタブを閉じる";
        }
        if (_closeRightBrowserTabsContextMenuItem != null)
        {
            _closeRightBrowserTabsContextMenuItem.Enabled = CountClosableBrowserTabs(index => index > tabIndex) > 0;
        }
        if (_closeLeftBrowserTabsContextMenuItem != null)
        {
            _closeLeftBrowserTabsContextMenuItem.Enabled = CountClosableBrowserTabs(index => index < tabIndex) > 0;
        }
        if (_closeOtherBrowserTabsContextMenuItem != null)
        {
            _closeOtherBrowserTabsContextMenuItem.Enabled = CountClosableBrowserTabs(index => index != tabIndex) > 0;
        }
    }
    private void SwitchBrowserTab(int newIndex)
    {
        EnsureBrowserModeBeforeWorkspaceNavigation();
        if (_isSwitchingBrowserTab || newIndex < 0 || newIndex >= _browserTabs.Count)
        {
            return;
        }
        if (newIndex == _activeBrowserTabIndex)
        {
            browserPanel.Focus();
            return;
        }
        CaptureActiveBrowserTabState();
        _isSwitchingBrowserTab = true;
        try
        {
            _activeBrowserTabIndex = newIndex;
            BrowserTabState state = _browserTabs[newIndex];
            _columnCount = Math.Clamp(state.ColumnCount, 1, 9);
            _currentSort = state.SortKind;
            _sortAscending = state.SortAscending;
            _navigationService.RestoreState(state.Navigation);
            string targetPath = state.CurrentPath;
            if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
            {
                targetPath = Directory.Exists(_navigationService.CurrentPath)
                    ? _navigationService.CurrentPath
                    : Environment.CurrentDirectory;
            }
            if (!ExecuteDirectoryNavigationRequest(
                _browserNavigationCoordinator.CreateDirectoryNavigationRequest(
                    targetPath,
                    state.FocusTargetName,
                    isHistoryNavigation: true,
                    suppressRecent: true)))
            {
                return;
            }
            RestoreMarksForBrowserTab(state);
            RefreshBrowserTabHeaders();
            browserPanel.Focus();
        }
        finally
        {
            _isSwitchingBrowserTab = false;
        }
    }
    private void SelectAdjacentBrowserTab(int delta)
    {
        if (GuardClipboardBusy())
        {
            return;
        }
        if (_browserTabs.Count <= 1)
        {
            return;
        }
        int nextIndex = (_activeBrowserTabIndex + delta + _browserTabs.Count) % _browserTabs.Count;
        SwitchBrowserTab(nextIndex);
    }
    private string? GetActiveBrowserTabLockRootPath()
    {
        if (_activeBrowserTabIndex < 0 || _activeBrowserTabIndex >= _browserTabs.Count)
        {
            return null;
        }
        BrowserTabState state = _browserTabs[_activeBrowserTabIndex];
        if (!state.IsLocked || string.IsNullOrWhiteSpace(state.StartupPath))
        {
            return null;
        }
        return state.StartupPath;
    }
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
        return $"({bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height})";
    }
    // ウィンドウ復元時の境界崩れを検出・補正する補助処理
    private static bool IsSaneNormalBounds(Rectangle bounds)
    {
        return bounds.Width >= MinimumNormalWindowWidth && bounds.Height >= MinimumNormalWindowHeight;
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
    private static bool IsCollapsedWindowBounds(Rectangle bounds)
    {
        return !IsSaneNormalBounds(bounds);
    }
    private static Rectangle ToRectangle(RECT rect)
    {
        return Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
    }
    private static RECT FromRectangle(Rectangle rect)
    {
        return new RECT { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
    }
    private void LogWindowPlacementSnapshot(string context)
    {
        var wp = new WINDOWPLACEMENT();
        wp.length = Marshal.SizeOf(wp);
        if (GetWindowPlacement(this.Handle, ref wp))
        {
            Rectangle normal = ToRectangle(wp.rcNormalPosition);
            LogService.Info($"[WindowRestoreFloorHit] {context} State={this.WindowState} PlacementNormal={FormatBoundsForLog(normal)} Bounds={FormatBoundsForLog(this.Bounds)} Watch={_isInRestorePlacementWatch}");
        }
    }
    private bool IsCollapsedWindowPlacementNormal(WINDOWPLACEMENT placement)
    {
        return IsCollapsedWindowBounds(ToRectangle(placement.rcNormalPosition));
    }
    private bool IsRestoreFloorHitCorruption(Rectangle candidate)
    {
        // 復元監視中のみ、高さが 480px 付近（floor-hit）なら汚染とみなす
        // 手動リサイズ開始（SC_SIZE）時に監視は終了するため、通常操作への干渉は抑制される。
        if (_isInRestorePlacementWatch)
        {
            if (candidate.Height > 0 && candidate.Height <= MinimumNormalWindowHeight + 4)
            {
                return true;
            }
        }
        // 1秒過ぎたら監視終了（フェイルセーフ）
        if (_isInRestorePlacementWatch && (DateTime.UtcNow - _lastRestoreUtc).TotalMilliseconds >= 1000)
        {
            _isInRestorePlacementWatch = false;
            LogService.Info($"[WindowRestoreFloorHit] End restore watch Reason=Timeout Bounds={FormatBoundsForLog(this.Bounds)}");
        }
        return false;
    }
    private Rectangle? _lastRecoveredCollapsedBounds;
    private DateTime _lastRecoveryUtc;
    private bool ShouldSuppressDuplicateCollapsedRecovery(Rectangle collapsedBounds)
    {
        if (_lastRecoveredCollapsedBounds == null) return false;
        // タブ復元直後は collapsed bounds になりやすいため、1000ms 経過時点で再評価して補正する。
        if (collapsedBounds == _lastRecoveredCollapsedBounds.Value &&
            (DateTime.UtcNow - _lastRecoveryUtc).TotalMilliseconds < 1000)
        {
            return true;
        }
        return false;
    }
    private void TryCaptureCurrentNormalBounds()
    {
        if (_isApplyingWindowBoundsRecovery) return;
        if (this.WindowState != FormWindowState.Normal) return;
        var currentBounds = this.Bounds;
        if (!IsSaneNormalBounds(currentBounds) || !HasUsableClientArea()) return;
        if (IsRestoreFloorHitCorruption(currentBounds))
        {
            LogService.Info($"[WindowRestoreFloorHit] Skip Capture CurrentNormalBounds due to floor-hit corruption: {FormatBoundsForLog(currentBounds)}");
            return;
        }
        _lastKnownGoodNormalBounds = currentBounds;
        // Record as baseline if it's "truly sane" (clearly above the floor)
        // This ensures Win+M has a reliable target to restore to.
        if (currentBounds.Height > MinimumNormalWindowHeight + 40)
        {
            _restoreBaselineNormalBounds = currentBounds;
        }
    }
    private void ScheduleRestorePlacementRepair(Rectangle repairBounds, string trigger)
    {
        if (_restorePlacementRepairScheduled)
        {
            LogService.Info($"[WindowRestoreRepairLoop] Repair scheduled skipped because already scheduled. Trigger={trigger}");
            return;
        }
        if (_restorePlacementRepairCount >= 2)
        {
            LogService.Warn($"[WindowRestoreRepairLoop] Repair suppressed because limit reached. Trigger={trigger}");
            return;
        }
        LogService.Info($"[WindowRestoreRepairLoop] Detected floor-hit; schedule repair. Trigger={trigger} Target={FormatBoundsForLog(repairBounds)}");
        _restorePlacementRepairScheduled = true;
        _pendingRestoreRepairBounds = repairBounds;
        BeginInvoke(new Action(async () =>
        {
            await Task.Delay(100);
            ApplyScheduledRestorePlacementRepair(trigger);
        }));
    }
    private void ApplyScheduledRestorePlacementRepair(string trigger)
    {
        if (!_restorePlacementRepairScheduled || _pendingRestoreRepairBounds == null) return;
        try
        {
            _isApplyingWindowBoundsRecovery = true;
            _restorePlacementRepairCount++;
            Rectangle recoveryBounds = _pendingRestoreRepairBounds.Value;
            LogService.Info($"[WindowRestoreRepairLoop] Repair applied count={_restorePlacementRepairCount} Bounds={FormatBoundsForLog(recoveryBounds)} Trigger={trigger}");
            var wp = new WINDOWPLACEMENT();
            wp.length = Marshal.SizeOf(wp);
            if (GetWindowPlacement(this.Handle, ref wp))
            {
                int beforeShowCmd = wp.showCmd;
                bool beforeVisible = this.Visible;
                FormWindowState beforeState = this.WindowState;
                wp.rcNormalPosition = FromRectangle(recoveryBounds);
                wp.showCmd = SW_SHOWNORMAL;
                SetWindowPlacement(this.Handle, ref wp);
                var wpAfter = new WINDOWPLACEMENT();
                wpAfter.length = Marshal.SizeOf(wpAfter);
                GetWindowPlacement(this.Handle, ref wpAfter);
                LogService.Info($"[WindowRestoreShowCmd] BeforeRepair Visible={beforeVisible} WindowState={beforeState} PlacementShowCmd={beforeShowCmd} | AfterRepair Visible={this.Visible} WindowState={this.WindowState} PlacementShowCmd={wpAfter.showCmd}");
            }
            if (!this.Visible)
            {
                this.Show();
            }
            this.WindowState = FormWindowState.Normal;
            this.SetBounds(recoveryBounds.X, recoveryBounds.Y, recoveryBounds.Width, recoveryBounds.Height);
            _lastKnownGoodNormalBounds = recoveryBounds;
            _lastRecoveredCollapsedBounds = this.Bounds;
            _lastRecoveryUtc = DateTime.UtcNow;
            if (_isInRestorePlacementWatch && IsSaneNormalBounds(this.Bounds) && this.Bounds.Height > MinimumNormalWindowHeight + 40)
            {
                _isInRestorePlacementWatch = false;
                LogService.Info($"[WindowRestoreFloorHit] End restore watch Reason=RepairSuccess Bounds={FormatBoundsForLog(this.Bounds)}");
            }
        }
        finally
        {
            _restorePlacementRepairScheduled = false;
            _pendingRestoreRepairBounds = null;
            BeginInvoke(new Action(async () =>
            {
                await Task.Delay(50);
                _isApplyingWindowBoundsRecovery = false;
                LogService.Info("[WindowRestoreRepairLoop] Repair guard released");
            }));
        }
    }
    private void RecoverCollapsedWindowBounds(string trigger)
    {
        if (_isApplyingWindowBoundsRecovery || _restorePlacementRepairScheduled) return;
        var currentBounds = this.Bounds;
        if (ShouldSuppressDuplicateCollapsedRecovery(currentBounds))
        {
            LogService.Info($"[WindowVisibility] SuppressDuplicateCollapsedRecovery Trigger={trigger} CollapsedBounds={FormatBoundsForLog(currentBounds)}");
            return;
        }
        Rectangle recoveryBounds;
        string fallbackSource;
        // Priority: PreMinimize -> RestoreBaseline -> LastKnownGood -> Settings -> Default
        if (_normalBoundsBeforeMinimize is { } preMin && IsSaneNormalBounds(preMin))
        {
            recoveryBounds = preMin;
            fallbackSource = "PreMinimize";
        }
        else if (_restoreBaselineNormalBounds is { } baseline && IsSaneNormalBounds(baseline))
        {
            recoveryBounds = baseline;
            fallbackSource = "RestoreBaseline";
        }
        else if (_lastKnownGoodNormalBounds is { } lastGood && IsSaneNormalBounds(lastGood))
        {
            recoveryBounds = lastGood;
            fallbackSource = "LastKnownGood";
        }
        else if (IsSaneNormalBounds(new Rectangle(_settings.Window.X, _settings.Window.Y, _settings.Window.Width, _settings.Window.Height)))
        {
            recoveryBounds = new Rectangle(_settings.Window.X, _settings.Window.Y, _settings.Window.Width, _settings.Window.Height);
            fallbackSource = "Settings";
        }
        else
        {
            var primaryArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
            recoveryBounds = new Rectangle(primaryArea.X + 100, primaryArea.Y + 100, 1024, 768);
            fallbackSource = "DefaultSafe";
        }
        string logMsg = "[WindowVisibility] RecoverCollapsedWindowBounds Scheduled " +
            "Trigger=" + trigger + " " +
            "CollapsedBounds=" + FormatBoundsForLog(currentBounds) + " " +
            "RecoveryBounds=" + FormatBoundsForLog(recoveryBounds) + " " +
            "Source=" + fallbackSource;
        LogService.Info(logMsg);
        ScheduleRestorePlacementRepair(recoveryBounds, trigger);
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
            TryProcessPendingCurrentDirectoryRefresh("Activated");
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
        int width = Math.Min(720, Math.Max(520, browserPanel.ClientSize.Width - 96));
        int availableHeight = Math.Max(152, browserPanel.ClientSize.Height - 32);
        int desiredHeight = 112 + (_commandHintRows.Count * 22);
        int height = Math.Min(344, Math.Max(152, Math.Min(availableHeight, desiredHeight)));
        int left = Math.Max(12, browserPanel.ClientSize.Width - width - 12);
        return new Rectangle(left, 12, width, height);
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
        if (_lastLoggedCommandHintRowCount != _commandHintRows.Count ||
            _lastLoggedCommandHintBounds != overlayRect ||
            _lastLoggedCommandHintPanelSize != panelSize)
        {
            string firstRow = _commandHintRows.Count > 0
                ? $"{_commandHintRows[0].SlotLabel}:{_commandHintRows[0].Title}"
                : "<none>";
            LogAltHint($"DrawCommandHintOverlay Bounds={overlayRect} Panel={panelSize} RowCount={_commandHintRows.Count} First={firstRow}");
            _lastLoggedCommandHintRowCount = _commandHintRows.Count;
            _lastLoggedCommandHintBounds = overlayRect;
            _lastLoggedCommandHintPanelSize = panelSize;
        }
        using SolidBrush backgroundBrush = new(Color.FromArgb(232, 0, 0, 0));
        using Pen borderPen = new(MidFDColors.BorderLine);
        using Pen separatorPen = new(Color.FromArgb(0, 120, 120));
        using SolidBrush titleBrush = new(Color.Yellow);
        using SolidBrush textBrush = new(MidFDColors.ListNormalFore);
        using SolidBrush exeBrush = new(Color.White);
        using Font titleFont = new("Consolas", 11F, FontStyle.Bold, GraphicsUnit.Point);
        using Font bodyFont = new("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        g.FillRectangle(backgroundBrush, overlayRect);
        g.DrawRectangle(borderPen, overlayRect);
        int padding = 14;
        int contentWidth = overlayRect.Width - (padding * 2);
        int slotWidth = 126;
        int exeWidth = Math.Max(190, Math.Min(250, (contentWidth * 33) / 100));
        int titleWidth = Math.Max(170, contentWidth - slotWidth - exeWidth);
        Rectangle titleRect = new(overlayRect.Left + padding, overlayRect.Top + padding - 2, contentWidth, 22);
        TextRenderer.DrawText(
            g,
            "External Tool Alt Slot",
            titleFont,
            titleRect,
            Color.Yellow,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Rectangle explanationRect = new(overlayRect.Left + padding, titleRect.Bottom + 4, contentWidth, 36);
        TextRenderer.DrawText(
            g,
            "Alt+slot で external_tools.json の割当済み外部ツールを直接起動します。",
            bodyFont,
            explanationRect,
            MidFDColors.ListNormalFore,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        int headerTop = explanationRect.Bottom + 6;
        g.DrawLine(separatorPen, overlayRect.Left + padding, headerTop - 4, overlayRect.Right - padding, headerTop - 4);
        Rectangle slotHeaderRect = new(overlayRect.Left + padding, headerTop, slotWidth, 18);
        Rectangle titleHeaderRect = new(slotHeaderRect.Right, headerTop, titleWidth, 18);
        Rectangle exeHeaderRect = new(titleHeaderRect.Right, headerTop, exeWidth, 18);
        TextRenderer.DrawText(g, "Slot", bodyFont, slotHeaderRect, Color.Yellow, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Title", bodyFont, titleHeaderRect, Color.Yellow, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Exe", bodyFont, exeHeaderRect, Color.Yellow, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        int rowTop = slotHeaderRect.Bottom + 4;
        int rowHeight = 22;
        int availableRows = Math.Max(1, (overlayRect.Bottom - padding - rowTop) / rowHeight);
        int visibleRows = Math.Min(availableRows, _commandHintRows.Count);
        for (int i = 0; i < visibleRows; i++)
        {
            ExternalToolAltHintRow row = _commandHintRows[i];
            int top = rowTop + (i * rowHeight);
            Rectangle slotRect = new(overlayRect.Left + padding, top, slotWidth, rowHeight);
            Rectangle titleRectRow = new(slotRect.Right, top, titleWidth, rowHeight);
            Rectangle exeRectRow = new(titleRectRow.Right, top, exeWidth, rowHeight);
            TextRenderer.DrawText(g, row.SlotLabel, bodyFont, slotRect, MidFDColors.ListNormalFore, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, row.Title, bodyFont, titleRectRow, MidFDColors.ListNormalFore, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, row.ExecutableName, bodyFont, exeRectRow, Color.White, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        if (_commandHintRows.Count > visibleRows)
        {
            int remain = _commandHintRows.Count - visibleRows;
            Rectangle moreRect = new(overlayRect.Left + padding, rowTop + (visibleRows * rowHeight), contentWidth, rowHeight);
            TextRenderer.DrawText(
                g,
                $"ほか {remain} 件",
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
    private void InitializeMenuStrip()
    {
        mainMenuStrip.Items.Clear();
        _browserOnlyMenuItems.Clear();
        _busyAwareMenuItems.Clear();
        _menuItemRules.Clear();
        var fileMenu = new ToolStripMenuItem("ファイル(&F)");
        fileMenu.DropDownItems.Add(CreateMenuItem("内容確認/実行(eXecute)(&O)", (s, e) => ExecuteCurrentFile(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Execute, "X")));
        fileMenu.DropDownItems.Add(CreateMenuItem("コピー(&C)", (s, e) => _ = ExecuteCopy(), browserOnly: true, requiresIdle: true, requiresSelection: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Copy, "C")));
        fileMenu.DropDownItems.Add(CreateMenuItem("移動(&M)", (s, e) => _ = ExecuteMove(), browserOnly: true, requiresIdle: true, requiresSelection: true, shortcutHint: "M"));
        fileMenu.DropDownItems.Add(CreateMenuItem("名前変更(&R)", (s, e) => ExecuteRename(), browserOnly: true, requiresIdle: true, requiresSelection: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Rename, "R")));
        fileMenu.DropDownItems.Add(CreateMenuItem("削除(&D)", (s, e) => _ = ExecuteDelete(), browserOnly: true, requiresIdle: true, requiresSelection: true, shortcutHint: "D / Delete"));
        fileMenu.DropDownItems.Add(CreateMenuItem("MidFD管理ゴミ箱を空にする(&T)", (s, e) => EmptyMidFdManagedTrash(), browserOnly: true, requiresIdle: true));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(CreateMenuItem("新規フォルダ(&K)", (s, e) => ExecuteCreateDirectory(), browserOnly: true, requiresIdle: true, shortcutHint: "K"));
        fileMenu.DropDownItems.Add(CreateMenuItem("新規ファイル(&N)", (s, e) => ExecuteCreateFile(), browserOnly: true, requiresIdle: true, shortcutHint: "N"));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(CreateMenuItem("終了(&X)", (s, e) => Close()));
        var viewMenu = new ToolStripMenuItem("表示(&V)");
        viewMenu.DropDownItems.Add(CreateMenuItem("ソート(&S)", (s, e) => ExecuteSort(), browserOnly: true, requiresIdle: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Sort, "S")));
        viewMenu.DropDownItems.Add(CreateMenuItem("フィルタ(&F)", (s, e) => ExecuteFilter(), browserOnly: true, requiresIdle: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Filter, "F / Ctrl+F")));
        _reloadCurrentDirectoryMenuItem = CreateMenuItem("現在ディレクトリを再読込(&R)", (s, e) => ExecuteCurrentDirectoryReloadCommand(), browserOnly: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Reload, "Ctrl+R"));
        viewMenu.DropDownItems.Add(_reloadCurrentDirectoryMenuItem);
        viewMenu.DropDownItems.Add(CreateMenuItem("現在タブのフィルタロック...(&L)", (s, e) => OpenActiveTabFilterLockDialog(), browserOnly: true, requiresIdle: true, shortcutHint: "Ctrl+Shift+L"));
        _clearTabFilterLockMenuItem = CreateMenuItem("現在タブのフィルタロックを解除(&U)", (s, e) => ClearActiveTabFilterLock(), browserOnly: true, requiresIdle: true);
        viewMenu.DropDownItems.Add(_clearTabFilterLockMenuItem);
        viewMenu.DropDownItems.Add(CreateMenuItem("内蔵Viewer / 画像Viewer(&P)", (s, e) => ExecutePreviewLaunch(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: true, shortcutHint: "V / Enter"));
        viewMenu.DropDownItems.Add(CreateMenuItem("Logdisk(&L)", (s, e) => ExecuteLogdisk(), browserOnly: true, requiresIdle: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Logdisk, "L")));
        var moveMenu = new ToolStripMenuItem("移動(&G)");
        moveMenu.DropDownItems.Add(CreateMenuItem("親へ(&U)", (s, e) => ExecuteBackspace(), browserOnly: true, requiresIdle: true, shortcutHint: "Backspace"));
        moveMenu.DropDownItems.Add(CreateMenuItem("ルートへ(&R)", (s, e) => ExecuteDriveRoot(), browserOnly: true, requiresIdle: true, shortcutHint: "\\"));
        moveMenu.DropDownItems.Add(CreateMenuItem("Top(&T)", (s, e) => ExecuteFunctionKey(11), browserOnly: true, requiresIdle: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Top, "")));
        moveMenu.DropDownItems.Add(CreateMenuItem("Bottom(&B)", (s, e) => ExecuteFunctionKey(12), browserOnly: true, requiresIdle: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Bottom, "")));
        moveMenu.DropDownItems.Add(CreateMenuItem("Tree(&E)", (s, e) => ExecuteTreeDialog(), browserOnly: true, requiresIdle: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Tree, "T")));
        moveMenu.DropDownItems.Add(CreateMenuItem("QuickAccess(&Q)", (s, e) => ExecuteQuickAccess(), browserOnly: true, requiresIdle: true, shortcutHint: "Q"));
        moveMenu.DropDownItems.Add(CreateMenuItem("戻る(&A)", (s, e) => ExecuteHistoryBack(), browserOnly: true, requiresIdle: true, shortcutHint: "Alt+Left"));
        moveMenu.DropDownItems.Add(CreateMenuItem("進む(&D)", (s, e) => ExecuteHistoryForward(), browserOnly: true, requiresIdle: true, shortcutHint: "Alt+Right"));
        moveMenu.DropDownItems.Add(new ToolStripSeparator());
        moveMenu.DropDownItems.Add(CreateMenuItem("新しいタブを作る(&N)", (s, e) => CreateNewBrowserTab(), browserOnly: true, requiresIdle: true, shortcutHint: "Ctrl+T"));
        _toggleBrowserTabLockMenuItem = CreateMenuItem("現在のタブを固定(&K)", (s, e) => ToggleActiveBrowserTabLock(), browserOnly: true, requiresIdle: true, shortcutHint: "Ctrl+L");
        moveMenu.DropDownItems.Add(_toggleBrowserTabLockMenuItem);
        _toggleBrowserTabReadOnlyMenuItem = CreateMenuItem("現在のタブを ReadOnly にする(&Y)", (s, e) => ToggleActiveBrowserTabReadOnly(), browserOnly: true, requiresIdle: true);
        moveMenu.DropDownItems.Add(_toggleBrowserTabReadOnlyMenuItem);
        moveMenu.DropDownItems.Add(CreateMenuItem("次のタブへ(&X)", (s, e) => SelectAdjacentBrowserTab(+1), browserOnly: true, requiresIdle: true, shortcutHint: "Ctrl+Right / Ctrl+Tab"));
        moveMenu.DropDownItems.Add(CreateMenuItem("前のタブへ(&P)", (s, e) => SelectAdjacentBrowserTab(-1), browserOnly: true, requiresIdle: true, shortcutHint: "Ctrl+Left / Ctrl+Shift+Tab"));
        moveMenu.DropDownItems.Add(CreateMenuItem("現在のタブを閉じる(&W)", (s, e) => CloseCurrentBrowserTab(), browserOnly: true, requiresIdle: true, shortcutHint: "Ctrl+W"));
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
        var toolsMenu = new ToolStripMenuItem("ツール(&T)");
        toolsMenu.DropDownItems.Add(CreateMenuItem("圧縮(&P)", (s, e) => _ = ExecutePack(), browserOnly: true, requiresIdle: true, requiresSelection: true, shortcutHint: "P"));
        toolsMenu.DropDownItems.Add(CreateMenuItem("解凍(&U)", (s, e) => _ = ExecuteUnpack(), browserOnly: true, requiresIdle: true, requiresSelection: true, shortcutHint: GetFunctionAwareShortcutHint(FunctionKeyAction.Unpack, "U")));
        toolsMenu.DropDownItems.Add(CreateMenuItem("外部エディタで開く(&E)", (s, e) => ExecuteOpenWithEditor(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: true, shortcutHint: "E"));
        toolsMenu.DropDownItems.Add(CreateMenuItem("外部 Diff (2件比較専用)(&D)", (s, e) => ExecuteOpenWithDiff(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresExactlyTwoSelection: true, requiresTwoFiles: true));
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        toolsMenu.DropDownItems.Add(CreateMenuItem("マーク一覧 / スロット(&M)", (s, e) => OpenMarkSlotDialog(), browserOnly: true, requiresIdle: true, shortcutHint: "Ctrl+M"));
        if (_featureGate.IsEnabled(FeatureId.WorkspaceSnapshot))
        {
            toolsMenu.DropDownItems.Add(CreateMenuItem("Workspace スナップショット...(&W)", (s, e) => OpenWorkspaceSnapshotDialog(), browserOnly: true, requiresIdle: true));
        }
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        toolsMenu.DropDownItems.Add(CreateMenuItem("設定(&O)", (s, e) => OpenSettingsForm(), shortcutHint: "O"));
        var helpMenu = new ToolStripMenuItem("ヘルプ(&H)");
        helpMenu.DropDownItems.Add(CreateMenuItem("主なキー操作ヒント(&K)", (s, e) => ShowMenuKeyHint()));
        helpMenu.DropDownItems.Add(CreateMenuItem("バージョン情報(&A)", (s, e) => ShowVersionInfo()));
        mainMenuStrip.Items.AddRange(new ToolStripItem[]
        {
            fileMenu,
            viewMenu,
            moveMenu,
            toolsMenu,
            helpMenu
        });
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
        LogAltHintContext("MenuActivate");
        _isAltHintHeld = false;
        HideCommandHintOverlay("MenuActivate");
        UpdateMenuStripState();
        RefreshMenuStripRuntimeLayout("MenuActivate", defer: false);
    }
    private void HandleMenuStripMenuDeactivate(object? sender, EventArgs e)
    {
        LogAltHintContext("MenuDeactivate");
        RefreshCommandHintOverlayState();
    }
    private Font CreateMenuStripFont()
    {
        return SystemFonts.MenuFont ?? mainMenuStrip?.Font ?? this.Font;
    }
    private static (int Height, Padding Padding) CalculateMenuStripMetrics(Font menuFont)
    {
        Size textSize = TextRenderer.MeasureText("Hg", menuFont, Size.Empty, TextFormatFlags.NoPadding);
        int verticalPadding = Math.Max(1, (int)Math.Round(menuFont.SizeInPoints / 12f));
        int horizontalPadding = 4;
        int height = textSize.Height + (verticalPadding * 2) + 2;
        return (height, new Padding(horizontalPadding, verticalPadding, 0, verticalPadding));
    }
    private static Padding CalculateRootMenuItemPadding(Font menuFont)
    {
        int horizontal = Math.Max(6, (int)Math.Round(menuFont.SizeInPoints * 0.45f));
        int vertical = Math.Max(1, (int)Math.Round(menuFont.SizeInPoints / 14f));
        return new Padding(horizontal, vertical, horizontal, vertical);
    }
    private static Padding CalculateDropDownItemPadding(Font menuFont)
    {
        int horizontal = Math.Max(8, (int)Math.Round(menuFont.SizeInPoints * 0.55f));
        int vertical = Math.Max(2, (int)Math.Round(menuFont.SizeInPoints / 10f));
        return new Padding(horizontal, vertical, horizontal, vertical);
    }
    private static Padding CalculateDropDownInnerPadding(Font menuFont)
    {
        int horizontal = Math.Max(1, (int)Math.Round(menuFont.SizeInPoints / 18f));
        return new Padding(horizontal, 1, horizontal, 1);
    }
    private void SynchronizeMenuStripFontAndLayout(Font menuFont)
    {
        if (mainMenuStrip == null)
        {
            return;
        }
        mainMenuStrip.SuspendLayout();
        try
        {
            var metrics = CalculateMenuStripMetrics(menuFont);
            mainMenuStrip.AutoSize = false;
            mainMenuStrip.Font = menuFont;
            mainMenuStrip.Padding = metrics.Padding;
            mainMenuStrip.Height = metrics.Height;
            foreach (ToolStripMenuItem rootItem in mainMenuStrip.Items.OfType<ToolStripMenuItem>())
            {
                ApplyRootMenuVisualMetrics(rootItem, menuFont);
                ApplyToolStripItemFontAndLayout(rootItem, menuFont);
            }
        }
        finally
        {
            mainMenuStrip.ResumeLayout(true);
            mainMenuStrip.PerformLayout();
            mainMenuStrip.Invalidate();
        }
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
    private static void ApplyRootMenuVisualMetrics(ToolStripMenuItem item, Font menuFont)
    {
        item.Margin = Padding.Empty;
        item.Padding = CalculateRootMenuItemPadding(menuFont);
        item.TextAlign = ContentAlignment.MiddleCenter;
        item.DropDown.Padding = CalculateDropDownInnerPadding(menuFont);
        item.DropDown.Margin = Padding.Empty;
    }
    private static void ApplyToolStripItemFontAndLayout(ToolStripItem item, Font menuFont)
    {
        item.Font = menuFont;
        if (item is not ToolStripDropDownItem dropDownItem)
        {
            return;
        }
        dropDownItem.DropDown.SuspendLayout();
        try
        {
            dropDownItem.DropDown.Font = menuFont;
            foreach (ToolStripItem childItem in dropDownItem.DropDownItems)
            {
                ApplyDropDownItemVisualMetrics(childItem, menuFont);
                ApplyToolStripItemFontAndLayout(childItem, menuFont);
            }
        }
        finally
        {
            dropDownItem.DropDown.ResumeLayout(true);
            dropDownItem.DropDown.PerformLayout();
            dropDownItem.DropDown.Invalidate();
        }
    }
    private static void ApplyDropDownItemVisualMetrics(ToolStripItem item, Font menuFont)
    {
        if (item is ToolStripSeparator)
        {
            return;
        }
        item.Margin = Padding.Empty;
        if (item is ToolStripMenuItem menuItem)
        {
            menuItem.Padding = CalculateDropDownItemPadding(menuFont);
            menuItem.TextAlign = ContentAlignment.MiddleLeft;
        }
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
    private void ExecuteCreateDirectory()
    {
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
            Directory.CreateDirectory(target);
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
        return _commandStateCoordinator.CreateCommandUiSnapshot(
            isBrowserMode,
            _isClipboardBusy,
            selectionCount,
            HasTwoFileSelectionForCommandState(selectionCount),
            currentItem?.Text,
            currentPath);
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
    private string GetFunctionAwareShortcutHint(FunctionKeyAction action, string primaryShortcut)
    {
        int? fKey = FunctionKeyProfileService.ResolveKeyNumber(CurrentFunctionKeyProfileValue, action);
        if (!fKey.HasValue)
        {
            return primaryShortcut;
        }
        string functionKeyShortcut = $"F{fKey.Value}";
        if (string.IsNullOrWhiteSpace(primaryShortcut))
        {
            return functionKeyShortcut;
        }
        return $"{primaryShortcut} / {functionKeyShortcut}";
    }
    private bool IsFunctionKeyAssignedToAction(int fKey, FunctionKeyAction expectedAction)
    {
        return FunctionKeyProfileService.ResolveAction(CurrentFunctionKeyProfileValue, fKey) == expectedAction;
    }
    private bool ShouldShowBrowserFunctionBarForCurrentProfile()
    {
        return FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) == FunctionKeyProfile.FDCompatible;
    }
    private bool ShouldShowFunctionBarForCurrentContext()
    {
        if (_uiMode == UIMode.Browser)
        {
            return ShouldShowBrowserFunctionBarForCurrentProfile();
        }
        bool compactViewer = _uiMode == UIMode.Viewer
            && (_currentViewerKind == PreviewKind.Text || _currentViewerKind == PreviewKind.Binary || _currentViewerKind == PreviewKind.LargeText);
        return !compactViewer;
    }
    private void ApplyFunctionBarVisibilityForCurrentContext()
    {
        bool shouldShow = ShouldShowFunctionBarForCurrentContext();
        functionBarPanel.Visible = shouldShow;
        if (shouldShow)
        {
            functionBarPanel.Height = _functionBarPreferredHeight;
        }
        contentFramePanel.PerformLayout();
        mainAreaPanel.PerformLayout();
        viewerPanel.PerformLayout();
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
            "Ctrl+M: マーク一覧 / スロット\nCtrl+T: 新しいタブ\nCtrl+L / タブダブルクリック / タブ右クリック: 現在のタブ固定を切替\n" +
            "Ctrl+Right / Ctrl+Tab: 次のタブ\nCtrl+Left / Ctrl+Shift+Tab: 前のタブ\nCtrl+W: タブを閉じる（固定タブは閉じない）\n" +
            "Alt: Browser の直起動一覧\nAlt+slot: 割当済み external tool を直起動\n" +
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
        HideCommandHintOverlay();
        if (mode == UIMode.Browser)
        {
            _previewCts?.Cancel(); // プレビュー読み込み中なら中断
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
            const string browserStatus = "Z:Open  X:Check  A:Attr  E:Edit  F:Filter  S:Sort  L:Logd  V:View  H:Shell";
            viewerPanel.Visible = false;
            browserPanel.Visible = true;
            browserPanel.BringToFront(); // Z順を確実にする
            browserPanel.Focus();
            EnsureStatusBarVisible();
            // ブラウザモードコマンドバー表示 (レガシー statusStrip 側も一応維持)
            if (_notificationService != null)
            {
                NormalizeStatusLabelLayout();
                _notificationService.SetPersistent(browserStatus);
                NormalizeStatusLabelLayout();
            }
            else
            {
                statusLabel.Text = browserStatus;
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
    private PreviewKind GetCurrentSelectionPreviewKind()
    {
        var item = GetCurrentBrowserItem();
        string? fullPath = item?.Tag as string;
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return PreviewKind.None;
        }
        return GetEffectivePreviewKind(fullPath);
    }
    private PreviewKind GetEffectivePreviewKind(string path, PreviewKind rawKind)
    {
        if (rawKind == PreviewKind.Video)
        {
            var res = VideoToolResolutionService.Resolve(_settings.Preview?.VideoToolDirectory);
            if (!res.FfmpegFound)
            {
                return PreviewKind.Binary;
            }
        }
        return rawKind;
    }
    private PreviewKind GetEffectivePreviewKind(string path)
    {
        var rawKind = PreviewService.GetPreviewKind(path);
        return GetEffectivePreviewKind(path, rawKind);
    }
    private void ApplyViewerChromeState()
    {
        bool compactViewer = _uiMode == UIMode.Viewer
            && (_currentViewerKind == PreviewKind.Text || _currentViewerKind == PreviewKind.Binary || _currentViewerKind == PreviewKind.LargeText);
        titleHeaderPanel.Visible = !compactViewer;
        headerPanel.Visible = !compactViewer;
        sepBeforeTopPanel.Visible = !compactViewer; // Restore: Boundary between Page row and Path row
        topPanel.Visible = !compactViewer;
        ApplyFunctionBarVisibilityForCurrentContext();
        // LargeText 用コントロールの表示制御
        if (_largeFileControl != null)
        {
            _largeFileControl.Visible = (_uiMode == UIMode.Viewer && _currentViewerKind == PreviewKind.LargeText);
        }
    }
    private void UpdateFunctionBar()
    {
        ApplyFunctionBarVisibilityForCurrentContext();
        var snapshot = BuildCommandUiSnapshot();
        if (_commandStateCoordinator.UsesBrowserFunctionBar(snapshot))
        {
            IReadOnlyList<FunctionKeyDefinition> definitions = FunctionKeyProfileService.GetDefinitions(CurrentFunctionKeyProfileValue);
            for (int i = 1; i <= 12; i++)
            {
                FunctionKeyDefinition? definition = definitions.FirstOrDefault(def => def.KeyNumber == i);
                bool isVisible = definition?.VisibleOnFunctionBar == true && definition.Action != FunctionKeyAction.None;
                SetFuncKeyText(i, isVisible ? definition!.Label : "", isVisible);
            }
        }
        else
        {
            // Viewer モード
            for (int i = 1; i <= 12; i++) SetFuncKeyText(i, "", false);
            SetFuncKeyText(1, "L:Enc ", true); // L キーによる文字コード切替
            SetFuncKeyText(2, "W:Wrap", true); // W キーによる折り返し切替
            SetFuncKeyText(3, "^F:Find", true); // Ctrl+F による検索入力
            SetFuncKeyText(4, "F3:Next", true); // F3 による前方検索
            SetFuncKeyText(5, "S+F3:Prv", true); // Shift+F3 による後方検索
            SetFuncKeyText(10, "Qt(En/Es)", true); // Enter / Esc による終了
        }
    }
    private void ExecuteViewerFind()
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            ExecuteLargeFileFind();
            return;
        }
        if (!viewerTextBox.Visible) return;
        string? query = SimpleInputDialog.ShowNullable("検索:", "Viewer 検索 (Ctrl+F)", _viewerSearchKeyword);
        if (query == null) return; // キャンセル時は現状維持
        _viewerSearchKeyword = query;
        ApplyViewerStatusLine(); // ステータスに反映
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowStatusMessage("検索キーワードをクリアしました。");
            return;
        }
        // 初回検索: 現在位置の次から前方へ
        int start = viewerTextBox.SelectionStart + viewerTextBox.SelectionLength;
        _ = InnerExecuteViewerSearch(query, start, backward: false);
    }
    private void ExecuteViewerFindNext(bool backward)
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            ExecuteLargeFileFindNext(backward);
            return;
        }
        if (!viewerTextBox.Visible) return;
        if (string.IsNullOrWhiteSpace(_viewerSearchKeyword))
        {
            ShowStatusMessage("検索キーワードが未設定です。新規検索ダイアログを開きます...");
            ExecuteViewerFind();
            return;
        }
        int start;
        if (backward)
        {
            // 前方向: 現在の選択開始位置より前から探す
            start = viewerTextBox.SelectionStart;
        }
        else
        {
            // 次方向: 現在の選択終了位置から探す
            start = viewerTextBox.SelectionStart + viewerTextBox.SelectionLength;
        }
        _ = InnerExecuteViewerSearch(_viewerSearchKeyword, start, backward);
    }
    private async Task InnerExecuteViewerSearch(string query, int start, bool backward, bool isWrapAround = false, int chunkCrossoverCount = 0)
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            await ExecuteLargeFileSearchAsync(query, backward, isWrapAround);
            return;
        }
        RichTextBoxFinds options = backward ? RichTextBoxFinds.Reverse : RichTextBoxFinds.None;
        int result = viewerTextBox.Find(query, start, options);
        if (result < 0 && !isWrapAround)
        {
            if (backward)
            {
                result = viewerTextBox.Find(query, viewerTextBox.TextLength, options);
                if (result >= 0) ShowStatusMessage("末尾から再検索しました");
            }
            else
            {
                result = viewerTextBox.Find(query, 0, options);
                if (result >= 0) ShowStatusMessage("先頭から再検索しました");
            }
        }
        if (result >= 0)
        {
            viewerTextBox.Focus();
        }
        else
        {
            ShowStatusMessage($"一致する文字列が見つかりません: \"{query}\"");
        }
    }
    private async Task ExecuteLargeFileSearchAsync(string query, bool backward, bool isWrapAround)
    {
        if (_largeFileState == null) return;
        var state = _largeFileState;
        string normalizedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            ClearLargeFileSearchHit(state);
            ShowStatusMessage("検索キーワードが未設定です。");
            return;
        }
        int requestId = ++state.SearchRequestId;
        state.LastSearchText = normalizedQuery;
        state.LastSearchBackward = backward;
        _viewerSearchKeyword = normalizedQuery;
        ApplyViewerStatusLine();
        ShowStatusMessage($"検索中: {normalizedQuery}");
        var token = _previewCts?.Token ?? CancellationToken.None;
        var encoding = GetCurrentViewerEncoding();
        var (startLine, startColumn) = GetLargeFileSearchStartPosition(state, normalizedQuery, backward, isWrapAround);
        try
        {
            var hit = await Services.LargeFileLineReaderService.SearchTextAsync(
                state,
                normalizedQuery,
                startLine,
                startColumn,
                backward,
                encoding,
                token);
            if (!IsLargeFileSearchRequestActive(state, requestId))
            {
                return;
            }
            if (hit.HasValue)
            {
                await ApplyLargeFileSearchHitAsync(state, requestId, normalizedQuery, hit.Value.Line, hit.Value.Column, hit.Value.Length, backward, isWrapAround);
                return;
            }
            if (!isWrapAround)
            {
                ShowStatusMessage(backward ? "先頭まで検索しました。末尾から再検索します..." : "末尾まで検索しました。先頭から再検索します...");
                await ExecuteLargeFileSearchAsync(normalizedQuery, backward, true);
                return;
            }
            ClearLargeFileSearchHit(state);
            ShowStatusMessage($"一致する文字列が見つかりません: \"{normalizedQuery}\"");
        }
        catch (OperationCanceledException)
        {
        }
    }
    private void EnsureStatusBarVisible()
    {
        if (statusStrip == null || statusStrip.IsDisposed)
        {
            return;
        }
        statusStrip.Visible = true;
        statusLabel.Visible = true;
    }
    private void ExecuteLargeFileFind()
    {
        if (_largeFileState == null)
        {
            return;
        }
        string initialQuery = string.IsNullOrWhiteSpace(_largeFileState.LastSearchText)
            ? _viewerSearchKeyword
            : _largeFileState.LastSearchText;
        string? query = SimpleInputDialog.ShowNullable("検索:", "LargeText 検索 (Ctrl+F)", initialQuery);
        if (query == null)
        {
            return;
        }
        string normalizedQuery = query.Trim();
        bool continueFromActiveHit = !string.IsNullOrWhiteSpace(normalizedQuery)
            && string.Equals(_largeFileState.LastSearchText, normalizedQuery, StringComparison.OrdinalIgnoreCase)
            && _largeFileState.ActiveSearchHitLine.HasValue;
        _viewerSearchKeyword = normalizedQuery;
        _largeFileState.LastSearchText = normalizedQuery;
        ApplyViewerStatusLine();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            ClearLargeFileSearchHit(_largeFileState);
            ShowStatusMessage("検索キーワードをクリアしました。");
            return;
        }
        if (!continueFromActiveHit)
        {
            _largeFileState.ActiveSearchHitLine = null;
            _largeFileState.ActiveSearchHitColumn = 0;
            _largeFileState.ActiveSearchHitLength = 0;
        }
        _ = ExecuteLargeFileSearchAsync(normalizedQuery, backward: false, isWrapAround: false);
    }
    private void ExecuteLargeFileFindNext(bool backward)
    {
        if (_largeFileState == null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_largeFileState.LastSearchText))
        {
            ShowStatusMessage("検索キーワードが未設定です。新規検索ダイアログを開きます...");
            ExecuteLargeFileFind();
            return;
        }
        _ = ExecuteLargeFileSearchAsync(_largeFileState.LastSearchText, backward, false);
    }
    private (int StartLine, int StartColumn) GetLargeFileSearchStartPosition(LargeFilePreviewState state, string query, bool backward, bool isWrapAround)
    {
        if (isWrapAround)
        {
            return backward
                ? (Math.Max(0, state.TotalLines - 1), int.MaxValue)
                : (0, 0);
        }
        if (state.ActiveSearchHitLine.HasValue
            && string.Equals(state.LastSearchText, query, StringComparison.OrdinalIgnoreCase))
        {
            if (backward)
            {
                return (
                    state.ActiveSearchHitLine.Value,
                    Math.Max(-1, state.ActiveSearchHitColumn - 1));
            }
            return (
                state.ActiveSearchHitLine.Value,
                state.ActiveSearchHitColumn + Math.Max(1, state.ActiveSearchHitLength));
        }
        return backward
            ? (Math.Max(0, state.FirstVisibleLine), int.MaxValue)
            : (Math.Max(0, state.FirstVisibleLine), 0);
    }
    private async Task ApplyLargeFileSearchHitAsync(
        LargeFilePreviewState state,
        int requestId,
        string query,
        int hitLine,
        int hitColumn,
        int hitLength,
        bool backward,
        bool isWrapAround)
    {
        if (!IsLargeFileSearchRequestActive(state, requestId))
        {
            return;
        }
        state.ActiveSearchHitLine = hitLine;
        state.ActiveSearchHitColumn = hitColumn;
        state.ActiveSearchHitLength = hitLength;
        _largeFileControl.SetActiveSearchHit(hitLine, hitColumn, hitLength);
        int targetFirstLine = Math.Max(0, hitLine - Math.Max(1, _largeFileControl.VisibleLineCount / 2));
        await NavigateLargeFilePreviewAsync(targetFirstLine, "SearchHit");
        if (!IsLargeFileSearchRequestActive(state, requestId))
        {
            return;
        }
        _largeFileControl.SetActiveSearchHit(hitLine, hitColumn, hitLength);
        ApplyViewerStatusLine();
        string wrapPrefix = isWrapAround
            ? (backward ? "末尾から再検索しました。 " : "先頭から再検索しました。 ")
            : string.Empty;
        ShowStatusMessage($"{wrapPrefix}{query}: {hitLine + 1:N0} 行目");
    }
    private bool IsLargeFileSearchRequestActive(LargeFilePreviewState state, int requestId)
    {
        return ReferenceEquals(_largeFileState, state)
            && state.SearchRequestId == requestId
            && _uiMode == UIMode.Viewer
            && _currentViewerKind == PreviewKind.LargeText
            && string.Equals(_currentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase);
    }
    private void ClearLargeFileSearchHit(LargeFilePreviewState state)
    {
        state.ActiveSearchHitLine = null;
        state.ActiveSearchHitColumn = 0;
        state.ActiveSearchHitLength = 0;
        _largeFileControl.ClearActiveSearchHit();
        ApplyViewerStatusLine();
    }
    private void SetFuncKeyText(int num, string text, bool enabled)
    {
        if (num < 1 || num > 12) return;
        var lbl = lblFuncKeys[num - 1];
        // WinFD風: "数字:ラベル" 形式
        // 数字部分はシアン/青系、ラベル部分は白/灰系にするのが理想だが、
        // 最小差分のため単一ラベル内でテキスト構成する。
        if (string.IsNullOrEmpty(text))
        {
            lbl.Text = "";
        }
        else
        {
            // Phase 5-ui-visual-fix1.4c: 先頭空白と2桁パディングを廃止して領域を確保
            lbl.Text = $"{num}:{text.Trim()}";
        }
}
    private void LayoutFunctionBar()
    {
        // Phase 5-ui-layout-fix2: 個別 Label の Z-Order 問題を回避するため Paint 描画へ切り替え済み
        // lblFuncKeys は非表示にして、functionBarPanel_Paint での描画に委譲する
        foreach (var lbl in lblFuncKeys)
        {
            lbl.Visible = false;
        }
        UpdateFunctionBar();
        if (!functionBarPanel.Visible)
        {
            return;
        }
        functionBarPanel.Invalidate(); // Paint イベントを起動して再描画
    }
    private void FunctionBarPanel_MouseClick(object? sender, MouseEventArgs e)
    {
        // Phase 5-funcbar-click-fix1: Browser 文脈でのみ有効とする
        if (_uiMode != UIMode.Browser || !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        int totalW = functionBarPanel.ClientSize.Width;
        if (totalW <= 0) return;
        // FunctionBarPanel_Paint と同じ分割ロジック (itemW = totalW / 12) に揃える
        int itemW = Math.Max(1, totalW / 12);
        int index = e.X / itemW;
        // 境界値ガード。幅端数は最終セル(11)に吸収される
        if (index < 0) index = 0;
        if (index > 11) index = 11;
        HandleFuncKeyClick(index);
    }
    private void FunctionBarPanel_Paint(object? sender, PaintEventArgs e)
    {
        var panel = sender as Panel;
        if (panel == null) return;
        if (_uiMode == UIMode.Browser && !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        int totalW = panel.ClientSize.Width;
        int totalH = panel.ClientSize.Height;
        if (totalW <= 0 || totalH <= 0) return;
        int itemW = Math.Max(1, totalW / 12);
        using var font = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);
        using var bgBrush = new SolidBrush(panel.BackColor);
        e.Graphics.FillRectangle(bgBrush, e.ClipRectangle);
        for (int i = 0; i < 12; i++)
        {
            var lbl = lblFuncKeys[i];
            if (!lbl.Visible && lbl.Text.Length == 0) continue; // 空は省略
            string text = lbl.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            int x = i * itemW;
            int w = (i == 11) ? (totalW - x) : itemW;
            // Phase 5-ui-visual-fix1.3: 隣接項目との重なり防止のため左右に 2px の内側余白を設ける
            const int innerPad = 2;
            var rect = new Rectangle(x + innerPad, 0, w - (innerPad * 2), totalH);
            var color = lbl.ForeColor;
            // Phase 5-ui-visual-fix1.4c: 動的な表示文字判定。「全文字 → 入らなければ承認済み省略形」の2段階
            string displayText = text;
            Size fullSize = TextRenderer.MeasureText(e.Graphics, displayText, font, rect.Size, TextFormatFlags.NoPadding);
            if (fullSize.Width > rect.Width)
            {
                // 分割してラベル部分を取得 (例: "1:Help" -> "1:", "Help")
                int colonIndex = text.IndexOf(':');
                if (colonIndex >= 0)
                {
                    string numPart = text.Substring(0, colonIndex + 1);
                    string labelPart = text.Substring(colonIndex + 1).Trim();
                    string shortened = GetShortenedLabel(labelPart);
                    displayText = numPart + shortened;
                }
            }
            // Phase 5-ui-visual-fix1.4c: エリプシス (...) による逃げを廃止し、可読性を優先
            TextRenderer.DrawText(e.Graphics, displayText, font, rect, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
    /// <summary>
    /// Phase 5-ui-visual-fix1.4c: 幅不足時のための承認済み短縮ラベル。
    /// Browser/Viewer それぞれの規定の省略形。
    /// </summary>
    private string GetShortenedLabel(string fullLabelPart)
    {
        if (string.IsNullOrEmpty(fullLabelPart)) return fullLabelPart;
        return fullLabelPart switch
        {
            // Browser
            "Help" => "Hlp",
            "Exec" => "Exc",
            "Copy" => "Cpy",
            "Edit" => "Edt",
            "Sort" => "Srt",
            "Filter" => "Flt",
            "Tree" => "Tre",
            "Logd" => "Log",
            "Unpk" => "Unp",
            "Encode" => "Enc",
            "Wrap" => "Wrp",
            "Ren" => "Ren",
            "Top" => "Top",
            "Btm" => "Btm",
            // Viewer
            "L:Enc" => "Enc",
            "W:Wrap" => "Wrp",
            "^F:Find" => "Find",
            "F3:Next" => "Next",
            "S+F3:Prv" => "Prev",
            "Qt(En/Es)" => "Quit",
            _ => fullLabelPart
        };
    }
    private void PositionPreviewPopup()
    {
        if (!this.IsHandleCreated) return;
        // ユーザーが手動で移動した後は自動配置で上書きしない
        // Phase 5-image-preview-fix1: マルチモニター解除等で完全に画面外へ出ている場合は例外的に引き戻す
        if (_previewPopup.IsManuallyPositioned)
        {
            var currentScreen = Screen.FromControl(_previewPopup).WorkingArea;
            if (!currentScreen.IntersectsWith(_previewPopup.Bounds))
            {
                _previewPopup.IsManuallyPositioned = false; // 強制復帰
            }
            else
            {
                return;
            }
        }
        var screen = Screen.FromControl(this).WorkingArea;
        int popupW = 400;
        int popupH = 400;
        int x = this.Right + 4;
        int y = this.Top;
        // 画面右端をはみ出る場合は左側に出す
        if (x + popupW > screen.Right)
        {
            x = this.Left - popupW - 4;
        }
        // 画面内に収まるように調整
        if (x < screen.Left) x = screen.Left;
        if (y + popupH > screen.Bottom) y = screen.Bottom - popupH;
        if (y < screen.Top) y = screen.Top;
        _previewPopup.SetBounds(x, y, popupW, popupH);
    }
    private bool LoadDirectory(string targetPath, string? focusTargetName = null, bool isHistoryNavigation = false, bool suppressRecent = false)
    {
        try
        {
            var request = CreateDirectoryLoadRequest(targetPath, focusTargetName, isHistoryNavigation, suppressRecent);
            var result = _browserLoadCoordinator.Execute(
                request,
                new BrowserLoadCoordinator.ExecutionContext
                {
                    ShowStatusMessage = ShowStatusMessage,
                    DecoratePathItem = ApplyMarkColor
                });
            // 成功時 UI 反映のオーケストレーション
            ApplyDirectoryLoadUi(result);
            return true;
        }
        catch (Exception ex)
        {
            return NotifyDirectoryLoadFailure(ex);
        }
    }
    private BrowserLoadCoordinator.DirectoryLoadRequest CreateDirectoryLoadRequest(
        string targetPath,
        string? focusTargetName,
        bool isHistoryNavigation,
        bool suppressRecent)
    {
        string? currentFullName = null;
        var currentItem = GetCurrentBrowserItem();
        if (currentItem != null)
        {
            currentFullName = GetItemFullName(currentItem);
        }
        return new BrowserLoadCoordinator.DirectoryLoadRequest(
            targetPath,
            focusTargetName,
            isHistoryNavigation,
            suppressRecent,
            _navigationService.CurrentPath,
            _browserCursorIndex,
            currentFullName,
            _filterPattern,
            _filterUseRegex,
            _settings.Appearance?.ShowHiddenFiles ?? false,
            _currentSort,
            _sortAscending,
            GetActiveTabFilterLock(),
            _settings.Appearance?.DateFormat,
            _settings.Appearance?.SizeFormat,
            _settings.Appearance?.ShowDirectoryMarker ?? true);
    }
    private void PopulateListView(IReadOnlyList<ListViewItem> items)
    {
        fileListView.BeginUpdate();
        fileListView.Items.Clear();
        try
        {
            if (items.Count > 0)
            {
                fileListView.Items.AddRange(items.ToArray());
            }
        }
        finally
        {
            fileListView.EndUpdate();
        }
    }
    private void ApplyDirectoryLoadUi(BrowserLoadCoordinator.DirectoryLoadResult result)
    {
        bool directoryChanged = !string.Equals(
            NavigationService.NormalizeDirectoryForCompare(result.PreviousPath),
            NavigationService.NormalizeDirectoryForCompare(result.NewPath),
            StringComparison.OrdinalIgnoreCase);
        if (directoryChanged)
        {
            InvalidateRecentMultiMarkIntent();
            InvalidateMarkSummaryCache();
        }
        // 1. 内部状態とパス表示の更新
        _navigationService.SetCurrentPath(result.NewPath, result.IsHistoryNavigation);
        // 2. 一覧項目の再構築
        PopulateListView(result.Items);
        // 3. 選択状態の復元
        RestoreSelectionState(result.FocusTargetName, result.LastIndex, result.IsReload);
        // 4. パネル再描画 (RestoreSelectionState 内で UpdateInfoPanel も呼ばれるためここでは Invalidate のみ)
        browserPanel.Invalidate();
        if (!result.SuppressRecent)
        {
            RecordQuickAccessRecent(result.PreviousPath, result.NewPath, result.IsReload);
        }
        CaptureActiveBrowserTabState(captureMarks: false);
        UpdateCurrentDirectoryWatcher(result.NewPath, "ApplyDirectoryLoadUi");
        TryProcessPendingCurrentDirectoryRefresh("ApplyDirectoryLoadUi");
        // Phase: header stream / initial final relayout corrective follow-up
        // ディレクトリ読み込みとタブ状態確定後の最終レイアウトを保証する
        UpdateInfoPanel();
    }
    private void RecordQuickAccessRecent(string previousPath, string newPath, bool isReload)
    {
        if (isReload || string.IsNullOrWhiteSpace(previousPath))
        {
            return;
        }
        if (QuickAccessService.PathsEqual(previousPath, newPath))
        {
            return;
        }
        if (QuickAccessService.RecordRecent(_quickAccessStore, newPath))
        {
            QuickAccessService.Save(_quickAccessStore);
        }
    }
    private bool NotifyDirectoryLoadFailure(Exception ex)
    {
        ShowStatusMessage($"読み込み失敗: {ex.Message}");
        return false;
    }
    private bool TryResolveExistingDirectoryFallback(
        string? missingPath,
        out string fallbackPath,
        out string reason)
    {
        fallbackPath = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(missingPath))
        {
            return TryResolveDefaultFallback(out fallbackPath, out reason);
        }
        try
        {
            // 1. 消失した path の親ディレクトリを順に辿る
            string? current = null;
            try
            {
                current = Path.GetFullPath(missingPath);
            }
            catch
            {
                current = missingPath;
            }
            while (!string.IsNullOrWhiteSpace(current))
            {
                try
                {
                    string? parent = Directory.GetParent(current)?.FullName;
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        break;
                    }
                    if (Directory.Exists(parent))
                    {
                        fallbackPath = parent;
                        reason = "親";
                        return true;
                    }
                    current = parent;
                }
                catch
                {
                    break;
                }
            }
            // 2. ドライブルート
            try
            {
                string? root = Path.GetPathRoot(missingPath);
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    fallbackPath = root;
                    reason = "ルート";
                    return true;
                }
            }
            catch { }
            // 3. デフォルト fallback
            return TryResolveDefaultFallback(out fallbackPath, out reason);
        }
        catch (Exception ex)
        {
            LogService.Error($"[DirectoryRefresh] Fallback resolution failed: {ex.Message}");
            return TryResolveDefaultFallback(out fallbackPath, out reason);
        }
    }
    private bool TryResolveDefaultFallback(out string fallbackPath, out string reason)
    {
        fallbackPath = string.Empty;
        reason = string.Empty;
        try
        {
            // UserProfile
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
            {
                fallbackPath = userProfile;
                reason = "ユーザープロファイル";
                return true;
            }
            // AppContext.BaseDirectory
            string appDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(appDir) && Directory.Exists(appDir))
            {
                fallbackPath = appDir;
                reason = "アプリケーション";
                return true;
            }
        }
        catch { }
        return false;
    }
    private bool ReloadCurrentDirectory(string reason, bool force = false)
    {
        string currentPath = _navigationService.CurrentPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            ShowStatusMessage("現在ディレクトリが未確定のため再読込できません。");
            return false;
        }
        if (!force && IsCurrentDirectoryRefreshBlocked())
        {
            return false;
        }
        if (!Directory.Exists(currentPath))
        {
            if (TryResolveExistingDirectoryFallback(currentPath, out string fallbackPath, out string fallbackReason))
            {
                LogService.Info($"[DirectoryRefresh] Fallback triggered. missing={currentPath}, fallback={fallbackPath}, reason={fallbackReason}");
                ShowStatusMessage($"現在のフォルダが見つからないため、{fallbackReason}フォルダへ移動しました。");
                return LoadDirectory(fallbackPath);
            }
            UpdateCurrentDirectoryWatcher(null, "CurrentDirectoryMissing");
            ShowStatusMessage("現在ディレクトリが見つかりません。");
            return false;
        }
        bool loaded = LoadDirectory(currentPath);
        if (loaded)
        {
            ShowStatusMessage(reason);
            return true;
        }
        if (_currentDirectoryRefreshRetryPending)
        {
            return false;
        }
        _currentDirectoryRefreshRetryPending = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CurrentDirectoryRefreshRetryDelayMilliseconds).ConfigureAwait(false);
                if (IsDisposed || !IsHandleCreated)
                {
                    return;
                }
                BeginInvoke(new Action(() =>
                {
                    _currentDirectoryRefreshRetryPending = false;
                    if (!string.Equals(
                        NormalizeDirectoryWatchPath(_navigationService.CurrentPath),
                        NormalizeDirectoryWatchPath(currentPath),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    if (!Directory.Exists(currentPath))
                    {
                        UpdateCurrentDirectoryWatcher(null, "RetryDirectoryMissing");
                        ShowStatusMessage("現在ディレクトリが見つかりません。");
                        return;
                    }
                    if (LoadDirectory(currentPath))
                    {
                        ShowStatusMessage(reason);
                    }
                }));
            }
            catch (ObjectDisposedException)
            {
                _currentDirectoryRefreshRetryPending = false;
            }
        });
        return false;
    }
    private bool ExecuteCurrentDirectoryReloadCommand()
    {
        if (_uiMode != UIMode.Browser)
        {
            return false;
        }
        if (IsCurrentDirectoryBusy())
        {
            ShowStatusMessage("処理中のため再読込できません。");
            return true;
        }
        ClearPendingCurrentDirectoryRefresh();
        ReloadCurrentDirectory("現在ディレクトリを再読込しました。");
        return true;
    }
    private void QueueCurrentDirectoryRefresh(string watchedDirectoryPath, string reason)
    {
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => QueueCurrentDirectoryRefresh(watchedDirectoryPath, reason)));
            }
            catch (ObjectDisposedException)
            {
            }
            return;
        }
        string normalizedWatchedPath = NormalizeDirectoryWatchPath(watchedDirectoryPath);
        string normalizedCurrentPath = NormalizeDirectoryWatchPath(_navigationService.CurrentPath);
        string normalizedWatcherPath = NormalizeDirectoryWatchPath(_currentDirectoryWatcherPath);
        if (string.IsNullOrWhiteSpace(normalizedWatchedPath) ||
            !string.Equals(normalizedWatchedPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(normalizedWatchedPath, normalizedWatcherPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _pendingExternalDirectoryRefresh = true;
        _pendingExternalDirectoryRefreshPath = watchedDirectoryPath;
        _pendingExternalDirectoryRefreshReason = reason;
        _directoryRefreshDebounceTimer.Stop();
        _directoryRefreshDebounceTimer.Start();
    }
    private void TryProcessPendingCurrentDirectoryRefresh(string source)
    {
        if (!_pendingExternalDirectoryRefresh || _isApplyingExternalDirectoryRefresh)
        {
            return;
        }
        string currentPath = _navigationService.CurrentPath;
        string pendingPath = _pendingExternalDirectoryRefreshPath ?? string.Empty;
        if (!string.Equals(
            NormalizeDirectoryWatchPath(currentPath),
            NormalizeDirectoryWatchPath(pendingPath),
            StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingCurrentDirectoryRefresh();
            return;
        }
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy())
        {
            return;
        }
        _isApplyingExternalDirectoryRefresh = true;
        try
        {
            string reason = _pendingExternalDirectoryRefreshReason;
            ClearPendingCurrentDirectoryRefresh();
            ReloadCurrentDirectory($"外部変更を反映しました: {reason}", force: true);
        }
        finally
        {
            _isApplyingExternalDirectoryRefresh = false;
        }
    }
    private void ClearPendingCurrentDirectoryRefresh()
    {
        _pendingExternalDirectoryRefresh = false;
        _pendingExternalDirectoryRefreshPath = null;
        _pendingExternalDirectoryRefreshReason = "外部変更";
        _directoryRefreshDebounceTimer.Stop();
    }
    private void UpdateCurrentDirectoryWatcher(string? currentPath, string reason)
    {
        if (!_featureGate.IsEnabled(FeatureId.FileSystemWatcherAutoRefresh))
        {
            DisposeCurrentDirectoryWatcher();
            ClearPendingCurrentDirectoryRefresh();
            return;
        }
        string normalizedCurrentPath = NormalizeDirectoryWatchPath(currentPath);
        string normalizedWatcherPath = NormalizeDirectoryWatchPath(_currentDirectoryWatcherPath);
        if (!string.IsNullOrWhiteSpace(normalizedCurrentPath) &&
            string.Equals(normalizedCurrentPath, normalizedWatcherPath, StringComparison.OrdinalIgnoreCase) &&
            _currentDirectoryWatcher != null)
        {
            return;
        }
        DisposeCurrentDirectoryWatcher();
        _currentDirectoryWatcherPath = null;
        if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
        {
            return;
        }
        try
        {
            var watcher = new FileSystemWatcher(currentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = false
            };
            watcher.Created += (_, _) => QueueCurrentDirectoryRefresh(currentPath, "Created");
            watcher.Deleted += (_, _) => QueueCurrentDirectoryRefresh(currentPath, "Deleted");
            watcher.Renamed += (_, _) => QueueCurrentDirectoryRefresh(currentPath, "Renamed");
            watcher.Error += (_, _) => QueueCurrentDirectoryRefresh(currentPath, "Error");
            watcher.EnableRaisingEvents = true;
            _currentDirectoryWatcher = watcher;
            _currentDirectoryWatcherPath = currentPath;
        }
        catch (Exception ex)
        {
            LogService.Warn($"[DirectoryRefreshWatcher] Watcher init failed. reason={reason}, path={currentPath}, message={ex.Message}");
            ShowStatusMessage("現在ディレクトリ監視を開始できませんでした。Ctrl+R で再読込してください。");
        }
    }
    private void DisposeCurrentDirectoryWatcher()
    {
        if (_currentDirectoryWatcher == null)
        {
            return;
        }
        try
        {
            _currentDirectoryWatcher.EnableRaisingEvents = false;
            _currentDirectoryWatcher.Dispose();
        }
        catch (Exception ex)
        {
            LogService.Warn($"[DirectoryRefreshWatcher] Dispose failed. message={ex.Message}");
        }
        finally
        {
            _currentDirectoryWatcher = null;
            _currentDirectoryWatcherPath = null;
        }
    }
    private bool IsCurrentDirectoryBusy()
    {
        return _isClipboardBusy ||
            _fileOpCts != null ||
            !string.IsNullOrWhiteSpace(_activeFileOperationName) ||
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
                _activeFileOperationName,
                canCancel: _fileOpCts != null,
                isCancelRequested: _fileOpCts?.IsCancellationRequested ?? false));
            return true;
        }
        return false;
    }
    private bool RequestActiveFileOperationCancel(string source)
    {
        bool requestedBefore = _fileOpCts?.IsCancellationRequested ?? false;
        LogService.Info(
            $"[CancelRuntime] Request received. source={source}, thread={Environment.CurrentManagedThreadId}, " +
            $"busy={_isClipboardBusy}, hasCts={_fileOpCts != null}, alreadyRequested={requestedBefore}, " +
            $"operation={_activeFileOperationName ?? "<unknown>"}, statusVersion={_fileOperationStatusVersion}, " +
            $"progressForm={_shellDeleteProgressFallback != null}");
        if (_fileOpCts == null)
        {
            LogService.Warn($"[CancelRuntime] Request ignored because CTS is null. source={source}");
            return false;
        }
        try
        {
            LogService.Info(
                $"[CancelRuntime] MarkCancelRequested before. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                $"requested={_fileOpCts.IsCancellationRequested}, progressForm={_shellDeleteProgressFallback != null}");
            _shellDeleteProgressFallback?.MarkCancelRequested();
            LogService.Info(
                $"[CancelRuntime] MarkCancelRequested after. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                $"requested={_fileOpCts.IsCancellationRequested}, progressForm={_shellDeleteProgressFallback != null}");
            if (!_fileOpCts.IsCancellationRequested)
            {
                LogService.Warn(
                    $"[CancelRuntime] CTS cancel before. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                    $"requested={_fileOpCts.IsCancellationRequested}, operation={_activeFileOperationName ?? "<unknown>"}");
                _fileOperationCancelRequestedTimestamp = Stopwatch.GetTimestamp();
                _fileOpCts.Cancel();
                LogService.Warn(
                    $"[CancelRuntime] CTS cancel after. source={source}, thread={Environment.CurrentManagedThreadId}, " +
                    $"requested={_fileOpCts.IsCancellationRequested}, operation={_activeFileOperationName ?? "<unknown>"}");
                LogService.Info($"[FileOperationCancel] Cancel requested. source={source}, operation={_activeFileOperationName ?? "<unknown>"}, statusVersion={_fileOperationStatusVersion}");
                ShowStatusMessage(FileOperationPresentationHelper.GetCancelRequestedMessage(_activeFileOperationName ?? "ファイル操作"));
            }
            else
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                    _activeFileOperationName,
                    canCancel: true,
                    isCancelRequested: true));
            }
            LogService.Info(
                $"[CancelRuntime] Request completed. source={source}, requested={_fileOpCts.IsCancellationRequested}, " +
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
            _fileOpCts != null ||
            !string.IsNullOrWhiteSpace(_activeFileOperationName) ||
            _shellDeleteProgressFallback != null ||
            _isFileOperationUndoRedoBusy ||
            _undoRedoProgressFallback != null;
    }
    private bool TryRouteActiveFileOperationCancel(string source)
    {
        bool hasActiveContext = HasActiveFileOperationCancelContext();
        LogService.Info(
            $"[CancelRuntime] Active operation cancel route check. source={source}, activeContext={hasActiveContext}, " +
            $"busy={_isClipboardBusy}, hasCts={_fileOpCts != null}, activeOperation={_activeFileOperationName ?? "<none>"}, " +
            $"shellProgress={_shellDeleteProgressFallback != null}, undoRedoProgress={_undoRedoProgressFallback != null}, " +
            $"thread={Environment.CurrentManagedThreadId}");
        if (!hasActiveContext)
        {
            return false;
        }
        if (_fileOpCts != null)
        {
            RequestActiveFileOperationCancel(source);
        }
        else
        {
            ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                _activeFileOperationName,
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
    private bool ExecuteFunctionKey(int fKey)
    {
        if (_uiMode != UIMode.Browser) return false;
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
        _browserCursorIndex = 0;
        SyncBrowserSelection();
    }
    private void MoveBrowserCursorToBottom()
    {
        if (fileListView.Items.Count <= 0)
        {
            return;
        }
        _browserCursorIndex = fileListView.Items.Count - 1;
        SyncBrowserSelection();
    }
    /// <summary>
    /// Phase 3-input-viewer1: Viewer モード専用の KeyDown 処理を helper 化。
    /// 処理を行った（早期 return すべき）場合は true を返す。
    /// </summary>
    private bool TryHandleViewerKeyDown(KeyEventArgs e)
    {
        if (_uiMode != UIMode.Viewer) return false;
        // Ctrl+C: 表示中行または選択範囲コピー
        if (e.Control && e.KeyCode == Keys.C)
        {
            if (TryCopyLargeFileVisibleText())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            if (viewerTextBox.Visible && viewerTextBox.SelectionLength > 0)
            {
                viewerTextBox.Copy();
                ShowStatusMessage("選択範囲をコピーしました。");
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            // いずれにも該当しない場合はデフォルトのコピー動作を許容（または無視）するために
            // ここでは return true せず、TextBox 等へイベントを流す可能性を残すことも検討できるが、
            // 現在の契約に従い、ここで Handled にする。
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // Enter / Esc で Browser 復帰
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
        {
            if (TryExitViewerToBrowser())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
        }
        // L: エンコーディング切替
        if (e.KeyCode == Keys.L)
        {
            if (_viewerEncodingOverride == ViewerEncoding.Auto) _viewerEncodingOverride = ViewerEncoding.UTF8;
            else if (_viewerEncodingOverride == ViewerEncoding.UTF8) _viewerEncodingOverride = ViewerEncoding.SJIS;
            else _viewerEncodingOverride = ViewerEncoding.Auto;
            ApplyViewerStatusLine();
            // プレビューを再描画
            RequestPreviewRefresh(force: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // W: 折り返し切替
        if (e.KeyCode == Keys.W)
        {
            viewerTextBox.WordWrap = !viewerTextBox.WordWrap;
            viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
            // 設定の永続化
            _settings.Preview.ViewerWordWrap = viewerTextBox.WordWrap;
            SettingsManager.Save(_settings);
            ApplyViewerStatusLine();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // ラージファイル用全体ナビゲーション
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            var state = _largeFileState;
            int oldLine = state.FirstVisibleLine;
            int newLine = oldLine;
            if (e.KeyCode == Keys.Home)
            {
                newLine = 0;
            }
            else if (e.KeyCode == Keys.End)
            {
                if (state.IsIndexing)
                {
                    state.PendingEndAfterIndex = true;
                    ShowStatusMessage("インデックス完了後に末尾へ移動します...");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return true;
                }
                newLine = _largeFileControl.GetMaxFirstVisibleLine();
            }
            else if (e.KeyCode == Keys.PageUp)
            {
                newLine = oldLine - _largeFileControl.VisibleLineCount;
            }
            else if (e.KeyCode == Keys.PageDown)
            {
                newLine = oldLine + _largeFileControl.VisibleLineCount;
            }
            if (newLine != oldLine || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
            {
                _ = NavigateLargeFilePreviewAsync(newLine, e.KeyCode.ToString());
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
        }
        // ナビゲーションキー等は TextBox 側に通してスクロールを可能にする
        if (IsNavigationOrModifierKey(e.KeyCode))
        {
            return true; // 早期 return (Browser 用 KeyDown 処理へ流さない)
        }
        // それ以外はすべて抑止
        e.Handled = true;
        e.SuppressKeyPress = true;
        return true;
    }
    /// <summary>
    /// Phase 3-input-viewer1: Viewer モード専用の ProcessCmdKey 操作を helper 化。
    /// </summary>
    private bool TryHandleViewerCmdKey(Keys keyData)
    {
        if (_uiMode != UIMode.Viewer) return false;
        // Ctrl+F / F3 / Shift+F3: Viewer 検索ロジックへのルーティング
        if (keyData == (Keys.Control | Keys.F))
        {
            ExecuteViewerFind();
            return true;
        }
        if (keyData == (Keys.Control | Keys.A))
        {
            if (_currentViewerKind == PreviewKind.Text && viewerTextBox.Visible)
            {
                viewerTextBox.SelectAll();
                return true;
            }
        }
        if (keyData == Keys.F3)
        {
            ExecuteViewerFindNext(backward: false);
            return true;
        }
        if (keyData == (Keys.Shift | Keys.F3))
        {
            ExecuteViewerFindNext(backward: true);
            return true;
        }
        // Ctrl+C: ラージファイル表示中コピー
        if (keyData == (Keys.Control | Keys.C))
        {
            if (TryCopyLargeFileVisibleText())
            {
                return true;
            }
        }
        // Enter / Esc: Browser 復帰
        if (keyData == Keys.Enter || keyData == Keys.Escape)
        {
            if (TryExitViewerToBrowser())
            {
                return true;
            }
        }
        return false;
    }
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
                LogService.Warn(
                    $"[CancelProvenance] MidFD browser ESC exit confirm shown. " +
                    $"activeContext={HasActiveFileOperationCancelContext()}, busy={_isClipboardBusy}, " +
                    $"hasCts={_fileOpCts != null}, requested={_fileOpCts?.IsCancellationRequested ?? false}, " +
                    $"activeOperation={_activeFileOperationName ?? "<none>"}, shellProgress={_shellDeleteProgressFallback != null}, " +
                    $"undoRedoProgress={_undoRedoProgressFallback != null}, previewVisible={_previewPopupVisible}, " +
                    $"markedCount={_markedFiles.Count}, thread={Environment.CurrentManagedThreadId}");
                var result = MessageBox.Show("終了しますか？", "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                LogService.Warn(
                    $"[CancelProvenance] MidFD browser ESC exit confirm result. result={result}, " +
                    $"activeContext={HasActiveFileOperationCancelContext()}, busy={_isClipboardBusy}, " +
                    $"hasCts={_fileOpCts != null}, requested={_fileOpCts?.IsCancellationRequested ?? false}, " +
                    $"activeOperation={_activeFileOperationName ?? "<none>"}, thread={Environment.CurrentManagedThreadId}");
                if (result == DialogResult.Yes)
                {
                    _isClosingFromEscExitPath = true;
                    this.Close();
                }
                else
                {
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
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            _ = ExecutePack();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.U)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            _ = ExecuteUnpack();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // E キーで外部エディタ起動を復活。F4+Edit profile の場合も同様。
        if (e.KeyCode == Keys.E || (e.KeyCode == Keys.F4 && IsFunctionKeyAssignedToAction(4, FunctionKeyAction.Edit)))
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
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
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
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
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
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
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            OpenTerminalInCurrentDirectory(ShellKind.PowerShell);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.H && e.Modifiers == Keys.Shift)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            OpenTerminalInCurrentDirectory(ShellKind.CommandPrompt);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.X)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
            ExecuteShellDialog();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.A)
        {
            if (GuardClipboardBusy()) { e.Handled = true; return true; }
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
    private bool TryHandleBrowserCmdKeyMarking(Keys keyData)
    {
        // Tab を横取りし、コントロール間フォーカス移動を防ぐ (ToggleMark)
        if (keyData == Keys.Tab)
        {
            ToggleMark(moveNext: false);
            return true;
        }
        // Home: ファイルのみ全マーク / 全解除 (トグル)
        if (keyData == Keys.Home)
        {
            ToggleBulkMarks(includeDirectories: false);
            return true;
        }
        // Shift+Home: ファイルのみ反転
        if (keyData == (Keys.Shift | Keys.Home))
        {
            InvertBulkMarks(includeDirectories: false);
            return true;
        }
        // End: ファイル + ディレクトリを全マーク / 全解除 (トグル)
        if (keyData == Keys.End)
        {
            ToggleBulkMarks(includeDirectories: true);
            return true;
        }
        // Shift+End: ファイル + ディレクトリを反転
        if (keyData == (Keys.Shift | Keys.End))
        {
            InvertBulkMarks(includeDirectories: true);
            return true;
        }
        // Ctrl+A: ファイル + ディレクトリを全マーク
        if (keyData == (Keys.Control | Keys.A))
        {
            MarkBulk(includeDirectories: true);
            return true;
        }
        return false;
    }
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
        var paths = new List<string>(fileListView.Items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem item in fileListView.Items)
        {
            if (item.Text == ".." || item.Tag is not string path || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (!includeDirectories && !IsBrowserFileItem(item))
            {
                continue;
            }
            if (seen.Add(path))
            {
                paths.Add(path);
            }
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
        SetCountOnlyMarkSummaryCache();
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
            $"total={totalStopwatch.ElapsedMilliseconds}ms");
    }
    /// <summary>
    /// Phase 3-input-cmdkey-nav1: ProcessCmdKey における Browser 文脈のナビゲーション操作を helper 化。
    /// </summary>
    private bool TryHandleBrowserCmdKeyNavigation(Keys keyData)
    {
        // 履歴移動 (Alt 系) - リストの中身の有無にかかわらず動作
        if (keyData == (Keys.Alt | Keys.Left))
        {
            ExecuteHistoryBack();
            return true;
        }
        if (keyData == (Keys.Alt | Keys.Right))
        {
            ExecuteHistoryForward();
            return true;
        }
        int total = fileListView.Items.Count;
        if (total <= 0) return false;
        int itemsPerPage = GetBrowserItemsPerPage(out _, out int rowsPerColumn);
        bool moved = false;
        if (keyData == Keys.Up)
        {
            _browserCursorIndex = (_browserCursorIndex - 1 + total) % total;
            moved = true;
        }
        else if (keyData == Keys.Down)
        {
            _browserCursorIndex = (_browserCursorIndex + 1) % total;
            moved = true;
        }
        else if (keyData == Keys.Left)
        {
            _browserCursorIndex = Math.Max(0, _browserCursorIndex - rowsPerColumn);
            moved = true;
        }
        else if (keyData == Keys.Right)
        {
            _browserCursorIndex = Math.Min(total - 1, _browserCursorIndex + rowsPerColumn);
            moved = true;
        }
        else if (keyData == (Keys.Control | Keys.Home) || keyData == Keys.F11)
        {
            return ExecuteFunctionKey(11);
        }
        else if (keyData == (Keys.Control | Keys.End) || keyData == Keys.F12)
        {
            return ExecuteFunctionKey(12);
        }
        else if (keyData == (Keys.Control | Keys.Back))
        {
            if (GuardClipboardBusy()) return true;
            _previewPopup.Clear();
            _currentPreviewTarget = null;
            ExecuteHistoryBack();
            return true;
        }
        else if (keyData == Keys.PageUp)
        {
            if (_browserCursorIndex - itemsPerPage >= 0)
            {
                _browserCursorIndex -= itemsPerPage;
                moved = true;
            }
        }
        else if (keyData == Keys.PageDown)
        {
            if (_browserCursorIndex + itemsPerPage < total)
            {
                _browserCursorIndex += itemsPerPage;
                moved = true;
            }
        }
        if (moved)
        {
            InvalidateRecentMultiMarkIntent();
            SyncBrowserSelection();
            return true;
        }
        return false;
    }
    /// <summary>
    /// Phase 3-input-cmdkey-launch1: ProcessCmdKey における Browser 文脈のエピエイリアス系操作 (Fキー / Filter / 再読込) を helper 化。
    /// </summary>
    private bool TryHandleBrowserCmdKeyAliases(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.M))
        {
            if (GuardClipboardBusy()) return true;
            OpenMarkSlotDialog();
            return true;
        }
        if (keyData == (Keys.Control | Keys.R))
        {
            return ExecuteCurrentDirectoryReloadCommand();
        }
        if (keyData == (Keys.Control | Keys.F))
        {
            ExecuteFilter();
            return true;
        }
        if (keyData == Keys.F1) return ExecuteFunctionKey(1);
        if (keyData == Keys.F2) return ExecuteFunctionKey(2);
        if (keyData == Keys.F3) return ExecuteFunctionKey(3);
        if (keyData == Keys.F4) return ExecuteFunctionKey(4);
        if (keyData == Keys.F5) return ExecuteFunctionKey(5);
        if (keyData == Keys.F6) return ExecuteFunctionKey(6);
        if (keyData == Keys.F7) return ExecuteFunctionKey(7);
        if (keyData == Keys.F8) return ExecuteFunctionKey(8);
        if (keyData == Keys.F9) return ExecuteFunctionKey(9);
        if (keyData == Keys.F10) return ExecuteFunctionKey(10);
        // Shift+R: 再読込
        if (keyData == (Keys.Shift | Keys.R))
        {
            return ExecuteCurrentDirectoryReloadCommand();
        }
        return false;
    }
    /// <summary>
    /// Phase 3-input-cmdkey-launch1: ProcessCmdKey における Browser 文脈の起動系操作 (外部アプリ / プロパティ) を helper 化。
    /// </summary>
    private bool TryHandleBrowserCmdKeyLaunch(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter))
        {
            var item = GetCurrentBrowserItem();
            if (item != null && item.Text != "..")
            {
                string? fullPath = item.Tag as string;
                if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                {
                    var rawKind = PreviewService.GetPreviewKind(fullPath);
                    if (rawKind == PreviewKind.Video)
                    {
                        if (_settings.Preview?.VideoEnterPlaysExternal == true)
                        {
                            ExecuteBrowserOpenRequest(CreateBrowserOpenRequest(fullPath, allowExecuteTarget: true));
                        }
                        else
                        {
                            var launchResult = VideoPlaybackLaunchService.Launch(
                                fullPath,
                                _settings.Preview?.VideoToolDirectory,
                                _settings.Preview?.VideoPlaybackVolumePercent ?? 100,
                                0);
                            if (launchResult.Success)
                            {
                                if (launchResult.UsedFfplay)
                                {
                                    ShowStatusMessage($"ffplay.exeで外部再生しました。音量:{launchResult.AppliedVolumePercent}%");
                                }
                                else
                                {
                                    ShowStatusMessage("ffplay.exeが見つからないため、既定アプリで動画を開きました。");
                                }
                            }
                            else
                            {
                                MessageBox.Show(this, launchResult.ErrorMessage ?? "外部再生の起動に失敗しました。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                        return true;
                    }
                }
            }
            return false;
        }
        if (keyData == (Keys.Alt | Keys.F1))
        {
            if (GuardClipboardBusy()) return true;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, $"\"{_navigationService.CurrentPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogService.Error($"NewInstance 起動失敗: {ex.Message}");
            }
            return true;
        }
        if (keyData == Keys.Z)
        {
            ExecuteZLaunch();
            return true;
        }
        if (keyData == (Keys.Alt | Keys.F2))
        {
            if (GuardClipboardBusy()) return true;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_navigationService.CurrentPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogService.Error($"Explorer 起動失敗: {ex.Message}");
            }
            return true;
        }
        if (keyData == (Keys.Alt | Keys.F3))
        {
            if (GuardClipboardBusy()) return true;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogService.Error($"ControlPanel 起動失敗: {ex.Message}");
            }
            return true;
        }
        // Alt+Enter: プロパティ
        if (keyData == (Keys.Alt | Keys.Enter))
        {
            if (fileListView.Items.Count > 0)
            {
                ExecuteProperties(ResolveSelection());
                return true;
            }
        }
        return false;
    }
    /// <summary>
    /// Phase 3-input-cmdkey-clipui1: ProcessCmdKey における Browser 文脈のクリップボード操作 (Ctrl+C/X/V) を helper 化。
    /// </summary>
    private bool TryHandleBrowserCmdKeyClipboard(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C))
        {
            ExecuteClipboardCopy();
            return true;
        }
        if (keyData == (Keys.Control | Keys.X))
        {
            ExecuteClipboardCut();
            return true;
        }
        if (keyData == (Keys.Control | Keys.V))
        {
            ExecuteClipboardPaste();
            return true;
        }
        return false;
    }
    /// <summary>
    /// Phase 3-input-cmdkey-clipui1: ProcessCmdKey における Browser 文脈の列数設定 (1-9) を helper 化。
    /// </summary>
    private bool TryHandleBrowserCmdKeyColumnCount(Keys keyData)
    {
        int val = 0;
        if (keyData >= Keys.D1 && keyData <= Keys.D9) val = (int)(keyData - Keys.D0);
        else if (keyData >= Keys.NumPad1 && keyData <= Keys.NumPad9) val = (int)(keyData - Keys.NumPad0);
        if (val > 0)
        {
            _columnCount = val; // 1キー=1列 ... 9キー=9列
            _settings.Session.LastColumnCount = _columnCount;
            UpdateInfoPanel();
            browserPanel.Invalidate();
            CaptureActiveBrowserTabState();
            return true;
        }
        return false;
    }
    /// <summary>
    /// WinFD風の上部情報欄（Info行・Name行）を更新する。
    /// カーソル位置のアイテム情報とマーク/ファイル数を表示する。
    /// </summary>
    private void UpdateInfoPanel()
    {
        // 1. 表示項目の取得
        var currentItem = GetCurrentBrowserItem();
        int itemsPerPage = GetBrowserItemsPerPage(out _, out int rowsPerColumn);
        // 2. 状態を InputState にまとめる
        var state = new HeaderPresentationHelper.InputState
        {
            CurrentPath = _navigationService.CurrentPath,
            CursorIndex = _browserCursorIndex,
            ItemCount = fileListView.Items.Count,
            ItemsPerPage = itemsPerPage,
            RowsPerColumn = rowsPerColumn,
            ColumnCount = _columnCount,
            MarkedFiles = _markedFiles,
            CachedMarkSummary = GetMarkSummaryForHeader(),
            CurrentItemText = currentItem?.Text,
            CurrentItemPath = currentItem?.Tag as string,
            SortKind = _currentSort,
            SortAscending = _sortAscending,
            FilterPattern = _filterPattern,
            FilterLockSummary = TabFilterLockService.BuildSummary(GetActiveTabFilterLock()),
            ShowExtensions = _settings.Appearance?.ShowExtensions ?? true,
            ShowDirectoryMarker = _settings.Appearance?.ShowDirectoryMarker ?? true,
            ShowItemIcons = _settings.Appearance?.ShowItemIcons ?? true,
            DateFormat = _settings.Appearance?.DateFormat ?? "yyyy-MM-dd HH:mm",
            SizeFormat = _settings.Appearance?.SizeFormat ?? "HumanReadable"
        };
        // 3. 表示文字列の生成をヘルパーに委譲
        var display = HeaderPresentationHelper.Build(state);
        // 4. UI への適用
        lblPage.Text = display.Page;
        lblTotal.Text = display.Total;
        // 【Path行右端】 (lblSort): Mark優先 (Compact形式)、なければSort/Filter
        bool hasMarks = display.MarkCount > 0 && !string.IsNullOrWhiteSpace(display.MarkSizeText);
        int pathRightMaxWidth = Math.Min(
            Math.Max(220, infoRow2Panel.ClientSize.Width / 2),
            Math.Max(80, infoRow2Panel.ClientSize.Width - 80));
        string pathRightText = hasMarks
            ? FitMarkSummaryCompact(
                display.MarkCount,
                display.MarkSizeText,
                lblSort.Font,
                pathRightMaxWidth)
            : display.SortFilter;
        lblSort.Text = pathRightText;
        lblSort.Visible = !string.IsNullOrWhiteSpace(pathRightText);
        // 【Item行右端】 (lblFileStatsEx): Attr Timestamp (常に選択アイテムの情報)
        string itemRightText = display.ItemMetaWithoutSize;
        lblFileStatsEx.Text = itemRightText;
        lblFileStatsEx.Visible = !string.IsNullOrWhiteSpace(itemRightText);
        // 【Corrective】 右端ラベルの幅をテキストに合わせて調整
        int sortWidth = !string.IsNullOrWhiteSpace(pathRightText)
            ? Math.Min(MeasureHeaderTextWidth(pathRightText, lblSort.Font) + 12, pathRightMaxWidth)
            : 0;
        int metaWidth = !string.IsNullOrWhiteSpace(itemRightText)
            ? Math.Max(MeasureHeaderTextWidth(itemRightText, lblFileStatsEx.Font) + 12, 180)
            : 0;
        lblSort.Width = sortWidth;
        lblFileStatsEx.Width = metaWidth;
        // 【Corrective】 残り幅を計算し、左側テキストを手動で省略する
        int pathAvailableWidth = infoRow2Panel.ClientSize.Width - (lblSort.Visible ? lblSort.Width : 0) - 8;
        int nameAvailableWidth = infoRow4Panel.ClientSize.Width - (lblFileStatsEx.Visible ? lblFileStatsEx.Width : 0) - 8;
        // Path行左
        lblPath.Text = FitTextWithEllipsis(display.Path, lblPath.Font, pathAvailableWidth);
        // Item行左
        if (display.SelectedItemIsDirectory)
        {
            lblName.Text = FitDirectoryNameHeaderText(display.RawFileName, lblName.Font, nameAvailableWidth);
        }
        else
        {
            lblName.Text = FitFileNameWithSizePreservingExtension(
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
    }
    private string GetMarkSummaryForHeader()
    {
        if (_markedFiles.Count == 0)
        {
            _markSummaryCache = string.Empty;
            _markSummaryCacheCount = 0;
            _markSummaryCachePath = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
            _markSummaryDirty = false;
            return string.Empty;
        }
        string currentDir = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        if (!_markSummaryDirty
            && _markSummaryCacheCount == _markedFiles.Count
            && string.Equals(_markSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase))
        {
            return _markSummaryCache;
        }
        long totalSize = 0;
        int fileCount = 0;
        int outsideCurrentDirectoryCount = 0;
        foreach (string path in _markedFiles)
        {
            string? parentDir = Path.GetDirectoryName(path);
            if (!string.Equals(
                NavigationService.NormalizeDirectoryForCompare(parentDir ?? string.Empty),
                currentDir,
                StringComparison.OrdinalIgnoreCase))
            {
                outsideCurrentDirectoryCount++;
            }
            if (File.Exists(path))
            {
                try
                {
                    totalSize += new FileInfo(path).Length;
                    fileCount++;
                }
                catch
                {
                    // Mark summary は表示補助なので、アクセス不能な項目は集計から外す。
                }
            }
        }
        string outsideInfo = outsideCurrentDirectoryCount > 0 ? $" Out:{outsideCurrentDirectoryCount}" : "";
        _markSummaryCache = $"Mark:{_markedFiles.Count,3} ({fileCount} Files){outsideInfo} {FileOperationService.FormatSize(totalSize)}";
        _markSummaryCacheCount = _markedFiles.Count;
        _markSummaryCachePath = currentDir;
        _markSummaryDirty = false;
        return _markSummaryCache;
    }
    private void InvalidateMarkSummaryCache()
    {
        _markSummaryDirty = true;
    }
    private void SetCountOnlyMarkSummaryCache()
    {
        _markSummaryCache = _markedFiles.Count > 0
            ? $"Mark:{_markedFiles.Count,3}"
            : string.Empty;
        _markSummaryCacheCount = _markedFiles.Count;
        _markSummaryCachePath = NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath);
        _markSummaryDirty = false;
    }
    private void ApplyMarkColor(ListViewItem item, string fullPath)
    {
        // Phase 2g-fix6.4b: 文字列への '*' 挿入を廃止。描画スロット方式へ移行
        // ここではファイル種別に応じた基本色の再設定のみを行う
        bool isDir = IsDirectoryListItem(item, fullPath);
        if (TryGetAttributesForColor(item, fullPath, out FileAttributes attrs))
        {
            item.ForeColor = ResolveAttributeColor(attrs, isDir);
        }
        else
        {
            item.ForeColor = isDir ? MidFDColors.ListDirectoryFore : MidFDColors.ListFileFore;
        }
        // 背景色は常に通常色 (Black) を維持。マーク背景塗りは BrowserPanel_Paint 側の
        // 選択状態との組み合わせで処理される。
        item.BackColor = MidFDColors.ListNormalBack;
    }
    private static Color ResolveAttributeColor(FileAttributes attrs, bool isDirectory)
    {
        if (attrs.HasFlag(FileAttributes.System))
            return MidFDColors.ListSystemFore;
        if (attrs.HasFlag(FileAttributes.Hidden))
            return MidFDColors.ListHiddenFore;
        if (attrs.HasFlag(FileAttributes.ReadOnly))
            return MidFDColors.ListReadOnlyFore;
        return isDirectory ? MidFDColors.ListDirectoryFore : MidFDColors.ListFileFore;
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
        if (_browserCursorIndex >= 0 && _browserCursorIndex < fileListView.Items.Count)
        {
            return fileListView.Items[_browserCursorIndex];
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
        bool selected = e.Item.Selected;
        Color bg = selected ? MidFDColors.ListSelectedBack : MidFDColors.ListNormalBack;
        Color fg = e.Item.ForeColor;
        // マークされた行は元色を優先（BackColorがシアンならそのまま）
        if (e.Item.BackColor == MidFDColors.ListMarkedBack)
        {
            bg = MidFDColors.ListMarkedBack;
            fg = MidFDColors.ListMarkedFore;
        }
        else if (selected)
        {
            // 局面選択中は文字を白っぽく
            fg = MidFDColors.ListSelectedFore;
        }
        using var bgBrush = new SolidBrush(bg);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);
        Font font = e.Item.ListView?.Font ?? SystemFonts.DefaultFont;
        bool isMarked = e.Item.Tag is string fullPath && _markedFiles.Contains(fullPath);
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
            TextRenderer.DrawText(
                e.Graphics,
                "*",
                font,
                markRect,
                GetCurrentThemeMarkGlyphColor(),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem?.Text ?? "",
            font,
            textBounds,
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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
        Graphics g = e.Graphics;
        g.Clear(browserPanel.BackColor);
        if (fileListView.Items.Count == 0)
        {
            DrawCommandHintOverlay(g);
            return;
        }
        int totalItems = fileListView.Items.Count;
        Font font = browserPanel.Font;
        int itemsPerPage = GetBrowserItemsPerPage(out int itemHeight, out int rowsPerColumn);
        // 列幅の計算
        int colWidth = browserPanel.Width / _columnCount;
        // 現在のページをカーソル位置から計算
        int currentPage = _browserCursorIndex / itemsPerPage;
        int startIndex = currentPage * itemsPerPage;
        int endIndex = Math.Min(startIndex + itemsPerPage, totalItems);
        // ページ内のアイテムを描画
        for (int i = startIndex; i < endIndex; i++)
        {
            int pageIndex = i - startIndex;
            int col = pageIndex / rowsPerColumn;
            int row = pageIndex % rowsPerColumn;
            int x = col * colWidth + 5;
            int y = row * itemHeight + 5;
            var item = fileListView.Items[i];
            bool isSelected = (i == _browserCursorIndex);
            // 描画領域の矩形
            Rectangle rect = new Rectangle(x, y, colWidth - 10, itemHeight);
            // 描画設定の決定
            Color bg = MidFDColors.ListNormalBack;
            Color fg = item.ForeColor;
            // item.Tag にフルパスが入っている前提でマーク状態を判定 (文字列依存からの脱却)
            bool isMarked = _markedFiles.Contains(item.Tag as string ?? string.Empty);
            if (isSelected)
            {
                // 選択中：マークの有無で背景色を微調整
                bg = isMarked ? MidFDColors.ListSelectedMarkedBack : MidFDColors.ListSelectedBack;
            }
            // 背景描画
            using (SolidBrush bgBrush = new SolidBrush(bg))
            {
                g.FillRectangle(bgBrush, rect);
            }
            // テキスト描画 (WinFD寄せ: Mark Slot を導入し、* とファイル名を分離)
            int markSlotWidth = 15;
            Rectangle markRect = new Rectangle(rect.X, rect.Y, markSlotWidth, rect.Height);
            int iconSlotWidth = (_settings.Appearance?.ShowItemIcons ?? true) ? 18 : 0;
            Rectangle iconRect = new Rectangle(rect.X + markSlotWidth, rect.Y + Math.Max(0, (rect.Height - 16) / 2), 16, 16);
            Rectangle textRect = new Rectangle(rect.X + markSlotWidth + iconSlotWidth, rect.Y, rect.Width - markSlotWidth - iconSlotWidth, rect.Height);
            if (isMarked)
            {
                TextRenderer.DrawText(g, "*", font, markRect, GetCurrentThemeMarkGlyphColor(), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            if ((_settings.Appearance?.ShowItemIcons ?? true) && textRect.Width > 24)
            {
                DrawBrowserItemIcon(g, item, iconRect);
            }
            string text = BuildBrowserDisplayText(item, textRect.Width, font, g);
            TextRenderer.DrawText(g, text, font, textRect, fg, Color.Transparent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        DrawCommandHintOverlay(g);
    }
    private Color GetCurrentThemeMarkGlyphColor()
    {
        return _settings.Appearance?.ColorTheme == "Light"
            ? Color.Black
            : Color.White;
    }
    private void DrawBrowserItemIcon(Graphics g, ListViewItem item, Rectangle iconRect)
    {
        try
        {
            string? fullPath = item.Tag as string;
            bool isDirectory = IsDirectoryListItem(item, fullPath);
            using var icon = (Icon)BrowserItemIconProvider.GetSmallIcon(fullPath, isDirectory).Clone();
            g.DrawIcon(icon, iconRect);
        }
        catch
        {
            // アイコン取得失敗時は一覧描画を優先して無視する
        }
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
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix).Width;
    }
    private void BrowserPanel_Resize(object? sender, EventArgs e)
    {
        if (_uiMode == UIMode.Browser)
        {
            UpdateInfoPanel();
            browserPanel.Invalidate();
        }
    }
    /// <summary>
    /// カスタムカーソル位置(_browserCursorIndex)を裏側のListViewに同期し、画面再描画とInfoPanel更新を行う。
    /// </summary>
    private void SyncBrowserSelection()
    {
        if (fileListView.Items.Count == 0 || _browserCursorIndex < 0 || _browserCursorIndex >= fileListView.Items.Count)
            return;
        // 裏側のListViewの状態をリセットして再設定
        fileListView.SelectedItems.Clear();
        var item = fileListView.Items[_browserCursorIndex];
        item.Selected = true;
        item.Focused = true;
        item.EnsureVisible();
        // プレビューと上部情報欄の更新を発火
        FileListView_SelectedIndexChanged(this, EventArgs.Empty);
        // UI描画更新
        browserPanel.Invalidate();
        CaptureActiveBrowserTabState();
    }
    // ─── Phase 3-fix1c: マウス基本操作（単クリック/ダブルクリック） ───
    private ContextMenuStrip? _browserContextMenu;
    private string? _browserContextPath;
    private string? _browserContextItemName;
    private void BrowserPanel_MouseClick(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        ClearPendingEscExitMarkPersistence();
        int newIndex = CalculateBrowserIndexFromPoint(e.X, e.Y);
        if (e.Button == MouseButtons.Left)
        {
            if (newIndex >= 0 && newIndex < fileListView.Items.Count)
            {
                _browserCursorIndex = newIndex;
                SyncBrowserSelection();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            if (TryConsumeBrowserContextMenuSuppress()) return;
            if (newIndex >= 0 && newIndex < fileListView.Items.Count)
            {
                var item = fileListView.Items[newIndex];
                if (item.Text == "..") return; // 空白や .. では何もしない
                string? fullPath = item.Tag as string;
                if (string.IsNullOrEmpty(fullPath)) return;
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath)) return;
                // 右クリック項目が未マークなら、既存マークを解除しクリック項目を対象化
                if (!_markedFiles.Contains(fullPath))
                {
                    ClearMarks();
                    _browserCursorIndex = newIndex;
                    SyncBrowserSelection();
                }
                _browserContextPath = fullPath;
                _browserContextItemName = item.Text;
                ShowBrowserContextMenu(e.Location);
            }
        }
    }
    private void ShowBrowserContextMenu(Point location)
    {
        if (TryConsumeBrowserContextMenuSuppress())
        {
            return;
        }
        if (_browserContextMenu == null)
        {
            _browserContextMenu = new ContextMenuStrip();
            _browserContextMenu.Opening += BrowserContextMenu_Opening;
        }
        else
        {
            var oldItems = _browserContextMenu.Items.Cast<ToolStripItem>().ToArray();
            _browserContextMenu.Items.Clear();
            foreach (var item in oldItems)
            {
                item.Dispose();
            }
        }
        var res = SelectionResolver.Resolve(_markedFiles, fileListView.Items.Count > 0 && _browserCursorIndex >= 0 ? fileListView.Items[_browserCursorIndex] : null);
        bool canOpenInNewTab = !string.IsNullOrWhiteSpace(_browserContextPath)
            && !string.Equals(_browserContextItemName, "..", StringComparison.Ordinal)
            && Directory.Exists(_browserContextPath);
        // 1. 開く
        var openItem = new ToolStripMenuItem("開く(&O)", null, (s, e) => ExecuteDefaultOpen());
        _browserContextMenu.Items.Add(openItem);
        var openInNewTabItem = new ToolStripMenuItem("新しいタブで開く(&T)", null, (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(_browserContextPath))
            {
                CreateNewBrowserTab(_browserContextPath);
            }
        })
        {
            Enabled = canOpenInNewTab
        };
        _browserContextMenu.Items.Add(openInNewTabItem);
        // 2. 7-Zip > (または直接の圧縮・解凍)
        bool isReadOnly = IsActiveBrowserTabReadOnly();
        var sevenZipMenu = Create7ZipMenu(res);
        if (sevenZipMenu != null)
        {
            _browserContextMenu.Items.Add(sevenZipMenu);
        }
        else
        {
            // 7-Zip サブメニューがない場合は直接配置
            var packItem = new ToolStripMenuItem("圧縮...", null, async (s, e) => await ExecutePack())
            {
                Enabled = !isReadOnly && res.Count > 0
            };
            _browserContextMenu.Items.Add(packItem);
            var unpackItem = new ToolStripMenuItem("解凍...", null, async (s, e) => await ExecuteUnpack())
            {
                Enabled = !isReadOnly && res.Count > 0 && res.FullPaths.Any(IsArchiveTarget)
            };
            _browserContextMenu.Items.Add(unpackItem);
            var packEachFolderItem = new ToolStripMenuItem("個別圧縮...", null, async (s, e) =>
            {
                await ExecutePack(forcePackEachFolderIndividually: true);
            })
            {
                Enabled = !isReadOnly && CanPackEachFolderIndividually(res)
            };
            _browserContextMenu.Items.Add(packEachFolderItem);
        }
        // 3. プログラムから開く >
        var openWithItem = new ToolStripMenuItem("プログラムから開く(&H)...", null, (s, e) => ExecuteOpenWith(res));
        _browserContextMenu.Items.Add(openWithItem);
        // 4. パスをコピー
        var copyPathItem = new ToolStripMenuItem("パスをコピー(&P)", null, (s, e) =>
        {
            if (res.FullPaths.Any())
            {
                string paths = string.Join(Environment.NewLine, res.FullPaths);
                Clipboard.SetText(paths);
                ShowStatusMessage($"{res.FullPaths.Count} 件のパスをクリップボードにコピーしました。");
            }
        });
        _browserContextMenu.Items.Add(copyPathItem);
        // SVGをコピー
        bool isSingleSvg = res.FullPaths.Count == 1 &&
                           (string.Equals(Path.GetExtension(res.FullPaths[0]), ".svg", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetExtension(res.FullPaths[0]), ".svgz", StringComparison.OrdinalIgnoreCase));
        if (isSingleSvg && _featureGate.IsEnabled(FeatureId.SvgClipboard))
        {
            var copySvgItem = new ToolStripMenuItem("SVGをコピー(&G)", null, (s, e) =>
            {
                if (SvgClipboardExportService.CopyToClipboard(res.FullPaths[0]))
                {
                    ShowStatusMessage("SVGをクリップボードにコピーしました。");
                }
                else
                {
                    ShowStatusMessage("SVGのコピーに失敗しました。");
                }
            });
            _browserContextMenu.Items.Add(copySvgItem);
        }
        _browserContextMenu.Items.Add(new ToolStripSeparator());
        // 5. 送る >
        var sendToMenu = new ToolStripMenuItem("送る(&N)");
        PopulateSendToMenu(sendToMenu);
        _browserContextMenu.Items.Add(sendToMenu);
        _browserContextMenu.Items.Add(new ToolStripSeparator());
        // 6. 切り取り / コピー / 貼り付け
        var cutItem = new ToolStripMenuItem("切り取り(&T)", null, (s, e) => ExecuteClipboardCut());
        var copyOpItem = new ToolStripMenuItem("コピー(&C)", null, (s, e) => ExecuteClipboardCopy());
        var pasteItem = new ToolStripMenuItem("貼り付け(&P)", null, (s, e) => ExecuteClipboardPaste());
        // Phase 3-clipboard1.3: 事前判定による Enabled 切替
        pasteItem.Enabled = !_isClipboardBusy && (ShellClipboardService.HasFileDrop() || ShellClipboardService.HasImage());
        _browserContextMenu.Items.Add(cutItem);
        _browserContextMenu.Items.Add(copyOpItem);
        _browserContextMenu.Items.Add(pasteItem);
        _browserContextMenu.Items.Add(new ToolStripSeparator());
        // PowerShell / コマンドプロンプト (直置き)
        _browserContextMenu.Items.Add(new ToolStripMenuItem("PowerShellをここで開く(&P)", null, (s, e) =>
            OpenTerminalInCurrentDirectory(ShellKind.PowerShell)));
        _browserContextMenu.Items.Add(new ToolStripMenuItem("コマンドプロンプトをここで開く(&C)", null, (s, e) =>
            OpenTerminalInCurrentDirectory(ShellKind.CommandPrompt)));
        _browserContextMenu.Items.Add(new ToolStripSeparator());
        // 7. プロパティ
        var propItem = new ToolStripMenuItem("プロパティ(&R)", null, (s, e) => ExecuteProperties(res));
        _browserContextMenu.Items.Add(propItem);
        _browserContextMenu.Show(browserPanel, location);
    }
    private void BrowserContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (TryConsumeBrowserContextMenuSuppress())
        {
            e.Cancel = true;
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
            await ExecutePack(forcePackEachFolderIndividually: true);
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
    private void PopulateSendToMenu(ToolStripMenuItem sendToMenu)
    {
        string sendToPath = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        if (!Directory.Exists(sendToPath)) return;
        try
        {
            var files = Directory.GetFiles(sendToPath);
            foreach (var file in files)
            {
                var attr = File.GetAttributes(file);
                if (attr.HasFlag(FileAttributes.Hidden)) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("圧縮") || name.Contains("Pack"))
                {
                    // 標準の圧縮機能やサブメニューと混同・重複するのを防ぐため、送るメニューからは除外する
                    continue;
                }
                var item = new ToolStripMenuItem(name, null, (s, e) => ExecuteSendTo(file));
                sendToMenu.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"SendTo 列挙失敗: {ex.Message}");
            var errorItem = new ToolStripMenuItem("(列挙失敗)");
            errorItem.Enabled = false;
            sendToMenu.DropDownItems.Add(errorItem);
        }
    }
    private void ExecuteSendTo(string targetExeOrShortcut)
    {
        var res = SelectionResolver.Resolve(_markedFiles, fileListView.Items.Count > 0 && _browserCursorIndex >= 0 ? fileListView.Items[_browserCursorIndex] : null);
        if (!res.FullPaths.Any()) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = targetExeOrShortcut;
            psi.UseShellExecute = true;
            var sb = new System.Text.StringBuilder();
            foreach (var path in res.FullPaths)
            {
                sb.Append($"\"{path}\" ");
            }
            psi.Arguments = sb.ToString().TrimEnd();
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            LogService.Error($"SendTo 実行失敗: {ex.Message}");
            ShowStatusMessage($"送る操作に失敗しました: {ex.Message}");
        }
    }
    private void BrowserPanel_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        if (e.Button != MouseButtons.Left) return;
        int newIndex = CalculateBrowserIndexFromPoint(e.X, e.Y);
        if (newIndex >= 0 && newIndex < fileListView.Items.Count)
        {
            _browserCursorIndex = newIndex;
            SyncBrowserSelection();
            ExecuteDefaultOpen(); // ダブルクリック専用（既定アプリ等）へ流す
        }
    }
    private void BrowserPanel_MouseWheel(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser || fileListView.Items.Count == 0) return;
        int itemsPerPage = GetBrowserItemsPerPage();
        if (itemsPerPage <= 0) return;
        int totalItems = fileListView.Items.Count;
        int currentPage = _browserCursorIndex / itemsPerPage;
        int offsetInPage = _browserCursorIndex % itemsPerPage;
        int totalPages = (totalItems + itemsPerPage - 1) / itemsPerPage;
        if (e.Delta > 0) // 上ホイール: 前ページへ
        {
            if (currentPage <= 0) return; // 境界 no-op
            int targetPage = currentPage - 1;
            int targetIndex = targetPage * itemsPerPage + offsetInPage;
            _browserCursorIndex = Math.Min(totalItems - 1, targetIndex);
            SyncBrowserSelection();
        }
        else if (e.Delta < 0) // 下ホイール: 次ページへ
        {
            if (currentPage >= totalPages - 1) return; // 境界 no-op
            int targetPage = currentPage + 1;
            int targetIndex = targetPage * itemsPerPage + offsetInPage;
            _browserCursorIndex = Math.Min(totalItems - 1, targetIndex);
            SyncBrowserSelection();
        }
    }
    // ─── Phase 3-fix2a: 外部 → MidFD Drag-in ───
    private void BrowserPanel_DragEnter(object? sender, DragEventArgs e)
    {
        if (_uiMode != UIMode.Browser || IsActiveBrowserTabReadOnly())
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // Phase 3-keybind-cleanup1.3: Clipboard処理中は受容しない
            if (_isClipboardBusy)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = DragDropEffects.Copy;
        }
        else if (BrowserImageDropService.HasImageData(e.Data)
            || BrowserDropUrlResolverService.HasPotentialUrlData(e.Data))
        {
            if (_isClipboardBusy)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = DragDropEffects.Copy;
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }
    private void BrowserPanel_DragDrop(object? sender, DragEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        if (GuardReadOnlyBrowserTab("ファイル取り込み")) return;
        if (_isClipboardBusy)
        {
            ShowStatusMessage("処理中のため画像取り込みできません。");
            return;
        }
        if (string.IsNullOrEmpty(_navigationService.CurrentPath)) return;
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            string msg = $"{files.Length} 件の項目を現在のディレクトリにコピーしますか？\n宛先: {_navigationService.CurrentPath}";
            var result = MessageBox.Show(msg, "Drag-in (Copy)", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes)
            {
                ShowStatusMessage("コピーはキャンセルされました。");
                return;
            }
            int successCount = 0;
            foreach (var sourcePath in files)
            {
                string fileName = Path.GetFileName(sourcePath);
                string destPath = Path.Combine(_navigationService.CurrentPath, fileName);
                bool sourceIsDir = Directory.Exists(sourcePath);
                bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
                if (destExists)
                {
                    bool destIsDir = Directory.Exists(destPath);
                    if (sourceIsDir != destIsDir)
                    {
                        MessageBox.Show($"型が異なるため上書きできません。\n宛先: {destPath}", "上書きエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        continue;
                    }
                    if (sourceIsDir)
                    {
                        MessageBox.Show($"フォルダ同士の上書き（統合）は現在未対応です。\nスキップします: {fileName}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        continue;
                    }
                    var overwriteMsg = FileOperationPresentationHelper.GetOverwriteConfirmationMessage(fileName);
                    var overwriteResult = MessageBox.Show(overwriteMsg, "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (overwriteResult == DialogResult.Cancel) break;
                    if (overwriteResult == DialogResult.No) continue;
                }
                try
                {
                    FileOperationService.Copy(sourcePath, destPath);
                    successCount++;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"コピー失敗: {fileName}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
                }
            }
            LoadDirectory(_navigationService.CurrentPath);
            ShowStatusMessage($"{successCount} 件の項目をドロップコピーしました。");
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
    }
    // ─── Phase 3-fix2b: MidFD → 外部 Drag-out (Copy限定) ───
    private void BrowserPanel_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        if (e.Button == MouseButtons.Right)
        {
            if (_settings.Input?.EnableMouseGestures == true)
            {
                _mouseGestureRecognizer.Begin(e.Location);
            }
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        // ドラッグ開始の「候補」座標とインデックスを保持
        _dragStartPoint = e.Location;
        _dragCandidateIndex = CalculateBrowserIndexFromPoint(e.X, e.Y);
        if (_dragCandidateIndex >= 0 && _dragCandidateIndex < fileListView.Items.Count && _browserCursorIndex != _dragCandidateIndex)
        {
            InvalidateRecentMultiMarkIntent();
            _browserCursorIndex = _dragCandidateIndex;
            SyncBrowserSelection();
        }
    }
    private void BrowserTabStrip_TabReordered(object? sender, BrowserTabStripReorderEventArgs e)
    {
        if (e.FromIndex < 0 || e.FromIndex >= _browserTabs.Count || e.ToIndex < 0 || e.ToIndex >= _browserTabs.Count || e.FromIndex == e.ToIndex)
        {
            return;
        }
        CaptureActiveBrowserTabState();
        BrowserTabState movedTab = _browserTabs[e.FromIndex];
        BrowserTabState? activeTab = _activeBrowserTabIndex >= 0 && _activeBrowserTabIndex < _browserTabs.Count
            ? _browserTabs[_activeBrowserTabIndex]
            : null;
        BrowserTabState? contextTab = _browserTabContextIndex >= 0 && _browserTabContextIndex < _browserTabs.Count
            ? _browserTabs[_browserTabContextIndex]
            : null;
        _browserTabs.RemoveAt(e.FromIndex);
        _browserTabs.Insert(e.ToIndex, movedTab);
        if (activeTab != null)
        {
            _activeBrowserTabIndex = _browserTabs.IndexOf(activeTab);
        }
        else
        {
            _activeBrowserTabIndex = Math.Clamp(e.ToIndex, 0, _browserTabs.Count - 1);
        }
        if (contextTab != null)
        {
            _browserTabContextIndex = _browserTabs.IndexOf(contextTab);
        }
        RefreshBrowserTabHeaders();
        browserPanel.Focus();
        ShowStatusMessage("タブ順を入れ替えました。");
    }
    private void BrowserPanel_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_uiMode != UIMode.Browser) return;
        if (e.Button == MouseButtons.Right && _mouseGestureRecognizer.IsTracking)
        {
            _mouseGestureRecognizer.Update(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left || _dragStartPoint == Point.Empty || _dragCandidateIndex == -1) return;
        // OS標準のドラッグ開始しきい値判定 (SystemInformation.DragSize)
        bool exceeded = Math.Abs(e.X - _dragStartPoint.X) > SystemInformation.DragSize.Width ||
                        Math.Abs(e.Y - _dragStartPoint.Y) > SystemInformation.DragSize.Height;
        if (exceeded)
        {
            // ドラッグ対象の確定
            List<string> dragPaths = new List<string>();
            string? dragCandidatePath = (_dragCandidateIndex >= 0 && _dragCandidateIndex < fileListView.Items.Count)
                ? fileListView.Items[_dragCandidateIndex].Tag as string
                : null;
            // 1. 複数 mark 中に未mark の current row をつかんだ場合は、その行だけを優先する
            if (!string.IsNullOrWhiteSpace(dragCandidatePath)
                && _markedFiles.Count > 1
                && !_markedFiles.Contains(dragCandidatePath)
                && (File.Exists(dragCandidatePath) || Directory.Exists(dragCandidatePath)))
            {
                dragPaths.Add(dragCandidatePath);
            }
            // 2. mark 済み行または単体 mark は従来どおり mark 集合優先
            else if (_markedFiles.Count > 0)
            {
                foreach (var path in _markedFiles)
                {
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        dragPaths.Add(path);
                    }
                }
            }
            // 3. マークなし時は直下のカーソル候補
            else if (_dragCandidateIndex >= 0 && _dragCandidateIndex < fileListView.Items.Count)
            {
                if (_browserCursorIndex != _dragCandidateIndex)
                {
                    _browserCursorIndex = _dragCandidateIndex;
                    SyncBrowserSelection();
                }
                var item = fileListView.Items[_dragCandidateIndex];
                string name = item.Text;
                string? fullPath = item.Tag as string;
                // 親ディレクトリ(..)や無効なパスは除外
                if (name != ".." && !string.IsNullOrEmpty(fullPath))
                {
                    if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    {
                        dragPaths.Add(fullPath);
                    }
                }
            }
            if (dragPaths.Count > 0)
            {
                // Phase 3-keybind-cleanup1.3: Clipboard処理中は開始しない
                if (_isClipboardBusy) return;
                // ドラッグ開始
                var data = new DataObject(DataFormats.FileDrop, dragPaths.ToArray());
                browserPanel.DoDragDrop(data, DragDropEffects.Copy);
            }
            // 開始した（または条件に合わず開始できなかった）ので状態をクリア
            _dragStartPoint = Point.Empty;
            _dragCandidateIndex = -1;
        }
    }
    private void BrowserPanel_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && _mouseGestureRecognizer.IsTracking)
        {
            string gesture = _mouseGestureRecognizer.End(e.Location);
            if (!string.IsNullOrEmpty(gesture) && TryExecuteBrowserMouseGesture(gesture))
            {
                SuppressNextBrowserContextMenu();
                return;
            }
        }
        // ボタンを離した時点で候補をリセット（クリックとして成立したか、ドラッグせずに離した）
        _dragStartPoint = Point.Empty;
        _dragCandidateIndex = -1;
    }
    private void SuppressNextBrowserContextMenu()
    {
        _suppressNextBrowserContextMenu = true;
        _suppressBrowserContextMenuUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
    }
    private bool TryConsumeBrowserContextMenuSuppress()
    {
        if (!_suppressNextBrowserContextMenu && DateTime.UtcNow > _suppressBrowserContextMenuUntilUtc)
        {
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
        switch (gesture)
        {
            case "L":
                ShowStatusMessage("Gesture: 戻る");
                ExecuteHistoryBack();
                return true;
            case "R":
                ShowStatusMessage("Gesture: 進む");
                ExecuteHistoryForward();
                return true;
            case "U":
                ShowStatusMessage("Gesture: 親ディレクトリへ移動");
                ExecuteBackspace();
                return true;
            case "UD":
                ReloadCurrentDirectory("Gesture: 再読込");
                return true;
            case "RU":
                ShowStatusMessage("Gesture: 右タブへ移動");
                SelectAdjacentBrowserTab(+1);
                return true;
            case "LU":
                ShowStatusMessage("Gesture: 左タブへ移動");
                SelectAdjacentBrowserTab(-1);
                return true;
            case "UR":
                ShowStatusMessage("Gesture: 右カテゴリへ移動");
                SelectAdjacentBrowserTabCategory(+1);
                return true;
            case "UL":
                ShowStatusMessage("Gesture: 左カテゴリへ移動");
                SelectAdjacentBrowserTabCategory(-1);
                return true;
            case "DR":
                ShowStatusMessage("Gesture: 現在タブを閉じる");
                CloseCurrentBrowserTab();
                return true;
            case "LR":
                RestoreLastClosedBrowserTab();
                return true;
            default:
                ShowStatusMessage($"Gesture: 未割り当て ({gesture})");
                return true;
        }
    }
    private void PushClosedBrowserTabSnapshot(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabs.Count)
        {
            return;
        }
        _closedBrowserTabs.Add(new ClosedBrowserTabSnapshot
        {
            CategoryId = _activeBrowserTabCategoryId,
            TabState = _browserTabs[tabIndex].Clone()
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
        string targetCategoryId = _browserTabCategories.Any(category => string.Equals(category.Id, snapshot.CategoryId, StringComparison.OrdinalIgnoreCase))
            ? snapshot.CategoryId
            : _activeBrowserTabCategoryId;
        if (!string.Equals(targetCategoryId, _activeBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            SwitchBrowserTabCategory(targetCategoryId);
        }
        int maxTabCount = GetMaxBrowserTabsPerCategory();
        if (_browserTabs.Count >= maxTabCount)
        {
            ShowStatusMessage($"タブは最大{maxTabCount}個までです。");
            _browserTabStrip?.FlashLimitReached();
            TryPlayBrowserTabLimitBeep();
            return;
        }
        _closedBrowserTabs.RemoveAt(_closedBrowserTabs.Count - 1);
        BrowserTabState restored = snapshot.TabState.Clone();
        _browserTabs.Add(restored);
        RefreshBrowserTabHeaders();
        _activeBrowserTabIndex = -1;
        SwitchBrowserTab(_browserTabs.Count - 1);
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
        return _columnCount * rowsPerColumn;
    }
    private int CalculateBrowserIndexFromPoint(int x, int y)
    {
        if (fileListView.Items.Count == 0) return -1;
        int itemsPerPage = GetBrowserItemsPerPage(out int itemHeight, out int rowsPerColumn);
        int colWidth = browserPanel.Width / _columnCount;
        int targetCol = x / colWidth;
        int targetRow = y / itemHeight;
        // 論理的な行・列の範囲外なら無効
        if (targetCol < 0 || targetCol >= _columnCount || targetRow < 0 || targetRow >= rowsPerColumn)
            return -1;
        int pageIndex = targetCol * rowsPerColumn + targetRow;
        int currentPage = _browserCursorIndex / itemsPerPage;
        return (currentPage * itemsPerPage) + pageIndex;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode == Keys.Escape)
        {
            LogService.Info(
                $"[CancelRuntime] MainForm.ProcessCmdKey Escape. busy={_isClipboardBusy}, " +
                $"hasCts={_fileOpCts != null}, requested={_fileOpCts?.IsCancellationRequested ?? false}, " +
                $"activeControl={DescribeControl(ActiveControl)}, thread={Environment.CurrentManagedThreadId}");
        }
        if (keyCode == Keys.Escape && TryRouteActiveFileOperationCancel("MainForm.ProcessCmdKey"))
        {
            return true;
        }
        if (IsCommandLauncherShortcut(keyData))
        {
            OpenCommandPalette();
            return true;
        }
        if (_viewerInputRouter.TryHandleCmdKey(CreateViewerCmdKeyContext(), keyData)) return true;
        if (_browserInputRouter.TryHandleCmdKey(CreateBrowserCmdKeyContext(), keyData)) return true;
        if (keyData == (Keys.Control | Keys.Shift | Keys.L))
        {
            if (_uiMode == UIMode.Browser && !IsCurrentDirectoryBusy())
            {
                OpenActiveTabFilterLockDialog();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
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
    private void OpenCommandPalette()
    {
        if (_uiMode != UIMode.Browser)
        {
            ShowStatusMessage("Command Palette は Browser モードでのみ使用できます。");
            return;
        }
        var commands = Services.CommandPaletteService.GetAllCommands(this, _featureGate);
        bool allowUsage = _featureGate.IsEnabled(FeatureId.CommandPaletteUsage);
        var usageState = allowUsage
            ? Services.CommandPaletteUsageStorage.Load()
            : new CommandPaletteUsageState();
        using var dialog = new Dialogs.CommandPaletteDialog(
            commands,
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
    // Bridge methods for CommandPalette
    internal void InvokeReloadCurrentDirectory() => ReloadCurrentDirectory("コマンドパレットから再読込しました。");
    internal void InvokeCopyCurrentDirectory() => CopyCurrentDirectoryFromHeader();
    internal void InvokeCopySelectedItemFullPath() => CopySelectedItemFullPathFromHeader();
    internal void InvokeOpenSettingsForm() => OpenSettingsForm();
    internal void InvokeOpenMarkSlotDialog() => OpenMarkSlotDialog();
    internal void InvokeOpenWorkspaceSnapshotDialog() => OpenWorkspaceSnapshotDialog();
    internal void InvokeLaunchExternalTool(ExternalToolCommandDefinition definition)
    {
        var context = GetExternalToolExecutionContext();
        bool usesMarkedPaths =
            ExternalToolArgumentTemplateService.UsesMarkedPathTemplate(definition.Arguments)
            || ExternalToolArgumentTemplateService.UsesMarkedPathTemplate(definition.WorkingDirectory);
        if (usesMarkedPaths && context.MarkedPaths.Count == 0)
        {
            var result = MessageBox.Show(
                this,
                "この外部ツールはマーク済みパス用テンプレートを使用しますが、現在マークは0件です。\n空のマーク一覧で起動しますか？",
                "外部ツール起動確認",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (result != DialogResult.Yes)
            {
                ShowStatusMessage("外部ツールの起動をキャンセルしました。");
                return;
            }
        }
        string? error = ExternalToolLauncherService.Launch(definition, context);
        if (error != null)
        {
            MessageBox.Show(this, error, "外部ツール起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            ShowStatusMessage($"外部ツールを起動しました: {definition.DisplayName}");
        }
    }
    private ExternalToolExecutionContext GetExternalToolExecutionContext()
    {
        return new ExternalToolExecutionContext
        {
            CurrentDirectory = _navigationService.CurrentPath,
            SelectedPath = GetSelectedItemFullPathForHeaderCopy(),
            SelectedName = GetSelectedItemNameForHeaderCopy(),
            MarkedPaths = _markedFiles.Snapshot()
        };
    }
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
            LogAltHint($"WM_SYSKEYDOWN Key={keyCode} AltHeld={_isAltHintHeld} CanShow={CanShowCommandHintOverlay()} ActiveControl={DescribeControl(ActiveControl)}");
            bool isAltOnlyKey =
                (keyCode == Keys.Menu || keyCode == Keys.LMenu || keyCode == Keys.RMenu) &&
                (ModifierKeys & Keys.Control) != Keys.Control;
            if (isAltOnlyKey && CanShowCommandHintOverlay())
            {
                _isAltHintHeld = true;
                ShowCommandHintOverlay();
                return;
            }
        }
        if (m.Msg == WM_SYSKEYUP)
        {
            LogAltHint($"WM_SYSKEYUP Key={keyCode} AltHeldBefore={_isAltHintHeld} ActiveControl={DescribeControl(ActiveControl)}");
            bool isAltKey =
                keyCode == Keys.Menu ||
                keyCode == Keys.LMenu ||
                keyCode == Keys.RMenu;
            if (isAltKey)
            {
                _isAltHintHeld = false;
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
            LogAltHint($"WM_SYSCOMMAND Command=0x{command:X} UiMode={_uiMode} ActiveControl={DescribeControl(ActiveControl)}");
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
                    $"hasCts={_fileOpCts != null}, requested={_fileOpCts?.IsCancellationRequested ?? false}, " +
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
                return;
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
        return BuildCommandHintState().CanShowOverlay;
    }
    private bool CanUseCommandLauncherCommands()
    {
        return BuildCommandHintState().CanUseCommandLauncherCommands;
    }
    private void OpenMenuStripFromKeyboard()
    {
        LogAltHintContext("OpenMenuStripFromKeyboard");
        HideCommandHintOverlay();
        _isAltHintHeld = false;
        UpdateMenuStripState();
        if (mainMenuStrip.Items.Count == 0)
        {
            return;
        }
        mainMenuStrip.Focus();
        if (mainMenuStrip.Items[0] is ToolStripMenuItem rootItem)
        {
            rootItem.Select();
            rootItem.ShowDropDown();
        }
    }
    private void MainForm_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu || (e.Control && e.Alt))
        {
            LogAltHint($"MainForm_KeyDown Key={e.KeyCode} Alt={e.Alt} Ctrl={e.Control} OverlayVisible={IsCommandHintOverlayVisible()}");
        }
        if (e.KeyCode == Keys.Escape && TryRouteActiveFileOperationCancel("MainForm.KeyDown"))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        bool isAltOnlyKey =
            (e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu) &&
            !e.Control;
        if (isAltOnlyKey && CanShowCommandHintOverlay())
        {
            _isAltHintHeld = true;
            ShowCommandHintOverlay();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (TryHandleCommandHintOverlayKeyDown(e)) return;
        if (_viewerInputRouter.TryHandleKeyDown(CreateViewerKeyDownContext(), e)) return;
        if (_browserInputRouter.TryHandleKeyDown(CreateBrowserKeyDownContext(), e)) return;
    }
    private ViewerInputRouter.CmdKeyContext CreateViewerCmdKeyContext()
    {
        return new ViewerInputRouter.CmdKeyContext
        {
            IsViewerMode = _uiMode == UIMode.Viewer,
            TryHandleCore = TryHandleViewerCmdKey
        };
    }
    private ViewerInputRouter.KeyDownContext CreateViewerKeyDownContext()
    {
        return new ViewerInputRouter.KeyDownContext
        {
            IsViewerMode = _uiMode == UIMode.Viewer,
            TryHandleCore = TryHandleViewerKeyDown
        };
    }
    private BrowserInputRouter.CmdKeyContext CreateBrowserCmdKeyContext()
    {
        return new BrowserInputRouter.CmdKeyContext
        {
            IsBrowserMode = _uiMode == UIMode.Browser,
            IsBrowserFocused = browserPanel.Focused,
            IsAuxPreviewActive = _previewPopupVisible && _previewPopup != null && _previewPopup.Visible,
            CanUseCommandLauncherCommands = CanUseCommandLauncherCommands(),
            TryHandleTabs = TryHandleBrowserCmdKeyTabs,
            OpenMenuStripFromKeyboard = OpenMenuStripFromKeyboard,
            TryHandleNavigation = TryHandleBrowserCmdKeyNavigation,
            TryHandleFileOperationUndoRedo = TryHandleBrowserCmdKeyFileOperationUndoRedo,
            TryHandleMarking = TryHandleBrowserCmdKeyMarking,
            TryHandleClipboard = TryHandleBrowserCmdKeyClipboard,
            TryHandleColumnCount = TryHandleBrowserCmdKeyColumnCount,
            TryHandleAliases = TryHandleBrowserCmdKeyAliases,
            TryHandleLaunch = TryHandleBrowserCmdKeyLaunch,
            TryHandleCommandLauncher = TryHandleBrowserCmdKeyExternalToolAltSlot
        };
    }
    private BrowserInputRouter.KeyDownContext CreateBrowserKeyDownContext()
    {
        return new BrowserInputRouter.KeyDownContext
        {
            IsBrowserMode = _uiMode == UIMode.Browser,
            TryHandleCore = TryHandleBrowserKeyDown
        };
    }
    private void MainForm_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu || e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey)
        {
            LogAltHint($"MainForm_KeyUp Key={e.KeyCode} AltHeld={_isAltHintHeld} OverlayVisible={IsCommandHintOverlayVisible()}");
        }
        bool isAltKey =
            e.KeyCode == Keys.Menu ||
            e.KeyCode == Keys.LMenu ||
            e.KeyCode == Keys.RMenu;
        if (isAltKey)
        {
            _isAltHintHeld = false;
            HideCommandHintOverlay("MainForm_KeyUp:AltReleased");
        }
    }
    private bool TryHandleCommandHintOverlayKeyDown(KeyEventArgs e)
    {
        if (!CanShowCommandHintOverlay())
        {
            HideCommandHintOverlay("TryHandleCommandHintOverlayKeyDown:CanShowFalse");
            return false;
        }
        if (IsCommandHintOverlayVisible() && e.KeyCode == Keys.Escape)
        {
            _isAltHintHeld = false;
            HideCommandHintOverlay("TryHandleCommandHintOverlayKeyDown:Escape");
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
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
        if (!shouldShow)
        {
            HideCommandHintOverlay("RefreshCommandHintOverlayState:ShouldShowFalse");
            return;
        }
        ShowCommandHintOverlay();
    }
    private void ShowCommandHintOverlay()
    {
        if (!CanShowCommandHintOverlay())
        {
            return;
        }
        LogAltHint($"ShowCommandHintOverlay Before OverlayVisible={IsCommandHintOverlayVisible()} ActiveControl={DescribeControl(ActiveControl)}");
        IReadOnlyList<ExternalToolAltHintRow> rows = BuildExternalToolAltHintRows();
        _commandHintRows = rows;
        browserPanel.Invalidate();
        string firstRow = _commandHintRows.Count > 0
            ? $"{_commandHintRows[0].SlotLabel}:{_commandHintRows[0].Title}"
            : "<none>";
        LogAltHint($"ShowCommandHintOverlay After OverlayVisible={IsCommandHintOverlayVisible()} Bounds={GetCommandHintOverlayBounds()} RowCount={_commandHintRows.Count} First={firstRow} BrowserContext={CanShowCommandHintOverlay()}");
    }
    private void HideCommandHintOverlay(string reason = "Unknown")
    {
        if (!IsCommandHintOverlayVisible())
        {
            _commandHintRows = Array.Empty<ExternalToolAltHintRow>();
            _lastLoggedCommandHintRowCount = -1;
            _lastLoggedCommandHintBounds = Rectangle.Empty;
            _lastLoggedCommandHintPanelSize = Size.Empty;
            return;
        }
        Rectangle overlayBounds = GetCommandHintOverlayBounds();
        LogAltHint($"HideCommandHintOverlay Reason={reason} Bounds={overlayBounds}");
        _commandHintRows = Array.Empty<ExternalToolAltHintRow>();
        _lastLoggedCommandHintRowCount = -1;
        _lastLoggedCommandHintBounds = Rectangle.Empty;
        _lastLoggedCommandHintPanelSize = Size.Empty;
        browserPanel.Invalidate();
    }
    private bool TryHandleBrowserCmdKeyExternalToolAltSlot(Keys keyData)
    {
        if (!TryResolveExternalToolByAltSlot(keyData, out ExternalToolCommandDefinition? tool, out string slotLabel))
        {
            return false;
        }
        if (GuardClipboardBusy())
        {
            return true;
        }
        LogAltHint($"TryHandleBrowserCmdKeyExternalToolAltSlot Slot={slotLabel} Tool={tool!.Id}");
        HideCommandHintOverlay("TryHandleBrowserCmdKeyExternalToolAltSlot");
        InvokeLaunchExternalTool(tool!);
        return true;
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
        var rows = new List<ExternalToolAltHintRow>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExternalToolCommandDefinition tool in store.Tools)
        {
            if (!tool.Enabled || string.IsNullOrWhiteSpace(tool.Id) || string.IsNullOrWhiteSpace(tool.ExecutablePath))
            {
                continue;
            }
            if (!TryNormalizeExternalToolAltSlot(tool.AltSlot, out string slot))
            {
                continue;
            }
            if (ReservedExternalToolAltSlots.Contains(slot[0]) || !used.Add(slot))
            {
                continue;
            }
            string displayName = string.IsNullOrWhiteSpace(tool.DisplayName) ? tool.Id : tool.DisplayName;
            rows.Add(new ExternalToolAltHintRow(
                $"Alt+{slot}",
                displayName,
                Path.GetFileName(tool.ExecutablePath)));
        }
        return rows.OrderBy(static x => x.SlotLabel, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private bool TryHandleBrowserCmdKeyFileOperationUndoRedo(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z))
        {
            if (GuardClipboardBusy()) return true;
            ExecuteFileOperationUndo();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Y))
        {
            if (GuardClipboardBusy()) return true;
            ExecuteFileOperationRedo();
            return true;
        }
        if (keyData == (Keys.Alt | Keys.Z))
        {
            if (GuardClipboardBusy()) return true;
            ExecuteFileOperationUndo();
            return true;
        }
        if (keyData == (Keys.Alt | Keys.Y))
        {
            if (GuardClipboardBusy()) return true;
            ExecuteFileOperationRedo();
            return true;
        }
        return false;
    }
    private bool TryHandleBrowserCmdKeyTabs(Keys keyData)
    {
        if (_uiMode != UIMode.Browser)
        {
            return false;
        }
        if (keyData == (Keys.Control | Keys.T))
        {
            CreateNewBrowserTab();
            return true;
        }
        if (keyData == (Keys.Control | Keys.L))
        {
            ToggleActiveBrowserTabLock();
            return true;
        }
        if (keyData == (Keys.Control | Keys.W))
        {
            CloseCurrentBrowserTab();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.N))
        {
            AddGeneratedBrowserTabCategory();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Left))
        {
            LogService.Info($"[BrowserTabCategory] Shortcut Key=Ctrl+Shift+Left ActiveCategory={_activeBrowserTabCategoryId} Tabs={_browserTabs.Count} ActiveIndex={_activeBrowserTabIndex}");
            SelectAdjacentBrowserTabCategory(-1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Right))
        {
            LogService.Info($"[BrowserTabCategory] Shortcut Key=Ctrl+Shift+Right ActiveCategory={_activeBrowserTabCategoryId} Tabs={_browserTabs.Count} ActiveIndex={_activeBrowserTabIndex}");
            SelectAdjacentBrowserTabCategory(+1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Left))
        {
            SelectAdjacentBrowserTab(-1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Right))
        {
            SelectAdjacentBrowserTab(+1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Alt | Keys.Left))
        {
            MoveBrowserTabCategory(_activeBrowserTabCategoryId, -1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Alt | Keys.Right))
        {
            MoveBrowserTabCategory(_activeBrowserTabCategoryId, +1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Tab))
        {
            SelectAdjacentBrowserTab(+1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            SelectAdjacentBrowserTab(-1);
            return true;
        }
        return false;
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
                int total = fileListView.Items.Count;
                if (_browserCursorIndex < total - 1)
                {
                    _browserCursorIndex++;
                    SyncBrowserSelection();
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
            int total = fileListView.Items.Count;
            if (_browserCursorIndex < total - 1)
            {
                _browserCursorIndex++;
                SyncBrowserSelection();
            }
        }
        PrimeRecentMultiMarkIntent();
    }
    private void RefreshMarkUi()
    {
        browserPanel.Invalidate();
        UpdateInfoPanel();
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
        bool changed = _markedFiles.Add(path);
        if (changed)
        {
            InvalidateMarkSummaryCache();
            InvalidateRecentMultiMarkIntent();
            ClearPendingEscExitMarkPersistence();
            SyncActiveBrowserTabMarksFromCurrentSelection();
        }
        return changed;
    }
    private bool UnmarkPath(string path)
    {
        bool changed = _markedFiles.Remove(path);
        if (changed)
        {
            InvalidateMarkSummaryCache();
            InvalidateRecentMultiMarkIntent();
            ClearPendingEscExitMarkPersistence();
            SyncActiveBrowserTabMarksFromCurrentSelection();
        }
        return changed;
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
        SyncActiveBrowserTabMarksFromCurrentSelection();
        LogService.Info($"[MoveHotpath] BulkUnmark reason={reason} requested={paths.Count} removed={removedCount}");
    }
    private void ClearMarks(bool invalidateRedo = true, bool preservePendingEscExitState = false)
    {
        if (_markedFiles.Count == 0) return;
        _markedFiles.Clear();
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        if (!preservePendingEscExitState)
        {
            ClearPendingEscExitMarkPersistence();
        }
        SyncActiveBrowserTabMarksFromCurrentSelection();
    }
    private void RestoreMarks(IEnumerable<string> paths, bool invalidateRedo = true)
    {
        _markedFiles.Restore(paths);
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        ClearPendingEscExitMarkPersistence();
        SyncActiveBrowserTabMarksFromCurrentSelection();
    }
    private void SyncActiveBrowserTabMarksFromCurrentSelection()
    {
        if (_activeBrowserTabIndex < 0 || _activeBrowserTabIndex >= _browserTabs.Count)
        {
            return;
        }
        _browserTabs[_activeBrowserTabIndex].MarkedPaths = CreatePersistableMarkedPaths(_markedFiles.Snapshot(), out int skippedCount);
        if (skippedCount > 0)
        {
            LogService.Info($"[BrowserTabs] Pruned stale active tab marks during sync. TabId={_browserTabs[_activeBrowserTabIndex].Id} Missing={skippedCount}");
        }
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
            BuildMarkGlobalSummary,
            ClearCategoryMarksFromDialog,
            ClearGlobalMarksFromDialog,
            ClearCurrentTabMarksFromDialog);
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
                bool isCurrentCategory = string.Equals(category.Id, _activeBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase);
                if (isCurrentCategory)
                {
                    currentCategoryName = category.DisplayName;
                }
                foreach (var tab in category.OpenTabs)
                {
                    globalTabCount++;
                    int markCount;
                    // 現在のタブは _markedFiles が最新
                    if (isCurrentCategory && tab.TabId == (_activeBrowserTabIndex >= 0 && _activeBrowserTabIndex < _browserTabs.Count ? _browserTabs[_activeBrowserTabIndex].Id : Guid.Empty))
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
        foreach (var tab in _browserTabs)
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
                if (string.Equals(category.Id, _activeBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase))
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
                if (string.Equals(category.CategoryId, _activeBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase))
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
            ShowStatusMessage($"カテゴリ '{_activeBrowserTabCategoryId}' のマークをすべて解除しました ({clearedCount}件)。");
        }
    }
    private void ClearGlobalMarksFromDialog()
    {
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        bool changed = false;
        int clearedCount = 0;
        // 1. 現在メモリ上で管理されているタブの状態をクリア
        foreach (var tab in _browserTabs)
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
        slot.SourceCategoryId = ResolveExistingBrowserTabCategoryId(_activeBrowserTabCategoryId);
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
    private string RestoreMarksFromSlot(int slotNumber)
    {
        MarkSlotEntry slot = GetOrCreateMarkSlot(slotNumber);
        List<string> slotPaths = slot.Paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        ClearMarks();
        RestoreMarks(restoredPaths);
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
        return message;
    }
    private void OpenMarkSlotSetOperationDialog(int preferredSlotNumber)
    {
        if (GuardFeatureDisabled(FeatureId.MarkSlotSetOperations, "PracticalStable では MarkSlot 集合演算は無効です。"))
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
        if (GuardFeatureDisabled(FeatureId.MarkSlotBackupTransfer, "PracticalStable では MarkSlot エクスポートは無効です。"))
        {
            return "PracticalStable では MarkSlot エクスポートは無効です。";
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
        if (GuardFeatureDisabled(FeatureId.MarkSlotBackupTransfer, "PracticalStable では MarkSlot インポートは無効です。"))
        {
            return "PracticalStable では MarkSlot インポートは無効です。";
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
        if (GuardFeatureDisabled(FeatureId.MarkSlotBackupTransfer, "PracticalStable では MarkSlot 一括エクスポートは無効です。"))
        {
            return "PracticalStable では MarkSlot 一括エクスポートは無効です。";
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
        if (GuardFeatureDisabled(FeatureId.MarkSlotBackupTransfer, "PracticalStable では MarkSlot 一括インポートは無効です。"))
        {
            return "PracticalStable では MarkSlot 一括インポートは無効です。";
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
        ClearMarks();
        RestoreMarks(restoredPaths);
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
        return _activeBrowserTabIndex >= 0 && _activeBrowserTabIndex < _browserTabs.Count
            ? _browserTabs[_activeBrowserTabIndex]
            : null;
    }
    private string GetActiveBrowserTabCategoryDisplayName()
    {
        return _browserTabCategories
            .FirstOrDefault(category => string.Equals(category.Id, _activeBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName
            ?? _activeBrowserTabCategoryId;
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
        string categoryId = ResolveExistingBrowserTabCategoryId(_activeBrowserTabCategoryId);
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
        if (!_settings.Session.PersistMarksAcrossRestart || markedPaths.Count == 0)
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
            if (rawKind == PreviewKind.Video && _settings.Preview?.VideoEnterPlaysExternal == true)
            {
                var launchResult = VideoPlaybackLaunchService.Launch(
                    fullPath,
                    _settings.Preview?.VideoToolDirectory,
                    _settings.Preview?.VideoPlaybackVolumePercent ?? 100,
                    0);
                if (launchResult.Success)
                {
                    if (launchResult.UsedFfplay)
                    {
                        ShowStatusMessage($"ffplay.exeで外部再生しました。音量:{launchResult.AppliedVolumePercent}%");
                    }
                    else
                    {
                        ShowStatusMessage("ffplay.exeが見つからないため、既定アプリで動画を開きました。");
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
            string? error = ExternalToolService.ExecuteShell(_navigationService.CurrentPath, $"\"{fullPath}\"");
            if (error != null) ShowStatusMessage(error);
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
        if (GuardClipboardBusy()) return;
        var item = GetCurrentBrowserItem();
        if (item == null || item.Text == "..") return;
        string? fullPath = item.Tag as string;
        if (string.IsNullOrEmpty(fullPath)) return;
        try
        {
            if (Directory.Exists(fullPath))
            {
                // ディレクトリは Explorer で開く (Z の軽量追加)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
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
        if (_activeBrowserTabIndex < 0 || _activeBrowserTabIndex >= _browserTabs.Count)
        {
            return false;
        }
        BrowserTabState state = _browserTabs[_activeBrowserTabIndex];
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
        var result = MessageBox.Show(
            this,
            "固定タブの範囲外です。親フォルダを新しいタブで開きますか？",
            "固定タブ範囲外",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
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
    private void ExecuteLogdisk()
    {
        if (GuardClipboardBusy()) return;
        string defaultPath = string.IsNullOrWhiteSpace(_navigationService.CurrentPath)
            ? (Path.GetPathRoot(_navigationService.CurrentPath) ?? "C:\\")
            : _navigationService.CurrentPath;
        string? selected = LogdiskDialog.Show(defaultPath);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            string resolved = _navigationService.NormalizeDestinationDirectory(selected);
            try
            {
                if (Directory.Exists(resolved))
                {
                    if (!PrepareUnlockedTabForLocationChange(resolved))
                    {
                        return;
                    }
                    LoadDirectory(resolved);
                }
                else
                {
                    MessageBox.Show($"指定されたパスが見つかりません: {resolved}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
    private void ExecuteSort()
    {
        if (GuardClipboardBusy()) return;
        string kindStr = _currentSort.ToString();
        var result = SortDialog.Show(kindStr, _sortAscending);
        if (result != null)
        {
            _currentSort = result.Kind switch
            {
                "Name" => SortKind.Name,
                "Ext" => SortKind.Ext,
                "Size" => SortKind.Size,
                "Date" => SortKind.Date,
                "DateCreated" => SortKind.DateCreated,
                "DateAccessed" => SortKind.DateAccessed,
                _ => _currentSort
            };
            _sortAscending = result.Ascending;
            _settings.Session.LastSortKind = _currentSort;
            _settings.Session.LastSortAscending = _sortAscending;
            LoadDirectory(_navigationService.CurrentPath);
        }
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
        return SelectionResolver.Resolve(_markedFiles, GetCurrentBrowserItem());
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
        if (_browserTabs.Count >= maxTabCount)
        {
            ShowStatusMessage($"タブは最大{maxTabCount}個までです。");
            _browserTabStrip?.FlashLimitReached();
            TryPlayBrowserTabLimitBeep();
            return false;
        }
        CaptureActiveBrowserTabState();
        string categoryId = ResolveExistingBrowserTabCategoryId(_activeBrowserTabCategoryId);
        BrowserTabState newState = CreateInitialBrowserTabStateForCategory(categoryId);
        int insertIndex = _activeBrowserTabIndex >= 0 && _activeBrowserTabIndex < _browserTabs.Count
            ? _activeBrowserTabIndex + 1
            : _browserTabs.Count;
        _browserTabs.Insert(insertIndex, newState);
        RefreshBrowserTabHeaders();
        _activeBrowserTabIndex = -1;
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
    private void ExecuteRename()
    {
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            _activeFileOperationName,
            _fileOpCts != null,
            "リネーム",
            ResolveSelection(),
            "リネーム対象がありません。");
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        var selection = entryPlan.Selection;
        if (!TryResolveMultiMarkSelectionAction("リネーム", "リネームをキャンセルしました。", selection, out selection))
        {
            return;
        }
        if (selection.Count == 1)
        {
            ExecuteSingleRename(selection.FirstPath);
            return;
        }
        ExecuteRenameEntry(selection);
    }
    private void ExecuteRenameEntry(SelectionResult selection)
    {
        var dialogResult = _renameDialogCoordinator.ShowEntryDialog(this, selection.FullPaths);
        if (!dialogResult.Confirmed || dialogResult.Mode == RenameEntryMode.Cancel)
        {
            ShowStatusMessage("リネームはキャンセルされました。");
            return;
        }
        if (dialogResult.Mode == RenameEntryMode.SingleStep)
        {
            ExecuteSequentialRename(selection, dialogResult.SingleStepInitialName);
            return;
        }
        ExecuteBatchRename(selection);
    }
    private void ExecuteSingleRename(string? sourcePath)
    {
        var outcome = _renameApplyCoordinator.ApplySingleRename(
            sourcePath ?? string.Empty,
            initialValue: null,
            showNoChangeStatus: true,
            showValidationMessage: true,
            (path, value, skipInitialPrompt, showValidation) =>
                _renameDialogCoordinator.ShowSingleRenameDialog(this, path, value, skipInitialPrompt, showValidation),
            GetFriendlyRenameErrorMessage,
            message => MessageBox.Show(message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error),
            BuildRenameUndoReadyMessage);
        ApplyRenameOutcome(outcome);
    }
    private void ExecuteSequentialRename(SelectionResult selection, string? firstItemInitialName)
    {
        var outcome = _renameApplyCoordinator.ApplySequentialRename(
            selection,
            firstItemInitialName,
            (path, value, skipInitialPrompt, showValidation) =>
                _renameDialogCoordinator.ShowSingleRenameDialog(this, path, value, skipInitialPrompt, showValidation),
            GetFriendlyRenameErrorMessage,
            message => MessageBox.Show(message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error),
            BuildRenameUndoReadyMessage);
        ApplyRenameOutcome(outcome);
    }
    private static string GetFriendlyRenameErrorMessage(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            const int sharingViolationHResult = unchecked((int)0x80070020);
            if (ioEx.HResult == sharingViolationHResult ||
                ioEx.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                return "別のプロセスがこのファイルを使用中のため、リネームできません。";
            }
        }
        return ex.Message;
    }
    private async void ExecuteBatchRename(SelectionResult selection)
    {
        string initialTemplate = "$F$E";
        if (_settings.Rename.RememberLastTemplate && !string.IsNullOrWhiteSpace(_settings.Rename.LastTemplate))
        {
            initialTemplate = _settings.Rename.LastTemplate;
        }
        var dialogResult = _renameDialogCoordinator.ShowBatchDialog(
            this,
            selection.FullPaths,
            initialTemplate,
            _settings.Rename.RememberLastTemplate);
        if (!dialogResult.Confirmed)
        {
            ShowStatusMessage("リネームはキャンセルされました。");
            return;
        }
        if (dialogResult.RememberTemplate)
        {
            _settings.Rename.RememberLastTemplate = true;
            _settings.Rename.LastTemplate = dialogResult.LastTemplateCandidate;
        }
        else
        {
            _settings.Rename.RememberLastTemplate = false;
        }
        SettingsManager.Save(_settings);
        if (GuardClipboardBusy()) return;
        var token = PrepareFileOperation("一括リネーム");
        int renameTotal = dialogResult.Preview.Items.Count(item => item.WillRename);
        var progressForm = new FileOperationProgressFallbackForm("一括リネーム", renameTotal, requestCancel: null, canCancel: false);
        PositionProgressFallbackForm(progressForm);
        progressForm.Show(this);
        progressForm.UpdateState("一括リネーム中", "準備中...", indeterminate: false, cancelRequested: false);
        try
        {
            var outcome = await Task.Run(() => _renameApplyCoordinator.ApplyBatchRename(
                selection,
                dialogResult.Preview,
                _navigationService.CurrentPath,
                message =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        Invoke(new Action(() => MessageBox.Show(this, message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                    }
                },
                BuildRenameUndoReadyMessage,
                (processed, total, currentName) =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() => progressForm.UpdateProgress(processed, total, currentName, cancelRequested: false)));
                    }
                }));
            if (outcome.StatusMessage == "問題のある行があるためリネームを実行できません。")
            {
                MessageBox.Show(this, outcome.StatusMessage, "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ApplyRenameOutcome(outcome);
        }
        catch (Exception ex)
        {
            LogService.Error("ExecuteBatchRename async error", ex);
            MessageBox.Show(this, $"予期せぬエラーが発生しました。\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            progressForm.Complete("一括リネーム完了");
            FinalizeFileOperation();
        }
    }
    private void ApplyRenameOutcome(RenameApplyCoordinator.RenameApplyOutcome outcome)
    {
        if (outcome.SuccessfulItems.Count > 0)
        {
            RecordRenameUndoBatch(outcome.SuccessfulItems);
        }
        if (outcome.PostOperationResult != null)
        {
            FileOperationResult renameResult = outcome.PostOperationResult;
            string statusMessage = FileOperationPresentationHelper.GetRenameResultStatusMessage(renameResult);
            HandlePostOperation(new FileOperationResult(
                renameResult.OperationName,
                renameResult.ExitStatus,
                renameResult.SuccessCount,
                renameResult.TotalCount,
                renameResult.NextFocusTarget,
                renameResult.DestinationDir,
                renameResult.ShouldClearPreview,
                renameResult.ShouldClearMarks,
                statusMessage,
                renameResult.SkipCount,
                renameResult.FailCount));
            return;
        }
        if (!string.IsNullOrWhiteSpace(outcome.StatusMessage))
        {
            ShowStatusMessage(outcome.StatusMessage);
        }
    }
    private async void ExecuteFileOperationUndo()
    {
        var stopwatch = Stopwatch.StartNew();
        LogService.Info($"[UndoRuntime] Undo requested. thread={Environment.CurrentManagedThreadId}");
        if (_isFileOperationUndoRedoBusy)
        {
            LogService.Warn($"[UndoRuntime] Undo ignored because another undo/redo is running. elapsed={stopwatch.ElapsedMilliseconds}ms");
            ShowStatusMessage("Undo/Redo 処理中です。");
            return;
        }
        if (!_fileOperationUndoRedoService.TryPeekUndo(out FileOperationUndoRedoBatch batch))
        {
            LogService.Warn($"[UndoRuntime] No undo batch. elapsed={stopwatch.ElapsedMilliseconds}ms");
            ShowStatusMessage("元に戻せるファイル操作がありません");
            return;
        }
        LogService.Info($"[UndoRuntime] Undo batch peeked. operation={batch.Operation}, items={batch.Items.Count}");
        bool showProgress = IsTrashDeleteUndoRedoOperation(batch.Operation);
        _isFileOperationUndoRedoBusy = true;
        UpdateMenuStripState();
        if (showProgress)
        {
            ShowFileOperationUndoRedoProgressFallback("元に戻す", batch.Items.Count);
        }
        try
        {
            var applyResult = await Task.Run(() =>
            {
                bool success = TryApplyFileOperationUndoRedoBatch(
                    batch,
                    undo: true,
                    out string? focusTargetName,
                    out string? errorMessage,
                    showProgress ? UpdateFileOperationUndoRedoProgressFallbackFromWorker : null);
                return new FileOperationUndoRedoApplyResult(success, focusTargetName, errorMessage);
            });
            if (!applyResult.Success)
            {
                if (showProgress)
                {
                    CompleteFileOperationUndoRedoProgressFallback("元に戻せませんでした。");
                }
                stopwatch.Stop();
                LogService.Warn(
                    $"[UndoRuntime] Undo apply failed. operation={batch.Operation}, items={batch.Items.Count}, " +
                    $"elapsed={stopwatch.ElapsedMilliseconds}ms, error={applyResult.ErrorMessage ?? "<none>"}");
                ShowStatusMessage(applyResult.ErrorMessage ?? "ファイル操作を元に戻せませんでした。");
                return;
            }
            _fileOperationUndoRedoService.CommitUndo();
            LogService.Info($"[RedoRuntime] Redo batch recorded by CommitUndo. operation={batch.Operation}, items={batch.Items.Count}");
            LoadDirectory(_navigationService.CurrentPath, applyResult.FocusTargetName);
            stopwatch.Stop();
            LogService.Info(
                $"[UndoRuntime] Undo completed. operation={batch.Operation}, items={batch.Items.Count}, " +
                $"focusTarget={applyResult.FocusTargetName ?? "<none>"}, elapsed={stopwatch.ElapsedMilliseconds}ms");
            string opLabel = GetFileOperationUndoRedoOperationLabel(batch.Operation);
            if (batch.IsPartialCancellation) opLabel += " (途中キャンセル分)";
            ShowStatusMessage($"{batch.Items.Count} 件の{opLabel}を元に戻しました");
            ScheduleBrowserFocusReturnAfterFileOperation("UndoCompleted");
            if (showProgress)
            {
                CompleteFileOperationUndoRedoProgressFallback("元に戻しました");
            }
        }
        catch (Exception ex)
        {
            if (showProgress)
            {
                CompleteFileOperationUndoRedoProgressFallback("元に戻せませんでした。");
            }
            stopwatch.Stop();
            LogService.Error(
                $"[UndoRuntime] Undo failed unexpectedly. operation={batch.Operation}, items={batch.Items.Count}, " +
                $"elapsed={stopwatch.ElapsedMilliseconds}ms",
                ex);
            ShowStatusMessage("ファイル操作を元に戻せませんでした。");
        }
        finally
        {
            _isFileOperationUndoRedoBusy = false;
            UpdateMenuStripState();
            TryProcessPendingCurrentDirectoryRefresh("UndoFinally");
        }
    }
    private async void ExecuteFileOperationRedo()
    {
        var stopwatch = Stopwatch.StartNew();
        LogService.Info($"[RedoRuntime] Redo requested. thread={Environment.CurrentManagedThreadId}");
        if (_isFileOperationUndoRedoBusy)
        {
            LogService.Warn($"[RedoRuntime] Redo ignored because another undo/redo is running. elapsed={stopwatch.ElapsedMilliseconds}ms");
            ShowStatusMessage("Undo/Redo 処理中です。");
            return;
        }
        if (!_fileOperationUndoRedoService.TryPeekRedo(out FileOperationUndoRedoBatch batch))
        {
            LogService.Warn($"[RedoRuntime] No redo batch. elapsed={stopwatch.ElapsedMilliseconds}ms");
            ShowStatusMessage("やり直せるファイル操作がありません");
            return;
        }
        LogService.Info($"[RedoRuntime] Redo batch peeked. operation={batch.Operation}, items={batch.Items.Count}");
        bool showProgress = IsTrashDeleteUndoRedoOperation(batch.Operation);
        string? precomputedFocusTargetName = IsTrashDeleteUndoRedoOperation(batch.Operation)
            ? GetNextFocusTarget(batch.Items.Select(item => item.BeforePath).ToList())
            : null;
        _isFileOperationUndoRedoBusy = true;
        UpdateMenuStripState();
        if (showProgress)
        {
            ShowFileOperationUndoRedoProgressFallback("やり直し", batch.Items.Count);
        }
        try
        {
            var applyResult = await Task.Run(() =>
            {
                bool success = TryApplyFileOperationUndoRedoBatch(
                    batch,
                    undo: false,
                    out string? focusTargetName,
                    out string? errorMessage,
                    showProgress ? UpdateFileOperationUndoRedoProgressFallbackFromWorker : null,
                    precomputedFocusTargetName);
                return new FileOperationUndoRedoApplyResult(success, focusTargetName, errorMessage);
            });
            if (!applyResult.Success)
            {
                if (showProgress)
                {
                    CompleteFileOperationUndoRedoProgressFallback("やり直せませんでした。");
                }
                stopwatch.Stop();
                LogService.Warn(
                    $"[RedoRuntime] Redo apply failed. operation={batch.Operation}, items={batch.Items.Count}, " +
                    $"elapsed={stopwatch.ElapsedMilliseconds}ms, error={applyResult.ErrorMessage ?? "<none>"}");
                ShowStatusMessage(applyResult.ErrorMessage ?? "ファイル操作をやり直せませんでした。");
                return;
            }
            _fileOperationUndoRedoService.CommitRedo();
            LogService.Info($"[UndoRuntime] Undo batch restored by CommitRedo. operation={batch.Operation}, items={batch.Items.Count}");
            LoadDirectory(_navigationService.CurrentPath, applyResult.FocusTargetName);
            stopwatch.Stop();
            LogService.Info(
                $"[RedoRuntime] Redo completed. operation={batch.Operation}, items={batch.Items.Count}, " +
                $"focusTarget={applyResult.FocusTargetName ?? "<none>"}, elapsed={stopwatch.ElapsedMilliseconds}ms");
            string opLabel = GetFileOperationUndoRedoOperationLabel(batch.Operation);
            if (batch.IsPartialCancellation) opLabel += " (途中キャンセル分)";
            ShowStatusMessage($"{batch.Items.Count} 件の{opLabel}をやり直しました");
            ScheduleBrowserFocusReturnAfterFileOperation("RedoCompleted");
            if (showProgress)
            {
                CompleteFileOperationUndoRedoProgressFallback("やり直しました");
            }
        }
        catch (Exception ex)
        {
            if (showProgress)
            {
                CompleteFileOperationUndoRedoProgressFallback("やり直せませんでした。");
            }
            stopwatch.Stop();
            LogService.Error(
                $"[RedoRuntime] Redo failed unexpectedly. operation={batch.Operation}, items={batch.Items.Count}, " +
                $"elapsed={stopwatch.ElapsedMilliseconds}ms",
                ex);
            ShowStatusMessage("ファイル操作をやり直せませんでした。");
        }
        finally
        {
            _isFileOperationUndoRedoBusy = false;
            UpdateMenuStripState();
            TryProcessPendingCurrentDirectoryRefresh("RedoFinally");
        }
    }
    private readonly record struct FileOperationUndoRedoApplyResult(
        bool Success,
        string? FocusTargetName,
        string? ErrorMessage);
    private bool TryApplyFileOperationUndoRedoBatch(
        FileOperationUndoRedoBatch batch,
        bool undo,
        out string? focusTargetName,
        out string? errorMessage,
        Action<int, int, string>? progress = null,
        string? precomputedFocusTargetName = null)
    {
        focusTargetName = null;
        errorMessage = null;
        if (batch.Items.Count == 0)
        {
            errorMessage = "Undo/Redo 履歴が空です。";
            return false;
        }
        if (IsTrashDeleteUndoRedoOperation(batch.Operation))
        {
            return TryApplyTrashDeleteUndoRedoBatch(
                batch,
                undo,
                out focusTargetName,
                out errorMessage,
                progress,
                precomputedFocusTargetName);
        }
        var operations = batch.Items
            .Select(item => undo
                ? new { CurrentPath = item.AfterPath, TargetPath = item.BeforePath, TargetName = item.BeforeName }
                : new { CurrentPath = item.BeforePath, TargetPath = item.AfterPath, TargetName = item.AfterName })
            .ToList();
        foreach (var operation in operations)
        {
            if (!PathExists(operation.CurrentPath))
            {
                errorMessage = $"対象が見つからないため続行できません: {operation.CurrentPath}";
                return false;
            }
            if (PathExists(operation.TargetPath))
            {
                errorMessage = $"同名の項目があるため続行できません: {operation.TargetPath}";
                return false;
            }
        }
        try
        {
            foreach (var operation in Enumerable.Reverse(operations))
            {
                if (batch.Operation == FileOperationUndoRedoOperation.Rename)
                {
                    FileOperationService.Rename(operation.CurrentPath, operation.TargetPath);
                    continue;
                }
                FileOperationService.Move(operation.CurrentPath, operation.TargetPath, overwrite: false);
            }
        }
        catch (Exception ex)
        {
            _fileOperationUndoRedoService.Reset();
            errorMessage = $"{ex.Message} (履歴は安全側で破棄しました)";
            return false;
        }
        focusTargetName = operations
            .Select(operation => operation.TargetPath)
            .FirstOrDefault(path =>
                string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                    NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath),
                    StringComparison.OrdinalIgnoreCase))
            is string focusPath
                ? Path.GetFileName(focusPath)
                : null;
        return true;
    }
    private bool TryApplyTrashDeleteUndoRedoBatch(
        FileOperationUndoRedoBatch batch,
        bool undo,
        out string? focusTargetName,
        out string? errorMessage,
        Action<int, int, string>? progress = null,
        string? precomputedFocusTargetName = null)
    {
        focusTargetName = null;
        errorMessage = null;
        try
        {
            var batchStopwatch = Stopwatch.StartNew();
            LogService.Info(
                $"[UndoRuntime] Recycle-bin batch apply start. mode={(undo ? "UndoRestore" : "RedoDelete")}, " +
                $"items={batch.Items.Count}, thread={Environment.CurrentManagedThreadId}");
            if (undo)
            {
                if (batch.Operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash)
                {
                    LogService.Info($"[FileOperationUndo] Restoring MidFD managed trash batch: {batch.Items.Count} items");
                    MidFdManagedTrashService.ResetManifestOperationDiagnostics();
                    MidFdManagedTrashService.BeginManifestBatch();
                    var uiUpdateSw = new Stopwatch();
                    int managedIndex = 0;
                    long maxItemMs = 0;
                    if (batch.Items.Count > 10)
                    {
                        MidFdManagedTrashService.SetLoggingSuppression(true);
                    }
                    try
                    {
                        var trashPathsToUpdate = new List<string>();
                        foreach (FileOperationUndoRedoItem item in batch.Items)
                        {
                            var itemSw = Stopwatch.StartNew();
                            managedIndex++;
                            uiUpdateSw.Start();
                            progress?.Invoke(managedIndex - 1, batch.Items.Count, Path.GetFileName(item.BeforePath));
                            uiUpdateSw.Stop();
                            bool suppressLogging = batch.Items.Count > 10;
                            MidFdManagedTrashService.RestoreFromTrash(item, skipStatusUpdate: true, suppressLogging: suppressLogging);
                            trashPathsToUpdate.Add(item.RecycleBinPath!);
                            uiUpdateSw.Start();
                            progress?.Invoke(managedIndex, batch.Items.Count, Path.GetFileName(item.BeforePath));
                            uiUpdateSw.Stop();
                            itemSw.Stop();
                            if (itemSw.ElapsedMilliseconds > maxItemMs) maxItemMs = itemSw.ElapsedMilliseconds;
                        }
                        if (trashPathsToUpdate.Count > 0)
                        {
                            MidFdManagedTrashService.UpdateRecordStatuses(trashPathsToUpdate, TrashRecordStatus.Restored);
                        }
                    }
                    finally
                    {
                        int suppressedCount = MidFdManagedTrashService.GetSuppressedSuccessCount();
                        if (suppressedCount > 0 || batch.Items.Count > 10)
                        {
                            LogService.Info($"[MidFdTrashLogThrottle] Summary operation={(undo ? "UndoRestore" : "RedoDelete")} items={batch.Items.Count} suppressed={suppressedCount} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
                        }
                        MidFdManagedTrashService.FlushManifestBatch();
                        MidFdManagedTrashService.SetLoggingSuppression(false);
                    }
                    focusTargetName = batch.Items
                        .Select(item => item.BeforePath)
                        .FirstOrDefault(path =>
                            string.Equals(
                                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                                NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath),
                                StringComparison.OrdinalIgnoreCase))
                        is string restoredPath
                            ? Path.GetFileName(restoredPath)
                            : null;
                    batchStopwatch.Stop();
                    var metrics = MidFdManagedTrashService.GetUndoRedoMetrics();
                    LogService.Info(
                        $"[UndoRedoPerf] Undo completed. operation={batch.Operation}, items={batch.Items.Count}, " +
                        $"totalMs={batchStopwatch.ElapsedMilliseconds}, lookupMs={metrics.lookup}, fileMoveMs={metrics.fileMove}, " +
                        $"statusUpdateMs={metrics.statusUpdate}, manifestStoreMs={metrics.manifestStore}, uiUpdateMs={uiUpdateSw.ElapsedMilliseconds}, " +
                        $"perItemAvgMs={(double)batchStopwatch.ElapsedMilliseconds / Math.Max(1, batch.Items.Count):F2}, maxItemMs={maxItemMs}");
                    return true;
                }
                errorMessage = "未対応の削除Undo操作です。";
                return false;
            }
            var refreshedItems = new List<FileOperationUndoRedoItem>();
            if (batch.Operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash)
            {
                LogService.Info($"[FileOperationRedo] Re-deleting MidFD managed trash batch: {batch.Items.Count} items");
                MidFdManagedTrashService.ResetManifestOperationDiagnostics();
                MidFdManagedTrashService.BeginManifestBatch();
                var uiUpdateSw = new Stopwatch();
                int managedRedoIndex = 0;
                long maxItemMs = 0;
                var recordsToRegister = new List<TrashManifestRecord>();
                if (batch.Items.Count > 10)
                {
                    MidFdManagedTrashService.SetLoggingSuppression(true);
                }
                try
                {
                    foreach (FileOperationUndoRedoItem item in batch.Items)
                    {
                        var itemSw = Stopwatch.StartNew();
                        managedRedoIndex++;
                        if (!PathExists(item.BeforePath))
                        {
                            errorMessage = $"対象が見つからないため続行できません: {item.BeforePath}";
                            return false;
                        }
                        uiUpdateSw.Start();
                        progress?.Invoke(managedRedoIndex - 1, batch.Items.Count, Path.GetFileName(item.BeforePath));
                        uiUpdateSw.Stop();
                        bool suppressLogging = batch.Items.Count > 10;
                        refreshedItems.Add(MidFdManagedTrashService.RedoDeleteToTrash(item, out TrashManifestRecord? record, skipRegistration: true, suppressLogging: suppressLogging));
                        if (record != null) recordsToRegister.Add(record);
                        if (recordsToRegister.Count >= 1000)
                        {
                            MidFdManagedTrashService.RegisterNewTrashRecordsPublic(recordsToRegister);
                            recordsToRegister.Clear();
                        }
                        uiUpdateSw.Start();
                        progress?.Invoke(managedRedoIndex, batch.Items.Count, Path.GetFileName(item.BeforePath));
                        uiUpdateSw.Stop();
                        itemSw.Stop();
                        if (itemSw.ElapsedMilliseconds > maxItemMs) maxItemMs = itemSw.ElapsedMilliseconds;
                    }
                }
                finally
                {
                    if (recordsToRegister.Count > 0)
                    {
                        MidFdManagedTrashService.RegisterNewTrashRecordsPublic(recordsToRegister);
                        recordsToRegister.Clear();
                    }
                    int suppressedCount = MidFdManagedTrashService.GetSuppressedSuccessCount();
                    if (suppressedCount > 0 || batch.Items.Count > 10)
                    {
                        LogService.Info($"[MidFdTrashLogThrottle] Summary operation=RedoDelete items={batch.Items.Count} suppressed={suppressedCount} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
                    }
                    MidFdManagedTrashService.FlushManifestBatch();
                    MidFdManagedTrashService.SetLoggingSuppression(false);
                }
                batch.Items = FileOperationUndoRedoService.CreateDeleteToTrashBatch(refreshedItems);
                focusTargetName = precomputedFocusTargetName;
                batchStopwatch.Stop();
                var metrics = MidFdManagedTrashService.GetUndoRedoMetrics();
                LogService.Info(
                    $"[UndoRedoPerf] Redo completed. operation={batch.Operation}, items={batch.Items.Count}, " +
                    $"totalMs={batchStopwatch.ElapsedMilliseconds}, lookupMs={metrics.lookup}, fileMoveMs={metrics.fileMove}, " +
                    $"statusUpdateMs={metrics.statusUpdate}, manifestStoreMs={metrics.manifestStore}, uiUpdateMs={uiUpdateSw.ElapsedMilliseconds}, " +
                    $"perItemAvgMs={(double)batchStopwatch.ElapsedMilliseconds / Math.Max(1, batch.Items.Count):F2}, maxItemMs={maxItemMs}");
                return true;
            }
            errorMessage = "未対応の削除Redo操作です。";
            return false;
        }
        catch (Exception ex)
        {
            _fileOperationUndoRedoService.Reset();
            LogService.Error("[UndoRuntime] Recycle-bin batch failed and history was reset.", ex);
            errorMessage = $"{ex.Message} (履歴は安全側で破棄しました)";
            return false;
        }
    }
    private static bool PathExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }
    private static List<string> CreatePersistableMarkedPaths(IEnumerable<string>? paths, out int skippedCount)
    {
        skippedCount = 0;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || !PathExists(path))
            {
                skippedCount++;
                continue;
            }
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }
        return result;
    }
    private void RecordRenameUndoBatch(IEnumerable<RenamePreviewItem> items)
    {
        _fileOperationUndoRedoService.RecordBatch(
            FileOperationUndoRedoOperation.Rename,
            FileOperationUndoRedoService.CreateRenameBatch(items));
    }
    private static string BuildRenameUndoReadyMessage(int successCount, int totalCount)
    {
        return BuildFileOperationUndoReadyMessage("リネーム", successCount, totalCount);
    }
    private static string BuildMoveUndoReadyMessage(int successCount, int totalCount)
    {
        return BuildFileOperationUndoReadyMessage("移動", successCount, totalCount);
    }
    private static string BuildFileOperationUndoReadyMessage(string operationLabel, int successCount, int totalCount)
    {
        return successCount == totalCount
            ? $"{successCount} 件{operationLabel}しました。Ctrl+Z で元に戻せます。"
            : $"{successCount} 件{operationLabel}しました。Ctrl+Z で成功分を元に戻せます。";
    }
    private static string GetFileOperationUndoRedoOperationLabel(FileOperationUndoRedoOperation operation)
    {
        return operation switch
        {
            FileOperationUndoRedoOperation.Rename => "リネーム",
            FileOperationUndoRedoOperation.Move => "移動",
            FileOperationUndoRedoOperation.DeleteToMidFdTrash => "削除",
            _ => "ファイル操作"
        };
    }
    private static bool IsTrashDeleteUndoRedoOperation(FileOperationUndoRedoOperation operation)
    {
        return operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash;
    }
    private async Task ExecuteDelete(bool permanent = false)
    {
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            _activeFileOperationName,
            _fileOpCts != null,
            "削除",
            ResolveSelection(),
            "削除対象がありません。");
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        var selectionSw = Stopwatch.StartNew();
        var selection = entryPlan.Selection;
        if (!TryResolveMultiMarkSelectionAction("削除", "削除をキャンセルしました。", selection, out selection))
        {
            return;
        }
        selectionSw.Stop();
        long selectionResolveMs = selectionSw.ElapsedMilliseconds;
        var warningSw = Stopwatch.StartNew();
        bool usePermanentDelete = permanent;
        bool useMidFdManagedTrash = !usePermanentDelete && (_settings.FileOperations?.UseMidFdManagedTrash ?? false);
        bool shouldConfirm = usePermanentDelete
            ? (_settings.FileOperations?.ConfirmPermanentDelete ?? true)
            : (_settings.FileOperations?.ConfirmDelete ?? true);
        warningSw.Stop();
        long outsideWarningMs = warningSw.ElapsedMilliseconds;
        var confirmSw = Stopwatch.StartNew();
        if (shouldConfirm && !_fileOperationDialogCoordinator.ConfirmDelete(this, selection, usePermanentDelete, _navigationService.CurrentPath, ShowStatusMessage))
        {
            return;
        }
        confirmSw.Stop();
        long confirmDialogMs = confirmSw.ElapsedMilliseconds;
        var focusPrepSw = Stopwatch.StartNew();
        // 操作後に一気に一番上まで戻るのを防ぐため、あらかじめ次にフォーカスすべき対象を見つけておく
        string? nextTargetName = GetNextFocusTarget(selection.FullPaths.ToList());
        focusPrepSw.Stop();
        long focusTargetPrepareMs = focusPrepSw.ElapsedMilliseconds;
        int totalCount = selection.Count;
        int successCount = 0;
        int failCount = 0;
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;
        var successPaths = new List<string>();
        var recycleBinDeleteUndoItems = new List<FileOperationUndoRedoItem>();
        bool canRecordRecycleBinUndo = useMidFdManagedTrash;
        bool recordedRecycleBinUndo = false;
        CancellationToken token = PrepareFileOperation(usePermanentDelete ? "完全削除" : "削除");
        int deleteStatusVersion = _fileOperationStatusVersion;
        bool useShellGuardedRecycleBinDelete = !usePermanentDelete && !useMidFdManagedTrash && totalCount <= ShellGuardedRecycleBinDeleteMaxItems;
        ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage("Delete", totalCount));
        if (!usePermanentDelete)
        {
            ShowShellDeleteProgressFallback(deleteStatusVersion, totalCount);
            if (useShellGuardedRecycleBinDelete)
            {
                UpdateShellDeleteProgressFallbackStateIfCurrent(
                    deleteStatusVersion,
                    "Shell 削除実行中...",
                    "Shell からの完了通知を待っています",
                    indeterminate: true);
            }
        }
        LogService.Info($"[MidFdTrashIntegrity] ExecuteDelete started. (Build: 2026-04-26-Investigation-Correctness)");
        var deleteTotalStopwatch = Stopwatch.StartNew();
        DateTime recycleBinDeleteStartedUtc = DateTime.UtcNow;
        long deleteLoopTotalMs = 0;
        long undoRecordMs = 0;
        long postOperationMs = 0;
        long shellServiceMs = 0;
        long progressCompleteMs = 0;
        // LargeDeletePerf metrics
        long manifestOperationTotalMs = 0;
        long manifestFileMoveTotalMs = 0;
        long manifestUpsertTotalMs = 0;
        long manifestLogTotalMs = 0;
        long manifestSaveTotalMs = 0;
        long progressUiTotalMs = 0;
        long progressiveRemovalTotalMs = 0;
        long markRemovalTotalMs = 0;
        long headerMenuUpdateTotalMs = 0;
        int manifestUpsertCount = 0;
        int manifestSaveCount = 0;
        int manifestFlushCount = 0;
        int manifestLogSuppressedCount = 0;
        int manifestSuccessLogCount = 0;
        int manifestChunkSummaryCount = 0;
        int manifestSlowItemCount = 0;
        int manifestAppendCount = 0;
        long manifestUpsertScanCount = 0;
        long manifestAppendMs = 0;
        int manifestRecordCountBefore = 0;
        int manifestRecordCountAfter = 0;
        bool manifestAppendMode = false;
        int headerUpdateCount = 0;
        int menuUpdateCount = 0;
        int progressUpdateCount = 0;
        int progressiveRemovalCount = 0;
        int markRemoveCallCount = 0;
        int invalidateCount = 0;
        long uiFlushMaxMs = 0;
        string midFdTrashBatchId = MidFdManagedTrashService.CreateBatchId();
        try
        {
            var swLoop = Stopwatch.StartNew();
            if (usePermanentDelete)
            {
                var result = await Task.Run(() =>
                {
                    int currentSuccess = 0;
                    int currentFailCount = 0;
                    FileOpExitStatus currentStatus = FileOpExitStatus.Success;
                    var chunkSw = Stopwatch.StartNew();
                    int chunkStartIndex = 0;
                    long chunkMaxPerItemMs = 0;
                    var pendingUiPaths = new List<string>();
                    var uiThrottleSw = Stopwatch.StartNew();
                    const int UI_CHUNK_SIZE = 250;
                    const int UI_THROTTLE_MS = 250;
                    bool largeDelete = totalCount >= 100;
                    foreach (string path in selection.FullPaths)
                    {
                        if (token.IsCancellationRequested)
                        {
                            currentStatus = FileOpExitStatus.Canceled;
                            break;
                        }
                        var itemSw = Stopwatch.StartNew();
                        string fileName = Path.GetFileName(path);
                        bool shouldUpdateProgress = (currentSuccess + currentFailCount) % 100 == 0 || pendingUiPaths.Count >= UI_CHUNK_SIZE || uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS;
                        if (shouldUpdateProgress)
                        {
                            var uiSw = Stopwatch.StartNew();
                            Invoke(new Action(() => ShowFileOperationProgressIfCurrent(
                                deleteStatusVersion,
                                "完全削除",
                                currentSuccess + currentFailCount + 1,
                                totalCount,
                                fileName)));
                            uiSw.Stop();
                            progressUiTotalMs += uiSw.ElapsedMilliseconds;
                            progressUpdateCount++;
                        }
                        try
                        {
                            FileOperationService.Delete(path);
                            currentSuccess++;
                            pendingUiPaths.Add(path);
                            string flushReason = "";
                            if (pendingUiPaths.Count >= UI_CHUNK_SIZE) flushReason = "CountThreshold";
                            else if (uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS) flushReason = "TimeThreshold";
                            if (!string.IsNullOrEmpty(flushReason))
                            {
                                var removalSw = Stopwatch.StartNew();
                                var flushPaths = pendingUiPaths.ToList();
                                pendingUiPaths.Clear();
                                Invoke(new Action(() => ApplyProgressiveDeleteUiChunk(
                                    flushPaths,
                                    deleteStatusVersion,
                                    ref markRemovalTotalMs,
                                    ref markRemoveCallCount,
                                    ref headerMenuUpdateTotalMs,
                                    ref headerUpdateCount,
                                    ref menuUpdateCount,
                                    ref invalidateCount,
                                    midFdTrashBatchId,
                                    flushReason)));
                                uiThrottleSw.Restart(); // restart AFTER invoke to avoid degenerate 1-item flushes
                                removalSw.Stop();
                                progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                progressiveRemovalCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Invoke(new Action(() =>
                                MessageBox.Show($"完全削除失敗: {path}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                            currentFailCount++;
                            currentStatus = FileOpExitStatus.Error;
                            break;
                        }
                        itemSw.Stop();
                        long itemMs = itemSw.ElapsedMilliseconds;
                        if (itemMs > chunkMaxPerItemMs) chunkMaxPerItemMs = itemMs;
                        if (itemMs > 1000)
                        {
                            LogService.Info($"[LargeDeletePerf] SlowItem operationId={midFdTrashBatchId} index={currentSuccess + currentFailCount} elapsedMs={itemMs} stage=PermanentDelete path={path}");
                        }
                        if ((currentSuccess + currentFailCount) % 100 == 0)
                        {
                            LogService.Info($"[LargeDeletePerf] DeleteChunk operationId={midFdTrashBatchId} start={chunkStartIndex} count=100 elapsedMs={chunkSw.ElapsedMilliseconds} avgPerItemMs={chunkSw.ElapsedMilliseconds / 100.0:F1} maxPerItemMs={chunkMaxPerItemMs}");
                            chunkSw.Restart();
                            chunkStartIndex = currentSuccess + currentFailCount;
                            chunkMaxPerItemMs = 0;
                        }
                    }
                    // Final flush
                    if (pendingUiPaths.Count > 0)
                    {
                        var removalSw = Stopwatch.StartNew();
                        var flushPaths = pendingUiPaths.ToList();
                        pendingUiPaths.Clear();
                        Invoke(new Action(() => ApplyProgressiveDeleteUiChunk(
                            flushPaths,
                            deleteStatusVersion,
                            ref markRemovalTotalMs,
                            ref markRemoveCallCount,
                            ref headerMenuUpdateTotalMs,
                            ref headerUpdateCount,
                            ref menuUpdateCount,
                            ref invalidateCount,
                            midFdTrashBatchId,
                            currentStatus == FileOpExitStatus.Canceled ? "CancelFinalFlush" : "FinalFlush")));
                        removalSw.Stop();
                        progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                        if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                        progressiveRemovalCount++;
                    }
                    return (currentSuccess, currentFailCount, currentStatus);
                }, token);
                swLoop.Stop();
                deleteLoopTotalMs = swLoop.ElapsedMilliseconds;
                LogService.Info($"[Perf] ExecuteDelete permanent async loop: {deleteLoopTotalMs}ms for {selection.Count} items");
                successCount = result.currentSuccess;
                failCount = result.currentFailCount;
                exitStatus = result.currentStatus;
            }
            else
            {
                if (useShellGuardedRecycleBinDelete)
                {
                    var shellServiceStopwatch = Stopwatch.StartNew();
                    var shellResult = await ShellRecycleBinDeleteService.DeleteToRecycleBinAsync(
                        selection.FullPaths.ToList(),
                        IsHandleCreated ? Handle : IntPtr.Zero,
                        token,
                        progress =>
                        {
                            if (IsDisposed || !IsHandleCreated)
                            {
                                return;
                            }
                            BeginInvoke(new Action(() =>
                            {
                                var uiSw = Stopwatch.StartNew();
                                ShowFileOperationProgressIfCurrent(
                                    deleteStatusVersion,
                                    "Delete",
                                    progress.ProcessedCount,
                                    progress.TotalCount,
                                    progress.Name);
                                UpdateShellDeleteProgressFallbackStateIfCurrent(
                                    deleteStatusVersion,
                                    _fileOpCts?.IsCancellationRequested ?? false
                                        ? "キャンセル要求中..."
                                        : "Shell 削除実行中...",
                                    progress.IsSuccess
                                        ? $"Shell 完了通知: {progress.ProcessedCount}/{progress.TotalCount} 件"
                                        : "Shell からの完了通知を待っています",
                                    indeterminate: true);
                                uiSw.Stop();
                                progressUiTotalMs += uiSw.ElapsedMilliseconds;
                                progressUpdateCount++;
                                if (progress.IsSuccess)
                                {
                                    var removalSw = Stopwatch.StartNew();
                                    // Shell guarded delete is usually small (<= MaxItems), so we use ApplyProgressiveDeleteUi directly
                                    // but if user increased the limit, it might be heavy.
                                    // For now, ShellGuardedRecycleBinDeleteMaxItems is likely small.
                                    ApplyProgressiveDeleteUi(progress.Path, deleteStatusVersion, ref markRemovalTotalMs, ref markRemoveCallCount, ref headerMenuUpdateTotalMs, ref headerUpdateCount, ref menuUpdateCount, ref invalidateCount);
                                    removalSw.Stop();
                                    progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                    if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                    progressiveRemovalCount++;
                                }
                            }));
                        });
                    shellServiceStopwatch.Stop();
                    shellServiceMs = shellServiceStopwatch.ElapsedMilliseconds;
                    swLoop.Stop();
                    deleteLoopTotalMs = swLoop.ElapsedMilliseconds;
                    LogService.Info(
                        $"[Perf] ExecuteDelete shell recycle-bin guarded loop: {deleteLoopTotalMs}ms " +
                        $"for {selection.Count} items, success={shellResult.SuccessCount}, " +
                        $"fail={shellResult.FailCount}, canceled={shellResult.IsCanceled}, hr=0x{shellResult.HResult:X8}, " +
                        $"serviceTotal={shellResult.TotalMs}ms, queueItems={shellResult.QueueItemsMs}ms, " +
                        $"perform={shellResult.PerformOperationsMs}ms, callbackSpan={shellResult.CallbackSpanMs}ms, " +
                        $"maxCallbackGap={shellResult.MaxCallbackGapMs}ms");
                    successCount = shellResult.SuccessCount;
                    failCount = shellResult.FailCount;
                    exitStatus = shellResult.IsCanceled
                        ? FileOpExitStatus.Canceled
                        : shellResult.HResult < 0
                            ? FileOpExitStatus.Error
                            : FileOpExitStatus.Success;
                    successPaths.AddRange(shellResult.SuccessPaths);
                }
                else if (useMidFdManagedTrash)
                {
                    bool largeDelete = totalCount > 10;
                    MidFdManagedTrashService.ResetManifestOperationDiagnostics();
                    // Always use batching for Managed Trash to ensure unified SQLite batch path even for small deletions
                    MidFdManagedTrashService.BeginManifestBatch();
                    if (largeDelete)
                    {
                        MidFdManagedTrashService.SetLoggingSuppression(true);
                    }
                    try
                    {
                        var managedTrashResult = await Task.Run(() =>
                    {
                        int currentSuccess = 0;
                        int currentFailCount = 0;
                        FileOpExitStatus currentStatus = FileOpExitStatus.Success;
                        var currentUndoItems = new List<FileOperationUndoRedoItem>();
                        var pendingRecords = new List<TrashManifestRecord>();
                        try
                        {
                            var chunkSw = Stopwatch.StartNew();
                            int chunkStartIndex = 0;
                            long chunkMaxPerItemMs = 0;
                            var pendingUiPaths = new List<string>();
                            var uiThrottleSw = Stopwatch.StartNew();
                            const int UI_CHUNK_SIZE = 250;
                            const int UI_THROTTLE_MS = 250;
                            foreach (string path in selection.FullPaths)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    currentStatus = FileOpExitStatus.Canceled;
                                    break;
                                }
                                var itemSw = Stopwatch.StartNew();
                                string fileName = Path.GetFileName(path);
                                int nextIndex = currentSuccess + currentFailCount + 1;
                                bool shouldUpdateProgress = (currentSuccess + currentFailCount) % 100 == 0 || pendingUiPaths.Count >= UI_CHUNK_SIZE || uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS;
                                if (shouldUpdateProgress)
                                {
                                    var uiSw = Stopwatch.StartNew();
                                    Invoke(new Action(() =>
                                    {
                                        ShowFileOperationProgressIfCurrent(
                                            deleteStatusVersion,
                                            "Delete",
                                            nextIndex,
                                            totalCount,
                                            fileName);
                                        UpdateShellDeleteProgressFallbackIfCurrent(
                                            deleteStatusVersion,
                                            currentSuccess + currentFailCount,
                                            totalCount,
                                            fileName);
                                    }));
                                    uiSw.Stop();
                                    progressUiTotalMs += uiSw.ElapsedMilliseconds;
                                    progressUpdateCount++;
                                }
                                try
                                {
                                    var trashSw = Stopwatch.StartNew();
                                    FileOperationUndoRedoItem undoItem = MidFdManagedTrashService.MoveToTrash(
                                        path,
                                        midFdTrashBatchId,
                                        nextIndex,
                                        true, // Always skip individual registration, we use batch registration below
                                        out TrashManifestRecord? record,
                                        out long fMoveMs,
                                        out long rUpsertMs,
                                        out long lMs,
                                        suppressLogging: largeDelete);
                                    if (record != null) pendingRecords.Add(record);
                                    trashSw.Stop();
                                    long totalOpMs = trashSw.ElapsedMilliseconds;
                                    manifestOperationTotalMs += totalOpMs;
                                    manifestFileMoveTotalMs += fMoveMs;
                                    manifestUpsertTotalMs += rUpsertMs;
                                    manifestLogTotalMs += lMs;
                                    manifestUpsertCount++;
                                    if (MidFdManagedTrashService.IsLoggingSuppressed()) manifestLogSuppressedCount++;
                                    else manifestSuccessLogCount++;
                                    if (totalOpMs > 1000) manifestSlowItemCount++;
                                    currentUndoItems.Add(undoItem);
                                    currentSuccess++;
                                    // Manifest chunk save (Unified for all deletion counts to ensure SQLite batch path)
                                    if (pendingRecords.Count >= 1000)
                                    {
                                        var mSw = Stopwatch.StartNew();
                                        MidFdManagedTrashService.RegisterNewTrashRecordsPublic(pendingRecords);
                                        pendingRecords.Clear();
                                        MidFdManagedTrashService.SaveActiveBatch();
                                        mSw.Stop();
                                        manifestSaveTotalMs += mSw.ElapsedMilliseconds;
                                        manifestSaveCount++;
                                        manifestFlushCount++;
                                        LogService.Info($"[LargeDeletePerf] ManifestFlush operationId={midFdTrashBatchId} reason=CountThreshold items={currentSuccess} elapsedMs={mSw.ElapsedMilliseconds} saveCount={manifestSaveCount}");
                                    }
                                    pendingUiPaths.Add(path);
                                    string flushReason = "";
                                    if (pendingUiPaths.Count >= UI_CHUNK_SIZE) flushReason = "CountThreshold";
                                    else if (uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS) flushReason = "TimeThreshold";
                                    if (!string.IsNullOrEmpty(flushReason))
                                    {
                                        var removalSw = Stopwatch.StartNew();
                                        var flushPaths = pendingUiPaths.ToList();
                                        pendingUiPaths.Clear();
                                        Invoke(new Action(() =>
                                        {
                                            ApplyProgressiveDeleteUiChunk(
                                                flushPaths,
                                                deleteStatusVersion,
                                                ref markRemovalTotalMs,
                                                ref markRemoveCallCount,
                                                ref headerMenuUpdateTotalMs,
                                                ref headerUpdateCount,
                                                ref menuUpdateCount,
                                                ref invalidateCount,
                                                midFdTrashBatchId,
                                                flushReason);
                                            UpdateShellDeleteProgressFallbackIfCurrent(
                                                deleteStatusVersion,
                                                currentSuccess,
                                                totalCount,
                                                fileName);
                                        }));
                                        uiThrottleSw.Restart(); // restart AFTER invoke to avoid degenerate 1-item flushes
                                        removalSw.Stop();
                                        progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                        if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                        progressiveRemovalCount++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Invoke(new Action(() =>
                                        MessageBox.Show($"削除失敗: {path}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                                    currentFailCount++;
                                    currentStatus = FileOpExitStatus.Error;
                                    break;
                                }
                                itemSw.Stop();
                                long itemMs = itemSw.ElapsedMilliseconds;
                                if (itemMs > chunkMaxPerItemMs) chunkMaxPerItemMs = itemMs;
                                if (itemMs > 1000)
                                {
                                    LogService.Info($"[LargeDeletePerf] SlowItem operationId={midFdTrashBatchId} index={currentSuccess + currentFailCount} elapsedMs={itemMs} stage=ManagedTrashMove path={path}");
                                }
                                if ((currentSuccess + currentFailCount) % 100 == 0)
                                {
                                    LogService.Info($"[LargeDeletePerf] DeleteChunk operationId={midFdTrashBatchId} start={chunkStartIndex} count=100 elapsedMs={chunkSw.ElapsedMilliseconds} avgPerItemMs={chunkSw.ElapsedMilliseconds / 100.0:F1} maxPerItemMs={chunkMaxPerItemMs}");
                                    if (largeDelete)
                                    {
                                        LogService.Info($"[MidFdTrash] MoveChunkSummary operationId={midFdTrashBatchId} start={chunkStartIndex} count=100 elapsedMs={chunkSw.ElapsedMilliseconds} avgPerItemMs={chunkSw.ElapsedMilliseconds / 100.0:F1} moved=100 failed=0 manifestBatchMode=true");
                                        manifestChunkSummaryCount++;
                                    }
                                    chunkSw.Restart();
                                    chunkStartIndex = currentSuccess + currentFailCount;
                                    chunkMaxPerItemMs = 0;
                                }
                            }
                            // Final flush
                            if (pendingUiPaths.Count > 0)
                            {
                                var removalSw = Stopwatch.StartNew();
                                var flushPaths = pendingUiPaths.ToList();
                                pendingUiPaths.Clear();
                                Invoke(new Action(() => ApplyProgressiveDeleteUiChunk(
                                    flushPaths,
                                    deleteStatusVersion,
                                    ref markRemovalTotalMs,
                                    ref markRemoveCallCount,
                                    ref headerMenuUpdateTotalMs,
                                    ref headerUpdateCount,
                                    ref menuUpdateCount,
                                    ref invalidateCount,
                                    midFdTrashBatchId,
                                    currentStatus == FileOpExitStatus.Canceled ? "CancelFinalFlush" : "FinalFlush")));
                                removalSw.Stop();
                                progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                progressiveRemovalCount++;
                            }
                            return (currentSuccess, currentFailCount, currentStatus, currentUndoItems);
                        }
                        finally
                        {
                            if (pendingRecords.Count > 0)
                            {
                                MidFdManagedTrashService.RegisterNewTrashRecordsPublic(pendingRecords);
                                pendingRecords.Clear();
                            }
                        }
                    }, token);
                        successCount = managedTrashResult.currentSuccess;
                        failCount = managedTrashResult.currentFailCount;
                        exitStatus = managedTrashResult.currentStatus;
                        recycleBinDeleteUndoItems.AddRange(managedTrashResult.currentUndoItems);
                    }
                    finally
                    {
                        // Manifest flush moved to outer finally to allow RestoreNow to reuse the active batch
                    }
                    var manifestDiagnostics = MidFdManagedTrashService.GetManifestOperationDiagnostics();
                    manifestAppendCount = manifestDiagnostics.AppendCount;
                    manifestUpsertScanCount = manifestDiagnostics.UpsertScanCount;
                    manifestAppendMs = manifestDiagnostics.AppendMs;
                    manifestRecordCountBefore = manifestDiagnostics.RecordCountBefore;
                    manifestRecordCountAfter = manifestDiagnostics.RecordCountAfter;
                    manifestAppendMode = manifestDiagnostics.AppendMode;
                    LogService.Info(
                        $"[LargeDeletePerf] ManifestRecordSummary operationId={midFdTrashBatchId} " +
                        $"appendMode={manifestAppendMode} appendCount={manifestAppendCount} " +
                        $"upsertScanCount={manifestUpsertScanCount} appendMs={manifestAppendMs} " +
                        $"recordCountBefore={manifestRecordCountBefore} recordCountAfter={manifestRecordCountAfter} " +
                        $"recordBatchCount={manifestDiagnostics.RecordBatchCount} recordBatchFlushCount={manifestDiagnostics.RecordBatchFlushCount} " +
                        $"recordBatchMs={manifestDiagnostics.RecordBatchMs} " +
                        $"dbConnMs={manifestDiagnostics.DbConnectionOpenMs} dbTransMs={manifestDiagnostics.DbTransactionBeginMs} " +
                        $"dbDelMs={manifestDiagnostics.DbDeleteLoopMs} dbInsMs={manifestDiagnostics.DbInsertLoopMs} dbCommitMs={manifestDiagnostics.DbCommitMs}");
                    swLoop.Stop();
                    deleteLoopTotalMs = swLoop.ElapsedMilliseconds;
                    LogService.Info(
                        $"[Perf] ExecuteDelete MidFD managed trash loop: {deleteLoopTotalMs}ms " +
                        $"for {selection.Count} items, success={successCount}, " +
                        $"fail={failCount}, canceled={exitStatus == FileOpExitStatus.Canceled}");
                }
                else
                {
                    var controlledResult = await Task.Run(() =>
                    {
                        int currentSuccess = 0;
                        int currentFailCount = 0;
                        FileOpExitStatus currentStatus = FileOpExitStatus.Success;
                        var currentSuccessPaths = new List<string>();
                        var chunkSw = Stopwatch.StartNew();
                        int chunkStartIndex = 0;
                        long chunkMaxPerItemMs = 0;
                        var pendingUiPaths = new List<string>();
                        var uiThrottleSw = Stopwatch.StartNew();
                        const int UI_CHUNK_SIZE = 250;
                        const int UI_THROTTLE_MS = 250;
                        bool useChunkedShellDelete = totalCount >= ChunkedShellRecycleBinDeleteMinItems;
                        if (useChunkedShellDelete)
                        {
                            int chunkCursor = 0;
                            while (chunkCursor < selection.FullPaths.Count)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    currentStatus = FileOpExitStatus.Canceled;
                                    break;
                                }
                                int chunkCount = Math.Min(ChunkedShellRecycleBinDeleteChunkSize, selection.FullPaths.Count - chunkCursor);
                                List<string> chunkPaths = selection.FullPaths.Skip(chunkCursor).Take(chunkCount).ToList();
                                string progressFileName = Path.GetFileName(chunkPaths[^1]);
                                var uiSw = Stopwatch.StartNew();
                                Invoke(new Action(() =>
                                {
                                    ShowFileOperationProgressIfCurrent(
                                        deleteStatusVersion,
                                        "Delete",
                                        currentSuccess + currentFailCount + 1,
                                        totalCount,
                                        progressFileName);
                                    UpdateShellDeleteProgressFallbackIfCurrent(
                                        deleteStatusVersion,
                                        currentSuccess + currentFailCount,
                                        totalCount,
                                        progressFileName);
                                }));
                                uiSw.Stop();
                                progressUiTotalMs += uiSw.ElapsedMilliseconds;
                                progressUpdateCount++;
                                ShellRecycleBinDeleteService.Result chunkResult =
                                    ShellRecycleBinDeleteService.DeleteToRecycleBinAsync(
                                        chunkPaths,
                                        IntPtr.Zero,
                                        token,
                                        static _ => { })
                                    .GetAwaiter()
                                    .GetResult();
                                currentSuccess += chunkResult.SuccessCount;
                                currentFailCount += chunkResult.FailCount;
                                currentSuccessPaths.AddRange(chunkResult.SuccessPaths);
                                pendingUiPaths.AddRange(chunkResult.SuccessPaths);
                                if (pendingUiPaths.Count > 0)
                                {
                                    var removalSw = Stopwatch.StartNew();
                                    var flushPaths = pendingUiPaths.ToList();
                                    pendingUiPaths.Clear();
                                    Invoke(new Action(() =>
                                    {
                                        ApplyProgressiveDeleteUiChunk(
                                            flushPaths,
                                            deleteStatusVersion,
                                            ref markRemovalTotalMs,
                                            ref markRemoveCallCount,
                                            ref headerMenuUpdateTotalMs,
                                            ref headerUpdateCount,
                                            ref menuUpdateCount,
                                            ref invalidateCount,
                                            midFdTrashBatchId,
                                            "ShellChunk");
                                        UpdateShellDeleteProgressFallbackIfCurrent(
                                            deleteStatusVersion,
                                            currentSuccess + currentFailCount,
                                            totalCount,
                                            progressFileName);
                                    }));
                                    removalSw.Stop();
                                    progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                    if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                    progressiveRemovalCount++;
                                }
                                LogService.Info(
                                    $"[Perf] ExecuteDelete chunked shell recycle-bin chunk: start={chunkCursor} count={chunkCount} " +
                                    $"success={chunkResult.SuccessCount} fail={chunkResult.FailCount} canceled={chunkResult.IsCanceled} " +
                                    $"serviceTotal={chunkResult.TotalMs}ms perform={chunkResult.PerformOperationsMs}ms");
                                if (chunkResult.IsCanceled)
                                {
                                    currentStatus = FileOpExitStatus.Canceled;
                                    break;
                                }
                                if (chunkResult.HResult < 0)
                                {
                                    currentStatus = FileOpExitStatus.Error;
                                    break;
                                }
                                chunkCursor += chunkCount;
                            }
                        }
                        else
                        {
                            foreach (string path in selection.FullPaths)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    currentStatus = FileOpExitStatus.Canceled;
                                    break;
                                }
                                var itemSw = Stopwatch.StartNew();
                                string fileName = Path.GetFileName(path);
                                bool shouldUpdateProgress = (currentSuccess + currentFailCount) % 100 == 0 || pendingUiPaths.Count >= UI_CHUNK_SIZE || uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS;
                                if (shouldUpdateProgress)
                                {
                                    var uiSw = Stopwatch.StartNew();
                                    Invoke(new Action(() =>
                                    {
                                        ShowFileOperationProgressIfCurrent(
                                            deleteStatusVersion,
                                            "Delete",
                                            currentSuccess + currentFailCount + 1,
                                            totalCount,
                                            fileName);
                                        UpdateShellDeleteProgressFallbackIfCurrent(
                                            deleteStatusVersion,
                                            currentSuccess + currentFailCount,
                                            totalCount,
                                            fileName);
                                    }));
                                    uiSw.Stop();
                                    progressUiTotalMs += uiSw.ElapsedMilliseconds;
                                    progressUpdateCount++;
                                }
                                try
                                {
                                    FileOperationService.DeleteToRecycleBin(path);
                                    currentSuccess++;
                                    currentSuccessPaths.Add(path);
                                    pendingUiPaths.Add(path);
                                    string flushReason = "";
                                    if (pendingUiPaths.Count >= UI_CHUNK_SIZE) flushReason = "CountThreshold";
                                    else if (uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS) flushReason = "TimeThreshold";
                                    if (!string.IsNullOrEmpty(flushReason))
                                    {
                                        var removalSw = Stopwatch.StartNew();
                                        var flushPaths = pendingUiPaths.ToList();
                                        pendingUiPaths.Clear();
                                        Invoke(new Action(() =>
                                        {
                                            ApplyProgressiveDeleteUiChunk(
                                                flushPaths,
                                                deleteStatusVersion,
                                                ref markRemovalTotalMs,
                                                ref markRemoveCallCount,
                                                ref headerMenuUpdateTotalMs,
                                                ref headerUpdateCount,
                                                ref menuUpdateCount,
                                                ref invalidateCount,
                                                midFdTrashBatchId,
                                                flushReason);
                                            UpdateShellDeleteProgressFallbackIfCurrent(
                                                deleteStatusVersion,
                                                currentSuccess,
                                                totalCount,
                                                fileName);
                                        }));
                                        uiThrottleSw.Restart(); // restart AFTER invoke to avoid degenerate 1-item flushes
                                        removalSw.Stop();
                                        progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                        if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                        progressiveRemovalCount++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Invoke(new Action(() =>
                                        MessageBox.Show($"削除失敗: {path}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                                    currentFailCount++;
                                    currentStatus = FileOpExitStatus.Error;
                                    break;
                                }
                                itemSw.Stop();
                                long itemMs = itemSw.ElapsedMilliseconds;
                                if (itemMs > chunkMaxPerItemMs) chunkMaxPerItemMs = itemMs;
                                if (itemMs > 1000)
                                {
                                    LogService.Info($"[LargeDeletePerf] SlowItem operationId={midFdTrashBatchId} index={currentSuccess + currentFailCount} elapsedMs={itemMs} stage=StandardRecycleBinDelete path={path}");
                                }
                                if ((currentSuccess + currentFailCount) % 100 == 0)
                                {
                                    LogService.Info($"[LargeDeletePerf] DeleteChunk operationId={midFdTrashBatchId} start={chunkStartIndex} count=100 elapsedMs={chunkSw.ElapsedMilliseconds} avgPerItemMs={chunkSw.ElapsedMilliseconds / 100.0:F1} maxPerItemMs={chunkMaxPerItemMs}");
                                    chunkSw.Restart();
                                    chunkStartIndex = currentSuccess + currentFailCount;
                                    chunkMaxPerItemMs = 0;
                                }
                            }
                        }
                    // Final flush
                    if (pendingUiPaths.Count > 0)
                    {
                        var removalSw = Stopwatch.StartNew();
                        var flushPaths = pendingUiPaths.ToList();
                        pendingUiPaths.Clear();
                        Invoke(new Action(() => ApplyProgressiveDeleteUiChunk(
                            flushPaths,
                            deleteStatusVersion,
                            ref markRemovalTotalMs,
                            ref markRemoveCallCount,
                            ref headerMenuUpdateTotalMs,
                            ref headerUpdateCount,
                            ref menuUpdateCount,
                            ref invalidateCount,
                            midFdTrashBatchId,
                            currentStatus == FileOpExitStatus.Canceled ? "CancelFinalFlush" : "FinalFlush")));
                        removalSw.Stop();
                        progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                        if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                        progressiveRemovalCount++;
                    }
                        return (currentSuccess, currentFailCount, currentStatus, currentSuccessPaths);
                    }, token);
                    swLoop.Stop();
                    deleteLoopTotalMs = swLoop.ElapsedMilliseconds;
                    LogService.Info(
                        $"[Perf] ExecuteDelete controlled recycle-bin loop: {deleteLoopTotalMs}ms " +
                        $"for {selection.Count} items, success={controlledResult.currentSuccess}, " +
                        $"fail={controlledResult.currentFailCount}, canceled={controlledResult.currentStatus == FileOpExitStatus.Canceled}");
                    successCount = controlledResult.currentSuccess;
                    failCount = controlledResult.currentFailCount;
                    exitStatus = controlledResult.currentStatus;
                    successPaths.AddRange(controlledResult.currentSuccessPaths);
                }
            }
            bool isFullSuccess = exitStatus == FileOpExitStatus.Success
                && successCount == totalCount
                && failCount == 0
                && !token.IsCancellationRequested;
            if (exitStatus == FileOpExitStatus.Canceled && useMidFdManagedTrash && successCount > 0)
            {
                int pendingCount = totalCount - successCount - failCount;
                var resolution = _fileOperationDialogCoordinator.ShowDeleteCancelResolution(this, successCount, pendingCount, failCount);
                LogService.Info($"[DeleteCancelResolution] Cancel requested success={successCount} pending={pendingCount} failed={failCount} UserChoice={resolution}");
                if (resolution == DeleteCancelResolution.RestoreNow)
                {
                    ShowStatusMessage($"{successCount} 件を復元中...");
                    LogService.Info($"[DeleteCancelRestorePerf] RestoreNow started items={recycleBinDeleteUndoItems.Count}");
                    var restoreSw = Stopwatch.StartNew();
                    long fileMoveTotalMs = 0;
                    long statusUpdateTotalMs = 0;
                    long maxItemMs = 0;
                    int slowCount = 0;
                    var restoredPaths = new List<string>();
                    var restoreResult = await Task.Run(() =>
                    {
                        try
                        {
                            MidFdManagedTrashService.ResetManifestOperationDiagnostics();
                            bool suppressLogging = recycleBinDeleteUndoItems.Count > 10;
                            if (suppressLogging)
                            {
                                MidFdManagedTrashService.SetLoggingSuppression(true); // Still set global for safety, but pass param too
                            }
                            foreach (var item in recycleBinDeleteUndoItems)
                            {
                                var itemSw = Stopwatch.StartNew();
                                try
                                {
                                    MidFdManagedTrashService.RestoreFromTrash(item, skipStatusUpdate: true, suppressLogging: suppressLogging);
                                    restoredPaths.Add(item.RecycleBinPath);
                                }
                                catch (Exception ex)
                                {
                                    LogService.Error($"[DeleteCancelRestorePerf] RestoreNow item failed path={item.BeforePath}", ex);
                                }
                                itemSw.Stop();
                                long elapsed = itemSw.ElapsedMilliseconds;
                                if (elapsed > 100)
                                {
                                    slowCount++;
                                    LogService.Info($"[DeleteCancelRestorePerf] RestoreNow slowItem path={item.BeforePath} elapsedMs={elapsed}");
                                }
                                if (elapsed > maxItemMs) maxItemMs = elapsed;
                                fileMoveTotalMs += elapsed;
                            }
                            if (restoredPaths.Count > 0)
                            {
                                var sSw = Stopwatch.StartNew();
                                MidFdManagedTrashService.UpdateRecordStatuses(restoredPaths, TrashRecordStatus.Restored);
                                sSw.Stop();
                                statusUpdateTotalMs = sSw.ElapsedMilliseconds;
                            }
                            int suppressedCount = MidFdManagedTrashService.GetSuppressedSuccessCount();
                            if (suppressedCount > 0 || recycleBinDeleteUndoItems.Count > 10)
                            {
                                LogService.Info($"[MidFdTrashLogThrottle] Summary operation=RestoreNow items={recycleBinDeleteUndoItems.Count} suppressed={suppressedCount} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
                            }
                            int suppressedCountAtEnd = MidFdManagedTrashService.GetSuppressedSuccessCount();
                            return (true, suppressedCountAtEnd);
                        }
                        catch (Exception ex)
                        {
                            LogService.Error("[DeleteCancelResolution] RestoreNow fatal error", ex);
                            return (false, 0);
                        }
                        finally
                        {
                            MidFdManagedTrashService.SetLoggingSuppression(false);
                        }
                    });
                    restoreSw.Stop();
                    long totalMs = restoreSw.ElapsedMilliseconds;
                    if (restoreResult.Item1)
                    {
                        int suppressedCount = restoreResult.Item2;
                        LogService.Info($"[DeleteCancelRestorePerf] RestoreNow completed items={successCount} totalMs={totalMs} fileMoveMs={fileMoveTotalMs} statusUpdateMs={statusUpdateTotalMs} slowItemCount={slowCount} maxItemMs={maxItemMs} perItemAvgMs={(double)totalMs / Math.Max(1, successCount):F2} suppressedSuccessLogs={suppressedCount}");
                        ShowStatusMessage("中断し、削除済みのファイルを復元しました。");
                    }
                    else
                    {
                        LogService.Warn($"[DeleteCancelRestorePerf] RestoreNow completed with some failures. totalMs={totalMs}");
                        ShowStatusMessage("中断しましたが、一部のファイル復元に失敗しました。");
                    }
                    canRecordRecycleBinUndo = false;
                    recycleBinDeleteUndoItems.Clear();
                }
                else
                {
                    // KeepDeleted or Cancel -> record undo for partial success items
                    canRecordRecycleBinUndo = true;
                    LogService.Info($"[DeleteCancelResolution] PartialUndoBatch will be registered count={successCount}");
                    ShowStatusMessage($"中断しました。削除済み {successCount} 件は Ctrl+Z で復元できます。");
                }
            }
            // partial / cancel では安全側として Undo 履歴を積まない。
            if (!usePermanentDelete && canRecordRecycleBinUndo && isFullSuccess && recycleBinDeleteUndoItems.Count != totalCount)
            {
                // Windows標準ごみ箱の場合、MidFD削除 Undo/Redo を積まない契約
                canRecordRecycleBinUndo = false;
                recycleBinDeleteUndoItems.Clear();
            }
            exitStatus = FileOperationPresentationHelper.NormalizeExitStatus(exitStatus, successCount, totalCount, failCount: failCount);
            if (!usePermanentDelete &&
                canRecordRecycleBinUndo &&
                (exitStatus == FileOpExitStatus.Success || (exitStatus == FileOpExitStatus.Canceled && recycleBinDeleteUndoItems.Count > 0)) &&
                recycleBinDeleteUndoItems.Count == successCount &&
                successCount > 0)
            {
                var undoRecordStopwatch = Stopwatch.StartNew();
                bool isPartialCancel = exitStatus == FileOpExitStatus.Canceled;
                _fileOperationUndoRedoService.RecordBatch(
                    FileOperationUndoRedoOperation.DeleteToMidFdTrash,
                    FileOperationUndoRedoService.CreateDeleteToTrashBatch(recycleBinDeleteUndoItems),
                    isPartialCancel);
                undoRecordStopwatch.Stop();
                undoRecordMs = undoRecordStopwatch.ElapsedMilliseconds;
                recordedRecycleBinUndo = true;
                LogService.Info($"[ShellDeleteUndo] Recorded MidFD managed trash undo batch: {recycleBinDeleteUndoItems.Count} items in {undoRecordMs}ms");
            }
            else if (!usePermanentDelete && useMidFdManagedTrash && isFullSuccess && !recordedRecycleBinUndo)
            {
                LogService.Warn(
                    $"[ShellDeleteUndo] MidFD recycle-bin undo batch was not recorded. " +
                    $"canRecord={canRecordRecycleBinUndo}, undoItems={recycleBinDeleteUndoItems.Count}, total={totalCount}");
            }
        }
        catch (OperationCanceledException)
        {
            exitStatus = FileOpExitStatus.Canceled;
        }
        catch (Exception ex)
        {
            exitStatus = FileOpExitStatus.Error;
            LogService.Error("ExecuteDelete async error", ex);
            _fileOperationDialogCoordinator.ShowUnexpectedOperationError(this, usePermanentDelete ? "完全削除" : "削除", ex);
        }
        finally
        {
            if (useMidFdManagedTrash)
            {
                var fSw = Stopwatch.StartNew();
                int suppressedCount = MidFdManagedTrashService.GetSuppressedSuccessCount();
                if (suppressedCount > 0 || totalCount > 10)
                {
                    string opName = exitStatus == FileOpExitStatus.Canceled ? "Delete(Canceled)" : "Delete";
                    LogService.Info($"[MidFdTrashLogThrottle] Summary operation={opName} items={totalCount} processed={successCount} suppressed={suppressedCount} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
                }
                MidFdManagedTrashService.FlushManifestBatch();
                MidFdManagedTrashService.SetLoggingSuppression(false);
                fSw.Stop();
                manifestSaveTotalMs += fSw.ElapsedMilliseconds;
                manifestSaveCount++;
                manifestFlushCount++;
            }
            var manifestDiagnostics = MidFdManagedTrashService.GetManifestOperationDiagnostics();
            var progressCompleteStopwatch = Stopwatch.StartNew();
            CompleteShellDeleteProgressFallbackIfCurrent(deleteStatusVersion, exitStatus, successCount, totalCount, failCount);
            progressCompleteStopwatch.Stop();
            progressCompleteMs = progressCompleteStopwatch.ElapsedMilliseconds;
            var postOperationStopwatch = Stopwatch.StartNew();
            HandlePostOperation(_fileOperationPostOperationCoordinator.CreateDeleteResult(
                exitStatus,
                successCount,
                totalCount,
                nextTargetName,
                usePermanentDelete,
                recordedRecycleBinUndo,
                failCount));
            postOperationStopwatch.Stop();
            postOperationMs = postOperationStopwatch.ElapsedMilliseconds;
            deleteTotalStopwatch.Stop();
            long cancelLatencyMs = 0;
            if (exitStatus == FileOpExitStatus.Canceled && _fileOperationCancelRequestedTimestamp > 0)
            {
                cancelLatencyMs = (long)Stopwatch.GetElapsedTime(_fileOperationCancelRequestedTimestamp).TotalMilliseconds;
            }
            string mode = usePermanentDelete ? "PermanentDelete" : (useMidFdManagedTrash ? "MidFdManagedTrash" : "WindowsRecycleBin");
            LogService.Info($"[LargeDeletePerf] BatchSummary operationId={midFdTrashBatchId} mode={mode} count={totalCount} success={successCount} fail={failCount} canceled={exitStatus == FileOpExitStatus.Canceled} totalMs={deleteTotalStopwatch.ElapsedMilliseconds} undoRecorded={recordedRecycleBinUndo}");
            LogService.Info($"[LargeDeletePerf] StageSummary operationId={midFdTrashBatchId} selectionResolveMs={selectionResolveMs} outsideWarningMs={outsideWarningMs} confirmDialogMs={confirmDialogMs} focusTargetPrepareMs={focusTargetPrepareMs} deleteLoopMs={deleteLoopTotalMs} manifestOperationMs={manifestOperationTotalMs} manifestFileMoveMs={manifestFileMoveTotalMs} manifestUpsertMs={manifestUpsertTotalMs} manifestLogMs={manifestLogTotalMs} manifestLogSuppressedCount={manifestLogSuppressedCount} manifestLogSuccessCount={manifestSuccessLogCount} manifestChunkSummaryCount={manifestChunkSummaryCount} manifestSlowItemCount={manifestSlowItemCount} manifestUpsertCount={manifestUpsertCount} manifestAppendMode={manifestAppendMode} manifestAppendCount={manifestAppendCount} manifestUpsertScanCount={manifestUpsertScanCount} manifestAppendMs={manifestAppendMs} manifestRecordCountBefore={manifestRecordCountBefore} manifestRecordCountAfter={manifestRecordCountAfter} manifestSaveCount={manifestSaveCount} manifestFlushCount={manifestFlushCount} manifestSaveTotalMs={manifestSaveTotalMs} dbConnMs={manifestDiagnostics.DbConnectionOpenMs} dbTransMs={manifestDiagnostics.DbTransactionBeginMs} dbDelMs={manifestDiagnostics.DbDeleteLoopMs} dbInsMs={manifestDiagnostics.DbInsertLoopMs} dbCommitMs={manifestDiagnostics.DbCommitMs} [ManagedTrashPerfInvestigation] totalFileMoveMs={manifestDiagnostics.TotalFileMoveMs} crossVolumeMoveCount={manifestDiagnostics.CrossVolumeMoveCount} sameVolumeCount={manifestDiagnostics.SameVolumeMoveCount} appDataFallbackCount={manifestDiagnostics.AppDataFallbackMoveCount} cancelLatencyMs={cancelLatencyMs} progressUiMs={progressUiTotalMs} progressCount={progressUpdateCount} progressiveRemovalMs={progressiveRemovalTotalMs} uiFlushCount={progressiveRemovalCount} uiFlushMaxMs={uiFlushMaxMs} markRemovalMs={markRemovalTotalMs} headerMenuUpdateMs={headerMenuUpdateTotalMs} postReloadMs={postOperationMs} undoRecordMs={undoRecordMs}");
        }
    }
    private void ScheduleBrowserFocusReturnAfterFileOperation(string reason)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || _uiMode != UIMode.Browser || !browserPanel.Visible)
            {
                return;
            }
            Activate();
            browserPanel.Focus();
            LogService.Info(
                $"[FileOperationFocus] Browser focus returned. reason={reason}, " +
                $"activeControl={DescribeControl(ActiveControl)}, browserFocused={browserPanel.Focused}");
        }));
    }
    private void ShowFileOperationUndoRedoProgressFallback(string operationName, int totalCount)
    {
        CloseFileOperationUndoRedoProgressFallback();
        var form = new FileOperationProgressFallbackForm(operationName, totalCount, requestCancel: null, canCancel: false);
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_undoRedoProgressFallback, form))
            {
                _undoRedoProgressFallback = null;
            }
            ScheduleBrowserFocusReturnAfterFileOperation("UndoRedoProgressFallbackClosed");
        };
        PositionProgressFallbackForm(form);
        _undoRedoProgressFallback = form;
        form.Show(this);
        form.UpdateProgress(0, totalCount, "準備中...", cancelRequested: false);
    }
    private void UpdateFileOperationUndoRedoProgressFallbackFromWorker(int processedCount, int totalCount, string currentFileName)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        BeginInvoke(new Action(() =>
        {
            _undoRedoProgressFallback?.UpdateProgress(processedCount, totalCount, currentFileName, cancelRequested: false);
        }));
    }
    private void CompleteFileOperationUndoRedoProgressFallback(string message)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        BeginInvoke(new Action(() =>
        {
            _undoRedoProgressFallback?.Complete(message);
        }));
    }
    private void CloseFileOperationUndoRedoProgressFallback()
    {
        var form = _undoRedoProgressFallback;
        _undoRedoProgressFallback = null;
        if (form != null && !form.IsDisposed)
        {
            form.Close();
        }
    }
    private void ApplyProgressiveDeleteUi(string deletedPath, int statusVersion, ref long markRemovalMs, ref int markRemoveCount, ref long headerUpdateMs, ref int headerUpdateCount, ref int menuUpdateCount, ref int invalidateCount)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        var markSw = Stopwatch.StartNew();
        UnmarkPath(deletedPath);
        markSw.Stop();
        markRemovalMs += markSw.ElapsedMilliseconds;
        markRemoveCount++;
        for (int i = 0; i < fileListView.Items.Count; i++)
        {
            if (fileListView.Items[i].Tag is string itemPath &&
                string.Equals(itemPath, deletedPath, StringComparison.OrdinalIgnoreCase))
            {
                fileListView.Items.RemoveAt(i);
                if (fileListView.Items.Count == 0)
                {
                    _browserCursorIndex = 0;
                }
                else if (_browserCursorIndex >= fileListView.Items.Count)
                {
                    _browserCursorIndex = fileListView.Items.Count - 1;
                }
                else if (i < _browserCursorIndex)
                {
                    _browserCursorIndex--;
                }
                break;
            }
        }
        if (string.Equals(_currentPreviewTarget, deletedPath, StringComparison.OrdinalIgnoreCase))
        {
            _currentPreviewTarget = null;
            ClearPreview();
        }
        var headerSw = Stopwatch.StartNew();
        UpdateInfoPanel();
        headerSw.Stop();
        headerUpdateMs += headerSw.ElapsedMilliseconds;
        headerUpdateCount++;
        UpdateMenuStripState();
        menuUpdateCount++;
        UpdateFunctionBar();
        browserPanel.Invalidate();
        invalidateCount++;
    }
    private void ApplyProgressiveDeleteUi(string deletedPath, int statusVersion)
    {
        long dummyMs = 0;
        int dummyCount = 0;
        ApplyProgressiveDeleteUi(deletedPath, statusVersion, ref dummyMs, ref dummyCount, ref dummyMs, ref dummyCount, ref dummyCount, ref dummyCount);
    }
    private void ApplyProgressiveDeleteUiChunk(
        List<string> deletedPaths,
        int statusVersion,
        ref long markRemovalMs,
        ref int markRemoveCount,
        ref long headerUpdateMs,
        ref int headerUpdateCount,
        ref int menuUpdateCount,
        ref int invalidateCount,
        string operationId,
        string reason)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion) || deletedPaths.Count == 0)
        {
            return;
        }
        var swFlush = Stopwatch.StartNew();
        // 1. Bulk Unmark
        var markSw = Stopwatch.StartNew();
        int removedMarks = _markedFiles.RemoveRange(deletedPaths);
        markSw.Stop();
        markRemovalMs += markSw.ElapsedMilliseconds;
        markRemoveCount++;
        if (removedMarks > 0)
        {
            // 大量削除中の chunk flush では、mark 全件の File I/O を避けるため
            // count-only のキャッシュ更新に留める。
            SetCountOnlyMarkSummaryCache();
            InvalidateRecentMultiMarkIntent();
            ClearPendingEscExitMarkPersistence();
            LogService.Info($"[LargeDeletePerf] BulkUnmark operationId={operationId} count={removedMarks} elapsedMs={markSw.ElapsedMilliseconds} reason={reason}");
        }
        // 2. Bulk UI Removal
        var targets = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);
        for (int i = fileListView.Items.Count - 1; i >= 0; i--)
        {
            if (fileListView.Items[i].Tag is string itemPath && targets.Contains(itemPath))
            {
                fileListView.Items.RemoveAt(i);
                if (fileListView.Items.Count == 0)
                {
                    _browserCursorIndex = 0;
                }
                else if (_browserCursorIndex >= fileListView.Items.Count)
                {
                    _browserCursorIndex = fileListView.Items.Count - 1;
                }
                else if (i < _browserCursorIndex)
                {
                    _browserCursorIndex--;
                }
            }
        }
        foreach (var path in deletedPaths)
        {
            if (string.Equals(_currentPreviewTarget, path, StringComparison.OrdinalIgnoreCase))
            {
                _currentPreviewTarget = null;
                ClearPreview();
                break;
            }
        }
        // 3. UI Global Updates
        if (reason.EndsWith("FinalFlush"))
        {
            var headerSw = Stopwatch.StartNew();
            UpdateInfoPanel();
            headerSw.Stop();
            headerUpdateMs += headerSw.ElapsedMilliseconds;
            headerUpdateCount++;
            UpdateMenuStripState();
            menuUpdateCount++;
            UpdateFunctionBar();
            browserPanel.Invalidate();
            invalidateCount++;
        }
        swFlush.Stop();
        LogService.Info($"[LargeDeletePerf] UiFlush operationId={operationId} reason={reason} items={deletedPaths.Count} elapsedMs={swFlush.ElapsedMilliseconds}");
    }
    private void ShowShellDeleteProgressFallback(int statusVersion, int totalCount)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        CloseShellDeleteProgressFallback();
        var form = new FileOperationProgressFallbackForm("削除", totalCount, () =>
        {
            RequestActiveFileOperationCancel("ShellDeleteProgressFallback");
        });
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_shellDeleteProgressFallback, form))
            {
                _shellDeleteProgressFallback = null;
            }
            ScheduleBrowserFocusReturnAfterFileOperation("ShellDeleteProgressFallbackClosed");
        };
        PositionProgressFallbackForm(form);
        _shellDeleteProgressFallback = form;
        form.Show(this);
        form.UpdateProgress(0, totalCount, "準備中...", _fileOpCts?.IsCancellationRequested ?? false);
    }
    private void PositionProgressFallbackForm(Form form)
    {
        form.Location = new Point(
            Left + Math.Max(0, (Width - form.Width) / 2),
            Top + Math.Max(0, (Height - form.Height) / 2));
    }
    private void UpdateShellDeleteProgressFallbackIfCurrent(int statusVersion, int processedCount, int totalCount, string currentFileName)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        _shellDeleteProgressFallback?.UpdateProgress(
            processedCount,
            totalCount,
            currentFileName,
            _fileOpCts?.IsCancellationRequested ?? false);
    }
    private void UpdateShellDeleteProgressFallbackStateIfCurrent(
        int statusVersion,
        string title,
        string detail,
        bool indeterminate)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        _shellDeleteProgressFallback?.UpdateState(
            title,
            detail,
            indeterminate,
            _fileOpCts?.IsCancellationRequested ?? false);
    }
    private void CompleteShellDeleteProgressFallbackIfCurrent(
        int statusVersion,
        FileOpExitStatus exitStatus,
        int successCount,
        int totalCount,
        int failCount)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        var form = _shellDeleteProgressFallback;
        if (form == null)
        {
            return;
        }
        string message = exitStatus switch
        {
            FileOpExitStatus.Success when successCount == totalCount && failCount == 0 => $"削除完了: {successCount}/{totalCount} 件",
            FileOpExitStatus.Canceled => $"削除を中断しました: {successCount}/{totalCount} 件",
            FileOpExitStatus.PartialSuccess => $"削除は一部完了: {successCount}/{totalCount} 件",
            _ => $"削除失敗または未完了: {successCount}/{totalCount} 件"
        };
        form.Complete(message);
    }
    private void CloseShellDeleteProgressFallback()
    {
        var form = _shellDeleteProgressFallback;
        _shellDeleteProgressFallback = null;
        if (form != null && !form.IsDisposed)
        {
            form.Close();
        }
    }
    private void ExecuteClipboardCopy()
    {
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            null,
            _fileOpCts != null,
            "コピー",
            ResolveSelection(),
            "コピー対象がありません。");
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
        _isClipboardBusy = true;
        try
        {
            ShellClipboardService.SetFileDrop(selection.FullPaths, false);
            ShowStatusMessage($"{selection.Count} 件をクリップボードにコピーしました。");
        }
        finally
        {
            _isClipboardBusy = false;
        }
    }
    private void ExecuteClipboardCut()
    {
        if (GuardReadOnlyBrowserTab("切り取り")) return;
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            null,
            _fileOpCts != null,
            "切り取り",
            ResolveSelection(),
            "切り取り対象がありません。");
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        _isClipboardBusy = true;
        try
        {
            var selection = entryPlan.Selection;
            ShellClipboardService.SetFileDrop(selection.FullPaths, true);
            ShowStatusMessage($"{selection.Count} 件をクリップボードに切り取り登録しました。");
        }
        finally
        {
            _isClipboardBusy = false;
        }
    }
    private async void ExecuteClipboardPaste()
    {
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        if (!ShellClipboardService.TryHasFileDrop(out bool hasFileDrop, out string? clipboardError))
        {
            ShowStatusMessage("クリップボードの確認に失敗しました");
            return;
        }
        if (!ShellClipboardService.TryHasImage(out bool hasImage, out string? imageClipboardError))
        {
            ShowStatusMessage("クリップボードの確認に失敗しました");
            return;
        }
        var pasteEntryPlan = _fileOperationEntryCoordinator.CreateClipboardPasteEntryPlan(
            _uiMode == UIMode.Browser,
            _isClipboardBusy,
            _fileOpCts != null,
            _fileOpCts?.IsCancellationRequested ?? false,
            hasFileDrop,
            hasImage,
            _navigationService.CurrentPath);
        if (!pasteEntryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(pasteEntryPlan.StatusMessage))
            {
                ShowStatusMessage(pasteEntryPlan.StatusMessage, 1000);
            }
            return;
        }
        if (hasFileDrop && hasImage)
        {
            var choice = _fileOperationDialogCoordinator.ChooseClipboardPasteMode(this);
            if (choice == ClipboardPasteChoice.Cancel)
            {
                ShowStatusMessage("貼り付けはキャンセルされました。");
                return;
            }
            if (choice == ClipboardPasteChoice.ClipboardImage)
            {
                ExecuteClipboardImagePaste();
                return;
            }
        }
        else if (!hasFileDrop && hasImage)
        {
            ExecuteClipboardImagePaste();
            return;
        }
        try
        {
            ShellClipboardService.TryGetSnapshot(out var beforeSnapshot, out _);
            if (!ShellClipboardService.TryGetFileDrop(out List<string> validPaths, out bool isCut))
            {
                ShowStatusMessage("クリップボードに有効なファイルがありません。");
                return;
            }
            string destDir = pasteEntryPlan.CurrentPath;
            string pasteOperationDisplayName = isCut ? "貼り付け(移動)" : "貼り付け(コピー)";
            CancellationToken token = PrepareFileOperation(pasteOperationDisplayName);
            int pasteStatusVersion = _fileOperationStatusVersion;
            ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage("Paste", validPaths.Count, destDir));
            IProgress<FileOperationProgress> progress = _fileOperationDialogCoordinator.CreatePasteProgress(
                isCut,
                message => ShowFileOperationStatusIfCurrent(
                    pasteStatusVersion,
                    (_fileOpCts?.IsCancellationRequested ?? false)
                        ? FileOperationPresentationHelper.GetCancelRequestedMessage(_activeFileOperationName ?? pasteOperationDisplayName)
                        : message));
            var result = await Task.Run(() =>
            {
                string? firstSuccessName = null;
                string? firstRenamedName = null;
                int successCount = 0;
                int skipCount = 0;
                int failCount = 0;
                int renamedCount = 0;
                bool wasCancelled = false;
                bool applyRenameCopyToAllSameDirectory = false;
                CopyCollisionDecision? applyToAllDecision = null;
                DirectoryMergeDecision? directoryApplyToAllDecision = null;
                foreach (var sourcePath in validPaths)
                {
                    if (token.IsCancellationRequested)
                    {
                        wasCancelled = true;
                        break;
                    }
                    string fileName = Path.GetFileName(sourcePath);
                    string destPath = Path.Combine(destDir, fileName);
                    progress.Report(new FileOperationProgress(successCount + skipCount + failCount + 1, validPaths.Count, fileName));
                    if (string.Equals(
                        NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(sourcePath) ?? string.Empty),
                        NavigationService.NormalizeDirectoryForCompare(destDir),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (!isCut)
                        {
                            string originalDestPath = destPath;
                            if (!applyRenameCopyToAllSameDirectory)
                            {
                                string suggestedPath = FileOperationService.GetUniquePath(destPath);
                                string suggestedName = Path.GetFileName(suggestedPath);
                                var sameDirDecision = (PasteSameDirectoryConfirmAction)this.Invoke(new Func<PasteSameDirectoryConfirmAction>(() =>
                                {
                                    ShowStatusMessage(FileOperationPresentationHelper.GetSameDirectoryAliasCopyConfirmationMessage(fileName, suggestedName));
                                    return _fileOperationDialogCoordinator.ConfirmPasteSameDirectory(this, fileName, suggestedName, validPaths.Count > 1);
                                }));
                                if (sameDirDecision == PasteSameDirectoryConfirmAction.Cancel)
                                {
                                    wasCancelled = true;
                                    break;
                                }
                                if (sameDirDecision == PasteSameDirectoryConfirmAction.No)
                                {
                                    skipCount++;
                                    continue;
                                }
                                if (sameDirDecision == PasteSameDirectoryConfirmAction.All)
                                {
                                    applyRenameCopyToAllSameDirectory = true;
                                }
                            }
                            ShowFileOperationProgressIfCurrent(
                                pasteStatusVersion,
                                pasteOperationDisplayName,
                                successCount + skipCount + failCount + 1,
                                validPaths.Count,
                                fileName,
                                usePasteProgress: true,
                                isCut: isCut);
                            destPath = FileOperationService.GetUniquePath(destPath);
                            fileName = Path.GetFileName(destPath);
                            if (!string.Equals(originalDestPath, destPath, StringComparison.OrdinalIgnoreCase))
                            {
                                renamedCount++;
                                firstRenamedName ??= fileName;
                            }
                        }
                        else
                        {
                            skipCount++;
                            continue;
                        }
                    }
                    bool sourceIsDir = Directory.Exists(sourcePath);
                    bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
                    bool overwriteMove = false;
                    if (destExists)
                    {
                        bool destIsDir = Directory.Exists(destPath);
                        if (sourceIsDir != destIsDir)
                        {
                            string conflictPath = destPath;
                            this.Invoke(() => _fileOperationDialogCoordinator.ShowTypeMismatchConflict(this, conflictPath));
                            failCount++;
                            continue;
                        }
                        if (sourceIsDir)
                        {
                            if (!TryResolvePasteDirectoryMerge(sourcePath, destPath, isCut, ref directoryApplyToAllDecision, out bool pasteShouldSkip, out bool pasteShouldCancel))
                            {
                                if (pasteShouldCancel)
                                {
                                    wasCancelled = true;
                                    break;
                                }
                                if (pasteShouldSkip)
                                {
                                    skipCount++;
                                    continue;
                                }
                            }
                            try
                            {
                                if (isCut)
                                {
                                    PasteMoveDirectoryIntoExisting(
                                        sourcePath,
                                        destPath,
                                        ref applyToAllDecision,
                                        out bool directoryShouldCancel,
                                        out int directorySkipCount,
                                        out int directoryFailCount);
                                    if (directoryShouldCancel)
                                    {
                                        wasCancelled = true;
                                        break;
                                    }
                                    skipCount += directorySkipCount;
                                    failCount += directoryFailCount;
                                }
                                else
                                {
                                    PasteCopyDirectoryIntoExisting(sourcePath, destPath, ref applyToAllDecision, out bool directoryShouldCancel);
                                    if (directoryShouldCancel)
                                    {
                                        wasCancelled = true;
                                        break;
                                    }
                                }
                                firstSuccessName ??= fileName;
                                successCount++;
                            }
                            catch (OperationCanceledException)
                            {
                                wasCancelled = true;
                                break;
                            }
                            catch (Exception ex)
                            {
                                string opErrName = isCut ? "貼り付け(移動)" : "貼り付け(コピー)";
                                LogService.Error($"{opErrName}フォルダ統合失敗: {fileName}", ex);
                                failCount++;
                            }
                            continue;
                        }
                        var collisionResolution = (PasteCollisionResolution)this.Invoke(() =>
                        {
                            ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage(
                                isCut ? "貼り付け(移動)" : "貼り付け(コピー)",
                                fileName));
                            return _fileOperationDialogCoordinator.ResolvePasteCollision(
                                this,
                                sourcePath,
                                destPath,
                                allowRename: !isCut,
                                isCut: isCut,
                                ref applyToAllDecision);
                        });
                        if (collisionResolution.ShouldCancel)
                        {
                            wasCancelled = true;
                            break;
                        }
                        if (collisionResolution.ShouldSkip)
                        {
                            skipCount++;
                            continue;
                        }
                        ShowFileOperationProgressIfCurrent(
                            pasteStatusVersion,
                            pasteOperationDisplayName,
                            successCount + skipCount + failCount + 1,
                            validPaths.Count,
                            fileName,
                            usePasteProgress: true,
                            isCut: isCut);
                        destPath = collisionResolution.DestinationPath;
                        fileName = Path.GetFileName(destPath);
                        overwriteMove = collisionResolution.OverwriteExisting;
                        if (collisionResolution.UsedRenameCopy)
                        {
                            renamedCount++;
                            firstRenamedName ??= collisionResolution.RenameTargetName ?? fileName;
                        }
                    }
                    try
                    {
                        if (isCut)
                        {
                            FileOperationService.Move(sourcePath, destPath, overwriteMove, suppressLogging: validPaths.Count > 100);
                        }
                        else
                        {
                            FileOperationService.Copy(sourcePath, destPath);
                        }
                        firstSuccessName ??= fileName;
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        string opErrName = isCut ? "切り取り(移動)" : "コピー";
                        LogService.Error($"{opErrName}失敗: {fileName}", ex);
                        failCount++;
                    }
                }
                return (successCount, skipCount, failCount, wasCancelled, firstSuccessName, renamedCount, firstRenamedName);
            }, token);
            if (isCut && !result.wasCancelled && result.successCount > 0 && result.failCount == 0 && result.skipCount == 0 && beforeSnapshot != null)
            {
                if (ShellClipboardService.TryGetSnapshot(out var afterSnapshot, out _) &&
                    ShellClipboardService.IsSameCutSnapshot(beforeSnapshot, afterSnapshot))
                {
                    ShellClipboardService.TryClear(out _);
                }
            }
            if (result.wasCancelled)
            {
                var canceledResult = new FileOperationResult("Paste", FileOpExitStatus.Canceled, result.successCount, validPaths.Count, result.firstSuccessName,
                    skipCount: result.skipCount, failCount: result.failCount);
                string cancelMsg = FileOperationPresentationHelper.GetPasteResultStatusMessage(
                    canceledResult,
                    isCut,
                    result.renamedCount,
                    result.firstRenamedName,
                    preserveClipboardOnIncomplete: true);
                HandlePostOperation(new FileOperationResult("Paste", FileOpExitStatus.Canceled, result.successCount, validPaths.Count, result.firstSuccessName,
                    customMessage: cancelMsg, skipCount: result.skipCount, failCount: result.failCount));
                return;
            }
            FileOpExitStatus pasteExitStatus = FileOperationPresentationHelper.NormalizeExitStatus(
                FileOpExitStatus.Success,
                result.successCount,
                validPaths.Count,
                result.skipCount,
                result.failCount);
            var pasteResult = new FileOperationResult("Paste", pasteExitStatus, result.successCount, validPaths.Count, result.firstSuccessName,
                skipCount: result.skipCount, failCount: result.failCount);
            string resultMsg = FileOperationPresentationHelper.GetPasteResultStatusMessage(
                pasteResult,
                isCut,
                result.renamedCount,
                result.firstRenamedName,
                preserveClipboardOnIncomplete: true);
            HandlePostOperation(new FileOperationResult("Paste", pasteExitStatus, result.successCount, validPaths.Count, result.firstSuccessName,
                customMessage: resultMsg, skipCount: result.skipCount, failCount: result.failCount));
        }
        catch (OperationCanceledException)
        {
            HandlePostOperation(new FileOperationResult("Paste", FileOpExitStatus.Canceled, 0, 0, customMessage: "貼り付けを中断しました。"));
        }
        catch (Exception ex)
        {
            LogService.Error("貼り付け処理中に致命的なエラーが発生しました", ex);
            HandlePostOperation(new FileOperationResult("Paste", FileOpExitStatus.Error, 0, 0));
        }
    }
    private void HandlePostOperation(FileOperationResult result)
    {
        var totalStopwatch = Stopwatch.StartNew();
        long finalizeMs = 0;
        long clearPreviewMs = 0;
        long reloadMs = 0;
        long refreshMarksMs = 0;
        long clearMarksMs = 0;
        long statusMs = 0;
        var plan = _fileOperationPostOperationCoordinator.CreatePlan(
            result,
            _settings.FileOperations?.ReloadAfterFileOperation ?? true,
            _navigationService.CurrentPath);
        if (plan.ShouldFinalizeBusy)
        {
            var sw = Stopwatch.StartNew();
            FinalizeFileOperation();
            sw.Stop();
            finalizeMs = sw.ElapsedMilliseconds;
        }
        if (plan.ShouldClearPreview)
        {
            var sw = Stopwatch.StartNew();
            ClearPreview();
            sw.Stop();
            clearPreviewMs = sw.ElapsedMilliseconds;
        }
        if (plan.ShouldReloadCurrentDirectory)
        {
            var sw = Stopwatch.StartNew();
            LoadDirectory(_navigationService.CurrentPath, plan.NextFocusTarget);
            sw.Stop();
            reloadMs = sw.ElapsedMilliseconds;
        }
        else if (plan.ShouldRefreshMarks)
        {
            var sw = Stopwatch.StartNew();
            RefreshMarkUi();
            sw.Stop();
            refreshMarksMs = sw.ElapsedMilliseconds;
        }
        if (plan.ShouldClearMarks)
        {
            var sw = Stopwatch.StartNew();
            ClearMarks();
            sw.Stop();
            clearMarksMs = sw.ElapsedMilliseconds;
        }
        var statusStopwatch = Stopwatch.StartNew();
        ShowStatusMessage(plan.StatusMessage);
        statusStopwatch.Stop();
        statusMs = statusStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        LogService.Info(
            $"[Perf] FileOperationPostOperation operation={result.OperationName} status={result.ExitStatus} " +
            $"total={totalStopwatch.ElapsedMilliseconds}ms finalize={finalizeMs}ms clearPreview={clearPreviewMs}ms " +
            $"reload={reloadMs}ms refreshMarks={refreshMarksMs}ms clearMarks={clearMarksMs}ms status={statusMs}ms " +
            $"reloadApplied={plan.ShouldReloadCurrentDirectory} focusTarget={plan.NextFocusTarget ?? "<none>"}");
    }
    private string? GetCreatedItemFocusTarget(string? fileName)
    {
        if (!(_settings.FileOperations?.SelectCreatedItemAfterCreate ?? true))
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }
    private bool IsCurrentFileOperationStatusVersion(int statusVersion)
    {
        return _isClipboardBusy && statusVersion == _fileOperationStatusVersion;
    }
    private void ShowFileOperationStatusIfCurrent(int statusVersion, string message)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        // busy feedback などの一時優先メッセージが表示されている間は進捗更新をスキップする
        if (DateTime.UtcNow < _statusNoticeHoldUntilUtc)
        {
            return;
        }
        ShowStatusMessage(message);
    }
    private void ShowFileOperationProgressIfCurrent(
        int statusVersion,
        string operationDisplayName,
        int processedCount,
        int totalCount,
        string currentFileName,
        bool usePasteProgress = false,
        bool isCut = false)
    {
        string message = (_fileOpCts?.IsCancellationRequested ?? false)
            ? FileOperationPresentationHelper.GetCancelRequestedMessage(_activeFileOperationName ?? operationDisplayName)
            : usePasteProgress
                ? FileOperationPresentationHelper.GetPasteProgressMessage(isCut, processedCount, totalCount, currentFileName)
                : FileOperationPresentationHelper.GetOperationProgressMessage(operationDisplayName, processedCount, totalCount, currentFileName);
        ShowFileOperationStatusIfCurrent(statusVersion, message);
    }
    private CancellationToken PrepareFileOperation(string? operationName = null)
    {
        _fileOperationStatusVersion++;
        _isClipboardBusy = true;
        _activeFileOperationName = operationName;
        UpdateMenuStripState();
        _fileOpCts?.Cancel();
        _fileOpCts?.Dispose();
        _fileOpCts = new CancellationTokenSource();
        _fileOperationCancelRequestedTimestamp = 0;
        return _fileOpCts.Token;
    }
    private void FinalizeFileOperation()
    {
        _fileOperationStatusVersion++;
        _fileOpCts?.Dispose();
        _fileOpCts = null;
        _isClipboardBusy = false;
        _activeFileOperationName = null;
        UpdateMenuStripState();
        TryProcessPendingCurrentDirectoryRefresh("FinalizeFileOperation");
    }
    private bool TryResolveCopyCollision(
        string sourcePath,
        ref string destPath,
        ref CopyCollisionDecision? applyToAllDecision,
        out CopyCollisionPolicy appliedPolicy,
        out bool shouldSkip,
        out bool shouldCancel)
    {
        appliedPolicy = CopyCollisionPolicy.Cancel;
        shouldSkip = false;
        shouldCancel = false;
        bool sourceIsDir = Directory.Exists(sourcePath);
        bool destIsDir = Directory.Exists(destPath);
        if (sourceIsDir != destIsDir)
        {
            string conflictPath = destPath;
            this.Invoke(() => _fileOperationDialogCoordinator.ShowTypeMismatchConflict(this, conflictPath));
            shouldSkip = true;
            return false;
        }
        if (sourceIsDir)
        {
            this.Invoke(() => _fileOperationDialogCoordinator.ShowUnsupportedDirectoryOverwrite(this));
            shouldSkip = true;
            return false;
        }
        var decision = applyToAllDecision;
        if (decision == null)
        {
            string dialogDestPath = destPath;
            string targetName = Path.GetFileName(dialogDestPath);
            decision = (CopyCollisionDecision)this.Invoke(() =>
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage("コピー", targetName));
                return _fileOperationDialogCoordinator.ShowCopyCollision(this, sourcePath, dialogDestPath);
            });
            if (decision.ApplyToAll && decision.Policy != CopyCollisionPolicy.Cancel)
            {
                applyToAllDecision = new CopyCollisionDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }
        switch (decision.Policy)
        {
            case CopyCollisionPolicy.NewerOnly:
                appliedPolicy = CopyCollisionPolicy.NewerOnly;
                var sourceTime = File.GetLastWriteTimeUtc(sourcePath);
                var destTime = File.GetLastWriteTimeUtc(destPath);
                shouldSkip = sourceTime <= destTime;
                return !shouldSkip;
            case CopyCollisionPolicy.RenameCopy:
                appliedPolicy = CopyCollisionPolicy.RenameCopy;
                destPath = FileOperationService.GetUniquePathStartingAtOne(destPath);
                return true;
            case CopyCollisionPolicy.Overwrite:
                appliedPolicy = CopyCollisionPolicy.Overwrite;
                return true;
            case CopyCollisionPolicy.Skip:
                appliedPolicy = CopyCollisionPolicy.Skip;
                shouldSkip = true;
                return false;
            default:
                shouldCancel = true;
                return false;
        }
    }
    private void ExecuteClipboardImagePaste()
    {
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        if (_uiMode != UIMode.Browser)
        {
            ShowStatusMessage("この画面では貼り付けできません");
            return;
        }
        if (_isClipboardBusy)
        {
            ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                "貼り付け",
                canCancel: _fileOpCts != null,
                isCancelRequested: _fileOpCts?.IsCancellationRequested ?? false));
            return;
        }
        if (string.IsNullOrEmpty(_navigationService.CurrentPath))
        {
            return;
        }
        _isClipboardBusy = true;
        try
        {
            if (!ShellClipboardService.TryGetImage(out var image, out string? imageError) || image == null)
            {
                LogBrowserImageImportWarn($"Source=ClipboardImageUnavailable Error={imageError ?? "<none>"}");
                ShowStatusMessage("クリップボードに画像がありません");
                return;
            }
            using (image)
            {
                string savedPath = ClipboardImagePasteService.SavePngToDirectory(image, _navigationService.CurrentPath);
                string fileName = Path.GetFileName(savedPath);
                LoadDirectory(_navigationService.CurrentPath, GetCreatedItemFocusTarget(fileName));
                LogBrowserImageImportInfo($"Source=ClipboardImage Saved={savedPath}");
                ShowStatusMessage($"画像を PNG として貼り付けました: {fileName}");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("クリップボード画像の貼り付けに失敗しました", ex);
            ShowStatusMessage($"画像貼り付け失敗: {ex.Message}");
        }
        finally
        {
            _isClipboardBusy = false;
        }
    }
    private bool TryResolvePasteDirectoryMerge(
        string sourcePath,
        string destPath,
        bool isCut,
        ref DirectoryMergeDecision? applyToAllDecision,
        out bool shouldSkip,
        out bool shouldCancel)
    {
        shouldSkip = false;
        shouldCancel = false;
        var guard = FileOperationService.AnalyzeDirectoryPasteMerge(sourcePath, destPath, isCut);
        if (!guard.CanMerge)
        {
            this.Invoke(() => _fileOperationDialogCoordinator.ShowInformationDialog(
                this,
                guard.Message,
                isCut ? "貼り付け(移動)エラー" : "貼り付け(コピー)エラー"));
            shouldSkip = true;
            return false;
        }
        var decision = applyToAllDecision;
        if (decision == null)
        {
            string targetName = Path.GetFileName(destPath);
            decision = (DirectoryMergeDecision)this.Invoke(() =>
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage(
                    isCut ? "貼り付け(移動)" : "貼り付け(コピー)",
                    targetName));
                return _fileOperationDialogCoordinator.ShowPasteDirectoryMerge(this, sourcePath, destPath, isCut);
            });
            if (decision.ApplyToAll && decision.Policy != DirectoryMergePolicy.Cancel)
            {
                applyToAllDecision = new DirectoryMergeDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }
        switch (decision.Policy)
        {
            case DirectoryMergePolicy.Merge:
                return true;
            case DirectoryMergePolicy.Skip:
                shouldSkip = true;
                return false;
            default:
                shouldCancel = true;
                return false;
        }
    }
    private bool TryResolveCopyDirectoryMerge(
        string sourcePath,
        string destPath,
        ref DirectoryMergeDecision? applyToAllDecision,
        out bool shouldSkip,
        out bool shouldCancel)
    {
        shouldSkip = false;
        shouldCancel = false;
        var decision = applyToAllDecision;
        if (decision == null)
        {
            string targetName = Path.GetFileName(destPath);
            decision = (DirectoryMergeDecision)this.Invoke(() =>
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage("コピー", targetName));
                return _fileOperationDialogCoordinator.ShowCopyDirectoryMerge(this, sourcePath, destPath);
            });
            if (decision.ApplyToAll && decision.Policy != DirectoryMergePolicy.Cancel)
            {
                applyToAllDecision = new DirectoryMergeDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }
        switch (decision.Policy)
        {
            case DirectoryMergePolicy.Merge:
                return true;
            case DirectoryMergePolicy.Skip:
                shouldSkip = true;
                return false;
            default:
                shouldCancel = true;
            return false;
        }
    }
    private bool TryResolveMoveDirectoryMerge(
        string sourcePath,
        string destPath,
        ref DirectoryMergeDecision? applyToAllDecision,
        out bool shouldSkip,
        out bool shouldCancel)
    {
        shouldSkip = false;
        shouldCancel = false;
        var guard = FileOperationService.AnalyzeDirectoryMoveMergePractical(sourcePath, destPath);
        if (!guard.CanMerge)
        {
            this.Invoke(() => _fileOperationDialogCoordinator.ShowInformationDialog(this, guard.Message, "移動エラー"));
            shouldSkip = true;
            return false;
        }
        var decision = applyToAllDecision;
        if (decision == null)
        {
            string targetName = Path.GetFileName(destPath);
            decision = (DirectoryMergeDecision)this.Invoke(() =>
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage("移動", targetName));
                return _fileOperationDialogCoordinator.ShowMoveDirectoryMerge(this, sourcePath, destPath);
            });
            if (decision.ApplyToAll && decision.Policy != DirectoryMergePolicy.Cancel)
            {
                applyToAllDecision = new DirectoryMergeDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }
        switch (decision.Policy)
        {
            case DirectoryMergePolicy.Merge:
                return true;
            case DirectoryMergePolicy.Skip:
                shouldSkip = true;
                return false;
            default:
                shouldCancel = true;
                return false;
        }
    }
    private void CopyDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        CancellationToken token)
    {
        foreach (var entry in FileOperationService.BuildDirectoryCopyPlan(sourceDir, destinationDir))
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(entry.DestinationPath);
                continue;
            }
            string destinationPath = entry.DestinationPath;
            bool destExists = File.Exists(destinationPath) || Directory.Exists(destinationPath);
            if (destExists)
            {
                if (!TryResolveCopyCollision(entry.SourcePath, ref destinationPath, ref fileApplyToAllDecision, out _, out bool shouldSkip, out bool shouldCancel))
                {
                    if (shouldCancel)
                    {
                        throw new OperationCanceledException(token);
                    }
                    if (shouldSkip)
                    {
                        continue;
                    }
                }
            }
            FileOperationService.Copy(entry.SourcePath, destinationPath);
        }
    }
    private void PasteCopyDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        out bool shouldCancel)
    {
        shouldCancel = false;
        foreach (var entry in FileOperationService.BuildDirectoryCopyPlan(sourceDir, destinationDir))
        {
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(entry.DestinationPath);
                continue;
            }
            string destinationPath = entry.DestinationPath;
            bool destExists = File.Exists(destinationPath) || Directory.Exists(destinationPath);
            if (destExists)
            {
                var collisionResolution = _fileOperationDialogCoordinator.ResolvePasteCollision(
                    this,
                    entry.SourcePath,
                    destinationPath,
                    allowRename: true,
                    isCut: false,
                    ref fileApplyToAllDecision);
                if (collisionResolution.ShouldCancel)
                {
                    shouldCancel = true;
                    return;
                }
                if (collisionResolution.ShouldSkip)
                {
                    continue;
                }
                destinationPath = collisionResolution.DestinationPath;
            }
            FileOperationService.Copy(entry.SourcePath, destinationPath);
        }
    }
    private void PasteMoveDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        out bool shouldCancel,
        out int skipCount,
        out int failCount)
    {
        MoveDirectoryIntoExistingWithCollisionResolution(
            sourceDir,
            destinationDir,
            ref fileApplyToAllDecision,
            "貼り付け(移動)",
            out shouldCancel,
            out skipCount,
            out failCount);
    }
    private void DirectMoveDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        out bool shouldCancel,
        out int skipCount,
        out int failCount)
    {
        MoveDirectoryIntoExistingWithCollisionResolution(
            sourceDir,
            destinationDir,
            ref fileApplyToAllDecision,
            "移動",
            out shouldCancel,
            out skipCount,
            out failCount);
    }
    private void MoveDirectoryIntoExistingWithCollisionResolution(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        string operationLogLabel,
        out bool shouldCancel,
        out int skipCount,
        out int failCount)
    {
        shouldCancel = false;
        skipCount = 0;
        failCount = 0;
        IReadOnlyList<DirectoryCopyPlanEntry> copyPlan = FileOperationService.BuildDirectoryCopyPlan(sourceDir, destinationDir);
        bool suppressItemSuccessLogs = copyPlan.Count > 100;
        foreach (var entry in copyPlan)
        {
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(entry.DestinationPath);
                continue;
            }
            string destinationPath = entry.DestinationPath;
            bool overwriteMove = false;
            bool destExists = File.Exists(destinationPath) || Directory.Exists(destinationPath);
            if (destExists)
            {
                var collisionResolution = PasteCollisionResolver.Resolve(
                    this,
                    entry.SourcePath,
                    destinationPath,
                    allowRename: false,
                    isCut: true,
                    ref fileApplyToAllDecision);
                if (collisionResolution.ShouldCancel)
                {
                    shouldCancel = true;
                    return;
                }
                if (collisionResolution.ShouldSkip)
                {
                    skipCount++;
                    continue;
                }
                destinationPath = collisionResolution.DestinationPath;
                overwriteMove = collisionResolution.OverwriteExisting;
            }
            try
            {
                FileOperationService.Move(entry.SourcePath, destinationPath, overwriteMove, suppressLogging: suppressItemSuccessLogs);
            }
            catch (Exception ex)
            {
                LogService.Error($"{operationLogLabel}フォルダ統合失敗: {Path.GetFileName(entry.SourcePath)}", ex);
                failCount++;
            }
        }
        DeleteEmptyDirectoriesBottomUp(sourceDir);
    }
    private static void DeleteEmptyDirectoriesBottomUp(string rootDir)
    {
        if (!Directory.Exists(rootDir))
        {
            return;
        }
        foreach (string directoryPath in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath, false);
            }
        }
        if (Directory.Exists(rootDir) && !Directory.EnumerateFileSystemEntries(rootDir).Any())
        {
            Directory.Delete(rootDir, false);
        }
    }
    private bool TryExtractSevenZipProgress(string line, out string percent)
    {
        percent = string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)%");
        if (match.Success)
        {
            percent = match.Groups[1].Value;
            return true;
        }
        return false;
    }
    /// <summary>
    /// Phase 5-viewer-ux1: Viewer の現在状態（エンコーディング・折り返し）をまとめた statusLabel 用の文字列を生成する。
    /// </summary>
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
        if (_currentViewerKind == PreviewKind.Text && !string.IsNullOrWhiteSpace(_currentViewerDetectedEncodingLabel))
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
            && (_currentViewerKind == PreviewKind.Text || _currentViewerKind == PreviewKind.Binary);
    }
    private void NormalizeStatusLabelLayout()
    {
        if (statusStrip == null || statusStrip.IsDisposed ||
            statusLabel == null || statusLabel.IsDisposed)
        {
            return;
        }
        // 縦方向の欠けを防止するため、フォント高さに基づいて StatusStrip の高さを確保する。
        // 目安としてフォント高さ + 6px (上下 3px ずつ) 程度を確保する。最小 24px。
        int desiredHeight = Math.Max(24, statusStrip.Font.Height + 6);
        if (statusStrip.AutoSize || statusStrip.Height < desiredHeight)
        {
            // AutoSize が ON だと Height 指定が効かない場合があるため
            statusStrip.AutoSize = false;
            statusStrip.Height = desiredHeight;
        }
        statusLabel.Alignment = ToolStripItemAlignment.Left;
        statusLabel.Overflow = ToolStripItemOverflow.Never;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        // ToolStripItem 特有の不定な余白を排除し、Padding で位置を安定させる。
        statusLabel.Margin = Padding.Empty;
        statusLabel.Padding = new Padding(0, 1, 0, 1);
        // StatusStrip内で利用可能幅を取らせ、長い文字列は右側でクリップさせる。
        statusLabel.Spring = true;
        // Springだけで安定しない場合に備え、明示幅も保険として設定する。
        // SizingGripや余白分を少し差し引く。
        int gripReserve = statusStrip.SizingGrip ? 20 : 0;
        int width = Math.Max(
            50,
            statusStrip.ClientSize.Width
            - statusLabel.Margin.Horizontal
            - gripReserve
            - 4);
        statusLabel.AutoSize = false;
        statusLabel.Width = width;
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
        LogService.Info(
            $"[LargeTextEntryTiming] {stage} elapsedMs={sw.ElapsedMilliseconds} " +
            $"totalElapsedMs={_largeTextEntryStopwatch.ElapsedMilliseconds} " +
            $"reqId={reqId} uiMode={_uiMode} kind={kind} " +
            $"requestPath='{path}' " +
            $"currentPath='{currentPath ?? "<not-read>"}' " +
            $"enc='{state?.DetectedEncodingLabel ?? "<null>"}' " +
            $"hasBom={state?.HasBom.ToString() ?? "<null>"} " +
            $"offsets={state?.LineOffsets.Count ?? -1} " +
            $"isIndexing={state?.IsIndexing.ToString() ?? "<null>"} " +
            $"status='{statusText}'");
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
        if (_currentViewerKind != PreviewKind.LargeText || _largeFileState == null)
            return false;
        if (_largeFileControl.TryGetCharacterSelectionRange(out var rawRange))
        {
            var range = NormalizeCharacterSelectionRange(rawRange);
            _ = TryCopyLargeFileCharacterSelectionAsync(range, _previewCts?.Token ?? CancellationToken.None);
            return true;
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
            var result = MessageBox.Show(
                $"{lineCount:N0} 行 / 約 {FileOperationService.FormatSize(estimatedBytes)} の選択範囲です。\n" +
                "クリップボードへは大きすぎるため、直接コピーしません。\n\n" +
                "選択範囲をファイルへ保存しますか？",
                "LargeText 大量コピー",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
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
        string? error = ExternalToolService.ExecuteShell(_navigationService.CurrentPath, $"\"{fullPath}\"");
        if (error != null)
        {
            ShowStatusMessage(error);
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
            using var dialog = new ArchiveListDialog(archivePath, result.Entries, _navigationService.CurrentPath, isReadOnly);
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
        if (GuardClipboardBusy("処理中のため archive を解凍できません"))
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
    private async Task ExecuteCopy()
    {
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            _activeFileOperationName,
            _fileOpCts != null,
            "コピー",
            ResolveSelection(),
            "コピー対象がありません。",
            busyOperationName: "Copy",
            isCancelRequested: _fileOpCts?.IsCancellationRequested ?? false);
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
                out string destDir,
                out bool copyNeedsCreateDirectory))
        {
            return;
        }
        if (copyNeedsCreateDirectory && GuardReadOnlyBrowserTab("フォルダ作成"))
        {
            return;
        }
        if (!_fileOperationDialogCoordinator.EnsureDestinationDirectory(this, destDir, copyNeedsCreateDirectory))
        {
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
        var token = PrepareFileOperation(entryPlan.BusyOperationName);
        int copyStatusVersion = _fileOperationStatusVersion;
        ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage("Copy", totalCount, destDir));
        IProgress<FileOperationProgress> progress = _fileOperationDialogCoordinator.CreateOperationProgress(
            "Copy",
            message => ShowFileOperationStatusIfCurrent(
                copyStatusVersion,
                (_fileOpCts?.IsCancellationRequested ?? false)
                    ? FileOperationPresentationHelper.GetCancelRequestedMessage(_activeFileOperationName ?? "Copy")
                    : message));
        try
        {
            var result = await Task.Run(() =>
            {
                int currentSuccess = 0;
                FileOpExitStatus status = FileOpExitStatus.Success;
                CopyCollisionDecision? fileApplyToAllDecision = null;
                DirectoryMergeDecision? directoryApplyToAllDecision = null;
                bool applyRenameCopyToAllSameDirectory = false;
                int currentSkipCount = 0;
                int currentFailCount = 0;
                foreach (var sourcePath in selection.FullPaths)
                {
                    if (token.IsCancellationRequested)
                    {
                        status = FileOpExitStatus.Canceled;
                        break;
                    }
                    string fileName = Path.GetFileName(sourcePath);
                    string destPath = Path.Combine(destDir, fileName);
                    progress.Report(new FileOperationProgress(currentSuccess + 1, totalCount, fileName));
                    bool sourceIsDir = Directory.Exists(sourcePath);
                    bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
                    bool isSameDirectoryCopy = string.Equals(
                        NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(sourcePath) ?? string.Empty),
                        NavigationService.NormalizeDirectoryForCompare(destDir),
                        StringComparison.OrdinalIgnoreCase);
                    if (isSameDirectoryCopy)
                    {
                        string originalDestPath = destPath;
                        if (!applyRenameCopyToAllSameDirectory)
                        {
                            string suggestedPath = FileOperationService.GetUniquePath(destPath);
                            string suggestedName = Path.GetFileName(suggestedPath);
                            var sameDirDecision = (PasteSameDirectoryConfirmAction)this.Invoke(new Func<PasteSameDirectoryConfirmAction>(() =>
                            {
                                ShowStatusMessage(FileOperationPresentationHelper.GetSameDirectoryAliasCopyConfirmationMessage(fileName, suggestedName));
                                return _fileOperationDialogCoordinator.ConfirmPasteSameDirectory(this, fileName, suggestedName, selection.Count > 1);
                            }));
                            if (sameDirDecision == PasteSameDirectoryConfirmAction.Cancel)
                            {
                                status = FileOpExitStatus.Canceled;
                                break;
                            }
                            if (sameDirDecision == PasteSameDirectoryConfirmAction.No)
                            {
                                currentSkipCount++;
                                continue;
                            }
                            if (sameDirDecision == PasteSameDirectoryConfirmAction.All)
                            {
                                applyRenameCopyToAllSameDirectory = true;
                            }
                        }
                        destPath = FileOperationService.GetUniquePath(destPath);
                        fileName = Path.GetFileName(destPath);
                        if (!string.Equals(originalDestPath, destPath, StringComparison.OrdinalIgnoreCase))
                        {
                            destExists = false;
                        }
                    }
                    if (destExists)
                    {
                        bool destIsDir = Directory.Exists(destPath);
                        if (sourceIsDir && destIsDir)
                        {
                            if (!TryResolveCopyDirectoryMerge(sourcePath, destPath, ref directoryApplyToAllDecision, out bool mergeShouldSkip, out bool mergeShouldCancel))
                            {
                                if (mergeShouldCancel)
                                {
                                    status = FileOpExitStatus.Canceled;
                                    break;
                                }
                                if (mergeShouldSkip)
                                {
                                    currentSkipCount++;
                                    continue;
                                }
                            }
                            try
                            {
                                CopyDirectoryIntoExisting(sourcePath, destPath, ref fileApplyToAllDecision, token);
                                currentSuccess++;
                                this.Invoke(() => UnmarkPath(sourcePath)); // 成功した分だけマークを外す
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
                                break;
                            }
                            continue;
                        }
                        if (!TryResolveCopyCollision(sourcePath, ref destPath, ref fileApplyToAllDecision, out _, out bool shouldSkip, out bool shouldCancel))
                        {
                            if (shouldCancel)
                            {
                                status = FileOpExitStatus.Canceled;
                                break;
                            }
                            if (shouldSkip)
                            {
                                currentSkipCount++;
                                continue;
                            }
                        }
                    }
                    try
                    {
                        FileOperationService.Copy(sourcePath, destPath);
                        currentSuccess++;
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
            successCount = result.currentSuccess;
            skipCount = result.currentSkipCount;
            failCount = result.currentFailCount;
            exitStatus = FileOperationPresentationHelper.NormalizeExitStatus(result.status, successCount, selection.Count, skipCount, failCount);
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
    private async Task ExecuteMove()
    {
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            _activeFileOperationName,
            _fileOpCts != null,
            "移動",
            ResolveSelection(),
            "移動対象がありません。",
            busyOperationName: "Move",
            isCancelRequested: _fileOpCts?.IsCancellationRequested ?? false);
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
                out string normalizedDestDir,
                out bool moveNeedsCreateDirectory))
        {
            return;
        }
        if (!_fileOperationDialogCoordinator.EnsureDestinationDirectory(this, normalizedDestDir, moveNeedsCreateDirectory))
        {
            return;
        }
        // 操作後に一気に一番上まで戻るのを防ぐため、あらかじめ次にフォーカスすべき対象を見つけておく
        string? nextTargetName = GetNextFocusTarget(selection.FullPaths.ToList());
        int successCount = 0;
        int totalCount = selection.FullPaths.Count;
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;
        int aggregateSkipCount = 0;
        int aggregateFailCount = 0;
        bool shouldClearMarks = true;
        IReadOnlyList<FileOperationUndoRedoItem> moveUndoItems = Array.Empty<FileOperationUndoRedoItem>();
        string? moveResultMessage = null;
        // 非同期実行の準備
        var token = PrepareFileOperation(entryPlan.BusyOperationName);
        int moveStatusVersion = _fileOperationStatusVersion;
        ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage("Move", totalCount, normalizedDestDir));
        IProgress<FileOperationProgress> progress = _fileOperationDialogCoordinator.CreateOperationProgress(
            "Move",
            message => ShowFileOperationStatusIfCurrent(
                moveStatusVersion,
                (_fileOpCts?.IsCancellationRequested ?? false)
                    ? FileOperationPresentationHelper.GetCancelRequestedMessage(_activeFileOperationName ?? "Move")
                    : message));
        try
        {
            var result = await Task.Run(() =>
            {
                int currentSuccess = 0;
                FileOpExitStatus status = FileOpExitStatus.Success;
                CopyCollisionDecision? fileApplyToAllDecision = null;
                DirectoryMergeDecision? directoryApplyToAllDecision = null;
                int currentSkipCount = 0;
                int currentFailCount = 0;
                bool clearMarks = true;
                bool canRecordUndoBatch = true;
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
                foreach (var sourcePath in selection.FullPaths)
                {
                    if (token.IsCancellationRequested)
                    {
                        status = FileOpExitStatus.Canceled;
                        break;
                    }
                    string fileName = Path.GetFileName(sourcePath);
                    string destPath = Path.Combine(normalizedDestDir, fileName);
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
                    var destinationCheckSw = Stopwatch.StartNew();
                    bool sourceIsDir = Directory.Exists(sourcePath);
                    bool destIsDir = Directory.Exists(destPath);
                    bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
                    destinationCheckSw.Stop();
                    destinationCheckTotalMs += destinationCheckSw.ElapsedMilliseconds;
                    CopyCollisionPolicy appliedPolicy = CopyCollisionPolicy.Skip;
                    if (destExists)
                    {
                        collisionCheckCount++;
                        if (sourceIsDir && destIsDir)
                        {
                            collisionDialogCount++;
                            canRecordUndoBatch = false;
                            if (!TryResolveMoveDirectoryMerge(sourcePath, destPath, ref directoryApplyToAllDecision, out bool mergeShouldSkip, out bool mergeShouldCancel))
                            {
                                if (mergeShouldCancel)
                                {
                                    status = FileOpExitStatus.Canceled;
                                    canRecordUndoBatch = false;
                                    break;
                                }
                                if (mergeShouldSkip)
                                {
                                    currentSkipCount++;
                                    clearMarks = false;
                                    canRecordUndoBatch = false;
                                    continue;
                                }
                            }
                            var directoryMoveSw = Stopwatch.StartNew();
                            DirectMoveDirectoryIntoExisting(
                                sourcePath,
                                destPath,
                                ref fileApplyToAllDecision,
                                out bool directoryShouldCancel,
                                out int directorySkipCount,
                                out int directoryFailCount);
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
                        collisionDialogCount++;
                        if (!TryResolveCopyCollision(sourcePath, ref destPath, ref fileApplyToAllDecision, out appliedPolicy, out bool shouldSkip, out bool shouldCancel))
                        {
                            if (shouldCancel)
                            {
                                status = FileOpExitStatus.Canceled;
                                canRecordUndoBatch = false;
                                break;
                            }
                            if (shouldSkip)
                            {
                                currentSkipCount++;
                                clearMarks = false;
                                canRecordUndoBatch = false;
                                continue;
                            }
                        }
                    }
                    try
                    {
                        bool overwrite = appliedPolicy == CopyCollisionPolicy.Overwrite;
                        if (overwrite)
                        {
                            canRecordUndoBatch = false;
                        }
                        var moveCallSw = Stopwatch.StartNew();
                        FileOperationService.Move(sourcePath, destPath, overwrite, suppressLogging: suppressItemSuccessLogs);
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
            successCount = result.currentSuccess;
            exitStatus = FileOperationPresentationHelper.NormalizeExitStatus(result.status, result.currentSuccess, selection.Count, result.currentSkipCount, result.currentFailCount);
            aggregateSkipCount = result.currentSkipCount;
            aggregateFailCount = result.currentFailCount;
            shouldClearMarks = result.clearMarks;
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
        var form = new FileOperationProgressFallbackForm(operationName, totalCount, () =>
        {
            RequestActiveFileOperationCancel($"{operationName}ProgressFallback");
        });
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_archiveProgressFallback, form))
            {
                _archiveProgressFallback = null;
            }
            ScheduleBrowserFocusReturnAfterFileOperation("ArchiveProgressFallbackClosed");
        };
        PositionProgressFallbackForm(form);
        _archiveProgressFallback = form;
        form.Show(this);
        form.UpdateState($"{operationName}中", "準備中...", indeterminate: true, _fileOpCts?.IsCancellationRequested ?? false);
    }
    private void UpdateArchiveProgressFallbackState(string operationName, string detail, bool indeterminate = true)
    {
        _archiveProgressFallback?.UpdateState(
            $"{operationName}中",
            detail,
            indeterminate,
            _fileOpCts?.IsCancellationRequested ?? false);
    }
    private void CompleteArchiveProgressFallback(string message)
    {
        _archiveProgressFallback?.Complete(message);
    }
    private void CloseArchiveProgressFallback()
    {
        var form = _archiveProgressFallback;
        _archiveProgressFallback = null;
        if (form != null && !form.IsDisposed)
        {
            form.Close();
        }
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
        return selection.Count > 1;
    }
    private async Task ExecutePack(bool forcePackEachFolderIndividually = false)
    {
        if (GuardReadOnlyBrowserTab("圧縮")) return;
        var selection = ResolveSelection();
        if (selection.Count == 0)
        {
            ShowStatusMessage("圧縮(Pack)対象がありません。");
            return;
        }
        string defaultName = BuildPackDefaultArchiveName(selection, _navigationService.CurrentPath);
        string selectionSummary = BuildPackSelectionSummary(selection);
        bool canPackEachFolder = CanPackEachFolderIndividually(selection);
        string? exePath = SevenZipService.ResolveExecutable(_settings.SevenZip?.ExePath);
        bool hasSevenZip = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath);
        IReadOnlyList<PackArchiveFormat> availableFormats;
        string hintText;
        if (hasSevenZip)
        {
            availableFormats = new[]
            {
                PackArchiveFormat.Zip,
                PackArchiveFormat.SevenZip,
                PackArchiveFormat.Tar
            };
            hintText = "zip / 7z / tar を扱います。個別圧縮は複数対象のとき有効です。";
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
        if ((request.Format == PackArchiveFormat.GZip || request.Format == PackArchiveFormat.BZip2 || request.Format == PackArchiveFormat.Xz) && selection.Count != 1)
        {
            MessageBox.Show(
                this,
                "gzip / bzip2 / xz は単一ファイルの圧縮のみ対応です。複数項目を圧縮する場合は zip / 7z / tar を選択してください。",
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
        PackOverwriteBackupSession? overwriteBackup = null;
        string? overwriteCleanupErrorMessage = null;
        if (GuardClipboardBusy()) return;
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
            if (exitStatus == FileOpExitStatus.Success)
            {
                ClearMarks();
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
    private async Task ExecuteHashAsync(SevenZipHashAlgorithm algorithm)
    {
        var selection = ResolveSelection();
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
    private async Task ExecuteUnpack()
    {
        if (GuardReadOnlyBrowserTab("解凍")) return;
        var selection = ResolveSelection();
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
        bool useZipFallback = false;
        bool useTarFallback = false;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            if (canUseZipFallbackOnly)
            {
                useZipFallback = true;
                ShowStatusMessage("7-Zip が見つからないため、Windows 標準 zip 解凍で実行します。");
            }
            else if (TarFallbackService.IsAvailable())
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
        if (GuardClipboardBusy()) return;
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
            if (exitStatus == FileOpExitStatus.Success)
            {
                ClearMarks();
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
        int reqId = Interlocked.Increment(ref _previewRequestId);
        Interlocked.Exchange(ref _activePreviewRequestId, reqId);
        // 内容を非同期で更新
        await UpdateLargeFileVirtualDisplayAsync(reqId, _previewCts?.Token ?? CancellationToken.None, preserveCharacterSelection);
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
        _notificationService.Show(message);
        if (_currentViewerKind == PreviewKind.LargeText)
        {
            LogViewerStatusRoute("ShowStatusMessage", GetViewerStatusLine());
        }
        // Phase: move viewer status to external - internal label no longer used
    }
    private void FileListView_SelectedIndexChanged(object? sender, EventArgs e)
    {
        // マウス操作時の同期: 選択変更を内部状態 (_browserCursorIndex) に書き戻す
        if (fileListView.SelectedIndices.Count > 0)
        {
            _browserCursorIndex = fileListView.SelectedIndices[0];
        }
        // Info/Name 行をリアルタイム更新
        UpdateInfoPanel();
        // プレビューエンコーディングを Auto にリセット
        _viewerEncodingOverride = ViewerEncoding.Auto;
        var currentItem = GetCurrentBrowserItem();
        string? currentPath = currentItem?.Tag as string;
        PreviewKind currentSelectionKind = GetBrowserSelectionPreviewKind(currentItem, currentPath);
        bool isImageSelection = currentSelectionKind == PreviewKind.Image;
        var viewer = GetReusableImageViewer();
        if (isImageSelection && viewer != null)
        {
            viewer.LoadMedia(currentPath!, currentSelectionKind, showErrorMessage: false);
            viewer.EnsureVisibleAndActivated();
        }
        else if (!isImageSelection && (_settings.Preview?.CloseImageViewerOnNonImageSelection ?? false))
        {
            CloseImageViewers();
        }
        UpdateMenuStripState();
        // Browser自動preview対象のみ事前クリアし、対象外は不要な再描画を避ける
        if (IsBrowserAutoPreviewEligible(currentSelectionKind))
        {
            ResetBrowserAutoPreviewSuppressedState();
            ClearPreview();
        }
        RequestPreviewRefresh();
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
    private static bool IsBrowserAutoPreviewEligible(PreviewKind kind)
    {
        return kind == PreviewKind.Image;
    }
    private static string GetBrowserAutoPreviewSuppressedMessage(PreviewKind kind)
    {
        return kind switch
        {
            PreviewKind.Text => "自動プレビューなし\nV / Enter で開きます。",
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
            if (!IsLatestPreviewRequest(reqId, requestPath, token))
            {
                LogService.Info(
                    $"[PreviewRequest] skippedReason=Superseded reqId={reqId} " +
                    $"requestPath='{requestPath}' currentPath='{currentPath}' activeReqId={_activePreviewRequestId}");
                return;
            }
            _largeFileState = null;
            string fullPath = requestPath;
            if (Directory.Exists(fullPath))
            {
                ClearPreview("プレビュー対象外", reqId);
                return;
            }
            // 【チラつき抑制】 前回と同じターゲットなら、表示クリアをスキップして即表示更新へ向かう
            if (_currentPreviewTarget != fullPath)
            {
                ClearPreview("", reqId);
                _currentPreviewTarget = fullPath;
            }
            var kind = GetEffectivePreviewKind(fullPath);
            LogLargeTextEntryTiming(
                "after GetPreviewKind",
                entrySw,
                fullPath,
                reqId,
                kind,
                currentPath: GetCurrentPreviewSelectionPath());
            if (!IsLatestPreviewRequest(reqId, fullPath, token))
            {
                LogService.Info(
                    $"[PreviewRequest] skippedReason=SupersededAfterKind reqId={reqId} " +
                    $"requestPath='{fullPath}' activeReqId={_activePreviewRequestId}");
                return;
            }
            if (kind == PreviewKind.None)
            {
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
                            return (
                                System.Text.Encoding.UTF8.GetString(buffer, 0, readCount) + (fs.Length > maxBytes ? "\n\n[... 表示節減されました ...]" : ""),
                                "UTF-8 (manual)");
                        }
                        else if (_viewerEncodingOverride == ViewerEncoding.SJIS)
                        {
                            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                            var sjisManual = System.Text.Encoding.GetEncoding("shift_jis");
                            return (
                                sjisManual.GetString(buffer, 0, readCount) + (fs.Length > maxBytes ? "\n\n[... 表示節減されました ...]" : ""),
                                "CP932 (manual)");
                        }
                        // 1. BOMチェック (StreamReader の標準機能に相当する処理)
                        if (readCount >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                        {
                            return (
                                System.Text.Encoding.UTF8.GetString(buffer, 3, readCount - 3) + (fs.Length > maxBytes ? "\n\n[... 表示節減されました ...]" : ""),
                                "UTF-8 BOM");
                        }
                        if (readCount >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
                        {
                            return (
                                System.Text.Encoding.Unicode.GetString(buffer, 2, readCount - 2) + (fs.Length > maxBytes ? "\n\n[... 表示節減されました ...]" : ""),
                                "UTF-16 LE BOM");
                        }
                        // 2. BOMなし UTF-8 試行 (例外を投げる設定で厳密に判定)
                        try
                        {
                            var utf8Strict = new System.Text.UTF8Encoding(false, true);
                            // 読み込み上限境界でマルチバイト文字が切断されている場合に備え、安全な長さまでトリミングする
                            int safeLength = GetSafeUtf8Length(buffer, readCount);
                            string utf8Result = utf8Strict.GetString(buffer, 0, safeLength);
                            return (
                                utf8Result + (fs.Length > maxBytes ? "\n\n[... 表示節減されました ...]" : ""),
                                "UTF-8");
                        }
                        catch (ArgumentException)
                        {
                            // UTF-8 として不正なバイトシーケンスが含まれる、または依然として不完全な場合は Shift_JIS フォールバックへ
                        }
                        // 3. Shift_JIS (CP932) フォールバック
                        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                        var sjis = System.Text.Encoding.GetEncoding("shift_jis");
                        return (
                            sjis.GetString(buffer, 0, readCount) + (fs.Length > maxBytes ? "\n\n[... 表示節減されました ...]" : ""),
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
            else if (kind == PreviewKind.LargeText)
            {
                if (_uiMode != UIMode.Viewer)
                {
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
                    LogLargeTextEntryTiming("before DetectLargeTextEncoding", entrySw, fullPath, reqId, kind, state);
                    var detected = await Task.Run(() => PreviewService.DetectLargeTextEncoding(fullPath), token);
                    state.DetectedEncoding = detected.Encoding;
                    state.DetectedEncodingLabel = detected.EncodingLabel;
                    state.HasBom = detected.HasBom;
                    state.IsBinaryLike = detected.IsBinaryLike;
                    state.IsEncodingUnsupportedForLargeText = detected.IsEncodingUnsupportedForLargeText;
                    state.IsLongLineDetected = detected.IsLongLineDetected;
                    LogLargeTextEntryTiming("after DetectLargeTextEncoding", entrySw, fullPath, reqId, kind, state);
                    if (!IsLatestPreviewRequest(reqId, fullPath, token) || _uiMode != UIMode.Viewer)
                    {
                        LogService.Info(
                            $"[PreviewRequest] skippedReason=SupersededAfterDetect reqId={reqId} " +
                            $"requestPath='{fullPath}' activeReqId={_activePreviewRequestId}");
                        return;
                    }
                    if (state.IsBinaryLike)
                    {
                        ClearPreview("LargeText対象外: binary-like file", reqId);
                        ApplyViewerStatusLine("LargeText binary-like guard");
                        ShowStatusMessage("LargeText対象外: binary-like file を検出しました。");
                        return;
                    }
                    if (state.IsEncodingUnsupportedForLargeText)
                    {
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
            // Task.Delay や Task.Run 内でのキャンセル。意図した動作なので何もせず終了。
            LogService.Info($"[PreviewRequest] skippedReason=Canceled reqId={reqId} requestPath='{requestPath}'");
        }
        catch (Exception ex)
        {
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
            if (_activePreviewRequestId == reqId)
            {
                _previewRequestInFlight = false;
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
                string? shellError = ExternalToolService.ExecuteShell(_navigationService.CurrentPath, $"\"{fullPath}\"");
                if (shellError != null) ShowStatusMessage(shellError);
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
        if (GuardClipboardBusy()) return;
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
        if (kind != PreviewKind.Text && kind != PreviewKind.LargeText)
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
        string? error = ExternalToolService.OpenTerminal(_navigationService.CurrentPath, kind);
        if (error != null) ShowStatusMessage(error);
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
        FileAttributes initialAttrs;
        DateTime initialLastWrite;
        DateTime initialCreation;
        DateTime initialAccess;
        try
        {
            initialAttrs = File.GetAttributes(firstPath);
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
            initialAttrs,
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
            progressForm = new FileOperationProgressFallbackForm("属性 / 日時変更", totalCount, requestCancel: null, canCancel: false);
            PositionProgressFallbackForm(progressForm);
            progressForm.Show(this);
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
        var attrs = File.GetAttributes(path);
        if (options.ReadOnly) attrs |= FileAttributes.ReadOnly;
        else attrs &= ~FileAttributes.ReadOnly;
        if (options.Hidden) attrs |= FileAttributes.Hidden;
        else attrs &= ~FileAttributes.Hidden;
        if (options.System) attrs |= FileAttributes.System;
        else attrs &= ~FileAttributes.System;
        if (options.Archive) attrs |= FileAttributes.Archive;
        else attrs &= ~FileAttributes.Archive;
        File.SetAttributes(path, attrs);
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
            _previewCts?.Cancel();
            _lastPreviewRequestedPath = null;
            _previewRequestInFlight = false;
            ResetBrowserAutoPreviewSuppressedState();
            ClearPreview("選択なしのためプレビューなし");
            return;
        }
        if (!force)
        {
            PreviewKind shallowKind = GetBrowserSelectionPreviewKind(currentItem, requestPath);
            if (!IsBrowserAutoPreviewEligible(shallowKind))
            {
                _previewCts?.Cancel();
                _lastPreviewRequestedPath = null;
                _previewRequestInFlight = false;
                ShowBrowserAutoPreviewSuppressedMessage(requestPath, shallowKind);
                return;
            }
        }
        if (!force
            && string.Equals(_lastPreviewRequestedPath, requestPath, StringComparison.OrdinalIgnoreCase)
            && _previewRequestInFlight)
        {
            LogService.Info($"[PreviewRequest] skippedReason=DuplicatePath requestPath='{requestPath}' activeReqId={_activePreviewRequestId}");
            return;
        }
        ResetBrowserAutoPreviewSuppressedState();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        int reqId = Interlocked.Increment(ref _previewRequestId);
        Interlocked.Exchange(ref _activePreviewRequestId, reqId);
        _lastPreviewRequestedPath = requestPath;
        _previewRequestInFlight = true;
        LogService.Info($"[PreviewRequest] queued reqId={reqId} requestPath='{requestPath}' force={force}");
        _ = UpdatePreviewAsync(reqId, requestPath, _previewCts.Token);
    }
    /// <summary>O キー: 設定画面を開く。OK 保存後は _settings を再読込して次のコマンドに反映する。</summary>
    private void OpenSettingsForm()
    {
        HideTransientOverlaysBeforeModalDialog();
        BrowserTabRuntimeStateSnapshot runtimeBrowserTabState = CaptureBrowserTabRuntimeStateSnapshot();
        using var form = new SettingsForm(_settings, _featureProfile);
        var result = form.ShowDialog(this);
        if (result == DialogResult.OK)
        {
            // SettingsForm が Save した内容を読み直してインメモリ設定と一致させる
            var reloaded = MidFD.Configuration.SettingsManager.Load(out SettingsManager.SettingsLoadMetadata settingsLoadMetadata);
            _settings.Profile = reloaded.Profile;
            _settings.Input = reloaded.Input ?? new InputSettings();
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
            ApplyFontSettings();
            ApplyColorSettings();
            viewerTextBox.WordWrap = _settings.Preview.ViewerWordWrap;
            viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
            if (_settings.Session.RestoreColumnCount)
            {
                _columnCount = Math.Clamp(_settings.Session.LastColumnCount, 1, 9);
            }
            if (_settings.Session.RestoreSort)
            {
                _currentSort = _settings.Session.LastSortKind;
                _sortAscending = _settings.Session.LastSortAscending;
            }
            LoadDirectory(_navigationService.CurrentPath);
            RebuildMenuStripAfterSettingsApply();
            ShowStatusMessage("設定を保存しました。");
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
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "PracticalStable では Workspace Snapshot は無効です。"))
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
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "PracticalStable では Workspace Snapshot エクスポートは無効です。"))
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
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "PracticalStable では Workspace Snapshot インポートは無効です。"))
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
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "PracticalStable では Workspace Snapshot 一括エクスポートは無効です。"))
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
        if (GuardFeatureDisabled(FeatureId.WorkspaceSnapshot, "PracticalStable では Workspace Snapshot 一括インポートは無効です。"))
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
        IReadOnlyList<QuickAccessEntry> historyEntries = QuickAccessService.BuildHistoryEntries(
            _navigationService.GetBackHistorySnapshot(),
            _navigationService.GetForwardHistorySnapshot());
        var result = QuickAccessDialog.Show(this, _quickAccessStore, _navigationService.CurrentPath, historyEntries);
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
            // 重要行 (FileListFontSize を反映)
            var filerInfoFont = new Font(filerFamily, filerSize);
            _headerPaintFont = filerInfoFont; // Phase 2g-fix3a: Paint 向けに保持
            // 高さをフォントに合わせて動的に調整
            var metrics = HeaderLayoutHelper.CalculateMetrics(filerInfoFont, 4);
            titleHeaderPanel.Height = metrics.TitleHeaderHeight;
            headerPanel.Height = metrics.RowHeight;
            sepBeforeTopPanel.Height = 1;
            sepBeforeTopPanel.Visible = true;
            infoRow2Panel.Height = metrics.RowHeight;
            infoRow2Panel.Visible = true;
            sepAfterRow2.Height = 0;
            sepAfterRow2.Visible = false;
            infoRow3Panel.Height = 0;
            infoRow3Panel.Visible = false;
            sepAfterRow3.Height = 0;
            sepAfterRow3.Visible = false;
            infoRow4Panel.Height = metrics.RowHeight;
            infoRow4Panel.Visible = true;
            sepAfterRow4.Height = 1;
            sepAfterRow4.Visible = true;
            topPanel.Height = metrics.TopPanelHeight;
            _functionBarPreferredHeight = metrics.RowHeight;
            functionBarPanel.Height = metrics.RowHeight;
            lblClock.Font = filerInfoFont;
            // Phase 5-ui-layout-fix2: BringToFront ハックは Dock 順が正しければ不要なため削除
            foreach (var lbl in lblFuncKeys)
            {
                lbl.Font = filerInfoFont;
            }
            lblPath.Font = filerInfoFont;
            lblSort.Font = filerInfoFont;
            lblItemAttr.Font = filerInfoFont;
            lblFileDate.Font = filerInfoFont;
            lblFileStats.Font = filerInfoFont;
            lblFileStatsEx.Font = filerInfoFont;
            lblName.Font = filerInfoFont;
            lblPage.Font = filerInfoFont;
            lblTotal.Font = filerInfoFont;
            lblUsed.Font = filerInfoFont;
            lblFree.Font = filerInfoFont;
            statusLabel.Font = filerInfoFont;
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
            // Phase 2f-fix2: レイアウト確定前にテキストの値を最新化しておく
            UpdateInfoPanel();
            // Phase 2g-fix2: テキスト更新後に Zone 幅を動的に計算する
            LayoutHeaderZones();
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
    }
    /// <summary>
    /// UTF-8 マルチバイト文字の途中で切断されない安全な長さを取得する（バッファ末尾の切り出し境界用）。
    /// </summary>
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
        var widths = HeaderLayoutHelper.CalculateZoneWidths(
            headerPanel.ClientSize.Width - lblClock.Width,
            lblPage.Font,
            lblPage.Text,
            lblTotal.Text,
            lblUsed.Text,
            lblFree.Text,
            this.MinimumSize.Width
        );
        headerZone1.Width = widths.Zone1;
        headerZone2.Width = widths.Zone2;
        headerZone3.Width = widths.Zone3;
        headerZone4.Width = widths.Zone4;
        if (this.MinimumSize.Width != widths.MinimumFormWidth)
        {
            LogService.Info($"[WindowFloorHitIntercept] MinimumSize width audit: {this.MinimumSize.Width} -> {widths.MinimumFormWidth}");
            this.MinimumSize = new Size(widths.MinimumFormWidth, this.MinimumSize.Height);
        }
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
    /// <summary>
    /// 最上段ヘッダ (titleHeaderPanel) 専用：文字描画
    /// Phase 36Z: 枠線は contentFramePanel が描くため、ここでは文字の上書きのみ。
    /// </summary>
    private void titleHeaderPanel_Paint(object sender, PaintEventArgs e)
    {
        // Title header is now compact/hidden in Browser mode.
        // No text drawing here.
    }
    /// <summary>
    /// コンテンツ・フレーム (contentFramePanel) 専用：アプリケーション全体の 1px 枠線描画 (オーナー)
    /// </summary>
    private void contentFramePanel_Paint(object sender, PaintEventArgs e)
    {
        var panel = sender as Panel;
        if (panel == null) return;
        if (_settings.Appearance?.ColorTheme == "Light")
        {
            // Light テーマ: 左右線はスキップ、下辺は SeparatorLine で弱めに描画
            using (var pen = new Pen(MidFDColors.SeparatorLine, 1))
            {
                // 下辺 (一覧領域の外枠として描画)
                e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
            }
        }
        else
        {
            // 既存どおり: BorderLine で左辺/右辺/下辺を描く
            using (var pen = new Pen(MidFDColors.BorderLine, 1))
            {
                // 左辺
                e.Graphics.DrawLine(pen, 0, 0, 0, panel.Height);
                // 右辺
                e.Graphics.DrawLine(pen, panel.Width - 1, 0, panel.Width - 1, panel.Height);
                // 下辺 (一覧領域の外枠として描画)
                e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
            }
        }
    }
    // ─── Phase 2g-fix3a: Row 1 時計更新ロジック ──────────────────────────
    private void StartHeaderClockTimer()
    {
        _headerClockTimer?.Stop();
        _headerClockTimer?.Dispose();
        _headerClockTimer = new System.Windows.Forms.Timer();
        _headerClockTimer.Interval = 1000; // 1秒周期
        _headerClockTimer.Tick += (s, e) => UpdateTitleHeaderClock();
        _headerClockTimer.Start();
    }
    private void UpdateTitleHeaderClock()
    {
        // 秒単位の時計文字列を更新
        lblClock.Text = DateTime.Now.ToString("yyyy-MM-dd(ddd) HH:mm:ss");
        // 再描画を要求
        lblClock.Invalidate();
        contentFramePanel.Invalidate();
        // 必要ならデバッグログ (最終的に削除可能)
        // Debug.WriteLine($"[Clock] {lblClock.Text}");
    }
    /// <summary>
    /// Phase 2g-fix4a: 各要素への配色適用を一括して行う。
    /// </summary>
        private void ApplyColorSettings()
    {
        MidFDColors.ApplyTheme(_settings.Appearance?.ColorTheme);
        var headerColors = GetHeaderColors();
        // Row 2 (現在は monolithic データストア、表示は Paint へ移譲)
        lblPage.ForeColor = headerColors.HeaderRow2Fore;
        lblTotal.ForeColor = headerColors.HeaderRow2Fore;
        lblUsed.ForeColor = headerColors.HeaderRow2Fore;
        lblFree.ForeColor = headerColors.HeaderRow2Fore;
        // Phase 2g-fix4b: 表示のみ抑制 (LayoutHeaderZones が計測できるようにコントロールは残す)
        lblPage.Visible = false;
        lblTotal.Visible = false;
        lblUsed.Visible = false;
        lblFree.Visible = false;
        // Row 3 (Meta)
        lblSort.ForeColor = headerColors.HeaderMetaFore;
        lblItemAttr.ForeColor = headerColors.HeaderMetaFore;
        lblFileDate.ForeColor = headerColors.HeaderMetaFore;
        lblFileStats.ForeColor = headerColors.HeaderMetaFore;
        lblFileStatsEx.ForeColor = headerColors.HeaderMetaFore;
        lblClock.ForeColor = headerColors.HeaderClockFore;
        lblClock.BackColor = MidFDColors.ListNormalBack;
        // Row 4 (Path)
        lblPath.ForeColor = headerColors.HeaderPathFore;
        // Row 5 (Name)
        lblName.ForeColor = headerColors.HeaderNameFore;
        // 一覧部
        fileListView.ForeColor = MidFDColors.ListNormalFore;
        fileListView.BackColor = MidFDColors.ListNormalBack;
        browserPanel.ForeColor = MidFDColors.ListNormalFore;
        browserPanel.BackColor = MidFDColors.ListNormalBack;
        mainMenuStrip.BackColor = MidFDColors.ListNormalBack;
        mainMenuStrip.ForeColor = MidFDColors.ListNormalFore;
        foreach (ToolStripMenuItem rootItem in mainMenuStrip.Items.OfType<ToolStripMenuItem>())
        {
            rootItem.BackColor = MidFDColors.ListNormalBack;
            rootItem.ForeColor = MidFDColors.ListNormalFore;
            ApplyDropDownTheme(rootItem);
        }
        viewerPanel.BackColor = MidFDColors.ViewerBack;
        viewerTextBox.BackColor = MidFDColors.ViewerBack;
        viewerTextBox.ForeColor = MidFDColors.ViewerFore;
        viewerMessageLabel.BackColor = MidFDColors.ViewerBack;
        viewerMessageLabel.ForeColor = MidFDColors.ViewerFore;
        // セパレーター
        sepBeforeTopPanel.BackColor = MidFDColors.BorderLine;
        sepAfterRow2.BackColor = MidFDColors.SeparatorLine;
        sepAfterRow3.BackColor = MidFDColors.SeparatorLine;
        sepAfterRow4.BackColor = MidFDColors.BorderLine;
        // 背景色の一貫性
        outerHostPanel.BackColor = MidFDColors.ListNormalBack;
        mainAreaPanel.BackColor = MidFDColors.ListNormalBack;
        headerPanel.BackColor = MidFDColors.ListNormalBack;
        topPanel.BackColor = MidFDColors.ListNormalBack;
        infoRow2Panel.BackColor = MidFDColors.ListNormalBack;
        infoRow3Panel.BackColor = MidFDColors.ListNormalBack;
        infoRow4Panel.BackColor = MidFDColors.ListNormalBack;
        titleHeaderPanel.BackColor = MidFDColors.ListNormalBack;
        contentFramePanel.BackColor = MidFDColors.ListNormalBack;
        functionBarPanel.BackColor = MidFDColors.ListNormalBack;
        // FunctionBar のラベル色を更新
        if (lblFuncKeys != null)
        {
            foreach (var lbl in lblFuncKeys)
            {
                lbl.BackColor = MidFDColors.ListNormalBack;
                lbl.ForeColor = MidFDColors.ListNormalFore;
            }
        }
        statusStrip.BackColor = MidFDColors.ListNormalBack;
        statusStrip.ForeColor = MidFDColors.ListNormalFore;
        if (_browserTabHostPanel != null)
        {
            _browserTabHostPanel.BackColor = MidFDColors.ListNormalBack;
        }
        if (_browserTabStrip != null)
        {
            ApplyBrowserTabStripDisplaySettings();
            _browserTabStrip.BackColor = MidFDColors.ListNormalBack;
            _browserTabStrip.ForeColor = MidFDColors.ListNormalFore;
            _browserTabStrip.ActiveTabBackColor = MidFDColors.ListSelectedBack;
            _browserTabStrip.InactiveTabBackColor = MidFDColors.ListNormalBack;
            _browserTabStrip.TabBorderColor = MidFDColors.BorderLine;
            _browserTabStrip.ActiveTabTextColor = _settings.Appearance?.ColorTheme == "Light" ? Color.Black : Color.Yellow;
            _browserTabStrip.InactiveTabTextColor = MidFDColors.ListNormalFore;
            _browserTabStrip.Invalidate();
        }
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
    private void ApplyDropDownTheme(ToolStripDropDownItem item)
    {
        item.DropDown.BackColor = MidFDColors.ListNormalBack;
        item.DropDown.ForeColor = MidFDColors.ListNormalFore;
        foreach (ToolStripItem child in item.DropDownItems)
        {
            child.BackColor = MidFDColors.ListNormalBack;
            child.ForeColor = MidFDColors.ListNormalFore;
            if (child is ToolStripDropDownItem childDropDown)
            {
                ApplyDropDownTheme(childDropDown);
            }
        }
    }
    /// <summary>
    /// Phase 2g-fix4a: アプリケーション全体の配色定数。
    /// </summary>
    private sealed class HeaderColorPalette
    {
        public required Color HeaderTitleFore { get; init; }
        public required Color HeaderClockFore { get; init; }
        public required Color HeaderRow2Fore { get; init; }
        public required Color HeaderRow2Value { get; init; }
        public required Color HeaderPathFore { get; init; }
        public required Color HeaderMetaFore { get; init; }
        public required Color HeaderNameFore { get; init; }
    }
    private HeaderColorPalette GetHeaderColors()
    {
        return _settings.Appearance?.ColorTheme switch
        {
            "Green" => new HeaderColorPalette
            {
                HeaderTitleFore = Color.Yellow,
                HeaderClockFore = Color.Yellow,
                HeaderRow2Fore = Color.Lime,
                HeaderRow2Value = Color.White,
                HeaderPathFore = Color.Lime,
                HeaderMetaFore = Color.Lime,
                HeaderNameFore = Color.LightGreen
            },
            "Amber" => new HeaderColorPalette
            {
                HeaderTitleFore = Color.FromArgb(255, 220, 120),
                HeaderClockFore = Color.FromArgb(255, 220, 120),
                HeaderRow2Fore = Color.FromArgb(255, 190, 80),
                HeaderRow2Value = Color.White,
                HeaderPathFore = Color.FromArgb(255, 210, 120),
                HeaderMetaFore = Color.FromArgb(255, 210, 120),
                HeaderNameFore = Color.FromArgb(255, 235, 180)
            },
                        "Light" => new HeaderColorPalette
            {
                HeaderTitleFore = Color.Black,
                HeaderClockFore = Color.Black,
                HeaderRow2Fore = Color.FromArgb(80, 80, 80),
                HeaderRow2Value = Color.Black,
                HeaderPathFore = Color.Black,
                HeaderMetaFore = Color.FromArgb(80, 80, 80),
                HeaderNameFore = Color.Black
            },
            _ => new HeaderColorPalette
            {
                HeaderTitleFore = Color.Yellow,
                HeaderClockFore = Color.Yellow,
                HeaderRow2Fore = Color.Cyan,
                HeaderRow2Value = Color.White,
                HeaderPathFore = Color.Cyan,
                HeaderMetaFore = Color.Cyan,
                HeaderNameFore = Color.LightCyan
            }
        };
    }
    /// <summary>
    /// Phase 2g-fix4b: Row 2 (Page, Total, Used, Free) を見出しと値で別々に描画するハンドラ。
    /// </summary>
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
        var headerColors = GetHeaderColors();
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
    }
    private void RestoreSelectionState(string? focusTargetName, int lastIndex, bool isReload)
    {
        if (fileListView.Items.Count == 0)
        {
            _browserCursorIndex = 0;
            UpdateInfoPanel();
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
        targetItem.Focused = true;
        targetItem.EnsureVisible();
        _browserCursorIndex = targetItem.Index;
        // 状態更新
        UpdateInfoPanel();
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
        lblName.SendToBack();
        this.PerformLayout();
    }
    private static int MeasureHeaderTextWidth(string text, Font font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        return TextRenderer.MeasureText(
            text,
            font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
        ).Width;
    }
    private static string FitTextWithEllipsis(string text, Font font, int maxWidth, string ellipsis = "...")
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return string.Empty;
        }
        if (MeasureHeaderTextWidth(text, font) <= maxWidth)
        {
            return text;
        }
        int ellipsisWidth = MeasureHeaderTextWidth(ellipsis, font);
        if (ellipsisWidth >= maxWidth)
        {
            return ellipsis;
        }
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = text.Substring(0, mid) + ellipsis;
            if (MeasureHeaderTextWidth(candidate, font) <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }
        return text.Substring(0, low) + ellipsis;
    }
    private static string FitFileNameWithSizePreservingExtension(
        string fileName,
        string sizeText,
        Font font,
        int maxWidth)
    {
        if (string.IsNullOrWhiteSpace(fileName) || maxWidth <= 0)
        {
            return string.Empty;
        }
        string sizeSuffix = string.IsNullOrWhiteSpace(sizeText)
            ? string.Empty
            : $" [{sizeText}]";
        string full = fileName + sizeSuffix;
        if (MeasureHeaderTextWidth(full, font) <= maxWidth)
        {
            return full;
        }
        string extension = Path.GetExtension(fileName);
        string baseName = fileName;
        if (!string.IsNullOrEmpty(extension) &&
            fileName.Length > extension.Length)
        {
            baseName = fileName[..^extension.Length];
        }
        else
        {
            extension = string.Empty;
        }
        string protectedSuffix = extension + sizeSuffix;
        int protectedSuffixWidth = MeasureHeaderTextWidth(protectedSuffix, font);
        int baseMaxWidth = maxWidth - protectedSuffixWidth;
        const string ellipsis = "…";
        int ellipsisWidth = MeasureHeaderTextWidth(ellipsis, font);
        if (baseMaxWidth <= ellipsisWidth)
        {
            // 極端に幅が足りない場合でも、拡張子と size を優先する。
            string fallback = ellipsis + protectedSuffix;
            if (MeasureHeaderTextWidth(fallback, font) <= maxWidth)
            {
                return fallback;
            }
            return FitTextWithEllipsis(full, font, maxWidth, ellipsis);
        }
        string shortenedBase = FitTextWithEllipsis(baseName, font, baseMaxWidth, ellipsis);
        return shortenedBase + protectedSuffix;
    }
    private static string FitDirectoryNameHeaderText(
        string displayName,
        Font font,
        int maxWidth)
    {
        return FitTextWithEllipsis(displayName, font, maxWidth);
    }
    private static string FitMarkSummaryCompact(
        int markCount,
        string markSizeText,
        Font font,
        int maxWidth)
    {
        string[] candidates =
        {
            $"Mark: {markCount} MarkSize: {markSizeText}",
            $"Mark: {markCount} {markSizeText}",
            $"M:{markCount} {markSizeText}",
            $"M:{markCount}",
        };
        foreach (string candidate in candidates)
        {
            if (MeasureHeaderTextWidth(candidate, font) <= maxWidth)
            {
                return candidate;
            }
        }
        return candidates[^1];
    }
    #region Browser Header Interaction Polish
    private void InitializeHeaderInteractionPolish()
    {
        if (_headerInteractionInitialized) return;
        _headerInteractionInitialized = true;
        _headerToolTip = new ToolTip
        {
            ShowAlways = true,
            InitialDelay = 400,
            ReshowDelay = 100,
            AutoPopDelay = 8000
        };
        InitializeHeaderContextMenus();
        WireHeaderCopyInteractions();
    }
    private void InitializeHeaderContextMenus()
    {
        // Path 行用メニュー
        _headerPathContextMenu = new ContextMenuStrip();
        var copyPathItem = new ToolStripMenuItem("パスをコピー");
        copyPathItem.Click += (_, _) => CopyCurrentDirectoryFromHeader();
        _headerPathContextMenu.Items.Add(copyPathItem);
        // Item 行用メニュー
        _headerItemContextMenu = new ContextMenuStrip();
        var copyFullPathItem = new ToolStripMenuItem("フルパスをコピー");
        copyFullPathItem.Click += (_, _) => CopySelectedItemFullPathFromHeader();
        var copyFileNameItem = new ToolStripMenuItem("ファイル名をコピー");
        copyFileNameItem.Click += (_, _) => CopySelectedItemNameFromHeader();
        _headerItemContextMenu.Items.Add(copyFullPathItem);
        _headerItemContextMenu.Items.Add(copyFileNameItem);
        _headerItemContextMenu.Opening += (s, e) =>
        {
            bool hasItem = !string.IsNullOrWhiteSpace(GetSelectedItemFullPathForHeaderCopy());
            copyFullPathItem.Enabled = hasItem;
            copyFileNameItem.Enabled = hasItem;
            e.Cancel = false;
        };
    }
    private void WireHeaderCopyInteractions()
    {
        // Cursor
        lblPath.Cursor = Cursors.Hand;
        lblName.Cursor = Cursors.Hand;
        // MouseClick (Left click copy)
        lblPath.MouseClick += HeaderPath_MouseClick;
        infoRow2Panel.MouseClick += HeaderPath_MouseClick;
        lblName.MouseClick += HeaderItem_MouseClick;
        infoRow4Panel.MouseClick += HeaderItem_MouseClick;
        // ContextMenuStrip
        lblPath.ContextMenuStrip = _headerPathContextMenu;
        infoRow2Panel.ContextMenuStrip = _headerPathContextMenu;
        lblName.ContextMenuStrip = _headerItemContextMenu;
        infoRow4Panel.ContextMenuStrip = _headerItemContextMenu;
    }
    private void HeaderPath_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            CopyCurrentDirectoryFromHeader();
        }
    }
    private void HeaderItem_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            CopySelectedItemFullPathFromHeader();
        }
    }
    private void CopyCurrentDirectoryFromHeader()
    {
        string? path = GetCurrentDirectoryForHeaderCopy();
        CopyTextToClipboardWithStatus(path, "パスをコピーしました。");
    }
    private void CopySelectedItemFullPathFromHeader()
    {
        string? fullPath = GetSelectedItemFullPathForHeaderCopy();
        CopyTextToClipboardWithStatus(fullPath, "フルパスをコピーしました。");
    }
    private void CopySelectedItemNameFromHeader()
    {
        string? fileName = GetSelectedItemNameForHeaderCopy();
        CopyTextToClipboardWithStatus(fileName, "ファイル名をコピーしました。");
    }
    private void CopyTextToClipboardWithStatus(string? text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatusMessage("コピーできる内容がありません。");
            return;
        }
        try
        {
            Clipboard.SetText(text, TextDataFormat.UnicodeText);
            ShowStatusMessage(successMessage);
        }
        catch (Exception ex)
        {
            ShowStatusMessage("クリップボードへコピーできませんでした。");
            LogService.Info($"[HeaderCopy] Clipboard copy failed: {ex}");
        }
    }
    private string? GetCurrentDirectoryForHeaderCopy()
    {
        string path = _navigationService.CurrentPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
    private string? GetSelectedItemFullPathForHeaderCopy()
    {
        string currentPath = _navigationService.CurrentPath;
        var item = GetCurrentBrowserItem();
        if (item == null) return null;
        string name = item.Text;
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name == "..")
        {
            try
            {
                return Directory.GetParent(currentPath)?.FullName;
            }
            catch { return null; }
        }
        // item.Tag にフルパスが入っている場合はそれを使う
        if (item.Tag is string tagPath && !string.IsNullOrWhiteSpace(tagPath))
        {
            return tagPath;
        }
        try
        {
            return Path.Combine(currentPath, name);
        }
        catch { return null; }
    }
    private string? GetSelectedItemNameForHeaderCopy()
    {
        var item = GetCurrentBrowserItem();
        if (item == null) return null;
        string name = item.Text;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name;
    }
    private void UpdateHeaderInteractionTooltips()
    {
        if (_headerToolTip == null) return;
        string? path = GetCurrentDirectoryForHeaderCopy();
        string? fullPath = GetSelectedItemFullPathForHeaderCopy();
        _headerToolTip.SetToolTip(lblPath, string.IsNullOrWhiteSpace(path) ? null : $"左クリックでパスをコピー:\r\n{path}");
        _headerToolTip.SetToolTip(infoRow2Panel, string.IsNullOrWhiteSpace(path) ? null : $"左クリックでパスをコピー:\r\n{path}");
        _headerToolTip.SetToolTip(lblName, string.IsNullOrWhiteSpace(fullPath) ? null : $"左クリックでフルパスをコピー:\r\n{fullPath}");
        _headerToolTip.SetToolTip(infoRow4Panel, string.IsNullOrWhiteSpace(fullPath) ? null : $"左クリックでフルパスをコピー:\r\n{fullPath}");
        // アイテムがない場合のカーソル調整
        lblName.Cursor = string.IsNullOrWhiteSpace(fullPath) ? Cursors.Default : Cursors.Hand;
    }
    #endregion
}
