using System.Text.Json;

namespace MidFD.Configuration;

public static class AuthorToolsStateStore
{
    private const string StateFileName = "author-tools.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string CurrentStatePath => GetStatePath(SettingsManager.CurrentSettingsDbPath);

    public static bool Load(out bool enabled, out string? errorMessage)
        => LoadFromPath(CurrentStatePath, out enabled, out errorMessage);

    public static bool TrySave(bool enabled, out string? errorMessage)
        => TrySaveToPath(CurrentStatePath, enabled, out errorMessage);

    internal static string GetStatePath(string settingsDbPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(settingsDbPath)) ?? AppContext.BaseDirectory;
        return Path.Combine(directory, StateFileName);
    }

    internal static bool LoadFromPath(string path, out bool enabled, out string? errorMessage)
    {
        enabled = false;
        errorMessage = null;
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            AuthorToolsState? state = JsonSerializer.Deserialize<AuthorToolsState>(File.ReadAllText(path), JsonOptions);
            if (state == null)
            {
                errorMessage = "作者状態ファイルが空です。";
                return false;
            }

            enabled = state.Enabled;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    internal static bool TrySaveToPath(string path, bool enabled, out string? errorMessage)
    {
        errorMessage = null;
        string? temporaryPath = null;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                errorMessage = "作者状態保存先が不正です。";
                return false;
            }

            Directory.CreateDirectory(directory);
            temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new AuthorToolsState(enabled), JsonOptions));
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
                temporaryPath = null;
            }
            else
            {
                File.Move(temporaryPath, path);
                temporaryPath = null;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            errorMessage = ex.Message;
            return false;
        }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private sealed record AuthorToolsState(bool Enabled);
}
