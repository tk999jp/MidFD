using MidFD.Models;

namespace MidFD.Services;

internal static class BrowserPathEntryNavigationService
{
    public static BrowserPathEntryNavigationResult Resolve(string? inputPath, NavigationService navigationService)
    {
        string trimmed = PathTextIntakeService.ExpandAndTrim(inputPath);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return Invalid("移動先パスを入力してください。");
        }

        if (trimmed.Contains('%'))
        {
            return Invalid("環境変数を解決できませんでした。");
        }

        string resolved;
        try
        {
            resolved = navigationService.NormalizeDestinationDirectory(trimmed);
        }
        catch (Exception ex)
        {
            return Invalid($"パス解決に失敗しました: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            return Invalid("移動先パスを解決できませんでした。");
        }

        if (File.Exists(resolved) && !Directory.Exists(resolved))
        {
            return new BrowserPathEntryNavigationResult
            {
                TargetKind = BrowserPathEntryTargetKind.File,
                ResolvedPath = resolved
            };
        }

        if (!Directory.Exists(resolved))
        {
            return Invalid(BuildMissingPathMessage(resolved));
        }

        return new BrowserPathEntryNavigationResult
        {
            TargetKind = BrowserPathEntryTargetKind.Directory,
            ResolvedPath = resolved
        };
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
