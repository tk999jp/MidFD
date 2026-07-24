using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MidFD.Controls;

namespace MidFD.Dialogs;

public enum AttributeAggregateState
{
    AllClear,
    AllSet,
    Mixed
}

public enum AttributeChangeAction
{
    Preserve,
    Set,
    Clear
}

public sealed record AttributeDialogRequest(
    string TargetLabel,
    AttributeAggregateState ReadOnlyState,
    AttributeAggregateState HiddenState,
    AttributeAggregateState SystemState,
    AttributeAggregateState ArchiveState,
    DateTime InitialLastWriteTime,
    DateTime InitialCreationTime,
    DateTime InitialLastAccessTime);

public sealed record AttributeDialogResult(
    AttributeChangeAction ReadOnlyAction,
    AttributeChangeAction HiddenAction,
    AttributeChangeAction SystemAction,
    AttributeChangeAction ArchiveAction,
    bool ChangeLastWriteTime,
    DateTime LastWriteTime,
    bool ChangeCreationTime,
    DateTime CreationTime,
    bool ChangeLastAccessTime,
    DateTime LastAccessTime,
    bool IncludeSubdirectories);

public static class AttributeDialog
{
    public static AttributeDialogResult? Show(AttributeDialogRequest request)
    {
        const int sideMargin = 12;
        const int topMargin = 12;

        using Form form = new()
        {
            ClientSize = new Size(580, 398),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "属性 / 日時変更",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = SystemColors.Control,
            ForeColor = SystemColors.ControlText,
            AutoScaleMode = AutoScaleMode.Font
        };

        int contentWidth = form.ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        Label lblTarget = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Text = $"対象: {request.TargetLabel}",
            Font = new Font("Meiryo UI", 9F, FontStyle.Bold),
            ForeColor = SystemColors.ControlText,
            AutoEllipsis = true
        };
        form.Controls.Add(lblTarget);
        currentTop = lblTarget.Bottom + 8;

        bool hasMixedAttribute = HasMixedAttribute(request);
        GroupBox grpAttribute = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = hasMixedAttribute ? 82 : 58,
            Text = "属性",
            ForeColor = SystemColors.ControlText
        };

        TableLayoutPanel tblAttr = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = hasMixedAttribute ? 2 : 1,
            Padding = new Padding(8, 0, 8, 2),
            BackColor = SystemColors.Control
        };
        for (int i = 0; i < 4; i++)
        {
            tblAttr.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        }
        if (hasMixedAttribute)
        {
            tblAttr.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            tblAttr.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        }

        CheckBox chkReadOnly = CreateThreeStateCheckBox("ReadOnly", request.ReadOnlyState);
        CheckBox chkHidden = CreateThreeStateCheckBox("Hidden", request.HiddenState);
        CheckBox chkSystem = CreateThreeStateCheckBox("System", request.SystemState);
        CheckBox chkArchive = CreateThreeStateCheckBox("Archive", request.ArchiveState);

        tblAttr.Controls.Add(chkReadOnly, 0, 0);
        tblAttr.Controls.Add(chkHidden, 1, 0);
        tblAttr.Controls.Add(chkSystem, 2, 0);
        tblAttr.Controls.Add(chkArchive, 3, 0);
        if (hasMixedAttribute)
        {
            Label lblMixedHint = new()
            {
                Text = "－ 混在（変更しない）",
                ForeColor = SystemColors.GrayText,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            tblAttr.Controls.Add(lblMixedHint, 0, 1);
            tblAttr.SetColumnSpan(lblMixedHint, 4);
            ToolTip mixedTip = new();
            mixedTip.SetToolTip(lblMixedHint, "対象間で属性が異なります。\n「－」のままなら各対象の現在の属性を変更しません。");
        }
        grpAttribute.Controls.Add(tblAttr);

        form.Controls.Add(grpAttribute);
        currentTop = grpAttribute.Bottom + 8;

        Label lblTimestampSection = new()
        {
            Left = sideMargin,
            Top = currentTop,
            AutoSize = true,
            Text = "日時",
            Font = new Font("Meiryo UI", 9F, FontStyle.Bold),
            ForeColor = SystemColors.ControlText
        };
        form.Controls.Add(lblTimestampSection);
        currentTop = lblTimestampSection.Bottom + 6;

        GroupBox grpBulkTimestamp = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 58,
            Text = "",
            ForeColor = SystemColors.ControlText
        };

        CheckBox chkBulkMode = new()
        {
            Text = "一括設定",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            AccessibleName = "一括設定",
            ForeColor = SystemColors.ControlText
        };

        DateTime bulkInitialValue = (request.InitialLastWriteTime == request.InitialCreationTime &&
                                     request.InitialCreationTime == request.InitialLastAccessTime)
            ? request.InitialLastWriteTime
            : request.InitialLastWriteTime;

        TableLayoutPanel tblBulk = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8, 2, 8, 2),
            BackColor = SystemColors.Control
        };
        tblBulk.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        tblBulk.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        tblBulk.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));

        SegmentedDateTimeEditor editorBulk = new() { Value = bulkInitialValue, Anchor = AnchorStyles.Left, Margin = new Padding(4, 0, 0, 0) };


        tblBulk.Controls.Add(chkBulkMode, 0, 0);
        tblBulk.Controls.Add(editorBulk, 1, 0);
        grpBulkTimestamp.Controls.Add(tblBulk);
        form.Controls.Add(grpBulkTimestamp);
        currentTop = grpBulkTimestamp.Bottom + 6;

        GroupBox grpIndividualTimestamp = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 132,
            Text = "個別設定",
            ForeColor = SystemColors.ControlText
        };

        TableLayoutPanel tblIndividual = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(8, 2, 8, 2),
            BackColor = SystemColors.Control
        };
        tblIndividual.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        tblIndividual.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        for (int r = 0; r < 3; r++)
        {
            tblIndividual.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        }

        CheckBox chkWrite = new() { Text = "更新日時", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.ControlText };
        SegmentedDateTimeEditor editorWrite = new() { Value = request.InitialLastWriteTime, Anchor = AnchorStyles.Left, Margin = new Padding(4, 0, 0, 0) };

        CheckBox chkCreate = new() { Text = "作成日時", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.ControlText };
        SegmentedDateTimeEditor editorCreate = new() { Value = request.InitialCreationTime, Anchor = AnchorStyles.Left, Margin = new Padding(4, 0, 0, 0) };

        CheckBox chkAccess = new() { Text = "最終アクセス", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.ControlText };
        SegmentedDateTimeEditor editorAccess = new() { Value = request.InitialLastAccessTime, Anchor = AnchorStyles.Left, Margin = new Padding(4, 0, 0, 0) };


        tblIndividual.Controls.Add(chkWrite, 0, 0);
        tblIndividual.Controls.Add(editorWrite, 1, 0);
        tblIndividual.Controls.Add(chkCreate, 0, 1);
        tblIndividual.Controls.Add(editorCreate, 1, 1);
        tblIndividual.Controls.Add(chkAccess, 0, 2);
        tblIndividual.Controls.Add(editorAccess, 1, 2);

        grpIndividualTimestamp.Controls.Add(tblIndividual);
        form.Controls.Add(grpIndividualTimestamp);
        currentTop = grpIndividualTimestamp.Bottom + 8;

        bool initializing = true;
        bool autoChecking = false;
        bool autoCheckingBulk = false;

        editorBulk.UserValueChanged += (_, _) =>
        {
            if (!initializing && !autoCheckingBulk && !chkBulkMode.Checked)
            {
                autoCheckingBulk = true;
                try
                {
                    chkBulkMode.Checked = true;
                }
                finally
                {
                    autoCheckingBulk = false;
                }
            }
        };

        void SetupIndividualRow(CheckBox chk, SegmentedDateTimeEditor editor, DateTime initialValue)
        {
            editor.UserValueChanged += (_, _) =>
            {
                if (!chkBulkMode.Checked)
                {
                    autoChecking = true;
                    chk.Checked = true;
                    autoChecking = false;
                }
            };

            chk.CheckedChanged += (_, _) =>
            {
                if (!initializing && !autoChecking && !chk.Checked)
                {
                    editor.Value = initialValue;
                }
            };
        }

        SetupIndividualRow(chkWrite, editorWrite, request.InitialLastWriteTime);
        SetupIndividualRow(chkCreate, editorCreate, request.InitialCreationTime);
        SetupIndividualRow(chkAccess, editorAccess, request.InitialLastAccessTime);

        void ApplyTimestampModeVisualState(bool bulkMode)
        {
            if (bulkMode)
            {
                grpBulkTimestamp.Enabled = true;
                editorBulk.Enabled = true;

                grpIndividualTimestamp.Enabled = false;
            }
            else
            {
                grpBulkTimestamp.Enabled = true;
                editorBulk.Enabled = true;

                grpIndividualTimestamp.Enabled = true;
                chkWrite.Enabled = true;
                chkCreate.Enabled = true;
                chkAccess.Enabled = true;

                editorWrite.Enabled = true;
                editorCreate.Enabled = true;
                editorAccess.Enabled = true;

            }

            form.Invalidate(true);
        }

        chkBulkMode.CheckedChanged += (_, _) =>
        {
            ApplyTimestampModeVisualState(chkBulkMode.Checked);
            if (chkBulkMode.Checked)
            {
                editorBulk.FocusSegment(SegmentKind.Year);
            }
        };

        ApplyTimestampModeVisualState(false);
        initializing = false;

        CheckBox chkRecursive = new()
        {
            Left = sideMargin + 2,
            Top = currentTop,
            Width = contentWidth - 4,
            Text = "サブディレクトリ以下も処理する",
            AutoSize = true,
            ForeColor = SystemColors.ControlText
        };
        form.Controls.Add(chkRecursive);
        currentTop = chkRecursive.Bottom + 12;

        Button btnOk = new()
        {
            Text = "OK",
            MinimumSize = new Size(80, 30),
            UseVisualStyleBackColor = true
        };

        Button btnCancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            MinimumSize = new Size(80, 30),
            UseVisualStyleBackColor = true
        };

        form.Controls.Add(btnOk);
        form.Controls.Add(btnCancel);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            new[] { btnOk, btnCancel },
            currentTop,
            buttonGap: 10,
            contentGap: 14);

        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        AttributeDialogResult? result = null;

        btnOk.Click += (_, _) =>
        {
            var readOnlyAction = ConvertCheckStateToAction(chkReadOnly.CheckState);
            var hiddenAction = ConvertCheckStateToAction(chkHidden.CheckState);
            var systemAction = ConvertCheckStateToAction(chkSystem.CheckState);
            var archiveAction = ConvertCheckStateToAction(chkArchive.CheckState);

            if (chkBulkMode.Checked)
            {
                if (!editorBulk.TryGetValue(out _, out var invalidSegment))
                {
                    ShowValidationError(form, "一括設定日時", invalidSegment, editorBulk);
                    return;
                }

                DateTime bulkValue = editorBulk.Value;
                result = new AttributeDialogResult(
                    readOnlyAction,
                    hiddenAction,
                    systemAction,
                    archiveAction,
                    true,
                    bulkValue,
                    true,
                    bulkValue,
                    true,
                    bulkValue,
                    chkRecursive.Checked);
            }
            else
            {
                if (chkWrite.Checked)
                {
                    if (!editorWrite.TryGetValue(out _, out var invalidSegment))
                    {
                        ShowValidationError(form, "更新日時", invalidSegment, editorWrite);
                        return;
                    }
                }

                if (chkCreate.Checked)
                {
                    if (!editorCreate.TryGetValue(out _, out var invalidSegment))
                    {
                        ShowValidationError(form, "作成日時", invalidSegment, editorCreate);
                        return;
                    }
                }

                if (chkAccess.Checked)
                {
                    if (!editorAccess.TryGetValue(out _, out var invalidSegment))
                    {
                        ShowValidationError(form, "最終アクセス日時", invalidSegment, editorAccess);
                        return;
                    }
                }

                result = new AttributeDialogResult(
                    readOnlyAction,
                    hiddenAction,
                    systemAction,
                    archiveAction,
                    chkWrite.Checked,
                    editorWrite.Value,
                    chkCreate.Checked,
                    editorCreate.Value,
                    chkAccess.Checked,
                    editorAccess.Value,
                    chkRecursive.Checked);
            }

            form.DialogResult = DialogResult.OK;
            form.Close();
        };

        if (form.ShowDialog() != DialogResult.OK)
        {
            return null;
        }

        return result;
    }

    private static CheckBox CreateThreeStateCheckBox(string text, AttributeAggregateState state)
    {
        var chk = state == AttributeAggregateState.Mixed
            ? new MixedAttributeCheckBox()
            : new CheckBox();
        chk.Text = text;
        chk.ThreeState = state == AttributeAggregateState.Mixed;
        chk.AutoCheck = state != AttributeAggregateState.Mixed;
        chk.AutoSize = true;
        chk.Anchor = AnchorStyles.Left;
        chk.ForeColor = SystemColors.ControlText;
        chk.CheckState = state switch
        {
            AttributeAggregateState.AllSet => CheckState.Checked,
            AttributeAggregateState.AllClear => CheckState.Unchecked,
            AttributeAggregateState.Mixed => CheckState.Indeterminate,
            _ => CheckState.Unchecked
        };

        if (state == AttributeAggregateState.Mixed)
        {
            chk.Click += (_, _) =>
            {
                chk.CheckState = chk.CheckState switch
                {
                    CheckState.Indeterminate => CheckState.Checked,
                    CheckState.Checked => CheckState.Unchecked,
                    _ => CheckState.Indeterminate
                };
            };
        }

        return chk;
    }

    private sealed class MixedAttributeCheckBox : CheckBox
    {
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && !e.Alt && !e.Control)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            base.OnKeyDown(e);
        }
    }

    private static bool HasMixedAttribute(AttributeDialogRequest request)
    {
        return request.ReadOnlyState == AttributeAggregateState.Mixed
            || request.HiddenState == AttributeAggregateState.Mixed
            || request.SystemState == AttributeAggregateState.Mixed
            || request.ArchiveState == AttributeAggregateState.Mixed;
    }

    private static AttributeChangeAction ConvertCheckStateToAction(CheckState state)
    {
        return state switch
        {
            CheckState.Checked => AttributeChangeAction.Set,
            CheckState.Unchecked => AttributeChangeAction.Clear,
            CheckState.Indeterminate => AttributeChangeAction.Preserve,
            _ => AttributeChangeAction.Preserve
        };
    }

    private static void ShowValidationError(Form owner, string rowName, SegmentKind segment, SegmentedDateTimeEditor editor)
    {
        string segmentName = segment switch
        {
            SegmentKind.Year => "年",
            SegmentKind.Month => "月",
            SegmentKind.Day => "日",
            SegmentKind.Hour => "時間",
            SegmentKind.Minute => "分",
            SegmentKind.Second => "秒",
            _ => ""
        };

        MessageBox.Show(
            owner,
            $"{rowName}の{segmentName}が不正です。",
            "属性 / 日時変更",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        editor.FocusSegment(segment);
    }
}
