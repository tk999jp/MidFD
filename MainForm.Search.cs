using MidFD.Runtime;
using MidFD.Commands;
using MidFD.Dialogs;
using MidFD.Models;
using MidFD.Services;
using MidFD.Configuration;
using MidFD.Helpers;
using System.Threading;

namespace MidFD;

public partial class MainForm
{
    private sealed class PendingUnifiedSearchHit
    {
        public required string FullPath { get; init; }
        public required UnifiedSearchMatch Match { get; init; }
        public required UnifiedSearchResultsView ResultsView { get; init; }
        public int RequestId { get; set; } = -1;
    }

    private enum UnifiedSearchActivationReturnKind
    {
        ReusableNameInspection,
        AdditionalNameInspection,
        ReusableGrepPreview,
        AdditionalGrepPreview
    }

    private sealed record UnifiedSearchPreviewReturnContext(Guid SearchSessionId)
    {
        public string SourceCategoryId { get; init; } = string.Empty;
        public Guid SourceTabId { get; init; }
        public Guid ResultTabId { get; init; }
        public UnifiedSearchActivationReturnKind Kind { get; init; }
    }

    private sealed class UnifiedSearchNameInspectionReturnContext
    {
        public Guid SearchSessionId { get; init; }
        public string SourceCategoryId { get; init; } = string.Empty;
        public Guid SourceTabId { get; init; }
        public Guid ResultTabId { get; init; }
        public UnifiedSearchActivationReturnKind Kind { get; init; }
    }

    private sealed class UnifiedSearchSession
    {
        public required Guid Id { get; init; }
        public required string SourceCategoryId { get; init; }
        public required Guid SourceTabId { get; init; }
        public required string RootPath { get; init; }
        public required UnifiedFilterFindCriteria Criteria { get; init; }
        public required UnifiedFilterFindMode Mode { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required UnifiedSearchResultsView View { get; init; }
        public UnifiedFilterFindSearchResult? Result { get; set; }
        public Guid? ReusableInspectionTabId { get; set; }
        public List<Guid> AdditionalResultTabIds { get; } = [];
        public UnifiedSearchProgress? LatestProgress;
        public Task? SearchTask { get; set; }
        public int ProgressUpdateScheduled;
        public bool IsActive { get; set; }
        public bool IsFinished { get; set; }
        public ContentSearchBatchDispatcher? Batches { get; set; }
    }

    private UnifiedSearchSession? _unifiedSearchSession;
    private PendingUnifiedSearchHit? _pendingUnifiedSearchHit;
    private UnifiedSearchPreviewReturnContext? _unifiedSearchPreviewReturnContext;
    private UnifiedSearchNameInspectionReturnContext? _unifiedSearchNameInspectionReturnContext;
    private readonly List<BrowserTabNavigationSearchDerivedTab> _searchDerivedBrowserTabLineages = [];
    private long _searchDerivedBrowserTabCreationOrder;

    private async Task RunUnifiedFilterFindAsync(UnifiedFilterFindInteractionRequest request)
    {
        UnifiedFilterFindMode mode = UnifiedFilterFindDialog.ResolveEffectiveMode(
            request.InitialMode == UnifiedFilterFindMode.FilterCurrentTab,
            request.CurrentCriteria.ContentPattern);
        if (mode == UnifiedFilterFindMode.FilterCurrentTab) return;

        ClearUnifiedSearchPreviewReturnContext();
        ClearNameSearchInspectionReturnContext();
        if (_unifiedSearchSession is { } previousSession)
            DetachUnifiedSearchSession(previousSession);
        string categoryId = _browserApplicationCoordinator.Workspace.ActiveCategoryId;
        int sourceTabIndex = request.TargetTabIndex;
        if (sourceTabIndex < 0 || sourceTabIndex >= _browserApplicationCoordinator.Workspace.TabCount)
        {
            ShowStatusMessage("検索元のBrowserタブを取得できませんでした。");
            return;
        }
        BrowserTabState source = _browserApplicationCoordinator.Workspace.TabStates[sourceTabIndex];
        Guid sessionId = Guid.NewGuid();

        var session = new UnifiedSearchSession
        {
            Id = sessionId,
            SourceCategoryId = categoryId,
            SourceTabId = source.Id,
            RootPath = request.RootPath,
            Criteria = request.CurrentCriteria.Clone(),
            Mode = mode,
            Cancellation = new CancellationTokenSource(),
            View = new UnifiedSearchResultsView(
                request.RootPath,
                mode,
                request.CurrentCriteria,
                (path, directory, hit) => ExecuteSearchResultActivation(path, directory, hit, false, null, sessionId),
                snapshot => ExecuteUnifiedSearchExport(snapshot),
                CreateUnifiedSearchResultAppearance,
                (path, directory, hit) => ExecuteSearchResultActivation(path, directory, hit, true, null, sessionId))
        };
        session.View.CloseRequested += (_, _) => CloseUnifiedSearchSession(restoreSource: true);
        _unifiedSearchSession = session;
        mainAreaPanel.Controls.Add(session.View);
        ActivateUnifiedSearchSession(session, focusResults: true);
        RefreshBrowserTabHeaders();
        UpdateFunctionBar();

        session.SearchTask = RunUnifiedSearchWorkerAsync(session);
        await session.SearchTask;
    }

    private async Task RunUnifiedSearchWorkerAsync(UnifiedSearchSession session)
    {
        try
        {
            session.Batches = new ContentSearchBatchDispatcher(
                () => ReferenceEquals(Volatile.Read(ref _unifiedSearchSession), session) && !session.IsFinished &&
                    !session.Cancellation.IsCancellationRequested && !session.View.IsDisposed && !IsDisposed && !Disposing,
                action => BeginInvoke((MethodInvoker)(() => action())),
                batch => { session.View.AppendBatch(batch); if (session.IsActive) UpdateFunctionBar(); });
            Action<UnifiedSearchProgress> report = progress => QueueUnifiedSearchProgress(session, progress);
            UnifiedFilterFindSearchResult result = session.Mode == UnifiedFilterFindMode.FindNamesRecursively
                ? await Task.Run(
                    () => UnifiedFilterFindService.FindNames(session.RootPath, session.Criteria, session.Cancellation.Token, report),
                    session.Cancellation.Token)
                : await UnifiedFilterFindService.SearchContentsAsync(
                    session.RootPath, session.Criteria, session.Cancellation.Token, report, session.Batches.Queue,
                    _integrationSeam?.ContentSearchEngine,
                    info => BeginInvoke((MethodInvoker)(() =>
                    {
                        if (ReferenceEquals(_unifiedSearchSession, session) && !session.Cancellation.IsCancellationRequested
                            && !session.View.IsDisposed) session.View.UpdateBackend(info);
                    })));
            if (!ReferenceEquals(_unifiedSearchSession, session) || IsDisposed || Disposing) return;
            session.Batches.Drain();
            session.Result = result;
            session.IsFinished = true;
            session.View.Complete(result);
            session.LatestProgress = new UnifiedSearchProgress(
                result.Mode == UnifiedFilterFindMode.FindNamesRecursively ? result.ResultCount : result.FileCount,
                result.ResultCount,
                result.Mode == UnifiedFilterFindMode.FindNamesRecursively ? 0 : result.FileCount,
                result.MatchCount,
                result.SkippedCount,
                session.RootPath);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_unifiedSearchSession, session))
            {
                session.IsFinished = true;
                session.View.CompleteCanceled();
            }
        }
        catch (Exception ex)
        {
            LogService.Error("[UnifiedFilterFind] search failed.", ex);
            if (ReferenceEquals(_unifiedSearchSession, session))
            {
                session.IsFinished = true;
                session.View.CompleteFailed(ex.Message);
            }
        }
        finally
        {
            session.IsFinished = true;
            session.Cancellation.Dispose();
            session.SearchTask = null;
            if (ReferenceEquals(_unifiedSearchSession, session) && !IsDisposed && !Disposing)
            {
                RefreshBrowserTabHeaders();
                UpdateFunctionBar();
            }
        }
    }

    private void QueueUnifiedSearchProgress(UnifiedSearchSession session, UnifiedSearchProgress progress)
    {
        if (!ReferenceEquals(Volatile.Read(ref _unifiedSearchSession), session)) return;
        Interlocked.Exchange(ref session.LatestProgress, progress);
        if (Interlocked.Exchange(ref session.ProgressUpdateScheduled, 1) != 0) return;
        try
        {
            BeginInvoke((MethodInvoker)(() => ApplyQueuedUnifiedSearchProgress(session)));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref session.ProgressUpdateScheduled, 0);
        }
    }

    private void ApplyQueuedUnifiedSearchProgress(UnifiedSearchSession session)
    {
        Interlocked.Exchange(ref session.ProgressUpdateScheduled, 0);
        if (!ReferenceEquals(_unifiedSearchSession, session) || session.View.IsDisposed || session.IsFinished) return;
        UnifiedSearchProgress? progress = Interlocked.Exchange(ref session.LatestProgress, null);
        if (progress != null) session.View.UpdateProgress(progress);
        if (Volatile.Read(ref session.LatestProgress) is { } pending) QueueUnifiedSearchProgress(session, pending);
    }

    private string? ExecuteUnifiedSearchExport(UnifiedFilterFindSearchResult snapshot)
    {
        string? output = UnifiedSearchResultOutputService.Write(snapshot);
        if (output == null) return "出力する検索結果がありません。";
        var launched = ConfiguredEditorService.Open(output, _settingsCoordinator.Value.ExternalTools?.ExternalEditorPath);
        return launched.Succeeded ? launched.Message : $"一覧出力を開けませんでした: {launched.Message}";
    }

    private void ActivateUnifiedSearchSession(UnifiedSearchSession session, bool focusResults)
    {
        if (!ReferenceEquals(_unifiedSearchSession, session)) return;
        if (!TryEnsureUnifiedSearchSource(session)) return;
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer)
        {
            SwitchUIMode(ViewerApplicationMode.Browser);
            ClearUnifiedSearchPreviewReturnContext();
        }
        if (!SwitchToUnifiedSearchSourceBrowserTab(session)) return;
        session.IsActive = true;
        session.View.Visible = true;
        session.View.BringToFront();
        RefreshBrowserTabHeaders();
        UpdateFunctionBar();
        if (focusResults && session.View.ResultList.CanFocus) session.View.ResultList.Focus();
    }

    private void DeactivateUnifiedSearchSurface()
    {
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null || !session.IsActive) return;
        session.IsActive = false;
        session.View.Visible = false;
        RefreshBrowserTabHeaders();
        UpdateFunctionBar();
    }

    private bool TryEnsureUnifiedSearchSource(UnifiedSearchSession session)
    {
        bool exists = HasUnifiedSearchSource(session);
        if (exists) return true;
        if (ReferenceEquals(_unifiedSearchSession, session))
            CloseUnifiedSearchSession(restoreSource: false, refreshProjection: true);
        ShowStatusMessage("検索元のBrowserタブが閉じられたため、検索結果を閉じました。");
        return false;
    }

    private bool HasUnifiedSearchSource(UnifiedSearchSession session)
    {
        BrowserTabCategoryDefinition? category = _browserApplicationCoordinator.Workspace.CategoryStates
            .FirstOrDefault(item => string.Equals(item.Id, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase));
        return category != null && GetBrowserTabContextStates(category.Id).Any(tab => tab.Id == session.SourceTabId);
    }

    private int FindBrowserTabCategoryIndex(string categoryId)
    {
        for (int index = 0; index < _browserApplicationCoordinator.Workspace.CategoryStates.Count; index++)
        {
            if (string.Equals(_browserApplicationCoordinator.Workspace.CategoryStates[index].Id, categoryId, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return -1;
    }

    private bool OpenUnifiedSearchTab(Guid sessionId)
    {
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null || session.Id != sessionId) return false;
        ClearNameSearchInspectionReturnContext();
        if (!TryEnsureUnifiedSearchSource(session) || !SwitchToUnifiedSearchSourceBrowserTab(session)) return false;
        ActivateUnifiedSearchSession(session, focusResults: true);
        return true;
    }

    private bool ReopenUnifiedSearchDialog(UnifiedSearchSession session)
    {
        if (!ReferenceEquals(_unifiedSearchSession, session)
            || !TryEnsureUnifiedSearchSource(session)
            || !SwitchToUnifiedSearchSourceBrowserTab(session)) return false;

        int sourceTabIndex = GetBrowserTabIndexById(
            _browserApplicationCoordinator.Workspace.TabStates,
            session.SourceTabId);
        if (sourceTabIndex < 0) return false;

        var request = new UnifiedFilterFindInteractionRequest(
            sourceTabIndex,
            session.RootPath,
            session.Criteria.Clone(),
            session.Mode,
            PreserveSearchDraft: true);
        UnifiedFilterFindDialogResult? selected = UnifiedFilterFindDialog.Show(this, request);
        if (selected == null) return true;

        var interaction = new UnifiedFilterFindCommandInteractionRequest(
            sourceTabIndex,
            session.RootPath,
            session.Criteria.Clone(),
            session.Mode);
        var response = new UnifiedFilterFindCommandInteractionResponse(
            true,
            selected.Mode,
            selected.Criteria);
        return ApplyCommandApplicationResult(_commandApplicationCoordinator.Continue(interaction, response));
    }

    private bool SwitchToUnifiedSearchSourceBrowserTab(UnifiedSearchSession session)
    {
        if (!TryEnsureUnifiedSearchSource(session)) return false;
        if (!string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<BrowserTabState> sourceTabs = GetBrowserTabContextStates(session.SourceCategoryId);
            int sourceIndex = GetBrowserTabIndexById(sourceTabs, session.SourceTabId);
            if (sourceIndex < 0) return false;
            SwitchBrowserTabCategory(session.SourceCategoryId, sourceIndex);
        }
        else
        {
            int sourceIndex = GetBrowserTabIndexById(_browserApplicationCoordinator.Workspace.TabStates, session.SourceTabId);
            if (sourceIndex < 0) return false;
            if (sourceIndex != _browserApplicationCoordinator.Workspace.ActiveTabIndex && !SwitchBrowserTab(sourceIndex))
                return false;
        }

        return string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
            && _browserApplicationCoordinator.Workspace.ActiveTabSnapshot?.Id == session.SourceTabId;
    }

    private bool NavigateAdjacentBrowserTabIncludingSearch(int delta)
    {
        if (GuardClipboardBusy()) return false;
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null || !string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase)) return false;
        int browserCount = _browserApplicationCoordinator.Workspace.TabCount;
        if (browserCount <= 0) return false;
        int sourceIndex = GetBrowserTabIndexById(_browserApplicationCoordinator.Workspace.TabStates, session.SourceTabId);
        if (sourceIndex < 0) return false;
        int searchIndex = sourceIndex + 1;
        int current = session.IsActive
            ? searchIndex
            : _browserApplicationCoordinator.Workspace.ActiveTabIndex <= sourceIndex
                ? _browserApplicationCoordinator.Workspace.ActiveTabIndex
                : _browserApplicationCoordinator.Workspace.ActiveTabIndex + 1;
        int next = (current + delta) % (browserCount + 1);
        if (next < 0) next += browserCount + 1;
        if (next == searchIndex) return OpenUnifiedSearchTab(session.Id);
        if (session.IsActive) DeactivateUnifiedSearchSurface();
        return SwitchBrowserTab(next < searchIndex ? next : next - 1);
    }

    private void CloseUnifiedSearchSession(bool restoreSource, bool refreshProjection = true)
    {
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null) return;
        DetachUnifiedSearchSession(session);

        if (restoreSource && !IsDisposed && !Disposing)
        {
            BrowserTabCategoryDefinition? category = _browserApplicationCoordinator.Workspace.CategoryStates
                .FirstOrDefault(item => string.Equals(item.Id, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<BrowserTabState> tabs = category == null ? [] : GetBrowserTabContextStates(category.Id);
            int sourceIndex = GetBrowserTabIndexById(tabs, session.SourceTabId);
            if (category != null && sourceIndex >= 0)
            {
                if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer) SwitchUIMode(ViewerApplicationMode.Browser);
                if (!string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase))
                    SwitchBrowserTabCategory(session.SourceCategoryId, sourceIndex);
                else
                    SwitchBrowserTab(sourceIndex);
                FocusBrowserFileList(force: true);
            }
        }
        if (refreshProjection && !IsDisposed && !Disposing) RefreshBrowserTabHeaders();
        UpdateFunctionBar();
    }

    private void DetachUnifiedSearchSession(UnifiedSearchSession session)
    {
        if (ReferenceEquals(_unifiedSearchSession, session)) _unifiedSearchSession = null;
        ClearUnifiedSearchPreviewReturnContext(session.Id);
        if (_pendingUnifiedSearchHit is { } pending && ReferenceEquals(pending.ResultsView, session.View))
            _pendingUnifiedSearchHit = null;
        session.IsActive = false;
        session.View.Visible = false;
        ClearNameSearchInspectionReturnContext(session.Id);
        if (!session.View.IsDisposed)
        {
            mainAreaPanel.Controls.Remove(session.View);
            session.View.Dispose();
        }
        if (!session.Cancellation.IsCancellationRequested)
        {
            try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (session.SearchTask == null || session.SearchTask.IsCompleted)
        {
            try { session.Cancellation.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private static int GetBrowserTabIndexById(IReadOnlyList<BrowserTabState> tabs, Guid tabId)
    {
        for (int i = 0; i < tabs.Count; i++) if (tabs[i].Id == tabId) return i;
        return -1;
    }

    private BrowserTabStripItem CreateUnifiedSearchTabItem(UnifiedSearchSession session)
    {
        string label = session.IsFinished ? "検索結果" : "検索中...";
        string query = string.IsNullOrWhiteSpace(session.Criteria.ContentPattern)
            ? session.Criteria.NamePattern
            : session.Criteria.ContentPattern;
        string tooltip = $"{(session.Mode == UnifiedFilterFindMode.FindNamesRecursively ? "名前検索" : "内容検索")}: {query}\n{session.RootPath}";
        return new BrowserTabStripItem(
            label,
            tooltip,
            Kind: BrowserTabStripItemKind.UnifiedSearch,
            RuntimeIdentity: session.Id.ToString("N"),
            SourceTabId: session.SourceTabId);
    }

    private BrowserTabNavigationSearchChild? CreateUnifiedSearchNavigationChildItem()
    {
        if (_unifiedSearchSession is not { } session) return null;
        BrowserTabStripItem item = CreateUnifiedSearchTabItem(session);
        return new BrowserTabNavigationSearchChild(
            session.SourceCategoryId,
            session.Id,
            session.SourceTabId,
            item.Text,
            item.ToolTipText);
    }

    private void ClearUnifiedSearchPreviewReturnContext(Guid? searchSessionId = null)
    {
        if (_unifiedSearchPreviewReturnContext is not { } context) return;
        if (searchSessionId is { } requestedId && context.SearchSessionId != requestedId) return;
        _unifiedSearchPreviewReturnContext = null;
    }

    private void ReturnToUnifiedSearchAfterViewerExit()
    {
        UnifiedSearchPreviewReturnContext? context = _unifiedSearchPreviewReturnContext;
        if (context == null) return;
        ClearUnifiedSearchPreviewReturnContext(context.SearchSessionId);
        UnifiedSearchSession? session = _unifiedSearchSession;
        bool resultTabIsActive = session is { } activeSession
            && activeSession.Id == context.SearchSessionId
            && string.Equals(activeSession.SourceCategoryId, context.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
            && activeSession.SourceTabId == context.SourceTabId
            && string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, context.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
            && _browserApplicationCoordinator.Workspace.ActiveTabSnapshot?.Id == context.ResultTabId;
        if (resultTabIsActive) OpenUnifiedSearchTab(context.SearchSessionId);
    }

    private void ClearNameSearchInspectionReturnContext(Guid? searchSessionId = null)
    {
        if (_unifiedSearchNameInspectionReturnContext is not { } context) return;
        if (searchSessionId is { } requestedId && context.SearchSessionId != requestedId) return;
        _unifiedSearchNameInspectionReturnContext = null;
    }

    private void ClearNameSearchInspectionReturnContextUnlessSource(Guid targetTabId, string targetCategoryId)
    {
        if (_unifiedSearchNameInspectionReturnContext is not { } context) return;
        if (context.ResultTabId == targetTabId
            || (context.SourceTabId == targetTabId
                && string.Equals(context.SourceCategoryId, targetCategoryId, StringComparison.OrdinalIgnoreCase))) return;
        ClearNameSearchInspectionReturnContext(context.SearchSessionId);
    }

    private bool TryReturnToNameSearchResultsAfterInspection()
    {
        if (_unifiedSearchNameInspectionReturnContext is not { } context) return false;
        UnifiedSearchSession? session = _unifiedSearchSession;
        bool sourceIsActive = session is { } activeSession
            && activeSession.Id == context.SearchSessionId
            && string.Equals(activeSession.SourceCategoryId, context.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
            && activeSession.SourceTabId == context.SourceTabId
            && TryEnsureUnifiedSearchSource(activeSession)
            && string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, context.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
            && _browserApplicationCoordinator.Workspace.ActiveTabSnapshot?.Id == context.ResultTabId;
        ClearNameSearchInspectionReturnContext(context.SearchSessionId);
        return sourceIsActive && OpenUnifiedSearchTab(context.SearchSessionId);
    }

    private void RequestSearchActivationForeground(bool preview)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (!preview)
        {
            FocusBrowserFileList(force: true);
            return;
        }
        BeginInvoke(new Action(FocusSearchPreviewSurface));
    }

    private void FocusSearchPreviewSurface()
    {
        if (IsDisposed || Disposing) return;
        Control? target = _viewerApplicationCoordinator.CurrentKind switch
        {
            PreviewKind.LargeText => _largeFileControl,
            PreviewKind.CsvTsv => _delimitedGrid,
            PreviewKind.Markdown when _markdownBrowser?.Visible == true => _markdownBrowser,
            _ when viewerTextBox.Visible => viewerTextBox,
            _ when viewerPictureBox.Visible => viewerPictureBox,
            _ => null
        };
        if (target is { CanFocus: true }) target.Focus();
    }

    private UnifiedSearchResultAppearance CreateUnifiedSearchResultAppearance()
    {
        var settings = _settingsCoordinator.Value;
        FileListColorResolver.ResolvedColors colors = FileListColorResolver.ResolveColors(settings);
        var theme = UiThemeResolver.Resolve(settings.Appearance);
        return new UnifiedSearchResultAppearance(
            colors.Background, colors.NormalFile, colors.Directory, colors.SelectedBackground,
            colors.SelectedForeground, theme.HeaderBackColor, theme.HeaderForeColor,
            theme.BorderColor, fileListView?.Font);
    }

    internal string? ExecuteSearchResultActivation(
        string path,
        bool directory,
        UnifiedSearchMatch? hit,
        bool inNewTab = false,
        UnifiedSearchResultsView? resultView = null,
        Guid? expectedSearchSessionId = null)
    {
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null)
            return "検索sessionが終了しました。検索結果を開き直してください。";
        if (expectedSearchSessionId is { } expectedId && session?.Id != expectedId)
            return "検索sessionが置き換わりました。検索結果を開き直してください。";
        if (resultView != null && !ReferenceEquals(resultView, session.View))
            return "検索結果が更新されました。検索結果を開き直してください。";
        if (!TryEnsureUnifiedSearchSource(session)) return "検索元のBrowserタブは利用できません。";
        ClearNameSearchInspectionReturnContext();
        ClearUnifiedSearchPreviewReturnContext();
        _pendingUnifiedSearchHit = null;
        SwitchUIMode(ViewerApplicationMode.Browser);
        if (!TryResolveSearchResultBrowserTab(session, inNewTab, out Guid resultTabId, out string? targetError))
            return targetError;

        string? error = UnifiedSearchResultActivationService.Activate(path, directory, hit != null,
            (parent, name) =>
            {
                return ExecuteConfirmedUserDirectoryNavigation(parent, name);
            },
            () => GetCurrentBrowserItem()?.Tag as string,
            () =>
            {
                if (hit == null) return false;
                UnifiedSearchSession? activeSession = _unifiedSearchSession;
                if (activeSession == null) return false;
                _pendingUnifiedSearchHit = new PendingUnifiedSearchHit
                {
                    FullPath = hit.FullPath,
                    Match = hit,
                    ResultsView = activeSession.View
                };
                bool started = ExecuteCommandFromUi(CommandIds.BrowserPreview, CommandScope.Browser, "SearchResults.Preview");
                if (started)
                {
                    _pendingUnifiedSearchHit.RequestId = _viewerApplicationCoordinator.ActiveRequestId;
                    if (ReferenceEquals(_unifiedSearchSession, activeSession)
                        && activeSession.Id == session.Id
                        && activeSession.Mode == UnifiedFilterFindMode.SearchContentsRecursively)
                    {
                        _unifiedSearchPreviewReturnContext = new UnifiedSearchPreviewReturnContext(activeSession.Id)
                        {
                            SourceCategoryId = activeSession.SourceCategoryId,
                            SourceTabId = activeSession.SourceTabId,
                            ResultTabId = resultTabId,
                            Kind = inNewTab
                                ? UnifiedSearchActivationReturnKind.AdditionalGrepPreview
                                : UnifiedSearchActivationReturnKind.ReusableGrepPreview
                        };
                    }
                }
                else
                {
                    _pendingUnifiedSearchHit = null;
                    ClearUnifiedSearchPreviewReturnContext(activeSession.Id);
                }
                return started;
            },
            RequestSearchActivationForeground);
        if (error == null && session.Mode == UnifiedFilterFindMode.FindNamesRecursively && hit == null)
        {
            _unifiedSearchNameInspectionReturnContext = new UnifiedSearchNameInspectionReturnContext
            {
                SearchSessionId = session.Id,
                SourceCategoryId = session.SourceCategoryId,
                SourceTabId = session.SourceTabId,
                ResultTabId = resultTabId,
                Kind = inNewTab
                    ? UnifiedSearchActivationReturnKind.AdditionalNameInspection
                    : UnifiedSearchActivationReturnKind.ReusableNameInspection
            };
        }
        return error;
    }

    private bool TryResolveSearchResultBrowserTab(
        UnifiedSearchSession session,
        bool inNewTab,
        out Guid resultTabId,
        out string? error)
    {
        resultTabId = Guid.Empty;
        error = null;
        DeactivateUnifiedSearchSurface();
        if (!SwitchToUnifiedSearchSourceBrowserTab(session))
        {
            error = "検索元のBrowserタブは利用できません。";
            return false;
        }

        IReadOnlyList<BrowserTabState> tabs = _browserApplicationCoordinator.Workspace.TabStates;
        if (!inNewTab && session.ReusableInspectionTabId is { } reusableId)
        {
            int reusableIndex = GetBrowserTabIndexById(tabs, reusableId);
            if (reusableIndex >= 0)
            {
                if (reusableIndex != _browserApplicationCoordinator.Workspace.ActiveTabIndex
                    && !SwitchBrowserTab(reusableIndex))
                {
                    error = "確認用Browserタブへ切り替えられませんでした。";
                    return false;
                }
                resultTabId = reusableId;
                RefreshBrowserTabHeaders();
                return true;
            }
            session.ReusableInspectionTabId = null;
        }

        session.AdditionalResultTabIds.RemoveAll(id => GetBrowserTabIndexById(tabs, id) < 0);
        Guid anchorTabId = inNewTab ? session.AdditionalResultTabIds.LastOrDefault() : Guid.Empty;
        if (anchorTabId == Guid.Empty && inNewTab
            && session.ReusableInspectionTabId is { } existingInspection
            && GetBrowserTabIndexById(tabs, existingInspection) >= 0)
        {
            anchorTabId = existingInspection;
        }
        if (anchorTabId == Guid.Empty)
        {
            anchorTabId = _searchDerivedBrowserTabLineages
                .Where(item => string.Equals(item.SourceCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
                    && item.SourceTabId == session.SourceTabId
                    && GetBrowserTabIndexById(tabs, item.ResultTabId) >= 0)
                .OrderByDescending(item => item.CreationOrder)
                .Select(item => item.ResultTabId)
                .FirstOrDefault();
        }
        if (anchorTabId == Guid.Empty) anchorTabId = session.SourceTabId;

        int anchorIndex = GetBrowserTabIndexById(tabs, anchorTabId);
        if (anchorIndex < 0)
        {
            error = "検索結果タブの挿入位置を取得できませんでした。";
            return false;
        }
        if (anchorIndex != _browserApplicationCoordinator.Workspace.ActiveTabIndex
            && !SwitchBrowserTab(anchorIndex))
        {
            error = "検索結果タブの挿入先へ切り替えられませんでした。";
            return false;
        }

        if (!ExecuteCommandFromUi(CommandIds.BrowserTabNew, CommandScope.Browser, "SearchResults.CreateNormalTab"))
        {
            ActivateUnifiedSearchSession(session, focusResults: true);
            error = "結果確認用のBrowserタブを作成できませんでした。";
            return false;
        }

        BrowserTabState? createdTab = _browserApplicationCoordinator.Workspace.ActiveTabSnapshot;
        if (createdTab == null)
        {
            ActivateUnifiedSearchSession(session, focusResults: true);
            error = "作成したBrowserタブを取得できませんでした。";
            return false;
        }
        resultTabId = createdTab.Id;
        _searchDerivedBrowserTabLineages.Add(new BrowserTabNavigationSearchDerivedTab(
            resultTabId,
            session.SourceCategoryId,
            session.SourceTabId,
            ++_searchDerivedBrowserTabCreationOrder));
        if (inNewTab) session.AdditionalResultTabIds.Add(resultTabId);
        else session.ReusableInspectionTabId = resultTabId;

        int currentAnchorIndex = GetBrowserTabIndexById(_browserApplicationCoordinator.Workspace.TabStates, anchorTabId);
        int createdIndex = GetBrowserTabIndexById(_browserApplicationCoordinator.Workspace.TabStates, resultTabId);
        int desiredIndex = currentAnchorIndex + 1;
        if (currentAnchorIndex < 0 || createdIndex < 0)
        {
            error = "検索結果タブの配置を確定できませんでした。";
            return false;
        }
        if (createdIndex != desiredIndex)
        {
            if (!_browserTabWorkflowApplicationCoordinator.ReorderTab(
                    createdIndex,
                    desiredIndex,
                    BuildBrowserTabStateFromCurrentUi(validateMarks: true)))
            {
                error = "検索結果タブを検索元の後方へ配置できませんでした。";
                return false;
            }
        }

        if (_browserApplicationCoordinator.Workspace.ActiveTabSnapshot?.Id != resultTabId)
        {
            ActivateUnifiedSearchSession(session, focusResults: true);
            error = "作成した検索結果タブが有効になりませんでした。";
            return false;
        }
        RefreshBrowserTabHeaders();
        return true;
    }

    private bool IsSearchHitPreviewPending(string path)
        => _pendingUnifiedSearchHit is { } pending
            && pending.RequestId == _viewerApplicationCoordinator.ActiveRequestId
            && string.Equals(pending.FullPath, path, StringComparison.OrdinalIgnoreCase);

    private bool ApplySearchHitToTextPreview(string text)
    {
        if (_pendingUnifiedSearchHit is not { } pending
            || !IsSearchHitPreviewPending(pending.FullPath)
            || !SearchHitTextLocationHelper.TryGetSelection(text, pending.Match.LineNumber, pending.Match.ColumnNumber,
                pending.Match.MatchText.Length, out int start, out int length))
        {
            if (_pendingUnifiedSearchHit is { } unavailable && unavailable.RequestId == _viewerApplicationCoordinator.ActiveRequestId)
            {
                unavailable.ResultsView.SetActionStatus("プレビューを開きましたが、ヒット位置を表示できませんでした。");
                _pendingUnifiedSearchHit = null;
            }
            return false;
        }
        viewerTextBox.Select(start, length);
        viewerTextBox.ScrollToCaret();
        RequestSearchActivationForeground(preview: true);
        viewerTextBox.Focus();
        pending.ResultsView.SetActionStatus($"{pending.Match.LineNumber:N0} 行目の検索箇所を表示しました。");
        _pendingUnifiedSearchHit = null;
        return true;
    }

    private bool IsSearchHitPreviewPendingForRequest(int requestId, string path)
        => IsSearchHitPreviewPending(path) && _pendingUnifiedSearchHit!.RequestId == requestId;

    private async Task<bool> ApplySearchHitToLargeTextPreviewAsync(LargeFilePreviewState state, int requestId, int indexedLineCount)
    {
        if (_pendingUnifiedSearchHit is not { } pending || pending.RequestId != requestId
            || !string.Equals(pending.Match.FullPath, state.FilePath, StringComparison.OrdinalIgnoreCase)) return false;
        if (!SearchHitTextLocationHelper.TryGetLargeTextLine(pending.Match.LineNumber, indexedLineCount, out int zeroBasedLine))
        {
            pending.ResultsView.SetActionStatus("LargeTextプレビューにヒット行がありません。");
            _pendingUnifiedSearchHit = null;
            return false;
        }
        int zeroBasedColumn = Math.Max(0, pending.Match.ColumnNumber - 1);
        int matchLength = pending.Match.MatchText.Length;
        _viewerWorkflowApplicationCoordinator.SetLargeFileSearchHit(state, zeroBasedLine, zeroBasedColumn, matchLength);
        _largeFileControl.SetActiveSearchHit(zeroBasedLine, zeroBasedColumn, matchLength);
        int targetFirstLine = Math.Max(0, zeroBasedLine - Math.Max(1, _largeFileControl.VisibleLineCount / 2));
        await NavigateLargeFilePreviewAsync(targetFirstLine, "UnifiedSearchHit");
        if (_pendingUnifiedSearchHit != pending
            || !string.Equals(_viewerApplicationCoordinator.CurrentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase)) return false;
        _largeFileControl.SetActiveSearchHit(zeroBasedLine, zeroBasedColumn, matchLength);
        pending.ResultsView.SetActionStatus($"{pending.Match.LineNumber:N0} 行目の検索箇所を表示しました。");
        RequestSearchActivationForeground(preview: true);
        _largeFileControl.Focus();
        _pendingUnifiedSearchHit = null;
        return true;
    }

    bool IViewerPreviewUiPort.IsSearchHitPreviewPending(string path) => IsSearchHitPreviewPending(path);

    void IViewerPreviewUiPort.ApplyContentSearchPreviewCompletion(int requestId, string path, PreviewKind kind, string result)
    {
        if (!IsSearchHitPreviewPendingForRequest(requestId, path)) return;
        if (result == "Completed" && kind == PreviewKind.LargeText
            && _viewerApplicationCoordinator.LargeFileState is { IsBinaryLike: false, IsEncodingUnsupportedForLargeText: false }) return;
        string message = result == "Completed"
            ? $"{kind}プレビューではヒット行位置を表示できません。"
            : $"プレビューが{(result == "Canceled" ? "キャンセル" : "完了せず")}、ヒット行位置を表示できませんでした。";
        _pendingUnifiedSearchHit?.ResultsView.SetActionStatus(message);
        RequestSearchActivationForeground(preview: true);
        _pendingUnifiedSearchHit = null;
    }
}
