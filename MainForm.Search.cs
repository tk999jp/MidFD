using MidFD.Runtime;
using MidFD.Commands;
using MidFD.Dialogs;
using MidFD.Models;
using MidFD.Services;
using MidFD.Configuration;
using MidFD.Helpers;
using System.Diagnostics;
using System.Threading;

namespace MidFD;

public partial class MainForm
{
    private static readonly TimeSpan ContentProgressPublishInterval = TimeSpan.FromMilliseconds(250);

    private sealed class PendingUnifiedSearchHit
    {
        public required string FullPath { get; init; }
        public required UnifiedSearchMatch Match { get; init; }
        public required UnifiedSearchResultsView ResultsView { get; init; }
        public int RequestId { get; set; } = -1;
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
        public UnifiedSearchProgress? LatestProgress;
        public Task? SearchTask { get; set; }
        public int ProgressUpdateScheduled;
        public long ProgressCallbackScheduledAt;
        public System.Windows.Forms.Timer? ContentProgressTimer { get; set; }
        public EventHandler? ContentProgressTimerTickHandler { get; set; }
        public long LastContentProgressTimerTickAt;
        public int ContentProgressTimerTickActive;
        public bool IsActive { get; set; }
        public bool IsFinished { get; set; }
        public ContentSearchBatchDispatcher? Batches { get; set; }
        public ContentSearchBatchUiScheduler? BatchScheduler { get; set; }
        public long LastBatchDiagnosticTimestamp;
        public UnifiedSearchUiDiagnostics? UiDiagnostics { get; set; }
    }

    private UnifiedSearchSession? _unifiedSearchSession;
    private PendingUnifiedSearchHit? _pendingUnifiedSearchHit;

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
                (path, directory, hit, origin) => DispatchUnifiedSearchResultActivation(
                    CommandIds.BrowserSearchResultsActivate, path, directory, hit, origin, sessionId),
                snapshot =>
                {
                    _ = ExecuteCommandFromUi(CommandIds.BrowserSearchExportResults, new CommandExecutionContext
                    {
                        Scope = CommandScope.Browser,
                        Source = "UnifiedSearchResults.Export",
                        UnifiedSearchResultsExport = new UnifiedSearchResultsExportCommandContext(sessionId, snapshot)
                    });
                    return null;
                },
                CreateUnifiedSearchResultAppearance,
                (path, directory, hit, origin) => DispatchUnifiedSearchResultActivation(
                    CommandIds.BrowserSearchResultsActivateInNewTab, path, directory, hit, origin, sessionId),
                (path, hit) => DispatchUnifiedSearchHitInExternalEditor(path, hit, sessionId),
                () => ConfiguredEditorService.GetSearchHitMenuCaption(
                    _settingsCoordinator.Value.ExternalTools?.ExternalEditorSearchHitArgumentTemplate,
                    _settingsCoordinator.Value.ExternalTools?.ExternalEditorPath))
        };
        session.UiDiagnostics = mode == UnifiedFilterFindMode.SearchContentsRecursively
            ? UnifiedSearchUiDiagnostics.TryCreate(session.Id)
            : null;
        session.View.UiDiagnostics = session.UiDiagnostics;
        session.View.ResultFilterChanged += filterText =>
        {
            bool handled = ExecuteCommandFromUi(CommandIds.BrowserSearchResultsFilter, new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "UnifiedSearchResults.Filter",
                UnifiedSearchResultsFilter = new UnifiedSearchResultsFilterCommandContext(sessionId, filterText)
            });
            if (!handled && _unifiedSearchSession is { } currentSession &&
                currentSession.Id == sessionId && !currentSession.View.IsDisposed)
            {
                currentSession.View.SetActionStatus("検索結果の絞り込みを適用できませんでした。");
            }
        };
        session.View.CloseRequested += (_, _) =>
        {
            _ = ExecuteCommandFromUi(CommandIds.BrowserSearchCloseResults, new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "UnifiedSearchResults.Close",
                UnifiedSearchSession = new UnifiedSearchSessionCommandContext(sessionId)
            });
        };
        _unifiedSearchSession = session;
        _commandApplicationCoordinator.RegisterUnifiedSearchSession(
            session.Id,
            session.SourceCategoryId,
            session.SourceTabId,
            session.RootPath,
            session.Mode);
        mainAreaPanel.Controls.Add(session.View);
        ActivateUnifiedSearchSession(session, focusResults: true);
        RefreshBrowserTabHeaders();
        UpdateFunctionBar();

        session.SearchTask = RunUnifiedSearchWorkerAsync(session);
        await session.SearchTask;
    }

    private async Task RunUnifiedSearchWorkerAsync(UnifiedSearchSession session)
    {
        UnifiedFilterFindSearchResult? completedResult = null;
        string? failureMessage = null;
        bool canceled = false;
        string diagnosticTerminal = "failed";
        session.UiDiagnostics?.StartSession();
        try
        {
            session.BatchScheduler = new ContentSearchBatchUiScheduler(this);
            session.UiDiagnostics?.Start(this);
            StartContentProgressTimer(session);
            session.Batches = new ContentSearchBatchDispatcher(
                canAccept: () => ReferenceEquals(Volatile.Read(ref _unifiedSearchSession), session) && !session.IsFinished &&
                    !session.Cancellation.IsCancellationRequested && !session.View.IsDisposed && !IsDisposed && !Disposing,
                canApply: () => ReferenceEquals(Volatile.Read(ref _unifiedSearchSession), session) && !session.IsFinished &&
                    !session.View.IsDisposed && !IsDisposed && !Disposing,
                schedule: session.BatchScheduler.Schedule,
                apply: batch => { session.View.AppendBatch(batch); if (session.IsActive) UpdateFunctionBar(); },
                observeSlice: metrics => RecordUnifiedSearchBatchSlice(session, metrics),
                uiDiagnostics: session.UiDiagnostics);
            Action<UnifiedSearchProgress> report = progress => QueueUnifiedSearchProgress(session, progress);
            completedResult = session.Mode == UnifiedFilterFindMode.FindNamesRecursively
                ? await Task.Run(
                    () => UnifiedFilterFindService.FindNames(session.RootPath, session.Criteria, session.Cancellation.Token, report),
                    session.Cancellation.Token)
                : await UnifiedFilterFindService.SearchContentsWithDiagnosticsAsync(
                    session.RootPath, session.Criteria, session.Cancellation.Token, report, session.Batches.Queue,
                    _integrationSeam?.ContentSearchEngine,
                    info => BeginInvoke((MethodInvoker)(() =>
                    {
                        if (ReferenceEquals(_unifiedSearchSession, session) && !session.Cancellation.IsCancellationRequested
                            && !session.View.IsDisposed) session.View.UpdateBackend(info);
                    })),
                    session.UiDiagnostics);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            diagnosticTerminal = "canceled";
        }
        catch (Exception ex)
        {
            LogService.Error("[UnifiedFilterFind] search failed.", ex);
            failureMessage = ex.Message;
            diagnosticTerminal = "failed";
        }

        try
        {
            StopContentProgressTimer(session);
            if (!ReferenceEquals(_unifiedSearchSession, session) || IsDisposed || Disposing)
            {
                diagnosticTerminal = "detached";
                session.Batches?.Abandon();
                return;
            }

            if (session.Batches != null) await session.Batches.CompleteInputAsync();
            if (!ReferenceEquals(_unifiedSearchSession, session) || IsDisposed || Disposing)
            {
                diagnosticTerminal = "detached";
                session.Batches?.Abandon();
                return;
            }

            session.IsFinished = true;
            if (failureMessage != null)
            {
                session.View.CompleteFailed(failureMessage);
            }
            else if (canceled)
            {
                session.View.CompleteCanceled();
            }
            else if (completedResult != null)
            {
                session.Result = completedResult;
                session.View.Complete(completedResult);
                session.LatestProgress = new UnifiedSearchProgress(
                    completedResult.Mode == UnifiedFilterFindMode.FindNamesRecursively
                        ? completedResult.ResultCount
                        : completedResult.FileCount,
                    completedResult.ResultCount,
                    completedResult.Mode == UnifiedFilterFindMode.FindNamesRecursively ? 0 : completedResult.FileCount,
                    completedResult.MatchCount,
                    completedResult.SkippedCount,
                    session.RootPath);
            }
            else
            {
                failureMessage = "検索結果を完了できませんでした。";
                session.View.CompleteFailed(failureMessage);
            }
            diagnosticTerminal = failureMessage != null ? "failed"
                : canceled ? "canceled"
                : completedResult?.IsPartial == true ? "partial"
                : "completed";
        }
        catch (Exception ex)
        {
            LogService.Error("[UnifiedFilterFind] queued search result delivery failed.", ex);
            failureMessage = string.IsNullOrWhiteSpace(failureMessage)
                ? ex.Message
                : $"{failureMessage} / UIへの結果反映に失敗しました: {ex.Message}";
            if (ReferenceEquals(_unifiedSearchSession, session) && !session.View.IsDisposed && !IsDisposed && !Disposing)
            {
                session.IsFinished = true;
                session.View.CompleteFailed(failureMessage);
            }
            diagnosticTerminal = "failed";
        }
        finally
        {
            StopContentProgressTimer(session);
            session.UiDiagnostics?.Stop(diagnosticTerminal);
            session.View.UiDiagnostics = null;
            session.IsFinished = true;
            session.BatchScheduler?.Dispose();
            session.BatchScheduler = null;
            session.Cancellation.Dispose();
            session.SearchTask = null;
            if (ReferenceEquals(_unifiedSearchSession, session) && !IsDisposed && !Disposing)
            {
                RefreshBrowserTabHeaders();
                UpdateFunctionBar();
            }
        }
    }

    private static void RecordUnifiedSearchBatchSlice(
        UnifiedSearchSession session,
        ContentSearchBatchSliceMetrics metrics)
    {
        UnifiedSearchUiDiagnostics? diagnostics = session.UiDiagnostics;
        if (diagnostics == null) return;
        diagnostics.RecordBatch(metrics);
        if (metrics.ApplyDuration < TimeSpan.FromMilliseconds(50) &&
            metrics.OldestHitWait < TimeSpan.FromMilliseconds(100) &&
            metrics.PendingHitCount < 512)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref session.LastBatchDiagnosticTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(1)) return;
        if (Interlocked.CompareExchange(ref session.LastBatchDiagnosticTimestamp, now, previous) != previous) return;

        LogService.Detail(
            $"[UnifiedSearchBatch] session={session.Id} atMs={diagnostics.ElapsedMilliseconds:F0} sliceHits={metrics.HitCount} " +
            $"pendingBatches={metrics.PendingBatchCount} pendingHits={metrics.PendingHitCount} " +
            $"queueWaitMs={metrics.OldestHitWait.TotalMilliseconds:F1} " +
            $"applyMs={metrics.ApplyDuration.TotalMilliseconds:F1} " +
            $"sliceMs={metrics.SliceDuration.TotalMilliseconds:F1} " +
            $"publicationIntervalMs={(metrics.PublicationInterval?.TotalMilliseconds.ToString("F1") ?? "first")}");
    }

    private void StartContentProgressTimer(UnifiedSearchSession session)
    {
        if (session.Mode != UnifiedFilterFindMode.SearchContentsRecursively) return;
        if (InvokeRequired) throw new InvalidOperationException("Content progress timer must be created on the UI thread.");

        var timer = new System.Windows.Forms.Timer { Interval = (int)ContentProgressPublishInterval.TotalMilliseconds };
        EventHandler tickHandler = (_, _) => ApplyContentSearchProgressTimerTick(session);
        session.ContentProgressTimer = timer;
        session.ContentProgressTimerTickHandler = tickHandler;
        session.LastContentProgressTimerTickAt = session.UiDiagnostics?.BeginSpan() ?? 0;
        timer.Tick += tickHandler;
        timer.Start();
    }

    private void StopContentProgressTimer(UnifiedSearchSession session)
    {
        bool isContentSearch = session.Mode == UnifiedFilterFindMode.SearchContentsRecursively;
        if (isContentSearch) Interlocked.Exchange(ref session.LatestProgress, null);
        System.Windows.Forms.Timer? timer = session.ContentProgressTimer;
        if (timer == null) return;
        if (InvokeRequired) throw new InvalidOperationException("Content progress timer must be stopped on the UI thread.");

        session.ContentProgressTimer = null;
        EventHandler? tickHandler = session.ContentProgressTimerTickHandler;
        session.ContentProgressTimerTickHandler = null;
        if (tickHandler != null) timer.Tick -= tickHandler;
        timer.Stop();
        timer.Dispose();
        Interlocked.Exchange(ref session.LastContentProgressTimerTickAt, 0);
    }

    private void ApplyContentSearchProgressTimerTick(UnifiedSearchSession session)
    {
        if (Interlocked.Exchange(ref session.ContentProgressTimerTickActive, 1) != 0) return;
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref _unifiedSearchSession), session) || session.IsFinished ||
                session.View.IsDisposed || IsDisposed || Disposing)
            {
                StopContentProgressTimer(session);
                return;
            }

            UnifiedSearchUiDiagnostics? diagnostics = session.UiDiagnostics;
            if (diagnostics != null)
            {
                long now = diagnostics.BeginSpan();
                long previous = Interlocked.Exchange(ref session.LastContentProgressTimerTickAt, now);
                if (previous != 0)
                {
                    long expectedAt = previous + (long)(ContentProgressPublishInterval.TotalSeconds * Stopwatch.Frequency);
                    diagnostics.RecordElapsed(UnifiedSearchUiDiagnosticMetric.ContentProgressTimerLag, expectedAt, now);
                }
            }

            if (Volatile.Read(ref session.LatestProgress) != null)
                ApplyQueuedUnifiedSearchProgress(session);
        }
        finally
        {
            Interlocked.Exchange(ref session.ContentProgressTimerTickActive, 0);
        }
    }

    private void QueueUnifiedSearchProgress(UnifiedSearchSession session, UnifiedSearchProgress progress)
    {
        UnifiedSearchUiDiagnostics? diagnostics = session.UiDiagnostics;
        long queueStartedAt = diagnostics?.BeginSpan() ?? 0;
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref _unifiedSearchSession), session)) return;
            long exchangeStartedAt = diagnostics?.BeginSpan() ?? 0;
            Interlocked.Exchange(ref session.LatestProgress, progress);
            diagnostics?.RecordSpan(UnifiedSearchUiDiagnosticMetric.QueueProgressExchange, exchangeStartedAt);
            if (session.Mode == UnifiedFilterFindMode.SearchContentsRecursively) return;
            if (Interlocked.Exchange(ref session.ProgressUpdateScheduled, 1) != 0) return;
            if (diagnostics != null)
                Interlocked.Exchange(ref session.ProgressCallbackScheduledAt, diagnostics.BeginSpan());
            try
            {
                BeginInvoke((MethodInvoker)(() => ApplyQueuedUnifiedSearchProgress(session)));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref session.ProgressUpdateScheduled, 0);
                Interlocked.Exchange(ref session.ProgressCallbackScheduledAt, 0);
            }
        }
        finally
        {
            diagnostics?.RecordSpan(UnifiedSearchUiDiagnosticMetric.QueueProgress, queueStartedAt);
        }
    }

    private void ApplyQueuedUnifiedSearchProgress(UnifiedSearchSession session)
    {
        UnifiedSearchUiDiagnostics? diagnostics = session.UiDiagnostics;
        bool contentTimerTick = session.Mode == UnifiedFilterFindMode.SearchContentsRecursively;
        long diagnosticStartedAt = diagnostics?.BeginSpan() ?? 0;
        if (!contentTimerTick && diagnostics != null)
        {
            long scheduledAt = Interlocked.Exchange(ref session.ProgressCallbackScheduledAt, 0);
            diagnostics.RecordElapsed(UnifiedSearchUiDiagnosticMetric.NameProgressCallbackWait, scheduledAt, diagnosticStartedAt);
        }
        try
        {
            if (!contentTimerTick) Interlocked.Exchange(ref session.ProgressUpdateScheduled, 0);
            if (!ReferenceEquals(_unifiedSearchSession, session) || session.View.IsDisposed || session.IsFinished) return;
            long exchangeStartedAt = diagnostics?.BeginSpan() ?? 0;
            UnifiedSearchProgress? progress = Interlocked.Exchange(ref session.LatestProgress, null);
            diagnostics?.RecordSpan(UnifiedSearchUiDiagnosticMetric.ApplyProgressExchange, exchangeStartedAt);
            if (progress != null) session.View.UpdateProgress(progress);
            if (!contentTimerTick && Volatile.Read(ref session.LatestProgress) is { } pending)
            {
                long rescheduleStartedAt = diagnostics?.BeginSpan() ?? 0;
                try { QueueUnifiedSearchProgress(session, pending); }
                finally { diagnostics?.RecordSpan(UnifiedSearchUiDiagnosticMetric.ProgressReschedule, rescheduleStartedAt); }
            }
        }
        finally { diagnostics?.RecordSpan(UnifiedSearchUiDiagnosticMetric.ApplyProgress, diagnosticStartedAt); }
    }

    private string? ExecuteUnifiedSearchExport(UnifiedFilterFindSearchResult snapshot)
    {
        string? output = UnifiedSearchResultOutputService.Write(snapshot);
        if (output == null) return "出力する検索結果がありません。";
        var launched = ConfiguredEditorService.Open(
            output,
            _settingsCoordinator.Value.ExternalTools?.ExternalEditorPath,
            _integrationSeam?.ConfiguredEditorLaunchOverride);
        return launched.Succeeded ? launched.Message : $"一覧出力を開けませんでした: {launched.Message}";
    }

    private void ActivateUnifiedSearchSession(UnifiedSearchSession session, bool focusResults)
    {
        if (!ReferenceEquals(_unifiedSearchSession, session)) return;
        if (!TryEnsureUnifiedSearchSource(session)) return;
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer)
        {
            if (!TryExitViewerToBrowser()) return;
            ClearUnifiedSearchPreviewReturnContext();
        }
        if (!SwitchToUnifiedSearchSourceBrowserTab(session)) return;
        session.IsActive = true;
        _commandApplicationCoordinator.SetUnifiedSearchSessionActive(session.Id, active: true);
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
        _commandApplicationCoordinator.SetUnifiedSearchSessionActive(session.Id, active: false);
        session.View.Visible = false;
        RefreshBrowserTabHeaders();
        UpdateFunctionBar();
    }

    private bool ApplyUnifiedSearchSurface(Guid sessionId, bool active)
    {
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null || session.Id != sessionId || session.View.IsDisposed)
        {
            return false;
        }

        if (!active)
        {
            DeactivateUnifiedSearchSurface();
            FocusBrowserFileList(force: true);
            return true;
        }

        if (!string.Equals(
                _browserApplicationCoordinator.Workspace.ActiveCategoryId,
                session.SourceCategoryId,
                StringComparison.OrdinalIgnoreCase) ||
            _browserApplicationCoordinator.Workspace.ActiveTabSnapshot?.Id != session.SourceTabId)
        {
            return false;
        }

        session.IsActive = true;
        _commandApplicationCoordinator.SetUnifiedSearchSessionActive(session.Id, active: true);
        session.View.Visible = true;
        session.View.BringToFront();
        RefreshBrowserTabHeaders();
        UpdateFunctionBar();
        if (session.View.ResultList.CanFocus)
        {
            session.View.ResultList.Focus();
        }
        return true;
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
        bool activated = ExecuteCommandFromUi(CommandIds.BrowserTabActivate, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.UnifiedSearch.Activate",
            CategoryId = session.SourceCategoryId,
            ContextBrowserTabId = session.SourceTabId,
            BrowserTabNavigation = new BrowserTabNavigationCommandContext(
                IncludeUnifiedSearchResults: true,
                UnifiedSearchSessionId: session.Id,
                UnifiedSearchSourceCategoryId: session.SourceCategoryId,
                UnifiedSearchSourceTabId: session.SourceTabId,
                UnifiedSearchIsActive: session.IsActive)
        });
        if (activated) ClearNameSearchInspectionReturnContext();
        return activated;
    }

    private bool ReopenUnifiedSearchDialog(UnifiedSearchSession session, string source)
    {
        if (!ReferenceEquals(_unifiedSearchSession, session)
            || !TryEnsureUnifiedSearchSource(session)
            || !SwitchToUnifiedSearchSourceBrowserTab(session)) return false;

        return ExecuteCommandFromUi(CommandIds.BrowserSearch, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = source,
            CategoryId = session.SourceCategoryId,
            ContextBrowserTabId = session.SourceTabId,
            UnifiedSearchResume = new UnifiedSearchResumeCommandContext(
                session.Id,
                session.SourceCategoryId,
                session.SourceTabId,
                session.RootPath,
                session.Criteria.Clone(),
                session.Mode)
        });
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

    private void CloseUnifiedSearchSession(bool restoreSource, bool refreshProjection = true)
    {
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null) return;
        if (restoreSource &&
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer &&
            !TryExitViewerToBrowser())
        {
            return;
        }
        DetachUnifiedSearchSession(session);

        if (restoreSource && !IsDisposed && !Disposing)
        {
            BrowserTabCategoryDefinition? category = _browserApplicationCoordinator.Workspace.CategoryStates
                .FirstOrDefault(item => string.Equals(item.Id, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<BrowserTabState> tabs = category == null ? [] : GetBrowserTabContextStates(category.Id);
            int sourceIndex = GetBrowserTabIndexById(tabs, session.SourceTabId);
            if (category != null && sourceIndex >= 0)
            {
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
        StopContentProgressTimer(session);
        session.UiDiagnostics?.Stop("detached");
        session.View.UiDiagnostics = null;
        session.Batches?.Abandon();
        session.BatchScheduler?.Dispose();
        session.BatchScheduler = null;
        _commandApplicationCoordinator.ClearUnifiedSearchSession(session.Id);
        if (_pendingUnifiedSearchHit is { } pending && ReferenceEquals(pending.ResultsView, session.View))
            _pendingUnifiedSearchHit = null;
        session.IsActive = false;
        session.View.Visible = false;
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
        _commandApplicationCoordinator.ClearUnifiedSearchPreviewReturnContext(searchSessionId);
    }

    private void ApplyUnifiedSearchPreviewReturnAfterViewerExit()
    {
        _commandApplicationCoordinator.ApplyUnifiedSearchPreviewReturn(
            _viewerApplicationCoordinator.ActiveRequestId,
            _browserApplicationCoordinator.Workspace.ActiveCategoryId,
            _browserApplicationCoordinator.Workspace.ActiveTabSnapshot?.Id);
    }

    private void ClearNameSearchInspectionReturnContext(Guid? searchSessionId = null)
    {
        _commandApplicationCoordinator.ClearUnifiedSearchNameInspectionReturnContext(searchSessionId);
    }

    private void ClearNameSearchInspectionReturnContextUnlessSource(Guid targetTabId, string targetCategoryId)
    {
        _commandApplicationCoordinator.ClearUnifiedSearchNameInspectionReturnUnlessSource(targetTabId, targetCategoryId);
    }

    private bool ApplyNameSearchInspectionReturn()
    {
        return _commandApplicationCoordinator.ApplyUnifiedSearchNameInspectionReturn(
            _browserApplicationCoordinator.Workspace.ActiveCategoryId,
            _browserApplicationCoordinator.Workspace.ActiveTabSnapshot?.Id);
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

    private string? DispatchUnifiedSearchResultActivation(
        string commandId,
        string path,
        bool directory,
        UnifiedSearchMatch? hit,
        UnifiedSearchResultActivationOrigin origin,
        Guid searchSessionId)
    {
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null || session.Id != searchSessionId)
        {
            return "検索sessionが終了しました。検索結果を開き直してください。";
        }

        var request = new UnifiedSearchResultActivationCommandContext(
            session.Id,
            session.SourceCategoryId,
            session.SourceTabId,
            path,
            directory,
            session.Mode,
            new UnifiedSearchResultIdentity(path, hit?.LineNumber, hit?.ColumnNumber, hit?.MatchText),
            origin,
            hit);
        var commandContext = new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = $"UnifiedSearchResults.{origin}",
            UnifiedSearchResultActivation = request
        };
        bool handled = ExecuteCommandCore(commandId, commandContext, out CommandApplicationResult result);
        return result.Effect?.UnifiedSearchResultActivation?.ErrorMessage ??
            (handled ? null : "検索結果を開けませんでした。");
    }

    private string? DispatchUnifiedSearchHitInExternalEditor(
        string path,
        UnifiedSearchMatch? hit,
        Guid searchSessionId)
    {
        UnifiedSearchSession? session = _unifiedSearchSession;
        if (session == null || session.Id != searchSessionId)
        {
            return "検索sessionが終了しました。検索結果を開き直してください。";
        }

        var request = new UnifiedSearchHitEditorCommandContext(
            session.Id,
            path,
            new UnifiedSearchResultIdentity(path, hit?.LineNumber, hit?.ColumnNumber, hit?.MatchText),
            hit);
        bool handled = ExecuteCommandCore(
            CommandIds.BrowserSearchResultsOpenHitInEditor,
            new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "UnifiedSearchResults.OpenHitInExternalEditor",
                UnifiedSearchHitEditor = request
            },
            out CommandApplicationResult result);
        return result.Effect?.UnifiedSearchHitEditor?.ErrorMessage ??
            (handled ? null : "検索hitを外部Editorで開けませんでした。");
    }

    bool IUnifiedSearchResultActivationUiPort.IsSearchSessionCurrent(Guid sessionId) =>
        _unifiedSearchSession is { } session && session.Id == sessionId && !session.View.IsDisposed;

    bool IUnifiedSearchResultActivationUiPort.IsViewerMode =>
        _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer;

    bool IUnifiedSearchResultActivationUiPort.TryExitViewerToBrowser() => TryExitViewerToBrowser();

    bool IUnifiedSearchResultActivationUiPort.SetSearchSurfaceActive(Guid sessionId, bool active) =>
        ApplyUnifiedSearchSurface(sessionId, active);

    bool IUnifiedSearchResultActivationUiPort.DispatchCommand(string commandId, CommandExecutionContext context)
    {
        bool? overrideResult = _integrationSeam?.UnifiedSearchResultActivationCommandOverride?.Invoke(
            commandId,
            context.Source ?? string.Empty);
        return overrideResult ?? ExecuteCommandFromUi(commandId, context);
    }

    bool IUnifiedSearchResultActivationUiPort.TryOpenPreview(
        Guid sessionId,
        Guid resultTabId,
        UnifiedSearchMatch hit,
        out int requestId)
    {
        requestId = -1;
        if (_unifiedSearchSession is not { } session || session.Id != sessionId || session.View.IsDisposed)
        {
            return false;
        }

        _pendingUnifiedSearchHit = new PendingUnifiedSearchHit
        {
            FullPath = hit.FullPath,
            Match = hit,
            ResultsView = session.View
        };
        bool started = ExecuteCommandFromUi(CommandIds.BrowserPreview, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "UnifiedSearchResults.Preview",
            ContextTargetPath = hit.FullPath
        });
        if (!started || _unifiedSearchSession?.Id != sessionId)
        {
            _pendingUnifiedSearchHit = null;
            return false;
        }

        requestId = _viewerApplicationCoordinator.ActiveRequestId;
        _pendingUnifiedSearchHit.RequestId = requestId;
        return true;
    }

    void IUnifiedSearchResultActivationUiPort.ClearPendingPreview(Guid sessionId)
    {
        if (_unifiedSearchSession is { } session && session.Id == sessionId &&
            _pendingUnifiedSearchHit is { } pending && ReferenceEquals(pending.ResultsView, session.View))
        {
            _pendingUnifiedSearchHit = null;
        }
    }

    void IUnifiedSearchResultActivationUiPort.RefreshSearchTabProjection() => RefreshBrowserTabHeaders();

    void IUnifiedSearchResultActivationUiPort.RequestForeground(bool preview) =>
        RequestSearchActivationForeground(preview);

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
