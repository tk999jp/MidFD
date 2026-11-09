namespace MidFD;

internal enum ImageViewerCommandAction
{
    Open,
    TogglePresentationMode,
    SaveAs,
    Close,
    Copy,
    Paste,
    PasteSelectionStretch,
    CopySvg,
    Quantize,
    Reset,
    RotateRight,
    RotateLeft,
    FlipHorizontal,
    FlipVertical,
    ShowInfo,
    Undo,
    Redo,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    FullscreenToggle,
    FullscreenExit,
    VideoSeekToStart,
    VideoSeekBackward,
    VideoSeekForward,
    VideoSeekToPosition,
    VideoExternalPlay,
    VideoSaveFrame,
    VideoSetPlaybackRate,
    Resize,
    Mosaic,
    Blur,
    Tone,
    PasteSelectionOriginal
}

internal static class ImageViewerCommandIds
{
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
}
