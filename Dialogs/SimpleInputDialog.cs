namespace MidFD.Dialogs;

public static class SimpleInputDialog
{
    public sealed record DisplayOptions(
        string? SummaryText = null,
        string? WarningText = null,
        bool EnableDirectoryCompletion = false);

    public static string Show(string prompt, string title, string defaultValue = "")
    {
        return Show(prompt, title, defaultValue, null);
    }

    public static string Show(string prompt, string title, string defaultValue, DisplayOptions? options)
    {
        using Form form = CreateForm(title, options);

        BuildDialog(form, prompt, defaultValue, options, -1, out TextBox textBox, out Button confirmation, out Button cancel);

        return form.ShowDialog() == DialogResult.OK ? textBox.Text : "";
    }

    public static string? ShowNullable(string prompt, string title, string defaultValue = "")
    {
        return ShowNullable(prompt, title, defaultValue, null, -1);
    }

    public static string? ShowNullable(string prompt, string title, string defaultValue, DisplayOptions? options)
    {
        return ShowNullable(prompt, title, defaultValue, options, -1);
    }

    /// <summary>
    /// ダイアログを表示し、OK なら入力値を、Cancel なら null を返す。
    /// </summary>
    /// <param name="selectionLength">
    /// 0以上の値を指定すると、先頭から selectionLength 文字だけを選択状態で表示する。
    /// -1（既定）は全選択。
    /// </param>
    public static string? ShowNullable(string prompt, string title, string defaultValue, DisplayOptions? options, int selectionLength)
    {
        using Form form = CreateForm(title, options);

        BuildDialog(form, prompt, defaultValue, options, selectionLength, out TextBox textBox, out Button confirmation, out Button cancel);

        return form.ShowDialog() == DialogResult.OK ? textBox.Text : null;
    }

    private static Form CreateForm(string title, DisplayOptions? options)
    {
        var form = new Form()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoScaleMode = AutoScaleMode.Font
        };
        form.ClientSize = new Size(string.IsNullOrWhiteSpace(options?.SummaryText) && string.IsNullOrWhiteSpace(options?.WarningText) ? 384 : 468, 120);
        return form;
    }

    private static void BuildDialog(
        Form form,
        string prompt,
        string defaultValue,
        DisplayOptions? options,
        int selectionLength,
        out TextBox textBox,
        out Button confirmation,
        out Button cancel)
    {
        int contentWidth = form.ClientSize.Width - 32;
        int currentTop = 16;

        Label? summaryLabel = null;
        if (!string.IsNullOrWhiteSpace(options?.SummaryText))
        {
            summaryLabel = new Label
            {
                Left = 16,
                Top = currentTop,
                Width = contentWidth,
                Text = options.SummaryText
            };
            summaryLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(summaryLabel, summaryLabel.Width, 42);
            form.Controls.Add(summaryLabel);
            currentTop = summaryLabel.Bottom + 10;
        }

        Label textLabel = new Label
        {
            Left = 16,
            Top = currentTop,
            Width = contentWidth,
            Text = prompt
        };
        textLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(textLabel, textLabel.Width, 24);
        currentTop = textLabel.Bottom + 8;

        textBox = new TextBox
        {
            Left = 16,
            Top = currentTop,
            Width = contentWidth,
            Text = defaultValue
        };
        currentTop = textBox.Bottom + 12;

        Label? warningLabel = null;
        if (!string.IsNullOrWhiteSpace(options?.WarningText))
        {
            warningLabel = new Label
            {
                Left = 16,
                Top = currentTop,
                Width = contentWidth,
                Text = options.WarningText,
                ForeColor = Color.Firebrick
            };
            warningLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(warningLabel, warningLabel.Width, 32);
            currentTop = warningLabel.Bottom;
            form.Controls.Add(warningLabel);
        }

        confirmation = new Button
        {
            Text = "OK",
            MinimumSize = new Size(80, 30),
            DialogResult = DialogResult.OK
        };
        cancel = new Button
        {
            Text = "Cancel",
            MinimumSize = new Size(80, 30),
            DialogResult = DialogResult.Cancel
        };

        form.Controls.Add(textLabel);
        form.Controls.Add(textBox);
        form.Controls.Add(confirmation);
        form.Controls.Add(cancel);
        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            new[] { confirmation, cancel },
            currentTop,
            buttonGap: 10,
            contentGap: 16);

        form.AcceptButton = confirmation;
        form.CancelButton = cancel;

        if (options?.EnableDirectoryCompletion == true)
        {
            Helpers.DirectoryPathCompletionController.Attach(textBox);
        }

        // selectionLength < 0: 全選択（既定）、0以上: 先頭から指定長だけ選択
        int capturedLength = selectionLength;
        TextBox shownTextBox = textBox;
        form.Shown += (s, e) =>
        {
            if (capturedLength < 0)
            {
                shownTextBox.SelectAll();
            }
            else
            {
                int len = Math.Min(capturedLength, shownTextBox.Text.Length);
                shownTextBox.Select(0, len);
            }
        };
    }
}

