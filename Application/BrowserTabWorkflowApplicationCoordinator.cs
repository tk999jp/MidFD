using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Browser tab/category state transition の application boundary。
/// Controlのselection、focus、ListView反映は返却されたtransitionをShellが適用する。
/// </summary>
internal sealed class BrowserTabWorkflowApplicationCoordinator
{
    private readonly BrowserApplicationCoordinator _browser;
    private readonly SettingsApplicationCoordinator _settings;
    private readonly BrowserWorkspaceLifecycleApplicationCoordinator _lifecycle;
    private readonly BrowserWorkspacePersistenceApplicationCoordinator _persistence;
    private readonly BrowserRefreshWorkflowApplicationCoordinator? _refreshWorkflow;
    private readonly ViewerModeApplicationCoordinator? _viewerMode;
    private readonly IViewerModeUiPort? _viewerUi;
    private readonly Stack<BrowserTabBatchHistoryEntry> _tabUndoHistory = new();
    private readonly Stack<BrowserTabBatchHistoryEntry> _tabRedoHistory = new();
    private readonly BrowserTabVisitHistoryService _visitHistory = new();

    public BrowserTabVisitHistoryService VisitHistory => _visitHistory;
    public IDisposable BeginHistoryModeScope(BrowserTabVisitHistoryMode mode) => _visitHistory.BeginModeScope(mode);

    public IReadOnlyList<BrowserTabVisitHistoryListItem> BuildTabVisitHistorySnapshot()
    {
        BrowserTabVisitLocation current = GetActiveVisitLocation();
        _visitHistory.PruneInvalidEntries(IsVisitLocationValid);

        var items = new List<BrowserTabVisitHistoryListItem>();
        int depth = 0;
        foreach (BrowserTabVisitLocation location in _visitHistory.GetBackSnapshot())
        {
            if (TryBuildVisitHistoryItem(
                    BrowserTabVisitHistoryListDirection.Back,
                    depth++,
                    location,
                    out BrowserTabVisitHistoryListItem item))
            {
                items.Add(item);
            }
        }

        if (TryBuildVisitHistoryItem(
                BrowserTabVisitHistoryListDirection.Current,
                0,
                current,
                out BrowserTabVisitHistoryListItem currentItem))
        {
            items.Add(currentItem);
        }

        depth = 0;
        foreach (BrowserTabVisitLocation location in _visitHistory.GetForwardSnapshot())
        {
            if (TryBuildVisitHistoryItem(
                    BrowserTabVisitHistoryListDirection.Forward,
                    depth++,
                    location,
                    out BrowserTabVisitHistoryListItem item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    public BrowserTabWorkflowApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        SettingsApplicationCoordinator settings,
        BrowserWorkspaceLifecycleApplicationCoordinator lifecycle,
        BrowserWorkspacePersistenceApplicationCoordinator persistence,
        BrowserRefreshWorkflowApplicationCoordinator? refreshWorkflow = null,
        ViewerModeApplicationCoordinator? viewerMode = null,
        IViewerModeUiPort? viewerUi = null)
    {
        _browser = browser;
        _settings = settings;
        _lifecycle = lifecycle;
        _persistence = persistence;
        _refreshWorkflow = refreshWorkflow;
        _viewerMode = viewerMode;
        _viewerUi = viewerUi;
    }

    public BrowserTabWorkflowApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        SettingsApplicationCoordinator settings,
        BrowserWorkspaceLifecycleApplicationCoordinator lifecycle)
        : this(
            browser,
            settings,
            lifecycle,
            new BrowserWorkspacePersistenceApplicationCoordinator(browser, settings))
    {
    }

    public BrowserCategorySwitchWorkflowStart BeginCategorySwitch(
        string categoryId,
        int? requestedTabIndex,
        string currentPath,
        BrowserTabState? currentState = null)
    {
        ApplyActiveState(currentState, validateMarks: false);
        BrowserCategorySwitchTransition transition = _browser.Workspace.PrepareCategorySwitch(
            categoryId,
            requestedTabIndex);
        if (transition.SwitchTabOnly)
        {
            BrowserTabState? targetState = _browser.Workspace.GetTabSnapshot(transition.RequestedTabIndex);
            return targetState == null
                ? BrowserCategorySwitchWorkflowStart.NotAvailable
                : new BrowserCategorySwitchWorkflowStart(
                    BrowserCategorySwitchWorkflowKind.TabSwitch,
                    transition.TargetCategoryId,
                    transition.RequestedTabIndex,
                    targetState,
                    Array.Empty<BrowserTabState>(),
                    ResolveTargetPath(targetState, currentPath));
        }

        if (string.Equals(
                transition.TargetCategoryId,
                _browser.Workspace.ActiveCategoryId,
                StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserCategorySwitchWorkflowStart(
                BrowserCategorySwitchWorkflowKind.NoOp,
                transition.TargetCategoryId,
                _browser.Workspace.ActiveTabIndex,
                null,
                Array.Empty<BrowserTabState>(),
                currentPath);
        }

        IReadOnlyList<BrowserTabState> targetTabs = _lifecycle.LoadTabsForCategory(transition.TargetCategoryId);
        int targetIndex = requestedTabIndex.HasValue &&
            requestedTabIndex.Value >= 0 &&
            requestedTabIndex.Value < targetTabs.Count
            ? requestedTabIndex.Value
            : Math.Clamp(
                _persistence.ResolveActiveTabIndex(transition.TargetCategoryId, targetTabs.Count),
                0,
                Math.Max(0, targetTabs.Count - 1));
        BrowserTabState targetTab = targetTabs[targetIndex];
        return new BrowserCategorySwitchWorkflowStart(
            BrowserCategorySwitchWorkflowKind.CategorySwitch,
            transition.TargetCategoryId,
            targetIndex,
            targetTab,
            targetTabs,
            ResolveTargetPath(targetTab, currentPath));
    }

    public BrowserCategorySwitchWorkflowResult CompleteCategorySwitch(
        BrowserCategorySwitchWorkflowStart operation,
        bool materialized,
        BrowserMarkRestoreValidationContext? validationContext = null)
    {
        if (operation.Kind == BrowserCategorySwitchWorkflowKind.NotAvailable)
        {
            return BrowserCategorySwitchWorkflowResult.NotAvailable;
        }
        if (operation.Kind == BrowserCategorySwitchWorkflowKind.NoOp)
        {
            return BrowserCategorySwitchWorkflowResult.NoOp;
        }
        if (!materialized || operation.TargetState == null)
        {
            return new BrowserCategorySwitchWorkflowResult(
                BrowserCategorySwitchWorkflowResultKind.NotCommitted,
                operation.TargetCategoryId,
                operation.TargetTabIndex,
                null,
                Array.Empty<string>(),
                0);
        }

        if (operation.Kind == BrowserCategorySwitchWorkflowKind.CategorySwitch)
        {
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
            _browser.Workspace.ReplaceActiveTabs(
                operation.TargetCategoryId,
                operation.TargetTabs,
                operation.TargetTabIndex);
            _browser.Workspace.SetContextTabIndex(-1);
        }

        BrowserTabActivationTransition activation = ActivateTabCore(
            operation.TargetTabIndex,
            allowPendingMarkRestoreValidation: operation.Kind != BrowserCategorySwitchWorkflowKind.CategorySwitch,
            validationContext);
        if (!activation.Applied)
        {
            return new BrowserCategorySwitchWorkflowResult(
                BrowserCategorySwitchWorkflowResultKind.NotCommitted,
                operation.TargetCategoryId,
                operation.TargetTabIndex,
                null,
                Array.Empty<string>(),
                0);
        }

        BrowserTabState state = ApplyRestoredTabMarks(operation.TargetTabIndex, activation, out int skippedMarkCount);
        if (operation.Kind == BrowserCategorySwitchWorkflowKind.CategorySwitch)
        {
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        }

        return new BrowserCategorySwitchWorkflowResult(
            BrowserCategorySwitchWorkflowResultKind.Committed,
            operation.TargetCategoryId,
            operation.TargetTabIndex,
            state,
            state.MarkedPaths,
            skippedMarkCount)
        {
            SnapshotMarkHitCount = activation.SnapshotMarkHitCount,
            FallbackValidationCount = activation.FallbackValidationCount,
            FallbackFileProbeCount = activation.FallbackFileProbeCount,
            FallbackDirectoryProbeCount = activation.FallbackDirectoryProbeCount,
            ParentBatchGroupCount = activation.ParentBatchGroupCount,
            ParentBatchPathCount = activation.ParentBatchPathCount,
            ParentBatchHitCount = activation.ParentBatchHitCount,
            ParentBatchMissCount = activation.ParentBatchMissCount,
            ParentBatchFailureCount = activation.ParentBatchFailureCount,
            SnapshotPathKindsAreFresh = activation.SnapshotPathKindsAreFresh
        };
    }

    public BrowserCategorySwitchWorkflowResult ExecuteCategorySwitch(
        string categoryId,
        int? requestedTabIndex,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default,
        bool recordVisitHistory = false)
    {
        using (recordVisitHistory ? BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record) : null)
        {
            BrowserTabVisitLocation fromLocation = new(
                _browser.Workspace.ActiveCategoryId ?? BrowserTabSettings.DefaultCategoryId,
                _browser.Workspace.ActiveTabSnapshot?.Id ?? currentState.Id);
            PrepareForBrowserWorkspaceNavigation();
            BrowserCategorySwitchWorkflowStart operation = BeginCategorySwitch(
                categoryId,
                requestedTabIndex,
                currentPath,
                currentState);
        if (operation.Kind == BrowserCategorySwitchWorkflowKind.NotAvailable ||
            operation.Kind == BrowserCategorySwitchWorkflowKind.NoOp)
        {
            return operation.Kind == BrowserCategorySwitchWorkflowKind.NoOp
                ? BrowserCategorySwitchWorkflowResult.NoOp
                : BrowserCategorySwitchWorkflowResult.NotAvailable;
        }

        BrowserTabState targetState = operation.TargetState!;
        BrowserDirectoryLoadOptions targetOptions = _browser.CreateTargetMaterializationOptions(targetState, options);
        BrowserDirectoryLoadExecution load = ExecuteTargetLoad(targetState, operation.TargetPath, targetOptions);
        if (!load.Succeeded)
        {
            return BrowserCategorySwitchWorkflowResult.NotCommitted with
            {
                TargetCategoryId = operation.TargetCategoryId,
                TargetTabIndex = operation.TargetTabIndex,
                DirectoryLoad = BrowserDirectoryLoadApplicationResult.Failure(load.Error!)
            };
        }

        BrowserCategorySwitchWorkflowResult completion = CompleteCategorySwitch(
            operation,
            materialized: true,
            validationContext: CreateMarkRestoreValidationContext(load.Result!));
        if (!completion.Committed)
        {
            return completion with
            {
                TargetCategoryId = operation.TargetCategoryId,
                TargetTabIndex = operation.TargetTabIndex,
                DirectoryLoad = BrowserDirectoryLoadApplicationResult.Success(load.Result!, default)
            };
        }

        BrowserDirectoryApplicationTransition transition = _browser.Directory.ApplyDirectoryLoad(
            load.Result!,
            targetOptions.ItemsPerPage,
            Math.Clamp(targetState.ColumnCount, 1, 9),
            operation.TargetState!.SortKind,
            operation.TargetState.SortAscending,
            isSwitchingBrowserTab: true);
        BrowserTabVisitLocation toLocation = new(
            completion.TargetCategoryId,
            completion.State?.Id ?? Guid.Empty);
        _visitHistory.RecordNavigation(fromLocation, toLocation);
            return completion with
            {
                DirectoryLoad = BrowserDirectoryLoadApplicationResult.Success(load.Result!, transition),
                PostLoadEffects = _refreshWorkflow?.PreparePostLoadEffects(shellState) ?? default
            };
        }
    }

    public BrowserTabCreationTransition CreateTab(
        BrowserTabState currentState,
        string? initialPath,
        bool useConfiguredInsertion)
    {
        ApplyActiveState(currentState, validateMarks: false);
        int maxTabCount = BrowserWorkspaceApplicationCoordinator.GetMaxTabCount(_settings.Value.BrowserTabs);
        string categoryId = _browser.Workspace.ResolveCategoryId(_browser.Workspace.ActiveCategoryId);
        BrowserTabState newState = useConfiguredInsertion
            ? _lifecycle.CreateInitialTabForCategory(categoryId)
            : currentState;
        BrowserTabCreationTransition transition = _browser.Workspace.CreateNewTab(
            newState,
            initialPath,
            _settings.Value.BrowserTabs.NewTabPosition,
            useConfiguredInsertion,
            maxTabCount);
        if (transition.Created)
        {
            _browser.Workspace.PrepareTabActivation();
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        }

        return transition;
    }

    public BrowserTabCreationExecution ExecuteCreateTab(
        BrowserTabState currentState,
        string currentPath,
        string? initialPath,
        bool useConfiguredInsertion,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        PrepareForBrowserWorkspaceNavigation();
        BrowserTabCreationTransition creation = CreateTab(
            currentState,
            initialPath,
            useConfiguredInsertion);
        if (!creation.Created)
        {
            return new BrowserTabCreationExecution(creation, null);
        }

        BrowserTabSwitchWorkflowResult activation = ExecuteTabSwitch(
            creation.Index,
            currentPath,
            currentState,
            options,
            columnCount,
            shellState);
        return new BrowserTabCreationExecution(creation, activation);
    }

    public BrowserTabCreationBatchExecution ExecuteCreateTabs(
        IReadOnlyList<string> initialPaths,
        BrowserTabState currentState,
        string currentPath,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        return ExecuteCreateTabsCore(
            initialPaths,
            currentState,
            currentPath,
            options,
            columnCount,
            shellState,
            recordHistory: true);
    }

    private BrowserTabCreationBatchExecution ExecuteCreateTabsCore(
        IReadOnlyList<string> initialPaths,
        BrowserTabState currentState,
        string currentPath,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        bool recordHistory)
    {
        if (initialPaths.Count < 2 ||
            initialPaths.Any(static path => string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) ||
            _browser.Workspace.TabCount + initialPaths.Count > MaxTabCount)
        {
            return BrowserTabCreationBatchExecution.PreflightFailed;
        }

        string categoryId = _browser.Workspace.ActiveCategoryId;
        IReadOnlyList<BrowserTabState> beforeTabs = _browser.Workspace.TabStates;
        int beforeActiveIndex = _browser.Workspace.ActiveTabIndex;
        Guid? beforeActiveTabId = _browser.Workspace.ActiveTabSnapshot?.Id ?? currentState.Id;
        BrowserTabState beforeActiveState = currentState.Clone();
        List<BrowserTabCreationExecution> executions = [];
        BrowserTabState nextState = currentState;
        string nextPath = currentPath;
        foreach (string initialPath in initialPaths)
        {
            BrowserTabCreationExecution execution = ExecuteCreateTab(
                nextState,
                nextPath,
                initialPath,
                useConfiguredInsertion: true,
                options,
                columnCount,
                shellState);
            executions.Add(execution);
            if (!execution.Activated)
            {
                break;
            }

            nextState = _browser.Workspace.ActiveTabSnapshot ?? nextState;
            nextPath = nextState.CurrentPath;
        }

        if (executions.Count != initialPaths.Count || executions.Any(static item => !item.Activated))
        {
            _browser.Workspace.ReplaceActiveTabs(categoryId, beforeTabs, beforeActiveIndex);
            _browser.Workspace.PrepareTabActivation();
            BrowserTabSwitchWorkflowResult? rollback = beforeTabs.Count == 0
                ? null
                : ExecuteTabSwitch(
                    Math.Clamp(beforeActiveIndex, 0, beforeTabs.Count - 1),
                    currentPath,
                    beforeActiveState,
                    options,
                    columnCount,
                    shellState);
            return new BrowserTabCreationBatchExecution(executions, true, rollback);
        }

        Guid? historyOperationId = null;
        if (recordHistory)
        {
            BrowserTabBatchHistoryEntry historyEntry = CreateTabBatchHistoryEntry(
                categoryId,
                initialPaths,
                beforeActiveTabId,
                beforeActiveState,
                beforeActiveIndex,
                executions);
            historyOperationId = historyEntry.OperationId;
            _tabUndoHistory.Push(historyEntry);
            _tabRedoHistory.Clear();
        }

        return new BrowserTabCreationBatchExecution(executions, true)
        {
            HistoryOperationId = historyOperationId
        };
    }

    public BrowserTabBatchHistoryExecution ExecuteTabBatchUndo(
        BrowserTabState currentState,
        string currentPath,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        if (_tabUndoHistory.Count == 0)
        {
            return BrowserTabBatchHistoryExecution.NotAvailable;
        }

        BrowserTabBatchHistoryEntry entry = _tabUndoHistory.Peek();
        if (_browser.Workspace.FindCategorySnapshot(entry.CategoryId) == null)
        {
            return BrowserTabBatchHistoryExecution.Failed("Undo対象のカテゴリが存在しません。");
        }

        IReadOnlyCollection<Guid> recordedIds = entry.Items.Select(static item => item.TabId).ToArray();
        bool isActiveCategory = string.Equals(
            _browser.Workspace.ActiveCategoryId,
            entry.CategoryId,
            StringComparison.OrdinalIgnoreCase);
        BrowserTabBatchIdRemovalTransition removal = isActiveCategory
            ? _browser.Workspace.RemoveTabsByIds(recordedIds)
            : _browser.Workspace.RemoveTabsByIds(
                entry.CategoryId,
                _settings.Value.Session,
                recordedIds);
        if (removal.TargetCategoryMissing)
        {
            return BrowserTabBatchHistoryExecution.Failed("Undo対象のカテゴリが存在しません。");
        }
        if (removal.BlockedByLock)
        {
            return BrowserTabBatchHistoryExecution.Blocked;
        }

        BrowserTabSwitchWorkflowResult? tabSwitch = null;
        if (removal.Applied && isActiveCategory)
        {
            _browser.Workspace.PrepareTabActivation();
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
            int targetIndex = -1;
            IReadOnlyList<BrowserTabState> remainingTabs = _browser.Workspace.TabStates;
            for (int index = 0; index < remainingTabs.Count; index++)
            {
                if (remainingTabs[index].Id == entry.BeforeActiveTabId)
                {
                    targetIndex = index;
                    break;
                }
            }
            if (targetIndex < 0)
            {
                targetIndex = removal.TargetTabIndex;
            }
            targetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, _browser.Workspace.TabCount - 1));
            if (_browser.Workspace.TabCount > 0)
            {
                BrowserTabState targetState = _browser.Workspace.GetTabSnapshot(targetIndex)!;
                tabSwitch = ExecuteTabSwitch(
                    targetIndex,
                    currentPath,
                    targetState,
                    options,
                    columnCount,
                    shellState);
            }
        }

        _tabUndoHistory.Pop();
        _tabRedoHistory.Push(entry);
        return new BrowserTabBatchHistoryExecution(
            BrowserTabBatchHistoryExecutionKind.Undone,
            tabSwitch,
            removal.Applied ? null : "記録済みタブはすでに閉じられています。",
            entry.OperationId);
    }

    public BrowserTabBatchHistoryExecution ExecuteTabBatchRedo(
        BrowserTabState currentState,
        string currentPath,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        if (_tabRedoHistory.Count == 0)
        {
            return BrowserTabBatchHistoryExecution.NotAvailable;
        }

        BrowserTabBatchHistoryEntry entry = _tabRedoHistory.Peek();
        if (_browser.Workspace.FindCategorySnapshot(entry.CategoryId) == null)
        {
            return BrowserTabBatchHistoryExecution.Failed("Redo対象のカテゴリが存在しません。");
        }
        if (entry.Items.Any(item => !Directory.Exists(item.DirectoryPath)) ||
            !_browser.Workspace.TryGetCategoryTabCount(
                _settings.Value.Session,
                entry.CategoryId,
                out int targetTabCount) ||
            targetTabCount + entry.Items.Count > MaxTabCount)
        {
            return BrowserTabBatchHistoryExecution.Failed("Redo対象のディレクトリまたはタブ容量を確認できません。");
        }

        bool isActiveCategory = string.Equals(
            _browser.Workspace.ActiveCategoryId,
            entry.CategoryId,
            StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<BrowserTabState> recreatedStates;
        IReadOnlyList<int> recreatedInsertionIndices;
        BrowserTabSwitchWorkflowResult? tabSwitch = null;
        if (isActiveCategory)
        {
            BrowserTabCreationBatchExecution batch = ExecuteCreateTabsCore(
                entry.Items.Select(static item => item.DirectoryPath).ToArray(),
                currentState,
                currentPath,
                options,
                columnCount,
                shellState,
                recordHistory: false);
            if (!batch.Succeeded)
            {
                return BrowserTabBatchHistoryExecution.Failed("Redo対象のタブを一括復元できませんでした。");
            }

            recreatedStates = batch.Items.Select(static item => item.Creation.State!).ToList();
            recreatedInsertionIndices = recreatedStates
                .Select(state => _browser.Workspace.TabStates
                    .Select((tab, index) => (tab, index))
                    .First(item => item.tab.Id == state.Id).index)
                .ToList();
            tabSwitch = batch.Items.LastOrDefault().Activation;
        }
        else
        {
            BrowserTabCategoryBatchCreationTransition creation = _browser.Workspace.CreateTabsForCategory(
                entry.CategoryId,
                _settings.Value.Session,
                entry.Items.Select(static item => item.State).ToArray(),
                entry.Items.Select(static item => item.InsertionIndex).ToArray(),
                entry.BeforeActiveTabId,
                entry.BeforeActiveTabIndex,
                MaxTabCount);
            if (!creation.Applied)
            {
                return BrowserTabBatchHistoryExecution.Failed(
                    creation.TargetCategoryMissing
                        ? "Redo対象のカテゴリが存在しません。"
                        : "Redo対象のタブを一括復元できませんでした。");
            }

            recreatedStates = creation.Items;
            recreatedInsertionIndices = creation.InsertionIndices;
        }

        _tabRedoHistory.Pop();
        BrowserTabBatchHistoryEntry redoEntry = CreateTabBatchHistoryEntry(
            entry.CategoryId,
            entry.Items.Select(static item => item.DirectoryPath).ToArray(),
            entry.BeforeActiveTabId,
            entry.BeforeActiveState,
            entry.BeforeActiveTabIndex,
            recreatedStates,
            recreatedInsertionIndices);
        _tabUndoHistory.Push(redoEntry);
        return new BrowserTabBatchHistoryExecution(
            BrowserTabBatchHistoryExecutionKind.Redone,
            tabSwitch,
            null,
            redoEntry.OperationId);
    }

    public bool CanUndoTabBatch => _tabUndoHistory.Count > 0;
    public bool CanRedoTabBatch => _tabRedoHistory.Count > 0;

    public bool HasTabBatchUndo(Guid operationId) =>
        _tabUndoHistory.Count > 0 && _tabUndoHistory.Peek().OperationId == operationId;

    public bool HasTabBatchRedo(Guid operationId) =>
        _tabRedoHistory.Count > 0 && _tabRedoHistory.Peek().OperationId == operationId;

    private BrowserTabBatchHistoryEntry CreateTabBatchHistoryEntry(
        string categoryId,
        IReadOnlyList<string> paths,
        Guid? beforeActiveTabId,
        BrowserTabState beforeActiveState,
        int beforeActiveTabIndex,
        IReadOnlyList<BrowserTabCreationExecution> executions)
    {
        IReadOnlyList<BrowserTabState> states = executions
            .Select(static execution => execution.Creation.State!)
            .ToList();
        IReadOnlyList<int> insertionIndices = states
            .Select(state => _browser.Workspace.TabStates
                .Select((tab, index) => (tab, index))
                .First(item => item.tab.Id == state.Id).index)
            .ToList();
        return CreateTabBatchHistoryEntry(
            categoryId,
            paths,
            beforeActiveTabId,
            beforeActiveState,
            beforeActiveTabIndex,
            states,
            insertionIndices);
    }

    private static BrowserTabBatchHistoryEntry CreateTabBatchHistoryEntry(
        string categoryId,
        IReadOnlyList<string> paths,
        Guid? beforeActiveTabId,
        BrowserTabState beforeActiveState,
        int beforeActiveTabIndex,
        IReadOnlyList<BrowserTabState> states,
        IReadOnlyList<int> insertionIndices)
    {
        var items = new List<BrowserTabBatchHistoryItem>(states.Count);
        foreach ((string path, BrowserTabState state, int insertionIndex) in paths.Zip(states, (path, state) => (path, state))
                     .Zip(insertionIndices, (pair, insertionIndex) => (pair.path, pair.state, insertionIndex)))
        {
            items.Add(new BrowserTabBatchHistoryItem(
                state.Id,
                path,
                insertionIndex,
                state.Clone()));
        }

        return new BrowserTabBatchHistoryEntry(
            categoryId,
            items,
            beforeActiveTabId,
            beforeActiveTabIndex,
            beforeActiveState.Clone());
    }

    public int MaxTabCount =>
        BrowserWorkspaceApplicationCoordinator.GetMaxTabCount(_settings.Value.BrowserTabs);

    public BrowserTabSwitchWorkflowStart BeginTabSwitch(
        int tabIndex,
        string currentPath,
        BrowserTabState? currentState = null)
    {
        ApplyActiveState(currentState, validateMarks: true);
        BrowserTabState? state = _browser.Workspace.GetTabSnapshot(tabIndex);
        if (state == null)
        {
            return BrowserTabSwitchWorkflowStart.NotAvailable;
        }

        return new BrowserTabSwitchWorkflowStart(
            true,
            tabIndex,
            state,
            ResolveTargetPath(state, currentPath));
    }

    public BrowserTabSwitchWorkflowResult CompleteTabSwitch(
        BrowserTabSwitchWorkflowStart operation,
        bool materialized,
        BrowserMarkRestoreValidationContext? validationContext = null)
    {
        if (!operation.Available)
        {
            return BrowserTabSwitchWorkflowResult.NotAvailable;
        }
        if (!materialized)
        {
            return BrowserTabSwitchWorkflowResult.NotCommitted;
        }

        Stopwatch restoreStopwatch = Stopwatch.StartNew();
        BrowserTabActivationTransition activation = ActivateTabCore(
            operation.TargetTabIndex,
            validationContext: validationContext);
        restoreStopwatch.Stop();
        if (!activation.Applied)
        {
            return BrowserTabSwitchWorkflowResult.NotCommitted;
        }

        Stopwatch markStateStopwatch = Stopwatch.StartNew();
        BrowserTabState state = ApplyRestoredTabMarks(operation.TargetTabIndex, activation, out int skippedMarkCount);
        markStateStopwatch.Stop();
        Stopwatch persistStopwatch = Stopwatch.StartNew();
        _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        persistStopwatch.Stop();
        LogService.Info(
            $"[BrowserTabSwitchPerf] restoreMs={restoreStopwatch.ElapsedMilliseconds} " +
            $"markStateMs={markStateStopwatch.ElapsedMilliseconds} persistCurrentStateMs={persistStopwatch.ElapsedMilliseconds} " +
            $"restoredCount={state.MarkedPaths.Count} skippedCount={skippedMarkCount} " +
            $"snapshotFresh={activation.SnapshotPathKindsAreFresh} snapshotHits={activation.SnapshotMarkHitCount} " +
            $"fallbackValidations={activation.FallbackValidationCount} " +
            $"fallbackFileProbes={activation.FallbackFileProbeCount} " +
            $"fallbackDirectoryProbes={activation.FallbackDirectoryProbeCount} " +
            $"parentBatchGroups={activation.ParentBatchGroupCount} " +
            $"parentBatchPaths={activation.ParentBatchPathCount} " +
            $"parentBatchHits={activation.ParentBatchHitCount} " +
            $"parentBatchMisses={activation.ParentBatchMissCount} " +
            $"parentBatchFailures={activation.ParentBatchFailureCount} " +
            $"individualFallbackValidations={activation.FallbackValidationCount} " +
            $"individualFileProbes={activation.FallbackFileProbeCount} " +
            $"individualDirectoryProbes={activation.FallbackDirectoryProbeCount}");
        return new BrowserTabSwitchWorkflowResult(
            BrowserTabSwitchWorkflowResultKind.Committed,
            operation.TargetTabIndex,
            state,
            state.MarkedPaths,
            skippedMarkCount)
        {
            SnapshotMarkHitCount = activation.SnapshotMarkHitCount,
            FallbackValidationCount = activation.FallbackValidationCount,
            FallbackFileProbeCount = activation.FallbackFileProbeCount,
            FallbackDirectoryProbeCount = activation.FallbackDirectoryProbeCount,
            ParentBatchGroupCount = activation.ParentBatchGroupCount,
            ParentBatchPathCount = activation.ParentBatchPathCount,
            ParentBatchHitCount = activation.ParentBatchHitCount,
            ParentBatchMissCount = activation.ParentBatchMissCount,
            ParentBatchFailureCount = activation.ParentBatchFailureCount,
            SnapshotPathKindsAreFresh = activation.SnapshotPathKindsAreFresh
        };
    }

    public BrowserTabSwitchWorkflowResult ExecuteTabSwitch(
        int tabIndex,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default,
        bool recordVisitHistory = false)
    {
        using (recordVisitHistory ? BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record) : null)
        {
            BrowserTabVisitLocation fromLocation = new(
                _browser.Workspace.ActiveCategoryId ?? BrowserTabSettings.DefaultCategoryId,
                _browser.Workspace.ActiveTabSnapshot?.Id ?? currentState.Id);
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            Stopwatch beginStopwatch = Stopwatch.StartNew();
            PrepareForBrowserWorkspaceNavigation();
            BrowserTabSwitchWorkflowStart operation = BeginTabSwitch(tabIndex, currentPath, currentState);
        beginStopwatch.Stop();
        if (!operation.Available || operation.TargetState == null)
        {
            return BrowserTabSwitchWorkflowResult.NotAvailable;
        }

        BrowserTabState targetState = operation.TargetState;
        BrowserDirectoryLoadOptions targetOptions = _browser.CreateTargetMaterializationOptions(targetState, options);
        Stopwatch directoryLoadStopwatch = Stopwatch.StartNew();
        BrowserDirectoryLoadExecution load = ExecuteTargetLoad(targetState, operation.TargetPath, targetOptions);
        directoryLoadStopwatch.Stop();
        if (!load.Succeeded)
        {
            return BrowserTabSwitchWorkflowResult.NotCommitted with
            {
                TargetTabIndex = operation.TargetTabIndex,
                DirectoryLoad = BrowserDirectoryLoadApplicationResult.Failure(load.Error!)
            };
        }

        Stopwatch restoreAndPersistStopwatch = Stopwatch.StartNew();
        BrowserTabSwitchWorkflowResult completion = CompleteTabSwitch(
            operation,
            materialized: true,
            validationContext: CreateMarkRestoreValidationContext(load.Result!));
        restoreAndPersistStopwatch.Stop();
        if (!completion.Committed)
        {
            return completion with
            {
                TargetTabIndex = operation.TargetTabIndex,
                DirectoryLoad = BrowserDirectoryLoadApplicationResult.Success(load.Result!, default)
            };
        }

        Stopwatch applyDirectoryStopwatch = Stopwatch.StartNew();
        BrowserDirectoryApplicationTransition transition = _browser.Directory.ApplyDirectoryLoad(
            load.Result!,
            targetOptions.ItemsPerPage,
            Math.Clamp(targetState.ColumnCount, 1, 9),
            targetState.SortKind,
            targetState.SortAscending,
            isSwitchingBrowserTab: true);
        applyDirectoryStopwatch.Stop();
        totalStopwatch.Stop();
        LogService.Info(
            $"[BrowserTabSwitchPerf] core totalMs={totalStopwatch.ElapsedMilliseconds} " +
            $"beginMs={beginStopwatch.ElapsedMilliseconds} directoryLoadMs={directoryLoadStopwatch.ElapsedMilliseconds} " +
            $"restorePersistMs={restoreAndPersistStopwatch.ElapsedMilliseconds} applyDirectoryMs={applyDirectoryStopwatch.ElapsedMilliseconds} " +
            $"markCount={currentState.MarkedPaths.Count} restoredCount={completion.RestoredMarks.Count} " +
            $"skippedCount={completion.SkippedMarkCount} " +
            $"snapshotFresh={completion.SnapshotPathKindsAreFresh} snapshotHits={completion.SnapshotMarkHitCount} " +
            $"snapshotReused={load.Result!.ReusedSnapshot} " +
            $"fallbackValidations={completion.FallbackValidationCount} " +
            $"fallbackFileProbes={completion.FallbackFileProbeCount} " +
            $"fallbackDirectoryProbes={completion.FallbackDirectoryProbeCount} " +
            $"parentBatchGroups={completion.ParentBatchGroupCount} " +
            $"parentBatchPaths={completion.ParentBatchPathCount} " +
            $"parentBatchHits={completion.ParentBatchHitCount} " +
            $"parentBatchMisses={completion.ParentBatchMissCount} " +
            $"parentBatchFailures={completion.ParentBatchFailureCount} " +
            $"individualFallbackValidations={completion.FallbackValidationCount} " +
            $"individualFileProbes={completion.FallbackFileProbeCount} " +
            $"individualDirectoryProbes={completion.FallbackDirectoryProbeCount}");
        BrowserTabVisitLocation toLocation = new(
            _browser.Workspace.ActiveCategoryId ?? BrowserTabSettings.DefaultCategoryId,
            completion.State?.Id ?? targetState.Id);
        _visitHistory.RecordNavigation(fromLocation, toLocation);
            return completion with
            {
                DirectoryLoad = BrowserDirectoryLoadApplicationResult.Success(load.Result!, transition),
                PostLoadEffects = _refreshWorkflow?.PreparePostLoadEffects(shellState) ?? default
            };
        }
    }

    public BrowserTabSwitchWorkflowResult ActivateCreatedTab(int tabIndex)
    {
        BrowserTabActivationTransition activation = ActivateTabCore(tabIndex);
        if (!activation.Applied)
        {
            return BrowserTabSwitchWorkflowResult.NotCommitted;
        }

        BrowserTabState state = ApplyRestoredTabMarks(tabIndex, activation, out int skippedMarkCount);
        _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        return new BrowserTabSwitchWorkflowResult(
            BrowserTabSwitchWorkflowResultKind.Committed,
            tabIndex,
            state,
            state.MarkedPaths,
            skippedMarkCount);
    }

    public BrowserTabToggleLockExecution ExecuteToggleLock(
        int tabIndex,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserTabSwitchWorkflowResult? switchResult = null;
        if (_browser.Workspace.ActiveTabIndex != tabIndex)
        {
            switchResult = ExecuteTabSwitch(
                tabIndex,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState);
            if (!switchResult.Value.Committed)
            {
                return new BrowserTabToggleLockExecution(switchResult, default);
            }
        }

        return new BrowserTabToggleLockExecution(
            switchResult,
            ToggleLock(tabIndex, _browser.CurrentPath));
    }

    public BrowserTabToggleReadOnlyExecution ExecuteToggleReadOnly(
        int tabIndex,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserTabSwitchWorkflowResult? switchResult = null;
        if (_browser.Workspace.ActiveTabIndex != tabIndex)
        {
            switchResult = ExecuteTabSwitch(
                tabIndex,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState);
            if (!switchResult.Value.Committed)
            {
                return new BrowserTabToggleReadOnlyExecution(switchResult, default);
            }
        }

        return new BrowserTabToggleReadOnlyExecution(
            switchResult,
            ToggleReadOnly(tabIndex));
    }

    public BrowserUnifiedFilterExecution ExecuteUnifiedFilter(
        int tabIndex,
        string pattern,
        bool useRegex,
        TabFilterLockState filterLock,
        bool isActive,
        bool isBrowserMode,
        bool isBusy,
        string reason,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        _browser.Workspace.SetTabFilterState(tabIndex, pattern, useRegex, filterLock);
        if (isActive)
        {
            _browser.SetFilter(pattern, useRegex);
        }

        BrowserDirectoryLoadOptions refreshOptions = options with
        {
            FilterPattern = pattern,
            FilterUseRegex = useRegex,
            FilterLock = filterLock
        };
        if (refreshOptions.LayoutProjectionInput is { } layoutInput)
        {
            bool filterActive = TabFilterLockService.IsActive(pattern, filterLock);
            layoutInput = layoutInput with
            {
                LeadingPresentationSlotCount = BrowserLayoutProjection.GetLeadingPresentationSlotCount(
                    filterActive)
            };
            refreshOptions = refreshOptions with
            {
                LayoutProjectionInput = layoutInput,
                ItemsPerPage = layoutInput.GetItemsPerPage(columnCount)
            };
        }

        BrowserManualRefreshExecution refresh = !isActive
            ? default
            : _refreshWorkflow?.ExecuteManualRefresh(
                isBrowserMode,
                isBusy,
                reason,
                refreshOptions,
                columnCount,
                shellState) ?? default;
        return new BrowserUnifiedFilterExecution(pattern, useRegex, filterLock, isActive, refresh);
    }

    public BrowserTabCloseExecution ExecuteCloseTab(
        int tabIndex,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        PrepareForBrowserWorkspaceNavigation();
        BrowserTabCloseDecision decision = EvaluateClose(tabIndex);
        if (decision.Kind is BrowserTabCloseDecisionKind.Locked or
            BrowserTabCloseDecisionKind.LastTab or
            BrowserTabCloseDecisionKind.RequiresCategoryRemoval)
        {
            return new BrowserTabCloseExecution(decision, null, null);
        }

        BrowserTabSwitchWorkflowResult? beforeClose = null;
        if (_browser.Workspace.ActiveTabIndex != tabIndex)
        {
            beforeClose = ExecuteTabSwitch(
                tabIndex,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState);
            if (!beforeClose.Value.Committed)
            {
                return new BrowserTabCloseExecution(decision, beforeClose, null);
            }
        }

        BrowserTabCloseTransition closed = RemoveTab(tabIndex);
        if (!closed.Applied)
        {
            return new BrowserTabCloseExecution(decision, beforeClose, null);
        }

        BrowserTabSwitchWorkflowResult afterClose = ExecuteTabSwitch(
            closed.TargetTabIndex,
            _browser.CurrentPath,
            _browser.Workspace.ActiveTabSnapshot ?? currentState,
            options,
            columnCount,
            shellState);
        return new BrowserTabCloseExecution(decision, beforeClose, new BrowserTabCloseCompletion(closed, afterClose));
    }

    public BrowserTabRangeCloseExecution ExecuteCloseTabs(
        IReadOnlyList<int> tabIndices,
        int preferredTabIndex,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        PrepareForBrowserWorkspaceNavigation();
        IReadOnlyList<int> closable = GetClosableTabIndices(tabIndices);
        if (closable.Count == 0)
        {
            return new BrowserTabRangeCloseExecution(closable, null, null);
        }

        BrowserTabSwitchWorkflowResult? beforeClose = null;
        if (_browser.Workspace.ActiveTabIndex != preferredTabIndex)
        {
            beforeClose = ExecuteTabSwitch(
                preferredTabIndex,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState);
            if (!beforeClose.Value.Committed)
            {
                return new BrowserTabRangeCloseExecution(closable, beforeClose, null);
            }
        }

        BrowserTabRangeCloseTransition closed = RemoveTabs(tabIndices, preferredTabIndex);
        if (!closed.Applied)
        {
            return new BrowserTabRangeCloseExecution(closable, beforeClose, null);
        }

        BrowserTabSwitchWorkflowResult afterClose = ExecuteTabSwitch(
            closed.TargetTabIndex,
            _browser.CurrentPath,
            _browser.Workspace.ActiveTabSnapshot ?? currentState,
            options,
            columnCount,
            shellState);
        return new BrowserTabRangeCloseExecution(
            closable,
            beforeClose,
            new BrowserTabRangeCloseCompletion(closed, afterClose));
    }

    public BrowserTabLockTransition ToggleLock(int tabIndex, string currentPath) =>
        _browser.Workspace.ToggleLock(tabIndex, currentPath);

    public BrowserTabReadOnlyTransition ToggleReadOnly(int tabIndex) =>
        _browser.Workspace.ToggleReadOnly(tabIndex);

    public void SetFilterLock(int tabIndex, TabFilterLockState filterLock) =>
        _browser.Workspace.SetTabFilterLock(tabIndex, filterLock);

    public void ApplyCapturedState(
        int tabIndex,
        BrowserTabState state,
        bool captureMarks,
        bool validateMarks,
        bool markValidationSucceeded) =>
        _browser.Workspace.ApplyCapturedState(
            tabIndex,
            state,
            captureMarks,
            validateMarks,
            markValidationSucceeded);

    public void SyncActiveTabMarks(IEnumerable<string> paths) =>
        _browser.Workspace.SyncActiveTabMarks(paths);

    public BrowserMarkMutationCommit CommitMarkMutation(MarkSummaryDelta? summaryDelta)
    {
        bool exactDeltaApplied = summaryDelta.HasValue &&
            _browser.TryApplyMarkSummaryDelta(summaryDelta.Value);
        SyncActiveTabMarks(_browser.Selection.Snapshot());
        return new BrowserMarkMutationCommit(exactDeltaApplied);
    }

    public BrowserSelectionMarkExecution ExecuteToggleMark(string path, bool adding)
    {
        MarkSummaryDelta? summaryDelta = _browser.TryPrepareMarkSummaryDelta(
            path,
            adding,
            out MarkSummaryDelta delta)
            ? delta
            : null;
        BrowserSelectionMarkTransition transition = _browser.ToggleMark(path);
        BrowserMarkMutationCommit commit = transition.Changed
            ? CommitMarkMutation(summaryDelta)
            : default;
        return new BrowserSelectionMarkExecution(transition, commit);
    }

    public int AddMarksAndSync(IEnumerable<string> paths)
    {
        int count = _browser.AddMarks(paths);
        if (count > 0)
        {
            SyncActiveTabMarks(_browser.Selection.Snapshot());
        }
        return count;
    }

    public int RemoveMarksAndSync(IEnumerable<string> paths)
    {
        int count = _browser.RemoveMarks(paths);
        if (count > 0)
        {
            SyncActiveTabMarks(_browser.Selection.Snapshot());
        }
        return count;
    }

    public int ClearMarksAndSync()
    {
        int count = _browser.Selection.Count;
        if (count > 0)
        {
            _browser.ClearMarks();
            SyncActiveTabMarks(_browser.Selection.Snapshot());
        }
        return count;
    }

    public void RestoreMarksAndSync(IEnumerable<string> paths)
    {
        _browser.RestoreMarks(paths);
        SyncActiveTabMarks(_browser.Selection.Snapshot());
    }

    public void RestoreMarksAndSync(IEnumerable<MarkPathKind> paths)
    {
        _browser.RestoreMarksWithKnownKinds(paths);
        SyncActiveTabMarks(_browser.Selection.Snapshot());
    }

    public void ReplaceMarksAndSync(IEnumerable<MarkPathKind> paths)
    {
        _browser.RestoreMarksWithKnownKinds(paths);
        SyncActiveTabMarks(_browser.Selection.Snapshot());
    }

    public BrowserBulkMarkTransition ExecuteToggleBulkMarksAndSync(IReadOnlyList<string> targets)
    {
        BrowserBulkMarkTransition transition = _browser.ToggleBulkMarks(targets);
        return ApplyBulkMarkTransition(transition);
    }

    public BrowserBulkMarkTransition ExecuteMarkBulkAndSync(IReadOnlyList<string> targets)
    {
        BrowserBulkMarkTransition transition = _browser.MarkBulk(targets);
        return ApplyBulkMarkTransition(transition);
    }

    public BrowserBulkMarkTransition ExecuteInvertBulkMarksAndSync(IReadOnlyList<string> targets)
    {
        BrowserBulkMarkTransition transition = _browser.InvertBulkMarks(targets);
        return ApplyBulkMarkTransition(transition);
    }

    private BrowserBulkMarkTransition ApplyBulkMarkTransition(BrowserBulkMarkTransition transition)
    {
        if (transition.Changed)
        {
            _browser.RestoreMarks(transition.NextMarks);
            SyncActiveTabMarks(_browser.Selection.Snapshot());
        }
        return transition;
    }

    public void SyncActiveStateAndPersist(BrowserTabState activeState)
    {
        ApplyActiveState(activeState, validateMarks: false);
        _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
    }

    public void SyncActiveState(BrowserTabState activeState) =>
        ApplyActiveState(activeState, validateMarks: false);

    public BrowserMarkPersistenceTransition ClearCategoryMarks(
        string categoryId,
        BrowserTabState activeState)
    {
        ApplyActiveState(activeState, validateMarks: false);
        return _persistence.ClearCategoryMarks(categoryId);
    }

    public BrowserMarkPersistenceTransition ClearAllMarks(BrowserTabState activeState)
    {
        ApplyActiveState(activeState, validateMarks: false);
        return _persistence.ClearAllMarks();
    }

    public BrowserMarkPersistenceTransition PersistAfterMarkMutation(BrowserTabState activeState)
    {
        ApplyActiveState(activeState, validateMarks: false);
        return _persistence.PersistAfterMarkMutation();
    }

    public void SetTitle(int tabIndex, string title) =>
        _browser.Workspace.SetTabTitle(tabIndex, title);

    public int ResolveAdjacentIndex(int delta, bool wrap) =>
        _browser.Workspace.ResolveAdjacentTabIndex(delta, wrap);

    public BrowserTabSwitchWorkflowResult ExecuteAdjacentSwitch(
        int delta,
        bool wrap,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        int targetIndex = ResolveAdjacentIndex(delta, wrap);
        return targetIndex < 0
            ? BrowserTabSwitchWorkflowResult.NotAvailable
            : ExecuteTabSwitch(
                targetIndex,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState);
    }

    public BrowserTabHistoryNavigationResult ExecuteTabHistoryNavigation(
        BrowserHistoryDirection direction,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default) =>
        ExecuteTabHistoryNavigation(
            direction,
            depth: 0,
            currentPath,
            currentState,
            options,
            columnCount,
            shellState);

    public BrowserTabHistoryNavigationResult ExecuteTabHistoryJump(
        BrowserTabVisitHistoryListDirection direction,
        int depth,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        return direction switch
        {
            BrowserTabVisitHistoryListDirection.Back => ExecuteTabHistoryNavigation(
                BrowserHistoryDirection.Back,
                depth,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState),
            BrowserTabVisitHistoryListDirection.Forward => ExecuteTabHistoryNavigation(
                BrowserHistoryDirection.Forward,
                depth,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState),
            _ => BrowserTabHistoryNavigationResult.Failed(BrowserHistoryDirection.Back, "現在のタブは移動先にできません。")
        };
    }

    private BrowserTabHistoryNavigationResult ExecuteTabHistoryNavigation(
        BrowserHistoryDirection direction,
        int depth,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState)
    {
        _visitHistory.PruneInvalidEntries(IsVisitLocationValid);

        bool hasTarget = direction == BrowserHistoryDirection.Back
            ? _visitHistory.TryGetBackAt(depth, out BrowserTabVisitLocation target)
            : _visitHistory.TryGetForwardAt(depth, out target);

        if (!hasTarget)
        {
            return BrowserTabHistoryNavigationResult.Failed(direction, "タブ履歴がありません。");
        }

        BrowserTabVisitLocation currentLocation = new(
            _browser.Workspace.ActiveCategoryId ?? BrowserTabSettings.DefaultCategoryId,
            _browser.Workspace.ActiveTabSnapshot?.Id ?? currentState.Id);

        using (_visitHistory.BeginModeScope(BrowserTabVisitHistoryMode.Replay))
        {
            if (string.Equals(target.CategoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                int targetIndex = -1;
                IReadOnlyList<BrowserTabState> tabStates = _browser.Workspace.TabStates;
                for (int i = 0; i < tabStates.Count; i++)
                {
                    if (tabStates[i].Id == target.TabId)
                    {
                        targetIndex = i;
                        break;
                    }
                }
                if (targetIndex < 0)
                {
                    return BrowserTabHistoryNavigationResult.Failed(direction);
                }

                BrowserTabSwitchWorkflowResult result = ExecuteTabSwitch(
                    targetIndex,
                    currentPath,
                    currentState,
                    options,
                    columnCount,
                    shellState);

                if (result.Committed)
                {
                    if (direction == BrowserHistoryDirection.Back)
                    {
                        _visitHistory.CommitBack(currentLocation, target, depth);
                    }
                    else
                    {
                        _visitHistory.CommitForward(currentLocation, target, depth);
                    }
                    return BrowserTabHistoryNavigationResult.ForTabSwitch(direction, result);
                }
                return BrowserTabHistoryNavigationResult.Failed(direction);
            }
            else
            {
                BrowserTabRestoreCategoryState? storedCategory = _persistence.FindStoredCategory(target.CategoryId);
                int targetIndex = storedCategory?.OpenTabs?.FindIndex(t => t.TabId == target.TabId) ?? -1;
                if (targetIndex < 0)
                {
                    return BrowserTabHistoryNavigationResult.Failed(direction);
                }

                BrowserCategorySwitchWorkflowResult result = ExecuteCategorySwitch(
                    target.CategoryId,
                    targetIndex,
                    currentPath,
                    currentState,
                    options,
                    columnCount,
                    shellState);

                if (result.Committed)
                {
                    if (direction == BrowserHistoryDirection.Back)
                    {
                        _visitHistory.CommitBack(currentLocation, target, depth);
                    }
                    else
                    {
                        _visitHistory.CommitForward(currentLocation, target, depth);
                    }
                    return BrowserTabHistoryNavigationResult.ForCategorySwitch(direction, result);
                }
                return BrowserTabHistoryNavigationResult.Failed(direction);
            }
        }
    }

    private BrowserTabVisitLocation GetActiveVisitLocation() =>
        new(
            _browser.Workspace.ActiveCategoryId ?? BrowserTabSettings.DefaultCategoryId,
            _browser.Workspace.ActiveTabSnapshot?.Id ?? Guid.Empty);

    private bool IsVisitLocationValid(BrowserTabVisitLocation location)
    {
        if (location.IsEmpty || _browser.Workspace.FindCategorySnapshot(location.CategoryId) == null)
        {
            return false;
        }

        if (string.Equals(location.CategoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return _browser.Workspace.TabStates.Any(tab => tab.Id == location.TabId);
        }

        return _persistence.FindStoredCategory(location.CategoryId)?.OpenTabs?.Any(tab => tab.TabId == location.TabId) == true;
    }

    private bool TryBuildVisitHistoryItem(
        BrowserTabVisitHistoryListDirection direction,
        int depth,
        BrowserTabVisitLocation location,
        out BrowserTabVisitHistoryListItem item)
    {
        item = null!;
        BrowserTabCategoryDefinition? category = _browser.Workspace.FindCategorySnapshot(location.CategoryId);
        if (category == null)
        {
            return false;
        }

        string categoryName = string.IsNullOrWhiteSpace(category.DisplayName)
            ? location.CategoryId
            : category.DisplayName;
        string? path;
        string? title;
        if (string.Equals(location.CategoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            BrowserTabState? tab = _browser.Workspace.TabStates.FirstOrDefault(candidate => candidate.Id == location.TabId);
            if (tab == null)
            {
                return false;
            }

            path = tab.CurrentPath;
            title = tab.Title;
        }
        else
        {
            BrowserTabSessionState? tab = _persistence.FindStoredCategory(location.CategoryId)?.OpenTabs?
                .FirstOrDefault(candidate => candidate.TabId == location.TabId);
            if (tab == null)
            {
                return false;
            }

            path = tab.CurrentPath;
            title = null;
        }

        string displayTitle = string.IsNullOrWhiteSpace(title)
            ? BrowserTabPresentationHelper.BuildTabTitle(
                path,
                normalizedPath => QuickAccessService.FindAliasDisplayName(
                    _browser.Workspace.QuickAccessSnapshot,
                    normalizedPath))
            : title;
        item = new BrowserTabVisitHistoryListItem(
            direction,
            depth,
            location.CategoryId,
            location.TabId,
            categoryName,
            displayTitle,
            path ?? string.Empty);
        return true;
    }

    public void SetContextTabIndex(int index) =>
        _browser.Workspace.SetContextTabIndex(index);

    public BrowserTabCloseDecision EvaluateClose(int tabIndex) =>
        _browser.Workspace.EvaluateTabClose(tabIndex);

    public IReadOnlyList<int> GetClosableTabIndices(IEnumerable<int> candidates) =>
        _browser.Workspace.GetClosableTabIndices(candidates);

    public int CountClosableTabs(BrowserTabCloseScope scope) =>
        _browser.Workspace.CountClosableTabs(scope);

    public bool ReorderTab(
        int fromIndex,
        int toIndex,
        BrowserTabState? currentState = null)
    {
        ApplyActiveState(currentState, validateMarks: false);
        bool applied = _browser.Workspace.ReorderTab(fromIndex, toIndex);
        if (applied)
        {
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        }

        return applied;
    }

    public bool ReorderTabById(
        string categoryId,
        Guid tabId,
        Guid anchorTabId,
        bool insertAfter,
        BrowserTabState? currentState = null)
    {
        bool activeCategory = string.Equals(
            _browser.Workspace.ResolveCategoryId(categoryId),
            _browser.Workspace.ActiveCategoryId,
            StringComparison.OrdinalIgnoreCase);
        if (activeCategory) ApplyActiveState(currentState, validateMarks: false);

        bool applied = _browser.Workspace.ReorderTabById(
            categoryId,
            tabId,
            anchorTabId,
            insertAfter,
            _settings.Value.Session);
        if (applied && activeCategory)
        {
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        }

        return applied;
    }

    public BrowserTabCloseTransition RemoveTab(int tabIndex)
    {
        _browser.Workspace.PushClosedTabSnapshot(tabIndex, BrowserTabWorkflowConstants.ClosedBrowserTabHistoryLimit);
        BrowserTabCloseTransition transition = _browser.Workspace.RemoveTab(tabIndex);
        if (transition.Applied)
        {
            _browser.Workspace.PrepareTabActivation();
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        }

        return transition;
    }

    public BrowserTabRangeCloseTransition RemoveTabs(
        IReadOnlyList<int> tabIndices,
        int preferredTabIndex)
    {
        BrowserTabRangeCloseTransition transition = _browser.Workspace.RemoveTabs(tabIndices, preferredTabIndex);
        if (transition.Applied)
        {
            _browser.Workspace.PrepareTabActivation();
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        }

        return transition;
    }

    public void PushClosedTabSnapshot(int tabIndex) =>
        _browser.Workspace.PushClosedTabSnapshot(tabIndex, BrowserTabWorkflowConstants.ClosedBrowserTabHistoryLimit);

    public BrowserClosedTabRestoreTransition RestoreLastClosedTab()
    {
        BrowserClosedTabRestoreTransition transition = _browser.Workspace.RestoreLastClosedTab(
            BrowserWorkspaceApplicationCoordinator.GetMaxTabCount(_settings.Value.BrowserTabs));
        if (transition.Kind == BrowserClosedTabRestoreKind.Restored)
        {
            _browser.Workspace.PrepareTabActivation();
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        }

        return transition;
    }

    public BrowserClosedTabRestoreExecution ExecuteRestoreLastClosedTab(
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        PrepareForBrowserWorkspaceNavigation();
        BrowserClosedTabRestoreTransition transition = RestoreLastClosedTab();
        BrowserCategorySwitchWorkflowResult? categorySwitch = null;
        if (transition.Kind == BrowserClosedTabRestoreKind.RequiresCategorySwitch &&
            transition.TargetCategoryId != null)
        {
            BrowserCategorySwitchWorkflowResult category = ExecuteCategorySwitch(
                transition.TargetCategoryId,
                requestedTabIndex: null,
                currentPath,
                currentState,
                options,
                columnCount,
                shellState);
            if (!category.Committed && category.Kind != BrowserCategorySwitchWorkflowResultKind.NoOp)
            {
                return new BrowserClosedTabRestoreExecution(transition, category, null);
            }

            transition = RestoreLastClosedTab();
            if (category.Committed)
            {
                categorySwitch = category;
            }
        }

        if (transition.Kind != BrowserClosedTabRestoreKind.Restored)
        {
            return new BrowserClosedTabRestoreExecution(transition, categorySwitch, null);
        }

        BrowserTabSwitchWorkflowResult tabSwitch = ExecuteTabSwitch(
            _browser.Workspace.TabCount - 1,
            _browser.CurrentPath,
            _browser.Workspace.ActiveTabSnapshot ?? currentState,
            options,
            columnCount,
            shellState);
        return new BrowserClosedTabRestoreExecution(transition, categorySwitch, tabSwitch);
    }

    internal void PrepareForBrowserWorkspaceNavigation()
    {
        if (_viewerMode != null && _viewerUi != null)
        {
            _viewerMode.PrepareForBrowserWorkspaceNavigation(_viewerUi);
        }
    }

    private void ApplyActiveState(BrowserTabState? currentState, bool validateMarks)
    {
        if (currentState == null)
        {
            return;
        }

        int activeTabIndex = _browser.Workspace.ActiveTabIndex;
        if (activeTabIndex >= 0 && activeTabIndex < _browser.Workspace.TabCount)
        {
            BrowserTabState existingState = _browser.Workspace.GetTabSnapshot(activeTabIndex)!;
            _browser.Workspace.ApplyCapturedState(
                activeTabIndex,
                currentState,
                captureMarks: true,
                shouldValidateMarks: validateMarks && existingState.MarksDirty,
                markValidationSucceeded: false);
        }
    }

    private BrowserTabActivationTransition ActivateTabCore(
        int tabIndex,
        bool allowPendingMarkRestoreValidation = true,
        BrowserMarkRestoreValidationContext? validationContext = null)
    {
        BrowserTabActivationTransition activation = _browser.Workspace.ActivateTabState(
            tabIndex,
            allowPendingMarkRestoreValidation,
            validationContext);
        if (activation.Applied)
        {
            _browser.SetColumnCount(activation.ColumnCount);
            _browser.SetSort(activation.SortKind, activation.SortAscending);
        }

        return activation;
    }

    private BrowserDirectoryLoadExecution ExecuteTargetLoad(
        BrowserTabState targetState,
        string targetPath,
        BrowserDirectoryLoadOptions options)
    {
        BrowserLoadCoordinator.DirectoryLoadRequest request = _browser.CreateTargetDirectoryLoadRequest(
            targetPath,
            targetState,
            isHistoryNavigation: true,
            suppressRecent: true,
            targetOptions: options);
        return _browser.Directory.Execute(request);
    }

    private static BrowserMarkRestoreValidationContext CreateMarkRestoreValidationContext(
        BrowserLoadCoordinator.DirectoryLoadResult result) =>
        new(result.SnapshotPathKinds, result.SnapshotPathKindsAreFresh);

    private BrowserTabState ApplyRestoredTabMarks(
        int tabIndex,
        BrowserTabActivationTransition activation,
        out int skippedMarkCount)
    {
        BrowserTabState state = activation.State;
        skippedMarkCount = activation.SkippedMarkCount;
        _browser.Workspace.SetTabMarksDirty(tabIndex, false);
        BrowserTabState normalized = state.Clone();
        normalized.MarksDirty = false;
        return normalized;
    }

    private static string ResolveTargetPath(BrowserTabState state, string currentPath) =>
        string.IsNullOrWhiteSpace(state.CurrentPath) || !Directory.Exists(state.CurrentPath)
            ? (Directory.Exists(currentPath) ? currentPath : Environment.CurrentDirectory)
            : state.CurrentPath;

}

internal enum BrowserCategorySwitchWorkflowKind
{
    NotAvailable,
    NoOp,
    TabSwitch,
    CategorySwitch
}

internal readonly record struct BrowserCategorySwitchWorkflowStart(
    BrowserCategorySwitchWorkflowKind Kind,
    string TargetCategoryId,
    int TargetTabIndex,
    BrowserTabState? TargetState,
    IReadOnlyList<BrowserTabState> TargetTabs,
    string TargetPath)
{
    public static BrowserCategorySwitchWorkflowStart NotAvailable => new(
        BrowserCategorySwitchWorkflowKind.NotAvailable,
        string.Empty,
        -1,
        null,
        Array.Empty<BrowserTabState>(),
        string.Empty);
}

internal enum BrowserCategorySwitchWorkflowResultKind
{
    NotAvailable,
    NoOp,
    NotCommitted,
    Committed
}

internal readonly record struct BrowserCategorySwitchWorkflowResult(
    BrowserCategorySwitchWorkflowResultKind Kind,
    string TargetCategoryId,
    int TargetTabIndex,
    BrowserTabState? State,
    IReadOnlyList<string> RestoredMarks,
    int SkippedMarkCount)
{
    public BrowserDirectoryLoadApplicationResult? DirectoryLoad { get; init; }
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
    public int SnapshotMarkHitCount { get; init; }
    public int FallbackValidationCount { get; init; }
    public int FallbackFileProbeCount { get; init; }
    public int FallbackDirectoryProbeCount { get; init; }
    public int ParentBatchGroupCount { get; init; }
    public int ParentBatchPathCount { get; init; }
    public int ParentBatchHitCount { get; init; }
    public int ParentBatchMissCount { get; init; }
    public int ParentBatchFailureCount { get; init; }
    public bool SnapshotPathKindsAreFresh { get; init; }

    public bool Committed => Kind == BrowserCategorySwitchWorkflowResultKind.Committed;

    public static BrowserCategorySwitchWorkflowResult NotAvailable => new(
        BrowserCategorySwitchWorkflowResultKind.NotAvailable,
        string.Empty,
        -1,
        null,
        Array.Empty<string>(),
        0);

    public static BrowserCategorySwitchWorkflowResult NoOp => new(
        BrowserCategorySwitchWorkflowResultKind.NoOp,
        string.Empty,
        -1,
        null,
        Array.Empty<string>(),
        0);

    public static BrowserCategorySwitchWorkflowResult NotCommitted => new(
        BrowserCategorySwitchWorkflowResultKind.NotCommitted,
        string.Empty,
        -1,
        null,
        Array.Empty<string>(),
        0);
}

internal readonly record struct BrowserTabSwitchWorkflowStart(
    bool Available,
    int TargetTabIndex,
    BrowserTabState? TargetState,
    string TargetPath)
{
    public static BrowserTabSwitchWorkflowStart NotAvailable =>
        new(false, -1, null, string.Empty);
}

internal enum BrowserTabSwitchWorkflowResultKind
{
    NotAvailable,
    NotCommitted,
    Committed
}

internal readonly record struct BrowserTabSwitchWorkflowResult(
    BrowserTabSwitchWorkflowResultKind Kind,
    int TargetTabIndex,
    BrowserTabState? State,
    IReadOnlyList<string> RestoredMarks,
    int SkippedMarkCount)
{
    public BrowserDirectoryLoadApplicationResult? DirectoryLoad { get; init; }
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
    public int SnapshotMarkHitCount { get; init; }
    public int FallbackValidationCount { get; init; }
    public int FallbackFileProbeCount { get; init; }
    public int FallbackDirectoryProbeCount { get; init; }
    public int ParentBatchGroupCount { get; init; }
    public int ParentBatchPathCount { get; init; }
    public int ParentBatchHitCount { get; init; }
    public int ParentBatchMissCount { get; init; }
    public int ParentBatchFailureCount { get; init; }
    public bool SnapshotPathKindsAreFresh { get; init; }

    public bool Committed => Kind == BrowserTabSwitchWorkflowResultKind.Committed;

    public static BrowserTabSwitchWorkflowResult NotAvailable => new(
        BrowserTabSwitchWorkflowResultKind.NotAvailable,
        -1,
        null,
        Array.Empty<string>(),
        0);

    public static BrowserTabSwitchWorkflowResult NotCommitted => new(
        BrowserTabSwitchWorkflowResultKind.NotCommitted,
        -1,
        null,
        Array.Empty<string>(),
        0);
}

internal readonly record struct BrowserTabCreationExecution(
    BrowserTabCreationTransition Creation,
    BrowserTabSwitchWorkflowResult? Activation)
{
    public bool Created => Creation.Created;
    public bool Activated => Activation is { Committed: true };
}

internal readonly record struct BrowserTabCreationBatchExecution(
    IReadOnlyList<BrowserTabCreationExecution> Items,
    bool PreflightPassed,
    BrowserTabSwitchWorkflowResult? RollbackActivation = null)
{
    public Guid? HistoryOperationId { get; init; }

    public static BrowserTabCreationBatchExecution PreflightFailed =>
        new(Array.Empty<BrowserTabCreationExecution>(), false);

    public bool Succeeded => PreflightPassed &&
        RollbackActivation == null &&
        Items.Count > 0 &&
        Items.All(static item => item.Activated);
}

internal sealed record BrowserTabBatchHistoryEntry(
    string CategoryId,
    IReadOnlyList<BrowserTabBatchHistoryItem> Items,
    Guid? BeforeActiveTabId,
    int BeforeActiveTabIndex,
    BrowserTabState BeforeActiveState)
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
}

internal sealed record BrowserTabBatchHistoryItem(
    Guid TabId,
    string DirectoryPath,
    int InsertionIndex,
    BrowserTabState State);

internal enum BrowserTabBatchHistoryExecutionKind
{
    NotAvailable,
    Undone,
    Redone,
    Blocked,
    Failed
}

internal readonly record struct BrowserTabBatchHistoryExecution(
    BrowserTabBatchHistoryExecutionKind Kind,
    BrowserTabSwitchWorkflowResult? TabSwitch,
    string? Reason,
    Guid? OperationId = null)
{
    public bool Applied => Kind is BrowserTabBatchHistoryExecutionKind.Undone or BrowserTabBatchHistoryExecutionKind.Redone;
    public static BrowserTabBatchHistoryExecution NotAvailable => new(BrowserTabBatchHistoryExecutionKind.NotAvailable, null, null);
    public static BrowserTabBatchHistoryExecution Blocked => new(BrowserTabBatchHistoryExecutionKind.Blocked, null, "固定タブを含むため、タブ操作を元に戻せません。");
    public static BrowserTabBatchHistoryExecution Failed(string reason) => new(BrowserTabBatchHistoryExecutionKind.Failed, null, reason);
}

internal readonly record struct BrowserMarkMutationCommit(bool ExactDeltaApplied);

internal readonly record struct BrowserSelectionMarkExecution(
    BrowserSelectionMarkTransition Transition,
    BrowserMarkMutationCommit Commit);

internal readonly record struct BrowserTabToggleLockExecution(
    BrowserTabSwitchWorkflowResult? Switch,
    BrowserTabLockTransition Transition)
{
    public bool Applied => Transition.Applied;
}

internal readonly record struct BrowserTabToggleReadOnlyExecution(
    BrowserTabSwitchWorkflowResult? Switch,
    BrowserTabReadOnlyTransition Transition)
{
    public bool Applied => Transition.Applied;
}

internal readonly record struct BrowserUnifiedFilterExecution(
    string FilterPattern,
    bool FilterUseRegex,
    TabFilterLockState FilterLock,
    bool IsActive,
    BrowserManualRefreshExecution Refresh)
{
    public bool RefreshStarted => IsActive && Refresh.Decision.ShouldRun;
}

internal readonly record struct BrowserTabCloseCompletion(
    BrowserTabCloseTransition Closed,
    BrowserTabSwitchWorkflowResult Switch);

internal readonly record struct BrowserTabCloseExecution(
    BrowserTabCloseDecision Decision,
    BrowserTabSwitchWorkflowResult? BeforeClose,
    BrowserTabCloseCompletion? Completion)
{
    public bool Closed => Completion is { Closed.Applied: true, Switch.Committed: true };
}

internal readonly record struct BrowserTabRangeCloseCompletion(
    BrowserTabRangeCloseTransition Closed,
    BrowserTabSwitchWorkflowResult Switch);

internal readonly record struct BrowserTabRangeCloseExecution(
    IReadOnlyList<int> ClosableIndices,
    BrowserTabSwitchWorkflowResult? BeforeClose,
    BrowserTabRangeCloseCompletion? Completion)
{
    public bool Closed => Completion is { Closed.Applied: true, Switch.Committed: true };
}

internal readonly record struct BrowserClosedTabRestoreExecution(
    BrowserClosedTabRestoreTransition Transition,
    BrowserCategorySwitchWorkflowResult? CategorySwitch,
    BrowserTabSwitchWorkflowResult? TabSwitch)
{
    public bool Restored => Transition.Kind == BrowserClosedTabRestoreKind.Restored &&
        TabSwitch is { Committed: true };
}

internal static class BrowserTabWorkflowConstants
{
    public const int ClosedBrowserTabHistoryLimit = 10;
}

internal readonly record struct BrowserTabHistoryNavigationResult(
    BrowserHistoryDirection Direction,
    BrowserTabSwitchWorkflowResult? TabSwitch,
    BrowserCategorySwitchWorkflowResult? CategorySwitch,
    bool Succeeded,
    string? Message = null)
{
    public static BrowserTabHistoryNavigationResult Failed(BrowserHistoryDirection direction, string? message = null) =>
        new(direction, null, null, false, message);

    public static BrowserTabHistoryNavigationResult ForTabSwitch(BrowserHistoryDirection direction, BrowserTabSwitchWorkflowResult tabSwitch, string? message = null) =>
        new(direction, tabSwitch, null, tabSwitch.Committed, message);

    public static BrowserTabHistoryNavigationResult ForCategorySwitch(BrowserHistoryDirection direction, BrowserCategorySwitchWorkflowResult categorySwitch, string? message = null) =>
        new(direction, null, categorySwitch, categorySwitch.Committed, message);
}
