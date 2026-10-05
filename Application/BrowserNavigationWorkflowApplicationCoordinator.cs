using System;
using System.IO;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Browser の directory navigation workflow を所有する。
/// WinForms の timer、watcher、list materialization は保持せず、plain request/resultだけを扱う。
/// </summary>
internal sealed class BrowserNavigationWorkflowApplicationCoordinator
{
    private const int RetryDelayMilliseconds = 100;
    private readonly BrowserApplicationCoordinator _browser;
    private readonly BrowserTabWorkflowApplicationCoordinator? _tabWorkflow;
    private readonly BrowserRefreshWorkflowApplicationCoordinator? _refreshWorkflow;
    private readonly SettingsApplicationCoordinator? _settings;

    public BrowserNavigationWorkflowApplicationCoordinator(BrowserApplicationCoordinator browser)
    {
        _browser = browser;
    }

    public BrowserNavigationWorkflowApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        BrowserTabWorkflowApplicationCoordinator tabWorkflow)
        : this(browser)
    {
        _tabWorkflow = tabWorkflow;
    }

    public BrowserNavigationWorkflowApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        BrowserTabWorkflowApplicationCoordinator tabWorkflow,
        BrowserRefreshWorkflowApplicationCoordinator refreshWorkflow)
        : this(browser, tabWorkflow, refreshWorkflow, null)
    {
    }

    public BrowserNavigationWorkflowApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        BrowserTabWorkflowApplicationCoordinator tabWorkflow,
        BrowserRefreshWorkflowApplicationCoordinator refreshWorkflow,
        SettingsApplicationCoordinator? settings)
        : this(browser, tabWorkflow)
    {
        _refreshWorkflow = refreshWorkflow;
        _settings = settings;
    }

    public BrowserCursorNavigationExecution ExecuteCursorNavigation(
        int globalIndex,
        int itemsPerPage,
        bool isBusy,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState)
    {
        BrowserCursorTransition cursor = _browser.SetCursorIndex(globalIndex, itemsPerPage);
        if (!cursor.Applied || !cursor.PageChanged || isBusy)
        {
            return new BrowserCursorNavigationExecution(cursor, null);
        }

        BrowserDirectoryLoadApplicationResult load = ExecuteAndApplyDirectoryLoad(
            _browser.CreateNavigationRequest(_browser.CurrentPath),
            options with { SnapshotPolicy = BrowserLoadCoordinator.SnapshotPolicy.ReuseSnapshot },
            columnCount,
            isSwitchingBrowserTab: false);
        return new BrowserCursorNavigationExecution(cursor, load)
        {
            PostLoadEffects = load.Succeeded
                ? _refreshWorkflow?.PreparePostLoadEffects(shellState) ?? default
                : default
        };
    }

    public BrowserCursorTransition ApplyCursorSelection(int globalIndex, int itemsPerPage) =>
        _browser.SetCursorIndex(globalIndex, itemsPerPage);

    public void PersistCurrentListState()
    {
        _settings?.SetSessionListState(
            _browser.ColumnCount,
            _browser.CurrentSort,
            _browser.SortAscending);
        _tabWorkflow?.SyncActiveStateAndPersist(
            _browser.Workspace.ActiveTabSnapshot ?? new BrowserTabState { CurrentPath = _browser.CurrentPath });
    }

    public BrowserColumnCountExecution ExecuteColumnCountChange(
        int columnCount,
        bool reloadPage,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCountForLoad,
        BrowserRefreshShellState shellState = default)
    {
        _browser.SetColumnCount(columnCount);
        _settings?.SetSessionListState(
            _browser.ColumnCount,
            _browser.CurrentSort,
            _browser.SortAscending);
        if (_tabWorkflow != null)
        {
            BrowserTabState state = currentState.Clone();
            state.ColumnCount = _browser.ColumnCount;
            _tabWorkflow.SyncActiveStateAndPersist(state);
        }

        if (!reloadPage || _refreshWorkflow == null)
        {
            return new BrowserColumnCountExecution(_browser.ColumnCount, null);
        }

        BrowserDirectoryLoadApplicationResult load = ExecuteAndApplyDirectoryLoad(
            _browser.CreateNavigationRequest(_browser.CurrentPath),
            options with { SnapshotPolicy = BrowserLoadCoordinator.SnapshotPolicy.ReuseSnapshot },
            columnCountForLoad,
            isSwitchingBrowserTab: false);
        return new BrowserColumnCountExecution(_browser.ColumnCount, load)
        {
            PostLoadEffects = load.Succeeded
                ? _refreshWorkflow.PreparePostLoadEffects(shellState)
                : default
        };
    }

    public BrowserManualRefreshExecution ExecuteSortAndReload(
        SortKind sortKind,
        bool ascending,
        bool isBrowserMode,
        bool isBusy,
        string reason,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        _browser.SetSort(sortKind, ascending);
        _settings?.SetSessionListState(
            _browser.ColumnCount,
            _browser.CurrentSort,
            _browser.SortAscending);
        return _refreshWorkflow?.ExecuteManualRefresh(
            isBrowserMode,
            isBusy,
            reason,
            options with
            {
                SortKind = sortKind,
                SortAscending = ascending
            },
            columnCount,
            shellState) ?? default;
    }

    public BrowserManualRefreshExecution ExecuteFilterAndReload(
        string pattern,
        bool useRegex,
        bool isBrowserMode,
        bool isBusy,
        string reason,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        _browser.SetFilter(pattern, useRegex);
        return _refreshWorkflow?.ExecuteManualRefresh(
            isBrowserMode,
            isBusy,
            reason,
            options with
            {
                FilterPattern = pattern,
                FilterUseRegex = useRegex
            },
            columnCount,
            shellState) ?? default;
    }

    public BrowserDriveRootNavigationExecution ExecuteDriveRoot(
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserDriveRootNavigationPreparation preparation = PrepareDriveRoot(currentState);
        return ExecutePreparedDriveRoot(
            preparation,
            currentState,
            options,
            columnCount,
            shellState,
            confirmedDerivedTabCreation: true);
    }

    public BrowserDriveRootNavigationPreparation PrepareDriveRoot(BrowserTabState currentState)
    {
        BrowserLockedRootResolution lockedRoot = _browser.Workspace.ResolveActiveLockedRoot();
        string? targetPath = lockedRoot.Path;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            string rootPath = Path.GetPathRoot(_browser.CurrentPath) ?? string.Empty;
            if (string.IsNullOrEmpty(rootPath) || string.Equals(_browser.CurrentPath, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return new BrowserDriveRootNavigationPreparation(lockedRoot, null, false);
            }
            targetPath = rootPath;
        }

        if (QuickAccessService.PathsEqual(_browser.CurrentPath, targetPath))
        {
            return new BrowserDriveRootNavigationPreparation(lockedRoot, null, false);
        }

        BrowserNavigationPreparation navigation = PrepareDirectoryNavigation(
            _browser.CreateNavigationRequest(targetPath),
            _tabWorkflow?.MaxTabCount ?? 0,
            _browser.Workspace.ActiveTabSnapshot ?? currentState);
        return new BrowserDriveRootNavigationPreparation(lockedRoot, navigation, true);
    }

    public BrowserDriveRootNavigationExecution ExecutePreparedDriveRoot(
        BrowserDriveRootNavigationPreparation preparation,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        bool confirmedDerivedTabCreation)
    {
        if (!preparation.TargetChanged || preparation.Navigation is not { } navigationPreparation)
        {
            return new BrowserDriveRootNavigationExecution(preparation.LockedRoot, null, false);
        }

        BrowserDirectoryNavigationExecution navigation = ExecutePreparedDirectoryNavigation(
            navigationPreparation,
            _tabWorkflow?.MaxTabCount ?? 0,
            _browser.Workspace.ActiveTabSnapshot ?? currentState,
            options,
            columnCount,
            shellState,
            confirmedDerivedTabCreation);
        return new BrowserDriveRootNavigationExecution(preparation.LockedRoot, navigation, true);
    }

    public BrowserQuickAccessNavigationExecution ExecuteQuickAccessNavigation(
        QuickAccessApplicationAction action,
        QuickAccessStore? updatedStore,
        QuickAccessEntry? selectedEntry,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserQuickAccessNavigationPreparation preparation = PrepareQuickAccessNavigation(
            action,
            updatedStore,
            selectedEntry);
        return ExecutePreparedQuickAccessNavigation(
            preparation,
            currentState,
            options,
            columnCount,
            shellState,
            confirmedDerivedTabCreation: true);
    }

    public BrowserQuickAccessNavigationPreparation PrepareQuickAccessNavigation(
        QuickAccessApplicationAction action,
        QuickAccessStore? updatedStore,
        QuickAccessEntry? selectedEntry)
    {
        BrowserQuickAccessTransition transition = _browser.PrepareQuickAccessResult(
            action,
            updatedStore,
            selectedEntry);
        BrowserNavigationPreparation? navigation = null;
        if (!string.IsNullOrWhiteSpace(transition.NavigationPath))
        {
            navigation = PrepareDirectoryNavigation(
                _browser.CreateNavigationRequest(transition.NavigationPath!),
                _tabWorkflow?.MaxTabCount ?? 0,
                _browser.Workspace.ActiveTabSnapshot ?? new BrowserTabState { CurrentPath = _browser.CurrentPath });
        }

        return new BrowserQuickAccessNavigationPreparation(
            action,
            updatedStore,
            selectedEntry,
            transition,
            navigation);
    }

    public BrowserQuickAccessNavigationExecution ExecutePreparedQuickAccessNavigation(
        BrowserQuickAccessNavigationPreparation preparation,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        bool confirmedDerivedTabCreation)
    {
        if (preparation.Navigation is { RequiresDerivedTabConfirmation: true } &&
            !confirmedDerivedTabCreation)
        {
            return new BrowserQuickAccessNavigationExecution(
                new BrowserQuickAccessTransition(
                    StoreUpdated: false,
                    SaveOnly: false,
                    NavigationPath: preparation.Transition.NavigationPath),
                new BrowserDirectoryNavigationExecution(
                    BrowserDirectoryNavigationExecutionKind.ConfirmationRequired,
                    null,
                    null,
                    null));
        }

        BrowserQuickAccessTransition transition = _browser.ApplyQuickAccessResult(
            preparation.Action,
            preparation.UpdatedStore,
            preparation.SelectedEntry);
        if (preparation.Navigation is not { } navigationPreparation)
        {
            return new BrowserQuickAccessNavigationExecution(transition, null);
        }

        BrowserDirectoryNavigationExecution navigation = ExecutePreparedDirectoryNavigation(
            navigationPreparation,
            _tabWorkflow?.MaxTabCount ?? 0,
            _browser.Workspace.ActiveTabSnapshot ?? currentState,
            options,
            columnCount,
            shellState,
            confirmedDerivedTabCreation);
        return new BrowserQuickAccessNavigationExecution(transition, navigation);
    }

    public BrowserQuickAccessRegistrationExecution ExecuteQuickAccessRegistration(
        string displayName,
        string? categoryName,
        string path,
        bool useForTabTitle,
        string currentPath,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        QuickAccessStore updatedStore = _browser.Workspace.QuickAccessSnapshot;
        if (!QuickAccessService.TrySaveManagedLocationEntry(
                updatedStore,
                existingEntry: null,
                displayName,
                path,
                categoryName,
                useForTabTitle,
                currentPath,
                out _,
                out string message))
        {
            return new BrowserQuickAccessRegistrationExecution(false, message, null);
        }

        BrowserQuickAccessNavigationExecution navigation = ExecuteQuickAccessNavigation(
            QuickAccessApplicationAction.SaveOnly,
            updatedStore,
            selectedEntry: null,
            currentState,
            options,
            columnCount,
            shellState);
        if (!navigation.Transition.PersistenceSucceeded)
        {
            return new BrowserQuickAccessRegistrationExecution(
                false,
                "QuickAccess を保存できませんでした。ストレージを確認してください。",
                navigation);
        }
        return new BrowserQuickAccessRegistrationExecution(true, message, navigation);
    }

    public BrowserDirectoryNavigationExecution ExecuteDirectoryNavigation(
        string targetPath,
        string? focusTargetName,
        bool isHistoryNavigation,
        bool suppressRecent,
        int maxTabCount,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        bool recordDirectoryMoveHistory = false)
    {
        return ExecuteDirectoryNavigation(
            _browser.CreateNavigationRequest(targetPath, focusTargetName, isHistoryNavigation, suppressRecent),
            maxTabCount,
            currentState,
            options,
            columnCount,
            shellState,
            recordDirectoryMoveHistory);
    }

    public BrowserNavigationWorkflowExecution BeginNavigation(
        BrowserNavigationCoordinator.DirectoryNavigationRequest? request,
        int maxTabCount,
        BrowserTabState currentState)
    {
        BrowserNavigationPreparation preparation = PrepareDirectoryNavigation(request, maxTabCount, currentState);
        BrowserNavigationWorkflowDecision decision = preparation.Decision;
        if (decision.Kind != BrowserNavigationWorkflowDecisionKind.Ready || decision.Request == null)
        {
            return new BrowserNavigationWorkflowExecution(
                decision.Kind switch
                {
                    BrowserNavigationWorkflowDecisionKind.NotRequested => BrowserNavigationWorkflowExecutionKind.NotRequested,
                    BrowserNavigationWorkflowDecisionKind.DirectoryMissing => BrowserNavigationWorkflowExecutionKind.DirectoryMissing,
                    BrowserNavigationWorkflowDecisionKind.Blocked => BrowserNavigationWorkflowExecutionKind.Blocked,
                    _ => BrowserNavigationWorkflowExecutionKind.Failed
                },
                decision.Request,
                null,
                null);
        }

        return new BrowserNavigationWorkflowExecution(
            BrowserNavigationWorkflowExecutionKind.Ready,
            decision.Request,
            null,
            null);
    }

    public BrowserDirectoryNavigationExecution ExecuteDirectoryNavigation(
        BrowserNavigationCoordinator.DirectoryNavigationRequest? request,
        int maxTabCount,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        bool recordDirectoryMoveHistory = false)
    {
        BrowserNavigationPreparation preparation = PrepareDirectoryNavigation(request, maxTabCount, currentState);
        return ExecutePreparedDirectoryNavigation(
            preparation,
            maxTabCount,
            currentState,
            options,
            columnCount,
            shellState,
            confirmedDerivedTabCreation: true,
            recordDirectoryMoveHistory: recordDirectoryMoveHistory);
    }

    public BrowserNavigationPreparation PrepareDirectoryNavigation(
        BrowserNavigationCoordinator.DirectoryNavigationRequest? request,
        int maxTabCount,
        BrowserTabState currentState)
    {
        BrowserNavigationWorkflowDecision decision = PrepareNavigation(request, maxTabCount);
        return new BrowserNavigationPreparation(decision, currentState.Id);
    }

    public BrowserDirectoryNavigationExecution ExecutePreparedDirectoryNavigation(
        BrowserNavigationPreparation preparation,
        int maxTabCount,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        bool confirmedDerivedTabCreation,
        bool recordDirectoryMoveHistory = false)
    {
        BrowserNavigationWorkflowDecision decision = PrepareNavigation(preparation.Request, maxTabCount);
        if (decision.Kind != BrowserNavigationWorkflowDecisionKind.Ready || decision.Request == null)
        {
            return new BrowserDirectoryNavigationExecution(
                MapExecutionKind(decision.Kind),
                null,
                null,
                null);
        }

        if (preparation.RequiresDerivedTabConfirmation &&
            decision.LocationChange == BrowserLocationChangeTransition.RequiresDerivedTab &&
            !confirmedDerivedTabCreation)
        {
            return new BrowserDirectoryNavigationExecution(
                BrowserDirectoryNavigationExecutionKind.ConfirmationRequired,
                null,
                null,
                null);
        }

        int? derivedTabIndex = null;
        if (decision.LocationChange == BrowserLocationChangeTransition.RequiresDerivedTab)
        {
            if (_tabWorkflow == null)
            {
                return new BrowserDirectoryNavigationExecution(
                    BrowserDirectoryNavigationExecutionKind.TabCreationUnavailable,
                    null,
                    null,
                    null);
            }

            BrowserTabCreationTransition creation = _tabWorkflow.CreateTab(
                currentState,
                initialPath: null,
                useConfiguredInsertion: false);
            if (!creation.Created || !_tabWorkflow.ActivateCreatedTab(creation.Index).Committed)
            {
                return new BrowserDirectoryNavigationExecution(
                    BrowserDirectoryNavigationExecutionKind.TabCreationUnavailable,
                    null,
                    null,
                    null);
            }

            derivedTabIndex = creation.Index;
        }

        BrowserDirectoryLoadApplicationResult load = ExecuteAndApplyDirectoryLoad(
            decision.Request,
            options,
            columnCount,
            isSwitchingBrowserTab: false);
        if (load.Succeeded && recordDirectoryMoveHistory)
        {
            _settings?.AddDirectoryMoveHistory(decision.Request.TargetPath);
        }
        BrowserDirectoryPostLoadEffects effects = load.Succeeded
            ? _refreshWorkflow?.PreparePostLoadEffects(shellState) ?? default
            : default;
        return new BrowserDirectoryNavigationExecution(
            load.Succeeded
                ? BrowserDirectoryNavigationExecutionKind.Loaded
                : BrowserDirectoryNavigationExecutionKind.Failed,
            load,
            derivedTabIndex,
            load.Error)
        {
            PostLoadEffects = effects
        };
    }

    private BrowserNavigationWorkflowDecision PrepareNavigation(
        BrowserNavigationCoordinator.DirectoryNavigationRequest? request,
        int maxTabCount)
    {
        BrowserNavigationDecision decision = _browser.PrepareNavigation(request, maxTabCount);
        return new BrowserNavigationWorkflowDecision(
            decision.Kind switch
            {
                BrowserNavigationDecisionKind.NotRequested => BrowserNavigationWorkflowDecisionKind.NotRequested,
                BrowserNavigationDecisionKind.DirectoryMissing => BrowserNavigationWorkflowDecisionKind.DirectoryMissing,
                BrowserNavigationDecisionKind.Blocked => BrowserNavigationWorkflowDecisionKind.Blocked,
                _ => BrowserNavigationWorkflowDecisionKind.Ready
            },
            decision.Request,
            decision.LocationChange);
    }

    public BrowserDirectoryLoadExecution ExecuteDirectoryLoad(
        string targetPath,
        string? focusTargetName,
        bool isHistoryNavigation,
        bool suppressRecent,
        BrowserLoadCoordinator.SnapshotPolicy snapshotPolicy,
        BrowserDirectoryLoadOptions options)
    {
        BrowserLoadCoordinator.DirectoryLoadRequest request = _browser.CreateDirectoryLoadRequest(
            targetPath,
            focusTargetName,
            isHistoryNavigation,
            suppressRecent,
            options with { SnapshotPolicy = snapshotPolicy });
        return _browser.Directory.Execute(request);
    }

    public BrowserHistoryNavigationExecution ExecuteHistoryNavigation(
        BrowserHistoryDirection direction,
        BrowserTabState currentState,
        int maxTabCount,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        bool? confirmedDerivedTabCreation = true)
    {
        BrowserHistoryNavigationStart started = direction == BrowserHistoryDirection.Back
            ? BeginHistoryBack(currentState, maxTabCount)
            : BeginHistoryForward(currentState, maxTabCount);
        if (started.RequiresDerivedTabConfirmation && confirmedDerivedTabCreation != true)
        {
            return new BrowserHistoryNavigationExecution(
                BrowserHistoryNavigationExecutionKind.NotReady,
                started,
                null,
                BrowserHistoryNavigationResult.NotAvailable);
        }
        return ExecutePreparedHistoryNavigation(
            started,
            currentState,
            maxTabCount,
            options,
            columnCount,
            shellState,
            confirmedDerivedTabCreation: confirmedDerivedTabCreation == true);
    }

    public BrowserHistoryNavigationExecution ExecutePreparedHistoryNavigation(
        BrowserHistoryNavigationStart started,
        BrowserTabState currentState,
        int maxTabCount,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        bool confirmedDerivedTabCreation)
    {
        if (!started.Available || started.Request == null)
        {
            return new BrowserHistoryNavigationExecution(
                started.Kind == BrowserHistoryNavigationStartKind.NotAvailable
                    ? BrowserHistoryNavigationExecutionKind.NotAvailable
                    : BrowserHistoryNavigationExecutionKind.NotReady,
                started,
                null,
                BrowserHistoryNavigationResult.NotAvailable);
        }

        BrowserNavigationPreparation preparation = PrepareDirectoryNavigation(
            started.Request,
            maxTabCount,
            currentState);
        if (preparation.Kind != BrowserNavigationWorkflowDecisionKind.Ready)
        {
            return new BrowserHistoryNavigationExecution(
                BrowserHistoryNavigationExecutionKind.NotReady,
                started,
                null,
                BrowserHistoryNavigationResult.NotAvailable);
        }

        if (preparation.RequiresDerivedTabConfirmation && !confirmedDerivedTabCreation)
        {
            return new BrowserHistoryNavigationExecution(
                BrowserHistoryNavigationExecutionKind.NotReady,
                started,
                null,
                BrowserHistoryNavigationResult.NotAvailable);
        }

        int? derivedTabIndex = null;
        if (preparation.RequiresDerivedTabConfirmation)
        {
            if (_tabWorkflow == null)
            {
                return new BrowserHistoryNavigationExecution(
                    BrowserHistoryNavigationExecutionKind.NotReady,
                    started with { Kind = BrowserHistoryNavigationStartKind.TabCreationUnavailable },
                    null,
                    BrowserHistoryNavigationResult.NotAvailable);
            }

            BrowserTabCreationTransition creation = _tabWorkflow.CreateTab(
                currentState,
                initialPath: null,
                useConfiguredInsertion: false);
            if (!creation.Created || !_tabWorkflow.ActivateCreatedTab(creation.Index).Committed)
            {
                return new BrowserHistoryNavigationExecution(
                    BrowserHistoryNavigationExecutionKind.NotReady,
                    started with { Kind = BrowserHistoryNavigationStartKind.TabCreationUnavailable },
                    null,
                    BrowserHistoryNavigationResult.NotAvailable);
            }

            derivedTabIndex = creation.Index;
        }

        BrowserHistoryNavigationStart executionStart = started with { DerivedTabIndex = derivedTabIndex };

        BrowserDirectoryLoadApplicationResult load = ExecuteAndApplyDirectoryLoad(
            executionStart.Request!,
            options,
            columnCount,
            isSwitchingBrowserTab: false);
        if (!load.Succeeded)
        {
            return new BrowserHistoryNavigationExecution(
                BrowserHistoryNavigationExecutionKind.Failed,
                executionStart,
                load,
                new BrowserHistoryNavigationResult(
                    BrowserHistoryNavigationResultKind.NotCommitted,
                    started.Direction,
                    executionStart.TargetPath,
                    executionStart.PreviousPath));
        }

        BrowserHistoryNavigationResult completed = CompleteHistoryNavigation(
            executionStart,
            materialized: true,
            _browser.Workspace.ActiveTabSnapshot ?? currentState);
        BrowserDirectoryPostLoadEffects effects = _refreshWorkflow?.PreparePostLoadEffects(shellState) ?? default;
        return new BrowserHistoryNavigationExecution(
            completed.Committed
                ? BrowserHistoryNavigationExecutionKind.Committed
                : BrowserHistoryNavigationExecutionKind.NotCommitted,
            executionStart,
            load,
            completed)
        {
            PostLoadEffects = effects
        };
    }

    public BrowserDirectoryNavigationExecution ExecuteParentNavigation(
        int maxTabCount,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        return ExecuteDirectoryNavigation(
            _browser.CreateParentNavigationRequest(),
            maxTabCount,
            currentState,
            options,
            columnCount,
            shellState);
    }

    public BrowserDirectoryApplicationTransition ApplyDirectoryLoad(
        BrowserLoadCoordinator.DirectoryLoadResult result,
        int itemsPerPage,
        int columnCount,
        SortKind sortKind,
        bool sortAscending,
        bool isSwitchingBrowserTab)
    {
        return _browser.Directory.ApplyDirectoryLoad(
            result,
            itemsPerPage,
            columnCount,
            sortKind,
            sortAscending,
            isSwitchingBrowserTab);
    }

    public BrowserDirectoryNavigationExecution ExecuteCurrentDirectoryReload(
        string? focusTargetName,
        BrowserPostOperationReloadContext context)
    {
        if (string.IsNullOrWhiteSpace(_browser.CurrentPath))
        {
            return new BrowserDirectoryNavigationExecution(
                BrowserDirectoryNavigationExecutionKind.NotRequested,
                null,
                null,
                null);
        }

        BrowserDirectoryLoadApplicationResult load = ExecuteAndApplyDirectoryLoad(
            _browser.CreateNavigationRequest(
                _browser.CurrentPath,
                focusTargetName,
                isHistoryNavigation: false,
                suppressRecent: false),
            context.Options,
            context.ColumnCount,
            isSwitchingBrowserTab: false);
        BrowserDirectoryPostLoadEffects effects = load.Succeeded
            ? _refreshWorkflow?.PreparePostLoadEffects(context.ShellState) ?? default
            : default;
        return new BrowserDirectoryNavigationExecution(
            load.Succeeded
                ? BrowserDirectoryNavigationExecutionKind.Loaded
                : BrowserDirectoryNavigationExecutionKind.Failed,
            load,
            null,
            load.Error)
        {
            PostLoadEffects = effects
        };
    }

    public BrowserDirectoryNavigationExecution ExecuteTargetStateReload(
        BrowserTabState targetState,
        BrowserDirectoryLoadOptions sourceOptions,
        BrowserRefreshShellState shellState = default)
    {
        BrowserDirectoryLoadOptions targetOptions = _browser.CreateTargetMaterializationOptions(
            targetState,
            sourceOptions);
        BrowserLoadCoordinator.DirectoryLoadRequest request = _browser.CreateTargetDirectoryLoadRequest(
            targetState.CurrentPath,
            targetState,
            targetOptions,
            isHistoryNavigation: false,
            suppressRecent: true);
        BrowserDirectoryLoadApplicationResult load = _browser.Directory.ExecuteAndApply(
            request,
            Math.Clamp(targetState.ColumnCount, 1, 9),
            isSwitchingBrowserTab: false);
        BrowserDirectoryPostLoadEffects effects = load.Succeeded
            ? _refreshWorkflow?.PreparePostLoadEffects(shellState) ?? default
            : default;
        return new BrowserDirectoryNavigationExecution(
            load.Succeeded
                ? BrowserDirectoryNavigationExecutionKind.Loaded
                : BrowserDirectoryNavigationExecutionKind.Failed,
            load,
            null,
            load.Error)
        {
            PostLoadEffects = effects
        };
    }

    private BrowserDirectoryLoadApplicationResult ExecuteAndApplyDirectoryLoad(
        BrowserNavigationCoordinator.DirectoryNavigationRequest request,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        bool isSwitchingBrowserTab)
    {
        BrowserLoadCoordinator.DirectoryLoadRequest loadRequest = _browser.CreateDirectoryLoadRequest(
            request.TargetPath,
            request.FocusTargetName,
            request.IsHistoryNavigation,
            request.SuppressRecent,
            options);
        return _browser.Directory.ExecuteAndApply(loadRequest, columnCount, isSwitchingBrowserTab);
    }

    private static BrowserDirectoryNavigationExecutionKind MapExecutionKind(
        BrowserNavigationWorkflowExecutionKind kind) =>
        kind switch
        {
            BrowserNavigationWorkflowExecutionKind.NotRequested => BrowserDirectoryNavigationExecutionKind.NotRequested,
            BrowserNavigationWorkflowExecutionKind.DirectoryMissing => BrowserDirectoryNavigationExecutionKind.DirectoryMissing,
            BrowserNavigationWorkflowExecutionKind.Blocked => BrowserDirectoryNavigationExecutionKind.Blocked,
            BrowserNavigationWorkflowExecutionKind.TabCreationUnavailable => BrowserDirectoryNavigationExecutionKind.TabCreationUnavailable,
            _ => BrowserDirectoryNavigationExecutionKind.Failed
        };

    private static BrowserDirectoryNavigationExecutionKind MapExecutionKind(
        BrowserNavigationWorkflowDecisionKind kind) =>
        kind switch
        {
            BrowserNavigationWorkflowDecisionKind.NotRequested => BrowserDirectoryNavigationExecutionKind.NotRequested,
            BrowserNavigationWorkflowDecisionKind.DirectoryMissing => BrowserDirectoryNavigationExecutionKind.DirectoryMissing,
            BrowserNavigationWorkflowDecisionKind.Blocked => BrowserDirectoryNavigationExecutionKind.Blocked,
            _ => BrowserDirectoryNavigationExecutionKind.Failed
        };

    public BrowserReloadPlan PrepareReload(string currentPath, bool force, bool refreshBlocked)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return BrowserReloadPlan.NoCurrentPath;
        }
        if (!force && refreshBlocked)
        {
            return BrowserReloadPlan.Blocked;
        }
        if (Directory.Exists(currentPath))
        {
            return new BrowserReloadPlan(BrowserReloadPlanKind.Reload, currentPath, null);
        }

        if (NavigationFallbackResolver.TryResolveExistingDirectoryFallback(
            currentPath,
            message => LogService.Error(message),
            out string fallbackPath,
            out string fallbackReason))
        {
            return new BrowserReloadPlan(BrowserReloadPlanKind.Fallback, fallbackPath, fallbackReason);
        }

        return new BrowserReloadPlan(BrowserReloadPlanKind.Missing, currentPath, null);
    }

    public BrowserReloadExecution BeginReload(
        string currentPath,
        bool force,
        bool refreshBlocked,
        BrowserDirectoryLoadOptions options)
    {
        return ExecuteReloadPlan(
            PrepareReload(currentPath, force, refreshBlocked),
            options,
            allowRetry: true);
    }

    public BrowserReloadExecution ResumeReload(
        string expectedPath,
        BrowserDirectoryLoadOptions options)
    {
        BrowserRetryEvaluation evaluation = EvaluateRetry(expectedPath);
        return evaluation.IsStale
            ? BrowserReloadExecution.Stale
            : ExecuteReloadPlan(evaluation.Plan, options, allowRetry: false);
    }

    private BrowserReloadExecution ExecuteReloadPlan(
        BrowserReloadPlan plan,
        BrowserDirectoryLoadOptions options,
        bool allowRetry)
    {
        switch (plan.Kind)
        {
            case BrowserReloadPlanKind.NoCurrentPath:
                return BrowserReloadExecution.NoCurrentPath;
            case BrowserReloadPlanKind.Blocked:
                return BrowserReloadExecution.Blocked;
            case BrowserReloadPlanKind.Missing:
                return new BrowserReloadExecution(
                    BrowserReloadExecutionKind.Missing,
                    plan.Path,
                    null,
                    plan.FallbackReason,
                    0,
                    null);
        }

        BrowserDirectoryLoadExecution execution = ExecuteDirectoryLoad(
            plan.Path,
            focusTargetName: null,
            isHistoryNavigation: false,
            suppressRecent: false,
            BrowserLoadCoordinator.SnapshotPolicy.RebuildSnapshot,
            options);
        if (execution.Succeeded)
        {
            return new BrowserReloadExecution(
                BrowserReloadExecutionKind.Loaded,
                plan.Path,
                execution.Result,
                plan.FallbackReason,
                0,
                null);
        }

        if (allowRetry && plan.Kind == BrowserReloadPlanKind.Reload)
        {
            BrowserReloadFailureTransition retry = BeginReloadFailure(plan.Path);
            if (retry.RetryScheduled)
            {
                return new BrowserReloadExecution(
                    BrowserReloadExecutionKind.RetryScheduled,
                    plan.Path,
                    null,
                    plan.FallbackReason,
                    retry.DelayMilliseconds,
                    execution.Error);
            }
        }

        return new BrowserReloadExecution(
            BrowserReloadExecutionKind.Failed,
            plan.Path,
            null,
            plan.FallbackReason,
            0,
            execution.Error);
    }

    public BrowserReloadFailureTransition BeginReloadFailure(string currentPath)
    {
        if (_browser.Refresh.CurrentDirectoryRefreshRetryPending)
        {
            return BrowserReloadFailureTransition.NotScheduled;
        }

        _browser.Refresh.SetCurrentDirectoryRefreshRetryPending(true);
        return new BrowserReloadFailureTransition(true, currentPath, RetryDelayMilliseconds);
    }

    public void CancelReloadRetry() => _browser.Refresh.SetCurrentDirectoryRefreshRetryPending(false);

    public BrowserRetryEvaluation EvaluateRetry(string expectedPath)
    {
        CancelReloadRetry();
        if (!IsCurrentDirectory(expectedPath))
        {
            return BrowserRetryEvaluation.Stale;
        }

        return new BrowserRetryEvaluation(
            IsStale: false,
            PrepareReload(expectedPath, force: false, refreshBlocked: false));
    }

    public bool IsCurrentDirectory(string path)
    {
        return string.Equals(
            NavigationService.NormalizeDirectoryForCompare(path),
            NavigationService.NormalizeDirectoryForCompare(_browser.CurrentPath),
            StringComparison.OrdinalIgnoreCase);
    }

    public BrowserHistoryNavigationStart BeginHistoryBack(
        BrowserTabState currentState,
        int maxTabCount) =>
        BeginHistoryNavigation(BrowserHistoryDirection.Back, currentState, maxTabCount);

    public BrowserHistoryNavigationStart BeginHistoryForward(
        BrowserTabState currentState,
        int maxTabCount) =>
        BeginHistoryNavigation(BrowserHistoryDirection.Forward, currentState, maxTabCount);

    public BrowserHistoryNavigationResult CompleteHistoryNavigation(
        BrowserHistoryNavigationStart operation,
        bool materialized,
        BrowserTabState currentState)
    {
        if (!operation.Available)
        {
            return BrowserHistoryNavigationResult.NotAvailable;
        }

        string? currentCandidate = operation.Direction == BrowserHistoryDirection.Back
            ? _browser.PeekBack()
            : _browser.PeekForward();
        if (!materialized ||
            !IsCurrentDirectory(operation.TargetPath!) ||
            !string.Equals(currentCandidate, operation.TargetPath, StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserHistoryNavigationResult(
                BrowserHistoryNavigationResultKind.NotCommitted,
                operation.Direction,
                operation.TargetPath,
                operation.PreviousPath);
        }

        if (operation.Direction == BrowserHistoryDirection.Back)
        {
            _browser.CommitBack(operation.PreviousPath);
        }
        else
        {
            _browser.CommitForward(operation.PreviousPath);
        }

        _tabWorkflow?.SyncActiveState(currentState);
        return new BrowserHistoryNavigationResult(
            BrowserHistoryNavigationResultKind.Committed,
            operation.Direction,
            operation.TargetPath,
            operation.PreviousPath);
    }

    private BrowserHistoryNavigationStart BeginHistoryNavigation(
        BrowserHistoryDirection direction,
        BrowserTabState currentState,
        int maxTabCount)
    {
        string? target = direction == BrowserHistoryDirection.Back
            ? _browser.PeekBack()
            : _browser.PeekForward();
        if (target == null)
        {
            return BrowserHistoryNavigationStart.NotAvailable;
        }

        BrowserNavigationCoordinator.DirectoryNavigationRequest request = _browser.CreateNavigationRequest(
            target,
            isHistoryNavigation: true,
            suppressRecent: false);
        BrowserNavigationWorkflowDecision decision = PrepareNavigation(request, maxTabCount);
        if (decision.Kind == BrowserNavigationWorkflowDecisionKind.DirectoryMissing)
        {
            return new BrowserHistoryNavigationStart(
                BrowserHistoryNavigationStartKind.DirectoryMissing,
                direction,
                target,
                _browser.CurrentPath,
                request,
                null,
                null);
        }
        if (decision.Kind == BrowserNavigationWorkflowDecisionKind.Blocked)
        {
            return new BrowserHistoryNavigationStart(
                BrowserHistoryNavigationStartKind.Blocked,
                direction,
                target,
                _browser.CurrentPath,
                request,
                null,
                null);
        }
        if (decision.Kind != BrowserNavigationWorkflowDecisionKind.Ready || decision.Request == null)
        {
            return new BrowserHistoryNavigationStart(
                BrowserHistoryNavigationStartKind.Failed,
                direction,
                target,
                _browser.CurrentPath,
                request,
                null,
                new IOException("History navigation preparation failed."));
        }

        return new BrowserHistoryNavigationStart(
            BrowserHistoryNavigationStartKind.Ready,
            direction,
            target,
            _browser.CurrentPath,
            decision.Request,
            null,
            null)
        {
            RequiresDerivedTabConfirmation =
                decision.LocationChange == BrowserLocationChangeTransition.RequiresDerivedTab
        };
    }
}

internal enum BrowserNavigationWorkflowDecisionKind
{
    NotRequested,
    DirectoryMissing,
    Blocked,
    Ready
}

internal readonly record struct BrowserNavigationWorkflowDecision(
    BrowserNavigationWorkflowDecisionKind Kind,
    BrowserNavigationCoordinator.DirectoryNavigationRequest? Request,
    BrowserLocationChangeTransition LocationChange);

internal readonly record struct BrowserNavigationPreparation(
    BrowserNavigationWorkflowDecision Decision,
    Guid SourceTabId)
{
    public BrowserNavigationWorkflowDecisionKind Kind => Decision.Kind;
    public BrowserNavigationCoordinator.DirectoryNavigationRequest? Request => Decision.Request;
    public BrowserLocationChangeTransition LocationChange => Decision.LocationChange;
    public bool RequiresDerivedTabConfirmation =>
        Kind == BrowserNavigationWorkflowDecisionKind.Ready &&
        LocationChange == BrowserLocationChangeTransition.RequiresDerivedTab;
}

internal enum BrowserNavigationWorkflowExecutionKind
{
    Ready,
    NotRequested,
    DirectoryMissing,
    Blocked,
    TabCreationUnavailable,
    Failed
}

internal readonly record struct BrowserNavigationWorkflowExecution(
    BrowserNavigationWorkflowExecutionKind Kind,
    BrowserNavigationCoordinator.DirectoryNavigationRequest? Request,
    int? DerivedTabIndex,
    Exception? Error)
{
    public static BrowserNavigationWorkflowExecution TabCreationUnavailable =>
        new(BrowserNavigationWorkflowExecutionKind.TabCreationUnavailable, null, null, null);
}

internal enum BrowserDirectoryNavigationExecutionKind
{
    Loaded,
    NotRequested,
    DirectoryMissing,
    Blocked,
    TabCreationUnavailable,
    ConfirmationRequired,
    Failed
}

internal readonly record struct BrowserPostOperationReloadContext(
    BrowserDirectoryLoadOptions Options,
    int ColumnCount,
    BrowserRefreshShellState ShellState);

internal readonly record struct BrowserDirectoryNavigationExecution(
    BrowserDirectoryNavigationExecutionKind Kind,
    BrowserDirectoryLoadApplicationResult? Load,
    int? DerivedTabIndex,
    Exception? Error)
{
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
    public bool Succeeded => Kind == BrowserDirectoryNavigationExecutionKind.Loaded && Load?.Succeeded == true;
}

internal readonly record struct BrowserDirectoryLoadExecution(
    BrowserLoadCoordinator.DirectoryLoadResult? Result,
    Exception? Error)
{
    public bool Succeeded => Result != null && Error == null;

    public static BrowserDirectoryLoadExecution Success(BrowserLoadCoordinator.DirectoryLoadResult result) =>
        new(result, null);

    public static BrowserDirectoryLoadExecution Failure(Exception error) =>
        new(null, error);
}

internal enum BrowserReloadPlanKind
{
    NoCurrentPath,
    Blocked,
    Reload,
    Fallback,
    Missing
}

internal readonly record struct BrowserReloadPlan(
    BrowserReloadPlanKind Kind,
    string Path,
    string? FallbackReason)
{
    public static BrowserReloadPlan NoCurrentPath => new(BrowserReloadPlanKind.NoCurrentPath, string.Empty, null);
    public static BrowserReloadPlan Blocked => new(BrowserReloadPlanKind.Blocked, string.Empty, null);
}

internal readonly record struct BrowserReloadFailureTransition(
    bool RetryScheduled,
    string CurrentPath,
    int DelayMilliseconds)
{
    public static BrowserReloadFailureTransition NotScheduled => new(false, string.Empty, 0);
}

internal readonly record struct BrowserRetryEvaluation(
    bool IsStale,
    BrowserReloadPlan Plan)
{
    public static BrowserRetryEvaluation Stale => new(true, BrowserReloadPlan.Blocked);
}

internal enum BrowserReloadExecutionKind
{
    Loaded,
    RetryScheduled,
    Missing,
    Blocked,
    NoCurrentPath,
    Failed,
    Stale
}

internal readonly record struct BrowserReloadExecution(
    BrowserReloadExecutionKind Kind,
    string Path,
    BrowserLoadCoordinator.DirectoryLoadResult? Result,
    string? FallbackReason,
    int DelayMilliseconds,
    Exception? Error)
{
    public BrowserDirectoryLoadApplicationResult? DirectoryLoad { get; init; }
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
    public BrowserWatcherUpdate Watcher { get; init; }

    public static BrowserReloadExecution NoCurrentPath =>
        new(BrowserReloadExecutionKind.NoCurrentPath, string.Empty, null, null, 0, null);

    public static BrowserReloadExecution Blocked =>
        new(BrowserReloadExecutionKind.Blocked, string.Empty, null, null, 0, null);

    public static BrowserReloadExecution Stale =>
        new(BrowserReloadExecutionKind.Stale, string.Empty, null, null, 0, null);
}

internal enum BrowserHistoryDirection
{
    Back,
    Forward
}

internal enum BrowserHistoryNavigationStartKind
{
    NotAvailable,
    Ready,
    DirectoryMissing,
    Blocked,
    TabCreationUnavailable,
    Failed
}

internal readonly record struct BrowserHistoryNavigationStart(
    BrowserHistoryNavigationStartKind Kind,
    BrowserHistoryDirection Direction,
    string? TargetPath,
    string PreviousPath,
    BrowserNavigationCoordinator.DirectoryNavigationRequest? Request,
    int? DerivedTabIndex,
    Exception? Error)
{
    public bool RequiresDerivedTabConfirmation { get; init; }
    public bool Available => Kind == BrowserHistoryNavigationStartKind.Ready;

    public static BrowserHistoryNavigationStart NotAvailable => new(
        BrowserHistoryNavigationStartKind.NotAvailable,
        BrowserHistoryDirection.Back,
        null,
        string.Empty,
        null,
        null,
        null);
}

internal enum BrowserHistoryNavigationResultKind
{
    NotAvailable,
    NotCommitted,
    Committed
}

internal readonly record struct BrowserHistoryNavigationResult(
    BrowserHistoryNavigationResultKind Kind,
    BrowserHistoryDirection Direction,
    string? TargetPath,
    string PreviousPath)
{
    public bool Committed => Kind == BrowserHistoryNavigationResultKind.Committed;

    public static BrowserHistoryNavigationResult NotAvailable => new(
        BrowserHistoryNavigationResultKind.NotAvailable,
        BrowserHistoryDirection.Back,
        null,
        string.Empty);
}

internal enum BrowserHistoryNavigationExecutionKind
{
    Committed,
    NotAvailable,
    NotReady,
    Failed,
    NotCommitted
}

internal readonly record struct BrowserHistoryNavigationExecution(
    BrowserHistoryNavigationExecutionKind Kind,
    BrowserHistoryNavigationStart Start,
    BrowserDirectoryLoadApplicationResult? Load,
    BrowserHistoryNavigationResult Result)
{
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
    public bool Succeeded => Kind == BrowserHistoryNavigationExecutionKind.Committed;
}

internal readonly record struct BrowserCursorNavigationExecution(
    BrowserCursorTransition Cursor,
    BrowserDirectoryLoadApplicationResult? Load)
{
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
    public bool PageLoaded => Load is { Succeeded: true };
}

internal readonly record struct BrowserColumnCountExecution(
    int ColumnCount,
    BrowserDirectoryLoadApplicationResult? Load)
{
    public BrowserDirectoryPostLoadEffects PostLoadEffects { get; init; }
}

internal readonly record struct BrowserDriveRootNavigationExecution(
    BrowserLockedRootResolution LockedRoot,
    BrowserDirectoryNavigationExecution? Navigation,
    bool TargetChanged)
{
    public bool Succeeded => !TargetChanged || Navigation is { Succeeded: true };
}

internal readonly record struct BrowserDriveRootNavigationPreparation(
    BrowserLockedRootResolution LockedRoot,
    BrowserNavigationPreparation? Navigation,
    bool TargetChanged);

internal readonly record struct BrowserQuickAccessNavigationPreparation(
    QuickAccessApplicationAction Action,
    QuickAccessStore? UpdatedStore,
    QuickAccessEntry? SelectedEntry,
    BrowserQuickAccessTransition Transition,
    BrowserNavigationPreparation? Navigation);

internal readonly record struct BrowserQuickAccessNavigationExecution(
    BrowserQuickAccessTransition Transition,
    BrowserDirectoryNavigationExecution? Navigation)
{
    public bool Succeeded => Navigation is null || Navigation.Value.Succeeded;
}

internal readonly record struct BrowserQuickAccessRegistrationExecution(
    bool Succeeded,
    string Message,
    BrowserQuickAccessNavigationExecution? Navigation);
