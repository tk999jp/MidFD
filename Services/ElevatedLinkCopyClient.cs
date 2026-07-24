using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using MidFD.FileOperationHelperProtocol;

namespace MidFD.Services;

internal sealed class ElevatedLinkCopyClient
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    public async Task<ElevatedLinkCopyResponse> CopyAsync(
        IReadOnlyList<ElevatedLinkCopyItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new ElevatedLinkCopyResponse { ProtocolVersion = ProtocolVersion };
        }

        string helperPath = Path.Combine(AppContext.BaseDirectory, "MidFD.FileOperationHelper.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("link operation helper is not bundled", helperPath);
        }

        string operationId = Guid.NewGuid().ToString("N");
        string pipeName = $"MidFD.LinkCopy.{operationId}";
        using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Process? helper = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--operation-id");
            startInfo.ArgumentList.Add(operationId);
            startInfo.ArgumentList.Add("--protocol-version");
            startInfo.ArgumentList.Add(ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                helper = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("link operation helper could not start");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new ElevatedLinkCopyCanceledException();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            var request = new ElevatedLinkCopyRequest
            {
                ProtocolVersion = ProtocolVersion,
                OperationId = operationId,
                ParentProcessId = Environment.ProcessId,
                Items = items.ToList()
            };
            await WriteMessageAsync(pipe, request, timeout.Token).ConfigureAwait(false);
            ElevatedLinkCopyResponse response = await ReadMessageAsync<ElevatedLinkCopyResponse>(pipe, timeout.Token).ConfigureAwait(false);
            ValidateResponse(response, items, operationId);

            if (!helper.WaitForExit((int)OperationTimeout.TotalMilliseconds))
            {
                throw new TimeoutException("link operation helper timeout");
            }
            if (helper.ExitCode != 0)
            {
                throw new InvalidOperationException($"link operation helper exited with code {helper.ExitCode}");
            }

            return response;
        }
        finally
        {
            if (helper != null)
            {
                try
                {
                    if (!helper.HasExited)
                    {
                        helper.WaitForExit(1_000);
                        if (!helper.HasExited) helper.Kill(entireProcessTree: true);
                    }
                }
                catch { }
                helper.Dispose();
            }
        }
    }

    private static async Task WriteMessageAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadMessageAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
        int length = BitConverter.ToInt32(lengthBuffer);
        if (length <= 0 || length > 1_048_576) throw new InvalidDataException("invalid helper response length");
        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("empty helper response");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    internal static void ValidateResponse(ElevatedLinkCopyResponse response, IReadOnlyList<ElevatedLinkCopyItem> items, string operationId)
    {
        if (response.ProtocolVersion != ProtocolVersion ||
            !string.Equals(response.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("link operation helper response mismatch");
        }

        var expectedIds = items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        var responseIds = response.Results.Select(result => result.ItemId).ToList();
        if (responseIds.Count != expectedIds.Count ||
            responseIds.Distinct(StringComparer.Ordinal).Count() != responseIds.Count ||
            responseIds.Any(id => !expectedIds.Contains(id)))
        {
            throw new InvalidDataException("link operation helper response items mismatch");
        }

        foreach (ElevatedLinkCopyResult result in response.Results)
        {
            if (result.Status is not ("success" or "failed" or "unsupported"))
                throw new InvalidDataException("link operation helper returned an invalid status");
            ElevatedLinkCopyItem item = items.First(candidate => candidate.ItemId == result.ItemId);
            if (result.Status != "success") continue;
            if (string.IsNullOrWhiteSpace(result.ResultingKind) || string.IsNullOrWhiteSpace(result.LinkTarget))
                throw new InvalidDataException("successful link result is incomplete");
            if (!ReparsePointHelper.IsReparsePoint(item.DestinationPath) ||
                !string.Equals(GetKind(item.DestinationPath), result.ResultingKind, StringComparison.Ordinal) ||
                !string.Equals(ReparsePointHelper.GetLinkTarget(item.DestinationPath), result.LinkTarget, StringComparison.Ordinal))
            {
                throw new InvalidDataException("destination link verification failed in client");
            }
        }
    }

    private static string GetKind(string path)
    {
        return ReparsePointHelper.GetReparseTag(path) switch
        {
            0xA000000C when ReparsePointHelper.IsDirectory(path) => "DirectorySymbolicLink",
            0xA000000C => "FileSymbolicLink",
            0xA0000003 => "Junction",
            _ => "Unsupported"
        };
    }
}

internal sealed class ElevatedLinkCopyCanceledException : OperationCanceledException
{
    public ElevatedLinkCopyCanceledException()
        : base("link operation helper was canceled at the UAC prompt") { }
}
