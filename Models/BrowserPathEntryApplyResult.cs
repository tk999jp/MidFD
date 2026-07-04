namespace MidFD.Models;

internal sealed class BrowserPathEntryApplyResult
{
    public bool Succeeded { get; init; }
    public bool ShouldCloseEditor { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}
