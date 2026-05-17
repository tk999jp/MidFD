using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class WorkspaceSnapshotDialog : Form
{
    private readonly Func<IReadOnlyList<WorkspaceSnapshotEntry>> _loadEntries;
    private readonly Func<IWin32Window, bool> _saveCurrentWorkspace;
    private readonly Func<IWin32Window, WorkspaceSnapshotEntry, bool> _restoreSnapshot;
    private readonly Func<IWin32Window, WorkspaceSnapshotEntry, bool> _renameSnapshot;
    private readonly Func<IWin32Window, WorkspaceSnapshotEntry, bool> _deleteSnapshot;
    private readonly Func<IWin32Window, WorkspaceSnapshotEntry, bool> _exportSnapshot;
    private readonly Func<IWin32Window, bool> _importSnapshot;
    private readonly Func<IWin32Window, bool> _exportAllSnapshots;
    private readonly Func<IWin32Window, bool> _importAllSnapshots;
    private readonly ListView _listView = new();

    public WorkspaceSnapshotDialog(
        Func<IReadOnlyList<WorkspaceSnapshotEntry>> loadEntries,
        Func<IWin32Window, bool> saveCurrentWorkspace,
        Func<IWin32Window, WorkspaceSnapshotEntry, bool> restoreSnapshot,
        Func<IWin32Window, WorkspaceSnapshotEntry, bool> renameSnapshot,
        Func<IWin32Window, WorkspaceSnapshotEntry, bool> deleteSnapshot,
        Func<IWin32Window, WorkspaceSnapshotEntry, bool> exportSnapshot,
        Func<IWin32Window, bool> importSnapshot,
        Func<IWin32Window, bool> exportAllSnapshots,
        Func<IWin32Window, bool> importAllSnapshots)
    {
        _loadEntries = loadEntries;
        _saveCurrentWorkspace = saveCurrentWorkspace;
        _restoreSnapshot = restoreSnapshot;
        _renameSnapshot = renameSnapshot;
        _deleteSnapshot = deleteSnapshot;
        _exportSnapshot = exportSnapshot;
        _importSnapshot = importSnapshot;
        _exportAllSnapshots = exportAllSnapshots;
        _importAllSnapshots = importAllSnapshots;

        Text = "Workspace スナップショット";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(860, 460); // 高さを少し広げる
        AutoScaleMode = AutoScaleMode.Font;

        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.MultiSelect = false;
        _listView.HideSelection = false;
        _listView.SetBounds(12, 12, 836, 330);
        _listView.Columns.Add("名前", 180);
        _listView.Columns.Add("作成日時", 130);
        _listView.Columns.Add("更新日時", 130);
        _listView.Columns.Add("カテゴリ", 70);
        _listView.Columns.Add("タブ", 60);
        _listView.Columns.Add("マーク", 70);
        _listView.Columns.Add("アクティブパス", 180);
        _listView.DoubleClick += (_, _) =>
        {
            WorkspaceSnapshotEntry? selected = GetSelectedEntry();
            if (selected != null && _restoreSnapshot(this, selected))
            {
                RefreshEntries();
            }
        };

        var saveButton = new Button { Text = "現在のWorkspaceを保存...", Width = 170, Height = 30, Left = 12, Top = 355 };
        saveButton.Click += (_, _) =>
        {
            if (_saveCurrentWorkspace(this))
            {
                RefreshEntries();
            }
        };

        var restoreButton = new Button { Text = "復元...", Width = 90, Height = 30, Left = 190, Top = 355 };
        restoreButton.Click += (_, _) =>
        {
            WorkspaceSnapshotEntry? selected = GetSelectedEntry();
            if (selected != null && _restoreSnapshot(this, selected))
            {
                RefreshEntries();
            }
        };

        var renameButton = new Button { Text = "名前変更...", Width = 100, Height = 30, Left = 288, Top = 355 };
        renameButton.Click += (_, _) =>
        {
            WorkspaceSnapshotEntry? selected = GetSelectedEntry();
            if (selected != null && _renameSnapshot(this, selected))
            {
                RefreshEntries();
            }
        };

        var deleteButton = new Button { Text = "削除...", Width = 90, Height = 30, Left = 396, Top = 355 };
        deleteButton.Click += (_, _) =>
        {
            WorkspaceSnapshotEntry? selected = GetSelectedEntry();
            if (selected != null && _deleteSnapshot(this, selected))
            {
                RefreshEntries();
            }
        };

        var exportButton = new Button { Text = "エクスポート...", Width = 110, Height = 30, Left = 12, Top = 400 };
        exportButton.Click += (_, _) =>
        {
            WorkspaceSnapshotEntry? selected = GetSelectedEntry();
            if (selected != null) _exportSnapshot(this, selected);
        };

        var importButton = new Button { Text = "インポート...", Width = 110, Height = 30, Left = 128, Top = 400 };
        importButton.Click += (_, _) =>
        {
            if (_importSnapshot(this)) RefreshEntries();
        };

        var exportAllButton = new Button { Text = "一括バックアップ...", Width = 140, Height = 30, Left = 246, Top = 400 };
        exportAllButton.Click += (_, _) =>
        {
            _exportAllSnapshots(this);
        };

        var importAllButton = new Button { Text = "一括インポート...", Width = 130, Height = 30, Left = 394, Top = 400 };
        importAllButton.Click += (_, _) =>
        {
            if (_importAllSnapshots(this)) RefreshEntries();
        };

        var closeButton = new Button { Text = "閉じる", Width = 90, Height = 30, Left = 758, Top = 400, DialogResult = DialogResult.Cancel };

        Controls.AddRange(new Control[] {
            _listView, saveButton, restoreButton, renameButton, deleteButton,
            exportButton, importButton, exportAllButton, importAllButton, closeButton
        });
        CancelButton = closeButton;
        RefreshEntries();
    }

    private void RefreshEntries()
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();
        try
        {
            foreach (WorkspaceSnapshotEntry entry in _loadEntries())
            {
                var item = new ListViewItem(entry.Name);
                item.SubItems.Add(entry.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(entry.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(entry.CategoryCount.ToString());
                item.SubItems.Add(entry.TabCount.ToString());
                item.SubItems.Add(entry.MarkedCount.ToString());
                item.SubItems.Add(entry.ActivePath);
                item.Tag = entry;
                _listView.Items.Add(item);
            }
        }
        finally
        {
            _listView.EndUpdate();
        }

        if (_listView.Items.Count > 0)
        {
            _listView.Items[0].Selected = true;
        }
    }

    private WorkspaceSnapshotEntry? GetSelectedEntry()
    {
        return _listView.SelectedItems.Count > 0 ? _listView.SelectedItems[0].Tag as WorkspaceSnapshotEntry : null;
    }
}
