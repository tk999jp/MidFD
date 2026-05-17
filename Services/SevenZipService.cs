using System.Diagnostics;
using System.IO;
using System.Text;
using MidFD.Models;

namespace MidFD.Services;

public static class SevenZipService
{
    private static readonly string[] SearchPaths = {
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe"
    };

    public static string? FindSevenZip()
    {
        foreach (var path in SearchPaths)
        {
            if (File.Exists(path)) return path;
        }

        // 環境変数からの単純検索は一旦割愛 (最低限の絶対パス優先)
        return null;
    }

    public static string? ResolveExecutable(string? configuredSevenZipPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredSevenZipPath) && File.Exists(configuredSevenZipPath))
        {
            return configuredSevenZipPath;
        }

        if (!string.IsNullOrWhiteSpace(configuredSevenZipPath))
        {
            LogService.Warn($"SevenZipService: configured 7-Zip path not found: {configuredSevenZipPath}");
        }

        return FindSevenZip();
    }

    public static string? ResolveCliExecutable(string? configuredSevenZipPath)
    {
        string? resolvedPath = ResolveExecutable(configuredSevenZipPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        string fileName = Path.GetFileName(resolvedPath);
        if (fileName.Equals("7z.exe", StringComparison.OrdinalIgnoreCase))
        {
            return resolvedPath;
        }

        string? directoryPath = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        string cliPath = Path.Combine(directoryPath, "7z.exe");
        return File.Exists(cliPath) ? cliPath : null;
    }

    public static string? ResolveGuiExecutable(string sevenZipExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(sevenZipExecutablePath))
        {
            return null;
        }

        string? sevenZipDirectory = Path.GetDirectoryName(sevenZipExecutablePath);
        if (string.IsNullOrWhiteSpace(sevenZipDirectory))
        {
            return null;
        }

        string guiPath = Path.Combine(sevenZipDirectory, "7zG.exe");
        return File.Exists(guiPath) ? guiPath : null;
    }

    public static string BuildUnavailableMessage(string? configuredSevenZipPath, string operationLabel)
    {
        return string.IsNullOrWhiteSpace(configuredSevenZipPath)
            ? $"7-Zip が見つからないため {operationLabel}できません。設定 > 外部連携 で 7-Zip パスを指定するか、7-Zip をインストールしてください。"
            : $"設定 > 外部連携 の 7-Zip パスが見つからないため {operationLabel}できません。7-Zip パスを確認するか、7-Zip をインストールしてください。";
    }

    public static IReadOnlyList<string> GetPackOutputArtifacts(string outputArchivePath)
    {
        if (string.IsNullOrWhiteSpace(outputArchivePath))
        {
            return Array.Empty<string>();
        }

        string normalizedPath = Path.GetFullPath(outputArchivePath);
        string? directory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return File.Exists(normalizedPath)
                ? new[] { normalizedPath }
                : Array.Empty<string>();
        }

        string fileName = Path.GetFileName(normalizedPath);
        string baseName = Path.GetFileNameWithoutExtension(normalizedPath);
        string extension = Path.GetExtension(normalizedPath);
        var artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(normalizedPath))
        {
            artifacts.Add(normalizedPath);
        }

        string splitVolumePrefix = fileName + ".";
        foreach (string candidate in Directory.EnumerateFiles(directory, fileName + ".*"))
        {
            string candidateFileName = Path.GetFileName(candidate);
            if (!candidateFileName.StartsWith(splitVolumePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string suffix = candidateFileName[splitVolumePrefix.Length..];
            if (IsNumericVolumeSuffix(suffix))
            {
                artifacts.Add(candidate);
            }
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, baseName + ".z*"))
            {
                if (IsLegacyZipVolumeExtension(Path.GetExtension(candidate)))
                {
                    artifacts.Add(candidate);
                }
            }
        }

        return artifacts
            .OrderBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (int ExitCode, string Output, string Error) ExecuteProcess(
        string executablePath,
        string arguments,
        CancellationToken token = default,
        Action<string>? onOutputLine = null,
        Encoding? outputEncoding = null,
        Encoding? errorEncoding = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = executablePath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = outputEncoding;
        process.StartInfo.StandardErrorEncoding = errorEncoding;

        process.Start();

        return WaitForProcessExit(process, token, onOutputLine, outputEncoding, errorEncoding);
    }

    private static (int ExitCode, string Output, string Error) ExecuteProcess(
        string executablePath,
        IEnumerable<string> argumentList,
        CancellationToken token = default,
        Action<string>? onOutputLine = null,
        Encoding? outputEncoding = null,
        Encoding? errorEncoding = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = executablePath;
        foreach (var arg in argumentList)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = outputEncoding;
        process.StartInfo.StandardErrorEncoding = errorEncoding;

        process.Start();

        return WaitForProcessExit(process, token, onOutputLine, outputEncoding, errorEncoding);
    }

    private static (int ExitCode, string Output, string Error) WaitForProcessExit(
        Process process,
        CancellationToken token,
        Action<string>? onOutputLine,
        Encoding? outputEncoding,
        Encoding? errorEncoding)
    {
        using var registration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to kill 7z process on cancellation: {ex.Message}");
            }
        });

        var outputBuilder = new System.Text.StringBuilder(1024);
        // リアルタイムで行を読み取る
        while (!process.StandardOutput.EndOfStream)
        {
            string? line = process.StandardOutput.ReadLine();
            if (line != null)
            {
                outputBuilder.AppendLine(line);
                onOutputLine?.Invoke(line);
            }
        }

        string output = outputBuilder.ToString();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (token.IsCancellationRequested)
        {
            return (-1, output, "Canceled by user");
        }

        return (process.ExitCode, output, error);
    }

    public static (int ExitCode, string Output, string Error) Pack(string sevenZipPath, List<string> sourcePaths, PackRequest request, CancellationToken token = default, Action<string>? onOutputLine = null)
    {
        if (!File.Exists(sevenZipPath))
        {
            throw new FileNotFoundException("7z.exe が見つかりません。", sevenZipPath);
        }

        string formatArgument = request.Format switch
        {
            PackArchiveFormat.SevenZip => "-t7z",
            PackArchiveFormat.Tar => "-ttar",
            PackArchiveFormat.GZip => "-tgzip",
            PackArchiveFormat.BZip2 => "-tbzip2",
            PackArchiveFormat.Xz => "-txz",
            PackArchiveFormat.Wim => "-twim",
            _ => "-tzip"
        };
        string compressionArgument = request.CompressionLevel switch
        {
            PackCompressionLevel.Store => "-mx=0",
            PackCompressionLevel.Fast => "-mx=1",
            PackCompressionLevel.Maximum => "-mx=9",
            _ => "-mx=5"
        };
        string splitArgument = string.IsNullOrWhiteSpace(request.SplitSize)
            ? string.Empty
            : $" -v{request.SplitSize}";

        string? listFilePath = null;
        try
        {
            bool containsWildcard = sourcePaths.Any(path => path.IndexOfAny(new[] { '*', '?' }) >= 0);
            var args = new List<string>
            {
                "a",
                formatArgument,
                compressionArgument,
                "-y",
                request.OutputArchivePath
            };

            if (!string.IsNullOrWhiteSpace(request.SplitSize))
            {
                args.Add($"-v{request.SplitSize}");
            }

            if (!containsWildcard && sourcePaths.Count > 0)
            {
                listFilePath = CreatePackListFile(sourcePaths);
                args.Add("-scsUTF-8");
                args.Add($"@{listFilePath}");
            }
            else
            {
                foreach (string path in sourcePaths)
                {
                    args.Add(path);
                }
            }

            var result = ExecuteProcess(sevenZipPath, args, token, onOutputLine);
            if (result.ExitCode != 0 && !token.IsCancellationRequested)
            {
                LogService.Error($"7z Pack Failure (ExitCode: {result.ExitCode})\nError: {result.Error}");
            }
            return result;
        }
        finally
        {
            TryDeleteTemporaryListFile(listFilePath);
        }
    }

    public static (int ExitCode, string Output, string Error) Unpack(string sevenZipPath, string archivePath, string extractToDir, CancellationToken token = default, Action<string>? onOutputLine = null)
    {
        if (!File.Exists(sevenZipPath))
        {
            throw new FileNotFoundException("7z.exe が見つかりません。", sevenZipPath);
        }

        // 7z x "archive.zip" -o"destDir" -y
        // 非対話実行で既存ファイル上書き確認に詰まらないようにする。
        string args = $"x \"{archivePath}\" -o\"{extractToDir}\" -y";

        var result = ExecuteProcess(sevenZipPath, args, token, onOutputLine);
        if (result.ExitCode != 0 && !token.IsCancellationRequested)
        {
            LogService.Error($"7z Unpack Failure (ExitCode: {result.ExitCode})\nArgs: {args}\nError: {result.Error}");
        }
        return result;
    }

    public static (int ExitCode, string Output, string Error) ExtractSelection(
        string sevenZipPath,
        string archivePath,
        string extractToDir,
        IReadOnlyCollection<string> entryPaths,
        CancellationToken token = default,
        Action<string>? onOutputLine = null)
    {
        if (!File.Exists(sevenZipPath))
        {
            throw new FileNotFoundException("7z.exe が見つかりません。", sevenZipPath);
        }

        if (entryPaths == null || entryPaths.Count == 0)
        {
            throw new ArgumentException("解凍対象 entry がありません。", nameof(entryPaths));
        }

        string joinedEntries = string.Join(" ", entryPaths.Select(path => $"\"{path}\""));
        // archive dialog からの選択解凍は非対話実行なので、既存ファイル確認で止まらないよう -y を付ける。
        string args = $"x \"{archivePath}\" -o\"{extractToDir}\" -y -- {joinedEntries}";

        var result = ExecuteProcess(sevenZipPath, args, token, onOutputLine);
        if (result.ExitCode != 0 && !token.IsCancellationRequested)
        {
            LogService.Error($"7z ExtractSelection Failure (ExitCode: {result.ExitCode})\nArgs: {args}\nError: {result.Error}");
        }

        return result;
    }

    public static (int ExitCode, string Output, string Error) List(string sevenZipPath, string archivePath, CancellationToken token = default)
    {
        if (!File.Exists(sevenZipPath))
        {
            throw new FileNotFoundException("7z.exe が見つかりません。", sevenZipPath);
        }

        string args = $"l -slt -ba -sccUTF-8 \"{archivePath}\"";
        var result = ExecuteProcess(sevenZipPath, args, token, outputEncoding: Encoding.UTF8, errorEncoding: Encoding.UTF8);
        if (result.ExitCode != 0 && !token.IsCancellationRequested)
        {
            LogService.Error($"7z List Failure (ExitCode: {result.ExitCode})\nArgs: {args}\nError: {result.Error}");
        }
        return result;
    }

    public static async Task<(int ExitCode, string Output, string Error)> HashAsync(
        string sevenZipPath,
        IReadOnlyList<string> sourcePaths,
        SevenZipHashAlgorithm algorithm,
        CancellationToken token = default,
        Action<string>? onOutputLine = null)
    {
        if (!File.Exists(sevenZipPath))
        {
            throw new FileNotFoundException("7z.exe が見つかりません。", sevenZipPath);
        }

        var args = new List<string> { "h" };
        string algoArg = algorithm switch
        {
            SevenZipHashAlgorithm.Crc32 => "-scrcCRC32",
            SevenZipHashAlgorithm.Crc64 => "-scrcCRC64",
            SevenZipHashAlgorithm.Sha1 => "-scrcSHA1",
            SevenZipHashAlgorithm.Sha256 => "-scrcSHA256",
            SevenZipHashAlgorithm.All => "-scrc*",
            _ => "-scrc*"
        };
        args.Add(algoArg);

        foreach (var path in sourcePaths)
        {
            args.Add(path);
        }

        return await Task.Run(() => ExecuteProcess(sevenZipPath, args, token, onOutputLine), token);
    }

    private static bool IsNumericVolumeSuffix(string suffix)
    {
        return suffix.Length >= 3 && suffix.All(char.IsDigit);
    }

    private static bool IsLegacyZipVolumeExtension(string extension)
    {
        return extension.Length == 4
            && (extension[1] == 'z' || extension[1] == 'Z')
            && char.IsDigit(extension[2])
            && char.IsDigit(extension[3]);
    }

    private static string CreatePackListFile(IEnumerable<string> sourcePaths)
    {
        string listFilePath = Path.Combine(Path.GetTempPath(), $"midfd-pack-{Guid.NewGuid():N}.lst");
        using var writer = new StreamWriter(listFilePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (string path in sourcePaths)
        {
            writer.WriteLine(path);
        }

        return listFilePath;
    }

    private static void TryDeleteTemporaryListFile(string? listFilePath)
    {
        if (string.IsNullOrWhiteSpace(listFilePath))
        {
            return;
        }

        try
        {
            if (File.Exists(listFilePath))
            {
                File.Delete(listFilePath);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to delete temporary 7-Zip listfile: {listFilePath}. {ex.Message}");
        }
    }
}
