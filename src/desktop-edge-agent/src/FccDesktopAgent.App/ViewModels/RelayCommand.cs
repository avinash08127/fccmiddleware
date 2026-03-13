using System.Windows.Input;

namespace FccDesktopAgent.App.ViewModels;

/// <summary>
/// Shared ICommand implementation used by all ViewModels.
/// Consolidated from duplicated implementations (T-DSK-004).
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

#pragma warning disable CS0067 // Event is required by ICommand interface
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}

/// <summary>
/// Generic ICommand implementation that passes the CommandParameter as a typed argument.
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;

#pragma warning disable CS0067 // Event is required by ICommand interface
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public RelayCommand(Action<T?> execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter is T t ? t : default);
}
