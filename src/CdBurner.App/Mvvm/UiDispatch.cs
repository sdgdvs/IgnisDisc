using System.Windows.Threading;

namespace CdBurner.App.Mvvm;

/// <summary>
/// Decides whether work raised on a background thread should be marshalled to a WPF dispatcher.
/// </summary>
public static class UiDispatch
{
    /// <summary>
    /// True only when <paramref name="dispatcher"/> belongs to another thread that is still alive and not shutting down.
    /// A dispatcher whose thread has exited never processes its queue again, so marshalling to it would drop the work;
    /// in that case (or with no dispatcher, or when already on its thread) the caller should run the work inline.
    /// </summary>
    public static bool ShouldMarshalTo(Dispatcher? dispatcher) =>
        dispatcher is { HasShutdownStarted: false } &&
        dispatcher.Thread.IsAlive &&
        !dispatcher.CheckAccess();
}
