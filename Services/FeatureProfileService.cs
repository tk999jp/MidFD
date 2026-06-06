using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Services;

public static class FeatureProfileService
{
    public static bool TryResolveProfile(string? profileValue, out FeatureProfile profile)
    {
        if (Enum.TryParse(profileValue, ignoreCase: true, out FeatureProfile parsed))
        {
            profile = parsed;
            return true;
        }

        profile = FeatureProfile.PracticalStable;
        return false;
    }

    public static FeatureProfile ResolveProfile(string? profileValue)
    {
        if (TryResolveProfile(profileValue, out FeatureProfile parsed))
        {
            return parsed;
        }

        return FeatureProfile.Full;
    }

    public static FeatureProfile ResolveRuntimeProfile(string? startupOverride, string? settingsProfile, FeatureProfile fallbackProfile = FeatureProfile.PracticalStable)
    {
        if (TryResolveProfile(startupOverride, out FeatureProfile startupProfile))
        {
            return startupProfile;
        }

        if (TryResolveProfile(settingsProfile, out FeatureProfile settingsResolvedProfile))
        {
            return settingsResolvedProfile;
        }

        return fallbackProfile;
    }

    public static string ToSettingValue(FeatureProfile profile) => profile.ToString();

    public static string ToDisplayName(FeatureProfile profile)
    {
        return profile switch
        {
            FeatureProfile.PracticalStable => "標準機能（推奨）",
            FeatureProfile.MinimalCore => "最小構成（内部用）",
            _ => "拡張機能"
        };
    }

    public static void ApplyRuntimeProfile(AppSettings settings, FeatureProfile profile, bool isMouseGestureExplicit)
    {
        settings.Input ??= new InputSettings();
        settings.Profile = ToSettingValue(profile);

        if (profile == FeatureProfile.PracticalStable && !isMouseGestureExplicit)
        {
            settings.Input.EnableMouseGestures = false;
        }
    }
}
