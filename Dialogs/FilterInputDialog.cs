using System.Drawing;
using System.Windows.Forms;

namespace MidFD.Dialogs;

public class FilterResult
{
    public string Pattern { get; set; } = "";
    public bool UseRegex { get; set; }
}

public static class FilterInputDialog
{
    public static FilterResult? Show(string prompt, string title, string defaultPattern = "", bool defaultUseRegex = false)
    {
        using Form form = new Form()
        {
            Width = 400,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoScaleMode = AutoScaleMode.Font
        };

        Label textLabel = new Label() { Left = 16, Top = 16, Width = 350, Text = prompt, AutoSize = true };
        TextBox textBox = new TextBox() { Left = 16, Top = textLabel.Bottom + 8, Width = 350, Text = defaultPattern };
        
        CheckBox regexCheckBox = new CheckBox() 
        { 
            Text = "正規表現を使用 (&R)", 
            Left = 16, 
            Top = textBox.Bottom + 8, 
            Width = 350, 
            Checked = defaultUseRegex 
        };

        Button confirmation = new Button() { Text = "OK", Left = 196, Width = 80, Top = regexCheckBox.Bottom + 16, Height = 30, DialogResult = DialogResult.OK };
        Button cancel = new Button() { Text = "Cancel", Left = 286, Width = 80, Top = regexCheckBox.Bottom + 16, Height = 30, DialogResult = DialogResult.Cancel };

        form.Controls.Add(textLabel);
        form.Controls.Add(textBox);
        form.Controls.Add(regexCheckBox);
        form.Controls.Add(confirmation);
        form.Controls.Add(cancel);

        form.ClientSize = new Size(400, confirmation.Bottom + 16);

        form.AcceptButton = confirmation;
        form.CancelButton = cancel;

        // すべて選択状態にする
        form.Shown += (s, e) => textBox.SelectAll();

        if (form.ShowDialog() == DialogResult.OK)
        {
            return new FilterResult { Pattern = textBox.Text, UseRegex = regexCheckBox.Checked };
        }
        return null;
    }
}
