using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CdBurner.App.Mvvm;

/// <summary>
/// Base class for observable objects implementing <see cref="INotifyPropertyChanged"/>.
/// Provides clean, self-contained property change notification methods.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the <see cref="PropertyChanged"/> event for the calling property.
    /// </summary>
    /// <param name="propertyName">Name of the property that changed.</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Raises the <see cref="PropertyChanged"/> event with explicit event arguments.
    /// </summary>
    /// <param name="e">Property changed event arguments.</param>
    protected virtual void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
    }

    /// <summary>
    /// Updates storage field and raises <see cref="PropertyChanged"/> if the value changed.
    /// </summary>
    /// <typeparam name="T">Property type.</typeparam>
    /// <param name="storage">Reference to backing field.</param>
    /// <param name="value">New value.</param>
    /// <param name="propertyName">Name of the property.</param>
    /// <returns>True if the value changed; false otherwise.</returns>
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Updates storage field, executes an action callback, and raises <see cref="PropertyChanged"/> if the value changed.
    /// </summary>
    /// <typeparam name="T">Property type.</typeparam>
    /// <param name="storage">Reference to backing field.</param>
    /// <param name="value">New value.</param>
    /// <param name="onChanged">Action callback executed upon value change.</param>
    /// <param name="propertyName">Name of the property.</param>
    /// <returns>True if the value changed; false otherwise.</returns>
    protected bool SetProperty<T>(ref T storage, T value, Action onChanged, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        onChanged?.Invoke();
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Compares old and new value, invokes callback on change, and raises <see cref="PropertyChanged"/>.
    /// </summary>
    /// <typeparam name="T">Property type.</typeparam>
    /// <param name="oldValue">Previous value.</param>
    /// <param name="newValue">New value.</param>
    /// <param name="callback">Callback receiving new value.</param>
    /// <param name="propertyName">Name of the property.</param>
    /// <returns>True if the value changed; false otherwise.</returns>
    protected bool SetProperty<T>(T oldValue, T newValue, Action<T> callback, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
        {
            return false;
        }

        callback(newValue);
        OnPropertyChanged(propertyName);
        return true;
    }
}
