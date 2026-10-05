using System;
using System.Collections.Generic;
using System.IO;
using MidFD.Commands;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

internal enum BrowserOpenSelectionKind
{
    None,
    ParentDirectory,
    SingleFile,
    SingleDirectory,
    SingleArchive,
    MultipleFiles,
    MultipleDirectories,
    Mixed,
    Invalid
}

internal readonly record struct BrowserOpenSelection(
    BrowserOpenSelectionKind Kind,
    IReadOnlyList<string> Paths)
{
    public bool IsSingle => Kind is
        BrowserOpenSelectionKind.ParentDirectory or
        BrowserOpenSelectionKind.SingleFile or
        BrowserOpenSelectionKind.SingleDirectory or
        BrowserOpenSelectionKind.SingleArchive;

    public bool IsBatchFile => Kind == BrowserOpenSelectionKind.MultipleFiles;

    public bool IsBatchDirectory => Kind == BrowserOpenSelectionKind.MultipleDirectories;
}

internal static class BrowserOpenSelectionResolver
{
    public static BrowserOpenSelection Resolve(
        SelectionResult? selection,
        string? currentItemName,
        string? currentItemPath,
        IReadOnlyDictionary<string, bool>? currentSnapshotPathKinds = null,
        bool allowFileSystemFallback = true)
    {
        if (selection == null)
        {
            if (string.Equals(currentItemName, "..", StringComparison.Ordinal))
            {
                return new BrowserOpenSelection(
                    BrowserOpenSelectionKind.ParentDirectory,
                    Array.Empty<string>());
            }

            return ResolveSingle(currentItemPath, currentSnapshotPathKinds, allowFileSystemFallback);
        }

        if (selection.Count == 0)
        {
            return string.Equals(currentItemName, "..", StringComparison.Ordinal)
                ? new BrowserOpenSelection(BrowserOpenSelectionKind.ParentDirectory, Array.Empty<string>())
                : new BrowserOpenSelection(BrowserOpenSelectionKind.None, Array.Empty<string>());
        }

        IReadOnlyList<string> paths = selection.FullPaths;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;
        int directoryCount = 0;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
            {
                return new BrowserOpenSelection(BrowserOpenSelectionKind.Invalid, paths);
            }

            if (!TryResolvePathKind(
                    path,
                    currentSnapshotPathKinds,
                    allowFileSystemFallback,
                    out bool isFile,
                    out bool isDirectory))
            {
                return new BrowserOpenSelection(BrowserOpenSelectionKind.Invalid, paths);
            }

            if (isFile)
            {
                fileCount++;
            }
            else if (isDirectory)
            {
                directoryCount++;
            }
        }

        if (paths.Count == 1)
        {
            return fileCount == 1
                ? new BrowserOpenSelection(
                    ArchiveFileTypeHelper.IsArchive(paths[0])
                        ? BrowserOpenSelectionKind.SingleArchive
                        : BrowserOpenSelectionKind.SingleFile,
                    paths)
                : new BrowserOpenSelection(BrowserOpenSelectionKind.SingleDirectory, paths);
        }

        BrowserOpenSelectionKind kind = fileCount == paths.Count
            ? BrowserOpenSelectionKind.MultipleFiles
            : directoryCount == paths.Count
                ? BrowserOpenSelectionKind.MultipleDirectories
                : BrowserOpenSelectionKind.Mixed;
        return new BrowserOpenSelection(kind, paths);
    }

    public static bool IsCommandEnabled(string commandId, BrowserOpenSelection selection) =>
        commandId switch
        {
            CommandIds.BrowserExecute => selection.IsSingle,
            CommandIds.BrowserDefaultOpen => selection.Kind is
                BrowserOpenSelectionKind.SingleFile or
                BrowserOpenSelectionKind.SingleDirectory or
                BrowserOpenSelectionKind.SingleArchive,
            CommandIds.BrowserDefaultOpenMarked => selection.Kind == BrowserOpenSelectionKind.MultipleFiles,
            CommandIds.BrowserOpenWith => selection.Kind is
                BrowserOpenSelectionKind.SingleFile or
                BrowserOpenSelectionKind.SingleArchive,
            CommandIds.BrowserOpenInNewTab => selection.Kind is
                BrowserOpenSelectionKind.SingleDirectory or
                BrowserOpenSelectionKind.MultipleDirectories,
            _ => false
        };

    private static BrowserOpenSelection ResolveSingle(
        string? path,
        IReadOnlyDictionary<string, bool>? currentSnapshotPathKinds,
        bool allowFileSystemFallback)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new BrowserOpenSelection(BrowserOpenSelectionKind.None, Array.Empty<string>());
        }

        if (currentSnapshotPathKinds?.TryGetValue(path, out bool isDirectory) == true)
        {
            return isDirectory
                ? new BrowserOpenSelection(BrowserOpenSelectionKind.SingleDirectory, [path])
                : new BrowserOpenSelection(
                    ArchiveFileTypeHelper.IsArchive(path)
                        ? BrowserOpenSelectionKind.SingleArchive
                        : BrowserOpenSelectionKind.SingleFile,
                    [path]);
        }

        if (!allowFileSystemFallback)
        {
            return new BrowserOpenSelection(BrowserOpenSelectionKind.Invalid, [path]);
        }

        if (Directory.Exists(path))
        {
            return new BrowserOpenSelection(BrowserOpenSelectionKind.SingleDirectory, [path]);
        }

        if (!File.Exists(path))
        {
            return new BrowserOpenSelection(BrowserOpenSelectionKind.Invalid, [path]);
        }

        BrowserOpenSelectionKind kind = ArchiveFileTypeHelper.IsArchive(path)
            ? BrowserOpenSelectionKind.SingleArchive
            : BrowserOpenSelectionKind.SingleFile;
        return new BrowserOpenSelection(kind, [path]);
    }

    private static bool TryResolvePathKind(
        string path,
        IReadOnlyDictionary<string, bool>? currentSnapshotPathKinds,
        bool allowFileSystemFallback,
        out bool isFile,
        out bool isDirectory)
    {
        if (currentSnapshotPathKinds?.TryGetValue(path, out isDirectory) == true)
        {
            isFile = !isDirectory;
            return true;
        }

        if (!allowFileSystemFallback)
        {
            isFile = false;
            isDirectory = false;
            return false;
        }

        isFile = File.Exists(path);
        isDirectory = !isFile && Directory.Exists(path);
        return isFile || isDirectory;
    }
}
