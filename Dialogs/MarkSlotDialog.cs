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
    private readonly Func<int, string> _restoreSlotAction;
    private readonly Func<int, string?, string> _renameSlotAction;
    private readonly Func<int, string> _deleteSlotAction;
    private readonly Func<MarkGlobalSummary> _globalSummaryProvider;
    private readonly Action _clearCategoryMarksAction;
    private readonly Action _clearGlobalMarksAction;
    private readonly Action _clearCurrentTabMarksAction;
    private readonly Label _currentMarksLabel;
    private readonly Label _slotSummaryLabel;
    private readonly Label _slotContentsLabel;
    private readonly ListView _markListView;
    private readonly ListView _slotListView;
    private readonly ListView _slotContentListView;
    private readonly Button _saveButton;
    private readonly Button _slotSetOperationButton;
    private readonly Button _manageButton;
    private readonly Button _importButton;
    private readonly Button _exportAllButton;
    private readonly Button _importAllButton;
    private readonly Button _restoreButton;
    private readonly Button _renameButton;
    private readonly Button _deleteButton;
    private readonly Button _closeButton;
    private readonly Label _summaryLabel;
    private readonly Label _persistenceLabel;
    private readonly Label _globalSummaryLabel;
    private readonly Button _clearCategoryButton;
    private readonly Button _clearGlobalButton;
    private readonly Button _clearCurrentTabButton;
    private Control? _bottomActionPanel;
    private readonly List<CurrentMarkRowState> _currentMarkRows = new();

    private readonly ContextMenuStrip _saveMenu = new();
    private readonly ContextMenuStrip _manageMenu = new();
    private ToolStripItem? _manageExportItem;
    private ToolStripItem? _manageDeleteItem;
    private readonly ContextMenuStrip _slotContextMenu = new();
    private ToolStripItem? _slotContextRestoreItem;
    private ToolStripItem? _slotContextExportItem;
    private ToolStripItem? _slotContextDeleteItem;
    private const string CurrentMarksHelpText = "現在の mark を確認しながら整理できます。Space: ON/OFF切替  /  ダブルクリック: 対象の場所へ戻る";
    private const string SlotHelpText = "スロットを選ぶと、保存▼ / 復元 / スロット演算 / スロット管理▼ が使えます。表示名列ダブルクリックで名前変更、その他の列ダブルクリックで復元、行の右クリックで専用メニューを表示します。";

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
        Func<int, string> restoreSlotAction,
        Func<int, string?, string> renameSlotAction,
        Func<int, string> deleteSlotAction,
        Func<MarkGlobalSummary> globalSummaryProvider,
        Action clearCategoryMarksAction,
        Action clearGlobalMarksAction,
        Action clearCurrentTabMarksAction)
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
        _globalSummaryProvider = globalSummaryProvider;
        _clearCategoryMarksAction = clearCategoryMarksAction;
        _clearGlobalMarksAction = clearGlobalMarksAction;
        _clearCurrentTabMarksAction = clearCurrentTabMarksAction;

        Text = "マーク一覧 / スロット";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable; // 重量級なのでリサイズ許可
        MinimizeBox = false;
        MaximizeBox = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1260, 700);
        MinimumSize = new Size(1000, 600);

        const float listFontSize = 8.5f;

        _currentMarksLabel = new Label { Text = "現在のマーク", AutoEllipsis = true };
        _markListView = CreateListView(new[]
        {
            ("Mark", 52),
            ("種別", 56),
            ("名前", 220),
            ("場所", 280),
            ("範囲", 58),
            ("状態", 66)
        }, listFontSize);
        _markListView.ShowItemToolTips = true;
        _markListView.DoubleClick += (_, _) => NavigateToSelectedMarkItem();

        _slotSummaryLabel = new Label { Text = "マークスロット", AutoEllipsis = true };
        _slotListView = CreateListView(new[]
        {
            ("Slot", 48),
            ("表示名", 180),
            ("件数", 50),
            ("保存", 88),
            ("概要", 184)
        }, listFontSize);
        _slotListView.ShowItemToolTips = true;
        _slotListView.SelectedIndexChanged += (_, _) =>
        {
            RefreshSlotContentItems();
            UpdateButtonState();
        };
        _slotListView.MouseDown += SlotListView_MouseDown;
        _slotListView.MouseDoubleClick += SlotListView_MouseDoubleClick;

        _slotContentsLabel = new Label { Text = "選択中スロットの内容", AutoEllipsis = true };
        _slotContentListView = CreateListView(new[]
        {
            ("種別", 52),
            ("名前", 132),
            ("場所", 128),
            ("範囲", 50),
            ("状態", 50)
        }, listFontSize);
        _slotContentListView.ShowItemToolTips = true;

        _summaryLabel = new Label { Text = BuildSlotHelpText(), AutoEllipsis = true };
        _persistenceLabel = new Label { ForeColor = SystemColors.GrayText, AutoEllipsis = true };

        _saveButton = new Button { Text = "保存▼" };
        _saveButton.MouseDown += SaveButton_MouseDown;

        _slotSetOperationButton = new Button { Text = "スロット演算..." };
        _slotSetOperationButton.Click += (_, _) => OpenSlotSetOperation();

        _manageButton = new Button { Text = "スロット管理▼", AutoSize = true };
        _manageButton.MouseDown += ManageButton_MouseDown;

        _importButton = new Button { Text = "インポート..." };
        _importButton.Click += (_, _) => ImportSelectedSlot();
        _exportAllButton = new Button { Text = "全スロット エクスポート..." };
        _exportAllButton.Click += (_, _) => ExportAllSlots();
        _importAllButton = new Button { Text = "全スロット インポート..." };
        _importAllButton.Click += (_, _) => ImportAllSlots();

        _restoreButton = new Button { Text = "復元" };
        _restoreButton.Click += (_, _) => RestoreSelectedSlot();

        _renameButton = new Button { Text = "名前変更" };
        _renameButton.Click += (_, _) => RenameSelectedSlot();

        _deleteButton = new Button { Text = "削除" };
        _deleteButton.Click += (_, _) => DeleteSelectedSlot();

        _globalSummaryLabel = new Label { AutoEllipsis = true, ForeColor = Color.LightSkyBlue };
        _clearCategoryButton = new Button { Text = "カテゴリ解除...", AutoSize = true, FlatStyle = FlatStyle.Flat };
        _clearCategoryButton.FlatAppearance.BorderSize = 0;
        _clearCategoryButton.Click += (_, _) => ClearCategoryMarks();

        _clearGlobalButton = new Button { Text = "Workspace解除...", AutoSize = true, FlatStyle = FlatStyle.Flat };
        _clearGlobalButton.FlatAppearance.BorderSize = 0;
        _clearGlobalButton.Click += (_, _) => ClearGlobalMarks();
        _clearCurrentTabButton = new Button { Text = "解除...", AutoSize = true, FlatStyle = FlatStyle.Flat };
        _clearCurrentTabButton.FlatAppearance.BorderSize = 0;
        _clearCurrentTabButton.Click += (_, _) => ClearCurrentTabMarks();

        _closeButton = new Button { Text = "閉じる", DialogResult = DialogResult.OK };

        Controls.Add(_currentMarksLabel);
        Controls.Add(_markListView);
        Controls.Add(_slotSummaryLabel);
        Controls.Add(_slotListView);
        Controls.Add(_slotContentsLabel);
        Controls.Add(_slotContentListView);
        Controls.Add(_summaryLabel);
        Controls.Add(_persistenceLabel);
        Controls.Add(_saveButton);
        Controls.Add(_slotSetOperationButton);
        Controls.Add(_manageButton);
        Controls.Add(_restoreButton);
        Controls.Add(_globalSummaryLabel);
        Controls.Add(_clearCategoryButton);
        Controls.Add(_clearGlobalButton);
        Controls.Add(_clearCurrentTabButton);
        Controls.Add(_closeButton);

        _slotListView.KeyDown += MarkSlotDialog_KeyDown;
        AcceptButton = _restoreButton;
        CancelButton = _closeButton;
        KeyDown += MarkSlotDialog_KeyDown;

        // 初期配置
        LayoutSections();
        RefreshContents();

        SizeChanged += (_, _) => LayoutSections();

        Shown += (s, e) =>
        {
            if (_slotListView.Items.Count > 0 && _slotListView.SelectedItems.Count == 0)
            {
                _slotListView.Items[0].Selected = true;
                _slotListView.Items[0].Focused = true;
            }
            _slotListView.Focus();
        };

        InitializeDropDownMenus();
        InitializeSlotContextMenu();
    }

    private string BuildSlotHelpText()
    {
        var actions = new List<string> { "保存▼", "復元", "スロット管理▼" };
        if (_allowSlotSetOperation)
        {
            actions.Insert(2, "スロット演算");
        }

        if (_allowSlotBackupTransfer)
        {
            actions.Add("エクスポート/インポート");
        }

        return $"スロットを選ぶと、{string.Join(" / ", actions)} が使えます。表示名列ダブルクリックで名前変更、その他の列ダブルクリックで復元、行の右クリックで専用メニューを表示します。";
    }

    private void LayoutSections()
    {
        const int outerMargin = 12;
        const int sectionGap = 10;
        const int labelHeight = 20;
        const int buttonGap = 8;
        const int contentGap = 12;

        SuspendLayout();
        try
        {
            // 下部ボタン行の配置（初回のみ生成）
            if (_bottomActionPanel == null)
            {
                var buttons = new[] { _saveButton, _restoreButton, _slotSetOperationButton, _manageButton, _closeButton };
                // フォームが縮まないよう、現在のクライアント領域から推定ボタン行高さを引いた値を contentBottom とする
                int initialContentBottom = Math.Max(400, ClientSize.Height - 64);

                _bottomActionPanel = FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
                    this,
                    buttons,
                    initialContentBottom,
                    buttonGap: buttonGap,
                    contentGap: contentGap);
            }

            if (_bottomActionPanel == null) return;

            // 重要: Dock.Bottom パネルの Top は不確定な場合があるため、ClientSize から計算する
            int actionRowTop = ClientSize.Height - _bottomActionPanel.Height;

            // ラベル類の配置（下から上へ）
            int currentBottom = actionRowTop - contentGap;
            _persistenceLabel.Width = Math.Max(100, ClientSize.Width - (outerMargin * 2));
            _persistenceLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_persistenceLabel, _persistenceLabel.Width, 36);
            _persistenceLabel.Location = new Point(outerMargin, currentBottom - _persistenceLabel.Height);

            _summaryLabel.Width = Math.Max(100, ClientSize.Width - (outerMargin * 2));
            _summaryLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_summaryLabel, _summaryLabel.Width, 24);
            _summaryLabel.Location = new Point(outerMargin, _persistenceLabel.Top - 4);

            int mainAreaBottom = _summaryLabel.Top - sectionGap;
            int mainAreaTop = outerMargin;
            int totalHeight = Math.Max(100, mainAreaBottom - mainAreaTop);

            // 左右の幅比率 (左 60% : 右 40%)
            int leftWidth = (int)((ClientSize.Width - outerMargin * 2 - sectionGap) * 0.60);
            leftWidth = Math.Max(400, leftWidth);
            int rightWidth = Math.Max(300, ClientSize.Width - outerMargin * 2 - sectionGap - leftWidth);

            // 右カラム: 選択中スロットの内容
            _slotContentsLabel.Location = new Point(ClientSize.Width - outerMargin - rightWidth, mainAreaTop);
            _slotContentsLabel.Size = new Size(rightWidth, labelHeight);
            _slotContentListView.Location = new Point(_slotContentsLabel.Left, _slotContentsLabel.Bottom + 4);
            _slotContentListView.Size = new Size(rightWidth, Math.Max(50, mainAreaBottom - _slotContentListView.Top));

            // 左カラム: 2段構成
            _currentMarksLabel.Location = new Point(outerMargin, mainAreaTop);
            _currentMarksLabel.Size = new Size(leftWidth, labelHeight);

            // マーク一覧 (上段) と スロット一覧 (下段) の高さ比率 (5:5)
            int labelSpace = labelHeight + 4 + sectionGap + labelHeight + 4;
            int availableListHeight = Math.Max(50, totalHeight - labelSpace);
            int markListHeight = availableListHeight / 2;
            int slotListHeight = availableListHeight - markListHeight;

            _markListView.Location = new Point(outerMargin, _currentMarksLabel.Bottom + 4);
            _markListView.Size = new Size(leftWidth, markListHeight);

            // 横断サマリー
            _globalSummaryLabel.Location = new Point(outerMargin, _markListView.Bottom + 6);
            _globalSummaryLabel.Size = new Size(leftWidth - 280, 24);
            _clearGlobalButton.Location = new Point(outerMargin + leftWidth - _clearGlobalButton.Width, _markListView.Bottom + 2);
            _clearCategoryButton.Location = new Point(_clearGlobalButton.Left - _clearCategoryButton.Width - 4, _markListView.Bottom + 2);
            _clearCurrentTabButton.Location = new Point(_clearCategoryButton.Left - _clearCurrentTabButton.Width - 4, _markListView.Bottom + 2);

            _slotSummaryLabel.Location = new Point(outerMargin, _markListView.Bottom + sectionGap + 14);
            _slotSummaryLabel.Size = new Size(leftWidth, labelHeight);
            _slotListView.Location = new Point(outerMargin, _slotSummaryLabel.Bottom + 4);
            _slotListView.Size = new Size(leftWidth, slotListHeight - 14);
        }
        finally
        {
            ResumeLayout();
        }
    }

    private static ListView CreateListView((string Text, int Width)[] columns, float fontSize)
    {
        FontFamily fontFamily = SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily;
        var listView = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            BackColor = Color.Black,
            ForeColor = Color.Cyan,
            Font = new Font(fontFamily, fontSize, FontStyle.Regular)
        };

        foreach (var column in columns)
        {
            listView.Columns.Add(column.Text, column.Width);
        }

        return listView;
    }

    private void MarkSlotDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_markListView.ContainsFocus)
        {
            if (e.KeyCode == Keys.Space)
            {
                ToggleCurrentMarksFromSelection();
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

    private void RefreshContents()
    {
        RefreshMarkItems();
        RefreshSlotItems();
        RefreshSlotContentItems();
        UpdateButtonState();
        RefreshPersistenceSummary();
        RefreshGlobalSummary();
        LayoutSections();
    }

    private void RefreshGlobalSummary()
    {
        var summary = _globalSummaryProvider();
        _globalSummaryLabel.Text = $"Workspace全域: {summary.GlobalMarkCount}件 ({summary.GlobalTabCount}タブ / {summary.GlobalCategoryCount}カテゴリ) / カテゴリ '{summary.CurrentCategoryName}': {summary.CurrentCategoryMarkCount}件 ({summary.CurrentCategoryTabCount}タブ)";
        _clearCategoryButton.Enabled = summary.CurrentCategoryMarkCount > 0;
        _clearGlobalButton.Enabled = summary.GlobalMarkCount > 0;
    }

    private void RefreshPersistenceSummary()
    {
        _persistenceLabel.Text = _markPersistenceSummaryProvider();
    }

    private void RefreshMarkItems()
    {
        string? selectedPath = GetSelectedMarkPath();
        string? topVisiblePath = GetTopVisibleMarkPath();
        MergeCurrentMarkRows(_markItemsProvider());

        int markedCount = _currentMarkRows.Count(item => item.IsMarked);
        int currentDirCount = _currentMarkRows.Count(item => item.IsInCurrentDirectory);
        int outsideCount = _currentMarkRows.Count - currentDirCount;
        int missingCount = _currentMarkRows.Count(item => !item.Exists);
        _currentMarksLabel.Text = $"現在のマーク (ON {markedCount}件 / 表示 {_currentMarkRows.Count}件 / 現在DIR内 {currentDirCount} / 外 {outsideCount} / 不在 {missingCount})";
        _summaryLabel.Text = CurrentMarksHelpText;
        _markListView.BeginUpdate();
        _markListView.Items.Clear();

        foreach (var item in _currentMarkRows)
        {
            var row = new ListViewItem(item.IsMarked ? "ON" : "OFF");
            row.SubItems.Add(GetMarkItemTypeText(item.FullPath));
            row.SubItems.Add(item.Name);
            row.SubItems.Add(GetDisplayLocation(item.FullPath));
            row.SubItems.Add(item.IsInCurrentDirectory ? "現在DIR内" : "現在DIR外");
            row.SubItems.Add(item.Exists ? "存在" : "不在");
            row.Tag = item.FullPath;
            row.ToolTipText = BuildCurrentMarkTooltip(item);
            if (!item.Exists)
            {
                row.ForeColor = Color.Yellow;
            }
            else if (!item.IsMarked)
            {
                row.ForeColor = Color.Gray;
            }
            _markListView.Items.Add(row);
        }

        _markListView.EndUpdate();
        RestoreTopVisibleMark(topVisiblePath);

        int targetIndex = FindMarkRowIndex(selectedPath);
        if (targetIndex < 0 && _markListView.Items.Count > 0)
        {
            targetIndex = 0;
        }

        if (targetIndex >= 0)
        {
            _markListView.Items[targetIndex].Selected = true;
            _markListView.Items[targetIndex].Focused = true;
        }
    }

    private void RefreshSlotItems()
    {
        int? selectedSlot = GetSelectedSlotNumber();
        var items = _slotItemsProvider();
        int usedCount = items.Count(item => item.Count > 0 || item.SavedAtLocal.HasValue || !IsDefaultSlotDisplayName(item));
        _slotSummaryLabel.Text = $"マークスロット (使用 {usedCount}/{items.Count})";
        _slotListView.BeginUpdate();
        _slotListView.Items.Clear();

        foreach (var item in items)
        {
            IReadOnlyList<MarkListViewItem> detailItems = _slotItemsDetailProvider(item.SlotNumber);
            var row = new ListViewItem(item.SlotNumber.ToString());
            string displayName = string.IsNullOrWhiteSpace(item.DisplayName) ? $"スロット {item.SlotNumber}" : item.DisplayName;
            row.SubItems.Add(displayName);
            row.SubItems.Add(item.Count.ToString());
            row.SubItems.Add(item.SavedAtLocal?.ToString("MM-dd HH:mm") ?? "-");
            row.SubItems.Add(BuildSlotOverviewText(item, detailItems));
            row.Tag = item.SlotNumber;
            row.ToolTipText = BuildSlotSummaryTooltip(item, detailItems);
            if (item.Count == 0)
            {
                row.ForeColor = Color.Gray;
            }
            _slotListView.Items.Add(row);
        }

        _slotListView.EndUpdate();

        int targetIndex = -1;
        if (selectedSlot.HasValue)
        {
            for (int i = 0; i < _slotListView.Items.Count; i++)
            {
                if (_slotListView.Items[i].Tag is int slotNumber && slotNumber == selectedSlot.Value)
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex < 0 && _slotListView.Items.Count > 0)
        {
            targetIndex = 0;
        }

        if (targetIndex >= 0)
        {
            _slotListView.Items[targetIndex].Selected = true;
            _slotListView.Items[targetIndex].Focused = true;
        }
    }

    private void RefreshSlotContentItems()
    {
        int itemCount = 0;
        _slotContentListView.BeginUpdate();
        _slotContentListView.Items.Clear();

        int? slotNumber = GetSelectedSlotNumber();
        MarkSlotSummaryViewItem? selectedSummary = GetSelectedSlotSummary();
        if (slotNumber.HasValue)
        {
            var items = _slotItemsDetailProvider(slotNumber.Value);
            itemCount = items.Count;
            foreach (var item in items)
            {
                var row = new ListViewItem(GetMarkItemTypeText(item.FullPath));
                row.SubItems.Add(item.Name);
                row.SubItems.Add(GetDisplayLocation(item.FullPath));
                row.SubItems.Add(item.IsInCurrentDirectory ? "現在DIR" : "外");
                row.SubItems.Add(item.Exists ? "存在" : "不在");
                row.Tag = item.FullPath;
                row.ToolTipText = BuildSlotDetailToolTip(item);

                if (!item.Exists)
                {
                    row.ForeColor = Color.Yellow;
                }

                _slotContentListView.Items.Add(row);
            }
        }

        _slotContentListView.EndUpdate();
        if (selectedSummary == null)
        {
            _slotContentsLabel.Text = "選択中スロットの内容 (未選択)";
        }
        else
        {
            string displayName = string.IsNullOrWhiteSpace(selectedSummary.DisplayName)
                ? $"スロット {selectedSummary.SlotNumber}"
                : selectedSummary.DisplayName;
            _slotContentsLabel.Text = $"選択中スロットの内容 ({itemCount}件 / スロット {selectedSummary.SlotNumber}: {displayName})";
        }
        UpdateSummaryText(selectedSummary, itemCount);
    }

    private void SaveSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        string defaultName = string.IsNullOrWhiteSpace(selected.DisplayName)
            ? $"スロット {selected.SlotNumber}"
            : selected.DisplayName;
        string? displayName = SimpleInputDialog.ShowNullable(
            $"スロット {selected.SlotNumber} を現在タブのマーク全件で上書き保存します。\n表示名を入力してください。",
            "マークスロット保存 (上書き)",
            defaultName);
        if (displayName == null)
        {
            return;
        }

        _saveSlotAction(selected.SlotNumber, displayName);
        RefreshContents();
    }

    private void SaveSelectedCategorySlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        _saveCategorySlotAction(selected.SlotNumber);
        RefreshContents();
    }

    private void SaveSelectedWorkspaceSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        _saveWorkspaceSlotAction(selected.SlotNumber);
        RefreshContents();
    }

    private void InitializeDropDownMenus()
    {
        _saveMenu.AutoClose = true;
        _saveMenu.Items.Add("現在タブのマークを保存...", null, (_, _) => SaveSelectedSlot());
        _saveMenu.Items.Add("現在カテゴリ全タブのマークを保存...", null, (_, _) => SaveSelectedCategorySlot());
        _saveMenu.Items.Add("Workspace全体のマークを保存...", null, (_, _) => SaveSelectedWorkspaceSlot());

        _manageMenu.AutoClose = true;
        if (_allowSlotBackupTransfer)
        {
            _manageExportItem = _manageMenu.Items.Add("選択スロットをエクスポート...", null, (_, _) => ExportSelectedSlot());
            _manageMenu.Items.Add("選択スロットへインポート...", null, (_, _) => ImportSelectedSlot());
            _manageMenu.Items.Add("-");
            _manageMenu.Items.Add("全スロットを一括エクスポート...", null, (_, _) => ExportAllSlots());
            _manageMenu.Items.Add("全スロットを一括インポート（全置換）...", null, (_, _) => ImportAllSlots());
            _manageMenu.Items.Add("-");
        }
        _manageMenu.Items.Add("スロット名を変更...", null, (_, _) => RenameSelectedSlot());
        _manageDeleteItem = _manageMenu.Items.Add("選択スロットを削除...", null, (_, _) => DeleteSelectedSlot());
    }

    private void InitializeSlotContextMenu()
    {
        _slotContextMenu.AutoClose = true;
        _slotContextRestoreItem = _slotContextMenu.Items.Add("現在タブへ復元", null, (_, _) => RestoreSelectedSlot());
        _slotContextMenu.Items.Add("スロット名を変更...", null, (_, _) => RenameSelectedSlot());
        if (_allowSlotBackupTransfer)
        {
            _slotContextMenu.Items.Add("-");
            _slotContextExportItem = _slotContextMenu.Items.Add("選択スロットをエクスポート...", null, (_, _) => ExportSelectedSlot());
            _slotContextMenu.Items.Add("選択スロットへインポート...", null, (_, _) => ImportSelectedSlot());
            _slotContextMenu.Items.Add("-");
        }
        _slotContextDeleteItem = _slotContextMenu.Items.Add("選択スロットを削除...", null, (_, _) => DeleteSelectedSlot());
    }

    private void SlotListView_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;

        ListViewHitTestInfo hit = _slotListView.HitTest(e.Location);
        if (hit.Item == null) return;

        // 右クリックされた行を選択状態にする
        hit.Item.Selected = true;
        hit.Item.Focused = true;

        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        bool hasContent = selected.Count > 0;
        bool hasCustomState = !IsDefaultSlotDisplayName(selected) || selected.SavedAtLocal.HasValue;

        if (_slotContextRestoreItem != null) _slotContextRestoreItem.Enabled = hasContent;
        if (_slotContextExportItem != null) _slotContextExportItem.Enabled = hasContent;
        if (_slotContextDeleteItem != null) _slotContextDeleteItem.Enabled = hasContent || hasCustomState;

        _slotContextMenu.Show(_slotListView, e.Location);
    }

    private void SlotListView_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        ListViewHitTestInfo hit = _slotListView.HitTest(e.Location);
        if (hit.Item == null)
        {
            return;
        }

        hit.Item.Selected = true;
        hit.Item.Focused = true;

        bool isDisplayNameColumn =
            hit.SubItem != null &&
            hit.Item.SubItems.Count > 1 &&
            ReferenceEquals(hit.SubItem, hit.Item.SubItems[1]);

        if (isDisplayNameColumn)
        {
            RenameSelectedSlot();
            return;
        }

        RestoreSelectedSlot();
    }

    private void SaveButton_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (GetSelectedSlotSummary() == null) return;

        _saveMenu.Show(_saveButton, new Point(0, _saveButton.Height));
    }

    private void ExportSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        _exportSlotAction(selected.SlotNumber);
        RefreshContents();
    }

    private void OpenSlotSetOperation()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        _openSlotSetOperationAction(selected.SlotNumber);
        RefreshContents();
    }

    private void ImportSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        _importSlotAction(selected.SlotNumber);
        RefreshContents();
    }

    private void ExportAllSlots()
    {
        _exportAllSlotsAction();
        RefreshContents();
    }

    private void ImportAllSlots()
    {
        _importAllSlotsAction();
        RefreshContents();
    }

    private void ManageButton_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        bool hasContent = selected.Count > 0;
        bool hasCustomState = !IsDefaultSlotDisplayName(selected) || selected.SavedAtLocal.HasValue;

        if (_manageExportItem != null) _manageExportItem.Enabled = hasContent;
        if (_manageDeleteItem != null) _manageDeleteItem.Enabled = hasContent || hasCustomState;

        _manageMenu.Show(_manageButton, new Point(0, _manageButton.Height));
    }

    private void RenameSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        string defaultName = string.IsNullOrWhiteSpace(selected.DisplayName)
            ? $"スロット {selected.SlotNumber}"
            : selected.DisplayName;
        string? displayName = SimpleInputDialog.ShowNullable(
            $"スロット {selected.SlotNumber} の表示名を変更します。",
            "マークスロット名前変更",
            defaultName);
        if (displayName == null)
        {
            return;
        }

        _renameSlotAction(selected.SlotNumber, displayName);
        RefreshContents();
    }

    private void RestoreSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        _restoreSlotAction(selected.SlotNumber);
        RefreshContents();
    }

    private void DeleteSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            return;
        }

        DialogResult result = MessageBox.Show(
            $"スロット {selected.SlotNumber} の内容を削除しますか？",
            "マークスロット削除",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return;
        }

        _deleteSlotAction(selected.SlotNumber);
        RefreshContents();
    }

    private MarkSlotSummaryViewItem? GetSelectedSlotSummary()
    {
        if (_slotListView.SelectedItems.Count == 0)
        {
            return _slotItemsProvider().FirstOrDefault();
        }

        if (_slotListView.SelectedItems[0].Tag is not int slotNumber)
        {
            return null;
        }

        return _slotItemsProvider().FirstOrDefault(item => item.SlotNumber == slotNumber);
    }

    private int? GetSelectedSlotNumber()
    {
        if (_slotListView.SelectedItems.Count == 0)
        {
            return null;
        }

        return _slotListView.SelectedItems[0].Tag is int slotNumber
            ? slotNumber
            : null;
    }

    private void UpdateButtonState()
    {
        MarkSlotSummaryViewItem? selected = GetSelectedSlotSummary();
        bool hasSlot = selected != null;
        bool hasContent = selected != null && selected.Count > 0;

        _saveButton.Enabled = hasSlot;
        _slotSetOperationButton.Visible = _allowSlotSetOperation;
        _slotSetOperationButton.Enabled = _allowSlotSetOperation && hasSlot;
        _manageButton.Enabled = hasSlot;
        _restoreButton.Enabled = hasContent;
        _importButton.Visible = _allowSlotBackupTransfer;
        _importButton.Enabled = _allowSlotBackupTransfer && hasSlot;
        _exportAllButton.Visible = _allowSlotBackupTransfer;
        _exportAllButton.Enabled = _allowSlotBackupTransfer;
        _importAllButton.Visible = _allowSlotBackupTransfer;
        _importAllButton.Enabled = _allowSlotBackupTransfer;
    }

    private void ToggleCurrentMarksFromSelection()
    {
        List<string> targets = GetSelectedMarkPath() is string path && !string.IsNullOrWhiteSpace(path)
            ? new List<string> { path }
            : new List<string>();

        if (targets.Count == 0)
        {
            return;
        }

        _toggleCurrentMarksAction(targets);
        RefreshMarkItems();
        RestoreMarkListFocus();
    }

    private void RestoreMarkListFocus()
    {
        if (!Visible || IsDisposed)
        {
            return;
        }

        ActiveControl = _markListView;
        _markListView.Select();
        _markListView.Focus();
    }

    private string? GetTopVisibleMarkPath()
    {
        try
        {
            return _markListView.TopItem?.Tag as string;
        }
        catch
        {
            return null;
        }
    }

    private void RestoreTopVisibleMark(string? fullPath)
    {
        int targetIndex = FindMarkRowIndex(fullPath);
        if (targetIndex < 0)
        {
            return;
        }

        try
        {
            _markListView.TopItem = _markListView.Items[targetIndex];
        }
        catch
        {
            // Ignore environments where TopItem cannot be restored.
        }
    }

    private string? GetSelectedMarkPath()
    {
        if (_markListView.SelectedItems.Count == 0)
        {
            return null;
        }

        return _markListView.SelectedItems[0].Tag as string;
    }

    private int FindMarkRowIndex(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return -1;
        }

        for (int i = 0; i < _markListView.Items.Count; i++)
        {
            if (string.Equals(_markListView.Items[i].Tag as string, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void MergeCurrentMarkRows(IReadOnlyList<MarkListViewItem> currentMarkedItems)
    {
        var currentByPath = currentMarkedItems.ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(_currentMarkRows.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);

        foreach (CurrentMarkRowState row in _currentMarkRows)
        {
            if (currentByPath.TryGetValue(row.FullPath, out MarkListViewItem? current))
            {
                row.Name = current.Name;
                row.IsInCurrentDirectory = current.IsInCurrentDirectory;
                row.Exists = current.Exists;
                row.IsMarked = true;
            }
            else
            {
                row.IsMarked = false;
            }
        }

        foreach (MarkListViewItem item in currentMarkedItems)
        {
            if (seenPaths.Contains(item.FullPath))
            {
                continue;
            }

            _currentMarkRows.Add(new CurrentMarkRowState
            {
                Name = item.Name,
                FullPath = item.FullPath,
                IsInCurrentDirectory = item.IsInCurrentDirectory,
                Exists = item.Exists,
                IsMarked = true
            });
        }

        _currentMarkRows.Sort(static (left, right) =>
        {
            int byCurrentDir = right.IsInCurrentDirectory.CompareTo(left.IsInCurrentDirectory);
            if (byCurrentDir != 0)
            {
                return byCurrentDir;
            }

            int byExists = right.Exists.CompareTo(left.Exists);
            if (byExists != 0)
            {
                return byExists;
            }

            return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    private void NavigateToSelectedMarkItem()
    {
        string? path = GetSelectedMarkPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _navigateToMarkedItemAction(path);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string GetDisplayLocation(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "-";
        }

        string? parentDirectory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            return parentDirectory;
        }

        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return string.IsNullOrWhiteSpace(root) ? fullPath : root;
    }

    private static string GetMarkItemTypeText(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "-";
        }

        if (Directory.Exists(fullPath))
        {
            return "DIR";
        }

        if (File.Exists(fullPath))
        {
            return "FILE";
        }

        return "不明";
    }

    private static string BuildCurrentMarkTooltip(CurrentMarkRowState item)
    {
        string typeText = GetMarkItemTypeText(item.FullPath);
        string scopeText = item.IsInCurrentDirectory ? "現在DIR内" : "現在DIR外";
        string existsText = item.Exists ? "存在" : "不在";
        string markText = item.IsMarked ? "ON" : "OFF";
        return $"名前: {item.Name}\n種別: {typeText}\nMark: {markText}\n範囲: {scopeText}\n状態: {existsText}\nパス: {item.FullPath}";
    }

    private void UpdateSummaryText(MarkSlotSummaryViewItem? selectedSummary, int itemCount)
    {
        if (selectedSummary == null)
        {
            _summaryLabel.Text = BuildSlotHelpText();
            return;
        }

        string displayName = string.IsNullOrWhiteSpace(selectedSummary.DisplayName)
            ? $"スロット {selectedSummary.SlotNumber}"
            : selectedSummary.DisplayName;
        string slotText = $"スロット {selectedSummary.SlotNumber} ({displayName})";
        if (itemCount <= 0)
        {
            bool hasCustomState = !IsDefaultSlotDisplayName(selectedSummary) || selectedSummary.SavedAtLocal.HasValue;
            string emptyActions = BuildAvailableSlotActionText(includeRestore: false, includeDelete: hasCustomState);
            _summaryLabel.Text = hasCustomState
                ? $"{slotText} は空です。{emptyActions} が使えます。削除すると空の既定状態に戻せます。"
                : $"{slotText} は空です。{emptyActions} が使えます。";
            return;
        }

        _summaryLabel.Text = $"{slotText} は {itemCount} 件です。{BuildAvailableSlotActionText(includeRestore: true, includeDelete: true)} が使えます。インポートや演算結果保存では現在タブは変わりません。";
    }

    private string BuildAvailableSlotActionText(bool includeRestore, bool includeDelete)
    {
        var actions = new List<string> { "保存▼(現在タブ/カテゴリ/全体)" };
        if (_allowSlotSetOperation)
        {
            actions.Add("スロット演算");
        }

        if (_allowSlotBackupTransfer)
        {
            actions.Add("エクスポート");
            actions.Add("インポート");
        }

        if (includeRestore)
        {
            actions.Add("現在タブへの復元");
        }

        actions.Add("名前変更");
        if (includeDelete)
        {
            actions.Add("削除");
        }

        return string.Join(" / ", actions);
    }

    private static string BuildSlotOverviewText(MarkSlotSummaryViewItem summary, IReadOnlyList<MarkListViewItem> detailItems)
    {
        if (summary.Count <= 0)
        {
            return $"{summary.SourceScopeLabel} / 0件";
        }

        int missingCount = detailItems.Count(item => !item.Exists);
        return missingCount > 0
            ? $"{summary.SourceScopeLabel} / {summary.Count}件 / 不在 {missingCount}"
            : $"{summary.SourceScopeLabel} / {summary.Count}件";
    }

    private static string BuildSlotSummaryTooltip(MarkSlotSummaryViewItem summary, IReadOnlyList<MarkListViewItem> detailItems)
    {
        string savedAtText = summary.SavedAtLocal?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        string displayName = string.IsNullOrWhiteSpace(summary.DisplayName)
            ? $"スロット {summary.SlotNumber}"
            : summary.DisplayName;
        string stateText = summary.Count <= 0
            ? (IsDefaultSlotDisplayName(summary) && !summary.SavedAtLocal.HasValue ? "空 / 既定状態" : "空 / 変更あり")
            : "保存済み";
        string categoryText = string.IsNullOrWhiteSpace(summary.SourceCategoryName) ? "-" : summary.SourceCategoryName;
        string tabText = string.IsNullOrWhiteSpace(summary.SourceTabDisplayName) ? "-" : summary.SourceTabDisplayName;
        return $"スロット: {summary.SlotNumber}\n表示名: {displayName}\n状態: {stateText}\n保存元: {summary.SourceScopeLabel}\nカテゴリ: {categoryText}\nタブ: {tabText}\n保存: {savedAtText}\n件数: {summary.Count}件\n概要: {BuildSlotOverviewText(summary, detailItems)}\n復元先: 現在タブへ置換";
    }

    private static string BuildSlotDetailToolTip(MarkListViewItem item)
    {
        string typeText = GetMarkItemTypeText(item.FullPath);
        string scopeText = item.IsInCurrentDirectory ? "現在DIR内" : "現在DIR外";
        string existsText = item.Exists ? "存在" : "不在";
        return $"名前: {item.Name}\n種別: {typeText}\n範囲: {scopeText}\n状態: {existsText}\nパス: {item.FullPath}";
    }

    private static bool IsDefaultSlotDisplayName(MarkSlotSummaryViewItem item)
    {
        return string.Equals(item.DisplayName, $"スロット {item.SlotNumber}", StringComparison.CurrentCulture);
    }

    private void ClearCategoryMarks()
    {
        var summary = _globalSummaryProvider();
        DialogResult result = MessageBox.Show(
            $"現在表示中のカテゴリ '{summary.CurrentCategoryName}' に含まれる全タブ (計 {summary.CurrentCategoryTabCount} タブ) のマーク合計 {summary.CurrentCategoryMarkCount} 件を一括解除しますか？\nマークスロットの保存内容は削除されません。",
            "カテゴリ全マーク解除",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            _clearCategoryMarksAction();
            RefreshContents();
        }
    }

    private void ClearGlobalMarks()
    {
        var summary = _globalSummaryProvider();
        DialogResult result = MessageBox.Show(
            $"Workspace 全域 ({summary.GlobalCategoryCount} カテゴリ / 計 {summary.GlobalTabCount} タブ) のマーク合計 {summary.GlobalMarkCount} 件を一括ですべて解除しますか？\nマークスロットの保存内容は削除されません。",
            "Workspace全マーク解除",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            _clearGlobalMarksAction();
            RefreshContents();
        }
    }

    private void ClearCurrentTabMarks()
    {
        var summary = _globalSummaryProvider();
        DialogResult result = MessageBox.Show(
            $"現在タブのマーク {summary.ActiveTabMarkCount} 件をすべて解除しますか？\nマークスロットの保存内容は削除されません。",
            "現在タブマーク解除",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            _clearCurrentTabMarksAction();
            RefreshContents();
        }
    }
}
