using System.Drawing;
using System.Linq;
using MidFD.Services;

namespace MidFD.Dialogs;

public static class MoveDestinationDialog
{
    public static string? Show(
        string prompt,
        string title,
        string defaultPath,
        IReadOnlyList<string>? history,
        string? summaryText,
        string? warningText)
    {
        using Form form = new Form
        {
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoScaleMode = AutoScaleMode.Font,
            ClientSize = new Size(string.IsNullOrWhiteSpace(summaryText) && string.IsNullOrWhiteSpace(warningText) ? 384 : 468, 170)
        };

        int contentWidth = form.ClientSize.Width - 32;
        int currentTop = 16;

        if (!string.IsNullOrWhiteSpace(summaryText))
        {
            Label summaryLabel = new Label
            {
                Left = 16,
                Top = currentTop,
                Width = contentWidth,
                Text = summaryText
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

        ComboBox inputBox = new ComboBox
        {
            Left = 16,
            Top = currentTop,
            Width = contentWidth,
            DropDownStyle = ComboBoxStyle.DropDown,
            Text = defaultPath
        };
        currentTop = inputBox.Bottom + 12;

        var normalizedHistory = new List<string>();
        int filteredEmpty = 0;
        int filteredNotExists = 0;
        int filteredDuplicate = 0;
        int keptSameAsDefault = 0;
        if (history != null)
        {
            string normDefault = (defaultPath ?? string.Empty).TrimEnd('\\', '/');
            foreach (var h in history)
            {
                if (string.IsNullOrWhiteSpace(h))
                {
                    filteredEmpty++;
                    continue;
                }

                string normalized = h.Trim();
                if (!Directory.Exists(normalized))
                {
                    filteredNotExists++;
                    continue;
                }

                string normH = normalized.TrimEnd('\\', '/');
                if (string.Equals(normH, normDefault, StringComparison.OrdinalIgnoreCase))
                {
                    keptSameAsDefault++;
                }

                if (normalizedHistory.Any(x => string.Equals(x.TrimEnd('\\', '/'), normH, StringComparison.OrdinalIgnoreCase)))
                {
                    filteredDuplicate++;
                    continue;
                }

                normalizedHistory.Add(normalized);
            }
        }

        LogService.Info(
            $"[DirectoryMoveHistory] source=MoveDestinationDialog rawCount={history?.Count ?? 0} visibleCount={normalizedHistory.Count} " +
            $"filteredEmpty={filteredEmpty} filteredNotExists={filteredNotExists} filteredDuplicate={filteredDuplicate} " +
            $"keptSameAsDefault={keptSameAsDefault} defaultPath='{defaultPath ?? string.Empty}'");

        foreach (var path in normalizedHistory)
        {
            inputBox.Items.Add(path);
        }

        Label? warningLabel = null;
        if (!string.IsNullOrWhiteSpace(warningText))
        {
            warningLabel = new Label
            {
                Left = 16,
                Top = currentTop,
                Width = contentWidth,
                Text = warningText,
                ForeColor = Color.Firebrick
            };
            warningLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(warningLabel, warningLabel.Width, 32);
            currentTop = warningLabel.Bottom;
        }

        Button btnOk = new Button
        {
            Text = "OK",
            MinimumSize = new Size(80, 30),
            DialogResult = DialogResult.OK
        };
        Button btnCancel = new Button
        {
            Text = "Cancel",
            MinimumSize = new Size(80, 30),
            DialogResult = DialogResult.Cancel
        };

        form.Controls.Add(textLabel);
        form.Controls.Add(inputBox);
        if (warningLabel != null)
        {
            form.Controls.Add(warningLabel);
        }
        form.Controls.Add(btnOk);
        form.Controls.Add(btnCancel);
        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            new[] { btnOk, btnCancel },
            currentTop,
            buttonGap: 10,
            contentGap: 16);

        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        Helpers.DirectoryPathCompletionController? completionController = null;
        string? lastSetText = defaultPath;
        int historyIndex = -1;

        void ApplyHistorySelection(int index)
        {
            if (index < 0 || index >= normalizedHistory.Count)
            {
                return;
            }

            historyIndex = index;
            inputBox.SelectedIndex = index;
            inputBox.Text = normalizedHistory[index];
            lastSetText = inputBox.Text;
            inputBox.SelectAll();
        }

        void SyncHistoryIndexFromText()
        {
            string trimmedCurrent = inputBox.Text.TrimEnd('\\', '/');
            int foundIndex = normalizedHistory.FindIndex(h =>
                string.Equals(h.TrimEnd('\\', '/'), trimmedCurrent, StringComparison.OrdinalIgnoreCase));

            historyIndex = foundIndex;
            inputBox.SelectedIndex = foundIndex >= 0 ? foundIndex : -1;
        }

        inputBox.DropDown += (_, _) => SyncHistoryIndexFromText();

        inputBox.SelectionChangeCommitted += (_, _) =>
        {
            historyIndex = inputBox.SelectedIndex;
            lastSetText = inputBox.Text;
        };

        try
        {
            completionController = Helpers.DirectoryPathCompletionController.Attach(
                inputBox,
                new Helpers.DirectoryPathCompletionOptions
                {
                    ShowOnTextChanged = false
                });

            inputBox.KeyDown += (_, e) =>
            {
                if (e.Handled || e.SuppressKeyPress)
                {
                    return;
                }

                if (completionController?.IsCompletionPopupVisible == true)
                {
                    return;
                }

                if (normalizedHistory.Count == 0)
                {
                    return;
                }

                if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down)
                {
                    return;
                }

                string currentText = inputBox.Text;
                if (currentText != lastSetText)
                {
                    string trimmedCurrent = currentText.TrimEnd('\\', '/');
                    int foundIndex = normalizedHistory.FindIndex(h => string.Equals(h.TrimEnd('\\', '/'), trimmedCurrent, StringComparison.OrdinalIgnoreCase));
                    historyIndex = foundIndex;
                }

                if (e.KeyCode == Keys.Up)
                {
                    historyIndex = historyIndex < 0
                        ? 0
                        : Math.Min(historyIndex + 1, normalizedHistory.Count - 1);
                }
                else // Keys.Down
                {
                    historyIndex = historyIndex < 0
                        ? -1
                        : Math.Max(historyIndex - 1, 0);
                }

                if (historyIndex >= 0)
                {
                    ApplyHistorySelection(historyIndex);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
            };

            form.Shown += (_, _) =>
            {
                inputBox.SelectAll();
                inputBox.Focus();
            };

            form.FormClosed += (_, _) =>
            {
                completionController?.Dispose();
                completionController = null;
            };

            return form.ShowDialog() == DialogResult.OK ? inputBox.Text : null;
        }
        finally
        {
            completionController?.Dispose();
        }
    }
}
