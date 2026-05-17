using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class FeatureProfileSelectionDialog : Form
{
    private readonly Button _practicalButton;
    private readonly Button _fullButton;

    public FeatureProfile SelectedProfile { get; private set; } = FeatureProfile.PracticalStable;

    public FeatureProfileSelectionDialog()
    {
        Text = "MidFD 利用モードの選択";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(720, 420);
        Padding = new Padding(12);

        var titleLabel = new Label
        {
            Text = "MidFD の利用モードを選択してください。",
            AutoSize = false,
            Location = new Point(16, 16),
            Size = new Size(680, 24),
            Font = new Font(Font, FontStyle.Bold)
        };

        var practicalGroup = new GroupBox
        {
            Text = "実用安定版（推奨）",
            Location = new Point(16, 52),
            Size = new Size(680, 128)
        };
        practicalGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(16, 24),
            Size = new Size(648, 90),
            Text = "通常利用向けの安定モードです。\r\n基本ファイラ機能、タブ、カテゴリ、QuickAccess、MarkSlot基本機能、\r\n7-Zip基本連携、外部ツール基本実行を有効にします。\r\n通常利用ではこちらを推奨します。"
        });

        var fullGroup = new GroupBox
        {
            Text = "高度機能α版",
            Location = new Point(16, 188),
            Size = new Size(680, 128)
        };
        fullGroup.Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(16, 24),
            Size = new Size(648, 90),
            Text = "開発中の高度機能を含むモードです。\r\nWorkspace Snapshot、MarkSlot集合演算、画像編集系機能、\r\n高度な自動追従などを含みます。\r\n不具合や仕様変更の可能性があります。"
        });

        var noteLabel = new Label
        {
            AutoSize = false,
            Location = new Point(16, 324),
            Size = new Size(680, 24),
            Text = "選択は後から「設定 > 起動 / 復元」で変更できます。"
        };

        _practicalButton = new Button
        {
            Text = "実用安定版で開始",
            Size = new Size(180, 32),
            Location = new Point(316, 364),
            DialogResult = DialogResult.OK
        };
        _practicalButton.Click += (_, _) =>
        {
            SelectedProfile = FeatureProfile.PracticalStable;
        };

        _fullButton = new Button
        {
            Text = "高度機能α版で開始",
            Size = new Size(180, 32),
            Location = new Point(506, 364),
            DialogResult = DialogResult.OK
        };
        _fullButton.Click += (_, _) =>
        {
            SelectedProfile = FeatureProfile.Full;
        };

        var cancelButton = new Button
        {
            Text = "キャンセル",
            Size = new Size(110, 32),
            Location = new Point(196, 364),
            DialogResult = DialogResult.Cancel
        };

        Controls.Add(titleLabel);
        Controls.Add(practicalGroup);
        Controls.Add(fullGroup);
        Controls.Add(noteLabel);
        Controls.Add(cancelButton);
        Controls.Add(_practicalButton);
        Controls.Add(_fullButton);

        AcceptButton = _practicalButton;
        CancelButton = cancelButton;
    }
}
