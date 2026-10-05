using System;
using System.Drawing;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed record UnifiedFilterFindDialogResult(
    UnifiedFilterFindMode Mode,
    UnifiedFilterFindCriteria Criteria);

public sealed class UnifiedFilterFindDialog : Form
{
    private sealed class DetailsEditor
    {
        public GroupBox Group { get; } = new() { Text = "詳細条件" };
        public TextBox ExtensionText { get; } = new();
        public CheckBox FromCheck { get; } = new();
        public DateTimePicker FromDate { get; } = new();
        public DateTimePicker FromTime { get; } = new();
        public CheckBox ToCheck { get; } = new();
        public DateTimePicker ToDate { get; } = new();
        public DateTimePicker ToTime { get; } = new();
        public CheckBox GitUnignored { get; } = new();
        public Label ExtensionLabel { get; } = new() { Text = "対象拡張子(&E)（カンマ区切り）", AutoSize = true };
        public Label ExtensionExample { get; } = new() { Text = "例: cs, md, txt", AutoSize = true };

        public DetailsEditor(int top)
        {
            Group.SetBounds(12, top, 545, 195);
            Group.TabIndex = 6;

            ExtensionLabel.SetBounds(14, 19, 240, 22);
            ExtensionText.SetBounds(14, 42, 510, 24);
            ExtensionText.TabIndex = 0;
            ExtensionExample.SetBounds(14, 67, 220, 20);

            FromCheck.Text = "更新日時 以降";
            FromCheck.SetBounds(14, 91, 112, 24);
            FromCheck.TabIndex = 1;
            ConfigureDatePicker(FromDate, "yyyy/MM/dd", 132, 90, 122);
            FromDate.TabIndex = 2;
            ConfigureDatePicker(FromTime, "HH:mm", 260, 90, 78);
            FromTime.ShowUpDown = true;
            FromTime.TabIndex = 3;

            ToCheck.Text = "更新日時 以前";
            ToCheck.SetBounds(14, 120, 112, 24);
            ToCheck.TabIndex = 4;
            ConfigureDatePicker(ToDate, "yyyy/MM/dd", 132, 119, 122);
            ToDate.TabIndex = 5;
            ConfigureDatePicker(ToTime, "HH:mm", 260, 119, 78);
            ToTime.ShowUpDown = true;
            ToTime.TabIndex = 6;

            GitUnignored.Text = "Gitで無視されていない項目のみ";
            GitUnignored.SetBounds(14, 151, 300, 24);
            GitUnignored.TabIndex = 7;
            Group.Controls.AddRange([
                ExtensionLabel, ExtensionText, ExtensionExample,
                FromCheck, FromDate, FromTime, ToCheck, ToDate, ToTime, GitUnignored
            ]);
            FromCheck.CheckedChanged += (_, _) => UpdateDateState();
            ToCheck.CheckedChanged += (_, _) => UpdateDateState();
        }

        public void LayoutForContent(int top)
        {
            int gap = Math.Max(4, Group.Font.Height / 4);
            Group.Top = top;
            ExtensionLabel.Top = 19;
            ExtensionText.Top = ExtensionLabel.Bottom + gap;
            ExtensionExample.Top = ExtensionText.Bottom + gap;

            int fromTop = ExtensionExample.Bottom + gap;
            FromCheck.Top = fromTop;
            FromDate.Top = fromTop;
            FromTime.Top = fromTop;

            int toTop = Math.Max(FromCheck.Bottom, Math.Max(FromDate.Bottom, FromTime.Bottom)) + gap;
            ToCheck.Top = toTop;
            ToDate.Top = toTop;
            ToTime.Top = toTop;

            int gitTop = Math.Max(ToCheck.Bottom, Math.Max(ToDate.Bottom, ToTime.Bottom)) + gap;
            GitUnignored.Top = gitTop;
            Group.Height = GitUnignored.Bottom + Math.Max(8, gap);
        }

        public void LoadState(TabFilterLockState state)
        {
            ExtensionText.Text = state.ExtensionText ?? string.Empty;
            FromCheck.Checked = state.ModifiedFromLocal.HasValue;
            FromDate.Value = state.ModifiedFromLocal ?? DateTime.Now;
            FromTime.Value = state.ModifiedFromLocal ?? DateTime.Now;
            ToCheck.Checked = state.ModifiedToLocal.HasValue;
            ToDate.Value = state.ModifiedToLocal ?? DateTime.Now;
            ToTime.Value = state.ModifiedToLocal ?? DateTime.Now;
            GitUnignored.Checked = state.GitUnignoredOnly;
            UpdateDateState();
        }

        public void Clear()
        {
            ExtensionText.Clear();
            FromCheck.Checked = false;
            ToCheck.Checked = false;
            GitUnignored.Checked = false;
        }

        public bool TryCreateState(out TabFilterLockState state, out string? error)
        {
            DateTime? from = ReadDate(FromCheck.Checked, FromDate, FromTime);
            DateTime? to = ReadDate(ToCheck.Checked, ToDate, ToTime);
            if (from.HasValue && to.HasValue && from > to)
            {
                state = new TabFilterLockState();
                error = "更新日時の「以降」が「以前」より後になっています。";
                return false;
            }

            string extensionText = ExtensionText.Text.Trim();
            state = new TabFilterLockState
            {
                Enabled = !string.IsNullOrWhiteSpace(extensionText) || from.HasValue || to.HasValue || GitUnignored.Checked,
                ExtensionText = extensionText,
                IncludeExtensions = TabFilterLockState.NormalizeExtensions(extensionText),
                ModifiedFromLocal = from,
                ModifiedToLocal = to,
                GitUnignoredOnly = GitUnignored.Checked
            };
            error = null;
            return true;
        }

        private void UpdateDateState()
        {
            FromDate.Enabled = FromCheck.Checked;
            FromTime.Enabled = FromCheck.Checked;
            ToDate.Enabled = ToCheck.Checked;
            ToTime.Enabled = ToCheck.Checked;
        }

        private static void ConfigureDatePicker(DateTimePicker picker, string format, int left, int top, int width)
        {
            picker.Format = DateTimePickerFormat.Custom;
            picker.CustomFormat = format;
            picker.SetBounds(left, top, width, 24);
        }

        private static DateTime? ReadDate(bool enabled, DateTimePicker date, DateTimePicker time)
        {
            if (!enabled) return null;
            DateTime d = date.Value;
            DateTime t = time.Value;
            return new DateTime(d.Year, d.Month, d.Day, t.Hour, t.Minute, 0);
        }
    }

    private readonly TabControl _pages = new() { TabStop = false };
    private readonly TabPage _filterPage = new("フィルタ(&F)");
    private readonly TabPage _searchPage = new("検索(&S)");
    private readonly TextBox _filterNamePattern = new();
    private readonly CheckBox _filterNameRegex = new();
    private readonly CheckBox _filterNameCaseSensitive = new();
    private readonly TextBox _searchNamePattern = new();
    private readonly CheckBox _searchRegex = new();
    private readonly CheckBox _searchNameCaseSensitive = new();
    private readonly TextBox _searchContentPattern = new();
    private readonly ComboBox _searchEncoding = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _searchEncodingLabel = new() { Text = "文字コード", AutoSize = true };
    private readonly CheckBox _searchContentCaseSensitive = new();
    private readonly DetailsEditor _filterDetails = new(top: 105);
    private readonly DetailsEditor _searchDetails = new(top: 210);
    private readonly Button _clearButton = new();
    private readonly Button _executeButton = new();
    private readonly Button _cancelButton = new();
    private readonly Label _rootLabel = new();

    public UnifiedFilterFindDialogResult? Result { get; private set; }

    public UnifiedFilterFindDialog(UnifiedFilterFindInteractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Text = "フィルタ / 検索";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 520);

        _rootLabel.Text = $"対象: {request.RootPath}";
        _rootLabel.AutoEllipsis = true;
        _rootLabel.SetBounds(16, 12, 585, 22);

        _pages.SetBounds(16, 42, 585, 412);
        _pages.TabIndex = 0;
        _pages.Controls.AddRange([_filterPage, _searchPage]);
        BuildFilterPage(request.CurrentCriteria);
        BuildSearchPage(request.PreserveSearchDraft ? request.CurrentCriteria : null);
        _pages.SelectedTab = request.InitialMode == UnifiedFilterFindMode.FilterCurrentTab ? _filterPage : _searchPage;
        _pages.SelectedIndexChanged += (_, _) =>
        {
            UpdateExecuteButtonText();
            if (Visible) FocusInitialInput();
        };

        _clearButton.Text = "条件をクリア";
        _clearButton.SetBounds(16, 472, 110, 30);
        _clearButton.TabIndex = 1;
        _clearButton.Click += (_, _) => ClearActiveDraft();
        _executeButton.Text = "実行";
        _executeButton.SetBounds(420, 472, 80, 30);
        _executeButton.TabIndex = 2;
        _executeButton.Click += (_, _) => Accept();
        _cancelButton.Text = "キャンセル";
        _cancelButton.SetBounds(510, 472, 90, 30);
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.TabIndex = 3;

        Controls.AddRange([_rootLabel, _pages, _clearButton, _executeButton, _cancelButton]);
        AcceptButton = _executeButton;
        CancelButton = _cancelButton;
        UpdateExecuteButtonText();
        Shown += (_, _) => BeginInvoke(new Action(() =>
        {
            if (!IsDisposed && Visible) FocusInitialInput();
        }));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyContentDerivedLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyContentDerivedLayout();
    }

    internal void ApplyContentDerivedLayout()
    {
        if (IsDisposed)
        {
            return;
        }

        _pages.CreateControl();
        LayoutPageChildren();
        _pages.PerformLayout();
        int displayHeight = _pages.DisplayRectangle.Height;
        if (displayHeight <= 0)
        {
            return;
        }

        int requiredPageContentHeight = new[] { _filterPage, _searchPage }
            .Select(page => page.Controls.Cast<Control>()
                .Select(control => control.Bottom + control.Margin.Bottom)
                .DefaultIfEmpty(0)
                .Max() + page.Padding.Bottom)
            .Max();
        int tabHeaderAndBorderHeight = Math.Max(0, _pages.Height - displayHeight);
        _pages.Height = requiredPageContentHeight + tabHeaderAndBorderHeight;

        int footerGap = Math.Max(8, Font.Height);
        int footerButtonHeight = new[] { _clearButton, _executeButton, _cancelButton }
            .Max(button => button.Height);
        int rightMargin = Math.Max(Font.Height, ClientSize.Width - _cancelButton.Right);
        int horizontalGap = Math.Max(6, _cancelButton.Left - _executeButton.Right);
        _cancelButton.Left = ClientSize.Width - rightMargin - _cancelButton.Width;
        _executeButton.Left = _cancelButton.Left - horizontalGap - _executeButton.Width;

        int footerTop = _pages.Bottom + footerGap;
        _clearButton.Top = footerTop;
        _executeButton.Top = footerTop;
        _cancelButton.Top = footerTop;
        int bottomMargin = Math.Max(Font.Height, ClientSize.Height - Math.Max(_clearButton.Bottom, Math.Max(_executeButton.Bottom, _cancelButton.Bottom)));
        ClientSize = new Size(ClientSize.Width, footerTop + footerButtonHeight + bottomMargin);
    }

    private void LayoutPageChildren()
    {
        int gap = Math.Max(4, Font.Height / 4);
        Label filterNameLabel = _filterPage.Controls.OfType<Label>().First(label => label.Text == "ファイル名(&N)");
        _filterNamePattern.Top = filterNameLabel.Bottom + gap;
        _filterNameRegex.Top = _filterNamePattern.Top;
        _filterNameCaseSensitive.Top = Math.Max(_filterNamePattern.Bottom, _filterNameRegex.Bottom) + gap;
        _filterDetails.LayoutForContent(_filterNameCaseSensitive.Bottom + gap);

        Label searchNameLabel = _searchPage.Controls.OfType<Label>().First(label => label.Text == "ファイル名(&N)");
        Label searchContentLabel = _searchPage.Controls.OfType<Label>().First(label => label.Text == "ファイル内容(&C)");
        _searchNamePattern.Top = searchNameLabel.Bottom + gap;
        searchContentLabel.Top = _searchNamePattern.Bottom + gap;
        _searchContentPattern.Top = searchContentLabel.Bottom + gap;
        _searchRegex.Top = _searchContentPattern.Bottom + gap;
        _searchNameCaseSensitive.Top = _searchRegex.Bottom + gap;
        _searchContentCaseSensitive.Top = _searchNameCaseSensitive.Bottom + gap;
        _searchEncodingLabel.Top = _searchContentCaseSensitive.Bottom + gap;
        _searchEncoding.Top = _searchEncodingLabel.Top;
        _searchDetails.LayoutForContent(Math.Max(_searchEncoding.Bottom, _searchEncodingLabel.Bottom) + gap);
    }

    public static UnifiedFilterFindDialogResult? Show(
        IWin32Window owner,
        UnifiedFilterFindInteractionRequest request)
    {
        using var dialog = new UnifiedFilterFindDialog(request);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
    }

    private void BuildFilterPage(UnifiedFilterFindCriteria currentCriteria)
    {
        var nameLabel = new Label { Text = "ファイル名(&N)", AutoSize = true };
        nameLabel.SetBounds(12, 18, 110, 22);
        _filterNamePattern.SetBounds(12, 41, 425, 24);
        _filterNamePattern.TabIndex = 0;
        _filterNamePattern.Text = currentCriteria.NamePattern ?? string.Empty;
        _filterNameRegex.Text = "正規表現";
        _filterNameRegex.SetBounds(444, 41, 110, 24);
        _filterNameRegex.TabIndex = 1;
        _filterNameRegex.Checked = currentCriteria.NameUseRegex;
        _filterNameCaseSensitive.Text = "大文字小文字を区別";
        _filterNameCaseSensitive.SetBounds(12, 69, 220, 24);
        _filterNameCaseSensitive.TabIndex = 2;
        _filterNameCaseSensitive.Checked = currentCriteria.NameCaseSensitive;
        // Browser tab filters have no saved case-sensitivity property.
        _filterNameCaseSensitive.Enabled = false;
        _filterDetails.Group.TabIndex = 3;
        _filterDetails.LoadState(currentCriteria.DetailFilter ?? new TabFilterLockState());
        _filterPage.Controls.AddRange([nameLabel, _filterNamePattern, _filterNameRegex, _filterNameCaseSensitive, _filterDetails.Group]);
    }

    private void BuildSearchPage(UnifiedFilterFindCriteria? searchDraft)
    {
        var nameLabel = new Label { Text = "ファイル名(&N)", AutoSize = true };
        nameLabel.SetBounds(12, 10, 110, 22);
        _searchNamePattern.SetBounds(12, 33, 425, 24);
        _searchNamePattern.TabIndex = 0;
        _searchNamePattern.Text = searchDraft?.NamePattern ?? string.Empty;

        var contentLabel = new Label { Text = "ファイル内容(&C)", AutoSize = true };
        contentLabel.SetBounds(12, 66, 120, 22);
        _searchContentPattern.SetBounds(12, 89, 425, 24);
        _searchContentPattern.TabIndex = 1;
        _searchContentPattern.Text = searchDraft?.ContentPattern ?? string.Empty;
        _searchRegex.Text = "正規表現";
        _searchRegex.SetBounds(12, 120, 110, 24);
        _searchRegex.TabIndex = 2;
        _searchRegex.Checked = searchDraft?.NameUseRegex == true || searchDraft?.ContentUseRegex == true;
        _searchNameCaseSensitive.Text = "ファイル名の大文字小文字を区別";
        _searchNameCaseSensitive.SetBounds(12, 149, 280, 24);
        _searchNameCaseSensitive.TabIndex = 3;
        _searchNameCaseSensitive.Checked = searchDraft?.NameCaseSensitive == true;
        _searchContentCaseSensitive.Text = "内容の大文字小文字を区別";
        _searchContentCaseSensitive.SetBounds(12, 178, 280, 24);
        _searchContentCaseSensitive.TabIndex = 4;
        _searchContentCaseSensitive.Checked = searchDraft?.ContentCaseSensitive == true;
        _searchEncodingLabel.SetBounds(12, 207, 90, 24);
        _searchEncoding.SetBounds(110, 207, 190, 24);
        _searchEncoding.TabIndex = 5;
        _searchEncoding.Items.AddRange(["自動", "UTF-8", "Shift_JIS", "UTF-16 LE", "UTF-16 BE"]);
        _searchEncoding.SelectedIndex = (int)(searchDraft?.ContentEncoding ?? ContentSearchEncodingMode.Auto);

        // Normal Search begins with independent drafts; a Search rerun can restore its own draft.
        _searchDetails.Group.TabIndex = 6;
        _searchDetails.LoadState(searchDraft?.DetailFilter ?? new TabFilterLockState());
        _searchPage.Controls.AddRange([
            nameLabel, _searchNamePattern, contentLabel, _searchContentPattern,
            _searchRegex, _searchNameCaseSensitive, _searchContentCaseSensitive,
            _searchEncodingLabel, _searchEncoding, _searchDetails.Group
        ]);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        => HandleDialogShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    internal bool HandleDialogShortcut(Keys keyData)
    {
        if ((keyData & Keys.Alt) != Keys.Alt) return false;
        Keys key = keyData & Keys.KeyCode;
        switch (key)
        {
            case Keys.F:
                _pages.SelectedTab = _filterPage;
                FocusControl(_filterNamePattern);
                return true;
            case Keys.S:
                _pages.SelectedTab = _searchPage;
                FocusControl(_searchNamePattern);
                return true;
            case Keys.C:
                _pages.SelectedTab = _searchPage;
                FocusControl(_searchContentPattern);
                return true;
            case Keys.N:
                FocusControl(_pages.SelectedTab == _filterPage ? _filterNamePattern : _searchNamePattern);
                return true;
            case Keys.E:
                FocusControl((_pages.SelectedTab == _filterPage ? _filterDetails : _searchDetails).ExtensionText);
                return true;
            default:
                return false;
        }
    }

    private void FocusInitialInput()
    {
        if (_pages.SelectedTab == _filterPage) FocusControl(_filterNamePattern);
        else FocusControl(_searchNamePattern);
    }

    private static void FocusControl(Control control)
    {
        if (control.Enabled && control.CanFocus) control.Focus();
    }

    private void UpdateExecuteButtonText()
        => _executeButton.Text = _pages.SelectedTab == _filterPage ? "適用" : "検索";

    internal static UnifiedFilterFindMode ResolveEffectiveMode(bool filterSelected, string? contentPattern)
        => filterSelected
            ? UnifiedFilterFindMode.FilterCurrentTab
            : string.IsNullOrWhiteSpace(contentPattern)
                ? UnifiedFilterFindMode.FindNamesRecursively
                : UnifiedFilterFindMode.SearchContentsRecursively;

    internal static bool TryValidateContentPattern(string? pattern, bool useRegex, out string? error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = null;
            return true;
        }
        return NamePatternMatcher.TryValidate(pattern, useRegex, out error);
    }

    private void ClearActiveDraft()
    {
        if (_pages.SelectedTab == _filterPage)
        {
            _filterNamePattern.Clear();
            _filterNameRegex.Checked = false;
            _filterNameCaseSensitive.Checked = false;
            _filterDetails.Clear();
            return;
        }

        _searchNamePattern.Clear();
        _searchRegex.Checked = false;
        _searchNameCaseSensitive.Checked = false;
        _searchContentPattern.Clear();
        _searchContentCaseSensitive.Checked = false;
        _searchEncoding.SelectedIndex = 0;
        _searchDetails.Clear();
    }

    private void Accept()
    {
        bool isFilter = _pages.SelectedTab == _filterPage;
        TextBox namePattern = isFilter ? _filterNamePattern : _searchNamePattern;
        bool nameUseRegex = isFilter
            ? _filterNameRegex.Checked
            : _searchRegex.Checked && !string.IsNullOrWhiteSpace(namePattern.Text);
        bool contentUseRegex = !isFilter
            && _searchRegex.Checked
            && !string.IsNullOrWhiteSpace(_searchContentPattern.Text);
        CheckBox nameCase = isFilter ? _filterNameCaseSensitive : _searchNameCaseSensitive;
        if (!NamePatternMatcher.TryValidate(namePattern.Text, nameUseRegex, out string? nameError))
        {
            MessageBox.Show(this, $"名前条件の正規表現が不正です。\n{nameError}", "フィルタ / 検索", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            FocusControl(namePattern);
            return;
        }

        UnifiedFilterFindMode mode = isFilter
            ? UnifiedFilterFindMode.FilterCurrentTab
            : ResolveEffectiveMode(false, _searchContentPattern.Text);
        if (mode == UnifiedFilterFindMode.SearchContentsRecursively
            && !TryValidateContentPattern(_searchContentPattern.Text, contentUseRegex, out string? contentError))
        {
            MessageBox.Show(this, $"内容検索の正規表現が不正です。\n{contentError}", "フィルタ / 検索", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            FocusControl(_searchContentPattern);
            return;
        }

        DetailsEditor details = isFilter ? _filterDetails : _searchDetails;
        if (!details.TryCreateState(out TabFilterLockState detailState, out string? detailError))
        {
            MessageBox.Show(this, detailError, "フィルタ / 検索", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new UnifiedFilterFindDialogResult(
            mode,
            new UnifiedFilterFindCriteria
            {
                NamePattern = namePattern.Text,
                NameUseRegex = nameUseRegex,
                NameCaseSensitive = nameCase.Checked,
                DetailFilter = detailState,
                ContentPattern = isFilter ? string.Empty : _searchContentPattern.Text,
                ContentUseRegex = contentUseRegex,
                ContentCaseSensitive = !isFilter && _searchContentCaseSensitive.Checked,
                ContentEncoding = isFilter ? ContentSearchEncodingMode.Auto : (ContentSearchEncodingMode)_searchEncoding.SelectedIndex
            });
        DialogResult = DialogResult.OK;
        Close();
    }
}
