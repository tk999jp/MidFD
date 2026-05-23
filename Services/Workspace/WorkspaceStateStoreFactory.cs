using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MidFD.Services.Workspace;

public static class WorkspaceStateStoreFactory
{
    public static string GetDefaultDbPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Data", "Workspace", "workspace.db");
    }

    public static IWorkspaceStateStore CreateDefault()
    {
        string dbPath = GetDefaultDbPath();
        try
        {
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT 1;";
                    command.ExecuteScalar();
                }
            }

            LogService.Info($"[WorkspaceStore] SqliteWorkspaceStateStore initialized successfully at: {dbPath}");
            return new SqliteWorkspaceStateStore(dbPath);
        }
        catch (Exception ex)
        {
            LogService.Warn($"[WorkspaceStore] Workspace SQLite unavailable. fallback to no-op workspace store. Path={dbPath} Error={ex.Message}");
            return new NoOpWorkspaceStateStore();
        }
    }
}
