using System.Collections;
using System.Collections.Generic;
using System.IO;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

internal enum BrowserSelectionMarkChangeKind
{
    None,
    Added,
    Removed,
    Cleared,
    Restored
}

internal readonly record struct BrowserSelectionMarkTransition(
    BrowserSelectionMarkChangeKind ChangeKind,
    int ChangedCount,
    bool HasMarks)
{
    public bool Changed => ChangeKind != BrowserSelectionMarkChangeKind.None && ChangedCount > 0;
}

/// <summary>
/// Browser selection and mark stateの唯一のmutation owner。
/// ListViewItemは受け取らず、shellが解決したcurrent pathだけを扱う。
/// </summary>
internal sealed class BrowserSelectionApplicationCoordinator : IReadOnlyCollection<string>
{
    private readonly MarkSelectionState _marks = new();

    public MarkSummaryCacheState MarkSummaryCacheState { get; private set; } = MarkSummaryCacheState.Invalid;
    public string MarkSummaryCache { get; private set; } = string.Empty;
    public string MarkSummaryCachePath { get; private set; } = string.Empty;
    public int MarkSummaryCacheCount { get; private set; } = -1;
    public string MarkSummaryCacheSizeText { get; private set; } = string.Empty;
    public string MarkSummaryCacheCompact { get; private set; } = string.Empty;
    public long MarkSummaryCacheTotalSize { get; private set; }
    public int MarkSummaryCacheFileCount { get; private set; }
    public int MarkSummaryCacheOutsideCount { get; private set; }
    public bool RecentMultiMarkIntentActive { get; private set; }
    public string RecentMultiMarkIntentDirectory { get; private set; } = string.Empty;
    public int RecentMultiMarkIntentCursorIndex { get; private set; } = -1;
    public IReadOnlyList<string> RecentMultiMarkIntentMarkedPaths { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string>? PendingEscExitPersistedMarks { get; private set; }

    public int Count => _marks.Count;

    public bool Any() => _marks.Any();

    public bool Contains(string? path) => _marks.Contains(path);

    public void RememberPathKind(string? path, bool isDirectory) => _marks.RememberPathKind(path, isDirectory);

    public IReadOnlyDictionary<string, bool> BuildPassivePathKinds(
        IReadOnlyDictionary<string, bool>? currentSnapshotPathKinds,
        IEnumerable<string>? additionalPaths = null) =>
        _marks.BuildKnownPathKinds(currentSnapshotPathKinds, additionalPaths);

    public bool Add(string? path) => _marks.Add(path);

    public int AddRange(IEnumerable<string> paths) => _marks.AddRange(paths);

    public bool Remove(string? path) => _marks.Remove(path);

    public int RemoveRange(IEnumerable<string> paths) => _marks.RemoveRange(paths);

    public void Clear() => _marks.Clear();

    public void Restore(IEnumerable<string>? paths) => _marks.Restore(paths);

    public BrowserSelectionMarkTransition Toggle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new BrowserSelectionMarkTransition(
                BrowserSelectionMarkChangeKind.None,
                0,
                _marks.Any());
        }

        bool wasMarked = _marks.Contains(path);
        bool changed = wasMarked ? _marks.Remove(path) : _marks.Add(path);
        return new BrowserSelectionMarkTransition(
            changed
                ? (wasMarked ? BrowserSelectionMarkChangeKind.Removed : BrowserSelectionMarkChangeKind.Added)
                : BrowserSelectionMarkChangeKind.None,
            changed ? 1 : 0,
            _marks.Any());
    }

    public BrowserSelectionMarkTransition ClearSelection()
    {
        int count = _marks.Count;
        if (count == 0)
        {
            return new BrowserSelectionMarkTransition(
                BrowserSelectionMarkChangeKind.None,
                0,
                false);
        }

        _marks.Clear();
        return new BrowserSelectionMarkTransition(
            BrowserSelectionMarkChangeKind.Cleared,
            count,
            false);
    }

    public BrowserSelectionMarkTransition RestoreSelection(IEnumerable<string>? paths)
    {
        int beforeCount = _marks.Count;
        _marks.Restore(paths);
        int changedCount = Math.Abs(_marks.Count - beforeCount);
        return new BrowserSelectionMarkTransition(
            changedCount > 0 ? BrowserSelectionMarkChangeKind.Restored : BrowserSelectionMarkChangeKind.None,
            changedCount,
            _marks.Any());
    }

    public BrowserSelectionMarkTransition RestoreSelectionWithKnownKinds(IEnumerable<MarkPathKind>? paths)
    {
        int beforeCount = _marks.Count;
        _marks.RestoreWithKnownKinds(paths);
        int changedCount = Math.Abs(_marks.Count - beforeCount);
        return new BrowserSelectionMarkTransition(
            changedCount > 0 ? BrowserSelectionMarkChangeKind.Restored : BrowserSelectionMarkChangeKind.None,
            changedCount,
            _marks.Any());
    }

    public bool TryPrepareMarkSummaryDelta(
        string path,
        bool adding,
        string currentPath,
        out MarkSummaryDelta delta)
    {
        delta = default;
        string currentDir = NavigationService.NormalizeDirectoryForCompare(currentPath);
        if (NetworkPathResolutionPolicy.IsAuxiliaryResolutionDeferred(currentPath) ||
            NetworkPathResolutionPolicy.IsUncPath(path))
        {
            return false;
        }

        MarkSummaryExactCache current = _marks.Count == 0
            ? new MarkSummaryExactCache(0, 0, 0, 0)
            : MarkSummaryCacheState == MarkSummaryCacheState.Complete &&
              MarkSummaryCacheCount == _marks.Count &&
              string.Equals(MarkSummaryCachePath, currentDir, StringComparison.OrdinalIgnoreCase)
                ? new MarkSummaryExactCache(
                    MarkSummaryCacheTotalSize,
                    MarkSummaryCacheFileCount,
                    MarkSummaryCacheOutsideCount,
                    MarkSummaryCacheCount)
                : default;
        if (_marks.Count > 0 && current.MarkCount != _marks.Count)
        {
            return false;
        }

        if (File.Exists(path))
        {
            try
            {
                long fileSize = new FileInfo(path).Length;
                bool isOutside = !string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                    currentDir,
                    StringComparison.OrdinalIgnoreCase);
                int direction = adding ? 1 : -1;
                delta = new MarkSummaryDelta(
                    checked(fileSize * direction),
                    direction,
                    isOutside ? direction : 0,
                    direction);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        bool directoryOutside = !string.Equals(
            NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
            currentDir,
            StringComparison.OrdinalIgnoreCase);
        int directoryDirection = adding ? 1 : -1;
        delta = new MarkSummaryDelta(
            0,
            0,
            directoryOutside ? directoryDirection : 0,
            directoryDirection);
        return true;
    }

    public bool TryApplyMarkSummaryDelta(MarkSummaryDelta delta, string currentPath)
    {
        MarkSummaryExactCache current = _marks.Count == 0
            ? new MarkSummaryExactCache(0, 0, 0, 0)
            : new MarkSummaryExactCache(
                MarkSummaryCacheTotalSize,
                MarkSummaryCacheFileCount,
                MarkSummaryCacheOutsideCount,
                MarkSummaryCacheCount);
        if (!MarkSummaryDeltaGate.TryApply(current, delta, _marks.Count, out MarkSummaryExactCache updated))
        {
            return false;
        }

        string currentDir = NavigationService.NormalizeDirectoryForCompare(currentPath);
        MarkSummaryCacheTotalSize = updated.TotalSize;
        MarkSummaryCacheFileCount = updated.FileCount;
        MarkSummaryCacheOutsideCount = updated.OutsideCount;
        MarkSummaryCache = updated.MarkCount == 0
            ? string.Empty
            : $"Mark:{updated.MarkCount,3} ({updated.FileCount} Files)" +
              (updated.OutsideCount > 0 ? $" Out:{updated.OutsideCount}" : string.Empty) +
              $" {FileOperationService.FormatSize(updated.TotalSize)}";
        MarkSummaryCacheCount = updated.MarkCount;
        MarkSummaryCacheSizeText = updated.MarkCount == 0
            ? string.Empty
            : FileOperationService.FormatSize(updated.TotalSize);
        MarkSummaryCacheCompact = updated.MarkCount == 0
            ? string.Empty
            : $"Mark: {updated.MarkCount} MarkSize: {MarkSummaryCacheSizeText}";
        MarkSummaryCachePath = currentDir;
        MarkSummaryCacheState = MarkSummaryCacheState.Complete;
        return true;
    }

    public void InvalidateMarkSummaryCache()
    {
        MarkSummaryCacheState = MarkSummaryCacheState.Invalid;
    }

    public void SetMarkSummaryComplete(
        string currentDir,
        IReadOnlyList<string> paths,
        MarkSummaryBuildResult result)
    {
        MarkSummaryCacheTotalSize = result.TotalSize;
        MarkSummaryCacheFileCount = result.FileCount;
        MarkSummaryCacheOutsideCount = result.OutsideCount;
        string size = FileOperationService.FormatSize(result.TotalSize);
        string outside = result.OutsideCount > 0 ? $" Out:{result.OutsideCount}" : string.Empty;
        MarkSummaryCache = $"Mark:{paths.Count,3} ({result.FileCount} Files){outside} {size}";
        MarkSummaryCacheCount = paths.Count;
        MarkSummaryCacheSizeText = size;
        MarkSummaryCacheCompact = $"Mark: {paths.Count} MarkSize: {size}";
        MarkSummaryCachePath = currentDir;
        MarkSummaryCacheState = MarkSummaryCacheState.Complete;
    }

    public void SetMarkSummaryCountOnly(string currentDir)
    {
        MarkSummaryCache = Count > 0 ? $"Mark:{Count,3}" : string.Empty;
        MarkSummaryCacheCount = Count;
        MarkSummaryCacheSizeText = string.Empty;
        MarkSummaryCacheCompact = Count > 0 ? $"Mark: {Count} MarkSize: ?" : string.Empty;
        MarkSummaryCachePath = currentDir;
        MarkSummaryCacheState = Count > 0 ? MarkSummaryCacheState.CountOnly : MarkSummaryCacheState.Complete;
    }

    public void SetMarkSummaryZero(string currentDir)
    {
        MarkSummaryCacheTotalSize = 0;
        MarkSummaryCacheFileCount = 0;
        MarkSummaryCacheOutsideCount = 0;
        MarkSummaryCache = string.Empty;
        MarkSummaryCacheCount = 0;
        MarkSummaryCacheSizeText = string.Empty;
        MarkSummaryCacheCompact = string.Empty;
        MarkSummaryCachePath = currentDir;
        MarkSummaryCacheState = MarkSummaryCacheState.Complete;
    }

    public void SetCompleteMarkSummary(string currentDir, MarkSummaryExactCache updated)
    {
        MarkSummaryCacheTotalSize = updated.TotalSize;
        MarkSummaryCacheFileCount = updated.FileCount;
        MarkSummaryCacheOutsideCount = updated.OutsideCount;
        MarkSummaryCache = updated.MarkCount == 0
            ? string.Empty
            : $"Mark:{updated.MarkCount,3} ({updated.FileCount} Files)" +
              (updated.OutsideCount > 0 ? $" Out:{updated.OutsideCount}" : string.Empty) +
              $" {FileOperationService.FormatSize(updated.TotalSize)}";
        MarkSummaryCacheCount = updated.MarkCount;
        MarkSummaryCacheSizeText = updated.MarkCount == 0
            ? string.Empty
            : FileOperationService.FormatSize(updated.TotalSize);
        MarkSummaryCacheCompact = updated.MarkCount == 0
            ? string.Empty
            : $"Mark: {updated.MarkCount} MarkSize: {MarkSummaryCacheSizeText}";
        MarkSummaryCachePath = currentDir;
        MarkSummaryCacheState = MarkSummaryCacheState.Complete;
    }

    public void SetPendingEscExitPersistedMarks(IEnumerable<string>? paths)
    {
        PendingEscExitPersistedMarks = paths?
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void SetRecentMultiMarkIntent(string directory, int cursorIndex, IReadOnlyList<string> markedPaths)
    {
        RecentMultiMarkIntentActive = true;
        RecentMultiMarkIntentDirectory = directory;
        RecentMultiMarkIntentCursorIndex = cursorIndex;
        RecentMultiMarkIntentMarkedPaths = markedPaths.ToList();
    }

    public void ClearRecentMultiMarkIntent()
    {
        RecentMultiMarkIntentActive = false;
        RecentMultiMarkIntentDirectory = string.Empty;
        RecentMultiMarkIntentCursorIndex = -1;
        RecentMultiMarkIntentMarkedPaths = Array.Empty<string>();
    }

    public IReadOnlyList<string> Snapshot() => _marks.Snapshot();

    public int CountOutsideCurrentDirectory(string currentPath)
    {
        string currentDir = NavigationService.NormalizeDirectoryForCompare(currentPath);
        int outsideCount = 0;
        foreach (string path in _marks)
        {
            string? parentDir = Path.GetDirectoryName(path);
            if (!string.Equals(
                NavigationService.NormalizeDirectoryForCompare(parentDir ?? string.Empty),
                currentDir,
                StringComparison.OrdinalIgnoreCase))
            {
                outsideCount++;
            }
        }
        return outsideCount;
    }

    public SelectionResult Resolve(string? currentPath, bool isParent)
    {
        if (_marks.Any())
        {
            return new SelectionResult(_marks, true);
        }

        return !isParent && !string.IsNullOrWhiteSpace(currentPath)
            ? new SelectionResult(new[] { currentPath }, false)
            : SelectionResult.Empty;
    }

    public IEnumerator<string> GetEnumerator() => _marks.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
