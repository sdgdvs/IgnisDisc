using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Burns audio CDs Session-at-Once with IgnisDisc's own drive commands instead of Windows' burning service. Opt-in
/// only. A burn is the real write followed by a read-back check of the whole disc, optionally with a laser-off test
/// write first (<see cref="TestWriteFirst"/>). Every run saves a full SCSI command log.
/// <see cref="AudioBurnOptions.Simulate"/> does the test write only.
/// </summary>
public sealed class NativeAudioBurnEngine : IBurnEngineBackend
{
    private readonly string _logDirectory;

    /// <param name="logDirectory">Where command logs go; defaults to %LOCALAPPDATA%\IgnisDisc\logs.</param>
    public NativeAudioBurnEngine(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "logs");
    }

    /// <summary>
    /// Run a laser-off test write before the real write, on drives that offer one. Off by default: most drives keep
    /// showing the simulated session afterwards and need the disc reloaded before they record it, so on those the
    /// burn stops after the test with <see cref="NativeSaoAudioWriter.ReloadMessage"/>.
    /// </summary>
    public bool TestWriteFirst { get; init; }

    /// <summary>The command log written by the last burn, if any.</summary>
    public string? LastLogPath { get; private set; }

    /// <summary>The writer's own result for the last burn (the real write, or the test write if it stopped there).</summary>
    public NativeSaoWriteResult? LastResult { get; private set; }

    /// <summary>True when the last burn ran a test write that passed before the real write.</summary>
    public bool LastTestWritePassed { get; private set; }

    /// <summary>Lets a test adjust the simulated drive (offsets, faults) before a burn on a virtual recorder.</summary>
    public Action<VirtualMmcDrive>? ConfigureVirtualDrive { get; init; }

    /// <summary>Builds the virtual drive's SCSI side for the disc in a virtual recorder.</summary>
    public static VirtualMmcDrive CreateVirtualDrive(VirtualDiscRecorder recorder)
    {
        var medium = recorder.CurrentMedium;
        var drive = new VirtualMmcDrive { MediaPresent = medium != null && !recorder.TrayOpen };
        if (medium != null)
        {
            drive.Profile = medium.MediaType switch
            {
                DiscMediaType.CdR => NativeSaoAudioWriter.ProfileCdR,
                DiscMediaType.CdRw => NativeSaoAudioWriter.ProfileCdRw,
                DiscMediaType.DvdMinusR => NativeTaoDataWriter.ProfileDvdMinusR,
                DiscMediaType.DvdMinusRw => NativeTaoDataWriter.ProfileDvdMinusRwSequential,
                DiscMediaType.DvdPlusR => NativeTaoDataWriter.ProfileDvdPlusR,
                DiscMediaType.DvdMinusRDualLayer => NativeTaoDataWriter.ProfileDvdMinusRDualLayer,
                DiscMediaType.DvdPlusRDualLayer => NativeTaoDataWriter.ProfileDvdPlusRDualLayer,
                DiscMediaType.DvdPlusRw => 0x001A,
                DiscMediaType.DvdRam => 0x0012,
                _ => 0x0000
            };
            drive.DiscStatus = medium.IsBlank ? 0 : medium.IsFinalized ? 2 : 1;
            if (medium.MediaType.IsCd())
            {
                drive.LastLeadOutStart = Msf.FromLba(Math.Max(0, medium.TotalCapacitySectors - 1));
            }
            else
            {
                drive.CapacitySectors = (int)Math.Min(int.MaxValue, medium.TotalCapacitySectors); // beyond CD MSF addresses
            }
            if (medium.MediaType == DiscMediaType.DvdRam)
            {
                drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(drive.CapacitySectors ?? 0, formatted: true);
            }
            if (medium.MediaType == DiscMediaType.DvdPlusRw)
            {
                // A blank virtual DVD+RW is a new, never-formatted disc; one with data has been formatted.
                drive.BackgroundFormatStatus = medium.IsBlank ? 0 : 3;
                drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildDvdPlusRwFormatCapacities(drive.CapacitySectors ?? 0, formatted: !medium.IsBlank);
            }
        }
        return drive;
    }

    public Task<BurnOperationResult> BurnAudioTracksAsync(
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(tracks);
        options ??= new AudioBurnOptions();
        LastLogPath = null;
        LastResult = null;
        LastTestWritePassed = false;

        var saoTracks = new List<SaoTrack>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            int sectors = (int)((t.TotalPcmBytes + OpticalConstants.RawSectorSize - 1) / OpticalConstants.RawSectorSize);
            int silence = i == 0 || options.UseDiscAtOnce
                ? Math.Max(0, t.PreGapFrames)
                : Math.Max(OpticalConstants.CdLeadInFrames, t.PreGapFrames); // Track-at-Once leaves 2-second gaps
            int gapAudio = t.PreGapPcmStreamFactory != null ? Math.Max(0, t.PreGapAudioFrames) : 0;

            // Track 1's layout always starts with two seconds of silence; later tracks get silence, then gap audio.
            int preGap = i == 0 ? Math.Max(silence, OpticalConstants.CdLeadInFrames + gapAudio) : silence + gapAudio;
            saoTracks.Add(new SaoTrack(i + 1, sectors, preGap, t.Isrc, gapAudio, t.PreEmphasis, t.CopyPermitted));
        }

        int? speed = options.Speed is { Multiplier: > 0 } s ? (int)Math.Round(s.Multiplier * 176.4) : null;
        var request = new NativeSaoWriteRequest(
            saoTracks,
            i => OpenTrack(tracks[i]),
            options.WriteCdText && options.CdTextBytes is { Length: > 0 } ? options.CdTextBytes : null,
            options.MediaCatalogNumber,
            TestWrite: options.Simulate,
            WriteSpeedKiloBytesPerSecond: speed);

        return Task.Run(() => Run(recorder, tracks, request, progress, cancellationToken), CancellationToken.None);
    }

    private BurnOperationResult Run(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, NativeSaoWriteRequest request,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        IScsiTransport device;
        Win32ScsiTransport? win32 = null;
        VirtualMmcDrive? virtualDrive = null;
        string devicePath = DevicePathFor(recorder);
        try
        {
            if (recorder is VirtualDiscRecorder virtualRecorder)
            {
                device = virtualDrive = CreateVirtualDrive(virtualRecorder);
                ConfigureVirtualDrive?.Invoke(virtualDrive);
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                {
                    return BurnOperationResult.Failed("Native writing needs Windows.");
                }
                device = win32 = Win32ScsiTransport.Open(devicePath);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return BurnOperationResult.Failed($"Could not open the drive: {ex.Message} Nothing was written to the disc.", ex);
        }

        using var log = new LoggingScsiTransport(device, ownsInner: true);
        string kind = request.TestWrite ? "test write only" : TestWriteFirst ? "test write, then real write" : "real write";
        log.Note($"IgnisDisc native Session-at-Once writer, {kind}. Drive: {recorder.VendorId} {recorder.ProductId} {recorder.ProductRevision} ({(recorder.IsVirtual ? "virtual" : "physical")}).");
        log.Note($"Tracks: {request.Tracks.Count}, CD-Text: {(request.CdTextPacks != null ? $"{request.CdTextPacks.Length / 18} packs" : "none")}.");

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
                return BurnOperationResult.Failed($"{ex.Message} Nothing was written to the disc. Command log: {LastLogPath}", ex);
            }
        }

        NativeSaoWriteResult result;
        try
        {
            var writer = new NativeSaoAudioWriter(log, log.Note);
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
            CommitToVirtualMedium(medium, virtualDrive, tracks, request);
        }

        if (result.Success)
        {
            return BurnOperationResult.Succeeded(result.LeadInSectorsWritten + result.AudioSectorsWritten, result.Elapsed);
        }

        string message = result.StoppedBeforeWriting ? $"{result.Message} Nothing was written to the disc." : result.Message;
        if (LastLogPath != null)
        {
            message += $" Command log: {LastLogPath}";
        }
        return result.Cancelled ? BurnOperationResult.CancelledResult(message) : BurnOperationResult.Failed(message);
    }

    private NativeSaoWriteResult WriteWithTestFirst(NativeSaoAudioWriter writer, NativeSaoWriteRequest request, LoggingScsiTransport log,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        if (request.TestWrite || !TestWriteFirst)
        {
            return writer.Write(request, progress, ct);
        }

        log.Note("Step 1: test write (laser off).");
        var test = writer.Write(request with { TestWrite = true }, Prefixed(progress, "Test write: "), ct);
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
                return new NativeSaoWriteResult
                {
                    Success = false,
                    StoppedBeforeWriting = true,
                    Message = NativeSaoAudioWriter.ReloadMessage,
                    Elapsed = test.Elapsed,
                    CueSheetVariantAccepted = test.CueSheetVariantAccepted,
                    DataBlockTypeAccepted = test.DataBlockTypeAccepted,
                    DiscStillBlankAfterTestWrite = test.DiscStillBlankAfterTestWrite
                };
            }
        }

        log.Note("Step 2: real write.");
        return writer.Write(request, progress, ct);
    }

    private static IProgress<BurnProgressSnapshot>? Prefixed(IProgress<BurnProgressSnapshot>? progress, string prefix) =>
        progress == null
            ? null
            : new InlineProgress(s => progress.Report(new BurnProgressSnapshot
            {
                CurrentLba = s.CurrentLba,
                TotalLba = s.TotalLba,
                ElapsedTime = s.ElapsedTime,
                RemainingTime = s.RemainingTime,
                BufferLevelPercent = s.BufferLevelPercent,
                CurrentSpeedMultiplier = s.CurrentSpeedMultiplier,
                CurrentBytesPerSecond = s.CurrentBytesPerSecond,
                CurrentActionDescription = prefix + s.CurrentActionDescription
            }));

    /// <summary>Reports on the calling thread; <see cref="Progress{T}"/> would post to a captured context.</summary>
    private sealed class InlineProgress(Action<BurnProgressSnapshot> report) : IProgress<BurnProgressSnapshot>
    {
        public void Report(BurnProgressSnapshot value) => report(value);
    }

    /// <summary>
    /// Puts what the simulated drive recorded onto the virtual disc, so the rest of the app sees a burned audio CD:
    /// audio from LBA 0, one track per table-of-contents entry, the CD-Text packs, disc closed.
    /// </summary>
    private static void CommitToVirtualMedium(VirtualDiscMedium medium, VirtualMmcDrive drive, IReadOnlyList<AudioTrackSource> tracks, NativeSaoWriteRequest request)
    {
        medium.Raw2352Mode = true;
        ReadOnlySpan<byte> program = drive.ProgramAreaWritten.GetBuffer().AsSpan(0, (int)drive.ProgramAreaWritten.Length);
        int firstSector = -SaoAudioLayout.FirstWritableLba; // the capture starts at LBA -150
        int sectors = Math.Max(0, program.Length / OpticalConstants.RawSectorSize - firstSector);
        for (int lba = 0; lba < sectors; lba++)
        {
            medium.WriteSector(lba, program.Slice((firstSector + lba) * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize), OpticalConstants.RawSectorSize);
        }

        var session = new VirtualSession { SessionNumber = medium.Sessions.Count + 1, StartLba = 0, SectorCount = sectors };
        var recorded = drive.RecordedTracks;
        for (int i = 0; i < recorded.Count; i++)
        {
            int end = i + 1 < recorded.Count ? recorded[i + 1].Index01Lba : drive.RecordedLeadOutLba;
            var source = i < tracks.Count ? tracks[i] : null;
            session.Tracks.Add(new VirtualTrack
            {
                TrackNumber = recorded[i].TrackNumber,
                StartLba = recorded[i].Index01Lba,
                SectorCount = end - recorded[i].Index01Lba,
                IsAudio = true,
                PreGapFrames = source?.PreGapFrames ?? 0,
                Title = source?.Title,
                Performer = source?.Performer,
                Songwriter = source?.Songwriter,
                Composer = source?.Composer,
                Isrc = source?.Isrc
            });
        }

        medium.Sessions.Add(session);
        medium.CdTextBytes = request.CdTextPacks?.ToArray();
        medium.IsFinalized = true;
    }

    /// <summary>
    /// The drive's device interface path (<c>\\?\…{53f56308-…}</c>) when known. The cdrom driver grants exclusive
    /// access only to a handle opened on the drive itself; a handle opened by drive letter goes through the mounted
    /// file system and the lock fails with ERROR_INVALID_HANDLE.
    /// </summary>
    internal static string DevicePathFor(IDiscRecorderBackend recorder) =>
        recorder.UniqueId.StartsWith(@"\\?\", StringComparison.Ordinal) ? recorder.UniqueId : recorder.VolumePath;

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

    /// <summary>The track's pre-gap audio (if any) followed by its own audio, as one stream.</summary>
    private static Stream OpenTrack(AudioTrackSource track)
    {
        Stream audio = OpenTrackAudio(track);
        if (track.PreGapPcmStreamFactory == null || track.PreGapAudioFrames <= 0)
        {
            return audio;
        }

        try
        {
            return new SequenceStream(track.PreGapPcmStreamFactory(), (long)track.PreGapAudioFrames * OpticalConstants.RawSectorSize, audio);
        }
        catch
        {
            audio.Dispose();
            throw;
        }
    }

    private static Stream OpenTrackAudio(AudioTrackSource track)
    {
        if (track.PcmStreamFactory != null)
        {
            return track.PcmStreamFactory();
        }
        if (track.PcmStream is { CanSeek: true } s)
        {
            s.Position = 0;
            return new NonClosingStream(s);
        }
        if (track.PcmStream != null)
        {
            return new NonClosingStream(track.PcmStream);
        }
        throw new IOException($"Track {track.TrackNumber} has no audio.");
    }

    public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(BurnOperationResult.Failed("Native writing supports audio CDs only."));

    public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(BurnOperationResult.Failed("Native writing supports audio CDs only."));

    public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(RipOperationResult.Failed("Native writing supports audio CDs only."));

    /// <summary>
    /// Exactly <c>firstLength</c> bytes of the first stream (padded with silence if it is shorter), then the second.
    /// </summary>
    private sealed class SequenceStream(Stream first, long firstLength, Stream second) : Stream
    {
        private long _firstRemaining = firstLength;
        private bool _firstEnded;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_firstRemaining > 0)
            {
                Span<byte> part = buffer.Slice(0, (int)Math.Min(buffer.Length, _firstRemaining));
                int n = _firstEnded ? 0 : first.Read(part);
                if (n <= 0)
                {
                    _firstEnded = true;
                    part.Clear();
                    n = part.Length;
                }
                _firstRemaining -= n;
                return n;
            }
            return second.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                first.Dispose();
                second.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;
        public NonClosingStream(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
