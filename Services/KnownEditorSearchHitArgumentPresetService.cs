namespace MidFD.Services;

internal sealed record KnownEditorSearchHitArgumentPreset(
    string EditorName,
    string ExecutableName,
    string ArgumentTemplate);

internal static class KnownEditorSearchHitArgumentPresetService
{
    private static readonly IReadOnlyDictionary<string, KnownEditorSearchHitArgumentPreset> Presets =
        new Dictionary<string, KnownEditorSearchHitArgumentPreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["MidEditor.exe"] = new("MidEditor", "MidEditor.exe", "--open \"{file}\" --line {line} --column {column}"),
            ["Code.exe"] = new("Visual Studio Code", "Code.exe", "--goto \"{file}:{line}:{column}\""),
            ["notepad++.exe"] = new("Notepad++", "notepad++.exe", "-n{line} -c{column} \"{file}\""),
            ["subl.exe"] = new("Sublime Text", "subl.exe", "\"{file}:{line}:{column}\""),
            ["sakura.exe"] = new("サクラエディタ", "sakura.exe", "-Y={line} -X={column} \"{file}\""),
            ["Mery.exe"] = new("Mery", "Mery.exe", "/l {line} /cl {column} \"{file}\""),
            ["Hidemaru.exe"] = new("秀丸エディタ", "Hidemaru.exe", "/jw{line},{column} \"{file}\""),
            ["xyzzy.exe"] = new("xyzzy", "xyzzy.exe", "\"{file}\" -g {line} -c {column}"),
            ["xyzzycli.exe"] = new("xyzzy", "xyzzycli.exe", "\"{file}\" -g {line} -c {column}")
        };

    internal static IReadOnlyCollection<KnownEditorSearchHitArgumentPreset> AllPresets => Presets.Values.ToArray();

    internal static bool TryResolve(
        string? configuredEditorPath,
        out KnownEditorSearchHitArgumentPreset preset)
    {
        preset = null!;
        if (string.IsNullOrWhiteSpace(configuredEditorPath))
        {
            return false;
        }

        string? executableName;
        try
        {
            executableName = Path.GetFileName(configuredEditorPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        return executableName != null && Presets.TryGetValue(executableName, out preset!);
    }

    internal static string? GetTemplateAfterEditorSelection(
        string? previousEditorPath,
        string? selectedEditorPath,
        string? currentTemplate,
        bool templateWasManuallyEdited)
    {
        if (templateWasManuallyEdited)
        {
            return currentTemplate;
        }

        bool hasCurrentTemplate = !string.IsNullOrWhiteSpace(currentTemplate);
        bool hadPreviousPreset =
            TryResolve(previousEditorPath, out KnownEditorSearchHitArgumentPreset previousPreset) &&
            string.Equals(currentTemplate, previousPreset.ArgumentTemplate, StringComparison.Ordinal);

        if (TryResolve(selectedEditorPath, out KnownEditorSearchHitArgumentPreset selectedPreset))
        {
            if (!hasCurrentTemplate || hadPreviousPreset)
            {
                return selectedPreset.ArgumentTemplate;
            }

            return currentTemplate;
        }

        return hadPreviousPreset ? null : currentTemplate;
    }
}
