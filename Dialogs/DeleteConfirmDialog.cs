using MidFD.Helpers;

namespace MidFD.Dialogs;

internal static class DeleteConfirmDialog
{
    public static DialogResult Show(
        IWin32Window owner,
        string title,
        string message,
        MessageBoxIcon icon,
        string? summaryText,
        string? warningText,
        bool requireAltYes = false)
    {
        using DeleteConfirmForm form = new()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font,
            RequireAltYes = requireAltYes
        };
        form.ClientSize = new Size(string.IsNullOrWhiteSpace(summaryText) && string.IsNullOrWhiteSpace(warningText) ? 416 : 484, 120);

        int sideMargin = 16;
        int contentWidth = form.ClientSize.Width - (sideMargin * 2);
        int currentTop = 16;

        PictureBox iconBox = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 48,
            Height = 48,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = icon switch
            {
                MessageBoxIcon.Warning => SystemIcons.Warning.ToBitmap(),
                MessageBoxIcon.Error => SystemIcons.Error.ToBitmap(),
                MessageBoxIcon.Information => SystemIcons.Information.ToBitmap(),
                _ => SystemIcons.Question.ToBitmap()
            }
        };
        form.Controls.Add(iconBox);

        int textLeft = iconBox.Right + 12;
        int textWidth = form.ClientSize.Width - textLeft - sideMargin;

        Label messageLabel = new()
        {
            Left = textLeft,
            Top = currentTop,
            Width = textWidth,
            Text = message
        };
        messageLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(messageLabel, messageLabel.Width, 40);
        form.Controls.Add(messageLabel);
        currentTop = Math.Max(iconBox.Bottom, messageLabel.Bottom) + 12;

        if (!string.IsNullOrWhiteSpace(summaryText))
        {
            Label summaryLabel = new()
            {
                Left = sideMargin,
                Top = currentTop,
                Width = contentWidth,
                Text = summaryText
            };
            summaryLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(summaryLabel, summaryLabel.Width, 42);
            form.Controls.Add(summaryLabel);
            currentTop = summaryLabel.Bottom + 10;
        }

        if (!string.IsNullOrWhiteSpace(warningText))
        {
            Label warningLabel = new()
            {
                Left = sideMargin,
                Top = currentTop,
                Width = contentWidth,
                Text = warningText,
                ForeColor = Color.Firebrick
            };
            warningLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(warningLabel, warningLabel.Width, 32);
            form.Controls.Add(warningLabel);
            currentTop = warningLabel.Bottom;
        }

        Button yesButton = new()
        {
            Text = requireAltYes ? "完全削除(Alt+Y)" : "はい(&Y)",
            MinimumSize = new Size(96, 30),
            DialogResult = DialogResult.Yes,
            TabIndex = 0
        };
        Button noButton = new()
        {
            Text = "いいえ(&N)",
            MinimumSize = new Size(96, 30),
            DialogResult = DialogResult.No,
            TabIndex = 1
        };

        form.Controls.Add(yesButton);
        form.Controls.Add(noButton);
        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            new[] { yesButton, noButton },
            currentTop,
            buttonGap: 10,
            contentGap: 16);

        form.AcceptButton = noButton;
        form.CancelButton = noButton;

        form.Shown += (_, _) =>
        {
            form.BeginInvoke(new Action(() =>
            {
                form.ActiveControl = noButton;
                noButton.Select();
                noButton.Focus();
            }));
        };

        return form.ShowDialog(owner);
    }

    private sealed class DeleteConfirmForm : Form
    {
        public bool RequireAltYes { get; set; }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (RequireAltYes)
            {
                // 単独の Y (Shift なしも含む) を確実に抑止する。
                // WinForms のボタン mnemonic は Alt なしの Y でも反応することがあるため ProcessCmdKey で先取りする。
                Keys keyCode = keyData & Keys.KeyCode;
                Keys modifiers = keyData & Keys.Modifiers;

                if (keyCode == Keys.Y)
                {
                    if (modifiers == Keys.Alt)
                    {
                        // Alt+Y のみ許可
                        this.DialogResult = DialogResult.Yes;
                        this.Close();
                        return true;
                    }

                    if (modifiers == Keys.None || modifiers == Keys.Shift)
                    {
                        // 単独 Y または Shift+Y は握り潰す
                        return true;
                    }
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
