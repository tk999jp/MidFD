using System.Drawing;
using System.Windows.Forms;
using System.Threading;

namespace MidFD.Presentation;

internal enum BrowserDropAction
{
    Cancel,
    Copy,
    Move
}

internal static class BrowserDropActionMenuPresenter
{
    public static BrowserDropAction Show(IWin32Window owner, Point screenPoint)
    {
        BrowserDropAction result = BrowserDropAction.Cancel;
        using var menu = new ContextMenuStrip();
        using var waitHandle = new ManualResetEventSlim(false);

        void Complete(BrowserDropAction action)
        {
            result = action;
            waitHandle.Set();
            menu.Close();
        }

        menu.Items.Add("ここにコピー", null, (_, _) => Complete(BrowserDropAction.Copy));
        menu.Items.Add("ここに移動", null, (_, _) => Complete(BrowserDropAction.Move));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("キャンセル", null, (_, _) => Complete(BrowserDropAction.Cancel));
        menu.Closed += (_, _) => waitHandle.Set();

        menu.Show(screenPoint);
        while (!waitHandle.IsSet)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        return result;
    }
}
