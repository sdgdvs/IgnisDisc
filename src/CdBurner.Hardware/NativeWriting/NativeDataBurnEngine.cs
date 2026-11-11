using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Burns data images (ISO files, data discs) with IgnisDisc's own drive commands instead of Windows' burning service.
/// Opt-in. Covers blank CD-R, CD-RW, DVD-R, DVD-RW (sequential) and single-layer DVD+R, and DVD+RW (blank, or written
/// over when the user allowed replacing its data), so far
/// (<see cref="NativeTaoDataWriter"/>); any other disc, and a disc that already holds a session, is handed to the Windows
/// writer (<see cref="LastFallbackReason"/> says so). Reading (the read-back check), erasing and audio go to the Windows
/// engine. Every direct run saves a full SCSI command log.
/// </summary>
public sealed class NativeDataBurnEngine : IBurnEngineBackend
{
    private readonly IBurnEngineBackend _fallback;
    private readonly string _logDirectory;

    /// <param name="fallback">Engine for discs the direct writer does not cover, and for reading and erasing.</param>
    /// <param name="logDirectory">Where command logs go; defaults to %LOCALAPPDATA%\IgnisDisc\logs.</param>
    public NativeDataBurnEngine(IBurnEngineBackend? fallback = null, string? logDirectory = null)
    {
        _fallback = fallback ?? new CompositeBurnEngine();
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "logs");
    }

    /// <summary>Run a laser-off test write before the real one (see <see cref="NativeAudioBurnEngine.TestWriteFirst"/>).</summary>
    public bool TestWriteFirst { get; init; }

    /// <summary>Lets a test adjust the simulated drive before a burn on a virtual recorder.</summary>
    public Action<VirtualMmcDrive>? ConfigureVirtualDrive { get; init; }

    public string? LastLogPath { get; private set; }
    public NativeDataWriteResult? LastResult { get; private set; }
    public bool LastTestWritePassed { get; private set; }

    /// <summary>Why the last burn went to the Windows writer instead; null when it was written directly.</summary>
    public string? LastFallbackReason { get; private set; }

    /// <summary>One plain sentence on which writer burned the last disc and at what speed, for a status line.</summary>
    public string? LastSummary => LastFallbackReason
        ?? (LastResult is { DiscRecorded: true } r
            ? "Written with direct drive commands" + (r.SpeedSetKiloBytesPerSecond is int kb ? $" at {kb / r.OneXKiloBytesPerSecond:0.#}x." : ".")
            : null);

    public async Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(imageStream);
        options ??= new DataBurnOptions();
        LastLogPath = null;
        LastResult = null;
        LastTestWritePassed = false;
        LastFallbackReason = null;

        // A disc that already holds a session is never erased here: DataBurnService built the image to follow that
        // session (its file system points past it), so it is appended by the Windows writer until sessions are done.
        var status = recorder.QueryMediaStatus();
        string? reason = !imageStream.CanSeek
            ? "the image is not a seekable file"
            : status.MediaType is DiscMediaType.DvdMinusRDualLayer or DiscMediaType.DvdPlusRDualLayer
                ? "direct writing does not cover dual-layer DVDs yet, because IgnisDisc does not set the layer break on the drive yet"
                : !IsCovered(status.MediaType)
                    ? $"direct writing covers CD-R, CD-RW, DVD-R, DVD-RW, DVD+R and DVD+RW so far, and this disc is {status.MediaType.GetDisplayName()}"
                    : !status.IsBlank && !(status.MediaType == DiscMediaType.DvdPlusRw && options.OverwriteExistingData)
                        ? "the disc already holds data, and direct writing adds no sessions yet"
                        : null;
        if (reason != null)
        {
            LastFallbackReason = $"Written with Windows' writer: {reason}.";
            return await _fallback.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken).ConfigureAwait(false);
        }

        long start = imageStream.Position;
        long bytes = imageStream.Length - start;
        // The drive counts speed in kB/s: 176.4 per 1x on CD (audio units), 1,385 per 1x on DVD.
        int? speed = options.Speed is { Multiplier: > 0 } s
            ? (int)Math.Round(s.Multiplier * WriteSpeedDescriptor.GetBaseRate(status.MediaType, isAudio: status.MediaType.IsCd()) / 1000.0)
            : null;
        var request = new NativeDataWriteRequest(imageStream, start, bytes, TestWrite: options.Simulate, options.CloseDisc, speed,
            OverwriteExistingData: options.OverwriteExistingData);

        var result = await Task.Run(() => Run(recorder, request, progress, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        if (result is { UnsupportedDisc: true, StoppedBeforeWriting: true })
        {
            // The drive reported a different disc than Windows did; nothing was written, so let Windows write it.
            LastFallbackReason = $"Written with Windows' writer: {result.Message}";
            imageStream.Position = start;
            return await _fallback.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken).ConfigureAwait(false);
        }

        imageStream.Position = start;
        if (result.Success)
        {
            return BurnOperationResult.Succeeded(result.DataSectorsWritten, result.Elapsed, result.AverageWriteMultiplier);
        }

        string message = result.StoppedBeforeWriting ? $"{result.Message} Nothing was written to the disc." : result.Message;
        if (LastLogPath != null)
        {
            message += $" Command log: {LastLogPath}";
        }
        return result.Cancelled ? BurnOperationResult.CancelledResult(message) : BurnOperationResult.Failed(message);
    }

    private NativeDataWriteResult Run(IDiscRecorderBackend recorder, NativeDataWriteRequest request, IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        IScsiTransport device;
        Win32ScsiTransport? win32 = null;
        VirtualMmcDrive? virtualDrive = null;
        try
        {
            if (recorder is VirtualDiscRecorder virtualRecorder)
            {
                device = virtualDrive = NativeAudioBurnEngine.CreateVirtualDrive(virtualRecorder);
                ConfigureVirtualDrive?.Invoke(virtualDrive);
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                {
                    return Stopped("Direct writing needs Windows.");
                }
                device = win32 = Win32ScsiTransport.Open(NativeAudioBurnEngine.DevicePathFor(recorder));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Stopped($"Could not open the drive: {ex.Message}");
        }

        using var log = new LoggingScsiTransport(device, ownsInner: true);
        string kind = request.TestWrite ? "test write only" : TestWriteFirst ? "test write, then real write" : "real write";
        log.Note($"IgnisDisc native data writer (CD Track-at-Once, DVD-R/-RW Disc-At-Once or incremental, DVD+R), {kind}. Drive: {recorder.VendorId} {recorder.ProductId} {recorder.ProductRevision} ({(recorder.IsVirtual ? "virtual" : "physical")}).");
        log.Note($"Image: {request.ImageBytes:N0} bytes, close disc {request.CloseDisc}, speed asked {(request.WriteSpeedKiloBytesPerSecond?.ToString() ?? "fastest listed")}.");

        IDisposable? exclusive = null;
        if (win32 != null)
        {
            try
            {
                exclusive = CdromExclusiveAccess.Acquire(win32.Handle, "IgnisDisc");
                log.Note("Exclusive access taken.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                log.Note($"Exclusive access refused: {ex.Message}");
                SaveLog(log);
                return Stopped(ex.Message);
            }
        }

        NativeDataWriteResult result;
        try
        {
            var writer = new NativeTaoDataWriter(log, log.Note);
            result = WriteWithTestFirst(writer, request, log, progress, ct);
            log.Note($"Result: {(result.Success ? "success" : "failed")}: {result.Message}");
        }
        finally
        {
            exclusive?.Dispose();
            SaveLog(log);
        }

        LastResult = result;
        if (result.DiscRecorded && recorder is VirtualDiscRecorder { CurrentMedium: { } medium } && virtualDrive?.DataDiscImage is { } image)
        {
            CommitToVirtualMedium(medium, image, request.CloseDisc);
        }
        return result;
    }

    private NativeDataWriteResult WriteWithTestFirst(NativeTaoDataWriter writer, NativeDataWriteRequest request, LoggingScsiTransport log,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        if (request.TestWrite || !TestWriteFirst)
        {
            return writer.Write(request, progress, ct);
        }

        log.Note("Step 1: test write (laser off).");
        var test = writer.Write(request with { TestWrite = true }, progress, ct);
        if (test.TestWriteNotOffered)
        {
            log.Note("The drive has no test-write mode for this disc; going straight to the real write.");
        }
        else if (!test.Success)
        {
            return test.Cancelled || test.DiscStillBlankAfterTestWrite == false
                ? test
                : test with { Message = "The test write failed, so the disc was not recorded: " + test.Message };
        }
        else
        {
            LastTestWritePassed = true;
            if (!writer.DiscReadsBlank())
            {
                log.Note(NativeSaoAudioWriter.ReloadMessage);
                return test with { Success = false, StoppedBeforeWriting = true, Message = NativeSaoAudioWriter.ReloadMessage };
            }
        }

        log.Note("Step 2: real write.");
        return writer.Write(request, progress, ct);
    }

    /// <summary>Puts what the simulated drive recorded onto the virtual disc: one data track from LBA 0.</summary>
    private static void CommitToVirtualMedium(VirtualDiscMedium medium, byte[] image, bool closeDisc)
    {
        medium.Raw2352Mode = false;
        bool overwriteOnly = medium.MediaType == DiscMediaType.DvdPlusRw;
        if (overwriteOnly)
        {
            medium.Sessions.Clear(); // written over from sector 0: one volume, no session history
        }
        int sectors = image.Length / NativeTaoDataWriter.SectorSize;
        for (int lba = 0; lba < sectors; lba++)
        {
            medium.WriteSector(lba, image.AsSpan(lba * NativeTaoDataWriter.SectorSize, NativeTaoDataWriter.SectorSize), NativeTaoDataWriter.SectorSize);
        }

        var session = new VirtualSession { SessionNumber = medium.Sessions.Count + 1, StartLba = 0, SectorCount = sectors };
        session.Tracks.Add(new VirtualTrack { TrackNumber = 1, StartLba = 0, SectorCount = sectors, IsAudio = false });
        medium.Sessions.Add(session);
        medium.IsBlank = false;
        medium.IsFinalized = closeDisc && !overwriteOnly;
        medium.NextWritableLba = sectors;
        medium.UsedSectors = Math.Max(medium.UsedSectors, sectors);
    }

    /// <summary>Discs the direct writer handles (blank, single layer).</summary>
    private static bool IsCovered(DiscMediaType type) =>
        type is DiscMediaType.CdR or DiscMediaType.CdRw or DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusR
            or DiscMediaType.DvdPlusRw;

    private static NativeDataWriteResult Stopped(string message) =>
        new() { Success = false, StoppedBeforeWriting = true, Message = message };

    private void SaveLog(LoggingScsiTransport log)
    {
        try
        {
            string path = Path.Combine(_logDirectory, $"native-write-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            log.SaveTo(path);
            LastLogPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLogPath = null;
        }
    }

    public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        _fallback.BurnAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);

    public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken) =>
        _fallback.EraseMediaAsync(recorder, fullErase, progress, cancellationToken);

    public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        _fallback.ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken);

    public Task<RipOperationResult> ReadSectorsForVerifyAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken,
        bool strict = false, SectorRange? writtenRange = null) =>
        _fallback.ReadSectorsForVerifyAsync(recorder, startLba, sectorCount, sectorConsumer, progress, cancellationToken, strict, writtenRange);
}
