using MidFD.Models;
using MidFD.Services;
using System.Threading.Tasks;

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
    private readonly Label _statusLabel;
    private readonly Label _currentPathLabel;
    private string _currentPath = string.Empty;
    private ArchiveListSortColumn _sortColumn = ArchiveListSortColumn.Name;
    private SortOrder _sortOrder = SortOrder.Ascending;
    private ArchiveTextPreviewForm? _activeTextPreviewForm;

    public ArchiveExtractRequest? PendingExtractRequest { get; private set; }

    private readonly string? _dateFormat;
    private readonly string? _sizeFormat;
    private readonly string? _sevenZipPath;

    public ArchiveListDialog(string archivePath, IReadOnlyList<ArchiveEntry> entries, string initialExtractDirectory, bool isReadOnly = false, string? dateFormat = null, string? sizeFormat = null, string? sevenZipPath = null)
    {
        _archivePath = archivePath;
        _initialExtractDirectory = initialExtractDirectory;
        _allEntries = entries;
        _isReadOnly = isReadOnly;
        _dateFormat = dateFormat;
        _sizeFormat = sizeFormat;
        _sevenZipPath = sevenZipPath;

        Text = $"Archive Contents - {Path.GetFileName(archivePath)}";
        ClientSize = new Size(800, 650);
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
            Size = new Size(500, 20),
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        _statusLabel = new Label
        {
            Text = isReadOnly ? "状態: ReadOnly（解凍できません）" : "状態: 通常（マーク解凍できます）",
            Location = new Point(520, 10),
            Size = new Size(270, 20),
            ForeColor = isReadOnly ? Color.Tomato : Color.LightGreen,
            Font = new Font(Font, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.TopRight
        };

        _currentPathLabel = new Label
        {
            Location = new Point(10, 32),
            Size = new Size(780, 20),
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        _summaryLabel = new Label
        {
            Location = new Point(10, 54),
            Size = new Size(400, 20),
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        _actionHintLabel = new Label
        {
            Location = new Point(420, 54),
            Size = new Size(370, 20),
            ForeColor = SystemColors.GrayText,
            Text = "Space: マーク  U: 解凍  Enter: 入る/プレビュー",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.TopRight
        };

        _listView = new ListView
        {
            Location = new Point(10, 80),
            Size = new Size(780, 505),
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            MultiSelect = true,
            ShowItemToolTips = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = MidFDColors.ListNormalBack,
            ForeColor = MidFDColors.ListNormalFore
        };
        _listView.Columns.Add("Mark", 52, HorizontalAlignment.Center);
        _listView.Columns.Add("Type", 68);
        _listView.Columns.Add("名前", 200);
        _listView.Columns.Add("サイズ", 90, HorizontalAlignment.Right);
        _listView.Columns.Add("更新日時", 130);
        _listView.Columns.Add("場所", 150);
        _listView.ColumnClick += ListView_ColumnClick;
        _listView.SelectedIndexChanged += (_, _) => SyncCurrentRowFocus();
        _listView.KeyDown += ListView_KeyDown;
        _listView.DoubleClick += (_, _) => HandleNavigation();

        _listView.TabIndex = 0;
        _listView.TabStop = true;

        PopulateItems();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 5, 10, 10),
            WrapContents = false,
            TabStop = false
        };

        _closeButton = new Button
        {
            Text = "閉じる",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(110, 30),
            Margin = new Padding(5, 0, 0, 0),
            TabIndex = 3,
            TabStop = true
        };

        _extractAllButton = new Button
        {
            Text = "すべて解凍...",
            AutoSize = true,
            MinimumSize = new Size(110, 30),
            Margin = new Padding(5, 0, 0, 0),
            TabIndex = 2,
            TabStop = true
        };
        _extractAllButton.Click += (_, _) => BeginExtractAll();

        _extractSelectedButton = new Button
        {
            Text = "マーク解凍...",
            AutoSize = true,
            MinimumSize = new Size(110, 30),
            Margin = new Padding(5, 0, 0, 0),
            TabIndex = 1,
            TabStop = true
        };
        _extractSelectedButton.Click += (_, _) => BeginExtractMarked();

        buttonPanel.Controls.Add(_closeButton);
        buttonPanel.Controls.Add(_extractAllButton);
        buttonPanel.Controls.Add(_extractSelectedButton);

        Controls.Add(_listView);
        Controls.Add(buttonPanel);
        Controls.Add(_actionHintLabel);
        Controls.Add(_summaryLabel);
        Controls.Add(_currentPathLabel);
        Controls.Add(_statusLabel);
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
        FormClosed += (_, _) => _activeTextPreviewForm?.Close();
    }

    public void FocusListView()
    {
        Activate();
        _listView.Focus();
    }

    public void FocusPreviewText()
    {
        if (_activeTextPreviewForm != null && !_activeTextPreviewForm.IsDisposed && _activeTextPreviewForm.Visible)
        {
            _activeTextPreviewForm.Activate();
            _activeTextPreviewForm.TextBox.Focus();
        }
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
            upItem.ForeColor = MidFDColors.ListDirectoryFore;
            _listView.Items.Add(upItem);
        }

        var visibleMap = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveEntry entry in _allEntries)
        {
            string path = entry.EntryPath;
            if (!string.IsNullOrEmpty(_currentPath))
            {
                if (!path.StartsWith(_currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Length <= _currentPath.Length) continue;
            }

            string relativePath = string.IsNullOrEmpty(_currentPath) ? path : path[_currentPath.Length..];
            int firstSlash = relativePath.IndexOf('/');

            if (firstSlash < 0)
            {
                // Direct child (file or explicit directory entry)
                string key = entry.IsDirectory && !entry.EntryPath.EndsWith('/')
                    ? entry.EntryPath + "/"
                    : entry.EntryPath;
                visibleMap[key] = entry;
            }
            else
            {
                // Nested entry -> Synthesize the intermediate directory
                string subDirRelative = relativePath[..(firstSlash + 1)];
                string subDirAbsolute = _currentPath + subDirRelative;

                // Explicit directory entry should overwrite any synthetic directory
                if (visibleMap.TryGetValue(subDirAbsolute, out var existing) && !existing.IsSyntheticDirectory)
                {
                    continue;
                }

                var synthEntry = new ArchiveEntry
                {
                    EntryPath = subDirAbsolute,
                    RawEntryPath = subDirAbsolute,
                    Name = subDirRelative.TrimEnd('/'),
                    IsDirectory = true,
                    Size = null,
                    ModifiedAt = null,
                    IsSyntheticDirectory = true
                };
                visibleMap[subDirAbsolute] = synthEntry;
            }
        }

        foreach (ArchiveEntry entry in visibleMap.Values)
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
            : (entry.Size != null
                ? FileSystemItemFactory.FormatDisplaySize(entry.Size.Value, _sizeFormat)
                : string.Empty);
        string modifiedText = entry.ModifiedAt != null
            ? FileSystemItemFactory.FormatDisplayDate(entry.ModifiedAt.Value, _dateFormat)
            : string.Empty;
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
        item.ForeColor = entry.IsDirectory ? MidFDColors.ListDirectoryFore : MidFDColors.ListFileFore;
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
        if (keyData == Keys.F6)
        {
            if (_activeTextPreviewForm != null && !_activeTextPreviewForm.IsDisposed && _activeTextPreviewForm.Visible)
            {
                if (_listView.ContainsFocus)
                {
                    FocusPreviewText();
                }
                else
                {
                    FocusListView();
                }
                return true;
            }
        }

        if (_listView.ContainsFocus)
        {
            if (keyData == Keys.Tab)
            {
                if (_activeTextPreviewForm != null && !_activeTextPreviewForm.IsDisposed && _activeTextPreviewForm.Visible)
                {
                    FocusPreviewText();
                    return true;
                }
            }

            if (keyData == Keys.Space)
            {
                ToggleFocusedItemMark(moveNext: true);
                return true;
            }

            if (keyData == Keys.U)
            {
                if (BlockReadOnlyArchiveOperation())
                {
                    return true;
                }
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
            if (BlockReadOnlyArchiveOperation())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
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

        if (item.Tag is not ArchiveEntry entry) return;

        if (entry.IsDirectory)
        {
            NavigateDown(entry.EntryPath);
            return;
        }

        // ファイルの場合の Enter 挙動
        string archiveExt = Path.GetExtension(_archivePath);
        bool isZip = string.Equals(archiveExt, ".zip", StringComparison.OrdinalIgnoreCase);
        bool isSevenZip = string.Equals(archiveExt, ".7z", StringComparison.OrdinalIgnoreCase);
        bool isText = ArchiveEntryPreviewService.IsTextFile(entry.EntryPath);

        if ((isZip || isSevenZip) && isText)
        {
            string archivePath = _archivePath;
            string entryPath = string.IsNullOrEmpty(entry.RawEntryPath) ? entry.EntryPath : entry.RawEntryPath;

            ArchiveEntryPreviewResult result;
            if (isZip)
            {
                result = ArchiveEntryPreviewService.GetZipEntryTextPreview(archivePath, entryPath);
            }
            else
            {
                result = ArchiveEntryPreviewService.Get7zEntryTextPreview(archivePath, entryPath, _sevenZipPath);
            }

            _activeTextPreviewForm?.Close();
            _activeTextPreviewForm?.Dispose();

            _activeTextPreviewForm = new ArchiveTextPreviewForm(entry.Name, result.Text);
            _activeTextPreviewForm.FormClosed += (_, _) => _activeTextPreviewForm = null;

            PositionTextPreviewForm(_activeTextPreviewForm);

            _activeTextPreviewForm.Show(this);
            _activeTextPreviewForm.Activate();

            // テキストプレビューを開いた後も一覧操作を継続できるようにフォーカスを戻す
            Activate();
            _listView.Focus();
        }
        else
        {
            System.Media.SystemSounds.Beep.Play();
        }
    }

    private void PositionTextPreviewForm(ArchiveTextPreviewForm previewForm)
    {
        const int margin = 8;
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;

        int x = Right + margin;
        int y = Top + 32;

        // 右側に入らない場合は左側へ
        if (x + previewForm.Width > workingArea.Right)
        {
            x = Left - previewForm.Width - margin;
        }

        // 左側にも入らない場合は親の中央へ
        if (x < workingArea.Left)
        {
            x = Left + (Width - previewForm.Width) / 2;
        }

        if (y + previewForm.Height > workingArea.Bottom)
        {
            y = workingArea.Bottom - previewForm.Height - margin;
        }

        x = Math.Max(workingArea.Left + margin, Math.Min(x, workingArea.Right - previewForm.Width - margin));
        y = Math.Max(workingArea.Top + margin, Math.Min(y, workingArea.Bottom - previewForm.Height - margin));

        previewForm.Location = new Point(x, y);
    }

    private void NavigateUp()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        
        string normalized = _currentPath.TrimEnd('/');
        int lastSlash = normalized.LastIndexOf('/');
        
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
        _currentPath = entryPath.EndsWith("/") ? entryPath : entryPath + "/";
        PopulateItems();
        if (_listView.Items.Count > 0)
        {
            _listView.Items[0].Selected = true;
            _listView.Items[0].Focused = true;
        }
    }

    private void QueueExtractMarked()
    {
        if (BlockReadOnlyArchiveOperation())
        {
            return;
        }
        if (!IsHandleCreated)
        {
            BeginExtractMarked();
            return;
        }

        BeginInvoke(new Action(BeginExtractMarked));
    }

    private void BeginExtractMarked()
    {
        if (BlockReadOnlyArchiveOperation())
        {
            return;
        }
        IReadOnlyList<string> entryPaths = GetExtractionEntryPathsForU();
        if (entryPaths.Count == 0)
        {
            System.Media.SystemSounds.Beep.Play();
            MessageBox.Show(this, "解凍できるファイルまたはフォルダを選択するか、対象をマークしてください。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            EntryPaths = entryPaths,
            ExtractAll = false
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    private void BeginExtractAll()
    {
        if (BlockReadOnlyArchiveOperation())
        {
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
        if (_markedEntryPaths.Count == 0)
        {
            return Array.Empty<string>();
        }

        return _allEntries
            .Where(entry => !entry.IsSyntheticDirectory)
            .Where(entry => _markedEntryPaths.Contains(entry.EntryPath))
            .Select(entry => string.IsNullOrEmpty(entry.RawEntryPath) ? entry.EntryPath : entry.RawEntryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool BlockReadOnlyArchiveOperation()
    {
        if (!_isReadOnly)
        {
            return false;
        }

        System.Media.SystemSounds.Beep.Play();
        UpdateActionHintText();
        return true;
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

        if (entry.IsSyntheticDirectory)
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
        UpdatePreviewIfFormOpen();
    }

    private void UpdatePreviewIfFormOpen()
    {
        if (_activeTextPreviewForm == null)
        {
            return;
        }

        ArchiveEntry? entry = GetSelectedArchiveEntry();
        if (entry == null)
        {
            _activeTextPreviewForm.SetContent("なし", "[プレビューするファイルが選択されていません。]");
            return;
        }

        if (entry.IsDirectory || entry.IsSyntheticDirectory)
        {
            _activeTextPreviewForm.SetContent(entry.Name, "[フォルダはプレビューできません。]");
            return;
        }

        string archiveExt = Path.GetExtension(_archivePath);
        bool isZip = string.Equals(archiveExt, ".zip", StringComparison.OrdinalIgnoreCase);
        bool isSevenZip = string.Equals(archiveExt, ".7z", StringComparison.OrdinalIgnoreCase);
        bool isText = ArchiveEntryPreviewService.IsTextFile(entry.EntryPath);

        if ((isZip || isSevenZip) && isText)
        {
            string entryPath = string.IsNullOrEmpty(entry.RawEntryPath) ? entry.EntryPath : entry.RawEntryPath;
            ArchiveEntryPreviewResult result;
            if (isZip)
            {
                result = ArchiveEntryPreviewService.GetZipEntryTextPreview(_archivePath, entryPath);
            }
            else
            {
                result = ArchiveEntryPreviewService.Get7zEntryTextPreview(_archivePath, entryPath, _sevenZipPath);
            }
            _activeTextPreviewForm.SetContent(entry.Name, result.Text);
        }
        else if (!isZip && !isSevenZip)
        {
            _activeTextPreviewForm.SetContent(entry.Name, "[ZIP/7z以外のファイルはプレビュー対象外です。]");
        }
        else
        {
            _activeTextPreviewForm.SetContent(entry.Name, "[このファイル形式はテキストプレビュー対象外です。]");
        }
    }

    private ArchiveEntry? GetSelectedArchiveEntry()
    {
        ListViewItem? item = _listView.SelectedItems.Count > 0 ? _listView.SelectedItems[0] : _listView.FocusedItem;
        if (item == null || item.Tag is not ArchiveEntry entry)
        {
            return null;
        }
        return entry;
    }

    private IReadOnlyList<string> GetExtractionEntryPathsForU()
    {
        IReadOnlyList<string> marked = GetMarkedEntryPaths();
        if (marked.Count > 0)
        {
            return marked;
        }

        ArchiveEntry? selected = GetSelectedArchiveEntry();
        if (selected == null || selected.IsSyntheticDirectory)
        {
            return Array.Empty<string>();
        }

        if (selected.IsDirectory)
        {
            string dirPath = !string.IsNullOrWhiteSpace(selected.RawEntryPath) ? selected.RawEntryPath : selected.EntryPath;
            string prefix = dirPath.Replace('\\', '/').TrimEnd('/') + "/";
            return _allEntries
                .Where(e => !e.IsDirectory && !e.IsSyntheticDirectory)
                .Where(e => {
                    string ePath = (!string.IsNullOrWhiteSpace(e.RawEntryPath) ? e.RawEntryPath : e.EntryPath).Replace('\\', '/');
                    return ePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                })
                .Select(e => !string.IsNullOrWhiteSpace(e.RawEntryPath) ? e.RawEntryPath : e.EntryPath)
                .ToList();
        }

        string path = !string.IsNullOrWhiteSpace(selected.RawEntryPath)
            ? selected.RawEntryPath
            : selected.EntryPath;

        return new[] { path };
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
        _currentPathLabel.Text = $"current: {pathText}";

        int directoryCount = _allEntries.Count(entry => entry.IsDirectory);
        int itemCount = _allEntries.Count;
        int fileCount = itemCount - directoryCount;
        int markedCount = _markedEntryPaths.Count;

        int currentDirItemCount = _listView.Items.Cast<ListViewItem>().Count(item => item.Tag is ArchiveEntry);
        
        string selectionText = _listView.SelectedItems.Count > 0
            ? $"  current: {BuildCurrentSelectionSummary(_listView.SelectedItems[0], currentDirItemCount)}"
            : string.Empty;

        _summaryLabel.Text = itemCount == 0
            ? "items: 0 (empty archive)"
            : markedCount > 0
                ? $"items: {itemCount} (this dir: {currentDirItemCount})  marks: {markedCount}{selectionText}"
                : $"items: {itemCount} (this dir: {currentDirItemCount}){selectionText}";
    }

    private void UpdateActionHintText()
    {
        int markedCount = _markedEntryPaths.Count;
        string extractHint = _isReadOnly
            ? "ReadOnly"
            : markedCount > 0
                ? $"U: マーク/選択を解凍({markedCount})"
                : "U: マーク/選択を解凍";

        _actionHintLabel.Text = $"Space: マーク  {extractHint}  Enter: 入る/プレビュー";
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
        string normalizedPath = entry.EntryPath.TrimEnd('/');
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
