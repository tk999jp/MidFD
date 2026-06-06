namespace MidFD.Models;

public enum FileOperationUndoRedoOperation
{
    Rename,
    Move,
    DeleteToMidFdTrash,
    CreateFromPaste
}

public sealed class FileOperationUndoRedoItem
{
    public string BeforePath { get; init; } = string.Empty;
    public string AfterPath { get; init; } = string.Empty;
    public string BeforeName { get; init; } = string.Empty;
    public string AfterName { get; init; } = string.Empty;
    public string RecycleBinPath { get; init; } = string.Empty;
    public DateTime RecycleBinDeletedAtUtc { get; init; } = DateTime.MinValue;
    public long CreatedFileLength { get; init; } = -1;
    public long CreatedFileLastWriteTimeUtcTicks { get; init; } = 0;
}

public sealed class FileOperationUndoRedoBatch
{
    public FileOperationUndoRedoOperation Operation { get; init; } = FileOperationUndoRedoOperation.Rename;
    public IReadOnlyList<FileOperationUndoRedoItem> Items { get; set; } = Array.Empty<FileOperationUndoRedoItem>();
    public bool IsPartialCancellation { get; set; }
}
