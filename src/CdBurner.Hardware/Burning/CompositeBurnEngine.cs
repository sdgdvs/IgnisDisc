using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Composite optical burning engine multiplexer that implements <see cref="IBurnEngineBackend"/>,
/// automatically routing operations to <see cref="VirtualBurnEngine"/> when targeting virtual recorders
/// (<see cref="IDiscRecorderBackend.IsVirtual"/> == true) or to <see cref="Imapi2BurnEngine"/> when targeting
/// physical hardware drives.
/// </summary>
public class CompositeBurnEngine : IBurnEngineBackend, ISecureAudioReader
{
    private readonly IBurnEngineBackend _virtualEngine;
    private readonly IBurnEngineBackend _physicalEngine;

    /// <summary>
    /// Gets the underlying virtual burn engine instance.
    /// </summary>
    public IBurnEngineBackend VirtualEngine => _virtualEngine;

    /// <summary>
    /// Gets the underlying physical IMAPIv2 burn engine instance.
    /// </summary>
    public IBurnEngineBackend PhysicalEngine => _physicalEngine;

    /// <summary>
    /// Initializes a new instance of <see cref="CompositeBurnEngine"/>.
    /// </summary>
    /// <param name="virtualEngine">Optional virtual burn engine backend; defaults to <see cref="VirtualBurnEngine"/>.</param>
    /// <param name="physicalEngine">Optional physical burn engine backend; defaults to <see cref="Imapi2BurnEngine"/>.</param>
    public CompositeBurnEngine(
        IBurnEngineBackend? virtualEngine = null,
        IBurnEngineBackend? physicalEngine = null)
    {
        _virtualEngine = virtualEngine ?? new VirtualBurnEngine();
        _physicalEngine = physicalEngine ?? new Imapi2BurnEngine();
    }

    /// <inheritdoc/>
    public Task<BurnOperationResult> BurnDataStreamAsync(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var backend = SelectBackend(recorder);
        return backend.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<BurnOperationResult> BurnAudioTracksAsync(
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var backend = SelectBackend(recorder);
        return backend.BurnAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<BurnOperationResult> EraseMediaAsync(
        IDiscRecorderBackend recorder,
        bool fullErase,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var backend = SelectBackend(recorder);
        return backend.EraseMediaAsync(recorder, fullErase, progress, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<RipOperationResult> ReadSectorsAsync(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var backend = SelectBackend(recorder);
        return backend.ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken);
    }

    /// <inheritdoc/>
    public bool SupportsC2ErrorPointers(IDiscRecorderBackend recorder, int probeLba)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return SelectBackend(recorder) is ISecureAudioReader secure && secure.SupportsC2ErrorPointers(recorder, probeLba);
    }

    /// <inheritdoc/>
    public Task<RipOperationResult> ReadAudioSectorsWithC2Async(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, int, ValueTask> sectorConsumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return SelectBackend(recorder) is ISecureAudioReader secure
            ? secure.ReadAudioSectorsWithC2Async(recorder, startLba, sectorCount, sectorConsumer, cancellationToken)
            : Task.FromResult(RipOperationResult.Failed("This drive backend does not support C2 error pointers."));
    }

    /// <inheritdoc/>
    public Task FlushReadCacheAsync(IDiscRecorderBackend recorder, int nearLba, int audioEndLba, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return SelectBackend(recorder) is ISecureAudioReader secure
            ? secure.FlushReadCacheAsync(recorder, nearLba, audioEndLba, cancellationToken)
            : Task.CompletedTask;
    }

    private IBurnEngineBackend SelectBackend(IDiscRecorderBackend recorder)
    {
        return recorder.IsVirtual ? _virtualEngine : _physicalEngine;
    }
}
