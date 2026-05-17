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

    public static ExternalToolCommandStore Load()
    {
        if (!File.Exists(FilePath))
        {
            return new ExternalToolCommandStore();
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            var store = JsonSerializer.Deserialize<ExternalToolCommandStore>(json, JsonOptions);
            return store ?? new ExternalToolCommandStore();
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to load external_tools.json.", ex);
            return new ExternalToolCommandStore();
        }
    }

    public static void Save(ExternalToolCommandStore store)
    {
        try
        {
            string json = JsonSerializer.Serialize(store, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to save external_tools.json.", ex);
        }
    }

    public static string GetFilePath() => FilePath;
}
