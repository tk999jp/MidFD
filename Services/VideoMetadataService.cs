using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MidFD.Services;

public sealed class VideoDurationResult
{
    public bool Success { get; init; }
    public bool FromCache { get; init; }
    public double DurationSeconds { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class VideoMetadataService
{
    private const int TimeoutMilliseconds = 5000;
    private static readonly ConcurrentDictionary<string, double> DurationCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, VideoMetadataDetails> DetailsCache = new(StringComparer.OrdinalIgnoreCase);

    public sealed class VideoMetadataDetails
    {
        public bool Success { get; init; }
        public bool FromCache { get; init; }
        public double? DurationSeconds { get; init; }
        public string? FormatName { get; init; }
        public string? FormatLongName { get; init; }
        public string? VideoCodec { get; init; }
        public string? AudioCodec { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public double? FrameRate { get; init; }
        public long? BitRate { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public static async Task<VideoDurationResult> TryGetDurationSecondsAsync(
        string videoPath,
        string? configuredVideoToolDirectory,
        CancellationToken cancellationToken)
    {
        VideoMetadataDetails details = await TryGetDetailsAsync(videoPath, configuredVideoToolDirectory, cancellationToken);
        if (details.Success && details.DurationSeconds is > 0)
        {
            return new VideoDurationResult
            {
                Success = true,
                FromCache = details.FromCache,
                DurationSeconds = details.DurationSeconds.Value
            };
        }

        return new VideoDurationResult
        {
            Success = false,
            ErrorMessage = details.ErrorMessage
        };
    }

    public static async Task<VideoMetadataDetails> TryGetDetailsAsync(
        string videoPath,
        string? configuredVideoToolDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return new VideoMetadataDetails { Success = false, ErrorMessage = "動画ファイルが見つかりません。" };
        }

        string key;
        try
        {
            var info = new FileInfo(videoPath);
            key = $"{videoPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex)
        {
            return new VideoMetadataDetails { Success = false, ErrorMessage = ex.Message };
        }

        if (DetailsCache.TryGetValue(key, out VideoMetadataDetails? cachedDetails) && cachedDetails != null)
        {
            return new VideoMetadataDetails
            {
                Success = cachedDetails.Success,
                FromCache = true,
                DurationSeconds = cachedDetails.DurationSeconds,
                FormatName = cachedDetails.FormatName,
                FormatLongName = cachedDetails.FormatLongName,
                VideoCodec = cachedDetails.VideoCodec,
                AudioCodec = cachedDetails.AudioCodec,
                Width = cachedDetails.Width,
                Height = cachedDetails.Height,
                FrameRate = cachedDetails.FrameRate,
                BitRate = cachedDetails.BitRate,
                ErrorMessage = cachedDetails.ErrorMessage
            };
        }

        VideoToolResolutionResult tools = VideoToolResolutionService.Resolve(configuredVideoToolDirectory);
        if (!tools.FfprobeFound || string.IsNullOrWhiteSpace(tools.FfprobePath))
        {
            return new VideoMetadataDetails { Success = false, ErrorMessage = "ffprobe 未検出" };
        }

        var psi = new ProcessStartInfo
        {
            FileName = tools.FfprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(tools.FfprobePath) ?? string.Empty
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(videoPath);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task waitTask = process.WaitForExitAsync(cancellationToken);
            Task completed = await Task.WhenAny(waitTask, Task.Delay(TimeoutMilliseconds, cancellationToken));
            if (completed != waitTask)
            {
                TryKillProcess(process);
                return new VideoMetadataDetails { Success = false, ErrorMessage = "ffprobe timeout" };
            }

            await waitTask;
            string stdout = (await stdoutTask).Trim();
            string stderr = (await stderrTask).Trim();
            if (process.ExitCode != 0)
            {
                return new VideoMetadataDetails { Success = false, ErrorMessage = string.IsNullOrWhiteSpace(stderr) ? $"ffprobe exit={process.ExitCode}" : stderr };
            }

            VideoMetadataDetails details = ParseVideoMetadata(stdout);
            if (!details.Success)
            {
                return details;
            }

            DetailsCache[key] = details;
            if (details.DurationSeconds is > 0)
            {
                DurationCache[key] = details.DurationSeconds.Value;
            }

            return details;
        }
        catch (OperationCanceledException)
        {
            return new VideoMetadataDetails { Success = false, ErrorMessage = "duration canceled" };
        }
        catch (Exception ex)
        {
            return new VideoMetadataDetails { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static VideoMetadataDetails ParseVideoMetadata(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? formatName = null;
            string? formatLongName = null;
            double? durationSeconds = null;
            long? bitRate = null;
            string? videoCodec = null;
            string? audioCodec = null;
            int? width = null;
            int? height = null;
            double? frameRate = null;

            if (root.TryGetProperty("format", out JsonElement format))
            {
                formatName = GetStringOrNull(format, "format_name");
                formatLongName = GetStringOrNull(format, "format_long_name");
                durationSeconds = TryParseDoubleInvariant(GetStringOrNull(format, "duration"));
                bitRate = TryParseLongInvariant(GetStringOrNull(format, "bit_rate"));
            }

            if (root.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    string? codecType = GetStringOrNull(stream, "codec_type");
                    if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        videoCodec ??= GetStringOrNull(stream, "codec_name");
                        width ??= GetIntOrNull(stream, "width");
                        height ??= GetIntOrNull(stream, "height");
                        frameRate ??= ParseFrameRate(stream);
                    }
                    else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        audioCodec ??= GetStringOrNull(stream, "codec_name");
                    }
                }
            }

            return new VideoMetadataDetails
            {
                Success = true,
                DurationSeconds = durationSeconds,
                FormatName = formatName,
                FormatLongName = formatLongName,
                VideoCodec = videoCodec,
                AudioCodec = audioCodec,
                Width = width,
                Height = height,
                FrameRate = frameRate,
                BitRate = bitRate
            };
        }
        catch (Exception ex)
        {
            return new VideoMetadataDetails
            {
                Success = false,
                ErrorMessage = $"ffprobe json parse failed: {ex.Message}"
            };
        }
    }

    private static string? GetStringOrNull(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null
        };
    }

    private static int? GetIntOrNull(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int value))
        {
            return value;
        }

        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return null;
    }

    private static double? TryParseDoubleInvariant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static long? TryParseLongInvariant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;
    }

    private static double? ParseFrameRate(JsonElement stream)
    {
        string? frameRateRaw = GetStringOrNull(stream, "avg_frame_rate");
        if (string.IsNullOrWhiteSpace(frameRateRaw) || string.Equals(frameRateRaw, "0/0", StringComparison.Ordinal))
        {
            frameRateRaw = GetStringOrNull(stream, "r_frame_rate");
        }

        if (string.IsNullOrWhiteSpace(frameRateRaw))
        {
            return null;
        }

        string[] parts = frameRateRaw.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
            denominator > 0)
        {
            return numerator / denominator;
        }

        return TryParseDoubleInvariant(frameRateRaw);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // no-op
        }
    }
}
