using System.Collections.Generic;
using MidFD.Commands;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

internal sealed class CommandStateCoordinator
{
    internal enum BrowserSelectionKind
    {
        None,
        ParentDirectory,
        Directory,
        File,
        ArchiveCandidate
    }

    internal readonly record struct MenuItemStateRule(
        bool RequiresSelection = false,
        bool RequiresFile = false,
        bool RequiresEditorTarget = false,
        bool RequiresExactlyTwoSelection = false,
        bool RequiresTwoFiles = false,
        string? CommandId = null);

    internal readonly record struct CommandUiSnapshot(
        bool IsBrowserMode,
        bool IsViewerMode,
        bool IsIdle,
        bool HasSelection,
        bool HasExactlyTwoSelection,
        bool HasFileSelection,
        bool HasEditorTarget,
        bool HasTwoFileSelection,
        BrowserSelectionKind SelectionKind = BrowserSelectionKind.None,
        BrowserOpenSelectionKind OpenSelectionKind = BrowserOpenSelectionKind.None,
        string FilterPattern = "",
        bool FilterDetailActive = false,
        bool CanUndo = false,
        bool CanRedo = false);

    internal readonly record struct CommandHintState(
        bool CanShowOverlay,
        bool CanUseCommandLauncherCommands);

    internal CommandUiSnapshot CreateCommandUiSnapshot(
        bool isBrowserMode,
        bool isBusy,
        int selectionCount,
        bool hasTwoFileSelection,
        string? currentItemText,
        string? currentPath,
        BrowserSelectionKind selectionKind,
        BrowserOpenSelectionKind openSelectionKind = BrowserOpenSelectionKind.None,
        string filterPattern = "",
        bool filterDetailActive = false,
        bool canUndo = false,
        bool canRedo = false)
    {
        bool hasSelection = selectionCount > 0;
        bool hasExactlyTwoSelection = selectionCount == 2;
        bool hasFileSelection = isBrowserMode
            && (selectionKind == BrowserSelectionKind.File
                || selectionKind == BrowserSelectionKind.ArchiveCandidate);
        bool hasEditorTarget = hasFileSelection && ExternalToolService.IsEditorTargetExtension(currentPath!);
        return new CommandUiSnapshot(
            IsBrowserMode: isBrowserMode,
            IsViewerMode: !isBrowserMode,
            IsIdle: !isBusy,
            HasSelection: hasSelection,
            HasExactlyTwoSelection: hasExactlyTwoSelection,
            HasFileSelection: hasFileSelection,
            HasEditorTarget: hasEditorTarget,
            HasTwoFileSelection: hasExactlyTwoSelection && hasTwoFileSelection,
            SelectionKind: selectionKind,
            OpenSelectionKind: openSelectionKind == BrowserOpenSelectionKind.None
                ? InferOpenSelectionKind(selectionCount, selectionKind, hasTwoFileSelection)
                : openSelectionKind,
            FilterPattern: filterPattern ?? string.Empty,
            FilterDetailActive: filterDetailActive,
            CanUndo: canUndo,
            CanRedo: canRedo);
    }

    internal bool UsesBrowserFunctionBar(CommandUiSnapshot snapshot)
    {
        return snapshot.IsBrowserMode;
    }

    internal bool IsCommandEnabled(string commandId, CommandUiSnapshot snapshot)
    {
        if (!snapshot.IsBrowserMode) return false;
        if (!CommandBusyPolicy.CanExecute(CommandBusyPolicy.GetBehavior(commandId), !snapshot.IsIdle)) return false;

        switch (commandId)
        {
            case CommandIds.BrowserExecute:
            case CommandIds.BrowserDefaultOpen:
            case CommandIds.BrowserDefaultOpenMarked:
            case CommandIds.BrowserOpenInNewTab:
                return BrowserOpenSelectionResolver.IsCommandEnabled(
                    commandId,
                    new BrowserOpenSelection(snapshot.OpenSelectionKind, System.Array.Empty<string>()));

            case CommandIds.BrowserChangeAttributes:
                return snapshot.SelectionKind == BrowserSelectionKind.Directory ||
                       snapshot.SelectionKind == BrowserSelectionKind.File ||
                       snapshot.SelectionKind == BrowserSelectionKind.ArchiveCandidate;

            case CommandIds.BrowserCreateDirectory:
                return true;

            case CommandIds.FileRename:
            case CommandIds.FileCopy:
            case CommandIds.FileMove:
            case CommandIds.FileDelete:
                case CommandIds.BrowserCopyFullPath:
                return snapshot.SelectionKind == BrowserSelectionKind.Directory ||
                       snapshot.SelectionKind == BrowserSelectionKind.File ||
                       snapshot.SelectionKind == BrowserSelectionKind.ArchiveCandidate;

            case CommandIds.BrowserPreview:
                return snapshot.SelectionKind == BrowserSelectionKind.File ||
                       snapshot.SelectionKind == BrowserSelectionKind.ArchiveCandidate;

            case CommandIds.ArchivePack:
                return snapshot.SelectionKind == BrowserSelectionKind.Directory ||
                       snapshot.SelectionKind == BrowserSelectionKind.File ||
                       snapshot.SelectionKind == BrowserSelectionKind.ArchiveCandidate;

            case CommandIds.BrowserOpenExternalEditor:
                return snapshot.SelectionKind == BrowserSelectionKind.File ||
                       snapshot.SelectionKind == BrowserSelectionKind.ArchiveCandidate;

            case CommandIds.ArchiveUnpack:
                return snapshot.SelectionKind == BrowserSelectionKind.ArchiveCandidate;

            case CommandIds.BrowserFilterClear:
                return !string.IsNullOrEmpty(snapshot.FilterPattern) || snapshot.FilterDetailActive;

            case CommandIds.EditUndo:
                return snapshot.CanUndo;

            case CommandIds.EditRedo:
                return snapshot.CanRedo;

            default:
                return true;
        }
    }

    private static BrowserOpenSelectionKind InferOpenSelectionKind(
        int selectionCount,
        BrowserSelectionKind selectionKind,
        bool hasTwoFileSelection)
    {
        if (selectionCount <= 0)
        {
            return BrowserOpenSelectionKind.None;
        }

        if (selectionCount > 1)
        {
            return hasTwoFileSelection
                ? BrowserOpenSelectionKind.MultipleFiles
                : BrowserOpenSelectionKind.Mixed;
        }

        return selectionKind switch
        {
            BrowserSelectionKind.ParentDirectory => BrowserOpenSelectionKind.ParentDirectory,
            BrowserSelectionKind.Directory => BrowserOpenSelectionKind.SingleDirectory,
            BrowserSelectionKind.ArchiveCandidate => BrowserOpenSelectionKind.SingleArchive,
            BrowserSelectionKind.File => BrowserOpenSelectionKind.SingleFile,
            _ => BrowserOpenSelectionKind.None
        };
    }

    internal CommandHintState CreateCommandHintState(
        bool isBrowserMode,
        bool isFormVisible,
        bool isFormEnabled,
        bool isBrowserPanelVisible,
        bool isMenuStripAltNavigationActive,
        bool hasInputFocus)
    {
        bool canShowOverlay = isBrowserMode
            && isFormVisible
            && isFormEnabled
            && isBrowserPanelVisible
            && !isMenuStripAltNavigationActive;

        return new CommandHintState(
            CanShowOverlay: canShowOverlay,
            CanUseCommandLauncherCommands: canShowOverlay && hasInputFocus);
    }
}
