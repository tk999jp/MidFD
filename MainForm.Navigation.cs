using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{    private bool LoadDirectory(string targetPath, string? focusTargetName = null, bool isHistoryNavigation = false, bool suppressRecent = false)
    {
        HideBrowserFileNameToolTip();
        try
        {
            var request = CreateDirectoryLoadRequest(targetPath, focusTargetName, isHistoryNavigation, suppressRecent);
            var result = _browserLoadCoordinator.Execute(
                request,
                new BrowserLoadCoordinator.ExecutionContext
                {
                    ShowStatusMessage = ShowStatusMessage,
                    DecoratePathItem = ApplyMarkColor
                });
            // 成功時 UI 反映のオーケストレーション
            ApplyDirectoryLoadUi(result);
            return true;
        }
        catch (Exception ex)
        {
            return NotifyDirectoryLoadFailure(ex);
        }
    }
    private BrowserLoadCoordinator.DirectoryLoadRequest CreateDirectoryLoadRequest(
        string targetPath,
        string? focusTargetName,
        bool isHistoryNavigation,
        bool suppressRecent)
    {
        string? currentFullName = null;
        var currentItem = GetCurrentBrowserItem();
        if (currentItem != null)
        {
            currentFullName = GetItemFullName(currentItem);
        }
        return new BrowserLoadCoordinator.DirectoryLoadRequest(
            targetPath,
            focusTargetName,
            isHistoryNavigation,
            suppressRecent,
            _navigationService.CurrentPath,
            _browserCursorIndex,
            currentFullName,
            _filterPattern,
            _filterUseRegex,
            _settings.Appearance?.ShowHiddenFiles ?? false,
            _currentSort,
            _sortAscending,
            GetActiveTabFilterLock(),
            _settings.Appearance?.DateFormat,
            _settings.Appearance?.SizeFormat,
            _settings.Appearance?.ShowDirectoryMarker ?? true);
    }
    private void PopulateListView(IReadOnlyList<ListViewItem> items)
    {
        fileListView.BeginUpdate();
        fileListView.Items.Clear();
        try
        {
            if (items.Count > 0)
            {
                fileListView.Items.AddRange(items.ToArray());
            }
        }
        finally
        {
            fileListView.EndUpdate();
        }
    }
    private void ApplyDirectoryLoadUi(BrowserLoadCoordinator.DirectoryLoadResult result)
    {
        DismissTransientContextMenus();
        _browserMarkInteractionController.ClearPendingPromotionCandidate();
        bool directoryChanged = !string.Equals(
            NavigationService.NormalizeDirectoryForCompare(result.PreviousPath),
            NavigationService.NormalizeDirectoryForCompare(result.NewPath),
            StringComparison.OrdinalIgnoreCase);
        if (directoryChanged)
        {
            InvalidateRecentMultiMarkIntent();
            InvalidateMarkSummaryCache();
        }
        // 1. 内部状態とパス表示の更新
        _navigationService.SetCurrentPath(result.NewPath, result.IsHistoryNavigation);
        // 2. 一覧項目の再構築
        PopulateListView(result.Items);
        // 3. 選択状態の復元
        RestoreSelectionState(result.FocusTargetName, result.LastIndex, result.IsReload);
        // 4. パネル再描画 (RestoreSelectionState 内で UpdateInfoPanel も呼ばれるためここでは Invalidate のみ)
        browserPanel.Invalidate();
        if (!result.SuppressRecent)
        {
            RecordQuickAccessRecent(result.PreviousPath, result.NewPath, result.IsReload);
        }
        CaptureActiveBrowserTabState(captureMarks: false);
        UpdateCurrentDirectoryWatcher(result.NewPath, "ApplyDirectoryLoadUi");
        TryProcessPendingCurrentDirectoryRefresh("ApplyDirectoryLoadUi");
        UpdateMenuStripState();
        // Phase: header stream / initial final relayout corrective follow-up
        // ディレクトリ読み込みとタブ状態確定後の最終レイアウトを保証する
        UpdateInfoPanel();
    }
    private void RecordQuickAccessRecent(string previousPath, string newPath, bool isReload)
    {
        if (isReload || string.IsNullOrWhiteSpace(previousPath))
        {
            return;
        }
        if (QuickAccessService.PathsEqual(previousPath, newPath))
        {
            return;
        }
        if (QuickAccessService.RecordRecent(_quickAccessStore, newPath))
        {
            QuickAccessService.Save(_quickAccessStore);
        }
    }
    private bool NotifyDirectoryLoadFailure(Exception ex)
    {
        ShowStatusMessage($"読み込み失敗: {ex.Message}");
        return false;
    }
    private bool ReloadCurrentDirectory(string reason, bool force = false)
    {
        string currentPath = _navigationService.CurrentPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            ShowStatusMessage("現在ディレクトリが未確定のため再読込できません。");
            return false;
        }
        if (!force && IsCurrentDirectoryRefreshBlocked())
        {
            return false;
        }
        if (!Directory.Exists(currentPath))
        {
            if (NavigationFallbackResolver.TryResolveExistingDirectoryFallback(
                currentPath,
                message => LogService.Error(message),
                out string fallbackPath,
                out string fallbackReason))
            {
                LogService.Info($"[DirectoryRefresh] Fallback triggered. missing={currentPath}, fallback={fallbackPath}, reason={fallbackReason}");
                ShowStatusMessage($"現在のフォルダが見つからないため、{fallbackReason}フォルダへ移動しました。");
                return LoadDirectory(fallbackPath);
            }
            UpdateCurrentDirectoryWatcher(null, "CurrentDirectoryMissing");
            ShowStatusMessage("現在ディレクトリが見つかりません。");
            return false;
        }
        bool loaded = LoadDirectory(currentPath);
        if (loaded)
        {
            ShowStatusMessage(reason);
            return true;
        }
        if (_currentDirectoryRefreshRetryPending)
        {
            return false;
        }
        _currentDirectoryRefreshRetryPending = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CurrentDirectoryRefreshRetryDelayMilliseconds).ConfigureAwait(false);
                if (IsDisposed || !IsHandleCreated)
                {
                    return;
                }
                BeginInvoke(new Action(() =>
                {
                    _currentDirectoryRefreshRetryPending = false;
                    if (!string.Equals(
                        NormalizeDirectoryWatchPath(_navigationService.CurrentPath),
                        NormalizeDirectoryWatchPath(currentPath),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    if (!Directory.Exists(currentPath))
                    {
                        UpdateCurrentDirectoryWatcher(null, "RetryDirectoryMissing");
                        ShowStatusMessage("現在ディレクトリが見つかりません。");
                        return;
                    }
                    if (LoadDirectory(currentPath))
                    {
                        ShowStatusMessage(reason);
                    }
                }));
            }
            catch (ObjectDisposedException)
            {
                _currentDirectoryRefreshRetryPending = false;
            }
        });
        return false;
    }
    private bool ExecuteCurrentDirectoryReloadCommand()
    {
        if (_uiMode != UIMode.Browser)
        {
            return false;
        }
        if (IsCurrentDirectoryBusy())
        {
            ShowStatusMessage("処理中のため再読込できません。");
            return true;
        }
        ClearPendingCurrentDirectoryRefresh();
        ReloadCurrentDirectory("現在ディレクトリを再読込しました。");
        return true;
    }
    private void QueueCurrentDirectoryRefresh(string watchedDirectoryPath, string reason, Exception? exception = null)
    {
        if (IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => QueueCurrentDirectoryRefresh(watchedDirectoryPath, reason, exception)));
            }
            catch (ObjectDisposedException)
            {
            }
            return;
        }
        string normalizedWatchedPath = NormalizeDirectoryWatchPath(watchedDirectoryPath);
        string normalizedCurrentPath = NormalizeDirectoryWatchPath(_navigationService.CurrentPath);
        string normalizedWatcherPath = NormalizeDirectoryWatchPath(_currentDirectoryWatcherPath);
        _navigationRefreshCoordinator.QueueRefresh(
            watchedDirectoryPath,
            reason,
            normalizedWatchedPath,
            normalizedCurrentPath,
            normalizedWatcherPath,
            exception,
            _directoryRefreshDebounceTimer);
    }
    private void TryProcessPendingCurrentDirectoryRefresh(string source)
    {
        string currentPath = _navigationService.CurrentPath;
        if (!_navigationRefreshCoordinator.CanProcessRefresh(NormalizeDirectoryWatchPath(currentPath)))
        {
            if (_navigationRefreshCoordinator.State.IsPending && !_navigationRefreshCoordinator.State.IsApplying)
            {
                ClearPendingCurrentDirectoryRefresh();
            }
            return;
        }
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy())
        {
            return;
        }
        int externalDelayMs = _previewDiagnosticDelayService.ExternalReloadDelayMs;
        var state = _navigationRefreshCoordinator.State;
        if (!state.DelayScheduled
            && !state.DelayCompleted
            && _previewDiagnosticDelayService.ShouldDelay(currentPath, externalDelayMs))
        {
            state.DelayScheduled = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource();
                    await _previewDiagnosticDelayService
                        .DelayAsync("ExternalChangeReload", currentPath, externalDelayMs, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    if (IsDisposed || !IsHandleCreated)
                    {
                        state.DelayScheduled = false;
                    }
                    else
                    {
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                state.DelayScheduled = false;
                                state.DelayCompleted = true;
                                TryProcessPendingCurrentDirectoryRefresh($"{source}:Delayed");
                            }));
                        }
                        catch (ObjectDisposedException)
                        {
                            state.DelayScheduled = false;
                        }
                    }
                }
            });
            return;
        }
        state.IsApplying = true;
        var sw = Stopwatch.StartNew();
        string statusBefore = statusLabel?.Text ?? "<null>";
        string reason = _navigationRefreshCoordinator.BuildExternalDirectoryRefreshReason(ExternalDirectoryRefreshBulkThreshold);
        string statusMessage = $"外部変更を反映しました: {reason}";
        string result = "Skipped";
        string exceptionType = state.ExceptionType ?? "-";
        string exceptionMessage = state.ExceptionMessage ?? "-";
        try
        {
            ClearPendingCurrentDirectoryRefresh();
            bool loaded = ReloadCurrentDirectory(statusMessage, force: true);
            result = loaded ? "Success" : "Error";
        }
        catch (Exception ex)
        {
            result = "Error";
            exceptionType = ex.GetType().Name;
            exceptionMessage = ex.Message;
            LogService.Warn(
                $"[ExternalChangeReload] source={source} path='{currentPath}' reason='{reason}' result=Error " +
                $"exceptionType='{exceptionType}' message='{exceptionMessage}'");
            throw;
        }
        finally
        {
            string statusAfter = statusLabel?.Text ?? "<null>";
            LogService.Info(
                $"[StatusUpdate] source='ExternalChangeReload' before='{statusBefore}' after='{statusAfter}'");
            LogService.Info(
                $"[ExternalChangeReload] source={source} path='{currentPath}' reason='{reason}' result={result} " +
                $"exceptionType='{exceptionType}' message='{exceptionMessage}' elapsedMs={sw.ElapsedMilliseconds}");
            state.IsApplying = false;
        }
    }
    private void ClearPendingCurrentDirectoryRefresh()
    {
        _navigationRefreshCoordinator.ClearPendingRefresh();
        _directoryRefreshDebounceTimer.Stop();
    }
    private void UpdateCurrentDirectoryWatcher(string? currentPath, string reason)
    {
        if (!_featureGate.IsEnabled(FeatureId.FileSystemWatcherAutoRefresh))
        {
            DisposeCurrentDirectoryWatcher();
            ClearPendingCurrentDirectoryRefresh();
            return;
        }
        string normalizedCurrentPath = NormalizeDirectoryWatchPath(currentPath);
        string normalizedWatcherPath = NormalizeDirectoryWatchPath(_currentDirectoryWatcherPath);
        if (!string.IsNullOrWhiteSpace(normalizedCurrentPath) &&
            string.Equals(normalizedCurrentPath, normalizedWatcherPath, StringComparison.OrdinalIgnoreCase) &&
            _currentDirectoryWatcher != null)
        {
            return;
        }
        DisposeCurrentDirectoryWatcher();
        _currentDirectoryWatcherPath = null;
        if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
        {
            return;
        }
        try
        {
            var watcher = new FileSystemWatcher(currentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime |
                               NotifyFilters.LastAccess |
                               NotifyFilters.Attributes,
                EnableRaisingEvents = false
            };
            watcher.Changed += (_, _) => QueueCurrentDirectoryRefresh(currentPath, "Changed");
            watcher.Created += (_, _) => QueueCurrentDirectoryRefresh(currentPath, "Created");
            watcher.Deleted += (_, _) => QueueCurrentDirectoryRefresh(currentPath, "Deleted");
            watcher.Renamed += (_, _) => QueueCurrentDirectoryRefresh(currentPath, "Renamed");
            watcher.Error += (_, e) => QueueCurrentDirectoryRefresh(currentPath, "Error", e.GetException());
            watcher.EnableRaisingEvents = true;
            _currentDirectoryWatcher = watcher;
            _currentDirectoryWatcherPath = currentPath;
        }
        catch (Exception ex)
        {
            LogService.Warn($"[DirectoryRefreshWatcher] Watcher init failed. reason={reason}, path={currentPath}, message={ex.Message}");
            ShowStatusMessage("現在ディレクトリ監視を開始できませんでした。Ctrl+R で再読込してください。");
        }
    }
    private void DisposeCurrentDirectoryWatcher()
    {
        if (_currentDirectoryWatcher == null)
        {
            return;
        }
        try
        {
            _currentDirectoryWatcher.EnableRaisingEvents = false;
            _currentDirectoryWatcher.Dispose();
        }
        catch (Exception ex)
        {
            LogService.Warn($"[DirectoryRefreshWatcher] Dispose failed. message={ex.Message}");
        }
        finally
        {
            _currentDirectoryWatcher = null;
            _currentDirectoryWatcherPath = null;
        }
    }
}
