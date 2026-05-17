using System;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Linq;

namespace MidFD.Dialogs;

public static class ComboSelectionDialog
{
    public static string? Show(string prompt, string title, IEnumerable<string> items, string defaultValue = "")
    {
        using Form form = new Form()
        {
            Width = 400,
            Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false
        };

        Label textLabel = new Label() { Left = 16, Top = 16, Width = 350, Text = prompt };
        ComboBox comboBox = new ComboBox() { Left = 16, Top = 40, Width = 350, DropDownStyle = ComboBoxStyle.DropDownList };
        comboBox.Items.AddRange(items.ToArray());
        
        if (comboBox.Items.Contains(defaultValue))
        {
            comboBox.SelectedItem = defaultValue;
        }
        else if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }

        Button confirmation = new Button() { Text = "OK", Left = 196, Width = 80, Top = 72, DialogResult = DialogResult.OK };
        Button cancel = new Button() { Text = "Cancel", Left = 286, Width = 80, Top = 72, DialogResult = DialogResult.Cancel };

        form.Controls.Add(textLabel);
        form.Controls.Add(comboBox);
        form.Controls.Add(confirmation);
        form.Controls.Add(cancel);

        form.AcceptButton = confirmation;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? comboBox.SelectedItem?.ToString() : null;
    }
}
