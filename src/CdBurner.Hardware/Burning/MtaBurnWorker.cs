using System.Threading.Channels;
using CdBurner.Core.Abstractions;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Dedicated MTA (Multi-Threaded Apartment) worker pipeline for executing blocking COM or virtual
/// optical burn operations off the caller or WPF STA dispatcher thread, streaming progress snapshots
/// asynchronously through a Channel to an <see cref="IProgress{BurnProgressSnapshot}"/>.
/// </summary>
public static class MtaBurnWorker
{
    /// <summary>
    /// Executes synchronous work on a dedicated MTA thread, streaming progress snapshots through a Channel.
    /// </summary>
    public static async Task<TResult> RunAsync<TResult>(
        Func<IProgress<BurnProgressSnapshot>, CancellationToken, TResult> work,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default,
        string threadName = "MtaBurnWorker",
        ThreadPriority priority = ThreadPriority.AboveNormal)
    {
        ArgumentNullException.ThrowIfNull(work);

        var channel = Channel.CreateUnbounded<BurnProgressSnapshot>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        var relay = new ChannelProgressRelay(channel.Writer);
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                var result = work(relay, cancellationToken);
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Name = threadName;
        thread.Priority = priority;
        thread.Start();

        // Drain channel items in FIFO order and report to progress
        while (await channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var snapshot))
            {
                progress?.Report(snapshot);
            }
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Executes asynchronous work on a dedicated MTA thread, streaming progress snapshots through a Channel.
    /// </summary>
    public static async Task<TResult> RunTaskAsync<TResult>(
        Func<IProgress<BurnProgressSnapshot>, CancellationToken, Task<TResult>> work,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default,
        string threadName = "MtaBurnWorker",
        ThreadPriority priority = ThreadPriority.AboveNormal)
    {
        ArgumentNullException.ThrowIfNull(work);

        var channel = Channel.CreateUnbounded<BurnProgressSnapshot>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        var relay = new ChannelProgressRelay(channel.Writer);
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                var task = work(relay, cancellationToken);
                var result = task.GetAwaiter().GetResult();
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Name = threadName;
        thread.Priority = priority;
        thread.Start();

        // Drain channel items in FIFO order and report to progress
        while (await channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var snapshot))
            {
                progress?.Report(snapshot);
            }
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a burn operation returning <see cref="BurnOperationResult"/> on a dedicated MTA thread.
    /// </summary>
    public static Task<BurnOperationResult> RunBurnAsync(
        Func<IProgress<BurnProgressSnapshot>, CancellationToken, BurnOperationResult> work,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
        => RunAsync(work, progress, cancellationToken);

    /// <summary>
    /// Executes an async burn operation returning <see cref="BurnOperationResult"/> on a dedicated MTA thread.
    /// </summary>
    public static Task<BurnOperationResult> RunBurnTaskAsync(
        Func<IProgress<BurnProgressSnapshot>, CancellationToken, Task<BurnOperationResult>> work,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
        => RunTaskAsync(work, progress, cancellationToken);

    private sealed class ChannelProgressRelay : IProgress<BurnProgressSnapshot>
    {
        private readonly ChannelWriter<BurnProgressSnapshot> _writer;

        public ChannelProgressRelay(ChannelWriter<BurnProgressSnapshot> writer)
        {
            _writer = writer;
        }

        public void Report(BurnProgressSnapshot value)
        {
            _writer.TryWrite(value);
        }
    }
}
