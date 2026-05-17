using System;
using System.Drawing;
using System.Windows.Forms;

namespace MidFD.Dialogs;

public static class CommandExecutionDialog
{
    public static (string Command, string Arguments)? Show(string defaultCommand = "", string defaultArguments = "")
    {
        using Form form = new Form()
        {
            Width = 500,
            Height = 180,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "eXecute",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Color.FromArgb(240, 240, 240)
        };

        Label lblCommand = new Label() { Text = "コマンド:", Left = 16, Top = 16, Width = 80, Font = new Font("Meiryo UI", 9F) };
        TextBox txtCommand = new TextBox() { Left = 100, Top = 14, Width = 360, Font = new Font("Consolas", 10F), Text = defaultCommand };

        Label lblArguments = new Label() { Text = "引数:", Left = 16, Top = 56, Width = 80, Font = new Font("Meiryo UI", 9F) };
        TextBox txtArguments = new TextBox() { Left = 100, Top = 54, Width = 360, Font = new Font("Consolas", 10F), Text = defaultArguments };

        Button btnOk = new Button()
        {
            Text = "OK(O)",
            Left = 260,
            Width = 90,
            Top = 100,
            DialogResult = DialogResult.OK,
            Font = new Font("Meiryo UI", 9F)
        };

        Button btnCancel = new Button()
        {
            Text = "キャンセル(C)",
            Left = 360,
            Width = 100,
            Top = 100,
            DialogResult = DialogResult.Cancel,
            Font = new Font("Meiryo UI", 9F)
        };

        form.Controls.Add(lblCommand);
        form.Controls.Add(txtCommand);
        form.Controls.Add(lblArguments);
        form.Controls.Add(txtArguments);
        form.Controls.Add(btnOk);
        form.Controls.Add(btnCancel);

        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        form.Shown += (s, e) => { txtCommand.Focus(); txtCommand.SelectAll(); };

        if (form.ShowDialog() == DialogResult.OK)
        {
            return (txtCommand.Text, txtArguments.Text);
        }

        return null;
    }
}
