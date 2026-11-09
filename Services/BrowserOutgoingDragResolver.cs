using System;
using MidFD.Models;

namespace MidFD.Services;

internal sealed class BrowserOutgoingDragDecision
{
    public BrowserDragEffect AllowedEffects { get; init; }
    public BrowserDragEffect PreferredEffect { get; init; }
    public bool HasPreferredEffect { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

internal static class BrowserOutgoingDragResolver
{
    public static BrowserOutgoingDragDecision Resolve(
        bool isRightButton,
        BrowserDragModifiers modifiers,
        bool isDragArchive = false)
    {
        if (isDragArchive)
        {
            return new BrowserOutgoingDragDecision
            {
                AllowedEffects = BrowserDragEffect.Copy,
                PreferredEffect = BrowserDragEffect.Copy,
                HasPreferredEffect = true,
                StatusText = "Drag: Copy"
            };
        }

        if (isRightButton)
        {
            return new BrowserOutgoingDragDecision
            {
                AllowedEffects = BrowserDragEffect.Copy | BrowserDragEffect.Move | BrowserDragEffect.Link,
                PreferredEffect = BrowserDragEffect.None,
                HasPreferredEffect = false,
                StatusText = "Drag: 操作を選択"
            };
        }

        if ((modifiers & BrowserDragModifiers.Shift) != 0)
        {
            return new BrowserOutgoingDragDecision
            {
                AllowedEffects = BrowserDragEffect.Move,
                PreferredEffect = BrowserDragEffect.Move,
                HasPreferredEffect = true,
                StatusText = "Drag: Move"
            };
        }

        if ((modifiers & BrowserDragModifiers.Control) != 0)
        {
            return new BrowserOutgoingDragDecision
            {
                AllowedEffects = BrowserDragEffect.Copy,
                PreferredEffect = BrowserDragEffect.Copy,
                HasPreferredEffect = true,
                StatusText = "Drag: Copy"
            };
        }

        return new BrowserOutgoingDragDecision
        {
            AllowedEffects = BrowserDragEffect.Copy | BrowserDragEffect.Move,
            PreferredEffect = BrowserDragEffect.None,
            HasPreferredEffect = false,
            StatusText = "Drag: Copy" // default left drag is copy/move but shown as default
        };
    }
}
