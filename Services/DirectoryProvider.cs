using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// ディレクトリの内容を走査、フィルタリング、ソートして提供するサービス。
/// </summary>
public class DirectoryProvider
{
    public static DirectoryResult GetSortedEntries(
        string targetPath,
        string filterPattern,
        bool useRegex,
        bool showHiddenFiles,
        SortKind sortKind,
        bool ascending,
        TabFilterLockState? filterLock = null,
        Action<string>? onFilterError = null)
    {
        var dirInfo = new DirectoryInfo(targetPath);
        var allDirs = dirInfo.GetDirectories()
            .Where(d => showHiddenFiles || !d.Attributes.HasFlag(FileAttributes.Hidden))
            .ToArray();
        var allFiles = dirInfo.GetFiles()
            .Where(f => showHiddenFiles || !f.Attributes.HasFlag(FileAttributes.Hidden))
            .ToArray();

        var dirsArray = string.IsNullOrEmpty(filterPattern)
            ? allDirs
            : allDirs.Where(d => IsMatch(d.Name, filterPattern, useRegex, onFilterError)).ToArray();

        var filesArray = string.IsNullOrEmpty(filterPattern)
            ? allFiles
            : allFiles.Where(f => IsMatch(f.Name, filterPattern, useRegex, onFilterError)).ToArray();

        IEnumerable<DirectoryInfo> dirsReq = dirsArray;
        IEnumerable<FileInfo> filesReq = filesArray;
        if (filterLock != null && filterLock.Enabled)
        {
            DirectoryResult filtered = TabFilterLockService.Apply(
                targetPath,
                dirsReq,
                filesReq,
                filterLock,
                onFilterError);
            dirsReq = filtered.SelectedDirs;
            filesReq = filtered.SelectedFiles;
        }

        var nameComparer = new NaturalStringComparer();
        switch (sortKind)
        {
            case SortKind.Name:
                dirsReq = ascending
                    ? dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenBy(d => d.Name, nameComparer)
                    : dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenByDescending(d => d.Name, nameComparer);
                filesReq = ascending
                    ? filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenBy(f => f.Name, nameComparer)
                    : filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenByDescending(f => f.Name, nameComparer);
                break;

            case SortKind.Ext:
                dirsReq = ascending
                    ? dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenBy(d => d.Extension).ThenBy(d => d.Name, nameComparer)
                    : dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenByDescending(d => d.Extension).ThenByDescending(d => d.Name, nameComparer);
                filesReq = ascending
                    ? filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenBy(f => f.Extension).ThenBy(f => f.Name, nameComparer)
                    : filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenByDescending(f => f.Extension).ThenByDescending(f => f.Name, nameComparer);
                break;

            case SortKind.Size:
                // ディレクトリは既存どおり名前順維持（属性グループ内）
                dirsReq = ascending
                    ? dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenBy(d => d.Name, nameComparer)
                    : dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenByDescending(d => d.Name, nameComparer);
                filesReq = ascending
                    ? filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenBy(f => f.Length).ThenBy(f => f.Name, nameComparer)
                    : filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenByDescending(f => f.Length).ThenByDescending(f => f.Name, nameComparer);
                break;

            case SortKind.Date:
                dirsReq = ascending
                    ? dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenBy(d => d.LastWriteTime).ThenBy(d => d.Name, nameComparer)
                    : dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenByDescending(d => d.LastWriteTime).ThenByDescending(d => d.Name, nameComparer);
                filesReq = ascending
                    ? filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenBy(f => f.LastWriteTime).ThenBy(f => f.Name, nameComparer)
                    : filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenByDescending(f => f.LastWriteTime).ThenByDescending(f => f.Name, nameComparer);
                break;

            case SortKind.DateCreated:
                dirsReq = ascending
                    ? dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenBy(d => d.CreationTime).ThenBy(d => d.Name, nameComparer)
                    : dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenByDescending(d => d.CreationTime).ThenByDescending(d => d.Name, nameComparer);
                filesReq = ascending
                    ? filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenBy(f => f.CreationTime).ThenBy(f => f.Name, nameComparer)
                    : filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenByDescending(f => f.CreationTime).ThenByDescending(f => f.Name, nameComparer);
                break;

            case SortKind.DateAccessed:
                dirsReq = ascending
                    ? dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenBy(d => d.LastAccessTime).ThenBy(d => d.Name, nameComparer)
                    : dirsReq.OrderBy(d => GetAttributeSortRank(d.Attributes)).ThenByDescending(d => d.LastAccessTime).ThenByDescending(d => d.Name, nameComparer);
                filesReq = ascending
                    ? filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenBy(f => f.LastAccessTime).ThenBy(f => f.Name, nameComparer)
                    : filesReq.OrderBy(f => GetAttributeSortRank(f.Attributes)).ThenByDescending(f => f.LastAccessTime).ThenByDescending(f => f.Name, nameComparer);
                break;
        }

        return new DirectoryResult
        {
            SelectedDirs = dirsReq.ToList(),
            SelectedFiles = filesReq.ToList(),
            RawDirectoryEntryCount = allDirs.Length + allFiles.Length
        };
    }

    private static bool IsMatch(string name, string pattern, bool useRegex, Action<string>? onFilterError)
    {
        if (string.IsNullOrEmpty(pattern)) return true;

        try
        {
            if (useRegex)
            {
                return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);
            }
            else
            {
                if (pattern.Contains("*") || pattern.Contains("?"))
                {
                    string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase);
                }
                else
                {
                    return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex)
        {
            if (useRegex)
            {
                onFilterError?.Invoke($"Filter Error: {ex.Message}");
            }
            return false;
        }
    }

    private static int GetAttributeSortRank(FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.System))
            return 0;
        if (attributes.HasFlag(FileAttributes.Hidden))
            return 1;
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            return 2;

        // Archive only は通常扱い（独立グループ化しない）
        return 3;
    }
}
