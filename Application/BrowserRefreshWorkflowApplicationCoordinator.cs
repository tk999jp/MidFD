using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Browser の refresh、watcher、passive count audit の workflow 判断を所有する。
/// Timer、FileSystemWatcher、UI status は Shell が所有し、ここは plain transition のみ返す。
/// </summary>
internal sealed class BrowserRefreshWorkflowApplicationCoordinator
{
    private readonly BrowserApplicationCoordinator _browser;

    public BrowserRefreshWorkflowApplicationCoordinator(BrowserApplicationCoordinator browser)
    {
        _browser = browser;
    }

    public bool IsPending => _browser.Refresh.IsPending;
    public bool IsPassiveRefresh => _browser.Refresh.IsPassiveRefresh;
    public bool IsApplying => _browser.Refresh.IsApplying;
    public bool IsApplyingDirectoryList => _browser.Refresh.IsApplyingDirectoryList;
    public bool DelayCompleted => _browser.Refresh.DelayCompleted;
    public int EventCount => _browser.Refresh.EventCount;
    public int FilteredTotalItemCount => _browser.Refresh.FilteredTotalItemCount;
    public string? CurrentWatcherPath => _browser.Refresh.CurrentDirectoryWatcherPath;
    public long CurrentWatcherGeneration => _browser.Refresh.CurrentDirectoryWatcherGeneration;
    public long DirectoryNavigationGeneration => _browser.Refresh.DirectoryNavigationGeneration;
    public long DirectoryContentGeneration => _browser.Refresh.DirectoryContentGeneration;

    public void SetApplyingDirectoryList(bool value) => _browser.Refresh.SetApplyingDirectoryList(value);

    public void ConfigureDirectoryCost(int rawDirectoryEntryCount, int filteredTotalItemCount, long itemBuildMilliseconds) =>
        _browser.Refresh.ConfigureDirectoryCost(rawDirectoryEntryCount, filteredTotalItemCount, itemBuildMilliseconds);

    public int GetQuietWindowMilliseconds(int defaultMilliseconds) =>
        _browser.Refresh.GetQuietWindowMilliseconds(defaultMilliseconds);

    public void MarkRefreshDelayCompleted() => _browser.Refresh.MarkRefreshDelayCompleted();

    public BrowserDirectoryPostLoadEffects PreparePostLoadEffects(
        BrowserRefreshShellState shellState)
    {
        BrowserWatcherUpdate watcher = BeginWatcherUpdate(
            _browser.CurrentPath,
            shellState.FeatureEnabled,
            shellState.CurrentNotifyFilter);
        BrowserCountAuditLifecyclePlan audit = EvaluateAuditLifecycle(
            shellState.FeatureEnabled,
            shellState.IsExitPending,
            shellState.IsDisposed,
            shellState.TimerEnabled && watcher.Kind == BrowserWatcherPlanKind.Keep);
        return new BrowserDirectoryPostLoadEffects(watcher, audit);
    }

    private void ScheduleRefreshDelay() => _browser.Refresh.ScheduleRefreshDelay();

    public BrowserRefreshQueueTransition QueueExternalChange(
        string watchedDirectoryPath,
        long watcherGeneration,
        string reason,
        Exception? exception,
        int defaultQuietWindowMilliseconds)
    {
        _browser.Refresh.ResetAuditBackoff();
        string normalizedWatchedPath = NormalizeDirectoryWatchPath(watchedDirectoryPath);
        string normalizedCurrentPath = NormalizeDirectoryWatchPath(_browser.CurrentPath);
        string normalizedWatcherPath = NormalizeDirectoryWatchPath(CurrentWatcherPath);
        return _browser.Refresh.QueueExternalRefresh(
            normalizedWatchedPath,
            reason,
            normalizedWatchedPath,
            normalizedCurrentPath,
            normalizedWatcherPath,
            watcherGeneration,
            CurrentWatcherGeneration,
            exception,
            defaultQuietWindowMilliseconds);
    }

    public BrowserRefreshQueueExecution QueueExternalChangeAndProcessIfNeeded(
        string watchedDirectoryPath,
        long watcherGeneration,
        string reason,
        Exception? exception,
        int defaultQuietWindowMilliseconds,
        bool isBrowserMode,
        bool isBusy,
        bool isExitPending,
        bool isDisposed,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserRefreshQueueTransition queued = QueueExternalChange(
            watchedDirectoryPath,
            watcherGeneration,
            reason,
            exception,
            defaultQuietWindowMilliseconds);
        BrowserRefreshProcessExecution? processed = queued.ProcessImmediately
            ? ExecutePendingRefreshProcessing(
                isBrowserMode,
                isBusy,
                isExitPending,
                isDisposed,
                options,
                columnCount,
                shellState)
            : null;
        return new BrowserRefreshQueueExecution(queued, processed);
    }

    public BrowserRefreshProcessExecution ExecuteDelayedPendingRefreshProcessing(
        bool isBrowserMode,
        bool isBusy,
        bool isExitPending,
        bool isDisposed,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        MarkRefreshDelayCompleted();
        return ExecutePendingRefreshProcessing(
            isBrowserMode,
            isBusy,
            isExitPending,
            isDisposed,
            options,
            columnCount,
            shellState);
    }

    private BrowserRefreshBeginTransition? BeginPendingRefresh(
        bool isBrowserMode,
        bool isBusy,
        bool isExitPending,
        bool isDisposed)
    {
        return _browser.Refresh.TryBeginPendingRefresh(
            NormalizeDirectoryWatchPath(_browser.CurrentPath),
            CurrentWatcherGeneration,
            isBrowserMode,
            isBusy,
            isExitPending,
            isDisposed);
    }

    private BrowserRefreshCompletion CompleteRefresh(long elapsedMilliseconds) =>
        _browser.Refresh.CompleteRefresh(elapsedMilliseconds);

    public void ClearPendingRefresh() => _browser.Refresh.ClearPendingRefresh();

    public BrowserManualRefreshDecision BeginManualRefresh(bool isBrowserMode, bool isBusy)
    {
        if (!isBrowserMode)
        {
            return BrowserManualRefreshDecision.NotBrowser;
        }
        if (isBusy)
        {
            return BrowserManualRefreshDecision.Busy;
        }

        _browser.Refresh.ClearPendingRefresh();
        _browser.Refresh.ResetAuditBackoff();
        return BrowserManualRefreshDecision.Proceed;
    }

    public void CompleteManualRefresh() => _browser.Refresh.ClearPendingRefresh();

    public void CancelReloadRetry() => _browser.Refresh.SetCurrentDirectoryRefreshRetryPending(false);

    public BrowserManualRefreshExecution ExecuteManualRefresh(
        bool isBrowserMode,
        bool isBusy,
        string reason,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserManualRefreshDecision decision = BeginManualRefresh(isBrowserMode, isBusy);
        if (!decision.ShouldRun)
        {
            return new BrowserManualRefreshExecution(decision, BrowserReloadExecution.Stale, default);
        }

        try
        {
            BrowserReloadExecution reload = ExecuteReload(
                _browser.CurrentPath,
                force: false,
                options,
                columnCount,
                shellState);
            return new BrowserManualRefreshExecution(
                decision,
                reload,
                reload.DirectoryLoad is { Succeeded: true }
                    ? reload.PostLoadEffects
                    : default);
        }
        finally
        {
            CompleteManualRefresh();
        }
    }

    public BrowserReloadExecution ExecuteReloadRetry(
        string expectedPath,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        if (!IsCurrentDirectory(expectedPath))
        {
            return BrowserReloadExecution.Stale;
        }

        BrowserReloadPlan plan = PrepareReload(expectedPath, force: false, refreshBlocked: false);
        return plan.Kind == BrowserReloadPlanKind.NoCurrentPath || plan.Kind == BrowserReloadPlanKind.Blocked
            ? new BrowserReloadExecution(
                plan.Kind == BrowserReloadPlanKind.NoCurrentPath
                    ? BrowserReloadExecutionKind.NoCurrentPath
                    : BrowserReloadExecutionKind.Blocked,
                plan.Path,
                null,
                plan.FallbackReason,
                0,
                null)
            : ExecuteReloadPlan(plan, options, columnCount, allowRetry: false, shellState);
    }

    public BrowserRefreshProcessStart? BeginPendingRefreshProcessing(
        bool isBrowserMode,
        bool isBusy,
        bool isExitPending,
        bool isDisposed) =>
        BeginPendingRefresh(isBrowserMode, isBusy, isExitPending, isDisposed) is { } transition
            ? new BrowserRefreshProcessStart(transition.Batch, transition.Reason)
            : null;

    public BrowserRefreshProcessCompletion CompletePendingRefreshProcessing(
        bool reloadSucceeded,
        long elapsedMilliseconds)
    {
        BrowserRefreshCompletion completion = CompleteRefresh(elapsedMilliseconds);
        if (completion.HasFollowUpPending)
        {
            ScheduleRefreshDelay();
        }

        return new BrowserRefreshProcessCompletion(
            reloadSucceeded,
            completion.HasFollowUpPending,
            completion.QuietWindowMilliseconds);
    }

    public BrowserRefreshProcessExecution ExecutePendingRefreshProcessing(
        bool isBrowserMode,
        bool isBusy,
        bool isExitPending,
        bool isDisposed,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserRefreshProcessStart? started = BeginPendingRefreshProcessing(
            isBrowserMode,
            isBusy,
            isExitPending,
            isDisposed);
        if (!started.HasValue)
        {
            return BrowserRefreshProcessExecution.NotStarted;
        }

        Stopwatch reloadStopwatch = Stopwatch.StartNew();
        BrowserReloadExecution reload = ExecuteReload(
            _browser.CurrentPath,
            force: true,
            options,
            columnCount,
            shellState);
        reloadStopwatch.Stop();
        BrowserRefreshProcessCompletion completion = CompletePendingRefreshProcessing(
            reload.DirectoryLoad?.Succeeded == true,
            reloadStopwatch.ElapsedMilliseconds);
        return new BrowserRefreshProcessExecution(
            started.Value.Batch,
            started.Value.Reason,
            reload,
            completion)
        {
            PostLoadEffects = reload.PostLoadEffects,
            ElapsedMilliseconds = reloadStopwatch.ElapsedMilliseconds
        };
    }

    public BrowserCountAuditLifecyclePlan EvaluateAuditLifecycle(
        bool featureEnabled,
        bool isExitPending,
        bool isDisposed,
        bool timerEnabled)
    {
        bool active = featureEnabled &&
            !isExitPending &&
            !isDisposed &&
            IsPassiveRefresh &&
            !string.IsNullOrWhiteSpace(_browser.CurrentPath);
        if (!active)
        {
            return new BrowserCountAuditLifecyclePlan(
                StopTimer: true,
                StartTimer: false,
                ResetBackoff: false,
                IntervalMilliseconds: 0);
        }

        if (timerEnabled)
        {
            return BrowserCountAuditLifecyclePlan.Keep;
        }

        ResetAuditBackoff();
        return new BrowserCountAuditLifecyclePlan(
            StopTimer: false,
            StartTimer: true,
            ResetBackoff: true,
            IntervalMilliseconds: GetAuditIntervalMilliseconds(_browser.CurrentPath));
    }

    public int ResetAuditBackoff()
    {
        _browser.Refresh.ResetAuditBackoff();
        return GetAuditIntervalMilliseconds(_browser.CurrentPath);
    }

    public int GetAuditIntervalMilliseconds(string? currentPath) =>
        _browser.Refresh.GetAuditIntervalMilliseconds(currentPath);

    public BrowserCountAuditRequest? BeginCountAudit(
        bool showHiddenFiles,
        bool isExitPending,
        bool isDisposed)
    {
        return _browser.Refresh.TryBeginCountAudit(
            _browser.CurrentPath,
            showHiddenFiles,
            IsPassiveRefresh,
            isExitPending,
            isDisposed,
            IsApplying);
    }

    public void ReleaseCanceledCountAudit() => _browser.Refresh.CompleteCountAudit();

    public BrowserCountAuditApplyResult CompleteCountAuditAndApply(
        BrowserCountAuditRequest request,
        int rawDirectoryEntryCount,
        bool isExitPending,
        bool isDisposed)
    {
        _browser.Refresh.CompleteCountAudit();
        return ApplyCountAudit(request, rawDirectoryEntryCount, isExitPending, isDisposed);
    }

    public Task<DirectoryCountAuditResult> ExecuteCountAuditAsync(BrowserCountAuditRequest request) =>
        Task.Run(
            () => DirectoryCountAuditService.CountVisibleEntriesDetailed(
                request.CurrentPath,
                request.ShowHiddenFiles,
                request.Token),
            request.Token);

    public async Task<BrowserCountAuditExecution> ExecuteCountAuditLifecycleAsync(
        bool showHiddenFiles,
        bool isExitPending,
        bool isDisposed)
    {
        BrowserCountAuditRequest? request = BeginCountAudit(
            showHiddenFiles,
            isExitPending,
            isDisposed);
        if (!request.HasValue)
        {
            return BrowserCountAuditExecution.NotStarted;
        }

        bool completed = false;
        try
        {
            DirectoryCountAuditResult result = await ExecuteCountAuditAsync(request.Value).ConfigureAwait(false);
            BrowserCountAuditApplyResult applied = CompleteCountAuditAndApply(
                request.Value,
                result.VisibleEntryCount,
                isExitPending,
                isDisposed);
            completed = true;
            return new BrowserCountAuditExecution(
                BrowserCountAuditExecutionKind.Completed,
                request.Value,
                result,
                applied);
        }
        catch (OperationCanceledException)
        {
            return BrowserCountAuditExecution.Canceled;
        }
        catch
        {
            return BrowserCountAuditExecution.Failed;
        }
        finally
        {
            if (!completed)
            {
                ReleaseCanceledCountAudit();
            }
        }
    }

    public BrowserCountAuditApplyResult ApplyCountAudit(
        BrowserCountAuditRequest request,
        int rawDirectoryEntryCount,
        bool isExitPending,
        bool isDisposed)
    {
        bool stale = isExitPending || isDisposed ||
            request.NavigationGeneration != DirectoryNavigationGeneration ||
            request.ContentGeneration != DirectoryContentGeneration ||
            request.WatcherGeneration != CurrentWatcherGeneration ||
            !string.Equals(
                NormalizeDirectoryWatchPath(request.CurrentPath),
                NormalizeDirectoryWatchPath(_browser.CurrentPath),
                StringComparison.OrdinalIgnoreCase);
        if (stale)
        {
            return BrowserCountAuditApplyResult.Stale;
        }

        BrowserCountAuditApplyTransition transition = _browser.Refresh.ApplyCountAudit(
            request.CurrentPath,
            request.WatcherGeneration,
            rawDirectoryEntryCount);
        return new BrowserCountAuditApplyResult(
            IsStale: false,
            transition.Changed,
            transition.NextIntervalMilliseconds,
            FilteredTotalItemCount);
    }

    public void CancelCountAudit() => _browser.Refresh.CancelCountAudit();

    public BrowserWatcherUpdate BeginWatcherUpdate(
        string? currentPath,
        bool featureEnabled,
        NotifyFilters? currentNotifyFilter) =>
        PrepareWatcherUpdate(currentPath, featureEnabled, currentNotifyFilter);

    public BrowserWatcherUpdate PrepareWatcherUpdate(
        string? currentPath,
        bool featureEnabled,
        NotifyFilters? currentNotifyFilter)
    {
        BrowserWatcherPlan plan = PrepareWatcher(currentPath, featureEnabled, currentNotifyFilter);
        if (plan.Kind == BrowserWatcherPlanKind.Keep)
        {
            return BrowserWatcherUpdate.Keep;
        }

        ResetWatcherState();
        return new BrowserWatcherUpdate(plan.Kind, plan.Path, plan.Generation, plan.NotifyFilter);
    }

    public void CompleteWatcherUpdate(BrowserWatcherUpdate update, bool materialized)
    {
        if (update.Kind == BrowserWatcherPlanKind.Create && materialized && update.Path != null)
        {
            CommitWatcher(update.Path, update.Generation);
        }
    }

    private BrowserWatcherPlan PrepareWatcher(
        string? currentPath,
        bool featureEnabled,
        NotifyFilters? currentNotifyFilter) =>
        _browser.Refresh.PrepareWatcher(
            currentPath,
            _browser.CurrentSort,
            featureEnabled,
            currentNotifyFilter);

    private void CommitWatcher(string path, long generation) => _browser.Refresh.CommitWatcher(path, generation);

    private void ResetWatcherState() => _browser.Refresh.ResetWatcherState();

    private static string NormalizeDirectoryWatchPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : NavigationService.NormalizeDirectoryForCompare(path);

    private BrowserReloadExecution ExecuteReload(
        string currentPath,
        bool force,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState)
    {
        BrowserReloadPlan plan = PrepareReload(currentPath, force, refreshBlocked: false);
        return ExecuteReloadPlan(plan, options, columnCount, allowRetry: true, shellState);
    }

    private BrowserReloadExecution ExecuteReloadPlan(
        BrowserReloadPlan plan,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        bool allowRetry,
        BrowserRefreshShellState shellState)
    {
        switch (plan.Kind)
        {
            case BrowserReloadPlanKind.NoCurrentPath:
                return BrowserReloadExecution.NoCurrentPath;
            case BrowserReloadPlanKind.Blocked:
                return BrowserReloadExecution.Blocked;
            case BrowserReloadPlanKind.Missing:
                return new BrowserReloadExecution(
                    BrowserReloadExecutionKind.Missing,
                    plan.Path,
                    null,
                    plan.FallbackReason,
                    0,
                    null)
                {
                    Watcher = PrepareWatcherUpdate(
                        null,
                        shellState.FeatureEnabled,
                        shellState.CurrentNotifyFilter)
                };
        }

        BrowserLoadCoordinator.DirectoryLoadRequest request = _browser.CreateDirectoryLoadRequest(
            plan.Path,
            focusTargetName: null,
            isHistoryNavigation: false,
            suppressRecent: false,
            options);
        BrowserDirectoryLoadExecution load = _browser.Directory.Execute(request);
        if (load.Succeeded)
        {
            BrowserDirectoryApplicationTransition transition = _browser.Directory.ApplyDirectoryLoad(
                load.Result!,
                options.ItemsPerPage,
                columnCount,
                options.SortKind ?? _browser.CurrentSort,
                options.SortAscending ?? _browser.SortAscending,
                isSwitchingBrowserTab: false);
            return new BrowserReloadExecution(
                BrowserReloadExecutionKind.Loaded,
                plan.Path,
                load.Result,
                plan.FallbackReason,
                0,
                null)
            {
                DirectoryLoad = BrowserDirectoryLoadApplicationResult.Success(load.Result!, transition),
                PostLoadEffects = PreparePostLoadEffects(shellState)
            };
        }

        if (allowRetry && plan.Kind == BrowserReloadPlanKind.Reload)
        {
            BrowserReloadFailureTransition retry = BeginReloadFailure(plan.Path);
            if (retry.RetryScheduled)
            {
                return new BrowserReloadExecution(
                    BrowserReloadExecutionKind.RetryScheduled,
                    plan.Path,
                    null,
                    plan.FallbackReason,
                    retry.DelayMilliseconds,
                    load.Error);
            }
        }

        return new BrowserReloadExecution(
            BrowserReloadExecutionKind.Failed,
            plan.Path,
            null,
            plan.FallbackReason,
            0,
            load.Error);
    }

    private BrowserReloadFailureTransition BeginReloadFailure(string currentPath)
    {
        if (_browser.CurrentDirectoryRefreshRetryPending)
        {
            return BrowserReloadFailureTransition.NotScheduled;
        }

        _browser.SetCurrentDirectoryRefreshRetryPending(true);
        return new BrowserReloadFailureTransition(true, currentPath, 100);
    }

    private BrowserReloadPlan PrepareReload(string currentPath, bool force, bool refreshBlocked)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return BrowserReloadPlan.NoCurrentPath;
        }
        if (!force && refreshBlocked)
        {
            return BrowserReloadPlan.Blocked;
        }
        if (Directory.Exists(currentPath))
        {
            return new BrowserReloadPlan(BrowserReloadPlanKind.Reload, currentPath, null);
        }
        if (NavigationFallbackResolver.TryResolveExistingDirectoryFallback(
            currentPath,
            message => LogService.Error(message),
            out string fallbackPath,
            out string fallbackReason))
        {
            return new BrowserReloadPlan(BrowserReloadPlanKind.Fallback, fallbackPath, fallbackReason);
        }
        return new BrowserReloadPlan(BrowserReloadPlanKind.Missing, currentPath, null);
    }

    private bool IsCurrentDirectory(string path) =>
        string.Equals(
            NormalizeDirectoryWatchPath(path),
            NormalizeDirectoryWatchPath(_browser.CurrentPath),
            StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct BrowserCountAuditLifecyclePlan(
    bool StopTimer,
    bool StartTimer,
    bool ResetBackoff,
    int IntervalMilliseconds)
{
    public static BrowserCountAuditLifecyclePlan Keep => new(false, false, false, 0);
}

internal readonly record struct BrowserCountAuditApplyResult(
    bool IsStale,
    bool Changed,
    int NextIntervalMilliseconds,
    int FilteredTotalItemCount)
{
    public static BrowserCountAuditApplyResult Stale => new(true, false, 0, 0);
}

internal enum BrowserCountAuditExecutionKind
{
    NotStarted,
    Completed,
    Canceled,
    Failed
}

internal readonly record struct BrowserCountAuditExecution(
    BrowserCountAuditExecutionKind Kind,
    BrowserCountAuditRequest Request,
    DirectoryCountAuditResult? Result,
    BrowserCountAuditApplyResult Applied)
{
    public bool Started => Kind != BrowserCountAuditExecutionKind.NotStarted;
    public bool Completed => Kind == BrowserCountAuditExecutionKind.Completed;

    public static BrowserCountAuditExecution NotStarted =>
        new(default, default, null, BrowserCountAuditApplyResult.Stale);

    public static BrowserCountAuditExecution Canceled =>
        new(BrowserCountAuditExecutionKind.Canceled, default, null, BrowserCountAuditApplyResult.Stale);

    public static BrowserCountAuditExecution Failed =>
        new(BrowserCountAuditExecutionKind.Failed, default, null, BrowserCountAuditApplyResult.Stale);
}

internal enum BrowserManualRefreshDecisionKind
{
    Proceed,
    NotBrowser,
    Busy
}

internal readonly record struct BrowserManualRefreshDecision(BrowserManualRefreshDecisionKind Kind)
{
    public bool ShouldRun => Kind == BrowserManualRefreshDecisionKind.Proceed;
    public static BrowserManualRefreshDecision Proceed => new(BrowserManualRefreshDecisionKind.Proceed);
    public static BrowserManualRefreshDecision NotBrowser => new(BrowserManualRefreshDecisionKind.NotBrowser);
    public static BrowserManualRefreshDecision Busy => new(BrowserManualRefreshDecisionKind.Busy);
}

internal readonly record struct BrowserRefreshProcessStart(NavigationRefreshBatch Batch, string Reason);

internal readonly record struct BrowserRefreshProcessCompletion(
    bool ReloadSucceeded,
    bool HasFollowUpPending,
    int QuietWindowMilliseconds);

internal readonly record struct BrowserManualRefreshExecution(
    BrowserManualRefreshDecision Decision,
    BrowserReloadExecution Reload,
    BrowserDirectoryPostLoadEffects PostLoadEffects);

internal readonly record struct BrowserRefreshProcessExecution(
    NavigationRefreshBatch Batch,
    string Reason,
    BrowserReloadExecution Reload,
    BrowserRefreshProcessCompletion Completion)
{
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public bool Started => !string.IsNullOrEmpty(Reason);

    public static BrowserRefreshProcessExecution NotStarted =>
        new(
            new NavigationRefreshBatch(string.Empty, 0, Array.Empty<string>(), 0, null, null),
            string.Empty,
            BrowserReloadExecution.Stale,
            new(false, false, 0));
}

internal readonly record struct BrowserRefreshQueueExecution(
    BrowserRefreshQueueTransition Queue,
    BrowserRefreshProcessExecution? Processed);

internal readonly record struct BrowserRefreshShellState(
    bool FeatureEnabled,
    bool IsExitPending,
    bool IsDisposed,
    bool TimerEnabled,
    NotifyFilters? CurrentNotifyFilter);

internal readonly record struct BrowserRefreshProcessRequest(
    bool IsBrowserMode,
    bool IsBusy,
    bool IsExitPending,
    bool IsDisposed,
    BrowserDirectoryLoadOptions Options,
    int ColumnCount,
    BrowserRefreshShellState ShellState);

internal readonly record struct BrowserDirectoryPostLoadEffects(
    BrowserWatcherUpdate Watcher,
    BrowserCountAuditLifecyclePlan Audit);

internal readonly record struct BrowserWatcherUpdate(
    BrowserWatcherPlanKind Kind,
    string? Path,
    long Generation,
    NotifyFilters NotifyFilter)
{
    public static BrowserWatcherUpdate Keep => new(BrowserWatcherPlanKind.Keep, null, 0, 0);
}
