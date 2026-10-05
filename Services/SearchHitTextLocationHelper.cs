namespace MidFD.Services;

public static class SearchHitTextLocationHelper
{
    public static bool TryGetLargeTextLine(int oneBasedLine, int indexedLineCount, out int zeroBasedLine)
    {
        zeroBasedLine = oneBasedLine - 1;
        return oneBasedLine >= 1 && zeroBasedLine < indexedLineCount;
    }

    public static bool TryGetSelection(
        string text,
        int oneBasedLine,
        int oneBasedColumn,
        int requestedLength,
        out int start,
        out int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        start = 0;
        length = 0;
        if (oneBasedLine < 1 || oneBasedColumn < 1) return false;

        int zeroBasedLine = oneBasedLine - 1;
        for (int line = 0; line < zeroBasedLine; line++)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0) return false;
            start = newline + 1;
        }

        int lineEnd = text.IndexOf('\n', start);
        if (lineEnd < 0) lineEnd = text.Length;
        if (lineEnd > start && text[lineEnd - 1] == '\r') lineEnd--;
        int zeroBasedColumn = oneBasedColumn - 1;
        if (zeroBasedColumn > lineEnd - start) return false;
        start += zeroBasedColumn;
        length = Math.Clamp(requestedLength, 0, lineEnd - start);
        return true;
    }
}
