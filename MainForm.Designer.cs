namespace MidFD;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    private System.Windows.Forms.Panel outerHostPanel;
    private System.Windows.Forms.Panel mainAreaPanel;
    private System.Windows.Forms.Panel contentFramePanel;
    private System.Windows.Forms.Panel titleHeaderPanel;
    private System.Windows.Forms.Label lblTitle;
    private System.Windows.Forms.Label lblClock;
    private System.Windows.Forms.Panel headerPanel;
    private System.Windows.Forms.Panel headerZone1;
    private System.Windows.Forms.Panel headerZone2;
    private System.Windows.Forms.Panel headerZone3;
    private System.Windows.Forms.Panel headerZone4;
    private System.Windows.Forms.Label lblPage;
    private System.Windows.Forms.Label lblTotal;
    private System.Windows.Forms.Label lblUsed;
    private System.Windows.Forms.Label lblFree;
    private System.Windows.Forms.Panel topPanel;
    // WinFD風3行情報欄
    private System.Windows.Forms.Panel infoRow2Panel;
    private System.Windows.Forms.Panel infoRow3Panel;
    private System.Windows.Forms.Panel infoRow4Panel;
    private System.Windows.Forms.Panel sepBeforeTopPanel;
    private System.Windows.Forms.Panel sepAfterRow2;
    private System.Windows.Forms.Panel sepAfterRow3;
    private System.Windows.Forms.Panel sepAfterRow4;
    private System.Windows.Forms.Label lblPath;
    private System.Windows.Forms.Label lblSort;
    private System.Windows.Forms.Label lblItemAttr;
    private System.Windows.Forms.Label lblFileDate;
    private System.Windows.Forms.Label lblFileStats;
    private System.Windows.Forms.Label lblFileStatsEx;
    private System.Windows.Forms.Label lblName;
    
    // 多列表示用パネルと裏側のListView
    private System.Windows.Forms.Panel browserPanel;
    private System.Windows.Forms.ListView fileListView;
    private System.Windows.Forms.MenuStrip mainMenuStrip;
    private System.Windows.Forms.StatusStrip statusStrip;
    private System.Windows.Forms.ToolStripStatusLabel statusLabel;
    private System.Windows.Forms.Timer messageTimer;

    // FunctionBar
    private System.Windows.Forms.Panel functionBarPanel;
    private System.Windows.Forms.Label[] lblFuncKeys;

    // Viewer Mode UI
    private System.Windows.Forms.Panel viewerPanel;
    private System.Windows.Forms.RichTextBox viewerTextBox;
    private System.Windows.Forms.PictureBox viewerPictureBox;
    private System.Windows.Forms.Label viewerMessageLabel;

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
        components = new System.ComponentModel.Container();
        outerHostPanel = new Panel();
        mainAreaPanel = new Panel();
        contentFramePanel = new Panel();
        browserPanel = new Panel();
        viewerPanel = new Panel();
        viewerTextBox = new RichTextBox();
        viewerPictureBox = new PictureBox();
        viewerMessageLabel = new Label();
        mainMenuStrip = new MenuStrip();
        topPanel = new Panel();
        sepAfterRow4 = new Panel();
        infoRow4Panel = new Panel();
        lblFileStatsEx = new Label();
        lblName = new Label();
        sepAfterRow3 = new Panel();
        infoRow3Panel = new Panel();
        lblFileStats = new Label();
        lblFileDate = new Label();
        lblItemAttr = new Label();
        lblSort = new Label();
        sepAfterRow2 = new Panel();
        infoRow2Panel = new Panel();
        lblPath = new Label();
        sepBeforeTopPanel = new Panel();
        headerPanel = new Panel();
        headerZone4 = new Panel();
        lblFree = new Label();
        headerZone3 = new Panel();
        lblUsed = new Label();
        headerZone2 = new Panel();
        lblTotal = new Label();
        headerZone1 = new Panel();
        lblPage = new Label();
        titleHeaderPanel = new Panel();
        lblTitle = new Label();
        lblClock = new Label();
        fileListView = new ListView();
        statusStrip = new StatusStrip();
        statusLabel = new ToolStripStatusLabel();
        messageTimer = new System.Windows.Forms.Timer(components);

        functionBarPanel = new Panel();
        lblFuncKeys = new Label[12];
        for (int i = 0; i < 12; i++)
        {
            lblFuncKeys[i] = new Label();
        }

        outerHostPanel.SuspendLayout();
        mainMenuStrip.SuspendLayout();
        contentFramePanel.SuspendLayout();
        viewerPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)viewerPictureBox).BeginInit();
        topPanel.SuspendLayout();
        infoRow4Panel.SuspendLayout();
        infoRow3Panel.SuspendLayout();
        infoRow2Panel.SuspendLayout();
        headerPanel.SuspendLayout();
        headerZone4.SuspendLayout();
        headerZone3.SuspendLayout();
        headerZone2.SuspendLayout();
        headerZone1.SuspendLayout();
        titleHeaderPanel.SuspendLayout();
        statusStrip.SuspendLayout();
        SuspendLayout();
        // 
        // outerHostPanel
        // 
        outerHostPanel.BackColor = Color.Black;
        outerHostPanel.Controls.Add(contentFramePanel);
        outerHostPanel.Dock = DockStyle.Fill;
        outerHostPanel.Location = new Point(0, 24);
        outerHostPanel.Name = "outerHostPanel";
        outerHostPanel.Padding = new Padding(8, 0, 8, 0);
        outerHostPanel.Size = new Size(800, 550);
        outerHostPanel.TabIndex = 0;
        // 
        // mainMenuStrip
        // 
        mainMenuStrip.BackColor = Color.Black;
        mainMenuStrip.Dock = DockStyle.Top;
        mainMenuStrip.ForeColor = Color.Cyan;
        mainMenuStrip.GripStyle = ToolStripGripStyle.Hidden;
        mainMenuStrip.ImageScalingSize = new Size(20, 20);
        mainMenuStrip.Location = new Point(0, 0);
        mainMenuStrip.Name = "mainMenuStrip";
        mainMenuStrip.Padding = new Padding(4, 1, 0, 1);
        mainMenuStrip.Size = new Size(800, 24);
        mainMenuStrip.TabIndex = 1;
        mainMenuStrip.Text = "mainMenuStrip";
        // 
        // mainAreaPanel
        // 
        mainAreaPanel.BackColor = Color.Black;
        mainAreaPanel.Controls.Add(browserPanel);
        mainAreaPanel.Controls.Add(viewerPanel);
        mainAreaPanel.Dock = DockStyle.Fill;
        mainAreaPanel.Location = new Point(0, 0);
        mainAreaPanel.Name = "mainAreaPanel";
        mainAreaPanel.Size = new Size(784, 414); // 初期サイズは適当で可。Dock.Fill で上書きされる
        mainAreaPanel.TabIndex = 0;

        // 
        // contentFramePanel
        // 
        contentFramePanel.BackColor = Color.Black;
        contentFramePanel.Controls.Add(mainAreaPanel);
        contentFramePanel.Controls.Add(functionBarPanel);
        contentFramePanel.Controls.Add(topPanel);
        contentFramePanel.Controls.Add(sepBeforeTopPanel);
        contentFramePanel.Controls.Add(headerPanel);
        contentFramePanel.Controls.Add(titleHeaderPanel);
        contentFramePanel.Dock = DockStyle.Fill;
        contentFramePanel.Location = new Point(8, 0);
        contentFramePanel.Name = "contentFramePanel";
        contentFramePanel.Padding = new Padding(1, 2, 1, 1);
        contentFramePanel.Size = new Size(784, 546);
        contentFramePanel.TabIndex = 0;
        contentFramePanel.Paint += contentFramePanel_Paint;
        // 
        // browserPanel
        // 
        browserPanel.BackColor = Color.Black;
        browserPanel.Dock = DockStyle.Fill;
        browserPanel.Font = new Font("Consolas", 11F);
        browserPanel.ForeColor = Color.Cyan;
        browserPanel.Location = new Point(1, 112);
        browserPanel.Name = "browserPanel";
        browserPanel.Size = new Size(782, 453);
        browserPanel.TabIndex = 0;
        browserPanel.Paint += BrowserPanel_Paint;
        // 
        // viewerPanel
        // 
        viewerPanel.BackColor = Color.FromArgb(20, 20, 20);
        viewerPanel.Controls.Add(viewerTextBox);
        viewerPanel.Controls.Add(viewerPictureBox);
        viewerPanel.Controls.Add(viewerMessageLabel);
        viewerPanel.Dock = DockStyle.Fill;
        viewerPanel.Location = new Point(1, 112);
        viewerPanel.Name = "viewerPanel";
        viewerPanel.Size = new Size(782, 453);
        viewerPanel.TabIndex = 1;
        viewerPanel.Visible = false;
        // 
        // viewerTextBox
        // 
        viewerTextBox.BackColor = Color.FromArgb(25, 25, 25);
        viewerTextBox.BorderStyle = BorderStyle.None;
        viewerTextBox.Dock = DockStyle.Fill;
        viewerTextBox.Font = new Font("Consolas", 10F);
        viewerTextBox.ForeColor = Color.FromArgb(220, 220, 220);
        viewerTextBox.Location = new Point(0, 0);
        viewerTextBox.Name = "viewerTextBox";
        viewerTextBox.ReadOnly = true;
        viewerTextBox.Size = new Size(782, 453);
        viewerTextBox.TabIndex = 0;
        viewerTextBox.Text = "";
        viewerTextBox.Visible = false;
        viewerTextBox.WordWrap = false;
        // 
        // viewerPictureBox
        // 
        viewerPictureBox.BackColor = Color.FromArgb(20, 20, 20);
        viewerPictureBox.Dock = DockStyle.Fill;
        viewerPictureBox.Location = new Point(0, 0);
        viewerPictureBox.Name = "viewerPictureBox";
        viewerPictureBox.Size = new Size(782, 453);
        viewerPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        viewerPictureBox.TabIndex = 1;
        viewerPictureBox.TabStop = false;
        viewerPictureBox.Visible = false;
        // 
        // viewerMessageLabel
        // 
        viewerMessageLabel.Dock = DockStyle.Fill;
        viewerMessageLabel.Font = new Font("Consolas", 12F);
        viewerMessageLabel.ForeColor = Color.Gray;
        viewerMessageLabel.Location = new Point(0, 0);
        viewerMessageLabel.Name = "viewerMessageLabel";
        viewerMessageLabel.Size = new Size(782, 453);
        viewerMessageLabel.TabIndex = 2;
        viewerMessageLabel.Text = "No Preview Available";
        viewerMessageLabel.TextAlign = ContentAlignment.MiddleCenter;
        viewerMessageLabel.Visible = false;
        // 
        // functionBarPanel
        // 
        functionBarPanel.BackColor = Color.Black;
        functionBarPanel.Dock = DockStyle.Bottom;
        functionBarPanel.Height = 24;
        functionBarPanel.Name = "functionBarPanel";
        // Phase 5-ui-layout-fix2: Paint ベース描画へ切り替え。LabelによるSetBoundsは廃止。
        functionBarPanel.Paint += FunctionBarPanel_Paint;
        functionBarPanel.Resize += (s, e) => functionBarPanel.Invalidate();

        // 
        // topPanel
        // 
        topPanel.BackColor = Color.Black;
        topPanel.Controls.Add(sepAfterRow4);
        topPanel.Controls.Add(infoRow4Panel);
        topPanel.Controls.Add(sepAfterRow3);
        topPanel.Controls.Add(infoRow3Panel);
        topPanel.Controls.Add(sepAfterRow2);
        topPanel.Controls.Add(infoRow2Panel);
        topPanel.Dock = DockStyle.Top;
        topPanel.Location = new Point(1, 50);
        topPanel.Name = "topPanel";
        topPanel.Size = new Size(782, 62);
        topPanel.TabIndex = 2;
        // 
        // sepAfterRow4
        // 
        sepAfterRow4.BackColor = Color.FromArgb(80, 80, 80);
        sepAfterRow4.Dock = DockStyle.Top;
        sepAfterRow4.Location = new Point(0, 62);
        sepAfterRow4.Name = "sepAfterRow4";
        sepAfterRow4.Size = new Size(782, 1);
        sepAfterRow4.TabIndex = 0;
        // 
        // infoRow4Panel
        // 
        infoRow4Panel.BackColor = Color.Black;
        infoRow4Panel.Controls.Add(lblFileStatsEx);
        infoRow4Panel.Controls.Add(lblName);
        infoRow4Panel.Dock = DockStyle.Top;
        infoRow4Panel.Location = new Point(0, 42);
        infoRow4Panel.Name = "infoRow4Panel";
        infoRow4Panel.Size = new Size(782, 20);
        infoRow4Panel.TabIndex = 1;
        // 
        // lblFileStatsEx
        // 
        lblFileStatsEx.AutoSize = true;
        lblFileStatsEx.Dock = DockStyle.Right;
        lblFileStatsEx.Font = new Font("Consolas", 10F);
        lblFileStatsEx.ForeColor = Color.Yellow;
        lblFileStatsEx.Location = new Point(778, 0);
        lblFileStatsEx.Name = "lblFileStatsEx";
        lblFileStatsEx.Padding = new Padding(0, 0, 4, 0);
        lblFileStatsEx.Size = new Size(4, 20);
        lblFileStatsEx.TabIndex = 0;
        lblFileStatsEx.TextAlign = ContentAlignment.MiddleRight;
        // 
        // lblName
        // 
        lblName.Dock = DockStyle.Fill;
        lblName.Font = new Font("Consolas", 10F);
        lblName.ForeColor = Color.LightCyan;
        lblName.Location = new Point(0, 0);
        lblName.Name = "lblName";
        lblName.Padding = new Padding(4, 0, 0, 0);
        lblName.Size = new Size(782, 20);
        lblName.TabIndex = 1;
        lblName.Text = "Name=";
        lblName.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // sepAfterRow3
        // 
        sepAfterRow3.BackColor = Color.FromArgb(80, 80, 80);
        sepAfterRow3.Dock = DockStyle.Top;
        sepAfterRow3.Location = new Point(0, 41);
        sepAfterRow3.Name = "sepAfterRow3";
        sepAfterRow3.Size = new Size(782, 1);
        sepAfterRow3.TabIndex = 2;
        sepAfterRow3.Visible = false;
        // 
        // infoRow3Panel
        // 
        infoRow3Panel.BackColor = Color.Black;
        infoRow3Panel.Controls.Add(lblFileStats);
        infoRow3Panel.Controls.Add(lblFileDate);
        infoRow3Panel.Controls.Add(lblItemAttr);
        infoRow3Panel.Controls.Add(lblSort);
        infoRow3Panel.Dock = DockStyle.Top;
        infoRow3Panel.Location = new Point(0, 21);
        infoRow3Panel.Name = "infoRow3Panel";
        infoRow3Panel.Size = new Size(782, 20);
        infoRow3Panel.TabIndex = 3;
        // 
        // lblFileStats
        // 
        lblFileStats.AutoSize = true;
        lblFileStats.Dock = DockStyle.Right;
        lblFileStats.Font = new Font("Consolas", 10F);
        lblFileStats.ForeColor = Color.Yellow;
        lblFileStats.Location = new Point(778, 0);
        lblFileStats.Name = "lblFileStats";
        lblFileStats.Padding = new Padding(0, 0, 4, 0);
        lblFileStats.Size = new Size(4, 20);
        lblFileStats.TabIndex = 0;
        // 
        // lblFileDate
        // 
        lblFileDate.AutoSize = true;
        lblFileDate.Dock = DockStyle.Left;
        lblFileDate.Font = new Font("Consolas", 10F);
        lblFileDate.ForeColor = Color.Yellow;
        lblFileDate.Location = new Point(4, 0);
        lblFileDate.Name = "lblFileDate";
        lblFileDate.Size = new Size(0, 20);
        lblFileDate.TabIndex = 1;
        // 
        // lblItemAttr
        // 
        lblItemAttr.AutoSize = true;
        lblItemAttr.Dock = DockStyle.Left;
        lblItemAttr.Font = new Font("Consolas", 10F);
        lblItemAttr.ForeColor = Color.Yellow;
        lblItemAttr.Location = new Point(4, 0);
        lblItemAttr.Name = "lblItemAttr";
        lblItemAttr.Size = new Size(0, 20);
        lblItemAttr.TabIndex = 2;
        // 
        // lblSort
        // 
        lblSort.AutoSize = true;
        lblSort.Dock = DockStyle.Left;
        lblSort.Font = new Font("Consolas", 10F);
        lblSort.ForeColor = Color.Yellow;
        lblSort.Location = new Point(0, 0);
        lblSort.Name = "lblSort";
        lblSort.Padding = new Padding(4, 0, 0, 0);
        lblSort.Size = new Size(4, 20);
        lblSort.TabIndex = 3;
        // 
        // sepAfterRow2
        // 
        sepAfterRow2.BackColor = Color.FromArgb(80, 80, 80);
        sepAfterRow2.Dock = DockStyle.Top;
        sepAfterRow2.Location = new Point(0, 20);
        sepAfterRow2.Name = "sepAfterRow2";
        sepAfterRow2.Size = new Size(782, 1);
        sepAfterRow2.TabIndex = 4;
        sepAfterRow2.Visible = false;
        // 
        // infoRow2Panel
        // 
        infoRow2Panel.BackColor = Color.Black;
        infoRow2Panel.Controls.Add(lblPath);
        infoRow2Panel.Dock = DockStyle.Top;
        infoRow2Panel.Location = new Point(0, 0);
        infoRow2Panel.Name = "infoRow2Panel";
        infoRow2Panel.Size = new Size(782, 20);
        infoRow2Panel.TabIndex = 5;
        // 
        // lblPath
        // 
        lblPath.Dock = DockStyle.Fill;
        lblPath.Font = new Font("Consolas", 10F);
        lblPath.ForeColor = Color.Cyan;
        lblPath.Location = new Point(0, 0);
        lblPath.Name = "lblPath";
        lblPath.Padding = new Padding(4, 0, 0, 0);
        lblPath.Size = new Size(782, 20);
        lblPath.TabIndex = 0;
        lblPath.Text = "Path=C:\\";
        lblPath.TextAlign = ContentAlignment.MiddleLeft;
        lblPath.Click += lblPath_Click;
        // 
        // sepBeforeTopPanel
        // 
        sepBeforeTopPanel.BackColor = Color.FromArgb(80, 80, 80);
        sepBeforeTopPanel.Dock = DockStyle.Top;
        sepBeforeTopPanel.Location = new Point(1, 49);
        sepBeforeTopPanel.Name = "sepBeforeTopPanel";
        sepBeforeTopPanel.Size = new Size(782, 1);
        sepBeforeTopPanel.TabIndex = 3;
        // 
        // headerPanel
        // 
        headerPanel.BackColor = Color.Black;
        headerPanel.Controls.Add(lblClock);
        headerPanel.Controls.Add(headerZone4);
        headerPanel.Controls.Add(headerZone3);
        headerPanel.Controls.Add(headerZone2);
        headerPanel.Controls.Add(headerZone1);
        headerPanel.Dock = DockStyle.Top;
        headerPanel.Location = new Point(1, 29);
        headerPanel.Name = "headerPanel";
        headerPanel.Size = new Size(782, 20);
        headerPanel.TabIndex = 4;
        // 
        // headerZone4
        // 
        headerZone4.Controls.Add(lblFree);
        headerZone4.Dock = DockStyle.Left;
        headerZone4.Location = new Point(570, 0);
        headerZone4.Name = "headerZone4";
        headerZone4.Size = new Size(230, 20);
        headerZone4.TabIndex = 0;
        // 
        // lblFree
        // 
        lblFree.Dock = DockStyle.Fill;
        lblFree.ForeColor = Color.Cyan;
        lblFree.Location = new Point(0, 0);
        lblFree.Name = "lblFree";
        lblFree.Size = new Size(230, 20);
        lblFree.TabIndex = 0;
        lblFree.Text = "Free:";
        lblFree.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // headerZone3
        // 
        headerZone3.Controls.Add(lblUsed);
        headerZone3.Dock = DockStyle.Left;
        headerZone3.Location = new Point(340, 0);
        headerZone3.Name = "headerZone3";
        headerZone3.Size = new Size(230, 20);
        headerZone3.TabIndex = 1;
        // 
        // lblUsed
        // 
        lblUsed.Dock = DockStyle.Fill;
        lblUsed.ForeColor = Color.Cyan;
        lblUsed.Location = new Point(0, 0);
        lblUsed.Name = "lblUsed";
        lblUsed.Size = new Size(230, 20);
        lblUsed.TabIndex = 0;
        lblUsed.Text = "Used:";
        lblUsed.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // headerZone2
        // 
        headerZone2.Controls.Add(lblTotal);
        headerZone2.Dock = DockStyle.Left;
        headerZone2.Location = new Point(90, 0);
        headerZone2.Name = "headerZone2";
        headerZone2.Size = new Size(250, 20);
        headerZone2.TabIndex = 2;
        // 
        // lblTotal
        // 
        lblTotal.Dock = DockStyle.Fill;
        lblTotal.ForeColor = Color.Cyan;
        lblTotal.Location = new Point(0, 0);
        lblTotal.Name = "lblTotal";
        lblTotal.Size = new Size(250, 20);
        lblTotal.TabIndex = 0;
        lblTotal.Text = "Total:";
        lblTotal.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // headerZone1
        // 
        headerZone1.Controls.Add(lblPage);
        headerZone1.Dock = DockStyle.Left;
        headerZone1.Location = new Point(0, 0);
        headerZone1.Name = "headerZone1";
        headerZone1.Size = new Size(90, 20);
        headerZone1.TabIndex = 3;
        // 
        // lblPage
        // 
        lblPage.Dock = DockStyle.Fill;
        lblPage.ForeColor = Color.Cyan;
        lblPage.Location = new Point(0, 0);
        lblPage.Name = "lblPage";
        lblPage.Padding = new Padding(4, 0, 0, 0);
        lblPage.Size = new Size(90, 20);
        lblPage.TabIndex = 0;
        lblPage.Text = "Page: 1/ 1";
        lblPage.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // titleHeaderPanel
        // 
        titleHeaderPanel.BackColor = Color.Black;
        titleHeaderPanel.Controls.Add(lblTitle);
        titleHeaderPanel.Dock = DockStyle.Top;
        titleHeaderPanel.Location = new Point(1, 1);
        titleHeaderPanel.Name = "titleHeaderPanel";
        titleHeaderPanel.Size = new Size(782, 0);
        titleHeaderPanel.TabIndex = 5;
        titleHeaderPanel.Visible = false;
        titleHeaderPanel.Paint += titleHeaderPanel_Paint;
        // 
        // lblTitle
        // 
        lblTitle.Location = new Point(0, 0);
        lblTitle.Name = "lblTitle";
        lblTitle.Size = new Size(100, 23);
        lblTitle.TabIndex = 0;
        lblTitle.Visible = false;
        // 
        // lblClock
        // 
        lblClock.AutoSize = true;
        lblClock.Dock = DockStyle.Right;
        lblClock.Location = new Point(0, 0);
        lblClock.Name = "lblClock";
        lblClock.Padding = new Padding(0, 0, 8, 0);
        lblClock.Size = new Size(108, 20);
        lblClock.TabIndex = 1;
        lblClock.TextAlign = ContentAlignment.MiddleRight;
        // 
        // fileListView
        // 
        fileListView.BackColor = Color.Black;
        fileListView.Dock = DockStyle.Fill;
        fileListView.Font = new Font("Consolas", 11F);
        fileListView.ForeColor = Color.Cyan;
        fileListView.FullRowSelect = true;
        fileListView.ImeMode = ImeMode.Disable;
        fileListView.Location = new Point(0, 0);
        fileListView.Name = "fileListView";
        fileListView.OwnerDraw = true;
        fileListView.Size = new Size(800, 574);
        fileListView.TabIndex = 1;
        fileListView.UseCompatibleStateImageBehavior = false;
        fileListView.View = View.Details;
        fileListView.Visible = false;
        fileListView.DrawColumnHeader += FileListView_DrawColumnHeader;
        fileListView.DrawItem += FileListView_DrawItem;
        fileListView.DrawSubItem += FileListView_DrawSubItem;
        // 
        // statusStrip
        // 
        statusStrip.BackColor = Color.Black;
        statusStrip.ForeColor = Color.Cyan;
        statusStrip.ImageScalingSize = new Size(20, 20);
        statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel });
        statusStrip.Location = new Point(0, 574);
        statusStrip.Name = "statusStrip";
        statusStrip.Size = new Size(800, 26);
        statusStrip.TabIndex = 2;
        // 
        // statusLabel
        // 
        statusLabel.Font = new Font("Consolas", 10F);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(63, 20);
        statusLabel.Text = "Ready.";
        // 
        // messageTimer
        // 
        messageTimer.Interval = 10000;
        // 
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(8F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(800, 600);
        Controls.Add(outerHostPanel);
        Controls.Add(fileListView);
        Controls.Add(statusStrip);
        Controls.Add(mainMenuStrip);
        ImeMode = ImeMode.Disable;
        KeyPreview = true;
        MainMenuStrip = mainMenuStrip;
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "MidFD";
        KeyDown += MainForm_KeyDown;
        outerHostPanel.ResumeLayout(false);
        mainMenuStrip.ResumeLayout(false);
        mainMenuStrip.PerformLayout();
        contentFramePanel.ResumeLayout(false);
        viewerPanel.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)viewerPictureBox).EndInit();
        topPanel.ResumeLayout(false);
        infoRow4Panel.ResumeLayout(false);
        infoRow4Panel.PerformLayout();
        infoRow3Panel.ResumeLayout(false);
        infoRow3Panel.PerformLayout();
        infoRow2Panel.ResumeLayout(false);
        headerPanel.ResumeLayout(false);
        headerZone4.ResumeLayout(false);
        headerZone3.ResumeLayout(false);
        headerZone2.ResumeLayout(false);
        headerZone1.ResumeLayout(false);
        titleHeaderPanel.ResumeLayout(false);
        statusStrip.ResumeLayout(false);
        statusStrip.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }
}
