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

    private bool TryHandleViewerKeyDown(KeyEventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Viewer) return false;
        if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.CsvTsv && _delimitedGrid?.Visible == true
            && e.KeyCode != Keys.Enter && e.KeyCode != Keys.Escape)
        {
            return false;
        }
        // Ctrl+C: 表示中行または選択範囲コピー
        if (e.Control && e.KeyCode == Keys.C)
        {
            if (TryCopyLargeFileVisibleText())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
            if (viewerTextBox.Visible && viewerTextBox.SelectionLength > 0)
            {
                viewerTextBox.Copy();
                ShowStatusMessage("選択範囲をコピーしました。");
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
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
        {
            if (TryExitViewerToBrowser())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return true;
            }
        }
        // L: エンコーディング切替
        if (e.KeyCode == Keys.L)
        {
            _viewerModeApplicationCoordinator.CycleEncodingAndRefresh(this);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // W: 折り返し切替
        if (e.KeyCode == Keys.W)
        {
            viewerTextBox.WordWrap = _viewerWorkflowApplicationCoordinator.ToggleWordWrap();
            viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? ScrollBars.Vertical : ScrollBars.Both;
            ApplyViewerStatusLine();
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
                ViewerNavigationDecision decision = _viewerWorkflowApplicationCoordinator.ResolveLargeFileNavigation(
                    state,
                    navigationKey,
                    _largeFileControl.VisibleLineCount,
                    _largeFileControl.GetMaxFirstVisibleLine());
                if (decision.PendingEndAfterIndex)
                {
                    ShowStatusMessage("インデックス完了後に末尾へ移動します...");
                }
                else if (decision.Handled)
                {
                    _ = NavigateLargeFilePreviewAsync(decision.TargetFirstVisibleLine, e.KeyCode.ToString());
                }
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
                    viewerTextBox.Copy();
                    ShowStatusMessage("選択範囲をコピーしました。");
                }
                return true;
            }
            if (_viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText)
            {
                _ = TryCopyLargeFileVisibleTextAsync();
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

    private bool TryHandleBrowserCmdKeyMarking(Keys keyData)
    {
        // Tab を横取りし、コントロール間フォーカス移動を防ぐ (ToggleMark)
        if (keyData == Keys.Tab)
        {
            if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy)) return true;
            ToggleMark(moveNext: false);
            return true;
        }
        // Shift+Home: ファイルのみ反転
        if (keyData == (Keys.Shift | Keys.Home))
        {
            if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy)) return true;
            InvertBulkMarks(includeDirectories: false);
            return true;
        }
        // Shift+End: ファイル + ディレクトリを反転
        if (keyData == (Keys.Shift | Keys.End))
        {
            if (!CommandBusyPolicy.CanMutateBrowserState(_fileOperationApplicationCoordinator.IsBusy)) return true;
            InvertBulkMarks(includeDirectories: true);
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
        if (!keyMap.TryGetValue(keyGesture, out string? commandId))
        {
            return BrowserCommandBindingResolver.Resolution.NotMatched;
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

        bool executed = _integrationSeam?.BrowserKeyCommandOverride is { } commandOverride
            ? commandOverride(commandId)
            : ExecuteCommandFromUi(commandId, _commandRegistry.Find(commandId)?.Scope ?? CommandScope.Browser, "Browser.CmdKey.Custom:" + keyGesture, selectionSnapshot);
        return executed
            ? BrowserCommandBindingResolver.Resolution.MatchedExecuted
            : BrowserCommandBindingResolver.Resolution.MatchedRejected;
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
            SetBrowserGlobalCursorIndex((_browserApplicationCoordinator.CursorIndex - 1 + total) % total);
            moved = true;
        }
        else if (keyData == Keys.Down)
        {
            SetBrowserGlobalCursorIndex((_browserApplicationCoordinator.CursorIndex + 1) % total);
            moved = true;
        }
        else if (keyData == Keys.Left)
        {
            SetBrowserGlobalCursorIndex(Math.Max(0, _browserApplicationCoordinator.CursorIndex - rowsPerColumn));
            moved = true;
        }
        else if (keyData == Keys.Right)
        {
            SetBrowserGlobalCursorIndex(Math.Min(total - 1, _browserApplicationCoordinator.CursorIndex + rowsPerColumn));
            moved = true;
        }
        else if (keyData == Keys.F11)
        {
            return ExecuteFunctionKey(11);
        }
        else if (keyData == Keys.F12)
        {
            return ExecuteFunctionKey(12);
        }
        else if (keyData == Keys.PageUp)
        {
            if (_browserApplicationCoordinator.CursorIndex - itemsPerPage >= 0)
            {
                SetBrowserGlobalCursorIndex(_browserApplicationCoordinator.CursorIndex - itemsPerPage);
                moved = true;
            }
        }
        else if (keyData == Keys.PageDown)
        {
            if (_browserApplicationCoordinator.CursorIndex + itemsPerPage < total)
            {
                SetBrowserGlobalCursorIndex(_browserApplicationCoordinator.CursorIndex + itemsPerPage);
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
                    ViewerCtrlEnterDecision decision = _viewerWorkflowApplicationCoordinator.ResolveCtrlEnter(fullPath);
                    if (decision.Handled)
                    {
                        if (decision.UseExternalMediaPlayback)
                        {
                            LaunchMediaPlayback(fullPath, decision.IsAudio);
                        }
                        else
                        {
                            ExecuteBrowserOpenRequest(decision.OpenRequest);
                        }
                        return true;
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
        // Ctrl+1/2/3/4 および Ctrl+NumPad1/2/3/4 による明示的な表示モード切替
        if (TryGetBrowserFileDisplayModeShortcut(keyData, out BrowserFileDisplayMode shortcutMode))
        {
            SetBrowserFileDetailDisplayMode(shortcutMode);
            return true;
        }

        // 通常の数字キー / NumPadキーによる列数選択
        int val = 0;
        if (keyData >= Keys.D1 && keyData <= Keys.D9) val = (int)(keyData - Keys.D0);
        else if (keyData >= Keys.NumPad1 && keyData <= Keys.NumPad9) val = (int)(keyData - Keys.NumPad0);

        if (val > 0)
        {
            bool isWinFD = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) == FunctionKeyProfile.FDCompatible;
            bool isRepeat = (val == _lastColumnCountKey);
            _lastColumnCountKey = val;
            bool columnChanged = _browserApplicationCoordinator.ColumnCount != val;

            if (columnChanged)
            {
                int previousItemsPerPage = GetBrowserItemsPerPage();
                SetBrowserFileDetailDisplayMode(
                    BrowserFileDisplayMode.NameOnly,
                    persistTabState: false,
                    rematerialize: false);
                int nextItemsPerPage = GetBrowserItemsPerPageForColumn(val);
                BrowserColumnCountExecution execution = _browserNavigationWorkflowApplicationCoordinator.ExecuteColumnCountChange(
                    val,
                    nextItemsPerPage != previousItemsPerPage,
                    BuildBrowserTabStateFromCurrentUi(),
                    CreateDirectoryLoadOptions(itemsPerPage: nextItemsPerPage),
                    val,
                    CaptureBrowserRefreshShellState());
                if (execution.Load is { Succeeded: true } load)
                {
                    ApplyDirectoryLoadUi(load);
                    ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
                }
            }
            else if (isRepeat && isWinFD)
            {
                BrowserFileDisplayMode currentMode = GetBrowserFileDisplayMode();
                BrowserFileDisplayMode nextMode = GetNextBrowserFileDisplayModeInCycle(currentMode);
                SetBrowserFileDetailDisplayMode(nextMode);
            }

            if (!columnChanged)
            {
                _browserNavigationWorkflowApplicationCoordinator.PersistCurrentListState();
                RematerializeBrowserPageIfCapacityChanged();
                CaptureActiveBrowserTabState();
            }
            UpdateInfoPanel();
            browserPanel.Invalidate();
            return true;
        }
        return false;
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
        bool isPlainNumberKey = (modifiers == Keys.None) &&
                                ((keyCode >= Keys.D1 && keyCode <= Keys.D9) ||
                                 (keyCode >= Keys.NumPad1 && keyCode <= Keys.NumPad9));
        if (!isPlainNumberKey)
        {
            _lastColumnCountKey = 0;
        }

        if (IsBrowserPathEntryActive())
        {
            ClearBrowserNamePrefixJump();
            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (_browserNamePrefixJumpSession.IsActive)
        {
            if (keyCode == Keys.Escape)
            {
                ClearBrowserNamePrefixJump();
                ShowStatusMessage("頭文字ジャンプを終了しました。");
                return true;
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

        if (keyCode == Keys.Escape)
        {
            LogService.Info(
                $"[CancelRuntime] MainForm.ProcessCmdKey Escape. busy={_fileOperationApplicationCoordinator.IsClipboardBusy}, " +
                $"hasCts={_fileOperationApplicationCoordinator.CancellationTokenSource != null}, requested={_fileOperationApplicationCoordinator.CancellationTokenSource?.IsCancellationRequested ?? false}, " +
                $"activeControl={DescribeControl(ActiveControl)}, thread={Environment.CurrentManagedThreadId}");
        }
        if (keyCode == Keys.Escape && TryRouteActiveFileOperationCancel("MainForm.ProcessCmdKey"))
        {
            return true;
        }
        if (keyCode == Keys.Escape && TryCloseImageViewersFromMainEsc("MainForm.ProcessCmdKey"))
        {
            return true;
        }
        if (keyCode == Keys.Escape && TryReturnToNameSearchResultsAfterInspection())
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
            if (keyData == (Keys.Control | Keys.Enter))
            {
                search.View.ActivateSelected(inNewTab: true);
                return true;
            }
            if (keyData == Keys.F7)
            {
                ReopenUnifiedSearchDialog(search);
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                if (search.View.HasResults) search.View.FocusResultFilter();
                else if (search.IsFinished) ReopenUnifiedSearchDialog(search);
                return true;
            }
            if (modifiers == Keys.None && keyCode >= Keys.F1 && keyCode <= Keys.F12)
            {
                int slot = (int)keyCode - (int)Keys.F1;
                if (slot <= 2) HandleFuncKeyClick(slot);
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
        if (_browserInputRouter.TryHandleCmdKey(CreateBrowserCmdKeyContext(), keyData)) return true;
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
        if (_browserNamePrefixJumpSession.IsActive && TryHandleBrowserNamePrefixJumpKeyDown(e))
        {
            return;
        }
        if (e.KeyCode == Keys.Escape && _browserRightInteractionState != BrowserRightInteractionState.Idle)
        {
            CleanupBrowserRightInteraction(clearContextMenuSuppression: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape && TryRouteActiveFileOperationCancel("MainForm.KeyDown"))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        if (e.KeyCode == Keys.Escape && TryCloseImageViewersFromMainEsc("MainForm.KeyDown"))
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
        if (e.KeyCode == Keys.Escape)
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
            TryHandleTabs = TryHandleBrowserCmdKeyTabs,
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

    private bool TryHandleBrowserCmdKeyTabs(Keys keyData)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
        {
            return false;
        }
        return false;
    }
}
