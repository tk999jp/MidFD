using MidFD.Models;

namespace MidFD.Services;

internal static class BrowserPathEntryCoordinator
{
    public static BrowserPathEntryApplyResult Apply(
        string? inputPath,
        NavigationService navigationService,
        Action<string> navigateDirectory,
        Func<string, string?> openFile)
    {
        BrowserPathEntryNavigationResult result = BrowserPathEntryNavigationService.Resolve(inputPath, navigationService);
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
            navigateDirectory(result.ResolvedPath);
            return new BrowserPathEntryApplyResult
            {
                Succeeded = true,
                ShouldCloseEditor = true
            };
        }

        string? error = openFile(result.ResolvedPath);
        return new BrowserPathEntryApplyResult
        {
            Succeeded = string.IsNullOrWhiteSpace(error),
            ShouldCloseEditor = string.IsNullOrWhiteSpace(error),
            StatusMessage = string.IsNullOrWhiteSpace(error)
                ? BrowserPathEntryNavigationService.BuildFileOpenSuccessMessage(result.ResolvedPath)
                : error ?? string.Empty
        };
    }
}
