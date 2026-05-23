using System;
using System.Drawing;
using System.Linq;
using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Services;

public class FileListColorResolver
{
    public static readonly string[] CoreThemes = { "ClassicCyan", "Green", "Amber", "Light" };
    public static readonly string[] BuiltInPresetKeys =
    {
        "ClassicCyan",
        "Green",
        "Amber",
        "Light",
        "WinFD Classic Dark",
        "WinFD Classic Light",
        "High Contrast Dark",
        "High Contrast Light",
        "Terminal Green",
        "Amber Contrast"
    };

    public class ResolvedColors
    {
        public Color Background { get; set; }
        public Color NormalFile { get; set; }
        public Color Directory { get; set; }
        public Color ReadOnly { get; set; }
        public Color Hidden { get; set; }
        public Color System { get; set; }
        public Color Marked { get; set; }
        public Color SelectedBackground { get; set; }
        public Color SelectedForeground { get; set; }
    }

    public static bool IsCoreTheme(string? themeKey)
    {
        return Array.Exists(CoreThemes, theme => string.Equals(theme, themeKey, StringComparison.Ordinal));
    }

    public static bool IsBuiltInPreset(string? presetKey)
    {
        return Array.Exists(BuiltInPresetKeys, preset => string.Equals(preset, presetKey, StringComparison.Ordinal));
    }

    public static string NormalizeCoreTheme(string? themeKey, AppSettings? settings = null)
    {
        if (string.Equals(themeKey, "Light", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(themeKey, "High Contrast Light", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(themeKey, "WinFD Classic Light", StringComparison.OrdinalIgnoreCase))
        {
            return "Light";
        }
        if (string.Equals(themeKey, "Green", StringComparison.OrdinalIgnoreCase))
        {
            return "Green";
        }
        if (string.Equals(themeKey, "Amber", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(themeKey, "Amber Contrast", StringComparison.OrdinalIgnoreCase))
        {
            return "Amber";
        }

        if (settings != null)
        {
            var app = settings.Appearance;
            var resolved = ResolvePresetColors(app.ColorTheme, app.CustomFileListColorPresets);
            if (app.UseCustomFileListColors && app.CustomFileListColors != null)
            {
                resolved = ApplyCustomColors(resolved, app.CustomFileListColors);
            }
            if (GetRelativeLuminance(resolved.Background) > 0.5)
            {
                return "Light";
            }
        }

        return "ClassicCyan";
    }

    public static string MakeUserPresetKey(string name) => $"USER:{name}";

    public static bool TryGetUserPresetName(string? presetKey, out string? userName)
    {
        if (!string.IsNullOrWhiteSpace(presetKey) && presetKey.StartsWith("USER:", StringComparison.Ordinal))
        {
            userName = presetKey["USER:".Length..];
            return true;
        }

        userName = null;
        return false;
    }

    public static ResolvedColors ResolveDefaultColors(string? themeKey)
    {
        var colors = new ResolvedColors();
        switch (NormalizeCoreTheme(themeKey))
        {
            case "Green":
                colors.Background = Color.Black;
                colors.NormalFile = Color.LightGreen;
                colors.Directory = Color.Lime;
                colors.ReadOnly = Color.Lime;
                colors.Hidden = Color.Blue;
                colors.System = Color.Magenta;
                colors.Marked = Color.White;
                colors.SelectedBackground = Color.FromArgb(0, 48, 0);
                colors.SelectedForeground = Color.FromArgb(240, 240, 224);
                break;
            case "Amber":
                colors.Background = Color.FromArgb(24, 18, 0);
                colors.NormalFile = Color.FromArgb(255, 235, 170);
                colors.Directory = Color.FromArgb(255, 220, 140);
                colors.ReadOnly = Color.Lime;
                colors.Hidden = Color.Blue;
                colors.System = Color.Magenta;
                colors.Marked = Color.FromArgb(240, 240, 224);
                colors.SelectedBackground = Color.FromArgb(92, 58, 0);
                colors.SelectedForeground = Color.FromArgb(255, 250, 240);
                break;
            case "Light":
                colors.Background = Color.White;
                colors.NormalFile = Color.Black;
                colors.Directory = Color.Black;
                colors.ReadOnly = Color.Green;
                colors.Hidden = Color.Blue;
                colors.System = Color.Magenta;
                colors.Marked = Color.Black;
                colors.SelectedBackground = Color.FromArgb(204, 232, 255);
                colors.SelectedForeground = Color.Black;
                break;
            default: // ClassicCyan
                colors.Background = Color.Black;
                colors.NormalFile = Color.Khaki;
                colors.Directory = Color.Cyan;
                colors.ReadOnly = Color.Lime;
                colors.Hidden = Color.Blue;
                colors.System = Color.Magenta;
                colors.Marked = Color.White;
                colors.SelectedBackground = Color.FromArgb(0, 64, 80);
                colors.SelectedForeground = Color.FromArgb(240, 240, 224);
                break;
        }
        return colors;
    }

    public static ResolvedColors ResolvePresetColors(string? presetKey, IReadOnlyList<CustomFileListColorPreset>? userPresets = null)
    {
        if (TryGetUserPresetName(presetKey, out string? userName) && userPresets != null)
        {
            var userPreset = userPresets.FirstOrDefault(preset => string.Equals(preset.Name, userName, StringComparison.OrdinalIgnoreCase));
            if (userPreset != null)
            {
                return ApplyCustomColors(ResolveDefaultColors("ClassicCyan"), userPreset.Colors);
            }
        }

        var colors = new ResolvedColors();
        switch (presetKey)
        {
            case "WinFD Classic Dark":
                colors.Background = Color.Black;
                colors.NormalFile = Color.White;
                colors.Directory = Color.Cyan;
                colors.ReadOnly = Color.Lime;
                colors.Hidden = Color.Gray;
                colors.System = Color.Red;
                colors.Marked = Color.White;
                colors.SelectedBackground = Color.FromArgb(0, 0, 128);
                colors.SelectedForeground = Color.White;
                break;
            case "WinFD Classic Light":
                colors.Background = Color.White;
                colors.NormalFile = Color.Black;
                colors.Directory = Color.Blue;
                colors.ReadOnly = Color.FromArgb(0, 128, 0);
                colors.Hidden = Color.Gray;
                colors.System = Color.FromArgb(128, 0, 128);
                colors.Marked = Color.Black;
                colors.SelectedBackground = Color.FromArgb(180, 200, 240);
                colors.SelectedForeground = Color.Black;
                break;
            case "High Contrast Dark":
                colors.Background = Color.Black;
                colors.NormalFile = Color.White;
                colors.Directory = Color.Yellow;
                colors.ReadOnly = Color.Lime;
                colors.Hidden = Color.Cyan;
                colors.System = Color.Magenta;
                colors.Marked = Color.White;
                colors.SelectedBackground = Color.FromArgb(0, 58, 112); // #003A70
                colors.SelectedForeground = Color.White;
                break;
            case "High Contrast Light":
                colors.Background = Color.White;
                colors.NormalFile = Color.Black;
                colors.Directory = Color.FromArgb(0, 0, 204); // #0000CC
                colors.ReadOnly = Color.FromArgb(0, 96, 0); // #006000
                colors.Hidden = Color.FromArgb(128, 0, 128); // #800080
                colors.System = Color.FromArgb(176, 0, 0); // #B00000
                colors.Marked = Color.Black;
                colors.SelectedBackground = Color.FromArgb(204, 232, 255); // #CCE8FF
                colors.SelectedForeground = Color.Black;
                break;
            case "Terminal Green":
                colors.Background = Color.Black;
                colors.NormalFile = Color.FromArgb(0, 220, 0);
                colors.Directory = Color.FromArgb(0, 255, 0);
                colors.ReadOnly = Color.FromArgb(0, 180, 0);
                colors.Hidden = Color.FromArgb(0, 100, 0);
                colors.System = Color.FromArgb(0, 150, 0);
                colors.Marked = Color.White;
                colors.SelectedBackground = Color.FromArgb(0, 60, 0);
                colors.SelectedForeground = Color.White;
                break;
            case "Amber Contrast":
                colors.Background = Color.Black;
                colors.NormalFile = Color.FromArgb(255, 190, 0);
                colors.Directory = Color.FromArgb(255, 215, 0);
                colors.ReadOnly = Color.FromArgb(218, 165, 32);
                colors.Hidden = Color.FromArgb(139, 101, 8);
                colors.System = Color.FromArgb(205, 133, 63);
                colors.Marked = Color.White;
                colors.SelectedBackground = Color.FromArgb(70, 40, 0);
                colors.SelectedForeground = Color.White;
                break;
            default:
                return ResolveDefaultColors(presetKey);
        }
        return colors;
    }

    public static string GetPresetDescription(string? presetKey)
    {
        return presetKey switch
        {
            "WinFD Classic Dark" => "黒背景にシアン/黄/緑/青/マゼンタを強く出す、WinFD寄りの高彩度配色です。",
            "WinFD Classic Light" => "白背景でも属性色を強く残す、WinFD寄りの明色配色です。",
            "High Contrast Dark" => "黒背景で識別性を優先し、選択行も読めるよう調整した配色です。",
            "High Contrast Light" => "白背景で黒文字中心、属性色は濃く、選択行も読めるよう調整した配色です。",
            "Terminal Green" => "黒背景に緑系を主役にした、端末風の配色です。",
            "Amber Contrast" => "黒背景にアンバー系を主体にした、高コントラスト配色です。",
            "Green" => "緑系主体の既存テーマです。",
            "Amber" => "アンバー系主体の既存テーマです。",
            "Light" => "白背景の既存テーマです。",
            _ => "既存のClassicCyanを基準にした標準配色です。"
        };
    }

    public static ResolvedColors ResolveColors(AppSettings settings)
    {
        var app = settings.Appearance;
        var resolved = ResolvePresetColors(app.ColorTheme, app.CustomFileListColorPresets);

        if (app.UseCustomFileListColors)
        {
            resolved = ApplyCustomColors(resolved, app.CustomFileListColors);

            if (app.EnableSemanticColorAssist)
            {
                // 自動補正は背景とほぼ同化する極端なケースに限定する。
                const double minimumSafetyRatio = 1.3;
                resolved.NormalFile = EnsureContrast(resolved.NormalFile, resolved.Background, minimumSafetyRatio);
                resolved.Directory = EnsureContrast(resolved.Directory, resolved.Background, minimumSafetyRatio);
                resolved.ReadOnly = EnsureContrast(resolved.ReadOnly, resolved.Background, minimumSafetyRatio);
                resolved.Hidden = EnsureContrast(resolved.Hidden, resolved.Background, minimumSafetyRatio);
                resolved.System = EnsureContrast(resolved.System, resolved.Background, minimumSafetyRatio);
                resolved.Marked = EnsureContrast(resolved.Marked, resolved.Background, minimumSafetyRatio);
            }
        }

        return resolved;
    }

    private static ResolvedColors ApplyCustomColors(ResolvedColors resolved, CustomFileListColorSettings custom)
    {
        return new ResolvedColors
        {
            Background = ParseHexColor(custom.Background) ?? resolved.Background,
            NormalFile = ParseHexColor(custom.NormalFile) ?? resolved.NormalFile,
            Directory = ParseHexColor(custom.Directory) ?? resolved.Directory,
            ReadOnly = ParseHexColor(custom.ReadOnly) ?? resolved.ReadOnly,
            Hidden = ParseHexColor(custom.Hidden) ?? resolved.Hidden,
            System = ParseHexColor(custom.System) ?? resolved.System,
            Marked = ParseHexColor(custom.Marked) ?? resolved.Marked,
            SelectedBackground = ParseHexColor(custom.SelectedBackground) ?? resolved.SelectedBackground,
            SelectedForeground = ParseHexColor(custom.SelectedForeground) ?? resolved.SelectedForeground
        };
    }

    public static Color? ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
        return null;
    }

    public static string ToHexColor(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public static double GetRelativeLuminance(Color c)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;

        r = (r <= 0.03928) ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
        g = (g <= 0.03928) ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
        b = (b <= 0.03928) ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    public static double GetContrastRatio(Color c1, Color c2)
    {
        double l1 = GetRelativeLuminance(c1);
        double l2 = GetRelativeLuminance(c2);
        return l1 > l2 ? (l1 + 0.05) / (l2 + 0.05) : (l2 + 0.05) / (l1 + 0.05);
    }

    public static Color EnsureContrast(Color fg, Color bg, double minRatio = 4.5)
    {
        double ratio = GetContrastRatio(fg, bg);
        if (ratio >= minRatio) return fg;

        ColorToHsl(fg, out double h, out double s, out double l);
        double bgL = GetRelativeLuminance(bg);

        // 背景が明るい場合は文字を暗くし、背景が暗い場合は文字を明るくする
        bool darken = bgL > 0.5;

        double step = 0.05;
        for (int i = 0; i < 20; i++)
        {
            if (darken)
            {
                l = Math.Max(0.0, l - step);
            }
            else
            {
                l = Math.Min(1.0, l + step);
            }

            Color candidate = HslToColor(h, s, l);
            if (GetContrastRatio(candidate, bg) >= minRatio)
            {
                return candidate;
            }

            if (darken && l <= 0.0) break;
            if (!darken && l >= 1.0) break;
        }

        return darken ? Color.Black : Color.White;
    }

    public static Color EnsureSemanticContrast(Color fg, Color normalFg, Color bg, double minHueDiff = 0.08, double minLightnessDiff = 0.15)
    {
        if (GetContrastRatio(fg, bg) >= 2.2 || GetContrastRatio(fg, normalFg) >= 1.12)
        {
            return fg;
        }

        ColorToHsl(fg, out double h, out double s, out double l);
        ColorToHsl(normalFg, out double nh, out double ns, out double nl);

        double hueDiff = Math.Abs(h - nh);
        if (hueDiff > 0.5) hueDiff = 1.0 - hueDiff;

        double lightDiff = Math.Abs(l - nl);

        if (hueDiff < minHueDiff && lightDiff < minLightnessDiff)
        {
            double bgL = GetRelativeLuminance(bg);
            if (bgL > 0.5)
            {
                if (l >= nl) l = Math.Max(0.0, l - minLightnessDiff);
                else l = Math.Min(1.0, l + minLightnessDiff);
            }
            else
            {
                if (l >= nl) l = Math.Min(1.0, l + minLightnessDiff);
                else l = Math.Max(0.0, l - minLightnessDiff);
            }
            fg = HslToColor(h, s, l);
            fg = EnsureContrast(fg, bg, 2.2);
        }

        return fg;
    }

    public static void ColorToHsl(Color color, out double h, out double s, out double l)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));

        h = s = l = (max + min) / 2.0;

        if (max == min)
        {
            h = s = 0.0;
        }
        else
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

            if (max == r)
            {
                h = (g - b) / d + (g < b ? 6.0 : 0.0);
            }
            else if (max == g)
            {
                h = (b - r) / d + 2.0;
            }
            else if (max == b)
            {
                h = (r - g) / d + 4.0;
            }

            h /= 6.0;
        }
    }

    public static Color HslToColor(double h, double s, double l)
    {
        double r, g, b;

        if (s == 0.0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
            double p = 2.0 * l - q;

            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }

        return Color.FromArgb(
            (int)Math.Max(0, Math.Min(255, Math.Round(r * 255))),
            (int)Math.Max(0, Math.Min(255, Math.Round(g * 255))),
            (int)Math.Max(0, Math.Min(255, Math.Round(b * 255)))
        );
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0.0) t += 1.0;
        if (t > 1.0) t -= 1.0;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }

    public static bool IsHighContrastPreset(string? presetKey)
    {
        if (string.IsNullOrWhiteSpace(presetKey)) return false;
        return presetKey.Equals("High Contrast Dark", StringComparison.OrdinalIgnoreCase)
            || presetKey.Equals("High Contrast Light", StringComparison.OrdinalIgnoreCase);
    }

    public static Color ResolveSelectedForegroundForPreset(
        string? presetKey,
        Color semanticForeground,
        Color selectedBackground)
    {
        if (!IsHighContrastPreset(presetKey))
        {
            return semanticForeground;
        }

        return EnsureContrast(
            semanticForeground,
            selectedBackground,
            minRatio: 4.5);
    }
}
