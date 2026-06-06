using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Text;

namespace MidFD.Services;

public static class DragArchiveService
{
    public sealed class DragArchiveInfo
    {
        public string BaseDirectory { get; init; } = "";
        public string ArchivePath { get; init; } = "";
    }

    private class ManifestEntry
    {
        public string RelativePath { get; set; } = "";
        public string Type { get; set; } = ""; // "File" or "Directory"
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    public static DragArchiveInfo GetOrCreateInfoZip(string tempDir, IReadOnlyList<string> sourcePaths, bool includeManifest)
    {
        string? fullOutputDir = Path.GetFullPath(tempDir);
        if (string.IsNullOrWhiteSpace(fullOutputDir))
        {
            throw new InvalidOperationException("出力先フォルダの特定に失敗しました。");
        }

        Directory.CreateDirectory(fullOutputDir);

        // クリーンアップ (古い一時ZIPおよび残骸の .tmp)
        CleanupOldArchives(fullOutputDir);

        // 1. マニフェストの収集とハッシュ計算
        var manifest = new List<ManifestEntry>();
        string normalizedBaseDirectory = GetCommonBaseDirectory(sourcePaths);
        CollectManifest(normalizedBaseDirectory, sourcePaths, manifest);

        // 相対パスの昇順でソートして安定化
        manifest.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

        // ハッシュ文字列の生成 (SHA256)
        string hash = ComputeManifestHash(normalizedBaseDirectory, manifest, includeManifest);

        string zipPath = Path.Combine(fullOutputDir, $"MidFD-drag-{hash}.zip");

        // 2. 既存ZIPの検証と再利用
        if (File.Exists(zipPath))
        {
            try
            {
                // ZIPファイルが正常に読めるかテスト
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    // 読める場合はそのまま再利用
                    LogService.Info($"[DragArchive] Reusing existing ZIP archive: {zipPath}");
                    return new DragArchiveInfo
                    {
                        BaseDirectory = normalizedBaseDirectory,
                        ArchivePath = zipPath
                    };
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[DragArchive] Existing ZIP file is corrupt or unreadable: {zipPath}. Error: {ex.Message}");
                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                    // 削除失敗は無視して進む
                }
            }
        }

        // 3. 新規ZIPの作成 (.tmp 経由)
        string tempZipPath = zipPath + ".tmp";
        if (File.Exists(tempZipPath))
        {
            try
            {
                File.Delete(tempZipPath);
            }
            catch { }
        }

        try
        {
            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                foreach (var sourcePath in sourcePaths)
                {
                    // CollectManifest ですでに検証済みのため、ここではそのまま追加
                    if (Directory.Exists(sourcePath))
                    {
                        AddDirectory(archive, sourcePath, normalizedBaseDirectory, entries);
                    }
                    else if (File.Exists(sourcePath))
                    {
                        AddFile(archive, sourcePath, normalizedBaseDirectory, entries);
                    }
                }

                if (includeManifest)
                {
                    AddManifestEntry(archive, BuildManifestText(normalizedBaseDirectory, hash, manifest));
                }
            }

            // 作成成功後に正式名称へ移動
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
            File.Move(tempZipPath, zipPath);
            LogService.Info($"[DragArchive] Created new ZIP archive: {zipPath}");
            return new DragArchiveInfo
            {
                BaseDirectory = normalizedBaseDirectory,
                ArchivePath = zipPath
            };
        }
        catch (Exception)
        {
            // 失敗時は .tmp を削除
            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
            catch { }
            throw;
        }
    }

    private static string GetCommonBaseDirectory(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths == null || sourcePaths.Count == 0)
        {
            throw new InvalidOperationException("圧縮対象がありません。");
        }

        var baseCandidates = new List<string>(sourcePaths.Count);
        foreach (string sourcePath in sourcePaths)
        {
            string normalizedSourcePath = Path.GetFullPath(sourcePath);
            string? candidateBase = Directory.Exists(normalizedSourcePath)
                ? Path.GetDirectoryName(normalizedSourcePath)
                : Path.GetDirectoryName(normalizedSourcePath);

            if (string.IsNullOrWhiteSpace(candidateBase))
            {
                throw new InvalidOperationException($"圧縮基準ディレクトリを特定できませんでした。\n対象: {sourcePath}");
            }

            baseCandidates.Add(Path.GetFullPath(candidateBase));
        }

        string commonBase = baseCandidates[0];
        for (int i = 1; i < baseCandidates.Count; i++)
        {
            commonBase = GetCommonDirectory(commonBase, baseCandidates[i]);
        }

        if (string.IsNullOrWhiteSpace(commonBase))
        {
            throw new InvalidOperationException("圧縮対象の共通親ディレクトリを特定できませんでした。");
        }

        return Path.GetFullPath(commonBase);
    }

    private static string GetCommonDirectory(string left, string right)
    {
        string normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string leftRoot = Path.GetPathRoot(normalizedLeft) ?? "";
        string rightRoot = Path.GetPathRoot(normalizedRight) ?? "";
        if (!string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"異なるドライブをまたぐため、共通親ディレクトリを作成できません。\n対象: {left}\n対象: {right}");
        }

        string leftRemainder = normalizedLeft[leftRoot.Length..];
        string rightRemainder = normalizedRight[rightRoot.Length..];
        string[] leftParts = leftRemainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        string[] rightParts = rightRemainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        int maxShared = Math.Min(leftParts.Length, rightParts.Length);
        int sharedCount = 0;
        while (sharedCount < maxShared && string.Equals(leftParts[sharedCount], rightParts[sharedCount], StringComparison.OrdinalIgnoreCase))
        {
            sharedCount++;
        }

        if (sharedCount == 0)
        {
            return leftRoot;
        }

        string sharedRemainder = Path.Combine(leftParts[..sharedCount]);
        return Path.Combine(leftRoot, sharedRemainder);
    }

    private static void CollectManifest(string baseDirectory, IReadOnlyList<string> sourcePaths, List<ManifestEntry> manifest)
    {
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourcePath in sourcePaths)
        {
            if (ReparsePointHelper.IsReparsePoint(sourcePath))
            {
                throw new InvalidOperationException($"シンボリックリンクまたはジャンクションが検出されたため、圧縮を中止しました。\n対象: {sourcePath}");
            }

            if (Directory.Exists(sourcePath))
            {
                CollectDirectoryManifest(sourcePath, baseDirectory, entries, manifest);
            }
            else if (File.Exists(sourcePath))
            {
                CollectFileManifest(sourcePath, baseDirectory, entries, manifest);
            }
        }
    }

    private static void CollectDirectoryManifest(string dirPath, string baseDirectory, HashSet<string> entries, List<ManifestEntry> manifest)
    {
        string relativeDir = GetSafeRelativeEntryPath(baseDirectory, dirPath, true);
        if (entries.Add(relativeDir))
        {
            var di = new DirectoryInfo(dirPath);
            manifest.Add(new ManifestEntry
            {
                RelativePath = relativeDir,
                Type = "Directory",
                Size = 0,
                LastWriteTimeUtc = di.LastWriteTimeUtc
            });
        }

        var files = Directory.GetFileSystemEntries(dirPath);
        foreach (var file in files)
        {
            if (ReparsePointHelper.IsReparsePoint(file))
            {
                throw new InvalidOperationException($"シンボリックリンクまたはジャンクションが検出されたため、圧縮を中止しました。\n対象: {file}");
            }

            if (Directory.Exists(file))
            {
                CollectDirectoryManifest(file, baseDirectory, entries, manifest);
            }
            else if (File.Exists(file))
            {
                CollectFileManifest(file, baseDirectory, entries, manifest);
            }
        }
    }

    private static void CollectFileManifest(string filePath, string baseDirectory, HashSet<string> entries, List<ManifestEntry> manifest)
    {
        string relativePath = GetSafeRelativeEntryPath(baseDirectory, filePath, false);
        if (entries.Add(relativePath))
        {
            var fi = new FileInfo(filePath);
            manifest.Add(new ManifestEntry
            {
                RelativePath = relativePath,
                Type = "File",
                Size = fi.Length,
                LastWriteTimeUtc = fi.LastWriteTimeUtc
            });
        }
    }

    private static void AddDirectory(ZipArchive archive, string dirPath, string baseDirectory, HashSet<string> entries)
    {
        string relativeDir = GetSafeRelativeEntryPath(baseDirectory, dirPath, true);

        if (!entries.Add(relativeDir))
        {
            throw new InvalidOperationException($"同名のエントリが既に存在します: {relativeDir}");
        }

        archive.CreateEntry(relativeDir);

        var files = Directory.GetFileSystemEntries(dirPath);
        foreach (var file in files)
        {
            if (Directory.Exists(file))
            {
                AddDirectory(archive, file, baseDirectory, entries);
            }
            else if (File.Exists(file))
            {
                AddFile(archive, file, baseDirectory, entries);
            }
        }
    }

    private static void AddFile(ZipArchive archive, string filePath, string baseDirectory, HashSet<string> entries)
    {
        string relativePath = GetSafeRelativeEntryPath(baseDirectory, filePath, false);
        if (!entries.Add(relativePath))
        {
            throw new InvalidOperationException($"同名のエントリが既に存在します: {relativePath}");
        }

        archive.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
    }

    private static string GetSafeRelativeEntryPath(string baseDirectory, string targetPath, bool isDirectory)
    {
        string relativePath = Path.GetRelativePath(baseDirectory, targetPath).Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException($"ZIPエントリ名を決定できませんでした。\n対象: {targetPath}");
        }

        if (relativePath.StartsWith("../", StringComparison.Ordinal)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.Contains("/../", StringComparison.Ordinal)
            || relativePath.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ZIPエントリ名に使用できない相対パスが生成されました。\n対象: {targetPath}\n相対パス: {relativePath}");
        }

        return isDirectory ? relativePath.TrimEnd('/') + "/" : relativePath;
    }

    private static string ComputeManifestHash(string baseDirectory, IReadOnlyList<ManifestEntry> manifest, bool includeManifest)
    {
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.WriteLine("formatVersion=1");
            writer.WriteLine("feature=MidFD Drag ZIP");
            writer.WriteLine($"includeManifest={includeManifest}");
            writer.WriteLine(baseDirectory);
            foreach (var entry in manifest)
            {
                writer.WriteLine($"{entry.RelativePath}|{entry.Type}|{entry.Size}|{entry.LastWriteTimeUtc:O}");
            }
        }

        ms.Position = 0;
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(ms);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private static string BuildManifestText(string baseDirectory, string hash, IReadOnlyList<ManifestEntry> manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("formatVersion=1");
        sb.AppendLine("appName=MidFD");
        sb.AppendLine("featureName=Drag ZIP");
        sb.AppendLine($"createdAtUtc={DateTime.UtcNow:O}");
        sb.AppendLine($"manifestHash={hash}");
        sb.AppendLine($"baseDirectory={baseDirectory}");
        sb.AppendLine($"itemCount={manifest.Count}");
        sb.AppendLine();
        sb.AppendLine("files:");

        foreach (var entry in manifest)
        {
            sb.AppendLine($"- {entry.RelativePath} | {entry.Type.ToLowerInvariant()} | {entry.Size} | {entry.LastWriteTimeUtc:O}");
        }

        return sb.ToString();
    }

    private static void AddManifestEntry(ZipArchive archive, string manifestText)
    {
        ZipArchiveEntry entry = archive.CreateEntry("_midfd_drag_manifest.txt", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(manifestText);
        writer.Flush();
    }

    private static void CleanupOldArchives(string tempDir)
    {
        try
        {
            if (!Directory.Exists(tempDir)) return;
            string[] patterns = { "MidFD-drag-*.zip", "MidFD-drag-*.zip.tmp" };
            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(tempDir, pattern);
                foreach (var file in files)
                {
                    var writeTime = File.GetLastWriteTime(file);
                    if (DateTime.Now - writeTime > TimeSpan.FromDays(7))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // スキップ
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to cleanup old drag archives: {ex.Message}");
        }
    }
}
