using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    private readonly record struct MarkdownBrowserContext(string? SourceText, string? LinkTarget, string? ImageTarget);

    private bool _markdownBrowserInitialNavigation;
    private bool _markdownExternalNavigationPending;
    private Uri? _markdownBrowserDocumentUri;
    private HtmlDocument? _markdownBrowserEventDocument;
    private HtmlElementEventHandler? _markdownBrowserContextMenuHandler;
    private ContextMenuStrip? _markdownBrowserContextMenu;
    private MarkdownBrowserContext _markdownBrowserContext;

    private PreviewKind GetCurrentSelectionPreviewKind()
    {
        var item = GetCurrentBrowserItem();
        string? fullPath = item?.Tag as string;
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return PreviewKind.None;
        }
        return GetEffectivePreviewKind(fullPath);
    }
    private WebBrowser CreateMarkdownBrowser()
    {
        var browser = new WebBrowser
        {
            Dock = DockStyle.Fill,
            Visible = false,
            ScriptErrorsSuppressed = true,
            IsWebBrowserContextMenuEnabled = MarkdownPreviewBrowserPolicy.IsStandardContextMenuEnabled
        };
        browser.Navigating += (_, e) => HandleMarkdownBrowserNavigating(e);
        browser.NewWindow += (_, e) => e.Cancel = MarkdownPreviewBrowserPolicy.CancelNewWindow;
        browser.DocumentCompleted += (_, _) =>
        {
            AttachMarkdownBrowserDocumentEvents(browser);
            _markdownBrowserInitialNavigation = false;
            _markdownBrowserDocumentUri = browser.Url;
            if (browser.Visible && !IsDisposed)
            {
                BeginInvoke(new Action(() => browser.Focus()));
            }
        };
        viewerPanel.Controls.Add(browser);
        return browser;
    }

    private ContextMenuStrip CreateMarkdownBrowserContextMenu(WebBrowser browser)
    {
        var menu = new ContextMenuStrip();
        var copySelection = new ToolStripMenuItem("選択範囲をコピー", null, (_, _) => browser.Document?.ExecCommand("Copy", false, string.Empty));
        var copySelectedMarkdown = new ToolStripMenuItem("選択部分を含むMarkdownをコピー", null, (_, _) => CopyMarkdownContextText(GetMarkdownBrowserSelectionSourceText(browser)));
        var copySource = new ToolStripMenuItem("このブロックのMarkdownをコピー", null, (_, _) => CopyMarkdownContextText(_markdownBrowserContext.SourceText));
        var copyLink = new ToolStripMenuItem("リンク先をコピー", null, (_, _) => CopyMarkdownContextText(_markdownBrowserContext.LinkTarget));
        var copyImage = new ToolStripMenuItem("画像のパスをコピー", null, (_, _) => CopyMarkdownContextText(_markdownBrowserContext.ImageTarget));
        var selectAll = new ToolStripMenuItem("すべて選択", null, (_, _) => browser.Document?.ExecCommand("SelectAll", false, string.Empty));
        var separator = new ToolStripSeparator();
        menu.Items.AddRange([copySelection, copySelectedMarkdown, copySource, copyLink, copyImage, separator, selectAll]);
        menu.Opening += (_, _) =>
        {
            bool hasSelection = HasMarkdownBrowserSelection(browser);
            if (hasSelection)
            {
                copySelection.Visible = true;
                copySelection.Enabled = true;
                copySelectedMarkdown.Visible = true;
                copySelectedMarkdown.Enabled = GetMarkdownBrowserSelectionSourceText(browser) != null;
                copySource.Visible = false;
                copyLink.Visible = false;
                copyImage.Visible = false;
                separator.Visible = true;
                return;
            }

            copySelection.Visible = false;
            copySelectedMarkdown.Visible = false;
            copySource.Visible = !string.IsNullOrEmpty(_markdownBrowserContext.SourceText);
            copySource.Enabled = copySource.Visible;
            copySource.Text = !string.IsNullOrEmpty(_markdownBrowserContext.LinkTarget)
                ? "このリンクのMarkdownをコピー"
                : !string.IsNullOrEmpty(_markdownBrowserContext.ImageTarget)
                    ? "この画像のMarkdownをコピー"
                    : "このブロックのMarkdownをコピー";
            copyLink.Visible = !string.IsNullOrEmpty(_markdownBrowserContext.LinkTarget);
            copyImage.Visible = !string.IsNullOrEmpty(_markdownBrowserContext.ImageTarget);
            separator.Visible = copySource.Visible || copyLink.Visible || copyImage.Visible;
        };
        return menu;
    }

    private void AttachMarkdownBrowserDocumentEvents(WebBrowser browser)
    {
        HtmlDocument? document = browser.Document;
        if (document == null || ReferenceEquals(document, _markdownBrowserEventDocument))
        {
            return;
        }

        if (_markdownBrowserEventDocument != null && _markdownBrowserContextMenuHandler != null)
        {
            _markdownBrowserEventDocument.ContextMenuShowing -= _markdownBrowserContextMenuHandler;
        }

        _markdownBrowserEventDocument = document;
        _markdownBrowserContextMenuHandler = (_, e) =>
        {
            e.ReturnValue = false;
            _markdownBrowserContext = GetMarkdownBrowserContext(document.GetElementFromPoint(e.ClientMousePosition));
            _markdownBrowserContextMenu ??= CreateMarkdownBrowserContextMenu(browser);
            _markdownBrowserContextMenu.Show(browser, browser.PointToClient(Cursor.Position));
        };
        document.ContextMenuShowing += _markdownBrowserContextMenuHandler;
    }

    private static bool HasMarkdownBrowserSelection(WebBrowser browser)
    {
        try
        {
            return browser.Document?.InvokeScript("midfdHasSelection") is bool hasSelection && hasSelection;
        }
        catch
        {
            return false;
        }
    }

    private string? GetMarkdownBrowserSelectionSourceText(WebBrowser browser)
    {
        try
        {
            string? ranges = browser.Document?.InvokeScript("midfdGetSelectionSourceBlocks") as string;
            return _markdownViewerSource == null
                ? null
                : MarkdownSelectionSourceResolver.ResolveContainingBlocks(_markdownViewerSource, ranges);
        }
        catch
        {
            return null;
        }
    }

    private MarkdownBrowserContext GetMarkdownBrowserContext(HtmlElement? element)
    {
        string? source = null;
        string? link = null;
        string? image = null;
        while (element != null)
        {
            link ??= AttributeOrNull(element, "data-md-link-target");
            image ??= AttributeOrNull(element, "data-md-image-target");
            source ??= GetMarkdownSourceRange(element);
            element = element.Parent;
        }
        return new MarkdownBrowserContext(source, link, image);
    }

    private string? GetMarkdownSourceRange(HtmlElement element)
    {
        if (_markdownViewerSource == null
            || !int.TryParse(AttributeOrNull(element, "data-md-start"), out int start)
            || !int.TryParse(AttributeOrNull(element, "data-md-length"), out int length)
            || start < 0 || length < 0 || start > _markdownViewerSource.Length - length)
        {
            return null;
        }
        return _markdownViewerSource.Substring(start, length);
    }

    private static string? AttributeOrNull(HtmlElement element, string name)
    {
        string value = element.GetAttribute(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private void CopyMarkdownContextText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        Clipboard.SetText(text);
        ShowStatusMessage("Markdownをコピーしました。");
    }

    private void HandleMarkdownBrowserNavigating(WebBrowserNavigatingEventArgs e)
    {
        MarkdownNavigationResult result = MarkdownNavigationPolicy.Evaluate(
            e.Url?.AbsoluteUri,
            _markdownBrowserDocumentUri,
            _markdownBrowserInitialNavigation);
        if (result.AllowsInternalNavigation)
        {
            return;
        }

        e.Cancel = true;
        if (result.Decision == MarkdownNavigationDecision.ConfirmExternalHttp
            && result.TargetUri != null)
        {
            ConfirmAndLaunchMarkdownExternalUrl(result.TargetUri);
            return;
        }

        ShowStatusMessage("Markdown Previewではこのリンクを開けません。");
    }

    private void ConfirmAndLaunchMarkdownExternalUrl(Uri targetUri)
    {
        if (_markdownExternalNavigationPending)
        {
            return;
        }

        _markdownExternalNavigationPending = true;
        try
        {
            string message = $"外部リンクを標準ブラウザで開きますか？\n\n{targetUri.AbsoluteUri}";
            if (MessageBox.Show(
                    this,
                    message,
                    "外部リンク",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            Process.Start(new ProcessStartInfo(targetUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatusMessage($"外部リンクを開けませんでした: {ex.Message}");
        }
        finally
        {
            _markdownExternalNavigationPending = false;
        }
    }

    private DataGridView CreateDelimitedGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            Visible = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = false,
            TabStop = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false
        };
        ApplyDelimitedGridTheme(grid);
        viewerPanel.Controls.Add(grid);
        return grid;
    }

    private void ApplyDelimitedGridTheme(DataGridView grid)
    {
        UiThemeColors theme = UiThemeResolver.Resolve(_settings.Appearance);
        Color selectionBack = MidFDColors.ListSelectedBack;
        Color selectionFore = MidFDColors.ListSelectedFore;
        var cellStyle = new DataGridViewCellStyle
        {
            BackColor = theme.ViewerBackColor,
            ForeColor = theme.ViewerForeColor,
            SelectionBackColor = selectionBack,
            SelectionForeColor = selectionFore
        };
        var headerStyle = new DataGridViewCellStyle
        {
            BackColor = theme.ViewerStatusBackColor,
            ForeColor = theme.ViewerStatusForeColor,
            SelectionBackColor = selectionBack,
            SelectionForeColor = selectionFore
        };

        grid.BackgroundColor = theme.ViewerBackColor;
        grid.ForeColor = theme.ViewerForeColor;
        grid.GridColor = theme.BorderColor;
        grid.DefaultCellStyle = cellStyle;
        grid.RowsDefaultCellStyle = cellStyle;
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle(cellStyle);
        grid.ColumnHeadersDefaultCellStyle = headerStyle;
        grid.RowHeadersDefaultCellStyle = headerStyle;
    }
    private PreviewKind GetEffectivePreviewKind(string path, PreviewKind rawKind)
    {
        var result = PreviewRoutingService.Route(path, rawKind, _settings.Preview?.VideoToolDirectory);
        return result.EffectiveKind;
    }
    private PreviewKind GetEffectivePreviewKind(string path)
    {
        var result = PreviewRoutingService.Route(path, _settings.Preview?.VideoToolDirectory);
        return result.EffectiveKind;
    }
    private void ApplyViewerChromeState()
    {
        if (_markdownBrowser != null) _markdownBrowser.Visible = _currentViewerKind == PreviewKind.Markdown && !IsMarkdownViewerRawMode;
        if (_delimitedGrid != null) _delimitedGrid.Visible = _currentViewerKind == PreviewKind.CsvTsv;
        bool compactViewer = _uiMode == UIMode.Viewer
            && (_currentViewerKind == PreviewKind.Text
                || _currentViewerKind == PreviewKind.Markdown
                || _currentViewerKind == PreviewKind.CsvTsv
                || _currentViewerKind == PreviewKind.Sqlite
                || _currentViewerKind == PreviewKind.Binary
                || _currentViewerKind == PreviewKind.LargeText);
        Presentation.PreviewUiPresenter.ApplyViewerChromeState(
            compactViewer,
            _uiMode == UIMode.Viewer && _currentViewerKind == PreviewKind.LargeText,
            titleHeaderPanel,
            headerPanel,
            sepBeforeTopPanel,
            topPanel,
            _largeFileControl);
        ApplyFunctionBarVisibilityForCurrentContext();
        UpdateMarkdownViewerModeStatus();
    }
    private bool IsMarkdownViewerRawMode => (_settings.Preview?.MarkdownViewerMode ?? MarkdownViewerMode.Rendered) == MarkdownViewerMode.Raw;

    private void SetMarkdownViewerMode(MarkdownViewerMode mode, bool save = true)
    {
        _settings.Preview ??= new PreviewSettings();
        bool changed = _settings.Preview.MarkdownViewerMode != mode;
        _settings.Preview.MarkdownViewerMode = mode;
        if (save && changed)
        {
            SettingsManager.Save(_settings);
        }

        if (_uiMode != UIMode.Viewer || _currentViewerKind != PreviewKind.Markdown || _markdownViewerSource == null)
        {
            UpdateMarkdownViewerModeStatus();
            return;
        }

        bool rawMode = mode == MarkdownViewerMode.Raw;
        ApplyViewerChromeState();
        if (rawMode)
        {
            viewerTextBox.ReadOnly = true;
            viewerTextBox.Text = _markdownViewerSource;
            viewerTextBox.Visible = true;
            viewerTextBox.BringToFront();
            viewerTextBox.Focus();
            ApplyViewerStatusLine("Markdown raw preview applied");
            return;
        }

        viewerTextBox.Visible = false;
        _markdownBrowser?.BringToFront();
        _markdownBrowser?.Focus();
        ApplyViewerStatusLine("Markdown rendered preview applied");
    }

    private void UpdateMarkdownViewerModeStatus()
    {
        if (_markdownModeSpacer == null || _markdownRenderedModeStatusLabel == null || _markdownRawModeStatusLabel == null)
        {
            return;
        }

        bool visible = _uiMode == UIMode.Viewer && _currentViewerKind == PreviewKind.Markdown && _markdownViewerSource != null;
        _markdownModeSpacer.Visible = visible;
        _markdownRenderedModeStatusLabel.Visible = visible;
        _markdownRawModeStatusLabel.Visible = visible;
        ApplyMarkdownModeStatusStyle(_markdownRenderedModeStatusLabel, "Rendered", !IsMarkdownViewerRawMode);
        ApplyMarkdownModeStatusStyle(_markdownRawModeStatusLabel, "Raw", IsMarkdownViewerRawMode);
    }

    private ToolStripStatusLabel CreateMarkdownModeStatusLabel(string text, MarkdownViewerMode mode)
    {
        var label = new ToolStripStatusLabel
        {
            Text = text,
            Alignment = ToolStripItemAlignment.Right,
            AutoSize = true,
            Visible = false,
            IsLink = false,
            Margin = new Padding(4, 1, 4, 1),
            Padding = new Padding(0, 1, 0, 1),
            Overflow = ToolStripItemOverflow.Never
        };
        label.Click += (_, _) => SetMarkdownViewerMode(mode);
        return label;
    }

    private void ApplyMarkdownModeStatusStyle(ToolStripStatusLabel label, string modeName, bool selected)
    {
        label.Text = selected ? $"✓ {modeName}" : modeName;
        label.BackColor = statusStrip.BackColor;
        label.ForeColor = statusLabel.ForeColor;
        label.Font = new Font(statusStrip.Font, selected ? FontStyle.Bold : FontStyle.Regular);
    }
    private void ExecuteViewerFind()
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            ExecuteLargeFileFind();
            return;
        }
        if (!viewerTextBox.Visible) return;
        string? query = SimpleInputDialog.ShowNullable("検索:", "Viewer 検索 (Ctrl+F)", _viewerSearchKeyword);
        if (query == null) return; // キャンセル時は現状維持
        _viewerSearchKeyword = query;
        ApplyViewerStatusLine(); // ステータスに反映
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowStatusMessage("検索キーワードをクリアしました。");
            return;
        }
        // 初回検索: 現在位置の次から前方へ
        int start = viewerTextBox.SelectionStart + viewerTextBox.SelectionLength;
        _ = InnerExecuteViewerSearch(query, start, backward: false);
    }
    private void ExecuteViewerFindNext(bool backward)
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            ExecuteLargeFileFindNext(backward);
            return;
        }
        if (!viewerTextBox.Visible) return;
        if (string.IsNullOrWhiteSpace(_viewerSearchKeyword))
        {
            ShowStatusMessage("検索キーワードが未設定です。新規検索ダイアログを開きます...");
            ExecuteViewerFind();
            return;
        }
        int start;
        if (backward)
        {
            // 前方向: 現在の選択開始位置より前から探す
            start = viewerTextBox.SelectionStart;
        }
        else
        {
            // 次方向: 現在の選択終了位置から探す
            start = viewerTextBox.SelectionStart + viewerTextBox.SelectionLength;
        }
        _ = InnerExecuteViewerSearch(_viewerSearchKeyword, start, backward);
    }
    private async Task InnerExecuteViewerSearch(string query, int start, bool backward, bool isWrapAround = false, int chunkCrossoverCount = 0)
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            await ExecuteLargeFileSearchAsync(query, backward, isWrapAround);
            return;
        }
        RichTextBoxFinds options = backward ? RichTextBoxFinds.Reverse : RichTextBoxFinds.None;
        int result = viewerTextBox.Find(query, start, options);
        if (result < 0 && !isWrapAround)
        {
            if (backward)
            {
                result = viewerTextBox.Find(query, viewerTextBox.TextLength, options);
                if (result >= 0) ShowStatusMessage("末尾から再検索しました");
            }
            else
            {
                result = viewerTextBox.Find(query, 0, options);
                if (result >= 0) ShowStatusMessage("先頭から再検索しました");
            }
        }
        if (result >= 0)
        {
            viewerTextBox.Focus();
        }
        else
        {
            ShowStatusMessage($"一致する文字列が見つかりません: \"{query}\"");
        }
    }
    private async Task ExecuteLargeFileSearchAsync(string query, bool backward, bool isWrapAround)
    {
        if (_largeFileState == null) return;
        var state = _largeFileState;
        string normalizedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            ClearLargeFileSearchHit(state);
            ShowStatusMessage("検索キーワードが未設定です。");
            return;
        }
        int requestId = ++state.SearchRequestId;
        state.LastSearchText = normalizedQuery;
        state.LastSearchBackward = backward;
        _viewerSearchKeyword = normalizedQuery;
        ApplyViewerStatusLine();
        ShowStatusMessage($"検索中: {normalizedQuery}");
        var token = _previewRequestCoordinator.Token;
        var encoding = GetCurrentViewerEncoding();
        var (startLine, startColumn) = GetLargeFileSearchStartPosition(state, normalizedQuery, backward, isWrapAround);
        try
        {
            var hit = await Services.LargeFileLineReaderService.SearchTextAsync(
                state,
                normalizedQuery,
                startLine,
                startColumn,
                backward,
                encoding,
                token);
            if (!IsLargeFileSearchRequestActive(state, requestId))
            {
                return;
            }
            if (hit.HasValue)
            {
                await ApplyLargeFileSearchHitAsync(state, requestId, normalizedQuery, hit.Value.Line, hit.Value.Column, hit.Value.Length, backward, isWrapAround);
                return;
            }
            if (!isWrapAround)
            {
                ShowStatusMessage(backward ? "先頭まで検索しました。末尾から再検索します..." : "末尾まで検索しました。先頭から再検索します...");
                await ExecuteLargeFileSearchAsync(normalizedQuery, backward, true);
                return;
            }
            ClearLargeFileSearchHit(state);
            ShowStatusMessage($"一致する文字列が見つかりません: \"{normalizedQuery}\"");
        }
        catch (OperationCanceledException)
        {
        }
    }
    private void EnsureStatusBarVisible()
    {
        Presentation.PreviewUiPresenter.EnsureStatusBarVisible(statusStrip, statusLabel);
    }
    private void ExecuteLargeFileFind()
    {
        if (_largeFileState == null)
        {
            return;
        }
        string initialQuery = string.IsNullOrWhiteSpace(_largeFileState.LastSearchText)
            ? _viewerSearchKeyword
            : _largeFileState.LastSearchText;
        string? query = SimpleInputDialog.ShowNullable("検索:", "LargeText 検索 (Ctrl+F)", initialQuery);
        if (query == null)
        {
            return;
        }
        string normalizedQuery = query.Trim();
        bool continueFromActiveHit = !string.IsNullOrWhiteSpace(normalizedQuery)
            && string.Equals(_largeFileState.LastSearchText, normalizedQuery, StringComparison.OrdinalIgnoreCase)
            && _largeFileState.ActiveSearchHitLine.HasValue;
        _viewerSearchKeyword = normalizedQuery;
        _largeFileState.LastSearchText = normalizedQuery;
        ApplyViewerStatusLine();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            ClearLargeFileSearchHit(_largeFileState);
            ShowStatusMessage("検索キーワードをクリアしました。");
            return;
        }
        if (!continueFromActiveHit)
        {
            _largeFileState.ActiveSearchHitLine = null;
            _largeFileState.ActiveSearchHitColumn = 0;
            _largeFileState.ActiveSearchHitLength = 0;
        }
        _ = ExecuteLargeFileSearchAsync(normalizedQuery, backward: false, isWrapAround: false);
    }
    private void ExecuteLargeFileFindNext(bool backward)
    {
        if (_largeFileState == null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_largeFileState.LastSearchText))
        {
            ShowStatusMessage("検索キーワードが未設定です。新規検索ダイアログを開きます...");
            ExecuteLargeFileFind();
            return;
        }
        _ = ExecuteLargeFileSearchAsync(_largeFileState.LastSearchText, backward, false);
    }
    private (int StartLine, int StartColumn) GetLargeFileSearchStartPosition(LargeFilePreviewState state, string query, bool backward, bool isWrapAround)
    {
        if (isWrapAround)
        {
            return backward
                ? (Math.Max(0, state.TotalLines - 1), int.MaxValue)
                : (0, 0);
        }
        if (state.ActiveSearchHitLine.HasValue
            && string.Equals(state.LastSearchText, query, StringComparison.OrdinalIgnoreCase))
        {
            if (backward)
            {
                return (
                    state.ActiveSearchHitLine.Value,
                    Math.Max(-1, state.ActiveSearchHitColumn - 1));
            }
            return (
                state.ActiveSearchHitLine.Value,
                state.ActiveSearchHitColumn + Math.Max(1, state.ActiveSearchHitLength));
        }
        return backward
            ? (Math.Max(0, state.FirstVisibleLine), int.MaxValue)
            : (Math.Max(0, state.FirstVisibleLine), 0);
    }
    private async Task ApplyLargeFileSearchHitAsync(
        LargeFilePreviewState state,
        int requestId,
        string query,
        int hitLine,
        int hitColumn,
        int hitLength,
        bool backward,
        bool isWrapAround)
    {
        if (!IsLargeFileSearchRequestActive(state, requestId))
        {
            return;
        }
        state.ActiveSearchHitLine = hitLine;
        state.ActiveSearchHitColumn = hitColumn;
        state.ActiveSearchHitLength = hitLength;
        _largeFileControl.SetActiveSearchHit(hitLine, hitColumn, hitLength);
        int targetFirstLine = Math.Max(0, hitLine - Math.Max(1, _largeFileControl.VisibleLineCount / 2));
        await NavigateLargeFilePreviewAsync(targetFirstLine, "SearchHit");
        if (!IsLargeFileSearchRequestActive(state, requestId))
        {
            return;
        }
        _largeFileControl.SetActiveSearchHit(hitLine, hitColumn, hitLength);
        ApplyViewerStatusLine();
        string wrapPrefix = isWrapAround
            ? (backward ? "末尾から再検索しました。 " : "先頭から再検索しました。 ")
            : string.Empty;
        ShowStatusMessage($"{wrapPrefix}{query}: {hitLine + 1:N0} 行目");
    }
    private bool IsLargeFileSearchRequestActive(LargeFilePreviewState state, int requestId)
    {
        return ReferenceEquals(_largeFileState, state)
            && state.SearchRequestId == requestId
            && _uiMode == UIMode.Viewer
            && _currentViewerKind == PreviewKind.LargeText
            && string.Equals(_currentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase);
    }
    private void ClearLargeFileSearchHit(LargeFilePreviewState state)
    {
        state.ActiveSearchHitLine = null;
        state.ActiveSearchHitColumn = 0;
        state.ActiveSearchHitLength = 0;
        _largeFileControl.ClearActiveSearchHit();
        ApplyViewerStatusLine();
    }
}
