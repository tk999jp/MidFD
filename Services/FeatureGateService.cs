using MidFD.Models;

namespace MidFD.Services;

public sealed class FeatureGateService
{
    private readonly bool? _workspaceSnapshotEnabledOverride;

    public FeatureGateService(FeatureProfile profile, bool? workspaceSnapshotEnabledOverride = null)
    {
        Profile = profile;
        _workspaceSnapshotEnabledOverride = workspaceSnapshotEnabledOverride;
    }

    public FeatureProfile Profile { get; }

    public bool IsEnabled(FeatureId featureId)
    {
        if (featureId == FeatureId.WorkspaceSnapshot && _workspaceSnapshotEnabledOverride.HasValue)
        {
            return _workspaceSnapshotEnabledOverride.Value;
        }

        return Profile switch
        {
            FeatureProfile.Full => true,
            FeatureProfile.PracticalStable => IsPracticalStableEnabled(featureId),
            FeatureProfile.MinimalCore => IsMinimalCoreEnabled(featureId),
            _ => true
        };
    }

    private static bool IsPracticalStableEnabled(FeatureId featureId)
    {
        return featureId switch
        {
            FeatureId.WorkspaceSnapshot => false,
            FeatureId.MarkSlotSetOperations => false,
            FeatureId.MarkSlotBackupTransfer => false,
            FeatureId.ImageQuantization => false,
            FeatureId.SvgClipboard => false,
            FeatureId.CommandPaletteUsage => false,
            FeatureId.FileSystemWatcherAutoRefresh => true,
            _ => true
        };
    }

    private static bool IsMinimalCoreEnabled(FeatureId featureId)
    {
        return featureId switch
        {
            FeatureId.WorkspaceSnapshot => false,
            FeatureId.MarkSlotSetOperations => false,
            FeatureId.MarkSlotBackupTransfer => false,
            FeatureId.ImageQuantization => false,
            FeatureId.SvgClipboard => false,
            FeatureId.CommandPaletteUsage => false,
            FeatureId.FileSystemWatcherAutoRefresh => true,
            _ => true
        };
    }
}
