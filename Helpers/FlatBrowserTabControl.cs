using System.Drawing;
using System.Windows.Forms;

using System.ComponentModel;

namespace MidFD.Helpers;

public sealed class FlatBrowserTabControl : TabControl
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color ActiveTabBackColor { get; set; } = Color.FromArgb(0, 64, 80);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color InactiveTabBackColor { get; set; } = Color.Black;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color TabBorderColor { get; set; } = Color.Cyan;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color ActiveTabTextColor { get; set; } = Color.Yellow;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color InactiveTabTextColor { get; set; } = Color.Cyan;

    public FlatBrowserTabControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        DrawMode = TabDrawMode.OwnerDrawFixed;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);

        if (TabPages.Count == 0)
        {
            return;
        }

        int baselineY = GetTabRect(0).Bottom - 1;
        using (var baselinePen = new Pen(TabBorderColor))
        {
            e.Graphics.DrawLine(baselinePen, 0, baselineY, Width - 1, baselineY);
        }

        for (int i = 0; i < TabPages.Count; i++)
        {
            DrawTab(e.Graphics, i, baselineY);
        }
    }

    private void DrawTab(Graphics graphics, int index, int baselineY)
    {
        Rectangle bounds = GetTabRect(index);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        bool isSelected = index == SelectedIndex;
        Rectangle fillBounds = new(bounds.X, bounds.Y + 1, bounds.Width, bounds.Height - 2);
        Color backgroundColor = isSelected ? ActiveTabBackColor : InactiveTabBackColor;
        Color textColor = isSelected ? ActiveTabTextColor : InactiveTabTextColor;

        using SolidBrush backBrush = new(backgroundColor);
        graphics.FillRectangle(backBrush, fillBounds);

        if (isSelected)
        {
            using Pen borderPen = new(TabBorderColor);
            // 上・左・右だけ描画し、下辺は閉じない
            graphics.DrawLine(borderPen, fillBounds.Left, fillBounds.Top, fillBounds.Right - 1, fillBounds.Top);
            graphics.DrawLine(borderPen, fillBounds.Left, fillBounds.Top, fillBounds.Left, baselineY - 1);
            graphics.DrawLine(borderPen, fillBounds.Right - 1, fillBounds.Top, fillBounds.Right - 1, baselineY - 1);

            // ベースラインを選択タブ部分だけ切る
            using Pen coverPen = new(backgroundColor);
            graphics.DrawLine(coverPen, fillBounds.Left + 1, baselineY, fillBounds.Right - 2, baselineY);
        }
        else
        {
            using Pen separatorPen = new(Color.FromArgb(96, TabBorderColor));
            graphics.DrawLine(separatorPen, fillBounds.Right - 1, fillBounds.Top + 4, fillBounds.Right - 1, baselineY - 4);
        }

        Rectangle textBounds = Rectangle.Inflate(fillBounds, -10, -2);
        TextRenderer.DrawText(
            graphics,
            TabPages[index].Text,
            Font,
            textBounds,
            textColor,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
