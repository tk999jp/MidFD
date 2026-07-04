using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidFD.Helpers;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    private const int WM_SIZE = 0x0005;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_SHOWWINDOW = 0x0018;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_SIZE = 0xF000;
    private const int SC_MOVE = 0xF010;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_MAXIMIZE = 0xF030;
    private const int SC_CLOSE = 0xF060;
    private const int SC_RESTORE = 0xF120;
    private const int SC_KEYMENU = 0xF100;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int SW_RESTORE = 9;

    private Rectangle? _lastKnownGoodNormalBounds;
    private bool _isApplyingWindowBoundsRecovery;
    private Rectangle? _normalBoundsBeforeMinimize;
    private DateTime _lastRestoreUtc = DateTime.MinValue;
    private bool _isInRestorePlacementWatch;
    private Rectangle? _restoreBaselineNormalBounds;
    private bool _restorePlacementRepairScheduled;
    private int _restorePlacementRepairCount;
    private Rectangle? _pendingRestoreRepairBounds;
    private Rectangle? _lastRecoveredCollapsedBounds;
    private DateTime _lastRecoveryUtc;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public int flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, [In] ref WINDOWPLACEMENT lpwndpl);

    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
        public override string ToString() => $"({left},{top},{right},{bottom}) {right - left}x{bottom - top}";
    }

#pragma warning disable CS0649 // Win32 API 構造体のフィールドへの代入警告を抑制
    private struct POINT
    {
        public int x;
        public int y;
        public override string ToString() => $"({x},{y})";
    }

    private struct MinMaxInfo
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
#pragma warning restore CS0649

    private static bool IsCollapsedWindowBounds(Rectangle bounds)
    {
        return WindowPlacementBoundsHelper.IsCollapsedWindowBounds(
            bounds,
            MinimumNormalWindowWidth,
            MinimumNormalWindowHeight);
    }

    private static Rectangle ToRectangle(RECT rect)
    {
        return Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
    }

    private static RECT FromRectangle(Rectangle rect)
    {
        return new RECT { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
    }

    private void LogWindowPlacementSnapshot(string context)
    {
        var wp = new WINDOWPLACEMENT();
        wp.length = Marshal.SizeOf(wp);
        if (GetWindowPlacement(this.Handle, ref wp))
        {
            Rectangle normal = ToRectangle(wp.rcNormalPosition);
            LogService.Info($"[WindowRestoreFloorHit] {context} State={this.WindowState} PlacementNormal={FormatBoundsForLog(normal)} Bounds={FormatBoundsForLog(this.Bounds)} Watch={_isInRestorePlacementWatch}");
        }
    }

    private bool IsCollapsedWindowPlacementNormal(WINDOWPLACEMENT placement)
    {
        return IsCollapsedWindowBounds(ToRectangle(placement.rcNormalPosition));
    }

    private bool IsRestoreFloorHitCorruption(Rectangle candidate)
    {
        // 復元監視中のみ、高さが 480px 付近（floor-hit）なら汚染とみなす
        // 手動リサイズ開始（SC_SIZE）時に監視は終了するため、通常操作への干渉は抑制される。
        if (_isInRestorePlacementWatch)
        {
            if (candidate.Height > 0 && candidate.Height <= MinimumNormalWindowHeight + 4)
            {
                return true;
            }
        }
        // 1秒過ぎたら監視終了（フェイルセーフ）
        if (_isInRestorePlacementWatch && (DateTime.UtcNow - _lastRestoreUtc).TotalMilliseconds >= 1000)
        {
            _isInRestorePlacementWatch = false;
            LogService.Info($"[WindowRestoreFloorHit] End restore watch Reason=Timeout Bounds={FormatBoundsForLog(this.Bounds)}");
        }
        return false;
    }

    private bool ShouldSuppressDuplicateCollapsedRecovery(Rectangle collapsedBounds)
    {
        if (_lastRecoveredCollapsedBounds == null) return false;
        // タブ復元直後は collapsed bounds になりやすいため、1000ms 経過時点で再評価して補正する。
        if (collapsedBounds == _lastRecoveredCollapsedBounds.Value &&
            (DateTime.UtcNow - _lastRecoveryUtc).TotalMilliseconds < 1000)
        {
            return true;
        }
        return false;
    }

    private void TryCaptureCurrentNormalBounds()
    {
        if (_isApplyingWindowBoundsRecovery) return;
        if (this.WindowState != FormWindowState.Normal) return;
        var currentBounds = this.Bounds;
        if (!IsSaneNormalBounds(currentBounds) || !HasUsableClientArea()) return;
        if (IsRestoreFloorHitCorruption(currentBounds))
        {
            LogService.Info($"[WindowRestoreFloorHit] Skip Capture CurrentNormalBounds due to floor-hit corruption: {FormatBoundsForLog(currentBounds)}");
            return;
        }
        _lastKnownGoodNormalBounds = currentBounds;
        // Record as baseline if it's "truly sane" (clearly above the floor)
        // This ensures Win+M has a reliable target to restore to.
        if (currentBounds.Height > MinimumNormalWindowHeight + 40)
        {
            _restoreBaselineNormalBounds = currentBounds;
        }
    }

    private void ScheduleRestorePlacementRepair(Rectangle repairBounds, string trigger)
    {
        if (_restorePlacementRepairScheduled)
        {
            LogService.Info($"[WindowRestoreRepairLoop] Repair scheduled skipped because already scheduled. Trigger={trigger}");
            return;
        }
        if (_restorePlacementRepairCount >= 2)
        {
            LogService.Warn($"[WindowRestoreRepairLoop] Repair suppressed because limit reached. Trigger={trigger}");
            return;
        }
        LogService.Info($"[WindowRestoreRepairLoop] Detected floor-hit; schedule repair. Trigger={trigger} Target={FormatBoundsForLog(repairBounds)}");
        _restorePlacementRepairScheduled = true;
        _pendingRestoreRepairBounds = repairBounds;
        BeginInvoke(new Action(async () =>
        {
            await Task.Delay(100);
            ApplyScheduledRestorePlacementRepair(trigger);
        }));
    }

    private void ApplyScheduledRestorePlacementRepair(string trigger)
    {
        if (!_restorePlacementRepairScheduled || _pendingRestoreRepairBounds == null) return;
        try
        {
            _isApplyingWindowBoundsRecovery = true;
            _restorePlacementRepairCount++;
            Rectangle recoveryBounds = _pendingRestoreRepairBounds.Value;
            LogService.Info($"[WindowRestoreRepairLoop] Repair applied count={_restorePlacementRepairCount} Bounds={FormatBoundsForLog(recoveryBounds)} Trigger={trigger}");
            var wp = new WINDOWPLACEMENT();
            wp.length = Marshal.SizeOf(wp);
            if (GetWindowPlacement(this.Handle, ref wp))
            {
                int beforeShowCmd = wp.showCmd;
                bool beforeVisible = this.Visible;
                FormWindowState beforeState = this.WindowState;
                wp.rcNormalPosition = FromRectangle(recoveryBounds);
                wp.showCmd = SW_SHOWNORMAL;
                SetWindowPlacement(this.Handle, ref wp);
                var wpAfter = new WINDOWPLACEMENT();
                wpAfter.length = Marshal.SizeOf(wpAfter);
                GetWindowPlacement(this.Handle, ref wpAfter);
                LogService.Info($"[WindowRestoreShowCmd] BeforeRepair Visible={beforeVisible} WindowState={beforeState} PlacementShowCmd={beforeShowCmd} | AfterRepair Visible={this.Visible} WindowState={this.WindowState} PlacementShowCmd={wpAfter.showCmd}");
            }
            if (!this.Visible)
            {
                this.Show();
            }
            this.WindowState = FormWindowState.Normal;
            this.SetBounds(recoveryBounds.X, recoveryBounds.Y, recoveryBounds.Width, recoveryBounds.Height);
            _lastKnownGoodNormalBounds = recoveryBounds;
            _lastRecoveredCollapsedBounds = this.Bounds;
            _lastRecoveryUtc = DateTime.UtcNow;
            if (_isInRestorePlacementWatch && IsSaneNormalBounds(this.Bounds) && this.Bounds.Height > MinimumNormalWindowHeight + 40)
            {
                _isInRestorePlacementWatch = false;
                LogService.Info($"[WindowRestoreFloorHit] End restore watch Reason=RepairSuccess Bounds={FormatBoundsForLog(this.Bounds)}");
            }
        }
        finally
        {
            _restorePlacementRepairScheduled = false;
            _pendingRestoreRepairBounds = null;
            BeginInvoke(new Action(async () =>
            {
                await Task.Delay(50);
                _isApplyingWindowBoundsRecovery = false;
                LogService.Info("[WindowRestoreRepairLoop] Repair guard released");
            }));
        }
    }

    private void RecoverCollapsedWindowBounds(string trigger)
    {
        if (_isApplyingWindowBoundsRecovery || _restorePlacementRepairScheduled) return;
        var currentBounds = this.Bounds;
        if (ShouldSuppressDuplicateCollapsedRecovery(currentBounds))
        {
            LogService.Info($"[WindowVisibility] SuppressDuplicateCollapsedRecovery Trigger={trigger} CollapsedBounds={FormatBoundsForLog(currentBounds)}");
            return;
        }
        Rectangle recoveryBounds;
        string fallbackSource;
        // Priority: PreMinimize -> RestoreBaseline -> LastKnownGood -> Settings -> Default
        if (_normalBoundsBeforeMinimize is { } preMin && IsSaneNormalBounds(preMin))
        {
            recoveryBounds = preMin;
            fallbackSource = "PreMinimize";
        }
        else if (_restoreBaselineNormalBounds is { } baseline && IsSaneNormalBounds(baseline))
        {
            recoveryBounds = baseline;
            fallbackSource = "RestoreBaseline";
        }
        else if (_lastKnownGoodNormalBounds is { } lastGood && IsSaneNormalBounds(lastGood))
        {
            recoveryBounds = lastGood;
            fallbackSource = "LastKnownGood";
        }
        else if (IsSaneNormalBounds(new Rectangle(_settings.Window.X, _settings.Window.Y, _settings.Window.Width, _settings.Window.Height)))
        {
            recoveryBounds = new Rectangle(_settings.Window.X, _settings.Window.Y, _settings.Window.Width, _settings.Window.Height);
            fallbackSource = "Settings";
        }
        else
        {
            var primaryArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
            recoveryBounds = new Rectangle(primaryArea.X + 100, primaryArea.Y + 100, 1024, 768);
            fallbackSource = "DefaultSafe";
        }
        string logMsg = "[WindowVisibility] RecoverCollapsedWindowBounds Scheduled " +
            "Trigger=" + trigger + " " +
            "CollapsedBounds=" + FormatBoundsForLog(currentBounds) + " " +
            "RecoveryBounds=" + FormatBoundsForLog(recoveryBounds) + " " +
            "Source=" + fallbackSource;
        LogService.Info(logMsg);
        ScheduleRestorePlacementRepair(recoveryBounds, trigger);
    }
}
