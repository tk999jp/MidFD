using System;
using System.Collections.Generic;
using System.Linq;

namespace MidFD.Models;

public sealed class TabFilterLockState
{
    public bool Enabled { get; set; }
    public string ExtensionText { get; set; } = string.Empty;
    public List<string> IncludeExtensions { get; set; } = new();
    public DateTime? ModifiedFromLocal { get; set; }
    public DateTime? ModifiedToLocal { get; set; }
    public bool GitUnignoredOnly { get; set; }

    public bool HasAnyCondition =>
        IncludeExtensions.Count > 0
        || ModifiedFromLocal.HasValue
        || ModifiedToLocal.HasValue
        || GitUnignoredOnly;

    public TabFilterLockState Clone()
    {
        return new TabFilterLockState
        {
            Enabled = Enabled,
            ExtensionText = ExtensionText ?? string.Empty,
            IncludeExtensions = new List<string>(IncludeExtensions ?? new List<string>()),
            ModifiedFromLocal = ModifiedFromLocal,
            ModifiedToLocal = ModifiedToLocal,
            GitUnignoredOnly = GitUnignoredOnly
        };
    }

    public static TabFilterLockState Disabled() => new();

    public static List<string> NormalizeExtensions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        char[] separators = { ';', ',', ' ', '\t', '\r', '\n', '\u3000' };
        return text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part.StartsWith(".", StringComparison.Ordinal) ? part : "." + part)
            .Select(static part => part.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
