namespace MidFD.Services;

public static class MarkdownSelectionSourceResolver
{
    public static string? ResolveContainingBlocks(string source, string? serializedRanges)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(serializedRanges))
        {
            return null;
        }

        var validRanges = new List<(int Start, int End)>();
        foreach (string range in serializedRanges.Split(','))
        {
            string[] values = range.Split(':');
            if (values.Length != 2
                || !int.TryParse(values[0], out int rangeStart)
                || !int.TryParse(values[1], out int length)
                || rangeStart < 0
                || length < 0
                || rangeStart > source.Length - length)
            {
                return null;
            }

            if (length == 0)
            {
                continue;
            }

            validRanges.Add((rangeStart, rangeStart + length));
        }

        if (validRanges.Count == 0)
        {
            return null;
        }

        int start = validRanges.Min(range => range.Start);
        int end = validRanges.Max(range => range.End);
        return source.Substring(start, end - start);
    }
}
