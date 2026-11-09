using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed record UnifiedFilterFindDialogResult(
    UnifiedFilterFindMode Mode,
    UnifiedFilterFindCriteria Criteria);

public sealed class UnifiedFilterFindDialog : Form
{
    private const string InvalidDateRangeError = "更新日時の「以降」が「以前」より後になっています。";

    private sealed class SegmentedDateTimeEditor : IDisposable
    {
        private static readonly int[] DigitCounts = [4, 2, 2, 2, 2];
        private static readonly string[] SegmentNames = ["Year", "Month", "Day", "Hour", "Minute"];
        private const int MinimumSupportedYear = 1753;
        private const int MaximumSupportedYear = 9998;
        private readonly TextBox[] _segments = new TextBox[DigitCounts.Length];
        private readonly int[] _enteredDigitCounts = new int[DigitCounts.Length];
        private readonly bool[] _autoAdvancePending = new bool[DigitCounts.Length];
        private readonly Label[] _separators = new Label[3];
        private readonly Label _validationLabel = new()
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DarkRed,
            TabStop = false,
            Visible = false,
            AccessibleRole = AccessibleRole.StatusBar
        };
        private readonly Button _calendarButton = new()
        {
            Text = "▾",
            TabStop = true,
            AccessibleName = "カレンダーを開く"
        };
        private readonly Button _incrementButton = new()
        {
            Text = "▲",
            TabStop = true,
            AccessibleName = "選択中の日時区画を増やす"
        };
        private readonly Button _decrementButton = new()
        {
            Text = "▼",
            TabStop = true,
            AccessibleName = "選択中の日時区画を減らす"
        };
        private readonly MonthCalendar _calendar = new() { MaxSelectionCount = 1 };
        private readonly ToolStripDropDown _calendarPopup = new() { AutoClose = true };
        private readonly List<Control> _controls = [];
        private DateTime _lastCompleteValue = DateTime.Now;
        private int _activeSegmentIndex = -1;
        private int _lastNumericSegmentIndex = 4;
        private bool _loadingValue;
        private bool _normalizingSegmentText;

        public SegmentedDateTimeEditor(string namePrefix)
        {
            _validationLabel.Name = $"{namePrefix}ValidationMessage";
            _validationLabel.AccessibleName = $"{namePrefix}入力エラー";
            for (int index = 0; index < _segments.Length; index++)
            {
                int capturedIndex = index;
                TextBox segment = new()
                {
                    Name = $"{namePrefix}{SegmentNames[index]}Input",
                    AccessibleName = $"{namePrefix}の{SegmentLabel(index)}",
                    AccessibleDescription = index == _segments.Length - 1
                        ? "必要桁入力後もここに留まります。"
                        : $"{DigitCounts[index]}桁入力すると{SegmentLabel(index + 1)}へ移動します。",
                    MaxLength = DigitCounts[index],
                    TextAlign = HorizontalAlignment.Center,
                    BorderStyle = BorderStyle.FixedSingle,
                    ImeMode = ImeMode.Disable,
                    TabStop = true
                };
                segment.Enter += (_, _) => BeginSegmentEdit(capturedIndex);
                segment.Leave += (_, _) =>
                {
                    if (_activeSegmentIndex == capturedIndex)
                    {
                        FinalizeSegmentInput(capturedIndex);
                        _activeSegmentIndex = -1;
                        InputChanged?.Invoke();
                    }
                };
                segment.MouseUp += (_, _) =>
                {
                    if (segment.Focused)
                    {
                        segment.SelectAll();
                    }
                };
                segment.KeyPress += (_, e) =>
                {
                    if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9'))
                    {
                        e.Handled = true;
                    }
                };
                segment.KeyDown += (_, e) => HandleSegmentKeyDown(capturedIndex, e);
                segment.TextChanged += (_, _) => HandleSegmentTextChanged(capturedIndex);
                _segments[index] = segment;
                _controls.Add(segment);
            }

            _separators[0] = CreateSeparator("/");
            _separators[1] = CreateSeparator("/");
            _separators[2] = CreateSeparator(":");
            _controls.AddRange(_separators);
            _calendarButton.Name = $"{namePrefix}CalendarButton";
            _calendarButton.Click += (_, _) => ShowCalendar();
            _incrementButton.Name = $"{namePrefix}IncrementButton";
            _incrementButton.Click += (_, _) => AdjustSegment(_lastNumericSegmentIndex, 1);
            _decrementButton.Name = $"{namePrefix}DecrementButton";
            _decrementButton.Click += (_, _) => AdjustSegment(_lastNumericSegmentIndex, -1);
            _controls.Add(_calendarButton);
            _controls.Add(_incrementButton);
            _controls.Add(_decrementButton);
            _controls.Add(_validationLabel);

            ToolStripControlHost host = new(_calendar)
            {
                AutoSize = false,
                Size = _calendar.Size,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _calendarPopup.Padding = Padding.Empty;
            _calendarPopup.Items.Add(host);
            _calendar.DateSelected += (_, e) => ApplyCalendarDate(e.Start);
        }

        public IReadOnlyList<Control> Controls => _controls;
        public TextBox YearInput => _segments[0];
        public TextBox MonthInput => _segments[1];
        public TextBox DayInput => _segments[2];
        public TextBox HourInput => _segments[3];
        public TextBox MinuteInput => _segments[4];
        public Button CalendarButton => _calendarButton;
        public Button IncrementButton => _incrementButton;
        public Button DecrementButton => _decrementButton;
        public Label ValidationLabel => _validationLabel;
        public string ValidationMessage => _validationLabel.Text;
        public int Bottom => _controls.Max(control => control.Bottom);
        public int Right => _controls.Max(control => control.Right);
        public event Action? InputChanged;

        public void ClearErrors()
        {
            SetValidationError(null, null);
        }

        public void ShowValidationError(TextBox input, string message)
            => SetValidationError(input, message);

        public void RefreshValidation()
        {
            if (HasPartialInputInProgress())
            {
                ClearErrors();
                return;
            }

            if (TryGetValue(out DateTime value, out _, out _))
            {
                _lastCompleteValue = value;
                ClearErrors();
                return;
            }

            TryGetValue(out _, out TextBox? invalidInput, out string? error);
            SetValidationError(invalidInput, error);
        }

        public int AssignTabIndexes(int firstIndex)
        {
            foreach (TextBox segment in _segments)
            {
                segment.TabIndex = firstIndex++;
            }

            // Keep keyboard traversal continuous across day -> hour -> minute.
            _calendarButton.TabIndex = firstIndex++;
            _incrementButton.TabIndex = firstIndex++;
            _decrementButton.TabIndex = firstIndex++;
            return firstIndex;
        }

        public void LayoutAt(int left, int top, Font font, int height, int gap, int right)
        {
            int x = left;
            int[] segmentWidths = DigitCounts
                .Select(count => Math.Max(height, TextRenderer.MeasureText(
                    new string('8', count),
                    font,
                    Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + Math.Max(10, font.Height / 2)))
                .ToArray();
            int separatorPadding = Math.Max(8, font.Height / 3);
            int separatorWidth = Math.Max(font.Height / 2, TextRenderer.MeasureText(
                "/", font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + separatorPadding);

            PlaceSegment(0, ref x, top, height, segmentWidths[0]);
            PlaceSeparator(0, ref x, top, height, separatorWidth);
            PlaceSegment(1, ref x, top, height, segmentWidths[1]);
            PlaceSeparator(1, ref x, top, height, separatorWidth);
            PlaceSegment(2, ref x, top, height, segmentWidths[2]);

            x += gap;
            int calendarWidth = Math.Max(30, height + 4);
            _calendarButton.SetBounds(x, top, calendarWidth, height);
            x += calendarWidth + gap;

            PlaceSegment(3, ref x, top, height, segmentWidths[3]);
            PlaceSeparator(2, ref x, top, height, Math.Max(font.Height / 2, TextRenderer.MeasureText(
                ":", font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + separatorPadding));
            PlaceSegment(4, ref x, top, height, segmentWidths[4]);

            x += gap;
            int spinWidth = Math.Max(26, font.Height + 8);
            int spinHeight = Math.Max(12, (height - 2) / 2);
            _incrementButton.SetBounds(x, top, spinWidth, spinHeight);
            _decrementButton.SetBounds(x, top + height - spinHeight, spinWidth, spinHeight);

            int validationTop = top + height + Math.Max(1, gap / 2);
            int validationHeight = Math.Max(18, font.Height + 4);
            _validationLabel.Font = font;
            _validationLabel.SetBounds(14, validationTop, Math.Max(1, right - 14), validationHeight);
        }

        public void SetValue(DateTime value)
        {
            _loadingValue = true;
            try
            {
                _lastCompleteValue = value;
                _segments[0].Text = value.Year.ToString("D4", CultureInfo.InvariantCulture);
                _segments[1].Text = value.Month.ToString("D2", CultureInfo.InvariantCulture);
                _segments[2].Text = value.Day.ToString("D2", CultureInfo.InvariantCulture);
                _segments[3].Text = value.Hour.ToString("D2", CultureInfo.InvariantCulture);
                _segments[4].Text = value.Minute.ToString("D2", CultureInfo.InvariantCulture);
                _calendar.SetDate(value.Date);
                Array.Clear(_enteredDigitCounts);
                SetValidationError(null, null);
            }
            finally
            {
                _loadingValue = false;
            }
            InputChanged?.Invoke();
        }

        public void SetEnabled(bool enabled)
        {
            if (!enabled)
            {
                _calendarPopup.Close();
                SetValidationError(null, null);
            }
            foreach (Control control in _controls)
            {
                control.Enabled = enabled;
            }
        }

        public bool TryGetValue(out DateTime value, out TextBox? invalidInput, out string? error)
        {
            value = default;
            invalidInput = null;
            error = null;
            int[] parts = new int[_segments.Length];
            for (int index = 0; index < _segments.Length; index++)
            {
                string text = _segments[index].Text;
                if (text.Length != DigitCounts[index]
                    || text.Any(static character => character < '0' || character > '9')
                    || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out parts[index]))
                {
                    invalidInput = _segments[index];
                    error = $"{SegmentLabel(index)}を{DigitCounts[index]}桁で入力してください。";
                    return false;
                }
            }

            if (parts[0] is < MinimumSupportedYear or > MaximumSupportedYear)
            {
                invalidInput = _segments[0];
                error = $"年は{MinimumSupportedYear}〜{MaximumSupportedYear}で入力してください。";
                return false;
            }
            if (parts[1] is < 1 or > 12)
            {
                invalidInput = _segments[1];
                error = "月は01〜12で入力してください。";
                return false;
            }
            int daysInMonth = DateTime.DaysInMonth(parts[0], parts[1]);
            if (parts[2] < 1 || parts[2] > daysInMonth)
            {
                invalidInput = _segments[2];
                error = $"日は01〜{daysInMonth:D2}で入力してください。";
                return false;
            }
            if (parts[3] is < 0 or > 23)
            {
                invalidInput = _segments[3];
                error = "時は00〜23で入力してください。";
                return false;
            }
            if (parts[4] is < 0 or > 59)
            {
                invalidInput = _segments[4];
                error = "分は00〜59で入力してください。";
                return false;
            }

            value = new DateTime(parts[0], parts[1], parts[2], parts[3], parts[4], 0);
            return true;
        }

        public bool CommitPendingInput(out TextBox? invalidInput, out string? error)
        {
            invalidInput = null;
            error = null;
            for (int index = 0; index < _segments.Length; index++)
            {
                int enteredDigits = _enteredDigitCounts[index];
                if (enteredDigits == 0)
                {
                    continue;
                }
                if (enteredDigits == 1)
                {
                    if (TryNormalizeSingleDigit(index))
                    {
                        continue;
                    }

                    invalidInput = _segments[index];
                    error = GetIncompleteOrSingleDigitError(index);
                    SetValidationError(invalidInput, error);
                    return false;
                }
                if (enteredDigits < DigitCounts[index])
                {
                    invalidInput = _segments[index];
                    error = $"{SegmentLabel(index)}を{DigitCounts[index]}桁で入力してください。";
                    SetValidationError(invalidInput, error);
                    return false;
                }
                if (!TryValidateSegment(index, out error))
                {
                    invalidInput = _segments[index];
                    SetValidationError(invalidInput, error);
                    return false;
                }
            }

            return true;
        }

        public void ApplyCalendarDate(DateTime selectedDate)
        {
            DateTime timeValue = TryGetValue(out DateTime current, out _, out _) ? current : _lastCompleteValue;
            SetValue(new DateTime(selectedDate.Year, selectedDate.Month, selectedDate.Day, timeValue.Hour, timeValue.Minute, 0));
            _calendarPopup.Close();
        }

        public void Dispose()
        {
            _calendarPopup.Dispose();
        }

        private void BeginSegmentEdit(int index)
        {
            _activeSegmentIndex = index;
            _lastNumericSegmentIndex = index;
            _enteredDigitCounts[index] = 0;
            _segments[index].SelectAll();
        }

        private void HandleSegmentTextChanged(int index)
        {
            if (_loadingValue || _normalizingSegmentText || _activeSegmentIndex != index || !_segments[index].Focused)
            {
                return;
            }

            try
            {
                string text = _segments[index].Text;
                _enteredDigitCounts[index] = text.Length <= DigitCounts[index]
                    && text.All(static character => character >= '0' && character <= '9')
                        ? text.Length
                        : 0;

                if (index > 0 && _enteredDigitCounts[index] == 1
                    && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int firstDigit)
                    && !CanStartTwoDigitValue(index, firstDigit))
                {
                    if (TryNormalizeSingleDigit(index))
                    {
                        ScheduleAutoAdvance(index);
                    }
                    else
                    {
                        ClearErrors();
                    }
                    return;
                }

                if (_enteredDigitCounts[index] < DigitCounts[index])
                {
                    ClearErrors();
                    return;
                }

                ClearErrors();
                if (TryGetValue(out DateTime completeValue, out _, out _))
                {
                    _lastCompleteValue = completeValue;
                }

                ScheduleAutoAdvance(index);
            }
            finally
            {
                InputChanged?.Invoke();
            }
        }

        private void FinalizeSegmentInput(int index)
        {
            if (_loadingValue || _enteredDigitCounts[index] == 0)
            {
                return;
            }
            if (_enteredDigitCounts[index] == 1)
            {
                if (TryNormalizeSingleDigit(index))
                {
                    return;
                }
                SetValidationError(_segments[index], GetIncompleteOrSingleDigitError(index));
                return;
            }
            if (TryValidateSegment(index, out string? error))
            {
                ClearErrors();
                return;
            }

            SetValidationError(_segments[index], error ?? GetIncompleteOrSingleDigitError(index));
        }

        private bool TryNormalizeSingleDigit(int index)
        {
            if (index == 0 || _enteredDigitCounts[index] != 1
                || !int.TryParse(_segments[index].Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || !IsValidSingleDigitValue(index, value))
            {
                return false;
            }

            _normalizingSegmentText = true;
            try
            {
                _segments[index].Text = value.ToString("D2", CultureInfo.InvariantCulture);
                _enteredDigitCounts[index] = DigitCounts[index];
            }
            finally
            {
                _normalizingSegmentText = false;
            }
            ClearErrors();
            return true;
        }

        private bool IsValidSingleDigitValue(int index, int value)
        {
            return index switch
            {
                1 => value is >= 1 and <= 9,
                2 => TryGetDaysInCurrentMonth(out int daysInMonth) && value is >= 1 && value <= Math.Min(9, daysInMonth),
                3 or 4 => value is >= 0 and <= 9,
                _ => false
            };
        }

        private bool CanStartTwoDigitValue(int index, int firstDigit)
        {
            int minimum;
            int maximum;
            switch (index)
            {
                case 1:
                    minimum = 1;
                    maximum = 12;
                    break;
                case 2:
                    if (!TryGetDaysInCurrentMonth(out maximum))
                    {
                        return true;
                    }
                    minimum = 1;
                    break;
                case 3:
                    minimum = 0;
                    maximum = 23;
                    break;
                case 4:
                    minimum = 0;
                    maximum = 59;
                    break;
                default:
                    return false;
            }

            int firstPossible = firstDigit * 10;
            int lastPossible = firstPossible + 9;
            return firstPossible <= maximum && lastPossible >= minimum;
        }

        private bool TryGetDaysInCurrentMonth(out int daysInMonth)
        {
            daysInMonth = 0;
            if (!TryReadSegment(0, out int year)
                || year is < MinimumSupportedYear or > MaximumSupportedYear
                || !TryReadSegment(1, out int month)
                || month is < 1 or > 12)
            {
                return false;
            }
            daysInMonth = DateTime.DaysInMonth(year, month);
            return true;
        }

        private string GetIncompleteOrSingleDigitError(int index)
        {
            if (!int.TryParse(_segments[index].Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return $"{SegmentLabel(index)}を{DigitCounts[index]}桁で入力してください。";
            }

            if (index == 1 && value is < 1 or > 12)
            {
                return "月は01〜12で入力してください。";
            }
            if (index == 2)
            {
                return TryGetDaysInCurrentMonth(out int daysInMonth)
                    ? $"日は01〜{daysInMonth:D2}で入力してください。"
                    : "先に有効な年と月を入力してください。";
            }
            if (index == 3 && value > 23)
            {
                return "時は00〜23で入力してください。";
            }
            if (index == 4 && value > 59)
            {
                return "分は00〜59で入力してください。";
            }
            return $"{SegmentLabel(index)}を{DigitCounts[index]}桁で入力してください。";
        }

        private bool HasPartialInputInProgress()
            => _activeSegmentIndex >= 0
                && _segments[_activeSegmentIndex].TextLength < DigitCounts[_activeSegmentIndex];

        private void ScheduleAutoAdvance(int index)
        {
            if (_enteredDigitCounts[index] != DigitCounts[index]
                || index + 1 >= _segments.Length
                || !CanAdvanceFrom(index)
                || _autoAdvancePending[index])
            {
                return;
            }

            _autoAdvancePending[index] = true;
            _segments[index].BeginInvoke(new Action(() =>
            {
                _autoAdvancePending[index] = false;
                if (!_segments[index].IsDisposed
                    && _activeSegmentIndex == index
                    && _segments[index].Focused
                    && _enteredDigitCounts[index] == DigitCounts[index]
                    && CanAdvanceFrom(index))
                {
                    _segments[index + 1].Focus();
                }
            }));
        }

        private bool CanAdvanceFrom(int index)
        {
            if (index < 0 || index >= _segments.Length || _enteredDigitCounts[index] != DigitCounts[index])
            {
                return false;
            }
            if (TryValidateSegment(index, out string? error))
            {
                return true;
            }
            SetValidationError(_segments[index], error);
            return false;
        }

        private bool TryValidateSegment(int index, out string? error)
        {
            error = null;
            if (!TryReadSegment(index, out int value))
            {
                error = $"{SegmentLabel(index)}を{DigitCounts[index]}桁で入力してください。";
                return false;
            }

            switch (index)
            {
                case 0 when value is < MinimumSupportedYear or > MaximumSupportedYear:
                    error = $"年は{MinimumSupportedYear}〜{MaximumSupportedYear}で入力してください。";
                    return false;
                case 1 when value is < 1 or > 12:
                    error = "月は01〜12で入力してください。";
                    return false;
                case 2:
                    if (!TryReadSegment(0, out int year)
                        || year is < MinimumSupportedYear or > MaximumSupportedYear
                        || !TryReadSegment(1, out int month)
                        || month is < 1 or > 12)
                    {
                        error = "先に有効な年と月を入力してください。";
                        return false;
                    }
                    int daysInMonth = DateTime.DaysInMonth(year, month);
                    if (value < 1 || value > daysInMonth)
                    {
                        error = $"日は01〜{daysInMonth:D2}で入力してください。";
                        return false;
                    }
                    break;
                case 3 when value is < 0 or > 23:
                    error = "時は00〜23で入力してください。";
                    return false;
                case 4 when value is < 0 or > 59:
                    error = "分は00〜59で入力してください。";
                    return false;
            }
            return true;
        }

        private bool TryReadSegment(int index, out int value)
        {
            value = 0;
            string text = _segments[index].Text;
            return text.Length == DigitCounts[index]
                && text.All(static character => character >= '0' && character <= '9')
                && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private void HandleSegmentKeyDown(int index, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode is Keys.Up or Keys.Down)
            {
                AdjustSegment(index, e.KeyCode == Keys.Up ? 1 : -1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Left && _segments[index].SelectionStart == 0 && index > 0)
            {
                _segments[index - 1].Focus();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Right
                && _segments[index].SelectionStart >= _segments[index].TextLength
                && index + 1 < _segments.Length)
            {
                _segments[index + 1].Focus();
                e.Handled = true;
            }
        }

        private void AdjustSegment(int index, int delta)
        {
            if (index < 0 || index >= _segments.Length || !TryGetValue(out DateTime current, out _, out _))
            {
                return;
            }

            DateTime adjusted;
            try
            {
                adjusted = index switch
                {
                    0 => current.AddYears(delta),
                    1 => current.AddMonths(delta),
                    2 => current.AddDays(delta),
                    3 => current.AddHours(delta),
                    _ => current.AddMinutes(delta)
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }
            if (adjusted.Year < MinimumSupportedYear || adjusted.Year > MaximumSupportedYear)
            {
                SetValidationError(_segments[index], $"年は{MinimumSupportedYear}〜{MaximumSupportedYear}の範囲で操作してください。");
                return;
            }

            TextBox activeSegment = _segments[index];
            SetValue(adjusted);
            activeSegment.Focus();
            activeSegment.SelectAll();
        }

        private void ShowCalendar()
        {
            DateTime selectedDate = TryGetValue(out DateTime current, out _, out _)
                ? current.Date
                : _lastCompleteValue.Date;
            _calendar.SetDate(selectedDate);
            _calendarPopup.Show(_calendarButton, new Point(0, _calendarButton.Height));
        }

        private void PlaceSegment(int index, ref int x, int top, int height, int width)
        {
            _segments[index].SetBounds(x, top, width, height);
            x += width;
        }

        private void PlaceSeparator(int index, ref int x, int top, int height, int width)
        {
            _separators[index].SetBounds(x, top, width, height);
            _separators[index].TextAlign = ContentAlignment.MiddleCenter;
            x += width;
        }

        private static Label CreateSeparator(string text)
            => new() { Text = text, AutoSize = false, TabStop = false };

        private static string SegmentLabel(int index)
            => index switch
            {
                0 => "年",
                1 => "月",
                2 => "日",
                3 => "時",
                _ => "分"
            };

        private void SetValidationError(TextBox? input, string? message)
        {
            bool hasError = input is not null && !string.IsNullOrWhiteSpace(message);
            _validationLabel.Text = hasError ? message! : string.Empty;
            _validationLabel.AccessibleDescription = !hasError
                ? string.Empty
                : $"{SegmentLabel(Array.IndexOf(_segments, input!))}: {message}";
            _validationLabel.Visible = hasError;
        }
    }

    private sealed class DetailsEditor
    {
        public GroupBox Group { get; } = new() { Text = "詳細条件" };
        public TextBox ExtensionText { get; } = new();
        public CheckBox FromCheck { get; } = new();
        public SegmentedDateTimeEditor FromDateTime { get; }
        public CheckBox ToCheck { get; } = new();
        public SegmentedDateTimeEditor ToDateTime { get; }
        public CheckBox GitUnignored { get; } = new();
        public Label ExtensionLabel { get; } = new() { Text = "対象拡張子(&E)（カンマ区切り）", AutoSize = true };
        public Label ExtensionExample { get; } = new() { Text = "例: cs, md, txt", AutoSize = true };

        public DetailsEditor(int top)
        {
            FromDateTime = new SegmentedDateTimeEditor("From");
            ToDateTime = new SegmentedDateTimeEditor("To");
            FromDateTime.InputChanged += RefreshDateRangeValidation;
            ToDateTime.InputChanged += RefreshDateRangeValidation;
            Group.SetBounds(12, top, 545, 195);
            Group.TabIndex = 6;

            ExtensionLabel.SetBounds(14, 19, 240, 22);
            ExtensionText.SetBounds(14, 42, 510, 24);
            ExtensionText.TabIndex = 0;
            ExtensionExample.SetBounds(14, 67, 220, 20);

            FromCheck.Text = "更新日時 以降";
            FromCheck.SetBounds(14, 91, 112, 24);
            FromCheck.TabIndex = 1;

            ToCheck.Text = "更新日時 以前";
            ToCheck.SetBounds(14, 120, 112, 24);
            int nextTabIndex = FromDateTime.AssignTabIndexes(2);
            ToCheck.TabIndex = nextTabIndex++;
            nextTabIndex = ToDateTime.AssignTabIndexes(nextTabIndex);

            GitUnignored.Text = "Gitで無視されていない項目のみ";
            GitUnignored.SetBounds(14, 151, 300, 24);
            GitUnignored.TabIndex = nextTabIndex;
            Group.Controls.AddRange([
                ExtensionLabel, ExtensionText, ExtensionExample,
                FromCheck, .. FromDateTime.Controls, ToCheck, .. ToDateTime.Controls, GitUnignored
            ]);
            FromCheck.CheckedChanged += (_, _) =>
            {
                UpdateDateState();
                RefreshDateRangeValidation();
            };
            ToCheck.CheckedChanged += (_, _) =>
            {
                UpdateDateState();
                RefreshDateRangeValidation();
            };
        }

        public void LayoutForContent(int top)
        {
            int gap = Math.Max(4, Group.Font.Height / 4);
            Group.Top = top;
            ExtensionLabel.Top = 19;
            ExtensionText.Top = ExtensionLabel.Bottom + gap;
            ExtensionExample.Top = ExtensionText.Bottom + gap;

            int fromTop = ExtensionExample.Bottom + gap;
            int rowHeight = Math.Max(28, Group.Font.Height + 12);
            FromCheck.Top = fromTop;
            FromCheck.Height = rowHeight;
            ResizeDateCheck(FromCheck);
            FromDateTime.LayoutAt(
                FromCheck.Right + gap,
                fromTop,
                Group.Font,
                rowHeight,
                gap,
                Group.ClientSize.Width - 14);
            int fromBottom = Math.Max(FromCheck.Bottom, FromDateTime.Bottom);

            int toTop = fromBottom + gap;
            ToCheck.Top = toTop;
            ToCheck.Height = rowHeight;
            ResizeDateCheck(ToCheck);
            ToDateTime.LayoutAt(
                ToCheck.Right + gap,
                toTop,
                Group.Font,
                rowHeight,
                gap,
                Group.ClientSize.Width - 14);
            int toBottom = Math.Max(ToCheck.Bottom, ToDateTime.Bottom);

            int gitTop = toBottom + gap;
            GitUnignored.Top = gitTop;
            Group.Height = GitUnignored.Bottom + Math.Max(8, gap);
        }

        private static void ResizeDateCheck(CheckBox check)
        {
            check.Left = 14;
            Size textSize = TextRenderer.MeasureText(
                check.Text,
                check.Font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            int glyphAndPadding = Math.Max(22, check.Font.Height + 4) + check.Padding.Horizontal;
            check.Width = Math.Max(textSize.Width + glyphAndPadding, check.GetPreferredSize(Size.Empty).Width);
        }

        public void LoadState(TabFilterLockState state)
        {
            ExtensionText.Text = state.ExtensionText ?? string.Empty;
            DateTime defaultDateTime = DateTime.Now;
            FromCheck.Checked = state.ModifiedFromLocal.HasValue;
            FromDateTime.SetValue(state.ModifiedFromLocal ?? defaultDateTime);
            ToCheck.Checked = state.ModifiedToLocal.HasValue;
            ToDateTime.SetValue(state.ModifiedToLocal ?? defaultDateTime);
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

        public bool TryCreateState(out TabFilterLockState state, out string? error, out Control? invalidInput)
        {
            FromDateTime.ClearErrors();
            ToDateTime.ClearErrors();
            DateTime? from = null;
            DateTime? to = null;
            invalidInput = null;
            if (FromCheck.Checked)
            {
                if (!FromDateTime.CommitPendingInput(out TextBox? pendingInput, out string? pendingError))
                {
                    state = new TabFilterLockState();
                    error = $"「更新日時 以降」{pendingError}";
                    invalidInput = pendingInput;
                    FromDateTime.ShowValidationError(pendingInput!, error);
                    return false;
                }
                if (!FromDateTime.TryGetValue(out DateTime fromValue, out TextBox? fromInvalidInput, out string? fromError))
                {
                    state = new TabFilterLockState();
                    error = $"「更新日時 以降」{fromError}";
                    invalidInput = fromInvalidInput;
                    FromDateTime.ShowValidationError(fromInvalidInput!, error);
                    return false;
                }
                from = fromValue;
            }

            if (ToCheck.Checked)
            {
                if (!ToDateTime.CommitPendingInput(out TextBox? pendingInput, out string? pendingError))
                {
                    state = new TabFilterLockState();
                    error = $"「更新日時 以前」{pendingError}";
                    invalidInput = pendingInput;
                    ToDateTime.ShowValidationError(pendingInput!, error);
                    return false;
                }
                if (!ToDateTime.TryGetValue(out DateTime toValue, out TextBox? toInvalidInput, out string? toError))
                {
                    state = new TabFilterLockState();
                    error = $"「更新日時 以前」{toError}";
                    invalidInput = toInvalidInput;
                    ToDateTime.ShowValidationError(toInvalidInput!, error);
                    return false;
                }
                to = toValue;
            }

            if (from.HasValue && to.HasValue && from > to)
            {
                state = new TabFilterLockState();
                error = InvalidDateRangeError;
                invalidInput = ToDateTime.YearInput;
                ToDateTime.ShowValidationError(ToDateTime.YearInput, error);
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
            FromDateTime.SetEnabled(FromCheck.Checked);
            ToDateTime.SetEnabled(ToCheck.Checked);
        }

        private void RefreshDateRangeValidation()
        {
            if (FromCheck.Checked)
            {
                FromDateTime.RefreshValidation();
            }
            else
            {
                FromDateTime.ClearErrors();
            }
            if (ToCheck.Checked)
            {
                ToDateTime.RefreshValidation();
            }
            else
            {
                ToDateTime.ClearErrors();
            }
            if (!FromCheck.Checked || !ToCheck.Checked)
            {
                return;
            }
            if (FromDateTime.TryGetValue(out DateTime from, out _, out _)
                && ToDateTime.TryGetValue(out DateTime to, out _, out _)
                && from > to)
            {
                ToDateTime.ShowValidationError(ToDateTime.YearInput, InvalidDateRangeError);
            }
        }

        public void Dispose()
        {
            FromDateTime.Dispose();
            ToDateTime.Dispose();
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
    private readonly DetailsEditor _filterDetails;
    private readonly DetailsEditor _searchDetails;
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
        _filterDetails = new DetailsEditor(top: 105);
        _searchDetails = new DetailsEditor(top: 210);
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
        FontChanged += (_, _) => ApplyContentDerivedLayout();

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _filterDetails.Dispose();
            _searchDetails.Dispose();
        }
        base.Dispose(disposing);
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
            .Max(GetRequiredPageContentHeight);
        int tabHeaderAndBorderHeight = Math.Max(0, _pages.Height - displayHeight);
        _pages.Height = requiredPageContentHeight + tabHeaderAndBorderHeight;
        _pages.PerformLayout();

        int displayHeightShortage = requiredPageContentHeight - _pages.DisplayRectangle.Height;
        if (displayHeightShortage > 0)
        {
            _pages.Height += displayHeightShortage;
            _pages.PerformLayout();
        }

        Rectangle display = _pages.DisplayRectangle;
        foreach (TabPage page in new[] { _filterPage, _searchPage })
        {
            if (!ReferenceEquals(page, _pages.SelectedTab) && page.Bounds != display)
            {
                page.Bounds = display;
            }
            page.PerformLayout();
        }

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

    private static int GetRequiredPageContentHeight(TabPage page)
        => page.Controls.Cast<Control>()
            .Select(control => control.Bottom + control.Margin.Bottom)
            .DefaultIfEmpty(0)
            .Max() + page.Padding.Bottom;

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
        if (!details.TryCreateState(out TabFilterLockState detailState, out _, out Control? invalidInput))
        {
            FocusControl(invalidInput ?? (isFilter ? _filterNamePattern : _searchNamePattern));
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
