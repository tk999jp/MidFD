using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MidFD.Services;

namespace MidFD.Helpers;

internal sealed class CommandStateCoordinator
{
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
        bool HasTwoFileSelection);

    internal readonly record struct CommandHintState(
        bool CanShowOverlay,
        bool CanUseCommandLauncherCommands);

    internal CommandUiSnapshot CreateCommandUiSnapshot(
        bool isBrowserMode,
        bool isBusy,
        int selectionCount,
        bool hasTwoFileSelection,
        string? currentItemText,
        string? currentPath)
    {
        bool hasSelection = selectionCount > 0;
        bool hasExactlyTwoSelection = selectionCount == 2;
        bool hasFileSelection = isBrowserMode
            && !string.IsNullOrWhiteSpace(currentPath)
            && currentItemText != ".."
            && File.Exists(currentPath);
        bool hasEditorTarget = hasFileSelection && ExternalToolService.IsEditorTargetExtension(currentPath!);
        return new CommandUiSnapshot(
            IsBrowserMode: isBrowserMode,
            IsViewerMode: !isBrowserMode,
            IsIdle: !isBusy,
            HasSelection: hasSelection,
            HasExactlyTwoSelection: hasExactlyTwoSelection,
            HasFileSelection: hasFileSelection,
            HasEditorTarget: hasEditorTarget,
            HasTwoFileSelection: hasExactlyTwoSelection && hasTwoFileSelection);
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
