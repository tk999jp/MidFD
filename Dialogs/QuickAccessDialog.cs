using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public class QuickAccessDialog : Form
{
    private const string AllCategoriesFilterLabel = "すべて";
    private const string UncategorizedFilterLabel = "未分類";

    private readonly TabControl _tabControl;
    private readonly TextBox _queryTextBox;
    private readonly ComboBox _categoryFilterComboBox;
    private readonly ListView _registeredListView;
    private readonly ListView _recentListView;
    private readonly ListView _historyListView;
    private readonly Button _okButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _addButton;
    private readonly Button _editButton;
    private readonly Button _deleteButton;
    private readonly Button _moveUpButton;
    private readonly Button _moveDownButton;
    private readonly Label _tabDescriptionLabel;
    private readonly Label _summaryLabel;
    private readonly Label _hintLabel;
    private readonly string _currentPath;
    private readonly QuickAccessStore _workingStore;
    private readonly IReadOnlyList<QuickAccessEntry> _historyEntries;
    private readonly QuickAccessOpenDiagnostics? _diagnostics;
    private readonly Font _registeredHeaderFont;
    private readonly HashSet<string> _collapsedRegisteredCategories = new(StringComparer.OrdinalIgnoreCase);
    private bool _openCompletionLogged;
    private bool _suppressCategoryFilterChanged;

    public string? SelectedPath { get; private set; }
    public QuickAccessEntry? SelectedEntry { get; private set; }
    public QuickAccessStore UpdatedStore => _workingStore.Clone();
    public QuickAccessDialogCloseAction CloseAction { get; private set; } = QuickAccessDialogCloseAction.Cancel;

    public QuickAccessDialog(
        QuickAccessStore store,
        string currentPath,
        IReadOnlyList<QuickAccessEntry> historyEntries,
        QuickAccessOpenDiagnostics? diagnostics = null)
    {
        const int sideMargin = 16;
        const int topMargin = 8;
        _diagnostics = diagnostics;
        _workingStore = _diagnostics?.MeasureStep(
            "QuickAccess.LoadConfig",
            store.Clone,
            cloned => $"itemCount={cloned.Bookmarks.Count + cloned.Aliases.Count + cloned.Commands.Count + cloned.Recents.Count} source=in-memory-store success=success")
            ?? store.Clone();
        _registeredHeaderFont = new Font(Font, FontStyle.Bold);
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
            Height = 20,
            ForeColor = SystemColors.ControlText,
            AutoEllipsis = true,
            TabStop = false
        };
        Controls.Add(_tabDescriptionLabel);
        currentTop = _tabDescriptionLabel.Bottom + 2;

        var queryLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 20,
            Text = "絞り込み (Ctrl+F)",
            AutoSize = true
        };
        Controls.Add(queryLabel);
        currentTop = queryLabel.Bottom + 1;

        int filterLabelWidth = 64;
        int filterGap = 8;
        int filterComboWidth = 190;
        int queryWidth = contentWidth - filterLabelWidth - filterGap - filterComboWidth - filterGap;
        _queryTextBox = new TextBox
        {
            Left = sideMargin,
            Top = currentTop,
            Width = queryWidth,
            Height = 24
        };
        _queryTextBox.TabIndex = 0;
        Controls.Add(_queryTextBox);

        var categoryFilterLabel = new Label
        {
            Left = _queryTextBox.Right + filterGap,
            Top = currentTop + 4,
            Width = filterLabelWidth,
            Height = 20,
            Text = "カテゴリ",
            AutoSize = false
        };
        Controls.Add(categoryFilterLabel);

        _categoryFilterComboBox = new ComboBox
        {
            Left = categoryFilterLabel.Right + filterGap,
            Top = currentTop,
            Width = filterComboWidth,
            Height = 24,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _categoryFilterComboBox.TabIndex = 1;
        Controls.Add(_categoryFilterComboBox);
        currentTop = _queryTextBox.Bottom + 6;

        _tabControl = new TabControl
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 332,
            TabIndex = 0,
            TabStop = false
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

        _registeredListView = CreateListView(includeCategoryColumn: true);
        _recentListView = CreateListView(includeCategoryColumn: false);
        _historyListView = CreateListView(includeCategoryColumn: false);

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
        _addButton.TabIndex = 0;
        _addButton.Click += AddEntry;

        _editButton = new Button
        {
            Text = "選択を編集",
            Width = 104,
            Height = 30,
            Margin = new Padding(0, 0, 4, 0)
        };
        _editButton.TabIndex = 1;
        _editButton.Click += EditSelected;

        _deleteButton = new Button
        {
            Text = "登録先を削除",
            Width = 110,
            Height = 30,
            Margin = new Padding(0, 0, 4, 0)
        };
        _deleteButton.TabIndex = 2;
        _deleteButton.Click += DeleteSelected;

        _moveUpButton = new Button
        {
            Text = "上へ",
            Width = 70,
            Height = 30,
            Margin = new Padding(0, 0, 4, 0)
        };
        _moveUpButton.TabIndex = 3;
        _moveUpButton.Click += MoveSelectedUp;

        _moveDownButton = new Button
        {
            Text = "下へ",
            Width = 70,
            Height = 30,
            Margin = new Padding(0, 0, 0, 0)
        };
        _moveDownButton.TabIndex = 4;
        _moveDownButton.Click += MoveSelectedDown;

        middleButtonPanel.Controls.Add(_addButton);
        middleButtonPanel.Controls.Add(_editButton);
        middleButtonPanel.Controls.Add(_deleteButton);
        middleButtonPanel.Controls.Add(_moveUpButton);
        middleButtonPanel.Controls.Add(_moveDownButton);
        middleButtonPanel.TabIndex = 3;
        middleButtonPanel.TabStop = false;
        Controls.Add(middleButtonPanel);
        currentTop = middleButtonPanel.Bottom + 6;

        _hintLabel = new Label
        {
            Text = GetBottomHintText(),
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 20,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
            TabStop = false
        };
        Controls.Add(_hintLabel);
        currentTop = _hintLabel.Bottom + 1;

        _summaryLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 20,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
            TabStop = false
        };
        Controls.Add(_summaryLabel);
        currentTop = _summaryLabel.Bottom + 1;

        _okButton = new Button
        {
            Text = "移動",
            DialogResult = DialogResult.OK,
            MinimumSize = new Size(80, 30)
        };
        _okButton.TabIndex = 4;
        _okButton.Click += (_, _) => ConfirmSelection();

        _saveButton = new Button
        {
            Text = "閉じる",
            DialogResult = DialogResult.OK,
            MinimumSize = new Size(80, 30)
        };
        _saveButton.TabIndex = 5;
        _saveButton.Click += (_, _) => ConfirmSaveOnly();

        _cancelButton = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            MinimumSize = new Size(80, 30)
        };
        _cancelButton.TabIndex = 6;
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
        _queryTextBox.TextChanged += (_, _) => RefreshItems("QueryChanged");
        _categoryFilterComboBox.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressCategoryFilterChanged)
            {
                return;
            }

            RefreshItems("CategoryChanged");
        };
        _queryTextBox.KeyDown += QueryTextBox_KeyDown;

        RefreshItems("Initial");
        _tabControl.SelectedIndex = 0;
        EnsureSelection(_registeredListView);
        _diagnostics?.MeasureStep("QuickAccess.ApplyUi", () =>
        {
            UpdateContextText();
            UpdateButtonState();
        }, $"itemCount={GetActiveListView().Items.Count} success=success");
        if (_diagnostics == null)
        {
            UpdateContextText();
            UpdateButtonState();
        }

        Shown += (_, _) =>
        {
            BeginInvoke(new Action(FocusActiveListView));
            if (_openCompletionLogged)
            {
                return;
            }

            _openCompletionLogged = true;
            _diagnostics?.LogOpenEnd(GetActiveTabName(), GetActiveListView().Items.Count);
        };
    }

    private ListView CreateListView(bool includeCategoryColumn)
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
            ForeColor = Color.Cyan,
            TabIndex = 0
        };
        listView.Columns.Add("No", 52);
        listView.Columns.Add("表示名", includeCategoryColumn ? 170 : 190);
        if (includeCategoryColumn)
        {
            listView.Columns.Add("カテゴリ", 120);
        }

        listView.Columns.Add("移動先", includeCategoryColumn ? 220 : 300);
        listView.Columns.Add("区分", 80);
        listView.Columns.Add("状態", 170);
        listView.DoubleClick += (_, _) =>
        {
            if (includeCategoryColumn && GetSelectedRegisteredHeaderCategory() != null)
            {
                return;
            }

            ConfirmSelection();
        };
        if (includeCategoryColumn)
        {
            listView.MouseClick += RegisteredListView_MouseClick;
        }
        listView.KeyDown += ListView_KeyDown;

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

        if (e.Control && e.KeyCode == Keys.F)
        {
            FocusSearchBox();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (IsTextInputControlActive(ActiveControl))
        {
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
            return;
        }

    }

    private void RefreshItems(string reason)
    {
        if (_diagnostics != null)
        {
            _diagnostics.MeasureStep(
                "QuickAccess.BuildItems",
                () =>
                {
                    RefreshItemsCore();
                    return GetActiveListView().Items.Count;
                },
                itemCount => $"reason={reason} itemCount={itemCount} success=success");
            return;
        }

        RefreshItemsCore();
    }

    private void RefreshItemsCore()
    {
        string selectedPath = GetSelectedEntry()?.Path ?? string.Empty;
        string? selectedHeaderCategory = GetSelectedRegisteredHeaderCategory();
        RefreshCategoryFilterOptions();
        string query = _queryTextBox.Text;

        IReadOnlyList<QuickAccessEntry> registeredEntries = QuickAccessService.GetRegisteredEntries(_workingStore);
        registeredEntries = FilterRegisteredEntries(registeredEntries, query);
        IReadOnlyList<QuickAccessEntry> recentEntries = QuickAccessService.FilterEntries(QuickAccessService.GetRecentEntries(_workingStore), query, _diagnostics);
        IReadOnlyList<QuickAccessEntry> historyEntries = QuickAccessService.FilterEntries(QuickAccessService.GetHistoryEntries(_historyEntries), query, _diagnostics);

        RefreshRegisteredList(registeredEntries);
        RefreshList(_recentListView, recentEntries);
        RefreshList(_historyListView, historyEntries);

        UpdateTabText(registeredEntries.Count, recentEntries.Count, historyEntries.Count);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            SelectPath(GetActiveListView(), selectedPath);
        }
        else if (!string.IsNullOrWhiteSpace(selectedHeaderCategory))
        {
            SelectRegisteredCategoryHeader(selectedHeaderCategory);
        }

        UpdateSummaryText();
    }

    private void RefreshRegisteredList(IReadOnlyList<QuickAccessEntry> entries)
    {
        int visibleOrdinal = 0;
        _registeredListView.BeginUpdate();
        _registeredListView.Items.Clear();
        foreach (string category in QuickAccessService.GetRegisteredCategoryOrder(entries))
        {
            List<QuickAccessEntry> categoryEntries = entries
                .Where(entry => string.Equals(QuickAccessService.GetEntryCategoryLabel(entry), category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (categoryEntries.Count == 0)
            {
                continue;
            }

            bool collapsed = _collapsedRegisteredCategories.Contains(category);
            var headerItem = new ListViewItem(string.Empty)
            {
                Tag = new RegisteredCategoryHeaderRow(category),
                BackColor = Color.FromArgb(24, 48, 96),
                ForeColor = Color.White,
                Font = _registeredHeaderFont,
                ToolTipText = $"{category} / {(collapsed ? "格納" : "展開")} / Enter, Space, ←, → で切り替え"
            };
            headerItem.SubItems.Add($"{(collapsed ? "▶" : "▼")} {category} ({categoryEntries.Count})");
            headerItem.SubItems.Add(string.Empty);
            headerItem.SubItems.Add(string.Empty);
            headerItem.SubItems.Add(string.Empty);
            headerItem.SubItems.Add(string.Empty);
            headerItem.SubItems.Add(string.Empty);
            _registeredListView.Items.Add(headerItem);

            if (collapsed)
            {
                continue;
            }

            foreach (QuickAccessEntry entry in categoryEntries)
            {
                visibleOrdinal++;
                string status = QuickAccessService.GetEntryStatusLabel(entry, _currentPath, _diagnostics);
                string categoryLabel = QuickAccessService.GetEntryCategoryLabel(entry);
                var item = new ListViewItem(GetVisibleNumberText(visibleOrdinal));
                item.SubItems.Add(entry.DisplayName);
                item.SubItems.Add(categoryLabel);
                item.SubItems.Add(QuickAccessService.GetEntryValueLabel(entry));
                item.SubItems.Add(QuickAccessService.GetEntryKindLabel(entry));
                item.SubItems.Add(status);
                item.ToolTipText = QuickAccessService.GetEntryTooltipText(entry, _currentPath, _diagnostics, status);
                item.Tag = entry;
                if (status.StartsWith("見つからない", StringComparison.Ordinal))
                {
                    item.ForeColor = Color.DarkSalmon;
                }
                else if (status.StartsWith("現在地", StringComparison.Ordinal))
                {
                    item.ForeColor = Color.LightGreen;
                }

                _registeredListView.Items.Add(item);
            }
        }

        _registeredListView.EndUpdate();
    }

    private void RefreshList(ListView listView, IReadOnlyList<QuickAccessEntry> entries)
    {
        int visibleOrdinal = 0;
        listView.BeginUpdate();
        listView.Items.Clear();

        foreach (QuickAccessEntry entry in entries)
        {
            visibleOrdinal++;
            string status = QuickAccessService.GetEntryStatusLabel(entry, _currentPath, _diagnostics);
            var item = new ListViewItem(GetVisibleNumberText(visibleOrdinal));
            item.SubItems.Add(entry.DisplayName);
            item.SubItems.Add(QuickAccessService.GetEntryValueLabel(entry));
            item.SubItems.Add(QuickAccessService.GetEntryKindLabel(entry));
            item.SubItems.Add(status);
            item.ToolTipText = QuickAccessService.GetEntryTooltipText(entry, _currentPath, _diagnostics, status);
            item.Tag = entry;
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

    private IReadOnlyList<QuickAccessEntry> FilterRegisteredEntries(IReadOnlyList<QuickAccessEntry> entries, string query)
    {
        string categoryFilter = GetSelectedCategoryFilterLabel();
        IEnumerable<QuickAccessEntry> filtered = entries;
        if (!string.Equals(categoryFilter, AllCategoriesFilterLabel, StringComparison.Ordinal))
        {
            filtered = filtered.Where(entry =>
            {
                string category = QuickAccessService.GetEntryCategoryLabel(entry);
                return string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase);
            });
        }

        return QuickAccessService.FilterEntries(filtered, query, _diagnostics);
    }

    private void RefreshCategoryFilterOptions()
    {
        string previous = GetSelectedCategoryFilterLabel();
        var categories = QuickAccessService.GetKnownCategoryNames(_workingStore);
        _suppressCategoryFilterChanged = true;
        _categoryFilterComboBox.BeginUpdate();
        _categoryFilterComboBox.Items.Clear();
        _categoryFilterComboBox.Items.Add(AllCategoriesFilterLabel);
        _categoryFilterComboBox.Items.Add(UncategorizedFilterLabel);
        foreach (string category in categories)
        {
            _categoryFilterComboBox.Items.Add(category);
        }

        int index = _categoryFilterComboBox.FindStringExact(previous);
        _categoryFilterComboBox.SelectedIndex = index >= 0 ? index : 0;
        _categoryFilterComboBox.EndUpdate();
        _suppressCategoryFilterChanged = false;
    }

    private string GetSelectedCategoryFilterLabel()
    {
        return _categoryFilterComboBox.SelectedItem as string ?? AllCategoriesFilterLabel;
    }

    private bool HasActiveRegisteredFilter()
    {
        return !string.IsNullOrWhiteSpace(_queryTextBox.Text) ||
               !string.Equals(GetSelectedCategoryFilterLabel(), AllCategoriesFilterLabel, StringComparison.Ordinal);
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

    protected override bool ProcessDialogKey(Keys keyData)
    {
        bool isCtrlTab = (keyData & Keys.Control) == Keys.Control && (keyData & Keys.KeyCode) == Keys.Tab;
        if (isCtrlTab)
        {
            int nextIndex = (keyData & Keys.Shift) == Keys.Shift
                ? (_tabControl.SelectedIndex + _tabControl.TabPages.Count - 1) % _tabControl.TabPages.Count
                : (_tabControl.SelectedIndex + 1) % _tabControl.TabPages.Count;
            _tabControl.SelectedIndex = nextIndex;
            return true;
        }

        if ((keyData & Keys.KeyCode) != Keys.Tab)
        {
            return base.ProcessDialogKey(keyData);
        }

        bool reverse = (keyData & Keys.Shift) == Keys.Shift;
        return HandleQuickAccessTabNavigation(reverse) || base.ProcessDialogKey(keyData);
    }

    private bool HandleQuickAccessTabNavigation(bool reverse)
    {
        Control? active = ActiveControl;
        if (ReferenceEquals(active, _queryTextBox))
        {
            if (reverse)
            {
                FocusActiveListView();
            }
            else
            {
                _categoryFilterComboBox.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _categoryFilterComboBox))
        {
            if (reverse)
            {
                _queryTextBox.Focus();
            }
            else
            {
                _addButton.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _addButton))
        {
            if (reverse)
            {
                _categoryFilterComboBox.Focus();
            }
            else
            {
                _editButton.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _editButton))
        {
            if (reverse)
            {
                _addButton.Focus();
            }
            else
            {
                _deleteButton.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _deleteButton))
        {
            if (reverse)
            {
                _editButton.Focus();
            }
            else
            {
                _moveUpButton.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _moveUpButton))
        {
            if (reverse)
            {
                _deleteButton.Focus();
            }
            else
            {
                _moveDownButton.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _moveDownButton))
        {
            if (reverse)
            {
                _moveUpButton.Focus();
            }
            else
            {
                _okButton.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _okButton))
        {
            if (reverse)
            {
                _moveDownButton.Focus();
            }
            else
            {
                _saveButton.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _saveButton))
        {
            if (reverse)
            {
                _okButton.Focus();
            }
            else
            {
                _cancelButton.Focus();
            }

            return true;
        }

        if (ReferenceEquals(active, _cancelButton))
        {
            if (reverse)
            {
                _saveButton.Focus();
            }
            else
            {
                FocusActiveListView();
            }

            return true;
        }

        if (active is ListView)
        {
            if (reverse)
            {
                _cancelButton.Focus();
            }
            else
            {
                _queryTextBox.Focus();
                _queryTextBox.SelectAll();
            }

            return true;
        }

        FocusActiveListView();
        return true;
    }

    private void ListView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.F)
        {
            FocusSearchBox();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.OemQuestion || e.KeyCode == Keys.Oem2)
        {
            FocusSearchBox();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (TryHandleDigitSelectionShortcut(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (!ReferenceEquals(sender, _registeredListView))
        {
            return;
        }

        if (e.KeyCode == Keys.Left)
        {
            if (SetSelectedRegisteredCategoryCollapsed(collapse: true))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }

            return;
        }

        if (e.KeyCode == Keys.Right)
        {
            if (SetSelectedRegisteredCategoryCollapsed(collapse: false))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }

            return;
        }

        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
        {
            if (ToggleRegisteredHeaderIfSelected())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }
    }

    private void RegisteredListView_MouseClick(object? sender, MouseEventArgs e)
    {
        ListViewHitTestInfo hit = _registeredListView.HitTest(e.Location);
        if (hit.Item?.Tag is not RegisteredCategoryHeaderRow header)
        {
            return;
        }

        hit.Item.Selected = true;
        hit.Item.Focused = true;
        hit.Item.EnsureVisible();
        ToggleRegisteredHeaderIfSelected();
        RestoreRegisteredHeaderFocus(header.CategoryLabel);
    }

    private void EnsureSelection(ListView listView)
    {
        if (listView.Items.Count == 0 || listView.SelectedItems.Count > 0)
        {
            return;
        }

        ListViewItem targetItem = listView.Items
            .Cast<ListViewItem>()
            .FirstOrDefault(item => item.Tag is QuickAccessEntry)
            ?? listView.Items[0];
        targetItem.Selected = true;
        targetItem.Focused = true;
        targetItem.EnsureVisible();
    }

    internal static bool IsTextInputControlActive(Control? activeControl)
    {
        if (activeControl is TextBoxBase)
        {
            return true;
        }

        return activeControl is ComboBox comboBox &&
               comboBox.DropDownStyle != ComboBoxStyle.DropDownList;
    }

    internal static bool TryGetPlainDigitShortcutOrdinal(Keys keyData, out int ordinal)
    {
        bool hasModifier = (keyData & Keys.Control) == Keys.Control ||
                           (keyData & Keys.Shift) == Keys.Shift ||
                           (keyData & Keys.Alt) == Keys.Alt;
        if (hasModifier)
        {
            ordinal = 0;
            return false;
        }

        ordinal = (keyData & Keys.KeyCode) switch
        {
            Keys.D1 or Keys.NumPad1 => 1,
            Keys.D2 or Keys.NumPad2 => 2,
            Keys.D3 or Keys.NumPad3 => 3,
            Keys.D4 or Keys.NumPad4 => 4,
            Keys.D5 or Keys.NumPad5 => 5,
            Keys.D6 or Keys.NumPad6 => 6,
            Keys.D7 or Keys.NumPad7 => 7,
            Keys.D8 or Keys.NumPad8 => 8,
            Keys.D9 or Keys.NumPad9 => 9,
            _ => 0
        };

        return ordinal != 0;
    }

    private bool TryHandleDigitSelectionShortcut(Keys keyData)
    {
        if (!TryGetPlainDigitShortcutOrdinal(keyData, out int ordinal))
        {
            return false;
        }

        NumberedQuickAccessItem? targetItem = GetVisibleNumberedItems()
            .FirstOrDefault(item => item.Ordinal == ordinal);
        if (targetItem == null)
        {
            return false;
        }

        SelectPath(GetActiveListView(), targetItem.Entry.Path);
        ConfirmSelection();
        return true;
    }

    private IReadOnlyList<NumberedQuickAccessItem> GetVisibleNumberedItems()
    {
        return GetVisibleNumberedItemsFor(GetActiveListView());
    }

    private static IReadOnlyList<NumberedQuickAccessItem> GetVisibleNumberedItemsFor(ListView listView)
    {
        int ordinal = 0;
        return listView.Items
            .Cast<ListViewItem>()
            .Select(item => new { item, entry = item.Tag as QuickAccessEntry })
            .Where(x => x.entry != null)
            .Select(x =>
            {
                ordinal++;
                return new NumberedQuickAccessItem(ordinal, x.entry!, x.item);
            })
            .ToList();
    }

    private IEnumerable<QuickAccessEntry> GetVisibleNumberedEntries()
    {
        return GetVisibleNumberedItems().Select(item => item.Entry);
    }

    private static string GetVisibleNumberText(int ordinal)
    {
        return ordinal is >= 1 and <= 9 ? ordinal.ToString() : string.Empty;
    }

    private bool ToggleRegisteredHeaderIfSelected()
    {
        string? category = GetSelectedRegisteredHeaderCategory();
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        bool collapse = !_collapsedRegisteredCategories.Contains(category);
        return SetRegisteredCategoryCollapsed(category, collapse);
    }

    private bool SetSelectedRegisteredCategoryCollapsed(bool collapse)
    {
        string? category = GetSelectedRegisteredCategoryLabel();
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        return SetRegisteredCategoryCollapsed(category, collapse);
    }

    private bool SetRegisteredCategoryCollapsed(string category, bool collapse)
    {
        bool changed = collapse
            ? _collapsedRegisteredCategories.Add(category)
            : _collapsedRegisteredCategories.Remove(category);
        if (!changed)
        {
            return false;
        }

        RefreshItems(collapse ? "CollapseCategory" : "ExpandCategory");
        SelectRegisteredCategoryHeader(category);
        RestoreRegisteredHeaderFocus(category);
        return true;
    }

    private void RestoreRegisteredHeaderFocus(string category)
    {
        BeginInvoke(new Action(() =>
        {
            SelectRegisteredCategoryHeader(category);
            ActiveControl = _registeredListView;
            _registeredListView.Select();
            _registeredListView.Focus();
        }));
    }

    private string? GetSelectedRegisteredCategoryLabel()
    {
        ListViewItem? item = GetSelectedRegisteredListViewItem();
        if (item == null)
        {
            return null;
        }

        if (item.Tag is RegisteredCategoryHeaderRow header)
        {
            return header.CategoryLabel;
        }

        if (item.Tag is QuickAccessEntry entry)
        {
            return QuickAccessService.GetEntryCategoryLabel(entry);
        }

        return null;
    }

    private string? GetSelectedRegisteredHeaderCategory()
    {
        ListViewItem? item = GetSelectedRegisteredListViewItem();
        return item?.Tag is RegisteredCategoryHeaderRow header ? header.CategoryLabel : null;
    }

    private ListViewItem? GetSelectedRegisteredListViewItem()
    {
        if (_registeredListView.SelectedItems.Count == 0)
        {
            return null;
        }

        return _registeredListView.SelectedItems[0];
    }

    private void SelectRegisteredCategoryHeader(string category)
    {
        foreach (ListViewItem item in _registeredListView.Items)
        {
            if (item.Tag is RegisteredCategoryHeaderRow header &&
                string.Equals(header.CategoryLabel, category, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
                break;
            }
        }
    }

    private void UpdateButtonState()
    {
        QuickAccessEntry? entry = GetSelectedEntry();
        string? selectedHeaderCategory = GetSelectedRegisteredHeaderCategory();
        bool headerSelected = !string.IsNullOrWhiteSpace(selectedHeaderCategory);
        _okButton.Enabled = entry != null;
        _saveButton.Enabled = true;
        bool canEdit = !headerSelected &&
                       (entry?.Kind == QuickAccessEntryKind.Bookmark || entry?.Kind == QuickAccessEntryKind.Alias);
        _editButton.Enabled = canEdit;
        _deleteButton.Enabled = canEdit;
        _categoryFilterComboBox.Enabled = _tabControl.SelectedIndex == 0;
        bool canReorderItems = canEdit &&
                               _tabControl.SelectedIndex == 0 &&
                               !HasActiveRegisteredFilter();
        bool canReorderCategory = headerSelected &&
                                  _tabControl.SelectedIndex == 0 &&
                                  !HasActiveRegisteredFilter();
        _moveUpButton.Enabled = canReorderCategory
            ? QuickAccessService.CanMoveRegisteredCategory(_workingStore, selectedHeaderCategory!, moveUp: true)
            : canReorderItems && QuickAccessService.CanMoveManagedEntry(_workingStore, entry!, moveUp: true);
        _moveDownButton.Enabled = canReorderCategory
            ? QuickAccessService.CanMoveRegisteredCategory(_workingStore, selectedHeaderCategory!, moveUp: false)
            : canReorderItems && QuickAccessService.CanMoveManagedEntry(_workingStore, entry!, moveUp: false);
        _okButton.Text = "移動";
        _addButton.Text = _tabControl.SelectedIndex == 0 ? "登録を追加..." : "選択を登録...";
        UpdateContextText();
        UpdateSummaryText();
    }

    private void UpdateContextText()
    {
        _hintLabel.Text = GetBottomHintText();
        _tabDescriptionLabel.Text = _tabControl.SelectedIndex switch
        {
            1 => "最近: Ctrl+Fで検索欄へ移れます。",
            2 => "履歴: Ctrl+Fで検索欄へ移れます。",
            _ => "登録先: Ctrl+Fで検索、Space/←/→でカテゴリ開閉できます。"
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
        string categoryGuide = _tabControl.SelectedIndex == 0
            ? $" / {GetSelectedCategoryFilterLabel()}"
            : string.Empty;
        string baseSummary = $"表示 {visibleCount} 件 / {tabName}{categoryGuide} / 状態: {stateGuide}";
        _summaryLabel.Text = baseSummary;
    }

    private string GetBottomHintText()
    {
        string headerHint = _tabControl.SelectedIndex == 0 && !string.IsNullOrWhiteSpace(GetSelectedRegisteredHeaderCategory())
            ? " / ヘッダー選択時: 上へ/下へ=カテゴリ移動"
            : string.Empty;
        return _tabControl.SelectedIndex switch
            {
            1 => "Enter=移動 / Ctrl+F=検索 / ↓=一覧 / Ctrl+Tab=切替 / No列1-9=直行",
            2 => "Enter=移動 / Ctrl+F=検索 / ↓=一覧 / Ctrl+Tab=切替 / No列1-9=直行",
            _ => $"Enter=移動 / Ctrl+F=検索 / Space・←→=開閉 / Ctrl+Tab=切替 / No列1-9=直行 / Insert,N=登録 / F4=編集 / Delete=削除{headerHint}"
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
            RefreshItems("AddEntry");
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
            RefreshItems("EditSelected");
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
            RefreshItems("DeleteSelected");
            _tabControl.SelectedIndex = 0;
            EnsureSelection(_registeredListView);
            UpdateButtonState();
        }
    }

    private void MoveSelectedUp(object? sender, EventArgs e)
    {
        MoveSelectedEntry(moveUp: true);
    }

    private void MoveSelectedDown(object? sender, EventArgs e)
    {
        MoveSelectedEntry(moveUp: false);
    }

    private void MoveSelectedEntry(bool moveUp)
    {
        if (HasActiveRegisteredFilter())
        {
            return;
        }

        string? selectedHeaderCategory = GetSelectedRegisteredHeaderCategory();
        if (!string.IsNullOrWhiteSpace(selectedHeaderCategory))
        {
            if (!QuickAccessService.TryMoveRegisteredCategory(_workingStore, selectedHeaderCategory, moveUp))
            {
                return;
            }

            RefreshItems(moveUp ? "MoveCategoryUp" : "MoveCategoryDown");
            _tabControl.SelectedIndex = 0;
            RestoreRegisteredHeaderFocus(selectedHeaderCategory);
            return;
        }

        QuickAccessEntry? entry = GetSelectedEntry();
        if (entry == null)
        {
            return;
        }

        if (!QuickAccessService.TryMoveManagedEntry(_workingStore, entry, moveUp))
        {
            return;
        }

        RefreshItems(moveUp ? "MoveUp" : "MoveDown");
        _tabControl.SelectedIndex = 0;
        SelectPath(_registeredListView, entry.Path);
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
        IReadOnlyList<QuickAccessEntry> historyEntries,
        QuickAccessOpenDiagnostics? diagnostics = null)
    {
        using var dialog = new QuickAccessDialog(store, currentPath, historyEntries, diagnostics);
        if (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            diagnostics?.LogDialogClose(dialog.CloseAction.ToString(), dialog.SelectedEntry?.Path);
            return new QuickAccessDialogResult(dialog.CloseAction, dialog.SelectedEntry, dialog.UpdatedStore);
        }

        diagnostics?.LogDialogClose(QuickAccessDialogCloseAction.Cancel.ToString(), null);
        return new QuickAccessDialogResult(QuickAccessDialogCloseAction.Cancel, null, null);
    }

    private string GetActiveTabName()
    {
        return _tabControl.SelectedIndex switch
        {
            1 => "Recent",
            2 => "History",
            _ => "Registered"
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _registeredHeaderFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record NumberedQuickAccessItem(int Ordinal, QuickAccessEntry Entry, ListViewItem Item);
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

file sealed record RegisteredCategoryHeaderRow(string CategoryLabel);
