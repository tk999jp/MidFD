namespace MidFD.Services;

public sealed class PreviewRoutingResult
{
    public PreviewKind RawKind { get; init; }
    public PreviewKind EffectiveKind { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public static class PreviewRoutingService
{
    public static PreviewRoutingResult Route(string path, string? videoToolDirectory)
    {
        PreviewKind rawKind = PreviewService.GetPreviewKind(path);
        return Route(path, rawKind, videoToolDirectory);
    }

    public static PreviewRoutingResult Route(string path, PreviewKind rawKind, string? videoToolDirectory)
    {
        if (rawKind == PreviewKind.Video)
        {
            var res = VideoToolResolutionService.Resolve(videoToolDirectory);
            if (!res.FfmpegFound)
            {
                return new PreviewRoutingResult
                {
                    RawKind = rawKind,
                    EffectiveKind = PreviewKind.Binary,
                    Reason = "ffmpeg-unavailable-video-binary-fallback"
                };
            }
        }

        return new PreviewRoutingResult
        {
            RawKind = rawKind,
            EffectiveKind = rawKind,
            Reason = "default-mapping"
        };
    }
}
