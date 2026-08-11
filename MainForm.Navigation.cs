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
{
    private long _currentDirectoryWatcherGeneration;
    private long _lastExternalDirectoryReloadMilliseconds;

    private int GetCurrentDirectoryRefreshQuietWindowMilliseconds() => _lastExternalDirectoryReloadMilliseconds switch
    {
        >= 3000 => 3000,
        >= 1000 => 1500,
        _ => CurrentDirectoryRefreshDebounceMilliseconds
    };

    private bool LoadDirectory(string targetPath, string? focusTargetName = null, bool isHistoryNavigation = false, bool suppressRecent = false)
        => LoadDirectory(targetPath, focusTargetName, isHistoryNavigation, suppressRecent, BrowserLoadCoordinator.SnapshotPolicy.RebuildSnapshot);

    private bool LoadDirectory(
        string targetPath,
        string? focusTargetName,
        bool isHistoryNavigation,
        bool suppressRecent,
        BrowserLoadCoordinator.SnapshotPolicy snapshotPolicy)
    {
        HideBrowserFileNameToolTip();
        try
        {
            if (snapshotPolicy == BrowserLoadCoordinator.SnapshotPolicy.RebuildSnapshot)
            {
                _directoryContentGeneration++;
                StopDirectoryCountAudit(dispose: false);
            }
            var request = CreateDirectoryLoadRequest(targetPath, focusTargetName, isHistoryNavigation, suppressRecent, snapshotPolicy);
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
        bool suppressRecent,
        BrowserLoadCoordinator.SnapshotPolicy snapshotPolicy)
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
            _settings.Appearance?.ShowDirectoryMarker ?? true,
            GetBrowserItemsPerPage(),
            snapshotPolicy);
    }
    private void PopulateListView(IReadOnlyList<ListViewItem> items)
    {
        fileListView.Items.Clear();
        if (items.Count > 0)
        {
            fileListView.Items.AddRange(items.ToArray());
        }
    }
    private void ApplyDirectoryLoadUi(
        BrowserLoadCoordinator.DirectoryLoadResult result,
        Action? applyFinalPresentation = null)
    {
        fileListView.BeginUpdate();
        try
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
                ClearPendingCurrentDirectoryRefresh();
                _navigationRefreshCoordinator.State.IsPassiveRefresh = false;
            }
            // 1. 内部状態とパス表示の更新
            _navigationService.SetCurrentPath(result.NewPath, result.IsHistoryNavigation);
            SyncBreadcrumbPathPresentation();
            if (directoryChanged)
            {
                if (!TryCarryMarkSummaryAcrossDirectoryChange(result.PreviousPath))
                {
                    InvalidateMarkSummaryCache();
                }
                _directoryNavigationGeneration++;
                StopDirectoryCountAudit(dispose: false);
            }
            _browserPageStartIndex = result.PageStartIndex;
            _browserTotalItemCount = result.TotalItemCount;
            _browserCursorIndex = result.LastIndex;
            _browserItemsPerPage = GetBrowserItemsPerPage();
            var listApplyStopwatch = Stopwatch.StartNew();
            _isApplyingDirectoryList = true;
            try
            {
                PopulateListView(result.Items);
                listApplyStopwatch.Stop();
                var selectionRestoreStopwatch = Stopwatch.StartNew();
                int pageLocalIndex = result.LastIndex - result.PageStartIndex;
                RestoreSelectionState(result.FocusTargetName, pageLocalIndex, result.IsReload);
                selectionRestoreStopwatch.Stop();
                LogService.Info(
                    $"[DirectoryLoadTiming] path='{result.NewPath}' itemCount={result.Items.Count} " +
                    $"enumerationSortMs={result.EnumerationAndSortMilliseconds} itemBuildMs={result.ItemBuildMilliseconds} generatedUiItemCount={result.GeneratedUiItemCount} totalItemCount={result.TotalItemCount} pageStartIndex={result.PageStartIndex} reusedSnapshot={result.ReusedSnapshot} " +
                    $"listApplyMs={listApplyStopwatch.ElapsedMilliseconds} selectionRestoreMs={selectionRestoreStopwatch.ElapsedMilliseconds}");
            }
            finally
            {
                _isApplyingDirectoryList = false;
            }
            if (fileListView.SelectedIndices.Count > 0)
            {
                ApplyBrowserSelectionChanged(scheduleInfoUpdate: false);
            }
            if (!result.ReusedSnapshot)
            {
                _navigationRefreshCoordinator.ConfigureDirectoryCost(
                    result.RawDirectoryEntryCount,
                    result.TotalItemCount,
                    result.ItemBuildMilliseconds);
            }
            if (!result.SuppressRecent)
            {
                RecordQuickAccessRecent(result.PreviousPath, result.NewPath, result.IsReload);
            }
            CommitActiveBrowserTabFromDirectoryLoad(result);
            if (!_isSwitchingBrowserTab)
            {
                ApplyActiveBrowserTabPresentation(synchronizeSelection: false);
            }
            applyFinalPresentation?.Invoke();
            UpdateCurrentDirectoryWatcher(result.NewPath, "ApplyDirectoryLoadUi");
            UpdateDirectoryCountAuditLifecycle();
            TryProcessPendingCurrentDirectoryRefresh("ApplyDirectoryLoadUi");
            UpdateMenuStripState();
            UpdateInfoPanel();
            if (!_isSwitchingBrowserTab)
            {
                browserPanel.Invalidate();
            }
        }
        finally
        {
            fileListView.EndUpdate();
        }
    }

    private BrowserLoadCoordinator.DirectoryLoadResult? PrepareBrowserTabSwitchDirectoryLoad(
        BrowserTabState targetTab,
        string targetPath)
    {
        HideBrowserFileNameToolTip();
        try
        {
            var request = new BrowserLoadCoordinator.DirectoryLoadRequest(
                targetPath,
                targetTab.FocusTargetName,
                IsHistoryNavigation: true,
                SuppressRecent: true,
                CurrentPath: _navigationService.CurrentPath,
                LastIndex: targetTab.CursorIndex,
                CurrentItemFullName: null,
                FilterPattern: _filterPattern,
                FilterUseRegex: _filterUseRegex,
                ShowHiddenFiles: _settings.Appearance?.ShowHiddenFiles ?? false,
                SortKind: targetTab.SortKind,
                SortAscending: targetTab.SortAscending,
                FilterLock: targetTab.FilterLock,
                DateFormat: _settings.Appearance?.DateFormat,
                SizeFormat: _settings.Appearance?.SizeFormat,
                ShowDirectoryMarker: _settings.Appearance?.ShowDirectoryMarker ?? true,
                ItemsPerPage: GetBrowserItemsPerPageForColumnCount(targetTab.ColumnCount),
                SnapshotPolicy: BrowserLoadCoordinator.SnapshotPolicy.RebuildSnapshot);
            return _browserLoadCoordinator.Execute(
                request,
                new BrowserLoadCoordinator.ExecutionContext
                {
                    ShowStatusMessage = ShowStatusMessage,
                    DecoratePathItem = ApplyMarkColor
                });
        }
        catch (Exception ex)
        {
            NotifyDirectoryLoadFailure(ex);
            return null;
        }
    }

    private void CommitPreparedBrowserTabSwitchDirectoryLoad(
        BrowserLoadCoordinator.DirectoryLoadResult result,
        Action? applyFinalPresentation = null)
    {
        _directoryContentGeneration++;
        StopDirectoryCountAudit(dispose: false);
        ApplyDirectoryLoadUi(result, applyFinalPresentation);
    }

    private int GetBrowserItemsPerPageForColumnCount(int columnCount)
    {
        int itemHeight = HeaderLayoutHelper.GetMeasuredLineHeight(browserPanel.Font, 4);
        int rowsPerColumn = Math.Max(1, (browserPanel.Height - 10) / itemHeight);
        int minimumColumnWidth = GetMinimumBrowserColumnWidthForMode(GetBrowserFileDisplayMode());
        int maxColumnsByWidth = Math.Max(1, browserPanel.Width / Math.Max(1, minimumColumnWidth));
        int effectiveColumns = Math.Max(1, Math.Min(Math.Max(1, columnCount), maxColumnsByWidth));
        return effectiveColumns * rowsPerColumn;
    }

    private void CommitActiveBrowserTabFromDirectoryLoad(BrowserLoadCoordinator.DirectoryLoadResult result)
    {
        BrowserTabState? activeTab = _browserTabViewState.ActiveTab;
        if (activeTab == null)
        {
            return;
        }

        activeTab.Title = GetBrowserTabTitle(result.NewPath);
        activeTab.CurrentPath = result.NewPath;
        activeTab.Navigation = _navigationService.CaptureState();
        activeTab.FocusTargetName = result.FocusTargetName;
        activeTab.CursorIndex = result.LastIndex;
        activeTab.ColumnCount = Math.Clamp(_columnCount, 1, 9);
        activeTab.SortKind = _currentSort;
        activeTab.SortAscending = _sortAscending;
    }

    private int GetBrowserPageLocalCursorIndex()
    {
        if (fileListView.Items.Count == 0)
        {
            return -1;
        }
        return BrowserPageIndex.ToLocal(_browserCursorIndex, _browserPageStartIndex, fileListView.Items.Count);
    }

    private void RematerializeBrowserPageIfCapacityChanged()
    {
        if (_uiMode != UIMode.Browser || _isApplyingDirectoryList || IsCurrentDirectoryBusy())
        {
            return;
        }
        int itemsPerPage = GetBrowserItemsPerPage();
        if (itemsPerPage <= 0 || itemsPerPage == _browserItemsPerPage || string.IsNullOrWhiteSpace(_navigationService.CurrentPath))
        {
            return;
        }
        LoadDirectory(
            _navigationService.CurrentPath,
            focusTargetName: null,
            isHistoryNavigation: false,
            suppressRecent: false,
            snapshotPolicy: BrowserLoadCoordinator.SnapshotPolicy.ReuseSnapshot);
    }

    private void SetBrowserGlobalCursorIndex(int globalIndex)
    {
        if (_browserTotalItemCount <= 0)
        {
            return;
        }
        int clamped = Math.Clamp(globalIndex, 0, _browserTotalItemCount - 1);
        int itemsPerPage = GetBrowserItemsPerPage();
        int previousPage = itemsPerPage > 0 ? _browserCursorIndex / itemsPerPage : 0;
        int nextPage = itemsPerPage > 0 ? clamped / itemsPerPage : 0;
        _browserCursorIndex = clamped;
        if (previousPage != nextPage && !IsCurrentDirectoryBusy())
        {
            LoadDirectory(
                _navigationService.CurrentPath,
                focusTargetName: null,
                isHistoryNavigation: false,
                suppressRecent: false,
                snapshotPolicy: BrowserLoadCoordinator.SnapshotPolicy.ReuseSnapshot);
            return;
        }
        SyncBrowserSelection();
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
        bool loaded = LoadDirectory(
            currentPath,
            focusTargetName: null,
            isHistoryNavigation: false,
            suppressRecent: false,
            snapshotPolicy: BrowserLoadCoordinator.SnapshotPolicy.RebuildSnapshot);
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
        ResetDirectoryCountAuditBackoff();
        ReloadCurrentDirectory("現在ディレクトリを再読込しました。");
        _navigationRefreshCoordinator.ClearPendingRefresh();
        return true;
    }
    private void QueueCurrentDirectoryRefresh(string watchedDirectoryPath, long watcherGeneration, string reason, Exception? exception = null)
    {
        if (IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath)
        {
            return;
        }
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => QueueCurrentDirectoryRefresh(watchedDirectoryPath, watcherGeneration, reason, exception)));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }
        ResetDirectoryCountAuditBackoff();
        string normalizedWatchedPath = NormalizeDirectoryWatchPath(watchedDirectoryPath);
        string normalizedCurrentPath = NormalizeDirectoryWatchPath(_navigationService.CurrentPath);
        string normalizedWatcherPath = NormalizeDirectoryWatchPath(_currentDirectoryWatcherPath);
        _directoryRefreshDebounceTimer.Interval = GetCurrentDirectoryRefreshQuietWindowMilliseconds();
        _navigationRefreshCoordinator.QueueRefresh(
            normalizedWatchedPath,
            reason,
            normalizedWatchedPath,
            normalizedCurrentPath,
            normalizedWatcherPath,
            watcherGeneration,
            _currentDirectoryWatcherGeneration,
            exception,
            _directoryRefreshDebounceTimer);
        if (!_navigationRefreshCoordinator.State.IsPassiveRefresh &&
            _navigationRefreshCoordinator.State.DelayCompleted)
        {
            TryProcessPendingCurrentDirectoryRefresh("BulkThreshold");
        }
        if (_navigationRefreshCoordinator.State.IsPassiveRefresh && _navigationRefreshCoordinator.State.EventCount == 1)
        {
            ShowStatusMessage("外部変更あり［高頻度フォルダ］ Ctrl+Rで更新できます。");
        }
    }
    private void TryProcessPendingCurrentDirectoryRefresh(string source)
    {
        string currentPath = _navigationService.CurrentPath;
        string normalizedCurrentPath = NormalizeDirectoryWatchPath(currentPath);
        if (_navigationRefreshCoordinator.State.IsPassiveRefresh || _isExitConfirmationPending || IsDisposed || Disposing || _isClosingFromEscExitPath)
        {
            return;
        }
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy())
        {
            return;
        }
        if (!_navigationRefreshCoordinator.TryBeginRefresh(normalizedCurrentPath, _currentDirectoryWatcherGeneration, out NavigationRefreshBatch? batch))
        {
            if (_navigationRefreshCoordinator.ShouldDiscardPending(normalizedCurrentPath, _currentDirectoryWatcherGeneration))
            {
                ClearPendingCurrentDirectoryRefresh();
            }
            return;
        }
        var sw = Stopwatch.StartNew();
        string statusBefore = statusLabel?.Text ?? "<null>";
        string reason = batch!.EventCount > ExternalDirectoryRefreshBulkThreshold
            ? $"Bulk({batch.EventCount})"
            : string.Join("+", batch.Reasons.OrderBy(static value => value));
        string statusMessage = $"外部変更を反映しました: {reason}";
        string result = "Skipped";
        string exceptionType = batch.ExceptionType ?? "-";
        string exceptionMessage = batch.ExceptionMessage ?? "-";
        try
        {
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
                $"exceptionType='{exceptionType}' message='{exceptionMessage}' elapsedMs={sw.ElapsedMilliseconds} " +
                $"itemEvents={batch.EventCount} watcherGeneration={batch.WatcherGeneration} followUpPending={_navigationRefreshCoordinator.State.IsPending}");
            _lastExternalDirectoryReloadMilliseconds = sw.ElapsedMilliseconds;
            _navigationRefreshCoordinator.CompleteRefresh();
            if (_navigationRefreshCoordinator.State.IsPending)
            {
                _directoryRefreshDebounceTimer.Interval = GetCurrentDirectoryRefreshQuietWindowMilliseconds();
                _navigationRefreshCoordinator.State.ScheduleRefreshDelay();
                _directoryRefreshDebounceTimer.Start();
            }
        }
    }
    private void ClearPendingCurrentDirectoryRefresh()
    {
        _navigationRefreshCoordinator.ClearPendingRefresh();
        _directoryRefreshDebounceTimer.Stop();
    }

    private void RearmCurrentDirectoryWatcherAfterInternalMutation(string currentPath)
    {
        DisposeCurrentDirectoryWatcher();
        ClearPendingCurrentDirectoryRefresh();
        UpdateCurrentDirectoryWatcher(currentPath, "InternalMutation");
    }

    private void StopDirectoryCountAudit(bool dispose)
    {
        _directoryCountAuditTimer.Stop();
        _directoryCountAuditCts?.Cancel();
        _directoryCountAuditCts?.Dispose();
        _directoryCountAuditCts = null;
        if (dispose)
        {
            _directoryCountAuditTimer.Dispose();
        }
    }

    private void UpdateDirectoryCountAuditLifecycle()
    {
        if (IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath ||
            !_featureGate.IsEnabled(FeatureId.FileSystemWatcherAutoRefresh) ||
            !_navigationRefreshCoordinator.State.IsPassiveRefresh ||
            string.IsNullOrWhiteSpace(_navigationService.CurrentPath))
        {
            StopDirectoryCountAudit(dispose: false);
            return;
        }
        if (!_directoryCountAuditTimer.Enabled)
        {
            ResetDirectoryCountAuditBackoff();
            _directoryCountAuditTimer.Start();
        }
    }

    private void ResetDirectoryCountAuditBackoff()
    {
        _directoryCountAuditSchedule.ResetForActivity();
        string currentPath = _navigationService.CurrentPath;
        _directoryCountAuditTimer.Interval = _directoryCountAuditSchedule.GetIntervalMilliseconds(
            !string.IsNullOrWhiteSpace(currentPath) && DirectoryCountAuditService.IsNetworkPath(currentPath));
    }

    private void RunCurrentDirectoryCountAudit()
    {
        if (!_navigationRefreshCoordinator.State.IsPassiveRefresh ||
            _isExitConfirmationPending || IsDisposed || Disposing ||
            _isClosingFromEscExitPath || _navigationRefreshCoordinator.State.IsApplying)
        {
            return;
        }

        string currentPath = _navigationService.CurrentPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return;
        }
        if (!_directoryCountAuditGate.TryEnter())
        {
            return;
        }

        _directoryCountAuditCts?.Cancel();
        _directoryCountAuditCts?.Dispose();
        var cts = new CancellationTokenSource();
        _directoryCountAuditCts = cts;
        long watcherGeneration = _currentDirectoryWatcherGeneration;
        long navigationGeneration = _directoryNavigationGeneration;
        long contentGeneration = _directoryContentGeneration;
        bool showHiddenFiles = _settings.Appearance?.ShowHiddenFiles ?? false;
        _ = Task.Run(() => DirectoryCountAuditService.CountVisibleEntriesDetailed(currentPath, showHiddenFiles, cts.Token), cts.Token)
            .ContinueWith(task =>
            {
                _directoryCountAuditGate.Exit();
                if (task.IsCanceled || task.IsFaulted || IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath)
                {
                    return;
                }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || Disposing ||
                            navigationGeneration != _directoryNavigationGeneration ||
                            contentGeneration != _directoryContentGeneration ||
                            watcherGeneration != _currentDirectoryWatcherGeneration ||
                            !string.Equals(
                                NormalizeDirectoryWatchPath(currentPath),
                                NormalizeDirectoryWatchPath(_navigationService.CurrentPath),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                        bool changed = _navigationRefreshCoordinator.ApplyCountAudit(
                            currentPath,
                            watcherGeneration,
                            task.Result.VisibleEntryCount);
                        _directoryCountAuditSchedule.RecordResult(changed);
                        _directoryCountAuditTimer.Interval = _directoryCountAuditSchedule.GetIntervalMilliseconds(
                            DirectoryCountAuditService.IsNetworkPath(currentPath));
                        if (changed)
                        {
                            ShowStatusMessage("外部変更あり［高頻度フォルダ］ Ctrl+Rで更新できます。");
                            LogService.Info($"[DirectoryCountAudit] path='{currentPath}' rawCount={task.Result.VisibleEntryCount} " +
                                $"enumerated={task.Result.EnumeratedEntryCount} attributeReads={task.Result.AttributeReadCount} " +
                                $"dirty=true nextIntervalMs={_directoryCountAuditTimer.Interval} " +
                                $"filteredTotalItemCount={_navigationRefreshCoordinator.State.FilteredTotalItemCount} generatedUiItemCount=0 listApply=false");
                        }
                    }));
                }
                catch (InvalidOperationException)
                {
                }
            }, TaskScheduler.Default);
    }
    private void UpdateCurrentDirectoryWatcher(string? currentPath, string reason)
    {
        if (!_featureGate.IsEnabled(FeatureId.FileSystemWatcherAutoRefresh))
        {
            StopDirectoryCountAudit(dispose: false);
            DisposeCurrentDirectoryWatcher();
            _navigationRefreshCoordinator.State.ResetDirectoryBaseline();
            ClearPendingCurrentDirectoryRefresh();
            return;
        }
        string normalizedCurrentPath = NormalizeDirectoryWatchPath(currentPath);
        string normalizedWatcherPath = NormalizeDirectoryWatchPath(_currentDirectoryWatcherPath);
        if (!string.IsNullOrWhiteSpace(normalizedCurrentPath) &&
            string.Equals(normalizedCurrentPath, normalizedWatcherPath, StringComparison.OrdinalIgnoreCase) &&
            _currentDirectoryWatcher != null &&
            _currentDirectoryWatcher.NotifyFilter == DirectoryWatcherNotifyFilterPolicy.ForSort(_currentSort))
        {
            return;
        }
        DisposeCurrentDirectoryWatcher();
        StopDirectoryCountAudit(dispose: false);
        _directoryCountAuditSchedule.ResetForActivity();
        _currentDirectoryWatcherPath = null;
        if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
        {
            _navigationRefreshCoordinator.State.ResetDirectoryBaseline();
            return;
        }
        try
        {
            var watcher = new FileSystemWatcher(currentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = DirectoryWatcherNotifyFilterPolicy.ForSort(_currentSort),
                EnableRaisingEvents = false
            };
            long generation = ++_currentDirectoryWatcherGeneration;
            watcher.Changed += (_, _) => QueueCurrentDirectoryRefresh(currentPath, generation, "Changed");
            watcher.Created += (_, _) => QueueCurrentDirectoryRefresh(currentPath, generation, "Created");
            watcher.Deleted += (_, _) => QueueCurrentDirectoryRefresh(currentPath, generation, "Deleted");
            watcher.Renamed += (_, _) => QueueCurrentDirectoryRefresh(currentPath, generation, "Renamed");
            watcher.Error += (_, e) => QueueCurrentDirectoryRefresh(currentPath, generation, "Error", e.GetException());
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
            _currentDirectoryWatcherGeneration++;
            _currentDirectoryWatcher = null;
            _currentDirectoryWatcherPath = null;
        }
    }
}
