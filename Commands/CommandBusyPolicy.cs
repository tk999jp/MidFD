namespace MidFD.Commands;

internal enum CommandBusyBehavior
{
    Block,
    Allow,
    DelegateToFileOperation,
    Unaffected,
    Cancel
}

/// <summary>
/// FileOperation busy時のCommand policy。UI projectionとdispatcherで同じ分類を使う。
/// </summary>
internal static class CommandBusyPolicy
{
    public static CommandBusyBehavior GetBehavior(
        string commandId,
        CommandExecutionContext? context = null)
    {
        if (commandId == CommandIds.FileOperationCancel)
        {
            return CommandBusyBehavior.Cancel;
        }

        if (commandId == CommandIds.AppOpenSystemInformation ||
            commandId == CommandIds.AppOpenSettings ||
            commandId == CommandIds.AppOpenCommandLauncher)
        {
            return CommandBusyBehavior.Allow;
        }

        if (commandId == CommandIds.BrowserChangeAttributes ||
            commandId == CommandIds.BrowserCreateDirectory ||
            commandId == CommandIds.BrowserCreateFile ||
            commandId == CommandIds.ArchivePack ||
            commandId == CommandIds.ArchiveUnpack ||
            commandId == CommandIds.ArchiveHash ||
            commandId == CommandIds.BrowserClipboardCopy ||
            commandId == CommandIds.ClipboardPaste ||
            commandId == CommandIds.ClipboardCut ||
            commandId == CommandIds.FileCopy ||
            commandId == CommandIds.FileMove ||
            commandId == CommandIds.FileImportExternalDrop ||
            commandId == CommandIds.FileRename ||
            commandId == CommandIds.FileDelete ||
            commandId == CommandIds.FileDeletePermanent ||
            commandId == CommandIds.AppEmptyManagedTrash ||
            commandId == CommandIds.AppRestoreManagedTrash ||
            commandId == CommandIds.AppDeleteManagedTrashForever ||
            commandId == CommandIds.AppCleanMissingManagedTrash)
        {
            return CommandBusyBehavior.DelegateToFileOperation;
        }

        if (commandId == CommandIds.AppSettingsImport &&
            context?.SettingsTransfer?.Action == SettingsTransferAction.ReadImport)
        {
            return CommandBusyBehavior.Unaffected;
        }

        if (IsUnaffectedCommand(commandId))
        {
            return CommandBusyBehavior.Unaffected;
        }

        return CommandBusyBehavior.Block;
    }

    public static bool IsEnabledWhileBusy(string commandId) =>
        IsAcceptedWhileBusy(GetBehavior(commandId));

    public static bool IsEnabledWhileBusy(string commandId, CommandExecutionContext? context) =>
        IsAcceptedWhileBusy(GetBehavior(commandId, context));

    public static bool CanExecute(CommandBusyBehavior behavior, bool isBusy) =>
        !isBusy || IsAcceptedWhileBusy(behavior);

    public static bool CanMutateBrowserState(bool isBusy) =>
        CanExecute(CommandBusyBehavior.Block, isBusy);

    public static bool CanStartFileOperation(bool isBusy) =>
        CanExecute(CommandBusyBehavior.DelegateToFileOperation, isBusy);

    public static bool RequiresBusyState(CommandBusyBehavior behavior) =>
        behavior is CommandBusyBehavior.Block or CommandBusyBehavior.DelegateToFileOperation;

    private static bool IsAcceptedWhileBusy(CommandBusyBehavior behavior) =>
        behavior is CommandBusyBehavior.Allow or CommandBusyBehavior.Unaffected or CommandBusyBehavior.Cancel;

    private static bool IsUnaffectedCommand(string commandId) =>
        commandId == CommandIds.AppCommandPaletteFavoriteToggle ||
        commandId == CommandIds.AppOpenExternalUrl ||
        commandId == CommandIds.ArchiveHashResultCopy ||
        commandId == CommandIds.BrowserDragOut ||
        commandId == CommandIds.BrowserSearchResultsFilter ||
        commandId == CommandIds.BrowserSearchResultsOpenHitInEditor ||
        commandId == CommandIds.BrowserTabNext ||
        commandId == CommandIds.BrowserTabPrevious ||
        commandId == CommandIds.BrowserTabActivate ||
        commandId == CommandIds.BrowserCursorMove ||
        commandId == CommandIds.BrowserCursorTop ||
        commandId == CommandIds.BrowserCursorBottom ||
        commandId == CommandIds.BrowserTabNavigationWidthSet ||
        commandId == CommandIds.ImageViewerClose ||
        commandId == CommandIds.ImageViewerShowInfo ||
        commandId == CommandIds.ImageViewerZoomIn ||
        commandId == CommandIds.ImageViewerZoomOut ||
        commandId == CommandIds.ImageViewerZoomReset ||
        commandId == CommandIds.ImageViewerFullscreenToggle ||
        commandId == CommandIds.ImageViewerFullscreenExit ||
        commandId == CommandIds.ImageViewerVideoSeekToStart ||
        commandId == CommandIds.ImageViewerVideoSeekBackward ||
        commandId == CommandIds.ImageViewerVideoSeekForward ||
        commandId == CommandIds.ImageViewerVideoSeekToPosition ||
        commandId == CommandIds.TextViewerWordWrapToggle ||
        commandId == CommandIds.TextViewerOpenExternalEditor ||
        commandId == CommandIds.TextViewerNavigate;
}
