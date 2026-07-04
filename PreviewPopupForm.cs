using System.Runtime.InteropServices;

using System.ComponentModel;

namespace MidFD;

/// <summary>
/// フォーカスを奪わないポップアッププレビューウィンドウ。
/// WS_EX_NOACTIVATE + WM_MOUSEACTIVATE で表示・クリック時ともに非アクティブを維持しつつ、
/// ドラッグで移動可能（フォーカス非奪取のまま）。
/// </summary>
public class PreviewPopupForm : Form
{
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE  = 0x0021;
    private const int MA_NOACTIVATE     = 3;

    private readonly PictureBox _pictureBox;
    private readonly Label _messageLabel;

    // ─── ドラッグ移動 ──────────────────────────────────────────────

    private bool _dragging = false;
    private Point _dragStart;               // ドラッグ開始時のカーソル位置（スクリーン座標）
    private Point _formOrigin;              // ドラッグ開始時の Form の Location

    /// <summary>ユーザーが手動で移動したかどうか。true の間は自動配置を上書きしない。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public bool IsManuallyPositioned { get; set; } = false;

    // ─── コンストラクタ ──────────────────────────────────────────────

    public PreviewPopupForm()
    {
        // FormBorderStyle.None: タイトルバーを除去。V キーで開閉する前提なので閉じるボタンも不要。
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar   = false;
        this.Text            = "Preview";
        this.ClientSize      = new Size(400, 400);
        this.StartPosition   = FormStartPosition.Manual;
        this.BackColor       = Color.FromArgb(30, 30, 30);

        _messageLabel = new Label
        {
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Silver,
            BackColor = Color.FromArgb(30, 30, 30),
            Font      = new Font("Consolas", 10F),
            Text      = "No Preview",
        };

        _pictureBox = new PictureBox
        {
            Dock      = DockStyle.Fill,
            SizeMode  = PictureBoxSizeMode.Zoom,
            Visible   = false,
            BackColor = Color.FromArgb(30, 30, 30),
        };

        // 子コントロール上でもドラッグできるようにイベントを親へ転送
        AttachDragEvents(_pictureBox);
        AttachDragEvents(_messageLabel);

        this.Controls.Add(_pictureBox);
        this.Controls.Add(_messageLabel);

        // 本体自身のマウスイベント
        this.MouseDown += OnDragMouseDown;
        this.MouseMove += OnDragMouseMove;
        this.MouseUp   += OnDragMouseUp;
    }

    private void AttachDragEvents(Control c)
    {
        c.MouseDown += (s, e) => OnDragMouseDown(s, e);
        c.MouseMove += (s, e) =>
        {
            // 子コントロール上の座標をスクリーン座標に直して親のMoveロジックへ渡す
            var screenPt = c.PointToScreen(e.Location);
            var formPt   = this.PointToClient(screenPt);
            OnDragMouseMove(s, new MouseEventArgs(e.Button, e.Clicks, formPt.X, formPt.Y, e.Delta));
        };
        c.MouseUp += (s, e) => OnDragMouseUp(s, e);
    }

    private void OnDragMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging   = true;
        _dragStart  = Cursor.Position;        // スクリーン座標で記録
        _formOrigin = this.Location;
    }

    private void OnDragMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var delta = new Point(
            Cursor.Position.X - _dragStart.X,
            Cursor.Position.Y - _dragStart.Y);
        this.Location = new Point(_formOrigin.X + delta.X, _formOrigin.Y + delta.Y);
    }

    private void OnDragMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        // ドラッグした場合だけ「手動移動済み」とマークする（クリックのみは除外）
        var delta = new Point(
            Cursor.Position.X - _dragStart.X,
            Cursor.Position.Y - _dragStart.Y);
        if (Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4)
        {
            IsManuallyPositioned = true;
        }
    }

    // ─── 非アクティブ化 ────────────────────────────────────────────

    /// <summary>OS レベルで WS_EX_NOACTIVATE を付与する。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    /// <summary>WinForms レベルでもアクティブ化を抑制する。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>クリック時のアクティブ化を WndProc レベルで完全に抑制する。</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }
 
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        System.Diagnostics.Debug.WriteLine($"[PreviewPopup] VisibleChanged: {this.Visible}");
    }

    /// <summary>フォーカスを奪わずにウィンドウを表示する。WS_EX_NOACTIVATE があるため Visible=true でも非アクティブ表示になる。</summary>
    public void ShowWithoutFocus()
    {
        this.Visible = true;
    }

    // ─── プレビュー表示 ──────────────────────────────────────────────

    /// <summary>後方互換のため ShowPreview は ShowPreviewImage へのエイリアス。</summary>
    public void ShowPreview(Bitmap bmp) => ShowPreviewImage(bmp);

    /// <summary>
    /// オーナーウィンドウの前面に持ってくる。
    /// SetWindowPos を使い、SWP_NOACTIVATE を指定することでフォーカスを奪わずに前面へ移動する。
    /// </summary>
    public void BringToFrontOfOwner()
    {
        if (!this.Visible || !this.IsHandleCreated) return;

        const int HWND_TOPMOST = -1;
        const int HWND_NOTOPMOST = -2;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        // 一旦 TOPMOST にして前面に出し、すぐに NOTOPMOST に戻す（Zオーダーは維持される）
        SetWindowPos(this.Handle, (IntPtr)HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(this.Handle, (IntPtr)HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>画像を表示する。旧画像は自動的に Dispose する。</summary>
    public void ShowPreviewImage(Bitmap bmp)
    {
        var old = _pictureBox.Image;
        _pictureBox.Image     = bmp;
        _pictureBox.Visible   = true;
        _messageLabel.Visible = false;
        old?.Dispose();
        _pictureBox.BringToFront();
        _pictureBox.Invalidate();
        _pictureBox.Update();
        this.Invalidate();
        this.Update();
    }


    /// <summary>メッセージ（対象外・失敗・読み込み中等）を表示する。旧画像を Dispose する。</summary>
    public void ShowMessage(string message)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[PreviewPopup] ShowMessage: {message}");
#endif
        var old = _pictureBox.Image;
        _pictureBox.Image     = null;
        _pictureBox.Visible   = false;
        _messageLabel.Text    = message;
        _messageLabel.Visible = true;
        old?.Dispose();
        _messageLabel.BringToFront();
        this.Invalidate();
        this.Update();
    }

    /// <summary>プレビューをクリアして「No Preview」を表示する。</summary>
    public void Clear() => ShowMessage("No Preview");

    /// <summary>メッセージ用ラベルのフォントを設定する。</summary>
    public void SetMessageFont(Font font)
    {
        _messageLabel.Font = font;
    }

    // ─── ライフサイクル ──────────────────────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            this.Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pictureBox.Image?.Dispose();
        base.Dispose(disposing);
    }
}
