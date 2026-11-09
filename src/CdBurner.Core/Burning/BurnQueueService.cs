using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Burning;

/// <summary>
/// Status of an individual job item within a burn queue.
/// </summary>
public enum BurnQueueItemStatus
{
    /// <summary>
    /// Item is waiting in the queue.
    /// </summary>
    Pending,

    /// <summary>
    /// Item is currently being written or verified.
    /// </summary>
    Burning,

    /// <summary>
    /// Item completed burning and verification successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Item failed to burn or verify.
    /// </summary>
    Failed,

    /// <summary>
    /// Item was cancelled by user request.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Item was skipped due to earlier queue failure or cancellation.
    /// </summary>
    Skipped
}

/// <summary>
/// Represents a single image burn job in the sequential burn queue.
/// </summary>
public record BurnQueueItem(
    string ImagePath,
    double? WriteSpeedMultiplier = null,
    bool VerifyAfterBurn = true,
    bool CloseDisc = true)
{
    /// <summary>
    /// Current execution state of this queue item.
    /// </summary>
    public BurnQueueItemStatus Status { get; set; } = BurnQueueItemStatus.Pending;

    /// <summary>
    /// Error message if the burn or verification failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Result metrics from the underlying burn engine upon completion.
    /// </summary>
    public BurnOperationResult? BurnResult { get; set; }
}

/// <summary>
/// Overall summary result of a sequential burn queue execution.
/// </summary>
public sealed record BurnQueueResult
{
    /// <summary>
    /// Indicates whether all items in the queue were successfully burned and verified.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Total number of items submitted in the queue.
    /// </summary>
    public int TotalItems { get; init; }

    /// <summary>
    /// Number of items successfully burned.
    /// </summary>
    public int CompletedItems { get; init; }

    /// <summary>
    /// Number of items that failed to burn or verify.
    /// </summary>
    public int FailedItems { get; init; }

    /// <summary>
    /// The processed list of queue items with individual results.
    /// </summary>
    public IReadOnlyList<BurnQueueItem> Items { get; init; } = Array.Empty<BurnQueueItem>();

    /// <summary>
    /// Total wall-clock time elapsed for the entire queue execution.
    /// </summary>
    public TimeSpan TotalTime { get; init; }

    /// <summary>
    /// Descriptive error message if any queue job failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Orchestrates sequential batch burning of multiple ISO and legacy optical disc images
/// with automated disc ejection, optional disc-swap prompts, and after-burn actions.
/// </summary>
public class BurnQueueService
{
    private readonly IBurnEngineBackend _burnEngine;
    private readonly DataBurnService _dataBurnService;

    /// <summary>
    /// Underlying burning engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Underlying data burning service.
    /// </summary>
    public DataBurnService DataBurnService => _dataBurnService;

    /// <summary>
    /// Initializes a new instance of <see cref="BurnQueueService"/>.
    /// </summary>
    public BurnQueueService(IBurnEngineBackend burnEngine, DataBurnService? dataBurnService = null)
    {
        _burnEngine = burnEngine ?? throw new ArgumentNullException(nameof(burnEngine));
        _dataBurnService = dataBurnService ?? new DataBurnService(_burnEngine);
    }

    /// <summary>
    /// Executes a sequence of image burn jobs sequentially on the specified optical recorder.
    /// </summary>
    /// <param name="recorder">The target optical disc recorder.</param>
    /// <param name="items">The collection of burn queue items to process sequentially.</param>
    /// <param name="promptForNextDiscAsync">Optional callback invoked between discs on a single drive to allow disc swapping.</param>
    /// <param name="progress">Optional real-time progress reporter for burning and verification.</param>
    /// <param name="postBurnAction">Action to execute after all queue items have completed.</param>
    /// <param name="soundPlayer">Optional custom sound player callback for <see cref="PostBurnAction"/>.</param>
    /// <param name="shutdownInitiator">Optional custom shutdown initiator for <see cref="PostBurnAction"/>.</param>
    /// <param name="continueOnError">Whether to proceed with remaining queue items if an item fails.</param>
    /// <param name="cancellationToken">Cancellation token to cancel queue execution.</param>
    public async Task<BurnQueueResult> ExecuteQueueAsync(
        IDiscRecorderBackend recorder,
        IReadOnlyList<BurnQueueItem> items,
        Func<int, BurnQueueItem, CancellationToken, Task<bool>>? promptForNextDiscAsync = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        PostBurnAction postBurnAction = PostBurnAction.None,
        Action? soundPlayer = null,
        Action? shutdownInitiator = null,
        bool continueOnError = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return new BurnQueueResult
            {
                Success = true,
                TotalItems = 0,
                CompletedItems = 0,
                FailedItems = 0,
                Items = items,
                TotalTime = TimeSpan.Zero
            };
        }

        var stopwatch = Stopwatch.StartNew();
        int completed = 0;
        int failed = 0;
        bool cancelled = false;
        string? failureReason = null;

        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];

            // When burning multiple discs sequentially on a single drive, eject previous media and prompt for next blank disc
            if (i > 0)
            {
                try
                {
                    recorder.EjectMedia();
                }
                catch
                {
                    // Ignore hardware ejection exceptions
                }

                if (promptForNextDiscAsync != null)
                {
                    bool proceed = await promptForNextDiscAsync(i, item, cancellationToken).ConfigureAwait(false);
                    if (!proceed)
                    {
                        cancelled = true;
                        item.Status = BurnQueueItemStatus.Cancelled;
                        item.ErrorMessage = "Queue cancelled by user prior to disc insertion.";
                        for (int rem = i + 1; rem < items.Count; rem++)
                        {
                            items[rem].Status = BurnQueueItemStatus.Skipped;
                        }
                        break;
                    }
                }
            }

            item.Status = BurnQueueItemStatus.Burning;

            try
            {
                if (!File.Exists(item.ImagePath))
                {
                    item.Status = BurnQueueItemStatus.Failed;
                    item.ErrorMessage = $"Image file not found: {item.ImagePath}";
                    item.BurnResult = BurnOperationResult.Failed(item.ErrorMessage);
                    failed++;
                    failureReason ??= item.ErrorMessage;

                    if (!continueOnError)
                    {
                        for (int rem = i + 1; rem < items.Count; rem++)
                        {
                            items[rem].Status = BurnQueueItemStatus.Skipped;
                        }
                        break;
                    }
                    continue;
                }

                var burnOptions = new DataBurnOptions
                {
                    VolumeLabel = Path.GetFileNameWithoutExtension(item.ImagePath),
                    CloseDisc = item.CloseDisc,
                    VerifyAfterBurn = item.VerifyAfterBurn,
                    Speed = item.WriteSpeedMultiplier.HasValue
                        ? WriteSpeedDescriptor.FromMultiplier(item.WriteSpeedMultiplier.Value, recorder.QueryMediaStatus().MediaType)
                        : null,
                    // A queue runs unattended: never silently replace what is on a rewritable disc.
                    // DataBurnService fails the item with a clear message instead.
                    OverwriteExistingData = false
                };

                BurnOperationResult burnResult;
                if (!IsQueueableImage(item.ImagePath, out string? unsupportedReason))
                {
                    burnResult = BurnOperationResult.Failed(unsupportedReason!);
                }
                else if (!string.Equals(Path.GetExtension(item.ImagePath), ".iso", StringComparison.OrdinalIgnoreCase))
                {
                    // .bin and legacy images (.nrg/.mdf/.ccd/.img) burn their 2,048-byte user data,
                    // extracted from raw sectors on the fly. Audio images and raw files without an ISO 9660
                    // volume are refused by OpenIsoUserDataStream instead of being written as garbage data.
                    var legacyInfo = LegacyDiscImageConverter.Inspect(item.ImagePath);
                    await using var stream = LegacyDiscImageConverter.OpenIsoUserDataStream(legacyInfo);
                    var dataResult = await _dataBurnService.BurnStreamAsync(
                        recorder,
                        stream,
                        burnOptions,
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    burnResult = dataResult.Success
                        ? BurnOperationResult.Succeeded(dataResult.SectorsWritten, dataResult.TotalTime, dataResult.AverageSpeedMultiplier)
                        : BurnOperationResult.Failed(dataResult.ErrorMessage ?? "Burn failed");
                }
                else
                {
                    var dataResult = await _dataBurnService.BurnIsoFileAsync(
                        recorder,
                        item.ImagePath,
                        burnOptions,
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    burnResult = dataResult.Success
                        ? BurnOperationResult.Succeeded(dataResult.SectorsWritten, dataResult.TotalTime, dataResult.AverageSpeedMultiplier)
                        : BurnOperationResult.Failed(dataResult.ErrorMessage ?? "Burn failed");
                }

                item.BurnResult = burnResult;
                if (burnResult.Success)
                {
                    item.Status = BurnQueueItemStatus.Completed;
                    completed++;
                }
                else
                {
                    item.Status = BurnQueueItemStatus.Failed;
                    item.ErrorMessage = burnResult.ErrorMessage;
                    failed++;
                    failureReason ??= item.ErrorMessage;

                    if (!continueOnError)
                    {
                        for (int rem = i + 1; rem < items.Count; rem++)
                        {
                            items[rem].Status = BurnQueueItemStatus.Skipped;
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                item.Status = BurnQueueItemStatus.Cancelled;
                item.ErrorMessage = "Queue execution cancelled.";
                item.BurnResult = BurnOperationResult.CancelledResult(item.ErrorMessage);
                for (int rem = i + 1; rem < items.Count; rem++)
                {
                    items[rem].Status = BurnQueueItemStatus.Skipped;
                }
                break;
            }
            catch (Exception ex)
            {
                item.Status = BurnQueueItemStatus.Failed;
                item.ErrorMessage = ex.Message;
                item.BurnResult = BurnOperationResult.Failed(ex.Message);
                failed++;
                failureReason ??= ex.Message;

                if (!continueOnError)
                {
                    for (int rem = i + 1; rem < items.Count; rem++)
                    {
                        items[rem].Status = BurnQueueItemStatus.Skipped;
                    }
                    break;
                }
            }
        }

        stopwatch.Stop();

        // Run the after-burn action once the queue has run to its end. Never after the user cancelled:
        // someone who just pressed Cancel is at the computer and must not have it shut down.
        if (postBurnAction != PostBurnAction.None && !cancelled)
        {
            PostBurnActionExecutor.Execute(postBurnAction, recorder, soundPlayer, shutdownInitiator);
        }

        return new BurnQueueResult
        {
            Success = failed == 0 && completed == items.Count,
            TotalItems = items.Count,
            CompletedItems = completed,
            FailedItems = failed,
            Items = items,
            TotalTime = stopwatch.Elapsed,
            ErrorMessage = failureReason
        };
    }

    /// <summary>
    /// The queue burns data images only: <c>.iso</c>, a <c>.bin</c> holding an ISO 9660 volume, and Nero / Alcohol /
    /// CloneCD images. A <c>.cue</c> sheet (often audio or mixed-mode) needs the track-aware burn in Image Studio.
    /// </summary>
    public static bool IsQueueableImage(string imagePath, out string? reason)
    {
        string ext = Path.GetExtension(imagePath).ToLowerInvariant();
        if (ext is ".iso" or ".bin" || LegacyDiscImageConverter.IsLegacyImageFile(imagePath))
        {
            reason = null;
            return true;
        }

        reason = ext == ".cue"
            ? $"'{Path.GetFileName(imagePath)}' is a CUE sheet. The burn queue takes data images only; burn CUE sheets from the Burn Image tab."
            : $"'{Path.GetFileName(imagePath)}' is not a supported image for the burn queue (.iso, .bin, .nrg, .mdf, .ccd, .img).";
        return false;
    }
}
