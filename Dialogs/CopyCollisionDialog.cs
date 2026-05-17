using MidFD.Models;
using MidFD.Services;
using MidFD.Helpers;

namespace MidFD.Dialogs;

public sealed class CopyCollisionDialog : Form
{
    private readonly RadioButton _newerOnlyRadioButton;
    private readonly RadioButton _renameCopyRadioButton;
    private readonly RadioButton _overwriteRadioButton;
    private readonly RadioButton _skipRadioButton;
    private bool _applyToAllRequested;

    public CopyCollisionDecision Result { get; private set; } = new();

    public CopyCollisionDialog(string sourcePath, string destPath)
    {
        const int sideMargin = 16;
        const int topMargin = 16;
        const int sectionGap = 14;
        const int rowGap = 10;

        string fileName = Path.GetFileName(destPath);

        Text = "同名ファイル衝突";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(660, 404);
        int contentWidth = ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;
        int labelColumnWidth = 120;
        int columnGap = 16;
        int sizeColumnWidth = 152;
        int updatedColumnWidth = contentWidth - labelColumnWidth - sizeColumnWidth - (columnGap * 2);
        int updatedColumnLeft = sideMargin + labelColumnWidth + columnGap;
        int sizeColumnLeft = updatedColumnLeft + updatedColumnWidth + columnGap;

        var titleLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 0,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"コピー先に '{fileName}' があります"
        };
        titleLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(titleLabel, titleLabel.Width, 28);
        currentTop = titleLabel.Bottom + sectionGap;

        var updatedHeaderLabel = new Label
        {
            Left = updatedColumnLeft,
            Top = currentTop,
            Width = updatedColumnWidth,
            Height = 24,
            Text = "更新日時",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font, FontStyle.Bold)
        };

        var sizeHeaderLabel = new Label
        {
            Left = sizeColumnLeft,
            Top = currentTop,
            Width = sizeColumnWidth,
            Height = 24,
            Text = "サイズ",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font, FontStyle.Bold)
        };
        currentTop = updatedHeaderLabel.Bottom + rowGap;

        var sourceLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = labelColumnWidth,
            Height = 30,
            Text = "コピー元",
            TextAlign = ContentAlignment.MiddleLeft
        };

        var sourceUpdatedLabel = CreateValueLabel(updatedColumnLeft, currentTop, updatedColumnWidth, FormatTimestamp(sourcePath), ContentAlignment.MiddleCenter);
        var sourceSizeLabel = CreateValueLabel(sizeColumnLeft, currentTop, sizeColumnWidth, FormatFileSize(sourcePath), ContentAlignment.MiddleRight);
        currentTop = sourceLabel.Bottom + 2;

        var destLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop + 24,
            Width = labelColumnWidth,
            Height = 30,
            Text = "コピー先",
            TextAlign = ContentAlignment.MiddleLeft
        };

        var destUpdatedLabel = CreateValueLabel(updatedColumnLeft, destLabel.Top, updatedColumnWidth, FormatTimestamp(destPath), ContentAlignment.MiddleCenter);
        var destSizeLabel = CreateValueLabel(sizeColumnLeft, destLabel.Top, sizeColumnWidth, FormatFileSize(destPath), ContentAlignment.MiddleRight);

        var arrowLabel = new Label
        {
            Left = updatedColumnLeft + (updatedColumnWidth / 2) - 11,
            Top = sourceLabel.Bottom + 2,
            Width = 22,
            Height = 20,
            Text = "↓",
            ForeColor = Color.Blue,
            Font = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        currentTop = destLabel.Bottom + sectionGap;

        var policyGroupBox = new GroupBox
        {
            Left = sideMargin + 88,
            Top = currentTop,
            Width = contentWidth - 88,
            Height = 128,
            Text = "コピー条件"
        };

        _newerOnlyRadioButton = new RadioButton
        {
            Left = 16,
            Top = 24,
            Width = 390,
            Text = "新しい日付のファイルのみコピー",
            Checked = true
        };

        _renameCopyRadioButton = new RadioButton
        {
            Left = 16,
            Top = 48,
            Width = 390,
            Text = "名前を変えてコピー"
        };

        _overwriteRadioButton = new RadioButton
        {
            Left = 16,
            Top = 72,
            Width = 390,
            Text = "上書コピー"
        };

        _skipRadioButton = new RadioButton
        {
            Left = 16,
            Top = 96,
            Width = 390,
            Text = "同名はコピーしない"
        };

        policyGroupBox.Controls.Add(_newerOnlyRadioButton);
        policyGroupBox.Controls.Add(_renameCopyRadioButton);
        policyGroupBox.Controls.Add(_overwriteRadioButton);
        policyGroupBox.Controls.Add(_skipRadioButton);

        var okButton = new Button
        {
            Text = "OK(&Y)",
            DialogResult = DialogResult.OK,
            UseMnemonic = true,
            MinimumSize = new Size(88, 30)
        };
        okButton.Click += (_, _) => _applyToAllRequested = false;

        var applyAllButton = new Button
        {
            Text = "以降全てOK(&A)",
            DialogResult = DialogResult.OK,
            UseMnemonic = true,
            MinimumSize = new Size(124, 30)
        };
        applyAllButton.Click += (_, _) => _applyToAllRequested = true;

        var cancelButton = new Button
        {
            Text = "キャンセル(&C)",
            DialogResult = DialogResult.Cancel,
            UseMnemonic = true,
            MinimumSize = new Size(104, 30)
        };

        Controls.Add(titleLabel);
        Controls.Add(updatedHeaderLabel);
        Controls.Add(sizeHeaderLabel);
        Controls.Add(sourceLabel);
        Controls.Add(sourceUpdatedLabel);
        Controls.Add(sourceSizeLabel);
        Controls.Add(arrowLabel);
        Controls.Add(destLabel);
        Controls.Add(destUpdatedLabel);
        Controls.Add(destSizeLabel);
        Controls.Add(policyGroupBox);
        Controls.Add(okButton);
        Controls.Add(applyAllButton);
        Controls.Add(cancelButton);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            this,
            new[] { okButton, applyAllButton, cancelButton },
            policyGroupBox.Bottom,
            buttonGap: 10,
            contentGap: 16);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        DialogKeyboardHelper.AttachOkCancelBindings(this, okButton, cancelButton);

        FormClosing += OnFormClosing;
    }

    public static CopyCollisionDecision Show(IWin32Window owner, string sourcePath, string destPath)
    {
        using var dialog = new CopyCollisionDialog(sourcePath, destPath);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result
            : new CopyCollisionDecision { Policy = CopyCollisionPolicy.Cancel };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }

        Result = new CopyCollisionDecision
        {
            Policy = GetSelectedPolicy(),
            ApplyToAll = _applyToAllRequested
        };
    }

    private CopyCollisionPolicy GetSelectedPolicy()
    {
        if (_renameCopyRadioButton.Checked) return CopyCollisionPolicy.RenameCopy;
        if (_overwriteRadioButton.Checked) return CopyCollisionPolicy.Overwrite;
        if (_skipRadioButton.Checked) return CopyCollisionPolicy.Skip;
        return CopyCollisionPolicy.NewerOnly;
    }

    private static Label CreateValueLabel(int left, int top, int width, string text, ContentAlignment textAlign)
    {
        return new Label
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 30,
            BorderStyle = BorderStyle.FixedSingle,
            Text = text,
            TextAlign = textAlign
        };
    }

    private static string FormatTimestamp(string path)
    {
        return File.Exists(path)
            ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss")
            : "-";
    }

    private static string FormatFileSize(string path)
    {
        if (!File.Exists(path))
        {
            return "-";
        }

        long length = new FileInfo(path).Length;
        if (length < 1024)
        {
            return $"{length:N0} バイト";
        }

        return FileOperationService.FormatSize(length);
    }
}
