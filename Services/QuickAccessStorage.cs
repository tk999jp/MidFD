using System.Text.Json;
using MidFD.Models;

namespace MidFD.Services;

public static class QuickAccessStorage
{
    private static readonly string QuickAccessFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static QuickAccessStorage()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        QuickAccessFilePath = Path.Combine(exeDir, "quickaccess.json");
    }

    public static bool Exists() => File.Exists(QuickAccessFilePath);

    public static QuickAccessStore Load()
    {
        if (!Exists())
        {
            return new QuickAccessStore();
        }

        try
        {
            string json = File.ReadAllText(QuickAccessFilePath);
            var store = JsonSerializer.Deserialize<QuickAccessStore>(json, JsonOptions);
            return QuickAccessService.SanitizeStore(store);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to load quickaccess.json.", ex);
            return new QuickAccessStore();
        }
    }

    public static void Save(QuickAccessStore store)
    {
        try
        {
            string json = JsonSerializer.Serialize(store, JsonOptions);
            File.WriteAllText(QuickAccessFilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to save quickaccess.json.", ex);
        }
    }
}
