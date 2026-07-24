using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MidFD.Configuration;
using MidFD.Models;
using MidFD.Services;
using MidFD.Services.TrashManifestStore;

namespace MidFD.Dialogs;

public sealed class ManagedTrashDialog : Form
{
    private readonly AppSettings _settings;
    private readonly FileOperationUndoRedoService? _undoRedoService;
    private readonly DataGridView _grid;
    private readonly Label _summaryLabel;
    private readonly List<Button> _mutationButtons = new();
    private readonly Button _restoreButton;
    private readonly Button _deleteButton;

    public ManagedTrashDialog(AppSettings settings, FileOperationUndoRedoService? undoRedoService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _undoRedoService = undoRedoService;

        Text = "MidFD管理ゴミ箱の確認・管理";
        Size = new Size(1000, 600);
        MinimumSize = new Size(800, 450);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        Controls.Add(rootLayout);

        var topPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        rootLayout.Controls.Add(topPanel, 0, 0);

        _summaryLabel = new Label
        {
            Text = "退避項目を読み込んでいます...",
            Location = new Point(0, 4),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold)
        };
        topPanel.Controls.Add(_summaryLabel);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            ColumnHeadersVisible = true,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 28,
            MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            AutoGenerateColumns = false,
            EditMode = DataGridViewEditMode.EditProgrammatically,
            StandardTab = true,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            EnableHeadersVisualStyles = false
        };

        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(31, 78, 121);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(31, 78, 121);
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "状態",
            Name = "Availability",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "元のパス",
            Name = "OriginalPath",
            Width = 320,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "退避パス",
            Name = "TrashPath",
            Width = 320,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "削除日時",
            Name = "DeletedTime",
            Width = 140,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "期限切れ予定",
            Name = "ExpireTime",
            Width = 140,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "残り",
            Name = "Remaining",
            Width = 80,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "サイズ",
            Name = "Size",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        rootLayout.Controls.Add(_grid, 0, 1);

        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0)
        };
        rootLayout.Controls.Add(bottomPanel, 0, 2);

        _restoreButton = new Button { Text = "選択復元", Width = 110, Height = 30 };
        _restoreButton.Click += (s, e) => ExecuteSelectedRestore();
        bottomPanel.Controls.Add(_restoreButton);
        _mutationButtons.Add(_restoreButton);

        _deleteButton = new Button { Text = "選択完全削除", Width = 110, Height = 30 };
        _deleteButton.Click += (s, e) => ExecuteSelectedDelete();
        bottomPanel.Controls.Add(_deleteButton);
        _mutationButtons.Add(_deleteButton);

        var btnEmpty = new Button { Text = "すべて空にする", Width = 110, Height = 30 };
        btnEmpty.Click += (s, e) => ExecuteEmptyTrash();
        bottomPanel.Controls.Add(btnEmpty);
        _mutationButtons.Add(btnEmpty);

        var btnCleanMissing = new Button { Text = "欠損レコード掃除", Width = 120, Height = 30 };
        btnCleanMissing.Click += (s, e) => ExecuteCleanMissing();
        bottomPanel.Controls.Add(btnCleanMissing);
        _mutationButtons.Add(btnCleanMissing);

        var btnRefresh = new Button { Text = "更新", Width = 80, Height = 30 };
        btnRefresh.Click += (s, e) => LoadTrashRecords();
        bottomPanel.Controls.Add(btnRefresh);

        var btnClose = new Button { Text = "閉じる", DialogResult = DialogResult.Cancel, Width = 80, Height = 30 };
        btnClose.Click += (s, e) => Close();
        bottomPanel.Controls.Add(btnClose);

        CancelButton = btnClose;

        _grid.SelectionChanged += (_, _) => UpdateSelectionActions();
        Shown += (_, _) => LoadTrashRecords();
    }

    private void LoadTrashRecords()
    {
        bool available = MidFdManagedTrashService.IsAvailable;
        foreach (Button button in _mutationButtons) button.Enabled = available;
        if (!available)
        {
            _grid.Rows.Clear();
            _summaryLabel.Text = MidFdManagedTrashService.AvailabilityMessage;
            return;
        }

        try
        {
            _grid.Rows.Clear();
            var manifest = MidFdManagedTrashService.LoadManifest();
            int retentionDays = _settings.FileOperations.ManagedTrashUndoRetentionDays;

            int activeCount = 0;
            int missingCount = 0;
            int invalidCount = 0;
            long totalSize = 0;

            foreach (TrashManifestRecord record in manifest.Records)
            {
                if (record.Status != TrashRecordStatus.InTrash) continue;
                ManagedTrashRecordView view = MidFdManagedTrashService.GetRecordView(record);

                if (view.Availability == ManagedTrashRecordAvailability.Available)
                {
                    activeCount++;
                    if (!record.IsDirectory) totalSize += record.Size;
                }
                else if (view.Availability == ManagedTrashRecordAvailability.Missing) missingCount++;
                else invalidCount++;

                string sizeStr = record.IsDirectory ? "<DIR>" : FormatSize(record.Size);
                string deletedTime = record.DeletedAtUtc == default || record.DeletedAtUtc == DateTime.MinValue
                    ? "-"
                    : record.DeletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                string expireTimeStr = "-";
                string remainingDaysStr = "-";

                if (retentionDays > 0 && record.DeletedAtUtc != default && record.DeletedAtUtc != DateTime.MinValue)
                {
                    DateTime expireTime = record.DeletedAtUtc.ToLocalTime().AddDays(retentionDays);
                    expireTimeStr = expireTime.ToString("yyyy-MM-dd HH:mm:ss");
                    double remaining = (expireTime - DateTime.Now).TotalDays;
                    if (remaining < 0)
                    {
                        remainingDaysStr = "期限切れ";
                    }
                    else
                    {
                        remainingDaysStr = $"{(int)Math.Ceiling(remaining)}日";
                    }
                }
                else if (retentionDays <= 0)
                {
                    expireTimeStr = "無期限";
                    remainingDaysStr = "無期限";
                }

                int rowIndex = _grid.Rows.Add(
                    view.AvailabilityText,
                    record.OriginalPath,
                    record.TrashPath,
                    deletedTime,
                    expireTimeStr,
                    remainingDaysStr,
                    sizeStr
                );

                _grid.Rows[rowIndex].Tag = view;
            }

            _summaryLabel.Text = $"利用可能: {activeCount} 件 / 実体なし: {missingCount} 件 / パス不正: {invalidCount} 件 " +
                $"(ファイル総容量: {FormatSize(totalSize)})  [保持期限: {(retentionDays > 0 ? $"{retentionDays}日" : "無期限")}]";
            UpdateSelectionActions();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ゴミ箱データの取得に失敗しました。Error={ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateSelectionActions()
    {
        bool storeAvailable = MidFdManagedTrashService.IsAvailable;
        List<ManagedTrashRecordView> selected = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(static row => row.Tag)
            .OfType<ManagedTrashRecordView>()
            .ToList();
        _restoreButton.Enabled = storeAvailable && selected.Count > 0 && selected.All(static view => view.CanRestore);
        _deleteButton.Enabled = storeAvailable && selected.Count > 0 && selected.All(static view => view.CanDeletePhysicalItem);
    }

    private void ExecuteSelectedRestore()
    {
        var selectedRows = _grid.SelectedRows.Cast<DataGridViewRow>().ToList();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show("復元する項目を選択してください。", "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int successCount = 0;
        var errors = new List<string>();

        foreach (DataGridViewRow row in selectedRows)
        {
            if (row.Tag is not ManagedTrashRecordView view || !view.CanRestore)
            {
                errors.Add("復元できないrecordが選択されています。状態を確認してください。");
                continue;
            }
            TrashManifestRecord record = view.Record;

            try
            {
                var item = new FileOperationUndoRedoItem
                {
                    BeforePath = record.OriginalPath,
                    BeforeName = record.OriginalName,
                    RecycleBinPath = record.TrashPath,
                    RecycleBinDeletedAtUtc = record.DeletedAtUtc
                };

                MidFdManagedTrashService.RestoreFromTrash(item, skipStatusUpdate: false);
                successCount++;
            }
            catch (Exception ex)
            {
                errors.Add($"復元失敗: {record.OriginalName}\r\n原因: {ex.Message}");
            }
        }

        LoadTrashRecords();

        if (errors.Count > 0)
        {
            string errSummary = string.Join("\r\n\r\n", errors.Take(5));
            if (errors.Count > 5)
            {
                errSummary += $"\r\n\r\n他 {errors.Count - 5} 件のエラーがあります。";
            }
            MessageBox.Show($"{successCount} 件を復元しましたが、以下のエラーが発生しました。\r\n\r\n{errSummary}", "一部復元失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show($"{successCount} 件の項目を復元しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ExecuteSelectedDelete()
    {
        var selectedRows = _grid.SelectedRows.Cast<DataGridViewRow>().ToList();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show("削除する項目を選択してください。", "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var result = MessageBox.Show(
            $"選択された {selectedRows.Count} 件の項目を完全に物理削除します。この操作は取り消せません。\r\nよろしいですか？",
            "完全削除の確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning
        );

        if (result != DialogResult.Yes) return;

        int successCount = 0;
        foreach (DataGridViewRow row in selectedRows)
        {
            if (row.Tag is not ManagedTrashRecordView view || !view.CanDeletePhysicalItem) continue;
            TrashManifestRecord record = view.Record;

            try
            {
                MidFdManagedTrashService.DeleteFromTrashForever(record.TrashPath, _undoRedoService);
                successCount++;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[ManagedTrashDialog] Failed to delete item forever. path={record.TrashPath}, error={ex.Message}");
            }
        }

        LoadTrashRecords();
        MessageBox.Show($"{successCount} 件の項目を完全に削除しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExecuteEmptyTrash()
    {
        var result = MessageBox.Show(
            "MidFD管理ゴミ箱内のすべての項目を完全に物理削除します。この操作は取り消せません。\r\nよろしいですか？",
            "ゴミ箱を空にする確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning
        );

        if (result != DialogResult.Yes) return;

        try
        {
            MidFdManagedTrashService.EmptyTrash();
            LoadTrashRecords();
            MessageBox.Show("ゴミ箱を空にしました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ゴミ箱のクリアに失敗しました。Error={ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExecuteCleanMissing()
    {
        try
        {
            int missingCount = MidFdManagedTrashService.CleanMissingTrashRecords(_undoRedoService);
            LoadTrashRecords();
            MessageBox.Show($"{missingCount} 件の欠損レコードを掃除しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"欠損レコードの掃除に失敗しました。Error={ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F1} GB";
    }
}
