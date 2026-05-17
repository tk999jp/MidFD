using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class QuickAccessLocationDialog : Form
{
    private readonly TextBox _displayNameTextBox;
    private readonly TextBox _pathTextBox;
    private readonly CheckBox _useForTabTitleCheckBox;
    private readonly Label _summaryLabel;
    private readonly string _currentPath;
    private bool _displayNameTouched;

    public string DisplayNameValue => _displayNameTextBox.Text.Trim();
    public string PathValue => _pathTextBox.Text.Trim();
    public bool UseForTabTitle => _useForTabTitleCheckBox.Checked;

    public QuickAccessLocationDialog(
        string title,
        string currentPath,
        string initialPath,
        string initialDisplayName,
        bool initialUseForTabTitle)
    {
        const int horizontalMargin = 16;
        const int verticalMargin = 16;
        const int controlSpacing = 8;
        const int rowGap = 12;
        const int buttonHeight = 30;

        _currentPath = currentPath;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        KeyPreview = true;

        int contentWidth = 720;
        int dialogWidth = contentWidth + (horizontalMargin * 2);
        int buttonHorizontalPadding = 18;
        int currentPathButtonWidth = Math.Max(96, TextRenderer.MeasureText("現在地を入れる", Font).Width + buttonHorizontalPadding);
        int browseButtonWidth = Math.Max(128, TextRenderer.MeasureText("フォルダを選ぶ...", Font).Width + buttonHorizontalPadding);
        int okButtonWidth = Math.Max(88, TextRenderer.MeasureText("保存", Font).Width + buttonHorizontalPadding);
        int cancelButtonWidth = Math.Max(96, TextRenderer.MeasureText("キャンセル", Font).Width + buttonHorizontalPadding);
        int pathTextWidth = contentWidth - currentPathButtonWidth - browseButtonWidth - (controlSpacing * 2);
        int bottomButtonsWidth = okButtonWidth + cancelButtonWidth + controlSpacing;

        var displayNameLabel = new Label
        {
            Left = horizontalMargin,
            Top = verticalMargin,
            Width = contentWidth,
            Text = "表示名"
        };
        _displayNameTextBox = new TextBox
        {
            Left = horizontalMargin,
            Top = displayNameLabel.Bottom + 4,
            Width = contentWidth,
            Text = initialDisplayName
        };

        var pathLabel = new Label
        {
            Left = horizontalMargin,
            Top = _displayNameTextBox.Bottom + rowGap,
            Width = contentWidth,
            Text = "移動先フォルダ"
        };
        _pathTextBox = new TextBox
        {
            Left = horizontalMargin,
            Top = pathLabel.Bottom + 4,
            Width = pathTextWidth,
            Text = initialPath
        };
        var currentPathButton = new Button
        {
            Left = _pathTextBox.Right + 8,
            Top = _pathTextBox.Top - 1,
            Width = currentPathButtonWidth,
            Height = 27,
            Text = "現在地を入れる"
        };
        var browseButton = new Button
        {
            Left = currentPathButton.Right + 8,
            Top = currentPathButton.Top,
            Width = browseButtonWidth,
            Height = 27,
            Text = "フォルダを選ぶ..."
        };

        _useForTabTitleCheckBox = new CheckBox
        {
            Left = horizontalMargin,
            Top = _pathTextBox.Bottom + rowGap + 4,
            Width = contentWidth,
            Text = "この表示名をタブ見出しにも使う",
            Checked = initialUseForTabTitle
        };

        _summaryLabel = new Label
        {
            Left = horizontalMargin,
            Top = _useForTabTitleCheckBox.Bottom + 12,
            Width = contentWidth,
            Height = 48,
            ForeColor = SystemColors.GrayText
        };

        var okButton = new Button
        {
            Text = "保存",
            Left = dialogWidth - horizontalMargin - bottomButtonsWidth,
            Width = okButtonWidth,
            Top = _summaryLabel.Bottom + 12,
            Height = buttonHeight,
            DialogResult = DialogResult.OK
        };
        var cancelButton = new Button
        {
            Text = "キャンセル",
            Left = okButton.Right + controlSpacing,
            Width = cancelButtonWidth,
            Top = okButton.Top,
            Height = buttonHeight,
            DialogResult = DialogResult.Cancel
        };

        Controls.Add(displayNameLabel);
        Controls.Add(_displayNameTextBox);
        Controls.Add(pathLabel);
        Controls.Add(_pathTextBox);
        Controls.Add(currentPathButton);
        Controls.Add(browseButton);
        Controls.Add(_useForTabTitleCheckBox);
        Controls.Add(_summaryLabel);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        ClientSize = new Size(dialogWidth, okButton.Bottom + verticalMargin);

        _displayNameTextBox.TextChanged += (_, _) =>
        {
            _displayNameTouched = true;
            UpdateSummaryText();
        };
        _pathTextBox.TextChanged += (_, _) =>
        {
            ApplySuggestedDisplayName();
            UpdateSummaryText();
        };
        _useForTabTitleCheckBox.CheckedChanged += (_, _) => UpdateSummaryText();
        currentPathButton.Click += (_, _) =>
        {
            _pathTextBox.Text = _currentPath;
        };
        browseButton.Click += (_, _) => BrowsePath();

        Shown += (_, _) =>
        {
            _displayNameTouched = false;
            _displayNameTextBox.SelectAll();
        };

        UpdateSummaryText();
        Helpers.DirectoryPathCompletionController.Attach(_pathTextBox);
    }

    private void BrowsePath()
    {
        string seed = string.IsNullOrWhiteSpace(_currentPath) ? _pathTextBox.Text : _currentPath;
        string? selected = TreeDialog.Show(seed);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            _pathTextBox.Text = selected;
        }
    }

    private void ApplySuggestedDisplayName()
    {
        if (_displayNameTouched && !string.IsNullOrWhiteSpace(_displayNameTextBox.Text))
        {
            return;
        }

        string normalized = QuickAccessService.NormalizePath(_pathTextBox.Text, _currentPath) ?? _pathTextBox.Text;
        _displayNameTextBox.Text = QuickAccessService.CreateDisplayName(normalized);
        _displayNameTouched = false;
        _displayNameTextBox.SelectionStart = _displayNameTextBox.Text.Length;
    }

    private void UpdateSummaryText()
    {
        string pathSummary = string.IsNullOrWhiteSpace(_pathTextBox.Text)
            ? "パス未入力"
            : _pathTextBox.Text.Trim();
        string displayNameSummary = _displayNameTextBox.Text.Trim().Length == 0
            ? "(空なら末端ディレクトリ名)"
            : _displayNameTextBox.Text.Trim();
        string tabTitleText = _useForTabTitleCheckBox.Checked ? "はい" : "いいえ";
        _summaryLabel.Text = $"表示名: {displayNameSummary} / タブ見出しにも使う: {tabTitleText}\r\n移動先: {pathSummary}";
    }

    public static QuickAccessLocationDialogResult? ShowEditor(
        IWin32Window owner,
        string title,
        string currentPath,
        string initialPath,
        string initialDisplayName,
        bool initialUseForTabTitle)
    {
        using var dialog = new QuickAccessLocationDialog(title, currentPath, initialPath, initialDisplayName, initialUseForTabTitle);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        return new QuickAccessLocationDialogResult(
            dialog.DisplayNameValue,
            dialog.PathValue,
            dialog.UseForTabTitle);
    }
}

public sealed record QuickAccessLocationDialogResult(
    string DisplayName,
    string Path,
    bool UseForTabTitle);
