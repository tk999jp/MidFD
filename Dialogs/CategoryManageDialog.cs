using MidFD.Configuration;

namespace MidFD.Dialogs;

public sealed class CategoryManageDialog : Form
{
    private sealed class CategoryManageListItem
    {
        public CategoryManageListItem(BrowserTabCategoryDefinition? category, string text)
        {
            Category = category;
            Text = text;
        }

        public BrowserTabCategoryDefinition? Category { get; }
        public string Text { get; }
        public bool IsAddEntry => Category == null;

        public override string ToString()
        {
            return Text;
        }
    }

    private readonly Func<IReadOnlyList<BrowserTabCategoryDefinition>> _categoriesProvider;
    private readonly Func<string?> _addCategoryAction;
    private readonly Func<BrowserTabCategoryDefinition, string?> _renameCategoryAction;
    private readonly Func<BrowserTabCategoryDefinition, string?> _deleteCategoryAction;
    private readonly Func<IReadOnlyList<BrowserTabCategoryDefinition>, string?> _deleteMarkedCategoriesAction;
    private readonly ListView _categoryListView;
    private readonly Button _renameButton;
    private readonly Button _deleteButton;
    private readonly Button _closeButton;
    private readonly Label _summaryLabel;
    private readonly Label _hintLabel;
    private readonly List<BrowserTabCategoryDefinition> _categories = new();
    private readonly HashSet<string> _markedCategoryIds = new(StringComparer.OrdinalIgnoreCase);

    public CategoryManageDialog(
        Func<IReadOnlyList<BrowserTabCategoryDefinition>> categoriesProvider,
        Func<string?> addCategoryAction,
        Func<BrowserTabCategoryDefinition, string?> renameCategoryAction,
        Func<BrowserTabCategoryDefinition, string?> deleteCategoryAction,
        Func<IReadOnlyList<BrowserTabCategoryDefinition>, string?> deleteMarkedCategoriesAction)
    {
        _categoriesProvider = categoriesProvider;
        _addCategoryAction = addCategoryAction;
        _renameCategoryAction = renameCategoryAction;
        _deleteCategoryAction = deleteCategoryAction;
        _deleteMarkedCategoriesAction = deleteMarkedCategoriesAction;

        Text = "カテゴリ管理";
        ClientSize = new Size(464, 420); // Width 480 相当
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;

        int sideMargin = 16;
        int currentTop = 16;
        int contentWidth = ClientSize.Width - (sideMargin * 2);

        _summaryLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 34,
            Text = "上段カテゴリの整理を行います。Space でマーク、Delete で削除できます。追加は一覧末尾の「カテゴリ追加」から行います。"
        };
        _summaryLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_summaryLabel, _summaryLabel.Width, 34);
        currentTop = _summaryLabel.Bottom + 8;

        _hintLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 34,
            Text = "Mark あり: 削除はマーク全件 / Mark なし: current row 1件"
        };
        _hintLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_hintLabel, _hintLabel.Width, 34);
        currentTop = _hintLabel.Bottom + 10;

        _categoryListView = new ListView
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 210,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _categoryListView.Columns.Add("Mark", 64, HorizontalAlignment.Left);
        _categoryListView.Columns.Add("カテゴリ", _categoryListView.Width - 64 - 24, HorizontalAlignment.Left);
        _categoryListView.SelectedIndexChanged += (_, _) => UpdateButtonState();
        _categoryListView.DoubleClick += (_, _) => ActivateSelectedItem();
        _categoryListView.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                ToggleCurrentMark();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Delete)
            {
                DeleteSelectedCategory();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                ActivateSelectedItem();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        _renameButton = new Button
        {
            Text = "名前変更...",
            MinimumSize = new Size(110, 30)
        };
        _renameButton.Click += (_, _) => RenameSelectedCategory();

        _deleteButton = new Button
        {
            Text = "削除",
            MinimumSize = new Size(96, 30)
        };
        _deleteButton.Click += (_, _) => DeleteSelectedCategory();

        _closeButton = new Button
        {
            Text = "閉じる",
            MinimumSize = new Size(96, 30),
            DialogResult = DialogResult.OK
        };

        Controls.Add(_summaryLabel);
        Controls.Add(_hintLabel);
        Controls.Add(_categoryListView);
        Controls.Add(_renameButton);
        Controls.Add(_deleteButton);
        Controls.Add(_closeButton);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            this,
            new[] { _renameButton, _deleteButton, _closeButton },
            _categoryListView.Bottom,
            sideMargin: sideMargin,
            contentGap: 16);

        AcceptButton = _closeButton;
        CancelButton = _closeButton;

        ReloadCategories(_categoriesProvider());
    }

    private void ReloadCategories(IEnumerable<BrowserTabCategoryDefinition> categories)
    {
        string? selectedCategoryId = GetSelectedCategory()?.Id;
        _categories.Clear();
        _categories.AddRange(categories.Select(static category => category.Clone()));
        _markedCategoryIds.RemoveWhere(categoryId =>
            !_categories.Any(category => string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase)));

        _categoryListView.BeginUpdate();
        _categoryListView.Items.Clear();
        foreach (BrowserTabCategoryDefinition category in _categories)
        {
            var listItem = new ListViewItem(_markedCategoryIds.Contains(category.Id) ? "●" : "")
            {
                Tag = new CategoryManageListItem(category, category.DisplayName)
            };
            listItem.SubItems.Add(category.DisplayName);
            _categoryListView.Items.Add(listItem);
        }

        var addItem = new ListViewItem("")
        {
            Tag = new CategoryManageListItem(null, "カテゴリ追加"),
            ForeColor = SystemColors.GrayText
        };
        addItem.SubItems.Add("カテゴリ追加");
        _categoryListView.Items.Add(addItem);

        _categoryListView.EndUpdate();
        SelectBestRow(selectedCategoryId);
        UpdateButtonState();
    }

    private void SelectBestRow(string? categoryId)
    {
        if (_categoryListView.Items.Count == 0)
        {
            return;
        }

        ListViewItem? targetItem = null;
        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            targetItem = _categoryListView.Items
                .Cast<ListViewItem>()
                .FirstOrDefault(item =>
                    ((item.Tag as CategoryManageListItem)?.Category is BrowserTabCategoryDefinition category)
                    && string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        }

        targetItem ??= _categoryListView.Items.Cast<ListViewItem>().FirstOrDefault();
        if (targetItem == null)
        {
            return;
        }

        targetItem.Selected = true;
        targetItem.Focused = true;
        targetItem.EnsureVisible();
    }

    private CategoryManageListItem? GetSelectedListItem()
    {
        return _categoryListView.SelectedItems.Count > 0
            ? _categoryListView.SelectedItems[0].Tag as CategoryManageListItem
            : null;
    }

    private BrowserTabCategoryDefinition? GetSelectedCategory()
    {
        return GetSelectedListItem()?.Category;
    }

    private void UpdateButtonState()
    {
        BrowserTabCategoryDefinition? selectedCategory = GetSelectedCategory();
        int markedCount = GetMarkedCategories().Count;

        _renameButton.Enabled = selectedCategory != null;
        _deleteButton.Enabled = markedCount > 0 || selectedCategory != null;
        _deleteButton.Text = markedCount > 0 ? $"削除({markedCount})" : "削除";
        _hintLabel.Text = markedCount > 0
            ? $"Mark あり: {markedCount} 件を削除対象にします / Space: mark切替 / Enter: current row を名前変更"
            : "Mark なし: current row 1件を削除します / Space: mark切替 / Enter: current row を名前変更";
    }

    private IReadOnlyList<BrowserTabCategoryDefinition> GetMarkedCategories()
    {
        return _categories
            .Where(category => _markedCategoryIds.Contains(category.Id))
            .Select(static category => category.Clone())
            .ToList();
    }

    private void ToggleCurrentMark()
    {
        CategoryManageListItem? selectedItem = GetSelectedListItem();
        BrowserTabCategoryDefinition? selectedCategory = selectedItem?.Category;
        if (selectedCategory == null)
        {
            return;
        }

        if (!_markedCategoryIds.Add(selectedCategory.Id))
        {
            _markedCategoryIds.Remove(selectedCategory.Id);
        }

        ReloadCategories(_categoriesProvider());
    }

    private void ActivateSelectedItem()
    {
        if (GetSelectedListItem()?.IsAddEntry == true)
        {
            AddCategory();
            return;
        }

        RenameSelectedCategory();
    }

    private void AddCategory()
    {
        if (_addCategoryAction() is string status && !string.IsNullOrWhiteSpace(status))
        {
            DialogResult = DialogResult.None;
        }

        ReloadCategories(_categoriesProvider());
    }

    private void RenameSelectedCategory()
    {
        BrowserTabCategoryDefinition? selected = GetSelectedCategory();
        if (selected == null)
        {
            return;
        }

        if (_renameCategoryAction(selected) is string status && !string.IsNullOrWhiteSpace(status))
        {
            DialogResult = DialogResult.None;
        }

        ReloadCategories(_categoriesProvider());
    }

    private void DeleteSelectedCategory()
    {
        IReadOnlyList<BrowserTabCategoryDefinition> markedCategories = GetMarkedCategories();
        if (markedCategories.Count > 0)
        {
            if (_deleteMarkedCategoriesAction(markedCategories) is string status && !string.IsNullOrWhiteSpace(status))
            {
                DialogResult = DialogResult.None;
            }
        }
        else
        {
            BrowserTabCategoryDefinition? selected = GetSelectedCategory();
            if (selected == null)
            {
                return;
            }

            if (_deleteCategoryAction(selected) is string status && !string.IsNullOrWhiteSpace(status))
            {
                DialogResult = DialogResult.None;
            }
        }

        ReloadCategories(_categoriesProvider());
    }
}
