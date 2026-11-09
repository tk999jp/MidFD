namespace MidFD;

internal static class VideoPlaybackGeometry
{
    internal static Size CalculateDisplaySize(
        uint encodedWidth,
        uint encodedHeight,
        uint sampleAspectRatioNumerator,
        uint sampleAspectRatioDenominator,
        bool swapsDimensions)
    {
        if (encodedWidth == 0 || encodedHeight == 0)
        {
            return Size.Empty;
        }

        double displayWidth = encodedWidth;
        double displayHeight = encodedHeight;
        if (sampleAspectRatioNumerator > 0 && sampleAspectRatioDenominator > 0)
        {
            displayWidth *= sampleAspectRatioNumerator / (double)sampleAspectRatioDenominator;
        }

        if (swapsDimensions)
        {
            (displayWidth, displayHeight) = (displayHeight, displayWidth);
        }

        if (double.IsNaN(displayWidth)
            || double.IsInfinity(displayWidth)
            || displayWidth > int.MaxValue
            || displayHeight > int.MaxValue)
        {
            return Size.Empty;
        }

        int width = Math.Max(1, (int)Math.Round(displayWidth, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(displayHeight, MidpointRounding.AwayFromZero));
        return new Size(width, height);
    }
}
