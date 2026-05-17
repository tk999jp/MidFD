using MidFD.Models;

namespace MidFD.Services;

public sealed class FeatureGateService
{
    public FeatureGateService(FeatureProfile profile)
    {
        Profile = profile;
    }

    public FeatureProfile Profile { get; }

    public bool IsEnabled(FeatureId featureId)
    {
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
            FeatureId.FileSystemWatcherAutoRefresh => false,
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
            FeatureId.FileSystemWatcherAutoRefresh => false,
            _ => true
        };
    }
}
