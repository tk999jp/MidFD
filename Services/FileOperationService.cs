using System.IO;
using System.Runtime.InteropServices;
using MidFD.Models;

namespace MidFD.Services;

public static class FileOperationService
{
    public static void Rename(string sourcePath, string destPath)
    {
        try
        {
            if (Directory.Exists(sourcePath))
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
        if (Directory.Exists(path))
        {
            DeleteDirectoryTreeWithoutFollowingReparsePoints(path);
        }
        else if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryTreeWithoutFollowingReparsePoints(string directoryPath)
    {
        if (ReparsePointHelper.IsReparsePoint(directoryPath))
        {
            Directory.Delete(directoryPath, false);
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(directoryPath))
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
            File.Delete(filePath);
        }

        foreach (string childDirectory in Directory.EnumerateDirectories(directoryPath))
        {
            if (ReparsePointHelper.IsReparsePoint(childDirectory))
            {
                Directory.Delete(childDirectory, false);
                continue;
            }

            DeleteDirectoryTreeWithoutFollowingReparsePoints(childDirectory);
        }

        File.SetAttributes(directoryPath, FileAttributes.Normal);
        Directory.Delete(directoryPath, false);
    }

    public static void Copy(string sourcePath, string destPath)
    {
        try
        {
            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, destPath);
            }
            else
            {
                File.Copy(sourcePath, destPath, true);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Copy failed: {sourcePath} -> {destPath}", ex);
            throw;
        }
    }

    public static void Move(string sourcePath, string destPath, bool overwrite = false, bool suppressLogging = false)
    {
        if (!suppressLogging) LogService.Info($"[FileOp] Move start: {sourcePath} -> {destPath} (overwrite={overwrite})");
        if (!overwrite && PathExists(destPath))
        {
            throw new IOException($"移動先に同名項目が残っています: {destPath}");
        }

        try
        {
            if (Directory.Exists(sourcePath))
            {
                Directory.Move(sourcePath, destPath);
                if (!suppressLogging) LogService.Info($"[FileOp] Directory.Move success: {sourcePath}");
            }
            else
            {
                File.Move(sourcePath, destPath, overwrite);
                if (!suppressLogging) LogService.Info($"[FileOp] File.Move success: {sourcePath}");
            }
        }
        catch (IOException ex) // ドライブまたぎ等を想定したフォールバック
        {
            LogService.Warn($"[FileOp] Move fallback triggered: {sourcePath} -> {destPath} (Exception: {ex.Message})");
            try
            {
                if (Directory.Exists(sourcePath))
                {
                    if (ReparsePointHelper.IsReparsePoint(sourcePath))
                    {
                        throw new IOException($"Reparse point directory move fallback is not allowed: {sourcePath}");
                    }

                    CopyDirectory(sourcePath, destPath);
                    DeleteDirectoryTreeWithoutFollowingReparsePoints(sourcePath);
                    if (!suppressLogging) LogService.Info($"[FileOp] Directory Move fallback (Copy+Delete) success: {sourcePath}");
                }
                else
                {
                    File.Copy(sourcePath, destPath, true);
                    File.Delete(sourcePath);
                    if (!suppressLogging) LogService.Info($"[FileOp] File Move fallback (Copy+Delete) success: {sourcePath}");
                }
            }
            catch (Exception ex2)
            {
                LogService.Error($"[FileOp] Move (fallback) failed: {sourcePath} -> {destPath}", ex2);
                throw;
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
        return Directory.Exists(path) || File.Exists(path);
    }


    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

        if (ReparsePointHelper.IsReparsePoint(sourceDir))
            throw new IOException($"Reparse point directory copy is not allowed: {sourceDir}");

        DirectoryInfo[] dirs = dir.GetDirectories();
        Directory.CreateDirectory(destinationDir);

        foreach (FileInfo file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (DirectoryInfo subDir in dirs)
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
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
                    IsDirectory = true
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

        string sourceRoot = Path.GetPathRoot(sourceFullPath) ?? string.Empty;
        string destinationRoot = Path.GetPathRoot(destinationFullPath) ?? string.Empty;
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

            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourceDir)) ?? string.Empty;
            string destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationDir)) ?? string.Empty;
            if (!string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new DirectoryPasteMergeGuardResult
                {
                    CanMerge = false,
                    AbortReason = DirectoryPasteMergeAbortReason.DifferentRoot,
                    BlockingPath = destinationDir,
                    Message = "貼り付け(移動)のフォルダ統合は同一ドライブ前提で別フェーズ扱いです。今回は実行しません。"
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
                if (Directory.Exists(destinationSubDirectory))
                {
                    return new DirectoryPasteMergeGuardResult
                    {
                        CanMerge = false,
                        AbortReason = DirectoryPasteMergeAbortReason.TypeMismatch,
                        BlockingPath = destinationSubDirectory,
                        Message = $"移動先に同名フォルダがあるためリンクを安全に移動できません。\n宛先: {destinationSubDirectory}"
                    };
                }
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
                if (Directory.Exists(destinationSubDirectory))
                {
                    return new DirectoryMoveMergeGuardResult
                    {
                        CanMerge = false,
                        AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                        BlockingPath = destinationSubDirectory,
                        Message = $"移動先に同名フォルダがあるためリンクを安全に移動できません。\n宛先: {destinationSubDirectory}"
                    };
                }
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
                if (Directory.Exists(destinationSubDirectory))
                {
                    return new DirectoryMoveMergeGuardResult
                    {
                        CanMerge = false,
                        AbortReason = DirectoryMoveMergeAbortReason.TypeMismatch,
                        BlockingPath = destinationSubDirectory,
                        Message = $"移動先に同名フォルダがあるためリンクを安全に移動できません。\n宛先: {destinationSubDirectory}"
                    };
                }
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