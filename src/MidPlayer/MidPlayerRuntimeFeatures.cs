namespace MidFD;

public static class MidPlayerRuntimeFeatures
{
    public static bool HasFullVideoBackend => FullVideoPlaybackExtensionLoader.IsAvailable;
}
