using System.Drawing;
using System.Windows.Forms;
using MidFD.Controls;

namespace MidFD.Presentation;

public static class PreviewUiPresenter
{
    public static void ApplyViewerChromeState(
        bool compactViewer,
        bool showLargeTextControl,
        Control titleHeaderPanel,
        Control headerPanel,
        Control sepBeforeTopPanel,
        Control topPanel,
        LargeFilePreviewControl? largeFileControl)
    {
        titleHeaderPanel.Visible = !compactViewer;
        headerPanel.Visible = !compactViewer;
        sepBeforeTopPanel.Visible = !compactViewer;
        topPanel.Visible = !compactViewer;
        if (largeFileControl != null)
        {
            largeFileControl.Visible = showLargeTextControl;
        }
    }

    public static void EnsureStatusBarVisible(StatusStrip? statusStrip, ToolStripStatusLabel? statusLabel)
    {
        if (statusStrip == null || statusStrip.IsDisposed)
        {
            return;
        }
        statusStrip.Visible = true;
        if (statusLabel != null)
        {
            statusLabel.Visible = true;
        }
    }

    public static void PositionPreviewPopup(Form owner, PreviewPopupForm previewPopup)
    {
        if (!owner.IsHandleCreated)
        {
            return;
        }

        if (previewPopup.IsManuallyPositioned)
        {
            Rectangle currentScreen = Screen.FromControl(previewPopup).WorkingArea;
            if (!currentScreen.IntersectsWith(previewPopup.Bounds))
            {
                previewPopup.IsManuallyPositioned = false;
            }
            else
            {
                return;
            }
        }

        Rectangle screen = Screen.FromControl(owner).WorkingArea;
        const int popupW = 400;
        const int popupH = 400;
        int x = owner.Right + 4;
        int y = owner.Top;

        if (x + popupW > screen.Right)
        {
            x = owner.Left - popupW - 4;
        }
        if (x < screen.Left)
        {
            x = screen.Left;
        }
        if (y + popupH > screen.Bottom)
        {
            y = screen.Bottom - popupH;
        }
        if (y < screen.Top)
        {
            y = screen.Top;
        }

        previewPopup.SetBounds(x, y, popupW, popupH);
    }
}
