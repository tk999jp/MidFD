using System;
using System.Collections.Generic;

namespace MidFD.Models;

/// <summary>
/// 外部ツール起動時の実行コンテキスト。
/// 引数テンプレートの展開に使用される。
/// </summary>
public sealed class ExternalToolExecutionContext
{
    /// <summary>
    /// 現在のディレクトリ。
    /// </summary>
    public string CurrentDirectory { get; init; } = "";

    /// <summary>
    /// 選択中の項目のフルパス（存在する場合）。
    /// </summary>
    public string? SelectedPath { get; init; }

    /// <summary>
    /// 選択中の項目の名前（存在する場合）。
    /// </summary>
    public string? SelectedName { get; init; }

    /// <summary>
    /// マークされている項目のフルパス一覧。
    /// </summary>
    public IReadOnlyList<string> MarkedPaths { get; init; } = Array.Empty<string>();
}
