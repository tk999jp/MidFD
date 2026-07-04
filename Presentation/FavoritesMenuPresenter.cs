using System.Drawing;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Presentation;

public static class FavoritesMenuPresenter
{
    public static void Build(
        ToolStripMenuItem favoritesMenu,
        IReadOnlyList<QuickAccessEntry> entries,
        Action<string> navigateToPath,
        Action addCurrentLocation,
        Action openQuickAccess,
        Action<ToolStripDropDownItem, Color, Color>? applyDropDownTheme,
        Color themeBackColor,
        Color themeForeColor)
    {
        favoritesMenu.DropDownItems.Clear();

        IReadOnlyList<QuickAccessEntry> favoriteEntries = entries
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            .ToList();
        IReadOnlyList<QuickAccessEntry> categorizedEntries = favoriteEntries
            .Where(entry => !string.IsNullOrWhiteSpace(QuickAccessService.NormalizeCategoryName(entry.CategoryName)))
            .ToList();

        if (categorizedEntries.Count == 0)
        {
            AddFavoritesMenuEntries(favoritesMenu.DropDownItems, favoriteEntries, navigateToPath);
        }
        else
        {
            foreach (IGrouping<string?, QuickAccessEntry> categoryGroup in categorizedEntries
                         .GroupBy(entry => QuickAccessService.NormalizeCategoryName(entry.CategoryName), StringComparer.OrdinalIgnoreCase))
            {
                string categoryName = categoryGroup.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(categoryName))
                {
                    continue;
                }

                var categoryMenu = new MidFD.Controls.TightCascadeToolStripMenuItem(categoryName) { Tag = "FavoriteCategory" };
                AddFavoritesMenuEntries(categoryMenu.DropDownItems, categoryGroup.ToList(), navigateToPath);
                favoritesMenu.DropDownItems.Add(categoryMenu);
            }

            IReadOnlyList<QuickAccessEntry> uncategorizedEntries = favoriteEntries
                .Where(entry => string.IsNullOrWhiteSpace(QuickAccessService.NormalizeCategoryName(entry.CategoryName)))
                .ToList();
            if (uncategorizedEntries.Count > 0)
            {
                if (favoritesMenu.DropDownItems.Count > 0)
                {
                    favoritesMenu.DropDownItems.Add(new ToolStripSeparator());
                }

                AddFavoritesMenuEntries(favoritesMenu.DropDownItems, uncategorizedEntries, navigateToPath);
            }
        }

        if (favoritesMenu.DropDownItems.Count > 0)
        {
            favoritesMenu.DropDownItems.Add(new ToolStripSeparator());
        }

        favoritesMenu.DropDownItems.Add(CreateAddCurrentLocationFavoriteMenuItem(addCurrentLocation));
        favoritesMenu.DropDownItems.Add(CreateOpenQuickAccessMenuItem(openQuickAccess));

        applyDropDownTheme?.Invoke(favoritesMenu, themeBackColor, themeForeColor);
    }

    private static void AddFavoritesMenuEntries(
        ToolStripItemCollection targetItems,
        IReadOnlyList<QuickAccessEntry> entries,
        Action<string> navigateToPath)
    {
        HashSet<string> duplicateDisplayNames = entries
            .Select(ResolveFavoritesMenuDisplayName)
            .Where(displayName => !string.IsNullOrWhiteSpace(displayName))
            .GroupBy(displayName => displayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (QuickAccessEntry entry in entries)
        {
            string path = entry.Path.Trim();
            string displayName = ResolveFavoritesMenuDisplayName(entry);
            if (duplicateDisplayNames.Contains(displayName))
            {
                displayName = $"{displayName} ({path})";
            }

            var item = new ToolStripMenuItem(displayName)
            {
                ToolTipText = path,
                Tag = "FavoriteItem"
            };
            item.Click += (_, _) => navigateToPath(path);
            targetItems.Add(item);
        }
    }

    private static ToolStripMenuItem CreateAddCurrentLocationFavoriteMenuItem(Action addCurrentLocation)
    {
        var item = new ToolStripMenuItem("現在地をお気に入りに追加") { Tag = "FavoriteActionItem" };
        item.Click += (_, _) => addCurrentLocation();
        return item;
    }

    private static ToolStripMenuItem CreateOpenQuickAccessMenuItem(Action openQuickAccess)
    {
        var item = new ToolStripMenuItem("QuickAccessを開く/編集...") { Tag = "FavoriteActionItem" };
        item.Click += (_, _) => openQuickAccess();
        return item;
    }

    private static string ResolveFavoritesMenuDisplayName(QuickAccessEntry entry)
    {
        string displayName = entry.DisplayName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        displayName = QuickAccessService.CreateDisplayName(entry.Path);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        string path = entry.Path?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(path) ? path : string.Empty;
    }
}
