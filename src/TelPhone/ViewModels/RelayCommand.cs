using System.Windows.Input;

namespace TelPhone.ViewModels;

internal sealed class RelayCommand(Action<object?> execute, Predicate<object?>? enabled = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => enabled?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}
