using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// 外部ツールの引数テンプレートを展開するサービス。
/// </summary>
public static class ExternalToolArgumentTemplateService
{
    public static bool UsesMarkedPathTemplate(string? template)
    {
        return !string.IsNullOrEmpty(template)
            && (template.Contains("{markedPaths}") || template.Contains("{markedPathsFile}"));
    }

    /// <summary>
    /// テンプレート文字列を実際の値で展開する。
    /// 一時ファイルを生成した場合は、生成されたファイルパスの一覧を返す（呼び出し元で削除するため）。
    /// </summary>
    public static string Resolve(string template, ExternalToolExecutionContext context, out List<string> temporaryFiles)
    {
        temporaryFiles = new List<string>();
        if (string.IsNullOrEmpty(template)) return "";

        string resolved = template;

        // {currentDir}
        resolved = resolved.Replace("{currentDir}", Quote(context.CurrentDirectory));

        // {selectedPath}
        resolved = resolved.Replace("{selectedPath}", Quote(context.SelectedPath ?? ""));

        // {selectedName}
        resolved = resolved.Replace("{selectedName}", Quote(context.SelectedName ?? ""));

        // {markedPaths}
        if (resolved.Contains("{markedPaths}"))
        {
            string joined = string.Join(" ", context.MarkedPaths.Select(Quote));
            resolved = resolved.Replace("{markedPaths}", joined);
        }

        // {markedPathsFile}
        if (resolved.Contains("{markedPathsFile}"))
        {
            string tempFile = CreateMarkedPathsFile(context.MarkedPaths);
            if (!string.IsNullOrEmpty(tempFile))
            {
                temporaryFiles.Add(tempFile);
                resolved = resolved.Replace("{markedPathsFile}", Quote(tempFile));
            }
            else
            {
                resolved = resolved.Replace("{markedPathsFile}", "");
            }
        }

        return resolved;
    }

    private static string Quote(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        // 既に引用符で囲まれている場合はそのまま
        if (path.StartsWith("\"") && path.EndsWith("\"")) return path;

        // スペースが含まれているか、特定の文字が含まれている場合は引用符で囲む
        // 基本的に常に囲んでも Process.Start (UseShellExecute=false) なら問題ないことが多いが、
        // ここでは安全にスペースがある場合のみ、または常に囲む方針とする。
        // プロンプトの指示「quote が必要な path は " で囲む」に従う。
        return $"\"{path.Replace("\"", "\\\"")}\"";
    }

    private static string CreateMarkedPathsFile(IReadOnlyList<string> paths)
    {
        try
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"midfd_marks_{Guid.NewGuid():N}.txt");
            // UTF-8 BOMなしで1行1パス
            File.WriteAllLines(tempFile, paths, new UTF8Encoding(false));
            return tempFile;
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to create temporary marked paths file.", ex);
            return "";
        }
    }
}
