namespace MidFD.Helpers;

public sealed class FileOperationProgressFallbackForm : Form
{
    private const int DialogClientWidth = 396;
    private const int DialogClientHeight = 172;
    private const int PaddingSize = 16;
    private const int ButtonWidth = 96;
    private const int ButtonHeight = 30;
    private const int ControlGap = 10;

    private readonly Label _titleLabel;
    private readonly Label _detailLabel;
    private readonly Label _currentLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _cancelButton;
    private readonly Action? _requestCancel;
    private readonly bool _canCancel;
    private readonly System.Windows.Forms.Timer _closeTimer;
    private bool _cancelRequested;
    private bool _completed;

    public FileOperationProgressFallbackForm(string operationName, int totalCount, Action? requestCancel, bool canCancel = true)
    {
        _requestCancel = requestCancel;
        _canCancel = canCancel;

        Text = $"{operationName}中";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        KeyPreview = true;
        ClientSize = new Size(DialogClientWidth, DialogClientHeight);
        MinimumSize = Size;

        int contentWidth = ClientSize.Width - PaddingSize * 2;
        int buttonLeft = ClientSize.Width - PaddingSize - ButtonWidth;

        _titleLabel = new Label
        {
            AutoSize = false,
            Left = PaddingSize,
            Top = 14,
            Width = contentWidth,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = $"{operationName}中...",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _detailLabel = new Label
        {
            AutoSize = false,
            Left = PaddingSize,
            Top = 42,
            Width = contentWidth,
            Height = 22,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = $"0/{Math.Max(0, totalCount)} 件",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _progressBar = new ProgressBar
        {
            Left = PaddingSize,
            Top = 70,
            Width = contentWidth,
            Height = 20,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Minimum = 0,
            Maximum = Math.Max(1, totalCount),
            Value = 0,
            Style = ProgressBarStyle.Continuous
        };

        _currentLabel = new Label
        {
            AutoSize = false,
            Left = PaddingSize,
            Top = 104,
            Width = buttonLeft - PaddingSize - ControlGap,
            Height = ButtonHeight,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "準備中...",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _cancelButton = new Button
        {
            Left = buttonLeft,
            Top = 102,
            Width = ButtonWidth,
            Height = ButtonHeight,
            MinimumSize = new Size(ButtonWidth, ButtonHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Text = "キャンセル"
        };
        _cancelButton.Click += (_, _) => RequestCancel();

        if (!_canCancel || _requestCancel == null)
        {
            _cancelButton.Visible = false;
            _currentLabel.Width = contentWidth;
        }

        _closeTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };

        Controls.AddRange([_titleLabel, _detailLabel, _progressBar, _currentLabel, _cancelButton]);
    }

    public void UpdateProgress(int processedCount, int totalCount, string currentFileName, bool cancelRequested)
    {
        int safeTotal = Math.Max(1, totalCount);
        int safeProcessed = Math.Clamp(processedCount, 0, safeTotal);

        _progressBar.Maximum = safeTotal;
        _progressBar.Value = safeProcessed;
        _detailLabel.Text = $"{safeProcessed}/{Math.Max(0, totalCount)} 件";
        _currentLabel.Text = cancelRequested ? "キャンセル要求中..." : TrimForDisplay(currentFileName);

        if (cancelRequested)
        {
            MarkCancelRequested();
        }
    }

    public void UpdateState(string title, string detail, bool indeterminate, bool cancelRequested)
    {
        Text = title;
        _titleLabel.Text = title;
        _currentLabel.Text = cancelRequested ? "キャンセル要求中..." : TrimForDisplay(detail);
        _progressBar.Style = indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;

        if (indeterminate)
        {
            _detailLabel.Text = "";
        }

        if (cancelRequested)
        {
            MarkCancelRequested();
        }
    }

    public void MarkCancelRequested()
    {
        _cancelRequested = true;
        _cancelButton.Enabled = false;
        _currentLabel.Text = "キャンセル要求中...";
    }

    public void Complete(string message)
    {
        _completed = true;
        _cancelButton.Enabled = false;
        _titleLabel.Text = message;
        _currentLabel.Text = _cancelRequested ? "キャンセル要求を反映しました。" : "完了しました。";
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            RequestCancel();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            RequestCancel();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RequestCancel()
    {
        if (!_canCancel || _requestCancel == null || _cancelRequested || _completed)
        {
            return;
        }

        MarkCancelRequested();
        _requestCancel();
    }

    private static string TrimForDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        const int maxLength = 32;
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "...");
    }
}
