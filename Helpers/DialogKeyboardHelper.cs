namespace MidFD.Helpers;

public static class DialogKeyboardHelper
{
    public static void AttachOkCancelBindings(Form dialog, IButtonControl? okButton, IButtonControl? cancelButton)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        dialog.KeyPreview = true;
        dialog.KeyDown -= Dialog_KeyDown;
        dialog.KeyDown += Dialog_KeyDown;

        void Dialog_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Modifiers != Keys.None)
            {
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                if (TryInvoke(dialog, cancelButton))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }

                return;
            }

            if (e.KeyCode is not (Keys.Y or Keys.N))
            {
                return;
            }

            if (IsTypingControlFocused(dialog.ActiveControl))
            {
                return;
            }

            IButtonControl? target = e.KeyCode == Keys.Y ? okButton : cancelButton;
            if (!TryInvoke(dialog, target))
            {
                return;
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private static bool TryInvoke(Form dialog, IButtonControl? button)
    {
        if (button is not Control control || !control.Visible || !control.Enabled)
        {
            return false;
        }

        if (control is Button clickable)
        {
            clickable.PerformClick();
            return true;
        }

        dialog.ActiveControl = control;
        return true;
    }

    private static bool IsTypingControlFocused(Control? activeControl)
    {
        if (activeControl == null)
        {
            return false;
        }

        if (activeControl is TextBoxBase or NumericUpDown or DateTimePicker or DataGridView)
        {
            return true;
        }

        if (activeControl is ComboBox comboBox && comboBox.DropDownStyle != ComboBoxStyle.DropDownList)
        {
            return true;
        }

        return activeControl is UpDownBase;
    }
}
