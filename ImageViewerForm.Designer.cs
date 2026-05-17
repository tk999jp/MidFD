namespace MidFD;

partial class ImageViewerForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();
        this.imageScrollPanel = new System.Windows.Forms.Panel();
        this.pictureBox1 = new System.Windows.Forms.PictureBox();
        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
        this.menuFile = new System.Windows.Forms.ToolStripMenuItem();
        this.menuOpen = new System.Windows.Forms.ToolStripMenuItem();
        this.menuSaveAs = new System.Windows.Forms.ToolStripMenuItem();
        this.menuClose = new System.Windows.Forms.ToolStripMenuItem();
        this.menuEdit = new System.Windows.Forms.ToolStripMenuItem();
        this.menuCopy = new System.Windows.Forms.ToolStripMenuItem();
        this.menuPaste = new System.Windows.Forms.ToolStripMenuItem();
        this.statusStrip1 = new System.Windows.Forms.StatusStrip();
        this.statusLabel = new System.Windows.Forms.ToolStripStatusLabel();
        this.imageScrollPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
        this.menuStrip1.SuspendLayout();
        this.statusStrip1.SuspendLayout();
        this.SuspendLayout();
        //
        // imageScrollPanel
        //
        this.imageScrollPanel.AutoScroll = true;
        this.imageScrollPanel.Controls.Add(this.pictureBox1);
        this.imageScrollPanel.Dock = System.Windows.Forms.DockStyle.Fill;
        this.imageScrollPanel.Location = new System.Drawing.Point(0, 24);
        this.imageScrollPanel.Name = "imageScrollPanel";
        this.imageScrollPanel.Size = new System.Drawing.Size(800, 426);
        this.imageScrollPanel.TabIndex = 2;
        //
        // pictureBox1
        //
        this.pictureBox1.Location = new System.Drawing.Point(0, 0);
        this.pictureBox1.Name = "pictureBox1";
        this.pictureBox1.Size = new System.Drawing.Size(640, 360);
        this.pictureBox1.SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage;
        this.pictureBox1.TabIndex = 0;
        this.pictureBox1.TabStop = false;
        this.pictureBox1.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
        //
        // menuStrip1
        //
        this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
        this.menuFile,
        this.menuEdit});
        this.menuStrip1.Location = new System.Drawing.Point(0, 0);
        this.menuStrip1.Name = "menuStrip1";
        this.menuStrip1.Size = new System.Drawing.Size(800, 24);
        this.menuStrip1.TabIndex = 1;
        this.menuStrip1.Text = "menuStrip1";
        //
        // menuFile
        //
        this.menuFile.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
        this.menuOpen,
        this.menuSaveAs,
        this.menuClose});
        this.menuFile.Name = "menuFile";
        this.menuFile.Size = new System.Drawing.Size(37, 20);
        this.menuFile.Text = "ファイル(&F)";
        //
        // menuOpen
        //
        this.menuOpen.Name = "menuOpen";
        this.menuOpen.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.O)));
        this.menuOpen.Size = new System.Drawing.Size(204, 22);
        this.menuOpen.Text = "開く(&O)";
        this.menuOpen.Click += new System.EventHandler(this.menuOpen_Click);
        //
        // menuSaveAs
        //
        this.menuSaveAs.Name = "menuSaveAs";
        this.menuSaveAs.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.S)));
        this.menuSaveAs.Size = new System.Drawing.Size(204, 22);
        this.menuSaveAs.Text = "名前をつけて保存(&A)";
        this.menuSaveAs.Click += new System.EventHandler(this.menuSaveAs_Click);
        //
        // menuClose
        //
        this.menuClose.Name = "menuClose";
        this.menuClose.Size = new System.Drawing.Size(204, 22);
        this.menuClose.Text = "閉じる(&C)";
        this.menuClose.Click += new System.EventHandler(this.menuClose_Click);
        //
        // menuEdit
        //
        this.menuEdit.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
        this.menuCopy,
        this.menuPaste});
        this.menuEdit.Name = "menuEdit";
        this.menuEdit.Size = new System.Drawing.Size(39, 20);
        this.menuEdit.Text = "編集(&E)";
        //
        // menuCopy
        //
        this.menuCopy.Name = "menuCopy";
        this.menuCopy.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.C)));
        this.menuCopy.Size = new System.Drawing.Size(180, 22);
        this.menuCopy.Text = "画像をコピー(&C)";
        this.menuCopy.Click += new System.EventHandler(this.menuCopy_Click);
        //
        // menuPaste
        //
        this.menuPaste.Name = "menuPaste";
        this.menuPaste.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.V)));
        this.menuPaste.Size = new System.Drawing.Size(180, 22);
        this.menuPaste.Text = "画像を貼り付け(&V)";
        this.menuPaste.Click += new System.EventHandler(this.menuPaste_Click);
        //
        // statusStrip1
        //
        this.statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
        this.statusLabel});
        this.statusStrip1.Location = new System.Drawing.Point(0, 428);
        this.statusStrip1.Name = "statusStrip1";
        this.statusStrip1.Size = new System.Drawing.Size(800, 22);
        this.statusStrip1.TabIndex = 3;
        //
        // statusLabel
        //
        this.statusLabel.Name = "statusLabel";
        this.statusLabel.Size = new System.Drawing.Size(39, 17);
        this.statusLabel.Text = "倍率 0%";
        //
        // ImageViewerForm
        //
        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(800, 450);
        this.Controls.Add(this.imageScrollPanel);
        this.Controls.Add(this.statusStrip1);
        this.Controls.Add(this.menuStrip1);
        this.MainMenuStrip = this.menuStrip1;
        this.Name = "ImageViewerForm";
        this.Text = "MidFD Image Viewer";
        this.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
        this.imageScrollPanel.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
        this.menuStrip1.ResumeLayout(false);
        this.menuStrip1.PerformLayout();
        this.statusStrip1.ResumeLayout(false);
        this.statusStrip1.PerformLayout();
        this.ResumeLayout(false);
        this.PerformLayout();
    }

    private System.Windows.Forms.Panel imageScrollPanel;
    private System.Windows.Forms.PictureBox pictureBox1;
    private System.Windows.Forms.MenuStrip menuStrip1;
    private System.Windows.Forms.ToolStripMenuItem menuFile;
    private System.Windows.Forms.ToolStripMenuItem menuOpen;
    private System.Windows.Forms.ToolStripMenuItem menuSaveAs;
    private System.Windows.Forms.ToolStripMenuItem menuClose;
    private System.Windows.Forms.ToolStripMenuItem menuEdit;
    private System.Windows.Forms.ToolStripMenuItem menuCopy;
    private System.Windows.Forms.ToolStripMenuItem menuPaste;
    private System.Windows.Forms.StatusStrip statusStrip1;
    private System.Windows.Forms.ToolStripStatusLabel statusLabel;
}
