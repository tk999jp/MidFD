using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MidFD.Dialogs;

/// <summary>
/// WinFD風のフォルダ参照ダイアログ。
/// モーダル表示され、選択されたフォルダのフルパスを返す。
/// </summary>
public static class TreeDialog
{
    private const string DummyNodeKey = "_dummy_";

    public static string? Show(string defaultPath = "")
    {
        using Form form = new Form()
        {
            Width = 500,
            Height = 600,
            FormBorderStyle = FormBorderStyle.Sizable,
            Text = "フォルダの参照",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = true,
            BackColor = Color.FromArgb(240, 240, 240),
            AutoScaleMode = AutoScaleMode.Font,
            MinimumSize = new Size(300, 400)
        };

        // メインパネル
        Panel mainPanel = new Panel()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };
        form.Controls.Add(mainPanel);

        // TreeView
        TreeView treeView = new TreeView()
        {
            Dock = DockStyle.Fill,
            Font = new Font("Meiryo UI", 10F),
            HideSelection = false,
            HotTracking = true,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true
        };
        mainPanel.Controls.Add(treeView);

        // 下部ボタンエリア
        Panel buttonPanel = new Panel()
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            Padding = new Padding(0, 8, 12, 8)
        };
        form.Controls.Add(buttonPanel);

        Button btnCancel = new Button()
        {
            Text = "キャンセル",
            Dock = DockStyle.Right,
            Width = 100,
            DialogResult = DialogResult.Cancel,
            Font = new Font("Meiryo UI", 9F)
        };
        buttonPanel.Controls.Add(btnCancel);

        // スペーサー
        Label spacer = new Label() { Dock = DockStyle.Right, Width = 8 };
        buttonPanel.Controls.Add(spacer);

        Button btnOk = new Button()
        {
            Text = "OK",
            Dock = DockStyle.Right,
            Width = 100,
            DialogResult = DialogResult.OK,
            Font = new Font("Meiryo UI", 9F)
        };
        buttonPanel.Controls.Add(btnOk);

        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        // イベント設定
        treeView.BeforeExpand += (s, e) =>
        {
            if (e.Node == null) return;
            // ダミーがあれば展開時に実際の内容を読み込む
            if (e.Node.Nodes.ContainsKey(DummyNodeKey))
            {
                e.Node.Nodes.Clear();
                if (e.Node.Tag is string path)
                {
                    LoadSubDirectories(e.Node, path);
                }
            }
        };

        treeView.NodeMouseDoubleClick += (s, e) =>
        {
            if (e.Node != null && e.Node.Tag != null)
            {
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
        };

        treeView.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (treeView.SelectedNode != null && treeView.SelectedNode.Tag != null)
                {
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                }
                e.Handled = true;
            }
        };

        // ドライブ一覧の初期ロード
        LoadDrives(treeView);

        // 初期表示後にも再度展開し、遅延読み込み直後でも現在位置へ寄せる。
        form.Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(defaultPath))
            {
                ExpandToPath(treeView, defaultPath);
            }
        };

        // 表示
        if (form.ShowDialog() == DialogResult.OK)
        {
            return treeView.SelectedNode?.Tag as string;
        }

        return null;
    }

    private static void LoadDrives(TreeView treeView)
    {
        treeView.Nodes.Clear();
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
        foreach (var drive in drives)
        {
            TreeNode driveNode = new TreeNode(drive.Name)
            {
                Tag = drive.RootDirectory.FullName,
                ImageKey = "Drive",
                SelectedImageKey = "Drive"
            };
            // 子があるかもしれないのでダミーを追加
            driveNode.Nodes.Add(DummyNodeKey, "loading...");
            treeView.Nodes.Add(driveNode);
        }
    }

    private static void LoadSubDirectories(TreeNode parentNode, string path)
    {
        try
        {
            var dirInfo = new DirectoryInfo(path);
            var subDirs = dirInfo.GetDirectories()
                                 .OrderBy(d => d.Name);

            foreach (var dir in subDirs)
            {
                // 隠し属性などは今回考慮せず実ディレクトリすべて出す
                TreeNode node = new TreeNode(dir.Name)
                {
                    Tag = dir.FullName
                };
                
                // 更に下にディレクトリがあるかチェック（遅延展開用ダミー）
                // 実際に中身を調べるのは重いので、常にダミーを置いて展開時にエラーハンドリングする方式
                node.Nodes.Add(DummyNodeKey, "loading...");
                
                parentNode.Nodes.Add(node);
            }
        }
        catch (Exception ex)
        {
            // アクセス拒否などは無視して空ノードにする
            Console.WriteLine($"TreeDialog Error: {ex.Message}");
        }
    }

    private static void ExpandToPath(TreeView treeView, string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath) ?? "";

            // 1. ルート（ドライブ）を探す
            TreeNode? currentNode = null;
            foreach (TreeNode node in treeView.Nodes)
            {
                if (string.Equals(node.Tag as string, root, StringComparison.OrdinalIgnoreCase))
                {
                    currentNode = node;
                    break;
                }
            }

            if (currentNode == null) return;

            // 2. 階層を辿る
            string relativePath = fullPath.Substring(root.Length);
            string[] segments = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            currentNode.Expand(); // ルートを展開

            foreach (string segment in segments)
            {
                TreeNode? nextNode = null;
                foreach (TreeNode child in currentNode.Nodes)
                {
                    if (string.Equals(child.Text, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        nextNode = child;
                        break;
                    }
                }

                if (nextNode != null)
                {
                    currentNode = nextNode;
                    currentNode.Expand();
                }
                else
                {
                    break;
                }
            }

            // 最後に到達した（または途切れた）ノードを選択
            treeView.SelectedNode = currentNode;
            currentNode.EnsureVisible();
        }
        catch
        {
            // パス解析エラーなどは安全に無視
        }
    }
}
