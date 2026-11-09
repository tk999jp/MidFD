using System.Diagnostics;

namespace MidFD.Services;

public static class LogService
{
    internal static event Action<string>? TimingRecorded;

    public static void Info(string message)
    {
        Trace.WriteLine($"[INFO] {message}");
        const string prefix = "[MidPlayerTiming]";
        if (message.StartsWith(prefix, StringComparison.Ordinal))
        {
            TimingRecorded?.Invoke(
                $"{prefix} pid={Environment.ProcessId} atUtc={DateTimeOffset.UtcNow:O}{message[prefix.Length..]}");
        }
    }
    public static void Warn(string message) => Trace.WriteLine($"[WARN] {message}");
    public static void Error(string message, Exception? exception = null)
        => Trace.WriteLine($"[ERROR] {message}{(exception == null ? string.Empty : $" ({exception.GetType().Name}: {exception.Message})")}");
}
