using System;
using System.Collections.Generic;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// 組み込みコマンドの一覧を管理するサービス。
/// </summary>
public static class CommandPaletteService
{
    public static IEnumerable<CommandLauncherCommand> GetBuiltInCommands(MainForm mainForm, FeatureGateService featureGate)
    {
        var commands = new List<CommandLauncherCommand>
        {
            new()
            {
                Id = "browser.reloadCurrentDirectory",
                DisplayName = "現在ディレクトリを再読込",
                Description = "最新の状態に更新します",
                Category = "Browser",
                SearchText = "reload current directory",
                Execute = () => mainForm.InvokeReloadCurrentDirectory()
            },
            new()
            {
                Id = "browser.copyCurrentPath",
                DisplayName = "現在パスをコピー",
                Description = "カレントディレクトリのフルパスをクリップボードにコピーします",
                Category = "Browser",
                SearchText = "copy current path",
                Execute = () => mainForm.InvokeCopyCurrentDirectory()
            },
            new()
            {
                Id = "browser.copySelectedItemFullPath",
                DisplayName = "選択項目のフルパスをコピー",
                Description = "カーソル位置にある項目のフルパスをクリップボードにコピーします",
                Category = "Browser",
                SearchText = "copy selected item full path",
                Execute = () => mainForm.InvokeCopySelectedItemFullPath()
            },
            new()
            {
                Id = "app.openSettings",
                DisplayName = "設定を開く",
                Description = "アプリケーションの設定画面を表示します",
                Category = "App",
                SearchText = "open settings",
                Execute = () => mainForm.InvokeOpenSettingsForm()
            },
            new()
            {
                Id = "mark.openSlotManager",
                DisplayName = "マークスロット管理を開く",
                Description = "マークスロット（ディレクトリ・コマンド）の管理画面を表示します",
                Category = "Mark",
                SearchText = "mark slot manager",
                Execute = () => mainForm.InvokeOpenMarkSlotDialog()
            }
        };

        if (featureGate.IsEnabled(FeatureId.WorkspaceSnapshot))
        {
            commands.Add(new CommandLauncherCommand
            {
                Id = "workspace.openSnapshotManager",
                DisplayName = "Workspace Snapshot 管理を開く",
                Description = "Workspace スナップショット（保存・復元・エクスポート）の管理画面を表示します",
                Category = "Workspace",
                SearchText = "workspace snapshot スナップショット 作業状態 manager",
                Execute = () => mainForm.InvokeOpenWorkspaceSnapshotDialog()
            });
        }

        return commands;
    }

    public static IEnumerable<CommandLauncherCommand> GetExternalCommands(MainForm mainForm)
    {
        var store = ExternalToolCommandStorage.Load();
        if (store?.Tools == null) return Array.Empty<CommandLauncherCommand>();

        return store.Tools
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => new CommandLauncherCommand
            {
                Id = $"external.{t.Id}",
                DisplayName = string.IsNullOrWhiteSpace(t.DisplayName) ? Path.GetFileName(t.ExecutablePath) : t.DisplayName,
                Description = t.Description ?? t.ExecutablePath,
                Category = "External",
                SearchText = string.Join(" ", new[]
                {
                    t.Alias?.Trim() ?? string.Empty,
                    t.AltSlot?.Trim() ?? string.Empty,
                    t.ExecutablePath?.Trim() ?? string.Empty
                }.Where(static x => !string.IsNullOrWhiteSpace(x))),
                SecondaryText = BuildExternalSecondaryText(t),
                Execute = () => mainForm.InvokeLaunchExternalTool(t)
            });
    }

    public static IEnumerable<CommandLauncherCommand> GetAllCommands(MainForm mainForm, FeatureGateService featureGate)
    {
        return GetBuiltInCommands(mainForm, featureGate).Concat(GetExternalCommands(mainForm));
    }

    private static string? BuildExternalSecondaryText(ExternalToolCommandDefinition tool)
    {
        string alias = tool.Alias?.Trim() ?? string.Empty;
        string altSlot = tool.AltSlot?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(alias) && string.IsNullOrWhiteSpace(altSlot))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            return $"Alt+{altSlot}";
        }

        if (string.IsNullOrWhiteSpace(altSlot))
        {
            return alias;
        }

        return $"{alias} / Alt+{altSlot}";
    }
}
