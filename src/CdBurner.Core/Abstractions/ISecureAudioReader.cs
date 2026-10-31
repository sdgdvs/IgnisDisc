namespace CdBurner.Core.Abstractions;

/// <summary>
/// Optional burn-engine capability used for secure audio ripping: audio reads that report the drive's C2 error
/// pointers, and drive read-cache defeat so a re-read comes from the disc rather than the cache.
/// <para>
/// When a CD drive cannot correct an audio frame it interpolates the samples instead of failing, so a plain read
/// "succeeds" with wrong data. C2 error pointers flag exactly which bytes were affected.
/// </para>
/// </summary>
public interface ISecureAudioReader
{
    /// <summary>True if the drive returns C2 error pointers with audio reads.</summary>
    bool SupportsC2ErrorPointers(IDiscRecorderBackend recorder, int probeLba);

    /// <summary>
    /// Reads raw 2352-byte audio sectors. For each sector the consumer also receives the number of bytes the drive
    /// flagged with C2 errors (0 = clean). Same contract as <see cref="IBurnEngineBackend.ReadSectorsAsync"/>:
    /// sectors are delivered in order and the read stops at the first unreadable sector.
    /// </summary>
    Task<RipOperationResult> ReadAudioSectorsWithC2Async(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, int, ValueTask> sectorConsumer,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads enough audio far away from <paramref name="nearLba"/> to evict it from the drive's read cache.
    /// <paramref name="audioEndLba"/> bounds the readable area.
    /// </summary>
    Task FlushReadCacheAsync(IDiscRecorderBackend recorder, int nearLba, int audioEndLba, CancellationToken cancellationToken);
}
