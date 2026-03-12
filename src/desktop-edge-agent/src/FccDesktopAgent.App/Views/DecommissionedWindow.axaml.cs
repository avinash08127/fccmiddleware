using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace FccDesktopAgent.App.Views;

public sealed partial class DecommissionedWindow : Window
{
    public DecommissionedWindow()
    {
        InitializeComponent();
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }
}
