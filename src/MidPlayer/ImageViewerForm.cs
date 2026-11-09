using System.Drawing;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Services;
using MidFD.ViewerIpc;

namespace MidFD;

public partial class ImageViewerForm : Form
{
    private const int WmKeyDown = 0x0100;
    private const long KeyWasPreviouslyDownMask = 1L << 30;

    internal enum SelectionResizeHandle
    {
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private const float MinZoom = 0.1f;
    private const float MaxZoom = 4.0f;
    private const float ZoomStep = 1.1f;
    private const int MaxHistoryCount = 10;

    private sealed class ImageHistoryEntry
    {
        public required Bitmap Image { get; init; }
        public required string Label { get; init; }
    }

    private string? _currentPath;
    private Bitmap? _originalImage;
    private Bitmap? _displayImage;
    private float _zoom = 1.0f;
    private bool _isFullscreen;
    private FormBorderStyle _savedBorderStyle;
    private FormWindowState _savedWindowState;
    private Rectangle _savedBounds;
    private bool _savedTopMost;
    private readonly ImageViewerSettings _previewSettings;
    private readonly bool _allowQuantization;
    private readonly bool _allowSvgClipboard;
    private readonly Func<string, int?, bool>? _hostCommandDispatcher;
    private readonly SusieDecoderService _susieDecoderService;
    private readonly EmbeddedVideoPlayerControl _embeddedVideoPlayer;
    private Font? _shellFont;
    private (Color Background, Color SecondaryBackground, Color PrimaryText, Color SecondaryText, Color Separator, Color Accent, Color DisabledText, bool HighContrast) _shellPalette;
    private Bitmap? _tonePreviewImage;
    private int _tonePreviewSequence;
    private readonly ToolStripMenuItem _menuQuantize;
    private readonly ToolStripMenuItem _menuResetImage;
    private readonly ToolStripMenuItem _menuCopySvg;
    private readonly ToolStripMenuItem _menuRotateRight;
    private readonly ToolStripMenuItem _menuRotateLeft;
    private readonly ToolStripMenuItem _menuFlipHorizontal;
    private readonly ToolStripMenuItem _menuFlipVertical;
    private readonly ToolStripMenuItem _menuImageInfo;
    private readonly ToolStripMenuItem _menuInlineVideoPlay = new("動画を内蔵再生/一時停止(&P)");
    private readonly ToolStripMenuItem _menuSaveVideoFrame = new("現在のフレームを保存...");
    private readonly Stack<ImageHistoryEntry> _undoStack = new();
    private readonly Stack<ImageHistoryEntry> _redoStack = new();
    private long _loadRequestId;
    private int _presentationGeneration;
    private int _selectionGeneration;
    private ViewerPresentationMode _presentationMode = ViewerPresentationMode.PreviewFollow;
    private int? _userSelectedVideoVolumePercent;
    private bool _activeLoadIsSelectionFollow;
    private string? _activeLoadPath;
    private long _activeLoadResponseRequestId;
    private long _videoPlaybackRequestId;
    private long _lastReportedVideoPlaybackRequestId;
    private ViewerVideoPlaybackState? _lastReportedVideoPlaybackState;
    private int _imageGeneration;
    private long _pendingFirstPaintRequestId;
    private long _pendingFirstPaintStartedAt;
    private readonly Label _loadingLabel;
    private CancellationTokenSource? _imageDecodeCts;
    private CancellationTokenSource? _mediaLoadCts;

    private bool _videoInitialWindowSizeApplied;
    private bool _videoManuallyResizedBeforeDimensions;
    private int _detailsExpansionHeightApplied;
    private int? _detailsExpandedWindowHeight;
    private Point? _detailsExpandedWindowLocation;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string, CancellationToken, Task<ImageDecodeResult>>? DecodeImageOverrideForTests { get; set; }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<OpenFileDialog, DialogResult>? ShowOpenMediaDialogOverride { get; set; }
    private bool _isSelectingRectangle;
    private bool _isResizingSelection;
    private SelectionResizeHandle _selectionResizeHandle;
    private Point _selectionResizeStartPoint;
    private Rectangle _selectionResizeStartRectangle;
    private Point _selectionStartClientPoint;
    private Point _selectionCurrentClientPoint;
    private Rectangle? _selectionClientRectangle;
    private int _displayRotationQuarterTurns;
    private bool _displayFlipHorizontal;
    private bool _displayFlipVertical;
    private ImageSourceKind _imageSourceKind = ImageSourceKind.Unknown;
    private bool _hostedLifetime;
    private bool _shutdownRequested;
    private Task _reuseCleanupTask = Task.CompletedTask;

    private enum ImageSourceKind
    {
        Unknown,
        File,
        VideoPlayback,
        Clipboard
    }

    private sealed class ImageViewerInfo
    {
        public string SourceKind { get; init; } = "-";
        public string FileName { get; init; } = "-";
        public string FullPath { get; init; } = "-";
        public string FileSizeText { get; init; } = "-";
        public string LastWriteTimeText { get; init; } = "-";
        public string ImageFormatText { get; init; } = "-";
        public string OriginalSizeText { get; init; } = "-";
        public string DisplaySizeText { get; init; } = "-";
        public string PixelFormatText { get; init; } = "-";
        public string DpiText { get; init; } = "-";
        public string VideoContainerText { get; init; } = "-";
        public string VideoContainerDetailText { get; init; } = "-";
        public string VideoContainerIdentifierText { get; init; } = "-";
        public string VideoCodecText { get; init; } = "-";
        public string AudioCodecText { get; init; } = "-";
        public string VideoDurationText { get; init; } = "-";
        public string VideoResolutionText { get; init; } = "-";
        public string VideoFrameRateText { get; init; } = "-";
        public string VideoBitRateText { get; init; } = "-";
    }

    public string? CurrentPath => _currentPath;
    public bool HasLoadedImage => _originalImage != null || _embeddedVideoPlayer.HasMedia;
    public event Action<Keys>? BrowserNavigationRequested;
    public event Action? MarkToggleRequested;
    public event Action<string, long, bool, bool>? MediaLoadCompleted;
    internal event Action<double>? PlaybackRateCommandRequested;
    internal event Action<ViewerPresentationMode, int, bool>? PresentationModeApplied;
    internal event Action<long, ViewerVideoPlaybackState>? VideoPlaybackStateChanged;
    internal event Action? HiddenForReuse;
    internal Task ReuseCleanupTask => _reuseCleanupTask;

    internal void ConfigureHostedLifetime()
    {
        _hostedLifetime = true;
        ConfigureMediaDropTargets(enabled: false);
        UpdatePresentationModeUi();
    }

    internal void SetReuseOnUserClose(bool reuse)
    {
        _previewSettings.ReuseOnUserClose = reuse;
        if (_hostedLifetime && !reuse && !Visible) Shutdown();
    }

    internal bool TryApplySusiePluginSettings(string? settingsJson, out string errorMessage)
    {
        if (!TryParseSusiePluginSettings(settingsJson, out SusiePluginSettings? settings, out errorMessage))
        {
            LogService.Warn($"[MidPlayer Settings] Susie settings update rejected: {errorMessage}");
            return false;
        }

        try
        {
            _susieDecoderService.UpdateSettings(settings!);
            _previewSettings.SusiePlugins = settings!.Clone();
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            errorMessage = $"Susie settings could not be applied: {error.GetType().Name}: {error.Message}";
            LogService.Error("[MidPlayer Settings] Susie settings update failed.", error);
            return false;
        }
    }

    private static bool TryParseSusiePluginSettings(
        string? json,
        out SusiePluginSettings? settings,
        out string errorMessage)
    {
        settings = null;
        errorMessage = "Susie settings payload is empty.";
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(root, "Plugins", out System.Text.Json.JsonElement plugins)
                || plugins.ValueKind != System.Text.Json.JsonValueKind.Array
                || !TryGetPropertyIgnoreCase(root, "DecoderByExtension", out System.Text.Json.JsonElement decoders)
                || decoders.ValueKind != System.Text.Json.JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(root, "DisabledStandardExtensions", out System.Text.Json.JsonElement disabled)
                || disabled.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                errorMessage = "Susie settings payload has an invalid structure.";
                return false;
            }

            foreach (System.Text.Json.JsonElement plugin in plugins.EnumerateArray())
            {
                if (plugin.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !TryGetPropertyIgnoreCase(plugin, "PluginPath", out System.Text.Json.JsonElement pluginPath)
                    || pluginPath.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(pluginPath.GetString()))
                {
                    errorMessage = "Susie settings payload contains an invalid Plugin registration.";
                    return false;
                }
            }

            foreach (System.Text.Json.JsonProperty decoder in decoders.EnumerateObject())
            {
                if (decoder.Value.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(decoder.Value.GetString()))
                {
                    errorMessage = "Susie settings payload contains an invalid decoder selection.";
                    return false;
                }
            }

            foreach (System.Text.Json.JsonElement extension in disabled.EnumerateArray())
            {
                if (extension.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(extension.GetString()))
                {
                    errorMessage = "Susie settings payload contains an invalid standard decoder setting.";
                    return false;
                }
            }

            settings = System.Text.Json.JsonSerializer.Deserialize<SusiePluginSettings>(
                json,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (settings == null)
            {
                errorMessage = "Susie settings payload did not contain settings.";
                return false;
            }
            settings.Normalize();
            errorMessage = string.Empty;
            return true;
        }
        catch (System.Text.Json.JsonException error)
        {
            errorMessage = $"Susie settings payload is invalid JSON: {error.Message}";
            return false;
        }
        catch (Exception error)
        {
            errorMessage = $"Susie settings payload could not be applied: {error.GetType().Name}: {error.Message}";
            return false;
        }
    }

    private static bool TryGetPropertyIgnoreCase(
        System.Text.Json.JsonElement element,
        string name,
        out System.Text.Json.JsonElement value)
    {
        foreach (System.Text.Json.JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal void Shutdown()
    {
        _shutdownRequested = true;
        Close();
    }

    private void ImageViewerForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_hostedLifetime && _previewSettings.ReuseOnUserClose
            && !_shutdownRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideForReuse();
        }
    }

    internal void HideForReuse()
    {
        if (!_hostedLifetime || !_previewSettings.ReuseOnUserClose)
        {
            Shutdown();
            return;
        }

        // Invalidate before hiding: late decode/edit results must not revive this media.
        ++_loadRequestId;
        ++_imageGeneration;
        _pendingFirstPaintRequestId = 0;
        _imageDecodeCts?.Cancel();
        _activeLoadIsSelectionFollow = false;
        ResetTonePreview();
        Hide();
        if (_isFullscreen) ExitFullscreen();
        ShowInTaskbar = false;
        Opacity = 0;
        _reuseCleanupTask = _embeddedVideoPlayer.StopForReuseAsync();
        _embeddedVideoPlayer.Visible = false;
        _loadingLabel.Visible = false;
        pictureBox1.Image = null;
        DisposeImage(_displayImage);
        DisposeImage(_originalImage);
        _displayImage = null;
        _originalImage = null;
        _currentPath = null;
        _imageSourceKind = ImageSourceKind.Unknown;
        ResetVideoPlaybackRequest();
        UpdatePresentationModeUi();
        _videoInitialWindowSizeApplied = false;
        _videoManuallyResizedBeforeDimensions = false;
        ClearSelection();
        ClearHistory();
        ResetDisplayTransformState();
        ClearDecoderStatus();
        SetQuantizeMenuEnabled(false);
        statusLabel.Text = string.Empty;
        Text = "MidFD Image Viewer";
        HiddenForReuse?.Invoke();
    }

    public ImageViewerForm(
        ImageViewerSettings? previewSettings = null,
        bool allowQuantization = true,
        bool allowSvgClipboard = true,
        Func<string, int?, bool>? hostCommandDispatcher = null,
        Guid? instanceId = null)
    {
        _previewSettings = previewSettings?.Clone() ?? new ImageViewerSettings();
        _allowQuantization = allowQuantization;
        _allowSvgClipboard = allowSvgClipboard;
        _hostCommandDispatcher = hostCommandDispatcher;
        InstanceId = instanceId ?? Guid.NewGuid();
        _susieDecoderService = new SusieDecoderService(_previewSettings.SusiePlugins);

        InitializeComponent();
        _embeddedVideoPlayer = new EmbeddedVideoPlayerControl
        {
            Dock = DockStyle.Fill,
            Visible = false,
            VideoSeekStepSeconds = _previewSettings.VideoSeekStepSeconds
        };
        imageScrollPanel.Controls.Add(_embeddedVideoPlayer);
        _embeddedVideoPlayer.BringToFront();
        _embeddedVideoPlayer.PlaybackFailed += message => LogService.Error($"[MidPlayer Video] {message}");
        _embeddedVideoPlayer.PresentationLockToggleRequested += () => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerTogglePresentationMode);
        _embeddedVideoPlayer.UserVolumeChanged += volume => _userSelectedVideoVolumePercent = Math.Clamp(volume, 0, 100);
        _embeddedVideoPlayer.PlaybackRateRequested += rate =>
        {
            if (_hostCommandDispatcher != null) PlaybackRateCommandRequested?.Invoke(rate);
            else _embeddedVideoPlayer.SetPlaybackRate(rate);
        };
        _embeddedVideoPlayer.VideoDisplaySizeAvailable += HandleInitialVideoWindowSize;
        _embeddedVideoPlayer.DetailsExpandedChanged += HandlePlaybackDetailsExpandedChanged;
        _embeddedVideoPlayer.VideoMediaDragEnter += ImageViewerForm_MediaDragEnter;
        _embeddedVideoPlayer.VideoMediaDragDrop += ImageViewerForm_MediaDragDrop;
        _embeddedVideoPlayer.NativeVideoSurfaceSpaceRequested += HandleNativeVideoSurfaceSpace;
        _embeddedVideoPlayer.NativeVideoSurfaceArrowKeyRequested += HandleNativeVideoSurfaceArrowKey;
        _embeddedVideoPlayer.PlaybackStateChanged += HandleEmbeddedVideoPlaybackStateChanged;
        _imagePresentationLockButton.Click += (_, _) =>
        {
            if (CanToggleImagePresentationMode())
            {
                DispatchViewerCommand(ImageViewerCommandIds.ImageViewerTogglePresentationMode);
            }
        };
        ConfigureMediaDropTargets(enabled: _hostCommandDispatcher == null);
        KeyPreview = true;
        KeyDown += ImageViewerForm_KeyDown;
        MouseWheel += ImageViewerForm_MouseWheel;
        imageScrollPanel.MouseWheel += ImageViewerForm_MouseWheel;
        pictureBox1.MouseWheel += ImageViewerForm_MouseWheel;
        pictureBox1.MouseDown += pictureBox1_MouseDown;
        pictureBox1.MouseMove += pictureBox1_MouseMove;
        pictureBox1.MouseUp += pictureBox1_MouseUp;
        pictureBox1.Paint += pictureBox1_Paint;
        FormClosed += ImageViewerForm_FormClosed;
        FormClosing += ImageViewerForm_FormClosing;
        ResizeEnd += ImageViewerForm_ResizeEnd;
        Disposed += (_, _) => _shellFont?.Dispose();

        _menuQuantize = new ToolStripMenuItem("減色(&Q)");
        _menuQuantize.Click += menuQuantize_Click;
        menuStrip1.Items.Add(_menuQuantize);

        _menuResetImage = new ToolStripMenuItem("元画像へ戻す(&R)");
        _menuResetImage.Click += menuResetImage_Click;
        menuStrip1.Items.Add(_menuResetImage);

        _menuCopySvg = new ToolStripMenuItem("SVGをコピー(&C)");
        _menuCopySvg.Click += menuCopySvg_Click;
        menuStrip1.Items.Add(_menuCopySvg);

        _menuRotateRight = new ToolStripMenuItem("右90度回転(&R)");
        _menuRotateRight.ShortcutKeyDisplayString = "R";
        _menuRotateRight.Click += menuRotateRight_Click;

        _menuRotateLeft = new ToolStripMenuItem("左90度回転(&L)");
        _menuRotateLeft.ShortcutKeyDisplayString = "L";
        _menuRotateLeft.Click += menuRotateLeft_Click;

        _menuFlipHorizontal = new ToolStripMenuItem("左右反転(&H)");
        _menuFlipHorizontal.ShortcutKeyDisplayString = "H";
        _menuFlipHorizontal.Click += menuFlipHorizontal_Click;

        _menuFlipVertical = new ToolStripMenuItem("上下反転(&V)");
        _menuFlipVertical.ShortcutKeyDisplayString = "V";
        _menuFlipVertical.Click += menuFlipVertical_Click;

        _menuImageInfo = new ToolStripMenuItem("画像情報(&I)...");
        _menuImageInfo.Click += menuImageInfo_Click;
        _menuInlineVideoPlay.ShortcutKeys = Keys.Control | Keys.Enter;
        _menuInlineVideoPlay.Click += (_, _) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerVideoExternalPlay);
        _menuSaveVideoFrame.Click += (_, _) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerVideoSaveFrame);
        var menuPlayback = new ToolStripMenuItem("動画(&V)");
        menuPlayback.DropDownItems.Add(_menuSaveVideoFrame);
        menuStrip1.Items.Add(menuPlayback);

        var menuImageProcessing = new ToolStripMenuItem("画像処理(&P)");
        menuImageProcessing.DropDownItems.Add(new ToolStripMenuItem("リサイズ(&Z)...", null, (_, _) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerResize)));
        menuImageProcessing.DropDownItems.Add(new ToolStripMenuItem("選択範囲をモザイク(&M)...", null, (_, _) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerMosaic)));
        menuImageProcessing.DropDownItems.Add(new ToolStripMenuItem("選択範囲をぼかす(&B)...", null, (_, _) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerBlur)));
        menuImageProcessing.DropDownItems.Add(new ToolStripMenuItem("色調補正(&T)...", null, (_, _) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerTone)));

        _menuResetImage.ShortcutKeys = Keys.Control | Keys.R;

        menuEdit.DropDownItems.Add(new ToolStripSeparator());
        menuEdit.DropDownItems.Add(new ToolStripMenuItem(
            "選択範囲へ貼り付け（縦横比を維持しない）",
            null,
            (_, _) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerPasteSelectionStretch)));
        menuEdit.DropDownItems.Add(new ToolStripMenuItem(
            "選択範囲へ貼り付け（元サイズを保持）",
            null,
            (_, _) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerPasteSelectionOriginal))
        {
            ShortcutKeyDisplayString = "Ctrl+Shift+V"
        });
        menuEdit.DropDownItems.Add(new ToolStripSeparator());
        menuEdit.DropDownItems.Add(_menuRotateRight);
        menuEdit.DropDownItems.Add(_menuRotateLeft);
        menuEdit.DropDownItems.Add(_menuFlipHorizontal);
        menuEdit.DropDownItems.Add(_menuFlipVertical);
        menuEdit.DropDownItems.Add(new ToolStripSeparator());
        menuEdit.DropDownItems.Add(_menuImageInfo);
        menuEdit.DropDownItems.Add(_menuInlineVideoPlay);
        menuEdit.DropDownItems.Add(new ToolStripSeparator());
        menuEdit.DropDownItems.Add(menuImageProcessing);

        _loadingLabel = new Label
        {
            Text = "Rendering SVG...",
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(160, 0, 0, 0),
            Visible = false,
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(10)
        };
        imageScrollPanel.Controls.Add(_loadingLabel);
        _loadingLabel.BringToFront();
        imageScrollPanel.Resize += (s, e) => CenterLoadingLabel();

        UpdateQuantizeMenuState();
        UpdatePresentationModeUi();
        UpdateZoomStatus();
        ApplyModernShellTheme();
    }

    internal Guid InstanceId { get; }
    internal ViewerPresentationMode PresentationMode => _presentationMode;
    internal int PresentationGeneration => _presentationGeneration;

    internal int ResolveVideoPlaybackVolume(int requestedVolumePercent)
        => _userSelectedVideoVolumePercent ?? Math.Clamp(requestedVolumePercent, 0, 100);

    internal bool ApplyPresentationMode(ViewerPresentationMode mode, int generation)
    {
        if (!Enum.IsDefined(mode) || generation < _presentationGeneration) return false;
        _presentationMode = mode;
        _presentationGeneration = generation;
        if (mode == ViewerPresentationMode.PlaybackLocked && _activeLoadIsSelectionFollow)
        {
            CancelActiveSelectionFollowLoad();
        }
        UpdatePresentationModeUi();
        return true;
    }

    internal bool ApplyPresentationModeFromHost(ViewerPresentationMode mode, int generation)
    {
        bool accepted = ApplyPresentationMode(mode, generation);
        PresentationModeApplied?.Invoke(mode, generation, accepted);
        return accepted;
    }

    internal bool TryAcceptSelectionFollow(int? generation, int? presentationGeneration = null)
    {
        if (_presentationMode != ViewerPresentationMode.PreviewFollow) return false;
        if (presentationGeneration.HasValue && presentationGeneration.Value != _presentationGeneration) return false;
        if (generation is { } incoming)
        {
            if (incoming <= _selectionGeneration) return false;
            _selectionGeneration = incoming;
        }
        return true;
    }

    private bool IsSelectionFollowCurrent(int? selectionGeneration, int? presentationGeneration)
        => _presentationMode == ViewerPresentationMode.PreviewFollow
            && (!presentationGeneration.HasValue || presentationGeneration.Value == _presentationGeneration)
            && (!selectionGeneration.HasValue || selectionGeneration.Value == _selectionGeneration);

    internal void RejectMediaLoad(string path, long? requestId)
    {
        if (requestId.HasValue) MediaLoadCompleted?.Invoke(path, requestId.Value, false, true);
    }

    private void UpdatePresentationModeUi()
    {
        bool automaticVideoState = _imageSourceKind == ImageSourceKind.VideoPlayback;
        ViewerVideoPlaybackState playbackState = _embeddedVideoPlayer.CurrentPlaybackState;
        bool locked = _presentationMode == ViewerPresentationMode.PlaybackLocked;
        _embeddedVideoPlayer.SetPresentationLockState(_hostedLifetime, locked, automaticVideoState);
        UpdateVideoCommandMenuState();

        bool showImageLockButton = CanToggleImagePresentationMode();
        _imagePresentationLockButton.Visible = showImageLockButton;
        _imagePresentationLockButton.Enabled = showImageLockButton;
        if (showImageLockButton)
        {
            _imagePresentationLockButton.Checked = locked;
            _imagePresentationLockButton.Text = locked ? "固定: ON" : "固定: OFF（追従）";
            string accessibleStateAndAction = locked
                ? "固定: ON。クリックまたはCtrl+EnterでBrowserの選択に追従します。"
                : "固定: OFF（追従）。クリックまたはCtrl+Enterで現在の画像を固定します。";
            _imagePresentationLockButton.AccessibleName = accessibleStateAndAction;
            _imagePresentationLockButton.AccessibleDescription = accessibleStateAndAction;
            _imagePresentationLockButton.ToolTipText = accessibleStateAndAction;
        }
    }

    private void UpdateVideoCommandMenuState()
    {
        bool hasVideo = _imageSourceKind == ImageSourceKind.VideoPlayback && _embeddedVideoPlayer.HasMedia;
        _menuInlineVideoPlay.Enabled = hasVideo;
        _menuSaveVideoFrame.Enabled = hasVideo
            && _embeddedVideoPlayer.CurrentPlaybackState is ViewerVideoPlaybackState.Playing or ViewerVideoPlaybackState.Paused;
    }

    private bool CanToggleImagePresentationMode()
        => _hostedLifetime
            && (_imageSourceKind is ImageSourceKind.File or ImageSourceKind.Clipboard)
            && _displayImage != null;

    private void ResetVideoPlaybackRequest()
    {
        _videoPlaybackRequestId = 0;
        _lastReportedVideoPlaybackRequestId = 0;
        _lastReportedVideoPlaybackState = null;
    }

    private void SetVideoPlaybackRequest(long requestId)
    {
        _videoPlaybackRequestId = requestId > 0 ? requestId : 0;
        _lastReportedVideoPlaybackRequestId = 0;
        _lastReportedVideoPlaybackState = null;
    }

    private void HandleEmbeddedVideoPlaybackStateChanged(ViewerVideoPlaybackState state)
    {
        UpdatePresentationModeUi();
        if (!_activeLoadIsSelectionFollow) ReportVideoPlaybackState(state);
    }

    private void HandleNativeVideoSurfaceArrowKey(Keys keyData)
    {
        if (IsMenuInputActive()
            || HasPlaybackCommandInputFocus())
        {
            return;
        }
        HandleViewerArrowKey(keyData);
    }

    private void ReportCurrentVideoPlaybackState(bool force = false)
        => ReportVideoPlaybackState(_embeddedVideoPlayer.CurrentPlaybackState, force);

    private void ReportVideoPlaybackState(ViewerVideoPlaybackState state, bool force = false)
    {
        if (_activeLoadIsSelectionFollow
            || _imageSourceKind != ImageSourceKind.VideoPlayback
            || _videoPlaybackRequestId <= 0)
        {
            return;
        }
        if (!force
            && _lastReportedVideoPlaybackRequestId == _videoPlaybackRequestId
            && _lastReportedVideoPlaybackState == state)
        {
            return;
        }
        _lastReportedVideoPlaybackRequestId = _videoPlaybackRequestId;
        _lastReportedVideoPlaybackState = state;
        VideoPlaybackStateChanged?.Invoke(_videoPlaybackRequestId, state);
    }

    private void CancelActiveSelectionFollowLoad()
    {
        if (!_activeLoadIsSelectionFollow) return;
        string? path = _activeLoadPath;
        long requestId = _activeLoadResponseRequestId;
        _activeLoadIsSelectionFollow = false;
        ++_loadRequestId;
        ++_imageGeneration;
        _pendingFirstPaintRequestId = 0;
        _imageDecodeCts?.Cancel();
        _ = _embeddedVideoPlayer.CancelPendingSelectionFollowLoad();
        if (!string.IsNullOrWhiteSpace(path)) MediaLoadCompleted?.Invoke(path, requestId, false, true);
    }

    private bool DispatchViewerCommand(string commandId, int? videoSeekSeconds = null)
    {
        if (_hostCommandDispatcher != null)
        {
            bool sent = _hostCommandDispatcher(commandId, videoSeekSeconds);
            if (!sent && commandId == ImageViewerCommandIds.ImageViewerTogglePresentationMode)
            {
                LogService.Warn($"[MidPlayer.PresentationMode] command-send-failed viewer={InstanceId} command={commandId}");
            }
            return sent;
        }

        ImageViewerCommandAction? localAction = commandId switch
        {
            ImageViewerCommandIds.ImageViewerOpen => ImageViewerCommandAction.Open,
            ImageViewerCommandIds.ImageViewerTogglePresentationMode => ImageViewerCommandAction.TogglePresentationMode,
            ImageViewerCommandIds.ImageViewerSaveAs => ImageViewerCommandAction.SaveAs,
            ImageViewerCommandIds.ImageViewerClose => ImageViewerCommandAction.Close,
            ImageViewerCommandIds.ImageViewerCopy => ImageViewerCommandAction.Copy,
            ImageViewerCommandIds.ImageViewerPaste => ImageViewerCommandAction.Paste,
            ImageViewerCommandIds.ImageViewerPasteSelectionStretch => ImageViewerCommandAction.PasteSelectionStretch,
            ImageViewerCommandIds.ImageViewerPasteSelectionOriginal => ImageViewerCommandAction.PasteSelectionOriginal,
            ImageViewerCommandIds.ImageViewerCopySvg => ImageViewerCommandAction.CopySvg,
            ImageViewerCommandIds.ImageViewerQuantize => ImageViewerCommandAction.Quantize,
            ImageViewerCommandIds.ImageViewerReset => ImageViewerCommandAction.Reset,
            ImageViewerCommandIds.ImageViewerRotateRight => ImageViewerCommandAction.RotateRight,
            ImageViewerCommandIds.ImageViewerRotateLeft => ImageViewerCommandAction.RotateLeft,
            ImageViewerCommandIds.ImageViewerFlipHorizontal => ImageViewerCommandAction.FlipHorizontal,
            ImageViewerCommandIds.ImageViewerFlipVertical => ImageViewerCommandAction.FlipVertical,
            ImageViewerCommandIds.ImageViewerShowInfo => ImageViewerCommandAction.ShowInfo,
            ImageViewerCommandIds.ImageViewerUndo => ImageViewerCommandAction.Undo,
            ImageViewerCommandIds.ImageViewerRedo => ImageViewerCommandAction.Redo,
            ImageViewerCommandIds.ImageViewerZoomIn => ImageViewerCommandAction.ZoomIn,
            ImageViewerCommandIds.ImageViewerZoomOut => ImageViewerCommandAction.ZoomOut,
            ImageViewerCommandIds.ImageViewerZoomReset => ImageViewerCommandAction.ZoomReset,
            ImageViewerCommandIds.ImageViewerFullscreenToggle => ImageViewerCommandAction.FullscreenToggle,
            ImageViewerCommandIds.ImageViewerFullscreenExit => ImageViewerCommandAction.FullscreenExit,
            ImageViewerCommandIds.ImageViewerVideoSeekBackward => ImageViewerCommandAction.VideoSeekBackward,
            ImageViewerCommandIds.ImageViewerVideoSeekForward => ImageViewerCommandAction.VideoSeekForward,
            ImageViewerCommandIds.ImageViewerVideoSeekToStart => ImageViewerCommandAction.VideoSeekToStart,
            ImageViewerCommandIds.ImageViewerVideoSeekToPosition => ImageViewerCommandAction.VideoSeekToPosition,
            ImageViewerCommandIds.ImageViewerVideoExternalPlay => ImageViewerCommandAction.VideoExternalPlay,
            ImageViewerCommandIds.ImageViewerVideoSaveFrame => ImageViewerCommandAction.VideoSaveFrame,
            ImageViewerCommandIds.ImageViewerVideoSetPlaybackRate => ImageViewerCommandAction.VideoSetPlaybackRate,
            ImageViewerCommandIds.ImageViewerResize => ImageViewerCommandAction.Resize,
            ImageViewerCommandIds.ImageViewerMosaic => ImageViewerCommandAction.Mosaic,
            ImageViewerCommandIds.ImageViewerBlur => ImageViewerCommandAction.Blur,
            ImageViewerCommandIds.ImageViewerTone => ImageViewerCommandAction.Tone,
            _ => null
        };
        return localAction is { } action && ExecuteRegisteredCommand(action, videoSeekSeconds);
    }

    internal bool ExecuteRegisteredCommand(string actionName, int? videoSeekSeconds = null)
        => Enum.TryParse(actionName, ignoreCase: false, out ImageViewerCommandAction action)
           && ExecuteRegisteredCommand(action, videoSeekSeconds);

    internal bool ExecuteRegisteredCommand(string actionName, int? videoSeekSeconds, double? playbackRate)
        => Enum.TryParse(actionName, ignoreCase: false, out ImageViewerCommandAction action)
           && ExecuteRegisteredCommand(action, videoSeekSeconds, playbackRate);

    private bool ExecuteRegisteredCommand(ImageViewerCommandAction action, int? videoSeekSeconds = null)
        => ExecuteRegisteredCommand(action, videoSeekSeconds, null);

    private bool ExecuteRegisteredCommand(ImageViewerCommandAction action, int? videoSeekSeconds, double? playbackRate)
    {
        switch (action)
        {
            case ImageViewerCommandAction.Open: ExecuteOpenImageFromFile(); break;
            case ImageViewerCommandAction.TogglePresentationMode:
                ApplyPresentationMode(
                    _presentationMode == ViewerPresentationMode.PreviewFollow
                        ? ViewerPresentationMode.PlaybackLocked
                        : ViewerPresentationMode.PreviewFollow,
                    unchecked(_presentationGeneration + 1));
                break;
            case ImageViewerCommandAction.SaveAs: ExecuteSaveImageAs(); break;
            case ImageViewerCommandAction.Close: Close(); break;
            case ImageViewerCommandAction.Copy: CopyImageToClipboard(); break;
            case ImageViewerCommandAction.Paste: PasteImageFromClipboard(); break;
            case ImageViewerCommandAction.PasteSelectionStretch: PasteImageIntoSelectionFromClipboard(); break;
            case ImageViewerCommandAction.PasteSelectionOriginal: PasteImageIntoSelectionFromClipboard(originalSize: true); break;
            case ImageViewerCommandAction.CopySvg: ExecuteCopySvg(); break;
            case ImageViewerCommandAction.Quantize: ExecuteQuantizeImage(); break;
            case ImageViewerCommandAction.Reset: ExecuteResetImage(); break;
            case ImageViewerCommandAction.RotateRight: ExecuteRotateRight(); break;
            case ImageViewerCommandAction.RotateLeft: ExecuteRotateLeft(); break;
            case ImageViewerCommandAction.FlipHorizontal: ExecuteFlipHorizontal(); break;
            case ImageViewerCommandAction.FlipVertical: ExecuteFlipVertical(); break;
            case ImageViewerCommandAction.ShowInfo: ShowImageInformationDialog(); break;
            case ImageViewerCommandAction.Undo: UndoImageOperation(); break;
            case ImageViewerCommandAction.Redo: RedoImageOperation(); break;
            case ImageViewerCommandAction.ZoomIn: SetZoom(_zoom * ZoomStep); break;
            case ImageViewerCommandAction.ZoomOut: SetZoom(_zoom / ZoomStep); break;
            case ImageViewerCommandAction.ZoomReset: SetZoom(1.0f); break;
            case ImageViewerCommandAction.FullscreenToggle: ToggleFullscreen(); break;
            case ImageViewerCommandAction.FullscreenExit: ExitFullscreen(); break;
            case ImageViewerCommandAction.VideoExternalPlay:
                if (!CanRunVideoCommand()) return false;
                _ = _embeddedVideoPlayer.TogglePlaybackAsync();
                break;
            case ImageViewerCommandAction.VideoSeekToStart:
                if (!CanRunVideoCommand()) return false;
                _embeddedVideoPlayer.SeekToSeconds(0);
                break;
            case ImageViewerCommandAction.VideoSeekBackward:
            case ImageViewerCommandAction.VideoSeekForward:
                if (!CanRunVideoCommand()) return false;
                long seekMagnitude = Math.Abs((long)(videoSeekSeconds ?? _previewSettings.VideoSeekStepSeconds));
                if (seekMagnitude is < 1 or > 600) return false;
                int seekDelta = checked((int)(action == ImageViewerCommandAction.VideoSeekBackward ? -seekMagnitude : seekMagnitude));
                _embeddedVideoPlayer.SeekBySeconds(seekDelta);
                break;
            case ImageViewerCommandAction.VideoSeekToPosition:
                if (!CanRunVideoCommand() || videoSeekSeconds is not { } targetSeconds || targetSeconds < 0) return false;
                _embeddedVideoPlayer.SeekToSeconds(targetSeconds);
                break;
            case ImageViewerCommandAction.VideoSaveFrame: ExecuteSaveCurrentVideoFrame(); break;
            case ImageViewerCommandAction.VideoSetPlaybackRate:
                if (playbackRate is not { } rate || !double.IsFinite(rate)) return false;
                return _embeddedVideoPlayer.SetPlaybackRate(rate);
            case ImageViewerCommandAction.Resize: PromptAndResizeImage(); break;
            case ImageViewerCommandAction.Mosaic: PromptAndApplyMosaic(); break;
            case ImageViewerCommandAction.Blur: PromptAndApplyBlur(); break;
            case ImageViewerCommandAction.Tone: PromptAndAdjustTone(); break;
            default: return false;
        }
        return true;
    }

    private bool CanRunVideoCommand()
        => _imageSourceKind == ImageSourceKind.VideoPlayback && _embeddedVideoPlayer.HasMedia;

    public void EnsureVisibleAndActivated()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Show();
        BringToFront();
        Activate();
    }

    public async void LoadImage(
        string path,
        bool showErrorMessage = true,
        long? externalRequestId = null,
        bool isSelectionFollow = false,
        int? selectionGeneration = null,
        int? presentationGeneration = null)
    {
        if (isSelectionFollow && !TryAcceptSelectionFollow(selectionGeneration, presentationGeneration))
        {
            RejectMediaLoad(path, externalRequestId);
            return;
        }
        long loadStartedAt = Stopwatch.GetTimestamp();
        _imageDecodeCts?.Cancel();
        var decodeCts = new CancellationTokenSource();
        _imageDecodeCts = decodeCts;
        if (isSelectionFollow)
        {
            _ = _embeddedVideoPlayer.CancelPendingSelectionFollowLoad();
        }
        long reqId = ++_loadRequestId;
        long responseRequestId = externalRequestId ?? reqId;
        _activeLoadIsSelectionFollow = isSelectionFollow;
        _activeLoadPath = path;
        _activeLoadResponseRequestId = responseRequestId;
        _pendingFirstPaintRequestId = 0;
        _imageGeneration++;
        if (!isSelectionFollow)
        {
            ResetVideoPlaybackRequest();
            _imageSourceKind = ImageSourceKind.File;
            UpdatePresentationModeUi();
            _embeddedVideoPlayer.StopPlayback();
            _videoInitialWindowSizeApplied = false;
            _videoManuallyResizedBeforeDimensions = false;
            ClearDecoderStatus();
            _embeddedVideoPlayer.Visible = false;
            pictureBox1.Visible = true;
            statusStrip1.Visible = true;
            _currentPath = path;
            Text = $"{Path.GetFileName(path)} - MidFD Image Viewer";
            ClearSelection();
        }

        bool isSvg = string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);
        _previewSettings.SusiePlugins.DecoderByExtension.TryGetValue(Path.GetExtension(path), out string? decoderPreference);
        LogService.Info(
            $"[MidPlayerTiming] stage=load-received request={responseRequestId} processId={Environment.ProcessId} " +
            $"visible={Visible} svg={isSvg} decoderPreference={decoderPreference ?? "auto"} " +
            $"susieEnabled={_previewSettings.SusiePlugins.Plugins.Count(plugin => plugin.IsEnabled)}");

        if (!isSelectionFollow)
        {
            // Explicit opens retain their existing loading feedback.
            statusLabel.Text = isSvg ? "SVGレンダリング中..." : "画像読み込み中...";
            _loadingLabel.Text = isSvg ? "Rendering SVG..." : "Loading Image...";
            CenterLoadingLabel();
            _loadingLabel.Visible = true;
            SetQuantizeMenuEnabled(false);
        }

        if (!isSelectionFollow)
        {
            DisposeImage(_originalImage);
            DisposeImage(_displayImage);
            _originalImage = null;
            _displayImage = null;
            pictureBox1.Image = null;
            UpdatePresentationModeUi();
        }

        try
        {
            long decodeStartedAt = Stopwatch.GetTimestamp();
            var result = await DecodeImageForViewerAsync(path, isSvg, decodeCts.Token);
            LogService.Info(
                $"[MidPlayerTiming] stage=decode-complete request={responseRequestId} " +
                $"decoder={(isSvg ? "svg" : result.Kind?.ToString() ?? "failed")} fallback={result.UsedFallback} " +
                $"elapsedMs={Stopwatch.GetElapsedTime(decodeStartedAt).TotalMilliseconds:0.0} " +
                $"totalMs={Stopwatch.GetElapsedTime(loadStartedAt).TotalMilliseconds:0.0}");

            if (reqId != _loadRequestId || IsDisposed
                || (isSelectionFollow && !IsSelectionFollowCurrent(selectionGeneration, presentationGeneration)))
            {
                result.Image?.Dispose();
                MediaLoadCompleted?.Invoke(path, responseRequestId, false, true);
                return;
            }

            if (result.Image == null)
            {
                if (!isSelectionFollow)
                {
                    ClearDecoderStatus();
                    statusLabel.Text = isSvg ? "SVG読み込み失敗" : "画像読み込み失敗";
                }
                string userMessage = isSvg
                    ? "SVGを読み込めませんでした。\nファイル形式やサイズ、または未対応の要素が原因の可能性があります。"
                    : result.ErrorMessage;
                LogService.Error($"[ImageViewer] LoadImage failed path='{path}' message='{userMessage}'");
                if (showErrorMessage)
                {
                    MessageBox.Show(userMessage, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                MediaLoadCompleted?.Invoke(path, responseRequestId, false, false);
                return;
            }

            if (isSelectionFollow)
            {
                ResetVideoPlaybackRequest();
                _imageSourceKind = ImageSourceKind.File;
                UpdatePresentationModeUi();
                _embeddedVideoPlayer.StopPlayback();
                _embeddedVideoPlayer.Visible = false;
                pictureBox1.Visible = true;
                _videoInitialWindowSizeApplied = false;
                _videoManuallyResizedBeforeDimensions = false;
                statusStrip1.Visible = true;
                _currentPath = path;
                Text = $"{Path.GetFileName(path)} - MidFD Image Viewer";
                ClearSelection();
                ClearDecoderStatus();
            }
            long applyStartedAt = Stopwatch.GetTimestamp();
            _pendingFirstPaintRequestId = responseRequestId;
            _pendingFirstPaintStartedAt = loadStartedAt;
            ReplaceCurrentImages(result.Image, clearHistory: true);
            ApplyInitialZoom(result.Image);
            UpdatePresentationModeUi();
            LogService.Info(
                $"[MidPlayerTiming] stage=bitmap-applied request={responseRequestId} " +
                $"elapsedMs={Stopwatch.GetElapsedTime(applyStartedAt).TotalMilliseconds:0.0} " +
                $"totalMs={Stopwatch.GetElapsedTime(loadStartedAt).TotalMilliseconds:0.0}");
            if (isSvg)
            {
                SetDecoderStatus("Renderer: SVG", "Renderer: SVG");
            }
            else if (result.Succeeded && result.Kind is { } decoderKind)
            {
                string decoderLabel = decoderKind switch
                {
                    ImageDecoderKind.Susie => $"Decoder: Susie / {result.DisplayName ?? "Plugin"}",
                    _ when result.UsedFallback => "Decoder: 標準 (fallback)",
                    _ => "Decoder: 標準"
                };
                string tooltip = string.Join(Environment.NewLine, new[]
                {
                    decoderLabel,
                    result.DecoderPath,
                    result.ErrorMessage
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                SetDecoderStatus(decoderLabel, tooltip);
            }
            statusLabel.Text = !string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? $"標準decoderで読み込み完了: {result.ErrorMessage}"
                : isSvg ? "SVG読み込み完了" : "読み込み完了";
            LogService.Info(
                $"[MidPlayerTiming] stage=load-result request={responseRequestId} success=true " +
                $"elapsedMs={Stopwatch.GetElapsedTime(loadStartedAt).TotalMilliseconds:0.0}");
            MediaLoadCompleted?.Invoke(path, responseRequestId, true, false);
        }
        catch (OperationCanceledException)
        {
            MediaLoadCompleted?.Invoke(path, responseRequestId, false, true);
        }
        catch (Exception ex)
        {
            if (reqId == _loadRequestId && !IsDisposed)
            {
                if (!isSelectionFollow)
                {
                    ClearDecoderStatus();
                    statusLabel.Text = "エラーが発生しました";
                }
                LogService.Error($"[ImageViewer] LoadImage exception path='{path}'", ex);
                if (showErrorMessage)
                {
                    MessageBox.Show($"読み込み中にエラーが発生しました:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                MediaLoadCompleted?.Invoke(path, responseRequestId, false, false);
            }
            else if (reqId != _loadRequestId)
            {
                MediaLoadCompleted?.Invoke(path, responseRequestId, false, true);
            }
        }
        finally
        {
            if (reqId == _loadRequestId && !IsDisposed && !isSelectionFollow)
            {
                _loadingLabel.Visible = false;
                SetQuantizeMenuEnabled(true);
            }
            if (ReferenceEquals(_imageDecodeCts, decodeCts)) _imageDecodeCts = null;
            if (reqId == _loadRequestId) _activeLoadIsSelectionFollow = false;
            decodeCts.Dispose();
        }
    }

    private Task<ImageDecodeResult> DecodeImageForViewerAsync(string path, bool isSvg, CancellationToken cancellationToken)
        => DecodeImageOverrideForTests is { } decodeOverride
            ? decodeOverride(path, cancellationToken)
            : isSvg
            ? Task.Run(() =>
            {
                var (image, errorMessage) = ImagePreviewService.GetPreviewImage(path);
                return image == null
                    ? ImageDecodeResult.Failure(errorMessage)
                    : new ImageDecodeResult(image, errorMessage);
            }, cancellationToken)
            : _susieDecoderService.DecodeAsync(path, cancellationToken);

    private void ClearDecoderStatus()
    {
        decoderStatusLabel.Text = string.Empty;
        decoderStatusLabel.ToolTipText = string.Empty;
    }

    private void SetDecoderStatus(string text, string toolTip)
    {
        const int maxDisplayElements = 32;
        int[] textElementStarts = StringInfo.ParseCombiningCharacters(text);
        decoderStatusLabel.Text = textElementStarts.Length <= maxDisplayElements
            ? text
            : text[..textElementStarts[maxDisplayElements - 1]] + "…";
        decoderStatusLabel.ToolTipText = toolTip;
    }

    private void CenterLoadingLabel()
    {
        _loadingLabel.Location = new Point(
            (imageScrollPanel.Width - _loadingLabel.Width) / 2,
            (imageScrollPanel.Height - _loadingLabel.Height) / 2
        );
    }

    internal void LoadMedia(
        string path,
        ImageViewerMediaKind kind,
        bool showErrorMessage = true,
        long? requestId = null,
        bool isSelectionFollow = false,
        int? selectionGeneration = null,
        int? presentationGeneration = null)
    {
        if (kind == ImageViewerMediaKind.Video)
        {
            LoadVideoPlayback(
                path,
                _previewSettings.VideoSkipSeconds,
                _previewSettings.VideoPlaybackVolumePercent,
                requestId,
                isSelectionFollow,
                selectionGeneration,
                presentationGeneration);
            return;
        }
        LoadImage(path, showErrorMessage, requestId, isSelectionFollow, selectionGeneration, presentationGeneration);
    }

    private void menuQuantize_Click(object? sender, EventArgs e)
        => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerQuantize);

    private async void ExecuteQuantizeImage()
    {
        if (!_allowQuantization)
        {
            statusLabel.Text = "標準機能（推奨）では画像減色は無効です。";
            return;
        }

        if (_originalImage == null)
        {
            return;
        }

        using var dialog = new ImageQuantizationDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ResultRequest == null)
        {
            return;
        }

        SetQuantizeMenuEnabled(false);
        string label = dialog.ResultLabel;
        int imageGeneration = _imageGeneration;
        statusLabel.Text = $"減色中: {label}";
        Bitmap? sourceImage = null;
        Bitmap? result = null;
        try
        {
            Bitmap quantizeSource = new(_displayImage ?? _originalImage);
            sourceImage = quantizeSource;
            result = await Task.Run(() => ImageQuantizationService.Quantize(quantizeSource, dialog.ResultRequest));
            sourceImage.Dispose();
            sourceImage = null;
            if (!ShouldApplyQuantizeResult(imageGeneration, _imageGeneration, IsDisposed))
            {
                result.Dispose();
                result = null;
                return;
            }
            PushUndoState("減色前");
            Bitmap appliedResult = result;
            SetDisplayImage(appliedResult);
            result = null;
            ApplyInitialZoom(appliedResult);
            statusLabel.Text = $"減色適用: {label}";
            ClearRedoStack();
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"減色失敗: {ex.Message}";
        }
        finally
        {
            sourceImage?.Dispose();
            result?.Dispose();
            if (ShouldApplyQuantizeResult(imageGeneration, _imageGeneration, IsDisposed)) SetQuantizeMenuEnabled(true);
        }
    }

    private void menuResetImage_Click(object? sender, EventArgs e)
        => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerReset);

    private void ExecuteResetImage()
    {
        if (_originalImage == null || _displayImage == null)
        {
            return;
        }
        PushUndoState("元画像へ戻す前");
        ResetDisplayTransformState();
        SetDisplayImage((Bitmap)_originalImage.Clone());
        ApplyInitialZoom(_displayImage);
        statusLabel.Text = "元画像に戻しました";
        ClearRedoStack();
    }

    private void menuCopySvg_Click(object? sender, EventArgs e)
        => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerCopySvg);

    private void ExecuteCopySvg()
    {
        if (!_allowSvgClipboard)
        {
            statusLabel.Text = "標準機能（推奨）では SVG コピーは無効です。";
            return;
        }

        if (_currentPath == null) return;

        if (SvgClipboardExportService.CopyToClipboard(_currentPath, _displayImage))
        {
            statusLabel.Text = "SVGをクリップボードにコピーしました。";
        }
        else
        {
            statusLabel.Text = "SVGのコピーに失敗しました。";
        }
    }

    private void PromptAndResizeImage()
    {
        if (_displayImage == null) return;
        var values = ImageEditDialogs.PromptResize(this, _displayImage.Width, _displayImage.Height);
        if (values is not { } resize) return;
        _ = ApplyImageEditAsync("リサイズ前", source => ImageEditService.Resize(source, resize.Width, resize.Height, resize.PreserveAspect));
    }

    private void PromptAndApplyMosaic()
    {
        if (!TryGetEditSelection(out Rectangle area)) return;
        int? blockSize = ImageEditDialogs.PromptInteger(this, "モザイク", "ブロックサイズ (2～256)", 12, 2, 256);
        if (blockSize is not { } size) return;
        _ = ApplyImageEditAsync("モザイク適用前", source => ImageEditService.ApplyMosaic(source, area, size));
    }

    private void PromptAndApplyBlur()
    {
        if (!TryGetEditSelection(out Rectangle area)) return;
        int? radius = ImageEditDialogs.PromptInteger(this, "ぼかし", "半径 (1～64)", 5, 1, 64);
        if (radius is not { } selectedRadius) return;
        _ = ApplyImageEditAsync("ぼかし適用前", source => ImageEditService.ApplyBlur(source, area, selectedRadius));
    }

    private void PromptAndAdjustTone()
    {
        if (_displayImage == null) return;
        using var source = new Bitmap(_displayImage);
        int generation = _imageGeneration;
        ImageToneSettings? values = ImageEditDialogs.PromptTone(
            this,
            new ImageToneSettings(0, 0, 100, 1.0),
            preview => RequestTonePreview(source, preview, generation));
        ResetTonePreview();
        if (values is not { } tone) return;
        _ = ApplyImageEditAsync("色調補正前", bitmap => ImageEditService.AdjustTone(bitmap, tone.Brightness, tone.Contrast, tone.Saturation, tone.Gamma));
    }

    private bool TryGetEditSelection(out Rectangle area)
    {
        area = Rectangle.Empty;
        if (_displayImage == null || _selectionClientRectangle is not { } clientSelection)
        {
            statusLabel.Text = "先に画像上で範囲を選択してください。";
            return false;
        }
        area = ClientSelectionRectToImageRect(clientSelection);
        if (area.Width <= 0 || area.Height <= 0)
        {
            statusLabel.Text = "選択範囲がありません。";
            return false;
        }
        return true;
    }

    private async Task ApplyImageEditAsync(string undoLabel, Func<Bitmap, Bitmap> edit, bool preserveView = false)
    {
        if (_displayImage == null) return;
        int generation = _imageGeneration;
        int appliedGeneration = -1;
        var source = (Bitmap)_displayImage.Clone();
        Bitmap? result = null;
        SetQuantizeMenuEnabled(false);
        statusLabel.Text = "画像処理中...";
        try
        {
            result = await Task.Run(() => edit(source));
            if (!ShouldApplyQuantizeResult(generation, _imageGeneration, IsDisposed)) return;
            PushUndoState(undoLabel);
            SetDisplayImage(result);
            result = null;
            appliedGeneration = _imageGeneration;
            if (!preserveView)
            {
                ApplyInitialZoom(_displayImage!);
            }
            ClearRedoStack();
            statusLabel.Text = "画像処理を適用しました。";
        }
        catch (Exception ex)
        {
            if (ShouldApplyQuantizeResult(generation, _imageGeneration, IsDisposed))
            {
                statusLabel.Text = $"画像処理に失敗しました: {ex.Message}";
            }
        }
        finally
        {
            source.Dispose();
            result?.Dispose();
            if (ShouldRestoreImageEditState(generation, _imageGeneration, appliedGeneration, IsDisposed))
            {
                SetQuantizeMenuEnabled(true);
            }
        }
    }

    internal static bool ShouldRestoreImageEditState(
        int requestGeneration,
        int currentGeneration,
        int appliedGeneration,
        bool isDisposed)
        => !isDisposed
            && (requestGeneration == currentGeneration || appliedGeneration == currentGeneration);

    private async void RequestTonePreview(Bitmap source, ImageToneSettings settings, int generation)
    {
        int sequence = Interlocked.Increment(ref _tonePreviewSequence);
        using var sourceCopy = new Bitmap(source);
        Bitmap? preview = null;
        try
        {
            preview = await Task.Run(() => ImageEditService.AdjustTone(sourceCopy, settings.Brightness, settings.Contrast, settings.Saturation, settings.Gamma));
            if (IsDisposed || generation != _imageGeneration || sequence != _tonePreviewSequence)
            {
                preview.Dispose();
                return;
            }
            _tonePreviewImage?.Dispose();
            _tonePreviewImage = preview;
            pictureBox1.Image = preview;
            preview = null;
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidPlayer Tone Preview] {ex.Message}");
        }
        finally
        {
            preview?.Dispose();
        }
    }

    private void ResetTonePreview()
    {
        Interlocked.Increment(ref _tonePreviewSequence);
        pictureBox1.Image = _displayImage;
        _tonePreviewImage?.Dispose();
        _tonePreviewImage = null;
    }

    private void ReplaceCurrentImages(Bitmap source, bool clearHistory)
    {
        _imageGeneration++;
        ClearSelection();
        ResetDisplayTransformState();
        DisposeImage(_originalImage);
        DisposeImage(_displayImage);
        _originalImage = (Bitmap)source.Clone();
        _displayImage = source;
        pictureBox1.Image = _displayImage;
        if (clearHistory)
        {
            ClearHistory();
        }
        UpdateQuantizeMenuState();
    }

    private void SetDisplayImage(Bitmap bitmap)
    {
        _imageGeneration++;
        ClearSelection();
        DisposeImage(_displayImage);
        _displayImage = bitmap;
        pictureBox1.Image = _displayImage;
        UpdateQuantizeMenuState();
    }

    private void ResetDisplayTransformState()
    {
        _displayRotationQuarterTurns = 0;
        _displayFlipHorizontal = false;
        _displayFlipVertical = false;
    }

    private void RotateDisplayImage(RotateFlipType rotateFlipType, string undoLabel, string statusText)
    {
        if (_displayImage == null)
        {
            return;
        }

        PushUndoState(undoLabel);
        Bitmap transformed = new Bitmap(_displayImage);
        transformed.RotateFlip(rotateFlipType);
        SetDisplayImage(transformed);
        ApplyInitialZoom(transformed);
        statusLabel.Text = statusText;
        ClearRedoStack();
    }

    private void menuRotateRight_Click(object? sender, EventArgs e)
        => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerRotateRight);

    private void ExecuteRotateRight()
    {
        _displayRotationQuarterTurns = (_displayRotationQuarterTurns + 1) % 4;
        RotateDisplayImage(RotateFlipType.Rotate90FlipNone, "右90度回転前", "右90度回転");
    }

    private void menuRotateLeft_Click(object? sender, EventArgs e)
        => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerRotateLeft);

    private void ExecuteRotateLeft()
    {
        _displayRotationQuarterTurns = (_displayRotationQuarterTurns + 3) % 4;
        RotateDisplayImage(RotateFlipType.Rotate270FlipNone, "左90度回転前", "左90度回転");
    }

    private void menuFlipHorizontal_Click(object? sender, EventArgs e)
        => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerFlipHorizontal);

    private void ExecuteFlipHorizontal()
    {
        _displayFlipHorizontal = !_displayFlipHorizontal;
        RotateDisplayImage(RotateFlipType.RotateNoneFlipX, "左右反転前", "左右反転");
    }

    private void menuFlipVertical_Click(object? sender, EventArgs e)
        => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerFlipVertical);

    private void ExecuteFlipVertical()
    {
        _displayFlipVertical = !_displayFlipVertical;
        RotateDisplayImage(RotateFlipType.RotateNoneFlipY, "上下反転前", "上下反転");
    }

    private void UpdateQuantizeMenuState()
    {
        bool allowQuantize = _allowQuantization;
        bool allowSvgClipboard = _allowSvgClipboard;
        _menuQuantize.Visible = allowQuantize;
        _menuQuantize.Enabled = allowQuantize && _displayImage != null;
        _menuResetImage.Enabled = _displayImage != null;

        // SVGコピーメニューの表示制御
        bool isSvg = _currentPath != null &&
                     (string.Equals(Path.GetExtension(_currentPath), ".svg", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(Path.GetExtension(_currentPath), ".svgz", StringComparison.OrdinalIgnoreCase));
        _menuCopySvg.Visible = allowSvgClipboard && isSvg;
        _menuCopySvg.Enabled = allowSvgClipboard && _displayImage != null;
        _menuImageInfo.Enabled = _displayImage != null || _originalImage != null;
    }

    private void SetQuantizeMenuEnabled(bool enabled)
    {
        _menuQuantize.Enabled = _allowQuantization && enabled && _displayImage != null;
        _menuResetImage.Enabled = enabled && _displayImage != null;
        _menuCopySvg.Enabled = _allowSvgClipboard && enabled && _displayImage != null;
        _menuImageInfo.Enabled = enabled && (_displayImage != null || _originalImage != null);
    }

    internal static bool ShouldApplyQuantizeResult(int requestGeneration, int currentGeneration, bool isDisposed)
        => requestGeneration == currentGeneration && !isDisposed;

    private void PushUndoState(string label)
    {
        if (_displayImage == null) return;
        _undoStack.Push(new ImageHistoryEntry { Image = (Bitmap)_displayImage.Clone(), Label = label });
        TrimHistoryStack(_undoStack);
    }

    private void PushRedoState(string label)
    {
        if (_displayImage == null) return;
        _redoStack.Push(new ImageHistoryEntry { Image = (Bitmap)_displayImage.Clone(), Label = label });
        TrimHistoryStack(_redoStack);
    }

    private void ClearRedoStack()
    {
        while (_redoStack.Count > 0)
        {
            _redoStack.Pop().Image.Dispose();
        }
    }

    private void ClearHistory()
    {
        while (_undoStack.Count > 0)
        {
            _undoStack.Pop().Image.Dispose();
        }
        while (_redoStack.Count > 0)
        {
            _redoStack.Pop().Image.Dispose();
        }
    }

    private void UndoImageOperation()
    {
        if (_undoStack.Count == 0 || _displayImage == null) return;
        PushRedoState("Redo");
        var entry = _undoStack.Pop();
        SetDisplayImage(entry.Image);
        ApplyInitialZoom(entry.Image);
        statusLabel.Text = $"Undo: {entry.Label}";
    }

    private void RedoImageOperation()
    {
        if (_redoStack.Count == 0 || _displayImage == null) return;
        PushUndoState("Undo");
        var entry = _redoStack.Pop();
        SetDisplayImage(entry.Image);
        ApplyInitialZoom(entry.Image);
        statusLabel.Text = $"Redo: {entry.Label}";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (IsMenuInputActive()) return base.ProcessCmdKey(ref msg, keyData);
        if (TryHandleSpaceCommand(ref msg, keyData)) return true;
        if ((keyData == (Keys.Control | Keys.V) || keyData == (Keys.Control | Keys.Shift | Keys.V)) && HasEditableInputFocus())
        {
            return false;
        }
        bool isVideoPlayback = _imageSourceKind == ImageSourceKind.VideoPlayback;
        if (!HasPlaybackCommandInputFocus()
            && (isVideoPlayback || !_embeddedVideoPlayer.HasPlaybackControlFocus)
            && HandleViewerArrowKey(keyData))
        {
            return true;
        }

        if (HandleViewerShortcutKey(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HandleViewerShortcutKey(Keys keyData)
    {
        if (HasEditableInputFocus()) return false;
        if (TryToggleHostedImagePresentationMode(keyData)) return true;
        Keys modifiers = keyData & (Keys.Control | Keys.Shift | Keys.Alt);
        Keys keyCode = keyData & Keys.KeyCode;

        if (keyCode == Keys.V && (modifiers == Keys.Control || modifiers == (Keys.Control | Keys.Shift)))
        {
            string command = HasValidPasteSelection()
                ? (modifiers == Keys.Control
                    ? ImageViewerCommandIds.ImageViewerPasteSelectionStretch
                    : ImageViewerCommandIds.ImageViewerPasteSelectionOriginal)
                : ImageViewerCommandIds.ImageViewerPaste;
            DispatchViewerCommand(command);
            return true;
        }

        if (modifiers == Keys.Control)
        {
            if (keyCode == Keys.C)
            {
                DispatchViewerCommand(ImageViewerCommandIds.ImageViewerCopy);
                return true;
            }

            if (keyCode == Keys.R)
            {
                menuResetImage_Click(this, EventArgs.Empty);
                return true;
            }
        }

        if (modifiers == Keys.None)
        {
            if (keyCode == Keys.R)
            {
                menuRotateRight_Click(this, EventArgs.Empty);
                return true;
            }

            if (keyCode == Keys.L)
            {
                menuRotateLeft_Click(this, EventArgs.Empty);
                return true;
            }

            if (keyCode == Keys.H)
            {
                menuFlipHorizontal_Click(this, EventArgs.Empty);
                return true;
            }

            if (keyCode == Keys.V)
            {
                menuFlipVertical_Click(this, EventArgs.Empty);
                return true;
            }
        }

        return false;
    }

    private bool TryToggleHostedImagePresentationMode(Keys keyData)
    {
        if (keyData != (Keys.Control | Keys.Enter)
            || IsMenuInputActive()
            || HasEditableInputFocus()
            || !CanToggleImagePresentationMode())
        {
            return false;
        }

        DispatchViewerCommand(ImageViewerCommandIds.ImageViewerTogglePresentationMode);
        return true;
    }

    private bool HandleViewerArrowKey(Keys keyData)
    {
        Keys modifiers = keyData & (Keys.Control | Keys.Shift | Keys.Alt);
        Keys keyCode = keyData & Keys.KeyCode;

        if (_imageSourceKind == ImageSourceKind.VideoPlayback
            && modifiers == Keys.None
            && keyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            ViewerVideoPlaybackState state = _embeddedVideoPlayer.CurrentPlaybackState;
            if (_hostedLifetime && _presentationMode == ViewerPresentationMode.PreviewFollow)
            {
                BrowserNavigationRequested?.Invoke(keyCode);
                return true;
            }

            if (state is ViewerVideoPlaybackState.Playing or ViewerVideoPlaybackState.Paused)
            {
                switch (keyCode)
                {
                    case Keys.Left:
                        _embeddedVideoPlayer.SeekBySeconds(-_previewSettings.VideoSeekStepSeconds);
                        break;
                    case Keys.Right:
                        _embeddedVideoPlayer.SeekBySeconds(_previewSettings.VideoSeekStepSeconds);
                        break;
                    case Keys.Up:
                        _embeddedVideoPlayer.AdjustVolume(5);
                        break;
                    case Keys.Down:
                        _embeddedVideoPlayer.AdjustVolume(-5);
                        break;
                }
                return true;
            }
            return _hostedLifetime && _presentationMode == ViewerPresentationMode.PlaybackLocked;
        }

        if (_hostedLifetime
            && _presentationMode == ViewerPresentationMode.PreviewFollow
            && modifiers == Keys.None
            && keyCode is Keys.Up or Keys.Down or Keys.Left or Keys.Right)
        {
            BrowserNavigationRequested?.Invoke(keyCode);
            return true;
        }

        return false;
    }

    private void ImageViewerForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (TryHandleSpaceKeyEvent(e)) return;

        if (TryToggleHostedImagePresentationMode(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.Z)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerUndo);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.Y)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerRedo);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape && e.Modifiers == Keys.None)
        {
            if (_isFullscreen)
            {
                DispatchViewerCommand(ImageViewerCommandIds.ImageViewerFullscreenExit);
            }
            else
            {
                DispatchViewerCommand(ImageViewerCommandIds.ImageViewerClose);
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Enter)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerClose);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.F)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerFullscreenToggle);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if ((e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Oem6) && !e.Control && !e.Alt)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerZoomIn);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if ((e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.OemOpenBrackets) && !e.Control && !e.Alt)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerZoomOut);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0 || e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerZoomReset);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (HandleViewerShortcutKey(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
    }

    private bool HasEditableInputFocus()
    {
        Control? focusedControl = ActiveControl;
        while (focusedControl is ContainerControl container && container.ActiveControl is { } child)
        {
            focusedControl = child;
        }

        return focusedControl is TextBoxBase or ComboBox or NumericUpDown or ToolStrip;
    }

    private bool HasPlaybackCommandInputFocus()
    {
        if (_embeddedVideoPlayer.PlaybackRateComboFocused)
        {
            // The closed DropDownList is a playback control, not an editable text field.
            // When its list is open, its keyboard selection remains the active interaction.
            return _embeddedVideoPlayer.PlaybackRateComboDroppedDown;
        }
        return HasEditableInputFocus();
    }

    private bool TryHandleSpaceCommand(ref Message message, Keys keyData)
    {
        Keys modifiers = keyData & Keys.Modifiers;
        if ((keyData & Keys.KeyCode) != Keys.Space || (modifiers & (Keys.Control | Keys.Alt)) != 0)
            return false;

        bool isVideoPlayback = _imageSourceKind == ImageSourceKind.VideoPlayback;
        if (IsMenuInputActive()
            || (isVideoPlayback ? HasPlaybackCommandInputFocus() : HasEditableInputFocus())
            || (!isVideoPlayback && (_embeddedVideoPlayer.HasPlaybackControlFocus
                || GetDeepestActiveControl() is ButtonBase)))
            return false;

        if (message.Msg == WmKeyDown && (message.LParam.ToInt64() & KeyWasPreviouslyDownMask) != 0)
            return true;

        if (!isVideoPlayback && !_hostedLifetime) return false;
        HandleSpaceInput();
        return true;
    }

    private bool TryHandleSpaceKeyEvent(KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Space || e.Control || e.Alt || IsMenuInputActive())
            return false;

        bool isVideoPlayback = _imageSourceKind == ImageSourceKind.VideoPlayback;
        if ((isVideoPlayback ? HasPlaybackCommandInputFocus() : HasEditableInputFocus())
            || (!isVideoPlayback && (_embeddedVideoPlayer.HasPlaybackControlFocus
                || GetDeepestActiveControl() is ButtonBase))
            || (!_hostedLifetime && !isVideoPlayback))
            return false;

        HandleSpaceInput();
        e.Handled = true;
        e.SuppressKeyPress = true;
        return true;
    }

    private Control? GetDeepestActiveControl()
    {
        Control? focusedControl = ActiveControl;
        while (focusedControl is ContainerControl container && container.ActiveControl is { } child)
            focusedControl = child;
        return focusedControl;
    }

    private bool IsMenuInputActive()
    {
        if (menuStrip1.ContainsFocus) return true;
        return HasOpenMenu(menuStrip1.Items);
    }

    private static bool HasOpenMenu(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripDropDownItem dropDownItem
                && dropDownItem.DropDown.Visible)
            {
                return true;
            }
            if (item is ToolStripDropDownItem nested && HasOpenMenu(nested.DropDownItems))
                return true;
        }
        return false;
    }

    private void HandleSpaceInput()
    {
        if (_imageSourceKind == ImageSourceKind.VideoPlayback)
        {
            _ = _embeddedVideoPlayer.TogglePlaybackAsync();
            return;
        }

        if (!_hostedLifetime)
        {
            return;
        }

        if (_presentationMode == ViewerPresentationMode.PlaybackLocked)
        {
            return;
        }

        MarkToggleRequested?.Invoke();
    }

    private void HandleNativeVideoSurfaceSpace()
    {
        if (IsMenuInputActive() || HasPlaybackCommandInputFocus()) return;
        HandleSpaceInput();
    }

    private void ConfigureMediaDropTargets(bool enabled)
    {
        bool allowDrop = enabled && !_hostedLifetime && _hostCommandDispatcher == null;
        Control[] targets = [this, imageScrollPanel, pictureBox1];
        foreach (Control target in targets)
        {
            target.AllowDrop = allowDrop;
            target.DragEnter -= ImageViewerForm_MediaDragEnter;
            target.DragDrop -= ImageViewerForm_MediaDragDrop;
            if (!allowDrop) continue;
            target.DragEnter += ImageViewerForm_MediaDragEnter;
            target.DragDrop += ImageViewerForm_MediaDragDrop;
        }
        _embeddedVideoPlayer.SetMediaDropTargetsEnabled(allowDrop);
    }

    private void ImageViewerForm_MediaDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = TryGetDroppedMedia(e.Data, out _, out _) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void ImageViewerForm_MediaDragDrop(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (!TryGetDroppedMedia(e.Data, out string path, out ImageViewerMediaKind kind)) return;
        e.Effect = DragDropEffects.Copy;
        LoadMedia(path, kind);
    }

    private bool TryGetDroppedMedia(IDataObject? data, out string path, out ImageViewerMediaKind kind)
    {
        path = string.Empty;
        kind = ImageViewerMediaKind.Image;
        if (_hostedLifetime || _hostCommandDispatcher != null || data == null)
            return false;

        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
                || data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] { Length: 1 } files)
                return false;

            string droppedPath = files[0];
            if (string.IsNullOrWhiteSpace(droppedPath) || Directory.Exists(droppedPath) || !File.Exists(droppedPath))
                return false;

            string fullPath = Path.GetFullPath(droppedPath);
            if (ImagePreviewService.IsSupportedExtension(fullPath))
            {
                kind = ImageViewerMediaKind.Image;
                path = fullPath;
                return true;
            }
            if (VideoMediaCatalog.IsSupportedExtension(fullPath)
                || VideoMediaCatalog.IsMpegTransportStreamFile(fullPath))
            {
                kind = ImageViewerMediaKind.Video;
                path = fullPath;
                return true;
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }

        return false;
    }

    private void ImageViewerForm_MouseWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == 0)
        {
            return;
        }
        if (e.Delta > 0)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerZoomIn);
        }
        else if (e.Delta < 0)
        {
            DispatchViewerCommand(ImageViewerCommandIds.ImageViewerZoomOut);
        }
    }

    private void CopyImageToClipboard()
    {
        try
        {
            if (TryGetSelectionImage(out Bitmap? selectionBitmap) && selectionBitmap != null)
            {
                using (selectionBitmap)
                {
                    SetImageClipboard(selectionBitmap);
                }
                return;
            }

            if (pictureBox1.Image != null)
            {
                SetImageClipboard(pictureBox1.Image);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"コピー失敗: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PasteImageFromClipboard()
    {
        try
        {
            using Bitmap? img = ReadImageClipboard();
            if (img == null)
            {
                MessageBox.Show("Clipboard does not contain an image.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ClearDecoderStatus();
            _loadRequestId++;
            PushUndoState("貼り付け前");
            ResetDisplayTransformState();
            SetDisplayImage((Bitmap)img.Clone());
            _currentPath = null;
            _imageSourceKind = ImageSourceKind.Clipboard;
            Text = "Clipboard Image - MidFD Image Viewer";
            ApplyInitialZoom(img);
            statusLabel.Text = "貼り付け";
            ClearRedoStack();
            UpdatePresentationModeUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to paste image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void SetImageClipboard(Image image)
    {
        using var png = new MemoryStream();
        image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        png.Position = 0;
        var data = new DataObject();
        data.SetData("PNG", false, png);
        data.SetData(DataFormats.Bitmap, true, image);
        Clipboard.SetDataObject(data, true);
    }

    private static Bitmap? ReadImageClipboard()
    {
        try
        {
            using MemoryStream? png = Clipboard.GetDataObject()?.GetData("PNG", false) switch
            {
                MemoryStream stream => new MemoryStream(stream.ToArray()),
                byte[] bytes => new MemoryStream(bytes),
                _ => null
            };
            if (png != null)
            {
                using var image = Image.FromStream(png);
                return image is Bitmap bitmap ? (Bitmap)bitmap.Clone() : new Bitmap(image);
            }
        }
        catch (ArgumentException)
        {
            // A malformed optional PNG must not hide an otherwise usable Bitmap.
        }
        using Image? fallback = Clipboard.GetImage();
        return fallback is Bitmap fallbackBitmap ? (Bitmap)fallbackBitmap.Clone()
            : fallback != null ? new Bitmap(fallback) : null;
    }

    private bool HasValidPasteSelection()
        => _imageSourceKind is ImageSourceKind.File or ImageSourceKind.Clipboard
            && _displayImage != null
            && _selectionClientRectangle is { } selection
            && ClientSelectionRectToImageRect(selection) is { Width: > 0, Height: > 0 };

    private async void PasteImageIntoSelectionFromClipboard(bool originalSize = false)
    {
        if (_imageSourceKind == ImageSourceKind.VideoPlayback)
        {
            return;
        }
        if (_displayImage == null)
        {
            statusLabel.Text = "貼り付け先の画像がありません。";
            return;
        }
        if (_selectionClientRectangle is not { } clientSelection)
        {
            statusLabel.Text = "貼り付け先の範囲を選択してください。";
            return;
        }

        Rectangle target = ClientSelectionRectToImageRect(clientSelection);
        if (target.Width <= 0 || target.Height <= 0)
        {
            statusLabel.Text = "貼り付け先の範囲がありません。";
            return;
        }

        try
        {
            using Bitmap? clipboardBitmap = ReadImageClipboard();
            if (clipboardBitmap == null)
            {
                statusLabel.Text = "クリップボードに画像がありません。";
                return;
            }
            await ApplyImageEditAsync(
                "選択範囲へ貼り付ける前",
                source => originalSize
                    ? ImageEditService.ReplaceSelectionOriginalSize(source, clipboardBitmap, target)
                    : ImageEditService.ReplaceSelectionStretch(source, clipboardBitmap, target),
                preserveView: true);
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"選択範囲への貼り付けに失敗しました: {ex.Message}";
        }
    }

    private void ExecuteOpenImageFromFile()
    {
        string imagePattern = string.Join(';', ImagePreviewService.SupportedExtensions.Select(extension => "*" + extension));
        string videoPattern = VideoMediaCatalog.BuildFilterPattern();
        string mediaPattern = string.Join(';', ImagePreviewService.SupportedExtensions
            .Concat(VideoMediaCatalog.OpenDialogExtensions)
            .Select(extension => "*" + extension));
        using var dialog = new OpenFileDialog
        {
            Filter = $"メディアファイル|{mediaPattern}|画像ファイル|{imagePattern}|動画ファイル|{videoPattern}|すべてのファイル|*.*",
            InitialDirectory = GetPreferredDirectory(),
            Title = "メディアを開く"
        };
        if ((ShowOpenMediaDialogOverride?.Invoke(dialog) ?? dialog.ShowDialog(this)) == DialogResult.OK)
        {
            string selectedPath = dialog.FileName;
            bool isImage = ImagePreviewService.IsSupportedExtension(selectedPath);
            bool isVideo = VideoMediaCatalog.IsSupportedExtension(selectedPath)
                || VideoMediaCatalog.IsMpegTransportStreamFile(selectedPath);
            if (isImage)
            {
                LoadImage(selectedPath);
            }
            else if (isVideo)
            {
                LoadVideoPlayback(
                    selectedPath,
                    _previewSettings.VideoSkipSeconds,
                    _previewSettings.VideoPlaybackVolumePercent);
            }
            else
            {
                MessageBox.Show(this, "選択したファイルは画像または対応動画として開けません。", "メディアを開く", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    private void ExecuteSaveImageAs()
    {
        if (pictureBox1.Image == null)
        {
            MessageBox.Show("保存する画像がありません。", "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string initialFileName = _currentPath != null ? Path.GetFileName(_currentPath) : "image.png";
        using var dialog = new SaveFileDialog
        {
            Filter = "PNG 画像|*.png|JPEG 画像|*.jpg;*.jpeg|BMP 画像|*.bmp|GIF 画像|*.gif",
            FileName = initialFileName,
            InitialDirectory = GetPreferredDirectory(),
            Title = "名前をつけて保存"
        };
        if ((ShowSaveImageDialogOverride?.Invoke(dialog) ?? dialog.ShowDialog(this)) != DialogResult.OK)
        {
            return;
        }
        var format = dialog.FilterIndex switch
        {
            2 => System.Drawing.Imaging.ImageFormat.Jpeg,
            3 => System.Drawing.Imaging.ImageFormat.Bmp,
            4 => System.Drawing.Imaging.ImageFormat.Gif,
            _ => System.Drawing.Imaging.ImageFormat.Png
        };
        pictureBox1.Image.Save(dialog.FileName, format);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<SaveFileDialog, DialogResult>? ShowSaveVideoFrameDialogOverride { get; set; }

    private async void ExecuteSaveCurrentVideoFrame()
    {
        if (_imageSourceKind != ImageSourceKind.VideoPlayback || !_embeddedVideoPlayer.HasMedia)
        {
            ShowVideoFrameCaptureStatus("保存できる動画フレームがありません。");
            return;
        }

        string? mediaPath = _currentPath;
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            ShowVideoFrameCaptureStatus("現在の動画パスを取得できません。");
            return;
        }

        long mediaGeneration = _imageGeneration;
        long position = _embeddedVideoPlayer.PositionMilliseconds;
        TimeSpan timestamp = TimeSpan.FromMilliseconds(position);
        string timestampText = $"{(int)timestamp.TotalHours:00}-{timestamp.Minutes:00}-{timestamp.Seconds:00}-{timestamp.Milliseconds:000}";
        string baseName = Path.GetFileNameWithoutExtension(mediaPath);
        using var dialog = new SaveFileDialog
        {
            Filter = "PNG 画像|*.png",
            DefaultExt = "png",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            FileName = $"{baseName}_{timestampText}.png",
            InitialDirectory = GetPreferredDirectory(),
            Title = "現在のフレームを保存"
        };
        if ((ShowSaveVideoFrameDialogOverride?.Invoke(dialog) ?? dialog.ShowDialog(this)) != DialogResult.OK)
            return;

        string targetPath;
        try { targetPath = Path.GetFullPath(dialog.FileName); }
        catch (Exception error) { ShowVideoFrameCaptureStatus($"保存先が正しくありません: {error.Message}"); return; }
        if (!string.Equals(Path.GetExtension(targetPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            ShowVideoFrameCaptureStatus("PNG形式で保存するため、拡張子 .png を指定してください。");
            return;
        }
        if (string.Equals(targetPath, Path.GetFullPath(mediaPath), StringComparison.OrdinalIgnoreCase))
        {
            ShowVideoFrameCaptureStatus("動画ファイル自体を上書きできません。");
            return;
        }

        string? directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            ShowVideoFrameCaptureStatus("保存先ディレクトリを取得できません。");
            return;
        }
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.tmp.png");
        try
        {
            VideoFrameCaptureResult capture = await _embeddedVideoPlayer.CaptureCurrentFrameAsync(temporaryPath, CancellationToken.None);
            if (!capture.Succeeded)
            {
                ShowVideoFrameCaptureStatus(capture.Error ?? "フレームを取得できませんでした。");
                return;
            }
            if (mediaGeneration != _imageGeneration
                || !string.Equals(mediaPath, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                ShowVideoFrameCaptureStatus("保存中に動画が切り替わったため、フレームを破棄しました。");
                return;
            }

            using (Image image = Image.FromFile(temporaryPath))
            {
                if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Png.Guid || image.Width <= 0 || image.Height <= 0)
                    throw new InvalidDataException("動画再生側の出力は有効なPNGフレームではありません。");
            }
            File.Move(temporaryPath, targetPath, overwrite: true);
            ShowVideoFrameCaptureStatus($"フレームをPNG保存しました: {Path.GetFileName(targetPath)}");
        }
        catch (Exception error)
        {
            ShowVideoFrameCaptureStatus($"フレーム保存に失敗しました: {error.Message}");
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private void ShowVideoFrameCaptureStatus(string message)
    {
        if (_imageSourceKind == ImageSourceKind.VideoPlayback)
            _embeddedVideoPlayer.SetStatusMessage(message);
        else
            statusLabel.Text = message;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<SaveFileDialog, DialogResult>? ShowSaveImageDialogOverride { get; set; }

    private string GetPreferredDirectory()
    {
        if (!string.IsNullOrEmpty(_currentPath))
        {
            string? directory = Path.GetDirectoryName(_currentPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    private void menuClose_Click(object sender, EventArgs e) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerClose);
    private void menuOpen_Click(object sender, EventArgs e) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerOpen);
    private void menuSaveAs_Click(object sender, EventArgs e) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerSaveAs);
    private void menuCopy_Click(object sender, EventArgs e) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerCopy);
    private void menuPaste_Click(object sender, EventArgs e) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerPaste);
    private void menuImageInfo_Click(object? sender, EventArgs e) => DispatchViewerCommand(ImageViewerCommandIds.ImageViewerShowInfo);

    private void ShowImageInformationDialog()
    {
        if (_displayImage == null && _originalImage == null)
        {
            MessageBox.Show("表示中の画像がありません。", "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var info = BuildImageViewerInfo();
        string message =
            $"種別: {info.SourceKind}{Environment.NewLine}" +
            $"ファイル名: {info.FileName}{Environment.NewLine}" +
            $"フルパス: {info.FullPath}{Environment.NewLine}" +
            $"ファイルサイズ: {info.FileSizeText}{Environment.NewLine}" +
            $"更新日時: {info.LastWriteTimeText}{Environment.NewLine}" +
            $"画像形式: {info.ImageFormatText}{Environment.NewLine}" +
            $"画像サイズ: {info.OriginalSizeText}{Environment.NewLine}" +
            $"表示中画像サイズ: {info.DisplaySizeText}{Environment.NewLine}" +
            $"PixelFormat: {info.PixelFormatText}{Environment.NewLine}" +
            $"DPI: {info.DpiText}";

        using var dialog = new Form
        {
            Text = "画像情報",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
            Width = 720,
            Height = 440,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var infoTextBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = message
        };

        var closeButton = new Button
        {
            Text = "閉じる",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Width = 96,
            Height = 30
        };

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 48
        };
        bottomPanel.Controls.Add(closeButton);
        bottomPanel.Resize += (_, _) =>
        {
            closeButton.Left = bottomPanel.Width - closeButton.Width - 12;
            closeButton.Top = 9;
        };
        closeButton.Left = bottomPanel.Width - closeButton.Width - 12;
        closeButton.Top = 9;

        dialog.AcceptButton = closeButton;
        dialog.CancelButton = closeButton;
        dialog.Controls.Add(infoTextBox);
        dialog.Controls.Add(bottomPanel);
        dialog.ShowDialog(this);
    }

    private ImageViewerInfo BuildImageViewerInfo()
    {
        string? sourcePath = GetInfoSourcePath();

        string fileName = "-";
        string fullPath = "-";
        string fileSize = "-";
        string lastWrite = "-";

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            fileName = Path.GetFileName(sourcePath);
            fullPath = sourcePath;
            try
            {
                if (File.Exists(sourcePath))
                {
                    var fileInfo = new FileInfo(sourcePath);
                    fileSize = FormatFileSize(fileInfo.Length);
                    lastWrite = fileInfo.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss");
                }
            }
            catch
            {
                fileSize = "-";
                lastWrite = "-";
            }
        }
        else if (_imageSourceKind == ImageSourceKind.Clipboard)
        {
            fileName = "(クリップボード画像)";
        }

        string videoContainer = "-";
        string videoContainerDetail = "-";
        string videoContainerIdentifier = "-";
        string videoCodec = "-";
        string audioCodec = "-";
        string videoDuration = "-";
        string videoResolution = "-";
        string videoFrameRate = "-";
        string videoBitRate = "-";

        return new ImageViewerInfo
        {
            SourceKind = GetSourceKindText(),
            FileName = fileName,
            FullPath = fullPath,
            FileSizeText = fileSize,
            LastWriteTimeText = lastWrite,
            ImageFormatText = GetImageFormatText(sourcePath),
            OriginalSizeText = _originalImage != null ? $"{_originalImage.Width} x {_originalImage.Height} px" : "-",
            DisplaySizeText = _displayImage != null ? $"{_displayImage.Width} x {_displayImage.Height} px" : "-",
            PixelFormatText = _displayImage?.PixelFormat.ToString() ?? _originalImage?.PixelFormat.ToString() ?? "-",
            DpiText = _displayImage != null
                ? $"{_displayImage.HorizontalResolution:0.##} x {_displayImage.VerticalResolution:0.##}"
                : _originalImage != null
                    ? $"{_originalImage.HorizontalResolution:0.##} x {_originalImage.VerticalResolution:0.##}"
                    : "-",
            VideoContainerText = videoContainer,
            VideoContainerDetailText = videoContainerDetail,
            VideoContainerIdentifierText = videoContainerIdentifier,
            VideoCodecText = videoCodec,
            AudioCodecText = audioCodec,
            VideoDurationText = videoDuration,
            VideoResolutionText = videoResolution,
            VideoFrameRateText = videoFrameRate,
            VideoBitRateText = videoBitRate
        };
    }

    private string GetSourceKindText()
    {
        return _imageSourceKind switch
        {
            ImageSourceKind.File => "画像ファイル",
            ImageSourceKind.VideoPlayback => "動画再生",
            ImageSourceKind.Clipboard => "クリップボード画像",
            _ => "不明"
        };
    }

    private string? GetInfoSourcePath()
    {
        if (_imageSourceKind == ImageSourceKind.File)
        {
            return _currentPath;
        }

        if (_imageSourceKind == ImageSourceKind.VideoPlayback)
        {
            return _currentPath;
        }

        return null;
    }

    private string GetImageFormatText(string? sourcePath)
    {
        if (_imageSourceKind == ImageSourceKind.VideoPlayback)
        {
            return "動画再生";
        }

        if (_imageSourceKind == ImageSourceKind.Clipboard)
        {
            return "Clipboard Bitmap";
        }

        string? ext = Path.GetExtension(sourcePath ?? _currentPath)?.ToLowerInvariant();
        return ext switch
        {
            ".png" => "PNG",
            ".jpg" => "JPEG",
            ".jpeg" => "JPEG",
            ".bmp" => "BMP",
            ".gif" => "GIF",
            ".tif" => "TIFF",
            ".tiff" => "TIFF",
            ".ico" => "Icon",
            ".svg" => "SVG",
            _ => "不明"
        };
    }

    private static string ResolveContainerLabelFromPath(string? sourcePath, string? formatLongName, string? formatName)
    {
        string? ext = Path.GetExtension(sourcePath)?.ToLowerInvariant();
        string container = ext switch
        {
            ".mp4" => "MP4",
            ".mov" => "MOV",
            ".m4v" => "M4V",
            ".mkv" => "Matroska / MKV",
            ".webm" => "WebM",
            ".avi" => "AVI",
            ".wmv" => "WMV / ASF",
            ".asf" => "ASF",
            ".ts" => "MPEG-TS",
            ".m2ts" => "MPEG-TS",
            ".mpg" => "MPEG-PS",
            ".mpeg" => "MPEG-PS",
            _ => "-"
        };

        if (container != "-")
        {
            return container;
        }

        if (!string.IsNullOrWhiteSpace(formatLongName))
        {
            return formatLongName;
        }

        return string.IsNullOrWhiteSpace(formatName) ? "-" : formatName;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024.0 && unitIndex < units.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} B"
            : $"{value:0.##} {units[unitIndex]}";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
        {
            return "-";
        }

        TimeSpan ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    private static string FormatBitRate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "-";
        }

        double mbps = bitsPerSecond / 1_000_000d;
        if (mbps >= 1d)
        {
            return $"{mbps:0.##} Mbps";
        }

        double kbps = bitsPerSecond / 1_000d;
        return $"{kbps:0.##} kbps";
    }

    private void ApplyInitialZoom(Image image)
    {
        int fitW = Math.Max(1, _previewSettings.InitialFitLimitWidth);
        int fitH = Math.Max(1, _previewSettings.InitialFitLimitHeight);
        float zoom = 1.0f;
        if (image.Width > fitW || image.Height > fitH)
        {
            float scaleX = (float)fitW / image.Width;
            float scaleY = (float)fitH / image.Height;
            zoom = Math.Min(scaleX, scaleY);
        }
        SetZoom(zoom);
    }

    private void SetZoom(float zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        UpdateScaledImageBounds();
        UpdateZoomStatus();
        pictureBox1.Invalidate();
    }

    private void UpdateZoomStatus()
    {
        statusLabel.Text = $"倍率 {(int)Math.Round(_zoom * 100)}%";
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyModernShellTheme();
        CenterLoadingLabel();
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplyModernShellTheme();
    }

    private void ApplyModernShellTheme()
    {
        if (_embeddedVideoPlayer is null || menuStrip1 is null || statusStrip1 is null
            || _loadingLabel is null)
        {
            return;
        }

        _shellPalette = EmbeddedVideoPlayerControl.GetChromePalette(SystemInformation.HighContrast);
        if (_shellFont == null)
        {
            _shellFont = new Font(
                _embeddedVideoPlayer.Font.FontFamily,
                _embeddedVideoPlayer.Font.SizeInPoints,
                _embeddedVideoPlayer.Font.Style,
                GraphicsUnit.Point);
        }

        Font = _shellFont;
        menuStrip1.Font = _shellFont;
        menuStrip1.Padding = new Padding(ScaleShellLogical(6), ScaleShellLogical(2), ScaleShellLogical(6), ScaleShellLogical(2));
        statusStrip1.Font = _shellFont;
        imageScrollPanel.Font = _shellFont;
        pictureBox1.Font = _shellFont;
        BackColor = _shellPalette.Background;
        imageScrollPanel.BackColor = _shellPalette.Background;
        pictureBox1.BackColor = _shellPalette.Background;
        menuStrip1.BackColor = _shellPalette.Background;
        menuStrip1.ForeColor = _shellPalette.PrimaryText;
        menuStrip1.RenderMode = ToolStripRenderMode.Professional;
        menuStrip1.Renderer = new ToolStripProfessionalRenderer(
            new ImageViewerProfessionalColorTable(_shellPalette));
        ApplyToolStripItemTheme(menuStrip1.Items);
        statusStrip1.BackColor = _shellPalette.SecondaryBackground;
        statusStrip1.ForeColor = _shellPalette.PrimaryText;
        statusLabel.BackColor = _shellPalette.SecondaryBackground;
        statusLabel.ForeColor = _shellPalette.PrimaryText;
        decoderStatusLabel.BackColor = _shellPalette.SecondaryBackground;
        decoderStatusLabel.ForeColor = _shellPalette.SecondaryText;
        _imagePresentationLockButton.BackColor = _shellPalette.SecondaryBackground;
        _imagePresentationLockButton.ForeColor = _shellPalette.PrimaryText;
        _imagePresentationLockButton.Font = _shellFont;
        _loadingLabel.BackColor = _shellPalette.SecondaryBackground;
        _loadingLabel.ForeColor = _shellPalette.PrimaryText;
        _loadingLabel.Font = _shellFont;
        pictureBox1.Invalidate();
    }

    private int ScaleShellLogical(int value)
        => Math.Max(1, (int)Math.Round(value * Math.Max(1, DeviceDpi) / 96d));

    private void ApplyToolStripItemTheme(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.ForeColor = _shellPalette.PrimaryText;
            item.BackColor = _shellPalette.Background;
            if (item is ToolStripDropDownItem dropDown)
            {
                dropDown.DropDown.BackColor = _shellPalette.SecondaryBackground;
                ApplyToolStripItemTheme(dropDown.DropDownItems);
            }
        }
    }

    private sealed class ImageViewerProfessionalColorTable(
        (Color Background, Color SecondaryBackground, Color PrimaryText, Color SecondaryText, Color Separator, Color Accent, Color DisabledText, bool HighContrast) palette)
        : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => palette.Background;
        public override Color MenuStripGradientEnd => palette.Background;
        public override Color MenuItemSelected => palette.SecondaryBackground;
        public override Color MenuItemSelectedGradientBegin => palette.SecondaryBackground;
        public override Color MenuItemSelectedGradientEnd => palette.SecondaryBackground;
        public override Color MenuItemPressedGradientBegin => palette.SecondaryBackground;
        public override Color MenuItemPressedGradientMiddle => palette.SecondaryBackground;
        public override Color MenuItemPressedGradientEnd => palette.SecondaryBackground;
        public override Color MenuItemBorder => palette.Separator;
        public override Color MenuBorder => palette.Separator;
        public override Color ToolStripDropDownBackground => palette.SecondaryBackground;
        public override Color ImageMarginGradientBegin => palette.SecondaryBackground;
        public override Color ImageMarginGradientMiddle => palette.SecondaryBackground;
        public override Color ImageMarginGradientEnd => palette.SecondaryBackground;
        public override Color SeparatorDark => palette.Separator;
        public override Color SeparatorLight => palette.SecondaryBackground;
        public override Color ToolStripBorder => palette.Separator;
        public override Color ToolStripGradientBegin => palette.Background;
        public override Color ToolStripGradientMiddle => palette.Background;
        public override Color ToolStripGradientEnd => palette.Background;
    }

    private void UpdateScaledImageBounds()
    {
        if (pictureBox1.Image == null)
        {
            return;
        }
        int width = Math.Max(1, (int)Math.Round(pictureBox1.Image.Width * _zoom));
        int height = Math.Max(1, (int)Math.Round(pictureBox1.Image.Height * _zoom));
        pictureBox1.Size = new Size(width, height);
        AdjustWindowSizeToImage(width, height);
        pictureBox1.Invalidate();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }
        _savedBounds = Bounds;
        _savedBorderStyle = FormBorderStyle;
        _savedWindowState = WindowState;
        _savedTopMost = TopMost;
        _isFullscreen = true;
        TopMost = true;
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        Bounds = Screen.FromControl(this).Bounds;
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }
        _isFullscreen = false;
        FormBorderStyle = _savedBorderStyle;
        TopMost = _savedTopMost;
        WindowState = FormWindowState.Normal;
        Bounds = _savedBounds;
        WindowState = _savedWindowState;
    }

    private void AdjustWindowSizeToImage(int imageWidth, int imageHeight)
    {
        if (_isFullscreen)
        {
            return;
        }
        Rectangle workArea = Screen.FromControl(this).WorkingArea;
        ApplyMediaWindowSize(
            new Size(imageWidth, imageHeight),
            GetImageWindowChromeHeight(),
            workArea,
            minimumAvailableSurfaceSize: 200);
    }

    private void ApplyInitialVideoWindowSize(string videoPath, Size displaySize)
        => HandleInitialVideoWindowSize(videoPath, displaySize, _imageGeneration);

    private void HandleInitialVideoWindowSize(string videoPath, Size displaySize, int mediaGeneration)
    {
        if (IsDisposed
            || Disposing
            || mediaGeneration != _imageGeneration
            || _imageSourceKind != ImageSourceKind.VideoPlayback
            || !string.Equals(_currentPath, videoPath, StringComparison.OrdinalIgnoreCase)
            || _videoInitialWindowSizeApplied
            || _videoManuallyResizedBeforeDimensions
            || _isFullscreen
            || WindowState != FormWindowState.Normal)
        {
            return;
        }

        Rectangle workArea = Screen.FromControl(this).WorkingArea;
        int fixedClientHeight = GetVideoWindowChromeHeight();
        Size availableSurface = GetAvailableMediaSurfaceSize(
            new Size(
                Math.Max(1, _previewSettings.InitialFitLimitWidth),
                Math.Max(1, _previewSettings.InitialFitLimitHeight)),
            fixedClientHeight,
            workArea);
        Size fittedVideoSize = FitVideoDisplaySize(displaySize, availableSurface);
        if (fittedVideoSize.IsEmpty)
        {
            return;
        }

        _videoInitialWindowSizeApplied = true;
        ApplyMediaWindowSize(fittedVideoSize, fixedClientHeight, workArea);
    }

    private int GetImageWindowChromeHeight()
        => (menuStrip1.Visible ? menuStrip1.Height : 0)
           + (statusStrip1.Visible ? statusStrip1.Height : 0);

    private int GetVideoWindowChromeHeight()
        => (menuStrip1.Visible ? menuStrip1.Height : 0)
           + (statusStrip1.Visible ? statusStrip1.Height : 0)
           + _embeddedVideoPlayer.PlaybackControlsHeight;

    private Size GetAvailableMediaSurfaceSize(Size requestedLimit, int fixedChromeHeight, Rectangle workArea)
    {
        int nonClientWidth = Math.Max(0, Width - ClientSize.Width);
        int nonClientHeight = Math.Max(0, Height - ClientSize.Height);
        int maxSurfaceWidth = Math.Max(1, workArea.Width - nonClientWidth);
        int maxSurfaceHeight = Math.Max(1, workArea.Height - nonClientHeight - fixedChromeHeight);
        return new Size(
            Math.Min(Math.Max(1, requestedLimit.Width), maxSurfaceWidth),
            Math.Min(Math.Max(1, requestedLimit.Height), maxSurfaceHeight));
    }

    private void ApplyMediaWindowSize(Size mediaSurfaceSize, int fixedChromeHeight, Rectangle workArea, int minimumAvailableSurfaceSize = 1)
    {
        int nonClientWidth = Math.Max(0, Width - ClientSize.Width);
        int nonClientHeight = Math.Max(0, Height - ClientSize.Height);
        int maxClientWidth = Math.Max(minimumAvailableSurfaceSize, workArea.Width - nonClientWidth);
        int maxClientHeight = Math.Max(minimumAvailableSurfaceSize, workArea.Height - nonClientHeight);
        int maxSurfaceWidth = maxClientWidth;
        int maxSurfaceHeight = Math.Max(1, maxClientHeight - fixedChromeHeight);
        int clientWidth = Math.Min(Math.Max(1, mediaSurfaceSize.Width), maxSurfaceWidth);
        int surfaceHeight = Math.Min(Math.Max(1, mediaSurfaceSize.Height), maxSurfaceHeight);
        // Clamp before assigning ClientSize. Letting the native minimum clamp the
        // outer bounds can leave docked chrome laid out at the smaller request.
        int minClientWidth = Math.Min(maxClientWidth, Math.Max(1, MinimumSize.Width - nonClientWidth));
        int minClientHeight = Math.Min(maxClientHeight, Math.Max(1, MinimumSize.Height - nonClientHeight));
        ClientSize = new Size(Math.Max(clientWidth, minClientWidth), Math.Max(surfaceHeight + fixedChromeHeight, minClientHeight));
        EnsureWindowInWorkArea(workArea);
    }

    private void HandlePlaybackDetailsExpandedChanged(bool expanded, int requestedHeight)
    {
        if (IsDisposed || Disposing || _isFullscreen || WindowState != FormWindowState.Normal)
        {
            return;
        }

        if (expanded)
        {
            _detailsExpansionHeightApplied = 0;
            _detailsExpandedWindowHeight = null;
            _detailsExpandedWindowLocation = null;
            Rectangle workArea = Screen.FromControl(this).WorkingArea;
            if (workArea.Bottom - Bottom < requestedHeight)
            {
                return;
            }

            Height += requestedHeight;
            _detailsExpansionHeightApplied = requestedHeight;
            _detailsExpandedWindowHeight = Height;
            _detailsExpandedWindowLocation = Location;
            return;
        }

        int expansionHeight = _detailsExpansionHeightApplied;
        bool stillOwnsExpansion = expansionHeight > 0
            && _detailsExpandedWindowHeight == Height
            && _detailsExpandedWindowLocation == Location;
        _detailsExpansionHeightApplied = 0;
        _detailsExpandedWindowHeight = null;
        _detailsExpandedWindowLocation = null;
        if (stillOwnsExpansion)
        {
            Height = Math.Max(MinimumSize.Height, Height - expansionHeight);
        }
    }

    internal static Size FitVideoDisplaySize(Size displaySize, Size fitLimit)
    {
        if (displaySize.Width <= 0
            || displaySize.Height <= 0
            || fitLimit.Width <= 0
            || fitLimit.Height <= 0)
        {
            return Size.Empty;
        }

        double scale = Math.Min(
            1d,
            Math.Min(
                fitLimit.Width / (double)displaySize.Width,
                fitLimit.Height / (double)displaySize.Height));
        int width = Math.Max(1, (int)Math.Round(displaySize.Width * scale, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(displaySize.Height * scale, MidpointRounding.AwayFromZero));
        return new Size(width, height);
    }

    private void ImageViewerForm_ResizeEnd(object? sender, EventArgs e)
    {
        if (_imageSourceKind == ImageSourceKind.VideoPlayback && !_videoInitialWindowSizeApplied)
        {
            _videoManuallyResizedBeforeDimensions = true;
        }
    }

    private void EnsureWindowInWorkArea(Rectangle workArea)
    {
        int x = Left;
        int y = Top;
        if (x < workArea.Left) x = workArea.Left;
        if (y < workArea.Top) y = workArea.Top;
        if (x + Width > workArea.Right) x = workArea.Right - Width;
        if (y + Height > workArea.Bottom) y = workArea.Bottom - Height;
        Location = new Point(x, y);
    }

    private void ImageViewerForm_FormClosed(object? sender, FormClosedEventArgs e)
    {
        _imageDecodeCts?.Cancel();
        _mediaLoadCts?.Cancel();
        _susieDecoderService.Dispose();
        _embeddedVideoPlayer.StopPlayback();
        Interlocked.Increment(ref _tonePreviewSequence);
        pictureBox1.Image = null;
        _tonePreviewImage?.Dispose();
        _tonePreviewImage = null;
        DisposeImage(_displayImage);
        DisposeImage(_originalImage);
        _displayImage = null;
        _originalImage = null;
        ClearHistory();
    }

    private void pictureBox1_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _displayImage == null)
        {
            return;
        }

        Rectangle displayedImageRect = GetDisplayedImageRectangle();
        if (!displayedImageRect.Contains(e.Location))
        {
            return;
        }

        pictureBox1.Focus();
        if (_selectionClientRectangle is { } existingSelection
            && TryGetSelectionResizeHandle(existingSelection, e.Location, 6, out SelectionResizeHandle resizeHandle))
        {
            _isSelectingRectangle = true;
            _isResizingSelection = true;
            _selectionResizeHandle = resizeHandle;
            _selectionResizeStartPoint = ClampPointToDisplayedImage(e.Location);
            _selectionResizeStartRectangle = existingSelection;
            pictureBox1.Cursor = GetSelectionResizeCursor(resizeHandle);
            return;
        }

        _isSelectingRectangle = true;
        _isResizingSelection = false;
        _selectionStartClientPoint = ClampPointToDisplayedImage(e.Location);
        _selectionCurrentClientPoint = _selectionStartClientPoint;
        _selectionClientRectangle = null;
        pictureBox1.Invalidate();
    }

    private void pictureBox1_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_isSelectingRectangle)
        {
            UpdateSelectionResizeCursor(e.Location);
            return;
        }

        if (_isResizingSelection)
        {
            UpdateSelectionResize(e.Location);
            pictureBox1.Invalidate();
            return;
        }

        _selectionCurrentClientPoint = ClampPointToDisplayedImage(e.Location);
        Rectangle selectionRect = CreateNormalizedRectangle(_selectionStartClientPoint, _selectionCurrentClientPoint);
        _selectionClientRectangle = selectionRect.Width < 1 || selectionRect.Height < 1
            ? null
            : selectionRect;
        pictureBox1.Invalidate();
    }

    private void pictureBox1_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!_isSelectingRectangle)
        {
            return;
        }

        _isSelectingRectangle = false;
        if (_isResizingSelection)
        {
            UpdateSelectionResize(e.Location);
            _isResizingSelection = false;
            UpdateSelectionResizeCursor(e.Location);
            pictureBox1.Invalidate();
            return;
        }

        _selectionCurrentClientPoint = ClampPointToDisplayedImage(e.Location);
        Rectangle selectionRect = CreateNormalizedRectangle(_selectionStartClientPoint, _selectionCurrentClientPoint);
        _selectionClientRectangle = selectionRect.Width < 2 || selectionRect.Height < 2
            ? null
            : selectionRect;
        pictureBox1.Invalidate();
    }

    private void pictureBox1_Paint(object? sender, PaintEventArgs e)
    {
        if (_pendingFirstPaintRequestId != 0 && _displayImage != null)
        {
            long requestId = _pendingFirstPaintRequestId;
            _pendingFirstPaintRequestId = 0;
            string diagnostic =
                $"[MidPlayerTiming] stage=first-picture-paint request={requestId} " +
                $"elapsedMs={Stopwatch.GetElapsedTime(_pendingFirstPaintStartedAt).TotalMilliseconds:0.0}";
            LogService.Info(diagnostic);
            TimingDiagnosticProduced?.Invoke(diagnostic);
        }

        Rectangle? selectionRect = _selectionClientRectangle;
        if (selectionRect == null || _displayImage == null)
        {
            return;
        }

        Rectangle clientRect = selectionRect.Value;
        Color selectionAccent = _shellPalette.Accent.IsEmpty ? SystemColors.Highlight : _shellPalette.Accent;
        using var outlinePen = new Pen(selectionAccent, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        using var fillBrush = new SolidBrush(Color.FromArgb(40, selectionAccent));
        e.Graphics.FillRectangle(fillBrush, clientRect);
        e.Graphics.DrawRectangle(outlinePen, clientRect);

        Rectangle imageRect = ClientSelectionRectToImageRect(clientRect);
        if (imageRect.Width <= 0 || imageRect.Height <= 0)
        {
            return;
        }

        string sizeText = $"{imageRect.Width}x{imageRect.Height}";
        Size textSize = TextRenderer.MeasureText(e.Graphics, sizeText, Font, Size.Empty, TextFormatFlags.NoPadding);
        Rectangle labelRect = new Rectangle(clientRect.Left, clientRect.Top - textSize.Height - 6, textSize.Width + 8, textSize.Height + 4);
        Rectangle displayedImageRect = GetDisplayedImageRectangle();
        if (labelRect.Top < displayedImageRect.Top)
        {
            labelRect.Y = Math.Min(displayedImageRect.Bottom - labelRect.Height, clientRect.Bottom + 2);
        }
        if (labelRect.Right > displayedImageRect.Right)
        {
            labelRect.X = displayedImageRect.Right - labelRect.Width;
        }
        if (labelRect.Left < displayedImageRect.Left)
        {
            labelRect.X = displayedImageRect.Left;
        }

        using var labelBackBrush = new SolidBrush(_shellPalette.SecondaryBackground);
        e.Graphics.FillRectangle(labelBackBrush, labelRect);
        TextRenderer.DrawText(
            e.Graphics,
            sizeText,
            Font,
            labelRect,
            _shellPalette.PrimaryText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    internal event Action<string>? TimingDiagnosticProduced;

    private void ClearSelection()
    {
        _isSelectingRectangle = false;
        _isResizingSelection = false;
        _selectionClientRectangle = null;
        _selectionStartClientPoint = Point.Empty;
        _selectionCurrentClientPoint = Point.Empty;
        if (!IsDisposed)
        {
            pictureBox1.Invalidate();
        }
    }

    private Rectangle GetDisplayedImageRectangle()
    {
        if (_displayImage == null || pictureBox1.Width <= 0 || pictureBox1.Height <= 0)
        {
            return Rectangle.Empty;
        }

        return new Rectangle(0, 0, pictureBox1.ClientSize.Width, pictureBox1.ClientSize.Height);
    }

    private Point ClampPointToDisplayedImage(Point point)
    {
        Rectangle displayedImageRect = GetDisplayedImageRectangle();
        if (displayedImageRect.IsEmpty)
        {
            return Point.Empty;
        }

        int x = Math.Clamp(point.X, displayedImageRect.Left, displayedImageRect.Right - 1);
        int y = Math.Clamp(point.Y, displayedImageRect.Top, displayedImageRect.Bottom - 1);
        return new Point(x, y);
    }

    private Rectangle ClientSelectionRectToImageRect(Rectangle clientRect)
    {
        if (_displayImage == null)
        {
            return Rectangle.Empty;
        }

        return SelectionClientRectToImageRect(clientRect, GetDisplayedImageRectangle(), _displayImage.Size);
    }

    internal static bool TryGetSelectionResizeHandle(
        Rectangle selection,
        Point point,
        int hitTolerance,
        out SelectionResizeHandle resizeHandle)
    {
        resizeHandle = default;
        if (selection.Width < 2 || selection.Height < 2 || hitTolerance < 0)
        {
            return false;
        }

        int leftDistance = Math.Abs(point.X - selection.Left);
        int rightDistance = Math.Abs(point.X - selection.Right);
        int topDistance = Math.Abs(point.Y - selection.Top);
        int bottomDistance = Math.Abs(point.Y - selection.Bottom);
        bool horizontalEdgeHit = Math.Min(leftDistance, rightDistance) <= hitTolerance;
        bool verticalEdgeHit = Math.Min(topDistance, bottomDistance) <= hitTolerance;

        if (horizontalEdgeHit && verticalEdgeHit)
        {
            bool useLeft = leftDistance <= rightDistance;
            bool useTop = topDistance <= bottomDistance;
            resizeHandle = (useLeft, useTop) switch
            {
                (true, true) => SelectionResizeHandle.TopLeft,
                (false, true) => SelectionResizeHandle.TopRight,
                (true, false) => SelectionResizeHandle.BottomLeft,
                _ => SelectionResizeHandle.BottomRight
            };
            return true;
        }

        if (point.Y >= selection.Top && point.Y <= selection.Bottom && horizontalEdgeHit)
        {
            resizeHandle = leftDistance <= rightDistance
                ? SelectionResizeHandle.Left
                : SelectionResizeHandle.Right;
            return true;
        }

        if (point.X >= selection.Left && point.X <= selection.Right && verticalEdgeHit)
        {
            resizeHandle = topDistance <= bottomDistance
                ? SelectionResizeHandle.Top
                : SelectionResizeHandle.Bottom;
            return true;
        }

        return false;
    }

    internal static Rectangle ResizeSelection(
        Rectangle startingSelection,
        SelectionResizeHandle handle,
        Point pointerStart,
        Point pointer,
        Rectangle displayedImageBounds,
        int minimumWidth = 2,
        int minimumHeight = 2)
    {
        if (minimumWidth < 1 || minimumHeight < 1
            || displayedImageBounds.Width < minimumWidth || displayedImageBounds.Height < minimumHeight
            || startingSelection.Width < minimumWidth || startingSelection.Height < minimumHeight)
        {
            return startingSelection;
        }

        bool movesLeft = handle is SelectionResizeHandle.Left or SelectionResizeHandle.TopLeft or SelectionResizeHandle.BottomLeft;
        bool movesRight = handle is SelectionResizeHandle.Right or SelectionResizeHandle.TopRight or SelectionResizeHandle.BottomRight;
        bool movesTop = handle is SelectionResizeHandle.Top or SelectionResizeHandle.TopLeft or SelectionResizeHandle.TopRight;
        bool movesBottom = handle is SelectionResizeHandle.Bottom or SelectionResizeHandle.BottomLeft or SelectionResizeHandle.BottomRight;
        int left = startingSelection.Left;
        int right = startingSelection.Right;
        int top = startingSelection.Top;
        int bottom = startingSelection.Bottom;

        if (movesLeft || movesRight)
        {
            (left, right) = ResizeSelectionAxis(
                startingSelection.Left,
                startingSelection.Right,
                movesLeft,
                pointerStart.X,
                pointer.X,
                displayedImageBounds.Left,
                displayedImageBounds.Right,
                minimumWidth);
        }

        if (movesTop || movesBottom)
        {
            (top, bottom) = ResizeSelectionAxis(
                startingSelection.Top,
                startingSelection.Bottom,
                movesTop,
                pointerStart.Y,
                pointer.Y,
                displayedImageBounds.Top,
                displayedImageBounds.Bottom,
                minimumHeight);
        }

        if (right - left < minimumWidth || bottom - top < minimumHeight
            || left < displayedImageBounds.Left || top < displayedImageBounds.Top
            || right > displayedImageBounds.Right || bottom > displayedImageBounds.Bottom)
        {
            return startingSelection;
        }

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static (int Minimum, int Maximum) ResizeSelectionAxis(
        int startingMinimum,
        int startingMaximum,
        bool movesMinimum,
        int pointerStart,
        int pointer,
        int boundsMinimum,
        int boundsMaximum,
        int minimumSize)
    {
        int clampedPointer = Math.Clamp(pointer, boundsMinimum, boundsMaximum);
        if (Math.Abs(clampedPointer - pointerStart) < minimumSize)
        {
            return (startingMinimum, startingMaximum);
        }

        int fixedEdge = movesMinimum ? startingMaximum : startingMinimum;
        if (Math.Abs(clampedPointer - fixedEdge) < minimumSize)
        {
            bool keepMinimumSide = clampedPointer < fixedEdge
                || (clampedPointer == fixedEdge && movesMinimum);
            clampedPointer = keepMinimumSide
                ? Math.Max(boundsMinimum, fixedEdge - minimumSize)
                : Math.Min(boundsMaximum, fixedEdge + minimumSize);
        }

        if (Math.Abs(clampedPointer - fixedEdge) < minimumSize)
        {
            return (startingMinimum, startingMaximum);
        }

        return clampedPointer < fixedEdge
            ? (clampedPointer, fixedEdge)
            : (fixedEdge, clampedPointer);
    }

    internal static Rectangle SelectionClientRectToImageRect(Rectangle clientRect, Rectangle displayedImageRect, Size imageSize)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0 || displayedImageRect.Width <= 0 || displayedImageRect.Height <= 0)
        {
            return Rectangle.Empty;
        }

        Rectangle clampedClientRect = Rectangle.Intersect(displayedImageRect, clientRect);
        if (clampedClientRect.Width <= 0 || clampedClientRect.Height <= 0)
        {
            return Rectangle.Empty;
        }

        double scaleX = imageSize.Width / (double)displayedImageRect.Width;
        double scaleY = imageSize.Height / (double)displayedImageRect.Height;
        int left = (int)Math.Floor(clampedClientRect.Left * scaleX);
        int top = (int)Math.Floor(clampedClientRect.Top * scaleY);
        int right = (int)Math.Ceiling(clampedClientRect.Right * scaleX);
        int bottom = (int)Math.Ceiling(clampedClientRect.Bottom * scaleY);

        left = Math.Clamp(left, 0, imageSize.Width - 1);
        top = Math.Clamp(top, 0, imageSize.Height - 1);
        right = Math.Clamp(right, left + 1, imageSize.Width);
        bottom = Math.Clamp(bottom, top + 1, imageSize.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private void UpdateSelectionResize(Point point)
    {
        if (!_isResizingSelection) return;
        _selectionClientRectangle = ResizeSelection(
            _selectionResizeStartRectangle,
            _selectionResizeHandle,
            _selectionResizeStartPoint,
            point,
            GetDisplayedImageRectangle());
    }

    private void UpdateSelectionResizeCursor(Point point)
    {
        pictureBox1.Cursor = _selectionClientRectangle is { } selection
            && TryGetSelectionResizeHandle(selection, point, 6, out SelectionResizeHandle handle)
                ? GetSelectionResizeCursor(handle)
                : Cursors.Default;
    }

    private static Cursor GetSelectionResizeCursor(SelectionResizeHandle handle)
        => handle switch
        {
            SelectionResizeHandle.Left or SelectionResizeHandle.Right => Cursors.SizeWE,
            SelectionResizeHandle.Top or SelectionResizeHandle.Bottom => Cursors.SizeNS,
            SelectionResizeHandle.TopLeft or SelectionResizeHandle.BottomRight => Cursors.SizeNWSE,
            _ => Cursors.SizeNESW
        };

    private static Rectangle CreateNormalizedRectangle(Point start, Point end)
    {
        int left = Math.Min(start.X, end.X);
        int top = Math.Min(start.Y, end.Y);
        int right = Math.Max(start.X, end.X);
        int bottom = Math.Max(start.Y, end.Y);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private bool TryGetSelectionImage(out Bitmap? selectionBitmap)
    {
        selectionBitmap = null;
        if (_displayImage == null || _selectionClientRectangle == null)
        {
            return false;
        }

        Rectangle imageRect = ClientSelectionRectToImageRect(_selectionClientRectangle.Value);
        if (imageRect.Width <= 0 || imageRect.Height <= 0)
        {
            return false;
        }

        selectionBitmap = _displayImage.Clone(imageRect, _displayImage.PixelFormat);
        return true;
    }

    private static void DisposeImage(Image? image)
    {
        image?.Dispose();
    }

    private static void TrimHistoryStack(Stack<ImageHistoryEntry> stack)
    {
        if (stack.Count <= MaxHistoryCount)
        {
            return;
        }

        var array = stack.ToArray();
        for (int i = MaxHistoryCount; i < array.Length; i++)
        {
            array[i].Image.Dispose();
        }
        stack.Clear();
        for (int i = MaxHistoryCount - 1; i >= 0; i--)
        {
            stack.Push(array[i]);
        }
    }

    internal void SetVideoPlaybackBackend(string backend)
        => _previewSettings.VideoPlaybackBackend = MidFD.Configuration.VideoPlaybackBackendKind.Normalize(backend);

    public async void LoadVideoPlayback(
        string videoPath,
        int initialSeconds,
        int volumePercent,
        long? externalRequestId = null,
        bool isSelectionFollow = false,
        int? selectionGeneration = null,
        int? presentationGeneration = null)
    {
        if (isSelectionFollow && !TryAcceptSelectionFollow(selectionGeneration, presentationGeneration))
        {
            RejectMediaLoad(videoPath, externalRequestId);
            return;
        }
        int effectiveVolume = ResolveVideoPlaybackVolume(volumePercent);
        if (!isSelectionFollow) ClearDecoderStatus();
        _imageDecodeCts?.Cancel();
        long requestGeneration = ++_loadRequestId;
        int mediaGeneration = ++_imageGeneration;
        _pendingFirstPaintRequestId = 0;
        long responseRequestId = externalRequestId ?? requestGeneration;
        _activeLoadIsSelectionFollow = isSelectionFollow;
        _activeLoadPath = videoPath;
        _activeLoadResponseRequestId = responseRequestId;
        _mediaLoadCts?.Cancel();
        _mediaLoadCts?.Dispose();
        _mediaLoadCts = new CancellationTokenSource();
        CancellationToken token = _mediaLoadCts.Token;

        if (!isSelectionFollow)
        {
            SetVideoPlaybackRequest(responseRequestId);
            _videoInitialWindowSizeApplied = false;
            _videoManuallyResizedBeforeDimensions = false;
            _imageSourceKind = ImageSourceKind.VideoPlayback;
            statusStrip1.Visible = false;
            _currentPath = videoPath;
            Text = $"{Path.GetFileName(videoPath)} - MidFD Image Viewer";
            ClearSelection();

            _embeddedVideoPlayer.StopPlayback();
            UpdatePresentationModeUi();
            _embeddedVideoPlayer.Visible = false;
            pictureBox1.Visible = true;
            pictureBox1.Image = null;
            DisposeImage(_originalImage);
            DisposeImage(_displayImage);
            _originalImage = null;
            _displayImage = null;
            ClearHistory();

            statusLabel.Text = string.Empty;
            _loadingLabel.Visible = false;
            _embeddedVideoPlayer.SetStatusMessage("動画を読み込んでいます...");
        }
        _embeddedVideoPlayer.VideoPlaybackBackend = _previewSettings.VideoPlaybackBackend;
        if (!isSelectionFollow) SetQuantizeMenuEnabled(false);

        bool loaded = false;
        try
        {
            if (!Visible || !IsHandleCreated)
            {
                if (!isSelectionFollow) FailVideoLoadRequest(videoPath, externalRequestId, "MidPlayer表示前のため動画再生を開始できませんでした。");
                else
                {
                    _activeLoadIsSelectionFollow = false;
                    MediaLoadCompleted?.Invoke(videoPath, responseRequestId, false, true);
                    ReportCurrentVideoPlaybackState(force: true);
                }
                return;
            }
            if (isSelectionFollow)
            {
                if (!PrepareEmbeddedVideoSurfaceForSelectionFollow())
                {
                    _activeLoadIsSelectionFollow = false;
                    MediaLoadCompleted?.Invoke(videoPath, responseRequestId, false, false);
                    ReportCurrentVideoPlaybackState(force: true);
                    return;
                }
            }
            else if (!PrepareEmbeddedVideoSurface(requireVisibleHost: true))
            {
                FailVideoLoadRequest(videoPath, externalRequestId, "動画表示surfaceを準備できませんでした。");
                return;
            }
            loaded = await _embeddedVideoPlayer.LoadAsync(
                videoPath,
                initialSeconds,
                effectiveVolume,
                autoplay: true,
                cancellationToken: token,
                ownerMediaGeneration: mediaGeneration,
                preserveCurrentMedia: isSelectionFollow);
        }
        catch (Exception ex)
        {
            if (requestGeneration == _loadRequestId && !token.IsCancellationRequested && !IsDisposed)
            {
                LogService.Error($"[ImageViewer] Video playback load failed path='{videoPath}'", ex);
            }
        }

        bool requestStillCurrent = requestGeneration == _loadRequestId
            && !token.IsCancellationRequested
            && !IsDisposed
            && (!isSelectionFollow || IsSelectionFollowCurrent(selectionGeneration, presentationGeneration));
        if (requestStillCurrent)
        {
            if (isSelectionFollow)
            {
                loaded = loaded && _embeddedVideoPlayer.CommitPendingSelectionFollow(videoPath, effectiveVolume, mediaGeneration);
                if (loaded)
                {
                    _videoInitialWindowSizeApplied = false;
                    _videoManuallyResizedBeforeDimensions = false;
                    SetVideoPlaybackRequest(responseRequestId);
                    _imageSourceKind = ImageSourceKind.VideoPlayback;
                    UpdatePresentationModeUi();
                    ClearDecoderStatus();
                    statusStrip1.Visible = false;
                    _currentPath = videoPath;
                    Text = $"{Path.GetFileName(videoPath)} - MidFD Image Viewer";
                    ClearSelection();
                    pictureBox1.Image = null;
                    DisposeImage(_originalImage);
                    DisposeImage(_displayImage);
                    _originalImage = null;
                    _displayImage = null;
                    ClearHistory();
                    pictureBox1.Visible = false;
                    _embeddedVideoPlayer.Visible = true;
                    _embeddedVideoPlayer.BringToFront();
                    SetQuantizeMenuEnabled(false);
                }
            }
            _activeLoadIsSelectionFollow = false;
            if (!isSelectionFollow)
            {
                _loadingLabel.Visible = false;
                SetQuantizeMenuEnabled(true);
                statusLabel.Text = string.Empty;
                _embeddedVideoPlayer.SetStatusMessage(loaded ? null : string.IsNullOrWhiteSpace(_embeddedVideoPlayer.StatusMessage) ? "動画を再生できませんでした。" : _embeddedVideoPlayer.StatusMessage);
            }
            MediaLoadCompleted?.Invoke(videoPath, responseRequestId, loaded, false);
            if (_imageSourceKind == ImageSourceKind.VideoPlayback)
                ReportCurrentVideoPlaybackState(force: true);
        }
        else
        {
            if (requestGeneration == _loadRequestId) _activeLoadIsSelectionFollow = false;
            MediaLoadCompleted?.Invoke(videoPath, responseRequestId, false, true);
            if (requestGeneration == _loadRequestId) ReportCurrentVideoPlaybackState(force: true);
        }
    }

    internal void FailVideoLoadRequest(string videoPath, long? externalRequestId, string message)
    {
        _activeLoadIsSelectionFollow = false;
        ClearDecoderStatus();
        _imageDecodeCts?.Cancel();
        _mediaLoadCts?.Cancel();
        long requestGeneration = ++_loadRequestId;
        long responseRequestId = externalRequestId ?? requestGeneration;
        SetVideoPlaybackRequest(responseRequestId);
        _imageGeneration++;
        _pendingFirstPaintRequestId = 0;
        _embeddedVideoPlayer.StopPlayback();
        _embeddedVideoPlayer.Visible = true;
        pictureBox1.Visible = false;
        _imageSourceKind = ImageSourceKind.VideoPlayback;
        UpdatePresentationModeUi();
        _currentPath = videoPath;
        statusStrip1.Visible = false;
        statusLabel.Text = string.Empty;
        _embeddedVideoPlayer.SetStatusMessage(message);
        _loadingLabel.Visible = false;
        MediaLoadCompleted?.Invoke(videoPath, responseRequestId, false, false);
        ReportVideoPlaybackState(ViewerVideoPlaybackState.Error, force: true);
    }

    private bool PrepareEmbeddedVideoSurface(bool requireVisibleHost = false)
    {
        if (requireVisibleHost && (!Visible || !IsHandleCreated))
        {
            return false;
        }

        pictureBox1.Visible = false;
        _embeddedVideoPlayer.Visible = true;
        _embeddedVideoPlayer.BringToFront();
        if (!requireVisibleHost)
        {
            _embeddedVideoPlayer.PrepareVideoSurface();
            return true;
        }

        PerformLayout();
        _embeddedVideoPlayer.PerformLayout();
        bool surfacePrepared = _embeddedVideoPlayer.PrepareVideoSurface(requireVisibleHost: true);
        PerformLayout();
        _embeddedVideoPlayer.PerformLayout();
        _embeddedVideoPlayer.BringToFront();
        return surfacePrepared
            && _embeddedVideoPlayer.Visible
            && _embeddedVideoPlayer.VideoViewVisible
            && _embeddedVideoPlayer.VideoViewHandleCreated
            && (!requireVisibleHost || Visible && IsHandleCreated);
    }

    private bool PrepareEmbeddedVideoSurfaceForSelectionFollow()
    {
        if (!Visible || !IsHandleCreated)
        {
            return false;
        }

        _embeddedVideoPlayer.Visible = true;
        _embeddedVideoPlayer.PrepareVideoSurface(requireVisibleHost: true);
        if (_displayImage != null && pictureBox1.Visible)
        {
            pictureBox1.BringToFront();
        }
        else
        {
            _embeddedVideoPlayer.BringToFront();
        }
        return _embeddedVideoPlayer.VideoViewVisible && _embeddedVideoPlayer.VideoViewHandleCreated;
    }

}
