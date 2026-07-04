using System;

namespace MidFD.Models;

/// <summary>
/// コマンドランチャー（コマンドパレット）で実行可能なコマンドの定義。
/// </summary>
public sealed class CommandLauncherCommand
{
    /// <summary>
    /// 一意識別子。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 表示名。検索対象。
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// 詳細説明。検索対象。
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// 補助検索文字列（alias / altSlot / executablePath など）。
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>
    /// 補助表示（alias / Alt slot など）。
    /// </summary>
    public string? SecondaryText { get; init; }

    /// <summary>
    /// layer 表示用の短いバッジ文字列。
    /// </summary>
    public string? LayerBadge { get; init; }

    /// <summary>
    /// layer 表示用の種別ラベル。
    /// </summary>
    public string? LayerKind { get; init; }

    /// <summary>
    /// カテゴリ（Browser, App, Mark 等）。検索対象。
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// 実行可能かどうかの判定ロジック。
    /// </summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>
    /// 実行不可時に表示する短いフィードバックメッセージ。
    /// </summary>
    public string? NonExecutableMessage { get; init; }

    /// <summary>
    /// 実行ロジック。
    /// </summary>
    public required Action Execute { get; init; }

    /// <summary>
    /// 実行ではなく検索欄へ注入する入力文字列。
    /// </summary>
    public string? QueryInsertText { get; init; }

    /// <summary>
    /// 実行時に検索欄を空へ戻す。
    /// </summary>
    public bool ClearsSearchText { get; init; }

    public string? Title { get; init; }
    public string? Group { get; init; }
    public string? Kind { get; init; }
    public string? Keywords { get; init; }
    public string? Subtitle { get; init; }
    public string? KeyBindingText { get; init; }
    public CommandPaletteActionKind ActionKind { get; init; } = CommandPaletteActionKind.Execute;
    public CommandPaletteSafetyLevel SafetyLevel { get; init; } = CommandPaletteSafetyLevel.Safe;
    public CommandPaletteSafetyInfo SafetyInfo { get; init; } = new();
    public int Score { get; set; }
}

public enum CommandPaletteSafetyLevel
{
    Safe,
    Confirm,
    Unsupported,
    Deferred
}

public sealed record CommandPaletteSafetyInfo
{
    public string? TargetKindText { get; init; }
    public string? TargetCountText { get; init; }
    public string? RepresentativePath { get; init; }
    public string? DestinationOrOutputText { get; init; }
    public string? ImpactText { get; init; }
    public string? ReasonText { get; init; }
    public bool IsDestructive { get; init; }
}
