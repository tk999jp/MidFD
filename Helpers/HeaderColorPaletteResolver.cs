using System.Drawing;
using MidFD.Configuration;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

public static class HeaderColorPaletteResolver
{
    public static HeaderColorPalette Resolve(AppearanceSettings? appearance)
    {
        string canonicalPreset = FileListColorResolver.CanonicalizePresetKey(appearance?.ColorTheme);
        var resolvedUiColors = UiThemeResolver.Resolve(appearance);
        bool useCustomUiTheme = appearance?.CustomUiThemeColorsEnabled == true;

        return canonicalPreset switch
        {
            "Green" => new HeaderColorPalette
            {
                HeaderTitleFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Lime,
                HeaderClockFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Lime,
                HeaderRow2Fore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Lime,
                HeaderRow2Value = Color.White,
                HeaderPathFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Lime,
                HeaderMetaFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Lime,
                HeaderNameFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.LightGreen
            },
            "Amber" => new HeaderColorPalette
            {
                HeaderTitleFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.FromArgb(255, 220, 120),
                HeaderClockFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.FromArgb(255, 220, 120),
                HeaderRow2Fore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.FromArgb(255, 190, 80),
                HeaderRow2Value = Color.White,
                HeaderPathFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.FromArgb(255, 210, 120),
                HeaderMetaFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.FromArgb(255, 210, 120),
                HeaderNameFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.FromArgb(255, 235, 180)
            },
            "Light" => new HeaderColorPalette
            {
                HeaderTitleFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Black,
                HeaderClockFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Black,
                HeaderRow2Fore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.FromArgb(80, 80, 80),
                HeaderRow2Value = Color.Black,
                HeaderPathFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Black,
                HeaderMetaFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.FromArgb(80, 80, 80),
                HeaderNameFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Black
            },
            "Slate" or "Mono Dark" or "Cyber" or "Violet" or "Sepia" => new HeaderColorPalette
            {
                HeaderTitleFore = resolvedUiColors.ChromeForeColor,
                HeaderClockFore = resolvedUiColors.ChromeForeColor,
                HeaderRow2Fore = resolvedUiColors.ChromeForeColor,
                HeaderRow2Value = Color.White,
                HeaderPathFore = resolvedUiColors.ChromeForeColor,
                HeaderMetaFore = resolvedUiColors.ChromeForeColor,
                HeaderNameFore = resolvedUiColors.ChromeForeColor
            },
            _ => new HeaderColorPalette
            {
                HeaderTitleFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Yellow,
                HeaderClockFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Yellow,
                HeaderRow2Fore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Cyan,
                HeaderRow2Value = Color.White,
                HeaderPathFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Cyan,
                HeaderMetaFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.Cyan,
                HeaderNameFore = useCustomUiTheme ? resolvedUiColors.ChromeForeColor : Color.LightCyan
            }
        };
    }
}
