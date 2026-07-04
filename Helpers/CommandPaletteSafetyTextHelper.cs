using System;
using System.Collections.Generic;
using System.Linq;
using MidFD.Models;

namespace MidFD.Helpers;

public static class CommandPaletteSafetyTextHelper
{
    public static string GetSafetyLevelText(CommandPaletteSafetyLevel safetyLevel)
    {
        return safetyLevel switch
        {
            CommandPaletteSafetyLevel.Safe => "safe",
            CommandPaletteSafetyLevel.Confirm => "confirm",
            CommandPaletteSafetyLevel.Unsupported => "unsupported",
            CommandPaletteSafetyLevel.Deferred => "deferred",
            _ => safetyLevel.ToString().ToLowerInvariant()
        };
    }

    public static string BuildAttentionText(CommandLauncherCommand command)
    {
        string safetyLevel = GetSafetyLevelText(command.SafetyLevel);
        string? reason = command.SafetyInfo.ReasonText ?? command.NonExecutableMessage;

        return command.SafetyLevel switch
        {
            CommandPaletteSafetyLevel.Safe => string.Empty,
            CommandPaletteSafetyLevel.Confirm => BuildJoinedAttentionText(safetyLevel, command.SafetyInfo.TargetKindText, command.SafetyInfo.TargetCountText),
            CommandPaletteSafetyLevel.Unsupported => BuildJoinedAttentionText(safetyLevel, reason),
            CommandPaletteSafetyLevel.Deferred => BuildJoinedAttentionText(safetyLevel, reason),
            _ => BuildJoinedAttentionText(safetyLevel, reason)
        };
    }

    public static string BuildInlineStatusText(CommandLauncherCommand command)
    {
        string safetyLevel = GetSafetyLevelText(command.SafetyLevel);
        string? target = BuildTargetSummaryText(command);
        string? reason = command.SafetyInfo.ReasonText ?? command.NonExecutableMessage;

        return command.SafetyLevel switch
        {
            CommandPaletteSafetyLevel.Safe => $"安全分類: {safetyLevel}",
            CommandPaletteSafetyLevel.Confirm => BuildJoinedAttentionText($"安全分類: {safetyLevel}", target),
            CommandPaletteSafetyLevel.Unsupported => BuildJoinedAttentionText($"安全分類: {safetyLevel}", reason),
            CommandPaletteSafetyLevel.Deferred => BuildJoinedAttentionText($"安全分類: {safetyLevel}", reason),
            _ => BuildJoinedAttentionText($"安全分類: {safetyLevel}", reason)
        };
    }

    public static string BuildDetailText(CommandLauncherCommand command, string? baseDescription = null)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(baseDescription))
        {
            lines.Add(baseDescription);
        }

        lines.Add($"安全分類: {GetSafetyLevelText(command.SafetyLevel)}");
        lines.Add($"確認要否: {(command.SafetyLevel == CommandPaletteSafetyLevel.Confirm ? "あり" : "なし")}");

        if (!string.IsNullOrWhiteSpace(command.SafetyInfo.TargetKindText))
        {
            lines.Add($"対象種別: {command.SafetyInfo.TargetKindText}");
        }

        if (!string.IsNullOrWhiteSpace(command.SafetyInfo.TargetCountText))
        {
            lines.Add($"対象件数: {command.SafetyInfo.TargetCountText}");
        }

        if (!string.IsNullOrWhiteSpace(command.SafetyInfo.RepresentativePath))
        {
            lines.Add($"代表パス: {command.SafetyInfo.RepresentativePath}");
        }

        if (!string.IsNullOrWhiteSpace(command.SafetyInfo.DestinationOrOutputText))
        {
            lines.Add($"宛先/出力先: {command.SafetyInfo.DestinationOrOutputText}");
        }

        lines.Add($"破壊性: {(command.SafetyInfo.IsDestructive ? "あり" : "なし")}");

        if (!string.IsNullOrWhiteSpace(command.SafetyInfo.ImpactText))
        {
            lines.Add($"実行後: {command.SafetyInfo.ImpactText}");
        }

        string? reason = command.SafetyInfo.ReasonText ?? command.NonExecutableMessage;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            lines.Add($"理由: {reason}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildJoinedAttentionText(params string?[] parts)
    {
        return string.Join(" / ", parts.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string? BuildTargetSummaryText(CommandLauncherCommand command)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(command.SafetyInfo.TargetKindText))
        {
            parts.Add(command.SafetyInfo.TargetKindText);
        }

        if (!string.IsNullOrWhiteSpace(command.SafetyInfo.TargetCountText))
        {
            parts.Add(command.SafetyInfo.TargetCountText);
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
