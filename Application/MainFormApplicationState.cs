using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MidFD.Coordinators;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;
using MidFD.Services.Workspace;

namespace MidFD.Runtime;

/// <summary>
/// Settings の読み込み済み値と実行時profileを所有するapplication境界。
/// MainFormは設定値の保管場所を持たず、このcoordinatorを通じてUIへ反映する。
/// </summary>
internal sealed class SettingsApplicationCoordinator : ISettingsApplicationPort
{
    private readonly SettingsSessionState _state;

    public SettingsApplicationCoordinator()
        : this(new SettingsSessionState())
    {
    }

    internal SettingsApplicationCoordinator(SettingsSessionState state)
    {
        _state = state;
    }

    public AppSettings Value
    {
        get => _state.Value;
        private set => _state.Value = value ?? new AppSettings();
    }

    public FeatureProfile FeatureProfile
    {
        get => _state.FeatureProfile;
        private set => _state.FeatureProfile = value;
    }

    public string? StartupProfileOverride => _state.StartupProfileOverride;

    public event Action<SettingsSqliteStore.SettingsSaveResult>? SaveFailed
    {
        add => SettingsManager.SaveFailed += value;
        remove => SettingsManager.SaveFailed -= value;
    }

    public SettingsRecoveryState? CurrentRecoveryState => SettingsManager.CurrentRecoveryState;

    public void SetStartupProfileOverride(string? value) => _state.StartupProfileOverride = value;

    public void SetFeatureProfile(FeatureProfile value) => FeatureProfile = value;

    public void ApplyRuntimeProfile(bool isMouseGestureExplicit)
    {
        FeatureProfile profile = FeatureProfileService.ResolveRuntimeProfile(
            StartupProfileOverride,
            Value.Profile,
            FeatureProfile.PracticalStable);
        SetFeatureProfile(profile);
        FeatureProfileService.ApplyRuntimeProfile(Value, profile, isMouseGestureExplicit);
    }

    public void ReplaceLoadedValue(AppSettings value)
    {
        value ??= new AppSettings();
        value.NormalizeChildren();
        value.Input.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(value.Input.MouseGestureCommandMap);
        InputSettings.NormalizeAndMigrateFunctionKeyChords(value.Input);
        _state.Value = value;
    }

    public AppSettings ReloadFromStorage(out SettingsManager.SettingsLoadMetadata metadata)
    {
        AppSettings value = SettingsManager.Load(out metadata);
        ReplaceLoadedValue(value);
        return Value;
    }

    public void NormalizeInputSettings()
    {
        Value.NormalizeChildren();
        Value.Input.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(Value.Input.MouseGestureCommandMap);
        InputSettings.NormalizeAndMigrateFunctionKeyChords(Value.Input);
    }

    public void EnsureNormalized() => Value.NormalizeChildren();

    public void Save() => SettingsManager.Save(Value);

    public SettingsSqliteStore.SettingsSaveResult TrySave() => SettingsManager.TrySave(Value);

    public SettingsSqliteStore.SettingsSaveResult TrySaveExplicit(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        NormalizeSettingsDraft(settings);
        return SettingsManager.TrySave(
            settings,
            SettingsManager.SettingsSaveIntent.Explicit,
            allowProtectedReplacement: true);
    }

    public SettingsSqliteStore.SettingsTransferResult ExportSettings(string targetPath, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SettingsManager.Export(targetPath, settings);
    }

    public SettingsSqliteStore.SettingsTransferResult ReadImportedSettings(string sourcePath) =>
        SettingsManager.ReadImport(sourcePath);

    public SettingsSqliteStore.SettingsTransferResult ApplyImportedSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SettingsManager.ApplyImportedSettings(settings, allowProtectedReplacement: true);
    }

    public SettingsPayloadProtectionInfo? CurrentPayloadProtection =>
        SettingsManager.CurrentPayloadProtection is { } protection
            ? new SettingsPayloadProtectionInfo(protection.PayloadVersion, protection.RecoveredFromBackup)
            : null;

    public int CurrentPayloadVersion => SettingsSqliteStore.CurrentPayloadVersion;

    public AppSettings CreateSettingsDraft(AppSettings source, FeatureProfile effectiveProfile)
    {
        ArgumentNullException.ThrowIfNull(source);
        AppSettings draft = source.Clone();
        draft.Profile = FeatureProfileService.ToSettingValue(effectiveProfile);
        NormalizeSettingsDraft(draft);
        return draft;
    }

    public SettingsReloadApplicationResult ReloadAfterDialog(AppSettings previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        AppSettings reloaded = ReloadFromStorage(out SettingsManager.SettingsLoadMetadata metadata);
        return new SettingsReloadApplicationResult(
            reloaded,
            metadata,
            AreLoggingOnlyChanges(previous, reloaded));
    }

    public void SetViewerWordWrap(bool value)
    {
        Value.NormalizeChildren();
        Value.Preview.ViewerWordWrap = value;
    }

    public void SaveViewerWordWrap(bool value)
    {
        SetViewerWordWrap(value);
        Save();
    }

    public void SaveShowBrowserToolbar(bool value)
    {
        SetShowBrowserToolbar(value);
        Save();
    }

    public void SetMarkdownViewerMode(MarkdownViewerMode value)
    {
        Value.NormalizeChildren();
        Value.Preview.MarkdownViewerMode = value;
    }

    public void SetBrowserTabLayoutMode(BrowserTabLayoutMode value)
    {
        Value.NormalizeChildren();
        Value.BrowserTabs.LayoutMode = value;
    }

    public void SetBrowserTabNavigationWidth(int value)
    {
        Value.NormalizeChildren();
        Value.BrowserTabs.NavigationWidth = value;
    }

    public void SetRenameTemplate(bool remember, string? template)
    {
        Value.NormalizeChildren();
        Value.Rename.RememberLastTemplate = remember;
        if (remember)
        {
            Value.Rename.LastTemplate = template;
        }
    }

    public void SetWindowBounds(int x, int y, int width, int height)
    {
        Value.NormalizeChildren();
        Value.Window.X = x;
        Value.Window.Y = y;
        Value.Window.Width = width;
        Value.Window.Height = height;
    }

    public void SetWindowState(int value)
    {
        Value.NormalizeChildren();
        Value.Window.SetState(value);
    }

    public void SetLastPath(string? value)
    {
        Value.NormalizeChildren();
        Value.Session.LastPath = value;
    }

    public void SetSessionListState(int columnCount, SortKind sortKind, bool sortAscending)
    {
        Value.NormalizeChildren();
        Value.Session.LastColumnCount = columnCount;
        Value.Session.LastSortKind = sortKind;
        Value.Session.LastSortAscending = sortAscending;
    }

    public void SetPersistedMarkedPaths(IReadOnlyList<string> paths)
    {
        Value.NormalizeChildren();
        Value.Session.PersistedMarkedPaths = paths.ToList();
    }

    public void ClearBrowserTabRestoreState()
    {
        Value.NormalizeChildren();
        Value.Session.ClearBrowserTabRestoreState();
    }

    public void EnsureBrowserWorkspaceCategoryConfiguration(BrowserWorkspaceApplicationCoordinator workspace)
    {
        Value.NormalizeChildren();
        workspace.EnsureCategoryConfiguration(Value.BrowserTabs, Value.Session);
    }

    public void SyncActiveBrowserWorkspaceCategory(BrowserWorkspaceApplicationCoordinator workspace)
    {
        Value.NormalizeChildren();
        workspace.SyncActiveCategoryFromSession(Value.Session);
    }

    public BrowserWorkspaceRuntimeStateSnapshot CaptureBrowserWorkspaceRuntimeState(
        BrowserWorkspaceApplicationCoordinator workspace)
    {
        Value.NormalizeChildren();
        return workspace.CaptureRuntimeSnapshot(Value.Session);
    }

    public void ApplyBrowserWorkspaceRestoreState(
        BrowserWorkspaceApplicationCoordinator workspace,
        BrowserWorkspaceRuntimeStateSnapshot runtimeState)
    {
        Value.NormalizeChildren();
        workspace.ApplyRestoreSnapshotToSettings(runtimeState, Value.BrowserTabs, Value.Session);
    }

    public BrowserTabRestoreSnapshot EnsureBrowserWorkspaceRestoreSnapshot(
        BrowserWorkspaceApplicationCoordinator workspace)
    {
        Value.NormalizeChildren();
        return workspace.EnsureRestoreSnapshot(Value.Session);
    }

    public BrowserTabRestoreCategoryState? FindBrowserWorkspaceRestoreCategoryState(
        BrowserWorkspaceApplicationCoordinator workspace,
        string categoryId)
    {
        Value.NormalizeChildren();
        return workspace.FindRestoreCategoryState(Value.Session, categoryId);
    }

    public BrowserTabSessionSerializationResult StoreActiveBrowserWorkspaceCategorySessionState(
        BrowserWorkspaceApplicationCoordinator workspace,
        bool updateCompatibilityMirror)
    {
        Value.NormalizeChildren();
        return workspace.StoreActiveCategorySessionState(
            Value.BrowserTabs,
            Value.Session,
            BrowserWorkspaceApplicationCoordinator.GetMaxTabCount(Value.BrowserTabs),
            updateCompatibilityMirror);
    }

    public int ResolveBrowserWorkspaceActiveTabIndex(
        BrowserWorkspaceApplicationCoordinator workspace,
        string categoryId,
        int restoredTabCount)
    {
        Value.NormalizeChildren();
        return workspace.ResolveActiveTabIndex(Value.Session, categoryId, restoredTabCount);
    }

    public IReadOnlyList<BrowserTabSessionState> GetBrowserWorkspaceSessionTabsForRestore(
        BrowserWorkspaceApplicationCoordinator workspace,
        string categoryId,
        out string resolvedCategoryId)
    {
        Value.NormalizeChildren();
        return workspace.GetSessionTabsForRestore(Value.Session, categoryId, out resolvedCategoryId);
    }

    public BrowserMarksClearTransition ClearBrowserCategoryMarks(
        BrowserWorkspaceApplicationCoordinator workspace,
        string categoryId)
    {
        Value.NormalizeChildren();
        return workspace.ClearCategoryMarks(categoryId, Value.Session);
    }

    public BrowserMarksClearTransition ClearAllBrowserMarks(
        BrowserWorkspaceApplicationCoordinator workspace)
    {
        Value.NormalizeChildren();
        return workspace.ClearAllMarks(Value.Session);
    }

    public bool TryAddBrowserCategory(
        BrowserWorkspaceApplicationCoordinator workspace,
        string displayName,
        out BrowserTabCategoryDefinition? category)
    {
        Value.NormalizeChildren();
        return workspace.TryAddCategory(displayName, Value.BrowserTabs, Value.Session, out category);
    }

    public BrowserCategoryReorderTransition ReorderBrowserCategory(
        BrowserWorkspaceApplicationCoordinator workspace,
        string categoryId,
        int delta)
    {
        Value.NormalizeChildren();
        return workspace.ReorderCategoryByDelta(categoryId, delta, Value.BrowserTabs, Value.Session);
    }

    public bool ReorderBrowserCategoryByIndex(
        BrowserWorkspaceApplicationCoordinator workspace,
        int fromIndex,
        int toIndex)
    {
        Value.NormalizeChildren();
        return workspace.ReorderCategory(fromIndex, toIndex, Value.BrowserTabs, Value.Session);
    }

    public bool TryRenameBrowserCategory(
        BrowserWorkspaceApplicationCoordinator workspace,
        string categoryId,
        string displayName,
        out bool duplicateName)
    {
        Value.NormalizeChildren();
        return workspace.TryRenameCategory(categoryId, displayName, Value.BrowserTabs, out duplicateName);
    }

    public BrowserCategoryRemovalTransition RemoveBrowserCategories(
        BrowserWorkspaceApplicationCoordinator workspace,
        IEnumerable<string> categoryIds)
    {
        Value.NormalizeChildren();
        return workspace.RemoveCategoriesAndResolveFallback(categoryIds, Value.BrowserTabs, Value.Session);
    }

    public void SetShowBrowserToolbar(bool value)
    {
        Value.NormalizeChildren();
        Value.Appearance.ShowBrowserToolbar = value;
    }

    public void SetBrowserFileDisplayMode(BrowserFileDisplayMode mode)
    {
        Value.NormalizeChildren();
        Value.Appearance.FileDisplayMode = mode;
        Value.Appearance.ShowFileSizeAndDateInBrowser = mode == BrowserFileDisplayMode.NameSizeDate;
    }

    public void SetImageViewerBounds(int x, int y, int width, int height)
    {
        Value.NormalizeChildren();
        Value.Preview.ImageViewerX = x;
        Value.Preview.ImageViewerY = y;
        Value.Preview.ImageViewerWidth = width;
        Value.Preview.ImageViewerHeight = height;
    }

    public void InitializeManagedTrash()
    {
        EnsureNormalized();
        MidFdManagedTrashService.Initialize(Value);
    }

    public Dictionary<string, List<string>> GetBrowserKeyCommandOverridesSnapshot()
    {
        EnsureNormalized();
        return Value.Input.BrowserKeyCommandOverrides.ToDictionary(
            static pair => pair.Key,
            static pair => new List<string>(pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetDirectoryMoveHistory()
    {
        EnsureNormalized();
        MigrateLegacyMoveDestinationHistory();
        return Value.Session.DirectoryMoveHistory.ToList();
    }

    public void AddDirectoryMoveHistory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            EnsureNormalized();
            MigrateLegacyMoveDestinationHistory();
            if (AddNormalizedDirectoryHistoryEntry(Value.Session.DirectoryMoveHistory, path, maxCount: 30))
            {
                SettingsManager.Save(Value);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"AddDirectoryMoveHistory 失敗: {ex.Message}");
        }
    }

    private void MigrateLegacyMoveDestinationHistory()
    {
        EnsureNormalized();
        List<string> legacy = Value.Session.MoveDestinationHistory;
        if (legacy.Count == 0)
        {
            return;
        }

        bool changed = false;
        foreach (string path in legacy)
        {
            changed |= AddNormalizedDirectoryHistoryEntry(Value.Session.DirectoryMoveHistory, path, maxCount: 30);
        }

        if (changed)
        {
            legacy.Clear();
            SettingsManager.Save(Value);
        }
    }

    private static bool AddNormalizedDirectoryHistoryEntry(IList<string> history, string path, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = Path.GetFullPath(path);
        if (!Directory.Exists(normalized))
        {
            return false;
        }

        if (!normalized.EndsWith(Path.DirectorySeparatorChar))
        {
            normalized += Path.DirectorySeparatorChar;
        }

        int index = -1;
        for (int i = 0; i < history.Count; i++)
        {
            if (string.Equals(history[i], normalized, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index == 0)
        {
            return false;
        }

        if (index > 0)
        {
            history.RemoveAt(index);
        }

        history.Insert(0, normalized);
        while (history.Count > maxCount)
        {
            history.RemoveAt(history.Count - 1);
        }

        return true;
    }

    private static bool AreLoggingOnlyChanges(AppSettings before, AppSettings after)
    {
        AppSettings beforeWithoutLogging = before.Clone();
        AppSettings afterWithoutLogging = after.Clone();
        beforeWithoutLogging.Logging = new LoggingSettings();
        afterWithoutLogging.Logging = new LoggingSettings();
        return string.Equals(
            System.Text.Json.JsonSerializer.Serialize(beforeWithoutLogging),
            System.Text.Json.JsonSerializer.Serialize(afterWithoutLogging),
            StringComparison.Ordinal);
    }

    public void NormalizeSettingsDraft(AppSettings settings)
    {
        settings.NormalizeChildren();
        settings.Input.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(
            settings.Input.MouseGestureCommandMap);
        settings.Input.BrowserKeyCommandOverrides = InputSettings.NormalizeBrowserKeyCommandOverrides(
            settings.Input.BrowserKeyCommandOverrides);
        InputSettings.NormalizeAndMigrateFunctionKeyChords(settings.Input);
    }
}

internal interface ISettingsApplicationPort
{
    AppSettings CreateSettingsDraft(AppSettings source, FeatureProfile effectiveProfile);
    void NormalizeSettingsDraft(AppSettings settings);
    SettingsSqliteStore.SettingsSaveResult TrySaveExplicit(AppSettings settings);
    SettingsSqliteStore.SettingsTransferResult ExportSettings(string targetPath, AppSettings settings);
    SettingsSqliteStore.SettingsTransferResult ReadImportedSettings(string sourcePath);
    SettingsSqliteStore.SettingsTransferResult ApplyImportedSettings(AppSettings settings);
    SettingsPayloadProtectionInfo? CurrentPayloadProtection { get; }
    int CurrentPayloadVersion { get; }
}

internal sealed record SettingsPayloadProtectionInfo(int? PayloadVersion, bool RecoveredFromBackup);

internal sealed record SettingsReloadApplicationResult(
    AppSettings Value,
    SettingsManager.SettingsLoadMetadata Metadata,
    bool IsLoggingOnlyChange);

internal sealed class BrowserSessionState : IDisposable
{
    public BrowserNavigationSessionState NavigationState { get; } = new();
    public BrowserTabViewState Tabs { get; } = new();
    public BrowserCategoryViewState Categories { get; } = new();
    public BrowserSelectionApplicationCoordinator Selection { get; } = new();
    public BrowserWorkspaceState Workspace { get; } = new();

    public BrowserTabState? ActiveTab => Tabs.ActiveTab;

    public void Dispose()
    {
        // FileSystemWatcher is owned by the shell event adapter and is disposed there.
        // The state owner only releases mutable application collections.
        NavigationState.Dispose();
        Selection.Clear();
        Tabs.Clear();
        Categories.Clear();
        Workspace.ClosedBrowserTabs.Clear();
    }
}

internal sealed class BrowserNavigationSessionState
{
    public NavigationService Navigation { get; } = new();
    public NavigationRefreshCoordinator RefreshCoordinator { get; } = new();
    public DirectoryCountAuditGate CountAuditGate { get; } = new();
    public DirectoryCountAuditSchedule CountAuditSchedule { get; } = new();
    public CancellationTokenSource? CountAuditCancellation { get; set; }
    public SortKind CurrentSort { get; set; } = SortKind.Name;
    public bool SortAscending { get; set; } = true;
    public string FilterPattern { get; set; } = string.Empty;
    public bool FilterUseRegex { get; set; }
    public int CursorIndex { get; set; }
    public int PageStartIndex { get; set; }
    public int ColumnCount { get; set; } = 3;
    public int TotalItemCount { get; set; }
    public int ItemsPerPage { get; set; }
    public long DirectoryNavigationGeneration { get; set; }
    public long DirectoryContentGeneration { get; set; }
    public long CurrentDirectoryWatcherGeneration { get; set; }
    public long LastExternalDirectoryReloadMilliseconds { get; set; }
    public string? CurrentDirectoryWatcherPath { get; set; }
    public bool CurrentDirectoryRefreshRetryPending { get; set; }
    public bool IsApplyingDirectoryList { get; set; }

    public void Dispose()
    {
        CountAuditCancellation?.Cancel();
        CountAuditCancellation?.Dispose();
        CountAuditCancellation = null;
    }
}

internal sealed class BrowserWorkspaceState
{
    public QuickAccessStore QuickAccess { get; set; } = new();
    public MarkSlotStore MarkSlots { get; set; } = MarkSlotStore.CreateDefault(5);
    public IWorkspaceStateStore? StateStore { get; set; }
    public WorkspaceSnapshotStorage? SnapshotStorage { get; set; }
    public bool RestoredBrowserTabsFromStore { get; set; }
    public List<ClosedBrowserTabSnapshot> ClosedBrowserTabs { get; } = new();
}

internal enum ViewerApplicationMode
{
    Browser,
    Viewer
}

internal enum ViewerEncodingPreference
{
    Auto,
    Utf8,
    ShiftJis
}

internal sealed class ViewerSessionState : IDisposable
{
    public PreviewRequestCoordinator PreviewRequests { get; } = new();
    public ViewerApplicationMode Mode { get; set; } = ViewerApplicationMode.Browser;
    public PreviewKind CurrentKind { get; set; } = PreviewKind.None;
    public string DetectedEncodingLabel { get; set; } = string.Empty;
    public ViewerEncodingPreference EncodingPreference { get; set; } = ViewerEncodingPreference.Auto;
    public string SearchKeyword { get; set; } = string.Empty;
    public string? MarkdownSource { get; set; }
    public int TextLineCount { get; set; } = 1;
    public LargeFilePreviewState? LargeFileState { get; set; }
    public string? CurrentPreviewTarget { get; set; }
    public string? LastRequestedPath { get; set; }
    public int ActiveRequestId;
    public bool AutoPreviewSuppressed { get; set; }
    public string? LastAutoPreviewSuppressedMessage { get; set; }

    public int ExchangeActiveRequestId(int requestId)
    {
        return Interlocked.Exchange(ref ActiveRequestId, requestId);
    }

    public void Dispose()
    {
        PreviewRequests.Cancel();
    }
}

internal sealed class ViewerApplicationCoordinator
{
    private readonly ViewerSessionState _state;

    public ViewerApplicationCoordinator(ViewerSessionState state)
    {
        _state = state;
    }

    public ViewerApplicationCoordinator()
        : this(new ViewerSessionState())
    {
    }

    public CancellationToken Token => _state.PreviewRequests.Token;
    public int CurrentRequestId => _state.PreviewRequests.CurrentRequestId;
    public bool IsInFlight => _state.PreviewRequests.IsInFlight;
    public int ActiveRequestId => _state.ActiveRequestId;
    public ViewerApplicationMode Mode => _state.Mode;
    public PreviewKind CurrentKind => _state.CurrentKind;
    public string DetectedEncodingLabel => _state.DetectedEncodingLabel;
    public ViewerEncodingPreference EncodingPreference => _state.EncodingPreference;
    public string SearchKeyword => _state.SearchKeyword;
    public string? MarkdownSource => _state.MarkdownSource;
    public int TextLineCount => _state.TextLineCount;
    public LargeFilePreviewState? LargeFileState => _state.LargeFileState;
    public string? CurrentPreviewTarget => _state.CurrentPreviewTarget;
    public string? LastRequestedPath => _state.LastRequestedPath;
    public bool AutoPreviewSuppressed => _state.AutoPreviewSuppressed;
    public string? LastAutoPreviewSuppressedMessage => _state.LastAutoPreviewSuppressedMessage;

    public void SetMode(ViewerApplicationMode mode) => _state.Mode = mode;
    public void SetCurrentKind(PreviewKind kind) => _state.CurrentKind = kind;
    public void SetDetectedEncodingLabel(string value) => _state.DetectedEncodingLabel = value;
    public void SetEncodingPreference(ViewerEncodingPreference value) => _state.EncodingPreference = value;
    public void SetSearchKeyword(string value) => _state.SearchKeyword = value ?? string.Empty;
    public void SetMarkdownSource(string? value) => _state.MarkdownSource = value;
    public void SetTextLineCount(int value) => _state.TextLineCount = Math.Max(1, value);
    public void SetLargeFileState(LargeFilePreviewState? value) => _state.LargeFileState = value;
    public void SetCurrentPreviewTarget(string? value) => _state.CurrentPreviewTarget = value;
    public void SetAutoPreviewSuppressed(bool value) => _state.AutoPreviewSuppressed = value;
    public void SetLastAutoPreviewSuppressedMessage(string? value) => _state.LastAutoPreviewSuppressedMessage = value;
    public void SetLastRequestedPath(string? value) => _state.LastRequestedPath = value;
    public void ExchangeActiveRequestId(int requestId) => _state.ExchangeActiveRequestId(requestId);

    public void SetLargeFileSearchState(string query, bool backward)
    {
        if (_state.LargeFileState == null)
        {
            return;
        }

        _state.LargeFileState.LastSearchText = query ?? string.Empty;
        _state.LargeFileState.LastSearchBackward = backward;
    }

    public void ClearLargeFileSearchHit()
    {
        if (_state.LargeFileState == null)
        {
            return;
        }

        _state.LargeFileState.ActiveSearchHitLine = null;
        _state.LargeFileState.ActiveSearchHitColumn = 0;
        _state.LargeFileState.ActiveSearchHitLength = 0;
    }

    public void SetLargeFileSearchHit(int hitLine, int hitColumn, int hitLength)
    {
        if (_state.LargeFileState == null)
        {
            return;
        }

        _state.LargeFileState.ActiveSearchHitLine = hitLine;
        _state.LargeFileState.ActiveSearchHitColumn = hitColumn;
        _state.LargeFileState.ActiveSearchHitLength = hitLength;
    }

    public void SetLargeFilePendingEndAfterIndex(bool pending)
    {
        if (_state.LargeFileState != null)
        {
            _state.LargeFileState.PendingEndAfterIndex = pending;
        }
    }

    public bool ConsumeLargeFilePendingEndAfterIndex()
    {
        if (_state.LargeFileState == null || !_state.LargeFileState.PendingEndAfterIndex)
        {
            return false;
        }

        _state.LargeFileState.PendingEndAfterIndex = false;
        return true;
    }

    public void SetLargeFileFirstVisibleLine(int line)
    {
        if (_state.LargeFileState != null)
        {
            _state.LargeFileState.FirstVisibleLine = Math.Max(0, line);
        }
    }

    public void CompleteLargeFileIndex(IReadOnlyList<long> lineOffsets, long totalBytes, int maxFirstVisibleLine)
    {
        if (_state.LargeFileState == null)
        {
            return;
        }

        _state.LargeFileState.ReplaceLineOffsets(lineOffsets, totalBytes);
        _state.LargeFileState.IsIndexing = false;
        if (_state.LargeFileState.FirstVisibleLine > maxFirstVisibleLine)
        {
            _state.LargeFileState.FirstVisibleLine = maxFirstVisibleLine;
        }
    }

    public CancellationToken BeginRequest(string requestPath, out int requestId)
    {
        _state.PreviewRequests.Cancel();
        CancellationToken token = _state.PreviewRequests.StartNewRequest(out requestId);
        _state.ExchangeActiveRequestId(requestId);
        _state.LastRequestedPath = requestPath;
        return token;
    }

    public void CancelRequest()
    {
        _state.PreviewRequests.Cancel();
    }

    public bool IsLatestRequest(int requestId, string requestPath, CancellationToken token, string? currentSelectionPath)
    {
        return !token.IsCancellationRequested
            && _state.ActiveRequestId == requestId
            && string.Equals(_state.LastRequestedPath, requestPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(currentSelectionPath, requestPath, StringComparison.OrdinalIgnoreCase);
    }

    public void EndRequestIfCurrent(int requestId)
    {
        if (_state.ActiveRequestId == requestId)
        {
            _state.PreviewRequests.EndRequest(requestId);
        }
    }

    public void EndRequest(int requestId)
    {
        _state.PreviewRequests.EndRequest(requestId);
    }

    public void Dispose() => _state.Dispose();
}

internal sealed class FileOperationState : IDisposable
{
    public FileOperationUiState Ui { get; } = new();
    public bool IsClipboardBusy { get; set; }
    public bool IsUndoRedoBusy { get; set; }

    public bool IsBusy => IsClipboardBusy
        || Ui.Cts != null
        || !string.IsNullOrWhiteSpace(Ui.ActiveOperationName)
        || IsUndoRedoBusy;

    public void Dispose()
    {
        Ui.Reset();
    }
}

internal sealed class SettingsSessionState
{
    public AppSettings Value { get; set; } = new();
    public FeatureProfile FeatureProfile { get; set; } = FeatureProfile.Full;
    public string? StartupProfileOverride { get; set; }
}
