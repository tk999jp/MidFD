using System;
using System.Collections.Generic;
using System.IO;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Browser の application workflow と session state の境界。
/// UI shell は control から値を取り出して request を渡し、状態遷移はここで確定する。
/// </summary>
internal sealed class BrowserApplicationCoordinator : IDisposable
{
    private readonly BrowserSessionState _state;
    private readonly BrowserNavigationCoordinator _navigation = new();

    public BrowserApplicationCoordinator(
        BrowserSessionState state,
        BrowserDirectoryApplicationCoordinator directory)
    {
        _state = state;
        Directory = directory;
        Refresh = new BrowserRefreshApplicationCoordinator(state.NavigationState);
        Workspace = new BrowserWorkspaceApplicationCoordinator(state);
    }

    public BrowserApplicationCoordinator()
        : this(new BrowserSessionState(), null!)
    {
        Directory = new BrowserDirectoryApplicationCoordinator(_state);
    }

    public BrowserDirectoryApplicationCoordinator Directory { get; }
    public BrowserRefreshApplicationCoordinator Refresh { get; }
    public BrowserWorkspaceApplicationCoordinator Workspace { get; }

    public string CreateQuickAccessDisplayName(string path) => QuickAccessService.CreateDisplayName(path);

    public IReadOnlyList<string> GetQuickAccessCategoryNames() =>
        QuickAccessService.GetKnownCategoryNames(Workspace.QuickAccessSnapshot);

    public bool IsAuxiliaryResolutionDeferred(string? path) =>
        NetworkPathResolutionPolicy.IsAuxiliaryResolutionDeferred(path);

    public bool IsUncPath(string? path) => NetworkPathResolutionPolicy.IsUncPath(path);

    public bool TryGetNetworkRoot(string? path, out string root) =>
        NetworkPathResolutionPolicy.TryGetNetworkRoot(path, out root);

    public string GetPathKind(string? path) => NetworkPathResolutionPolicy.GetPathKind(path);

    public string GetPathRoot(string? path) => NetworkPathResolutionPolicy.GetPathRoot(path);

    public void LogMarkSummaryResolutionDeferred(string? currentPath) =>
        NetworkPathResolutionPolicy.LogDecision(
            "NetworkPathResolutionDeferral.Skip",
            "HeaderInfo.MarkSummary",
            "BrowserMarkSummary",
            currentPath,
            usedCached: true,
            resolvedSync: false,
            reason: "unc-path");

    public BrowserSelectionApplicationCoordinator Selection => _state.Selection;
    private NavigationService Navigation => _state.NavigationState.Navigation;
    public string CurrentPath => Navigation.CurrentPath;
    public NavigationService.NavigationSnapshot NavigationSnapshot => Navigation.CaptureState();
    public bool CanGoBack => Navigation.CanGoBack;
    public bool CanGoForward => Navigation.CanGoForward;
    public SortKind CurrentSort => _state.NavigationState.CurrentSort;
    public bool SortAscending => _state.NavigationState.SortAscending;
    public string FilterPattern => _state.NavigationState.FilterPattern;
    public bool FilterUseRegex => _state.NavigationState.FilterUseRegex;
    public int CursorIndex => _state.NavigationState.CursorIndex;
    public int PageStartIndex => _state.NavigationState.PageStartIndex;
    public int ColumnCount => _state.NavigationState.ColumnCount;
    public int TotalItemCount => _state.NavigationState.TotalItemCount;
    public int ItemsPerPage => _state.NavigationState.ItemsPerPage;
    public long DirectoryNavigationGeneration => _state.NavigationState.DirectoryNavigationGeneration;
    public long DirectoryContentGeneration => _state.NavigationState.DirectoryContentGeneration;
    public long CurrentDirectoryWatcherGeneration => _state.NavigationState.CurrentDirectoryWatcherGeneration;
    public string? CurrentDirectoryWatcherPath => _state.NavigationState.CurrentDirectoryWatcherPath;
    public long LastExternalDirectoryReloadMilliseconds => _state.NavigationState.LastExternalDirectoryReloadMilliseconds;
    public bool CurrentDirectoryRefreshRetryPending => _state.NavigationState.CurrentDirectoryRefreshRetryPending;
    public bool IsApplyingDirectoryList => _state.NavigationState.IsApplyingDirectoryList;

    public IReadOnlyList<string> GetBackHistorySnapshot() => Navigation.GetBackHistorySnapshot();
    public IReadOnlyList<string> GetForwardHistorySnapshot() => Navigation.GetForwardHistorySnapshot();
    public string NormalizeDestinationDirectory(string path) => Navigation.NormalizeDestinationDirectory(path);
    public BrowserPathEntryNavigationResult ResolvePathEntry(string? inputPath) =>
        BrowserPathEntryNavigationService.Resolve(inputPath, NormalizeDestinationDirectory);

    public BrowserQuickAccessTransition ApplyQuickAccessResult(
        QuickAccessApplicationAction action,
        QuickAccessStore? updatedStore,
        QuickAccessEntry? selectedEntry)
    {
        BrowserQuickAccessTransition transition = PrepareQuickAccessResult(action, updatedStore, selectedEntry);
        if (action == QuickAccessApplicationAction.Cancel)
        {
            return transition;
        }

        if (updatedStore != null)
        {
            Workspace.SetQuickAccess(updatedStore);
            bool persisted = QuickAccessService.Save(updatedStore);
            transition = transition with { PersistenceSucceeded = persisted };
        }

        return transition;
    }

    public BrowserQuickAccessTransition PrepareQuickAccessResult(
        QuickAccessApplicationAction action,
        QuickAccessStore? updatedStore,
        QuickAccessEntry? selectedEntry)
    {
        if (action == QuickAccessApplicationAction.Cancel)
        {
            return BrowserQuickAccessTransition.Cancelled;
        }

        bool storeUpdated = updatedStore != null;
        if (action == QuickAccessApplicationAction.SaveOnly)
        {
            return new BrowserQuickAccessTransition(storeUpdated, true, null);
        }

        if (selectedEntry == null || string.IsNullOrWhiteSpace(selectedEntry.Path))
        {
            return new BrowserQuickAccessTransition(storeUpdated, false, null);
        }

        return new BrowserQuickAccessTransition(
            storeUpdated,
            false,
            NormalizeDestinationDirectory(selectedEntry.Path));
    }

    public BrowserNavigationCoordinator.DirectoryNavigationRequest? CreateParentNavigationRequest()
    {
        return _navigation.CreateParentNavigationRequest(
            _state.NavigationState.Navigation.CurrentPath);
    }

    public BrowserNavigationCoordinator.DirectoryNavigationRequest CreateNavigationRequest(
        string targetPath,
        string? focusTargetName = null,
        bool isHistoryNavigation = false,
        bool suppressRecent = false)
    {
        return _navigation.CreateDirectoryNavigationRequest(
            targetPath,
            focusTargetName,
            isHistoryNavigation,
            suppressRecent);
    }

    internal string? PeekBack() => _state.NavigationState.Navigation.PeekBack();

    internal string? PeekForward() => _state.NavigationState.Navigation.PeekForward();

    internal void CommitBack(string previousPath) =>
        _state.NavigationState.Navigation.CommitBack(previousPath);

    internal void CommitForward(string previousPath) =>
        _state.NavigationState.Navigation.CommitForward(previousPath);

    public BrowserCursorTransition SetCursorIndex(int requestedIndex, int itemsPerPage)
    {
        int total = _state.NavigationState.TotalItemCount;
        if (total <= 0)
        {
            int previousEmptyState = _state.NavigationState.CursorIndex;
            _state.NavigationState.CursorIndex = Math.Max(0, requestedIndex);
            return new BrowserCursorTransition(
                previousEmptyState,
                _state.NavigationState.CursorIndex,
                false,
                true);
        }

        int previous = _state.NavigationState.CursorIndex;
        int currentPage = itemsPerPage > 0 ? previous / itemsPerPage : 0;
        int next = BrowserPageIndex.ClampGlobalIndex(requestedIndex, total);
        int nextPage = itemsPerPage > 0 ? next / itemsPerPage : 0;
        _state.NavigationState.CursorIndex = next;
        return new BrowserCursorTransition(previous, next, currentPage != nextPage, true);
    }

    public void SetSort(SortKind sortKind, bool ascending)
    {
        _state.NavigationState.CurrentSort = sortKind;
        _state.NavigationState.SortAscending = ascending;
    }

    public void SetColumnCount(int value)
    {
        _state.NavigationState.ColumnCount = Math.Clamp(value, 1, 9);
    }

    public void SetFilter(string pattern, bool useRegex)
    {
        string effectivePattern = pattern ?? string.Empty;
        _state.NavigationState.FilterPattern = effectivePattern;
        _state.NavigationState.FilterUseRegex = useRegex;
        Workspace.SyncActiveFilterState(effectivePattern, useRegex);
    }

    public void SetDirectoryContentGeneration(long value) => _state.NavigationState.DirectoryContentGeneration = value;
    public void IncrementDirectoryContentGeneration() => _state.NavigationState.DirectoryContentGeneration++;
    public void IncrementDirectoryNavigationGeneration() => _state.NavigationState.DirectoryNavigationGeneration++;
    public void SetCurrentDirectoryRefreshRetryPending(bool value) => _state.NavigationState.CurrentDirectoryRefreshRetryPending = value;
    public void SetApplyingDirectoryList(bool value) => _state.NavigationState.IsApplyingDirectoryList = value;

    public void SetCurrentDirectoryWatcherGeneration(long value) => _state.NavigationState.CurrentDirectoryWatcherGeneration = value;
    public void SetLastExternalDirectoryReloadMilliseconds(long value) => _state.NavigationState.LastExternalDirectoryReloadMilliseconds = value;

    public void Dispose() => _state.Dispose();

    public SelectionResult ResolveSelection(
        string? currentItemPath,
        bool isParent,
        SelectionResult? contextOverride = null)
    {
        return contextOverride is { Count: > 0 }
            ? contextOverride
            : Selection.Resolve(currentItemPath, isParent);
    }

    public void RememberCurrentPathKind(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(CurrentPath) ||
            !Directory.TryGetCurrentSnapshotPathKinds(CurrentPath, out IReadOnlyDictionary<string, bool> pathKinds) ||
            !pathKinds.TryGetValue(path, out bool isDirectory))
        {
            return;
        }

        Selection.RememberPathKind(path, isDirectory);
    }

    public IReadOnlyDictionary<string, bool> GetPassivePathKinds(IEnumerable<string>? additionalPaths = null)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return Selection.BuildPassivePathKinds(null, additionalPaths);
        }

        Directory.TryGetCurrentSnapshotPathKinds(
            CurrentPath,
            out IReadOnlyDictionary<string, bool> currentSnapshotPathKinds);
        return Selection.BuildPassivePathKinds(currentSnapshotPathKinds, additionalPaths);
    }

    public BrowserSelectionMarkTransition ToggleMark(string path)
    {
        RememberCurrentPathKind(path);
        return Selection.Toggle(path);
    }

    public int AddMarks(IEnumerable<string> paths)
    {
        List<string> pathList = paths.ToList();
        RememberCurrentPathKinds(pathList);
        return Selection.AddRange(pathList);
    }

    public int RemoveMarks(IEnumerable<string> paths) => Selection.RemoveRange(paths);

    public BrowserSelectionMarkTransition ClearMarks() => Selection.ClearSelection();

    public BrowserSelectionMarkTransition RestoreMarks(IEnumerable<string>? paths)
    {
        List<string> pathList = paths?.ToList() ?? [];
        RememberCurrentPathKinds(pathList);
        return Selection.RestoreSelection(pathList);
    }

    public BrowserSelectionMarkTransition RestoreMarksWithKnownKinds(IEnumerable<MarkPathKind>? paths)
    {
        List<MarkPathKind> pathKinds = paths?.ToList() ?? [];
        return Selection.RestoreSelectionWithKnownKinds(pathKinds);
    }

    public BrowserBulkMarkTransition ToggleBulkMarks(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
        {
            return BrowserBulkMarkTransition.Empty;
        }

        RememberCurrentPathKinds(targets);

        bool allMarked = targets.All(Selection.Contains);
        var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        List<string> nextMarks = allMarked
            ? Selection.Snapshot().Where(path => !targetSet.Contains(path)).ToList()
            : Selection.Snapshot().Concat(targets).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new BrowserBulkMarkTransition(nextMarks, nextMarks.Count != Selection.Count, allMarked);
    }

    public BrowserBulkMarkTransition MarkBulk(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
        {
            return BrowserBulkMarkTransition.Empty;
        }

        RememberCurrentPathKinds(targets);

        List<string> nextMarks = Selection.Snapshot()
            .Concat(targets)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BrowserBulkMarkTransition(nextMarks, nextMarks.Count != Selection.Count, false);
    }

    public BrowserBulkMarkTransition InvertBulkMarks(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
        {
            return BrowserBulkMarkTransition.Empty;
        }

        RememberCurrentPathKinds(targets);

        var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        List<string> nextMarks = Selection.Snapshot()
            .Where(path => !targetSet.Contains(path))
            .Concat(targets.Where(path => !Selection.Contains(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BrowserBulkMarkTransition(
            nextMarks,
            nextMarks.Count != Selection.Count ||
                !nextMarks.SequenceEqual(Selection.Snapshot(), StringComparer.OrdinalIgnoreCase),
            false);
    }

    public bool TryPrepareMarkSummaryDelta(string path, bool adding, out MarkSummaryDelta delta) =>
        Selection.TryPrepareMarkSummaryDelta(
            path,
            adding,
            _state.NavigationState.Navigation.CurrentPath,
            out delta);

    public bool TryApplyMarkSummaryDelta(MarkSummaryDelta delta) =>
        Selection.TryApplyMarkSummaryDelta(
            delta,
            _state.NavigationState.Navigation.CurrentPath);

    public void InvalidateMarkSummaryCache() => Selection.InvalidateMarkSummaryCache();

    public void SetMarkSummaryComplete(
        string currentDir,
        IReadOnlyList<string> paths,
        MarkSummaryBuildResult result) => Selection.SetMarkSummaryComplete(currentDir, paths, result);

    public void SetMarkSummaryCountOnly(string currentDir) => Selection.SetMarkSummaryCountOnly(currentDir);

    public void SetMarkSummaryZero(string currentDir) => Selection.SetMarkSummaryZero(currentDir);

    public void SetCompleteMarkSummary(string currentDir, MarkSummaryExactCache updated) =>
        Selection.SetCompleteMarkSummary(currentDir, updated);

    public void SetPendingEscExitPersistedMarks(IEnumerable<string>? paths) =>
        Selection.SetPendingEscExitPersistedMarks(paths);

    public void SetRecentMultiMarkIntent(
        string directory,
        int cursorIndex,
        IReadOnlyList<string> markedPaths) => Selection.SetRecentMultiMarkIntent(directory, cursorIndex, markedPaths);

    public void ClearRecentMultiMarkIntent() => Selection.ClearRecentMultiMarkIntent();

    public int CountMarksOutsideCurrentDirectory(string currentDirectory) =>
        Selection.CountOutsideCurrentDirectory(currentDirectory);

    private void RememberCurrentPathKinds(IEnumerable<string> paths)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        if (!Directory.TryGetCurrentSnapshotPathKinds(
                CurrentPath,
                out IReadOnlyDictionary<string, bool> pathKinds))
        {
            return;
        }

        foreach (string path in paths)
        {
            if (pathKinds.TryGetValue(path, out bool isDirectory))
            {
                Selection.RememberPathKind(path, isDirectory);
            }
        }
    }

    public BrowserNavigationDecision PrepareNavigation(
        BrowserNavigationCoordinator.DirectoryNavigationRequest? request,
        int maxTabCount)
    {
        if (request == null)
        {
            return BrowserNavigationDecision.NotRequested;
        }
        if (!System.IO.Directory.Exists(request.TargetPath))
        {
            return new BrowserNavigationDecision(
                BrowserNavigationDecisionKind.DirectoryMissing,
                request,
                BrowserLocationChangeTransition.Denied);
        }

        BrowserLocationChangeTransition locationChange = Workspace.PrepareLocationChange(
            request.TargetPath,
            _state.NavigationState.Navigation.CurrentPath,
            maxTabCount);
        return new BrowserNavigationDecision(
            locationChange == BrowserLocationChangeTransition.Denied
                ? BrowserNavigationDecisionKind.Blocked
                : BrowserNavigationDecisionKind.Ready,
            request,
            locationChange);
    }

    public BrowserLoadCoordinator.DirectoryLoadRequest CreateDirectoryLoadRequest(
        string targetPath,
        string? focusTargetName,
        bool isHistoryNavigation,
        bool suppressRecent,
        BrowserDirectoryLoadOptions options,
        int? lastIndexOverride = null,
        bool useCurrentItemFullName = true)
    {
        return new BrowserLoadCoordinator.DirectoryLoadRequest(
            targetPath,
            focusTargetName,
            isHistoryNavigation,
            suppressRecent,
            _state.NavigationState.Navigation.CurrentPath,
            lastIndexOverride ?? _state.NavigationState.CursorIndex,
            useCurrentItemFullName ? options.CurrentItemFullName : null,
            options.FilterPattern ?? _state.NavigationState.FilterPattern,
            options.FilterUseRegex ?? _state.NavigationState.FilterUseRegex,
            options.ShowHiddenFiles,
            options.SortKind ?? _state.NavigationState.CurrentSort,
            options.SortAscending ?? _state.NavigationState.SortAscending,
            options.FilterLock,
            options.DateFormat,
            options.SizeFormat,
            options.ShowDirectoryMarker,
            options.ItemsPerPage,
            options.SnapshotPolicy);
    }

    public BrowserDirectoryLoadOptions CreateTargetMaterializationOptions(
        BrowserTabState targetState,
        BrowserDirectoryLoadOptions sourceOptions)
    {
        int targetColumnCount = Math.Clamp(targetState.ColumnCount, 1, 9);
        BrowserLayoutProjectionInput? targetLayoutInput = sourceOptions.LayoutProjectionInput;
        if (targetLayoutInput is { } layoutInput)
        {
            bool targetFilterActive = TabFilterLockService.IsActive(
                targetState.FilterPattern,
                targetState.FilterLock);
            targetLayoutInput = layoutInput with
            {
                LeadingPresentationSlotCount = BrowserLayoutProjection.GetLeadingPresentationSlotCount(
                    targetFilterActive)
            };
        }

        int targetItemsPerPage = targetLayoutInput?.GetItemsPerPage(targetColumnCount)
            ?? sourceOptions.ItemsPerPage;
        return sourceOptions with
        {
            CurrentItemFullName = null,
            FilterLock = targetState.FilterLock?.Clone() ?? new TabFilterLockState(),
            FilterPattern = targetState.FilterPattern,
            FilterUseRegex = targetState.FilterUseRegex,
            ItemsPerPage = targetItemsPerPage,
            LayoutProjectionInput = targetLayoutInput,
            SortKind = targetState.SortKind,
            SortAscending = targetState.SortAscending
        };
    }

    public BrowserLoadCoordinator.DirectoryLoadRequest CreateTargetDirectoryLoadRequest(
        string targetPath,
        BrowserTabState targetState,
        BrowserDirectoryLoadOptions targetOptions,
        bool isHistoryNavigation,
        bool suppressRecent)
    {
        return CreateDirectoryLoadRequest(
            targetPath,
            targetState.FocusTargetName,
            isHistoryNavigation,
            suppressRecent,
            targetOptions,
            lastIndexOverride: Math.Max(0, targetState.CursorIndex),
            useCurrentItemFullName: false);
    }
}

internal enum QuickAccessApplicationAction
{
    Cancel,
    Navigate,
    SaveOnly
}

internal sealed record BrowserQuickAccessTransition(
    bool StoreUpdated,
    bool SaveOnly,
    string? NavigationPath,
    bool PersistenceSucceeded = true)
{
    public static BrowserQuickAccessTransition Cancelled { get; } = new(false, false, null);
}

internal readonly record struct BrowserCursorTransition(
    int PreviousIndex,
    int CurrentIndex,
    bool PageChanged,
    bool Applied);

internal readonly record struct BrowserBulkMarkTransition(
    IReadOnlyList<string> NextMarks,
    bool Changed,
    bool RemovedAll)
{
    public static BrowserBulkMarkTransition Empty =>
        new(Array.Empty<string>(), false, false);
}

internal readonly record struct BrowserDirectoryLoadOptions(
    string? CurrentItemFullName,
    bool ShowHiddenFiles,
    TabFilterLockState? FilterLock,
    string? DateFormat,
    string? SizeFormat,
    bool ShowDirectoryMarker,
    int ItemsPerPage,
    BrowserLoadCoordinator.SnapshotPolicy SnapshotPolicy,
    string? FilterPattern = null,
    bool? FilterUseRegex = null,
    SortKind? SortKind = null,
    bool? SortAscending = null,
    BrowserLayoutProjectionInput? LayoutProjectionInput = null);

internal enum BrowserNavigationDecisionKind
{
    NotRequested,
    DirectoryMissing,
    Blocked,
    Ready
}

internal readonly record struct BrowserNavigationDecision(
    BrowserNavigationDecisionKind Kind,
    BrowserNavigationCoordinator.DirectoryNavigationRequest? Request,
    BrowserLocationChangeTransition LocationChange)
{
    public static BrowserNavigationDecision NotRequested =>
        new(BrowserNavigationDecisionKind.NotRequested, null, BrowserLocationChangeTransition.Denied);
}
