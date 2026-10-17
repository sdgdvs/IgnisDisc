using System.Windows.Input;

namespace CdBurner.App.Mvvm;

/// <summary>
/// Synchronous implementation of <see cref="ICommand"/> delegating execution to an <see cref="Action"/>.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Initializes a new parameterless command.
    /// </summary>
    /// <param name="execute">The action to execute.</param>
    /// <param name="canExecute">Optional function determining if command can execute.</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    /// <summary>
    /// Initializes a new command accepting an object parameter.
    /// </summary>
    /// <param name="execute">The action to execute with parameter.</param>
    /// <param name="canExecute">Optional predicate determining if command can execute.</param>
    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc/>
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Raises the <see cref="CanExecuteChanged"/> event to inform subscribers to re-evaluate command status.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Alias for <see cref="RaiseCanExecuteChanged"/> to match toolkit conventions.
    /// </summary>
    public void NotifyCanExecuteChanged() => RaiseCanExecuteChanged();
}

/// <summary>
/// Generic synchronous implementation of <see cref="ICommand"/> with typed parameter.
/// </summary>
/// <typeparam name="T">Type of parameter passed to execution action and can-execute predicate.</typeparam>
public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Initializes a new generic command with typed parameter.
    /// </summary>
    /// <param name="execute">Action executing with typed parameter.</param>
    /// <param name="canExecute">Optional predicate determining if command can execute.</param>
    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter)
    {
        if (parameter is null && typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
        {
            return _canExecute?.Invoke(default) ?? true;
        }

        if (parameter is T typedParam)
        {
            return _canExecute?.Invoke(typedParam) ?? true;
        }

        if (parameter is null)
        {
            return _canExecute?.Invoke(default) ?? true;
        }

        return false;
    }

    /// <inheritdoc/>
    public void Execute(object? parameter)
    {
        if (parameter is T typedParam)
        {
            _execute(typedParam);
        }
        else if (parameter is null)
        {
            _execute(default);
        }
    }

    /// <summary>
    /// Raises the <see cref="CanExecuteChanged"/> event to inform subscribers to re-evaluate command status.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Alias for <see cref="RaiseCanExecuteChanged"/>.
    /// </summary>
    public void NotifyCanExecuteChanged() => RaiseCanExecuteChanged();
}
