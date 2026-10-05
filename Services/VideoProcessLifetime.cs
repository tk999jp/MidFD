using System.Diagnostics;

namespace MidFD.Services;

internal enum VideoProcessWaitOutcome
{
    Exited,
    Cancelled,
    TimedOut
}

internal static class VideoProcessLifetime
{
    public static async Task<VideoProcessWaitOutcome> WaitForExitOrStopAsync(
        Process process,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            return VideoProcessWaitOutcome.Exited;
        }
        catch (OperationCanceledException)
        {
            VideoProcessWaitOutcome outcome = cancellationToken.IsCancellationRequested
                ? VideoProcessWaitOutcome.Cancelled
                : VideoProcessWaitOutcome.TimedOut;
            await StopAndWaitAsync(process).ConfigureAwait(false);
            return outcome;
        }
    }

    public static async Task StopAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort; the exit wait below confirms whether termination succeeded.
        }

        try
        {
            if (!process.HasExited) await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process was never started.
        }
    }
}
