using System;
using System.IO;
using System.Text;
using MidFD.Models;

namespace MidFD.Services;

public static class UnifiedSearchResultOutputService
{
    public static string? Write(UnifiedFilterFindSearchResult result)
    {
        if (result.ResultCount == 0 && !result.IsPartial)
        {
            return null;
        }

        string directory = Path.Combine(Path.GetTempPath(), "MidFD", "search", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string fileName = result.Mode == UnifiedFilterFindMode.FindNamesRecursively
            ? "find-results.txt"
            : "content-search-results.txt";
        string path = Path.Combine(directory, fileName);
        File.WriteAllLines(path, EnumerateLines(result), new UTF8Encoding(false));
        return path;
    }

    public static IEnumerable<string> EnumerateLines(UnifiedFilterFindSearchResult result)
    {
        if (result.IsPartial)
        {
            yield return result.IsScanCompleteWithExclusions
                ? $"# 検索状態: 走査完了・一部結果除外（{result.ExcludedRecordCount:N0}件）"
                : "# 検索状態: 部分結果（検索未完了）";
            string reason = (result.IncompleteReason ?? "検索未完了です。")
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            yield return result.IsScanCompleteWithExclusions
                ? $"# 除外理由: {reason}"
                : $"# 未完了理由: {reason}";
        }

        if (result.Mode == UnifiedFilterFindMode.FindNamesRecursively)
        {
            foreach (UnifiedSearchNameResult item in result.NameResults) yield return item.FullPath;
            yield break;
        }

        foreach (UnifiedSearchResultFile file in result.Files)
            foreach (UnifiedSearchMatch hit in file.Matches)
                yield return $"{file.FullPath}:{hit.LineNumber}:{hit.ColumnNumber}:{hit.LineText}";
    }
}
