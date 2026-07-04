namespace MidFD.Configuration.Storage;

public static class StorageProfileResolver
{
    public static StorageProfileKind GetDefaultProfileKind()
    {
        return StorageProfileKind.Portable;
    }

    public static StorageProfileInfo CreatePortableProfileInfo(IStoragePathProvider provider)
    {
        return FromPaths(provider.GetPaths(), StorageProfileKind.Portable);
    }

    public static StorageProfileInfo CreateInstalledProfileInfo(IStoragePathProvider provider)
    {
        return FromPaths(provider.GetPaths(), StorageProfileKind.Installed);
    }

    public static StorageProfileInfo FromPaths(AppStoragePaths paths, StorageProfileKind? explicitKind = null)
    {
        StorageProfileKind kind = explicitKind ?? NormalizeKind(paths.ProfileKind);
        string dataRoot = kind switch
        {
            StorageProfileKind.Installed or StorageProfileKind.Packaged => Path.Combine(paths.ProfileRoot, "Data"),
            _ => Path.Combine(paths.AppBaseDirectory, "Data")
        };
        string archivePreviewTempRoot = paths.TempRoot;
        string dragArchiveTempRoot = Path.Combine(paths.TempRoot, "MidFD", "DragArchive");

        return new StorageProfileInfo(
            kind,
            paths.ProfileRoot,
            dataRoot,
            paths.SettingsDbPath,
            paths.LogDirectory,
            paths.TempRoot,
            archivePreviewTempRoot,
            dragArchiveTempRoot,
            paths.SettingsJsonPath,
            paths.BackupDirectory);
    }

    public static bool IsPortableLike(StorageProfileKind kind)
    {
        return NormalizeKind(kind) == StorageProfileKind.Portable;
    }

    private static StorageProfileKind NormalizeKind(StorageProfileKind kind)
    {
        return kind == StorageProfileKind.LegacyPortable
            ? StorageProfileKind.Portable
            : kind;
    }
}
