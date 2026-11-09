using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MidFD.Models;

namespace MidFD.Services;

public enum MarkSlotClipboardImportFailureReason
{
    None,
    KdslResultNotFound,
    MultipleKdslResults,
    KdslResultFenceUnclosed,
    NoChangesDeclared,
    MalformedChangeSection,
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
    IReadOnlyList<string>? UnresolvedPaths = null,
    IReadOnlyList<MarkSlotClipboardEntryDiagnostic>? EntryDiagnostics = null,
    int? DeclaredCount = null,
    int ListedEntryCount = 0)
{
    public int AmbiguousEntryCount => EntryDiagnostics?.Count(static entry =>
        entry.Kind == MarkSlotClipboardEntryDiagnosticKind.Ambiguous) ?? 0;
    public int RecoveredEntryCount => EntryDiagnostics?.Count(static entry =>
        entry.Kind == MarkSlotClipboardEntryDiagnosticKind.RecoveredDescription) ?? 0;
    public int FatalCount => SyntaxInvalidEntryCount + OutsideRepositoryPathCount + AmbiguousEntryCount;
    public int ExcludedCount => MissingFileCount + DirectoryPathCount + DuplicatePathCount;
    public bool CountMismatch => DeclaredCount.HasValue && DeclaredCount.Value != ListedEntryCount;
    public int InvalidPathCount => SyntaxInvalidEntryCount
        + MissingFileCount
        + OutsideRepositoryPathCount
        + DirectoryPathCount
        + AmbiguousEntryCount;
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
        string? root = null;
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            try
            {
                root = NormalizeRepositoryRoot(repositoryRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                // Keep the change-section diagnostic distinct from an invalid repository root.
            }
        }

        ChangeSection? changeSection = FindChangeSection(lines, root);
        if (changeSection == null)
        {
            MarkSlotClipboardImportFailureReason reason = lines.Skip(1).Any(IsMalformedChangeHeader)
                ? MarkSlotClipboardImportFailureReason.MalformedChangeSection
                : MarkSlotClipboardImportFailureReason.NoChangesDeclared;
            return Failure(reason, 0, canonical.IgnoredEarlierResultCount);
        }

        if (root == null)
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
        int listedEntryCount = 0;
        bool hasAnyEntries = false;
        var unresolvedPaths = new List<string>();
        var entryDiagnostics = new List<MarkSlotClipboardEntryDiagnostic>();

        if (changeSection.InlineEntry is { } inlineEntry)
        {
            hasAnyEntries = true;
            listedEntryCount++;
            ProcessEntry(inlineEntry, root, paths, seen, unresolvedPaths, entryDiagnostics,
                ref syntaxInvalidEntryCount, ref missingFileCount, ref outsideRepositoryPathCount,
                ref directoryPathCount, ref duplicatePathCount);
        }

        for (int index = changeSection.HeaderIndex + 1; index < lines.Length; index++)
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
            listedEntryCount++;
            ProcessEntry(rawLine, root, paths, seen, unresolvedPaths, entryDiagnostics,
                ref syntaxInvalidEntryCount, ref missingFileCount, ref outsideRepositoryPathCount,
                ref directoryPathCount, ref duplicatePathCount);
        }

        if (!hasAnyEntries)
        {
            return Failure(MarkSlotClipboardImportFailureReason.NoExplicitFiles, syntaxInvalidEntryCount, canonical.IgnoredEarlierResultCount);
        }

        int fatalCount = syntaxInvalidEntryCount + outsideRepositoryPathCount
            + entryDiagnostics.Count(static entry => entry.Kind == MarkSlotClipboardEntryDiagnosticKind.Ambiguous);
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
            unresolvedPaths,
            entryDiagnostics,
            changeSection.DeclaredCount,
            listedEntryCount);
    }

    private static void ProcessEntry(
        string rawEntry,
        string repositoryRoot,
        List<string> paths,
        HashSet<string> seen,
        List<string> unresolvedPaths,
        List<MarkSlotClipboardEntryDiagnostic> diagnostics,
        ref int syntaxInvalidEntryCount,
        ref int missingFileCount,
        ref int outsideRepositoryPathCount,
        ref int directoryPathCount,
        ref int duplicatePathCount)
    {
        EntryResolution resolution = ResolveEntry(rawEntry, repositoryRoot);
        if (resolution.IsRecovered)
        {
            diagnostics.Add(new MarkSlotClipboardEntryDiagnostic(
                rawEntry,
                MarkSlotClipboardEntryDiagnosticKind.RecoveredDescription,
                resolution.CandidatePath));
        }

        switch (resolution.Kind)
        {
            case EntryResolutionKind.File:
            {
                string fullPath = resolution.FullPath!;
                string identity = PathTextIntakeService.CanonicalIdentity(fullPath);
                if (seen.Add(identity))
                {
                    paths.Add(fullPath);
                }
                else
                {
                    duplicatePathCount++;
                    diagnostics.Add(new MarkSlotClipboardEntryDiagnostic(
                        rawEntry,
                        MarkSlotClipboardEntryDiagnosticKind.Duplicate,
                        resolution.CandidatePath));
                }
                break;
            }
            case EntryResolutionKind.Directory:
                directoryPathCount++;
                unresolvedPaths.Add(resolution.CandidatePath!);
                diagnostics.Add(new MarkSlotClipboardEntryDiagnostic(
                    rawEntry,
                    MarkSlotClipboardEntryDiagnosticKind.Directory,
                    resolution.CandidatePath));
                break;
            case EntryResolutionKind.Missing:
                missingFileCount++;
                unresolvedPaths.Add(resolution.CandidatePath!);
                diagnostics.Add(new MarkSlotClipboardEntryDiagnostic(
                    rawEntry,
                    MarkSlotClipboardEntryDiagnosticKind.MissingFile,
                    resolution.CandidatePath));
                break;
            case EntryResolutionKind.OutsideRepository:
                outsideRepositoryPathCount++;
                diagnostics.Add(new MarkSlotClipboardEntryDiagnostic(
                    rawEntry,
                    MarkSlotClipboardEntryDiagnosticKind.OutsideRepository,
                    resolution.CandidatePath));
                break;
            case EntryResolutionKind.Ambiguous:
                diagnostics.Add(new MarkSlotClipboardEntryDiagnostic(
                    rawEntry,
                    MarkSlotClipboardEntryDiagnosticKind.Ambiguous));
                break;
            default:
                syntaxInvalidEntryCount++;
                diagnostics.Add(new MarkSlotClipboardEntryDiagnostic(
                    rawEntry,
                    MarkSlotClipboardEntryDiagnosticKind.SyntaxInvalid,
                    resolution.CandidatePath));
                break;
        }
    }

    private static EntryResolution ResolveEntry(string rawEntry, string repositoryRoot)
    {
        string value = rawEntry.Trim();
        if (value.StartsWith("- ", StringComparison.Ordinal))
        {
            value = value[2..].Trim();
        }

        if (value.Length == 0)
        {
            return EntryResolution.Syntax(rawEntry);
        }

        if (value.StartsWith('`'))
        {
            int close = value.IndexOf('`', 1);
            if (close <= 1)
            {
                return EntryResolution.Syntax(rawEntry);
            }

            string codePath = value[1..close];
            string suffix = value[(close + 1)..];
            if (codePath.Contains('`') || suffix.Contains('`') || !HasValidCodeSpanSuffix(suffix))
            {
                return EntryResolution.Syntax(rawEntry, codePath);
            }

            if (!string.IsNullOrWhiteSpace(suffix)
                && HasExistingPathInterpretation(suffix.Trim(), repositoryRoot))
            {
                return EntryResolution.AmbiguousEntry(rawEntry);
            }

            EntryResolution codeResolution = EvaluatePath(codePath, repositoryRoot);
            return string.IsNullOrWhiteSpace(suffix)
                ? codeResolution
                : codeResolution with { IsRecovered = true };
        }

        if (value.Contains('`'))
        {
            return EntryResolution.Syntax(rawEntry, value);
        }

        // 完全一致する既存file／directoryを最優先し、file名内の空白・括弧・ハイフンを保持する。
        EntryResolution exact = EvaluatePath(value, repositoryRoot);
        if (exact.Kind is EntryResolutionKind.File or EntryResolutionKind.Directory)
        {
            return exact;
        }

        if (HasMultipleExistingPathParts(value, repositoryRoot))
        {
            return EntryResolution.AmbiguousEntry(rawEntry);
        }

        List<string> candidates = GetExplanationPathCandidates(value);
        if (candidates.Count == 0)
        {
            return exact;
        }

        List<EntryResolution> evaluated = candidates
            .Distinct(StringComparer.Ordinal)
            .Select(candidate => EvaluatePath(candidate, repositoryRoot))
            .ToList();
        List<EntryResolution> existing = evaluated
            .Where(static candidate => candidate.Kind is EntryResolutionKind.File or EntryResolutionKind.Directory)
            .GroupBy(static candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();

        if (existing.Count > 1)
        {
            return EntryResolution.AmbiguousEntry(rawEntry);
        }

        if (existing.Count == 1)
        {
            return existing[0] with { IsRecovered = true };
        }

        List<EntryResolution> safeCandidates = evaluated
            .Where(static candidate => candidate.Kind is EntryResolutionKind.Missing or EntryResolutionKind.File or EntryResolutionKind.Directory)
            .GroupBy(static candidate => candidate.CandidatePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (safeCandidates.Count == 1)
        {
            return safeCandidates[0] with { IsRecovered = true };
        }

        if (safeCandidates.Count > 1)
        {
            return EntryResolution.AmbiguousEntry(rawEntry);
        }

        EntryResolution? outside = evaluated.FirstOrDefault(static candidate =>
            candidate.Kind == EntryResolutionKind.OutsideRepository);
        return outside ?? exact;
    }

    private static bool HasValidCodeSpanSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return true;
        }

        char first = suffix[0];
        return char.IsWhiteSpace(first) || first is '：' or ':' or '—' or '-' or '（' or '(';
    }

    private static bool HasMultipleExistingPathParts(string value, string repositoryRoot)
    {
        foreach (string separator in new[] { ", ", "; ", "、" })
        {
            int index = value.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= value.Length)
            {
                continue;
            }

            EntryResolution left = EvaluatePath(value[..index].Trim(), repositoryRoot);
            EntryResolution right = EvaluatePath(value[(index + separator.Length)..].Trim(), repositoryRoot);
            if (left.Kind == EntryResolutionKind.File && right.Kind == EntryResolutionKind.File
                && !string.Equals(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> GetExplanationPathCandidates(string value)
    {
        var candidates = new List<string>();
        AddSeparatedCandidates(value, "：", candidates);
        AddSpacedSeparatorCandidates(value, " — ", candidates);
        AddSpacedSeparatorCandidates(value, " - ", candidates);
        AddParenthesizedDescriptionCandidates(value, "（", "）", candidates);
        AddParenthesizedDescriptionCandidates(value, "(", ")", candidates);
        return candidates;
    }

    private static bool HasExistingPathInterpretation(string value, string repositoryRoot)
    {
        EntryResolution exact = EvaluatePath(value, repositoryRoot);
        if (exact.Kind is EntryResolutionKind.File or EntryResolutionKind.Directory)
        {
            return true;
        }

        // Only reuse the existing explicit description delimiters. Do not search arbitrary prose for path fragments.
        return GetExplanationPathCandidates(value)
            .Distinct(StringComparer.Ordinal)
            .Select(candidate => EvaluatePath(candidate, repositoryRoot))
            .Any(static candidate =>
                candidate.Kind is EntryResolutionKind.File or EntryResolutionKind.Directory);
    }

    private static void AddSeparatedCandidates(string value, string separator, List<string> candidates)
    {
        int start = 0;
        while ((start = value.IndexOf(separator, start, StringComparison.Ordinal)) >= 0)
        {
            int split = start;
            string suffix = value[(split + separator.Length)..].Trim();
            string prefix = value[..split].Trim();
            if (prefix.Length > 0 && suffix.Length > 0)
            {
                candidates.Add(prefix);
            }
            start += separator.Length;
        }
    }

    private static void AddSpacedSeparatorCandidates(string value, string separator, List<string> candidates)
    {
        int start = 0;
        while ((start = value.IndexOf(separator, start, StringComparison.Ordinal)) >= 0)
        {
            string prefix = value[..start].Trim();
            string suffix = value[(start + separator.Length)..].Trim();
            if (prefix.Length > 0 && suffix.Length > 0)
            {
                candidates.Add(prefix);
            }
            start += separator.Length;
        }
    }

    private static void AddParenthesizedDescriptionCandidates(
        string value,
        string opening,
        string closing,
        List<string> candidates)
    {
        int start = value.IndexOf(opening, StringComparison.Ordinal);
        if (start <= 0 || !char.IsWhiteSpace(value[start - 1]) || !value.EndsWith(closing, StringComparison.Ordinal))
        {
            return;
        }

        int contentStart = start + opening.Length;
        int contentLength = value.Length - contentStart - closing.Length;
        if (contentLength > 0 && !string.IsNullOrWhiteSpace(value.Substring(contentStart, contentLength)))
        {
            string prefix = value[..start].TrimEnd();
            if (prefix.Length > 0)
            {
                candidates.Add(prefix);
            }
        }
    }

    private static EntryResolution EvaluatePath(string candidatePath, string repositoryRoot)
    {
        if (candidatePath.Length == 0
            || candidatePath.Contains("...", StringComparison.Ordinal)
            || candidatePath.Contains('…')
            || candidatePath.Any(character => Path.GetInvalidPathChars().Contains(character)
                || character is '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return EntryResolution.Syntax(candidatePath, candidatePath);
        }

        string[] segments = candidatePath.Split(new[] { '/', '\\' }, StringSplitOptions.None);
        if (segments.Any(static segment => segment is "." or ".."))
        {
            return EntryResolution.Outside(candidatePath);
        }

        if (Path.IsPathRooted(candidatePath))
        {
            return EntryResolution.Outside(candidatePath);
        }

        if (candidatePath.Contains(':'))
        {
            return EntryResolution.Syntax(candidatePath, candidatePath);
        }

        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, candidatePath));
            string prefix = repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
                || repositoryRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? repositoryRoot
                : repositoryRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return EntryResolution.Outside(candidatePath);
            }

            if (Directory.Exists(fullPath))
            {
                return EntryResolution.Directory(candidatePath, fullPath);
            }

            return File.Exists(fullPath)
                ? EntryResolution.File(candidatePath, fullPath)
                : EntryResolution.Missing(candidatePath, fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return EntryResolution.Outside(candidatePath);
        }
    }

    private static ChangeSection? FindChangeSection(string[] lines, string? repositoryRoot)
    {
        for (int index = 1; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line is "変更:" or "変更：")
            {
                return new ChangeSection(index, null, null);
            }

            if (!TryGetChangeHeaderValue(line, out string value))
            {
                continue;
            }

            if (IsChangeCountHeader(value))
            {
                if (!TryParseChangeCountHeader(value, out int declaredCount))
                {
                    // An out-of-range count is malformed, not a reason to reinterpret the section.
                    return null;
                }

                if (repositoryRoot != null && HasExplicitEntryAfter(lines, index))
                {
                    return new ChangeSection(index, null, declaredCount);
                }

                continue;
            }

            if (repositoryRoot != null)
            {
                EntryResolution inline = ResolveEntry(value, repositoryRoot);
                if (inline.Kind == EntryResolutionKind.File)
                {
                    return new ChangeSection(index, value, null);
                }
            }
        }

        return null;
    }

    private static bool HasExplicitEntryAfter(string[] lines, int headerIndex)
    {
        for (int index = headerIndex + 1; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (IsSectionBoundary(lines[index]))
            {
                break;
            }
            if (line.Length == 0 || IsCodeFence(line))
            {
                continue;
            }
            return true;
        }

        return false;
    }

    private static bool TryGetChangeHeaderValue(string line, out string value)
    {
        value = string.Empty;
        if (!line.StartsWith("変更", StringComparison.Ordinal) || line.Length <= "変更".Length)
        {
            return false;
        }

        string remainder = line["変更".Length..].TrimStart();
        if (remainder.Length == 0 || remainder[0] is not (':' or '：'))
        {
            return false;
        }

        value = remainder[1..].Trim();
        return true;
    }

    private static bool IsChangeCountHeader(string value)
        => Regex.IsMatch(
            value,
            @"^\d+\s*(?:path(?:s)?|files?|件)(?:\s*[（(].*[）)])?[。.]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool TryParseChangeCountHeader(string value, out int declaredCount)
    {
        declaredCount = 0;
        Match match = Regex.Match(
            value,
            @"^(?<count>\d+)\s*(?:path(?:s)?|files?|件)(?:\s*[（(].*[）)])?[。.]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            && int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out declaredCount);
    }

    private enum EntryResolutionKind
    {
        File,
        Directory,
        Missing,
        SyntaxInvalid,
        OutsideRepository,
        Ambiguous
    }

    private sealed record EntryResolution(
        EntryResolutionKind Kind,
        string? CandidatePath,
        string? FullPath = null,
        bool IsRecovered = false)
    {
        public static EntryResolution File(string candidate, string fullPath) =>
            new(EntryResolutionKind.File, candidate, fullPath);
        public static EntryResolution Directory(string candidate, string fullPath) =>
            new(EntryResolutionKind.Directory, candidate, fullPath);
        public static EntryResolution Missing(string candidate, string fullPath) =>
            new(EntryResolutionKind.Missing, candidate, fullPath);
        public static EntryResolution Syntax(string? rawEntry, string? candidate = null) =>
            new(EntryResolutionKind.SyntaxInvalid, candidate ?? rawEntry);
        public static EntryResolution Outside(string candidate) =>
            new(EntryResolutionKind.OutsideRepository, candidate);
        public static EntryResolution AmbiguousEntry(string rawEntry) =>
            new(EntryResolutionKind.Ambiguous, rawEntry);
    }

    private sealed record ChangeSection(int HeaderIndex, string? InlineEntry, int? DeclaredCount);

    private static MarkSlotClipboardImportResult Failure(
        MarkSlotClipboardImportFailureReason reason,
        int syntaxInvalidEntryCount = 0,
        int ignoredEarlierResultCount = 0)
        => new(Array.Empty<string>(), syntaxInvalidEntryCount, 0, 0, 0, 0, false, reason, ignoredEarlierResultCount);

    private static string NormalizeRepositoryRoot(string repositoryRoot)
    {
        string fullPath = Path.GetFullPath(repositoryRoot.Trim());
        string? filesystemRoot = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(filesystemRoot) &&
            string.Equals(fullPath, filesystemRoot, StringComparison.OrdinalIgnoreCase))
        {
            return filesystemRoot;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

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

    private static bool IsMalformedChangeHeader(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Equals("変更:", StringComparison.Ordinal)
            || !trimmed.StartsWith("変更", StringComparison.Ordinal))
        {
            return false;
        }

        if (TryGetChangeHeaderValue(trimmed, out string value)
            && IsChangeCountHeader(value)
            && TryParseChangeCountHeader(value, out _))
        {
            return false;
        }

        string remainder = trimmed["変更".Length..].TrimStart();
        return remainder.StartsWith(':') || remainder.StartsWith('：');
    }
}
