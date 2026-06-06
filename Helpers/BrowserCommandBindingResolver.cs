using System;
using System.Collections.Generic;
using System.Linq;
using MidFD.Commands;
using MidFD.Configuration;

namespace MidFD.Helpers;

public static class BrowserCommandBindingResolver
{
    public static Dictionary<string, string> ResolveEffectiveKeyCommandMap(
        string? functionKeyProfileValue,
        Dictionary<string, List<string>>? overrides,
        CommandRegistry commandRegistry)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(functionKeyProfileValue);
        foreach ((string commandId, IReadOnlyList<string> keyGestures) in defaults)
        {
            foreach (string keyGesture in keyGestures)
            {
                string normalizedGesture = InputSettings.NormalizeKeyGestureText(keyGesture);
                if (!string.IsNullOrWhiteSpace(normalizedGesture) &&
                    !InputSettings.IsFunctionKeyChordGesture(normalizedGesture) &&
                    !InputSettings.IsBrowserStructuralReservedGesture(normalizedGesture) &&
                    !string.Equals(normalizedGesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
                {
                    result[normalizedGesture] = commandId;
                }
            }
        }

        if (overrides == null)
        {
            return result;
        }

        foreach ((string commandId, List<string> keyGestures) in overrides)
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                continue;
            }

            CommandDefinition? definition = commandRegistry.Find(commandId);
            if (definition == null || !definition.IsCustomizable)
            {
                continue;
            }

            foreach ((string boundGesture, string boundCommand) in result.Where(x => string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                result.Remove(boundGesture);
            }

            List<string> normalizedGestures = InputSettings.NormalizeBrowserKeyGestures(keyGestures);
            if (normalizedGestures.Count == 0 ||
                normalizedGestures.Any(static x => string.Equals(x, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (string normalizedGesture in normalizedGestures)
            {
                if (InputSettings.IsFunctionKeyChordGesture(normalizedGesture))
                {
                    continue;
                }
                if (InputSettings.IsBrowserStructuralReservedGesture(normalizedGesture))
                {
                    continue;
                }
                result[normalizedGesture] = commandId;
            }
        }

        return result;
    }
}
