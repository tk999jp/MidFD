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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static MarkSlotStorage()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        MarkSlotFilePath = Path.Combine(exeDir, "markslots.json");
    }

    public static MarkSlotStore Load(int slotCount)
    {
        if (!File.Exists(MarkSlotFilePath))
        {
            return MarkSlotStore.CreateDefault(slotCount);
        }

        try
        {
            string json = File.ReadAllText(MarkSlotFilePath);
            var store = JsonSerializer.Deserialize<MarkSlotStore>(json, JsonOptions);
            return Sanitize(store, slotCount);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to load markslots.json.", ex);
            return MarkSlotStore.CreateDefault(slotCount);
        }
    }

    public static void Save(MarkSlotStore store, int slotCount)
    {
        try
        {
            string json = JsonSerializer.Serialize(Sanitize(store, slotCount), JsonOptions);
            File.WriteAllText(MarkSlotFilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to save markslots.json.", ex);
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

            string json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<MarkSlotExportDocument>(json, JsonOptions);
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

            string json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<MarkSlotBackupSetDocument>(json, JsonOptions);
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
