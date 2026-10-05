namespace MidFD.Models;

public class QuickAccessStore
{
    internal bool LoadFailed { get; set; }
    public List<QuickAccessEntry> Bookmarks { get; set; } = new();
    public List<QuickAccessEntry> Recents { get; set; } = new();
    public List<QuickAccessEntry> Aliases { get; set; } = new();
    public List<QuickAccessEntry> Commands { get; set; } = new();

    public QuickAccessStore Clone()
    {
        return new QuickAccessStore
        {
            LoadFailed = LoadFailed,
            Bookmarks = Bookmarks.Select(item => item.Clone()).ToList(),
            Recents = Recents.Select(item => item.Clone()).ToList(),
            Aliases = Aliases.Select(item => item.Clone()).ToList(),
            Commands = Commands.Select(item => item.Clone()).ToList()
        };
    }
}
