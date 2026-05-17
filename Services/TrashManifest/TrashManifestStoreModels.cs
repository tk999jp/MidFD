namespace MidFD.Services.TrashManifestStore;

internal sealed class TrashManifest
{
    public List<TrashManifestRecord> Records { get; set; } = new();
}

internal sealed class TrashManifestRecord
{
    public string BatchId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string TrashPath { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public DateTime DeletedAtUtc { get; set; }
    public TrashRecordStatus Status { get; set; } = TrashRecordStatus.InTrash;
}

internal enum TrashRecordStatus
{
    InTrash,
    Restored
}
