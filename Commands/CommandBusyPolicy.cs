namespace MidFD.Commands;

internal enum CommandBusyBehavior
{
    Block,
    Allow,
    DelegateToFileOperation
}

/// <summary>
/// FileOperation busy時のCommand policy。UI projectionとdispatcherで同じ分類を使う。
/// </summary>
internal static class CommandBusyPolicy
{
    public static CommandBusyBehavior GetBehavior(string commandId)
    {
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
            commandId == CommandIds.FileRename ||
            commandId == CommandIds.FileDelete ||
            commandId == CommandIds.AppEmptyManagedTrash)
        {
            return CommandBusyBehavior.DelegateToFileOperation;
        }

        return CommandBusyBehavior.Block;
    }

    public static bool IsEnabledWhileBusy(string commandId) =>
        GetBehavior(commandId) == CommandBusyBehavior.Allow;

    public static bool CanExecute(CommandBusyBehavior behavior, bool isBusy) =>
        !isBusy || behavior == CommandBusyBehavior.Allow;

    public static bool CanMutateBrowserState(bool isBusy) =>
        CanExecute(CommandBusyBehavior.Block, isBusy);

    public static bool CanStartFileOperation(bool isBusy) =>
        CanExecute(CommandBusyBehavior.DelegateToFileOperation, isBusy);
}
