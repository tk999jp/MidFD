namespace MidFD.Configuration;

public sealed record AuthorToolsStartupResult(
    bool Enabled,
    bool HasConflict,
    bool SaveFailed,
    string? Notification);

public enum AuthorToolsCommand
{
    None,
    Enable,
    Disable,
    Conflict
}

public static class AuthorToolsStartup
{
    public const string EnableArgument = "--enable-author-tools";
    public const string DisableArgument = "--disable-author-tools";

    public static AuthorToolsCommand ParseArguments(IEnumerable<string> args)
    {
        bool enableRequested = args.Any(arg => string.Equals(arg, EnableArgument, StringComparison.OrdinalIgnoreCase));
        bool disableRequested = args.Any(arg => string.Equals(arg, DisableArgument, StringComparison.OrdinalIgnoreCase));
        return (enableRequested, disableRequested) switch
        {
            (true, true) => AuthorToolsCommand.Conflict,
            (true, false) => AuthorToolsCommand.Enable,
            (false, true) => AuthorToolsCommand.Disable,
            _ => AuthorToolsCommand.None
        };
    }

    public static AuthorToolsStartupResult Resolve(string[] args)
    {
        bool enabled = AuthorToolsStateStore.Load(out bool storedEnabled, out string? loadError)
            ? storedEnabled
            : false;
        AuthorToolsCommand command = ParseArguments(args);

        if (command == AuthorToolsCommand.Conflict)
        {
            return new AuthorToolsStartupResult(
                enabled,
                HasConflict: true,
                SaveFailed: false,
                Notification: "--enable-author-tools と --disable-author-tools は同時指定できません。作者状態は変更していません。" );
        }

        if (command == AuthorToolsCommand.None)
        {
            return new AuthorToolsStartupResult(
                enabled,
                HasConflict: false,
                SaveFailed: false,
                Notification: loadError == null ? null : $"作者状態を読み込めませんでした。既定値(false)で起動します: {loadError}");
        }

        bool requestedState = command == AuthorToolsCommand.Enable;
        if (AuthorToolsStateStore.TrySave(requestedState, out string? saveError))
        {
            return new AuthorToolsStartupResult(requestedState, false, false, null);
        }

        return new AuthorToolsStartupResult(
            enabled,
            HasConflict: false,
            SaveFailed: true,
            Notification: $"作者状態を保存できなかったため、従前状態({(enabled ? "有効" : "無効")})で起動します: {saveError}");
    }
}
