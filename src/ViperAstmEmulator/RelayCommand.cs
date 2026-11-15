using System.Windows;
using System.Windows.Input;

namespace ViperAstmEmulator;

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _busy;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        : this(p => { execute(p); return Task.CompletedTask; }, canExecute) { }

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_busy && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Lab Interface Emulator", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
