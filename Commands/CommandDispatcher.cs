namespace MidFD.Commands;

using MidFD.Runtime;

public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;
    private readonly ICommandApplicationExecutionPort? _application;

    public CommandDispatcher(CommandRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    internal CommandDispatcher(CommandRegistry registry, ICommandApplicationExecutionPort application)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public bool CanDispatch(string commandId, CommandExecutionContext context)
    {
        return context is not null &&
            _registry.Find(commandId) is { Scope: var scope } &&
            scope == context.Scope;
    }

    internal CommandApplicationResult Dispatch(string commandId, CommandExecutionContext context)
    {
        if (!CanDispatch(commandId, context))
        {
            return CommandApplicationResult.NotHandled;
        }

        return _application?.TryExecute(commandId, context) ?? CommandApplicationResult.NotHandled;
    }

    internal CommandApplicationResult Continue(
        CommandApplicationInteractionRequest request,
        CommandApplicationInteractionResponse response) =>
        _application?.Continue(request, response) ?? CommandApplicationResult.NotHandled;
}
