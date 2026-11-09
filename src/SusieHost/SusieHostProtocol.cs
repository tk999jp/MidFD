namespace MidFD;

internal sealed class SusieHostMessage
{
    public int ProtocolVersion { get; init; } = 1;
    public string Type { get; init; } = string.Empty;
    public string? RequestId { get; init; }
    public long InputHandle { get; init; }
    public long InputLength { get; init; }
    public string? PathHint { get; init; }
    public string? FallbackPath { get; init; }
    public long OutputHandle { get; init; }
    public bool Success { get; init; }
    public string? Status { get; init; }
    public int? PluginErrorCode { get; init; }
    public bool? OutputHandlesReleased { get; init; }
    public string? Detail { get; init; }
    public string? DisplayName { get; init; }
    public string? VersionInfo { get; init; }
    public string[] Extensions { get; init; } = Array.Empty<string>();
    public string[] FormatNames { get; init; } = Array.Empty<string>();
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? OutputLength { get; init; }
    public bool? Is64BitProcess { get; init; }
    public int? ProcessId { get; init; }
}
