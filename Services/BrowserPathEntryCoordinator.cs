using MidFD.Models;

namespace MidFD.Services;

internal static class BrowserPathEntryCoordinator
{
    public static BrowserPathEntryApplyResult Apply(
        string? inputPath,
        Func<string, string> normalizeDestinationDirectory,
        Func<string, bool> navigateDirectory,
        Func<string, string?> selectFile)
    {
        BrowserPathEntryNavigationResult result = BrowserPathEntryNavigationService.Resolve(inputPath, normalizeDestinationDirectory);
        return ApplyResolved(result, navigateDirectory, selectFile);
    }

    public static BrowserPathEntryApplyResult Apply(
        string? inputPath,
        NavigationService navigationService,
        Func<string, bool> navigateDirectory,
        Func<string, string?> selectFile)
    {
        BrowserPathEntryNavigationResult result = BrowserPathEntryNavigationService.Resolve(inputPath, navigationService);
        return ApplyResolved(result, navigateDirectory, selectFile);
    }

    private static BrowserPathEntryApplyResult ApplyResolved(
        BrowserPathEntryNavigationResult result,
        Func<string, bool> navigateDirectory,
        Func<string, string?> selectFile)
    {
        if (result.TargetKind == BrowserPathEntryTargetKind.None)
        {
            return new BrowserPathEntryApplyResult
            {
                Succeeded = false,
                ShouldCloseEditor = false,
                StatusMessage = result.StatusMessage
            };
        }

        if (result.TargetKind == BrowserPathEntryTargetKind.Directory)
        {
            if (!navigateDirectory(result.ResolvedPath))
            {
                return new BrowserPathEntryApplyResult
                {
                    Succeeded = false,
                    ShouldCloseEditor = false,
                    StatusMessage = "指定されたフォルダーへ移動できませんでした。"
                };
            }

            return new BrowserPathEntryApplyResult
            {
                Succeeded = true,
                ShouldCloseEditor = true
            };
        }

        string? error = selectFile(result.ResolvedPath);
        return new BrowserPathEntryApplyResult
        {
            Succeeded = string.IsNullOrWhiteSpace(error),
            ShouldCloseEditor = string.IsNullOrWhiteSpace(error),
            StatusMessage = string.IsNullOrWhiteSpace(error)
                ? BrowserPathEntryNavigationService.BuildFileSelectionSuccessMessage(result.ResolvedPath)
                : error ?? string.Empty
        };
    }
}
