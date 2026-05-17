namespace MidFD.Models;

public enum DirectoryMoveMergeAbortReason
{
    None,
    DifferentRoot,
    NestedFileCollision,
    TypeMismatch,
    PartialStateRisk
}
