namespace MidFD.Configuration.Storage;

public sealed class LegacyStoragePathProvider : IStoragePathProvider
{
    private readonly string _appDomainBaseDirectory;
    private readonly string _appContextBaseDirectory;
    private readonly string _localAppDataDirectory;
    private readonly string _tempRoot;

    public LegacyStoragePathProvider(
        string appDomainBaseDirectory,
        string appContextBaseDirectory,
        string localAppDataDirectory,
        string tempRoot)
    {
        _appDomainBaseDirectory = NormalizeRequiredDirectory(appDomainBaseDirectory, nameof(appDomainBaseDirectory));
        _appContextBaseDirectory = NormalizeRequiredDirectory(appContextBaseDirectory, nameof(appContextBaseDirectory));
        _localAppDataDirectory = NormalizeRequiredDirectory(localAppDataDirectory, nameof(localAppDataDirectory));
        _tempRoot = NormalizeRequiredDirectory(tempRoot, nameof(tempRoot));
    }

    public static LegacyStoragePathProvider CreateDefault()
    {
        return new LegacyStoragePathProvider(
            AppDomain.CurrentDomain.BaseDirectory,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath());
    }

    public AppStoragePaths GetPaths()
    {
        string profileRoot = _appDomainBaseDirectory;
        string appBase = _appContextBaseDirectory;
        string midFdLocalAppData = Path.Combine(_localAppDataDirectory, "MidFD");

        return new AppStoragePaths(
            StorageProfileKind.LegacyPortable,
            appBase,
            profileRoot,
            appBase,
            Path.Combine(appBase, "UserDocs"),
            Path.Combine(_appDomainBaseDirectory, "settings.json"),
            Path.Combine(appBase, "Data", "Settings", "settings.db"),
            Path.Combine(_appDomainBaseDirectory, "quickaccess.json"),
            Path.Combine(_appDomainBaseDirectory, "markslots.json"),
            Path.Combine(appBase, "external_tools.json"),
            Path.Combine(appBase, "command_palette_usage.json"),
            Path.Combine(appBase, "Data", "Workspace", "workspace.db"),
            Path.Combine(appBase, "Data", "Trash", "manifest.db"),
            Path.Combine(appBase, "logs"),
            Path.Combine(midFdLocalAppData, "Logs"),
            Path.Combine(midFdLocalAppData, "video-still-preview-cache"),
            _tempRoot,
            Path.Combine(profileRoot, "Backups"));
    }

    private static string NormalizeRequiredDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", parameterName);
        }

        return Path.GetFullPath(path);
    }
}
