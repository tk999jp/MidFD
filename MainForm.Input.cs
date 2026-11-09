using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Dialogs;
using MidFD.Services;
using MidFD.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Media;
using MidFD.Models;
using MidFD.Helpers;
using MidFD.Commands;
using MidFD.Services.TrashManifestStore;
using MidFD.Services.Workspace;
using MidFD.Runtime;
namespace MidFD;

public partial class MainForm : Form
{
    private BrowserTabInputTraceFilter? _browserTabInputTraceFilter;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_browserTabInputTraceFilter == null)
        {
            _browserTabInputTraceFilter = new BrowserTabInputTraceFilter(this);
            System.Windows.Forms.Application.AddMessageFilter(_browserTabInputTraceFilter);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_browserTabInputTraceFilter != null)
        {
            System.Windows.Forms.Application.RemoveMessageFilter(_browserTabInputTraceFilter);
            _browserTabInputTraceFilter = null;
        }
        base.OnHandleDestroyed(e);
    }

    internal static bool IsBrowserTabInputTraceMessage(int message, Keys keyCode) =>
        message is 0x0100 or 0x0101 or 0x0104 or 0x0105
        && keyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.Left or Keys.Right;

    private bool CanTraceBrowserTabInput =>
        LogService.IsEnabled && !IsDisposed && !Disposing && IsHandleCreated
        && ReferenceEquals(Form.ActiveForm, this)
        && _viewerApplicationCoordinator?.Mode == ViewerApplicationMode.Browser
        && GetBrowserTabTraceForegroundWindow() == Handle;

    private void TraceBrowserTabInput(string stage, ref Message message, Keys keyData)
    {
        if (!CanTraceBrowserTabInput) return;
        Control? source = Control.FromChildHandle(message.HWnd);
        if (!ReferenceEquals(source?.FindForm(), this)) return;
        IntPtr focusHandle = GetBrowserTabTraceFocus();
        Control? focus = Control.FromChildHandle(focusHandle);
        short ctrlState = GetBrowserTabTraceKeyState((int)Keys.ControlKey);
        LogService.Info(
            $"[BrowserTabInputTrace] stage={stage} pid={Environment.ProcessId} " +
            $"message=0x{message.Msg:X4} vk=0x{((int)keyData & (int)Keys.KeyCode):X2} " +
            $"keyData={InputSettings.ToKeyGestureText(keyData)} keyDataValue=0x{(int)keyData:X} " +
            $"ctrlState=0x{(ushort)ctrlState:X4} ctrlDown={(ctrlState & 0x8000) != 0} " +
            $"modifierKeys=0x{(int)Control.ModifierKeys:X} modifiers=0x{((int)keyData & (int)Keys.Modifiers):X} " +
            $"sourceHwnd=0x{message.HWnd.ToInt64():X} focusHwnd=0x{focusHandle.ToInt64():X} " +
            $"focusedControl={focus?.GetType().Name ?? "-"}/{focus?.Name ?? "-"} " +
            $"activeMain={ReferenceEquals(Form.ActiveForm, this)} mainFocused={Focused || ContainsFocus} " +
            $"browserFocused={BrowserInputRouter.IsBrowserInputFocused(browserPanel)} pathEntry={IsBrowserPathEntryActive()}");
    }

    private sealed class BrowserTabInputTraceFilter(MainForm owner) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message message)
        {
            Keys keyCode = (Keys)(nint)message.WParam & Keys.KeyCode;
            if (IsBrowserTabInputTraceMessage(message.Msg, keyCode))
                owner.TraceBrowserTabInput("raw-message", ref message, keyCode);
            return false;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetFocus")]
    private static extern IntPtr GetBrowserTabTraceFocus();
    [DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short GetBrowserTabTraceKeyState(int virtualKey);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetBrowserTabTraceForegroundWindow();

    private bool TryHandleViewerKeyDown(KeyEventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Viewer) return false;
        if (e.KeyCode == Keys.Escape && e.Modifiers == Keys.None && TryRouteActiveFileOperationCancel("Viewer.KeyDown"))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.CsvTsv && _delimitedGrid?.Visible == true
            && e.KeyCode != Keys.Enter && !(e.KeyCode == Keys.Escape && e.Modifiers == Keys.None))
        {
            return false;
        }
        // Ctrl+C: 表示中行または選択範囲コピー
        if (e.Modifiers == Keys.Control && e.KeyCode == Keys.C)
        {
            if (TryCopyLargeFileVisibleText())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            if (viewerTextBox.Visible && viewerTextBox.SelectionLength > 0)
            {
                DispatchTextViewerCopy("Viewer.KeyDown.CtrlC", TextViewerCopyOrigin.EmbeddedViewer);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            // いずれにも該当しない場合はデフォルトのコピー動作を許容（または無視）するために
            // ここでは return true せず、TextBox 等へイベントを流す可能性を残すことも検討できるが、
            // 現在の契約に従い、ここで Handled にする。
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // Enter / Esc で Browser 復帰
        if (e.Modifiers == Keys.None && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape))
        {
            if (TryExitViewerToBrowser())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
        }
        // L: エンコーディング切替
        if (e.KeyCode == Keys.L && e.Modifiers == Keys.None)
        {
            ExecuteCommandFromUi(CommandIds.TextViewerEncodingCycle, new CommandExecutionContext
            {
                Scope = CommandScope.TextViewer,
                Source = "Viewer.KeyDown.L"
            });
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // W: 折り返し切替
        if (e.KeyCode == Keys.W && e.Modifiers == Keys.None)
        {
            ExecuteCommandFromUi(CommandIds.TextViewerWordWrapToggle, new CommandExecutionContext
            {
                Scope = CommandScope.TextViewer,
                Source = "Viewer.KeyDown.W"
            });
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // ラージファイル用全体ナビゲーション
        if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText && _viewerApplicationCoordinator.LargeFileState != null)
        {
            var state = _viewerApplicationCoordinator.LargeFileState;
            if (TryMapViewerNavigationKey(e.KeyCode, out ViewerInputKey navigationKey))
            {
                TextViewerNavigationTarget target = navigationKey switch
                {
                    ViewerInputKey.Home => TextViewerNavigationTarget.Home,
                    ViewerInputKey.End => TextViewerNavigationTarget.End,
                    ViewerInputKey.PageUp => TextViewerNavigationTarget.PageUp,
                    _ => TextViewerNavigationTarget.PageDown
                };
                ExecuteCommandFromUi(CommandIds.TextViewerNavigate, new CommandExecutionContext
                {
                    Scope = CommandScope.TextViewer,
                    Source = "Viewer.KeyDown.LargeTextNavigation",
                    TextViewerNavigation = new TextViewerNavigationCommandContext(
                        target,
                        _largeFileControl.VisibleLineCount,
                        _largeFileControl.GetMaxFirstVisibleLine())
                });
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
        }
        // ナビゲーションキー等は TextBox 側に通してスクロールを可能にする
        if (IsNavigationOrModifierKey(e.KeyCode))
        {
            return true; // 早期 return (Browser 用 KeyDown 処理へ流さない)
        }
        // それ以外はすべて抑止
        e.Handled = true;
        e.SuppressKeyPress = true;
        return true;
    }

    private bool TryHandleViewerCmdKey(Keys keyData)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Viewer) return false;
        if (keyData == Keys.E)
        {
            DispatchTextViewerExternalEditor("Viewer.ProcessCmdKey.E");
            return true;
        }
        // Ctrl+F / F3 / Shift+F3: Viewer 検索ロジックへのルーティング
        if (keyData == (Keys.Control | Keys.F))
        {
            ExecuteViewerFind();
            return true;
        }
        if (keyData == (Keys.Control | Keys.A))
        {
            if (IsPlainTextBoxViewerKind(_viewerApplicationCoordinator.CurrentKind) && viewerTextBox.Visible)
            {
                viewerTextBox.SelectAll();
                return true;
            }
            if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText && _largeFileControl.Visible)
            {
                _largeFileControl.SelectAll();
                return true;
            }
        }
        if (keyData == Keys.F3)
        {
            ExecuteViewerFindNext(backward: false);
            return true;
        }
        if (keyData == (Keys.Shift | Keys.F3))
        {
            ExecuteViewerFindNext(backward: true);
            return true;
        }
        // Ctrl+C: 表示中コピー
        if (keyData == (Keys.Control | Keys.C))
        {
            if (IsPlainTextBoxViewerKind(_viewerApplicationCoordinator.CurrentKind) && viewerTextBox.Visible)
            {
                if (viewerTextBox.SelectionLength > 0)
                {
                    DispatchTextViewerCopy("Viewer.ProcessCmdKey.CtrlC", TextViewerCopyOrigin.EmbeddedViewer);
                }
                else
                {
                    DispatchTextViewerCopy("Viewer.ProcessCmdKey.CtrlC", TextViewerCopyOrigin.EmbeddedViewer);
                }
                return true;
            }
            if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText)
            {
                DispatchTextViewerCopy("Viewer.ProcessCmdKey.CtrlC", TextViewerCopyOrigin.EmbeddedViewer, largeFile: true);
                return true;
            }
        }
        // Enter / Esc: Browser 復帰
        if (keyData == Keys.Enter || keyData == Keys.Escape)
        {
            if (TryExitViewerToBrowser())
            {
                return true;
            }
        }
        return false;
    }

    private bool DispatchTextViewerCopy(
        string source,
        TextViewerCopyOrigin origin,
        bool largeFile = false,
        bool renderedSelection = false,
        string? selectedText = null,
        string? statusMessage = null)
        => ExecuteCommandFromUi(CommandIds.TextViewerCopy, new CommandExecutionContext
        {
            Scope = CommandScope.TextViewer,
            Source = source,
            TextViewerCopyOrigin = origin,
            TextViewerLargeFileCopy = largeFile,
            TextViewerSelectedText = origin == TextViewerCopyOrigin.ArchivePreview
                ? selectedText
                : selectedText ?? (!largeFile && viewerTextBox.SelectionLength > 0
                    ? viewerTextBox.SelectedText
                    : null),
            TextViewerRenderedSelectionCopy = renderedSelection,
            TextViewerCopyStatusMessage = statusMessage
        });

    private bool DispatchTextViewerExternalEditor(string source)
        => ExecuteCommandFromUi(CommandIds.TextViewerOpenExternalEditor, new CommandExecutionContext
        {
            Scope = CommandScope.TextViewer,
            Source = source,
            TextViewerExternalEditor = new TextViewerExternalEditorCommandContext(
                _viewerApplicationCoordinator.DisplayedPreviewTarget ?? string.Empty,
                _viewerApplicationCoordinator.DisplayedPreviewKind)
        });

    private bool TryHandleBrowserCmdKeyMarking(Keys keyData)
    {
        // Tab を横取りし、コントロール間フォーカス移動を防ぐ (ToggleMark)
        if (keyData == Keys.Tab)
        {
            if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy)) return true;
            _ = ExecuteBrowserMarkToggleCommand(moveNext: false, "Browser.CmdKey.Tab");
            return true;
        }
        // Shift+Home: ファイルのみ反転
        if (keyData == (Keys.Shift | Keys.Home))
        {
            if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy)) return true;
            _ = ExecuteBrowserMarkInvertCommand(includeDirectories: false, "Browser.CmdKey.ShiftHome");
            return true;
        }
        // Shift+End: ファイル + ディレクトリを反転
        if (keyData == (Keys.Shift | Keys.End))
        {
            if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy)) return true;
            _ = ExecuteBrowserMarkInvertCommand(includeDirectories: true, "Browser.CmdKey.ShiftEnd");
            return true;
        }

        return false;
    }

    private bool TryHandleBrowserCmdKeyCustomBindings(Keys keyData)
    {
        return ResolveBrowserCmdKeyCustomBinding(keyData) != BrowserCommandBindingResolver.Resolution.NotMatched;
    }

    private BrowserCommandBindingResolver.Resolution ResolveBrowserCmdKeyCustomBinding(Keys keyData)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
        {
            return BrowserCommandBindingResolver.Resolution.NotMatched;
        }

        Dictionary<string, string> keyMap = ResolveBrowserKeyCommandMap();
        string keyGesture = InputSettings.ToKeyGestureText(keyData);
        bool isBrowserTabShortcut = keyData is (Keys.Control | Keys.Left) or (Keys.Control | Keys.Right);
        if (!keyMap.TryGetValue(keyGesture, out string? commandId))
        {
            if (isBrowserTabShortcut)
            {
                LogService.Info($"[BrowserTabShortcut] stage=binding-result key={keyGesture} command=unbound");
            }
            return BrowserCommandBindingResolver.Resolution.NotMatched;
        }

        if (isBrowserTabShortcut)
        {
            LogService.Info($"[BrowserTabShortcut] stage=binding-result key={keyGesture} command={commandId}");
        }

        if (string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            ShowStatusMessage($"キー割り当て無効: {keyGesture}");
            return BrowserCommandBindingResolver.Resolution.MatchedRejected;
        }

        SelectionResult? selectionSnapshot = null;
        if (string.Equals(commandId, CommandIds.BrowserExecute, StringComparison.OrdinalIgnoreCase))
        {
            var (resolvedCommandId, cursorSnapshot) = ResolveBrowserEnterCommand(GetCurrentBrowserItem());
            commandId = resolvedCommandId;
            selectionSnapshot = cursorSnapshot;
        }

        bool executed;
        int activeTabBefore = _browserApplicationCoordinator.Workspace.ActiveTabIndex;
        if (CommandIds.TryGetBrowserColumnCount(commandId, out int columnCount))
        {
            int numericGestureColumnCount = ResolveBrowserColumnCountShortcut(keyData);
            executed = numericGestureColumnCount == columnCount
                ? ExecuteBrowserColumnCountCommand(columnCount, "Browser.CmdKey.Custom:" + keyGesture)
                : DispatchBrowserColumnCountCommandIdentity(commandId, columnCount, "Browser.CmdKey.Custom:" + keyGesture);
        }
        else
        {
            executed = _integrationSeam?.BrowserKeyCommandOverride is { } commandOverride
                ? commandOverride(commandId)
                : ExecuteCommandFromUi(commandId, _commandRegistry.Find(commandId)?.Scope ?? CommandScope.Browser, "Browser.CmdKey.Custom:" + keyGesture, selectionSnapshot);
        }

        BrowserCommandBindingResolver.Resolution resolution = executed
            ? BrowserCommandBindingResolver.Resolution.MatchedExecuted
            : BrowserCommandBindingResolver.Resolution.MatchedRejected;
        if (isBrowserTabShortcut)
        {
            LogService.Info($"[BrowserTabShortcut] stage=command-result key={keyGesture} command={commandId} executed={executed} " +
                $"tabBefore={activeTabBefore} tabAfter={_browserApplicationCoordinator.Workspace.ActiveTabIndex} " +
                $"tabCount={_browserApplicationCoordinator.Workspace.TabStates.Count}");
        }
        return resolution;
    }

    private Dictionary<string, string> ResolveBrowserKeyCommandMap()
    {
        return BrowserCommandBindingResolver.ResolveEffectiveKeyCommandMap(
            CurrentFunctionKeyProfileValue,
            _settingsCoordinator.Value.Input?.BrowserKeyCommandOverrides,
            _commandRegistry,
            _settingsCoordinator.Value.Input?.CommandLauncherShortcut);
    }

    private bool TryHandleBrowserCmdKeyNavigation(Keys keyData)
    {
        // 履歴移動 (Alt 系) - リストの中身の有無にかかわらず動作
        int total = _browserApplicationCoordinator.TotalItemCount > 0 ? _browserApplicationCoordinator.TotalItemCount : fileListView.Items.Count;
        if (total <= 0) return false;
        int itemsPerPage = GetBrowserItemsPerPage(out _, out int rowsPerColumn);
        bool moved = false;
        if (keyData == Keys.Up)
        {
            _ = ExecuteBrowserCursorMoveCommand((_browserApplicationCoordinator.CursorIndex - 1 + total) % total, "Browser.CmdKey.Up");
            moved = true;
        }
        else if (keyData == Keys.Down)
        {
            _ = ExecuteBrowserCursorMoveCommand((_browserApplicationCoordinator.CursorIndex + 1) % total, "Browser.CmdKey.Down");
            moved = true;
        }
        else if (keyData == Keys.Left)
        {
            _ = ExecuteBrowserCursorMoveCommand(Math.Max(0, _browserApplicationCoordinator.CursorIndex - rowsPerColumn), "Browser.CmdKey.Left");
            moved = true;
        }
        else if (keyData == Keys.Right)
        {
            _ = ExecuteBrowserCursorMoveCommand(Math.Min(total - 1, _browserApplicationCoordinator.CursorIndex + rowsPerColumn), "Browser.CmdKey.Right");
            moved = true;
        }
        else if (keyData == Keys.PageUp)
        {
            if (_browserApplicationCoordinator.CursorIndex - itemsPerPage >= 0)
            {
                _ = ExecuteBrowserCursorMoveCommand(_browserApplicationCoordinator.CursorIndex - itemsPerPage, "Browser.CmdKey.PageUp");
                moved = true;
            }
        }
        else if (keyData == Keys.PageDown)
        {
            if (_browserApplicationCoordinator.CursorIndex + itemsPerPage < total)
            {
                _ = ExecuteBrowserCursorMoveCommand(_browserApplicationCoordinator.CursorIndex + itemsPerPage, "Browser.CmdKey.PageDown");
                moved = true;
            }
        }
        if (moved)
        {
            InvalidateRecentMultiMarkIntent();
            return true;
        }
        return false;
    }

    private bool ExecuteBrowserCursorMoveCommand(int targetIndex, string source) =>
        ExecuteCommandFromUi(CommandIds.BrowserCursorMove, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = source,
            BrowserCursorNavigation = new BrowserCursorNavigationCommandContext(targetIndex)
        });

    private bool ExecuteBrowserMarkToggleCommand(bool moveNext, string source) =>
        ExecuteCommandFromUi(CommandIds.BrowserMarkToggle, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = source,
            BrowserMarkToggle = new BrowserMarkToggleCommandContext(moveNext)
        });

    private bool ExecuteBrowserMarkInvertCommand(bool includeDirectories, string source) =>
        ExecuteCommandFromUi(CommandIds.BrowserMarkInvert, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = source,
            BrowserBulkMark = new BrowserBulkMarkCommandContext(includeDirectories)
        });

    private bool ExecuteBrowserCursorMoveFromImageViewer(Keys keyData)
    {
        int total = _browserApplicationCoordinator.TotalItemCount > 0
            ? _browserApplicationCoordinator.TotalItemCount
            : fileListView.Items.Count;
        if (total <= 0)
        {
            return false;
        }

        int itemsPerPage = GetBrowserItemsPerPage(out _, out int rowsPerColumn);
        int currentIndex = _browserApplicationCoordinator.CursorIndex;
        int? targetIndex = keyData switch
        {
            Keys.Up => (currentIndex - 1 + total) % total,
            Keys.Down => (currentIndex + 1) % total,
            Keys.Left => Math.Max(0, currentIndex - rowsPerColumn),
            Keys.Right => Math.Min(total - 1, currentIndex + rowsPerColumn),
            Keys.PageUp when currentIndex - itemsPerPage >= 0 => currentIndex - itemsPerPage,
            Keys.PageDown when currentIndex + itemsPerPage < total => currentIndex + itemsPerPage,
            _ => null
        };
        return targetIndex is int target && ExecuteCommandFromUi(
            CommandIds.BrowserCursorMove,
            new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "ImageViewer.Navigation",
                BrowserCursorNavigation = new BrowserCursorNavigationCommandContext(target)
            });
    }

    private void ExecuteBrowserMarkToggleFromImageViewer()
    {
        ExecuteCommandFromUi(
            CommandIds.BrowserMarkToggle,
            new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "ImageViewer.Mark",
                BrowserMarkToggle = new BrowserMarkToggleCommandContext(MoveNext: true)
            });
    }

    private bool TryHandleBrowserCmdKeyAliases(Keys keyData)
    {
        if (keyData == (Keys.Shift | Keys.F1)) return ExecuteFunctionKey(1, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F2)) return ExecuteFunctionKey(2, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F3)) return ExecuteFunctionKey(3, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F4)) return ExecuteFunctionKey(4, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F5)) return ExecuteFunctionKey(5, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F6)) return ExecuteFunctionKey(6, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F7)) return ExecuteFunctionKey(7, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F8)) return ExecuteFunctionKey(8, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F9)) return ExecuteFunctionKey(9, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F10)) return ExecuteFunctionKey(10, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F11)) return ExecuteFunctionKey(11, forceShiftLayer: true);
        if (keyData == (Keys.Shift | Keys.F12)) return ExecuteFunctionKey(12, forceShiftLayer: true);
        if (keyData == (Keys.Control | Keys.F1)) return ExecuteFunctionKey(1, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F2)) return ExecuteFunctionKey(2, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F3)) return ExecuteFunctionKey(3, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F4)) return ExecuteFunctionKey(4, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F5)) return ExecuteFunctionKey(5, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F6)) return ExecuteFunctionKey(6, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F7)) return ExecuteFunctionKey(7, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F8)) return ExecuteFunctionKey(8, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F9)) return ExecuteFunctionKey(9, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F10)) return ExecuteFunctionKey(10, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F11)) return ExecuteFunctionKey(11, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Control | Keys.F12)) return ExecuteFunctionKey(12, forcedModifierLayer: Keys.Control);
        if (keyData == (Keys.Alt | Keys.F1)) return ExecuteFunctionKey(1, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F2)) return ExecuteFunctionKey(2, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F3)) return ExecuteFunctionKey(3, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F5)) return ExecuteFunctionKey(5, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F6)) return ExecuteFunctionKey(6, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F7)) return ExecuteFunctionKey(7, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F8)) return ExecuteFunctionKey(8, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F9)) return ExecuteFunctionKey(9, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F10)) return ExecuteFunctionKey(10, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F11)) return ExecuteFunctionKey(11, forcedModifierLayer: Keys.Alt);
        if (keyData == (Keys.Alt | Keys.F12)) return ExecuteFunctionKey(12, forcedModifierLayer: Keys.Alt);

        if (keyData == Keys.F1) return ExecuteFunctionKey(1);
        if (keyData == Keys.F2) return ExecuteFunctionKey(2);
        if (keyData == Keys.F3) return ExecuteFunctionKey(3);
        if (keyData == Keys.F4) return ExecuteFunctionKey(4);
        if (keyData == Keys.F5) return ExecuteFunctionKey(5);
        if (keyData == Keys.F6) return ExecuteFunctionKey(6);
        if (keyData == Keys.F7) return ExecuteFunctionKey(7);
        if (keyData == Keys.F8) return ExecuteFunctionKey(8);
        if (keyData == Keys.F9) return ExecuteFunctionKey(9);
        if (keyData == Keys.F10) return ExecuteFunctionKey(10);
        if (keyData == Keys.F11) return ExecuteFunctionKey(11);
        if (keyData == Keys.F12) return ExecuteFunctionKey(12);
        return false;
    }

    private bool TryHandleBrowserCmdKeyLaunch(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter))
        {
            var item = GetCurrentBrowserItem();
            if (item != null && item.Text != "..")
            {
                string? fullPath = item.Tag as string;
                if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                {
                    BrowserCtrlEnterRoute route = _viewerWorkflowApplicationCoordinator.ResolveBrowserCtrlEnter(fullPath);
                    if (route == BrowserCtrlEnterRoute.OpenVideoPlaybackLocked)
                    {
                        return ExecuteCommandFromUi(CommandIds.BrowserOpenPlaybackLocked, new CommandExecutionContext
                        {
                            Scope = CommandScope.Browser,
                            Source = "Browser.CmdKey.CtrlEnter.PlaybackLocked",
                            ContextTargetPath = fullPath,
                            SelectionSnapshot = new SelectionResult([fullPath], hasMarkedSelection: false)
                        });
                    }
                    if (route == BrowserCtrlEnterRoute.PlayAudioExternally)
                    {
                        return ExecuteCommandFromUi(CommandIds.BrowserAudioCtrlEnter, new CommandExecutionContext
                        {
                            Scope = CommandScope.Browser,
                            Source = "Browser.CmdKey.CtrlEnter.Audio",
                            ContextTargetPath = fullPath
                        });
                    }
                }
            }
            return false;
        }
        return false;
    }

    private bool TryHandleBrowserCmdKeyClipboard(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C))
        {
            SelectionResult selectionSnapshot = ResolveSelection();
            return ExecuteCommandFromUi(
                CommandIds.BrowserClipboardCopy,
                CommandScope.Browser,
                "Browser.CmdKey.CtrlC",
                selectionSnapshot);
        }
        if (keyData == (Keys.Control | Keys.V))
        {
            return ExecuteCommandFromUi(CommandIds.ClipboardPaste, CommandScope.Browser, "Browser.CmdKey.CtrlV");
        }
        return false;
    }

    private bool TryHandleBrowserCmdKeyColumnCount(Keys keyData)
    {
        // FDCompatibleではCtrl+1-9とCtrl+NumPad1-9をDrive A-I commandへ渡す。
        if (ShouldHandleBrowserFileDisplayModeShortcut(CurrentFunctionKeyProfileValue, keyData) &&
            TryGetBrowserFileDisplayModeShortcut(keyData, out BrowserFileDisplayMode shortcutMode))
        {
            return ExecuteBrowserDisplayModeFromUi(shortcutMode, "Browser.CmdKey.DisplayMode");
        }

        // 通常の数字キー / NumPadキーによる列数選択
        int val = ResolveBrowserColumnCountShortcut(keyData);

        if (val > 0)
        {
            return ExecuteBrowserColumnCountCommand(val, "Browser.CmdKey.ColumnCount");
        }
        return false;
    }

    private bool ExecuteBrowserColumnCountCommand(int columnCount, string source)
    {
        (string commandId, BrowserViewCommandContext viewContext) = ResolveBrowserColumnCountInputCommand(
            CurrentFunctionKeyProfileValue,
            columnCount,
            _browserApplicationCoordinator.ColumnCount,
            _lastColumnCountKey,
            GetBrowserFileDisplayMode());
        string commandSource = commandId == CommandIds.BrowserViewDisplayModeSet
            ? source + ".DisplayModeCycle"
            : source;
        bool executed = ExecuteCommandFromUi(commandId, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = commandSource,
            BrowserView = viewContext
        });
        if (executed)
        {
            _lastColumnCountKey = columnCount;
        }

        return executed;
    }

    private bool DispatchBrowserColumnCountCommandIdentity(string commandId, int columnCount, string source)
    {
        return ExecuteCommandFromUi(commandId, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = source,
            BrowserView = new BrowserViewCommandContext(ColumnCount: columnCount)
        });
    }

    internal static (string CommandId, BrowserViewCommandContext Context) ResolveBrowserColumnCountInputCommand(
        string? profileValue,
        int requestedColumnCount,
        int currentColumnCount,
        int previousColumnCountKey,
        BrowserFileDisplayMode currentDisplayMode)
    {
        bool isRepeatedFdColumnInput =
            FunctionKeyProfileService.ResolveProfile(profileValue) == FunctionKeyProfile.FDCompatible &&
            requestedColumnCount == previousColumnCountKey &&
            requestedColumnCount == currentColumnCount;
        if (isRepeatedFdColumnInput)
        {
            return (
                CommandIds.BrowserViewDisplayModeSet,
                new BrowserViewCommandContext(DisplayMode: GetNextBrowserFileDisplayModeInCycle(currentDisplayMode)));
        }

        return (
            CommandIds.GetBrowserColumnCountCommandId(requestedColumnCount),
            new BrowserViewCommandContext(ColumnCount: requestedColumnCount));
    }

    internal static int ResolveBrowserColumnCountShortcut(Keys keyData)
    {
        if (keyData >= Keys.D1 && keyData <= Keys.D9)
        {
            return (int)(keyData - Keys.D0);
        }
        if (keyData >= Keys.NumPad1 && keyData <= Keys.NumPad9)
        {
            return (int)(keyData - Keys.NumPad0);
        }
        return 0;
    }

    internal static bool TryGetBrowserFileDisplayModeShortcut(
        Keys keyData,
        out BrowserFileDisplayMode mode)
    {
        mode = keyData switch
        {
            (Keys.Control | Keys.D1) or (Keys.Control | Keys.NumPad1) => BrowserFileDisplayMode.NameOnly,
            (Keys.Control | Keys.D2) or (Keys.Control | Keys.NumPad2) => BrowserFileDisplayMode.NameSize,
            (Keys.Control | Keys.D3) or (Keys.Control | Keys.NumPad3) => BrowserFileDisplayMode.NameSizeDate,
            (Keys.Control | Keys.D4) or (Keys.Control | Keys.NumPad4) => BrowserFileDisplayMode.NameExtensionAligned,
            _ => (BrowserFileDisplayMode)(-1)
        };
        return Enum.IsDefined(mode);
    }

    internal static string ResolveBrowserFileDisplayModeShortcutHint(
        string? profileValue,
        BrowserFileDisplayMode mode,
        IReadOnlyDictionary<string, string> effectiveBindings)
    {
        int digit = mode switch
        {
            BrowserFileDisplayMode.NameOnly => 1,
            BrowserFileDisplayMode.NameSize => 2,
            BrowserFileDisplayMode.NameSizeDate => 3,
            BrowserFileDisplayMode.NameExtensionAligned => 4,
            _ => 0
        };
        if (digit == 0)
        {
            return string.Empty;
        }

        (Keys Key, string Label)[] candidates =
        [
            (Keys.Control | (Keys)((int)Keys.D1 + digit - 1), $"Ctrl+{digit}"),
            (Keys.Control | (Keys)((int)Keys.NumPad1 + digit - 1), $"Ctrl+NumPad{digit}")
        ];
        foreach ((Keys key, string label) in candidates)
        {
            if (!ShouldHandleBrowserFileDisplayModeShortcut(profileValue, key) ||
                !TryGetBrowserFileDisplayModeShortcut(key, out BrowserFileDisplayMode resolvedMode) ||
                resolvedMode != mode)
            {
                continue;
            }

            string gesture = InputSettings.ToKeyGestureText(key);
            if (!effectiveBindings.ContainsKey(gesture))
            {
                return label;
            }
        }

        return string.Empty;
    }

    internal static bool ShouldHandleBrowserFileDisplayModeShortcut(string? profileValue, Keys keyData)
    {
        Keys modifiers = keyData & Keys.Modifiers;
        Keys keyCode = keyData & Keys.KeyCode;
        bool isFdCompatible = FunctionKeyProfileService.ResolveProfile(profileValue) == FunctionKeyProfile.FDCompatible;
        bool isCtrlDriveDigit = modifiers == Keys.Control &&
            ((keyCode >= Keys.D1 && keyCode <= Keys.D9) ||
             (keyCode >= Keys.NumPad1 && keyCode <= Keys.NumPad9));
        return !isFdCompatible || !isCtrlDriveDigit;
    }

    internal static BrowserFileDisplayMode GetNextBrowserFileDisplayModeInCycle(BrowserFileDisplayMode mode) => mode switch
    {
        BrowserFileDisplayMode.NameOnly => BrowserFileDisplayMode.NameSize,
        BrowserFileDisplayMode.NameSize => BrowserFileDisplayMode.NameSizeDate,
        BrowserFileDisplayMode.NameSizeDate => BrowserFileDisplayMode.NameExtensionAligned,
        _ => BrowserFileDisplayMode.NameOnly
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys modifiers = keyData & Keys.Modifiers;
        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode is Keys.Left or Keys.Right)
            TraceBrowserTabInput("process-cmd-key", ref msg, keyData);
        bool isBrowserTabShortcut = keyData is (Keys.Control | Keys.Left) or (Keys.Control | Keys.Right);
        if (isBrowserTabShortcut)
        {
            Control? focused = ActiveControl;
            while (focused?.Controls.Cast<Control>().FirstOrDefault(control => control.Focused || control.ContainsFocus) is { } child)
            {
                focused = child;
            }
            LogService.Info(
                $"[BrowserTabShortcut] stage=cmdkey-received key={InputSettings.ToKeyGestureText(keyData)} " +
                $"mode={_viewerApplicationCoordinator.Mode} browserFocused={BrowserInputRouter.IsBrowserInputFocused(browserPanel)} " +
                $"pathEntry={IsBrowserPathEntryActive()} focusedControl={focused?.GetType().Name ?? "-"}/{focused?.Name ?? "-"} " +
                $"activeMain={ReferenceEquals(Form.ActiveForm, this)} sourceHwnd=0x{msg.HWnd.ToInt64():X} " +
                $"profile={CurrentFunctionKeyProfileValue}");
        }
        bool isPlainNumberKey = (modifiers == Keys.None) &&
                                ((keyCode >= Keys.D1 && keyCode <= Keys.D9) ||
                                 (keyCode >= Keys.NumPad1 && keyCode <= Keys.NumPad9));
        if (!isPlainNumberKey)
        {
            _lastColumnCountKey = 0;
        }

        if (IsBrowserPathEntryActive())
        {
            if (isBrowserTabShortcut)
            {
                LogService.Info("[BrowserTabShortcut] stage=router-bypass reason=path-entry");
            }
            ClearBrowserNamePrefixJump();
            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer && keyData == Keys.E)
        {
            DispatchTextViewerExternalEditor("Viewer.ProcessCmdKey.E");
            return true;
        }

        if (keyData == Keys.Escape && TryRouteActiveFileOperationCancel("MainForm.ProcessCmdKey"))
        {
            return true;
        }

        if (_browserNamePrefixJumpSession.IsActive)
        {
            if (keyCode == Keys.Escape && modifiers == Keys.None)
            {
                ClearBrowserNamePrefixJump();
                ShowStatusMessage("頭文字ジャンプを終了しました。");
                return true;
            }
            if (keyCode == Keys.Escape)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (keyCode == Keys.Back)
            {
                if (_browserNamePrefixJumpSession.Backspace())
                {
                    UpdateBrowserNamePrefixJumpCursor();
                }
                else
                {
                    ShowBrowserNamePrefixJumpStatus();
                }
                return true;
            }
            if (keyCode == Keys.Enter)
            {
                ClearBrowserNamePrefixJump();
                ShowStatusMessage("頭文字ジャンプを確定しました。");
                return true;
            }
            if ((modifiers & (Keys.Control | Keys.Alt)) != Keys.None)
            {
                ClearBrowserNamePrefixJump();
            }
            else if (IsBrowserNamePrefixJumpTextKey(keyCode))
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            else
            {
                ClearBrowserNamePrefixJump();
            }
        }

        if (keyData == Keys.Escape && TryCloseImageViewersFromMainEsc("MainForm.ProcessCmdKey"))
        {
            return true;
        }
        if (keyData == Keys.Escape && ApplyNameSearchInspectionReturn())
        {
            return true;
        }
        if (keyData is Keys.Enter or Keys.Space
            && _browserTabNavigation?.ContainsFocus == true
            && _browserTabNavigation.ActivateSelectedNode())
        {
            return true;
        }
        if (_unifiedSearchSession is { IsActive: true } search)
        {
            if (keyData == (Keys.Control | Keys.Shift | Keys.O))
            {
                search.View.ExportResults();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Enter))
            {
                search.View.ActivateSelected(UnifiedSearchResultActivationOrigin.CtrlEnter);
                return true;
            }
            if (keyData == Keys.F7)
            {
                ReopenUnifiedSearchDialog(search, "Browser.SearchResults.F7");
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                if (search.View.HasResults) search.View.FocusResultFilter();
                else if (search.IsFinished) ReopenUnifiedSearchDialog(search, "Browser.SearchResults.CtrlF");
                return true;
            }
            if (modifiers == Keys.None && keyCode >= Keys.F1 && keyCode <= Keys.F12)
            {
                int slot = (int)keyCode - (int)Keys.F1;
                if (slot <= 3) HandleFuncKeyClick(slot);
                return true;
            }
            if (search.View.ResultFilterFocused)
            {
                if (modifiers == Keys.None && search.View.HandleResultKey(keyCode)) return true;
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (modifiers == Keys.None && search.View.HandleResultKey(keyCode)) return true;
        }
        if (TryHandleCommandHintOverlayCmdKey(keyData))
        {
            return true;
        }
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer
            && TryHandleViewerCmdKey(keyData)) return true;
        bool browserInputHandled = _browserInputRouter.TryHandleCmdKey(CreateBrowserCmdKeyContext(), keyData);
        if (isBrowserTabShortcut)
        {
            LogService.Info($"[BrowserTabShortcut] stage=router-result key={InputSettings.ToKeyGestureText(keyData)} handled={browserInputHandled}");
        }
        if (browserInputHandled) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OpenMenuStripFromKeyboard()
    {
        LogAltHintContext("OpenMenuStripFromKeyboard");
        _isOpeningMenuStripExplicitly = true;
        HideCommandHintOverlay();
        _isAltHintHeld = false;
        _isExternalToolAltPopupAltOwned = false;
        UpdateMenuStripState();
        if (mainMenuStrip.Items.Count == 0)
        {
            _isOpeningMenuStripExplicitly = false;
            return;
        }
        mainMenuStrip.Focus();
        if (mainMenuStrip.Items[0] is ToolStripMenuItem rootItem)
        {
            rootItem.Select();
            rootItem.ShowDropDown();
        }
    }

    private void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Shift)
        {
            UpdateFunctionBarShiftLayerState(true);
        }
        if (e.Control)
        {
            UpdateFunctionBarCtrlLayerState(true);
        }
        if (e.Alt || e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu)
        {
            UpdateFunctionBarAltLayerState(true);
        }
        if (e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu || (e.Control && e.Alt))
        {
            LogAltHint($"MainForm_KeyDown Key={e.KeyCode} Alt={e.Alt} Ctrl={e.Control} OverlayVisible={IsCommandHintOverlayVisible()}");
        }
        if (IsBrowserPathEntryActive())
        {
            ClearBrowserNamePrefixJump();
            return;
        }
        if (_unifiedSearchSession is { IsActive: true } search && search.View.ResultFilterFocused)
        {
            // KeyPreview must leave the filter's input with its focused control.
            return;
        }
        if (e.KeyData == Keys.Escape && TryRouteActiveFileOperationCancel("MainForm.KeyDown"))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (_browserNamePrefixJumpSession.IsActive && TryHandleBrowserNamePrefixJumpKeyDown(e))
        {
            return;
        }
        if (e.KeyData == Keys.Escape && _browserRightInteractionState != BrowserRightInteractionState.Idle)
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyData == Keys.Escape && TryCloseImageViewersFromMainEsc("MainForm.KeyDown"))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        bool isAltOnlyKey =
            (e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu) &&
            !e.Control;
        if (isAltOnlyKey && CanShowCommandHintOverlay())
        {
            _isExternalToolAltPopupAltOwned = true;
            _isAltHintHeld = true;
            ShowCommandHintOverlay();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (TryHandleCommandHintOverlayKeyDown(e)) return;
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer
            && TryHandleViewerKeyDown(e)) return;
        if (_browserInputRouter.TryHandleKeyDown(CreateBrowserKeyDownContext(), e)) return;
    }

    private void MainForm_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser ||
            IsBrowserPathEntryActive() ||
            !BrowserInputRouter.IsBrowserInputFocused(browserPanel) ||
            (ModifierKeys & (Keys.Control | Keys.Alt)) != Keys.None)
        {
            return;
        }

        if (TryHandleBrowserNamePrefixJumpCharacter(e.KeyChar))
        {
            e.Handled = true;
        }
    }

    private bool TryHandleBrowserNamePrefixJumpCharacter(char value)
    {
        if (_browserNamePrefixJumpSession.IsActive)
        {
            if (IsCurrentDirectoryBusy())
            {
                ClearBrowserNamePrefixJump();
                return true;
            }
            if (char.IsControl(value))
            {
                return false;
            }
            UpdateBrowserNamePrefixJumpCharacter(value);
            return true;
        }

        if (value != '@')
        {
            return false;
        }

        _ = ExecuteCommandFromUi(
            CommandIds.BrowserNamePrefixJump,
            CommandScope.Browser,
            "Browser.KeyPress.NamePrefixJump");
        return true;
    }

    private bool TryHandleBrowserNamePrefixJumpKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && e.Modifiers == Keys.None)
        {
            ClearBrowserNamePrefixJump();
            ShowStatusMessage("頭文字ジャンプを終了しました。");
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.Back)
        {
            if (_browserNamePrefixJumpSession.Backspace())
            {
                UpdateBrowserNamePrefixJumpCursor();
            }
            else
            {
                ShowBrowserNamePrefixJumpStatus();
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.KeyCode == Keys.Enter)
        {
            ClearBrowserNamePrefixJump();
            ShowStatusMessage("頭文字ジャンプを確定しました。");
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        if (e.Control || e.Alt)
        {
            ClearBrowserNamePrefixJump();
            return false;
        }
        if (IsBrowserNamePrefixJumpTextKey(e.KeyCode))
        {
            // Keep KeyPress available: the translated character, not the physical OEM key, is the prefix input.
            return true;
        }

        ClearBrowserNamePrefixJump();
        return false;
    }

    private static bool IsBrowserNamePrefixJumpTextKey(Keys keyCode)
    {
        keyCode &= Keys.KeyCode;
        return (keyCode >= Keys.A && keyCode <= Keys.Z) ||
            (keyCode >= Keys.D0 && keyCode <= Keys.D9) ||
            (keyCode >= Keys.NumPad0 && keyCode <= Keys.NumPad9) ||
            keyCode is Keys.Space or Keys.Add or Keys.Subtract or Keys.Multiply or Keys.Divide or Keys.Decimal or
                Keys.OemSemicolon or Keys.Oemplus or Keys.Oemcomma or Keys.OemMinus or Keys.OemPeriod or
                Keys.OemQuestion or Keys.Oemtilde or Keys.OemOpenBrackets or Keys.OemPipe or
                Keys.OemCloseBrackets or Keys.OemQuotes or Keys.OemBackslash or Keys.Oem102 or
                Keys.ProcessKey or Keys.Packet;
    }

    private static bool TryMapViewerNavigationKey(Keys keyCode, out ViewerInputKey key)
    {
        key = keyCode switch
        {
            Keys.Home => ViewerInputKey.Home,
            Keys.End => ViewerInputKey.End,
            Keys.PageUp => ViewerInputKey.PageUp,
            Keys.PageDown => ViewerInputKey.PageDown,
            _ => default
        };
        return keyCode is Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown;
    }

    private BrowserInputRouter.CmdKeyContext CreateBrowserCmdKeyContext()
    {
        return new BrowserInputRouter.CmdKeyContext
        {
            IsBrowserMode = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            IsBrowserFocused = BrowserInputRouter.IsBrowserInputFocused(browserPanel),
            IsAuxPreviewActive = false,
            CanUseCommandLauncherCommands = CanUseCommandLauncherCommands(),
            TryHandleCustomBindings = ResolveBrowserCmdKeyCustomBinding,
            OpenMenuStripFromKeyboard = OpenMenuStripFromKeyboard,
            TryHandleNavigation = TryHandleBrowserCmdKeyNavigation,
            TryHandleFileOperationUndoRedo = TryHandleBrowserCmdKeyFileOperationUndoRedo,
            TryHandleMarking = TryHandleBrowserCmdKeyMarking,
            TryHandleClipboard = TryHandleBrowserCmdKeyClipboard,
            TryHandleColumnCount = TryHandleBrowserCmdKeyColumnCount,
            TryHandleAliases = TryHandleBrowserCmdKeyAliases,
            TryHandleLaunch = TryHandleBrowserCmdKeyLaunch,
            TryHandleCommandLauncher = TryHandleBrowserCmdKeyExternalToolAltSlot
        };
    }

    private BrowserInputRouter.KeyDownContext CreateBrowserKeyDownContext()
    {
        return new BrowserInputRouter.KeyDownContext
        {
            IsBrowserMode = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            IsBrowserFocused = BrowserInputRouter.IsBrowserInputFocused(browserPanel),
            TryHandleCore = TryHandleBrowserKeyDown
        };
    }

    private void MainForm_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.LShiftKey || e.KeyCode == Keys.RShiftKey)
        {
            UpdateFunctionBarShiftLayerState(false);
        }
        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey)
        {
            UpdateFunctionBarCtrlLayerState(false);
        }
        if (e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu)
        {
            UpdateFunctionBarAltLayerState(false);
        }
        if (e.KeyCode == Keys.Menu || e.KeyCode == Keys.LMenu || e.KeyCode == Keys.RMenu || e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey)
        {
            LogAltHint($"MainForm_KeyUp Key={e.KeyCode} AltHeld={_isAltHintHeld} OverlayVisible={IsCommandHintOverlayVisible()}");
        }
        bool isAltKey =
            e.KeyCode == Keys.Menu ||
            e.KeyCode == Keys.LMenu ||
            e.KeyCode == Keys.RMenu;
        if (isAltKey)
        {
            _isAltHintHeld = false;
            _isExternalToolAltPopupAltOwned = false;
            HideCommandHintOverlay("MainForm_KeyUp:AltReleased");
        }
    }

    private bool TryHandleBrowserCmdKeyExternalToolAltSlot(Keys keyData)
    {
        if (!TryResolveExternalToolByAltSlot(keyData, out ExternalToolCommandDefinition? tool, out string slotLabel))
        {
            return false;
        }
        if (GuardMutationBusy())
        {
            return true;
        }
        LogAltHint($"TryHandleBrowserCmdKeyExternalToolAltSlot Slot={slotLabel} Tool={tool!.Id}");
        HideCommandHintOverlay("TryHandleBrowserCmdKeyExternalToolAltSlot");
        LaunchExternalTool(tool!);
        return true;
    }

    private bool TryHandleBrowserCmdKeyFileOperationUndoRedo(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z))
        {
            if (GuardMutationBusy()) return true;
            return ExecuteCommandFromUi(CommandIds.EditUndo, CommandScope.Browser, "Browser.CmdKey.CtrlZ");
        }
        if (keyData == (Keys.Control | Keys.Y))
        {
            if (GuardMutationBusy()) return true;
            return ExecuteCommandFromUi(CommandIds.EditRedo, CommandScope.Browser, "Browser.CmdKey.CtrlY");
        }
        if (keyData == (Keys.Alt | Keys.Z))
        {
            if (GuardMutationBusy()) return true;
            return ExecuteCommandFromUi(CommandIds.EditUndo, CommandScope.Browser, "Browser.CmdKey.AltZ");
        }
        if (keyData == (Keys.Alt | Keys.Y))
        {
            if (GuardMutationBusy()) return true;
            return ExecuteCommandFromUi(CommandIds.EditRedo, CommandScope.Browser, "Browser.CmdKey.AltY");
        }
        return false;
    }

}
