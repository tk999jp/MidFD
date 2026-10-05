using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MidFD.Services;

internal enum DispatchAwareWaitResult
{
    Signaled,
    Timeout,
    MessageLoopTerminated,
    Failed
}

/// <summary>
/// STA上でkernel handleを待機しながら、現在threadのwindow messageを限定的にdispatchする。
/// </summary>
internal static class DispatchAwareWaitService
{
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint QueueStatusAllInput = 0x04FF;
    private const uint MsgWaitInputAvailable = 0x0004;
    private const uint PeekMessageRemove = 0x0001;
    private const uint WmQuit = 0x0012;
    private const uint Infinite = 0xFFFFFFFF;

    internal static DispatchAwareWaitResult WaitForSignal(
        WaitHandle signal,
        uint timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(signal);
        IntPtr handle = signal.SafeWaitHandle.DangerousGetHandle();
        long? deadline = timeoutMilliseconds == Infinite
            ? null
            : Stopwatch.GetTimestamp() +
              (long)(Stopwatch.Frequency * (timeoutMilliseconds / 1_000.0));

        while (true)
        {
            uint remainingMilliseconds = GetRemainingMilliseconds(timeoutMilliseconds, deadline);
            uint result = MsgWaitForMultipleObjectsEx(
                1,
                ref handle,
                remainingMilliseconds,
                QueueStatusAllInput,
                MsgWaitInputAvailable);

            if (result == WaitObject0)
            {
                return DispatchAwareWaitResult.Signaled;
            }

            if (result == WaitTimeout)
            {
                return DispatchAwareWaitResult.Timeout;
            }

            if (result == WaitFailed)
            {
                return DispatchAwareWaitResult.Failed;
            }

            if (result != WaitObject0 + 1)
            {
                return DispatchAwareWaitResult.Failed;
            }

            while (PeekMessage(
                out NativeMessage message,
                IntPtr.Zero,
                0,
                0,
                PeekMessageRemove))
            {
                if (message.MessageId == WmQuit)
                {
                    PostQuitMessage(message.WParam.ToInt32());
                    return DispatchAwareWaitResult.MessageLoopTerminated;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            if (deadline.HasValue && Stopwatch.GetTimestamp() >= deadline.Value)
            {
                return DispatchAwareWaitResult.Timeout;
            }
        }
    }

    private static uint GetRemainingMilliseconds(uint timeoutMilliseconds, long? deadline)
    {
        if (!deadline.HasValue)
        {
            return Infinite;
        }

        long remainingTicks = deadline.Value - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return 0;
        }

        long remainingMilliseconds = remainingTicks * 1_000L / Stopwatch.Frequency;
        return (uint)Math.Min(
            timeoutMilliseconds,
            Math.Max(1, remainingMilliseconds));
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount,
        ref IntPtr pHandles,
        uint dwMilliseconds,
        uint dwWakeMask,
        uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
    private static extern bool PeekMessage(
        out NativeMessage lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        uint wRemoveMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TranslateMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    private static extern IntPtr DispatchMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void PostQuitMessage(int nExitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint MessageId;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }
}
