using System.Drawing;
using System.Windows.Forms;
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
        if (FileListColorResolver.NormalizeCoreTheme(_settings.Appearance?.ColorTheme) == "Light")
        {
            // Light テーマ: 左右線はスキップ、下辺は SeparatorLine で弱めに描画
            using (var pen = new Pen(MidFDColors.SeparatorLine, 1))
            {
                // 下辺 (一覧領域の外枠として描画)
                e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
            }
        }
        else
        {
            // 既存どおり: BorderLine で左辺/右辺/下辺を描く
            using (var pen = new Pen(MidFDColors.BorderLine, 1))
            {
                // 左辺
                e.Graphics.DrawLine(pen, 0, 0, 0, panel.Height);
                // 右辺
                e.Graphics.DrawLine(pen, panel.Width - 1, 0, panel.Width - 1, panel.Height);
                // 下辺 (一覧領域の外枠として描画)
                e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
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
        WireHeaderCopyInteractions();
    }
    private void InitializeHeaderContextMenus()
    {
        // Path 行用メニュー
        _headerPathContextMenu = new ContextMenuStrip();
        var copyPathItem = new ToolStripMenuItem("パスをコピー");
        copyPathItem.Click += (_, _) => CopyCurrentDirectoryFromHeader();
        _headerPathContextMenu.Items.Add(copyPathItem);
        // Item 行用メニュー
        _headerItemContextMenu = new ContextMenuStrip();
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
        _headerToolTip.SetToolTip(lblPath, string.IsNullOrWhiteSpace(path) ? null : $"クリック / Ctrl+L でパス入力:\r\n{path}");
        _headerToolTip.SetToolTip(infoRow2Panel, string.IsNullOrWhiteSpace(path) ? null : $"クリック / Ctrl+L でパス入力:\r\n{path}");
        _headerToolTip.SetToolTip(lblName, string.IsNullOrWhiteSpace(fullPath) ? null : $"左クリックでフルパスをコピー:\r\n{fullPath}");
        _headerToolTip.SetToolTip(infoRow4Panel, string.IsNullOrWhiteSpace(fullPath) ? null : $"左クリックでフルパスをコピー:\r\n{fullPath}");
        // アイテムがない場合のカーソル調整
        lblName.Cursor = string.IsNullOrWhiteSpace(fullPath) ? Cursors.Default : Cursors.Hand;
    }
    #endregion
}
