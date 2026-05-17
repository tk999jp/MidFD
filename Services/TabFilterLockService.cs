using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MidFD.Models;

namespace MidFD.Services;

public static class TabFilterLockService
{
    public static DirectoryResult Apply(
        string currentDirectory,
        IEnumerable<DirectoryInfo> directories,
        IEnumerable<FileInfo> files,
        TabFilterLockState? filter,
        Action<string>? onWarning = null)
    {
        var dirList = directories.ToList();
        var fileList = files.ToList();
        if (filter == null || !filter.Enabled || !filter.HasAnyCondition)
        {
            return new DirectoryResult { SelectedDirs = dirList, SelectedFiles = fileList };
        }

        if (filter.IncludeExtensions.Count > 0)
        {
            var allowed = filter.IncludeExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            fileList = fileList
                .Where(file => allowed.Contains(file.Extension.ToLowerInvariant()))
                .ToList();
        }

        if (filter.ModifiedFromLocal.HasValue)
        {
            DateTime from = TrimToMinute(filter.ModifiedFromLocal.Value);
            dirList = dirList.Where(dir => dir.LastWriteTime >= from).ToList();
            fileList = fileList.Where(file => file.LastWriteTime >= from).ToList();
        }

        if (filter.ModifiedToLocal.HasValue)
        {
            DateTime exclusiveTo = TrimToMinute(filter.ModifiedToLocal.Value).AddMinutes(1);
            dirList = dirList.Where(dir => dir.LastWriteTime < exclusiveTo).ToList();
            fileList = fileList.Where(file => file.LastWriteTime < exclusiveTo).ToList();
        }

        if (filter.GitUnignoredOnly)
        {
            var paths = dirList.Select(static dir => dir.FullName)
                .Concat(fileList.Select(static file => file.FullName))
                .ToList();
            if (GitIgnoreFilterService.TryGetIgnoredPaths(currentDirectory, paths, out HashSet<string> ignored, out string? warning))
            {
                dirList = dirList.Where(dir => !ignored.Contains(dir.FullName)).ToList();
                fileList = fileList.Where(file => !ignored.Contains(file.FullName)).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(warning))
            {
                onWarning?.Invoke(warning);
            }
        }

        return new DirectoryResult { SelectedDirs = dirList, SelectedFiles = fileList };
    }

    public static string BuildSummary(TabFilterLockState? filter)
    {
        if (filter == null || !filter.Enabled || !filter.HasAnyCondition)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (filter.IncludeExtensions.Count > 0)
        {
            parts.Add(string.Join(",", filter.IncludeExtensions));
        }

        if (filter.ModifiedFromLocal.HasValue)
        {
            parts.Add($"{TrimToMinute(filter.ModifiedFromLocal.Value):yyyy-MM-dd HH:mm}以降");
        }

        if (filter.ModifiedToLocal.HasValue)
        {
            parts.Add($"{TrimToMinute(filter.ModifiedToLocal.Value):yyyy-MM-dd HH:mm}以前");
        }

        if (filter.GitUnignoredOnly)
        {
            parts.Add("Git unignored");
        }

        string detail = string.Join(" | ", parts);
        return string.IsNullOrWhiteSpace(detail) ? "Filter: ON" : $"Filter: {detail}";
    }

    public static DateTime TrimToMinute(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);
    }
}
