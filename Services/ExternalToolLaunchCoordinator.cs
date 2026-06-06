using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// 外部ツール起動前の文脈構築と事前判定をまとめる Coordinator。
/// UI は呼び出し側に残し、起動判断に必要な値だけをここで整える。
/// </summary>
public static class ExternalToolLaunchCoordinator
{
    public static ExternalToolExecutionContext BuildExecutionContext(
        string currentDirectory,
        string? selectedPath,
        string? selectedName,
        IEnumerable<string> markedPaths)
    {
        return new ExternalToolExecutionContext
        {
            CurrentDirectory = currentDirectory,
            SelectedPath = selectedPath,
            SelectedName = selectedName,
            MarkedPaths = markedPaths
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public static bool ShouldConfirmEmptyMarkedPaths(ExternalToolCommandDefinition definition, ExternalToolExecutionContext context)
    {
        if (context.MarkedPaths.Count > 0)
        {
            return false;
        }

        return ExternalToolArgumentTemplateService.UsesMarkedPathTemplate(definition.Arguments)
            || ExternalToolArgumentTemplateService.UsesMarkedPathTemplate(definition.WorkingDirectory);
    }

    public static string BuildEmptyMarkedPathsConfirmationMessage()
    {
        return "この外部ツールはマーク済みパス用テンプレートを使用しますが、現在マークは0件です。\n空のマーク一覧で起動しますか？";
    }
}
