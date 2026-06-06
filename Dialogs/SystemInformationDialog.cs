using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class SystemInformationDialog : Form
{
    private readonly ComboBox _driveCombo;
    private readonly Label _summaryDriveValueLabel;
    private readonly Label _summaryCapacityValueLabel;
    private readonly Label _summaryUsedValueLabel;
    private readonly Label _summaryFreeValueLabel;
    private readonly Label _summaryUsageValueLabel;
    private readonly Label _summaryMediaValueLabel;
    private readonly Label _driveRootValueLabel;
    private readonly Label _driveTypeValueLabel;
    private readonly Label _driveMediaValueLabel;
    private readonly Label _fileSystemValueLabel;
    private readonly Label _totalCapacityValueLabel;
    private readonly Label _volumeLabelValueLabel;
    private readonly Label _serialNumberValueLabel;
    private readonly Label _usedCapacityValueLabel;
    private readonly Label _freeCapacityValueLabel;
    private readonly Label _usageRateValueLabel;
    private readonly Label _bytesPerSectorValueLabel;
    private readonly Label _bytesPerClusterValueLabel;
    private readonly Label _computerNameValueLabel;
    private readonly Label _userNameValueLabel;
    private readonly Label _windowsVersionValueLabel;
    private readonly Label _uptimeValueLabel;
    private readonly Label _osArchitectureValueLabel;
    private readonly Label _dotNetVersionValueLabel;
    private readonly Label _cpuNameValueLabel;
    private readonly Label _cpuPhysicalCoreValueLabel;
    private readonly Label _cpuLogicalProcessorValueLabel;
    private readonly Label _cpuClockValueLabel;
    private readonly Label _gpuNameValueLabel;
    private readonly Label _gpuMemoryValueLabel;
    private readonly Label _gpuDriverValueLabel;
    private readonly Label _memoryTotalValueLabel;
    private readonly Label _memoryUsedValueLabel;
    private readonly Label _memoryAvailableValueLabel;
    private readonly Label _memoryLoadValueLabel;
    private readonly SystemInformationSnapshot _snapshot;

    public SystemInformationDialog(string? currentPath)
    {
        var service = new SystemInformationService();
        _snapshot = service.CreateSnapshot();

        Text = "情報";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(980, 580);
        ClientSize = new Size(1100, 650);
        AutoScaleMode = AutoScaleMode.Font;

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12)
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
        Controls.Add(rootLayout);

        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.Controls.Add(topPanel, 0, 0);

        topPanel.Controls.Add(new Label
        {
            Text = "ドライブ:",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        }, 0, 0);

        _driveCombo = new ComboBox
        {
            Dock = DockStyle.Left,
            Width = 360,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _driveCombo.SelectedIndexChanged += (_, _) => UpdateDriveInformation();
        topPanel.Controls.Add(_driveCombo, 1, 0);

        var summaryBox = CreateGroupBox("ドライブ概要");
        summaryBox.Margin = new Padding(0, 0, 0, 10);
        rootLayout.Controls.Add(summaryBox, 0, 1);

        var summaryLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 2,
            Padding = new Padding(10, 8, 10, 8)
        };
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14F));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        summaryBox.Controls.Add(summaryLayout);

        _summaryDriveValueLabel = AddSummaryValue(summaryLayout, 0, "ドライブ");
        _summaryCapacityValueLabel = AddSummaryValue(summaryLayout, 1, "容量");
        _summaryUsedValueLabel = AddSummaryValue(summaryLayout, 2, "使用済み");
        _summaryFreeValueLabel = AddSummaryValue(summaryLayout, 3, "空き");
        _summaryUsageValueLabel = AddSummaryValue(summaryLayout, 4, "使用率");
        _summaryMediaValueLabel = AddSummaryValue(summaryLayout, 5, "メディア");
        var contentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0)
        };
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.Controls.Add(contentLayout, 0, 2);

        (_computerNameValueLabel,
         _userNameValueLabel,
         _windowsVersionValueLabel,
         _uptimeValueLabel,
         _osArchitectureValueLabel,
         _dotNetVersionValueLabel) = CreateSystemSummaryGroup(contentLayout, 0, 0);

        var bottomLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        bottomLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        contentLayout.Controls.Add(bottomLayout, 0, 1);

        var leftColumn = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 0)
        };
        var rightColumn = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 0, 0, 0)
        };
        bottomLayout.Controls.Add(leftColumn, 0, 0);
        bottomLayout.Controls.Add(rightColumn, 1, 0);

        (_cpuNameValueLabel,
         _cpuPhysicalCoreValueLabel,
         _cpuLogicalProcessorValueLabel,
         _cpuClockValueLabel,
         _gpuNameValueLabel,
         _gpuMemoryValueLabel,
         _gpuDriverValueLabel,
         _memoryTotalValueLabel,
         _memoryUsedValueLabel,
         _memoryAvailableValueLabel,
         _memoryLoadValueLabel) = CreateInfoGroup(
            leftColumn,
            "ハードウェア概要",
            "CPU名",
            "物理コア数",
            "論理プロセッサ数",
            "基準クロック",
            "GPU",
            "GPUメモリ",
            "GPUドライバ",
            "メモリ総量",
            "使用中メモリ",
            "使用可能メモリ",
            "メモリ使用率");

        (_driveRootValueLabel,
         _volumeLabelValueLabel,
         _driveTypeValueLabel,
         _driveMediaValueLabel,
         _fileSystemValueLabel,
         _serialNumberValueLabel,
         _totalCapacityValueLabel,
         _usedCapacityValueLabel,
         _freeCapacityValueLabel,
         _usageRateValueLabel,
         _bytesPerSectorValueLabel,
         _bytesPerClusterValueLabel) = CreateInfoGroup(
            rightColumn,
            "ドライブ詳細",
            "ドライブルート",
            "ボリュームラベル",
            "種別",
            "メディア",
            "ファイルシステム",
            "シリアル番号",
            "全容量",
            "使用容量",
            "空き容量",
            "使用率",
            "セクタサイズ",
            "クラスタサイズ");

        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rootLayout.Controls.Add(bottomPanel, 0, 3);

        var noteLabel = new Label
        {
            Text = "一部情報とHDD/SSD判定は、この環境で安全に取得できる範囲のみ表示しています。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        bottomPanel.Controls.Add(noteLabel, 0, 0);

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = 90,
            Height = 30,
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        bottomPanel.Controls.Add(okButton, 1, 0);

        AcceptButton = okButton;
        CancelButton = okButton;

        PopulateDriveItems(service.ResolveInitialDriveRoot(currentPath, _snapshot.Drives));
        UpdateStaticInformation();
    }

    private void PopulateDriveItems(string? initialRoot)
    {
        _driveCombo.Items.Clear();
        foreach (DriveInformationSnapshot drive in _snapshot.Drives)
        {
            _driveCombo.Items.Add(new DriveSelectionItem(drive.RootPath, drive.DisplayName));
        }

        if (_driveCombo.Items.Count == 0)
        {
            _driveCombo.Items.Add(new DriveSelectionItem(string.Empty, "(ドライブなし)"));
            _driveCombo.SelectedIndex = 0;
            UpdateDriveInformation();
            return;
        }

        int selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(initialRoot))
        {
            for (int i = 0; i < _driveCombo.Items.Count; i++)
            {
                if (_driveCombo.Items[i] is DriveSelectionItem item &&
                    string.Equals(item.RootPath, initialRoot, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        _driveCombo.SelectedIndex = selectedIndex;
    }

    private void UpdateStaticInformation()
    {
        UpdateMemoryInformation();

        _computerNameValueLabel.Text = NullToDash(_snapshot.System.ComputerName);
        _userNameValueLabel.Text = NullToDash(_snapshot.System.UserName);
        _windowsVersionValueLabel.Text = NullToDash(_snapshot.System.WindowsVersion);
        _uptimeValueLabel.Text = NullToDash(_snapshot.System.Uptime);
        _osArchitectureValueLabel.Text = NullToDash(_snapshot.System.OsBitness);
        _dotNetVersionValueLabel.Text = NullToDash(_snapshot.System.DotNetVersion);

        _cpuNameValueLabel.Text = NullToDash(_snapshot.Hardware.Cpu.Name);
        _cpuPhysicalCoreValueLabel.Text = NullToDash(_snapshot.Hardware.Cpu.PhysicalCoreCount);
        _cpuLogicalProcessorValueLabel.Text = NullToDash(_snapshot.Hardware.Cpu.LogicalProcessorCount);
        _cpuClockValueLabel.Text = NullToDash(_snapshot.Hardware.Cpu.ClockSummary);

        _gpuNameValueLabel.Text = NullToDash(_snapshot.Hardware.Gpu.Name);
        _gpuMemoryValueLabel.Text = NullToDash(_snapshot.Hardware.Gpu.Memory);
        _gpuDriverValueLabel.Text = NullToDash(_snapshot.Hardware.Gpu.DriverVersion);
    }

    private void UpdateMemoryInformation()
    {
        _memoryTotalValueLabel.Text = FormatBytes(_snapshot.Memory.TotalPhysicalBytes);
        _memoryUsedValueLabel.Text = FormatBytes(CalculateUsedMemoryBytes(_snapshot.Memory));
        _memoryAvailableValueLabel.Text = FormatBytes(_snapshot.Memory.AvailablePhysicalBytes);
        _memoryLoadValueLabel.Text = _snapshot.Memory.MemoryLoadPercent.HasValue
            ? $"{_snapshot.Memory.MemoryLoadPercent.Value}%"
            : "取得不可";
    }

    private void UpdateDriveInformation()
    {
        if (_driveCombo.SelectedItem is not DriveSelectionItem selection)
        {
            ClearDriveInformation();
            return;
        }

        DriveInformationSnapshot? drive = _snapshot.Drives.FirstOrDefault(d =>
            string.Equals(d.RootPath, selection.RootPath, StringComparison.OrdinalIgnoreCase));
        if (drive == null)
        {
            ClearDriveInformation();
            return;
        }

        _summaryDriveValueLabel.Text = NullToDash(drive.DisplayName);
        _summaryCapacityValueLabel.Text = FormatBytes(drive.TotalBytes);
        _summaryUsedValueLabel.Text = FormatBytes(drive.UsedBytes);
        _summaryFreeValueLabel.Text = FormatBytes(drive.FreeBytes);
        _summaryUsageValueLabel.Text = FormatUsagePercent(drive.UsedBytes, drive.TotalBytes);
        _summaryMediaValueLabel.Text = NullToDash(drive.MediaKind);

        _driveRootValueLabel.Text = NullToDash(drive.RootPath);
        _volumeLabelValueLabel.Text = NullToDash(drive.VolumeLabel);
        _driveTypeValueLabel.Text = NullToDash(drive.DriveType);
        _driveMediaValueLabel.Text = NullToDash(drive.MediaKind);
        _fileSystemValueLabel.Text = NullToDash(drive.FileSystem);
        _serialNumberValueLabel.Text = NullToDash(drive.SerialNumber);
        _totalCapacityValueLabel.Text = FormatBytes(drive.TotalBytes);
        _usedCapacityValueLabel.Text = FormatBytes(drive.UsedBytes);
        _freeCapacityValueLabel.Text = FormatBytes(drive.FreeBytes);
        _usageRateValueLabel.Text = FormatUsagePercent(drive.UsedBytes, drive.TotalBytes);
        _bytesPerSectorValueLabel.Text = drive.BytesPerSector.HasValue ? FormatBytes((ulong)drive.BytesPerSector.Value) : "取得不可";
        _bytesPerClusterValueLabel.Text = drive.BytesPerCluster.HasValue ? FormatBytes((ulong)drive.BytesPerCluster.Value) : "取得不可";

    }

    private void ClearDriveInformation()
    {
        foreach (Label label in new[]
                 {
                     _summaryDriveValueLabel, _summaryCapacityValueLabel, _summaryUsedValueLabel, _summaryFreeValueLabel, _summaryUsageValueLabel,
                     _summaryMediaValueLabel, _driveRootValueLabel, _volumeLabelValueLabel, _driveTypeValueLabel, _driveMediaValueLabel,
                     _fileSystemValueLabel, _serialNumberValueLabel, _totalCapacityValueLabel, _usedCapacityValueLabel,
                     _freeCapacityValueLabel, _usageRateValueLabel, _bytesPerSectorValueLabel, _bytesPerClusterValueLabel
                 })
        {
            label.Text = "-";
        }
    }

    private static ulong? CalculateUsedMemoryBytes(MemoryInformationSnapshot memory)
    {
        if (!memory.TotalPhysicalBytes.HasValue || !memory.AvailablePhysicalBytes.HasValue)
        {
            return null;
        }

        ulong total = memory.TotalPhysicalBytes.Value;
        ulong available = memory.AvailablePhysicalBytes.Value;
        return total >= available ? total - available : null;
    }

    private static string FormatBytes(long? value)
    {
        if (!value.HasValue)
        {
            return "取得不可";
        }

        return FormatBytes((ulong?)value.Value);
    }

    private static string FormatBytes(ulong? value)
    {
        if (!value.HasValue)
        {
            return "取得不可";
        }

        return FormatHumanReadableBytes(value.Value);
    }

    private static string FormatHumanReadableBytes(ulong value)
    {
        if (value < 1024)
        {
            return $"{value:#,0} byte";
        }

        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double size = value / 1024d;
        int unitIndex = 0;
        while (size >= 1024d && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        string format = size >= 100d ? "N0" : size >= 10d ? "N1" : "N2";
        return $"{size.ToString(format)} {units[unitIndex]}";
    }

    private static string FormatUsagePercent(long? usedBytes, long? totalBytes)
    {
        if (!usedBytes.HasValue || !totalBytes.HasValue || totalBytes.Value <= 0)
        {
            return "取得不可";
        }

        double percent = usedBytes.Value * 100d / totalBytes.Value;
        return $"{percent:N1}%";
    }

    private static string NullToDash(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static Label AddSummaryValue(TableLayoutPanel parent, int columnIndex, string title)
    {
        parent.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 8, 0)
        }, columnIndex, 0);

        var valueLabel = new Label
        {
            Text = "-",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Margin = new Padding(0, 2, 8, 0)
        };
        parent.Controls.Add(valueLabel, columnIndex, 1);
        return valueLabel;
    }

    private static (Label, Label, Label, Label, Label, Label) CreateSystemSummaryGroup(
        TableLayoutPanel parent,
        int column,
        int row)
    {
        GroupBox box = CreateGroupBox("システム概要");
        box.Margin = new Padding(0, 0, 0, 8);
        box.AutoSize = true;
        box.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
        for (int i = 0; i < 3; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        }

        // Row 0
        AddInlineName(layout, "コンピュータ名", 0, 0);
        Label computerName = AddInlineValue(layout, 1, 0);
        AddInlineName(layout, "ユーザー名", 2, 0);
        Label userName = AddInlineValue(layout, 3, 0);

        // Row 1
        AddInlineName(layout, "OS", 0, 1);
        Label windows = AddInlineValue(layout, 1, 1);
        AddInlineName(layout, ".NETバージョン", 2, 1);
        Label dotNetVersion = AddInlineValue(layout, 3, 1);

        // Row 2
        AddInlineName(layout, "OSビット数", 0, 2);
        Label osArchitecture = AddInlineValue(layout, 1, 2);
        AddInlineName(layout, "稼働時間", 2, 2);
        Label uptime = AddInlineValue(layout, 3, 2);

        box.Controls.Add(layout);
        parent.Controls.Add(box, column, row);
        return (computerName, userName, windows, uptime, osArchitecture, dotNetVersion);
    }

    private static void AddInlineName(TableLayoutPanel parent, string text, int column, int row)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            Margin = new Padding(0, 1, 8, 1)
        }, column, row);
    }

    private static Label AddInlineValue(TableLayoutPanel parent, int column, int row)
    {
        var value = new Label
        {
            Text = "-",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 1, 12, 1)
        };
        parent.Controls.Add(value, column, row);
        return value;
    }

    private static (Label, Label, Label, Label, Label, Label, Label, Label, Label, Label, Label, Label) CreateInfoGroup(
        Control parent,
        string title,
        string field1,
        string field2,
        string field3,
        string field4,
        string field5,
        string field6,
        string field7,
        string field8,
        string field9,
        string field10,
        string field11,
        string field12)
    {
        Label[] labels = CreateInfoGroupCore(parent, title, [field1, field2, field3, field4, field5, field6, field7, field8, field9, field10, field11, field12]);
        return (labels[0], labels[1], labels[2], labels[3], labels[4], labels[5], labels[6], labels[7], labels[8], labels[9], labels[10], labels[11]);
    }

    private static (Label, Label, Label, Label, Label, Label, Label, Label) CreateInfoGroup(
        Control parent,
        string title,
        string field1,
        string field2,
        string field3,
        string field4,
        string field5,
        string field6,
        string field7,
        string field8)
    {
        Label[] labels = CreateInfoGroupCore(parent, title, [field1, field2, field3, field4, field5, field6, field7, field8]);
        return (labels[0], labels[1], labels[2], labels[3], labels[4], labels[5], labels[6], labels[7]);
    }

    private static (Label, Label, Label, Label, Label, Label, Label, Label, Label, Label, Label) CreateInfoGroup(
        Control parent,
        string title,
        string field1,
        string field2,
        string field3,
        string field4,
        string field5,
        string field6,
        string field7,
        string field8,
        string field9,
        string field10,
        string field11)
    {
        Label[] labels = CreateInfoGroupCore(parent, title, [field1, field2, field3, field4, field5, field6, field7, field8, field9, field10, field11]);
        return (labels[0], labels[1], labels[2], labels[3], labels[4], labels[5], labels[6], labels[7], labels[8], labels[9], labels[10]);
    }

    private static (Label, Label, Label, Label, Label, Label, Label, Label, Label) CreateInfoGroup(
        Control parent,
        string title,
        string field1,
        string field2,
        string field3,
        string field4,
        string field5,
        string field6,
        string field7,
        string field8,
        string field9)
    {
        Label[] labels = CreateInfoGroupCore(parent, title, [field1, field2, field3, field4, field5, field6, field7, field8, field9]);
        return (labels[0], labels[1], labels[2], labels[3], labels[4], labels[5], labels[6], labels[7], labels[8]);
    }

    private static Label[] CreateInfoGroupCore(Control parent, string title, IReadOnlyList<string> fields)
    {
        GroupBox box = CreateGroupBox(title);
        box.Margin = new Padding(0, 0, 0, 8);
        box.Dock = DockStyle.Top;
        box.AutoSize = true;
        box.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var layout = CreateGroupLayout(fields);
        var labels = new Label[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            labels[i] = AddField(layout, fields[i], i);
        }

        box.Controls.Add(layout);
        parent.Controls.Add(box);
        box.BringToFront();
        return labels;
    }

    private static (Label, Label, Label, Label, Label, Label) CreateInfoGroup(
        TableLayoutPanel parent,
        int column,
        int row,
        string title,
        string field1,
        string field2,
        string field3,
        string field4,
        string field5,
        string field6)
    {
        GroupBox box = CreateGroupBox(title);
        string[] fields = [field1, field2, field3, field4, field5, field6];
        var layout = CreateGroupLayout(fields);

        Label v1 = AddField(layout, field1, 0);
        Label v2 = AddField(layout, field2, 1);
        Label v3 = AddField(layout, field3, 2);
        Label v4 = AddField(layout, field4, 3);
        Label v5 = AddField(layout, field5, 4);
        Label v6 = AddField(layout, field6, 5);
        box.Controls.Add(layout);
        parent.Controls.Add(box, column, row);
        return (v1, v2, v3, v4, v5, v6);
    }

    private static (Label, Label, Label, Label, Label, Label, Label, Label) CreateInfoGroup(
        TableLayoutPanel parent,
        int column,
        int row,
        string title,
        string field1,
        string field2,
        string field3,
        string field4,
        string field5,
        string field6,
        string field7,
        string field8)
    {
        GroupBox box = CreateGroupBox(title);
        string[] fields = [field1, field2, field3, field4, field5, field6, field7, field8];
        var layout = CreateGroupLayout(fields);

        Label v1 = AddField(layout, field1, 0);
        Label v2 = AddField(layout, field2, 1);
        Label v3 = AddField(layout, field3, 2);
        Label v4 = AddField(layout, field4, 3);
        Label v5 = AddField(layout, field5, 4);
        Label v6 = AddField(layout, field6, 5);
        Label v7 = AddField(layout, field7, 6);
        Label v8 = AddField(layout, field8, 7);
        box.Controls.Add(layout);
        parent.Controls.Add(box, column, row);
        return (v1, v2, v3, v4, v5, v6, v7, v8);
    }

    private static (Label, Label, Label, Label, Label) CreateInfoGroup(
        TableLayoutPanel parent,
        int column,
        int row,
        string title,
        string field1,
        string field2,
        string field3,
        string field4,
        string field5)
    {
        GroupBox box = CreateGroupBox(title);
        string[] fields = [field1, field2, field3, field4, field5];
        var layout = CreateGroupLayout(fields);

        Label v1 = AddField(layout, field1, 0);
        Label v2 = AddField(layout, field2, 1);
        Label v3 = AddField(layout, field3, 2);
        Label v4 = AddField(layout, field4, 3);
        Label v5 = AddField(layout, field5, 4);
        box.Controls.Add(layout);
        parent.Controls.Add(box, column, row);
        return (v1, v2, v3, v4, v5);
    }

    private static (Label, Label, Label, Label, Label, Label, Label, Label, Label, Label, Label) CreateInfoGroup(
        TableLayoutPanel parent,
        int column,
        int row,
        string title,
        string field1,
        string field2,
        string field3,
        string field4,
        string field5,
        string field6,
        string field7,
        string field8,
        string field9,
        string field10,
        string field11)
    {
        GroupBox box = CreateGroupBox(title);
        string[] fields = [field1, field2, field3, field4, field5, field6, field7, field8, field9, field10, field11];
        var layout = CreateGroupLayout(fields);

        Label v1 = AddField(layout, field1, 0);
        Label v2 = AddField(layout, field2, 1);
        Label v3 = AddField(layout, field3, 2);
        Label v4 = AddField(layout, field4, 3);
        Label v5 = AddField(layout, field5, 4);
        Label v6 = AddField(layout, field6, 5);
        Label v7 = AddField(layout, field7, 6);
        Label v8 = AddField(layout, field8, 7);
        Label v9 = AddField(layout, field9, 8);
        Label v10 = AddField(layout, field10, 9);
        Label v11 = AddField(layout, field11, 10);
        box.Controls.Add(layout);
        parent.Controls.Add(box, column, row);
        return (v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11);
    }

    private static GroupBox CreateGroupBox(string title)
    {
        return new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 8, 8)
        };
    }

    private static TableLayoutPanel CreateGroupLayout(IReadOnlyList<string> fields)
    {
        int rowCount = fields.Count;
        int labelColumnWidth = CalculateLabelColumnWidth(fields);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rowCount,
            Padding = new Padding(10, 8, 10, 8),
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelColumnWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < rowCount; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        }
        return layout;
    }

    private static int CalculateLabelColumnWidth(IReadOnlyList<string> fields)
    {
        int maxWidth = 0;
        foreach (string field in fields)
        {
            int width = TextRenderer.MeasureText(field + "　", SystemFonts.MessageBoxFont).Width;
            if (width > maxWidth)
            {
                maxWidth = width;
            }
        }

        int preferred = maxWidth + 8;
        return Math.Clamp(preferred, 148, 190);
    }

    private static Label AddField(TableLayoutPanel parent, string labelText, int rowIndex)
    {
        var nameLabel = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = false,
            Margin = new Padding(0, 1, 8, 1)
        };
        var valueLabel = new Label
        {
            Text = "-",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 1, 0, 1)
        };

        parent.Controls.Add(nameLabel, 0, rowIndex);
        parent.Controls.Add(valueLabel, 1, rowIndex);
        return valueLabel;
    }

    private sealed record DriveSelectionItem(string RootPath, string DisplayText)
    {
        public override string ToString() => DisplayText;
    }
}
