using System.Collections;

namespace MidFD.Models;

/// <summary>
/// マーク済みパスの存在判定と順序保持を両立する小さな状態モデル。
/// </summary>
public sealed class MarkSelectionState : IReadOnlyCollection<string>
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _orderedPaths = new();

    public int Count => _orderedPaths.Count;

    public bool Any() => _orderedPaths.Count > 0;

    public bool Contains(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && _paths.Contains(path);
    }

    public bool Add(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_paths.Add(path))
        {
            return false;
        }

        _orderedPaths.Add(path);
        return true;
    }

    public bool Remove(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_paths.Remove(path))
        {
            return false;
        }

        _orderedPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public int RemoveRange(IEnumerable<string> paths)
    {
        int removedCount = 0;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p) && _paths.Remove(p))
            {
                targets.Add(p);
                removedCount++;
            }
        }

        if (removedCount > 0)
        {
            _orderedPaths.RemoveAll(p => targets.Contains(p));
        }

        return removedCount;
    }

    public void Clear()
    {
        _paths.Clear();
        _orderedPaths.Clear();
    }

    public void Restore(IEnumerable<string>? paths)
    {
        Clear();
        if (paths == null) return;

        foreach (var path in paths)
        {
            Add(path);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        return _orderedPaths.ToList();
    }

    public IEnumerator<string> GetEnumerator()
    {
        return _orderedPaths.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
