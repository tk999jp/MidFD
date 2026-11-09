namespace MidFD.Services;

public sealed class PreviewRoutingResult
{
    public PreviewKind RawKind { get; init; }
    public PreviewKind EffectiveKind { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public static class PreviewRoutingService
{
    public static PreviewRoutingResult Route(string path)
    {
        PreviewKind rawKind = PreviewService.GetPreviewKind(path);
        return Route(path, rawKind);
    }

    public static PreviewRoutingResult Route(string path, string? videoToolDirectory)
        => Route(path);

    public static PreviewRoutingResult Route(string path, PreviewKind rawKind)
    {
        return new PreviewRoutingResult
        {
            RawKind = rawKind,
            EffectiveKind = rawKind,
            Reason = "default-mapping"
        };
    }

    public static PreviewRoutingResult Route(string path, PreviewKind rawKind, string? videoToolDirectory)
        => Route(path, rawKind);
}
