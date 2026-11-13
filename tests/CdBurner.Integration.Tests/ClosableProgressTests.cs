using CdBurner.App.Mvvm;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Progress reports are delivered later than they are made. A report made near the end of a burn used to arrive after
/// the screen had set its final status line and overwrite it (an archive burn that had finished showed
/// "ReadingSectors (92%)"; seen in the gate of five unrelated changes). <see cref="ClosableProgress{T}"/> drops reports
/// delivered after <see cref="ClosableProgress{T}.Close"/>.
/// </summary>
public class ClosableProgressTests
{
    /// <summary>Holds posted callbacks until the test runs them, so the order is under the test's control.</summary>
    private sealed class ManualContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
        public void RunAll()
        {
            while (_queue.Count > 0)
            {
                var (callback, state) = _queue.Dequeue();
                callback(state);
            }
        }
    }

    private static ClosableProgress<string> Create(ManualContext context, List<string> seen)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context); // Progress<T> takes the context it is created on
        try
        {
            return new ClosableProgress<string>(seen.Add);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void ReportDeliveredAfterClose_IsDropped()
    {
        var context = new ManualContext();
        var seen = new List<string>();
        var progress = Create(context, seen);

        ((IProgress<string>)progress).Report("ReadingSectors (92%)"); // made before the burn returned...
        progress.Close();                                              // ...the screen sets its final line here...
        context.RunAll();                                              // ...and only then is the report delivered

        Assert.Empty(seen);
    }

    [Fact]
    public void ReportsDeliveredBeforeClose_AreHandled_AndCloseCanBeRepeated()
    {
        var context = new ManualContext();
        var seen = new List<string>();
        var progress = Create(context, seen);

        ((IProgress<string>)progress).Report("Writing (10%)");
        ((IProgress<string>)progress).Report("Writing (50%)");
        context.RunAll();
        progress.Close();
        progress.Close();
        ((IProgress<string>)progress).Report("late");
        context.RunAll();

        Assert.Equal(new[] { "Writing (10%)", "Writing (50%)" }, seen);
    }
}
