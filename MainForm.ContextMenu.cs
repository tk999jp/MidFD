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

    private void ShowBrowserItemContextMenu(Point location)
    {
        if (TryConsumeBrowserContextMenuSuppress())
        {
            return;
        }
        ListViewItem? item = GetCurrentBrowserItem();
        if (item == null)
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
        BuildBrowserItemContextMenu(_browserItemContextMenu, item);
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

    private void BuildBrowserItemContextMenu(ContextMenuStrip menu, ListViewItem item)
    {
        var selection = ResolveSelection();
        bool isReadOnly = IsActiveBrowserTabReadOnly();
        bool isBusy = IsCurrentDirectoryBusy();
        bool hasSelection = selection.Count > 0;
        string? itemPath = item.Tag as string;
        bool canUseItemPath = item.Text != ".." && !string.IsNullOrWhiteSpace(itemPath);
        string? browserItemWorkingDirectory = canUseItemPath && itemPath != null ? GetBrowserItemWorkingDirectory(itemPath) : null;
        bool canRegisterItem = !isReadOnly
            && !isBusy
            && canUseItemPath
            && itemPath != null
            && Directory.Exists(itemPath);
        var openItem = new ToolStripMenuItem("開く", null, (s, e) => ExecuteEnter())
        {
            Enabled = !isBusy
        };
        menu.Items.Add(openItem);
        var openDefaultItem = new ToolStripMenuItem("既定アプリで開く", null, (s, e) => ExecuteDefaultOpen())
        {
            Enabled = !isBusy
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
            ExecuteCommandFromUi(CommandIds.BrowserCopyFullPath, CommandScope.Browser, "BrowserContextMenu.Item.CopyFullPath"))
        {
            Enabled = !isBusy && hasSelection
        };
        menu.Items.Add(copyPathItem);
        var copyItem = new ToolStripMenuItem("コピー", null, (s, e) => _ = ExecuteCopy())
        {
            Enabled = !isBusy && hasSelection
        };
        menu.Items.Add(copyItem);
        var moveItem = new ToolStripMenuItem("移動", null, (s, e) => _ = ExecuteMove())
        {
            Enabled = !isBusy && hasSelection && !isReadOnly
        };
        menu.Items.Add(moveItem);
        var renameItem = new ToolStripMenuItem("リネーム", null, (s, e) => ExecuteRename())
        {
            Enabled = !isBusy && hasSelection && !isReadOnly
        };
        menu.Items.Add(renameItem);
        var deleteItem = new ToolStripMenuItem("削除", null, (s, e) => _ = ExecuteDelete(permanent: false))
        {
            Enabled = !isBusy && hasSelection && !isReadOnly
        };
        menu.Items.Add(deleteItem);
        var attributeItem = new ToolStripMenuItem("属性/日時変更", null, (s, e) =>
            ExecuteCommandFromUi(CommandIds.BrowserChangeAttributes, CommandScope.Browser, "BrowserContextMenu.Item.Attribute"))
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
        var propertiesItem = new ToolStripMenuItem("プロパティ", null, (s, e) => ExecuteProperties(selection))
        {
            Enabled = !isBusy && hasSelection
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
        menu.Items.Add(new ToolStripMenuItem("新規フォルダ", null, (s, e) => ExecuteCreateDirectory())
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
        menu.Items.Add(new ToolStripMenuItem("現在のパスをコピー", null, (_, _) => CopyCurrentDirectoryFromHeader())
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
        var res = SelectionResolver.Resolve(_markedFiles, fileListView.Items.Count > 0 && _browserCursorIndex >= 0 ? fileListView.Items[_browserCursorIndex] : null);
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
