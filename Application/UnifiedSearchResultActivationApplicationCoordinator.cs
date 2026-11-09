using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MidFD.Commands;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

internal interface IUnifiedSearchResultActivationUiPort
{
    bool IsSearchSessionCurrent(Guid sessionId);
    bool IsViewerMode { get; }
    bool TryExitViewerToBrowser();
    bool SetSearchSurfaceActive(Guid sessionId, bool active);
    bool DispatchCommand(string commandId, CommandExecutionContext context);
    bool TryOpenPreview(Guid sessionId, Guid resultTabId, UnifiedSearchMatch hit, out int requestId);
    void ClearPendingPreview(Guid sessionId);
    void RefreshSearchTabProjection();
    void RequestForeground(bool preview);
}

internal sealed record UnifiedSearchResultActivationExecution(
    Guid SessionId,
    Guid ResultTabId,
    bool OpenedPreview,
    string? ErrorMessage);

internal sealed record UnifiedSearchHitEditorExecution(
    Guid SessionId,
    string TargetPath,
    UnifiedSearchMatch? Hit,
    string? ErrorMessage)
{
    public int? LineNumber => Hit?.LineNumber;
    public int? ColumnNumber => Hit?.ColumnNumber;
}

internal sealed class UnifiedSearchResultActivationApplicationCoordinator
{
    private sealed class SessionState(Guid id, string sourceCategoryId, Guid sourceTabId, string rootPath, UnifiedFilterFindMode mode)
    {
        public Guid Id { get; } = id;
        public string SourceCategoryId { get; } = sourceCategoryId;
        public Guid SourceTabId { get; } = sourceTabId;
        public string RootPath { get; } = rootPath;
        public UnifiedFilterFindMode Mode { get; } = mode;
        public bool IsActive { get; set; }
        public Guid? ReusableInspectionTabId { get; set; }
        public List<Guid> AdditionalResultTabIds { get; } = [];
    }

    private sealed record ActivationReturnContext(
        Guid SessionId,
        string SourceCategoryId,
        Guid SourceTabId,
        Guid ResultTabId,
        int? ViewerRequestId,
        bool IsNewTab);

    private readonly BrowserApplicationCoordinator _browser;
    private readonly BrowserWorkspacePersistenceApplicationCoordinator _persistence;
    private readonly ICommandApplicationShellPort _shell;
    private readonly IUnifiedSearchResultActivationUiPort _ui;
    private readonly Dictionary<Guid, SessionState> _sessions = [];
    private readonly List<BrowserTabNavigationSearchDerivedTab> _derivedTabs = [];
    private ActivationReturnContext? _previewReturnContext;
    private ActivationReturnContext? _nameInspectionReturnContext;
    private long _creationOrder;

    public UnifiedSearchResultActivationApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        BrowserWorkspacePersistenceApplicationCoordinator persistence,
        ICommandApplicationShellPort shell,
        IUnifiedSearchResultActivationUiPort ui)
    {
        _browser = browser;
        _persistence = persistence;
        _shell = shell;
        _ui = ui;
    }

    public void RegisterSession(Guid sessionId, string sourceCategoryId, Guid sourceTabId, string rootPath, UnifiedFilterFindMode mode)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(sourceCategoryId) || sourceTabId == Guid.Empty || string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        string normalizedRoot;
        try { normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException) { return; }
        _sessions[sessionId] = new SessionState(sessionId, sourceCategoryId, sourceTabId, normalizedRoot, mode);
    }

    public void UnregisterSession(Guid sessionId)
    {
        _sessions.Remove(sessionId);
        if (_previewReturnContext?.SessionId == sessionId) _previewReturnContext = null;
        if (_nameInspectionReturnContext?.SessionId == sessionId) _nameInspectionReturnContext = null;
    }

    public void ForgetDerivedTab(Guid tabId) => _derivedTabs.RemoveAll(item => item.ResultTabId == tabId);

    public void SetSessionActive(Guid sessionId, bool active)
    {
        if (_sessions.TryGetValue(sessionId, out SessionState? session)) session.IsActive = active;
    }

    public bool CanFilterSearchResults(Guid sessionId) =>
        sessionId != Guid.Empty &&
        _sessions.TryGetValue(sessionId, out SessionState? session) &&
        session.IsActive &&
        _ui.IsSearchSessionCurrent(sessionId);

    public UnifiedSearchHitEditorExecution? TryPrepareHitEditor(UnifiedSearchHitEditorCommandContext? request)
    {
        if (request == null || request.SessionId == Guid.Empty ||
            !_sessions.TryGetValue(request.SessionId, out SessionState? session) ||
            !session.IsActive || session.Mode is not (UnifiedFilterFindMode.SearchContentsRecursively or UnifiedFilterFindMode.FindNamesRecursively) ||
            !_ui.IsSearchSessionCurrent(session.Id))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.TargetPath)) return new(session.Id, request.TargetPath ?? string.Empty, request.Hit, "対象pathが指定されていません。");
        string? error = request.Hit == null
            ? session.Mode == UnifiedFilterFindMode.FindNamesRecursively &&
              request.HitIdentity.HitLineNumber == null && request.HitIdentity.HitColumnNumber == null &&
              request.HitIdentity.MatchText == null && PathsEqual(request.TargetPath, request.HitIdentity.FullPath)
                ? null
                : "名前検索ではfile単位の外部Editor起動だけを受け付けます。"
            : session.Mode == UnifiedFilterFindMode.SearchContentsRecursively
                ? ValidateHitEditorRequest(request)
                : "名前検索から行・列指定は受け付けません。";
        if (error != null)
        {
            return new(session.Id, request.TargetPath, request.Hit, error);
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(request.TargetPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        { return new(session.Id, request.TargetPath, request.Hit, "対象pathが不正です。"); }

        if (!IsUnderRoot(session.RootPath, fullPath))
        {
            return new(session.Id, fullPath, request.Hit, "対象pathが検索root外です。");
        }
        if (Directory.Exists(fullPath))
        {
            return new(session.Id, fullPath, request.Hit, "フォルダは外部Editorで開けません。");
        }
        if (!File.Exists(fullPath))
        {
            return new(session.Id, fullPath, request.Hit,
                $"対象が見つかりません: {fullPath}");
        }

        return new(session.Id, fullPath, request.Hit, null);
    }

    private static string? ValidateHitEditorRequest(UnifiedSearchHitEditorCommandContext request)
    {
        UnifiedSearchMatch? hit = request.Hit;
        if (hit == null) return "検索hitがありません。";
        if (string.IsNullOrWhiteSpace(request.TargetPath) ||
            !PathsEqual(request.TargetPath, hit.FullPath) ||
            !PathsEqual(request.TargetPath, request.HitIdentity.FullPath) ||
            request.HitIdentity.HitLineNumber != hit.LineNumber ||
            request.HitIdentity.HitColumnNumber != hit.ColumnNumber ||
            !string.Equals(request.HitIdentity.MatchText, hit.MatchText, StringComparison.Ordinal))
        {
            return "検索hitの対象情報が一致しません。";
        }

        if (hit.LineNumber < 1 || hit.ColumnNumber < 1 || hit.ColumnNumber > hit.LineText.Length + 1)
        {
            return "検索hitの行・列情報が不正です。";
        }

        int matchOffset = hit.ColumnNumber - 1;
        if (!string.IsNullOrEmpty(hit.MatchText) &&
            (matchOffset + hit.MatchText.Length > hit.LineText.Length ||
             !hit.LineText.AsSpan(matchOffset, hit.MatchText.Length).SequenceEqual(hit.MatchText.AsSpan())))
        {
            return "検索hitの行・列情報が不正です。";
        }

        try
        {
            _ = Path.GetFullPath(request.TargetPath);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return "検索hitの対象pathが不正です。";
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(root, path);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public IReadOnlyList<BrowserTabNavigationSearchDerivedTab> GetDerivedTabs()
    {
        PruneDerivedTabs();
        return _derivedTabs.ToArray();
    }

    public IReadOnlySet<Guid> GetBlockedResultTabIds(string categoryId)
    {
        PruneDerivedTabs();
        SessionState? session = _sessions.Values.FirstOrDefault(item => item.IsActive &&
            string.Equals(item.SourceCategoryId, categoryId, StringComparison.OrdinalIgnoreCase));
        if (session == null) return new HashSet<Guid>();
        return session.AdditionalResultTabIds
            .Append(session.ReusableInspectionTabId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
    }

    public bool IsResultTabBlocked(string categoryId, Guid tabId) => GetBlockedResultTabIds(categoryId).Contains(tabId);

    public void PruneDerivedTabs()
    {
        HashSet<string> categoryIds = _browser.Workspace.CategoryStates
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = _derivedTabs.Count - 1; index >= 0; index--)
        {
            BrowserTabNavigationSearchDerivedTab lineage = _derivedTabs[index];
            if (!categoryIds.Contains(lineage.SourceCategoryId))
            {
                _derivedTabs.RemoveAt(index);
                continue;
            }

            IReadOnlyList<Guid> tabIds = string.Equals(
                lineage.SourceCategoryId,
                _browser.Workspace.ActiveCategoryId,
                StringComparison.OrdinalIgnoreCase)
                    ? _browser.Workspace.TabStates.Select(item => item.Id).ToArray()
                    : _persistence.GetSessionTabsForRestore(lineage.SourceCategoryId, out _).Select(item => item.TabId).ToArray();
            if (!tabIds.Contains(lineage.ResultTabId) || !tabIds.Contains(lineage.SourceTabId))
            {
                _derivedTabs.RemoveAt(index);
            }
        }
    }

    public void ClearReturnContexts(Guid? sessionId = null)
    {
        ClearPreviewReturnContext(sessionId);
        ClearNameInspectionReturnContext(sessionId);
    }

    public void ClearPreviewReturnContext(Guid? sessionId = null)
    {
        if (sessionId == null || _previewReturnContext?.SessionId == sessionId) _previewReturnContext = null;
    }

    public void ClearNameInspectionReturnContext(Guid? sessionId = null)
    {
        if (sessionId == null || _nameInspectionReturnContext?.SessionId == sessionId) _nameInspectionReturnContext = null;
    }

    public void ClearNameInspectionReturnContextUnlessSource(Guid targetTabId, string targetCategoryId)
    {
        if (_nameInspectionReturnContext is not { } context) return;
        if (context.ResultTabId == targetTabId ||
            (context.SourceTabId == targetTabId && string.Equals(context.SourceCategoryId, targetCategoryId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        _nameInspectionReturnContext = null;
    }

    public bool ApplyPreviewReturn(int activeViewerRequestId, string? activeCategoryId, Guid? activeTabId)
    {
        ActivationReturnContext? context = _previewReturnContext;
        _previewReturnContext = null;
        if (context == null || context.ViewerRequestId != activeViewerRequestId ||
            context.SessionId == Guid.Empty || context.ResultTabId != activeTabId ||
            !string.Equals(context.SourceCategoryId, activeCategoryId, StringComparison.OrdinalIgnoreCase) ||
            !_sessions.TryGetValue(context.SessionId, out SessionState? session) ||
            !_ui.IsSearchSessionCurrent(context.SessionId) ||
            session.SourceTabId != context.SourceTabId ||
            !string.Equals(session.SourceCategoryId, context.SourceCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DispatchSearchSurfaceActivation(session, "UnifiedSearchResults.ReturnAfterPreview");
    }

    public bool ApplyNameInspectionReturn(string? activeCategoryId, Guid? activeTabId)
    {
        ActivationReturnContext? context = _nameInspectionReturnContext;
        _nameInspectionReturnContext = null;
        if (context == null || context.ResultTabId != activeTabId ||
            !string.Equals(context.SourceCategoryId, activeCategoryId, StringComparison.OrdinalIgnoreCase) ||
            !_sessions.TryGetValue(context.SessionId, out SessionState? session) ||
            !_ui.IsSearchSessionCurrent(context.SessionId) ||
            session.SourceTabId != context.SourceTabId ||
            !string.Equals(session.SourceCategoryId, context.SourceCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DispatchSearchSurfaceActivation(session, "UnifiedSearchResults.ReturnAfterInspection");
    }

    private bool DispatchSearchSurfaceActivation(SessionState session, string source)
    {
        return _ui.DispatchCommand(CommandIds.BrowserTabActivate, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = source,
            CategoryId = session.SourceCategoryId,
            ContextBrowserTabId = session.SourceTabId,
            BrowserTabNavigation = new BrowserTabNavigationCommandContext(
                IncludeUnifiedSearchResults: true,
                UnifiedSearchSessionId: session.Id,
                UnifiedSearchSourceCategoryId: session.SourceCategoryId,
                UnifiedSearchSourceTabId: session.SourceTabId,
                UnifiedSearchIsActive: session.IsActive)
        });
    }

    public UnifiedSearchResultActivationExecution? TryActivate(
        string commandId,
        UnifiedSearchResultActivationCommandContext? request)
    {
        bool inNewTab = commandId == CommandIds.BrowserSearchResultsActivateInNewTab;
        if (!inNewTab && commandId != CommandIds.BrowserSearchResultsActivate)
        {
            return null;
        }
        if (request == null || request.SessionId == Guid.Empty ||
            !_sessions.TryGetValue(request.SessionId, out SessionState? session))
        {
            return Failure(request?.SessionId ?? Guid.Empty, "検索sessionが終了しました。検索結果を開き直してください。");
        }

        string? requestError = ValidateRequest(request, session, inNewTab);
        if (requestError != null) return Failure(session.Id, requestError);
        if (!_ui.IsSearchSessionCurrent(session.Id))
        {
            return Failure(session.Id, "検索結果が更新されました。検索結果を開き直してください。");
        }
        string? targetError = ValidateTarget(request);
        if (targetError != null) return Failure(session.Id, targetError);
        if (!IsSourceTabAvailable(session))
        {
            return Failure(session.Id, "検索元のBrowserタブは利用できません。検索結果を閉じずに、利用可能なタブへ戻ってください。");
        }

        if (_ui.IsViewerMode && !_ui.TryExitViewerToBrowser())
        {
            return Failure(session.Id, "Viewerを閉じられませんでした。");
        }

        if (session.IsActive && !_ui.SetSearchSurfaceActive(session.Id, active: false))
        {
            return Failure(session.Id, "検索結果を切り替えられませんでした。");
        }

        if (!DispatchTabActivation(session.SourceCategoryId, session.SourceTabId, "UnifiedSearchResults.SourceTab"))
        {
            return FailAndRestore(session, "検索元のBrowserタブへ切り替えられませんでした。");
        }
        if (!string.Equals(_browser.Workspace.ActiveCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase) ||
            _browser.Workspace.ActiveTabSnapshot?.Id != session.SourceTabId)
        {
            return FailAndRestore(session, "検索元のBrowserタブへ切り替えられませんでした。");
        }

        if (!TryResolveResultTab(session, inNewTab, out Guid resultTabId, out string? tabError))
        {
            return FailAndRestore(session, tabError ?? "検索結果を開くBrowserタブを準備できませんでした。", resultTabId);
        }

        int? previewRequestId = null;
        string? error = UnifiedSearchResultActivationService.Activate(
            request.TargetPath,
            request.IsDirectory,
            preview: request.Hit != null,
            (parent, name) => _ui.DispatchCommand(CommandIds.BrowserNavigateToPath, new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "UnifiedSearchResults.NavigateToResult",
                BrowserDirectoryNavigation = new BrowserDirectoryNavigationCommandContext(parent, name)
            }),
            () => _shell.CaptureState().CurrentItemPath,
            () =>
            {
                if (request.Hit == null) return false;
                if (!_ui.TryOpenPreview(session.Id, resultTabId, request.Hit, out int requestId)) return false;
                previewRequestId = requestId;
                return true;
            },
            _ui.RequestForeground);

        if (error != null)
        {
            return FailAndRestore(session, error, resultTabId);
        }

        ClearReturnContexts();
        if (request.Hit == null) _ui.ClearPendingPreview(session.Id);
        var returnContext = new ActivationReturnContext(
            session.Id,
            session.SourceCategoryId,
            session.SourceTabId,
            resultTabId,
            previewRequestId,
            inNewTab);
        if (request.Hit != null)
        {
            _previewReturnContext = returnContext;
        }
        else
        {
            _nameInspectionReturnContext = returnContext;
        }

        return new UnifiedSearchResultActivationExecution(session.Id, resultTabId, request.Hit != null, null);
    }

    private string? ValidateTarget(UnifiedSearchResultActivationCommandContext request)
    {
        bool exists = request.IsDirectory ? Directory.Exists(request.TargetPath) : File.Exists(request.TargetPath);
        if (!exists) return $"対象が見つかりません: {request.TargetPath}";
        string? parent = Path.GetDirectoryName(request.TargetPath);
        return string.IsNullOrEmpty(parent) ? "対象の親ディレクトリを取得できませんでした。" : null;
    }

    private bool IsSourceTabAvailable(SessionState session)
    {
        if (!_browser.Workspace.CategoryStates.Any(item => string.Equals(
                item.Id, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.Equals(_browser.Workspace.ActiveCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return _browser.Workspace.TabStates.Any(item => item.Id == session.SourceTabId);
        }

        return _persistence.GetSessionTabsForRestore(session.SourceCategoryId, out string resolvedCategoryId)
            .Any(item => item.TabId == session.SourceTabId && string.Equals(
                resolvedCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase));
    }

    private UnifiedSearchResultActivationExecution FailAndRestore(
        SessionState session,
        string message,
        Guid resultTabId = default)
    {
        if (session.IsActive || !_ui.IsSearchSessionCurrent(session.Id))
        {
            return Failure(session.Id, message, resultTabId);
        }

        bool restored = DispatchSearchSurfaceActivation(session, "UnifiedSearchResults.ActivationFailure");
        return restored
            ? Failure(session.Id, message, resultTabId)
            : Failure(session.Id, $"{message} 検索結果へ戻れませんでした。", resultTabId);
    }

    private string? ValidateRequest(
        UnifiedSearchResultActivationCommandContext request,
        SessionState session,
        bool inNewTab)
    {
        if (!string.Equals(session.SourceCategoryId, request.SourceCategoryId, StringComparison.OrdinalIgnoreCase) ||
            session.SourceTabId != request.SourceTabId || session.Mode != request.SearchMode)
        {
            return "検索結果の起点が置き換わりました。検索結果を開き直してください。";
        }
        if ((request.Origin == UnifiedSearchResultActivationOrigin.CtrlEnter) != inNewTab)
        {
            return "検索結果の入力操作とCommandが一致しません。";
        }
        if (string.IsNullOrWhiteSpace(request.TargetPath) || request.ResultIdentity == null ||
            !PathsEqual(request.TargetPath, request.ResultIdentity.FullPath))
        {
            return "検索結果の対象を確認できませんでした。";
        }
        if (session.Mode == UnifiedFilterFindMode.SearchContentsRecursively)
        {
            if (request.IsDirectory || request.Hit is not { } hit ||
                !PathsEqual(request.TargetPath, hit.FullPath) ||
                request.ResultIdentity.HitLineNumber != hit.LineNumber ||
                request.ResultIdentity.HitColumnNumber != hit.ColumnNumber ||
                !string.Equals(request.ResultIdentity.MatchText, hit.MatchText, StringComparison.Ordinal))
            {
                return "検索hitの対象情報が一致しません。";
            }
        }
        else if (request.Hit != null ||
            request.ResultIdentity.HitLineNumber != null || request.ResultIdentity.HitColumnNumber != null ||
            request.ResultIdentity.MatchText != null)
        {
            return "名前検索結果に内容検索hitが含まれています。";
        }
        return null;
    }

    private bool TryResolveResultTab(SessionState session, bool inNewTab, out Guid resultTabId, out string? error)
    {
        resultTabId = Guid.Empty;
        error = null;
        IReadOnlyList<BrowserTabState> tabs = _browser.Workspace.TabStates;
        if (!inNewTab && session.ReusableInspectionTabId is { } reusableId)
        {
            int reusableIndex = GetTabIndex(tabs, reusableId);
            if (reusableIndex >= 0)
            {
                if (!DispatchTabActivation(session.SourceCategoryId, reusableId, "UnifiedSearchResults.ReuseTab"))
                {
                    error = "確認用Browserタブへ切り替えられませんでした。";
                    return false;
                }
                resultTabId = reusableId;
                return true;
            }
            session.ReusableInspectionTabId = null;
        }

        session.AdditionalResultTabIds.RemoveAll(id => GetTabIndex(tabs, id) < 0);
        Guid anchorTabId = inNewTab ? session.AdditionalResultTabIds.LastOrDefault() : Guid.Empty;
        if (anchorTabId == Guid.Empty && inNewTab && session.ReusableInspectionTabId is { } existingInspection &&
            GetTabIndex(tabs, existingInspection) >= 0)
        {
            anchorTabId = existingInspection;
        }
        if (anchorTabId == Guid.Empty)
        {
            anchorTabId = _derivedTabs
                .Where(item => string.Equals(item.SourceCategoryId, session.SourceCategoryId, StringComparison.OrdinalIgnoreCase) &&
                    item.SourceTabId == session.SourceTabId && GetTabIndex(tabs, item.ResultTabId) >= 0)
                .OrderByDescending(item => item.CreationOrder)
                .Select(item => item.ResultTabId)
                .FirstOrDefault();
        }
        if (anchorTabId == Guid.Empty) anchorTabId = session.SourceTabId;
        int anchorIndex = GetTabIndex(tabs, anchorTabId);
        if (anchorIndex < 0)
        {
            error = "検索結果タブの挿入位置を取得できませんでした。";
            return false;
        }
        if (_browser.Workspace.ActiveTabSnapshot?.Id != anchorTabId &&
            !DispatchTabActivation(session.SourceCategoryId, anchorTabId, "UnifiedSearchResults.AnchorTab"))
        {
            error = "検索結果タブの挿入先へ切り替えられませんでした。";
            return false;
        }

        HashSet<Guid> tabIdsBeforeCreation = tabs.Select(item => item.Id).ToHashSet();
        bool creationDispatched = _ui.DispatchCommand(CommandIds.BrowserTabNew, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "UnifiedSearchResults.CreateInspectionTab"
        });

        IReadOnlyList<BrowserTabState> tabsAfterCreation = _browser.Workspace.TabStates;
        BrowserTabState[] createdTabs = tabsAfterCreation.Where(item => !tabIdsBeforeCreation.Contains(item.Id)).ToArray();
        if (createdTabs.Length > 0)
        {
            TrackCreatedResultTabs(session, createdTabs, inNewTab);
            _ui.RefreshSearchTabProjection();
        }
        if (!creationDispatched)
        {
            resultTabId = createdTabs.FirstOrDefault(item => item.Id == _browser.Workspace.ActiveTabSnapshot?.Id)?.Id
                ?? createdTabs.LastOrDefault()?.Id
                ?? Guid.Empty;
            error = "結果確認用のBrowserタブを作成できませんでした。";
            return false;
        }

        BrowserTabState? createdTab = _browser.Workspace.ActiveTabSnapshot;
        if (createdTab == null || !createdTabs.Any(item => item.Id == createdTab.Id))
        {
            error = "作成したBrowserタブを取得できませんでした。";
            return false;
        }

        resultTabId = createdTab.Id;

        IReadOnlyList<BrowserTabState> updatedTabs = _browser.Workspace.TabStates;
        int currentAnchorIndex = GetTabIndex(updatedTabs, anchorTabId);
        int createdIndex = GetTabIndex(updatedTabs, resultTabId);
        if (currentAnchorIndex < 0 || createdIndex < 0)
        {
            error = "検索結果タブの配置を確定できませんでした。";
            return false;
        }
        if (createdIndex != currentAnchorIndex + 1 &&
            !_ui.DispatchCommand(CommandIds.BrowserTabReorder, new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                CategoryId = session.SourceCategoryId,
                Source = "UnifiedSearchResults.PositionResultTab",
                BrowserTabReorder = new BrowserTabReorderCommandContext(
                    resultTabId,
                    anchorTabId,
                    InsertAfter: true,
                    FocusBrowserAfter: true,
                    NotifyUser: false)
            }))
        {
            error = "検索結果タブを検索元の後方へ配置できませんでした。";
            return false;
        }

        if (_browser.Workspace.ActiveTabSnapshot?.Id != resultTabId)
        {
            error = "作成した検索結果タブが有効になりませんでした。";
            return false;
        }
        _ui.RefreshSearchTabProjection();
        return true;
    }

    private void TrackCreatedResultTabs(SessionState session, IReadOnlyList<BrowserTabState> createdTabs, bool inNewTab)
    {
        foreach (BrowserTabState tab in createdTabs)
        {
            if (!_derivedTabs.Any(item => item.ResultTabId == tab.Id))
            {
                _derivedTabs.Add(new BrowserTabNavigationSearchDerivedTab(
                    tab.Id,
                    session.SourceCategoryId,
                    session.SourceTabId,
                    ++_creationOrder));
            }

            if (inNewTab)
            {
                if (!session.AdditionalResultTabIds.Contains(tab.Id)) session.AdditionalResultTabIds.Add(tab.Id);
            }
            else
            {
                session.ReusableInspectionTabId = tab.Id;
            }
        }
    }

    private bool DispatchTabActivation(string categoryId, Guid tabId, string source)
    {
        if (string.Equals(_browser.Workspace.ActiveCategoryId, categoryId, StringComparison.OrdinalIgnoreCase) &&
            _browser.Workspace.ActiveTabSnapshot?.Id == tabId)
        {
            return true;
        }
        return _ui.DispatchCommand(CommandIds.BrowserTabActivate, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            CategoryId = categoryId,
            ContextBrowserTabId = tabId,
            Source = source
        });
    }

    private static int GetTabIndex(IReadOnlyList<BrowserTabState> tabs, Guid tabId)
    {
        for (int index = 0; index < tabs.Count; index++)
        {
            if (tabs[index].Id == tabId) return index;
        }
        return -1;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static UnifiedSearchResultActivationExecution Failure(Guid sessionId, string message, Guid resultTabId = default) =>
        new(sessionId, resultTabId, OpenedPreview: false, ErrorMessage: message);
}
