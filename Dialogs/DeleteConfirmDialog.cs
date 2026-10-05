using MidFD.Helpers;

using System.ComponentModel;

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
        bool requireAltYes = false,
        bool usePermanentDelete = false)
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
            Text = requireAltYes
                ? (usePermanentDelete ? "完全削除(Alt+Y)" : "はい(Alt+Y)")
                : "はい(&Y)",
            MinimumSize = new Size(96, 30),
            TabIndex = 0
        };
        Button noButton = new()
        {
            Text = "いいえ(&N)",
            MinimumSize = new Size(96, 30),
            DialogResult = DialogResult.No,
            TabIndex = 1
        };

        form.ConfigureApprovalButton(yesButton);

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

    internal sealed class DeleteConfirmForm : Form
    {
        private Button? _approvalButton;
        private bool _altYesAuthorized;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public bool RequireAltYes { get; set; }

        internal Button ApprovalButton => _approvalButton
            ?? throw new InvalidOperationException("The approval button has not been configured.");

        internal void ConfigureApprovalButton(Button approvalButton)
        {
            _approvalButton = approvalButton;
            approvalButton.DialogResult = RequireAltYes ? DialogResult.None : DialogResult.Yes;
        }

        internal bool ProcessKeyForTest(Keys keyData)
        {
            Message msg = default;
            return ProcessCmdKey(ref msg, keyData);
        }

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
                        _altYesAuthorized = true;
                        this.DialogResult = DialogResult.Yes;
                        this.Close();
                        return true;
                    }

                    // Alt を伴わない Y 系列は、mnemonic／入力経路に関係なく承認しない。
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (RequireAltYes && DialogResult == DialogResult.Yes && !_altYesAuthorized)
            {
                DialogResult = DialogResult.None;
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }
    }

    internal static DeleteConfirmForm CreateTestForm(bool requireAltYes)
    {
        DeleteConfirmForm form = new() { RequireAltYes = requireAltYes };
        Button approvalButton = new() { Text = requireAltYes ? "はい(Alt+Y)" : "はい(&Y)" };
        Button noButton = new() { Text = "いいえ(&N)", DialogResult = DialogResult.No };
        form.ConfigureApprovalButton(approvalButton);
        form.Controls.Add(approvalButton);
        form.Controls.Add(noButton);
        form.AcceptButton = noButton;
        form.CancelButton = noButton;
        return form;
    }
}
