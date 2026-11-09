namespace MidFD.Commands;

public static class CommandIds
{
    public const string BrowserNavigateParent = "browser.navigate.parent";
    public const string BrowserNavigateDriveRoot = "browser.navigate.drive_root";
    public const string BrowserNavigateBack = "browser.navigate.back";
    public const string BrowserNavigateForward = "browser.navigate.forward";
    public const string BrowserReload = "browser.reload";
    public const string BrowserExecute = "browser.execute";
    public const string BrowserDefaultOpen = "browser.default_open";
    public const string BrowserOpenPlaybackLocked = "browser.media.open_playback_locked";
    public const string BrowserDefaultOpenMarked = "browser.default_open_marked";
    public const string BrowserOpenWith = "browser.open_with";
    public const string BrowserSendTo = "browser.send_to";
    public const string BrowserDragOut = "browser.drag.out";
    public const string BrowserOpenWithDiff = "browser.open_with_diff";
    public const string BrowserLaunchConfiguredExternalTool = "browser.external_tool.launch";
    public const string BrowserOpenCommandDialog = "browser.open_command_dialog";
    public const string BrowserMarkAllFiles = "browser.mark.all_files";
    public const string BrowserMarkAllItems = "browser.mark.all_items";
    public const string BrowserCursorTop = "browser.cursor.top";
    public const string BrowserCursorBottom = "browser.cursor.bottom";
    public const string BrowserNamePrefixJump = "browser.name_prefix_jump";
    public const string BrowserChangeAttributes = "browser.change.attributes";
    public const string BrowserOpenExplorer = "browser.open.explorer";
    public const string BrowserRevealInExplorer = "browser.reveal_in_explorer";
    public const string BrowserOpenShell = "browser.open.shell";
    public const string BrowserOpenExternalEditor = "browser.open.external_editor";
    public const string BrowserOpenCommandPrompt = "browser.open.command_prompt";
    public const string BrowserCreateDirectory = "browser.create_directory";
    public const string BrowserCreateFile = "browser.create_file";
    public const string BrowserPathEntryOpen = "browser.path_entry.open";
    public const string LegacyBrowserViewColumnCountSet = "browser.view.column_count.set";
    public const string BrowserViewColumnCount1 = "browser.view.column_count.1";
    public const string BrowserViewColumnCount2 = "browser.view.column_count.2";
    public const string BrowserViewColumnCount3 = "browser.view.column_count.3";
    public const string BrowserViewColumnCount4 = "browser.view.column_count.4";
    public const string BrowserViewColumnCount5 = "browser.view.column_count.5";
    public const string BrowserViewColumnCount6 = "browser.view.column_count.6";
    public const string BrowserViewColumnCount7 = "browser.view.column_count.7";
    public const string BrowserViewColumnCount8 = "browser.view.column_count.8";
    public const string BrowserViewColumnCount9 = "browser.view.column_count.9";
    public const string BrowserViewDisplayModeSet = "browser.view.display_mode.set";
    public const string BrowserNavigateToPath = "browser.navigate.to_path";
    public const string BrowserMarkSlotRestore = "browser.mark_slot.restore";
    public const string BrowserMarkSlotSave = "browser.mark_slot.save";
    public const string BrowserMarkSlotRename = "browser.mark_slot.rename";
    public const string BrowserMarkSlotDelete = "browser.mark_slot.delete";
    public const string BrowserMarkSlotRemoveItems = "browser.mark_slot.remove_items";
    public const string BrowserMarkSlotExport = "browser.mark_slot.export";
    public const string BrowserMarkSlotImport = "browser.mark_slot.import";
    public const string BrowserMarkSlotsExportAll = "browser.mark_slots.export_all";
    public const string BrowserMarkSlotsImportAll = "browser.mark_slots.import_all";
    public const string BrowserMarkSlotClipboardImport = "browser.mark_slot.clipboard_import";
    public const string BrowserMarkSlotSetOperationPreview = "browser.mark_slot.set_operation.preview";
    public const string BrowserMarkSlotSetOperationApply = "browser.mark_slot.set_operation.apply";
    public const string BrowserMarkSlotSetOperationSave = "browser.mark_slot.set_operation.save";
    public const string BrowserMarkClear = "browser.mark.clear";
    public const string ArchiveContentsOpen = "archive.contents.open";
    public const string ArchiveContentsNavigate = "archive.contents.navigate";
    public const string ArchiveContentsPreviewText = "archive.contents.preview_text";
    public const string ArchiveOpenInSevenZipFileManager = "archive.open_in_sevenzip_file_manager";
    public const string BrowserPreview = "browser.preview";
    public const string BrowserSort = "browser.sort";
    public const string BrowserFilter = "browser.filter";
    public const string BrowserSearch = "browser.search";
    public const string BrowserSearchCloseResults = "browser.search.results.close";
    public const string BrowserSearchExportResults = "browser.search.results.export";
    public const string BrowserSearchResultsFilter = "browser.search.results.filter";
    public const string BrowserSearchResultsOpenHitInEditor = "browser.search.results.open_hit_in_editor";
    public const string BrowserSearchResultsActivate = "browser.search.results.activate";
    public const string BrowserSearchResultsActivateInNewTab = "browser.search.results.activate_in_new_tab";
    public const string BrowserFilterClear = "browser.filter.clear";
    public const string BrowserTree = "browser.tree";
    public const string BrowserQuickAccess = "browser.quick_access";
    public const string BrowserQuickAccessActivate = "browser.quick_access.activate";
    public const string BrowserQuickAccessRegister = "browser.quick_access.register";
    public const string BrowserQuickAccessEntryAdd = "browser.quick_access.entry.add";
    public const string BrowserQuickAccessEntryUpdate = "browser.quick_access.entry.update";
    public const string BrowserQuickAccessEntryDelete = "browser.quick_access.entry.delete";
    public const string BrowserQuickAccessReorder = "browser.quick_access.reorder";
    public const string BrowserLogdisk = "browser.logdisk";
    public const string BrowserDriveA = "browser.drive.a";
    public const string BrowserDriveB = "browser.drive.b";
    public const string BrowserDriveC = "browser.drive.c";
    public const string BrowserDriveD = "browser.drive.d";
    public const string BrowserDriveE = "browser.drive.e";
    public const string BrowserDriveF = "browser.drive.f";
    public const string BrowserDriveG = "browser.drive.g";
    public const string BrowserDriveH = "browser.drive.h";
    public const string BrowserDriveI = "browser.drive.i";
    public const string ArchiveUnpack = "archive.unpack";
    public const string ArchivePack = "archive.pack";
    public const string ArchiveHash = "archive.hash";
    public const string ArchiveHashResultCopy = "archive.hash.result.copy";
    public const string BrowserCopyFullPath = "browser.copy.full_path";
    public const string BrowserCopyCurrentPath = "browser.copy.current_path";
    public const string BrowserCopyItemName = "browser.copy.item_name";
    public const string BrowserClipboardCopy = "browser.clipboard.copy";
    public const string BrowserTabNew = "browser.tab.new";
    public const string BrowserOpenInNewTab = "browser.open_in_new_tab";
    public const string BrowserTabNext = "browser.tab.next";
    public const string BrowserTabPrevious = "browser.tab.previous";
    public const string BrowserTabActivate = "browser.tab.activate";
    public const string BrowserTabHistoryBack = "browser.tab.history.back";
    public const string BrowserTabHistoryForward = "browser.tab.history.forward";
    public const string BrowserTabHistoryShow = "browser.tab.history.show";
    public const string BrowserTabLayoutToggle = "browser.tab.layout.toggle";
    public const string BrowserTabLayoutSet = "browser.tab.layout.set";
    public const string BrowserTabNavigationWidthSet = "browser.tab.navigation_width.set";
    public const string BrowserTabReadOnlyToggle = "browser.tab.read_only.toggle";
    public const string BrowserTabGroupCreate = "browser.tab_group.create";
    public const string BrowserTabGroupAdd = "browser.tab_group.add";
    public const string BrowserTabGroupRemove = "browser.tab_group.remove";
    public const string BrowserTabGroupRename = "browser.tab_group.rename";
    public const string BrowserTabGroupExpandedSet = "browser.tab_group.expanded.set";
    public const string BrowserTabCategoryAdd = "browser.tab_category.add";
    public const string BrowserTabCategoryRename = "browser.tab_category.rename";
    public const string BrowserTabCategoryDelete = "browser.tab_category.delete";
    public const string BrowserTabCategoryMoveLeft = "browser.tab_category.move_left";
    public const string BrowserTabCategoryMoveRight = "browser.tab_category.move_right";
    public const string BrowserTabCategoryNext = "browser.tab_category.next";
    public const string BrowserTabCategoryPrevious = "browser.tab_category.previous";
    public const string BrowserTabCategoryActivate = "browser.tab_category.activate";
    public const string BrowserTabReorder = "browser.tab.reorder";
    public const string BrowserTabTreeDrop = "browser.tab.tree_drop";
    public const string BrowserTabCategoryReorder = "browser.tab_category.reorder";
    public const string BrowserTabCategoryManage = "browser.tab_category.manage";
    public const string BrowserTabClose = "browser.tab.close";
    public const string BrowserTabRestoreClosed = "browser.tab.restore_closed";
    public const string ClipboardPaste = "clipboard.paste";
    public const string ClipboardCut = "browser.clipboard.cut";
    public const string BrowserProperties = "browser.properties";
    public const string EditUndo = "edit.undo";
    public const string EditRedo = "edit.redo";
    public const string AppOpenSystemInformation = "app.open_system_information";
    public const string AppOpenNewInstance = "app.open_new_instance";
    public const string AppOpenControlPanel = "app.open_control_panel";
    public const string AppOpenExternalUrl = "app.open_external_url";
    public const string AppCommandPaletteFavoriteToggle = "app.command_palette.favorite.toggle";
    public const string AppExternalToolDefinitionsSave = "app.external_tool_definitions.save";
    public const string AppOpenSettings = "app.open_settings";
    public const string AppSettingsSave = "app.settings.save";
    public const string AppHideBrowserToolbar = "app.browser_toolbar.hide";
    public const string FileOperationCancel = "file.operation.cancel";
    public const string AppSettingsImport = "app.settings.import";
    public const string AppSettingsExport = "app.settings.export";
    public const string AppExit = "app.exit";
    public const string AppOpenCommandLauncher = "app.open_command_launcher";
    public const string BrowserShowHelp = "browser.show_help";
    public const string BrowserOpenMarkSlot = "browser.open_mark_slot";
    public const string BrowserTabFilterLock = "browser.tab.filter_lock";
    public const string BrowserTabFilterLockClear = "browser.tab.filter_lock.clear";
    public const string BrowserTabLock = "browser.tab.lock";
    public const string BrowserTabCloseRight = "browser.tab.close_right";
    public const string BrowserTabCloseLeft = "browser.tab.close_left";
    public const string BrowserTabCloseOther = "browser.tab.close_other";
    public const string AppOpenCommandList = "app.open_command_list";
    public const string FileCopy = "file.copy";
    public const string FileMove = "file.move";
    public const string FileRename = "file.rename";
    public const string FileDelete = "file.delete";
    public const string FileDeletePermanent = "file.delete.permanent";
    public const string AppEmptyManagedTrash = "app.empty_managed_trash";
    public const string AppOpenManagedTrash = "app.open_managed_trash";
    public const string AppRestoreManagedTrash = "app.restore_managed_trash";
    public const string AppDeleteManagedTrashForever = "app.delete_managed_trash_forever";
    public const string AppCleanMissingManagedTrash = "app.clean_missing_managed_trash";
    public const string AppOpenWorkspaceSnapshots = "app.workspace_snapshots.open";
    public const string WorkspaceSnapshotSave = "workspace_snapshot.save";
    public const string WorkspaceSnapshotRestore = "workspace_snapshot.restore";
    public const string WorkspaceSnapshotRename = "workspace_snapshot.rename";
    public const string WorkspaceSnapshotDelete = "workspace_snapshot.delete";
    public const string WorkspaceSnapshotExport = "workspace_snapshot.export";
    public const string WorkspaceSnapshotImport = "workspace_snapshot.import";
    public const string WorkspaceSnapshotExportAll = "workspace_snapshot.export_all";
    public const string WorkspaceSnapshotImportAll = "workspace_snapshot.import_all";
    public const string BrowserCursorMove = "browser.cursor.move";
    public const string BrowserMarkToggle = "browser.mark.toggle";
    public const string BrowserMarkRangeAdd = "browser.mark.range.add";
    public const string BrowserMarkInvert = "browser.mark.invert";
    public const string FileImportExternalDrop = "file.import.external_drop";
    public const string ImageViewerOpen = "image_viewer.open";
    public const string ImageViewerTogglePresentationMode = "image_viewer.presentation.toggle";
    public const string ImageViewerSaveAs = "image_viewer.save_as";
    public const string ImageViewerClose = "image_viewer.close";
    public const string ImageViewerCopy = "image_viewer.copy";
    public const string ImageViewerPaste = "image_viewer.paste";
    public const string ImageViewerPasteSelectionStretch = "image_viewer.paste_selection_stretch";
    public const string ImageViewerPasteSelectionOriginal = "image_viewer.paste_selection_original";
    public const string ImageViewerCopySvg = "image_viewer.copy_svg";
    public const string ImageViewerQuantize = "image_viewer.quantize";
    public const string ImageViewerReset = "image_viewer.reset";
    public const string ImageViewerRotateRight = "image_viewer.rotate_right";
    public const string ImageViewerRotateLeft = "image_viewer.rotate_left";
    public const string ImageViewerFlipHorizontal = "image_viewer.flip_horizontal";
    public const string ImageViewerFlipVertical = "image_viewer.flip_vertical";
    public const string ImageViewerShowInfo = "image_viewer.show_info";
    public const string ImageViewerUndo = "image_viewer.undo";
    public const string ImageViewerRedo = "image_viewer.redo";
    public const string ImageViewerZoomIn = "image_viewer.zoom.increase";
    public const string ImageViewerZoomOut = "image_viewer.zoom.decrease";
    public const string ImageViewerZoomReset = "image_viewer.zoom.reset";
    public const string ImageViewerFullscreenToggle = "image_viewer.fullscreen.toggle";
    public const string ImageViewerFullscreenExit = "image_viewer.fullscreen.exit";
    public const string ImageViewerVideoSeekToStart = "image_viewer.video.seek.start";
    public const string ImageViewerVideoSeekBackward = "image_viewer.video.seek.backward";
    public const string ImageViewerVideoSeekForward = "image_viewer.video.seek.forward";
    public const string ImageViewerVideoSeekToPosition = "image_viewer.video.seek.position";
    public const string ImageViewerVideoExternalPlay = "image_viewer.video.play.external";
    public const string ImageViewerVideoSaveFrame = "image_viewer.video.frame.save_png";
    public const string ImageViewerVideoSetPlaybackRate = "image_viewer.video.playback_rate.set";
    public const string ImageViewerResize = "image_viewer.resize";
    public const string ImageViewerMosaic = "image_viewer.mosaic";
    public const string ImageViewerBlur = "image_viewer.blur";
    public const string ImageViewerTone = "image_viewer.tone";
    public const string TextViewerReturnToBrowser = "text_viewer.return_to_browser";
    public const string TextViewerEncodingCycle = "text_viewer.encoding.cycle";
    public const string TextViewerWordWrapToggle = "text_viewer.word_wrap.toggle";
    public const string TextViewerSearch = "text_viewer.search";
    public const string TextViewerSearchNext = "text_viewer.search.next";
    public const string TextViewerSearchPrevious = "text_viewer.search.previous";
    public const string TextViewerCopy = "text_viewer.copy";
    public const string TextViewerOpenExternalEditor = "text_viewer.open_external_editor";
    public const string TextViewerNavigate = "text_viewer.navigate";
    public const string TextViewerMarkdownModeSet = "text_viewer.markdown_mode.set";
    public const string BrowserAudioCtrlEnter = "browser.audio.ctrl_enter";

    public static string GetBrowserDriveCommandId(int driveNumber)
    {
        if (driveNumber is < 1 or > 26)
        {
            throw new ArgumentOutOfRangeException(nameof(driveNumber), driveNumber, "Drive command must be between A and Z.");
        }

        return driveNumber switch
        {
            1 => BrowserDriveA,
            2 => BrowserDriveB,
            3 => BrowserDriveC,
            4 => BrowserDriveD,
            5 => BrowserDriveE,
            6 => BrowserDriveF,
            7 => BrowserDriveG,
            8 => BrowserDriveH,
            9 => BrowserDriveI,
            _ => $"browser.drive.{char.ToLowerInvariant((char)('A' + driveNumber - 1))}"
        };
    }

    public static bool TryGetBrowserDriveNumber(string? commandId, out int driveNumber)
    {
        for (int candidate = 1; candidate <= 26; candidate++)
        {
            if (string.Equals(commandId, GetBrowserDriveCommandId(candidate), StringComparison.OrdinalIgnoreCase))
            {
                driveNumber = candidate;
                return true;
            }
        }

        driveNumber = 0;
        return false;
    }

    public static string GetBrowserColumnCountCommandId(int columnCount)
    {
        return columnCount switch
        {
            1 => BrowserViewColumnCount1,
            2 => BrowserViewColumnCount2,
            3 => BrowserViewColumnCount3,
            4 => BrowserViewColumnCount4,
            5 => BrowserViewColumnCount5,
            6 => BrowserViewColumnCount6,
            7 => BrowserViewColumnCount7,
            8 => BrowserViewColumnCount8,
            9 => BrowserViewColumnCount9,
            _ => throw new ArgumentOutOfRangeException(nameof(columnCount), columnCount, "Column-count command must be between 1 and 9.")
        };
    }

    public static bool TryGetBrowserColumnCount(string? commandId, out int columnCount)
    {
        for (int candidate = 1; candidate <= 9; candidate++)
        {
            if (string.Equals(commandId, GetBrowserColumnCountCommandId(candidate), StringComparison.OrdinalIgnoreCase))
            {
                columnCount = candidate;
                return true;
            }
        }

        columnCount = 0;
        return false;
    }

}
