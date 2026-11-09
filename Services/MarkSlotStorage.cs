using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MidFD.Models;

namespace MidFD.Services;

public static class MarkSlotStorage
{
    private static readonly string MarkSlotFilePath;
    internal const int MaxImportBytes = 16 * 1024 * 1024;
    internal const int MaxStoreBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    static MarkSlotStorage()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        MarkSlotFilePath = Path.Combine(exeDir, "markslots.json");
    }

    public static MarkSlotStore Load(int slotCount) => Load(slotCount, MarkSlotFilePath);

    internal static MarkSlotStore Load(int slotCount, string filePath)
    {
        if (!File.Exists(filePath))
        {
            return MarkSlotStore.CreateDefault(slotCount);
        }

        try
        {
            var store = ReadDocument<MarkSlotStore>(filePath, MaxStoreBytes);
            if (store?.Slots == null) throw new JsonException("MarkSlot store payload is empty or invalid.");
            return Sanitize(store, slotCount);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to load markslots.json.", ex);
            var failed = MarkSlotStore.CreateDefault(slotCount);
            failed.LoadFailed = true;
            return failed;
        }
    }

    public static bool Save(MarkSlotStore store, int slotCount) => Save(store, slotCount, MarkSlotFilePath);

    internal static bool Save(MarkSlotStore store, int slotCount, string filePath)
    {
        try
        {
            if (store.LoadFailed) throw new InvalidDataException("読込に失敗したMarkSlot状態の自動上書きを拒否しました。");
            WriteDocument(filePath, Sanitize(store, slotCount), MaxStoreBytes);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to save markslots.json to '{filePath}'.", ex);
            return false;
        }
    }

    public static bool TryExportSlot(string filePath, MarkSlotEntry slot, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            MarkSlotEntry sanitizedSlot = SanitizeSlotEntry(slot, slot.SlotNumber);
            var document = new MarkSlotExportDocument
            {
                SchemaVersion = MarkSlotExportDocument.CurrentSchemaVersion,
                ExportedAtUtc = DateTime.UtcNow,
                AppName = "MidFD",
                Kind = MarkSlotExportDocument.CurrentKind,
                Slot = new MarkSlotExportEntry
                {
                    SlotNumber = sanitizedSlot.SlotNumber,
                    DisplayName = sanitizedSlot.DisplayName,
                    SavedAtUtc = sanitizedSlot.SavedAtUtc,
                    SourceScope = NormalizeOptionalText(sanitizedSlot.SourceScope),
                    SourceCategoryId = NormalizeOptionalText(sanitizedSlot.SourceCategoryId),
                    SourceCategoryName = NormalizeOptionalText(sanitizedSlot.SourceCategoryName),
                    SourceTabId = sanitizedSlot.SourceTabId,
                    SourceTabDisplayName = NormalizeOptionalText(sanitizedSlot.SourceTabDisplayName),
                    Paths = new List<string>(sanitizedSlot.Paths)
                }
            };

            WriteDocument(filePath, document, MaxImportBytes);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to export mark slot to '{filePath}'.", ex);
            errorMessage = "マークスロットのエクスポートに失敗しました。";
            return false;
        }
    }

    public static bool TryImportSlot(string filePath, out MarkSlotEntry? importedSlot, out string errorMessage, out string? warningMessage)
    {
        importedSlot = null;
        errorMessage = string.Empty;
        warningMessage = null;

        try
        {
            var document = ReadDocument<MarkSlotExportDocument>(filePath, MaxImportBytes);
            if (document == null)
            {
                errorMessage = "Mark Slot export JSON を読み取れませんでした。";
                return false;
            }

            if (document.SchemaVersion != MarkSlotExportDocument.CurrentSchemaVersion)
            {
                errorMessage = $"未対応の schemaVersion です: {document.SchemaVersion}";
                return false;
            }

            if (!string.Equals(document.Kind, MarkSlotExportDocument.CurrentKind, StringComparison.Ordinal))
            {
                errorMessage = "Mark Slot export file ではありません。";
                return false;
            }

            if (document.Slot == null)
            {
                errorMessage = "slot が含まれていません。";
                return false;
            }

            if (document.Slot.Paths == null)
            {
                errorMessage = "slot.paths が含まれていません。";
                return false;
            }

            string? sourceScope = NormalizeImportedSourceScope(document.Slot.SourceScope, out bool normalizedUnknownScope);
            if (normalizedUnknownScope)
            {
                warningMessage = "保存元 scope が未対応値だったため、不明 / Legacy として取り込みます。";
            }

            importedSlot = SanitizeSlotEntry(
                new MarkSlotEntry
                {
                    SlotNumber = document.Slot.SlotNumber,
                    DisplayName = document.Slot.DisplayName,
                    SavedAtUtc = document.Slot.SavedAtUtc,
                    Paths = document.Slot.Paths,
                    SourceScope = sourceScope,
                    SourceCategoryId = document.Slot.SourceCategoryId,
                    SourceCategoryName = document.Slot.SourceCategoryName,
                    SourceTabId = document.Slot.SourceTabId,
                    SourceTabDisplayName = document.Slot.SourceTabDisplayName
                },
                document.Slot.SlotNumber);

            return true;
        }
        catch (JsonException ex)
        {
            LogService.Error($"Failed to parse mark slot import file '{filePath}'.", ex);
            errorMessage = "JSON の形式が正しくないため、マークスロットをインポートできません。";
            return false;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to import mark slot from '{filePath}'.", ex);
            errorMessage = "マークスロットのインポートに失敗しました。";
            return false;
        }
    }

    public static bool TryExportAllSlots(string filePath, MarkSlotStore store, int slotCount, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            MarkSlotStore sanitizedStore = Sanitize(store, slotCount);
            var document = new MarkSlotBackupSetDocument
            {
                SchemaVersion = MarkSlotBackupSetDocument.CurrentSchemaVersion,
                ExportedAtUtc = DateTime.UtcNow,
                AppName = "MidFD",
                Kind = MarkSlotBackupSetDocument.CurrentKind,
                SlotCount = slotCount,
                Slots = sanitizedStore.Slots
                    .OrderBy(static slot => slot.SlotNumber)
                    .Select(static slot => new MarkSlotExportEntry
                    {
                        SlotNumber = slot.SlotNumber,
                        DisplayName = slot.DisplayName,
                        SavedAtUtc = slot.SavedAtUtc,
                        SourceScope = slot.SourceScope,
                        SourceCategoryId = slot.SourceCategoryId,
                        SourceCategoryName = slot.SourceCategoryName,
                        SourceTabId = slot.SourceTabId,
                        SourceTabDisplayName = slot.SourceTabDisplayName,
                        Paths = new List<string>(slot.Paths)
                    })
                    .ToList()
            };

            WriteDocument(filePath, document, MaxStoreBytes);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to export all mark slots to '{filePath}'.", ex);
            errorMessage = "全マークスロットのエクスポートに失敗しました。";
            return false;
        }
    }

    public static bool TryImportAllSlots(string filePath, int slotCount, out MarkSlotStore? importedStore, out string errorMessage, out string? warningMessage)
    {
        importedStore = null;
        errorMessage = string.Empty;
        warningMessage = null;
        try
        {
            var document = ReadDocument<MarkSlotBackupSetDocument>(filePath, MaxStoreBytes);
            if (document == null)
            {
                errorMessage = "Mark Slot backup JSON を読み取れませんでした。";
                return false;
            }

            if (document.SchemaVersion != MarkSlotBackupSetDocument.CurrentSchemaVersion)
            {
                errorMessage = $"未対応の schemaVersion です: {document.SchemaVersion}";
                return false;
            }

            if (!string.Equals(document.Kind, MarkSlotBackupSetDocument.CurrentKind, StringComparison.Ordinal))
            {
                errorMessage = "Mark Slot backup file ではありません。";
                return false;
            }

            if (document.SlotCount <= 0)
            {
                errorMessage = "slotCount が不正です。";
                return false;
            }

            if (document.Slots == null)
            {
                errorMessage = "slots が含まれていません。";
                return false;
            }

            if (document.SlotCount != document.Slots.Count)
            {
                errorMessage = "slotCount と slots 件数が一致しません。";
                return false;
            }

            bool hasUnknownScope = false;
            var normalizedSlots = new List<MarkSlotEntry>();
            foreach (MarkSlotExportEntry source in document.Slots)
            {
                if (source.Paths == null)
                {
                    errorMessage = $"slot {source.SlotNumber} の paths が含まれていません。";
                    return false;
                }

                string? sourceScope = NormalizeImportedSourceScope(source.SourceScope, out bool normalizedUnknownScope);
                hasUnknownScope |= normalizedUnknownScope;

                int normalizedSlotNumber = Math.Clamp(source.SlotNumber, 1, slotCount);
                normalizedSlots.Add(SanitizeSlotEntry(new MarkSlotEntry
                {
                    SlotNumber = normalizedSlotNumber,
                    DisplayName = source.DisplayName,
                    SavedAtUtc = source.SavedAtUtc,
                    Paths = source.Paths,
                    SourceScope = sourceScope,
                    SourceCategoryId = source.SourceCategoryId,
                    SourceCategoryName = source.SourceCategoryName,
                    SourceTabId = source.SourceTabId,
                    SourceTabDisplayName = source.SourceTabDisplayName
                }, normalizedSlotNumber));
            }

            var sanitized = MarkSlotStore.CreateDefault(slotCount);
            foreach (MarkSlotEntry target in sanitized.Slots)
            {
                MarkSlotEntry? source = normalizedSlots.FirstOrDefault(slot => slot.SlotNumber == target.SlotNumber);
                if (source == null)
                {
                    continue;
                }

                target.DisplayName = source.DisplayName;
                target.SavedAtUtc = source.SavedAtUtc;
                target.Paths = source.Paths;
                target.SourceScope = source.SourceScope;
                target.SourceCategoryId = source.SourceCategoryId;
                target.SourceCategoryName = source.SourceCategoryName;
                target.SourceTabId = source.SourceTabId;
                target.SourceTabDisplayName = source.SourceTabDisplayName;
            }

            importedStore = sanitized;
            if (hasUnknownScope)
            {
                warningMessage = "保存元 scope に未対応値が含まれていたため、不明 / Legacy として取り込みました。";
            }

            return true;
        }
        catch (JsonException ex)
        {
            LogService.Error($"Failed to parse all mark slots import file '{filePath}'.", ex);
            errorMessage = "JSON の形式が正しくないため、全マークスロットをインポートできません。";
            return false;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to import all mark slots from '{filePath}'.", ex);
            errorMessage = "全マークスロットのインポートに失敗しました。";
            return false;
        }
    }

    private static T? ReadDocument<T>(string path, int maxBytes)
    {
        // Deny concurrent growth/replacement during the bounded read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) throw new InvalidDataException($"JSONが容量上限 ({maxBytes} bytes) を超えています。");
        byte[] json = new byte[checked((int)stream.Length)];
        stream.ReadExactly(json);
        return DeserializeDocument<T>(json);
    }

    private static T? DeserializeDocument<T>(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
        ValidateJson(json);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static void ValidateJson(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 32 });
        int tokens = 0;
        var arrays = new Stack<(int Depth, int Count)>();
        while (reader.Read()) ValidateToken(ref reader, ref tokens, arrays);
    }

    private static void ValidateToken(ref Utf8JsonReader reader, ref int tokens, Stack<(int Depth, int Count)> arrays)
    {
        if (++tokens > 1_000_000 ||
            (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && reader.ValueSpan.Length > 128 * 1024))
            throw new InvalidDataException("JSONの項目数・文字列長が上限を超えています。");
        if (arrays.Count > 0 && reader.CurrentDepth == arrays.Peek().Depth + 1 &&
            reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.EndArray or JsonTokenType.EndObject))
        {
            var current = arrays.Pop();
            if (++current.Count > 100_000) throw new InvalidDataException("JSON配列の項目数が上限を超えています。");
            arrays.Push(current);
        }
        if (reader.TokenType == JsonTokenType.StartArray) arrays.Push((reader.CurrentDepth, 0));
        else if (reader.TokenType == JsonTokenType.EndArray) arrays.Pop();
    }

    internal static void WriteDocument<T>(string path, T document, int maxBytes)
    {
        AtomicFileWriter.Write(path, stream =>
        {
            using (var bounded = new BoundedWriteStream(stream, maxBytes))
                JsonSerializer.Serialize(bounded, document, JsonOptions);
            stream.Position = 0;
            ValidateJsonStream(stream);
        });
    }

    // Keep only an incomplete token instead of rereading the full serialized file.
    private static void ValidateJsonStream(Stream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        int buffered = 0, tokens = 0;
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 32 });
        var arrays = new Stack<(int Depth, int Count)>();
        while (true)
        {
            int read = stream.Read(buffer, buffered, buffer.Length - buffered);
            bool final = read == 0;
            buffered += read;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), final, state);
            while (reader.Read()) ValidateToken(ref reader, ref tokens, arrays);
            int consumed = checked((int)reader.BytesConsumed);
            state = reader.CurrentState;
            buffered -= consumed;
            buffer.AsSpan(consumed, buffered).CopyTo(buffer);
            if (final) break;
            if (buffered == buffer.Length)
            {
                if (buffer.Length >= 256 * 1024)
                    throw new InvalidDataException("JSON文字列長が上限を超えています。");
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
    }

    private sealed class BoundedWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maxBytes - written)
                throw new InvalidDataException("JSONの保存容量上限を超えています。");
            inner.Write(buffer);
            written += buffer.Length;
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        // AtomicFileWriter retains ownership of the underlying stream.
    }

    private static MarkSlotStore Sanitize(MarkSlotStore? source, int slotCount)
    {
        var sanitized = MarkSlotStore.CreateDefault(slotCount);
        var sourceSlots = source?.Slots ?? new List<MarkSlotEntry>();

        foreach (MarkSlotEntry targetSlot in sanitized.Slots)
        {
            MarkSlotEntry? sourceSlot = sourceSlots.FirstOrDefault(slot => slot.SlotNumber == targetSlot.SlotNumber);
            if (sourceSlot == null)
            {
                continue;
            }

            MarkSlotEntry sanitizedSlot = SanitizeSlotEntry(sourceSlot, targetSlot.SlotNumber);
            targetSlot.DisplayName = sanitizedSlot.DisplayName;
            targetSlot.SavedAtUtc = sanitizedSlot.SavedAtUtc;
            targetSlot.Paths = sanitizedSlot.Paths;
            targetSlot.SourceScope = sanitizedSlot.SourceScope;
            targetSlot.SourceCategoryId = sanitizedSlot.SourceCategoryId;
            targetSlot.SourceCategoryName = sanitizedSlot.SourceCategoryName;
            targetSlot.SourceTabId = sanitizedSlot.SourceTabId;
            targetSlot.SourceTabDisplayName = sanitizedSlot.SourceTabDisplayName;
        }

        return sanitized;
    }

    private static MarkSlotEntry SanitizeSlotEntry(MarkSlotEntry? sourceSlot, int slotNumber)
    {
        return new MarkSlotEntry
        {
            SlotNumber = slotNumber,
            DisplayName = string.IsNullOrWhiteSpace(sourceSlot?.DisplayName)
                ? $"スロット {slotNumber}"
                : sourceSlot.DisplayName.Trim(),
            SavedAtUtc = sourceSlot?.SavedAtUtc,
            Paths = (sourceSlot?.Paths ?? new List<string>())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path.Trim())
                .Where(static path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SourceScope = NormalizeOptionalText(sourceSlot?.SourceScope),
            SourceCategoryId = NormalizeOptionalText(sourceSlot?.SourceCategoryId),
            SourceCategoryName = NormalizeOptionalText(sourceSlot?.SourceCategoryName),
            SourceTabId = sourceSlot?.SourceTabId,
            SourceTabDisplayName = NormalizeOptionalText(sourceSlot?.SourceTabDisplayName)
        };
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeImportedSourceScope(string? sourceScope, out bool normalizedUnknownScope)
    {
        normalizedUnknownScope = false;
        string? normalized = NormalizeOptionalText(sourceScope);
        if (normalized == null)
        {
            return null;
        }

        if (string.Equals(normalized, MarkSlotSourceScopes.CurrentTab, StringComparison.Ordinal) ||
            string.Equals(normalized, MarkSlotSourceScopes.CurrentCategory, StringComparison.Ordinal) ||
            string.Equals(normalized, MarkSlotSourceScopes.Workspace, StringComparison.Ordinal) ||
            string.Equals(normalized, MarkSlotSourceScopes.SlotSetOperation, StringComparison.Ordinal))
        {
            return normalized;
        }

        normalizedUnknownScope = true;
        return null;
    }
}
