using MidFD.Configuration;

namespace MidFD.Services.Workspace;

public static class WorkspaceStateMigrationService
{
    public static WorkspaceState FromSessionSnapshot(BrowserTabRestoreSnapshot snapshot)
    {
        return new WorkspaceState
        {
            RestoreSnapshot = snapshot.Clone(),
            SavedAtUtc = DateTime.UtcNow
        };
    }
}
