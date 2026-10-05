using System.Windows.Forms;

namespace MidFD.Controls;

#pragma warning disable WFO5003 // .NET 10の標準async Drop target契約を利用する。
internal sealed class AsyncDropPanel : Panel, IAsyncDropTarget
{
    public event DragEventHandler? AsyncDragDrop;

    public void OnAsyncDragDrop(DragEventArgs e)
        => AsyncDragDrop?.Invoke(this, e);
}
#pragma warning restore WFO5003
