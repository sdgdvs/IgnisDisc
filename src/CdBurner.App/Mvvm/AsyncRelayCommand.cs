using System.Windows.Input;

namespace CdBurner.App.Mvvm;

/// <summary>
/// Asynchronous implementation of <see cref="ICommand"/> with running state tracking and can-execute re-evaluation.
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _isRunning;
    private Task? _executionTask;

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Gets whether an asynchronous execution is currently in progress.
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning != value)
            {
                _isRunning = value;
                RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Gets the task currently executing, or null if idle.
    /// </summary>
    public Task? ExecutionTask
    {
        get => _executionTask;
        private set => _executionTask = value;
    }

    /// <summary>
    /// Initializes a parameterless asynchronous command.
    /// </summary>
    /// <param name="execute">Asynchronous task delegate to execute.</param>
    /// <param name="canExecute">Optional synchronous predicate determining if command can execute.</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    /// <summary>
    /// Initializes an asynchronous command accepting an object parameter.
    /// </summary>
    /// <param name="execute">Asynchronous task delegate accepting a parameter.</param>
    /// <param name="canExecute">Optional predicate determining if command can execute.</param>
    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter)
    {
        if (IsRunning)
        {
            return false;
        }

        return _canExecute?.Invoke(parameter) ?? true;
    }

    /// <inheritdoc/>
    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled AsyncRelayCommand exception: {ex}");
        }
    }

    /// <summary>
    /// Executes the asynchronous command and awaits its completion.
    /// </summary>
    /// <param name="parameter">Command parameter.</param>
    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            IsRunning = true;
            var task = _execute(parameter);
            ExecutionTask = task;
            await task;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Raises the <see cref="CanExecuteChanged"/> event to notify UI subscribers.
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

/// <summary>
/// Generic asynchronous implementation of <see cref="ICommand"/> with typed parameter.
/// </summary>
/// <typeparam name="T">Type of parameter passed to execution task and can-execute predicate.</typeparam>
public class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Predicate<T?>? _canExecute;
    private bool _isRunning;
    private Task? _executionTask;

    /// <inheritdoc/>
    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Gets whether an asynchronous execution is currently in progress.
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning != value)
            {
                _isRunning = value;
                RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Gets the task currently executing, or null if idle.
    /// </summary>
    public Task? ExecutionTask
    {
        get => _executionTask;
        private set => _executionTask = value;
    }

    /// <summary>
    /// Initializes a generic asynchronous command with typed parameter.
    /// </summary>
    /// <param name="execute">Asynchronous task delegate accepting typed parameter.</param>
    /// <param name="canExecute">Optional predicate determining if command can execute.</param>
    public AsyncRelayCommand(Func<T?, Task> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc/>
    public bool CanExecute(object? parameter)
    {
        if (IsRunning)
        {
            return false;
        }

        if (parameter is null && typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) is null)
        {
            return _canExecute?.Invoke(default) ?? true;
        }

        if (parameter is T typed)
        {
            return _canExecute?.Invoke(typed) ?? true;
        }

        if (parameter is null)
        {
            return _canExecute?.Invoke(default) ?? true;
        }

        return false;
    }

    /// <inheritdoc/>
    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled AsyncRelayCommand exception: {ex}");
        }
    }

    /// <summary>
    /// Executes the asynchronous command and awaits its completion.
    /// </summary>
    /// <param name="parameter">Command parameter.</param>
    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            IsRunning = true;
            Task task = parameter is T typed
                ? _execute(typed)
                : _execute(default);

            ExecutionTask = task;
            await task;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Raises the <see cref="CanExecuteChanged"/> event to notify UI subscribers.
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
