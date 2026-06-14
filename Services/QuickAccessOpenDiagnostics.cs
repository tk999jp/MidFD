using System.Diagnostics;
using System.IO;
using MidFD.Models;

namespace MidFD.Services;

public sealed class QuickAccessOpenDiagnostics
{
    private const long SuccessCacheTtlMs = 2_000;
    private const long FailureCacheTtlMs = 500;
    private readonly Stopwatch _totalStopwatch = Stopwatch.StartNew();
    private readonly Dictionary<string, (string PathKind, string PathRoot)> _pathInfoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedProbeResult> _probeCache = new(StringComparer.OrdinalIgnoreCase);

    public QuickAccessOpenDiagnostics(string operationId)
    {
        OperationId = operationId;
    }

    public string OperationId { get; }

    public long ElapsedMs => _totalStopwatch.ElapsedMilliseconds;

    public bool LastProbeUsedCache { get; private set; }

    public static string CreateOperationId()
    {
        return Guid.NewGuid().ToString("N")[..8];
    }

    public void LogOpenStart(string? currentPath, QuickAccessStore store)
    {
        (string pathKind, string pathRoot) = GetPathInfo(currentPath);
        LogService.Detail(
            $"[QuickAccess.Open.Start] operationId={OperationId} elapsedMs={ElapsedMs} stepElapsedMs=0 " +
            $"bookmarkCount={store.Bookmarks.Count} aliasCount={store.Aliases.Count} recentCount={store.Recents.Count} " +
            $"historyCount=0 commandCount={store.Commands.Count} pathKind={pathKind} pathRoot={pathRoot} success=start");
    }

    public void LogOpenEnd(string currentTab, int visibleItemCount)
    {
        LogService.Detail(
            $"[QuickAccess.Open.End] operationId={OperationId} elapsedMs={ElapsedMs} stepElapsedMs={ElapsedMs} " +
            $"tab={currentTab} itemCount={visibleItemCount} success=shown");
    }

    public void LogDialogClose(string action, string? selectedPath)
    {
        (string pathKind, string pathRoot) = GetPathInfo(selectedPath);
        LogService.Detail(
            $"[QuickAccess.Dialog.Close] operationId={OperationId} elapsedMs={ElapsedMs} stepElapsedMs=0 " +
            $"action={action} pathKind={pathKind} pathRoot={pathRoot} success=close");
    }

    public T MeasureStep<T>(string stepName, Func<T> action, Func<T, string>? detailFactory = null)
    {
        LogStepStart(stepName);
        var stopwatch = Stopwatch.StartNew();
        T result = action();
        stopwatch.Stop();
        string detail = detailFactory == null ? string.Empty : detailFactory(result);
        LogStepEnd(stepName, stopwatch.ElapsedMilliseconds, detail);
        return result;
    }

    public void MeasureStep(string stepName, Action action, string? detail = null)
    {
        LogStepStart(stepName);
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        LogStepEnd(stepName, stopwatch.ElapsedMilliseconds, detail ?? string.Empty);
    }

    public bool MeasureDirectoryExists(string stepName, string? path, string purpose)
    {
        return MeasureBoolProbe(stepName, path, purpose, static candidate => Directory.Exists(candidate));
    }

    public bool MeasureFileExists(string stepName, string? path, string purpose)
    {
        return MeasureBoolProbe(stepName, path, purpose, static candidate => File.Exists(candidate));
    }

    public string BuildPathDetail(string? path, string purpose, bool success, string? extra = null)
    {
        (string pathKind, string pathRoot) = GetPathInfo(path);
        string detail =
            $"itemCount=1 pathKind={pathKind} pathRoot={pathRoot} purpose={purpose} success={(success ? "success" : "fail")}";
        if (!string.IsNullOrWhiteSpace(extra))
        {
            detail += $" {extra}";
        }

        return detail;
    }

    private bool MeasureBoolProbe(string stepName, string? path, string purpose, Func<string, bool> probe)
    {
        LastProbeUsedCache = false;
        if (string.IsNullOrWhiteSpace(path))
        {
            LogStepEnd(stepName, 0, BuildPathDetail(path, purpose, success: false, extra: "cache=skip skip=empty-path"));
            return false;
        }

        string cacheKey = BuildProbeCacheKey(stepName, purpose, path);
        if (TryGetCachedProbeResult(cacheKey, out CachedProbeResult? cachedResult) && cachedResult != null)
        {
            LastProbeUsedCache = true;
            LogStepEnd(stepName, 0, BuildPathDetail(path, purpose, cachedResult.Value, extra: "cache=hit"));
            return cachedResult.Value;
        }

        var stopwatch = Stopwatch.StartNew();
        bool result = probe(path);
        stopwatch.Stop();
        StoreProbeResult(cacheKey, result);
        LogStepEnd(stepName, stopwatch.ElapsedMilliseconds, BuildPathDetail(path, purpose, result, extra: "cache=miss"));
        return result;
    }

    private void LogStepStart(string stepName)
    {
        LogService.Detail(
            $"[{stepName}.Start] operationId={OperationId} elapsedMs={ElapsedMs} stepElapsedMs=0");
    }

    private void LogStepEnd(string stepName, long stepElapsedMs, string detail)
    {
        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
        LogService.Detail(
            $"[{stepName}.End] operationId={OperationId} elapsedMs={ElapsedMs} stepElapsedMs={stepElapsedMs}{suffix}");
    }

    private (string PathKind, string PathRoot) GetPathInfo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ("Unknown", "<empty>");
        }

        if (_pathInfoCache.TryGetValue(path, out (string PathKind, string PathRoot) cachedInfo))
        {
            return cachedInfo;
        }

        string pathKind = NetworkPathResolutionPolicy.GetPathKind(path);
        string pathRoot = NetworkPathResolutionPolicy.GetPathRoot(path);
        (string PathKind, string PathRoot) pathInfo = (pathKind, pathRoot);
        _pathInfoCache[path] = pathInfo;
        return pathInfo;
    }

    private bool TryGetCachedProbeResult(string cacheKey, out CachedProbeResult? result)
    {
        if (!_probeCache.TryGetValue(cacheKey, out result))
        {
            return false;
        }

        if (result is null)
        {
            return false;
        }

        long ageMs = ElapsedMs - result.CreatedAtMs;
        long ttlMs = result.Value ? SuccessCacheTtlMs : FailureCacheTtlMs;
        if (ageMs <= ttlMs)
        {
            return true;
        }

        _probeCache.Remove(cacheKey);
        return false;
    }

    private void StoreProbeResult(string cacheKey, bool value)
    {
        _probeCache[cacheKey] = new CachedProbeResult(value, ElapsedMs);
    }

    private static string BuildProbeCacheKey(string stepName, string purpose, string path)
    {
        return $"{stepName}\u001F{purpose}\u001F{path}";
    }

    private sealed record CachedProbeResult(bool Value, long CreatedAtMs);
}
