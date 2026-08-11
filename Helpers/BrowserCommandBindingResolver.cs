using MidFD.Commands;
using MidFD.Configuration;

namespace MidFD.Helpers;

public static class BrowserCommandBindingResolver
{
    public enum Resolution
    {
        NotMatched,
        MatchedExecuted,
        MatchedRejected
    }

    public static Dictionary<string, string> ResolveEffectiveKeyCommandMap(
        string? functionKeyProfileValue,
        Dictionary<string, List<string>>? overrides,
        CommandRegistry commandRegistry,
        string? legacyCommandLauncherShortcut = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults =
            InputSettings.GetDefaultBrowserKeyCommandMap(functionKeyProfileValue);

        foreach ((string commandId, IReadOnlyList<string> gestures) in defaults)
        {
            foreach (string gesture in gestures.Select(InputSettings.NormalizeKeyGestureText))
            {
                if (IsUsableKeyboardGesture(gesture))
                {
                    result[gesture] = commandId;
                }
            }
        }

        var normalizedOverrides = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (overrides != null)
        {
            foreach ((string commandId, List<string> gestures) in overrides)
            {
                if (commandRegistry.Find(commandId) is { } definition &&
                    (definition.InputSurfaces & CommandInputSurface.Keyboard) != 0)
                {
                    normalizedOverrides[commandId] = InputSettings.NormalizeBrowserKeyGestures(gestures);
                }
            }
        }

        // An override owns the command's default slots, including an empty-list tombstone.
        foreach (string commandId in normalizedOverrides.Keys.OrderBy(static id => id, StringComparer.Ordinal))
        {
            foreach (string gesture in result
                         .Where(pair => string.Equals(pair.Value, commandId, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                result.Remove(gesture);
            }
        }

        // Empty-list and explicit none both consume the command's former default gestures.
        foreach (string commandId in normalizedOverrides.Keys.OrderBy(static id => id, StringComparer.Ordinal))
        {
            List<string> gestures = normalizedOverrides[commandId];
            if (gestures.Count == 0 || HasTombstone(gestures))
            {
                if (defaults.TryGetValue(commandId, out IReadOnlyList<string>? defaultGestures))
                {
                    foreach (string gesture in defaultGestures.Select(InputSettings.NormalizeKeyGestureText))
                    {
                        if (IsUsableKeyboardGesture(gesture))
                        {
                            result[gesture] = InputSettings.MouseGestureUnassignedCommandId;
                        }
                    }
                }
            }
        }

        // Explicit assignments win over tombstones and are resolved by command ID,
        // making the result independent of JSON/dictionary enumeration order.
        var explicitClaims = normalizedOverrides
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(pair => pair.Value.Count > 0 && !HasTombstone(pair.Value))
            .SelectMany(pair => pair.Value
                .Where(IsUsableKeyboardGesture)
                .Select(gesture => (Gesture: gesture, CommandId: pair.Key)))
            .GroupBy(pair => pair.Gesture, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(pair => pair.CommandId, StringComparer.Ordinal).First())
            .ToArray();

        foreach ((string gesture, string commandId) in explicitClaims)
        {
            result[gesture] = commandId;
        }

        ApplyLegacyCommandLauncherCompatibility(result, defaults, normalizedOverrides, legacyCommandLauncherShortcut);
        return result;
    }

    private static void ApplyLegacyCommandLauncherCompatibility(
        Dictionary<string, string> result,
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults,
        IReadOnlyDictionary<string, List<string>> overrides,
        string? legacyShortcut)
    {
        if (string.IsNullOrWhiteSpace(legacyShortcut))
        {
            return;
        }

        string normalized = InputSettings.NormalizeKeyGestureText(legacyShortcut);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (overrides.Keys.Any(id => string.Equals(id, CommandIds.AppOpenCommandLauncher, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // A present legacy value replaces profile defaults. Conflict checks below
        // must not leave the profile launcher binding behind.
        foreach (string gesture in result
                     .Where(pair => string.Equals(pair.Value, CommandIds.AppOpenCommandLauncher, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            result.Remove(gesture);
        }

        string launcherGesture = "Ctrl+Shift+P";
        if (string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase) &&
            overrides.Values.Any(gestures => gestures.Any(gesture =>
                string.Equals(InputSettings.NormalizeKeyGestureText(gesture), launcherGesture, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        // Any explicit owner, including a tombstone, is authoritative over the legacy field.
        if (overrides.Values.Any(gestures => gestures.Any(gesture =>
                string.Equals(InputSettings.NormalizeKeyGestureText(gesture), normalized, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        // An empty-list tombstone has no gesture value, so recognize the defaults
        // it intentionally suppresses before applying the legacy fallback.
        if (!string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase) &&
            overrides.Any(pair =>
                (pair.Value.Count == 0 || HasTombstone(pair.Value)) &&
                defaults.TryGetValue(pair.Key, out IReadOnlyList<string>? defaultGestures) &&
                defaultGestures.Any(gesture =>
                    string.Equals(InputSettings.NormalizeKeyGestureText(gesture), normalized, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        if (string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase))
        {
            result[launcherGesture] = InputSettings.MouseGestureUnassignedCommandId;
        }
        else if (IsUsableKeyboardGesture(normalized))
        {
            result[normalized] = CommandIds.AppOpenCommandLauncher;
        }
    }

    private static bool HasTombstone(IEnumerable<string> gestures)
    {
        return gestures.Any(static gesture =>
            string.Equals(gesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUsableKeyboardGesture(string? gesture)
    {
        return !string.IsNullOrWhiteSpace(gesture) &&
               !InputSettings.IsFunctionKeyChordGesture(gesture) &&
               !InputSettings.IsBrowserStructuralReservedGesture(gesture) &&
               !string.Equals(gesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase);
    }
}
