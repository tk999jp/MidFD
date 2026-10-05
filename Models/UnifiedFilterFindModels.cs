using System;
using MidFD.Services;

namespace MidFD.Models;

public enum UnifiedFilterFindMode
{
    FilterCurrentTab,
    FindNamesRecursively,
    SearchContentsRecursively
}

public sealed class UnifiedFilterFindCriteria
{
    public string NamePattern { get; set; } = string.Empty;
    public bool NameUseRegex { get; set; }
    public bool NameCaseSensitive { get; set; }
    public TabFilterLockState DetailFilter { get; set; } = new();
    public string ContentPattern { get; set; } = string.Empty;
    public bool ContentUseRegex { get; set; }
    public bool ContentCaseSensitive { get; set; }
    public ContentSearchEncodingMode ContentEncoding { get; set; }

    public UnifiedFilterFindCriteria Clone()
    {
        return new UnifiedFilterFindCriteria
        {
            NamePattern = NamePattern ?? string.Empty,
            NameUseRegex = NameUseRegex,
            NameCaseSensitive = NameCaseSensitive,
            DetailFilter = DetailFilter?.Clone() ?? new TabFilterLockState(),
            ContentPattern = ContentPattern ?? string.Empty,
            ContentUseRegex = ContentUseRegex,
            ContentCaseSensitive = ContentCaseSensitive,
            ContentEncoding = ContentEncoding
        };
    }

    public bool HasDetailCondition => DetailFilter.Enabled && DetailFilter.HasAnyCondition;

    public bool HasActiveFilter =>
        !string.IsNullOrWhiteSpace(NamePattern) || HasDetailCondition;

    public static UnifiedFilterFindCriteria FromTab(
        string? namePattern,
        bool nameUseRegex,
        TabFilterLockState? detailFilter)
    {
        return new UnifiedFilterFindCriteria
        {
            NamePattern = namePattern ?? string.Empty,
            NameUseRegex = nameUseRegex,
            DetailFilter = detailFilter?.Clone() ?? new TabFilterLockState()
        };
    }
}

public sealed record UnifiedFilterFindInteractionRequest(
    int TargetTabIndex,
    string RootPath,
    UnifiedFilterFindCriteria CurrentCriteria,
    UnifiedFilterFindMode InitialMode,
    bool PreserveSearchDraft = false);

public sealed record UnifiedSearchNameResult(string FullPath, bool IsDirectory);
public sealed record UnifiedSearchMatch(string FullPath, int LineNumber, int ColumnNumber, string LineText, string MatchText = "");
public sealed record UnifiedSearchHitPreviewRequest(string FullPath, int LineNumber, int ColumnNumber, int MatchLength);
public sealed record UnifiedSearchProgress(long ScannedEntryCount, long MatchedEntryCount, long ScannedFileCount, long HitCount, int SkippedCount, string CurrentPath);

public sealed class UnifiedSearchResultFile
{
    public string FullPath { get; }
    public IReadOnlyList<UnifiedSearchMatch> Matches { get; }
    public UnifiedSearchResultFile(string fullPath, IEnumerable<UnifiedSearchMatch> matches)
    {
        FullPath = fullPath;
        Matches = Array.AsReadOnly(matches.ToArray());
    }
    internal UnifiedSearchResultFile(string fullPath, List<UnifiedSearchMatch> liveMatches, bool live)
    {
        FullPath = fullPath;
        Matches = liveMatches.AsReadOnly();
    }
}

public sealed class UnifiedFilterFindSearchResult
{
    public UnifiedFilterFindMode Mode { get; }
    public IReadOnlyList<UnifiedSearchNameResult> NameResults { get; }
    public IReadOnlyList<UnifiedSearchResultFile> Files { get; }
    public int SkippedCount { get; }
    public IReadOnlyList<string> SkipReasons { get; }
    public int ResultCount => Mode == UnifiedFilterFindMode.FindNamesRecursively ? NameResults.Count : Files.Count;
    public int FileCount => Files.Count;
    public int MatchCount { get; }
    public UnifiedFilterFindSearchResult(UnifiedFilterFindMode mode,
        IEnumerable<UnifiedSearchNameResult> nameResults, IEnumerable<UnifiedSearchResultFile> files,
        int skippedCount, IEnumerable<string> skipReasons)
    {
        Mode = mode;
        NameResults = Array.AsReadOnly(nameResults.ToArray());
        Files = Array.AsReadOnly(files.ToArray());
        SkippedCount = skippedCount;
        SkipReasons = Array.AsReadOnly(skipReasons.ToArray());
        MatchCount = mode == UnifiedFilterFindMode.FindNamesRecursively ? NameResults.Count : Files.Sum(f => f.Matches.Count);
    }
}
