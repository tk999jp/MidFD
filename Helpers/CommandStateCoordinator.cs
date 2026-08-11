using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
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
        bool RequiresTwoFiles = false);

    internal readonly record struct CommandUiSnapshot(
        bool IsBrowserMode,
        bool IsViewerMode,
        bool IsIdle,
        bool HasSelection,
        bool HasExactlyTwoSelection,
        bool HasFileSelection,
        bool HasEditorTarget,
        bool HasTwoFileSelection,
        BrowserSelectionKind SelectionKind = BrowserSelectionKind.None);

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
        BrowserSelectionKind selectionKind)
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
            SelectionKind: selectionKind);
    }

    internal Dictionary<ToolStripItem, bool> BuildMenuItemStates(
        CommandUiSnapshot snapshot,
        IReadOnlyList<ToolStripItem> browserOnlyItems,
        IReadOnlyList<ToolStripItem> busyAwareItems,
        IReadOnlyDictionary<ToolStripItem, MenuItemStateRule> menuItemRules)
    {
        var browserOnlySet = new HashSet<ToolStripItem>(browserOnlyItems);
        var busyAwareSet = new HashSet<ToolStripItem>(busyAwareItems);
        var allItems = new HashSet<ToolStripItem>(browserOnlyItems);
        allItems.UnionWith(busyAwareItems);
        allItems.UnionWith(menuItemRules.Keys);

        var states = new Dictionary<ToolStripItem, bool>(allItems.Count);
        foreach (ToolStripItem item in allItems)
        {
            bool enabled = true;

            if (browserOnlySet.Contains(item))
            {
                enabled &= snapshot.IsBrowserMode;
            }

            if (busyAwareSet.Contains(item))
            {
                enabled &= snapshot.IsBrowserMode && snapshot.IsIdle;
            }

            if (enabled && menuItemRules.TryGetValue(item, out MenuItemStateRule rule))
            {
                if (rule.RequiresSelection)
                {
                    enabled &= snapshot.HasSelection;
                }

                if (enabled && rule.RequiresFile)
                {
                    enabled &= snapshot.HasFileSelection;
                }

                if (enabled && rule.RequiresEditorTarget)
                {
                    enabled &= snapshot.HasEditorTarget;
                }

                if (enabled && rule.RequiresExactlyTwoSelection)
                {
                    enabled &= snapshot.HasExactlyTwoSelection;
                }

                if (enabled && rule.RequiresTwoFiles)
                {
                    enabled &= snapshot.HasTwoFileSelection;
                }
            }

            states[item] = enabled;
        }

        return states;
    }

    internal bool UsesBrowserFunctionBar(CommandUiSnapshot snapshot)
    {
        return snapshot.IsBrowserMode;
    }

    internal bool IsCommandEnabled(string commandId, CommandUiSnapshot snapshot)
    {
        if (!snapshot.IsBrowserMode) return false;
        if (!snapshot.IsIdle)
        {
            if (commandId != CommandIds.AppOpenSystemInformation &&
                commandId != CommandIds.AppOpenSettings &&
                commandId != CommandIds.AppOpenCommandLauncher)
            {
                return false;
            }
        }

        switch (commandId)
        {
            case CommandIds.BrowserExecute:
                return snapshot.SelectionKind == BrowserSelectionKind.ParentDirectory ||
                       snapshot.SelectionKind == BrowserSelectionKind.Directory ||
                       snapshot.SelectionKind == BrowserSelectionKind.File ||
                       snapshot.SelectionKind == BrowserSelectionKind.ArchiveCandidate;

            case CommandIds.BrowserDefaultOpen:
                return snapshot.SelectionKind == BrowserSelectionKind.Directory ||
                       snapshot.SelectionKind == BrowserSelectionKind.File ||
                       snapshot.SelectionKind == BrowserSelectionKind.ArchiveCandidate;

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

            default:
                return true;
        }
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
