using System.Collections;

using System.Linq;

namespace MidFD.Models;

/// <summary>
/// マーク済みパスの存在判定と順序保持を両立する小さな状態モデル。
/// </summary>
public sealed class MarkSelectionState : IReadOnlyCollection<string>
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _orderedPaths = new();
    private readonly Dictionary<string, bool> _knownPathKinds = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _orderedPaths.Count;

    public bool Any() => _orderedPaths.Count > 0;

    public bool Contains(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && _paths.Contains(path);
    }

    public void RememberPathKind(string? path, bool isDirectory)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _knownPathKinds[path] = isDirectory;
        }
    }

    public IReadOnlyDictionary<string, bool> BuildKnownPathKinds(
        IReadOnlyDictionary<string, bool>? currentSnapshotPathKinds,
        IEnumerable<string>? additionalPaths = null)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in _orderedPaths)
        {
            if (currentSnapshotPathKinds?.TryGetValue(path, out bool currentKind) == true)
            {
                result[path] = currentKind;
            }
            else if (_knownPathKinds.TryGetValue(path, out bool knownKind))
            {
                result[path] = knownKind;
            }
        }

        foreach (string? path in additionalPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (currentSnapshotPathKinds?.TryGetValue(path, out bool currentKind) == true)
            {
                result[path] = currentKind;
            }
            else if (_knownPathKinds.TryGetValue(path, out bool knownKind))
            {
                result[path] = knownKind;
            }
        }

        return result;
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

    public int AddRange(IEnumerable<string> paths)
    {
        int addedCount = 0;
        foreach (string path in paths)
        {
            if (Add(path))
            {
                addedCount++;
            }
        }
        return addedCount;
    }

    public bool Remove(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_paths.Remove(path))
        {
            return false;
        }

        _orderedPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _knownPathKinds.Remove(path);
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
            foreach (string path in targets)
            {
                _knownPathKinds.Remove(path);
            }
        }

        return removedCount;
    }

    public void Clear()
    {
        _paths.Clear();
        _orderedPaths.Clear();
        _knownPathKinds.Clear();
    }

    public void Restore(IEnumerable<string>? paths)
    {
        ReplaceWith(paths);
    }

    public void RestoreWithKnownKinds(IEnumerable<MarkPathKind>? paths)
    {
        var replacement = new List<MarkPathKind>();
        var replacementSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MarkPathKind entry in paths ?? Enumerable.Empty<MarkPathKind>())
        {
            if (!string.IsNullOrWhiteSpace(entry.Path) && replacementSet.Add(entry.Path))
            {
                replacement.Add(entry);
            }
        }

        _paths.Clear();
        _orderedPaths.Clear();
        _paths.UnionWith(replacementSet);
        _orderedPaths.AddRange(replacement.Select(static entry => entry.Path));
        foreach (string path in _knownPathKinds.Keys.Where(path => !replacementSet.Contains(path)).ToList())
        {
            _knownPathKinds.Remove(path);
        }
        foreach (MarkPathKind entry in replacement)
        {
            _knownPathKinds[entry.Path] = entry.IsDirectory;
        }
    }

    public void ReplaceWith(IEnumerable<string>? paths)
    {
        var replacement = new List<string>();
        var replacementSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths != null)
        {
            foreach (string? path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path) && replacementSet.Add(path))
                {
                    replacement.Add(path);
                }
            }
        }

        _paths.Clear();
        _orderedPaths.Clear();
        _paths.UnionWith(replacementSet);
        _orderedPaths.AddRange(replacement);
        foreach (string path in _knownPathKinds.Keys.Where(path => !replacementSet.Contains(path)).ToList())
        {
            _knownPathKinds.Remove(path);
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
