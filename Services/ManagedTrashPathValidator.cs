using MidFD.Services.TrashManifestStore;

namespace MidFD.Services;

internal sealed class ManagedTrashPathValidator
{
    private const string TrashDirectoryName = ".midfd-trash";
    private const string ItemsDirectoryName = "items";
    private readonly string[] _compatibilityItemsRoots;
    private readonly Func<string, bool> _isReparsePoint;

    public ManagedTrashPathValidator(
        IEnumerable<string>? compatibilityItemsRoots = null,
        Func<string, bool>? isReparsePoint = null)
    {
        _compatibilityItemsRoots = (compatibilityItemsRoots ?? Array.Empty<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _isReparsePoint = isReparsePoint ?? IsExistingReparsePoint;
    }

    public string ValidatePath(string path)
    {
        if (!TryResolveItemsRoot(path, out string? itemsRoot) || itemsRoot == null)
        {
            throw new InvalidOperationException("管理ゴミ箱root外のpathは操作できません。");
        }

        string fullPath = Normalize(path);
        EnsureContained(fullPath, itemsRoot);
        EnsureNoReparsePoint(GetValidationRoot(itemsRoot), fullPath);
        return fullPath;
    }

    public string ValidateRecord(TrashManifestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateIdentitySegment(record.BatchId, nameof(record.BatchId));
        ValidateIdentitySegment(record.ItemId, nameof(record.ItemId));

        string fullPath = ValidatePath(record.TrashPath);
        if (!TryResolveItemsRoot(fullPath, out string? itemsRoot) || itemsRoot == null)
        {
            throw new InvalidOperationException("管理ゴミ箱recordの物理rootを解決できません。");
        }

        string relative = Path.GetRelativePath(itemsRoot, fullPath);
        string[] segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 ||
            !string.Equals(segments[0], record.BatchId, StringComparison.OrdinalIgnoreCase) ||
            !segments[1].StartsWith(record.ItemId + "_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("管理ゴミ箱recordと物理pathのidentityが一致しません。");
        }

        return fullPath;
    }

    public bool TryResolveItemsRoot(string path, out string? itemsRoot)
    {
        itemsRoot = null;
        string fullPath;
        try
        {
            fullPath = Normalize(path);
        }
        catch
        {
            return false;
        }

        foreach (string compatibilityRoot in _compatibilityItemsRoots)
        {
            if (IsWithin(fullPath, compatibilityRoot))
            {
                itemsRoot = compatibilityRoot;
                return true;
            }
        }

        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot)) return false;
        string sameVolumeItemsRoot = Normalize(Path.Combine(pathRoot, TrashDirectoryName, ItemsDirectoryName));
        if (!IsWithin(fullPath, sameVolumeItemsRoot)) return false;

        itemsRoot = sameVolumeItemsRoot;
        return true;
    }

    public bool IsSafeItemsRoot(string itemsRoot)
    {
        try
        {
            string normalized = Normalize(itemsRoot);
            string probePath = Path.Combine(normalized, ".validation-probe");
            if (!TryResolveItemsRoot(probePath, out string? resolved) ||
                !string.Equals(normalized, resolved, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            EnsureNoReparsePoint(GetValidationRoot(normalized), normalized);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetValidationRoot(string itemsRoot)
    {
        string? volumeRoot = Path.GetPathRoot(itemsRoot);
        if (string.IsNullOrWhiteSpace(volumeRoot)) throw new InvalidOperationException("管理ゴミ箱volume rootを解決できません。");
        return Normalize(volumeRoot);
    }

    private static void ValidateIdentitySegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException($"管理ゴミ箱recordの{name}が不正です。");
        }
    }

    private static void EnsureContained(string path, string itemsRoot)
    {
        if (!IsWithin(path, itemsRoot) || string.Equals(path, itemsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("管理ゴミ箱item pathがitems root直下の対象を指していません。");
        }
    }

    private static bool IsWithin(string path, string parent)
    {
        string normalizedPath = Normalize(path);
        string normalizedParent = Normalize(parent);
        return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must not be empty.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void EnsureNoReparsePoint(string root, string target)
    {
        if (_isReparsePoint(root)) throw new IOException("管理ゴミ箱rootがreparse pointです。");

        string relative = Path.GetRelativePath(root, target);
        string current = root;
        foreach (string segment in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (_isReparsePoint(current)) throw new IOException($"管理ゴミ箱pathにreparse pointが含まれます: {current}");
        }
    }

    private static bool IsExistingReparsePoint(string path) =>
        (File.Exists(path) || Directory.Exists(path)) && ReparsePointHelper.IsReparsePoint(path);
}
