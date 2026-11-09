using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MidFD;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 6 && args[0] == "--serve")
        {
            return RunServer(args[1], args[2], args[3], args[4], args[5]);
        }

        if (args.Length != 4 || args[0] != "--decode-verify")
        {
            Console.Error.WriteLine("Usage: SusieHost32|SusieHost64.exe --decode-verify <plugin-path> <image-path> <output-png>");
            Console.Error.WriteLine("       SusieHost32|SusieHost64.exe --serve <pipe> <plugin-path> <sandbox-root> <parent-pid> <protocol-version>");
            return 2;
        }

        try
        {
            SusieSpiDecodeResult result = SusieSpiDecoder.Decode(args[1], args[2]);
            if (result.Image == null)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    success = false,
                    status = result.Attempt.Status.ToString(),
                    result.Attempt.PluginErrorCode,
                    result.Attempt.UsedUnicodePath,
                    result.Attempt.OutputHandlesReleased,
                    result.Attempt.Detail,
                    is64BitProcess = Environment.Is64BitProcess,
                    processId = Environment.ProcessId
                }));
                return 1;
            }

            using (result.Image)
            {
                result.Image.Save(args[3], ImageFormat.Png);
                Console.Out.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true,
                    status = result.Attempt.Status.ToString(),
                    result.Attempt.PluginErrorCode,
                    result.Attempt.UsedUnicodePath,
                    result.Attempt.OutputHandlesReleased,
                    result.Attempt.Detail,
                    width = result.Image.Width,
                    height = result.Image.Height,
                    topLeftArgb = result.Image.GetPixel(0, 0).ToArgb(),
                    outputPath = Path.GetFullPath(args[3]),
                    is64BitProcess = Environment.Is64BitProcess,
                    processId = Environment.ProcessId
                }));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                status = "HostFailure",
                error = $"{ex.GetType().Name}: {ex.Message}",
                is64BitProcess = Environment.Is64BitProcess,
                processId = Environment.ProcessId
            }));
            return 3;
        }
    }

    private static int RunServer(string pipeName, string pluginPath, string sandboxRoot, string parentPidText, string protocolVersionText)
    {
        if (!int.TryParse(parentPidText, out int expectedParentPid)
            || !int.TryParse(protocolVersionText, out int protocolVersion)
            || protocolVersion != 1)
        {
            return 2;
        }

        string safeRoot = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string safePlugin = Path.GetFullPath(pluginPath);
        if (!safePlugin.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(safePlugin)) return 3;

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10000);
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) && serverPid != expectedParentPid) return 4;
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            SusieSpiPluginMetadata metadata = SusieSpiDecoder.Inspect(safePlugin);
            WriteMessage(writer, new SusieHostMessage
            {
                Type = "ready",
                DisplayName = metadata.DisplayName,
                VersionInfo = metadata.VersionInfo,
                Extensions = metadata.Extensions.ToArray(),
                FormatNames = metadata.FormatNames.ToArray(),
                Is64BitProcess = Environment.Is64BitProcess,
                ProcessId = Environment.ProcessId
            });

            Bitmap? pendingBitmap = null;
            string? pendingRequestId = null;
            while (true)
            {
                string? line = reader.ReadLine();
                if (line == null) break;
                SusieHostMessage? request;
                try { request = JsonSerializer.Deserialize<SusieHostMessage>(line, JsonOptions); }
                catch (JsonException) { continue; }
                if (request == null || request.ProtocolVersion != protocolVersion) continue;

                if (request.Type == "shutdown") break;
                if ((request.Type == "decode" || request.Type == "decode-file-fallback")
                    && !string.IsNullOrWhiteSpace(request.RequestId))
                {
                    pendingBitmap?.Dispose();
                    pendingBitmap = null;
                    pendingRequestId = null;
                    try
                    {
                        SusieSpiDecodeResult result;
                        if (request.Type == "decode")
                        {
                            byte[] input = ReadRemoteBuffer(request.InputHandle, request.InputLength);
                            result = SusieSpiDecoder.DecodeMemory(safePlugin, input, request.PathHint ?? "input.bin");
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(request.FallbackPath) || !IsContainedFile(safeRoot, request.FallbackPath))
                                throw new InvalidDataException("The file fallback path is outside the host sandbox.");
                            result = SusieSpiDecoder.Decode(safePlugin, request.FallbackPath);
                        }
                        if (result.Image == null && !string.IsNullOrWhiteSpace(request.FallbackPath)
                            && IsContainedFile(safeRoot, request.FallbackPath))
                        {
                            result = SusieSpiDecoder.Decode(safePlugin, request.FallbackPath);
                        }

                        if (result.Image == null)
                        {
                            WriteMessage(writer, new SusieHostMessage
                            {
                                Type = "decode-result",
                                RequestId = request.RequestId,
                                Status = result.Attempt.Status.ToString(),
                                PluginErrorCode = result.Attempt.PluginErrorCode,
                                OutputHandlesReleased = result.Attempt.OutputHandlesReleased,
                                Detail = result.Attempt.Detail
                            });
                            continue;
                        }

                        pendingBitmap = result.Image;
                        pendingRequestId = request.RequestId;
                        long outputLength = checked((long)pendingBitmap.Width * pendingBitmap.Height * 4);
                        if (outputLength <= 0 || outputLength > 1024L * 1024 * 1024)
                        {
                            pendingBitmap.Dispose();
                            pendingBitmap = null;
                            pendingRequestId = null;
                            WriteMessage(writer, new SusieHostMessage
                            {
                                Type = "decode-result",
                                RequestId = request.RequestId,
                                Status = "InvalidDib",
                                Detail = "Decoded bitmap dimensions exceed the output limit."
                            });
                            continue;
                        }
                        WriteMessage(writer, new SusieHostMessage
                        {
                            Type = "decode-result",
                            RequestId = request.RequestId,
                            Success = true,
                            Status = result.Attempt.Status.ToString(),
                            PluginErrorCode = result.Attempt.PluginErrorCode,
                            OutputHandlesReleased = result.Attempt.OutputHandlesReleased,
                            Width = pendingBitmap.Width,
                            Height = pendingBitmap.Height,
                            OutputLength = outputLength,
                            Detail = result.Attempt.Detail
                        });
                    }
                    catch (Exception ex)
                    {
                        WriteMessage(writer, new SusieHostMessage
                        {
                            Type = "decode-result",
                            RequestId = request.RequestId,
                            Status = "HostFailure",
                            Detail = $"{ex.GetType().Name}: {ex.Message}"
                        });
                    }
                    continue;
                }

                if (request.Type == "copy-output" && pendingBitmap != null
                    && string.Equals(pendingRequestId, request.RequestId, StringComparison.Ordinal))
                {
                    try
                    {
                        WriteBitmapToRemoteBuffer(pendingBitmap, request.OutputHandle, request.OutputLength ?? 0);
                        WriteMessage(writer, new SusieHostMessage
                        {
                            Type = "copy-result",
                            RequestId = request.RequestId,
                            Success = true,
                            Status = "Decoded",
                            OutputLength = request.OutputLength
                        });
                    }
                    catch (Exception ex)
                    {
                        WriteMessage(writer, new SusieHostMessage
                        {
                            Type = "copy-result",
                            RequestId = request.RequestId,
                            Status = "HostFailure",
                            Detail = $"{ex.GetType().Name}: {ex.Message}"
                        });
                    }
                    finally
                    {
                        pendingBitmap.Dispose();
                        pendingBitmap = null;
                        pendingRequestId = null;
                    }
                }
            }
            pendingBitmap?.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                string logPath = Path.Combine(sandboxRoot, "diagnostics", "host-failure.log");
                File.WriteAllText(logPath, ex.ToString(), new UTF8Encoding(false));
            }
            catch { }
            Console.Error.WriteLine($"Susie Host failure: {ex.GetType().Name}: {ex.Message}");
            return 5;
        }
    }


    private static bool IsContainedFile(string sandboxRoot, string candidate)
    {
        try
        {
            string fullPath = Path.GetFullPath(candidate);
            string root = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath);
        }
        catch { return false; }
    }

    private static byte[] ReadRemoteBuffer(long remoteHandle, long length)
    {
        if (remoteHandle <= 0 || length <= 0 || length > 1024L * 1024 * 1024 || length > int.MaxValue)
            throw new InvalidDataException("Input buffer handle or length is invalid.");
        IntPtr handle = ToNativeHandle(remoteHandle);
        IntPtr view = MapViewOfFile(handle, FileMapRead, 0, 0, new UIntPtr(checked((uint)length)));
        if (view == IntPtr.Zero) throw new IOException($"MapViewOfFile(input) failed: {Marshal.GetLastWin32Error()}");
        try
        {
            byte[] bytes = new byte[checked((int)length)];
            Marshal.Copy(view, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            UnmapViewOfFile(view);
            CloseHandle(handle);
        }
    }

    private static void WriteBitmapToRemoteBuffer(Bitmap source, long remoteHandle, long expectedLength)
    {
        long required = checked((long)source.Width * source.Height * 4);
        if (remoteHandle <= 0 || expectedLength != required || required > int.MaxValue)
            throw new InvalidDataException("Output buffer handle or dimensions do not match the validated bitmap.");
        IntPtr handle = ToNativeHandle(remoteHandle);
        IntPtr view = MapViewOfFile(handle, FileMapWrite, 0, 0, new UIntPtr(checked((uint)required)));
        if (view == IntPtr.Zero) throw new IOException($"MapViewOfFile(output) failed: {Marshal.GetLastWin32Error()}");
        try
        {
            using var normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(normalized))
                graphics.DrawImageUnscaled(source, 0, 0);
            Rectangle bounds = new(0, 0, normalized.Width, normalized.Height);
            BitmapData data = normalized.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowLength = checked(normalized.Width * 4);
                byte[] row = new byte[rowLength];
                for (int y = 0; y < normalized.Height; y++)
                {
                    IntPtr sourceRow = IntPtr.Add(data.Scan0, checked(y * data.Stride));
                    Marshal.Copy(sourceRow, row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(view, checked(y * rowLength)), row.Length);
                }
            }
            finally { normalized.UnlockBits(data); }
        }
        finally
        {
            UnmapViewOfFile(view);
            CloseHandle(handle);
        }
    }

    private static IntPtr ToNativeHandle(long value)
        => IntPtr.Size == 4 ? new IntPtr(unchecked((int)value)) : new IntPtr(value);

    private static void WriteMessage(StreamWriter writer, SusieHostMessage message)
        => writer.WriteLine(JsonSerializer.Serialize(message, JsonOptions));

    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, UIntPtr bytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr address);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
