using System.Drawing;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Services;

public static class FunctionBarColorResolver
{
    public static FunctionBarColorPalette ResolveFdCompatibleDarkDefault(Color background, Color directory)
    {
        Color enabledBack = directory;
        if (FileListColorResolver.GetRelativeLuminance(enabledBack) < 0.25)
        {
            enabledBack = ColorContrastHelper.Blend(enabledBack, Color.White, 0.5);
        }

        Color disabledBack = ColorContrastHelper.Blend(background, enabledBack, 0.5);
        Color disabledForeBase = ColorContrastHelper.PickReadableTextColor(disabledBack, Color.Black, Color.White);
        Color disabledFore = ColorContrastHelper.Blend(disabledForeBase, disabledBack, 0.45);
        if (ColorContrastHelper.GetContrastRatio(disabledFore, disabledBack) < 3.0)
        {
            disabledFore = ColorContrastHelper.Blend(disabledForeBase, disabledBack, 0.25);
        }

        return new FunctionBarColorPalette
        {
            BackColor = background,
            BorderColor = enabledBack,
            EnabledBackColor = enabledBack,
            EnabledTextColor = Color.Black,
            DisabledBackColor = disabledBack,
            DisabledTextColor = disabledFore,
            DisabledBorderColor = ColorContrastHelper.Blend(background, enabledBack, 0.18),
            HotKeyBackColor = Color.Yellow,
            HotKeyTextColor = Color.Black,
            HoverBackColor = ColorContrastHelper.Blend(enabledBack, Color.White, 0.25),
            PressedBackColor = ColorContrastHelper.Blend(enabledBack, Color.Black, 0.2)
        };
    }
}
