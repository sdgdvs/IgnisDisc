using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Images;

/// <summary>
/// Outcome of a <see cref="BatchedSectorReader.ReadRangeAsync"/> pass.
/// </summary>
/// <param name="Completed">True if every sector in the range was delivered to one of the sinks.</param>
/// <param name="StoppedAtLba">When <see cref="Completed"/> is false, the unreadable LBA at which the caller asked to stop.</param>
/// <param name="TotalRetries">Number of single-sector re-reads performed on suspect sectors.</param>
public readonly record struct BatchedReadOutcome(bool Completed, int? StoppedAtLba, int TotalRetries);

/// <summary>
/// Reads a sector range through an <see cref="IBurnEngineBackend"/> in large batches, isolating and retrying
/// only the sectors that actually fail.
/// <para>
/// Every <see cref="IBurnEngineBackend.ReadSectorsAsync"/> call on a physical drive opens the device and probes it,
/// so issuing one call per sector caps throughput at roughly a hundred sectors per second. Batching amortises that
/// cost; the engine contract (sectors delivered in order, stopping at the first unrecovered error) tells us exactly
/// which sector to retry when a batch comes back short.
/// </para>
/// </summary>
public static class BatchedSectorReader
{
    /// <summary>Default sectors per engine call: 1 MiB of 2048-byte data (about 1.2 MB of raw 2352-byte sectors).</summary>
    public const int DefaultBatchSectors = 512;

    /// <param name="onSector">Receives each readable sector in LBA order. The memory is only valid during the call.</param>
    /// <param name="onUnreadable">Called for a sector that failed every retry; return true to continue past it, false to stop.</param>
    /// <param name="onProgress">Optional; receives the number of sectors handled so far after each batch.</param>
    /// <param name="readFromDisc">
    /// Read 2,048-byte data sectors the way a check after a burn does
    /// (<see cref="IBurnEngineBackend.ReadSectorsForVerifyAsync"/>), so the drive cannot answer from what a burn left
    /// in its memory. Every batch is judged against the whole range, not on its own.
    /// </param>
    /// <param name="strict">With <paramref name="readFromDisc"/>: force every read from the disc.</param>
    public static async Task<BatchedReadOutcome> ReadRangeAsync(
        IBurnEngineBackend engine,
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        int maxRetriesPerSector,
        Func<int, ReadOnlyMemory<byte>, ValueTask> onSector,
        Func<int, ValueTask<bool>> onUnreadable,
        Action<int>? onProgress = null,
        int batchSectors = DefaultBatchSectors,
        CancellationToken cancellationToken = default,
        bool readFromDisc = false,
        bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(onSector);
        ArgumentNullException.ThrowIfNull(onUnreadable);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSectors, 1);

        int endLba = startLba + sectorCount;
        int nextLba = startLba;
        int totalRetries = 0;
        bool verifyReads = readFromDisc && !raw2352Mode;
        var wholeRange = new SectorRange(startLba, sectorCount);

        Task<RipOperationResult> Read(int lba, int count, Func<int, ReadOnlyMemory<byte>, ValueTask> consumer) => verifyReads
            ? engine.ReadSectorsForVerifyAsync(recorder, lba, count, consumer, progress: null, cancellationToken, strict, wholeRange)
            : engine.ReadSectorsAsync(recorder, lba, count, raw2352Mode, consumer, progress: null, cancellationToken);

        while (nextLba < endLba)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batch = Math.Min(batchSectors, endLba - nextLba);
            int batchStart = nextLba;
            int delivered = 0;
            bool batchOk;

            try
            {
                var result = await Read(
                    batchStart,
                    batch,
                    async (lba, data) =>
                    {
                        // Engines deliver in order; ignore anything outside the expected position defensively.
                        if (lba != batchStart + delivered) return;
                        await onSector(lba, data).ConfigureAwait(false);
                        delivered++;
                    }).ConfigureAwait(false);

                batchOk = result.Success && delivered == batch;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                batchOk = false;
            }

            nextLba = batchStart + delivered;

            if (!batchOk && nextLba < endLba)
            {
                // The first undelivered sector is the one the drive could not read. Retry it on its own.
                int suspectLba = nextLba;
                bool recovered = false;

                for (int retry = 0; retry < maxRetriesPerSector && !recovered; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalRetries++;
                    try
                    {
                        bool got = false;
                        var single = await Read(
                            suspectLba,
                            1,
                            async (lba, data) =>
                            {
                                if (lba != suspectLba || got) return;
                                await onSector(lba, data).ConfigureAwait(false);
                                got = true;
                            }).ConfigureAwait(false);

                        recovered = single.Success && got;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Try again until the retry budget is spent.
                    }
                }

                if (!recovered && !await onUnreadable(suspectLba).ConfigureAwait(false))
                {
                    onProgress?.Invoke(suspectLba - startLba);
                    return new BatchedReadOutcome(false, suspectLba, totalRetries);
                }

                nextLba = suspectLba + 1;
            }

            onProgress?.Invoke(nextLba - startLba);
        }

        return new BatchedReadOutcome(true, null, totalRetries);
    }
}
