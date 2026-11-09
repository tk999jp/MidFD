using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MidFD.Services;

public static class ImageEditService
{
    private const long MaximumPixelCount = 100_000_000;

    public static Bitmap Resize(Bitmap source, int width, int height, bool preserveAspectRatio)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > MaximumPixelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "出力画像の寸法が許容範囲を超えています。");
        }

        if (preserveAspectRatio)
        {
            double scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
            width = Math.Max(1, (int)Math.Round(source.Width * scale));
            height = Math.Max(1, (int)Math.Round(source.Height * scale));
        }

        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            resized.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            using Graphics graphics = Graphics.FromImage(resized);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            return resized;
        }
        catch
        {
            resized.Dispose();
            throw;
        }
    }

    public static Bitmap ReplaceSelectionStretch(Bitmap destination, Bitmap source, Rectangle target)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(destination);
        ValidateSource(source);
        if (target.Width <= 0 || target.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        Rectangle clippedTarget = Rectangle.Intersect(
            new Rectangle(0, 0, destination.Width, destination.Height),
            target);
        if (clippedTarget.Width <= 0 || clippedTarget.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        Bitmap output = ToArgb(destination);
        try
        {
            float scaleX = source.Width / (float)target.Width;
            float scaleY = source.Height / (float)target.Height;
            var sourceRect = new RectangleF(
                (clippedTarget.Left - target.Left) * scaleX,
                (clippedTarget.Top - target.Top) * scaleY,
                clippedTarget.Width * scaleX,
                clippedTarget.Height * scaleY);

            using Graphics graphics = Graphics.FromImage(output);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
            graphics.DrawImage(
                source,
                clippedTarget,
                sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height,
                GraphicsUnit.Pixel,
                attributes);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public static Bitmap ReplaceSelectionOriginalSize(Bitmap destination, Bitmap source, Rectangle target)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(destination);
        ValidateSource(source);
        if (target.Width <= 0 || target.Height <= 0) throw new ArgumentOutOfRangeException(nameof(target));

        // Use widened coordinates before clipping so distant rectangles cannot overflow.
        long left = Math.Max(0L, target.Left);
        long top = Math.Max(0L, target.Top);
        long right = Math.Min(destination.Width, Math.Min((long)target.Left + target.Width, (long)target.Left + source.Width));
        long bottom = Math.Min(destination.Height, Math.Min((long)target.Top + target.Height, (long)target.Top + source.Height));
        var bounds = new Rectangle(0, 0, destination.Width, destination.Height);
        var output = new Bitmap(destination.Width, destination.Height, PixelFormat.Format32bppArgb);
        try
        {
            output.SetResolution(destination.HorizontalResolution, destination.VerticalResolution);
            CopyArgbPixels(destination, bounds, output, Point.Empty);
            if (right <= left || bottom <= top) return output;
            var writeRect = new Rectangle((int)left, (int)top, (int)(right - left), (int)(bottom - top));
            var sourceRect = new Rectangle((int)(left - target.Left), (int)(top - target.Top), writeRect.Width, writeRect.Height);
            CopyArgbPixels(source, sourceRect, output, writeRect.Location);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void CopyArgbPixels(Bitmap source, Rectangle sourceRect, Bitmap destination, Point location)
    {
        // Read the existing raster and write to an owned memory bitmap, including PNG-backed inputs.
        BitmapData inputData = source.LockBits(sourceRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            BitmapData outputData = destination.LockBits(new Rectangle(location, sourceRect.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[sourceRect.Width * 4];
                for (int y = 0; y < sourceRect.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(inputData.Scan0, y * inputData.Stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(outputData.Scan0, y * outputData.Stride), row.Length);
                }
            }
            finally { destination.UnlockBits(outputData); }
        }
        finally { source.UnlockBits(inputData); }
    }

    public static Bitmap ApplyMosaic(Bitmap source, Rectangle area, int blockSize = 12)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);
        if (blockSize is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(blockSize));
        area = NormalizeArea(source, area);
        Bitmap input = ToArgb(source);
        Bitmap output = ToArgb(source);
        try
        {
            Rectangle bounds = new(0, 0, source.Width, source.Height);
            BitmapData inputData = input.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData outputData = output.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int columns = (area.Width + blockSize - 1) / blockSize;
                var sums = new long[columns, 4];
                var counts = new int[columns];
                for (int blockY = area.Top; blockY < area.Bottom; blockY += blockSize)
                {
                    Array.Clear(sums);
                    Array.Clear(counts);
                    int blockBottom = Math.Min(area.Bottom, blockY + blockSize);
                    for (int y = blockY; y < blockBottom; y++)
                    {
                        byte[] row = ReadRow(inputData, y);
                        for (int x = area.Left; x < area.Right; x++)
                        {
                            int column = (x - area.Left) / blockSize;
                            int offset = x * 4;
                            sums[column, 0] += row[offset];
                            sums[column, 1] += row[offset + 1];
                            sums[column, 2] += row[offset + 2];
                            sums[column, 3] += row[offset + 3];
                        }
                    }

                    for (int column = 0; column < columns; column++)
                    {
                        int xStart = area.Left + column * blockSize;
                        int xEnd = Math.Min(area.Right, xStart + blockSize);
                        counts[column] = (xEnd - xStart) * (blockBottom - blockY);
                    }

                    for (int y = blockY; y < blockBottom; y++)
                    {
                        byte[] row = ReadRow(outputData, y);
                        for (int x = area.Left; x < area.Right; x++)
                        {
                            int column = (x - area.Left) / blockSize;
                            int offset = x * 4;
                            int count = counts[column];
                            for (int channel = 0; channel < 4; channel++)
                            {
                                row[offset + channel] = (byte)((sums[column, channel] + count / 2) / count);
                            }
                        }
                        WriteRow(outputData, y, row);
                    }
                }
            }
            finally
            {
                output.UnlockBits(outputData);
                input.UnlockBits(inputData);
            }
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            input.Dispose();
        }
    }

    public static Bitmap ApplyBlur(Bitmap source, Rectangle area, int radius = 5)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);
        if (radius is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(radius));
        area = NormalizeArea(source, area);
        Bitmap input = ToArgb(source);
        Bitmap output = ToArgb(source);
        try
        {
            Rectangle bounds = new(0, 0, source.Width, source.Height);
            BitmapData inputData = input.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData outputData = output.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int kernelSize = radius * 2 + 1;
                var ring = new int[kernelSize][];
                var verticalSums = new long[area.Width * 4];
                int firstSourceY = Math.Max(0, area.Top - radius);
                int lastSourceY = Math.Min(source.Height - 1, area.Bottom - 1 + radius);
                int nextOutputY = area.Top;

                for (int sourceY = firstSourceY; sourceY <= lastSourceY; sourceY++)
                {
                    byte[] row = ReadRow(inputData, sourceY);
                    int[] horizontal = ComputeHorizontalSums(row, source.Width, area, radius);
                    int ringIndex = sourceY % kernelSize;
                    ring[ringIndex] = horizontal;
                    for (int i = 0; i < horizontal.Length; i++) verticalSums[i] += horizontal[i];

                    while (nextOutputY < area.Bottom && Math.Min(source.Height - 1, nextOutputY + radius) <= sourceY)
                    {
                        int verticalCount = sourceY - Math.Max(0, nextOutputY - radius) + 1;
                        byte[] outputRow = ReadRow(outputData, nextOutputY);
                        for (int x = area.Left; x < area.Right; x++)
                        {
                            int areaX = x - area.Left;
                            int horizontalCount = Math.Min(source.Width - 1, x + radius) - Math.Max(0, x - radius) + 1;
                            int divisor = verticalCount * horizontalCount;
                            int rowOffset = x * 4;
                            int sumOffset = areaX * 4;
                            for (int channel = 0; channel < 4; channel++)
                            {
                                outputRow[rowOffset + channel] = (byte)((verticalSums[sumOffset + channel] + divisor / 2) / divisor);
                            }
                        }
                        WriteRow(outputData, nextOutputY, outputRow);

                        int removeY = nextOutputY - radius;
                        if (removeY >= firstSourceY && removeY <= sourceY && ring[removeY % kernelSize] is { } expired)
                        {
                            for (int i = 0; i < expired.Length; i++) verticalSums[i] -= expired[i];
                        }
                        nextOutputY++;
                    }
                }
            }
            finally
            {
                output.UnlockBits(outputData);
                input.UnlockBits(inputData);
            }
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            input.Dispose();
        }
    }

    public static Bitmap AdjustTone(Bitmap source, int brightness, int contrast, int saturation, double gamma)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);
        if (brightness is < -100 or > 100) throw new ArgumentOutOfRangeException(nameof(brightness));
        if (contrast is < -100 or > 100) throw new ArgumentOutOfRangeException(nameof(contrast));
        if (saturation is < 0 or > 200) throw new ArgumentOutOfRangeException(nameof(saturation));
        if (!double.IsFinite(gamma) || gamma is < 0.1 or > 4.0) throw new ArgumentOutOfRangeException(nameof(gamma));

        Bitmap output = ToArgb(source);
        try
        {
            Rectangle bounds = new(0, 0, output.Width, output.Height);
            BitmapData data = output.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                double contrastFactor = 259d * (contrast + 255) / (255d * (259 - contrast));
                double brightnessOffset = brightness * 2.55;
                double saturationFactor = saturation / 100d;
                for (int y = 0; y < output.Height; y++)
                {
                    byte[] row = ReadRow(data, y);
                    for (int x = 0; x < output.Width; x++)
                    {
                        int offset = x * 4;
                        double blue = ApplyGamma(contrastFactor * (row[offset] - 128) + 128 + brightnessOffset, gamma);
                        double green = ApplyGamma(contrastFactor * (row[offset + 1] - 128) + 128 + brightnessOffset, gamma);
                        double red = ApplyGamma(contrastFactor * (row[offset + 2] - 128) + 128 + brightnessOffset, gamma);
                        double gray = red * 0.299 + green * 0.587 + blue * 0.114;
                        row[offset] = ClampByte(gray + (blue - gray) * saturationFactor);
                        row[offset + 1] = ClampByte(gray + (green - gray) * saturationFactor);
                        row[offset + 2] = ClampByte(gray + (red - gray) * saturationFactor);
                    }
                    WriteRow(data, y, row);
                }
            }
            finally
            {
                output.UnlockBits(data);
            }
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static int[] ComputeHorizontalSums(byte[] row, int imageWidth, Rectangle area, int radius)
    {
        var sums = new int[area.Width * 4];
        int firstX = area.Left;
        int start = Math.Max(0, firstX - radius);
        int end = Math.Min(imageWidth - 1, firstX + radius);
        Span<int> current = stackalloc int[4];
        for (int x = start; x <= end; x++)
        {
            int offset = x * 4;
            for (int c = 0; c < 4; c++) current[c] += row[offset + c];
        }
        for (int x = area.Left; x < area.Right; x++)
        {
            int target = (x - area.Left) * 4;
            for (int c = 0; c < 4; c++) sums[target + c] = current[c];
            if (x + 1 >= area.Right) continue;
            int removeX = x - radius;
            if (removeX >= 0)
            {
                int offset = removeX * 4;
                for (int c = 0; c < 4; c++) current[c] -= row[offset + c];
            }
            int addX = x + radius + 1;
            if (addX < imageWidth)
            {
                int offset = addX * 4;
                for (int c = 0; c < 4; c++) current[c] += row[offset + c];
            }
        }
        return sums;
    }

    private static Bitmap ToArgb(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb)
        {
            return (Bitmap)source.Clone();
        }
        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        try
        {
            output.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            using Graphics graphics = Graphics.FromImage(output);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(source, 0, 0);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static Rectangle NormalizeArea(Bitmap source, Rectangle area)
    {
        Rectangle bounds = new(0, 0, source.Width, source.Height);
        Rectangle normalized = Rectangle.Intersect(bounds, area);
        if (normalized.Width <= 0 || normalized.Height <= 0) throw new ArgumentOutOfRangeException(nameof(area));
        return normalized;
    }

    private static void ValidateSource(Bitmap source)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Width > 32768 || source.Height > 32768
            || (long)source.Width * source.Height > MaximumPixelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "入力画像の寸法が許容範囲を超えています。");
        }
    }

    private static byte[] ReadRow(BitmapData data, int y)
    {
        var row = new byte[Math.Abs(data.Stride)];
        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
        return row;
    }

    private static void WriteRow(BitmapData data, int y, byte[] row)
        => Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);

    private static double ApplyGamma(double value, double gamma)
        => 255 * Math.Pow(Math.Clamp(value, 0, 255) / 255d, 1d / gamma);

    private static byte ClampByte(double value)
        => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
