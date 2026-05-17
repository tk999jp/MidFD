using MidFD.Configuration;

namespace MidFD.Services.Workspace;

public sealed class WorkspaceState
{
    public BrowserTabRestoreSnapshot RestoreSnapshot { get; init; } = new();
    public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;

    public WorkspaceState Clone()
    {
        return new WorkspaceState
        {
            RestoreSnapshot = RestoreSnapshot.Clone(),
            SavedAtUtc = SavedAtUtc
        };
    }
}
