using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteConnectionStringBuilder = Microsoft.Data.Sqlite.SqliteConnectionStringBuilder;
using SqliteOpenMode = Microsoft.Data.Sqlite.SqliteOpenMode;

namespace MidFD.Services;

public static class SqlitePreviewService
{
    public const int DefaultRowLimit = 50;
    private const int MaxObjects = 40;
    internal const int MaxValueBytes = 1024 * 1024;
    internal const int MaxPreviewChars = 1024 * 1024;
    private const int MaxColumns = 128;
    private const int MaxCellTextLength = 240;
    private const int BlobHexPrefixBytes = 16;
    private const string Separator = "------------------------------------------------------------";

    public static async Task<string> GetPreviewAsync(string path, CancellationToken token)
    {
        return await Task.Run(() => BuildPreview(path, DefaultRowLimit, token), token);
    }

    public static string BuildPreview(string path, int rowLimit, CancellationToken token)
    {
        rowLimit = Math.Clamp(rowLimit, 1, DefaultRowLimit);
        var sb = new StringBuilder();
        sb.AppendLine($"[SQLite Preview: {Path.GetFileName(path)}]");
        sb.AppendLine($"Read-only connection / forced LIMIT {rowLimit}");
        sb.AppendLine();

        try
        {
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            // SQLite enforces this before materializing a value/row, not after GetValue.
            SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, MaxValueBytes);
            SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_SQL_LENGTH, 128 * 1024);
            SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_COLUMN, MaxColumns);
            SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_EXPR_DEPTH, 100);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            int steps = 0;
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000,
                _ => token.IsCancellationRequested || ++steps >= 5000 || elapsed.Elapsed > TimeSpan.FromSeconds(2) ? 1 : 0, null);

            var objects = LoadObjects(connection, token);
            if (objects.Count == 0)
            {
                sb.AppendLine("No user tables or views.");
                return sb.ToString();
            }

            sb.AppendLine("== Tables / Views ==");
            sb.AppendLine($"Objects: {Math.Min(objects.Count, MaxObjects)}{(objects.Count > MaxObjects ? " (more omitted)" : string.Empty)}");
            foreach (var obj in objects.Take(MaxObjects))
            {
                sb.AppendLine($"- {obj.Type}: {obj.Name}");
            }
            sb.AppendLine();

            int index = 0;
            foreach (var obj in objects.Take(MaxObjects))
            {
                token.ThrowIfCancellationRequested();
                if (sb.Length >= MaxPreviewChars)
                {
                    sb.AppendLine("[Preview omitted: output resource limit reached]");
                    break;
                }
                index++;
                AppendObjectPreview(sb, connection, obj, index, Math.Min(objects.Count, MaxObjects), rowLimit, token);
                sb.AppendLine();
            }

            if (objects.Count > MaxObjects)
            {
                sb.AppendLine($"[Skipped] Additional objects beyond the first {MaxObjects} were omitted.");
            }

            return sb.ToString();
        }
        catch (SqliteException ex)
        {
            token.ThrowIfCancellationRequested();
            return sb.AppendLine($"[プレビュー不可: SQLite として開けません] {ex.Message}").ToString();
        }
        catch (IOException)
        {
            return sb.AppendLine("[プレビュー不可: 使用中またはロックされています]").ToString();
        }
        catch (UnauthorizedAccessException)
        {
            return sb.AppendLine("[プレビュー不可: アクセス権限がありません]").ToString();
        }
    }

    private static List<SqliteObjectInfo> LoadObjects(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN length(name) <= 4096 THEN name END, type, substr(COALESCE(sql, ''), 1, 16384)
            FROM sqlite_schema
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            LIMIT 41
            """;

        var objects = new List<SqliteObjectInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0)) throw new IOException("SQLite object name exceeds preview resource limit.");
            objects.Add(new SqliteObjectInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
            if (objects[^1].Name.Length > 4096) throw new IOException("SQLite object name exceeds preview resource limit.");
        }

        return objects;
    }

    private static void AppendObjectPreview(
        StringBuilder sb,
        SqliteConnection connection,
        SqliteObjectInfo obj,
        int index,
        int total,
        int rowLimit,
        CancellationToken token)
    {
        sb.AppendLine(Separator);
        sb.AppendLine($"[{obj.Type} {index}/{total}] {obj.Name}");
        if (!string.IsNullOrWhiteSpace(obj.SchemaSql))
        {
            sb.AppendLine();
            sb.AppendLine("Schema");
            sb.AppendLine(obj.SchemaSql);
        }

        sb.AppendLine();
        AppendColumns(sb, connection, obj.Name, token);
        sb.AppendLine();
        AppendRows(sb, connection, obj.Name, rowLimit, token);
    }

    private static void AppendColumns(StringBuilder sb, SqliteConnection connection, string objectName, CancellationToken token)
    {
        sb.AppendLine("Columns");
        sb.AppendLine("cid\tname\ttype\tnull\tpk");
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(objectName)})";
            using var reader = command.ExecuteReader();
            bool hasColumn = false;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (sb.Length >= MaxPreviewChars) { sb.AppendLine("[Preview omitted: metadata output resource limit reached]"); break; }
                hasColumn = true;
                string cid = Convert.ToString(reader["cid"], CultureInfo.InvariantCulture) ?? string.Empty;
                string name = FormatValue(reader["name"]);
                string type = FormatValue(reader["type"]);
                bool notNull = Convert.ToInt32(reader["notnull"], CultureInfo.InvariantCulture) != 0;
                bool pk = Convert.ToInt32(reader["pk"], CultureInfo.InvariantCulture) != 0;
                sb.AppendLine($"{cid}\t{name}\t{(string.IsNullOrWhiteSpace(type) ? "(none)" : type)}\t{(notNull ? "NO" : "YES")}\t{(pk ? "YES" : "NO")}");
            }

            if (!hasColumn)
            {
                sb.AppendLine("(no column metadata)");
            }
        }
        catch (SqliteException ex)
        {
            token.ThrowIfCancellationRequested();
            sb.AppendLine($"- [column read failed / preview resource limit] {ex.Message}");
        }
    }

    private static void AppendRows(
        StringBuilder sb,
        SqliteConnection connection,
        string objectName,
        int rowLimit,
        CancellationToken token)
    {
        sb.AppendLine($"Rows (first {rowLimit})");
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {QuoteIdentifier(objectName)} LIMIT $limit";
            command.Parameters.AddWithValue("$limit", rowLimit);

            using var reader = command.ExecuteReader();
            string[] columns = Enumerable.Range(0, reader.FieldCount)
                .Select(reader.GetName)
                .ToArray();
            sb.Append("#\t");
            sb.AppendLine(string.Join("\t", columns));

            int rowCount = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (sb.Length >= MaxPreviewChars)
                {
                    sb.AppendLine("[Preview omitted: output resource limit reached]");
                    break;
                }
                var values = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = FormatValue(reader.GetValue(i));
                }

                rowCount++;
                sb.Append(rowCount.ToString(CultureInfo.InvariantCulture));
                sb.Append('\t');
                sb.AppendLine(string.Join("\t", values));
            }

            if (rowCount == 0)
            {
                sb.AppendLine("(no rows)");
            }
        }
        catch (SqliteException ex)
        {
            token.ThrowIfCancellationRequested();
            sb.AppendLine($"[row read failed / preview resource limit] {ex.Message}");
        }
    }

    internal static string FormatValue(object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            return "<NULL>";
        }

        if (value is byte[] bytes)
        {
            string hex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, BlobHexPrefixBytes)));
            string suffix = bytes.Length > BlobHexPrefixBytes ? "..." : string.Empty;
            return $"<BLOB {bytes.Length} bytes; hex={hex}{suffix}>";
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        text = text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        if (text.Length > MaxCellTextLength)
        {
            text = $"{text[..MaxCellTextLength]}... <TEXT {text.Length} chars>";
        }

        return text;
    }

    internal static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record SqliteObjectInfo(string Name, string Type, string SchemaSql);
}
