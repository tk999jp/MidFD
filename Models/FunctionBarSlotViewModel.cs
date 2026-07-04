namespace MidFD.Models;

public readonly record struct FunctionBarSlotViewModel(
    int Slot,
    bool IsShiftLayer,
    string? CommandId,
    string ShortLabel,
    string? KeyHint,
    string? HotKeyChar,
    string DisplayLabel,
    bool IsEnabled,
    string ToolTipText,
    string LayoutLabel,
    bool IsSlotVisible);
