namespace MidFD.Services.Workspace;

public static class WorkspaceStateStoreFactory
{
    public static string GetDefaultDbPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Data", "Workspace", "workspace.db");
    }

    public static IWorkspaceStateStore CreateDefault()
    {
        return new SqliteWorkspaceStateStore(GetDefaultDbPath());
    }
}
