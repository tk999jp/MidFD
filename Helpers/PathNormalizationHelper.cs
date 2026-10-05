using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MidFD.Helpers
{
    public static class PathNormalizationHelper
    {
        /// <summary>
        /// パスリストから親フォルダと子パスが混在している場合に、親フォルダ配下にある子パスを除外して正規化します。
        /// 同時に、重複するパスも1件にまとめます。
        /// </summary>
        public static IReadOnlyList<string> FilterParentChildPaths(IEnumerable<string> paths)
            => FilterParentChildPathsCore(paths, null);

        internal static IReadOnlyList<string> FilterParentChildPathsWithDiagnostics(
            IEnumerable<string> paths,
            Action<string> ancestorLookup)
        {
            ArgumentNullException.ThrowIfNull(ancestorLookup);
            return FilterParentChildPathsCore(paths, ancestorLookup);
        }

        private static IReadOnlyList<string> FilterParentChildPathsCore(
            IEnumerable<string> paths,
            Action<string>? ancestorLookup)
        {
            if (paths == null)
            {
                return new List<string>();
            }

            var pathList = paths.ToList();
            if (pathList.Count <= 1)
            {
                return pathList;
            }

            var normalizedPaths = new List<string>();
            var normalizedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sortedPaths = pathList
                .Select(p => Path.GetFullPath(p))
                .OrderBy(p => p.Length)
                .ToList();

            foreach (var path in sortedPaths)
            {
                string pathKey = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normalizedPathSet.Contains(pathKey))
                {
                    continue;
                }

                bool hasParent = false;
                string? parentCandidate = Path.GetDirectoryName(pathKey);
                while (!string.IsNullOrEmpty(parentCandidate))
                {
                    parentCandidate = parentCandidate.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    ancestorLookup?.Invoke(parentCandidate);
                    if (normalizedPathSet.Contains(parentCandidate))
                    {
                        hasParent = true;
                        break;
                    }

                    string? nextParent = Path.GetDirectoryName(parentCandidate);
                    if (string.Equals(nextParent, parentCandidate, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    parentCandidate = nextParent;
                }

                if (!hasParent)
                {
                    normalizedPathSet.Add(pathKey);
                    normalizedPaths.Add(path);
                }
            }

            return normalizedPaths;
        }
    }
}
