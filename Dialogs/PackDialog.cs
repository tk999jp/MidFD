using MidFD.Models;
using MidFD.Helpers;

namespace MidFD.Dialogs;

public enum PackExistingArchiveAction
{
    Add,
    Overwrite,
    Cancel
}

public sealed class PackDialog : Form
{
    private const string CustomSplitValue = "__custom__";

    private static PackArchiveFormat _lastFormat = PackArchiveFormat.Zip;
    private static PackCompressionLevel _lastCompressionLevel = PackCompressionLevel.Normal;
    private static string? _lastOutputDirectory;

    private readonly TextBox _outputDirectoryTextBox;
    private readonly TextBox _outputFileNameTextBox;
    private readonly ComboBox _formatComboBox;
    private readonly ComboBox _compressionComboBox;
    private readonly ComboBox _splitComboBox;
    private readonly TextBox _customSplitTextBox;
    private readonly Label _targetSummaryLabel;
    private readonly CheckBox _packEachFolderIndividuallyCheckBox;
    private readonly string _initialDirectory;
    private readonly Func<IWin32Window, string, PackExistingArchiveAction>? _confirmExistingArchiveOverwrite;
    private bool _updatingOutputExtension;

    public PackRequest? Result { get; private set; }

    public PackDialog(
        string initialDirectory,
        string defaultArchiveName,
        string targetSummary,
        bool canPackEachFolderIndividually,
        bool defaultPackEachFolderIndividually,
        IReadOnlyList<PackArchiveFormat> availableFormats,
        string hintText,
        Func<IWin32Window, string, PackExistingArchiveAction>? confirmExistingArchiveOverwrite = null)
    {
        _initialDirectory = initialDirectory;
        _confirmExistingArchiveOverwrite = confirmExistingArchiveOverwrite;
        string initialOutputDirectory = initialDirectory;
        string initialFileName = BuildInitialFileName(defaultArchiveName);

        Text = "Pack";
        ClientSize = new Size(604, 386);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;

        int sideMargin = 16;
        int currentTop = 16;
        int labelToControlGap = 24;
        int rowGap = 36;

        var targetSummaryTitleLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 56,
            Text = "対象:"
        };
        string displaySummary = targetSummary ?? string.Empty;
        if (displaySummary.StartsWith("対象:"))
        {
            displaySummary = displaySummary.Substring(3).TrimStart();
        }
        else if (displaySummary.StartsWith("対象："))
        {
            displaySummary = displaySummary.Substring(3).TrimStart();
        }

        _targetSummaryLabel = new Label
        {
            Left = sideMargin + 56,
            Top = currentTop,
            Width = 532,
            AutoEllipsis = true,
            Text = string.IsNullOrWhiteSpace(displaySummary) ? "不明" : displaySummary
        };
        currentTop += 24;

        var outputDirectoryLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 160,
            Text = "出力先フォルダ(&D):"
        };

        _outputDirectoryTextBox = new TextBox
        {
            Left = sideMargin,
            Top = currentTop + labelToControlGap,
            Width = 322,
            Text = initialOutputDirectory,
            TabIndex = 0
        };

        var browseFolderButton = new Button
        {
            Left = 346,
            Top = currentTop + labelToControlGap - 2,
            Width = 118,
            Height = 28,
            Text = "フォルダを選ぶ...",
            TabStop = false
        };
        browseFolderButton.Click += (_, _) => BrowseOutputDirectory();

        var browseTreeButton = new Button
        {
            Left = 470,
            Top = currentTop + labelToControlGap - 2,
            Width = 118,
            Height = 28,
            Text = "ツリーから選択...",
            TabStop = false
        };
        browseTreeButton.Click += (_, _) => BrowseOutputDirectoryFromTree();

        currentTop += rowGap + outputDirectoryLabel.Height;

        var outputFileNameLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 160,
            Text = "archive ファイル名(&N):"
        };

        _outputFileNameTextBox = new TextBox
        {
            Left = sideMargin,
            Top = currentTop + labelToControlGap,
            Width = 572,
            Text = initialFileName,
            TabIndex = 1
        };

        currentTop += rowGap + outputFileNameLabel.Height + 12;

        var formatLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 120,
            Text = "形式(&F):"
        };

        _formatComboBox = new ComboBox
        {
            Left = sideMargin,
            Top = currentTop + 22,
            Width = 150,
            DropDownStyle = ComboBoxStyle.DropDownList,
            TabIndex = 2
        };
        foreach (var format in availableFormats)
        {
            string label = format.ToString().ToLowerInvariant();
            if (format == PackArchiveFormat.SevenZip) label = "7z";
            _formatComboBox.Items.Add(new ComboItem<PackArchiveFormat>(label, format));
        }

        int selectedIndex = 0;
        for (int i = 0; i < _formatComboBox.Items.Count; i++)
        {
            if (_formatComboBox.Items[i] is ComboItem<PackArchiveFormat> item && item.Value == _lastFormat)
            {
                selectedIndex = i;
                break;
            }
        }
        _formatComboBox.SelectedIndex = selectedIndex;
        _formatComboBox.SelectedIndexChanged += (_, _) => SyncOutputExtension();

        var compressionLabel = new Label
        {
            Left = 188,
            Top = currentTop,
            Width = 160,
            Text = "圧縮率(&C):"
        };

        _compressionComboBox = new ComboBox
        {
            Left = 188,
            Top = currentTop + 22,
            Width = 160,
            DropDownStyle = ComboBoxStyle.DropDownList,
            TabIndex = 3
        };
        _compressionComboBox.Items.Add(new ComboItem<PackCompressionLevel>("store", PackCompressionLevel.Store));
        _compressionComboBox.Items.Add(new ComboItem<PackCompressionLevel>("fast", PackCompressionLevel.Fast));
        _compressionComboBox.Items.Add(new ComboItem<PackCompressionLevel>("normal", PackCompressionLevel.Normal));
        _compressionComboBox.Items.Add(new ComboItem<PackCompressionLevel>("maximum", PackCompressionLevel.Maximum));
        _compressionComboBox.SelectedIndex = _lastCompressionLevel switch
        {
            PackCompressionLevel.Store => 0,
            PackCompressionLevel.Fast => 1,
            PackCompressionLevel.Maximum => 3,
            _ => 2
        };

        var splitLabel = new Label
        {
            Left = 370,
            Top = currentTop,
            Width = 218,
            Text = "分割サイズ(&S):"
        };

        _splitComboBox = new ComboBox
        {
            Left = 370,
            Top = currentTop + 22,
            Width = 218,
            DropDownStyle = ComboBoxStyle.DropDownList,
            TabIndex = 4
        };
        _splitComboBox.Items.Add(new SplitComboItem("分割しない", null));
        _splitComboBox.Items.Add(new SplitComboItem("10 MB", "10m"));
        _splitComboBox.Items.Add(new SplitComboItem("100 MB", "100m"));
        _splitComboBox.Items.Add(new SplitComboItem("700 MB", "700m"));
        _splitComboBox.Items.Add(new SplitComboItem("カスタム...", CustomSplitValue));
        _splitComboBox.SelectedIndex = 0;
        _splitComboBox.SelectedIndexChanged += (_, _) => UpdateCustomSplitState();

        _customSplitTextBox = new TextBox
        {
            Left = 370,
            Top = currentTop + 54,
            Width = 218,
            Enabled = false,
            PlaceholderText = "custom: 10m / 1g",
            TabIndex = 5,
            TabStop = false
        };

        currentTop = _customSplitTextBox.Bottom + 8;

        _packEachFolderIndividuallyCheckBox = new CheckBox
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 572,
            AutoSize = true,
            Text = "個別圧縮(&I)",
            Enabled = canPackEachFolderIndividually,
            Checked = canPackEachFolderIndividually && defaultPackEachFolderIndividually
        };

        currentTop = _packEachFolderIndividuallyCheckBox.Bottom + 8;

        var hintLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = 572,
            Height = 34,
            ForeColor = SystemColors.GrayText,
            Text = hintText
        };
        hintLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(hintLabel, hintLabel.Width, 34);

        var okButton = new Button
        {
            Text = "OK",
            MinimumSize = new Size(80, 30),
            DialogResult = DialogResult.None,
            TabIndex = 6
        };
        okButton.Click += (_, _) => Confirm();

        var cancelButton = new Button
        {
            Text = "Cancel",
            MinimumSize = new Size(80, 30),
            DialogResult = DialogResult.Cancel,
            TabIndex = 7
        };

        Controls.Add(targetSummaryTitleLabel);
        Controls.Add(_targetSummaryLabel);
        Controls.Add(outputDirectoryLabel);
        Controls.Add(_outputDirectoryTextBox);
        Controls.Add(browseFolderButton);
        Controls.Add(browseTreeButton);
        Controls.Add(outputFileNameLabel);
        Controls.Add(_outputFileNameTextBox);
        Controls.Add(formatLabel);
        Controls.Add(_formatComboBox);
        Controls.Add(compressionLabel);
        Controls.Add(_compressionComboBox);
        Controls.Add(splitLabel);
        Controls.Add(_splitComboBox);
        Controls.Add(_customSplitTextBox);
        Controls.Add(_packEachFolderIndividuallyCheckBox);
        Controls.Add(hintLabel);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            this,
            new[] { okButton, cancelButton },
            hintLabel.Bottom,
            buttonGap: 10,
            contentGap: 16);

        AcceptButton = okButton;
        CancelButton = cancelButton;
        DialogKeyboardHelper.AttachOkCancelBindings(this, okButton, cancelButton);
        Helpers.DirectoryPathCompletionController.Attach(_outputDirectoryTextBox);

        Shown += (_, _) => BeginInvoke(new Action(FocusInitialControl));
    }

    public static PackRequest? Show(
        IWin32Window owner,
        string initialDirectory,
        string defaultArchiveName,
        string targetSummary,
        bool canPackEachFolderIndividually,
        bool defaultPackEachFolderIndividually,
        IReadOnlyList<PackArchiveFormat> availableFormats,
        string hintText,
        Func<IWin32Window, string, PackExistingArchiveAction>? confirmExistingArchiveOverwrite = null)
    {
        using var dialog = new PackDialog(
            initialDirectory,
            defaultArchiveName,
            targetSummary,
            canPackEachFolderIndividually,
            defaultPackEachFolderIndividually,
            availableFormats,
            hintText,
            confirmExistingArchiveOverwrite);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
    }

    private void BrowseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "出力先フォルダを選択してください",
            SelectedPath = ResolveOutputDirectoryForDialog(),
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ApplyOutputDirectory(dialog.SelectedPath);
        }
    }

    private void BrowseOutputDirectoryFromTree()
    {
        string? selected = TreeDialog.Show(ResolveOutputDirectoryForDialog());
        if (!string.IsNullOrWhiteSpace(selected))
        {
            ApplyOutputDirectory(selected);
        }
    }

    private void ApplyOutputDirectory(string selectedPath)
    {
        _outputDirectoryTextBox.Text = selectedPath;
    }

    private void SyncOutputExtension()
    {
        if (_updatingOutputExtension)
        {
            return;
        }

        string currentFileName = _outputFileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(currentFileName))
        {
            return;
        }

        try
        {
            _updatingOutputExtension = true;
            _outputFileNameTextBox.Text = ApplyFormatExtension(currentFileName, GetSelectedFormat());
        }
        finally
        {
            _updatingOutputExtension = false;
        }
    }

    private void UpdateCustomSplitState()
    {
        bool isCustom = _splitComboBox.SelectedItem is SplitComboItem item && item.Value == CustomSplitValue;
        _customSplitTextBox.Enabled = isCustom;
        _customSplitTextBox.TabStop = isCustom;
        if (!isCustom)
        {
            _customSplitTextBox.Text = string.Empty;
            return;
        }

        BeginInvoke(new Action(() =>
        {
            if (!_customSplitTextBox.Focused)
            {
                FocusAndSelect(_customSplitTextBox);
            }
        }));
    }

    private void Confirm()
    {
        string outputDirectory = _outputDirectoryTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            ShowValidationError("出力先フォルダを指定してください。", _outputDirectoryTextBox);
            return;
        }

        string outputFileName = _outputFileNameTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(outputFileName))
        {
            ShowValidationError("archive ファイル名を指定してください。", _outputFileNameTextBox);
            return;
        }

        string normalizedDirectory;
        try
        {
            normalizedDirectory = Path.GetFullPath(outputDirectory);
        }
        catch
        {
            ShowValidationError("出力先フォルダが不正です。", _outputDirectoryTextBox);
            return;
        }

        string effectiveOutputPath;
        try
        {
            effectiveOutputPath = ResolveEffectiveOutputArchivePath(normalizedDirectory, outputFileName, GetSelectedFormat());
        }
        catch
        {
            ShowValidationError("出力 archive パスが不正です。", _outputFileNameTextBox);
            return;
        }

        string? archiveParentDirectory = Path.GetDirectoryName(effectiveOutputPath);
        if (string.IsNullOrWhiteSpace(archiveParentDirectory) || !Directory.Exists(archiveParentDirectory))
        {
            ShowValidationError("archive の出力先フォルダが存在しません。", _outputFileNameTextBox);
            return;
        }

        string? splitSize = ResolveSplitSize();
        if (_splitComboBox.SelectedItem is SplitComboItem selectedSplit && selectedSplit.Value == CustomSplitValue && splitSize == null)
        {
            ShowValidationError("分割サイズは 10m / 700m / 1g のように入力してください。", _customSplitTextBox, selectAll: true);
            return;
        }

        if (!_packEachFolderIndividuallyCheckBox.Checked && File.Exists(effectiveOutputPath) && _confirmExistingArchiveOverwrite != null)
        {
            PackExistingArchiveAction action = _confirmExistingArchiveOverwrite(this, effectiveOutputPath);
            if (action == PackExistingArchiveAction.Cancel)
            {
                FocusAndSelect(_outputFileNameTextBox);
                return;
            }
        }

        Result = new PackRequest
        {
            OutputArchivePath = effectiveOutputPath,
            Format = GetSelectedFormat(),
            CompressionLevel = GetSelectedCompressionLevel(),
            SplitSize = splitSize,
            PackEachFolderIndividually = _packEachFolderIndividuallyCheckBox.Checked && _packEachFolderIndividuallyCheckBox.Enabled
        };

        _lastFormat = Result.Format;
        _lastCompressionLevel = Result.CompressionLevel;
        _lastOutputDirectory = normalizedDirectory;

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string ApplyFormatExtension(string archiveName, PackArchiveFormat format)
    {
        string extension = format switch
        {
            PackArchiveFormat.SevenZip => ".7z",
            PackArchiveFormat.Tar => ".tar",
            PackArchiveFormat.GZip => ".gz",
            PackArchiveFormat.BZip2 => ".bz2",
            PackArchiveFormat.Xz => ".xz",
            PackArchiveFormat.Wim => ".wim",
            _ => ".zip"
        };

        string? directoryPart = Path.GetDirectoryName(archiveName);
        string fileNamePart = Path.GetFileName(archiveName);
        string baseName = Path.GetFileNameWithoutExtension(fileNamePart);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "archive";
        }

        string resolvedFileName = baseName + extension;
        return string.IsNullOrWhiteSpace(directoryPart)
            ? resolvedFileName
            : Path.Combine(directoryPart, resolvedFileName);
    }

    private static string BuildInitialFileName(string defaultArchiveName)
    {
        return ApplyFormatExtension(Path.GetFileName(defaultArchiveName), _lastFormat);
    }

    private static string ResolveEffectiveOutputArchivePath(string normalizedDirectory, string outputFileName, PackArchiveFormat format)
    {
        string effectiveFileName = ApplyFormatExtension(outputFileName, format);
        if (Path.IsPathRooted(effectiveFileName))
        {
            return Path.GetFullPath(effectiveFileName);
        }

        return Path.GetFullPath(Path.Combine(normalizedDirectory, effectiveFileName));
    }

    private string ResolveOutputDirectoryForDialog()
    {
        string raw = _outputDirectoryTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw))
        {
            return _initialDirectory;
        }

        try
        {
            return Path.GetFullPath(raw);
        }
        catch
        {
            return _initialDirectory;
        }
    }

    private PackArchiveFormat GetSelectedFormat()
    {
        return (_formatComboBox.SelectedItem as ComboItem<PackArchiveFormat>)?.Value ?? PackArchiveFormat.Zip;
    }

    private PackCompressionLevel GetSelectedCompressionLevel()
    {
        return (_compressionComboBox.SelectedItem as ComboItem<PackCompressionLevel>)?.Value ?? PackCompressionLevel.Normal;
    }

    private string? ResolveSplitSize()
    {
        if (_splitComboBox.SelectedItem is not SplitComboItem item)
        {
            return null;
        }

        if (item.Value == null)
        {
            return null;
        }

        if (item.Value != CustomSplitValue)
        {
            return item.Value;
        }

        string raw = _customSplitTextBox.Text.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.EndsWith("kb", StringComparison.Ordinal))
        {
            return raw[..^2] + "k";
        }

        if (raw.EndsWith("mb", StringComparison.Ordinal))
        {
            return raw[..^2] + "m";
        }

        if (raw.EndsWith("gb", StringComparison.Ordinal))
        {
            return raw[..^2] + "g";
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\d+$"))
        {
            return raw + "m";
        }

        return System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\d+[bkmg]$")
            ? raw
            : null;
    }

    private void FocusInitialControl()
    {
        FocusAndSelect(_outputFileNameTextBox);
    }

    private void FocusAndSelect(Control control, bool selectAll = true)
    {
        ActiveControl = control;
        control.Focus();
        if (selectAll && control is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void ShowValidationError(string message, Control focusTarget, bool selectAll = true)
    {
        MessageBox.Show(this, message, "Pack", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        FocusAndSelect(focusTarget, selectAll);
    }

    private sealed class ComboItem<T>
    {
        public ComboItem(string text, T value)
        {
            Text = text;
            Value = value;
        }

        public string Text { get; }
        public T Value { get; }

        public override string ToString() => Text;
    }

    private sealed class SplitComboItem
    {
        public SplitComboItem(string text, string? value)
        {
            Text = text;
            Value = value;
        }

        public string Text { get; }
        public string? Value { get; }

        public override string ToString() => Text;
    }
}
