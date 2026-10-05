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

    public static bool Exists()
    {
        try
        {
            using FileStream _ = File.Open(QuickAccessFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return true; }
    }

    public static QuickAccessStore Load() => Load(QuickAccessFilePath);

    internal static QuickAccessStore Load(string filePath)
    {
        try
        {
            using FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            var store = JsonSerializer.Deserialize<QuickAccessStore>(json, JsonOptions);
            if (store == null) throw new JsonException("Quick access payload is empty.");
            return QuickAccessService.SanitizeStore(store);
        }
        catch (FileNotFoundException) { return new QuickAccessStore(); }
        catch (DirectoryNotFoundException) { return new QuickAccessStore(); }
        catch (Exception ex)
        {
            LogService.Error("Failed to load quickaccess.json.", ex);
            return new QuickAccessStore { LoadFailed = true };
        }
    }

    public static bool Save(QuickAccessStore store) => Save(store, QuickAccessFilePath);

    internal static bool Save(QuickAccessStore store, string filePath)
    {
        if (store.LoadFailed)
        {
            LogService.Warn("Refusing to overwrite quickaccess.json after a load failure.");
            return false;
        }

        try
        {
            string json = JsonSerializer.Serialize(store, JsonOptions);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to save quickaccess.json to '{filePath}'.", ex);
            return false;
        }
    }
}
