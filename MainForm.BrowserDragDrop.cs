using MidFD.Helpers;
using MidFD.Commands;
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
        bool isOutlookAttachmentDrop = OutlookAttachmentDropService.IsOutlookAttachmentDrop(data);
        return ResolveIncomingBrowserDragDecision(
            HasInternalDragArchiveMarker(data),
            data != null && data.GetDataPresent(DataFormats.FileDrop),
            !isOutlookAttachmentDrop && BrowserImageDropService.HasImageData(data),
            BrowserDropUrlResolverService.HasPotentialUrlData(data),
            isOutlookAttachmentDrop,
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
        bool isOutlookAttachmentDrop = OutlookAttachmentDropService.IsOutlookAttachmentDrop(data);
        return ResolveIncomingDropDecision(
            HasInternalDragArchiveMarker(data),
            data != null && data.GetDataPresent(DataFormats.FileDrop),
            !isOutlookAttachmentDrop && BrowserImageDropService.HasImageData(data),
            BrowserDropUrlResolverService.HasPotentialUrlData(data),
            isOutlookAttachmentDrop,
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

            int keyState = e.KeyState;
            var dropPoint = new Point(e.X, e.Y);
            BrowserAsyncDropPayload? payload = InvokeBrowserUi(() =>
            {
                // WinForms IDataObject may wrap an STA-owned OLE IDataObject. Read every
                // format on the MainForm STA before the async route starts worker work.
                bool fileDropPresent = data.GetDataPresent(DataFormats.FileDrop);
                bool outlookAttachmentDrop = OutlookAttachmentDropService.IsOutlookAttachmentDrop(data);
                bool hasImageData = !outlookAttachmentDrop && BrowserImageDropService.HasImageData(data);
                bool hasPotentialUrlData = BrowserDropUrlResolverService.HasPotentialUrlData(data);
                if (!fileDropPresent && !outlookAttachmentDrop && !hasImageData && !hasPotentialUrlData)
                {
                    return null;
                }

                string[] files = Array.Empty<string>();
                string[] outlookAttachmentNames = Array.Empty<string>();
                if (outlookAttachmentDrop)
                {
                    // FileContents stays valid until this async drop callback completes.
                    outlookAttachmentNames = OutlookAttachmentDropService.GetAttachmentNames(data).ToArray();
                }
                else if (fileDropPresent)
                {
                    string[]? fileDropFiles = data.GetData(DataFormats.FileDrop) as string[];
                    if (fileDropFiles == null || fileDropFiles.Length == 0)
                    {
                        return null;
                    }

                    files = fileDropFiles.ToArray();
                }

                return new BrowserAsyncDropPayload(
                    files,
                    outlookAttachmentNames,
                    keyState,
                    dropPoint,
                    HasInternalDragArchiveMarker(data),
                    FileDropPresent: fileDropPresent,
                    hasImageData,
                    hasPotentialUrlData,
                    IsOutlookAttachmentDrop: outlookAttachmentDrop);
            });
            if (payload == null)
            {
                return;
            }
            LogService.Info(
                $"[ExternalDropAsync] nativeDataObject=True, asyncCapability=True, asyncMode=True, " +
                $"getAsyncMode=WinForms, startOperation=WinForms, fileDropCount={payload.Files.Length}, " +
                $"outlookAttachmentDrop={payload.IsOutlookAttachmentDrop}, outlookAttachmentCount={payload.OutlookAttachmentNames.Length}, " +
                $"internalMarkerPresent={payload.InternalMarkerPresent}");

            if (payload.IsOutlookAttachmentDrop)
            {
                RunBrowserAsyncOutlookDrop(data, payload);
                LogService.Info("[ExternalDropAsync] outlookRoute=completed, endOperation=WinForms");
                return;
            }

            if (!payload.FileDropPresent && (payload.HasImageData || payload.HasPotentialUrlData))
            {
                RunBrowserAsyncImageDrop(data, payload);
                LogService.Info("[ExternalDropAsync] imageRoute=completed, endOperation=WinForms");
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

    private sealed record BrowserAsyncExternalDropPreparation(
        BrowserExternalDropImportKind Kind,
        Uri? ImageUri = null,
        string? SuggestedFileName = null);

    private sealed record BrowserAsyncOutlookPreparation();

    private void RunBrowserAsyncOutlookDrop(
        IDataObject data,
        BrowserAsyncDropPayload payload)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = InvokeBrowserUi(() =>
        {
            if (ResolveBrowserAsyncOutlookDropPreparation(payload) == null)
            {
                return false;
            }

            return ExecuteBrowserExternalDropImport(
                BrowserExternalDropImportKind.OutlookAttachments,
                data,
                targetCount: payload.OutlookAttachmentNames.Length,
                outlookAttachmentNames: payload.OutlookAttachmentNames,
                confirmOverwrite: ConfirmOutlookDropOverwrite,
                completionCallback: () => completion.TrySetResult(true),
                processAsync: true);
        });
        if (!started)
        {
            completion.TrySetResult(true);
        }
        // The async drop callback retains the IDataObject until the worker has released
        // every FileContents IStream and STGMEDIUM.
        completion.Task.GetAwaiter().GetResult();
        LogService.Info("[ExternalDropAsync] outlookRoute=command-dispatch-completed, endOperation=WinForms");
    }

    private void RunBrowserAsyncImageDrop(
        IDataObject data,
        BrowserAsyncDropPayload payload)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = InvokeBrowserUi(() =>
        {
            BrowserAsyncExternalDropPreparation? preparation =
                ResolveBrowserAsyncImageDropPreparation(data, payload);
            if (preparation == null)
            {
                return false;
            }

            return ExecuteBrowserExternalDropImport(
                preparation.Kind,
                data,
                imageUri: preparation.ImageUri,
                suggestedFileName: preparation.SuggestedFileName,
                completionCallback: () => completion.TrySetResult(true),
                processAsync: true);
        });
        if (!started)
        {
            completion.TrySetResult(true);
        }
        completion.Task.GetAwaiter().GetResult();
    }

    private bool ExecuteBrowserExternalDropImport(
        BrowserExternalDropImportKind kind,
        IDataObject? data,
        Uri? imageUri = null,
        string? suggestedFileName = null,
        IReadOnlyList<string>? outlookAttachmentNames = null,
        Func<string, OverwriteConfirmResult>? confirmOverwrite = null,
        Action? completionCallback = null,
        bool processAsync = false,
        int targetCount = 1)
    {
        Func<string, OverwriteConfirmResult>? uiConfirmOverwrite = confirmOverwrite == null
            ? null
            : fileName => InvokeBrowserUi(() => confirmOverwrite(fileName));

        Action? observedCompletionCallback = completionCallback;
        if (observedCompletionCallback != null && _integrationSeam?.Observer is { } observer)
        {
            Action callback = observedCompletionCallback;
            observedCompletionCallback = () =>
            {
                try
                {
                    observer.OnExternalDropCompletion();
                }
                finally
                {
                    callback();
                }
            };
        }

        return ExecuteCommandFromUi(
            CommandIds.FileImportExternalDrop,
            new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "DragDrop",
                BrowserExternalDropImport = new BrowserExternalDropImportCommandContext(
                    kind,
                    data,
                    ImageUri: imageUri,
                    SuggestedFileName: suggestedFileName,
                    InvokeUi: InvokeBrowserUi,
                    ConfirmOverwrite: uiConfirmOverwrite,
                    ProcessAsync: processAsync,
                    TargetCount: targetCount,
                    OutlookAttachmentNames: outlookAttachmentNames),
                FileOperation = new FileOperationCommandContext(
                    CompletionCallback: observedCompletionCallback)
            });
    }

    private OverwriteConfirmResult ConfirmOutlookDropOverwrite(string fileName)
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
    }

    private BrowserAsyncOutlookPreparation? ResolveBrowserAsyncOutlookDropPreparation(
        BrowserAsyncDropPayload payload)
    {
        if (payload.InternalMarkerPresent || !CanPrepareBrowserAsyncExternalDrop(payload))
        {
            return null;
        }

        if (!payload.IsOutlookAttachmentDrop)
        {
            return null;
        }

        _integrationSeam?.Observer?.OnOutlookAttachmentDropRouted();
        if (payload.OutlookAttachmentNames.Length == 0)
        {
            LogService.Warn("[OutlookDrop] No attachment names resolved.");
            return null;
        }

        return new BrowserAsyncOutlookPreparation();
    }

    private BrowserAsyncExternalDropPreparation? ResolveBrowserAsyncImageDropPreparation(
        IDataObject data,
        BrowserAsyncDropPayload payload)
    {
        if (payload.InternalMarkerPresent || payload.FileDropPresent || payload.IsOutlookAttachmentDrop ||
            !CanPrepareBrowserAsyncExternalDrop(payload))
        {
            return null;
        }

        if (payload.HasImageData)
        {
            return new BrowserAsyncExternalDropPreparation(BrowserExternalDropImportKind.Image);
        }

        if (!payload.HasPotentialUrlData)
        {
            return null;
        }

        if (BrowserDropUrlResolverService.TryResolveImageUrl(
                data,
                out Uri? imageUri,
                out string? suggestedFileName) &&
            imageUri is Uri resolvedImageUri)
        {
            return new BrowserAsyncExternalDropPreparation(
                BrowserExternalDropImportKind.ImageUrl,
                resolvedImageUri,
                suggestedFileName);
        }

        LogBrowserImageImportWarn(
            $"Source=BrowserDropUrlUnresolved Data={BrowserImageDropService.DescribeDataObject(data)}");
        ShowStatusMessage("画像URLを特定できませんでした。");
        return null;
    }

    private bool CanPrepareBrowserAsyncExternalDrop(BrowserAsyncDropPayload payload)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
        {
            return false;
        }

        if (GuardReadOnlyBrowserTab("ファイル取り込み"))
        {
            return false;
        }

        if (_fileOperationApplicationCoordinator.IsClipboardBusy)
        {
            return false;
        }

        BrowserIncomingDragDecision decision = ResolveIncomingBrowserDragDecision(
            payload.InternalMarkerPresent,
            payload.FileDropPresent,
            payload.HasImageData,
            payload.HasPotentialUrlData,
            payload.IsOutlookAttachmentDrop,
            payload.KeyState);
        if (decision.Intent != BrowserDragDropIntent.None)
        {
            return true;
        }

        ShowStatusMessage("ドロップ不可な操作または状態です。");
        return false;
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

        // 外部FileDropの明示的なsource集合はBrowser内のmark選択ではない。
        var selection = new SelectionResult(files, hasMarkedSelection: false);
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
            string commandId = action == BrowserDropAction.Move
                ? CommandIds.FileMove
                : CommandIds.FileCopy;
            started = ExecuteCommandFromUi(
                commandId,
                new CommandExecutionContext
                {
                    Scope = CommandScope.Browser,
                    Source = "DragDrop",
                    SelectionSnapshot = selection,
                    FileOperation = new FileOperationCommandContext(
                        DestinationDirectory: _browserApplicationCoordinator.CurrentPath,
                        CompletionCallback: effectiveCompletionCallback,
                        RecordDragCopyUndo: action == BrowserDropAction.Copy)
                });
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
