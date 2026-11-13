namespace CdBurner.App.Mvvm;

/// <summary>
/// A <see cref="Progress{T}"/> that can be closed. <see cref="Progress{T}"/> delivers each report later (posted to the UI
/// thread, or to the thread pool where there is none), so a report made near the end of an operation can arrive after the
/// caller has set its final status line and overwrite it: an archive burn that had finished showed "ReadingSectors (92%)".
/// Call <see cref="Close"/> as soon as the operation returns; reports delivered after that are dropped. A report already
/// being handled finishes first, because both run under the same lock (the pattern Image Studio uses for its log).
/// Create it on the UI thread, as <see cref="Progress{T}"/> takes the thread's synchronization context when created.
/// </summary>
public sealed class ClosableProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _inner;
    private readonly object _gate = new();
    private bool _closed;

    public ClosableProgress(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _inner = new Progress<T>(value =>
        {
            lock (_gate)
            {
                if (!_closed)
                {
                    handler(value);
                }
            }
        });
    }

    public void Report(T value) => _inner.Report(value);

    /// <summary>Drops every report delivered from now on. Safe to call more than once.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }
}
