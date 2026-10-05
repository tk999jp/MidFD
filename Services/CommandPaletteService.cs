using System.Collections.Generic;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// Command Paletteの標準検索とレイヤー表示を組み立てるサービス。
/// </summary>
public static class CommandPaletteService
{
    internal static CommandPalettePresentation BuildPresentation(
        ICommandPaletteHost host,
        FeatureGateService featureGate,
        CommandPaletteUsageState usageState,
        string rawQuery,
        IReadOnlySet<string>? expandedSections = null,
        SelectionResult? selectionSnapshot = null)
    {
        CommandPaletteSearchContext context = new(host, selectionSnapshot);

        return CommandPaletteUniversalSearchService.BuildPresentation(context, featureGate, usageState, rawQuery, expandedSections);
    }

    internal static CommandPalettePresentation BuildPresentation(
        IReadOnlyList<CommandLauncherCommand> standardCommands,
        ICommandPaletteLayerHost host,
        FeatureGateService featureGate,
        CommandPaletteUsageState usageState,
        string rawQuery)
    {
        CommandPaletteLayerQuery query = CommandPaletteLayerQueryParser.Parse(rawQuery);
        if (!query.IsLayered)
        {
            return CommandPalettePresentation.Standard(standardCommands);
        }

        if (!CommandPaletteLayerService.TryBuild(host, featureGate, usageState, query, out CommandPalettePresentation? layeredPresentation) ||
            layeredPresentation == null)
        {
            return CommandPalettePresentation.Standard(standardCommands);
        }

        if (query.IsExplicitLayerQuery)
        {
            return layeredPresentation;
        }

        return CommandPalettePresentation.Mixed(layeredPresentation.Commands, standardCommands, layeredPresentation.StatusText);
    }

}
