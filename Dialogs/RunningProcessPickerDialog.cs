using System.Diagnostics;

namespace MidFD.Dialogs;

public sealed class RunningProcessPickerDialog : Form
{
    private sealed record RunningProcessItem(string ProcessName, string WindowTitle, string ExecutablePath);

    private readonly ListView _listView;

    public string? SelectedExecutablePath { get; private set; }

    private RunningProcessPickerDialog()
    {
        Text = "実行中から選択";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(860, 420);

        var hintLabel = new Label
        {
            Left = 16,
            Top = 16,
            Width = 828,
            Height = 34,
            Text = "実行ファイルパスを取得できたプロセスだけを一覧します。アクセス不可のプロセスは自動的に除外します。"
        };

        _listView = new ListView
        {
            Left = 16,
            Top = hintLabel.Bottom + 8,
            Width = 828,
            Height = 318,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false
        };
        _listView.Columns.Add("プロセス名", 180);
        _listView.Columns.Add("ウィンドウタイトル", 220);
        _listView.Columns.Add("実行ファイルパス", 400);

        var okButton = new Button
        {
            Left = 674,
            Top = _listView.Bottom + 12,
            Width = 80,
            Height = 30,
            Text = "OK",
            DialogResult = DialogResult.OK
        };
        var cancelButton = new Button
        {
            Left = 764,
            Top = okButton.Top,
            Width = 80,
            Height = 30,
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };

        Controls.Add(hintLabel);
        Controls.Add(_listView);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Load += (_, _) => PopulateProcessList();
        Shown += (_, _) =>
        {
            if (_listView.Items.Count > 0)
            {
                _listView.Items[0].Selected = true;
                _listView.Select();
            }
        };
        _listView.DoubleClick += (_, _) =>
        {
            if (_listView.SelectedItems.Count == 0)
            {
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };
        FormClosing += OnFormClosing;
    }

    public static string? ShowPicker(IWin32Window owner)
    {
        using var dialog = new RunningProcessPickerDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.SelectedExecutablePath
            : null;
    }

    private void PopulateProcessList()
    {
        var items = new List<RunningProcessItem>();

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string executablePath = process.MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    continue;
                }

                items.Add(new RunningProcessItem(
                    process.ProcessName,
                    process.MainWindowTitle,
                    executablePath));
            }
            catch
            {
                // 権限不足などで path が取れないプロセスは quietly skip する。
            }
            finally
            {
                process.Dispose();
            }
        }

        var deduplicatedItems = items
            .GroupBy(static item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static item => !string.IsNullOrWhiteSpace(item.WindowTitle))
                .First())
            .ToList();

        foreach (RunningProcessItem item in deduplicatedItems
                     .OrderBy(static item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.WindowTitle, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase))
        {
            var listViewItem = new ListViewItem(item.ProcessName);
            listViewItem.SubItems.Add(item.WindowTitle);
            listViewItem.SubItems.Add(item.ExecutablePath);
            listViewItem.Tag = item;
            _listView.Items.Add(listViewItem);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }

        if (_listView.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "実行中プロセスを選択してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
            return;
        }

        if (_listView.SelectedItems[0].Tag is not RunningProcessItem item)
        {
            MessageBox.Show(this, "実行ファイルパスを取得できませんでした。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }

        SelectedExecutablePath = item.ExecutablePath;
    }
}
