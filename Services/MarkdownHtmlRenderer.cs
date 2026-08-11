using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MidFD.Services;

public static class MarkdownHtmlRenderer
{
    public static string Render(string markdown, string? markdownPath = null)
    {
        var html = new StringBuilder("<!doctype html><html scroll=\"auto\"><head><meta charset='utf-8'><style>html{margin:0;box-sizing:border-box;background:#202020}body{margin:0;box-sizing:border-box;border-top:1px solid #555;font-family:Segoe UI, sans-serif;padding:20px 28px;background:#202020;color:#eee;line-height:1.55}*{box-sizing:border-box}h1,h2,h3,h4,h5,h6{margin:1.1em 0 .45em;color:#fff;overflow-wrap:break-word;word-wrap:break-word}p{margin:.55em 0;overflow-wrap:break-word;word-wrap:break-word}code{font-family:Consolas,monospace;background:#303030;padding:2px 5px;border-radius:3px;overflow-wrap:break-word;word-wrap:break-word}pre{font-family:Consolas,monospace;white-space:pre-wrap;max-width:100%;overflow-x:auto;background:#151515;padding:10px;border:1px solid #444;border-radius:4px}blockquote{margin:.7em 0;padding:.35em .9em;border-left:4px solid #777;background:#292929;overflow-wrap:break-word;word-wrap:break-word}.md-table-scroll{margin:1em 0;overflow-x:auto}table{border-collapse:collapse;min-width:50%}th,td{border:1px solid #777;padding:6px 10px;text-align:left}th{background:#3d4f66;color:#fff}tr:nth-child(even){background:#292929}a{color:#66ccff;overflow-wrap:break-word;word-wrap:break-word}img{max-width:100%;height:auto}.md-image-fallback{color:#bbb;font-style:italic}</style><script>function midfdHasSelection(){return document.selection&&document.selection.type!==\"None\";}function midfdGetSelectionSourceBlocks(){if(!midfdHasSelection()||!document.body){return \"\";}var selection=document.selection.createRange(),values=[],elements=document.all;for(var i=0;i<elements.length;i++){var element=elements[i],start=element.getAttribute('data-md-start'),length=element.getAttribute('data-md-length');if(start===null||length===null){continue;}var block=document.body.createTextRange();block.moveToElementText(element);if(selection.compareEndPoints('EndToStart',block)>0&&selection.compareEndPoints('StartToEnd',block)<0){values.push(start+':'+length);}}return values.join(',');}</script></head><body>");
        html.Replace(
            "</style>",
            $".md-image-preview{{display:block;max-width:{MarkdownInlineImageResolver.MaxMarkdownInlinePreviewWidth}px;width:auto}}.md-image-preview img{{display:block;width:100%;max-width:100%;height:auto}}</style>");
        bool inCode = false;
        bool inList = false;
        bool inTable = false;
        bool tableHeaderWritten = false;
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
        int sourceOffset = 0;
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string rawLine = lines[lineIndex];
            string line = WebUtility.HtmlEncode(rawLine);
            string sourceAttributes = SourceAttributes(sourceOffset, rawLine.Length);
            if (line.StartsWith("```")) { if (inList) { html.Append("</ul>"); inList = false; } inCode = !inCode; html.Append(inCode ? $"<pre{sourceAttributes}>" : "</pre>"); sourceOffset += rawLine.Length + 1; continue; }
            if (inCode) { html.Append(line).Append("\n"); sourceOffset += rawLine.Length + 1; continue; }
            if (!inTable && line.Contains('|') && lineIndex + 1 < lines.Length && IsTableSeparator(WebUtility.HtmlEncode(lines[lineIndex + 1])))
            {
                inTable = true; tableHeaderWritten = true;
                html.Append("<div class=\"md-table-scroll\"><table><thead><tr").Append(sourceAttributes).Append(">"); AppendTableCells(html, TokenizeTableCells(rawLine), "th", sourceOffset, markdownPath); html.Append("</tr></thead><tbody>");
                sourceOffset += rawLine.Length + 1;
                lineIndex++;
                sourceOffset += lines[lineIndex].Length + 1;
                continue;
            }
            if (IsTableSeparator(line)) { inTable = true; tableHeaderWritten = false; continue; }
            if (inTable && line.Contains('|'))
            {
                IReadOnlyList<TableCell> cells = TokenizeTableCells(rawLine);
                if (!tableHeaderWritten) { html.Append("<div class=\"md-table-scroll\"><table><thead><tr").Append(sourceAttributes).Append(">"); AppendTableCells(html, cells, "th", sourceOffset, markdownPath); html.Append("</tr></thead><tbody>"); tableHeaderWritten = true; }
                else { html.Append("<tr").Append(sourceAttributes).Append(">"); AppendTableCells(html, cells, "td", sourceOffset, markdownPath); html.Append("</tr>"); }
                sourceOffset += rawLine.Length + 1;
                continue;
            }
            if (inTable) { html.Append("</tbody></table></div>"); inTable = false; }
            Match heading = Regex.Match(line, "^(#{1,6})\\s+(.+)$");
            if (heading.Success) { CloseList(html, ref inList); int level = heading.Groups[1].Value.Length; html.Append("<h").Append(level).Append(sourceAttributes).Append('>').Append(Inline(rawLine[(level + 1)..], sourceOffset + level + 1, markdownPath)).Append("</h").Append(level).Append('>'); sourceOffset += rawLine.Length + 1; continue; }
            if (line.StartsWith("> ")) { html.Append("<blockquote").Append(sourceAttributes).Append(">").Append(Inline(rawLine[2..], sourceOffset + 2, markdownPath)).Append("</blockquote>"); sourceOffset += rawLine.Length + 1; continue; }
            if (line.StartsWith("- ") || line.StartsWith("* ")) { if (!inList) { html.Append("<ul>"); inList = true; } html.Append("<li").Append(sourceAttributes).Append(">").Append(Inline(rawLine[2..], sourceOffset + 2, markdownPath)).Append("</li>"); sourceOffset += rawLine.Length + 1; continue; }
            CloseList(html, ref inList);
            html.Append(string.IsNullOrWhiteSpace(line) ? $"<br{sourceAttributes}>" : $"<p{sourceAttributes}>{Inline(rawLine, sourceOffset, markdownPath)}</p>");
            sourceOffset += rawLine.Length + 1;
        }
        if (inTable) html.Append("</tbody></table></div>");
        CloseList(html, ref inList);
        return html.Append("</body></html>").ToString();
    }

    private static string Inline(string source, int sourceOffset, string? markdownPath)
    {
        var result = new StringBuilder();
        int cursor = 0;
        foreach (Match match in Regex.Matches(source, @"(!)?\[([^\]]*)\]\(([^\)]+)\)"))
        {
            result.Append(FormatInlineText(WebUtility.HtmlEncode(source[cursor..match.Index])));
            string label = WebUtility.HtmlEncode(match.Groups[2].Value);
            string target = match.Groups[3].Value;
            string attributes = SourceAttributes(sourceOffset + match.Index, match.Length);
            if (match.Groups[1].Success)
            {
                if (MarkdownInlineImageResolver.TryCreateDataUri(markdownPath, target, out string? dataUri, out int displayWidth, out int displayHeight))
                {
                    bool constrainedPreview = displayWidth > 0 && displayHeight > 0;
                    if (constrainedPreview)
                    {
                        result.Append("<div class=\"md-image-preview\">");
                    }
                    result.Append("<img src=\"").Append(WebUtility.HtmlEncode(dataUri)).Append("\" alt=\"").Append(label).Append("\"").Append(attributes).Append(" data-md-image-target=\"").Append(WebUtility.HtmlEncode(target)).Append("\">");
                    if (constrainedPreview)
                    {
                        result.Append("</div>");
                    }
                }
                else
                {
                    result.Append("<span class=\"md-image-fallback\"").Append(attributes).Append(" data-md-image-target=\"").Append(WebUtility.HtmlEncode(target)).Append("\">").Append(label).Append("</span>");
                }
            }
            else
            {
                result.Append("<a href=\"").Append(WebUtility.HtmlEncode(target)).Append("\"").Append(attributes).Append(" data-md-link-target=\"").Append(WebUtility.HtmlEncode(target)).Append("\">").Append(FormatInlineText(label)).Append("</a>");
            }
            cursor = match.Index + match.Length;
        }
        result.Append(FormatInlineText(WebUtility.HtmlEncode(source[cursor..])));
        return result.ToString();
    }

    private static string FormatInlineText(string encoded)
    {
        string result = encoded;
        result = Regex.Replace(result, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        return Regex.Replace(result, @"`([^`]+)`", "<code>$1</code>");
    }
    private static string SourceAttributes(int start, int length) => $" data-md-start=\"{start}\" data-md-length=\"{length}\"";
    private static bool IsTableSeparator(string line) => Regex.IsMatch(line, @"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$");
    private readonly record struct TableCell(string Text, int SourceOffset);

    private static IReadOnlyList<TableCell> TokenizeTableCells(string rawLine)
    {
        int start = 0;
        int end = rawLine.Length;
        while (start < end && char.IsWhiteSpace(rawLine[start])) start++;
        while (end > start && char.IsWhiteSpace(rawLine[end - 1])) end--;
        while (start < end && rawLine[start] == '|') start++;
        while (end > start && rawLine[end - 1] == '|') end--;

        var cells = new List<TableCell>();
        int cellStart = start;
        for (int index = start; index <= end; index++)
        {
            if (index < end && rawLine[index] != '|') continue;

            int textStart = cellStart;
            int textEnd = index;
            while (textStart < textEnd && char.IsWhiteSpace(rawLine[textStart])) textStart++;
            while (textEnd > textStart && char.IsWhiteSpace(rawLine[textEnd - 1])) textEnd--;
            cells.Add(new TableCell(rawLine[textStart..textEnd], textStart));
            cellStart = index + 1;
        }
        return cells;
    }

    private static void AppendTableCells(StringBuilder html, IEnumerable<TableCell> cells, string tag, int sourceOffset, string? markdownPath)
    {
        foreach (TableCell cell in cells) html.Append('<').Append(tag).Append('>').Append(Inline(cell.Text, sourceOffset + cell.SourceOffset, markdownPath)).Append("</").Append(tag).Append('>');
    }
    private static void CloseList(StringBuilder html, ref bool inList)
    {
        if (inList) { html.Append("</ul>"); inList = false; }
    }
}
