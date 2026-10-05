using System;
using System.IO;
using System.Text.Json;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// 外部ツール定義の読み込みと保存を担うサービス。
/// </summary>
public static class ExternalToolCommandStorage
{
    private static readonly string FilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static ExternalToolCommandStorage()
    {
        // ポータブル運用のため実行ディレクトリに配置
        string exeDir = AppContext.BaseDirectory;
        FilePath = Path.Combine(exeDir, "external_tools.json");
    }

    public static ExternalToolCommandStore Load() => Load(FilePath);

    internal static ExternalToolCommandStore Load(string filePath)
    {
        try
        {
            using FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            var store = JsonSerializer.Deserialize<ExternalToolCommandStore>(json, JsonOptions);
            if (store == null || store.SchemaVersion != 1) throw new InvalidDataException("Unsupported external tool definitions schema.");
            return store;
        }
        catch (FileNotFoundException) { return new ExternalToolCommandStore(); }
        catch (DirectoryNotFoundException) { return new ExternalToolCommandStore(); }
        catch (Exception ex)
        {
            LogService.Error("Failed to load external_tools.json.", ex);
            return new ExternalToolCommandStore { LoadFailed = true };
        }
    }

    public static bool Save(ExternalToolCommandStore store) => Save(store, FilePath);

    internal static bool Save(ExternalToolCommandStore store, string filePath)
    {
        if (store.LoadFailed)
        {
            LogService.Warn("Refusing to overwrite external_tools.json after a load failure.");
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
            LogService.Error($"Failed to save external tool definitions to '{filePath}'.", ex);
            return false;
        }
    }

    public static string GetFilePath() => FilePath;
}
