namespace MidFD.Configuration.Storage;

public sealed class InstalledStoragePathProvider : IStoragePathProvider
{
    private readonly string _appContextBaseDirectory;
    private readonly string _localAppDataDirectory;
    private readonly string _tempRoot;

    public InstalledStoragePathProvider(
        string appContextBaseDirectory,
        string localAppDataDirectory,
        string tempRoot)
    {
        _appContextBaseDirectory = NormalizeRequiredDirectory(appContextBaseDirectory, nameof(appContextBaseDirectory));
        _localAppDataDirectory = NormalizeRequiredDirectory(localAppDataDirectory, nameof(localAppDataDirectory));
        _tempRoot = NormalizeRequiredDirectory(tempRoot, nameof(tempRoot));
    }

    public AppStoragePaths GetPaths()
    {
        string appBase = _appContextBaseDirectory;
        string profileRoot = Path.Combine(_localAppDataDirectory, "MidFD");

        return new AppStoragePaths(
            StorageProfileKind.Installed,
            appBase,
            profileRoot,
            appBase,
            Path.Combine(appBase, "UserDocs"),
            Path.Combine(profileRoot, "settings.json"),
            Path.Combine(profileRoot, "Data", "Settings", "settings.db"),
            Path.Combine(profileRoot, "quickaccess.json"),
            Path.Combine(profileRoot, "markslots.json"),
            Path.Combine(profileRoot, "external_tools.json"),
            Path.Combine(profileRoot, "command_palette_usage.json"),
            Path.Combine(profileRoot, "Data", "Workspace", "workspace.db"),
            Path.Combine(profileRoot, "Data", "Trash", "manifest.db"),
            Path.Combine(profileRoot, "Logs"),
            Path.Combine(profileRoot, "Logs"),
            Path.Combine(profileRoot, "video-still-preview-cache"),
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
