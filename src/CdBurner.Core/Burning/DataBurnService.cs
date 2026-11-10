global using DataBurnStatus = CdBurner.Core.Abstractions.BurnStatus;
using System.Security.Cryptography;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
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
        string? verificationNote = null;
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
            verificationNote = verifyResult.Note;
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
            verified,
            verificationNote);
    }

    /// <summary>
    /// Reads the burned sectors back and compares each one with the image (roadmap M1.3). The drive is told to read
    /// the disc itself, not its cache (<see cref="IBurnEngineBackend.ReadSectorsForVerifyAsync"/>). The disc's file
    /// system is parsed from its own sectors, so bad sectors are reported with the files that own them, and when the
    /// disc carries a root SHA256SUMS/BLAKE3SUMS every listed file is checked against it in the same pass.
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage, string? Note)> VerifyBurnedDiscAsync(
        IDiscRecorderBackend recorder,
        Stream sourceStream,
        int sectorCount,
        long initialStreamPosition,
        int startLba,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();

        // 1. The file system and hash lists, read from the disc's own sectors (small random reads, off this thread).
        var (fileMap, manifestCheck, manifestError) = await Task.Run(
            () => ReadDiscLayout(recorder, startLba, sectorCount, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (manifestError != null)
        {
            return (false, manifestError, null);
        }

        // 2. One pass over the disc: compare every sector with the image and feed the hash-list check.
        if (sourceStream.CanSeek)
        {
            sourceStream.Position = initialStreamPosition;
        }

        byte[] expected = new byte[OpticalConstants.DataSectorSize];
        var mismatchedLbas = new List<int>();
        var ripResult = await _burnEngine.ReadSectorsForVerifyAsync(
            recorder,
            startLba,
            sectorCount,
            sectorConsumer: async (lba, sectorData) =>
            {
                int read = await ReadSectorFullyAsync(sourceStream, expected, cancellationToken).ConfigureAwait(false);
                if (read < expected.Length)
                {
                    Array.Clear(expected, read, expected.Length - read);
                }
                if (!sectorData.Span.SequenceEqual(expected))
                {
                    mismatchedLbas.Add(lba);
                }
                manifestCheck?.AddSector(lba, sectorData.Span);
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        if (ripResult.Note != null)
        {
            notes.Add(ripResult.Note);
        }

        if (!ripResult.Success)
        {
            string where = ripResult.BadSectorsList.Count > 0 ? DescribeFiles(ripResult.BadSectorsList, fileMap) : string.Empty;
            return (false, (ripResult.ErrorMessage ?? "Failed to read burned sectors during verification.") + where, null);
        }

        if (mismatchedLbas.Count > 0)
        {
            return (false,
                $"Verification failed: {mismatchedLbas.Count} sector(s) on the disc differ from the image, starting at sector {mismatchedLbas[0]}." +
                DescribeFiles(mismatchedLbas, fileMap), null);
        }

        if (manifestCheck != null)
        {
            var check = manifestCheck.Finish();
            string lists = string.Join(" and ", check.ManifestNames);
            if (!check.Passed)
            {
                var bad = check.MismatchedFiles.Concat(check.MissingFiles.Select(p => p + " (missing)")).ToList();
                return (false,
                    $"Verification failed: {bad.Count} file(s) on the disc do not match its {lists}: " +
                    string.Join(", ", bad.Take(10)) + (bad.Count > 10 ? ", ..." : "") + ".", null);
            }
            notes.Add($"All {check.FilesChecked} file(s) match the disc's {lists}." +
                (check.FilesNotLookedUp > 0 ? $" {check.FilesNotLookedUp} file(s) with UDF-only names were checked by sector compare only." : ""));
        }

        return (true, null, notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    /// <summary>
    /// Parses the burned file system from the disc's sectors. Returns the file extents (to name bad sectors) and the
    /// hash-list check, or an error if the disc has a hash list that cannot be read. A disc whose file system cannot
    /// be parsed (e.g. a non-ISO image) is still verified sector by sector, just without file names.
    /// </summary>
    private (IReadOnlyList<LbaFileExtentMap>? FileMap, DiscManifestCheck? Check, string? Error) ReadDiscLayout(
        IDiscRecorderBackend recorder, int startLba, int sectorCount, CancellationToken cancellationToken)
    {
        string? ReadSectors(int lba, int count, byte[] buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int filled = 0;
            var result = _burnEngine.ReadSectorsForVerifyAsync(recorder, lba, count, (_, data) =>
            {
                data.Span.CopyTo(buffer.AsSpan(filled * OpticalConstants.DataSectorSize));
                filled++;
                return ValueTask.CompletedTask;
            }, null, cancellationToken).GetAwaiter().GetResult();
            return result.Success && filled == count ? null : result.ErrorMessage ?? "read failed";
        }

        IsoImageReader reader;
        try
        {
            reader = IsoImageReader.FromStream(new SectorReadStream(ReadSectors, startLba + sectorCount), leaveOpen: false, startLba);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return (null, null, null);
        }

        using (reader)
        {
            var map = LbaFileExtentMap.FromIsoReader(reader);
            try
            {
                return (map, DiscManifestCheck.Prepare(reader), null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return (map, null, $"Verification failed: the hash list on the disc could not be read ({ex.Message}).");
            }
        }
    }

    private static string DescribeFiles(IReadOnlyList<int> lbas, IReadOnlyList<LbaFileExtentMap>? fileMap)
    {
        if (fileMap == null || fileMap.Count == 0)
        {
            return string.Empty;
        }
        var files = new List<string>();
        foreach (int lba in lbas)
        {
            foreach (var extent in fileMap)
            {
                if (extent.SectorCount > 0 && extent.ContainsLba(lba) && !files.Contains(extent.FilePath))
                {
                    files.Add(extent.FilePath);
                }
            }
        }
        return files.Count == 0
            ? " The damage is in the file system area, outside any file."
            : " Affected file(s): " + string.Join(", ", files.Take(10)) + (files.Count > 10 ? ", ..." : "") + ".";
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
    /// What the post-burn check found worth saying when it passed, e.g. how many files matched the disc's SHA256SUMS,
    /// or that the drive would not skip its cache. Null when there is nothing to add.
    /// </summary>
    public string? VerificationNote { get; init; }

    /// <summary>
    /// Optional path to the temporary or source ISO image file burned.
    /// </summary>
    public string? ImagePath { get; init; }

    public static DataBurnResult Succeeded(
        int sectorsWritten,
        TimeSpan duration,
        double avgSpeed = 0.0,
        bool verified = false,
        string? verificationNote = null) => new()
    {
        Success = true,
        Status = BurnStatus.Succeeded,
        SectorsWritten = sectorsWritten,
        TotalTime = duration,
        AverageSpeedMultiplier = avgSpeed,
        Verified = verified,
        VerificationNote = verificationNote
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
