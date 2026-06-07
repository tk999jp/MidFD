using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Services;

public static class FunctionKeyProfileService
{
    private static readonly FunctionKeyDefinition[] StandardDefinitions =
    {
        new() { KeyNumber = 1, Action = FunctionKeyAction.Help, Label = "help", VisibleOnFunctionBar = true },
        new() { KeyNumber = 2, Action = FunctionKeyAction.Rename, Label = "ren", VisibleOnFunctionBar = true },
        new() { KeyNumber = 3, Action = FunctionKeyAction.Copy, Label = "copy", VisibleOnFunctionBar = true },
        new() { KeyNumber = 4, Action = FunctionKeyAction.None, Label = "edit", VisibleOnFunctionBar = true }, // mapped to browser.open.external_editor
        new() { KeyNumber = 5, Action = FunctionKeyAction.Reload, Label = "rld", VisibleOnFunctionBar = true },
        new() { KeyNumber = 6, Action = FunctionKeyAction.None, Label = "sort", VisibleOnFunctionBar = true }, // mapped to CommandIds.BrowserSort
        new() { KeyNumber = 7, Action = FunctionKeyAction.None, Label = "filt", VisibleOnFunctionBar = true }, // mapped to CommandIds.BrowserFilter
        new() { KeyNumber = 8, Action = FunctionKeyAction.None, Label = "qacc", VisibleOnFunctionBar = true }, // mapped to CommandIds.BrowserQuickAccess
        new() { KeyNumber = 9, Action = FunctionKeyAction.None, Label = "logd", VisibleOnFunctionBar = true }, // mapped to CommandIds.BrowserLogdisk
        new() { KeyNumber = 10, Action = FunctionKeyAction.None, Label = "cmd", VisibleOnFunctionBar = true }, // mapped to app.open_command_launcher / app.open_command_launcher
        new() { KeyNumber = 11, Action = FunctionKeyAction.None, Label = "mark", VisibleOnFunctionBar = true }, // mapped to MarkSlot / mark management
        new() { KeyNumber = 12, Action = FunctionKeyAction.None, Label = "cmds", VisibleOnFunctionBar = true }  // mapped to command list / command registry
    };

    private static readonly FunctionKeyDefinition[] FdCompatibleDefinitions =
    {
        new() { KeyNumber = 1, Action = FunctionKeyAction.Help, Label = "help" },
        new() { KeyNumber = 2, Action = FunctionKeyAction.Execute, Label = "check" },
        new() { KeyNumber = 3, Action = FunctionKeyAction.Copy, Label = "copy" },
        new() { KeyNumber = 4, Action = FunctionKeyAction.None, Label = "edit" },
        new() { KeyNumber = 5, Action = FunctionKeyAction.Rename, Label = "ren" },
        new() { KeyNumber = 6, Action = FunctionKeyAction.Sort, Label = "sort" },
        new() { KeyNumber = 7, Action = FunctionKeyAction.Filter, Label = "filter" },
        new() { KeyNumber = 8, Action = FunctionKeyAction.Tree, Label = "tree" },
        new() { KeyNumber = 9, Action = FunctionKeyAction.Logdisk, Label = "logd" },
        new() { KeyNumber = 10, Action = FunctionKeyAction.Unpack, Label = "unpk" },
        new() { KeyNumber = 11, Action = FunctionKeyAction.Top, Label = "top" },
        new() { KeyNumber = 12, Action = FunctionKeyAction.None, Label = "btm" }
    };

    public static FunctionKeyProfile ResolveProfile(string? value)
    {
        return string.Equals(value, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase)
            ? FunctionKeyProfile.FDCompatible
            : FunctionKeyProfile.Standard;
    }

    public static IReadOnlyList<FunctionKeyDefinition> GetDefinitions(string? profileValue)
    {
        return ResolveProfile(profileValue) == FunctionKeyProfile.FDCompatible
            ? FdCompatibleDefinitions
            : StandardDefinitions;
    }

    public static FunctionKeyAction ResolveAction(string? profileValue, int fKey)
    {
        return ResolveDefinition(profileValue, fKey)?.Action ?? FunctionKeyAction.None;
    }

    public static FunctionKeyDefinition? ResolveDefinition(string? profileValue, int fKey)
    {
        return GetDefinitions(profileValue).FirstOrDefault(def => def.KeyNumber == fKey);
    }

    public static int? ResolveKeyNumber(string? profileValue, FunctionKeyAction action)
    {
        FunctionKeyDefinition? definition = GetDefinitions(profileValue).FirstOrDefault(def => def.Action == action);
        return definition?.KeyNumber;
    }

    public static string? ResolveCommandIdFromAction(FunctionKeyAction action)
    {
        return action switch
        {
            FunctionKeyAction.Help => CommandIds.BrowserShowHelp,
            FunctionKeyAction.Execute => CommandIds.BrowserExecute,
            FunctionKeyAction.Rename => "file.rename",
            FunctionKeyAction.Copy => "file.copy",
            FunctionKeyAction.Edit => CommandIds.BrowserOpenExternalEditor,
            FunctionKeyAction.Reload => CommandIds.BrowserReload,
            FunctionKeyAction.Sort => CommandIds.BrowserSort,
            FunctionKeyAction.Filter => CommandIds.BrowserFilter,
            FunctionKeyAction.Tree => CommandIds.BrowserTree,
            FunctionKeyAction.Logdisk => CommandIds.BrowserLogdisk,
            FunctionKeyAction.Unpack => CommandIds.ArchiveUnpack,
            FunctionKeyAction.QuickAccess => CommandIds.BrowserQuickAccess,
            FunctionKeyAction.CommandLauncher => CommandIds.AppOpenCommandLauncher,
            FunctionKeyAction.Top => CommandIds.BrowserCursorTop,
            FunctionKeyAction.Bottom => CommandIds.BrowserCursorBottom,
            _ => null
        };
    }

    public static string? ResolveFunctionBarCommandId(
        FunctionKeyProfile profile,
        int slot,
        System.Collections.Generic.Dictionary<string, string?>? standardOverrides,
        System.Collections.Generic.Dictionary<string, string?>? fdOverrides,
        System.Collections.Generic.Dictionary<string, string?>? standardOverridesShift = null,
        System.Collections.Generic.Dictionary<string, string?>? fdOverridesShift = null,
        bool isShift = false,
        System.Collections.Generic.Dictionary<string, string?>? standardOverridesCtrl = null,
        System.Collections.Generic.Dictionary<string, string?>? fdOverridesCtrl = null,
        System.Collections.Generic.Dictionary<string, string?>? standardOverridesAlt = null,
        System.Collections.Generic.Dictionary<string, string?>? fdOverridesAlt = null,
        bool isCtrl = false,
        bool isAlt = false)
    {
        string slotKey = $"F{slot}";
        if ((isCtrl && isAlt) || (isAlt && isShift) || (isCtrl && isShift))
        {
            return null;
        }

        if (profile == FunctionKeyProfile.FDCompatible)
        {
            var activeOverrides = isAlt
                ? fdOverridesAlt
                : (isCtrl ? fdOverridesCtrl : (isShift ? fdOverridesShift : fdOverrides));
            if (activeOverrides != null && activeOverrides.TryGetValue(slotKey, out string? cmdId) && !string.IsNullOrWhiteSpace(cmdId))
            {
                return cmdId;
            }

            if (isAlt)
            {
                return slot switch
                {
                    1 => CommandIds.AppOpenNewInstance,
                    2 => CommandIds.BrowserOpenExplorer,
                    3 => CommandIds.AppOpenControlPanel,
                    5 => CommandIds.AppOpenSettings,
                    _ => null
                };
            }

            if (isCtrl) return null;

            if (isShift)
            {
                return slot switch
                {
                    1 => CommandIds.BrowserChangeAttributes,
                    2 => CommandIds.AppOpenSystemInformation,
                    3 => "file.move",
                    4 => "file.delete",
                    5 => CommandIds.BrowserCreateDirectory,
                    6 => CommandIds.BrowserOpenShell,
                    7 => CommandIds.BrowserReload,
                    8 => CommandIds.BrowserOpenExternalEditor,
                    9 => CommandIds.BrowserPreview,
                    10 => CommandIds.ArchivePack,
                    11 => CommandIds.BrowserQuickAccess,
                    12 => null,
                    _ => null
                };
            }

            return slot switch
            {
                1 => CommandIds.BrowserShowHelp,
                2 => CommandIds.BrowserExecute,
                3 => "file.copy",
                4 => "file.delete",
                5 => "file.rename",
                6 => CommandIds.BrowserSort,
                7 => CommandIds.BrowserFilter,
                8 => CommandIds.BrowserTree,
                9 => CommandIds.BrowserLogdisk,
                10 => CommandIds.ArchiveUnpack,
                11 => CommandIds.BrowserCursorTop,
                12 => CommandIds.BrowserCursorBottom,
                _ => null
            };
        }
        else
        {
            var activeOverrides = isAlt
                ? standardOverridesAlt
                : (isCtrl ? standardOverridesCtrl : (isShift ? standardOverridesShift : standardOverrides));
            if (activeOverrides != null && activeOverrides.TryGetValue(slotKey, out string? cmdId) && !string.IsNullOrWhiteSpace(cmdId))
            {
                return cmdId;
            }

            if (isAlt)
            {
                return slot switch
                {
                    1 => CommandIds.AppOpenNewInstance,
                    2 => CommandIds.BrowserOpenExplorer,
                    3 => CommandIds.AppOpenControlPanel,
                    5 => CommandIds.AppOpenSettings,
                    _ => null
                };
            }

            if (isCtrl) return null;

            if (isShift)
            {
                // Shift defaults for Standard profile (aligned with standard and clean command mappings)
                string? defaultShiftCmd = slot switch
                {
                    1 => CommandIds.AppOpenSystemInformation, // Info (replaces KeyH)
                    2 => "file.rename", // Ren (supports bulk rename or standard rename)
                    3 => CommandIds.BrowserFilter, // Filt (clear/adjust filter)
                    4 => CommandIds.BrowserOpenExternalEditor, // Edit
                    5 => CommandIds.BrowserReload, // Rld
                    6 => CommandIds.BrowserSort, // Sort
                    7 => CommandIds.BrowserFilter, // Filt
                    8 => CommandIds.BrowserQuickAccess, // QAcc
                    9 => CommandIds.BrowserOpenShell, // PS / Shell
                    10 => CommandIds.ArchiveUnpack, // Unpk
                    11 => CommandIds.BrowserTabClose, // TabC
                    12 => CommandIds.AppOpenSettings, // Set
                    _ => null
                };
                if (defaultShiftCmd != null) return defaultShiftCmd;
                return null;
            }

            string? defaultCmd = slot switch
            {
                1 => CommandIds.BrowserShowHelp, // F1 Help
                2 => "file.rename", // F2 Ren
                3 => "file.copy", // F3 Copy
                4 => CommandIds.BrowserOpenExternalEditor, // F4 Edit
                5 => CommandIds.BrowserReload, // F5 Rld
                6 => CommandIds.BrowserSort, // F6 Sort
                7 => CommandIds.BrowserFilter, // F7 Filt
                8 => CommandIds.BrowserQuickAccess, // F8 QAcc
                9 => CommandIds.BrowserLogdisk, // F9 Logd
                10 => CommandIds.AppOpenCommandLauncher, // F10 Cmd
                11 => CommandIds.BrowserOpenMarkSlot, // F11 Mark
                12 => CommandIds.AppOpenCommandList, // F12 Cmds
                _ => null
            };

            if (defaultCmd != null) return defaultCmd;

            var action = ResolveAction(InputSettings.StandardProfileValue, slot);
            return ResolveCommandIdFromAction(action);
        }
    }

    public static string ResolveFunctionBarShortLabel(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return string.Empty;

        string label = commandId switch
        {
            CommandIds.BrowserNavigateParent => "parent",
            CommandIds.BrowserNavigateBack => "back",
            CommandIds.BrowserNavigateForward => "fwd",
            CommandIds.BrowserExecute => "exec",
            CommandIds.BrowserReload => "rld",
            CommandIds.BrowserCursorTop => "top",
            CommandIds.BrowserCursorBottom => "btm",
            CommandIds.BrowserOpenExplorer => "expl",
            CommandIds.BrowserOpenShell => "psh",
            CommandIds.BrowserOpenExternalEditor => "edit",
            CommandIds.BrowserChangeAttributes => "attr",
            CommandIds.BrowserSort => "sort",
            CommandIds.BrowserFilter => "filt",
            CommandIds.BrowserTree => "tree",
            CommandIds.BrowserQuickAccess => "qacc",
            CommandIds.BrowserLogdisk => "logd",
            CommandIds.ArchiveUnpack => "unpk",
            CommandIds.BrowserCreateDirectory => "mkdir",
            CommandIds.BrowserPreview => "view",
            CommandIds.ArchivePack => "pack",
            CommandIds.BrowserCopyFullPath => "path",
            CommandIds.BrowserTabNew => "tabn",
            CommandIds.BrowserTabNext => "tab>",
            CommandIds.BrowserTabPrevious => "tab<",
            CommandIds.BrowserTabCategoryNext => "cat>",
            CommandIds.BrowserTabCategoryPrevious => "cat<",
            CommandIds.BrowserTabClose => "tabc",
            CommandIds.BrowserTabRestoreClosed => "tabr",
            CommandIds.ClipboardPaste => "pst",
            "file.copy" => "copy",
            "file.move" => "move",
            "file.rename" => "ren",
            "file.delete" => "del",
            CommandIds.EditUndo => "undo",
            CommandIds.EditRedo => "redo",
            CommandIds.BrowserShowHelp => "help",
            CommandIds.BrowserOpenMarkSlot => "mark",
            CommandIds.AppOpenCommandList => "cmds",
            CommandIds.AppOpenSystemInformation => "info",
            CommandIds.AppOpenNewInstance => "new",
            CommandIds.AppOpenControlPanel => "ctpl",
            CommandIds.AppOpenSettings => "set",
            CommandIds.AppOpenCommandLauncher => "cmd",
            _ => "cmd"
        };

        return InputSettings.NormalizeFunctionBarLabelText(label);
    }

    public static string ResolveFunctionBarDisplayLabelFromCommandId(
        FunctionKeyProfile profile,
        string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId) ||
            string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string label = profile == FunctionKeyProfile.FDCompatible
            ? commandId switch
            {
                CommandIds.BrowserExecute => "exec",
                CommandIds.BrowserReload => "rld",
                CommandIds.BrowserQuickAccess => "qacc",
                CommandIds.BrowserCreateDirectory => "mkdr",
                "file.rename" => "ren",
                _ => ResolveFunctionBarShortLabel(commandId)
            }
            : commandId switch
            {
                "file.rename" => "rena",
                CommandIds.BrowserReload => "relo",
                CommandIds.BrowserQuickAccess => "qiqa",
                CommandIds.BrowserCreateDirectory => "mkdr",
                _ => ResolveFunctionBarShortLabel(commandId)
            };

        return InputSettings.NormalizeFunctionBarLabelText(label);
    }

    public static string ResolveFdCompatibleFunctionBarShortLabel(int slot, bool isShift, bool isCtrl, bool isAlt)
    {
        if (isCtrl || (isAlt && isShift) || (isCtrl && isShift) || (isCtrl && isAlt))
        {
            return string.Empty;
        }

        string label = isAlt
            ? slot switch
            {
                1 => "new",
                2 => "expl",
                3 => "ctpl",
                5 => "set",
                _ => string.Empty
            }
            : isShift
                ? slot switch
                {
                    1 => "attr",
                    2 => "info",
                    3 => "move",
                    4 => "del",
                    5 => "mkdr",
                    6 => "psh",
                    7 => "rld",
                    8 => "edit",
                    9 => "view",
                    10 => "pack",
                    11 => "qacc",
                    _ => string.Empty
                }
                : slot switch
                {
                    1 => "help",
                    2 => "exec",
                    3 => "copy",
                    4 => "del",
                    5 => "ren",
                    6 => "sort",
                    7 => "filt",
                    8 => "tree",
                    9 => "logd",
                    10 => "unpk",
                    11 => "top",
                    12 => "btm",
                    _ => string.Empty
                };

        return InputSettings.NormalizeFunctionBarLabelText(label);
    }

    public static string ResolveCommandDisplayText(CommandDefinition? command)
    {
        if (command == null)
        {
            return string.Empty;
        }

        string displayName = string.IsNullOrWhiteSpace(command.DisplayName)
            ? command.Id
            : command.DisplayName;
        string description = string.IsNullOrWhiteSpace(command.Description)
            ? string.Empty
            : command.Description.Trim();
        return string.IsNullOrEmpty(description)
            ? displayName
            : $"{displayName} — {description}";
    }

    public static string ResolveFunctionBarKeyHint(string? commandId, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? overrides, string? profileValue)
    {
        if (!TryResolveBrowserGesture(commandId, overrides, profileValue, out string? gesture))
        {
            return string.Empty;
        }

        return gesture ?? string.Empty;
    }

    public static string ResolveFunctionBarBrowserHotKeyCharacter(string? commandId, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? overrides, string? profileValue)
    {
        if (!TryResolveFunctionBarBrowserHotKeyCharacter(commandId, overrides, profileValue, out string? hotKeyCharacter))
        {
            return string.Empty;
        }

        return hotKeyCharacter ?? string.Empty;
    }

    private static bool IsFunctionKeyGesture(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        // Check if key is F1 to F12 (with or without Shift/Ctrl/Alt modifier)
        string[] parts = gesture.Split('+', System.StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (part.StartsWith("F", System.StringComparison.OrdinalIgnoreCase) && part.Length >= 2)
            {
                if (int.TryParse(part.Substring(1), out int num) && num >= 1 && num <= 12)
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static string ResolveFunctionBarPrimaryKeyHint(string? commandId, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? overrides, string? profileValue)
    {
        if (!TryResolveBrowserGesture(commandId, overrides, profileValue, out string? gesture) || string.IsNullOrWhiteSpace(gesture))
        {
            return string.Empty;
        }

        if (!InputSettings.TryParseKeyGesture(gesture, out Keys keyData))
        {
            return string.Empty;
        }

        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode < Keys.A || keyCode > Keys.Z)
        {
            return string.Empty;
        }

        string keyText = keyCode.ToString();
        return keyText.Length == 1 ? keyText.ToUpperInvariant() : string.Empty;
    }

    private static bool TryResolveFunctionBarBrowserHotKeyCharacter(string? commandId, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? overrides, string? profileValue, out string? hotKeyCharacter)
    {
        hotKeyCharacter = null;
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return false;
        }

        if (TryResolveUnmodifiedBrowserAlphabetHotKeyCharacter(commandId, overrides, out hotKeyCharacter))
        {
            return true;
        }

        var defaultMap = InputSettings.GetDefaultBrowserKeyCommandMap(profileValue);
        if (!defaultMap.TryGetValue(commandId, out IReadOnlyList<string>? defaultGestures) || defaultGestures == null)
        {
            return false;
        }

        return TryResolveUnmodifiedBrowserAlphabetHotKeyCharacter(defaultGestures, out hotKeyCharacter);
    }

    private static bool TryResolveUnmodifiedBrowserAlphabetHotKeyCharacter(string? commandId, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? overrides, out string? hotKeyCharacter)
    {
        hotKeyCharacter = null;
        if (string.IsNullOrWhiteSpace(commandId) || overrides == null)
        {
            return false;
        }

        foreach (var kv in overrides)
        {
            if (!string.Equals(kv.Key, commandId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<string> normalizedGestures = InputSettings.NormalizeBrowserKeyGestures(kv.Value);
            return TryResolveUnmodifiedBrowserAlphabetHotKeyCharacter(normalizedGestures, out hotKeyCharacter);
        }

        return false;
    }

    private static bool TryResolveUnmodifiedBrowserAlphabetHotKeyCharacter(IEnumerable<string> gestures, out string? hotKeyCharacter)
    {
        hotKeyCharacter = null;
        foreach (string gesture in gestures)
        {
            if (string.IsNullOrWhiteSpace(gesture))
            {
                continue;
            }

            if (string.Equals(gesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsFunctionKeyGesture(gesture))
            {
                continue;
            }

            if (!InputSettings.TryParseKeyGesture(gesture, out Keys keyData))
            {
                continue;
            }

            if ((keyData & Keys.Modifiers) != Keys.None)
            {
                continue;
            }

            Keys keyCode = keyData & Keys.KeyCode;
            if (keyCode < Keys.A || keyCode > Keys.Z)
            {
                continue;
            }

            string keyText = keyCode.ToString();
            if (keyText.Length == 1)
            {
                hotKeyCharacter = keyText.ToUpperInvariant();
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveBrowserGesture(
        string? commandId,
        System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? overrides,
        string? profileValue,
        out string? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return false;
        }

        if (overrides != null)
        {
            foreach (var kv in overrides)
            {
                if (!string.Equals(kv.Key, commandId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                List<string> normalizedGestures = InputSettings.NormalizeBrowserKeyGestures(kv.Value);
                foreach (string normalizedGesture in normalizedGestures)
                {
                    if (!string.IsNullOrWhiteSpace(normalizedGesture) &&
                        !IsFunctionKeyGesture(normalizedGesture) &&
                        !string.Equals(normalizedGesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
                    {
                        gesture = normalizedGesture;
                        return true;
                    }
                }

                return false;
            }
        }

        var defaultMap = InputSettings.GetDefaultBrowserKeyCommandMap(profileValue);
        if (defaultMap.TryGetValue(commandId, out IReadOnlyList<string>? defaultGestures))
        {
            foreach (string defaultGesture in defaultGestures)
            {
                if (!string.IsNullOrWhiteSpace(defaultGesture) && !IsFunctionKeyGesture(defaultGesture))
                {
                    gesture = defaultGesture;
                    return true;
                }
            }
        }

        return false;
    }

    private static string SimplifyKeyGesture(string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return string.Empty;

        return gesture
            .Replace("Ctrl", "^")
            .Replace("Shift", "+")
            .Replace("Alt", "@")
            .Replace("+", "");
    }

    public static string ResolveFunctionBarDisplayLabel(
        FunctionKeyProfile profile,
        int slot,
        bool isShift,
        bool isCtrl,
        bool isAlt,
        string? commandId,
        System.Collections.Generic.Dictionary<string, FunctionBarLabelOverride>? labelOverrides)
    {
        if (string.IsNullOrWhiteSpace(commandId) ||
            string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(commandId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string slotKey = $"F{slot}";
        if (labelOverrides != null &&
            labelOverrides.TryGetValue(slotKey, out FunctionBarLabelOverride? labelOverride) &&
            labelOverride != null &&
            string.Equals(labelOverride.CommandId, commandId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(labelOverride.Label))
        {
            return InputSettings.NormalizeFunctionBarLabelText(labelOverride.Label);
        }

        return ResolveFunctionBarDisplayLabelFromCommandId(profile, commandId);
    }

    public static string ResolveFunctionBarDefaultDisplayLabel(FunctionKeyProfile profile, int slot, bool isShiftLayer, bool isCtrlLayer = false, bool isAltLayer = false)
    {
        if (isCtrlLayer || (isAltLayer && isShiftLayer) || (isCtrlLayer && isShiftLayer) || (isCtrlLayer && isAltLayer))
        {
            return string.Empty;
        }

        string? commandId = ResolveFunctionBarCommandId(
            profile,
            slot,
            null,
            null,
            null,
            null,
            isShiftLayer,
            null,
            null,
            null,
            null,
            isCtrlLayer,
            isAltLayer);

        return ResolveFunctionBarDisplayLabelFromCommandId(profile, commandId);
    }
}
