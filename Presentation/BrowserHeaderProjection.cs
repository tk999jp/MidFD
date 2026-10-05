using MidFD.Helpers;

namespace MidFD.Presentation;

internal static class BrowserHeaderProjection
{
    public static string BuildRightText(HeaderPresentationHelper.DisplayStrings display, bool hasMarks)
    {
        if (!hasMarks)
        {
            return display.SortFilter;
        }

        string markText = $"Mark: {display.MarkCount} MarkSize: {display.MarkSizeText}";
        int sortIndex = display.SortFilter.IndexOf("S:", StringComparison.Ordinal);
        if (sortIndex < 0)
        {
            return string.IsNullOrWhiteSpace(display.SortFilter)
                ? markText
                : $"{display.SortFilter} {markText}";
        }

        string filterText = display.SortFilter[..sortIndex].TrimEnd();
        string sortText = display.SortFilter[sortIndex..].TrimStart();
        return string.IsNullOrWhiteSpace(filterText)
            ? $"{markText} {sortText}"
            : $"{filterText} {markText} {sortText}";
    }
}
