using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MidFD.Configuration;
using MidFD.Services;
using Microsoft.Win32.SafeHandles;

namespace MidFD;

internal sealed record SusiePluginRuntimeMetadata(string DisplayName, string VersionInfo, IReadOnlyList<string> Extensions, IReadOnlyList<string> FormatNames);
internal sealed record SusieDecodeAttempt(Bitmap? Image, string Status, string Detail, int? PluginErrorCode = null, bool? OutputHandlesReleased = null);
internal enum ImageDecoderKind { Standard, Susie }
internal sealed record ImageDecodeResult(
    Bitmap? Image,
    string ErrorMessage,
    ImageDecoderKind? Kind = null,
    string? DisplayName = null,
    string? DecoderPath = null,
    bool UsedFallback = false)
{
    internal bool Succeeded => Image != null;
    internal static ImageDecodeResult Failure(string errorMessage, bool usedFallback = false)
        => new(null, errorMessage, UsedFallback: usedFallback);
}

internal sealed class SusieHostStartupException : Exception
{
    internal SusieHostStartupException(string stage, Exception innerException, string? hostFailureLog = null)
        : base(BuildMessage(stage, innerException, hostFailureLog), innerException)
    {
        Stage = stage;
    }

    internal string Stage { get; }

    private static string BuildMessage(string stage, Exception innerException, string? hostFailureLog)
    {
        string message = $"Susie Host {stage} failed: {innerException.GetType().Name}: {innerException.Message}";
        string? hostSummary = GetHostFailureSummary(hostFailureLog);
        if (!string.IsNullOrWhiteSpace(hostSummary)
            && !innerException.Message.Contains(hostSummary, StringComparison.Ordinal))
            message += $" Host detail: {hostSummary}";
        if (!string.IsNullOrWhiteSpace(hostFailureLog)
            && !innerException.Message.Contains(hostFailureLog, StringComparison.Ordinal))
            message += $"{Environment.NewLine}Host metadata failure log: {hostFailureLog}";
        return message;
    }

    private static string? GetHostFailureSummary(string? hostFailureLog)
    {
        string? summary = hostFailureLog?
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        const int maxLength = 120;
        return summary == null || summary.Length <= maxLength ? summary : summary[..maxLength] + "…";
    }
}

internal sealed class SusieDecoderService : IDisposable
{
    private sealed class HostEntry(SusieHostClient host)
    {
        internal SusieHostClient Host { get; } = host;
        internal int LeaseCount { get; set; }
        internal bool IsRetired { get; set; }
        internal bool DisposalStarted { get; set; }
    }

    private sealed class HostLease(SusieDecoderService owner, HostEntry entry) : IDisposable
    {
        private SusieDecoderService? _owner = owner;
        internal SusieHostClient Host { get; } = entry.Host;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseHost(entry);
    }

    private readonly record struct SettingsSnapshot(SusiePluginSettings Value, long Generation);

    private SusiePluginSettings _settings;
    private long _settingsGeneration;
    private readonly Func<string, string, CancellationToken, Task<SusieHostClient>> _startHostAsync;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostEntry> _hostsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hostFailuresByPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal SusieDecoderService(
        SusiePluginSettings settings,
        Func<string, string, CancellationToken, Task<SusieHostClient>>? startHostAsync = null)
    {
        _settings = (settings ?? new SusiePluginSettings()).Clone();
        _settings.Normalize();
        _startHostAsync = startHostAsync ?? SusieHostClient.StartAsync;
    }

    internal void UpdateSettings(SusiePluginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SusiePluginSettings replacement = settings.Clone();
        replacement.Normalize();
        var dispose = new List<SusieHostClient>();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long nextGeneration = checked(_settingsGeneration + 1);
            _settings = replacement;
            _settingsGeneration = nextGeneration;

            HashSet<string> enabledPluginPaths = replacement.Plugins
                .Where(plugin => plugin.IsEnabled)
                .Select(plugin => TryGetFullPath(plugin.PluginPath))
                .Where(path => path != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach ((string path, HostEntry entry) in _hostsByPath.ToArray())
            {
                if (enabledPluginPaths.Contains(path)) continue;
                _hostsByPath.Remove(path);
                SusieHostClient? retired = RetireHostLocked(entry);
                if (retired != null) dispose.Add(retired);
            }
        }

        DisposeHosts(dispose);
    }

    private SettingsSnapshot CaptureSettings()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new SettingsSnapshot(_settings, _settingsGeneration);
        }
    }

    internal async Task<ImageDecodeResult> DecodeAsync(string imagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SettingsSnapshot snapshot = CaptureSettings();
        SusiePluginSettings settings = snapshot.Value;
        string extension = Path.GetExtension(imagePath);
        bool standardEnabled = !settings.DisabledStandardExtensions.Contains(extension);
        settings.DecoderByExtension.TryGetValue(extension, out string? selectedDecoder);

        if (string.Equals(selectedDecoder, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return standardEnabled
                ? await DecodeStandardAsync(imagePath, cancellationToken).ConfigureAwait(false)
                : ImageDecodeResult.Failure($"{extension} の標準decoderは設定で無効です。");
        }

        SusiePluginRegistration[] registrations = settings.Plugins.Where(plugin => plugin.IsEnabled).ToArray();

        if (!string.IsNullOrWhiteSpace(selectedDecoder))
        {
            SusiePluginRegistration? selected;
            try
            {
                selected = registrations.FirstOrDefault(plugin => string.Equals(
                    Path.GetFullPath(plugin.PluginPath), Path.GetFullPath(selectedDecoder), StringComparison.OrdinalIgnoreCase));
            }
            catch { selected = null; }
            if (selected == null)
                return await DecodeStandardFallbackAsync(imagePath, standardEnabled, $"{extension} の選択Pluginが登録されていません。", cancellationToken).ConfigureAwait(false);

            string[]? inspectedExtensions = selected.InspectionMetadata?.Extensions;
            if (inspectedExtensions is { Length: > 0 }
                && !inspectedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                Services.LogService.Info($"[MidPlayerTiming] stage=susie-host-preparation extension={extension} selectedMetadataSkip=true");
                return await DecodeStandardFallbackAsync(imagePath, standardEnabled, $"選択Pluginは {extension} に対応していません。", cancellationToken).ConfigureAwait(false);
            }

            long selectedHostPreparationStarted = Stopwatch.GetTimestamp();
            using HostLease? selectedLease = await GetHostLeaseAsync(selected, snapshot.Generation, cancellationToken).ConfigureAwait(false);
            SusieHostClient? selectedHost = selectedLease?.Host;
            Services.LogService.Info(
                $"[MidPlayerTiming] stage=susie-host-preparation extension={extension} selected=True " +
                $"candidate=1 success={selectedHost != null} " +
                $"elapsedMs={Stopwatch.GetElapsedTime(selectedHostPreparationStarted).TotalMilliseconds:0.0}");
            if (selectedHost == null)
                return await DecodeStandardFallbackAsync(imagePath, standardEnabled, $"{extension} の選択Plugin Hostを起動できませんでした。", cancellationToken).ConfigureAwait(false);
            if (!selectedHost.Metadata.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return await DecodeStandardFallbackAsync(imagePath, standardEnabled, $"選択Pluginは {extension} に対応していません。", cancellationToken).ConfigureAwait(false);
            return await DecodeWithFallbackAsync(selectedHost, imagePath, standardEnabled, cancellationToken).ConfigureAwait(false);
        }

        if (registrations.Length == 0)
        {
            return standardEnabled
                ? await DecodeStandardAsync(imagePath, cancellationToken).ConfigureAwait(false)
                : ImageDecodeResult.Failure($"{extension} のdecoderは登録されていません。");
        }

        SusiePluginRegistration[] pluginCandidates = SelectCandidatesForExtension(registrations, extension);
        long hostPreparationStarted = Stopwatch.GetTimestamp();
        HostLease?[] leases = await GetHostLeasesAsync(pluginCandidates, snapshot.Generation, cancellationToken).ConfigureAwait(false);
        try
        {
            HostLease[] matches = leases
                .Where(lease => lease != null && lease.Host.Metadata.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                .Cast<HostLease>()
                .ToArray();
            Services.LogService.Info(
                $"[MidPlayerTiming] stage=susie-host-preparation extension={extension} registered={registrations.Length} " +
                $"candidate={pluginCandidates.Length} metadataSkipped={registrations.Length - pluginCandidates.Length} matches={matches.Length} " +
                $"elapsedMs={Stopwatch.GetElapsedTime(hostPreparationStarted).TotalMilliseconds:0.0}");
            if (matches.Length == 0)
            {
                return standardEnabled
                    ? await DecodeStandardAsync(imagePath, cancellationToken).ConfigureAwait(false)
                    : ImageDecodeResult.Failure($"{extension} に対応する登録Pluginがありません。");
            }
            if (matches.Length > 1)
            {
                string candidates = string.Join(Environment.NewLine, matches.Select(lease => lease.Host.PluginPath));
                return ImageDecodeResult.Failure($"{extension} に対応するPluginが複数あります。設定でdecoderを選んでください。{Environment.NewLine}{candidates}");
            }
            return await DecodeWithFallbackAsync(matches[0].Host, imagePath, standardEnabled, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (HostLease? lease in leases) lease?.Dispose();
        }
    }

    internal static SusiePluginRegistration[] SelectCandidatesForExtension(
        IEnumerable<SusiePluginRegistration> registrations,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        return registrations.Where(registration =>
        {
            string[]? knownExtensions = registration.InspectionMetadata?.Extensions;
            return knownExtensions is not { Length: > 0 }
                || knownExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }).ToArray();
    }

    internal async Task<SusiePluginRuntimeMetadata?> InspectPluginAsync(string pluginPath, CancellationToken cancellationToken)
    {
        SusiePluginRegistration registration = new() { PluginPath = pluginPath, IsEnabled = true };
        SettingsSnapshot snapshot = CaptureSettings();
        using HostLease? lease = await GetHostLeaseAsync(registration, snapshot.Generation, cancellationToken).ConfigureAwait(false);
        return lease?.Host.Metadata;
    }

    internal IReadOnlyDictionary<string, int> SnapshotHostProcessIds()
    {
        lock (_gate)
        {
            return _hostsByPath.ToDictionary(pair => pair.Key, pair => pair.Value.Host.ProcessId, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal string? GetHostFailure(string pluginPath)
    {
        try
        {
            lock (_gate) return _hostFailuresByPath.GetValueOrDefault(Path.GetFullPath(pluginPath));
        }
        catch { return null; }
    }

    private async Task<ImageDecodeResult> DecodeWithFallbackAsync(
        SusieHostClient host,
        string imagePath,
        bool standardEnabled,
        CancellationToken cancellationToken)
    {
        SusieDecodeAttempt attempt = await host.DecodeAsync(imagePath, cancellationToken).ConfigureAwait(false);
        if (attempt.Image != null)
        {
            return new ImageDecodeResult(attempt.Image, string.Empty, ImageDecoderKind.Susie,
                host.Metadata.DisplayName, host.PluginPath);
        }
        Services.LogService.Warn($"[Susie Host] {Path.GetFileName(imagePath)}: {attempt.Status}: {attempt.Detail}");
        return await DecodeStandardFallbackAsync(imagePath, standardEnabled,
            $"Susie Pluginで読み込めませんでした ({attempt.Status}): {attempt.Detail}", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ImageDecodeResult> DecodeStandardFallbackAsync(
        string imagePath, bool standardEnabled, string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!standardEnabled) return ImageDecodeResult.Failure(reason + " 標準decoderも設定で無効です。");
        ImageDecodeResult standard = await DecodeStandardAsync(imagePath, cancellationToken).ConfigureAwait(false);
        return standard.Succeeded
            ? standard with { ErrorMessage = "標準decoderへ切り替えて読み込みました。" + Environment.NewLine + reason, UsedFallback = true }
            : ImageDecodeResult.Failure(reason + " 標準decoder: " + standard.ErrorMessage, usedFallback: true);
    }

    private static Task<ImageDecodeResult> DecodeStandardAsync(string imagePath, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var (image, errorMessage) = ImagePreviewService.GetPreviewImage(imagePath);
            return image == null
                ? ImageDecodeResult.Failure(errorMessage)
                : new ImageDecodeResult(image, errorMessage, ImageDecoderKind.Standard, "標準decoder");
        }, cancellationToken);

    private async Task<HostLease?[]> GetHostLeasesAsync(
        IReadOnlyList<SusiePluginRegistration> registrations,
        long settingsGeneration,
        CancellationToken cancellationToken)
    {
        Task<HostLease?>[] tasks = registrations
            .Select(registration => GetHostLeaseAsync(registration, settingsGeneration, cancellationToken))
            .ToArray();
        try
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            foreach (Task<HostLease?> task in tasks)
                if (task.IsCompletedSuccessfully) task.Result?.Dispose();
            throw;
        }
    }

    private async Task<HostLease?> GetHostLeaseAsync(
        SusiePluginRegistration registration,
        long settingsGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath;
        try { fullPath = Path.GetFullPath(registration.PluginPath); }
        catch (Exception ex)
        {
            Services.LogService.Warn($"[Susie Host] Plugin path resolution failed path='{registration.PluginPath}' error='{ex.GetType().Name}: {ex.Message}'");
            return null;
        }
        string identity;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            identity = fullPath + "|" + Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (FileNotFoundException ex)
        {
            RecordHostFailure(fullPath, "Plugin file validation", new FileNotFoundException("Registered Susie Plugin file was not found.", fullPath, ex));
            return null;
        }
        catch (DirectoryNotFoundException ex)
        {
            RecordHostFailure(fullPath, "Plugin path validation", new DirectoryNotFoundException($"Registered Susie Plugin directory was not found: {fullPath}", ex));
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            RecordHostFailure(fullPath, "Plugin file access", ex);
            return null;
        }
        catch (Exception ex)
        {
            RecordHostFailure(fullPath, "Plugin file identity read", ex);
            return null;
        }

        SusieHostClient? previousToDispose = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hostsByPath.TryGetValue(fullPath, out HostEntry? current))
            {
                if (string.Equals(current.Host.Identity, identity, StringComparison.Ordinal) && current.Host.IsUsable)
                {
                    current.LeaseCount++;
                    return new HostLease(this, current);
                }
                _hostsByPath.Remove(fullPath);
                previousToDispose = RetireHostLocked(current);
            }
        }
        previousToDispose?.Dispose();

        SusieHostClient? created = null;
        try
        {
            created = await _startHostAsync(fullPath, identity, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            created?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            created?.Dispose();
            string stage = ex is SusieHostStartupException startupFailure ? startupFailure.Stage : "Host process start";
            RecordHostFailure(fullPath, stage, ex);
            return null;
        }

        if (created == null) return null;

        HostLease? result = null;
        SusieHostClient? createdToDispose = null;
        var retiredToDispose = new List<SusieHostClient>();
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                created.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (_disposed)
            {
                createdToDispose = created;
            }
            else if (_hostsByPath.TryGetValue(fullPath, out HostEntry? race)
                && string.Equals(race.Host.Identity, identity, StringComparison.Ordinal)
                && race.Host.IsUsable)
            {
                race.LeaseCount++;
                result = new HostLease(this, race);
                if (!ReferenceEquals(created, race.Host)) createdToDispose = created;
            }
            else
            {
                if (_hostsByPath.TryGetValue(fullPath, out HostEntry? stale))
                {
                    _hostsByPath.Remove(fullPath);
                    SusieHostClient? retired = RetireHostLocked(stale);
                    if (retired != null) retiredToDispose.Add(retired);
                }

                var entry = new HostEntry(created) { LeaseCount = 1 };
                if (settingsGeneration == _settingsGeneration) _hostsByPath[fullPath] = entry;
                else entry.IsRetired = true;
                result = new HostLease(this, entry);
                _hostFailuresByPath.Remove(fullPath);
            }
        }
        if (createdToDispose != null && !retiredToDispose.Contains(createdToDispose)) createdToDispose.Dispose();
        DisposeHosts(retiredToDispose);
        return result;
    }

    private void RecordHostFailure(string fullPath, string stage, Exception error)
    {
        string detail = $"{stage}: {error.GetType().Name}: {error.Message}{Environment.NewLine}{error}";
        lock (_gate) _hostFailuresByPath[fullPath] = detail;
        Services.LogService.Warn($"[Susie Host] stage='{stage}' path='{fullPath}' error='{error.GetType().Name}: {error.Message}'");
    }

    public void Dispose()
    {
        var dispose = new List<SusieHostClient>();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (HostEntry entry in _hostsByPath.Values)
            {
                SusieHostClient? retired = RetireHostLocked(entry);
                if (retired != null) dispose.Add(retired);
            }
            _hostsByPath.Clear();
        }
        DisposeHosts(dispose);
    }

    private void ReleaseHost(HostEntry entry)
    {
        SusieHostClient? dispose = null;
        lock (_gate)
        {
            if (entry.LeaseCount > 0) entry.LeaseCount--;
            if (entry.IsRetired && entry.LeaseCount == 0 && !entry.DisposalStarted)
            {
                entry.DisposalStarted = true;
                dispose = entry.Host;
            }
        }
        dispose?.Dispose();
    }

    private static SusieHostClient? RetireHostLocked(HostEntry entry)
    {
        entry.IsRetired = true;
        if (entry.LeaseCount != 0 || entry.DisposalStarted) return null;
        entry.DisposalStarted = true;
        return entry.Host;
    }

    private static void DisposeHosts(IEnumerable<SusieHostClient> hosts)
    {
        foreach (SusieHostClient host in hosts)
        {
            try { host.Dispose(); }
            catch (Exception error) { Services.LogService.Warn($"[Susie Host] retiring Host failed: {error.GetType().Name}: {error.Message}"); }
        }
    }

    private static string? TryGetFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }
}

internal sealed class SusieHostClient : IDisposable
{
    private const int ProtocolVersion = 1;
    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SusieHostSandbox _sandbox;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _writeGate = new();
    private int _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private SusieHostClient(string identity, NamedPipeServerStream pipe, StreamReader reader, StreamWriter writer, SusieHostSandbox sandbox, SusiePluginRuntimeMetadata metadata)
    {
        Identity = identity;
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
        _sandbox = sandbox;
        Metadata = metadata;
    }

    internal string Identity { get; }
    internal SusiePluginRuntimeMetadata Metadata { get; }
    internal string PluginPath => Identity[..Identity.LastIndexOf('|')];
    internal int ProcessId => _sandbox.ProcessId;
    internal bool IsUsable => Volatile.Read(ref _disposed) == 0 && !_sandbox.HasExited;

    internal static async Task<SusieHostClient> StartAsync(string pluginPath, string identity, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(pluginPath);
        bool is32Bit = extension.Equals(".spi", StringComparison.OrdinalIgnoreCase);
        string hostName = is32Bit ? "SusieHost32" : "SusieHost64";
        string hostDirectory = Path.Combine(AppContext.BaseDirectory, hostName);
        string executableName = hostName + ".exe";
        string pipeName = "MidFD-Susie-" + Guid.NewGuid().ToString("N");
        string stage = "Plugin architecture validation";
        SusieHostSandbox.AppContainerProfile? profile = null;
        NamedPipeServerStream? pipe = null;
        SusieHostSandbox? sandbox = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            ushort expectedMachine = ReadPluginMachine(pluginPath);
            if ((is32Bit && expectedMachine != 0x014c) || (!is32Bit && expectedMachine != 0x8664))
                throw new InvalidDataException($"Registered Plugin PE machine 0x{expectedMachine:X4} does not match {extension}.");

            stage = "AppContainer setup";
            profile = SusieHostSandbox.CreateProfile(identity);
            var pipeSecurity = new PipeSecurity();
            pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(profile.SidText), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            pipe = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                pipeSecurity,
                HandleInheritability.None,
                (PipeAccessRights)0);

            stage = "Host executable resolution and process start";
            sandbox = SusieHostSandbox.Start(hostDirectory, executableName, pluginPath, profile, pipeName, Environment.ProcessId);
            profile = null; // The running sandbox now owns the profile lifetime.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            stage = "Named Pipe connection";
            await AwaitStartupOperationAsync(
                pipe.WaitForConnectionAsync(timeout.Token),
                cancellationToken,
                ex => CreateStartupTimeoutException(sandbox, "did not connect to its named pipe within 15 seconds", ex)).ConfigureAwait(false);
            stage = "Host process identity verification";
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) || clientPid != sandbox.ProcessId)
                throw new UnauthorizedAccessException("Named Pipe client PID does not match the isolated Host process.");

            reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            stage = "Host protocol handshake";
            SusieHostMessage? ready = await AwaitStartupOperationAsync(
                ReadMessageAsync(reader, timeout.Token),
                cancellationToken,
                ex => CreateStartupTimeoutException(sandbox, "did not complete its startup handshake within 15 seconds", ex)).ConfigureAwait(false);
            if (ready == null || ready.Type != "ready" || ready.ProtocolVersion != ProtocolVersion)
                throw new IOException("Susie Host did not complete the versioned handshake.");
            if (ready.ProcessId != sandbox.ProcessId || ready.Is64BitProcess != !is32Bit)
                throw new IOException("Susie Host process architecture or identity did not match the registered Plugin.");

            stage = "Plugin metadata validation";
            var metadata = new SusiePluginRuntimeMetadata(
                ready.DisplayName ?? Path.GetFileName(pluginPath),
                ready.VersionInfo ?? string.Empty,
                ready.Extensions.Select(NormalizeExtension).Where(value => value.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ready.FormatNames);
            return new SusieHostClient(identity, pipe, reader, writer, sandbox, metadata);
        }
        catch (Exception ex)
        {
            string? hostFailureLog = sandbox?.ReadHostFailureLog();
            reader?.Dispose();
            writer?.Dispose();
            sandbox?.Dispose();
            pipe?.Dispose();
            profile?.Dispose();
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new SusieHostStartupException(stage, ex, hostFailureLog);
        }
    }

    internal static async Task AwaitStartupOperationAsync(
        Task operation,
        CancellationToken callerCancellationToken,
        Func<OperationCanceledException, Exception> createTimeoutException)
    {
        try { await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) { throw createTimeoutException(ex); }
    }

    internal static async Task<T> AwaitStartupOperationAsync<T>(
        Task<T> operation,
        CancellationToken callerCancellationToken,
        Func<OperationCanceledException, Exception> createTimeoutException)
    {
        try { return await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) { throw createTimeoutException(ex); }
    }

    private static TimeoutException CreateStartupTimeoutException(
        SusieHostSandbox sandbox,
        string description,
        OperationCanceledException innerException)
    {
        string? hostLog = sandbox.ReadHostFailureLog();
        string logSummary = string.IsNullOrWhiteSpace(hostLog) ? string.Empty : $" Host failure log: {hostLog}";
        return new TimeoutException($"Susie Host process {sandbox.ProcessId} {description}; it was {sandbox.DescribeProcessState()}.{logSummary}", innerException);
    }

    internal async Task<SusieDecodeAttempt> DecodeAsync(string imagePath, CancellationToken cancellationToken)
    {
        bool requestGateAcquired = false;
        bool protocolMayBeOutOfSync = false;
        try
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            requestGateAcquired = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            string extension = Path.GetExtension(imagePath);
            var file = new FileInfo(imagePath);
            if (!file.Exists) return new SusieDecodeAttempt(null, "InputFileFailed", "Input file does not exist.");
            if (file.Length <= 0 || file.Length > 1024L * 1024 * 1024)
                return new SusieDecodeAttempt(null, "InputFileFailed", "Input file is outside the Host transfer size limit.");

            byte[] input = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            using SusieSharedMemoryBuffer inputBuffer = SusieSharedMemoryBuffer.Create(input.Length);
            inputBuffer.Write(input);
            string requestId = Guid.NewGuid().ToString("N");
            long remoteInput = _sandbox.DuplicateHandleToHost(inputBuffer.Handle, FileMapRead);
            cancellationToken.ThrowIfCancellationRequested();
            protocolMayBeOutOfSync = true;
            await SendMessageAsync(new SusieHostMessage
            {
                Type = "decode",
                ProtocolVersion = ProtocolVersion,
                RequestId = requestId,
                InputHandle = remoteInput,
                InputLength = input.Length,
                PathHint = "input" + extension
            }, cancellationToken).ConfigureAwait(false);

            SusieHostMessage? decoded = await ReadWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
            if (decoded == null || decoded.Type != "decode-result" || decoded.RequestId != requestId)
                return FailHost("ProtocolFailure", "Susie Host returned a mismatched decode result.");

            if (!decoded.Success)
            {
                string fallbackPath = Path.Combine(_sandbox.RootDirectory, "input", requestId + (extension.Length > 0 ? extension : ".bin"));
                try
                {
                    await File.WriteAllBytesAsync(fallbackPath, input, cancellationToken).ConfigureAwait(false);
                    await SendMessageAsync(new SusieHostMessage
                    {
                        Type = "decode-file-fallback",
                        ProtocolVersion = ProtocolVersion,
                        RequestId = requestId,
                        FallbackPath = fallbackPath
                    }, cancellationToken).ConfigureAwait(false);
                    SusieHostMessage? fileResult = await ReadWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
                    if (fileResult?.Type == "decode-result" && fileResult.RequestId == requestId && fileResult.Success)
                        decoded = fileResult;
                    else if (fileResult?.Type == "decode-result" && fileResult.RequestId == requestId)
                        decoded = fileResult;
                    else
                        return FailHost("ProtocolFailure", "Susie Host returned a mismatched file fallback result.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new SusieDecodeAttempt(null, decoded.Status ?? "Unsupported", decoded.Detail ?? ex.Message, decoded.PluginErrorCode, decoded.OutputHandlesReleased);
                }
            }

            if (!decoded.Success)
                return new SusieDecodeAttempt(null, decoded.Status ?? "Unsupported", decoded.Detail ?? string.Empty, decoded.PluginErrorCode, decoded.OutputHandlesReleased);

            if (decoded.Width is not > 0 || decoded.Height is not > 0) return FailHost("InvalidDib", "Host returned invalid image dimensions.");
            long expectedLength;
            try { expectedLength = checked((long)decoded.Width.Value * decoded.Height.Value * 4); }
            catch (OverflowException) { return FailHost("InvalidDib", "Host image dimensions overflowed."); }
            if (decoded.OutputLength != expectedLength || expectedLength > 1024L * 1024 * 1024 || expectedLength > int.MaxValue)
                return FailHost("InvalidDib", "Host output length does not match its dimensions or exceeds the limit.");

            using SusieSharedMemoryBuffer outputBuffer = SusieSharedMemoryBuffer.Create(checked((int)expectedLength));
            long remoteOutput = _sandbox.DuplicateHandleToHost(outputBuffer.Handle, FileMapWrite);
            await SendMessageAsync(new SusieHostMessage
            {
                Type = "copy-output",
                ProtocolVersion = ProtocolVersion,
                RequestId = requestId,
                OutputHandle = remoteOutput,
                OutputLength = expectedLength
            }, cancellationToken).ConfigureAwait(false);
            SusieHostMessage? copied = await ReadWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
            if (copied == null || copied.Type != "copy-result" || copied.RequestId != requestId || !copied.Success)
                return FailHost(copied?.Status ?? "ProtocolFailure", copied?.Detail ?? "Host did not complete the MMF transfer.");

            byte[] pixels = outputBuffer.Read();
            Bitmap image = CreateBitmap(decoded.Width.Value, decoded.Height.Value, pixels);
            return new SusieDecodeAttempt(image, decoded.Status ?? "Decoded", decoded.Detail ?? string.Empty,
                decoded.PluginErrorCode, decoded.OutputHandlesReleased);
        }
        catch (OperationCanceledException)
        {
            if (protocolMayBeOutOfSync) Dispose();
            throw;
        }
        catch (TimeoutException ex)
        {
            return FailHost("Timeout", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or ObjectDisposedException)
        {
            return FailHost("HostFailure", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (requestGateAcquired) _requestGate.Release();
        }
    }

    private async Task<SusieHostMessage?> ReadWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await ReadMessageAsync(_reader, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Dispose();
            throw new TimeoutException("Susie Plugin did not return before the Host operation timeout.");
        }
    }

    private async Task SendMessageAsync(SusieHostMessage message, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(message, JsonOptions);
        lock (_writeGate)
        {
            _writer.WriteLine(json);
            _writer.Flush();
        }
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private SusieDecodeAttempt FailHost(string status, string detail)
    {
        Dispose();
        return new SusieDecodeAttempt(null, status, detail);
    }

    private static Bitmap CreateBitmap(int width, int height, byte[] pixels)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowLength = checked(width * 4);
            for (int y = 0; y < height; y++)
            {
                IntPtr targetRow = IntPtr.Add(data.Scan0, checked(y * data.Stride));
                Marshal.Copy(pixels, checked(y * rowLength), targetRow, rowLength);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return new Bitmap(bitmap);
    }

    private static string NormalizeExtension(string extension)
    {
        string value = extension.Trim();
        if (value.Length == 0) return string.Empty;
        return value[0] == '.' ? value : "." + value;
    }

    private static ushort ReadPluginMachine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64) throw new InvalidDataException("Registered Plugin file is shorter than a PE header.");
        if (reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Registered Plugin has no MZ header.");
        stream.Position = 0x3c;
        int peOffset = reader.ReadInt32();
        if (peOffset < 64 || peOffset > stream.Length - 6) throw new InvalidDataException("Registered Plugin PE header offset is invalid.");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Registered Plugin has no PE signature.");
        return reader.ReadUInt16();
    }

    private static async Task<SusieHostMessage?> ReadMessageAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line == null ? null : JsonSerializer.Deserialize<SusieHostMessage>(line, JsonOptions);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_pipe.IsConnected)
            {
                string shutdown = JsonSerializer.Serialize(new SusieHostMessage { Type = "shutdown", ProtocolVersion = ProtocolVersion }, JsonOptions);
                _writer.WriteLine(shutdown);
            }
        }
        catch { }
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        _sandbox.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
