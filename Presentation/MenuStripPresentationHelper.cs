using System.Drawing;
using System.Windows.Forms;

namespace MidFD.Presentation;

public static class MenuStripPresentationHelper
{
    public static void ApplyRenderer(MenuStrip? menuStrip, bool isLightPalette, Color commandTextColor)
    {
        if (menuStrip == null)
        {
            return;
        }

        menuStrip.Renderer = new MenuIntegratedNavigationRenderer(isLightPalette, commandTextColor);
    }

    public static void SynchronizeFontAndLayout(
        MenuStrip? menuStrip,
        Font menuFont,
        bool isLightPalette,
        Color commandTextColor)
    {
        if (menuStrip == null)
        {
            return;
        }

        menuStrip.Renderer = new MenuIntegratedNavigationRenderer(isLightPalette, commandTextColor);
        menuStrip.SuspendLayout();
        try
        {
            var metrics = CalculateMenuStripMetrics(menuFont);
            menuStrip.AutoSize = false;
            menuStrip.Font = menuFont;
            menuStrip.Padding = metrics.Padding;
            menuStrip.Height = metrics.Height;
            foreach (ToolStripItem item in menuStrip.Items)
            {
                item.Font = menuFont;
                if (item is ToolStripMenuItem rootItem)
                {
                    ApplyRootMenuVisualMetrics(rootItem, menuFont);
                    ApplyToolStripItemFontAndLayout(rootItem, menuFont);
                }
            }
        }
        finally
        {
            menuStrip.ResumeLayout(true);
            menuStrip.PerformLayout();
            menuStrip.Invalidate();
        }
    }

    private sealed class MenuIntegratedNavigationColorTable : ProfessionalColorTable
    {
        private readonly bool _isLightPalette;

        public MenuIntegratedNavigationColorTable(bool isLightPalette)
        {
            _isLightPalette = isLightPalette;
        }

        public override Color MenuItemSelected => Color.FromArgb(40, 128, 128, 128);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(40, 128, 128, 128);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(40, 128, 128, 128);
        public override Color MenuItemBorder => Color.FromArgb(80, 128, 128, 128);
        public override Color MenuBorder => Color.FromArgb(80, 128, 128, 128);
        public override Color ToolStripDropDownBackground => _isLightPalette ? Color.FromArgb(248, 248, 248) : Color.WhiteSmoke;
    }

    private sealed class MenuIntegratedNavigationRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _isLightPalette;
        private readonly Color _commandTextColor;
        private readonly Color _commandAccentColor;

        public MenuIntegratedNavigationRenderer(bool isLightPalette, Color commandTextColor) : base(new MenuIntegratedNavigationColorTable(isLightPalette))
        {
            _isLightPalette = isLightPalette;
            _commandTextColor = commandTextColor;
            _commandAccentColor = isLightPalette
                ? Color.FromArgb(180, 180, 180)
                : Color.FromArgb(commandTextColor.R, commandTextColor.G, commandTextColor.B);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripButton btn)
            {
                base.OnRenderButtonBackground(e);
                return;
            }

            var rect = new Rectangle(Point.Empty, btn.Size);
            if (btn.Selected || btn.Pressed)
            {
                Color fillColor = _isLightPalette ? Color.FromArgb(224, 224, 224) : Color.FromArgb(0, 64, 64);
                Color borderColor = _isLightPalette ? Color.FromArgb(180, 180, 180) : _commandAccentColor;
                using (var brush = new SolidBrush(fillColor))
                {
                    e.Graphics.FillRectangle(brush, rect);
                }
                using (var pen = new Pen(borderColor))
                {
                    e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                }
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripMenuItem item)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            var rect = new Rectangle(Point.Empty, item.Size);
            if (item.IsOnDropDown)
            {
                if (item.Selected || item.Pressed)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(220, 235, 252)))
                    {
                        e.Graphics.FillRectangle(brush, rect);
                    }
                    using (var pen = new Pen(Color.FromArgb(180, 200, 240)))
                    {
                        e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                    }
                }
            }
            else if (item.Selected || item.Pressed)
            {
                Color fillColor = _isLightPalette ? Color.FromArgb(224, 224, 224) : Color.FromArgb(0, 64, 64);
                Color borderColor = _isLightPalette ? Color.FromArgb(180, 180, 180) : _commandAccentColor;
                using (var brush = new SolidBrush(fillColor))
                {
                    e.Graphics.FillRectangle(brush, rect);
                }
                using (var pen = new Pen(borderColor))
                {
                    e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is ToolStripMenuItem item)
            {
                e.TextColor = item.IsOnDropDown
                    ? item.Enabled ? Color.Black : Color.Gray
                    : item.Enabled ? (_isLightPalette ? Color.FromArgb(32, 32, 32) : _commandTextColor) : Color.Gray;
            }
            else if (e.Item is ToolStripButton btn)
            {
                e.TextColor = btn.Enabled
                    ? (_isLightPalette ? Color.FromArgb(32, 32, 32) : _commandTextColor)
                    : Color.Gray;
            }

            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            if (e.Vertical)
            {
                base.OnRenderSeparator(e);
                return;
            }

            var rect = new Rectangle(Point.Empty, e.Item.Size);
            using (var brush = new SolidBrush(Color.WhiteSmoke))
            {
                e.Graphics.FillRectangle(brush, rect);
            }
            int y = rect.Height / 2;
            using (var pen = new Pen(Color.FromArgb(200, 200, 200)))
            {
                e.Graphics.DrawLine(pen, 6, y, rect.Width - 6, y);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is MenuStrip)
            {
                return;
            }

            base.OnRenderToolStripBorder(e);
        }
    }

    private static (int Height, Padding Padding) CalculateMenuStripMetrics(Font menuFont)
    {
        return (28, new Padding(4, 1, 0, 1));
    }

    private static Padding CalculateRootMenuItemPadding(Font menuFont)
    {
        return new Padding(6, 4, 6, 4);
    }

    private static Padding CalculateDropDownItemPadding(Font menuFont)
    {
        int horizontal = Math.Max(8, (int)Math.Round(menuFont.SizeInPoints * 0.55f));
        int vertical = Math.Max(2, (int)Math.Round(menuFont.SizeInPoints / 10f));
        return new Padding(horizontal, vertical, horizontal, vertical);
    }

    private static Padding CalculateDropDownInnerPadding(Font menuFont)
    {
        int horizontal = Math.Max(1, (int)Math.Round(menuFont.SizeInPoints / 18f));
        return new Padding(horizontal, 1, horizontal, 1);
    }

    public static void ConfigureGlobalDropDownProperties(ToolStripDropDownItem item, Font? menuFont)
    {
        if (item.DropDown is ToolStripDropDownMenu menu)
        {
            menu.DropShadowEnabled = false;
            if (item.Name == "favoritesMenu" || item.Tag?.ToString() == "FavoriteCategory")
            {
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = false;
                menu.Padding = new Padding(2, 1, 2, 1);
            }
            else
            {
                menu.ShowImageMargin = false;
                menu.Padding = menuFont != null
                    ? CalculateDropDownInnerPadding(menuFont)
                    : new Padding(2, 1, 2, 1);
            }
        }
    }

    private static void ApplyRootMenuVisualMetrics(ToolStripMenuItem item, Font menuFont)
    {
        item.Margin = Padding.Empty;
        item.Padding = CalculateRootMenuItemPadding(menuFont);
        item.TextAlign = ContentAlignment.MiddleCenter;
        ConfigureGlobalDropDownProperties(item, menuFont);
    }

    private static bool IsFavoriteMenuItem(ToolStripItem item)
    {
        if (item == null) return false;
        if (item.Name == "favoritesMenu") return true;
        string? tag = item.Tag?.ToString();
        return tag == "FavoriteCategory" || tag == "FavoriteItem" || tag == "FavoriteActionItem";
    }

    private static void ApplyToolStripItemFontAndLayout(ToolStripItem item, Font menuFont)
    {
        item.Font = menuFont;
        if (item is not ToolStripDropDownItem dropDownItem)
        {
            return;
        }

        dropDownItem.DropDown.SuspendLayout();
        try
        {
            dropDownItem.DropDown.Font = menuFont;
            ConfigureGlobalDropDownProperties(dropDownItem, menuFont);
            foreach (ToolStripItem childItem in dropDownItem.DropDownItems)
            {
                ApplyDropDownItemVisualMetrics(childItem, menuFont);
                ApplyToolStripItemFontAndLayout(childItem, menuFont);
            }
        }
        finally
        {
            dropDownItem.DropDown.ResumeLayout(true);
            dropDownItem.DropDown.PerformLayout();
            dropDownItem.DropDown.Invalidate();
        }
    }

    private static void ApplyDropDownItemVisualMetrics(ToolStripItem item, Font menuFont)
    {
        if (item is ToolStripSeparator)
        {
            return;
        }

        item.Margin = Padding.Empty;
        if (item is ToolStripMenuItem menuItem)
        {
            menuItem.Padding = IsFavoriteMenuItem(menuItem)
                ? new Padding(4, 2, 4, 2)
                : CalculateDropDownItemPadding(menuFont);
            menuItem.TextAlign = ContentAlignment.MiddleLeft;
        }
    }
}
