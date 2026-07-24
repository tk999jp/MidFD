using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Media;
using System.Windows.Forms;

namespace MidFD.Controls;

public enum SegmentKind
{
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second
}

public static class SegmentRangeValidator
{
    public static bool IsValidSegmentValue(SegmentKind kind, int value, int year, int month)
    {
        return kind switch
        {
            SegmentKind.Year => value >= DateTimePicker.MinimumDateTime.Year && value <= DateTimePicker.MaximumDateTime.Year,
            SegmentKind.Month => value >= 1 && value <= 12,
            SegmentKind.Day => value >= 1 && value <= GetMaxDaysInMonth(year, month),
            SegmentKind.Hour => value >= 0 && value <= 23,
            SegmentKind.Minute => value >= 0 && value <= 59,
            SegmentKind.Second => value >= 0 && value <= 59,
            _ => false
        };
    }

    public static int GetMaxDaysInMonth(int year, int month)
    {
        if (year < 1 || year > 9999 || month < 1 || month > 12)
        {
            return 31;
        }
        return DateTime.DaysInMonth(year, month);
    }
}

public sealed class SegmentedDateTimeEditor : UserControl
{
    private readonly SegmentTextBox _txtYear;
    private readonly SegmentTextBox _txtMonth;
    private readonly SegmentTextBox _txtDay;
    private readonly SegmentTextBox _txtHour;
    private readonly SegmentTextBox _txtMinute;
    private readonly SegmentTextBox _txtSecond;
    private readonly Button _btnCalendar;

    private readonly TableLayoutPanel _layoutPanel;

    private DateTime _lastValidDate = DateTime.Now;
    private bool _isInternalUpdating;

    public event EventHandler? ValueChanged;
    public event EventHandler? UserValueChanged;

    public SegmentedDateTimeEditor()
    {
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;

        _txtYear = CreateSegmentTextBox(SegmentKind.Year, 4);
        _txtMonth = CreateSegmentTextBox(SegmentKind.Month, 2);
        _txtDay = CreateSegmentTextBox(SegmentKind.Day, 2);
        _txtHour = CreateSegmentTextBox(SegmentKind.Hour, 2);
        _txtMinute = CreateSegmentTextBox(SegmentKind.Minute, 2);
        _txtSecond = CreateSegmentTextBox(SegmentKind.Second, 2);

        _btnCalendar = new Button
        {
            Text = "📅",
            AccessibleName = "日付を選択",
            AutoSize = false,
            Width = 30,
            Height = 23,
            Margin = new Padding(2, 0, 0, 0),
            UseVisualStyleBackColor = true
        };
        new ToolTip().SetToolTip(_btnCalendar, "日付を選択");
        _btnCalendar.Click += BtnCalendar_Click;

        _layoutPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 12,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };

        for (int i = 0; i < 12; i++)
        {
            _layoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }
        _layoutPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _layoutPanel.Controls.Add(_txtYear, 0, 0);
        _layoutPanel.Controls.Add(CreateSeparatorLabel("/"), 1, 0);
        _layoutPanel.Controls.Add(_txtMonth, 2, 0);
        _layoutPanel.Controls.Add(CreateSeparatorLabel("/"), 3, 0);
        _layoutPanel.Controls.Add(_txtDay, 4, 0);
        _layoutPanel.Controls.Add(CreateSeparatorLabel(" "), 5, 0);
        _layoutPanel.Controls.Add(_txtHour, 6, 0);
        _layoutPanel.Controls.Add(CreateSeparatorLabel(":"), 7, 0);
        _layoutPanel.Controls.Add(_txtMinute, 8, 0);
        _layoutPanel.Controls.Add(CreateSeparatorLabel(":"), 9, 0);
        _layoutPanel.Controls.Add(_txtSecond, 10, 0);
        _layoutPanel.Controls.Add(_btnCalendar, 11, 0);

        Controls.Add(_layoutPanel);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        Value = DateTime.Now;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTime Value
    {
        get
        {
            if (TryGetValue(out var dt, out _))
            {
                _lastValidDate = dt;
                return dt;
            }
            return _lastValidDate;
        }
        set
        {
            _isInternalUpdating = true;
            try
            {
                _txtYear.Text = value.Year.ToString("D4", CultureInfo.InvariantCulture);
                _txtMonth.Text = value.Month.ToString("D2", CultureInfo.InvariantCulture);
                _txtDay.Text = value.Day.ToString("D2", CultureInfo.InvariantCulture);
                _txtHour.Text = value.Hour.ToString("D2", CultureInfo.InvariantCulture);
                _txtMinute.Text = value.Minute.ToString("D2", CultureInfo.InvariantCulture);
                _txtSecond.Text = value.Second.ToString("D2", CultureInfo.InvariantCulture);
                _lastValidDate = value;
                ClearUserEditedFlags();
            }
            finally
            {
                _isInternalUpdating = false;
            }
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearUserEditedFlags()
    {
        _txtYear.ClearUserEdited();
        _txtMonth.ClearUserEdited();
        _txtDay.ClearUserEdited();
        _txtHour.ClearUserEdited();
        _txtMinute.ClearUserEdited();
        _txtSecond.ClearUserEdited();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        bool value = Enabled;
        _txtYear.Enabled = value;
        _txtMonth.Enabled = value;
        _txtDay.Enabled = value;
        _txtHour.Enabled = value;
        _txtMinute.Enabled = value;
        _txtSecond.Enabled = value;
        _btnCalendar.Enabled = value;

    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        Color color = BackColor;
        _layoutPanel.BackColor = color;
        _txtYear.BackColor = SystemColors.Window;
        _txtMonth.BackColor = SystemColors.Window;
        _txtDay.BackColor = SystemColors.Window;
        _txtHour.BackColor = SystemColors.Window;
        _txtMinute.BackColor = SystemColors.Window;
        _txtSecond.BackColor = SystemColors.Window;
        foreach (Control child in _layoutPanel.Controls)
        {
            if (child is Label lbl)
            {
                lbl.BackColor = color;
            }
        }
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        Color color = ForeColor;
        _txtYear.ForeColor = SystemColors.WindowText;
        _txtMonth.ForeColor = SystemColors.WindowText;
        _txtDay.ForeColor = SystemColors.WindowText;
        _txtHour.ForeColor = SystemColors.WindowText;
        _txtMinute.ForeColor = SystemColors.WindowText;
        _txtSecond.ForeColor = SystemColors.WindowText;
        foreach (Control child in _layoutPanel.Controls)
        {
            if (child is Label lbl)
            {
                lbl.ForeColor = color;
            }
        }
    }

    public bool TryGetValue(out DateTime value, out SegmentKind invalidSegment)
    {
        invalidSegment = SegmentKind.Year;
        value = default;

        string yearText = GetNormalizedTextForParsing(_txtYear);
        string monthText = GetNormalizedTextForParsing(_txtMonth);
        string dayText = GetNormalizedTextForParsing(_txtDay);
        string hourText = GetNormalizedTextForParsing(_txtHour);
        string minuteText = GetNormalizedTextForParsing(_txtMinute);
        string secondText = GetNormalizedTextForParsing(_txtSecond);

        if (!int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            !SegmentRangeValidator.IsValidSegmentValue(SegmentKind.Year, year, year, 1))
        {
            invalidSegment = SegmentKind.Year;
            return false;
        }

        if (!int.TryParse(monthText, NumberStyles.None, CultureInfo.InvariantCulture, out int month) ||
            !SegmentRangeValidator.IsValidSegmentValue(SegmentKind.Month, month, year, month))
        {
            invalidSegment = SegmentKind.Month;
            return false;
        }

        if (!int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out int day) ||
            !SegmentRangeValidator.IsValidSegmentValue(SegmentKind.Day, day, year, month))
        {
            invalidSegment = SegmentKind.Day;
            return false;
        }

        if (!int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out int hour) ||
            !SegmentRangeValidator.IsValidSegmentValue(SegmentKind.Hour, hour, year, month))
        {
            invalidSegment = SegmentKind.Hour;
            return false;
        }

        if (!int.TryParse(minuteText, NumberStyles.None, CultureInfo.InvariantCulture, out int minute) ||
            !SegmentRangeValidator.IsValidSegmentValue(SegmentKind.Minute, minute, year, month))
        {
            invalidSegment = SegmentKind.Minute;
            return false;
        }

        if (!int.TryParse(secondText, NumberStyles.None, CultureInfo.InvariantCulture, out int second) ||
            !SegmentRangeValidator.IsValidSegmentValue(SegmentKind.Second, second, year, month))
        {
            invalidSegment = SegmentKind.Second;
            return false;
        }

        try
        {
            value = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            return true;
        }
        catch
        {
            invalidSegment = SegmentKind.Year;
            return false;
        }
    }

    private static string GetNormalizedTextForParsing(SegmentTextBox box)
    {
        string text = box.Text;
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            return "0" + text;
        }
        return text;
    }

    public void FocusSegment(SegmentKind segment)
    {
        var target = GetTextBox(segment);
        target.Focus();
        target.SelectAll();
    }

    public void DateSelected(DateTime date)
    {
        _isInternalUpdating = true;
        try
        {
            _txtYear.Text = date.Year.ToString("D4", CultureInfo.InvariantCulture);
            _txtMonth.Text = date.Month.ToString("D2", CultureInfo.InvariantCulture);
            _txtDay.Text = date.Day.ToString("D2", CultureInfo.InvariantCulture);
            if (TryGetValue(out var validDt, out _))
            {
                _lastValidDate = validDt;
            }
        }
        finally
        {
            _isInternalUpdating = false;
        }
        ValueChanged?.Invoke(this, EventArgs.Empty);
        UserValueChanged?.Invoke(this, EventArgs.Empty);
        FocusSegment(SegmentKind.Day);
    }

    private SegmentTextBox CreateSegmentTextBox(SegmentKind kind, int maxLength)
    {
        int charWidth = Font.Height > 0 ? (int)(Font.Height * 0.8) : 10;
        int boxWidth = (maxLength * charWidth) + 16;
        if (maxLength == 4) boxWidth = Math.Max(boxWidth, 44);
        else boxWidth = Math.Max(boxWidth, 30);

        var box = new SegmentTextBox(this, kind)
        {
            MaxLength = maxLength,
            TextAlign = HorizontalAlignment.Center,
            ImeMode = ImeMode.Disable,
            Width = boxWidth,
            Margin = new Padding(1, 0, 1, 0),
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText
        };
        return box;
    }

    private static Label CreateSeparatorLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 3, 0, 0),
            ForeColor = SystemColors.ControlText
        };
    }

    private SegmentTextBox GetTextBox(SegmentKind kind) => kind switch
    {
        SegmentKind.Year => _txtYear,
        SegmentKind.Month => _txtMonth,
        SegmentKind.Day => _txtDay,
        SegmentKind.Hour => _txtHour,
        SegmentKind.Minute => _txtMinute,
        SegmentKind.Second => _txtSecond,
        _ => _txtYear
    };

    private void MoveFocusNext(SegmentKind current)
    {
        if (current == SegmentKind.Second) return;
        var next = current + 1;
        FocusSegment(next);
    }

    private void MoveFocusPrevious(SegmentKind current)
    {
        if (current == SegmentKind.Year) return;
        var prev = current - 1;
        FocusSegment(prev);
    }

    private void AdjustValue(SegmentKind kind, int delta)
    {
        DateTime baseDt = Value;
        DateTime newDt = kind switch
        {
            SegmentKind.Year => AddYearsSafe(baseDt, delta),
            SegmentKind.Month => AddMonthsSafe(baseDt, delta),
            SegmentKind.Day => AddDaysSafe(baseDt, delta),
            SegmentKind.Hour => AddHoursSafe(baseDt, delta),
            SegmentKind.Minute => AddMinutesSafe(baseDt, delta),
            SegmentKind.Second => AddSecondsSafe(baseDt, delta),
            _ => baseDt
        };

        Value = newDt;
        UserValueChanged?.Invoke(this, EventArgs.Empty);
        FocusSegment(kind);
    }

    private static DateTime AddYearsSafe(DateTime dt, int value)
    {
        try { return dt.AddYears(value); }
        catch { return value > 0 ? DateTimePicker.MaximumDateTime : DateTimePicker.MinimumDateTime; }
    }

    private static DateTime AddMonthsSafe(DateTime dt, int value)
    {
        try { return dt.AddMonths(value); }
        catch { return value > 0 ? DateTimePicker.MaximumDateTime : DateTimePicker.MinimumDateTime; }
    }

    private static DateTime AddDaysSafe(DateTime dt, int value)
    {
        try { return dt.AddDays(value); }
        catch { return value > 0 ? DateTimePicker.MaximumDateTime : DateTimePicker.MinimumDateTime; }
    }

    private static DateTime AddHoursSafe(DateTime dt, int value)
    {
        try { return dt.AddHours(value); }
        catch { return value > 0 ? DateTimePicker.MaximumDateTime : DateTimePicker.MinimumDateTime; }
    }

    private static DateTime AddMinutesSafe(DateTime dt, int value)
    {
        try { return dt.AddMinutes(value); }
        catch { return value > 0 ? DateTimePicker.MaximumDateTime : DateTimePicker.MinimumDateTime; }
    }

    private static DateTime AddSecondsSafe(DateTime dt, int value)
    {
        try { return dt.AddSeconds(value); }
        catch { return value > 0 ? DateTimePicker.MaximumDateTime : DateTimePicker.MinimumDateTime; }
    }

    private int GetCurrentYearForValidation()
    {
        return int.TryParse(_txtYear.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int y) && y >= 1 && y <= 9999
            ? y
            : _lastValidDate.Year;
    }

    private int GetCurrentMonthForValidation()
    {
        return int.TryParse(_txtMonth.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m >= 1 && m <= 12
            ? m
            : _lastValidDate.Month;
    }

    private void RestoreSegmentFromLastValid(SegmentKind kind)
    {
        _isInternalUpdating = true;
        try
        {
            switch (kind)
            {
                case SegmentKind.Year:
                    _txtYear.Text = _lastValidDate.Year.ToString("D4", CultureInfo.InvariantCulture);
                    break;
                case SegmentKind.Month:
                    _txtMonth.Text = _lastValidDate.Month.ToString("D2", CultureInfo.InvariantCulture);
                    break;
                case SegmentKind.Day:
                    _txtDay.Text = _lastValidDate.Day.ToString("D2", CultureInfo.InvariantCulture);
                    break;
                case SegmentKind.Hour:
                    _txtHour.Text = _lastValidDate.Hour.ToString("D4", CultureInfo.InvariantCulture).Substring(2);
                    break;
                case SegmentKind.Minute:
                    _txtMinute.Text = _lastValidDate.Minute.ToString("D2", CultureInfo.InvariantCulture);
                    break;
                case SegmentKind.Second:
                    _txtSecond.Text = _lastValidDate.Second.ToString("D2", CultureInfo.InvariantCulture);
                    break;
            }
        }
        finally
        {
            _isInternalUpdating = false;
        }
    }

    private void CheckAndClampDayForYearMonthChange()
    {
        int y = GetCurrentYearForValidation();
        int m = GetCurrentMonthForValidation();
        int maxDays = SegmentRangeValidator.GetMaxDaysInMonth(y, m);

        if (int.TryParse(_txtDay.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int currentDay))
        {
            if (currentDay > maxDays)
            {
                _isInternalUpdating = true;
                try
                {
                    _txtDay.Text = maxDays.ToString("D2", CultureInfo.InvariantCulture);
                }
                finally
                {
                    _isInternalUpdating = false;
                }
            }
        }
    }

    private static void EnsureZeroPadded(SegmentTextBox box)
    {
        if (box.Text.Length == 1 && char.IsDigit(box.Text[0]))
        {
            box.Text = "0" + box.Text;
        }
    }

    private void BtnCalendar_Click(object? sender, EventArgs e)
    {
        DateTime initialDate = Value;

        MonthCalendar calendar = new()
        {
            MaxSelectionCount = 1,
            SelectionStart = initialDate,
            SelectionEnd = initialDate
        };

        ToolStripDropDown dropDown = new()
        {
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        ToolStripControlHost host = new(calendar)
        {
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        dropDown.Items.Add(host);

        calendar.DateSelected += (s, args) =>
        {
            DateSelected(args.Start);
            dropDown.Close();
        };

        calendar.KeyDown += (s, args) =>
        {
            if (args.KeyCode == Keys.Escape)
            {
                dropDown.Close();
            }
            else if (args.KeyCode == Keys.Enter)
            {
                DateSelected(calendar.SelectionStart);
                dropDown.Close();
            }
        };

        dropDown.Show(_btnCalendar, 0, _btnCalendar.Height);
        calendar.Focus();
    }

    private sealed class SegmentTextBox : TextBox
    {
        private readonly SegmentedDateTimeEditor _parent;
        private readonly SegmentKind _kind;
        private string _lastAcceptedText = string.Empty;
        private bool _isUserEdited;

        public SegmentTextBox(SegmentedDateTimeEditor parent, SegmentKind kind)
        {
            _parent = parent;
            _kind = kind;
        }

        public void ClearUserEdited()
        {
            _isUserEdited = false;
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            _lastAcceptedText = Text;
            SelectAll();
        }

        protected override void OnLeave(EventArgs e)
        {
            if (!_parent._isInternalUpdating)
            {
                int reqLength = _kind == SegmentKind.Year ? 4 : 2;
                int y = _parent.GetCurrentYearForValidation();
                int m = _parent.GetCurrentMonthForValidation();

                if (_isUserEdited && Text.Length == 1 && _kind != SegmentKind.Year)
                {
                    if (int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out int val) &&
                        SegmentRangeValidator.IsValidSegmentValue(_kind, val, y, m))
                    {
                        string padded = "0" + Text;
                        _parent._isInternalUpdating = true;
                        try
                        {
                            Text = padded;
                        }
                        finally
                        {
                            _parent._isInternalUpdating = false;
                        }

                        if (_parent.TryGetValue(out var newDt, out _))
                        {
                            bool valueChanged = newDt != _parent._lastValidDate;
                            _parent._lastValidDate = newDt;
                            _isUserEdited = false;
                            _lastAcceptedText = Text;

                            if (valueChanged)
                            {
                                _parent.ValueChanged?.Invoke(_parent, EventArgs.Empty);
                                _parent.UserValueChanged?.Invoke(_parent, EventArgs.Empty);
                            }
                        }
                        else
                        {
                            _parent.RestoreSegmentFromLastValid(_kind);
                            _isUserEdited = false;
                        }
                    }
                    else
                    {
                        _parent.RestoreSegmentFromLastValid(_kind);
                        _isUserEdited = false;
                    }
                }
                else if (string.IsNullOrEmpty(Text) || (Text.Length < reqLength && _kind == SegmentKind.Year))
                {
                    _parent.RestoreSegmentFromLastValid(_kind);
                    _isUserEdited = false;
                }
                else if (Text.Length == 1 && _kind != SegmentKind.Year)
                {
                    if (int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out int val) &&
                        SegmentRangeValidator.IsValidSegmentValue(_kind, val, y, m))
                    {
                        _parent._isInternalUpdating = true;
                        try
                        {
                            Text = "0" + Text;
                        }
                        finally
                        {
                            _parent._isInternalUpdating = false;
                        }
                    }
                    else
                    {
                        _parent.RestoreSegmentFromLastValid(_kind);
                    }
                    _isUserEdited = false;
                }
                else if (int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out int fullVal))
                {
                    if (!SegmentRangeValidator.IsValidSegmentValue(_kind, fullVal, y, m))
                    {
                        _parent.RestoreSegmentFromLastValid(_kind);
                    }
                    _isUserEdited = false;
                }
                else
                {
                    _parent.RestoreSegmentFromLastValid(_kind);
                    _isUserEdited = false;
                }
            }
            base.OnLeave(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            {
                e.Handled = true;
                return;
            }
            base.OnKeyPress(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);

            if (_parent._isInternalUpdating)
            {
                _lastAcceptedText = Text;
                return;
            }

            _isUserEdited = true;

            string digitsOnly = new string(Text.Where(char.IsDigit).ToArray());
            if (digitsOnly != Text)
            {
                _parent._isInternalUpdating = true;
                try
                {
                    Text = digitsOnly;
                    SelectionStart = Text.Length;
                }
                finally
                {
                    _parent._isInternalUpdating = false;
                }
            }

            int reqLength = _kind == SegmentKind.Year ? 4 : 2;
            int y = _parent.GetCurrentYearForValidation();
            int m = _parent.GetCurrentMonthForValidation();

            if (Text.Length == reqLength)
            {
                if (int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out int val) &&
                    SegmentRangeValidator.IsValidSegmentValue(_kind, val, y, m))
                {
                    _lastAcceptedText = Text;

                    if (_kind == SegmentKind.Year || _kind == SegmentKind.Month)
                    {
                        _parent.CheckAndClampDayForYearMonthChange();
                    }

                    if (_parent.TryGetValue(out var dt, out _))
                    {
                        _parent._lastValidDate = dt;
                    }

                    _parent.ValueChanged?.Invoke(_parent, EventArgs.Empty);
                    _parent.UserValueChanged?.Invoke(_parent, EventArgs.Empty);

                    if (SelectionLength == 0)
                    {
                        _parent.MoveFocusNext(_kind);
                    }
                }
                else
                {
                    SystemSounds.Beep.Play();
                    _parent._isInternalUpdating = true;
                    try
                    {
                        Text = _lastAcceptedText;
                        SelectionStart = Text.Length;
                    }
                    finally
                    {
                        _parent._isInternalUpdating = false;
                    }
                }
            }
            else if (Text.Length > reqLength)
            {
                SystemSounds.Beep.Play();
                _parent._isInternalUpdating = true;
                try
                {
                    Text = _lastAcceptedText;
                    SelectionStart = Text.Length;
                }
                finally
                {
                    _parent._isInternalUpdating = false;
                }
            }
            else
            {
                _lastAcceptedText = Text;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left:
                    _parent.MoveFocusPrevious(_kind);
                    return true;
                case Keys.Right:
                    _parent.MoveFocusNext(_kind);
                    return true;
                case Keys.Up:
                    _parent.AdjustValue(_kind, 1);
                    return true;
                case Keys.Down:
                    _parent.AdjustValue(_kind, -1);
                    return true;
                case Keys.Back:
                    if (Text.Length == 0)
                    {
                        _parent.MoveFocusPrevious(_kind);
                        return true;
                    }
                    break;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
