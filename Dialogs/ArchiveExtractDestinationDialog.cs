using MidFD.Models;
using MidFD.Helpers;

namespace MidFD.Dialogs;

public sealed class ArchiveExtractDestinationDialog : Form
{
    private readonly string _resolveBaseDirectory;
    private readonly TextBox _pathTextBox;
    private readonly RadioButton _createFolderRadio;
    private readonly RadioButton _directExtractRadio;
    private readonly Button _okButton;

    public ArchiveExtractDestinationOptions? Result { get; private set; }

    public ArchiveExtractDestinationDialog(string defaultDirectory, string archiveDisplayName, bool allowCreateFolder)
    {
        const int sideMargin = 16;
        const int topMargin = 16;
        _resolveBaseDirectory = defaultDirectory;
        Text = "archive 解凍先";
        ClientSize = new Size(504, 180); // Width 520 相当
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;

        int contentWidth = ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        var promptLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            AutoSize = true,
            Text = "解凍先フォルダを入力してください:"
        };
        Controls.Add(promptLabel);
        currentTop = promptLabel.Bottom + 8;

        _pathTextBox = new TextBox
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Text = defaultDirectory
        };
        Controls.Add(_pathTextBox);
        currentTop = _pathTextBox.Bottom + 16;

        _createFolderRadio = new RadioButton
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Text = $"archive 名のフォルダを作って解凍 ({archiveDisplayName})",
            Checked = allowCreateFolder,
            AutoSize = true
        };
        Controls.Add(_createFolderRadio);
        _createFolderRadio.Height = FileOperationDialogLayoutHelper.MeasureTextHeight(_createFolderRadio, _createFolderRadio.Width, _createFolderRadio.Height);
        currentTop = _createFolderRadio.Bottom + 6;

        _directExtractRadio = new RadioButton
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Text = "指定フォルダへそのまま解凍",
            Checked = !allowCreateFolder,
            AutoSize = true
        };
        Controls.Add(_directExtractRadio);
        currentTop = _directExtractRadio.Bottom;

        _okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.None,
            MinimumSize = new Size(80, 30)
        };
        _okButton.Click += (_, _) => Confirm();

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            MinimumSize = new Size(80, 30)
        };

        Controls.Add(_okButton);
        Controls.Add(cancelButton);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            this,
            new[] { _okButton, cancelButton },
            currentTop,
            buttonGap: 10,
            contentGap: 16);

        AcceptButton = _okButton;
        CancelButton = cancelButton;
        DialogKeyboardHelper.AttachOkCancelBindings(this, _okButton, cancelButton);

        Shown += (_, _) => _pathTextBox.SelectAll();

        Helpers.DirectoryPathCompletionController.Attach(_pathTextBox);
    }

    public static ArchiveExtractDestinationOptions? Show(IWin32Window owner, string defaultDirectory, string archiveDisplayName, bool allowCreateFolder = true)
    {
        using var dialog = new ArchiveExtractDestinationDialog(defaultDirectory, archiveDisplayName, allowCreateFolder);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
    }

    private void Confirm()
    {
        string trimmed = _pathTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            System.Media.SystemSounds.Beep.Play();
            _pathTextBox.Focus();
            return;
        }

        try
        {
            string resolvedBase = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(_resolveBaseDirectory, trimmed));

            Result = new ArchiveExtractDestinationOptions
            {
                BaseDirectory = resolvedBase,
                CreateArchiveRootDirectory = _createFolderRadio.Checked
            };

            DialogResult = DialogResult.OK;
            Close();
        }
        catch
        {
            System.Media.SystemSounds.Beep.Play();
            _pathTextBox.Focus();
        }
    }
}
