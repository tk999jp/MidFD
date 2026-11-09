using Microsoft.Data.Sqlite;

namespace MidFD.Services.Workspace;

/// <summary>Live workspace DBs must participate in SQLite's WAL synchronization.
/// ReadOnly never creates a missing main DB or changes its schema/data. SQLite may
/// create WAL synchronization sidecars; an immutable flag or OS write-denial lease
/// would instead ignore commits or prevent a writer connecting after the reader.</summary>
internal sealed class WorkspaceReadConnection : IDisposable
{
    internal SqliteConnection Connection { get; }
    private WorkspaceReadConnection(SqliteConnection connection) => Connection = connection;

    internal static WorkspaceReadConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            return new WorkspaceReadConnection(connection);
        }
        catch { connection.Dispose(); throw; }
    }

    public void Dispose() => Connection.Dispose();
}
