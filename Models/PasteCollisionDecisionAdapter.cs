using MidFD.Services;

namespace MidFD.Models;

public static class PasteCollisionDecisionAdapter
{
    public static CopyCollisionDecision ToCopyCollisionDecision(PasteCollisionDialogResult result, string destPath)
    {
        var policy = result.Action switch
        {
            PasteCollisionAction.NewerOnly => CopyCollisionPolicy.NewerOnly,
            PasteCollisionAction.RenameCopy => CopyCollisionPolicy.RenameCopy,
            PasteCollisionAction.Overwrite => CopyCollisionPolicy.Overwrite,
            PasteCollisionAction.Skip => CopyCollisionPolicy.Skip,
            _ => CopyCollisionPolicy.Cancel
        };

        return new CopyCollisionDecision
        {
            Policy = policy,
            ApplyToAll = result.ApplyToAll,
            ResolvedTargetPath = policy == CopyCollisionPolicy.RenameCopy
                ? FileOperationService.GetUniquePathStartingAtOne(destPath)
                : null
        };
    }
}
