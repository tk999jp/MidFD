using System;
using System.Collections.Generic;
using System.Linq;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Services;

internal interface ICommandPaletteHost : ICommandPaletteLayerHost
{
    CommandRegistry GetCommandRegistry();
    string GetCurrentFunctionKeyProfileValue();
    Dictionary<string, List<string>>? GetBrowserKeyCommandOverrides();
    string ResolveKeyBindingText(string commandId);
}

public sealed class CommandPaletteSearchContext
{
    private readonly ICommandPaletteHost _host;
    private readonly SelectionResult? _selectionOverride;

    internal CommandPaletteSearchContext(ICommandPaletteHost host, SelectionResult? selectionOverride = null)
    {
        _host = host;
        _selectionOverride = selectionOverride;
    }

    public CommandRegistry GetCommandRegistry() => _host.GetCommandRegistry();
    public bool IsFileOperationBusy => _host.IsFileOperationBusy;

    public void ExecuteCommandFromUi(
        string commandId,
        CommandScope scope,
        string source,
        SelectionResult? selectionSnapshot = null,
        SevenZipHashAlgorithm? hashAlgorithm = null)
    {
        _host.ExecuteCommandFromUi(commandId, scope, source, selectionSnapshot, hashAlgorithm);
    }

    public void OpenSettingsForm(SettingsForm.InitialTab initialTab)
    {
        SettingsInitialPage page = initialTab switch
        {
            SettingsForm.InitialTab.Color => SettingsInitialPage.Color,
            SettingsForm.InitialTab.Operation => SettingsInitialPage.Operation,
            SettingsForm.InitialTab.InputAssignment => SettingsInitialPage.InputAssignment,
            SettingsForm.InitialTab.External => SettingsInitialPage.External,
            SettingsForm.InitialTab.StartupAndLog => SettingsInitialPage.StartupAndLog,
            _ => SettingsInitialPage.Display
        };
        _host.ExecuteCommandFromUi(CommandIds.AppOpenSettings, new CommandExecutionContext
        {
            Scope = CommandScope.Global,
            Source = "CommandPalette.Search.SettingsPage",
            SettingsInitialPage = page
        });
    }
    public SelectionResult ResolveSelection() => _selectionOverride ?? _host.ResolveSelection();
    public string GetCurrentBrowserPath() => _host.GetCurrentBrowserPath();
    public IReadOnlyDictionary<string, bool> GetPassiveSelectionPathKinds() => _host.GetPassiveSelectionPathKinds();
    public void ShowArchiveContents(string archivePath)
    {
        _host.ExecuteCommandFromUi(CommandIds.ArchiveContentsOpen, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "CommandPalette.Search.ArchiveContents",
            ContextTargetPath = archivePath
        });
    }
    public string ResolveKeyBindingText(string commandId) => _host.ResolveKeyBindingText(commandId);

    internal static string ResolveKeyBindingText(ICommandPaletteHost host, string commandId)
    {
        Dictionary<string, string> bindings = BrowserCommandBindingResolver.ResolveEffectiveKeyCommandMap(
            host.GetCurrentFunctionKeyProfileValue(),
            host.GetBrowserKeyCommandOverrides(),
            host.GetCommandRegistry());

        string[] gestures = bindings
            .Where(x => string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return gestures.Length == 0 ? "未割り当て" : string.Join(" / ", gestures);
    }
}
