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

public static class PreviewService
{
    internal const int LargeTextThresholdBytes = 2 * 1024 * 1024; // 2MB
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".webm", ".mkv"
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
        if (string.IsNullOrEmpty(path)) return PreviewKind.None;
        if (Directory.Exists(path)) return PreviewKind.None;
        if (!File.Exists(path)) return PreviewKind.None;

        if (ImagePreviewService.IsSupportedExtension(path))
        {
            return PreviewKind.Image;
        }

        if (IsSupportedVideoExtension(path))
        {
            return PreviewKind.Video;
        }

        if (IsBinaryFastPathExtension(path))
        {
            LogService.Info($"[PreviewKind] fastPath=Binary path='{path}'");
            return PreviewKind.Binary;
        }

        if (ExternalToolService.IsEditorTargetExtension(path))
        {
            return (IsLargeFile(path) || HasLongLine(path)) ? PreviewKind.LargeText : PreviewKind.Text;
        }

        // Phase 3-viewer-fix2: 未知の拡張子でも内容からテキストか判定を試みる
        if (IsLikelyText(path))
        {
            return (IsLargeFile(path) || HasLongLine(path)) ? PreviewKind.LargeText : PreviewKind.Text;
        }

        // 依然として不明な場合はバイナリ扱い
        return PreviewKind.Binary;
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

        if (IsSupportedVideoExtension(path))
        {
            return PreviewKind.Video;
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
        return VideoExtensions.Contains(ext);
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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var fs = File.OpenRead(path);
        if (fs.Length == 0)
        {
            return new LargeTextEncodingDetectionResult
            {
                Encoding = Encoding.UTF8,
                EncodingLabel = "UTF-8",
                HasBom = false,
                Reason = "empty-file"
            };
        }

        int readSize = (int)Math.Min(fs.Length, sampleBytes);
        byte[] buffer = new byte[readSize];
        int readCount = fs.Read(buffer, 0, readSize);
        if (readCount <= 0)
        {
            return new LargeTextEncodingDetectionResult
            {
                Encoding = Encoding.UTF8,
                EncodingLabel = "UTF-8",
                HasBom = false,
                Reason = "empty-sample"
            };
        }

        if (readCount >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            return new LargeTextEncodingDetectionResult
            {
                Encoding = Encoding.UTF8,
                EncodingLabel = "UTF-8 BOM",
                HasBom = true,
                IsLongLineDetected = HasLongLine(path),
                Reason = "bom-utf8"
            };
        }

        if (readCount >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            return new LargeTextEncodingDetectionResult
            {
                Encoding = Encoding.Unicode,
                EncodingLabel = "UTF-16 LE",
                HasBom = true,
                IsLongLineDetected = HasLongLine(path),
                Reason = "bom-utf16le"
            };
        }

        if (readCount >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            return new LargeTextEncodingDetectionResult
            {
                Encoding = Encoding.BigEndianUnicode,
                EncodingLabel = "UTF-16 BE",
                HasBom = true,
                IsLongLineDetected = HasLongLine(path),
                Reason = "bom-utf16be"
            };
        }

        if (IsBinaryLikeSample(buffer, readCount))
        {
            return new LargeTextEncodingDetectionResult
            {
                Encoding = Encoding.UTF8,
                EncodingLabel = "Binary-like",
                IsBinaryLike = true,
                Reason = "binary-like-sample"
            };
        }

        if (TryDecodeAsUtf8Strict(buffer, readCount))
        {
            return new LargeTextEncodingDetectionResult
            {
                Encoding = Encoding.UTF8,
                EncodingLabel = "UTF-8",
                HasBom = false,
                IsLongLineDetected = HasLongLine(path),
                Reason = "utf8-strict-ok"
            };
        }

        return new LargeTextEncodingDetectionResult
        {
            Encoding = Encoding.GetEncoding(932),
            EncodingLabel = "CP932",
            HasBom = false,
            IsLongLineDetected = HasLongLine(path),
            Reason = "cp932-fallback"
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
                int readCount = fs.Read(buffer, 0, bytesToRead);

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
                    return NormalizeNewlinesForViewer(System.Text.Encoding.Unicode.GetString(buffer, 2, readCount - 2))
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
