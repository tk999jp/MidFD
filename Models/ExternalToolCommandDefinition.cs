using System;

namespace MidFD.Models;

/// <summary>
/// コマンドパレットから実行可能な外部ツールの定義。
/// </summary>
public sealed class ExternalToolCommandDefinition
{
    /// <summary>
    /// 一意識別子。
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// 表示名。Command Palette のリストに表示される。
    /// </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 詳細説明。
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 検索用の短縮名。
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// Browser中の Alt+<key> 直起動スロット。
    /// </summary>
    public string? AltSlot { get; set; }

    /// <summary>
    /// 実行ファイルのパス。
    /// </summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// 引数テンプレート。 {currentDir}, {selectedPath} などのプレースホルダーを含む。
    /// </summary>
    public string Arguments { get; set; } = "";

    /// <summary>
    /// 作業ディレクトリ。空の場合は現在ディレクトリ。
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// 有効かどうか。
    /// </summary>
    public bool Enabled { get; set; } = true;
}
