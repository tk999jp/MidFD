namespace MidFD.Configuration.Storage;

public static class StorageProfileProviderFactory
{
    public static IStoragePathProvider CreateDefault()
    {
        return CreatePortable();
    }

    public static IStoragePathProvider CreatePortable()
    {
        return LegacyStoragePathProvider.CreateDefault();
    }

    public static IStoragePathProvider CreateInstalledCandidate()
    {
        return new InstalledStoragePathProvider(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath());
    }

    public static IStoragePathProvider CreateForActivation(StorageProfileActivation activation)
    {
        return activation.Kind == StorageProfileKind.Installed
            ? CreateInstalledCandidate()
            : CreatePortable();
    }

    public static StorageProfileInfo CreateDefaultProfileInfo()
    {
        return StorageProfileResolver.CreatePortableProfileInfo(CreatePortable());
    }

}
