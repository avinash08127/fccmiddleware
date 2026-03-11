using Avalonia.Controls;

namespace FccDesktopAgent.App.Views;

public sealed partial class MainWindow : Window
{
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Called by the tray "Exit" handler to bypass the minimize-to-tray behaviour
    /// and allow the window (and application) to close normally.
    /// </summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose)
        {
            // Minimize to tray: cancel the close and hide the window instead.
            // The tray icon and background services keep running.
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
