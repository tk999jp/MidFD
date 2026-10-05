using System.Collections.Generic;
using System.Linq;
using MidFD.Models;

namespace MidFD.Helpers;

internal readonly record struct BrowserPassiveSelectionFacts(
    int Count,
    BrowserOpenSelectionKind OpenSelectionKind,
    bool HasUnknown,
    bool AllFiles,
    bool HasDirectory,
    bool AllDirectories,
    bool AllArchives)
{
    public bool HasSelection => Count > 0;

    public bool HasArchiveSelection => HasSelection && AllArchives;

    public bool HasHashableSelection => HasSelection && AllFiles;

    public static BrowserPassiveSelectionFacts Resolve(
        SelectionResult? selection,
        IReadOnlyDictionary<string, bool>? knownPathKinds,
        bool allowFileSystemFallback = true)
    {
        BrowserOpenSelection openSelection = BrowserOpenSelectionResolver.Resolve(
            selection,
            currentItemName: null,
            currentItemPath: null,
            currentSnapshotPathKinds: knownPathKinds,
            allowFileSystemFallback: allowFileSystemFallback);
        int count = selection?.Count ?? (openSelection.IsSingle ? 1 : 0);
        bool allFiles = openSelection.Kind is
            BrowserOpenSelectionKind.SingleFile or
            BrowserOpenSelectionKind.SingleArchive or
            BrowserOpenSelectionKind.MultipleFiles;
        bool allDirectories = openSelection.Kind is
            BrowserOpenSelectionKind.SingleDirectory or
            BrowserOpenSelectionKind.MultipleDirectories;
        bool hasDirectory = openSelection.Kind is
            BrowserOpenSelectionKind.SingleDirectory or
            BrowserOpenSelectionKind.MultipleDirectories or
            BrowserOpenSelectionKind.Mixed;
        bool allArchives = allFiles &&
            selection?.FullPaths.All(ArchiveFileTypeHelper.IsArchive) == true;

        return new BrowserPassiveSelectionFacts(
            count,
            openSelection.Kind,
            openSelection.Kind == BrowserOpenSelectionKind.Invalid,
            allFiles,
            hasDirectory,
            allDirectories,
            allArchives);
    }
}
