using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MidFD.Services;

public static class GitIgnoreFilterService
{
    private const int TimeoutMilliseconds = 3000;

    public static bool TryGetIgnoredPaths(string currentDirectory, IEnumerable<string> fullPaths, out HashSet<string> ignoredFullPaths, out string? warning)
    {
        ignoredFullPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        warning = null;

        if (string.IsNullOrWhiteSpace(currentDirectory) || !Directory.Exists(currentDirectory))
        {
            return false;
        }

        if (!TryRunGit(currentDirectory, "rev-parse --show-toplevel", null, out string rootOutput, out _, out _))
        {
            return false;
        }

        string repoRoot = rootOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return false;
        }

        var pathPairs = fullPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new
            {
                FullPath = Path.GetFullPath(path),
                RelativePath = ToGitRelativePath(repoRoot, path)
            })
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.RelativePath))
            .ToList();

        if (pathPairs.Count == 0)
        {
            return true;
        }

        string stdin = string.Join(Environment.NewLine, pathPairs.Select(static pair => pair.RelativePath)) + Environment.NewLine;
        bool completed = TryRunGit(repoRoot, "check-ignore --stdin", stdin, out string output, out string error, out int exitCode);
        if (!completed)
        {
            warning = "Git ignore 判定に失敗したため、Git条件は適用しませんでした。";
            return false;
        }

        if (exitCode != 0 && exitCode != 1)
        {
            LogService.Warn($"[FilterLockGit] check-ignore failed. ExitCode={exitCode} Error={error}");
            warning = "Git ignore 判定に失敗したため、Git条件は適用しませんでした。";
            return false;
        }

        var ignoredRelatives = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pathPairs)
        {
            if (ignoredRelatives.Contains(pair.RelativePath))
            {
                ignoredFullPaths.Add(pair.FullPath);
            }
        }

        return true;
    }

    private static bool TryRunGit(string workingDirectory, string arguments, string? stdin, out string output, out string error, out int exitCode)
    {
        output = string.Empty;
        error = string.Empty;
        exitCode = -1;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                CreateNoWindow = true
            };

            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (stdin != null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(TimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            output = outputTask.GetAwaiter().GetResult();
            error = errorTask.GetAwaiter().GetResult();
            exitCode = process.ExitCode;
            return exitCode == 0 || exitCode == 1;
        }
        catch (Exception ex)
        {
            LogService.Warn($"[FilterLockGit] git execution failed. Message={ex.Message}");
            return false;
        }
    }

    private static string ToGitRelativePath(string repoRoot, string fullPath)
    {
        try
        {
            string relative = Path.GetRelativePath(repoRoot, Path.GetFullPath(fullPath));
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return string.Empty;
            }

            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }
        catch
        {
            return string.Empty;
        }
    }
}
