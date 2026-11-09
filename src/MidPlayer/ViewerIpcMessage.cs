using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MidFD.ViewerIpc;

public enum ViewerPresentationMode
{
    PreviewFollow,
    PlaybackLocked
}

public enum ViewerVideoPlaybackState
{
    Empty,
    Opening,
    Playing,
    Paused,
    Stopped,
    Ended,
    Error
}

public sealed class ViewerPresentationModeJsonConverter : JsonConverter<ViewerPresentationMode>
{
    public override bool HandleNull => true;

    public override ViewerPresentationMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse(reader.GetString(), ignoreCase: true, out ViewerPresentationMode parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }
        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out int numeric)
            && Enum.IsDefined((ViewerPresentationMode)numeric))
        {
            return (ViewerPresentationMode)numeric;
        }
        return ViewerPresentationMode.PreviewFollow;
    }

    public override void Write(Utf8JsonWriter writer, ViewerPresentationMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : ViewerPresentationMode.PreviewFollow.ToString());
}

public sealed class ViewerIpcMessage
{
    public const string VideoLoadModeStill = "Still";
    public const string VideoLoadModePlayback = "Playback";
    // Nonterminal user close; "close" continues to mean process shutdown.
    public const string TypeHide = "hide";
    public const string TypeHidden = "hidden";
    public const string TypeSetReusePolicy = "set-reuse-policy";
    public const string TypeUpdateSusieSettings = "update-susie-settings";
    public const string TypeSusieSettingsUpdateResult = "susie-settings-update-result";
    public const string TypePresentationModeApplied = "presentation-mode-applied";
    public const string TypeVideoPlaybackStateChanged = "video-playback-state";

    public string Type { get; init; } = string.Empty;
    public Guid ViewerId { get; init; }
    public string? Path { get; init; }
    public string? MediaKind { get; init; }
    public string? VideoLoadMode { get; init; }
    public string? VideoPlaybackBackend { get; init; }
    public ViewerPresentationMode? PresentationMode { get; init; }
    public ViewerVideoPlaybackState? VideoPlaybackState { get; init; }
    public int? PresentationGeneration { get; init; }
    public int? SelectionGeneration { get; init; }
    public bool? PresentationModeAccepted { get; init; }
    public bool? IsSelectionFollow { get; init; }
    public bool ShowErrorMessage { get; init; } = true;
    public string? VideoToolDirectory { get; init; }
    public int? VideoSeconds { get; init; }
    public int? VolumePercent { get; init; }
    public string? CommandId { get; init; }
    public string? Action { get; init; }
    public int? SeekSeconds { get; init; }
    public double? PlaybackRate { get; init; }
    public int? KeyData { get; init; }
    public bool? Success { get; init; }
    public bool? Cancelled { get; init; }
    public long? RequestId { get; init; }
    public long? VisibilityGeneration { get; init; }
    public long? ViewerWindowHandle { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? WindowState { get; init; }
    public bool? Visible { get; init; }
    public bool? ReuseOnUserClose { get; init; }
    public bool? AllowQuantization { get; init; }
    public bool? AllowSvgClipboard { get; init; }
    public string? SettingsJson { get; init; }
    public string? ErrorMessage { get; init; }
    public string? DiagnosticMessage { get; init; }
}

public static class ViewerIpcJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(ViewerIpcMessage message) => JsonSerializer.Serialize(message, Options);

    public static ViewerIpcMessage? Deserialize(string json) => JsonSerializer.Deserialize<ViewerIpcMessage>(json, Options);

    public static async Task WriteAsync(StreamWriter writer, ViewerIpcMessage message, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ViewerIpcMessage?> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line == null ? null : Deserialize(line);
    }
}
