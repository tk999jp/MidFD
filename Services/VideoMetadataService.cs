using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

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

    public static async Task<VideoDurationResult> TryGetDurationSecondsAsync(
        string videoPath,
        string? configuredFfmpegPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return new VideoDurationResult { Success = false, ErrorMessage = "動画ファイルが見つかりません。" };
        }

        string key;
        try
        {
            var info = new FileInfo(videoPath);
            key = $"{videoPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex)
        {
            return new VideoDurationResult { Success = false, ErrorMessage = ex.Message };
        }

        if (DurationCache.TryGetValue(key, out double cached))
        {
            return new VideoDurationResult { Success = true, FromCache = true, DurationSeconds = cached };
        }

        VideoToolResolutionResult tools = VideoToolResolutionService.Resolve(configuredFfmpegPath);
        if (!tools.FfprobeFound || string.IsNullOrWhiteSpace(tools.FfprobePath))
        {
            return new VideoDurationResult { Success = false, ErrorMessage = "ffprobe 未検出" };
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
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
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
                return new VideoDurationResult { Success = false, ErrorMessage = "ffprobe timeout" };
            }

            await waitTask;
            string stdout = (await stdoutTask).Trim();
            string stderr = (await stderrTask).Trim();
            if (process.ExitCode != 0)
            {
                return new VideoDurationResult { Success = false, ErrorMessage = string.IsNullOrWhiteSpace(stderr) ? $"ffprobe exit={process.ExitCode}" : stderr };
            }

            if (!double.TryParse(stdout, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0)
            {
                return new VideoDurationResult { Success = false, ErrorMessage = "duration parse failed" };
            }

            DurationCache[key] = seconds;
            return new VideoDurationResult { Success = true, DurationSeconds = seconds };
        }
        catch (OperationCanceledException)
        {
            return new VideoDurationResult { Success = false, ErrorMessage = "duration canceled" };
        }
        catch (Exception ex)
        {
            return new VideoDurationResult { Success = false, ErrorMessage = ex.Message };
        }
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
