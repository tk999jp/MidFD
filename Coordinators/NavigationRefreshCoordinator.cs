using System;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Coordinators;

public class NavigationRefreshCoordinator
{
    private readonly NavigationRefreshRequestState _state = new();

    public NavigationRefreshRequestState State => _state;

    public void QueueRefresh(
        string watchedDirectoryPath,
        string reason,
        string normalizedWatchedPath,
        string normalizedCurrentPath,
        string normalizedWatcherPath,
        Exception? exception,
        System.Windows.Forms.Timer debounceTimer)
    {
        if (string.IsNullOrWhiteSpace(normalizedWatchedPath) ||
            !string.Equals(normalizedWatchedPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(normalizedWatchedPath, normalizedWatcherPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _state.IsPending = true;
        _state.TargetPath = watchedDirectoryPath;
        _state.Reason = reason;
        _state.Reasons.Add(reason);
        _state.EventCount++;

        if (exception != null)
        {
            _state.ExceptionType = exception.GetType().Name;
            _state.ExceptionMessage = exception.Message;
        }

        debounceTimer.Stop();
        debounceTimer.Start();
    }

    public void ClearPendingRefresh()
    {
        _state.Clear();
    }

    public bool CanProcessRefresh(string currentNormalizedPath)
    {
        if (!_state.IsPending || _state.IsApplying)
        {
            return false;
        }

        string pendingNormalized = _state.TargetPath ?? string.Empty;
        // In this method we just return whether it's valid to process.
        // We do not normalize it here if it's already normalized
        return true;
    }

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
