using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Models;

namespace MidFD.Services;

public static class UnifiedFilterFindService
{
    public static UnifiedFilterFindSearchResult FindNames(
        string rootPath,
        UnifiedFilterFindCriteria criteria,
        CancellationToken cancellationToken = default,
        Action<UnifiedSearchProgress>? progress = null)
        => FindNamesCore(rootPath, criteria, cancellationToken, progress,
            path => new DirectoryInfo(path).EnumerateFileSystemInfos());

    internal static UnifiedFilterFindSearchResult FindNamesWithEnumerator(
        string rootPath,
        UnifiedFilterFindCriteria criteria,
        Func<string, IEnumerable<FileSystemInfo>> enumerateDirectory,
        CancellationToken cancellationToken = default,
        Action<UnifiedSearchProgress>? progress = null)
        => FindNamesCore(rootPath, criteria, cancellationToken, progress, enumerateDirectory);

    private static UnifiedFilterFindSearchResult FindNamesCore(
        string rootPath,
        UnifiedFilterFindCriteria criteria,
        CancellationToken cancellationToken,
        Action<UnifiedSearchProgress>? progress,
        Func<string, IEnumerable<FileSystemInfo>> enumerateDirectory)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ValidateNameCriteria(criteria);
        string root = NormalizeRoot(rootPath);
        var names = new List<UnifiedSearchNameResult>();
        var skipped = new List<string>();
        var pending = new Stack<string>();
        var reporter = new ProgressReporter(progress);
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directoryPath = pending.Pop();
            try
            {
                foreach (FileSystemInfo entry in enumerateDirectory(directoryPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = entry.FullName;
                    bool isDirectory = entry is DirectoryInfo;
                    if (isDirectory && ReparsePointHelper.ShouldRecurseIntoDirectory(path))
                    {
                        pending.Push(path);
                    }

                    bool matched = false;
                    try
                    {
                        if (NamePatternMatcher.IsMatch(entry.Name, criteria.NamePattern, criteria.NameUseRegex, criteria.NameCaseSensitive, cancellationToken) &&
                            MatchesDetail(entry, criteria.DetailFilter, rootPath))
                        {
                            names.Add(new UnifiedSearchNameResult(path, isDirectory));
                            matched = true;
                        }
                    }
                    catch (Exception ex) when (IsItemAccessFailure(ex))
                    {
                        skipped.Add($"{path}: {ex.Message}");
                    }
                    reporter.Update(path, !isDirectory, matched, 0, skipped.Count);
                }
            }
            catch (Exception ex) when (IsItemAccessFailure(ex))
            {
                skipped.Add($"{directoryPath}: {ex.Message}");
                reporter.Flush(directoryPath, skipped.Count);
            }
        }

        names.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
        reporter.Flush(root, skipped.Count);
        return new UnifiedFilterFindSearchResult(
            UnifiedFilterFindMode.FindNamesRecursively,
            names, [],
            skipped.Count,
            skipped);
    }

    public static Task<UnifiedFilterFindSearchResult> SearchContentsAsync(
        string rootPath,
        UnifiedFilterFindCriteria criteria,
        CancellationToken cancellationToken = default,
        Action<UnifiedSearchProgress>? progress = null,
        Action<ContentSearchBatch>? batch = null,
        IContentSearchEngine? engine = null,
        Action<ContentSearchBackendInfo>? backendSelected = null)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ValidateContentCriteria(criteria);
        var request = new ContentSearchRequest(NormalizeRoot(rootPath), criteria.Clone(), criteria.ContentEncoding)
        { BackendSelected = backendSelected };
        return (engine ?? new ContentSearchEngineResolver().Resolve()).SearchAsync(request, cancellationToken, progress, batch);
    }
    internal static bool MatchesDetail(FileSystemInfo entry, TabFilterLockState? detail, string rootPath)
    {
        if (detail == null || !detail.Enabled || !detail.HasAnyCondition)
        {
            return true;
        }

        if (detail.IncludeExtensions.Count > 0 && entry is not FileInfo)
        {
            return false;
        }

        if (entry is FileInfo file && detail.IncludeExtensions.Count > 0 &&
            !detail.IncludeExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        DateTime modified = entry.LastWriteTime;
        if (detail.ModifiedFromLocal.HasValue && modified < TabFilterLockService.TrimToMinute(detail.ModifiedFromLocal.Value))
        {
            return false;
        }
        if (detail.ModifiedToLocal.HasValue && modified >= TabFilterLockService.TrimToMinute(detail.ModifiedToLocal.Value).AddMinutes(1))
        {
            return false;
        }
        if (detail.GitUnignoredOnly &&
            GitIgnoreFilterService.TryGetIgnoredPaths(rootPath, new[] { entry.FullName }, out HashSet<string> ignored, out _) &&
            ignored.Contains(entry.FullName))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"検索ルートが見つかりません: {rootPath}");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    private static void ValidateNameCriteria(UnifiedFilterFindCriteria criteria)
    {
        if (!NamePatternMatcher.TryValidate(criteria.NamePattern, criteria.NameUseRegex, out string? error))
        {
            throw new ArgumentException($"名前条件の正規表現が不正です: {error}", nameof(criteria));
        }
    }

    internal static void ValidateContentCriteria(UnifiedFilterFindCriteria criteria)
    {
        if (string.IsNullOrWhiteSpace(criteria.ContentPattern))
        {
            throw new ArgumentException("内容検索の検索文字列を入力してください。", nameof(criteria));
        }
        if (criteria.ContentPattern.Contains('\n'))
            throw new ArgumentException("複数行をまたぐ検索は共通検索契約v1で未対応です。", nameof(criteria));
        if (!NamePatternMatcher.TryValidate(criteria.ContentPattern, criteria.ContentUseRegex, out string? error))
        {
            throw new ArgumentException($"内容検索の正規表現が不正です: {error}", nameof(criteria));
        }
        ValidateNameCriteria(criteria);
    }

    private static bool IsItemAccessFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DirectoryNotFoundException;

    private sealed class ProgressReporter(Action<UnifiedSearchProgress>? report)
    {
        private const int EntryInterval = 128;
        private static readonly TimeSpan TimeInterval = TimeSpan.FromMilliseconds(100);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _scannedEntries;
        private long _matchedEntries;
        private long _scannedFiles;
        private long _hits;
        private long _lastReportedEntryCount;
        private string _currentPath = string.Empty;

        public void Update(string path, bool isFile, bool matched, int hitDelta, int skippedCount)
        {
            _scannedEntries++;
            if (isFile) _scannedFiles++;
            if (matched) _matchedEntries++;
            _hits += hitDelta;
            _currentPath = path;
            if (report == null || (_scannedEntries - _lastReportedEntryCount < EntryInterval && _clock.Elapsed < TimeInterval)) return;
            Emit(skippedCount);
        }

        public void Flush(string path, int skippedCount)
        {
            _currentPath = path;
            Emit(skippedCount);
        }

        private void Emit(int skippedCount)
        {
            if (report == null) return;
            _lastReportedEntryCount = _scannedEntries;
            _clock.Restart();
            report(new UnifiedSearchProgress(_scannedEntries, _matchedEntries, _scannedFiles, _hits, skippedCount, _currentPath));
        }
    }
}
