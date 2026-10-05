using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Services;
using System.Windows.Forms;

namespace MidFD.Presentation;

internal sealed record BrowserKeyAssignmentRow(
    string Gesture,
    string CommandId,
    string FeatureName,
    string Category,
    string State,
    string Description);

internal static class BrowserKeyAssignmentProjection
{
    internal static IReadOnlyList<BrowserKeyAssignmentRow> CreateRows(
        string? profileValue,
        Dictionary<string, List<string>>? overrides,
        CommandRegistry registry,
        string? legacyCommandLauncherShortcut = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(profileValue);
        Dictionary<string, string> effective = BrowserCommandBindingResolver.ResolveEffectiveKeyCommandMap(
            profileValue,
            overrides,
            registry,
            legacyCommandLauncherShortcut);

        return SortRows(effective
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                           !string.Equals(pair.Value, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase) &&
                           !InputSettings.IsFunctionKeyChordGesture(pair.Key))
            .Select(pair =>
            {
                CommandDefinition? command = registry.Find(pair.Value);
                if (command == null)
                {
                    return null;
                }

                bool isDefault = defaults.TryGetValue(command.Id, out IReadOnlyList<string>? defaultGestures) &&
                                 defaultGestures.Any(gesture => string.Equals(
                                     InputSettings.NormalizeKeyGestureText(gesture),
                                     pair.Key,
                                     StringComparison.OrdinalIgnoreCase));
                return new BrowserKeyAssignmentRow(
                    InputSettings.NormalizeKeyGestureText(pair.Key),
                    command.Id,
                    FunctionKeyProfileService.ResolveCommandDisplayText(command),
                    command.Scope.ToString(),
                    isDefault ? "既定" : "カスタム",
                    command.Description);
            })
            .Where(static row => row != null)
            .Cast<BrowserKeyAssignmentRow>());
    }

    internal static IReadOnlyList<BrowserKeyAssignmentRow> SortRows(IEnumerable<BrowserKeyAssignmentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows
            .OrderBy(static row => GetModifierGroup(row.Gesture))
            .ThenBy(static row => GetModifierCombinationOrder(row.Gesture))
            .ThenBy(static row => GetKeyCodeOrder(row.Gesture))
            .ThenBy(static row => row.Gesture, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.CommandId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static Dictionary<string, List<string>> SetGestureOwner(
        string? profileValue,
        Dictionary<string, List<string>>? sourceOverrides,
        CommandRegistry registry,
        string? legacyCommandLauncherShortcut,
        string gesture,
        string? targetCommandId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        string normalizedGesture = InputSettings.NormalizeKeyGestureText(gesture);
        if (string.IsNullOrWhiteSpace(normalizedGesture) ||
            InputSettings.IsFunctionKeyChordGesture(normalizedGesture) ||
            InputSettings.IsBrowserStructuralReservedGesture(normalizedGesture))
        {
            throw new ArgumentException("The key gesture cannot be assigned through the Browser key view.", nameof(gesture));
        }

        CommandDefinition? target = null;
        if (targetCommandId != null)
        {
            target = registry.Find(targetCommandId);
            if (target == null || (target.InputSurfaces & CommandInputSurface.Keyboard) == 0)
            {
                throw new ArgumentException("The target command is not assignable from the Browser key view.", nameof(targetCommandId));
            }
        }

        Dictionary<string, List<string>> next = InputSettings.NormalizeBrowserKeyCommandOverrides(sourceOverrides);
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(profileValue);
        Dictionary<string, string> effective = BrowserCommandBindingResolver.ResolveEffectiveKeyCommandMap(
            profileValue,
            next,
            registry,
            legacyCommandLauncherShortcut);
        string? currentOwner = effective.TryGetValue(normalizedGesture, out string? currentCommandId) &&
                               !string.Equals(currentCommandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase)
            ? currentCommandId
            : null;

        if (string.Equals(currentOwner, target?.Id, StringComparison.OrdinalIgnoreCase))
        {
            return next;
        }

        bool removesLegacyLauncherGesture =
            string.Equals(currentOwner, CommandIds.AppOpenCommandLauncher, StringComparison.OrdinalIgnoreCase) &&
            !next.ContainsKey(CommandIds.AppOpenCommandLauncher) &&
            string.Equals(InputSettings.NormalizeKeyGestureText(legacyCommandLauncherShortcut), normalizedGesture, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(currentOwner))
        {
            List<string> currentGestures = GetCommandGestures(currentOwner, next, defaults, legacyCommandLauncherShortcut);
            currentGestures.RemoveAll(key => string.Equals(key, normalizedGesture, StringComparison.OrdinalIgnoreCase));
            StoreCommandGestures(next, currentOwner, currentGestures, defaults, forceOverride: removesLegacyLauncherGesture);
        }

        if (target != null)
        {
            List<string> targetGestures = GetCommandGestures(target.Id, next, defaults, legacyCommandLauncherShortcut);
            if (!targetGestures.Contains(normalizedGesture, StringComparer.OrdinalIgnoreCase))
            {
                targetGestures.Add(normalizedGesture);
            }
            StoreCommandGestures(next, target.Id, targetGestures, defaults);
        }

        return InputSettings.NormalizeBrowserKeyCommandOverrides(next);
    }

    internal static string? FindDefaultOwner(string? profileValue, string gesture)
    {
        string normalizedGesture = InputSettings.NormalizeKeyGestureText(gesture);
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(profileValue);
        return defaults
            .Where(pair => pair.Value.Any(key => string.Equals(
                InputSettings.NormalizeKeyGestureText(key),
                normalizedGesture,
                StringComparison.OrdinalIgnoreCase)))
            .Select(static pair => pair.Key)
            .OrderBy(static commandId => commandId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static List<string> GetCommandGestures(
        string commandId,
        IReadOnlyDictionary<string, List<string>> overrides,
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults,
        string? legacyCommandLauncherShortcut)
    {
        List<string> gestures = overrides.TryGetValue(commandId, out List<string>? overridden)
            ? InputSettings.NormalizeBrowserKeyGestures(overridden)
            : defaults.TryGetValue(commandId, out IReadOnlyList<string>? defaultGestures)
                ? InputSettings.NormalizeBrowserKeyGestures(defaultGestures)
                : new List<string>();

        if (string.Equals(commandId, CommandIds.AppOpenCommandLauncher, StringComparison.OrdinalIgnoreCase) &&
            !overrides.ContainsKey(commandId))
        {
            string legacyGesture = InputSettings.NormalizeKeyGestureText(legacyCommandLauncherShortcut);
            if (!string.IsNullOrWhiteSpace(legacyGesture) &&
                !string.Equals(legacyGesture, "None", StringComparison.OrdinalIgnoreCase) &&
                !gestures.Contains(legacyGesture, StringComparer.OrdinalIgnoreCase))
            {
                gestures.Add(legacyGesture);
            }
        }

        return gestures;
    }

    private static void StoreCommandGestures(
        IDictionary<string, List<string>> overrides,
        string commandId,
        IEnumerable<string> gestures,
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults,
        bool forceOverride = false)
    {
        List<string> normalized = InputSettings.NormalizeBrowserKeyGestures(gestures);
        List<string> defaultGestures = defaults.TryGetValue(commandId, out IReadOnlyList<string>? values)
            ? InputSettings.NormalizeBrowserKeyGestures(values)
            : new List<string>();
        if (!forceOverride && normalized.SequenceEqual(defaultGestures, StringComparer.OrdinalIgnoreCase))
        {
            overrides.Remove(commandId);
        }
        else
        {
            overrides[commandId] = normalized;
        }
    }

    private static int GetModifierGroup(string gesture)
    {
        if (!InputSettings.TryParseKeyGesture(gesture, out Keys keyData))
        {
            return int.MaxValue;
        }

        return (keyData & Keys.Modifiers) switch
        {
            Keys.None => 0,
            Keys.Control => 1,
            Keys.Shift => 2,
            Keys.Alt => 3,
            _ => 4
        };
    }

    private static int GetModifierCombinationOrder(string gesture)
    {
        if (!InputSettings.TryParseKeyGesture(gesture, out Keys keyData))
        {
            return int.MaxValue;
        }

        Keys modifiers = keyData & Keys.Modifiers;
        return ((modifiers & Keys.Control) != 0 ? 1 : 0) |
               ((modifiers & Keys.Shift) != 0 ? 2 : 0) |
               ((modifiers & Keys.Alt) != 0 ? 4 : 0);
    }

    private static int GetKeyCodeOrder(string gesture)
    {
        return InputSettings.TryParseKeyGesture(gesture, out Keys keyData)
            ? (int)(keyData & Keys.KeyCode)
            : int.MaxValue;
    }
}
