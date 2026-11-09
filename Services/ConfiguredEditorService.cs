using System.Diagnostics;

namespace MidFD.Services;

public sealed record ConfiguredEditorLaunchResult(
    bool Succeeded,
    string? Message,
    bool PositionArgumentsSent = false);

public static class ConfiguredEditorService
{
    public static ConfiguredEditorLaunchResult Open(string path, string? configuredEditor)
        => Open(path, configuredEditor, File.Exists, ExternalToolService.OpenWithEditor, ResolveNotepadPath);

    internal static ConfiguredEditorLaunchResult Open(
        string path,
        string? configuredEditor,
        Func<ProcessStartInfo, string?>? launchOverride)
        => Open(path, configuredEditor, File.Exists,
            (executable, targetPath) => Launch(executable, [targetPath], launchOverride),
            ResolveNotepadPath);

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

    public static ConfiguredEditorLaunchResult OpenSearchHitFile(string path, string? configuredEditor)
        => OpenSearchHitFile(path, line: 1, column: 1, configuredEditor, argumentTemplate: null);

    public static ConfiguredEditorLaunchResult OpenSearchHitFile(
        string path,
        int line,
        int column,
        string? configuredEditor,
        string? argumentTemplate)
        => OpenSearchHitFile(path, line, column, configuredEditor, argumentTemplate,
            File.Exists, launchOverride: null, ResolveNotepadPath);

    internal static ConfiguredEditorLaunchResult OpenSearchHitFile(
        string path,
        int line,
        int column,
        string? configuredEditor,
        string? argumentTemplate,
        Func<ProcessStartInfo, string?>? launchOverride)
        => OpenSearchHitFile(path, line, column, configuredEditor, argumentTemplate,
            File.Exists, launchOverride, ResolveNotepadPath);

    internal static ConfiguredEditorLaunchResult OpenSearchHitFile(
        string path,
        int line,
        int column,
        string? configuredEditor,
        string? argumentTemplate,
        Func<string, bool> exists,
        Func<ProcessStartInfo, string?>? launchOverride,
        Func<string?> fallback)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return new(false, $"対象pathが不正です: {ex.Message}");
        }

        if (line < 1 || column < 1)
        {
            return new(false, "検索hitの行番号と列番号は1以上である必要があります。");
        }

        if (!Exists(exists, fullPath))
        {
            return new(false, $"対象が見つかりません: {fullPath}");
        }

        bool configured = !string.IsNullOrWhiteSpace(configuredEditor) && Exists(exists, configuredEditor);
        string executable;
        if (configured)
        {
            try
            {
                executable = Path.GetFullPath(configuredEditor!);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            {
                return new(false, $"外部Editor pathが不正です: {ex.Message}");
            }
        }
        else
        {
            try
            {
                executable = fallback() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return new(false, $"外部Editorが未設定で、notepad.exe の確認にも失敗しました: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(executable))
            {
                return new(false, "外部Editorが未設定で、notepad.exe も見つかりませんでした。");
            }
        }

        bool hasTemplate = configured && !string.IsNullOrWhiteSpace(argumentTemplate);
        IReadOnlyList<string> arguments;
        bool usesPositionArguments = false;
        if (hasTemplate)
        {
            if (!ConfiguredEditorSearchHitArgumentTemplateService.TryExpand(
                    argumentTemplate, fullPath, line, column,
                    out arguments, out usesPositionArguments, out string? templateError))
            {
                return new(false, $"Grep用外部Editor引数テンプレートが不正です: {templateError}");
            }
        }
        else
        {
            arguments = [fullPath];
        }

        string? launchError = Launch(executable, arguments, launchOverride);
        if (launchError != null)
        {
            return new(false, configured
                ? launchError
                : $"外部Editorが未設定のため notepad.exe で開けませんでした: {launchError}");
        }

        if (!configured)
        {
            return new(true,
                "外部Editorが未設定か見つからないため notepad.exe でファイルのみ開きました。Grep引数テンプレートは適用していません。");
        }

        if (!hasTemplate)
        {
            return new(true,
                "検索hitのファイルのみを外部Editorで開きました。Grep引数テンプレートが未設定のため、行・列は送信していません。");
        }

        if (usesPositionArguments)
        {
            return new(true,
                "設定したGrep引数で外部Editorを起動し、検索hitの位置値を引数として送信しました。実際の位置移動は外部Editor側の処理結果で、MidFDからは確認できません。",
                PositionArgumentsSent: true);
        }

        return new(true,
            "設定したGrep引数で外部Editorを起動しました。テンプレートに行・列placeholderがないため、位置値は送信していません。");
    }

    internal static string GetSearchHitMenuCaption(string? argumentTemplate, string? configuredEditor)
    {
        bool editorAvailable = !string.IsNullOrWhiteSpace(configuredEditor) && Exists(File.Exists, configuredEditor);
        return ConfiguredEditorSearchHitArgumentTemplateService.GetMenuCaption(argumentTemplate, editorAvailable);
    }

    private static string? Launch(
        string executable,
        IReadOnlyList<string> arguments,
        Func<ProcessStartInfo, string?>? launchOverride)
    {
        if (launchOverride == null)
        {
            return ExternalToolService.OpenWithEditorArguments(executable, arguments);
        }

        if (!ExternalToolService.TryCreateEditorProcessStartInfo(
                executable, arguments, out ProcessStartInfo? startInfo, out string error))
        {
            return error;
        }

        try
        {
            return launchOverride(startInfo!);
        }
        catch (Exception ex)
        {
            return $"起動に失敗しました: {ex.Message}";
        }
    }

    private static bool Exists(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveNotepadPath()
    {
        string[] candidates = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe")];
        return candidates.FirstOrDefault(File.Exists) ?? "notepad.exe";
    }
}
