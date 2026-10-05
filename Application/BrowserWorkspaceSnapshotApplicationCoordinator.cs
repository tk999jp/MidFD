using System;
using System.Collections.Generic;
using MidFD.Models;
using MidFD.Services.Workspace;

namespace MidFD.Runtime;

internal interface IBrowserWorkspaceSnapshotUiPort
{
    bool ConfirmRestore(WorkspaceSnapshotEntry entry, WorkspaceState state);
    BrowserTabState CaptureCurrentBrowserTabState();
    void ApplyBrowserTabRuntimeRestore(BrowserWorkspaceRuntimeRestoreExecution runtime);
}

/// <summary>
/// Workspace snapshot の保存先と入出力 workflow を所有する。
/// Snapshotの確認・ダイアログ・表示反映はShellへ返す。
/// </summary>
internal sealed class BrowserWorkspaceSnapshotApplicationCoordinator
{
    private readonly BrowserApplicationCoordinator _browser;
    private readonly SettingsApplicationCoordinator _settings;
    private readonly BrowserWorkspacePersistenceApplicationCoordinator _persistence;
    private readonly BrowserWorkspaceLifecycleApplicationCoordinator? _lifecycle;
    private readonly BrowserTabWorkflowApplicationCoordinator? _tabWorkflow;
    private readonly BrowserNavigationWorkflowApplicationCoordinator? _navigation;
    private BrowserWorkspaceSnapshotRestoreOperation? _pendingRestore;

    public BrowserWorkspaceSnapshotApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        SettingsApplicationCoordinator settings,
        BrowserWorkspacePersistenceApplicationCoordinator persistence,
        BrowserWorkspaceLifecycleApplicationCoordinator? lifecycle = null,
        BrowserTabWorkflowApplicationCoordinator? tabWorkflow = null,
        BrowserNavigationWorkflowApplicationCoordinator? navigation = null)
    {
        _browser = browser;
        _settings = settings;
        _persistence = persistence;
        _lifecycle = lifecycle;
        _tabWorkflow = tabWorkflow;
        _navigation = navigation;
    }

    public bool HasStorage => _browser.Workspace.HasSnapshotStorage;
    public int SnapshotCount => _browser.Workspace.SnapshotCount;

    public IReadOnlyList<WorkspaceSnapshotEntry> LoadEntries() =>
        _browser.Workspace.LoadSnapshotEntries();

    public bool ExistsByName(string name) =>
        _browser.Workspace.SnapshotExistsByName(name);

    public WorkspaceState CaptureCurrentState() =>
        _persistence.CaptureCurrentWorkspaceState();

    public bool Save(string name, WorkspaceState state, out string errorMessage) =>
        _browser.Workspace.TrySaveSnapshot(name, state, out errorMessage);

    public bool Load(string snapshotId, out WorkspaceState? state, out string errorMessage) =>
        _browser.Workspace.TryLoadSnapshotState(snapshotId, out state, out errorMessage);

    public BrowserWorkspaceSnapshotRestoreApplicationResult ExecuteRestore(
        WorkspaceSnapshotEntry entry,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState,
        IBrowserWorkspaceSnapshotUiPort ui)
    {
        if (!Load(entry.SnapshotId, out WorkspaceState? state, out string errorMessage) || state == null)
        {
            return BrowserWorkspaceSnapshotRestoreApplicationResult.Failed(errorMessage);
        }

        if (!ui.ConfirmRestore(entry, state))
        {
            return BrowserWorkspaceSnapshotRestoreApplicationResult.Canceled;
        }

        BrowserWorkspaceSnapshotRestoreExecution execution = ExecuteRestoreTransaction(
            state,
            currentState,
            options,
            columnCount,
            shellState);
        if (!execution.Ready &&
            (_pendingRestore == null || _pendingRestore.OperationId != execution.OperationId))
        {
            return BrowserWorkspaceSnapshotRestoreApplicationResult.Failed(
                execution.Error ?? new InvalidOperationException("Workspace snapshot target could not be prepared."));
        }

        BrowserWorkspaceSnapshotRestoreEffect effect;
        try
        {
            if (execution.Error != null) throw execution.Error;
            if (!execution.Runtime.Succeeded)
            {
                throw new InvalidOperationException("Workspace snapshot target runtime materialization failed.");
            }
            ui.ApplyBrowserTabRuntimeRestore(execution.Runtime);
            effect = CompleteRestoreMaterialization(
                execution.OperationId,
                runtimeMaterialized: true,
                materializationError: null,
                options,
                columnCount,
                shellState,
                ui.CaptureCurrentBrowserTabState());
        }
        catch (Exception ex)
        {
            effect = CompleteRestoreMaterialization(
                execution.OperationId,
                runtimeMaterialized: false,
                materializationError: ex.Message,
                options,
                columnCount,
                shellState);
        }

        if (effect.Kind == BrowserWorkspaceSnapshotRestoreEffectKind.RollbackRequired &&
            effect.RuntimeRestore is { } rollbackRuntime)
        {
            BrowserWorkspaceSnapshotRestoreEffect rollbackEffect;
            try
            {
                if (!rollbackRuntime.Succeeded)
                {
                    throw new InvalidOperationException("Workspace snapshot rollback runtime materialization failed.");
                }
                ui.ApplyBrowserTabRuntimeRestore(rollbackRuntime);
                rollbackEffect = CompleteRollback(
                    effect.OperationId,
                    runtimeMaterialized: true,
                    materializationError: null,
                    ui.CaptureCurrentBrowserTabState());
            }
            catch (Exception ex)
            {
                rollbackEffect = CompleteRollback(
                    effect.OperationId,
                    runtimeMaterialized: false,
                    materializationError: ex.Message);
            }

            if (rollbackEffect.Kind != BrowserWorkspaceSnapshotRestoreEffectKind.Completed)
            {
                return BrowserWorkspaceSnapshotRestoreApplicationResult.Failed(
                    new IOException("Workspace snapshot restore and rollback failed.",
                        rollbackEffect.Error ?? effect.Error));
            }
        }

        return effect.Kind == BrowserWorkspaceSnapshotRestoreEffectKind.Completed
            ? BrowserWorkspaceSnapshotRestoreApplicationResult.Completed
            : BrowserWorkspaceSnapshotRestoreApplicationResult.Failed(
                effect.Error ?? new InvalidOperationException("Workspace snapshot restore failed."));
    }

    public bool Rename(string snapshotId, string name, out string errorMessage) =>
        _browser.Workspace.TryRenameSnapshot(snapshotId, name, out errorMessage);

    public bool Delete(string snapshotId) =>
        _browser.Workspace.DeleteSnapshot(snapshotId);

    public bool Export(
        string snapshotId,
        WorkspaceSnapshotMetadata metadata,
        string destinationPath,
        out string errorMessage) =>
        _browser.Workspace.TryExportSnapshot(snapshotId, metadata, destinationPath, out errorMessage);

    public bool Import(
        string sourcePath,
        string fallbackName,
        out string importedName,
        out string errorMessage) =>
        _browser.Workspace.TryImportSnapshot(sourcePath, fallbackName, out importedName, out errorMessage);

    public bool ExportAll(string destinationPath, out int exportedCount, out string errorMessage) =>
        _browser.Workspace.TryExportAllSnapshots(destinationPath, out exportedCount, out errorMessage);

    public bool ImportAll(string sourcePath, out int importedCount, out string errorMessage) =>
        _browser.Workspace.TryImportAllSnapshots(sourcePath, out importedCount, out errorMessage);

    public BrowserWorkspaceSnapshotRestoreStart BeginRestore(
        WorkspaceState state,
        BrowserTabState? currentState = null)
    {
        ApplyActiveState(currentState);
        BrowserWorkspaceSnapshotRestoreTransaction transaction = new(
            BrowserWorkspaceApplicationCoordinator.CreateRuntimeSnapshot(state),
            _persistence.CaptureRuntimeState());
        Guid operationId = Guid.NewGuid();
        try
        {
            _persistence.ApplyRuntimeState(transaction.TargetRuntimeState);
            _pendingRestore = new BrowserWorkspaceSnapshotRestoreOperation(operationId, transaction);
            return new BrowserWorkspaceSnapshotRestoreStart(
                operationId,
                transaction.TargetRuntimeState,
                null);
        }
        catch (Exception ex)
        {
            return new BrowserWorkspaceSnapshotRestoreStart(
                operationId,
                null,
                ex);
        }
    }

    public BrowserWorkspaceSnapshotRestoreExecution ExecuteRestoreTransaction(
        WorkspaceState state,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserWorkspaceSnapshotRestoreStart started = BeginRestore(state, currentState);
        if (!started.Ready || started.TargetRuntimeState == null)
        {
            return new BrowserWorkspaceSnapshotRestoreExecution(
                started.OperationId,
                BrowserWorkspaceRuntimeRestoreExecution.Unavailable,
                started.Error ?? new InvalidOperationException("Workspace snapshot target could not be prepared."));
        }

        try
        {
            BrowserWorkspaceRuntimeRestoreExecution runtime = ExecuteRuntimeRestore(
                started.TargetRuntimeState,
                stateAlreadyApplied: true,
                currentState: currentState,
                options: options,
                columnCount: columnCount,
                shellState: shellState);
            return new BrowserWorkspaceSnapshotRestoreExecution(started.OperationId, runtime, null);
        }
        catch (Exception ex)
        {
            return new BrowserWorkspaceSnapshotRestoreExecution(
                started.OperationId, BrowserWorkspaceRuntimeRestoreExecution.Unavailable, ex);
        }
    }

    public BrowserWorkspaceSnapshotRestoreEffect CompleteRestoreMaterialization(
        Guid operationId,
        bool runtimeMaterialized,
        string? materializationError,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default,
        BrowserTabState? currentState = null)
    {
        BrowserWorkspaceSnapshotRestoreEffect effect = CompleteRestore(
            operationId, runtimeMaterialized, materializationError, currentState);
        if (effect.Kind != BrowserWorkspaceSnapshotRestoreEffectKind.RollbackRequired ||
            effect.RuntimeState is not { } rollbackState)
        {
            return effect;
        }

        try
        {
            BrowserWorkspaceRuntimeRestoreExecution rollbackRuntime = ExecuteRuntimeRestore(
                rollbackState,
                stateAlreadyApplied: true,
                new BrowserTabState(),
                options,
                columnCount,
                shellState);
            // Keep the transaction pending until the UI has applied and captured rollback tabs.
            return effect with { RuntimeRestore = rollbackRuntime };
        }
        catch (Exception ex)
        {
            return CompleteRollback(operationId, runtimeMaterialized: false, ex.Message);
        }
    }

    public BrowserWorkspaceRuntimeRestoreExecution ExecuteRuntimeRestore(
        BrowserWorkspaceRuntimeStateSnapshot runtimeState,
        bool stateAlreadyApplied,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        if (_lifecycle == null || _tabWorkflow == null)
        {
            return BrowserWorkspaceRuntimeRestoreExecution.Unavailable;
        }

        BrowserWorkspaceRuntimeRestorePlan plan = _lifecycle.RestoreRuntimeState(
            runtimeState,
            stateAlreadyApplied);
        if (_browser.Workspace.TabCount == 0)
        {
            return new BrowserWorkspaceRuntimeRestoreExecution(plan, null);
        }

        BrowserTabSwitchWorkflowResult switchResult = _tabWorkflow.ExecuteTabSwitch(
            plan.TargetTabIndex,
            _browser.CurrentPath,
            _browser.Workspace.ActiveTabSnapshot ?? currentState,
            options,
            columnCount,
            shellState);
        return new BrowserWorkspaceRuntimeRestoreExecution(plan, switchResult);
    }

    public BrowserWorkspaceSettingsApplyExecution ExecuteSettingsAppliedRestoreAndReload(
        BrowserWorkspaceRuntimeStateSnapshot runtimeState,
        BrowserTabState currentState,
        BrowserDirectoryLoadOptions options,
        int columnCount,
        BrowserRefreshShellState shellState = default)
    {
        BrowserWorkspaceRuntimeRestoreExecution restore = ExecuteRuntimeRestore(
            runtimeState,
            stateAlreadyApplied: false,
            currentState,
            options,
            columnCount,
            shellState);
        _lifecycle?.ApplyStartupSessionState();
        if (_navigation == null)
        {
            return new BrowserWorkspaceSettingsApplyExecution(restore, null);
        }

        BrowserTabState targetState = _browser.Workspace.ActiveTabSnapshot ?? currentState;
        BrowserDirectoryNavigationExecution reload = _navigation.ExecuteTargetStateReload(
            targetState,
            options,
            shellState);
        return new BrowserWorkspaceSettingsApplyExecution(restore, reload);
    }

    public BrowserWorkspaceSnapshotRestoreEffect CompleteRestore(
        Guid operationId,
        bool runtimeMaterialized,
        string? materializationError,
        BrowserTabState? currentState = null)
    {
        if (_pendingRestore is not { } operation || operation.OperationId != operationId)
        {
            return BrowserWorkspaceSnapshotRestoreEffect.Stale;
        }

        if (!runtimeMaterialized)
        {
            return BeginRollback(operation, materializationError);
        }

        try
        {
            ApplyActiveState(currentState);
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
            if (!_persistence.SaveWorkspaceStateStore())
            {
                return BeginRollback(operation, "Workspace snapshot persistence failed.");
            }

            _pendingRestore = null;
            return BrowserWorkspaceSnapshotRestoreEffect.Completed;
        }
        catch (Exception ex)
        {
            return BeginRollback(operation, ex.Message);
        }
    }

    public BrowserWorkspaceSnapshotRestoreEffect CompleteRollback(
        Guid operationId,
        bool runtimeMaterialized,
        string? materializationError,
        BrowserTabState? currentState = null)
    {
        if (_pendingRestore is not { } operation || operation.OperationId != operationId)
        {
            return BrowserWorkspaceSnapshotRestoreEffect.Stale;
        }

        if (!runtimeMaterialized)
        {
            _pendingRestore = null;
            return new BrowserWorkspaceSnapshotRestoreEffect(
                BrowserWorkspaceSnapshotRestoreEffectKind.Failed,
                operationId,
                null,
                new IOException(materializationError ?? "Workspace snapshot rollback materialization failed."));
        }

        try
        {
            ApplyActiveState(currentState);
            _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
            bool persisted = _persistence.SaveWorkspaceStateStore();
            _pendingRestore = null;
            return persisted
                ? BrowserWorkspaceSnapshotRestoreEffect.Completed
                : new BrowserWorkspaceSnapshotRestoreEffect(
                    BrowserWorkspaceSnapshotRestoreEffectKind.Failed,
                    operationId,
                    null,
                    new IOException("Workspace snapshot rollback persistence failed."));
        }
        catch (Exception ex)
        {
            _pendingRestore = null;
            return new BrowserWorkspaceSnapshotRestoreEffect(
                BrowserWorkspaceSnapshotRestoreEffectKind.Failed,
                operationId,
                null,
                new IOException(materializationError ?? ex.Message, ex));
        }
    }

    private BrowserWorkspaceSnapshotRestoreEffect BeginRollback(
        BrowserWorkspaceSnapshotRestoreOperation operation,
        string? errorMessage)
    {
        try
        {
            _persistence.ApplyRuntimeState(operation.Transaction.RollbackRuntimeState);
            return new BrowserWorkspaceSnapshotRestoreEffect(
                BrowserWorkspaceSnapshotRestoreEffectKind.RollbackRequired,
                operation.OperationId,
                operation.Transaction.RollbackRuntimeState,
                new IOException(errorMessage ?? "Workspace snapshot restore failed."));
        }
        catch (Exception ex)
        {
            _pendingRestore = null;
            return new BrowserWorkspaceSnapshotRestoreEffect(
                BrowserWorkspaceSnapshotRestoreEffectKind.Failed,
                operation.OperationId,
                null,
                ex);
        }
    }

    public BrowserWorkspaceRuntimeStateSnapshot CaptureRuntimeStateAndPersist(BrowserTabState? activeState)
    {
        if (activeState != null)
        {
            int activeTabIndex = _browser.Workspace.ActiveTabIndex;
            if (activeTabIndex >= 0 && activeTabIndex < _browser.Workspace.TabCount)
            {
                _browser.Workspace.ApplyCapturedState(
                    activeTabIndex,
                    activeState,
                    captureMarks: true,
                    shouldValidateMarks: false,
                    markValidationSucceeded: false);
            }
        }

        _persistence.StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        return _persistence.CaptureRuntimeState();
    }

    private void ApplyActiveState(BrowserTabState? activeState)
    {
        if (activeState == null)
        {
            return;
        }

        int activeTabIndex = _browser.Workspace.ActiveTabIndex;
        if (activeTabIndex >= 0 && activeTabIndex < _browser.Workspace.TabCount)
        {
            _browser.Workspace.ApplyCapturedState(
                activeTabIndex,
                activeState,
                captureMarks: true,
                shouldValidateMarks: false,
                markValidationSucceeded: false);
        }
    }
}

internal readonly record struct BrowserWorkspaceSnapshotRestoreTransaction(
    BrowserWorkspaceRuntimeStateSnapshot TargetRuntimeState,
    BrowserWorkspaceRuntimeStateSnapshot RollbackRuntimeState);

internal sealed record BrowserWorkspaceSnapshotRestoreOperation(
    Guid OperationId,
    BrowserWorkspaceSnapshotRestoreTransaction Transaction);

internal readonly record struct BrowserWorkspaceSnapshotRestoreStart(
    Guid OperationId,
    BrowserWorkspaceRuntimeStateSnapshot? TargetRuntimeState,
    Exception? Error)
{
    public bool Ready => TargetRuntimeState != null && Error == null;
}

internal enum BrowserWorkspaceSnapshotRestoreEffectKind
{
    Completed,
    RollbackRequired,
    Failed,
    Stale
}

internal readonly record struct BrowserWorkspaceSnapshotRestoreEffect(
    BrowserWorkspaceSnapshotRestoreEffectKind Kind,
    Guid OperationId,
    BrowserWorkspaceRuntimeStateSnapshot? RuntimeState,
    Exception? Error)
{
    public BrowserWorkspaceRuntimeRestoreExecution? RuntimeRestore { get; init; }

    public static BrowserWorkspaceSnapshotRestoreEffect Completed =>
        new(BrowserWorkspaceSnapshotRestoreEffectKind.Completed, Guid.Empty, null, null);

    public static BrowserWorkspaceSnapshotRestoreEffect Stale =>
        new(BrowserWorkspaceSnapshotRestoreEffectKind.Stale, Guid.Empty, null, null);
}

internal readonly record struct BrowserWorkspaceRuntimeRestoreExecution(
    BrowserWorkspaceRuntimeRestorePlan Plan,
    BrowserTabSwitchWorkflowResult? Switch)
{
    public static BrowserWorkspaceRuntimeRestoreExecution Unavailable =>
        new(default, null);

    public bool Succeeded => Switch is { Committed: true };
}

internal readonly record struct BrowserWorkspaceSnapshotRestoreExecution(
    Guid OperationId,
    BrowserWorkspaceRuntimeRestoreExecution Runtime,
    Exception? Error)
{
    public bool Ready => OperationId != Guid.Empty && Error == null;
}

internal readonly record struct BrowserWorkspaceSnapshotRestoreApplicationResult(
    bool Succeeded,
    bool WasCanceled,
    Exception? Error)
{
    public static BrowserWorkspaceSnapshotRestoreApplicationResult Completed => new(true, false, null);
    public static BrowserWorkspaceSnapshotRestoreApplicationResult Canceled => new(false, true, null);

    public static BrowserWorkspaceSnapshotRestoreApplicationResult Failed(string errorMessage) =>
        Failed(new InvalidOperationException(errorMessage));

    public static BrowserWorkspaceSnapshotRestoreApplicationResult Failed(Exception error) =>
        new(false, false, error);
}

internal readonly record struct BrowserWorkspaceSettingsApplyExecution(
    BrowserWorkspaceRuntimeRestoreExecution Restore,
    BrowserDirectoryNavigationExecution? Reload);
