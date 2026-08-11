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

}
