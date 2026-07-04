using System;
using System.Collections.Generic;
using System.Drawing;
using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Services;

internal static class UiThemeResolver
{
    public static IReadOnlyList<string> PresetNames { get; } = new[]
    {
        "MidFD標準",
        "Terminal Green",
        "Amber",
        "Mono Dark",
        "Cyber",
        "Violet",
        "Sepia",
        "Classic Blue",
        "Light"
    };

    /// <summary>
    /// 一覧色プリセット名から UI テーマプリセット名へマップする。
    /// </summary>
    public static string MapFromDisplayColor(string? displayColorPreset)
    {
        if (displayColorPreset == null) return "MidFD標準";
        if (displayColorPreset.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0) return "Light";
        if (displayColorPreset.IndexOf("Mono Dark", StringComparison.OrdinalIgnoreCase) >= 0) return "Mono Dark";
        if (displayColorPreset.IndexOf("Cyber", StringComparison.OrdinalIgnoreCase) >= 0) return "Cyber";
        if (displayColorPreset.IndexOf("Violet", StringComparison.OrdinalIgnoreCase) >= 0) return "Violet";
        if (displayColorPreset.IndexOf("Sepia", StringComparison.OrdinalIgnoreCase) >= 0) return "Sepia";
        if (displayColorPreset.IndexOf("Green", StringComparison.OrdinalIgnoreCase) >= 0) return "Terminal Green";
        if (displayColorPreset.IndexOf("Amber", StringComparison.OrdinalIgnoreCase) >= 0) return "Amber";
        return "MidFD標準";
    }

    /// <summary>
    /// AppearanceSettings から UI クローム色を解決する。
    /// 一覧配色を基準に chrome/header/status を追従させ、viewer は従来テーマのまま維持する。
    /// </summary>
    public static UiThemeColors Resolve(AppearanceSettings? appearance)
    {
        string presetName = MapFromDisplayColor(appearance?.ColorTheme);
        var baseColors = Resolve(presetName);

        if (appearance == null)
        {
            return baseColors;
        }

        var tempSettings = new AppSettings
        {
            Appearance = appearance.Clone()
        };
        var listColors = FileListColorResolver.ResolveColors(tempSettings);
        string canonicalPreset = FileListColorResolver.CanonicalizePresetKey(appearance.ColorTheme);

        if (string.Equals(canonicalPreset, "MidFdStandard", StringComparison.OrdinalIgnoreCase))
        {
            baseColors = new UiThemeColors
            {
                ChromeBackColor = baseColors.ChromeBackColor,
                ChromeForeColor = baseColors.ChromeForeColor,
                AccentColor = baseColors.AccentColor,
                HeaderBackColor = baseColors.HeaderBackColor,
                HeaderForeColor = baseColors.HeaderForeColor,
                StatusBackColor = baseColors.StatusBackColor,
                StatusForeColor = baseColors.StatusForeColor,
                ViewerBackColor = baseColors.ViewerBackColor,
                ViewerForeColor = baseColors.ViewerForeColor,
                ViewerStatusBackColor = baseColors.ViewerStatusBackColor,
                ViewerStatusForeColor = baseColors.ViewerStatusForeColor,
                BorderColor = baseColors.BorderColor,
                SeparatorColor = baseColors.SeparatorColor
            };

            if (appearance.CustomUiThemeColorsEnabled)
            {
                baseColors = ApplyCustomColors(baseColors, appearance);
            }

            return baseColors;
        }

        baseColors = new UiThemeColors
        {
            ChromeBackColor = listColors.Background,
            ChromeForeColor = listColors.NormalFile,
            AccentColor = listColors.NormalFile,
            HeaderBackColor = listColors.Background,
            HeaderForeColor = listColors.NormalFile,
            StatusBackColor = listColors.Background,
            StatusForeColor = listColors.NormalFile,
            ViewerBackColor = baseColors.ViewerBackColor,
            ViewerForeColor = baseColors.ViewerForeColor,
            ViewerStatusBackColor = baseColors.ViewerStatusBackColor,
            ViewerStatusForeColor = baseColors.ViewerStatusForeColor,
            BorderColor = baseColors.BorderColor,
            SeparatorColor = listColors.NormalFile
        };

        // 手動指定色が有効な場合、ファイラー/ビューア色を上書きする
        if (appearance?.CustomUiThemeColorsEnabled == true)
        {
            baseColors = ApplyCustomColors(baseColors, appearance);
        }

        return baseColors;
    }

    private static UiThemeColors ApplyCustomColors(UiThemeColors base_, AppearanceSettings a)
    {
        Color filerBack = TryParseColor(a.CustomFilerBackColor) ?? base_.ChromeBackColor;
        Color filerFore = TryParseColor(a.CustomFilerForeColor) ?? base_.ChromeForeColor;
        Color viewerBack = TryParseColor(a.CustomViewerBackColor) ?? base_.ViewerBackColor;
        Color viewerFore = TryParseColor(a.CustomViewerForeColor) ?? base_.ViewerForeColor;

        return new UiThemeColors
        {
            // ファイラー系（メニュー/ヘッダ/ステータス）に手動指定を反映
            ChromeBackColor = filerBack,
            ChromeForeColor = filerFore,
            AccentColor = base_.AccentColor,
            HeaderBackColor = filerBack,
            HeaderForeColor = filerFore,
            StatusBackColor = filerBack,
            StatusForeColor = filerFore,
            // ビューア系に手動指定を反映
            ViewerBackColor = viewerBack,
            ViewerForeColor = viewerFore,
            ViewerStatusBackColor = viewerBack,
            ViewerStatusForeColor = viewerFore,
            // 線系はベーステーマから引き継ぐ
            BorderColor = base_.BorderColor,
            SeparatorColor = base_.SeparatorColor
        };
    }

    public static Color? TryParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            string s = hex.TrimStart('#');
            if (s.Length == 6)
            {
                int r = Convert.ToInt32(s[0..2], 16);
                int g = Convert.ToInt32(s[2..4], 16);
                int b = Convert.ToInt32(s[4..6], 16);
                return Color.FromArgb(r, g, b);
            }
        }
        catch { }
        return null;
    }

    public static string ToHexString(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static UiThemeColors Resolve(string? presetName)
    {
        return presetName switch
        {
            "Terminal Green" => new UiThemeColors
            {
                ChromeBackColor = Color.Black,
                ChromeForeColor = Color.Lime,
                AccentColor = Color.Lime,
                HeaderBackColor = Color.Black,
                HeaderForeColor = Color.Lime,
                StatusBackColor = Color.Black,
                StatusForeColor = Color.Lime,
                ViewerBackColor = Color.FromArgb(0, 32, 0),
                ViewerForeColor = Color.White,
                ViewerStatusBackColor = Color.Black,
                ViewerStatusForeColor = Color.Lime,
                BorderColor = Color.Lime,
                SeparatorColor = Color.FromArgb(0, 96, 0)
            },
            "Amber" => new UiThemeColors
            {
                ChromeBackColor = Color.FromArgb(24, 18, 0),
                ChromeForeColor = Color.FromArgb(255, 210, 120),
                AccentColor = Color.FromArgb(255, 210, 120),
                HeaderBackColor = Color.FromArgb(24, 18, 0),
                HeaderForeColor = Color.FromArgb(255, 210, 120),
                StatusBackColor = Color.FromArgb(24, 18, 0),
                StatusForeColor = Color.FromArgb(255, 210, 120),
                ViewerBackColor = Color.FromArgb(48, 32, 0),
                ViewerForeColor = Color.White,
                ViewerStatusBackColor = Color.FromArgb(24, 18, 0),
                ViewerStatusForeColor = Color.FromArgb(255, 210, 120),
                BorderColor = Color.FromArgb(220, 220, 220),
                SeparatorColor = Color.FromArgb(130, 84, 0)
            },
            "Mono Dark" => new UiThemeColors
            {
                ChromeBackColor = Color.FromArgb(18, 18, 18),
                ChromeForeColor = Color.FromArgb(214, 214, 214),
                AccentColor = Color.FromArgb(176, 176, 176),
                HeaderBackColor = Color.FromArgb(24, 24, 24),
                HeaderForeColor = Color.FromArgb(214, 214, 214),
                StatusBackColor = Color.FromArgb(18, 18, 18),
                StatusForeColor = Color.FromArgb(214, 214, 214),
                ViewerBackColor = Color.FromArgb(28, 28, 28),
                ViewerForeColor = Color.FromArgb(236, 236, 236),
                ViewerStatusBackColor = Color.FromArgb(18, 18, 18),
                ViewerStatusForeColor = Color.FromArgb(214, 214, 214),
                BorderColor = Color.FromArgb(96, 96, 96),
                SeparatorColor = Color.FromArgb(64, 64, 64)
            },
            "Cyber" => new UiThemeColors
            {
                ChromeBackColor = Color.FromArgb(16, 22, 36),
                ChromeForeColor = Color.FromArgb(214, 246, 255),
                AccentColor = Color.FromArgb(72, 240, 255),
                HeaderBackColor = Color.FromArgb(22, 28, 44),
                HeaderForeColor = Color.FromArgb(214, 246, 255),
                StatusBackColor = Color.FromArgb(16, 22, 36),
                StatusForeColor = Color.FromArgb(214, 246, 255),
                ViewerBackColor = Color.FromArgb(30, 18, 44),
                ViewerForeColor = Color.FromArgb(238, 246, 255),
                ViewerStatusBackColor = Color.FromArgb(16, 22, 36),
                ViewerStatusForeColor = Color.FromArgb(214, 246, 255),
                BorderColor = Color.FromArgb(72, 240, 255),
                SeparatorColor = Color.FromArgb(142, 56, 188)
            },
            "Violet" => new UiThemeColors
            {
                ChromeBackColor = Color.FromArgb(24, 16, 36),
                ChromeForeColor = Color.FromArgb(220, 208, 248),
                AccentColor = Color.FromArgb(184, 144, 255),
                HeaderBackColor = Color.FromArgb(28, 20, 42),
                HeaderForeColor = Color.FromArgb(220, 208, 248),
                StatusBackColor = Color.FromArgb(24, 16, 36),
                StatusForeColor = Color.FromArgb(220, 208, 248),
                ViewerBackColor = Color.FromArgb(32, 20, 48),
                ViewerForeColor = Color.FromArgb(240, 236, 252),
                ViewerStatusBackColor = Color.FromArgb(24, 16, 36),
                ViewerStatusForeColor = Color.FromArgb(220, 208, 248),
                BorderColor = Color.FromArgb(132, 108, 190),
                SeparatorColor = Color.FromArgb(86, 68, 124)
            },
            "Sepia" => new UiThemeColors
            {
                ChromeBackColor = Color.FromArgb(38, 30, 22),
                ChromeForeColor = Color.FromArgb(224, 206, 178),
                AccentColor = Color.FromArgb(196, 168, 124),
                HeaderBackColor = Color.FromArgb(44, 34, 24),
                HeaderForeColor = Color.FromArgb(224, 206, 178),
                StatusBackColor = Color.FromArgb(38, 30, 22),
                StatusForeColor = Color.FromArgb(224, 206, 178),
                ViewerBackColor = Color.FromArgb(52, 40, 28),
                ViewerForeColor = Color.FromArgb(244, 236, 220),
                ViewerStatusBackColor = Color.FromArgb(38, 30, 22),
                ViewerStatusForeColor = Color.FromArgb(224, 206, 178),
                BorderColor = Color.FromArgb(152, 124, 88),
                SeparatorColor = Color.FromArgb(96, 74, 48)
            },
            "Classic Blue" => new UiThemeColors
            {
                ChromeBackColor = Color.FromArgb(0, 0, 96),
                ChromeForeColor = Color.Cyan,
                AccentColor = Color.Cyan,
                HeaderBackColor = Color.FromArgb(0, 0, 80),
                HeaderForeColor = Color.Cyan,
                StatusBackColor = Color.FromArgb(0, 0, 64),
                StatusForeColor = Color.Cyan,
                ViewerBackColor = Color.FromArgb(0, 0, 80),
                ViewerForeColor = Color.White,
                ViewerStatusBackColor = Color.FromArgb(0, 0, 64),
                ViewerStatusForeColor = Color.Cyan,
                BorderColor = Color.FromArgb(0, 200, 200),
                SeparatorColor = Color.FromArgb(0, 120, 140)
            },
            "Light" => new UiThemeColors
            {
                ChromeBackColor = Color.FromArgb(245, 245, 245),
                ChromeForeColor = Color.Black,
                AccentColor = Color.Blue,
                HeaderBackColor = Color.FromArgb(240, 240, 240),
                HeaderForeColor = Color.Black,
                StatusBackColor = Color.FromArgb(245, 245, 245),
                StatusForeColor = Color.Black,
                ViewerBackColor = Color.White,
                ViewerForeColor = Color.Black,
                ViewerStatusBackColor = Color.FromArgb(240, 240, 240),
                ViewerStatusForeColor = Color.Black,
                BorderColor = Color.FromArgb(220, 220, 220),
                SeparatorColor = Color.FromArgb(200, 200, 200)
            },
            // "MidFD標準" またはデフォルト: 黒+シアン基調（従来MidFD寄り）
            _ => new UiThemeColors
            {
                ChromeBackColor = Color.FromArgb(16, 20, 28),
                ChromeForeColor = Color.Cyan,
                AccentColor = Color.Cyan,
                HeaderBackColor = Color.FromArgb(20, 24, 32),
                HeaderForeColor = Color.Cyan,
                StatusBackColor = Color.FromArgb(16, 20, 28),
                StatusForeColor = Color.Cyan,
                ViewerBackColor = Color.FromArgb(0, 0, 64),
                ViewerForeColor = Color.FromArgb(200, 220, 255),
                ViewerStatusBackColor = Color.FromArgb(0, 0, 40),
                ViewerStatusForeColor = Color.Cyan,
                BorderColor = Color.FromArgb(0, 160, 180),
                SeparatorColor = Color.FromArgb(0, 100, 120)
            }
        };
    }
}
