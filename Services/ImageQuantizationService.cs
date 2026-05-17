using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MidFD.Services;

public enum QuantizationDitherMode
{
    None,
    FloydSteinberg,
    Atkinson,
    OrderedBayer4x4,
    VoidAndClusterBlueNoise,
    SierraLite,
    BlueNoiseErrorDiffusion
}

public enum QuantizationMergeLevel
{
    None,
    Weak,
    Medium,
    Strong
}

public sealed class QuantizationRequest
{
    public required int ColorCount { get; init; }
    public required bool UseRgb565 { get; init; }
    public required QuantizationDitherMode Dither { get; init; }
    public required QuantizationMergeLevel MergeLevel { get; init; }
}

public static class ImageQuantizationService
{
    private static readonly int[] Bayer4x4 =
    {
        0, 8, 2, 10,
        12, 4, 14, 6,
        3, 11, 1, 9,
        15, 7, 13, 5
    };

    private static readonly int[] BlueNoise8x8 =
    {
        32, 12, 40, 4, 34, 14, 42, 6,
        48, 60, 20, 56, 50, 62, 22, 58,
        44, 8, 36, 0, 46, 10, 38, 2,
        24, 52, 28, 16, 26, 54, 30, 18,
        35, 15, 43, 7, 33, 13, 41, 5,
        51, 63, 23, 59, 49, 61, 21, 57,
        47, 11, 39, 3, 45, 9, 37, 1,
        27, 55, 31, 19, 25, 53, 29, 17
    };

    public static Bitmap Quantize(Bitmap source, QuantizationRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (request.UseRgb565)
        {
            var res = QuantizeRgb565(source);
            sw.Stop();
            System.Diagnostics.Debug.WriteLine($"Quantize: {source.Width}x{source.Height}, RGB565, {sw.ElapsedMilliseconds}ms");
            return res;
        }

        int colorCount = Math.Clamp(request.ColorCount, 2, 256);
        var palette = BuildPalette(source, colorCount);
        ApplyMergeLevel(palette, request.MergeLevel, colorCount);
        var result = ApplyPalette(source, palette, request.Dither, request.MergeLevel);
        sw.Stop();

        // 重複色の確認
        int uniqueCount = palette.Select(c => c.ToArgb()).Distinct().Count();
        System.Diagnostics.Debug.WriteLine($"Quantize: {source.Width}x{source.Height}, Colors={colorCount}, Real={palette.Count}, Unique={uniqueCount}, Dither={request.Dither}, Merge={request.MergeLevel}, {sw.ElapsedMilliseconds}ms");
        return result;
    }

    public static Bitmap QuantizeRgb565(Bitmap source)
    {
        var src = Copy32bppArgb(source);
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var srcData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int srcBytes = Math.Abs(srcData.Stride) * src.Height;
            int dstBytes = Math.Abs(dstData.Stride) * dst.Height;
            byte[] srcBuffer = new byte[srcBytes];
            byte[] dstBuffer = new byte[dstBytes];
            Marshal.Copy(srcData.Scan0, srcBuffer, 0, srcBytes);
            for (int y = 0; y < src.Height; y++)
            {
                int row = y * srcData.Stride;
                for (int x = 0; x < src.Width; x++)
                {
                    int i = row + (x * 4);
                    byte b = srcBuffer[i + 0];
                    byte g = srcBuffer[i + 1];
                    byte r = srcBuffer[i + 2];
                    byte a = srcBuffer[i + 3];
                    byte r5 = (byte)(r >> 3);
                    byte g6 = (byte)(g >> 2);
                    byte b5 = (byte)(b >> 3);
                    dstBuffer[i + 0] = (byte)((b5 << 3) | (b5 >> 2));
                    dstBuffer[i + 1] = (byte)((g6 << 2) | (g6 >> 4));
                    dstBuffer[i + 2] = (byte)((r5 << 3) | (r5 >> 2));
                    dstBuffer[i + 3] = a;
                }
            }
            Marshal.Copy(dstBuffer, 0, dstData.Scan0, dstBytes);
        }
        finally
        {
            src.UnlockBits(srcData);
            dst.UnlockBits(dstData);
            src.Dispose();
        }
        return dst;
    }

    private static List<Color> BuildPalette(Bitmap source, int colorCount)
    {
        var src = Copy32bppArgb(source);
        var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * src.Height;
            byte[] buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);
            var buckets = new Dictionary<int, (long r, long g, long b, int count)>();
            int area = src.Width * src.Height;
            int step = area > 2_000_000 ? 2 : 1;
            for (int y = 0; y < src.Height; y += step)
            {
                int row = y * data.Stride;
                for (int x = 0; x < src.Width; x += step)
                {
                    int i = row + (x * 4);
                    int b = buffer[i + 0];
                    int g = buffer[i + 1];
                    int r = buffer[i + 2];
                    int a = buffer[i + 3];
                    if (a < 128) continue; // 透明度は考慮せず、不透明に近い色のみ対象
                    // 半透明ピクセルがパレットを歪めないよう、アルファ値に応じた重み付けを行う
                    int weight = (a == 255) ? 4 : (a >= 192 ? 2 : 1);
                    int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    if (!buckets.TryGetValue(key, out var v))
                    {
                        v = (0, 0, 0, 0);
                    }
                    v.r += (long)r * weight;
                    v.g += (long)g * weight;
                    v.b += (long)b * weight;
                    v.count += weight;
                    buckets[key] = v;
                }
            }

            var colors = buckets.Values.Select(x => ((int)(x.r / x.count), (int)(x.g / x.count), (int)(x.b / x.count), x.count)).ToList();
            if (colors.Count == 0)
            {
                return new List<Color> { Color.Black, Color.White };
            }
            if (colors.Count <= colorCount)
            {
                return colors.Select(x => Color.FromArgb(x.Item1, x.Item2, x.Item3)).ToList();
            }

            // Weighted Median Cut
            var boxes = new List<ColorBox> { new ColorBox(colors) };
            while (boxes.Count < colorCount)
            {
                // 分割対象boxの選定基準を「ピクセル数×色相範囲（スコア）」へ改善
                var boxToSplit = boxes.Where(b => b.Colors.Count > 1).OrderByDescending(b => b.Score()).FirstOrDefault();
                if (boxToSplit == null) break;

                boxes.Remove(boxToSplit);
                int axis = boxToSplit.LongestAxis();
                if (axis == 0) boxToSplit.Colors.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                else if (axis == 1) boxToSplit.Colors.Sort((a, b) => a.Item2.CompareTo(b.Item2));
                else boxToSplit.Colors.Sort((a, b) => a.Item3.CompareTo(b.Item3));

                // 単純な中央分割ではなく、重み（ピクセル数）の中央値で分割
                int splitIndex = boxToSplit.FindWeightedMedian();
                boxes.Add(new ColorBox(boxToSplit.Colors.GetRange(0, splitIndex + 1)));
                boxes.Add(new ColorBox(boxToSplit.Colors.GetRange(splitIndex + 1, boxToSplit.Colors.Count - (splitIndex + 1))));
            }

            var palette = boxes.Select(b => b.AverageColor()).ToList();

            // 1-pass K-means refinement で色相ズレを補正
            RefinePalette(palette, colors);

            return palette;
        }
        finally
        {
            src.UnlockBits(data);
            src.Dispose();
        }
    }

    private sealed class ColorBox
    {
        public List<(int r, int g, int b, int count)> Colors;
        public int TotalCount;
        private int rMin, rMax, gMin, gMax, bMin, bMax;

        public ColorBox(List<(int r, int g, int b, int count)> colors)
        {
            Colors = colors;
            TotalCount = 0;
            rMin = gMin = bMin = 255;
            rMax = gMax = bMax = 0;
            foreach (var c in colors)
            {
                TotalCount += c.count;
                if (c.r < rMin) rMin = c.r;
                if (c.r > rMax) rMax = c.r;
                if (c.g < gMin) gMin = c.g;
                if (c.g > gMax) gMax = c.g;
                if (c.b < bMin) bMin = c.b;
                if (c.b > bMax) bMax = c.b;
            }
        }

        public int LongestAxis()
        {
            int rd = rMax - rMin;
            int gd = gMax - gMin;
            int bd = bMax - bMin;
            if (rd >= gd && rd >= bd) return 0;
            if (gd >= rd && gd >= bd) return 1;
            return 2;
        }

        public long Score()
        {
            // 色範囲の広さとピクセル頻度の両方を考慮した分割優先度スコア
            long range = (rMax - rMin) + (gMax - gMin) + (bMax - bMin);
            return range * TotalCount;
        }

        public int FindWeightedMedian()
        {
            long half = TotalCount / 2;
            long current = 0;
            for (int i = 0; i < Colors.Count - 1; i++)
            {
                current += Colors[i].count;
                if (current >= half) return i;
            }
            return Colors.Count / 2;
        }

        public Color AverageColor()
        {
            if (Colors.Count == 0) return Color.Black;
            long r = 0, g = 0, b = 0, count = 0;
            foreach (var c in Colors)
            {
                r += (long)c.r * c.count;
                g += (long)c.g * c.count;
                b += (long)c.b * c.count;
                count += c.count;
            }
            return Color.FromArgb((int)(r / count), (int)(g / count), (int)(b / count));
        }
    }

    private static void RefinePalette(List<Color> palette, List<(int r, int g, int b, int count)> colors)
    {
        // 1回だけ K-means 的な再配置を行い、Median Cut の平均化による色相ズレを補正する
        if (palette.Count == 0) return;

        long[] rSum = new long[palette.Count];
        long[] gSum = new long[palette.Count];
        long[] bSum = new long[palette.Count];
        long[] countSum = new long[palette.Count];

        foreach (var c in colors)
        {
            int best = 0;
            long bestDist = long.MaxValue;
            for (int i = 0; i < palette.Count; i++)
            {
                var p = palette[i];
                int dr = c.r - p.R;
                int dg = c.g - p.G;
                int db = c.b - p.B;
                // パレット洗練時も知覚重み付き距離を使用
                long d = (long)(dr * dr * 3) + (long)(dg * dg * 4) + (long)(db * db * 2);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            rSum[best] += (long)c.r * c.count;
            gSum[best] += (long)c.g * c.count;
            bSum[best] += (long)c.b * c.count;
            countSum[best] += c.count;
        }

        for (int i = 0; i < palette.Count; i++)
        {
            if (countSum[i] > 0)
            {
                palette[i] = Color.FromArgb((int)(rSum[i] / countSum[i]), (int)(gSum[i] / countSum[i]), (int)(bSum[i] / countSum[i]));
            }
        }
    }

    private static void ApplyMergeLevel(List<Color> palette, QuantizationMergeLevel level, int requestedColorCount)
    {
        int threshold = level switch
        {
            QuantizationMergeLevel.Weak => 10,
            QuantizationMergeLevel.Medium => 16,
            QuantizationMergeLevel.Strong => 24,
            _ => 0
        };
        if (threshold <= 0 || palette.Count <= 2)
        {
            return;
        }
        int thresholdSq = threshold * threshold;
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < palette.Count; i++)
            {
                for (int j = i + 1; j < palette.Count; j++)
                {
                    int dr = palette[i].R - palette[j].R;
                    int dg = palette[i].G - palette[j].G;
                    int db = palette[i].B - palette[j].B;
                    int d = dr * dr + dg * dg + db * db;
                    if (d > thresholdSq) continue;
                    if (palette.Count <= 2 || (requestedColorCount <= 2 && palette.Count <= 2))
                    {
                        return;
                    }
                    var mergedColor = Color.FromArgb((palette[i].R + palette[j].R) / 2, (palette[i].G + palette[j].G) / 2, (palette[i].B + palette[j].B) / 2);
                    palette[i] = mergedColor;
                    palette.RemoveAt(j);
                    merged = true;
                    break;
                }
                if (merged) break;
            }
        } while (merged && palette.Count > 2);
    }

    private static Bitmap ApplyPalette(Bitmap source, List<Color> palette, QuantizationDitherMode dither, QuantizationMergeLevel mergeLevel)
    {
        var src = Copy32bppArgb(source);
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var srcData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(srcData.Stride) * src.Height;
            byte[] srcBuffer = new byte[bytes];
            byte[] dstBuffer = new byte[bytes];
            Marshal.Copy(srcData.Scan0, srcBuffer, 0, bytes);

            // 6-bit cache (2^18 = 262,144 entries) で色境界の精度を向上
            var nearestCache = new Dictionary<int, int>(65536);

            // 色統合レベルに応じてディザ強度を減衰させる係数
            float mergeFactor = mergeLevel switch
            {
                QuantizationMergeLevel.Weak => 0.8f,
                QuantizationMergeLevel.Medium => 0.5f,
                QuantizationMergeLevel.Strong => 0.2f,
                _ => 1.0f
            };

            if (dither == QuantizationDitherMode.None)
            {
                for (int y = 0; y < src.Height; y++)
                {
                    int row = y * srcData.Stride;
                    for (int x = 0; x < src.Width; x++)
                    {
                        int i = row + (x * 4);
                        byte a = srcBuffer[i + 3];
                        if (a == 0)
                        {
                            dstBuffer[i + 3] = 0;
                            continue;
                        }
                        int idx = FindNearestIndexCached(palette, srcBuffer[i + 2], srcBuffer[i + 1], srcBuffer[i + 0], nearestCache);
                        var c = palette[idx];
                        dstBuffer[i + 0] = c.B;
                        dstBuffer[i + 1] = c.G;
                        dstBuffer[i + 2] = c.R;
                        dstBuffer[i + 3] = a;
                    }
                }
            }
            else if (dither == QuantizationDitherMode.OrderedBayer4x4 || dither == QuantizationDitherMode.VoidAndClusterBlueNoise)
            {
                bool useBlueNoise = dither == QuantizationDitherMode.VoidAndClusterBlueNoise;
                for (int y = 0; y < src.Height; y++)
                {
                    int row = y * srcData.Stride;
                    for (int x = 0; x < src.Width; x++)
                    {
                        int i = row + (x * 4);
                        byte a = srcBuffer[i + 3];
                        if (a == 0)
                        {
                            dstBuffer[i + 3] = 0;
                            continue;
                        }
                        int t = useBlueNoise
                            ? BlueNoise8x8[(y & 7) * 8 + (x & 7)] - 32
                            : Bayer4x4[(y & 3) * 4 + (x & 3)] - 8;
                        // 任意パレットでの色飛びを抑えるため、RGB直接加算の強度を抑制 (BlueNoiseは1, Bayerは2)
                        int baseStrength = useBlueNoise ? 1 : 2;
                        float strength = baseStrength * mergeFactor;
                        int r = Clamp(srcBuffer[i + 2] + (int)(t * strength));
                        int g = Clamp(srcBuffer[i + 1] + (int)(t * strength));
                        int b = Clamp(srcBuffer[i + 0] + (int)(t * strength));
                        int idx = FindNearestIndexCached(palette, r, g, b, nearestCache);
                        var c = palette[idx];
                        dstBuffer[i + 0] = c.B;
                        dstBuffer[i + 1] = c.G;
                        dstBuffer[i + 2] = c.R;
                        dstBuffer[i + 3] = a;
                    }
                }
            }
            else
            {
                ApplyErrorDiffusion(srcBuffer, dstBuffer, src.Width, src.Height, srcData.Stride, palette, nearestCache, dither, mergeLevel);
            }

            Marshal.Copy(dstBuffer, 0, dstData.Scan0, bytes);
        }
        finally
        {
            src.UnlockBits(srcData);
            dst.UnlockBits(dstData);
            src.Dispose();
        }
        return dst;
    }

    private static void ApplyErrorDiffusion(byte[] src, byte[] dst, int width, int height, int stride, List<Color> palette, Dictionary<int, int> nearestCache, QuantizationDitherMode mode, QuantizationMergeLevel mergeLevel)
    {
        float[] errR = new float[width * height];
        float[] errG = new float[width * height];
        float[] errB = new float[width * height];

        // 色統合レベルに応じてディザ強度を減衰させる係数
        float mergeFactor = mergeLevel switch
        {
            QuantizationMergeLevel.Weak => 0.8f,
            QuantizationMergeLevel.Medium => 0.5f,
            QuantizationMergeLevel.Strong => 0.2f,
            _ => 1.0f
        };

        // 拡散強度の調整：自然（Atkinson）は弱め、階調優先（SierraLite）は中程度に設定
        float baseStrength = mode switch
        {
            QuantizationDitherMode.Atkinson => 0.45f,
            QuantizationDitherMode.SierraLite => 0.65f,
            QuantizationDitherMode.BlueNoiseErrorDiffusion => 0.50f,
            _ => 0.60f
        };
        float strength = baseStrength * mergeFactor;

        for (int y = 0; y < height; y++)
        {
            // イラストの輪郭汚れを抑えるため serpentine scan (蛇行走査) を強制適用
            bool rev = (y % 2 == 1);
            int xStart = rev ? width - 1 : 0;
            int xEnd = rev ? -1 : width;
            int xStep = rev ? -1 : 1;
            for (int x = xStart; x != xEnd; x += xStep)
            {
                int i = y * stride + x * 4;
                byte a = src[i + 3];
                if (a < 128) // 透明部分には誤差を拡散せず、透明として扱う
                {
                    dst[i + 3] = 0;
                    continue;
                }
                int idxFlat = y * width + x;
                int noise = (mode == QuantizationDitherMode.BlueNoiseErrorDiffusion)
                    ? BlueNoise8x8[(y & 7) * 8 + (x & 7)] - 32
                    : 0;

                // 蓄積された誤差に強度を掛けて適用
                int r = Clamp((int)Math.Round(src[i + 2] + errR[idxFlat] * strength + noise * 0.5f));
                int g = Clamp((int)Math.Round(src[i + 1] + errG[idxFlat] * strength + noise * 0.5f));
                int b = Clamp((int)Math.Round(src[i + 0] + errB[idxFlat] * strength + noise * 0.5f));
                int n = FindNearestIndexCached(palette, r, g, b, nearestCache);
                Color c = palette[n];
                dst[i + 0] = c.B;
                dst[i + 1] = c.G;
                dst[i + 2] = c.R;
                dst[i + 3] = a;

                float dr = (r - c.R);
                float dg = (g - c.G);
                float db = (b - c.B);

                if (mode == QuantizationDitherMode.Atkinson)
                {
                    int dir = rev ? -1 : 1;
                    float factor = 1f / 8f;

                    AddErr(errR, width, height, x + dir, y, dr * factor);
                    AddErr(errG, width, height, x + dir, y, dg * factor);
                    AddErr(errB, width, height, x + dir, y, db * factor);

                    AddErr(errR, width, height, x + (dir * 2), y, dr * factor);
                    AddErr(errG, width, height, x + (dir * 2), y, dg * factor);
                    AddErr(errB, width, height, x + (dir * 2), y, db * factor);

                    AddErr(errR, width, height, x - dir, y + 1, dr * factor);
                    AddErr(errG, width, height, x - dir, y + 1, dg * factor);
                    AddErr(errB, width, height, x - dir, y + 1, db * factor);

                    AddErr(errR, width, height, x, y + 1, dr * factor);
                    AddErr(errG, width, height, x, y + 1, dg * factor);
                    AddErr(errB, width, height, x, y + 1, db * factor);

                    AddErr(errR, width, height, x + dir, y + 1, dr * factor);
                    AddErr(errG, width, height, x + dir, y + 1, dg * factor);
                    AddErr(errB, width, height, x + dir, y + 1, db * factor);

                    AddErr(errR, width, height, x, y + 2, dr * factor);
                    AddErr(errG, width, height, x, y + 2, dg * factor);
                    AddErr(errB, width, height, x, y + 2, db * factor);
                }
                else if (mode == QuantizationDitherMode.SierraLite)
                {
                    int dir = rev ? -1 : 1;
                    AddErr(errR, width, height, x + dir, y, dr * 0.5f);
                    AddErr(errG, width, height, x + dir, y, dg * 0.5f);
                    AddErr(errB, width, height, x + dir, y, db * 0.5f);
                    AddErr(errR, width, height, x - dir, y + 1, dr * 0.25f);
                    AddErr(errG, width, height, x - dir, y + 1, dg * 0.25f);
                    AddErr(errB, width, height, x - dir, y + 1, db * 0.25f);
                    AddErr(errR, width, height, x, y + 1, dr * 0.25f);
                    AddErr(errG, width, height, x, y + 1, dg * 0.25f);
                    AddErr(errB, width, height, x, y + 1, db * 0.25f);
                }
                else
                {
                    int dir = rev ? -1 : 1;
                    AddErr(errR, width, height, x + dir, y, dr * (7f / 16f));
                    AddErr(errG, width, height, x + dir, y, dg * (7f / 16f));
                    AddErr(errB, width, height, x + dir, y, db * (7f / 16f));

                    AddErr(errR, width, height, x - dir, y + 1, dr * (3f / 16f));
                    AddErr(errG, width, height, x - dir, y + 1, dg * (3f / 16f));
                    AddErr(errB, width, height, x - dir, y + 1, db * (3f / 16f));

                    AddErr(errR, width, height, x, y + 1, dr * (5f / 16f));
                    AddErr(errG, width, height, x, y + 1, dg * (5f / 16f));
                    AddErr(errB, width, height, x, y + 1, db * (5f / 16f));

                    AddErr(errR, width, height, x + dir, y + 1, dr * (1f / 16f));
                    AddErr(errG, width, height, x + dir, y + 1, dg * (1f / 16f));
                    AddErr(errB, width, height, x + dir, y + 1, db * (1f / 16f));
                }
            }
        }
    }

    private static void AddErr(float[] target, int width, int height, int x, int y, float v)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return;
        int idx = y * width + x;
        // 誤差の蓄積を制限し、極端な粒状ノイズ（特に黒い点）が発生するのを抑える
        target[idx] = Math.Clamp(target[idx] + v, -128f, 128f);
    }

    private static int FindNearestIndexCached(List<Color> palette, int r, int g, int b, Dictionary<int, int> cache)
    {
        // 6-bit (0-63) 単位でキャッシュキーを作成し、境界の精度を確保
        int cacheKey = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
        if (cache.TryGetValue(cacheKey, out int idx))
        {
            return idx;
        }
        int best = 0;
        long bestDist = long.MaxValue;
        for (int i = 0; i < palette.Count; i++)
        {
            var p = palette[i];
            int dr = r - p.R;
            int dg = g - p.G;
            int db = b - p.B;
            // 知覚的な感度に基づいた重み付き距離計算 (Gを重く、Bを軽く)
            // 金髪や肌色が赤へ吸われないよう色相ズレを抑制
            long d = (long)(dr * dr * 3) + (long)(dg * dg * 4) + (long)(db * db * 2);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        cache[cacheKey] = best;
        return best;
    }

    private static Bitmap Copy32bppArgb(Bitmap source)
    {
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        return copy;
    }

    private static int Clamp(int value) => Math.Max(0, Math.Min(255, value));
}
