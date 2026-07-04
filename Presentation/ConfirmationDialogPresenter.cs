using System.Drawing;
using System.Windows.Forms;
using MidFD.Helpers;

namespace MidFD.Presentation;

public static class ConfirmationDialogPresenter
{
    public static DialogResult ShowDragInCopyConfirmationDialog(IWin32Window owner, string message)
    {
        return ShowYesNoDialog(owner, "Drag-in (Copy)", message);
    }

    public static DialogResult ShowDragInMoveConfirmationDialog(IWin32Window owner, string message)
    {
        return ShowYesNoDialog(owner, "Drag-in (Move)", message);
    }

    public static DialogResult ShowLargeTextClipboardCopyConfirmationDialog(
        IWin32Window owner,
        int lineCount,
        long estimatedBytes)
    {
        string message = DialogTextBuilder.BuildLargeTextClipboardCopyConfirmationMessage(lineCount, estimatedBytes);
        return ShowYesNoDialog(owner, "LargeText 大量コピー", message);
    }

    private static DialogResult ShowYesNoDialog(IWin32Window owner, string title, string message)
    {
        using (var form = new Form())
        {
            form.Text = title;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.CenterParent;
            form.ClientSize = new Size(420, 140);

            var label = new Label
            {
                Text = message,
                Location = new Point(15, 15),
                Size = new Size(390, 60),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var btnYes = new Button
            {
                Text = "はい(&Y)",
                DialogResult = DialogResult.Yes,
                Location = new Point(210, 90),
                Size = new Size(90, 30)
            };

            var btnNo = new Button
            {
                Text = "いいえ(&N)",
                DialogResult = DialogResult.No,
                Location = new Point(310, 90),
                Size = new Size(90, 30)
            };

            form.Controls.Add(label);
            form.Controls.Add(btnYes);
            form.Controls.Add(btnNo);

            form.AcceptButton = btnYes;
            form.CancelButton = btnNo;

            return form.ShowDialog(owner);
        }
    }
}
