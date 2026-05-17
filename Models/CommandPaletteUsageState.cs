using System;
using System.Collections.Generic;

namespace MidFD.Models;

public sealed class CommandPaletteUsageState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<CommandPaletteRecentCommand> RecentCommands { get; set; } = new();
    public List<string> FavoriteCommandIds { get; set; } = new();
}

public sealed class CommandPaletteRecentCommand
{
    public string CommandId { get; set; } = string.Empty;
    public DateTime LastUsedUtc { get; set; }
    public int UseCount { get; set; }
}
