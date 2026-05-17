using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MidFD.Models;

namespace MidFD.Services;

public static class CommandPaletteUsageStorage
{
    private const int MaxRecentCommands = 50;
    private static readonly string FilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static CommandPaletteUsageStorage()
    {
        string exeDir = AppContext.BaseDirectory;
        FilePath = Path.Combine(exeDir, "command_palette_usage.json");
    }

    public static CommandPaletteUsageState Load()
    {
        if (!File.Exists(FilePath))
        {
            return new CommandPaletteUsageState();
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            var state = JsonSerializer.Deserialize<CommandPaletteUsageState>(json, JsonOptions);
            return Sanitize(state);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to load command_palette_usage.json.", ex);
            return new CommandPaletteUsageState();
        }
    }

    public static void Save(CommandPaletteUsageState state)
    {
        try
        {
            string json = JsonSerializer.Serialize(Sanitize(state), JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to save command_palette_usage.json.", ex);
        }
    }

    public static void RecordRecent(CommandPaletteUsageState state, string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return;
        }

        CommandPaletteUsageState sanitized = Sanitize(state);
        state.SchemaVersion = sanitized.SchemaVersion;
        state.FavoriteCommandIds = sanitized.FavoriteCommandIds;
        state.RecentCommands = sanitized.RecentCommands;

        string normalizedId = commandId.Trim();
        CommandPaletteRecentCommand? existing = state.RecentCommands.FirstOrDefault(
            item => string.Equals(item.CommandId, normalizedId, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new CommandPaletteRecentCommand { CommandId = normalizedId };
            state.RecentCommands.Add(existing);
        }

        existing.LastUsedUtc = DateTime.UtcNow;
        existing.UseCount = Math.Max(0, existing.UseCount) + 1;
        state.RecentCommands = state.RecentCommands
            .OrderByDescending(static item => item.LastUsedUtc)
            .Take(MaxRecentCommands)
            .ToList();
    }

    private static CommandPaletteUsageState Sanitize(CommandPaletteUsageState? source)
    {
        var sanitized = new CommandPaletteUsageState();
        if (source == null || source.SchemaVersion != CommandPaletteUsageState.CurrentSchemaVersion)
        {
            return sanitized;
        }

        sanitized.FavoriteCommandIds = (source.FavoriteCommandIds ?? new List<string>())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        sanitized.RecentCommands = (source.RecentCommands ?? new List<CommandPaletteRecentCommand>())
            .Where(static item => !string.IsNullOrWhiteSpace(item.CommandId))
            .GroupBy(static item => item.CommandId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                CommandPaletteRecentCommand latest = group
                    .OrderByDescending(static item => item.LastUsedUtc)
                    .First();
                return new CommandPaletteRecentCommand
                {
                    CommandId = group.Key,
                    LastUsedUtc = latest.LastUsedUtc,
                    UseCount = group.Sum(static item => Math.Max(0, item.UseCount))
                };
            })
            .OrderByDescending(static item => item.LastUsedUtc)
            .Take(MaxRecentCommands)
            .ToList();

        return sanitized;
    }
}
