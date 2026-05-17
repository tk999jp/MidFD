using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MidFD.Models
{
    /// <summary>
    /// 対象解決の結果を保持する不変クラス。
    /// </summary>
    public class SelectionResult
    {
        /// <summary>
        /// 解決された絶対パスのリスト。
        /// </summary>
        public IReadOnlyList<string> FullPaths { get; }

        /// <summary>
        /// マークされた選択が含まれているかどうか。
        /// </summary>
        public bool HasMarkedSelection { get; }

        /// <summary>
        /// 対象の件数。
        /// </summary>
        public int Count => FullPaths.Count;

        /// <summary>
        /// 複数選択（マーク）かどうか。
        /// </summary>
        public bool IsMultiple => Count > 1;

        /// <summary>
        /// 先頭のパス。
        /// </summary>
        public string? FirstPath => FullPaths.FirstOrDefault();

        /// <summary>
        /// 先頭のファイル名。
        /// </summary>
        public string? FirstFileName => FirstPath != null ? Path.GetFileName(FirstPath) : null;

        public SelectionResult(IEnumerable<string> paths, bool hasMarkedSelection)
        {
            FullPaths = paths?.ToList() ?? new List<string>();
            HasMarkedSelection = hasMarkedSelection;
        }

        /// <summary>
        /// 空の結果。
        /// </summary>
        public static SelectionResult Empty { get; } = new SelectionResult(Enumerable.Empty<string>(), false);
    }
}
