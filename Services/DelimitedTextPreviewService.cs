using System.Text;

namespace MidFD.Services;

public sealed record DelimitedTextTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, char Delimiter, string EncodingLabel);

public static class DelimitedTextPreviewService
{
    public static async Task<DelimitedTextTable> ReadAsync(string path, int maxBytes, CancellationToken token)
    {
        string text = await PreviewService.GetTextPreviewAsync(path, maxBytes, token);
        token.ThrowIfCancellationRequested();
        char delimiter = string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        List<IReadOnlyList<string>> records = Parse(text, delimiter).ToList();
        IReadOnlyList<string> headers = records.Count == 0 ? Array.Empty<string>() : records[0];
        IReadOnlyList<IReadOnlyList<string>> rows = records.Count <= 1 ? Array.Empty<IReadOnlyList<string>>() : records.Skip(1).ToList();
        return new DelimitedTextTable(headers, rows, delimiter, "detected");
    }

    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text, char delimiter)
    {
        var records = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == delimiter && !quoted) { row.Add(field.ToString()); field.Clear(); }
            else if ((ch == '\r' || ch == '\n') && !quoted)
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear(); records.Add(row.ToArray()); row.Clear();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); records.Add(row.ToArray()); }
        return records;
    }
}
