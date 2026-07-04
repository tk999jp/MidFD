using System.Drawing;
using MidFD.Configuration;
using MidFD.Services;

namespace MidFD.Helpers;

public static class StatusColorResolver
{
    public static Color Resolve(
        StatusKind kind,
        FileListColorResolver.ResolvedColors? resolvedColors,
        AppearanceSettings? appearance)
    {
        FileListColorResolver.ResolvedColors colors = resolvedColors
            ?? FileListColorResolver.ResolvePresetColors(
                appearance?.ColorTheme,
                appearance?.CustomFileListColorPresets);

        return kind switch
        {
            StatusKind.Result => colors.StatusResult,
            StatusKind.Error => colors.StatusError,
            _ => colors.StatusNormal
        };
    }
}
