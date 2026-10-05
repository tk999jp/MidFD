using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;
using MidFD.Runtime;

namespace MidFD;

public partial class MainForm
{
    private sealed record BrowserAsyncDropPayload(
        string[] Files,
        string[] OutlookAttachmentNames,
        int KeyState,
        Point DropPoint,
        bool InternalMarkerPresent,
        bool FileDropPresent,
        bool HasImageData,
        bool HasPotentialUrlData,
        bool IsOutlookAttachmentDrop);

    internal static string FormatBrowserDropResult(
        string operationLabel,
        BrowserDropCounters counters)
    {
        string result = $"ドロップ{operationLabel}: {counters.SuccessCount} 件成功、{counters.SkipCount} 件スキップ、{counters.FailCount} 件失敗、{counters.CancelCount} 件キャンセル、{counters.NoOpCount} 件不要";
        if (counters.PartialSkipCount > 0 || counters.PartialCancelCount > 0 || counters.PartialFailCount > 0)
        {
            result += $"（partial: skip {counters.PartialSkipCount}、cancel {counters.PartialCancelCount}、fail {counters.PartialFailCount}）";
        }
        if (counters.NestedSkipCount > 0 || counters.NestedFailCount > 0)
        {
            result += $"（merge内訳: skip {counters.NestedSkipCount}、fail {counters.NestedFailCount}）";
        }
        return result;
    }

    internal static StatusKind ResolveBrowserDropStatusKind(BrowserDropCounters counters)
    {
        if (counters.FailCount > 0 || counters.CancelCount > 0)
        {
            return StatusKind.Error;
        }

        return counters.SuccessCount > 0 && counters.SkipCount == 0 && counters.NoOpCount == 0
            ? StatusKind.Result
            : StatusKind.Normal;
    }

    private void HandleBrowserPanelDragEnterOrOver(DragEventArgs e, string operationName)
    {
        BrowserIncomingDragDecision decision = ResolveIncomingBrowserDragDecision(e);
        e.Effect = ToWinFormsDragDropEffects(decision.Effect);
        if (decision.Intent != BrowserDragDropIntent.None)
        {
            _currentIncomingDragDecision = decision;
        }
        RefreshBrowserStatusSummary(decision.StatusText);
        LogService.Info(DragDropDataObjectDiagnosticHelper.GetDiagnosticLog(
            operationName,
            _viewerApplicationCoordinator.Mode.ToString(),
            IsActiveBrowserTabReadOnly(),
            _fileOperationApplicationCoordinator.IsClipboardBusy,
            HasInternalDragArchiveMarker(e.Data),
            e.Data,
            e.Effect,
            decision.Reason));
    }

    private static DragDropEffects ToWinFormsDragDropEffects(BrowserDragDropEffect effect) =>
        (DragDropEffects)(int)effect;

    private BrowserIncomingDragDecision ResolveIncomingBrowserDragDecision(DragEventArgs e)
    {
        IDataObject? data = e.Data;
        return ResolveIncomingBrowserDragDecision(
            HasInternalDragArchiveMarker(data),
            data != null && data.GetDataPresent(DataFormats.FileDrop),
            BrowserImageDropService.HasImageData(data),
            BrowserDropUrlResolverService.HasPotentialUrlData(data),
            OutlookAttachmentDropService.IsOutlookAttachmentDrop(data),
            e.KeyState);
    }

    private BrowserIncomingDragDecision ResolveIncomingBrowserDragDecision(
        bool internalMarkerPresent,
        bool fileDropPresent,
        bool hasImageData,
        bool hasPotentialUrlData,
        bool isOutlookAttachmentDrop,
        int keyState)
    {
        return BrowserIncomingDragResolver.Resolve(
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            IsActiveBrowserTabReadOnly(),
            _fileOperationApplicationCoordinator.IsClipboardBusy,
            internalMarkerPresent,
            fileDropPresent,
            hasImageData,
            hasPotentialUrlData,
            isOutlookAttachmentDrop,
            keyState);
    }

    private BrowserIncomingDragDecision ResolveIncomingDropDecision(DragEventArgs e)
    {
        IDataObject? data = e.Data;
        return ResolveIncomingDropDecision(
            HasInternalDragArchiveMarker(data),
            data != null && data.GetDataPresent(DataFormats.FileDrop),
            BrowserImageDropService.HasImageData(data),
            BrowserDropUrlResolverService.HasPotentialUrlData(data),
            OutlookAttachmentDropService.IsOutlookAttachmentDrop(data),
            e.KeyState);
    }

    private BrowserIncomingDragDecision ResolveIncomingDropDecision(
        bool internalMarkerPresent,
        bool fileDropPresent,
        bool hasImageData,
        bool hasPotentialUrlData,
        bool isOutlookAttachmentDrop,
        int keyState)
    {
        // Drop event keyState might lose right mouse button flag (2) or modifier keys.
        // If DragOver remembered a Prompt or Move decision, prefer that.
        var eventDecision = ResolveIncomingBrowserDragDecision(
            internalMarkerPresent,
            fileDropPresent,
            hasImageData,
            hasPotentialUrlData,
            isOutlookAttachmentDrop,
            keyState);
        if (_currentIncomingDragDecision != null)
        {
            // Only override if the current event is less specific (e.g. falls back to default Copy)
            if (eventDecision.Intent == BrowserDragDropIntent.Copy &&
                (_currentIncomingDragDecision.Intent == BrowserDragDropIntent.Prompt ||
                 _currentIncomingDragDecision.Intent == BrowserDragDropIntent.Move))
            {
                return _currentIncomingDragDecision;
            }
        }
        return eventDecision;
    }

    private void HandleBrowserPanelDragLeave()
    {
        _currentIncomingDragDecision = null;
        RefreshBrowserStatusSummary();
    }

    private BrowserDropAction ResolveBrowserDropAction(DragEventArgs e, BrowserIncomingDragDecision decision)
        => ResolveBrowserDropAction(new Point(e.X, e.Y), decision);

    private BrowserDropAction ResolveBrowserDropAction(Point dropPoint, BrowserIncomingDragDecision decision)
    {
        if (decision.Intent != BrowserDragDropIntent.Prompt)
        {
            return decision.Intent switch
            {
                BrowserDragDropIntent.Move => BrowserDropAction.Move,
                BrowserDragDropIntent.Copy => BrowserDropAction.Copy,
                _ => BrowserDropAction.Cancel
            };
        }

        return BrowserDropActionMenuPresenter.Show(this, dropPoint);
    }

    private void BrowserPanel_AsyncDragDrop(object? sender, DragEventArgs e)
    {
        try
        {
            LogService.Info($"[ExternalDropAsync] callbackReceived=True, thread={Environment.CurrentManagedThreadId}");
            IDataObject? data = e.Data;
            if (data == null)
            {
                return;
            }

            bool fileDropPresent = data.GetDataPresent(DataFormats.FileDrop);
            bool outlookAttachmentDrop = OutlookAttachmentDropService.IsOutlookAttachmentDrop(data);
            if (!fileDropPresent && !outlookAttachmentDrop)
            {
                return;
            }

            string[] files = Array.Empty<string>();
            string[] outlookAttachmentNames = Array.Empty<string>();
            if (outlookAttachmentDrop)
            {
                // Virtual data is valid only during this callback. Snapshot descriptor names here;
                // FileContents is consumed later in the same callback by ProcessDrop.
                outlookAttachmentNames = OutlookAttachmentDropService.GetAttachmentNames(data).ToArray();
            }
            else if (fileDropPresent)
            {
                string[]? fileDropFiles = data.GetData(DataFormats.FileDrop) as string[];
                if (fileDropFiles == null || fileDropFiles.Length == 0)
                {
                    return;
                }

                files = fileDropFiles.ToArray();
            }

            var payload = new BrowserAsyncDropPayload(
                files,
                outlookAttachmentNames,
                e.KeyState,
                new Point(e.X, e.Y),
                HasInternalDragArchiveMarker(data),
                FileDropPresent: fileDropPresent,
                BrowserImageDropService.HasImageData(data),
                BrowserDropUrlResolverService.HasPotentialUrlData(data),
                IsOutlookAttachmentDrop: outlookAttachmentDrop);
            LogService.Info(
                $"[ExternalDropAsync] nativeDataObject=True, asyncCapability=True, asyncMode=True, " +
                $"getAsyncMode=WinForms, startOperation=WinForms, fileDropCount={payload.Files.Length}, " +
                $"outlookAttachmentDrop={payload.IsOutlookAttachmentDrop}, outlookAttachmentCount={payload.OutlookAttachmentNames.Length}, " +
                $"internalMarkerPresent={payload.InternalMarkerPresent}");

            if (payload.IsOutlookAttachmentDrop)
            {
                ProcessBrowserAsyncOutlookDrop(data, payload);
                LogService.Info("[ExternalDropAsync] outlookRoute=completed, endOperation=WinForms");
                return;
            }

            if (!browserPanel.InvokeRequired)
            {
                LogService.Info("[ExternalDropAsync] callbackOnUiThread=True, operationNotStarted=True");
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                browserPanel.BeginInvoke(new Action(() => HandleBrowserAsyncDragDropOnUi(payload, completion)));
            }
            catch (Exception ex)
            {
                LogService.Error("[ExternalDropAsync] UI dispatch failed", ex);
                return;
            }

            completion.Task.GetAwaiter().GetResult();
            LogService.Info("[ExternalDropAsync] operationCompleted=True, endOperation=WinForms");
        }
        catch (Exception ex)
        {
            LogService.Error("[ExternalDropAsync] data extraction failed", ex);
        }
    }

    private sealed record BrowserAsyncOutlookPreparation(
        string TargetDirectory);

    private void ProcessBrowserAsyncOutlookDrop(
        IDataObject data,
        BrowserAsyncDropPayload payload)
    {
        BrowserAsyncOutlookPreparation? preparation = InvokeBrowserUi(
            () => ResolveBrowserAsyncOutlookDropPreparation(payload));
        if (preparation == null)
        {
            return;
        }

        OutlookAttachmentDropResult dropResult = OutlookAttachmentDropService.ProcessDrop(
            data,
            preparation.TargetDirectory,
            fileName => InvokeBrowserUi(() =>
            {
                string overwriteMessage = FileOperationPresentationHelper.GetOverwriteConfirmationMessage(fileName);
                DialogResult overwriteResult = MessageBox.Show(
                    overwriteMessage,
                    "確認",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);
                return overwriteResult == DialogResult.Yes
                    ? OverwriteConfirmResult.Yes
                    : overwriteResult == DialogResult.No
                        ? OverwriteConfirmResult.No
                        : OverwriteConfirmResult.Cancel;
            }),
            showTypeMismatch: destinationPath => InvokeBrowserUi(() =>
                MessageBox.Show(
                    $"型が異なるため上書きできません。\n宛先: {destinationPath}",
                    "上書きエラー",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error)),
            showCopyFailure: (fileName, exception) => InvokeBrowserUi(() =>
                MessageBox.Show(
                    $"コピー失敗: {fileName}\n{exception?.Message ?? "添付データを保存できませんでした。"}",
                    "エラー",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error)));

        LogService.Info(
            $"[ExternalDropAsync] outlookRoute=processed, success={dropResult.AllSucceeded}, " +
            $"attachmentCount={dropResult.AttachmentCount}, processedCount={dropResult.ProcessedCount}, " +
            $"successCount={dropResult.SuccessCount}, failureCount={dropResult.FailureCount}, " +
            $"canceled={dropResult.WasCanceled}, classification={dropResult.Classification}");
        if (!dropResult.AnySucceeded)
        {
            return;
        }

        BeginBrowserUi(() =>
        {
            if (dropResult.AllSucceeded)
            {
                ShowStatusMessage("仮想ファイルのコピーが完了しました。");
            }

            string? focusTarget = dropResult.SuccessfulFileNames.Count > 0
                ? dropResult.SuccessfulFileNames[0]
                : null;
            LoadDirectory(preparation.TargetDirectory, focusTarget);
        });
    }

    private BrowserAsyncOutlookPreparation? ResolveBrowserAsyncOutlookDropPreparation(
        BrowserAsyncDropPayload payload)
    {
        if (payload.InternalMarkerPresent)
        {
            return null;
        }

        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
        {
            return null;
        }

        if (GuardReadOnlyBrowserTab("ファイル取り込み"))
        {
            return null;
        }

        if (_fileOperationApplicationCoordinator.IsClipboardBusy
            || string.IsNullOrEmpty(_browserApplicationCoordinator.CurrentPath))
        {
            return null;
        }

        BrowserIncomingDragDecision decision = ResolveIncomingBrowserDragDecision(
            payload.InternalMarkerPresent,
            payload.FileDropPresent,
            payload.HasImageData,
            payload.HasPotentialUrlData,
            payload.IsOutlookAttachmentDrop,
            payload.KeyState);
        if (decision.Intent == BrowserDragDropIntent.None)
        {
            ShowStatusMessage("ドロップ不可な操作または状態です。");
            return null;
        }

        _integrationSeam?.Observer?.OnOutlookAttachmentDropRouted();
        if (payload.OutlookAttachmentNames.Length == 0)
        {
            LogService.Warn("[OutlookDrop] No attachment names resolved.");
            return null;
        }

        string targetDirectory = _browserApplicationCoordinator.CurrentPath!;
        return new BrowserAsyncOutlookPreparation(targetDirectory);
    }

    private T InvokeBrowserUi<T>(Func<T> action)
    {
        if (!IsHandleCreated || IsDisposed || Disposing)
        {
            throw new InvalidOperationException("Browser UI is not available for async drop processing.");
        }

        return InvokeRequired ? (T)Invoke(action)! : action();
    }

    private void InvokeBrowserUi(Action action)
    {
        if (!IsHandleCreated || IsDisposed || Disposing)
        {
            throw new InvalidOperationException("Browser UI is not available for async drop processing.");
        }

        if (InvokeRequired)
        {
            Invoke(action);
            return;
        }

        action();
    }

    private void BeginBrowserUi(Action action)
    {
        if (!IsHandleCreated || IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException ex)
        {
            LogService.Error("[ExternalDropAsync] UI dispatch after Outlook drop failed", ex);
        }
        catch (InvalidOperationException ex)
        {
            LogService.Error("[ExternalDropAsync] UI dispatch after Outlook drop failed", ex);
        }
    }

    internal void DispatchExternalFileDrop(
        IDataObject data,
        int keyState = 0,
        int x = 0,
        int y = 0,
        DragDropEffects allowedEffect = DragDropEffects.Copy,
        DragDropEffects effect = DragDropEffects.Copy)
    {
        if (InvokeRequired)
        {
            throw new InvalidOperationException("External Drop must be dispatched on the MainForm UI thread.");
        }

        BrowserPanel_DragDrop(
            browserPanel,
            new DragEventArgs(data, keyState, x, y, allowedEffect, effect));
    }

    internal void DispatchExternalAsyncDrop(
        IDataObject data,
        int keyState = 0,
        int x = 0,
        int y = 0,
        DragDropEffects allowedEffect = DragDropEffects.Copy,
        DragDropEffects effect = DragDropEffects.Copy)
    {
        if (!InvokeRequired)
        {
            throw new InvalidOperationException("External async Drop must be dispatched off the MainForm UI thread.");
        }

        browserPanel.OnAsyncDragDrop(
            new DragEventArgs(data, keyState, x, y, allowedEffect, effect));
    }

    private void HandleBrowserAsyncDragDropOnUi(
        BrowserAsyncDropPayload payload,
        TaskCompletionSource<bool> completion)
    {
        bool operationCompletionPending = false;
        try
        {
            if (payload.InternalMarkerPresent || !payload.FileDropPresent)
            {
                return;
            }

            if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
            {
                return;
            }

            if (GuardReadOnlyBrowserTab("ファイル取り込み"))
            {
                return;
            }

            if (_fileOperationApplicationCoordinator.IsClipboardBusy
                || string.IsNullOrEmpty(_browserApplicationCoordinator.CurrentPath))
            {
                return;
            }

            BrowserIncomingDragDecision decision = ResolveIncomingDropDecision(
                payload.InternalMarkerPresent,
                payload.FileDropPresent,
                payload.HasImageData,
                payload.HasPotentialUrlData,
                payload.IsOutlookAttachmentDrop,
                payload.KeyState);
            if (decision.Intent == BrowserDragDropIntent.None)
            {
                ShowStatusMessage("ドロップ不可な操作または状態です。");
                return;
            }

            BrowserDropAction action = ResolveBrowserDropAction(payload.DropPoint, decision);
            TryHandleBrowserFileDrop(
                payload.Files,
                action,
                () => completion.TrySetResult(true),
                out operationCompletionPending);
        }
        catch (Exception ex)
        {
            LogService.Error("[ExternalDropAsync] UI operation setup failed", ex);
        }
        finally
        {
            if (!operationCompletionPending)
            {
                completion.TrySetResult(true);
            }
        }
    }

    private bool TryHandleBrowserFileDrop(DragEventArgs e, BrowserIncomingDragDecision decision)
    {
        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0)
        {
            return true;
        }

        BrowserDropAction action = ResolveBrowserDropAction(e, decision);
        if (action == BrowserDropAction.Cancel)
        {
            ShowStatusMessage("ドロップ操作はキャンセルされました。");
            return true;
        }

        return TryHandleBrowserFileDrop(
            files,
            action,
            completionCallback: null,
            out _,
            waitForCompletion: true);
    }

    private bool TryHandleBrowserFileDrop(
        string[] files,
        BrowserDropAction action,
        Action? completionCallback,
        out bool operationCompletionPending,
        bool waitForCompletion = false)
    {
        operationCompletionPending = false;
        if (action == BrowserDropAction.Cancel)
        {
            ShowStatusMessage("ドロップ操作はキャンセルされました。");
            completionCallback?.Invoke();
            return true;
        }

        var selection = new SelectionResult(files, hasMarkedSelection: files.Length > 1);
        SyncExternalDropWait? syncWait = waitForCompletion ? new SyncExternalDropWait() : null;
        Action? effectiveCompletionCallback = completionCallback;
        if (syncWait != null)
        {
            _syncExternalDropWaitActive = true;
            _syncExternalDropCloseRequested = false;
            _syncExternalDropCancelRequested = false;
            effectiveCompletionCallback = () =>
            {
                try
                {
                    _integrationSeam?.Observer?.OnExternalDropCompletion();
                }
                finally
                {
                    syncWait.SetSignal();
                }
            };
        }

        bool started = false;
        try
        {
            started = _fileOperationApplicationCoordinator.TryStart(
                action == BrowserDropAction.Move
                    ? FileOperationCommandKind.Move
                    : FileOperationCommandKind.Copy,
                this,
                selection,
                destinationDirectory: _browserApplicationCoordinator.CurrentPath,
                completionCallback: effectiveCompletionCallback,
                recordDragCopyUndo: action == BrowserDropAction.Copy);
            operationCompletionPending = started;
            if (!started)
            {
                effectiveCompletionCallback?.Invoke();
            }
            else if (syncWait != null)
            {
                DispatchAwareWaitResult waitResult = DispatchAwareWaitService.WaitForSignal(
                    syncWait.WaitHandle,
                    _integrationSeam?.SyncDropWaitTimeoutMilliseconds ?? uint.MaxValue);
                if (waitResult != DispatchAwareWaitResult.Signaled)
                {
                    LogService.Error($"[ExternalDropSync] wait terminated without operation completion: {waitResult}");
                }
            }
        }
        finally
        {
            if (syncWait != null)
            {
                bool closeRequested = _syncExternalDropCloseRequested;
                _syncExternalDropWaitActive = false;
                _syncExternalDropCloseRequested = false;
                _syncExternalDropCancelRequested = false;
                syncWait.Dispose();
                if (closeRequested && !IsDisposed && !Disposing && IsHandleCreated)
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (!IsDisposed && !Disposing && IsHandleCreated)
                        {
                            Close();
                        }
                    }));
                }
            }
        }

        if (!started)
        {
            ShowStatusMessage("現在、別のファイル操作を実行中です。");
        }

        return true;
    }

    private bool TryHandleSyncExternalDropCloseRequest(string source)
    {
        if (!_syncExternalDropWaitActive)
        {
            return false;
        }

        _syncExternalDropCloseRequested = true;
        if (_syncExternalDropCancelRequested)
        {
            return true;
        }

        _syncExternalDropCancelRequested = true;
        try
        {
            _integrationSeam?.Observer?.OnCloseCancellationRequested();
            if (!TryRouteActiveFileOperationCancel(source))
            {
                LogService.Warn($"[ExternalDropSync] close request had no active cancellation route. source={source}");
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"[ExternalDropSync] close cancellation request failed. source={source}", ex);
        }

        return true;
    }

    private sealed class SyncExternalDropWait : IDisposable
    {
        private readonly object _sync = new();
        private readonly ManualResetEvent _signal = new(false);
        private bool _signaled;
        private bool _disposeRequested;
        private bool _disposed;

        public WaitHandle WaitHandle => _signal;

        public void SetSignal()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_signaled)
                {
                    _signaled = true;
                    _signal.Set();
                }

                DisposeIfRequested();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposeRequested = true;
                DisposeIfRequested();
            }
        }

        private void DisposeIfRequested()
        {
            if (!_disposeRequested || !_signaled || _disposed)
            {
                return;
            }

            _disposed = true;
            _signal.Dispose();
        }
    }

}
