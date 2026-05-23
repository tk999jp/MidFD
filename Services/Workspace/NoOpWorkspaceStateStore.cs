namespace MidFD.Services.Workspace;

public sealed class NoOpWorkspaceStateStore : IWorkspaceStateStore
{
    public WorkspaceState? Load()
    {
        return null;
    }

    public void Save(WorkspaceState state)
    {
        LogService.Warn("Workspace state save bypassed. Storage is unavailable.");
    }

    public void Clear()
    {
        LogService.Warn("Workspace state clear bypassed. Storage is unavailable.");
    }
}
