using System.Text.Json.Serialization;

namespace MidFD.FileOperationHelperProtocol;

public sealed class ElevatedLinkCopyRequest
{
    public int ProtocolVersion { get; init; }
    public string OperationId { get; init; } = string.Empty;
    public int ParentProcessId { get; init; }
    public List<ElevatedLinkCopyItem> Items { get; init; } = new();
}

public sealed class ElevatedLinkCopyItem
{
    public string ItemId { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public string ExpectedKind { get; init; } = string.Empty;
}

public sealed class ElevatedLinkCopyResponse
{
    public int ProtocolVersion { get; init; }
    public string OperationId { get; init; } = string.Empty;
    public List<ElevatedLinkCopyResult> Results { get; init; } = new();
}

public sealed class ElevatedLinkCopyResult
{
    public string ItemId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int NativeErrorCode { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public string ResultingKind { get; init; } = string.Empty;
    public string? LinkTarget { get; init; }
}
