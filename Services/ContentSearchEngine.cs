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

internal sealed class ContentSearchContext
{
    public ContentSearchRequest Request { get; }
    private readonly Action<UnifiedSearchProgress>? _progress;
    private readonly Action<ContentSearchBatch>? _batch;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, List<UnifiedSearchMatch>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _diagnostics = [];
    private readonly List<UnifiedSearchMatch> _pending = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _scanned, _hits;
    private bool _firstBatch = true;
    private string _path;
    public long HitCount => _hits;

    public ContentSearchContext(ContentSearchRequest request, CancellationToken token, Action<UnifiedSearchProgress>? progress,
        Action<ContentSearchBatch>? batch)
    {
        Request = request;
        _token = token;
        _progress = progress;
        _batch = batch;
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
            if (!_files.TryGetValue(hit.FullPath, out var list)) _files[hit.FullPath] = list = [];
            list.Add(hit);
            _hits++;
            _path = hit.FullPath;
            _pending.Add(hit);
            if (_firstBatch || _pending.Count >= 64 || _clock.ElapsedMilliseconds >= 100) Flush();
        }
    }

    public void Diagnostic(string message) { _diagnostics.Add(message); }

    public void SetScannedCount(long count) { _scanned = count; Report(); }

    public void Flush()
    {
        _token.ThrowIfCancellationRequested();
        if (_pending.Count > 0)
        {
            var batch = new ContentSearchBatch(_pending.ToArray());
            _pending.Clear();
            _firstBatch = false;
            _batch?.Invoke(batch);
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
        return new(UnifiedFilterFindMode.SearchContentsRecursively, [],
            _files.Select(file => new UnifiedSearchResultFile(file.Key, file.Value)), _diagnostics.Count, _diagnostics);
    }

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
                        foreach (string line in ReadLines(reader, token, () => context.Diagnostic($"{entry.FullName}: binary (NUL)")))
                        {
                            number++;
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

    private static IEnumerable<string> ReadLines(TextReader reader, CancellationToken token, Action binaryDetected)
    {
        char[] buffer = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == '\0')
                {
                    binaryDetected();
                    if (line.Length > 0) yield return line.ToString();
                    yield break;
                }
                if (buffer[i] != '\n') { line.Append(buffer[i]); continue; }
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                yield return line.ToString();
                line.Clear();
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
