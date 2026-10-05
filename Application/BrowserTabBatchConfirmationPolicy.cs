namespace MidFD.Runtime;

internal static class BrowserTabBatchConfirmationPolicy
{
    public static bool RequiresConfirmation(int validDirectoryCount, int configuredThreshold)
    {
        int threshold = Math.Max(
            Configuration.BrowserTabSettings.MinimumMultiDirectoryOpenConfirmationThreshold,
            configuredThreshold);
        return validDirectoryCount >= threshold;
    }
}
