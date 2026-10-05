using System;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Directory load成功時のapplication state遷移を担当する。
/// UI concrete objectを保持せず、UIへ返す効果だけを返す。
/// </summary>
internal sealed class BrowserDirectoryApplicationCoordinator
{
    private readonly BrowserSessionState _state;
    private readonly BrowserLoadCoordinator _loadCoordinator = new();

    public BrowserDirectoryApplicationCoordinator(BrowserSessionState state)
    {
        _state = state;
    }

    public BrowserDirectoryApplicationTransition ApplyDirectoryLoad(
        BrowserLoadCoordinator.DirectoryLoadResult result,
        int itemsPerPage,
        int columnCount,
        SortKind sortKind,
        bool sortAscending,
        bool isSwitchingBrowserTab)
    {
        if (isSwitchingBrowserTab)
        {
            IncrementDirectoryContentGeneration();
            CancelCountAudit();
        }

        bool directoryChanged = !string.Equals(
            NavigationService.NormalizeDirectoryForCompare(result.PreviousPath),
            NavigationService.NormalizeDirectoryForCompare(result.NewPath),
            StringComparison.OrdinalIgnoreCase);

        BrowserNavigationSessionState navigation = _state.NavigationState;
        navigation.Navigation.SetCurrentPath(result.NewPath, result.IsHistoryNavigation);
        navigation.PageStartIndex = result.PageStartIndex;
        navigation.TotalItemCount = result.TotalItemCount;
        navigation.CursorIndex = result.LastIndex;
        navigation.ItemsPerPage = itemsPerPage;
        if (!result.SuppressRecent && !result.IsReload && !string.IsNullOrWhiteSpace(result.PreviousPath) &&
            !QuickAccessService.PathsEqual(result.PreviousPath, result.NewPath) &&
            QuickAccessService.RecordRecent(_state.Workspace.QuickAccess, result.NewPath))
        {
            QuickAccessService.Save(_state.Workspace.QuickAccess);
        }

        BrowserTabState? activeTab = _state.Tabs.ActiveTab;
        if (activeTab != null)
        {
            activeTab.Title = BrowserTabPresentationHelper.BuildTabTitle(
                result.NewPath,
                normalizedPath => QuickAccessService.FindAliasDisplayName(_state.Workspace.QuickAccess, normalizedPath));
            activeTab.CurrentPath = result.NewPath;
            activeTab.Navigation = navigation.Navigation.CaptureState();
            activeTab.FocusTargetName = result.FocusTargetName;
            activeTab.CursorIndex = result.LastIndex;
            activeTab.ColumnCount = Math.Clamp(columnCount, 1, 9);
            activeTab.SortKind = sortKind;
            activeTab.SortAscending = sortAscending;
        }

        if (directoryChanged)
        {
            _state.NavigationState.RefreshCoordinator.ClearPendingRefresh();
            _state.NavigationState.RefreshCoordinator.State.IsPassiveRefresh = false;
            IncrementDirectoryNavigationGeneration();
            CancelCountAudit();
        }

        if (!result.ReusedSnapshot)
        {
            _state.NavigationState.RefreshCoordinator.ConfigureDirectoryCost(
                result.RawDirectoryEntryCount,
                result.TotalItemCount,
                result.ItemBuildMilliseconds);
        }

        return new BrowserDirectoryApplicationTransition(
            directoryChanged,
            ShouldApplySelection: true,
            ShouldApplyActiveTabPresentation: !isSwitchingBrowserTab,
            ShouldInvalidateBrowserPanel: !isSwitchingBrowserTab,
            ShouldConfigureDirectoryCost: !result.ReusedSnapshot);
    }

    public BrowserDirectoryLoadApplicationResult ExecuteAndApply(
        BrowserLoadCoordinator.DirectoryLoadRequest request,
        int columnCount,
        bool isSwitchingBrowserTab)
    {
        BrowserDirectoryLoadExecution execution = Execute(request);
        if (execution.Succeeded)
        {
            BrowserLoadCoordinator.DirectoryLoadResult result = execution.Result!;
            BrowserDirectoryApplicationTransition transition = ApplyDirectoryLoad(
                result,
                request.ItemsPerPage,
                columnCount,
                request.SortKind,
                request.SortAscending,
                isSwitchingBrowserTab);
            return BrowserDirectoryLoadApplicationResult.Success(result, transition);
        }

        return BrowserDirectoryLoadApplicationResult.Failure(execution.Error!);
    }

    public BrowserDirectoryLoadExecution Execute(BrowserLoadCoordinator.DirectoryLoadRequest request)
    {
        if (request.SnapshotPolicy == BrowserLoadCoordinator.SnapshotPolicy.RebuildSnapshot)
        {
            IncrementDirectoryContentGeneration();
            CancelCountAudit();
        }

        try
        {
            return BrowserDirectoryLoadExecution.Success(ExecuteLoad(request));
        }
        catch (Exception ex)
        {
            return BrowserDirectoryLoadExecution.Failure(ex);
        }
    }

    public BrowserLoadCoordinator.DirectoryLoadResult ExecuteLoad(
        BrowserLoadCoordinator.DirectoryLoadRequest request)
    {
        return _loadCoordinator.Execute(request);
    }

    public bool TryGetCurrentSnapshotTargetPaths(
        string expectedPath,
        bool includeDirectories,
        out IReadOnlyList<string> paths)
    {
        return _loadCoordinator.TryGetCurrentSnapshotTargetPaths(expectedPath, includeDirectories, out paths);
    }

    public bool TryGetCurrentSnapshotPathKinds(
        string expectedPath,
        out IReadOnlyDictionary<string, bool> pathKinds)
    {
        return _loadCoordinator.TryGetCurrentSnapshotPathKinds(expectedPath, out pathKinds);
    }

    public bool TryFindCurrentSnapshotPrefixIndex(
        string expectedPath,
        string prefix,
        out int globalIndex)
    {
        return _loadCoordinator.TryFindCurrentSnapshotPrefixIndex(expectedPath, prefix, out globalIndex);
    }

    public void InvalidateSnapshot() => _loadCoordinator.InvalidateSnapshot();

    private void IncrementDirectoryContentGeneration() => _state.NavigationState.DirectoryContentGeneration++;

    private void IncrementDirectoryNavigationGeneration() => _state.NavigationState.DirectoryNavigationGeneration++;

    private void CancelCountAudit()
    {
        _state.NavigationState.CountAuditCancellation?.Cancel();
        _state.NavigationState.CountAuditCancellation?.Dispose();
        _state.NavigationState.CountAuditCancellation = null;
    }
}

internal readonly record struct BrowserDirectoryApplicationTransition(
    bool DirectoryChanged,
    bool ShouldApplySelection,
    bool ShouldApplyActiveTabPresentation,
    bool ShouldInvalidateBrowserPanel,
    bool ShouldConfigureDirectoryCost);

internal readonly record struct BrowserDirectoryLoadApplicationResult(
    BrowserLoadCoordinator.DirectoryLoadResult? Result,
    BrowserDirectoryApplicationTransition Transition,
    Exception? Error)
{
    public bool Succeeded => Result != null && Error == null;

    public static BrowserDirectoryLoadApplicationResult Success(
        BrowserLoadCoordinator.DirectoryLoadResult result,
        BrowserDirectoryApplicationTransition transition) =>
        new(result, transition, null);

    public static BrowserDirectoryLoadApplicationResult Failure(Exception error) =>
        new(null, default, error);
}
