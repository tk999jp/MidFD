namespace MidFD.Models;

public sealed class ClosedBrowserTabSnapshot
{
    public string CategoryId { get; init; } = string.Empty;
    public BrowserTabState TabState { get; init; } = new();
}
