using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Presentation;

public static class BrowserTabContextMenuPresenter
{
    public static void ApplyCategoryState(
        BrowserTabCategoryContextMenuState state,
        ToolStripMenuItem? moveLeftItem,
        ToolStripMenuItem? moveRightItem,
        ToolStripMenuItem? renameItem,
        ToolStripMenuItem? deleteItem,
        ContextMenuStrip? menu)
    {
        if (moveLeftItem != null)
        {
            moveLeftItem.Visible = state.HasTargetCategory;
            moveLeftItem.Enabled = state.CanMoveLeft;
        }
        if (moveRightItem != null)
        {
            moveRightItem.Visible = state.HasTargetCategory;
            moveRightItem.Enabled = state.CanMoveRight;
        }
        if (renameItem != null)
        {
            renameItem.Visible = state.HasTargetCategory;
            renameItem.Enabled = state.HasTargetCategory;
        }
        if (deleteItem != null)
        {
            deleteItem.Visible = state.HasTargetCategory;
            deleteItem.Enabled = state.HasTargetCategory;
        }
        if (menu != null && menu.Items.Count >= 7)
        {
            menu.Items[1].Visible = state.HasTargetCategory;
            menu.Items[6].Visible = state.HasTargetCategory;
        }
    }

    public static void ApplyTabState(
        BrowserTabContextMenuState state,
        ToolStripMenuItem? toggleLockItem,
        ToolStripMenuItem? toggleReadOnlyItem,
        ToolStripMenuItem? clearFilterLockItem,
        ToolStripMenuItem? closeItem,
        ToolStripMenuItem? closeRightItem,
        ToolStripMenuItem? closeLeftItem,
        ToolStripMenuItem? closeOtherItem)
    {
        if (toggleLockItem != null)
        {
            toggleLockItem.Text = state.IsLocked
                ? "このタブの固定を解除"
                : "このタブを固定";
        }
        if (toggleReadOnlyItem != null)
        {
            toggleReadOnlyItem.Text = state.IsReadOnly
                ? "このタブの ReadOnly を解除"
                : "このタブを ReadOnly にする";
        }
        if (clearFilterLockItem != null)
        {
            clearFilterLockItem.Enabled = state.CanClearFilterLock;
        }
        if (closeItem != null)
        {
            closeItem.Text = state.IsLocked
                ? "このタブを閉じる（固定中は不可）"
                : "このタブを閉じる";
        }
        if (closeRightItem != null)
        {
            closeRightItem.Enabled = state.CanCloseRight;
        }
        if (closeLeftItem != null)
        {
            closeLeftItem.Enabled = state.CanCloseLeft;
        }
        if (closeOtherItem != null)
        {
            closeOtherItem.Enabled = state.CanCloseOther;
        }
    }
}
