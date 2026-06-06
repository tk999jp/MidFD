using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MidFD.Services;

internal sealed class PreviewDiagnosticDelayService
{
    private const int MaxDelayMilliseconds = 10_000;
    private readonly bool _enabled;
    private readonly bool _onlyUnc;

    public int PreviewDelayMs { get; }
    public int PreviewKindDelayMs { get; }
    public int PreviewOpenDelayMs { get; }
    public int ExternalReloadDelayMs { get; }

    public PreviewDiagnosticDelayService()
    {
        _enabled = ParseEnabled(Environment.GetEnvironmentVariable("MIDFD_DIAGNOSTIC_SLOW_PREVIEW"));
        _onlyUnc = ParseEnabled(Environment.GetEnvironmentVariable("MIDFD_DIAGNOSTIC_ONLY_UNC"));
        PreviewDelayMs = ParseDelay(Environment.GetEnvironmentVariable("MIDFD_DIAGNOSTIC_PREVIEW_DELAY_MS"));
        PreviewKindDelayMs = ParseDelay(Environment.GetEnvironmentVariable("MIDFD_DIAGNOSTIC_PREVIEW_KIND_DELAY_MS"));
        PreviewOpenDelayMs = ParseDelay(Environment.GetEnvironmentVariable("MIDFD_DIAGNOSTIC_PREVIEW_OPEN_DELAY_MS"));
        ExternalReloadDelayMs = ParseDelay(Environment.GetEnvironmentVariable("MIDFD_DIAGNOSTIC_EXTERNAL_RELOAD_DELAY_MS"));
    }

    public bool Enabled => _enabled;

    public bool ShouldDelay(string? path, int delayMs)
    {
        if (!_enabled || delayMs <= 0)
        {
            return false;
        }

        if (!_onlyUnc)
        {
            return true;
        }

        return IsUncPath(path);
    }

    public async Task DelayAsync(string stage, string? path, int delayMs, CancellationToken token)
    {
        if (!ShouldDelay(path, delayMs))
        {
            return;
        }

        LogService.Info($"[PreviewDiagnostic] delay stage='{stage}' delayMs={delayMs} path='{path ?? "<null>"}'");
        await Task.Delay(delayMs, token).ConfigureAwait(false);
    }

    private static bool ParseEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseDelay(string? value)
    {
        if (!int.TryParse(value, out int parsed))
        {
            return 0;
        }

        if (parsed < 0)
        {
            return 0;
        }

        return Math.Min(parsed, MaxDelayMilliseconds);
    }

    private static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Path.IsPathRooted(path) && path.StartsWith(@"\\", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
