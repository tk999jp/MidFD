using System;
using System.Collections.Generic;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Presentation;

/// <summary>
/// FunctionBarの表示・enabled・command hintを、controlから独立したplain modelへ変換する。
/// </summary>
internal static class FunctionBarProjection
{
    public static IReadOnlyList<FunctionBarSlotViewModel> Build(
        FunctionKeyProfile profile,
        bool isBrowserMode,
        bool isShiftLayer,
        bool isCtrlLayer,
        bool isAltLayer,
        InputSettings settings,
        CommandRegistry commandRegistry,
        CommandStateCoordinator.CommandUiSnapshot commandSnapshot)
    {
        if (!isBrowserMode)
        {
            return BuildViewerSlots();
        }

        var commandState = new CommandStateCoordinator();
        string profileValue = profile == FunctionKeyProfile.FDCompatible
            ? InputSettings.FdCompatibleProfileValue
            : InputSettings.StandardProfileValue;
        var models = new List<FunctionBarSlotViewModel>(12);
        for (int slot = 1; slot <= 12; slot++)
        {
            string? commandId = FunctionKeyProfileService.ResolveFunctionBarCommandId(
                profile,
                slot,
                settings.FunctionBarCommandOverridesStandard,
                settings.FunctionBarCommandOverridesFdCompatible,
                settings.FunctionBarCommandOverridesShiftStandard,
                settings.FunctionBarCommandOverridesShiftFdCompatible,
                isShiftLayer,
                settings.FunctionBarCommandOverridesCtrlStandard,
                settings.FunctionBarCommandOverridesCtrlFdCompatible,
                settings.FunctionBarCommandOverridesAltStandard,
                settings.FunctionBarCommandOverridesAltFdCompatible,
                isCtrlLayer,
                isAltLayer);
            bool unassigned = string.IsNullOrEmpty(commandId) || FunctionKeyProfileService.IsExplicitUnassigned(commandId);
            string shortLabel = unassigned
                ? string.Empty
                : FunctionKeyProfileService.ResolveFunctionBarDisplayLabelFromCommandId(profile, commandId);
            Dictionary<string, FunctionBarLabelOverride>? labelOverrides = GetLabelOverrides(settings, isShiftLayer, isCtrlLayer, isAltLayer, profile);
            if (!unassigned && commandId != null && labelOverrides?.TryGetValue($"F{slot}", out FunctionBarLabelOverride? labelOverride) == true &&
                labelOverride != null && string.Equals(labelOverride.CommandId, commandId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(labelOverride.Label))
            {
                shortLabel = InputSettings.NormalizeFunctionBarLabelText(labelOverride.Label);
            }

            string? keyHint = null;
            string? hotKeyChar = null;
            if (!unassigned && commandId != null)
            {
                keyHint = FunctionKeyProfileService.ResolveFunctionBarKeyHint(commandId, settings.BrowserKeyCommandOverrides, profileValue);
                hotKeyChar = FunctionKeyProfileService.ResolveFunctionBarBrowserHotKeyCharacter(commandId, settings.BrowserKeyCommandOverrides, profileValue);
                if (string.IsNullOrWhiteSpace(keyHint)) keyHint = null;
                if (string.IsNullOrWhiteSpace(hotKeyChar)) hotKeyChar = null;
            }

            string displayLabel = unassigned
                ? string.Empty
                : FunctionKeyProfileService.ResolveFunctionBarDisplayLabel(
                    profile,
                    slot,
                    isShiftLayer,
                    isCtrlLayer,
                    isAltLayer,
                    commandId,
                    labelOverrides);
            bool enabled = !unassigned && commandId != null && commandState.IsCommandEnabled(commandId, commandSnapshot);
            string slotPrefix = isCtrlLayer ? $"Ctrl+F{slot}" : isAltLayer ? $"Alt+F{slot}" : isShiftLayer ? $"Shift+F{slot}" : $"F{slot}";
            string tooltip = BuildTooltip(shortLabel, commandId, slotPrefix, keyHint, commandRegistry);
            string layoutLabel = ResolveLayoutLabel(profile, slot, isShiftLayer, isCtrlLayer, isAltLayer, settings);

            models.Add(new FunctionBarSlotViewModel(
                slot,
                isShiftLayer,
                commandId,
                shortLabel,
                keyHint,
                hotKeyChar,
                displayLabel,
                enabled,
                tooltip,
                layoutLabel,
                true));
        }
        return models;
    }

    private static IReadOnlyList<FunctionBarSlotViewModel> BuildViewerSlots()
    {
        var models = new List<FunctionBarSlotViewModel>(12);
        string[] labels = { "L:Enc ", "W:Wrap", "^F:Find", "F3:Next", "S+F3:Prv", "", "", "", "", "Qt(En/Es)", "", "" };
        for (int i = 0; i < labels.Length; i++)
        {
            bool enabled = !string.IsNullOrEmpty(labels[i]);
            models.Add(new FunctionBarSlotViewModel(i + 1, false, null, string.Empty, null, null, labels[i], enabled, string.Empty, labels[i], true));
        }
        return models;
    }

    private static string BuildTooltip(
        string shortLabel,
        string? commandId,
        string slotPrefix,
        string? keyHint,
        CommandRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return $"{slotPrefix}: 未割り当て";
        }
        CommandDefinition? definition = registry.Find(commandId);
        var lines = new List<string>
        {
            shortLabel,
            $"Command: {commandId}",
            $"Function: {slotPrefix}"
        };
        if (!string.IsNullOrEmpty(keyHint)) lines.Add($"通常キー: {keyHint}");
        lines.Add(definition?.Description ?? $"未登録のコマンドID: {commandId}");
        return string.Join("\r\n", lines);
    }

    private static string ResolveLayoutLabel(
        FunctionKeyProfile profile,
        int slot,
        bool isShift,
        bool isCtrl,
        bool isAlt,
        InputSettings settings)
    {
        if (!isShift && !isCtrl && !isAlt)
        {
            return FunctionKeyProfileService.ResolveFunctionBarDisplayLabel(
                profile,
                slot,
                false,
                false,
                false,
                FunctionKeyProfileService.ResolveFunctionBarCommandId(
                    profile,
                    slot,
                    settings.FunctionBarCommandOverridesStandard,
                    settings.FunctionBarCommandOverridesFdCompatible,
                    settings.FunctionBarCommandOverridesShiftStandard,
                    settings.FunctionBarCommandOverridesShiftFdCompatible,
                    false,
                    settings.FunctionBarCommandOverridesCtrlStandard,
                    settings.FunctionBarCommandOverridesCtrlFdCompatible,
                    settings.FunctionBarCommandOverridesAltStandard,
                    settings.FunctionBarCommandOverridesAltFdCompatible,
                    false,
                    false),
                GetLabelOverrides(settings, false, false, false, profile));
        }

        string? normalCommandId = FunctionKeyProfileService.ResolveFunctionBarCommandId(
            profile,
            slot,
            settings.FunctionBarCommandOverridesStandard,
            settings.FunctionBarCommandOverridesFdCompatible,
            settings.FunctionBarCommandOverridesShiftStandard,
            settings.FunctionBarCommandOverridesShiftFdCompatible,
            false,
            settings.FunctionBarCommandOverridesCtrlStandard,
            settings.FunctionBarCommandOverridesCtrlFdCompatible,
            settings.FunctionBarCommandOverridesAltStandard,
            settings.FunctionBarCommandOverridesAltFdCompatible,
            false,
            false);
        return string.IsNullOrWhiteSpace(normalCommandId) || FunctionKeyProfileService.IsExplicitUnassigned(normalCommandId)
            ? string.Empty
            : FunctionKeyProfileService.ResolveFunctionBarDisplayLabel(
                profile,
                slot,
                false,
                false,
                false,
                normalCommandId,
                GetLabelOverrides(settings, false, false, false, profile));
    }

    private static Dictionary<string, FunctionBarLabelOverride> GetLabelOverrides(
        InputSettings settings,
        bool isShift,
        bool isCtrl,
        bool isAlt,
        FunctionKeyProfile profile)
    {
        bool fd = profile == FunctionKeyProfile.FDCompatible;
        if (isCtrl) return fd ? settings.FunctionBarLabelOverridesCtrlFdCompatible : settings.FunctionBarLabelOverridesCtrlStandard;
        if (isAlt) return fd ? settings.FunctionBarLabelOverridesAltFdCompatible : settings.FunctionBarLabelOverridesAltStandard;
        if (isShift) return fd ? settings.FunctionBarLabelOverridesShiftFdCompatible : settings.FunctionBarLabelOverridesShiftStandard;
        return fd ? settings.FunctionBarLabelOverridesFdCompatible : settings.FunctionBarLabelOverridesStandard;
    }
}
