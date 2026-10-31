using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>Outcome of a <see cref="SecureAudioReader.ReadRangeAsync"/> pass.</summary>
/// <param name="Completed">True if every sector was delivered to one of the sinks.</param>
/// <param name="StoppedAtLba">When not completed, the unreadable LBA at which the caller asked to stop.</param>
/// <param name="RecoveredSectors">Sectors that had C2 errors (or failed) at first but read clean on a re-read.</param>
/// <param name="SuspectSectors">Sectors still C2-flagged after every re-read; their last reading was delivered.</param>
/// <param name="Rereads">Single-sector re-reads performed.</param>
/// <param name="CacheFlushes">Drive cache flushes performed (one per re-read round).</param>
public readonly record struct SecureReadOutcome(
    bool Completed, int? StoppedAtLba, int RecoveredSectors, int SuspectSectors, int Rereads, int CacheFlushes);

/// <summary>
/// Secure audio extraction: reads with C2 error pointers and re-reads only the flagged sectors, flushing the drive's
/// read cache before each round so re-reads come from the disc.
/// <para>
/// Each batch is read once. Sectors the drive flagged (or could not return) become suspects. In each of up to
/// <c>maxRereadRounds</c> rounds the cache is flushed once and every remaining suspect is re-read on its own; a suspect
/// is accepted as soon as a reading comes back with no C2 errors. A sector still flagged after the last round is
/// delivered with its latest reading and counted in <see cref="SecureReadOutcome.SuspectSectors"/>; a sector that never
/// returned any data goes to the unreadable sink.
/// </para>
/// </summary>
public static class SecureAudioReader
{
    public const int DefaultBatchSectors = 256;

    public static async Task<SecureReadOutcome> ReadRangeAsync(
        ISecureAudioReader reader,
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        int audioEndLba,
        int maxRereadRounds,
        Func<int, ReadOnlyMemory<byte>, ValueTask> onSector,
        Func<int, ValueTask<bool>> onUnreadable,
        Action<int>? onProgress = null,
        int batchSectors = DefaultBatchSectors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(onSector);
        ArgumentNullException.ThrowIfNull(onUnreadable);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSectors, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRereadRounds);

        const int size = OpticalConstants.RawSectorSize;
        int recovered = 0, suspect = 0, rereads = 0, flushes = 0;
        int endLba = startLba + sectorCount;

        var data = new byte[batchSectors * size];
        var c2 = new int[batchSectors];      // -1 = no data yet
        var everFlagged = new bool[batchSectors];

        for (int batchStart = startLba; batchStart < endLba; batchStart += batchSectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(batchSectors, endLba - batchStart);
            Array.Fill(c2, -1, 0, n);
            Array.Clear(everFlagged, 0, n);

            // 1. Read the batch, stepping over sectors the drive cannot return at all.
            int pos = batchStart;
            while (pos < batchStart + n)
            {
                int delivered = 0;
                int from = pos;
                RipOperationResult result;
                try
                {
                    result = await reader.ReadAudioSectorsWithC2Async(recorder, from, batchStart + n - from, (lba, sector, c2Bytes) =>
                    {
                        if (lba == from + delivered)
                        {
                            Store(lba - batchStart, sector, c2Bytes);
                            delivered++;
                        }
                        return ValueTask.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = RipOperationResult.Failed(ex.Message);
                }

                pos = from + delivered;
                if (!result.Success || delivered == 0)
                {
                    pos++; // leave this sector without data; it becomes a suspect
                }
            }

            // 2. Re-read suspects, one cache flush per round.
            var suspects = Enumerable.Range(0, n).Where(i => c2[i] != 0).ToList();
            foreach (int i in suspects)
            {
                everFlagged[i] = true;
            }

            for (int round = 0; round < maxRereadRounds && suspects.Count > 0; round++)
            {
                await reader.FlushReadCacheAsync(recorder, batchStart + suspects[0], audioEndLba, cancellationToken).ConfigureAwait(false);
                flushes++;

                foreach (int i in suspects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rereads++;
                    try
                    {
                        await reader.ReadAudioSectorsWithC2Async(recorder, batchStart + i, 1, (lba, sector, c2Bytes) =>
                        {
                            // Keep the newest reading unless it is worse than a clean one we already have.
                            if (lba == batchStart + i && (c2[i] != 0 || c2Bytes == 0))
                            {
                                Store(i, sector, c2Bytes);
                            }
                            return ValueTask.CompletedTask;
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Still a suspect; the next round tries again.
                    }
                }

                suspects = suspects.Where(i => c2[i] != 0).ToList();
            }

            // 3. Deliver the batch in order.
            for (int i = 0; i < n; i++)
            {
                int lba = batchStart + i;
                if (c2[i] < 0)
                {
                    if (!await onUnreadable(lba).ConfigureAwait(false))
                    {
                        onProgress?.Invoke(lba - startLba);
                        return new SecureReadOutcome(false, lba, recovered, suspect, rereads, flushes);
                    }
                    continue;
                }

                if (c2[i] > 0) suspect++;
                else if (everFlagged[i]) recovered++;

                await onSector(lba, data.AsMemory(i * size, size)).ConfigureAwait(false);
            }

            onProgress?.Invoke(batchStart + n - startLba);
        }

        return new SecureReadOutcome(true, null, recovered, suspect, rereads, flushes);

        void Store(int index, ReadOnlyMemory<byte> sector, int c2Bytes)
        {
            sector.Span.CopyTo(data.AsSpan(index * size, size));
            c2[index] = c2Bytes;
        }
    }
}
