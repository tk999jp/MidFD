using MidFD.Models;
using MidFD.Helpers;

namespace MidFD.Dialogs;

public sealed class PasteFolderMergeDialog : Form
{
    private readonly RadioButton _mergeRadioButton;
    private readonly RadioButton _skipRadioButton;
    private bool _applyToAllRequested;

    public DirectoryMergeDecision Result { get; private set; } = new();

    public PasteFolderMergeDialog(string sourcePath, string destPath, bool isCut = false)
    {
        string folderName = Path.GetFileName(destPath);
        string titlePrefix = isCut ? "貼り付け(移動)時の同名フォルダ" : "貼り付け時の同名フォルダ";
        string sourceLabelText = isCut ? "移動元" : "貼り付け元";
        string destinationLabelText = isCut ? "移動先" : "貼り付け先";
        string mergeText = isCut ? "統合して移動" : "統合して貼り付け";
        string skipText = isCut ? "このフォルダは移動しない" : "このフォルダは貼り付けない";
        string titleMessage = isCut
            ? $"移動先に '{folderName}' フォルダがあります。どうしますか？"
            : $"貼り付け先に '{folderName}' フォルダがあります。どうしますか？";
        string applyToAllText = isCut ? "以降の同名フォルダにも適用" : "以降すべてに適用";

        Text = titlePrefix;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(568, 214);

        var titleLabel = new Label
        {
            Left = 16,
            Top = 16,
            Width = 536,
            Height = 36,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = titleMessage
        };

        var sourceLabel = new Label
        {
            Left = 16,
            Top = 56,
            Width = 536,
            Height = 18,
            AutoEllipsis = true,
            Text = $"{sourceLabelText}: {sourcePath}"
        };

        var destinationLabel = new Label
        {
            Left = 16,
            Top = 76,
            Width = 536,
            Height = 18,
            AutoEllipsis = true,
            Text = $"{destinationLabelText}: {destPath}"
        };

        var applyToAllCheckBox = new CheckBox
        {
            Left = 16,
            Top = 106,
            Width = 236,
            Height = 22,
            Text = applyToAllText,
            TabIndex = 0
        };

        _mergeRadioButton = new RadioButton
        {
            Left = 16,
            Top = 136,
            Width = 174,
            Height = 24,
            Text = mergeText,
            Checked = true,
            TabIndex = 1
        };

        _skipRadioButton = new RadioButton
        {
            Left = 206,
            Top = 136,
            Width = 214,
            Height = 24,
            Text = skipText,
            TabIndex = 2
        };

        var okButton = new Button
        {
            Left = 244,
            Top = 178,
            Width = 84,
            Height = 28,
            Text = "OK(&Y)",
            DialogResult = DialogResult.OK,
            UseMnemonic = true,
            TabIndex = 3
        };
        okButton.Click += (_, _) => _applyToAllRequested = false;

        var applyAllButton = new Button
        {
            Left = 334,
            Top = 178,
            Width = 124,
            Height = 28,
            Text = "以降すべてOK(&A)",
            DialogResult = DialogResult.OK,
            UseMnemonic = true,
            TabIndex = 4
        };
        applyAllButton.Click += (_, _) => _applyToAllRequested = true;

        var cancelButton = new Button
        {
            Left = 464,
            Top = 178,
            Width = 88,
            Height = 28,
            Text = "取消(&C)",
            DialogResult = DialogResult.Cancel,
            UseMnemonic = true,
            TabIndex = 5
        };

        Controls.Add(titleLabel);
        Controls.Add(sourceLabel);
        Controls.Add(destinationLabel);
        Controls.Add(applyToAllCheckBox);
        Controls.Add(_mergeRadioButton);
        Controls.Add(_skipRadioButton);
        Controls.Add(okButton);
        Controls.Add(applyAllButton);
        Controls.Add(cancelButton);

        titleLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(titleLabel, titleLabel.Width, 36);
        sourceLabel.Top = titleLabel.Bottom + 4;
        destinationLabel.Top = sourceLabel.Bottom + 4;
        applyToAllCheckBox.Top = destinationLabel.Bottom + 10;
        _mergeRadioButton.Top = applyToAllCheckBox.Bottom + 8;
        _skipRadioButton.Top = _mergeRadioButton.Top;
        FileOperationDialogLayoutHelper.EnsureBottomButtonRow(
            this,
            new[] { okButton, applyAllButton, cancelButton },
            Math.Max(_mergeRadioButton.Bottom, _skipRadioButton.Bottom),
            buttonGap: 8,
            contentGap: 14);
        int contentWidth = ClientSize.Width - 32;
        titleLabel.Width = contentWidth;
        sourceLabel.Width = contentWidth;
        destinationLabel.Width = contentWidth;

        AcceptButton = okButton;
        CancelButton = cancelButton;
        DialogKeyboardHelper.AttachOkCancelBindings(this, okButton, cancelButton);

        FormClosing += (_, _) =>
        {
            if (DialogResult != DialogResult.OK)
            {
                return;
            }

            Result = new DirectoryMergeDecision
            {
                Policy = _skipRadioButton.Checked ? DirectoryMergePolicy.Skip : DirectoryMergePolicy.Merge,
                ApplyToAll = _applyToAllRequested || applyToAllCheckBox.Checked
            };
        };
    }

    public static DirectoryMergeDecision Show(IWin32Window owner, string sourcePath, string destPath, bool isCut = false)
    {
        using var dialog = new PasteFolderMergeDialog(sourcePath, destPath, isCut);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result
            : new DirectoryMergeDecision { Policy = DirectoryMergePolicy.Cancel };
    }
}
