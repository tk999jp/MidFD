using System.Text;
using System.Text.RegularExpressions;
#if MIDPLAYER_WITH_LIBVLC
using LibVLCSharp.Shared;
#endif

namespace MidFD;

internal sealed record IgnoredSidecarSubtitle(string Path, string Reason);

internal sealed record SubtitleSidecarResolution(
    string? SupportedPath,
    IReadOnlyList<IgnoredSidecarSubtitle> IgnoredSidecars);

internal static class SubtitleSidecarResolver
{
    internal const string DisableAutoDetectOption = ":no-sub-autodetect-file";
    internal const uint ExplicitSubtitlePriority = 4;

    private const int ProbeLimitBytes = 64 * 1024;

    // LibVLC 3.0.24's SLAVE_SPU_EXTENSIONS. Keep the gate aligned with the
    // formats that the current engine has historically auto-detected.
    private static readonly string[] SidecarExtensions =
    [
        "aqt", "ass", "cdg", "dks", "idx", "jss", "mpl2", "mpsub", "pjs", "psb",
        "rt", "sami", "sbv", "scc", "smi", "srt", "ssa", "stl", "sub", "tt",
        "ttml", "usf", "vtt", "webvtt"
    ];

    private static readonly Regex SrtTiming = new(
        @"^\s*(?<start>\d{2}:\d{2}:\d{2},\d{3})\s+-->\s+(?<end>\d{2}:\d{2}:\d{2},\d{3})(?:\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VttTiming = new(
        @"^\s*(?:(?:\d{2}:)?\d{2}:\d{2})\.\d{3}\s+-->\s+(?:(?:\d{2}:)?\d{2}:\d{2})\.\d{3}(?:\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SubViewerTiming = new(
        @"^\s*\d{2}:\d{2}:\d{2}\.(?<start>\d{1,3}),\d{2}:\d{2}:\d{2}\.(?<end>\d{1,3})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ymm4SubTiming = new(
        @"^\s*\d{2}:\d{2}:\d{2}\.\d{7,},\d{2}:\d{2}:\d{2}\.\d{7,}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MicroDvdCue = new(
        @"^\s*\{(?<start>\d+)\}\{(?<end>\d*)\}(?<text>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Mpl2Cue = new(
        @"^\s*\[(?<start>\d+)\]\[(?<end>\d*)\](?<text>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static Task<SubtitleSidecarResolution> ResolveAsync(string videoPath, CancellationToken cancellationToken)
        => Task.Run(() => Resolve(videoPath, cancellationToken), cancellationToken);

    // The codec probe already runs on a worker. Keep the same resolver contract
    // without queuing another worker before the selected backend is loaded.
    internal static SubtitleSidecarResolution ResolveForPlaybackProbe(string videoPath, CancellationToken cancellationToken)
        => Resolve(videoPath, cancellationToken);

#if MIDPLAYER_WITH_LIBVLC
    internal static bool ConfigureMedia(Media media, SubtitleSidecarResolution resolution)
        => ConfigureMedia(
            resolution,
            option => media.AddOption(option),
            (priority, uri) => media.AddSlave(MediaSlaveType.Subtitle, priority, uri));
#endif

    internal static bool ConfigureMedia(
        SubtitleSidecarResolution resolution,
        Action<string> addOption,
        Func<uint, Uri, bool> addSlave)
    {
        ArgumentNullException.ThrowIfNull(addOption);
        ArgumentNullException.ThrowIfNull(addSlave);
        addOption(DisableAutoDetectOption);
        if (resolution.SupportedPath is not { Length: > 0 } subtitlePath)
        {
            return true;
        }

        var subtitleUri = new Uri(Path.GetFullPath(subtitlePath), UriKind.Absolute);
        return addSlave(ExplicitSubtitlePriority, subtitleUri);
    }

    private static SubtitleSidecarResolution Resolve(string videoPath, CancellationToken cancellationToken)
    {
        var ignored = new List<IgnoredSidecarSubtitle>();
        string fullVideoPath;
        try
        {
            fullVideoPath = Path.GetFullPath(videoPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SubtitleSidecarResolution(null, ignored);
        }

        string? directory = Path.GetDirectoryName(fullVideoPath);
        if (string.IsNullOrEmpty(directory))
        {
            return new SubtitleSidecarResolution(null, ignored);
        }

        var candidates = new List<string>(SidecarExtensions.Length);
        foreach (string extension in SidecarExtensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidate = Path.ChangeExtension(fullVideoPath, "." + extension);
            if (File.Exists(candidate))
            {
                candidates.Add(candidate);
            }
        }

        // LibVLC assigns exact basename matches the same top match priority and
        // alphabetically sorts them. Validate in that order and retain only the
        // first usable match, which is the existing default subtitle selection.
        candidates.Sort(StringComparer.CurrentCulture);

        foreach (string candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                ValidationResult validation = Validate(candidate, cancellationToken);
                if (validation.IsSupported)
                {
                    return new SubtitleSidecarResolution(candidate, ignored);
                }

                ignored.Add(new IgnoredSidecarSubtitle(candidate, validation.Reason));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                ignored.Add(new IgnoredSidecarSubtitle(candidate, $"ReadFailure:{ex.GetType().Name}"));
            }
        }

        return new SubtitleSidecarResolution(null, ignored);
    }

    private static ValidationResult Validate(string path, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".idx")
        {
            return ValidateVobSubIndex(path, cancellationToken);
        }
        if (extension == ".stl")
        {
            return ValidateEbuStl(path, cancellationToken);
        }
        if (extension == ".cdg")
        {
            return ValidateCdg(path, cancellationToken);
        }

        PrefixRead prefix = ReadPrefix(path, ProbeLimitBytes, cancellationToken);
        string content = DecodeTextPrefix(prefix.Bytes);
        if (content.Length == 0 || content.Contains('\0'))
        {
            return ValidationResult.Unsupported;
        }

        string[] lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        return extension switch
        {
            ".srt" => IsSrt(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".vtt" or ".webvtt" => IsWebVtt(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".ass" or ".ssa" => IsAssOrSsa(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".smi" or ".sami" => IsSami(content) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".sub" => IsSupportedSub(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".mpl2" => lines.Any(line => Mpl2Cue.IsMatch(line)) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".mpsub" => IsMpSub(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".aqt" => IsAqt(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".dks" => IsDks(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".jss" => IsJacoSub(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".pjs" => IsPhoenixSub(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".psb" => IsPowerDivx(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".rt" => IsRealText(content) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".sbv" => IsSbv(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".scc" => IsScc(lines) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".tt" or ".ttml" => IsTtml(content) ? ValidationResult.Supported : ValidationResult.Unsupported,
            ".usf" => IsUsf(content) ? ValidationResult.Supported : ValidationResult.Unsupported,
            _ => ValidationResult.Unsupported
        };
    }

    private static ValidationResult ValidateVobSubIndex(string indexPath, CancellationToken cancellationToken)
    {
        PrefixRead index = ReadPrefix(indexPath, ProbeLimitBytes, cancellationToken);
        string text = DecodeTextPrefix(index.Bytes);
        string subPath = Path.ChangeExtension(indexPath, ".sub");
        if (!Regex.IsMatch(text, @"(?im)^\s*#\s*VobSub index file,\s*v\d+\s*$")
            || !Regex.IsMatch(text, @"(?im)^\s*id:\s*[^,]+,\s*index:\s*\d+\s*$")
            || !Regex.IsMatch(text, @"(?im)^\s*timestamp:\s*\d{2}:\d{2}:\d{2}:\d{3}\s+filepos:\s*[0-9a-f]+\s*$")
            || !File.Exists(subPath))
        {
            return ValidationResult.Unsupported;
        }

        PrefixRead sub = ReadPrefix(subPath, 4096, cancellationToken);
        if (sub.FileLength <= 0 || !ContainsMpegPackStartCode(sub.Bytes))
        {
            return ValidationResult.Unsupported;
        }

        return ValidationResult.Supported;
    }

    private static ValidationResult ValidateEbuStl(string path, CancellationToken cancellationToken)
    {
        PrefixRead prefix = ReadPrefix(path, 1024, cancellationToken);
        if (prefix.Bytes.Length < 1024 || prefix.FileLength < 1152 || prefix.FileLength % 128 != 0)
        {
            return ValidationResult.Unsupported;
        }

        string header = Encoding.ASCII.GetString(prefix.Bytes, 0, 1024);
        string codePage = header[..3];
        string diskFormat = header.Substring(3, 8);
        char displayStandard = header[11];
        string characterTable = header.Substring(12, 2);
        string blockCountText = header.Substring(238, 5);
        if (!Regex.IsMatch(codePage, @"^\d{3}$")
            || (diskFormat != "STL25.01" && diskFormat != "STL30.01")
            || (displayStandard != ' ' && displayStandard is not ('0' or '1' or '2'))
            || !Regex.IsMatch(characterTable, @"^0[0-4]$")
            || !int.TryParse(blockCountText, out int blockCount)
            || blockCount <= 0
            || prefix.FileLength != 1024L + blockCount * 128L)
        {
            return ValidationResult.Unsupported;
        }

        return ValidationResult.Supported;
    }

    private static ValidationResult ValidateCdg(string path, CancellationToken cancellationToken)
    {
        PrefixRead prefix = ReadPrefix(path, 24 * 8, cancellationToken);
        if (prefix.FileLength < 24 || prefix.FileLength % 24 != 0 || prefix.Bytes.Length < 24)
        {
            return ValidationResult.Unsupported;
        }

        for (int offset = 0; offset + 24 <= prefix.Bytes.Length; offset += 24)
        {
            byte command = (byte)(prefix.Bytes[offset] & 0x3f);
            byte instruction = (byte)(prefix.Bytes[offset + 1] & 0x3f);
            if (command == 0x09 && instruction is 0x01 or 0x02 or 0x06 or 0x14 or 0x18)
            {
                return ValidationResult.Supported;
            }
        }

        return ValidationResult.Unsupported;
    }

    private static bool IsSrt(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            if (SrtTiming.IsMatch(lines[index]) && HasCueBody(lines, index + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsWebVtt(string[] lines)
    {
        if (lines.Length == 0 || !Regex.IsMatch(lines[0].TrimStart('\uFEFF'), @"^WEBVTT(?:\s|$)", RegexOptions.IgnoreCase))
        {
            return false;
        }

        for (int index = 1; index < lines.Length; index++)
        {
            if (VttTiming.IsMatch(lines[index]) && HasCueBody(lines, index + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAssOrSsa(string[] lines)
    {
        bool scriptInfo = false;
        bool events = false;
        bool format = false;
        bool dialogue = false;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            scriptInfo |= trimmed.Equals("[Script Info]", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("!: This is a Sub Station Alpha v1", StringComparison.OrdinalIgnoreCase);
            events |= trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
            format |= events && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("Start", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("End", StringComparison.OrdinalIgnoreCase)
                && trimmed.Contains("Text", StringComparison.OrdinalIgnoreCase);
            dialogue |= events && trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)
                && trimmed.Split(',').Length >= 3;
        }
        return scriptInfo && events && format && dialogue;
    }

    private static bool IsSami(string content)
        => Regex.IsMatch(content, @"<SAMI(?:\s|>)", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, @"<SYNC\b[^>]*\bStart\s*=", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, @"<P(?:\s|>)", RegexOptions.IgnoreCase);

    private static bool IsSupportedSub(string[] lines)
    {
        bool microDvdCue = false;
        foreach (string line in lines)
        {
            Match cue = MicroDvdCue.Match(line);
            if (!cue.Success)
            {
                continue;
            }

            string text = cue.Groups["text"].Value.Trim();
            if (cue.Groups["start"].Value == "1"
                && cue.Groups["end"].Value == "1"
                && Regex.IsMatch(text, @"^\d+(?:\.\d+)?$"))
            {
                continue; // MicroDVD's optional frame-rate header.
            }
            microDvdCue = text.Length > 0;
            if (microDvdCue)
            {
                return true;
            }
        }

        if (microDvdCue || lines.Any(line => Mpl2Cue.IsMatch(line)))
        {
            return true;
        }

        for (int index = 0; index < lines.Length; index++)
        {
            Match timing = SubViewerTiming.Match(lines[index]);
            if (!timing.Success)
            {
                continue;
            }

            // YMM4's 7-digit timestamp precision and speaker marker are not
            // SubViewer cues; LibVLC otherwise mistakes the first line for SBV.
            if (Ymm4SubTiming.IsMatch(lines[index]))
            {
                return false;
            }

            string? body = FirstNonEmptyLine(lines, index + 1);
            if (body == null || body.TrimStart().StartsWith(">>", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static bool IsMpSub(string[] lines)
    {
        bool hasFormat = lines.Any(line => Regex.IsMatch(line.Trim(), @"^FORMAT=(?:TIME|\d+)$", RegexOptions.IgnoreCase));
        if (!hasFormat)
        {
            return false;
        }

        for (int index = 0; index < lines.Length; index++)
        {
            if (Regex.IsMatch(lines[index].Trim(), @"^\d+(?:\.\d+)?\s+\d+(?:\.\d+)?$")
                && HasCueBody(lines, index + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAqt(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            if (Regex.IsMatch(lines[index].Trim(), @"^-->>\s*\d+$") && HasCueBody(lines, index + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsDks(string[] lines)
        => lines.Any(line => Regex.IsMatch(line, @"^\s*\[\d{1,2}:\d{1,2}:\d{1,2}\].+\S"));

    private static bool IsJacoSub(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            bool cue = Regex.IsMatch(lines[index].Trim(), @"^@\d+\s+@\d+(?:\s+.*)?$")
                || Regex.IsMatch(lines[index].Trim(), @"^\d{1,2}:\d{2}:\d{2}\.\d+\s+\d{1,2}:\d{2}:\d{2}(?:\.\d+)?(?:\s+.*)?$");
            if (cue && HasCueBody(lines, index + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPhoenixSub(string[] lines)
        => lines.Any(line => Regex.IsMatch(line, @"^\s*\d+\s*,\s*\d+\s*,\s*\S.+$"));

    private static bool IsPowerDivx(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            if (Regex.IsMatch(lines[index].Trim(), @"^\{\d{1,2}:\d{2}:\d{2}\}$") && HasCueBody(lines, index + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsRealText(string content)
        => Regex.IsMatch(content, @"<time\b[^>]*(?:begin|start)\s*=", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, @"</time>|<clear\b", RegexOptions.IgnoreCase);

    private static bool IsSbv(string[] lines)
    {
        var timing = new Regex(
            @"^\s*\d{1,2}:\d{2}:\d{2}\.\d{1,3},\d{1,2}:\d{2}:\d{2}\.\d{1,3}\s*$",
            RegexOptions.CultureInvariant);
        for (int index = 0; index < lines.Length; index++)
        {
            if (timing.IsMatch(lines[index]) && HasCueBody(lines, index + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsScc(string[] lines)
    {
        if (!lines.Any(line => line.Trim().Equals("Scenarist_SCC V1.0", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        return lines.Any(line => Regex.IsMatch(line.Trim(), @"^\d{2}:\d{2}:\d{2}:\d{2}\s+(?:[0-9a-f]{4}\s*)+$", RegexOptions.IgnoreCase));
    }

    private static bool IsTtml(string content)
        => Regex.IsMatch(content, @"<tt(?:\s|>)", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, @"<p\b[^>]*\bbegin\s*=", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, @"<p\b[^>]*(?:\bend\s*=|\bdur\s*=)", RegexOptions.IgnoreCase);

    private static bool IsUsf(string content)
        => Regex.IsMatch(content, @"<USFSubtitles(?:\s|>)", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, @"<subtitle\b[^>]*\bstart\s*=", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, @"<subtitle\b[^>]*\b(?:stop|end)\s*=", RegexOptions.IgnoreCase);

    private static bool HasCueBody(string[] lines, Regex cuePattern)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            if (cuePattern.IsMatch(lines[index]) && HasCueBody(lines, index + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasCueBody(string[] lines, int startIndex)
        => FirstNonEmptyLine(lines, startIndex) is { } body && body.Trim().Length > 0;

    private static string? FirstNonEmptyLine(string[] lines, int startIndex)
    {
        for (int index = startIndex; index < lines.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(lines[index]))
            {
                return lines[index];
            }
        }
        return null;
    }

    private static bool ContainsMpegPackStartCode(byte[] bytes)
    {
        for (int index = 0; index + 3 < bytes.Length; index++)
        {
            if (bytes[index] == 0x00 && bytes[index + 1] == 0x00 && bytes[index + 2] == 0x01 && bytes[index + 3] == 0xBA)
            {
                return true;
            }
        }
        return false;
    }

    private static PrefixRead ReadPrefix(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
        long fileLength = stream.Length;
        byte[] bytes = new byte[(int)Math.Min(Math.Max(fileLength, 0), maximumBytes)];
        int total = 0;
        while (total < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(bytes, total, bytes.Length - total);
            if (read <= 0)
            {
                break;
            }
            total += read;
        }

        if (total != bytes.Length)
        {
            Array.Resize(ref bytes, total);
        }
        return new PrefixRead(bytes, fileLength);
    }

    private static string DecodeTextPrefix(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return Encoding.UTF8.GetString(bytes, Encoding.UTF8.Preamble.Length, bytes.Length - Encoding.UTF8.Preamble.Length);
        }
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode.GetString(bytes, Encoding.Unicode.Preamble.Length, bytes.Length - Encoding.Unicode.Preamble.Length);
        }
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, Encoding.BigEndianUnicode.Preamble.Length, bytes.Length - Encoding.BigEndianUnicode.Preamble.Length);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private readonly record struct PrefixRead(byte[] Bytes, long FileLength);

    private readonly record struct ValidationResult(bool IsSupported, string Reason)
    {
        internal static ValidationResult Supported => new(true, string.Empty);
        internal static ValidationResult Unsupported => new(false, "UnsupportedFormat");
    }
}
