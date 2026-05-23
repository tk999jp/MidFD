using System;
using System.Drawing;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class ArchiveTextPreviewForm : Form
{
    private readonly TextBox _textBox;

    public ArchiveTextPreviewForm(string title, string content)
    {
        Text = $"Preview - {title}";
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(480, 320);
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable;

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            BackColor = MidFDColors.ViewerBack,
            ForeColor = MidFDColors.ViewerFore,
            Font = new Font("Consolas", 10F),
            Text = NormalizeNewlinesForTextBox(content),
            BorderStyle = BorderStyle.None
        };

        // テキスト選択が青くハイライトされるのを抑止
        _textBox.SelectionStart = 0;
        _textBox.SelectionLength = 0;

        Controls.Add(_textBox);

        // Esc, Q, Enter キーでフォームを閉じる
        KeyDown += (sender, e) =>
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Q || e.KeyCode == Keys.Enter)
            {
                Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        _textBox.KeyDown += (sender, e) =>
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Q || e.KeyCode == Keys.Enter)
            {
                Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        KeyPreview = true;
    }

    public void SetContent(string title, string text)
    {
        Text = $"Preview - {title}";
        _textBox.Text = NormalizeNewlinesForTextBox(text);
        _textBox.SelectionStart = 0;
        _textBox.SelectionLength = 0;
    }

    private static string NormalizeNewlinesForTextBox(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", Environment.NewLine);
    }
}
