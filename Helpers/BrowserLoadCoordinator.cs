using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

/// <summary>
/// Browser 一覧の directory load 前後パイプラインを組み立てる。
/// MainForm 側には UI 最終適用と例外ハンドリングを残す。
/// </summary>
public sealed class BrowserLoadCoordinator
{
    public enum SnapshotPolicy
    {
        RebuildSnapshot,
        ReuseSnapshot
    }

    private DirectorySnapshot? _snapshot;

    private sealed record SnapshotEntry(DirectoryInfo? Directory, FileInfo? File);
    private sealed record DirectorySnapshot(
        SnapshotKey Key,
        DirectoryInfo Directory,
        DirectoryResult Result,
        IReadOnlyList<SnapshotEntry> Entries,
        IReadOnlyDictionary<string, bool> PathKinds);

    private sealed record SnapshotKey(
        string Path,
        string FilterPattern,
        bool FilterUseRegex,
        bool ShowHiddenFiles,
        SortKind SortKind,
        bool SortAscending,
        string FilterLockSignature);

    public sealed record DirectoryLoadRequest(
        string TargetPath,
        string? FocusTargetName,
        bool IsHistoryNavigation,
        bool SuppressRecent,
        string CurrentPath,
        int LastIndex,
        string? CurrentItemFullName,
        string FilterPattern,
        bool FilterUseRegex,
        bool ShowHiddenFiles,
        SortKind SortKind,
        bool SortAscending,
        TabFilterLockState? FilterLock,
        string? DateFormat,
        string? SizeFormat,
        bool ShowDirectoryMarker,
        int ItemsPerPage,
        SnapshotPolicy SnapshotPolicy);

    public sealed record DirectoryLoadResult(
        string NewPath,
        string PreviousPath,
        bool IsReload,
        string? FocusTargetName,
        int LastIndex,
        bool IsHistoryNavigation,
        bool SuppressRecent,
        IReadOnlyList<BrowserItemData> Items,
        int TotalItemCount,
        int RawDirectoryEntryCount,
        int PageStartIndex,
        long EnumerationAndSortMilliseconds,
        long ItemBuildMilliseconds,
        int GeneratedItemCount,
        bool ReusedSnapshot,
        IReadOnlyList<string> StatusMessages)
    {
        public IReadOnlyDictionary<string, bool> SnapshotPathKinds { get; init; } =
            new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

        public bool SnapshotPathKindsAreFresh { get; init; }
    }

    public void InvalidateSnapshot() => _snapshot = null;

    public bool TryGetCurrentSnapshotTargetPaths(
        string expectedPath,
        bool includeDirectories,
        out IReadOnlyList<string> paths)
    {
        paths = Array.Empty<string>();
        DirectorySnapshot? snapshot = _snapshot;
        if (snapshot == null || !string.Equals(
                snapshot.Key.Path,
                new DirectoryInfo(expectedPath).FullName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SnapshotEntry entry in snapshot.Entries)
        {
            string? path = entry.Directory?.FullName ?? entry.File?.FullName;
            if (path == null || (!includeDirectories && entry.File == null))
            {
                continue;
            }
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }
        paths = result;
        return true;
    }

    public bool TryGetCurrentSnapshotPathKinds(
        string expectedPath,
        out IReadOnlyDictionary<string, bool> pathKinds)
    {
        pathKinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        DirectorySnapshot? snapshot = _snapshot;
        if (snapshot == null || !string.Equals(
                snapshot.Key.Path,
                new DirectoryInfo(expectedPath).FullName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        pathKinds = snapshot.PathKinds;
        return true;
    }

    public bool TryFindCurrentSnapshotPrefixIndex(
        string expectedPath,
        string prefix,
        out int globalIndex)
    {
        globalIndex = -1;
        if (string.IsNullOrEmpty(prefix))
        {
            return false;
        }

        DirectorySnapshot? snapshot = _snapshot;
        if (snapshot == null || !string.Equals(
                snapshot.Key.Path,
                new DirectoryInfo(expectedPath).FullName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int index = 0; index < snapshot.Entries.Count; index++)
        {
            SnapshotEntry entry = snapshot.Entries[index];
            string? name = entry.Directory?.Name ?? entry.File?.Name;
            if (name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                globalIndex = index;
                return true;
            }
        }

        return false;
    }

    public DirectoryLoadResult Execute(DirectoryLoadRequest request)
    {
        var dirInfo = new DirectoryInfo(request.TargetPath);
        string newPath = dirInfo.FullName;
        string previousPath = request.CurrentPath;
        bool isReload = string.Equals(request.CurrentPath, newPath, StringComparison.OrdinalIgnoreCase);
        string? focusTargetName = request.FocusTargetName;

        if (isReload && request.SnapshotPolicy == SnapshotPolicy.RebuildSnapshot && string.IsNullOrEmpty(focusTargetName))
        {
            focusTargetName = request.CurrentItemFullName;
        }

        SnapshotKey key = CreateSnapshotKey(request);
        if (request.SnapshotPolicy == SnapshotPolicy.RebuildSnapshot)
        {
            _snapshot = null;
        }
        bool reusedSnapshot = _snapshot is not null && _snapshot.Key == key;
        DirectoryResult result;
        IReadOnlyList<SnapshotEntry> entries;
        long enumerationMilliseconds;
        if (reusedSnapshot)
        {
            result = _snapshot!.Result;
            entries = _snapshot.Entries;
            enumerationMilliseconds = 0;
        }
        else
        {
            var enumerationStopwatch = Stopwatch.StartNew();
            result = DirectoryProvider.GetSortedEntries(
                request.TargetPath,
                request.FilterPattern,
                request.FilterUseRegex,
                request.ShowHiddenFiles,
                request.SortKind,
                request.SortAscending,
                request.FilterLock);
            enumerationStopwatch.Stop();
            enumerationMilliseconds = enumerationStopwatch.ElapsedMilliseconds;
            entries = BuildSnapshotEntries(
                result,
                dirInfo,
                ShouldIncludeParentEntry(request.TargetPath, request.FilterPattern, request.FilterLock));
            _snapshot = new DirectorySnapshot(
                key,
                dirInfo,
                result,
                entries,
                BuildSnapshotPathKinds(entries));
        }

        int totalItemCount = entries.Count;
        int resolvedFocusIndex = ResolveFocusIndex(entries, focusTargetName);
        int lastIndex = BrowserPageIndex.ClampGlobalIndex(
            resolvedFocusIndex >= 0 ? resolvedFocusIndex : request.LastIndex,
            totalItemCount);
        int pageStart = BrowserPageIndex.GetPageStartForTotal(lastIndex, totalItemCount, request.ItemsPerPage);
        var itemBuildStopwatch = Stopwatch.StartNew();
        List<BrowserItemData> items = BuildItems(entries, request, pageStart);
        itemBuildStopwatch.Stop();
        IReadOnlyList<string> statusMessages = reusedSnapshot
            ? Array.Empty<string>()
            : result.FilterErrorMessages;

        return new DirectoryLoadResult(
            newPath,
            previousPath,
            isReload,
            focusTargetName,
            lastIndex,
            request.IsHistoryNavigation,
            request.SuppressRecent,
            items,
            totalItemCount,
            result.RawDirectoryEntryCount,
            pageStart,
            enumerationMilliseconds,
            itemBuildStopwatch.ElapsedMilliseconds,
            items.Count,
            reusedSnapshot,
            statusMessages)
        {
            SnapshotPathKinds = _snapshot!.PathKinds,
            SnapshotPathKindsAreFresh = !reusedSnapshot
        };
    }

    private static SnapshotKey CreateSnapshotKey(DirectoryLoadRequest request)
    {
        TabFilterLockState? filterLock = request.FilterLock;
        string filterLockSignature = filterLock == null
            ? string.Empty
            : string.Join(
                "|",
                filterLock.Enabled,
                filterLock.ExtensionText,
                string.Join(",", filterLock.IncludeExtensions.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)),
                filterLock.ModifiedFromLocal?.Ticks,
                filterLock.ModifiedToLocal?.Ticks,
                filterLock.GitUnignoredOnly);
        return new SnapshotKey(
            new DirectoryInfo(request.TargetPath).FullName,
            request.FilterPattern,
            request.FilterUseRegex,
            request.ShowHiddenFiles,
            request.SortKind,
            request.SortAscending,
            filterLockSignature);
    }

    internal static bool ShouldIncludeParentEntry(
        string targetPath,
        string filterPattern,
        TabFilterLockState? filterLock)
    {
        return new DirectoryInfo(targetPath).Parent != null
            && !TabFilterLockService.IsActive(filterPattern, filterLock);
    }

    private static IReadOnlyList<SnapshotEntry> BuildSnapshotEntries(
        DirectoryResult result,
        DirectoryInfo dirInfo,
        bool includeParent)
    {
        var entries = new List<SnapshotEntry>(result.RawDirectoryEntryCount + 1);
        if (includeParent)
        {
            entries.Add(new SnapshotEntry(null, null));
        }
        entries.AddRange(result.SelectedDirs.Select(static directory => new SnapshotEntry(directory, null)));
        entries.AddRange(result.SelectedFiles.Select(static file => new SnapshotEntry(null, file)));
        return entries;
    }

    private static IReadOnlyDictionary<string, bool> BuildSnapshotPathKinds(IReadOnlyList<SnapshotEntry> entries)
    {
        var pathKinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (SnapshotEntry entry in entries)
        {
            if (entry.Directory != null)
            {
                pathKinds[entry.Directory.FullName] = true;
            }
            else if (entry.File != null)
            {
                pathKinds[entry.File.FullName] = false;
            }
        }

        return new ReadOnlyDictionary<string, bool>(pathKinds);
    }

    private static int ResolveFocusIndex(IReadOnlyList<SnapshotEntry> entries, string? focusTargetName)
    {
        if (string.IsNullOrWhiteSpace(focusTargetName))
        {
            return -1;
        }
        for (int index = 0; index < entries.Count; index++)
        {
            SnapshotEntry entry = entries[index];
            if (entry.Directory == null && entry.File == null && focusTargetName == "..")
            {
                return index;
            }
            string? fullName = entry.Directory?.FullName ?? entry.File?.FullName;
            string? name = entry.Directory?.Name ?? entry.File?.Name;
            if (string.Equals(fullName, focusTargetName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, focusTargetName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static List<BrowserItemData> BuildItems(
        IReadOnlyList<SnapshotEntry> entries,
        DirectoryLoadRequest request,
        int pageStart)
    {
        var items = new List<BrowserItemData>();
        int pageSize = request.ItemsPerPage > 0 ? request.ItemsPerPage : int.MaxValue;
        int end = Math.Min(entries.Count, pageStart + pageSize);
        for (int logicalIndex = pageStart; logicalIndex < end; logicalIndex++)
        {
            SnapshotEntry entry = entries[logicalIndex];
            if (entry.Directory == null && entry.File == null)
            {
                items.Add(BrowserItemDataFactory.CreateParent());
                continue;
            }
            if (entry.Directory != null)
            {
                items.Add(BrowserItemDataFactory.CreateDirectory(entry.Directory, request.DateFormat, request.ShowDirectoryMarker));
                continue;
            }
            if (entry.File != null)
            {
                items.Add(BrowserItemDataFactory.CreateFile(entry.File, request.DateFormat, request.SizeFormat));
            }
        }

        return items;
    }
}
