using System.ComponentModel;
using System.Diagnostics;

namespace MidFD.Services;

internal sealed class ContentSearchEngineResolver
{
    private readonly IEnumerable<string> _candidates;
    private readonly Func<string, bool> _probe;
    internal ContentSearchEngineResolver(IEnumerable<string>? candidates = null, Func<string, bool>? probe = null)
    {
        _candidates = candidates ?? DiscoverCandidates();
        _probe = probe ?? IsAvailable;
    }
    internal IContentSearchEngine Resolve()
    {
        foreach (string candidate in _candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (_probe(candidate)) return new RipgrepContentSearchEngine(candidate);
        return new InternalContentSearchEngine();
    }
    private static IEnumerable<string> DiscoverCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "rg.exe");
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string clean = directory.Trim().Trim('"');
            yield return Path.Combine(clean, "rg.exe");
            yield return Path.Combine(clean, "rg");
        }
    }
    internal static string? ReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("--no-config");
            process.StartInfo.ArgumentList.Add("--version");
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            return process.ExitCode == 0 && output.Result.StartsWith("ripgrep ", StringComparison.Ordinal)
                ? output.Result.Split('\n')[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { return null; }
    }
    private static bool IsAvailable(string path) => ReadVersion(path) != null;
}
