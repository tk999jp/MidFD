using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public class QuickAccessDialog : Form
{
    private readonly TabControl _tabControl;
    private readonly TextBox _queryTextBox;
    private readonly ListView _registeredListView;
    private readonly ListView _recentListView;
    private readonly ListView _historyListView;
    private readonly Button _okButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _addButton;
    private readonly Button _editButton;
    private readonly Button _deleteButton;
    private readonly Label _tabDescriptionLabel;
    private readonly Label _summaryLabel;
    private readonly Label _hintLabel;
    private readonly string _currentPath;
    private readonly QuickAccessStore _workingStore;
    private readonly IReadOnlyList<QuickAccessEntry> _historyEntries;

    public string? SelectedPath { get; private set; }
    public QuickAccessEntry? SelectedEntry { get; private set; }
    public QuickAccessStore UpdatedStore => _workingStore.Clone();
    public QuickAccessDialogCloseAction CloseAction { get; private set; } = QuickAccessDialogCloseAction.Cancel;

    public QuickAccessDialog(QuickAccessStore store, string currentPath, IReadOnlyList<QuickAccessEntry> historyEntries)
    {
        const int sideMargin = 16;
        const int topMargin = 8;
        _workingStore = store.Clone();
        _currentPath = currentPath;
        _historyEntries = historyEntries;

        Text = "QuickAccess";
        ClientSize = new Size(920, 690);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(820, 600);

        int contentWidth = ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        _tabDescriptionLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 32,
            ForeColor = SystemColors.ControlText,
            AutoEllipsis = true
        };
        Controls.Add(_tabDescriptionLabel);
        currentTop = _tabDescriptionLabel.Bottom + 4;

        var queryLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 120,
            Height = 20,
            Text = "絞り込み",
            AutoSize = true
        };
        Controls.Add(queryLabel);
        currentTop = queryLabel.Bottom + 1;

        _queryTextBox = new TextBox
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 24
        };
        Controls.Add(_queryTextBox);
        currentTop = _queryTextBox.Bottom + 6;

        _tabControl = new TabControl
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 350
        };
        _tabControl.TabPages.Add("登録先");
        _tabControl.TabPages.Add("最近");
        _tabControl.TabPages.Add("履歴");
        _tabControl.SelectedIndexChanged += (_, _) =>
        {
            EnsureSelection(GetActiveListView());
            BeginInvoke(new Action(FocusActiveListView));
            UpdateContextText();
            UpdateButtonState();
            UpdateSummaryText();
        };
        Controls.Add(_tabControl);
        currentTop = _tabControl.Bottom + 6;

        _registeredListView = CreateListView();
        _recentListView = CreateListView();
        _historyListView = CreateListView();

        _tabControl.TabPages[0].Controls.Add(_registeredListView);
        _tabControl.TabPages[1].Controls.Add(_recentListView);
        _tabControl.TabPages[2].Controls.Add(_historyListView);

        // 中段ボタン (FlowLayoutPanel で管理して見切れ防止)
        var middleButtonPanel = new FlowLayoutPanel
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 30,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _addButton = new Button
        {
            Text = "登録を追加...",
            Width = 120,
            Height = 30,
            Margin = new Padding(0, 0, 4, 0)
        };
        _addButton.Click += AddEntry;

        _editButton = new Button
        {
            Text = "選択を編集",
            Width = 104,
            Height = 30,
            Margin = new Padding(0, 0, 4, 0)
        };
        _editButton.Click += EditSelected;

        _deleteButton = new Button
        {
            Text = "登録先を削除",
            Width = 110,
            Height = 30,
            Margin = new Padding(0, 0, 0, 0)
        };
        _deleteButton.Click += DeleteSelected;

        middleButtonPanel.Controls.Add(_addButton);
        middleButtonPanel.Controls.Add(_editButton);
        middleButtonPanel.Controls.Add(_deleteButton);
        Controls.Add(middleButtonPanel);
        currentTop = middleButtonPanel.Bottom + 6;

        _hintLabel = new Label
        {
            Text = GetBottomHintText(),
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 26,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true
        };
        Controls.Add(_hintLabel);
        currentTop = _hintLabel.Bottom + 1;

        _summaryLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 16,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true
        };
        Controls.Add(_summaryLabel);
        currentTop = _summaryLabel.Bottom;

        _okButton = new Button
        {
            Text = "移動",
            DialogResult = DialogResult.OK,
            MinimumSize = new Size(80, 30)
        };
        _okButton.Click += (_, _) => ConfirmSelection();

        _saveButton = new Button
        {
            Text = "閉じる",
            DialogResult = DialogResult.OK,
            MinimumSize = new Size(80, 30)
        };
        _saveButton.Click += (_, _) => ConfirmSaveOnly();

        _cancelButton = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            MinimumSize = new Size(80, 30)
        };
        _cancelButton.Click += (_, _) => CancelDialog();

        Controls.Add(_okButton);
        Controls.Add(_saveButton);
        Controls.Add(_cancelButton);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            this,
            new[] { _okButton, _saveButton, _cancelButton },
            currentTop,
            buttonGap: 12,
            contentGap: 12);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        KeyDown += QuickAccessDialog_KeyDown;
        _queryTextBox.TextChanged += (_, _) => RefreshItems();
        _queryTextBox.KeyDown += QueryTextBox_KeyDown;

        RefreshItems();
        _tabControl.SelectedIndex = 0;
        EnsureSelection(_registeredListView);
        UpdateContextText();
        UpdateButtonState();
        Shown += (_, _) => BeginInvoke(new Action(FocusSearchBox));
    }

    private ListView CreateListView()
    {
        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            ShowItemToolTips = true,
            BackColor = Color.Black,
            ForeColor = Color.Cyan
        };
        listView.Columns.Add("表示名", 220);
        listView.Columns.Add("移動先", 340);
        listView.Columns.Add("区分", 90);
        listView.Columns.Add("状態", 180);
        listView.DoubleClick += (_, _) => ConfirmSelection();
        listView.SelectedIndexChanged += (_, _) => UpdateButtonState();
        return listView;
    }

    private void QuickAccessDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            CancelDialog();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.Tab)
        {
            int nextIndex = e.Shift
                ? (_tabControl.SelectedIndex + _tabControl.TabPages.Count - 1) % _tabControl.TabPages.Count
                : (_tabControl.SelectedIndex + 1) % _tabControl.TabPages.Count;
            _tabControl.SelectedIndex = nextIndex;
            e.Handled = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.F)
        {
            FocusSearchBox();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Insert)
        {
            AddEntry(null, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.N)
        {
            AddEntry(null, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F4)
        {
            EditSelected(null, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            DeleteSelected(null, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void QueryTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Tab)
        {
            int nextIndex = e.Shift
                ? (_tabControl.SelectedIndex + _tabControl.TabPages.Count - 1) % _tabControl.TabPages.Count
                : (_tabControl.SelectedIndex + 1) % _tabControl.TabPages.Count;
            _tabControl.SelectedIndex = nextIndex;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Down && GetActiveListView().Items.Count > 0)
        {
            FocusActiveListView();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            ConfirmSelection();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void RefreshItems()
    {
        string selectedPath = GetSelectedEntry()?.Path ?? string.Empty;
        string query = _queryTextBox.Text;

        IReadOnlyList<QuickAccessEntry> registeredEntries = QuickAccessService.FilterEntries(QuickAccessService.GetRegisteredEntries(_workingStore), query);
        IReadOnlyList<QuickAccessEntry> recentEntries = QuickAccessService.FilterEntries(QuickAccessService.GetRecentEntries(_workingStore), query);
        IReadOnlyList<QuickAccessEntry> historyEntries = QuickAccessService.FilterEntries(QuickAccessService.GetHistoryEntries(_historyEntries), query);

        RefreshList(_registeredListView, registeredEntries);
        RefreshList(_recentListView, recentEntries);
        RefreshList(_historyListView, historyEntries);

        UpdateTabText(registeredEntries.Count, recentEntries.Count, historyEntries.Count);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            SelectPath(GetActiveListView(), selectedPath);
        }

        UpdateSummaryText();
    }

    private void RefreshList(ListView listView, IReadOnlyList<QuickAccessEntry> entries)
    {
        listView.BeginUpdate();
        listView.Items.Clear();

        foreach (QuickAccessEntry entry in entries)
        {
            var item = new ListViewItem(entry.DisplayName);
            item.SubItems.Add(QuickAccessService.GetEntryValueLabel(entry));
            item.SubItems.Add(QuickAccessService.GetEntryKindLabel(entry));
            item.SubItems.Add(QuickAccessService.GetEntryStatusLabel(entry, _currentPath));
            item.ToolTipText = QuickAccessService.GetEntryTooltipText(entry, _currentPath);
            item.Tag = entry;
            string status = QuickAccessService.GetEntryStatusLabel(entry, _currentPath);
            if (status.StartsWith("見つからない", StringComparison.Ordinal))
            {
                item.ForeColor = Color.DarkSalmon;
            }
            else if (status.StartsWith("現在地", StringComparison.Ordinal))
            {
                item.ForeColor = Color.LightGreen;
            }

            listView.Items.Add(item);
        }

        listView.EndUpdate();
    }

    private ListView GetActiveListView()
    {
        return _tabControl.SelectedIndex switch
        {
            1 => _recentListView,
            2 => _historyListView,
            _ => _registeredListView
        };
    }

    private void FocusActiveListView()
    {
        ListView listView = GetActiveListView();
        EnsureSelection(listView);
        listView.Select();
        listView.Focus();
    }

    private void FocusSearchBox()
    {
        _queryTextBox.Focus();
        _queryTextBox.SelectAll();
    }

    private void EnsureSelection(ListView listView)
    {
        if (listView.Items.Count == 0 || listView.SelectedItems.Count > 0)
        {
            return;
        }

        listView.Items[0].Selected = true;
        listView.Items[0].Focused = true;
        listView.Items[0].EnsureVisible();
    }

    private void UpdateButtonState()
    {
        QuickAccessEntry? entry = GetSelectedEntry();
        _okButton.Enabled = entry != null;
        _saveButton.Enabled = true;
        bool canEdit = entry?.Kind == QuickAccessEntryKind.Bookmark || entry?.Kind == QuickAccessEntryKind.Alias;
        _editButton.Enabled = canEdit;
        _deleteButton.Enabled = canEdit;
        _okButton.Text = "移動";
        _addButton.Text = _tabControl.SelectedIndex == 0 ? "登録を追加..." : "選択を登録...";
        UpdateSummaryText();
    }

    private void UpdateContextText()
    {
        _hintLabel.Text = GetBottomHintText();
        _tabDescriptionLabel.Text = _tabControl.SelectedIndex switch
        {
            1 => "最近: 起動直後は絞り込みへ入力できます。状態列を見ながら、そのまま移動または登録できます。",
            2 => "履歴: 起動直後は絞り込みへ入力できます。状態列で戻る候補 / 進む候補を見ながら移動または登録できます。",
            _ => "登録先: 起動直後は絞り込みへ入力できます。状態列で現在地 / 移動可 / 見つからないと、外部コマンドの対象 / 実行可否を確認できます。"
        };
    }

    private void UpdateSummaryText()
    {
        ListView listView = GetActiveListView();
        int visibleCount = listView.Items.Count;
        string tabName = _tabControl.SelectedIndex switch
        {
            1 => "最近",
            2 => "履歴",
            _ => "登録先"
        };
        string stateGuide = _tabControl.SelectedIndex == 2
            ? "戻る候補・進む候補・現在地・見つからない"
            : "現在地・移動可・見つからない";
        _summaryLabel.Text = $"表示 {visibleCount} 件 / {tabName} / 状態: {stateGuide}";
    }

    private string GetBottomHintText()
    {
        return _tabControl.SelectedIndex switch
        {
            1 => "Enter=移動 / ↓=一覧 / Insert,N=選択を登録 / Ctrl+Tab=タブ切替",
            2 => "Enter=移動 / ↓=一覧 / Insert,N=選択を登録 / Ctrl+Tab=タブ切替",
            _ => "Enter=移動 / ↓=一覧 / Insert,N=登録 / F4=編集 / Delete=削除 / Ctrl+Tab=タブ切替"
        };
    }

    private void UpdateTabText(int registeredCount, int recentCount, int historyCount)
    {
        _tabControl.TabPages[0].Text = $"登録先 ({registeredCount})";
        _tabControl.TabPages[1].Text = $"最近 ({recentCount})";
        _tabControl.TabPages[2].Text = $"履歴 ({historyCount})";
    }

    private QuickAccessEntry? GetSelectedEntry()
    {
        ListView listView = GetActiveListView();
        if (listView.SelectedItems.Count == 0)
        {
            return null;
        }

        return listView.SelectedItems[0].Tag as QuickAccessEntry;
    }

    private void AddEntry(object? sender, EventArgs e)
    {
        QuickAccessEntry? seedEntry = GetSelectedEntry();
        string initialPath = _currentPath;
        string initialDisplayName = QuickAccessService.CreateDisplayName(_currentPath);
        string? initialCategoryName = null;
        if (_tabControl.SelectedIndex != 0 && seedEntry != null && !string.IsNullOrWhiteSpace(seedEntry.Path))
        {
            initialPath = seedEntry.Path;
            initialDisplayName = QuickAccessService.CreateDisplayName(seedEntry.Path);
            initialCategoryName = seedEntry.CategoryName;
        }

        QuickAccessLocationDialogResult? dialogResult = QuickAccessLocationDialog.ShowEditor(
            this,
            "QuickAccess 登録",
            _currentPath,
            initialPath,
            initialDisplayName,
            initialCategoryName,
            QuickAccessService.GetKnownCategoryNames(_workingStore),
            initialUseForTabTitle: false);
        if (dialogResult == null)
        {
            return;
        }

        if (QuickAccessService.TrySaveManagedLocationEntry(
            _workingStore,
            null,
            dialogResult.DisplayName,
            dialogResult.Path,
            dialogResult.CategoryName,
            dialogResult.UseForTabTitle,
            _currentPath,
            out string normalizedPath,
            out string message))
        {
            RefreshItems();
            _tabControl.SelectedIndex = 0;
            SelectPath(_registeredListView, normalizedPath);
        }
        else
        {
            MessageBox.Show(this, message, "QuickAccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void EditSelected(object? sender, EventArgs e)
    {
        QuickAccessEntry? entry = GetSelectedEntry();
        if (entry == null)
        {
            return;
        }

        if (entry.Kind != QuickAccessEntryKind.Bookmark && entry.Kind != QuickAccessEntryKind.Alias)
        {
            MessageBox.Show(this, "登録先タブの項目だけ編集できます。", "QuickAccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        QuickAccessLocationDialogResult? dialogResult = QuickAccessLocationDialog.ShowEditor(
            this,
            "QuickAccess 編集",
            _currentPath,
            entry.Path,
            entry.DisplayName,
            entry.CategoryName,
            QuickAccessService.GetKnownCategoryNames(_workingStore),
            initialUseForTabTitle: entry.Kind == QuickAccessEntryKind.Alias);
        if (dialogResult == null)
        {
            return;
        }

        if (QuickAccessService.TrySaveManagedLocationEntry(
            _workingStore,
            entry,
            dialogResult.DisplayName,
            dialogResult.Path,
            dialogResult.CategoryName,
            dialogResult.UseForTabTitle,
            _currentPath,
            out string normalizedPath,
            out string message))
        {
            RefreshItems();
            _tabControl.SelectedIndex = 0;
            SelectPath(_registeredListView, normalizedPath);
        }
        else
        {
            MessageBox.Show(this, message, "QuickAccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void DeleteSelected(object? sender, EventArgs e)
    {
        QuickAccessEntry? entry = GetSelectedEntry();
        if (entry == null)
        {
            return;
        }

        if (entry.Kind != QuickAccessEntryKind.Bookmark && entry.Kind != QuickAccessEntryKind.Alias)
        {
            MessageBox.Show(this, "最近タブと履歴タブの項目は runtime 表示です。この画面では削除できません。", "QuickAccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (QuickAccessService.RemoveManagedEntry(_workingStore, entry))
        {
            RefreshItems();
            _tabControl.SelectedIndex = 0;
            EnsureSelection(_registeredListView);
            UpdateButtonState();
        }
    }

    private void SelectPath(ListView listView, string path)
    {
        bool matched = false;
        foreach (ListViewItem item in listView.Items)
        {
            if (item.Tag is QuickAccessEntry entry && QuickAccessService.PathsEqual(entry.Path, path))
            {
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            EnsureSelection(listView);
        }

        UpdateButtonState();
    }

    private void ConfirmSelection()
    {
        QuickAccessEntry? entry = GetSelectedEntry();
        if (entry == null)
        {
            return;
        }

        CloseAction = QuickAccessDialogCloseAction.Navigate;
        SelectedEntry = entry.Clone();
        SelectedPath = entry.Path;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ConfirmSaveOnly()
    {
        CloseAction = QuickAccessDialogCloseAction.SaveOnly;
        SelectedEntry = null;
        SelectedPath = null;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CancelDialog()
    {
        CloseAction = QuickAccessDialogCloseAction.Cancel;
        SelectedEntry = null;
        SelectedPath = null;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    public static QuickAccessDialogResult Show(
        IWin32Window owner,
        QuickAccessStore store,
        string currentPath,
        IReadOnlyList<QuickAccessEntry> historyEntries)
    {
        using var dialog = new QuickAccessDialog(store, currentPath, historyEntries);
        if (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            return new QuickAccessDialogResult(dialog.CloseAction, dialog.SelectedEntry, dialog.UpdatedStore);
        }

        return new QuickAccessDialogResult(QuickAccessDialogCloseAction.Cancel, null, null);
    }
}

public enum QuickAccessDialogCloseAction
{
    Cancel,
    SaveOnly,
    Navigate
}

public sealed record QuickAccessDialogResult(
    QuickAccessDialogCloseAction Action,
    QuickAccessEntry? SelectedEntry,
    QuickAccessStore? UpdatedStore);
