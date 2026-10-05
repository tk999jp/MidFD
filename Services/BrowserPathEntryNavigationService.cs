using MidFD.Models;

namespace MidFD.Services;

internal static class BrowserPathEntryNavigationService
{
    public static BrowserPathEntryNavigationResult Resolve(string? inputPath, Func<string, string> normalizeDestinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(normalizeDestinationDirectory);
        return ResolveCore(inputPath, normalizeDestinationDirectory);
    }

    public static BrowserPathEntryNavigationResult Resolve(string? inputPath, NavigationService navigationService)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        return ResolveCore(inputPath, navigationService.NormalizeDestinationDirectory);
    }

    private static BrowserPathEntryNavigationResult ResolveCore(
        string? inputPath,
        Func<string, string> normalizeDestinationDirectory)
    {
        string[] candidates = PathTextIntakeService.EnumeratePathCandidates(inputPath).ToArray();
        if (candidates.Length == 0)
        {
            return Invalid("移動先パスを入力してください。");
        }

        string? lastResolvedPath = null;
        string? lastResolutionError = null;
        foreach (string candidate in candidates)
        {
            string resolved;
            try
            {
                resolved = normalizeDestinationDirectory(candidate);
            }
            catch (Exception ex)
            {
                lastResolutionError = $"パス解決に失敗しました: {ex.Message}";
                continue;
            }

            if (string.IsNullOrWhiteSpace(resolved)) continue;
            lastResolvedPath = resolved;

            if (File.Exists(resolved) && !Directory.Exists(resolved))
            {
                return new BrowserPathEntryNavigationResult
                {
                    TargetKind = BrowserPathEntryTargetKind.File,
                    ResolvedPath = resolved
                };
            }

            if (Directory.Exists(resolved))
            {
                return new BrowserPathEntryNavigationResult
                {
                    TargetKind = BrowserPathEntryTargetKind.Directory,
                    ResolvedPath = resolved
                };
            }
        }

        if (lastResolvedPath != null) return Invalid(BuildMissingPathMessage(lastResolvedPath));
        return Invalid(lastResolutionError ?? "移動先パスを解決できませんでした。");
    }

    public static string BuildMissingPathMessage(string path)
    {
        return $"指定されたパスが見つかりません: {path}";
    }

    public static string BuildFileOpenSuccessMessage(string path)
    {
        return $"既定アプリで開きました: {Path.GetFileName(path)}";
    }

    private static BrowserPathEntryNavigationResult Invalid(string message)
    {
        return new BrowserPathEntryNavigationResult
        {
            TargetKind = BrowserPathEntryTargetKind.None,
            StatusMessage = message
        };
    }
}
