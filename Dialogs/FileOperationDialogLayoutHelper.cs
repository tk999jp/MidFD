namespace MidFD.Dialogs;

internal static class FileOperationDialogLayoutHelper
{
    public static void EnsureBottomButtonRow(
        Form form,
        IReadOnlyList<Button> buttons,
        int contentBottom,
        int sideMargin = 16,
        int bottomMargin = 16,
        int buttonGap = 8,
        int contentGap = 12)
    {
        if (buttons.Count == 0)
        {
            return;
        }

        NormalizeButtonSizes(buttons);

        int buttonHeight = buttons.Max(static button => button.Height);
        int totalButtonWidth = buttons.Sum(static button => button.Width) + (buttonGap * (buttons.Count - 1));
        int requiredWidth = Math.Max(form.ClientSize.Width, (sideMargin * 2) + totalButtonWidth);
        int requiredHeight = Math.Max(form.ClientSize.Height, contentBottom + contentGap + buttonHeight + bottomMargin);

        if (requiredWidth != form.ClientSize.Width || requiredHeight != form.ClientSize.Height)
        {
            form.ClientSize = new Size(requiredWidth, requiredHeight);
        }

        AlignButtonsRight(form, buttons, sideMargin, bottomMargin, buttonGap);
    }

    /// <summary>
    /// モダンな FlowLayoutPanel ベースのアクション行を適用します。
    /// ボタンは AutoSize = true となり、RightToLeft で右寄せ配置されます。
    /// </summary>
    public static Control? ApplyModernBottomActionRow(
        Form form,
        IReadOnlyList<Button> buttons,
        int contentBottom,
        int sideMargin = 16,
        int bottomMargin = 16,
        int buttonGap = 10,
        int contentGap = 16)
    {
        if (buttons == null || buttons.Count == 0) return null;

        int maxButtonHeight = 0;
        int totalButtonWidth = 0;

        foreach (var button in buttons)
        {
            button.AutoSize = true;
            Size preferred = button.GetPreferredSize(Size.Empty);
            maxButtonHeight = Math.Max(maxButtonHeight, Math.Max(button.MinimumSize.Height, preferred.Height));
            totalButtonWidth += Math.Max(button.MinimumSize.Width, preferred.Width) + buttonGap;
        }

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Height = maxButtonHeight + contentGap + bottomMargin,
            Padding = new Padding(sideMargin, contentGap, sideMargin, bottomMargin)
        };

        // 引数のリスト順に右から配置（RightToLeft なので、最初に追加したものが右端になる）
        // [button2] [button1] [button0] の順に右から並べるため、逆順で追加して
        // リストの見た目通りの [button0] [button1] [button2] (右寄せ) にする
        for (int i = buttons.Count - 1; i >= 0; i--)
        {
            var button = buttons[i];
            button.Margin = new Padding(buttonGap, 0, 0, 0);
            panel.Controls.Add(button);
        }

        form.Controls.Add(panel);

        int requiredWidth = Math.Max(form.ClientSize.Width, totalButtonWidth + (sideMargin * 2) - buttonGap);
        form.ClientSize = new Size(requiredWidth, contentBottom + panel.Height);

        return panel;
    }

    public static void AlignButtonsRight(
        Form form,
        IReadOnlyList<Button> buttons,
        int sideMargin = 16,
        int bottomMargin = 16,
        int buttonGap = 8)
    {
        if (buttons.Count == 0)
        {
            return;
        }

        NormalizeButtonSizes(buttons);

        int buttonTop = form.ClientSize.Height - bottomMargin - buttons.Max(static button => button.Height);
        int currentLeft = form.ClientSize.Width - sideMargin;

        for (int i = buttons.Count - 1; i >= 0; i--)
        {
            Button button = buttons[i];
            currentLeft -= button.Width;
            button.Left = currentLeft;
            button.Top = buttonTop;
            button.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            currentLeft -= buttonGap;
        }
    }

    public static int MeasureTextHeight(Control control, int width, int minimumHeight = 0)
    {
        string text = string.IsNullOrWhiteSpace(control.Text) ? " " : control.Text;
        Size measured = TextRenderer.MeasureText(
            text,
            control.Font,
            new Size(Math.Max(1, width), int.MaxValue),
            TextFormatFlags.WordBreak);
        return Math.Max(minimumHeight, measured.Height + 4);
    }

    public static int MeasureLabelHeight(Label label, int width, int minimumHeight = 0)
        => MeasureTextHeight(label, width, minimumHeight);

    private static void NormalizeButtonSizes(IReadOnlyList<Button> buttons)
    {
        foreach (Button button in buttons)
        {
            Size preferred = button.GetPreferredSize(Size.Empty);
            button.Width = Math.Max(button.Width, preferred.Width + 10);
            button.Height = Math.Max(button.Height, preferred.Height + 6);
        }
    }
}
