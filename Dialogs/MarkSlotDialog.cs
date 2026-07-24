using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class MarkSlotDialog : Form
{
    private sealed class CurrentMarkRowState
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsInCurrentDirectory { get; set; }
        public bool Exists { get; set; }
        public bool IsMarked { get; set; }
    }

    public sealed record MarkListViewItem(
        string Name,
        string FullPath,
        bool IsInCurrentDirectory,
        bool Exists);

    public sealed record MarkSlotSummaryViewItem(
        int SlotNumber,
        string DisplayName,
        int Count,
        DateTime? SavedAtLocal,
        string SourceScopeLabel,
        string? SourceCategoryName,
        string? SourceTabDisplayName,
        bool IsLegacySource);

    public sealed record MarkGlobalSummary(
        int ActiveTabMarkCount,
        int CurrentCategoryMarkCount,
        int CurrentCategoryTabCount,
        string CurrentCategoryName,
        int GlobalMarkCount,
        int GlobalCategoryCount,
        int GlobalTabCount);

    private readonly Func<IReadOnlyList<MarkListViewItem>> _markItemsProvider;
    private readonly Func<IReadOnlyList<MarkSlotSummaryViewItem>> _slotItemsProvider;
    private readonly Func<int, IReadOnlyList<MarkListViewItem>> _slotItemsDetailProvider;
    private readonly Func<string> _markPersistenceSummaryProvider;
    private readonly Func<IReadOnlyList<string>, string> _toggleCurrentMarksAction;
    private readonly Action<string> _navigateToMarkedItemAction;
    private readonly Func<int, string?, string> _saveSlotAction;
    private readonly Func<int, string> _saveCategorySlotAction;
    private readonly Func<int, string> _saveWorkspaceSlotAction;
    private readonly Action<int> _openSlotSetOperationAction;
    private readonly bool _allowSlotSetOperation;
    private readonly Func<int, string> _exportSlotAction;
    private readonly Func<int, string> _importSlotAction;
    private readonly Func<string> _exportAllSlotsAction;
    private readonly Func<string> _importAllSlotsAction;
    private readonly bool _allowSlotBackupTransfer;
    private readonly Func<int, MarkSlotActionResult> _restoreSlotAction;
    private readonly Func<int, string?, string> _renameSlotAction;
    private readonly Func<int, string> _deleteSlotAction;
    private readonly Func<int, IReadOnlyCollection<string>, MarkSlotActionResult> _removeSlotItemsAction;
    private readonly Func<MarkGlobalSummary> _globalSummaryProvider;
    private readonly Action _clearCategoryMarksAction;
    private readonly Action _clearGlobalMarksAction;
    private readonly Action _clearCurrentTabMarksAction;
    private readonly Func<MarkSlotClipboardActionResult>? _importClipboardMarkAction;
    private readonly Action<MarkSlotClipboardActionResult> _showImportResultAction;
    private readonly Action<int>? _openManagementDialogAction;
    private readonly Action<string, string, MessageBoxIcon> _showMessageAction;

    private readonly Label _topSummaryLabel;
    private readonly Button? _importClipboardButton;

    private readonly SplitContainer _mainSplitContainer;
    private readonly SplitContainer _rightSplitContainer;

    private readonly ListView _slotListView;
    private readonly ListView _currentMarkListView;
    private readonly ListView _slotContentListView;

    private readonly Label _currentMarksHeaderLabel;
    private readonly Button _markOperationsButton;
    private readonly ContextMenuStrip _markOperationsMenu;

    private readonly Label _slotContentHeaderLabel;

    private readonly Button _saveToSlotButton;
    private readonly Button _restoreFromSlotButton;
    private readonly Button _manageButton;
    private readonly Button _closeButton;

    private readonly List<CurrentMarkRowState> _currentMarkRows = new();
    private int _initialSelectedSlotNumber = 1;

    public MarkSlotDialog(
        Func<IReadOnlyList<MarkListViewItem>> markItemsProvider,
        Func<IReadOnlyList<MarkSlotSummaryViewItem>> slotItemsProvider,
        Func<int, IReadOnlyList<MarkListViewItem>> slotItemsDetailProvider,
        Func<string> markPersistenceSummaryProvider,
        Func<IReadOnlyList<string>, string> toggleCurrentMarksAction,
        Action<string> navigateToMarkedItemAction,
        Func<int, string?, string> saveSlotAction,
        Func<int, string> saveCategorySlotAction,
        Func<int, string> saveWorkspaceSlotAction,
        Action<int> openSlotSetOperationAction,
        bool allowSlotSetOperation,
        Func<int, string> exportSlotAction,
        Func<int, string> importSlotAction,
        Func<string> exportAllSlotsAction,
        Func<string> importAllSlotsAction,
        bool allowSlotBackupTransfer,
        Func<int, MarkSlotActionResult> restoreSlotAction,
        Func<int, string?, string> renameSlotAction,
        Func<int, string> deleteSlotAction,
        Func<int, IReadOnlyCollection<string>, MarkSlotActionResult> removeSlotItemsAction,
        Func<MarkGlobalSummary> globalSummaryProvider,
        Action clearCategoryMarksAction,
        Action clearGlobalMarksAction,
        Action clearCurrentTabMarksAction,
        Func<MarkSlotClipboardActionResult>? importClipboardMarkAction = null,
        Action<int>? openManagementDialogAction = null,
        Action<string, string, MessageBoxIcon>? showMessageAction = null,
        Action<MarkSlotClipboardActionResult>? showImportResultAction = null)
    {
        _markItemsProvider = markItemsProvider;
        _slotItemsProvider = slotItemsProvider;
        _slotItemsDetailProvider = slotItemsDetailProvider;
        _markPersistenceSummaryProvider = markPersistenceSummaryProvider;
        _toggleCurrentMarksAction = toggleCurrentMarksAction;
        _navigateToMarkedItemAction = navigateToMarkedItemAction;
        _saveSlotAction = saveSlotAction;
        _saveCategorySlotAction = saveCategorySlotAction;
        _saveWorkspaceSlotAction = saveWorkspaceSlotAction;
        _openSlotSetOperationAction = openSlotSetOperationAction;
        _allowSlotSetOperation = allowSlotSetOperation;
        _exportSlotAction = exportSlotAction;
        _importSlotAction = importSlotAction;
        _exportAllSlotsAction = exportAllSlotsAction;
        _importAllSlotsAction = importAllSlotsAction;
        _allowSlotBackupTransfer = allowSlotBackupTransfer;
        _restoreSlotAction = restoreSlotAction;
        _renameSlotAction = renameSlotAction;
        _deleteSlotAction = deleteSlotAction;
        _removeSlotItemsAction = removeSlotItemsAction;
        _globalSummaryProvider = globalSummaryProvider;
        _clearCategoryMarksAction = clearCategoryMarksAction;
        _clearGlobalMarksAction = clearGlobalMarksAction;
        _clearCurrentTabMarksAction = clearCurrentTabMarksAction;
        _importClipboardMarkAction = importClipboardMarkAction;
        _openManagementDialogAction = openManagementDialogAction;
        _showMessageAction = showMessageAction ?? ((msg, title, icon) => MessageBox.Show(this, msg, title, MessageBoxButtons.OK, icon));
        _showImportResultAction = showImportResultAction ?? (result =>
        {
            using var dialog = new MarkSlotImportResultDialog(result);
            dialog.ShowDialog(this);
        });

        Text = "マーク一覧 / スロット";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1120, 650);
        MinimumSize = new Size(900, 540);

        // 上部Summary
        _topSummaryLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            AutoEllipsis = true
        };

        if (_importClipboardMarkAction != null)
        {
            _importClipboardButton = new Button
            {
                Text = "RESULT取込 (Ctrl+M)",
                AutoSize = true,
                Anchor = AnchorStyles.Right
            };
            _importClipboardButton.Click += (_, _) => ExecuteImportClipboardToCurrentMarks();
        }

        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 5, 10, 5),
            ColumnCount = 2,
            RowCount = 1
        };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topPanel.Controls.Add(_topSummaryLabel, 0, 0);
        if (_importClipboardButton != null)
        {
            topPanel.Controls.Add(_importClipboardButton, 1, 0);
        }

        // ListView の作成
        _slotListView = CreateListView(new[]
        {
            ("Slot", 60),
            ("名前", 160),
            ("件数", 60),
            ("範囲", 100),
            ("保存日時", 140)
        });
        _slotListView.SelectedIndexChanged += (_, _) => OnSelectedSlotChanged();
        _slotListView.DoubleClick += (_, _) => RestoreSelectedSlot();

        _currentMarkListView = CreateListView(new[]
        {
            ("Mark", 60),
            ("種別", 60),
            ("名前", 160),
            ("場所", 260),
            ("状態", 90)
        });
        _currentMarkListView.DoubleClick += (_, _) => NavigateToSelectedMarkPath();

        _slotContentListView = CreateListView(new[]
        {
            ("種別", 60),
            ("名前", 160),
            ("場所", 300),
            ("状態", 90)
        });

        // 右上 ヘッダー (現在Mark)
        _currentMarksHeaderLabel = new Label
        {
            Text = "現在Mark（0件）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold)
        };

        _markOperationsMenu = new ContextMenuStrip();
        _markOperationsMenu.Items.Add("現在タブのMarkを解除...", null, (_, _) => ExecuteClearCurrentTabMarks());
        _markOperationsMenu.Items.Add("現在カテゴリ全タブのMarkを解除...", null, (_, _) => ExecuteClearCategoryMarks());
        _markOperationsMenu.Items.Add("Workspace全体のMarkを解除...", null, (_, _) => ExecuteClearWorkspaceMarks());

        _markOperationsButton = new Button
        {
            Text = "Mark操作 ▼",
            AutoSize = true,
            Anchor = AnchorStyles.Right
        };
        _markOperationsButton.Click += (s, _) =>
        {
            if (s is Control ctrl)
            {
                _markOperationsMenu.Show(ctrl, new Point(0, ctrl.Height));
            }
        };

        var currentMarkHeaderPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(2, 2, 2, 2),
            ColumnCount = 2,
            RowCount = 1
        };
        currentMarkHeaderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        currentMarkHeaderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        currentMarkHeaderPanel.Controls.Add(_currentMarksHeaderLabel, 0, 0);
        currentMarkHeaderPanel.Controls.Add(_markOperationsButton, 1, 0);

        var currentMarkContainer = new Panel { Dock = DockStyle.Fill };
        currentMarkContainer.Controls.Add(_currentMarkListView);
        currentMarkContainer.Controls.Add(currentMarkHeaderPanel);

        // 右下 ヘッダー (選択中スロット保存内容)
        _slotContentHeaderLabel = new Label
        {
            Text = "選択中スロット: なし",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            Padding = new Padding(2, 0, 0, 0)
        };

        var slotContentContainer = new Panel { Dock = DockStyle.Fill };
        slotContentContainer.Controls.Add(_slotContentListView);
        slotContentContainer.Controls.Add(_slotContentHeaderLabel);

        // 右側 SplitContainer (上下 50:50)
        _rightSplitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6
        };
        _rightSplitContainer.Panel1.Controls.Add(currentMarkContainer);
        _rightSplitContainer.Panel2.Controls.Add(slotContentContainer);

        // 左側スロットコンテナ
        var slotHeaderLabel = new Label
        {
            Text = "スロット一覧",
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            Padding = new Padding(4, 0, 0, 0)
        };
        var slotContainer = new Panel { Dock = DockStyle.Fill };
        slotContainer.Controls.Add(_slotListView);
        slotContainer.Controls.Add(slotHeaderLabel);

        // 主領域 SplitContainer (左右 42:58)
        _mainSplitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            Padding = new Padding(10, 0, 10, 0)
        };
        _mainSplitContainer.Panel1.Controls.Add(slotContainer);
        _mainSplitContainer.Panel2.Controls.Add(_rightSplitContainer);

        // Footer 専用 Panel
        _saveToSlotButton = new Button
        {
            Text = "現在Markを選択スロットへ保存...",
            AutoSize = true,
            Height = 32
        };
        _saveToSlotButton.Click += (_, _) => SaveCurrentMarksToSelectedSlot();

        _restoreFromSlotButton = new Button
        {
            Text = "選択スロットを現在Markへ復元",
            AutoSize = true,
            Height = 32
        };
        _restoreFromSlotButton.Click += (_, _) => RestoreSelectedSlot();

        _manageButton = new Button
        {
            Text = "管理...",
            Width = 90,
            Height = 32
        };
        _manageButton.Click += (_, _) => OpenManagementDialog();

        _closeButton = new Button
        {
            Text = "閉じる",
            Width = 90,
            Height = 32,
            DialogResult = DialogResult.Cancel
        };
        _closeButton.Click += (_, _) => Close();

        var helperLabel = new Label
        {
            Text = _importClipboardMarkAction != null
                ? "↑↓:slot選択  Enter:復元  Ctrl+S:保存  Ctrl+M:RESULT取込  Esc:閉じる"
                : "↑↓:slot選択  Enter:復元  Ctrl+S:保存  Esc:閉じる",
            Dock = DockStyle.Bottom,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray
        };

        var footerFlowPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(5, 4, 5, 0)
        };
        footerFlowPanel.Controls.Add(_saveToSlotButton);
        footerFlowPanel.Controls.Add(_restoreFromSlotButton);
        footerFlowPanel.Controls.Add(_manageButton);

        var footerRightPanel = new Panel
        {
            Dock = DockStyle.Right,
            Width = 100,
            Padding = new Padding(5, 4, 5, 0)
        };
        footerRightPanel.Controls.Add(_closeButton);

        var footerMainPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40
        };
        footerMainPanel.Controls.Add(footerFlowPanel);
        footerMainPanel.Controls.Add(footerRightPanel);

        var footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 65,
            Padding = new Padding(10, 0, 10, 5)
        };
        footerPanel.Controls.Add(footerMainPanel);
        footerPanel.Controls.Add(helperLabel);

        Controls.Add(_mainSplitContainer);
        Controls.Add(topPanel);
        Controls.Add(footerPanel);

        KeyDown += MarkSlotDialog_KeyDown;
        RefreshContents();
        Shown += (_, _) =>
        {
            _mainSplitContainer.SplitterDistance = (int)(_mainSplitContainer.Width * 0.42);
            _rightSplitContainer.SplitterDistance = _rightSplitContainer.Height / 2;
        };
    }

    private static ListView CreateListView((string Text, int Width)[] columns)
    {
        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BackColor = MidFDColors.ListNormalBack,
            ForeColor = MidFDColors.ListNormalFore
        };
        foreach (var col in columns)
        {
            listView.Columns.Add(col.Text, col.Width);
        }
        return listView;
    }

    private static Color GetMutedForeColor()
    {
        var baseFore = MidFDColors.ListNormalFore;
        return Color.FromArgb(140, baseFore.R, baseFore.G, baseFore.B);
    }

    internal void MarkSlotDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.M)
        {
            if (_importClipboardMarkAction != null)
            {
                ExecuteImportClipboardToCurrentMarks();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }
        else if (e.Control && e.KeyCode == Keys.S)
        {
            SaveCurrentMarksToSelectedSlot();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_markListViewContainsFocus())
        {
            if (e.KeyCode == Keys.Space)
            {
                ToggleCurrentMarksFromSelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                NavigateToSelectedMarkPath();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }
        else if (_slotListView.ContainsFocus)
        {
            if (e.KeyCode == Keys.Enter)
            {
                RestoreSelectedSlot();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }

        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    private bool _markListViewContainsFocus() => _currentMarkListView.ContainsFocus;

    public void RefreshContents()
    {
        RefreshTopSummary();
        RefreshSlotItems();
        RefreshCurrentMarkItems();
        OnSelectedSlotChanged();
    }

    private void RefreshTopSummary()
    {
        int markedCount = _currentMarkRows.Count(item => item.IsMarked);
        int currentDirCount = _currentMarkRows.Count(item => item.IsInCurrentDirectory);
        int outsideCount = _currentMarkRows.Count - currentDirCount;
        int missingCount = _currentMarkRows.Count(item => !item.Exists);

        _topSummaryLabel.Text = $"現在Mark: {markedCount}件 / 現在DIR内 {currentDirCount} / 外 {outsideCount} / 不在 {missingCount}";
        _currentMarksHeaderLabel.Text = $"現在Mark（{_currentMarkRows.Count}件）";
    }

    private void RefreshSlotItems()
    {
        int? previousSlot = GetSelectedSlotSummary()?.SlotNumber ?? _initialSelectedSlotNumber;
        _slotListView.BeginUpdate();
        _slotListView.Items.Clear();

        var slots = _slotItemsProvider();
        ListViewItem? targetItemToSelect = null;

        foreach (var slot in slots)
        {
            var item = new ListViewItem(slot.SlotNumber.ToString());
            item.SubItems.Add(slot.DisplayName);
            item.SubItems.Add(slot.Count.ToString());
            item.SubItems.Add(slot.SourceScopeLabel);
            item.SubItems.Add(slot.SavedAtLocal.HasValue ? slot.SavedAtLocal.Value.ToString("yyyy-MM-dd HH:mm") : "-");
            item.Tag = slot;

            if (slot.Count == 0)
            {
                item.ForeColor = GetMutedForeColor();
            }

            _slotListView.Items.Add(item);

            if (previousSlot.HasValue && slot.SlotNumber == previousSlot.Value)
            {
                targetItemToSelect = item;
            }
        }

        if (targetItemToSelect == null && _slotListView.Items.Count > 0)
        {
            targetItemToSelect = _slotListView.Items[0];
        }

        if (targetItemToSelect != null)
        {
            targetItemToSelect.Selected = true;
            targetItemToSelect.Focused = true;
            targetItemToSelect.EnsureVisible();
        }

        _slotListView.EndUpdate();
    }

    private void RefreshCurrentMarkItems()
    {
        MergeCurrentMarkRows(_markItemsProvider());

        _currentMarkListView.BeginUpdate();
        _currentMarkListView.Items.Clear();

        foreach (var item in _currentMarkRows)
        {
            var row = new ListViewItem(item.IsMarked ? "ON" : "OFF");
            row.SubItems.Add(GetMarkItemTypeText(item.FullPath));
            row.SubItems.Add(item.Name);
            row.SubItems.Add(item.FullPath);
            row.SubItems.Add(item.IsInCurrentDirectory ? "現在DIR内" : "現在DIR外");
            row.Tag = item.FullPath;

            if (!item.Exists)
            {
                row.ForeColor = MidFDColors.ListArchiveFore;
            }
            else if (!item.IsMarked)
            {
                row.ForeColor = GetMutedForeColor();
            }

            _currentMarkListView.Items.Add(row);
        }

        _currentMarkListView.EndUpdate();
        RefreshTopSummary();
    }

    private void MergeCurrentMarkRows(IReadOnlyList<MarkListViewItem> latestActiveItems)
    {
        var activePathSet = new HashSet<string>(
            latestActiveItems.Select(item => item.FullPath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in _currentMarkRows)
        {
            row.IsMarked = activePathSet.Contains(row.FullPath);
        }

        var existingPathSet = new HashSet<string>(
            _currentMarkRows.Select(row => row.FullPath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in latestActiveItems)
        {
            if (existingPathSet.Contains(item.FullPath))
            {
                var existing = _currentMarkRows.First(row => string.Equals(row.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
                existing.Name = item.Name;
                existing.IsInCurrentDirectory = item.IsInCurrentDirectory;
                existing.Exists = item.Exists;
                existing.IsMarked = true;
            }
            else
            {
                _currentMarkRows.Add(new CurrentMarkRowState
                {
                    Name = item.Name,
                    FullPath = item.FullPath,
                    IsInCurrentDirectory = item.IsInCurrentDirectory,
                    Exists = item.Exists,
                    IsMarked = true
                });
            }
        }
    }

    private void OnSelectedSlotChanged()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            _slotContentHeaderLabel.Text = "選択中スロット: なし";
            _slotContentListView.Items.Clear();
            _saveToSlotButton.Enabled = false;
            _restoreFromSlotButton.Enabled = false;
            return;
        }

        _initialSelectedSlotNumber = selected.SlotNumber;
        _saveToSlotButton.Enabled = true;
        _restoreFromSlotButton.Enabled = selected.Count > 0;

        if (selected.Count == 0)
        {
            _slotContentHeaderLabel.Text = $"選択中スロット: Slot {selected.SlotNumber}「{selected.DisplayName}」 / 保存内容 0件 / 未保存";
        }
        else
        {
            string dateStr = selected.SavedAtLocal.HasValue ? selected.SavedAtLocal.Value.ToString("yyyy-MM-dd HH:mm") : "-";
            _slotContentHeaderLabel.Text = $"選択中スロット: Slot {selected.SlotNumber}「{selected.DisplayName}」 / 保存内容 {selected.Count}件 / {selected.SourceScopeLabel} / 保存 {dateStr}";
        }

        RefreshSlotContentItems(selected.SlotNumber);
    }

    private void RefreshSlotContentItems(int slotNumber)
    {
        _slotContentListView.BeginUpdate();
        _slotContentListView.Items.Clear();

        var details = _slotItemsDetailProvider(slotNumber);
        foreach (var item in details)
        {
            var row = new ListViewItem(GetMarkItemTypeText(item.FullPath));
            row.SubItems.Add(item.Name);
            row.SubItems.Add(item.FullPath);
            row.SubItems.Add(item.Exists ? "存在" : "不在");
            row.Tag = item.FullPath;

            if (!item.Exists)
            {
                row.ForeColor = MidFDColors.ListArchiveFore;
            }

            _slotContentListView.Items.Add(row);
        }

        _slotContentListView.EndUpdate();
    }

    private static string GetMarkItemTypeText(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) return "DIR";
            if (System.IO.File.Exists(path)) return "FILE";
        }
        catch { }
        return "UNKNOWN";
    }

    private void ToggleCurrentMarksFromSelection()
    {
        if (_currentMarkListView.SelectedItems.Count == 0) return;

        var selectedPaths = _currentMarkListView.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag as string)
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .ToList();

        if (selectedPaths.Count == 0) return;

        _toggleCurrentMarksAction(selectedPaths);
        RefreshCurrentMarkItems();
    }

    private void NavigateToSelectedMarkPath()
    {
        if (_currentMarkListView.SelectedItems.Count == 0) return;
        if (_currentMarkListView.SelectedItems[0].Tag is string path && !string.IsNullOrEmpty(path))
        {
            _navigateToMarkedItemAction(path);
        }
    }

    internal void SaveCurrentMarksToSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        string message = _saveSlotAction(selected.SlotNumber, selected.DisplayName);
        _showMessageAction(message, "マークスロット保存", MessageBoxIcon.Information);
        RefreshContents();
    }

    internal void RestoreSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        MarkSlotActionResult result = _restoreSlotAction(selected.SlotNumber);
        if (result.Success)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            _showMessageAction(result.Message, "マークスロット復元", MessageBoxIcon.Warning);
            RefreshContents();
        }
    }

    internal void ExecuteImportClipboardToCurrentMarks()
    {
        if (_importClipboardMarkAction == null) return;

        MarkSlotClipboardActionResult actionResult = _importClipboardMarkAction();
        if (actionResult.Success)
        {
            RefreshCurrentMarkItems();
            _showImportResultAction(actionResult);
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            _showMessageAction(actionResult.Message, "KDSL_RESULT→現在Mark", MessageBoxIcon.Warning);
            RefreshContents();
        }
    }

    internal IReadOnlyList<string> CurrentMarkPathsForTest => _currentMarkListView.Items.Cast<ListViewItem>()
        .Select(item => item.Tag as string ?? string.Empty)
        .ToList();

    private void ExecuteClearCurrentTabMarks()
    {
        int count = _currentMarkRows.Count(x => x.IsMarked && x.IsInCurrentDirectory);
        if (count == 0)
        {
            _showMessageAction("現在タブに解除対象のマークはありません。", "Mark解除", MessageBoxIcon.Information);
            return;
        }

        var dr = MessageBox.Show(this, $"現在タブのマーク {count} 件を解除しますか？", "現在タブのMark解除", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (dr != DialogResult.Yes) return;

        _clearCurrentTabMarksAction();
        RefreshContents();
    }

    private void ExecuteClearCategoryMarks()
    {
        var summary = _globalSummaryProvider();
        if (summary.CurrentCategoryMarkCount == 0)
        {
            _showMessageAction("現在カテゴリに解除対象のマークはありません。", "Mark解除", MessageBoxIcon.Information);
            return;
        }

        var dr = MessageBox.Show(this, $"現在カテゴリ「{summary.CurrentCategoryName}」の全マーク {summary.CurrentCategoryMarkCount} 件（{summary.CurrentCategoryTabCount} タブ）を解除しますか？", "カテゴリ全タブのMark解除", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (dr != DialogResult.Yes) return;

        _clearCategoryMarksAction();
        RefreshContents();
    }

    private void ExecuteClearWorkspaceMarks()
    {
        var summary = _globalSummaryProvider();
        if (summary.GlobalMarkCount == 0)
        {
            _showMessageAction("Workspace全体に解除対象のマークはありません。", "Mark解除", MessageBoxIcon.Information);
            return;
        }

        var dr = MessageBox.Show(this, $"Workspace全体の全マーク {summary.GlobalMarkCount} 件（{summary.GlobalCategoryCount} カテゴリ / {summary.GlobalTabCount} タブ）を解除しますか？", "Workspace全体のMark解除", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (dr != DialogResult.Yes) return;

        _clearGlobalMarksAction();
        RefreshContents();
    }

    private void OpenManagementDialog()
    {
        var selected = GetSelectedSlotSummary();
        int slotNumber = selected?.SlotNumber ?? 1;

        if (_openManagementDialogAction != null)
        {
            _openManagementDialogAction(slotNumber);
            RefreshContents();
            return;
        }

        using var dialog = new MarkSlotManagementDialog(
            initialSlotNumber: slotNumber,
            slotItemsProvider: _slotItemsProvider,
            slotItemsDetailProvider: _slotItemsDetailProvider,
            renameSlotAction: _renameSlotAction,
            deleteSlotAction: _deleteSlotAction,
            removeSlotItemsAction: _removeSlotItemsAction,
            saveCategorySlotAction: _saveCategorySlotAction,
            saveWorkspaceSlotAction: _saveWorkspaceSlotAction,
            openSlotSetOperationAction: _openSlotSetOperationAction,
            allowSlotSetOperation: _allowSlotSetOperation,
            exportSlotAction: _exportSlotAction,
            importSlotAction: _importSlotAction,
            exportAllSlotsAction: _exportAllSlotsAction,
            importAllSlotsAction: _importAllSlotsAction,
            allowSlotBackupTransfer: _allowSlotBackupTransfer,
            showMessageAction: _showMessageAction);

        dialog.ShowDialog(this);
        RefreshContents();
    }

    public MarkSlotSummaryViewItem? GetSelectedSlotSummary()
    {
        if (_slotListView.SelectedItems.Count > 0)
        {
            return _slotListView.SelectedItems[0].Tag as MarkSlotSummaryViewItem;
        }
        foreach (ListViewItem item in _slotListView.Items)
        {
            if (item.Selected)
            {
                return item.Tag as MarkSlotSummaryViewItem;
            }
        }
        if (_slotListView.Items.Count > 0)
        {
            return _slotListView.Items[0].Tag as MarkSlotSummaryViewItem;
        }
        return null;
    }
}
