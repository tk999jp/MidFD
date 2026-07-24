using System.Text.Json;
using Microsoft.Data.Sqlite;
using MidFD.Configuration;
using MidFD.Models;
using MidFD.Services;
using SqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteConnectionStringBuilder = Microsoft.Data.Sqlite.SqliteConnectionStringBuilder;
using SqliteTransaction = Microsoft.Data.Sqlite.SqliteTransaction;

namespace MidFD.Services.Workspace;

public sealed class SqliteWorkspaceStateStore : IWorkspaceStateStore
{
    private const string SchemaVersion = "1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _dbPath;

    public SqliteWorkspaceStateStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public WorkspaceState? Load()
    {
        if (!File.Exists(_dbPath)) return null;
        using SqliteConnection connection = OpenReadOnlyConnection();

        string? activeCategoryId = ReadMeta(connection, "active_category_id");
        string? savedAtValue = ReadMeta(connection, "saved_at_utc");
        DateTime savedAtUtc = DateTime.TryParse(savedAtValue, out DateTime parsedSavedAt)
            ? parsedSavedAt.ToUniversalTime()
            : DateTime.MinValue;

        List<BrowserTabRestoreCategoryState> categories = LoadCategories(connection);
        if (categories.Count == 0)
        {
            return null;
        }

        var snapshot = new BrowserTabRestoreSnapshot
        {
            ActiveCategoryId = string.IsNullOrWhiteSpace(activeCategoryId)
                ? BrowserTabSettings.DefaultCategoryId
                : activeCategoryId
        };
        snapshot.Categories.AddRange(categories);

        return new WorkspaceState
        {
            RestoreSnapshot = snapshot,
            SavedAtUtc = savedAtUtc
        };
    }

    public void Save(WorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SaveCore(state);
    }

    private void SaveCore(WorkspaceState state)
    {
        using SqliteConnection connection = OpenInitializedConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        ExecuteNonQuery(connection, transaction, "DELETE FROM workspace_marks;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM workspace_tabs;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM workspace_categories;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM workspace_meta;");

        WriteMeta(connection, transaction, "schema_version", SchemaVersion);
        WriteMeta(connection, transaction, "active_category_id", state.RestoreSnapshot.ActiveCategoryId);
        WriteMeta(connection, transaction, "saved_at_utc", state.SavedAtUtc.ToString("O"));

        int categoryOrder = 0;
        foreach (BrowserTabRestoreCategoryState category in state.RestoreSnapshot.Categories)
        {
            string categoryId = string.IsNullOrWhiteSpace(category.Id)
                ? BrowserTabSettings.DefaultCategoryId
                : category.Id;
            InsertCategory(connection, transaction, categoryId, category.DisplayName, categoryOrder++);

            int tabOrder = 0;
            foreach (BrowserTabSessionState tab in category.OpenTabs)
            {
                Guid tabId = tab.TabId == Guid.Empty ? Guid.NewGuid() : tab.TabId;
                InsertTab(connection, transaction, categoryId, tabId, tab, tabOrder++);
                InsertMarks(connection, transaction, tabId, tab.MarkedPaths);
            }
        }

        transaction.Commit();
    }

    public void Clear()
    {
        using SqliteConnection connection = OpenInitializedConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        ExecuteNonQuery(connection, transaction, "DELETE FROM workspace_marks;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM workspace_tabs;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM workspace_categories;");
        ExecuteNonQuery(connection, transaction, "DELETE FROM workspace_meta;");
        transaction.Commit();
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
        ExecuteNonQuery(connection, null, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, null, "PRAGMA synchronous=NORMAL;");
        InitializeSchema(connection);
        return connection;
    }

    private SqliteConnection OpenReadOnlyConnection()
    {
        string immutableUri = $"file:{Path.GetFullPath(_dbPath).Replace('\\', '/')}?immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = immutableUri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, null, """
            CREATE TABLE IF NOT EXISTS workspace_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(connection, null, """
            CREATE TABLE IF NOT EXISTS workspace_categories (
                category_id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                sort_order INTEGER NOT NULL
            );
            """);
        ExecuteNonQuery(connection, null, """
            CREATE TABLE IF NOT EXISTS workspace_tabs (
                tab_id TEXT PRIMARY KEY,
                category_id TEXT NOT NULL,
                display_name TEXT,
                current_path TEXT NOT NULL,
                startup_path TEXT,
                is_locked INTEGER NOT NULL,
                is_read_only INTEGER NOT NULL,
                focus_target_name TEXT,
                cursor_index INTEGER NOT NULL,
                column_count INTEGER NOT NULL,
                sort_kind TEXT NOT NULL,
                sort_ascending INTEGER NOT NULL,
                back_history_json TEXT NOT NULL,
                forward_history_json TEXT NOT NULL,
                last_visited_by_drive_json TEXT NOT NULL,
                filter_lock_json TEXT,
                tab_order INTEGER NOT NULL,
                FOREIGN KEY(category_id) REFERENCES workspace_categories(category_id)
            );
            """);
        EnsureColumn(connection, "workspace_tabs", "filter_lock_json", "TEXT");
        ExecuteNonQuery(connection, null, """
            CREATE TABLE IF NOT EXISTS workspace_marks (
                tab_id TEXT NOT NULL,
                path TEXT NOT NULL,
                marked_order INTEGER NOT NULL,
                PRIMARY KEY(tab_id, path),
                FOREIGN KEY(tab_id) REFERENCES workspace_tabs(tab_id)
            );
            """);
    }

    private static List<BrowserTabRestoreCategoryState> LoadCategories(SqliteConnection connection)
    {
        var categories = new List<BrowserTabRestoreCategoryState>();
        var categoryIds = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category_id, display_name
            FROM workspace_categories
            ORDER BY sort_order ASC;
            """;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string categoryId = reader.GetString(0);
                categories.Add(new BrowserTabRestoreCategoryState
                {
                    Id = categoryId,
                    DisplayName = reader.GetString(1)
                });
                categoryIds.Add(categoryId);
            }
        }

        for (int i = 0; i < categories.Count; i++)
        {
            categories[i].OpenTabs = LoadTabs(connection, categoryIds[i]);
        }

        return categories;
    }

    private static List<BrowserTabSessionState> LoadTabs(SqliteConnection connection, string categoryId)
    {
        var tabs = new List<BrowserTabSessionState>();
        var tabIds = new List<Guid>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tab_id, display_name, current_path, startup_path, is_locked, is_read_only,
                   focus_target_name, cursor_index, column_count, sort_kind, sort_ascending,
                   back_history_json, forward_history_json, last_visited_by_drive_json, filter_lock_json
            FROM workspace_tabs
            WHERE category_id = $category_id
            ORDER BY tab_order ASC;
            """;
        command.Parameters.AddWithValue("$category_id", categoryId);

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Guid tabId = Guid.TryParse(reader.GetString(0), out Guid parsedTabId)
                    ? parsedTabId
                    : Guid.NewGuid();
                string sortKindText = reader.GetString(9);
                SortKind sortKind = Enum.TryParse(sortKindText, out SortKind parsedSortKind)
                    ? parsedSortKind
                    : SortKind.Name;

                tabs.Add(new BrowserTabSessionState
                {
                    TabId = tabId,
                    CurrentPath = reader.GetString(2),
                    StartupPath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    IsLocked = reader.GetInt32(4) != 0,
                    IsReadOnly = reader.GetInt32(5) != 0,
                    FocusTargetName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CursorIndex = reader.GetInt32(7),
                    ColumnCount = reader.GetInt32(8),
                    SortKind = sortKind,
                    SortAscending = reader.GetInt32(10) != 0,
                    BackHistory = DeserializeList(reader.GetString(11)),
                    ForwardHistory = DeserializeList(reader.GetString(12)),
                    LastVisitedPathByDrive = DeserializeDictionary(reader.GetString(13)),
                    FilterLock = reader.IsDBNull(14) ? new TabFilterLockState() : DeserializeFilterLock(reader.GetString(14))
                });
                tabIds.Add(tabId);
            }
        }

        for (int i = 0; i < tabs.Count; i++)
        {
            tabs[i].MarkedPaths = LoadMarks(connection, tabIds[i]);
        }

        return tabs;
    }

    private static List<string> LoadMarks(SqliteConnection connection, Guid tabId)
    {
        var marks = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path
            FROM workspace_marks
            WHERE tab_id = $tab_id
            ORDER BY marked_order ASC;
            """;
        command.Parameters.AddWithValue("$tab_id", tabId.ToString("D"));

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            marks.Add(reader.GetString(0));
        }

        return marks;
    }

    private static void InsertCategory(SqliteConnection connection, SqliteTransaction transaction, string categoryId, string displayName, int sortOrder)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workspace_categories (category_id, display_name, sort_order)
            VALUES ($category_id, $display_name, $sort_order);
            """;
        command.Parameters.AddWithValue("$category_id", categoryId);
        command.Parameters.AddWithValue("$display_name", string.IsNullOrWhiteSpace(displayName) ? categoryId : displayName);
        command.Parameters.AddWithValue("$sort_order", sortOrder);
        command.ExecuteNonQuery();
    }

    private static void InsertTab(SqliteConnection connection, SqliteTransaction transaction, string categoryId, Guid tabId, BrowserTabSessionState tab, int tabOrder)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workspace_tabs (
                tab_id, category_id, display_name, current_path, startup_path, is_locked, is_read_only,
                focus_target_name, cursor_index, column_count, sort_kind, sort_ascending,
                back_history_json, forward_history_json, last_visited_by_drive_json, filter_lock_json, tab_order)
            VALUES (
                $tab_id, $category_id, $display_name, $current_path, $startup_path, $is_locked, $is_read_only,
                $focus_target_name, $cursor_index, $column_count, $sort_kind, $sort_ascending,
                $back_history_json, $forward_history_json, $last_visited_by_drive_json, $filter_lock_json, $tab_order);
            """;
        command.Parameters.AddWithValue("$tab_id", tabId.ToString("D"));
        command.Parameters.AddWithValue("$category_id", categoryId);
        command.Parameters.AddWithValue("$display_name", DBNull.Value);
        command.Parameters.AddWithValue("$current_path", tab.CurrentPath ?? string.Empty);
        command.Parameters.AddWithValue("$startup_path", string.IsNullOrWhiteSpace(tab.StartupPath) ? DBNull.Value : tab.StartupPath);
        command.Parameters.AddWithValue("$is_locked", tab.IsLocked ? 1 : 0);
        command.Parameters.AddWithValue("$is_read_only", tab.IsReadOnly ? 1 : 0);
        command.Parameters.AddWithValue("$focus_target_name", string.IsNullOrWhiteSpace(tab.FocusTargetName) ? DBNull.Value : tab.FocusTargetName);
        command.Parameters.AddWithValue("$cursor_index", tab.CursorIndex);
        command.Parameters.AddWithValue("$column_count", tab.ColumnCount);
        command.Parameters.AddWithValue("$sort_kind", tab.SortKind.ToString());
        command.Parameters.AddWithValue("$sort_ascending", tab.SortAscending ? 1 : 0);
        command.Parameters.AddWithValue("$back_history_json", JsonSerializer.Serialize(tab.BackHistory ?? new List<string>(), JsonOptions));
        command.Parameters.AddWithValue("$forward_history_json", JsonSerializer.Serialize(tab.ForwardHistory ?? new List<string>(), JsonOptions));
        command.Parameters.AddWithValue("$last_visited_by_drive_json", JsonSerializer.Serialize(tab.LastVisitedPathByDrive ?? new Dictionary<string, string>(), JsonOptions));
        command.Parameters.AddWithValue("$filter_lock_json", JsonSerializer.Serialize(tab.FilterLock ?? new TabFilterLockState(), JsonOptions));
        command.Parameters.AddWithValue("$tab_order", tabOrder);
        command.ExecuteNonQuery();
    }

    private static void InsertMarks(SqliteConnection connection, SqliteTransaction transaction, Guid tabId, IEnumerable<string>? marks)
    {
        int markOrder = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? mark in marks ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(mark) || !seen.Add(mark))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO workspace_marks (tab_id, path, marked_order)
                VALUES ($tab_id, $path, $marked_order);
                """;
            command.Parameters.AddWithValue("$tab_id", tabId.ToString("D"));
            command.Parameters.AddWithValue("$path", mark);
            command.Parameters.AddWithValue("$marked_order", markOrder++);
            command.ExecuteNonQuery();
        }
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM workspace_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO workspace_meta (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction? transaction, string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        bool exists = false;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({tableName});";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            ExecuteNonQuery(connection, null, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
        }
    }

    private static List<string> DeserializeList(string json)
    {
        return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
    }

    private static Dictionary<string, string> DeserializeDictionary(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static TabFilterLockState DeserializeFilterLock(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TabFilterLockState>(json, JsonOptions) ?? new TabFilterLockState();
        }
        catch
        {
            return new TabFilterLockState();
        }
    }
}
