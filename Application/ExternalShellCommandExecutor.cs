using MidFD.Commands;
using MidFD.Services;

namespace MidFD.Runtime;

internal interface IExternalShellCommandExecutor
{
    string? Execute(BrowserCommandExecutionCommandContext request);
}

internal sealed class ExternalShellCommandExecutor : IExternalShellCommandExecutor
{
    public string? Execute(BrowserCommandExecutionCommandContext request) => ExternalToolService.ExecuteShellCommand(
        request.Command,
        request.Arguments,
        request.WorkingDirectory,
        request.FallbackWorkingDirectory);
}
