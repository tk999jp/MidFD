using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MidFD.Dialogs;

public static class LogdiskDialog
{
    public static string? Show(
        string defaultPath = "",
        IReadOnlyList<string>? history = null)
    {
        const int sideMargin = 16;
        const int topMargin = 16;

        using Form form = new Form()
        {
            ClientSize = new Size(584, 180), // Width 600 相当
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "WinFD - Logdsk",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Color.FromArgb(240, 240, 240),
            AutoScaleMode = AutoScaleMode.Font
        };

        int contentWidth = form.ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        Label promptLabel = new Label()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            AutoSize = true,
            Text = "変更するドライブ名を入力してください",
            Font = new Font("Meiryo UI", 11F)
        };
        form.Controls.Add(promptLabel);
        currentTop = promptLabel.Bottom + 16;

        // ボタン領域 (A-Z)
        int startX = sideMargin;
        int buttonY = currentTop;
        int buttonWidth = 24;
        int buttonHeight = 26;

        // 実際の利用可能ドライブを取得
        var readyDrives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => d.Name.Substring(0, 1).ToUpper())
            .OrderBy(d => d)
            .ToList();

        // 現在ドライブ文字を特定（ハイライト用）
        string currentDriveLetter = defaultPath.Length > 0 ? defaultPath.Substring(0, 1).ToUpper() : "";

        int buttonIndex = 0;
        int lastButtonBottom = buttonY + buttonHeight;
        foreach (string dStr in readyDrives)
        {
            char c = dStr[0];
            bool isCurrent = (c.ToString().ToUpper() == currentDriveLetter);

            Button drvBtn = new Button()
            {
                Text = c.ToString(),
                Left = startX + (buttonIndex * (buttonWidth + 2)),
                Top = buttonY,
                Width = buttonWidth,
                Height = buttonHeight,
                Font = new Font("Consolas", isCurrent ? 9F : 10F, isCurrent ? FontStyle.Bold : FontStyle.Regular),
                FlatStyle = isCurrent ? FlatStyle.Flat : FlatStyle.System,
                UseVisualStyleBackColor = false,
                BackColor = isCurrent ? Color.SteelBlue : SystemColors.Control,
                ForeColor = isCurrent ? Color.White : SystemColors.ControlText
            };

            form.Controls.Add(drvBtn);
            buttonIndex++;
            lastButtonBottom = Math.Max(lastButtonBottom, drvBtn.Bottom);
        }
        currentTop = lastButtonBottom + 16;

        ComboBox inputBox = new ComboBox()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Font = new Font("Consolas", 11F),
            DropDownStyle = ComboBoxStyle.DropDown,
            Text = defaultPath,
            MaxDropDownItems = Helpers.LogdiskDropdownLayoutPolicy.MaxVisibleRows,
            IntegralHeight = true
        };

        var normalizedHistory = new List<string>();
        if (history != null)
        {
            string normDefault = (defaultPath ?? "").TrimEnd('\\', '/');
            foreach (var h in history)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                string normH = h.TrimEnd('\\', '/');
                if (string.Equals(normH, normDefault, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                normalizedHistory.Add(h);
            }
        }

        foreach (var path in normalizedHistory)
        {
            inputBox.Items.Add(path);
        }

        form.Controls.Add(inputBox);
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

        void OpenNativeHistoryDropdown()
        {
            completionController?.CloseCompletionPopup();
            SyncHistoryIndexFromText();
            inputBox.DroppedDown = true;
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
                    ShowOnTextChanged = false,
                    UseNativeHistoryDropdown = true
                });

            inputBox.KeyDown += (s, e) =>
            {
                if (e.Handled || e.SuppressKeyPress)
                {
                    return;
                }

                if (Helpers.LogdiskDropdownLayoutPolicy.IsNativeHistoryShortcut(e.KeyData))
                {
                    OpenNativeHistoryDropdown();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
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

                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
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
                            ? 0
                            : Math.Max(historyIndex - 1, 0);
                    }

                    ApplyHistorySelection(historyIndex);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            currentTop = inputBox.Bottom;

            // ボタンのクリックイベントを修正
            foreach (Control ctrl in form.Controls)
            {
                if (ctrl is Button drvBtn && drvBtn.Text.Length == 1)
                {
                    char c = drvBtn.Text[0];
                    drvBtn.Click += (s, e) => inputBox.Text = $"{c}:\\";
                }
            }

            Button btnOk = new Button()
            {
                Text = "OK(O)",
                DialogResult = DialogResult.OK,
                Font = new Font("Meiryo UI", 10F),
                MinimumSize = new Size(100, 30)
            };

            Button btnCancel = new Button()
            {
                Text = "キャンセル(C)",
                DialogResult = DialogResult.Cancel,
                Font = new Font("Meiryo UI", 10F),
                MinimumSize = new Size(120, 30)
            };

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

            form.Shown += (s, e) =>
            {
                Rectangle workingArea = Screen.FromControl(form).WorkingArea;
                int itemHeight = Math.Max(inputBox.ItemHeight, inputBox.Font.Height + 4);
                inputBox.DropDownHeight = Helpers.LogdiskDropdownLayoutPolicy.CalculateDropDownHeight(
                    itemHeight,
                    workingArea.Bottom,
                    inputBox.PointToScreen(new Point(0, inputBox.Height)).Y);

                // 入力欄にフォーカス（全選択状態で）
                inputBox.SelectAll();
                inputBox.Focus();
            };

            form.FormClosed += (s, e) =>
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
