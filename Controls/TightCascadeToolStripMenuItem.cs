using System;
using System.Drawing;
using System.Windows.Forms;

namespace MidFD.Controls
{
    public class TightCascadeToolStripMenuItem : ToolStripMenuItem
    {
        public TightCascadeToolStripMenuItem() : base()
        {
        }

        public TightCascadeToolStripMenuItem(string text) : base(text)
        {
        }

        public TightCascadeToolStripMenuItem(string text, Image image) : base(text, image)
        {
        }

        public TightCascadeToolStripMenuItem(string text, Image image, EventHandler onClick) : base(text, image, onClick)
        {
        }

        public TightCascadeToolStripMenuItem(string text, Image image, params ToolStripItem[] dropDownItems) : base(text, image, dropDownItems)
        {
        }

        public TightCascadeToolStripMenuItem(string text, Image image, EventHandler onClick, string name) : base(text, image, onClick, name)
        {
        }

        protected override Point DropDownLocation
        {
            get
            {
                Point p = base.DropDownLocation;
                if (Parent is ToolStripDropDownMenu parentMenu)
                {
                    Rectangle parentBounds = parentMenu.Bounds;
                    int parentRight = parentBounds.Right;
                    int parentLeft = parentBounds.Left;
                    int parentCenter = parentLeft + parentBounds.Width / 2;

                    // Detect whether the submenu is cascading to the right or left relative to the parent menu.
                    if (p.X >= parentCenter)
                    {
                        // Cascade to the right: snap left edge of child to right edge of parent.
                        // Overlap by 1px to cover borders.
                        p.X = parentRight - 1;
                    }
                    else
                    {
                        // Cascade to the left: snap right edge of child to left edge of parent.
                        // DropDown.PreferredSize.Width is used to compute the snapping coordinate.
                        int childWidth = DropDown.PreferredSize.Width;
                        p.X = parentLeft - childWidth + 1;
                    }
                }
                return p;
            }
        }
    }
}
