global using DataBurnStatus = CdBurner.Core.Abstractions.BurnStatus;
using System.Security.Cryptography;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Burning;

/// <summary>
/// High-level orchestration service for data disc and ISO image burning, capacity validation,
/// post-burn sector verification, and hardware media ejection.
/// </summary>
public class DataBurnService
{
    private readonly IBurnEngineBackend _burnEngine;

    /// <summary>
    /// Shown when a burn would replace the files on a used rewritable disc and the caller did not allow it.
    /// </summary>
    public const string ReplaceExistingDataMessage =
        "This rewritable disc already has files on it, and burning will replace all of them. " +
        "Confirm that you want to replace them (command line: add --overwrite), or erase the disc first.";

    /// <summary>
    /// Shown when a burn targets a finalized sequential rewritable disc (such as CD-RW or sequential DVD-RW)
    /// that cannot be overwritten in place and must be erased first.
    /// </summary>
    public const string EraseRequiredMessage =
        "This rewritable disc already has finalized data on it and must be erased before burning again " +
        "(Drive Utilities -> Erase, or ignisdisc-cli erase --drive <id>).";

    /// <summary>
    /// Gets the underlying burning engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Initializes a new instance of <see cref="DataBurnService"/> with the specified burning engine backend.
    /// </summary>
    /// <param name="burnEngine">The backend engine executing data streams against recorders.</param>
    public DataBurnService(IBurnEngineBackend burnEngine)
    {
        _burnEngine = burnEngine ?? throw new ArgumentNullException(nameof(burnEngine));
    }

    /// <summary>
    /// Compiles a virtual file system hierarchy from a <see cref="CompilationDirectory"/> into an ISO/UDF image stream
    /// and burns it to the target recorder.
    /// </summary>
    public async Task<DataBurnResult> BurnCompilationAsync(
        IDiscRecorderBackend recorder,
        CompilationDirectory rootDirectory,
        DataBurnOptions? burnOptions = null,
        IsoBuildOptions? buildOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(rootDirectory);
        burnOptions ??= new DataBurnOptions();
        buildOptions ??= new IsoBuildOptions(VolumeLabel: burnOptions.VolumeLabel ?? "CDBURNER_DISC");

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Validate media presence & state
        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return DataBurnResult.Failed("No optical media present in recorder.");
        }

        if (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable)
        {
            return DataBurnResult.Failed("Mounted media is not writable (disc is finalized or not appendable).");
        }

        if (mediaStatus.RequiresEraseToWrite)
        {
            return DataBurnResult.Failed(EraseRequiredMessage);
        }

        // 2. Build ISO image in memory
        progress?.Report(new BurnProgressSnapshot
        {
            CurrentActionDescription = "BuildingFileSystem",
            CurrentLba = 0,
            TotalLba = 100,
            BufferLevelPercent = 100.0
        });

        cancellationToken.ThrowIfCancellationRequested();

        long estimatedPayloadBytes = rootDirectory.GetAllFilesRecursive().Sum(f => f.SizeBytes);
        Stream imageStream = estimatedPayloadBytes > 128L * 1024 * 1024
            ? new FileStream(
                Path.Combine(Path.GetTempPath(), $"cdburner_iso_{Guid.NewGuid():N}.tmp"),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 65536,
                options: FileOptions.DeleteOnClose | FileOptions.SequentialScan | FileOptions.Asynchronous)
            : new MemoryStream();

        // When appending a session, the file system must be laid out relative to the drive's next writable address,
        // otherwise every directory and file pointer in the new session is off by the size of the earlier sessions.
        int sessionStartLba = (!mediaStatus.IsBlank && mediaStatus.IsAppendable && mediaStatus.NextWritableLba > 0)
            ? mediaStatus.NextWritableLba
            : 0;

        using (imageStream)
        {
            await IsoUdfImageBuilder.BuildAsync(rootDirectory, imageStream, buildOptions, null, cancellationToken, sessionStartLba);
            imageStream.Position = 0;

            // 3. Verify media capacity
            int requiredSectors = OpticalConstants.BytesToSectors(imageStream.Length, OpticalConstants.DataSectorSize);
            if (mediaStatus.FreeSectors > 0 && requiredSectors > mediaStatus.FreeSectors)
            {
                return DataBurnResult.Failed(
                    $"Compilation size ({requiredSectors} sectors) exceeds available media capacity ({mediaStatus.FreeSectors} sectors).");
            }

            // 4. Burn stream
            return await ExecuteBurnPipelineAsync(recorder, imageStream, burnOptions, requiredSectors, progress, cancellationToken);
        }
    }

    /// <summary>
    /// Overload accepting <see cref="IsoBuildOptions"/> before <see cref="DataBurnOptions"/>.
    /// </summary>
    public Task<DataBurnResult> BurnCompilationAsync(
        IDiscRecorderBackend recorder,
        CompilationDirectory rootDirectory,
        IsoBuildOptions? buildOptions,
        DataBurnOptions? burnOptions,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return BurnCompilationAsync(recorder, rootDirectory, burnOptions, buildOptions, progress, cancellationToken);
    }

    /// <summary>
    /// Compiles and burns a full <see cref="DataCompilation"/> project to the target recorder.
    /// </summary>
    public Task<DataBurnResult> BurnCompilationAsync(
        IDiscRecorderBackend recorder,
        DataCompilation compilation,
        DataBurnOptions? burnOptions = null,
        IsoBuildOptions? buildOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        burnOptions ??= new DataBurnOptions { VolumeLabel = compilation.VolumeLabel };
        buildOptions ??= new IsoBuildOptions(VolumeLabel: compilation.VolumeLabel);
        return BurnCompilationAsync(recorder, compilation.Root, burnOptions, buildOptions, progress, cancellationToken);
    }

    /// <summary>
    /// Burns an existing <c>.iso</c> disk image file directly to the target recorder.
    /// </summary>
    public async Task<DataBurnResult> BurnIsoFileAsync(
        IDiscRecorderBackend recorder,
        string isoFilePath,
        DataBurnOptions? burnOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(isoFilePath);

        if (!File.Exists(isoFilePath))
        {
            return DataBurnResult.Failed($"ISO image file not found: {isoFilePath}");
        }

        await using var fileStream = new FileStream(isoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await BurnStreamAsync(recorder, fileStream, burnOptions, progress, cancellationToken);
    }

    /// <summary>
    /// Burns an arbitrary data image stream to the target recorder.
    /// </summary>
    public async Task<DataBurnResult> BurnStreamAsync(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions? burnOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(imageStream);
        burnOptions ??= new DataBurnOptions();

        cancellationToken.ThrowIfCancellationRequested();

        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return DataBurnResult.Failed("No optical media present in recorder.");
        }

        if (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable)
        {
            return DataBurnResult.Failed("Mounted media is not writable (disc is finalized or not appendable).");
        }

        if (mediaStatus.RequiresEraseToWrite)
        {
            return DataBurnResult.Failed(EraseRequiredMessage);
        }

        long streamLength = imageStream.CanSeek ? (imageStream.Length - imageStream.Position) : -1;
        int estimatedSectors = streamLength >= 0
            ? OpticalConstants.BytesToSectors(streamLength, OpticalConstants.DataSectorSize)
            : 0;

        if (estimatedSectors > 0 && mediaStatus.FreeSectors > 0 && estimatedSectors > mediaStatus.FreeSectors)
        {
            return DataBurnResult.Failed(
                $"Image size ({estimatedSectors} sectors) exceeds available media capacity ({mediaStatus.FreeSectors} sectors).");
        }

        return await ExecuteBurnPipelineAsync(recorder, imageStream, burnOptions, estimatedSectors, progress, cancellationToken);
    }

    private async Task<DataBurnResult> ExecuteBurnPipelineAsync(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions burnOptions,
        int estimatedSectors,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        bool ejectRequested = burnOptions.EjectWhenDone || burnOptions.EjectAfterBurn;
        bool verifyRequested = burnOptions.VerifyAfterBurn;
        long initialStreamPosition = imageStream.CanSeek ? imageStream.Position : 0;
        var preBurnStatus = recorder.QueryMediaStatus();
        if (preBurnStatus.HasDataThatBurningReplaces && !burnOptions.OverwriteExistingData)
        {
            return DataBurnResult.Failed(ReplaceExistingDataMessage);
        }

        int burnStartLba = (!preBurnStatus.IsBlank && preBurnStatus.IsAppendable && preBurnStatus.NextWritableLba > 0)
            ? preBurnStatus.NextWritableLba
            : 0;

        // When verification is requested, defer media ejection until verification finishes
        var engineOptions = new DataBurnOptions
        {
            Speed = burnOptions.Speed,
            Simulate = burnOptions.Simulate,
            EjectWhenDone = verifyRequested ? false : ejectRequested,
            VerifyAfterBurn = false,
            CloseDisc = burnOptions.CloseDisc,
            VolumeLabel = burnOptions.VolumeLabel,
            OverwriteExistingData = burnOptions.OverwriteExistingData
        };

        BurnOperationResult burnResult;
        try
        {
            burnResult = await _burnEngine.BurnDataStreamAsync(
                recorder,
                imageStream,
                engineOptions,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException oce)
        {
            return DataBurnResult.Cancelled(oce.Message);
        }

        if (!burnResult.Success || burnResult.Status == BurnStatus.Cancelled)
        {
            return DataBurnResult.FromBurnResult(burnResult);
        }

        bool verified = false;
        if (verifyRequested && !burnOptions.Simulate)
        {
            int sectorsWritten = burnResult.SectorsWritten > 0 ? burnResult.SectorsWritten : estimatedSectors;
            var verifyResult = await VerifyBurnedDiscAsync(
                recorder,
                imageStream,
                sectorsWritten,
                initialStreamPosition,
                burnStartLba,
                progress,
                cancellationToken);
            if (!verifyResult.Success)
            {
                if (ejectRequested)
                {
                    try { recorder.EjectMedia(); } catch { }
                }
                return DataBurnResult.Failed(verifyResult.ErrorMessage ?? "Post-burn verification failed.");
            }
            verified = true;
        }

        if (ejectRequested)
        {
            try
            {
                recorder.EjectMedia();
            }
            catch
            {
            }
        }

        return DataBurnResult.Succeeded(
            burnResult.SectorsWritten,
            burnResult.TotalTime,
            burnResult.AverageSpeedMultiplier,
            verified);
    }

    private async Task<(bool Success, string? ErrorMessage)> VerifyBurnedDiscAsync(
        IDiscRecorderBackend recorder,
        Stream sourceStream,
        int sectorCount,
        long initialStreamPosition,
        int startLba,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        if (sourceStream.CanSeek)
        {
            sourceStream.Position = initialStreamPosition;
        }

        using var sourceSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var discSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] sourceBuf = new byte[OpticalConstants.DataSectorSize];
        int sectorsToHash = sectorCount;

        // Hash source stream sectors
        while (sectorsToHash > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await ReadSectorFullyAsync(sourceStream, sourceBuf, cancellationToken);
            if (read == 0) break;
            if (read < OpticalConstants.DataSectorSize)
            {
                Array.Clear(sourceBuf, read, OpticalConstants.DataSectorSize - read);
            }
            sourceSha.AppendData(sourceBuf);
            sectorsToHash--;
        }

        // Read disc sectors and hash
        var ripResult = await _burnEngine.ReadSectorsAsync(
            recorder,
            startLba: startLba,
            sectorCount: sectorCount,
            raw2352Mode: false,
            sectorConsumer: (lba, sectorData) =>
            {
                discSha.AppendData(sectorData.Span);
                return ValueTask.CompletedTask;
            },
            progress: progress,
            cancellationToken: cancellationToken);

        if (!ripResult.Success)
        {
            return (false, ripResult.ErrorMessage ?? "Failed to read burned sectors during verification.");
        }

        byte[] sourceHash = sourceSha.GetHashAndReset();
        byte[] discHash = discSha.GetHashAndReset();

        if (!sourceHash.AsSpan().SequenceEqual(discHash))
        {
            return (false, "Verification failed: burned disc SHA-256 hash does not match source image.");
        }

        return (true, null);
    }

    private static async ValueTask<int> ReadSectorFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }
}

/// <summary>
/// Detailed result of a data disc burning operation, including verification status.
/// </summary>
public class DataBurnResult : BurnOperationResult
{
    /// <summary>
    /// Indicates whether post-burn verification was performed and passed successfully.
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>
    /// Optional path to the temporary or source ISO image file burned.
    /// </summary>
    public string? ImagePath { get; init; }

    public static DataBurnResult Succeeded(
        int sectorsWritten,
        TimeSpan duration,
        double avgSpeed = 0.0,
        bool verified = false) => new()
    {
        Success = true,
        Status = BurnStatus.Succeeded,
        SectorsWritten = sectorsWritten,
        TotalTime = duration,
        AverageSpeedMultiplier = avgSpeed,
        Verified = verified
    };

    public static new DataBurnResult Failed(string error, Exception? ex = null) => new()
    {
        Success = false,
        Status = ex is OperationCanceledException ? BurnStatus.Cancelled : BurnStatus.Failed,
        ErrorMessage = error,
        Exception = ex
    };

    public static DataBurnResult Cancelled(string error = "Burn operation was cancelled.") => new()
    {
        Success = false,
        Status = BurnStatus.Cancelled,
        ErrorMessage = error
    };

    public static DataBurnResult FromBurnResult(BurnOperationResult result, bool verified = false) => new()
    {
        Success = result.Success,
        Status = result.Status != BurnStatus.NotStarted
            ? result.Status
            : (result.Success ? BurnStatus.Succeeded : (result.Exception is OperationCanceledException ? BurnStatus.Cancelled : BurnStatus.Failed)),
        ErrorMessage = result.ErrorMessage,
        SectorsWritten = result.SectorsWritten,
        TotalTime = result.TotalTime,
        AverageSpeedMultiplier = result.AverageSpeedMultiplier,
        Exception = result.Exception,
        Verified = verified
    };
}
