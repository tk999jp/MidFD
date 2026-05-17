using System.Drawing;

namespace MidFD.Models;

public static class MidFDColors
{
    public static Color ListNormalFore { get; private set; } = Color.Cyan;
    public static Color ListNormalBack { get; private set; } = Color.Black;
    public static Color ListSelectedBack { get; private set; } = Color.FromArgb(0, 64, 80);
    public static Color ListSelectedFore { get; private set; } = Color.White;

    public static Color ListDirectoryFore { get; private set; } = Color.Cyan;
    public static Color ListFileFore { get; private set; } = Color.Khaki;
    public static Color ListSystemFore { get; private set; } = Color.Magenta;
    public static Color ListHiddenFore { get; private set; } = Color.Blue;
    public static Color ListReadOnlyFore { get; private set; } = Color.Lime;
    public static Color ListArchiveFore { get; private set; } = Color.Gold;
    public static Color ListMarkedBack { get; private set; } = Color.Black;
    public static Color ListMarkedFore { get; private set; } = Color.White;
    public static Color ListSelectedMarkedBack { get; private set; } = Color.FromArgb(0, 80, 100);

    public static Color BorderLine { get; private set; } = Color.Cyan;
    public static Color SeparatorLine { get; private set; } = Color.FromArgb(0, 100, 100);

    public static Color ViewerBack { get; private set; } = Color.FromArgb(0, 0, 64);
    public static Color ViewerFore { get; private set; } = Color.White;

    public static void ApplyTheme(string? themeKey)
    {
        switch (themeKey)
        {
            case "Green":
                ListNormalFore = Color.Lime;
                ListNormalBack = Color.Black;
                ListSelectedBack = Color.FromArgb(0, 48, 0);
                ListSelectedFore = Color.White;
                ListDirectoryFore = Color.Lime;
                ListFileFore = Color.LightGreen;
                ListSystemFore = Color.Magenta;
                ListHiddenFore = Color.Blue;
                ListReadOnlyFore = Color.Lime;
                ListArchiveFore = Color.GreenYellow;
                ListMarkedBack = Color.Black;
                ListMarkedFore = Color.White;
                ListSelectedMarkedBack = Color.FromArgb(0, 72, 0);
                BorderLine = Color.Lime;
                SeparatorLine = Color.FromArgb(0, 96, 0);
                ViewerBack = Color.FromArgb(0, 32, 0);
                ViewerFore = Color.White;
                break;

            case "Amber":
                ListNormalFore = Color.FromArgb(255, 210, 120);
                ListNormalBack = Color.FromArgb(24, 18, 0);
                ListSelectedBack = Color.FromArgb(92, 58, 0);
                ListSelectedFore = Color.White;
                ListDirectoryFore = Color.FromArgb(255, 220, 140);
                ListFileFore = Color.FromArgb(255, 235, 170);
                ListSystemFore = Color.Magenta;
                ListHiddenFore = Color.Blue;
                ListReadOnlyFore = Color.Lime;
                ListArchiveFore = Color.FromArgb(255, 210, 120);
                ListMarkedBack = Color.FromArgb(24, 18, 0);
                ListMarkedFore = Color.White;
                ListSelectedMarkedBack = Color.FromArgb(120, 72, 0);
                BorderLine = Color.FromArgb(220, 220, 220);
                SeparatorLine = Color.FromArgb(130, 84, 0);
                ViewerBack = Color.FromArgb(48, 32, 0);
                ViewerFore = Color.White;
                break;

            case "Light": // "Windows" 相当の配色
                // 基本背景・文字：エクスプローラは完全な白背景に黒文字
                ListNormalFore = Color.Black;
                ListNormalBack = Color.White;
                // 選択色：Windows標準の薄い青 (#CCE8FF)
                ListSelectedBack = Color.FromArgb(204, 232, 255);
                ListSelectedFore = Color.Black;
                // ディレクトリ・ファイル：エクスプローラは文字色を分けないため黒に統一
                // (MidFDの視認性を考慮し、ディレクトリのみ僅かに濃い紺にする選択肢もあります)
                ListDirectoryFore = Color.Black; 
                ListFileFore = Color.Black;
                ListSystemFore = Color.Magenta;
                ListHiddenFore = Color.Blue;
                ListReadOnlyFore = Color.Green;
                ListArchiveFore = Color.FromArgb(120, 90, 20);
                // マーク（チェック状）：背景は透明(白)のまま、文字で識別
                ListMarkedBack = Color.White;
                ListMarkedFore = Color.Black;
                // マークかつ選択状態：選択色の彩度を僅かに上げた色
                ListSelectedMarkedBack = Color.FromArgb(190, 220, 245);
                // 境界線：目立ちすぎない薄いグレー
                BorderLine = Color.FromArgb(220, 220, 220);
                SeparatorLine = Color.FromArgb(232, 232, 232);
                // ビューア：標準的な白背景
                ViewerBack = Color.White;
                ViewerFore = Color.Black;
                break;

            default:
                ListNormalFore = Color.Cyan;
                ListNormalBack = Color.Black;
                ListSelectedBack = Color.FromArgb(0, 64, 80);
                ListSelectedFore = Color.White;
                ListDirectoryFore = Color.Cyan;
                ListFileFore = Color.Khaki;
                ListSystemFore = Color.Magenta;
                ListHiddenFore = Color.Blue;
                ListReadOnlyFore = Color.Lime;
                ListArchiveFore = Color.Gold;
                ListMarkedBack = Color.Black;
                ListMarkedFore = Color.White;
                ListSelectedMarkedBack = Color.FromArgb(0, 80, 100);
                BorderLine = Color.Cyan;
                SeparatorLine = Color.FromArgb(0, 100, 100);
                ViewerBack = Color.FromArgb(0, 0, 64);
                ViewerFore = Color.White;
                break;
        }
    }
}
