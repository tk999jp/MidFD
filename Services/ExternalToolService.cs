using System.Diagnostics;

namespace MidFD.Services;

/// <summary>
/// ターミナル（PowerShell / cmd）の種別を表す。
/// </summary>
public enum ShellKind
{
    PowerShell,
    CommandPrompt,
}

/// <summary>
/// 外部ツール（Viewer / Editor）の起動を担うサービス。
/// UI処理（ダイアログ表示など）は呼び出し元で行い、本サービスはプロセス起動に専念する。
/// </summary>
public static class ExternalToolService
{
    private static readonly HashSet<string> AllowedBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd"
    };

    // F4 = Editor 起動の対象拡張子（テキスト系）
    private static readonly HashSet<string> EditorExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".ini", ".json", ".xml", ".csv", ".md",
        ".yml", ".yaml", ".cs", ".csproj", ".sln", ".bat", ".ps1",
        ".sh", ".py", ".js", ".ts", ".html", ".css", ".sql",
    };

    /// <summary>F4 キーで Editor 起動する対象拡張子かどうかを判定する。</summary>
    public static bool IsEditorTargetExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return EditorExtensions.Contains(ext);
    }

    /// <summary>
    /// 外部 Viewer を使用してファイルを開く。
    /// </summary>
    /// <returns>起動成功時は null、失敗時はエラーメッセージを返す。</returns>
    public static string? OpenWithViewer(string exePath, string filePath)
    {
        return LaunchProcess(exePath, filePath);
    }

    /// <summary>
    /// 外部 Editor を使用してファイルを開く。
    /// </summary>
    /// <returns>起動成功時は null、失敗時はエラーメッセージを返す。</returns>
    public static string? OpenWithEditor(string exePath, string filePath)
    {
        return LaunchProcess(exePath, filePath);
    }

    /// <summary>
    /// 外部 Diff ツールを使用して 2 件のファイル比較を開始する。
    /// </summary>
    public static string? OpenWithDiff(string exePath, string leftPath, string rightPath)
    {
        return LaunchProcess(exePath, leftPath, rightPath);
    }

    /// <summary>
    /// 外部 Editor を使用して、指定されたコマンド引数でファイルを開く。
    /// </summary>
    public static string? OpenWithEditorCommand(string exePath, string workingDir, string commandArgs)
    {
        return ExecuteCommand(exePath, commandArgs, workingDir);
    }

    /// <summary>
    /// OSの既定関連付けでファイルまたはフォルダを開く。
    /// </summary>
    public static string? OpenWithShellAssociation(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "対象パスが空です。";
        }

        try
        {
            string normalized = Path.GetFullPath(path);
            if (!File.Exists(normalized) && !Directory.Exists(normalized))
            {
                return $"対象が見つかりません: {normalized}";
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = normalized,
                UseShellExecute = true
            });
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"OpenWithShellAssociation failed. Path: {path}", ex);
            return $"起動に失敗しました: {ex.Message}";
        }
    }

    private static string? LaunchProcess(string exePath, params string[] arguments)
    {
        if (!TryNormalizeExecutablePath(exePath, out string normalizedExePath, out string error))
        {
            LogService.Warn($"ExternalTool exe validation failed: {exePath} reason={error}");
            return error;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = normalizedExePath,
                UseShellExecute = false
            };

            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument ?? string.Empty);
            }

            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"LaunchProcess failed. Exe: {normalizedExePath}", ex);
            return $"起動に失敗しました: {ex.Message}";
        }
    }

    /// <summary>
    /// 指定された種別のターミナルを作業ディレクトリで開く。
    /// </summary>
    /// <returns>起動成功時は null、失敗時はエラーメッセージ。</returns>
    public static string? OpenTerminal(string workingDir, ShellKind kind)
    {
        if (!TryNormalizeExistingDirectory(workingDir, out string normalizedWorkingDir, out string error))
        {
            return error;
        }

        string fileName = kind == ShellKind.PowerShell ? "powershell.exe" : "cmd.exe";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = normalizedWorkingDir,
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"OpenTerminal failed. Kind: {kind}, Dir: {normalizedWorkingDir}", ex);
            return $"{fileName} の起動に失敗しました: {ex.Message}";
        }
    }

    /// <summary>
    /// シェルコマンドを実行する（Hコマンド用）
    /// </summary>
    public static string? ExecuteShell(string workingDir, string? command)
    {
        try
        {
            if (!TryNormalizeExistingDirectory(workingDir, out string normalizedWorkingDir, out string directoryError))
            {
                return directoryError;
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = normalizedWorkingDir,
                    UseShellExecute = true
                });
                return null;
            }

            command = command.Trim();
            string fileName;
            string arguments = string.Empty;

            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = command.IndexOf("\"", 1, StringComparison.Ordinal);
                if (endQuote > 0)
                {
                    fileName = command.Substring(1, endQuote - 1);
                    if (command.Length > endQuote + 1)
                    {
                        arguments = command.Substring(endQuote + 1).Trim();
                    }
                }
                else
                {
                    fileName = command.Substring(1);
                }
            }
            else
            {
                int spaceIndex = command.IndexOf(" ", StringComparison.Ordinal);
                if (spaceIndex > 0)
                {
                    fileName = command[..spaceIndex];
                    arguments = command[(spaceIndex + 1)..].Trim();
                }
                else
                {
                    fileName = command;
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = normalizedWorkingDir,
                UseShellExecute = true
            });

            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"ExecuteShell failed. Cmd: {command}", ex);
            return $"起動に失敗しました: {ex.Message}";
        }
    }

    /// <summary>
    /// 指定された実行ファイルと引数でプロセスを起動する（Xコマンド用）
    /// </summary>
    public static string? ExecuteCommand(string exePath, string arguments, string workingDir)
    {
        try
        {
            if (!TryNormalizeExecutablePath(exePath, out string normalizedExePath, out string error))
            {
                return error;
            }

            if (!TryNormalizeExistingDirectory(workingDir, out string normalizedWorkingDir, out _))
            {
                normalizedWorkingDir = Path.GetDirectoryName(normalizedExePath) ?? Environment.CurrentDirectory;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = normalizedExePath,
                Arguments = arguments,
                WorkingDirectory = normalizedWorkingDir,
                UseShellExecute = true // OSの関連付けや標準の起動フローを利用
            });
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"ExecuteCommand failed. Exe: {exePath}, Args: {arguments}", ex);
            return $"実行に失敗しました: {ex.Message}";
        }
    }

    private static bool TryNormalizeExecutablePath(string? exePath, out string normalizedPath, out string error)
    {
        normalizedPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            error = "実行ファイルが指定されていません。";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(exePath);
        }
        catch (Exception ex)
        {
            error = $"実行ファイルパスが不正です: {ex.Message}";
            return false;
        }

        if (!File.Exists(normalizedPath))
        {
            error = $"実行ファイルが見つかりません: {normalizedPath}";
            return false;
        }

        if (!AllowedBinaryExtensions.Contains(Path.GetExtension(normalizedPath)))
        {
            error = $"許可されていない実行ファイル形式です: {Path.GetExtension(normalizedPath)}";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeExistingDirectory(string? directoryPath, out string normalizedPath, out string error)
    {
        normalizedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            error = "ディレクトリが指定されていません。";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(directoryPath);
        }
        catch (Exception ex)
        {
            error = $"ディレクトリパスが不正です: {ex.Message}";
            return false;
        }

        if (!Directory.Exists(normalizedPath))
        {
            error = $"ディレクトリが見つかりません: {normalizedPath}";
            return false;
        }

        return true;
    }
}
