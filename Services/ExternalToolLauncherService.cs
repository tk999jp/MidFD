using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// 外部ツールの起動を担うサービス。
/// </summary>
public static class ExternalToolLauncherService
{
    /// <summary>
    /// 外部ツールを起動する。
    /// </summary>
    /// <returns>起動成功時は null、失敗時はエラーメッセージを返す。</returns>
    public static string? Launch(ExternalToolCommandDefinition definition, ExternalToolExecutionContext context)
    {
        List<string> tempFiles = new();
        try
        {
            if (string.IsNullOrWhiteSpace(definition.ExecutablePath))
            {
                return "外部ツールの実行ファイルが未設定です。";
            }
            if (!Path.IsPathRooted(definition.ExecutablePath))
            {
                return "外部ツールの実行ファイルは絶対パスで指定してください。";
            }
            string normalizedExePath = Path.GetFullPath(definition.ExecutablePath);
            if (!File.Exists(normalizedExePath))
            {
                return $"外部ツールの実行ファイルが見つかりません: {normalizedExePath}";
            }

            // 引数の展開
            string resolvedArgs = ExternalToolArgumentTemplateService.Resolve(definition.Arguments, context, out tempFiles);

            // 作業ディレクトリの解決
            string? workingDir = definition.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDir))
            {
                workingDir = context.CurrentDirectory;
            }
            else
            {
                // テンプレート展開（WorkingDirectory にもテンプレートを使えるようにしておく）
                workingDir = ExternalToolArgumentTemplateService.Resolve(workingDir, context, out var wdTempFiles);
                tempFiles.AddRange(wdTempFiles);
                // 引用符がついている可能性があるので除去
                workingDir = workingDir.Trim('"');
            }

            if (!string.IsNullOrEmpty(workingDir) && !Directory.Exists(workingDir))
            {
                workingDir = context.CurrentDirectory;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = normalizedExePath,
                Arguments = resolvedArgs,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            Process.Start(startInfo);
            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to launch external tool: {definition.DisplayName} ({definition.ExecutablePath})", ex);
            return $"起動に失敗しました: {ex.Message}";
        }
        finally
        {
            // 起動直後の削除は外部ツールが読み込む前に消えるリスクがあるため廃止。
            // 代わりに、Launch 時に古い一時ファイルを掃除する。
            CleanupOldTemporaryFiles();
        }
    }

    private static void CleanupOldTemporaryFiles()
    {
        try
        {
            string tempDir = Path.GetTempPath();
            var now = DateTime.Now;
            var threshold = now.AddDays(-7); // 7日以上前のものを削除

            // midfd_marks_*.txt を対象にする
            var files = Directory.GetFiles(tempDir, "midfd_marks_*.txt");
            foreach (var file in files)
            {
                try
                {
                    var lastWrite = File.GetLastWriteTime(file);
                    if (lastWrite < threshold)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 個別のファイル削除失敗は無視
                }
            }
        }
        catch
        {
            // ディレクトリ一覧取得失敗などは無視
        }
    }
}
