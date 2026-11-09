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

    public static CommandPaletteUsageState Load() => Load(FilePath);

    internal static CommandPaletteUsageState Load(string filePath)
    {
        try
        {
            string json = JsonResourceSafety.ReadValidatedJsonFile(filePath, FileShare.ReadWrite | FileShare.Delete);
            var state = JsonSerializer.Deserialize<CommandPaletteUsageState>(json, JsonOptions);
            if (state == null) throw new JsonException("Command palette usage payload is empty.");
            return Sanitize(state);
        }
        catch (FileNotFoundException) { return new CommandPaletteUsageState(); }
        catch (DirectoryNotFoundException) { return new CommandPaletteUsageState(); }
        catch (Exception ex)
        {
            LogService.Error("Failed to load command_palette_usage.json.", ex);
            return new CommandPaletteUsageState { LoadFailed = true };
        }
    }

    public static bool Save(CommandPaletteUsageState state) => Save(state, FilePath);

    internal static bool Save(CommandPaletteUsageState state, string filePath)
    {
        if (state.LoadFailed)
        {
            LogService.Warn("Refusing to overwrite command_palette_usage.json after a load failure.");
            return false;
        }

        try
        {
            JsonResourceSafety.WriteJsonAtomically(filePath, stream => JsonSerializer.Serialize(stream, Sanitize(state), JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to save command_palette_usage.json to '{filePath}'.", ex);
            return false;
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

    internal static bool ToggleFavorite(
        CommandPaletteUsageState state,
        string commandId,
        bool persist,
        Func<CommandPaletteUsageState, bool>? save = null)
    {
        if (state.LoadFailed || string.IsNullOrWhiteSpace(commandId))
        {
            return false;
        }

        List<string> previousFavorites = state.FavoriteCommandIds.ToList();
        string normalizedId = commandId.Trim();
        if (previousFavorites.Any(id => string.Equals(id, normalizedId, StringComparison.OrdinalIgnoreCase)))
        {
            state.FavoriteCommandIds = previousFavorites
                .Where(id => !string.Equals(id, normalizedId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            state.FavoriteCommandIds = previousFavorites
                .Append(normalizedId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!persist || (save ?? Save)(state))
        {
            return true;
        }

        state.FavoriteCommandIds = previousFavorites;
        return false;
    }

    private static CommandPaletteUsageState Sanitize(CommandPaletteUsageState? source)
    {
        var sanitized = new CommandPaletteUsageState();
        if (source == null)
        {
            return sanitized;
        }
        sanitized.LoadFailed = source.LoadFailed || source.SchemaVersion != CommandPaletteUsageState.CurrentSchemaVersion;
        if (sanitized.LoadFailed) return sanitized;

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
