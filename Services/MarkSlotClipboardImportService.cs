using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MidFD.Services;

public enum MarkSlotClipboardImportFailureReason
{
    None,
    KdslResultNotFound,
    MultipleKdslResults,
    KdslResultFenceUnclosed,
    ChangeSectionNotFound,
    NoExplicitFiles,
    NoValidExistingFiles,
    InvalidEntriesDetected
}

public sealed record MarkSlotClipboardImportResult(
    IReadOnlyList<string> Paths,
    int SyntaxInvalidEntryCount,
    int MissingFileCount,
    int OutsideRepositoryPathCount,
    int DirectoryPathCount,
    int DuplicatePathCount,
    bool IsSuccess,
    MarkSlotClipboardImportFailureReason FailureReason,
    int IgnoredEarlierResultCount = 0,
    IReadOnlyList<string>? UnresolvedPaths = null)
{
    public int FatalCount => SyntaxInvalidEntryCount + OutsideRepositoryPathCount;
    public int ExcludedCount => MissingFileCount + DirectoryPathCount + DuplicatePathCount;
    public int InvalidPathCount => SyntaxInvalidEntryCount
        + MissingFileCount
        + OutsideRepositoryPathCount
        + DirectoryPathCount;
}

public static class MarkSlotClipboardImportService
{
    public static MarkSlotClipboardImportResult Extract(
        string? text,
        string? currentDirectory = null,
        string? repositoryRoot = null,
        string? applicationDirectory = null)
    {
        _ = currentDirectory;
        _ = applicationDirectory;

        CanonicalBlockResult canonical = ExtractCanonicalBlock(text);
        if (canonical.FailureReason.HasValue)
        {
            return Failure(canonical.FailureReason.Value);
        }

        string[] lines = canonical.Text!.Split('\n');
        int changeIndex = Array.FindIndex(lines, 1, line => string.Equals(line.Trim(), "変更:", StringComparison.Ordinal));
        if (changeIndex < 0)
        {
            return Failure(MarkSlotClipboardImportFailureReason.ChangeSectionNotFound, 0, canonical.IgnoredEarlierResultCount);
        }

        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return Failure(MarkSlotClipboardImportFailureReason.NoValidExistingFiles, 0, canonical.IgnoredEarlierResultCount);
        }

        string root;
        try
        {
            root = Path.GetFullPath(repositoryRoot.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return Failure(MarkSlotClipboardImportFailureReason.NoValidExistingFiles, 0, canonical.IgnoredEarlierResultCount);
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int syntaxInvalidEntryCount = 0;
        int missingFileCount = 0;
        int outsideRepositoryPathCount = 0;
        int directoryPathCount = 0;
        int duplicatePathCount = 0;
        bool hasAnyEntries = false;
        var unresolvedPaths = new List<string>();

        for (int index = changeIndex + 1; index < lines.Length; index++)
        {
            string rawLine = lines[index];
            string line = rawLine.Trim();
            if (IsSectionBoundary(rawLine))
            {
                break;
            }

            if (line.Length == 0 || IsCodeFence(line))
            {
                continue;
            }

            hasAnyEntries = true;

            string val = line;
            if (val.StartsWith("- ", StringComparison.Ordinal))
            {
                val = val[2..].Trim();
            }

            // バックティックチェック
            bool backtickMalformed = false;
            if (val.Contains('`'))
            {
                if (val.Length >= 2 && val.StartsWith('`') && val.EndsWith('`'))
                {
                    val = val[1..^1].Trim();
                    if (val.Contains('`'))
                    {
                        backtickMalformed = true;
                    }
                }
                else
                {
                    backtickMalformed = true;
                }
            }

            // 省略記号チェック
            bool hasEllipsis = val.Contains("...") || val.Contains('…');

            // 説明混在・無効文字・複数パス等のチェック
            bool hasInvalidChar = val.Any(c => Path.GetInvalidPathChars().Contains(c) || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|');

            // Windowsドライブレター以外のコロン
            bool isMultipleOrExplanation = false;
            if (val.Contains(':') && !(val.Length >= 2 && val[1] == ':' && char.IsLetter(val[0])))
            {
                isMultipleOrExplanation = true;
            }

            if (backtickMalformed || val.Length == 0 || hasEllipsis || hasInvalidChar || isMultipleOrExplanation)
            {
                syntaxInvalidEntryCount++;
                continue;
            }

            // セグメントチェック (. や ..)
            string[] segments = val.Split(new[] { '/', '\\' }, StringSplitOptions.None);
            bool outsideRepo = false;
            if (segments.Any(segment => segment is "." or ".."))
            {
                outsideRepo = true;
            }

            if (Path.IsPathRooted(val) || (val.Contains(':') && !(val.Length >= 2 && val[1] == ':' && char.IsLetter(val[0]))))
            {
                outsideRepo = true;
            }

            string? candidateFullPath = null;
            if (!outsideRepo)
            {
                try
                {
                    candidateFullPath = Path.GetFullPath(Path.Combine(root, val));
                    string prefix = root + Path.DirectorySeparatorChar;
                    if (!candidateFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        outsideRepo = true;
                    }
                }
                catch
                {
                    outsideRepo = true;
                }
            }

            if (outsideRepo)
            {
                outsideRepositoryPathCount++;
                continue;
            }

            if (Directory.Exists(candidateFullPath))
            {
                directoryPathCount++;
                unresolvedPaths.Add(val);
                continue;
            }

            if (!File.Exists(candidateFullPath))
            {
                missingFileCount++;
                unresolvedPaths.Add(val);
                continue;
            }

            // 重複一意化
            string identity = PathTextIntakeService.CanonicalIdentity(candidateFullPath!);
            if (seen.Add(identity))
            {
                paths.Add(candidateFullPath!);
            }
            else
            {
                duplicatePathCount++;
            }
        }

        if (!hasAnyEntries)
        {
            return Failure(MarkSlotClipboardImportFailureReason.NoExplicitFiles, syntaxInvalidEntryCount, canonical.IgnoredEarlierResultCount);
        }

        int fatalCount = syntaxInvalidEntryCount + outsideRepositoryPathCount;
        bool isSuccess = fatalCount == 0 && paths.Count > 0;

        MarkSlotClipboardImportFailureReason failureReason = MarkSlotClipboardImportFailureReason.None;
        if (!isSuccess)
        {
            if (fatalCount > 0)
            {
                failureReason = MarkSlotClipboardImportFailureReason.InvalidEntriesDetected;
            }
            else if (paths.Count == 0)
            {
                failureReason = MarkSlotClipboardImportFailureReason.NoValidExistingFiles;
            }
        }

        return new MarkSlotClipboardImportResult(
            paths,
            syntaxInvalidEntryCount,
            missingFileCount,
            outsideRepositoryPathCount,
            directoryPathCount,
            duplicatePathCount,
            isSuccess,
            failureReason,
            canonical.IgnoredEarlierResultCount,
            unresolvedPaths);
    }

    private static MarkSlotClipboardImportResult Failure(
        MarkSlotClipboardImportFailureReason reason,
        int syntaxInvalidEntryCount = 0,
        int ignoredEarlierResultCount = 0)
        => new(Array.Empty<string>(), syntaxInvalidEntryCount, 0, 0, 0, 0, false, reason, ignoredEarlierResultCount);

    private static CanonicalBlockResult ExtractCanonicalBlock(string? text)
    {
        string normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var markerIndexes = new List<int>();
        var fences = new List<FenceRange>();
        int? fenceStart = null;
        int fenceLength = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (string.Equals(line, "KDSL_RESULT:", StringComparison.Ordinal))
            {
                markerIndexes.Add(index);
            }

            if (fenceStart.HasValue)
            {
                if (IsClosingFence(line, fenceLength))
                {
                    fences.Add(new FenceRange(fenceStart.Value, index, fenceLength));
                    fenceStart = null;
                    fenceLength = 0;
                }

                continue;
            }

            if (TryGetOpeningFenceLength(line, out int openingLength))
            {
                fenceStart = index;
                fenceLength = openingLength;
            }
        }

        if (fenceStart.HasValue)
        {
            fences.Add(new FenceRange(fenceStart.Value, lines.Length, fenceLength));
        }

        if (markerIndexes.Count == 0)
        {
            return new CanonicalBlockResult(null, MarkSlotClipboardImportFailureReason.KdslResultNotFound, 0);
        }

        int ignoredEarlierResultCount = markerIndexes.Count - 1;
        int markerIndex = markerIndexes.Last();

        FenceRange? containingFence = fences.FirstOrDefault(fence => markerIndex > fence.Start && markerIndex < fence.End);
        if (containingFence != null)
        {
            if (containingFence.End >= lines.Length)
            {
                return new CanonicalBlockResult(null, MarkSlotClipboardImportFailureReason.KdslResultFenceUnclosed, ignoredEarlierResultCount);
            }

            return new CanonicalBlockResult(
                string.Join('\n', lines[markerIndex..containingFence.End]),
                null,
                ignoredEarlierResultCount);
        }

        var blockLines = new List<string>();
        for (int index = markerIndex; index < lines.Length; index++)
        {
            FenceRange? fence = fences.FirstOrDefault(candidate => index >= candidate.Start && index <= candidate.End);
            if (fence != null)
            {
                continue;
            }

            blockLines.Add(lines[index]);
        }

        return new CanonicalBlockResult(string.Join('\n', blockLines), null, ignoredEarlierResultCount);
    }

    private static bool TryGetOpeningFenceLength(string line, out int length)
    {
        length = 0;
        if (!line.StartsWith("```", StringComparison.Ordinal))
        {
            return false;
        }

        int count = line.TakeWhile(character => character == '`').Count();
        if (count < 3)
        {
            return false;
        }

        string language = line[count..].Trim();
        if (language.Contains('`', StringComparison.Ordinal))
        {
            return false;
        }

        length = count;
        return true;
    }

    private static bool IsClosingFence(string line, int openingLength)
        => line.Length >= openingLength
            && line.All(character => character == '`');

    private sealed record FenceRange(int Start, int End, int Length);

    private sealed record CanonicalBlockResult(
        string? Text,
        MarkSlotClipboardImportFailureReason? FailureReason,
        int IgnoredEarlierResultCount);

    private static readonly HashSet<string> KnownSectionKeys = new(StringComparer.Ordinal)
    {
        "状態", "要約", "理由", "検証", "実機", "未確認", "次", "実行", "危険", "commit", "変更"
    };

    private static bool IsSectionBoundary(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        string line = rawLine.Trim();
        if (line.StartsWith("#", StringComparison.Ordinal) || IsCodeFence(line))
        {
            return line.StartsWith("#", StringComparison.Ordinal);
        }

        if (line.StartsWith("-", StringComparison.Ordinal) || line.StartsWith("`", StringComparison.Ordinal))
        {
            return false;
        }

        if (KnownSectionKeys.Contains(line))
        {
            return true;
        }

        int colon = line.IndexOfAny(new[] { ':', '：' });
        if (colon <= 0)
        {
            return false;
        }

        string key = line[..colon].Trim();
        if (key.Length == 0 || key.Contains('/', StringComparison.Ordinal) || key.Contains('\\', StringComparison.Ordinal) || key.Contains('.', StringComparison.Ordinal) || key.Contains('`', StringComparison.Ordinal))
        {
            return false;
        }

        if (colon == 1 && char.IsLetter(line[0]) && line.Length > 2 && (line[2] == '\\' || line[2] == '/'))
        {
            return false;
        }

        return true;
    }

    private static bool IsCodeFence(string line)
        => line.StartsWith("```", StringComparison.Ordinal);
}
