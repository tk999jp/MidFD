using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MidFD.Services;

public sealed class VideoStillPreviewResult
{
    public bool Success { get; init; }
    public bool FromCache { get; init; }
    public string? ImagePath { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class VideoStillPreviewService
{
    private const int DefaultTimeoutMilliseconds = 15000;

    public static string GetDefaultCacheDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "MidFD", "video-still-preview-cache");
    }

    public static string? ResolveFfmpegExecutable(string? configuredPath)
    {
        return VideoToolResolutionService.Resolve(configuredPath).FfmpegPath;
    }

    public static async Task<VideoStillPreviewResult> GenerateStillAsync(
        string videoPath,
        int seconds,
        string? configuredFfmpegPath,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = "動画ファイルが見つかりません。"
            };
        }

        VideoToolResolutionResult toolResolution = VideoToolResolutionService.Resolve(configuredFfmpegPath);
        string? ffmpegPath = toolResolution.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = "ffmpeg.exe が未設定のため、動画定点プレビューを生成できません。"
            };
        }

        try
        {
            Directory.CreateDirectory(cacheDirectory);
        }
        catch (Exception ex)
        {
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = $"キャッシュフォルダを作成できません: {ex.Message}"
            };
        }

        seconds = Math.Clamp(seconds, 0, 36000);
        string outputPath = BuildCachePath(videoPath, seconds, cacheDirectory);
        if (File.Exists(outputPath))
        {
            return new VideoStillPreviewResult
            {
                Success = true,
                FromCache = true,
                ImagePath = outputPath
            };
        }

        string tempOutputPath = $"{outputPath}.{Guid.NewGuid():N}.tmp.png";
        TryDeleteFile(tempOutputPath);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        processStartInfo.ArgumentList.Add("-hide_banner");
        processStartInfo.ArgumentList.Add("-loglevel");
        processStartInfo.ArgumentList.Add("error");
        processStartInfo.ArgumentList.Add("-ss");
        processStartInfo.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        processStartInfo.ArgumentList.Add("-i");
        processStartInfo.ArgumentList.Add(videoPath);
        processStartInfo.ArgumentList.Add("-frames:v");
        processStartInfo.ArgumentList.Add("1");
        processStartInfo.ArgumentList.Add("-an");
        processStartInfo.ArgumentList.Add("-sn");
        processStartInfo.ArgumentList.Add("-y");
        processStartInfo.ArgumentList.Add(tempOutputPath);

        using var process = new Process { StartInfo = processStartInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = $"ffmpeg 実行に失敗しました: {ex.Message}"
            };
        }

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        Task waitForExitTask = process.WaitForExitAsync(cancellationToken);

        Task completed = await Task.WhenAny(waitForExitTask, Task.Delay(DefaultTimeoutMilliseconds, cancellationToken));
        if (completed != waitForExitTask)
        {
            TryKillProcess(process);
            string timeoutError = await standardErrorTask;
            TryDeleteFile(tempOutputPath);
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(timeoutError)
                    ? "動画定点プレビュー生成がタイムアウトしました。"
                    : timeoutError.Trim()
            };
        }

        await waitForExitTask;
        string stdErr = await standardErrorTask;
        _ = await standardOutputTask;

        if (cancellationToken.IsCancellationRequested)
        {
            TryDeleteFile(tempOutputPath);
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = "動画定点プレビュー生成を中断しました。"
            };
        }

        if (process.ExitCode != 0)
        {
            TryDeleteFile(tempOutputPath);
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(stdErr)
                    ? $"ffmpeg 実行エラー (code={process.ExitCode})"
                    : stdErr.Trim()
            };
        }

        if (!File.Exists(tempOutputPath))
        {
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = "動画定点プレビュー画像を生成できませんでした。"
            };
        }

        try
        {
            File.Move(tempOutputPath, outputPath, overwrite: true);
        }
        catch (IOException) when (File.Exists(outputPath))
        {
            TryDeleteFile(tempOutputPath);
            return new VideoStillPreviewResult
            {
                Success = true,
                FromCache = true,
                ImagePath = outputPath
            };
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempOutputPath);
            return new VideoStillPreviewResult
            {
                Success = false,
                ErrorMessage = $"プレビュー画像の保存に失敗しました: {ex.Message}"
            };
        }

        return new VideoStillPreviewResult
        {
            Success = true,
            FromCache = false,
            ImagePath = outputPath
        };
    }

    private static string BuildCachePath(string videoPath, int seconds, string cacheDirectory)
    {
        var fileInfo = new FileInfo(videoPath);
        string key = $"{videoPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}|{seconds}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string hash = Convert.ToHexString(bytes).ToLowerInvariant();
        return Path.Combine(cacheDirectory, $"{hash}.png");
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // no-op
        }
    }
}
