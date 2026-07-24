using System;
using System.Collections.Generic;
using MidFD.Services;
using System.Text.Json.Serialization;

namespace MidFD.Models;

public sealed class BrowserTabState
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string CurrentPath { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public string StartupPath { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }
    public TabFilterLockState FilterLock { get; set; } = new();
    public List<string> MarkedPaths { get; set; } = new();
    [JsonIgnore]
    public bool MarksDirty { get; set; }
    public NavigationService.NavigationSnapshot Navigation { get; set; } = new();
    public string? FocusTargetName { get; set; }
    public int CursorIndex { get; set; }
    public int ColumnCount { get; set; } = 3;
    public SortKind SortKind { get; set; } = SortKind.Name;
    public bool SortAscending { get; set; } = true;

    public BrowserTabState Clone()
    {
        return new BrowserTabState
        {
            Id = Id,
            Title = Title,
            CurrentPath = CurrentPath,
            IsLocked = IsLocked,
            StartupPath = StartupPath,
            IsReadOnly = IsReadOnly,
            FilterLock = FilterLock?.Clone() ?? new TabFilterLockState(),
            MarkedPaths = new List<string>(MarkedPaths),
            MarksDirty = MarksDirty,
            Navigation = Navigation,
            FocusTargetName = FocusTargetName,
            CursorIndex = CursorIndex,
            ColumnCount = ColumnCount,
            SortKind = SortKind,
            SortAscending = SortAscending
        };
    }
}
