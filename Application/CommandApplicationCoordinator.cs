using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// 登録済みCommandの文脈判定とapplication workflowへのroutingを担当する。
/// UI event handlerやMainFormの具体型は参照しない。
/// </summary>
internal sealed class CommandApplicationCoordinator
{
    private readonly BrowserApplicationCoordinator _browser;
    private readonly BrowserNavigationWorkflowApplicationCoordinator _navigation;
    private readonly BrowserRefreshWorkflowApplicationCoordinator _refresh;
    private readonly BrowserTabWorkflowApplicationCoordinator _tabs;
    private readonly UnifiedUndoRedoApplicationCoordinator _unifiedUndoRedo;
    private readonly BrowserCategoryWorkflowApplicationCoordinator _categories;
    private readonly BrowserWorkspacePersistenceApplicationCoordinator _persistence;
    private readonly ViewerApplicationCoordinator _viewer;
    private readonly ViewerWorkflowApplicationCoordinator _viewerWorkflow;
    private readonly ViewerModeApplicationCoordinator _viewerMode;
    private readonly IViewerModeUiPort _viewerModeUi;
    private readonly FileOperationApplicationCoordinator _fileOperations;
    private readonly IFileOperationUiPort _fileOperationUi;
    private readonly ICommandApplicationShellPort _shell;

    public CommandApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        BrowserNavigationWorkflowApplicationCoordinator navigation,
        BrowserRefreshWorkflowApplicationCoordinator refresh,
        BrowserTabWorkflowApplicationCoordinator tabs,
        UnifiedUndoRedoApplicationCoordinator unifiedUndoRedo,
        BrowserCategoryWorkflowApplicationCoordinator categories,
        BrowserWorkspacePersistenceApplicationCoordinator persistence,
        ViewerApplicationCoordinator viewer,
        ViewerWorkflowApplicationCoordinator viewerWorkflow,
        ViewerModeApplicationCoordinator viewerMode,
        IViewerModeUiPort viewerModeUi,
        FileOperationApplicationCoordinator fileOperations,
        IFileOperationUiPort fileOperationUi,
        ICommandApplicationShellPort shell)
    {
        _browser = browser;
        _navigation = navigation;
        _refresh = refresh;
        _tabs = tabs;
        _unifiedUndoRedo = unifiedUndoRedo;
        _categories = categories;
        _persistence = persistence;
        _viewer = viewer;
        _viewerWorkflow = viewerWorkflow;
        _viewerMode = viewerMode;
        _viewerModeUi = viewerModeUi;
        _fileOperations = fileOperations;
        _fileOperationUi = fileOperationUi;
        _shell = shell;
    }

    public CommandApplicationResult TryExecute(string commandId, CommandExecutionContext context)
    {
        bool browser = _viewer.Mode == ViewerApplicationMode.Browser;
        bool busy = _fileOperations.IsBusy;

        if (busy && CommandBusyPolicy.GetBehavior(commandId) == CommandBusyBehavior.Block)
        {
            return CommandApplicationResult.NotHandled;
        }

        switch (commandId)
        {
            case CommandIds.BrowserNavigateParent:
                return ExecuteParentNavigation(browser);
            case CommandIds.BrowserNavigateBack:
                return ExecuteHistoryNavigation(browser, BrowserHistoryDirection.Back);
            case CommandIds.BrowserNavigateForward:
                return ExecuteHistoryNavigation(browser, BrowserHistoryDirection.Forward);
            case CommandIds.BrowserReload:
                return ExecuteReload(browser);
            case CommandIds.BrowserMarkAllFiles:
                return ExecuteBulkMarks(browser, includeDirectories: false);
            case CommandIds.BrowserMarkAllItems:
                return ExecuteBulkMarks(browser, includeDirectories: true);
            case CommandIds.BrowserCursorTop:
                return ExecuteCursorNavigation(browser, top: true);
            case CommandIds.BrowserCursorBottom:
                return ExecuteCursorNavigation(browser, top: false);
            case CommandIds.BrowserNamePrefixJump:
                return EffectIf(browser, CommandApplicationEffectKind.BeginNamePrefixJump);
            case CommandIds.BrowserChangeAttributes:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.ChangeAttributes, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.BrowserExecute:
                return ExecuteEnter(browser, context.SelectionSnapshot);
            case CommandIds.BrowserDefaultOpen:
                return ExecuteDefaultOpen(browser, context.SelectionSnapshot);
            case CommandIds.BrowserDefaultOpenMarked:
                return ExecuteDefaultOpenMarked(browser, context.SelectionSnapshot);
            case CommandIds.BrowserOpenWith:
                return ExecuteOpenWith(browser, context.SelectionSnapshot);
            case CommandIds.BrowserOpenCommandDialog:
                return EffectIf(browser, CommandApplicationEffectKind.OpenCommandDialog);
            case CommandIds.BrowserCreateDirectory:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.CreateDirectory, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.BrowserCreateFile:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(
                        FileOperationCommandKind.CreateFile,
                        context.SelectionSnapshot,
                        CreateFileExtension: context.FileExtension),
                    _fileOperationUi);
            case CommandIds.BrowserPathEntryOpen:
                return EffectIf(browser, CommandApplicationEffectKind.OpenPathEntry);
            case CommandIds.BrowserOpenExplorer:
                return EffectIf(browser, CommandApplicationEffectKind.OpenExplorer);
            case CommandIds.BrowserRevealInExplorer:
                return ExecuteContextualExternalCommandEffect(commandId, browser, context);
            case CommandIds.BrowserOpenShell:
                return EffectIf(browser, CommandApplicationEffectKind.OpenTerminal, shellKind: ShellKind.PowerShell);
            case CommandIds.BrowserOpenExternalEditor:
                return EffectIf(browser, CommandApplicationEffectKind.OpenExternalEditor);
            case CommandIds.BrowserOpenCommandPrompt:
                return ExecuteContextualExternalCommandEffect(commandId, browser, context);
            case CommandIds.BrowserPreview:
                return ExecutePreviewLaunch(browser);
            case CommandIds.BrowserSort:
                return BeginSortInteraction(browser);
            case CommandIds.BrowserFilter:
                return BeginFilterFindInteraction(browser, context, UnifiedFilterFindMode.FilterCurrentTab);
            case CommandIds.BrowserSearch:
                return BeginFilterFindInteraction(browser, context, UnifiedFilterFindMode.FindNamesRecursively);
            case CommandIds.BrowserFilterClear:
                return ExecuteClearFilter(browser, context);
            case CommandIds.BrowserTree:
                return BeginTreeInteraction(browser);
            case CommandIds.BrowserQuickAccess:
                return BeginQuickAccessInteraction(browser);
            case CommandIds.BrowserLogdisk:
                return BeginLogdiskInteraction(browser);
            case CommandIds.ArchivePack:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Pack, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.ArchiveUnpack:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Unpack, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.ArchiveHash:
                FileOperationCommandRequest? hashRequest = CreateArchiveHashRequest(context);
                if (!browser || hashRequest is null)
                {
                    return CommandApplicationResult.NotHandled;
                }
                return TryStartFileOperation(hashRequest.Value, _fileOperationUi);
            case CommandIds.BrowserClipboardCopy:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(CreateClipboardCopyRequest(context), _fileOperationUi);
            case CommandIds.BrowserCopyFullPath:
                return EffectIf(browser, CommandApplicationEffectKind.CopyFullPaths);
            case CommandIds.BrowserCopyCurrentPath:
                return EffectIf(browser && !string.IsNullOrWhiteSpace(_browser.CurrentPath), CommandApplicationEffectKind.CopyCurrentPath);
            case CommandIds.BrowserShowHelp:
                return Effect(CommandApplicationEffectKind.ShowHelp);
            case CommandIds.BrowserOpenMarkSlot:
                return EffectIf(browser, CommandApplicationEffectKind.OpenMarkSlot);
            case CommandIds.BrowserTabNew:
                return ExecuteCreateTab(browser, null, useConfiguredInsertion: true);
            case CommandIds.BrowserOpenInNewTab:
                return ExecuteOpenInNewTab(browser, context.SelectionSnapshot);
            case CommandIds.BrowserTabHistoryBack:
                return ExecuteTabHistoryNavigation(browser, BrowserHistoryDirection.Back);
            case CommandIds.BrowserTabHistoryForward:
                return ExecuteTabHistoryNavigation(browser, BrowserHistoryDirection.Forward);
            case CommandIds.BrowserTabHistoryShow:
                return BeginTabHistoryInteraction(browser);
            case CommandIds.BrowserTabNext:
                return ExecuteAdjacentTab(browser, +1);
            case CommandIds.BrowserTabPrevious:
                return ExecuteAdjacentTab(browser, -1);
            case CommandIds.BrowserTabLayoutToggle:
                return ExecuteToggleTabLayout(browser && context.Scope == CommandScope.Browser);
            case CommandIds.BrowserTabReadOnlyToggle:
                return ExecuteToggleReadOnly(browser, context);
            case CommandIds.BrowserTabGroupCreate:
                return EffectIf(browser, CommandApplicationEffectKind.CreateBrowserTabGroup,
                    intValue: context.ContextTabIndex ?? _browser.Workspace.ActiveTabIndex,
                    categoryId: context.CategoryId,
                    contextBrowserTabId: context.ContextBrowserTabId);
            case CommandIds.BrowserTabGroupAdd:
                return EffectIf(browser, CommandApplicationEffectKind.AddBrowserTabGroupMember,
                    intValue: context.ContextTabIndex ?? _browser.Workspace.ActiveTabIndex,
                    textValue: context.RuntimeTargetId?.ToString("N"),
                    categoryId: context.CategoryId,
                    contextBrowserTabId: context.ContextBrowserTabId);
            case CommandIds.BrowserTabGroupRemove:
                return EffectIf(browser, CommandApplicationEffectKind.RemoveBrowserTabGroupMember,
                    intValue: context.ContextTabIndex ?? _browser.Workspace.ActiveTabIndex,
                    categoryId: context.CategoryId,
                    contextBrowserTabId: context.ContextBrowserTabId);
            case CommandIds.BrowserTabGroupRename:
                return EffectIf(browser, CommandApplicationEffectKind.RenameBrowserTabGroup,
                    intValue: context.ContextTabIndex ?? _browser.Workspace.ActiveTabIndex,
                    textValue: context.RuntimeTargetId?.ToString("N"));
            case CommandIds.BrowserTabCategoryManage:
                return EffectIf(browser, CommandApplicationEffectKind.ManageTabCategories);
            case CommandIds.BrowserTabCategoryAdd:
                return ExecuteAddCategory(browser);
            case CommandIds.BrowserTabCategoryRename:
                return BeginCategoryRenameInteraction(browser, context.CategoryId);
            case CommandIds.BrowserTabCategoryDelete:
                return BeginCategoryDeleteInteraction(browser, context.CategoryId);
            case CommandIds.BrowserTabCategoryMoveLeft:
                return ExecuteReorderCategory(browser, context.CategoryId, -1);
            case CommandIds.BrowserTabCategoryMoveRight:
                return ExecuteReorderCategory(browser, context.CategoryId, +1);
            case CommandIds.BrowserTabCategoryNext:
                return ExecuteAdjacentCategory(browser, +1);
            case CommandIds.BrowserTabCategoryPrevious:
                return ExecuteAdjacentCategory(browser, -1);
            case CommandIds.BrowserTabClose:
                return ExecuteCloseTab(browser, context);
            case CommandIds.BrowserTabRestoreClosed:
                return ExecuteRestoreClosedTab(browser);
            case CommandIds.ClipboardPaste:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Paste),
                    _fileOperationUi);
            case CommandIds.ClipboardCut:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Cut, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.BrowserProperties:
                return EffectIf(browser, CommandApplicationEffectKind.Properties, selection: context.SelectionSnapshot);
            case CommandIds.FileCopy:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Copy, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.FileMove:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Move, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.FileRename:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Rename, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.FileDelete:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Delete, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.EditUndo:
                return ExecuteUnifiedHistory(browser, redo: false);
            case CommandIds.EditRedo:
                return ExecuteUnifiedHistory(browser, redo: true);
            case CommandIds.AppOpenSystemInformation:
                return EffectIf(browser, CommandApplicationEffectKind.ShowSystemInformation);
            case CommandIds.AppOpenNewInstance:
                return EffectIf(browser, CommandApplicationEffectKind.OpenNewInstance, textValue: _browser.CurrentPath);
            case CommandIds.AppOpenControlPanel:
                return EffectIf(browser, CommandApplicationEffectKind.OpenControlPanel);
            case CommandIds.BrowserTabLock:
                return ExecuteToggleLock(browser, context);
            case CommandIds.BrowserTabCloseRight:
                return ExecuteCloseTabRange(browser, context, BrowserTabCloseScope.Right);
            case CommandIds.BrowserTabCloseLeft:
                return ExecuteCloseTabRange(browser, context, BrowserTabCloseScope.Left);
            case CommandIds.BrowserTabCloseOther:
                return ExecuteCloseTabRange(browser, context, BrowserTabCloseScope.Other);
            case CommandIds.AppOpenSettings:
                return Effect(CommandApplicationEffectKind.OpenSettings);
            case CommandIds.AppOpenCommandLauncher:
                return Effect(CommandApplicationEffectKind.OpenCommandLauncher);
            case CommandIds.AppOpenCommandList:
                return Effect(CommandApplicationEffectKind.ShowCommandList);
            case CommandIds.AppOpenManagedTrash:
                return Effect(CommandApplicationEffectKind.OpenManagedTrash);
            case CommandIds.AppEmptyManagedTrash:
                if (!browser || context.Scope != CommandScope.Global) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.EmptyManagedTrash),
                    _fileOperationUi);
            default:
                return CommandApplicationResult.NotHandled;
        }
    }

    public CommandApplicationResult Continue(
        CommandApplicationInteractionRequest request,
        CommandApplicationInteractionResponse response)
    {
        switch (request, response)
        {
            case (SortCommandInteractionRequest, SortCommandInteractionResponse sort):
                if (!sort.Accepted)
                {
                    return Effect(CommandApplicationEffectKind.NoOp);
                }

                return ExecuteSortContinuation(sort.SortKind, sort.Ascending);
            case (UnifiedFilterFindCommandInteractionRequest unified, UnifiedFilterFindCommandInteractionResponse unifiedResponse):
                return ExecuteUnifiedFilterFindContinuation(unified, unifiedResponse);
            case (TreeCommandInteractionRequest, TreeCommandInteractionResponse tree):
                return string.IsNullOrWhiteSpace(tree.SelectedPath)
                    ? Effect(CommandApplicationEffectKind.NoOp)
                    : ExecuteTreeContinuation(tree.SelectedPath);
            case (QuickAccessCommandInteractionRequest, QuickAccessCommandInteractionResponse quickAccess):
                return ExecuteQuickAccessContinuation(quickAccess);
            case (BrowserDerivedTabNavigationInteractionRequest derived, BrowserDerivedTabNavigationInteractionResponse derivedResponse):
                return ExecuteDerivedTabNavigationContinuation(derived, derivedResponse);
            case (LogdiskCommandInteractionRequest, LogdiskCommandInteractionResponse logdisk):
                return ExecuteLogdiskContinuation(logdisk.SelectedPath);
            case (ArchiveListCommandInteractionRequest archive, ArchiveListCommandInteractionResponse archiveResponse):
                return ExecuteArchiveListContinuation(archive, archiveResponse);
            case (CategoryRenameCommandInteractionRequest rename, CategoryRenameCommandInteractionResponse renamed):
                return ExecuteCategoryRenameContinuation(rename, renamed);
            case (CategoryDeleteCommandInteractionRequest delete, CategoryDeleteCommandInteractionResponse deleted):
                return ExecuteCategoryDeleteContinuation(delete, deleted);
            case (TabCloseCategoryRemovalInteractionRequest tabClose, TabCloseCategoryRemovalInteractionResponse tabCloseResponse):
                return ExecuteTabCloseCategoryRemovalContinuation(tabClose, tabCloseResponse);
            case (BrowserTabVisitHistoryCommandInteractionRequest history, BrowserTabVisitHistoryCommandInteractionResponse historyResponse):
                return historyResponse.Accepted
                    ? ExecuteTabHistoryJump(historyResponse.Direction, historyResponse.Depth, browser: true)
                    : Effect(CommandApplicationEffectKind.NoOp);
            default:
                return CommandApplicationResult.NotHandled;
        }
    }

    private CommandApplicationResult ExecutePreviewLaunch(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        if (string.IsNullOrWhiteSpace(state.CurrentItemPath) ||
            string.Equals(state.CurrentItemName, "..", StringComparison.Ordinal) ||
            Directory.Exists(state.CurrentItemPath))
        {
            return CommandApplicationResult.NotHandled;
        }

        PreviewKind contentKind = _viewerWorkflow.ResolvePreviewLaunchKind(state.CurrentItemPath);
        if (contentKind == PreviewKind.None)
        {
            return CommandApplicationResult.NotHandled;
        }

        _viewerMode.Switch(ViewerApplicationMode.Viewer, contentKind, _viewerModeUi);
        return Effect(CommandApplicationEffectKind.ViewerCommandCompleted);
    }

    private CommandApplicationResult BeginSortInteraction(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        return Interaction(new SortCommandInteractionRequest(
            _browser.CurrentSort,
            _browser.SortAscending));
    }

    private CommandApplicationResult BeginFilterFindInteraction(
        bool allowed,
        CommandExecutionContext context,
        UnifiedFilterFindMode initialMode)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        int tabIndex = context.ContextTabIndex ?? _browser.Workspace.ActiveTabIndex;
        if (tabIndex < 0 || tabIndex >= _browser.Workspace.TabCount)
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserTabState state = _browser.Workspace.TabStates[tabIndex];
        return Interaction(new UnifiedFilterFindCommandInteractionRequest(
            tabIndex,
            state.CurrentPath,
            UnifiedFilterFindCriteria.FromTab(state.FilterPattern, state.FilterUseRegex, state.FilterLock),
            initialMode));
    }

    private CommandApplicationResult ExecuteClearFilter(bool allowed, CommandExecutionContext context)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        int tabIndex = context.ContextTabIndex ?? _browser.Workspace.ActiveTabIndex;
        if (tabIndex < 0 || tabIndex >= _browser.Workspace.TabCount)
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserTabState target = _browser.Workspace.TabStates[tabIndex];
        if (!TabFilterLockService.IsActive(target.FilterPattern, target.FilterLock))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserUnifiedFilterExecution execution = _tabs.ExecuteUnifiedFilter(
            tabIndex,
            string.Empty,
            target.FilterUseRegex,
            TabFilterLockState.Disabled(),
            tabIndex == _browser.Workspace.ActiveTabIndex,
            state.IsBrowserMode,
            state.IsBusy,
            "現在ディレクトリを再読込しました。",
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(
            CommandApplicationEffectKind.ApplyUnifiedFilter,
            intValue: tabIndex,
            unifiedFilter: execution);
    }

    private CommandApplicationResult BeginTreeInteraction(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        return Interaction(new TreeCommandInteractionRequest(_browser.CurrentPath));
    }

    private CommandApplicationResult BeginQuickAccessInteraction(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        IReadOnlyList<string> backHistory = _browser.GetBackHistorySnapshot();
        IReadOnlyList<string> forwardHistory = _browser.GetForwardHistorySnapshot();
        return Interaction(new QuickAccessCommandInteractionRequest(
            _browser.CurrentPath,
            _browser.Workspace.QuickAccessSnapshot.Clone(),
            QuickAccessService.BuildHistoryEntries(backHistory, forwardHistory)));
    }

    private CommandApplicationResult BeginLogdiskInteraction(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        string defaultPath = string.IsNullOrWhiteSpace(_browser.CurrentPath)
            ? (Path.GetPathRoot(_browser.CurrentPath) ?? "C:\\")
            : _browser.CurrentPath;
        return Interaction(new LogdiskCommandInteractionRequest(
            defaultPath,
            _shell.GetSharedLocationCandidates()));
    }

    private CommandApplicationResult BeginCategoryRenameInteraction(bool allowed, string? categoryId)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        _categories.EnsureConfiguration();
        BrowserTabCategoryDefinition? category = string.IsNullOrWhiteSpace(categoryId)
            ? _browser.Workspace.FindCategorySnapshot(_browser.Workspace.ActiveCategoryId)
            : _browser.Workspace.FindCategorySnapshot(categoryId);
        return category == null
            ? CommandApplicationResult.NotHandled
            : Interaction(new CategoryRenameCommandInteractionRequest(category.Id, category.DisplayName));
    }

    private CommandApplicationResult BeginCategoryDeleteInteraction(bool allowed, string? categoryId)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        _categories.EnsureConfiguration();
        BrowserTabCategoryDefinition? category = string.IsNullOrWhiteSpace(categoryId)
            ? _browser.Workspace.FindCategorySnapshot(_browser.Workspace.ActiveCategoryId)
            : _browser.Workspace.FindCategorySnapshot(categoryId);
        if (category == null)
        {
            return CommandApplicationResult.NotHandled;
        }
        if (!_categories.CanRemove(category.Id))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "既定カテゴリは削除できません。");
        }

        return Interaction(new CategoryDeleteCommandInteractionRequest(category.Id, category.DisplayName));
    }

    private CommandApplicationResult ExecuteSortContinuation(SortKind sortKind, bool ascending)
    {
        CommandApplicationShellState state = _shell.CaptureState();
        BrowserManualRefreshExecution execution = _navigation.ExecuteSortAndReload(
            sortKind,
            ascending,
            state.IsBrowserMode,
            state.IsBusy,
            "現在ディレクトリを再読込しました。",
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyManualRefresh, manualRefresh: execution);
    }

    private CommandApplicationResult ExecuteUnifiedFilterFindContinuation(
        UnifiedFilterFindCommandInteractionRequest request,
        UnifiedFilterFindCommandInteractionResponse response)
    {
        if (!response.Accepted)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        UnifiedFilterFindCriteria criteria = response.Criteria.Clone();
        if (response.Mode != UnifiedFilterFindMode.FilterCurrentTab)
        {
            return Effect(
                CommandApplicationEffectKind.RunUnifiedFilterFind,
                unifiedSearch: new UnifiedFilterFindInteractionRequest(
                    request.TargetTabIndex,
                    request.RootPath,
                    criteria,
                    response.Mode));
        }

        CommandApplicationShellState state = _shell.CaptureState();
        bool isActive = request.TargetTabIndex == _browser.Workspace.ActiveTabIndex;
        BrowserUnifiedFilterExecution execution = _tabs.ExecuteUnifiedFilter(
            request.TargetTabIndex,
            criteria.NamePattern,
            criteria.NameUseRegex,
            criteria.DetailFilter,
            isActive,
            state.IsBrowserMode,
            state.IsBusy,
            "現在ディレクトリを再読込しました。",
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(
            CommandApplicationEffectKind.ApplyUnifiedFilter,
            intValue: request.TargetTabIndex,
            unifiedFilter: execution);
    }

    private CommandApplicationResult ExecuteTreeContinuation(string selectedPath)
    {
        CommandApplicationShellState state = _shell.CaptureState();
        return ExecuteDirectoryCommandNavigation(
            _browser.CreateNavigationRequest(selectedPath),
            state,
            clearPreview: false,
            refreshPreview: true);
    }

    private CommandApplicationResult ExecuteQuickAccessContinuation(QuickAccessCommandInteractionResponse response)
    {
        if (response.Action == CommandQuickAccessAction.Cancel)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserQuickAccessNavigationPreparation preparation = _navigation.PrepareQuickAccessNavigation(
            response.Action == CommandQuickAccessAction.Navigate
                ? QuickAccessApplicationAction.Navigate
                : QuickAccessApplicationAction.SaveOnly,
            response.UpdatedStore,
            response.SelectedEntry);
        if (preparation.Navigation is { RequiresDerivedTabConfirmation: true })
        {
            return Interaction(new BrowserDerivedTabNavigationInteractionRequest(
                new BrowserQuickAccessNavigationContinuation(preparation)));
        }

        BrowserQuickAccessNavigationExecution execution = _navigation.ExecutePreparedQuickAccessNavigation(
            preparation,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState,
            confirmedDerivedTabCreation: true);
        return Effect(CommandApplicationEffectKind.ApplyQuickAccess, quickAccess: execution);
    }

    private CommandApplicationResult ExecuteDerivedTabNavigationContinuation(
        BrowserDerivedTabNavigationInteractionRequest request,
        BrowserDerivedTabNavigationInteractionResponse response)
    {
        if (!response.Accepted)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        return request.Continuation switch
        {
            BrowserDirectoryNavigationContinuation directory =>
                Effect(
                    CommandApplicationEffectKind.ApplyDirectoryNavigation,
                    directoryNavigation: _navigation.ExecutePreparedDirectoryNavigation(
                        directory.Preparation,
                        _tabs.MaxTabCount,
                        state.CurrentTab,
                        state.DirectoryLoadOptions,
                        state.ColumnCount,
                        state.RefreshShellState,
                        confirmedDerivedTabCreation: true,
                        recordDirectoryMoveHistory: directory.RecordDirectoryMoveHistory),
                    boolValue: directory.ClearPreview,
                    refreshPreview: directory.RefreshPreview),
            BrowserHistoryNavigationContinuation history =>
                Effect(
                    CommandApplicationEffectKind.ApplyHistoryNavigation,
                    historyNavigation: _navigation.ExecutePreparedHistoryNavigation(
                        history.Start,
                        state.CurrentTab,
                        _tabs.MaxTabCount,
                        state.DirectoryLoadOptions,
                        state.ColumnCount,
                        state.RefreshShellState,
                        confirmedDerivedTabCreation: true)),
            BrowserQuickAccessNavigationContinuation quickAccess =>
                Effect(
                    CommandApplicationEffectKind.ApplyQuickAccess,
                    quickAccess: _navigation.ExecutePreparedQuickAccessNavigation(
                        quickAccess.Preparation,
                        state.CurrentTab,
                        state.DirectoryLoadOptions,
                        state.ColumnCount,
                        state.RefreshShellState,
                        confirmedDerivedTabCreation: true)),
            BrowserLogdiskNavigationContinuation logdisk =>
                Effect(
                    CommandApplicationEffectKind.ApplyLogdisk,
                    pathEntry: logdisk.PathEntry,
                    directoryNavigation: _navigation.ExecutePreparedDirectoryNavigation(
                        logdisk.Preparation,
                        _tabs.MaxTabCount,
                        state.CurrentTab,
                        state.DirectoryLoadOptions,
                        state.ColumnCount,
                        state.RefreshShellState,
                        confirmedDerivedTabCreation: true,
                        recordDirectoryMoveHistory: true)),
            _ => CommandApplicationResult.NotHandled
        };
    }

    private CommandApplicationResult ExecuteLogdiskContinuation(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        BrowserPathEntryNavigationResult navigation = _browser.ResolvePathEntry(selectedPath);
        if (navigation.TargetKind != BrowserPathEntryTargetKind.Directory)
        {
            return Effect(
                CommandApplicationEffectKind.ApplyLogdisk,
                pathEntry: navigation);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserNavigationPreparation preparation = _navigation.PrepareDirectoryNavigation(
            _browser.CreateNavigationRequest(
                navigation.ResolvedPath,
                isHistoryNavigation: false,
                suppressRecent: false),
            _tabs.MaxTabCount,
            state.CurrentTab);
        if (preparation.RequiresDerivedTabConfirmation)
        {
            return Interaction(new BrowserDerivedTabNavigationInteractionRequest(
                new BrowserLogdiskNavigationContinuation(preparation, navigation)));
        }

        BrowserDirectoryNavigationExecution execution = _navigation.ExecutePreparedDirectoryNavigation(
            preparation,
            _tabs.MaxTabCount,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState,
            confirmedDerivedTabCreation: true,
            recordDirectoryMoveHistory: true);
        return Effect(
            CommandApplicationEffectKind.ApplyLogdisk,
            pathEntry: navigation,
            directoryNavigation: execution);
    }

    private CommandApplicationResult ExecuteCategoryRenameContinuation(
        CategoryRenameCommandInteractionRequest request,
        CategoryRenameCommandInteractionResponse response)
    {
        if (!response.Accepted || string.IsNullOrWhiteSpace(response.DisplayName))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        bool applied = _categories.Rename(request.CategoryId, response.DisplayName.Trim(), out bool duplicateName);
        return Effect(
            CommandApplicationEffectKind.ApplyCategoryRename,
            textValue: response.DisplayName.Trim(),
            boolValue: applied,
            categoryRename: new CategoryRenameApplicationResult(request.CategoryId, applied, duplicateName));
    }

    private CommandApplicationResult ExecuteCategoryDeleteContinuation(
        CategoryDeleteCommandInteractionRequest request,
        CategoryDeleteCommandInteractionResponse response)
    {
        if (!response.Accepted)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserCategoryRemovalExecution execution = _categories.ExecuteRemoveAndSwitch(
            [request.CategoryId],
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(
            CommandApplicationEffectKind.ApplyCategoryDelete,
            textValue: request.DisplayName,
            categoryDelete: execution);
    }

    private CommandApplicationResult ExecuteTabCloseCategoryRemovalContinuation(
        TabCloseCategoryRemovalInteractionRequest request,
        TabCloseCategoryRemovalInteractionResponse response)
    {
        if (!response.Accepted)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabCloseConfirmationExecution execution = _categories.ContinueTabCloseAfterCategoryConfirmation(
            request.TabIndex,
            removeCategory: true,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(
            CommandApplicationEffectKind.ApplyTabCloseConfirmation,
            intValue: request.TabIndex,
            textValue: request.DisplayName,
            tabCloseConfirmation: execution);
    }

    private static CommandApplicationResult Interaction(CommandApplicationInteractionRequest request) =>
        new(true, null, request);

    private static CommandApplicationResult EffectIf(
        bool allowed,
        CommandApplicationEffectKind kind,
        int intValue = 0,
        string? textValue = null,
        bool boolValue = false,
        ShellKind? shellKind = null,
        SelectionResult? selection = null,
        string? categoryId = null,
        Guid? contextBrowserTabId = null)
    {
        return allowed
            ? Effect(
                kind,
                intValue,
                textValue,
                boolValue: boolValue,
                shellKind: shellKind,
                selection: selection,
                categoryId: categoryId,
                contextBrowserTabId: contextBrowserTabId)
            : CommandApplicationResult.NotHandled;
    }

    internal static CommandApplicationResult ExecuteContextualExternalCommandEffect(
        string commandId,
        bool allowed,
        CommandExecutionContext context)
    {
        return commandId switch
        {
            CommandIds.BrowserRevealInExplorer => EffectIf(
                allowed && !string.IsNullOrWhiteSpace(context.ContextTargetPath),
                CommandApplicationEffectKind.RevealInExplorer,
                textValue: context.ContextTargetPath),
            CommandIds.BrowserOpenCommandPrompt => EffectIf(
                allowed,
                CommandApplicationEffectKind.OpenTerminal,
                textValue: context.ContextTargetPath,
                shellKind: ShellKind.CommandPrompt),
            _ => CommandApplicationResult.NotHandled
        };
    }

    private CommandApplicationResult ExecuteDirectoryCommandNavigation(
        BrowserNavigationCoordinator.DirectoryNavigationRequest? request,
        CommandApplicationShellState state,
        bool clearPreview,
        bool refreshPreview = false,
        bool recordDirectoryMoveHistory = false)
    {
        BrowserNavigationPreparation preparation = _navigation.PrepareDirectoryNavigation(
            request,
            _tabs.MaxTabCount,
            state.CurrentTab);
        if (preparation.RequiresDerivedTabConfirmation)
        {
            return Interaction(new BrowserDerivedTabNavigationInteractionRequest(
                new BrowserDirectoryNavigationContinuation(
                    preparation,
                    clearPreview,
                    refreshPreview,
                    recordDirectoryMoveHistory)));
        }

        BrowserDirectoryNavigationExecution execution = _navigation.ExecutePreparedDirectoryNavigation(
            preparation,
            _tabs.MaxTabCount,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState,
            confirmedDerivedTabCreation: true,
            recordDirectoryMoveHistory: recordDirectoryMoveHistory);
        return Effect(
            CommandApplicationEffectKind.ApplyDirectoryNavigation,
            directoryNavigation: execution,
            boolValue: clearPreview,
            refreshPreview: refreshPreview);
    }

    private CommandApplicationResult ExecuteParentNavigation(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        return ExecuteDirectoryCommandNavigation(
            _browser.CreateParentNavigationRequest(),
            state,
            clearPreview: false);
    }

    private CommandApplicationResult ExecuteHistoryNavigation(bool allowed, BrowserHistoryDirection direction)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserHistoryNavigationStart started = direction == BrowserHistoryDirection.Back
            ? _navigation.BeginHistoryBack(state.CurrentTab, _tabs.MaxTabCount)
            : _navigation.BeginHistoryForward(state.CurrentTab, _tabs.MaxTabCount);
        if (started.RequiresDerivedTabConfirmation)
        {
            return Interaction(new BrowserDerivedTabNavigationInteractionRequest(
                new BrowserHistoryNavigationContinuation(started)));
        }

        BrowserHistoryNavigationExecution execution = _navigation.ExecutePreparedHistoryNavigation(
            started,
            state.CurrentTab,
            _tabs.MaxTabCount,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState,
            confirmedDerivedTabCreation: true);
        return Effect(CommandApplicationEffectKind.ApplyHistoryNavigation, historyNavigation: execution);
    }

    private CommandApplicationResult ExecuteTabHistoryNavigation(bool allowed, BrowserHistoryDirection direction)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabHistoryNavigationResult execution = _tabs.ExecuteTabHistoryNavigation(
            direction,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyTabHistoryNavigation, tabHistoryNavigation: execution);
    }

    private CommandApplicationResult BeginTabHistoryInteraction(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        return Interaction(new BrowserTabVisitHistoryCommandInteractionRequest(
            _tabs.BuildTabVisitHistorySnapshot()));
    }

    private CommandApplicationResult ExecuteTabHistoryJump(
        BrowserTabVisitHistoryListDirection direction,
        int depth,
        bool browser)
    {
        if (!browser || depth < 0 || direction == BrowserTabVisitHistoryListDirection.Current)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabHistoryNavigationResult execution = _tabs.ExecuteTabHistoryJump(
            direction,
            depth,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyTabHistoryNavigation, tabHistoryNavigation: execution);
    }

    private CommandApplicationResult ExecuteReload(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserManualRefreshExecution execution = _refresh.ExecuteManualRefresh(
            state.IsBrowserMode,
            state.IsBusy,
            "現在ディレクトリを再読込しました。",
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyManualRefresh, manualRefresh: execution);
    }

    private CommandApplicationResult ExecuteCursorNavigation(bool allowed, bool top)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        if (state.BrowserItemCount <= 0)
        {
            return Effect(CommandApplicationEffectKind.CursorNoOp);
        }

        int targetIndex = top ? 0 : Math.Max(0, state.TotalItemCount - 1);
        BrowserCursorNavigationExecution execution = _navigation.ExecuteCursorNavigation(
            targetIndex,
            state.ItemsPerPage,
            state.IsBusy,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyCursorNavigation, cursorNavigation: execution);
    }

    private CommandApplicationResult ExecuteBulkMarks(bool allowed, bool includeDirectories)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        IReadOnlyList<string> targets = _shell.GetBulkMarkTargetPaths(includeDirectories);
        if (targets.Count == 0)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        BrowserBulkMarkTransition transition = _tabs.ExecuteToggleBulkMarksAndSync(targets);
        return Effect(
            CommandApplicationEffectKind.ApplyBulkMarks,
            intValue: targets.Count,
            boolValue: includeDirectories,
            bulkMarks: transition);
    }

    private CommandApplicationResult ExecuteEnter(bool allowed, SelectionResult? selectionSnapshot)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserOpenSelection selection = BrowserOpenSelectionResolver.Resolve(
            selectionSnapshot,
            state.CurrentItemName,
            state.CurrentItemPath);
        if (selection.Kind == BrowserOpenSelectionKind.ParentDirectory)
        {
            return ExecuteDirectoryCommandNavigation(
                _browser.CreateParentNavigationRequest(),
                state,
                clearPreview: true);
        }

        if (selection.Kind == BrowserOpenSelectionKind.SingleDirectory &&
            !string.IsNullOrWhiteSpace(selection.Paths[0]))
        {
            return ExecuteDirectoryCommandNavigation(
                _browser.CreateNavigationRequest(selection.Paths[0]),
                state,
                clearPreview: true);
        }

        if (selection.Kind is not
                (BrowserOpenSelectionKind.SingleFile or BrowserOpenSelectionKind.SingleArchive) ||
            string.IsNullOrWhiteSpace(selection.Paths[0]))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        string selectedPath = selection.Paths[0];
        ViewerEnterDecision decision = _viewerWorkflow.ResolveEnter(selectedPath);
        if (decision.UseExternalMediaPlayback)
        {
            return Effect(
                CommandApplicationEffectKind.LaunchExternalMediaPlayback,
                textValue: selectedPath,
                boolValue: decision.IsAudio);
        }

        if (decision.OpenRequest is not { } openRequest)
        {
            return CommandApplicationResult.NotHandled;
        }

        return openRequest.Route switch
        {
            ViewerOpenRoute.ExecuteTarget => Effect(
                CommandApplicationEffectKind.ConfirmExecuteTarget,
                textValue: openRequest.FullPath),
            ViewerOpenRoute.Archive => BeginArchiveListInteraction(openRequest.FullPath),
            ViewerOpenRoute.MediaViewer => Effect(
                CommandApplicationEffectKind.OpenMediaViewer,
                intValue: (int)openRequest.ViewerKind,
                textValue: openRequest.FullPath),
            ViewerOpenRoute.InternalViewer => ExecuteInternalViewerLaunch(openRequest.ViewerKind),
            _ => CommandApplicationResult.NotHandled
        };
    }

    private CommandApplicationResult ExecuteInternalViewerLaunch(PreviewKind kind)
    {
        if (kind == PreviewKind.None)
        {
            return CommandApplicationResult.NotHandled;
        }

        _viewerMode.Switch(ViewerApplicationMode.Viewer, kind, _viewerModeUi);
        return Effect(CommandApplicationEffectKind.ViewerCommandCompleted);
    }

    private CommandApplicationResult BeginArchiveListInteraction(string archivePath)
    {
        CommandApplicationShellState state = _shell.CaptureState();
        ArchiveListResult result = ArchiveListService.GetArchiveContents(
            state.SevenZipPath,
            archivePath);
        if (ResolveArchiveListFailureEffect(result) is { } failureEffect)
        {
            return Effect(
                failureEffect,
                textValue: archivePath,
                messageValue: result.ErrorMessage);
        }

        return Interaction(new ArchiveListCommandInteractionRequest(
            archivePath,
            result,
            state.CurrentPath,
            state.IsReadOnlyBrowserTab,
            state.DateFormat,
            state.SizeFormat));
    }

    private CommandApplicationResult ExecuteArchiveListContinuation(
        ArchiveListCommandInteractionRequest _,
        ArchiveListCommandInteractionResponse response)
    {
        CommandApplicationEffectKind continuationEffect = ResolveArchiveListContinuationEffect(response);
        if (continuationEffect == CommandApplicationEffectKind.NoOp)
        {
            return Effect(continuationEffect);
        }

        if (response.PendingExtractRequest is { } extractRequest)
        {
            return _fileOperations.TryStartArchiveExtract(extractRequest, _fileOperationUi)
                ? Effect(continuationEffect)
                : Effect(CommandApplicationEffectKind.NoOp);
        }

        return Effect(CommandApplicationEffectKind.NoOp);
    }

    internal static CommandApplicationEffectKind? ResolveArchiveListFailureEffect(ArchiveListResult result)
        => result.Success ? null : CommandApplicationEffectKind.OpenArchiveFallback;

    internal static CommandApplicationEffectKind ResolveArchiveListContinuationEffect(
        ArchiveListCommandInteractionResponse response)
        => response.PendingExtractRequest is null
            ? CommandApplicationEffectKind.NoOp
            : CommandApplicationEffectKind.FileOperationStarted;

    private CommandApplicationResult ExecuteDefaultOpen(bool allowed, SelectionResult? selectionSnapshot)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserOpenSelection selection = BrowserOpenSelectionResolver.Resolve(
            selectionSnapshot,
            state.CurrentItemName,
            state.CurrentItemPath);

        if (selection.Kind is
                BrowserOpenSelectionKind.SingleDirectory or
                BrowserOpenSelectionKind.SingleFile or
                BrowserOpenSelectionKind.SingleArchive)
        {
            return Effect(CommandApplicationEffectKind.ApplyDefaultOpen, textValue: selection.Paths[0]);
        }

        return Effect(CommandApplicationEffectKind.NoOp);
    }

    private CommandApplicationResult ExecuteOpenWith(bool allowed, SelectionResult? selectionSnapshot)
    {
        return ExecuteOpenWithEffect(allowed, selectionSnapshot);
    }

    internal static CommandApplicationResult ExecuteOpenWithEffect(
        bool allowed,
        SelectionResult? selectionSnapshot)
    {
        if (!allowed || selectionSnapshot is not { Count: 1 })
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserOpenSelection selection = BrowserOpenSelectionResolver.Resolve(
            selectionSnapshot,
            currentItemName: null,
            currentItemPath: null);
        if (!BrowserOpenSelectionResolver.IsCommandEnabled(CommandIds.BrowserOpenWith, selection))
        {
            return CommandApplicationResult.NotHandled;
        }

        return Effect(CommandApplicationEffectKind.OpenWithDialog, textValue: selection.Paths[0]);
    }

    private CommandApplicationResult ExecuteDefaultOpenMarked(bool allowed, SelectionResult? selectionSnapshot)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        SelectionResult? markedSelection = ResolveCommandSelection(null);
        if (markedSelection is not { HasMarkedSelection: true, Count: >= 2 })
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        BrowserOpenSelection selection = BrowserOpenSelectionResolver.Resolve(
            markedSelection,
            currentItemName: null,
            currentItemPath: null);
        if (!selection.IsBatchFile)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        return Effect(
            CommandApplicationEffectKind.ApplyDefaultOpen,
            selection: new SelectionResult(selection.Paths, hasMarkedSelection: true));
    }

    private CommandApplicationResult ExecuteOpenInNewTab(bool allowed, SelectionResult? selectionSnapshot)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
        {
            CommandApplicationShellState state = _shell.CaptureState();
            BrowserOpenSelection selection = BrowserOpenSelectionResolver.Resolve(
                ResolveCommandSelection(selectionSnapshot),
                state.CurrentItemName,
                state.CurrentItemPath);
            if (selection.Kind == BrowserOpenSelectionKind.SingleDirectory)
            {
                return ExecuteCreateTab(true, selection.Paths[0], useConfiguredInsertion: true);
            }

            if (!selection.IsBatchDirectory)
            {
                return CommandApplicationResult.NotHandled;
            }

            IReadOnlyList<string> validDirectories = selection.Paths
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (validDirectories.Count < 2)
            {
                return validDirectories.Count == 1
                    ? ExecuteCreateTab(true, validDirectories[0], useConfiguredInsertion: true)
                    : Effect(CommandApplicationEffectKind.NoOp);
            }

            int confirmationThreshold = Math.Max(
                BrowserTabSettings.MinimumMultiDirectoryOpenConfirmationThreshold,
                state.MultiDirectoryOpenConfirmationThreshold);
            if (BrowserTabBatchConfirmationPolicy.RequiresConfirmation(
                    validDirectories.Count,
                    confirmationThreshold) &&
                !_shell.ConfirmMultiDirectoryOpen(validDirectories.Count, confirmationThreshold))
            {
                return Effect(CommandApplicationEffectKind.NoOp);
            }

            BrowserTabCreationBatchExecution execution = _tabs.ExecuteCreateTabs(
                validDirectories,
                state.CurrentTab,
                state.CurrentPath,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState);
            if (execution.Succeeded && execution.HistoryOperationId is Guid historyOperationId)
            {
                _unifiedUndoRedo.RecordTab(historyOperationId);
            }
            return Effect(
                CommandApplicationEffectKind.ApplyTabCreationBatch,
                tabCreationBatch: execution);
        }
    }

    private CommandApplicationResult ExecuteUnifiedHistory(bool allowed, bool redo)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        bool available = redo
            ? _unifiedUndoRedo.TryPeekRedo(out UnifiedUndoRedoEntry entry, out string? unavailableReason)
            : _unifiedUndoRedo.TryPeekUndo(out entry, out unavailableReason);
        if (!available)
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: unavailableReason);
        }

        if (entry.Domain == UnifiedUndoRedoDomain.File)
        {
            return TryStartFileOperation(
                new FileOperationCommandRequest(redo ? FileOperationCommandKind.Redo : FileOperationCommandKind.Undo),
                _fileOperationUi);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabBatchHistoryExecution execution = redo
            ? _tabs.ExecuteTabBatchRedo(
                state.CurrentTab,
                state.CurrentPath,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState)
            : _tabs.ExecuteTabBatchUndo(
                state.CurrentTab,
                state.CurrentPath,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState);
        if (execution.Applied &&
            ((!redo && execution.OperationId != entry.OperationId) ||
             (redo && execution.OperationId is not Guid)))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "タブ操作の履歴を同期できませんでした。");
        }
        if (execution.Applied)
        {
            bool committed = redo
                ? execution.OperationId is Guid newOperationId &&
                  _unifiedUndoRedo.CommitTabRedo(entry.OperationId, newOperationId)
                : _unifiedUndoRedo.CommitTabUndo(entry.OperationId);
            if (!committed)
            {
                return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "タブ操作の履歴を同期できませんでした。");
            }
        }
        return Effect(CommandApplicationEffectKind.ApplyTabBatchHistory, tabBatchHistory: execution);
    }

    private SelectionResult? ResolveCommandSelection(SelectionResult? selectionSnapshot)
    {
        if (selectionSnapshot != null)
        {
            return selectionSnapshot;
        }

        IReadOnlyList<string> markedPaths = _browser.Selection.Snapshot();
        return markedPaths.Count == 0
            ? null
            : new SelectionResult(markedPaths, hasMarkedSelection: true);
    }

    private CommandApplicationResult ExecuteCreateTab(bool allowed, string? initialPath, bool useConfiguredInsertion)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
        {
            CommandApplicationShellState state = _shell.CaptureState();
            BrowserTabCreationExecution execution = _tabs.ExecuteCreateTab(
                state.CurrentTab,
                state.CurrentPath,
                initialPath,
                useConfiguredInsertion,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState);
            return Effect(CommandApplicationEffectKind.ApplyTabCreation, tabCreation: execution);
        }
    }

    private CommandApplicationResult ExecuteAdjacentTab(bool allowed, int delta)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
        {
            CommandApplicationShellState state = _shell.CaptureState();
            BrowserTabSwitchWorkflowResult execution = _tabs.ExecuteAdjacentSwitch(
                delta,
                wrap: true,
                currentPath: state.CurrentPath,
                currentState: state.CurrentTab,
                options: state.DirectoryLoadOptions,
                columnCount: state.ColumnCount,
                shellState: state.RefreshShellState);
            return Effect(CommandApplicationEffectKind.ApplyTabSwitch, tabSwitch: execution);
        }
    }

    private CommandApplicationResult ExecuteAdjacentCategory(bool allowed, int delta)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
        {
            CommandApplicationShellState state = _shell.CaptureState();
            BrowserCategorySwitchWorkflowResult execution = _categories.ExecuteAdjacentSwitch(
                delta,
                currentPath: state.CurrentPath,
                currentState: state.CurrentTab,
                options: state.DirectoryLoadOptions,
                columnCount: state.ColumnCount,
                shellState: state.RefreshShellState);
            return Effect(CommandApplicationEffectKind.ApplyCategorySwitch, categorySwitch: execution);
        }
    }

    private CommandApplicationResult ExecuteToggleTabLayout(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabLayoutMode next = state.TabLayoutMode == BrowserTabLayoutMode.Horizontal
            ? BrowserTabLayoutMode.Vertical
            : BrowserTabLayoutMode.Horizontal;
        if (!_persistence.SetBrowserTabLayout(next))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        return Effect(CommandApplicationEffectKind.ApplyTabLayout, intValue: (int)next);
    }

    private CommandApplicationResult ExecuteAddCategory(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserCategoryAddSwitchExecution execution = _categories.ExecuteAddAndSwitch(
            _browser.Workspace.GetNextCategoryDisplayName(),
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyCategoryAdd, categoryAdd: execution);
    }

    private CommandApplicationResult ExecuteReorderCategory(bool allowed, string? categoryId, int delta)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        string targetCategoryId = string.IsNullOrWhiteSpace(categoryId)
            ? _browser.Workspace.ActiveCategoryId
            : categoryId;
        if (string.IsNullOrWhiteSpace(targetCategoryId))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        BrowserCategoryReorderTransition execution = _categories.Reorder(
            targetCategoryId,
            delta,
            state.CurrentTab);
        return Effect(CommandApplicationEffectKind.ApplyCategoryReorder, categoryReorder: execution);
    }

    private CommandApplicationResult ExecuteToggleReadOnly(bool browser, CommandExecutionContext context)
    {
        if (!browser || !TryResolveTabTarget(context, out int tabIndex))
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabToggleReadOnlyExecution execution = _tabs.ExecuteToggleReadOnly(
            tabIndex,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyTabReadOnly, tabReadOnly: execution);
    }

    private CommandApplicationResult ExecuteToggleLock(bool browser, CommandExecutionContext context)
    {
        if (!browser || !TryResolveTabTarget(context, out int tabIndex))
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabToggleLockExecution execution = _tabs.ExecuteToggleLock(
            tabIndex,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyTabLock, tabLock: execution);
    }

    private CommandApplicationResult ExecuteCloseTab(bool allowed, CommandExecutionContext context)
    {
        if (!allowed || !TryResolveTabTarget(context, out int tabIndex))
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabCloseExecution execution = _categories.ExecuteTabClose(
            tabIndex,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        if (execution.Decision.Kind == BrowserTabCloseDecisionKind.RequiresCategoryRemoval &&
            execution.Decision.CategoryId is { } categoryId &&
            _browser.Workspace.FindCategorySnapshot(categoryId) is { } category)
        {
            return Interaction(new TabCloseCategoryRemovalInteractionRequest(
                tabIndex,
                categoryId,
                category.DisplayName));
        }
        return Effect(CommandApplicationEffectKind.ApplyTabClose, tabClose: execution);
    }

    private CommandApplicationResult ExecuteCloseTabRange(bool allowed, CommandExecutionContext context, BrowserTabCloseScope scope)
    {
        if (!allowed || !TryResolveTabTarget(context, out int tabIndex))
        {
            return CommandApplicationResult.NotHandled;
        }

        List<int> tabIndices = Enumerable.Range(0, _browser.Workspace.TabCount)
            .Where(index => scope switch
            {
                BrowserTabCloseScope.Left => index < tabIndex,
                BrowserTabCloseScope.Right => index > tabIndex,
                _ => index != tabIndex
            })
            .ToList();
        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabRangeCloseExecution execution = _tabs.ExecuteCloseTabs(
            tabIndices,
            tabIndex,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(
            CommandApplicationEffectKind.ApplyTabRangeClose,
            intValue: tabIndex,
            textValue: scope.ToString(),
            tabRangeClose: execution);
    }

    private CommandApplicationResult ExecuteRestoreClosedTab(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserClosedTabRestoreExecution execution = _tabs.ExecuteRestoreLastClosedTab(
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyRestoredTab, restoredTab: execution);
    }

    internal static FileOperationCommandRequest? CreateArchiveHashRequest(CommandExecutionContext context)
    {
        if (context.HashAlgorithm is not { } algorithm)
        {
            return null;
        }

        return new FileOperationCommandRequest(
            FileOperationCommandKind.ArchiveHash,
            context.SelectionSnapshot,
            HashAlgorithm: algorithm);
    }

    internal static FileOperationCommandRequest CreateClipboardCopyRequest(CommandExecutionContext context)
        => new(FileOperationCommandKind.ClipboardCopy, context.SelectionSnapshot);

    private CommandApplicationResult TryStartFileOperation(
        FileOperationCommandRequest request,
        IFileOperationUiPort ui)
    {
        if (_fileOperations.TryStart(request, ui))
        {
            return new CommandApplicationResult(true);
        }

        return !_fileOperations.IsBusy && _fileOperations.IsReadOnlyBlocked(request.Kind)
            ? new CommandApplicationResult(true)
            : CommandApplicationResult.NotHandled;
    }

    private static CommandApplicationResult Effect(
        CommandApplicationEffectKind kind,
        int intValue = 0,
        string? textValue = null,
        string? messageValue = null,
        bool boolValue = false,
        ShellKind? shellKind = null,
        SelectionResult? selection = null,
        BrowserDirectoryNavigationExecution? directoryNavigation = null,
        BrowserHistoryNavigationExecution? historyNavigation = null,
        BrowserManualRefreshExecution? manualRefresh = null,
        BrowserCursorNavigationExecution? cursorNavigation = null,
        BrowserBulkMarkTransition? bulkMarks = null,
        BrowserTabCreationExecution? tabCreation = null,
        BrowserTabCreationBatchExecution? tabCreationBatch = null,
        BrowserTabBatchHistoryExecution? tabBatchHistory = null,
        BrowserTabSwitchWorkflowResult? tabSwitch = null,
        BrowserCategorySwitchWorkflowResult? categorySwitch = null,
        BrowserCategoryAddSwitchExecution? categoryAdd = null,
        BrowserCategoryReorderTransition? categoryReorder = null,
        BrowserTabToggleLockExecution? tabLock = null,
        BrowserTabToggleReadOnlyExecution? tabReadOnly = null,
        BrowserUnifiedFilterExecution? unifiedFilter = null,
        UnifiedFilterFindInteractionRequest? unifiedSearch = null,
        BrowserTabCloseExecution? tabClose = null,
        BrowserTabRangeCloseExecution? tabRangeClose = null,
        BrowserClosedTabRestoreExecution? restoredTab = null,
        BrowserQuickAccessNavigationExecution? quickAccess = null,
        BrowserPathEntryNavigationResult? pathEntry = null,
        CategoryRenameApplicationResult? categoryRename = null,
        BrowserCategoryRemovalExecution? categoryDelete = null,
        BrowserTabCloseConfirmationExecution? tabCloseConfirmation = null,
        BrowserTabHistoryNavigationResult? tabHistoryNavigation = null,
        bool refreshPreview = false,
        string? categoryId = null,
        Guid? contextBrowserTabId = null)
    {
        return new CommandApplicationResult(
            true,
            new CommandApplicationEffect(
                kind,
                intValue,
                textValue,
                messageValue,
                boolValue,
                shellKind,
                selection,
                directoryNavigation,
                historyNavigation,
                manualRefresh,
                cursorNavigation,
                bulkMarks,
                tabCreation,
                tabCreationBatch,
                tabBatchHistory,
                tabSwitch,
                categorySwitch,
                categoryAdd,
                categoryReorder,
                tabLock,
                tabReadOnly,
                unifiedFilter,
                unifiedSearch,
                tabClose,
                tabRangeClose,
                restoredTab,
                quickAccess,
                pathEntry,
                categoryRename,
                categoryDelete,
                tabCloseConfirmation,
                tabHistoryNavigation,
                refreshPreview,
                categoryId,
                contextBrowserTabId));
    }

    private bool TryResolveTabTarget(CommandExecutionContext context, out int tabIndex)
    {
        tabIndex = context.ContextTabIndex ?? _browser.Workspace.ActiveTabIndex;
        return tabIndex >= 0 && tabIndex < _browser.Workspace.TabCount;
    }
}

internal enum CommandApplicationEffectKind
{
    NoOp,
    CursorNoOp,
    ApplyDirectoryNavigation,
    ApplyHistoryNavigation,
    ApplyTabHistoryNavigation,
    ApplyManualRefresh,
    ApplyCursorNavigation,
    BeginNamePrefixJump,
    ApplyBulkMarks,
    ApplyTabCreation,
    ApplyTabCreationBatch,
    ApplyTabBatchHistory,
    ApplyTabSwitch,
    ApplyCategorySwitch,
    ApplyCategoryAdd,
    ApplyCategoryReorder,
    ApplyTabLayout,
    CreateBrowserTabGroup,
    AddBrowserTabGroupMember,
    RemoveBrowserTabGroupMember,
    RenameBrowserTabGroup,
    ViewerCommandCompleted,
    LaunchExternalMediaPlayback,
    ConfirmExecuteTarget,
    OpenMediaViewer,
    OpenArchiveFallback,
    FileOperationStarted,
    ApplyDefaultOpen,
    OpenWithDialog,
    ApplyTabLock,
    ApplyTabReadOnly,
    ApplyUnifiedFilter,
    RunUnifiedFilterFind,
    ApplyTabClose,
    ApplyTabRangeClose,
    ApplyRestoredTab,
    ApplyQuickAccess,
    ApplyLogdisk,
    ApplyCategoryRename,
    ApplyCategoryDelete,
    ApplyTabCloseConfirmation,
    ShowMessage,
    OpenCommandDialog,
    OpenPathEntry,
    OpenExplorer,
    RevealInExplorer,
    OpenTerminal,
    OpenExternalEditor,
    CopyFullPaths,
    CopyCurrentPath,
    ShowHelp,
    OpenMarkSlot,
    ManageTabCategories,
    Properties,
    ShowSystemInformation,
    OpenNewInstance,
    OpenControlPanel,
    OpenSettings,
    OpenCommandLauncher,
    ShowCommandList,
    OpenManagedTrash
}

internal abstract record CommandApplicationInteractionRequest;

internal sealed record SortCommandInteractionRequest(
    SortKind CurrentSort,
    bool CurrentAscending) : CommandApplicationInteractionRequest;

internal sealed record UnifiedFilterFindCommandInteractionRequest(
    int TargetTabIndex,
    string RootPath,
    UnifiedFilterFindCriteria CurrentCriteria,
    UnifiedFilterFindMode InitialMode) : CommandApplicationInteractionRequest;

internal sealed record TreeCommandInteractionRequest(
    string CurrentPath) : CommandApplicationInteractionRequest;

internal sealed record QuickAccessCommandInteractionRequest(
    string CurrentPath,
    QuickAccessStore Store,
    IReadOnlyList<QuickAccessEntry> HistoryEntries) : CommandApplicationInteractionRequest;

internal abstract record BrowserDerivedTabNavigationContinuation;

internal sealed record BrowserDirectoryNavigationContinuation(
    BrowserNavigationPreparation Preparation,
    bool ClearPreview,
    bool RefreshPreview,
    bool RecordDirectoryMoveHistory) : BrowserDerivedTabNavigationContinuation;

internal sealed record BrowserHistoryNavigationContinuation(
    BrowserHistoryNavigationStart Start) : BrowserDerivedTabNavigationContinuation;

internal sealed record BrowserQuickAccessNavigationContinuation(
    BrowserQuickAccessNavigationPreparation Preparation) : BrowserDerivedTabNavigationContinuation;

internal sealed record BrowserLogdiskNavigationContinuation(
    BrowserNavigationPreparation Preparation,
    BrowserPathEntryNavigationResult PathEntry) : BrowserDerivedTabNavigationContinuation;

internal sealed record BrowserDerivedTabNavigationInteractionRequest(
    BrowserDerivedTabNavigationContinuation Continuation) : CommandApplicationInteractionRequest;

internal sealed record LogdiskCommandInteractionRequest(
    string DefaultPath,
    IReadOnlyList<string> Candidates) : CommandApplicationInteractionRequest;

internal sealed record ArchiveListCommandInteractionRequest(
    string ArchivePath,
    ArchiveListResult Contents,
    string CurrentPath,
    bool IsReadOnly,
    string? DateFormat,
    string? SizeFormat) : CommandApplicationInteractionRequest;

internal sealed record CategoryRenameCommandInteractionRequest(
    string CategoryId,
    string DisplayName) : CommandApplicationInteractionRequest;

internal sealed record CategoryDeleteCommandInteractionRequest(
    string CategoryId,
    string DisplayName) : CommandApplicationInteractionRequest;

internal sealed record TabCloseCategoryRemovalInteractionRequest(
    int TabIndex,
    string CategoryId,
    string DisplayName) : CommandApplicationInteractionRequest;

internal sealed record BrowserTabVisitHistoryCommandInteractionRequest(
    IReadOnlyList<BrowserTabVisitHistoryListItem> Items) : CommandApplicationInteractionRequest;

internal abstract record CommandApplicationInteractionResponse;

internal sealed record SortCommandInteractionResponse(
    bool Accepted,
    SortKind SortKind,
    bool Ascending) : CommandApplicationInteractionResponse;

internal sealed record UnifiedFilterFindCommandInteractionResponse(
    bool Accepted,
    UnifiedFilterFindMode Mode,
    UnifiedFilterFindCriteria Criteria) : CommandApplicationInteractionResponse;

internal sealed record TreeCommandInteractionResponse(
    string? SelectedPath) : CommandApplicationInteractionResponse;

internal enum CommandQuickAccessAction
{
    Cancel,
    SaveOnly,
    Navigate
}

internal sealed record QuickAccessCommandInteractionResponse(
    CommandQuickAccessAction Action,
    QuickAccessEntry? SelectedEntry,
    QuickAccessStore? UpdatedStore) : CommandApplicationInteractionResponse;

internal sealed record BrowserDerivedTabNavigationInteractionResponse(
    bool Accepted) : CommandApplicationInteractionResponse;

internal sealed record LogdiskCommandInteractionResponse(
    string? SelectedPath) : CommandApplicationInteractionResponse;

internal sealed record ArchiveListCommandInteractionResponse(
    ArchiveExtractRequest? PendingExtractRequest) : CommandApplicationInteractionResponse;

internal sealed record CategoryRenameCommandInteractionResponse(
    bool Accepted,
    string? DisplayName) : CommandApplicationInteractionResponse;

internal sealed record CategoryDeleteCommandInteractionResponse(
    bool Accepted) : CommandApplicationInteractionResponse;

internal sealed record TabCloseCategoryRemovalInteractionResponse(
    bool Accepted) : CommandApplicationInteractionResponse;

internal sealed record BrowserTabVisitHistoryCommandInteractionResponse(
    bool Accepted,
    BrowserTabVisitHistoryListDirection Direction,
    int Depth) : CommandApplicationInteractionResponse;

internal readonly record struct CategoryRenameApplicationResult(
    string CategoryId,
    bool Applied,
    bool DuplicateName);

internal sealed record CommandApplicationEffect(
    CommandApplicationEffectKind Kind,
    int IntValue = 0,
    string? TextValue = null,
    string? MessageValue = null,
    bool BoolValue = false,
    ShellKind? ShellKind = null,
    SelectionResult? Selection = null,
    BrowserDirectoryNavigationExecution? DirectoryNavigation = null,
    BrowserHistoryNavigationExecution? HistoryNavigation = null,
    BrowserManualRefreshExecution? ManualRefresh = null,
    BrowserCursorNavigationExecution? CursorNavigation = null,
    BrowserBulkMarkTransition? BulkMarks = null,
    BrowserTabCreationExecution? TabCreation = null,
    BrowserTabCreationBatchExecution? TabCreationBatch = null,
    BrowserTabBatchHistoryExecution? TabBatchHistory = null,
    BrowserTabSwitchWorkflowResult? TabSwitch = null,
    BrowserCategorySwitchWorkflowResult? CategorySwitch = null,
    BrowserCategoryAddSwitchExecution? CategoryAdd = null,
    BrowserCategoryReorderTransition? CategoryReorder = null,
    BrowserTabToggleLockExecution? TabLock = null,
    BrowserTabToggleReadOnlyExecution? TabReadOnly = null,
    BrowserUnifiedFilterExecution? UnifiedFilter = null,
    UnifiedFilterFindInteractionRequest? UnifiedSearch = null,
    BrowserTabCloseExecution? TabClose = null,
    BrowserTabRangeCloseExecution? TabRangeClose = null,
    BrowserClosedTabRestoreExecution? RestoredTab = null,
    BrowserQuickAccessNavigationExecution? QuickAccess = null,
    BrowserPathEntryNavigationResult? PathEntry = null,
    CategoryRenameApplicationResult? CategoryRename = null,
    BrowserCategoryRemovalExecution? CategoryDelete = null,
    BrowserTabCloseConfirmationExecution? TabCloseConfirmation = null,
    BrowserTabHistoryNavigationResult? TabHistoryNavigation = null,
    bool RefreshPreview = false,
    string? CategoryId = null,
    Guid? ContextBrowserTabId = null);

internal interface ICommandApplicationShellPort
{
    CommandApplicationShellState CaptureState();
    IReadOnlyList<string> GetBulkMarkTargetPaths(bool includeDirectories);
    IReadOnlyList<string> GetSharedLocationCandidates();
    bool ConfirmMultiDirectoryOpen(int directoryCount, int threshold);
}

internal sealed record CommandApplicationShellState(
    bool IsBrowserMode,
    bool IsBusy,
    string CurrentPath,
    BrowserTabState CurrentTab,
    BrowserDirectoryLoadOptions DirectoryLoadOptions,
    int ColumnCount,
    BrowserRefreshShellState RefreshShellState,
    BrowserTabLayoutMode TabLayoutMode,
    int ItemsPerPage,
    int BrowserItemCount,
    int TotalItemCount,
    string? CurrentItemName,
    string? CurrentItemPath,
    string? SevenZipPath,
    bool IsReadOnlyBrowserTab,
    string? DateFormat,
    string? SizeFormat,
    int MultiDirectoryOpenConfirmationThreshold);

internal sealed record CommandApplicationResult(
    bool Handled,
    CommandApplicationEffect? Effect = null,
    CommandApplicationInteractionRequest? InteractionRequest = null)
{
    public static CommandApplicationResult NotHandled { get; } = new(false);

    public static implicit operator CommandApplicationResult(bool handled) => new(handled);
}
