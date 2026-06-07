using MidFD.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace MidFD.Configuration;

public class InputSettings
{
    public const string StandardProfileValue = "Standard";
    public const string FdCompatibleProfileValue = "FDCompatible";
    public const string MouseGestureUnassignedCommandId = "none";

    public static readonly IReadOnlyDictionary<string, string> DefaultMouseGestureCommandMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["L"] = CommandIds.BrowserNavigateBack,
            ["R"] = CommandIds.BrowserNavigateForward,
            ["U"] = CommandIds.BrowserNavigateParent,
            ["D"] = MouseGestureUnassignedCommandId,
            ["LR"] = CommandIds.BrowserTabRestoreClosed,
            ["UD"] = CommandIds.BrowserReload,
            ["LU"] = CommandIds.BrowserTabPrevious,
            ["LD"] = MouseGestureUnassignedCommandId,
            ["RL"] = MouseGestureUnassignedCommandId,
            ["RU"] = CommandIds.BrowserTabNext,
            ["RD"] = MouseGestureUnassignedCommandId,
            ["UR"] = CommandIds.BrowserTabCategoryNext,
            ["UL"] = CommandIds.BrowserTabCategoryPrevious,
            ["DL"] = MouseGestureUnassignedCommandId,
            ["DR"] = CommandIds.BrowserTabClose,
            ["DU"] = MouseGestureUnassignedCommandId
        };

    public static readonly IReadOnlyDictionary<string, string> LegacyMouseGestureKeyAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = "L",
            ["right"] = "R",
            ["up"] = "U",
            ["down"] = "D",
            ["leftRight"] = "LR",
            ["leftUp"] = "LU",
            ["leftDown"] = "LD",
            ["rightLeft"] = "RL",
            ["rightUp"] = "RU",
            ["rightDown"] = "RD",
            ["upLeft"] = "UL",
            ["upRight"] = "UR",
            ["upDown"] = "UD",
            ["downLeft"] = "DL",
            ["downRight"] = "DR",
            ["downUp"] = "DU"
        };

    public string FunctionKeyProfile { get; set; } = StandardProfileValue;
    public string CommandLauncherShortcut { get; set; } = "Ctrl+Shift+P";
    public bool EnableMouseGestures { get; set; } = true;
    public Dictionary<string, string> MouseGestureCommandMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonConverter(typeof(BrowserKeyOverridesConverter))]
    public Dictionary<string, List<string>> BrowserKeyCommandOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> FunctionBarCommandOverridesStandard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> FunctionBarCommandOverridesFdCompatible { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Shift layer overrides
    public Dictionary<string, string?> FunctionBarCommandOverridesShiftStandard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> FunctionBarCommandOverridesShiftFdCompatible { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> FunctionBarCommandOverridesCtrlStandard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> FunctionBarCommandOverridesCtrlFdCompatible { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> FunctionBarCommandOverridesAltStandard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> FunctionBarCommandOverridesAltFdCompatible { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Label overrides
    public Dictionary<string, FunctionBarLabelOverride> FunctionBarLabelOverridesStandard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionBarLabelOverride> FunctionBarLabelOverridesFdCompatible { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionBarLabelOverride> FunctionBarLabelOverridesShiftStandard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionBarLabelOverride> FunctionBarLabelOverridesShiftFdCompatible { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionBarLabelOverride> FunctionBarLabelOverridesCtrlStandard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionBarLabelOverride> FunctionBarLabelOverridesCtrlFdCompatible { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionBarLabelOverride> FunctionBarLabelOverridesAltStandard { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionBarLabelOverride> FunctionBarLabelOverridesAltFdCompatible { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowFunctionBarTooltips { get; set; } = true;

    public InputSettings Clone()
    {
        var clone = (InputSettings)MemberwiseClone();
        clone.MouseGestureCommandMap = new Dictionary<string, string>(
            MouseGestureCommandMap ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        clone.BrowserKeyCommandOverrides = NormalizeBrowserKeyCommandOverrides(BrowserKeyCommandOverrides);
        clone.FunctionBarCommandOverridesStandard = new Dictionary<string, string?>(
            FunctionBarCommandOverridesStandard ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarCommandOverridesFdCompatible = new Dictionary<string, string?>(
            FunctionBarCommandOverridesFdCompatible ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        // Clone Shift layer overrides
        clone.FunctionBarCommandOverridesShiftStandard = new Dictionary<string, string?>(
            FunctionBarCommandOverridesShiftStandard ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarCommandOverridesShiftFdCompatible = new Dictionary<string, string?>(
            FunctionBarCommandOverridesShiftFdCompatible ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarCommandOverridesCtrlStandard = new Dictionary<string, string?>(
            FunctionBarCommandOverridesCtrlStandard ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarCommandOverridesCtrlFdCompatible = new Dictionary<string, string?>(
            FunctionBarCommandOverridesCtrlFdCompatible ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarCommandOverridesAltStandard = new Dictionary<string, string?>(
            FunctionBarCommandOverridesAltStandard ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarCommandOverridesAltFdCompatible = new Dictionary<string, string?>(
            FunctionBarCommandOverridesAltFdCompatible ?? new Dictionary<string, string?>(),
            StringComparer.OrdinalIgnoreCase);

        // Clone Label overrides deeply
        clone.FunctionBarLabelOverridesStandard = (FunctionBarLabelOverridesStandard ?? new())
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Clone()!, StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarLabelOverridesFdCompatible = (FunctionBarLabelOverridesFdCompatible ?? new())
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Clone()!, StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarLabelOverridesShiftStandard = (FunctionBarLabelOverridesShiftStandard ?? new())
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Clone()!, StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarLabelOverridesShiftFdCompatible = (FunctionBarLabelOverridesShiftFdCompatible ?? new())
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Clone()!, StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarLabelOverridesCtrlStandard = (FunctionBarLabelOverridesCtrlStandard ?? new())
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Clone()!, StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarLabelOverridesCtrlFdCompatible = (FunctionBarLabelOverridesCtrlFdCompatible ?? new())
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Clone()!, StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarLabelOverridesAltStandard = (FunctionBarLabelOverridesAltStandard ?? new())
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Clone()!, StringComparer.OrdinalIgnoreCase);
        clone.FunctionBarLabelOverridesAltFdCompatible = (FunctionBarLabelOverridesAltFdCompatible ?? new())
            .ToDictionary(kv => kv.Key, kv => kv.Value?.Clone()!, StringComparer.OrdinalIgnoreCase);

        return clone;
    }

    public static string NormalizeMouseGestureId(string? gestureId)
    {
        if (string.IsNullOrWhiteSpace(gestureId))
        {
            return string.Empty;
        }

        string value = gestureId.Trim();
        if (LegacyMouseGestureKeyAliases.TryGetValue(value, out string? alias))
        {
            return alias;
        }

        return value.ToUpperInvariant();
    }

    public static Dictionary<string, string> NormalizeMouseGestureCommandMap(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return result;
        }

        foreach ((string key, string value) in source)
        {
            string normalizedKey = NormalizeMouseGestureId(key);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                continue;
            }

            result[normalizedKey] = value ?? string.Empty;
        }

        return result;
    }

    public static string NormalizeFunctionBarLabelText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        char[] normalized = trimmed.ToCharArray();
        for (int i = 0; i < normalized.Length; i++)
        {
            char ch = normalized[i];
            if (ch >= 'A' && ch <= 'Z')
            {
                normalized[i] = char.ToLowerInvariant(ch);
            }
        }

        return new string(normalized);
    }

    public static Dictionary<string, List<string>> NormalizeBrowserKeyCommandOverrides(Dictionary<string, List<string>>? source)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
        {
            return result;
        }

        foreach ((string commandId, List<string> keyGestures) in source)
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                continue;
            }

            string normalizedCommandId = commandId.Trim();
            result[normalizedCommandId] = NormalizeBrowserKeyGestures(keyGestures);
        }

        return result;
    }

    public static bool IsFunctionKeyChordGesture(string? gesture)
    {
        if (!TryParseKeyGesture(gesture, out Keys keyData))
        {
            return false;
        }

        Keys keyCode = keyData & Keys.KeyCode;
        return keyCode >= Keys.F1 && keyCode <= Keys.F12;
    }

    public static bool IsBrowserStructuralReservedGesture(string? gesture)
    {
        if (!TryParseKeyGesture(gesture, out Keys keyData))
        {
            return false;
        }

        return IsBrowserStructuralReservedKeyData(keyData);
    }

    public static bool IsBrowserStructuralReservedKeyData(Keys keyData)
    {
        Keys modifiers = keyData & Keys.Modifiers;
        Keys keyCode = keyData & Keys.KeyCode;

        bool isPlainMainNumber = modifiers == Keys.None && keyCode >= Keys.D1 && keyCode <= Keys.D9;
        bool isPlainNumPadNumber = modifiers == Keys.None && keyCode >= Keys.NumPad1 && keyCode <= Keys.NumPad9;
        bool isCtrlMainDisplayMode = modifiers == Keys.Control && keyCode >= Keys.D1 && keyCode <= Keys.D3;
        bool isCtrlNumPadDisplayMode = modifiers == Keys.Control && keyCode >= Keys.NumPad1 && keyCode <= Keys.NumPad3;

        return isPlainMainNumber || isPlainNumPadNumber || isCtrlMainDisplayMode || isCtrlNumPadDisplayMode;
    }

    public static bool TryParseFunctionKeyChord(string? gesture, out string slotKey, out Keys modifiers)
    {
        slotKey = string.Empty;
        modifiers = Keys.None;
        if (!TryParseKeyGesture(gesture, out Keys keyData))
        {
            return false;
        }

        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode < Keys.F1 || keyCode > Keys.F12)
        {
            return false;
        }

        modifiers = keyData & Keys.Modifiers;
        if (modifiers != Keys.None && modifiers != Keys.Shift && modifiers != Keys.Control && modifiers != Keys.Alt)
        {
            return false;
        }

        slotKey = $"F{(int)(keyCode - Keys.F1) + 1}";
        return true;
    }

    public static void NormalizeAndMigrateFunctionKeyChords(InputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.BrowserKeyCommandOverrides != null)
        {
            if (settings.BrowserKeyCommandOverrides.TryGetValue("file.copy", out var copyGestures) && copyGestures != null)
            {
                copyGestures.RemoveAll(g => string.Equals(NormalizeKeyGestureText(g), "Ctrl+C", StringComparison.OrdinalIgnoreCase));
            }
            if (settings.BrowserKeyCommandOverrides.TryGetValue("file.move", out var moveGestures) && moveGestures != null)
            {
                moveGestures.RemoveAll(g => string.Equals(NormalizeKeyGestureText(g), "Ctrl+X", StringComparison.OrdinalIgnoreCase));
            }
        }
        settings.BrowserKeyCommandOverrides = NormalizeBrowserKeyCommandOverrides(settings.BrowserKeyCommandOverrides);
        settings.FunctionBarCommandOverridesStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        settings.FunctionBarCommandOverridesFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        settings.FunctionBarCommandOverridesShiftStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        settings.FunctionBarCommandOverridesShiftFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        settings.FunctionBarCommandOverridesCtrlStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        settings.FunctionBarCommandOverridesCtrlFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        settings.FunctionBarCommandOverridesAltStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        settings.FunctionBarCommandOverridesAltFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        bool isFdCompatible = string.Equals(settings.FunctionKeyProfile, FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string?> normalMap = isFdCompatible ? settings.FunctionBarCommandOverridesFdCompatible : settings.FunctionBarCommandOverridesStandard;
        Dictionary<string, string?> shiftMap = isFdCompatible ? settings.FunctionBarCommandOverridesShiftFdCompatible : settings.FunctionBarCommandOverridesShiftStandard;
        Dictionary<string, string?> ctrlMap = isFdCompatible ? settings.FunctionBarCommandOverridesCtrlFdCompatible : settings.FunctionBarCommandOverridesCtrlStandard;
        Dictionary<string, string?> altMap = isFdCompatible ? settings.FunctionBarCommandOverridesAltFdCompatible : settings.FunctionBarCommandOverridesAltStandard;

        var nextOverrides = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string commandId, List<string> gestures) in settings.BrowserKeyCommandOverrides)
        {
            List<string> current = NormalizeBrowserKeyGestures(gestures);
            var remaining = new List<string>();
            foreach (string gesture in current)
            {
                if (!TryParseFunctionKeyChord(gesture, out string slotKey, out Keys modifiers))
                {
                    remaining.Add(gesture);
                    continue;
                }

                Dictionary<string, string?> targetMap = modifiers switch
                {
                    Keys.Shift => shiftMap,
                    Keys.Control => ctrlMap,
                    Keys.Alt => altMap,
                    _ => normalMap
                };

                if (modifiers == Keys.Alt && string.Equals(slotKey, "F4", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 明示オーバーライドが既にある場合は既存を優先し、通常キー側からだけ除外する。
                if (!targetMap.ContainsKey(slotKey))
                {
                    targetMap[slotKey] = commandId;
                }
            }

            nextOverrides[commandId] = remaining;
        }

        settings.BrowserKeyCommandOverrides = NormalizeBrowserKeyCommandOverrides(nextOverrides);
    }

    public static List<string> NormalizeBrowserKeyGestures(IEnumerable<string>? source)
    {
        var result = new List<string>();
        if (source == null)
        {
            return result;
        }

        foreach (string? keyGesture in source)
        {
            string normalizedGesture = NormalizeKeyGestureText(keyGesture);
            if (string.IsNullOrWhiteSpace(normalizedGesture))
            {
                continue;
            }

            if (result.Any(static g => string.Equals(g, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.Equals(normalizedGesture, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
            {
                result.Clear();
                result.Add(MouseGestureUnassignedCommandId);
                continue;
            }

            if (!result.Contains(normalizedGesture, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalizedGesture);
            }
        }

        return result;
    }

    public static string NormalizeKeyGestureText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (string.Equals(trimmed, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return MouseGestureUnassignedCommandId;
        }

        if (!TryParseKeyGesture(trimmed, out Keys keyData))
        {
            return string.Empty;
        }

        return ToKeyGestureText(keyData);
    }

    public static bool TryParseKeyGesture(string? value, out Keys keyData)
    {
        keyData = Keys.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        Keys modifiers = Keys.None;
        Keys keyCode = Keys.None;

        foreach (string part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Keys.Control;
                continue;
            }
            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Keys.Shift;
                continue;
            }
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Keys.Alt;
                continue;
            }

            if (keyCode != Keys.None)
            {
                return false;
            }

            if (!Enum.TryParse(part, true, out Keys parsed))
            {
                return false;
            }

            keyCode = parsed & Keys.KeyCode;
        }

        if (keyCode == Keys.None || keyCode == Keys.ControlKey || keyCode == Keys.ShiftKey || keyCode == Keys.Menu)
        {
            return false;
        }

        keyData = keyCode | modifiers;
        return true;
    }

    public static string ToKeyGestureText(Keys keyData)
    {
        Keys modifiers = keyData & Keys.Modifiers;
        Keys keyCode = keyData & Keys.KeyCode;

        var tokens = new List<string>(4);
        if ((modifiers & Keys.Control) != 0) tokens.Add("Ctrl");
        if ((modifiers & Keys.Shift) != 0) tokens.Add("Shift");
        if ((modifiers & Keys.Alt) != 0) tokens.Add("Alt");
        tokens.Add(keyCode.ToString());
        return string.Join("+", tokens);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetDefaultBrowserKeyCommandMap(string? functionKeyProfileValue)
    {
        bool isFdCompatible = string.Equals(functionKeyProfileValue, FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [CommandIds.BrowserNavigateParent] = new[] { "Back", "Alt+Up" },
            [CommandIds.BrowserNavigateBack] = new[] { "Alt+Left" },
            [CommandIds.BrowserNavigateForward] = new[] { "Alt+Right" },
            [CommandIds.BrowserReload] = new[] { "Ctrl+R", "Shift+R" },
            [CommandIds.BrowserMarkAllFiles] = new[] { "Home" },
            [CommandIds.BrowserMarkAllItems] = new[] { "End", "Ctrl+A" },
            [CommandIds.BrowserChangeAttributes] = new[] { "A" },
            [CommandIds.BrowserOpenShell] = new[] { "H" },
            [CommandIds.BrowserOpenExternalEditor] = new[] { "E" },
            [CommandIds.BrowserCreateDirectory] = new[] { "K" },
            [CommandIds.BrowserPreview] = new[] { "V" },
            [CommandIds.BrowserSort] = new[] { "S" },
            [CommandIds.BrowserFilter] = new[] { "F", "Ctrl+F" },
            [CommandIds.BrowserTree] = new[] { "T" },
            [CommandIds.BrowserQuickAccess] = new[] { "Q" },
            [CommandIds.BrowserLogdisk] = new[] { "L" },
            [CommandIds.BrowserOpenMarkSlot] = new[] { "Ctrl+M" },
            [CommandIds.AppOpenCommandLauncher] = new[] { "Ctrl+Shift+P" },
            [CommandIds.AppOpenCommandList] = new[] { "Ctrl+Shift+L" },
            [CommandIds.BrowserShowHelp] = new[] { "Ctrl+H" },
            [CommandIds.AppOpenSystemInformation] = new[] { "I" },
            [CommandIds.BrowserCopyFullPath] = new[] { "Ctrl+Shift+C" },
            [CommandIds.ClipboardPaste] = new[] { "Ctrl+V" },
            [CommandIds.BrowserTabNew] = new[] { "Ctrl+T" },
            [CommandIds.BrowserTabClose] = new[] { "Ctrl+W" },
            [CommandIds.BrowserTabCategoryNext] = new[] { "Ctrl+Shift+Right" },
            [CommandIds.BrowserTabCategoryPrevious] = new[] { "Ctrl+Shift+Left" },
            [CommandIds.ArchivePack] = new[] { "P" },
            [CommandIds.ArchiveUnpack] = new[] { "U" },
            [CommandIds.AppOpenSettings] = new[] { "O" },
            ["file.copy"] = new[] { "C" },
            ["file.move"] = new[] { "M" },
            ["file.rename"] = new[] { "R" },
            ["file.delete"] = new[] { "D", "Delete" },
            [CommandIds.EditUndo] = new[] { "Ctrl+Z", "Alt+Z" },
            [CommandIds.EditRedo] = new[] { "Ctrl+Y", "Alt+Y" }
        };

        if (isFdCompatible)
        {
            map[CommandIds.BrowserOpenShell] = new[] { "H" };
            map[CommandIds.AppOpenSettings] = new[] { "O" };
        }

        return map;
    }
}

public sealed class BrowserKeyOverridesConverter : JsonConverter<Dictionary<string, List<string>>>
{
    public override Dictionary<string, List<string>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                continue;
            }

            List<string> values = property.Value.ValueKind switch
            {
                JsonValueKind.String => InputSettings.NormalizeBrowserKeyGestures(new[] { property.Value.GetString() ?? string.Empty }),
                JsonValueKind.Array => InputSettings.NormalizeBrowserKeyGestures(
                    property.Value.EnumerateArray()
                        .Where(static x => x.ValueKind == JsonValueKind.String)
                        .Select(static x => x.GetString() ?? string.Empty)),
                _ => new List<string>()
            };

            result[property.Name.Trim()] = values;
        }

        return InputSettings.NormalizeBrowserKeyCommandOverrides(result);
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, List<string>> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Dictionary<string, List<string>> normalized = InputSettings.NormalizeBrowserKeyCommandOverrides(value);
        foreach ((string commandId, List<string> gestures) in normalized)
        {
            writer.WritePropertyName(commandId);
            writer.WriteStartArray();
            foreach (string gesture in gestures)
            {
                writer.WriteStringValue(gesture);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

public class FunctionBarLabelOverride
{
    public string CommandId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    public FunctionBarLabelOverride Clone()
    {
        return new FunctionBarLabelOverride
        {
            CommandId = CommandId,
            Label = InputSettings.NormalizeFunctionBarLabelText(Label)
        };
    }
}
