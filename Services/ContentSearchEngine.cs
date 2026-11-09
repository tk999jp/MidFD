using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MidFD.Models;

namespace MidFD.Services;

public interface IContentSearchEngine
{
    Task<UnifiedFilterFindSearchResult> SearchAsync(ContentSearchRequest request, CancellationToken cancellationToken = default,
        Action<UnifiedSearchProgress>? progress = null, Action<ContentSearchBatch>? batch = null);
}

internal sealed class ContentSearchResourceLimitException(string message) : InvalidOperationException(message);

internal enum ContentSearchExcludedRecordReason
{
    OversizedJsonEvent,
    LogicalLineTooLong,
    ExcessiveSubmatches
}

internal sealed class ContentSearchRecoverableRecordExclusionException(
    ContentSearchExcludedRecordReason reason, string message) : Exception(message)
{
    public ContentSearchExcludedRecordReason Reason { get; } = reason;
}

internal sealed class ContentSearchContext
{
    internal const int MaxLineChars = 4 * 1024 * 1024;
    internal const int MaxHits = 100_000;
    private const long MaxRetainedChars = 32L * 1024 * 1024;
    private long _retainedChars;
    private string? _previousLine;
    public ContentSearchRequest Request { get; }
    private readonly Action<UnifiedSearchProgress>? _progress;
    private readonly Action<ContentSearchBatch>? _batch;
    private readonly UnifiedSearchUiDiagnostics? _uiDiagnostics;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, List<UnifiedSearchMatch>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _diagnostics = [];
    private readonly List<UnifiedSearchMatch> _pending = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _scanned, _hits;
    private long _excludedRecords, _oversizedJsonEvents, _overlongLines, _excessiveSubmatches;
    private bool _firstBatch = true;
    private string _path;
    public long HitCount => _hits;
    public long ExcludedRecordCount => _excludedRecords;

    public ContentSearchContext(ContentSearchRequest request, CancellationToken token, Action<UnifiedSearchProgress>? progress,
        Action<ContentSearchBatch>? batch, UnifiedSearchUiDiagnostics? uiDiagnostics = null)
    {
        Request = request;
        _token = token;
        _progress = progress;
        _batch = batch;
        _uiDiagnostics = uiDiagnostics ?? UnifiedSearchUiDiagnostics.CurrentContentSearch;
        _path = request.RootPath;
    }

    public bool IsEligible(string path)
    {
        _token.ThrowIfCancellationRequested();
        string relative = Path.GetRelativePath(Request.RootPath, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return false;
        for (string? current = path; current != null && !string.Equals(current, Request.RootPath, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if (ReparsePointHelper.IsReparsePoint(current)) return false;
        var file = new FileInfo(path);
        return NamePatternMatcher.IsMatch(file.Name, Request.Criteria.NamePattern, Request.Criteria.NameUseRegex,
            Request.Criteria.NameCaseSensitive, _token) &&
            UnifiedFilterFindService.MatchesDetail(file, Request.Criteria.DetailFilter, Request.RootPath);
    }

    internal static StreamReader OpenText(string path, ContentSearchEncodingMode mode = ContentSearchEncodingMode.Auto)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            Span<byte> bom = stackalloc byte[4];
            int count = stream.Read(bom);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding = mode switch
            {
                ContentSearchEncodingMode.Auto or ContentSearchEncodingMode.Utf8 => new UTF8Encoding(false, true),
                ContentSearchEncodingMode.ShiftJis => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
                ContentSearchEncodingMode.Utf16Le => new UnicodeEncoding(false, true, true),
                ContentSearchEncodingMode.Utf16Be => new UnicodeEncoding(true, true, true),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            int offset = 0;
            if (count >= 4 && (bom.SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 }) || bom.SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff })))
                throw new DecoderFallbackException("UTF-32はencoding v1で未対応です。");
            if (count >= 2 && bom[0] == 0xff && bom[1] == 0xfe && mode is ContentSearchEncodingMode.Auto or ContentSearchEncodingMode.Utf16Le)
            { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
            else if (count >= 2 && bom[0] == 0xfe && bom[1] == 0xff && mode is ContentSearchEncodingMode.Auto or ContentSearchEncodingMode.Utf16Be)
            { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
            else if (count >= 3 && bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf && mode is ContentSearchEncodingMode.Auto or ContentSearchEncodingMode.Utf8) offset = 3;
            stream.Position = offset;
            return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);
        }
        catch { stream.Dispose(); throw; }
    }

    public void Scanned(string path)
    {
        _token.ThrowIfCancellationRequested();
        _scanned++;
        _path = path;
        if (_clock.ElapsedMilliseconds >= 100) Report();
    }

    public void Add(IEnumerable<UnifiedSearchMatch> matches)
    {
        foreach (UnifiedSearchMatch hit in matches)
        {
            _token.ThrowIfCancellationRequested();
            long additional = hit.MatchText.Length + (ReferenceEquals(_previousLine, hit.LineText) ? 0 : hit.LineText.Length);
            if (_hits >= MaxHits || _retainedChars + additional > MaxRetainedChars)
                throw new ContentSearchResourceLimitException("検索結果が保持資源上限に達したため中断しました。到着済みhitは部分結果です。条件・対象を絞って再検索してください。");
            if (_hits == 0) _uiDiagnostics?.MarkMilestone(UnifiedSearchUiMilestone.FirstHitGenerated);
            _retainedChars += additional;
            _previousLine = hit.LineText;
            if (!_files.TryGetValue(hit.FullPath, out var list)) _files[hit.FullPath] = list = [];
            list.Add(hit);
            _hits++;
            _path = hit.FullPath;
            _pending.Add(hit);
            if (_firstBatch || _pending.Count >= 64 || _clock.ElapsedMilliseconds >= 100) Flush();
        }
    }

    public void Diagnostic(string message)
    {
        if (_diagnostics.Count >= 1000) throw new ContentSearchResourceLimitException("検索diagnostic件数が上限に達したため中断しました。");
        _diagnostics.Add(message);
    }

    public void ExcludeRecord(ContentSearchExcludedRecordReason reason)
    {
        _token.ThrowIfCancellationRequested();
        if (_excludedRecords == long.MaxValue)
            throw new ContentSearchResourceLimitException("除外レコード件数が集計上限に達したため中断しました。");
        _excludedRecords++;
        switch (reason)
        {
            case ContentSearchExcludedRecordReason.OversizedJsonEvent:
                _oversizedJsonEvents = checked(_oversizedJsonEvents + 1);
                break;
            case ContentSearchExcludedRecordReason.LogicalLineTooLong:
                _overlongLines = checked(_overlongLines + 1);
                break;
            case ContentSearchExcludedRecordReason.ExcessiveSubmatches:
                _excessiveSubmatches = checked(_excessiveSubmatches + 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason));
        }
    }

    public void SetScannedCount(long count) { _scanned = count; Report(); }

    public void Flush()
    {
        _token.ThrowIfCancellationRequested();
        if (_pending.Count > 0)
        {
            var batch = new ContentSearchBatch(_pending.ToArray());
            _pending.Clear();
            _firstBatch = false;
            if (_batch is { } notify)
            {
                _uiDiagnostics?.MarkMilestone(UnifiedSearchUiMilestone.FirstBatchNotified);
                notify(batch);
            }
        }
        Report();
    }

    private void Report()
    {
        _token.ThrowIfCancellationRequested();
        _clock.Restart();
        _progress?.Invoke(new UnifiedSearchProgress(_scanned, _files.Count, _scanned, _hits, _diagnostics.Count, _path));
    }

    public UnifiedFilterFindSearchResult Complete()
    {
        Flush();
        return _excludedRecords > 0
            ? CreateResult(isPartial: true, incompleteReason: $"走査完了・一部結果除外: {BuildExcludedRecordSummary()}",
                isScanCompleteWithExclusions: true)
            : CreateResult(isPartial: false, incompleteReason: null);
    }

    public UnifiedFilterFindSearchResult CompletePartial(string reason)
    {
        string incompleteReason = string.IsNullOrWhiteSpace(reason) ? "検索が未完了です。" : reason;
        if (_excludedRecords > 0) incompleteReason += $" 先行除外: {BuildExcludedRecordSummary()}";
        return CreateResult(isPartial: true, incompleteReason: incompleteReason);
    }

    private string BuildExcludedRecordSummary()
    {
        var reasons = new List<string>(3);
        if (_oversizedJsonEvents > 0) reasons.Add($"16 Mi文字超JSONイベント {_oversizedJsonEvents:N0}件");
        if (_overlongLines > 0) reasons.Add($"4 Mi文字超論理行 {_overlongLines:N0}件");
        if (_excessiveSubmatches > 0) reasons.Add($"単一イベント内submatch上限超過 {_excessiveSubmatches:N0}件");
        return $"{_excludedRecords:N0}件を除外（{string.Join("、", reasons)}）";
    }

    private UnifiedFilterFindSearchResult CreateResult(bool isPartial, string? incompleteReason,
        bool isScanCompleteWithExclusions = false)
        => new(UnifiedFilterFindMode.SearchContentsRecursively, [],
            _files.Select(file => new UnifiedSearchResultFile(file.Key, file.Value)),
            _diagnostics.Count, _diagnostics, isPartial, incompleteReason,
            isScanCompleteWithExclusions, _excludedRecords);

    internal static bool CanPreservePartial(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException or AccessViolationException);

    internal static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or DecoderFallbackException;
}

public sealed class InternalContentSearchEngine : IContentSearchEngine
{
    public Task<UnifiedFilterFindSearchResult> SearchAsync(ContentSearchRequest request, CancellationToken cancellationToken = default,
        Action<UnifiedSearchProgress>? progress = null, Action<ContentSearchBatch>? batch = null)
    {
        UnifiedFilterFindService.ValidateContentCriteria(request.Criteria);
        request.BackendSelected?.Invoke(ContentSearchBackendInfo.Internal);
        return Task.Run(() => Search(request, cancellationToken, progress, batch), cancellationToken);
    }

    private static UnifiedFilterFindSearchResult Search(ContentSearchRequest request, CancellationToken token,
        Action<UnifiedSearchProgress>? progress, Action<ContentSearchBatch>? batch)
    {
        var context = new ContentSearchContext(request, token, progress, batch);
        try
        {
            var matcher = request.Criteria.ContentUseRegex
                ? MidFdSearchRegexContract.Create(request.Criteria.ContentPattern, request.Criteria.ContentCaseSensitive) : null;
            var directories = new Stack<string>();
            directories.Push(request.RootPath);
            while (directories.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry is DirectoryInfo)
                        {
                            if (ReparsePointHelper.ShouldRecurseIntoDirectory(entry.FullName)) directories.Push(entry.FullName);
                            continue;
                        }
                        context.Scanned(entry.FullName);
                        try
                        {
                            if (!context.IsEligible(entry.FullName)) continue;
                            using var reader = ContentSearchContext.OpenText(entry.FullName, request.Encoding);
                            int number = 0;
                            foreach ((string? Text, bool Excluded) record in ReadLines(reader, token,
                                () => context.Diagnostic($"{entry.FullName}: binary (NUL)")))
                            {
                                number++;
                                if (record.Excluded)
                                {
                                    context.ExcludeRecord(ContentSearchExcludedRecordReason.LogicalLineTooLong);
                                    continue;
                                }
                                string line = record.Text!;
                                var matches = matcher?.FindMatches(line, token) ?? MidFdSearchRegexContract.FindLiteralMatches(
                                    line, request.Criteria.ContentPattern, request.Criteria.ContentCaseSensitive, token);
                                context.Add(matches.Select(m => new UnifiedSearchMatch(
                                    entry.FullName, number, m.Index + 1, line, line.Substring(m.Index, m.Length))));
                            }
                            context.Flush();
                        }
                        catch (Exception ex) when (ContentSearchContext.IsReadFailure(ex)) { context.Diagnostic($"{entry.FullName}: {ex.Message}"); }
                    }
                }
                catch (Exception ex) when (ContentSearchContext.IsReadFailure(ex)) { context.Diagnostic($"{directory}: {ex.Message}"); }
            }
            return context.Complete();
        }
        catch (Exception ex) when ((context.HitCount > 0 || context.ExcludedRecordCount > 0) && ContentSearchContext.CanPreservePartial(ex))
        {
            return context.CompletePartial($"検索を完了できませんでした: {ex.Message}");
        }
    }

    private static IEnumerable<(string? Text, bool Excluded)> ReadLines(TextReader reader, CancellationToken token, Action binaryDetected)
    {
        char[] buffer = new char[4096];
        var line = new StringBuilder();
        bool excludingOverlongLine = false;
        bool pendingCarriageReturn = false;
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == '\0')
                {
                    binaryDetected();
                    if (pendingCarriageReturn) AppendLogicalCharacter(line, '\r', ref excludingOverlongLine);
                    if (excludingOverlongLine) yield return (null, true);
                    else if (line.Length > 0) yield return (line.ToString(), false);
                    yield break;
                }
                if (buffer[i] == '\n')
                {
                    pendingCarriageReturn = false;
                    if (excludingOverlongLine) yield return (null, true);
                    else yield return (line.ToString(), false);
                    line.Clear();
                    excludingOverlongLine = false;
                    continue;
                }
                if (buffer[i] == '\r')
                {
                    if (pendingCarriageReturn) AppendLogicalCharacter(line, '\r', ref excludingOverlongLine);
                    pendingCarriageReturn = true;
                    continue;
                }
                if (pendingCarriageReturn)
                {
                    AppendLogicalCharacter(line, '\r', ref excludingOverlongLine);
                    pendingCarriageReturn = false;
                }
                AppendLogicalCharacter(line, buffer[i], ref excludingOverlongLine);
            }
        }
        if (pendingCarriageReturn) AppendLogicalCharacter(line, '\r', ref excludingOverlongLine);
        if (excludingOverlongLine) yield return (null, true);
        else if (line.Length > 0) yield return (line.ToString(), false);
    }

    private static void AppendLogicalCharacter(StringBuilder line, char value, ref bool excludingOverlongLine)
    {
        if (excludingOverlongLine) return;
        if (line.Length >= ContentSearchContext.MaxLineChars)
        {
            line.Clear();
            excludingOverlongLine = true;
            return;
        }
        line.Append(value);
    }
}
