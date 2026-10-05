using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MidFD.Models;

namespace MidFD.Services;

internal sealed record ContentSearchProcessResult(int ExitCode, string StandardError);
internal interface IContentSearchProcessRunner
{
    Task<ContentSearchProcessResult> RunAsync(ProcessStartInfo startInfo, Action<string> stdout, CancellationToken token);
}

internal sealed class ContentSearchProcessRunner : IContentSearchProcessRunner
{
    public async Task<ContentSearchProcessResult> RunAsync(ProcessStartInfo startInfo, Action<string> stdout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var registration = token.Register(() => Kill(process));
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                token.ThrowIfCancellationRequested();
                stdout(line);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(process.ExitCode, await stderr.ConfigureAwait(false));
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // Drain both pipes on parsing/callback failure as well as cancellation.
            await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), stderr).ConfigureAwait(false);
        }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

internal sealed record RipgrepSearchEvent(string Kind, string? Path, IReadOnlyList<UnifiedSearchMatch> Matches,
    long? ScannedFiles = null, long? BinaryOffset = null, string? LineText = null, int LineNumber = 0,
    string? TextDiagnostic = null);

internal static class RipgrepSearchJsonParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static RipgrepSearchEvent Parse(string json, string root, Func<string, bool>? shouldDecodeMatch = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var record = document.RootElement;
            string kind = record.GetProperty("type").GetString() ?? throw new FormatException("rg event type missing");
            var data = record.GetProperty("data");
            if (kind == "summary") return new(kind, null, [], data.GetProperty("stats").GetProperty("searches").GetInt64());
            if (kind is not ("begin" or "match" or "end" or "context")) throw new FormatException($"Unknown rg event: {kind}");
            string path = Path.GetFullPath(ReadText(data.GetProperty("path")), root);
            if (kind == "end") return new(kind, path, [], BinaryOffset:
                data.TryGetProperty("binary_offset", out var binary) && binary.ValueKind != JsonValueKind.Null ? binary.GetInt64() : null);
            if (kind != "match") return new(kind, path, []);
            if (shouldDecodeMatch?.Invoke(path) == false) return new(kind, path, []);
            byte[] bytes = ReadBytes(data.GetProperty("lines"));
            int logicalLength = bytes.Length;
            if (logicalLength > 0 && bytes[logicalLength - 1] == '\n')
            {
                logicalLength--;
                if (logicalLength > 0 && bytes[logicalLength - 1] == '\r') logicalLength--;
            }
            string text;
            try { text = StrictUtf8.GetString(bytes, 0, logicalLength); }
            catch (DecoderFallbackException ex) { return new(kind, path, [], TextDiagnostic: ex.Message); }
            int number = data.GetProperty("line_number").GetInt32();
            if (number < 1 || text.Contains('\n')) throw new FormatException("rg multiline event is outside the line contract");
            var hits = new List<UnifiedSearchMatch>();
            foreach (var submatch in data.GetProperty("submatches").EnumerateArray())
            {
                int start = submatch.GetProperty("start").GetInt32(), end = submatch.GetProperty("end").GetInt32();
                if (start < 0 || end < start || end > bytes.Length) throw new FormatException("rg submatch offset is outside the line");
                // rg's byte-oriented empty-match iteration can report offsets inside a UTF-8 scalar
                // and inside the line terminator. Neither is a Product coordinate.
                if (start > logicalLength || end > logicalLength) continue;
                int column;
                try { column = StrictUtf8.GetCharCount(bytes, 0, start) + 1; }
                catch (DecoderFallbackException) when (start == end) { continue; }
                string matched = StrictUtf8.GetString(bytes, start, end - start);
                hits.Add(new(path, number, column, text, matched));
            }
            return new(kind, path, hits, LineText: text, LineNumber: number);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or DecoderFallbackException or ArgumentException or OverflowException)
        { throw new FormatException("ripgrep JSONを共通検索結果へ変換できません。", ex); }
    }
    private static byte[] ReadBytes(JsonElement element)
        => element.TryGetProperty("text", out var text) ? StrictUtf8.GetBytes(text.GetString()!) : Convert.FromBase64String(element.GetProperty("bytes").GetString()!);
    private static string ReadText(JsonElement element) => StrictUtf8.GetString(ReadBytes(element));
}

internal sealed class RipgrepContentSearchEngine(string executable, IContentSearchProcessRunner? runner = null) : IContentSearchEngine
{
    private readonly IContentSearchProcessRunner _runner = runner ?? new ContentSearchProcessRunner();
    internal static ProcessStartInfo CreateStartInfo(string executable, ContentSearchRequest request)
    {
        UnifiedFilterFindService.ValidateContentCriteria(request.Criteria);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = request.RootPath, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false) };
        string encoding = request.Encoding switch
        {
            ContentSearchEncodingMode.Auto => "auto", ContentSearchEncodingMode.Utf8 => "utf-8",
            ContentSearchEncodingMode.ShiftJis => "shift_jis", ContentSearchEncodingMode.Utf16Le => "utf-16le",
            ContentSearchEncodingMode.Utf16Be => "utf-16be", _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        foreach (string argument in new[] { "--no-config", "--json", "--engine", "default", "--encoding", encoding,
            "--hidden", "--no-ignore", "--no-follow", "--line-buffered", "--no-mmap",
            request.Criteria.ContentCaseSensitive ? "--case-sensitive" : "--ignore-case" }) start.ArgumentList.Add(argument);
        if (!request.Criteria.ContentUseRegex) start.ArgumentList.Add("--fixed-strings");
        start.ArgumentList.Add("--regexp");
        start.ArgumentList.Add(request.Criteria.ContentUseRegex
            ? MidFdSearchRegexContract.ToRipgrepPattern(request.Criteria.ContentPattern) : request.Criteria.ContentPattern);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(request.RootPath);
        return start;
    }

    public async Task<UnifiedFilterFindSearchResult> SearchAsync(ContentSearchRequest request, CancellationToken cancellationToken = default,
        Action<UnifiedSearchProgress>? progress = null, Action<ContentSearchBatch>? batch = null)
    {
        var start = CreateStartInfo(executable, request);
        request.BackendSelected?.Invoke(new(ContentSearchBackendKind.Ripgrep, "ripgrep", ContentSearchEngineResolver.ReadVersion(executable)));
        var context = new ContentSearchContext(request, cancellationToken, progress, batch);
        var eligibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        MidFdSearchRegexContract? matcher = request.Criteria.ContentUseRegex
            ? MidFdSearchRegexContract.Create(request.Criteria.ContentPattern, request.Criteria.ContentCaseSensitive) : null;
        bool summary = false;
        ContentSearchProcessResult process;
        try
        {
            process = await _runner.RunAsync(start, line =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RipgrepSearchEvent item = RipgrepSearchJsonParser.Parse(line, request.RootPath,
                    path => eligibility.TryGetValue(path, out bool accepted) && accepted);
                if (item.Kind == "summary") { summary = true; context.SetScannedCount(item.ScannedFiles ?? 0); return; }
                if (item.Path == null) return;
                if (item.Kind == "begin")
                {
                    context.Scanned(item.Path);
                    try { eligibility[item.Path] = context.IsEligible(item.Path); }
                    catch (Exception ex) when (ContentSearchContext.IsReadFailure(ex)) { eligibility[item.Path] = false; context.Diagnostic($"{item.Path}: {ex.Message}"); }
                }
                else if (item.Kind == "match")
                {
                    if (!eligibility.TryGetValue(item.Path, out bool accepted)) throw new FormatException("rg match without begin");
                    if (accepted)
                    {
                        if (item.TextDiagnostic != null)
                        {
                            context.Diagnostic($"{item.Path}: {item.TextDiagnostic}");
                            eligibility[item.Path] = false;
                            return;
                        }
                        if (matcher == null) context.Add(item.Matches);
                        else
                        {
                            // Native rg discovers candidate logical lines; shared authority enumerates
                            // scalar-safe all-matches, including zero-width matches, on the Product line.
                            string text = item.LineText!;
                            context.Add(matcher.FindMatches(text, cancellationToken).Select(m => new UnifiedSearchMatch(
                                item.Path, item.LineNumber, m.Index + 1, text, text.Substring(m.Index, m.Length))));
                        }
                    }
                }
                else if (item.Kind == "end")
                {
                    if (eligibility.TryGetValue(item.Path, out bool accepted) && accepted && item.BinaryOffset != null)
                        context.Diagnostic($"{item.Path}: binary (NUL at byte {item.BinaryOffset})");
                    eligibility.Remove(item.Path);
                    context.Flush();
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (context.HitCount == 0 && ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return await new InternalContentSearchEngine().SearchAsync(request, cancellationToken, progress, batch).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        string[] diagnostics = process.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool accessOnly = diagnostics.Length > 0 && diagnostics.All(diagnostic =>
            diagnostic.Contains("os error", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("access error", StringComparison.OrdinalIgnoreCase));
        if (process.ExitCode is not (0 or 1) && (!summary || !accessOnly))
            throw new IOException($"ripgrep検索に失敗しました (exit {process.ExitCode}): {process.StandardError}");
        if (!summary || eligibility.Count != 0) throw new FormatException("ripgrep JSON streamが完結していません。");
        foreach (string diagnostic in diagnostics) context.Diagnostic(diagnostic.TrimEnd('\r'));
        return context.Complete();
    }
}
