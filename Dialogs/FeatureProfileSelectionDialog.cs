using MidFD.Models;
using MidFD.Configuration;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class FeatureProfileSelectionDialog : Form
{
    private readonly RadioButton _practicalProfileRadio;
    private readonly RadioButton _fullProfileRadio;
    private readonly CheckBox _fdCompatibleFunctionKeysCheckBox;
    private readonly CheckBox _videoEnterPlaysExternalCheckBox;
    private readonly TextBox _sevenZipPathBox;
    private readonly TextBox _videoToolDirectoryBox;
    private readonly TextBox _externalEditorPathBox;

    public FeatureProfile SelectedProfile { get; private set; } = FeatureProfile.PracticalStable;
    public bool UseFdCompatibleFunctionKeys => _fdCompatibleFunctionKeysCheckBox.Checked;
    public bool VideoEnterPlaysExternal => _videoEnterPlaysExternalCheckBox.Checked;
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
        ClientSize = new Size(720, 760);
        Padding = new Padding(12);

        SelectedProfile = ResolveInitialProfile(settings);

        var titleLabel = new Label
        {
            Text = "MidFD の利用モードと基本操作を選択してください。",
            AutoSize = false,
            Location = new Point(16, 16),
            Size = new Size(680, 24),
            Font = new Font(Font, FontStyle.Bold)
        };

        var profileGroup = new GroupBox
        {
            Text = "利用モード",
            Location = new Point(16, 52),
            Size = new Size(680, 250)
        };

        _practicalProfileRadio = new RadioButton
        {
            Text = "実用安定版（推奨）",
            AutoSize = true,
            Location = new Point(16, 24),
            Checked = SelectedProfile == FeatureProfile.PracticalStable
        };
        profileGroup.Controls.Add(_practicalProfileRadio);
        profileGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, 48),
            Size = new Size(632, 76),
            Text = "通常利用向けの安定モードです。\r\n基本ファイラ機能、タブ、カテゴリ、QuickAccess、MarkSlot基本機能、\r\n7-Zip基本連携、外部ツール基本実行を有効にします。\r\n通常利用ではこちらを推奨します。"
        });

        _fullProfileRadio = new RadioButton
        {
            Text = "高度機能α版",
            AutoSize = true,
            Location = new Point(16, 132),
            Checked = SelectedProfile == FeatureProfile.Full
        };
        profileGroup.Controls.Add(_fullProfileRadio);
        profileGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(36, 156),
            Size = new Size(632, 76),
            Text = "開発中の高度機能を含むモードです。\r\nWorkspace Snapshot、MarkSlot集合演算、画像編集系機能、\r\n高度な自動追従などを含みます。\r\n不具合や仕様変更の可能性があります。"
        });

        var operationGroup = new GroupBox
        {
            Text = "操作スタイル",
            Location = new Point(16, 310),
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
            Location = new Point(16, 454),
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
            Location = new Point(16, 686),
            Size = new Size(680, 24),
            Text = "これらの設定は後から設定画面で変更できます。"
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
            SelectedProfile = _fullProfileRadio.Checked
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
        Controls.Add(profileGroup);
        Controls.Add(operationGroup);
        Controls.Add(externalInfoGroup);
        Controls.Add(noteLabel);
        Controls.Add(cancelButton);
        Controls.Add(startButton);

        AcceptButton = startButton;
        CancelButton = cancelButton;
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
}
