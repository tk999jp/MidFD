using MidFD.Configuration;

namespace MidFD.Helpers;

public static class FunctionBarLabelFormatter
{
    public static string ExtractDisplayText(string? hiddenText)
    {
        if (string.IsNullOrWhiteSpace(hiddenText))
        {
            return string.Empty;
        }

        int separatorIndex = hiddenText.IndexOf(':');
        string displayText = separatorIndex >= 0
            ? hiddenText[(separatorIndex + 1)..]
            : hiddenText;
        return InputSettings.NormalizeFunctionBarLabelText(displayText);
    }

    public static string? ResolveHotKeyCharacter(string? hotKeyHint)
    {
        if (string.IsNullOrWhiteSpace(hotKeyHint))
        {
            return null;
        }

        string trimmed = hotKeyHint.Trim();
        if (trimmed.Length != 1)
        {
            return null;
        }

        char ch = trimmed[0];
        if (ch >= 'A' && ch <= 'Z')
        {
            return char.ToUpperInvariant(ch).ToString();
        }
        if (ch >= 'a' && ch <= 'z')
        {
            return char.ToUpperInvariant(ch).ToString();
        }

        return null;
    }

    public static bool TryBuildHotKeySegments(
        string labelText,
        string? hotKeyCharacter,
        out int hotKeyIndex,
        out string prefix,
        out string hotKey,
        out string suffix)
    {
        hotKeyIndex = -1;
        prefix = string.Empty;
        hotKey = string.Empty;
        suffix = string.Empty;

        if (string.IsNullOrWhiteSpace(labelText) || string.IsNullOrWhiteSpace(hotKeyCharacter))
        {
            return false;
        }

        string normalizedHotKey = hotKeyCharacter.Trim();
        if (normalizedHotKey.Length != 1)
        {
            return false;
        }

        char hotKeyChar = normalizedHotKey[0];
        for (int i = 0; i < labelText.Length; i++)
        {
            char labelChar = labelText[i];
            if (char.ToUpperInvariant(labelChar) == char.ToUpperInvariant(hotKeyChar))
            {
                hotKeyIndex = i;
                prefix = labelText[..i];
                hotKey = char.ToUpperInvariant(labelChar).ToString();
                suffix = labelText[(i + 1)..];
                return true;
            }
        }

        return false;
    }

    public static string GetShortenedLabel(string fullLabelPart)
    {
        if (string.IsNullOrEmpty(fullLabelPart)) return fullLabelPart;
        return fullLabelPart switch
        {
            "help" => "hlp",
            "exec" => "exc",
            "copy" => "cpy",
            "edit" => "edt",
            "sort" => "srt",
            "filter" => "flt",
            "tree" => "tre",
            "logd" => "log",
            "unpk" => "unp",
            "encode" => "enc",
            "wrap" => "wrp",
            "ren" => "ren",
            "top" => "top",
            "btm" => "btm",
            "l:enc" => "enc",
            "w:wrap" => "wrp",
            "^f:find" => "find",
            "f3:next" => "next",
            "s+f3:prv" => "prev",
            "qt(en/es)" => "quit",
            _ => fullLabelPart
        };
    }
}
