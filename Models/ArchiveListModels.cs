namespace MidFD.Models;

public sealed class ArchiveEntry
{
    public string EntryPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public DateTime? ModifiedAt { get; init; }
}

public sealed class ArchiveListResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SevenZipPath { get; init; }
    public IReadOnlyList<ArchiveEntry> Entries { get; init; } = Array.Empty<ArchiveEntry>();
}

public sealed class ArchiveExtractRequest
{
    public string ArchivePath { get; init; } = string.Empty;
    public string DestinationDirectory { get; init; } = string.Empty;
    public IReadOnlyList<string> EntryPaths { get; init; } = Array.Empty<string>();
    public bool ExtractAll { get; init; }
}

public sealed class ArchiveExtractDestinationOptions
{
    public string BaseDirectory { get; init; } = string.Empty;
    public bool CreateArchiveRootDirectory { get; init; }
}

public sealed class ArchiveExtractResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SevenZipPath { get; init; }
    public string DestinationDirectory { get; init; } = string.Empty;
    public int ExtractedEntryCount { get; init; }
}
