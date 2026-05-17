namespace MidFD.Services.TrashManifestStore;

using System.Globalization;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteTransaction = Microsoft.Data.Sqlite.SqliteTransaction;
using SqliteDataReader = Microsoft.Data.Sqlite.SqliteDataReader;
using SqliteCommand = Microsoft.Data.Sqlite.SqliteCommand;
using SqliteType = Microsoft.Data.Sqlite.SqliteType;
using SqliteConnectionStringBuilder = Microsoft.Data.Sqlite.SqliteConnectionStringBuilder;
using SqliteOpenMode = Microsoft.Data.Sqlite.SqliteOpenMode;

internal sealed class SqliteTrashManifestStore : ITrashManifestStore
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteTrashManifestStore(string dbPath)
    {
        _dbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;

                CREATE TABLE IF NOT EXISTS trash_records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    batch_id TEXT NOT NULL,
                    item_id TEXT NOT NULL,
                    original_path TEXT NOT NULL,
                    trash_path TEXT NOT NULL,
                    original_name TEXT NOT NULL,
                    is_directory INTEGER NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    last_write_time_utc TEXT NOT NULL,
                    deleted_at_utc TEXT NOT NULL,
                    status TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_trash_records_batch_item
                ON trash_records(batch_id, item_id);

                CREATE INDEX IF NOT EXISTS ix_trash_records_status
                ON trash_records(status);

                CREATE INDEX IF NOT EXISTS ix_trash_records_original_path
                ON trash_records(original_path);

                CREATE UNIQUE INDEX IF NOT EXISTS ux_trash_records_trash_path
                ON trash_records(trash_path);
            ";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            LogService.Error($"[MidFdTrash] Failed to initialize SQLite database: {_dbPath}", ex);
            throw;
        }
    }

    public TrashManifest Load()
    {
        var manifest = new TrashManifest();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM trash_records";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                manifest.Records.Add(MapReaderToRecord(reader));
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidFdTrash] Failed to load manifest from SQLite; using empty manifest. error={ex.Message}");
        }

        return manifest;
    }

    public void Save(TrashManifest manifest)
    {
        // Individual records are persisted in Register/Upsert/Update.
    }

    public void RegisterNewRecord(TrashManifest manifest, TrashManifestRecord record)
    {
        InsertRecord(record);
        manifest.Records.Add(record);
    }

    public void RegisterNewRecords(TrashManifest manifest, IEnumerable<TrashManifestRecord> records)
    {
        var recordList = records.ToList();
        if (recordList.Count == 0) return;

        // Optimization: Batch removal from memory O(N + M) instead of O(N * M)
        var trashPathsToRemove = new HashSet<string>(recordList.Select(r => r.TrashPath), StringComparer.OrdinalIgnoreCase);
        var idPairsToRemove = new HashSet<(string, string)>(recordList.Select(r => (r.BatchId, r.ItemId)));

        manifest.Records.RemoveAll(existing =>
            trashPathsToRemove.Contains(existing.TrashPath) ||
            idPairsToRemove.Contains((existing.BatchId, existing.ItemId)));

        manifest.Records.AddRange(recordList);

        var swTotal = Stopwatch.StartNew();
        long connMs = 0;
        long transMs = 0;
        long delMs = 0;
        long insMs = 0;
        long commitMs = 0;

        try
        {
            var sw = Stopwatch.StartNew();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA synchronous=NORMAL";
                pragmaCmd.ExecuteNonQuery();
            }

            sw.Stop();
            connMs = sw.ElapsedMilliseconds;

            sw.Restart();
            using var transaction = connection.BeginTransaction();
            sw.Stop();
            transMs = sw.ElapsedMilliseconds;

            // Note: We use INSERT OR REPLACE to handle collisions on (batch_id, item_id) or trash_path.
            // This avoids the need for a separate DELETE loop.
            delMs = 0;

            sw.Restart();
            using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT OR REPLACE INTO trash_records (
                        batch_id, item_id, original_path, trash_path, original_name,
                        is_directory, size_bytes, last_write_time_utc, deleted_at_utc, status
                    ) VALUES (
                        @bid, @iid, @op, @tp, @on, @isd, @sz, @lw, @da, @st
                    )";
                insertCmd.Parameters.Add("@bid", SqliteType.Text);
                insertCmd.Parameters.Add("@iid", SqliteType.Text);
                insertCmd.Parameters.Add("@op", SqliteType.Text);
                insertCmd.Parameters.Add("@tp", SqliteType.Text);
                insertCmd.Parameters.Add("@on", SqliteType.Text);
                insertCmd.Parameters.Add("@isd", SqliteType.Integer);
                insertCmd.Parameters.Add("@sz", SqliteType.Integer);
                insertCmd.Parameters.Add("@lw", SqliteType.Text);
                insertCmd.Parameters.Add("@da", SqliteType.Text);
                insertCmd.Parameters.Add("@st", SqliteType.Text);
                insertCmd.Prepare();

                foreach (var record in recordList)
                {
                    insertCmd.Parameters["@bid"].Value = record.BatchId;
                    insertCmd.Parameters["@iid"].Value = record.ItemId;
                    insertCmd.Parameters["@op"].Value = record.OriginalPath;
                    insertCmd.Parameters["@tp"].Value = record.TrashPath;
                    insertCmd.Parameters["@on"].Value = record.OriginalName;
                    insertCmd.Parameters["@isd"].Value = record.IsDirectory ? 1 : 0;
                    insertCmd.Parameters["@sz"].Value = record.Size;
                    insertCmd.Parameters["@lw"].Value = record.LastWriteTimeUtc.ToString("O");
                    insertCmd.Parameters["@da"].Value = record.DeletedAtUtc.ToString("O");
                    insertCmd.Parameters["@st"].Value = record.Status.ToString();
                    insertCmd.ExecuteNonQuery();
                }
            }
            sw.Stop();
            insMs = sw.ElapsedMilliseconds;

            sw.Restart();
            transaction.Commit();
            sw.Stop();
            commitMs = sw.ElapsedMilliseconds;

            MidFdManagedTrashService.RecordDbOperationTimings(connMs, transMs, delMs, insMs, commitMs);
        }
        catch (Exception ex)
        {
            LogService.Error($"[MidFdTrash] SQLite RegisterNewRecords failed. count={recordList.Count}", ex);
            throw;
        }
    }

    public int UpsertRecord(TrashManifest manifest, TrashManifestRecord record)
    {
        int scanCount = manifest.Records.Count;

        manifest.Records.RemoveAll(existing =>
            string.Equals(existing.TrashPath, record.TrashPath, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(existing.BatchId, record.BatchId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(existing.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase)));
        manifest.Records.Add(record);

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM trash_records WHERE trash_path = @tp OR (batch_id = @bid AND item_id = @iid)";
                deleteCmd.Parameters.AddWithValue("@tp", record.TrashPath);
                deleteCmd.Parameters.AddWithValue("@bid", record.BatchId);
                deleteCmd.Parameters.AddWithValue("@iid", record.ItemId);
                deleteCmd.ExecuteNonQuery();
            }

            InsertRecordInternal(connection, transaction, record);
            transaction.Commit();
        }
        catch (Exception ex)
        {
            LogService.Error($"[MidFdTrash] SQLite UpsertRecord failed. trash={record.TrashPath}", ex);
            throw;
        }

        return scanCount;
    }

    public bool UpdateRecordStatus(TrashManifest manifest, string trashPath, TrashRecordStatus status)
    {
        var record = manifest.Records.FirstOrDefault(r => string.Equals(r.TrashPath, trashPath, StringComparison.OrdinalIgnoreCase));
        if (record == null) return false;
        record.Status = status;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE trash_records SET status = @status WHERE trash_path = @tp";
            command.Parameters.AddWithValue("@status", status.ToString());
            command.Parameters.AddWithValue("@tp", trashPath);
            return command.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            LogService.Error($"[MidFdTrash] SQLite UpdateRecordStatus failed. trash={trashPath}, status={status}", ex);
            return false;
        }
    }

    public int UpdateRecordStatuses(TrashManifest manifest, IEnumerable<string> trashPaths, TrashRecordStatus status)
    {
        var paths = trashPaths.ToList();
        if (paths.Count == 0) return 0;

        // Update memory
        var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var record in manifest.Records)
        {
            if (pathSet.Contains(record.TrashPath))
            {
                record.Status = status;
            }
        }

        // Update DB
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE trash_records SET status = @status WHERE trash_path = @tp";
            command.Parameters.Add("@status", SqliteType.Text);
            command.Parameters.Add("@tp", SqliteType.Text);
            command.Prepare();

            int updated = 0;
            foreach (var path in paths)
            {
                command.Parameters["@status"].Value = status.ToString();
                command.Parameters["@tp"].Value = path;
                updated += command.ExecuteNonQuery();
            }

            transaction.Commit();
            return updated;
        }
        catch (Exception ex)
        {
            LogService.Error($"[MidFdTrash] SQLite UpdateRecordStatuses failed. count={paths.Count}, status={status}", ex);
            return 0;
        }
    }

    public bool TryGetRecordByOriginalPath(TrashManifest manifest, string originalPath, out TrashManifestRecord? record)
    {
        record = manifest.Records.LastOrDefault(r => string.Equals(r.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase));
        return record != null;
    }

    public (int inserted, int updated, int skipped) UpsertRecords(TrashManifest manifest, IEnumerable<TrashManifestRecord> records)
    {
        int inserted = 0;
        int updated = 0;
        int skipped = 0;

        var existingDict = manifest.Records.ToDictionary(r => $"{r.BatchId}:{r.ItemId}", StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM trash_records WHERE batch_id = @bid AND item_id = @iid";
            var delBidParam = deleteCmd.Parameters.Add("@bid", SqliteType.Text);
            var delIidParam = deleteCmd.Parameters.Add("@iid", SqliteType.Text);

            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
                INSERT INTO trash_records (
                    batch_id, item_id, original_path, trash_path, original_name,
                    is_directory, size_bytes, last_write_time_utc, deleted_at_utc, status
                ) VALUES (
                    @bid, @iid, @op, @tp, @on, @isd, @sz, @lwt, @dat, @st
                )
            ";
            var insBidParam = insertCmd.Parameters.Add("@bid", SqliteType.Text);
            var insIidParam = insertCmd.Parameters.Add("@iid", SqliteType.Text);
            var insOpParam = insertCmd.Parameters.Add("@op", SqliteType.Text);
            var insTpParam = insertCmd.Parameters.Add("@tp", SqliteType.Text);
            var insOnParam = insertCmd.Parameters.Add("@on", SqliteType.Text);
            var insIsdParam = insertCmd.Parameters.Add("@isd", SqliteType.Integer);
            var insSzParam = insertCmd.Parameters.Add("@sz", SqliteType.Integer);
            var insLwtParam = insertCmd.Parameters.Add("@lwt", SqliteType.Text);
            var insDatParam = insertCmd.Parameters.Add("@dat", SqliteType.Text);
            var insStParam = insertCmd.Parameters.Add("@st", SqliteType.Text);

            foreach (var record in records)
            {
                string key = $"{record.BatchId}:{record.ItemId}";
                if (!existingDict.TryGetValue(key, out var existing))
                {
                    insBidParam.Value = record.BatchId;
                    insIidParam.Value = record.ItemId;
                    insOpParam.Value = record.OriginalPath;
                    insTpParam.Value = record.TrashPath;
                    insOnParam.Value = record.OriginalName;
                    insIsdParam.Value = record.IsDirectory ? 1 : 0;
                    insSzParam.Value = record.Size;
                    insLwtParam.Value = record.LastWriteTimeUtc.ToString("O");
                    insDatParam.Value = record.DeletedAtUtc.ToString("O");
                    insStParam.Value = record.Status.ToString();
                    insertCmd.ExecuteNonQuery();

                    existingDict[key] = record;
                    inserted++;
                }
                else if (existing.Status != record.Status || existing.TrashPath != record.TrashPath)
                {
                    delBidParam.Value = record.BatchId;
                    delIidParam.Value = record.ItemId;
                    deleteCmd.ExecuteNonQuery();

                    insBidParam.Value = record.BatchId;
                    insIidParam.Value = record.ItemId;
                    insOpParam.Value = record.OriginalPath;
                    insTpParam.Value = record.TrashPath;
                    insOnParam.Value = record.OriginalName;
                    insIsdParam.Value = record.IsDirectory ? 1 : 0;
                    insSzParam.Value = record.Size;
                    insLwtParam.Value = record.LastWriteTimeUtc.ToString("O");
                    insDatParam.Value = record.DeletedAtUtc.ToString("O");
                    insStParam.Value = record.Status.ToString();
                    insertCmd.ExecuteNonQuery();

                    existingDict[key] = record;
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }

            transaction.Commit();

            manifest.Records.Clear();
            manifest.Records.AddRange(existingDict.Values);
        }
        catch (Exception ex)
        {
            LogService.Error($"[MidFdTrash] SQLite UpsertRecords failed.", ex);
            throw;
        }

        return (inserted, updated, skipped);
    }

    private void InsertRecord(TrashManifestRecord record)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            InsertRecordInternal(connection, null, record);
        }
        catch (Exception ex)
        {
            LogService.Error($"[MidFdTrash] SQLite InsertRecord failed. trash={record.TrashPath}", ex);
            throw;
        }
    }

    private void InsertRecordInternal(SqliteConnection connection, SqliteTransaction? transaction, TrashManifestRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO trash_records (
                batch_id, item_id, original_path, trash_path, original_name,
                is_directory, size_bytes, last_write_time_utc, deleted_at_utc, status
            ) VALUES (
                @bid, @iid, @op, @tp, @on, @isd, @sz, @lwt, @dat, @st
            )
        ";
        command.Parameters.AddWithValue("@bid", record.BatchId);
        command.Parameters.AddWithValue("@iid", record.ItemId);
        command.Parameters.AddWithValue("@op", record.OriginalPath);
        command.Parameters.AddWithValue("@tp", record.TrashPath);
        command.Parameters.AddWithValue("@on", record.OriginalName);
        command.Parameters.AddWithValue("@isd", record.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("@sz", record.Size);
        command.Parameters.AddWithValue("@lwt", record.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("@dat", record.DeletedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("@st", record.Status.ToString());
        command.ExecuteNonQuery();
    }

    private TrashManifestRecord MapReaderToRecord(SqliteDataReader reader)
    {
        return new TrashManifestRecord
        {
            BatchId = reader.GetString(reader.GetOrdinal("batch_id")),
            ItemId = reader.GetString(reader.GetOrdinal("item_id")),
            OriginalPath = reader.GetString(reader.GetOrdinal("original_path")),
            TrashPath = reader.GetString(reader.GetOrdinal("trash_path")),
            OriginalName = reader.GetString(reader.GetOrdinal("original_name")),
            IsDirectory = reader.GetInt32(reader.GetOrdinal("is_directory")) != 0,
            Size = reader.GetInt64(reader.GetOrdinal("size_bytes")),
            LastWriteTimeUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_write_time_utc")), null, DateTimeStyles.RoundtripKind),
            DeletedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("deleted_at_utc")), null, DateTimeStyles.RoundtripKind),
            Status = Enum.Parse<TrashRecordStatus>(reader.GetString(reader.GetOrdinal("status")))
        };
    }
}
