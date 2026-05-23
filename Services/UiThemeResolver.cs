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
        "MidFD Default",
        "Terminal Green",
        "Amber",
        "Classic Blue",
        "Light"
    };

    /// <summary>
    /// 一覧色プリセット名から UI テーマプリセット名へマップする。
    /// ClassicCyan は MidFD Default（黒+シアン基調）へ解決する。
    /// 青ベタ背景（Classic Blue）は ClassicCyan の連動先にしない。
    /// </summary>
    public static string MapFromDisplayColor(string? displayColorPreset)
    {
        if (displayColorPreset == null) return "MidFD Default";
        if (displayColorPreset.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0) return "Light";
        if (displayColorPreset.IndexOf("Green", StringComparison.OrdinalIgnoreCase) >= 0) return "Terminal Green";
        if (displayColorPreset.IndexOf("Amber", StringComparison.OrdinalIgnoreCase) >= 0) return "Amber";
        // ClassicCyan / MidFD Classic Cyan などは MidFD Default（黒+シアン）へ
        return "MidFD Default";
    }

    /// <summary>
    /// AppearanceSettings から UI テーマ色を解決する。優先順位:
    ///   1. ColorTheme から UI 基調色を自動解決
    ///   2. CustomUiThemeColorsEnabled == true の場合のみ手動指定色で上書き
    /// </summary>
    public static UiThemeColors Resolve(AppearanceSettings? appearance)
    {
        string presetName = MapFromDisplayColor(appearance?.ColorTheme);
        var baseColors = Resolve(presetName);

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
            // Classic Blue: 青背景/レトロブルー系（旧MidFD Classic Cyan相当）
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
            // "MidFD Default" またはデフォルト: 黒+シアン基調（従来MidFD寄り）
            // ClassicCyan連動時もここへ解決される
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
