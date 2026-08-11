using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Services;

public static class FunctionKeyProfileService
{
    internal static bool IsExplicitUnassigned(string? commandId)
    {
        return string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly FunctionKeyDefinition[] CanonicalDefinitions = CreateCanonicalDefinitions();

    private static FunctionKeyDefinition[] CreateCanonicalDefinitions()
    {
        var definitions = new List<FunctionKeyDefinition>();
        void Add(FunctionKeyProfile profile, bool isShift, bool isCtrl, bool isAlt, params string?[] commandIds)
        {
            for (int slot = 1; slot <= commandIds.Length; slot++)
            {
                string? commandId = commandIds[slot - 1];
                if (!string.IsNullOrWhiteSpace(commandId))
                {
                    definitions.Add(new FunctionKeyDefinition
                    {
                        Profile = profile,
                        KeyNumber = slot,
                        IsShift = isShift,
                        IsCtrl = isCtrl,
                        IsAlt = isAlt,
                        CommandId = commandId
                    });
                }
            }
        }

        Add(FunctionKeyProfile.Standard, false, false, false,
            CommandIds.BrowserShowHelp, CommandIds.FileRename, CommandIds.FileCopy,
            CommandIds.BrowserOpenExternalEditor, CommandIds.BrowserReload, CommandIds.BrowserSort,
            CommandIds.BrowserFilter, CommandIds.BrowserQuickAccess, CommandIds.BrowserLogdisk,
            CommandIds.AppOpenCommandLauncher, CommandIds.BrowserOpenMarkSlot, CommandIds.AppOpenCommandList);
        Add(FunctionKeyProfile.Standard, true, false, false,
            CommandIds.AppOpenSystemInformation, CommandIds.FileRename, CommandIds.BrowserFilter,
            CommandIds.BrowserOpenExternalEditor, CommandIds.BrowserReload, CommandIds.BrowserSort,
            CommandIds.BrowserFilter, CommandIds.BrowserQuickAccess, CommandIds.BrowserOpenShell,
            CommandIds.ArchiveUnpack, CommandIds.BrowserTabClose, CommandIds.AppOpenSettings);
        Add(FunctionKeyProfile.Standard, false, false, true,
            CommandIds.AppOpenNewInstance, CommandIds.BrowserOpenExplorer, CommandIds.AppOpenControlPanel,
            null, CommandIds.AppOpenSettings);

        Add(FunctionKeyProfile.FDCompatible, false, false, false,
            CommandIds.BrowserShowHelp, CommandIds.BrowserOpenCommandDialog, CommandIds.FileCopy,
            CommandIds.FileDelete, CommandIds.FileRename, CommandIds.BrowserSort,
            CommandIds.BrowserFilter, CommandIds.BrowserTree, CommandIds.BrowserLogdisk,
            CommandIds.ArchiveUnpack, CommandIds.BrowserCursorTop, CommandIds.BrowserCursorBottom);
        Add(FunctionKeyProfile.FDCompatible, true, false, false,
            CommandIds.BrowserChangeAttributes, CommandIds.AppOpenSystemInformation, CommandIds.FileMove,
            CommandIds.FileDelete, CommandIds.BrowserCreateDirectory, CommandIds.BrowserOpenCommandPrompt,
            CommandIds.BrowserReload, CommandIds.BrowserOpenExternalEditor, CommandIds.BrowserPreview,
            CommandIds.ArchivePack, CommandIds.BrowserQuickAccess);
        Add(FunctionKeyProfile.FDCompatible, false, false, true,
            CommandIds.AppOpenNewInstance, CommandIds.BrowserOpenExplorer, CommandIds.AppOpenControlPanel,
            null, CommandIds.AppOpenSettings);

        return definitions.ToArray();
    }

    public static FunctionKeyProfile ResolveProfile(string? value)
    {
        return string.Equals(value, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase)
            ? FunctionKeyProfile.FDCompatible
            : FunctionKeyProfile.Standard;
    }

    public static IReadOnlyList<FunctionKeyDefinition> GetDefinitions(string? profileValue)
    {
        FunctionKeyProfile profile = ResolveProfile(profileValue);
        return CanonicalDefinitions.Where(definition => definition.Profile == profile).ToArray();
    }

    public static FunctionKeyDefinition? ResolveDefinition(string? profileValue, int fKey)
    {
        return ResolveDefinition(ResolveProfile(profileValue), fKey, false, false, false);
    }

    public static FunctionKeyDefinition? ResolveDefinition(
        FunctionKeyProfile profile,
        int fKey,
        bool isShift,
        bool isCtrl,
        bool isAlt)
    {
        return CanonicalDefinitions.FirstOrDefault(definition =>
            definition.Profile == profile &&
            definition.KeyNumber == fKey &&
            definition.IsShift == isShift &&
            definition.IsCtrl == isCtrl &&
            definition.IsAlt == isAlt);
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

            return ResolveDefinition(profile, slot, isShift, isCtrl, isAlt)?.CommandId;
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

            return ResolveDefinition(profile, slot, isShift, isCtrl, isAlt)?.CommandId;
        }
    }

    public static string ResolveFunctionBarShortLabel(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return string.Empty;

        string label = commandId switch
        {
            CommandIds.BrowserNavigateParent => "parent",
            CommandIds.BrowserExecute => "open",
            CommandIds.BrowserNavigateBack => "back",
            CommandIds.BrowserNavigateForward => "fwd",
            CommandIds.BrowserDefaultOpen => "open",
            CommandIds.BrowserOpenCommandDialog => "eXec",
            CommandIds.BrowserReload => "rld",
            CommandIds.BrowserCursorTop => "top",
            CommandIds.BrowserCursorBottom => "btm",
            CommandIds.BrowserOpenExplorer => "expl",
            CommandIds.BrowserOpenShell => "psh",
            CommandIds.BrowserOpenExternalEditor => "edit",
            CommandIds.BrowserOpenCommandPrompt => "sHell",
            CommandIds.BrowserChangeAttributes => "attr",
            CommandIds.BrowserSort => "sort",
            CommandIds.BrowserFilter => "filt",
            CommandIds.BrowserTree => "tree",
            CommandIds.BrowserQuickAccess => "qacc",
            CommandIds.BrowserLogdisk => "logd",
            CommandIds.ArchiveUnpack => "unpk",
            CommandIds.BrowserCreateDirectory => "mkdir",
            CommandIds.BrowserCreateFile => "newf",
            CommandIds.BrowserPathEntryOpen => "path",
            CommandIds.BrowserPreview => "view",
            CommandIds.ArchivePack => "pack",
            CommandIds.BrowserCopyFullPath => "path",
            CommandIds.BrowserTabNew => "tabn",
            CommandIds.BrowserTabNext => "tab>",
            CommandIds.BrowserTabPrevious => "tab<",
            CommandIds.BrowserTabCategoryAdd => "cat+",
            CommandIds.BrowserTabCategoryRename => "catr",
            CommandIds.BrowserTabCategoryDelete => "catd",
            CommandIds.BrowserTabCategoryMoveLeft => "cat<",
            CommandIds.BrowserTabCategoryMoveRight => "cat>",
            CommandIds.BrowserTabCategoryNext => "cat>",
            CommandIds.BrowserTabCategoryPrevious => "cat<",
            CommandIds.BrowserTabClose => "tabc",
            CommandIds.BrowserTabRestoreClosed => "tabr",
            CommandIds.ClipboardPaste => "pst",
            CommandIds.FileCopy => "copy",
            CommandIds.FileMove => "move",
            CommandIds.FileRename => "ren",
            CommandIds.FileDelete => "del",
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

        return label;
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
                CommandIds.BrowserReload => "rld",
                CommandIds.BrowserQuickAccess => "qacc",
                CommandIds.BrowserCreateDirectory => "mkdr",
                CommandIds.FileRename => "ren",
                _ => ResolveFunctionBarShortLabel(commandId)
            }
            : commandId switch
            {
                CommandIds.FileRename => "rena",
                CommandIds.BrowserReload => "relo",
                CommandIds.BrowserQuickAccess => "qacc",
                CommandIds.BrowserCreateDirectory => "mkdr",
                _ => ResolveFunctionBarShortLabel(commandId)
            };

        return label;
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
