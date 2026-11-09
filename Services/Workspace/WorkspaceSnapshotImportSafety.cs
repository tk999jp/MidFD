using System.Text;
using System.Text.Json;
using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Services.Workspace;

internal static class WorkspaceSnapshotImportSafety
{
    internal const int MaxSingleFileBytes = 16 * 1024 * 1024;
    internal const int MaxSnapshotJsonBytes = 16 * 1024 * 1024;
    internal const int MaxSnapshotPayloadBytes = 16 * 1024 * 1024;
    internal const int MaxJsonStringBytes = 128 * 1024;
    private const int MaxJsonDepth = 64;
    private const int MaxJsonTokens = 250_000;
    private const int MaxArrayItems = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaxJsonDepth
    };

    internal static bool IsStringWithinBudget(string? value) =>
        value == null || Encoding.UTF8.GetByteCount(value) <= MaxJsonStringBytes;

    internal static bool TryReadSingle(
        string sourcePath,
        out WorkspaceSnapshotExportFile? importFile,
        out string errorMessage)
    {
        importFile = null;
        if (!TryReadJson(sourcePath, MaxSingleFileBytes, out byte[]? json, out errorMessage))
        {
            return false;
        }

        try
        {
            if (!HasRootProperty(json!, "kind") || !HasRootProperty(json!, "payload"))
            {
                errorMessage = "無効なスナップショットファイルです。";
                return false;
            }

            importFile = JsonSerializer.Deserialize<WorkspaceSnapshotExportFile>(json!, JsonOptions);
            if (importFile?.Kind != "MidFD.WorkspaceSnapshot" ||
                importFile.SchemaVersion != 1 ||
                !IsSafeWorkspaceState(importFile.Payload))
            {
                importFile = null;
                errorMessage = "スナップショットの形式または内容が無効です。";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            importFile = null;
            errorMessage = $"スナップショットの読込に失敗しました: {ex.Message}";
            return false;
        }
    }

    internal static bool TryDeserializeWorkspacePayload(
        string payloadJson,
        out WorkspaceState? state,
        out string errorMessage)
    {
        state = null;
        if (!TryValidateWorkspacePayloadJson(payloadJson, out byte[] json, out errorMessage))
        {
            return false;
        }

        try
        {
            state = JsonSerializer.Deserialize<WorkspaceState>(json, JsonOptions);
            if (!IsSafeWorkspaceState(state))
            {
                state = null;
                errorMessage = "スナップショットの形式または内容が無効です。";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            state = null;
            errorMessage = $"スナップショットの読込に失敗しました: {ex.Message}";
            return false;
        }
    }

    internal static bool TryValidateWorkspacePayloadJson(string payloadJson, out string errorMessage) =>
        TryValidateWorkspacePayloadJson(payloadJson, out _, out errorMessage);

    private static bool TryValidateWorkspacePayloadJson(string payloadJson, out byte[] json, out string errorMessage)
    {
        json = Array.Empty<byte>();
        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxSnapshotPayloadBytes)
        {
            errorMessage = "個別スナップショットのpayloadが上限を超えています。";
            return false;
        }

        json = Encoding.UTF8.GetBytes(payloadJson);
        return ValidateJsonStructure(json, out errorMessage);
    }

    internal static IEnumerable<(string Name, WorkspaceState State)> ReadBackupSetSnapshots(string sourcePath)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach ((string name, WorkspaceState state) in ReadBackupSetSnapshots(stream))
        {
            yield return (name, state);
        }
    }

    internal static IEnumerable<(string Name, WorkspaceState State)> ReadBackupSetSnapshots(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var reader = new StreamingJsonValueReader(source);
        reader.ConsumeUtf8Bom();
        reader.SkipWhitespace();
        reader.Expect((byte)'{');

        string? kind = null;
        int schemaVersion = 1;
        bool kindSeen = false;
        bool snapshotsSeen = false;
        bool firstProperty = true;
        long snapshotCount = 0;

        while (true)
        {
            reader.SkipWhitespace();
            if (reader.TryConsume((byte)'}'))
            {
                break;
            }

            if (!firstProperty)
            {
                reader.Expect((byte)',');
                reader.SkipWhitespace();
            }
            firstProperty = false;

            string propertyName = ReadJsonString(reader.ReadRawValue(MaxJsonStringBytes * 6 + 2));
            reader.SkipWhitespace();
            reader.Expect((byte)':');
            reader.SkipWhitespace();

            if (string.Equals(propertyName, "snapshots", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshotsSeen)
                {
                    throw new InvalidDataException("バックアップセットにsnapshots項目が重複しています。");
                }
                snapshotsSeen = true;
                reader.Expect((byte)'[');
                bool firstSnapshot = true;
                while (true)
                {
                    reader.SkipWhitespace();
                    if (reader.TryConsume((byte)']'))
                    {
                        break;
                    }
                    if (!firstSnapshot)
                    {
                        reader.Expect((byte)',');
                        reader.SkipWhitespace();
                    }
                    firstSnapshot = false;
                    checked { snapshotCount++; }

                    byte[] snapshotJson = reader.ReadRawValue(MaxSnapshotJsonBytes);
                    if (!TryReadSnapshot(snapshotJson, out string name, out WorkspaceState state, out string itemError))
                    {
                        throw new InvalidDataException(itemError);
                    }
                    yield return (name, state);
                }
            }
            else
            {
                byte[] propertyValue = reader.ReadRawValue(MaxSnapshotJsonBytes);
                if (!ValidateJsonStructure(propertyValue, out string structureError))
                {
                    throw new InvalidDataException(structureError);
                }

                using JsonDocument value = JsonDocument.Parse(propertyValue, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
                if (string.Equals(propertyName, "kind", StringComparison.OrdinalIgnoreCase))
                {
                    kindSeen = true;
                    kind = value.RootElement.ValueKind == JsonValueKind.String ? value.RootElement.GetString() : null;
                }
                else if (string.Equals(propertyName, "schemaVersion", StringComparison.OrdinalIgnoreCase))
                {
                    schemaVersion = value.RootElement.ValueKind == JsonValueKind.Number && value.RootElement.TryGetInt32(out int parsedVersion)
                        ? parsedVersion
                        : 0;
                }
                else if (string.Equals(propertyName, "exportedAtUtc", StringComparison.OrdinalIgnoreCase))
                {
                    _ = JsonSerializer.Deserialize<DateTime>(propertyValue, JsonOptions);
                }
            }
        }

        reader.SkipWhitespace();
        if (!reader.EndOfStream || !kindSeen || kind != "MidFD.WorkspaceSnapshotBackupSet" ||
            schemaVersion != 1 || !snapshotsSeen || snapshotCount == 0)
        {
            throw new InvalidDataException("バックアップセットの形式または内容が無効です。");
        }
    }

    private static bool TryReadSnapshot(byte[] snapshotJson, out string name, out WorkspaceState state, out string errorMessage)
    {
        name = string.Empty;
        state = null!;
        if (!ValidateJsonStructure(snapshotJson, out errorMessage))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(snapshotJson, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            JsonElement payload = default;
            bool payloadSeen = false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "payload", StringComparison.OrdinalIgnoreCase))
                {
                    payload = property.Value;
                    payloadSeen = true;
                }
            }
            if (!payloadSeen || payload.ValueKind != JsonValueKind.Object ||
                Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaxSnapshotPayloadBytes)
            {
                errorMessage = "個別スナップショットのpayloadが上限を超えているか、形式が無効です。";
                return false;
            }

            WorkspaceSnapshotExportFile? item = JsonSerializer.Deserialize<WorkspaceSnapshotExportFile>(snapshotJson, JsonOptions);
            if (item?.Kind != "MidFD.WorkspaceSnapshot" || item.SchemaVersion != 1 || !IsSafeWorkspaceState(item.Payload))
            {
                errorMessage = "バックアップセットの形式または内容が無効です。";
                return false;
            }

            name = string.IsNullOrWhiteSpace(item.Metadata?.Name) ? "Imported Snapshot" : item.Metadata.Name;
            state = item.Payload!;
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            errorMessage = $"バックアップセットの読込に失敗しました: {ex.Message}";
            return false;
        }
    }

    private static string ReadJsonString(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        if (document.RootElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("JSON property名が文字列ではありません。");
        }
        string value = document.RootElement.GetString() ?? string.Empty;
        if (!IsStringWithinBudget(value))
        {
            throw new InvalidDataException("JSON内の文字列が上限を超えています。");
        }
        return value;
    }

    private static bool TryReadJson(string sourcePath, int maxBytes, out byte[]? json, out string errorMessage)
    {
        json = null;
        errorMessage = string.Empty;
        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maxBytes)
            {
                errorMessage = $"インポートファイルが上限サイズ（{maxBytes / (1024 * 1024)} MiB）を超えています。";
                return false;
            }

            using var buffer = new MemoryStream((int)stream.Length);
            byte[] chunk = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    errorMessage = $"インポートファイルが上限サイズ（{maxBytes / (1024 * 1024)} MiB）を超えています。";
                    return false;
                }
                buffer.Write(chunk, 0, read);
            }

            byte[] bytes = buffer.ToArray();
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                bytes = bytes[3..];
            }

            if (!ValidateJsonStructure(bytes, out errorMessage))
            {
                return false;
            }

            json = bytes;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errorMessage = $"インポートファイルを読み込めません: {ex.Message}";
            return false;
        }
    }

    private static bool ValidateJsonStructure(byte[] json, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxJsonDepth });
            int tokenCount = 0;
            var arrays = new Stack<(int Depth, int Count)>();
            while (reader.Read())
            {
                if (++tokenCount > MaxJsonTokens)
                {
                    errorMessage = "JSONの項目数が上限を超えています。";
                    return false;
                }

                if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                {
                    bool withinStringBudget = reader.ValueIsEscaped
                        ? IsStringWithinBudget(reader.GetString())
                        : reader.ValueSpan.Length <= MaxJsonStringBytes;
                    if (!withinStringBudget)
                    {
                        errorMessage = "JSON内の文字列が上限を超えています。";
                        return false;
                    }
                }

                if (arrays.Count > 0 && reader.TokenType is not JsonTokenType.EndArray and not JsonTokenType.EndObject)
                {
                    (int depth, int count) = arrays.Pop();
                    if (reader.CurrentDepth == depth + 1)
                    {
                        count++;
                        if (count > MaxArrayItems)
                        {
                            errorMessage = "JSON配列の項目数が上限を超えています。";
                            return false;
                        }
                    }
                    arrays.Push((depth, count));
                }

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    arrays.Push((reader.CurrentDepth, 0));
                }
                else if (reader.TokenType == JsonTokenType.EndArray && arrays.Count > 0 && arrays.Peek().Depth == reader.CurrentDepth)
                {
                    arrays.Pop();
                }
            }
        }
        catch (JsonException ex)
        {
            errorMessage = $"JSONの形式が無効です: {ex.Message}";
            return false;
        }

        return true;
    }

    private static bool HasRootProperty(byte[] json, string propertyName)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxJsonDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read())
        {
            if (reader.CurrentDepth == 1 &&
                reader.TokenType == JsonTokenType.PropertyName &&
                string.Equals(reader.GetString(), propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsSafeWorkspaceState(WorkspaceState? state)
    {
        if (state?.RestoreSnapshot?.Categories is not { Count: > 0 and <= 4096 } categories)
        {
            return false;
        }

        int totalTabs = 0;
        int totalMarkedPaths = 0;
        foreach (BrowserTabRestoreCategoryState? category in categories)
        {
            if (category?.OpenTabs is not { Count: <= BrowserTabSettings.SafetyMaxTabsPerCategory } tabs)
            {
                return false;
            }

            totalTabs += tabs.Count;
            if (totalTabs > 20_000)
            {
                return false;
            }

            foreach (BrowserTabSessionState? tab in tabs)
            {
                if (tab == null || tab.MarkedPaths == null || tab.BackHistory == null ||
                    tab.ForwardHistory == null || tab.LastVisitedPathByDrive == null ||
                    tab.MarkedPaths.Count > MaxArrayItems || tab.BackHistory.Count > MaxArrayItems ||
                    tab.ForwardHistory.Count > MaxArrayItems || tab.LastVisitedPathByDrive.Count > MaxArrayItems)
                {
                    return false;
                }

                totalMarkedPaths += tab.MarkedPaths.Count;
                if (totalMarkedPaths > MaxArrayItems)
                {
                    return false;
                }
            }
        }

        return categories.Any(static category => category.OpenTabs.Count > 0);
    }

    private sealed class StreamingJsonValueReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _offset;
        private int _length;

        internal StreamingJsonValueReader(Stream stream) => _stream = stream;

        internal bool EndOfStream => PeekByte() < 0;

        internal void ConsumeUtf8Bom()
        {
            if (PeekByte(0) == 0xEF && PeekByte(1) == 0xBB && PeekByte(2) == 0xBF)
            {
                _offset += 3;
            }
        }

        internal void SkipWhitespace()
        {
            while (PeekByte() is 0x20 or 0x09 or 0x0A or 0x0D)
            {
                _offset++;
            }
        }

        internal bool TryConsume(byte expected)
        {
            if (PeekByte() != expected)
            {
                return false;
            }
            _offset++;
            return true;
        }

        internal void Expect(byte expected)
        {
            if (!TryConsume(expected))
            {
                throw new InvalidDataException($"バックアップセットのJSON構造が無効です（'{(char)expected}'が必要です）。");
            }
        }

        internal byte[] ReadRawValue(int maxBytes)
        {
            SkipWhitespace();
            int first = PeekByte();
            if (first < 0)
            {
                throw new InvalidDataException("バックアップセットのJSONが途中で終了しています。");
            }

            using var value = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
            if (first is '{' or '[')
            {
                int depth = 0;
                bool inString = false;
                bool escaped = false;
                do
                {
                    byte current = ReadAndAppend(value, maxBytes);
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (current == (byte)'\\') escaped = true;
                        else if (current == (byte)'"') inString = false;
                    }
                    else if (current == (byte)'"')
                    {
                        inString = true;
                    }
                    else if (current is (byte)'{' or (byte)'[')
                    {
                        depth++;
                    }
                    else if (current is (byte)'}' or (byte)']')
                    {
                        depth--;
                    }
                    if (depth <= 0)
                    {
                        break;
                    }
                } while (PeekByte() >= 0);

                if (depth != 0 || inString)
                {
                    throw new InvalidDataException("バックアップセットのJSON値が途中で終了しています。");
                }
            }
            else if (first == '"')
            {
                ReadAndAppend(value, maxBytes);
                bool escaped = false;
                while (true)
                {
                    int next = PeekByte();
                    if (next < 0)
                    {
                        throw new InvalidDataException("バックアップセットのJSON文字列が途中で終了しています。");
                    }
                    byte current = ReadAndAppend(value, maxBytes);
                    if (escaped) escaped = false;
                    else if (current == (byte)'\\') escaped = true;
                    else if (current == (byte)'"') break;
                }
            }
            else
            {
                while (true)
                {
                    int current = PeekByte();
                    if (current < 0 || current is 0x20 or 0x09 or 0x0A or 0x0D or (byte)',' or (byte)']' or (byte)'}')
                    {
                        break;
                    }
                    ReadAndAppend(value, maxBytes);
                }
            }

            return value.ToArray();
        }

        private byte ReadAndAppend(MemoryStream value, int maxBytes)
        {
            int current = ReadByte();
            if (current < 0)
            {
                throw new InvalidDataException("バックアップセットのJSON値が途中で終了しています。");
            }
            if (value.Length >= maxBytes)
            {
                throw new InvalidDataException($"個別JSON値が上限サイズ（{maxBytes / (1024 * 1024)} MiB）を超えています。");
            }
            value.WriteByte((byte)current);
            return (byte)current;
        }

        private int ReadByte()
        {
            if (_offset >= _length && !FillBuffer()) return -1;
            return _buffer[_offset++];
        }

        private int PeekByte(int lookahead = 0)
        {
            if (_offset + lookahead >= _length && !FillBuffer(lookahead + 1)) return -1;
            return _buffer[_offset + lookahead];
        }

        private bool FillBuffer(int required = 1)
        {
            if (_offset > 0)
            {
                int remaining = _length - _offset;
                if (remaining > 0) Buffer.BlockCopy(_buffer, _offset, _buffer, 0, remaining);
                _offset = 0;
                _length = remaining;
            }
            while (_length < required)
            {
                int read = _stream.Read(_buffer, _length, _buffer.Length - _length);
                if (read == 0) return false;
                _length += read;
            }
            return true;
        }
    }
}
