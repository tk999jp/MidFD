namespace MidFD.Commands;

public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;
    private readonly Func<string, CommandExecutionContext, bool> _executor;

    public CommandDispatcher(CommandRegistry registry, Func<string, CommandExecutionContext, bool> executor)
    {
        _registry = registry;
        _executor = executor;
    }

    public bool TryExecute(string commandId, CommandExecutionContext context)
    {
        if (_registry.Find(commandId) is null)
        {
            return false;
        }

        return _executor(commandId, context);
    }
}
