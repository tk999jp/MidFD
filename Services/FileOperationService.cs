using System.IO;
using System.Runtime.InteropServices;
using MidFD.Models;

namespace MidFD.Services;

public static class FileOperationService
{
    internal static void CreateDirectoryForUserMutation(string path)
    {
        Directory.CreateDirectory(path);
    }
    public static void Rename(string sourcePath, string destPath)
    {
        try
        {
            if (ReparsePointHelper.IsDirectory(sourcePath))
            {
                Directory.Move(sourcePath, destPath);
            }
            else
            {
                File.Move(sourcePath, destPath);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Rename failed: {sourcePath} -> {destPath}", ex);
            throw;
        }
    }

    /// <summary>ゴミ箱へ削除する (Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile/Directory を使用)</summary>
    public static void DeleteToRecycleBin(string path)
    {
        if (ReparsePointHelper.IsReparsePoint(path))
        {
            ShellRecycleBinDeleteService.Result result = ShellRecycleBinDeleteService
                .DeleteToRecycleBinAsync(new[] { path }, IntPtr.Zero, CancellationToken.None, _ => { })
                .GetAwaiter()
                .GetResult();
            if (result.IsCanceled || result.FailCount != 0 || result.SuccessCount != 1)
            {
                throw new IOException($"リンクをゴミ箱へ移動できませんでした: {path}");
            }
            return;
        }

        if (Directory.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, 
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, 
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else if (File.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, 
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, 
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }


    /// <summary>完全に削除する (物理削除)</summary>
    public static void Delete(string path)
    {
        if (ReparsePointHelper.IsReparsePoint(path))
        {
            if (ReparsePointHelper.IsDirectory(path)) Directory.Delete(path, false);
            else File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            DeleteDirectoryTreeWithoutFollowingReparsePoints(path);
        }
        else if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryTreeWithoutFollowingReparsePoints(string directoryPath, ISet<string>? excludedReparsePaths = null)
    {
        if (ReparsePointHelper.IsReparsePoint(directoryPath))
        {
            Directory.Delete(directoryPath, false);
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(directoryPath))
        {
            if (excludedReparsePaths?.Contains(filePath) == true)
            {
                continue;
            }
            if (ReparsePointHelper.IsReparsePoint(filePath))
            {
                File.Delete(filePath);
                continue;
            }

            File.SetAttributes(filePath, FileAttributes.Normal);
            File.Delete(filePath);
        }

        foreach (string childDirectory in Directory.EnumerateDirectories(directoryPath))
        {
            if (excludedReparsePaths?.Contains(childDirectory) == true)
            {
                continue;
            }
            if (ReparsePointHelper.IsReparsePoint(childDirectory))
            {
                Directory.Delete(childDirectory, false);
                continue;
            }

            DeleteDirectoryTreeWithoutFollowingReparsePoints(childDirectory, excludedReparsePaths);
        }

        File.SetAttributes(directoryPath, FileAttributes.Normal);
        Directory.Delete(directoryPath, false);
    }

    public static void Copy(string sourcePath, string destPath, ISet<string>? excludedReparsePaths = null)
    {
        bool destinationExisted = ReparsePointHelper.Exists(destPath);
        try
        {
            if (ReparsePointHelper.IsReparsePoint(sourcePath))
            {
                CopyLinkObject(sourcePath, destPath);
            }
            else if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, destPath, excludedReparsePaths);
            }
            else
            {
                File.Copy(sourcePath, destPath, true);
            }
        }
        catch (Exception ex)
        {
            TryDeleteEmptyCreatedDirectory(destPath, destinationExisted);
            LogService.Error($"Copy failed: {sourcePath} -> {destPath}", ex);
            throw;
        }
    }

    public static void Move(
        string sourcePath,
        string destPath,
        bool overwrite = false,
        bool suppressLogging = false,
        CancellationToken cancellationToken = default,
        ISet<string>? excludedReparsePaths = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!suppressLogging) LogService.Info($"[FileOp] Move start: {sourcePath} -> {destPath} (overwrite={overwrite})");
        bool isDirectory = ReparsePointHelper.IsDirectory(sourcePath);
        if (!ReparsePointHelper.Exists(sourcePath)) throw new FileNotFoundException("移動元が見つかりません。", sourcePath);
        if (!overwrite && PathExists(destPath))
        {
            throw new IOException($"移動先に同名項目が残っています: {destPath}");
        }

        try
        {
            if (HaveSameStorageRoot(sourcePath, destPath))
            {
                if (isDirectory)
                {
                    Directory.Move(sourcePath, destPath);
                    if (!suppressLogging) LogService.Info($"[FileOp] Same-root Directory.Move success: {sourcePath}");
                }
                else
                {
                    File.Move(sourcePath, destPath, overwrite);
                    if (!suppressLogging) LogService.Info($"[FileOp] Same-root File.Move success: {sourcePath}");
                }
            }
            else
            {
                MoveWithCopyFallback(sourcePath, destPath, overwrite, cancellationToken: cancellationToken,
                    excludedReparsePaths: excludedReparsePaths);
                if (!suppressLogging) LogService.Info($"[FileOp] Cross-root temporary copy+finalize+delete success: {sourcePath}");
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[FileOp] Move failed: {sourcePath} -> {destPath}", ex);
            throw;
        }
    }

    private static bool PathExists(string path)
    {
        return ReparsePointHelper.Exists(path);
    }

    internal static bool IsDirectoryPath(string path) => ReparsePointHelper.IsDirectory(path);
    internal static bool IsDirectoryContainerPath(string path) => ReparsePointHelper.IsDirectoryContainer(path);

    internal static bool HaveSameStorageRoot(string sourcePath, string destinationPath) =>
        string.Equals(GetStorageRootIdentity(sourcePath), GetStorageRootIdentity(destinationPath), StringComparison.OrdinalIgnoreCase);

    internal static string GetStorageRootIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException($"storage rootを解決できません: {path}");
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static int DeleteSuccessfulPreparedReparsePointsUnderSource(
        string sourceDir,
        ISet<string>? excludedReparsePaths,
        ISet<string>? successfulPreparedReparsePaths,
        string operationLogLabel)
    {
        if (excludedReparsePaths == null || successfulPreparedReparsePaths == null || successfulPreparedReparsePaths.Count == 0)
        {
            return 0;
        }

        int failCount = 0;
        string root = Path.GetFullPath(sourceDir);
        foreach (string sourcePath in successfulPreparedReparsePaths.OrderByDescending(path => path.Length))
        {
            if (!excludedReparsePaths.Contains(sourcePath) || !IsSameOrDescendantPath(root, sourcePath))
            {
                continue;
            }
            if (!ReparsePointHelper.Exists(sourcePath))
            {
                continue;
            }
            try
            {
                if (ReparsePointHelper.IsReparsePoint(sourcePath))
                {
                    Delete(sourcePath);
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"{operationLogLabel}リンク移動元削除失敗: {Path.GetFileName(sourcePath)}", ex);
                failCount++;
            }
        }
        return failCount;
    }

    internal static void DeleteEmptyDirectoriesBottomUp(string rootDir)
    {
        if (!IsDirectoryContainerPath(rootDir))
        {
            return;
        }

        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootDir);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string directoryPath in Directory.EnumerateDirectories(current))
            {
                if (!IsDirectoryContainerPath(directoryPath))
                {
                    continue;
                }

                directories.Add(directoryPath);
                pending.Push(directoryPath);
            }
        }

        foreach (string directoryPath in directories.OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath, false);
            }
        }

        if (IsDirectoryContainerPath(rootDir) && !Directory.EnumerateFileSystemEntries(rootDir).Any())
        {
            Directory.Delete(rootDir, false);
        }
    }

    private static bool IsSameOrDescendantPath(string rootPath, string candidatePath)
    {
        string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }


    private static void CopyDirectory(string sourceDir, string destinationDir, ISet<string>? excludedReparsePaths = null)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

        if (ReparsePointHelper.IsReparsePoint(sourceDir))
        {
            CopyLinkObject(sourceDir, destinationDir);
            return;
        }

        DirectoryInfo[] dirs = dir.GetDirectories();
        Directory.CreateDirectory(destinationDir);

        foreach (FileInfo file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            if (ReparsePointHelper.IsReparsePoint(file.FullName))
            {
                if (excludedReparsePaths?.Contains(file.FullName) != true)
                    CopyLinkObject(file.FullName, targetFilePath);
            }
            else
            {
                file.CopyTo(targetFilePath, true);
            }
        }

        foreach (DirectoryInfo subDir in dirs)
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            if (ReparsePointHelper.IsReparsePoint(subDir.FullName))
            {
                if (excludedReparsePaths?.Contains(subDir.FullName) != true)
                    CopyLinkObject(subDir.FullName, newDestinationDir);
            }
            else
            {
                CopyDirectory(subDir.FullName, newDestinationDir, excludedReparsePaths);
            }
        }
    }

    private static void CopyLinkObject(string sourcePath, string destinationPath)
    {
        uint tag = ReparsePointHelper.GetReparseTag(sourcePath);
        CopyLinkObjectForTag(tag, sourcePath, destinationPath);
    }

    internal static void CopyLinkObjectForTag(uint tag, string sourcePath, string destinationPath)
    {
        switch (tag)
        {
            case 0xA0000003:
                WindowsLinkCopyService.CopyJunction(sourcePath, destinationPath);
                break;
            case 0xA000000C:
                string target = ReparsePointHelper.GetLinkTarget(sourcePath);
                if (ReparsePointHelper.IsDirectory(sourcePath))
                {
                    WindowsLinkCopyService.CreateDirectorySymbolicLink(destinationPath, target);
                }
                else
                {
                    WindowsLinkCopyService.CopyFileSymbolicLink(sourcePath, destinationPath);
                }
                break;
            default:
                throw new IOException($"非対応のreparse tagです (0x{tag:X8}): {sourcePath}");
        }
    }

    private static void TryDeleteEmptyCreatedDirectory(string path, bool existedBeforeCopy)
    {
        if (existedBeforeCopy || !ReparsePointHelper.IsDirectoryContainer(path))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, false);
            }
        }
        catch
        {
            // The original copy failure remains the operation result.
        }
    }

    public static IReadOnlyList<DirectoryCopyPlanEntry> BuildDirectoryCopyPlan(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

        var entries = new List<DirectoryCopyPlanEntry>();
        var stack = new Stack<string>();
        stack.Push(sourceDir);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            if (!ReparsePointHelper.ShouldRecurseIntoDirectory(current))
            {
                continue;
            }

            foreach (string directoryPath in Directory.EnumerateDirectories(current))
            {
                string relativePath = Path.GetRelativePath(sourceDir, directoryPath);
                entries.Add(new DirectoryCopyPlanEntry
                {
                    SourcePath = directoryPath,
                    DestinationPath = Path.Combine(destinationDir, relativePath),
                    IsDirectory = !ReparsePointHelper.IsReparsePoint(directoryPath)
                });

                if (ReparsePointHelper.ShouldRecurseIntoDirectory(directoryPath))
                {
                    stack.Push(directoryPath);
                }
            }

            foreach (string filePath in Directory.EnumerateFiles(current))
            {
                string relativePath = Path.GetRelativePath(sourceDir, filePath);
                entries.Add(new DirectoryCopyPlanEntry
                {
                    SourcePath = filePath,
                    DestinationPath = Path.Combine(destinationDir, relativePath),
                    IsDirectory = false
                });
            }
        }

        return entries;
    }

    internal static void MoveWithCopyFallback(
        string sourcePath,
        string destPath,
        bool overwrite = false,
        Action<string>? deleteSourceOverride = null,
        CancellationToken cancellationToken = default,
        Action<string>? afterEntryCopied = null,
        Func<string, bool>? reparsePointProbe = null,
        ISet<string>? excludedReparsePaths = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool isDirectory = ReparsePointHelper.IsDirectory(sourcePath);
        if (!ReparsePointHelper.Exists(sourcePath)) throw new FileNotFoundException("移動元が見つかりません。", sourcePath);
        Func<string, bool> isReparsePoint = reparsePointProbe ?? ReparsePointHelper.IsReparsePoint;
        if (!overwrite && PathExists(destPath)) throw new IOException($"移動先に同名項目が残っています: {destPath}");

        string destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destPath)) ?? throw new InvalidOperationException("移動先directoryを解決できません。");
        Directory.CreateDirectory(destinationDirectory);
        string temporaryPath = Path.Combine(destinationDirectory, $".midfd-move-{Guid.NewGuid():N}.tmp");
        bool finalized = false;
        try
        {
            if (isReparsePoint(sourcePath))
            {
                CopyLinkObject(sourcePath, temporaryPath);
            }
            else if (isDirectory)
            {
                CopyDirectoryForMove(sourcePath, temporaryPath, cancellationToken, afterEntryCopied, isReparsePoint, excludedReparsePaths);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(sourcePath, temporaryPath, overwrite: false);
                afterEntryCopied?.Invoke(sourcePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (PathExists(destPath) && (!overwrite || isDirectory))
            {
                throw new IOException($"移動先に同名項目が作成されたため確定できません: {destPath}");
            }

            if (isDirectory) Directory.Move(temporaryPath, destPath);
            else File.Move(temporaryPath, destPath, overwrite);
            finalized = true;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deleteSourceOverride != null) deleteSourceOverride(sourcePath);
                else if (isReparsePoint(sourcePath)) Delete(sourcePath);
                else if (isDirectory) DeleteDirectoryTreeWithoutFollowingReparsePoints(sourcePath, excludedReparsePaths);
                else File.Delete(sourcePath);
            }
            catch (Exception ex)
            {
                throw new MovePartialException(sourcePath, destPath, ex);
            }
        }
        finally
        {
            if (!finalized && PathExists(temporaryPath))
            {
                try
                {
                    if (Directory.Exists(temporaryPath)) DeleteDirectoryTreeWithoutFollowingReparsePoints(temporaryPath);
                    else File.Delete(temporaryPath);
                }
                catch { }
            }
        }
    }

    private static void CopyDirectoryForMove(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken,
        Action<string>? afterEntryCopied,
        Func<string, bool> isReparsePoint,
        ISet<string>? excludedReparsePaths)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isReparsePoint(sourceDirectory))
        {
            CopyLinkObject(sourceDirectory, destinationDirectory);
            return;
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileDestination = Path.Combine(destinationDirectory, Path.GetFileName(file));
            if (isReparsePoint(file))
            {
                if (excludedReparsePaths?.Contains(file) != true)
                    CopyLinkObject(file, fileDestination);
            }
            else
            {
                File.Copy(file, fileDestination, overwrite: false);
            }
            afterEntryCopied?.Invoke(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directoryDestination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            if (isReparsePoint(directory))
            {
                if (excludedReparsePaths?.Contains(directory) != true)
                    CopyLinkObject(directory, directoryDestination);
            }
            else
            {
                CopyDirectoryForMove(directory, directoryDestination, cancellationToken, afterEntryCopied, isReparsePoint, excludedReparsePaths);
            }
        }
    }

    internal sealed class MovePartialException : IOException
    {
        public MovePartialException(string sourcePath, string destinationPath, Exception innerException)
            : base($"移動先は確定しましたが移動元を削除できませんでした: {sourcePath} -> {destinationPath}", innerException) { }
    }

    public static DirectoryMoveMergeGuardResult AnalyzeDirectoryMoveMerge(string sourceDir, string destinationDir)
    {
        var sourceInfo = new DirectoryInfo(sourceDir);
        if (!sourceInfo.Exists)
        {
            return new DirectoryMoveMergeGuardResult
            {
                CanMerge = false,
                AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                BlockingPath = sourceDir,
                Message = $"移動元フォルダが見つかりません: {sourceDir}"
            };
        }

        if (!Directory.Exists(destinationDir))
        {
            return new DirectoryMoveMergeGuardResult
            {
                CanMerge = false,
                AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                BlockingPath = destinationDir,
                Message = $"移動先フォルダが見つかりません: {destinationDir}"
            };
        }

        string sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourceDir)) ?? string.Empty;
        string destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationDir)) ?? string.Empty;
        if (!string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new DirectoryMoveMergeGuardResult
            {
                CanMerge = false,
                AbortReason = DirectoryMoveMergeAbortReason.DifferentRoot,
                BlockingPath = destinationDir,
                Message = "フォルダの統合移動は同一ドライブ内でのみ許可します。"
            };
        }

        return AnalyzeDirectoryMoveMergeRecursive(sourceDir, destinationDir);
    }

    public static DirectoryMoveMergeGuardResult AnalyzeDirectoryMoveMergePractical(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return new DirectoryMoveMergeGuardResult
            {
                CanMerge = false,
                AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                BlockingPath = sourceDir,
                Message = $"移動元フォルダが見つかりません: {sourceDir}"
            };
        }

        if (!Directory.Exists(destinationDir))
        {
            return new DirectoryMoveMergeGuardResult
            {
                CanMerge = false,
                AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                BlockingPath = destinationDir,
                Message = $"移動先フォルダが見つかりません: {destinationDir}"
            };
        }

        string sourceFullPath = Path.GetFullPath(sourceDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string destinationFullPath = Path.GetFullPath(destinationDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (destinationFullPath.StartsWith(sourceFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new DirectoryMoveMergeGuardResult
            {
                CanMerge = false,
                AbortReason = DirectoryMoveMergeAbortReason.PartialStateRisk,
                BlockingPath = destinationDir,
                Message = "移動元フォルダの配下へは統合移動しません。\n途中結果と移動元 cleanup の説明が難しいためです。"
            };
        }

        return AnalyzeDirectoryMoveMergePracticalRecursive(sourceDir, destinationDir);
    }

    public static DirectoryPasteMergeGuardResult AnalyzeDirectoryPasteMerge(string sourceDir, string destinationDir, bool isCut)
    {
        if (!Directory.Exists(sourceDir))
        {
            return new DirectoryPasteMergeGuardResult
            {
                CanMerge = false,
                AbortReason = DirectoryPasteMergeAbortReason.TypeMismatch,
                BlockingPath = sourceDir,
                Message = $"貼り付け元フォルダが見つかりません: {sourceDir}"
            };
        }

        if (!Directory.Exists(destinationDir))
        {
            return new DirectoryPasteMergeGuardResult
            {
                CanMerge = false,
                AbortReason = DirectoryPasteMergeAbortReason.TypeMismatch,
                BlockingPath = destinationDir,
                Message = $"貼り付け先フォルダが見つかりません: {destinationDir}"
            };
        }

        if (isCut)
        {
            string sourceFullPath = Path.GetFullPath(sourceDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string destinationFullPath = Path.GetFullPath(destinationDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (destinationFullPath.StartsWith(sourceFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new DirectoryPasteMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryPasteMergeAbortReason.PartialStateRisk,
                    BlockingPath = destinationDir,
                    Message = "貼り付け(移動)の同名フォルダ統合は、移動元フォルダの配下へは実行しません。\n途中結果の説明が難しく、source cleanup 契約を保てないためです。"
                };
            }

            var structuralGuard = AnalyzeDirectoryPasteCutMergeRecursive(sourceDir, destinationDir);
            if (!structuralGuard.CanMerge)
            {
                return structuralGuard;
            }

            return new DirectoryPasteMergeGuardResult
            {
                CanMerge = true,
                AbortReason = DirectoryPasteMergeAbortReason.None,
                BlockingPath = null,
                Message = "貼り付け(移動)の同名フォルダ統合を、同名ファイル衝突や型違いが無いフォルダ構造に対して実行できます。"
            };
        }

        return new DirectoryPasteMergeGuardResult
        {
            CanMerge = true,
            AbortReason = DirectoryPasteMergeAbortReason.None,
            BlockingPath = null,
            Message = "貼り付け(コピー)のフォルダ統合を実行できます。"
        };
    }

    private static DirectoryPasteMergeGuardResult AnalyzeDirectoryPasteCutMergeRecursive(string sourceDir, string destinationDir)
    {
        foreach (string filePath in Directory.EnumerateFiles(sourceDir))
        {
            string destinationFilePath = Path.Combine(destinationDir, Path.GetFileName(filePath));
            if (Directory.Exists(destinationFilePath))
            {
                return new DirectoryPasteMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryPasteMergeAbortReason.TypeMismatch,
                    BlockingPath = destinationFilePath,
                    Message = $"型が異なるため貼り付け(移動)のフォルダ統合はできません。\n宛先: {destinationFilePath}"
                };
            }
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(sourceDir))
        {
            string destinationSubDirectory = Path.Combine(destinationDir, Path.GetFileName(directoryPath));
            if (File.Exists(destinationSubDirectory))
            {
                return new DirectoryPasteMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryPasteMergeAbortReason.TypeMismatch,
                    BlockingPath = destinationSubDirectory,
                    Message = $"型が異なるため貼り付け(移動)のフォルダ統合はできません。\n宛先: {destinationSubDirectory}"
                };
            }

            if (ReparsePointHelper.IsReparsePoint(directoryPath))
            {
                continue;
            }

            if (Directory.Exists(destinationSubDirectory))
            {
                var childResult = AnalyzeDirectoryPasteCutMergeRecursive(directoryPath, destinationSubDirectory);
                if (!childResult.CanMerge)
                {
                    return childResult;
                }
            }
        }

        return new DirectoryPasteMergeGuardResult
        {
            CanMerge = true,
            AbortReason = DirectoryPasteMergeAbortReason.None,
            BlockingPath = null,
            Message = "貼り付け(移動)のフォルダ統合は構造上は可能ですが、partial state 条件をまだ固定していないため実行しません。"
        };
    }

    public static void MoveDirectoryIntoExisting(string sourceDir, string destinationDir)
    {
        if (ReparsePointHelper.IsReparsePoint(sourceDir))
        {
            string destinationLinkPath = Path.Combine(destinationDir, Path.GetFileName(sourceDir));
            if (File.Exists(destinationLinkPath) || Directory.Exists(destinationLinkPath))
            {
                throw new IOException($"移動先に同名項目が残っています: {destinationLinkPath}");
            }
            Directory.Move(sourceDir, destinationLinkPath);
            return;
        }

        var guard = AnalyzeDirectoryMoveMerge(sourceDir, destinationDir);
        if (!guard.CanMerge)
        {
            throw new InvalidOperationException(guard.Message);
        }

        foreach (string filePath in Directory.EnumerateFiles(sourceDir))
        {
            string destinationFilePath = Path.Combine(destinationDir, Path.GetFileName(filePath));
            if (File.Exists(destinationFilePath) || Directory.Exists(destinationFilePath))
            {
                throw new IOException($"移動先に同名項目が残っています: {destinationFilePath}");
            }

            File.Move(filePath, destinationFilePath);
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(sourceDir))
        {
            if (ReparsePointHelper.IsReparsePoint(directoryPath))
            {
                string destinationLinkPath = Path.Combine(destinationDir, Path.GetFileName(directoryPath));
                if (File.Exists(destinationLinkPath) || Directory.Exists(destinationLinkPath))
                {
                    throw new IOException($"移動先に同名項目が残っています: {destinationLinkPath}");
                }
                Directory.Move(directoryPath, destinationLinkPath);
                continue;
            }

            string destinationSubDirectory = Path.Combine(destinationDir, Path.GetFileName(directoryPath));
            if (Directory.Exists(destinationSubDirectory))
            {
                MoveDirectoryIntoExisting(directoryPath, destinationSubDirectory);
            }
            else if (File.Exists(destinationSubDirectory))
            {
                throw new IOException($"移動先に同名ファイルが残っています: {destinationSubDirectory}");
            }
            else
            {
                Directory.Move(directoryPath, destinationSubDirectory);
            }
        }

        Directory.Delete(sourceDir, false);
    }

    /// <summary>衝突しないパスを生成する (test.txt -> test (2).txt)</summary>
    public static string GetUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        string directory = Path.GetDirectoryName(path) ?? "";
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        int count = 2;

        while (true)
        {
            string newName = $"{fileNameWithoutExtension} ({count}){extension}";
            string newPath = Path.Combine(directory, newName);
            if (!File.Exists(newPath) && !Directory.Exists(newPath)) return newPath;
            count++;
            if (count > 1000) break; // 無限ループ防止
        }
        return path;
    }

    public static string GetUniquePathStartingAtOne(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int count = 1; count <= 1000; count++)
        {
            string newName = $"{fileNameWithoutExtension} ({count}){extension}";
            string newPath = Path.Combine(directory, newName);
            if (!File.Exists(newPath) && !Directory.Exists(newPath))
            {
                return newPath;
            }
        }

        return path;
    }

    private static DirectoryMoveMergeGuardResult AnalyzeDirectoryMoveMergeRecursive(string sourceDir, string destinationDir)
    {
        foreach (string filePath in Directory.EnumerateFiles(sourceDir))
        {
            string destinationFilePath = Path.Combine(destinationDir, Path.GetFileName(filePath));
            if (File.Exists(destinationFilePath))
            {
                return new DirectoryMoveMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryMoveMergeAbortReason.NestedFileCollision,
                    BlockingPath = destinationFilePath,
                    Message = $"移動先に同名ファイルがあるため、フォルダ統合移動はまだ行えません。\n宛先: {destinationFilePath}"
                };
            }

            if (Directory.Exists(destinationFilePath))
            {
                return new DirectoryMoveMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                    BlockingPath = destinationFilePath,
                    Message = $"型が異なるためフォルダ統合移動はできません。\n宛先: {destinationFilePath}"
                };
            }
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(sourceDir))
        {
            string destinationSubDirectory = Path.Combine(destinationDir, Path.GetFileName(directoryPath));
            if (File.Exists(destinationSubDirectory))
            {
                return new DirectoryMoveMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                    BlockingPath = destinationSubDirectory,
                    Message = $"型が異なるためフォルダ統合移動はできません。\n宛先: {destinationSubDirectory}"
                };
            }

            if (ReparsePointHelper.IsReparsePoint(directoryPath))
            {
                continue;
            }

            if (Directory.Exists(destinationSubDirectory))
            {
                var childResult = AnalyzeDirectoryMoveMergeRecursive(directoryPath, destinationSubDirectory);
                if (!childResult.CanMerge)
                {
                    return childResult;
                }
            }
        }

        return new DirectoryMoveMergeGuardResult
        {
            CanMerge = true,
            AbortReason = DirectoryMoveMergeAbortReason.None,
            Message = "フォルダ統合移動を安全側条件で実行できます。"
        };
    }

    private static DirectoryMoveMergeGuardResult AnalyzeDirectoryMoveMergePracticalRecursive(string sourceDir, string destinationDir)
    {
        foreach (string filePath in Directory.EnumerateFiles(sourceDir))
        {
            string destinationFilePath = Path.Combine(destinationDir, Path.GetFileName(filePath));
            if (Directory.Exists(destinationFilePath))
            {
                return new DirectoryMoveMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                    BlockingPath = destinationFilePath,
                    Message = $"型が異なるためフォルダ統合移動はできません。\n宛先: {destinationFilePath}"
                };
            }
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(sourceDir))
        {
            string destinationSubDirectory = Path.Combine(destinationDir, Path.GetFileName(directoryPath));
            if (File.Exists(destinationSubDirectory))
            {
                return new DirectoryMoveMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                    BlockingPath = destinationSubDirectory,
                    Message = $"型が異なるためフォルダ統合移動はできません。\n宛先: {destinationSubDirectory}"
                };
            }

            if (ReparsePointHelper.IsReparsePoint(directoryPath))
            {
                continue;
            }

            if (Directory.Exists(destinationSubDirectory))
            {
                var childResult = AnalyzeDirectoryMoveMergePracticalRecursive(directoryPath, destinationSubDirectory);
                if (!childResult.CanMerge)
                {
                    return childResult;
                }
            }
        }

        return new DirectoryMoveMergeGuardResult
        {
            CanMerge = true,
            AbortReason = DirectoryMoveMergeAbortReason.None,
            Message = "フォルダ統合移動を実行できます。nested file collision は個別確認します。"
        };
    }

    #region Size Formatting
    /// <summary>サイズを人間が読みやすい形式 (B, KB, MB, GB, TB) に変換する。</summary>
    public static string FormatSize(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024d && unitIndex < suffix.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return string.Format("{0:0.##}{1}", value, suffix[unitIndex]);
    }
    #endregion
}
