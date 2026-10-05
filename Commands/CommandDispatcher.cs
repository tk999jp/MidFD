namespace MidFD.Commands;

public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;

    public CommandDispatcher(CommandRegistry registry)
    {
        _registry = registry;
    }

    public bool CanDispatch(string commandId, CommandExecutionContext context)
    {
        return _registry.Find(commandId) is { Scope: var scope } && scope == context.Scope;
    }
}
