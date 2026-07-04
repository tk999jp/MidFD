using System.Drawing;
using System.Windows.Forms;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    /// <summary>
    /// Phase 2g-fix4a: 各要素への配色適用を一括して行う。
    /// </summary>
    private void ApplyColorSettings()
    {
        // UIクロームは一覧配色に追従し、Viewer は従来のテーマ基調を維持する。
        MidFDColors.ApplyTheme(FileListColorResolver.NormalizeCoreTheme(_settings.Appearance?.ColorTheme));

        var uiThemeColors = UiThemeResolver.Resolve(_settings.Appearance);
        MidFDColors.BorderLine = uiThemeColors.BorderColor;
        MidFDColors.SeparatorLine = uiThemeColors.SeparatorColor;
        MidFDColors.ViewerBack = uiThemeColors.ViewerBackColor;
        MidFDColors.ViewerFore = uiThemeColors.ViewerForeColor;

        _resolvedColors = FileListColorResolver.ResolveColors(_settings);

        var headerColors = HeaderColorPaletteResolver.Resolve(_settings.Appearance);
        // Row 2 (現在は monolithic データストア、表示は Paint へ移譲)
        lblPage.ForeColor = headerColors.HeaderRow2Fore;
        lblTotal.ForeColor = headerColors.HeaderRow2Fore;
        lblUsed.ForeColor = headerColors.HeaderRow2Fore;
        lblFree.ForeColor = headerColors.HeaderRow2Fore;
        // Phase 2g-fix4b: 表示のみ抑制 (LayoutHeaderZones が計測できるようにコントロールは残す)
        lblPage.Visible = false;
        lblTotal.Visible = false;
        lblUsed.Visible = false;
        lblFree.Visible = false;
        // Row 3 (Meta)
        lblSort.ForeColor = headerColors.HeaderMetaFore;
        lblItemAttr.ForeColor = headerColors.HeaderMetaFore;
        lblFileDate.ForeColor = headerColors.HeaderMetaFore;
        lblFileStats.ForeColor = headerColors.HeaderMetaFore;
        lblFileStatsEx.ForeColor = headerColors.HeaderMetaFore;
        lblClock.ForeColor = headerColors.HeaderClockFore;
        lblClock.BackColor = uiThemeColors.HeaderBackColor;
        // Row 4 (Path)
        lblPath.ForeColor = headerColors.HeaderPathFore;
        // Row 5 (Name)
        lblName.ForeColor = headerColors.HeaderNameFore;
        // 一覧部
        fileListView.ForeColor = _resolvedColors.NormalFile;
        fileListView.BackColor = _resolvedColors.Background;
        browserPanel.ForeColor = _resolvedColors.NormalFile;
        browserPanel.BackColor = _resolvedColors.Background;
        string menuPreset = UiThemeResolver.MapFromDisplayColor(_settings.Appearance?.ColorTheme);
        var menuThemeColors = UiThemeResolver.Resolve(menuPreset);
        mainMenuStrip.BackColor = menuThemeColors.ChromeBackColor;
        mainMenuStrip.ForeColor = menuThemeColors.ChromeForeColor;
        ApplyMenuStripRenderer(
            FileListColorResolver.NormalizeCoreTheme(_settings.Appearance?.ColorTheme, _settings) == "Light",
            menuThemeColors.ChromeForeColor);
        foreach (ToolStripItem item in mainMenuStrip.Items)
        {
            item.BackColor = menuThemeColors.ChromeBackColor;
            item.ForeColor = menuThemeColors.ChromeForeColor;
            if (item is ToolStripMenuItem rootItem)
            {
                ApplyDropDownTheme(rootItem, menuThemeColors.ChromeBackColor, menuThemeColors.ChromeForeColor);
            }
        }
        UpdateBrowserToolbarVisibility();
        viewerPanel.BackColor = uiThemeColors.ViewerBackColor;
        viewerTextBox.BackColor = uiThemeColors.ViewerBackColor;
        viewerTextBox.ForeColor = uiThemeColors.ViewerForeColor;
        viewerMessageLabel.BackColor = uiThemeColors.ViewerBackColor;
        viewerMessageLabel.ForeColor = uiThemeColors.ViewerForeColor;
        // セパレーター
        sepBeforeTopPanel.BackColor = uiThemeColors.BorderColor;
        sepAfterRow2.BackColor = uiThemeColors.SeparatorColor;
        sepAfterRow3.BackColor = uiThemeColors.SeparatorColor;
        sepAfterRow4.BackColor = uiThemeColors.BorderColor;
        // 背景色の一貫性
        outerHostPanel.BackColor = uiThemeColors.ChromeBackColor;
        mainAreaPanel.BackColor = uiThemeColors.ChromeBackColor;
        headerPanel.BackColor = uiThemeColors.HeaderBackColor;
        topPanel.BackColor = uiThemeColors.HeaderBackColor;
        infoRow2Panel.BackColor = uiThemeColors.HeaderBackColor;
        infoRow3Panel.BackColor = uiThemeColors.HeaderBackColor;
        infoRow4Panel.BackColor = uiThemeColors.HeaderBackColor;
        titleHeaderPanel.BackColor = uiThemeColors.HeaderBackColor;
        contentFramePanel.BackColor = uiThemeColors.HeaderBackColor;
        functionBarPanel.BackColor = uiThemeColors.ChromeBackColor;

        headerZone1.BackColor = uiThemeColors.HeaderBackColor;
        headerZone2.BackColor = uiThemeColors.HeaderBackColor;
        headerZone3.BackColor = uiThemeColors.HeaderBackColor;
        headerZone4.BackColor = uiThemeColors.HeaderBackColor;
        lblClock.BackColor = uiThemeColors.HeaderBackColor;
        lblPath.BackColor = uiThemeColors.HeaderBackColor;
        lblSort.BackColor = uiThemeColors.HeaderBackColor;
        lblItemAttr.BackColor = uiThemeColors.HeaderBackColor;
        lblFileDate.BackColor = uiThemeColors.HeaderBackColor;
        lblFileStats.BackColor = uiThemeColors.HeaderBackColor;
        lblFileStatsEx.BackColor = uiThemeColors.HeaderBackColor;
        lblName.BackColor = uiThemeColors.HeaderBackColor;
        lblTitle.BackColor = uiThemeColors.HeaderBackColor;

        bool isCompatible = FunctionKeyProfileService.ResolveProfile(CurrentFunctionKeyProfileValue) == FunctionKeyProfile.FDCompatible;
        var functionColors = GetFunctionBarColors(isCompatible);
        functionBarPanel.BackColor = functionColors.BackColor;

        // FunctionBar のラベル色を更新
        if (lblFuncKeys != null)
        {
            foreach (var lbl in lblFuncKeys)
            {
                lbl.BackColor = functionColors.EnabledBackColor;
                lbl.ForeColor = functionColors.EnabledTextColor;
            }
        }
        statusStrip.BackColor = uiThemeColors.StatusBackColor;
        statusStrip.ForeColor = uiThemeColors.StatusForeColor;
        statusLabel.BackColor = uiThemeColors.StatusBackColor;
        if (_resolvedColors != null)
        {
            if (_notificationService != null)
            {
                _notificationService.ApplyCurrentColors();
            }
            else
            {
                statusLabel.ForeColor = _resolvedColors.StatusNormal;
            }
        }
        else
        {
            statusLabel.ForeColor = uiThemeColors.StatusForeColor;
        }
        if (_browserTabHostPanel != null)
        {
            _browserTabHostPanel.BackColor = uiThemeColors.ChromeBackColor;
        }
        if (_browserTabStrip != null)
        {
            ApplyBrowserTabStripDisplaySettings();
            _browserTabStrip.BackColor = uiThemeColors.ChromeBackColor;
            _browserTabStrip.ForeColor = uiThemeColors.ChromeForeColor;
            _browserTabStrip.ActiveTabBackColor = MidFDColors.ListSelectedBack;
            _browserTabStrip.InactiveTabBackColor = uiThemeColors.ChromeBackColor;
            _browserTabStrip.TabBorderColor = uiThemeColors.BorderColor;
            _browserTabStrip.ActiveTabTextColor = uiThemeColors.AccentColor;
            _browserTabStrip.InactiveTabTextColor = uiThemeColors.ChromeForeColor;
            _browserTabStrip.Invalidate();
        }
        foreach (ListViewItem item in fileListView.Items)
        {
            if (item.Tag is string fullPath)
            {
                ApplyMarkColor(item, fullPath);
            }
        }
        functionBarPanel.Invalidate();

        fileListView.Invalidate();
        browserPanel.Invalidate();
    }
    private void ApplyDropDownTheme(ToolStripDropDownItem item, Color backColor, Color foreColor)
    {
        Color dropDownBack = Color.WhiteSmoke;
        Color dropDownFore = Color.Black;

        item.DropDown.BackColor = dropDownBack;
        item.DropDown.ForeColor = dropDownFore;
        MidFD.Presentation.MenuStripPresentationHelper.ConfigureGlobalDropDownProperties(item, null);
        foreach (ToolStripItem child in item.DropDownItems)
        {
            child.BackColor = dropDownBack;
            child.ForeColor = dropDownFore;
            if (child is ToolStripDropDownItem childDropDown)
            {
                ApplyDropDownTheme(childDropDown, dropDownBack, dropDownFore);
            }
        }
    }
}
