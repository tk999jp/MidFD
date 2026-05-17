using System;
using System.Collections.Generic;
using System.Linq;

namespace MidFD.Models;

public sealed class MarkSlotStore
{
    public List<MarkSlotEntry> Slots { get; set; } = new();

    public MarkSlotStore Clone()
    {
        return new MarkSlotStore
        {
            Slots = Slots.Select(static slot => slot.Clone()).ToList()
        };
    }

    public static MarkSlotStore CreateDefault(int slotCount)
    {
        return new MarkSlotStore
        {
            Slots = Enumerable.Range(1, slotCount)
                .Select(static index => new MarkSlotEntry
                {
                    SlotNumber = index,
                    DisplayName = $"スロット {index}"
                })
                .ToList()
        };
    }
}

public sealed class MarkSlotEntry
{
    public int SlotNumber { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTime? SavedAtUtc { get; set; }
    public List<string> Paths { get; set; } = new();
    public string? SourceScope { get; set; }
    public string? SourceCategoryId { get; set; }
    public string? SourceCategoryName { get; set; }
    public Guid? SourceTabId { get; set; }
    public string? SourceTabDisplayName { get; set; }

    public MarkSlotEntry Clone()
    {
        return new MarkSlotEntry
        {
            SlotNumber = SlotNumber,
            DisplayName = DisplayName,
            SavedAtUtc = SavedAtUtc,
            Paths = new List<string>(Paths),
            SourceScope = SourceScope,
            SourceCategoryId = SourceCategoryId,
            SourceCategoryName = SourceCategoryName,
            SourceTabId = SourceTabId,
            SourceTabDisplayName = SourceTabDisplayName
        };
    }
}

public static class MarkSlotSourceScopes
{
    public const string CurrentTab = "CurrentTab";
    public const string CurrentCategory = "CurrentCategory";
    public const string Workspace = "Workspace";
    public const string SlotSetOperation = "SlotSetOperation";
}

public sealed class MarkSlotExportDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentKind = "MarkSlotExport";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime ExportedAtUtc { get; set; }
    public string AppName { get; set; } = "MidFD";
    public string Kind { get; set; } = CurrentKind;
    public MarkSlotExportEntry? Slot { get; set; }
}

public sealed class MarkSlotExportEntry
{
    public int SlotNumber { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTime? SavedAtUtc { get; set; }
    public string? SourceScope { get; set; }
    public string? SourceCategoryId { get; set; }
    public string? SourceCategoryName { get; set; }
    public Guid? SourceTabId { get; set; }
    public string? SourceTabDisplayName { get; set; }
    public List<string> Paths { get; set; } = new();
}

public sealed class MarkSlotBackupSetDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentKind = "MarkSlotBackupSet";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime ExportedAtUtc { get; set; }
    public string AppName { get; set; } = "MidFD";
    public string Kind { get; set; } = CurrentKind;
    public int SlotCount { get; set; }
    public List<MarkSlotExportEntry> Slots { get; set; } = new();
}
