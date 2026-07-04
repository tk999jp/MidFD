using System.Drawing;

namespace MidFD.Helpers;

public static class ColorContrastHelper
{
    public static Color Blend(Color colorA, Color colorB, double amount)
    {
        amount = Math.Max(0.0, Math.Min(1.0, amount));
        byte r = (byte)(colorA.R * (1.0 - amount) + colorB.R * amount);
        byte g = (byte)(colorA.G * (1.0 - amount) + colorB.G * amount);
        byte b = (byte)(colorA.B * (1.0 - amount) + colorB.B * amount);
        return Color.FromArgb(r, g, b);
    }

    public static double GetRelativeLuminance(Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        r = (r <= 0.03928) ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
        g = (g <= 0.03928) ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
        b = (b <= 0.03928) ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    public static double GetContrastRatio(Color colorA, Color colorB)
    {
        double l1 = GetRelativeLuminance(colorA);
        double l2 = GetRelativeLuminance(colorB);
        double bright = Math.Max(l1, l2);
        double dark = Math.Min(l1, l2);
        return (bright + 0.05) / (dark + 0.05);
    }

    public static Color PickReadableTextColor(Color backColor, Color darkCandidate, Color lightCandidate)
    {
        double darkContrast = GetContrastRatio(backColor, darkCandidate);
        double lightContrast = GetContrastRatio(backColor, lightCandidate);
        return darkContrast >= lightContrast ? darkCandidate : lightCandidate;
    }
}
