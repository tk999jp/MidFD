using MidFD.Models;

namespace MidFD.Services;

public static class MarkOperationLifecycleResolver
{
    public static IReadOnlyList<string> Reconcile(
        IReadOnlyList<string> snapshot,
        IReadOnlySet<string> visibleBefore,
        IReadOnlySet<string> visibleAfter,
        FileOpExitStatus status,
        Func<string, bool> exists,
        IReadOnlyDictionary<string, string>? renameMap = null)
    {
        if (status is FileOpExitStatus.Canceled or FileOpExitStatus.Error or FileOpExitStatus.Skipped)
        {
            return snapshot.ToList();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in snapshot)
        {
            string path = renameMap != null && renameMap.TryGetValue(source, out string? renamed) ? renamed : source;
            // page切替／ページングで一時的にListView外へ出ても、実体が残るmarkは維持する。
            // delete／move-out等の確定結果は、呼び出し側のoperation result／exists判定で除外する。
            if (!exists(path)) continue;
            if (seen.Add(path)) result.Add(path);
        }
        return result;
    }
}
