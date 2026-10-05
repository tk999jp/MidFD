using System;

namespace MidFD.Models;

public enum BrowserTabVisitHistoryListDirection
{
    Back,
    Current,
    Forward
}

public sealed record BrowserTabVisitHistoryListItem(
    BrowserTabVisitHistoryListDirection Direction,
    int Depth,
    string CategoryId,
    Guid TabId,
    string CategoryDisplayName,
    string TabDisplayName,
    string CurrentPath)
{
    public BrowserTabVisitLocation Location => new(CategoryId, TabId);

    public bool IsSelectable => Direction != BrowserTabVisitHistoryListDirection.Current;
}

public sealed record BrowserTabVisitHistoryDialogResult(
    BrowserTabVisitHistoryListDirection Direction,
    int Depth);
