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
    public const int CurrentPayloadVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dbPath;
    private readonly string _legacyJsonPath;
    private readonly string _connectionString;
    private readonly string _backupDirectory;

    public SettingsSqliteStore(string dbPath, string legacyJsonPath, string? backupDirectory = null)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _legacyJsonPath = Path.GetFullPath(legacyJsonPath);
        _backupDirectory = Path.GetFullPath(backupDirectory ?? Path.Combine(Path.GetDirectoryName(_dbPath) ?? AppContext.BaseDirectory, "Backups"));
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
            if (File.Exists(_dbPath))
            {
                SettingsLoadResult result;
                try
                {
                    using (SqliteConnection readConnection = OpenReadOnlyConnection())
                    {
                        result = TryLoadFromSqlite(readConnection) ?? CreateRecoveryFailedResult(SettingsLoadStatus.Corrupt);
                    }
                }
                catch (Exception primaryException)
                {
                    result = CreateRecoveryFailedResult(ClassifyLoadFailure(primaryException));
                }
                if (result.CanWritePrimary)
                {
                    return result;
                }

                SettingsLoadResult? recovered = TryLoadBackupOrDefault(result);
                if (recovered != null)
                {
                    return recovered.PrimaryPayloadProtected && !recovered.RecoveredFromBackup
                        ? recovered with
                        {
                            Metadata = new SettingsManager.SettingsLoadMetadata { LoadKind = SettingsManager.SettingsLoadKind.RecoveryFailed }
                        }
                        : recovered;
                }

                return result.PrimaryPayloadProtected
                    ? result with
                    {
                        Metadata = new SettingsManager.SettingsLoadMetadata { LoadKind = SettingsManager.SettingsLoadKind.RecoveryFailed }
                    }
                    : result;
            }

            SettingsLoadResult? missingPrimaryRecovery = TryLoadBackupOrDefault(null);
            if (missingPrimaryRecovery != null) return missingPrimaryRecovery;
            if (!File.Exists(_legacyJsonPath) && EnumerateBackupPaths().Any())
            {
                return CreateRecoveryFailedResult(SettingsLoadStatus.Corrupt);
            }

            if (File.Exists(_legacyJsonPath))
            {
                string json = File.ReadAllText(_legacyJsonPath);
                AppSettings? legacy = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (legacy == null) throw new JsonException("Legacy settings payload is empty.");
                legacy.NormalizeChildren();
                SettingsLoadResult result;
                using (SqliteConnection legacyConnection = OpenInitializedConnection())
                {
                    result = ImportLegacyJson(legacyConnection, legacy, json);
                }
                try
                {
                    using SqliteConnection verifyConnection = OpenReadOnlyConnection();
                    if (TryLoadFromSqlite(verifyConnection) is not { CanWritePrimary: true })
                    {
                        throw new IOException("Legacy settings import verification failed.");
                    }
                }
                catch
                {
                    DeleteIfExists(_dbPath);
                    throw;
                }
                try { MoveLegacyJsonToBackup(); }
                catch (Exception backupException)
                {
                    LogService.Warn($"Legacy settings backup move failed. Path={_legacyJsonPath} Error={backupException.Message}");
                }
                return result;
            }

            return new SettingsLoadResult(
                new AppSettings(),
                new SettingsManager.SettingsLoadMetadata { LoadKind = SettingsManager.SettingsLoadKind.TrueFirstLaunch },
                SettingsLoadStatus.NotFound,
                CanWritePrimary: false);
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to load settings from SQLite. Path={_dbPath}", ex);
            return CreateRecoveryFailedResult(ClassifyLoadFailure(ex));
        }
    }

    public SettingsSaveResult TrySave(AppSettings settings, SettingsManager.SettingsLoadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(metadata);

        string tempPath = _dbPath + ".tmp-" + Guid.NewGuid().ToString("N");
        string snapshotPath = tempPath + ".backup";
        try
        {
            using (SqliteConnection connection = OpenInitializedConnection(tempPath, useWal: false))
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                UpsertState(connection, transaction, settings, metadata, sourceJsonPath: null);
                transaction.Commit();
            }
            ValidateSettingsDb(tempPath);
            AtomicReplace(tempPath, _dbPath);

            BackupGenerationStatus backupStatus = CreateAndRotateBackup(_dbPath, snapshotPath);
            if (backupStatus == BackupGenerationStatus.Success)
            {
                return new SettingsSaveResult(SettingsSaveStatus.Success, _dbPath, string.Empty, null, true);
            }
            return new SettingsSaveResult(
                backupStatus == BackupGenerationStatus.RotationFailed ? SettingsSaveStatus.PrimarySavedBackupRotationFailed : SettingsSaveStatus.PrimarySavedBackupFailed,
                _dbPath,
                "設定は保存されましたが、バックアップ更新に失敗しました。",
                "Primary settings DB was saved; backup generation did not complete.",
                false);
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to save settings to SQLite. Path={_dbPath}", ex);
            return CreateSaveFailure(ex);
        }
        finally
        {
            DeleteTemporaryFile(tempPath);
            DeleteTemporaryFile(snapshotPath);
        }
    }

    public SettingsTransferResult Export(string targetPath, AppSettings settings, SettingsManager.SettingsLoadMetadata metadata)
    {
        try
        {
            string json = JsonSerializer.Serialize(new SettingsTransferDocument
            {
                FormatVersion = 1,
                PayloadVersion = CurrentPayloadVersion,
                Settings = settings
            }, JsonOptions);
            File.WriteAllText(Path.GetFullPath(targetPath), json);
            return new SettingsTransferResult(true, string.Empty, null, settings);
        }
        catch (Exception ex)
        {
            return new SettingsTransferResult(false, "設定をエクスポートできませんでした。", ex.Message, null);
        }
    }

    public SettingsTransferResult Import(string sourcePath)
    {
        try
        {
            string json = File.ReadAllText(Path.GetFullPath(sourcePath));
            SettingsTransferDocument document = JsonSerializer.Deserialize<SettingsTransferDocument>(json, JsonOptions)
                ?? throw new JsonException("Settings transfer document is empty.");
            if (document.FormatVersion != 1 || document.Settings == null)
            {
                throw new JsonException("Unsupported settings transfer document.");
            }

            PayloadVersionDecision payloadDecision = EvaluatePayloadVersion(document.PayloadVersion);
            if (!payloadDecision.IsSupported)
            {
                return new SettingsTransferResult(
                    false,
                    $"未対応の設定形式です。検出PayloadVersion={document.PayloadVersion}、対応PayloadVersion={CurrentPayloadVersion}。",
                    $"PayloadVersion={document.PayloadVersion}; supported={CurrentPayloadVersion}; status={payloadDecision.Status}",
                    null,
                    false,
                    document.PayloadVersion,
                    CurrentPayloadVersion,
                    payloadDecision.Status);
            }

            document.Settings.NormalizeChildren();
            return new SettingsTransferResult(true, string.Empty, null, document.Settings);
        }
        catch (Exception ex)
        {
            return new SettingsTransferResult(false, "設定をインポートできませんでした。", ex.Message, null);
        }
    }

    private SettingsLoadResult ImportLegacyJson(SqliteConnection connection, AppSettings settings, string json)
    {
        SettingsManager.SettingsLoadMetadata metadata = SettingsManager.ExtractLoadMetadata(json);
        metadata.LoadKind = SettingsManager.SettingsLoadKind.NormalPrimary;

        using SqliteTransaction transaction = connection.BeginTransaction();
        UpsertState(connection, transaction, settings, metadata, _legacyJsonPath);
        transaction.Commit();

        return new SettingsLoadResult(settings, metadata, SettingsLoadStatus.Success, CanWritePrimary: true);
    }

    private SettingsLoadResult? TryLoadFromSqlite(SqliteConnection connection)
    {
        ValidateSettingsStructure(connection);
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
            throw new SettingsVersionException($"Unsupported settings schema version: {schemaVersion}");
        }

        int payloadVersion = reader.GetInt32(1);
        string payloadJson = reader.GetString(2);
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(payloadJson, JsonOptions) ?? new AppSettings();
        settings.NormalizeChildren();
        var metadata = new SettingsManager.SettingsLoadMetadata
        {
            IsProfileExplicit = reader.GetInt32(3) != 0,
            IsMouseGesturesExplicit = reader.GetInt32(4) != 0,
            LoadKind = SettingsManager.SettingsLoadKind.NormalPrimary
        };

        PayloadVersionDecision payloadDecision = EvaluatePayloadVersion(payloadVersion);
        if (!payloadDecision.IsSupported)
        {
            return new SettingsLoadResult(
                new AppSettings(),
                metadata,
                SettingsLoadStatus.PayloadVersionMismatch,
                false,
                PayloadVersion: payloadVersion,
                PayloadStatus: payloadDecision.Status,
                PrimaryPayloadProtected: true,
                ProtectedPrimaryPayloadVersion: payloadVersion,
                ProtectedPrimaryPayloadStatus: payloadDecision.Status);
        }

        return new SettingsLoadResult(
            settings,
            metadata,
            SettingsLoadStatus.Success,
            true,
            PayloadVersion: payloadVersion,
            PayloadStatus: payloadDecision.Status);
    }

    private static void ValidateSettingsStructure(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(settings_state);";
        using SqliteDataReader reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) columns.Add(reader.GetString(1));
        string[] required = { "id", "schema_version", "payload_version", "payload_json", "is_profile_explicit", "is_mouse_gestures_explicit" };
        if (required.Any(column => !columns.Contains(column)))
        {
            throw new SettingsStructureException("settings_state table structure is invalid.");
        }
    }

    private void UpsertState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AppSettings settings,
        SettingsManager.SettingsLoadMetadata metadata,
        string? sourceJsonPath)
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
        command.Parameters.AddWithValue("$payload_version", CurrentPayloadVersion);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$is_profile_explicit", metadata.IsProfileExplicit ? 1 : 0);
        command.Parameters.AddWithValue("$is_mouse_gestures_explicit", metadata.IsMouseGesturesExplicit ? 1 : 0);
        command.Parameters.AddWithValue("$imported_from_json_path", string.IsNullOrWhiteSpace(sourceJsonPath) ? DBNull.Value : sourceJsonPath);
        command.Parameters.AddWithValue("$backup_json_path", DBNull.Value);
        command.Parameters.AddWithValue("$imported_at_utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenInitializedConnection(string? dbPath = null, bool useWal = true)
    {
        dbPath ??= _dbPath;
        string? directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"""
                PRAGMA journal_mode={(useWal ? "WAL" : "DELETE")};
                PRAGMA synchronous=NORMAL;
                """;
            pragma.ExecuteNonQuery();
        }

        InitializeSchema(connection);
        return connection;
    }

    private SqliteConnection OpenReadOnlyConnection(string? dbPath = null)
    {
        dbPath ??= _dbPath;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private IEnumerable<string> EnumerateBackupPaths()
    {
        if (!Directory.Exists(_backupDirectory)) yield break;
        foreach (string path in Directory.EnumerateFiles(_backupDirectory, "settings-*.db")
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return path;
        }
    }

    private SettingsLoadResult? TryLoadBackupOrDefault(SettingsLoadResult? primaryResult)
    {
        SettingsLoadResult? firstUnsupportedPayload = null;
        foreach (string backupPath in EnumerateBackupPaths())
        {
            try
            {
                using SqliteConnection backupConnection = OpenReadOnlyConnection(backupPath);
                SettingsLoadResult? backupResult = TryLoadFromSqlite(backupConnection);
                if (backupResult is { CanWritePrimary: true })
                {
                    backupResult.Metadata.LoadKind = SettingsManager.SettingsLoadKind.RecoveredFromBackup;
                    return backupResult with
                    {
                        Status = SettingsLoadStatus.RecoveredFromBackup,
                        RecoveredFromBackup = true,
                        PrimaryPayloadProtected = primaryResult?.PrimaryPayloadProtected == true,
                        ProtectedPrimaryPayloadVersion = primaryResult?.ProtectedPrimaryPayloadVersion,
                        ProtectedPrimaryPayloadStatus = primaryResult?.ProtectedPrimaryPayloadStatus ?? SettingsPayloadVersionStatus.Unknown
                    };
                }

                if (backupResult?.PrimaryPayloadProtected == true && firstUnsupportedPayload == null)
                {
                    firstUnsupportedPayload = backupResult;
                }
            }
            catch (Exception backupException)
            {
                LogService.Warn($"Settings backup validation failed. Path={backupPath} Error={backupException.Message}");
            }
        }

        if (primaryResult != null)
        {
            return primaryResult;
        }

        return firstUnsupportedPayload is { } unsupported
            ? unsupported with
            {
                Metadata = new SettingsManager.SettingsLoadMetadata { LoadKind = SettingsManager.SettingsLoadKind.RecoveryFailed },
                Settings = new AppSettings()
            }
            : null;
    }

    private static SettingsLoadResult CreateRecoveryFailedResult(SettingsLoadStatus status)
    {
        return new SettingsLoadResult(
            new AppSettings(),
            new SettingsManager.SettingsLoadMetadata { LoadKind = SettingsManager.SettingsLoadKind.RecoveryFailed },
            status,
            CanWritePrimary: false);
    }

    private BackupGenerationStatus CreateAndRotateBackup(string sourcePath, string snapshotPath)
    {
        try
        {
            using (SqliteConnection source = OpenReadOnlyConnection(sourcePath))
            using (SqliteConnection target = new(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
            }
            ValidateSettingsDb(snapshotPath);

            Directory.CreateDirectory(_backupDirectory);
            DeleteIfExists(GetBackupPath(5));
            for (int generation = 5; generation >= 1; generation--)
            {
                string path = GetBackupPath(generation);
                if (generation == 5) continue;
                if (File.Exists(path)) File.Move(path, GetBackupPath(generation + 1), true);
            }
            AtomicReplace(snapshotPath, GetBackupPath(1));
            return BackupGenerationStatus.Success;
        }
        catch (Exception ex)
        {
            LogService.Warn($"Settings backup generation failed. Path={_dbPath} Error={ex.Message}");
            return File.Exists(GetBackupPath(1)) ? BackupGenerationStatus.RotationFailed : BackupGenerationStatus.SnapshotFailed;
        }
    }

    private void ValidateSettingsDb(string path)
    {
        using SqliteConnection connection = OpenReadOnlyConnection(path);
        if (TryLoadFromSqlite(connection) is not { CanWritePrimary: true })
        {
            throw new IOException($"Settings DB validation failed. Path={path}");
        }
    }

    private string GetBackupPath(int generation) => Path.Combine(_backupDirectory, $"settings-{generation:000}.db");

    private void MoveLegacyJsonToBackup()
    {
        string backupPath = _legacyJsonPath + ".bak";
        if (!File.Exists(_legacyJsonPath)) return;
        File.Move(_legacyJsonPath, backupPath, true);
    }

    private static void AtomicReplace(string sourcePath, string targetPath)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(targetPath)) File.Replace(sourcePath, targetPath, null);
        else File.Move(sourcePath, targetPath);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            DeleteIfExists(path);
            DeleteIfExists(path + "-wal");
            DeleteIfExists(path + "-shm");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Settings temporary cleanup failed. Path={path} Error={ex.Message}");
        }
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

    private SettingsSaveResult CreateSaveFailure(Exception ex)
    {
        SettingsSaveStatus status = ex is JsonException or NotSupportedException
            ? SettingsSaveStatus.SerializationFailure
            : ex is IOException or UnauthorizedAccessException || ex is SqliteException { SqliteErrorCode: 5 or 6 or 8 or 10 or 13 or 14 }
                ? SettingsSaveStatus.IoFailure
                : SettingsSaveStatus.UnknownFailure;
        return new SettingsSaveResult(status, _dbPath, "設定を保存できませんでした。", ex.Message);
    }

    private static SettingsLoadStatus ClassifyLoadFailure(Exception ex)
    {
        if (ex is SettingsVersionException) return SettingsLoadStatus.UnsupportedVersion;
        if (ex is SettingsStructureException) return SettingsLoadStatus.Corrupt;
        if (ex is JsonException) return SettingsLoadStatus.Corrupt;
        if (ex is IOException or UnauthorizedAccessException) return SettingsLoadStatus.IoFailure;
        if (ex is SqliteException sqliteException)
        {
            return sqliteException.SqliteErrorCode is 5 or 6 or 8 or 10 or 13 or 14 ? SettingsLoadStatus.IoFailure : sqliteException.SqliteErrorCode is 11 or 26 ? SettingsLoadStatus.Corrupt : SettingsLoadStatus.UnknownFailure;
        }

        return SettingsLoadStatus.UnknownFailure;
    }

    private sealed class SettingsVersionException : Exception { public SettingsVersionException(string message) : base(message) { } }
    private sealed class SettingsStructureException : Exception { public SettingsStructureException(string message) : base(message) { } }
    public enum SettingsLoadStatus { Success, NotFound, RecoveredFromBackup, PayloadVersionMismatch, UnsupportedVersion, Corrupt, IoFailure, UnknownFailure }
    public enum SettingsPayloadVersionStatus { Unknown, Current, MigrationAvailable, UnsupportedOlder, UnsupportedFuture }
    public readonly record struct PayloadVersionDecision(SettingsPayloadVersionStatus Status, bool IsSupported);
    public sealed record SettingsLoadResult(
        AppSettings Settings,
        SettingsManager.SettingsLoadMetadata Metadata,
        SettingsLoadStatus Status,
        bool CanWritePrimary,
        bool RecoveredFromBackup = false,
        int? PayloadVersion = null,
        int SupportedPayloadVersion = CurrentPayloadVersion,
        SettingsPayloadVersionStatus PayloadStatus = SettingsPayloadVersionStatus.Unknown,
        bool PrimaryPayloadProtected = false,
        int? ProtectedPrimaryPayloadVersion = null,
        SettingsPayloadVersionStatus ProtectedPrimaryPayloadStatus = SettingsPayloadVersionStatus.Unknown);
    public enum SettingsSaveStatus { Success, PrimarySavedBackupFailed, PrimarySavedBackupRotationFailed, SerializationFailure, IoFailure, PayloadReplacementConfirmationRequired, SuppressedByPayloadProtection, UnknownFailure }
    private enum BackupGenerationStatus { Success, SnapshotFailed, RotationFailed }
    public sealed record SettingsSaveResult(SettingsSaveStatus Status, string TargetPath, string UserMessage, string? DiagnosticDetail, bool BackupSucceeded = false)
    {
        public bool Succeeded => Status is SettingsSaveStatus.Success or SettingsSaveStatus.PrimarySavedBackupFailed or SettingsSaveStatus.PrimarySavedBackupRotationFailed;
        public bool PrimarySaved => Succeeded;
        public bool SuppressedByPayloadProtection => Status == SettingsSaveStatus.SuppressedByPayloadProtection;
    }
    public sealed record SettingsTransferResult(
        bool Succeeded,
        string UserMessage,
        string? DiagnosticDetail,
        AppSettings? Settings,
        bool BackupSucceeded = true,
        int? PayloadVersion = null,
        int SupportedPayloadVersion = CurrentPayloadVersion,
        SettingsPayloadVersionStatus PayloadStatus = SettingsPayloadVersionStatus.Unknown);
    private sealed class SettingsTransferDocument
    {
        public int FormatVersion { get; set; }
        public int PayloadVersion { get; set; }
        public AppSettings? Settings { get; set; }
    }

    private static PayloadVersionDecision EvaluatePayloadVersion(int payloadVersion)
    {
        if (payloadVersion == CurrentPayloadVersion)
        {
            return new PayloadVersionDecision(SettingsPayloadVersionStatus.Current, true);
        }

        return payloadVersion < CurrentPayloadVersion
            ? new PayloadVersionDecision(SettingsPayloadVersionStatus.UnsupportedOlder, false)
            : new PayloadVersionDecision(SettingsPayloadVersionStatus.UnsupportedFuture, false);
    }
}
