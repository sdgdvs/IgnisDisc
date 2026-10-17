using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Orchestration service for erasing and blanking rewritable optical media (CD-RW, DVD-RW, DVD+RW, DVD-RAM)
/// in Quick (TOC/PMA/Lead-in wipe) and Full (complete physical sector overwrite/zero-fill) modes.
/// </summary>
public class DiscEraseService
{
    private readonly IBurnEngineBackend? _defaultEngine;

    /// <summary>
    /// Gets the default burning engine backend configured for this service instance, if any.
    /// </summary>
    public IBurnEngineBackend? BurnEngine => _defaultEngine;

    /// <summary>
    /// Initializes a new instance of <see cref="DiscEraseService"/> with an optional burning engine backend.
    /// </summary>
    /// <param name="burnEngine">Optional default burn engine backend.</param>
    public DiscEraseService(IBurnEngineBackend? burnEngine = null)
    {
        _defaultEngine = burnEngine;
    }

    /// <summary>
    /// Validates whether the specified physical medium type is rewritable and eligible for erasure.
    /// Returns true for CD-RW, DVD-RW, DVD+RW, and DVD-RAM.
    /// </summary>
    /// <param name="mediaType">The optical physical media type to test.</param>
    public static bool IsRewritable(DiscMediaType mediaType) =>
        mediaType is DiscMediaType.CdRw or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusRw or DiscMediaType.DvdRam;

    /// <summary>
    /// Validates whether the media currently inserted in the specified recorder can be erased.
    /// </summary>
    /// <param name="recorder">The optical disc recorder to test.</param>
    /// <returns>True if media is present and rewritable; otherwise false.</returns>
    public static bool CanErase(IDiscRecorderBackend recorder)
    {
        if (recorder == null) return false;
        var status = recorder.QueryMediaStatus();
        return status.MediaPresent && (status.IsRewritable || IsRewritable(status.MediaType));
    }

    /// <summary>
    /// Erases rewritable media inserted in the specified recorder using the default or injected engine.
    /// </summary>
    /// <param name="recorder">Target optical recorder containing rewritable media.</param>
    /// <param name="fullErase">True for full sector overwrite/zeroing; false for quick TOC blanking.</param>
    /// <param name="progress">Optional real-time progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    public Task<BurnOperationResult> EraseAsync(
        IDiscRecorderBackend recorder,
        bool fullErase = false,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_defaultEngine == null)
        {
            throw new InvalidOperationException("No burn engine was provided to DiscEraseService constructor.");
        }

        return EraseAsync(recorder, _defaultEngine, fullErase, progress, cancellationToken);
    }

    /// <summary>
    /// Erases rewritable media inserted in the specified recorder using the specified burning engine backend.
    /// </summary>
    /// <param name="recorder">Target optical recorder containing rewritable media.</param>
    /// <param name="engine">Burning engine backend executing the physical erase command.</param>
    /// <param name="fullErase">True for full sector overwrite/zeroing; false for quick TOC blanking.</param>
    /// <param name="progress">Optional real-time progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    public async Task<BurnOperationResult> EraseAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        bool fullErase = false,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Query current media presence and status
        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return BurnOperationResult.Failed("No optical media present in recorder.");
        }

        // 2. Validate rewritable media type (CdRw, DvdMinusRw, DvdPlusRw, DvdRam)
        if (!IsRewritable(mediaStatus.MediaType) && !mediaStatus.IsRewritable)
        {
            return BurnOperationResult.Failed(
                $"Media type '{mediaStatus.MediaType.GetDisplayName()}' is not rewritable and cannot be erased. Supported types: CD-RW, DVD-RW, DVD+RW, DVD-RAM.");
        }

        // 3. Execute erase pipeline via engine backend
        return await engine.EraseMediaAsync(recorder, fullErase, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs a Quick Erase (TOC / lead-in invalidation) on the media currently inserted in <paramref name="recorder"/>.
    /// </summary>
    public Task<BurnOperationResult> QuickEraseAsync(
        IDiscRecorderBackend recorder,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
        => EraseAsync(recorder, fullErase: false, progress, cancellationToken);

    /// <summary>
    /// Performs a Quick Erase (TOC / lead-in invalidation) using the specified engine.
    /// </summary>
    public Task<BurnOperationResult> QuickEraseAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
        => EraseAsync(recorder, engine, fullErase: false, progress, cancellationToken);

    /// <summary>
    /// Performs a Full Erase (physical sector zero-fill) on the media currently inserted in <paramref name="recorder"/>.
    /// </summary>
    public Task<BurnOperationResult> FullEraseAsync(
        IDiscRecorderBackend recorder,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
        => EraseAsync(recorder, fullErase: true, progress, cancellationToken);

    /// <summary>
    /// Performs a Full Erase (physical sector zero-fill) using the specified engine.
    /// </summary>
    public Task<BurnOperationResult> FullEraseAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
        => EraseAsync(recorder, engine, fullErase: true, progress, cancellationToken);

    /// <summary>
    /// Alias for <see cref="EraseAsync(IDiscRecorderBackend, bool, IProgress{BurnProgressSnapshot}?, CancellationToken)"/>.
    /// </summary>
    public Task<BurnOperationResult> EraseMediaAsync(
        IDiscRecorderBackend recorder,
        bool fullErase = false,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
        => EraseAsync(recorder, fullErase, progress, cancellationToken);

    /// <summary>
    /// Alias for <see cref="EraseAsync(IDiscRecorderBackend, IBurnEngineBackend, bool, IProgress{BurnProgressSnapshot}?, CancellationToken)"/>.
    /// </summary>
    public Task<BurnOperationResult> EraseMediaAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        bool fullErase = false,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
        => EraseAsync(recorder, engine, fullErase, progress, cancellationToken);
}
