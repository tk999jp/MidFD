using System.Text.Json;
using Microsoft.Data.Sqlite;
using MidFD.Configuration;
using MidFD.Models;
using SqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteConnectionStringBuilder = Microsoft.Data.Sqlite.SqliteConnectionStringBuilder;

namespace MidFD.Services.Workspace;

public sealed class WorkspaceSnapshotStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath;

    public WorkspaceSnapshotStorage(string dbPath)
    {
        _dbPath = dbPath;
    }

    public IReadOnlyList<WorkspaceSnapshotEntry> LoadEntries()
    {
        return LoadEntriesInternal(false).Select(static x => x.Entry).ToList();
    }

    public IReadOnlyList<(WorkspaceSnapshotEntry Entry, string PayloadJson)> LoadAllSnapshotsWithPayload()
    {
        return LoadEntriesInternal(true);
    }

    internal IEnumerable<(WorkspaceSnapshotEntry Entry, string PayloadJson)> EnumerateSnapshotsWithPayload()
    {
        if (!File.Exists(_dbPath)) yield break;

        using var read = WorkspaceReadConnection.Open(_dbPath);
        SqliteConnection connection = read.Connection;
        using var transaction = connection.BeginTransaction(deferred: true);
        if (!HasSnapshotTable(connection)) yield break;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_id, name, created_at_utc, updated_at_utc,
                   category_count, tab_count, marked_count, active_path, payload_json,
                   length(CAST(name AS BLOB)), length(CAST(payload_json AS BLOB))
            FROM workspace_snapshots
            ORDER BY updated_at_utc DESC, name ASC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            long nameBytes = reader.GetInt64(9);
            long payloadBytes = reader.GetInt64(10);
            if (nameBytes > WorkspaceSnapshotImportSafety.MaxJsonStringBytes)
            {
                throw new InvalidDataException("スナップショット名がエクスポート上限を超えています。");
            }
            if (payloadBytes > WorkspaceSnapshotImportSafety.MaxSnapshotPayloadBytes)
            {
                throw new InvalidDataException("個別スナップショットのpayloadが上限を超えています。");
            }

            var entry = new WorkspaceSnapshotEntry
            {
                SnapshotId = reader.GetString(0),
                Name = reader.GetString(1),
                CreatedAtUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                UpdatedAtUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                CategoryCount = reader.GetInt32(4),
                TabCount = reader.GetInt32(5),
                MarkedCount = reader.GetInt32(6),
                ActivePath = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
            };
            yield return (entry, reader.GetString(8));
        }
    }

    private IReadOnlyList<(WorkspaceSnapshotEntry Entry, string PayloadJson)> LoadEntriesInternal(bool includePayload)
    {
        if (!File.Exists(_dbPath)) return Array.Empty<(WorkspaceSnapshotEntry Entry, string PayloadJson)>();
        using var read = WorkspaceReadConnection.Open(_dbPath);
        SqliteConnection connection = read.Connection;
        using var transaction = connection.BeginTransaction(deferred: true);
        if (!HasSnapshotTable(connection)) return Array.Empty<(WorkspaceSnapshotEntry Entry, string PayloadJson)>();
        using var command = connection.CreateCommand();
        string columns = includePayload
            ? "snapshot_id, name, created_at_utc, updated_at_utc, category_count, tab_count, marked_count, active_path, payload_json"
            : "snapshot_id, name, created_at_utc, updated_at_utc, category_count, tab_count, marked_count, active_path";

        command.CommandText = $"""
            SELECT {columns}
            FROM workspace_snapshots
            ORDER BY updated_at_utc DESC, name ASC;
            """;

        var results = new List<(WorkspaceSnapshotEntry Entry, string PayloadJson)>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            var entry = new WorkspaceSnapshotEntry
            {
                SnapshotId = reader.GetString(0),
                Name = reader.GetString(1),
                CreatedAtUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                UpdatedAtUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime(),
                CategoryCount = reader.GetInt32(4),
                TabCount = reader.GetInt32(5),
                MarkedCount = reader.GetInt32(6),
                ActivePath = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
            };
            string payload = includePayload ? reader.GetString(8) : string.Empty;
            results.Add((entry, payload));
        }

        return results;
    }

    public bool TryLoadSnapshotState(string snapshotId, out WorkspaceState? state, out string errorMessage)
    {
        if (TryGetSnapshotPayload(snapshotId, out string? payloadJson, out errorMessage))
        {
            try
            {
                state = JsonSerializer.Deserialize<WorkspaceState>(payloadJson!, JsonOptions);
                if (!HasRestorableTabs(state))
                {
                    errorMessage = "スナップショットの内容が不正です。";
                    state = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"スナップショットの読込に失敗しました: {ex.Message}";
                state = null;
                return false;
            }
        }
        state = null;
        return false;
    }

    public bool TryGetSnapshotPayload(string snapshotId, out string? payloadJson, out string errorMessage)
    {
        payloadJson = null;
        errorMessage = string.Empty;
        if (!File.Exists(_dbPath))
        {
            errorMessage = "スナップショットの内容が見つかりません。";
            return false;
        }
        using var read = WorkspaceReadConnection.Open(_dbPath);
        SqliteConnection connection = read.Connection;
        using var transaction = connection.BeginTransaction(deferred: true);
        if (!HasSnapshotTable(connection))
        {
            errorMessage = "スナップショットの内容が見つかりません。";
            return false;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_json, length(CAST(payload_json AS BLOB))
            FROM workspace_snapshots
            WHERE snapshot_id = $snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            errorMessage = "スナップショットの内容が見つかりません。";
            return false;
        }
        if (reader.GetInt64(1) > WorkspaceSnapshotImportSafety.MaxSnapshotPayloadBytes)
        {
            errorMessage = "個別スナップショットのpayloadが上限を超えています。";
            return false;
        }
        string json = reader.GetString(0);
        if (string.IsNullOrWhiteSpace(json))
        {
            errorMessage = "スナップショットの内容が見つかりません。";
            return false;
        }

        payloadJson = json;
        return true;
    }

    public bool TrySaveSnapshot(string name, WorkspaceState state, out string errorMessage)
    {
        if (!TryPrepareWorkspaceSnapshot(name, state, isImport: false, out string trimmedName, out string payloadJson, out errorMessage))
        {
            return false;
        }

        WorkspaceSnapshotEntry summary = CreateSummary(Guid.NewGuid().ToString("D"), trimmedName, state);

        using SqliteConnection connection = OpenInitializedConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_snapshots (
                snapshot_id, name, created_at_utc, updated_at_utc, category_count, tab_count, marked_count, active_path, payload_json)
            VALUES (
                $snapshot_id, $name, $created_at_utc, $updated_at_utc, $category_count, $tab_count, $marked_count, $active_path, $payload_json)
            ON CONFLICT(name) DO UPDATE SET
                updated_at_utc = excluded.updated_at_utc,
                category_count = excluded.category_count,
                tab_count = excluded.tab_count,
                marked_count = excluded.marked_count,
                active_path = excluded.active_path,
                payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$snapshot_id", summary.SnapshotId);
        command.Parameters.AddWithValue("$name", summary.Name);
        command.Parameters.AddWithValue("$created_at_utc", summary.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", summary.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$category_count", summary.CategoryCount);
        command.Parameters.AddWithValue("$tab_count", summary.TabCount);
        command.Parameters.AddWithValue("$marked_count", summary.MarkedCount);
        command.Parameters.AddWithValue("$active_path", string.IsNullOrWhiteSpace(summary.ActivePath) ? DBNull.Value : summary.ActivePath);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.ExecuteNonQuery();
        return true;
    }

    internal bool TrySaveSnapshotIfNameAvailable(
        string name,
        WorkspaceState state,
        out bool nameAlreadyExists,
        out string errorMessage)
    {
        nameAlreadyExists = false;
        if (!TryPrepareWorkspaceSnapshot(name, state, isImport: false, out string trimmedName, out string payloadJson, out errorMessage))
        {
            return false;
        }

        WorkspaceSnapshotEntry summary = CreateSummary(Guid.NewGuid().ToString("D"), trimmedName, state);
        using SqliteConnection connection = OpenInitializedConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_snapshots (
                snapshot_id, name, created_at_utc, updated_at_utc, category_count, tab_count, marked_count, active_path, payload_json)
            VALUES (
                $snapshot_id, $name, $created_at_utc, $updated_at_utc, $category_count, $tab_count, $marked_count, $active_path, $payload_json)
            ON CONFLICT(name) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$snapshot_id", summary.SnapshotId);
        command.Parameters.AddWithValue("$name", summary.Name);
        command.Parameters.AddWithValue("$created_at_utc", summary.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", summary.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$category_count", summary.CategoryCount);
        command.Parameters.AddWithValue("$tab_count", summary.TabCount);
        command.Parameters.AddWithValue("$marked_count", summary.MarkedCount);
        command.Parameters.AddWithValue("$active_path", string.IsNullOrWhiteSpace(summary.ActivePath) ? DBNull.Value : summary.ActivePath);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        if (command.ExecuteNonQuery() == 0)
        {
            nameAlreadyExists = true;
            return false;
        }
        return true;
    }

    internal bool TrySaveSnapshotsAtomically(
        IReadOnlyList<(string Name, WorkspaceState State)> snapshots,
        string sourceLabel,
        out IReadOnlyList<string> savedNames,
        out string errorMessage)
    {
        savedNames = Array.Empty<string>();
        errorMessage = string.Empty;
        if (snapshots.Count == 0)
        {
            errorMessage = "保存できるWorkspace状態がありません。";
            return false;
        }

        try
        {
            var prepared = new List<(string RequestedName, WorkspaceState State, string PayloadJson)>(snapshots.Count);
            foreach ((string name, WorkspaceState state) in snapshots)
            {
                if (!TryPrepareWorkspaceSnapshot(name, state, isImport: false, out string requestedName, out string payloadJson, out errorMessage))
                {
                    return false;
                }
                prepared.Add((requestedName, state, payloadJson));
            }

            using SqliteConnection connection = OpenInitializedConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            var resultNames = new List<string>(prepared.Count);

            foreach ((string requestedName, WorkspaceState state, string payloadJson) in prepared)
            {
                resultNames.Add(InsertImportedSnapshot(connection, transaction, requestedName, state, payloadJson, sourceLabel));
            }

            transaction.Commit();
            savedNames = resultNames;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    internal bool TryImportSnapshotsAtomically(
        IEnumerable<(string Name, WorkspaceState State)> snapshots,
        string sourceLabel,
        out int importedCount,
        out string errorMessage)
    {
        importedCount = 0;
        errorMessage = string.Empty;
        try
        {
            using SqliteConnection connection = OpenInitializedConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();
            int count = 0;
            foreach ((string name, WorkspaceState state) in snapshots)
            {
                string candidateName = name?.Trim() ?? string.Empty;
                if (!TryPrepareImportedSnapshot(candidateName, state, out string payloadJson, out string candidateError))
                {
                    throw new InvalidDataException(candidateError);
                }

                _ = InsertImportedSnapshot(connection, transaction, candidateName, state, payloadJson, sourceLabel);
                checked { count++; }
            }

            if (count == 0)
            {
                throw new InvalidDataException("バックアップセットにスナップショットがありません。");
            }

            transaction.Commit();
            importedCount = count;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    internal static bool TryPrepareImportedSnapshot(
        string name,
        WorkspaceState? state,
        out string payloadJson,
        out string errorMessage) =>
        TryPrepareWorkspaceSnapshot(name, state, isImport: true, out _, out payloadJson, out errorMessage);

    private static bool TryPrepareWorkspaceSnapshot(
        string? name,
        WorkspaceState? state,
        bool isImport,
        out string normalizedName,
        out string payloadJson,
        out string errorMessage)
    {
        normalizedName = name?.Trim() ?? string.Empty;
        payloadJson = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            errorMessage = isImport
                ? "バックアップセットの形式または内容が無効です。"
                : "スナップショット名を入力してください。";
            return false;
        }
        if (!WorkspaceSnapshotImportSafety.IsStringWithinBudget(normalizedName))
        {
            errorMessage = "スナップショット名が上限（128 KiB）を超えています。";
            return false;
        }

        if (!WorkspaceSnapshotImportSafety.IsSafeWorkspaceState(state))
        {
            errorMessage = isImport
                ? "バックアップセットの形式または内容が無効です。"
                : "保存できるWorkspace状態がありません。";
            return false;
        }

        try
        {
            payloadJson = JsonSerializer.Serialize(state, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            payloadJson = string.Empty;
            errorMessage = isImport
                ? $"バックアップセットの内容をJSONへ変換できません: {ex.Message}"
                : $"Workspace状態を保存用JSONへ変換できません: {ex.Message}";
            return false;
        }

        if (!WorkspaceSnapshotImportSafety.TryValidateWorkspacePayloadJson(payloadJson, out errorMessage))
        {
            payloadJson = string.Empty;
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool ExistsByName(string name)
    {
        if (!File.Exists(_dbPath)) return false;
        using var read = WorkspaceReadConnection.Open(_dbPath);
        SqliteConnection connection = read.Connection;
        using var transaction = connection.BeginTransaction(deferred: true);
        if (!HasSnapshotTable(connection)) return false;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM workspace_snapshots WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name.Trim());
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    public bool TryRenameSnapshot(string snapshotId, string newName, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(newName))
        {
            errorMessage = "スナップショット名を入力してください。";
            return false;
        }

        using SqliteConnection connection = OpenInitializedConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_snapshots
            SET name = $name,
                updated_at_utc = $updated_at_utc
            WHERE snapshot_id = $snapshot_id;
            """;
        command.Parameters.AddWithValue("$name", newName.Trim());
        command.Parameters.AddWithValue("$updated_at_utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        try
        {
            return command.ExecuteNonQuery() > 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            errorMessage = "同名のスナップショットがすでにあります。";
            return false;
        }
    }

    public bool DeleteSnapshot(string snapshotId)
    {
        using SqliteConnection connection = OpenInitializedConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM workspace_snapshots WHERE snapshot_id = $snapshot_id;";
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        return command.ExecuteNonQuery() > 0;
    }

    private SqliteConnection OpenInitializedConnection()
    {
        string? directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS workspace_snapshots (
                snapshot_id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                category_count INTEGER NOT NULL,
                tab_count INTEGER NOT NULL,
                marked_count INTEGER NOT NULL,
                active_path TEXT,
                payload_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool SnapshotNameExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM workspace_snapshots WHERE name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static string InsertImportedSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requestedName,
        WorkspaceState state,
        string payloadJson,
        string sourceLabel) =>
        InsertImportedSnapshot(connection, transaction, requestedName, state, payloadJson, sourceLabel, DateTime.Now);

    internal static string InsertImportedSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requestedName,
        WorkspaceState state,
        string payloadJson,
        string sourceLabel,
        DateTime collisionTimestamp)
    {
        string name = requestedName;
        string timestampedName = $"{requestedName} ({sourceLabel} {collisionTimestamp:yyyy-MM-dd HH-mm-ss-fff})";
        int suffix = 2;
        bool timestamped = false;
        while (true)
        {
            if (!WorkspaceSnapshotImportSafety.IsStringWithinBudget(name))
            {
                throw new InvalidDataException("インポート後のスナップショット名が上限（128 KiB）を超えています。");
            }

            if (SnapshotNameExists(connection, transaction, name))
            {
                name = timestamped ? $"{timestampedName} ({suffix++})" : timestampedName;
                timestamped = true;
                continue;
            }

            WorkspaceSnapshotEntry summary = CreateSummary(Guid.NewGuid().ToString("D"), name, state);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO workspace_snapshots (
                    snapshot_id, name, created_at_utc, updated_at_utc, category_count, tab_count, marked_count, active_path, payload_json)
                VALUES (
                    $snapshot_id, $name, $created_at_utc, $updated_at_utc, $category_count, $tab_count, $marked_count, $active_path, $payload_json)
                ON CONFLICT(name) DO NOTHING;
                """;
            AddSnapshotParameters(command, summary, payloadJson);
            if (command.ExecuteNonQuery() > 0)
            {
                return name;
            }

            name = timestamped ? $"{timestampedName} ({suffix++})" : timestampedName;
            timestamped = true;
        }
    }

    private static void AddSnapshotParameters(
        SqliteCommand command,
        WorkspaceSnapshotEntry summary,
        string payloadJson)
    {
        command.Parameters.AddWithValue("$snapshot_id", summary.SnapshotId);
        command.Parameters.AddWithValue("$name", summary.Name);
        command.Parameters.AddWithValue("$created_at_utc", summary.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", summary.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$category_count", summary.CategoryCount);
        command.Parameters.AddWithValue("$tab_count", summary.TabCount);
        command.Parameters.AddWithValue("$marked_count", summary.MarkedCount);
        command.Parameters.AddWithValue("$active_path", string.IsNullOrWhiteSpace(summary.ActivePath) ? DBNull.Value : summary.ActivePath);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
    }

    private static bool HasSnapshotTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table_name LIMIT 1;";
        command.Parameters.AddWithValue("$table_name", "workspace_snapshots");
        return command.ExecuteScalar() is not null;
    }

    private static WorkspaceSnapshotEntry CreateSummary(string snapshotId, string name, WorkspaceState state)
    {
        BrowserTabRestoreSnapshot snapshot = state.RestoreSnapshot.Clone();
        int categoryCount = snapshot.Categories.Count;
        int tabCount = snapshot.Categories.Sum(static category => category.OpenTabs.Count);
        int markedCount = snapshot.Categories.Sum(category => category.OpenTabs.Sum(static tab => tab.MarkedPaths?.Count ?? 0));
        BrowserTabRestoreCategoryState? activeCategory = snapshot.Categories.FirstOrDefault(
            category => string.Equals(category.Id, snapshot.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Categories.FirstOrDefault();
        BrowserTabSessionState? activeTab = activeCategory?.OpenTabs.Count > 0
            ? activeCategory.OpenTabs[Math.Clamp(activeCategory.ActiveTabIndex, 0, activeCategory.OpenTabs.Count - 1)]
            : null;
        DateTime now = DateTime.UtcNow;

        return new WorkspaceSnapshotEntry
        {
            SnapshotId = snapshotId,
            Name = name,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CategoryCount = categoryCount,
            TabCount = tabCount,
            MarkedCount = markedCount,
            ActivePath = activeTab?.CurrentPath ?? string.Empty
        };
    }

    private static bool HasRestorableTabs(WorkspaceState? state)
    {
        if (state?.RestoreSnapshot?.Categories is not { Count: > 0 })
        {
            return false;
        }

        return state.RestoreSnapshot.Categories.Any(static category => category.OpenTabs.Count > 0);
    }
}
