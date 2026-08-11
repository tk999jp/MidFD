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
using MidFD.Presentation;
using MidFD.Services.TrashManifestStore;
using MidFD.Services.Workspace;
namespace MidFD;

public partial class MainForm : Form
{
    private SelectionResult? _browserContextMenuSelectionOverride;

    private void ExecuteOpenCurrentPathInExplorer()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(_navigationService.CurrentPath);
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            LogService.Error($"Explorer 起動失敗: {ex.Message}");
            MessageBox.Show(this, $"エクスプローラーを開くことができませんでした。\n理由: {ex.Message}", "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExecuteOpenBrowserItemInExplorer(string itemPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{itemPath}\"",
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            LogService.Error($"Explorer 選択起動失敗: {ex.Message}");
            ShowStatusMessage("Explorer の起動に失敗しました。");
            MessageBox.Show(this, $"エクスプローラーで項目を選択して開くことができませんでした。\n理由: {ex.Message}", "起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClearAndDisposeMenuItems(ContextMenuStrip menu)
    {
        var items = new ToolStripItem[menu.Items.Count];
        menu.Items.CopyTo(items, 0);
        menu.Items.Clear();
        for (int i = 0; i < items.Length; i++)
        {
            items[i].Dispose();
        }
    }

    private void RunWithBrowserContextMenuSelection(SelectionResult selection, Action action)
    {
        SelectionResult? previous = _browserContextMenuSelectionOverride;
        _browserContextMenuSelectionOverride = selection;
        try
        {
            action();
        }
        finally
        {
            _browserContextMenuSelectionOverride = previous;
        }
    }

    private void ShowBrowserItemContextMenu(Point location, ListViewItem item, BrowserContextMenuTargetResolution targetResolution)
    {
        if (TryConsumeBrowserContextMenuSuppress())
        {
            return;
        }
        if (_browserItemContextMenu == null)
        {
            _browserItemContextMenu = new ContextMenuStrip();
        }
        else
        {
            _browserItemContextMenu.Close();
            ClearAndDisposeMenuItems(_browserItemContextMenu);
        }
        BuildBrowserItemContextMenu(_browserItemContextMenu, item, targetResolution);
        if (_browserItemContextMenu.Items.Count == 0)
        {
            return;
        }
        _browserItemContextMenu.Show(browserPanel, location);
    }

    private void ShowBrowserBlankContextMenu(Point location)
    {
        if (TryConsumeBrowserContextMenuSuppress())
        {
            return;
        }
        if (_browserBlankContextMenu == null)
        {
            _browserBlankContextMenu = new ContextMenuStrip();
        }
        else
        {
            _browserBlankContextMenu.Close();
            ClearAndDisposeMenuItems(_browserBlankContextMenu);
        }
        BuildBrowserBlankContextMenu(_browserBlankContextMenu);
        if (_browserBlankContextMenu.Items.Count == 0)
        {
            return;
        }
        _browserBlankContextMenu.Show(browserPanel, location);
    }

    private void EnsureBrowserTabCategoryContextMenu()
    {
        if (_browserTabCategoryContextMenu != null)
        {
            return;
        }
        _browserTabCategoryContextMenu = new ContextMenuStrip();
        _browserTabCategoryContextMenu.Closed += (_, _) => ClearBrowserTabCategoryContextState();
        _addBrowserTabCategoryContextMenuItem = new ToolStripMenuItem("カテゴリ追加");
        _addBrowserTabCategoryContextMenuItem.ShortcutKeyDisplayString = ResolveBrowserCommandShortcutHint(CommandIds.BrowserTabCategoryAdd);
        _addBrowserTabCategoryContextMenuItem.Click += (_, _) => ExecuteCommandFromUi(CommandIds.BrowserTabCategoryAdd, CommandScope.Browser, "BrowserTabCategoryContextMenu.Add");
        _moveBrowserTabCategoryLeftContextMenuItem = new ToolStripMenuItem("左へ移動");
        _moveBrowserTabCategoryLeftContextMenuItem.ShortcutKeyDisplayString = ResolveBrowserCommandShortcutHint(CommandIds.BrowserTabCategoryMoveLeft);
        _moveBrowserTabCategoryLeftContextMenuItem.Click += (_, _) => ExecuteCommandFromUi(CommandIds.BrowserTabCategoryMoveLeft, CommandScope.Browser, "BrowserTabCategoryContextMenu.MoveLeft", categoryId: _categoryViewState.ContextCategoryId);
        _moveBrowserTabCategoryRightContextMenuItem = new ToolStripMenuItem("右へ移動");
        _moveBrowserTabCategoryRightContextMenuItem.ShortcutKeyDisplayString = ResolveBrowserCommandShortcutHint(CommandIds.BrowserTabCategoryMoveRight);
        _moveBrowserTabCategoryRightContextMenuItem.Click += (_, _) => ExecuteCommandFromUi(CommandIds.BrowserTabCategoryMoveRight, CommandScope.Browser, "BrowserTabCategoryContextMenu.MoveRight", categoryId: _categoryViewState.ContextCategoryId);
        _renameBrowserTabCategoryContextMenuItem = new ToolStripMenuItem("名前変更");
        _renameBrowserTabCategoryContextMenuItem.Click += (_, _) => ExecuteCommandFromUi(CommandIds.BrowserTabCategoryRename, CommandScope.Browser, "BrowserTabCategoryContextMenu.Rename", categoryId: _categoryViewState.ContextCategoryId);
        _deleteBrowserTabCategoryContextMenuItem = new ToolStripMenuItem("削除");
        _deleteBrowserTabCategoryContextMenuItem.Click += (_, _) => ExecuteCommandFromUi(CommandIds.BrowserTabCategoryDelete, CommandScope.Browser, "BrowserTabCategoryContextMenu.Delete", categoryId: _categoryViewState.ContextCategoryId);
        _manageBrowserTabCategoriesContextMenuItem = new ToolStripMenuItem("カテゴリ管理...");
        _manageBrowserTabCategoriesContextMenuItem.Click += (_, _) => ExecuteCommandFromUi(CommandIds.BrowserTabCategoryManage, CommandScope.Browser, "BrowserTabCategoryContextMenu.Manage");
        _browserTabCategoryContextMenu.Items.AddRange(
        [
            _addBrowserTabCategoryContextMenuItem,
            new ToolStripSeparator(),
            _moveBrowserTabCategoryLeftContextMenuItem,
            _moveBrowserTabCategoryRightContextMenuItem,
            _renameBrowserTabCategoryContextMenuItem,
            _deleteBrowserTabCategoryContextMenuItem,
            new ToolStripSeparator(),
            _manageBrowserTabCategoriesContextMenuItem
        ]);
    }

    private void ShowBrowserTabCategoryContextMenu(BrowserTabStripCategoryEventArgs e)
    {
        ShowBrowserTabCategoryContextMenu(_browserTabStrip, e);
    }

    private void ShowBrowserTabCategoryContextMenu(Control? owner, BrowserTabStripCategoryEventArgs e)
    {
        if (_browserTabStrip == null)
        {
            return;
        }
        EnsureBrowserTabCategoryConfiguration();
        EnsureBrowserTabCategoryContextMenu();
        ClearBrowserTabCategoryContextState();
        _categoryViewState.ContextCategoryId = e.Kind == BrowserTabStripCategoryItemKind.ManageEntry ? null : e.CategoryId;
        _browserTabCategoryContextKind = e.Kind;
        BrowserTabCategoryDefinition? targetCategory = FindBrowserTabCategoryDefinition(_categoryViewState.ContextCategoryId);
        int targetIndex = targetCategory == null
            ? -1
            : _categoryViewState.FindIndex(category => string.Equals(category.Id, targetCategory.Id, StringComparison.OrdinalIgnoreCase));
        var state = new BrowserTabCategoryContextMenuState(
            targetCategory != null,
            targetIndex > 0,
            targetIndex >= 0 && targetIndex < _categoryViewState.Count - 1);
        BrowserTabContextMenuPresenter.ApplyCategoryState(
            state,
            _moveBrowserTabCategoryLeftContextMenuItem,
            _moveBrowserTabCategoryRightContextMenuItem,
            _renameBrowserTabCategoryContextMenuItem,
            _deleteBrowserTabCategoryContextMenuItem,
            _browserTabCategoryContextMenu);
        if (owner != null)
        {
            _browserTabCategoryContextMenu?.Show(owner, e.Location);
        }
    }

    private void MoveBrowserTabCategoryFromContext(int delta)
    {
        if (!string.IsNullOrWhiteSpace(_categoryViewState.ContextCategoryId))
        {
            MoveBrowserTabCategory(_categoryViewState.ContextCategoryId, delta);
        }
    }

    private void RenameBrowserTabCategoryFromContext()
    {
        BrowserTabCategoryDefinition? target = FindBrowserTabCategoryDefinition(_categoryViewState.ContextCategoryId);
        if (target != null)
        {
            RenameBrowserTabCategory(target);
        }
    }

    private void DeleteBrowserTabCategoryFromContext()
    {
        BrowserTabCategoryDefinition? target = FindBrowserTabCategoryDefinition(_categoryViewState.ContextCategoryId);
        if (target != null)
        {
            DeleteBrowserTabCategory(target);
        }
    }

    private void EnsureBrowserTabContextMenu()
    {
        if (_browserTabContextMenu != null)
        {
            return;
        }
        _browserTabContextMenu = new ContextMenuStrip();
        _toggleBrowserTabLockContextMenuItem = new ToolStripMenuItem();
        _toggleBrowserTabLockContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabViewState.ContextTabIndex < 0)
            {
                return;
            }
            ExecuteCommandFromUi(CommandIds.BrowserTabLock, CommandScope.Browser, "BrowserTabContextMenu.Lock", contextTabIndex: _browserTabViewState.ContextTabIndex);
        };
        _toggleBrowserTabReadOnlyContextMenuItem = new ToolStripMenuItem();
        _toggleBrowserTabReadOnlyContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabViewState.ContextTabIndex < 0)
            {
                return;
            }
            ExecuteCommandFromUi(CommandIds.BrowserTabReadOnlyToggle, CommandScope.Browser, "BrowserTabContextMenu.ReadOnly", contextTabIndex: _browserTabViewState.ContextTabIndex);
        };
        _openBrowserTabFilterLockContextMenuItem = new ToolStripMenuItem("フィルタロック...(&L)");
        _openBrowserTabFilterLockContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabViewState.ContextTabIndex < 0) return;
            ExecuteCommandFromUi(CommandIds.BrowserTabFilterLock, CommandScope.Browser, "BrowserTabContextMenu.FilterLock", contextTabIndex: _browserTabViewState.ContextTabIndex);
        };
        _clearBrowserTabFilterLockContextMenuItem = new ToolStripMenuItem("フィルタロックを解除(&U)");
        _clearBrowserTabFilterLockContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabViewState.ContextTabIndex < 0) return;
            ExecuteCommandFromUi(CommandIds.BrowserTabFilterLockClear, CommandScope.Browser, "BrowserTabContextMenu.FilterLockClear", contextTabIndex: _browserTabViewState.ContextTabIndex);
        };
        _closeBrowserTabContextMenuItem = new ToolStripMenuItem("このタブを閉じる");
        _closeBrowserTabContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabViewState.ContextTabIndex < 0)
            {
                return;
            }
            ExecuteCommandFromUi(CommandIds.BrowserTabClose, CommandScope.Browser, "BrowserTabContextMenu.Close", contextTabIndex: _browserTabViewState.ContextTabIndex);
        };
        _closeRightBrowserTabsContextMenuItem = new ToolStripMenuItem("右側の全てのタブを閉じる");
        _closeRightBrowserTabsContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabViewState.ContextTabIndex < 0)
            {
                return;
            }
            ExecuteCommandFromUi(CommandIds.BrowserTabCloseRight, CommandScope.Browser, "BrowserTabContextMenu.CloseRight", contextTabIndex: _browserTabViewState.ContextTabIndex);
        };
        _closeLeftBrowserTabsContextMenuItem = new ToolStripMenuItem("左側の全てのタブを閉じる");
        _closeLeftBrowserTabsContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabViewState.ContextTabIndex < 0)
            {
                return;
            }
            ExecuteCommandFromUi(CommandIds.BrowserTabCloseLeft, CommandScope.Browser, "BrowserTabContextMenu.CloseLeft", contextTabIndex: _browserTabViewState.ContextTabIndex);
        };
        _closeOtherBrowserTabsContextMenuItem = new ToolStripMenuItem("このタブ以外を閉じる");
        _closeOtherBrowserTabsContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabViewState.ContextTabIndex < 0)
            {
                return;
            }
            ExecuteCommandFromUi(CommandIds.BrowserTabCloseOther, CommandScope.Browser, "BrowserTabContextMenu.CloseOther", contextTabIndex: _browserTabViewState.ContextTabIndex);
        };
        _browserTabContextMenu.Items.Add(_toggleBrowserTabLockContextMenuItem);
        _browserTabContextMenu.Items.Add(_toggleBrowserTabReadOnlyContextMenuItem);
        _browserTabContextMenu.Items.Add(new ToolStripSeparator());
        _browserTabContextMenu.Items.Add(_openBrowserTabFilterLockContextMenuItem);
        _browserTabContextMenu.Items.Add(_clearBrowserTabFilterLockContextMenuItem);
        _browserTabContextMenu.Items.Add(new ToolStripSeparator());
        _browserTabContextMenu.Items.Add(_closeBrowserTabContextMenuItem);
        _browserTabContextMenu.Items.Add(_closeRightBrowserTabsContextMenuItem);
        _browserTabContextMenu.Items.Add(_closeLeftBrowserTabsContextMenuItem);
        _browserTabContextMenu.Items.Add(_closeOtherBrowserTabsContextMenuItem);
    }

    private void UpdateBrowserTabContextMenuItems(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabViewState.Count)
        {
            return;
        }
        BrowserTabState state = _browserTabViewState.Tabs[tabIndex];
        var menuState = new BrowserTabContextMenuState(
            state.IsLocked,
            state.IsReadOnly,
            state.FilterLock.Enabled && state.FilterLock.HasAnyCondition,
            CountClosableBrowserTabs(index => index > tabIndex) > 0,
            CountClosableBrowserTabs(index => index < tabIndex) > 0,
            CountClosableBrowserTabs(index => index != tabIndex) > 0);
        BrowserTabContextMenuPresenter.ApplyTabState(
            menuState,
            _toggleBrowserTabLockContextMenuItem,
            _toggleBrowserTabReadOnlyContextMenuItem,
            _clearBrowserTabFilterLockContextMenuItem,
            _closeBrowserTabContextMenuItem,
            _closeRightBrowserTabsContextMenuItem,
            _closeLeftBrowserTabsContextMenuItem,
            _closeOtherBrowserTabsContextMenuItem);
    }

    private void BuildBrowserItemContextMenu(ContextMenuStrip menu, ListViewItem item, BrowserContextMenuTargetResolution targetResolution)
    {
        var selection = targetResolution.Selection;
        bool isReadOnly = IsActiveBrowserTabReadOnly();
        bool isBusy = IsCurrentDirectoryBusy();
        bool hasSelection = selection.Count > 0;
        bool isMultiSelectionContext = targetResolution.Kind == BrowserContextMenuKind.MultiSelection;
        string? itemPath = item.Tag as string;
        bool canUseItemPath = item.Text != ".." && !string.IsNullOrWhiteSpace(itemPath);
        string? browserItemWorkingDirectory = canUseItemPath && itemPath != null ? GetBrowserItemWorkingDirectory(itemPath) : null;
        bool isDirectoryItem = canUseItemPath
            && itemPath != null
            && Directory.Exists(itemPath);
        bool canRegisterItem = !isReadOnly
            && !isBusy
            && isDirectoryItem;
        bool canOpenInNewTab = !isBusy
            && !isMultiSelectionContext
            && isDirectoryItem;
        var openItem = new ToolStripMenuItem("開く", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.BrowserExecute, CommandScope.Browser, "Browser.ContextMenu.Open", selection))
        {
            Enabled = !isBusy && !isMultiSelectionContext && canUseItemPath
        };
        menu.Items.Add(openItem);
        var openInNewTabItem = new ToolStripMenuItem("新しいタブで開く", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.BrowserOpenInNewTab, CommandScope.Browser, "Browser.ContextMenu.OpenInNewTab", selection))
        {
            Enabled = canOpenInNewTab
        };
        menu.Items.Add(openInNewTabItem);
        var openDefaultItem = new ToolStripMenuItem("既定アプリで開く", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.BrowserDefaultOpen, CommandScope.Browser, "Browser.ContextMenu.DefaultOpen", selection))
        {
            Enabled = !isBusy && !isMultiSelectionContext && canUseItemPath
        };
        menu.Items.Add(openDefaultItem);
        menu.Items.Add(new ToolStripSeparator());

        var sevenZipMenu = Create7ZipMenu(selection);
        if (sevenZipMenu != null)
        {
            menu.Items.Add(sevenZipMenu);
        }

        var sendToMenu = new ToolStripMenuItem("送る(&N)");
        PopulateSendToMenu(sendToMenu);
        if (sendToMenu.DropDownItems.Count == 0)
        {
            sendToMenu.Enabled = false;
            sendToMenu.DropDownItems.Add(new ToolStripMenuItem("(項目なし)") { Enabled = false });
        }
        menu.Items.Add(sendToMenu);
        var explorerItem = new ToolStripMenuItem("エクスプローラーで表示", null, (s, e) =>
        {
            if (!canUseItemPath || itemPath == null)
            {
                return;
            }
            ExecuteOpenBrowserItemInExplorer(itemPath);
        })
        {
            Enabled = !isBusy && canUseItemPath
        };
        menu.Items.Add(explorerItem);
        var shellItem = new ToolStripMenuItem("PowerShellをここで開く", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.BrowserOpenShell, CommandScope.Browser, "BrowserContextMenu.Item.PowerShell"))
        {
            Enabled = !isBusy
        };
        menu.Items.Add(shellItem);
        var commandPromptItem = new ToolStripMenuItem("コマンドプロンプトで開く", null, (s, e) =>
        {
            if (!canUseItemPath || itemPath == null)
            {
                return;
            }
            string? workingDirectory = browserItemWorkingDirectory;
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return;
            }
            OpenTerminalInWorkingDirectory(workingDirectory, ShellKind.CommandPrompt);
        })
        {
            Enabled = !isBusy && !string.IsNullOrWhiteSpace(browserItemWorkingDirectory)
        };
        menu.Items.Add(commandPromptItem);
        menu.Items.Add(new ToolStripSeparator());
        var copyPathItem = new ToolStripMenuItem("パスをコピー", null, (s, e) =>
            RunWithBrowserContextMenuSelection(selection, () =>
                ExecuteCommandFromUi(CommandIds.BrowserCopyFullPath, CommandScope.Browser, "BrowserContextMenu.Item.CopyFullPath")))
        {
            Enabled = !isBusy && hasSelection
        };
        menu.Items.Add(copyPathItem);
        var cutItem = new ToolStripMenuItem("切り取り", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.ClipboardCut, CommandScope.Browser, "BrowserContextMenu.Item.Cut", selection))
        {
            Enabled = !isBusy && hasSelection && !isReadOnly
        };
        menu.Items.Add(cutItem);
        var copyItem = new ToolStripMenuItem("コピー", null, (s, e) =>
            RunWithBrowserContextMenuSelection(selection, () => ExecuteCommandFromUi(CommandIds.FileCopy, CommandScope.Browser, "BrowserContextMenu.Item.Copy", selection)))
        {
            Enabled = !isBusy && hasSelection
        };
        menu.Items.Add(copyItem);
        var moveItem = new ToolStripMenuItem("移動", null, (s, e) =>
            RunWithBrowserContextMenuSelection(selection, () => ExecuteCommandFromUi(CommandIds.FileMove, CommandScope.Browser, "BrowserContextMenu.Item.Move", selection)))
        {
            Enabled = !isBusy && hasSelection && !isReadOnly
        };
        menu.Items.Add(moveItem);
        var renameItem = new ToolStripMenuItem("リネーム", null, (s, e) =>
            RunWithBrowserContextMenuSelection(selection, () => ExecuteCommandFromUi(CommandIds.FileRename, CommandScope.Browser, "BrowserContextMenu.Item.Rename", selection)))
        {
            Enabled = !isBusy && hasSelection && !isReadOnly
        };
        menu.Items.Add(renameItem);
        var deleteItem = new ToolStripMenuItem("削除", null, (s, e) =>
            RunWithBrowserContextMenuSelection(selection, () => ExecuteCommandFromUi(CommandIds.FileDelete, CommandScope.Browser, "BrowserContextMenu.Item.Delete", selection)))
        {
            Enabled = !isBusy && hasSelection && !isReadOnly
        };
        menu.Items.Add(deleteItem);
        var attributeItem = new ToolStripMenuItem("属性/日時変更", null, (s, e) =>
            RunWithBrowserContextMenuSelection(selection, () =>
                ExecuteCommandFromUi(CommandIds.BrowserChangeAttributes, CommandScope.Browser, "BrowserContextMenu.Item.Attribute")))
        {
            Enabled = !isBusy && hasSelection && !isReadOnly
        };
        menu.Items.Add(attributeItem);
        var quickAccessItem = new ToolStripMenuItem("QuickAccessへ登録", null, (_, _) => AddSelectedBrowserItemToFavorites())
        {
            Enabled = canRegisterItem
        };
        menu.Items.Add(quickAccessItem);
        menu.Items.Add(new ToolStripSeparator());
        var propertiesItem = new ToolStripMenuItem("プロパティ", null, (s, e) => ExecuteCommandFromUi(CommandIds.BrowserProperties, CommandScope.Browser, "BrowserContextMenu.Item.Properties", selection))
        {
            Enabled = !isBusy && selection.Count == 1
        };
        menu.Items.Add(propertiesItem);
    }

    private void BuildBrowserBlankContextMenu(ContextMenuStrip menu)
    {
        bool isReadOnly = IsActiveBrowserTabReadOnly();
        bool isBusy = IsCurrentDirectoryBusy();
        bool canCurrentPath = !string.IsNullOrWhiteSpace(_navigationService.CurrentPath);
        bool canClipboardPaste = !isReadOnly && !isBusy && !_isClipboardBusy && (
            canCurrentPath &&
            (ShellClipboardService.HasFileDrop() ||
             ShellClipboardService.HasImage() ||
             ((_settings.FileOperations?.ClipboardPasteTextAsFileEnabled ?? false) && ShellClipboardService.HasText())));
        bool canQuickAccess = !isReadOnly && !isBusy && canCurrentPath;
        menu.Items.Add(new ToolStripMenuItem("貼り付け", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.ClipboardPaste, CommandScope.Browser, "BrowserContextMenu.Blank.Paste"))
        {
            Enabled = canClipboardPaste
        });
        menu.Items.Add(new ToolStripMenuItem("新規フォルダ", null, (s, e) => ExecuteCommandFromUi(CommandIds.BrowserCreateDirectory, CommandScope.Browser, "BrowserContextMenu.Blank.CreateDirectory"))
        {
            Enabled = !isReadOnly && !isBusy
        });
        menu.Items.Add(new ToolStripMenuItem("現在地をQuickAccessへ登録", null, (_, _) => AddCurrentLocationToFavorites())
        {
            Enabled = canQuickAccess
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("再読込", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.BrowserReload, CommandScope.Browser, "BrowserContextMenu.Blank.Reload"))
        {
            Enabled = !isBusy
        });
        menu.Items.Add(new ToolStripMenuItem("Explorerで開く", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.BrowserOpenExplorer, CommandScope.Browser, "BrowserContextMenu.Blank.Explorer"))
        {
            Enabled = canCurrentPath && !isBusy
        });
        menu.Items.Add(new ToolStripMenuItem("PowerShellをここで開く", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.BrowserOpenShell, CommandScope.Browser, "BrowserContextMenu.Blank.PowerShell"))
        {
            Enabled = canCurrentPath && !isBusy
        });
        menu.Items.Add(new ToolStripMenuItem("コマンドプロンプトで開く", null, (s, e) =>
            OpenTerminalInCurrentDirectory(ShellKind.CommandPrompt))
        {
            Enabled = canCurrentPath && !isBusy
        });
        menu.Items.Add(new ToolStripMenuItem("現在のパスをコピー", null, (_, _) => ExecuteCommandFromUi(CommandIds.BrowserCopyCurrentPath, CommandScope.Browser, "BrowserContextMenu.Blank.CopyCurrentPath"))
        {
            Enabled = canCurrentPath
        });
    }

    private void PopulateSendToMenu(ToolStripMenuItem sendToMenu)
    {
        string sendToPath = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        if (!Directory.Exists(sendToPath)) return;
        try
        {
            var files = Directory.GetFiles(sendToPath);
            foreach (var file in files)
            {
                var attr = File.GetAttributes(file);
                if (attr.HasFlag(FileAttributes.Hidden)) continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("圧縮") || name.Contains("Pack"))
                {
                    // 標準の圧縮機能やサブメニューと混同・重複するのを防ぐため、送るメニューからは除外する
                    continue;
                }
                Image? iconImage = null;
                try
                {
                    using (var assocIcon = Icon.ExtractAssociatedIcon(file))
                    {
                        if (assocIcon != null)
                        {
                            iconImage = assocIcon.ToBitmap();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.Detail($"SendTo アイコン取得失敗 (安全に無視): {file}, {ex.Message}");
                }
                var item = new ToolStripMenuItem(name, iconImage, (s, e) => ExecuteSendTo(file));
                sendToMenu.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"SendTo 列挙失敗: {ex.Message}");
            var errorItem = new ToolStripMenuItem("(列挙失敗)");
            errorItem.Enabled = false;
            sendToMenu.DropDownItems.Add(errorItem);
        }
    }

    private void ExecuteSendTo(string targetExeOrShortcut)
    {
        int pageLocalCursorIndex = GetBrowserPageLocalCursorIndex();
        var res = SelectionResolver.Resolve(_markedFiles, pageLocalCursorIndex >= 0 ? fileListView.Items[pageLocalCursorIndex] : null);
        if (!res.FullPaths.Any()) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = targetExeOrShortcut,
                UseShellExecute = true
            };
            var sb = new System.Text.StringBuilder();
            foreach (var path in res.FullPaths)
            {
                string escaped = (path ?? string.Empty).Replace("\"", "\\\"");
                sb.Append('"').Append(escaped).Append("\" ");
            }
            psi.Arguments = sb.ToString().TrimEnd();
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            LogService.Error($"SendTo 実行失敗: {ex.Message}");
            ShowStatusMessage($"送る操作に失敗しました: {ex.Message}");
        }
    }

    internal void InvokeOpenShell() => OpenTerminalInCurrentDirectory(ShellKind.PowerShell);
    internal void InvokeOpenExternalEditor() => ExecuteOpenWithEditor();
    internal void InvokeOpenSettingsForm() => OpenSettingsForm();
    internal void InvokeOpenSettingsForm(SettingsForm.InitialTab initialTab) => OpenSettingsForm(initialTab);
    internal void InvokeOpenMarkSlotDialog() => OpenMarkSlotDialog();
    internal void InvokeOpenWorkspaceSnapshotDialog() => OpenWorkspaceSnapshotDialog();
    internal void InvokeLaunchExternalTool(ExternalToolCommandDefinition definition)
    {
        var context = ExternalToolLaunchCoordinator.BuildExecutionContext(
            _navigationService.CurrentPath,
            GetSelectedItemFullPathForHeaderCopy(),
            GetSelectedItemNameForHeaderCopy(),
            _markedFiles.Snapshot());

        if (ExternalToolLaunchCoordinator.ShouldConfirmEmptyMarkedPaths(definition, context))
        {
            var result = ShowEmptyMarkedPathsConfirmationDialog(
                ExternalToolLaunchCoordinator.BuildEmptyMarkedPathsConfirmationMessage()
            );
            if (result != DialogResult.Yes)
            {
                ShowStatusMessage("外部ツールの起動をキャンセルしました。");
                return;
            }
        }
        string? error = ExternalToolLauncherService.Launch(definition, context);
        if (error != null)
        {
            MessageBox.Show(this, error, "外部ツール起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            ShowStatusMessage($"外部ツールを起動しました: {definition.DisplayName}");
        }
    }

    private DialogResult ShowEmptyMarkedPathsConfirmationDialog(string message)
    {
        using (var form = new Form())
        {
            form.Text = "外部ツール起動確認";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.CenterParent;
            form.ClientSize = new Size(420, 140);

            var label = new Label
            {
                Text = message,
                Location = new Point(15, 15),
                Size = new Size(390, 60),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var btnYes = new Button
            {
                Text = "はい(&Y)",
                DialogResult = DialogResult.Yes,
                Location = new Point(210, 90),
                Size = new Size(90, 30)
            };

            var btnNo = new Button
            {
                Text = "いいえ(&N)",
                DialogResult = DialogResult.No,
                Location = new Point(310, 90),
                Size = new Size(90, 30)
            };

            form.Controls.Add(label);
            form.Controls.Add(btnYes);
            form.Controls.Add(btnNo);

            form.AcceptButton = btnYes;
            form.CancelButton = btnNo;

            return form.ShowDialog(this);
        }
    }

    private void OpenTerminalInWorkingDirectory(string workingDirectory, ShellKind kind)
    {
        string? error = ExternalToolService.OpenTerminal(workingDirectory, kind);
        if (error != null) ShowStatusMessage(error);
    }
}
