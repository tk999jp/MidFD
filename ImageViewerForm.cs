using System.Drawing;
using System.IO;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Models;
using MidFD.Services;

namespace MidFD;

public partial class ImageViewerForm : Form
{
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
    private readonly PreviewSettings _previewSettings;
    private readonly FeatureGateService _featureGate;
    private readonly ToolStripMenuItem _menuQuantize;
    private readonly ToolStripMenuItem _menuResetImage;
    private readonly ToolStripMenuItem _menuCopySvg;
    private readonly ToolStripMenuItem _menuRotateRight;
    private readonly ToolStripMenuItem _menuRotateLeft;
    private readonly ToolStripMenuItem _menuFlipHorizontal;
    private readonly ToolStripMenuItem _menuFlipVertical;
    private readonly ToolStripMenuItem _menuImageInfo;
    private readonly Stack<ImageHistoryEntry> _undoStack = new();
    private readonly Stack<ImageHistoryEntry> _redoStack = new();
    private int _loadRequestId;
    private readonly Label _loadingLabel;

    // VideoStill Fields
    private string? _videoStillSourceVideoPath;
    private string? _videoStillConfirmedCachePath;
    private int _videoStillCurrentSeconds;
    private double? _videoStillDurationSeconds;
    private VideoMetadataService.VideoMetadataDetails? _videoStillMetadataDetails;
    private int _videoStillVolumePercent;
    private string? _configuredFfmpegPath;
    private CancellationTokenSource? _videoStillCts;
    private bool _isVideoStillMode;
    private readonly Panel _videoStillSeekBarPanel;
    private bool _isSelectingRectangle;
    private Point _selectionStartClientPoint;
    private Point _selectionCurrentClientPoint;
    private Rectangle? _selectionClientRectangle;
    private int _displayRotationQuarterTurns;
    private bool _displayFlipHorizontal;
    private bool _displayFlipVertical;
    private ImageSourceKind _imageSourceKind = ImageSourceKind.Unknown;

    private enum ImageSourceKind
    {
        Unknown,
        File,
        VideoStill,
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
    public bool HasLoadedImage => _originalImage != null;
    public event Action<Keys>? BrowserNavigationRequested;
    public event Action? MarkToggleRequested;

    public ImageViewerForm(PreviewSettings? previewSettings = null, FeatureGateService? featureGate = null)
    {
        _previewSettings = previewSettings?.Clone() ?? new PreviewSettings();
        _featureGate = featureGate ?? new FeatureGateService(FeatureProfile.Full);

        InitializeComponent();
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

        _menuResetImage.ShortcutKeys = Keys.Control | Keys.R;

        menuEdit.DropDownItems.Add(new ToolStripSeparator());
        menuEdit.DropDownItems.Add(_menuRotateRight);
        menuEdit.DropDownItems.Add(_menuRotateLeft);
        menuEdit.DropDownItems.Add(_menuFlipHorizontal);
        menuEdit.DropDownItems.Add(_menuFlipVertical);
        menuEdit.DropDownItems.Add(new ToolStripSeparator());
        menuEdit.DropDownItems.Add(_menuImageInfo);

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

        _videoStillSeekBarPanel = new Panel
        {
            Visible = false,
            BackColor = SystemColors.ControlDark,
            Height = 6,
            Cursor = Cursors.Hand,
            Dock = DockStyle.Bottom
        };
        _videoStillSeekBarPanel.Paint += VideoStillSeekBarPanel_Paint;
        _videoStillSeekBarPanel.MouseClick += VideoStillSeekBarPanel_MouseClick;
        Controls.Add(_videoStillSeekBarPanel);
        _videoStillSeekBarPanel.BringToFront();

        UpdateQuantizeMenuState();
        UpdateZoomStatus();
    }

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

    public async void LoadImage(string path, bool showErrorMessage = true)
    {
        _isVideoStillMode = false;
        _imageSourceKind = ImageSourceKind.File;
        statusStrip1.Visible = true;
        _videoStillSeekBarPanel.Visible = false;
        _videoStillCts?.Cancel();
        _currentPath = path;
        Text = $"{Path.GetFileName(path)} - MidFD Image Viewer";
        int reqId = ++_loadRequestId;
        ClearSelection();

        bool isSvg = string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

        // 読み込み開始時のUIフィードバック
        statusLabel.Text = isSvg ? "SVGレンダリング中..." : "画像読み込み中...";
        _loadingLabel.Text = isSvg ? "Rendering SVG..." : "Loading Image...";
        CenterLoadingLabel();
        _loadingLabel.Visible = true;
        SetQuantizeMenuEnabled(false);

        // 前の画像をクリア（Wait感の向上）
        DisposeImage(_originalImage);
        DisposeImage(_displayImage);
        _originalImage = null;
        _displayImage = null;
        pictureBox1.Image = null;

        try
        {
            var result = await Task.Run(() => ImagePreviewService.GetPreviewImage(path));

            if (reqId != _loadRequestId || IsDisposed)
            {
                result.Image?.Dispose();
                return;
            }

            if (result.Image == null)
            {
                statusLabel.Text = isSvg ? "SVG読み込み失敗" : "画像読み込み失敗";
                string userMessage = isSvg
                    ? "SVGを読み込めませんでした。\nファイル形式やサイズ、または未対応の要素が原因の可能性があります。"
                    : result.ErrorMessage;
                LogService.Error($"[ImageViewer] LoadImage failed path='{path}' message='{userMessage}'");
                if (showErrorMessage)
                {
                    MessageBox.Show(userMessage, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            ReplaceCurrentImages(result.Image, clearHistory: true);
            ApplyInitialZoom(result.Image);
            statusLabel.Text = isSvg ? "SVG読み込み完了" : "読み込み完了";
        }
        catch (Exception ex)
        {
            if (reqId == _loadRequestId && !IsDisposed)
            {
                statusLabel.Text = "エラーが発生しました";
                LogService.Error($"[ImageViewer] LoadImage exception path='{path}'", ex);
                if (showErrorMessage)
                {
                    MessageBox.Show($"読み込み中にエラーが発生しました:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        finally
        {
            if (reqId == _loadRequestId && !IsDisposed)
            {
                _loadingLabel.Visible = false;
                SetQuantizeMenuEnabled(true);
            }
        }
    }

    private void CenterLoadingLabel()
    {
        _loadingLabel.Location = new Point(
            (imageScrollPanel.Width - _loadingLabel.Width) / 2,
            (imageScrollPanel.Height - _loadingLabel.Height) / 2
        );
    }

    public void LoadMedia(string path, PreviewKind kind, bool showErrorMessage = true)
    {
        if (kind == PreviewKind.Video)
        {
            statusLabel.Text = "動画の内蔵再生は未対応です。";
            return;
        }
        LoadImage(path, showErrorMessage);
    }

    private async void menuQuantize_Click(object? sender, EventArgs e)
    {
        if (!_featureGate.IsEnabled(FeatureId.ImageQuantization))
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
        statusLabel.Text = $"減色中: {label}";
        try
        {
            PushUndoState("減色前");
            Bitmap src = new Bitmap(_displayImage ?? _originalImage);
            Bitmap result = await Task.Run(() => ImageQuantizationService.Quantize(src, dialog.ResultRequest));
            src.Dispose();
            SetDisplayImage(result);
            ApplyInitialZoom(result);
            statusLabel.Text = $"減色適用: {label}";
            ClearRedoStack();
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"減色失敗: {ex.Message}";
        }
        finally
        {
            SetQuantizeMenuEnabled(true);
        }
    }

    private void menuResetImage_Click(object? sender, EventArgs e)
    {
        if (_originalImage == null || _displayImage == null)
        {
            return;
        }
        PushUndoState("元画像へ戻す前");
        ResetDisplayTransformState();
        SetDisplayImage(new Bitmap(_originalImage));
        ApplyInitialZoom(_displayImage);
        statusLabel.Text = "元画像に戻しました";
        ClearRedoStack();
    }

    private void menuCopySvg_Click(object? sender, EventArgs e)
    {
        if (!_featureGate.IsEnabled(FeatureId.SvgClipboard))
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

    private void ReplaceCurrentImages(Bitmap source, bool clearHistory)
    {
        ClearSelection();
        ResetDisplayTransformState();
        DisposeImage(_originalImage);
        DisposeImage(_displayImage);
        _originalImage = new Bitmap(source);
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
    {
        _displayRotationQuarterTurns = (_displayRotationQuarterTurns + 1) % 4;
        RotateDisplayImage(RotateFlipType.Rotate90FlipNone, "右90度回転前", "右90度回転");
    }

    private void menuRotateLeft_Click(object? sender, EventArgs e)
    {
        _displayRotationQuarterTurns = (_displayRotationQuarterTurns + 3) % 4;
        RotateDisplayImage(RotateFlipType.Rotate270FlipNone, "左90度回転前", "左90度回転");
    }

    private void menuFlipHorizontal_Click(object? sender, EventArgs e)
    {
        _displayFlipHorizontal = !_displayFlipHorizontal;
        RotateDisplayImage(RotateFlipType.RotateNoneFlipX, "左右反転前", "左右反転");
    }

    private void menuFlipVertical_Click(object? sender, EventArgs e)
    {
        _displayFlipVertical = !_displayFlipVertical;
        RotateDisplayImage(RotateFlipType.RotateNoneFlipY, "上下反転前", "上下反転");
    }

    private void UpdateQuantizeMenuState()
    {
        bool allowQuantize = _featureGate.IsEnabled(FeatureId.ImageQuantization);
        bool allowSvgClipboard = _featureGate.IsEnabled(FeatureId.SvgClipboard);
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
        _menuQuantize.Enabled = _featureGate.IsEnabled(FeatureId.ImageQuantization) && enabled && _displayImage != null;
        _menuResetImage.Enabled = enabled && _displayImage != null;
        _menuCopySvg.Enabled = _featureGate.IsEnabled(FeatureId.SvgClipboard) && enabled && _displayImage != null;
        _menuImageInfo.Enabled = enabled && (_displayImage != null || _originalImage != null);
    }

    private void PushUndoState(string label)
    {
        if (_displayImage == null) return;
        _undoStack.Push(new ImageHistoryEntry { Image = new Bitmap(_displayImage), Label = label });
        TrimHistoryStack(_undoStack);
    }

    private void PushRedoState(string label)
    {
        if (_displayImage == null) return;
        _redoStack.Push(new ImageHistoryEntry { Image = new Bitmap(_displayImage), Label = label });
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
        if (HandleViewerShortcutKey(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HandleViewerShortcutKey(Keys keyData)
    {
        Keys modifiers = keyData & (Keys.Control | Keys.Shift | Keys.Alt);
        Keys keyCode = keyData & Keys.KeyCode;

        if (modifiers == Keys.Control)
        {
            if (keyCode == Keys.C)
            {
                CopyImageToClipboard();
                return true;
            }

            if (keyCode == Keys.V)
            {
                PasteImageFromClipboard();
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

    private void ImageViewerForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && !e.Control && !e.Alt)
        {
            if (!HasEditableInputFocus())
            {
                MarkToggleRequested?.Invoke();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            return;
        }

        if (_isVideoStillMode)
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                ExecuteVideoStillExternalPlay();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Home)
            {
                ChangeVideoStillPosition(0, absolute: true);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Left)
            {
                int delta = GetVideoStillStepSeconds();
                if (e.Shift) delta = GetVideoStillShiftStepSeconds();
                ChangeVideoStillPosition(-delta, absolute: false);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Right)
            {
                int delta = GetVideoStillStepSeconds();
                if (e.Shift) delta = GetVideoStillShiftStepSeconds();
                ChangeVideoStillPosition(delta, absolute: false);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }

        if (e.Control && e.KeyCode == Keys.Z)
        {
            UndoImageOperation();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.Y)
        {
            RedoImageOperation();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            if (_isFullscreen)
            {
                ExitFullscreen();
            }
            else
            {
                Close();
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Enter)
        {
            Close();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.F)
        {
            ToggleFullscreen();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if ((e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Oem6) && !e.Control && !e.Alt)
        {
            SetZoom(_zoom * ZoomStep);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if ((e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.OemOpenBrackets) && !e.Control && !e.Alt)
        {
            SetZoom(_zoom / ZoomStep);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0 || e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1)
        {
            SetZoom(1.0f);
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
        if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
        {
            BrowserNavigationRequested?.Invoke(e.KeyCode);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private bool HasEditableInputFocus()
    {
        Control? activeControl = ActiveControl;
        return activeControl is TextBoxBase or ComboBox or NumericUpDown or ToolStrip;
    }

    private void ImageViewerForm_MouseWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == 0)
        {
            return;
        }
        if (e.Delta > 0)
        {
            SetZoom(_zoom * ZoomStep);
        }
        else if (e.Delta < 0)
        {
            SetZoom(_zoom / ZoomStep);
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
                    Clipboard.SetImage(selectionBitmap);
                }
                return;
            }

            if (pictureBox1.Image != null)
            {
                Clipboard.SetImage(pictureBox1.Image);
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
            if (!Clipboard.ContainsImage())
            {
                MessageBox.Show("Clipboard does not contain an image.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Image? img = Clipboard.GetImage();
            if (img == null)
            {
                return;
            }
            PushUndoState("貼り付け前");
            ResetDisplayTransformState();
            SetDisplayImage(new Bitmap(img));
            _currentPath = null;
            _imageSourceKind = ImageSourceKind.Clipboard;
            Text = "Clipboard Image - MidFD Image Viewer";
            ApplyInitialZoom(img);
            statusLabel.Text = "貼り付け";
            ClearRedoStack();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to paste image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenImageFromFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.svg|すべてのファイル|*.*",
            InitialDirectory = GetPreferredDirectory(),
            Title = "画像を開く"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadImage(dialog.FileName);
        }
    }

    private void SaveImageAs()
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
        if (dialog.ShowDialog(this) != DialogResult.OK)
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

    private void menuClose_Click(object sender, EventArgs e) => Close();
    private void menuOpen_Click(object sender, EventArgs e) => OpenImageFromFile();
    private void menuSaveAs_Click(object sender, EventArgs e) => SaveImageAs();
    private void menuCopy_Click(object sender, EventArgs e) => CopyImageToClipboard();
    private void menuPaste_Click(object sender, EventArgs e) => PasteImageFromClipboard();
    private void menuImageInfo_Click(object? sender, EventArgs e) => ShowImageInformationDialog();

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

        if (_imageSourceKind == ImageSourceKind.VideoStill)
        {
            message += Environment.NewLine + Environment.NewLine +
                       $"コンテナ形式: {info.VideoContainerText}{Environment.NewLine}" +
                       $"コンテナ詳細: {info.VideoContainerDetailText}{Environment.NewLine}" +
                       $"コンテナ識別子: {info.VideoContainerIdentifierText}{Environment.NewLine}" +
                       $"動画codec: {info.VideoCodecText}{Environment.NewLine}" +
                       $"音声codec: {info.AudioCodecText}{Environment.NewLine}" +
                       $"動画解像度: {info.VideoResolutionText}{Environment.NewLine}" +
                       $"フレームレート: {info.VideoFrameRateText}{Environment.NewLine}" +
                       $"動画長: {info.VideoDurationText}{Environment.NewLine}" +
                       $"ビットレート: {info.VideoBitRateText}";
        }

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

        if (_imageSourceKind == ImageSourceKind.VideoStill && _videoStillMetadataDetails != null)
        {
            string formatName = _videoStillMetadataDetails.FormatName ?? "-";
            string formatLongName = _videoStillMetadataDetails.FormatLongName ?? "-";
            videoContainer = ResolveContainerLabelFromPath(sourcePath, formatLongName, formatName);
            videoContainerDetail = formatLongName;
            videoContainerIdentifier = formatName;
            videoCodec = _videoStillMetadataDetails.VideoCodec ?? "-";
            audioCodec = _videoStillMetadataDetails.AudioCodec ?? "なし";
            if (_videoStillMetadataDetails.Width is int vw && _videoStillMetadataDetails.Height is int vh)
            {
                videoResolution = $"{vw} x {vh} px";
            }

            if (_videoStillMetadataDetails.FrameRate is double fps && fps > 0)
            {
                videoFrameRate = $"{fps:0.##} fps";
            }

            if (_videoStillMetadataDetails.DurationSeconds is double duration && duration > 0)
            {
                videoDuration = FormatDuration(duration);
            }

            if (_videoStillMetadataDetails.BitRate is long bitRate && bitRate > 0)
            {
                videoBitRate = FormatBitRate(bitRate);
            }
        }

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
            ImageSourceKind.VideoStill => "VideoStill",
            ImageSourceKind.Clipboard => "クリップボード画像",
            _ => "不明"
        };
    }

    private string? GetInfoSourcePath()
    {
        if (_imageSourceKind == ImageSourceKind.VideoStill)
        {
            return _videoStillSourceVideoPath;
        }

        if (_imageSourceKind == ImageSourceKind.File)
        {
            return _currentPath;
        }

        return null;
    }

    private string GetImageFormatText(string? sourcePath)
    {
        if (_imageSourceKind == ImageSourceKind.VideoStill)
        {
            return "VideoStill";
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
        int nonClientWidth = Width - ClientSize.Width;
        int nonClientHeight = Height - ClientSize.Height;
        int maxClientWidth = Math.Max(200, workArea.Width - nonClientWidth);
        int maxClientHeight = Math.Max(200, workArea.Height - nonClientHeight);
        int desiredClientWidth = imageWidth;
        int videoStillBarHeight = (_isVideoStillMode && _videoStillSeekBarPanel.Visible)
            ? _videoStillSeekBarPanel.Height
            : 0;
        int statusStripHeight = statusStrip1.Visible ? statusStrip1.Height : 0;
        int desiredClientHeight = imageHeight + menuStrip1.Height + statusStripHeight + videoStillBarHeight;
        int clientWidth = Math.Min(desiredClientWidth, maxClientWidth);
        int clientHeight = Math.Min(desiredClientHeight, maxClientHeight);
        ClientSize = new Size(clientWidth, clientHeight);
        EnsureWindowInWorkArea(workArea);
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
        pictureBox1.Image = null;
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
        _isSelectingRectangle = true;
        _selectionStartClientPoint = ClampPointToDisplayedImage(e.Location);
        _selectionCurrentClientPoint = _selectionStartClientPoint;
        _selectionClientRectangle = null;
        pictureBox1.Invalidate();
    }

    private void pictureBox1_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_isSelectingRectangle)
        {
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
        _selectionCurrentClientPoint = ClampPointToDisplayedImage(e.Location);
        Rectangle selectionRect = CreateNormalizedRectangle(_selectionStartClientPoint, _selectionCurrentClientPoint);
        _selectionClientRectangle = selectionRect.Width < 2 || selectionRect.Height < 2
            ? null
            : selectionRect;
        pictureBox1.Invalidate();
    }

    private void pictureBox1_Paint(object? sender, PaintEventArgs e)
    {
        Rectangle? selectionRect = _selectionClientRectangle;
        if (selectionRect == null || _displayImage == null)
        {
            return;
        }

        Rectangle clientRect = selectionRect.Value;
        using var outlinePen = new Pen(Color.DeepSkyBlue, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        using var fillBrush = new SolidBrush(Color.FromArgb(40, Color.DeepSkyBlue));
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

        using var labelBackBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        e.Graphics.FillRectangle(labelBackBrush, labelRect);
        TextRenderer.DrawText(
            e.Graphics,
            sizeText,
            Font,
            labelRect,
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void ClearSelection()
    {
        _isSelectingRectangle = false;
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

        Rectangle displayedImageRect = GetDisplayedImageRectangle();
        Rectangle clampedClientRect = Rectangle.Intersect(displayedImageRect, clientRect);
        if (clampedClientRect.Width <= 0 || clampedClientRect.Height <= 0)
        {
            return Rectangle.Empty;
        }

        double scaleX = _displayImage.Width / (double)displayedImageRect.Width;
        double scaleY = _displayImage.Height / (double)displayedImageRect.Height;
        int left = (int)Math.Floor(clampedClientRect.Left * scaleX);
        int top = (int)Math.Floor(clampedClientRect.Top * scaleY);
        int right = (int)Math.Ceiling(clampedClientRect.Right * scaleX);
        int bottom = (int)Math.Ceiling(clampedClientRect.Bottom * scaleY);

        left = Math.Clamp(left, 0, _displayImage.Width - 1);
        top = Math.Clamp(top, 0, _displayImage.Height - 1);
        right = Math.Clamp(right, left + 1, _displayImage.Width);
        bottom = Math.Clamp(bottom, top + 1, _displayImage.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

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

    public async void LoadVideoStill(string videoPath, string? configuredFfmpegPath, int initialSeconds, int volumePercent)
    {
        _isVideoStillMode = true;
        _imageSourceKind = ImageSourceKind.VideoStill;
        _videoStillMetadataDetails = null;
        ClearSelection();
        statusStrip1.Visible = false;
        _videoStillSourceVideoPath = videoPath;
        _configuredFfmpegPath = configuredFfmpegPath;
        _videoStillVolumePercent = volumePercent;
        _currentPath = videoPath; // Copy機能などでパスが使われるため

        _videoStillSeekBarPanel.Visible = true;
        _videoStillSeekBarPanel.BringToFront();

        _videoStillCts?.Cancel();
        _videoStillCts?.Dispose();
        _videoStillCts = new CancellationTokenSource();
        var token = _videoStillCts.Token;

        try
        {
            var details = await VideoMetadataService.TryGetDetailsAsync(videoPath, configuredFfmpegPath, token);
            if (details.Success)
            {
                _videoStillMetadataDetails = details;
                _videoStillDurationSeconds = details.DurationSeconds;
            }
            else
            {
                _videoStillDurationSeconds = null;
            }
        }
        catch
        {
            _videoStillDurationSeconds = null;
        }

        _videoStillCurrentSeconds = ClampVideoStillSeconds(initialSeconds);

        await UpdateVideoStillImageAsync(token);
    }

    private async Task UpdateVideoStillImageAsync(CancellationToken token)
    {
        if (_videoStillSourceVideoPath == null) return;
        string videoPath = _videoStillSourceVideoPath;
        int seconds = _videoStillCurrentSeconds;

        statusLabel.Text = $"[VideoStill] 生成中:{seconds}秒";
        _loadingLabel.Text = "Generating Video Still...";
        CenterLoadingLabel();
        _loadingLabel.Visible = true;
        SetQuantizeMenuEnabled(false);

        try
        {
            var cacheDir = VideoStillPreviewService.GetDefaultCacheDirectory();
            var stillResult = await VideoStillPreviewService.GenerateStillAsync(
                videoPath, seconds, _configuredFfmpegPath, cacheDir, token);

            if (token.IsCancellationRequested) return;

            if (!stillResult.Success || string.IsNullOrWhiteSpace(stillResult.ImagePath))
            {
                statusLabel.Text = $"[VideoStill] {seconds}秒の静止画を取得できません。前回画像を維持します。";
                return;
            }

            _videoStillConfirmedCachePath = stillResult.ImagePath;
            using var fs = new FileStream(stillResult.ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var decoded = new Bitmap(fs);
            var previewImage = new Bitmap(decoded);

            ReplaceCurrentImages(previewImage, clearHistory: true);
            ApplyInitialZoom(previewImage);

            UpdateVideoStillStatusLine();
            _videoStillSeekBarPanel.Invalidate();
            Text = $"{Path.GetFileName(videoPath)} @ {seconds}秒 - MidFD Image Viewer";
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            statusLabel.Text = $"[VideoStill] 読み込み失敗: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _loadingLabel.Visible = false;
                SetQuantizeMenuEnabled(true);
            }
        }
    }

    private void ChangeVideoStillPosition(int value, bool absolute)
    {
        if (!_isVideoStillMode) return;
        int newSeconds = absolute ? value : _videoStillCurrentSeconds + value;
        newSeconds = ClampVideoStillSeconds(newSeconds);

        if (newSeconds != _videoStillCurrentSeconds)
        {
            _videoStillCurrentSeconds = newSeconds;
            _videoStillCts?.Cancel();
            _videoStillCts?.Dispose();
            _videoStillCts = new CancellationTokenSource();
            _ = UpdateVideoStillImageAsync(_videoStillCts.Token);
            UpdateVideoStillStatusLine();
            _videoStillSeekBarPanel.Invalidate();
        }
    }

    private void ExecuteVideoStillExternalPlay()
    {
        if (!_isVideoStillMode || string.IsNullOrWhiteSpace(_videoStillSourceVideoPath)) return;
        int previewSeconds = _videoStillCurrentSeconds;
        VideoPlaybackLaunchResult launchResult = VideoPlaybackLaunchService.Launch(
            _videoStillSourceVideoPath,
            _configuredFfmpegPath,
            _videoStillVolumePercent,
            previewSeconds);

        if (launchResult.Success && launchResult.UsedFfplay)
        {
            statusLabel.Text = $"ffplay.exeで外部再生しました。位置:{launchResult.AppliedStartSeconds}秒 / 音量:{launchResult.AppliedVolumePercent}%";
            LogService.Info(
                $"[VideoStill] ExternalPlay success previewSeconds={previewSeconds} appliedStartSeconds={launchResult.AppliedStartSeconds} durationSeconds={_videoStillDurationSeconds?.ToString("F3") ?? "n/a"} videoPath='{_videoStillSourceVideoPath}'");
            BeginInvoke(new Action(Close));
            return;
        }

        if (launchResult.Success && launchResult.UsedDefaultApp)
        {
            statusLabel.Text = "ffplay.exeが見つからないため、既定アプリで動画を開きました。";
            LogService.Info(
                $"[VideoStill] ExternalPlay fallback previewSeconds={previewSeconds} appliedStartSeconds={launchResult.AppliedStartSeconds} durationSeconds={_videoStillDurationSeconds?.ToString("F3") ?? "n/a"} videoPath='{_videoStillSourceVideoPath}'");
            BeginInvoke(new Action(Close));
            return;
        }

        statusLabel.Text = launchResult.ErrorMessage ?? "外部再生の起動に失敗しました。";
        if (!string.IsNullOrWhiteSpace(launchResult.ProcessError))
        {
            LogService.Error("[VideoStill] 外部再生起動失敗", new InvalidOperationException(launchResult.ProcessError));
        }
    }

    private void UpdateVideoStillStatusLine()
    {
        if (!_isVideoStillMode) return;
        bool hasDuration = _videoStillDurationSeconds.HasValue && _videoStillDurationSeconds.Value > 0;
        int maxSeconds = GetVideoStillDisplayDurationSeconds();
        string durationPart = hasDuration ? $" / {maxSeconds}秒" : "";
        string controlPart = hasDuration ? "←/→:位置" : "←/→:位置 (動画長不明のため位置バー無効)";
        statusLabel.Text = $"[VideoStill] 位置:{_videoStillCurrentSeconds}秒{durationPart} | {controlPart} | Ctrl+Enter:再生";
    }

    private int GetVideoStillDisplayDurationSeconds()
    {
        if (_videoStillDurationSeconds.HasValue && _videoStillDurationSeconds.Value > 0)
            return (int)Math.Ceiling(_videoStillDurationSeconds.Value);
        return 0;
    }

    private bool TryGetVideoStillSeekBarMaxSeconds(out int maxSeconds)
    {
        if (_videoStillDurationSeconds.HasValue && _videoStillDurationSeconds.Value > 0)
        {
            int max = (int)Math.Floor(_videoStillDurationSeconds.Value) - 1;
            maxSeconds = Math.Max(0, max);
            return true;
        }
        maxSeconds = 0;
        return false;
    }

    private int ClampVideoStillSeconds(int seconds)
    {
        if (TryGetVideoStillSeekBarMaxSeconds(out int maxSeconds))
        {
            return Math.Clamp(seconds, 0, maxSeconds);
        }
        return Math.Max(0, seconds);
    }

    private int GetVideoStillStepSeconds()
    {
        double duration = _videoStillDurationSeconds ?? 0;
        if (duration <= 0) return 5;
        if (duration <= 30) return 1;
        if (duration <= 90) return 2;
        return 5;
    }

    private int GetVideoStillShiftStepSeconds()
    {
        double duration = _videoStillDurationSeconds ?? 0;
        if (duration <= 0) return 30;
        if (duration <= 30) return 5;
        if (duration <= 90) return 10;
        return 30;
    }

    private void VideoStillSeekBarPanel_Paint(object? sender, PaintEventArgs e)
    {
        if (!_isVideoStillMode) return;
        var rect = _videoStillSeekBarPanel.ClientRectangle;
        if (rect.Width <= 2 || rect.Height <= 2) return;
        e.Graphics.Clear(SystemColors.ControlDark);

        if (TryGetVideoStillSeekBarMaxSeconds(out int safeMax))
        {
            float ratio = safeMax > 0 ? (float)_videoStillCurrentSeconds / safeMax : 0f;
            ratio = Math.Clamp(ratio, 0f, 1f);
            int fillWidth = (int)(rect.Width * ratio);
            if (fillWidth > 0)
            {
                using var fillBrush = new SolidBrush(Color.FromArgb(200, 80, 160, 255));
                e.Graphics.FillRectangle(fillBrush, 0, 0, fillWidth, rect.Height);
            }
            int markerX = Math.Clamp(fillWidth, 0, rect.Width - 1);
            using var markerPen = new Pen(Color.White, 1f);
            e.Graphics.DrawLine(markerPen, markerX, 0, markerX, rect.Height);
        }
    }

    private void VideoStillSeekBarPanel_MouseClick(object? sender, MouseEventArgs e)
    {
        if (!_isVideoStillMode || e.Button != MouseButtons.Left) return;
        if (!TryGetVideoStillSeekBarMaxSeconds(out int safeMax))
        {
            return;
        }
        int w = _videoStillSeekBarPanel.Width;
        if (w <= 0) return;
        double ratio = (double)e.X / w;
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        int targetSeconds = safeMax > 0 ? (int)Math.Round(safeMax * ratio) : 0;
        ChangeVideoStillPosition(targetSeconds, absolute: true);
    }

}
