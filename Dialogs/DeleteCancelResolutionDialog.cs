using MidFD.Helpers;

namespace MidFD.Dialogs;

public enum DeleteCancelResolution
{
    RestoreNow,
    KeepDeleted,
    Cancel
}

internal static class DeleteCancelResolutionDialog
{
    public static DeleteCancelResolution Show(
        IWin32Window owner,
        int successCount,
        int pendingCount,
        int failedCount)
    {
        using Form form = new()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "削除のキャンセル",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font,
            ClientSize = new Size(480, 200)
        };

        int sideMargin = 16;
        int currentTop = 16;

        PictureBox iconBox = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 48,
            Height = 48,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = SystemIcons.Question.ToBitmap()
        };
        form.Controls.Add(iconBox);

        int textLeft = iconBox.Right + 12;
        int textWidth = form.ClientSize.Width - textLeft - sideMargin;

        Label titleLabel = new()
        {
            Left = textLeft,
            Top = currentTop,
            Width = textWidth,
            Text = "削除処理を中断しました。",
            Font = new Font(form.Font, FontStyle.Bold)
        };
        form.Controls.Add(titleLabel);
        currentTop = titleLabel.Bottom + 8;

        Label statsLabel = new()
        {
            Left = textLeft,
            Top = currentTop,
            Width = textWidth,
            Text = $"すでに削除済み: {successCount} 件\n未処理: {pendingCount} 件\n失敗: {failedCount} 件\n\n削除済みのファイルをどうしますか？"
        };
        statsLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(statsLabel, statsLabel.Width, 80);
        form.Controls.Add(statsLabel);
        currentTop = statsLabel.Bottom + 16;

        Button restoreButton = new()
        {
            Text = "元の場所に戻す(&R)",
            MinimumSize = new Size(130, 32),
            DialogResult = DialogResult.Yes,
            TabIndex = 1
        };
        Button keepButton = new()
        {
            Text = "ここまでの削除を確定(&K)",
            MinimumSize = new Size(160, 32),
            DialogResult = DialogResult.No,
            TabIndex = 0
        };
        Button cancelButton = new()
        {
            Text = "キャンセル",
            MinimumSize = new Size(100, 32),
            DialogResult = DialogResult.Cancel,
            TabIndex = 2
        };

        form.Controls.Add(restoreButton);
        form.Controls.Add(keepButton);
        form.Controls.Add(cancelButton);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            new[] { keepButton, restoreButton, cancelButton },
            currentTop,
            buttonGap: 10,
            contentGap: 16);

        form.AcceptButton = keepButton;
        form.CancelButton = cancelButton;

        form.Shown += (_, _) =>
        {
            form.BeginInvoke(new Action(() =>
            {
                form.ActiveControl = keepButton;
                keepButton.Select();
                keepButton.Focus();
            }));
        };

        DialogResult result = form.ShowDialog(owner);
        return result switch
        {
            DialogResult.Yes => DeleteCancelResolution.RestoreNow,
            DialogResult.No => DeleteCancelResolution.KeepDeleted,
            _ => DeleteCancelResolution.Cancel
        };
    }
}
