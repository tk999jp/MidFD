using System.Runtime.InteropServices;

namespace MidFD;

/// <summary>Routes native video-surface clicks and wheel volume changes to playback controls.</summary>
internal sealed class NativeVideoSurfaceClickBridge : IDisposable, IMessageFilter
{
    private const int WmParentNotify = 0x0210;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmCaptureChanged = 0x0215;
    private const int WmCancelMode = 0x001F;
    private const int WmNcDestroy = 0x0082;
    private const int MkLButton = 0x0001;
    private const int WhMouseLl = 14;
    private const int WhGetMessage = 3;
    private const uint GaRoot = 2;
    private const int VkSpace = 0x20;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const long KeyWasPreviouslyDownMask = 1L << 30;

    private readonly Control _root;
    // LibVLC can create its video HWND on a separate native thread. Scoped
    // WH_GETMESSAGE hooks observe input only on threads owning this surface's HWNDs.
    private readonly LowLevelMouseProc _lowLevelProc;
    private IntPtr _lowLevelHook;
    private readonly object _physicalMessageGate = new();
    private readonly List<(int Message, Point Position, int Delta, long Received)> _physicalMessages = new();
    private bool _physicalButtonDown;
    private bool _physicalDragged;
    private Point _physicalDownScreenPoint;
    private readonly Action _clicked;
    private readonly Action<int> _adjustVolume;
    private readonly Action _spacePressed;
    private readonly Action<Keys> _arrowKeyPressed;
    private readonly Dictionary<IntPtr, SurfaceWindow> _windows = new();
    private readonly Dictionary<uint, IntPtr> _queueHooks = new();
    private readonly HookProc _queueHookCallback;
    private IntPtr _surfaceHandle;
    private bool _disposed;
    private bool _buttonDown;
    private bool _dragged;
    private Point _downScreenPoint;
    private int _wheelDeltaRemainder;
    internal int AcceptedClickCount { get; private set; }
    internal int AcceptedWheelCount { get; private set; }
    internal bool PhysicalInputHookInstalled => _lowLevelHook != IntPtr.Zero;
    internal bool? RootVisibleOverrideForTests { get; set; }
    internal Func<IntPtr, bool>? FocusedSurfaceWindowOverrideForTests { get; set; }

    public NativeVideoSurfaceClickBridge(
        Control root,
        Action clicked,
        Action<int> adjustVolume,
        Action spacePressed,
        Action<Keys> arrowKeyPressed)
    {
        _root = root;
        _queueHookCallback = ReadNativeQueue;
        if (root.IsHandleCreated) VideoInputTrace.Record("bridge-created", $"root=0x{root.Handle:X}");
        _lowLevelProc = HandleLowLevelMouseInput;
        // Hook registration is best-effort; the native-subclass/message-filter route
        // remains in place if Windows policy forbids low-level hooks.
        _lowLevelHook = SetWindowsHookEx(WhMouseLl, _lowLevelProc, GetModuleHandle(null), 0);
        if (_lowLevelHook == IntPtr.Zero)
            Services.LogService.Warn($"[MidPlayer.VideoMouse] low-level mouse hook unavailable; Win32={Marshal.GetLastWin32Error()}; using native-window fallback");
        else
            Services.LogService.Info("[MidPlayer.VideoMouse] physical mouse hook registered for video surface");
        VideoInputTrace.Record("physical-hook", $"installed={_lowLevelHook != IntPtr.Zero}");
        _clicked = clicked;
        _adjustVolume = adjustVolume;
        _spacePressed = spacePressed;
        _arrowKeyPressed = arrowKeyPressed;
        _root.HandleCreated += RootHandleCreated;
        _root.HandleDestroyed += RootHandleDestroyed;
        _root.ControlAdded += RootControlAdded;
        if (_root.IsHandleCreated) RefreshWindows();
        Application.AddMessageFilter(this);
    }

    private void RootHandleCreated(object? sender, EventArgs e) => RefreshWindows();

    private void RootHandleDestroyed(object? sender, EventArgs e) => ReleaseWindows();

    private void RootControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is not { IsDisposed: false } child) return;
        child.HandleCreated += ChildHandleCreated;
        if (child.IsHandleCreated) RefreshWindows();
    }

    private void ChildHandleCreated(object? sender, EventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        if (_disposed || !_root.IsHandleCreated) return;
        if (_surfaceHandle != _root.Handle) VideoInputTrace.Record("bridge-surface", $"root=0x{_root.Handle:X}");
        _surfaceHandle = _root.Handle;
        var nativeThreads = new HashSet<uint>();
        AddWindow(_root.Handle);
        EnumChildWindows(_root.Handle, (handle, _) =>
        {
            uint thread = GetWindowThreadProcessId(handle, out uint process);
            if (thread != GetCurrentThreadId() && process == Environment.ProcessId) nativeThreads.Add(thread);
            AddWindow(handle);
            return true;
        }, IntPtr.Zero);
        foreach (uint thread in _queueHooks.Keys.Except(nativeThreads).ToArray())
        {
            UnhookWindowsHookEx(_queueHooks[thread]);
            _queueHooks.Remove(thread);
        }
        foreach (uint thread in nativeThreads)
        {
            if (_queueHooks.ContainsKey(thread)) continue;
            // Native child windows can consume input before DispatchMessage/WndProc.
            // Observe only threads owning this surface, never a global keyboard hook.
            IntPtr hook = SetWindowsHookEx(WhGetMessage, _queueHookCallback, IntPtr.Zero, thread);
            if (hook != IntPtr.Zero) _queueHooks.Add(thread, hook);
            VideoInputTrace.Record("queue-hook", $"thread={thread} installed={hook != IntPtr.Zero}");
        }
    }

    private IntPtr ReadNativeQueue(int code, IntPtr removed, IntPtr address)
    {
        if (code >= 0 && removed == new IntPtr(1) && !_disposed)
        {
            NativeMessage native = Marshal.PtrToStructure<NativeMessage>(address);
            if (TryRouteNativeKeyboardMessage(ref native))
            {
                Marshal.StructureToPtr(native, address, false);
            }
            else if (_lowLevelHook == IntPtr.Zero
                && (native.Message is WmLButtonDown or WmLButtonUp or WmLButtonDoubleClick or WmMouseWheel
                    || (native.Message == WmMouseMove && (native.WParam.ToInt64() & MkLButton) != 0)))
            {
                IntPtr root = _surfaceHandle;
                Point point = GetPhysicalCursorPoint(out Point physicalPoint) ? physicalPoint
                    : native.Message == WmMouseWheel ? ScreenPointFromLParam(native.LParam)
                    : ToScreen(native.Hwnd, PointFromLParam(native.LParam));
                bool inside = root != IntPtr.Zero && IsWindowVisible(root)
                    && IsActiveVideoPoint(point, native.Message)
                    && (native.Hwnd == root || IsChild(root, native.Hwnd));
                if (inside)
                {
                    Message message = Message.Create(native.Hwnd, native.Message, native.WParam, native.LParam);
                    VideoInputTrace.Message("native-queue", message);
                    InvokeOnRootThread(() => PreFilterMessage(ref message));
                    native.Message = 0; // Our controls own click/wheel; do not also run VLC mouse actions.
                    Marshal.StructureToPtr(native, address, false);
                }
                else if (native.Message == WmLButtonUp)
                {
                    InvokeOnRootThread(CancelClick);
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, removed, address);
    }

    private bool TryRouteNativeKeyboardMessage(ref NativeMessage native)
    {
        if (native.Message is not (WmKeyDown or WmKeyUp) || !IsFocusedSurfaceWindow(native.Hwnd)) return false;

        int virtualKey = native.WParam.ToInt32();
        bool isSpace = virtualKey == VkSpace;
        Keys key = virtualKey switch
        {
            VkLeft => Keys.Left,
            VkUp => Keys.Up,
            VkRight => Keys.Right,
            VkDown => Keys.Down,
            _ => Keys.None
        };
        if (!isSpace && key == Keys.None) return false;
        if (IsControlOrAltPressed() || (!isSpace && IsShiftPressed())) return false;

        bool keyDown = native.Message == WmKeyDown;
        bool repeat = keyDown && (native.LParam.ToInt64() & KeyWasPreviouslyDownMask) != 0;
        VideoInputTrace.Record("native-key-queue", $"hwnd=0x{native.Hwnd:X} vk=0x{virtualKey:X} down={keyDown} repeat={repeat} root=0x{_surfaceHandle:X}");
        if (keyDown && (isSpace ? !repeat : true))
        {
            if (isSpace) PostOnRootThread(_spacePressed);
            else PostOnRootThread(() => _arrowKeyPressed(key));
        }

        // Space repeats are swallowed without another toggle; arrow repeats remain seek steps.
        native.Message = 0;
        return true;
    }

    private bool IsFocusedSurfaceWindow(IntPtr hwnd)
    {
        if (FocusedSurfaceWindowOverrideForTests is { } focusedSurfaceWindow)
            return focusedSurfaceWindow(hwnd);

        IntPtr root = _surfaceHandle;
        if (root == IntPtr.Zero || !IsWindowVisible(root) || !IsSurfaceWindow(hwnd)) return false;
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || GetAncestor(foreground, GaRoot) != GetAncestor(root, GaRoot)) return false;
        IntPtr focused = GetFocus();
        return focused != IntPtr.Zero && (focused == root || IsChild(root, focused));
    }

    internal void RefreshNativeWindows() => RefreshWindows();

    private void AddWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || _windows.ContainsKey(handle)) return;
        try
        {
            var window = new SurfaceWindow(this);
            window.AssignHandle(handle);
            _windows.Add(handle, window);
        }
        catch (InvalidOperationException)
        {
            // A native child can disappear while its HWND tree is being enumerated.
        }
    }

    private void BeforeMessage(SurfaceWindow window, ref Message message)
    {
        if (_lowLevelHook != IntPtr.Zero) return;
        switch (message.Msg)
        {
            case WmLButtonDown:
            case WmLButtonDoubleClick:
                Point downPoint = GetPhysicalCursorPoint(out Point physicalDownPoint) ? physicalDownPoint
                    : ToScreen(window.Handle, PointFromLParam(message.LParam));
                if (IsActiveVideoPoint(downPoint, message.Msg)) BeginClick(downPoint);
                else CancelClick();
                break;
            case WmMouseMove when (message.WParam.ToInt64() & MkLButton) != 0:
                Point movePoint = GetPhysicalCursorPoint(out Point physicalMovePoint) ? physicalMovePoint
                    : ToScreen(window.Handle, PointFromLParam(message.LParam));
                TrackMove(movePoint);
                break;
            case WmCaptureChanged:
                IntPtr nextCapture = message.WParam;
                if (_buttonDown && nextCapture != IntPtr.Zero && !IsSurfaceWindow(nextCapture)) CancelClick();
                break;
            case WmCancelMode:
                CancelClick();
                break;
        }
    }

    private bool HandleMouseWheel(ref Message message)
    {
        if (_lowLevelHook != IntPtr.Zero || _disposed || _root.IsDisposed || !_root.IsHandleCreated || !_root.Visible)
            return false;
        Point point = GetPhysicalCursorPoint(out Point physicalPoint) ? physicalPoint : ScreenPointFromLParam(message.LParam);
        if (!IsActiveVideoPoint(point, WmMouseWheel))
            return false;
        TryAdjustVolumeFromWheel(message.WParam);
        message.Result = IntPtr.Zero;
        return true;
    }

    public bool PreFilterMessage(ref Message message)
    {
        VideoInputTrace.Message("filter", message);
        if (_disposed || _root.IsDisposed || !_root.IsHandleCreated
            || !(RootVisibleOverrideForTests ?? _root.Visible)) return false;

        if ((message.Msg is WmKeyDown or WmKeyUp) && IsSurfaceWindow(message.HWnd))
        {
            var native = new NativeMessage
            {
                Hwnd = message.HWnd,
                Message = message.Msg,
                WParam = message.WParam,
                LParam = message.LParam
            };
            if (TryRouteNativeKeyboardMessage(ref native))
            {
                message.Msg = native.Message;
                return true;
            }
        }

        if (IsSurfaceWindow(message.HWnd) && TakePhysicalDelivery(message)) return true;
        switch (message.Msg)
        {
            case WmLButtonDown:
            case WmLButtonDoubleClick:
                if (!TryGetScreenPoint(message, out Point downPoint) || !IsActiveVideoPoint(downPoint, message.Msg)) return false;
                BeginClick(downPoint);
                return true;
            case WmMouseMove when _buttonDown && (message.WParam.ToInt64() & MkLButton) != 0:
                if (TryGetScreenPoint(message, out Point movePoint)) TrackMove(movePoint);
                return true;
            case WmLButtonUp when _buttonDown:
                bool isInside = TryGetScreenPoint(message, out Point upPoint) && IsActiveVideoPoint(upPoint, message.Msg);
                if (isInside) TrackMove(upPoint);
                if (CompleteClick() && isInside)
                {
                    AcceptedClickCount++;
                    InvokeOnRootThread(_clicked);
                }
                else CancelClick();
                return true;
            case WmMouseWheel:
                if (!TryGetScreenPoint(message, out Point wheelPoint) || !IsActiveVideoPoint(wheelPoint, message.Msg)) return false;
                TryAdjustVolumeFromWheel(message.WParam);
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetScreenPoint(Message message, out Point screenPoint)
    {
        screenPoint = Point.Empty;
        if (message.HWnd == IntPtr.Zero) return false;
        if (!GetPhysicalCursorPoint(out screenPoint))
            screenPoint = message.Msg == WmMouseWheel ? ScreenPointFromLParam(message.LParam)
                : ToScreen(message.HWnd, PointFromLParam(message.LParam));
        return true;
    }

    private void TryAdjustVolumeFromWheel(IntPtr wParam)
        => AdjustVolumeFromWheelDelta(unchecked((short)((wParam.ToInt64() >> 16) & 0xffff)));

    private void AdjustVolumeFromWheelDelta(int delta)
    {
        if (delta == 0) return;
        _wheelDeltaRemainder += delta;
        int steps = _wheelDeltaRemainder / 120;
        _wheelDeltaRemainder %= 120;
        if (steps != 0)
        {
            AcceptedWheelCount += Math.Abs(steps);
            int change = Math.Clamp(steps * 5, -100, 100);
            InvokeOnRootThread(() => _adjustVolume(change));
        }
    }

    private void BeginClick(Point screenPoint)
    {
        _buttonDown = true;
        _dragged = false;
        _downScreenPoint = screenPoint;
    }

    private void TrackMove(Point screenPoint)
    {
        if (!_buttonDown || _dragged) return;
        Size threshold = SystemInformation.DragSize;
        _dragged = Math.Abs(screenPoint.X - _downScreenPoint.X) > Math.Max(1, threshold.Width / 2)
            || Math.Abs(screenPoint.Y - _downScreenPoint.Y) > Math.Max(1, threshold.Height / 2);
    }

    private bool CompleteClick()
    {
        bool click = _buttonDown && !_dragged;
        _buttonDown = false;
        _dragged = false;
        return click;
    }

    private bool IsSurfaceWindow(IntPtr hwnd)
        => _root.IsHandleCreated && (hwnd == _root.Handle || IsChild(_root.Handle, hwnd));

    private void CancelClick()
    {
        _buttonDown = false;
        _dragged = false;
    }

    private void InvokeOnRootThread(Action action)
    {
        if (_disposed || _root.IsDisposed || !_root.IsHandleCreated) return;
        if (!_root.InvokeRequired)
        {
            action();
            return;
        }
        try { _root.BeginInvoke(() => { if (!_disposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    private static Point ToScreen(IntPtr hwnd, Point point)
    {
        var native = new NativePoint { X = point.X, Y = point.Y };
        return ClientToScreen(hwnd, ref native) ? new Point(native.X, native.Y) : point;
    }

    private static bool GetPhysicalCursorPoint(out Point point)
    {
        var native = new NativePoint();
        bool success = GetPhysicalCursorPos(ref native);
        point = success ? new Point(native.X, native.Y) : Point.Empty;
        return success;
    }

    private static Point PointFromLParam(IntPtr value)
    {
        long packed = value.ToInt64();
        return new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
    }

    private static Point ScreenPointFromLParam(IntPtr value)
    {
        long packed = value.ToInt64();
        return new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
    }

    private void AfterMessage(SurfaceWindow window, int messageId, IntPtr lParam)
    {
        if (messageId == WmParentNotify)
        {
            InvokeOnRootThread(RefreshWindows);
        }
        else if (messageId == WmLButtonUp && _lowLevelHook == IntPtr.Zero)
        {
            Point upPoint = GetPhysicalCursorPoint(out Point physicalPoint) ? physicalPoint
                : ToScreen(window.Handle, PointFromLParam(lParam));
            bool isInside = IsActiveVideoPoint(upPoint, messageId);
            if (isInside) TrackMove(upPoint);
            if (CompleteClick() && isInside)
            {
                AcceptedClickCount++;
                InvokeOnRootThread(_clicked);
            }
            else CancelClick();
        }
        else if (messageId == WmNcDestroy)
        {
            _windows.Remove(window.Handle);
            window.ReleaseAssignedHandle();
        }
    }

    private void PostOnRootThread(Action action)
    {
        if (_disposed || _root.IsDisposed || !_root.IsHandleCreated) return;
        try { _root.BeginInvoke(() => { if (!_disposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    private bool TakePhysicalDelivery(Message message)
    {
        int kind = message.Msg == WmLButtonDoubleClick ? WmLButtonDown : message.Msg;
        if (kind is not (WmLButtonDown or WmLButtonUp or WmMouseWheel)) return false;
        Point messagePoint = kind == WmMouseWheel ? ScreenPointFromLParam(message.LParam)
            : ToScreen(message.HWnd, PointFromLParam(message.LParam));
        Point point = messagePoint;
        var physicalCursor = new NativePoint();
        if (GetPhysicalCursorPos(ref physicalCursor)) point = new Point(physicalCursor.X, physicalCursor.Y);
        int delta = kind == WmMouseWheel ? unchecked((short)((message.WParam.ToInt64() >> 16) & 0xffff)) : 0;
        lock (_physicalMessageGate)
        {
            _physicalMessages.RemoveAll(item => Environment.TickCount64 - item.Received > 2000);
            int index = _physicalMessages.FindIndex(item => item.Message == kind && item.Position == point && item.Delta == delta);
            bool matched = index >= 0;
            if (matched) _physicalMessages.RemoveAt(index);
            VideoInputTrace.Record("physical-delivery", $"hwnd=0x{message.HWnd:X} msg=0x{kind:X} messagePoint={messagePoint.X},{messagePoint.Y} cursor={point.X},{point.Y} delta={delta} matched={matched} queued={_physicalMessages.Count}");
            return matched;
        }
    }

    private void RememberPhysicalInput(int message, LowLevelMouseData data)
    {
        // GetMessageTime can be replaced by a reentrant UI message. Match each queued
        // surface delivery once by kind, position and wheel delta instead of timestamp.
        lock (_physicalMessageGate)
        {
            _physicalMessages.Add((message, new Point(data.Position.X, data.Position.Y),
                message == WmMouseWheel ? unchecked((short)(data.MouseData >> 16)) : 0, Environment.TickCount64));
            if (_physicalMessages.Count > 128) _physicalMessages.RemoveAt(0);
        }
    }

    private IntPtr HandleLowLevelMouseInput(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !_disposed && !_root.IsDisposed && _root.IsHandleCreated)
        {
            int message = wParam.ToInt32();
            if (message is WmLButtonDown or WmLButtonUp or WmMouseMove or WmMouseWheel)
            {
                LowLevelMouseData data = Marshal.PtrToStructure<LowLevelMouseData>(lParam);
                var position = new Point(data.Position.X, data.Position.Y);
                bool overVideo = IsActiveVideoPoint(position, message);
                if (VideoInputTrace.Enabled && message != WmMouseMove)
                    VideoInputTrace.Record("hit-test", $"msg=0x{message:X} point={position.X},{position.Y} overVideo={overVideo} root=0x{_surfaceHandle:X} focus=0x{GetFocus():X} foreground=0x{GetForegroundWindow():X}");
                if (overVideo && message != WmMouseMove)
                    VideoInputTrace.Record("physical-input", $"msg=0x{message:X} x={position.X} y={position.Y} time={data.Time}");
                switch (message)
                {
                    case WmLButtonDown when overVideo:
                        RememberPhysicalInput(message, data);
                        _physicalButtonDown = true;
                        _physicalDragged = false;
                        _physicalDownScreenPoint = position;
                        break;
                    case WmMouseMove when _physicalButtonDown:
                        UpdatePhysicalDrag(position);
                        break;
                    case WmLButtonUp when _physicalButtonDown:
                        RememberPhysicalInput(message, data);
                        UpdatePhysicalDrag(position);
                        bool completedPhysicalClick = !_physicalDragged && overVideo;
                        _physicalButtonDown = false;
                        _physicalDragged = false;
                        if (completedPhysicalClick)
                        {
                            AcceptedClickCount++;
                            if (AcceptedClickCount == 1)
                                Services.LogService.Info("[MidPlayer.VideoMouse] first physical video click accepted");
                            PostOnRootThread(_clicked);
                        }
                        break;
                    case WmMouseWheel when overVideo:
                        RememberPhysicalInput(message, data);
                        if (AcceptedWheelCount == 0)
                            Services.LogService.Info("[MidPlayer.VideoMouse] first physical video wheel received");
                        int delta = unchecked((short)(data.MouseData >> 16));
                        PostOnRootThread(() => AdjustVolumeFromWheelDelta(delta));
                        break;
                }
            }
        }
        return CallNextHookEx(_lowLevelHook, code, wParam, lParam);
    }

    private bool IsActiveVideoPoint(Point screenPoint, int inputMessage)
    {
        // Low-level hooks can run while Windows has switched the UI thread's
        // DPI context for another window. Normalize virtualized screen APIs to
        // physical desktop coordinates before comparing against hook coordinates.
        IntPtr previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try { return IsActiveVideoPointInPhysicalCoordinates(screenPoint, inputMessage); }
        finally
        {
            if (previousDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpiContext);
        }
    }

    private bool IsActiveVideoPointInPhysicalCoordinates(Point screenPoint, int inputMessage)
    {
        Rectangle bounds = _root.RectangleToScreen(_root.ClientRectangle);
        if (!_root.Visible || !bounds.Contains(screenPoint))
        {
            if (VideoInputTrace.Enabled && inputMessage != WmMouseMove)
                VideoInputTrace.Record("hit-test-result", $"point={screenPoint.X},{screenPoint.Y} inside=False reason=bounds root=0x{_root.Handle:X} bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
            return false;
        }
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || GetAncestor(foreground, GaRoot) != GetAncestor(_root.Handle, GaRoot))
        {
            if (VideoInputTrace.Enabled && inputMessage != WmMouseMove)
                VideoInputTrace.Record("hit-test-result", $"point={screenPoint.X},{screenPoint.Y} inside=False reason=foreground root=0x{_root.Handle:X} foreground=0x{foreground:X} bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
            return false;
        }
        var point = new NativePoint { X = screenPoint.X, Y = screenPoint.Y };
        IntPtr target = WindowFromPoint(point);
        bool inside = target == _root.Handle || (target != IntPtr.Zero && IsChild(_root.Handle, target));
        if (VideoInputTrace.Enabled && inputMessage != WmMouseMove)
            VideoInputTrace.Record("hit-test-result", $"point={screenPoint.X},{screenPoint.Y} inside={inside} target=0x{target:X} root=0x{_root.Handle:X} bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
        return inside;
    }

    private void UpdatePhysicalDrag(Point screenPoint)
    {
        if (!_physicalButtonDown || _physicalDragged) return;
        Size threshold = SystemInformation.DragSize;
        _physicalDragged = Math.Abs(screenPoint.X - _physicalDownScreenPoint.X) > Math.Max(1, threshold.Width / 2)
            || Math.Abs(screenPoint.Y - _physicalDownScreenPoint.Y) > Math.Max(1, threshold.Height / 2);
    }

    private void ReleaseWindows()
    {
        _surfaceHandle = IntPtr.Zero;
        foreach (IntPtr hook in _queueHooks.Values) UnhookWindowsHookEx(hook);
        _queueHooks.Clear();
        lock (_physicalMessageGate) _physicalMessages.Clear();
        foreach (SurfaceWindow window in _windows.Values.ToArray()) window.ReleaseAssignedHandle();
        _windows.Clear();
        CancelClick();
        _physicalButtonDown = false;
        _physicalDragged = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _root.HandleCreated -= RootHandleCreated;
        _root.HandleDestroyed -= RootHandleDestroyed;
        _root.ControlAdded -= RootControlAdded;
        Application.RemoveMessageFilter(this);
        if (_lowLevelHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_lowLevelHook);
            _lowLevelHook = IntPtr.Zero;
        }
        foreach (Control child in _root.Controls) child.HandleCreated -= ChildHandleCreated;
        ReleaseWindows();
    }

    private sealed class SurfaceWindow(NativeVideoSurfaceClickBridge owner) : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            VideoInputTrace.Message("wndproc", m);
            int messageId = m.Msg;
            NativeMessage native = new()
            {
                Hwnd = m.HWnd,
                Message = messageId,
                WParam = m.WParam,
                LParam = m.LParam
            };
            if (owner.TryRouteNativeKeyboardMessage(ref native))
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (messageId == WmMouseWheel && owner.HandleMouseWheel(ref m))
            {
                return;
            }
            IntPtr lParam = m.LParam;
            owner.BeforeMessage(this, ref m);
            base.WndProc(ref m);
            owner.AfterMessage(this, messageId, lParam);
        }

        public void ReleaseAssignedHandle()
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseData
    {
        public NativePoint Position;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageTime();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static bool IsControlOrAltPressed()
        => (GetKeyState(0x11) & 0x8000) != 0 || (GetKeyState(0x12) & 0x8000) != 0;

    private static bool IsShiftPressed() => (GetKeyState(0x10) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public int Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
}

internal static class VideoInputTrace
{
    private static string? _path = Environment.GetEnvironmentVariable("MIDPLAYER_INPUT_TRACE");
    private static readonly object Gate = new();
    internal static bool Enabled => !string.IsNullOrWhiteSpace(Volatile.Read(ref _path));
    internal static string? SetPathForTests(string? path) => Interlocked.Exchange(ref _path, path);
    internal static void Chrome(string name, Control control, string detail = "")
    {
        if (!Enabled || !control.IsHandleCreated || !control.Visible) return;
        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
        Record("chrome", $"name={name} hwnd=0x{control.Handle:X} x={bounds.X} y={bounds.Y} w={bounds.Width} h={bounds.Height} {detail}");
    }
    internal static void Message(string stage, Message message)
    {
        if (!Enabled) return;
        if (message.Msg is 0x0201 or 0x0202 or 0x020A)
            Record(stage, $"hwnd=0x{message.HWnd:X} msg=0x{message.Msg:X} wp={message.WParam} lp={message.LParam}");
    }
    internal static void Record(string stage, string detail)
    {
        string? path = Volatile.Read(ref _path);
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Gate)
        {
            try { File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} tid={GetCurrentThreadId()} exe={Environment.ProcessPath} stage={stage} {detail}{Environment.NewLine}"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
