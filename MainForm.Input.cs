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
namespace MidFD;

public partial class MainForm : Form
{

    private bool TryHandleViewerKeyDown(KeyEventArgs e)
    {
        if (_uiMode != UIMode.Viewer) return false;
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
            if (_viewerEncodingOverride == ViewerEncoding.Auto) _viewerEncodingOverride = ViewerEncoding.UTF8;
            else if (_viewerEncodingOverride == ViewerEncoding.UTF8) _viewerEncodingOverride = ViewerEncoding.SJIS;
            else _viewerEncodingOverride = ViewerEncoding.Auto;
            ApplyViewerStatusLine();
            // プレビューを再描画
            RequestPreviewRefresh(force: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // W: 折り返し切替
        if (e.KeyCode == Keys.W)
        {
            viewerTextBox.WordWrap = !viewerTextBox.WordWrap;
            viewerTextBox.ScrollBars = viewerTextBox.WordWrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
            // 設定の永続化
            _settings.Preview.ViewerWordWrap = viewerTextBox.WordWrap;
            SettingsManager.Save(_settings);
            ApplyViewerStatusLine();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }
        // ラージファイル用全体ナビゲーション
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            var state = _largeFileState;
            int oldLine = state.FirstVisibleLine;
            int newLine = oldLine;
            if (e.KeyCode == Keys.Home)
            {
                newLine = 0;
            }
            else if (e.KeyCode == Keys.End)
            {
                if (state.IsIndexing)
                {
                    state.PendingEndAfterIndex = true;
                    ShowStatusMessage("インデックス完了後に末尾へ移動します...");
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return true;
                }
                newLine = _largeFileControl.GetMaxFirstVisibleLine();
            }
            else if (e.KeyCode == Keys.PageUp)
            {
                newLine = oldLine - _largeFileControl.VisibleLineCount;
            }
            else if (e.KeyCode == Keys.PageDown)
            {
                newLine = oldLine + _largeFileControl.VisibleLineCount;
            }
            if (newLine != oldLine || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
            {
                _ = NavigateLargeFilePreviewAsync(newLine, e.KeyCode.ToString());
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
        if (_uiMode != UIMode.Viewer) return false;
        // Ctrl+F / F3 / Shift+F3: Viewer 検索ロジックへのルーティング
        if (keyData == (Keys.Control | Keys.F))
        {
            ExecuteViewerFind();
            return true;
        }
        if (keyData == (Keys.Control | Keys.A))
        {
            if (_currentViewerKind == PreviewKind.Text && viewerTextBox.Visible)
            {
                viewerTextBox.SelectAll();
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
        // Ctrl+C: ラージファイル表示中コピー
        if (keyData == (Keys.Control | Keys.C))
        {
            if (TryCopyLargeFileVisibleText())
            {
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
            ToggleMark(moveNext: false);
            return true;
        }
        // Shift+Home: ファイルのみ反転
        if (keyData == (Keys.Shift | Keys.Home))
        {
            InvertBulkMarks(includeDirectories: false);
            return true;
        }
        // Shift+End: ファイル + ディレクトリを反転
        if (keyData == (Keys.Shift | Keys.End))
        {
            InvertBulkMarks(includeDirectories: true);
            return true;
        }

        return false;
    }

    private bool TryHandleBrowserCmdKeyCustomBindings(Keys keyData)
    {
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy())
        {
            return false;
        }

        Dictionary<string, string> keyMap = ResolveBrowserKeyCommandMap();
        string keyGesture = InputSettings.ToKeyGestureText(keyData);
        if (!keyMap.TryGetValue(keyGesture, out string? commandId))
        {
            return false;
        }

        if (string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            ShowStatusMessage($"キー割り当て無効: {keyGesture}");
            return true;
        }

        return ExecuteCommandFromUi(commandId, CommandScope.Browser, $"Browser.CmdKey.Custom:{keyGesture}");
    }

    private Dictionary<string, string> ResolveBrowserKeyCommandMap()
    {
        return BrowserCommandBindingResolver.ResolveEffectiveKeyCommandMap(
            CurrentFunctionKeyProfileValue,
            _settings.Input?.BrowserKeyCommandOverrides,
            _commandRegistry);
    }

    private bool TryHandleBrowserCmdKeyNavigation(Keys keyData)
    {
        // 履歴移動 (Alt 系) - リストの中身の有無にかかわらず動作
        if (keyData == (Keys.Alt | Keys.Left))
        {
            ExecuteHistoryBack();
            return true;
        }
        if (keyData == (Keys.Alt | Keys.Right))
        {
            ExecuteHistoryForward();
            return true;
        }
        int total = fileListView.Items.Count;
        if (total <= 0) return false;
        int itemsPerPage = GetBrowserItemsPerPage(out _, out int rowsPerColumn);
        bool moved = false;
        if (keyData == Keys.Up)
        {
            _browserCursorIndex = (_browserCursorIndex - 1 + total) % total;
            moved = true;
        }
        else if (keyData == Keys.Down)
        {
            _browserCursorIndex = (_browserCursorIndex + 1) % total;
            moved = true;
        }
        else if (keyData == Keys.Left)
        {
            _browserCursorIndex = Math.Max(0, _browserCursorIndex - rowsPerColumn);
            moved = true;
        }
        else if (keyData == Keys.Right)
        {
            _browserCursorIndex = Math.Min(total - 1, _browserCursorIndex + rowsPerColumn);
            moved = true;
        }
        else if (keyData == (Keys.Control | Keys.Home) || keyData == Keys.F11)
        {
            return ExecuteFunctionKey(11);
        }
        else if (keyData == (Keys.Control | Keys.End) || keyData == Keys.F12)
        {
            return ExecuteFunctionKey(12);
        }
        else if (keyData == (Keys.Control | Keys.Back))
        {
            if (GuardClipboardBusy()) return true;
            _previewPopup.Clear();
            _currentPreviewTarget = null;
            ExecuteHistoryBack();
            return true;
        }
        else if (keyData == Keys.PageUp)
        {
            if (_browserCursorIndex - itemsPerPage >= 0)
            {
                _browserCursorIndex -= itemsPerPage;
                moved = true;
            }
        }
        else if (keyData == Keys.PageDown)
        {
            if (_browserCursorIndex + itemsPerPage < total)
            {
                _browserCursorIndex += itemsPerPage;
                moved = true;
            }
        }
        if (moved)
        {
            InvalidateRecentMultiMarkIntent();
            SyncBrowserSelection();
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

        if (TryHandleFdCompatibleShortcutAliases(keyData))
        {
            return true;
        }
        if (keyData == (Keys.Control | Keys.M))
        {
            if (GuardClipboardBusy()) return true;
            OpenMarkSlotDialog();
            return true;
        }
        if (keyData == (Keys.Control | Keys.R))
        {
            return ExecuteCurrentDirectoryReloadCommand();
        }
        if (keyData == (Keys.Control | Keys.F))
        {
            ExecuteFilter();
            return true;
        }
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
        // Shift+R: 再読込
        if (keyData == (Keys.Shift | Keys.R))
        {
            return ExecuteCurrentDirectoryReloadCommand();
        }
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
                    var rawKind = PreviewService.GetPreviewKind(fullPath);
                    if (rawKind == PreviewKind.Video)
                    {
                        if (_settings.Preview?.VideoEnterPlaysExternal == true)
                        {
                            ExecuteBrowserOpenRequest(CreateBrowserOpenRequest(fullPath, allowExecuteTarget: true));
                        }
                        else
                        {
                            var launchResult = VideoPlaybackLaunchService.Launch(
                                fullPath,
                                _settings.Preview?.VideoToolDirectory,
                                _settings.Preview?.VideoPlaybackVolumePercent ?? 100,
                                0);
                            if (launchResult.Success)
                            {
                                if (launchResult.UsedFfplay)
                                {
                                    ShowStatusMessage($"ffplay.exeで外部再生しました。音量:{launchResult.AppliedVolumePercent}%");
                                }
                                else
                                {
                                    ShowStatusMessage("ffplay.exeが見つからないため、既定アプリで動画を開きました。");
                                }
                            }
                            else
                            {
                                MessageBox.Show(this, launchResult.ErrorMessage ?? "外部再生の起動に失敗しました。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                        return true;
                    }
                }
            }
            return false;
        }
        if (keyData == (Keys.Alt | Keys.F1))
        {
            return ExecuteCommandFromUi(CommandIds.AppOpenNewInstance, CommandScope.Global, "Browser.CmdKey.AltF1");
        }
        if (keyData == Keys.Z)
        {
            ExecuteZLaunch();
            return true;
        }
        if (keyData == (Keys.Alt | Keys.F2))
        {
            return ExecuteCommandFromUi(CommandIds.BrowserOpenExplorer, CommandScope.Browser, "Browser.CmdKey.AltF2");
        }
        if (keyData == (Keys.Alt | Keys.F3))
        {
            return ExecuteCommandFromUi(CommandIds.AppOpenControlPanel, CommandScope.Global, "Browser.CmdKey.AltF3");
        }
        // Alt+Enter: プロパティ
        if (keyData == (Keys.Alt | Keys.Enter))
        {
            if (fileListView.Items.Count > 0)
            {
                ExecuteProperties(ResolveSelection());
                return true;
            }
        }
        return false;
    }

    private bool TryHandleBrowserCmdKeyClipboard(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C))
        {
            ExecuteClipboardCopy();
            return true;
        }
        if (keyData == (Keys.Control | Keys.X))
        {
            ExecuteClipboardCut();
            return true;
        }
        if (keyData == (Keys.Control | Keys.V))
        {
            return ExecuteCommandFromUi(CommandIds.ClipboardPaste, CommandScope.Browser, "Browser.CmdKey.CtrlV");
        }
        return false;
    }

    private bool TryHandleBrowserCmdKeyColumnCount(Keys keyData)
    {
        // Ctrl+1/2/3 および Ctrl+NumPad1/2/3 による明示的な表示モード切替
        if (keyData == (Keys.Control | Keys.D1) || keyData == (Keys.Control | Keys.NumPad1))
        {
            SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameOnly);
            return true;
        }
        if (keyData == (Keys.Control | Keys.D2) || keyData == (Keys.Control | Keys.NumPad2))
        {
            SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameSize);
            return true;
        }
        if (keyData == (Keys.Control | Keys.D3) || keyData == (Keys.Control | Keys.NumPad3))
        {
            SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameSizeDate);
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

            if (_columnCount != val)
            {
                _columnCount = val;
                SetBrowserFileDetailDisplayMode(BrowserFileDisplayMode.NameOnly);
            }
            else if (isRepeat && isWinFD)
            {
                BrowserFileDisplayMode currentMode = GetBrowserFileDisplayMode();
                BrowserFileDisplayMode nextMode = currentMode switch
                {
                    BrowserFileDisplayMode.NameOnly => BrowserFileDisplayMode.NameSize,
                    BrowserFileDisplayMode.NameSize => BrowserFileDisplayMode.NameSizeDate,
                    _ => BrowserFileDisplayMode.NameOnly
                };
                SetBrowserFileDetailDisplayMode(nextMode);
            }

            _settings.Session.LastColumnCount = _columnCount;
            UpdateInfoPanel();
            browserPanel.Invalidate();
            CaptureActiveBrowserTabState();
            return true;
        }
        return false;
    }

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

        if (keyCode == Keys.Escape)
        {
            LogService.Info(
                $"[CancelRuntime] MainForm.ProcessCmdKey Escape. busy={_isClipboardBusy}, " +
                $"hasCts={_fileOpCts != null}, requested={_fileOpCts?.IsCancellationRequested ?? false}, " +
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
        if (TryHandleCommandHintOverlayCmdKey(keyData))
        {
            return true;
        }
        if (IsCommandLauncherShortcut(keyData))
        {
            return ExecuteCommandFromUi(CommandIds.AppOpenCommandLauncher, CommandScope.Global, "MainForm.ProcessCmdKey.CommandLauncher");
        }
        if (_viewerInputRouter.TryHandleCmdKey(CreateViewerCmdKeyContext(), keyData)) return true;
        if (_browserInputRouter.TryHandleCmdKey(CreateBrowserCmdKeyContext(), keyData)) return true;
        if (keyData == (Keys.Control | Keys.Shift | Keys.L))
        {
            if (_uiMode == UIMode.Browser && !IsCurrentDirectoryBusy())
            {
                OpenActiveTabFilterLockDialog();
                return true;
            }
        }
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
        if (_viewerInputRouter.TryHandleKeyDown(CreateViewerKeyDownContext(), e)) return;
        if (_browserInputRouter.TryHandleKeyDown(CreateBrowserKeyDownContext(), e)) return;
    }

    private ViewerInputRouter.CmdKeyContext CreateViewerCmdKeyContext()
    {
        return new ViewerInputRouter.CmdKeyContext
        {
            IsViewerMode = _uiMode == UIMode.Viewer,
            TryHandleCore = TryHandleViewerCmdKey
        };
    }

    private ViewerInputRouter.KeyDownContext CreateViewerKeyDownContext()
    {
        return new ViewerInputRouter.KeyDownContext
        {
            IsViewerMode = _uiMode == UIMode.Viewer,
            TryHandleCore = TryHandleViewerKeyDown
        };
    }

    private BrowserInputRouter.CmdKeyContext CreateBrowserCmdKeyContext()
    {
        return new BrowserInputRouter.CmdKeyContext
        {
            IsBrowserMode = _uiMode == UIMode.Browser,
            IsBrowserFocused = browserPanel.Focused,
            IsAuxPreviewActive = _previewPopupVisible && _previewPopup != null && _previewPopup.Visible,
            CanUseCommandLauncherCommands = CanUseCommandLauncherCommands(),
            TryHandleTabs = TryHandleBrowserCmdKeyTabs,
            TryHandleCustomBindings = TryHandleBrowserCmdKeyCustomBindings,
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
            IsBrowserMode = _uiMode == UIMode.Browser,
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
        if (GuardClipboardBusy())
        {
            return true;
        }
        LogAltHint($"TryHandleBrowserCmdKeyExternalToolAltSlot Slot={slotLabel} Tool={tool!.Id}");
        HideCommandHintOverlay("TryHandleBrowserCmdKeyExternalToolAltSlot");
        InvokeLaunchExternalTool(tool!);
        return true;
    }

    private bool TryHandleBrowserCmdKeyFileOperationUndoRedo(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z))
        {
            if (GuardClipboardBusy()) return true;
            return ExecuteCommandFromUi(CommandIds.EditUndo, CommandScope.Browser, "Browser.CmdKey.CtrlZ");
        }
        if (keyData == (Keys.Control | Keys.Y))
        {
            if (GuardClipboardBusy()) return true;
            return ExecuteCommandFromUi(CommandIds.EditRedo, CommandScope.Browser, "Browser.CmdKey.CtrlY");
        }
        if (keyData == (Keys.Alt | Keys.Z))
        {
            if (GuardClipboardBusy()) return true;
            return ExecuteCommandFromUi(CommandIds.EditUndo, CommandScope.Browser, "Browser.CmdKey.AltZ");
        }
        if (keyData == (Keys.Alt | Keys.Y))
        {
            if (GuardClipboardBusy()) return true;
            return ExecuteCommandFromUi(CommandIds.EditRedo, CommandScope.Browser, "Browser.CmdKey.AltY");
        }
        return false;
    }

    private bool TryHandleBrowserCmdKeyTabs(Keys keyData)
    {
        if (_uiMode != UIMode.Browser)
        {
            return false;
        }
        if (keyData == (Keys.Control | Keys.T))
        {
            CreateNewBrowserTab();
            return true;
        }
        if (keyData == (Keys.Control | Keys.L))
        {
            ToggleActiveBrowserTabLock();
            return true;
        }
        if (keyData == (Keys.Control | Keys.W))
        {
            CloseCurrentBrowserTab();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.N))
        {
            AddGeneratedBrowserTabCategory();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Left))
        {
            LogService.Info($"[BrowserTabCategory] Shortcut Key=Ctrl+Shift+Left ActiveCategory={_activeBrowserTabCategoryId} Tabs={_browserTabs.Count} ActiveIndex={_activeBrowserTabIndex}");
            SelectAdjacentBrowserTabCategory(-1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Right))
        {
            LogService.Info($"[BrowserTabCategory] Shortcut Key=Ctrl+Shift+Right ActiveCategory={_activeBrowserTabCategoryId} Tabs={_browserTabs.Count} ActiveIndex={_activeBrowserTabIndex}");
            SelectAdjacentBrowserTabCategory(+1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Left))
        {
            SelectAdjacentBrowserTab(-1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Right))
        {
            SelectAdjacentBrowserTab(+1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Alt | Keys.Left))
        {
            MoveBrowserTabCategory(_activeBrowserTabCategoryId, -1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Alt | Keys.Right))
        {
            MoveBrowserTabCategory(_activeBrowserTabCategoryId, +1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Tab))
        {
            SelectAdjacentBrowserTab(+1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            SelectAdjacentBrowserTab(-1);
            return true;
        }
        return false;
    }
}