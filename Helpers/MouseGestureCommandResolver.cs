using System;
using System.Collections.Generic;
using MidFD.Configuration;

namespace MidFD.Helpers;

public static class MouseGestureCommandResolver
{
    public static bool TryResolveCommandId(
        string? gesture,
        Dictionary<string, string>? configuredMap,
        out string commandId)
    {
        commandId = string.Empty;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        string normalizedGesture = InputSettings.NormalizeMouseGestureId(gesture);
        if (configuredMap != null && configuredMap.TryGetValue(normalizedGesture, out string? configuredId))
        {
            commandId = NormalizeCommandId(configuredId);
        }
        else if (InputSettings.DefaultMouseGestureCommandMap.TryGetValue(normalizedGesture, out string? defaultId))
        {
            commandId = NormalizeCommandId(defaultId);
        }
        else
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(commandId))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeCommandId(string? commandId)
    {
        string normalized = (commandId ?? string.Empty).Trim();
        return string.Equals(normalized, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase)
            ? InputSettings.MouseGestureUnassignedCommandId
            : normalized;
    }
}
