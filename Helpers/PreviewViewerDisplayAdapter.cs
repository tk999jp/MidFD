namespace MidFD.Helpers;

/// <summary>
/// Preview popup と内蔵 viewer の表示適用だけを担当する薄い adapter。
/// MainForm は「いつ更新するか」を持ち、ここでは「どう見せるか」だけを扱う。
/// </summary>
public sealed class PreviewViewerDisplayAdapter
{
    private readonly PreviewPopupForm _previewPopup;
    private readonly Panel _viewerPanel;
    private readonly RichTextBox _viewerTextBox;
    private readonly PictureBox _viewerPictureBox;
    private readonly Label _viewerMessageLabel;

    public PreviewViewerDisplayAdapter(
        PreviewPopupForm previewPopup,
        Panel viewerPanel,
        RichTextBox viewerTextBox,
        PictureBox viewerPictureBox,
        Label viewerMessageLabel)
    {
        _previewPopup = previewPopup;
        _viewerPanel = viewerPanel;
        _viewerTextBox = viewerTextBox;
        _viewerPictureBox = viewerPictureBox;
        _viewerMessageLabel = viewerMessageLabel;
    }

    public void HideViewerPanel()
    {
        _viewerPanel.Visible = false;
    }

    public void ShowViewerPanel()
    {
        _viewerPanel.Visible = true;
        _viewerPanel.BringToFront();
        _viewerPanel.Focus();
    }

    public void ShowMessage(string message)
    {
        if (_previewPopup.Visible)
        {
            _previewPopup.ShowMessage(message);
        }

        ClearViewerImage();
        _viewerTextBox.Clear();
        _viewerTextBox.Visible = false;
        _viewerMessageLabel.Text = message;
        _viewerMessageLabel.Visible = true;
    }

    public void ShowExternalImageViewerMessage()
    {
        if (_previewPopup.Visible)
        {
            _previewPopup.Hide();
        }

        ClearViewerImage();
        _viewerTextBox.Visible = false;
        _viewerMessageLabel.Text = "画像は専用画像ビューアで表示します。\nV / Enter で開きます。";
        _viewerMessageLabel.Visible = true;
    }

    public void ShowTextContent(string text)
    {
        if (_previewPopup.Visible)
        {
            _previewPopup.Clear();
        }

        _viewerMessageLabel.Visible = false;
        ClearViewerImage();
        _viewerTextBox.Text = text;
        _viewerTextBox.Visible = true;
        _viewerTextBox.Focus();
    }

    private void ClearViewerImage()
    {
        _viewerPictureBox.Image?.Dispose();
        _viewerPictureBox.Image = null;
        _viewerPictureBox.Visible = false;
    }
}
