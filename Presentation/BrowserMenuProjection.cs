using System.Collections.Generic;
using MidFD.Commands;
using MidFD.Helpers;

namespace MidFD.Presentation;

internal readonly record struct BrowserMenuItemProjectionInput(
    bool BrowserOnly,
    bool BusyAware,
    CommandStateCoordinator.MenuItemStateRule Rule);

internal static class BrowserMenuProjection
{
    public static IReadOnlyList<bool> Build(
        CommandStateCoordinator.CommandUiSnapshot snapshot,
        IReadOnlyList<BrowserMenuItemProjectionInput> items)
    {
        var states = new bool[items.Count];
        for (int index = 0; index < items.Count; index++)
        {
            BrowserMenuItemProjectionInput item = items[index];
            bool enabled = true;
            if (item.BrowserOnly) enabled &= snapshot.IsBrowserMode;
            if (item.BusyAware)
            {
                enabled &= snapshot.IsBrowserMode;
                if (!snapshot.IsIdle)
                {
                    enabled &= item.Rule.CommandId is { } busyCommandId
                        && CommandBusyPolicy.IsEnabledWhileBusy(busyCommandId);
                }
            }
            if (enabled && !snapshot.IsIdle && item.Rule.CommandId is { } commandId)
            {
                enabled &= CommandBusyPolicy.IsEnabledWhileBusy(commandId);
            }
            if (enabled && item.Rule.RequiresSelection) enabled &= snapshot.HasSelection;
            if (enabled && item.Rule.RequiresFile) enabled &= snapshot.HasFileSelection;
            if (enabled && item.Rule.RequiresEditorTarget) enabled &= snapshot.HasEditorTarget;
            if (enabled && item.Rule.RequiresExactlyTwoSelection) enabled &= snapshot.HasExactlyTwoSelection;
            if (enabled && item.Rule.RequiresTwoFiles) enabled &= snapshot.HasTwoFileSelection;
            if (enabled && item.Rule.CommandId is { } selectionCommandId)
            {
                if (selectionCommandId == CommandIds.BrowserFilterClear)
                {
                    enabled &= !string.IsNullOrEmpty(snapshot.FilterPattern) || snapshot.FilterDetailActive;
                }
                else if (selectionCommandId is CommandIds.BrowserExecute
                    or CommandIds.BrowserDefaultOpen
                    or CommandIds.BrowserDefaultOpenMarked
                    or CommandIds.BrowserOpenWith
                    or CommandIds.BrowserOpenInNewTab)
                {
                    enabled &= BrowserOpenSelectionResolver.IsCommandEnabled(
                        selectionCommandId,
                        new BrowserOpenSelection(snapshot.OpenSelectionKind, System.Array.Empty<string>()));
                }
            }
            states[index] = enabled;
        }
        return states;
    }
}
