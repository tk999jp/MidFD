using System;
using System.Collections.Generic;
using System.Linq;

namespace MidFD.Helpers;

public static class HistoryHelper
{
    public const int MaxHistoryCount = 50;

    /// <summary>
    /// 履歴リストを正規化します（空文字除外、連続重複抑制、最大件数制限）。
    /// </summary>
    /// <param name="history">正規化対象の履歴（インデックス 0 が最新）</param>
    /// <returns>正規化済みの履歴リスト</returns>
    public static List<string> Normalize(IEnumerable<string>? history)
    {
        var result = new List<string>();
        if (history == null) return result;

        string? last = null;
        foreach (var path in history)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            
            // 連続する重複を避ける（大文字小文字を区別しない）
            if (string.Equals(path, last, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(path);
            last = path;
        }

        // 最新の MaxHistoryCount 件を保持
        if (result.Count > MaxHistoryCount)
        {
            return result.Take(MaxHistoryCount).ToList();
        }

        return result;
    }
}
