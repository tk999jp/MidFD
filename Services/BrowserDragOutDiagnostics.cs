using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>Opt-in, path-free timing for one browser drag-archive gesture.</summary>
internal static class BrowserDragOutDiagnostics
{
    internal const string EnableEnvironmentVariable = "MIDFD_DRAG_ARCHIVE_DIAGNOSTICS";
    private static readonly bool Enabled = IsOptedIn(Environment.GetEnvironmentVariable(EnableEnvironmentVariable));

    internal static long CaptureStartTimestampIfEnabled()
        => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static BrowserDragOutDiagnosticSession? TryCreate(
        BrowserDragMouseButton mouseButton,
        long startedAtTimestamp)
    {
        if (!Enabled || startedAtTimestamp <= 0)
        {
            return null;
        }

        try
        {
            return new BrowserDragOutDiagnosticSession(mouseButton, LogService.Info, startedAtTimestamp);
        }
        catch
        {
            return null;
        }
    }

    internal static BrowserDragOutDiagnosticSession? TryCreateForTesting(
        string? optInValue,
        Action<string> log,
        BrowserDragMouseButton mouseButton = BrowserDragMouseButton.Left,
        Func<bool>? buttonStateProbe = null)
        => IsOptedIn(optInValue)
            ? new BrowserDragOutDiagnosticSession(mouseButton, log, Stopwatch.GetTimestamp(), buttonStateProbe)
            : null;

    private static bool IsOptedIn(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal);
}

internal sealed class BrowserDragOutDiagnosticSession
{
    private const int MaximumBufferedEvents = 16;
    private const int VirtualKeyLeftButton = 0x01;
    private const int VirtualKeyRightButton = 0x02;
    private const short KeyDownMask = unchecked((short)0x8000);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern short GetAsyncKeyState(int virtualKey);

    private readonly Guid _gestureId = Guid.NewGuid();
    private readonly long _startedAtTimestamp;
    private readonly Action<string> _log;
    private readonly BrowserDragMouseButton _mouseButton;
    private readonly Func<bool>? _buttonStateProbe;
    private readonly object _gate = new();
    private readonly List<string> _bufferedEvents = new(MaximumBufferedEvents);
    private int _droppedEvents;

    internal BrowserDragOutDiagnosticSession(
        BrowserDragMouseButton mouseButton,
        Action<string> log,
        long startedAtTimestamp,
        Func<bool>? buttonStateProbe = null)
    {
        _mouseButton = mouseButton;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _startedAtTimestamp = startedAtTimestamp;
        _buttonStateProbe = buttonStateProbe;
    }

    internal string CorrelationId => _gestureId.ToString("N");

    internal bool? SampleInitiatingButtonPressed()
    {
        if (_buttonStateProbe is not null)
        {
            try
            {
                return _buttonStateProbe();
            }
            catch
            {
                return null;
            }
        }

        int virtualKey = _mouseButton switch
        {
            BrowserDragMouseButton.Left => VirtualKeyLeftButton,
            BrowserDragMouseButton.Right => VirtualKeyRightButton,
            _ => 0
        };
        if (virtualKey == 0)
        {
            return null;
        }

        try
        {
            return (GetAsyncKeyState(virtualKey) & KeyDownMask) != 0;
        }
        catch
        {
            return null;
        }
    }

    internal void Record(
        string stage,
        bool? buttonPressed = null,
        long? zipSizeBytes = null,
        string? reason = null)
    {
        string buttonValue = buttonPressed switch
        {
            true => "true",
            false => "false",
            null => "unknown"
        };
        string sizeValue = zipSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        string reasonValue = string.IsNullOrWhiteSpace(reason) ? "none" : reason;
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(_startedAtTimestamp, Stopwatch.GetTimestamp()).TotalMilliseconds;
        string entry = string.Create(
            CultureInfo.InvariantCulture,
            $"stage={stage} elapsedMs={elapsedMilliseconds:F1} buttonPressed={buttonValue} zipSizeBytes={sizeValue} reason={reasonValue}");

        lock (_gate)
        {
            if (_bufferedEvents.Count < MaximumBufferedEvents)
            {
                _bufferedEvents.Add(entry);
            }
            else
            {
                _droppedEvents++;
            }
        }
    }

    internal void MarkWorkerStarted(bool? buttonPressed)
    {
        Record("archive_worker_started", buttonPressed);
    }

    internal void MarkWorkerCompleted(bool? buttonPressed, long? zipSizeBytes, string reason)
    {
        Record("archive_worker_completed", buttonPressed, zipSizeBytes, reason);
    }

    internal void Flush()
    {
        string[] entries;
        int droppedEvents;
        lock (_gate)
        {
            if (_bufferedEvents.Count == 0 && _droppedEvents == 0)
            {
                return;
            }

            entries = _bufferedEvents.ToArray();
            _bufferedEvents.Clear();
            droppedEvents = _droppedEvents;
            _droppedEvents = 0;
        }

        var message = new StringBuilder(128 + entries.Sum(entry => entry.Length));
        message.Append("[DragArchiveProbe] gesture=")
            .Append(CorrelationId)
            .Append(' ')
            .AppendJoin(" | ", entries);
        if (droppedEvents > 0)
        {
            message.Append(" | droppedEvents=").Append(droppedEvents);
        }

        try
        {
            _log(message.ToString());
        }
        catch
        {
            // Opt-in diagnostics must not change drag behavior if logging fails.
        }
    }
}
