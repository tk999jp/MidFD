namespace MidFD.Configuration;

public static class SessionRestorePolicy
{
    public static bool ShouldRestoreStartupState(SessionSettings session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RestoreStartupState;
    }

    public static bool ShouldRestoreStartupWorkspace(SessionSettings session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RestoreStartupState && session.RestoreTabsOnStartup;
    }

    public static bool ShouldRestoreStartupFolder(SessionSettings session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RestoreStartupState && session.RestoreLastPath;
    }

    public static bool ShouldRestoreDisplayState(SessionSettings session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RestoreStartupState;
    }

    public static bool ShouldRestoreWindowBounds(SessionSettings session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RestoreStartupState && session.RestoreWindowBounds;
    }

    public static bool ShouldRestoreColumnCount(SessionSettings session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RestoreStartupState && session.RestoreColumnCount;
    }

    public static bool ShouldRestoreSort(SessionSettings session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RestoreStartupState && session.RestoreSort;
    }
}
