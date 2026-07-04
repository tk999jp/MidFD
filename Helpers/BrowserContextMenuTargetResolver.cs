using System.Collections.Generic;
using System.Linq;
using MidFD.Models;

namespace MidFD.Helpers;

internal static class BrowserContextMenuTargetResolver
{
    public static BrowserContextMenuTargetResolution Resolve(
        IReadOnlyCollection<string> markedPaths,
        int clickedIndex,
        int itemCount,
        string? clickedPath,
        bool isParentEntry)
    {
        if (clickedIndex < 0 || clickedIndex >= itemCount)
        {
            return new BrowserContextMenuTargetResolution(
                BrowserContextMenuKind.Background,
                -1,
                SelectionResult.Empty);
        }

        List<string> marks = markedPaths?
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        bool hasClickableItemPath = !isParentEntry && !string.IsNullOrWhiteSpace(clickedPath);
        bool clickedIsMarked = hasClickableItemPath
            && marks.Contains(clickedPath!, System.StringComparer.OrdinalIgnoreCase);

        if (marks.Count > 1 && clickedIsMarked)
        {
            return new BrowserContextMenuTargetResolution(
                BrowserContextMenuKind.MultiSelection,
                clickedIndex,
                new SelectionResult(marks, hasMarkedSelection: true));
        }

        if (marks.Count == 1 && clickedIsMarked)
        {
            return new BrowserContextMenuTargetResolution(
                BrowserContextMenuKind.Item,
                clickedIndex,
                new SelectionResult(marks, hasMarkedSelection: true));
        }

        if (hasClickableItemPath)
        {
            return new BrowserContextMenuTargetResolution(
                BrowserContextMenuKind.Item,
                clickedIndex,
                new SelectionResult([clickedPath!], hasMarkedSelection: false));
        }

        return new BrowserContextMenuTargetResolution(
            BrowserContextMenuKind.Item,
            clickedIndex,
            SelectionResult.Empty);
    }
}
