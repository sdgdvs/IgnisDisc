using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Burns data images (ISO files, data discs) with IgnisDisc's own drive commands instead of Windows' burning service.
/// Opt-in. Covers blank CD-R, CD-RW, DVD-R, DVD-RW and single-layer DVD+R, a new session on one of those left open,
/// and the overwritable DVD+RW, DVD-RW in restricted overwrite mode and DVD-RAM (blank, or written over when the user
/// allowed replacing their data), so far (<see cref="NativeTaoDataWriter"/>); any other disc is handed to the Windows
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

    /// <summary>Pauses while a reloaded disc spins up after a test write; tests replace it so they need not wait.</summary>
    public Action<TimeSpan> Wait { get; init; } = Thread.Sleep;

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
        // session (its file system points past it), so it is written as a new session at the disc's next writable
        // address, which the writer checks against the drive's own before anything is written.
        var status = recorder.QueryMediaStatus();
        bool appending = !status.IsBlank && !status.IsOverwriteOnly && status.IsAppendable && status.NextWritableLba > 0;
        string? reason = !imageStream.CanSeek
            ? "the image is not a seekable file"
            : status.MediaType is DiscMediaType.DvdMinusRDualLayer
                ? "direct writing covers dual-layer DVD+R but not dual-layer DVD-R yet"
                : status.MediaType is DiscMediaType.BdRDualLayer or DiscMediaType.BdReDualLayer or DiscMediaType.BdxlRTripleLayer
                    or DiscMediaType.BdxlRQuadLayer or DiscMediaType.BdxlReTripleLayer
                ? "direct writing covers single-layer Blu-ray discs so far"
                : !IsCovered(status.MediaType)
                    ? $"direct writing covers CD-R, CD-RW, DVD-R, DVD-RW, DVD+R, DVD+RW, DVD-RAM, BD-R and BD-RE so far, and this disc is {status.MediaType.GetDisplayName()}"
                    : appending && !AddsSessionsTo(status.MediaType)
                        ? $"direct writing adds sessions to CD-R, CD-RW, DVD-R, DVD-RW and DVD+R so far, and this disc is {status.MediaType.GetDisplayName()}"
                    : !status.IsBlank && !appending && !(status.IsOverwriteOnly && options.OverwriteExistingData)
                        ? "the disc already holds data that direct writing would not keep"
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
            OverwriteExistingData: options.OverwriteExistingData, LayerBreakSectors: options.LayerBreakSectors,
            SessionStartLba: appending ? status.NextWritableLba : null);

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
        log.Note($"IgnisDisc native data writer (CD Track-at-Once, DVD-R/-RW Disc-At-Once or incremental, DVD+R, DVD+R DL, DVD+RW, DVD-RW overwrite, DVD-RAM, BD-R, BD-RE), {kind}. Drive: {recorder.VendorId} {recorder.ProductId} {recorder.ProductRevision} ({(recorder.IsVirtual ? "virtual" : "physical")}).");
        log.Note($"Image: {request.ImageBytes:N0} bytes, close disc {request.CloseDisc}, speed asked {(request.WriteSpeedKiloBytesPerSecond?.ToString() ?? "fastest listed")}" +
                 (request.AppendsSession ? $", new session laid out from sector {request.SessionStartLba}." : "."));

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
        if (result.DiscRecorded && recorder is VirtualDiscRecorder { CurrentMedium: { } medium } && virtualDrive != null)
        {
            CommitToVirtualMedium(medium, virtualDrive, virtualDrive.DataDiscImage ?? Array.Empty<byte>());
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
            // A drive that can close its own tray reloads the disc here; any other stops for a reload by hand.
            if (!writer.DiscReadyFor(request) && !TrayReload.TryReload(log, log.Note, Wait, () => writer.DiscReadyFor(request), ct))
            {
                log.Note(NativeSaoAudioWriter.ReloadMessage);
                return test with { Success = false, StoppedBeforeWriting = true, Message = NativeSaoAudioWriter.ReloadMessage };
            }
        }

        log.Note("Step 2: real write.");
        return writer.Write(request, progress, ct);
    }

    /// <summary>
    /// Puts what the simulated drive recorded onto the virtual disc: an overwritable disc holds one volume from LBA 0;
    /// on any other the session just closed is added after the earlier ones, and the disc stays open at the drive's
    /// next session address or is closed, as the drive reports.
    /// </summary>
    private static void CommitToVirtualMedium(VirtualDiscMedium medium, VirtualMmcDrive drive, byte[] firstSessionImage)
    {
        medium.Raw2352Mode = false;
        bool overwriteOnly = medium.MediaType is DiscMediaType.DvdPlusRw or DiscMediaType.DvdRam or DiscMediaType.BdRe;
        int start = 0;
        byte[] data = firstSessionImage;
        if (overwriteOnly)
        {
            medium.Sessions.Clear(); // written over from sector 0: one volume, no session history
        }
        else if (drive.DataSessions.Count > 0)
        {
            var added = drive.DataSessions[^1];
            start = added.TrackStartLba;
            data = drive.ReadSessionData(added);
        }

        int sectors = data.Length / NativeTaoDataWriter.SectorSize;
        for (int i = 0; i < sectors; i++)
        {
            medium.WriteSector(start + i, data.AsSpan(i * NativeTaoDataWriter.SectorSize, NativeTaoDataWriter.SectorSize), NativeTaoDataWriter.SectorSize);
        }

        int trackNumber = medium.Sessions.Sum(s => s.Tracks.Count) + 1;
        var session = new VirtualSession { SessionNumber = medium.Sessions.Count + 1, StartLba = start, SectorCount = sectors };
        session.Tracks.Add(new VirtualTrack { TrackNumber = trackNumber, StartLba = start, SectorCount = sectors, IsAudio = false });
        medium.Sessions.Add(session);
        medium.IsBlank = false;
        medium.IsFinalized = !overwriteOnly && drive.DiscStatus == 2;
        medium.NextWritableLba = !overwriteOnly && drive.NextSessionAddress is int next ? next : start + sectors;
        medium.UsedSectors = Math.Max(medium.UsedSectors, start + sectors);
    }

    /// <summary>Discs the direct writer adds a session to (left open by an earlier burn).</summary>
    private static bool AddsSessionsTo(DiscMediaType type) =>
        type is DiscMediaType.CdR or DiscMediaType.CdRw or DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusR;

    /// <summary>Discs the direct writer handles (blank, single layer).</summary>
    private static bool IsCovered(DiscMediaType type) =>
        type is DiscMediaType.CdR or DiscMediaType.CdRw or DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusR
            or DiscMediaType.DvdPlusRw or DiscMediaType.DvdRam or DiscMediaType.BdR or DiscMediaType.BdRe or DiscMediaType.DvdPlusRDualLayer;

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
