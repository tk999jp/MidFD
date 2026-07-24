using System;
using System.Collections.Generic;

namespace MidFD.Models;

public class NavigationRefreshRequestState
{
    public bool IsPending { get; set; }
    public string? TargetPath { get; set; }
    public string Reason { get; set; } = "外部変更";
    public HashSet<string> Reasons { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int EventCount { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public bool DelayScheduled { get; set; }
    public bool DelayCompleted { get; set; }
    public bool IsApplying { get; set; }
    public long WatcherGeneration { get; set; }
    public bool IsPassiveRefresh { get; set; }
    public int RawDirectoryEntryCount { get; set; }
    public int FilteredTotalItemCount { get; set; }

    public void Clear()
    {
        ClearPendingEventState();
    }

    public NavigationRefreshBatch Snapshot() => new(
        TargetPath ?? string.Empty, WatcherGeneration, Reasons.ToArray(), EventCount, ExceptionType, ExceptionMessage);

    public void ClearPendingEventState()
    {
        IsPending = false;
        TargetPath = null;
        Reason = "外部変更";
        Reasons.Clear();
        EventCount = 0;
        ExceptionType = null;
        ExceptionMessage = null;
        DelayScheduled = false;
        DelayCompleted = false;
    }

    public void ResetDirectoryBaseline()
    {
        RawDirectoryEntryCount = 0;
        FilteredTotalItemCount = 0;
        IsPassiveRefresh = false;
        IsApplying = false;
        WatcherGeneration = 0;
    }

    public void ScheduleRefreshDelay()
    {
        DelayScheduled = true;
        DelayCompleted = false;
    }

    public void CompleteRefreshDelay()
    {
        DelayCompleted = true;
    }
}
