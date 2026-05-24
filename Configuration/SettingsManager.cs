using System.IO;
using System.Text.Json;
using MidFD.Services;
using MidFD.Helpers;

namespace MidFD.Configuration;

public static class SettingsManager
{
    public sealed class SettingsLoadMetadata
    {
        public bool IsProfileExplicit { get; init; }
        public bool IsMouseGesturesExplicit { get; init; }
    }

    private static readonly string SettingsFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static SettingsManager()
    {
        // アプリケーションの実行ディレクトリに settings.json を配置する (ポータブル指向)
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        SettingsFilePath = Path.Combine(exeDir, "settings.json");
    }

    public static AppSettings Load()
    {
        return Load(out _);
    }

    public static AppSettings Load(out SettingsLoadMetadata metadata)
    {
        if (!File.Exists(SettingsFilePath))
        {
            metadata = new SettingsLoadMetadata();
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(SettingsFilePath);
            metadata = ExtractLoadMetadata(json);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            AppSettings loadedSettings = settings ?? new AppSettings();
            MaterializeBrowserTabRestoreState(loadedSettings);
            bool videoStillMigrated = ApplyVideoStillInitialSecondsMigration(loadedSettings);
            bool loggingMigrated = ApplyLoggingDefaultOffMigration(loadedSettings);
            if (videoStillMigrated || loggingMigrated)
            {
                Save(loadedSettings);
            }
            ApplyVideoToolDirectoryMigration(loadedSettings);
            return loadedSettings;
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to load settings.", ex);
            // 設定ファイルが壊れていても起動不能にしないため、デフォルトを返す
            metadata = new SettingsLoadMetadata();
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AppSettings persistableSettings = BuildPersistableSettings(settings);
            string? settingsDirectory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            string json = JsonSerializer.Serialize(persistableSettings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to save settings.", ex);
            // 保存失敗時もUIをクラッシュさせない
        }
    }

    private static AppSettings BuildPersistableSettings(AppSettings settings)
    {
        AppSettings persistableSettings = settings.Clone();
        persistableSettings.Session ??= new SessionSettings();
        persistableSettings.BrowserTabs ??= new BrowserTabSettings();
        if (!persistableSettings.Session.RestoreTabsOnStartup)
        {
            persistableSettings.Session.ClearBrowserTabRestoreState();
            persistableSettings.BrowserTabs.Categories = new List<BrowserTabCategoryDefinition>();
        }
        else
        {
            // Workspace SQLite 移行後も、BrowserTabRestoreSnapshot は互換 fallback として保存する。
            persistableSettings.Session.BrowserTabRestoreSnapshot = BuildBrowserTabRestoreSnapshotForPersist(persistableSettings);
            persistableSettings.Session.ClearBrowserTabRestoreLegacyMirror();
            persistableSettings.BrowserTabs.Categories = new List<BrowserTabCategoryDefinition>();
        }

        NormalizeAllTabHistories(persistableSettings);
        return persistableSettings;
    }

    private static bool ApplyVideoStillInitialSecondsMigration(AppSettings settings)
    {
        settings.Preview ??= new PreviewSettings();
        if (settings.Preview.VideoStillInitialSecondsMigratedToZero)
        {
            return false;
        }

        // 旧既定値(10秒)のみ一度だけ0秒へ移行する。
        bool shouldMigrate = settings.Preview.VideoSkipSeconds == 10;
        if (shouldMigrate)
        {
            settings.Preview.VideoSkipSeconds = 0;
        }

        settings.Preview.VideoStillInitialSecondsMigratedToZero = true;
        return shouldMigrate;
    }

    private static void ApplyVideoToolDirectoryMigration(AppSettings settings)
    {
        settings.Preview ??= new PreviewSettings();
        if (string.IsNullOrEmpty(settings.Preview.VideoToolDirectory) && !string.IsNullOrEmpty(settings.Preview.VideoStillPreviewFfmpegPath))
        {
            string oldPath = settings.Preview.VideoStillPreviewFfmpegPath;
            if (File.Exists(oldPath))
            {
                string? parentDir = Path.GetDirectoryName(oldPath);
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                {
                    settings.Preview.VideoToolDirectory = parentDir;
                }
                else
                {
                    settings.Preview.VideoToolDirectory = oldPath;
                }
            }
            else
            {
                settings.Preview.VideoToolDirectory = oldPath;
            }

            // メモリ上でのみ移行し、旧設定値をクリアして不要な二重保存を防ぐ
            settings.Preview.VideoStillPreviewFfmpegPath = null;
        }
    }

    private static bool ApplyLoggingDefaultOffMigration(AppSettings settings)
    {
        settings.Logging ??= new LoggingSettings();
        if (settings.Logging.DefaultOffMigrationApplied)
        {
            return false;
        }

        if (settings.Logging.IsEnabled && !settings.Logging.IsDetailedEnabled)
        {
            settings.Logging.IsEnabled = false;
        }

        settings.Logging.DefaultOffMigrationApplied = true;
        return true;
    }

    private static void NormalizeAllTabHistories(AppSettings settings)
    {
        if (settings.Session == null) return;

        // Session.OpenTabs (compatibility mirror)
        foreach (var tab in settings.Session.OpenTabs)
        {
            tab.BackHistory = HistoryHelper.Normalize(tab.BackHistory);
            tab.ForwardHistory = HistoryHelper.Normalize(tab.ForwardHistory);
        }

        // Session.BrowserTabCategories (compatibility mirror)
        foreach (var category in settings.Session.BrowserTabCategories)
        {
            foreach (var tab in category.OpenTabs)
            {
                tab.BackHistory = HistoryHelper.Normalize(tab.BackHistory);
                tab.ForwardHistory = HistoryHelper.Normalize(tab.ForwardHistory);
            }
        }

        // Session.BrowserTabRestoreSnapshot
        if (settings.Session.BrowserTabRestoreSnapshot != null)
        {
            foreach (var category in settings.Session.BrowserTabRestoreSnapshot.Categories)
            {
                foreach (var tab in category.OpenTabs)
                {
                    tab.BackHistory = HistoryHelper.Normalize(tab.BackHistory);
                    tab.ForwardHistory = HistoryHelper.Normalize(tab.ForwardHistory);
                }
            }
        }
    }

    private static void MaterializeBrowserTabRestoreState(AppSettings settings)
    {
        settings.BrowserTabs ??= new BrowserTabSettings();
        settings.Session ??= new SessionSettings();

        if (settings.Session.BrowserTabRestoreSnapshot?.Categories is { Count: > 0 } snapshotCategories)
        {
            string activeCategoryId = ResolveActiveCategoryId(
                settings.Session.BrowserTabRestoreSnapshot.ActiveCategoryId,
                snapshotCategories.Select(static category => category.Id));

            settings.BrowserTabs.Categories = snapshotCategories
                .Select(static category => new BrowserTabCategoryDefinition
                {
                    Id = NormalizeCategoryId(category.Id),
                    DisplayName = NormalizeCategoryDisplayName(category.DisplayName, category.Id)
                })
                .ToList();

            settings.Session.BrowserTabCategories = snapshotCategories
                .Select(static category => new BrowserTabCategorySessionState
                {
                    CategoryId = NormalizeCategoryId(category.Id),
                    ActiveTabIndex = category.ActiveTabIndex,
                    OpenTabs = category.OpenTabs.Select(static tab => tab.Clone()).ToList()
                })
                .ToList();

            settings.Session.ActiveBrowserTabCategoryId = activeCategoryId;
            MaterializeCompatibilityMirror(settings.Session, activeCategoryId);
            NormalizeAllTabHistories(settings);
            return;
        }

        settings.BrowserTabs.Categories = new List<BrowserTabCategoryDefinition>();
        settings.Session.BrowserTabCategories = new List<BrowserTabCategorySessionState>();
        settings.Session.ActiveBrowserTabCategoryId = BrowserTabSettings.DefaultCategoryId;
        MaterializeCompatibilityMirror(settings.Session, BrowserTabSettings.DefaultCategoryId);
    }

    private static BrowserTabRestoreSnapshot BuildBrowserTabRestoreSnapshotForPersist(AppSettings settings)
    {
        settings.BrowserTabs ??= new BrowserTabSettings();
        settings.Session ??= new SessionSettings();

        if (settings.Session.BrowserTabRestoreSnapshot?.Categories is { Count: > 0 } snapshotCategories)
        {
            return NormalizeBrowserTabRestoreSnapshot(
                settings.Session.BrowserTabRestoreSnapshot,
                settings.BrowserTabs.Categories ?? new List<BrowserTabCategoryDefinition>(),
                snapshotCategories);
        }

        return BuildBrowserTabRestoreSnapshotFromRuntimeState(settings);
    }

    private static BrowserTabRestoreSnapshot BuildBrowserTabRestoreSnapshotFromRuntimeState(AppSettings settings)
    {
        settings.BrowserTabs ??= new BrowserTabSettings();
        settings.Session ??= new SessionSettings();

        List<BrowserTabCategoryDefinition> categoryDefinitions = (settings.BrowserTabs.Categories ?? new List<BrowserTabCategoryDefinition>())
            .Where(static category => category != null)
            .Select(static category => new BrowserTabCategoryDefinition
            {
                Id = NormalizeCategoryId(category.Id),
                DisplayName = NormalizeCategoryDisplayName(category.DisplayName, category.Id)
            })
            .ToList();

        List<BrowserTabCategorySessionState> categoryStates = GetRuntimeCategorySessionStates(settings);

        var orderedCategoryIds = new List<string>();
        var displayNameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void EnsureCategory(string? categoryId, string? displayName)
        {
            string normalizedId = NormalizeCategoryId(categoryId);
            if (!displayNameById.ContainsKey(normalizedId))
            {
                orderedCategoryIds.Add(normalizedId);
            }

            displayNameById[normalizedId] = NormalizeCategoryDisplayName(displayName, normalizedId);
        }

        foreach (BrowserTabCategoryDefinition category in categoryDefinitions)
        {
            EnsureCategory(category.Id, category.DisplayName);
        }

        foreach (BrowserTabCategorySessionState categoryState in categoryStates)
        {
            EnsureCategory(categoryState.CategoryId, null);
        }

        if (orderedCategoryIds.Count == 0)
        {
            EnsureCategory(BrowserTabSettings.DefaultCategoryId, "既定");
        }

        string activeCategoryId = ResolveActiveCategoryId(
            settings.Session.ActiveBrowserTabCategoryId,
            orderedCategoryIds);

        var snapshot = new BrowserTabRestoreSnapshot
        {
            ActiveCategoryId = activeCategoryId
        };

        foreach (string categoryId in orderedCategoryIds)
        {
            BrowserTabCategorySessionState? categoryState = categoryStates.FirstOrDefault(
                state => string.Equals(state.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));

            snapshot.Categories.Add(new BrowserTabRestoreCategoryState
            {
                Id = categoryId,
                DisplayName = displayNameById[categoryId],
                ActiveTabIndex = categoryState?.ActiveTabIndex ?? 0,
                OpenTabs = categoryState?.OpenTabs.Select(static tab => tab.Clone()).ToList() ?? new List<BrowserTabSessionState>()
            });
        }

        return snapshot;
    }

    private static BrowserTabRestoreSnapshot NormalizeBrowserTabRestoreSnapshot(
        BrowserTabRestoreSnapshot sourceSnapshot,
        IEnumerable<BrowserTabCategoryDefinition> categoryDefinitions,
        IEnumerable<BrowserTabRestoreCategoryState> snapshotCategories)
    {
        var normalizedDefinitions = categoryDefinitions
            .Where(static category => category != null)
            .Select(static category => new BrowserTabCategoryDefinition
            {
                Id = NormalizeCategoryId(category.Id),
                DisplayName = NormalizeCategoryDisplayName(category.DisplayName, category.Id)
            })
            .ToList();

        var snapshotStateById = snapshotCategories
            .Where(static category => category != null)
            .GroupBy(static category => NormalizeCategoryId(category.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Clone(),
                StringComparer.OrdinalIgnoreCase);

        var orderedCategoryIds = new List<string>();
        var displayNameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void EnsureCategory(string? categoryId, string? displayName)
        {
            string normalizedId = NormalizeCategoryId(categoryId);
            if (!displayNameById.ContainsKey(normalizedId))
            {
                orderedCategoryIds.Add(normalizedId);
            }

            displayNameById[normalizedId] = NormalizeCategoryDisplayName(displayName, normalizedId);
        }

        foreach (BrowserTabCategoryDefinition category in normalizedDefinitions)
        {
            EnsureCategory(category.Id, category.DisplayName);
        }

        foreach (BrowserTabRestoreCategoryState snapshotCategory in snapshotStateById.Values)
        {
            EnsureCategory(snapshotCategory.Id, snapshotCategory.DisplayName);
        }

        if (orderedCategoryIds.Count == 0)
        {
            EnsureCategory(BrowserTabSettings.DefaultCategoryId, "既定");
        }

        var normalizedSnapshot = new BrowserTabRestoreSnapshot
        {
            ActiveCategoryId = ResolveActiveCategoryId(sourceSnapshot.ActiveCategoryId, orderedCategoryIds)
        };

        foreach (string categoryId in orderedCategoryIds)
        {
            snapshotStateById.TryGetValue(categoryId, out BrowserTabRestoreCategoryState? snapshotCategory);
            normalizedSnapshot.Categories.Add(new BrowserTabRestoreCategoryState
            {
                Id = categoryId,
                DisplayName = displayNameById[categoryId],
                ActiveTabIndex = snapshotCategory?.ActiveTabIndex ?? 0,
                OpenTabs = snapshotCategory?.OpenTabs.Select(static tab => tab.Clone()).ToList() ?? new List<BrowserTabSessionState>()
            });
        }

        return normalizedSnapshot;
    }

    private static List<BrowserTabCategorySessionState> GetRuntimeCategorySessionStates(AppSettings settings)
    {
        settings.BrowserTabs ??= new BrowserTabSettings();
        settings.Session ??= new SessionSettings();

        List<BrowserTabCategorySessionState> categoryStates = (settings.Session.BrowserTabCategories ?? new List<BrowserTabCategorySessionState>())
            .Where(static state => state != null && !string.IsNullOrWhiteSpace(state.CategoryId))
            .GroupBy(static state => NormalizeCategoryId(state.CategoryId), StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                BrowserTabCategorySessionState state = group.First();
                return new BrowserTabCategorySessionState
                {
                    CategoryId = NormalizeCategoryId(state.CategoryId),
                    ActiveTabIndex = state.ActiveTabIndex,
                    OpenTabs = state.OpenTabs.Select(static tab => tab.Clone()).ToList()
                };
            })
            .ToList();

        string activeCategoryId = ResolveActiveCategoryId(
            settings.Session.ActiveBrowserTabCategoryId,
            categoryStates.Select(static state => state.CategoryId)
                .Concat((settings.BrowserTabs.Categories ?? new List<BrowserTabCategoryDefinition>()).Select(static category => category.Id)));

        if ((settings.Session.OpenTabs?.Count ?? 0) > 0)
        {
            List<BrowserTabSessionState> activeCategoryMirrorTabs = (settings.Session.OpenTabs ?? new List<BrowserTabSessionState>())
                .Select(static tab => tab.Clone())
                .ToList();
            BrowserTabCategorySessionState? activeCategoryState = categoryStates.FirstOrDefault(
                state => string.Equals(state.CategoryId, activeCategoryId, StringComparison.OrdinalIgnoreCase));

            if (activeCategoryState == null)
            {
                categoryStates.Add(new BrowserTabCategorySessionState
                {
                    CategoryId = activeCategoryId,
                    ActiveTabIndex = settings.Session.ActiveTabIndex,
                    OpenTabs = activeCategoryMirrorTabs
                });
            }
            else
            {
                activeCategoryState.ActiveTabIndex = settings.Session.ActiveTabIndex;
                activeCategoryState.OpenTabs = activeCategoryMirrorTabs;
            }
        }

        return categoryStates;
    }

    private static void MaterializeCompatibilityMirror(SessionSettings session, string activeCategoryId)
    {
        List<BrowserTabCategorySessionState> categoryStates = session.BrowserTabCategories ?? new List<BrowserTabCategorySessionState>();
        BrowserTabCategorySessionState? activeCategoryState = categoryStates.FirstOrDefault(
            state => string.Equals(state.CategoryId, activeCategoryId, StringComparison.OrdinalIgnoreCase));

        if (activeCategoryState == null)
        {
            activeCategoryState = categoryStates.FirstOrDefault(static state => state.OpenTabs.Count > 0)
                ?? categoryStates.FirstOrDefault();
        }

        session.ActiveBrowserTabCategoryId = activeCategoryState != null
            ? NormalizeCategoryId(activeCategoryState.CategoryId)
            : BrowserTabSettings.DefaultCategoryId;
        session.OpenTabs = activeCategoryState?.OpenTabs.Select(static tab => tab.Clone()).ToList() ?? new List<BrowserTabSessionState>();
        session.ActiveTabIndex = activeCategoryState?.OpenTabs.Count > 0
            ? Math.Clamp(activeCategoryState.ActiveTabIndex, 0, activeCategoryState.OpenTabs.Count - 1)
            : 0;
    }

    private static string NormalizeCategoryId(string? categoryId)
    {
        string trimmed = string.IsNullOrWhiteSpace(categoryId)
            ? BrowserTabSettings.DefaultCategoryId
            : categoryId.Trim();

        return string.Equals(trimmed, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase)
            ? BrowserTabSettings.DefaultCategoryId
            : trimmed;
    }

    private static string NormalizeCategoryDisplayName(string? displayName, string? categoryId)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        return string.Equals(NormalizeCategoryId(categoryId), BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase)
            ? "既定"
            : NormalizeCategoryId(categoryId);
    }

    private static string ResolveActiveCategoryId(string? requestedCategoryId, IEnumerable<string> categoryIds)
    {
        string normalizedRequestedId = NormalizeCategoryId(requestedCategoryId);
        List<string> normalizedIds = categoryIds
            .Select(NormalizeCategoryId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedIds.Any(id => string.Equals(id, normalizedRequestedId, StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedRequestedId;
        }

        return normalizedIds.FirstOrDefault() ?? BrowserTabSettings.DefaultCategoryId;
    }

    private static SettingsLoadMetadata ExtractLoadMetadata(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            bool isProfileExplicit = false;
            bool isMouseGesturesExplicit = false;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, "Profile", StringComparison.OrdinalIgnoreCase))
                    {
                        isProfileExplicit = true;
                    }

                    if (!string.Equals(property.Name, "Input", StringComparison.OrdinalIgnoreCase) ||
                        property.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (JsonProperty inputProperty in property.Value.EnumerateObject())
                    {
                        if (string.Equals(inputProperty.Name, "EnableMouseGestures", StringComparison.OrdinalIgnoreCase))
                        {
                            isMouseGesturesExplicit = true;
                            break;
                        }
                    }
                }
            }

            return new SettingsLoadMetadata
            {
                IsProfileExplicit = isProfileExplicit,
                IsMouseGesturesExplicit = isMouseGesturesExplicit
            };
        }
        catch
        {
            return new SettingsLoadMetadata();
        }
    }
}
