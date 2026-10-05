using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Dialogs;
using MidFD.Services;
using MidFD.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Media;
using MidFD.Models;
using MidFD.Runtime;
using MidFD.Helpers;
using MidFD.Presentation;
using MidFD.Commands;
using MidFD.Services.TrashManifestStore;
using MidFD.Services.Workspace;
namespace MidFD;

public partial class MainForm : Form
{
    private const int FunctionBarFixedCellCount = 6;
    private const int FunctionBarSlotPaddingX = 2;
    private const int FunctionBarSlotMinWidth = 46;
    private const int FunctionBarInnerGap = 5;
    private const int FunctionBarGroupGap = 20;
    private const int FunctionBarLayerBadgeReserveWidth = 48;
    private const int FunctionBarLayerBadgeLeftPadding = 4;

    private static float GetFunctionBarEffectiveScale(Font layoutFont, int panelWidth)
    {
        float fontScale = layoutFont.Size / 10.0F;
        fontScale = Math.Clamp(fontScale, 1.0F, 3.0F);

        // 横方向フィットのためのスケール逆算
        // 12スロット同一幅 (desiredSlotWidthBase=76) + バッジ予約幅 (badgeReserveWidthBase=48) = 960
        // 固定ギャップ合計 = 89 (InnerGap*9 + GroupGap*2 + SlotPadding*2 = 5*9 + 20*2 + 2*2 = 89)
        float widthFitScale = (panelWidth - 89.0F) / 960.0F;
        widthFitScale = Math.Clamp(widthFitScale, 1.0F, 3.0F);

        return Math.Min(fontScale, widthFitScale);
    }

    private Font CreateFunctionBarRenderFont(Font baseFont)
    {
        int panelWidth = functionBarPanel?.ClientSize.Width ?? 1024;
        if (panelWidth <= 0) panelWidth = 1024;
        float scale = GetFunctionBarEffectiveScale(baseFont, panelWidth);
        float size = Math.Clamp(baseFont.Size * 0.78F, 7.5F, 10.0F * scale);
        return new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Point);
    }

    private void InitializeFunctionBarToolTip()
    {
        _fKeyToolTip.InitialDelay = 500;
        _fKeyToolTip.ReshowDelay = 200;
        _fKeyToolTip.AutoPopDelay = 6000;
        _fKeyToolTip.ShowAlways = false;
    }

    private void UpdateFunctionBarCtrlLayerState(bool isCtrlPressed)
    {
        if (_isFunctionBarCtrlLayerActive != isCtrlPressed)
        {
            _isFunctionBarCtrlLayerActive = isCtrlPressed;
            UpdateFunctionBar();
            if (functionBarPanel.Visible)
            {
                functionBarPanel.Invalidate();
            }
        }
    }

    private void UpdateFunctionBarAltLayerState(bool isAltPressed)
    {
        if (_isFunctionBarAltLayerActive != isAltPressed)
        {
            _isFunctionBarAltLayerActive = isAltPressed;
            UpdateFunctionBar();
            if (functionBarPanel.Visible)
            {
                functionBarPanel.Invalidate();
            }
        }
    }

    private void UpdateFunctionBarShiftLayerState(bool isShiftPressed)
    {
        if (_isFunctionBarShiftLayerActive != isShiftPressed)
        {
            _isFunctionBarShiftLayerActive = isShiftPressed;
            UpdateFunctionBar();
            if (functionBarPanel.Visible)
            {
                functionBarPanel.Invalidate();
            }
        }
    }

    private (bool isShift, bool isCtrl, bool isAlt) GetActiveFunctionBarLayer()
    {
        if (_isFunctionBarCtrlLayerActive)
        {
            return (false, true, false);
        }
        if (_isFunctionBarAltLayerActive)
        {
            return (false, false, true);
        }
        if (_isFunctionBarShiftLayerActive)
        {
            return (true, false, false);
        }
        return (false, false, false);
    }

    private IReadOnlyList<FunctionBarSlotViewModel> BuildFunctionBarProjection(bool activeLayer = true)
    {
        if (_unifiedSearchSession is { IsActive: true } search)
        {
            string[] labels = ["Open", "Expo", "Close"];
            string[] keys = ["Enter", "E", "Esc"];
            return Enumerable.Range(1, 12).Select(slot =>
            {
                int index = slot - 1;
                bool enabled = index < labels.Length && (index == 2 || (index == 1 ? search.View.CanExport : search.View.HasResults));
                string label = index < labels.Length ? labels[index] : string.Empty;
                string key = index < keys.Length ? keys[index] : string.Empty;
                return new FunctionBarSlotViewModel(slot, false, index < labels.Length ? $"search.{index}" : null,
                    label, key, null, label, enabled, key, label, true);
            }).ToArray();
        }
        var (isShift, isCtrl, isAlt) = activeLayer
            ? GetActiveFunctionBarLayer()
            : (false, false, false);
        FunctionKeyProfile profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        return FunctionBarProjection.Build(
            profile,
            _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser,
            isShift,
            isCtrl,
            isAlt,
            _settingsCoordinator.Value.Input,
            _commandRegistry,
            BuildCommandUiSnapshot());
    }

    private void WireHeaderAndFunctionBarEvents()
    {
        EnableDoubleBuffering(this.functionBarPanel);
        // Phase 5-funcbar-click-fix1: FunctionBar のクリック復旧 (描画セグメント判定)
        this.functionBarPanel.MouseClick += FunctionBarPanel_MouseClick;
        this.functionBarPanel.MouseMove += FunctionBarPanel_MouseMove;
        this.functionBarPanel.MouseDown += FunctionBarPanel_MouseDown;
        this.functionBarPanel.MouseUp += FunctionBarPanel_MouseUp;
        this.functionBarPanel.MouseLeave += FunctionBarPanel_MouseLeave;
        // Phase 2g-fix2: ウィンドウリサイズ時にも Row 2 の Zone 幅を再計算する
        this.headerPanel.Resize += (s, e) => LayoutHeaderZones();
        // Phase 2g-fix3a: Row 1 時計更新 Timer を開始
        StartHeaderClockTimer();
        // Phase 2g-fix3b: Row 1 の再描画責務分離と局所ちらつき低減
        EnableDoubleBuffering(this.titleHeaderPanel);
        EnableDoubleBuffering(this.contentFramePanel);
        this.titleHeaderPanel.Resize += (s, e) =>
        {
            this.titleHeaderPanel.Invalidate();
            this.contentFramePanel.Invalidate();
        };
        this.contentFramePanel.Resize += (s, e) => this.contentFramePanel.Invalidate();
        // Phase 2g-fix4b.1: Row 2 の Custom Paint 配線
        headerPanel.Paint += HeaderPanel_Paint;
        headerZone1.Paint += HeaderZone_Paint;
        headerZone2.Paint += HeaderZone_Paint;
        headerZone3.Paint += HeaderZone_Paint;
        headerZone4.Paint += HeaderZone_Paint;
        // Zone自体のちらつきを抑える
        EnableDoubleBuffering(headerZone1);
        EnableDoubleBuffering(headerZone2);
        EnableDoubleBuffering(headerZone3);
        EnableDoubleBuffering(headerZone4);
        EnableDoubleBuffering(browserPanel);
        // Phase 3-bottom-funcbar-click1: FunctionBar のラベルクリック配線
        for (int i = 0; i < lblFuncKeys.Length; i++)
        {
            int index = i; // クロージャ用
            lblFuncKeys[i].Click += (s, e) => HandleFuncKeyClick(index);
        }
    }

    private bool ShouldShowBrowserFunctionBarForCurrentProfile()
    {
        return _settingsCoordinator.Value.Appearance?.ShowFunctionBar ?? true;
    }

    private bool ShouldShowFunctionBarForCurrentContext()
    {
        if (!(_settingsCoordinator.Value.Appearance?.ShowFunctionBar ?? true))
        {
            return false;
        }
        if (_unifiedSearchSession is { IsActive: true }) return true;
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser)
        {
            return ShouldShowBrowserFunctionBarForCurrentProfile();
        }
        bool compactViewer = _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Viewer
            && (_viewerApplicationCoordinator.CurrentKind == PreviewKind.Text
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.Markdown
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.Sqlite
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.Binary
                || _viewerApplicationCoordinator.CurrentKind == PreviewKind.LargeText);
        return !compactViewer;
    }

    private void ApplyFunctionBarVisibilityForCurrentContext()
    {
        bool shouldShow = ShouldShowFunctionBarForCurrentContext();
        functionBarPanel.Visible = shouldShow;
        if (shouldShow)
        {
            functionBarPanel.Height = _functionBarPreferredHeight;
        }
        else
        {
            functionBarPanel.Height = 0;
        }
        contentFramePanel.PerformLayout();
        mainAreaPanel.PerformLayout();
        viewerPanel.PerformLayout();
    }

    private void UpdateFunctionBar()
    {
        ApplyFunctionBarVisibilityForCurrentContext();
        IReadOnlyList<FunctionBarSlotViewModel> models;
        if (_unifiedSearchSession is { IsActive: true })
        {
            models = BuildFunctionBarProjection();
        }
        else
        {
            var snapshot = BuildCommandUiSnapshot();
            var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
            var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
            models = FunctionBarProjection.Build(
                profile,
                _commandStateCoordinator.UsesBrowserFunctionBar(snapshot),
                isShift,
                isCtrl,
                isAlt,
                _settingsCoordinator.Value.Input,
                _commandRegistry,
                snapshot);
        }
        for (int i = 1; i <= 12; i++)
        {
            FunctionBarSlotViewModel model = models[i - 1];
            SetFuncKeyText(i, model.DisplayLabel, model.IsEnabled);
        }
    }

    private void SetFuncKeyText(int num, string text, bool enabled)
    {
        if (num < 1 || num > 12) return;
        var lbl = lblFuncKeys[num - 1];
        // WinFD風: "数字:ラベル" 形式
        // 数字部分はシアン/青系、ラベル部分は白/灰系にするのが理想だが、
        // 最小差分のため単一ラベル内でテキスト構成する。
        if (string.IsNullOrEmpty(text))
        {
            lbl.Text = "";
        }
        else
        {
            // Phase 5-ui-visual-fix1.4c: 先頭空白と2桁パディングを廃止して領域を確保
            lbl.Text = $"{num}:{text.Trim()}";
        }
}

    private void LayoutFunctionBar()
    {
        // Phase 5-ui-layout-fix2: 個別 Label の Z-Order 問題を回避するため Paint 描画へ切り替え済み
        // lblFuncKeys は非表示にして、functionBarPanel_Paint での描画に委譲する
        foreach (var lbl in lblFuncKeys)
        {
            lbl.Visible = false;
        }
        UpdateFunctionBar();
        if (!functionBarPanel.Visible)
        {
            return;
        }
        functionBarPanel.Invalidate(); // Paint イベントを起動して再描画
    }

    private void FunctionBarPanel_MouseMove(object? sender, MouseEventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        using var layoutFont = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);
        using var functionBarFont = CreateFunctionBarRenderFont(layoutFont);

        int index = HitTestFunctionKeyIndex(e.Location, functionBarPanel.ClientRectangle, functionBarFont);
        if (index != _hoveredFuncKeyIndex)
        {
            int oldIndex = _hoveredFuncKeyIndex;
            _hoveredFuncKeyIndex = index;

            if (oldIndex >= 0) InvalidateFunctionBarItem(oldIndex);
            if (index >= 0) InvalidateFunctionBarItem(index);

            UpdateFunctionBarToolTip(index, e.Location);
        }
    }

    private void FunctionBarPanel_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        if (e.Button != MouseButtons.Left) return;

        using var layoutFont = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);
        using var functionBarFont = CreateFunctionBarRenderFont(layoutFont);

        int index = HitTestFunctionKeyIndex(e.Location, functionBarPanel.ClientRectangle, functionBarFont);
        if (index >= 0)
        {
            var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
            var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
            var models = BuildFunctionBarProjection();
            bool isEnabled = models[index].IsEnabled;

            if (isEnabled)
            {
                _pressedFuncKeyIndex = index;
                InvalidateFunctionBarItem(index);
            }
        }
    }

    private void FunctionBarPanel_MouseUp(object? sender, MouseEventArgs e)
    {
        if (_pressedFuncKeyIndex >= 0)
        {
            int oldPressed = _pressedFuncKeyIndex;
            _pressedFuncKeyIndex = -1;
            InvalidateFunctionBarItem(oldPressed);
        }
    }

    private void FunctionBarPanel_MouseLeave(object? sender, EventArgs e)
    {
        int oldHovered = _hoveredFuncKeyIndex;
        int oldPressed = _pressedFuncKeyIndex;
        bool changed = false;

        if (_hoveredFuncKeyIndex >= 0)
        {
            _hoveredFuncKeyIndex = -1;
            changed = true;
        }
        if (_pressedFuncKeyIndex >= 0)
        {
            _pressedFuncKeyIndex = -1;
            changed = true;
        }

        if (changed)
        {
            if (oldHovered >= 0) InvalidateFunctionBarItem(oldHovered);
            if (oldPressed >= 0 && oldPressed != oldHovered) InvalidateFunctionBarItem(oldPressed);
        }

        HideFunctionBarToolTip();
    }

    private void FunctionBarPanel_MouseClick(object? sender, MouseEventArgs e)
    {
        if (_viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser || !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        if (e.Button != MouseButtons.Left) return;

        using var layoutFont = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);
        using var functionBarFont = CreateFunctionBarRenderFont(layoutFont);

        int index = HitTestFunctionKeyIndex(e.Location, functionBarPanel.ClientRectangle, functionBarFont);
        if (index < 0) return;

        var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
        var models = BuildFunctionBarProjection();
        bool isEnabled = models[index].IsEnabled;

        if (!isEnabled)
        {
            return;
        }

        HandleFuncKeyClick(index);
    }

    private void FunctionBarPanel_Paint(object? sender, PaintEventArgs e)
    {
        var panel = sender as Panel;
        if (panel == null) return;
        if (_viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser && !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        int totalW = panel.ClientSize.Width;
        int totalH = panel.ClientSize.Height;
        if (totalW <= 0 || totalH <= 0) return;
        using var layoutFont = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);
        using var functionBarFont = CreateFunctionBarRenderFont(layoutFont);

        var snapshot = _cachedCommandUiSnapshot;
        bool isCompatible = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) == FunctionKeyProfile.FDCompatible;
        var palette = GetFunctionBarColors(isCompatible);

        // 外枠全体を一度クリア
        using var clearBrush = new SolidBrush(palette.BackColor);
        e.Graphics.FillRectangle(clearBrush, e.ClipRectangle);

        var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
        Rectangle[]? activeRects = null;

        var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        var models = BuildFunctionBarProjection();
        var layoutModels = BuildFunctionBarProjection(activeLayer: false);
        var labels = layoutModels.Select(model => model.LayoutLabel).ToArray();
        activeRects = CalculateFunctionBarLabelRects(panel.ClientRectangle, functionBarFont, labels);
        for (int i = 0; i < 12; i++)
        {
            var model = models[i];
            Rectangle cellRect = activeRects[i];
            bool isPressed = (i == _pressedFuncKeyIndex);
            bool isHovered = (i == _hoveredFuncKeyIndex);
            bool isEnabled = model.IsEnabled;
            DrawFunctionBarButtonFrame(e.Graphics, cellRect, palette, isEnabled, isHovered, isPressed, false);
            const int innerPad = 4;
            var rect = new Rectangle(cellRect.X + innerPad, cellRect.Y, cellRect.Width - (innerPad * 2), cellRect.Height);
            string displayText = model.DisplayLabel;
            if (!string.IsNullOrEmpty(displayText))
            {
                Size fullSize = TextRenderer.MeasureText(e.Graphics, displayText, functionBarFont, rect.Size, TextFormatFlags.NoPadding);
                if (fullSize.Width > rect.Width)
                {
                    displayText = FunctionBarLabelFormatter.GetShortenedLabel(model.ShortLabel);
                }
                DrawFunctionBarButtonText(e.Graphics, rect, displayText, model.HotKeyChar, functionBarFont, palette, isEnabled, isPressed);
            }
        }

        // 左端バッジ描画
        int badgeW = GetFunctionBarLayerBadgeWidth(isShift, isCtrl, isAlt, functionBarFont);
        if (badgeW > 0)
        {
            DrawFunctionBarLayerBadge(e.Graphics, panel.ClientRectangle, activeRects, layoutFont, palette, isShift, isCtrl, isAlt);
        }
    }
    private void UpdateFunctionBarToolTip(int index, Point location)
    {
        if (index < 0 || !_settingsCoordinator.Value.Input.ShowFunctionBarTooltips)
        {
            HideFunctionBarToolTip();
            return;
        }

        if (_fKeyToolTipIndex == index) return;

        var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
        var models = BuildFunctionBarProjection();
        string toolTipText = models[index].ToolTipText;

        HideFunctionBarToolTip();
        _fKeyToolTip.Show(toolTipText, functionBarPanel, location.X + 16, location.Y + 20, 6000);
        _fKeyToolTipIndex = index;
    }

    private void HideFunctionBarToolTip()
    {
        _fKeyToolTip.Hide(functionBarPanel);
        _fKeyToolTipIndex = -1;
    }
    private (Color EnabledBack, Color EnabledFore, Color Border, Color HotKeyBack, Color HoverBack, Color PressedBack) ResolveDarkStandardFunctionThemeColors(
        string presetKey,
        FileListColorResolver.ResolvedColors resolved)
    {
        presetKey = FileListColorResolver.CanonicalizePresetKey(presetKey);

        if (string.Equals(presetKey, "Slate", StringComparison.OrdinalIgnoreCase))
        {
            Color enabledBack = ColorContrastHelper.Blend(resolved.Background, resolved.Directory, 0.42);
            Color enabledFore = ColorContrastHelper.PickReadableTextColor(enabledBack, resolved.NormalFile, Color.White);
            return (
                enabledBack,
                enabledFore,
                resolved.Directory,
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.08),
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.16),
                ColorContrastHelper.Blend(enabledBack, Color.Black, 0.18));
        }

        if (string.Equals(presetKey, "Violet", StringComparison.OrdinalIgnoreCase))
        {
            Color enabledBack = ColorContrastHelper.Blend(resolved.Background, resolved.Directory, 0.44);
            Color enabledFore = ColorContrastHelper.PickReadableTextColor(enabledBack, resolved.NormalFile, Color.White);
            return (
                enabledBack,
                enabledFore,
                resolved.Directory,
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.08),
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.16),
                ColorContrastHelper.Blend(enabledBack, Color.Black, 0.18));
        }

        if (string.Equals(presetKey, "Sepia", StringComparison.OrdinalIgnoreCase))
        {
            Color enabledBack = ColorContrastHelper.Blend(resolved.Background, resolved.Directory, 0.46);
            Color enabledFore = ColorContrastHelper.PickReadableTextColor(enabledBack, resolved.NormalFile, Color.White);
            return (
                enabledBack,
                enabledFore,
                resolved.Directory,
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.06),
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.16),
                ColorContrastHelper.Blend(enabledBack, Color.Black, 0.18));
        }

        if (string.Equals(presetKey, "Mono Dark", StringComparison.OrdinalIgnoreCase))
        {
            Color enabledBack = ColorContrastHelper.Blend(resolved.Background, resolved.Directory, 0.40);
            Color enabledFore = ColorContrastHelper.PickReadableTextColor(enabledBack, resolved.NormalFile, Color.White);
            return (
                enabledBack,
                enabledFore,
                resolved.Directory,
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.06),
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.14),
                ColorContrastHelper.Blend(enabledBack, Color.Black, 0.18));
        }

        if (string.Equals(presetKey, "Cyber", StringComparison.OrdinalIgnoreCase))
        {
            Color enabledBack = ColorContrastHelper.Blend(resolved.Background, resolved.Directory, 0.52);
            Color enabledFore = ColorContrastHelper.PickReadableTextColor(enabledBack, resolved.NormalFile, Color.White);
            return (
                enabledBack,
                enabledFore,
                resolved.Directory,
                ColorContrastHelper.Blend(enabledBack, resolved.System, 0.28),
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.20),
                ColorContrastHelper.Blend(enabledBack, Color.Black, 0.18));
        }

        if (string.Equals(presetKey, "Green", StringComparison.OrdinalIgnoreCase))
        {
            Color enabledBack = ColorContrastHelper.Blend(resolved.Background, resolved.Directory, 0.36);
            Color enabledFore = ColorContrastHelper.PickReadableTextColor(enabledBack, resolved.NormalFile, Color.White);
            return (
                enabledBack,
                enabledFore,
                resolved.Directory,
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.08),
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.18),
                ColorContrastHelper.Blend(enabledBack, Color.Black, 0.18));
        }

        if (string.Equals(presetKey, "Amber", StringComparison.OrdinalIgnoreCase))
        {
            Color enabledBack = ColorContrastHelper.Blend(resolved.Background, resolved.Directory, 0.38);
            Color enabledFore = ColorContrastHelper.PickReadableTextColor(enabledBack, resolved.NormalFile, Color.White);
            return (
                enabledBack,
                enabledFore,
                resolved.Directory,
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.06),
                ColorContrastHelper.Blend(enabledBack, Color.White, 0.16),
                ColorContrastHelper.Blend(enabledBack, Color.Black, 0.18));
        }

        return (
            Color.FromArgb(60, 120, 180),
            Color.FromArgb(220, 238, 255),
            Color.FromArgb(70, 100, 120),
            Color.FromArgb(70, 140, 110),
            Color.FromArgb(70, 132, 192),
            Color.FromArgb(46, 92, 140));
    }

    private FunctionBarColorPalette GetFunctionBarColors(bool isWinFdCompatible)
    {
        var resolved = _resolvedColors ?? FileListColorResolver.ResolveColors(_settingsCoordinator.Value!);
        string canonicalTheme = FileListColorResolver.ResolveEffectiveColorPresetKey(_settingsCoordinator.Value!);
        isWinFdCompatible = string.Equals(canonicalTheme, "WinFdCompatible", StringComparison.OrdinalIgnoreCase);
        string themeNormalized = FileListColorResolver.NormalizeCoreTheme(canonicalTheme, _settingsCoordinator.Value!);
        bool isLightTheme = themeNormalized == "Light";
        Color lightSeparatorColor = UiThemeResolver.Resolve(_settingsCoordinator.Value.Appearance).SeparatorColor;

        // 現在のテーマを象徴する Directory (フォルダ色: ClassicCyan=シアン, Green=緑, Amber=黄/黄金など) を主調色として使用
        Color accentColor = resolved.Directory;
        Color? customBackColor;
        Color? customForeColor;
        if (FileListColorResolver.IsAutoColorSelection(_settingsCoordinator.Value))
        {
            AutoProfileColorSettings? autoColors = _settingsCoordinator.Value.Appearance!.GetAutoProfileColors(_settingsCoordinator.Value.Input.FunctionKeyProfile);
            customBackColor = UiThemeResolver.TryParseColor(autoColors?.CustomFunctionBarBackColor);
            customForeColor = UiThemeResolver.TryParseColor(autoColors?.CustomFunctionBarForeColor);
        }
        else
        {
            customBackColor = UiThemeResolver.TryParseColor(_settingsCoordinator.Value.Appearance?.CustomFunctionBarBackColor);
            customForeColor = UiThemeResolver.TryParseColor(_settingsCoordinator.Value.Appearance?.CustomFunctionBarForeColor);
        }
        bool hasCustomFunctionBarColors = customBackColor.HasValue || customForeColor.HasValue;

        if (hasCustomFunctionBarColors)
        {
            Color enabledBack = customBackColor ?? (isLightTheme ? Color.FromArgb(228, 228, 228) : Color.FromArgb(60, 120, 180));
            Color barBack = resolved.Background;
            Color barFore = customForeColor ?? (isLightTheme ? Color.FromArgb(32, 32, 32) : Color.FromArgb(220, 238, 255));
            Color disabledBack = ColorContrastHelper.Blend(barBack, enabledBack, isLightTheme ? 0.12 : 0.50);
            Color disabledForeBase = ColorContrastHelper.PickReadableTextColor(disabledBack, Color.Black, Color.White);
            Color disabledFore = ColorContrastHelper.Blend(disabledForeBase, disabledBack, isLightTheme ? 0.20 : 0.35);
            if (ColorContrastHelper.GetContrastRatio(disabledFore, disabledBack) < (isLightTheme ? 3.0 : 3.2))
            {
                disabledFore = disabledForeBase;
            }

            return new FunctionBarColorPalette
            {
                BackColor = barBack,
                BorderColor = isLightTheme ? lightSeparatorColor : Color.FromArgb(70, 100, 120),
                EnabledBackColor = enabledBack,
                EnabledTextColor = barFore,
                DisabledBackColor = disabledBack,
                DisabledTextColor = disabledFore,
                DisabledBorderColor = isLightTheme
                    ? ColorContrastHelper.Blend(barBack, lightSeparatorColor, 0.5)
                    : ColorContrastHelper.Blend(barBack, enabledBack, 0.03),
                HotKeyBackColor = ColorContrastHelper.Blend(enabledBack, Color.Yellow, isLightTheme ? 0.30 : 0.18),
                HotKeyTextColor = barFore,
                HoverBackColor = ColorContrastHelper.Blend(enabledBack, Color.White, isLightTheme ? 0.28 : 0.18),
                PressedBackColor = ColorContrastHelper.Blend(enabledBack, Color.Black, isLightTheme ? 0.10 : 0.20)
            };
        }

        if (isWinFdCompatible && !isLightTheme)
        {
            return FunctionBarColorResolver.ResolveFdCompatibleDarkDefault(resolved.Background, resolved.Directory);
        }

        if (isWinFdCompatible)
        {
            if (isLightTheme)
            {
                Color barBack = Color.FromArgb(235, 235, 235);
                Color enabledBack = ColorContrastHelper.Blend(Color.White, accentColor, 0.25);

                // フォルダ色が極端に暗い場合は輝度を確保し、明るく美しいボタン背景色を保証
                if (FileListColorResolver.GetRelativeLuminance(enabledBack) < 0.6)
                {
                    enabledBack = Color.FromArgb(200, 240, 240); // デフォルトの美しいライト水色
                }

                // 無効状態 (Disabled) 配色をテーマ派生で構成 (Light系)
                Color disabledBack = ColorContrastHelper.Blend(barBack, enabledBack, 0.08); // 背景寄りの非常に淡いブレンド

                // コントラスト優先で可読テキストを決定
                Color disabledForeBase = ColorContrastHelper.PickReadableTextColor(disabledBack, Color.Black, Color.White);
                Color disabledFore = ColorContrastHelper.Blend(disabledForeBase, disabledBack, 0.20); // 弱強調のために少し背景に寄せる
                if (ColorContrastHelper.GetContrastRatio(disabledFore, disabledBack) < 4.5)
                {
                    disabledFore = disabledForeBase;
                }

                return new FunctionBarColorPalette
                {
                    BackColor = barBack,
                    BorderColor = lightSeparatorColor,
                    EnabledBackColor = enabledBack,
                    EnabledTextColor = Color.Black,
                    DisabledBackColor = disabledBack,
                    DisabledTextColor = disabledFore,
                    DisabledBorderColor = ColorContrastHelper.Blend(barBack, lightSeparatorColor, 0.5),
                    HotKeyBackColor = Color.Yellow,
                    HotKeyTextColor = Color.Black,
                    HoverBackColor = ColorContrastHelper.Blend(enabledBack, Color.White, 0.45),
                    PressedBackColor = ColorContrastHelper.Blend(enabledBack, Color.Black, 0.15)
                };
            }
            else
            {
                Color barBack = resolved.Background;

                // フォルダ色を有効ボタンの背景色とする
                Color enabledBack = accentColor;

                // フォルダ色が暗すぎる場合は明るいボタン色にするため輝度を確保
                if (FileListColorResolver.GetRelativeLuminance(enabledBack) < 0.25)
                {
                    enabledBack = ColorContrastHelper.Blend(enabledBack, Color.White, 0.5);
                }

                // 無効状態 (Disabled) 配色をテーマ派生で構成 (Dark系: 50%ブレンドで明るく認識しやすい無効背景)
                Color disabledBack = ColorContrastHelper.Blend(barBack, enabledBack, 0.5); // ユーザー様調整の50%ブレンドを確実に維持

                // コントラスト最優先で無効背景に対して最も可読性の高い文字色 (黒 or 白) を選出
                Color disabledForeBase = ColorContrastHelper.PickReadableTextColor(disabledBack, Color.Black, Color.White);

                // 無効状態としての「薄い灰色系・弱強調」を出すため、背景色と多めにブレンド
                Color disabledFore = ColorContrastHelper.Blend(disabledForeBase, disabledBack, 0.45);
                if (ColorContrastHelper.GetContrastRatio(disabledFore, disabledBack) < 3.0)
                {
                    disabledFore = ColorContrastHelper.Blend(disabledForeBase, disabledBack, 0.25);
                }

                Color frameBack = ColorContrastHelper.Blend(barBack, enabledBack, 0.18);
                Color disabledBorder = ColorContrastHelper.Blend(barBack, enabledBack, 0.03); // 3%ブレンドで超極薄境界線

                return new FunctionBarColorPalette
                {
                    BackColor = barBack,
                    BorderColor = enabledBack,
                    EnabledBackColor = enabledBack,
                    EnabledTextColor = Color.Black, // WinFDの伝統である極めて高い判読性の確保
                    DisabledBackColor = disabledBack,
                    DisabledTextColor = disabledFore,
                    DisabledBorderColor = ColorContrastHelper.Blend(barBack, enabledBack, 0.18),
                    HotKeyBackColor = Color.Yellow,
                    HotKeyTextColor = Color.Black,
                    HoverBackColor = ColorContrastHelper.Blend(enabledBack, Color.White, 0.25),
                    PressedBackColor = ColorContrastHelper.Blend(enabledBack, Color.Black, 0.2)
                };
            }
        }
        else
        {
            Color barBack = resolved.Background;
            if (isLightTheme)
            {
                Color lightEnabledBack = Color.FromArgb(228, 228, 228);
                Color lightDisabledBack = Color.FromArgb(236, 236, 236);
                Color lightDisabledTextBase = ColorContrastHelper.PickReadableTextColor(lightDisabledBack, Color.Black, Color.White);
                Color lightDisabledText = ColorContrastHelper.Blend(lightDisabledTextBase, lightDisabledBack, 0.25);
                if (ColorContrastHelper.GetContrastRatio(lightDisabledText, lightDisabledBack) < 3.2)
                {
                    lightDisabledText = lightDisabledTextBase;
                }

                return new FunctionBarColorPalette
                {
                    BackColor = barBack,
                    BorderColor = lightSeparatorColor,
                    EnabledBackColor = lightEnabledBack,
                    EnabledTextColor = Color.FromArgb(32, 32, 32),
                    DisabledBackColor = lightDisabledBack,
                    DisabledTextColor = lightDisabledText,
                    DisabledBorderColor = ColorContrastHelper.Blend(barBack, lightSeparatorColor, 0.5),
                    HotKeyBackColor = Color.FromArgb(246, 242, 220),
                    HotKeyTextColor = Color.FromArgb(32, 32, 32),
                    HoverBackColor = Color.FromArgb(220, 220, 220),
                    PressedBackColor = Color.FromArgb(210, 210, 210)
                };
            }

            Color enabledBack = Color.FromArgb(60, 120, 180);
            Color enabledFore = Color.FromArgb(220, 238, 255);
            Color borderColor = Color.FromArgb(70, 100, 120);
            Color hotKeyBack = Color.FromArgb(70, 140, 110);
            Color hoverBack = Color.FromArgb(70, 132, 192);
            Color pressedBack = Color.FromArgb(46, 92, 140);
            (enabledBack, enabledFore, borderColor, hotKeyBack, hoverBack, pressedBack) = ResolveDarkStandardFunctionThemeColors(canonicalTheme, resolved);
            Color disabledBack = ColorContrastHelper.Blend(barBack, enabledBack, 0.5);
            Color disabledTextBase = ColorContrastHelper.PickReadableTextColor(disabledBack, Color.Black, Color.White);
            Color disabledText = ColorContrastHelper.Blend(disabledTextBase, disabledBack, 0.35);
            if (ColorContrastHelper.GetContrastRatio(disabledText, disabledBack) < 3.2)
            {
                disabledText = disabledTextBase;
            }

            return new FunctionBarColorPalette
            {
                BackColor = barBack,
                BorderColor = borderColor,
                EnabledBackColor = enabledBack,
                EnabledTextColor = enabledFore,
                DisabledBackColor = disabledBack,
                DisabledTextColor = disabledText,
                DisabledBorderColor = Color.FromArgb(45, 58, 68),

                HotKeyBackColor = hotKeyBack,
                HotKeyTextColor = enabledFore,

                HoverBackColor = hoverBack,
                PressedBackColor = pressedBack
            };
        }
    }

    private struct WinFdCompatibleLabelInfo
    {
        public string DisplayText;
        public int HotKeyCharIndex;
    }

    private WinFdCompatibleLabelInfo ResolveWinFdCompatibleLabelInfo(int keyNumber)
    {
        string? commandId = ResolveFunctionBarCommandIdForHint(
            FunctionKeyProfile.FDCompatible,
            keyNumber,
            _isFunctionBarShiftLayerActive,
            _isFunctionBarCtrlLayerActive,
            _isFunctionBarAltLayerActive);
        string displayText = FunctionKeyProfileService.ResolveFunctionBarDisplayLabelFromCommandId(
            FunctionKeyProfile.FDCompatible,
            commandId);
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return new WinFdCompatibleLabelInfo { DisplayText = "", HotKeyCharIndex = -1 };
        }

        int hotKeyCharIndex = (_isFunctionBarCtrlLayerActive || (_isFunctionBarAltLayerActive && _isFunctionBarShiftLayerActive) || (_isFunctionBarCtrlLayerActive && _isFunctionBarShiftLayerActive) || (_isFunctionBarCtrlLayerActive && _isFunctionBarAltLayerActive))
            ? -1
            : (_isFunctionBarAltLayerActive
                ? keyNumber switch
                {
                    1 => 0,
                    2 => 0,
                    3 => 0,
                    5 => 0,
                    _ => -1
                }
                : _isFunctionBarShiftLayerActive
                    ? keyNumber switch
                    {
                        1 => 0,
                        2 => 0,
                        3 => 0,
                        4 => 0,
                        5 => 0,
                        6 => 0,
                        7 => 0,
                        8 => 0,
                        9 => 0,
                        10 => 0,
                        11 => 0,
                        _ => -1
                    }
                    : keyNumber switch
                    {
                        2 => 1,
                        3 => 0,
                        4 => 0,
                        5 => 0,
                        6 => 0,
                        7 => 0,
                        8 => 0,
                        9 => 0,
                        10 => 0,
                        _ => -1
                    });

        return new WinFdCompatibleLabelInfo
        {
            DisplayText = displayText,
            HotKeyCharIndex = hotKeyCharIndex
        };
    }

    private string GetFunctionBarLayerBadgeText(bool isShift, bool isCtrl, bool isAlt)
    {
        if (isCtrl) return "Ctrl";
        if (isAlt) return "Alt";
        if (isShift) return "Shift";
        return "";
    }

    private int GetFunctionBarLayerBadgeWidth(bool isShift, bool isCtrl, bool isAlt, Font font)
    {
        string text = GetFunctionBarLayerBadgeText(isShift, isCtrl, isAlt);
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        int panelWidth = functionBarPanel?.ClientSize.Width ?? 1024;
        if (panelWidth <= 0) panelWidth = 1024;
        float scale = GetFunctionBarEffectiveScale(font, panelWidth);
        return (int)Math.Round(48 * scale);
    }

    private System.Drawing.Drawing2D.GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawFunctionBarLayerBadge(Graphics g, Rectangle panelBounds, Rectangle[]? rects, Font font, FunctionBarColorPalette palette, bool isShift, bool isCtrl, bool isAlt)
    {
        string text = GetFunctionBarLayerBadgeText(isShift, isCtrl, isAlt);
        if (string.IsNullOrEmpty(text)) return;

        int badgeW = GetFunctionBarLayerBadgeWidth(isShift, isCtrl, isAlt, font);
        int badgeY;
        int badgeH;
        if (rects != null && rects.Length > 0)
        {
            badgeY = rects[0].Y;
            badgeH = rects[0].Height;
        }
        else
        {
            int slotHeight = GetFunctionBarSlotHeight(g, font);
            badgeY = Math.Max(panelBounds.Top, panelBounds.Bottom - slotHeight - 2);
            badgeH = slotHeight;
        }
        const int paddingX = 4;
        var badgeRect = new Rectangle(panelBounds.X + paddingX, badgeY, badgeW - (paddingX * 2), badgeH);

        // 1番目のスロットと重ならないようにガード
        if (rects != null && rects.Length > 0)
        {
            int maxX = rects[0].Left - 4;
            if (badgeRect.Right > maxX)
            {
                if (maxX - badgeRect.Left < 20) return; // 描画スペースが極小の場合は描画しない
                badgeRect.Width = maxX - badgeRect.Left;
            }
        }

        Color backColor = Color.FromArgb(24, 38, 57);
        Color borderColor = Color.FromArgb(0, 192, 222);
        Color textColor = Color.FromArgb(220, 245, 255);

        using (var path = CreateRoundedRectanglePath(badgeRect, 3))
        {
            using (var bgBrush = new SolidBrush(backColor))
            {
                g.FillPath(bgBrush, path);
            }
            using (var borderPen = new Pen(borderColor, 1.2f))
            {
                g.DrawPath(borderPen, path);
            }
        }

        int panelWidth = functionBarPanel?.ClientSize.Width ?? 1024;
        if (panelWidth <= 0) panelWidth = 1024;
        float scale = GetFunctionBarEffectiveScale(font, panelWidth);
        using var badgeFont = new Font(font.FontFamily, Math.Clamp(8.5F * scale, 8.0F, 18.0F), FontStyle.Bold);
        TextRenderer.DrawText(
            g,
            text,
            badgeFont,
            badgeRect,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
        );
    }

    private void DrawFunctionBarButtonText(
        Graphics graphics,
        Rectangle labelRect,
        string labelText,
        string? hotKeyCharacter,
        Font font,
        FunctionBarColorPalette palette,
        bool isEnabled,
        bool isPressed)
    {
        if (string.IsNullOrWhiteSpace(labelText))
        {
            return;
        }

        string normalizedLabel = InputSettings.NormalizeFunctionBarLabelText(labelText);
        if (isEnabled && FunctionBarLabelFormatter.TryBuildHotKeySegments(normalizedLabel, hotKeyCharacter, out _, out string prefix, out string hotKey, out string suffix))
        {
            Size prefixSize = string.IsNullOrEmpty(prefix)
                ? Size.Empty
                : TextRenderer.MeasureText(graphics, prefix, font, Size.Empty, TextFormatFlags.NoPadding);
            Size hotKeySize = TextRenderer.MeasureText(graphics, hotKey, font, Size.Empty, TextFormatFlags.NoPadding);
            Size suffixSize = string.IsNullOrEmpty(suffix)
                ? Size.Empty
                : TextRenderer.MeasureText(graphics, suffix, font, Size.Empty, TextFormatFlags.NoPadding);
                int totalWidth = prefixSize.Width + hotKeySize.Width + suffixSize.Width;
                if (totalWidth <= labelRect.Width)
                {
                    int startX = labelRect.X + Math.Max(0, (labelRect.Width - totalWidth) / 2);
                    if (startX > labelRect.X)
                    {
                        startX -= 1;
                    }
                    int measuredHeight = TextRenderer.MeasureText(graphics, normalizedLabel, font, Size.Empty, TextFormatFlags.NoPadding).Height;
                    int contentHeight = Math.Min(labelRect.Height, Math.Max(measuredHeight, hotKeySize.Height));
                    int textY = labelRect.Y + Math.Max(0, (labelRect.Height - contentHeight) / 2);
                if (isPressed)
                {
                    textY = Math.Min(labelRect.Bottom - contentHeight, textY + 1);
                }

                Rectangle prefixRect = new Rectangle(startX, textY, prefixSize.Width, contentHeight);
                Rectangle hotKeyRect = new Rectangle(startX + prefixSize.Width, textY, hotKeySize.Width, contentHeight);
                Rectangle suffixRect = new Rectangle(startX + prefixSize.Width + hotKeySize.Width, textY, suffixSize.Width, contentHeight);

                if (hotKeyRect.Width > 0)
                {
                    Rectangle highlightRect = Rectangle.Intersect(hotKeyRect, labelRect);
                    using var highlightBrush = new SolidBrush(palette.HotKeyBackColor);
                    graphics.FillRectangle(highlightBrush, highlightRect);
                }

                Color normalText = isEnabled ? palette.EnabledTextColor : palette.DisabledTextColor;
                TextRenderer.DrawText(graphics, prefix, font, prefixRect, normalText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, hotKey, font, hotKeyRect, palette.HotKeyTextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, suffix, font, suffixRect, normalText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
        }

        Color textColor = isEnabled ? palette.EnabledTextColor : palette.DisabledTextColor;
        Rectangle textRect = labelRect;
        if (isPressed)
        {
            textRect.Offset(0, 1);
        }
        TextRenderer.DrawText(graphics, normalizedLabel, font, textRect, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private void DrawFunctionBarButtonFrame(
        Graphics graphics,
        Rectangle cellRect,
        FunctionBarColorPalette palette,
        bool isEnabled,
        bool isHovered,
        bool isPressed,
        bool emphasizeBorder)
    {
        Color cellBg = isEnabled
            ? isPressed
                ? palette.PressedBackColor
                : isHovered
                    ? palette.HoverBackColor
                    : palette.EnabledBackColor
            : palette.DisabledBackColor;

        using (var bgBrush = new SolidBrush(cellBg))
        {
            graphics.FillRectangle(bgBrush, cellRect);
        }

        bool isLightTheme = FileListColorResolver.NormalizeCoreTheme(_settingsCoordinator.Value!.Appearance!.ColorTheme, _settingsCoordinator.Value!) == "Light";
        Color borderCol = isLightTheme
            ? (isEnabled ? palette.BorderColor : palette.DisabledBorderColor)
            : isEnabled
                ? ColorContrastHelper.Blend(cellBg, palette.BackColor, 0.22)
                : ColorContrastHelper.Blend(cellBg, palette.BackColor, 0.14);
        if (emphasizeBorder && isEnabled)
        {
            borderCol = ColorContrastHelper.Blend(borderCol, palette.BackColor, 0.28);
        }

        using (var borderPen = new Pen(borderCol))
        {
            graphics.DrawRectangle(borderPen, cellRect.X, cellRect.Y, cellRect.Width - 1, cellRect.Height - 1);
        }

        if (isEnabled)
        {
            if (isPressed)
            {
                Color innerDarkCol = Color.FromArgb(40, ColorContrastHelper.Blend(cellBg, palette.BackColor, 0.20));
                using var innerDarkPen = new Pen(innerDarkCol);
                graphics.DrawRectangle(innerDarkPen, cellRect.X + 1, cellRect.Y + 1, cellRect.Width - 3, cellRect.Height - 3);
            }
            else if (isHovered)
            {
                Color innerLightCol = Color.FromArgb(56, ColorContrastHelper.Blend(cellBg, palette.BackColor, 0.12));
                using var innerLightPen = new Pen(innerLightCol);
                graphics.DrawRectangle(innerLightPen, cellRect.X + 1, cellRect.Y + 1, cellRect.Width - 3, cellRect.Height - 3);
            }
        }
    }

    private Rectangle[] CalculateFunctionBarLabelRects(Rectangle bounds, Font font, IReadOnlyList<string> labels)
    {
        var rects = new Rectangle[labels.Count];
        if (labels.Count == 0)
        {
            return rects;
        }

        int labelHeight;
        int labelWidth;
        using (var g = CreateGraphics())
        {
            labelHeight = GetFunctionBarSlotHeight(g, font);
            labelWidth = GetFunctionBarSlotWidth(bounds, font, labels.Count, 0);
        }

        int totalGap = GetFunctionBarLabelGapTotal(labels.Count);
        int buttonGroupWidth = (labelWidth * labels.Count) + totalGap;
        int startX = bounds.Left + Math.Max(0, (bounds.Width - buttonGroupWidth) / 2);
        int labelY = Math.Max(bounds.Top, bounds.Bottom - labelHeight - 2);

        int currentX = startX;
        for (int i = 0; i < labels.Count; i++)
        {
            rects[i] = new Rectangle(currentX, labelY, labelWidth, labelHeight);
            currentX += labelWidth;
            if (i < labels.Count - 1)
            {
                currentX += (i == 3 || i == 7) ? FunctionBarGroupGap : FunctionBarInnerGap;
            }
        }

        return rects;
    }

    private static int GetFunctionBarLabelGapTotal(int labelCount)
    {
        if (labelCount <= 1)
        {
            return 0;
        }

        int totalGap = 0;
        for (int i = 0; i < labelCount - 1; i++)
        {
            totalGap += (i == 3 || i == 7) ? FunctionBarGroupGap : FunctionBarInnerGap;
        }

        return totalGap;
    }

    private int GetFunctionBarSlotCellWidth(Font font)
    {
        int panelWidth = functionBarPanel?.ClientSize.Width ?? 1024;
        if (panelWidth <= 0) panelWidth = 1024;
        float scale = GetFunctionBarEffectiveScale(font, panelWidth);
        return Math.Clamp((int)Math.Round(font.Size * 1.05F), 6, (int)Math.Round(12 * scale));
    }

    private int GetFunctionBarSlotHeight(Graphics g, Font font)
    {
        int measuredHeight = TextRenderer.MeasureText(
            g,
            "Hg",
            font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
        int panelWidth = functionBarPanel?.ClientSize.Width ?? 1024;
        if (panelWidth <= 0) panelWidth = 1024;
        float scale = GetFunctionBarEffectiveScale(font, panelWidth);
        int baseHeight = Math.Clamp(measuredHeight + 4, 16, (int)Math.Round(24 * scale));
        if (functionBarPanel != null && functionBarPanel.ClientSize.Height > 0)
        {
            int maxAllowed = Math.Max(16, functionBarPanel.ClientSize.Height - 4);
            return Math.Min(baseHeight, maxAllowed);
        }
        return baseHeight;
    }

    private int GetFunctionBarSlotWidth(Rectangle bounds, Font font, int labelCount, int badgeReserveWidth)
    {
        int cellWidth = GetFunctionBarSlotCellWidth(font);
        int desiredSlotWidth = (cellWidth * FunctionBarFixedCellCount) + (FunctionBarSlotPaddingX * 2);
        int totalGap = GetFunctionBarLabelGapTotal(labelCount);
        int availableWidth = Math.Max(0, bounds.Width - badgeReserveWidth - totalGap - (FunctionBarSlotPaddingX * 2));
        int maxSlotWidthByPanel = labelCount > 0 ? availableWidth / labelCount : 0;
        if (maxSlotWidthByPanel <= 0)
        {
            return FunctionBarSlotMinWidth;
        }

        int slotWidth = Math.Min(desiredSlotWidth, maxSlotWidthByPanel);
        slotWidth = Math.Max(slotWidth, FunctionBarSlotMinWidth);
        return Math.Min(slotWidth, maxSlotWidthByPanel);
    }

    private Rectangle[] CalculateWinFdFunctionBarLabelRects(Rectangle bounds, Font font)
    {
        var labels = new string[12];
        for (int i = 1; i <= 12; i++)
        {
            labels[i - 1] = ResolveWinFdCompatibleLabelInfo(i).DisplayText;
        }

        return CalculateFunctionBarLabelRects(bounds, font, labels);
    }

    private void DrawWinFdCompatibleFunctionBarItem(
        Graphics graphics,
        Rectangle labelRect,
        int keyNumber,
        string? customCmdId,
        string? displayTextOverride,
        bool isEnabled,
        bool isHovered,
        bool isPressed,
        Font font)
    {
        WinFdCompatibleLabelInfo labelInfo;
        string? hotKeyCharacter = null;
        if (!string.IsNullOrEmpty(customCmdId))
        {
            string shortLabel = FunctionBarLabelFormatter.ExtractDisplayText(displayTextOverride);
            if (string.IsNullOrWhiteSpace(shortLabel))
            {
                shortLabel = ResolveWinFdCompatibleLabelInfo(keyNumber).DisplayText;
            }
            labelInfo = new WinFdCompatibleLabelInfo { DisplayText = shortLabel, HotKeyCharIndex = -1 };
            hotKeyCharacter = FunctionBarLabelFormatter.ResolveHotKeyCharacter(FunctionKeyProfileService.ResolveFunctionBarPrimaryKeyHint(
                customCmdId,
                _settingsCoordinator.Value.Input.BrowserKeyCommandOverrides,
                InputSettings.StandardProfileValue));
        }
        else
        {
            var defaultLabelInfo = ResolveWinFdCompatibleLabelInfo(keyNumber);
            string displayText = FunctionBarLabelFormatter.ExtractDisplayText(displayTextOverride);
            if (!string.IsNullOrWhiteSpace(displayText))
            {
                labelInfo = new WinFdCompatibleLabelInfo { DisplayText = displayText, HotKeyCharIndex = defaultLabelInfo.HotKeyCharIndex };
            }
            else
            {
                labelInfo = defaultLabelInfo;
            }
        }

        string labelText = labelInfo.DisplayText;
        if (string.IsNullOrEmpty(labelText)) return;
        if (string.IsNullOrWhiteSpace(hotKeyCharacter) && labelInfo.HotKeyCharIndex >= 0 && labelInfo.HotKeyCharIndex < labelText.Length)
        {
            hotKeyCharacter = labelText[labelInfo.HotKeyCharIndex].ToString();
        }

        // パレットの取得
        var palette = GetFunctionBarColors(isWinFdCompatible: true);

        // 1. 背景色の決定
        Color bgCol;
        if (isEnabled)
        {
            if (isPressed)
            {
                bgCol = palette.PressedBackColor;
            }
            else if (isHovered)
            {
                bgCol = palette.HoverBackColor;
            }
            else
            {
                bgCol = palette.EnabledBackColor;
            }
        }
        else
        {
            bgCol = palette.DisabledBackColor;
        }

        using (var bgBrush = new SolidBrush(bgCol))
        {
            graphics.FillRectangle(bgBrush, labelRect);
        }

        // 2. セル境界線描画
        Color borderCol = isEnabled ? palette.BorderColor : palette.DisabledBorderColor;
        using (var borderPen = new Pen(borderCol))
        {
            graphics.DrawLine(borderPen, labelRect.Left, labelRect.Top, labelRect.Right - 1, labelRect.Top);
            graphics.DrawLine(borderPen, labelRect.Left, labelRect.Bottom - 1, labelRect.Right - 1, labelRect.Bottom - 1);
            graphics.DrawLine(borderPen, labelRect.Left, labelRect.Top, labelRect.Left, labelRect.Bottom - 1);
            graphics.DrawLine(borderPen, labelRect.Right - 1, labelRect.Top, labelRect.Right - 1, labelRect.Bottom - 1);
        }

        if (isEnabled)
        {
            if (isPressed)
            {
                Color innerDarkCol = Color.FromArgb(128, ColorContrastHelper.Blend(palette.EnabledBackColor, Color.Black, 0.45));
                using var innerDarkPen = new Pen(innerDarkCol);
                graphics.DrawRectangle(innerDarkPen, labelRect.X + 1, labelRect.Y + 1, labelRect.Width - 2, labelRect.Height - 2);
            }
            else if (isHovered)
            {
                Color innerLightCol = Color.FromArgb(128, ColorContrastHelper.Blend(palette.EnabledBackColor, Color.White, 0.65));
                using var innerLightPen = new Pen(innerLightCol);
                graphics.DrawRectangle(innerLightPen, labelRect.X + 1, labelRect.Y + 1, labelRect.Width - 2, labelRect.Height - 2);
            }
        }

        // 3. テキスト描画領域
        Size textSize = TextRenderer.MeasureText(graphics, labelText, font, Size.Empty, TextFormatFlags.NoPadding);
        Rectangle textRect = new Rectangle(labelRect.X + (labelRect.Width - textSize.Width) / 2, labelRect.Y, textSize.Width, labelRect.Height);
        if (isEnabled && isPressed)
        {
            textRect.Offset(0, 1);
        }

        // 4. バインドキー背景の描画
        if (isEnabled && !string.IsNullOrWhiteSpace(hotKeyCharacter))
        {
            if (FunctionBarLabelFormatter.TryBuildHotKeySegments(labelText, hotKeyCharacter, out _, out string prefix, out string hotKeyStr, out _))
            {
                Size beforeSize = string.IsNullOrEmpty(prefix)
                    ? Size.Empty
                    : TextRenderer.MeasureText(graphics, prefix, font, Size.Empty, TextFormatFlags.NoPadding);
                Size hotKeySize = TextRenderer.MeasureText(graphics, hotKeyStr, font, Size.Empty, TextFormatFlags.NoPadding);

                int textY = textRect.Y + (textRect.Height - textSize.Height) / 2;
                int hotKeyX = textRect.X + beforeSize.Width;

                using var hotKeyBgBrush = new SolidBrush(palette.HotKeyBackColor);
                graphics.FillRectangle(hotKeyBgBrush, hotKeyX, textY, hotKeySize.Width, textSize.Height);

                TextRenderer.DrawText(graphics, prefix, font, new Rectangle(textRect.X, textY, beforeSize.Width, textRect.Height), palette.EnabledTextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, hotKeyStr, font, new Rectangle(hotKeyX, textY, hotKeySize.Width, textRect.Height), palette.HotKeyTextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                string suffix = labelText.Substring(prefix.Length + 1);
                if (!string.IsNullOrEmpty(suffix))
                {
                    int suffixX = hotKeyX + hotKeySize.Width;
                    TextRenderer.DrawText(graphics, suffix, font, new Rectangle(suffixX, textY, textRect.Right - suffixX, textRect.Height), palette.EnabledTextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                return;
            }
        }

        // 5. テキストの描画
        Color textColor = isEnabled ? palette.EnabledTextColor : palette.DisabledTextColor;
        TextRenderer.DrawText(graphics, labelText, font, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
    private int HitTestFunctionKeyIndex(Point loc, Rectangle clientBounds, Font font)
    {
        var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        var layoutModels = BuildFunctionBarProjection(activeLayer: false);
        var labels = layoutModels.Select(model => model.LayoutLabel).ToArray();
        var rects = CalculateFunctionBarLabelRects(clientBounds, font, labels);
        for (int i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(loc))
            {
                return i;
            }
        }
        return -1;
    }

    private void InvalidateFunctionBarItem(int index)
    {
        if (index < 0 || index >= 12) return;

        using var layoutFont = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);
        using var functionBarFont = CreateFunctionBarRenderFont(layoutFont);

        var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        var layoutModels = BuildFunctionBarProjection(activeLayer: false);
        var labels = layoutModels.Select(model => model.LayoutLabel).ToArray();
        var rects = CalculateFunctionBarLabelRects(functionBarPanel.ClientRectangle, functionBarFont, labels);
        if (index < rects.Length)
        {
            var r = rects[index];
            r.Inflate(1, 1);
            functionBarPanel.Invalidate(r);
        }
    }
    /// <summary>
    /// Phase 5-ui-visual-fix1.4c: 幅不足時のための承認済み短縮ラベル。
    /// Browser/Viewer それぞれの規定の省略形。
    /// </summary>

}
