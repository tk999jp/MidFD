using MidFD.Configuration;

namespace MidFD;

public sealed class ImageViewerSettings
{
    public bool ReuseOnUserClose { get; set; } = true;
    public int InitialFitLimitWidth { get; set; } = 1920;
    public int InitialFitLimitHeight { get; set; } = 1080;
    public int VideoSkipSeconds { get; set; }
    private int _videoSeekStepSeconds = 10;
    public int VideoSeekStepSeconds
    {
        get => _videoSeekStepSeconds;
        set => _videoSeekStepSeconds = Math.Clamp(value, 1, 600);
    }
    public int VideoPlaybackVolumePercent { get; set; } = 100;
    private string _videoPlaybackBackend = VideoPlaybackBackendKind.Auto;
    [System.Text.Json.Serialization.JsonConverter(typeof(VideoPlaybackBackendNameConverter))]
    public string VideoPlaybackBackend
    {
        get => _videoPlaybackBackend;
        set => _videoPlaybackBackend = VideoPlaybackBackendKind.Normalize(value);
    }
    public SusiePluginSettings SusiePlugins { get; set; } = new();

    public static ImageViewerSettings FromJson(string json)
        => System.Text.Json.JsonSerializer.Deserialize<ImageViewerSettings>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new System.Text.Json.JsonException("Viewer settings payload was empty.");

    public ImageViewerSettings Clone()
    {
        var clone = (ImageViewerSettings)MemberwiseClone();
        clone.SusiePlugins = (SusiePlugins ?? new SusiePluginSettings()).Clone();
        return clone;
    }
}
