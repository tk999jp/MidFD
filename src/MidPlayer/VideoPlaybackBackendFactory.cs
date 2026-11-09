using MidFD.Configuration;

namespace MidFD;

internal static class VideoPlaybackBackendFactory
{
    internal static IVideoPlaybackBackend Create(string? selectedBackend)
    {
        if (string.Equals(selectedBackend, VideoPlaybackBackendKind.MediaFoundation, StringComparison.OrdinalIgnoreCase))
            return new MediaFoundationPlaybackBackend();
        return FullVideoPlaybackExtensionLoader.CreateBackend(selectedBackend)
            ?? new MediaFoundationPlaybackBackend();
    }
}
