using System.Text.Json;
using Microsoft.Data.Sqlite;
using MidFD.Services;
using SqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteConnectionStringBuilder = Microsoft.Data.Sqlite.SqliteConnectionStringBuilder;
using SqliteOpenMode = Microsoft.Data.Sqlite.SqliteOpenMode;
using SqliteTransaction = Microsoft.Data.Sqlite.SqliteTransaction;

namespace MidFD.Configuration;

public sealed class SettingsSqliteStore
{
    private const int SchemaVersion = 1;
    private const int PayloadVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dbPath;
    private readonly string _legacyJsonPath;
    private readonly string _legacyBackupPath;
    private readonly string _connectionString;

    public SettingsSqliteStore(string dbPath, string legacyJsonPath)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _legacyJsonPath = Path.GetFullPath(legacyJsonPath);
        _legacyBackupPath = _legacyJsonPath + ".bak";
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public SettingsLoadResult Load()
    {
        try
        {
            using SqliteConnection connection = OpenInitializedConnection();
            SettingsLoadResult? sqliteResult = TryLoadFromSqlite(connection);
            if (sqliteResult != null)
            {
                return sqliteResult;
            }

            if (File.Exists(_legacyJsonPath))
            {
                return ImportLegacyJson(connection);
            }

            return new SettingsLoadResult(new AppSettings(), new SettingsManager.SettingsLoadMetadata());
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to load settings from SQLite. Path={_dbPath}", ex);
            return new SettingsLoadResult(new AppSettings(), new SettingsManager.SettingsLoadMetadata());
        }
    }

    public void Save(AppSettings settings)
    {
        Save(settings, new SettingsManager.SettingsLoadMetadata
        {
            IsProfileExplicit = true,
            IsMouseGesturesExplicit = true
        });
    }

    public void Save(AppSettings settings, SettingsManager.SettingsLoadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(metadata);

        try
        {
            using SqliteConnection connection = OpenInitializedConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            UpsertState(connection, transaction, settings, metadata, sourceJsonPath: null, backupJsonPath: null);
            transaction.Commit();
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to save settings to SQLite. Path={_dbPath}", ex);
        }
    }

    private SettingsLoadResult ImportLegacyJson(SqliteConnection connection)
    {
        string json = File.ReadAllText(_legacyJsonPath);
        AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        AppSettings settings = loaded ?? new AppSettings();
        SettingsManager.SettingsLoadMetadata metadata = SettingsManager.ExtractLoadMetadata(json);

        using SqliteTransaction transaction = connection.BeginTransaction();
        UpsertState(connection, transaction, settings, metadata, _legacyJsonPath, _legacyBackupPath);
        transaction.Commit();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_legacyBackupPath) ?? string.Empty);
            File.Move(_legacyJsonPath, _legacyBackupPath, true);
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to rename legacy settings JSON after SQLite import. Source={_legacyJsonPath} Backup={_legacyBackupPath} Error={ex.Message}");
        }

        return new SettingsLoadResult(settings, metadata);
    }

    private SettingsLoadResult? TryLoadFromSqlite(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, payload_version, payload_json, is_profile_explicit, is_mouse_gestures_explicit
            FROM settings_state
            WHERE id = 1;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        int schemaVersion = reader.GetInt32(0);
        if (schemaVersion != SchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported settings schema version: {schemaVersion}");
        }

        int payloadVersion = reader.GetInt32(1);
        string payloadJson = reader.GetString(2);
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(payloadJson, JsonOptions) ?? new AppSettings();
        var metadata = new SettingsManager.SettingsLoadMetadata
        {
            IsProfileExplicit = reader.GetInt32(3) != 0,
            IsMouseGesturesExplicit = reader.GetInt32(4) != 0
        };

        if (payloadVersion != PayloadVersion)
        {
            LogService.Warn($"Unsupported settings payload version encountered. Path={_dbPath}, payloadVersion={payloadVersion}");
        }

        return new SettingsLoadResult(settings, metadata);
    }

    private void UpsertState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AppSettings settings,
        SettingsManager.SettingsLoadMetadata metadata,
        string? sourceJsonPath,
        string? backupJsonPath)
    {
        string payloadJson = JsonSerializer.Serialize(settings, JsonOptions);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings_state (
                id,
                schema_version,
                payload_version,
                payload_json,
                is_profile_explicit,
                is_mouse_gestures_explicit,
                imported_from_json_path,
                backup_json_path,
                imported_at_utc,
                updated_at_utc
            )
            VALUES (
                1,
                $schema_version,
                $payload_version,
                $payload_json,
                $is_profile_explicit,
                $is_mouse_gestures_explicit,
                $imported_from_json_path,
                $backup_json_path,
                $imported_at_utc,
                $updated_at_utc
            )
            ON CONFLICT(id) DO UPDATE SET
                schema_version = excluded.schema_version,
                payload_version = excluded.payload_version,
                payload_json = excluded.payload_json,
                is_profile_explicit = excluded.is_profile_explicit,
                is_mouse_gestures_explicit = excluded.is_mouse_gestures_explicit,
                imported_from_json_path = excluded.imported_from_json_path,
                backup_json_path = excluded.backup_json_path,
                imported_at_utc = excluded.imported_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$schema_version", SchemaVersion);
        command.Parameters.AddWithValue("$payload_version", PayloadVersion);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$is_profile_explicit", metadata.IsProfileExplicit ? 1 : 0);
        command.Parameters.AddWithValue("$is_mouse_gestures_explicit", metadata.IsMouseGesturesExplicit ? 1 : 0);
        command.Parameters.AddWithValue("$imported_from_json_path", string.IsNullOrWhiteSpace(sourceJsonPath) ? DBNull.Value : sourceJsonPath);
        command.Parameters.AddWithValue("$backup_json_path", string.IsNullOrWhiteSpace(backupJsonPath) ? DBNull.Value : backupJsonPath);
        command.Parameters.AddWithValue("$imported_at_utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenInitializedConnection()
    {
        string? directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                """;
            pragma.ExecuteNonQuery();
        }

        InitializeSchema(connection);
        return connection;
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS settings_state (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                schema_version INTEGER NOT NULL,
                payload_version INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                is_profile_explicit INTEGER NOT NULL,
                is_mouse_gestures_explicit INTEGER NOT NULL,
                imported_from_json_path TEXT,
                backup_json_path TEXT,
                imported_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public sealed record SettingsLoadResult(AppSettings Settings, SettingsManager.SettingsLoadMetadata Metadata);
}
