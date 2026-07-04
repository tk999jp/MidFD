using System;
using System.Windows.Forms;
using MidFD;
using MidFD.Helpers;

namespace MidFD.Coordinators;

public class BrowserTabContextMenuCoordinator
{
    public ContextMenuStrip? TabContextMenu { get; set; }
    public int TabContextIndex { get; set; } = -1;

    public ContextMenuStrip? CategoryContextMenu { get; set; }
    public string? CategoryContextCategoryId { get; set; }
    public BrowserTabStripCategoryItemKind CategoryContextKind { get; set; } = BrowserTabStripCategoryItemKind.Category;

    public void CloseAll()
    {
        TabContextMenu?.Close();
        CategoryContextMenu?.Close();
    }

    public void Reset()
    {
        TabContextIndex = -1;
        CategoryContextCategoryId = null;
        CategoryContextKind = BrowserTabStripCategoryItemKind.Category;
    }
}
