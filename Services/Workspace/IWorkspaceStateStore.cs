namespace MidFD.Services.Workspace;

public interface IWorkspaceStateStore
{
    WorkspaceState? Load();

    void Save(WorkspaceState state);

    void Clear();
}
