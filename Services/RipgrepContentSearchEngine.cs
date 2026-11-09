using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MidFD.Models;

namespace MidFD.Services;

internal sealed record ContentSearchProcessResult(int ExitCode, string StandardError);
internal abstract record ContentSearchProcessOutput;
internal sealed record ContentSearchJsonEvent(string Json) : ContentSearchProcessOutput;
internal sealed record ContentSearchOversizedJsonEvent(string EventKind = "match") : ContentSearchProcessOutput;
internal sealed class ContentSearchTruncatedJsonEventException(string message) : IOException(message);

internal interface IContentSearchProcessRunner
{
    Task<ContentSearchProcessResult> RunAsync(ProcessStartInfo startInfo, Action<ContentSearchProcessOutput> stdout, CancellationToken token);
}

internal sealed class ContentSearchProcessRunner : IContentSearchProcessRunner
{
    public async Task<ContentSearchProcessResult> RunAsync(ProcessStartInfo startInfo, Action<ContentSearchProcessOutput> stdout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var registration = token.Register(() => Kill(process));
        Task<string> stderr = ReadErrorAsync(process);
        try
        {
            await foreach (ContentSearchProcessOutput output in ReadOutputAsync(process.StandardOutput, token))
            {
                token.ThrowIfCancellationRequested();
                stdout(output);
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
    internal static async IAsyncEnumerable<ContentSearchProcessOutput> ReadOutputAsync(StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        char[] buffer = new char[4096];
        var line = new StringBuilder();
        bool discardingOversizedEvent = false;
        ContentSearchJsonLineValidator? oversizedEventValidator = null;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            token.ThrowIfCancellationRequested();
            int i = 0;
            while (i < count)
            {
                if (discardingOversizedEvent)
                {
                    int newline = Array.IndexOf(buffer, '\n', i, count - i);
                    int segmentLength = newline >= 0 ? newline - i : count - i;
                    oversizedEventValidator!.Append(buffer.AsSpan(i, segmentLength));
                    if (newline >= 0)
                    {
                        yield return new ContentSearchOversizedJsonEvent(oversizedEventValidator.Complete());
                        line.Clear();
                        discardingOversizedEvent = false;
                        oversizedEventValidator = null;
                        i = newline + 1;
                    }
                    else i = count;
                    continue;
                }

                if (buffer[i] == '\n')
                {
                    yield return new ContentSearchJsonEvent(line.ToString());
                    line.Clear();
                    i++;
                    continue;
                }

                if (line.Length >= RipgrepSearchJsonParser.MaxJsonEventChars)
                {
                    oversizedEventValidator = new ContentSearchJsonLineValidator();
                    string prefix = line.ToString();
                    oversizedEventValidator.Append(prefix.AsSpan());
                    oversizedEventValidator.Append(buffer[i]);
                    line.Clear();
                    discardingOversizedEvent = true;
                    i++;
                    continue;
                }
                line.Append(buffer[i]);
                i++;
            }
        }
        if (discardingOversizedEvent)
            throw new ContentSearchTruncatedJsonEventException("16 Mi文字超のripgrep JSONイベントが改行delimiter前にEOFとなり、次のrecord境界を確認できません。");
        if (line.Length > 0) yield return new ContentSearchJsonEvent(line.ToString());
    }

    private static async Task<string> ReadErrorAsync(Process process)
    {
        var error = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (error.Length + count > 1024 * 1024)
            {
                Kill(process);
                throw new ContentSearchResourceLimitException("ripgrep stderrが読込資源上限を超えたため中断しました。");
            }
            error.Append(buffer, 0, count);
        }
        return error.ToString();
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

internal sealed class ContentSearchJsonLineValidator
{
    private enum ContainerKind { Object, Array }
    private enum ContainerState { ObjectKeyOrEnd, ObjectKey, ObjectColon, ObjectValue, ObjectCommaOrEnd, ArrayValueOrEnd, ArrayValue, ArrayCommaOrEnd }
    private enum PrimitiveKind { None, Number, Literal }

    private struct ContainerFrame(ContainerKind kind, ContainerState state)
    {
        public ContainerKind Kind = kind;
        public ContainerState State = state;
        public string? PropertyName;
    }

    private readonly List<ContainerFrame> _containers = new(32);
    private readonly StringBuilder _capturedString = new(16);
    private bool _rootStarted;
    private bool _rootCompleted;
    private bool _rootIsObject;
    private bool _rootTypeSeen;
    private bool _rootTypeIsString;
    private string? _rootType;
    private bool _rootDataSeen;
    private bool _rootDataIsObject;
    private bool _insideString;
    private bool _stringIsProperty;
    private bool _captureRootProperty;
    private bool _captureRootType;
    private bool _captureOverflow;
    private bool _escaped;
    private int _unicodeDigitsRemaining;
    private int _unicodeValue;
    private PrimitiveKind _primitive;
    private int _numberState;
    private string? _literal;
    private int _literalIndex;

    public void Append(char value)
    {
        while (true)
        {
            if (_insideString)
            {
                AppendStringCharacter(value);
                return;
            }
            if (_primitive == PrimitiveKind.Literal)
            {
                if (_literalIndex >= _literal!.Length || value != _literal[_literalIndex])
                    throw new FormatException("16 Mi文字超ripgrep JSONイベントのliteralが不正です。");
                _literalIndex++;
                if (_literalIndex == _literal.Length)
                {
                    _primitive = PrimitiveKind.None;
                    _literal = null;
                    CompleteValue();
                }
                return;
            }
            if (_primitive == PrimitiveKind.Number)
            {
                if (TryConsumeNumberCharacter(value)) return;
                if (!IsCompleteNumber() || !IsJsonDelimiter(value))
                    throw new FormatException("16 Mi文字超ripgrep JSONイベントのnumberが不正です。");
                _primitive = PrimitiveKind.None;
                CompleteValue();
                continue;
            }
            if (IsJsonWhiteSpace(value)) return;
            if (_rootCompleted) throw new FormatException("16 Mi文字超ripgrep JSONイベントにroot後の内容があります。");
            if (_containers.Count == 0)
            {
                if (_rootStarted) throw new FormatException("16 Mi文字超ripgrep JSONイベントのrootが複数あります。");
                BeginValue(value);
                return;
            }

            ContainerFrame frame = _containers[^1];
            switch (frame.State)
            {
                case ContainerState.ObjectKeyOrEnd:
                    if (value == '}') CloseContainer(ContainerKind.Object);
                    else if (value == '"') BeginString(isProperty: true);
                    else throw new FormatException("16 Mi文字超ripgrep JSONイベントのobject keyが不正です。");
                    return;
                case ContainerState.ObjectKey:
                    if (value == '"') BeginString(isProperty: true);
                    else throw new FormatException("16 Mi文字超ripgrep JSONイベントのobject keyが不正です。");
                    return;
                case ContainerState.ObjectColon:
                    if (value != ':') throw new FormatException("16 Mi文字超ripgrep JSONイベントのobject colonが不正です。");
                    frame.State = ContainerState.ObjectValue;
                    _containers[^1] = frame;
                    return;
                case ContainerState.ObjectValue:
                case ContainerState.ArrayValue:
                    BeginValue(value);
                    return;
                case ContainerState.ArrayValueOrEnd:
                    if (value == ']') { CloseContainer(ContainerKind.Array); return; }
                    BeginValue(value);
                    return;
                case ContainerState.ObjectCommaOrEnd:
                    if (value == ',') frame.State = ContainerState.ObjectKey;
                    else if (value == '}') { CloseContainer(ContainerKind.Object); return; }
                    else throw new FormatException("16 Mi文字超ripgrep JSONイベントのobject separatorが不正です。");
                    _containers[^1] = frame;
                    return;
                case ContainerState.ArrayCommaOrEnd:
                    if (value == ',') frame.State = ContainerState.ArrayValue;
                    else if (value == ']') { CloseContainer(ContainerKind.Array); return; }
                    else throw new FormatException("16 Mi文字超ripgrep JSONイベントのarray separatorが不正です。");
                    _containers[^1] = frame;
                    return;
                default:
                    throw new FormatException("16 Mi文字超ripgrep JSONイベントのparser stateが不正です。");
            }
        }
    }

    public void Append(ReadOnlySpan<char> values)
    {
        int offset = 0;
        while (offset < values.Length)
        {
            if (_insideString && !_escaped && _unicodeDigitsRemaining == 0)
            {
                ReadOnlySpan<char> remaining = values[offset..];
                int escapeOrQuote = remaining.IndexOfAny('"', '\\');
                int control = remaining.IndexOfAnyInRange('\0', '\x1f');
                if (control >= 0 && (escapeOrQuote < 0 || control < escapeOrQuote))
                    throw new FormatException("16 Mi文字超ripgrep JSONイベントに未escape制御文字があります。");
                if (escapeOrQuote < 0)
                {
                    AppendCapturedSegment(remaining);
                    return;
                }
                if (escapeOrQuote > 0) AppendCapturedSegment(remaining[..escapeOrQuote]);
                offset += escapeOrQuote;
                Append(values[offset]);
                offset++;
                continue;
            }
            Append(values[offset]);
            offset++;
        }
    }

    public string Complete()
    {
        if (_insideString || _escaped || _unicodeDigitsRemaining != 0)
            throw new FormatException("16 Mi文字超ripgrep JSONイベントのstringが閉じていません。");
        if (_primitive == PrimitiveKind.Number)
        {
            if (!IsCompleteNumber()) throw new FormatException("16 Mi文字超ripgrep JSONイベントのnumberが未完了です。");
            _primitive = PrimitiveKind.None;
            CompleteValue();
        }
        else if (_primitive == PrimitiveKind.Literal)
            throw new FormatException("16 Mi文字超ripgrep JSONイベントのliteralが未完了です。");

        if (_containers.Count != 0 || !_rootStarted || !_rootCompleted || !_rootIsObject ||
            !_rootTypeSeen || !_rootTypeIsString || !_rootDataSeen || !_rootDataIsObject ||
            _rootType is not ("begin" or "match" or "end" or "context" or "summary"))
            throw new FormatException("16 Mi文字超ripgrep JSONイベントのroot schemaが不正です。");
        return _rootType;
    }

    private void BeginValue(char value)
    {
        if (_containers.Count == 0)
        {
            if (_rootStarted) throw new FormatException("16 Mi文字超ripgrep JSONイベントのrootが複数あります。");
            _rootStarted = true;
            _rootIsObject = value == '{';
        }
        else if (_containers.Count == 1 && _containers[0].Kind == ContainerKind.Object)
        {
            string? property = _containers[0].PropertyName;
            if (property == "type")
            {
                _rootTypeSeen = true;
                _rootTypeIsString = value == '"';
                _rootType = null;
            }
            else if (property == "data")
            {
                _rootDataSeen = true;
                _rootDataIsObject = value == '{';
            }
        }

        switch (value)
        {
            case '{': PushContainer(ContainerKind.Object, ContainerState.ObjectKeyOrEnd); return;
            case '[': PushContainer(ContainerKind.Array, ContainerState.ArrayValueOrEnd); return;
            case '"': BeginString(isProperty: false); return;
            case 't': BeginLiteral("true"); return;
            case 'f': BeginLiteral("false"); return;
            case 'n': BeginLiteral("null"); return;
            case '-': _primitive = PrimitiveKind.Number; _numberState = 1; return;
            case '0': _primitive = PrimitiveKind.Number; _numberState = 2; return;
            default:
                if (value is >= '1' and <= '9') { _primitive = PrimitiveKind.Number; _numberState = 3; return; }
                throw new FormatException("16 Mi文字超ripgrep JSONイベントのvalueが不正です。");
        }
    }

    private void PushContainer(ContainerKind kind, ContainerState state)
    {
        if (_containers.Count >= 32) throw new FormatException("16 Mi文字超ripgrep JSONイベントのdepth上限を超えています。");
        _containers.Add(new(kind, state));
    }

    private void CloseContainer(ContainerKind kind)
    {
        if (_containers.Count == 0 || _containers[^1].Kind != kind)
            throw new FormatException("16 Mi文字超ripgrep JSONイベントのcontainerが対応しません。");
        _containers.RemoveAt(_containers.Count - 1);
        CompleteValue();
    }

    private void CompleteValue()
    {
        if (_containers.Count == 0)
        {
            if (!_rootStarted || _rootCompleted) throw new FormatException("16 Mi文字超ripgrep JSONイベントのvalue境界が不正です。");
            _rootCompleted = true;
            return;
        }

        ContainerFrame frame = _containers[^1];
        if (frame.Kind == ContainerKind.Object && frame.State == ContainerState.ObjectValue)
        {
            frame.State = ContainerState.ObjectCommaOrEnd;
            if (_containers.Count == 1) frame.PropertyName = null;
        }
        else if (frame.Kind == ContainerKind.Array && frame.State is ContainerState.ArrayValueOrEnd or ContainerState.ArrayValue)
            frame.State = ContainerState.ArrayCommaOrEnd;
        else throw new FormatException("16 Mi文字超ripgrep JSONイベントのvalue位置が不正です。");
        _containers[^1] = frame;
    }

    private void BeginString(bool isProperty)
    {
        _insideString = true;
        _stringIsProperty = isProperty;
        _captureRootProperty = isProperty && _containers.Count == 1 && _containers[0].Kind == ContainerKind.Object;
        _captureRootType = !isProperty && _containers.Count == 1 && _containers[0].Kind == ContainerKind.Object &&
            _containers[0].PropertyName == "type";
        _captureOverflow = false;
        _escaped = false;
        _unicodeDigitsRemaining = 0;
        _unicodeValue = 0;
        _capturedString.Clear();
    }

    private void AppendStringCharacter(char value)
    {
        if (_unicodeDigitsRemaining > 0)
        {
            int digit = HexValue(value);
            if (digit < 0) throw new FormatException("16 Mi文字超ripgrep JSONイベントのunicode escapeが不正です。");
            _unicodeValue = (_unicodeValue << 4) | digit;
            if (--_unicodeDigitsRemaining == 0) AppendCaptured((char)_unicodeValue);
            return;
        }
        if (_escaped)
        {
            _escaped = false;
            switch (value)
            {
                case '"': AppendCaptured('"'); return;
                case '\\': AppendCaptured('\\'); return;
                case '/': AppendCaptured('/'); return;
                case 'b': AppendCaptured('\b'); return;
                case 'f': AppendCaptured('\f'); return;
                case 'n': AppendCaptured('\n'); return;
                case 'r': AppendCaptured('\r'); return;
                case 't': AppendCaptured('\t'); return;
                case 'u': _unicodeDigitsRemaining = 4; _unicodeValue = 0; return;
                default: throw new FormatException("16 Mi文字超ripgrep JSONイベントのescapeが不正です。");
            }
        }
        if (value == '\\') { _escaped = true; return; }
        if (value == '"')
        {
            _insideString = false;
            string? captured = _captureOverflow ? null : _capturedString.ToString();
            if (_stringIsProperty)
            {
                if (_containers.Count == 0 || _containers[^1].Kind != ContainerKind.Object)
                    throw new FormatException("16 Mi文字超ripgrep JSONイベントのproperty位置が不正です。");
                ContainerFrame frame = _containers[^1];
                frame.State = ContainerState.ObjectColon;
                if (_captureRootProperty) frame.PropertyName = captured;
                _containers[^1] = frame;
            }
            else
            {
                if (_captureRootType) _rootType = captured;
                CompleteValue();
            }
            return;
        }
        if (value < ' ') throw new FormatException("16 Mi文字超ripgrep JSONイベントに未escape制御文字があります。");
        AppendCaptured(value);
    }

    private void AppendCaptured(char value)
    {
        if (!_captureRootProperty && !_captureRootType) return;
        if (_capturedString.Length >= 32) { _captureOverflow = true; return; }
        _capturedString.Append(value);
    }

    private void AppendCapturedSegment(ReadOnlySpan<char> value)
    {
        if (!_captureRootProperty && !_captureRootType) return;
        int remaining = 32 - _capturedString.Length;
        if (value.Length > remaining)
        {
            if (remaining > 0) _capturedString.Append(value[..remaining]);
            _captureOverflow = true;
            return;
        }
        _capturedString.Append(value);
    }

    private void BeginLiteral(string literal)
    {
        _primitive = PrimitiveKind.Literal;
        _literal = literal;
        _literalIndex = 1;
    }

    private bool TryConsumeNumberCharacter(char value)
    {
        switch (_numberState)
        {
            case 1 when value is >= '0' and <= '9': _numberState = value == '0' ? 2 : 3; return true;
            case 2 when value == '.': _numberState = 4; return true;
            case 2 when value is 'e' or 'E': _numberState = 6; return true;
            case 3 when value is >= '0' and <= '9': return true;
            case 3 when value == '.': _numberState = 4; return true;
            case 3 when value is 'e' or 'E': _numberState = 6; return true;
            case 4 when value is >= '0' and <= '9': _numberState = 5; return true;
            case 5 when value is >= '0' and <= '9': return true;
            case 5 when value is 'e' or 'E': _numberState = 6; return true;
            case 6 when value is '+' or '-': _numberState = 7; return true;
            case 6 when value is >= '0' and <= '9': _numberState = 8; return true;
            case 7 when value is >= '0' and <= '9': _numberState = 8; return true;
            case 8 when value is >= '0' and <= '9': return true;
            default: return false;
        }
    }

    private bool IsCompleteNumber() => _numberState is 2 or 3 or 5 or 8;
    private static bool IsJsonWhiteSpace(char value) => value is ' ' or '\t' or '\r';
    private static bool IsJsonDelimiter(char value) => IsJsonWhiteSpace(value) || value is ',' or ']' or '}';
    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };
}

internal sealed record RipgrepSearchEvent(string Kind, string? Path, IReadOnlyList<UnifiedSearchMatch> Matches,
    long? ScannedFiles = null, long? BinaryOffset = null, string? LineText = null, int LineNumber = 0,
    string? TextDiagnostic = null);

internal static class RipgrepSearchJsonParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaxJsonEventChars = 16 * 1024 * 1024;
    internal static RipgrepSearchEvent Parse(string json, string root, Func<string, bool>? shouldDecodeMatch = null)
    {
        try
        {
            if (json.Length > MaxJsonEventChars)
                throw new ContentSearchResourceLimitException("ripgrep JSON単一行が16 MiBの資源上限を超えています。");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
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
            int number = data.GetProperty("line_number").GetInt32();
            if (number < 1) throw new FormatException("rg line number is outside the line contract");
            JsonElement submatches = data.GetProperty("submatches");
            if (submatches.GetArrayLength() > ContentSearchContext.MaxHits)
                throw new ContentSearchRecoverableRecordExclusionException(
                    ContentSearchExcludedRecordReason.ExcessiveSubmatches,
                    "単一ripgrep JSONイベントのsubmatch件数が100,000件を超えています。");
            byte[] bytes = ReadBytes(data.GetProperty("lines"));
            int logicalLength = bytes.Length;
            if (logicalLength > 0 && bytes[logicalLength - 1] == '\n')
            {
                logicalLength--;
                if (logicalLength > 0 && bytes[logicalLength - 1] == '\r') logicalLength--;
            }
            if (Array.IndexOf(bytes, (byte)'\n', 0, logicalLength) >= 0)
                throw new FormatException("rg multiline event is outside the line contract");
            int decodedLength;
            try { decodedLength = StrictUtf8.GetCharCount(bytes, 0, logicalLength); }
            catch (DecoderFallbackException ex) { return new(kind, path, [], TextDiagnostic: ex.Message); }
            if (decodedLength > ContentSearchContext.MaxLineChars)
                throw new ContentSearchRecoverableRecordExclusionException(
                    ContentSearchExcludedRecordReason.LogicalLineTooLong,
                    "ripgrep論理行が4 Mi文字を超えています。");
            string text = StrictUtf8.GetString(bytes, 0, logicalLength);
            var hits = new List<UnifiedSearchMatch>();
            foreach (var submatch in submatches.EnumerateArray())
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
        catch (ContentSearchRecoverableRecordExclusionException) { throw; }
        catch (ContentSearchResourceLimitException) { throw; }
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
        try
        {
            ContentSearchProcessResult process = await _runner.RunAsync(start, output =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output is ContentSearchOversizedJsonEvent)
                {
                    var oversized = (ContentSearchOversizedJsonEvent)output;
                    if (oversized.EventKind != "match")
                        throw new FormatException($"過大ripgrep JSONイベント種別を安全に継続できません: {oversized.EventKind}");
                    context.ExcludeRecord(ContentSearchExcludedRecordReason.OversizedJsonEvent);
                    return;
                }
                if (output is not ContentSearchJsonEvent jsonEvent) throw new FormatException("不明なripgrep stdout recordです。");
                RipgrepSearchEvent item;
                try
                {
                    item = RipgrepSearchJsonParser.Parse(jsonEvent.Json, request.RootPath,
                        path => eligibility.TryGetValue(path, out bool accepted) && accepted);
                }
                catch (ContentSearchRecoverableRecordExclusionException ex)
                {
                    context.ExcludeRecord(ex.Reason);
                    return;
                }
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
        catch (Exception ex) when ((context.HitCount > 0 || context.ExcludedRecordCount > 0) && ContentSearchContext.CanPreservePartial(ex))
        {
            return context.CompletePartial($"検索を完了できませんでした: {ex.Message}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return await new InternalContentSearchEngine().SearchAsync(request, cancellationToken, progress, batch).ConfigureAwait(false);
        }
    }
}
