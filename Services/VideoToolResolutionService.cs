namespace MidFD.Services;

public sealed class VideoToolResolutionResult
{
    public string? FfmpegPath { get; init; }
    public string? FfplayPath { get; init; }
    public string? FfprobePath { get; init; }
    public string FfmpegSource { get; init; } = string.Empty;
    public string FfplaySource { get; init; } = string.Empty;
    public string FfprobeSource { get; init; } = string.Empty;
    public IReadOnlyList<string> FfplayCandidates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FfprobeCandidates { get; init; } = Array.Empty<string>();
    public bool FfmpegFound => !string.IsNullOrWhiteSpace(FfmpegPath);
    public bool FfplayFound => !string.IsNullOrWhiteSpace(FfplayPath);
    public bool FfprobeFound => !string.IsNullOrWhiteSpace(FfprobePath);
}

public static class VideoToolResolutionService
{
    public static VideoToolResolutionResult Resolve(string? configuredFfmpegPath)
    {
        string? configuredPath = string.IsNullOrWhiteSpace(configuredFfmpegPath)
            ? null
            : configuredFfmpegPath.Trim();

        string? ffmpegPath = null;
        string ffmpegSource = string.Empty;

        foreach ((string candidate, string source) in EnumerateFfmpegCandidates(configuredPath))
        {
            if (File.Exists(candidate))
            {
                ffmpegPath = candidate;
                ffmpegSource = source;
                break;
            }
        }

        string? ffplayPath = null;
        string ffplaySource = string.Empty;
        var ffplayCandidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string candidate, string source) in EnumerateFfplayCandidates(configuredPath, ffmpegPath))
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                continue;
            }

            ffplayCandidates.Add(candidate);
            if (ffplayPath is null && File.Exists(candidate))
            {
                ffplayPath = candidate;
                ffplaySource = source;
            }
        }

        string? ffprobePath = null;
        string ffprobeSource = string.Empty;
        var ffprobeCandidates = new List<string>();
        seen.Clear();
        foreach ((string candidate, string source) in EnumerateFfprobeCandidates(configuredPath, ffmpegPath))
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                continue;
            }

            ffprobeCandidates.Add(candidate);
            if (ffprobePath is null && File.Exists(candidate))
            {
                ffprobePath = candidate;
                ffprobeSource = source;
            }
        }

        return new VideoToolResolutionResult
        {
            FfmpegPath = ffmpegPath,
            FfplayPath = ffplayPath,
            FfprobePath = ffprobePath,
            FfmpegSource = ffmpegSource,
            FfplaySource = ffplaySource,
            FfprobeSource = ffprobeSource,
            FfplayCandidates = ffplayCandidates,
            FfprobeCandidates = ffprobeCandidates
        };
    }

    private static IEnumerable<(string Candidate, string Source)> EnumerateFfmpegCandidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                yield return (configuredPath, "指定パス");
            }

            if (Directory.Exists(configuredPath))
            {
                yield return (Path.Combine(configuredPath, "ffmpeg.exe"), "指定フォルダ直下");
                yield return (Path.Combine(configuredPath, "bin", "ffmpeg.exe"), "指定フォルダ/bin");
                yield return (Path.Combine(configuredPath, "ffmpeg", "bin", "ffmpeg.exe"), "指定フォルダ/ffmpeg/bin");
            }
        }

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (string rawPart in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawPart.Trim();
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            yield return (Path.Combine(dir, "ffmpeg.exe"), "PATH");
        }
    }

    private static IEnumerable<(string Candidate, string Source)> EnumerateFfplayCandidates(string? configuredPath, string? resolvedFfmpegPath)
    {
        if (!string.IsNullOrWhiteSpace(resolvedFfmpegPath))
        {
            foreach ((string candidate, string source) in EnumerateSiblingFfplayCandidates(resolvedFfmpegPath, "解決済みffmpeg"))
            {
                yield return (candidate, source);
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                if (string.Equals(Path.GetFileName(configuredPath), "ffplay.exe", StringComparison.OrdinalIgnoreCase))
                {
                    yield return (configuredPath, "指定パス");
                }

                foreach ((string candidate, string source) in EnumerateSiblingFfplayCandidates(configuredPath, "指定ファイル同フォルダ"))
                {
                    yield return (candidate, source);
                }
            }
            else if (Directory.Exists(configuredPath))
            {
                yield return (Path.Combine(configuredPath, "ffplay.exe"), "指定フォルダ直下");
                yield return (Path.Combine(configuredPath, "bin", "ffplay.exe"), "指定フォルダ/bin");
                yield return (Path.Combine(configuredPath, "ffmpeg", "bin", "ffplay.exe"), "指定フォルダ/ffmpeg/bin");
            }
        }

        string baseDir = AppContext.BaseDirectory;
        yield return (Path.Combine(baseDir, "tools", "ffmpeg", "bin", "ffplay.exe"), "AppBase/tools/ffmpeg/bin");
        yield return (Path.Combine(baseDir, "tools", "ffmpeg", "ffplay.exe"), "AppBase/tools/ffmpeg");
        yield return (Path.Combine(baseDir, "ffmpeg", "bin", "ffplay.exe"), "AppBase/ffmpeg/bin");

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (string rawPart in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawPart.Trim();
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            yield return (Path.Combine(dir, "ffplay.exe"), "PATH");
        }
    }

    private static IEnumerable<(string Candidate, string Source)> EnumerateSiblingFfplayCandidates(string path, string sourcePrefix)
    {
        string? directory = File.Exists(path) ? Path.GetDirectoryName(path) : Directory.Exists(path) ? path : null;
        if (string.IsNullOrWhiteSpace(directory))
        {
            yield break;
        }

        yield return (Path.Combine(directory, "ffplay.exe"), $"{sourcePrefix}同フォルダ");
    }

    private static IEnumerable<(string Candidate, string Source)> EnumerateFfprobeCandidates(string? configuredPath, string? resolvedFfmpegPath)
    {
        if (!string.IsNullOrWhiteSpace(resolvedFfmpegPath))
        {
            string? dir = Path.GetDirectoryName(resolvedFfmpegPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                yield return (Path.Combine(dir, "ffprobe.exe"), "解決済みffmpeg同フォルダ");
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
        {
            yield return (Path.Combine(configuredPath, "ffprobe.exe"), "指定フォルダ直下");
            yield return (Path.Combine(configuredPath, "bin", "ffprobe.exe"), "指定フォルダ/bin");
        }

        string baseDir = AppContext.BaseDirectory;
        yield return (Path.Combine(baseDir, "tools", "ffmpeg", "bin", "ffprobe.exe"), "AppBase/tools/ffmpeg/bin");
        yield return (Path.Combine(baseDir, "tools", "ffmpeg", "ffprobe.exe"), "AppBase/tools/ffmpeg");

        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (string rawPart in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawPart.Trim();
            if (!string.IsNullOrWhiteSpace(dir))
            {
                yield return (Path.Combine(dir, "ffprobe.exe"), "PATH");
            }
        }
    }
}
