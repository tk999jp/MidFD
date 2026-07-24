using System.Runtime.InteropServices;
using System.Text;

namespace MidFD.Helpers;

internal enum CompletionMode
{
    Directory,
    History
}

internal sealed class DirectoryPathCompletionOptions
{
    public bool ShowOnTextChanged { get; init; } = true;
    public Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<List<string>>>? CustomCandidateProvider { get; init; }
    public bool UseNativeHistoryDropdown { get; init; }
    public Func<Point, bool>? IsInsideExternalControl { get; init; }
    public Action? OutsideClick { get; init; }
}

internal sealed class DirectoryPathCompletionController : IDisposable
{
    private static readonly PopupItem EmptyPopupItem = new(string.Empty, null);
    private readonly Control _control;
    private readonly ListBox _listBox;
    private readonly DirectoryPathCompletionPopupForm _popupForm;
    private readonly IEditorAdapter _editor;
    private readonly CompletionMessageFilter _messageFilter;
    private readonly List<Control> _hookedControls = new();
    private readonly DirectoryPathCompletionOptions _options;
    private string? _currentDirPath;
    private string? _initialTextBeforeTab;
    private string? _lastHandledText;
    private bool _isUpdating;
    private bool _isTabCycling;
    private CompletionMode _currentCompletionMode = CompletionMode.Directory;
    private const int MaxCandidates = 30;

    private bool _disposed;
    private bool _messageFilterRegistered;
    private CancellationTokenSource? _candidateCts;
    private int _candidateRequestVersion;

    private DirectoryPathCompletionController(Control control, DirectoryPathCompletionOptions options)
    {
        _control = control;
        _options = options;
        _editor = CreateEditorAdapter(control);

        _listBox = new NonSelectableListBox
        {
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            SelectionMode = SelectionMode.One,
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            Font = _control.Font,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = _control.Font.Height + 4,
            IntegralHeight = false
        };
        _listBox.DrawItem += ListBox_DrawItem;
        _listBox.MouseDown += ListBox_MouseDown;

        _popupForm = new DirectoryPathCompletionPopupForm(_listBox);
        _popupForm.VisibleChanged += PopupForm_VisibleChanged;

        _messageFilter = new CompletionMessageFilter(this);
        Application.AddMessageFilter(_messageFilter);
        _messageFilterRegistered = true;

        HookEvents(_control);
        if (_control is ComboBox cb)
        {
            cb.ControlAdded += ComboBox_ControlAdded;
            foreach (Control child in cb.Controls)
            {
                HookEvents(child);
            }
        }
    }

    private void ListBox_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        CommitSelection();
    }

    private void PopupForm_VisibleChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!_popupForm.Visible)
        {
            _listBox.Items.Clear();
            _isTabCycling = false;
            _initialTextBeforeTab = null;
        }
    }

    private void ComboBox_ControlAdded(object? sender, ControlEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Control != null)
        {
            HookEvents(e.Control);
        }
    }

    public static DirectoryPathCompletionController Attach(
        Control control,
        DirectoryPathCompletionOptions? options = null)
    {
        return new DirectoryPathCompletionController(control, options ?? new DirectoryPathCompletionOptions());
    }

    public void ShowHistoryCandidates()
    {
        if (_disposed)
        {
            return;
        }
        _ = UpdateCandidatesAsync(CompletionMode.History);
    }

    private string ControlText => _editor.Text;

    private bool IsPopupVisible => _popupForm.Visible;

    public bool IsCompletionPopupVisible => IsPopupVisible;

    public void CloseCompletionPopup()
    {
        ClosePopup();
    }

    private void SetControlText(string text)
    {
        _isUpdating = true;
        _lastHandledText = text;
        try
        {
            _editor.SetText(text);
            _editor.MoveCaretToEnd();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void Control_TextChanged(object? sender, EventArgs e)
    {
        if (_disposed || _isUpdating)
        {
            return;
        }

        string text = ControlText;
        if (text == _lastHandledText)
        {
            return;
        }

        _isTabCycling = false;
        _initialTextBeforeTab = null;
        _lastHandledText = text;

        if (!_options.ShowOnTextChanged)
        {
            try
            {
                _candidateCts?.Cancel();
            }
            catch
            {
                // Ignore
            }
            ClosePopup();
            return;
        }

        _ = UpdateCandidatesAsync();
    }

    private void Control_LostFocus(object? sender, EventArgs e)
    {
        if (_disposed || _control.IsDisposed || !_control.IsHandleCreated)
        {
            return;
        }

        _control.BeginInvoke(new Action(() =>
        {
            if (_disposed || _control.IsDisposed)
            {
                return;
            }

            _editor.RefreshHandle();
            if (!HasFocusWithinEditorOrPopup())
            {
                ClosePopup();
            }
        }));
    }

    private void Control_PreviewKeyDown(object? sender, PreviewKeyDownEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.KeyCode == Keys.Tab)
        {
            // ポップアップが表示されているか、あるいは入力があって補完が可能な場合は Tab を自前で処理する
            if (IsPopupVisible || !string.IsNullOrWhiteSpace(ControlText))
            {
                e.IsInputKey = true;
            }
            return;
        }

        if (e.KeyCode == Keys.Down && (e.Alt || e.Control))
        {
            if (_options.UseNativeHistoryDropdown)
            {
                e.IsInputKey = true;
                return;
            }
            e.IsInputKey = true;
            return;
        }

        if (!IsPopupVisible)
        {
            return;
        }

        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Escape)
        {
            e.IsInputKey = true;
        }
    }

    private async void Control_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.KeyCode == Keys.Tab)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            if (!IsPopupVisible || _currentCompletionMode == CompletionMode.History)
            {
                bool opened = await UpdateCandidatesAsync(CompletionMode.Directory);
                if (!opened || !IsPopupVisible || _disposed)
                {
                    return; // 候補なし
                }
            }

            int direction = e.Shift ? -1 : 1;
            CycleCompletion(direction);
            return;
        }

        if (e.KeyCode == Keys.Down && (e.Alt || e.Control))
        {
            if (_options.UseNativeHistoryDropdown)
            {
                return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            _ = UpdateCandidatesAsync(CompletionMode.History);
            return;
        }

        if (!HandlePopupKey(e.KeyCode))
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void ListBox_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Index < 0)
        {
            return;
        }

        bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        Color backColor = isSelected ? Color.SteelBlue : _listBox.BackColor;
        Color foreColor = isSelected ? Color.White : _listBox.ForeColor;

        using var brush = new SolidBrush(backColor);
        e.Graphics.FillRectangle(brush, e.Bounds);

        string text = (_listBox.Items[e.Index] as PopupItem)?.DisplayText ?? string.Empty;
        TextRenderer.DrawText(e.Graphics, text, _listBox.Font, e.Bounds, foreColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private void MoveSelection(int delta)
    {
        if (_disposed)
        {
            return;
        }

        int count = _listBox.Items.Count;
        if (count == 0)
        {
            return;
        }

        int next = _listBox.SelectedIndex + delta;
        if (next < 0)
        {
            next = count - 1;
        }
        else if (next >= count)
        {
            next = 0;
        }

        _listBox.SelectedIndex = next;
    }

    private void CycleCompletion(int direction)
    {
        if (_disposed)
        {
            return;
        }

        int count = _listBox.Items.Count;
        if (count <= 1)
        {
            return;
        }

        if (!_isTabCycling)
        {
            _isTabCycling = true;
            _initialTextBeforeTab = ControlText;

            // 最初はインクリメンタル検索用の空項目(index 0)を避けて index 1 から開始
            if (_listBox.SelectedIndex <= 0)
            {
                _listBox.SelectedIndex = 1;
            }
        }
        else
        {
            // インクリメンタル用の空項目(index 0)を除いて 1..count-1 の範囲で巡回
            int next = _listBox.SelectedIndex + direction;
            if (next < 1)
            {
                next = count - 1;
            }
            else if (next >= count)
            {
                next = 1;
            }
            _listBox.SelectedIndex = next;
        }

        if (_listBox.SelectedItem is PopupItem item && item.Value != null)
        {
            string result;
            if (item.IsFullPath)
            {
                result = item.Value;
            }
            else if (_currentDirPath != null)
            {
                result = Path.Combine(_currentDirPath, item.Value) + Path.DirectorySeparatorChar;
            }
            else
            {
                result = item.Value;
            }
            SetControlText(result);
        }
    }

    private static bool TryBuildCandidateQuery(string text, out string dirPath, out string filter)
    {
        dirPath = string.Empty;
        filter = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            if (text.Length == 2 && text[1] == ':' && char.IsLetter(text[0]))
            {
                dirPath = text + Path.DirectorySeparatorChar;
                filter = string.Empty;
            }
            else if (text.EndsWith(Path.DirectorySeparatorChar) || text.EndsWith(Path.AltDirectorySeparatorChar))
            {
                dirPath = text;
                filter = string.Empty;
            }
            else
            {
                dirPath = Path.GetDirectoryName(text) ?? string.Empty;
                filter = Path.GetFileName(text) ?? string.Empty;

                if (string.IsNullOrEmpty(dirPath) && text.Length >= 3 && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
                {
                    dirPath = text.Substring(0, 3);
                }
            }

            return !string.IsNullOrEmpty(dirPath);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> EnumerateDirectoryCandidates(
        string dirPath,
        string filter,
        CancellationToken token)
    {
        const int MaxRawCandidates = 200;

        var candidates = new List<string>(MaxCandidates);

        if (!Directory.Exists(dirPath))
        {
            return candidates;
        }

        foreach (string path in Directory.EnumerateDirectories(dirPath, filter + "*"))
        {
            token.ThrowIfCancellationRequested();

            string? name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(name))
            {
                candidates.Add(name);
            }

            if (candidates.Count >= MaxRawCandidates)
            {
                break;
            }
        }

        return candidates
            .OrderBy(static n => n, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
    }

    private async Task<bool> UpdateCandidatesAsync(CompletionMode mode = CompletionMode.Directory)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            _candidateCts?.Cancel();
            _candidateCts?.Dispose();
        }
        catch
        {
            // Ignore
        }

        _candidateCts = new CancellationTokenSource();
        CancellationToken token = _candidateCts.Token;

        _candidateRequestVersion++;
        int requestVersion = _candidateRequestVersion;
        string textSnapshot = ControlText;

        try
        {
            _currentCompletionMode = mode;
            List<PopupItem> popupItems = new();
            bool isCustomCandidatesLoaded = false;

            // 1. History Mode
            if (mode == CompletionMode.History && _options.CustomCandidateProvider != null)
            {
                List<string> customCandidates = await _options.CustomCandidateProvider(textSnapshot, token);
                if (token.IsCancellationRequested || _disposed)
                {
                    return false;
                }

                foreach (string c in customCandidates)
                {
                    popupItems.Add(new PopupItem(c, c) { IsFullPath = true });
                }
                isCustomCandidatesLoaded = true;
            }

            // 2. Directory Mode (Fallback to Local Directories)
            if (!isCustomCandidatesLoaded && popupItems.Count == 0)
            {
                if (!TryBuildCandidateQuery(textSnapshot, out string dirPath, out string filter))
                {
                    ClosePopup();
                    return false;
                }

                List<string> dirs = await Task.Run(() => EnumerateDirectoryCandidates(dirPath, filter, token), token);
                if (token.IsCancellationRequested || _disposed)
                {
                    return false;
                }

                _currentDirPath = dirPath;
                foreach (string dir in dirs)
                {
                    popupItems.Add(new PopupItem(dir, dir) { IsFullPath = false });
                }
            }
            else
            {
                _currentDirPath = null;
            }

            if (requestVersion != _candidateRequestVersion ||
                ControlText != textSnapshot ||
                _control.IsDisposed ||
                !_control.IsHandleCreated ||
                _popupForm.IsDisposed)
            {
                return false;
            }

            if (mode == CompletionMode.History)
            {
                if (popupItems.Count == 0)
                {
                    ClosePopup();
                    return false;
                }
            }
            else
            {
                if (popupItems.Count == 0 || (popupItems.Count == 1 && string.Equals(popupItems[0].Value, textSnapshot, StringComparison.OrdinalIgnoreCase)))
                {
                    ClosePopup();
                    return false;
                }
            }

            ShowPopup(popupItems, textSnapshot);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            ClosePopup();
            return false;
        }
    }

    private void ShowPopup(List<PopupItem> items, string textSnapshot)
    {
        if (_disposed)
        {
            return;
        }

        if (_currentCompletionMode == CompletionMode.History)
        {
            _listBox.BackColor = Color.FromArgb(225, 225, 225);
            _listBox.ForeColor = Color.FromArgb(30, 30, 30);
        }
        else
        {
            _listBox.BackColor = Color.FromArgb(40, 40, 40);
            _listBox.ForeColor = Color.White;
        }

        _listBox.BeginUpdate();
        _listBox.Items.Clear();
        _listBox.Items.Add(EmptyPopupItem);
        foreach (PopupItem item in items)
        {
            _listBox.Items.Add(item);
        }

        int selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(textSnapshot))
        {
            int exactIndex = items.FindIndex(item => string.Equals(item.Value, textSnapshot, StringComparison.OrdinalIgnoreCase));
            if (exactIndex >= 0)
            {
                selectedIndex = exactIndex + 1;
            }
            else
            {
                int prefixIndex = items.FindIndex(item => item.Value != null && item.Value.StartsWith(textSnapshot, StringComparison.OrdinalIgnoreCase));
                if (prefixIndex >= 0)
                {
                    selectedIndex = prefixIndex + 1;
                }
            }
        }

        _listBox.SelectedIndex = selectedIndex;
        _listBox.EndUpdate();

        int itemHeight = _listBox.ItemHeight;
        int visibleCount = Math.Min(_listBox.Items.Count, 10);
        int height = (itemHeight * visibleCount) + 2;
        Rectangle popupBounds = _editor.GetPopupBounds(_control.Width, height);
        Form? owner = _control.FindForm();
        _popupForm.ShowPopup(owner, popupBounds);
        _editor.RefreshHandle();
        _editor.EnsureFocus();
    }

    private void CommitSelection()
    {
        if (_disposed)
        {
            return;
        }

        if (_listBox.SelectedItem is PopupItem selectedItem && selectedItem.Value is string selected)
        {
            string result;
            if (selectedItem.IsFullPath)
            {
                result = selected;
            }
            else if (_currentDirPath != null)
            {
                result = Path.Combine(_currentDirPath, selected) + Path.DirectorySeparatorChar;
            }
            else
            {
                result = selected;
            }
            SetControlText(result);
            _ = UpdateCandidatesAsync();
            return;
        }

        ClosePopup();
        TriggerAcceptButton();
    }

    private void ClosePopup()
    {
        if (_popupForm.IsDisposed)
        {
            _currentDirPath = null;
            return;
        }

        if (_popupForm.Visible)
        {
            _popupForm.Hide();
        }

        _currentDirPath = null;
    }

    private void TriggerAcceptButton()
    {
        Form? owner = _control.FindForm();
        if (owner?.AcceptButton is IButtonControl button)
        {
            if (button is Control control && !control.Enabled)
            {
                return;
            }

            button.PerformClick();
        }
    }

    private bool HandlePopupKey(Keys keyCode)
    {
        if (_disposed || !IsPopupVisible)
        {
            return false;
        }

        switch (keyCode)
        {
            case Keys.Up:
                MoveSelection(-1);
                return true;
            case Keys.Down:
                MoveSelection(1);
                return true;
            case Keys.Enter:
                CommitSelection();
                return true;
            case Keys.Escape:
                ClosePopup();
                return true;
            default:
                return false;
        }
    }

    private bool HasFocusWithinEditorOrPopup()
    {
        if (_disposed)
        {
            return false;
        }

        _editor.RefreshHandle();
        if (_editor.HasFocus || _control.Focused || _hookedControls.Any(static c => c.Focused))
        {
            return true;
        }

        if (_popupForm.HasPopupFocus)
        {
            return true;
        }

        IntPtr focusedHandle = GetFocus();
        return _editor.ContainsHandle(focusedHandle) || IsOwnedHandle(focusedHandle, _control) || _popupForm.ContainsOwnedHandle(focusedHandle);
    }

    private void HookEvents(Control? target)
    {
        if (_disposed)
        {
            return;
        }

        if (target == null || _hookedControls.Contains(target))
        {
            return;
        }

        target.TextChanged += Control_TextChanged;
        target.KeyDown += Control_KeyDown;
        target.PreviewKeyDown += Control_PreviewKeyDown;
        target.LostFocus += Control_LostFocus;
        target.LocationChanged += Control_BoundsChanged;
        target.SizeChanged += Control_BoundsChanged;
        target.ParentChanged += Control_ParentChanged;
        target.VisibleChanged += Control_VisibleChanged;
        _hookedControls.Add(target);
    }

    private void Control_BoundsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!IsPopupVisible || _listBox.Items.Count == 0)
        {
            return;
        }

        int height = _popupForm.Height;
        Rectangle popupBounds = _editor.GetPopupBounds(_control.Width, height);
        _popupForm.Bounds = popupBounds;
    }

    private void Control_ParentChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (sender is not Control target)
        {
            return;
        }

        target.LocationChanged -= Control_BoundsChanged;
        target.SizeChanged -= Control_BoundsChanged;
        target.LocationChanged += Control_BoundsChanged;
        target.SizeChanged += Control_BoundsChanged;
    }

    private void Control_VisibleChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!_control.Visible)
        {
            ClosePopup();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _candidateCts?.Cancel();
            _candidateCts?.Dispose();
            _candidateCts = null;
        }
        catch
        {
            // Ignore
        }

        _disposed = true;

        if (_control is ComboBox cb)
        {
            cb.ControlAdded -= ComboBox_ControlAdded;
        }

        if (_listBox != null)
        {
            _listBox.DrawItem -= ListBox_DrawItem;
            _listBox.MouseDown -= ListBox_MouseDown;
        }

        if (_popupForm != null)
        {
            _popupForm.VisibleChanged -= PopupForm_VisibleChanged;
        }

        ClosePopup();

        foreach (Control control in _hookedControls)
        {
            control.TextChanged -= Control_TextChanged;
            control.KeyDown -= Control_KeyDown;
            control.PreviewKeyDown -= Control_PreviewKeyDown;
            control.LostFocus -= Control_LostFocus;
            control.LocationChanged -= Control_BoundsChanged;
            control.SizeChanged -= Control_BoundsChanged;
            control.ParentChanged -= Control_ParentChanged;
            control.VisibleChanged -= Control_VisibleChanged;
        }

        _hookedControls.Clear();

        if (_messageFilterRegistered && _messageFilter != null)
        {
            try
            {
                Application.RemoveMessageFilter(_messageFilter);
                _messageFilterRegistered = false;
            }
            catch
            {
                // Ignore
            }
        }

        if (_popupForm != null)
        {
            try
            {
                if (!_popupForm.IsDisposed)
                {
                    _popupForm.Dispose();
                }
            }
            catch
            {
                // Ignore
            }
        }
    }

    private static bool IsOwnedHandle(IntPtr handle, Control control)
    {
        if (handle == IntPtr.Zero || !control.IsHandleCreated)
        {
            return false;
        }

        return handle == control.Handle || IsChild(control.Handle, handle);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref COMBOBOXINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int EM_SETSEL = 0x00B1;

    [StructLayout(LayoutKind.Sequential)]
    private struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem;
        public RECT rcButton;
        public int stateButton;
        public IntPtr hwndCombo;
        public IntPtr hwndItem;
        public IntPtr hwndList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static IEditorAdapter CreateEditorAdapter(Control control)
    {
        return control switch
        {
            TextBoxBase textBox => new TextBoxEditorAdapter(textBox),
            ComboBox comboBox => new ComboBoxEditorAdapter(comboBox),
            _ => new ControlEditorAdapter(control)
        };
    }

    private sealed class CompletionMessageFilter : IMessageFilter
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private readonly DirectoryPathCompletionController _owner;

        public CompletionMessageFilter(DirectoryPathCompletionController owner)
        {
            _owner = owner;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (_owner._disposed)
            {
                return false;
            }

            bool isMouseDown = m.Msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_NCLBUTTONDOWN;
            if (!_owner.IsPopupVisible && !isMouseDown)
            {
                return false;
            }

            if (isMouseDown && _owner._options.OutsideClick != null)
            {
                Point point = Cursor.Position;
                bool insideEditor = _owner._control.IsHandleCreated &&
                    _owner._control.RectangleToScreen(_owner._control.ClientRectangle).Contains(point);
                bool insidePopup = _owner._popupForm.Visible && _owner._popupForm.Bounds.Contains(point);
                bool insideExternal = _owner._options.IsInsideExternalControl?.Invoke(point) == true;
                if (BrowserPathEntryInteractionPolicy.ShouldDismissForBrowserClick(
                        editorActive: _owner._control.Visible,
                        clickInsideInput: insideEditor,
                        clickInsideGoButton: insideExternal,
                        clickInsidePopup: insidePopup))
                {
                    _owner.ClosePopup();
                    _owner._options.OutsideClick?.Invoke();
                }
                return false;
            }

            if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN)
            {
                return false;
            }

            _owner._editor.RefreshHandle();
            if (!_owner._editor.ContainsHandle(m.HWnd) && !IsOwnedHandle(m.HWnd, _owner._control))
            {
                return false;
            }

            Keys keyCode = (Keys)(nint)m.WParam & Keys.KeyCode;
            return _owner.HandlePopupKey(keyCode);
        }
    }

    private interface IEditorAdapter
    {
        string Text { get; }
        bool HasFocus { get; }
        void RefreshHandle();
        bool ContainsHandle(IntPtr handle);
        void EnsureFocus();
        void SetText(string text);
        void MoveCaretToEnd();
        Rectangle GetPopupBounds(int width, int height);
    }

    private sealed class ControlEditorAdapter : IEditorAdapter
    {
        private readonly Control _control;

        public ControlEditorAdapter(Control control)
        {
            _control = control;
        }

        public string Text => _control.Text;

        public bool HasFocus => _control.Focused;

        public void RefreshHandle()
        {
        }

        public bool ContainsHandle(IntPtr handle)
        {
            return IsOwnedHandle(handle, _control);
        }

        public void EnsureFocus()
        {
            if (!_control.Focused)
            {
                _control.Focus();
            }
        }

        public void SetText(string text)
        {
            _control.Text = text;
        }

        public void MoveCaretToEnd()
        {
        }

        public Rectangle GetPopupBounds(int width, int height)
        {
            return new Rectangle(_control.PointToScreen(new Point(0, _control.Height)), new Size(width, height));
        }
    }

    private sealed class TextBoxEditorAdapter : IEditorAdapter
    {
        private readonly TextBoxBase _textBox;

        public TextBoxEditorAdapter(TextBoxBase textBox)
        {
            _textBox = textBox;
        }

        public string Text => _textBox.Text;

        public bool HasFocus => _textBox.Focused;

        public void RefreshHandle()
        {
        }

        public bool ContainsHandle(IntPtr handle)
        {
            return IsOwnedHandle(handle, _textBox);
        }

        public void EnsureFocus()
        {
            if (!_textBox.Focused)
            {
                _textBox.Focus();
            }
        }

        public void SetText(string text)
        {
            _textBox.Text = text;
        }

        public void MoveCaretToEnd()
        {
            _textBox.SelectionStart = _textBox.TextLength;
            _textBox.SelectionLength = 0;
        }

        public Rectangle GetPopupBounds(int width, int height)
        {
            return new Rectangle(_textBox.PointToScreen(new Point(0, _textBox.Height)), new Size(width, height));
        }
    }

    private sealed class ComboBoxEditorAdapter : IEditorAdapter
    {
        private readonly ComboBox _comboBox;
        private IntPtr _editorHandle;

        public ComboBoxEditorAdapter(ComboBox comboBox)
        {
            _comboBox = comboBox;
            RefreshHandle();
        }

        public string Text
        {
            get
            {
                RefreshHandle();
                if (_editorHandle != IntPtr.Zero)
                {
                    return GetWindowTextValue(_editorHandle);
                }

                return _comboBox.Text;
            }
        }

        public bool HasFocus
        {
            get
            {
                RefreshHandle();
                return ContainsHandle(GetFocus());
            }
        }

        public void RefreshHandle()
        {
            if (!_comboBox.IsHandleCreated)
            {
                _editorHandle = IntPtr.Zero;
                return;
            }

            COMBOBOXINFO info = new()
            {
                cbSize = Marshal.SizeOf<COMBOBOXINFO>()
            };
            if (GetComboBoxInfo(_comboBox.Handle, ref info))
            {
                _editorHandle = info.hwndItem;
            }
        }

        public bool ContainsHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            RefreshHandle();
            if (_editorHandle != IntPtr.Zero && (handle == _editorHandle || IsChild(_editorHandle, handle)))
            {
                return true;
            }

            return IsOwnedHandle(handle, _comboBox);
        }

        public void EnsureFocus()
        {
            RefreshHandle();
            if (_editorHandle != IntPtr.Zero)
            {
                SetFocus(_editorHandle);
                return;
            }

            if (!_comboBox.Focused)
            {
                _comboBox.Focus();
            }
        }

        public void SetText(string text)
        {
            _comboBox.Text = text;
        }

        public void MoveCaretToEnd()
        {
            RefreshHandle();
            if (_editorHandle != IntPtr.Zero)
            {
                SendMessage(_editorHandle, EM_SETSEL, (IntPtr)_comboBox.Text.Length, (IntPtr)_comboBox.Text.Length);
                return;
            }

            _comboBox.SelectionStart = _comboBox.Text.Length;
            _comboBox.SelectionLength = 0;
        }

        public Rectangle GetPopupBounds(int width, int height)
        {
            return new Rectangle(_comboBox.PointToScreen(new Point(0, _comboBox.Height)), new Size(width, height));
        }

        private static string GetWindowTextValue(IntPtr handle)
        {
            int length = GetWindowTextLength(handle);
            if (length <= 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new(length + 1);
            GetWindowText(handle, builder, builder.Capacity);
            return builder.ToString();
        }
    }

    private sealed class NonSelectableListBox : ListBox
    {
        public NonSelectableListBox()
        {
            SetStyle(ControlStyles.Selectable, false);
        }
    }

    private sealed class PopupItem
    {
        public PopupItem(string displayText, string? value)
        {
            DisplayText = displayText;
            Value = value;
        }

        public string DisplayText { get; }

        public string? Value { get; }

        public bool IsFullPath { get; set; }
    }
}
