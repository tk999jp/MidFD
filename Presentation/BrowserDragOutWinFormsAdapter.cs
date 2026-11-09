using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Presentation;

internal static class BrowserDragOutWinFormsAdapter
{
    public static BrowserDragMouseButton ToCommandMouseButton(MouseButtons button) => button switch
    {
        MouseButtons.Left => BrowserDragMouseButton.Left,
        MouseButtons.Right => BrowserDragMouseButton.Right,
        _ => BrowserDragMouseButton.None
    };

    public static BrowserDragModifiers ToCommandModifiers(Keys modifiers)
    {
        BrowserDragModifiers result = BrowserDragModifiers.None;
        if ((modifiers & Keys.Shift) != 0) result |= BrowserDragModifiers.Shift;
        if ((modifiers & Keys.Control) != 0) result |= BrowserDragModifiers.Control;
        if ((modifiers & Keys.Alt) != 0) result |= BrowserDragModifiers.Alt;
        return result;
    }

    public static MouseButtons ToWinFormsMouseButton(BrowserDragMouseButton button) => button switch
    {
        BrowserDragMouseButton.Left => MouseButtons.Left,
        BrowserDragMouseButton.Right => MouseButtons.Right,
        _ => MouseButtons.None
    };

    public static DragDropEffects ToWinFormsEffects(BrowserDragEffect effects)
    {
        DragDropEffects result = DragDropEffects.None;
        if ((effects & BrowserDragEffect.Copy) != 0) result |= DragDropEffects.Copy;
        if ((effects & BrowserDragEffect.Move) != 0) result |= DragDropEffects.Move;
        if ((effects & BrowserDragEffect.Link) != 0) result |= DragDropEffects.Link;
        return result;
    }
}
