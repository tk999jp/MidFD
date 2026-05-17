using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Services
{
    /// <summary>
    /// MainForm の状態から操作対象のパス群を解決するサービス。
    /// </summary>
    public static class SelectionResolver
    {
        /// <summary>
        /// 現在のマーク状態とカーソル位置から、操作対象を解決する。
        /// </summary>
        /// <param name="markedFiles">マークされたファイルのリスト</param>
        /// <param name="cursorItem">現在のカーソル位置のアイテム</param>
        /// <returns>解決結果 (SelectionResult)</returns>
        public static SelectionResult Resolve(IEnumerable<string> markedFiles, ListViewItem? cursorItem)
        {
            // 1. マーク優先
            if (markedFiles != null && markedFiles.Any())
            {
                // マークリストに不正なものが混じっていないか（念のため）
                // ただし .. は元々マークできない設計なので、ここでは純粋に返す
                return new SelectionResult(markedFiles, true);
            }

            // 2. カーソルフォールバック
            if (cursorItem != null && cursorItem.Text != "..")
            {
                string? path = cursorItem.Tag as string;
                if (!string.IsNullOrEmpty(path))
                {
                    return new SelectionResult(new[] { path }, false);
                }
            }

            return SelectionResult.Empty;
        }
    }
}
