using MidFD.Configuration;

namespace MidFD;

internal sealed class VideoBackendSelector
{
    private readonly IVideoCodecProbe? _codecProbe;

    internal VideoBackendSelector(IVideoCodecProbe? codecProbe = null, IVideoBackendCapabilityProbe? capabilityProbe = null)
    {
        _codecProbe = codecProbe ?? FullVideoPlaybackExtensionLoader.CreateCodecProbe();
        _ = capabilityProbe;
    }

    internal Task ReleaseProbeResourcesAsync() => _codecProbe is IVideoCodecProbeResourceOwner owner ? owner.ReleaseResourcesAsync() : Task.CompletedTask;
    internal Task TransferProbeResourcesAsync(IVideoPlaybackBackend backend, CancellationToken cancel)
        => _codecProbe is IVideoCodecProbeResourceOwner owner ? owner.TransferResourcesAsync(backend, cancel) : Task.CompletedTask;

    internal Task<VideoBackendSelection> SelectAsync(string requestedMode, string path, CancellationToken cancel)
    {
        string mode = VideoPlaybackBackendKind.Normalize(requestedMode);
        cancel.ThrowIfCancellationRequested();
        if (!FullVideoPlaybackExtensionLoader.IsAvailable)
            return Task.FromResult(VideoBackendSelection.MediaFoundationOnly(mode));
        return Task.FromResult(mode == VideoPlaybackBackendKind.Auto
            ? VideoBackendSelection.DirectAutoLibVlc()
            : VideoBackendSelection.Manual(mode));
    }
}
