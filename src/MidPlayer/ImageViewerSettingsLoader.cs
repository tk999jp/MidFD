using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using MidFD.Configuration;

namespace MidFD;

internal static class ImageViewerSettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static ImageViewerSettings LoadStandalone()
        => LoadFromDatabase(ResolveSettingsDatabasePath());

    internal static ImageViewerSettings LoadFromDatabase(string databasePath)
    {
        if (!File.Exists(databasePath)) return new ImageViewerSettings();
        try
        {
            string? payload = ReadSettingsPayload(databasePath);
            if (payload == null) return new ImageViewerSettings();
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement preview = FindProperty(document.RootElement, "Preview");
            if (preview.ValueKind != JsonValueKind.Object) return new ImageViewerSettings();

            var settings = new ImageViewerSettings
            {
                InitialFitLimitWidth = ReadInt(preview, "InitialFitLimitWidth", 1920),
                InitialFitLimitHeight = ReadInt(preview, "InitialFitLimitHeight", 1080),
                VideoSkipSeconds = ReadInt(preview, "VideoSkipSeconds", 0),
                VideoSeekStepSeconds = ReadInt(preview, "VideoSeekStepSeconds", 10),
                VideoPlaybackBackend = VideoPlaybackBackendKind.Normalize(ReadString(preview, "VideoPlaybackBackend")),
                VideoPlaybackVolumePercent = ReadInt(preview, "VideoPlaybackVolumePercent", 100)
            };
            JsonElement susiePlugins = FindProperty(preview, "SusiePlugins");
            if (susiePlugins.ValueKind == JsonValueKind.Object)
            {
                settings.SusiePlugins = susiePlugins.Deserialize<SusiePluginSettings>(JsonOptions) ?? new SusiePluginSettings();
                settings.SusiePlugins.Normalize();
            }
            return settings;
        }
        catch (Exception ex)
        {
            Services.LogService.Warn($"[MidPlayer Settings] Unable to read the active MidFD settings database '{databasePath}': {ex.GetType().Name}: {ex.Message}");
            return new ImageViewerSettings();
        }
    }

    private static string ResolveSettingsDatabasePath()
    {
        string profile = Environment.GetEnvironmentVariable("MIDFD_STORAGE_PROFILE") ?? string.Empty;
        if (profile.Length == 0)
        {
            string bootstrapPath = Path.Combine(AppContext.BaseDirectory, "storage-profile.json");
            if (File.Exists(bootstrapPath))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(bootstrapPath));
                    profile = ReadString(document.RootElement, "profile") ?? string.Empty;
                }
                catch { }
            }
        }

        string root = string.Equals(profile, "installed", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidFD")
            : AppContext.BaseDirectory;
        return Path.Combine(root, "Data", "Settings", "settings.db");
    }

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        return default;
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        JsonElement value = FindProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : fallback;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        JsonElement value = FindProperty(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ReadSettingsPayload(string databasePath)
    {
        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            int result = sqlite3_open_v2(Utf8Z(databasePath), out database, SqliteOpenReadOnly, IntPtr.Zero);
            if (result != SqliteOk) throw new IOException($"SQLite open failed ({result}): {GetSqliteError(database)}");
            result = sqlite3_prepare_v2(database, Utf8Z("SELECT payload_json FROM settings_state WHERE id = 1"), -1, out statement, IntPtr.Zero);
            if (result != SqliteOk) throw new IOException($"SQLite query preparation failed ({result}): {GetSqliteError(database)}");
            result = sqlite3_step(statement);
            if (result == SqliteDone) return null;
            if (result != SqliteRow) throw new IOException($"SQLite query failed ({result}): {GetSqliteError(database)}");
            IntPtr text = sqlite3_column_text(statement, 0);
            int byteLength = sqlite3_column_bytes(statement, 0);
            if (text == IntPtr.Zero || byteLength <= 0) return null;
            byte[] bytes = new byte[byteLength];
            Marshal.Copy(text, bytes, 0, byteLength);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (database != IntPtr.Zero) sqlite3_close_v2(database);
        }
    }

    private static byte[] Utf8Z(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static string GetSqliteError(IntPtr database)
    {
        if (database == IntPtr.Zero) return string.Empty;
        IntPtr message = sqlite3_errmsg(database);
        return message == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(message) ?? string.Empty;
    }

    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);
}
