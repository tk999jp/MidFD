using System.Globalization;
using System.Text;

namespace MidFD.Services;

internal static class ConfiguredEditorSearchHitArgumentTemplateService
{
    internal static bool TryValidate(string? template, out string? error)
    {
        return TryParse(template, out _, out _, out error);
    }

    internal static bool UsesPositionArguments(string? template)
    {
        return TryParse(template, out _, out bool usesPositionArguments, out _) && usesPositionArguments;
    }

    internal static string GetMenuCaption(string? template, bool configuredEditorAvailable)
    {
        if (!configuredEditorAvailable)
        {
            return "notepad.exeで検索結果のファイルのみを開く";
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            return "外部エディタでファイルを開く";
        }

        if (TryParse(template, out _, out bool usesPositionArguments, out _))
        {
            return usesPositionArguments
                ? "検索位置を引数で外部エディタへ渡して開く"
                : "設定引数で外部エディタを開く";
        }

        return "外部エディタ引数テンプレートを使用";
    }

    internal static bool TryExpand(
        string? template,
        string filePath,
        int line,
        int column,
        out IReadOnlyList<string> arguments,
        out bool usesPositionArguments,
        out string? error)
    {
        arguments = Array.Empty<string>();
        usesPositionArguments = false;
        error = null;

        if (line < 1 || column < 1)
        {
            error = "検索hitの行番号と列番号は1以上である必要があります。";
            return false;
        }

        if (!TryParse(template, out IReadOnlyList<string> parts, out usesPositionArguments, out error))
        {
            return false;
        }

        var expanded = new string[parts.Count];
        for (int index = 0; index < parts.Count; index++)
        {
            if (!TryExpandToken(parts[index], filePath, line, column, out expanded[index], out error))
            {
                return false;
            }
        }

        arguments = expanded;
        return true;
    }

    private static bool TryParse(
        string? template,
        out IReadOnlyList<string> arguments,
        out bool usesPositionArguments,
        out string? error)
    {
        arguments = Array.Empty<string>();
        usesPositionArguments = false;
        error = null;

        if (string.IsNullOrWhiteSpace(template))
        {
            error = "引数テンプレートが空です。";
            return false;
        }

        if (template.Contains('\0') || template.Contains('\r') || template.Contains('\n'))
        {
            error = "引数テンプレートにNULまたは改行を含めることはできません。";
            return false;
        }

        var parts = new List<string>();
        int index = 0;
        bool hasFilePlaceholder = false;
        while (index < template.Length)
        {
            while (index < template.Length && IsArgumentSeparator(template[index])) index++;
            if (index >= template.Length) break;

            var token = new StringBuilder();
            bool inQuotes = false;
            bool tokenStarted = false;
            while (index < template.Length && (inQuotes || !IsArgumentSeparator(template[index])))
            {
                if (template[index] == '\\')
                {
                    int slashStart = index;
                    while (index < template.Length && template[index] == '\\') index++;
                    int slashCount = index - slashStart;

                    if (index < template.Length && template[index] == '"')
                    {
                        token.Append('\\', slashCount / 2);
                        if (slashCount % 2 == 0)
                        {
                            inQuotes = !inQuotes;
                        }
                        else
                        {
                            token.Append('"');
                        }
                        index++;
                    }
                    else
                    {
                        token.Append('\\', slashCount);
                    }

                    tokenStarted = true;
                    continue;
                }

                char current = template[index++];
                if (current == '"')
                {
                    inQuotes = !inQuotes;
                }
                else
                {
                    token.Append(current);
                }

                tokenStarted = true;
            }

            if (inQuotes)
            {
                error = "引数テンプレートの引用符が閉じていません。";
                return false;
            }

            if (tokenStarted)
            {
                string parsedToken = token.ToString();
                if (!TryValidatePlaceholders(parsedToken, ref hasFilePlaceholder, ref usesPositionArguments, out error))
                {
                    return false;
                }
                parts.Add(parsedToken);
            }
        }

        if (!hasFilePlaceholder)
        {
            error = "引数テンプレートには {file} が必要です。";
            return false;
        }

        arguments = parts;
        return true;
    }

    private static bool IsArgumentSeparator(char value) => value is ' ' or '\t';

    private static bool TryValidatePlaceholders(
        string token,
        ref bool hasFilePlaceholder,
        ref bool usesPositionArguments,
        out string? error)
    {
        error = null;
        int index = 0;
        while (index < token.Length)
        {
            char current = token[index];
            if (current == '}')
            {
                error = "引数テンプレートに対応しない '}' があります。";
                return false;
            }

            if (current != '{')
            {
                index++;
                continue;
            }

            int close = token.IndexOf('}', index + 1);
            if (close < 0)
            {
                error = "引数テンプレートのplaceholderが閉じていません。";
                return false;
            }

            string placeholder = token[(index + 1)..close];
            switch (placeholder)
            {
                case "file":
                    hasFilePlaceholder = true;
                    break;
                case "line":
                case "column":
                    usesPositionArguments = true;
                    break;
                default:
                    error = $"未定義のplaceholderです: {{{placeholder}}}";
                    return false;
            }

            index = close + 1;
        }

        return true;
    }

    private static bool TryExpandToken(
        string token,
        string filePath,
        int line,
        int column,
        out string expanded,
        out string? error)
    {
        error = null;
        var result = new StringBuilder(token.Length + filePath.Length);
        int index = 0;
        while (index < token.Length)
        {
            if (token[index] != '{')
            {
                result.Append(token[index++]);
                continue;
            }

            int close = token.IndexOf('}', index + 1);
            if (close < 0)
            {
                expanded = string.Empty;
                error = "引数テンプレートのplaceholderが閉じていません。";
                return false;
            }

            string placeholder = token[(index + 1)..close];
            result.Append(placeholder switch
            {
                "file" => filePath,
                "line" => line.ToString(CultureInfo.InvariantCulture),
                "column" => column.ToString(CultureInfo.InvariantCulture),
                _ => string.Empty
            });
            index = close + 1;
        }

        expanded = result.ToString();
        return true;
    }
}
