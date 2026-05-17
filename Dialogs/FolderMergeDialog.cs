using MidFD.Models;
using MidFD.Helpers;

namespace MidFD.Dialogs;

public sealed class FolderMergeDialog : Form
{
    private readonly RadioButton _mergeRadioButton;
    private readonly RadioButton _skipRadioButton;
    private bool _applyToAllRequested;

    public DirectoryMergeDecision Result { get; private set; } = new();

    public FolderMergeDialog(string sourcePath, string destPath, string operationLabel = "コピー")
    {
        string folderName = Path.GetFileName(destPath);
        string sourceLabelText = $"{operationLabel}元";
        string destinationLabelText = $"{operationLabel}先";
        string mergeActionText = operationLabel == "移動" ? "統合して移動" : "統合してコピー";
        string skipActionText = operationLabel == "移動" ? "このフォルダは移動しない" : "このフォルダはコピーしない";

        Text = "同名フォルダ衝突";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(560, 228);

        var titleLabel = new Label
        {
            Left = 16,
            Top = 16,
            Width = 528,
            Height = 32,
            BorderStyle = BorderStyle.FixedSingle,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"{destinationLabelText}に '{folderName}' フォルダがあります"
        };

        var sourceLabel = new Label
        {
            Left = 16,
            Top = 60,
            Width = 528,
            Height = 22,
            AutoEllipsis = true,
            Text = $"{sourceLabelText}: {sourcePath}"
        };

        var destinationLabel = new Label
        {
            Left = 16,
            Top = 84,
            Width = 528,
            Height = 22,
            AutoEllipsis = true,
            Text = $"{destinationLabelText}: {destPath}"
        };

        var policyGroupBox = new GroupBox
        {
            Left = 16,
            Top = 116,
            Width = 528,
            Height = 64,
            Text = "フォルダ処理"
        };

        _mergeRadioButton = new RadioButton
        {
            Left = 16,
            Top = 24,
            Width = 240,
            Text = mergeActionText,
            Checked = true
        };

        _skipRadioButton = new RadioButton
        {
            Left = 272,
            Top = 24,
            Width = 220,
            Text = skipActionText
        };

        policyGroupBox.Controls.Add(_mergeRadioButton);
        policyGroupBox.Controls.Add(_skipRadioButton);

        var okButton = new Button
        {
            Left = 176,
            Top = 190,
            Width = 92,
            Height = 28,
            Text = "OK(&Y)",
            DialogResult = DialogResult.OK,
            UseMnemonic = true
        };
        okButton.Click += (_, _) => _applyToAllRequested = false;

        var applyAllButton = new Button
        {
            Left = 280,
            Top = 190,
            Width = 144,
            Height = 28,
            Text = "以降すべてOK(&A)",
            DialogResult = DialogResult.OK,
            UseMnemonic = true
        };
        applyAllButton.Click += (_, _) => _applyToAllRequested = true;

        var cancelButton = new Button
        {
            Left = 436,
            Top = 190,
            Width = 92,
            Height = 28,
            Text = "キャンセル(&C)",
            DialogResult = DialogResult.Cancel,
            UseMnemonic = true
        };

        Controls.Add(titleLabel);
        Controls.Add(sourceLabel);
        Controls.Add(destinationLabel);
        Controls.Add(policyGroupBox);
        Controls.Add(okButton);
        Controls.Add(applyAllButton);
        Controls.Add(cancelButton);

        titleLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(titleLabel, titleLabel.Width, 32);
        sourceLabel.Top = titleLabel.Bottom + 12;
        destinationLabel.Top = sourceLabel.Bottom + 6;
        policyGroupBox.Top = destinationLabel.Bottom + 10;
        FileOperationDialogLayoutHelper.EnsureBottomButtonRow(
            this,
            new[] { okButton, applyAllButton, cancelButton },
            policyGroupBox.Bottom,
            buttonGap: 10,
            contentGap: 12);
        int contentWidth = ClientSize.Width - 32;
        titleLabel.Width = contentWidth;
        sourceLabel.Width = contentWidth;
        destinationLabel.Width = contentWidth;
        policyGroupBox.Width = contentWidth;

        AcceptButton = okButton;
        CancelButton = cancelButton;
        DialogKeyboardHelper.AttachOkCancelBindings(this, okButton, cancelButton);

        FormClosing += OnFormClosing;
    }

    public static DirectoryMergeDecision Show(IWin32Window owner, string sourcePath, string destPath, string operationLabel = "コピー")
    {
        using var dialog = new FolderMergeDialog(sourcePath, destPath, operationLabel);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result
            : new DirectoryMergeDecision { Policy = DirectoryMergePolicy.Cancel };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }

        Result = new DirectoryMergeDecision
        {
            Policy = _skipRadioButton.Checked ? DirectoryMergePolicy.Skip : DirectoryMergePolicy.Merge,
            ApplyToAll = _applyToAllRequested
        };
    }
}
