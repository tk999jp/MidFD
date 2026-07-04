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
            var sortedPaths = pathList
                .Select(p => Path.GetFullPath(p))
                .OrderBy(p => p.Length)
                .ToList();

            foreach (var path in sortedPaths)
            {
                bool hasParent = false;
                foreach (var parentCandidate in normalizedPaths)
                {
                    string p = parentCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string c = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (p.Equals(c, StringComparison.OrdinalIgnoreCase))
                    {
                        hasParent = true;
                        break;
                    }

                    string prefix = p + Path.DirectorySeparatorChar;
                    if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        hasParent = true;
                        break;
                    }
                }

                if (!hasParent)
                {
                    normalizedPaths.Add(path);
                }
            }

            return normalizedPaths;
        }
    }
}
