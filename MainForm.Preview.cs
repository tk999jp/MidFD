using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Runtime;
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
        return _viewerWorkflowApplicationCoordinator.ResolveSelectionPreviewKind(fullPath);
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
        var copySelection = new ToolStripMenuItem("選択範囲をコピー", null, (_, _) =>
            _ = DispatchTextViewerCopy(
                "Markdown.ContextMenu.RenderedSelection",
                TextViewerCopyOrigin.EmbeddedViewer,
                renderedSelection: true));
        var copySelectedMarkdown = new ToolStripMenuItem("選択部分を含むMarkdownをコピー", null, (_, _) =>
            _ = DispatchMarkdownSourceCopy(GetMarkdownBrowserSelectionSourceText(browser)));
        var copySource = new ToolStripMenuItem("このブロックのMarkdownをコピー", null, (_, _) =>
            _ = DispatchMarkdownSourceCopy(_markdownBrowserContext.SourceText));
        var copyLink = new ToolStripMenuItem("リンク先をコピー", null, (_, _) =>
            _ = DispatchMarkdownSourceCopy(_markdownBrowserContext.LinkTarget));
        var copyImage = new ToolStripMenuItem("画像のパスをコピー", null, (_, _) =>
            _ = DispatchMarkdownSourceCopy(_markdownBrowserContext.ImageTarget));
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
            return _viewerApplicationCoordinator.MarkdownSource == null
                ? null
                : MarkdownSelectionSourceResolver.ResolveContainingBlocks(_viewerApplicationCoordinator.MarkdownSource, ranges);
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
        if (_viewerApplicationCoordinator.MarkdownSource == null
            || !int.TryParse(AttributeOrNull(element, "data-md-start"), out int start)
            || !int.TryParse(AttributeOrNull(element, "data-md-length"), out int length)
            || start < 0 || length < 0 || start > _viewerApplicationCoordinator.MarkdownSource.Length - length)
        {
            return null;
        }
        return _viewerApplicationCoordinator.MarkdownSource.Substring(start, length);
    }

    private static string? AttributeOrNull(HtmlElement element, string name)
    {
        string value = element.GetAttribute(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private bool DispatchMarkdownSourceCopy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        return DispatchTextViewerCopy(
            "Markdown.ContextMenu.SourceCopy",
            TextViewerCopyOrigin.EmbeddedViewer,
            selectedText: text,
            statusMessage: "Markdownをコピーしました。");
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

            ExternalUrlLaunchResult launchResult = OpenExternalUrlThroughCommand(
                targetUri.AbsoluteUri,
                "TextViewer.Markdown.ExternalLink");
            TextPreviewInteractionHelper.ReportExternalUrlLaunchResult(
                launchResult,
                ShowStatusMessage,
                this,
                showErrorDialog: false);
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

    private ExternalUrlLaunchResult OpenExternalUrlThroughCommand(string target, string source)
    {
        if (!TextPreviewInteractionHelper.CanOpenWebLink(target)
            || !Uri.TryCreate(target, UriKind.Absolute, out Uri? targetUri))
        {
            return ExternalUrlLaunchResult.InvalidTarget();
        }

        bool applied = ExecuteCommandCore(
            CommandIds.AppOpenExternalUrl,
            new CommandExecutionContext
            {
                Scope = CommandScope.Global,
                Source = source,
                ExternalUrlOpen = new ExternalUrlOpenCommandContext(targetUri)
            },
            out CommandApplicationResult commandResult,
            out string? effectFailureMessage);

        if (!commandResult.Handled)
        {
            return ExternalUrlLaunchResult.CommandRejected();
        }

        return applied
            ? ExternalUrlLaunchResult.Opened()
            : ExternalUrlLaunchResult.EffectFailed(effectFailureMessage);
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
        UiThemeColors theme = UiThemeResolver.Resolve(_settingsCoordinator.Value.Appearance);
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
    private void ApplyViewerChromeState()
    {
        if (_markdownBrowser != null) _markdownBrowser.Visible = _viewerApplicationCoordinator.CurrentKind == PreviewKind.Markdown && !IsMarkdownViewerRawMode;
        if (_delimitedGrid != null) _delimitedGrid.Visible = _viewerApplicationCoordinator.CurrentKind == PreviewKind.CsvTsv;
        bool compactViewer = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer
            && (_viewerApplicationCoordinator.CurrentKind == PreviewKind.Text
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.Markdown
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.CsvTsv
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.Sqlite
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.Binary
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText);
        Presentation.PreviewUiPresenter.ApplyViewerChromeState(
            compactViewer,
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer && _viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText,
            titleHeaderPanel,
            headerPanel,
            sepBeforeTopPanel,
            topPanel,
            _largeFileControl);
        ApplyFunctionBarVisibilityForCurrentContext();
        UpdateMarkdownViewerModeStatus();
        UpdateViewerEditorStatus();
    }

    private void UpdateViewerEditorStatus()
    {
        if (_viewerEditorStatusLabel == null) return;
        PreviewKind kind = _viewerApplicationCoordinator.DisplayedPreviewKind;
        bool supported = kind is PreviewKind.Text or PreviewKind.LargeText or PreviewKind.Markdown or PreviewKind.CsvTsv;
        bool viewer = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer;
        string? displayedSource = _viewerApplicationCoordinator.DisplayedPreviewTarget;
        _viewerEditorStatusLabel.Visible = viewer && supported;
        _viewerEditorStatusLabel.Enabled = viewer && supported && !string.IsNullOrWhiteSpace(displayedSource);
        _viewerEditorStatusLabel.ToolTipText = "E: 表示中のsource fileを外部Editorで開きます。";
    }
    private bool IsMarkdownViewerRawMode => _viewerWorkflowApplicationCoordinator.IsMarkdownViewerRawMode;

    private void SetMarkdownViewerMode(MarkdownViewerMode mode, bool save = true)
    {
        if (save)
        {
            ExecuteCommandFromUi(CommandIds.TextViewerMarkdownModeSet, new CommandExecutionContext
            {
                Scope = CommandScope.TextViewer,
                Source = "Viewer.MarkdownModeStatus",
                TextViewerMarkdownMode = mode
            });
            return;
        }

        _viewerWorkflowApplicationCoordinator.SetMarkdownViewerMode(mode, save: false);

        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Viewer || _viewerApplicationCoordinator.CurrentKind != PreviewKind.Markdown || _viewerApplicationCoordinator.MarkdownSource == null)
        {
            UpdateMarkdownViewerModeStatus();
            return;
        }

        ApplyMarkdownViewerMode(mode);
    }

    private void UpdateMarkdownViewerModeStatus()
    {
        if (_markdownModeSpacer == null || _markdownRenderedModeStatusLabel == null || _markdownRawModeStatusLabel == null)
        {
            return;
        }

        bool visible = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer && _viewerApplicationCoordinator.CurrentKind == PreviewKind.Markdown && _viewerApplicationCoordinator.MarkdownSource != null;
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
        bool largeFile = _viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText
            && _viewerApplicationCoordinator.LargeFileState != null;
        if (!largeFile && !viewerTextBox.Visible) return;
        string initialQuery = largeFile
            && !string.IsNullOrWhiteSpace(_viewerApplicationCoordinator.LargeFileState?.LastSearchText)
                ? _viewerApplicationCoordinator.LargeFileState!.LastSearchText
                : _viewerApplicationCoordinator.SearchKeyword;
        ExecuteCommandFromUi(CommandIds.TextViewerSearch, new CommandExecutionContext
        {
            Scope = CommandScope.TextViewer,
            Source = "Viewer.Search",
            TextViewerSearch = new TextViewerSearchCommandContext(
                largeFile ? string.Empty : viewerTextBox.Text,
                initialQuery,
                largeFile ? 0 : viewerTextBox.SelectionStart + viewerTextBox.SelectionLength,
                IsLargeFile: largeFile)
        });
    }
    private void ExecuteViewerFindNext(bool backward)
    {
        bool largeFile = _viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText
            && _viewerApplicationCoordinator.LargeFileState != null;
        if (!largeFile && !viewerTextBox.Visible) return;
        string currentQuery = largeFile
            ? _viewerApplicationCoordinator.LargeFileState!.LastSearchText
            : _viewerApplicationCoordinator.SearchKeyword;
        if (string.IsNullOrWhiteSpace(currentQuery))
        {
            ShowStatusMessage("検索キーワードが未設定です。新規検索ダイアログを開きます...");
            ExecuteViewerFind();
            return;
        }
        string query = currentQuery;
        ExecuteCommandFromUi(
            backward ? CommandIds.TextViewerSearchPrevious : CommandIds.TextViewerSearchNext,
            new CommandExecutionContext
            {
                Scope = CommandScope.TextViewer,
                Source = backward ? "Viewer.SearchPrevious" : "Viewer.SearchNext",
                TextViewerSearch = new TextViewerSearchCommandContext(
                    largeFile ? string.Empty : viewerTextBox.Text,
                    query,
                    largeFile
                        ? 0
                        : backward
                            ? viewerTextBox.SelectionStart
                            : viewerTextBox.SelectionStart + viewerTextBox.SelectionLength,
                    backward,
                    largeFile,
                    PreserveLargeFileSearchHit: largeFile)
            });
    }
    private void EnsureStatusBarVisible()
    {
        Presentation.PreviewUiPresenter.EnsureStatusBarVisible(statusStrip, statusLabel);
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
        if (!_viewerWorkflowApplicationCoordinator.IsLargeFileSearchRequestActive(state, requestId))
        {
            return;
        }
        _viewerWorkflowApplicationCoordinator.SetLargeFileSearchHit(state, hitLine, hitColumn, hitLength);
        _largeFileControl.SetActiveSearchHit(hitLine, hitColumn, hitLength);
        int targetFirstLine = Math.Max(0, hitLine - Math.Max(1, _largeFileControl.VisibleLineCount / 2));
        await NavigateLargeFilePreviewAsync(targetFirstLine, "SearchHit");
        if (!_viewerWorkflowApplicationCoordinator.IsLargeFileSearchRequestActive(state, requestId))
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
    private void ClearLargeFileSearchHit(LargeFilePreviewState state)
    {
        _viewerWorkflowApplicationCoordinator.ClearLargeFileSearchHit(state);
        _largeFileControl.ClearActiveSearchHit();
        ApplyViewerStatusLine();
    }

    bool IViewerPreviewUiPort.IsViewerMode => _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer;

    int IViewerPreviewUiPort.VisibleLineCount => _largeFileControl.VisibleLineCount;

    bool IViewerPreviewUiPort.IsLatestRequest(int requestId, string path, CancellationToken token)
        => !IsDisposed && !Disposing && IsLatestPreviewRequest(requestId, path, token);

    TextPreviewEncodingOverride IViewerPreviewUiPort.GetTextPreviewEncodingOverride()
        => _viewerApplicationCoordinator.EncodingPreference switch
        {
            ViewerEncodingPreference.Utf8 => TextPreviewEncodingOverride.Utf8,
            ViewerEncodingPreference.ShiftJis => TextPreviewEncodingOverride.ShiftJis,
            _ => TextPreviewEncodingOverride.Auto
        };

    void IViewerPreviewUiPort.ClearPreview(string message, int requestId)
        => ClearPreview(message, requestId);

    void IViewerPreviewUiPort.ApplyViewerChromeState()
        => ApplyViewerChromeState();

    void IViewerPreviewUiPort.ApplyViewerStatus(string reason)
        => ApplyViewerStatusLine(reason);

    void IViewerPreviewUiPort.ShowStatusMessage(string message)
        => ShowStatusMessage(message);

    void IViewerPreviewUiPort.ReturnToBrowserForVideo()
    {
        HideViewerContentBeforeExit();
        ShowBrowserSurfaceForMode();
        EnsureStatusBarVisible();
        ApplyViewerChromeState();
        UpdateFunctionBar();
        UpdateMenuStripState();
        RefreshBrowserStatusForMode();
    }

    void IViewerPreviewUiPort.ApplyImagePreview(string path)
    {
        ApplyViewerChromeState();
        viewerMessageLabel.Text = "画像は専用画像ビューアで表示します。\nV / Enter で開きます。";
        viewerMessageLabel.Visible = true;
        viewerTextBox.Visible = false;
        viewerPictureBox.Image?.Dispose();
        viewerPictureBox.Image = null;
        viewerPictureBox.Visible = false;
        var openViewer = GetReusableImageViewer();
        if (openViewer != null && !string.Equals(openViewer.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            openViewer.LoadMedia(path, PreviewKind.Image, showErrorMessage: false);
        }
    }

    void IViewerPreviewUiPort.ApplyVideoPreview(string path)
    {
        ClearPreview("内蔵MidPlayerで動画を再生します。", -1);
        OpenImageViewerCore(path, PreviewKind.Video);
    }

    void IViewerPreviewUiPort.ApplyTextPreview(TextPreviewContent content)
    {
        ApplyViewerChromeState();
        Presentation.PreviewUiPresenter.ApplyPlainTextContent(
            viewerTextBox,
            viewerMessageLabel,
            viewerPictureBox,
            content.Text);
        if (IsSearchHitPreviewPending(_viewerApplicationCoordinator.CurrentPreviewTarget ?? string.Empty))
        {
            ApplySearchHitToTextPreview(content.Text);
        }
        ApplyViewerStatusLine("Text preview applied");
    }

    void IViewerPreviewUiPort.ApplyMarkdownPreview(string text, string path, MarkdownViewerMode mode)
    {
        ApplyViewerChromeState();
        viewerMessageLabel.Visible = false;
        viewerPictureBox.Visible = false;
        viewerTextBox.Visible = false;
        _markdownBrowser ??= CreateMarkdownBrowser();
        _markdownBrowserInitialNavigation = true;
        _markdownBrowserDocumentUri = null;
        _markdownBrowser.DocumentText = MarkdownHtmlRenderer.Render(text, path);
        bool navigateToSearchHit = IsSearchHitPreviewPending(path);
        ApplyMarkdownViewerMode(navigateToSearchHit ? MarkdownViewerMode.Raw : mode);
        if (navigateToSearchHit)
        {
            ApplySearchHitToTextPreview(viewerTextBox.Text);
        }
        ApplyViewerStatusLine("Markdown preview applied");
    }

    private void ApplyMarkdownViewerMode(MarkdownViewerMode mode)
    {
        bool rawMode = mode == MarkdownViewerMode.Raw;
        ApplyViewerChromeState();
        if (rawMode)
        {
            viewerTextBox.ReadOnly = true;
            viewerTextBox.Text = _viewerApplicationCoordinator.MarkdownSource ?? string.Empty;
            viewerTextBox.Select(0, 0);
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

    void IViewerPreviewUiPort.ApplyDelimitedPreview(DelimitedTextTable table)
    {
        ApplyViewerChromeState();
        viewerMessageLabel.Visible = false;
        viewerPictureBox.Visible = false;
        viewerTextBox.Visible = false;
        _delimitedGrid ??= CreateDelimitedGrid();
        _delimitedGrid.Columns.Clear();
        for (int i = 0; i < table.Headers.Count; i++)
        {
            _delimitedGrid.Columns.Add($"c{i}", table.Headers[i]);
        }
        foreach (IReadOnlyList<string> row in table.Rows)
        {
            _delimitedGrid.Rows.Add(row.Take(table.Headers.Count).Cast<object>().ToArray());
        }
        _delimitedGrid.Visible = true;
        _delimitedGrid.BringToFront();
        if (_delimitedGrid.Rows.Count > 0 && _delimitedGrid.Columns.Count > 0)
        {
            _delimitedGrid.CurrentCell = _delimitedGrid[0, 0];
            _delimitedGrid.Focus();
        }
        ApplyViewerStatusLine("CSV/TSV grid preview applied");
    }

    void IViewerPreviewUiPort.ApplySqlitePreview(string text)
    {
        ApplyViewerChromeState();
        Presentation.PreviewUiPresenter.ApplyPlainTextContent(
            viewerTextBox,
            viewerMessageLabel,
            viewerPictureBox,
            text);
        ApplyViewerStatusLine("SQLite preview applied");
    }

    void IViewerPreviewUiPort.ApplyBinaryPreview(string text)
    {
        ApplyViewerChromeState();
        Presentation.PreviewUiPresenter.ApplyPlainTextContent(
            viewerTextBox,
            viewerMessageLabel,
            viewerPictureBox,
            text);
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer)
        {
            NormalizeStatusLabelLayout();
            ApplyViewerStatusLine();
        }
    }

    void IViewerPreviewUiPort.BeginLargeTextPreview(LargeFilePreviewState state)
    {
        ApplyViewerChromeState();
        viewerPictureBox.Visible = false;
        viewerTextBox.Visible = false;
        viewerMessageLabel.Text = "LargeText 読み込み中...";
        viewerMessageLabel.Visible = true;
        _largeFileControl.ResetFirstContentPaintMarker();
        _largeTextEntryStopwatch.Restart();
        ApplyViewerStatusLine("LargeText loading ui shown");
    }

    async Task IViewerPreviewUiPort.ApplyLargeTextInitialDisplayAsync(
        LargeFilePreviewState state,
        int requestId,
        CancellationToken token)
    {
        if (!IsLatestPreviewRequest(requestId, state.FilePath, token) || _viewerApplicationCoordinator.Mode != ViewerApplicationMode.Viewer)
        {
            return;
        }

        _largeFileControl.SetState(state, state.DetectedEncoding);
        ApplyViewerStatusLine("LargeText SetState applied");
        LogViewerLayoutBounds("LargeText after SetState");
        int navigationRequestId = ++state.NavigationRequestId;
        await UpdateLargeFileVirtualDisplayAsync(requestId, navigationRequestId, token);
        if (IsCurrentLargeFileNavigationRequest(state, navigationRequestId))
        {
            ApplyViewerStatusLine("LargeText initial first paint ready");
            statusStrip.Invalidate();
            statusStrip.Update();
            _largeFileControl.Invalidate();
            _largeFileControl.Update();
        }
        BeginInvoke(new Action(async () =>
        {
            if (!IsLargeTextStatusApplyTarget(state)) return;
            await Task.Delay(150);
            if (!IsLargeTextStatusApplyTarget(state)) return;
            StartLargeTextFullIndexAsync(
                state,
                requestId,
                Stopwatch.StartNew(),
                state.FilePath,
                PreviewKind.LargeText,
                token);
        }));
    }
}
