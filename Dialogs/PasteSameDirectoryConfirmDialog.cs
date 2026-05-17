namespace MidFD.Dialogs;

public enum PasteSameDirectoryConfirmAction
{
    Cancel = 0,
    Yes = 1,
    No = 2,
    All = 3
}

public static class PasteSameDirectoryConfirmDialog
{
    public static PasteSameDirectoryConfirmAction Show(IWin32Window owner, string fileName, string suggestedName, bool showApplyToAll)
    {
        const int sideMargin = 16;
        const int topMargin = 16;
        using Form form = new Form
        {
            ClientSize = new Size(520, 156),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "同じフォルダへの別名コピー確認",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font
        };

        int contentWidth = form.ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        Label titleLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 0,
            Text = $"'{fileName}' はこのフォルダに既にあります。",
            TextAlign = ContentAlignment.MiddleLeft
        };
        titleLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(titleLabel, titleLabel.Width, 28);
        currentTop = titleLabel.Bottom + 6;

        Label messageLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 0,
            Text = $"別名コピーを作成しますか？\n作成する場合は '{suggestedName}' になります。",
            TextAlign = ContentAlignment.MiddleLeft
        };
        messageLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(messageLabel, messageLabel.Width, 40);
        currentTop = messageLabel.Bottom;

        Button yesButton = new Button { Text = "はい(&Y)", UseMnemonic = true, TabIndex = 0, MinimumSize = new Size(88, 30) };
        Button noButton = new Button { Text = "いいえ(&N)", UseMnemonic = true, TabIndex = 1, MinimumSize = new Size(88, 30) };
        Button allButton = new Button { Text = "一括(&A)", UseMnemonic = true, TabIndex = 2, MinimumSize = new Size(88, 30) };
        Button cancelButton = new Button { Text = "キャンセル(&C)", UseMnemonic = true, DialogResult = DialogResult.Cancel, TabIndex = showApplyToAll ? 3 : 2, MinimumSize = new Size(104, 30) };

        PasteSameDirectoryConfirmAction result = PasteSameDirectoryConfirmAction.Cancel;
        yesButton.Click += (_, _) =>
        {
            result = PasteSameDirectoryConfirmAction.Yes;
            form.DialogResult = DialogResult.OK;
        };
        noButton.Click += (_, _) =>
        {
            result = PasteSameDirectoryConfirmAction.No;
            form.DialogResult = DialogResult.OK;
        };
        allButton.Click += (_, _) =>
        {
            result = PasteSameDirectoryConfirmAction.All;
            form.DialogResult = DialogResult.OK;
        };

        form.Controls.Add(titleLabel);
        form.Controls.Add(messageLabel);
        form.Controls.Add(yesButton);
        form.Controls.Add(noButton);
        if (showApplyToAll)
        {
            form.Controls.Add(allButton);
        }
        form.Controls.Add(cancelButton);
        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            showApplyToAll
                ? new[] { yesButton, noButton, allButton, cancelButton }
                : new[] { yesButton, noButton, cancelButton },
            currentTop,
            buttonGap: 8,
            contentGap: 16);

        form.AcceptButton = yesButton;
        form.CancelButton = cancelButton;
        Helpers.DialogKeyboardHelper.AttachOkCancelBindings(form, yesButton, cancelButton);
        form.Shown += (_, _) => yesButton.Select();

        return form.ShowDialog(owner) == DialogResult.OK
            ? result
            : PasteSameDirectoryConfirmAction.Cancel;
    }
}
