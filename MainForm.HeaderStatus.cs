using System.Drawing;
using System.Windows.Forms;
using MidFD.Commands;
using MidFD.Models;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    /// <summary>
    /// 最上段ヘッダ (titleHeaderPanel) 専用：文字描画
    /// Phase 36Z: 枠線は contentFramePanel が描くため、ここでは文字の上書きのみ。
    /// </summary>
    private void titleHeaderPanel_Paint(object sender, PaintEventArgs e)
    {
        // Title header is now compact/hidden in Browser mode.
        // No text drawing here.
    }
    /// <summary>
    /// コンテンツ・フレーム (contentFramePanel) 専用：アプリケーション全体の 1px 枠線描画 (オーナー)
    /// </summary>
    private void contentFramePanel_Paint(object sender, PaintEventArgs e)
    {
        var panel = sender as Panel;
        if (panel == null) return;
        int right = Math.Max(0, panel.ClientSize.Width - 1);
        int bottom = Math.Max(0, panel.ClientSize.Height - 1);
        int top = Math.Clamp(headerPanel.Top, 0, bottom);
        if (FileListColorResolver.NormalizeCoreTheme(_settings.Appearance?.ColorTheme) == "Light")
        {
            // Light テーマ: 左右線はスキップ、下辺は SeparatorLine で弱めに描画
            using (var pen = new Pen(MidFDColors.SeparatorLine, 1))
            {
                // 下辺 (一覧領域の外枠として描画)
                e.Graphics.DrawLine(pen, 0, bottom, right, bottom);
            }
        }
        else
        {
            // 既存どおり: BorderLine で左辺/右辺/下辺を描く
            using (var pen = new Pen(MidFDColors.BorderLine, 1))
            {
                // 左辺
                e.Graphics.DrawLine(pen, 0, top, 0, bottom);
                // 右辺
                e.Graphics.DrawLine(pen, right, top, right, bottom);
                // 下辺 (一覧領域の外枠として描画)
                e.Graphics.DrawLine(pen, 0, bottom, right, bottom);
            }
        }
    }
    // ─── Phase 2g-fix3a: Row 1 時計更新ロジック ──────────────────────────
    private void StartHeaderClockTimer()
    {
        _headerClockTimer?.Stop();
        _headerClockTimer?.Dispose();
        _headerClockTimer = new System.Windows.Forms.Timer();
        _headerClockTimer.Interval = 1000; // 1秒周期
        _headerClockTimer.Tick += (s, e) => UpdateTitleHeaderClock();
        _headerClockTimer.Start();
    }
    private void UpdateTitleHeaderClock()
    {
        // 秒単位の時計文字列を更新
        lblClock.Text = DateTime.Now.ToString("yyyy-MM-dd(ddd) HH:mm:ss");
        // Px1 diag note: UpdateTitleHeaderClock は LayoutHeaderZones のみ呼び、
        //   ResolveAdaptiveHeaderStatusFont は呼ばない (font は ApplyFontSettings 後そのまま)。
        LayoutHeaderZones();
        LogHeaderRightDiag("UpdateTitleHeaderClock");
        // 再描画を要求
        lblClock.Invalidate();
        contentFramePanel.Invalidate();
        // 必要ならデバッグログ (最終的に削除可能)
        // Debug.WriteLine($"[Clock] {lblClock.Text}");
    }
    #region Browser Header Interaction Polish
    private void InitializeHeaderInteractionPolish()
    {
        if (_headerInteractionInitialized) return;
        _headerInteractionInitialized = true;
        _headerToolTip = new ToolTip
        {
            ShowAlways = true,
            InitialDelay = 400,
            ReshowDelay = 100,
            AutoPopDelay = 8000
        };
        InitializeHeaderContextMenus();
        InitializeHeaderSortContextMenu();
        WireHeaderCopyInteractions();
    }
    private void InitializeHeaderSortContextMenu()
    {
        _headerSortContextMenu = new ContextMenuStrip();
        AddHeaderSortKeyItem("名前(&N)", SortKind.Name);
        AddHeaderSortKeyItem("拡張子(&E)", SortKind.Ext);
        AddHeaderSortKeyItem("サイズ(&S)", SortKind.Size);
        AddHeaderSortKeyItem("日付(&T)", SortKind.Date);
        _headerSortContextMenu.Items.Add(new ToolStripSeparator());
        _headerSortAscendingItem = new ToolStripMenuItem("昇順(&A)");
        _headerSortAscendingItem.Click += (_, _) => ApplyHeaderSortDirection(ascending: true);
        _headerSortContextMenu.Items.Add(_headerSortAscendingItem);
        _headerSortDescendingItem = new ToolStripMenuItem("降順(&D)");
        _headerSortDescendingItem.Click += (_, _) => ApplyHeaderSortDirection(ascending: false);
        _headerSortContextMenu.Items.Add(_headerSortDescendingItem);
        _headerSortContextMenu.Opening += (_, e) =>
        {
            if (!lblSort.Visible || string.IsNullOrWhiteSpace(lblSort.Text))
            {
                e.Cancel = true;
                return;
            }
            foreach (var pair in _headerSortKeyItems)
            {
                pair.Value.Checked = pair.Key == _currentSort;
            }
            if (_headerSortAscendingItem != null)
            {
                _headerSortAscendingItem.Checked = _sortAscending;
            }
            if (_headerSortDescendingItem != null)
            {
                _headerSortDescendingItem.Checked = !_sortAscending;
            }
        };
        lblSort.MouseClick += HeaderSort_MouseClick;
    }
    private void AddHeaderSortKeyItem(string text, SortKind sortKind)
    {
        if (_headerSortContextMenu == null)
        {
            return;
        }
        var item = new ToolStripMenuItem(text)
        {
            Tag = sortKind
        };
        item.Click += HeaderSortKeyItem_Click;
        _headerSortKeyItems[sortKind] = item;
        _headerSortContextMenu.Items.Add(item);
    }
    private void HeaderSort_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !lblSort.Visible || string.IsNullOrWhiteSpace(lblSort.Text) || _headerSortContextMenu == null)
        {
            return;
        }
        _headerSortContextMenu.Show(lblSort, new Point(0, lblSort.Height));
    }
    private void HeaderSortKeyItem_Click(object? sender, EventArgs e)
    {
        if (GuardClipboardBusy() || sender is not ToolStripMenuItem item || item.Tag is not SortKind sortKind)
        {
            return;
        }
        bool ascending = sortKind == _currentSort ? !_sortAscending : _sortAscending;
        ApplySortState(sortKind, ascending);
    }
    private void ApplyHeaderSortDirection(bool ascending)
    {
        if (!GuardClipboardBusy())
        {
            ApplySortState(_currentSort, ascending);
        }
    }
    private static bool IsHeaderSortText(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(static part => part.StartsWith("S:", StringComparison.Ordinal));
    }
    private void InitializeHeaderContextMenus()
    {
        // Path 行用メニュー
        _headerPathContextMenu = new ContextMenuStrip();
        _headerPathContextMenu.Opening += (_, e) => e.Cancel = TryConsumeHeaderContextMenuSuppress();
        var copyPathItem = new ToolStripMenuItem("パスをコピー");
        copyPathItem.Click += (_, _) => ExecuteCommandFromUi(CommandIds.BrowserCopyCurrentPath, CommandScope.Browser, "HeaderContextMenu.CopyCurrentPath");
        _headerPathContextMenu.Items.Add(copyPathItem);
        // Item 行用メニュー
        _headerItemContextMenu = new ContextMenuStrip();
        _headerItemContextMenu.Opening += (_, e) => e.Cancel = TryConsumeHeaderContextMenuSuppress();
        var copyFullPathItem = new ToolStripMenuItem("フルパスをコピー");
        copyFullPathItem.Click += (_, _) => CopySelectedItemFullPathFromHeader();
        var copyFileNameItem = new ToolStripMenuItem("ファイル名をコピー");
        copyFileNameItem.Click += (_, _) => CopySelectedItemNameFromHeader();
        _headerItemContextMenu.Items.Add(copyFullPathItem);
        _headerItemContextMenu.Items.Add(copyFileNameItem);
        _headerItemContextMenu.Opening += (s, e) =>
        {
            bool hasItem = !string.IsNullOrWhiteSpace(GetSelectedItemFullPathForHeaderCopy());
            copyFullPathItem.Enabled = hasItem;
            copyFileNameItem.Enabled = hasItem;
            e.Cancel = false;
        };
    }
    private void WireHeaderCopyInteractions()
    {
        // Cursor
        lblPath.Cursor = Cursors.Hand;
        lblName.Cursor = Cursors.Hand;
        // MouseClick
        lblPath.MouseClick += HeaderPath_MouseClick;
        infoRow2Panel.MouseClick += HeaderPath_MouseClick;
        lblName.MouseClick += HeaderItem_MouseClick;
        infoRow4Panel.MouseClick += HeaderItem_MouseClick;
        // ContextMenuStrip
        lblPath.ContextMenuStrip = _headerPathContextMenu;
        infoRow2Panel.ContextMenuStrip = _headerPathContextMenu;
        lblName.ContextMenuStrip = _headerItemContextMenu;
        infoRow4Panel.ContextMenuStrip = _headerItemContextMenu;
    }
    private void HeaderPath_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !_suppressBrowserPathEntryPanelClick)
        {
            OpenBrowserPathEntry();
        }
    }
    private void HeaderItem_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            CopySelectedItemFullPathFromHeader();
        }
    }
    private void CopyCurrentDirectoryFromHeader()
    {
        string? path = GetCurrentDirectoryForHeaderCopy();
        CopyTextToClipboardWithStatus(path, "パスをコピーしました。");
    }
    private void CopySelectedItemFullPathFromHeader()
    {
        string? fullPath = GetSelectedItemFullPathForHeaderCopy();
        CopyTextToClipboardWithStatus(fullPath, "フルパスをコピーしました。");
    }
    private void CopySelectedOrMarkedFullPathsToClipboard()
    {
        SelectionResult selection = ResolveSelection();
        if (!selection.FullPaths.Any())
        {
            ShowStatusMessage("コピーできるパスがありません。");
            return;
        }

        CopyTextToClipboardWithStatus(
            string.Join(Environment.NewLine, selection.FullPaths),
            $"{selection.FullPaths.Count} 件のパスをクリップボードにコピーしました。");
    }
    private void CopySelectedItemNameFromHeader()
    {
        string? fileName = GetSelectedItemNameForHeaderCopy();
        CopyTextToClipboardWithStatus(fileName, "ファイル名をコピーしました。");
    }
    private void CopyTextToClipboardWithStatus(string? text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatusMessage("コピーできる内容がありません。");
            return;
        }
        try
        {
            Clipboard.SetText(text, TextDataFormat.UnicodeText);
            ShowStatusMessage(successMessage);
        }
        catch (Exception ex)
        {
            ShowStatusMessage("クリップボードへコピーできませんでした。");
            LogService.Info($"[HeaderCopy] Clipboard copy failed: {ex}");
        }
    }
    private string? GetCurrentDirectoryForHeaderCopy()
    {
        string path = _navigationService.CurrentPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
    private string? GetSelectedItemFullPathForHeaderCopy()
    {
        string currentPath = _navigationService.CurrentPath;
        var item = GetCurrentBrowserItem();
        if (item == null) return null;
        string name = item.Text;
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name == "..")
        {
            try
            {
                return Directory.GetParent(currentPath)?.FullName;
            }
            catch { return null; }
        }
        // item.Tag にフルパスが入っている場合はそれを使う
        if (item.Tag is string tagPath && !string.IsNullOrWhiteSpace(tagPath))
        {
            return tagPath;
        }
        try
        {
            return Path.Combine(currentPath, name);
        }
        catch { return null; }
    }
    private string? GetSelectedItemNameForHeaderCopy()
    {
        var item = GetCurrentBrowserItem();
        if (item == null) return null;
        string name = item.Text;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name;
    }
    private void UpdateHeaderInteractionTooltips()
    {
        if (_headerToolTip == null) return;
        string? path = GetCurrentDirectoryForHeaderCopy();
        string? fullPath = GetSelectedItemFullPathForHeaderCopy();
        string pathShortcut = ResolveBrowserCommandShortcutHint(CommandIds.BrowserPathEntryOpen);
        _headerToolTip.SetToolTip(lblPath, string.IsNullOrWhiteSpace(path) ? null : $"クリック / {pathShortcut} でパス入力:\r\n{path}");
        _headerToolTip.SetToolTip(infoRow2Panel, string.IsNullOrWhiteSpace(path) ? null : $"クリック / {pathShortcut} でパス入力:\r\n{path}");
        _headerToolTip.SetToolTip(lblName, string.IsNullOrWhiteSpace(fullPath) ? null : $"左クリックでフルパスをコピー:\r\n{fullPath}");
        _headerToolTip.SetToolTip(infoRow4Panel, string.IsNullOrWhiteSpace(fullPath) ? null : $"左クリックでフルパスをコピー:\r\n{fullPath}");
        // アイテムがない場合のカーソル調整
        lblName.Cursor = string.IsNullOrWhiteSpace(fullPath) ? Cursors.Default : Cursors.Hand;
    }
    #endregion
}
