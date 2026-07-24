using System.IO;
using System.Text;

namespace MidFD.Services;

public sealed class LargeTextEncodingDetectionResult
{
    public required Encoding Encoding { get; init; }
    public required string EncodingLabel { get; init; }
    public bool HasBom { get; init; }
    public bool IsBinaryLike { get; init; }
    public bool IsEncodingUnsupportedForLargeText { get; init; }
    public bool IsLongLineDetected { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class TextPreviewProbeResult
{
    public long ObservedLength { get; init; }
    public int RequestedBytes { get; init; }
    public int ReadCount { get; init; }
    public byte[] Sample { get; init; } = Array.Empty<byte>();
    public bool HasBom { get; init; }
    public Encoding Encoding { get; init; } = Encoding.UTF8;
    public string EncodingLabel { get; init; } = "UTF-8";
    public bool Utf8StrictValid { get; init; }
    public int NulCount { get; init; }
    public double NulRatio { get; init; }
    public int ControlCount { get; init; }
    public double ControlRatio { get; init; }
    public bool HasLongLine { get; init; }
    public bool UseLargeText { get; init; }
    public bool IsBinaryLike { get; init; }
    public bool TextPositive { get; init; }
    public bool ReadFailed { get; init; }
    public int RetryCount { get; init; }
    public bool ObservationInconsistent { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public static class PreviewService
{
    internal const int LargeTextThresholdBytes = 2 * 1024 * 1024; // 2MB
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown"
    };
    private static readonly HashSet<string> SqliteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3"
    };
    private static readonly HashSet<string> VideoFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".webm", ".mkv"
    };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    };
    private static readonly HashSet<string> BinaryFastPathExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".wim", ".iso",
        ".zip", ".7z", ".rar", ".cab",
        ".pptx", ".xlsx", ".docx", ".ppt", ".xls", ".doc",
        ".pdf"
    };

    public static PreviewKind GetPreviewKind(string path)
    {
        return GetPreviewKind(path, out _);
    }

    public static PreviewKind GetPreviewKind(string path, out TextPreviewProbeResult? probe)
    {
        probe = null;
        if (string.IsNullOrEmpty(path)) return PreviewKind.None;
        if (Directory.Exists(path)) return PreviewKind.None;
        if (!File.Exists(path)) return PreviewKind.None;

        if (ImagePreviewService.IsSupportedExtension(path))
        {
            return PreviewKind.Image;
        }

        if (IsSupportedMediaExtension(path))
        {
            return PreviewKind.Video;
        }

        if (IsMarkdownExtension(path))
        {
            return PreviewKind.Markdown;
        }

        if (IsSqliteExtension(path))
        {
            return PreviewKind.Sqlite;
        }

        if (IsBinaryFastPathExtension(path))
        {
            LogService.Info($"[PreviewKind] fastPath=Binary path='{path}'");
            return PreviewKind.Binary;
        }

        if (ExternalToolService.IsEditorTargetExtension(path))
        {
            probe = ProbeTextPreviewWithRetry(path);
            return ResolveTextPreviewKind(probe);
        }

        // Phase 3-viewer-fix2: 未知の拡張子でも内容からテキストか判定を試みる
        probe = ProbeTextPreviewWithRetry(path);
        if (probe.TextPositive)
        {
            return ResolveTextPreviewKind(probe);
        }

        // 依然として不明な場合はバイナリ扱い
        return PreviewKind.Binary;
    }

    public static PreviewKind ResolveTextPreviewKind(TextPreviewProbeResult probe)
    {
        return probe.ReadFailed || !probe.TextPositive
            ? PreviewKind.Binary
            : probe.UseLargeText ? PreviewKind.LargeText : PreviewKind.Text;
    }

    public static PreviewKind GetPreviewKindShallow(string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || isDirectory)
        {
            return PreviewKind.None;
        }

        string ext = Path.GetExtension(path);
        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewKind.None;
        }

        if (ImagePreviewService.IsSupportedExtension(path))
        {
            return PreviewKind.Image;
        }

        if (IsSupportedMediaExtension(path))
        {
            return PreviewKind.Video;
        }

        if (IsMarkdownExtension(path))
        {
            return PreviewKind.Markdown;
        }

        if (IsSqliteExtension(path))
        {
            return PreviewKind.Sqlite;
        }

        if (IsBinaryFastPathExtension(path))
        {
            return PreviewKind.Binary;
        }

        if (ExternalToolService.IsEditorTargetExtension(path))
        {
            return PreviewKind.Text;
        }

        return PreviewKind.None;
    }

    public static bool IsSupportedVideoExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return VideoFileExtensions.Contains(ext);
    }

    public static bool IsSupportedMediaExtension(string path)
    {
        return IsSupportedVideoExtension(path) || IsSupportedAudioExtension(path);
    }

    public static bool IsSupportedAudioExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return AudioExtensions.Contains(ext);
    }

    public static bool IsMarkdownExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return MarkdownExtensions.Contains(ext);
    }

    public static bool IsSqliteExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return SqliteExtensions.Contains(ext);
    }

    private static bool IsBinaryFastPathExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return BinaryFastPathExtensions.Contains(ext);
    }

    /// <summary>
    /// LargeText 向けの文字コード判定。
    /// UTF-8 BOM / UTF-8 no BOM / CP932 / BOM付き UTF-16 を判定する。
    /// </summary>
    public static LargeTextEncodingDetectionResult DetectLargeTextEncoding(string path, int sampleBytes = 128 * 1024)
    {
        TextPreviewProbeResult probe = ProbeTextPreview(path, sampleBytes);
        return new LargeTextEncodingDetectionResult
        {
            Encoding = probe.Encoding,
            EncodingLabel = probe.EncodingLabel,
            HasBom = probe.HasBom,
            IsBinaryLike = probe.IsBinaryLike,
            IsLongLineDetected = probe.HasLongLine,
            Reason = probe.Reason
        };
    }

    public static TextPreviewProbeResult ProbeTextPreview(string path, int requestedBytes = 512 * 1024)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ProbeTextPreview(stream, stream.Length, requestedBytes);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new TextPreviewProbeResult
            {
                RequestedBytes = Math.Max(1, requestedBytes),
                ReadFailed = true,
                Reason = ex is UnauthorizedAccessException ? "read-unauthorized" : "read-error"
            };
        }
    }

    private static TextPreviewProbeResult ProbeTextPreviewWithRetry(string path)
    {
        TextPreviewProbeResult first = ProbeTextPreview(path);
        if (first.ReadFailed || !first.TextPositive || first.ObservedLength <= LargeTextThresholdBytes
            || first.ReadCount >= first.RequestedBytes)
        {
            return first;
        }

        TextPreviewProbeResult second = ProbeTextPreview(path);
        if (second.TextPositive && second.ReadCount == first.ReadCount && second.ReadCount < second.RequestedBytes
            && second.ObservedLength <= LargeTextThresholdBytes)
        {
            return CloneProbe(second, useLargeText: false, retryCount: 1, observationInconsistent: false, reason: "reprobe-stable-small-text");
        }

        if (second.IsBinaryLike && second.ReadCount == first.ReadCount)
        {
            return CloneProbe(second, useLargeText: false, retryCount: 1, observationInconsistent: false, reason: "reprobe-stable-binary-like");
        }

        return CloneProbe(first, useLargeText: false, retryCount: 1, observationInconsistent: true, reason: "reprobe-observation-inconsistent");
    }

    private static TextPreviewProbeResult CloneProbe(
        TextPreviewProbeResult source,
        bool useLargeText,
        int retryCount,
        bool observationInconsistent,
        string reason)
    {
        return new TextPreviewProbeResult
        {
            ObservedLength = source.ObservedLength,
            RequestedBytes = source.RequestedBytes,
            ReadCount = source.ReadCount,
            Sample = source.Sample,
            HasBom = source.HasBom,
            Encoding = source.Encoding,
            EncodingLabel = source.EncodingLabel,
            Utf8StrictValid = source.Utf8StrictValid,
            NulCount = source.NulCount,
            NulRatio = source.NulRatio,
            ControlCount = source.ControlCount,
            ControlRatio = source.ControlRatio,
            HasLongLine = source.HasLongLine,
            UseLargeText = useLargeText,
            IsBinaryLike = source.IsBinaryLike,
            TextPositive = source.TextPositive,
            ReadFailed = source.ReadFailed,
            RetryCount = retryCount,
            ObservationInconsistent = observationInconsistent,
            Reason = reason
        };
    }

    internal static TextPreviewProbeResult ProbeTextPreview(Stream stream, long observedLength, int requestedBytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int requested = Math.Max(1, requestedBytes);
        byte[] buffer = new byte[(int)Math.Min(observedLength, requested)];
        int totalRead = 0;
        try
        {
            totalRead = ReadUpTo(stream, buffer, CancellationToken.None);
        }
        catch (IOException)
        {
            return new TextPreviewProbeResult
            {
                ObservedLength = observedLength,
                RequestedBytes = requested,
                ReadCount = totalRead,
                Sample = buffer[..totalRead],
                ReadFailed = true,
                Reason = "read-error"
            };
        }

        byte[] sample = buffer[..totalRead];
        return AnalyzeTextPreviewSample(observedLength, requested, sample);
    }

    private static TextPreviewProbeResult AnalyzeTextPreviewSample(long observedLength, int requestedBytes, byte[] sample)
    {
        int length = sample.Length;
        bool utf8Bom = length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF;
        bool utf16LeBom = length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE;
        bool utf16BeBom = length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF;
        bool utf16Bom = utf16LeBom || utf16BeBom;
        int nulCount = 0;
        int controlCount = 0;
        int currentLineLength = 0;
        bool hasLongLine = false;
        for (int i = 0; i < length; i++)
        {
            byte value = sample[i];
            if (value == 0) nulCount++;
            if (value < 0x20 && value != 0x09 && value != 0x0A && value != 0x0D && (!utf16Bom || value != 0)) controlCount++;
            if (value == 0x0A)
            {
                currentLineLength = 0;
            }
            else if (value != 0x0D)
            {
                currentLineLength++;
                if (currentLineLength > 32 * 1024)
                {
                    hasLongLine = true;
                }
            }
        }
        if (length >= 32 * 1024 && currentLineLength >= 32 * 1024)
        {
            hasLongLine = true;
        }

        double nulRatio = length == 0 ? 0 : (double)nulCount / length;
        double controlRatio = length == 0 ? 0 : (double)controlCount / length;
        bool utf8Strict = !utf8Bom && !utf16LeBom && !utf16BeBom && TryDecodeAsUtf8Strict(sample, length);
        Encoding encoding = Encoding.UTF8;
        string encodingLabel = "UTF-8";
        string reason = "empty-file";
        bool bom = utf8Bom || utf16LeBom || utf16BeBom;
        bool utf16Valid = true;
        bool utf16ContentBinary = false;
        if (utf8Bom)
        {
            encodingLabel = "UTF-8 BOM";
            reason = "bom-utf8";
        }
        else if (utf16LeBom)
        {
            encoding = new UnicodeEncoding(false, true, true);
            encodingLabel = "UTF-16 LE";
            reason = "bom-utf16le";
            utf16Valid = TryDecodeUtf16(sample, 2, length - 2, observedLength > length, encoding, out string decoded);
            utf16ContentBinary = utf16Valid && IsBinaryTextContent(decoded);
        }
        else if (utf16BeBom)
        {
            encoding = new UnicodeEncoding(true, true, true);
            encodingLabel = "UTF-16 BE";
            reason = "bom-utf16be";
            utf16Valid = TryDecodeUtf16(sample, 2, length - 2, observedLength > length, encoding, out string decoded);
            utf16ContentBinary = utf16Valid && IsBinaryTextContent(decoded);
        }
        else if (utf8Strict)
        {
            reason = "utf8-strict-ok";
        }
        else
        {
            encoding = Encoding.GetEncoding(932);
            encodingLabel = "CP932";
            reason = "cp932-fallback";
        }

        bool rawBinaryEvidence = nulRatio >= 0.02 || controlRatio >= 0.20;
        bool binarySignature = HasKnownBinarySignature(sample, 0, length)
            || (utf16Bom && HasKnownBinarySignature(sample, 2, length - 2));
        bool binaryEvidence = binarySignature || (utf16Bom ? !utf16Valid || utf16ContentBinary : rawBinaryEvidence);
        bool lowBinaryRatios = nulRatio < 0.02 && controlRatio < 0.20;
        bool textPositive = length == 0 || (!binarySignature && utf16Bom && utf16Valid && !utf16ContentBinary)
            || (!utf16Bom && bom && lowBinaryRatios)
            || (!binarySignature && !utf16Bom && lowBinaryRatios && (utf8Strict || encoding.CodePage == 932));
        bool useLargeText = observedLength > LargeTextThresholdBytes || hasLongLine;
        return new TextPreviewProbeResult
        {
            ObservedLength = observedLength,
            RequestedBytes = requestedBytes,
            ReadCount = length,
            Sample = sample,
            HasBom = bom,
            Encoding = encoding,
            EncodingLabel = encodingLabel,
            Utf8StrictValid = utf8Strict,
            NulCount = nulCount,
            NulRatio = nulRatio,
            ControlCount = controlCount,
            ControlRatio = controlRatio,
            HasLongLine = hasLongLine,
            UseLargeText = useLargeText,
            IsBinaryLike = binaryEvidence && !textPositive,
            TextPositive = textPositive,
            Reason = reason
        };
    }

    private static bool IsLargeFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Length > LargeTextThresholdBytes;
        }
        catch { return false; }
    }

    /// <summary>
    /// 通常の TextBox/RichTextBox で表示すると固まる恐れのある長大1行が含まれているか判定する。
    /// </summary>
    private static bool HasLongLine(string path, int sampleBytes = 512 * 1024, int longLineThreshold = 32 * 1024)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int readSize = (int)Math.Min(fs.Length, sampleBytes);
            if (readSize == 0) return false;

            byte[] buffer = new byte[readSize];
            int readCount = fs.Read(buffer, 0, readSize);

            int currentLineLength = 0;
            for (int i = 0; i < readCount; i++)
            {
                byte b = buffer[i];
                if (b == 0x0A) // LF
                {
                    currentLineLength = 0;
                }
                else if (b != 0x0D) // Ignore CR
                {
                    currentLineLength++;
                    if (currentLineLength > longLineThreshold) return true;
                }
            }

            // サンプル範囲内に改行が一度も出現せず、かつサンプルサイズが閾値を超えている場合も長大行とみなす
            if (readCount >= longLineThreshold && currentLineLength >= longLineThreshold) return true;

            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// ファイルの先頭数KBを確認し、テキストファイルらしいか判定する。
    /// BOM、UTF-8 strict、NULL文字、制御文字の出現頻度を考慮する。
    /// </summary>
    private static bool IsLikelyText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length == 0) return true; // 空ファイルはテキストとして扱う

            // 先頭 4KB を読み込む (判定には十分な量)
            byte[] buffer = new byte[Math.Min(fs.Length, 4096)];
            int readCount = fs.Read(buffer, 0, buffer.Length);

            // 1. BOM チェック (UTF-8, UTF-16LE, UTF-16BE)
            if (readCount >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return true;
            if (readCount >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return true;
            if (readCount >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) return true;

            // 2. NULL文字チェック
            // バイナリファイル特有の判定。1つでもあればバイナリとみなす。
            for (int i = 0; i < readCount; i++)
            {
                if (buffer[i] == 0x00) return false;
            }

            // 3. 制御文字の割合チェック
            // TAB(9), LF(10), CR(13) 以外の制御文字 (0-31) をカウント
            int controlChars = 0;
            for (int i = 0; i < readCount; i++)
            {
                byte b = buffer[i];
                if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
                {
                    controlChars++;
                }
            }
            // 制御文字が 10% を超えるならバイナリの可能性が高い (安全策)
            if (readCount > 0 && (double)controlChars / readCount > 0.1) return false;

            // 4. UTF-8 strict チェック (BOMなし UTF-8)
            try
            {
                var utf8Strict = new System.Text.UTF8Encoding(false, true);
                int safeLen = GetSafeUtf8Length(buffer, readCount);
                utf8Strict.GetString(buffer, 0, safeLen);
                return true;
            }
            catch (ArgumentException)
            {
                // UTF-8 でなくても、制御文字が少なければ Text (SJIS等の可能性) とみなす
                // すでにステップ 2, 3 をパスしていればテキストの可能性が高い
                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// テキストプレビューの内容を取得する
    /// </summary>
    public static async Task<string> GetTextPreviewAsync(string path, int maxBytes, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            using (var fs = File.OpenRead(path))
            {
                token.ThrowIfCancellationRequested();

                int bytesToRead = (int)Math.Min(fs.Length, maxBytes);
                byte[] buffer = new byte[bytesToRead];
                int readCount = ReadUpTo(fs, buffer, token);

                token.ThrowIfCancellationRequested();

                // エンコーディング判定
                // 1. BOMチェック (StreamReader の標準機能に相当する処理)
                if (readCount >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                {
                    return NormalizeNewlinesForViewer(System.Text.Encoding.UTF8.GetString(buffer, 3, readCount - 3))
                        + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : "");
                }
                if (readCount >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
                {
                    Encoding encoding = new UnicodeEncoding(false, true, true);
                    if (!TryDecodeUtf16(buffer, 2, readCount - 2, fs.Length > readCount, encoding, out string decoded)) throw new InvalidDataException("Invalid UTF-16 LE preview payload.");
                    return NormalizeNewlinesForViewer(decoded)
                        + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : "");
                }
                if (readCount >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
                {
                    Encoding encoding = new UnicodeEncoding(true, true, true);
                    if (!TryDecodeUtf16(buffer, 2, readCount - 2, fs.Length > readCount, encoding, out string decoded)) throw new InvalidDataException("Invalid UTF-16 BE preview payload.");
                    return NormalizeNewlinesForViewer(decoded)
                        + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : "");
                }

                // 2. BOMなし UTF-8 試行 (例外を投げる設定で厳密に判定)
                try
                {
                    var utf8Strict = new System.Text.UTF8Encoding(false, true);
                    // 読み込み上限境界でマルチバイト文字が切断されている場合に備え、安全な長さまでトリミングする
                    int safeLength = GetSafeUtf8Length(buffer, readCount);
                    string utf8Result = NormalizeNewlinesForViewer(utf8Strict.GetString(buffer, 0, safeLength));
                    return utf8Result + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : "");
                }
                catch (ArgumentException)
                {
                    // UTF-8 として不正なバイトシーケンスが含まれる、または依然として不完全な場合は Shift_JIS フォールバックへ
                }

                // 3. Shift_JIS (CP932) フォールバック
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                var sjis = System.Text.Encoding.GetEncoding("shift_jis");
                return NormalizeNewlinesForViewer(sjis.GetString(buffer, 0, readCount))
                    + (fs.Length > maxBytes ? $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]" : "");
            }
        }, token);
    }

    private static string NormalizeNewlinesForViewer(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        text = text.Replace("\r\n", "\n");
        text = text.Replace('\r', '\n');
        return text.Replace("\n", Environment.NewLine);
    }

    private static int ReadUpTo(Stream stream, byte[] buffer, CancellationToken token)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }

    private static bool TryDecodeUtf16(byte[] buffer, int offset, int byteCount, bool allowTruncatedTail, Encoding encoding, out string decoded)
    {
        decoded = string.Empty;
        int safeLength = byteCount - (byteCount % 2);
        if (safeLength != byteCount && !allowTruncatedTail) return false;
        if (allowTruncatedTail && safeLength >= 2)
        {
            int last = offset + safeLength - 2;
            ushort codeUnit = encoding.CodePage == 1201
                ? (ushort)((buffer[last] << 8) | buffer[last + 1])
                : (ushort)(buffer[last] | (buffer[last + 1] << 8));
            if (char.IsHighSurrogate((char)codeUnit)) safeLength -= 2;
        }
        try
        {
            decoded = encoding.GetString(buffer, offset, safeLength);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsBinaryTextContent(string text)
    {
        if (text.Length == 0) return false;
        int binaryCount = text.Count(static c => c == '\0' || (c < 0x20 && c != '\t' && c != '\n' && c != '\r') || (c >= 0x7F && c <= 0x9F));
        return (double)binaryCount / text.Length >= 0.20;
    }

    private static bool HasKnownBinarySignature(byte[] buffer, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset + length > buffer.Length) return false;
        ReadOnlySpan<byte> sample = buffer.AsSpan(offset, length);
        return HasPrefix(sample, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
            || HasPrefix(sample, [0x50, 0x4B, 0x03, 0x04])
            || HasPrefix(sample, [0x50, 0x4B, 0x05, 0x06])
            || HasPrefix(sample, [0x50, 0x4B, 0x07, 0x08])
            || HasPrefix(sample, [0x25, 0x50, 0x44, 0x46, 0x2D])
            || IsPeHeader(sample);
    }

    private static bool HasPrefix(ReadOnlySpan<byte> sample, byte[] prefix)
    {
        return sample.Length >= prefix.Length && sample[..prefix.Length].SequenceEqual(prefix);
    }

    private static bool IsPeHeader(ReadOnlySpan<byte> sample)
    {
        if (!HasPrefix(sample, [0x4D, 0x5A]) || sample.Length < 0x40) return false;
        int peOffset = sample[0x3C]
            | (sample[0x3D] << 8)
            | (sample[0x3E] << 16)
            | (sample[0x3F] << 24);
        return peOffset >= 0
            && peOffset <= sample.Length - 4
            && sample[peOffset] == 0x50
            && sample[peOffset + 1] == 0x45
            && sample[peOffset + 2] == 0x00
            && sample[peOffset + 3] == 0x00;
    }

    /// <summary>
    /// バイナリプレビュー（HexDump）の内容を取得する
    /// </summary>
    public static async Task<string> GetBinaryPreviewAsync(string path, int maxBytes, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(path);
                
                token.ThrowIfCancellationRequested();

                int len = (int)Math.Min(fs.Length, maxBytes);
                byte[] buf = new byte[len];
                int read = fs.Read(buf, 0, len);
                
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[Binary Dump: {Path.GetFileName(path)} - {(fs.Length > maxBytes ? $"First {maxBytes / 1024}KB" : $"{read} Bytes")}]\n");
                
                for (int i = 0; i < read; i += 16)
                {
                    // 各行構築のタイミングでもキャンセルを拾えるようにする
                    if (i % 512 == 0) token.ThrowIfCancellationRequested();

                    // アドレス部
                    sb.Append($"{i:X8}  ");

                    // 16進数部
                    for (int j = 0; j < 16; j++)
                    {
                        if (i + j < read) sb.Append($"{buf[i + j]:X2} ");
                        else sb.Append("   ");
                        
                        if (j == 7) sb.Append(" ");
                    }
                    
                    sb.Append(" |");
                    
                    // ASCII文字列表現部
                    for (int j = 0; j < 16; j++)
                    {
                        if (i + j < read)
                        {
                            byte b = buf[i + j];
                            sb.Append((b >= 32 && b <= 126) ? (char)b : '.');
                        }
                    }
                    sb.AppendLine("|");
                }

                return sb.ToString();
            }
            catch (IOException)
            {
                return "[プレビュー不可: 使用中またはロックされています]";
            }
            catch (UnauthorizedAccessException)
            {
                return "[プレビュー不可: アクセス権限がありません]";
            }
        }, token);
    }

    /// <summary>
    /// UTF-8 マルチバイト文字の途中で切断されない安全な長さを取得する（バッファ末尾の切り出し境界用）。
    /// </summary>
    private static int GetSafeUtf8Length(byte[] buffer, int length)
    {
        if (length <= 0) return 0;
        // UTF-8 マルチバイトの末尾は最大3バイトまで不完全な可能性がある (4バイト文字の場合)
        // 末尾から最大3バイト遡り、マルチバイト開始バイト (11xxxxxx) を探す
        for (int i = 1; i <= Math.Min(length, 3); i++)
        {
            byte b = buffer[length - i];
            if ((b & 0x80) == 0) return length; // ASCII (0xxxxxxx) なら問題なし
            
            if ((b & 0xC0) == 0xC0) // マルチバイト開始点 (11xxxxxx)
            {
                int expected;
                if      ((b & 0xE0) == 0xC0) expected = 2; // 2バイト形式
                else if ((b & 0xF0) == 0xE0) expected = 3; // 3バイト形式
                else if ((b & 0xF8) == 0xF0) expected = 4; // 4バイト形式
                else return length; // 不明な形式

                // 期待される長さに対して現在のバッファ（i バイト分）が足りなければ、その文字の直前までを有効とする
                return (i < expected) ? (length - i) : length;
            }
            // 継続バイト (10xxxxxx) の場合はさらに前のバイトを確認する
        }
        return length;
    }

    private static bool TryDecodeAsUtf8Strict(byte[] buffer, int length)
    {
        try
        {
            var utf8Strict = new UTF8Encoding(false, true);
            int safeLength = GetSafeUtf8Length(buffer, length);
            utf8Strict.GetString(buffer, 0, safeLength);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsBinaryLikeSample(byte[] buffer, int length)
    {
        if (length <= 0) return false;

        int nulCount = 0;
        int controlCount = 0;
        for (int i = 0; i < length; i++)
        {
            byte b = buffer[i];
            if (b == 0x00) nulCount++;
            if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D) controlCount++;
        }

        double nulRatio = (double)nulCount / length;
        double controlRatio = (double)controlCount / length;
        return nulRatio >= 0.02 || controlRatio >= 0.20;
    }
}
