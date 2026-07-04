namespace MidFD.Configuration.Storage;

public static class StorageProfileActivationContext
{
    private static readonly object SyncRoot = new();
    private static StorageProfileActivation _current = StorageProfileActivationResolver.Resolve(Array.Empty<string>(), null, string.Empty);

    public static StorageProfileActivation Current
    {
        get
        {
            lock (SyncRoot)
            {
                return _current;
            }
        }
    }

    public static void Initialize(string[] args)
    {
        StorageProfileActivation activation = StorageProfileActivationResolver.ResolveDefault(args);
        lock (SyncRoot)
        {
            _current = activation;
        }
    }
}
