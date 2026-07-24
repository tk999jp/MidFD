namespace MidFD.Services;

public sealed class DirectoryCountAuditSchedule
{
    internal const int ActiveIntervalMilliseconds = 3_000;
    internal const int NetworkActiveIntervalMilliseconds = 10_000;
    internal const int FirstUnchangedIntervalMilliseconds = 10_000;
    internal const int ContinuedUnchangedIntervalMilliseconds = 30_000;
    internal const int QuietIntervalMilliseconds = 60_000;
    private int _unchangedCount;

    public int UnchangedCount => _unchangedCount;

    public int GetIntervalMilliseconds(bool isNetworkPath)
    {
        int interval = _unchangedCount switch
        {
            0 => ActiveIntervalMilliseconds,
            1 => FirstUnchangedIntervalMilliseconds,
            2 => ContinuedUnchangedIntervalMilliseconds,
            _ => QuietIntervalMilliseconds
        };
        return isNetworkPath ? Math.Max(interval, NetworkActiveIntervalMilliseconds) : interval;
    }

    public void RecordResult(bool changed)
    {
        if (changed) _unchangedCount = 0;
        else if (_unchangedCount < 3) _unchangedCount++;
    }

    public void ResetForActivity() => _unchangedCount = 0;
}
