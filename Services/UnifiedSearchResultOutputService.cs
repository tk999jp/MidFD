using System;
using System.IO;
using System.Text;
using MidFD.Models;

namespace MidFD.Services;

public static class UnifiedSearchResultOutputService
{
    public static string? Write(UnifiedFilterFindSearchResult result)
    {
        if (result.ResultCount == 0)
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
        if (result.Mode == UnifiedFilterFindMode.FindNamesRecursively)
            return result.NameResults.Select(item => item.FullPath);
        return result.Files.SelectMany(file => file.Matches.Select(hit =>
            $"{file.FullPath}:{hit.LineNumber}:{hit.ColumnNumber}:{hit.LineText}"));
    }
}
