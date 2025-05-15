// Plik: ViewModels/Base/AsyncRelayCommand.cs
using CosplayManager.Services; // Potrzebne dla SimpleFileLogger
using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CosplayManager.ViewModels.Base
{
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            // Dodajemy logowanie TUTAJ
            string commandName = _execute.Method.Name; // Próba uzyskania nazwy metody Execute dla identyfikacji komendy
            SimpleFileLogger.Log($"AsyncRelayCommand.CanExecute START for '{commandName}'. IsExecuting: {_isExecuting}. Parameter type: {parameter?.GetType().Name ?? "null"}. Has _canExecute: {_canExecute != null}");

            bool result = false;
            if (_isExecuting)
            {
                SimpleFileLogger.Log($"AsyncRelayCommand.CanExecute for '{commandName}': Currently executing. Result: false");
                return false;
            }

            if (_canExecute == null)
            {
                SimpleFileLogger.Log($"AsyncRelayCommand.CanExecute for '{commandName}': No _canExecute predicate. Result: true");
                result = true;
            }
            else
            {
                try
                {
                    result = _canExecute(parameter);
                    SimpleFileLogger.Log($"AsyncRelayCommand.CanExecute for '{commandName}': _canExecute predicate returned: {result}");
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"AsyncRelayCommand.CanExecute for '{commandName}': Exception in _canExecute predicate.", ex);
                    result = false; // Na wypadek błędu w CanExecute, lepiej zablokować
                }
            }
            SimpleFileLogger.Log($"AsyncRelayCommand.CanExecute FINAL for '{commandName}'. Result: {result}");
            return result;
        }

        public async void Execute(object? parameter)
        {
            string commandName = _execute.Method.Name;
            SimpleFileLogger.Log($"AsyncRelayCommand.Execute START for '{commandName}'. Parameter type: {parameter?.GetType().Name ?? "null"}");
            if (CanExecute(parameter)) // CanExecute wewnętrznie zaloguje swój wynik
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                SimpleFileLogger.Log($"AsyncRelayCommand.Execute for '{commandName}': IsExecuting set to true. Calling _execute.");
                try
                {
                    await _execute(parameter);
                    SimpleFileLogger.Log($"AsyncRelayCommand.Execute for '{commandName}': _execute finished.");
                }
                catch (Exception ex)
                {
                    SimpleFileLogger.LogError($"AsyncRelayCommand.Execute for '{commandName}': Exception during _execute.", ex);
                    // Można rozważyć ponowne rzucenie wyjątku lub obsłużenie go inaczej
                }
                finally
                {
                    _isExecuting = false;
                    RaiseCanExecuteChanged();
                    SimpleFileLogger.Log($"AsyncRelayCommand.Execute for '{commandName}': IsExecuting set to false. Raised CanExecuteChanged.");
                }
            }
            else
            {
                SimpleFileLogger.Log($"AsyncRelayCommand.Execute for '{commandName}': CanExecute returned false. Execution skipped.");
            }
        }

        public event EventHandler? CanExecuteChanged
        {
            add
            {
                CommandManager.RequerySuggested += value;
                SimpleFileLogger.Log($"AsyncRelayCommand: CanExecuteChanged subscriber added for {_execute.Method.Name}.");
            }
            remove
            {
                CommandManager.RequerySuggested -= value;
                SimpleFileLogger.Log($"AsyncRelayCommand: CanExecuteChanged subscriber removed for {_execute.Method.Name}.");
            }
        }
        public void RaiseCanExecuteChanged()
        {
            SimpleFileLogger.Log($"AsyncRelayCommand.RaiseCanExecuteChanged called for {_execute.Method.Name}. Invalidating RequerySuggested.");
            CommandManager.InvalidateRequerySuggested();
        }
    }
}