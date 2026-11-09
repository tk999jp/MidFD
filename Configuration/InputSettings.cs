using MidFD.Commands;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace MidFD.Configuration;

public sealed record LegacyBrowserColumnCountOverrideObservation(
    string LegacyCommandId,
    string Gesture,
    int? TargetColumnCount,
    string Status,
    string Reason);

public sealed class BrowserKeyCommandOverridesDictionary : Dictionary<string, List<string>>
{
    public BrowserKeyCommandOverridesDictionary()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public IReadOnlyList<LegacyBrowserColumnCountOverrideObservation> LegacyColumnCountObservations { get; internal set; } = [];
}

public class InputSettings
{
    private static readonly IReadOnlyDictionary<string, string> RegisteredCommandIds =
        new CommandRegistry().GetAll().ToDictionary(
            static command => command.Id,
            static command => command.Id,
            StringComparer.OrdinalIgnoreCase);

    private const string LegacyBrowserCheckExecuteCommandId = "browser.check_execute";
    private Dictionary<string, List<string>> _browserKeyCommandOverrides = new(StringComparer.OrdinalIgnoreCase);
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
    public Dictionary<string, List<string>> BrowserKeyCommandOverrides
    {
        get => _browserKeyCommandOverrides;
        set
        {
            _browserKeyCommandOverrides = value ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (value is BrowserKeyCommandOverridesDictionary normalized && normalized.LegacyColumnCountObservations.Count > 0)
            {
                LegacyColumnCountOverrideObservations = normalized.LegacyColumnCountObservations;
            }
            else if (value is not BrowserKeyCommandOverridesDictionary)
            {
                LegacyColumnCountOverrideObservations = [];
            }
        }
    }
    [JsonIgnore]
    public IReadOnlyList<LegacyBrowserColumnCountOverrideObservation> LegacyColumnCountOverrideObservations { get; private set; } = [];
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

            string normalizedCommandId = NormalizeCommandId(value ?? string.Empty);
            if (string.Equals(normalizedCommandId, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(normalizedCommandId))
            {
                normalizedCommandId = string.Equals(normalizedCommandId, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase)
                    ? MouseGestureUnassignedCommandId
                    : string.Empty;
            }
            else if (!TryGetRegisteredCommandId(normalizedCommandId, out normalizedCommandId))
            {
                normalizedCommandId = MouseGestureUnassignedCommandId;
            }

            if (!result.ContainsKey(normalizedKey) || string.Equals(result[normalizedKey], LegacyBrowserCheckExecuteCommandId, StringComparison.OrdinalIgnoreCase))
            {
                result[normalizedKey] = normalizedCommandId;
            }
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

    public static Dictionary<string, List<string>> NormalizeBrowserKeyCommandOverrides(Dictionary<string, List<string>>? source) =>
        NormalizeBrowserKeyCommandOverridesDetailed(source);

    public static BrowserKeyCommandOverridesDictionary NormalizeBrowserKeyCommandOverridesDetailed(
        Dictionary<string, List<string>>? source)
    {
        var result = new BrowserKeyCommandOverridesDictionary();
        if (source == null)
        {
            return result;
        }

        KeyValuePair<string, List<string>>[] entries = source
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .OrderBy(pair => string.Equals(
                pair.Key.Trim(),
                NormalizeCommandId(pair.Key),
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(pair => NormalizeCommandId(pair.Key), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();

        foreach ((string commandId, List<string> keyGestures) in entries)
        {
            if (string.Equals(commandId.Trim(), CommandIds.LegacyBrowserViewColumnCountSet, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string normalizedCommandId = NormalizeCommandId(commandId);
            if (!TryGetRegisteredCommandId(normalizedCommandId, out normalizedCommandId) ||
                result.ContainsKey(normalizedCommandId))
            {
                continue;
            }

            result[normalizedCommandId] = NormalizeBrowserKeyGestures(keyGestures);
        }

        KeyValuePair<string, List<string>>[] legacyEntries = entries
            .Where(pair => string.Equals(
                pair.Key.Trim(),
                CommandIds.LegacyBrowserViewColumnCountSet,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        var migratedGestures = new Dictionary<int, List<string>>();
        var observations = new List<LegacyBrowserColumnCountOverrideObservation>();

        foreach ((string legacyId, List<string> sourceGestures) in legacyEntries)
        {
            string[] rawGestures = sourceGestures?.ToArray() ?? [];
            if (rawGestures.Length == 0)
            {
                observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                    legacyId,
                    string.Empty,
                    null,
                    "unresolved",
                    "The legacy command had an empty override and carries no column-count target."));
                continue;
            }

            bool hasTombstone = rawGestures.Any(rawGesture => string.Equals(
                NormalizeKeyGestureText(rawGesture),
                MouseGestureUnassignedCommandId,
                StringComparison.OrdinalIgnoreCase));
            if (hasTombstone)
            {
                foreach (string rawGesture in rawGestures)
                {
                    string normalizedGesture = NormalizeKeyGestureText(rawGesture);
                    if (string.Equals(normalizedGesture, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
                    {
                        observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                            legacyId,
                            normalizedGesture,
                            null,
                            "unresolved",
                            "The legacy tombstone disables the old command without identifying a column-count target."));
                    }
                    else if (!TryGetLegacyBrowserColumnCount(normalizedGesture, out int suppressedColumnCount))
                    {
                        observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                            legacyId,
                            rawGesture ?? string.Empty,
                            null,
                            "unresolved",
                            "The legacy gesture is not a recognized target and the command is tombstoned."));
                    }
                    else
                    {
                        observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                            legacyId,
                            normalizedGesture,
                            suppressedColumnCount,
                            "suppressed-by-tombstone",
                            "The legacy command tombstone takes precedence over its numeric gestures."));
                    }
                }

                continue;
            }

            foreach (string rawGesture in rawGestures)
            {
                string gesture = NormalizeKeyGestureText(rawGesture);
                if (string.IsNullOrWhiteSpace(gesture))
                {
                    observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                        legacyId,
                        rawGesture ?? string.Empty,
                        null,
                        "unresolved",
                        "The legacy gesture is not a recognized key gesture."));
                    continue;
                }

                if (string.Equals(gesture, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
                {
                    observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                        legacyId,
                        gesture,
                        null,
                        "unresolved",
                        "The legacy tombstone has no column-count target."));
                    continue;
                }

                if (!TryGetLegacyBrowserColumnCount(gesture, out int columnCount))
                {
                    observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                        legacyId,
                        gesture,
                        null,
                        "unresolved",
                        "The legacy gesture does not identify a D1-D9 or NumPad1-NumPad9 target."));
                    continue;
                }

                string targetCommandId = CommandIds.GetBrowserColumnCountCommandId(columnCount);
                if (result.ContainsKey(targetCommandId))
                {
                    observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                        legacyId,
                        gesture,
                        columnCount,
                        "canonical-override-retained",
                        "An existing individual command override takes precedence."));
                    continue;
                }

                if (!migratedGestures.TryGetValue(columnCount, out List<string>? targetGestures))
                {
                    targetGestures = [];
                    migratedGestures.Add(columnCount, targetGestures);
                }
                targetGestures.Add(gesture);
                observations.Add(new LegacyBrowserColumnCountOverrideObservation(
                    legacyId,
                    gesture,
                    columnCount,
                    "migrated",
                    "The physical numeric key identifies the individual column-count command."));
            }
        }

        foreach ((int columnCount, List<string> gestures) in migratedGestures.OrderBy(static pair => pair.Key))
        {
            string commandId = CommandIds.GetBrowserColumnCountCommandId(columnCount);
            if (!result.ContainsKey(commandId))
            {
                result[commandId] = NormalizeBrowserKeyGestures(gestures);
            }
        }

        result.LegacyColumnCountObservations = observations;
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

    public static bool TryGetLegacyBrowserColumnCount(string? gesture, out int columnCount)
    {
        if (!TryParseKeyGesture(gesture, out Keys keyData))
        {
            columnCount = 0;
            return false;
        }

        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode >= Keys.D1 && keyCode <= Keys.D9)
        {
            columnCount = (int)(keyCode - Keys.D0);
            return true;
        }

        if (keyCode >= Keys.NumPad1 && keyCode <= Keys.NumPad9)
        {
            columnCount = (int)(keyCode - Keys.NumPad0);
            return true;
        }

        columnCount = 0;
        return false;
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
        return isPlainMainNumber;
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
            if (settings.BrowserKeyCommandOverrides.TryGetValue(CommandIds.FileCopy, out var copyGestures) && copyGestures != null)
            {
                copyGestures.RemoveAll(g => string.Equals(NormalizeKeyGestureText(g), "Ctrl+C", StringComparison.OrdinalIgnoreCase));
            }
            if (settings.BrowserKeyCommandOverrides.TryGetValue(CommandIds.FileMove, out var moveGestures) && moveGestures != null)
            {
                moveGestures.RemoveAll(g => string.Equals(NormalizeKeyGestureText(g), "Ctrl+X", StringComparison.OrdinalIgnoreCase));
            }
        }
        settings.NormalizeCommandReferences();

        bool isFdCompatible = string.Equals(settings.FunctionKeyProfile, FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string?> normalMap = isFdCompatible ? settings.FunctionBarCommandOverridesFdCompatible : settings.FunctionBarCommandOverridesStandard;
        Dictionary<string, string?> shiftMap = isFdCompatible ? settings.FunctionBarCommandOverridesShiftFdCompatible : settings.FunctionBarCommandOverridesShiftStandard;
        Dictionary<string, string?> ctrlMap = isFdCompatible ? settings.FunctionBarCommandOverridesCtrlFdCompatible : settings.FunctionBarCommandOverridesCtrlStandard;
        Dictionary<string, string?> altMap = isFdCompatible ? settings.FunctionBarCommandOverridesAltFdCompatible : settings.FunctionBarCommandOverridesAltStandard;

        var nextOverrides = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> browserKeyOverrides = settings.BrowserKeyCommandOverrides
            ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string commandId, List<string> gestures) in browserKeyOverrides)
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
        settings.NormalizeCommandReferences();
    }

    public void NormalizeCommandReferences()
    {
        MouseGestureCommandMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BrowserKeyCommandOverrides ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        FunctionBarCommandOverridesStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FunctionBarCommandOverridesFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FunctionBarCommandOverridesShiftStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FunctionBarCommandOverridesShiftFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FunctionBarCommandOverridesCtrlStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FunctionBarCommandOverridesCtrlFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FunctionBarCommandOverridesAltStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FunctionBarCommandOverridesAltFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FunctionBarLabelOverridesStandard ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        FunctionBarLabelOverridesFdCompatible ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        FunctionBarLabelOverridesShiftStandard ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        FunctionBarLabelOverridesShiftFdCompatible ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        FunctionBarLabelOverridesCtrlStandard ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        FunctionBarLabelOverridesCtrlFdCompatible ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        FunctionBarLabelOverridesAltStandard ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        FunctionBarLabelOverridesAltFdCompatible ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);

        MigrateLegacyBrowserCheckExecuteCommand(this);
        MouseGestureCommandMap = NormalizeMouseGestureCommandMap(MouseGestureCommandMap);
        BrowserKeyCommandOverrides = NormalizeBrowserKeyCommandOverrides(BrowserKeyCommandOverrides);

        NormalizeFunctionBarCommandOverrides(FunctionBarCommandOverridesStandard);
        NormalizeFunctionBarCommandOverrides(FunctionBarCommandOverridesFdCompatible);
        NormalizeFunctionBarCommandOverrides(FunctionBarCommandOverridesShiftStandard);
        NormalizeFunctionBarCommandOverrides(FunctionBarCommandOverridesShiftFdCompatible);
        NormalizeFunctionBarCommandOverrides(FunctionBarCommandOverridesCtrlStandard);
        NormalizeFunctionBarCommandOverrides(FunctionBarCommandOverridesCtrlFdCompatible);
        NormalizeFunctionBarCommandOverrides(FunctionBarCommandOverridesAltStandard);
        NormalizeFunctionBarCommandOverrides(FunctionBarCommandOverridesAltFdCompatible);

        NormalizeFunctionBarLabelOverrides(FunctionBarLabelOverridesStandard);
        NormalizeFunctionBarLabelOverrides(FunctionBarLabelOverridesFdCompatible);
        NormalizeFunctionBarLabelOverrides(FunctionBarLabelOverridesShiftStandard);
        NormalizeFunctionBarLabelOverrides(FunctionBarLabelOverridesShiftFdCompatible);
        NormalizeFunctionBarLabelOverrides(FunctionBarLabelOverridesCtrlStandard);
        NormalizeFunctionBarLabelOverrides(FunctionBarLabelOverridesCtrlFdCompatible);
        NormalizeFunctionBarLabelOverrides(FunctionBarLabelOverridesAltStandard);
        NormalizeFunctionBarLabelOverrides(FunctionBarLabelOverridesAltFdCompatible);
    }

    private static void NormalizeFunctionBarCommandOverrides(Dictionary<string, string?> map)
    {
        MigrateLegacyCommandId(map);
        foreach (string key in map.Keys.ToArray())
        {
            if (IsFunctionBarSlotKey(key))
            {
                string? commandId = map[key];
                if (string.IsNullOrWhiteSpace(commandId) ||
                    string.Equals(commandId, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                map[key] = TryGetRegisteredCommandId(commandId, out string canonicalCommandId)
                    ? canonicalCommandId
                    : MouseGestureUnassignedCommandId;
                continue;
            }

            if (LooksLikeCommandId(key))
            {
                if (!TryGetRegisteredCommandId(key, out string canonicalCommandId))
                {
                    map.Remove(key);
                }
                else
                {
                    MoveCommandIdKeyToCanonical(map, key, canonicalCommandId);
                }
            }
        }
    }

    private static void NormalizeFunctionBarLabelOverrides(Dictionary<string, FunctionBarLabelOverride> map)
    {
        MigrateLegacyCommandId(map);
        foreach (string key in map.Keys.ToArray())
        {
            if (!IsFunctionBarSlotKey(key) && LooksLikeCommandId(key))
            {
                if (!TryGetRegisteredCommandId(key, out string canonicalCommandId))
                {
                    map.Remove(key);
                    continue;
                }

                MoveCommandIdKeyToCanonical(map, key, canonicalCommandId);
            }
        }

        foreach (string key in map.Keys.ToArray())
        {
            FunctionBarLabelOverride? labelOverride = map[key];
            string? commandId = labelOverride?.CommandId;
            if (labelOverride == null || string.IsNullOrWhiteSpace(commandId) ||
                string.Equals(commandId, MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetRegisteredCommandId(commandId, out string canonicalCommandId))
            {
                labelOverride.CommandId = canonicalCommandId;
            }
            else
            {
                map.Remove(key);
            }
        }
    }

    private static void MoveCommandIdKeyToCanonical<T>(Dictionary<string, T> map, string key, string canonicalCommandId)
    {
        if (string.Equals(key, canonicalCommandId, StringComparison.Ordinal))
        {
            return;
        }

        T value = map[key];
        map.Remove(key);
        if (!map.ContainsKey(canonicalCommandId))
        {
            map[canonicalCommandId] = value;
        }
    }

    private static bool IsFunctionBarSlotKey(string key)
    {
        return key.Length >= 2 &&
               (key[0] == 'F' || key[0] == 'f') &&
               int.TryParse(key.AsSpan(1), out int slot) &&
               slot is >= 1 and <= 12;
    }

    private static bool LooksLikeCommandId(string value)
    {
        return value.Contains('.', StringComparison.Ordinal);
    }

    private static bool TryGetRegisteredCommandId(string? commandId, out string canonicalCommandId)
    {
        string normalized = NormalizeCommandId(commandId ?? string.Empty);
        if (RegisteredCommandIds.TryGetValue(normalized, out string? registeredCommandId))
        {
            canonicalCommandId = registeredCommandId;
            return true;
        }

        canonicalCommandId = string.Empty;
        return false;
    }

    private static void MigrateLegacyBrowserCheckExecuteCommand(InputSettings settings)
    {
        Dictionary<string, List<string>> overrides = settings.BrowserKeyCommandOverrides
            ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        settings.BrowserKeyCommandOverrides = overrides;

        if (overrides.TryGetValue(CommandIds.BrowserExecute, out List<string>? legacyExecuteGestures) &&
            !overrides.ContainsKey(CommandIds.BrowserDefaultOpen) &&
            !overrides.ContainsKey(CommandIds.BrowserOpenCommandDialog) &&
            !overrides.ContainsKey(LegacyBrowserCheckExecuteCommandId))
        {
            List<string> normalized = NormalizeBrowserKeyGestures(legacyExecuteGestures);
            if (normalized.Count == 1 && string.Equals(normalized[0], "X", StringComparison.OrdinalIgnoreCase))
            {
                overrides.Remove(CommandIds.BrowserExecute);
                overrides[CommandIds.BrowserOpenCommandDialog] = normalized;
            }
        }

        if (!overrides.TryGetValue(LegacyBrowserCheckExecuteCommandId, out List<string>? legacyGestures))
        {
            return;
        }

        if (!overrides.ContainsKey(CommandIds.BrowserExecute))
        {
            overrides[CommandIds.BrowserExecute] = NormalizeBrowserKeyGestures(legacyGestures);
        }
        overrides.Remove(LegacyBrowserCheckExecuteCommandId);
    }

    private static string NormalizeCommandId(string commandId)
    {
        string normalized = commandId.Trim();
        if (string.Equals(normalized, LegacyBrowserCheckExecuteCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return CommandIds.BrowserExecute;
        }
        if (string.Equals(normalized, CommandIds.BrowserTabFilterLock, StringComparison.OrdinalIgnoreCase))
        {
            return CommandIds.BrowserFilter;
        }
        if (string.Equals(normalized, CommandIds.BrowserTabFilterLockClear, StringComparison.OrdinalIgnoreCase))
        {
            return CommandIds.BrowserFilterClear;
        }
        return normalized;
    }

    private static void MigrateLegacyCommandId<T>(Dictionary<string, T> map)
    {
        foreach (string legacyId in map.Keys.ToArray())
        {
            string normalizedId = NormalizeCommandId(legacyId);
            if (string.Equals(legacyId, normalizedId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!map.ContainsKey(normalizedId) && map.TryGetValue(legacyId, out T? legacyValue))
            {
                map[normalizedId] = legacyValue;
            }
            map.Remove(legacyId);
        }
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
            [CommandIds.BrowserNavigateBack] = new[] { "Alt+Left", "Ctrl+Back" },
            [CommandIds.BrowserNavigateForward] = new[] { "Alt+Right" },
            [CommandIds.BrowserReload] = new[] { "Ctrl+R", "Shift+R" },
            [CommandIds.BrowserMarkAllFiles] = new[] { "Home" },
            [CommandIds.BrowserMarkAllItems] = new[] { "End", "Ctrl+A" },
            [CommandIds.BrowserExecute] = new[] { "Enter" },
            [CommandIds.BrowserDefaultOpen] = new[] { "Z" },
            [CommandIds.BrowserOpenCommandDialog] = new[] { "X" },
            [CommandIds.BrowserTabPrevious] = new[] { "Ctrl+Left", "Ctrl+Shift+Tab" },
            [CommandIds.BrowserTabNext] = new[] { "Ctrl+Right", "Ctrl+Tab" },
            [CommandIds.BrowserTabHistoryBack] = new[] { "Alt+Shift+Left" },
            [CommandIds.BrowserTabHistoryForward] = new[] { "Alt+Shift+Right" },
            [CommandIds.BrowserChangeAttributes] = new[] { "A" },
            [CommandIds.BrowserOpenShell] = new[] { "H" },
            [CommandIds.BrowserOpenCommandPrompt] = new[] { "Shift+H" },
            [CommandIds.BrowserOpenExternalEditor] = isFdCompatible
                ? new[] { "E", "Shift+Enter" }
                : new[] { "E" },
            [CommandIds.BrowserCreateDirectory] = new[] { "K" },
            [CommandIds.BrowserCreateFile] = new[] { "N" },
            [CommandIds.BrowserPreview] = new[] { "V" },
            [CommandIds.BrowserSort] = new[] { "S" },
            [CommandIds.BrowserFilter] = new[] { "F" },
            [CommandIds.BrowserSearch] = new[] { "Ctrl+F" },
            [CommandIds.BrowserTree] = new[] { "T" },
            [CommandIds.BrowserQuickAccess] = new[] { "Q" },
            [CommandIds.BrowserLogdisk] = new[] { "L" },
            [CommandIds.BrowserOpenMarkSlot] = new[] { "Ctrl+M" },
            [CommandIds.BrowserPathEntryOpen] = new[] { "Ctrl+L" },
            [CommandIds.AppOpenCommandLauncher] = new[] { "Ctrl+Shift+P" },
            [CommandIds.BrowserTabLock] = new[] { "Ctrl+Shift+L" },
            [CommandIds.BrowserShowHelp] = new[] { "Ctrl+H" },
            [CommandIds.AppOpenSystemInformation] = new[] { "I" },
            [CommandIds.BrowserCopyFullPath] = new[] { "Ctrl+Shift+C" },
            [CommandIds.BrowserCopyCurrentPath] = Array.Empty<string>(),
            [CommandIds.ClipboardPaste] = new[] { "Ctrl+V" },
            [CommandIds.ClipboardCut] = new[] { "Ctrl+X" },
            [CommandIds.BrowserTabNew] = new[] { "Ctrl+T" },
            [CommandIds.BrowserTabClose] = new[] { "Ctrl+W" },
            [CommandIds.BrowserTabLayoutToggle] = Array.Empty<string>(),
            [CommandIds.BrowserTabReadOnlyToggle] = Array.Empty<string>(),
            [CommandIds.BrowserTabCategoryAdd] = new[] { "Ctrl+Shift+N" },
            [CommandIds.BrowserCursorTop] = new[] { "Ctrl+Home" },
            [CommandIds.BrowserCursorBottom] = new[] { "Ctrl+End" },
            [CommandIds.BrowserTabCategoryMoveLeft] = new[] { "Ctrl+Alt+Left" },
            [CommandIds.BrowserTabCategoryMoveRight] = new[] { "Ctrl+Alt+Right" },
            [CommandIds.BrowserTabCategoryPrevious] = new[] { "Ctrl+Shift+Left", "Ctrl+Up" },
            [CommandIds.BrowserTabCategoryNext] = new[] { "Ctrl+Shift+Right", "Ctrl+Down" },
            [CommandIds.BrowserTabCategoryManage] = Array.Empty<string>(),
            [CommandIds.ArchivePack] = new[] { "P" },
            [CommandIds.ArchiveUnpack] = new[] { "U" },
            [CommandIds.AppOpenSettings] = new[] { "O" },
            [CommandIds.FileCopy] = new[] { "C" },
            [CommandIds.FileMove] = new[] { "M" },
            [CommandIds.FileRename] = new[] { "R" },
            [CommandIds.FileDelete] = new[] { "D", "Delete" },
            [CommandIds.FileDeletePermanent] = new[] { "Shift+D", "Shift+Delete" },
            [CommandIds.BrowserProperties] = new[] { "Alt+Enter" },
            [CommandIds.EditUndo] = new[] { "Ctrl+Z", "Alt+Z" },
            [CommandIds.EditRedo] = new[] { "Ctrl+Y", "Alt+Y" },
            [CommandIds.AppOpenManagedTrash] = Array.Empty<string>()
        };

        for (int columnCount = 1; columnCount <= 9; columnCount++)
        {
            map[CommandIds.GetBrowserColumnCountCommandId(columnCount)] = [$"NumPad{columnCount}"];
        }

        if (isFdCompatible)
        {
            map[CommandIds.BrowserOpenShell] = new[] { "H" };
            map[CommandIds.AppOpenSettings] = new[] { "O" };
            for (int drive = 1; drive <= 9; drive++)
            {
                map[CommandIds.GetBrowserDriveCommandId(drive)] = new[]
                {
                    $"Ctrl+D{drive}",
                    $"Ctrl+NumPad{drive}"
                };
            }
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
            return InputSettings.NormalizeBrowserKeyCommandOverridesDetailed(null);
        }

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return InputSettings.NormalizeBrowserKeyCommandOverridesDetailed(null);
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                continue;
            }

            bool isLegacyColumnCount = string.Equals(
                property.Name.Trim(),
                CommandIds.LegacyBrowserViewColumnCountSet,
                StringComparison.OrdinalIgnoreCase);
            IEnumerable<string> rawValues = property.Value.ValueKind switch
            {
                JsonValueKind.String => new[] { property.Value.GetString() ?? string.Empty },
                JsonValueKind.Array =>
                    property.Value.EnumerateArray()
                        .Where(static x => x.ValueKind == JsonValueKind.String)
                        .Select(static x => x.GetString() ?? string.Empty),
                _ => Array.Empty<string>()
            };

            List<string> values = isLegacyColumnCount
                ? rawValues.ToList()
                : InputSettings.NormalizeBrowserKeyGestures(rawValues);
            result[property.Name.Trim()] = values;
        }

        return InputSettings.NormalizeBrowserKeyCommandOverridesDetailed(result);
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
