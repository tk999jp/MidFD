namespace MidFD.Dialogs;

public enum ClipboardPasteChoice
{
    Cancel = 0,
    FileDrop,
    ClipboardImage
}

public sealed class ClipboardPasteChoiceDialog : Form
{
    private ClipboardPasteChoice _result = ClipboardPasteChoice.Cancel;

    public ClipboardPasteChoiceDialog()
    {
        Text = "貼り付け方法の選択";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(472, 146);

        var messageLabel = new Label
        {
            Left = 16,
            Top = 16,
            Width = 440,
            Height = 42,
            Text = "クリップボードに複数の貼り付け候補があります。どちらを貼り付けますか？",
            TextAlign = ContentAlignment.MiddleLeft
        };

        var fileButton = new Button
        {
            Left = 16,
            Top = 84,
            Width = 136,
            Height = 30,
            Text = "ファイルを貼り付け(&F)",
            UseMnemonic = true
        };

        var imageButton = new Button
        {
            Left = 160,
            Top = 84,
            Width = 176,
            Height = 30,
            Text = "画像を PNG として貼り付け(&I)",
            UseMnemonic = true
        };

        var cancelButton = new Button
        {
            Left = 344,
            Top = 84,
            Width = 112,
            Height = 30,
            Text = "キャンセル(&C)",
            UseMnemonic = true,
            DialogResult = DialogResult.Cancel
        };

        fileButton.Click += (_, _) => Commit(ClipboardPasteChoice.FileDrop);
        imageButton.Click += (_, _) => Commit(ClipboardPasteChoice.ClipboardImage);

        Controls.Add(messageLabel);
        Controls.Add(fileButton);
        Controls.Add(imageButton);
        Controls.Add(cancelButton);

        messageLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(messageLabel, messageLabel.Width, 42);
        FileOperationDialogLayoutHelper.EnsureBottomButtonRow(
            this,
            new[] { fileButton, imageButton, cancelButton },
            messageLabel.Bottom,
            buttonGap: 8,
            contentGap: 18);
        int contentWidth = ClientSize.Width - 32;
        messageLabel.Width = contentWidth;

        AcceptButton = imageButton;
        CancelButton = cancelButton;
        Shown += (_, _) =>
        {
            ActiveControl = imageButton;
            imageButton.Select();
        };
    }

    public static ClipboardPasteChoice ShowChoice(IWin32Window owner)
    {
        using var dialog = new ClipboardPasteChoiceDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog._result
            : ClipboardPasteChoice.Cancel;
    }

    private void Commit(ClipboardPasteChoice result)
    {
        _result = result;
        DialogResult = DialogResult.OK;
    }
}
