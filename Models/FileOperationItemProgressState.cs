namespace MidFD.Models;

public enum FileOperationItemProgressKind
{
    Copy,
    Move,
    Delete,
    Other
}

public sealed record FileOperationItemProgressState(
    FileOperationItemProgressKind OperationKind,
    int CurrentItems,
    int TotalItems,
    bool IsIndeterminate,
    bool IsActive);
