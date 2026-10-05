using MidFD.Models;

namespace MidFD.Runtime;

internal interface IViewerModeUiPort : IViewerPreviewUiPort
{
    void HideViewerContent();
    void ApplyPendingBrowserRefresh(BrowserRefreshProcessExecution execution);
    void CleanupBrowserInteraction();
    void HideTransientOverlay();
    PreviewKind GetCurrentSelectionKind();
    void ApplyViewerChrome();
    void UpdateFunctionBar();
    void UpdateMenuState();
    void ShowBrowserSurface();
    void ShowViewerSurface();
    void EnsureStatusBar();
    void RefreshBrowserStatus();
    void ApplyViewerStatus();
    void LogViewerLayout(string reason);
    void ClearPreview(string message);
    string? GetCurrentPreviewSelectionPath();
}

internal readonly record struct ViewerExitApplicationResult(
    bool Exited,
    bool AppliedPendingBrowserRefresh);

/// <summary>
/// Browser/Viewer mode transitionのapplication側状態遷移を担当する。
/// Controlの表示操作は型付きUI portへ返し、WinForms型をapplication層へ持ち込まない。
/// </summary>
internal sealed class ViewerModeApplicationCoordinator
{
    private readonly ViewerSessionState _state;
    private readonly ViewerWorkflowApplicationCoordinator _workflow;
    private readonly ViewerPreviewApplicationCoordinator _preview;
    private readonly BrowserRefreshWorkflowApplicationCoordinator? _refreshWorkflow;

    public ViewerModeApplicationCoordinator(
        ViewerSessionState state,
        ViewerWorkflowApplicationCoordinator workflow,
        ViewerPreviewApplicationCoordinator preview,
        BrowserRefreshWorkflowApplicationCoordinator? refreshWorkflow = null)
    {
        _state = state;
        _workflow = workflow;
        _preview = preview;
        _refreshWorkflow = refreshWorkflow;
    }

    public void CycleEncodingAndRefresh(IViewerModeUiPort ui)
    {
        if (_state.Mode != ViewerApplicationMode.Viewer)
        {
            return;
        }

        _workflow.CycleEncodingPreference();
        ui.ApplyViewerStatus();
        _preview.RequestRefresh(
            ui.GetCurrentPreviewSelectionPath(),
            force: true,
            previewKindOverride: null,
            ui);
    }

    public ViewerExitApplicationResult TryExitToBrowser(
        IViewerModeUiPort ui,
        BrowserRefreshProcessRequest? refreshRequest = null)
    {
        if (_state.Mode != ViewerApplicationMode.Viewer)
        {
            return new ViewerExitApplicationResult(false, false);
        }

        ui.HideViewerContent();
        Switch(ViewerApplicationMode.Browser, previewKindOverride: null, ui);
        bool appliedPendingRefresh = false;
        if (_refreshWorkflow is { IsPending: true } refreshWorkflow &&
            refreshRequest is { } request)
        {
            BrowserRefreshProcessExecution execution = refreshWorkflow.ExecutePendingRefreshProcessing(
                request.IsBrowserMode,
                request.IsBusy,
                request.IsExitPending,
                request.IsDisposed,
                request.Options,
                request.ColumnCount,
                request.ShellState);
            appliedPendingRefresh = execution.Started;
            if (appliedPendingRefresh)
            {
                ui.ApplyPendingBrowserRefresh(execution);
            }
        }

        return new ViewerExitApplicationResult(true, appliedPendingRefresh);
    }

    public void PrepareForBrowserWorkspaceNavigation(IViewerModeUiPort ui)
    {
        ui.ClearPreview("No Preview");
        if (_state.Mode != ViewerApplicationMode.Viewer)
        {
            return;
        }

        ui.HideViewerContent();
        Switch(ViewerApplicationMode.Browser, previewKindOverride: null, ui);
    }

    public void Switch(ViewerApplicationMode mode, PreviewKind? previewKindOverride, IViewerModeUiPort ui)
    {
        if (mode != ViewerApplicationMode.Browser)
        {
            ui.CleanupBrowserInteraction();
        }
        ui.HideTransientOverlay();
        if (mode == ViewerApplicationMode.Browser)
        {
            _preview.CancelRequest();
        }

        _state.Mode = mode;
        ViewerModeLifecyclePlan lifecyclePlan =
            _workflow.CreateModeLifecyclePlan(
                mode == ViewerApplicationMode.Browser,
                _state.CurrentKind,
                ui.GetCurrentSelectionKind());
        _state.CurrentKind = previewKindOverride ?? lifecyclePlan.NextViewerKind;

        ui.ApplyViewerChrome();
        ui.UpdateFunctionBar();
        ui.UpdateMenuState();

        if (mode == ViewerApplicationMode.Browser)
        {
            ui.ShowBrowserSurface();
            ui.EnsureStatusBar();
            ui.RefreshBrowserStatus();
            return;
        }

        ui.ShowViewerSurface();
        ui.EnsureStatusBar();
        ui.ApplyViewerStatus();
        ui.LogViewerLayout("SwitchUIMode Viewer");
        if (lifecyclePlan.ShouldClearPreview)
        {
            ui.ClearPreview(lifecyclePlan.ClearMessage);
        }
        if (lifecyclePlan.ShouldRefreshPreview)
        {
            _preview.RequestRefresh(
                ui.GetCurrentPreviewSelectionPath(),
                force: true,
                previewKindOverride,
                ui);
        }
    }
}
