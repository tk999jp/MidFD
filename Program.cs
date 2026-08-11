namespace MidFD;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // 早期に例外フックを登録
        RegisterStartupExceptionHooks();

        if (HasStorageProfileDiagnosticsRequest(args))
        {
            Configuration.Storage.StorageProfileActivation activation = Configuration.Storage.StorageProfileActivationResolver.ResolveDefault(args);
            string? diagnosticsPath = TryGetStorageProfileDiagnosticsPath(args);
            string writtenPath = Configuration.Storage.StorageProfileDiagnosticsService.RunToFile(activation, diagnosticsPath);
            if (!string.IsNullOrWhiteSpace(diagnosticsPath))
            {
                Console.WriteLine(writtenPath);
            }
            return;
        }

        if (args.Any(arg => string.Equals(arg, "--shell-delete-probe", StringComparison.OrdinalIgnoreCase)))
        {
            int cancelAfter = TryGetShellDeleteProbeCancelAfter(args);
            int count = TryGetShellDeleteProbeCount(args);
            string reportPath = Helpers.ShellDeleteCapabilityProbe.Run(cancelAfter, count);
            Console.WriteLine(reportPath);
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        Configuration.Storage.StorageProfileActivationContext.Initialize(args);
        ApplicationConfiguration.Initialize();
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        try
        {
            Configuration.AuthorToolsStartupResult authorToolsStartup = Configuration.AuthorToolsStartup.Resolve(args);
            if (!string.IsNullOrWhiteSpace(authorToolsStartup.Notification))
            {
                MessageBox.Show(authorToolsStartup.Notification, "MidFD 作者機能", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            if (!TryResolveStartupProfileOverride(args, out string? startupProfileOverride))
            {
                return;
            }

            var mainForm = new MainForm(startupProfileOverride, authorToolsStartup.Enabled);
            Application.Run(mainForm);
        }
        catch (Exception ex)
        {
            string logPath = Services.StartupExceptionLogger.Write("Application.Run", ex);
            ShowStartupFailureMessage(logPath, ex);
        }
    }

    private static void RegisterStartupExceptionHooks()
    {
        // WinForms UI スレッド例外
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (sender, e) =>
        {
            Services.StartupExceptionLogger.Write("Application.ThreadException", e.Exception);
            try { MessageBox.Show("画面操作中にエラーが発生しました。該当操作を中止しました。", "MidFD", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        };

        // 一般的な未処理例外
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Services.StartupExceptionLogger.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };

        // 非同期 Task 内の未処理例外
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Services.StartupExceptionLogger.Write("TaskScheduler.UnobservedTaskException", e.Exception);
            // ログに記録するのみとし、SetObserved() は行わない (既定の挙動を尊重)
        };
    }

    private static void ShowStartupFailureMessage(string logPath, Exception ex)
    {
        try
        {
            string message = "MidFD の起動中にエラーが発生しました。\n" +
                             "詳細は以下のログファイルに保存しました。\n\n" +
                             (string.IsNullOrEmpty(logPath) ? "(ログの保存に失敗しました)" : logPath) + "\n\n" +
                             $"エラー: {ex.GetType().Name}\n" +
                             $"{ex.Message}";

            MessageBox.Show(message, "MidFD 起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // MessageBox 自体が失敗した場合はどうしようもないので無視
        }
    }


    private static int TryGetShellDeleteProbeCancelAfter(string[] args)
    {
        const string prefix = "--shell-delete-probe-cancel-after=";
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg[prefix.Length..], out int value))
            {
                return Math.Max(0, value);
            }
        }

        return 0;
    }

    private static int TryGetShellDeleteProbeCount(string[] args)
    {
        const string prefix = "--shell-delete-probe-count=";
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg[prefix.Length..], out int value))
            {
                return Math.Clamp(value, 1, 200);
            }
        }

        return 10;
    }

    private static string? TryGetProfileArgument(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--profile", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }

            const string prefix = "--profile=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    private static string? TryGetStorageProfileDiagnosticsPath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--storage-profile-diagnostics-file", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : string.Empty;
            }

            const string prefix = "--storage-profile-diagnostics-file=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    private static bool HasStorageProfileDiagnosticsRequest(string[] args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--storage-profile-diagnostics", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--storage-profile-diagnostics-file", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveStartupProfileOverride(string[] args, out string? startupProfileOverride)
    {
        startupProfileOverride = TryGetProfileArgument(args);
        Configuration.AppSettings settings = Configuration.SettingsManager.Load(out Configuration.SettingsManager.SettingsLoadMetadata settingsLoadMetadata);

        if (Services.FeatureProfileService.TryResolveProfile(startupProfileOverride, out Models.FeatureProfile startupProfile))
        {
            startupProfileOverride = Services.FeatureProfileService.ToSettingValue(startupProfile);
            return true;
        }

        if (settingsLoadMetadata.IsProfileExplicit && Services.FeatureProfileService.TryResolveProfile(settings.Profile, out _))
        {
            startupProfileOverride = null;
            return true;
        }

        if (!ShouldShowFeatureProfileSelection(settingsLoadMetadata))
        {
            startupProfileOverride = null;
            return true;
        }

        try
        {
            settings.Input ??= new Configuration.InputSettings();
            settings.FileOperations ??= new Configuration.FileOperationsSettings();
            settings.Appearance ??= new Configuration.AppearanceSettings();
            settings.BrowserTabs ??= new Configuration.BrowserTabSettings();
            using var dialog = new Dialogs.FeatureProfileSelectionDialog(settings, isFirstLaunch: true);
            var result = dialog.ShowDialog();
            if (result != DialogResult.OK)
            {
                return false;
            }

            Services.FeatureProfileService.ApplyRuntimeProfile(settings, dialog.SelectedProfile, settingsLoadMetadata.IsMouseGesturesExplicit);
            settings.Input ??= new Configuration.InputSettings();
            settings.FileOperations ??= new Configuration.FileOperationsSettings();
            settings.Preview ??= new Configuration.PreviewSettings();
            settings.SevenZip ??= new Configuration.SevenZipSettings();
            settings.ExternalTools ??= new Configuration.ExternalToolsSettings();
            settings.Input.FunctionKeyProfile = dialog.UseFdCompatibleFunctionKeys
                ? Configuration.InputSettings.FdCompatibleProfileValue
                : Configuration.InputSettings.StandardProfileValue;
            settings.Preview.VideoEnterPlaysExternal = dialog.VideoEnterPlaysExternal;
            settings.Input.EnableMouseGestures = dialog.EnableMouseGestures;
            settings.Input.ShowFunctionBarTooltips = dialog.ShowFunctionBarTooltips;
            settings.FileOperations.EnableDragArchiveHandoff = dialog.EnableDragArchiveHandoff;
            settings.FileOperations.IncludeDragZipManifest = dialog.IncludeDragZipManifest;
            settings.FileOperations.UseMidFdManagedTrash = dialog.UseMidFdManagedTrash;
            settings.FileOperations.ClipboardPasteTextAsFileEnabled = dialog.ClipboardPasteTextAsFileEnabled;
            settings.Appearance.ShowPathAsBreadcrumb = dialog.ShowPathAsBreadcrumb;
            settings.Appearance.ColorTheme = dialog.ColorTheme;
            settings.BrowserTabs.LayoutMode = dialog.LayoutMode;
            settings.SevenZip.ExePath = NormalizeOptionalPath(dialog.SevenZipPath);
            settings.Preview.VideoToolDirectory = NormalizeOptionalPath(dialog.VideoToolDirectory);
            settings.ExternalTools.ExternalEditorPath = NormalizeOptionalPath(dialog.ExternalEditorPath);
            ApplyStartupRestorePreset(settings.Session, dialog.RestoreStartupState);
            Configuration.SettingsManager.Save(settings);
            startupProfileOverride = Services.FeatureProfileService.ToSettingValue(dialog.SelectedProfile);
            return true;
        }
        catch (Exception ex)
        {
            Services.StartupExceptionLogger.Write("FeatureProfileSelection", ex);
            return false;
        }
    }

    internal static bool ShouldShowFeatureProfileSelection(Configuration.SettingsManager.SettingsLoadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return metadata.LoadKind == Configuration.SettingsManager.SettingsLoadKind.TrueFirstLaunch;
    }

    private static string? NormalizeOptionalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static void ApplyStartupRestorePreset(Configuration.SessionSettings session, bool enabled)
    {
        session ??= new Configuration.SessionSettings();
        session.RestoreStartupState = enabled;
        session.RestoreTabsOnStartup = enabled;
        session.RestoreLastPath = enabled;
        session.RestoreDisplayState = enabled;
        session.RestoreWindowBounds = enabled;
        session.RestoreColumnCount = enabled;
        session.RestoreSort = enabled;
    }
}
