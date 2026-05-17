namespace MidFD.Models;

public enum DirectoryPasteMergeAbortReason
{
    None,
    TypeMismatch,
    DifferentRoot,
    NestedFileCollision,
    PartialStateRisk,
    CopyMergeDeferred,
    MoveMergeDeferred
}
