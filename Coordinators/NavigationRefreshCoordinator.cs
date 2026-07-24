using System;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Coordinators;

public class NavigationRefreshCoordinator
{
    private readonly NavigationRefreshRequestState _state = new();

    public NavigationRefreshRequestState State => _state;

    public void ConfigureDirectoryCost(int rawDirectoryEntryCount, int filteredTotalItemCount, long itemBuildMilliseconds)
    {
        _state.RawDirectoryEntryCount = rawDirectoryEntryCount;
        _state.FilteredTotalItemCount = filteredTotalItemCount;
        _state.IsPassiveRefresh = rawDirectoryEntryCount >= 10_000 || itemBuildMilliseconds >= 750;
    }

    public bool ApplyCountAudit(string currentPath, long watcherGeneration, int rawDirectoryEntryCount)
    {
        if ((_state.TargetPath != null && !string.Equals(_state.TargetPath, currentPath, StringComparison.OrdinalIgnoreCase)) ||
            (_state.WatcherGeneration != 0 && _state.WatcherGeneration != watcherGeneration && _state.IsPending))
        {
            return false;
        }
        if (!_state.IsPassiveRefresh || rawDirectoryEntryCount == _state.RawDirectoryEntryCount)
        {
            return false;
        }

        _state.IsPending = true;
        _state.TargetPath = currentPath;
        _state.WatcherGeneration = watcherGeneration;
        _state.Reason = "CountAudit";
        _state.Reasons.Add("CountAudit");
        _state.EventCount++;
        _state.RawDirectoryEntryCount = rawDirectoryEntryCount;
        return true;
    }

    public void QueueRefresh(
        string watchedDirectoryPath,
        string reason,
        string normalizedWatchedPath,
        string normalizedCurrentPath,
        string normalizedWatcherPath,
        long watcherGeneration,
        long activeWatcherGeneration,
        Exception? exception,
        System.Windows.Forms.Timer debounceTimer)
    {
        if (string.IsNullOrWhiteSpace(normalizedWatchedPath) ||
            !string.Equals(normalizedWatchedPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(normalizedWatchedPath, normalizedWatcherPath, StringComparison.OrdinalIgnoreCase) ||
            watcherGeneration != activeWatcherGeneration)
        {
            return;
        }

        _state.IsPending = true;
        _state.TargetPath = watchedDirectoryPath;
        _state.WatcherGeneration = watcherGeneration;
        _state.Reason = reason;
        _state.Reasons.Add(reason);
        _state.EventCount++;
        if (exception != null)
        {
            _state.ExceptionType = exception.GetType().Name;
            _state.ExceptionMessage = exception.Message;
        }

        if (!_state.IsPassiveRefresh && _state.EventCount < 64)
        {
            _state.ScheduleRefreshDelay();
            debounceTimer.Stop();
            debounceTimer.Start();
        }
        else if (!_state.IsPassiveRefresh)
        {
            _state.CompleteRefreshDelay();
            debounceTimer.Stop();
        }
    }

    public void ClearPendingRefresh()
    {
        _state.ClearPendingEventState();
    }

    public void MarkRefreshDelayCompleted() => _state.CompleteRefreshDelay();

    public bool ShouldDiscardPending(string normalizedCurrentPath, long watcherGeneration)
    {
        return _state.IsPending && !_state.IsApplying &&
            (_state.WatcherGeneration != watcherGeneration ||
             !string.Equals(_state.TargetPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase));
    }

    public bool CanProcessRefresh(string currentNormalizedPath)
    {
        if (!_state.IsPending || _state.IsApplying)
        {
            return false;
        }

        return string.Equals(_state.TargetPath, currentNormalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryBeginRefresh(string normalizedCurrentPath, long watcherGeneration, out NavigationRefreshBatch? batch)
    {
        batch = null;
        if (_state.IsPassiveRefresh || !_state.IsPending || _state.IsApplying || !_state.DelayCompleted ||
            _state.WatcherGeneration != watcherGeneration ||
            !string.Equals(_state.TargetPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        batch = _state.Snapshot();
        _state.ClearPendingEventState();
        _state.IsApplying = true;
        return true;
    }

    public void CompleteRefresh() => _state.IsApplying = false;

    public string BuildExternalDirectoryRefreshReason(int bulkThreshold)
    {
        int eventCount = _state.EventCount;
        if (eventCount > bulkThreshold)
        {
            return $"Bulk({eventCount})";
        }
        if (_state.Reasons.Count > 0)
        {
            return string.Join("+", _state.Reasons.OrderBy(static value => value));
        }
        return _state.Reason;
    }
}
