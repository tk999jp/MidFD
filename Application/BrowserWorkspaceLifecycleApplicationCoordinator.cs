using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Browser/Workspace の runtime lifecycle を所有する。
/// startup復元・runtime state切替・category tab materializationの意味を確定し、
/// persistence I/O は BrowserWorkspacePersistenceApplicationCoordinator に委譲する。
/// </summary>
internal sealed class BrowserWorkspaceLifecycleApplicationCoordinator
{
    private readonly BrowserApplicationCoordinator _browser;
    private readonly SettingsApplicationCoordinator _settings;
    private readonly BrowserWorkspacePersistenceApplicationCoordinator _persistence;
    private readonly BrowserRefreshWorkflowApplicationCoordinator? _refreshWorkflow;

    public BrowserWorkspaceLifecycleApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        SettingsApplicationCoordinator settings,
        BrowserWorkspacePersistenceApplicationCoordinator persistence,
        BrowserRefreshWorkflowApplicationCoordinator? refreshWorkflow = null)
    {
        _browser = browser;
        _settings = settings;
        _persistence = persistence;
        _refreshWorkflow = refreshWorkflow;
    }

    public BrowserWorkspaceLifecycleApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        SettingsApplicationCoordinator settings)
        : this(
            browser,
            settings,
            new BrowserWorkspacePersistenceApplicationCoordinator(browser, settings))
    {
    }

    public void ApplyStartupSessionState()
    {
        if (SessionRestorePolicy.ShouldRestoreColumnCount(_settings.Value.Session))
        {
            _browser.SetColumnCount(_settings.Value.Session.LastColumnCount);
        }
        if (SessionRestorePolicy.ShouldRestoreSort(_settings.Value.Session))
        {
            _browser.SetSort(
                _settings.Value.Session.LastSortKind,
                _settings.Value.Session.LastSortAscending);
        }
    }

    private void SetInitialTab(BrowserTabState state) =>
        _browser.Workspace.ReplaceActiveTabs(
            _browser.Workspace.ActiveCategoryId,
            new[] { state },
            0);

    public BrowserWorkspaceRuntimeRestorePlan RestoreRuntimeState(
        BrowserWorkspaceRuntimeStateSnapshot runtimeState,
        bool stateAlreadyApplied = false)
    {
        if (!stateAlreadyApplied)
        {
            _persistence.ApplyRuntimeState(runtimeState);
        }

        BrowserWorkspaceRuntimeRestorePlan plan = PrepareRuntimeRestoreAfterStateApplied();
        CommitRuntimeRestore(plan);
        return plan;
    }

    private BrowserWorkspaceRuntimeRestorePlan PrepareRuntimeRestoreAfterStateApplied()
    {
        string activeCategoryId = _browser.Workspace.ActiveCategoryId;
        IReadOnlyList<BrowserTabState> targetTabs = LoadTabsForCategory(activeCategoryId);
        int targetIndex = Math.Clamp(
            _persistence.ResolveActiveTabIndex(activeCategoryId, targetTabs.Count),
            0,
            Math.Max(0, targetTabs.Count - 1));
        return new BrowserWorkspaceRuntimeRestorePlan(activeCategoryId, targetTabs, targetIndex);
    }

    private void CommitRuntimeRestore(BrowserWorkspaceRuntimeRestorePlan plan)
    {
        _browser.Workspace.ReplaceActiveTabs(plan.ActiveCategoryId, plan.TargetTabs, -1);
        _browser.Workspace.SetContextTabIndex(-1);
        _browser.Workspace.PrepareTabActivation();
    }

    public string ResolveStartupPath(IReadOnlyList<string> commandLineArgs, string currentDirectory)
    {
        if (commandLineArgs.Count > 1 && Directory.Exists(commandLineArgs[1]))
        {
            return commandLineArgs[1];
        }

        if (SessionRestorePolicy.ShouldRestoreStartupFolder(_settings.Value.Session) &&
            !string.IsNullOrEmpty(_settings.Value.Session.LastPath) &&
            Directory.Exists(_settings.Value.Session.LastPath))
        {
            return _settings.Value.Session.LastPath;
        }

        return currentDirectory;
    }

    public BrowserWorkspaceStartupRestoreResult BeginStartupRestore(string startupPath)
    {
        BrowserWorkspaceStartupRestoreResult restoration = RestoreStartupTabs();
        if (restoration.Restored)
        {
            BrowserStartupLegacyMarkRestoreResult legacyMarks = _persistence.ApplyStartupLegacyMarks(
                restoration.RestoreEnabled,
                restoredTabs: true,
                restoration.WorkspaceStoreLoaded,
                _browser.Workspace.TabStates);
            restoration = restoration with { LegacyMarkRestore = legacyMarks };
            return FinalizeStartupState(restoration, initialState: null);
        }

        return restoration with
        {
            RequiresInitialDirectoryLoad = true,
            InitialDirectoryPath = startupPath
        };
    }

    public BrowserWorkspaceStartupRestoreResult CompleteStartupRestore(
        BrowserWorkspaceStartupRestoreResult restoration,
        BrowserTabState initialState)
    {
        if (restoration.Restored)
        {
            return restoration;
        }

        SetInitialTab(initialState);
        BrowserStartupLegacyMarkRestoreResult legacyMarks = _persistence.ApplyStartupLegacyMarks(
            restoration.RestoreEnabled,
            restoredTabs: false,
            restoration.WorkspaceStoreLoaded,
            _browser.Workspace.TabStates);
        return FinalizeStartupState(restoration with
        {
            LegacyMarkRestore = legacyMarks,
            RequiresInitialDirectoryLoad = false
        }, initialState);
    }

    public BrowserWorkspaceStartupExecution ExecuteStartupRestore(
        string startupPath,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserWorkspaceStartupRestoreResult restoration = BeginStartupRestore(startupPath);
        if (!restoration.RequiresInitialDirectoryLoad)
        {
            return ExecuteRestoredStartupDirectoryLoad(restoration, options, shellState);
        }

        BrowserLoadCoordinator.DirectoryLoadRequest request = _browser.CreateDirectoryLoadRequest(
            restoration.InitialDirectoryPath ?? startupPath,
            focusTargetName: null,
            isHistoryNavigation: false,
            suppressRecent: false,
            options);
        BrowserDirectoryLoadExecution load = _browser.Directory.Execute(request);
        if (!load.Succeeded)
        {
            return new BrowserWorkspaceStartupExecution(
                restoration,
                BrowserDirectoryLoadApplicationResult.Failure(load.Error!));
        }

        BrowserLoadCoordinator.DirectoryLoadResult result = load.Result!;
        BrowserDirectoryApplicationTransition transition = _browser.Directory.ApplyDirectoryLoad(
            result,
            options.ItemsPerPage,
            columnCount,
            options.SortKind ?? _browser.CurrentSort,
            options.SortAscending ?? _browser.SortAscending,
            isSwitchingBrowserTab: false);
        BrowserTabState initialState = _browser.Workspace.CreateInitialTab(
            _browser.Workspace.ActiveCategoryId,
            result.NewPath,
            columnCount,
            options.SortKind ?? _browser.CurrentSort,
            options.SortAscending ?? _browser.SortAscending);
        initialState.FilterLock = options.FilterLock?.Clone() ?? new TabFilterLockState();
        initialState.FocusTargetName = result.FocusTargetName;
        initialState.CursorIndex = result.LastIndex;
        initialState.MarkedPaths = _browser.Selection.Snapshot().ToList();
        initialState.MarksDirty = false;

        BrowserWorkspaceStartupRestoreResult completed = CompleteStartupRestore(restoration, initialState);
        return new BrowserWorkspaceStartupExecution(
            completed,
            BrowserDirectoryLoadApplicationResult.Success(result, transition))
        {
            PostLoadEffects = _refreshWorkflow?.PreparePostLoadEffects(shellState) ?? default
        };
    }

    private BrowserWorkspaceStartupExecution ExecuteRestoredStartupDirectoryLoad(
        BrowserWorkspaceStartupRestoreResult restoration,
        BrowserDirectoryLoadOptions options,
        BrowserRefreshShellState shellState)
    {
        if (restoration.ActiveTabIndex < 0 ||
            restoration.ActiveTabIndex >= _browser.Workspace.TabCount)
        {
            return new BrowserWorkspaceStartupExecution(
                restoration,
                BrowserDirectoryLoadApplicationResult.Failure(
                    new InvalidOperationException("復元したBrowser tabをactiveにできません。")));
        }

        BrowserTabActivationTransition activation = _browser.Workspace.ActivateTabState(restoration.ActiveTabIndex);
        if (!activation.Applied || activation.State == null)
        {
            return new BrowserWorkspaceStartupExecution(
                restoration,
                BrowserDirectoryLoadApplicationResult.Failure(
                    new InvalidOperationException("復元したBrowser tabの状態を適用できません。")));
        }

        _browser.SetColumnCount(activation.ColumnCount);
        _browser.SetSort(activation.SortKind, activation.SortAscending);
        BrowserTabState activeState = activation.State;
        BrowserDirectoryLoadOptions restoredOptions = _browser.CreateTargetMaterializationOptions(activeState, options);
        BrowserLoadCoordinator.DirectoryLoadRequest request = _browser.CreateTargetDirectoryLoadRequest(
            activeState.CurrentPath,
            activeState,
            isHistoryNavigation: false,
            suppressRecent: true,
            targetOptions: restoredOptions);
        BrowserDirectoryLoadExecution load = _browser.Directory.Execute(request);
        if (!load.Succeeded)
        {
            return new BrowserWorkspaceStartupExecution(
                restoration,
                BrowserDirectoryLoadApplicationResult.Failure(load.Error!));
        }

        BrowserLoadCoordinator.DirectoryLoadResult result = load.Result!;
        BrowserDirectoryApplicationTransition transition = _browser.Directory.ApplyDirectoryLoad(
            result,
            restoredOptions.ItemsPerPage,
            Math.Clamp(activeState.ColumnCount, 1, 9),
            activeState.SortKind,
            activeState.SortAscending,
            isSwitchingBrowserTab: false);
        return new BrowserWorkspaceStartupExecution(
            restoration,
            BrowserDirectoryLoadApplicationResult.Success(result, transition))
        {
            PostLoadEffects = _refreshWorkflow?.PreparePostLoadEffects(shellState) ?? default
        };
    }

    private BrowserWorkspaceStartupRestoreResult FinalizeStartupState(
        BrowserWorkspaceStartupRestoreResult restoration,
        BrowserTabState? initialState)
    {
        if (restoration.LegacyMarkRestore?.Kind == BrowserStartupLegacyMarkRestoreKind.SkippedByWorkspaceAuthority)
        {
            return restoration with { ActiveStateCaptureApplied = false };
        }

        int activeTabIndex = _browser.Workspace.ActiveTabIndex;
        if (activeTabIndex < 0 || activeTabIndex >= _browser.Workspace.TabCount)
        {
            return restoration with { ActiveStateCaptureApplied = false };
        }

        BrowserTabState capturedState = (initialState ?? _browser.Workspace.GetTabSnapshot(activeTabIndex)!).Clone();
        capturedState.MarkedPaths = _browser.Selection.Snapshot().ToList();
        _browser.Workspace.ApplyCapturedState(
            activeTabIndex,
            capturedState,
            captureMarks: true,
            shouldValidateMarks: false,
            markValidationSucceeded: false);
        return restoration with { ActiveStateCaptureApplied = true };
    }

    private BrowserWorkspaceStartupRestoreResult RestoreStartupTabs()
    {
        int restoredTabCount = 0;
        int skippedTabCount = 0;
        bool hadSavedTabs = false;
        bool restoreEnabled = false;
        bool tabLimitApplied = false;
        int maxTabCount = 0;
        try
        {
            _persistence.EnsureConfiguration();
            _persistence.EnsureNormalized();
            restoreEnabled = _persistence.ShouldRestoreWorkspace;
            if (!restoreEnabled)
            {
                _browser.Workspace.SetRestoredBrowserTabsFromStore(false);
                return BrowserWorkspaceStartupRestoreResult.NotRestored;
            }

            bool workspaceStoreLoaded = _persistence.TryLoadWorkspaceStateStore(
                out BrowserTabRestoreSnapshot? workspaceSnapshot);
            _browser.Workspace.SetRestoredBrowserTabsFromStore(workspaceStoreLoaded);
            if (workspaceSnapshot != null)
            {
                _persistence.ApplyWorkspaceRestoreSnapshot(workspaceSnapshot);
            }

            _persistence.EnsureConfiguration();
            BrowserTabRestoreSnapshot snapshot = _persistence.EnsureRestoreSnapshot();
            string restoredCategoryId = _browser.Workspace.ResolveCategoryId(snapshot.ActiveCategoryId);
            IReadOnlyList<BrowserTabSessionState> savedTabs = _persistence.GetSessionTabsForRestore(
                restoredCategoryId,
                out restoredCategoryId);
            hadSavedTabs = savedTabs.Count > 0;
            if (!hadSavedTabs)
            {
                return new BrowserWorkspaceStartupRestoreResult(
                    false,
                    false,
                    workspaceStoreLoaded,
                    0,
                    0,
                    restoredCategoryId,
                    Array.Empty<BrowserTabState>(),
                    0,
                    restoreEnabled,
                    false,
                    maxTabCount);
            }

            maxTabCount = BrowserWorkspaceApplicationCoordinator.GetMaxTabCount(_settings.Value.BrowserTabs);
            if (savedTabs.Count > maxTabCount)
            {
                skippedTabCount = savedTabs.Count - maxTabCount;
                tabLimitApplied = true;
                savedTabs = savedTabs.Take(maxTabCount).ToList();
            }

            var restoredTabs = new List<BrowserTabState>();
            foreach (BrowserTabSessionState sessionTab in savedTabs)
            {
                BrowserTabRestoreResult restore = _browser.Workspace.RestoreTab(sessionTab);
                if (restore.Applied)
                {
                    restoredTabs.Add(restore.State!);
                    if (restore.SkippedMarkCount > 0)
                    {
                        LogService.Info(
                            $"[BrowserTabs] Pruned stale restored marks. TabId={restore.State!.Id} Missing={restore.SkippedMarkCount}");
                    }
                }
                else
                {
                    skippedTabCount++;
                }
            }

            if (restoredTabs.Count == 0)
            {
                return new BrowserWorkspaceStartupRestoreResult(
                    false,
                    true,
                    workspaceStoreLoaded,
                    0,
                    skippedTabCount,
                    restoredCategoryId,
                    Array.Empty<BrowserTabState>(),
                    0,
                    restoreEnabled,
                    tabLimitApplied,
                    maxTabCount);
            }

            foreach (BrowserTabRestoreCategoryState category in snapshot.Categories)
            {
                restoredTabCount += string.Equals(category.Id, restoredCategoryId, StringComparison.OrdinalIgnoreCase)
                    ? restoredTabs.Count
                    : category.OpenTabs.Count;
            }

            int targetIndex = _persistence.ResolveActiveTabIndex(restoredCategoryId, restoredTabs.Count);
            _browser.Workspace.ReplaceActiveTabs(restoredCategoryId, restoredTabs, targetIndex);
            _persistence.RestoreBrowserTabGroups(snapshot);
            if (!workspaceStoreLoaded)
            {
                _persistence.SaveWorkspaceStateStore();
            }

            return new BrowserWorkspaceStartupRestoreResult(
                true,
                true,
                workspaceStoreLoaded,
                restoredTabCount,
                skippedTabCount,
                restoredCategoryId,
                restoredTabs,
                targetIndex,
                restoreEnabled,
                tabLimitApplied,
                maxTabCount);
        }
        catch (Exception ex)
        {
            LogService.Error(
                "Unexpected error during startup browser tabs restoration. Falling back to default startup.",
                ex);
            return new BrowserWorkspaceStartupRestoreResult(
                false,
                hadSavedTabs,
                false,
                0,
                0,
                _browser.Workspace.ActiveCategoryId,
                Array.Empty<BrowserTabState>(),
                0,
                restoreEnabled,
                tabLimitApplied,
                maxTabCount,
                ex);
        }
    }

    public IReadOnlyList<BrowserTabState> LoadTabsForCategory(string categoryId)
    {
        _persistence.EnsureConfiguration();
        _persistence.EnsureNormalized();
        BrowserTabRestoreCategoryState? categoryState = _persistence.FindStoredCategory(categoryId);
        var restoredTabs = new List<BrowserTabState>();
        int sessionTabCount = categoryState?.OpenTabs?.Count ?? 0;
        foreach (BrowserTabSessionState sessionTab in categoryState?.OpenTabs ?? Enumerable.Empty<BrowserTabSessionState>())
        {
            BrowserTabRestoreResult restore = _browser.Workspace.RestoreTab(sessionTab);
            if (restore.Applied)
            {
                restoredTabs.Add(restore.State!);
            }
        }

        bool usedFallback = false;
        if (restoredTabs.Count == 0)
        {
            restoredTabs.Add(CreateInitialTabForCategory(categoryId));
            usedFallback = true;
        }

        LogService.Info(
            $"[BrowserTabCategory] Load Category={categoryId} SessionTabs={sessionTabCount} " +
            $"RestoredTabs={restoredTabs.Count} UsedFallback={usedFallback} CurrentPath={_browser.CurrentPath}");
        return restoredTabs;
    }

    public BrowserTabState CreateInitialTabForCategory(string categoryId)
    {
        string resolvedCategoryId = _browser.Workspace.ResolveCategoryId(categoryId);
        return _browser.Workspace.CreateInitialTab(
            resolvedCategoryId,
            _browser.CurrentPath,
            _browser.ColumnCount,
            _browser.CurrentSort,
            _browser.SortAscending);
    }
}

internal sealed record BrowserWorkspaceStartupRestoreResult(
    bool Restored,
    bool HadSavedTabs,
    bool WorkspaceStoreLoaded,
    int RestoredTabCount,
    int SkippedTabCount,
    string? ActiveCategoryId,
    IReadOnlyList<BrowserTabState> RestoredTabs,
    int ActiveTabIndex,
    bool RestoreEnabled = true,
    bool TabLimitApplied = false,
    int MaxTabCount = 0,
    Exception? Error = null,
    bool RequiresInitialDirectoryLoad = false,
    BrowserStartupLegacyMarkRestoreResult? LegacyMarkRestore = null,
    string? InitialDirectoryPath = null,
    bool ActiveStateCaptureApplied = false)
{
    public static BrowserWorkspaceStartupRestoreResult NotRestored { get; } = new(
        false,
        false,
        false,
        0,
        0,
        null,
        Array.Empty<BrowserTabState>(),
        0,
        false,
        false,
        0);
}

internal readonly record struct BrowserWorkspaceStartupExecution(
    BrowserWorkspaceStartupRestoreResult Restoration,
    BrowserDirectoryLoadApplicationResult? DirectoryLoad)
{
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
}

internal readonly record struct BrowserWorkspaceRuntimeRestorePlan(
    string ActiveCategoryId,
    IReadOnlyList<BrowserTabState> TargetTabs,
    int TargetTabIndex);
