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
    public const string EnableArgument = StartupArgumentCatalog.EnableAuthorToolsName;
    public const string DisableArgument = StartupArgumentCatalog.DisableAuthorToolsName;

    public static AuthorToolsCommand ParseArguments(IEnumerable<string> args)
        => StartupArgumentParser.Parse(args).AuthorToolsCommand;

    public static AuthorToolsStartupResult Resolve(string[] args)
        => Resolve(StartupArgumentParser.Parse(args).AuthorToolsCommand);

    public static AuthorToolsStartupResult Resolve(AuthorToolsCommand command)
    {
        bool enabled = AuthorToolsStateStore.Load(out bool storedEnabled, out string? loadError)
            ? storedEnabled
            : false;

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
