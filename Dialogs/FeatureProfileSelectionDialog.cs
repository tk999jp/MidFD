using MidFD.Models;
using MidFD.Configuration;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class FeatureProfileSelectionDialog : Form
{
    private readonly CheckBox _restoreLastPathCheckBox;
    private readonly CheckBox _enableWorkspaceSnapshotCheckBox;
    private readonly CheckBox _enableMouseGesturesCheckBox;
    private readonly CheckBox _showFunctionBarTooltipsCheckBox;
    private readonly CheckBox _enableDragArchiveHandoffCheckBox;
    private readonly CheckBox _includeDragZipManifestCheckBox;
    private readonly CheckBox _fdCompatibleFunctionKeysCheckBox;
    private readonly CheckBox _videoEnterPlaysExternalCheckBox;
    private readonly TextBox _sevenZipPathBox;
    private readonly TextBox _videoToolDirectoryBox;
    private readonly TextBox _externalEditorPathBox;

    public FeatureProfile SelectedProfile { get; private set; } = FeatureProfile.PracticalStable;
    public bool UseFdCompatibleFunctionKeys => _fdCompatibleFunctionKeysCheckBox.Checked;
    public bool VideoEnterPlaysExternal => _videoEnterPlaysExternalCheckBox.Checked;
    public bool RestoreLastPath => _restoreLastPathCheckBox.Checked;
    public bool EnableWorkspaceSnapshotFeatures => _enableWorkspaceSnapshotCheckBox.Checked;
    public bool EnableMouseGestures => _enableMouseGesturesCheckBox.Checked;
    public bool ShowFunctionBarTooltips => _showFunctionBarTooltipsCheckBox.Checked;
    public bool EnableDragArchiveHandoff => _enableDragArchiveHandoffCheckBox.Checked;
    public bool IncludeDragZipManifest => _includeDragZipManifestCheckBox.Checked;
    public string? SevenZipPath => NullIfEmpty(_sevenZipPathBox.Text);
    public string? VideoToolDirectory => NullIfEmpty(_videoToolDirectoryBox.Text);
    public string? ExternalEditorPath => NullIfEmpty(_externalEditorPathBox.Text);

    public FeatureProfileSelectionDialog(AppSettings? settings = null)
    {
        Text = "MidFD 初回セットアップ";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(720, 920);
        Padding = new Padding(12);

        SelectedProfile = ResolveInitialProfile(settings);

        var titleLabel = new Label
        {
            Text = "MidFD の初期オプションと基本操作を選択してください。",
            AutoSize = false,
            Location = new Point(16, 16),
            Size = new Size(680, 24),
            Font = new Font(Font, FontStyle.Bold)
        };

        var advancedHeadingLabel = new Label
        {
            Text = "初回セットアップでは導入用の項目だけを表示します。後から「設定 > 操作 / 外部連携」で変更できます。",
            AutoSize = false,
            Location = new Point(16, 48),
            Size = new Size(680, 36)
        };

        var advancedGroup = new GroupBox
        {
            Text = "初期オプション",
            Location = new Point(16, 92),
            Size = new Size(680, 350)
        };

        int advancedTop = 24;
        advancedGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(16, advancedTop),
            Size = new Size(640, 20),
            Text = "高度な使い方（任意）"
        });
        advancedTop += 24;
        _enableDragArchiveHandoffCheckBox = new CheckBox
        {
            Text = "Drag ZIP を使う",
            AutoSize = true,
            Location = new Point(16, advancedTop),
            Checked = settings?.FileOperations?.EnableDragArchiveHandoff ?? false
        };
        advancedGroup.Controls.Add(_enableDragArchiveHandoffCheckBox);
        advancedGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, advancedTop + 20),
            Size = new Size(620, 20),
            Text = "Shift/Ctrl+複数マークドラッグ時に、ZIP 1個へまとめて外部へ渡します。"
        });

        _includeDragZipManifestCheckBox = new CheckBox
        {
            Text = "Drag ZIP に内容一覧manifestを同梱する",
            AutoSize = true,
            Location = new Point(36, advancedTop + 44),
            Checked = settings?.FileOperations?.IncludeDragZipManifest ?? false
        };
        advancedGroup.Controls.Add(_includeDragZipManifestCheckBox);
        advancedGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(56, advancedTop + 64),
            Size = new Size(600, 20),
            Text = "ZIP内へ対象一覧を入れます。ローカルパス情報を含む場合があります。"
        });

        _enableMouseGesturesCheckBox = new CheckBox
        {
            Text = "マウスジェスチャーを使う",
            AutoSize = true,
            Location = new Point(16, advancedTop + 92),
            Checked = settings?.Input?.EnableMouseGestures ?? false
        };
        advancedGroup.Controls.Add(_enableMouseGesturesCheckBox);
        advancedGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, advancedTop + 112),
            Size = new Size(620, 20),
            Text = "右ドラッグで戻る/進むなどの操作を行います。"
        });

        _showFunctionBarTooltipsCheckBox = new CheckBox
        {
            Text = "Functionバーの詳細説明を表示する",
            AutoSize = true,
            Location = new Point(16, advancedTop + 140),
            Checked = settings?.Input?.ShowFunctionBarTooltips ?? true
        };
        advancedGroup.Controls.Add(_showFunctionBarTooltipsCheckBox);
        advancedGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, advancedTop + 160),
            Size = new Size(620, 20),
            Text = "Functionバーのマウスオーバー時に説明やキーヒントを表示します。"
        });

        _restoreLastPathCheckBox = new CheckBox
        {
            Text = "前回フォルダを復元する",
            AutoSize = true,
            Location = new Point(16, advancedTop + 188),
            Checked = settings?.Session?.RestoreLastPath ?? true
        };
        advancedGroup.Controls.Add(_restoreLastPathCheckBox);
        advancedGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, advancedTop + 208),
            Size = new Size(620, 20),
            Text = "起動時に前回見ていたフォルダへ戻ります。"
        });

        _enableWorkspaceSnapshotCheckBox = new CheckBox
        {
            Text = "Workspace Snapshot / 作業状態復元を使う",
            AutoSize = true,
            Location = new Point(16, advancedTop + 236),
            Checked = SelectedProfile == FeatureProfile.Full
        };
        advancedGroup.Controls.Add(_enableWorkspaceSnapshotCheckBox);

        advancedGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, advancedTop + 256),
            Size = new Size(620, 36),
            Text = "Workspace Snapshot や MarkSlot の拡張管理導線を表示します。\r\n起動時の復元内容は、後から「設定 > 起動・ログ」で調整できます。"
        });

        var operationGroup = new GroupBox
        {
            Text = "操作スタイル",
            Location = new Point(16, 450),
            Size = new Size(680, 134)
        };
        _fdCompatibleFunctionKeysCheckBox = new CheckBox
        {
            Text = "FD/WinFD互換の操作プリセットを使う",
            AutoSize = true,
            Location = new Point(16, 20),
            Checked = string.Equals(settings?.Input?.FunctionKeyProfile, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase)
        };
        operationGroup.Controls.Add(_fdCompatibleFunctionKeysCheckBox);
        operationGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, 40),
            Size = new Size(632, 20),
            Text = "Fキー配置・一部Shift+F・列数キー操作をWinFD寄りにします。"
        });

        _videoEnterPlaysExternalCheckBox = new CheckBox
        {
            Text = "動画ファイルは Enter で外部再生する",
            AutoSize = true,
            Location = new Point(16, 64),
            Checked = settings?.Preview?.VideoEnterPlaysExternal ?? false
        };
        operationGroup.Controls.Add(_videoEnterPlaysExternalCheckBox);
        operationGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, 86),
            Size = new Size(632, 40),
            Text = "OFF: Enter=静止画 / Ctrl+Enter=外部再生\r\nON : Enter=外部再生 / Ctrl+Enter=静止画\r\nVキーは常に静止画プレビュー"
        });

        var externalInfoGroup = new GroupBox
        {
            Text = "外部連携（任意）",
            Location = new Point(16, 590),
            Size = new Size(680, 222)
        };
        int labelWidth = 130;
        int inputX = 154;
        int top = 28;

        AddLabel(externalInfoGroup, "7-Zip パス:", 16, top + 4, labelWidth);
        _sevenZipPathBox = AddTextBox(externalInfoGroup, inputX, top, 432, settings?.SevenZip?.ExePath ?? string.Empty);
        AddBrowseFileButton(externalInfoGroup, 596, top - 1, 64, _sevenZipPathBox, "7-Zip 実行ファイルを選択", "実行ファイル|*.exe|すべてのファイル|*.*");
        top += 36;

        AddLabel(externalInfoGroup, "動画ツールフォルダ:", 16, top + 4, labelWidth);
        _videoToolDirectoryBox = AddTextBox(externalInfoGroup, inputX, top, 432, settings?.Preview?.VideoToolDirectory ?? string.Empty);
        AddBrowseFolderButton(externalInfoGroup, 596, top - 1, 64, _videoToolDirectoryBox, "動画ツールフォルダを選択");
        top += 36;

        AddLabel(externalInfoGroup, "外部エディタ:", 16, top + 4, labelWidth);
        _externalEditorPathBox = AddTextBox(externalInfoGroup, inputX, top, 432, settings?.ExternalTools?.ExternalEditorPath ?? string.Empty);
        AddBrowseFileButton(externalInfoGroup, 596, top - 1, 64, _externalEditorPathBox, "外部エディタ実行ファイルを選択", "実行ファイル|*.exe|すべてのファイル|*.*");
        top += 40;

        externalInfoGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(16, top),
            Size = new Size(648, 78),
            Text = "※未設定でも基本操作は可能です。\r\n※動画の外部再生は ffplay がなくても Windows の関連付けで開きます。\r\n※動画静止画プレビューには ffmpeg.exe が必要です。\r\n※後から「設定 > 外部連携」で変更できます。"
        });

        var noteLabel = new Label
        {
            AutoSize = false,
            Location = new Point(16, 820),
            Size = new Size(680, 40),
            Text = "後から「設定 > 操作 / 外部連携」で変更できます。\r\nAlt+英数字ランチャーや MarkSlot などは、入力割り当てや外部ツール定義から利用します。"
        };

        int buttonBottomY = ClientSize.Height - 40;

        var startButton = new Button
        {
            Text = "この設定で開始",
            Size = new Size(180, 32),
            Location = new Point(316, buttonBottomY),
            DialogResult = DialogResult.OK
        };
        startButton.Click += (_, _) =>
        {
            SelectedProfile = _enableWorkspaceSnapshotCheckBox.Checked
                ? FeatureProfile.Full
                : FeatureProfile.PracticalStable;
        };

        var cancelButton = new Button
        {
            Text = "キャンセル",
            Size = new Size(110, 32),
            Location = new Point(506, buttonBottomY),
            DialogResult = DialogResult.Cancel
        };

        Controls.Add(titleLabel);
        Controls.Add(advancedHeadingLabel);
        Controls.Add(advancedGroup);
        Controls.Add(operationGroup);
        Controls.Add(externalInfoGroup);
        Controls.Add(noteLabel);
        Controls.Add(cancelButton);
        Controls.Add(startButton);

        AcceptButton = startButton;
        CancelButton = cancelButton;

        _enableDragArchiveHandoffCheckBox.CheckedChanged += (_, _) => UpdateAdvancedOptionsEnabledState();
        UpdateAdvancedOptionsEnabledState();
    }

    private static string? NullIfEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return value.Trim();
    }

    private static void AddLabel(Control parent, string text, int x, int y, int width)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 20)
        });
    }

    private static TextBox AddTextBox(Control parent, int x, int y, int width, string value)
    {
        var box = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 23),
            Text = value
        };
        parent.Controls.Add(box);
        return box;
    }

    private static void AddBrowseFileButton(Control parent, int x, int y, int width, TextBox target, string title, string filter)
    {
        var button = new Button
        {
            Text = "参照",
            Location = new Point(x, y),
            Size = new Size(width, 25)
        };
        button.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true
            };
            if (dialog.ShowDialog(parent.FindForm()) == DialogResult.OK)
            {
                target.Text = dialog.FileName;
            }
        };
        parent.Controls.Add(button);
    }

    private static void AddBrowseFolderButton(Control parent, int x, int y, int width, TextBox target, string description)
    {
        var button = new Button
        {
            Text = "参照",
            Location = new Point(x, y),
            Size = new Size(width, 25)
        };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = description,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog(parent.FindForm()) == DialogResult.OK)
            {
                target.Text = dialog.SelectedPath;
            }
        };
        parent.Controls.Add(button);
    }

    private static FeatureProfile ResolveInitialProfile(AppSettings? settings)
    {
        return FeatureProfileService.TryResolveProfile(settings?.Profile, out FeatureProfile profile)
            ? profile
            : FeatureProfile.PracticalStable;
    }

    private void UpdateAdvancedOptionsEnabledState()
    {
        _includeDragZipManifestCheckBox.Enabled = _enableDragArchiveHandoffCheckBox.Checked;
    }
}
