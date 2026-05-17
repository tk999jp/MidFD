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
    /// カテゴリ（Browser, App, Mark 等）。検索対象。
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// 実行可能かどうかの判定ロジック。
    /// </summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>
    /// 実行ロジック。
    /// </summary>
    public required Action Execute { get; init; }
}
