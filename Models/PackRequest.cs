namespace MidFD.Models;

public enum PackArchiveFormat
{
    Zip,
    SevenZip,
    Tar,
    GZip,
    BZip2,
    Xz,
    Wim
}

public enum PackCompressionLevel
{
    Store,
    Fast,
    Normal,
    Maximum
}

public sealed class PackRequest
{
    public string OutputArchivePath { get; init; } = string.Empty;
    public PackArchiveFormat Format { get; init; } = PackArchiveFormat.Zip;
    public PackCompressionLevel CompressionLevel { get; init; } = PackCompressionLevel.Normal;
    public string? SplitSize { get; init; }
    public bool PackEachFolderIndividually { get; init; }
}
