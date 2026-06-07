using MidFD.Models;

namespace MidFD.Configuration;

public class AppSettings
{
    public string Profile { get; set; } = string.Empty;
    public InputSettings Input { get; set; } = new InputSettings();
    public SevenZipSettings SevenZip { get; set; } = new SevenZipSettings();
    public ExternalToolsSettings ExternalTools { get; set; } = new ExternalToolsSettings();
    public FileOperationsSettings FileOperations { get; set; } = new FileOperationsSettings();
    public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
    public LoggingSettings Logging { get; set; } = new LoggingSettings();
    public PreviewSettings Preview { get; set; } = new PreviewSettings();
    public RenameSettings Rename { get; set; } = new RenameSettings();
    public List<string> QuickAccess { get; set; } = new();
    public BrowserTabSettings BrowserTabs { get; set; } = new BrowserTabSettings();
    public FontSettings Fonts { get; set; } = new FontSettings();
    public WindowSettings Window { get; set; } = new WindowSettings();
    public SessionSettings Session { get; set; } = new SessionSettings();

    public AppSettings Clone()
    {
        return new AppSettings
        {
            Profile = Profile,
            Input = Input.Clone(),
            SevenZip = SevenZip.Clone(),
            ExternalTools = ExternalTools.Clone(),
            FileOperations = FileOperations.Clone(),
            Appearance = Appearance.Clone(),
            Logging = Logging.Clone(),
            Preview = Preview.Clone(),
            Rename = Rename.Clone(),
            QuickAccess = new List<string>(QuickAccess),
            BrowserTabs = BrowserTabs.Clone(),
            Fonts = Fonts.Clone(),
            Window = Window.Clone(),
            Session = Session.Clone()
        };
    }
}

public class AppearanceSettings
{
    public string ColorTheme { get; set; } = "ClassicCyan";
    public bool ShowBrowserTabCategoryRow { get; set; } = true;
    public bool ShowExtensions { get; set; } = true;
    public bool ShowDirectoryMarker { get; set; } = true;
    public bool ShowHiddenFiles { get; set; } = false;
    public bool ShowItemIcons { get; set; } = true;
    public bool UseUnderlineCursor { get; set; } = false;
    public bool ShowFileSizeAndDateInBrowser { get; set; } = false;
    public BrowserFileDisplayMode FileDisplayMode { get; set; } = BrowserFileDisplayMode.NameOnly;
    public bool ShowSystemInfo { get; set; } = true;
    public bool ShowLightweightInfo { get; set; } = true;
    public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm";
    public string SizeFormat { get; set; } = "HumanReadable";
    public bool ShowBrowserToolbar { get; set; } = false;

    public bool UseCustomFileListColors { get; set; } = false;
    public bool EnableSemanticColorAssist { get; set; } = true;
    public CustomFileListColorSettings CustomFileListColors { get; set; } = new();
    public List<CustomFileListColorPreset> CustomFileListColorPresets { get; set; } = new();

    // UIクローム/Viewer手動指定色
    public bool CustomUiThemeColorsEnabled { get; set; } = false;
    public string? CustomFilerBackColor { get; set; }
    public string? CustomFilerForeColor { get; set; }
    public string? CustomViewerBackColor { get; set; }
    public string? CustomViewerForeColor { get; set; }


    public AppearanceSettings Clone()
    {
        var clone = (AppearanceSettings)MemberwiseClone();
        clone.CustomFileListColors = CustomFileListColors.Clone();
        clone.CustomFileListColorPresets = CustomFileListColorPresets.Select(static preset => preset.Clone()).ToList();
        return clone;
    }

    public BrowserFileDisplayMode ResolveFileDisplayMode()
    {
        if (FileDisplayMode == BrowserFileDisplayMode.NameOnly && ShowFileSizeAndDateInBrowser)
        {
            return BrowserFileDisplayMode.NameSizeDate;
        }

        return FileDisplayMode;
    }
}

public enum BrowserFileDisplayMode
{
    NameOnly = 0,
    NameSize = 1,
    NameSizeDate = 2
}

public class LoggingSettings
{
    public bool IsEnabled { get; set; } = false;
    public bool IsDetailedEnabled { get; set; } = false;
    public bool DefaultOffMigrationApplied { get; set; } = false;
    public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;
    public int RetentionDays { get; set; } = 14;

    public LoggingSettings Clone() => (LoggingSettings)MemberwiseClone();
}

public class WindowSettings
{
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public FormWindowState State { get; set; } = FormWindowState.Normal;

    public WindowSettings Clone() => (WindowSettings)MemberwiseClone();
}

public class SessionSettings
{
    public string? LastPath { get; set; }
    public bool RestoreLastPath { get; set; } = true;
    public bool RestoreTabsOnStartup { get; set; } = false;
    // Browser tab restore の永続化正本。通常 save ではこの snapshot のみを保存対象にする。
    public BrowserTabRestoreSnapshot? BrowserTabRestoreSnapshot { get; set; }
    // 互換 mirror。古い設定の取り込みと load 後の互換参照用で、通常 save では永続化しない。
    public List<BrowserTabSessionState> OpenTabs { get; set; } = new();
    public int ActiveTabIndex { get; set; }
    public string ActiveBrowserTabCategoryId { get; set; } = BrowserTabSettings.DefaultCategoryId;
    public List<BrowserTabCategorySessionState> BrowserTabCategories { get; set; } = new();
    public bool PersistMarksAcrossRestart { get; set; } = true;
    public List<string> PersistedMarkedPaths { get; set; } = new();
    public bool RestoreWindowBounds { get; set; } = true;
    public bool RestoreColumnCount { get; set; } = true;
    public bool RestoreSort { get; set; } = true;
    public int LastColumnCount { get; set; } = 3;
    public SortKind LastSortKind { get; set; } = SortKind.Name;
    public bool LastSortAscending { get; set; } = true;
    public List<string> DirectoryMoveHistory { get; set; } = new();
    public List<string> MoveDestinationHistory { get; set; } = new();

    public SessionSettings Clone()
    {
        var clone = (SessionSettings)MemberwiseClone();
        clone.BrowserTabRestoreSnapshot = BrowserTabRestoreSnapshot?.Clone();
        clone.OpenTabs = OpenTabs.Select(static tab => tab.Clone()).ToList();
        clone.BrowserTabCategories = BrowserTabCategories.Select(static category => category.Clone()).ToList();
        clone.PersistedMarkedPaths = new List<string>(PersistedMarkedPaths);
        clone.DirectoryMoveHistory = new List<string>(DirectoryMoveHistory ?? new List<string>());
        clone.MoveDestinationHistory = new List<string>(MoveDestinationHistory ?? new List<string>());
        return clone;
    }

    public void ClearBrowserTabRestoreState()
    {
        BrowserTabRestoreSnapshot = null;
        ClearBrowserTabRestoreLegacyMirror();
    }

    public void ClearBrowserTabRestoreLegacyMirror()
    {
        OpenTabs = new List<BrowserTabSessionState>();
        ActiveTabIndex = 0;
        ActiveBrowserTabCategoryId = BrowserTabSettings.DefaultCategoryId;
        BrowserTabCategories = new List<BrowserTabCategorySessionState>();
    }
}

public class BrowserTabRestoreSnapshot
{
    public string ActiveCategoryId { get; set; } = BrowserTabSettings.DefaultCategoryId;
    public List<BrowserTabRestoreCategoryState> Categories { get; set; } = new();

    public BrowserTabRestoreSnapshot Clone()
    {
        return new BrowserTabRestoreSnapshot
        {
            ActiveCategoryId = ActiveCategoryId,
            Categories = Categories.Select(static category => category.Clone()).ToList()
        };
    }
}

public class BrowserTabRestoreCategoryState
{
    public string Id { get; set; } = BrowserTabSettings.DefaultCategoryId;
    public string DisplayName { get; set; } = "既定";
    public int ActiveTabIndex { get; set; }
    public List<BrowserTabSessionState> OpenTabs { get; set; } = new();

    public BrowserTabRestoreCategoryState Clone()
    {
        return new BrowserTabRestoreCategoryState
        {
            Id = Id,
            DisplayName = DisplayName,
            ActiveTabIndex = ActiveTabIndex,
            OpenTabs = OpenTabs.Select(static tab => tab.Clone()).ToList()
        };
    }
}

public class BrowserTabSettings
{
    public const string DefaultCategoryId = "default";
    public const int DefaultMaxTabsPerCategory = 30;
    public const int SafetyMaxTabsPerCategory = 100;

    public int MaxTabsPerCategory { get; set; } = DefaultMaxTabsPerCategory;
    public List<BrowserTabCategoryDefinition> Categories { get; set; } = new();

    public BrowserTabSettings Clone()
    {
        return new BrowserTabSettings
        {
            MaxTabsPerCategory = MaxTabsPerCategory,
            Categories = Categories.Select(static category => category.Clone()).ToList()
        };
    }
}

public class BrowserTabCategoryDefinition
{
    public string Id { get; set; } = BrowserTabSettings.DefaultCategoryId;
    public string DisplayName { get; set; } = "既定";

    public BrowserTabCategoryDefinition Clone()
    {
        return new BrowserTabCategoryDefinition
        {
            Id = Id,
            DisplayName = DisplayName
        };
    }
}

public class BrowserTabCategorySessionState
{
    public string CategoryId { get; set; } = BrowserTabSettings.DefaultCategoryId;
    public List<BrowserTabSessionState> OpenTabs { get; set; } = new();
    public int ActiveTabIndex { get; set; }

    public BrowserTabCategorySessionState Clone()
    {
        return new BrowserTabCategorySessionState
        {
            CategoryId = CategoryId,
            OpenTabs = OpenTabs.Select(static tab => tab.Clone()).ToList(),
            ActiveTabIndex = ActiveTabIndex
        };
    }
}

public class BrowserTabSessionState
{
    public Guid TabId { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public string StartupPath { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }
    public TabFilterLockState FilterLock { get; set; } = new();
    public List<string> MarkedPaths { get; set; } = new();
    public List<string> BackHistory { get; set; } = new();
    public List<string> ForwardHistory { get; set; } = new();
    public Dictionary<string, string> LastVisitedPathByDrive { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? FocusTargetName { get; set; }
    public int CursorIndex { get; set; }
    public int ColumnCount { get; set; } = 3;
    public SortKind SortKind { get; set; } = SortKind.Name;
    public bool SortAscending { get; set; } = true;

    public BrowserTabSessionState Clone()
    {
        return new BrowserTabSessionState
        {
            TabId = TabId,
            CurrentPath = CurrentPath,
            IsLocked = IsLocked,
            StartupPath = StartupPath,
            IsReadOnly = IsReadOnly,
            FilterLock = FilterLock?.Clone() ?? new TabFilterLockState(),
            MarkedPaths = new List<string>(MarkedPaths ?? new List<string>()),
            BackHistory = new List<string>(BackHistory ?? new List<string>()),
            ForwardHistory = new List<string>(ForwardHistory ?? new List<string>()),
            LastVisitedPathByDrive = new Dictionary<string, string>(LastVisitedPathByDrive ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            FocusTargetName = FocusTargetName,
            CursorIndex = CursorIndex,
            ColumnCount = ColumnCount,
            SortKind = SortKind,
            SortAscending = SortAscending
        };
    }
}

public class FontSettings
{
    public string FileListFontFamily { get; set; } = "Consolas";
    public float FileListFontSize { get; set; } = 11.0f;
    public string ViewerFontFamily { get; set; } = "Consolas";
    public float ViewerFontSize { get; set; } = 10.0f;

    public FontSettings Clone() => (FontSettings)MemberwiseClone();
}

public class PreviewSettings
{
    public bool IsVisible { get; set; } = false;
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 400;
    public bool IsManuallyPositioned { get; set; } = false;
    public bool ViewerWordWrap { get; set; } = false;
    public bool ReuseImageViewer { get; set; } = true;
    public bool CloseImageViewerOnNonImageSelection { get; set; } = false;
    public bool RememberImageViewerBounds { get; set; } = true;
    public int ImageViewerX { get; set; } = -1;
    public int ImageViewerY { get; set; } = -1;
    public int ImageViewerWidth { get; set; } = 960;
    public int ImageViewerHeight { get; set; } = 720;
    public int InitialFitLimitWidth { get; set; } = 1920;
    public int InitialFitLimitHeight { get; set; } = 1080;
    public int VideoSkipSeconds { get; set; } = 0;
    public bool VideoStillInitialSecondsMigratedToZero { get; set; } = false;
    public bool VideoStillPreviewEnabled { get; set; } = true;
    public string? VideoToolDirectory { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoStillPreviewFfmpegPath { get; set; }
    public int VideoPlaybackVolumePercent { get; set; } = 100;
    public bool VideoEnterPlaysExternal { get; set; } = false;

    public PreviewSettings Clone() => (PreviewSettings)MemberwiseClone();
}

public class RenameSettings
{
    public bool RememberLastTemplate { get; set; }
    public string? LastTemplate { get; set; }

    public RenameSettings Clone() => (RenameSettings)MemberwiseClone();
}

public class SevenZipSettings
{
    public string? ExePath { get; set; }
    public SevenZipSettings Clone() => (SevenZipSettings)MemberwiseClone();
}

public class ExternalToolsSettings
{
    /// <summary>外部 Viewer の実行ファイルパス。未設定の場合は null。</summary>
    public string? ExternalViewerPath { get; set; }

    /// <summary>外部 Editor の実行ファイルパス。未設定の場合は null。</summary>
    public string? ExternalEditorPath { get; set; }

    /// <summary>外部 Diff ツールの実行ファイルパス。未設定の場合は null。</summary>
    public string? ExternalDiffPath { get; set; }

    public bool FallbackToShellWhenViewerMissing { get; set; } = true;
    public bool FallbackToShellWhenEditorMissing { get; set; } = true;

    public ExternalToolsSettings Clone() => (ExternalToolsSettings)MemberwiseClone();
}

public enum ManagedTrashStoreMode
{
    Json,
    Sqlite
}

public class FileOperationsSettings
{
    public bool ConfirmDelete { get; set; } = true;
    public bool ConfirmPermanentDelete { get; set; } = true;
    public bool UseRecycleBinByDefault { get; set; } = true;
    public bool UseMidFdManagedTrash { get; set; } = false;
    public ManagedTrashStoreMode ManagedTrashStoreMode { get; set; } = ManagedTrashStoreMode.Json;
    public bool ReloadAfterFileOperation { get; set; } = true;
    public bool SelectCreatedItemAfterCreate { get; set; } = true;
    public bool ClipboardPasteTextAsFileEnabled { get; set; } = false;
    public bool EnableDragArchiveHandoff { get; set; } = false;
    public bool IncludeDragZipManifest { get; set; } = false;

    public FileOperationsSettings Clone() => (FileOperationsSettings)MemberwiseClone();
}

public class CustomFileListColorSettings
{
    public string? Background { get; set; }
    public string? NormalFile { get; set; }
    public string? Directory { get; set; }
    public string? ReadOnly { get; set; }
    public string? Hidden { get; set; }
    public string? System { get; set; }
    public string? Marked { get; set; }
    public string? SelectedBackground { get; set; }
    public string? SelectedForeground { get; set; }

    public CustomFileListColorSettings Clone() => (CustomFileListColorSettings)MemberwiseClone();
}

public class CustomFileListColorPreset
{
    public string Name { get; set; } = string.Empty;
    public CustomFileListColorSettings Colors { get; set; } = new();

    public CustomFileListColorPreset Clone()
    {
        return new CustomFileListColorPreset
        {
            Name = Name,
            Colors = Colors.Clone()
        };
    }
}
