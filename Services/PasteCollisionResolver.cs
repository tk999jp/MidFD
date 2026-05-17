using MidFD.Dialogs;
using MidFD.Models;

namespace MidFD.Services;

public sealed class PasteCollisionResolution
{
    public CopyCollisionDecision Decision { get; init; } = new();
    public string DestinationPath { get; init; } = string.Empty;
    public bool OverwriteExisting { get; init; }
    public bool ShouldSkip { get; init; }
    public bool ShouldCancel { get; init; }
    public bool UsedRenameCopy { get; init; }
    public string? RenameTargetName { get; init; }
}

public static class PasteCollisionResolver
{
    public static PasteCollisionResolution Resolve(
        IWin32Window owner,
        string sourcePath,
        string destPath,
        bool allowRename,
        bool isCut,
        ref CopyCollisionDecision? applyToAllDecision)
    {
        CopyCollisionDecision decision;
        if (applyToAllDecision is not null)
        {
            decision = applyToAllDecision;
        }
        else
        {
            string? renamePreviewName = allowRename
                ? Path.GetFileName(FileOperationService.GetUniquePathStartingAtOne(destPath))
                : null;
            var dialogResult = PasteCollisionDialog.Show(owner, Path.GetFileName(destPath), renamePreviewName, allowRename, isCut);
            decision = PasteCollisionDecisionAdapter.ToCopyCollisionDecision(dialogResult, destPath);
            if (decision.ApplyToAll && decision.Policy != CopyCollisionPolicy.Cancel)
            {
                applyToAllDecision = new CopyCollisionDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }

        var resolvedPath = decision.Policy == CopyCollisionPolicy.RenameCopy
            ? decision.ResolvedTargetPath ?? FileOperationService.GetUniquePathStartingAtOne(destPath)
            : destPath;

        return decision.Policy switch
        {
            CopyCollisionPolicy.Cancel => new PasteCollisionResolution
            {
                Decision = decision,
                DestinationPath = destPath,
                ShouldCancel = true
            },
            CopyCollisionPolicy.Skip => new PasteCollisionResolution
            {
                Decision = decision,
                DestinationPath = destPath,
                ShouldSkip = true
            },
            CopyCollisionPolicy.RenameCopy => new PasteCollisionResolution
            {
                Decision = decision,
                DestinationPath = resolvedPath,
                UsedRenameCopy = true,
                RenameTargetName = Path.GetFileName(resolvedPath)
            },
            CopyCollisionPolicy.Overwrite => new PasteCollisionResolution
            {
                Decision = decision,
                DestinationPath = destPath,
                OverwriteExisting = true
            },
            CopyCollisionPolicy.NewerOnly => new PasteCollisionResolution
            {
                Decision = decision,
                DestinationPath = destPath,
                ShouldSkip = File.GetLastWriteTimeUtc(sourcePath) <= File.GetLastWriteTimeUtc(destPath),
                OverwriteExisting = File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(destPath)
            },
            _ => new PasteCollisionResolution
            {
                Decision = new CopyCollisionDecision { Policy = CopyCollisionPolicy.Cancel },
                DestinationPath = destPath,
                ShouldCancel = true
            }
        };
    }
}
