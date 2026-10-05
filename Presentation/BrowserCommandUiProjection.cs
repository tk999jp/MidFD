using System;
using System.Collections.Generic;
using System.IO;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Presentation;

internal static class BrowserCommandUiProjection
{
    internal static bool ResolveFilterDetailActive(BrowserTabState? activeTab) =>
        TabFilterLockService.IsActive(string.Empty, activeTab?.FilterLock);

    public static CommandStateCoordinator.CommandUiSnapshot Build(
        bool isBrowserMode,
        bool isBusy,
        int selectionCount,
        bool hasTwoFileSelection,
        string? currentItemText,
        string? currentPath,
        bool isFileItem,
        SelectionResult? selectionSnapshot = null,
        IReadOnlyDictionary<string, bool>? currentSnapshotPathKinds = null,
        bool allowFileSystemFallback = true,
        bool canUndo = false,
        bool canRedo = false,
        string filterPattern = "",
        bool filterDetailActive = false)
    {
        CommandStateCoordinator.BrowserSelectionKind selectionKind = CommandStateCoordinator.BrowserSelectionKind.None;
        if (isBrowserMode && currentItemText != null)
        {
            if (currentItemText == "..")
            {
                selectionKind = CommandStateCoordinator.BrowserSelectionKind.ParentDirectory;
            }
            else if (isFileItem)
            {
                selectionKind = CommandStateCoordinator.BrowserSelectionKind.File;
                string extension = Path.GetExtension(currentPath ?? string.Empty);
                if (ArchiveFileTypeHelper.IsArchive(currentPath) || string.Equals(extension, ".lha", StringComparison.OrdinalIgnoreCase))
                {
                    selectionKind = CommandStateCoordinator.BrowserSelectionKind.ArchiveCandidate;
                }
            }
            else
            {
                selectionKind = CommandStateCoordinator.BrowserSelectionKind.Directory;
            }
        }

        return new CommandStateCoordinator().CreateCommandUiSnapshot(
            isBrowserMode,
            isBusy,
            selectionCount,
            hasTwoFileSelection,
            currentItemText,
            currentPath,
            selectionKind,
            BrowserOpenSelectionResolver.Resolve(
                selectionSnapshot,
                currentItemText,
                currentPath,
                currentSnapshotPathKinds,
                allowFileSystemFallback).Kind,
            filterPattern,
            filterDetailActive,
            canUndo,
            canRedo);
    }
}
