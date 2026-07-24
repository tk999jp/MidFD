using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MidFD.FileOperationHelperProtocol;

namespace MidFD.FileOperationHelper;

internal static class Program
{
    private const int ProtocolVersion = 1;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            HelperArguments parsed = HelperArguments.Parse(args);
            using Process parent = ValidateParentProcess(parsed.ParentProcessId);

            using var pipe = new NamedPipeClientStream(
                ".",
                parsed.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            _ = MonitorParentAsync(parent, lifetime);
            await pipe.ConnectAsync(15_000, lifetime.Token);

            uint serverProcessId = GetNamedPipeServerProcessId(pipe.SafePipeHandle);
            if (serverProcessId != parsed.ParentProcessId || serverProcessId != parent.Id)
            {
                throw new UnauthorizedAccessException("invalid link helper pipe server");
            }

            ElevatedLinkCopyRequest request = await ReadMessageAsync<ElevatedLinkCopyRequest>(pipe, lifetime.Token);
            if (request.ProtocolVersion != parsed.ProtocolVersion ||
                request.ProtocolVersion != ProtocolVersion ||
                request.ParentProcessId != parsed.ParentProcessId ||
                !string.Equals(request.OperationId, parsed.OperationId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("link helper protocol mismatch");
            }

            if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.ItemId)) ||
                request.Items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != request.Items.Count)
            {
                throw new InvalidDataException("duplicate or empty helper item id");
            }

            var response = new ElevatedLinkCopyResponse
            {
                ProtocolVersion = ProtocolVersion,
                OperationId = request.OperationId
            };
            foreach (ElevatedLinkCopyItem item in request.Items)
            {
                response.Results.Add(ProcessItem(item));
            }

            try
            {
                await WriteMessageAsync(pipe, response, lifetime.Token);
            }
            catch
            {
                foreach (ElevatedLinkCopyResult result in response.Results.Where(result => result.Status == "success"))
                {
                    ElevatedLinkCopyItem item = request.Items.First(item => item.ItemId == result.ItemId);
                    TryDeleteCreatedLink(item.DestinationPath, result.ResultingKind, item.SourcePath);
                }
                throw;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static ElevatedLinkCopyResult ProcessItem(ElevatedLinkCopyItem item)
    {
        bool destinationCreated = false;
        try
        {
            ValidateItem(item);
            HelperReparseInfo sourceInfo = HelperReparsePointService.Read(item.SourcePath);
            string expectedKind = sourceInfo.Kind.ToString();
            if (!string.Equals(expectedKind, item.ExpectedKind, StringComparison.Ordinal))
            {
                return Failed(item, 0, "source reparse kind does not match request");
            }
            string target = sourceInfo.RawTarget;

            if (item.ExpectedKind == "FileSymbolicLink")
            {
                CreateFileSymbolicLink(item.DestinationPath, target);
            }
            else if (item.ExpectedKind == "DirectorySymbolicLink")
            {
                CreateDirectorySymbolicLink(item.DestinationPath, target);
            }
            else if (item.ExpectedKind == "Junction" && sourceInfo.RawData is not null)
            {
                destinationCreated = true;
                HelperReparsePointService.CreateJunction(item.DestinationPath, sourceInfo.RawData);
            }
            else
            {
                return Failed(item, 4390, "unsupported reparse data");
            }
            destinationCreated = true;

            HelperReparseInfo destinationInfo = HelperReparsePointService.Read(item.DestinationPath);
            if (!string.Equals(destinationInfo.Kind.ToString(), item.ExpectedKind, StringComparison.Ordinal) ||
                !string.Equals(destinationInfo.RawTarget, target, StringComparison.Ordinal))
            {
                throw new IOException("destination link verification failed");
            }

            return new ElevatedLinkCopyResult
            {
                ItemId = item.ItemId,
                Status = "success",
                ResultingKind = item.ExpectedKind,
                LinkTarget = GetClientComparableTarget(item.ExpectedKind, item.DestinationPath, destinationInfo.RawTarget)
            };
        }
        catch (Win32Exception ex)
        {
            if (destinationCreated) TryDeleteCreatedLink(item.DestinationPath, item.ExpectedKind, item.SourcePath);
            return Failed(item, ex.NativeErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            if (destinationCreated) TryDeleteCreatedLink(item.DestinationPath, item.ExpectedKind, item.SourcePath);
            return Failed(item, 0, ex.Message);
        }
    }

    private static void ValidateItem(ElevatedLinkCopyItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ItemId) ||
            string.IsNullOrWhiteSpace(item.SourcePath) ||
            string.IsNullOrWhiteSpace(item.DestinationPath) ||
            (item.ExpectedKind != "FileSymbolicLink" && item.ExpectedKind != "DirectorySymbolicLink" && item.ExpectedKind != "Junction"))
        {
            throw new InvalidDataException("invalid link helper item");
        }

        string source = Path.GetFullPath(item.SourcePath);
        string destination = Path.GetFullPath(item.DestinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(destination) || Directory.Exists(destination) || ReparsePointExists(destination))
        {
            throw new IOException("source and destination are invalid or destination already exists");
        }

        HelperReparseInfo sourceInfo = HelperReparsePointService.Read(source);
        if (!string.Equals(sourceInfo.Kind.ToString(), item.ExpectedKind, StringComparison.Ordinal))
            throw new IOException("source link type does not match request");

        string destinationParent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("destination parent is missing");
        if (!Directory.Exists(destinationParent))
        {
            throw new DirectoryNotFoundException("destination parent must be created by MidFD");
        }
    }

    private static void CreateFileSymbolicLink(string destination, string target)
    {
        if (CreateSymbolicLink(destination, target, 0) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void CreateDirectorySymbolicLink(string destination, string target)
    {
        if (CreateSymbolicLink(destination, target, 0x1) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static ElevatedLinkCopyResult Failed(ElevatedLinkCopyItem item, int errorCode, string message) => new()
    {
        ItemId = item.ItemId,
        Status = "failed",
        NativeErrorCode = errorCode,
        ErrorMessage = message
    };

    private static string GetClientComparableTarget(string expectedKind, string destinationPath, string fallback)
    {
        if (expectedKind != "Junction") return fallback;
        FileSystemInfo info = new DirectoryInfo(destinationPath);
        return string.IsNullOrEmpty(info.LinkTarget) ? fallback : info.LinkTarget;
    }

    private static Process ValidateParentProcess(int parentProcessId)
    {
        Process parent = Process.GetProcessById(parentProcessId);
        string? path = parent.MainModule?.FileName;
        string expected = Path.Combine(AppContext.BaseDirectory, "MidFD.exe");
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("invalid MidFD parent process");
        }
        return parent;
    }

    private static uint GetNamedPipeServerProcessId(SafePipeHandle pipeHandle)
    {
        if (!GetNamedPipeServerProcessId(pipeHandle, out uint processId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return processId;
    }

    private static async Task<T> ReadMessageAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        int length = BitConverter.ToInt32(lengthBuffer);
        if (length <= 0 || length > 1_048_576) throw new InvalidDataException("invalid helper message length");
        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("empty helper message");
    }

    private static async Task WriteMessageAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static void TryDeleteCreatedLink(string destination, string expectedKind, string source)
    {
        try
        {
            HelperReparseInfo? destinationInfo = TryRead(destination);
            HelperReparseInfo sourceInfo = HelperReparsePointService.Read(source);
            if (destinationInfo is null || destinationInfo.Kind.ToString() != expectedKind ||
                destinationInfo.RawTarget != sourceInfo.RawTarget) return;
            if (destinationInfo.Kind == HelperReparseKind.Junction) Directory.Delete(destination, false);
            else if ((destinationInfo.Kind == HelperReparseKind.DirectorySymbolicLink)) Directory.Delete(destination, false);
            else File.Delete(destination);
        }
        catch { }
    }

    private static HelperReparseInfo? TryRead(string path)
    {
        try { return HelperReparsePointService.Read(path); }
        catch { return null; }
    }

    private static async Task MonitorParentAsync(Process parent, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (parent.HasExited)
                {
                    lifetime.Cancel();
                    return;
                }
                await Task.Delay(250, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { lifetime.Cancel(); }
    }

    private static bool ReparsePointExists(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern byte CreateSymbolicLink(string symbolicLinkFileName, string targetFileName, uint flags);

    private sealed record HelperArguments(string PipeName, int ParentProcessId, string OperationId, int ProtocolVersion)
    {
        public static HelperArguments Parse(string[] args)
        {
            string? pipe = null;
            string? operation = null;
            int parent = 0;
            int protocol = 0;
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--pipe": pipe = args[i + 1]; break;
                    case "--parent-pid": parent = int.Parse(args[i + 1]); break;
                    case "--operation-id": operation = args[i + 1]; break;
                    case "--protocol-version": protocol = int.Parse(args[i + 1]); break;
                    default: throw new InvalidDataException("unknown helper argument");
                }
            }
            if (protocol != Program.ProtocolVersion || string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(operation) || parent <= 0)
                throw new InvalidDataException("missing helper argument");
            return new HelperArguments(pipe, parent, operation, protocol);
        }

    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
