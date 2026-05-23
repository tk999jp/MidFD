using MidFD.Models;

namespace MidFD.Services;

public static class ArchiveExtractService
{
    public static string ResolveDestinationDirectory(string baseDirectory, string archivePath, bool createArchiveRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return string.Empty;
        }

        string normalizedBase = Path.GetFullPath(baseDirectory.Trim().Trim('"'));
        if (!createArchiveRootDirectory)
        {
            return EnsureSafeExtractDestinationDirectory(normalizedBase);
        }

        string archiveFolderName = Path.GetFileNameWithoutExtension(archivePath);
        if (string.IsNullOrWhiteSpace(archiveFolderName))
        {
            return normalizedBase;
        }

        string dest = Path.Combine(normalizedBase, archiveFolderName);
        return EnsureSafeExtractDestinationDirectory(dest);
    }

    public static string EnsureSafeExtractDestinationDirectory(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) return destinationDirectory;

        // 既にディレクトリとして存在していればOK（既存ディレクトリへのマージは許容）
        if (Directory.Exists(destinationDirectory)) return destinationDirectory;

        // ファイルとして存在していなければOK（CreateDirectory で作成可能）
        if (!File.Exists(destinationDirectory)) return destinationDirectory;

        // ファイル名と衝突している！ 代替名を生成する
        string parent = Path.GetDirectoryName(destinationDirectory) ?? "";
        string originalName = Path.GetFileName(destinationDirectory);

        string candidate = Path.Combine(parent, originalName + "_extracted");
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            LogService.Info($"ArchiveExtractService: Collision detected with file '{destinationDirectory}'. Redirecting to '{candidate}'.");
            return candidate;
        }

        int i = 1;
        while (true)
        {
            candidate = Path.Combine(parent, $"{originalName}_extracted_{i}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                LogService.Info($"ArchiveExtractService: Collision detected with file '{destinationDirectory}'. Redirecting to '{candidate}'.");
                return candidate;
            }
            i++;
            if (i > 1000) break; // safety break
        }

        return candidate;
    }

    public static ArchiveExtractResult ExtractSelection(
        string? configuredSevenZipPath,
        ArchiveExtractRequest request,
        CancellationToken token = default,
        Action<string>? onOutputLine = null)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ArchivePath) || !File.Exists(request.ArchivePath))
        {
            return new ArchiveExtractResult
            {
                Success = false,
                ErrorMessage = "archive が見つかりません。",
                DestinationDirectory = request.DestinationDirectory
            };
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            return new ArchiveExtractResult
            {
                Success = false,
                ErrorMessage = "解凍先フォルダが指定されていません。",
                DestinationDirectory = request.DestinationDirectory
            };
        }

        string[] entryPaths = request.EntryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!request.ExtractAll && entryPaths.Length == 0)
        {
            return new ArchiveExtractResult
            {
                Success = false,
                ErrorMessage = "解凍対象が選択されていません。",
                DestinationDirectory = request.DestinationDirectory
            };
        }

        string? sevenZipPath = SevenZipService.ResolveExecutable(configuredSevenZipPath);
        if (string.IsNullOrWhiteSpace(sevenZipPath))
        {
            LogService.Warn($"ArchiveExtractService: 7-Zip not found. Checking Tar fallback. Archive={request.ArchivePath}");

            // ZIP は既存の ZipFallbackService を優先
            bool isZip = string.Equals(Path.GetExtension(request.ArchivePath), ".zip", StringComparison.OrdinalIgnoreCase);
            if (isZip)
            {
                try
                {
                    Directory.CreateDirectory(request.DestinationDirectory);
                    var entriesToExtract = request.ExtractAll ? null : entryPaths;
                    ZipFallbackService.Unpack(
                        request.ArchivePath,
                        request.DestinationDirectory,
                        entriesToExtract,
                        token,
                        onOutputLine);
                    return new ArchiveExtractResult
                    {
                        Success = true,
                        DestinationDirectory = request.DestinationDirectory,
                        ExtractedEntryCount = request.ExtractAll ? 0 : entryPaths.Length
                    };
                }
                catch (Exception ex)
                {
                    return new ArchiveExtractResult { Success = false, ErrorMessage = $"zip 解凍に失敗しました: {ex.Message}", DestinationDirectory = request.DestinationDirectory };
                }
            }

            // それ以外は TarFallbackService を試す
            if (TarFallbackService.IsAvailable())
            {
                try
                {
                    Directory.CreateDirectory(request.DestinationDirectory);

                    var entriesToExtract = request.ExtractAll ? null : entryPaths;
                    var res = TarFallbackService.Unpack(request.ArchivePath, request.DestinationDirectory, entriesToExtract, token, onOutputLine);
                    
                    if (res.ExitCode == 0)
                    {
                        return new ArchiveExtractResult { Success = true, DestinationDirectory = request.DestinationDirectory };
                    }

                    string errorMsg = res.Error ?? string.Empty;
                    string displayMsg = $"Windows 標準機能での解凍に失敗しました (ExitCode:{res.ExitCode})。";

                    // すでに具体的な日本語エラー（展開先フォルダ...など）が含まれている場合はそれを優先
                    if (errorMsg.Contains("展開先フォルダ"))
                    {
                        displayMsg += $"\n{errorMsg}";
                    }
                    else
                    {
                        displayMsg += "\n暗号化や分割アーカイブの可能性があります。\n\n" + errorMsg;
                    }

                    return new ArchiveExtractResult
                    {
                        Success = false,
                        ErrorMessage = displayMsg,
                        DestinationDirectory = request.DestinationDirectory
                    };
                }
                catch (Exception ex)
                {
                    return new ArchiveExtractResult { Success = false, ErrorMessage = $"解凍エラー: {ex.Message}", DestinationDirectory = request.DestinationDirectory };
                }
            }

            return new ArchiveExtractResult
            {
                Success = false,
                ErrorMessage = SevenZipService.BuildUnavailableMessage(configuredSevenZipPath, "archive を解凍"),
                DestinationDirectory = request.DestinationDirectory,
                SevenZipPath = configuredSevenZipPath
            };
        }

        try
        {
            Directory.CreateDirectory(request.DestinationDirectory);

            (int ExitCode, string Output, string Error) commandResult = request.ExtractAll
                ? SevenZipService.Unpack(sevenZipPath, request.ArchivePath, request.DestinationDirectory, token, onOutputLine)
                : SevenZipService.ExtractSelection(sevenZipPath, request.ArchivePath, request.DestinationDirectory, entryPaths, token, onOutputLine);

            if (commandResult.ExitCode != 0)
            {
                LogService.Error($"ArchiveExtractService: extract failed. Archive={request.ArchivePath}\nError: {commandResult.Error}");
                return new ArchiveExtractResult
                {
                    Success = false,
                    ErrorMessage = "archive 解凍に失敗しました。",
                    DestinationDirectory = request.DestinationDirectory,
                    SevenZipPath = sevenZipPath,
                    ExtractedEntryCount = request.ExtractAll ? 0 : entryPaths.Length
                };
            }

            return new ArchiveExtractResult
            {
                Success = true,
                DestinationDirectory = request.DestinationDirectory,
                SevenZipPath = sevenZipPath,
                ExtractedEntryCount = request.ExtractAll ? 0 : entryPaths.Length
            };
        }
        catch (Exception ex)
        {
            LogService.Error($"ArchiveExtractService: unexpected failure. Archive={request.ArchivePath}", ex);
            return new ArchiveExtractResult
            {
                Success = false,
                ErrorMessage = $"archive 解凍に失敗しました: {ex.Message}",
                DestinationDirectory = request.DestinationDirectory,
                SevenZipPath = sevenZipPath,
                ExtractedEntryCount = request.ExtractAll ? 0 : entryPaths.Length
            };
        }
    }

}
