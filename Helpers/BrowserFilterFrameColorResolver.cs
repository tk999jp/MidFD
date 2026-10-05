using System.Drawing;
using MidFD.Services;

namespace MidFD.Helpers;

internal static class BrowserFilterFrameColorResolver
{
    internal const double MinimumBackgroundContrast = 3.0;

    public static Color Resolve(
        FileListColorResolver.ResolvedColors colors,
        Color normalBorder,
        Color uiAccent)
    {
        Color readableTextCandidate = ColorContrastHelper.PickReadableTextColor(
            colors.Background,
            colors.NormalFile,
            colors.SelectedForeground);
        var candidates = new[]
        {
            colors.SelectedForeground,
            colors.NormalFile,
            colors.Directory,
            uiAccent,
            colors.StatusNormal,
            readableTextCandidate
        };

        var uniqueCandidates = new List<Color>();
        var seen = new HashSet<int>();
        foreach (Color candidate in candidates)
        {
            if (candidate.IsEmpty || !seen.Add(candidate.ToArgb()))
            {
                continue;
            }

            uniqueCandidates.Add(candidate);
        }

        if (uniqueCandidates.Count == 0)
        {
            return normalBorder;
        }

        List<Color> readableCandidates = uniqueCandidates
            .Where(candidate => ColorContrastHelper.GetContrastRatio(candidate, colors.Background) >= MinimumBackgroundContrast)
            .ToList();
        IReadOnlyList<Color> selectionPool = readableCandidates.Count > 0
            ? readableCandidates
            : uniqueCandidates;

        Color best = selectionPool[0];
        foreach (Color candidate in selectionPool.Skip(1))
        {
            long candidateDistance = GetRgbDistanceSquared(candidate, normalBorder);
            long bestDistance = GetRgbDistanceSquared(best, normalBorder);
            if (candidateDistance > bestDistance ||
                (candidateDistance == bestDistance &&
                 ColorContrastHelper.GetContrastRatio(candidate, colors.Background) >
                 ColorContrastHelper.GetContrastRatio(best, colors.Background)))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static long GetRgbDistanceSquared(Color first, Color second)
    {
        long red = first.R - second.R;
        long green = first.G - second.G;
        long blue = first.B - second.B;
        return (red * red) + (green * green) + (blue * blue);
    }
}
