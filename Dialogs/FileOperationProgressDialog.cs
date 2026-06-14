using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class FileOperationProgressDialog : Form
{
    private const int DialogClientWidth = 384;
    private const int DialogClientHeight = 166;
    private const int PaddingSize = 16;
    private const int ButtonWidth = 96;
    private const int ButtonHeight = 30;
    private const int ControlGap = 10;

    private readonly Label _titleLabel;
    private readonly Label _operationLabel;
    private readonly Label _hintLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _cancelButton;
    private readonly Action? _requestCancel;
    private readonly bool _canCancel;
    private bool _cancelRequested;
    private bool _completed;

    public FileOperationProgressDialog(Action? requestCancel, bool canCancel = true)
    {
        _requestCancel = requestCancel;
        _canCancel = canCancel;

        Text = "ファイル操作中";
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
            Height = 22,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "ファイル操作中",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _operationLabel = new Label
        {
            AutoSize = false,
            Left = PaddingSize,
            Top = 42,
            Width = contentWidth,
            Height = 24,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "処理中",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _progressBar = new ProgressBar
        {
            Left = PaddingSize,
            Top = 72,
            Width = contentWidth,
            Height = 18,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        _hintLabel = new Label
        {
            AutoSize = false,
            Left = PaddingSize,
            Top = 102,
            Width = buttonLeft - PaddingSize - ControlGap,
            Height = ButtonHeight,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "Esc で中断できます。",
            TextAlign = ContentAlignment.MiddleLeft
        };

        _cancelButton = new Button
        {
            Left = buttonLeft,
            Top = 100,
            Width = ButtonWidth,
            Height = ButtonHeight,
            MinimumSize = new Size(ButtonWidth, ButtonHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Text = "中断"
        };
        _cancelButton.Click += (_, _) => RequestCancel();

        if (!_canCancel || _requestCancel == null)
        {
            _cancelButton.Visible = false;
            _hintLabel.Width = contentWidth;
        }

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                RequestCancel();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        Controls.AddRange([_titleLabel, _operationLabel, _progressBar, _hintLabel, _cancelButton]);
    }

    public void UpdateProgress(FileOperationItemProgressState state, bool cancelRequested)
    {
        if (_completed)
        {
            return;
        }

        string operationText = GetOperationText(state.OperationKind);
        _titleLabel.Text = "ファイル操作中";
        if (state.IsIndeterminate || state.TotalItems <= 0)
        {
            _operationLabel.Text = $"{operationText}中";
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 30;
            _progressBar.Value = 0;
        }
        else
        {
            int safeTotal = Math.Max(1, state.TotalItems);
            int safeProcessed = Math.Clamp(state.CurrentItems, 0, safeTotal);
            _operationLabel.Text = $"{operationText}中: {safeProcessed} / {safeTotal} 項目";
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = safeTotal;
            _progressBar.Value = safeProcessed;
        }

        if (cancelRequested)
        {
            MarkCancelRequested();
        }
        else if (!_cancelRequested)
        {
            _hintLabel.Text = "Esc で中断できます。";
        }
    }

    public void MarkCancelRequested()
    {
        if (_cancelRequested || _completed)
        {
            return;
        }

        _cancelRequested = true;
        _cancelButton.Enabled = false;
        _hintLabel.Text = "中断要求中...";
    }

    public void Complete(string message)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _cancelButton.Enabled = false;
        _titleLabel.Text = "ファイル操作中";
        _operationLabel.Text = message;
        _hintLabel.Text = string.Empty;
        Close();
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

    private void RequestCancel()
    {
        if (!_canCancel || _requestCancel == null || _cancelRequested || _completed)
        {
            return;
        }

        MarkCancelRequested();
        _requestCancel();
    }

    private static string GetOperationText(FileOperationItemProgressKind kind)
    {
        return kind switch
        {
            FileOperationItemProgressKind.Copy => "コピー",
            FileOperationItemProgressKind.Move => "移動",
            FileOperationItemProgressKind.Delete => "削除",
            _ => "処理"
        };
    }
}
