using System.Diagnostics;

namespace MidFD.Services;

public sealed class VideoPlaybackLaunchResult
{
    public bool Success { get; init; }
    public bool UsedFfplay { get; init; }
    public bool UsedDefaultApp { get; init; }
    public bool ExitedImmediately { get; init; }
    public int? ExitCode { get; init; }
    public int? ProcessId { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? ProcessError { get; init; }
    public string? ResolvedExecutablePath { get; init; }
    public string? ResolvedFfmpegPath { get; init; }
    public int AppliedVolumePercent { get; init; } = 100;
    public int AppliedStartSeconds { get; init; }
    public IReadOnlyList<string> FfplayCandidates { get; init; } = Array.Empty<string>();
    public string? ErrorMessage { get; init; }
}

public static class VideoPlaybackLaunchService
{
    private const int ImmediateExitCheckMilliseconds = 700;

    public static VideoPlaybackLaunchResult Launch(string videoPath, string? configuredVideoToolDirectory, int volumePercent, int? startSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return new VideoPlaybackLaunchResult
            {
                Success = false,
                ErrorMessage = "動画ファイルが見つかりません。"
            };
        }

        int clampedVolume = Math.Clamp(volumePercent, 0, 100);
        int clampedStartSeconds = Math.Max(0, startSeconds ?? 0);
        VideoToolResolutionResult toolResolution = VideoToolResolutionService.Resolve(configuredVideoToolDirectory);
        string? ffplayPath = toolResolution.FfplayPath;
        if (!string.IsNullOrWhiteSpace(ffplayPath))
        {
            if (!string.Equals(Path.GetFileName(ffplayPath), "ffplay.exe", StringComparison.OrdinalIgnoreCase))
            {
                LogService.Warn($"[VideoPlayback] Tool resolution failed: Specified path is not ffplay.exe. Path='{ffplayPath}'");
                return new VideoPlaybackLaunchResult
                {
                    Success = false,
                    UsedFfplay = true,
                    ResolvedExecutablePath = ffplayPath,
                    ResolvedFfmpegPath = toolResolution.FfmpegPath,
                    AppliedVolumePercent = clampedVolume,
                    AppliedStartSeconds = clampedStartSeconds,
                    FfplayCandidates = toolResolution.FfplayCandidates,
                    ErrorMessage = "ffplay.exe の起動に失敗しました（設定されたツールが ffplay.exe ではありません）。"
                };
            }

            string workingDirectory = Path.GetDirectoryName(ffplayPath) ?? string.Empty;
            LogService.Info($"[VideoPlayback.External.Start] video='{videoPath}' ffplay='{ffplayPath}' volume={clampedVolume} startSeconds={clampedStartSeconds} workingDir='{workingDirectory}' UseShellExecute=false CreateNoWindow=false");
            try
            {
                List<string> primaryArgs =
                [
                    "-hide_banner",
                    "-loglevel",
                    "warning",
                    "-autoexit",
                    "-nostdin",
                    "-volume",
                    clampedVolume.ToString()
                ];
                if (clampedStartSeconds > 0)
                {
                    primaryArgs.Add("-ss");
                    primaryArgs.Add(clampedStartSeconds.ToString());
                }
                primaryArgs.Add(videoPath);
                FfplayLaunchAttempt primaryAttempt = TryLaunchFfplay(ffplayPath, workingDirectory, primaryArgs);
                if (primaryAttempt.Started && !primaryAttempt.ExitedImmediately)
                {
                    LogService.Info($"[VideoPlayback.External.Started] Primary attempt success. pid={primaryAttempt.ProcessId} exitedWithinProbe={primaryAttempt.ExitedImmediately}");
                    return BuildSuccessResult(primaryAttempt, ffplayPath, workingDirectory, toolResolution, clampedVolume, clampedStartSeconds);
                }

                LogService.Warn($"[VideoPlayback.External.Fallback] Primary attempt failed or exited immediately (started={primaryAttempt.Started}, exited={primaryAttempt.ExitedImmediately}, exitCode={primaryAttempt.ExitCode}). Trying fallback arguments.");

                // D&D 相当の最小引数で再試行して、起動オプション差分を吸収する。
                string[] fallbackArgs =
                    clampedStartSeconds > 0
                        ? ["-autoexit", "-volume", clampedVolume.ToString(), "-ss", clampedStartSeconds.ToString(), videoPath]
                        : ["-autoexit", "-volume", clampedVolume.ToString(), videoPath];
                FfplayLaunchAttempt fallbackAttempt = TryLaunchFfplay(ffplayPath, workingDirectory, fallbackArgs);
                if (fallbackAttempt.Started && !fallbackAttempt.ExitedImmediately)
                {
                    LogService.Info($"[VideoPlayback.External.Started] Fallback attempt success. pid={fallbackAttempt.ProcessId} exitedWithinProbe={fallbackAttempt.ExitedImmediately}");
                    return BuildSuccessResult(fallbackAttempt, ffplayPath, workingDirectory, toolResolution, clampedVolume, clampedStartSeconds);
                }

                FfplayLaunchAttempt failedAttempt = fallbackAttempt.Started ? fallbackAttempt : primaryAttempt;
                LogService.Error($"[VideoPlayback.External.Failed] All launch attempts failed. Started={failedAttempt.Started} exitedImmediately={failedAttempt.ExitedImmediately} exitCode={failedAttempt.ExitCode}");
                return new VideoPlaybackLaunchResult
                {
                    Success = false,
                    UsedFfplay = true,
                    ExitedImmediately = failedAttempt.ExitedImmediately,
                    ExitCode = failedAttempt.ExitCode,
                    ProcessId = failedAttempt.ProcessId,
                    WorkingDirectory = workingDirectory,
                    ResolvedExecutablePath = ffplayPath,
                    ResolvedFfmpegPath = toolResolution.FfmpegPath,
                    AppliedVolumePercent = clampedVolume,
                    AppliedStartSeconds = clampedStartSeconds,
                    FfplayCandidates = toolResolution.FfplayCandidates,
                    ErrorMessage = failedAttempt.Started
                        ? "ffplay.exe は起動しましたが、すぐに終了しました。"
                        : "ffplay.exe の起動に失敗しました。"
                };
            }
            catch (Exception ex)
            {
                LogService.Error($"[VideoPlayback.External.Error] Exception during ffplay start: {ex.Message}", ex);
                return new VideoPlaybackLaunchResult
                {
                    Success = false,
                    UsedFfplay = true,
                    WorkingDirectory = workingDirectory,
                    ResolvedExecutablePath = ffplayPath,
                    ResolvedFfmpegPath = toolResolution.FfmpegPath,
                    AppliedVolumePercent = clampedVolume,
                    AppliedStartSeconds = clampedStartSeconds,
                    FfplayCandidates = toolResolution.FfplayCandidates,
                    ProcessError = ex.ToString(),
                    ErrorMessage = $"ffplay.exe の起動に失敗しました: {ex.Message}"
                };
            }
        }

        LogService.Info($"[VideoPlayback.External.DefaultAppFallback.Start] ffplay.exe not resolved. Attempting default app shell execute for video='{videoPath}'");
        try
        {
            var shellStart = new ProcessStartInfo
            {
                FileName = videoPath,
                UseShellExecute = true
            };
            Process.Start(shellStart);
            LogService.Info("[VideoPlayback.External.DefaultAppFallback.Success] Default app process started.");
            return new VideoPlaybackLaunchResult
            {
                Success = true,
                UsedDefaultApp = true,
                ResolvedFfmpegPath = toolResolution.FfmpegPath,
                AppliedVolumePercent = clampedVolume,
                AppliedStartSeconds = clampedStartSeconds,
                FfplayCandidates = toolResolution.FfplayCandidates,
                ErrorMessage = "ffplay not found; used default app fallback."
            };
        }
        catch (Exception ex)
        {
            LogService.Error($"[VideoPlayback.External.DefaultAppFallback.Error] Failed to launch video via default app: {ex.Message}", ex);
            return new VideoPlaybackLaunchResult
            {
                Success = false,
                UsedDefaultApp = true,
                ResolvedFfmpegPath = toolResolution.FfmpegPath,
                AppliedVolumePercent = clampedVolume,
                AppliedStartSeconds = clampedStartSeconds,
                FfplayCandidates = toolResolution.FfplayCandidates,
                ErrorMessage = $"既定アプリで動画を開けませんでした: {ex.Message}"
            };
        }
    }

    private static VideoPlaybackLaunchResult BuildSuccessResult(
        FfplayLaunchAttempt attempt,
        string ffplayPath,
        string workingDirectory,
        VideoToolResolutionResult toolResolution,
        int appliedVolumePercent,
        int appliedStartSeconds)
    {
        return new VideoPlaybackLaunchResult
        {
            Success = true,
            UsedFfplay = true,
            ProcessId = attempt.ProcessId,
            WorkingDirectory = workingDirectory,
            ResolvedExecutablePath = ffplayPath,
            ResolvedFfmpegPath = toolResolution.FfmpegPath,
            AppliedVolumePercent = appliedVolumePercent,
            AppliedStartSeconds = appliedStartSeconds,
            FfplayCandidates = toolResolution.FfplayCandidates
        };
    }

    private static FfplayLaunchAttempt TryLaunchFfplay(string ffplayPath, string workingDirectory, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffplayPath,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            RedirectStandardInput = false
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            return new FfplayLaunchAttempt { Started = false };
        }

        bool exitedImmediately = process.WaitForExit(ImmediateExitCheckMilliseconds);
        int? exitCode = exitedImmediately && process.HasExited ? process.ExitCode : null;
        return new FfplayLaunchAttempt
        {
            Started = true,
            ProcessId = process.Id,
            ExitedImmediately = exitedImmediately,
            ExitCode = exitCode
        };
    }

    private sealed class FfplayLaunchAttempt
    {
        public bool Started { get; init; }
        public int? ProcessId { get; init; }
        public bool ExitedImmediately { get; init; }
        public int? ExitCode { get; init; }
    }
}
