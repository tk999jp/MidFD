using System.Runtime.InteropServices;

namespace MidFD.Helpers;

internal sealed class DirectoryPathCompletionPopupForm : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private readonly Control _hostedControl;

    public DirectoryPathCompletionPopupForm(Control hostedControl)
    {
        _hostedControl = hostedControl;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Padding = Padding.Empty;
        Margin = Padding.Empty;
        BackColor = Color.FromArgb(40, 40, 40);
        DoubleBuffered = true;

        hostedControl.Dock = DockStyle.Fill;
        Controls.Add(hostedControl);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    public bool HasPopupFocus
    {
        get
        {
            IntPtr focusedHandle = GetFocus();
            return ContainsOwnedHandle(focusedHandle);
        }
    }

    public void ShowPopup(Form? owner, Rectangle bounds)
    {
        Bounds = bounds;
        if (!Visible)
        {
            if (owner != null)
            {
                Show(owner);
            }
            else
            {
                Show();
            }
        }
        else
        {
            SetBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }

    public bool ContainsOwnedHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsHandleCreated)
        {
            return false;
        }

        return handle == Handle || IsChild(Handle, handle) || (_hostedControl.IsHandleCreated && (handle == _hostedControl.Handle || IsChild(_hostedControl.Handle, handle)));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
}
