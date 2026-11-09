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
        Configuration.StartupOptions startup = Configuration.StartupArgumentParser.Parse(args);

        if (startup.Mode == Configuration.StartupMode.StorageDiagnostics)
        {
            Configuration.Storage.StorageProfileActivation activation = Configuration.Storage.StorageProfileActivationResolver.ResolveDefault(startup);
            string? diagnosticsPath = startup.DiagnosticsFilePath;
            string writtenPath = Configuration.Storage.StorageProfileDiagnosticsService.RunToFile(activation, diagnosticsPath);
            if (!string.IsNullOrWhiteSpace(diagnosticsPath))
            {
                Console.WriteLine(writtenPath);
            }
            return;
        }

        if (startup.Mode == Configuration.StartupMode.ShellDeleteProbe)
        {
            int cancelAfter = startup.ProbeCancelAfter;
            int count = startup.ProbeCount;
            string reportPath = Helpers.ShellDeleteCapabilityProbe.Run(cancelAfter, count);
            Console.WriteLine(reportPath);
            return;
        }

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        Configuration.Storage.StorageProfileActivationContext.Initialize(startup);
        ApplicationConfiguration.Initialize();
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        try
        {
            Configuration.AuthorToolsStartupResult authorToolsStartup = Configuration.AuthorToolsStartup.Resolve(startup.AuthorToolsCommand);
            if (!string.IsNullOrWhiteSpace(authorToolsStartup.Notification))
            {
                MessageBox.Show(authorToolsStartup.Notification, "MidFD 作者機能", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            if (!TryResolveStartupProfileOverride(startup.ProfileValue, out string? startupProfileOverride))
            {
                return;
            }

            var mainForm = new MainForm(startupProfileOverride, authorToolsStartup.Enabled, integrationSeam: null, startup);
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


    private static bool TryResolveStartupProfileOverride(string? profileArgument, out string? startupProfileOverride)
    {
        startupProfileOverride = profileArgument;
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
            settings.WorkspaceSnapshotEnabledOverride = dialog.WorkspaceSnapshotEnabledOverride;
            settings.Input ??= new Configuration.InputSettings();
            settings.FileOperations ??= new Configuration.FileOperationsSettings();
            settings.Preview ??= new Configuration.PreviewSettings();
            settings.SevenZip ??= new Configuration.SevenZipSettings();
            settings.ExternalTools ??= new Configuration.ExternalToolsSettings();
            settings.Input.FunctionKeyProfile = dialog.UseFdCompatibleFunctionKeys
                ? Configuration.InputSettings.FdCompatibleProfileValue
                : Configuration.InputSettings.StandardProfileValue;
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
            settings.ExternalTools.ExternalEditorSearchHitArgumentTemplate = NormalizeOptionalPath(dialog.ExternalEditorSearchHitArgumentTemplate);
            ApplyStartupRestorePreset(settings.Session, dialog.RestoreStartupState);
            Runtime.CommandApplicationResult saveResult = DispatchInitialSettingsSave(settings, new Runtime.SettingsApplicationCoordinator());
            InitialSettingsSaveEvaluation saveEvaluation = EvaluateInitialSettingsSave(saveResult);
            if (saveEvaluation.Outcome == InitialSettingsSaveOutcome.CommandNotAccepted)
            {
                return false;
            }

            if (saveEvaluation.Outcome == InitialSettingsSaveOutcome.PrimarySaveFailed)
            {
                MessageBox.Show(
                    saveEvaluation.UserMessage,
                    "設定保存エラー",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            else if (saveEvaluation.Outcome == InitialSettingsSaveOutcome.PrimarySavedBackupFailed)
            {
                MessageBox.Show(
                    saveEvaluation.UserMessage,
                    "設定バックアップ警告",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

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

    internal static Runtime.CommandApplicationResult DispatchInitialSettingsSave(
        Configuration.AppSettings settings,
        Runtime.ISettingsApplicationPort settingsApplication)
    {
        var application = new Runtime.BootstrapSettingsCommandApplicationCoordinator(settingsApplication);
        var dispatcher = new Commands.CommandDispatcher(new Commands.CommandRegistry(), application);
        return dispatcher.Dispatch(
            Commands.CommandIds.AppSettingsSave,
            new Commands.CommandExecutionContext
            {
                Scope = Commands.CommandScope.Global,
                Source = "Program.FirstLaunchProfileSelection",
                SettingsSave = new Commands.SettingsSaveCommandContext(settings)
            });
    }

    internal static InitialSettingsSaveEvaluation EvaluateInitialSettingsSave(Runtime.CommandApplicationResult commandResult)
    {
        if (!commandResult.Handled ||
            commandResult.Effect is not { } effect ||
            effect.Kind != Runtime.CommandApplicationEffectKind.SettingsSaveCompleted ||
            effect.SettingsSaveResult is not { } saveResult)
        {
            return new InitialSettingsSaveEvaluation(InitialSettingsSaveOutcome.CommandNotAccepted, null);
        }

        if (!saveResult.PrimarySaved)
        {
            return new InitialSettingsSaveEvaluation(
                InitialSettingsSaveOutcome.PrimarySaveFailed,
                string.IsNullOrWhiteSpace(saveResult.UserMessage) ? "設定を保存できませんでした。" : saveResult.UserMessage);
        }

        if (!saveResult.BackupSucceeded)
        {
            return new InitialSettingsSaveEvaluation(
                InitialSettingsSaveOutcome.PrimarySavedBackupFailed,
                string.IsNullOrWhiteSpace(saveResult.UserMessage) ? "設定は保存されましたが、バックアップ更新に失敗しました。" : saveResult.UserMessage);
        }

        return new InitialSettingsSaveEvaluation(InitialSettingsSaveOutcome.Saved, null);
    }

    internal enum InitialSettingsSaveOutcome
    {
        CommandNotAccepted,
        Saved,
        PrimarySavedBackupFailed,
        PrimarySaveFailed
    }

    internal readonly record struct InitialSettingsSaveEvaluation(
        InitialSettingsSaveOutcome Outcome,
        string? UserMessage);

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
