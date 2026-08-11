using System.Globalization;
using System.Text;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Presentation;

public static class BrowserTabPresentationHelper
{
    public static BrowserTabPresentationSnapshot BuildPresentation(
        BrowserTabState state,
        int index,
        Func<string, string?> resolveAliasDisplayName)
    {
        string titleBasisPath = state.IsLocked && !string.IsNullOrWhiteSpace(state.StartupPath)
            ? state.StartupPath
            : state.CurrentPath;
        string? relativeSuffix = state.IsLocked
            ? GetRelativeSuffix(state.StartupPath, state.CurrentPath)
            : null;
        string? resolvedAliasTitle = ResolveAliasDisplayName(titleBasisPath, resolveAliasDisplayName);
        string? aliasTitle = resolvedAliasTitle != null && (!state.IsLocked || relativeSuffix != null)
            ? resolvedAliasTitle
            : null;
        string baseTitle = aliasTitle ?? titleBasisPath;
        string displayCore = string.IsNullOrWhiteSpace(relativeSuffix)
            ? baseTitle
            : $"{baseTitle} >{relativeSuffix}";
        bool fixedWithinBase = state.IsLocked && !string.IsNullOrWhiteSpace(state.StartupPath)
            && relativeSuffix != null;
        string headerCore = displayCore;
        string prefixText = (state.IsLocked ? "■ " : string.Empty) + (state.IsReadOnly ? "[RO] " : string.Empty);
        return new BrowserTabPresentationSnapshot(
            titleBasisPath,
            aliasTitle,
            baseTitle,
            relativeSuffix,
            prefixText,
            fixedWithinBase || aliasTitle != null ? null : state.CurrentPath,
            displayCore,
            $"{prefixText}{headerCore}",
            BuildToolTip(state, displayCore),
            state.CurrentPath);
    }

    public static string BuildHeaderSnapshotKey(
        bool showCategoryRow,
        int activeCategoryIndex,
        int activeTabIndex,
        IReadOnlyList<BrowserTabStripCategoryItem> categories,
        IReadOnlyList<BrowserTabStripItem> tabs)
    {
        var sb = new StringBuilder();
        AppendSnapshotField(sb, showCategoryRow ? "1" : "0");
        AppendSnapshotField(sb, activeCategoryIndex.ToString(CultureInfo.InvariantCulture));
        AppendSnapshotField(sb, activeTabIndex.ToString(CultureInfo.InvariantCulture));
        AppendSnapshotField(sb, categories.Count.ToString(CultureInfo.InvariantCulture));
        foreach (BrowserTabStripCategoryItem category in categories)
        {
            AppendSnapshotField(sb, category.CategoryId);
            AppendSnapshotField(sb, category.Text);
            AppendSnapshotField(sb, category.ToolTipText ?? string.Empty);
            AppendSnapshotField(sb, category.Kind.ToString());
        }
        AppendSnapshotField(sb, tabs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (BrowserTabStripItem tab in tabs)
        {
            AppendSnapshotField(sb, tab.Text);
            AppendSnapshotField(sb, tab.ToolTipText ?? string.Empty);
        }
        return sb.ToString();
    }

    public static string BuildCategoryToolTip(BrowserTabCategoryDefinition category)
    {
        string name = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName.Trim();
        return string.Equals(category.Id, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase)
            ? $"カテゴリ: {name}"
            : $"カテゴリ: {name}{Environment.NewLine}ID: {category.Id}";
    }

    public static string BuildTabTitle(string? path, Func<string, string?> resolveAliasDisplayName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "新しいタブ";
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            normalizedPath = path;
        }

        string? aliasDisplayName = resolveAliasDisplayName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(aliasDisplayName))
        {
            return aliasDisplayName;
        }

        string? root = null;
        try
        {
            root = Path.GetPathRoot(normalizedPath);
        }
        catch
        {
            root = null;
        }

        if (!string.IsNullOrWhiteSpace(root))
        {
            string normalizedRoot = EnsureTrailingDirectorySeparator(root);
            string trimmedPath = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string trimmedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedRoot;
            }

            string relative = trimmedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? trimmedPath.Substring(normalizedRoot.Length)
                : trimmedPath;
            string[] segments = relative
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return normalizedRoot;
            }
            if (segments.Length == 1)
            {
                return $"{normalizedRoot}{segments[0]}{Path.DirectorySeparatorChar}";
            }
            return $"{normalizedRoot}…{Path.DirectorySeparatorChar}{segments[^1]}{Path.DirectorySeparatorChar}";
        }

        string fallback = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(fallback);
        return !string.IsNullOrWhiteSpace(name) ? name : path;
    }

    public static string BuildHeaderText(BrowserTabState state, int index)
    {
        string title = string.IsNullOrWhiteSpace(state.Title) ? $"Tab {index + 1}" : state.Title;
        string lockedPrefix = state.IsLocked ? "■ " : string.Empty;
        string readOnlyPrefix = state.IsReadOnly ? "[RO] " : string.Empty;
        return $"{lockedPrefix}{readOnlyPrefix}{title}";
    }

    public static string BuildToolTip(BrowserTabState state)
    {
        return BuildToolTip(state, string.IsNullOrWhiteSpace(state.Title) ? "新しいタブ" : state.Title);
    }

    private static string BuildToolTip(BrowserTabState state, string heading)
    {
        var lines = new List<string>
        {
            state.IsLocked ? "状態: 固定タブ" : "状態: 通常タブ",
            state.IsReadOnly ? "ReadOnly: 有効" : "ReadOnly: 無効"
        };
        lines.Add($"見出し: {heading}");
        if (!string.IsNullOrWhiteSpace(state.CurrentPath))
        {
            lines.Add($"場所: {state.CurrentPath}");
        }
        if (state.IsLocked && !string.IsNullOrWhiteSpace(state.StartupPath))
        {
            lines.Add($"起動元: {state.StartupPath}");
        }
        return string.Join(Environment.NewLine, lines.Where(static line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string? ResolveAliasDisplayName(string? path, Func<string, string?> resolveAliasDisplayName)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            normalizedPath = path;
        }
        return resolveAliasDisplayName(normalizedPath);
    }

    private static string? GetRelativeSuffix(string? basePath, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(currentPath)) return null;
        try
        {
            string normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedCurrent = Path.GetFullPath(currentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedBase, normalizedCurrent, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            string basePrefix = normalizedBase + Path.DirectorySeparatorChar;
            if (!normalizedCurrent.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string relative = normalizedCurrent[basePrefix.Length..];
            return string.Join(">", relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            return null;
        }
    }

    private static void AppendSnapshotField(StringBuilder sb, string value)
    {
        sb.Append(value.Length);
        sb.Append(':');
        sb.Append(value);
        sb.Append('|');
    }

    public static string EnsureTrailingDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        char lastChar = path[^1];
        if (lastChar == Path.DirectorySeparatorChar || lastChar == Path.AltDirectorySeparatorChar)
        {
            return path;
        }
        return path + Path.DirectorySeparatorChar;
    }
}

public sealed record BrowserTabPresentationSnapshot(
    string TitleBasisPath,
    string? AliasTitle,
    string BaseTitle,
    string? RelativeSuffix,
    string PrefixText,
    string? CanonicalPath,
    string DisplayCore,
    string HeaderText,
    string ToolTipText,
    string CurrentPath);
