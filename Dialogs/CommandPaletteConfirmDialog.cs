using MidFD.Helpers;

namespace MidFD.Dialogs;

internal static class CommandPaletteConfirmDialog
{
    public static bool Show(IWin32Window owner, string operationName, string bodyText, bool isDestructive)
    {
        const int sideMargin = 16;
        const int topMargin = 16;

        using var form = new Form
        {
            Text = operationName,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font,
            ClientSize = new Size(560, 260)
        };

        int contentWidth = form.ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        PictureBox iconBox = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 48,
            Height = 48,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = (isDestructive ? MessageBoxIcon.Warning : MessageBoxIcon.Question) switch
            {
                MessageBoxIcon.Warning => SystemIcons.Warning.ToBitmap(),
                _ => SystemIcons.Question.ToBitmap()
            }
        };
        form.Controls.Add(iconBox);

        int textLeft = iconBox.Right + 12;
        int textWidth = form.ClientSize.Width - textLeft - sideMargin;

        Label titleLabel = new()
        {
            Left = textLeft,
            Top = currentTop,
            Width = textWidth,
            Text = isDestructive
                ? "この操作は確認が必要です。"
                : "この操作を実行しますか。",
            Font = new Font(form.Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        titleLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(titleLabel, titleLabel.Width, 28);
        form.Controls.Add(titleLabel);

        Label bodyLabel = new()
        {
            Left = textLeft,
            Top = titleLabel.Bottom + 8,
            Width = textWidth,
            Text = bodyText,
            TextAlign = ContentAlignment.TopLeft
        };
        bodyLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(bodyLabel, bodyLabel.Width, 104);
        form.Controls.Add(bodyLabel);

        Button executeButton = new()
        {
            Text = isDestructive ? "実行(&Y)" : "実行(&Y)",
            MinimumSize = new Size(96, 30),
            DialogResult = DialogResult.Yes
        };

        Button cancelButton = new()
        {
            Text = "キャンセル(&N)",
            MinimumSize = new Size(96, 30),
            DialogResult = DialogResult.No
        };

        form.Controls.Add(executeButton);
        form.Controls.Add(cancelButton);
        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            new[] { executeButton, cancelButton },
            bodyLabel.Bottom,
            buttonGap: 10,
            contentGap: 16);

        form.AcceptButton = cancelButton;
        form.CancelButton = cancelButton;
        form.Shown += (_, _) =>
        {
            form.BeginInvoke(new Action(() =>
            {
                form.ActiveControl = cancelButton;
                cancelButton.Select();
                cancelButton.Focus();
            }));
        };

        return form.ShowDialog(owner) == DialogResult.Yes;
    }
}
