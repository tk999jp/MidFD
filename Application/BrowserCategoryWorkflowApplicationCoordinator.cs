using System;
using System.Collections.Generic;
using System.Linq;
using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Runtime;

/// <summary>
/// Browser workspace category の状態遷移と永続化境界を所有する。
/// UI は確認ダイアログと表示更新だけを担当する。
/// </summary>
internal sealed class BrowserCategoryWorkflowApplicationCoordinator
{
    private readonly BrowserApplicationCoordinator _browser;
    private readonly SettingsApplicationCoordinator _settings;
    private readonly BrowserWorkspaceLifecycleApplicationCoordinator _lifecycle;
    private readonly BrowserWorkspacePersistenceApplicationCoordinator _persistence;
    private readonly BrowserTabWorkflowApplicationCoordinator? _tabWorkflow;

    public BrowserCategoryWorkflowApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        SettingsApplicationCoordinator settings,
        BrowserWorkspaceLifecycleApplicationCoordinator lifecycle,
        BrowserWorkspacePersistenceApplicationCoordinator persistence,
        BrowserTabWorkflowApplicationCoordinator? tabWorkflow = null)
    {
        _browser = browser;
        _settings = settings;
        _lifecycle = lifecycle;
        _persistence = persistence;
        _tabWorkflow = tabWorkflow;
    }

    public void EnsureConfiguration() =>
        _settings.EnsureBrowserWorkspaceCategoryConfiguration(_browser.Workspace);

    public void PrepareContextCategory(string? categoryId)
    {
        EnsureConfiguration();
        SetContextCategoryId(categoryId);
    }

    public string? ResolveAdjacent(int delta)
    {
        EnsureConfiguration();
        return _browser.Workspace.ResolveAdjacentCategoryId(delta);
    }

    public BrowserCategorySwitchWorkflowResult ExecuteAdjacentSwitch(
        int delta,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default,
        bool recordVisitHistory = false)
    {
        string? categoryId = ResolveAdjacent(delta);
        return categoryId == null || _tabWorkflow == null
            ? BrowserCategorySwitchWorkflowResult.NotAvailable
            : _tabWorkflow.ExecuteCategorySwitch(
                categoryId,
                requestedTabIndex: null,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState,
                recordVisitHistory);
    }

    public BrowserCategorySwitchWorkflowResult ExecuteSwitch(
        string categoryId,
        int? requestedTabIndex,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default,
        bool recordVisitHistory = false) =>
        _tabWorkflow?.ExecuteCategorySwitch(
            categoryId,
            requestedTabIndex,
            currentPath,
            currentState,
            options,
            columnCount,
            shellState,
            recordVisitHistory) ?? BrowserCategorySwitchWorkflowResult.NotAvailable;

    public bool CanRemove(string categoryId) =>
        _browser.Workspace.FindCategory(categoryId) != null &&
        !string.Equals(
            _browser.Workspace.ResolveCategoryId(categoryId),
            BrowserTabSettings.DefaultCategoryId,
            StringComparison.OrdinalIgnoreCase);

    public void SetContextCategoryId(string? categoryId) =>
        _browser.Workspace.SetContextCategoryId(categoryId);

    public string? Add(string displayName)
    {
        if (!_settings.TryAddBrowserCategory(
            _browser.Workspace,
            displayName,
            out BrowserTabCategoryDefinition? category))
        {
            return null;
        }

        _persistence.EnsureRestoreSnapshot();
        _persistence.SaveSettings();
        return category!.Id;
    }

    public BrowserCategoryAddSwitchExecution ExecuteAddAndSwitch(
        string displayName,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default,
        bool recordVisitHistory = true)
    {
        _tabWorkflow?.PrepareForBrowserWorkspaceNavigation();
        string? categoryId = Add(displayName);
        if (categoryId == null || _tabWorkflow == null)
        {
            return new BrowserCategoryAddSwitchExecution(categoryId, BrowserCategorySwitchWorkflowResult.NotAvailable);
        }

        return new BrowserCategoryAddSwitchExecution(
            categoryId,
            _tabWorkflow.ExecuteCategorySwitch(
                categoryId,
                requestedTabIndex: null,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState,
                recordVisitHistory));
    }

    public BrowserCategoryReorderTransition Reorder(
        string categoryId,
        int delta,
        BrowserTabState? activeState = null)
    {
        ApplyActiveState(activeState);
        BrowserCategoryReorderTransition transition = _settings.ReorderBrowserCategory(
            _browser.Workspace,
            categoryId,
            delta);
        if (transition.Applied)
        {
            _persistence.EnsureRestoreSnapshot();
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
            _persistence.SaveSettings();
        }

        return transition;
    }

    public bool Rename(string categoryId, string displayName, out bool duplicateName)
    {
        bool applied = _settings.TryRenameBrowserCategory(
            _browser.Workspace,
            categoryId,
            displayName,
            out duplicateName);
        if (applied)
        {
            _persistence.EnsureRestoreSnapshot();
            _persistence.SaveSettings();
        }

        return applied;
    }

    public BrowserCategoryRemovalWorkflowResult Remove(
        IEnumerable<string> categoryIds,
        BrowserTabState? activeState = null)
    {
        ApplyActiveState(activeState);
        BrowserCategoryRemovalTransition removal = _settings.RemoveBrowserCategories(
            _browser.Workspace,
            categoryIds);
        if (removal.RemovedCount <= 0)
        {
            return new BrowserCategoryRemovalWorkflowResult(removal, null, 0);
        }

        IReadOnlyList<BrowserTabState>? fallbackTabs = null;
        int fallbackIndex = 0;
        if (removal.RequiresFallback && removal.FallbackCategoryId != null)
        {
            fallbackTabs = _lifecycle.LoadTabsForCategory(removal.FallbackCategoryId);
            fallbackIndex = Math.Clamp(
                _settings.ResolveBrowserWorkspaceActiveTabIndex(
                    _browser.Workspace,
                    removal.FallbackCategoryId,
                    fallbackTabs.Count),
                0,
                Math.Max(0, fallbackTabs.Count - 1));
            _browser.Workspace.ReplaceActiveTabs(removal.FallbackCategoryId, fallbackTabs, -1);
            _browser.Workspace.SetContextTabIndex(-1);
            _browser.Workspace.PrepareTabActivation();
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        }

        _persistence.SaveSettings();
        return new BrowserCategoryRemovalWorkflowResult(removal, fallbackTabs, fallbackIndex);
    }

    public BrowserCategoryRemovalExecution ExecuteRemoveAndSwitch(
        IEnumerable<string> categoryIds,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        _tabWorkflow?.PrepareForBrowserWorkspaceNavigation();
        BrowserCategoryRemovalWorkflowResult removal = Remove(categoryIds, currentState);
        if (!removal.Transition.RequiresFallback ||
            removal.FallbackTabs == null ||
            _tabWorkflow == null)
        {
            return new BrowserCategoryRemovalExecution(removal, null);
        }

        BrowserTabSwitchWorkflowResult switchResult = _tabWorkflow.ExecuteTabSwitch(
            removal.FallbackTabIndex,
            currentPath,
            _browser.Workspace.ActiveTabSnapshot ?? currentState,
            options,
            columnCount,
            shellState);
        return new BrowserCategoryRemovalExecution(removal, switchResult);
    }

    public BrowserTabCloseExecution ExecuteTabClose(
        int tabIndex,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default) =>
        _tabWorkflow?.ExecuteCloseTab(
            tabIndex,
            currentPath,
            currentState,
            options,
            columnCount,
            shellState) ?? new BrowserTabCloseExecution(
                BrowserTabCloseDecision.Invalid,
                null,
                null);

    public BrowserTabCloseConfirmationExecution ContinueTabCloseAfterCategoryConfirmation(
        int tabIndex,
        bool removeCategory,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        if (!removeCategory || _tabWorkflow == null)
        {
            return BrowserTabCloseConfirmationExecution.NotApplied;
        }

        BrowserTabCloseDecision decision = _tabWorkflow.EvaluateClose(tabIndex);
        if (decision.Kind != BrowserTabCloseDecisionKind.RequiresCategoryRemoval || decision.CategoryId == null)
        {
            return BrowserTabCloseConfirmationExecution.NotApplied;
        }

        BrowserCategoryRemovalExecution removal = ExecuteRemoveAndSwitch(
            [decision.CategoryId],
            currentPath,
            currentState,
            options,
            columnCount,
            shellState);
        return new BrowserTabCloseConfirmationExecution(removal.Removed, removal.Switch);
    }

    public bool ReorderByIndex(
        int fromIndex,
        int toIndex,
        BrowserTabState? activeState = null)
    {
        ApplyActiveState(activeState);
        bool applied = _settings.ReorderBrowserCategoryByIndex(
            _browser.Workspace,
            fromIndex,
            toIndex);
        if (applied)
        {
            _persistence.EnsureRestoreSnapshot();
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
            _persistence.SaveSettings();
        }

        return applied;
    }

    private void ApplyActiveState(BrowserTabState? activeState)
    {
        if (activeState == null)
        {
            return;
        }

        int activeTabIndex = _browser.Workspace.ActiveTabIndex;
        if (activeTabIndex >= 0 && activeTabIndex < _browser.Workspace.TabCount)
        {
            _browser.Workspace.ApplyCapturedState(
                activeTabIndex,
                activeState,
                captureMarks: true,
                shouldValidateMarks: false,
                markValidationSucceeded: false);
        }
    }
}

internal readonly record struct BrowserCategoryRemovalWorkflowResult(
    BrowserCategoryRemovalTransition Transition,
    IReadOnlyList<BrowserTabState>? FallbackTabs,
    int FallbackTabIndex);

internal readonly record struct BrowserCategoryAddSwitchExecution(
    string? CategoryId,
    BrowserCategorySwitchWorkflowResult Switch)
{
    public bool Added => CategoryId != null;
}

internal readonly record struct BrowserCategoryRemovalExecution(
    BrowserCategoryRemovalWorkflowResult Removal,
    BrowserTabSwitchWorkflowResult? Switch)
{
    public bool Removed => Removal.Transition.RemovedCount > 0;
}

internal readonly record struct BrowserTabCloseConfirmationExecution(
    bool Applied,
    BrowserTabSwitchWorkflowResult? Switch)
{
    public static BrowserTabCloseConfirmationExecution NotApplied => new(false, null);
}
