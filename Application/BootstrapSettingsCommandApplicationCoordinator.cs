using MidFD.Commands;

namespace MidFD.Runtime;

/// <summary>
/// Routes the pre-MainForm first-launch settings save through the shared registry and dispatcher.
/// The existing SettingsApplicationCoordinator remains the persistence owner.
/// </summary>
internal sealed class BootstrapSettingsCommandApplicationCoordinator : ICommandApplicationExecutionPort
{
    private readonly ISettingsApplicationPort _settings;

    public BootstrapSettingsCommandApplicationCoordinator(ISettingsApplicationPort settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public CommandApplicationResult TryExecute(string commandId, CommandExecutionContext context)
    {
        if (commandId != CommandIds.AppSettingsSave || context.Scope != CommandScope.Global)
        {
            return CommandApplicationResult.NotHandled;
        }

        CommandBusyBehavior behavior = CommandBusyPolicy.GetBehavior(commandId, context);
        // The first-launch setup runs before the MainForm and file-operation runtime exist.
        if (!CommandBusyPolicy.CanExecute(behavior, isBusy: false))
        {
            return CommandApplicationResult.NotHandled;
        }

        return CommandApplicationCoordinator.ExecuteSettingsSaveCommand(_settings, context.SettingsSave);
    }

    public CommandApplicationResult Continue(
        CommandApplicationInteractionRequest request,
        CommandApplicationInteractionResponse response) => CommandApplicationResult.NotHandled;
}
