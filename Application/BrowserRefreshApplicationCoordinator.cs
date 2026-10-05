using System;
using System.IO;
using System.Threading;
using MidFD.Coordinators;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Browser外部変更の意味判断を保持する。FileSystemWatcher/TimerはShellが所有し、
/// Shellから渡されたplain eventに対するrefresh・audit・generationの判断だけを行う。
/// </summary>
internal sealed class BrowserRefreshApplicationCoordinator
{
    private readonly BrowserNavigationSessionState _state;

    public BrowserRefreshApplicationCoordinator(BrowserNavigationSessionState state)
    {
        _state = state;
    }

    public bool IsPending => _state.RefreshCoordinator.State.IsPending;
    public bool IsPassiveRefresh => _state.RefreshCoordinator.State.IsPassiveRefresh;
    public bool IsApplying => _state.RefreshCoordinator.State.IsApplying;
    public bool DelayCompleted => _state.RefreshCoordinator.State.DelayCompleted;
    public int EventCount => _state.RefreshCoordinator.State.EventCount;
    public int FilteredTotalItemCount => _state.RefreshCoordinator.State.FilteredTotalItemCount;
    public string? CurrentDirectoryWatcherPath => _state.CurrentDirectoryWatcherPath;
    public long CurrentDirectoryWatcherGeneration => _state.CurrentDirectoryWatcherGeneration;
    public long DirectoryNavigationGeneration => _state.DirectoryNavigationGeneration;
    public long DirectoryContentGeneration => _state.DirectoryContentGeneration;
    public bool CurrentDirectoryRefreshRetryPending => _state.CurrentDirectoryRefreshRetryPending;
    public bool IsApplyingDirectoryList => _state.IsApplyingDirectoryList;

    public void SetDirectoryContentGeneration(long value) => _state.DirectoryContentGeneration = value;
    public void IncrementDirectoryContentGeneration() => _state.DirectoryContentGeneration++;
    public void IncrementDirectoryNavigationGeneration() => _state.DirectoryNavigationGeneration++;
    public void SetCurrentDirectoryRefreshRetryPending(bool value) => _state.CurrentDirectoryRefreshRetryPending = value;
    public void SetApplyingDirectoryList(bool value) => _state.IsApplyingDirectoryList = value;
    public void SetPassiveRefresh(bool value) => _state.RefreshCoordinator.State.IsPassiveRefresh = value;
    public void ScheduleRefreshDelay() => _state.RefreshCoordinator.State.ScheduleRefreshDelay();

    public int GetQuietWindowMilliseconds(int defaultMilliseconds)
    {
        return _state.LastExternalDirectoryReloadMilliseconds switch
        {
            >= 3000 => 3000,
            >= 1000 => 1500,
            _ => defaultMilliseconds
        };
    }

    public void ConfigureDirectoryCost(int rawDirectoryEntryCount, int filteredTotalItemCount, long itemBuildMilliseconds)
    {
        _state.RefreshCoordinator.ConfigureDirectoryCost(
            rawDirectoryEntryCount,
            filteredTotalItemCount,
            itemBuildMilliseconds);
    }

    public BrowserRefreshQueueTransition QueueExternalRefresh(
        string watchedDirectoryPath,
        string reason,
        string normalizedWatchedPath,
        string normalizedCurrentPath,
        string normalizedWatcherPath,
        long watcherGeneration,
        long activeWatcherGeneration,
        Exception? exception,
        int defaultQuietWindowMilliseconds)
    {
        _state.CountAuditSchedule.ResetForActivity();
        bool restartQuietTimer = _state.RefreshCoordinator.QueueRefresh(
            watchedDirectoryPath,
            reason,
            normalizedWatchedPath,
            normalizedCurrentPath,
            normalizedWatcherPath,
            watcherGeneration,
            activeWatcherGeneration,
            exception);

        return new BrowserRefreshQueueTransition(
            Accepted: restartQuietTimer || IsPending,
            RestartQuietTimer: restartQuietTimer,
            ProcessImmediately: false,
            QuietWindowMilliseconds: GetQuietWindowMilliseconds(defaultQuietWindowMilliseconds),
            ShowPassiveRefreshHint: IsPassiveRefresh && EventCount == 1,
            AuditIntervalMilliseconds: GetAuditIntervalMilliseconds(_state.Navigation.CurrentPath));
    }

    public void MarkRefreshDelayCompleted() => _state.RefreshCoordinator.MarkRefreshDelayCompleted();

    public BrowserRefreshBeginTransition? TryBeginPendingRefresh(
        string normalizedCurrentPath,
        long watcherGeneration,
        bool isBrowserMode,
        bool isBusy,
        bool isExitPending,
        bool isDisposed)
    {
        if (!isBrowserMode || isBusy || isExitPending || isDisposed || IsPassiveRefresh)
        {
            return null;
        }

        if (!_state.RefreshCoordinator.TryBeginRefresh(normalizedCurrentPath, watcherGeneration, out NavigationRefreshBatch? batch))
        {
            if (_state.RefreshCoordinator.ShouldDiscardPending(normalizedCurrentPath, watcherGeneration))
            {
                ClearPendingRefresh();
            }
            return null;
        }

        string reason = batch!.EventCount > BrowserRefreshConstants.ExternalDirectoryRefreshBulkThreshold
            ? $"Bulk({batch.EventCount})"
            : string.Join("+", batch.Reasons.OrderBy(static value => value));
        return new BrowserRefreshBeginTransition(batch, reason);
    }

    public BrowserRefreshCompletion CompleteRefresh(long elapsedMilliseconds)
    {
        _state.LastExternalDirectoryReloadMilliseconds = elapsedMilliseconds;
        _state.RefreshCoordinator.CompleteRefresh();
        return new BrowserRefreshCompletion(
            HasFollowUpPending: IsPending,
            QuietWindowMilliseconds: GetQuietWindowMilliseconds(BrowserRefreshConstants.CurrentDirectoryRefreshDebounceMilliseconds));
    }

    public void ClearPendingRefresh() => _state.RefreshCoordinator.ClearPendingRefresh();

    public void ResetAuditBackoff()
    {
        _state.CountAuditSchedule.ResetForActivity();
    }

    public int GetAuditIntervalMilliseconds(string? currentPath)
    {
        return _state.CountAuditSchedule.GetIntervalMilliseconds(
            !string.IsNullOrWhiteSpace(currentPath) && DirectoryCountAuditService.IsNetworkPath(currentPath));
    }

    public BrowserCountAuditRequest? TryBeginCountAudit(
        string? currentPath,
        bool showHiddenFiles,
        bool isPassiveRefresh,
        bool isExitPending,
        bool isDisposed,
        bool isApplying)
    {
        if (!isPassiveRefresh || isExitPending || isDisposed || isApplying || string.IsNullOrWhiteSpace(currentPath))
        {
            return null;
        }
        if (!_state.CountAuditGate.TryEnter())
        {
            return null;
        }

        CancelCountAudit();
        var cts = new CancellationTokenSource();
        _state.CountAuditCancellation = cts;
        return new BrowserCountAuditRequest(
            currentPath,
            showHiddenFiles,
            _state.CurrentDirectoryWatcherGeneration,
            _state.DirectoryNavigationGeneration,
            _state.DirectoryContentGeneration,
            cts.Token);
    }

    public void CompleteCountAudit()
    {
        _state.CountAuditGate.Exit();
    }

    public BrowserCountAuditApplyTransition ApplyCountAudit(
        string currentPath,
        long watcherGeneration,
        int rawDirectoryEntryCount)
    {
        bool changed = _state.RefreshCoordinator.ApplyCountAudit(
            currentPath,
            watcherGeneration,
            rawDirectoryEntryCount);
        _state.CountAuditSchedule.RecordResult(changed);
        return new BrowserCountAuditApplyTransition(changed, GetAuditIntervalMilliseconds(currentPath));
    }

    public void CancelCountAudit()
    {
        _state.CountAuditCancellation?.Cancel();
        _state.CountAuditCancellation?.Dispose();
        _state.CountAuditCancellation = null;
    }

    public BrowserWatcherPlan PrepareWatcher(
        string? currentPath,
        SortKind sortKind,
        bool featureEnabled,
        NotifyFilters? currentNotifyFilter)
    {
        if (!featureEnabled)
        {
            return BrowserWatcherPlan.Disable;
        }

        string normalizedCurrentPath = NavigationService.NormalizeDirectoryForCompare(currentPath ?? string.Empty);
        string normalizedWatcherPath = NavigationService.NormalizeDirectoryForCompare(_state.CurrentDirectoryWatcherPath ?? string.Empty);
        NotifyFilters desiredFilter = DirectoryWatcherNotifyFilterPolicy.ForSort(sortKind);
        if (!string.IsNullOrWhiteSpace(normalizedCurrentPath) &&
            string.Equals(normalizedCurrentPath, normalizedWatcherPath, StringComparison.OrdinalIgnoreCase) &&
            currentNotifyFilter == desiredFilter)
        {
            return BrowserWatcherPlan.Keep;
        }

        if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
        {
            return BrowserWatcherPlan.Disable;
        }

        return new BrowserWatcherPlan(
            BrowserWatcherPlanKind.Create,
            currentPath,
            _state.CurrentDirectoryWatcherGeneration + 1,
            desiredFilter);
    }

    public void CommitWatcher(string path, long generation)
    {
        _state.CurrentDirectoryWatcherGeneration = generation;
        _state.CurrentDirectoryWatcherPath = path;
    }

    public void ResetWatcherState()
    {
        _state.CurrentDirectoryWatcherGeneration++;
        _state.CurrentDirectoryWatcherPath = null;
        _state.RefreshCoordinator.State.ResetDirectoryBaseline();
        ClearPendingRefresh();
    }
}

internal static class BrowserRefreshConstants
{
    public const int CurrentDirectoryRefreshDebounceMilliseconds = 750;
    public const int ExternalDirectoryRefreshBulkThreshold = 64;
}

internal readonly record struct BrowserRefreshQueueTransition(
    bool Accepted,
    bool RestartQuietTimer,
    bool ProcessImmediately,
    int QuietWindowMilliseconds,
    bool ShowPassiveRefreshHint,
    int AuditIntervalMilliseconds);

internal readonly record struct BrowserRefreshBeginTransition(
    NavigationRefreshBatch Batch,
    string Reason);

internal readonly record struct BrowserRefreshCompletion(
    bool HasFollowUpPending,
    int QuietWindowMilliseconds);

internal readonly record struct BrowserCountAuditRequest(
    string CurrentPath,
    bool ShowHiddenFiles,
    long WatcherGeneration,
    long NavigationGeneration,
    long ContentGeneration,
    CancellationToken Token);

internal readonly record struct BrowserCountAuditApplyTransition(
    bool Changed,
    int NextIntervalMilliseconds);

internal enum BrowserWatcherPlanKind
{
    Keep,
    Create,
    Disable
}

internal readonly record struct BrowserWatcherPlan(
    BrowserWatcherPlanKind Kind,
    string? Path,
    long Generation,
    NotifyFilters NotifyFilter)
{
    public static BrowserWatcherPlan Keep => new(BrowserWatcherPlanKind.Keep, null, 0, 0);
    public static BrowserWatcherPlan Disable => new(BrowserWatcherPlanKind.Disable, null, 0, 0);
}
