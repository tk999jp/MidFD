namespace MidFD.Services;

internal enum ExternalUrlLaunchStatus
{
    Opened,
    InvalidTarget,
    CommandRejected,
    EffectFailed
}

internal readonly record struct ExternalUrlLaunchResult(
    ExternalUrlLaunchStatus Status,
    string? ErrorMessage = null)
{
    public static ExternalUrlLaunchResult Opened() => new(ExternalUrlLaunchStatus.Opened);

    public static ExternalUrlLaunchResult InvalidTarget() => new(ExternalUrlLaunchStatus.InvalidTarget);

    public static ExternalUrlLaunchResult CommandRejected() => new(ExternalUrlLaunchStatus.CommandRejected);

    public static ExternalUrlLaunchResult EffectFailed(string? errorMessage = null)
        => new(ExternalUrlLaunchStatus.EffectFailed, errorMessage);
}

internal static class ExternalUrlLaunchService
{
    public static ExternalUrlLaunchResult Launch(string? target, Action<Uri> launch)
    {
        if (launch == null) throw new ArgumentNullException(nameof(launch));
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? targetUri)
            || !(targetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || targetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return ExternalUrlLaunchResult.InvalidTarget();
        }

        try
        {
            launch(targetUri);
            return ExternalUrlLaunchResult.Opened();
        }
        catch (Exception ex)
        {
            LogService.Error("External URL launch failed.", ex);
            return ExternalUrlLaunchResult.EffectFailed(ex.Message);
        }
    }
}
