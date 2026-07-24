using System.Drawing;
using System.Windows.Forms;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    private TextBox? _browserPathEntryTextBox;
    private Button? _browserPathEntryGoButton;
    private DirectoryPathCompletionController? _browserPathEntryCompletionController;
    private bool _suppressBrowserPathEntryLostFocus;
    private bool _suppressBrowserPathEntryPanelClick;

    private bool IsBrowserPathEntryActive()
    {
        return _browserPathEntryTextBox != null &&
               !_browserPathEntryTextBox.IsDisposed &&
               _browserPathEntryTextBox.Visible;
    }

    private void OpenBrowserPathEntry()
    {
        if (_uiMode != UIMode.Browser || IsCurrentDirectoryBusy())
        {
            return;
        }

        EnsureBrowserPathEntryControl();
        if (_browserPathEntryTextBox == null)
        {
            return;
        }

        _browserPathEntryTextBox.Text = _navigationService.CurrentPath;
        ShowBrowserPathEntryEditor();
        _browserPathEntryCompletionController?.ShowHistoryCandidates();
    }

    private void EnsureBrowserPathEntryControl()
    {
        if (_browserPathEntryTextBox != null)
        {
            return;
        }

        _browserPathEntryTextBox = new TextBox
        {
            Name = "browserPathEntryTextBox",
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10F),
            Visible = false
        };
        _browserPathEntryGoButton = new Button
        {
            Name = "browserPathEntryGoButton",
            Text = "→",
            Font = new Font("Meiryo UI", 9F, FontStyle.Bold),
            Width = 46,
            Height = Math.Max(infoRow2Panel.ClientSize.Height - 2, 18),
            FlatStyle = FlatStyle.Flat,
            Visible = false,
            TabStop = false,
            Cursor = Cursors.Hand,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White
        };
        _browserPathEntryGoButton.FlatAppearance.BorderSize = 1;
        _browserPathEntryGoButton.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);

        _browserPathEntryCompletionController = DirectoryPathCompletionController.Attach(
            _browserPathEntryTextBox,
            new DirectoryPathCompletionOptions
            {
                ShowOnTextChanged = true,
                CustomCandidateProvider = (text, token) =>
                {
                    return Task.Run(() =>
                    {
                        var all = Services.BrowserPathEntryCandidateService.BuildCandidates(
                            _navigationService,
                            _quickAccessStore,
                            GetSharedDirectoryMoveHistory());
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            return all.ToList();
                        }
                        var filtered = all.Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (filtered.Count == 0 || (filtered.Count == 1 && string.Equals(filtered[0], text, StringComparison.OrdinalIgnoreCase)))
                        {
                            return all.ToList();
                        }
                        var remaining = all.Except(filtered, StringComparer.OrdinalIgnoreCase);
                        return filtered.Concat(remaining).ToList();
                    }, token);
                },
                IsInsideExternalControl = point =>
                    _browserPathEntryGoButton != null &&
                    !_browserPathEntryGoButton.IsDisposed &&
                    _browserPathEntryGoButton.RectangleToScreen(_browserPathEntryGoButton.ClientRectangle).Contains(point),
                OutsideClick = () =>
                {
                    if (_browserPathEntryTextBox == null || _browserPathEntryTextBox.IsDisposed)
                    {
                        return;
                    }
                    BeginInvoke(new Action(() =>
                    {
                        if (IsBrowserPathEntryActive())
                        {
                            CancelBrowserPathEntry();
                        }
                    }));
                }
            });
        _browserPathEntryTextBox.KeyDown += BrowserPathEntryTextBox_KeyDown;
        _browserPathEntryTextBox.LostFocus += BrowserPathEntryTextBox_LostFocus;
        _browserPathEntryTextBox.MouseDown += BrowserPathEntryChild_MouseDown;
        _browserPathEntryGoButton.Click += BrowserPathEntryGoButton_Click;
        _browserPathEntryGoButton.MouseDown += BrowserPathEntryChild_MouseDown;
        infoRow2Panel.Resize += BrowserPathEntryHostPanel_Resize;

        infoRow2Panel.Controls.Add(_browserPathEntryTextBox);
        infoRow2Panel.Controls.Add(_browserPathEntryGoButton);
        LayoutBrowserPathEntryControls();
        _browserPathEntryTextBox.BringToFront();
        _browserPathEntryGoButton.BringToFront();
    }

    private void ShowBrowserPathEntryEditor()
    {
        if (_browserPathEntryTextBox == null || _browserPathEntryGoButton == null)
        {
            return;
        }

        LayoutBrowserPathEntryControls();
        lblPath.Visible = false;
        if (_breadcrumbPathControl != null)
        {
            _breadcrumbPathControl.Visible = false;
        }
        _browserPathEntryTextBox.Visible = true;
        _browserPathEntryGoButton.Visible = true;
        _browserPathEntryTextBox.BringToFront();
        _browserPathEntryGoButton.BringToFront();
        FocusBrowserPathEntryEditor(selectAll: true);

        BeginInvoke(new Action(() =>
        {
            if (!IsBrowserPathEntryActive())
            {
                return;
            }

            FocusBrowserPathEntryEditor(selectAll: true);
        }));
    }

    private void FocusBrowserPathEntryEditor(bool selectAll)
    {
        if (_browserPathEntryTextBox == null)
        {
            return;
        }

        _browserPathEntryTextBox.Focus();
        if (selectAll)
        {
            _browserPathEntryTextBox.SelectAll();
        }
        else
        {
            _browserPathEntryTextBox.Select(_browserPathEntryTextBox.Text.Length, 0);
        }
    }

    private void LayoutBrowserPathEntryControls()
    {
        if (_browserPathEntryTextBox == null || _browserPathEntryGoButton == null)
        {
            return;
        }

        int rowHeight = Math.Max(infoRow2Panel.ClientSize.Height, 20);
        int buttonWidth = 46;
        int buttonHeight = Math.Max(rowHeight - 2, 18);
        int rightReserved = (lblSort.Visible ? lblSort.Width : 0) + 2;
        int buttonLeft = Math.Max(0, infoRow2Panel.ClientSize.Width - rightReserved - buttonWidth);
        int textBoxWidth = Math.Max(60, buttonLeft - 1);

        _browserPathEntryTextBox.Dock = DockStyle.None;
        _browserPathEntryTextBox.Left = 0;
        _browserPathEntryTextBox.Top = 0;
        _browserPathEntryTextBox.Width = textBoxWidth;
        _browserPathEntryTextBox.Height = rowHeight;
        _browserPathEntryTextBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _browserPathEntryGoButton.Left = buttonLeft;
        _browserPathEntryGoButton.Top = Math.Max(0, (rowHeight - buttonHeight) / 2);
        _browserPathEntryGoButton.Width = buttonWidth;
        _browserPathEntryGoButton.Height = buttonHeight;
        _browserPathEntryGoButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
    }

    private void BrowserPathEntryHostPanel_Resize(object? sender, EventArgs e)
    {
        LayoutBrowserPathEntryControls();
    }

    private void BrowserPathEntryChild_MouseDown(object? sender, MouseEventArgs e)
    {
        _suppressBrowserPathEntryPanelClick = true;
        BeginInvoke(new Action(() => _suppressBrowserPathEntryPanelClick = false));
    }

    private void BrowserPathEntryGoButton_Click(object? sender, EventArgs e)
    {
        CommitBrowserPathEntry();
    }

    private void BrowserPathEntryTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_browserPathEntryTextBox == null)
        {
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CancelBrowserPathEntry();
            return;
        }



        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        CommitBrowserPathEntry();
    }

    private void BrowserPathEntryTextBox_LostFocus(object? sender, EventArgs e)
    {
        if (_browserPathEntryTextBox == null || _suppressBrowserPathEntryLostFocus)
        {
            return;
        }

        // If the Go Button is clicked, it will take focus, but we don't want to cancel immediately.
        // We defer the focus check to see if the active control is the Go button.
        BeginInvoke(new Action(() =>
        {
            if (_browserPathEntryTextBox == null ||
                _browserPathEntryTextBox.IsDisposed ||
                !_browserPathEntryTextBox.Visible ||
                _browserPathEntryTextBox.ContainsFocus)
            {
                return;
            }

            if (_browserPathEntryGoButton != null && _browserPathEntryGoButton.Focused)
            {
                return;
            }

            // Check if the completion popup is visible
            if (_browserPathEntryCompletionController != null && _browserPathEntryCompletionController.IsCompletionPopupVisible)
            {
                return;
            }

            CancelBrowserPathEntry();
        }));
    }

    private void CommitBrowserPathEntry()
    {
        if (_browserPathEntryTextBox == null)
        {
            return;
        }

        BrowserPathEntryApplyResult result = BrowserPathEntryCoordinator.Apply(
            _browserPathEntryTextBox.Text,
            _navigationService,
            navigateDirectory: path =>
            {
                NavigateToLocationDirectory(path);
            },
            openFile: TryOpenBrowserPathEntryFile);

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatusMessage(result.StatusMessage, 2000);
        }

        if (result.ShouldCloseEditor)
        {
            HideBrowserPathEntryEditor();
            return;
        }

        FocusBrowserPathEntryEditor(selectAll: true);
    }

    private string? TryOpenBrowserPathEntryFile(string fullPath)
    {
        return ExternalToolService.OpenWithShellAssociation(fullPath);
    }

    private void CancelBrowserPathEntry()
    {
        HideBrowserPathEntryEditor();
    }

    private void HideBrowserPathEntryEditor()
    {
        if (_browserPathEntryTextBox == null || _browserPathEntryGoButton == null)
        {
            return;
        }

        _suppressBrowserPathEntryLostFocus = true;
        try
        {
            if (_browserPathEntryCompletionController != null)
            {
                // Accessing private close popup method is not exposed, but setting visible false
                // of the control will close the completion controller's popup internally via VisibleChanged hook.
            }
            _browserPathEntryTextBox.Visible = false;
            _browserPathEntryGoButton.Visible = false;
            ApplyPathDisplayMode();
        }
        finally
        {
            _suppressBrowserPathEntryLostFocus = false;
        }

        fileListView.Focus();
    }

    private bool NavigateToLocationDirectory(string resolvedPath)
    {
        return ExecuteDirectoryNavigationRequest(
            _browserNavigationCoordinator.CreateDirectoryNavigationRequest(resolvedPath),
            onNavigationSucceeded: () => AddDirectoryMoveHistory(resolvedPath),
            onDirectoryMissing: missingPath => ShowStatusMessage(BrowserPathEntryNavigationService.BuildMissingPathMessage(missingPath), 2000));
    }
}
