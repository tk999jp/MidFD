namespace MidFD.Services;

public sealed record ConfiguredEditorLaunchResult(bool Succeeded, string? Message);

public static class ConfiguredEditorService
{
    public static ConfiguredEditorLaunchResult Open(string path, string? configuredEditor)
        => Open(path, configuredEditor, File.Exists, ExternalToolService.OpenWithEditor, ResolveNotepadPath);

    internal static ConfiguredEditorLaunchResult Open(string path, string? configuredEditor,
        Func<string, bool> exists, Func<string, string, string?> launch, Func<string?> fallback)
    {
        bool configured = !string.IsNullOrWhiteSpace(configuredEditor) && exists(configuredEditor);
        string? executable = configured ? configuredEditor : fallback();
        if (string.IsNullOrWhiteSpace(executable))
            return new(false, "外部Editorが未設定で、notepad.exe も見つかりませんでした。");
        string? error = launch(executable, path);
        if (error != null) return new(false, configured ? error
            : $"外部Editorが未設定かつ notepad.exe の起動にも失敗しました: {error}");
        return new(true, configured ? null : "外部Editorが未設定のため notepad.exe で開きました。");
    }

    private static string? ResolveNotepadPath()
    {
        string[] candidates = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe")];
        return candidates.FirstOrDefault(File.Exists) ?? "notepad.exe";
    }
}
