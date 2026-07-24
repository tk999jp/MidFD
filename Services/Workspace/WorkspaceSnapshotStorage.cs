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

    private IReadOnlyList<(WorkspaceSnapshotEntry Entry, string PayloadJson)> LoadEntriesInternal(bool includePayload)
    {
        if (!File.Exists(_dbPath)) return Array.Empty<(WorkspaceSnapshotEntry Entry, string PayloadJson)>();
        using SqliteConnection connection = OpenReadOnlyConnection();
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
        using SqliteConnection connection = OpenReadOnlyConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM workspace_snapshots
            WHERE snapshot_id = $snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);

        object? result = command.ExecuteScalar();
        if (result is not string json || string.IsNullOrWhiteSpace(json))
        {
            errorMessage = "スナップショットの内容が見つかりません。";
            return false;
        }

        payloadJson = json;
        return true;
    }

    public bool TrySaveSnapshot(string name, WorkspaceState state, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            errorMessage = "スナップショット名を入力してください。";
            return false;
        }

        if (!HasRestorableTabs(state))
        {
            errorMessage = "保存できるWorkspace状態がありません。";
            return false;
        }

        string trimmedName = name.Trim();
        string payloadJson = JsonSerializer.Serialize(state, JsonOptions);
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

    public bool ExistsByName(string name)
    {
        if (!File.Exists(_dbPath)) return false;
        using SqliteConnection connection = OpenReadOnlyConnection();
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

    private SqliteConnection OpenReadOnlyConnection()
    {
        string immutableUri = $"file:{Path.GetFullPath(_dbPath).Replace('\\', '/')}?immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = immutableUri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
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
