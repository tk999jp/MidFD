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
using MidFD.Helpers;
using MidFD.Commands;
using MidFD.Services.TrashManifestStore;
using MidFD.Services.Workspace;
namespace MidFD;

public partial class MainForm : Form
{

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
        return true;
    }

    private bool ShouldShowFunctionBarForCurrentContext()
    {
        if (_uiMode == UIMode.Browser)
        {
            return ShouldShowBrowserFunctionBarForCurrentProfile();
        }
        bool compactViewer = _uiMode == UIMode.Viewer
            && (_currentViewerKind == PreviewKind.Text || _currentViewerKind == PreviewKind.Binary || _currentViewerKind == PreviewKind.LargeText);
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
        contentFramePanel.PerformLayout();
        mainAreaPanel.PerformLayout();
        viewerPanel.PerformLayout();
    }


    private IReadOnlyList<FunctionBarSlotViewModel> BuildFunctionBarSlotModels(FunctionKeyProfile profile, bool isShiftLayer, bool isCtrlLayer = false, bool isAltLayer = false)
    {
        var snapshot = BuildCommandUiSnapshot();
        var models = new List<FunctionBarSlotViewModel>(12);
        var profileValue = profile == FunctionKeyProfile.FDCompatible
            ? InputSettings.FdCompatibleProfileValue
            : InputSettings.StandardProfileValue;
        var definitions = FunctionKeyProfileService.GetDefinitions(profileValue);

        for (int slot = 1; slot <= 12; slot++)
        {
            var def = definitions.FirstOrDefault(d => d.KeyNumber == slot);
            string? customCmdId = FunctionKeyProfileService.ResolveFunctionBarCommandId(
                profile,
                slot,
                _settings.Input.FunctionBarCommandOverridesStandard,
                _settings.Input.FunctionBarCommandOverridesFdCompatible,
                _settings.Input.FunctionBarCommandOverridesShiftStandard,
                _settings.Input.FunctionBarCommandOverridesShiftFdCompatible,
                isShiftLayer,
                _settings.Input.FunctionBarCommandOverridesCtrlStandard,
                _settings.Input.FunctionBarCommandOverridesCtrlFdCompatible,
                _settings.Input.FunctionBarCommandOverridesAltStandard,
                _settings.Input.FunctionBarCommandOverridesAltFdCompatible,
                isCtrlLayer,
                isAltLayer);

            bool isUnassignedModifier = string.IsNullOrEmpty(customCmdId) ||
                                        string.Equals(customCmdId, "none", StringComparison.OrdinalIgnoreCase);
            if (profile == FunctionKeyProfile.Standard)
            {
                isUnassignedModifier = (isCtrlLayer || isAltLayer) && isUnassignedModifier;
            }

            // Determine ShortLabel
            string shortLabel;
            if (isUnassignedModifier)
            {
                shortLabel = "";
            }
            else if (profile == FunctionKeyProfile.FDCompatible)
            {
                shortLabel = FunctionKeyProfileService.ResolveFdCompatibleFunctionBarShortLabel(slot, isShiftLayer, isCtrlLayer, isAltLayer);
            }
            else if (!string.IsNullOrEmpty(customCmdId))
            {
                shortLabel = FunctionKeyProfileService.ResolveFunctionBarDisplayLabelFromCommandId(profile, customCmdId);
            }
            else
            {
                shortLabel = def?.Label ?? "Cmd";
            }

            // Apply Custom ShortLabel Override if exists and active CommandId matches
            if (!isUnassignedModifier && !string.IsNullOrEmpty(customCmdId) && !string.Equals(customCmdId, "none", StringComparison.OrdinalIgnoreCase))
            {
                var labelOverrides = GetActiveFunctionBarLabelOverrides(isShiftLayer, isCtrlLayer, isAltLayer, profile == FunctionKeyProfile.FDCompatible);
                if (labelOverrides != null && labelOverrides.TryGetValue($"F{slot}", out var labelOverride) && labelOverride != null)
                {
                    if (string.Equals(labelOverride.CommandId, customCmdId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(labelOverride.Label))
                    {
                        shortLabel = InputSettings.NormalizeFunctionBarLabelText(labelOverride.Label);
                    }
                }
            }

            // Determine KeyHint (browser shortcut表示用。hotkey強調は未修飾の英字ショートカットだけに限定)
            string? keyHint = null;
            string? hotKeyChar = null;
            if (!isUnassignedModifier && !string.IsNullOrEmpty(customCmdId))
            {
                keyHint = FunctionKeyProfileService.ResolveFunctionBarKeyHint(
                    customCmdId,
                    _settings.Input.BrowserKeyCommandOverrides,
                    profileValue);
                if (string.IsNullOrWhiteSpace(keyHint))
                {
                    keyHint = null;
                }
                hotKeyChar = FunctionKeyProfileService.ResolveFunctionBarBrowserHotKeyCharacter(
                    customCmdId,
                    _settings.Input.BrowserKeyCommandOverrides,
                    profileValue);
                if (string.IsNullOrWhiteSpace(hotKeyChar))
                {
                    hotKeyChar = null;
                }
            }

            // Determine DisplayLabel
            string displayLabel;
            if (isUnassignedModifier)
            {
                displayLabel = "";
            }
            else
            {
                var labelOverrides = GetActiveFunctionBarLabelOverrides(isShiftLayer, isCtrlLayer, isAltLayer, profile == FunctionKeyProfile.FDCompatible);
                displayLabel = FunctionKeyProfileService.ResolveFunctionBarDisplayLabel(
                    profile,
                    slot,
                    isShiftLayer,
                    isCtrlLayer,
                    isAltLayer,
                    customCmdId,
                    labelOverrides);
            }

            // Determine IsEnabled
            bool isEnabled = true;
            if (_uiMode == UIMode.Browser)
            {
                if (isUnassignedModifier)
                {
                    isEnabled = false;
                }
                else if (!string.IsNullOrEmpty(customCmdId))
                {
                    isEnabled = _commandStateCoordinator.IsCommandEnabled(customCmdId, snapshot);
                }
                else
                {
                    isEnabled = def != null && def.Action != FunctionKeyAction.None
                        ? _commandStateCoordinator.IsActionEnabled(def.Action, snapshot)
                        : false;
                }
            }
            else if (isUnassignedModifier)
            {
                isEnabled = false;
            }

            // Determine ToolTipText
            string toolTipText;
            string slotPrefix;
            if (isCtrlLayer)
            {
                slotPrefix = $"Ctrl+F{slot}";
            }
            else if (isAltLayer)
            {
                slotPrefix = $"Alt+F{slot}";
            }
            else if (isShiftLayer)
            {
                slotPrefix = $"Shift+F{slot}";
            }
            else
            {
                slotPrefix = $"F{slot}";
            }

            if (isUnassignedModifier)
            {
                toolTipText = $"{slotPrefix}: 未割り当て";
            }
            else if (!string.IsNullOrEmpty(customCmdId))
            {
                var cmdDef = _commandRegistry.Find(customCmdId);
                string commandName = cmdDef?.DisplayName ?? "不明なコマンド";
                string description = cmdDef?.Description ?? $"未登録のコマンドID: {customCmdId}";
                var toolTipLines = new List<string>
                {
                    shortLabel,
                    $"Command: {customCmdId}",
                    $"Function: {slotPrefix}"
                };
                if (!string.IsNullOrEmpty(keyHint))
                {
                    toolTipLines.Add($"通常キー: {keyHint}");
                }
                toolTipLines.Add(description);
                toolTipText = string.Join("\r\n", toolTipLines);
            }
            else
            {
                if (profile == FunctionKeyProfile.FDCompatible)
                {
                    var action = def?.Action ?? FunctionKeyAction.None;
                    if (action == FunctionKeyAction.None)
                    {
                        toolTipText = $"{slotPrefix}: 未割り当て";
                    }
                    else
                    {
                        string actionLabel = GetActionShortLabel_MainForm(action);
                        string description = GetActionDescription_MainForm(action);
                        toolTipText = $"[{slotPrefix}] {actionLabel}\r\n{description}";
                    }
                }
                else
                {
                    toolTipText = $"{shortLabel}\r\nFunction: {slotPrefix}\r\nカスタムコマンドを割り当てることができます。";
                }
            }

            // Determine LayoutLabel (width base label from standard layer)
            string layoutLabel;
            if (!isShiftLayer && !isCtrlLayer && !isAltLayer)
            {
                layoutLabel = displayLabel;
            }
            else
            {
                string? normalCmdId = FunctionKeyProfileService.ResolveFunctionBarCommandId(
                    profile,
                    slot,
                    _settings.Input.FunctionBarCommandOverridesStandard,
                    _settings.Input.FunctionBarCommandOverridesFdCompatible,
                    _settings.Input.FunctionBarCommandOverridesShiftStandard,
                    _settings.Input.FunctionBarCommandOverridesShiftFdCompatible,
                    false,
                    _settings.Input.FunctionBarCommandOverridesCtrlStandard,
                    _settings.Input.FunctionBarCommandOverridesCtrlFdCompatible,
                    _settings.Input.FunctionBarCommandOverridesAltStandard,
                    _settings.Input.FunctionBarCommandOverridesAltFdCompatible,
                    false,
                    false);

                bool normalUnassigned = string.IsNullOrEmpty(normalCmdId) ||
                                        string.Equals(normalCmdId, "none", StringComparison.OrdinalIgnoreCase);
                if (profile == FunctionKeyProfile.Standard)
                {
                    normalUnassigned = false;
                }

                string normalShortLabel;
                if (normalUnassigned)
                {
                    normalShortLabel = "";
                }
                else if (profile == FunctionKeyProfile.FDCompatible)
                {
                    normalShortLabel = FunctionKeyProfileService.ResolveFdCompatibleFunctionBarShortLabel(slot, false, false, false);
                }
                else if (!string.IsNullOrEmpty(normalCmdId))
                {
                    normalShortLabel = FunctionKeyProfileService.ResolveFunctionBarDisplayLabelFromCommandId(profile, normalCmdId);
                }
                else
                {
                    normalShortLabel = def?.Label ?? "Cmd";
                }

                if (!normalUnassigned && !string.IsNullOrEmpty(normalCmdId) && !string.Equals(normalCmdId, "none", StringComparison.OrdinalIgnoreCase))
                {
                    var normalOverrides = GetActiveFunctionBarLabelOverrides(false, false, false, profile == FunctionKeyProfile.FDCompatible);
                    if (normalOverrides != null && normalOverrides.TryGetValue($"F{slot}", out var labelOverride) && labelOverride != null)
                    {
                        if (string.Equals(labelOverride.CommandId, normalCmdId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(labelOverride.Label))
                        {
                            normalShortLabel = InputSettings.NormalizeFunctionBarLabelText(labelOverride.Label);
                        }
                    }
                }

                if (normalUnassigned)
                {
                    layoutLabel = "";
                }
                else
                {
                    var normalOverrides = GetActiveFunctionBarLabelOverrides(false, false, false, profile == FunctionKeyProfile.FDCompatible);
                    layoutLabel = FunctionKeyProfileService.ResolveFunctionBarDisplayLabel(
                        profile,
                        slot,
                        false,
                        false,
                        false,
                        normalCmdId,
                        normalOverrides);
                }
            }

            models.Add(new FunctionBarSlotViewModel(
                slot,
                isShiftLayer,
                customCmdId,
                shortLabel,
                keyHint,
                hotKeyChar,
                displayLabel,
                isEnabled,
                toolTipText,
                layoutLabel,
                true // IsSlotVisible
            ));
        }

        return models;
    }

    private Dictionary<string, FunctionBarLabelOverride> GetActiveFunctionBarLabelOverrides(bool isShift, bool isCtrl, bool isAlt, bool isFdCompatible)
    {
        if (isCtrl)
        {
            return isFdCompatible
                ? _settings.Input.FunctionBarLabelOverridesCtrlFdCompatible
                : _settings.Input.FunctionBarLabelOverridesCtrlStandard;
        }

        if (isAlt)
        {
            return isFdCompatible
                ? _settings.Input.FunctionBarLabelOverridesAltFdCompatible
                : _settings.Input.FunctionBarLabelOverridesAltStandard;
        }

        if (isShift)
        {
            return isFdCompatible
                ? _settings.Input.FunctionBarLabelOverridesShiftFdCompatible
                : _settings.Input.FunctionBarLabelOverridesShiftStandard;
        }

        return isFdCompatible
            ? _settings.Input.FunctionBarLabelOverridesFdCompatible
            : _settings.Input.FunctionBarLabelOverridesStandard;
    }

    private void UpdateFunctionBar()
    {
        ApplyFunctionBarVisibilityForCurrentContext();
        var snapshot = BuildCommandUiSnapshot();
        if (_commandStateCoordinator.UsesBrowserFunctionBar(snapshot))
        {
            var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
            var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
            var models = BuildFunctionBarSlotModels(profile, isShift, isCtrl, isAlt);
            for (int i = 1; i <= 12; i++)
            {
                var model = models[i - 1];
                bool showEnabled = profile == FunctionKeyProfile.FDCompatible ? model.IsEnabled : true;
                SetFuncKeyText(i, model.DisplayLabel, showEnabled);
            }
        }
        else
        {
            // Viewer モード
            for (int i = 1; i <= 12; i++) SetFuncKeyText(i, "", false);
            SetFuncKeyText(1, "L:Enc ", true); // L キーによる文字コード切替
            SetFuncKeyText(2, "W:Wrap", true); // W キーによる折り返し切替
            SetFuncKeyText(3, "^F:Find", true); // Ctrl+F による検索入力
            SetFuncKeyText(4, "F3:Next", true); // F3 による前方検索
            SetFuncKeyText(5, "S+F3:Prv", true); // Shift+F3 による後方検索
            SetFuncKeyText(10, "Qt(En/Es)", true); // Enter / Esc による終了
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
        if (_uiMode != UIMode.Browser || !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        using var font = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);

        int index = HitTestFunctionKeyIndex(e.Location, functionBarPanel.ClientRectangle, font);
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
        if (_uiMode != UIMode.Browser || !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        if (e.Button != MouseButtons.Left) return;

        using var font = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);

        int index = HitTestFunctionKeyIndex(e.Location, functionBarPanel.ClientRectangle, font);
        if (index >= 0)
        {
            var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
            var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
            var models = BuildFunctionBarSlotModels(profile, isShift, isCtrl, isAlt);
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
        if (_uiMode != UIMode.Browser || !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        if (e.Button != MouseButtons.Left) return;

        using var font = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);

        int index = HitTestFunctionKeyIndex(e.Location, functionBarPanel.ClientRectangle, font);
        if (index < 0) return;

        var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
        var models = BuildFunctionBarSlotModels(profile, isShift, isCtrl, isAlt);
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
        if (_uiMode == UIMode.Browser && !ShouldShowBrowserFunctionBarForCurrentProfile()) return;
        int totalW = panel.ClientSize.Width;
        int totalH = panel.ClientSize.Height;
        if (totalW <= 0 || totalH <= 0) return;
        using var font = _headerPaintFont != null
            ? new Font(_headerPaintFont.FontFamily, _headerPaintFont.Size, _headerPaintFont.Style)
            : new Font("Consolas", 10F);

        var snapshot = _cachedCommandUiSnapshot;
        bool isCompatible = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) == FunctionKeyProfile.FDCompatible;
        var palette = GetFunctionBarColors(isCompatible);

        // 外枠全体を一度クリア
        using var clearBrush = new SolidBrush(palette.BackColor);
        e.Graphics.FillRectangle(clearBrush, e.ClipRectangle);

        var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
        Rectangle[]? activeRects = null;

        if (isCompatible)
        {
            // WinFD互換表示の上辺全幅罫線 (パレットの無効境界色に連動)
            using (var sepPen = new Pen(palette.DisabledBorderColor, 1))
            {
                e.Graphics.DrawLine(sepPen, 0, 0, totalW - 1, 0);
            }
        }

        var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        var models = BuildFunctionBarSlotModels(profile, isShift, isCtrl, isAlt);
        var layoutModels = BuildFunctionBarSlotModels(profile, false, false, false);
        var labels = layoutModels.Select(model => model.LayoutLabel).ToArray();
        activeRects = CalculateFunctionBarLabelRects(panel.ClientRectangle, font, labels);
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
                Size fullSize = TextRenderer.MeasureText(e.Graphics, displayText, font, rect.Size, TextFormatFlags.NoPadding);
                if (fullSize.Width > rect.Width)
                {
                    displayText = GetShortenedLabel(model.ShortLabel);
                }
                DrawFunctionBarButtonText(e.Graphics, rect, displayText, model.HotKeyChar, font, palette, isEnabled, isPressed);
            }
        }

        // 左端バッジ描画
        int badgeW = GetFunctionBarLayerBadgeWidth(isShift, isCtrl, isAlt);
        if (badgeW > 0)
        {
            DrawFunctionBarLayerBadge(e.Graphics, panel.ClientRectangle, activeRects, font, palette, isShift, isCtrl, isAlt);
        }
    }

    private void UpdateFunctionBarToolTip(int index, Point location)
    {
        if (index < 0 || !_settings.Input.ShowFunctionBarTooltips)
        {
            HideFunctionBarToolTip();
            return;
        }

        if (_fKeyToolTipIndex == index) return;

        var profile = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue);
        var (isShift, isCtrl, isAlt) = GetActiveFunctionBarLayer();
        var models = BuildFunctionBarSlotModels(profile, isShift, isCtrl, isAlt);
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
}
