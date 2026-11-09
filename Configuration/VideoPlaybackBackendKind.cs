using System.Text.Json;
using System.Text.Json.Serialization;

namespace MidFD.Configuration;

// Strings keep settings/IPC compatible with missing and future backend names.
public static class VideoPlaybackBackendKind
{
    public const string Auto = "Auto";
    public const string LibVLC = "LibVLC";
    public const string MediaFoundation = "MediaFoundation";
    public static string Normalize(string? value)
        => string.Equals(value, MediaFoundation, StringComparison.OrdinalIgnoreCase) ? MediaFoundation
            : string.Equals(value, LibVLC, StringComparison.OrdinalIgnoreCase) ? LibVLC : Auto;
}

public sealed class VideoPlaybackBackendNameConverter : JsonConverter<string>
{
    public override bool HandleNull => true;
    public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return VideoPlaybackBackendKind.Normalize(reader.GetString());
        reader.Skip();
        return VideoPlaybackBackendKind.Auto;
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(VideoPlaybackBackendKind.Normalize(value));
}
