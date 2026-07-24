using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Text;
using MidFD.Configuration.Storage;

namespace MidFD.Services;

public static class DragArchiveService
{
    private const string DragArchivePrefix = "MidFD-drag-";
    private static readonly TimeSpan DragArchiveRetention = TimeSpan.FromMinutes(30);
    private static readonly AppStoragePaths StoragePaths = LegacyStoragePathProvider.CreateDefault().GetPaths();

    public sealed class DragArchiveInfo
    {
        public string BaseDirectory { get; init; } = "";
        public string ArchivePath { get; init; } = "";
        public int ItemCount { get; init; }
    }

    private class ManifestEntry
    {
        public string RelativePath { get; set; } = "";
        public string Type { get; set; } = ""; // "File" or "Directory"
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    public static string GetDragArchiveTempDirectory()
    {
        return Path.Combine(StoragePaths.TempRoot, "MidFD", "DragArchive");
    }

    public static void CleanupDragArchivesOnStartup(string tempDir)
    {
        CleanupDragArchives(tempDir, DragArchiveRetention);
    }

    public static void CleanupDragArchivesBeforeCreation(string tempDir)
    {
        CleanupDragArchives(tempDir, DragArchiveRetention);
    }

    public static DragArchiveInfo GetOrCreateInfoZip(string tempDir, IReadOnlyList<string> sourcePaths, bool includeManifest)
    {
        string? fullOutputDir = Path.GetFullPath(tempDir);
        if (string.IsNullOrWhiteSpace(fullOutputDir))
        {
            throw new InvalidOperationException("出力先フォルダの特定に失敗しました。");
        }

        Directory.CreateDirectory(fullOutputDir);

        // sourcePathsのクリーンアップ後、manifest／ZIP共通のroot filterを適用する。
        var cleanedSourcePaths = FilterSourcePaths(CleanSourcePaths(sourcePaths));
        if (cleanedSourcePaths.Count == 0)
        {
            throw new InvalidOperationException("圧縮対象がありません。リンク先を追跡せず、空のDrag ZIPは作成しません。");
        }

        // 1. マニフェストの収集とハッシュ計算
        var manifest = new List<ManifestEntry>();
        string normalizedBaseDirectory = GetCommonBaseDirectory(cleanedSourcePaths);
        CollectManifest(normalizedBaseDirectory, cleanedSourcePaths, manifest);
        if (!manifest.Any(static entry => string.Equals(entry.Type, "File", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("圧縮対象ファイルがありません。リンク先を追跡せず、空のDrag ZIPは作成しません。");
        }

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
                        ArchivePath = zipPath,
                        ItemCount = sourcePaths.Count
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
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var archive = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
            {
                foreach (var sourcePath in cleanedSourcePaths)
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
                try
                {
                    File.Delete(zipPath);
                }
                catch (Exception ex) when (IsAccessDeniedLike(ex))
                {
                    throw CreateAccessDeniedException(zipPath, ex);
                }
            }
            try
            {
                File.Move(tempZipPath, zipPath);
            }
            catch (Exception ex) when (IsAccessDeniedLike(ex))
            {
                throw CreateAccessDeniedException(zipPath, ex);
            }
            LogService.Info($"[DragArchive] Created new ZIP archive: {zipPath}");
            return new DragArchiveInfo
            {
                BaseDirectory = normalizedBaseDirectory,
                ArchivePath = zipPath,
                ItemCount = sourcePaths.Count
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
            if (Directory.Exists(normalizedSourcePath) && IsDriveRootDirectory(normalizedSourcePath))
            {
                throw new InvalidOperationException($"ドライブ直下はドラッグ用ZIPの対象にできません。\n対象: {sourcePath}");
            }

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
        string normalizedLeft = Path.GetFullPath(left);
        string normalizedRight = Path.GetFullPath(right);

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
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourcePath in sourcePaths)
        {
            if (ReparsePointHelper.IsReparsePoint(sourcePath))
            {
                continue;
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

    private static void CollectDirectoryManifest(string dirPath, string baseDirectory, Dictionary<string, string> entries, List<ManifestEntry> manifest)
    {
        string relativeDir = GetSafeRelativeEntryPath(baseDirectory, dirPath, true);
        string fullDirPath = Path.GetFullPath(dirPath);

        if (!entries.ContainsKey(relativeDir))
        {
            entries.Add(relativeDir, fullDirPath);
            var di = new DirectoryInfo(dirPath);
            manifest.Add(new ManifestEntry
            {
                RelativePath = relativeDir,
                Type = "Directory",
                Size = 0,
                LastWriteTimeUtc = di.LastWriteTimeUtc
            });
        }
        else
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFileSystemEntries(dirPath);
        }
        catch (Exception ex) when (IsAccessDeniedLike(ex))
        {
            throw CreateAccessDeniedException(dirPath, ex);
        }
        foreach (var file in files)
        {
            if (ReparsePointHelper.IsReparsePoint(file))
            {
                continue;
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

    private static void CollectFileManifest(string filePath, string baseDirectory, Dictionary<string, string> entries, List<ManifestEntry> manifest)
    {
        try
        {
            string relativePath = GetSafeRelativeEntryPath(baseDirectory, filePath, false);
            string fullPath = Path.GetFullPath(filePath);

            if (entries.TryGetValue(relativePath, out var existingPath))
            {
                if (string.Equals(existingPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                else
                {
                    throw new InvalidOperationException($"異なる実体ファイルが同名のエントリとして衝突しています。\nエントリ: {relativePath}\n既存: {existingPath}\n新規: {fullPath}");
                }
            }

            entries.Add(relativePath, fullPath);
            var fi = new FileInfo(filePath);
            manifest.Add(new ManifestEntry
            {
                RelativePath = relativePath,
                Type = "File",
                Size = fi.Length,
                LastWriteTimeUtc = fi.LastWriteTimeUtc
            });
        }
        catch (Exception ex) when (IsAccessDeniedLike(ex))
        {
            throw CreateAccessDeniedException(filePath, ex);
        }
    }

    private static void AddDirectory(ZipArchive archive, string dirPath, string baseDirectory, Dictionary<string, string> entries)
    {
        string relativeDir = GetSafeRelativeEntryPath(baseDirectory, dirPath, true);
        string fullDirPath = Path.GetFullPath(dirPath);

        if (entries.TryGetValue(relativeDir, out var existingPath))
        {
            return;
        }

        entries.Add(relativeDir, fullDirPath);
        archive.CreateEntry(relativeDir);

        string[] files;
        try
        {
            files = Directory.GetFileSystemEntries(dirPath);
        }
        catch (Exception ex) when (IsAccessDeniedLike(ex))
        {
            throw CreateAccessDeniedException(dirPath, ex);
        }
        foreach (var file in files)
        {
            if (ReparsePointHelper.IsReparsePoint(file))
            {
                continue;
            }
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

    private static void AddFile(ZipArchive archive, string filePath, string baseDirectory, Dictionary<string, string> entries)
    {
        try
        {
            string relativePath = GetSafeRelativeEntryPath(baseDirectory, filePath, false);
            string fullPath = Path.GetFullPath(filePath);

            if (entries.TryGetValue(relativePath, out var existingPath))
            {
                if (string.Equals(existingPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                else
                {
                    throw new InvalidOperationException($"異なる実体ファイルが同名のエントリとして衝突しています。\nエントリ: {relativePath}\n既存: {existingPath}\n新規: {fullPath}");
                }
            }

            entries.Add(relativePath, fullPath);
            archive.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
        }
        catch (Exception ex) when (IsAccessDeniedLike(ex))
        {
            throw CreateAccessDeniedException(filePath, ex);
        }
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

    private static IReadOnlyList<string> CleanSourcePaths(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths == null || sourcePaths.Count == 0)
        {
            return Array.Empty<string>();
        }

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in sourcePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                uniquePaths.Add(Path.GetFullPath(path));
            }
        }

        var list = new List<string>(uniquePaths);
        list.Sort((a, b) => a.Length.CompareTo(b.Length));

        var result = new List<string>();
        foreach (var path in list)
        {
            bool hasParent = false;
            foreach (var parent in result)
            {
                if (Directory.Exists(parent))
                {
                    string parentDir = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
                    if (path.StartsWith(parentDir, StringComparison.OrdinalIgnoreCase))
                    {
                        hasParent = true;
                        break;
                    }
                }
            }
            if (!hasParent)
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> FilterSourcePaths(IReadOnlyList<string> sourcePaths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in sourcePaths)
        {
            if ((!File.Exists(sourcePath) && !Directory.Exists(sourcePath)) || ReparsePointHelper.IsReparsePoint(sourcePath))
            {
                continue;
            }

            string identity = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (seen.Add(identity))
            {
                result.Add(sourcePath);
            }
        }
        return result;
    }

    private static bool IsDriveRootDirectory(string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(normalizedPath);
        return !string.IsNullOrWhiteSpace(root)
            && string.Equals(
                normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccessDeniedLike(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return true;
        }

        return ex is IOException io && ((io.HResult & 0xFFFF) == 5 || (io.HResult & 0xFFFF) == 32);
    }

    private static InvalidOperationException CreateAccessDeniedException(string targetPath, Exception ex)
    {
        return new InvalidOperationException(
            $"ファイルを読み取れません。使用中、またはアクセス権限がない可能性があります。\n対象: {targetPath}",
            ex);
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

    private static void CleanupDragArchives(string tempDir, TimeSpan? minimumAge)
    {
        try
        {
            if (!Directory.Exists(tempDir)) return;
            foreach (var file in Directory.GetFiles(tempDir, $"{DragArchivePrefix}*.zip"))
            {
                try
                {
                    if (minimumAge.HasValue)
                    {
                        var writeTime = File.GetLastWriteTime(file);
                        if (DateTime.Now - writeTime < minimumAge.Value)
                        {
                            continue;
                        }
                    }

                    File.Delete(file);
                }
                catch
                {
                    // 取得/削除できないものは静かに残す
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"[DragArchive] Cleanup skipped: {ex.Message}");
        }
    }
}
