using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

/// <summary>
/// Browser 一覧の directory load 前後パイプラインを組み立てる。
/// MainForm 側には UI 最終適用と例外ハンドリングを残す。
/// </summary>
public sealed class BrowserLoadCoordinator
{
    public sealed record DirectoryLoadRequest(
        string TargetPath,
        string? FocusTargetName,
        bool IsHistoryNavigation,
        bool SuppressRecent,
        string CurrentPath,
        int LastIndex,
        string? CurrentItemFullName,
        string FilterPattern,
        bool FilterUseRegex,
        bool ShowHiddenFiles,
        SortKind SortKind,
        bool SortAscending,
        TabFilterLockState? FilterLock,
        string? DateFormat,
        string? SizeFormat,
        bool ShowDirectoryMarker);

    public sealed record DirectoryLoadResult(
        string NewPath,
        string PreviousPath,
        bool IsReload,
        string? FocusTargetName,
        int LastIndex,
        bool IsHistoryNavigation,
        bool SuppressRecent,
        IReadOnlyList<ListViewItem> Items);

    public sealed class ExecutionContext
    {
        public required Action<string> ShowStatusMessage { get; init; }
        public required Action<ListViewItem, string> DecoratePathItem { get; init; }
    }

    public DirectoryLoadResult Execute(DirectoryLoadRequest request, ExecutionContext context)
    {
        var dirInfo = new DirectoryInfo(request.TargetPath);
        string newPath = dirInfo.FullName;
        string previousPath = request.CurrentPath;
        bool isReload = string.Equals(request.CurrentPath, newPath, StringComparison.OrdinalIgnoreCase);
        string? focusTargetName = request.FocusTargetName;

        if (isReload && string.IsNullOrEmpty(focusTargetName))
        {
            focusTargetName = request.CurrentItemFullName;
        }

        DirectoryResult result = DirectoryProvider.GetSortedEntries(
            request.TargetPath,
            request.FilterPattern,
            request.FilterUseRegex,
            request.ShowHiddenFiles,
            request.SortKind,
            request.SortAscending,
            request.FilterLock,
            msg => context.ShowStatusMessage(msg));

        List<ListViewItem> items = BuildListViewItems(result, dirInfo, request, context);

        return new DirectoryLoadResult(
            newPath,
            previousPath,
            isReload,
            focusTargetName,
            request.LastIndex,
            request.IsHistoryNavigation,
            request.SuppressRecent,
            items);
    }

    private static List<ListViewItem> BuildListViewItems(
        DirectoryResult result,
        DirectoryInfo dirInfo,
        DirectoryLoadRequest request,
        ExecutionContext context)
    {
        var items = new List<ListViewItem>();

        if (dirInfo.Parent != null)
        {
            var parentItem = new ListViewItem("..");
            parentItem.SubItems.Add("<DIR>");
            parentItem.SubItems.Add("");
            parentItem.SubItems.Add("");
            parentItem.SubItems.Add("");
            parentItem.Tag = null;
            items.Add(parentItem);
        }

        foreach (var d in result.SelectedDirs)
        {
            var item = FileSystemItemFactory.CreateDirectoryItem(
                d,
                request.DateFormat,
                request.ShowDirectoryMarker);
            context.DecoratePathItem(item, d.FullName);
            items.Add(item);
        }

        foreach (var f in result.SelectedFiles)
        {
            var item = FileSystemItemFactory.CreateFileItem(
                f,
                request.DateFormat,
                request.SizeFormat);
            context.DecoratePathItem(item, f.FullName);
            items.Add(item);
        }

        return items;
    }
}
