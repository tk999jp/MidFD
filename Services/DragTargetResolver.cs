namespace MidFD.Services;

public static class DragTargetResolver
{
    public static IReadOnlyList<string> Resolve(
        IEnumerable<string> markedPaths,
        string? grabbedPath,
        bool includeGrabbedWhenUnmarked = true)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasMarks = markedPaths.Any();
        IEnumerable<string> roots = hasMarks
            ? markedPaths.Concat(includeGrabbedWhenUnmarked && !markedPaths.Contains(grabbedPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                ? new[] { grabbedPath! } : Array.Empty<string>())
            : new[] { grabbedPath! };
        foreach (string? path in roots)
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path))) continue;
            string identity = PathTextIntakeService.CanonicalIdentity(path);
            if (seen.Add(identity)) result.Add(path);
        }
        return result;
    }
}
