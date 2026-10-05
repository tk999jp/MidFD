using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;
using MidFD.Runtime;

namespace MidFD;

public partial class MainForm
{
    private bool LoadDirectory(string targetPath, string? focusTargetName = null, bool isHistoryNavigation = false, bool suppressRecent = false, bool recordDirectoryMoveHistory = false)
        => LoadDirectory(targetPath, focusTargetName, isHistoryNavigation, suppressRecent, BrowserLoadCoordinator.SnapshotPolicy.RebuildSnapshot, recordDirectoryMoveHistory);

    private bool LoadDirectory(
        string targetPath,
        string? focusTargetName,
        bool isHistoryNavigation,
        bool suppressRecent,
        BrowserLoadCoordinator.SnapshotPolicy snapshotPolicy,
        bool recordDirectoryMoveHistory = false)
    {
        HideBrowserFileNameToolTip();
        BrowserDirectoryNavigationExecution execution = _browserNavigationWorkflowApplicationCoordinator.ExecuteDirectoryNavigation(
            targetPath,
            focusTargetName,
            isHistoryNavigation,
            suppressRecent,
            _browserTabWorkflowApplicationCoordinator.MaxTabCount,
            BuildBrowserTabStateFromCurrentUi(),
            CreateDirectoryLoadOptions() with { SnapshotPolicy = snapshotPolicy },
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState(),
            recordDirectoryMoveHistory);
        if (!execution.Succeeded || execution.Load is not { Succeeded: true, Result: not null })
        {
            if (execution.Error != null)
            {
                NotifyDirectoryLoadFailure(execution.Error);
            }
            return false;
        }

        PrepareDerivedBrowserTabPresentation(execution.DerivedTabIndex);
        ApplyDirectoryLoadUi(
            execution.Load.Value,
            CreateDerivedBrowserTabSelectionCallback(execution.DerivedTabIndex));
        ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
        return true;
    }

    private bool ExecuteConfirmedUserDirectoryNavigation(
        string targetPath,
        string? focusTargetName = null,
        bool isHistoryNavigation = false,
        bool suppressRecent = false,
        bool recordDirectoryMoveHistory = false,
        bool clearPreview = false)
    {
        BrowserNavigationCoordinator.DirectoryNavigationRequest request =
            _browserApplicationCoordinator.CreateNavigationRequest(
                targetPath,
                focusTargetName,
                isHistoryNavigation,
                suppressRecent);
        BrowserNavigationPreparation preparation = _browserNavigationWorkflowApplicationCoordinator.PrepareDirectoryNavigation(
            request,
            _browserTabWorkflowApplicationCoordinator.MaxTabCount,
            BuildBrowserTabStateFromCurrentUi());
        if (preparation.Kind == BrowserNavigationWorkflowDecisionKind.NotRequested)
        {
            return false;
        }
        if (preparation.Kind == BrowserNavigationWorkflowDecisionKind.DirectoryMissing)
        {
            MessageBox.Show(
                $"指定されたパスが見つかりません: {targetPath}",
                "エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        if (preparation.Kind == BrowserNavigationWorkflowDecisionKind.Blocked)
        {
            return true;
        }
        if (preparation.RequiresDerivedTabConfirmation && !ShowBrowserDerivedTabNavigationConfirmation())
        {
            return true;
        }

        HideBrowserFileNameToolTip();
        if (clearPreview)
        {
            ClearPreview();
        }
        BrowserDirectoryNavigationExecution execution = _browserNavigationWorkflowApplicationCoordinator.ExecutePreparedDirectoryNavigation(
            preparation,
            _browserTabWorkflowApplicationCoordinator.MaxTabCount,
            BuildBrowserTabStateFromCurrentUi(),
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState(),
            confirmedDerivedTabCreation: true,
            recordDirectoryMoveHistory: recordDirectoryMoveHistory);
        if (execution.Kind is BrowserDirectoryNavigationExecutionKind.TabCreationUnavailable or
            BrowserDirectoryNavigationExecutionKind.ConfirmationRequired)
        {
            return true;
        }
        if (!execution.Succeeded || execution.Load is not { Succeeded: true, Result: not null } load)
        {
            if (execution.Error != null)
            {
                NotifyDirectoryLoadFailure(execution.Error);
            }
            return false;
        }

        PrepareDerivedBrowserTabPresentation(execution.DerivedTabIndex);
        ApplyDirectoryLoadUi(
            load,
            CreateDerivedBrowserTabSelectionCallback(execution.DerivedTabIndex));
        ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
        return true;
    }

    private bool ShowBrowserDerivedTabNavigationConfirmation()
    {
        using var dialog = new Form
        {
            Text = "固定タブ範囲外",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            ControlBox = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Padding.Empty,
            Font = SystemFonts.MessageBoxFont
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var icon = new PictureBox
        {
            Image = SystemIcons.Question.ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(0, 2, 12, 0)
        };
        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 2);

        var messageLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Text = "固定タブの範囲外です。対象フォルダを新しいタブで開きますか？",
            Margin = new Padding(0, 0, 0, 16)
        };
        layout.Controls.Add(messageLabel, 1, 0);

        var yesButton = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(86, 28),
            Text = "はい(&Y)",
            DialogResult = DialogResult.Yes
        };
        var noButton = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(86, 28),
            Text = "いいえ(&N)",
            DialogResult = DialogResult.No
        };

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        buttonPanel.Controls.Add(noButton);
        buttonPanel.Controls.Add(yesButton);
        layout.Controls.Add(buttonPanel, 1, 1);

        dialog.AcceptButton = yesButton;
        dialog.CancelButton = noButton;
        dialog.Controls.Add(layout);
        dialog.ActiveControl = yesButton;
        return dialog.ShowDialog(this) == DialogResult.Yes;
    }

    private void PrepareDerivedBrowserTabPresentation(int? derivedTabIndex)
    {
        if (derivedTabIndex.HasValue)
        {
            RefreshBrowserTabHeaders();
            ShowStatusMessage("固定タブから派生タブを作成しました。");
        }
    }

    private Action? CreateDerivedBrowserTabSelectionCallback(int? derivedTabIndex)
    {
        return derivedTabIndex is { } index
            ? () => ApplyVisibleBrowserTabSelection(index)
            : null;
    }

    private BrowserRefreshShellState CaptureBrowserRefreshShellState() =>
        new(
            _featureGate.IsEnabled(FeatureId.FileSystemWatcherAutoRefresh),
            _isExitConfirmationPending || _isClosingFromEscExitPath,
            IsDisposed || Disposing,
            _directoryCountAuditTimer.Enabled,
            _currentDirectoryWatcher?.NotifyFilter);

    private void ApplyDirectoryPostLoadEffects(BrowserDirectoryPostLoadEffects effects)
    {
        ApplyWatcherUpdate(effects.Watcher);
        ApplyDirectoryCountAuditLifecycle(effects.Audit);
    }

    private void ApplyDirectoryCountAuditLifecycle(BrowserCountAuditLifecyclePlan lifecycle)
    {
        if (lifecycle.StopTimer)
        {
            StopDirectoryCountAudit(dispose: false);
            return;
        }
        if (lifecycle.StartTimer)
        {
            _directoryCountAuditTimer.Interval = lifecycle.IntervalMilliseconds;
            _directoryCountAuditTimer.Start();
        }
    }

    private BrowserDirectoryLoadOptions CreateDirectoryLoadOptions(
        string? currentItemFullName = null,
        TabFilterLockState? filterLock = null,
        int? itemsPerPage = null,
        string? filterPattern = null,
        bool? filterUseRegex = null,
        SortKind? sortKind = null,
        bool? sortAscending = null)
    {
        string? currentFullNameValue = currentItemFullName;
        if (currentFullNameValue == null)
        {
            ListViewItem? currentItem = GetCurrentBrowserItem();
            currentFullNameValue = currentItem == null ? null : GetItemFullName(currentItem);
        }
        return new BrowserDirectoryLoadOptions(
            currentFullNameValue,
            _settingsCoordinator.Value.Appearance?.ShowHiddenFiles ?? false,
            filterLock ?? GetActiveTabFilterLock(),
            _settingsCoordinator.Value.Appearance?.DateFormat,
            _settingsCoordinator.Value.Appearance?.SizeFormat,
            _settingsCoordinator.Value.Appearance?.ShowDirectoryMarker ?? true,
            itemsPerPage ?? GetBrowserItemsPerPage(),
            BrowserLoadCoordinator.SnapshotPolicy.RebuildSnapshot,
            filterPattern,
            filterUseRegex,
            sortKind,
            sortAscending,
            CreateBrowserLayoutProjectionInput());
    }

    private BrowserLayoutProjectionInput CreateBrowserLayoutProjectionInput() =>
        new(
            browserPanel.Width,
            browserPanel.Height,
            HeaderLayoutHelper.GetMeasuredLineHeight(browserPanel.Font, 4),
            GetBrowserFileDisplayMode(),
            BrowserLayoutProjection.GetLeadingPresentationSlotCount(
                TabFilterLockService.IsActive(
                    _browserApplicationCoordinator.FilterPattern,
                    GetActiveTabFilterLock())));

    private void PopulateListView(IReadOnlyList<BrowserItemData> items)
    {
        fileListView.Items.Clear();
        if (items.Count > 0)
        {
            fileListView.Items.AddRange(items.Select(item =>
            {
                ListViewItem listViewItem = FileSystemItemFactory.CreateItem(item);
                if (!item.IsParent && item.FullPath != null)
                {
                    ApplyMarkColor(listViewItem, item.FullPath);
                }
                return listViewItem;
            }).ToArray());
        }
    }
    private void ApplyDirectoryLoadUi(
        BrowserLoadCoordinator.DirectoryLoadResult result,
        BrowserDirectoryApplicationTransition transition,
        Action? applyFinalPresentation = null)
    {
        if (transition.DirectoryChanged || !result.ReusedSnapshot)
        {
            ClearBrowserNamePrefixJump();
        }
        fileListView.BeginUpdate();
        try
        {
            DismissTransientContextMenus();
            foreach (string statusMessage in result.StatusMessages)
            {
                ShowStatusMessage(statusMessage);
            }
            _browserMarkInteractionController.ClearPendingPromotionCandidate();
            if (transition.DirectoryChanged)
            {
                InvalidateRecentMultiMarkIntent();
                if (!TryCarryMarkSummaryAcrossDirectoryChange(result.PreviousPath))
                {
                    InvalidateMarkSummaryCache();
                }
            }
            SyncBreadcrumbPathPresentation();
            var listApplyStopwatch = Stopwatch.StartNew();
            _browserRefreshWorkflowApplicationCoordinator.SetApplyingDirectoryList(true);
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
                    $"enumerationSortMs={result.EnumerationAndSortMilliseconds} itemBuildMs={result.ItemBuildMilliseconds} generatedItemCount={result.GeneratedItemCount} totalItemCount={result.TotalItemCount} pageStartIndex={result.PageStartIndex} reusedSnapshot={result.ReusedSnapshot} " +
                    $"listApplyMs={listApplyStopwatch.ElapsedMilliseconds} selectionRestoreMs={selectionRestoreStopwatch.ElapsedMilliseconds}");
            }
            finally
            {
                _browserRefreshWorkflowApplicationCoordinator.SetApplyingDirectoryList(false);
            }
            if (transition.ShouldApplySelection && fileListView.SelectedIndices.Count > 0)
            {
                ApplyBrowserSelectionChanged(scheduleInfoUpdate: false);
            }
            if (transition.ShouldApplyActiveTabPresentation)
            {
                ApplyActiveBrowserTabPresentation(synchronizeSelection: false);
            }
            applyFinalPresentation?.Invoke();
            UpdateMenuStripState();
            UpdateInfoPanel();
            if (transition.ShouldInvalidateBrowserPanel)
            {
                browserPanel.Invalidate();
            }
        }
        finally
        {
            fileListView.EndUpdate();
        }
    }

    private void ApplyDirectoryLoadUi(
        BrowserDirectoryLoadApplicationResult load,
        Action? applyFinalPresentation = null)
    {
        if (load.Result is not { } result)
        {
            return;
        }

        ApplyDirectoryLoadUi(result, load.Transition, applyFinalPresentation);
    }

    private void ApplyDirectoryLoadUiForCommandResult(
        BrowserDirectoryLoadApplicationResult load,
        Action? applyFinalPresentation = null,
        Action<bool>? applySelectionChanged = null)
    {
        if (load.Result is not { } result)
        {
            return;
        }

        BrowserDirectoryApplicationTransition transition = load.Transition;
        if (transition.DirectoryChanged || !result.ReusedSnapshot)
        {
            ClearBrowserNamePrefixJump();
        }
        fileListView.BeginUpdate();
        try
        {
            DismissTransientContextMenus();
            foreach (string statusMessage in result.StatusMessages)
            {
                ShowStatusMessage(statusMessage);
            }
            _browserMarkInteractionController.ClearPendingPromotionCandidate();
            if (transition.DirectoryChanged)
            {
                InvalidateRecentMultiMarkIntent();
                if (!TryCarryMarkSummaryAcrossDirectoryChange(result.PreviousPath))
                {
                    InvalidateMarkSummaryCache();
                }
            }
            SyncBreadcrumbPathPresentation();
            var listApplyStopwatch = Stopwatch.StartNew();
            _browserRefreshWorkflowApplicationCoordinator.SetApplyingDirectoryList(true);
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
                    $"enumerationSortMs={result.EnumerationAndSortMilliseconds} itemBuildMs={result.ItemBuildMilliseconds} generatedItemCount={result.GeneratedItemCount} totalItemCount={result.TotalItemCount} pageStartIndex={result.PageStartIndex} reusedSnapshot={result.ReusedSnapshot} " +
                    $"listApplyMs={listApplyStopwatch.ElapsedMilliseconds} selectionRestoreMs={selectionRestoreStopwatch.ElapsedMilliseconds}");
            }
            finally
            {
                _browserRefreshWorkflowApplicationCoordinator.SetApplyingDirectoryList(false);
            }
            if (transition.ShouldApplySelection && fileListView.SelectedIndices.Count > 0)
            {
                (applySelectionChanged ?? ApplyBrowserSelectionChangedForCommandResult)(false);
            }
            if (transition.ShouldApplyActiveTabPresentation)
            {
                ApplyActiveBrowserTabPresentation(synchronizeSelection: false);
            }
            applyFinalPresentation?.Invoke();
            UpdateMenuStripState();
            UpdateInfoPanel();
            if (transition.ShouldInvalidateBrowserPanel)
            {
                browserPanel.Invalidate();
            }
        }
        finally
        {
            fileListView.EndUpdate();
        }
    }

    private void ApplyPreparedBrowserTabSwitchDirectoryLoad(
        BrowserDirectoryLoadApplicationResult load,
        Action? applyFinalPresentation = null)
    {
        ApplyDirectoryLoadUi(load, applyFinalPresentation);
    }

    private void ApplyPreparedBrowserTabSwitchDirectoryLoadForCommandResult(
        BrowserDirectoryLoadApplicationResult load,
        Action? applyFinalPresentation = null)
    {
        ApplyDirectoryLoadUiForCommandResult(load, applyFinalPresentation);
    }

    private int GetBrowserPageLocalCursorIndex()
    {
        if (fileListView.Items.Count == 0)
        {
            return -1;
        }
        return BrowserPageIndex.ToLocal(_browserApplicationCoordinator.CursorIndex, _browserApplicationCoordinator.PageStartIndex, fileListView.Items.Count);
    }

    private void RematerializeBrowserPageIfCapacityChanged()
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || _browserRefreshWorkflowApplicationCoordinator.IsApplyingDirectoryList || IsCurrentDirectoryBusy())
        {
            return;
        }
        int itemsPerPage = GetBrowserItemsPerPage();
        if (itemsPerPage <= 0 || itemsPerPage == _browserApplicationCoordinator.ItemsPerPage || string.IsNullOrWhiteSpace(_browserApplicationCoordinator.CurrentPath))
        {
            return;
        }
        LoadDirectory(
            _browserApplicationCoordinator.CurrentPath,
            focusTargetName: null,
            isHistoryNavigation: false,
            suppressRecent: false,
            snapshotPolicy: BrowserLoadCoordinator.SnapshotPolicy.ReuseSnapshot);
    }

    private void SetBrowserGlobalCursorIndex(int globalIndex)
    {
        BrowserCursorNavigationExecution execution = _browserNavigationWorkflowApplicationCoordinator.ExecuteCursorNavigation(
            globalIndex,
            GetBrowserItemsPerPage(),
            IsCurrentDirectoryBusy(),
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());
        if (!execution.Cursor.Applied)
        {
            return;
        }
        if (execution.Load is { Succeeded: true } load)
        {
            ApplyDirectoryLoadUi(load);
            ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
            return;
        }
        SyncBrowserSelection();
    }

    private void UpdateBrowserNamePrefixJumpCharacter(char value)
    {
        if (!_browserNamePrefixJumpSession.Append(value))
        {
            return;
        }
        string character = _browserNamePrefixJumpSession.Prefix;
        bool matched = _browserApplicationCoordinator.Directory.TryFindCurrentSnapshotPrefixIndex(
            _browserApplicationCoordinator.CurrentPath,
            character,
            out int globalIndex);
        if (matched)
        {
            SetBrowserGlobalCursorIndex(globalIndex);
        }
        ClearBrowserNamePrefixJump();
        ShowStatusMessage(matched
            ? $"頭文字ジャンプ: {character}"
            : $"頭文字ジャンプ: {character}（該当なし）");
    }

    private void UpdateBrowserNamePrefixJumpCursor()
    {
        string prefix = _browserNamePrefixJumpSession.Prefix;
        if (prefix.Length == 0)
        {
            ShowBrowserNamePrefixJumpStatus();
            return;
        }

        if (!_browserApplicationCoordinator.Directory.TryFindCurrentSnapshotPrefixIndex(
                _browserApplicationCoordinator.CurrentPath,
                prefix,
                out int globalIndex))
        {
            ShowStatusMessage($"頭文字ジャンプ: {prefix}（該当なし）");
            return;
        }

        SetBrowserGlobalCursorIndex(globalIndex);
        ShowBrowserNamePrefixJumpStatus();
    }

    private void ShowBrowserNamePrefixJumpStatus() =>
        ShowStatusMessage($"頭文字ジャンプ: {_browserNamePrefixJumpSession.Prefix}");

    private void ClearBrowserNamePrefixJump() => _browserNamePrefixJumpSession.End();

    private bool NotifyDirectoryLoadFailure(Exception ex)
    {
        ShowStatusMessage($"読み込み失敗: {ex.Message}");
        return false;
    }
    private bool ApplyReloadExecution(BrowserReloadExecution execution, string reason)
    {
        switch (execution.Kind)
        {
            case BrowserReloadExecutionKind.Loaded when execution.DirectoryLoad is { Succeeded: true } load:
                if (!string.IsNullOrWhiteSpace(execution.FallbackReason))
                {
                    LogService.Info($"[DirectoryRefresh] Fallback applied. path={execution.Path}, reason={execution.FallbackReason}");
                    ShowStatusMessage($"現在のフォルダが見つからないため、{execution.FallbackReason}フォルダへ移動しました。");
                }
                ApplyDirectoryLoadUi(load);
                ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
                if (string.IsNullOrWhiteSpace(execution.FallbackReason))
                {
                    ShowStatusMessage(reason);
                }
                return true;
            case BrowserReloadExecutionKind.RetryScheduled:
                ScheduleReloadRetry(execution.Path, execution.DelayMilliseconds, reason);
                return false;
            case BrowserReloadExecutionKind.NoCurrentPath:
                ShowStatusMessage("現在ディレクトリが未確定のため再読込できません。");
                return false;
            case BrowserReloadExecutionKind.Blocked:
                return false;
            case BrowserReloadExecutionKind.Missing:
                ApplyWatcherUpdate(execution.Watcher);
                ShowStatusMessage("現在ディレクトリが見つかりません。");
                return false;
            case BrowserReloadExecutionKind.Stale:
                return false;
            default:
                NotifyDirectoryLoadFailure(
                    execution.Error ?? new IOException("Directory reload failed."));
                return false;
        }
    }

    private void ScheduleReloadRetry(string expectedPath, int delayMilliseconds, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
                if (IsDisposed || !IsHandleCreated)
                {
                    _browserRefreshWorkflowApplicationCoordinator.CancelReloadRetry();
                    return;
                }

                BeginInvoke(new Action(() =>
                {
                    BrowserReloadExecution execution = _browserRefreshWorkflowApplicationCoordinator.ExecuteReloadRetry(
                        expectedPath,
                        CreateDirectoryLoadOptions(),
                        _browserApplicationCoordinator.ColumnCount,
                        CaptureBrowserRefreshShellState());
                    ApplyReloadExecution(execution, reason);
                }));
            }
            catch (ObjectDisposedException)
            {
                _browserRefreshWorkflowApplicationCoordinator.CancelReloadRetry();
            }
        });
    }
    private bool ExecuteCurrentDirectoryReloadCommand()
    {
        BrowserManualRefreshExecution execution = _browserRefreshWorkflowApplicationCoordinator.ExecuteManualRefresh(
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            IsCurrentDirectoryBusy(),
            "現在ディレクトリを再読込しました。",
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());
        return ApplyManualRefreshExecution(execution, "現在ディレクトリを再読込しました。");
    }

    private bool ApplyManualRefreshExecution(BrowserManualRefreshExecution execution, string statusMessage)
    {
        if (!execution.Decision.ShouldRun)
        {
            if (execution.Decision.Kind == BrowserManualRefreshDecisionKind.Busy)
            {
                ShowStatusMessage("処理中のため再読込できません。");
                return true;
            }
            return false;
        }

        if (execution.Reload.DirectoryLoad is { Succeeded: true } load)
        {
            ApplyDirectoryLoadUi(load);
            ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
            ShowStatusMessage(statusMessage);
            return true;
        }

        return ApplyReloadExecution(execution.Reload, statusMessage);
    }

    private bool ApplyManualRefreshExecutionForCommandResult(
        BrowserManualRefreshExecution execution,
        string statusMessage)
    {
        if (!execution.Decision.ShouldRun)
        {
            if (execution.Decision.Kind == BrowserManualRefreshDecisionKind.Busy)
            {
                ShowStatusMessage("処理中のため再読込できません。");
                return true;
            }
            return false;
        }

        if (execution.Reload.DirectoryLoad is { Succeeded: true } load)
        {
            ApplyDirectoryLoadUiForCommandResult(load);
            ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
            ShowStatusMessage(statusMessage);
            return true;
        }

        return ApplyReloadExecutionForCommandResult(execution.Reload, statusMessage);
    }

    private bool ApplyReloadExecutionForCommandResult(
        BrowserReloadExecution execution,
        string reason)
    {
        switch (execution.Kind)
        {
            case BrowserReloadExecutionKind.Loaded when execution.DirectoryLoad is { Succeeded: true } load:
                if (!string.IsNullOrWhiteSpace(execution.FallbackReason))
                {
                    LogService.Info($"[DirectoryRefresh] Fallback applied. path={execution.Path}, reason={execution.FallbackReason}");
                    ShowStatusMessage($"現在のフォルダが見つからないため、{execution.FallbackReason}フォルダへ移動しました。");
                }
                ApplyDirectoryLoadUiForCommandResult(load);
                ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
                if (string.IsNullOrWhiteSpace(execution.FallbackReason))
                {
                    ShowStatusMessage(reason);
                }
                return true;
            case BrowserReloadExecutionKind.RetryScheduled:
                ScheduleReloadRetry(execution.Path, execution.DelayMilliseconds, reason);
                return false;
            case BrowserReloadExecutionKind.NoCurrentPath:
                ShowStatusMessage("現在ディレクトリが未確定のため再読込できません。");
                return false;
            case BrowserReloadExecutionKind.Blocked:
                return false;
            case BrowserReloadExecutionKind.Missing:
                ApplyWatcherUpdate(execution.Watcher);
                ShowStatusMessage("現在ディレクトリが見つかりません。");
                return false;
            case BrowserReloadExecutionKind.Stale:
                return false;
            default:
                NotifyDirectoryLoadFailure(
                    execution.Error ?? new IOException("Directory reload failed."));
                return false;
        }
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
        BrowserRefreshQueueExecution execution = _browserRefreshWorkflowApplicationCoordinator.QueueExternalChangeAndProcessIfNeeded(
            watchedDirectoryPath,
            watcherGeneration,
            reason,
            exception,
            BrowserRefreshConstants.CurrentDirectoryRefreshDebounceMilliseconds,
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            IsCurrentDirectoryBusy(),
            _isExitConfirmationPending || _isClosingFromEscExitPath,
            IsDisposed || Disposing,
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());
        BrowserRefreshQueueTransition queueTransition = execution.Queue;
        _directoryCountAuditTimer.Interval = queueTransition.AuditIntervalMilliseconds;
        if (queueTransition.RestartQuietTimer)
        {
            _directoryRefreshDebounceTimer.Interval = queueTransition.QuietWindowMilliseconds;
            _directoryRefreshDebounceTimer.Stop();
            _directoryRefreshDebounceTimer.Start();
        }
        if (execution.Processed is { } processed)
        {
            ApplyPendingRefreshExecution(processed, "BulkThreshold");
        }
        if (queueTransition.ShowPassiveRefreshHint)
        {
            ShowStatusMessage("外部変更あり［高頻度フォルダ］ Ctrl+Rで更新できます。");
        }
    }
    private void TryProcessPendingCurrentDirectoryRefresh(string source)
    {
        BrowserRefreshProcessExecution execution = _browserRefreshWorkflowApplicationCoordinator.ExecutePendingRefreshProcessing(
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            IsCurrentDirectoryBusy(),
            _isExitConfirmationPending || _isClosingFromEscExitPath,
            IsDisposed || Disposing,
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());
        ApplyPendingRefreshExecution(execution, source);
    }

    private void ApplyPendingRefreshExecution(BrowserRefreshProcessExecution execution, string source)
    {
        if (!execution.Started)
        {
            return;
        }
        string currentPath = _browserApplicationCoordinator.CurrentPath;
        if (!execution.Started)
        {
            return;
        }
        NavigationRefreshBatch batch = execution.Batch;
        string statusBefore = statusLabel?.Text ?? "<null>";
        string reason = execution.Reason;
        string statusMessage = $"外部変更を反映しました: {reason}";
        string result = execution.Completion.ReloadSucceeded ? "Success" : "Error";
        string exceptionType = batch.ExceptionType ?? "-";
        string exceptionMessage = batch.ExceptionMessage ?? "-";
        try
        {
            if (execution.Reload.DirectoryLoad is { Succeeded: true } load)
            {
                ApplyDirectoryLoadUi(load);
                ApplyDirectoryPostLoadEffects(execution.PostLoadEffects);
                ShowStatusMessage(statusMessage);
            }
            else if (execution.Reload.Error != null)
            {
                NotifyDirectoryLoadFailure(execution.Reload.Error);
            }
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
                $"exceptionType='{exceptionType}' message='{exceptionMessage}' elapsedMs={execution.ElapsedMilliseconds} " +
                $"itemEvents={batch.EventCount} watcherGeneration={batch.WatcherGeneration} followUpPending={_browserRefreshWorkflowApplicationCoordinator.IsPending}");
            if (execution.Completion.HasFollowUpPending)
            {
                _directoryRefreshDebounceTimer.Interval = execution.Completion.QuietWindowMilliseconds;
                _directoryRefreshDebounceTimer.Start();
            }
        }
    }
    private void ClearPendingCurrentDirectoryRefresh()
    {
        _browserRefreshWorkflowApplicationCoordinator.ClearPendingRefresh();
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
        _browserRefreshWorkflowApplicationCoordinator.CancelCountAudit();
        if (dispose)
        {
            _directoryCountAuditTimer.Dispose();
        }
    }

    private void ResetDirectoryCountAuditBackoff()
    {
        _directoryCountAuditTimer.Interval = _browserRefreshWorkflowApplicationCoordinator.ResetAuditBackoff();
    }

    private void RunCurrentDirectoryCountAudit()
    {
        if (_isExitConfirmationPending || IsDisposed || Disposing || _isClosingFromEscExitPath)
        {
            return;
        }

        _ = RunCurrentDirectoryCountAuditAsync();
    }

    private async Task RunCurrentDirectoryCountAuditAsync()
    {
        BrowserCountAuditExecution execution =
            await _browserRefreshWorkflowApplicationCoordinator.ExecuteCountAuditLifecycleAsync(
                _settingsCoordinator.Value.Appearance?.ShowHiddenFiles ?? false,
                _isExitConfirmationPending || _isClosingFromEscExitPath,
                IsDisposed || Disposing).ConfigureAwait(false);
        if (!execution.Completed || IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || Disposing || _isExitConfirmationPending || _isClosingFromEscExitPath || execution.Applied.IsStale)
                {
                    return;
                }

                _directoryCountAuditTimer.Interval = execution.Applied.NextIntervalMilliseconds;
                if (execution.Applied.Changed)
                {
                    DirectoryCountAuditResult result = execution.Result!;
                    ShowStatusMessage("外部変更あり［高頻度フォルダ］ Ctrl+Rで更新できます。");
                    LogService.Info($"[DirectoryCountAudit] path='{execution.Request.CurrentPath}' rawCount={result.VisibleEntryCount} " +
                        $"enumerated={result.EnumeratedEntryCount} attributeReads={result.AttributeReadCount} " +
                        $"dirty=true nextIntervalMs={_directoryCountAuditTimer.Interval} " +
                        $"filteredTotalItemCount={execution.Applied.FilteredTotalItemCount} generatedUiItemCount=0 listApply=false");
                }
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }
    private void UpdateCurrentDirectoryWatcher(string? currentPath, string reason)
    {
        BrowserWatcherUpdate watcherUpdate = _browserRefreshWorkflowApplicationCoordinator.PrepareWatcherUpdate(
            currentPath,
            _featureGate.IsEnabled(FeatureId.FileSystemWatcherAutoRefresh),
            _currentDirectoryWatcher?.NotifyFilter);
        ApplyWatcherUpdate(watcherUpdate, reason);
    }

    private void ApplyWatcherUpdate(BrowserWatcherUpdate watcherUpdate, string reason = "PostLoad")
    {
        if (watcherUpdate.Kind == BrowserWatcherPlanKind.Keep)
        {
            return;
        }
        DisposeCurrentDirectoryWatcher();
        StopDirectoryCountAudit(dispose: false);
        if (watcherUpdate.Kind == BrowserWatcherPlanKind.Disable)
        {
            return;
        }
        try
        {
            var watcher = new FileSystemWatcher(watcherUpdate.Path!)
            {
                IncludeSubdirectories = false,
                NotifyFilter = watcherUpdate.NotifyFilter,
                EnableRaisingEvents = false
            };
            long generation = watcherUpdate.Generation;
            watcher.Changed += (_, _) => QueueCurrentDirectoryRefresh(watcherUpdate.Path!, generation, "Changed");
            watcher.Created += (_, _) => QueueCurrentDirectoryRefresh(watcherUpdate.Path!, generation, "Created");
            watcher.Deleted += (_, _) => QueueCurrentDirectoryRefresh(watcherUpdate.Path!, generation, "Deleted");
            watcher.Renamed += (_, _) => QueueCurrentDirectoryRefresh(watcherUpdate.Path!, generation, "Renamed");
            watcher.Error += (_, e) => QueueCurrentDirectoryRefresh(watcherUpdate.Path!, generation, "Error", e.GetException());
            watcher.EnableRaisingEvents = true;
            _currentDirectoryWatcher = watcher;
            _browserRefreshWorkflowApplicationCoordinator.CompleteWatcherUpdate(watcherUpdate, materialized: true);
        }
        catch (Exception ex)
        {
            _browserRefreshWorkflowApplicationCoordinator.CompleteWatcherUpdate(watcherUpdate, materialized: false);
            LogService.Warn($"[DirectoryRefreshWatcher] Watcher init failed. reason={reason}, path={watcherUpdate.Path}, message={ex.Message}");
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
        }
    }
}
