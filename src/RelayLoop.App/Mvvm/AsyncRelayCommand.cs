using System.Windows.Input;

namespace RelayLoop.App.Mvvm;

public sealed class CommandExecutionFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private int _isRunning;

    public event EventHandler? CanExecuteChanged;

    public event EventHandler<CommandExecutionFailedEventArgs>? ExecutionFailed;

    public Exception? LastException { get; private set; }

    public bool CanExecute(object? parameter) => Volatile.Read(ref _isRunning) == 0 && (canExecute?.Invoke() ?? true);

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!(canExecute?.Invoke() ?? true) || Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        LastException = null;
        RaiseCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LastException = exception;
            try
            {
                ExecutionFailed?.Invoke(this, new CommandExecutionFailedEventArgs(exception));
            }
            catch
            {
                // Error observers must not turn a handled command failure into a dispatcher crash.
            }
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
