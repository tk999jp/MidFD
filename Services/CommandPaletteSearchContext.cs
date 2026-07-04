using System;
using System.Collections.Generic;
using System.Linq;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Services;

public sealed class CommandPaletteSearchContext
{
    private readonly Func<CommandRegistry> _registryProvider;
    private readonly Action<string, CommandScope, string, SelectionResult?> _executor;
    private readonly Action _openSettingsForm;
    private readonly Action<SettingsForm.InitialTab> _openSettingsFormWithTab;
    private readonly Action _showCommandList;
    private readonly Action _showSystemInformationDialog;
    private readonly Action _openControlPanel;
    private readonly Func<SelectionResult> _selectionProvider;
    private readonly Func<string> _currentBrowserPathProvider;
    private readonly Action<string> _showArchiveContents;
    private readonly Action<SevenZipHashAlgorithm> _executeArchiveHash;
    private readonly Func<string, string> _keyBindingResolver;

    public CommandPaletteSearchContext(
        Func<CommandRegistry> registryProvider,
        Action<string, CommandScope, string, SelectionResult?> executor,
        Action openSettingsForm,
        Action<SettingsForm.InitialTab> openSettingsFormWithTab,
        Action showCommandList,
        Action showSystemInformationDialog,
        Action openControlPanel,
        Func<SelectionResult> selectionProvider,
        Func<string> currentBrowserPathProvider,
        Action<string> showArchiveContents,
        Action<SevenZipHashAlgorithm> executeArchiveHash,
        Func<string, string>? keyBindingResolver = null)
    {
        _registryProvider = registryProvider;
        _executor = executor;
        _openSettingsForm = openSettingsForm;
        _openSettingsFormWithTab = openSettingsFormWithTab;
        _showCommandList = showCommandList;
        _showSystemInformationDialog = showSystemInformationDialog;
        _openControlPanel = openControlPanel;
        _selectionProvider = selectionProvider;
        _currentBrowserPathProvider = currentBrowserPathProvider;
        _showArchiveContents = showArchiveContents;
        _executeArchiveHash = executeArchiveHash;
        _keyBindingResolver = keyBindingResolver ?? (_ => "未割り当て");
    }

    public static implicit operator CommandPaletteSearchContext(MainForm mainForm)
    {
        return new CommandPaletteSearchContext(
            () => mainForm.InvokeGetCommandRegistry(),
            (id, scope, source, selectionSnapshot) => mainForm.InvokeExecuteCommandFromUi(id, scope, source, selectionSnapshot),
            () => mainForm.InvokeOpenSettingsForm(),
            initialTab => mainForm.InvokeOpenSettingsForm(initialTab),
            () => mainForm.InvokeShowCommandList(),
            () => mainForm.InvokeShowSystemInformationDialog(),
            () => mainForm.InvokeOpenControlPanel(),
            () => mainForm.InvokeResolveSelection(),
            () => mainForm.InvokeGetCurrentBrowserPath(),
            path => mainForm.InvokeShowArchiveContents(path),
            algorithm => _ = mainForm.InvokeExecuteArchiveHashAsync(algorithm),
            commandId => ResolveKeyBindingText(mainForm, commandId)
        );
    }

    public CommandRegistry GetCommandRegistry() => _registryProvider();

    public void ExecuteCommandFromUi(string commandId, CommandScope scope, string source, SelectionResult? selectionSnapshot = null)
    {
        _executor(commandId, scope, source, selectionSnapshot);
    }

    public void OpenSettingsForm() => _openSettingsForm();
    public void OpenSettingsForm(SettingsForm.InitialTab initialTab) => _openSettingsFormWithTab(initialTab);
    public void ShowCommandList() => _showCommandList();
    public void ShowSystemInformationDialog() => _showSystemInformationDialog();
    public void OpenControlPanel() => _openControlPanel();
    public SelectionResult ResolveSelection() => _selectionProvider();
    public string GetCurrentBrowserPath() => _currentBrowserPathProvider();
    public void ShowArchiveContents(string archivePath) => _showArchiveContents(archivePath);
    public void ExecuteArchiveHash(SevenZipHashAlgorithm algorithm) => _executeArchiveHash(algorithm);
    public string ResolveKeyBindingText(string commandId) => _keyBindingResolver(commandId);

    internal static string ResolveKeyBindingText(MainForm mainForm, string commandId)
    {
        Dictionary<string, string> bindings = BrowserCommandBindingResolver.ResolveEffectiveKeyCommandMap(
            mainForm.InvokeGetCurrentFunctionKeyProfileValue(),
            mainForm.InvokeGetBrowserKeyCommandOverrides(),
            mainForm.InvokeGetCommandRegistry());

        string[] gestures = bindings
            .Where(x => string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return gestures.Length == 0 ? "未割り当て" : string.Join(" / ", gestures);
    }
}
