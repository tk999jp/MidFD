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
    private readonly Stack<ImageHistoryEntry> _undoStack = new();
    private readonly Stack<ImageHistoryEntry> _redoStack = new();
    private int _loadRequestId;
    private readonly Label _loadingLabel;

    public string? CurrentPath => _currentPath;
    public bool HasLoadedImage => _originalImage != null;
    public event Action<Keys>? BrowserNavigationRequested;

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
        _currentPath = path;
        Text = $"{Path.GetFileName(path)} - MidFD Image Viewer";
        int reqId = ++_loadRequestId;

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
            statusLabel.Text = "PracticalStable では画像減色は無効です。";
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
        SetDisplayImage(new Bitmap(_originalImage));
        ApplyInitialZoom(_displayImage);
        statusLabel.Text = "元画像に戻しました";
        ClearRedoStack();
    }

    private void menuCopySvg_Click(object? sender, EventArgs e)
    {
        if (!_featureGate.IsEnabled(FeatureId.SvgClipboard))
        {
            statusLabel.Text = "PracticalStable では SVG コピーは無効です。";
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
        DisposeImage(_displayImage);
        _displayImage = bitmap;
        pictureBox1.Image = _displayImage;
        UpdateQuantizeMenuState();
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
    }

    private void SetQuantizeMenuEnabled(bool enabled)
    {
        _menuQuantize.Enabled = _featureGate.IsEnabled(FeatureId.ImageQuantization) && enabled && _displayImage != null;
        _menuResetImage.Enabled = enabled && _displayImage != null;
        _menuCopySvg.Enabled = _featureGate.IsEnabled(FeatureId.SvgClipboard) && enabled && _displayImage != null;
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

    private void ImageViewerForm_KeyDown(object? sender, KeyEventArgs e)
    {
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
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopyImageToClipboard();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.V)
        {
            PasteImageFromClipboard();
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
            SetDisplayImage(new Bitmap(img));
            _currentPath = null;
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
        int desiredClientHeight = imageHeight + menuStrip1.Height + statusStrip1.Height;
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
}
