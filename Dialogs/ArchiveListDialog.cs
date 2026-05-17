using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class ArchiveListDialog : Form
{
    private const int MarkColumnIndex = 0;
    private const int TypeColumnIndex = 1;
    private const int NameColumnIndex = 2;
    private const int SizeColumnIndex = 3;
    private const int ModifiedColumnIndex = 4;
    private const int LocationColumnIndex = 5;

    private readonly string _archivePath;
    private readonly string _initialExtractDirectory;
    private readonly IReadOnlyList<ArchiveEntry> _allEntries;
    private readonly ListView _listView;
    private readonly Label _summaryLabel;
    private readonly Label _actionHintLabel;
    private readonly Button _extractSelectedButton;
    private readonly Button _extractAllButton;
    private readonly Button _closeButton;
    private readonly HashSet<string> _markedEntryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _isReadOnly;
    private string _currentPath = string.Empty;
    private ArchiveListSortColumn _sortColumn = ArchiveListSortColumn.Name;
    private SortOrder _sortOrder = SortOrder.Ascending;

    public ArchiveExtractRequest? PendingExtractRequest { get; private set; }

    public ArchiveListDialog(string archivePath, IReadOnlyList<ArchiveEntry> entries, string initialExtractDirectory, bool isReadOnly = false)
    {
        _archivePath = archivePath;
        _initialExtractDirectory = initialExtractDirectory;
        _allEntries = entries;
        _isReadOnly = isReadOnly;

        Text = $"Archive Contents - {Path.GetFileName(archivePath)}";
        ClientSize = new Size(1100, 650);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Font;

        var titleLabel = new Label
        {
            Text = $"archive: {archivePath}",
            Location = new Point(10, 10),
            Size = new Size(1080, 20),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _summaryLabel = new Label
        {
            AutoSize = false,
            Location = new Point(10, 34),
            Size = new Size(1080, 20),
            ForeColor = SystemColors.GrayText,
            Text = BuildSummaryText(_allEntries, _currentPath),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _actionHintLabel = new Label
        {
            AutoSize = false,
            Location = new Point(10, 56),
            Size = new Size(1080, 20),
            ForeColor = SystemColors.GrayText,
            Text = "Space: 現在行をマーク切替して次へ  U: マーク済みを解凍  すべて解凍...: 全件  一覧は読み取り専用です。",
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _listView = new ListView
        {
            Location = new Point(10, 80),
            Size = new Size(1080, 505),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = true,
            ShowItemToolTips = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _listView.Columns.Add("Mark", 52, HorizontalAlignment.Center);
        _listView.Columns.Add("Type", 68);
        _listView.Columns.Add("名前", 360);
        _listView.Columns.Add("サイズ", 110, HorizontalAlignment.Right);
        _listView.Columns.Add("更新日時", 150);
        _listView.Columns.Add("場所", 300);
        _listView.ColumnClick += ListView_ColumnClick;
        _listView.SelectedIndexChanged += (_, _) => SyncCurrentRowFocus();
        _listView.KeyDown += ListView_KeyDown;
        _listView.DoubleClick += (_, _) => HandleNavigation();

        PopulateItems();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 5, 10, 10),
            WrapContents = false
        };

        _closeButton = new Button
        {
            Text = "閉じる",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(110, 30),
            Margin = new Padding(5, 0, 0, 0)
        };

        _extractAllButton = new Button
        {
            Text = "すべて解凍...",
            AutoSize = true,
            MinimumSize = new Size(110, 30),
            Margin = new Padding(5, 0, 0, 0)
        };
        _extractAllButton.Click += (_, _) => BeginExtractAll();

        _extractSelectedButton = new Button
        {
            Text = "マーク解凍...",
            AutoSize = true,
            MinimumSize = new Size(110, 30),
            Margin = new Padding(5, 0, 0, 0)
        };
        _extractSelectedButton.Click += (_, _) => BeginExtractMarked();

        buttonPanel.Controls.Add(_closeButton);
        buttonPanel.Controls.Add(_extractAllButton);
        buttonPanel.Controls.Add(_extractSelectedButton);

        Controls.Add(_listView);
        Controls.Add(buttonPanel);
        Controls.Add(_actionHintLabel);
        Controls.Add(_summaryLabel);
        Controls.Add(titleLabel);

        AcceptButton = _extractSelectedButton;
        CancelButton = _closeButton;

        Shown += (_, _) =>
        {
            if (_listView.Items.Count > 0)
            {
                _listView.Items[0].Selected = true;
                _listView.Items[0].Focused = true;
            }
            UpdateExtractButtonState();
            _listView.Focus();
        };

        KeyDown += ArchiveListDialog_KeyDown;
    }

    private void PopulateItems()
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();

        if (!string.IsNullOrEmpty(_currentPath))
        {
            var upItem = new ListViewItem(string.Empty);
            upItem.SubItems.Add("UP");
            upItem.SubItems.Add("..");
            upItem.SubItems.Add(string.Empty);
            upItem.SubItems.Add(string.Empty);
            upItem.SubItems.Add(string.Empty);
            upItem.Tag = "UP";
            _listView.Items.Add(upItem);
        }

        string prefix = string.IsNullOrEmpty(_currentPath) ? "" : _currentPath.TrimEnd('/') + "/";
        
        // Filter entries that are direct children of _currentPath
        // We also need to handle cases where directories are not explicit entries
        var visibleEntries = _allEntries
            .Select(entry => {
                string path = entry.EntryPath.Replace('\\', '/');
                if (!string.IsNullOrEmpty(prefix))
                {
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                    if (path.Length <= prefix.Length) return null;
                    path = path[prefix.Length..];
                }
                
                int firstSlash = path.IndexOf('/');
                if (firstSlash < 0) return entry; // Direct child file or directory (if it doesn't end with /)
                
                // If it ends with / and it's the only slash, it's a direct child directory entry
                if (firstSlash == path.Length - 1) return entry;

                // Otherwise, it's a nested item. 
                // We should represent the intermediate directory if it's not already an entry.
                // But for simplicity in this practical subset, we'll only show explicit entries 
                // or we could synthesize directory entries. 
                // Let's assume most 7z list outputs have explicit folder entries for hierarchical navigation to work well.
                return null;
            })
            .Where(entry => entry != null)
            .Cast<ArchiveEntry>()
            .DistinctBy(entry => entry.EntryPath);

        foreach (ArchiveEntry entry in visibleEntries)
        {
            var item = CreateItem(entry);
            _listView.Items.Add(item);
        }

        _listView.ListViewItemSorter = new ArchiveListItemComparer(_sortColumn, _sortOrder);
        _listView.Sort();
        _listView.EndUpdate();
        UpdateDialogState();
    }

    private ListViewItem CreateItem(ArchiveEntry entry)
    {
        string sizeText = entry.IsDirectory
            ? string.Empty
            : (entry.Size?.ToString("N0") ?? string.Empty);
        string modifiedText = entry.ModifiedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
        string nameText = string.IsNullOrWhiteSpace(entry.Name) ? entry.EntryPath : entry.Name;
        string locationText = BuildLocationText(entry);
        var item = new ListViewItem(string.Empty)
        {
            Tag = entry,
            ToolTipText = BuildEntryToolTip(entry, nameText, locationText, sizeText, modifiedText)
        };
        item.SubItems.Add(entry.IsDirectory ? "DIR" : "FILE");
        item.SubItems.Add(nameText);
        item.SubItems.Add(sizeText);
        item.SubItems.Add(modifiedText);
        item.SubItems.Add(locationText);
        ApplyMarkIndicator(item);
        return item;
    }

    private static string BuildSummaryText(IReadOnlyList<ArchiveEntry> entries, string currentPath)
    {
        string pathText = string.IsNullOrEmpty(currentPath) ? "/" : "/" + currentPath.TrimEnd('/');
        int directoryCount = entries.Count(static entry => entry.IsDirectory);
        int fileCount = entries.Count - directoryCount;
        return entries.Count == 0
            ? $"[{pathText}] items: 0 (empty archive)"
            : $"[{pathText}] items: {entries.Count}  dirs: {directoryCount}  files: {fileCount}";
    }

    private void ListView_ColumnClick(object? sender, ColumnClickEventArgs e)
    {
        ArchiveListSortColumn clickedColumn = e.Column switch
        {
            TypeColumnIndex => ArchiveListSortColumn.Type,
            NameColumnIndex => ArchiveListSortColumn.Name,
            SizeColumnIndex => ArchiveListSortColumn.Size,
            ModifiedColumnIndex => ArchiveListSortColumn.ModifiedAt,
            LocationColumnIndex => ArchiveListSortColumn.Path,
            _ => ArchiveListSortColumn.Name
        };

        if (_sortColumn == clickedColumn)
        {
            _sortOrder = _sortOrder == SortOrder.Ascending
                ? SortOrder.Descending
                : SortOrder.Ascending;
        }
        else
        {
            _sortColumn = clickedColumn;
            _sortOrder = SortOrder.Ascending;
        }

        _listView.ListViewItemSorter = new ArchiveListItemComparer(_sortColumn, _sortOrder);
        _listView.Sort();

        if (_listView.Items.Count > 0 && _listView.SelectedIndices.Count == 0)
        {
            _listView.Items[0].Selected = true;
        }

        SyncCurrentRowFocus();
    }

    private void ArchiveListDialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.OK;
            Close();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Back)
        {
            NavigateUp();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_listView.ContainsFocus)
        {
            if (keyData == Keys.Space)
            {
                ToggleFocusedItemMark(moveNext: true);
                return true;
            }

            if (keyData == Keys.U)
            {
                QueueExtractMarked();
                return true;
            }

            if (keyData == Keys.Enter)
            {
                HandleNavigation();
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ListView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_listView.Items.Count == 0)
        {
            return;
        }

        if (e.KeyCode == Keys.Space)
        {
            ToggleFocusedItemMark(moveNext: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.U)
        {
            QueueExtractMarked();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void HandleNavigation()
    {
        ListViewItem? item = _listView.SelectedItems.Count > 0 ? _listView.SelectedItems[0] : _listView.FocusedItem;
        if (item == null) return;

        if (item.Tag is string s && s == "UP")
        {
            NavigateUp();
            return;
        }

        if (item.Tag is ArchiveEntry entry && entry.IsDirectory)
        {
            NavigateDown(entry.EntryPath);
        }
    }

    private void NavigateUp()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        
        string normalized = _currentPath.TrimEnd('/', '\\');
        int lastSlash = normalized.LastIndexOfAny(new[] { '/', '\\' });
        
        if (lastSlash < 0)
        {
            _currentPath = string.Empty;
        }
        else
        {
            _currentPath = normalized[..lastSlash] + "/";
        }
        
        PopulateItems();
        if (_listView.Items.Count > 0)
        {
            _listView.Items[0].Selected = true;
            _listView.Items[0].Focused = true;
        }
    }

    private void NavigateDown(string entryPath)
    {
        _currentPath = entryPath.EndsWith("/") || entryPath.EndsWith("\\") ? entryPath : entryPath + "/";
        PopulateItems();
        if (_listView.Items.Count > 0)
        {
            _listView.Items[0].Selected = true;
            _listView.Items[0].Focused = true;
        }
    }

    private void QueueExtractMarked()
    {
        if (!IsHandleCreated)
        {
            BeginExtractMarked();
            return;
        }

        BeginInvoke(new Action(BeginExtractMarked));
    }

    private void BeginExtractMarked()
    {
        IReadOnlyList<string> markedEntryPaths = GetMarkedEntryPaths();
        if (markedEntryPaths.Count == 0)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        ArchiveExtractDestinationOptions? destination = SelectDestinationOptions();
        if (destination == null)
        {
            return;
        }

        PendingExtractRequest = new ArchiveExtractRequest
        {
            ArchivePath = _archivePath,
            DestinationDirectory = ArchiveExtractService.ResolveDestinationDirectory(destination.BaseDirectory, _archivePath, destination.CreateArchiveRootDirectory),
            EntryPaths = markedEntryPaths,
            ExtractAll = false
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private void BeginExtractAll()
    {
        ArchiveExtractDestinationOptions? destination = SelectDestinationOptions();
        if (destination == null)
        {
            return;
        }

        PendingExtractRequest = new ArchiveExtractRequest
        {
            ArchivePath = _archivePath,
            DestinationDirectory = ArchiveExtractService.ResolveDestinationDirectory(destination.BaseDirectory, _archivePath, destination.CreateArchiveRootDirectory),
            EntryPaths = Array.Empty<string>(),
            ExtractAll = true
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private ArchiveExtractDestinationOptions? SelectDestinationOptions()
    {
        string archiveDisplayName = Path.GetFileNameWithoutExtension(_archivePath);
        return ArchiveExtractDestinationDialog.Show(this, _initialExtractDirectory, archiveDisplayName);
    }

    private IReadOnlyList<string> GetMarkedEntryPaths()
    {
        return _listView.Items
            .Cast<ListViewItem>()
            .Where(IsMarked)
            .Select(item => item.Tag as ArchiveEntry)
            .Where(entry => entry != null)
            .Select(entry => entry!.EntryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ToggleFocusedItemMark(bool moveNext)
    {
        ListViewItem? item = _listView.SelectedItems.Count > 0 ? _listView.SelectedItems[0] : _listView.FocusedItem;
        if (item == null)
        {
            if (_listView.Items.Count == 0)
            {
                return;
            }

            item = _listView.Items[0];
        }

        ToggleMark(item);
        item.Focused = true;
        item.Selected = true;

        if (moveNext)
        {
            MoveFocusToNextItem(item);
        }

        _listView.Focus();
        UpdateDialogState();
    }

    private void ToggleMark(ListViewItem item)
    {
        if (item.Tag is string s && s == "UP")
        {
            return;
        }

        if (item.Tag is not ArchiveEntry entry)
        {
            return;
        }

        if (!_markedEntryPaths.Add(entry.EntryPath))
        {
            _markedEntryPaths.Remove(entry.EntryPath);
        }

        ApplyMarkIndicator(item);
    }

    private bool IsMarked(ListViewItem item)
    {
        return item.Tag is ArchiveEntry entry && _markedEntryPaths.Contains(entry.EntryPath);
    }

    private void ApplyMarkIndicator(ListViewItem item)
    {
        if (item.SubItems.Count > 0)
        {
            item.SubItems[0].Text = IsMarked(item) ? "*" : string.Empty;
        }
    }

    private void MoveFocusToNextItem(ListViewItem currentItem)
    {
        if (_listView.Items.Count == 0)
        {
            return;
        }

        int nextIndex = Math.Min(currentItem.Index + 1, _listView.Items.Count - 1);
        ListViewItem nextItem = _listView.Items[nextIndex];
        currentItem.Selected = false;
        nextItem.Selected = true;
        nextItem.Focused = true;
        nextItem.EnsureVisible();
    }

    private void SyncCurrentRowFocus()
    {
        if (_listView.SelectedItems.Count == 0)
        {
            return;
        }

        ListViewItem item = _listView.SelectedItems[0];
        item.Focused = true;
        UpdateDialogState();
    }

    private void UpdateDialogState()
    {
        UpdateSummaryText();
        UpdateActionHintText();
        UpdateExtractButtonState();
    }

    private void UpdateSummaryText()
    {
        string pathText = string.IsNullOrEmpty(_currentPath) ? "/" : "/" + _currentPath.TrimEnd('/');
        int directoryCount = _allEntries.Count(entry => entry.IsDirectory);
        int itemCount = _allEntries.Count;
        int fileCount = itemCount - directoryCount;
        int markedCount = _markedEntryPaths.Count;

        int currentDirItemCount = _listView.Items.Cast<ListViewItem>().Count(item => item.Tag is ArchiveEntry);
        
        string selectionText = _listView.SelectedItems.Count > 0
            ? $"  current: {BuildCurrentSelectionSummary(_listView.SelectedItems[0], currentDirItemCount)}"
            : string.Empty;

        _summaryLabel.Text = itemCount == 0
            ? $"[{pathText}] items: 0 (empty archive)"
            : markedCount > 0
                ? $"[{pathText}] items: {itemCount} (this dir: {currentDirItemCount})  marks: {markedCount}{selectionText}"
                : $"[{pathText}] items: {itemCount} (this dir: {currentDirItemCount}){selectionText}";
    }

    private void UpdateActionHintText()
    {
        int markedCount = _markedEntryPaths.Count;
        string currentText = _listView.SelectedItems.Count > 0
            ? BuildCurrentRowHint(_listView.SelectedItems[0])
            : "current: —";
        string extractHint = _isReadOnly
            ? "ReadOnlyタブでは解凍できません"
            : markedCount > 0
                ? $"U: マーク {markedCount} 件を解凍"
                : "U: マーク済みを解凍";

        _actionHintLabel.Text = $"Space: 現在行をマーク切替して次へ  {extractHint}  {currentText}";
    }

    private void UpdateExtractButtonState()
    {
        if (_extractSelectedButton == null || _extractAllButton == null)
        {
            return;
        }

        if (_isReadOnly)
        {
            _extractSelectedButton.Enabled = false;
            _extractAllButton.Enabled = false;
            _extractSelectedButton.Text = "マーク解凍...";
            return;
        }

        int markedCount = _markedEntryPaths.Count;
        _extractSelectedButton.Enabled = markedCount > 0;
        _extractSelectedButton.Text = markedCount > 0
            ? $"マーク解凍... ({markedCount})"
            : "マーク解凍...";
        _extractAllButton.Enabled = _listView.Items.Count > 0;
    }

    private enum ArchiveListSortColumn
    {
        Type,
        Name,
        Size,
        ModifiedAt,
        Path
    }

    private static string BuildLocationText(ArchiveEntry entry)
    {
        string normalizedPath = entry.EntryPath.Replace('\\', '/').TrimEnd('/');
        int separatorIndex = normalizedPath.LastIndexOf('/');
        if (separatorIndex <= 0)
        {
            return "（root）";
        }

        string location = normalizedPath[..separatorIndex];
        return string.IsNullOrWhiteSpace(location) ? "（root）" : location;
    }

    private static string BuildEntryToolTip(ArchiveEntry entry, string nameText, string locationText, string sizeText, string modifiedText)
    {
        string typeText = entry.IsDirectory ? "DIR" : "FILE";
        string displaySize = string.IsNullOrWhiteSpace(sizeText) ? "—" : sizeText;
        string displayModified = string.IsNullOrWhiteSpace(modifiedText) ? "—" : modifiedText;
        return string.Join(
            Environment.NewLine,
            $"名前: {nameText}",
            $"種別: {typeText}",
            $"場所: {locationText}",
            $"サイズ: {displaySize}",
            $"更新日時: {displayModified}",
            $"フルパス: {entry.EntryPath}");
    }

    private string BuildCurrentSelectionSummary(ListViewItem item, int itemCount)
    {
        if (item.Tag is not ArchiveEntry entry)
        {
            return "—";
        }

        string nameText = item.SubItems.Count > NameColumnIndex ? item.SubItems[NameColumnIndex].Text : entry.Name;
        string typeText = entry.IsDirectory ? "DIR" : "FILE";
        string markText = IsMarked(item) ? "*" : "-";
        return $"{item.Index + 1}/{itemCount} / {markText} {typeText} / {nameText}";
    }

    private string BuildCurrentRowHint(ListViewItem item)
    {
        if (item.Tag is not ArchiveEntry entry)
        {
            return "current: —";
        }

        string nameText = item.SubItems.Count > NameColumnIndex ? item.SubItems[NameColumnIndex].Text : entry.Name;
        string markText = IsMarked(item) ? "*" : "未";
        return $"current: {markText} / {nameText}";
    }

    private sealed class ArchiveListItemComparer : System.Collections.IComparer
    {
        private readonly ArchiveListSortColumn _column;
        private readonly SortOrder _sortOrder;

        public ArchiveListItemComparer(ArchiveListSortColumn column, SortOrder sortOrder)
        {
            _column = column;
            _sortOrder = sortOrder;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not ListViewItem leftItem || y is not ListViewItem rightItem)
            {
                return 0;
            }

            // "UP" item always comes first
            if (leftItem.Tag is string s1 && s1 == "UP") return -1;
            if (rightItem.Tag is string s2 && s2 == "UP") return 1;

            ArchiveEntry? left = leftItem.Tag as ArchiveEntry;
            ArchiveEntry? right = rightItem.Tag as ArchiveEntry;

            // Grouping: DIR first (always, regardless of sort order)
            int groupResult = Comparer<int>.Default.Compare(left?.IsDirectory == true ? 0 : 1, right?.IsDirectory == true ? 0 : 1);
            if (groupResult != 0)
            {
                return groupResult;
            }

            // Within the same group (DIRs or FILEs), apply sort column and order
            int result = _column switch
            {
                ArchiveListSortColumn.Type => 0, // Already grouped by type
                ArchiveListSortColumn.Name => CompareText(left?.Name, right?.Name, left?.EntryPath, right?.EntryPath),
                ArchiveListSortColumn.Size => Nullable.Compare(left?.Size, right?.Size),
                ArchiveListSortColumn.ModifiedAt => Nullable.Compare(left?.ModifiedAt, right?.ModifiedAt),
                ArchiveListSortColumn.Path => CompareText(left?.EntryPath, right?.EntryPath),
                _ => 0
            };

            if (result == 0)
            {
                result = CompareText(left?.EntryPath, right?.EntryPath);
            }

            return _sortOrder == SortOrder.Descending ? -result : result;
        }

        private static int CompareText(string? primaryLeft, string? primaryRight, string? fallbackLeft = null, string? fallbackRight = null)
        {
            string left = string.IsNullOrWhiteSpace(primaryLeft) ? (fallbackLeft ?? string.Empty) : primaryLeft;
            string right = string.IsNullOrWhiteSpace(primaryRight) ? (fallbackRight ?? string.Empty) : primaryRight;
            return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        }
    }
}
