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
    // F4 = Editor 起動の対象拡張子（テキスト系）
    private static readonly HashSet<string> _editorExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".ini", ".json", ".xml", ".csv", ".md",
        ".yml", ".yaml", ".cs", ".csproj", ".sln", ".bat", ".ps1",
        ".sh", ".py", ".js", ".ts", ".html", ".css", ".sql",
    };

    /// <summary>F4 キーで Editor 起動する対象拡張子かどうかを判定する。</summary>
    public static bool IsEditorTargetExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return _editorExtensions.Contains(ext);
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

    private static string? LaunchProcess(string exePath, params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            LogService.Warn($"ExternalTool exe not found: {exePath}");
            return $"実行ファイルが見つかりません: {exePath}";
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                UseShellExecute = false,
            });
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"LaunchProcess failed. Exe: {exePath}, Args: {string.Join(" ", arguments)}", ex);
            return $"起動に失敗しました: {ex.Message}";
        }
    }

    /// <summary>
    /// 指定された種別のターミナルを作業ディレクトリで開く。
    /// </summary>
    /// <returns>起動成功時は null、失敗時はエラーメッセージ。</returns>
    public static string? OpenTerminal(string workingDir, ShellKind kind)
    {
        if (!Directory.Exists(workingDir))
        {
            return $"ディレクトリが見つかりません: {workingDir}";
        }

        string fileName = kind == ShellKind.PowerShell ? "powershell.exe" : "cmd.exe";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDir,
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"OpenTerminal failed. Kind: {kind}, Dir: {workingDir}", ex);
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
            if (string.IsNullOrWhiteSpace(command))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = workingDir,
                    UseShellExecute = true
                });
                return null;
            }

            command = command.Trim();
            string fileName;
            string arguments = "";

            if (command.StartsWith("\""))
            {
                int endQuote = command.IndexOf("\"", 1);
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
                int spaceIndex = command.IndexOf(" ");
                if (spaceIndex > 0)
                {
                    fileName = command.Substring(0, spaceIndex);
                    arguments = command.Substring(spaceIndex + 1).Trim();
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
                WorkingDirectory = workingDir,
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
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return "実行ファイルが指定されていません。";
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDir,
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

    private static string QuoteArgument(string value)
    {
        string escaped = (value ?? string.Empty).Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
