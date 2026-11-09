using System.Globalization;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using MidFD.Configuration;

namespace MidFD;

internal sealed class EmbeddedVideoPlayerControl : UserControl
{
    private sealed record PlaybackRateChoice(double Rate)
    {
        public override string ToString() => $"{Rate:0.##}x";
    }

    private const int DetailsPanelExpandedHeight = 130;
    private const int StatusRowHeight = 24;
    private const int VideoSurfaceSeparatorHeight = 1;
    private const int ProgressRowHeight = 31;
    private const int TransportRowHeight = 44;
    private readonly Panel _videoView = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Panel _videoSurfaceSeparator = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly MediaChromeButton _playPauseButton = new() { Icon = MediaChromeIcon.Play };
    private readonly MediaChromeButton _stopButton = new() { Icon = MediaChromeIcon.Stop };
    private readonly MediaChromeButton _skipBackButton = new();
    private readonly MediaChromeButton _skipForwardButton = new();
    private readonly MediaSeekBar _seekBar = new() { Minimum = 0, Maximum = 1000, Dock = DockStyle.Fill };
    private readonly MediaSeekBar _volumeBar = new() { Minimum = 0, Maximum = 100, Value = 100 };
    private readonly MediaIconView _volumeIcon = new() { Icon = MediaChromeIcon.Volume };
    private readonly Label _volumeValueLabel = new() { AutoSize = false, Text = "100%", TextAlign = ContentAlignment.MiddleRight };
    private readonly Label _currentTimeLabel = new() { AutoSize = false, Text = "0:00", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _durationLabel = new() { AutoSize = false, Text = "—", TextAlign = ContentAlignment.MiddleRight };
    private readonly Panel _statusPanel = new() { Dock = DockStyle.Fill, Visible = false, Margin = Padding.Empty };
    private readonly Label _statusLabel = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _videoDetailsLabel = new() { Text = "映像", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _audioDetailsLabel = new() { Text = "音声", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _playbackDetailsLabel = new() { Text = "再生", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _videoDetailsValueLabel = new() { Text = "—", AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _audioDetailsValueLabel = new() { Text = "—", AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _playbackDetailsValueLabel = new() { Text = "—", AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _engineDetailsLabel = new() { Text = "再生エンジン", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _engineDetailsValueLabel = new() { Text = "LibVLC", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _selectionDetailsLabel = new() { Text = "選択理由", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _selectionDetailsValueLabel = new() { Text = "—", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly MediaChromeButton _detailsButton = new() { Icon = MediaChromeIcon.Info };
    private readonly MediaPlaybackRateComboBox _playbackRateCombo = new();
    private readonly MediaChromeButton _presentationLockButton = new() { Icon = MediaChromeIcon.Unlock, Visible = false };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 200 };
    private readonly ToolTip _controlToolTip = new();
    private readonly HashSet<Control> _mediaDropTargets = new();
    private readonly TableLayoutPanel _rootLayout;
    private readonly TableLayoutPanel _progressPanel;
    private readonly TableLayoutPanel _controlsPanel;
    private readonly FlowLayoutPanel _transportGroup;
    private readonly FlowLayoutPanel _secondaryGroup;
    private readonly Panel _transportSpacer = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Panel _detailsPanel;
    private readonly TableLayoutPanel _detailsLayout;
    private Font? _ownedChromeFont;
    private IVideoPlaybackBackend? _backend;
    private IVideoPlaybackBackend? _pendingFollowBackend;
    private VideoBackendSelection? _pendingFollowSelection;
    private bool _pendingFollowOverrideLoaded;
    private NativeVideoSurfaceClickBridge? _nativeSurfaceClickBridge;
    private Control? _nativeSurfaceClickRoot;
    private readonly Queue<(int Generation, IVideoPlaybackBackend Backend)> _nativeSurfaceClickToggleQueue = new();
    private bool _drainingNativeSurfaceClickToggleQueue;
    private bool _mediaDropTargetsEnabled;
    private string _backendKind = VideoPlaybackBackendKind.Auto;
    private VideoBackendSelector _backendSelector = new();
    private VideoBackendSelection? _selection;
    private CancellationTokenSource? _selectionCts;
    private CancellationTokenSource? _selectionFollowCts;
    private int _selectionFollowGeneration;
    private Task<bool> _selectionFollowTask = Task.FromResult(false);
    internal VideoBackendSelection? CurrentBackendSelection => _selection;
    private string _activePath = string.Empty;
    private int _ownerMediaGeneration;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string VideoPlaybackBackend { get => _backendKind; set => _backendKind = VideoPlaybackBackendKind.Normalize(value); }
    internal string ActiveBackendKind => _backend?.BackendKind ?? _backendKind;
    internal long PositionMilliseconds => _userStopped ? _userStoppedPositionMilliseconds : Math.Max(0, _backend?.Position ?? 0);
    private Task _pendingCleanupTask = Task.CompletedTask;
    private bool _updatingSeekBar;
    private bool _updatingPlaybackRateCombo;
    private bool _updatingTransportLayout;
    private bool _compactTransport;
    private bool _ultraCompactTransport;
    private bool _seekDragging;
    private bool _seekCommandInProgress;
    private long? _queuedSeekTarget;
    private bool _pauseCommandInProgress;
    private bool _stopCommandInProgress;
    private bool _mediaReady;
    private bool _loadInProgress;
    private bool _playbackIsPlayingUi;
    private bool _detailsExpanded;
    private VideoPlaybackState _lastPublishedPlaybackState = VideoPlaybackState.Empty;
    private IVideoPlaybackBackend? _lastPublishedPlaybackBackend;
    private bool _hasPublishedPlaybackState;
    private volatile bool _userStopped;
    private long _userStoppedPositionMilliseconds;
    private int _videoSeekStepSeconds = 10;
    private int _volumePercent = 100;
    private int _loadGeneration;
    private (Color Background, Color SecondaryBackground, Color PrimaryText, Color SecondaryText, Color Separator, Color Accent, Color DisabledText, bool HighContrast) _chromePalette;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string, int, int, bool, CancellationToken, Task<bool>>? LoadAsyncOverride { get; set; }
    internal bool VideoViewHandleCreated => _videoView.IsHandleCreated;
    internal bool VideoViewVisible => _videoView.Visible;
    internal int PlaybackControlsHeight => ScaleLogical(VideoSurfaceSeparatorHeight + ProgressRowHeight + TransportRowHeight)
        + (_detailsPanel.Visible ? ScaleLogical(DetailsPanelExpandedHeight) : 0)
        + (_statusPanel.Visible ? ScaleLogical(StatusRowHeight) : 0);
    internal bool DetailsExpanded => _detailsPanel.Visible;
    internal int VolumePercent => _volumePercent;
    internal bool PlaybackRateComboFocused => _playbackRateCombo.Focused;
    internal bool PlaybackRateComboDroppedDown => _playbackRateCombo.DroppedDown;
    internal bool HasPlaybackControlFocus => _playPauseButton.ContainsFocus
        || _stopButton.ContainsFocus
        || _skipBackButton.ContainsFocus
        || _skipForwardButton.ContainsFocus
        || _detailsButton.ContainsFocus
        || _seekBar.ContainsFocus
        || _volumeBar.ContainsFocus
        || _playbackRateCombo.ContainsFocus
        || _presentationLockButton.ContainsFocus;
    internal double PlaybackRate => _backend?.PlaybackRate ?? 1d;
    internal MidFD.ViewerIpc.ViewerVideoPlaybackState CurrentPlaybackState
        => ToViewerPlaybackState(GetCurrentPlaybackState());
    internal event Action<double>? PlaybackRateRequested;
    internal event Action? PresentationLockToggleRequested;
    internal event Action<int>? UserVolumeChanged;
    internal event EventHandler<DragEventArgs>? VideoMediaDragEnter;
    internal event EventHandler<DragEventArgs>? VideoMediaDragDrop;
    internal event Action? NativeVideoSurfaceSpaceRequested;
    internal event Action<Keys>? NativeVideoSurfaceArrowKeyRequested;
    internal event Action<MidFD.ViewerIpc.ViewerVideoPlaybackState>? PlaybackStateChanged;
    internal bool PresentationLockButtonVisible => _presentationLockButton.Visible;
    internal bool PresentationLockButtonEnabled => _presentationLockButton.Enabled;
    internal bool PresentationLocked => _presentationLockButton.Icon == MediaChromeIcon.Lock;
    internal void PerformPresentationLockClickForTests() => _presentationLockButton.PerformClick();
    internal void SetPresentationLockState(bool hosted, bool locked, bool automaticVideoState = false)
    {
        _presentationLockButton.Visible = hosted;
        _presentationLockButton.Enabled = hosted;
        _presentationLockButton.Icon = locked ? MediaChromeIcon.Lock : MediaChromeIcon.Unlock;
        ApplyButtonAppearance(_presentationLockButton, locked);
        _presentationLockButton.AccessibleDescription = automaticVideoState
            ? "動画の固定とBrowser選択への追従を切り替えます。再生状態が変わると自動規則を再適用します。"
            : "クリックでBrowser選択への追従と現在の媒体の固定を切り替えます。";
        _presentationLockButton.AccessibleName = automaticVideoState
            ? locked ? "動画固定中 — 追従へ切替" : "動画追従中 — 固定へ切替"
            : locked ? "固定中 — 固定解除" : "追従中 — 固定する";
        _controlToolTip.SetToolTip(_presentationLockButton, automaticVideoState
            ? locked
                ? "動画固定中：クリックでBrowser選択への追従へ切り替えます。再生状態が変わると自動規則を再適用します。"
                : "動画追従中：クリックで現在の動画を固定します。再生状態が変わると自動規則を再適用します。"
            : locked
                ? "固定中：現在の媒体を保持。クリックでBrowser選択への追従へ戻ります。"
                : "追従中：Browser選択へ追従。クリックで現在の媒体を固定します。");
        UpdateTransportLayout();
    }
    internal string StatusMessage => _statusLabel.Text;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int VideoSeekStepSeconds
    {
        get => _videoSeekStepSeconds;
        set
        {
            _videoSeekStepSeconds = Math.Clamp(value, 1, 600);
            UpdateTransportButtonLabels();
        }
    }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool? PlaybackIsPlayingOverrideForTests { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<bool, Task<bool>>? PlaybackCommandOverrideForTests { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<int, Task<bool>>? SeekCommandOverrideForTests { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool? HasMediaOverrideForTests { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool? PlaybackLoadedOverrideForTests { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal MidFD.ViewerIpc.ViewerVideoPlaybackState? PlaybackStateOverrideForTests { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Task<bool>>? StopCommandOverrideForTests { get; set; }
    internal event Action<string, Size, int>? VideoDisplaySizeAvailable;
    internal event Action<bool, int>? DetailsExpandedChanged;

    public EmbeddedVideoPlayerControl()
    {
        BackColor = Color.FromArgb(24, 26, 31);
        Margin = Padding.Empty;
        _ownedChromeFont = CreatePreferredChromeFont(Font);
        if (_ownedChromeFont != null)
        {
            Font = _ownedChromeFont;
        }
        _controlsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = TransportRowHeight,
            Margin = Padding.Empty,
            Padding = new Padding(10, 5, 10, 5),
            ColumnCount = 3,
            RowCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = SystemColors.Control
        };
        _controlsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _controlsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _controlsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _controlsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _transportGroup = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = BackColor
        };
        _secondaryGroup = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = BackColor
        };
        _volumeBar.Size = new Size(ScaleLogical(88), ScaleLogical(24));
        _volumeBar.MinimumSize = new Size(ScaleLogical(40), ScaleLogical(24));
        _volumeBar.Padding = new Padding(4, 0, 4, 0);
        _volumeBar.Margin = new Padding(2, 0, 2, 0);
        _volumeBar.AccessibleName = "音量";
        _volumeBar.AccessibleDescription = "動画の音量を0から100パーセントで調整します。Ctrl+上矢印で5%増加、Ctrl+下矢印で5%減少します。";
        _volumeIcon.Size = new Size(ScaleLogical(18), ScaleLogical(24));
        _volumeIcon.Margin = new Padding(0, 0, 2, 0);
        _volumeValueLabel.Size = new Size(ScaleLogical(40), ScaleLogical(28));
        _volumeValueLabel.Margin = new Padding(2, 0, 2, 0);
        _playbackRateCombo.Size = new Size(ScaleLogical(76), ScaleLogical(26));
        _playbackRateCombo.MinimumSize = new Size(ScaleLogical(70), ScaleLogical(24));
        _playbackRateCombo.Margin = new Padding(0, 0, 4, 0);
        _playbackRateCombo.AccessibleName = "再生速度";
        _playbackRateCombo.AccessibleDescription = "動画の再生速度を選択します。新しい動画は1.0倍速で始まります。";
        _detailsButton.Size = new Size(ScaleLogical(36), ScaleLogical(34));
        _detailsButton.MinimumSize = new Size(ScaleLogical(32), ScaleLogical(32));
        _detailsButton.Margin = new Padding(3, 0, 0, 0);
        _statusPanel.Padding = new Padding(10, 0, 10, 0);
        _statusPanel.Controls.Add(_statusLabel);
        _transportGroup.Controls.AddRange([_playPauseButton, _stopButton, _skipBackButton, _skipForwardButton]);
        _presentationLockButton.Size = _detailsButton.Size;
        _presentationLockButton.Margin = _detailsButton.Margin;
        _presentationLockButton.Click += (_, _) => PresentationLockToggleRequested?.Invoke();
        _secondaryGroup.Controls.AddRange([_playbackRateCombo, _volumeIcon, _volumeBar, _volumeValueLabel, _presentationLockButton, _detailsButton]);
        _controlsPanel.Controls.Add(_transportGroup, 0, 0);
        _controlsPanel.Controls.Add(_transportSpacer, 1, 0);
        _controlsPanel.Controls.Add(_secondaryGroup, 2, 0);

        _progressPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = ProgressRowHeight,
            Margin = Padding.Empty,
            Padding = new Padding(8, 0, 8, 0),
            ColumnCount = 3,
            RowCount = 1,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = BackColor
        };
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _progressPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _currentTimeLabel.Width = 68;
        _currentTimeLabel.MinimumSize = new Size(64, 22);
        _currentTimeLabel.Margin = new Padding(0, 0, 4, 0);
        _durationLabel.Width = 68;
        _durationLabel.MinimumSize = new Size(64, 22);
        _durationLabel.Margin = new Padding(4, 0, 0, 0);
        _seekBar.Height = ScaleLogical(27);
        _seekBar.MinimumSize = new Size(80, ScaleLogical(24));
        _seekBar.Padding = new Padding(6, 0, 6, 0);
        _seekBar.Margin = new Padding(0, 0, 0, 0);
        _progressPanel.Controls.Add(_currentTimeLabel, 0, 0);
        _progressPanel.Controls.Add(_seekBar, 1, 0);
        _progressPanel.Controls.Add(_durationLabel, 2, 0);

        _detailsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = DetailsPanelExpandedHeight,
            Visible = false,
            Margin = Padding.Empty,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = Color.FromArgb(32, 35, 41),
            BorderStyle = BorderStyle.None
        };
        _detailsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = _detailsPanel.BackColor,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        _detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(90)));
        _detailsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 5; row++)
        {
            _detailsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 5f));
        }
        foreach (Label label in new[] { _videoDetailsLabel, _audioDetailsLabel, _playbackDetailsLabel, _engineDetailsLabel, _selectionDetailsLabel })
        {
            label.ForeColor = Color.FromArgb(180, 184, 190);
            label.BackColor = _detailsPanel.BackColor;
            label.Margin = new Padding(2, 0, 4, 0);
        }
        foreach (Label label in new[] { _videoDetailsValueLabel, _audioDetailsValueLabel, _playbackDetailsValueLabel, _engineDetailsValueLabel, _selectionDetailsValueLabel })
        {
            label.ForeColor = Color.FromArgb(238, 238, 238);
            label.BackColor = _detailsPanel.BackColor;
            label.Margin = new Padding(2, 0, 4, 0);
        }
        _detailsLayout.Controls.Add(_videoDetailsLabel, 0, 0);
        _detailsLayout.Controls.Add(_videoDetailsValueLabel, 1, 0);
        _detailsLayout.Controls.Add(_audioDetailsLabel, 0, 1);
        _detailsLayout.Controls.Add(_audioDetailsValueLabel, 1, 1);
        _detailsLayout.Controls.Add(_playbackDetailsLabel, 0, 2);
        _detailsLayout.Controls.Add(_playbackDetailsValueLabel, 1, 2);
        _detailsLayout.Controls.Add(_engineDetailsLabel, 0, 3);
        _detailsLayout.Controls.Add(_engineDetailsValueLabel, 1, 3);
        _detailsLayout.Controls.Add(_selectionDetailsLabel, 0, 4);
        _detailsLayout.Controls.Add(_selectionDetailsValueLabel, 1, 4);
        _detailsPanel.Controls.Add(_detailsLayout);
        _detailsPanel.Paint += (_, e) =>
        {
            using var separator = new Pen(_chromePalette.Separator);
            e.Graphics.DrawLine(separator, 0, 0, _detailsPanel.ClientSize.Width, 0);
        };

        _rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            BackColor = BackColor
        };
        _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(VideoSurfaceSeparatorHeight)));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(ProgressRowHeight)));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(TransportRowHeight)));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _rootLayout.Controls.Add(_videoView, 0, 0);
        _rootLayout.Controls.Add(_videoSurfaceSeparator, 0, 1);
        _rootLayout.Controls.Add(_progressPanel, 0, 2);
        _rootLayout.Controls.Add(_controlsPanel, 0, 3);
        _rootLayout.Controls.Add(_detailsPanel, 0, 4);
        _rootLayout.Controls.Add(_statusPanel, 0, 5);
        Controls.Add(_rootLayout);
        RegisterMediaDropTarget(this);
        RegisterMediaDropTarget(_videoView);

        foreach (MediaChromeButton button in new[] { _playPauseButton, _stopButton, _skipBackButton, _skipForwardButton })
        {
            button.EnabledChanged += (_, _) => ApplyButtonAppearance(button, ReferenceEquals(button, _playPauseButton));
        }
        _detailsButton.EnabledChanged += (_, _) => ApplyDetailsButtonAppearance();
        ApplyChromeTheme();
        UpdateTransportButtonLabels();
        _controlToolTip.SetToolTip(_stopButton, "再生を停止して先頭へ戻します。メディアは閉じません。");
        _controlToolTip.SetToolTip(_seekBar, "再生位置");
        _controlToolTip.SetToolTip(_volumeBar, "音量");
        _controlToolTip.SetToolTip(_volumeIcon, "音量");
        _controlToolTip.SetToolTip(_volumeValueLabel, "音量");
        _controlToolTip.SetToolTip(_detailsButton, "詳細");

        _playPauseButton.Click += async (_, _) => await TogglePlaybackAsync();
        _stopButton.Click += async (_, _) => await StopCurrentMediaAsync();
        _skipBackButton.Click += async (_, _) => await SeekBySecondsAsync(-VideoSeekStepSeconds);
        _skipForwardButton.Click += async (_, _) => await SeekBySecondsAsync(VideoSeekStepSeconds);
        _seekBar.MouseDown += (_, _) => _seekDragging = true;
        _seekBar.Scroll += async (_, _) => await SeekToBarPositionAsync();
        _seekBar.MouseUp += (_, _) =>
        {
            _seekDragging = false;
            _ = SeekToBarPositionAsync();
            RefreshPlaybackPosition();
        };
        _detailsButton.Click += (_, _) => SetDetailsExpanded(!_detailsExpanded);
        _volumeBar.Scroll += (_, _) => SetUserVolume(_volumeBar.Value);
        _playbackRateCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingPlaybackRateCombo || _playbackRateCombo.SelectedItem is not PlaybackRateChoice choice) return;
            RefreshPlaybackRateChoices();
            if (_mediaReady && HasMedia) PlaybackRateRequested?.Invoke(choice.Rate);
        };
        _uiTimer.Tick += (_, _) =>
        {
            // Native LibVLC output HWNDs can be created asynchronously after playback starts.
            // Refresh the child-window bridge while media is active so the real output surface
            // remains covered even when LibVLC does not send WM_PARENTNOTIFY to its host.
            if (_mediaReady && string.Equals(_backend?.BackendKind, VideoPlaybackBackendKind.LibVLC, StringComparison.Ordinal))
                _nativeSurfaceClickBridge?.RefreshNativeWindows();
            RefreshPlaybackPosition();
        };
        SizeChanged += (_, _) => UpdateTransportLayout();
        FontChanged += (_, _) => UpdateTransportLayout();
        _uiTimer.Start();
        RefreshPlaybackRateChoices();
        UpdatePlaybackControlState();
        UpdateTransportLayout();
    }

    internal static (
        Color Background,
        Color SecondaryBackground,
        Color PrimaryText,
        Color SecondaryText,
        Color Separator,
        Color Accent,
        Color DisabledText,
        bool HighContrast) GetChromePalette(bool highContrast)
        => highContrast
            ? (
                SystemColors.Control,
                SystemColors.Control,
                SystemColors.ControlText,
                SystemColors.GrayText,
                SystemColors.ControlDark,
                SystemColors.Highlight,
                SystemColors.GrayText,
                true)
            : (
                Color.FromArgb(24, 26, 31),
                Color.FromArgb(32, 35, 41),
                Color.FromArgb(242, 243, 245),
                Color.FromArgb(174, 179, 188),
                Color.FromArgb(52, 56, 64),
                SystemColors.Highlight,
                SystemColors.GrayText,
                false);

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * Math.Max(1, DeviceDpi) / 96d));

    private static Font? CreatePreferredChromeFont(Font current)
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            string family = fonts.Families.Any(item => string.Equals(item.Name, "Segoe UI Variable", StringComparison.OrdinalIgnoreCase))
                ? "Segoe UI Variable"
                : "Segoe UI";
            if (string.Equals(current.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return new Font(family, current.SizeInPoints, current.Style, GraphicsUnit.Point);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiMetrics();
        UpdateTransportLayout();
    }

    private void ApplyDpiMetrics()
    {
        if (_rootLayout is null) return;
        int Scale(int value) => ScaleLogical(value);
        _rootLayout.RowStyles[1].Height = Scale(VideoSurfaceSeparatorHeight);
        _rootLayout.RowStyles[2].Height = Scale(ProgressRowHeight);
        _rootLayout.RowStyles[3].Height = Scale(TransportRowHeight);
        _rootLayout.RowStyles[4].Height = _detailsPanel.Visible ? Scale(DetailsPanelExpandedHeight) : 0;
        _rootLayout.RowStyles[5].Height = _statusPanel.Visible ? Scale(StatusRowHeight) : 0;
        _controlsPanel.Padding = new Padding(Scale(10), Scale(5), Scale(10), Scale(5));
        _progressPanel.Padding = new Padding(Scale(8), 0, Scale(8), 0);
        _progressPanel.ColumnStyles[0].Width = Scale(72);
        _progressPanel.ColumnStyles[2].Width = Scale(72);
        _detailsPanel.Padding = new Padding(Scale(10), Scale(8), Scale(10), Scale(8));
        _statusPanel.Padding = new Padding(Scale(10), 0, Scale(10), 0);
        _detailsLayout.ColumnStyles[0].Width = Scale(90);
        _detailsPanel.Height = Scale(DetailsPanelExpandedHeight);
        _seekBar.MinimumSize = new Size(Scale(80), Scale(24));
        _seekBar.Height = Scale(27);
        _currentTimeLabel.Width = Scale(68);
        _currentTimeLabel.MinimumSize = new Size(Scale(64), Scale(22));
        _currentTimeLabel.Margin = new Padding(0, 0, Scale(4), 0);
        _durationLabel.Width = Scale(68);
        _durationLabel.MinimumSize = new Size(Scale(64), Scale(22));
        _durationLabel.Margin = new Padding(Scale(4), 0, 0, 0);
        _seekBar.Padding = new Padding(Scale(6), 0, Scale(6), 0);
        _volumeIcon.Size = new Size(Scale(18), Scale(24));
        _volumeBar.Padding = new Padding(Scale(4), 0, Scale(4), 0);
        _volumeValueLabel.Size = new Size(Scale(40), Scale(28));
        _detailsButton.MinimumSize = new Size(Scale(32), Scale(32));
        _detailsButton.Margin = new Padding(Scale(3), 0, 0, 0);
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplyChromeTheme();
    }

    private void ApplyChromeTheme()
    {
        if (_rootLayout is null)
        {
            return;
        }

        _chromePalette = GetChromePalette(SystemInformation.HighContrast);
        BackColor = _chromePalette.Background;
        _rootLayout.BackColor = _chromePalette.Background;
        _controlsPanel.BackColor = _chromePalette.Background;
        _transportGroup.BackColor = _chromePalette.Background;
        _transportSpacer.BackColor = _chromePalette.Background;
        _secondaryGroup.BackColor = _chromePalette.Background;
        _playbackRateCombo.ItemHeight = ScaleLogical(20);
        _playbackRateCombo.ApplyPalette(_chromePalette.SecondaryBackground, _chromePalette.PrimaryText, _chromePalette.Separator, _chromePalette.HighContrast);
        _progressPanel.BackColor = _chromePalette.Background;
        _videoSurfaceSeparator.BackColor = _chromePalette.Separator;
        _videoView.BackColor = Color.Black;
        _volumeBar.ApplyPalette(
            _chromePalette.Background,
            _chromePalette.Separator,
            _chromePalette.Accent,
            _chromePalette.PrimaryText,
            _chromePalette.DisabledText);
        _volumeIcon.ApplyPalette(_chromePalette.Background, _chromePalette.PrimaryText, _chromePalette.DisabledText);
        _volumeValueLabel.BackColor = _chromePalette.Background;
        _volumeValueLabel.ForeColor = _chromePalette.PrimaryText;
        _currentTimeLabel.BackColor = _chromePalette.Background;
        _currentTimeLabel.ForeColor = _chromePalette.PrimaryText;
        _durationLabel.BackColor = _chromePalette.Background;
        _durationLabel.ForeColor = _chromePalette.PrimaryText;
        _statusPanel.BackColor = _chromePalette.SecondaryBackground;
        _statusLabel.BackColor = _chromePalette.SecondaryBackground;
        _statusLabel.ForeColor = _chromePalette.SecondaryText;
        _detailsPanel.BackColor = _chromePalette.SecondaryBackground;
        _detailsLayout.BackColor = _chromePalette.SecondaryBackground;
        foreach (Label label in new[] { _videoDetailsLabel, _audioDetailsLabel, _playbackDetailsLabel, _engineDetailsLabel, _selectionDetailsLabel })
        {
            label.BackColor = _chromePalette.SecondaryBackground;
            label.ForeColor = _chromePalette.SecondaryText;
        }
        foreach (Label label in new[] { _videoDetailsValueLabel, _audioDetailsValueLabel, _playbackDetailsValueLabel, _engineDetailsValueLabel, _selectionDetailsValueLabel })
        {
            label.BackColor = _chromePalette.SecondaryBackground;
            label.ForeColor = _chromePalette.PrimaryText;
        }

        ApplyButtonAppearance(_playPauseButton, primary: true);
        ApplyButtonAppearance(_stopButton, primary: false);
        ApplyButtonAppearance(_skipBackButton, primary: false);
        ApplyButtonAppearance(_skipForwardButton, primary: false);
        ApplyDetailsButtonAppearance();
        ApplyButtonAppearance(_presentationLockButton, _presentationLockButton.Icon == MediaChromeIcon.Lock);
        _seekBar.ApplyPalette(
            _chromePalette.Background,
            _chromePalette.Separator,
            _chromePalette.Accent,
            _chromePalette.PrimaryText,
            _chromePalette.DisabledText);
        _detailsPanel.Invalidate();
    }

    private void ApplyButtonAppearance(MediaChromeButton button, bool primary)
    {
        button.NormalSurface = primary ? _chromePalette.Accent : _chromePalette.SecondaryBackground;
        button.HoverSurface = _chromePalette.HighContrast
            ? SystemColors.ControlLight
            : primary ? ControlPaint.Light(_chromePalette.Accent, 0.16f) : Color.FromArgb(42, 46, 53);
        button.PressedSurface = _chromePalette.HighContrast
            ? SystemColors.ControlDark
            : primary ? ControlPaint.Dark(_chromePalette.Accent, 0.12f) : Color.FromArgb(52, 57, 65);
        button.HighContrast = _chromePalette.HighContrast;
        button.FocusBorder = _chromePalette.HighContrast ? SystemColors.Highlight : _chromePalette.Accent;
        button.BorderColor = _chromePalette.Separator;
        button.DisabledSurface = _chromePalette.HighContrast ? SystemColors.Control : _chromePalette.Background;
        button.DisabledTextColor = _chromePalette.DisabledText;
        button.BackColor = _chromePalette.Background;
        button.ForeColor = primary && _chromePalette.HighContrast ? SystemColors.HighlightText : _chromePalette.PrimaryText;
        if (button != _presentationLockButton) button.AccessibleName = button == _playPauseButton
            ? (_playbackIsPlayingUi ? "一時停止" : "再生")
            : button == _stopButton ? "停止"
            : button == _skipBackButton ? $"{_videoSeekStepSeconds}秒戻る"
            : $"{_videoSeekStepSeconds}秒進む";
        button.Invalidate();
    }

    private void ApplyDetailsButtonAppearance()
    {
        _detailsButton.NormalSurface = _detailsExpanded ? _chromePalette.Accent : _chromePalette.SecondaryBackground;
        _detailsButton.HoverSurface = _chromePalette.HighContrast ? SystemColors.ControlLight : Color.FromArgb(42, 46, 53);
        _detailsButton.PressedSurface = _chromePalette.HighContrast ? SystemColors.ControlDark : Color.FromArgb(52, 57, 65);
        _detailsButton.FocusBorder = _chromePalette.HighContrast ? SystemColors.Highlight : _chromePalette.Accent;
        _detailsButton.HighContrast = _chromePalette.HighContrast;
        _detailsButton.BorderColor = _chromePalette.Separator;
        _detailsButton.DisabledSurface = _chromePalette.HighContrast ? SystemColors.Control : _chromePalette.Background;
        _detailsButton.DisabledTextColor = _chromePalette.DisabledText;
        _detailsButton.BackColor = _chromePalette.Background;
        _detailsButton.ForeColor = _detailsExpanded && _chromePalette.HighContrast ? SystemColors.HighlightText : _chromePalette.PrimaryText;
        _detailsButton.AccessibleName = _detailsExpanded ? "動画の詳細情報を閉じる" : "動画の詳細情報を表示";
        _detailsButton.Invalidate();
    }

    public bool HasMedia => HasMediaOverrideForTests ?? _backend?.HasMedia == true;
    public bool IsPlaying => _backend?.IsPlaying ?? PlaybackIsPlayingOverrideForTests == true;
    public event Action<string>? PlaybackFailed;

    public async Task<bool> LoadAsync(
        string path,
        int startSeconds,
        int volumePercent,
        bool autoplay,
        CancellationToken cancellationToken,
        int ownerMediaGeneration = 0,
        bool preserveCurrentMedia = false)
    {
        if (preserveCurrentMedia)
        {
            Task<bool> followTask = LoadSelectionFollowAsync(path, startSeconds, volumePercent, cancellationToken, ownerMediaGeneration);
            _selectionFollowTask = followTask;
            try { return await followTask; }
            finally
            {
                if (ReferenceEquals(_selectionFollowTask, followTask)) _selectionFollowTask = Task.FromResult(false);
            }
        }
        _loadInProgress = true;
        _mediaReady = false;
        PlaybackLoadedOverrideForTests = null;
        PublishPlaybackState(VideoPlaybackState.Opening);
        if (autoplay && !PrepareVideoSurface(requireVisibleHost: true))
        {
            _loadInProgress = false;
            PublishPlaybackState(VideoPlaybackState.Error);
            return false;
        }
        if (LoadAsyncOverride is { } loadAsyncOverride)
        {
            try
            {
                SetVolume(volumePercent);
                bool result = await loadAsyncOverride(path, startSeconds, volumePercent, autoplay, cancellationToken);
                _mediaReady = result; PlaybackLoadedOverrideForTests = result;
                SetStatusMessage(result ? null : "動画を再生できませんでした。");
                SetPlaybackUiState(result && autoplay); RefreshPlaybackRateChoices(); UpdatePlaybackControlState();
                _loadInProgress = false;
                PublishPlaybackState(result ? null : VideoPlaybackState.Error);
                return result;
            }
            catch
            {
                _loadInProgress = false;
                PublishPlaybackState(VideoPlaybackState.Error);
                throw;
            }
        }
        Task cleanup = StopPlaybackCore();
        int generation = ++_loadGeneration;
        _loadInProgress = true;
        _mediaReady = false;
        PublishPlaybackState(VideoPlaybackState.Opening);
        string requestedMode = _backendKind;
        var selectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _selectionCts = selectionCts;
        _selection = null;
        _activePath = path; _ownerMediaGeneration = ownerMediaGeneration;
        try
        {
            await cleanup;
            if (generation != _loadGeneration || selectionCts.IsCancellationRequested || IsDisposed) return false;
            SetStatusMessage("動画を読み込んでいます...");
            var selection = await _backendSelector.SelectAsync(requestedMode, path, selectionCts.Token);
            if (generation != _loadGeneration || selectionCts.IsCancellationRequested || IsDisposed) return false;
            _selection = selection;
            Services.LogService.Info($"[MidPlayerTiming] stage=video-backend-selection requested={selection.RequestedMode} selected={selection.SelectedBackend ?? "none"} libVlc={selection.LibVlcCapability.State} mf={selection.MfCapability.State} videoCodec={selection.VideoCodec} audioCodec={selection.AudioCodec} codecCache={selection.CodecInfo?.FromCache} mfCache={selection.MfCapability.FromCache} elapsedMs={selection.ElapsedMilliseconds:0.0} reason={selection.Reason}");
            RefreshPlaybackDetails(_backend);
            if (selection.SelectedBackend == null)
            {
                await _backendSelector.ReleaseProbeResourcesAsync();
                if (generation == _loadGeneration) SetStatusMessage(selection.Reason);
                if (generation == _loadGeneration) PublishPlaybackState(VideoPlaybackState.Error);
                return false;
            }
            if (_backend?.BackendKind != selection.SelectedBackend)
            {
                if (_backend is { } previous)
                {
                    _backend = null;
                    _pendingCleanupTask = previous.DisposeAsync().AsTask();
                    await _pendingCleanupTask;
                }
                if (generation != _loadGeneration || selectionCts.IsCancellationRequested) return false;
                var backend = _backend = VideoPlaybackBackendFactory.Create(selection.SelectedBackend);
                _videoView.Controls.Add(backend.RenderSurface);
                RegisterMediaDropTarget(backend.RenderSurface);
                backend.PlaybackStateChanged += () =>
                    QueueBackendStateRefresh(backend);
                backend.PlaybackFailed += message =>
                    DispatchBackendCallback(backend, () =>
                    {
                        SetStatusMessage(message);
                        PlaybackFailed?.Invoke(message);
                        PublishPlaybackState(VideoPlaybackState.Error);
                    });
                backend.VideoDisplaySizeAvailable += size =>
                    DispatchBackendCallback(backend, () => VideoDisplaySizeAvailable?.Invoke(_activePath, size, _ownerMediaGeneration));
            }
            if (!PrepareVideoSurface(requireVisibleHost: autoplay))
            {
                PublishPlaybackState(VideoPlaybackState.Error);
                return false;
            }
            SetVolume(volumePercent);
            SetStatusMessage("動画を読み込んでいます...");
            var current = _backend!;
            EnsureNativeVideoSurfaceClickBridge(current.RenderSurface);
            if (selection.CodecInfo != null)
                await _backendSelector.TransferProbeResourcesAsync(current, selectionCts.Token);
            if (generation != _loadGeneration || selectionCts.IsCancellationRequested) return false;
            if (current is IVideoPlaybackProbeConsumer consumer) consumer.UseCodecProbe(selection.CodecInfo);
            bool loaded = await current.LoadAsync(path, startSeconds, volumePercent, autoplay, selectionCts.Token);
            if (generation != _loadGeneration) return false;
            if (selectionCts.IsCancellationRequested) { await StopPlaybackCore(); return false; }
            if (!loaded)
            {
                string error = current.LastError ?? "動画を開けませんでした。";
                await StopPlaybackCore();
                SetStatusMessage(error);
                if (generation + 1 == _loadGeneration) PublishPlaybackState(VideoPlaybackState.Error);
                return false;
            }
            // LibVLC creates its native video child HWND during playback startup, after the
            // initial bridge is attached to RenderSurface. Refresh now so clicks and wheel
            // messages from the actual output window are observed as well.
            _nativeSurfaceClickBridge?.RefreshNativeWindows();
            _mediaReady = true; PlaybackLoadedOverrideForTests = null;
            _loadInProgress = false;
            SetPlaybackUiState(autoplay); SetStatusMessage(null); RefreshPlaybackRateChoices();
            RefreshPlaybackPosition(); UpdatePlaybackControlState();
            return true;
        }
        catch (OperationCanceledException)
        {
            if (generation == _loadGeneration) PublishPlaybackState(VideoPlaybackState.Empty);
            return false;
        }
        catch (Exception error)
        {
            if (generation == _loadGeneration)
            {
                await StopPlaybackCore();
                SetStatusMessage(error.Message); PlaybackFailed?.Invoke(error.Message);
                if (generation + 1 == _loadGeneration) PublishPlaybackState(VideoPlaybackState.Error);
            }
            return false;
        }
        finally
        {
            if (generation == _loadGeneration && selectionCts.IsCancellationRequested)
                await StopPlaybackCore();
            if (generation == _loadGeneration)
            {
                _loadInProgress = false;
                if (!_mediaReady && _lastPublishedPlaybackState == VideoPlaybackState.Opening)
                    PublishPlaybackState(VideoPlaybackState.Error);
            }
            if (ReferenceEquals(_selectionCts, selectionCts)) _selectionCts = null;
            selectionCts.Dispose();
        }
    }

    private async Task<bool> LoadSelectionFollowAsync(string path, int startSeconds, int volumePercent, CancellationToken cancellationToken, int ownerMediaGeneration)
    {
        await CancelPendingSelectionFollowLoad();
        int generation = Interlocked.Increment(ref _selectionFollowGeneration);
        var selectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _selectionFollowCts = selectionCts;
        IVideoPlaybackBackend? candidate = null;
        bool transferredProbe = false;
        try
        {
            if (LoadAsyncOverride is { } loadAsyncOverride)
            {
                bool overrideLoaded = await loadAsyncOverride(path, startSeconds, 0, true, selectionCts.Token);
                if (!overrideLoaded || generation != _selectionFollowGeneration || selectionCts.IsCancellationRequested || IsDisposed)
                    return false;
                _pendingFollowOverrideLoaded = true;
                return true;
            }
            if (!PrepareVideoSurface(requireVisibleHost: true)) return false;
            var selection = await _backendSelector.SelectAsync(_backendKind, path, selectionCts.Token);
            if (generation != _selectionFollowGeneration || selectionCts.IsCancellationRequested || IsDisposed) return false;
            if (selection.SelectedBackend == null)
            {
                await _backendSelector.ReleaseProbeResourcesAsync();
                return false;
            }

            candidate = VideoPlaybackBackendFactory.Create(selection.SelectedBackend);
            _videoView.Controls.Add(candidate.RenderSurface);
            RegisterMediaDropTarget(candidate.RenderSurface);
            candidate.RenderSurface.Visible = true;
            candidate.RenderSurface.SendToBack();
            if (_backend is { } active) active.RenderSurface.BringToFront();
            if (candidate is IVideoPlaybackProbeConsumer consumer) consumer.UseCodecProbe(selection.CodecInfo);
            if (selection.CodecInfo != null)
            {
                await _backendSelector.TransferProbeResourcesAsync(candidate, selectionCts.Token);
                transferredProbe = true;
            }
            bool loaded = await candidate.LoadAsync(path, startSeconds, 0, autoplay: true, selectionCts.Token);
            if (!loaded || generation != _selectionFollowGeneration || selectionCts.IsCancellationRequested || IsDisposed)
            {
                return false;
            }

            _pendingFollowBackend = candidate;
            _pendingFollowSelection = selection;
            candidate = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            if (generation == _selectionFollowGeneration)
            {
                Services.LogService.Warn($"[MidPlayer Video] selection-follow candidate failed: {error.Message}");
            }
            return false;
        }
        finally
        {
            if (candidate != null)
            {
                candidate.RenderSurface.Visible = false;
                await candidate.DisposeAsync();
                if (transferredProbe) await _backendSelector.ReleaseProbeResourcesAsync();
            }
            if (ReferenceEquals(_selectionFollowCts, selectionCts)) _selectionFollowCts = null;
            selectionCts.Dispose();
        }
    }

    internal Task CancelPendingSelectionFollowLoad()
    {
        Interlocked.Increment(ref _selectionFollowGeneration);
        try { _selectionFollowCts?.Cancel(); } catch (ObjectDisposedException) { }
        Task loadingTask = _selectionFollowTask;
        IVideoPlaybackBackend? candidate = _pendingFollowBackend;
        _pendingFollowBackend = null;
        _pendingFollowSelection = null;
        _pendingFollowOverrideLoaded = false;
        if (candidate == null) return loadingTask;
        candidate.RenderSurface.Visible = false;
        Task cleanup = DisposeBackendAsync(Task.CompletedTask, candidate);
        return _pendingCleanupTask = Task.WhenAll(loadingTask, cleanup);
    }

    internal bool CommitPendingSelectionFollow(string path, int volumePercent, int ownerMediaGeneration)
    {
        if (_pendingFollowOverrideLoaded)
        {
            _pendingFollowOverrideLoaded = false;
            SetVolume(volumePercent);
            _mediaReady = true;
            PlaybackLoadedOverrideForTests = true;
            _userStopped = false;
            SetPlaybackUiState(true);
            SetStatusMessage(null);
            RefreshPlaybackRateChoices();
            UpdatePlaybackControlState();
            PublishPlaybackState();
            return true;
        }
        IVideoPlaybackBackend? candidate = _pendingFollowBackend;
        VideoBackendSelection? selection = _pendingFollowSelection;
        if (candidate == null || selection == null) return false;
        _pendingFollowBackend = null;
        _pendingFollowSelection = null;
        _selection = selection;
        _activePath = path;
        _ownerMediaGeneration = ownerMediaGeneration;
        candidate.RenderSurface.Visible = true;
        candidate.RenderSurface.BringToFront();
        IVideoPlaybackBackend? previous = _backend;
        _backend = candidate;
        AttachActiveBackendEvents(candidate);
        EnsureNativeVideoSurfaceClickBridge(candidate.RenderSurface);
        SetVolume(volumePercent);
        _mediaReady = true;
        PlaybackLoadedOverrideForTests = null;
        _userStopped = false;
        SetPlaybackUiState(true);
        SetStatusMessage(null);
        RefreshPlaybackDetails(candidate);
        RefreshPlaybackRateChoices();
        RefreshPlaybackPosition();
        UpdatePlaybackControlState();
        PublishPlaybackState();
        if (previous != null)
        {
            previous.RenderSurface.Visible = false;
            _pendingCleanupTask = previous.DisposeAsync().AsTask();
        }
        return true;
    }

    private void AttachActiveBackendEvents(IVideoPlaybackBackend backend)
    {
        backend.PlaybackStateChanged += () => QueueBackendStateRefresh(backend);
        backend.PlaybackFailed += message =>
            DispatchBackendCallback(backend, () =>
            {
                SetStatusMessage(message);
                PlaybackFailed?.Invoke(message);
                PublishPlaybackState(VideoPlaybackState.Error);
            });
        backend.VideoDisplaySizeAvailable += size =>
            DispatchBackendCallback(backend, () => VideoDisplaySizeAvailable?.Invoke(_activePath, size, _ownerMediaGeneration));
    }

    private void QueueBackendStateRefresh(IVideoPlaybackBackend backend)
    {
        int generation = Volatile.Read(ref _loadGeneration);
        DispatchBackendCallback(backend, () =>
        {
            if (generation != _loadGeneration) return;
            RefreshPlaybackPosition();
        });
    }

    private void DispatchBackendCallback(IVideoPlaybackBackend backend, Action callback)
    {
        if (IsDisposed || !ReferenceEquals(_backend, backend)) return;
        if (!InvokeRequired)
        {
            if (!IsDisposed && ReferenceEquals(_backend, backend)) callback();
            return;
        }
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && ReferenceEquals(_backend, backend)) callback();
            }));
        }
        catch (InvalidOperationException) { }
    }

    internal async Task<bool> TogglePlaybackAsync()
    {
        VideoInputTrace.Record("toggle-entry", $"backend={_backend?.BackendKind} playing={_backend?.IsPlaying} busy={_pauseCommandInProgress}");
        var backend = _backend;
        var commandOverride = PlaybackCommandOverrideForTests;
        if ((backend == null && commandOverride == null) || _pauseCommandInProgress || _stopCommandInProgress) return false;
        int generation = _loadGeneration;
        bool pause = backend?.IsPlaying ?? PlaybackIsPlayingOverrideForTests == true;
        _pauseCommandInProgress = true; UpdatePlaybackControlState();
        try
        {
            bool succeeded = commandOverride != null ? await Task.Run(() => commandOverride(pause))
                : pause ? await backend!.PauseAsync() : await backend!.PlayAsync();
            if (generation != _loadGeneration || !ReferenceEquals(backend, _backend)) return false;
            if (!succeeded)
            {
                string error = backend?.LastError ?? "動画を再生できません。";
                SetStatusMessage(error); PlaybackFailed?.Invoke(error); PublishPlaybackState(VideoPlaybackState.Error); return false;
            }
            SetPlaybackUiState(!pause);
            PublishPlaybackState();
            VideoInputTrace.Record("toggle-result", $"backend={backend?.BackendKind} success={succeeded} playing={backend?.IsPlaying}");
            if (!pause) { _userStopped = false; SetStatusMessage(null); }
            return !pause;
        }
        catch (Exception error)
        {
            if (generation == _loadGeneration)
            {
                SetStatusMessage(error.Message); PlaybackFailed?.Invoke(error.Message); PublishPlaybackState(VideoPlaybackState.Error);
            }
            return false;
        }
        finally { if (generation == _loadGeneration) { _pauseCommandInProgress = false; UpdatePlaybackControlState(); } }
    }

    internal async Task<VideoFrameCaptureResult> CaptureCurrentFrameAsync(string outputPath, CancellationToken cancellationToken)
    {
        IVideoPlaybackBackend? backend = _backend;
        int generation = _loadGeneration;
        if (!_mediaReady || backend == null || !HasMedia)
            return new(false, "保存できる動画フレームがありません。");
        if (backend.State is not (VideoPlaybackState.Playing or VideoPlaybackState.Paused))
            return new(false, "再生中または一時停止中の動画だけ保存できます。");

        VideoFrameCaptureResult result = await backend.CaptureFrameAsync(outputPath, cancellationToken);
        return generation == _loadGeneration && ReferenceEquals(backend, _backend)
            ? result
            : new(false, "保存中に再生媒体が切り替わりました。");
    }

    private void EnsureNativeVideoSurfaceClickBridge(Control surface)
    {
        if (ReferenceEquals(surface, _nativeSurfaceClickRoot))
        {
            _nativeSurfaceClickBridge?.RefreshNativeWindows();
            return;
        }
        _nativeSurfaceClickBridge?.Dispose();
        _nativeSurfaceClickRoot = surface;
        _nativeSurfaceClickBridge = new NativeVideoSurfaceClickBridge(
            surface,
            () =>
            {
                if (_mediaReady && ReferenceEquals(_backend?.RenderSurface, surface))
                    QueueNativeSurfacePlaybackToggle();
            },
            delta =>
            {
                if (_mediaReady && ReferenceEquals(_backend?.RenderSurface, surface))
                    AdjustVolume(delta);
            },
            () =>
            {
                if (ReferenceEquals(_backend?.RenderSurface, surface))
                    NativeVideoSurfaceSpaceRequested?.Invoke();
            },
            key =>
            {
                if (ReferenceEquals(_backend?.RenderSurface, surface))
                    NativeVideoSurfaceArrowKeyRequested?.Invoke(key);
            });
    }

    internal void SetMediaDropTargetsEnabled(bool enabled)
    {
        _mediaDropTargetsEnabled = enabled;
        foreach (Control target in _mediaDropTargets)
            target.AllowDrop = enabled;
    }

    private void RegisterMediaDropTarget(Control target)
    {
        if (!_mediaDropTargets.Add(target)) return;
        target.AllowDrop = _mediaDropTargetsEnabled;
        target.DragEnter += MediaDropTarget_DragEnter;
        target.DragDrop += MediaDropTarget_DragDrop;
        target.Disposed += MediaDropTarget_Disposed;
    }

    private void MediaDropTarget_Disposed(object? sender, EventArgs e)
    {
        if (sender is not Control target) return;
        target.DragEnter -= MediaDropTarget_DragEnter;
        target.DragDrop -= MediaDropTarget_DragDrop;
        target.Disposed -= MediaDropTarget_Disposed;
        _mediaDropTargets.Remove(target);
    }

    private void MediaDropTarget_DragEnter(object? sender, DragEventArgs e)
        => VideoMediaDragEnter?.Invoke(sender, e);

    private void MediaDropTarget_DragDrop(object? sender, DragEventArgs e)
        => VideoMediaDragDrop?.Invoke(sender, e);

    private void QueueNativeSurfacePlaybackToggle()
    {
        IVideoPlaybackBackend? backend = _backend;
        if (!_mediaReady || backend == null || !backend.HasMedia) return;
        _nativeSurfaceClickToggleQueue.Enqueue((_loadGeneration, backend));
        if (_drainingNativeSurfaceClickToggleQueue) return;
        _drainingNativeSurfaceClickToggleQueue = true;
        _ = DrainNativeSurfaceClickToggleQueueAsync();
    }

    private async Task DrainNativeSurfaceClickToggleQueueAsync()
    {
        try
        {
            while (!IsDisposed && _nativeSurfaceClickToggleQueue.TryDequeue(out var request))
            {
                if (request.Generation != _loadGeneration
                    || !ReferenceEquals(request.Backend, _backend)
                    || !_mediaReady
                    || !request.Backend.HasMedia)
                {
                    continue;
                }

                await TogglePlaybackAsync();
            }
        }
        finally
        {
            _drainingNativeSurfaceClickToggleQueue = false;
            if (!IsDisposed && _nativeSurfaceClickToggleQueue.Count > 0)
            {
                _drainingNativeSurfaceClickToggleQueue = true;
                _ = DrainNativeSurfaceClickToggleQueueAsync();
            }
        }
    }

    public void SeekBySeconds(int delta)
    {
        if (SeekCommandOverrideForTests is { } seekOverride)
        {
            _ = seekOverride(delta);
            return;
        }

        _ = SeekBySecondsAsync(delta);
    }

    public void SeekToSeconds(int seconds)
    {
        _ = SeekToTimeAsync(Math.Max(0L, seconds) * 1000L);
    }

    internal async Task<bool> StopCurrentMediaAsync()
    {
        var backend = _backend; var stopOverride = StopCommandOverrideForTests;
        if ((backend == null && stopOverride == null) || _stopCommandInProgress) return false;
        int generation = _loadGeneration;
        _stopCommandInProgress = true; UpdatePlaybackControlState();
        try
        {
            bool success = stopOverride != null ? await Task.Run(stopOverride) : await backend!.StopAsync();
            if (generation != _loadGeneration || !ReferenceEquals(backend, _backend)) return false;
            if (!success) { SetStatusMessage(backend?.LastError ?? "動画を停止できませんでした。"); return false; }
            _userStopped = true;
            _userStoppedPositionMilliseconds = backend?.Position ?? 0;
            SetDisplayedTime(_userStoppedPositionMilliseconds, backend?.Duration ?? 0);
            SetPlaybackUiState(false); SetStatusMessage(null); RefreshPlaybackDetails(backend);
            PublishPlaybackState(VideoPlaybackState.Stopped);
            return true;
        }
        catch (Exception error) { if (generation == _loadGeneration) { SetStatusMessage(error.Message); PlaybackFailed?.Invoke(error.Message); } return false; }
        finally { if (generation == _loadGeneration) { _stopCommandInProgress = false; UpdatePlaybackControlState(); } }
    }

    public void StopPlayback()
    {
        _ = StopPlaybackCore();
    }

    private Task StopPlaybackCore()
    {
        _selectionCts?.Cancel();
        Task followCleanup = CancelPendingSelectionFollowLoad();
        ++_loadGeneration;
        _mediaReady = false; _loadInProgress = false; PlaybackLoadedOverrideForTests = null;
        _userStopped = false; _userStoppedPositionMilliseconds = 0;
        _pauseCommandInProgress = false; _stopCommandInProgress = false;
        _seekCommandInProgress = false; _queuedSeekTarget = null;
        SetDetailsExpanded(false); SetPlaybackUiState(false); SetDisplayedTime(0, 0);
        UpdatePlaybackControlState();
        RefreshPlaybackRateChoices();
        _playPauseButton.Enabled = true;
        PublishPlaybackState(VideoPlaybackState.Empty);
        Task playbackCleanup = _backend?.StopForReuseAsync() ?? _pendingCleanupTask;
        return _pendingCleanupTask = FinishPlaybackCleanupAsync(playbackCleanup, followCleanup);
    }

    private async Task FinishPlaybackCleanupAsync(Task playbackCleanup, Task followCleanup)
    {
        await Task.WhenAll(playbackCleanup, followCleanup);
        await _backendSelector.ReleaseProbeResourcesAsync();
    }

    internal Task StopForReuseAsync() => StopPlaybackCore();

    internal bool PrepareVideoSurface(bool requireVisibleHost = false)
    {
        if (IsDisposed || Disposing)
        {
            throw new ObjectDisposedException(nameof(EmbeddedVideoPlayerControl));
        }

        Form? host = FindForm();
        if (requireVisibleHost && (host == null || !host.Visible || !host.IsHandleCreated))
        {
            return false;
        }

        Visible = true;
        _videoView.Visible = true;
        if (requireVisibleHost)
        {
            PerformLayout();
            _rootLayout.PerformLayout();
            _videoView.PerformLayout();
        }
        _ = Handle;
        if (!_videoView.IsHandleCreated)
        {
            _ = _videoView.Handle;
        }
        if (requireVisibleHost)
        {
            _videoView.BringToFront();
        }

        if (_backend is { } backend)
        {
            backend.RenderSurface.Visible = true;
            _ = backend.RenderSurface.Handle;
            backend.RenderSurface.BringToFront();
        }
        return !requireVisibleHost || IsVisibleVideoSurfaceReady();
    }

    private bool IsVisibleVideoSurfaceReady()
    {
        Form? host = FindForm();
        return host != null
            && host.Visible
            && host.IsHandleCreated
            && Visible
            && IsHandleCreated
            && _videoView.Visible
            && _videoView.IsHandleCreated
            && (_backend == null || (_backend.RenderSurface.Visible && _backend.RenderSurface.IsHandleCreated));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Stop();
            _nativeSurfaceClickBridge?.Dispose();
            _nativeSurfaceClickBridge = null;
            _nativeSurfaceClickRoot = null;
            var backend = _backend;
            Task cleanup = StopPlaybackCore();
            _backend = null;
            if (backend != null) _ = DisposeBackendAsync(cleanup, backend);
            _uiTimer.Dispose(); _controlToolTip.Dispose();
            _ownedChromeFont?.Dispose(); _ownedChromeFont = null;
        }
        base.Dispose(disposing);
    }
    private static async Task DisposeBackendAsync(Task cleanup, IVideoPlaybackBackend backend)
    {
        try { await cleanup; } finally { await backend.DisposeAsync(); }
    }

    internal static Size CalculateVideoDisplaySize(uint encodedWidth, uint encodedHeight, uint sampleAspectRatioNumerator, uint sampleAspectRatioDenominator, bool swapsDimensions)
        => VideoPlaybackGeometry.CalculateDisplaySize(encodedWidth, encodedHeight, sampleAspectRatioNumerator, sampleAspectRatioDenominator, swapsDimensions);

    private static string FormatPlaybackState(VideoPlaybackState state) => state switch
    {
        VideoPlaybackState.Playing => "再生中",
        VideoPlaybackState.Paused => "一時停止",
        VideoPlaybackState.Stopped or VideoPlaybackState.Ended => "停止",
        VideoPlaybackState.Opening => "準備中",
        VideoPlaybackState.Error => "再生エラー",
        _ => "—"
    };

    private int MeasureButtonWidth(Button button, string text, int minimumWidth)
    {
        Size textSize = TextRenderer.MeasureText(text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        return Math.Max(minimumWidth, textSize.Width + button.Padding.Horizontal + 8);
    }

    private void SetPlaybackUiState(bool playing)
    {
        if (_playbackIsPlayingUi == playing) return;
        _playbackIsPlayingUi = playing;
        UpdateTransportButtonLabels();
    }

    private void ApplyTransportButtonLabels(bool compact)
    {
        ApplyTransportButtonLabels(compact, ultraCompact: false);
    }

    private void ApplyTransportButtonLabels(bool compact, bool ultraCompact)
    {
        _playPauseButton.Text = string.Empty;
        _playPauseButton.Icon = _playbackIsPlayingUi ? MediaChromeIcon.Pause : MediaChromeIcon.Play;
        _stopButton.Text = string.Empty;
        _skipBackButton.Text = string.Empty;
        _skipForwardButton.Text = string.Empty;
        _skipBackButton.Icon = MediaChromeIcon.SeekBackward;
        _skipForwardButton.Icon = MediaChromeIcon.SeekForward;

        int buttonHeight = ScaleLogical(34);
        _playPauseButton.Size = new Size(ScaleLogical(ultraCompact ? 32 : compact ? 34 : 40), buttonHeight);
        _stopButton.Size = new Size(ScaleLogical(ultraCompact ? 30 : compact ? 32 : 36), buttonHeight);
        int minimumSkipWidth = ScaleLogical(ultraCompact ? 30 : compact ? 32 : 36);
        int skipWidth = Math.Max(
            minimumSkipWidth,
            Math.Max(
                MeasureButtonWidth(_skipBackButton, _skipBackButton.Text, minimumSkipWidth),
                MeasureButtonWidth(_skipForwardButton, _skipForwardButton.Text, minimumSkipWidth)));
        _skipBackButton.Size = new Size(skipWidth, buttonHeight);
        _skipForwardButton.Size = new Size(skipWidth, buttonHeight);
        MediaChromeButton[] transportButtons = [_playPauseButton, _stopButton, _skipBackButton, _skipForwardButton];
        for (int index = 0; index < transportButtons.Length; index++)
        {
            MediaChromeButton button = transportButtons[index];
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Margin = new Padding(0, 0, index == transportButtons.Length - 1 ? 0 : ScaleLogical(compact ? 4 : 6), 0);
        }
        int volumeWidth = ScaleLogical(ultraCompact ? 40 : compact ? 58 : 88);
        int playbackRateWidth = ScaleLogical(ultraCompact ? 58 : compact ? 66 : 76);
        _playbackRateCombo.MinimumSize = new Size(ScaleLogical(ultraCompact ? 54 : compact ? 62 : 70), ScaleLogical(24));
        _playbackRateCombo.Size = new Size(playbackRateWidth, ScaleLogical(26));
        _playbackRateCombo.Margin = new Padding(0, 0, ScaleLogical(compact ? 2 : 4), 0);
        _volumeBar.MinimumSize = new Size(volumeWidth, ScaleLogical(24));
        _volumeBar.Size = new Size(volumeWidth, ScaleLogical(24));
        _volumeIcon.Margin = new Padding(0, 0, ScaleLogical(compact ? 1 : 2), 0);
        _volumeBar.Margin = new Padding(ScaleLogical(ultraCompact ? 1 : 2), 0, ScaleLogical(ultraCompact ? 1 : 2), 0);
        _volumeValueLabel.Size = new Size(ScaleLogical(compact ? 36 : 40), ScaleLogical(28));
        _volumeValueLabel.Margin = new Padding(ScaleLogical(ultraCompact ? 1 : 2), 0, ScaleLogical(ultraCompact ? 1 : 2), 0);
        Size detailsSize = new(ScaleLogical(ultraCompact ? 30 : compact ? 32 : 36), buttonHeight);
        _detailsButton.MinimumSize = detailsSize;
        _detailsButton.Size = detailsSize;
        _presentationLockButton.MinimumSize = detailsSize;
        _presentationLockButton.Size = detailsSize;
        _presentationLockButton.Margin = new Padding(ScaleLogical(compact ? 2 : 3), 0, 0, 0);
        _detailsButton.Margin = new Padding(ScaleLogical(compact ? 2 : 3), 0, 0, 0);
        ApplyButtonAppearance(_playPauseButton, primary: true);
        ApplyButtonAppearance(_stopButton, primary: false);
        ApplyButtonAppearance(_skipBackButton, primary: false);
        ApplyButtonAppearance(_skipForwardButton, primary: false);
        ApplyDetailsButtonAppearance();
        _controlToolTip.SetToolTip(_playPauseButton, _playbackIsPlayingUi ? "一時停止" : "再生");
        _controlToolTip.SetToolTip(_stopButton, "再生を停止して先頭へ戻します。メディアは閉じません。");
        _controlToolTip.SetToolTip(_skipBackButton, $"{_videoSeekStepSeconds}秒戻る");
        _controlToolTip.SetToolTip(_skipForwardButton, $"{_videoSeekStepSeconds}秒進む");
        _volumeValueLabel.Text = $"{_volumePercent}%";
    }

    private void UpdateTransportButtonLabels()
    {
        if (_skipBackButton.IsDisposed || _skipForwardButton.IsDisposed) return;
        ApplyTransportButtonLabels(_compactTransport, _ultraCompactTransport);
        UpdateTransportLayout();
    }

    private void UpdateTransportLayout()
    {
        if (_controlsPanel == null || _controlsPanel.IsDisposed || _updatingTransportLayout) return;
        _updatingTransportLayout = true;
        try
        {
            ApplyTransportButtonLabels(compact: false);
            _transportGroup.PerformLayout();
            _secondaryGroup.PerformLayout();
            _controlsPanel.PerformLayout();
            int requiredWidth = _transportGroup.PreferredSize.Width
                + _secondaryGroup.PreferredSize.Width
                + _controlsPanel.Padding.Horizontal;
            bool compact = _controlsPanel.ClientSize.Width > 0 && _controlsPanel.ClientSize.Width < requiredWidth;
            _compactTransport = compact;
            if (compact)
            {
                ApplyTransportButtonLabels(compact: true);
                _transportGroup.PerformLayout();
                _secondaryGroup.PerformLayout();
                _controlsPanel.PerformLayout();
                requiredWidth = _transportGroup.PreferredSize.Width
                    + _secondaryGroup.PreferredSize.Width
                    + _controlsPanel.Padding.Horizontal;
                bool ultraCompact = _controlsPanel.ClientSize.Width > 0 && _controlsPanel.ClientSize.Width < requiredWidth;
                _ultraCompactTransport = ultraCompact;
                if (ultraCompact)
                {
                    ApplyTransportButtonLabels(compact: true, ultraCompact: true);
                    _transportGroup.PerformLayout();
                    _secondaryGroup.PerformLayout();
                    _controlsPanel.PerformLayout();
                }
            }
            else
            {
                _ultraCompactTransport = false;
            }
        }
        finally
        {
            _updatingTransportLayout = false;
        }
        VideoInputTrace.Chrome("lock", _presentationLockButton, $"icon={_presentationLockButton.Icon}");
        VideoInputTrace.Chrome("rate", _playbackRateCombo);
    }

    private void SetVolume(int value)
    {
        _volumePercent = Math.Clamp(value, 0, 100);
        if (_volumeBar.Value != _volumePercent) _volumeBar.Value = _volumePercent;
        _volumeValueLabel.Text = $"{_volumePercent}%";
        try { _backend?.SetVolume(_volumePercent); }
        catch (Exception error) { SetStatusMessage(error.Message); PlaybackFailed?.Invoke(error.Message); }
        VideoInputTrace.Record("volume-ui", $"value={_volumePercent} label={_volumeValueLabel.Text}");
    }

    private void SetUserVolume(int value)
    {
        int previous = _volumePercent;
        SetVolume(value);
        if (_volumePercent != previous) UserVolumeChanged?.Invoke(_volumePercent);
    }

    internal bool SetPlaybackRate(double rate)
    {
        IVideoPlaybackBackend? backend = _backend;
        if (!_mediaReady || backend == null || !HasMedia)
        {
            RefreshPlaybackRateChoices();
            return false;
        }
        bool applied = backend.TrySetPlaybackRate(rate, out string? failureReason);
        RefreshPlaybackRateChoices();
        if (!applied)
        {
            if (!string.IsNullOrWhiteSpace(failureReason)) SetStatusMessage(failureReason);
            return false;
        }
        SetStatusMessage(null);
        RefreshPlaybackDetails(backend);
        return true;
    }

    private void RefreshPlaybackRateChoices()
    {
        if (_playbackRateCombo.IsDisposed) return;
        IVideoPlaybackBackend? backend = _backend;
        IReadOnlyList<double> rates = backend?.SupportedPlaybackRates ?? VideoPlaybackRateContract.NormalOnly;
        if (rates.Count == 0) rates = VideoPlaybackRateContract.NormalOnly;
        double currentRate = backend?.PlaybackRate ?? 1d;
        _updatingPlaybackRateCombo = true;
        try
        {
            _playbackRateCombo.BeginUpdate();
            _playbackRateCombo.Items.Clear();
            foreach (double rate in rates)
            {
                _playbackRateCombo.Items.Add(new PlaybackRateChoice(rate));
            }
            int selected = 0;
            double smallestDelta = double.MaxValue;
            for (int index = 0; index < _playbackRateCombo.Items.Count; index++)
            {
                if (_playbackRateCombo.Items[index] is not PlaybackRateChoice item) continue;
                double delta = Math.Abs(item.Rate - currentRate);
                if (delta < smallestDelta) { selected = index; smallestDelta = delta; }
            }
            if (_playbackRateCombo.Items.Count > 0) _playbackRateCombo.SelectedIndex = selected;
            bool ready = _mediaReady && HasMedia;
            _playbackRateCombo.Enabled = ready && rates.Count > 1;
            string? restriction = backend?.PlaybackRateRestrictionReason;
            _controlToolTip.SetToolTip(_playbackRateCombo,
                !string.IsNullOrWhiteSpace(restriction)
                    ? restriction
                    : ready ? "新しい動画は1.0倍速で始まります。" : "動画の再生中に速度を選択できます。");
        }
        finally
        {
            _playbackRateCombo.EndUpdate();
            _updatingPlaybackRateCombo = false;
        }
    }

    internal bool AdjustVolume(int deltaPercent)
    {
        VideoInputTrace.Record("volume-entry", $"delta={deltaPercent} volume={_volumePercent} backend={_backend?.BackendKind}");
        if (!(_mediaReady || PlaybackLoadedOverrideForTests == true) || !HasMedia)
        {
            return false;
        }

        SetUserVolume(Math.Clamp(_volumePercent + deltaPercent, 0, 100));
        return true;
    }

    private async Task<bool> SeekBySecondsAsync(int delta)
    {
        var backend = _backend;
        if (!CanSeek(backend)) return false;
        long current = _userStopped ? _userStoppedPositionMilliseconds : backend!.Position;
        return await QueueSeekTargetAsync(backend!, _loadGeneration, CalculateSeekTarget(current, (long)delta * 1000L, backend!.Duration));
    }

    private async Task<bool> SeekToTimeAsync(long milliseconds)
    {
        var backend = _backend;
        if (!CanSeek(backend)) return false;
        return await QueueSeekTargetAsync(backend!, _loadGeneration, CalculateSeekTarget(0, Math.Max(0, milliseconds), backend!.Duration));
    }

    private async Task<bool> SeekToBarPositionAsync()
    {
        if (_updatingSeekBar || IsDisposed || !CanSeek(_backend)) return false;
        var backend = _backend!;
        return await QueueSeekTargetAsync(backend, _loadGeneration, backend.Duration * _seekBar.Value / _seekBar.Maximum);
    }

    private async Task<bool> QueueSeekTargetAsync(IVideoPlaybackBackend backend, int generation, long target)
    {
        if (generation != _loadGeneration || !ReferenceEquals(backend, _backend)) return false;
        _queuedSeekTarget = target;
        if (_seekCommandInProgress) return true;
        _seekCommandInProgress = true; UpdatePlaybackControlState();
        bool success = true;
        try
        {
            while (_queuedSeekTarget is { } requested)
            {
                _queuedSeekTarget = null;
                success = await backend.SeekAsync(requested);
                if (generation != _loadGeneration || !ReferenceEquals(backend, _backend)) return false;
                if (!success) { SetStatusMessage(backend.LastError ?? "シークできませんでした。"); break; }
                if (_userStopped) _userStoppedPositionMilliseconds = requested;
            }
            return success;
        }
        finally { if (generation == _loadGeneration) { _seekCommandInProgress = false; _queuedSeekTarget = null; UpdatePlaybackControlState(); RefreshPlaybackPosition(); } }
    }

    internal static long CalculateSeekTarget(long currentMilliseconds, long deltaMilliseconds, long durationMilliseconds)
    {
        long target = Math.Max(0, currentMilliseconds + deltaMilliseconds);
        return durationMilliseconds > 0 ? Math.Min(target, durationMilliseconds) : target;
    }

    private bool CanSeek(IVideoPlaybackBackend? backend)
        => (_mediaReady || PlaybackLoadedOverrideForTests == true) && backend?.IsSeekable == true && backend.Duration > 0;

    private void RefreshPlaybackPosition()
    {
        var backend = _backend;
        if (backend == null || IsDisposed)
        {
            UpdatePlaybackControlState(); RefreshPlaybackDetails(null); PublishPlaybackState(); return;
        }
        long time = _userStopped ? _userStoppedPositionMilliseconds : backend.Position;
        long length = backend.Duration;
        try
        {
            if (!_seekDragging && !_seekCommandInProgress)
            {
                _updatingSeekBar = true;
                _seekBar.Value = length == 0 ? 0 : Math.Clamp((int)(time * _seekBar.Maximum / length), _seekBar.Minimum, _seekBar.Maximum);
            }
            _currentTimeLabel.Text = FormatTime(time); _durationLabel.Text = FormatOptionalTime(length);
            SetPlaybackUiState(backend.IsPlaying);
            if (backend.State == VideoPlaybackState.Ended) _userStopped = false;
            RefreshPlaybackDetails(backend, time, length, backend.IsSeekable);
        }
        finally { _updatingSeekBar = false; UpdatePlaybackControlState(); PublishPlaybackState(); }
    }

    private VideoPlaybackState GetCurrentPlaybackState()
    {
        if (PlaybackStateOverrideForTests is { } overridden)
        {
            return overridden switch
            {
                MidFD.ViewerIpc.ViewerVideoPlaybackState.Opening => VideoPlaybackState.Opening,
                MidFD.ViewerIpc.ViewerVideoPlaybackState.Playing => VideoPlaybackState.Playing,
                MidFD.ViewerIpc.ViewerVideoPlaybackState.Paused => VideoPlaybackState.Paused,
                MidFD.ViewerIpc.ViewerVideoPlaybackState.Stopped => VideoPlaybackState.Stopped,
                MidFD.ViewerIpc.ViewerVideoPlaybackState.Ended => VideoPlaybackState.Ended,
                MidFD.ViewerIpc.ViewerVideoPlaybackState.Error => VideoPlaybackState.Error,
                _ => VideoPlaybackState.Empty
            };
        }
        if (_loadInProgress) return VideoPlaybackState.Opening;
        if (!_mediaReady && PlaybackLoadedOverrideForTests != true)
            return _lastPublishedPlaybackState == VideoPlaybackState.Error
                ? VideoPlaybackState.Error
                : VideoPlaybackState.Empty;
        if (_backend != null) return _backend.State;
        return (PlaybackIsPlayingOverrideForTests ?? _playbackIsPlayingUi)
            ? VideoPlaybackState.Playing
            : VideoPlaybackState.Paused;
    }

    private void PublishPlaybackState(VideoPlaybackState? forcedState = null)
    {
        if (IsDisposed) return;
        VideoPlaybackState state = forcedState ?? GetCurrentPlaybackState();
        IVideoPlaybackBackend? backend = _backend;
        if (_hasPublishedPlaybackState
            && state == _lastPublishedPlaybackState
            && ReferenceEquals(backend, _lastPublishedPlaybackBackend))
        {
            return;
        }
        _hasPublishedPlaybackState = true;
        _lastPublishedPlaybackState = state;
        _lastPublishedPlaybackBackend = backend;
        PlaybackStateChanged?.Invoke(ToViewerPlaybackState(state));
    }

    private static MidFD.ViewerIpc.ViewerVideoPlaybackState ToViewerPlaybackState(VideoPlaybackState state)
        => state switch
        {
            VideoPlaybackState.Opening => MidFD.ViewerIpc.ViewerVideoPlaybackState.Opening,
            VideoPlaybackState.Playing => MidFD.ViewerIpc.ViewerVideoPlaybackState.Playing,
            VideoPlaybackState.Paused => MidFD.ViewerIpc.ViewerVideoPlaybackState.Paused,
            VideoPlaybackState.Stopped => MidFD.ViewerIpc.ViewerVideoPlaybackState.Stopped,
            VideoPlaybackState.Ended => MidFD.ViewerIpc.ViewerVideoPlaybackState.Ended,
            VideoPlaybackState.Error => MidFD.ViewerIpc.ViewerVideoPlaybackState.Error,
            _ => MidFD.ViewerIpc.ViewerVideoPlaybackState.Empty
        };

    private void SetDisplayedTime(long time, long duration)
    {
        _updatingSeekBar = true;
        try
        {
            _seekBar.Value = 0;
            _currentTimeLabel.Text = FormatTime(time);
            _durationLabel.Text = FormatOptionalTime(duration);
        }
        finally
        {
            _updatingSeekBar = false;
        }
    }

    private void UpdatePlaybackControlState()
    {
        if (_controlsPanel.IsDisposed) return;
        IVideoPlaybackBackend? player = _backend;
        bool ready = (_mediaReady || PlaybackLoadedOverrideForTests == true) && HasMedia;
        bool busy = _pauseCommandInProgress || _stopCommandInProgress;
        bool seekable = ready && CanSeek(player);
        _playPauseButton.Enabled = ready && !busy;
        _stopButton.Enabled = ready && !busy;
        _skipBackButton.Enabled = seekable && !busy;
        _skipForwardButton.Enabled = seekable && !busy;
        _seekBar.Enabled = seekable && !_stopCommandInProgress;
        _volumeBar.Enabled = ready;
        _volumeIcon.Enabled = ready;
        _volumeValueLabel.ForeColor = ready ? _chromePalette.PrimaryText : _chromePalette.DisabledText;
        _volumeValueLabel.Invalidate();
        _detailsButton.Enabled = ready;
    }

    private void SetDetailsExpanded(bool expanded)
    {
        bool changed = _detailsPanel.Visible != expanded;
        _detailsExpanded = expanded;
        _detailsPanel.Visible = expanded;
        _rootLayout.RowStyles[4].Height = expanded ? ScaleLogical(DetailsPanelExpandedHeight) : 0;
        _rootLayout.PerformLayout();
        ApplyDetailsButtonAppearance();
        if (changed)
        {
            DetailsExpandedChanged?.Invoke(expanded, expanded ? ScaleLogical(DetailsPanelExpandedHeight) : 0);
        }
        RefreshPlaybackDetails(_backend);
    }

    internal void SetStatusMessage(string? message)
    {
        string text = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
        bool visible = text.Length > 0;
        if (string.Equals(_statusLabel.Text, text, StringComparison.Ordinal)
            && _statusPanel.Visible == visible)
        {
            return;
        }

        _statusLabel.Text = text;
        _controlToolTip.SetToolTip(_statusLabel, text);
        _statusPanel.Visible = visible;
        if (_rootLayout != null && _rootLayout.RowStyles.Count > 5)
        {
            _rootLayout.RowStyles[5].Height = visible ? ScaleLogical(StatusRowHeight) : 0;
            _rootLayout.PerformLayout();
        }
    }

    private void RefreshPlaybackDetails(IVideoPlaybackBackend? player, long? currentTime = null, long? duration = null, bool? seekable = null)
    {
        var metadata = player?.Metadata ?? new VideoPlaybackMetadata();
        _videoDetailsValueLabel.Text = DisplayUnknown(metadata.Video);
        _audioDetailsValueLabel.Text = DisplayUnknown(metadata.Audio);
        _controlToolTip.SetToolTip(_videoDetailsValueLabel, DisplayUnknown(metadata.VideoToolTip));
        _controlToolTip.SetToolTip(_audioDetailsValueLabel, DisplayUnknown(metadata.AudioToolTip));
        string engineName = _selection is { SelectedBackend: null } ? "未選択"
            : player?.BackendKind == VideoPlaybackBackendKind.MediaFoundation ? "Media Foundation（外部字幕非対応）" : player?.BackendKind ?? "未選択";
        _engineDetailsValueLabel.Text = _selection?.RequestedMode == VideoPlaybackBackendKind.Auto ? $"{engineName} / 自動選択" : engineName;
        _selectionDetailsValueLabel.Text = _selection?.Reason ?? "—";
        _controlToolTip.SetToolTip(_engineDetailsValueLabel, _selection?.ToolTip ?? metadata.Notice ?? engineName);
        _controlToolTip.SetToolTip(_selectionDetailsValueLabel, _selection?.ToolTip ?? "—");
        if (player == null) { _playbackDetailsValueLabel.Text = "—"; return; }
        long current = _userStopped ? _userStoppedPositionMilliseconds : currentTime ?? player.Position;
        long total = duration ?? player.Duration;
        bool canSeek = seekable ?? player.IsSeekable;
        string state = _userStopped ? "停止" : FormatPlaybackState(player.State);
        _playbackDetailsValueLabel.Text = $"{state} · {player.PlaybackRate:0.##}x · {FormatTime(current)} / {FormatOptionalTime(total)}";
        _controlToolTip.SetToolTip(_playbackDetailsValueLabel, (canSeek ? "シーク可能" : "シーク不可または未確認") + (metadata.Notice == null ? "" : Environment.NewLine + metadata.Notice));
    }

    private static string DisplayUnknown(string? value)
        => string.IsNullOrWhiteSpace(value) || value == "-" ? "不明" : value;
    private static string FormatOptionalTime(long milliseconds)
        => milliseconds > 0 ? FormatTime(milliseconds) : "—";

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private static string FormatTime(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private enum MediaChromeIcon
    {
        None,
        Play,
        Pause,
        Stop,
        Info,
        Volume,
        SeekBackward,
        SeekForward,
        Lock,
        Unlock
    }

    private sealed class MediaPlaybackRateComboBox : ComboBox
    {
        private Color _border = SystemColors.ControlDark;
        private bool _highContrast;

        public MediaPlaybackRateComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
        }

        public void ApplyPalette(Color background, Color foreground, Color border, bool highContrast)
        {
            _highContrast = highContrast;
            FlatStyle = highContrast ? FlatStyle.Standard : FlatStyle.Flat;
            BackColor = highContrast ? SystemColors.Window : background;
            ForeColor = highContrast ? SystemColors.WindowText : foreground;
            _border = highContrast ? SystemColors.WindowFrame : border;
            Invalidate();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool disabled = !Enabled || (e.State & DrawItemState.Disabled) != 0;
            Color background = selected ? SystemColors.Highlight : BackColor;
            Color foreground = disabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : ForeColor;
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, e.Bounds);
            string text = (e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text) ?? string.Empty;
            TextRenderer.DrawText(e.Graphics, text, Font, e.Bounds, foreground,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // Keep the native combo/dropdown/keyboard behavior; theme only the closed arrow and border.
            if (!_highContrast && m.Msg is 0x000F or 0x0318 && IsHandleCreated)
            {
                using Graphics graphics = m.Msg == 0x0318 && m.WParam != IntPtr.Zero
                    ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle);
                int width = Math.Max(16, (int)Math.Round(18 * DeviceDpi / 96d));
                Rectangle arrow = new(Width - width - 1, 1, width, Height - 2);
                using var brush = new SolidBrush(BackColor);
                graphics.FillRectangle(brush, arrow);
                using var pen = new Pen(Enabled ? ForeColor : SystemColors.GrayText, Math.Max(1, DeviceDpi / 96f));
                int x = arrow.Left + arrow.Width / 2, y = arrow.Top + arrow.Height / 2;
                graphics.DrawLines(pen, [new Point(x - 3, y - 1), new Point(x, y + 2), new Point(x + 3, y - 1)]);
                using var border = new Pen(_border);
                graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    private sealed class MediaChromeButton : Button
    {
        private bool _hovered;
        private bool _pressed;

        public MediaChromeButton()
        {
            SetStyle(ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            TextAlign = ContentAlignment.MiddleCenter;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public MediaChromeIcon Icon { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool HighContrast { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color NormalSurface { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color HoverSurface { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color PressedSurface { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color DisabledSurface { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color DisabledTextColor { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color BorderColor { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color FocusBorder { get; set; }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Focus();
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            Color fill = !Enabled
                ? DisabledSurface
                : _pressed ? PressedSurface
                : _hovered ? HoverSurface
                : NormalSurface;
            using (GraphicsPath path = CreateRoundedRectangle(bounds, Scale(5)))
            using (var brush = new SolidBrush(fill))
            using (var border = new Pen(HighContrast ? BorderColor : Color.Transparent, Scale(1)))
            {
                graphics.FillPath(brush, path);
                if (HighContrast) graphics.DrawPath(border, path);
            }

            Color foreground = Enabled ? ForeColor : DisabledTextColor;
            if (Icon == MediaChromeIcon.None)
            {
                TextRenderer.DrawText(
                    graphics,
                    Text,
                    Font,
                    bounds,
                    foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            else
            {
                DrawIcon(graphics, bounds, Icon, foreground, Scale);
            }

            if (Focused && ShowFocusCues)
            {
                Rectangle focusBounds = Rectangle.Inflate(bounds, -Scale(4), -Scale(4));
                ControlPaint.DrawFocusRectangle(graphics, focusBounds, foreground, fill);
                if (!HighContrast)
                {
                    using var focusPen = new Pen(FocusBorder, Scale(1));
                    using GraphicsPath focusPath = CreateRoundedRectangle(bounds, Scale(5));
                    graphics.DrawPath(focusPen, focusPath);
                }
            }
        }

        private int Scale(int logical) => Math.Max(1, (int)Math.Round(logical * Math.Max(1, DeviceDpi) / 96d));
    }

    private sealed class MediaIconView : Control
    {
        private Color _iconColor = SystemColors.ControlText;
        private Color _disabledColor = SystemColors.GrayText;

        public MediaIconView()
        {
            SetStyle(ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = "音量";
            TabStop = false;
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public MediaChromeIcon Icon { get; set; }

        public void ApplyPalette(Color background, Color icon, Color disabled)
        {
            BackColor = background;
            _iconColor = icon;
            _disabledColor = disabled;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawIcon(e.Graphics, ClientRectangle, Icon, Enabled ? _iconColor : _disabledColor, logical =>
                Math.Max(1, (int)Math.Round(logical * Math.Max(1, DeviceDpi) / 96d)));
        }
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawIcon(Graphics graphics, Rectangle bounds, MediaChromeIcon icon, Color color, Func<int, int> scale)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float unit = scale(1);
        float side = Math.Min(scale(18), Math.Max(scale(10), Math.Min(bounds.Width, bounds.Height) - scale(12)));
        float left = bounds.Left + (bounds.Width - side) / 2f;
        float top = bounds.Top + (bounds.Height - side) / 2f;
        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, Math.Max(unit * 1.6f, scale(1)))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        switch (icon)
        {
            case MediaChromeIcon.Play:
                using (var triangle = new GraphicsPath())
                {
                    triangle.AddPolygon([
                        new PointF(left + side * 0.28f, top + side * 0.12f),
                        new PointF(left + side * 0.82f, top + side * 0.5f),
                        new PointF(left + side * 0.28f, top + side * 0.88f)]);
                    graphics.FillPath(brush, triangle);
                }
                break;
            case MediaChromeIcon.Pause:
                graphics.FillRectangle(brush, left + side * 0.22f, top + side * 0.12f, side * 0.2f, side * 0.76f);
                graphics.FillRectangle(brush, left + side * 0.58f, top + side * 0.12f, side * 0.2f, side * 0.76f);
                break;
            case MediaChromeIcon.Stop:
                graphics.FillRectangle(brush, left + side * 0.24f, top + side * 0.24f, side * 0.52f, side * 0.52f);
                break;
            case MediaChromeIcon.SeekBackward:
            case MediaChromeIcon.SeekForward:
                for (int index = 0; index < 2; index++)
                {
                    float x = left + side * (0.1f + index * 0.43f);
                    bool back = icon == MediaChromeIcon.SeekBackward;
                    graphics.FillPolygon(brush, [
                        new PointF(x + side * (back ? 0.35f : 0), top + side * 0.18f),
                        new PointF(x + side * (back ? 0 : 0.35f), top + side * 0.5f),
                        new PointF(x + side * (back ? 0.35f : 0), top + side * 0.82f)]);
                }
                break;
            case MediaChromeIcon.Lock:
            case MediaChromeIcon.Unlock:
                graphics.DrawRectangle(pen, left + side * 0.18f, top + side * 0.45f, side * 0.64f, side * 0.45f);
                float shackleLeft = left + side * (icon == MediaChromeIcon.Lock ? 0.32f : 0.58f);
                graphics.DrawArc(pen, shackleLeft, top + side * 0.07f, side * 0.36f, side * 0.49f, 180, 180);
                graphics.DrawLine(pen, shackleLeft, top + side * 0.31f, shackleLeft, top + side * 0.45f);
                if (icon == MediaChromeIcon.Lock)
                    graphics.DrawLine(pen, shackleLeft + side * 0.36f, top + side * 0.31f, shackleLeft + side * 0.36f, top + side * 0.45f);
                graphics.DrawLine(pen, left + side * 0.5f, top + side * 0.62f, left + side * 0.5f, top + side * 0.73f);
                break;
            case MediaChromeIcon.Info:
                graphics.DrawEllipse(pen, left + side * 0.08f, top + side * 0.08f, side * 0.84f, side * 0.84f);
                graphics.FillEllipse(brush, left + side * 0.46f, top + side * 0.25f, side * 0.1f, side * 0.1f);
                graphics.DrawLine(pen, left + side * 0.51f, top + side * 0.46f, left + side * 0.51f, top + side * 0.74f);
                break;
            case MediaChromeIcon.Volume:
                graphics.FillPolygon(brush, [
                    new PointF(left + side * 0.08f, top + side * 0.38f),
                    new PointF(left + side * 0.32f, top + side * 0.38f),
                    new PointF(left + side * 0.62f, top + side * 0.14f),
                    new PointF(left + side * 0.62f, top + side * 0.86f),
                    new PointF(left + side * 0.32f, top + side * 0.62f),
                    new PointF(left + side * 0.08f, top + side * 0.62f)]);
                graphics.DrawArc(pen, left + side * 0.42f, top + side * 0.27f, side * 0.5f, side * 0.46f, -50, 100);
                graphics.DrawArc(pen, left + side * 0.34f, top + side * 0.1f, side * 0.75f, side * 0.8f, -48, 96);
                break;
        }
    }

    private sealed class MediaSeekBar : Control
    {
        private int _minimum;
        private int _maximum = 1000;
        private int _value;
        private bool _hovered;
        private bool _dragging;
        private Color _trackColor = Color.FromArgb(62, 66, 72);
        private Color _playedColor = SystemColors.Highlight;
        private Color _thumbColor = Color.FromArgb(238, 238, 238);
        private Color _disabledColor = SystemColors.GrayText;

        public MediaSeekBar()
        {
            SetStyle(ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.Slider;
            AccessibleName = "再生位置";
            AccessibleDescription = "動画の再生位置です。クリックまたはドラッグで調整します。";
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => _minimum;
            set
            {
                _minimum = value;
                if (_maximum < _minimum) _maximum = _minimum;
                Value = _value;
            }
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(_minimum, value);
                Value = _value;
            }
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, _minimum, _maximum);
                if (_value == clamped) return;
                _value = clamped;
                Invalidate();
            }
        }

        public event EventHandler? Scroll;

        public void ApplyPalette(Color background, Color track, Color played, Color thumb, Color disabled)
        {
            BackColor = background;
            _trackColor = track;
            _playedColor = played;
            _thumbColor = thumb;
            _disabledColor = disabled;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key is Keys.Home or Keys.End
                || base.IsInputKey(keyData);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            if (!_dragging) Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus();
            _dragging = true;
            Capture = true;
            SetValueFromPoint(e.X, raiseScroll: true);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging && Enabled && (e.Button & MouseButtons.Left) != 0)
            {
                SetValueFromPoint(e.X, raiseScroll: true);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragging && e.Button == MouseButtons.Left)
            {
                SetValueFromPoint(e.X, raiseScroll: true);
                _dragging = false;
                Capture = false;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled)
            {
                int? target = e.KeyCode switch
                {
                    Keys.Home => _minimum,
                    Keys.End => _maximum,
                    _ => null
                };
                if (target.HasValue)
                {
                    int before = _value;
                    Value = target.Value;
                    if (_value != before) Scroll?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle track = GetTrackBounds();
            if (track.Width <= 0) return;

            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int centerY = track.Top + track.Height / 2;
            int radius = Scale(_hovered || _dragging ? 6 : 5);
            int thumbX = GetThumbX(track);
            Color trackColor = Enabled ? _trackColor : _disabledColor;
            Color playedColor = Enabled ? _playedColor : _disabledColor;
            Color thumbColor = Enabled ? _thumbColor : _disabledColor;

            using (var trackPen = new Pen(trackColor, Math.Max(Scale(3), track.Height)))
            {
                trackPen.StartCap = LineCap.Round;
                trackPen.EndCap = LineCap.Round;
                graphics.DrawLine(trackPen, track.Left, centerY, track.Right, centerY);
            }
            if (thumbX > track.Left)
            {
                using var playedPen = new Pen(playedColor, Math.Max(Scale(3), track.Height))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(playedPen, track.Left, centerY, thumbX, centerY);
            }

            var thumbBounds = new Rectangle(thumbX - radius, centerY - radius, radius * 2, radius * 2);
            using (var thumbBrush = new SolidBrush(thumbColor))
            using (var thumbOutline = new Pen(Enabled ? _playedColor : _disabledColor, Scale(1) * 1.5f))
            {
                graphics.FillEllipse(thumbBrush, thumbBounds);
                graphics.DrawEllipse(thumbOutline, thumbBounds);
            }

            if (Focused && ShowFocusCues)
            {
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2), ForeColor, BackColor);
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        private Rectangle GetTrackBounds()
        {
            int inset = Math.Max(Scale(7), Padding.Left + Scale(5));
            int left = Math.Min(inset, Math.Max(0, ClientSize.Width / 2 - 1));
            int right = Math.Max(left + 1, ClientSize.Width - inset - 1);
            int height = Math.Max(Scale(3), Math.Min(Scale(4), ClientSize.Height - Scale(4)));
            return new Rectangle(left, Math.Max(0, (ClientSize.Height - height) / 2), right - left, height);
        }

        private int Scale(int logical) => Math.Max(1, (int)Math.Round(logical * Math.Max(1, DeviceDpi) / 96d));

        private int GetThumbX(Rectangle track)
        {
            double ratio = _maximum <= _minimum ? 0 : (_value - _minimum) / (double)(_maximum - _minimum);
            return track.Left + (int)Math.Round((track.Width - 1) * Math.Clamp(ratio, 0, 1));
        }

        private void SetValueFromPoint(int x, bool raiseScroll)
        {
            Rectangle track = GetTrackBounds();
            int before = _value;
            double ratio = track.Width <= 1 ? 0 : (x - track.Left) / (double)(track.Width - 1);
            Value = _minimum + (int)Math.Round(Math.Clamp(ratio, 0, 1) * (_maximum - _minimum));
            if (raiseScroll && _value != before)
            {
                Scroll?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
