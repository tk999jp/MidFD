using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class MarkSlotManagementDialog : Form
{
    private sealed class SingleLineInputForm : Form
    {
        private readonly TextBox _textBox;

        public string InputText => _textBox.Text;

        public SingleLineInputForm(string title, string prompt, string initialValue)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(420, 140);

            var label = new Label
            {
                Text = prompt,
                Left = 12,
                Top = 12,
                Width = 396,
                Height = 35
            };

            _textBox = new TextBox
            {
                Text = initialValue,
                Left = 12,
                Top = 50,
                Width = 396
            };

            var okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Left = 240,
                Top = 90,
                Width = 80
            };

            var cancelButton = new Button
            {
                Text = "キャンセル",
                DialogResult = DialogResult.Cancel,
                Left = 328,
                Top = 90,
                Width = 80
            };

            AcceptButton = okButton;
            CancelButton = cancelButton;

            Controls.Add(label);
            Controls.Add(_textBox);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
        }

        public static string? ShowInput(IWin32Window owner, string title, string prompt, string initialValue)
        {
            using var form = new SingleLineInputForm(title, prompt, initialValue);
            return form.ShowDialog(owner) == DialogResult.OK ? form.InputText : null;
        }
    }

    private readonly int _initialSlotNumber;
    private readonly Func<IReadOnlyList<MarkSlotDialog.MarkSlotSummaryViewItem>> _slotItemsProvider;
    private readonly Func<int, IReadOnlyList<MarkSlotDialog.MarkListViewItem>> _slotItemsDetailProvider;
    private readonly Func<int, string?, string> _renameSlotAction;
    private readonly Func<int, string> _deleteSlotAction;
    private readonly Func<int, IReadOnlyCollection<string>, MarkSlotActionResult> _removeSlotItemsAction;
    private readonly HashSet<string> _markedPaths = new(StringComparer.OrdinalIgnoreCase);
    private int? _markSelectionAnchorIndex;
    private int? _mouseDownItemIndex;
    private bool _mouseDownItemWasMarked;
    private string? _mouseDownItemPath;
    private readonly Func<int, string> _saveCategorySlotAction;
    private readonly Func<int, string> _saveWorkspaceSlotAction;
    private readonly Action<int> _openSlotSetOperationAction;
    private readonly bool _allowSlotSetOperation;
    private readonly Func<int, string> _exportSlotAction;
    private readonly Func<int, string> _importSlotAction;
    private readonly Func<string> _exportAllSlotsAction;
    private readonly Func<string> _importAllSlotsAction;
    private readonly bool _allowSlotBackupTransfer;
    private readonly Action<string, string, MessageBoxIcon> _showMessageAction;

    private readonly SplitContainer _mainSplitContainer;
    private readonly ListView _slotListView;
    private readonly ListView _slotContentListView;
    private readonly Label _slotHeaderLabel;
    private readonly Label _slotContentHeaderLabel;

    private readonly Button _renameButton;
    private readonly Button _deleteButton;
    private readonly Button _deleteMarkedItemsButton;
    private readonly Button _setOperationButton;

    private readonly Button _saveScopeMenuButton;
    private readonly ContextMenuStrip _saveScopeMenu;

    private readonly Button? _importMenuButton;
    private readonly ContextMenuStrip? _importMenu;

    private readonly Button? _exportMenuButton;
    private readonly ContextMenuStrip? _exportMenu;

    private readonly Button _closeButton;

    internal Func<IWin32Window, string, string, MessageBoxButtons, MessageBoxIcon, DialogResult> ConfirmMessageBoxShow
        = (owner, text, caption, buttons, icon) => MessageBox.Show(owner, text, caption, buttons, icon);

    public MarkSlotManagementDialog(
        int initialSlotNumber,
        Func<IReadOnlyList<MarkSlotDialog.MarkSlotSummaryViewItem>> slotItemsProvider,
        Func<int, IReadOnlyList<MarkSlotDialog.MarkListViewItem>> slotItemsDetailProvider,
        Func<int, string?, string> renameSlotAction,
        Func<int, string> deleteSlotAction,
        Func<int, IReadOnlyCollection<string>, MarkSlotActionResult> removeSlotItemsAction,
        Func<int, string> saveCategorySlotAction,
        Func<int, string> saveWorkspaceSlotAction,
        Action<int> openSlotSetOperationAction,
        bool allowSlotSetOperation,
        Func<int, string> exportSlotAction,
        Func<int, string> importSlotAction,
        Func<string> exportAllSlotsAction,
        Func<string> importAllSlotsAction,
        bool allowSlotBackupTransfer,
        Action<string, string, MessageBoxIcon>? showMessageAction = null)
    {
        _initialSlotNumber = initialSlotNumber;
        _slotItemsProvider = slotItemsProvider;
        _slotItemsDetailProvider = slotItemsDetailProvider;
        _renameSlotAction = renameSlotAction;
        _deleteSlotAction = deleteSlotAction;
        _removeSlotItemsAction = removeSlotItemsAction;
        _saveCategorySlotAction = saveCategorySlotAction;
        _saveWorkspaceSlotAction = saveWorkspaceSlotAction;
        _openSlotSetOperationAction = openSlotSetOperationAction;
        _allowSlotSetOperation = allowSlotSetOperation;
        _exportSlotAction = exportSlotAction;
        _importSlotAction = importSlotAction;
        _exportAllSlotsAction = exportAllSlotsAction;
        _importAllSlotsAction = importAllSlotsAction;
        _allowSlotBackupTransfer = allowSlotBackupTransfer;
        _showMessageAction = showMessageAction ?? ((msg, title, icon) => MessageBox.Show(this, msg, title, MessageBoxButtons.OK, icon));

        Text = "マークスロット管理";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(920, 620);
        MinimumSize = new Size(920, 520);

        _slotHeaderLabel = new Label
        {
            Text = "選択中スロット: なし",
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            Padding = new Padding(4, 0, 0, 0)
        };

        _slotListView = CreateListView(new[]
        {
            ("Slot", 60),
            ("名前", 180),
            ("件数", 70),
            ("範囲", 120),
            ("保存日時", 150)
        });
        _slotListView.SelectedIndexChanged += (_, _) => OnSelectedSlotChanged();

        var slotContainer = new Panel { Dock = DockStyle.Fill };
        slotContainer.Controls.Add(_slotListView);
        slotContainer.Controls.Add(_slotHeaderLabel);

        _slotContentHeaderLabel = new Label
        {
            Text = "保存済み内容（0件）",
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            Padding = new Padding(4, 0, 0, 0)
        };

        _slotContentListView = CreateListView(new[]
        {
            ("Mark", 50),
            ("種別", 60),
            ("名前", 180),
            ("場所", 380),
            ("状態", 90)
        });
        _slotContentListView.MultiSelect = true;
        _slotContentListView.KeyDown += SlotContentListView_KeyDown;
        _slotContentListView.MouseDown += SlotContentListView_MouseDown;
        _slotContentListView.MouseUp += SlotContentListView_MouseUp;

        var slotContentContainer = new Panel { Dock = DockStyle.Fill };
        slotContentContainer.Controls.Add(_slotContentListView);
        slotContentContainer.Controls.Add(_slotContentHeaderLabel);

        _mainSplitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            Padding = new Padding(10, 10, 10, 0)
        };
        _mainSplitContainer.Panel1.Controls.Add(slotContainer);
        _mainSplitContainer.Panel2.Controls.Add(slotContentContainer);

        // Footer ボタン群
        _renameButton = new Button { Text = "名前変更...", AutoSize = true, Height = 32 };
        _renameButton.Click += (_, _) => ExecuteRenameSelectedSlot();

        _deleteButton = new Button { Text = "スロットを削除...", AutoSize = true, Height = 32 };
        _deleteButton.Click += (_, _) => ExecuteDeleteSelectedSlot();

        _deleteMarkedItemsButton = new Button { Text = "マーク項目を削除...", AutoSize = true, Height = 32 };
        _deleteMarkedItemsButton.Click += (_, _) => ExecuteDeleteMarkedItems();

        _setOperationButton = new Button { Text = "集合演算...", AutoSize = true, Height = 32 };
        _setOperationButton.Click += (_, _) => ExecuteOpenSetOperation();
        _setOperationButton.Visible = _allowSlotSetOperation;

        _saveScopeMenu = new ContextMenuStrip();
        _saveScopeMenu.Items.Add("現在カテゴリ全タブのMarkを選択スロットへ保存...", null, (_, _) => ExecuteSaveCategoryToSelectedSlot());
        _saveScopeMenu.Items.Add("Workspace全体のMarkを選択スロットへ保存...", null, (_, _) => ExecuteSaveWorkspaceToSelectedSlot());

        _saveScopeMenuButton = new Button { Text = "保存範囲 ▼", AutoSize = true, Height = 32 };
        _saveScopeMenuButton.Click += (s, _) =>
        {
            if (s is Control ctrl) _saveScopeMenu.Show(ctrl, new Point(0, ctrl.Height));
        };

        if (_allowSlotBackupTransfer)
        {
            _importMenu = new ContextMenuStrip();
            _importMenu.Items.Add("選択スロットへインポート...", null, (_, _) => ExecuteImportSelectedSlot());
            _importMenu.Items.Add("全スロットをインポート（全置換）...", null, (_, _) => ExecuteImportAllSlots());

            _importMenuButton = new Button { Text = "インポート ▼", AutoSize = true, Height = 32 };
            _importMenuButton.Click += (s, _) =>
            {
                if (s is Control ctrl) _importMenu.Show(ctrl, new Point(0, ctrl.Height));
            };

            _exportMenu = new ContextMenuStrip();
            _exportMenu.Items.Add("選択スロットをエクスポート...", null, (_, _) => ExecuteExportSelectedSlot());
            _exportMenu.Items.Add("全スロットをエクスポート...", null, (_, _) => ExecuteExportAllSlots());

            _exportMenuButton = new Button { Text = "エクスポート ▼", AutoSize = true, Height = 32 };
            _exportMenuButton.Click += (s, _) =>
            {
                if (s is Control ctrl) _exportMenu.Show(ctrl, new Point(0, ctrl.Height));
            };
        }

        _closeButton = new Button { Text = "閉じる", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
        _closeButton.Click += (_, _) => Close();

        var footerFlowPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(5, 4, 5, 4)
        };
        footerFlowPanel.Controls.Add(_renameButton);
        footerFlowPanel.Controls.Add(_deleteButton);
        footerFlowPanel.Controls.Add(_deleteMarkedItemsButton);
        if (_allowSlotSetOperation) footerFlowPanel.Controls.Add(_setOperationButton);
        footerFlowPanel.Controls.Add(_saveScopeMenuButton);
        if (_allowSlotBackupTransfer && _importMenuButton != null) footerFlowPanel.Controls.Add(_importMenuButton);
        if (_allowSlotBackupTransfer && _exportMenuButton != null) footerFlowPanel.Controls.Add(_exportMenuButton);

        var footerRightPanel = new Panel
        {
            Dock = DockStyle.Right,
            Width = 100,
            Padding = new Padding(5, 4, 5, 4)
        };
        footerRightPanel.Controls.Add(_closeButton);

        var footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 45,
            Padding = new Padding(10, 0, 10, 5)
        };
        footerPanel.Controls.Add(footerFlowPanel);
        footerPanel.Controls.Add(footerRightPanel);

        Controls.Add(_mainSplitContainer);
        Controls.Add(footerPanel);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        RefreshSlotItems();
        Shown += (_, _) =>
        {
            _mainSplitContainer.SplitterDistance = (int)(_mainSplitContainer.Height * 0.35);
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

    public void RefreshSlotItems()
    {
        int? previousSlot = GetSelectedSlotSummary()?.SlotNumber ?? _initialSlotNumber;
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
        OnSelectedSlotChanged();
    }

    private void OnSelectedSlotChanged()
    {
        _markedPaths.Clear();
        _markSelectionAnchorIndex = null;
        _mouseDownItemIndex = null;
        var selected = GetSelectedSlotSummary();
        if (selected == null)
        {
            _slotHeaderLabel.Text = "選択中スロット: なし";
            _slotContentHeaderLabel.Text = "保存済み内容（0件 / Mark 0件）";
            _slotContentListView.Items.Clear();
            _renameButton.Enabled = false;
            _deleteButton.Enabled = false;
            _deleteMarkedItemsButton.Enabled = false;
            _setOperationButton.Enabled = false;
            _saveScopeMenuButton.Enabled = false;
            if (_importMenuButton != null) _importMenuButton.Enabled = false;
            if (_exportMenuButton != null) _exportMenuButton.Enabled = false;
            return;
        }

        string dateStr = selected.SavedAtLocal.HasValue ? selected.SavedAtLocal.Value.ToString("yyyy-MM-dd HH:mm") : "-";
        _slotHeaderLabel.Text = $"選択中スロット: Slot {selected.SlotNumber}「{selected.DisplayName}」 / {selected.Count}件 / {selected.SourceScopeLabel} / 保存日時: {dateStr}";

        _renameButton.Enabled = true;
        _deleteButton.Enabled = true;
        _setOperationButton.Enabled = true;
        _saveScopeMenuButton.Enabled = true;
        if (_importMenuButton != null) _importMenuButton.Enabled = true;
        if (_exportMenuButton != null) _exportMenuButton.Enabled = true;

        RefreshSlotContentItems(selected.SlotNumber);
    }

    private void RefreshSlotContentItems(int slotNumber)
    {
        _slotContentListView.BeginUpdate();
        _slotContentListView.Items.Clear();

        var details = _slotItemsDetailProvider(slotNumber);
        var detailPaths = new HashSet<string>(details.Select(d => d.FullPath), StringComparer.OrdinalIgnoreCase);
        _markedPaths.RemoveWhere(path => !detailPaths.Contains(path));

        _slotContentHeaderLabel.Text = $"保存済み内容（{details.Count}件 / Mark {_markedPaths.Count}件）";

        foreach (var item in details)
        {
            bool isMarked = _markedPaths.Contains(item.FullPath);
            var row = new ListViewItem(isMarked ? "ON" : "");
            row.SubItems.Add(GetMarkItemTypeText(item.FullPath));
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
        UpdateDeleteMarkedButtonState();
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

    public MarkSlotDialog.MarkSlotSummaryViewItem? GetSelectedSlotSummary()
    {
        if (_slotListView.SelectedItems.Count > 0)
        {
            return _slotListView.SelectedItems[0].Tag as MarkSlotDialog.MarkSlotSummaryViewItem;
        }
        foreach (ListViewItem item in _slotListView.Items)
        {
            if (item.Selected)
            {
                return item.Tag as MarkSlotDialog.MarkSlotSummaryViewItem;
            }
        }
        if (_slotListView.Items.Count > 0)
        {
            return _slotListView.Items[0].Tag as MarkSlotDialog.MarkSlotSummaryViewItem;
        }
        return null;
    }

    private void ExecuteRenameSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        string defaultName = $"スロット {selected.SlotNumber}";
        string? newName = SingleLineInputForm.ShowInput(
            this,
            "マークスロット名の変更",
            $"スロット {selected.SlotNumber} の表示名を入力してください (空文字で「{defaultName}」):",
            selected.DisplayName);

        if (newName == null) return;

        _renameSlotAction(selected.SlotNumber, newName);
        RefreshSlotItems();
    }

    private void ExecuteDeleteSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        var dr = MessageBox.Show(this, $"スロット {selected.SlotNumber} の内容を削除しますか？", "マークスロット削除", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (dr != DialogResult.Yes) return;

        _deleteSlotAction(selected.SlotNumber);
        RefreshSlotItems();
    }

    private void ExecuteOpenSetOperation()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        _openSlotSetOperationAction(selected.SlotNumber);
        RefreshSlotItems();
    }

    private bool ShowActionMessageIfPresent(string? message, string title, MessageBoxIcon icon)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }
        _showMessageAction(message, title, icon);
        return true;
    }

    private void ExecuteSaveCategoryToSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        string message = _saveCategorySlotAction(selected.SlotNumber);
        if (ShowActionMessageIfPresent(message, "カテゴリ全タブのMark保存", MessageBoxIcon.Information))
        {
            RefreshSlotItems();
        }
    }

    private void ExecuteSaveWorkspaceToSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        string message = _saveWorkspaceSlotAction(selected.SlotNumber);
        if (ShowActionMessageIfPresent(message, "Workspace全体のMark保存", MessageBoxIcon.Information))
        {
            RefreshSlotItems();
        }
    }

    private void ExecuteImportSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        string message = _importSlotAction(selected.SlotNumber);
        if (ShowActionMessageIfPresent(message, "スロットへインポート", MessageBoxIcon.Information))
        {
            RefreshSlotItems();
        }
    }

    private void ExecuteImportAllSlots()
    {
        var dr = ConfirmMessageBoxShow(this, "全スロットをインポートすると、現在のすべてのマークスロットが上書きされます。よろしいですか？", "全スロットをインポート（全置換）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (dr != DialogResult.Yes) return;

        string message = _importAllSlotsAction();
        if (ShowActionMessageIfPresent(message, "全スロットをインポート", MessageBoxIcon.Information))
        {
            RefreshSlotItems();
        }
    }

    private void ExecuteExportSelectedSlot()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null) return;

        string message = _exportSlotAction(selected.SlotNumber);
        if (ShowActionMessageIfPresent(message, "スロットのエクスポート", MessageBoxIcon.Information))
        {
            RefreshSlotItems();
        }
    }

    private void ExecuteExportAllSlots()
    {
        string message = _exportAllSlotsAction();
        if (ShowActionMessageIfPresent(message, "全スロットのエクスポート", MessageBoxIcon.Information))
        {
            RefreshSlotItems();
        }
    }

    private void UpdateDeleteMarkedButtonState()
    {
        _deleteMarkedItemsButton.Enabled = _markedPaths.Count > 0;
    }

    private void UpdateDisplayAndButtonState()
    {
        var selected = GetSelectedSlotSummary();
        if (selected != null)
        {
            _slotContentHeaderLabel.Text = $"保存済み内容（{_slotContentListView.Items.Count}件 / Mark {_markedPaths.Count}件）";
        }
        else
        {
            _slotContentHeaderLabel.Text = "保存済み内容（0件 / Mark 0件）";
        }
        UpdateDeleteMarkedButtonState();
    }

    private void SyncAllItemMarkTexts()
    {
        _slotContentListView.BeginUpdate();
        foreach (ListViewItem item in _slotContentListView.Items)
        {
            if (item.Tag is string path)
            {
                item.Text = _markedPaths.Contains(path) ? "ON" : "";
            }
        }
        _slotContentListView.EndUpdate();
    }

    private void SlotContentListView_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var hitTest = _slotContentListView.HitTest(e.Location);
        if (hitTest.Item != null && hitTest.Item.Tag is string path)
        {
            _mouseDownItemIndex = hitTest.Item.Index;
            _mouseDownItemPath = path;
            _mouseDownItemWasMarked = _markedPaths.Contains(path);
        }
        else
        {
            _mouseDownItemIndex = null;
            _mouseDownItemPath = null;
            _mouseDownItemWasMarked = false;
        }
    }

    private void SlotContentListView_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_mouseDownItemIndex.HasValue || _mouseDownItemPath == null)
        {
            _mouseDownItemIndex = null;
            _mouseDownItemPath = null;
            _mouseDownItemWasMarked = false;
            return;
        }

        var hitTest = _slotContentListView.HitTest(e.Location);
        if (hitTest.Item != null && hitTest.Item.Index == _mouseDownItemIndex.Value && hitTest.Item.Tag is string path && path == _mouseDownItemPath)
        {
            ApplyMouseMarkGesture(hitTest.Item.Index, Control.ModifierKeys, _mouseDownItemWasMarked);
        }
        _mouseDownItemIndex = null;
        _mouseDownItemPath = null;
        _mouseDownItemWasMarked = false;
    }

    public void ApplyMouseMarkGesture(int clickedIndex, Keys modifiers)
    {
        if (clickedIndex >= 0 && clickedIndex < _slotContentListView.Items.Count)
        {
            var item = _slotContentListView.Items[clickedIndex];
            if (item.Tag is string path)
            {
                ApplyMouseMarkGesture(clickedIndex, modifiers, _markedPaths.Contains(path));
                return;
            }
        }
        ApplyMouseMarkGesture(clickedIndex, modifiers, false);
    }

    public void ApplyMouseMarkGesture(int clickedIndex, Keys modifiers, bool clickedWasMarked)
    {
        if (clickedIndex < 0 || clickedIndex >= _slotContentListView.Items.Count) return;

        var clickedItem = _slotContentListView.Items[clickedIndex];
        if (clickedItem.Tag is not string clickedPath) return;

        if (modifiers.HasFlag(Keys.Shift))
        {
            int anchor = _markSelectionAnchorIndex ?? _slotContentListView.FocusedItem?.Index ?? clickedIndex;
            int start = Math.Min(anchor, clickedIndex);
            int end = Math.Max(anchor, clickedIndex);
            bool targetOn = !clickedWasMarked;

            for (int i = start; i <= end; i++)
            {
                if (i >= 0 && i < _slotContentListView.Items.Count)
                {
                    if (_slotContentListView.Items[i].Tag is string path)
                    {
                        if (targetOn)
                        {
                            _markedPaths.Add(path);
                        }
                        else
                        {
                            _markedPaths.Remove(path);
                        }
                    }
                }
            }
        }
        else if (modifiers.HasFlag(Keys.Control))
        {
            if (clickedWasMarked)
            {
                _markedPaths.Remove(clickedPath);
            }
            else
            {
                _markedPaths.Add(clickedPath);
            }
            _markSelectionAnchorIndex = clickedIndex;
        }
        else
        {
            if (clickedWasMarked)
            {
                _markedPaths.Remove(clickedPath);
            }
            else
            {
                _markedPaths.Add(clickedPath);
            }
            _markSelectionAnchorIndex = clickedIndex;
        }

        SyncAllItemMarkTexts();
        UpdateDisplayAndButtonState();
    }

    private void SlotContentListView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            if (e.Control)
            {
                var focused = _slotContentListView.FocusedItem;
                if (focused == null)
                {
                    foreach (ListViewItem item in _slotContentListView.Items)
                    {
                        if (item.Focused)
                        {
                            focused = item;
                            break;
                        }
                    }
                }
                if (focused == null)
                {
                    foreach (ListViewItem item in _slotContentListView.Items)
                    {
                        if (item.Selected)
                        {
                            focused = item;
                            break;
                        }
                    }
                }
                if (focused == null && _slotContentListView.Items.Count > 0)
                {
                    focused = _slotContentListView.Items[0];
                }
                if (focused != null)
                {
                    ToggleItemMark(focused);
                    _markSelectionAnchorIndex = focused.Index;
                }
            }
            else
            {
                var selectedItems = new List<ListViewItem>();
                foreach (ListViewItem item in _slotContentListView.Items)
                {
                    if (item.Selected)
                    {
                        selectedItems.Add(item);
                    }
                }

                if (selectedItems.Count == 0)
                {
                    var focused = _slotContentListView.FocusedItem;
                    if (focused == null)
                    {
                        foreach (ListViewItem item in _slotContentListView.Items)
                        {
                            if (item.Focused)
                            {
                                focused = item;
                                break;
                            }
                        }
                    }
                    if (focused != null)
                    {
                        ToggleItemMark(focused);
                        _markSelectionAnchorIndex = focused.Index;
                    }
                }
                else
                {
                    bool hasUnmarked = false;
                    foreach (ListViewItem item in selectedItems)
                    {
                        if (item.Tag is string path && !_markedPaths.Contains(path))
                        {
                            hasUnmarked = true;
                            break;
                        }
                    }

                    _slotContentListView.BeginUpdate();
                    foreach (ListViewItem item in selectedItems)
                    {
                        if (item.Tag is string path)
                        {
                            if (hasUnmarked)
                            {
                                _markedPaths.Add(path);
                                item.Text = "ON";
                            }
                            else
                            {
                                _markedPaths.Remove(path);
                                item.Text = "";
                            }
                        }
                    }
                    _slotContentListView.EndUpdate();
                }
            }

            UpdateDisplayAndButtonState();
        }
        else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
        {
            if (e.Shift)
            {
                // Shift + Up/Down の場合は、nativeの選択更新後に一括マークを適用する
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;

                    _slotContentListView.BeginUpdate();
                    foreach (ListViewItem item in _slotContentListView.Items)
                    {
                        if (item.Selected && item.Tag is string path)
                        {
                            _markedPaths.Add(path);
                            item.Text = "ON";
                        }
                    }
                    _slotContentListView.EndUpdate();
                    UpdateDisplayAndButtonState();
                }));
            }
        }
    }

    private void ToggleItemMark(ListViewItem item)
    {
        if (item.Tag is string path)
        {
            if (_markedPaths.Contains(path))
            {
                _markedPaths.Remove(path);
                item.Text = "";
            }
            else
            {
                _markedPaths.Add(path);
                item.Text = "ON";
            }
        }
    }

    private void ExecuteDeleteMarkedItems()
    {
        var selected = GetSelectedSlotSummary();
        if (selected == null || _markedPaths.Count == 0) return;

        string confirmMsg = $"Slot {selected.SlotNumber}「{selected.DisplayName}」の保存済み内容から\n" +
                           $"マークした {_markedPaths.Count} 件を削除しますか？\n\n" +
                           $"実際のファイルは削除されません。";

        var dr = ConfirmMessageBoxShow(this, confirmMsg, "マーク項目の一括削除", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (dr != DialogResult.Yes) return;

        int focusIndex = -1;
        if (_slotContentListView.FocusedItem != null)
        {
            focusIndex = _slotContentListView.FocusedItem.Index;
        }

        var result = _removeSlotItemsAction(selected.SlotNumber, _markedPaths);
        if (result.Success)
        {
            _markedPaths.Clear();
            _markSelectionAnchorIndex = null;
            _mouseDownItemIndex = null;
            RefreshSlotItems();
            RefreshSlotContentItems(selected.SlotNumber);

            if (_slotContentListView.Items.Count > 0)
            {
                int targetIndex = Math.Clamp(focusIndex, 0, _slotContentListView.Items.Count - 1);
                var item = _slotContentListView.Items[targetIndex];
                item.Focused = true;
                item.Selected = true;
                item.EnsureVisible();
                _markSelectionAnchorIndex = targetIndex;
            }
        }
        else
        {
            _showMessageAction(result.Message, "マーク項目削除失敗", MessageBoxIcon.Warning);
            RefreshSlotContentItems(selected.SlotNumber);
        }
    }
}
