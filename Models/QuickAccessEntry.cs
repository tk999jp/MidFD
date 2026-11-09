namespace MidFD.Models;

public enum QuickAccessEntryKind
{
    Bookmark = 0,
    Recent = 1,
    Alias = 2,
    History = 3
}

public class QuickAccessEntry
{
    public string DisplayName { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public string Path { get; set; } = string.Empty;
    public QuickAccessEntryKind Kind { get; set; }

    public QuickAccessEntry Clone()
    {
        return new QuickAccessEntry
        {
            DisplayName = DisplayName,
            CategoryName = CategoryName,
            Path = Path,
            Kind = Kind
        };
    }
}
