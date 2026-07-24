using System.Text.RegularExpressions;

namespace MidFD.Services;

/// <summary>一般的なテキストから既存パスを取り出すための共通処理。</summary>
public static class PathTextIntakeService
{
    private static readonly Regex MarkdownLink = new(@"\[[^\]]*\]\((?<path>[^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex BacktickPath = new(@"`(?<path>[^`]+)`", RegexOptions.Compiled);

    public static string ExpandAndTrim(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '`' && text[^1] == '`')))
        {
            text = text[1..^1].Trim();
        }
        return Environment.ExpandEnvironmentVariables(text);
    }

    public static IReadOnlyList<string> ExtractCandidateTexts(string? text)
    {
        var candidates = new List<string>();
        foreach (string rawLine in (text ?? string.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsDeletedStatusLine(rawLine)) continue;
            string candidate = NormalizeLine(rawLine);
            candidate = NormalizeLine(candidate);
            if (candidate.Length == 0) continue;
            foreach (string extracted in ExtractCandidates(candidate))
            {
                string value = ExpandAndTrim(extracted);
                if (value.Length > 0) candidates.Add(value);
            }
        }
        return candidates;
    }

    public static IReadOnlyList<string> ExtractExistingPaths(
        string? text,
        string? currentDirectory = null,
        string? repositoryRoot = null,
        string? applicationDirectory = null,
        bool includeDirectories = true)
        => ExtractExistingPathsDetailed(text, currentDirectory, repositoryRoot, applicationDirectory, includeDirectories).ExistingPaths;

    public static PathTextIntakeResult ExtractExistingPathsDetailed(
        string? text,
        string? currentDirectory = null,
        string? repositoryRoot = null,
        string? applicationDirectory = null,
        bool includeDirectories = true)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int invalidPathCount = 0;
        foreach (string rawLine in (text ?? string.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsDeletedStatusLine(rawLine))
            {
                invalidPathCount++;
                continue;
            }

            string candidate = NormalizeLine(rawLine);
            candidate = NormalizeLine(candidate);
            if (candidate.Length == 0) continue;
            bool resolved = false;
            foreach (string extracted in ExtractCandidates(candidate))
            {
                candidate = ExpandAndTrim(extracted);
                if (candidate.Length == 0) continue;
                resolved |= TryAddCandidate(candidate, currentDirectory, repositoryRoot, applicationDirectory, includeDirectories, result, seen);
            }
            if (!resolved) invalidPathCount++;
        }
        return new PathTextIntakeResult(result, invalidPathCount);
    }

    private static bool TryAddCandidate(string candidate, string? currentDirectory, string? repositoryRoot, string? applicationDirectory, bool includeDirectories, List<string> result, HashSet<string> seen)
    {
            foreach (string baseDirectory in EnumerateBases(candidate, currentDirectory, repositoryRoot, applicationDirectory))
            {
                string resolved = Resolve(candidate, baseDirectory);
                if ((!includeDirectories && Directory.Exists(resolved)) || (!File.Exists(resolved) && !Directory.Exists(resolved))) continue;
                string identity = CanonicalIdentity(resolved);
                if (seen.Add(identity)) result.Add(resolved);
                return true;
            }
            return false;
    }

    private static bool IsDeletedStatusLine(string line)
        => Regex.IsMatch(line.Trim(), @"^(?:[-*+]\s+)?D\s+", RegexOptions.IgnoreCase);

    private static string NormalizeLine(string line)
    {
        string value = line.Trim();
        value = Regex.Replace(value, @"^\s*(?:[-*+]\s+|\d+[.)]\s+)", string.Empty);
        Match status = Regex.Match(value, @"^\s*([AMD]|R\d*)\s+", RegexOptions.IgnoreCase);
        if (status.Success)
        {
            if (status.Groups[1].Value.StartsWith("D", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            value = value[status.Length..].Trim();
            if (status.Groups[1].Value.StartsWith("R", StringComparison.OrdinalIgnoreCase))
            {
                string[] renameParts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                value = renameParts.Length > 1 ? renameParts[^1] : value;
            }
        }
        if (value.StartsWith("変更:", StringComparison.Ordinal) || value.StartsWith("FILES:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[(value.IndexOf(':') + 1)..].Trim();
        }
        return ExpandAndTrim(value);
    }

    private static IEnumerable<string> ExtractCandidates(string value)
    {
        Match link = MarkdownLink.Match(value);
        if (link.Success)
        {
            yield return link.Groups["path"].Value.Trim();
            yield break;
        }

        Match backtick = BacktickPath.Match(value);
        if (backtick.Success)
        {
            yield return backtick.Groups["path"].Value.Trim();
            yield break;
        }

        int separator = value.IndexOf(" — ", StringComparison.Ordinal);
        if (separator >= 0) value = value[..separator].Trim();
        int colon = value.IndexOf(": ", StringComparison.Ordinal);
        bool isDriveSeparator = colon == 1 && char.IsLetter(value[0]) && value.Length > 2 && (value[2] == '\\' || value[2] == '/');
        if (colon > 0 && !isDriveSeparator && !Path.IsPathRooted(value[..colon])) value = value[..colon].Trim();
        yield return value;
    }

    private static IEnumerable<string> EnumerateBases(string candidate, string? current, string? repository, string? applicationDirectory)
    {
        if (Path.IsPathRooted(candidate)) { yield return string.Empty; yield break; }
        if (!string.IsNullOrWhiteSpace(repository)) yield return repository;
        if (!string.IsNullOrWhiteSpace(current)) yield return current;
        if (!string.IsNullOrWhiteSpace(applicationDirectory)) yield return applicationDirectory;
    }

    private static string Resolve(string candidate, string baseDirectory)
    {
        string combined = Path.IsPathRooted(candidate) || string.IsNullOrEmpty(baseDirectory)
            ? candidate
            : Path.Combine(baseDirectory, candidate);
        try { return Path.GetFullPath(combined); }
        catch { return combined; }
    }

    public static string CanonicalIdentity(string path)
    {
        string full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public sealed record PathTextIntakeResult(IReadOnlyList<string> ExistingPaths, int InvalidPathCount);
