using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

internal interface ICommandApplicationBackgroundResultPort
{
    void QueueBackgroundResult(CommandApplicationResult result);
}

internal interface ICommandApplicationBusyStatePort
{
    bool IsFileOperationBusy { get; }
}

internal interface ICommandApplicationExecutionPort
{
    CommandApplicationResult TryExecute(string commandId, CommandExecutionContext context);
    CommandApplicationResult Continue(
        CommandApplicationInteractionRequest request,
        CommandApplicationInteractionResponse response);
}

/// <summary>
/// 登録済みCommandの文脈判定とapplication workflowへのroutingを担当する。
/// UI event handlerやMainFormの具体型は参照しない。
/// </summary>
internal sealed class CommandApplicationCoordinator : ICommandApplicationExecutionPort
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
    private readonly BrowserMarkSlotWorkflowApplicationCoordinator? _markSlots;
    private readonly IFileOperationUiPort _fileOperationUi;
    private readonly ICommandApplicationShellPort _shell;
    private readonly BrowserWorkspaceSnapshotApplicationCoordinator? _workspaceSnapshots;
    private readonly IBrowserWorkspaceSnapshotUiPort? _workspaceSnapshotUi;
    private readonly ISettingsApplicationPort? _settings;
    private readonly ExternalToolDefinitionsApplicationCoordinator _externalToolDefinitions;
    private readonly IExternalShellCommandExecutor _shellCommandExecutor;
    private readonly ICommandApplicationBackgroundResultPort? _backgroundResultPort;
    private readonly ICommandApplicationBusyStatePort? _busyStatePort;
    private readonly UnifiedSearchResultActivationApplicationCoordinator? _unifiedSearchResultActivation;
    private readonly BrowserDragOutApplicationCoordinator _browserDragOut;
    private readonly ArchiveListNavigationApplicationCoordinator _archiveListNavigation = new();

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
        ICommandApplicationShellPort shell,
        BrowserMarkSlotWorkflowApplicationCoordinator? markSlots = null,
        BrowserWorkspaceSnapshotApplicationCoordinator? workspaceSnapshots = null,
        IBrowserWorkspaceSnapshotUiPort? workspaceSnapshotUi = null,
        ISettingsApplicationPort? settings = null,
        ICommandApplicationBackgroundResultPort? backgroundResultPort = null,
        ExternalToolDefinitionsApplicationCoordinator? externalToolDefinitions = null,
        IExternalShellCommandExecutor? shellCommandExecutor = null,
        ICommandApplicationBusyStatePort? busyStatePort = null,
        IUnifiedSearchResultActivationUiPort? unifiedSearchResultActivationUi = null,
        BrowserDragOutApplicationCoordinator? browserDragOut = null)
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
        _markSlots = markSlots;
        _fileOperationUi = fileOperationUi;
        _shell = shell;
        _workspaceSnapshots = workspaceSnapshots;
        _workspaceSnapshotUi = workspaceSnapshotUi;
        _settings = settings;
        _backgroundResultPort = backgroundResultPort;
        _externalToolDefinitions = externalToolDefinitions ?? new ExternalToolDefinitionsApplicationCoordinator();
        _shellCommandExecutor = shellCommandExecutor ?? new ExternalShellCommandExecutor();
        _busyStatePort = busyStatePort;
        _unifiedSearchResultActivation = unifiedSearchResultActivationUi == null
            ? null
            : new UnifiedSearchResultActivationApplicationCoordinator(browser, persistence, shell, unifiedSearchResultActivationUi);
        _browserDragOut = browserDragOut ?? new BrowserDragOutApplicationCoordinator();
    }

    public void RegisterUnifiedSearchSession(Guid sessionId, string sourceCategoryId, Guid sourceTabId, string rootPath, UnifiedFilterFindMode mode) =>
        _unifiedSearchResultActivation?.RegisterSession(sessionId, sourceCategoryId, sourceTabId, rootPath, mode);

    public void ClearUnifiedSearchSession(Guid sessionId) =>
        _unifiedSearchResultActivation?.UnregisterSession(sessionId);

    public void SetUnifiedSearchSessionActive(Guid sessionId, bool active) =>
        _unifiedSearchResultActivation?.SetSessionActive(sessionId, active);

    public IReadOnlyList<BrowserTabNavigationSearchDerivedTab> GetUnifiedSearchDerivedTabs() =>
        _unifiedSearchResultActivation?.GetDerivedTabs() ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>();

    public IReadOnlySet<Guid> GetBlockedUnifiedSearchResultTabIds(string categoryId) =>
        _unifiedSearchResultActivation?.GetBlockedResultTabIds(categoryId) ?? new HashSet<Guid>();

    public bool IsUnifiedSearchResultTabBlocked(string categoryId, Guid tabId) =>
        _unifiedSearchResultActivation?.IsResultTabBlocked(categoryId, tabId) == true;

    public void ClearUnifiedSearchActivationReturnContexts(Guid? sessionId = null) =>
        _unifiedSearchResultActivation?.ClearReturnContexts(sessionId);

    public void ClearUnifiedSearchPreviewReturnContext(Guid? sessionId = null) =>
        _unifiedSearchResultActivation?.ClearPreviewReturnContext(sessionId);

    public void ClearUnifiedSearchNameInspectionReturnContext(Guid? sessionId = null) =>
        _unifiedSearchResultActivation?.ClearNameInspectionReturnContext(sessionId);

    public void ClearUnifiedSearchNameInspectionReturnUnlessSource(Guid targetTabId, string targetCategoryId) =>
        _unifiedSearchResultActivation?.ClearNameInspectionReturnContextUnlessSource(targetTabId, targetCategoryId);

    public bool ApplyUnifiedSearchPreviewReturn(int activeViewerRequestId, string? activeCategoryId, Guid? activeTabId) =>
        _unifiedSearchResultActivation?.ApplyPreviewReturn(activeViewerRequestId, activeCategoryId, activeTabId) == true;

    public bool ApplyUnifiedSearchNameInspectionReturn(string? activeCategoryId, Guid? activeTabId) =>
        _unifiedSearchResultActivation?.ApplyNameInspectionReturn(activeCategoryId, activeTabId) == true;

    public bool IsBackgroundEffectCurrent(CommandApplicationEffect effect) =>
        effect.ViewerSearchOrigin is not { } origin ||
        (!origin.Token.IsCancellationRequested && _viewer.CurrentRequestId == origin.ViewerRequestId &&
         _viewerWorkflow.IsLargeFileSearchRequestActive(origin.State, origin.SearchRequestId));

    public CommandApplicationResult TryExecute(string commandId, CommandExecutionContext context)
    {
        CommandBusyBehavior busyBehavior = CommandBusyPolicy.GetBehavior(commandId, context);
        bool hasBusyState = TryGetFileOperationBusy(out bool busy);
        if (CommandBusyPolicy.RequiresBusyState(busyBehavior) && !hasBusyState)
        {
            return CommandApplicationResult.NotHandled;
        }
        if (busyBehavior == CommandBusyBehavior.Block && busy)
        {
            return CommandApplicationResult.NotHandled;
        }
        if ((busyBehavior is CommandBusyBehavior.DelegateToFileOperation or CommandBusyBehavior.Cancel)
            && _fileOperations is null)
        {
            return CommandApplicationResult.NotHandled;
        }

        if (context.Scope == CommandScope.ImageViewer)
        {
            if (commandId == CommandIds.ImageViewerTogglePresentationMode)
            {
                return ExecuteToggleMediaViewerPresentationMode(context);
            }
            return ExecuteImageViewerCommand(commandId, context);
        }
        if (context.Scope == CommandScope.TextViewer)
        {
            return ExecuteTextViewerCommand(commandId, context);
        }
        if (commandId == CommandIds.AppOpenExternalUrl && context.Scope == CommandScope.Global)
        {
            return ExecuteOpenExternalUrl(context.ExternalUrlOpen);
        }
        if (commandId == CommandIds.ArchiveHashResultCopy && context.Scope == CommandScope.Global)
        {
            return ExecuteHashResultCopy(context.HashResultCopy);
        }
        if (commandId == CommandIds.AppCommandPaletteFavoriteToggle && context.Scope == CommandScope.Global)
        {
            return ExecuteCommandPaletteFavoriteToggle(context.CommandPaletteFavoriteToggle);
        }
        if (commandId == CommandIds.AppExternalToolDefinitionsSave && context.Scope == CommandScope.Global)
        {
            return ExecuteExternalToolDefinitionsSave(context);
        }
        if (commandId == CommandIds.AppSettingsSave && context.Scope == CommandScope.Global)
        {
            return ExecuteSettingsSave(context.SettingsSave);
        }

        bool browser = _viewer.Mode == ViewerApplicationMode.Browser;

        if (CommandIds.TryGetBrowserDriveNumber(commandId, out int driveNumber))
        {
            return ExecuteBrowserDrive(browser, (char)('A' + driveNumber - 1));
        }
        if (CommandIds.TryGetBrowserColumnCount(commandId, out int requestedColumnCount))
        {
            return ExecuteBrowserColumnCount(browser, requestedColumnCount, context.BrowserView);
        }

        switch (commandId)
        {
            case CommandIds.ImageViewerOpen:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerVideoSaveFrame:
            case CommandIds.ImageViewerVideoSetPlaybackRate:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerSaveAs:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerClose:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerCopy:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerPaste:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerPasteSelectionStretch:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerPasteSelectionOriginal:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerCopySvg:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerQuantize:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerReset:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerRotateRight:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerRotateLeft:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerFlipHorizontal:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerFlipVertical:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerShowInfo:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerUndo:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerRedo:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerResize:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerMosaic:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerBlur:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.ImageViewerTone:
                return ExecuteImageViewerCommand(commandId, context);
            case CommandIds.BrowserNavigateParent:
                return ExecuteParentNavigation(browser);
            case CommandIds.BrowserNavigateDriveRoot:
                return ExecuteDriveRootNavigation(browser && !busy);
            case CommandIds.BrowserNavigateBack:
                return ExecuteHistoryNavigation(browser, BrowserHistoryDirection.Back);
            case CommandIds.BrowserNavigateForward:
                return ExecuteHistoryNavigation(browser, BrowserHistoryDirection.Forward);
            case CommandIds.BrowserNavigateToPath:
                return ExecuteTargetPathNavigation(browser, context);
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
            case CommandIds.BrowserCursorMove:
                return ExecuteBrowserCursorMove(browser, context.BrowserCursorNavigation);
            case CommandIds.BrowserMarkToggle:
                return ExecuteBrowserMarkToggle(browser, context.BrowserMarkToggle);
            case CommandIds.BrowserMarkRangeAdd:
                return ExecuteBrowserMarkRangeAdd(browser && !busy, context.BrowserMarkRangeAdd);
            case CommandIds.BrowserMarkInvert:
                return ExecuteBrowserMarkInvert(browser, context.BrowserBulkMark);
            case CommandIds.BrowserMarkClear:
                return ExecuteBrowserMarkClear(browser && !busy, context.BrowserMarkClear);
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
            case CommandIds.BrowserOpenPlaybackLocked:
                return ExecutePlaybackLockedOpen(browser && !busy, context.SelectionSnapshot);
            case CommandIds.BrowserDefaultOpenMarked:
                return ExecuteDefaultOpenMarked(browser, context.SelectionSnapshot);
            case CommandIds.BrowserOpenWith:
                return ExecuteOpenWith(browser, context.SelectionSnapshot);
            case CommandIds.BrowserAudioCtrlEnter:
                return ExecuteBrowserAudioCtrlEnter(browser, context.ContextTargetPath);
            case CommandIds.BrowserSendTo:
                return !browser || context.SelectionSnapshot is not { Count: > 0 }
                    || string.IsNullOrWhiteSpace(context.ContextTargetPath)
                        ? CommandApplicationResult.NotHandled
                        : Effect(
                            CommandApplicationEffectKind.LaunchSendTo,
                            textValue: context.ContextTargetPath,
                            selection: context.SelectionSnapshot);
            case CommandIds.BrowserDragOut:
                return ExecuteBrowserDragOut(browser, context.BrowserDragOut);
            case CommandIds.BrowserOpenWithDiff:
                if (!browser)
                {
                    return CommandApplicationResult.NotHandled;
                }
                return context.SelectionSnapshot is { Count: 2 } diffSelection
                    ? Effect(CommandApplicationEffectKind.LaunchExternalDiff, selection: diffSelection)
                    : Effect(CommandApplicationEffectKind.ShowMessage, textValue: "外部Diffはちょうど 2 件選択時のみ使えます。");
            case CommandIds.BrowserLaunchConfiguredExternalTool:
                return browser && context.ExternalToolLaunch is { } externalToolLaunch
                    ? Effect(CommandApplicationEffectKind.LaunchConfiguredExternalTool, externalToolLaunch: externalToolLaunch)
                    : CommandApplicationResult.NotHandled;
            case CommandIds.BrowserOpenCommandDialog:
                return context.BrowserCommandExecution is { } commandExecution
                    ? ExecuteBrowserCommandExecution(browser, commandExecution)
                    : EffectIf(browser, CommandApplicationEffectKind.OpenCommandDialog);
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
            case CommandIds.BrowserViewDisplayModeSet:
                return browser && context.BrowserView?.DisplayMode is { } displayMode
                    ? ExecuteBrowserDisplayModeSet(displayMode)
                    : CommandApplicationResult.NotHandled;
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
                return context.SortKind is { } requestedSort
                    ? ExecuteSortContinuation(requestedSort, context.SortAscending ?? _browser.SortAscending)
                    : BeginSortInteraction(browser);
            case CommandIds.BrowserFilter:
                return BeginFilterFindInteraction(browser, context, UnifiedFilterFindMode.FilterCurrentTab);
            case CommandIds.BrowserSearch:
                return BeginFilterFindInteraction(browser, context, UnifiedFilterFindMode.FindNamesRecursively);
            case CommandIds.BrowserSearchCloseResults:
                return ExecuteUnifiedSearchSessionClose(browser, context.UnifiedSearchSession);
            case CommandIds.BrowserSearchResultsFilter:
                return ExecuteUnifiedSearchResultsFilter(browser, context.UnifiedSearchResultsFilter);
            case CommandIds.BrowserSearchExportResults:
                return ExecuteUnifiedSearchResultsExport(browser, context.UnifiedSearchResultsExport);
            case CommandIds.BrowserSearchResultsActivate:
            case CommandIds.BrowserSearchResultsActivateInNewTab:
                return ExecuteUnifiedSearchResultActivation(commandId, browser, context.UnifiedSearchResultActivation);
            case CommandIds.BrowserSearchResultsOpenHitInEditor:
                return ExecuteUnifiedSearchHitEditor(browser, context.UnifiedSearchHitEditor);
            case CommandIds.BrowserFilterClear:
                return ExecuteClearFilter(browser, context);
            case CommandIds.BrowserTree:
                return BeginTreeInteraction(browser);
            case CommandIds.BrowserQuickAccess:
                return BeginQuickAccessInteraction(browser);
            case CommandIds.BrowserQuickAccessActivate:
                return ExecuteQuickAccessEntryActivation(browser, context.BrowserQuickAccessEntry);
            case CommandIds.BrowserQuickAccessRegister:
                return BeginQuickAccessRegistration(browser, context.ContextTargetPath);
            case CommandIds.BrowserQuickAccessEntryAdd:
            case CommandIds.BrowserQuickAccessEntryUpdate:
            case CommandIds.BrowserQuickAccessEntryDelete:
            case CommandIds.BrowserQuickAccessReorder:
                return ExecuteQuickAccessDraftMutation(browser, commandId, context.BrowserQuickAccessDraft);
            case CommandIds.BrowserLogdisk:
                return BeginLogdiskInteraction(browser);
            case CommandIds.ArchivePack:
                if (!browser) return CommandApplicationResult.NotHandled;
                if (context.QuickPackExistingArchive)
                {
                    return new CommandApplicationResult(context.SelectionSnapshot is { Count: > 0 } quickSelection &&
                        _fileOperations.TryStartQuickPack(quickSelection, _fileOperationUi));
                }
                return TryStartFileOperation(
                    new FileOperationCommandRequest(
                        FileOperationCommandKind.Pack,
                        context.SelectionSnapshot,
                        ForcePackEachFolderIndividually: context.ForcePackEachFolderIndividually),
                    _fileOperationUi);
            case CommandIds.ArchiveUnpack:
                if (!browser) return CommandApplicationResult.NotHandled;
                if (context.ArchiveExtract is { } extractRequest)
                {
                    return new CommandApplicationResult(_fileOperations.TryStartArchiveExtract(extractRequest, _fileOperationUi));
                }
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Unpack, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.ArchiveOpenInSevenZipFileManager:
                return ExecuteOpenArchiveInSevenZipFileManager(browser, context.ContextTargetPath);
            case CommandIds.ArchiveHash:
                FileOperationCommandRequest? hashRequest = CreateArchiveHashRequest(context);
                if (!browser || hashRequest is null)
                {
                    return CommandApplicationResult.NotHandled;
                }
                return TryStartFileOperation(hashRequest.Value, _fileOperationUi);
            case CommandIds.ArchiveContentsOpen:
                return !browser || string.IsNullOrWhiteSpace(context.ContextTargetPath)
                    ? CommandApplicationResult.NotHandled
                    : BeginArchiveListInteraction(context.ContextTargetPath);
            case CommandIds.ArchiveContentsNavigate:
                return !browser
                    ? CommandApplicationResult.NotHandled
                    : ExecuteArchiveListNavigation(context.ArchiveListNavigation);
            case CommandIds.ArchiveContentsPreviewText:
                return context.Scope != CommandScope.ArchiveContents
                    ? CommandApplicationResult.NotHandled
                    : ExecuteArchiveTextPreview(context.ArchiveTextPreview);
            case CommandIds.BrowserClipboardCopy:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(CreateClipboardCopyRequest(context), _fileOperationUi);
            case CommandIds.BrowserCopyFullPath:
                return EffectIf(browser, CommandApplicationEffectKind.CopyFullPaths, selection: context.SelectionSnapshot);
            case CommandIds.BrowserCopyCurrentPath:
                return EffectIf(browser && !string.IsNullOrWhiteSpace(_browser.CurrentPath), CommandApplicationEffectKind.CopyCurrentPath);
            case CommandIds.BrowserCopyItemName:
                return EffectIf(browser && !string.IsNullOrWhiteSpace(context.HeaderCopy?.CurrentItemName),
                    CommandApplicationEffectKind.CopyHeaderItemName,
                    textValue: context.HeaderCopy?.CurrentItemName);
            case CommandIds.BrowserShowHelp:
                return Effect(CommandApplicationEffectKind.ShowHelp);
            case CommandIds.BrowserOpenMarkSlot:
                return EffectIf(browser, CommandApplicationEffectKind.OpenMarkSlot);
            case CommandIds.BrowserMarkSlotRestore:
                if (!browser || busy || _markSlots == null || context.MarkSlotNumber is not int slotNumber || slotNumber < 1)
                {
                    return CommandApplicationResult.NotHandled;
                }
                MarkSlotActionResult markRestore = _markSlots.RestoreToCurrentTab(slotNumber, _shell.CaptureState().CurrentTab);
                return Effect(CommandApplicationEffectKind.ApplyMarkSlotRestore,
                    textValue: markRestore.Message,
                    boolValue: markRestore.Success);
            case CommandIds.BrowserMarkSlotSave:
                return ExecuteMarkSlotSave(browser && !busy, context.BrowserMarkSlotSave);
            case CommandIds.BrowserMarkSlotRename:
                return ExecuteMarkSlotRename(browser && !busy, context.BrowserMarkSlotMutation);
            case CommandIds.BrowserMarkSlotDelete:
                return ExecuteMarkSlotDelete(browser && !busy, context.BrowserMarkSlotMutation);
            case CommandIds.BrowserMarkSlotRemoveItems:
                return ExecuteMarkSlotRemoveItems(browser && !busy, context.BrowserMarkSlotMutation);
            case CommandIds.BrowserMarkSlotExport:
                return ExecuteMarkSlotExport(browser, context.BrowserMarkSlotTransfer);
            case CommandIds.BrowserMarkSlotImport:
                return BeginMarkSlotImport(browser, commandId, context.BrowserMarkSlotImport);
            case CommandIds.BrowserMarkSlotsExportAll:
                return ExecuteMarkSlotsExportAll(browser, context.BrowserMarkSlotTransfer);
            case CommandIds.BrowserMarkSlotsImportAll:
                return BeginMarkSlotsImportAll(browser, commandId, context.BrowserMarkSlotImport);
            case CommandIds.BrowserMarkSlotClipboardImport:
                return ExecuteMarkSlotClipboardImport(browser, busy, context.BrowserMarkSlotClipboardImport);
            case CommandIds.BrowserMarkSlotSetOperationPreview:
                return ExecuteMarkSlotSetOperationPreview(browser, context.BrowserMarkSlotSetOperationPreview);
            case CommandIds.BrowserMarkSlotSetOperationApply:
                return ExecuteMarkSlotSetOperationApply(browser && !busy, context.BrowserMarkSlotSetOperationApply);
            case CommandIds.BrowserMarkSlotSetOperationSave:
                return ExecuteMarkSlotSetOperationSave(browser, context.BrowserMarkSlotSetOperationSave);
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
                return context.BrowserTabNavigation?.IncludeUnifiedSearchResults == true
                    ? ExecuteAdjacentTabWithinUnifiedSearch(browser, +1, context.BrowserTabNavigation)
                    : ExecuteAdjacentTab(browser, +1);
            case CommandIds.BrowserTabPrevious:
                return context.BrowserTabNavigation?.IncludeUnifiedSearchResults == true
                    ? ExecuteAdjacentTabWithinUnifiedSearch(browser, -1, context.BrowserTabNavigation)
                    : ExecuteAdjacentTab(browser, -1);
            case CommandIds.BrowserTabActivate:
                return context.BrowserTabNavigation is { IncludeUnifiedSearchResults: true } searchActivation
                    ? ExecuteUnifiedSearchSurfaceActivation(browser, context, searchActivation)
                    : ExecuteTabActivate(browser, context);
            case CommandIds.BrowserTabReorder:
                return ExecuteReorderTab(browser, context);
            case CommandIds.BrowserTabTreeDrop:
                return ExecuteBrowserTabTreeDrop(browser, context);
            case CommandIds.BrowserTabCategoryActivate:
                return ExecuteCategoryActivate(browser, context.CategoryId);
            case CommandIds.BrowserTabCategoryReorder:
                return ExecuteReorderCategoryByTarget(browser, context);
            case CommandIds.BrowserTabLayoutToggle:
                return ExecuteToggleTabLayout(browser && context.Scope == CommandScope.Browser);
            case CommandIds.BrowserTabLayoutSet:
                return ExecuteSetTabLayout(browser && context.Scope == CommandScope.Browser, context.BrowserTabLayout);
            case CommandIds.BrowserTabNavigationWidthSet:
                if (!browser || context.BrowserTabNavigationWidth is not { } navigationWidth || _persistence is null)
                {
                    return CommandApplicationResult.NotHandled;
                }
                _persistence.SetBrowserTabNavigationWidth(navigationWidth.Width);
                return new CommandApplicationResult(true);
            case CommandIds.BrowserTabReadOnlyToggle:
                return ExecuteToggleReadOnly(browser, context);
            case CommandIds.BrowserTabGroupCreate:
                return BeginBrowserTabGroupCreateInteraction(browser, context);
            case CommandIds.BrowserTabGroupAdd:
                return BeginOrExecuteBrowserTabGroupAdd(browser, context);
            case CommandIds.BrowserTabGroupRemove:
                return ExecuteBrowserTabGroupRemove(browser, context);
            case CommandIds.BrowserTabGroupRename:
                return BeginBrowserTabGroupRenameInteraction(browser, context);
            case CommandIds.BrowserTabGroupExpandedSet:
                return browser && context.BrowserTabGroupExpanded is { } groupExpanded &&
                    _persistence.SetBrowserTabGroupExpanded(groupExpanded.GroupId, groupExpanded.Expanded)
                    ? Effect(CommandApplicationEffectKind.NoOp)
                    : CommandApplicationResult.NotHandled;
            case CommandIds.BrowserTabCategoryManage:
                return EffectIf(browser, CommandApplicationEffectKind.ManageTabCategories);
            case CommandIds.BrowserTabCategoryAdd:
                return ExecuteAddCategory(browser, context.CategoryDisplayName);
            case CommandIds.BrowserTabCategoryRename:
                return BeginCategoryRenameInteraction(browser, context.CategoryId);
            case CommandIds.BrowserTabCategoryDelete:
                return ExecuteCategoryDelete(browser, context);
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
                FileOperationCommandContext? copyContext = context.FileOperation;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(
                        FileOperationCommandKind.Copy,
                        context.SelectionSnapshot,
                        DestinationDirectory: copyContext?.DestinationDirectory,
                        CompletionCallback: copyContext?.CompletionCallback,
                        RecordDragCopyUndo: copyContext?.RecordDragCopyUndo == true),
                    _fileOperationUi);
            case CommandIds.FileImportExternalDrop:
                if (!browser || context.BrowserExternalDropImport is not { } externalDrop)
                {
                    return CommandApplicationResult.NotHandled;
                }
                if (!externalDrop.ProcessAsync)
                {
                    return new CommandApplicationResult(
                        _fileOperations.TryProcessExternalDropInline(externalDrop, _fileOperationUi));
                }
                return TryStartFileOperation(
                    new FileOperationCommandRequest(
                        FileOperationCommandKind.ExternalDropImport,
                        CompletionCallback: context.FileOperation?.CompletionCallback,
                        ExternalDropImport: externalDrop),
                    _fileOperationUi);
            case CommandIds.FileMove:
                if (!browser) return CommandApplicationResult.NotHandled;
                FileOperationCommandContext? moveContext = context.FileOperation;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(
                        FileOperationCommandKind.Move,
                        context.SelectionSnapshot,
                        DestinationDirectory: moveContext?.DestinationDirectory,
                        CompletionCallback: moveContext?.CompletionCallback),
                    _fileOperationUi);
            case CommandIds.FileRename:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Rename, context.SelectionSnapshot),
                    _fileOperationUi);
            case CommandIds.FileDelete:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Delete, context.SelectionSnapshot, PermanentDelete: false),
                    _fileOperationUi);
            case CommandIds.FileDeletePermanent:
                if (!browser) return CommandApplicationResult.NotHandled;
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.Delete, context.SelectionSnapshot, PermanentDelete: true),
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
            case CommandIds.AppHideBrowserToolbar:
                if (_settings is null)
                {
                    return CommandApplicationResult.NotHandled;
                }
                SettingsSqliteStore.SettingsSaveResult toolbarSave = _settings.TrySaveShowBrowserToolbar(false);
                if (!toolbarSave.PrimarySaved)
                {
                    return CommandApplicationResult.NotHandled;
                }
                return Effect(CommandApplicationEffectKind.ApplyBrowserToolbarVisibility);
            case CommandIds.BrowserTabLock:
                return ExecuteToggleLock(browser, context);
            case CommandIds.BrowserTabCloseRight:
                return ExecuteCloseTabRange(browser, context, BrowserTabCloseScope.Right);
            case CommandIds.BrowserTabCloseLeft:
                return ExecuteCloseTabRange(browser, context, BrowserTabCloseScope.Left);
            case CommandIds.BrowserTabCloseOther:
                return ExecuteCloseTabRange(browser, context, BrowserTabCloseScope.Other);
            case CommandIds.AppOpenSettings:
                return Effect(
                    CommandApplicationEffectKind.OpenSettings,
                    intValue: (int)(context.SettingsInitialPage ?? SettingsInitialPage.Display));
            case CommandIds.FileOperationCancel:
                return ExecuteFileOperationCancel();
            case CommandIds.AppSettingsImport:
            case CommandIds.AppSettingsExport:
                return ExecuteSettingsTransfer(commandId, context.SettingsTransfer);
            case CommandIds.AppExit:
                return Effect(CommandApplicationEffectKind.CloseApplication);
            case CommandIds.AppOpenCommandLauncher:
                return Effect(CommandApplicationEffectKind.OpenCommandLauncher);
            case CommandIds.AppOpenCommandList:
                return Effect(CommandApplicationEffectKind.ShowCommandList);
            case CommandIds.AppOpenManagedTrash:
                return Effect(CommandApplicationEffectKind.OpenManagedTrash);
            case CommandIds.AppEmptyManagedTrash:
                if (!browser || context.Scope != CommandScope.Global) return CommandApplicationResult.NotHandled;
                if (context.ManagedTrash?.Action == ManagedTrashCommandAction.Empty)
                {
                    return ExecuteManagedTrashOperation(context.ManagedTrash);
                }
                return TryStartFileOperation(
                    new FileOperationCommandRequest(FileOperationCommandKind.EmptyManagedTrash),
                    _fileOperationUi);
            case CommandIds.AppRestoreManagedTrash:
                return ExecuteManagedTrashOperation(context.ManagedTrash, ManagedTrashCommandAction.RestoreSelected);
            case CommandIds.AppDeleteManagedTrashForever:
                return ExecuteManagedTrashOperation(context.ManagedTrash, ManagedTrashCommandAction.DeleteSelectedForever);
            case CommandIds.AppCleanMissingManagedTrash:
                return ExecuteManagedTrashOperation(context.ManagedTrash, ManagedTrashCommandAction.CleanMissing);
            case CommandIds.AppOpenWorkspaceSnapshots:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            case CommandIds.WorkspaceSnapshotSave:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            case CommandIds.WorkspaceSnapshotRestore:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            case CommandIds.WorkspaceSnapshotRename:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            case CommandIds.WorkspaceSnapshotDelete:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            case CommandIds.WorkspaceSnapshotExport:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            case CommandIds.WorkspaceSnapshotImport:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            case CommandIds.WorkspaceSnapshotExportAll:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            case CommandIds.WorkspaceSnapshotImportAll:
                return ExecuteWorkspaceSnapshotCommand(commandId, browser, context.WorkspaceSnapshot);
            default:
                return CommandApplicationResult.NotHandled;
        }
    }

    private CommandApplicationResult ExecuteBrowserCommandExecution(
        bool allowed,
        BrowserCommandExecutionCommandContext request)
    {
        if (!allowed || string.IsNullOrWhiteSpace(request.Command))
        {
            return CommandApplicationResult.NotHandled;
        }

        string? error = _shellCommandExecutor.Execute(request);
        return string.IsNullOrWhiteSpace(error)
            ? true
            : Effect(CommandApplicationEffectKind.ShowMessage, textValue: error);
    }

    private CommandApplicationResult ExecuteExternalToolDefinitionsSave(CommandExecutionContext context)
    {
        return _externalToolDefinitions.Save(context.ExternalToolDefinitionsSave);
    }

    private CommandApplicationResult ExecuteBrowserDrive(bool allowed, char driveLetter)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserPathEntryNavigationResult target = _browser.ResolvePathEntry($"{driveLetter}:");
        if (target.TargetKind != BrowserPathEntryTargetKind.Directory)
        {
            string message = string.IsNullOrWhiteSpace(target.StatusMessage)
                ? $"Drive {driveLetter}: を利用できません。"
                : target.StatusMessage;
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: message);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        return ExecuteDirectoryCommandNavigation(
            _browser.CreateNavigationRequest(target.ResolvedPath),
            state,
            clearPreview: false);
    }

    private CommandApplicationResult ExecuteBrowserDragOut(
        bool browser,
        BrowserDragOutCommandContext? context)
    {
        if (!browser || _fileOperations is null)
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserDragOutPreparation? preparation = _browserDragOut.Prepare(
            context,
            _fileOperations.IsClipboardBusy);
        return preparation is null
            ? Effect(CommandApplicationEffectKind.NoOp)
            : Effect(
                CommandApplicationEffectKind.PrepareBrowserDragOut,
                browserDragOutPreparation: preparation);
    }

    private CommandApplicationResult ExecuteBrowserColumnCount(
        bool allowed,
        int columnCount,
        BrowserViewCommandContext? request)
    {
        if (!allowed || columnCount is < 1 or > 9 ||
            request?.ColumnCount is { } requestedContextCount && requestedContextCount != columnCount)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        if (columnCount == state.ColumnCount)
        {
            int retainedItemsPerPage = _shell.GetBrowserItemsPerPageForColumn(columnCount);
            BrowserColumnCountExecution retainedExecution = _navigation.ExecuteColumnCountChange(
                columnCount,
                retainedItemsPerPage != state.ItemsPerPage,
                state.CurrentTab,
                _shell.CreateBrowserDirectoryLoadOptions(retainedItemsPerPage),
                columnCount,
                state.RefreshShellState);
            return Effect(CommandApplicationEffectKind.ApplyBrowserColumnCount, columnCountChange: retainedExecution);
        }

        int previousItemsPerPage = state.ItemsPerPage;
        _shell.SetBrowserFileDisplayMode(BrowserFileDisplayMode.NameOnly, persistTabState: false, rematerialize: false);
        int nextItemsPerPage = _shell.GetBrowserItemsPerPageForColumn(columnCount);
        BrowserColumnCountExecution execution = _navigation.ExecuteColumnCountChange(
            columnCount,
            nextItemsPerPage != previousItemsPerPage,
            state.CurrentTab,
            _shell.CreateBrowserDirectoryLoadOptions(nextItemsPerPage),
            columnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyBrowserColumnCount, columnCountChange: execution);
    }

    private CommandApplicationResult ExecuteBrowserDisplayModeSet(BrowserFileDisplayMode mode)
    {
        CommandApplicationShellState state = _shell.CaptureState();
        _shell.SetBrowserFileDisplayMode(mode, persistTabState: true, rematerialize: false);
        int itemsPerPage = _shell.GetBrowserItemsPerPageForColumn(state.ColumnCount);
        BrowserColumnCountExecution execution = _navigation.ExecuteColumnCountChange(
            state.ColumnCount,
            itemsPerPage != state.ItemsPerPage,
            state.CurrentTab,
            _shell.CreateBrowserDirectoryLoadOptions(itemsPerPage),
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyBrowserColumnCount, columnCountChange: execution);
    }

    private CommandApplicationResult ExecuteOpenArchiveInSevenZipFileManager(bool allowed, string? archivePath)
    {
        if (!allowed || string.IsNullOrWhiteSpace(archivePath))
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        string? sevenZipPath = SevenZipService.ResolveCliExecutable(state.SevenZipPath) ?? SevenZipService.FindSevenZip();
        string? directory = string.IsNullOrWhiteSpace(sevenZipPath) ? null : Path.GetDirectoryName(sevenZipPath);
        string? fileManagerPath = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "7zFM.exe");
        if (string.IsNullOrWhiteSpace(fileManagerPath) || !File.Exists(fileManagerPath))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "7-Zip File Manager が見つかりません。");
        }

        return Effect(CommandApplicationEffectKind.OpenExternalProcess, textValue: fileManagerPath, messageValue: archivePath);
    }

    private CommandApplicationResult ExecuteManagedTrashOperation(
        ManagedTrashCommandContext? request,
        ManagedTrashCommandAction? expectedAction = null)
    {
        if (request == null || _fileOperations.IsBusy ||
            (expectedAction.HasValue && request.Action != expectedAction.Value))
        {
            return CommandApplicationResult.NotHandled;
        }

        ManagedTrashOperationResult? operation = request.Action switch
        {
            ManagedTrashCommandAction.RestoreSelected when request.SelectedRecords is { Count: > 0 } selected =>
                _fileOperations.RestoreManagedTrash(selected),
            ManagedTrashCommandAction.DeleteSelectedForever when request.SelectedRecords is { Count: > 0 } selected =>
                _fileOperations.DeleteManagedTrashForever(selected),
            ManagedTrashCommandAction.Empty => _fileOperations.EmptyManagedTrash(),
            ManagedTrashCommandAction.CleanMissing => _fileOperations.CleanMissingManagedTrash(),
            _ => null
        };
        return operation is { } completed
            ? Effect(
                CommandApplicationEffectKind.ManagedTrashActionCompleted,
                managedTrashOperation: completed,
                managedTrash: request)
            : CommandApplicationResult.NotHandled;
    }

    private CommandApplicationResult ExecuteWorkspaceSnapshotCommand(
        string commandId,
        bool browser,
        WorkspaceSnapshotCommandContext? request)
    {
        if (!browser || request == null || _workspaceSnapshots == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        WorkspaceSnapshotCommandAction? expectedAction = commandId switch
        {
            CommandIds.AppOpenWorkspaceSnapshots => WorkspaceSnapshotCommandAction.Open,
            CommandIds.WorkspaceSnapshotSave => WorkspaceSnapshotCommandAction.Save,
            CommandIds.WorkspaceSnapshotRestore => WorkspaceSnapshotCommandAction.Restore,
            CommandIds.WorkspaceSnapshotRename => WorkspaceSnapshotCommandAction.Rename,
            CommandIds.WorkspaceSnapshotDelete => WorkspaceSnapshotCommandAction.Delete,
            CommandIds.WorkspaceSnapshotExport => WorkspaceSnapshotCommandAction.Export,
            CommandIds.WorkspaceSnapshotImport => WorkspaceSnapshotCommandAction.Import,
            CommandIds.WorkspaceSnapshotExportAll => WorkspaceSnapshotCommandAction.ExportAll,
            CommandIds.WorkspaceSnapshotImportAll => WorkspaceSnapshotCommandAction.ImportAll,
            _ => null
        };
        if (expectedAction != request.Action)
        {
            return CommandApplicationResult.NotHandled;
        }

        if (!_workspaceSnapshots.HasStorage)
        {
            return SnapshotFailure("Workspace スナップショットの保存先を初期化できません。");
        }

        switch (request.Action)
        {
            case WorkspaceSnapshotCommandAction.Open:
                return Effect(
                    CommandApplicationEffectKind.DispatchWorkspaceSnapshot,
                    workspaceSnapshot: request,
                    workspaceSnapshotEntries: _workspaceSnapshots.LoadEntries());
            case WorkspaceSnapshotCommandAction.Save:
            {
                string name = request.Name?.Trim() ?? string.Empty;
                if (name.Length == 0)
                {
                    return SnapshotFailure("スナップショット名を入力してください。");
                }
                if (_workspaceSnapshots.ExistsByName(name) && !request.OverwriteConfirmed)
                {
                    return Interaction(new WorkspaceSnapshotOverwriteInteractionRequest(request with { Name = name }));
                }
                if (!_workspaceSnapshots.Save(name, _workspaceSnapshots.CaptureCurrentState(), out string saveError))
                {
                    return SnapshotFailure(saveError);
                }
                return SnapshotSuccess($"Workspace スナップショットを保存しました: {name}");
            }
            case WorkspaceSnapshotCommandAction.Restore:
            {
                if (request.Entry is not { } entry || _workspaceSnapshotUi == null)
                {
                    return CommandApplicationResult.NotHandled;
                }
                CommandApplicationShellState state = _shell.CaptureState();
                BrowserWorkspaceSnapshotRestoreApplicationResult restore = _workspaceSnapshots.ExecuteRestore(
                    entry,
                    state.CurrentTab,
                    state.DirectoryLoadOptions,
                    state.ColumnCount,
                    state.RefreshShellState,
                    _workspaceSnapshotUi);
                if (restore.WasCanceled)
                {
                    return CommandApplicationResult.NotHandled;
                }
                return restore.Succeeded
                    ? SnapshotSuccess($"Workspace スナップショットを復元しました: {entry.Name}")
                    : SnapshotFailure($"Workspace スナップショットの復元に失敗しました。{Environment.NewLine}{restore.Error?.Message ?? "原因を特定できないエラーが発生しました。"}");
            }
            case WorkspaceSnapshotCommandAction.Rename:
            {
                if (request.Entry is not { } entry || string.IsNullOrWhiteSpace(request.Name))
                {
                    return CommandApplicationResult.NotHandled;
                }
                string name = request.Name.Trim();
                if (!_workspaceSnapshots.Rename(entry.SnapshotId, name, out string renameError))
                {
                    return SnapshotFailure(renameError);
                }
                return SnapshotSuccess($"Workspace スナップショット名を変更しました: {name}");
            }
            case WorkspaceSnapshotCommandAction.Delete:
            {
                if (request.Entry is not { } entry || !request.Confirmed)
                {
                    return CommandApplicationResult.NotHandled;
                }
                return _workspaceSnapshots.Delete(entry.SnapshotId)
                    ? SnapshotSuccess($"Workspace スナップショットを削除しました: {entry.Name}")
                    : SnapshotFailure("スナップショットを削除できませんでした。");
            }
            case WorkspaceSnapshotCommandAction.Export:
            {
                if (request.Entry is not { } entry || string.IsNullOrWhiteSpace(request.FilePath))
                {
                    return CommandApplicationResult.NotHandled;
                }
                var metadata = new WorkspaceSnapshotMetadata
                {
                    Name = entry.Name,
                    CreatedAtUtc = entry.CreatedAtUtc,
                    UpdatedAtUtc = entry.UpdatedAtUtc
                };
                if (!_workspaceSnapshots.Export(entry.SnapshotId, metadata, request.FilePath, out string exportError))
                {
                    return SnapshotFailure(exportError);
                }
                return SnapshotSuccess($"スナップショットをエクスポートしました: {Path.GetFileName(request.FilePath)}");
            }
            case WorkspaceSnapshotCommandAction.Import:
            {
                if (string.IsNullOrWhiteSpace(request.FilePath))
                {
                    return CommandApplicationResult.NotHandled;
                }
                string fallbackName = Path.GetFileNameWithoutExtension(request.FilePath);
                if (!_workspaceSnapshots.Import(request.FilePath, fallbackName, out string importedName, out string importError))
                {
                    return SnapshotFailure(importError);
                }
                return SnapshotSuccess($"スナップショットをインポートしました: {importedName}");
            }
            case WorkspaceSnapshotCommandAction.ExportAll:
                if (string.IsNullOrWhiteSpace(request.FilePath)) return CommandApplicationResult.NotHandled;
                if (_workspaceSnapshots.SnapshotCount == 0)
                {
                    return Effect(
                        CommandApplicationEffectKind.ShowWorkspaceSnapshotInformation,
                        textValue: "エクスポートするスナップショットがありません。");
                }
                if (!_workspaceSnapshots.ExportAll(request.FilePath, out int exportedCount, out string exportAllError))
                {
                    return SnapshotFailure(exportAllError);
                }
                return SnapshotSuccess($"全 {exportedCount} 件のスナップショットを一括エクスポートしました。");
            case WorkspaceSnapshotCommandAction.ImportAll:
                if (string.IsNullOrWhiteSpace(request.FilePath)) return CommandApplicationResult.NotHandled;
                if (!_workspaceSnapshots.ImportAll(request.FilePath, out int importedCount, out string importAllError))
                {
                    return SnapshotFailure(importAllError);
                }
                return SnapshotSuccess($"{importedCount} 件のスナップショットを一括インポートしました。");
            default:
                return CommandApplicationResult.NotHandled;
        }
    }

    private static CommandApplicationResult SnapshotFailure(string message) =>
        Effect(CommandApplicationEffectKind.ShowWorkspaceSnapshotFailure, textValue: message);

    private CommandApplicationResult SnapshotSuccess(string message) =>
        Effect(
            CommandApplicationEffectKind.ShowMessage,
            textValue: message,
            workspaceSnapshotEntries: _workspaceSnapshots?.LoadEntries());

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
            case (QuickAccessRegistrationCommandInteractionRequest registration,
                QuickAccessRegistrationCommandInteractionResponse registrationResponse):
                return ExecuteQuickAccessRegistrationContinuation(registration, registrationResponse);
            case (BrowserDerivedTabNavigationInteractionRequest derived, BrowserDerivedTabNavigationInteractionResponse derivedResponse):
                return ExecuteDerivedTabNavigationContinuation(derived, derivedResponse);
            case (LogdiskCommandInteractionRequest, LogdiskCommandInteractionResponse logdisk):
                return ExecuteLogdiskContinuation(logdisk.SelectedPath);
            case (ArchiveListCommandInteractionRequest archive, ArchiveListCommandInteractionResponse archiveResponse):
                return ExecuteArchiveListContinuation(archive, archiveResponse);
            case (ArchiveListNavigationApplyRequest navigation, ArchiveListNavigationApplyResponse navigationResponse):
                return CompleteArchiveListNavigation(navigation.Proposal, navigationResponse.Applied);
            case (CategoryRenameCommandInteractionRequest rename, CategoryRenameCommandInteractionResponse renamed):
                return ExecuteCategoryRenameContinuation(rename, renamed);
            case (BrowserTabGroupCommandInteractionRequest tabGroup, BrowserTabGroupCommandInteractionResponse tabGroupResponse):
                return ContinueBrowserTabGroupInteraction(tabGroup, tabGroupResponse);
            case (CategoryDeleteCommandInteractionRequest delete, CategoryDeleteCommandInteractionResponse deleted):
                return ExecuteCategoryDeleteContinuation(delete, deleted);
            case (TabCloseCategoryRemovalInteractionRequest tabClose, TabCloseCategoryRemovalInteractionResponse tabCloseResponse):
                return ExecuteTabCloseCategoryRemovalContinuation(tabClose, tabCloseResponse);
            case (BrowserTabVisitHistoryCommandInteractionRequest history, BrowserTabVisitHistoryCommandInteractionResponse historyResponse):
                return historyResponse.Accepted
                    ? ExecuteTabHistoryJump(historyResponse.Direction, historyResponse.Depth, browser: true)
                    : Effect(CommandApplicationEffectKind.NoOp);
            case (WorkspaceSnapshotOverwriteInteractionRequest overwrite, WorkspaceSnapshotOverwriteInteractionResponse overwriteResponse):
                return overwriteResponse.Accepted
                    ? ExecuteWorkspaceSnapshotCommand(
                        CommandIds.WorkspaceSnapshotSave,
                        true,
                        overwrite.Command with { OverwriteConfirmed = true })
                    : Effect(CommandApplicationEffectKind.NoOp);
            case (BrowserMarkSlotImportInteractionRequest markSlotImport, BrowserMarkSlotImportInteractionResponse markSlotImportResponse):
                return ContinueMarkSlotImport(markSlotImport, markSlotImportResponse);
            case (TextViewerSearchCommandInteractionRequest textViewerSearch, TextViewerSearchCommandInteractionResponse textViewerSearchResponse):
                return textViewerSearchResponse.Accepted
                    ? ExecuteTextViewerSearch(textViewerSearch.Context with { Query = textViewerSearchResponse.Query ?? string.Empty })
                    : Effect(CommandApplicationEffectKind.NoOp);
            default:
                return CommandApplicationResult.NotHandled;
        }
    }

    private CommandApplicationResult ExecuteTextViewerCommand(string commandId, CommandExecutionContext context)
    {
        if (commandId == CommandIds.TextViewerCopy)
        {
            if (context.TextViewerCopyOrigin == TextViewerCopyOrigin.ArchivePreview)
            {
                if (string.IsNullOrEmpty(context.TextViewerSelectedText) ||
                    context.TextViewerLargeFileCopy ||
                    context.TextViewerRenderedSelectionCopy)
                {
                    return CommandApplicationResult.NotHandled;
                }
            }
            else if (context.TextViewerCopyOrigin != TextViewerCopyOrigin.EmbeddedViewer ||
                _viewer.Mode != ViewerApplicationMode.Viewer)
            {
                return CommandApplicationResult.NotHandled;
            }
        }
        else if (context.TextViewerCopyOrigin == TextViewerCopyOrigin.ArchivePreview ||
            _viewer.Mode != ViewerApplicationMode.Viewer)
        {
            return CommandApplicationResult.NotHandled;
        }

        switch (commandId)
        {
            case CommandIds.TextViewerReturnToBrowser:
                if (context.TextViewerExit is not { } refreshRequest)
                {
                    return CommandApplicationResult.NotHandled;
                }
                return _viewerMode.TryExitToBrowser(_viewerModeUi, refreshRequest).Exited
                    ? Effect(CommandApplicationEffectKind.ApplyTextViewerExit)
                    : CommandApplicationResult.NotHandled;
            case CommandIds.TextViewerEncodingCycle:
                _viewerMode.CycleEncodingAndRefresh(_viewerModeUi);
                return Effect(CommandApplicationEffectKind.ViewerCommandCompleted);
            case CommandIds.TextViewerWordWrapToggle:
                return Effect(
                    CommandApplicationEffectKind.ApplyTextViewerWordWrap,
                    boolValue: _viewerWorkflow.ToggleWordWrap());
            case CommandIds.TextViewerSearch:
                return context.TextViewerSearch is { } search
                    ? new CommandApplicationResult(true, InteractionRequest: new TextViewerSearchCommandInteractionRequest(search))
                    : CommandApplicationResult.NotHandled;
            case CommandIds.TextViewerSearchNext:
            case CommandIds.TextViewerSearchPrevious:
                return context.TextViewerSearch is { } nextSearch
                    ? ExecuteTextViewerSearch(nextSearch with
                    {
                        Backward = string.Equals(commandId, CommandIds.TextViewerSearchPrevious, StringComparison.Ordinal)
                    })
                    : CommandApplicationResult.NotHandled;
            case CommandIds.TextViewerCopy:
                return Effect(
                    CommandApplicationEffectKind.ApplyTextViewerCopy,
                    textValue: context.TextViewerSelectedText,
                    boolValue: context.TextViewerLargeFileCopy,
                    messageValue: context.TextViewerCopyStatusMessage,
                    textViewerRenderedSelectionCopy: context.TextViewerRenderedSelectionCopy);
            case CommandIds.TextViewerOpenExternalEditor:
                if (context.TextViewerExternalEditor is not { } externalEditor)
                {
                    return CommandApplicationResult.NotHandled;
                }
                TextViewerExternalEditorPreparation preparation = _viewerWorkflow.PrepareDisplayedTextExternalEditor(
                    externalEditor.TargetPath,
                    externalEditor.DisplayedKind);
                return Effect(
                    CommandApplicationEffectKind.OpenTextViewerInExternalEditor,
                    textValue: preparation.TargetPath,
                    messageValue: preparation.ErrorMessage);
            case CommandIds.TextViewerNavigate:
                if (context.TextViewerNavigation is not { } navigation || _viewer.LargeFileState is not { } largeFileState)
                {
                    return CommandApplicationResult.NotHandled;
                }
                ViewerInputKey inputKey = navigation.Target switch
                {
                    TextViewerNavigationTarget.Home => ViewerInputKey.Home,
                    TextViewerNavigationTarget.End => ViewerInputKey.End,
                    TextViewerNavigationTarget.PageUp => ViewerInputKey.PageUp,
                    TextViewerNavigationTarget.PageDown => ViewerInputKey.PageDown,
                    _ => throw new ArgumentOutOfRangeException(nameof(navigation.Target))
                };
                return Effect(
                    CommandApplicationEffectKind.ApplyTextViewerNavigation,
                    intValue: (int)navigation.Target,
                    textViewerNavigation: _viewerWorkflow.ResolveLargeFileNavigation(
                        largeFileState,
                        inputKey,
                        navigation.VisibleLineCount,
                        navigation.MaxFirstVisibleLine));
            case CommandIds.TextViewerMarkdownModeSet:
                if (context.TextViewerMarkdownMode is not { } mode)
                {
                    return CommandApplicationResult.NotHandled;
                }
                _viewerWorkflow.SetMarkdownViewerMode(mode, save: true);
                return Effect(
                    CommandApplicationEffectKind.ApplyTextViewerMarkdownMode,
                    intValue: (int)mode);
            default:
                return CommandApplicationResult.NotHandled;
        }
    }

    private static CommandApplicationResult ExecuteOpenExternalUrl(ExternalUrlOpenCommandContext? request)
    {
        if (request?.TargetUri is not { IsAbsoluteUri: true } targetUri
            || !(targetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || targetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return CommandApplicationResult.NotHandled;
        }

        return Effect(
            CommandApplicationEffectKind.OpenExternalUrl,
            textValue: targetUri.AbsoluteUri);
    }

    private static CommandApplicationResult ExecuteHashResultCopy(HashResultCopyContext? request)
    {
        if (request == null) return CommandApplicationResult.NotHandled;
        string? text;
        switch (request.Format)
        {
            case HashResultCopyFormat.SelectedHash:
                if (request.SelectedIndex < 0 || request.SelectedIndex >= request.Items.Count)
                    return CommandApplicationResult.NotHandled;
                text = request.Items[request.SelectedIndex].HashValue;
                break;
            case HashResultCopyFormat.Csv:
                if (request.Items.Count == 0) return CommandApplicationResult.NotHandled;
                var csv = new System.Text.StringBuilder();
                csv.AppendLine("\"対象\",\"アルゴリズム\",\"ハッシュ値\"");
                foreach (HashResultCopyItem item in request.Items)
                    csv.Append('"').Append(item.Target.Replace("\"", "\"\"")).Append("\",")
                        .Append('"').Append(item.Algorithm.Replace("\"", "\"\"")).Append("\",")
                        .Append('"').Append(item.HashValue.Replace("\"", "\"\"")).AppendLine("\"");
                text = csv.ToString();
                break;
            case HashResultCopyFormat.Log:
                text = request.OutputText;
                break;
            default:
                return CommandApplicationResult.NotHandled;
        }
        return string.IsNullOrEmpty(text)
            ? CommandApplicationResult.NotHandled
            : Effect(CommandApplicationEffectKind.CopyHashResultText, textValue: text);
    }

    private static CommandApplicationResult ExecuteCommandPaletteFavoriteToggle(
        CommandPaletteFavoriteToggleCommandContext? request)
    {
        if (request is not { UsageState: { } usageState, TargetCommandId.Length: > 0 } ||
            !CommandPaletteUsageStorage.ToggleFavorite(usageState, request.TargetCommandId, request.Persist))
        {
            return CommandApplicationResult.NotHandled;
        }

        return new CommandApplicationResult(true);
    }

    private CommandApplicationResult ExecuteTextViewerSearch(TextViewerSearchCommandContext context)
    {
        string query = context.Query ?? string.Empty;
        if (context.IsLargeFile)
        {
            query = query.Trim();
        }
        _viewerWorkflow.SetSearchKeyword(query);
        if (context.IsLargeFile)
        {
            if (_viewer.LargeFileState is { } state)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    _viewerWorkflow.InvalidateLargeFileSearchRequest(state);
                    _viewerWorkflow.ClearLargeFileSearchState(state);
                }
                else if (!context.PreserveLargeFileSearchHit
                    && !(string.Equals(state.LastSearchText, query, StringComparison.OrdinalIgnoreCase)
                        && state.ActiveSearchHitLine.HasValue))
                {
                    _viewerWorkflow.ClearLargeFileSearchState(state);
                }

                if (!string.IsNullOrWhiteSpace(query))
                {
                    BeginLargeFileSearch(state, query, context.Backward, isWrapAround: false);
                }
            }
            return Effect(
                CommandApplicationEffectKind.ApplyTextViewerSearch,
                textViewerSearch: new TextViewerSearchExecution(query, context.Backward, true, null));
        }

        ViewerTextSearchResult result = _viewerWorkflow.FindText(
            context.TextSnapshot,
            query,
            context.StartIndex,
            context.Backward,
            allowWrap: true);
        return Effect(
            CommandApplicationEffectKind.ApplyTextViewerSearch,
            textViewerSearch: new TextViewerSearchExecution(query, context.Backward, false, result));
    }

    private void BeginLargeFileSearch(
        LargeFilePreviewState state,
        string query,
        bool backward,
        bool isWrapAround)
    {
        _viewerWorkflow.PrepareLargeFileSearch(state, query, backward);
        _ = CompleteLargeFileSearchAsync(
            state,
            query,
            backward,
            isWrapAround,
            _viewerWorkflow.ResolveCurrentViewerEncoding(),
            _viewer.Token);
    }

    private async Task CompleteLargeFileSearchAsync(
        LargeFilePreviewState state,
        string query,
        bool backward,
        bool isWrapAround,
        System.Text.Encoding encoding,
        CancellationToken token)
    {
        int viewerRequestId = _viewer.CurrentRequestId;
        Task<LargeFileSearchResult> searchTask = _viewerWorkflow.SearchLargeFileAsync(
            state,
            query,
            backward,
            isWrapAround,
            encoding,
            token);
        int requestId = state.SearchRequestId;
        var origin = new ViewerSearchResultOrigin(state, viewerRequestId, requestId, token);
        try
        {
            LargeFileSearchResult search = await searchTask.ConfigureAwait(true);
            if (!_viewerWorkflow.IsLargeFileSearchRequestActive(state, search.RequestId))
            {
                return;
            }

            _backgroundResultPort?.QueueBackgroundResult(Effect(
                CommandApplicationEffectKind.ApplyTextViewerLargeSearchResult,
                textViewerSearch: new TextViewerSearchExecution(
                    query,
                    backward,
                    true,
                    null,
                    search),
                viewerSearchOrigin: origin));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_viewerWorkflow.IsLargeFileSearchRequestActive(state, requestId))
            {
                return;
            }

            LogService.Error($"Viewer内検索に失敗しました: {query}", ex);
            _backgroundResultPort?.QueueBackgroundResult(Effect(
                CommandApplicationEffectKind.ShowMessage,
                textValue: "Viewer内検索に失敗しました。",
                viewerSearchOrigin: origin));
        }
    }

    private CommandApplicationResult ExecuteSettingsTransfer(
        string commandId,
        SettingsTransferCommandContext? context)
    {
        if (context == null || string.IsNullOrWhiteSpace(context.FilePath) || _settings == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        SettingsSqliteStore.SettingsTransferResult? result = (commandId, context.Action) switch
        {
            (CommandIds.AppSettingsExport, SettingsTransferAction.Export) when context.Settings != null =>
                _settings.ExportSettings(context.FilePath, context.Settings),
            (CommandIds.AppSettingsImport, SettingsTransferAction.ReadImport) =>
                _settings.ReadImportedSettings(context.FilePath),
            (CommandIds.AppSettingsImport, SettingsTransferAction.ApplyImport) when context.Settings != null =>
                _settings.ApplyImportedSettings(context.Settings),
            _ => null
        };

        return result == null
            ? CommandApplicationResult.NotHandled
            : Effect(
                CommandApplicationEffectKind.SettingsTransferCompleted,
                settingsTransfer: context,
                settingsTransferResult: result);
    }

    private CommandApplicationResult ExecuteSettingsSave(SettingsSaveCommandContext? context)
        => ExecuteSettingsSaveCommand(_settings, context);

    internal static CommandApplicationResult ExecuteSettingsSaveCommand(
        ISettingsApplicationPort? settingsApplication,
        SettingsSaveCommandContext? context)
    {
        if (context?.Settings is not { } settings || settingsApplication is null)
        {
            return CommandApplicationResult.NotHandled;
        }

        SettingsSqliteStore.SettingsSaveResult result = settingsApplication.TrySaveExplicit(settings);
        return Effect(
            CommandApplicationEffectKind.SettingsSaveCompleted,
            settingsSaveResult: result);
    }

    private CommandApplicationResult ExecuteBrowserAudioCtrlEnter(bool allowed, string? fullPath)
    {
        if (!allowed || string.IsNullOrWhiteSpace(fullPath))
        {
            return CommandApplicationResult.NotHandled;
        }

        ViewerAudioCtrlEnterDecision decision = _viewerWorkflow.ResolveAudioCtrlEnter(fullPath);
        if (!decision.Handled)
        {
            return CommandApplicationResult.NotHandled;
        }
        return Effect(
            CommandApplicationEffectKind.LaunchExternalMediaPlayback,
            textValue: fullPath,
            boolValue: decision.UseExternalMediaPlayback);
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

        if (!TryResolveTabCommandTarget(context, out int tabIndex))
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserTabState state = _browser.Workspace.TabStates[tabIndex];
        if (context.UnifiedSearchResume is { } resume)
        {
            if (resume.SessionId == Guid.Empty
                || resume.SourceTabId == Guid.Empty
                || context.ContextBrowserTabId != resume.SourceTabId
                || !string.Equals(context.CategoryId, resume.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_browser.Workspace.ActiveCategoryId, resume.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(resume.RootPath)
                || resume.Mode == UnifiedFilterFindMode.FilterCurrentTab)
            {
                return CommandApplicationResult.NotHandled;
            }

            return Interaction(new UnifiedFilterFindCommandInteractionRequest(
                tabIndex,
                resume.RootPath,
                resume.Criteria.Clone(),
                resume.Mode,
                resume.SourceTabId,
                PreserveSearchDraft: true));
        }

        return Interaction(new UnifiedFilterFindCommandInteractionRequest(
            tabIndex,
            state.CurrentPath,
            UnifiedFilterFindCriteria.FromTab(state.FilterPattern, state.FilterUseRegex, state.FilterLock),
            initialMode,
            context.ContextBrowserTabId));
    }

    private static CommandApplicationResult ExecuteUnifiedSearchSessionClose(
        bool allowed,
        UnifiedSearchSessionCommandContext? context)
    {
        return allowed && context is not null && context.SessionId != Guid.Empty
            ? Effect(CommandApplicationEffectKind.CloseUnifiedSearchSession, runtimeTargetId: context.SessionId)
            : CommandApplicationResult.NotHandled;
    }

    private static CommandApplicationResult ExecuteUnifiedSearchResultsExport(
        bool allowed,
        UnifiedSearchResultsExportCommandContext? context)
    {
        return allowed && context is not null && context.SessionId != Guid.Empty &&
            (context.Snapshot.ResultCount > 0 || context.Snapshot.IsPartial)
            ? Effect(
                CommandApplicationEffectKind.ExportUnifiedSearchResults,
                runtimeTargetId: context.SessionId,
                unifiedSearchResultsExport: context)
            : CommandApplicationResult.NotHandled;
    }

    private CommandApplicationResult ExecuteUnifiedSearchResultsFilter(
        bool allowed,
        UnifiedSearchResultsFilterCommandContext? context)
    {
        if (!allowed || context is null ||
            _unifiedSearchResultActivation?.CanFilterSearchResults(context.SessionId) != true)
        {
            return CommandApplicationResult.NotHandled;
        }

        return Effect(
            CommandApplicationEffectKind.ApplyUnifiedSearchResultsFilter,
            runtimeTargetId: context.SessionId,
            unifiedSearchResultsFilter: context);
    }

    private CommandApplicationResult ExecuteUnifiedSearchResultActivation(
        string commandId,
        bool allowed,
        UnifiedSearchResultActivationCommandContext? context)
    {
        if (!allowed || _unifiedSearchResultActivation == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        UnifiedSearchResultActivationExecution? execution = _unifiedSearchResultActivation.TryActivate(commandId, context);
        return execution == null
            ? CommandApplicationResult.NotHandled
            : Effect(
                CommandApplicationEffectKind.UnifiedSearchResultActivationCompleted,
                runtimeTargetId: execution.SessionId,
                unifiedSearchResultActivation: execution);
    }

    private CommandApplicationResult ExecuteUnifiedSearchHitEditor(
        bool allowed,
        UnifiedSearchHitEditorCommandContext? request)
    {
        if (!allowed || _unifiedSearchResultActivation == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        UnifiedSearchHitEditorExecution? execution = _unifiedSearchResultActivation.TryPrepareHitEditor(request);
        return execution == null
            ? CommandApplicationResult.NotHandled
            : new CommandApplicationResult(true, new CommandApplicationEffect(
                CommandApplicationEffectKind.OpenUnifiedSearchHitInExternalEditor,
                RuntimeTargetId: execution.SessionId)
            {
                UnifiedSearchHitEditor = execution
            });
    }

    private CommandApplicationResult ExecuteClearFilter(bool allowed, CommandExecutionContext context)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        if (!TryResolveTabCommandTarget(context, out int tabIndex))
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

    private CommandApplicationResult ExecuteQuickAccessEntryActivation(
        bool allowed,
        BrowserQuickAccessEntryCommandContext? request)
    {
        if (!allowed || request?.Entry is not { } entry || string.IsNullOrWhiteSpace(entry.Path))
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserQuickAccessNavigationExecution execution = _navigation.ExecuteQuickAccessNavigation(
            QuickAccessApplicationAction.Navigate,
            updatedStore: null,
            selectedEntry: entry,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyQuickAccess, quickAccess: execution);
    }

    private CommandApplicationResult BeginQuickAccessRegistration(bool allowed, string? pathToRegister)
    {
        if (!allowed || string.IsNullOrWhiteSpace(pathToRegister) || _fileOperations.IsBusy)
        {
            return CommandApplicationResult.NotHandled;
        }

        return Interaction(new QuickAccessRegistrationCommandInteractionRequest(
            _browser.CurrentPath,
            pathToRegister,
            _browser.CreateQuickAccessDisplayName(pathToRegister),
            _browser.GetQuickAccessCategoryNames()));
    }

    private CommandApplicationResult ExecuteQuickAccessDraftMutation(
        bool allowed,
        string commandId,
        BrowserQuickAccessDraftCommandContext? request)
    {
        if (!allowed || request == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserQuickAccessDraftMutationResult mutation = commandId switch
        {
            CommandIds.BrowserQuickAccessEntryAdd => _browser.AddQuickAccessDraftEntry(request),
            CommandIds.BrowserQuickAccessEntryUpdate => _browser.UpdateQuickAccessDraftEntry(request),
            CommandIds.BrowserQuickAccessEntryDelete => _browser.DeleteQuickAccessDraftEntry(request),
            CommandIds.BrowserQuickAccessReorder => _browser.ReorderQuickAccessDraft(request),
            _ => throw new ArgumentOutOfRangeException(nameof(commandId), commandId, "Unknown QuickAccess draft command.")
        };

        return new CommandApplicationResult(true) { QuickAccessDraftMutation = mutation };
    }

    private CommandApplicationResult ExecuteQuickAccessRegistrationContinuation(
        QuickAccessRegistrationCommandInteractionRequest request,
        QuickAccessRegistrationCommandInteractionResponse response)
    {
        if (!response.Accepted)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserQuickAccessRegistrationExecution registration = _navigation.ExecuteQuickAccessRegistration(
            response.DisplayName,
            response.CategoryName,
            response.Path,
            response.UseForTabTitle,
            request.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(
            CommandApplicationEffectKind.CompleteQuickAccessRegistration,
            quickAccessRegistration: registration);
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

    private CommandApplicationResult BeginBrowserTabGroupCreateInteraction(bool allowed, CommandExecutionContext context)
    {
        if (!allowed || !TryResolveBrowserTabGroupTarget(context, out BrowserTabGroupCommandContext target))
        {
            return CommandApplicationResult.NotHandled;
        }

        if (target.TabId == Guid.Empty)
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "対象のBrowserタブが見つかりません。");
        }
        if (!IsBrowserTabGroupMemberTargetAvailable(target))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        IReadOnlySet<Guid> blockedTabIds = GetCurrentBrowserTabGroupBlockedTabIds(
            target.CategoryId,
            context.BrowserTabGroupBlockedTabIds);
        if (blockedTabIds.Contains(target.TabId))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "検索結果を表示中はタブをグループへ移動できません。検索を閉じると利用できます。");
        }

        return Interaction(new BrowserTabGroupCommandInteractionRequest(
            BrowserTabGroupInteractionKind.Create,
            target.CategoryId,
            target.TabId,
            null,
            null,
            BlockedTabIds: blockedTabIds));
    }

    private CommandApplicationResult BeginOrExecuteBrowserTabGroupAdd(bool allowed, CommandExecutionContext context)
    {
        if (!allowed || !TryResolveBrowserTabGroupTarget(context, out BrowserTabGroupCommandContext target))
        {
            return CommandApplicationResult.NotHandled;
        }

        if (target.TabId == Guid.Empty)
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "対象のBrowserタブが見つかりません。");
        }
        if (!IsBrowserTabGroupMemberTargetAvailable(target))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        IReadOnlySet<Guid> blockedTabIds = GetCurrentBrowserTabGroupBlockedTabIds(
            target.CategoryId,
            context.BrowserTabGroupBlockedTabIds);
        if (blockedTabIds.Contains(target.TabId))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "検索結果を表示中はタブをグループへ移動できません。検索を閉じると利用できます。");
        }

        if (target.GroupId is { } groupId)
        {
            return ExecuteBrowserTabGroupAdd(target, groupId, blockedTabIds);
        }

        IReadOnlyList<BrowserTabGroup> groups = _persistence.GetBrowserTabGroups(target.CategoryId);
        if (groups.Count == 0)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        return Interaction(new BrowserTabGroupCommandInteractionRequest(
            BrowserTabGroupInteractionKind.Add,
            target.CategoryId,
            target.TabId,
            null,
            Groups: groups,
            BlockedTabIds: blockedTabIds));
    }

    private CommandApplicationResult ExecuteBrowserTabGroupRemove(bool allowed, CommandExecutionContext context)
    {
        if (!allowed || !TryResolveBrowserTabGroupTarget(context, out BrowserTabGroupCommandContext target))
        {
            return CommandApplicationResult.NotHandled;
        }

        if (target.TabId == Guid.Empty)
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "対象のBrowserタブが見つかりません。");
        }
        if (!IsBrowserTabGroupMemberTargetAvailable(target))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }
        if (!_persistence.TryRemoveBrowserTabGroupMember(target.CategoryId, target.TabId))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        return Effect(
            CommandApplicationEffectKind.ApplyBrowserTabGroupMutation,
            textValue: "タブをグループから外しました。",
            contextBrowserTabId: target.TabId);
    }

    private CommandApplicationResult ExecuteBrowserTabTreeDrop(
        bool allowed,
        CommandExecutionContext context)
    {
        if (!allowed || context.BrowserTabTreeDrop is not { } request)
        {
            return CommandApplicationResult.NotHandled;
        }

        if (string.IsNullOrWhiteSpace(request.SourceCategoryId)
            || request.SourceTabId == Guid.Empty
            || !string.Equals(request.SourceCategoryId, request.DestinationCategoryId, StringComparison.OrdinalIgnoreCase)
            || _browser.Workspace.FindCategorySnapshot(request.SourceCategoryId) is not { } category
            || !TryGetBrowserTabTreeDropTabIds(category.Id, out IReadOnlyList<Guid> tabIds)
            || tabIds.Count(id => id == request.SourceTabId) != 1)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        BrowserTabGroup? destinationGroup = null;
        if (request.DestinationGroupId is { } destinationGroupId)
        {
            destinationGroup = _persistence.FindBrowserTabGroup(destinationGroupId);
            if (destinationGroup == null
                || !string.Equals(destinationGroup.CategoryId, category.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Effect(CommandApplicationEffectKind.NoOp);
            }
        }

        IReadOnlySet<Guid> blockedTabIds = GetCurrentBrowserTabGroupBlockedTabIds(
            category.Id,
            context.BrowserTabGroupBlockedTabIds);
        if (blockedTabIds.Contains(request.SourceTabId))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "検索結果を表示中はタブをグループへ移動できません。検索を閉じると利用できます。");
        }

        if (!TryFindUniqueBrowserTabGroupMembership(category.Id, request.SourceTabId, out BrowserTabGroup? sourceGroup))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        bool membershipChangeRequired = sourceGroup?.Id != request.DestinationGroupId;
        if (membershipChangeRequired != request.RequiresMembershipChange
            || !IsBrowserTabTreeDropPlacementValid(request, category.Id, request.SourceTabId, tabIds, blockedTabIds))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        string? membershipMessage = null;
        if (request.RequiresMembershipChange)
        {
            CommandApplicationResult membershipResult = request.DestinationGroupId is { } groupId
                ? ExecuteBrowserTabGroupAdd(
                    new BrowserTabGroupCommandContext(category.Id, request.SourceTabId, groupId),
                    groupId,
                    blockedTabIds)
                : ExecuteBrowserTabGroupRemove(
                    allowed: true,
                    new CommandExecutionContext
                    {
                        Scope = CommandScope.Browser,
                        BrowserTabGroup = new BrowserTabGroupCommandContext(category.Id, request.SourceTabId)
                    });

            if (membershipResult.Effect?.Kind != CommandApplicationEffectKind.ApplyBrowserTabGroupMutation)
            {
                return membershipResult;
            }

            membershipMessage = membershipResult.Effect.TextValue;
        }

        bool membershipConfirmed = TryFindUniqueBrowserTabGroupMembership(
                category.Id,
                request.SourceTabId,
                out BrowserTabGroup? confirmedMembership)
            && confirmedMembership?.Id == request.DestinationGroupId;
        if (!membershipConfirmed)
        {
            return request.RequiresMembershipChange
                ? BrowserTabTreeDropResult(
                    new BrowserTabTreeDropExecution(
                        MembershipChanged: true,
                        MembershipConfirmed: false,
                        ReorderRequested: request.RequiresReorder,
                        ReorderAttempted: false,
                        ReorderApplied: false),
                    membershipMessage)
                : Effect(CommandApplicationEffectKind.NoOp);
        }

        if (!request.RequiresMembershipChange && !request.RequiresReorder)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        if (!request.RequiresReorder)
        {
            return BrowserTabTreeDropResult(
                new BrowserTabTreeDropExecution(
                    MembershipChanged: request.RequiresMembershipChange,
                    MembershipConfirmed: true,
                    ReorderRequested: false,
                    ReorderAttempted: false,
                    ReorderApplied: false),
                membershipMessage);
        }

        if (request.AnchorTabId is not { } anchorTabId
            || blockedTabIds.Contains(anchorTabId)
            || !TryGetBrowserTabTreeDropTabIds(category.Id, out tabIds)
            || tabIds.Count(id => id == anchorTabId) != 1
            || !IsBrowserTabTreeDropAnchorValid(request, category.Id, anchorTabId))
        {
            return request.RequiresMembershipChange
                ? BrowserTabTreeDropResult(
                    new BrowserTabTreeDropExecution(
                        MembershipChanged: true,
                        MembershipConfirmed: true,
                        ReorderRequested: true,
                        ReorderAttempted: false,
                        ReorderApplied: false),
                    membershipMessage)
                : Effect(CommandApplicationEffectKind.NoOp);
        }

        bool insertAfter = request.Placement is BrowserTabTreeDropPlacement.After
            or BrowserTabTreeDropPlacement.AppendToGroup
            or BrowserTabTreeDropPlacement.AppendToCategory;
        CommandApplicationShellState shellState = _shell.CaptureState();
        BrowserTabState? currentState = string.Equals(
            category.Id,
            _browser.Workspace.ActiveCategoryId,
            StringComparison.OrdinalIgnoreCase)
                ? shellState.CurrentTab
                : null;
        bool reorderApplied = _tabs.ReorderTabById(
            category.Id,
            request.SourceTabId,
            anchorTabId,
            insertAfter,
            currentState);

        return BrowserTabTreeDropResult(
            new BrowserTabTreeDropExecution(
                MembershipChanged: request.RequiresMembershipChange,
                MembershipConfirmed: true,
                ReorderRequested: true,
                ReorderAttempted: true,
                ReorderApplied: reorderApplied),
            membershipMessage);
    }

    private bool IsBrowserTabTreeDropPlacementValid(
        BrowserTabTreeDropCommandContext request,
        string categoryId,
        Guid sourceTabId,
        IReadOnlyList<Guid> tabIds,
        IReadOnlySet<Guid> blockedTabIds)
    {
        if (request.AnchorTabId is { } anchorTabId
            && (anchorTabId == sourceTabId
                || blockedTabIds.Contains(anchorTabId)
                || tabIds.Count(id => id == anchorTabId) != 1
                || !IsBrowserTabTreeDropAnchorValid(request, categoryId, anchorTabId)))
        {
            return false;
        }

        return request.Placement switch
        {
            BrowserTabTreeDropPlacement.Before or BrowserTabTreeDropPlacement.After => request.AnchorTabId.HasValue,
            BrowserTabTreeDropPlacement.AppendToGroup => request.DestinationGroupId.HasValue
                && request.AnchorTabId == FindLastBrowserTabTreeDropAnchor(
                    categoryId,
                    tabIds,
                    sourceTabId,
                    request.DestinationGroupId,
                    blockedTabIds),
            BrowserTabTreeDropPlacement.AppendToCategory => !request.DestinationGroupId.HasValue
                && request.AnchorTabId == FindLastBrowserTabTreeDropAnchor(
                    categoryId,
                    tabIds,
                    sourceTabId,
                    groupId: null,
                    excludedTabIds: blockedTabIds),
            _ => false
        };
    }

    private bool IsBrowserTabTreeDropAnchorValid(
        BrowserTabTreeDropCommandContext request,
        string categoryId,
        Guid anchorTabId)
    {
        if (!TryFindUniqueBrowserTabGroupMembership(categoryId, anchorTabId, out BrowserTabGroup? anchorGroup))
        {
            return false;
        }

        return request.Placement switch
        {
            BrowserTabTreeDropPlacement.Before or BrowserTabTreeDropPlacement.After or BrowserTabTreeDropPlacement.AppendToGroup =>
                anchorGroup?.Id == request.DestinationGroupId,
            BrowserTabTreeDropPlacement.AppendToCategory => !request.DestinationGroupId.HasValue,
            _ => false
        };
    }

    private Guid? FindLastBrowserTabTreeDropAnchor(
        string categoryId,
        IReadOnlyList<Guid> tabIds,
        Guid sourceTabId,
        Guid? groupId,
        IReadOnlySet<Guid>? excludedTabIds = null)
    {
        for (int index = tabIds.Count - 1; index >= 0; index--)
        {
            Guid tabId = tabIds[index];
            if (tabId == sourceTabId || excludedTabIds?.Contains(tabId) == true)
            {
                continue;
            }

            if (groupId is { } targetGroupId)
            {
                if (TryFindUniqueBrowserTabGroupMembership(categoryId, tabId, out BrowserTabGroup? memberGroup)
                    && memberGroup?.Id == targetGroupId)
                {
                    return tabId;
                }
                continue;
            }

            return tabId;
        }

        return null;
    }

    private bool TryFindUniqueBrowserTabGroupMembership(
        string categoryId,
        Guid tabId,
        out BrowserTabGroup? group)
    {
        BrowserTabGroup[] matches = _persistence.GetBrowserTabGroups(categoryId)
            .Where(candidate => candidate.MemberTabIds.Contains(tabId))
            .Take(2)
            .ToArray();
        group = matches.FirstOrDefault();
        return matches.Length <= 1;
    }

    private bool TryGetBrowserTabTreeDropTabIds(string categoryId, out IReadOnlyList<Guid> tabIds)
    {
        if (string.Equals(categoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            tabIds = _browser.Workspace.TabStates.Select(static tab => tab.Id).ToArray();
            return true;
        }

        BrowserTabRestoreCategoryState? storedCategory = _persistence.FindStoredCategory(categoryId);
        tabIds = storedCategory?.OpenTabs.Select(static tab => tab.TabId).ToArray() ?? Array.Empty<Guid>();
        return storedCategory != null;
    }

    private CommandApplicationResult BrowserTabTreeDropResult(
        BrowserTabTreeDropExecution execution,
        string? membershipMessage) =>
        Effect(
            CommandApplicationEffectKind.ApplyBrowserTabTreeDrop,
            textValue: membershipMessage,
            browserTabTreeDrop: execution);

    private CommandApplicationResult BeginBrowserTabGroupRenameInteraction(bool allowed, CommandExecutionContext context)
    {
        if (!allowed || !TryResolveBrowserTabGroupTarget(context, out BrowserTabGroupCommandContext target))
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserTabGroup? group = target.GroupId is { } groupId
            ? _persistence.FindBrowserTabGroup(groupId)
            : _persistence.FindBrowserTabGroupByMember(target.CategoryId, target.TabId);
        if (target.GroupId.HasValue && group == null)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }
        if (target.TabId != Guid.Empty
            && !target.GroupId.HasValue
            && !IsBrowserTabGroupMemberTargetAvailable(target))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }
        if (group != null)
        {
            return Interaction(new BrowserTabGroupCommandInteractionRequest(
                BrowserTabGroupInteractionKind.Rename,
                group.CategoryId,
                group.MemberTabIds.FirstOrDefault(),
                group.Id,
                group.DisplayName,
                BlockedTabIds: context.BrowserTabGroupBlockedTabIds));
        }

        IReadOnlyList<BrowserTabGroup> groups = _persistence.GetBrowserTabGroups(target.CategoryId);
        return groups.Count == 0
            ? Effect(CommandApplicationEffectKind.NoOp)
            : Interaction(new BrowserTabGroupCommandInteractionRequest(
                BrowserTabGroupInteractionKind.Rename,
                target.CategoryId,
                target.TabId,
                null,
                Groups: groups,
                BlockedTabIds: context.BrowserTabGroupBlockedTabIds));
    }

    private CommandApplicationResult ContinueBrowserTabGroupInteraction(
        BrowserTabGroupCommandInteractionRequest request,
        BrowserTabGroupCommandInteractionResponse response)
    {
        if (!response.Accepted)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        return request.Kind switch
        {
            BrowserTabGroupInteractionKind.Create => ExecuteBrowserTabGroupCreate(
                request.CategoryId,
                request.TabId,
                response.DisplayName ?? string.Empty,
                request.BlockedTabIds),
            BrowserTabGroupInteractionKind.Add when response.GroupId is { } groupId
                && CanContinueBrowserTabGroupAdd(request, groupId) => ExecuteBrowserTabGroupAdd(
                new BrowserTabGroupCommandContext(request.CategoryId, request.TabId),
                groupId,
                request.BlockedTabIds),
            BrowserTabGroupInteractionKind.Rename when response.GroupId is { } groupId
                && CanContinueBrowserTabGroupRename(request, groupId) => ExecuteBrowserTabGroupRename(
                    groupId,
                    response.DisplayName ?? string.Empty),
            _ => Effect(CommandApplicationEffectKind.NoOp)
        };
    }

    private CommandApplicationResult ExecuteBrowserTabGroupCreate(
        string categoryId,
        Guid tabId,
        string displayName,
        IReadOnlySet<Guid>? blockedTabIds)
    {
        var target = new BrowserTabGroupCommandContext(categoryId, tabId);
        if (!IsBrowserTabGroupMemberTargetAvailable(target))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        IReadOnlySet<Guid> currentBlockedTabIds = GetCurrentBrowserTabGroupBlockedTabIds(categoryId, blockedTabIds);
        if (currentBlockedTabIds.Contains(tabId))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "検索結果を表示中はタブをグループへ移動できません。検索を閉じると利用できます。");
        }

        if (!_persistence.TryCreateBrowserTabGroup(
                categoryId,
                displayName,
                tabId,
                currentBlockedTabIds,
                out BrowserTabGroup? created)
            || created == null)
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "空でないグループ名を入力してください。");
        }

        _unifiedSearchResultActivation?.ForgetDerivedTab(tabId);

        return Effect(
            CommandApplicationEffectKind.ApplyBrowserTabGroupMutation,
            boolValue: true,
            textValue: $"タブグループを作成しました: {created.DisplayName}",
            contextBrowserTabId: tabId);
    }

    private CommandApplicationResult ExecuteBrowserTabGroupAdd(
        BrowserTabGroupCommandContext target,
        Guid groupId,
        IReadOnlySet<Guid>? blockedTabIds)
    {
        if (!IsBrowserTabGroupMemberTargetAvailable(target))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        BrowserTabGroup? requestedGroup = _persistence.FindBrowserTabGroup(groupId);
        if (requestedGroup == null)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        IReadOnlySet<Guid> currentBlockedTabIds = GetCurrentBrowserTabGroupBlockedTabIds(
            target.CategoryId,
            blockedTabIds);
        if (currentBlockedTabIds.Contains(target.TabId))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "検索結果を表示中はタブをグループへ移動できません。検索を閉じると利用できます。");
        }

        if (!_persistence.TryAddBrowserTabGroupMember(
                groupId,
                target.CategoryId,
                target.TabId,
                currentBlockedTabIds))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "同じカテゴリのタブグループを選択してください。");
        }

        _unifiedSearchResultActivation?.ForgetDerivedTab(target.TabId);

        return Effect(
            CommandApplicationEffectKind.ApplyBrowserTabGroupMutation,
            boolValue: true,
            textValue: "タブをグループへ移動しました。",
            contextBrowserTabId: target.TabId);
    }

    private CommandApplicationResult ExecuteBrowserTabGroupRename(Guid groupId, string displayName)
    {
        if (!_persistence.TryRenameBrowserTabGroup(groupId, displayName))
        {
            return Effect(CommandApplicationEffectKind.ShowMessage, textValue: "空でないグループ名を入力してください。");
        }

        return Effect(
            CommandApplicationEffectKind.ApplyBrowserTabGroupMutation,
            textValue: $"タブグループ名を変更しました: {displayName.Trim()}");
    }

    private bool TryResolveBrowserTabGroupTarget(
        CommandExecutionContext context,
        out BrowserTabGroupCommandContext target)
    {
        BrowserTabGroupCommandContext? requested = context.BrowserTabGroup;
        Guid? requestedGroupId = requested?.GroupId ?? context.RuntimeTargetId;
        BrowserTabGroup? requestedGroup = requestedGroupId is { } groupId
            ? _persistence.FindBrowserTabGroup(groupId)
            : null;
        string requestedCategoryId = requested?.CategoryId
            ?? context.CategoryId
            ?? requestedGroup?.CategoryId
            ?? _browser.Workspace.ActiveCategoryId;
        BrowserTabCategoryDefinition? category = _browser.Workspace.FindCategorySnapshot(requestedCategoryId);
        target = new BrowserTabGroupCommandContext(requestedCategoryId, Guid.Empty, requestedGroupId, requested?.DisplayName);
        if (category == null
            || (requestedGroup != null
                && !string.IsNullOrWhiteSpace(requested?.CategoryId)
                && !string.Equals(requestedGroup.CategoryId, category.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string categoryId = category.Id;

        Guid tabId = requested?.TabId is { } requestedTabId && requestedTabId != Guid.Empty
            ? requestedTabId
            : context.ContextBrowserTabId ?? Guid.Empty;
        if (tabId == Guid.Empty)
        {
            IReadOnlyList<Guid> tabIds;
            int defaultTabIndex;
            if (string.Equals(categoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                tabIds = _browser.Workspace.TabStates.Select(static tab => tab.Id).ToArray();
                defaultTabIndex = _browser.Workspace.ActiveTabIndex;
            }
            else
            {
                BrowserTabRestoreCategoryState? storedCategory = _persistence.FindStoredCategory(categoryId);
                tabIds = storedCategory?.OpenTabs.Select(static tab => tab.TabId).ToArray() ?? Array.Empty<Guid>();
                defaultTabIndex = storedCategory?.ActiveTabIndex ?? -1;
            }

            int tabIndex = context.ContextTabIndex ?? defaultTabIndex;
            if (tabIndex >= 0 && tabIndex < tabIds.Count)
            {
                tabId = tabIds[tabIndex];
            }
        }

        target = new BrowserTabGroupCommandContext(
            categoryId,
            tabId,
            requestedGroupId,
            requested?.DisplayName);
        return !string.IsNullOrWhiteSpace(categoryId);
    }

    private bool IsBrowserTabGroupMemberTargetAvailable(BrowserTabGroupCommandContext target)
    {
        if (target.TabId == Guid.Empty
            || _browser.Workspace.FindCategorySnapshot(target.CategoryId) is not { } category)
        {
            return false;
        }

        if (string.Equals(category.Id, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return _browser.Workspace.TabStates.Any(tab => tab.Id == target.TabId);
        }

        return _persistence.FindStoredCategory(category.Id)?.OpenTabs.Any(tab => tab.TabId == target.TabId) == true;
    }

    private IReadOnlySet<Guid> GetCurrentBrowserTabGroupBlockedTabIds(
        string categoryId,
        IReadOnlySet<Guid>? capturedBlockedTabIds)
    {
        return _unifiedSearchResultActivation?.GetBlockedResultTabIds(categoryId)
            ?? capturedBlockedTabIds
            ?? new HashSet<Guid>();
    }

    private bool CanContinueBrowserTabGroupRename(
        BrowserTabGroupCommandInteractionRequest request,
        Guid groupId)
    {
        if (request.GroupId is { } requestedGroupId && requestedGroupId != groupId)
        {
            return false;
        }

        if (request.Groups is { } offeredGroups
            && !offeredGroups.Any(group => group.Id == groupId
                && string.Equals(group.CategoryId, request.CategoryId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        BrowserTabGroup? currentGroup = _persistence.FindBrowserTabGroup(groupId);
        return currentGroup != null
            && string.Equals(currentGroup.CategoryId, request.CategoryId, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanContinueBrowserTabGroupAdd(
        BrowserTabGroupCommandInteractionRequest request,
        Guid groupId)
    {
        if (request.Groups is { } offeredGroups
            && !offeredGroups.Any(group => group.Id == groupId
                && string.Equals(group.CategoryId, request.CategoryId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        BrowserTabGroup? currentGroup = _persistence.FindBrowserTabGroup(groupId);
        return currentGroup != null
            && string.Equals(currentGroup.CategoryId, request.CategoryId, StringComparison.OrdinalIgnoreCase);
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

    private CommandApplicationResult ExecuteCategoryDelete(bool allowed, CommandExecutionContext context)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        if (context.CategoryIds is not { Count: > 0 } categoryIds)
        {
            return BeginCategoryDeleteInteraction(allowed, context.CategoryId);
        }

        if (!context.CategoryDeleteConfirmed)
        {
            return CommandApplicationResult.NotHandled;
        }

        _categories.EnsureConfiguration();
        string[] targets = categoryIds
            .Where(static categoryId => !string.IsNullOrWhiteSpace(categoryId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(categoryId => _browser.Workspace.FindCategorySnapshot(categoryId) != null)
            .ToArray();
        if (targets.Length == 0)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserCategoryRemovalExecution execution = _categories.ExecuteRemoveAndSwitch(
            targets,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(
            CommandApplicationEffectKind.ApplyCategoryDelete,
            textValue: context.CategoryResultMessage ?? $"カテゴリを削除しました: {targets.Length} 件",
            categoryDelete: execution);
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

        int targetTabIndex = request.TargetTabIndex;
        if (request.TargetBrowserTabId is { } targetTabId
            && !TryResolveTabIndexById(targetTabId, out targetTabIndex))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        UnifiedFilterFindCriteria criteria = response.Criteria.Clone();
        if (response.Mode != UnifiedFilterFindMode.FilterCurrentTab)
        {
            return Effect(
                CommandApplicationEffectKind.RunUnifiedFilterFind,
                unifiedSearch: new UnifiedFilterFindInteractionRequest(
                    targetTabIndex,
                    request.RootPath,
                    criteria,
                    response.Mode));
        }

        CommandApplicationShellState state = _shell.CaptureState();
        bool isActive = targetTabIndex == _browser.Workspace.ActiveTabIndex;
        BrowserUnifiedFilterExecution execution = _tabs.ExecuteUnifiedFilter(
            targetTabIndex,
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
            intValue: targetTabIndex,
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
            BrowserDriveRootNavigationContinuation driveRoot =>
                ExecutePreparedDriveRootNavigation(driveRoot.Preparation, state),
            _ => CommandApplicationResult.NotHandled
        };
    }

    private CommandApplicationResult ExecutePreparedDriveRootNavigation(
        BrowserDriveRootNavigationPreparation preparation,
        CommandApplicationShellState state)
    {
        BrowserDriveRootNavigationExecution execution = _navigation.ExecutePreparedDriveRoot(
            preparation,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState,
            confirmedDerivedTabCreation: true);
        return Effect(
            CommandApplicationEffectKind.ApplyDirectoryNavigation,
            directoryNavigation: execution.Navigation,
            boolValue: true);
    }

    private CommandApplicationResult ExecuteLogdiskContinuation(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        BrowserPathEntryNavigationResult navigation = _browser.ResolvePathEntry(selectedPath);
        if (navigation.TargetKind == BrowserPathEntryTargetKind.None)
        {
            return Effect(
                CommandApplicationEffectKind.ApplyLogdisk,
                pathEntry: navigation);
        }

        BrowserNavigationCoordinator.DirectoryNavigationRequest? navigationRequest =
            navigation.TargetKind == BrowserPathEntryTargetKind.File
                ? _navigation.CreateMarkedItemNavigationRequest(navigation.ResolvedPath, requireVisibleTarget: true)
                : _browser.CreateNavigationRequest(
                    navigation.ResolvedPath,
                    isHistoryNavigation: false,
                    suppressRecent: false);
        if (navigationRequest == null)
        {
            return Effect(
                CommandApplicationEffectKind.ApplyLogdisk,
                pathEntry: new BrowserPathEntryNavigationResult
                {
                    TargetKind = BrowserPathEntryTargetKind.None,
                    StatusMessage = "指定したファイルの親フォルダーへ移動できませんでした。"
                });
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserNavigationPreparation preparation = _navigation.PrepareDirectoryNavigation(
            navigationRequest,
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

        int tabIndex = request.TabIndex;
        if (request.TargetBrowserTabId is { } targetTabId)
        {
            string requestedCategoryId = _browser.Workspace.ResolveCategoryId(request.CategoryId);
            string activeCategoryId = _browser.Workspace.ResolveCategoryId(_browser.Workspace.ActiveCategoryId);
            if (!string.Equals(activeCategoryId, requestedCategoryId, StringComparison.OrdinalIgnoreCase)
                || _browser.Workspace.FindCategorySnapshot(request.CategoryId) is null
                || !TryResolveTabIndexById(targetTabId, out tabIndex))
            {
                return Effect(CommandApplicationEffectKind.NoOp);
            }

            BrowserTabCloseDecision currentDecision = _tabs.EvaluateClose(tabIndex);
            if (currentDecision.Kind != BrowserTabCloseDecisionKind.RequiresCategoryRemoval
                || !string.Equals(
                    _browser.Workspace.ResolveCategoryId(currentDecision.CategoryId),
                    requestedCategoryId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Effect(CommandApplicationEffectKind.NoOp);
            }
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabCloseConfirmationExecution execution = _categories.ContinueTabCloseAfterCategoryConfirmation(
            tabIndex,
            removeCategory: true,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(
            CommandApplicationEffectKind.ApplyTabCloseConfirmation,
            intValue: tabIndex,
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
        bool recordDirectoryMoveHistory = false,
        string? directoryMissingMessage = null)
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
            messageValue: preparation.Decision.Kind == BrowserNavigationWorkflowDecisionKind.DirectoryMissing
                ? directoryMissingMessage
                : null,
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

    private CommandApplicationResult ExecuteDriveRootNavigation(bool allowed)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        BrowserDriveRootNavigationPreparation preparation = _navigation.PrepareDriveRoot(state.CurrentTab);
        string? missingLockedRootMessage = preparation.LockedRoot.Kind == BrowserLockedRootResolutionKind.Missing
            ? "固定タブのルートが見つかりません。通常のルートへ移動します。"
            : null;
        if (!preparation.TargetChanged || preparation.Navigation is not { } navigationPreparation)
        {
            return missingLockedRootMessage == null
                ? Effect(CommandApplicationEffectKind.NoOp)
                : Effect(CommandApplicationEffectKind.ShowMessage, textValue: missingLockedRootMessage);
        }

        if (navigationPreparation.RequiresDerivedTabConfirmation)
        {
            return Interaction(new BrowserDerivedTabNavigationInteractionRequest(
                new BrowserDriveRootNavigationContinuation(preparation),
                PreInteractionMessage: missingLockedRootMessage));
        }

        BrowserDriveRootNavigationExecution execution = _navigation.ExecutePreparedDriveRoot(
            preparation,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState,
            confirmedDerivedTabCreation: true);
        return Effect(
            CommandApplicationEffectKind.ApplyDirectoryNavigation,
            directoryNavigation: execution.Navigation,
            boolValue: true,
            messageValue: missingLockedRootMessage);
    }

    private CommandApplicationResult ExecuteTargetPathNavigation(bool allowed, CommandExecutionContext context)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserDirectoryNavigationCommandContext? navigation = context.BrowserDirectoryNavigation;
        string? directoryMissingMessage = null;
        if (context.BrowserMarkedItemNavigation is { } markedItemNavigation)
        {
            BrowserNavigationCoordinator.DirectoryNavigationRequest? markedItemRequest =
                _navigation.CreateMarkedItemNavigationRequest(
                    markedItemNavigation.FullPath,
                    markedItemNavigation.RequireVisibleTarget);
            if (markedItemRequest == null)
            {
                return CommandApplicationResult.NotHandled;
            }

            CommandApplicationShellState markedItemState = _shell.CaptureState();
            return ExecuteDirectoryCommandNavigation(
                markedItemRequest,
                markedItemState,
                clearPreview: false,
                recordDirectoryMoveHistory: markedItemNavigation.RequireVisibleTarget,
                directoryMissingMessage: "対象フォルダが見つかりません。");
        }

        string? path = navigation?.TargetPath ?? context.ContextTargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return CommandApplicationResult.NotHandled;
        }

        if (navigation != null)
        {
            CommandApplicationShellState navigationState = _shell.CaptureState();
            return ExecuteDirectoryCommandNavigation(
                _browser.CreateNavigationRequest(
                    navigation.TargetPath,
                    navigation.FocusTargetName,
                    navigation.IsHistoryNavigation,
                    navigation.SuppressRecent),
                navigationState,
                clearPreview: navigation.ClearPreview,
                refreshPreview: navigation.RefreshPreview,
                recordDirectoryMoveHistory: navigation.RecordDirectoryMoveHistory,
                directoryMissingMessage: directoryMissingMessage);
        }

        BrowserPathEntryNavigationResult target = _browser.ResolvePathEntry(path);
        if (target.TargetKind != BrowserPathEntryTargetKind.Directory)
        {
            return Effect(CommandApplicationEffectKind.ApplyLogdisk, pathEntry: target);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        return ExecuteDirectoryCommandNavigation(
            _browser.CreateNavigationRequest(target.ResolvedPath, isHistoryNavigation: false, suppressRecent: false),
            state,
            clearPreview: false,
            recordDirectoryMoveHistory: true);
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

    private CommandApplicationResult ExecuteBrowserCursorMove(
        bool allowed,
        BrowserCursorNavigationCommandContext? context)
    {
        if (!allowed || context == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        int total = state.TotalItemCount > 0 ? state.TotalItemCount : state.BrowserItemCount;
        if (total <= 0)
        {
            return Effect(CommandApplicationEffectKind.CursorNoOp);
        }

        int targetIndex = Math.Clamp(context.TargetGlobalIndex, 0, total - 1);
        BrowserCursorNavigationExecution execution = _navigation.ExecuteCursorNavigation(
            targetIndex,
            state.ItemsPerPage,
            state.IsBusy,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyCursorNavigation, cursorNavigation: execution);
    }

    private CommandApplicationResult ExecuteBrowserMarkToggle(
        bool allowed,
        BrowserMarkToggleCommandContext? context)
    {
        if (!allowed || context == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        if (context.TargetPath == null && state.CurrentItemName == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        string? path = context.TargetPath ?? (state.CurrentItemName == ".." ? null : state.CurrentItemPath);
        BrowserSelectionMarkExecution? markExecution = null;
        if (path != null)
        {
            bool adding = context.RequestedMarkedState ?? !_browser.Selection.Contains(path);
            if (adding &&
                context.SkipMissingTargetWhenAdding &&
                !File.Exists(path) &&
                !Directory.Exists(path))
            {
                markExecution = new BrowserSelectionMarkExecution(default, default, SkippedMissingTarget: true);
            }
            else
            {
                markExecution = _tabs.ExecuteToggleMark(path, adding);
            }
        }

        BrowserCursorNavigationExecution? cursorExecution = null;
        int total = state.TotalItemCount > 0 ? state.TotalItemCount : state.BrowserItemCount;
        if (context.MoveNext && total > 0 && _browser.CursorIndex < total - 1)
        {
            cursorExecution = _navigation.ExecuteCursorNavigation(
                _browser.CursorIndex + 1,
                state.ItemsPerPage,
                state.IsBusy,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState);
        }

        return Effect(
            CommandApplicationEffectKind.ApplyBrowserMarkToggle,
            textValue: path,
            cursorNavigation: cursorExecution,
            browserMarkExecution: markExecution);
    }

    private CommandApplicationResult ExecuteBrowserMarkInvert(
        bool allowed,
        BrowserBulkMarkCommandContext? context)
    {
        if (!allowed || context is null)
        {
            return CommandApplicationResult.NotHandled;
        }

        IReadOnlyList<string> targets = _shell.GetBulkMarkTargetPaths(context.IncludeDirectories);
        if (targets.Count == 0)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        BrowserBulkMarkTransition transition = _tabs.ExecuteInvertBulkMarksAndSync(targets);
        return Effect(
            CommandApplicationEffectKind.ApplyBulkMarks,
            textValue: "invert",
            intValue: targets.Count,
            boolValue: context.IncludeDirectories,
            bulkMarks: transition);
    }

    private CommandApplicationResult ExecuteBrowserMarkRangeAdd(
        bool allowed,
        BrowserMarkRangeAddCommandContext? context)
    {
        if (!allowed || context == null)
        {
            return CommandApplicationResult.NotHandled;
        }
        if (context.TargetPaths.Count == 0)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        int addedCount = _tabs.AddMarksAndSync(context.TargetPaths);
        return Effect(CommandApplicationEffectKind.ApplyBrowserMarkRangeAdd, intValue: addedCount);
    }

    private CommandApplicationResult ExecuteMarkSlotSave(
        bool allowed,
        BrowserMarkSlotSaveCommandContext? context)
    {
        if (!allowed || _markSlots == null || context == null || !IsValidMarkSlotNumber(context.SlotNumber))
        {
            return CommandApplicationResult.NotHandled;
        }

        MarkSlotActionResult result;
        if (context.Scope == BrowserMarkSlotSaveScope.CurrentTab)
        {
            if (context.ActiveTab == null)
            {
                return CommandApplicationResult.NotHandled;
            }

            result = _markSlots.SaveCurrentTab(
                context.SlotNumber,
                context.DisplayName,
                context.Paths ?? Array.Empty<string>(),
                context.ActiveTab,
                context.CategoryName,
                context.TabDisplayName,
                _markSlots.GetSlots().Slots.Count);
        }
        else
        {
            string scope = context.Scope == BrowserMarkSlotSaveScope.CurrentCategory
                ? MarkSlotSourceScopes.CurrentCategory
                : MarkSlotSourceScopes.Workspace;
            string scopeLabel = context.Scope == BrowserMarkSlotSaveScope.CurrentCategory
                ? "現在カテゴリ"
                : "全Workspace";
            BrowserMarkSlotSaveAggregation aggregation = new(
                context.SourceScope ?? scope,
                context.SourceScopeLabel ?? scopeLabel,
                context.SourceCategoryId,
                context.SourceCategoryName,
                context.CategoryCount,
                context.TabCount,
                context.RawMarkCount,
                context.Paths ?? Array.Empty<string>());
            result = _markSlots.SaveAggregation(
                context.SlotNumber,
                context.DisplayName,
                aggregation,
                _markSlots.GetSlots().Slots.Count);
        }

        return Effect(CommandApplicationEffectKind.ApplyMarkSlotAction, markSlotAction: result);
    }

    private CommandApplicationResult ExecuteMarkSlotRename(
        bool allowed,
        BrowserMarkSlotMutationCommandContext? context)
    {
        if (!allowed || _markSlots == null || context == null || !IsValidMarkSlotNumber(context.SlotNumber))
        {
            return CommandApplicationResult.NotHandled;
        }

        return Effect(
            CommandApplicationEffectKind.ApplyMarkSlotAction,
            markSlotAction: _markSlots.Rename(context.SlotNumber, context.DisplayName, _markSlots.GetSlots().Slots.Count));
    }

    private CommandApplicationResult ExecuteMarkSlotDelete(
        bool allowed,
        BrowserMarkSlotMutationCommandContext? context)
    {
        if (!allowed || _markSlots == null || context == null || !IsValidMarkSlotNumber(context.SlotNumber))
        {
            return CommandApplicationResult.NotHandled;
        }

        return Effect(
            CommandApplicationEffectKind.ApplyMarkSlotAction,
            markSlotAction: _markSlots.Delete(context.SlotNumber, _markSlots.GetSlots().Slots.Count));
    }

    private CommandApplicationResult ExecuteMarkSlotRemoveItems(
        bool allowed,
        BrowserMarkSlotMutationCommandContext? context)
    {
        if (!allowed || _markSlots == null || context == null || !IsValidMarkSlotNumber(context.SlotNumber) || context.Paths == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        return Effect(
            CommandApplicationEffectKind.ApplyMarkSlotAction,
            markSlotAction: _markSlots.RemoveItems(context.SlotNumber, context.Paths, _markSlots.GetSlots().Slots.Count));
    }

    private CommandApplicationResult ExecuteMarkSlotExport(
        bool allowed,
        BrowserMarkSlotTransferCommandContext? context)
    {
        if (!allowed || _markSlots == null || context?.SlotNumber is not int slotNumber ||
            !IsValidMarkSlotNumber(slotNumber) || string.IsNullOrWhiteSpace(context.FilePath))
        {
            return CommandApplicationResult.NotHandled;
        }

        bool exported = _markSlots.TryExportSlot(context.FilePath, slotNumber, out string errorMessage);
        return Effect(
            CommandApplicationEffectKind.ApplyMarkSlotAction,
            markSlotAction: new MarkSlotActionResult(
                exported,
                exported
                    ? $"マークスロット {slotNumber} をエクスポートしました"
                    : errorMessage));
    }

    private CommandApplicationResult BeginMarkSlotImport(
        bool allowed,
        string commandId,
        BrowserMarkSlotImportCommandContext? context)
    {
        if (!allowed || _markSlots == null || context?.SlotNumber is not int slotNumber ||
            !IsValidMarkSlotNumber(slotNumber) || string.IsNullOrWhiteSpace(context.FilePath))
        {
            return CommandApplicationResult.NotHandled;
        }

        if (!_markSlots.TryImportSlot(
                context.FilePath,
                out MarkSlotEntry? importedSlot,
                out string errorMessage,
                out string? warningMessage) || importedSlot == null)
        {
            return Effect(
                CommandApplicationEffectKind.ApplyMarkSlotAction,
                markSlotAction: new MarkSlotActionResult(false, errorMessage));
        }

        return Interaction(new BrowserMarkSlotImportInteractionRequest(
            commandId,
            slotNumber,
            context.FilePath,
            importedSlot,
            null,
            warningMessage));
    }

    private CommandApplicationResult ExecuteMarkSlotsExportAll(
        bool allowed,
        BrowserMarkSlotTransferCommandContext? context)
    {
        if (!allowed || _markSlots == null || string.IsNullOrWhiteSpace(context?.FilePath))
        {
            return CommandApplicationResult.NotHandled;
        }

        int slotCount = _markSlots.GetSlots().Slots.Count;
        bool exported = _markSlots.TryExportAllSlots(context.FilePath, slotCount, out string errorMessage);
        return Effect(
            CommandApplicationEffectKind.ApplyMarkSlotAction,
            markSlotAction: new MarkSlotActionResult(
                exported,
                exported
                    ? $"全マークスロットをエクスポートしました ({slotCount}スロット)"
                    : errorMessage));
    }

    private CommandApplicationResult BeginMarkSlotsImportAll(
        bool allowed,
        string commandId,
        BrowserMarkSlotImportCommandContext? context)
    {
        if (!allowed || _markSlots == null || string.IsNullOrWhiteSpace(context?.FilePath))
        {
            return CommandApplicationResult.NotHandled;
        }

        int slotCount = _markSlots.GetSlots().Slots.Count;
        if (!_markSlots.TryImportAllSlots(
                context.FilePath,
                slotCount,
                out MarkSlotStore? importedStore,
                out string errorMessage,
                out string? warningMessage) || importedStore == null)
        {
            return Effect(
                CommandApplicationEffectKind.ApplyMarkSlotAction,
                markSlotAction: new MarkSlotActionResult(false, errorMessage));
        }

        return Interaction(new BrowserMarkSlotImportInteractionRequest(
            commandId,
            null,
            context.FilePath,
            null,
            importedStore,
            warningMessage));
    }

    private CommandApplicationResult ContinueMarkSlotImport(
        BrowserMarkSlotImportInteractionRequest request,
        BrowserMarkSlotImportInteractionResponse response)
    {
        if (!response.Accepted)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        if (_markSlots == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        MarkSlotActionResult result;
        if (string.Equals(request.CommandId, CommandIds.BrowserMarkSlotImport, StringComparison.Ordinal))
        {
            if (request.SlotNumber is not int slotNumber || request.ImportedSlot == null)
            {
                return CommandApplicationResult.NotHandled;
            }
            result = _markSlots.ImportSlot(slotNumber, request.ImportedSlot, _markSlots.GetSlots().Slots.Count);
        }
        else if (string.Equals(request.CommandId, CommandIds.BrowserMarkSlotsImportAll, StringComparison.Ordinal))
        {
            if (request.ImportedStore == null)
            {
                return CommandApplicationResult.NotHandled;
            }
            int slotCount = _markSlots.GetSlots().Slots.Count;
            bool replaced = _markSlots.ReplaceSlots(request.ImportedStore, slotCount);
            result = new MarkSlotActionResult(
                replaced,
                replaced
                    ? $"全マークスロットをインポートしました ({slotCount}スロット置換)"
                    : "全マークスロットを保存できませんでした。ストレージを確認してください。");
        }
        else
        {
            return CommandApplicationResult.NotHandled;
        }

        if (result.Success && !string.IsNullOrWhiteSpace(request.WarningMessage))
        {
            result = result with { Message = $"{result.Message} / {request.WarningMessage}" };
        }

        return Effect(CommandApplicationEffectKind.ApplyMarkSlotAction, markSlotAction: result);
    }

    private CommandApplicationResult ExecuteMarkSlotClipboardImport(
        bool browser,
        bool busy,
        BrowserMarkSlotClipboardImportCommandContext? context)
    {
        if (!browser || _markSlots == null || context == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        MarkSlotClipboardImportResult? import = null;
        MarkSlotClipboardActionResult result;
        if (busy)
        {
            result = BrowserMarkSlotWorkflowApplicationCoordinator.BuildClipboardImportFailure(
                "ファイル操作中はマークを変更できません。",
                context.RepositoryRoot);
        }
        else if (!string.IsNullOrWhiteSpace(context.ClipboardError))
        {
            result = BrowserMarkSlotWorkflowApplicationCoordinator.BuildClipboardImportFailure(
                context.ClipboardError,
                context.RepositoryRoot);
        }
        else
        {
            import = MarkSlotClipboardImportService.Extract(
                context.ClipboardText,
                context.CurrentPath,
                context.RepositoryRoot,
                AppContext.BaseDirectory);
            result = import.IsSuccess
                ? _markSlots.ApplyClipboardReplacement(
                    import,
                    context.RepositoryRoot,
                    _shell.CaptureState().CurrentTab)
                : BrowserMarkSlotWorkflowApplicationCoordinator.BuildClipboardImportFailure(
                    import,
                    context.RepositoryRoot);
        }

        return Effect(
            CommandApplicationEffectKind.ApplyMarkSlotClipboard,
            boolValue: result.Success,
            markSlotClipboardAction: result,
            markSlotClipboardImport: import);
    }

    private CommandApplicationResult ExecuteMarkSlotSetOperationPreview(
        bool allowed,
        BrowserMarkSlotSetOperationPreviewCommandContext? context)
    {
        if (!allowed || _markSlots == null || context == null ||
            !IsValidMarkSlotNumber(context.SlotANumber) || !IsValidMarkSlotNumber(context.SlotBNumber) ||
            !IsValidMarkSlotSetOperation(context.OperationKind))
        {
            return CommandApplicationResult.NotHandled;
        }

        MarkSlotSetOperationPreviewResult preview = _markSlots.BuildSetOperationPreview(
            context.SlotANumber,
            context.SlotBNumber,
            context.OperationKind,
            _shell.CaptureState().CurrentPath,
            context.ExistingPaths,
            context.ExistingDirectoryPaths);
        return Effect(
            CommandApplicationEffectKind.NoOp,
            markSlotSetOperationPreview: preview);
    }

    private CommandApplicationResult ExecuteMarkSlotSetOperationApply(
        bool allowed,
        MarkSlotSetOperationPreviewResult? preview)
    {
        if (!allowed || _markSlots == null || preview == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        MarkSlotActionResult result = preview.ResultCount > 0
            ? _markSlots.ApplySetOperationToCurrentTab(preview, _shell.CaptureState().CurrentTab)
            : new MarkSlotActionResult(false, "0件の演算結果は現在タブへ適用できません。");
        return Effect(CommandApplicationEffectKind.ApplyMarkSlotAction, markSlotAction: result);
    }

    private CommandApplicationResult ExecuteMarkSlotSetOperationSave(
        bool allowed,
        BrowserMarkSlotSetOperationSaveCommandContext? context)
    {
        if (!allowed || _markSlots == null || context?.Request == null ||
            !IsValidMarkSlotNumber(context.Request.TargetSlotNumber) ||
            !IsValidMarkSlotSetOperation(context.Request.OperationKind))
        {
            return CommandApplicationResult.NotHandled;
        }

        MarkSlotActionResult result = _markSlots.SaveSetOperation(
            context.Request,
            context.DisplayName,
            _markSlots.GetSlots().Slots.Count);
        return Effect(CommandApplicationEffectKind.ApplyMarkSlotAction, markSlotAction: result);
    }

    private static bool IsValidMarkSlotSetOperation(string operationKind) => operationKind is
        MarkSlotSetOperations.Or or
        MarkSlotSetOperations.And or
        MarkSlotSetOperations.AMinusB or
        MarkSlotSetOperations.BMinusA or
        MarkSlotSetOperations.Xor;

    private CommandApplicationResult ExecuteBrowserMarkClear(
        bool allowed,
        BrowserMarkClearCommandContext? context)
    {
        if (!allowed || context == null)
        {
            return CommandApplicationResult.NotHandled;
        }

        BrowserTabState activeTab = context.ActiveTab.Clone();
        BrowserMarksClearTransition clear;
        string message;
        switch (context.Scope)
        {
            case BrowserMarkClearScope.CurrentTab:
                int clearedCount = _tabs.ClearMarksAndSync();
                activeTab.MarkedPaths = _browser.Selection.Snapshot().ToList();
                if (clearedCount > 0)
                {
                    _tabs.PersistAfterMarkMutation(activeTab);
                }
                clear = new BrowserMarksClearTransition(clearedCount > 0, clearedCount);
                message = context.StatusMessageOverride ?? $"現在タブのマークをすべて解除しました ({clearedCount}件)。";
                break;
            case BrowserMarkClearScope.CurrentCategory:
                string categoryId = string.IsNullOrWhiteSpace(context.CategoryId)
                    ? _browser.Workspace.ActiveCategoryId ?? BrowserTabSettings.DefaultCategoryId
                    : context.CategoryId;
                clear = _tabs.ClearCategoryMarks(categoryId, activeTab).Clear;
                message = $"カテゴリ '{categoryId}' のマークをすべて解除しました ({clear.ClearedCount}件)。";
                break;
            case BrowserMarkClearScope.Workspace:
                clear = _tabs.ClearAllMarks(activeTab).Clear;
                message = $"Workspace 全域の全マークを解除しました ({clear.ClearedCount}件)。";
                break;
            default:
                return CommandApplicationResult.NotHandled;
        }

        return Effect(
            CommandApplicationEffectKind.ApplyBrowserMarkClear,
            messageValue: message,
            boolValue: context.PreservePendingEscExitState,
            markClear: clear);
    }

    private bool IsValidMarkSlotNumber(int slotNumber)
    {
        int count = _markSlots?.GetSlots().Slots.Count ?? 0;
        return slotNumber >= 1 && slotNumber <= count;
    }

    private static CommandApplicationResult ExecuteImageViewerCommand(
        string commandId,
        CommandExecutionContext context)
    {
        if (context.Scope != CommandScope.ImageViewer ||
            context.ImageViewerCommand is not { } viewer ||
            viewer.ViewerInstanceId == Guid.Empty)
        {
            return CommandApplicationResult.NotHandled;
        }

        ImageViewerCommandAction? action = commandId switch
        {
            CommandIds.ImageViewerOpen => ImageViewerCommandAction.Open,
            CommandIds.ImageViewerSaveAs => ImageViewerCommandAction.SaveAs,
            CommandIds.ImageViewerClose => ImageViewerCommandAction.Close,
            CommandIds.ImageViewerCopy => ImageViewerCommandAction.Copy,
            CommandIds.ImageViewerPaste => ImageViewerCommandAction.Paste,
            CommandIds.ImageViewerPasteSelectionStretch => ImageViewerCommandAction.PasteSelectionStretch,
            CommandIds.ImageViewerPasteSelectionOriginal => ImageViewerCommandAction.PasteSelectionOriginal,
            CommandIds.ImageViewerCopySvg => ImageViewerCommandAction.CopySvg,
            CommandIds.ImageViewerQuantize => ImageViewerCommandAction.Quantize,
            CommandIds.ImageViewerReset => ImageViewerCommandAction.Reset,
            CommandIds.ImageViewerRotateRight => ImageViewerCommandAction.RotateRight,
            CommandIds.ImageViewerRotateLeft => ImageViewerCommandAction.RotateLeft,
            CommandIds.ImageViewerFlipHorizontal => ImageViewerCommandAction.FlipHorizontal,
            CommandIds.ImageViewerFlipVertical => ImageViewerCommandAction.FlipVertical,
            CommandIds.ImageViewerShowInfo => ImageViewerCommandAction.ShowInfo,
            CommandIds.ImageViewerUndo => ImageViewerCommandAction.Undo,
            CommandIds.ImageViewerRedo => ImageViewerCommandAction.Redo,
            CommandIds.ImageViewerZoomIn => ImageViewerCommandAction.ZoomIn,
            CommandIds.ImageViewerZoomOut => ImageViewerCommandAction.ZoomOut,
            CommandIds.ImageViewerZoomReset => ImageViewerCommandAction.ZoomReset,
            CommandIds.ImageViewerFullscreenToggle => ImageViewerCommandAction.FullscreenToggle,
            CommandIds.ImageViewerFullscreenExit => ImageViewerCommandAction.FullscreenExit,
            CommandIds.ImageViewerVideoSeekToStart => ImageViewerCommandAction.VideoSeekToStart,
            CommandIds.ImageViewerVideoSeekBackward => ImageViewerCommandAction.VideoSeekBackward,
            CommandIds.ImageViewerVideoSeekForward => ImageViewerCommandAction.VideoSeekForward,
            CommandIds.ImageViewerVideoSeekToPosition => ImageViewerCommandAction.VideoSeekToPosition,
            CommandIds.ImageViewerVideoExternalPlay => ImageViewerCommandAction.VideoExternalPlay,
            CommandIds.ImageViewerVideoSaveFrame => ImageViewerCommandAction.VideoSaveFrame,
            CommandIds.ImageViewerVideoSetPlaybackRate => ImageViewerCommandAction.VideoSetPlaybackRate,
            CommandIds.ImageViewerResize => ImageViewerCommandAction.Resize,
            CommandIds.ImageViewerMosaic => ImageViewerCommandAction.Mosaic,
            CommandIds.ImageViewerBlur => ImageViewerCommandAction.Blur,
            CommandIds.ImageViewerTone => ImageViewerCommandAction.Tone,
            _ => null
        };

        return action is { } resolved
            ? Effect(
                CommandApplicationEffectKind.DispatchImageViewerCommand,
                runtimeTargetId: viewer.ViewerInstanceId,
                imageViewerAction: resolved,
                imageViewerCommand: viewer)
            : CommandApplicationResult.NotHandled;
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

        Guid navigationSessionId = _archiveListNavigation.BeginSession(
            archivePath,
            result.SevenZipPath,
            result.Entries);
        return Interaction(new ArchiveListCommandInteractionRequest(
            archivePath,
            result,
            state.CurrentPath,
            state.IsReadOnlyBrowserTab,
            state.DateFormat,
            state.SizeFormat,
            navigationSessionId));
    }

    private CommandApplicationResult ExecuteArchiveListContinuation(
        ArchiveListCommandInteractionRequest request,
        ArchiveListCommandInteractionResponse response)
    {
        _archiveListNavigation.EndSession(request.NavigationSessionId);
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

    private CommandApplicationResult ExecuteArchiveListNavigation(ArchiveListNavigationCommandContext? request)
    {
        if (!_archiveListNavigation.TryPrepareNavigation(request, out ArchiveListNavigationExecution proposal))
        {
            return CommandApplicationResult.NotHandled;
        }

        return Effect(
            CommandApplicationEffectKind.ApplyArchiveListNavigation,
            archiveListNavigation: proposal);
    }

    private CommandApplicationResult CompleteArchiveListNavigation(
        ArchiveListNavigationExecution proposal,
        bool uiApplied)
    {
        ArchiveListNavigationCommitResult result = _archiveListNavigation.CompleteNavigation(proposal, uiApplied);
        return result switch
        {
            ArchiveListNavigationCommitResult.Committed => Effect(
                CommandApplicationEffectKind.ArchiveListNavigationCommitted,
                archiveListNavigation: proposal),
            ArchiveListNavigationCommitResult.UiRejected => CommandApplicationResult.NotHandled,
            _ => Effect(
                CommandApplicationEffectKind.RollbackArchiveListNavigation,
                archiveListNavigation: proposal)
        };
    }

    private CommandApplicationResult ExecuteArchiveTextPreview(ArchiveTextPreviewCommandContext? request)
    {
        if (!_archiveListNavigation.TryCreateTextPreview(request, out ArchiveTextPreviewExecution execution))
        {
            return CommandApplicationResult.NotHandled;
        }

        return Effect(
            CommandApplicationEffectKind.ApplyArchiveTextPreview,
            archiveTextPreview: execution);
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

    private CommandApplicationResult ExecutePlaybackLockedOpen(bool allowed, SelectionResult? selectionSnapshot)
    {
        if (!allowed) return CommandApplicationResult.NotHandled;
        CommandApplicationShellState state = _shell.CaptureState();
        BrowserOpenSelection selection = BrowserOpenSelectionResolver.Resolve(
            selectionSnapshot,
            state.CurrentItemName,
            state.CurrentItemPath);
        if (selection.Kind != BrowserOpenSelectionKind.SingleFile || string.IsNullOrWhiteSpace(selection.Paths[0]))
            return Effect(CommandApplicationEffectKind.NoOp);

        ViewerOpenRequest? request = _viewerWorkflow.CreatePlaybackLockedOpenRequest(selection.Paths[0]);
        return request is { } openRequest
            ? Effect(CommandApplicationEffectKind.ApplyViewerOpenRequest, viewerOpenRequest: openRequest)
            : Effect(CommandApplicationEffectKind.NoOp, messageValue: "再生モードで開ける画像または動画を選択してください。");
    }

    private CommandApplicationResult ExecuteToggleMediaViewerPresentationMode(CommandExecutionContext context)
    {
        if (context.Scope != CommandScope.ImageViewer
            || context.ImageViewerCommand is not { } viewer
            || viewer.ViewerInstanceId == Guid.Empty)
        {
            return CommandApplicationResult.NotHandled;
        }

        (MidFD.ViewerIpc.ViewerPresentationMode mode, int generation) =
            _viewerWorkflow.ToggleMediaViewerPresentationMode(viewer.ViewerInstanceId);
        return Effect(
            CommandApplicationEffectKind.SetViewerPresentationMode,
            intValue: (int)mode,
            longValue: generation,
            runtimeTargetId: viewer.ViewerInstanceId);
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

        SelectionResult? markedSelection = selectionSnapshot is { HasMarkedSelection: true }
            ? selectionSnapshot
            : ResolveCommandSelection(null);
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

    private CommandApplicationResult ExecuteFileOperationCancel()
    {
        FileOperationCancellationRequest request = _fileOperations.RequestActiveCancellation();
        return request.Status switch
        {
            FileOperationCancellationRequestStatus.Inactive => Effect(CommandApplicationEffectKind.NoOp),
            FileOperationCancellationRequestStatus.Requested => Effect(
                CommandApplicationEffectKind.FileOperationCancellationRequested,
                textValue: FileOperationPresentationHelper.GetCancelRequestedMessage(request.OperationName)),
            FileOperationCancellationRequestStatus.AlreadyRequested => Effect(
                CommandApplicationEffectKind.ShowMessage,
                textValue: FileOperationPresentationHelper.GetBusyBlockedMessage(
                    request.OperationName,
                    canCancel: true,
                    isCancelRequested: true)),
            _ => Effect(
                CommandApplicationEffectKind.ShowMessage,
                textValue: FileOperationPresentationHelper.GetBusyBlockedMessage(
                    request.OperationName,
                    canCancel: false,
                    isCancelRequested: false))
        };
    }

    private bool TryGetFileOperationBusy(out bool isBusy)
    {
        if (_fileOperations is not null)
        {
            isBusy = _fileOperations.IsBusy;
            return true;
        }

        if (_busyStatePort is not null)
        {
            isBusy = _busyStatePort.IsFileOperationBusy;
            return true;
        }

        isBusy = false;
        return false;
    }

    private CommandApplicationResult ExecuteAdjacentTabWithinUnifiedSearch(
        bool allowed,
        int delta,
        BrowserTabNavigationCommandContext? request)
    {
        if (!allowed || request is not
            {
                UnifiedSearchSessionId: { } sessionId,
                UnifiedSearchSourceCategoryId: { Length: > 0 } sourceCategoryId,
                UnifiedSearchSourceTabId: { } sourceTabId
            } ||
            !string.Equals(
                _browser.Workspace.ActiveCategoryId,
                sourceCategoryId,
                StringComparison.OrdinalIgnoreCase))
        {
            return CommandApplicationResult.NotHandled;
        }
        if (_fileOperations.IsClipboardBusy)
        {
            return Effect(
                CommandApplicationEffectKind.ShowMessage,
                textValue: FileOperationPresentationHelper.GetBusyBlockedMessage(
                    _fileOperations.ActiveOperationName,
                    canCancel: _fileOperations.CanCancel,
                    isCancelRequested: _fileOperations.CancellationTokenSource?.IsCancellationRequested ?? false));
        }

        IReadOnlyList<BrowserTabState> tabs = _browser.Workspace.TabStates;
        int sourceIndex = -1;
        for (int index = 0; index < tabs.Count; index++)
        {
            if (tabs[index].Id == sourceTabId)
            {
                sourceIndex = index;
                break;
            }
        }
        int browserCount = _browser.Workspace.TabCount;
        if (sourceIndex < 0 || browserCount <= 0)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        int searchIndex = sourceIndex + 1;
        int currentIndex = request.UnifiedSearchIsActive
            ? searchIndex
            : _browser.Workspace.ActiveTabIndex <= sourceIndex
                ? _browser.Workspace.ActiveTabIndex
                : _browser.Workspace.ActiveTabIndex + 1;
        int nextIndex = (currentIndex + delta) % (browserCount + 1);
        if (nextIndex < 0)
        {
            nextIndex += browserCount + 1;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        if (nextIndex == searchIndex)
        {
            if (state.CurrentTab.Id == sourceTabId)
            {
                return Effect(
                    CommandApplicationEffectKind.ActivateUnifiedSearchSurface,
                    runtimeTargetId: sessionId);
            }

            using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
            {
                BrowserTabSwitchWorkflowResult sourceSwitch = _tabs.ExecuteTabSwitch(
                    sourceIndex,
                    state.CurrentPath,
                    state.CurrentTab,
                    state.DirectoryLoadOptions,
                    state.ColumnCount,
                    state.RefreshShellState,
                    recordVisitHistory: true);
                return sourceSwitch.Committed
                    ? Effect(
                        CommandApplicationEffectKind.ApplyUnifiedSearchTabSwitch,
                        tabSwitch: sourceSwitch,
                        runtimeTargetId: sessionId)
                    : Effect(CommandApplicationEffectKind.ApplyTabSwitch, tabSwitch: sourceSwitch);
            }
        }

        int targetTabIndex = nextIndex < searchIndex ? nextIndex : nextIndex - 1;
        if (request.UnifiedSearchIsActive && targetTabIndex == _browser.Workspace.ActiveTabIndex)
        {
            return Effect(
                CommandApplicationEffectKind.DeactivateUnifiedSearchSurface,
                runtimeTargetId: sessionId);
        }

        using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
        {
            BrowserTabSwitchWorkflowResult execution = _tabs.ExecuteTabSwitch(
                targetTabIndex,
                state.CurrentPath,
                state.CurrentTab,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState,
                recordVisitHistory: true);
            return Effect(
                CommandApplicationEffectKind.ApplyTabSwitch,
                tabSwitch: execution,
                boolValue: request.UnifiedSearchIsActive);
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

    private CommandApplicationResult ExecuteTabActivate(bool allowed, CommandExecutionContext context)
    {
        if (!allowed || !TryResolveTabActivationTarget(context, out string categoryId, out int requestedTabIndex))
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        if (string.Equals(categoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase) &&
            requestedTabIndex == _browser.Workspace.ActiveTabIndex)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
        {
            if (!string.Equals(categoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                BrowserCategorySwitchWorkflowResult category = _categories.ExecuteSwitch(
                    categoryId,
                    requestedTabIndex,
                    state.CurrentPath,
                    state.CurrentTab,
                    state.DirectoryLoadOptions,
                    state.ColumnCount,
                    state.RefreshShellState,
                    recordVisitHistory: true);
                return Effect(CommandApplicationEffectKind.ApplyCategorySwitch, categorySwitch: category);
            }

            BrowserTabSwitchWorkflowResult tab = _tabs.ExecuteTabSwitch(
                requestedTabIndex,
                state.CurrentPath,
                state.CurrentTab,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState,
                recordVisitHistory: true);
            return Effect(CommandApplicationEffectKind.ApplyTabSwitch, tabSwitch: tab);
        }
    }

    private CommandApplicationResult ExecuteUnifiedSearchSurfaceActivation(
        bool allowed,
        CommandExecutionContext context,
        BrowserTabNavigationCommandContext request)
    {
        if (!allowed || request.UnifiedSearchSessionId is not { } sessionId ||
            string.IsNullOrWhiteSpace(request.UnifiedSearchSourceCategoryId) ||
            request.UnifiedSearchSourceTabId is not { } sourceTabId)
        {
            return CommandApplicationResult.NotHandled;
        }

        var sourceContext = new CommandExecutionContext
        {
            CategoryId = request.UnifiedSearchSourceCategoryId,
            ContextBrowserTabId = sourceTabId
        };
        if (!TryResolveTabActivationTarget(sourceContext, out string categoryId, out int sourceTabIndex))
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        if (string.Equals(categoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase) &&
            sourceTabIndex == _browser.Workspace.ActiveTabIndex)
        {
            return Effect(CommandApplicationEffectKind.ActivateUnifiedSearchSurface, runtimeTargetId: sessionId);
        }

        using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
        {
            if (!string.Equals(categoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                BrowserCategorySwitchWorkflowResult category = _categories.ExecuteSwitch(
                    categoryId,
                    sourceTabIndex,
                    state.CurrentPath,
                    state.CurrentTab,
                    state.DirectoryLoadOptions,
                    state.ColumnCount,
                    state.RefreshShellState,
                    recordVisitHistory: true);
                return Effect(
                    CommandApplicationEffectKind.ApplyUnifiedSearchCategorySwitch,
                    categorySwitch: category,
                    runtimeTargetId: sessionId);
            }

            BrowserTabSwitchWorkflowResult tab = _tabs.ExecuteTabSwitch(
                sourceTabIndex,
                state.CurrentPath,
                state.CurrentTab,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState,
                recordVisitHistory: true);
            return tab.Committed
                ? Effect(
                    CommandApplicationEffectKind.ApplyUnifiedSearchTabSwitch,
                    tabSwitch: tab,
                    runtimeTargetId: sessionId)
                : Effect(CommandApplicationEffectKind.ApplyTabSwitch, tabSwitch: tab);
        }
    }

    private CommandApplicationResult ExecuteCategoryActivate(bool allowed, string? categoryId)
    {
        if (!allowed || string.IsNullOrWhiteSpace(categoryId))
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandApplicationShellState state = _shell.CaptureState();
        using (_tabs.BeginHistoryModeScope(BrowserTabVisitHistoryMode.Record))
        {
            BrowserCategorySwitchWorkflowResult category = _categories.ExecuteSwitch(
                categoryId,
                requestedTabIndex: null,
                state.CurrentPath,
                state.CurrentTab,
                state.DirectoryLoadOptions,
                state.ColumnCount,
                state.RefreshShellState,
                recordVisitHistory: true);
            return Effect(CommandApplicationEffectKind.ApplyCategorySwitch, categorySwitch: category);
        }
    }

    private CommandApplicationResult ExecuteReorderTab(bool allowed, CommandExecutionContext context)
    {
        if (!allowed || context.BrowserTabReorder is not { } reorder)
        {
            return CommandApplicationResult.NotHandled;
        }

        string categoryId = _browser.Workspace.ResolveCategoryId(context.CategoryId);
        CommandApplicationShellState state = _shell.CaptureState();
        BrowserTabState? currentState = string.Equals(
            categoryId,
            _browser.Workspace.ActiveCategoryId,
            StringComparison.OrdinalIgnoreCase)
                ? state.CurrentTab
                : null;
        bool applied = _tabs.ReorderTabById(
            categoryId,
            reorder.MovingTabId,
            reorder.AnchorTabId,
            reorder.InsertAfter,
            currentState);
        return applied
            ? Effect(
                CommandApplicationEffectKind.ApplyTabReorder,
                messageValue: reorder.NotifyUser ? "タブ順を入れ替えました。" : null,
                boolValue: reorder.FocusBrowserAfter,
                categoryId: categoryId)
            : Effect(CommandApplicationEffectKind.NoOp);
    }

    private CommandApplicationResult ExecuteReorderCategoryByTarget(bool allowed, CommandExecutionContext context)
    {
        if (!allowed || context.BrowserTabCategoryReorder is not { } reorder)
        {
            return CommandApplicationResult.NotHandled;
        }

        IReadOnlyList<BrowserTabCategoryDefinition> categories = _browser.Workspace.CategoryStates;
        int fromIndex = -1;
        int toIndex = -1;
        string displayName = string.Empty;
        for (int index = 0; index < categories.Count; index++)
        {
            if (string.Equals(categories[index].Id, reorder.MovingCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                fromIndex = index;
                displayName = categories[index].DisplayName;
            }
            if (string.Equals(categories[index].Id, reorder.AnchorCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                toIndex = index;
            }
        }
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        int insertionIndex = toIndex + (reorder.InsertAfter ? 1 : 0);
        if (fromIndex < insertionIndex)
        {
            insertionIndex--;
        }
        if (fromIndex == insertionIndex)
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        CommandApplicationShellState state = _shell.CaptureState();
        bool applied = _categories.ReorderByIndex(fromIndex, insertionIndex, state.CurrentTab);
        return applied
            ? Effect(
                CommandApplicationEffectKind.ApplyCategoryReorder,
                textValue: "カテゴリ順を入れ替えました。",
                categoryReorder: new BrowserCategoryReorderTransition(true, reorder.MovingCategoryId, displayName))
            : Effect(CommandApplicationEffectKind.NoOp);
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

    private CommandApplicationResult ExecuteSetTabLayout(bool allowed, BrowserTabLayoutMode? requested)
    {
        if (!allowed || requested is not { } mode || !Enum.IsDefined(mode))
        {
            return CommandApplicationResult.NotHandled;
        }

        if (!_persistence.SetBrowserTabLayout(mode))
        {
            return Effect(CommandApplicationEffectKind.NoOp);
        }

        return Effect(CommandApplicationEffectKind.ApplyTabLayout, intValue: (int)mode);
    }

    private CommandApplicationResult ExecuteAddCategory(bool allowed, string? requestedDisplayName)
    {
        if (!allowed)
        {
            return CommandApplicationResult.NotHandled;
        }

        string displayName = string.IsNullOrWhiteSpace(requestedDisplayName)
            ? _browser.Workspace.GetNextCategoryDisplayName()
            : requestedDisplayName.Trim();
        CommandApplicationShellState state = _shell.CaptureState();
        BrowserCategoryAddSwitchExecution execution = _categories.ExecuteAddAndSwitch(
            displayName,
            state.CurrentPath,
            state.CurrentTab,
            state.DirectoryLoadOptions,
            state.ColumnCount,
            state.RefreshShellState);
        return Effect(CommandApplicationEffectKind.ApplyCategoryAdd, textValue: displayName, categoryAdd: execution);
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
        if (!browser || !TryResolveTabCommandTarget(context, out int tabIndex))
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
        if (!browser || !TryResolveTabCommandTarget(context, out int tabIndex))
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
        if (!allowed || !TryResolveTabCommandTarget(context, out int tabIndex))
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
                category.DisplayName,
                context.ContextBrowserTabId));
        }
        return Effect(CommandApplicationEffectKind.ApplyTabClose, tabClose: execution);
    }

    private CommandApplicationResult ExecuteCloseTabRange(bool allowed, CommandExecutionContext context, BrowserTabCloseScope scope)
    {
        if (!allowed || !TryResolveTabCommandTarget(context, out int tabIndex))
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
        BrowserColumnCountExecution? columnCountChange = null,
        ManagedTrashOperationResult? managedTrashOperation = null,
        WorkspaceSnapshotCommandContext? workspaceSnapshot = null,
        bool refreshPreview = false,
        string? categoryId = null,
        Guid? contextBrowserTabId = null,
        Guid? runtimeTargetId = null,
        ImageViewerCommandAction? imageViewerAction = null,
        ImageViewerCommandContext? imageViewerCommand = null,
        BrowserSelectionMarkExecution? browserMarkExecution = null,
        IReadOnlyList<WorkspaceSnapshotEntry>? workspaceSnapshotEntries = null,
        ManagedTrashCommandContext? managedTrash = null,
        BrowserQuickAccessRegistrationExecution? quickAccessRegistration = null,
        MarkSlotActionResult? markSlotAction = null,
        BrowserMarksClearTransition? markClear = null,
        MarkSlotSetOperationPreviewResult? markSlotSetOperationPreview = null,
        MarkSlotClipboardActionResult? markSlotClipboardAction = null,
        MarkSlotClipboardImportResult? markSlotClipboardImport = null,
        TextViewerSearchExecution? textViewerSearch = null,
        ViewerNavigationDecision? textViewerNavigation = null,
        ViewerOpenRequest? viewerOpenRequest = null,
        bool textViewerRenderedSelectionCopy = false,
        SettingsTransferCommandContext? settingsTransfer = null,
        SettingsSqliteStore.SettingsTransferResult? settingsTransferResult = null,
        SettingsSqliteStore.SettingsSaveResult? settingsSaveResult = null,
        ExternalToolLaunchCommandContext? externalToolLaunch = null,
        UnifiedSearchResultsExportCommandContext? unifiedSearchResultsExport = null,
        UnifiedSearchResultsFilterCommandContext? unifiedSearchResultsFilter = null,
        UnifiedSearchResultActivationExecution? unifiedSearchResultActivation = null,
        ArchiveListNavigationExecution? archiveListNavigation = null,
        ArchiveTextPreviewExecution? archiveTextPreview = null,
        BrowserDragOutPreparation? browserDragOutPreparation = null,
        BrowserTabTreeDropExecution? browserTabTreeDrop = null,
        ViewerSearchResultOrigin? viewerSearchOrigin = null,
        long longValue = 0)
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
                contextBrowserTabId,
                columnCountChange,
                managedTrashOperation,
                workspaceSnapshot,
                runtimeTargetId,
                imageViewerAction,
                imageViewerCommand,
                browserMarkExecution,
                workspaceSnapshotEntries,
                managedTrash,
                quickAccessRegistration,
                markSlotAction,
                markClear,
                markSlotSetOperationPreview,
                markSlotClipboardAction,
                markSlotClipboardImport,
                textViewerSearch,
                textViewerNavigation,
                viewerOpenRequest,
                textViewerRenderedSelectionCopy,
                settingsTransfer,
                settingsTransferResult,
                settingsSaveResult,
                externalToolLaunch,
                unifiedSearchResultsExport,
                unifiedSearchResultActivation,
                archiveListNavigation)
            {
                ViewerSearchOrigin = viewerSearchOrigin,
                UnifiedSearchResultsFilter = unifiedSearchResultsFilter,
                ArchiveTextPreview = archiveTextPreview,
                BrowserDragOutPreparation = browserDragOutPreparation,
                BrowserTabTreeDrop = browserTabTreeDrop,
                LongValue = longValue
            });
    }

    private bool TryResolveTabTarget(CommandExecutionContext context, out int tabIndex)
    {
        tabIndex = context.ContextTabIndex ?? _browser.Workspace.ActiveTabIndex;
        return tabIndex >= 0 && tabIndex < _browser.Workspace.TabCount;
    }

    private bool TryResolveTabCommandTarget(CommandExecutionContext context, out int tabIndex)
    {
        return context.ContextBrowserTabId is { } targetTabId
            ? TryResolveTabIndexById(targetTabId, out tabIndex)
            : TryResolveTabTarget(context, out tabIndex);
    }

    private bool TryResolveTabIndexById(Guid targetTabId, out int tabIndex)
    {
        IReadOnlyList<BrowserTabState> tabs = _browser.Workspace.TabStates;
        for (int index = 0; index < tabs.Count; index++)
        {
            if (tabs[index].Id == targetTabId)
            {
                tabIndex = index;
                return true;
            }
        }

        tabIndex = -1;
        return false;
    }

    private bool TryResolveTabActivationTarget(
        CommandExecutionContext context,
        out string categoryId,
        out int tabIndex)
    {
        categoryId = string.IsNullOrWhiteSpace(context.CategoryId)
            ? _browser.Workspace.ActiveCategoryId ?? BrowserTabSettings.DefaultCategoryId
            : context.CategoryId;
        tabIndex = -1;

        IReadOnlyList<BrowserTabState> activeTabs = _browser.Workspace.TabStates;
        if (context.ContextBrowserTabId is { } targetTabId)
        {
            if (string.Equals(categoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                for (int index = 0; index < activeTabs.Count; index++)
                {
                    if (activeTabs[index].Id == targetTabId)
                    {
                        tabIndex = index;
                        return true;
                    }
                }
                return false;
            }

            IReadOnlyList<BrowserTabSessionState> storedTabs =
                _persistence.GetSessionTabsForRestore(categoryId, out _);
            for (int index = 0; index < storedTabs.Count; index++)
            {
                if (storedTabs[index].TabId == targetTabId)
                {
                    tabIndex = index;
                    return true;
                }
            }
            return false;
        }

        int requestedIndex = context.ContextTabIndex ?? -1;
        int tabCount;
        if (string.Equals(categoryId, _browser.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            tabCount = activeTabs.Count;
        }
        else if (_persistence.FindStoredCategory(categoryId) is not { } storedCategory)
        {
            return false;
        }
        else
        {
            tabCount = storedCategory.OpenTabs.Count;
        }

        if (requestedIndex < 0 || requestedIndex >= tabCount)
        {
            return false;
        }

        tabIndex = requestedIndex;
        return true;
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
    ApplyUnifiedSearchCategorySwitch,
    ApplyCategoryAdd,
    ApplyCategoryReorder,
    ApplyTabLayout,
    ApplyBrowserTabGroupMutation,
    ApplyBrowserTabTreeDrop,
    ViewerCommandCompleted,
    LaunchExternalMediaPlayback,
    ConfirmExecuteTarget,
    OpenMediaViewer,
    OpenArchiveFallback,
    ApplyArchiveListNavigation,
    ArchiveListNavigationCommitted,
    RollbackArchiveListNavigation,
    ApplyArchiveTextPreview,
    PrepareBrowserDragOut,
    OpenExternalProcess,
    LaunchConfiguredExternalTool,
    FileOperationStarted,
    ApplyDefaultOpen,
    OpenWithDialog,
    OpenExternalUrl,
    ApplyTabLock,
    ApplyTabReadOnly,
    ApplyUnifiedFilter,
    RunUnifiedFilterFind,
    CloseUnifiedSearchSession,
    ExportUnifiedSearchResults,
    ApplyUnifiedSearchResultsFilter,
    UnifiedSearchResultActivationCompleted,
    OpenUnifiedSearchHitInExternalEditor,
    ApplyTabClose,
    ApplyTabRangeClose,
    ApplyRestoredTab,
    ApplyQuickAccess,
    CompleteQuickAccessRegistration,
    ApplyLogdisk,
    ApplyCategoryRename,
    ApplyCategoryDelete,
    ApplyTabReorder,
    ApplyTabCloseConfirmation,
    ShowMessage,
    ShowWorkspaceSnapshotInformation,
    ShowWorkspaceSnapshotFailure,
    OpenCommandDialog,
    OpenPathEntry,
    ApplyBrowserColumnCount,
    ApplyMarkSlotRestore,
    ApplyMarkSlotAction,
    ApplyMarkSlotClipboard,
    ApplyBrowserMarkClear,
    ApplyBrowserMarkRangeAdd,
    ApplyTextViewerExit,
    ApplyTextViewerWordWrap,
    ApplyTextViewerMarkdownMode,
    ApplyTextViewerCopy,
    OpenTextViewerInExternalEditor,
    CopyHashResultText,
    ApplyTextViewerSearch,
    ApplyTextViewerLargeSearchResult,
    ApplyTextViewerNavigation,
    ApplyViewerOpenRequest,
    SettingsTransferCompleted,
    SettingsSaveCompleted,
    CloseApplication,
    ApplyBrowserMarkToggle,
    ActivateUnifiedSearchSurface,
    DeactivateUnifiedSearchSurface,
    ApplyUnifiedSearchTabSwitch,
    DispatchImageViewerCommand,
    SetViewerPresentationMode,
    ManagedTrashActionCompleted,
    DispatchWorkspaceSnapshot,
    OpenExplorer,
    RevealInExplorer,
    OpenTerminal,
    OpenExternalEditor,
    LaunchSendTo,
    LaunchExternalDiff,
    CopyFullPaths,
    CopyCurrentPath,
    CopyHeaderItemName,
    ShowHelp,
    OpenMarkSlot,
    ManageTabCategories,
    Properties,
    ShowSystemInformation,
    OpenNewInstance,
    OpenControlPanel,
    OpenSettings,
    ApplyBrowserToolbarVisibility,
    OpenCommandLauncher,
    ShowCommandList,
    OpenManagedTrash,
    FileOperationCancellationRequested
}

internal abstract record CommandApplicationInteractionRequest;

internal sealed record SortCommandInteractionRequest(
    SortKind CurrentSort,
    bool CurrentAscending) : CommandApplicationInteractionRequest;

internal sealed record UnifiedFilterFindCommandInteractionRequest(
    int TargetTabIndex,
    string RootPath,
    UnifiedFilterFindCriteria CurrentCriteria,
    UnifiedFilterFindMode InitialMode,
    Guid? TargetBrowserTabId = null,
    bool PreserveSearchDraft = false) : CommandApplicationInteractionRequest;

internal sealed record TreeCommandInteractionRequest(
    string CurrentPath) : CommandApplicationInteractionRequest;

internal sealed record QuickAccessCommandInteractionRequest(
    string CurrentPath,
    QuickAccessStore Store,
    IReadOnlyList<QuickAccessEntry> HistoryEntries) : CommandApplicationInteractionRequest;

internal sealed record QuickAccessRegistrationCommandInteractionRequest(
    string CurrentPath,
    string PathToRegister,
    string DefaultDisplayName,
    IReadOnlyList<string> CategoryNames) : CommandApplicationInteractionRequest;

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

internal sealed record BrowserDriveRootNavigationContinuation(
    BrowserDriveRootNavigationPreparation Preparation) : BrowserDerivedTabNavigationContinuation;

internal sealed record BrowserDerivedTabNavigationInteractionRequest(
    BrowserDerivedTabNavigationContinuation Continuation,
    string? PreInteractionMessage = null) : CommandApplicationInteractionRequest;

internal sealed record BrowserMarkSlotImportInteractionRequest(
    string CommandId,
    int? SlotNumber,
    string FilePath,
    MarkSlotEntry? ImportedSlot,
    MarkSlotStore? ImportedStore,
    string? WarningMessage) : CommandApplicationInteractionRequest;

internal sealed record TextViewerSearchCommandInteractionRequest(
    TextViewerSearchCommandContext Context) : CommandApplicationInteractionRequest;

internal sealed record LogdiskCommandInteractionRequest(
    string DefaultPath,
    IReadOnlyList<string> Candidates) : CommandApplicationInteractionRequest;

internal sealed record ArchiveListCommandInteractionRequest(
    string ArchivePath,
    ArchiveListResult Contents,
    string CurrentPath,
    bool IsReadOnly,
    string? DateFormat,
    string? SizeFormat,
    Guid NavigationSessionId) : CommandApplicationInteractionRequest;

internal sealed record CategoryRenameCommandInteractionRequest(
    string CategoryId,
    string DisplayName) : CommandApplicationInteractionRequest;

internal enum BrowserTabGroupInteractionKind
{
    Create,
    Add,
    Rename
}

internal sealed record BrowserTabGroupCommandInteractionRequest(
    BrowserTabGroupInteractionKind Kind,
    string CategoryId,
    Guid TabId,
    Guid? GroupId = null,
    string? DisplayName = null,
    IReadOnlyList<BrowserTabGroup>? Groups = null,
    IReadOnlySet<Guid>? BlockedTabIds = null) : CommandApplicationInteractionRequest;

internal sealed record CategoryDeleteCommandInteractionRequest(
    string CategoryId,
    string DisplayName) : CommandApplicationInteractionRequest;

internal sealed record TabCloseCategoryRemovalInteractionRequest(
    int TabIndex,
    string CategoryId,
    string DisplayName,
    Guid? TargetBrowserTabId = null) : CommandApplicationInteractionRequest;

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

internal sealed record QuickAccessRegistrationCommandInteractionResponse(
    bool Accepted,
    string DisplayName,
    string? CategoryName,
    string Path,
    bool UseForTabTitle) : CommandApplicationInteractionResponse;

internal sealed record BrowserDerivedTabNavigationInteractionResponse(
    bool Accepted) : CommandApplicationInteractionResponse;

internal sealed record BrowserMarkSlotImportInteractionResponse(
    bool Accepted) : CommandApplicationInteractionResponse;

internal sealed record TextViewerSearchCommandInteractionResponse(
    bool Accepted,
    string? Query) : CommandApplicationInteractionResponse;

internal sealed record LogdiskCommandInteractionResponse(
    string? SelectedPath) : CommandApplicationInteractionResponse;

internal sealed record ArchiveListCommandInteractionResponse(
    ArchiveExtractRequest? PendingExtractRequest) : CommandApplicationInteractionResponse;

internal sealed record ArchiveListNavigationApplyRequest(
    ArchiveListNavigationExecution Proposal) : CommandApplicationInteractionRequest;

internal sealed record ArchiveListNavigationApplyResponse(
    bool Applied) : CommandApplicationInteractionResponse;

internal sealed record CategoryRenameCommandInteractionResponse(
    bool Accepted,
    string? DisplayName) : CommandApplicationInteractionResponse;

internal sealed record BrowserTabGroupCommandInteractionResponse(
    bool Accepted,
    Guid? GroupId = null,
    string? DisplayName = null) : CommandApplicationInteractionResponse;

internal sealed record CategoryDeleteCommandInteractionResponse(
    bool Accepted) : CommandApplicationInteractionResponse;

internal sealed record TabCloseCategoryRemovalInteractionResponse(
    bool Accepted) : CommandApplicationInteractionResponse;

internal sealed record BrowserTabVisitHistoryCommandInteractionResponse(
    bool Accepted,
    BrowserTabVisitHistoryListDirection Direction,
    int Depth) : CommandApplicationInteractionResponse;

internal sealed record WorkspaceSnapshotOverwriteInteractionRequest(
    WorkspaceSnapshotCommandContext Command) : CommandApplicationInteractionRequest;

internal sealed record WorkspaceSnapshotOverwriteInteractionResponse(
    bool Accepted) : CommandApplicationInteractionResponse;

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
    Guid? ContextBrowserTabId = null,
    BrowserColumnCountExecution? ColumnCountChange = null,
    ManagedTrashOperationResult? ManagedTrashOperation = null,
    WorkspaceSnapshotCommandContext? WorkspaceSnapshot = null,
    Guid? RuntimeTargetId = null,
    ImageViewerCommandAction? ImageViewerAction = null,
    ImageViewerCommandContext? ImageViewerCommand = null,
    BrowserSelectionMarkExecution? BrowserMarkExecution = null,
    IReadOnlyList<WorkspaceSnapshotEntry>? WorkspaceSnapshotEntries = null,
    ManagedTrashCommandContext? ManagedTrash = null,
    BrowserQuickAccessRegistrationExecution? QuickAccessRegistration = null,
    MarkSlotActionResult? MarkSlotAction = null,
    BrowserMarksClearTransition? MarkClear = null,
    MarkSlotSetOperationPreviewResult? MarkSlotSetOperationPreview = null,
    MarkSlotClipboardActionResult? MarkSlotClipboardAction = null,
    MarkSlotClipboardImportResult? MarkSlotClipboardImport = null,
    TextViewerSearchExecution? TextViewerSearch = null,
    ViewerNavigationDecision? TextViewerNavigation = null,
    ViewerOpenRequest? ViewerOpenRequest = null,
    bool TextViewerRenderedSelectionCopy = false,
    SettingsTransferCommandContext? SettingsTransfer = null,
    SettingsSqliteStore.SettingsTransferResult? SettingsTransferResult = null,
    SettingsSqliteStore.SettingsSaveResult? SettingsSaveResult = null,
    ExternalToolLaunchCommandContext? ExternalToolLaunch = null,
    UnifiedSearchResultsExportCommandContext? UnifiedSearchResultsExport = null,
    UnifiedSearchResultActivationExecution? UnifiedSearchResultActivation = null,
    ArchiveListNavigationExecution? ArchiveListNavigation = null)
{
    public long LongValue { get; init; }
    public UnifiedSearchResultsFilterCommandContext? UnifiedSearchResultsFilter { get; init; }
    public UnifiedSearchHitEditorExecution? UnifiedSearchHitEditor { get; init; }
    public ArchiveTextPreviewExecution? ArchiveTextPreview { get; init; }
    public BrowserDragOutPreparation? BrowserDragOutPreparation { get; init; }
    public BrowserTabTreeDropExecution? BrowserTabTreeDrop { get; init; }
    public ViewerSearchResultOrigin? ViewerSearchOrigin { get; init; }
}

internal sealed record ViewerSearchResultOrigin(
    LargeFilePreviewState State, int ViewerRequestId, int SearchRequestId, CancellationToken Token);

internal sealed record BrowserTabTreeDropExecution(
    bool MembershipChanged,
    bool MembershipConfirmed,
    bool ReorderRequested,
    bool ReorderAttempted,
    bool ReorderApplied);

internal sealed record TextViewerSearchExecution(
    string Query,
    bool Backward,
    bool IsLargeFile,
    ViewerTextSearchResult? TextSearchResult,
    LargeFileSearchResult? LargeFileSearchResult = null);

internal interface ICommandApplicationShellPort
{
    CommandApplicationShellState CaptureState();
    IReadOnlyList<string> GetBulkMarkTargetPaths(bool includeDirectories);
    IReadOnlyList<string> GetSharedLocationCandidates();
    void SetBrowserFileDisplayMode(BrowserFileDisplayMode mode, bool persistTabState, bool rematerialize);
    int GetBrowserItemsPerPageForColumn(int columnCount);
    BrowserDirectoryLoadOptions CreateBrowserDirectoryLoadOptions(int itemsPerPage);
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
    public BrowserQuickAccessDraftMutationResult? QuickAccessDraftMutation { get; init; }

    public static CommandApplicationResult NotHandled { get; } = new(false);

    public static implicit operator CommandApplicationResult(bool handled) => new(handled);
}
