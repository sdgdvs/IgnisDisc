using System.Buffers.Binary;
using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>What to write.</summary>
/// <param name="Tracks">Track lengths and pre-gaps.</param>
/// <param name="OpenTrackAudio">Opens track <c>i</c>'s audio (16-bit stereo PCM, 2,352 bytes per sector). A short
/// stream is padded with silence; extra bytes are ignored.</param>
/// <param name="CdTextPacks">CD-Text packs (18 bytes each) for the lead-in, or null for none.</param>
/// <param name="MediaCatalogNumber">Optional 13-digit UPC/EAN.</param>
/// <param name="TestWrite">True: laser-off simulation, the disc stays blank. False: the disc is recorded and then
/// read back and compared.</param>
/// <param name="WriteSpeedKiloBytesPerSecond">Write speed to ask the drive for (176 per 1x), or null for the drive's
/// own choice for the disc.</param>
public sealed record NativeSaoWriteRequest(
    IReadOnlyList<SaoTrack> Tracks,
    Func<int, Stream> OpenTrackAudio,
    byte[]? CdTextPacks,
    string? MediaCatalogNumber,
    bool TestWrite,
    int? WriteSpeedKiloBytesPerSecond = null)
{
    /// <summary>Samples to move all the audio later before it is sent (negative: earlier), to correct the drive's write
    /// offset; see <see cref="CdBurner.Core.Audio.WriteOffsetPlan"/>. 0 sends the audio as it is.</summary>
    public int ShiftSamples { get; init; }

    /// <summary>True: leave the disc open for another session (mode page 05h multi-session 11b). The drive writes the
    /// lead-out and a B0h pointer to the next session instead of closing the disc: the first session of a CD-Extra
    /// disc. False (the default) closes the disc.</summary>
    public bool LeaveSessionOpen { get; init; }
}

/// <summary>Outcome of a native Session-at-Once write.</summary>
public sealed record NativeSaoWriteResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>True when the writer stopped before sending the first WRITE command: the disc was not touched.</summary>
    public bool StoppedBeforeWriting { get; init; }

    public string Message { get; init; } = string.Empty;
    public int LeadInSectorsWritten { get; init; }
    public int AudioSectorsWritten { get; init; }
    public TimeSpan Elapsed { get; init; }
    public CueSheetVariant? CueSheetVariantAccepted { get; init; }
    public byte? DataBlockTypeAccepted { get; init; }

    /// <summary>After a test write: whether the drive still reported a blank disc. Null when not checked.</summary>
    public bool? DiscStillBlankAfterTestWrite { get; init; }

    /// <summary>True when a real write sent every sector and the drive closed the session.</summary>
    public bool DiscRecorded { get; init; }

    /// <summary>True when the run stopped only because the drive has no test-write mode for this disc.</summary>
    public bool TestWriteNotOffered { get; init; }

    /// <summary>After a real write: the read-back check. Null after a test write or when writing did not finish.</summary>
    public NativeVerifyResult? Verification { get; init; }

    /// <summary>Average speed the drive took the audio at (program area, from the host side), in audio "x"; 0 when not measured.</summary>
    public double AverageWriteMultiplier { get; init; }

    /// <summary>Mode 1 sectors written for a mixed-mode CD's data track (its pre-gap and post-gap included).</summary>
    public int DataSectorsWritten { get; init; }

    /// <summary>After a real write with <see cref="NativeSaoWriteRequest.LeaveSessionOpen"/>: whether READ DISC
    /// INFORMATION then reported an appendable disc (status 1). Null when not asked for or not checked.</summary>
    public bool? DiscLeftOpen { get; init; }
}

/// <summary>
/// Writes an audio CD Session-at-Once by sending MMC commands to the drive: mode page 0x05, SEND CUE SHEET, the
/// CD-Text lead-in, the audio from LBA −150, then SYNCHRONIZE CACHE. Follows docs/NATIVE-WRITER-DESIGN.md: every
/// check runs before the first WRITE, and any failure up to that point leaves the disc untouched. A real write is
/// always followed by <see cref="NativeSaoVerifier"/>; the result is a success only if that check passes.
/// </summary>
public sealed class NativeSaoAudioWriter
{
    public const ushort AudioSectorsPerWrite = 27;     // 63,504 bytes, under the common 64 KiB transfer limit
    public const ushort DataSectorsPerWrite = 32;      // 65,536 bytes of Mode 1 user data: exactly the 64 KiB limit
    public const int DataSectorSize = 2048;
    public const ushort LeadInSectorsPerWrite = 640;   // 61,440 bytes
    public const ushort ProfileCdR = 0x0009;
    public const ushort ProfileCdRw = 0x000A;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromMinutes(10);

    private readonly IScsiTransport _transport;
    private readonly Action<string> _note;
    private readonly Action<TimeSpan> _sleep;

    /// <param name="transport">The drive (a <see cref="LoggingScsiTransport"/> in normal use).</param>
    /// <param name="note">Receives progress notes for the log.</param>
    /// <param name="sleep">Delay used while waiting for the drive (replaced in tests).</param>
    public NativeSaoAudioWriter(IScsiTransport transport, Action<string>? note = null, Action<TimeSpan>? sleep = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _note = note ?? (_ => { });
        _sleep = sleep ?? Thread.Sleep;
    }

    public NativeSaoWriteResult Write(NativeSaoWriteRequest request, IProgress<BurnProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();
        var state = new RunState();
        try
        {
            return Run(request, progress, cancellationToken, clock, state);
        }
        catch (NativeWriteStop stop)
        {
            _note(stop.Message);
            if (state.WritingStarted)
            {
                TryFlush();
            }
            return new NativeSaoWriteResult
            {
                Success = false,
                Cancelled = stop.Cancelled,
                TestWriteNotOffered = state.TestWriteNotOffered,
                StoppedBeforeWriting = !state.WritingStarted,
                Message = stop.Message,
                LeadInSectorsWritten = state.LeadInSectors,
                AudioSectorsWritten = state.AudioSectors,
                Elapsed = clock.Elapsed,
                CueSheetVariantAccepted = state.Variant,
                DataBlockTypeAccepted = state.BlockType
            };
        }
    }

    private NativeSaoWriteResult Run(NativeSaoWriteRequest request, IProgress<BurnProgressSnapshot>? progress, CancellationToken ct, Stopwatch clock, RunState state)
    {
        SaoAudioLayout layout;
        byte[]? leadInCycle = null;
        try
        {
            layout = SaoAudioLayout.Create(request.Tracks);
            if (request.CdTextPacks is { Length: > 0 } packs)
            {
                leadInCycle = CdTextLeadInEncoder.EncodeCycle(packs);
            }
        }
        catch (ArgumentException ex)
        {
            throw Stop(ex.Message);
        }

        bool cdText = leadInCycle != null;
        if (layout.HasDataTrack && request.ShiftSamples != 0)
        {
            // The shift moves audio across sector boundaries; the data track's sectors cannot move with it.
            _note($"Mixed-mode CD: the write-offset correction ({request.ShiftSamples:+0;-0} samples) is not applied.");
            request = request with { ShiftSamples = 0 };
        }
        Check(ct);

        // 1. Drive ready, disc type, Session-at-Once support.
        WaitUntilReady(TimeSpan.FromSeconds(30), ct, "before writing");
        var mastering = ReadCdMasteringFeature();
        if (mastering.Profile is not (ProfileCdR or ProfileCdRw))
        {
            throw Stop($"The disc in the drive is not a CD-R or CD-RW (current profile 0x{mastering.Profile:X4}).");
        }
        if (!mastering.Present || !mastering.Current || !mastering.SessionAtOnce)
        {
            throw Stop("The drive does not offer Session-at-Once writing for this disc.");
        }
        if (request.TestWrite && !mastering.TestWrite)
        {
            state.TestWriteNotOffered = true;
            throw Stop("The drive does not offer test writes in Session-at-Once mode.");
        }
        if (cdText && !mastering.RwSubchannel)
        {
            throw Stop("The drive cannot write CD-Text (it does not accept R-W sub-channel data in Session-at-Once mode).");
        }

        // 2. Blank disc, lead-in start, capacity.
        var disc = ReadDiscInformation() ?? throw Stop("The drive did not return disc information.");
        if (disc.Status != 0)
        {
            throw Stop("The disc is not blank. Native writing needs a blank CD-R or a blanked CD-RW.");
        }
        int leadInStartLba = 0;
        if (cdText)
        {
            try
            {
                leadInStartLba = CdTextLeadInEncoder.LeadInStartLba(disc.LeadInStart);
            }
            catch (ArgumentException ex)
            {
                throw Stop($"Cannot place the CD-Text lead-in: {ex.Message}");
            }
        }
        if (disc.LastLeadOutStart != default && disc.LastLeadOutStart.Minute < 90 && layout.LeadOutLba > disc.LastLeadOutStart.ToLba())
        {
            throw Stop($"The audio ({layout.TotalSectors:N0} sectors) does not fit on this disc (lead-out must start by {disc.LastLeadOutStart}).");
        }
        _note($"Disc blank, lead-in starts {disc.LeadInStart}, last lead-out start {disc.LastLeadOutStart}, audio ends at LBA {layout.LeadOutLba}.");

        // 3. Write parameters, then read them back.
        var current = ReadWriteParameters() ?? throw Stop("The drive did not return its write parameters (mode page 0x05).");
        var wanted = current.ForSessionAtOnce(request.TestWrite, mastering.BufferUnderrunFree, cdText, request.LeaveSessionOpen);
        if (!SelectWriteParameters(wanted))
        {
            if (!cdText || !SelectWriteParameters(wanted = wanted.WithRawDataBlockType()))
            {
                throw Stop("The drive rejected the Session-at-Once write parameters.");
            }
        }
        state.BlockType = wanted.DataBlockType;

        var confirmed = ReadWriteParameters() ?? throw Stop("The drive did not report its write parameters after they were set.");
        if (confirmed.WriteType != WriteParametersPage.WriteTypeSessionAtOnce)
        {
            throw Stop($"The drive did not switch to Session-at-Once (write type {confirmed.WriteType}).");
        }
        if (confirmed.TestWrite != request.TestWrite)
        {
            throw Stop("The drive did not confirm test-write mode, so nothing was written.");
        }
        byte multiSession = request.LeaveSessionOpen ? WriteParametersPage.MultiSessionNextAllowed : WriteParametersPage.MultiSessionClosed;
        if (confirmed.MultiSession != multiSession)
        {
            throw Stop(request.LeaveSessionOpen
                ? $"The drive did not keep the setting that leaves the disc open for another session (multi-session field {confirmed.MultiSession})."
                : $"The drive did not keep the setting that closes the disc (multi-session field {confirmed.MultiSession}).");
        }
        _note($"Write parameters confirmed: SAO, test write {confirmed.TestWrite}, BUFE {confirmed.BufferUnderrunFree}, data block type {confirmed.DataBlockType}, "
            + (request.LeaveSessionOpen ? "disc left open for another session." : "disc closed afterwards."));

        // 4. Cue sheet (with cdrdao's fallback layouts).
        Msf? leadInTime = cdText ? disc.LeadInStart : null;
        state.Variant = SendCueSheet(layout, request.MediaCatalogNumber, leadInTime)
            ?? throw Stop("The drive rejected every cue sheet layout.");
        Check(ct);

        if (request.WriteSpeedKiloBytesPerSecond is int speed and > 0)
        {
            SetWriteSpeed(speed);
        }

        // 5. From here on the drive is writing (with the laser at read power during a test write).
        int leadInSectors = cdText ? -151 - leadInStartLba + 1 : 0;
        int totalSectors = leadInSectors + layout.TotalSectors;
        var reporter = new ProgressReporter(this, progress, clock, totalSectors);

        if (leadInCycle != null)
        {
            WriteLeadIn(leadInCycle, leadInStartLba, leadInSectors, state, reporter, ct);
        }

        // The drive may round the speed asked for (this one wrote CD-R at 10x when asked for 8x), so log what it did:
        // sectors accepted over time. Its buffer holds about a second of audio, so this is close but not exact.
        var programClock = Stopwatch.StartNew();
        WriteProgramArea(request, layout, state, reporter, leadInSectors, ct);
        double seconds = programClock.Elapsed.TotalSeconds;
        double averageMultiplier = 0;
        if (seconds > 1 && state.AudioSectors > 0)
        {
            averageMultiplier = state.AudioSectors / seconds / 75.0;
            _note($"Audio sent at an average of {averageMultiplier:F1}x ({state.AudioSectors:N0} sectors in {seconds:F0} s).");
        }

        // 6. Flush; the drive writes the lead-out and closes the session itself.
        reporter.Report(totalSectors, "Writing the lead-out");
        Flush("after the audio");
        WaitUntilReady(FlushTimeout, CancellationToken.None, "after writing");
        Flush("after the drive became ready");

        bool? stillBlank = null;
        string extra = string.Empty;
        if (request.TestWrite)
        {
            stillBlank = CheckUnwrittenAfterTestWrite(out extra);
            if (stillBlank == false)
            {
                _note("WARNING: the disc is no longer blank after a test write.");
                return new NativeSaoWriteResult
                {
                    Success = false,
                    Message = "The drive reported a recorded disc after a test write: it did not honour the test-write setting. Do not use native writing with this drive.",
                    LeadInSectorsWritten = state.LeadInSectors,
                    AudioSectorsWritten = state.AudioSectors,
                    Elapsed = clock.Elapsed,
                    CueSheetVariantAccepted = state.Variant,
                    DataBlockTypeAccepted = state.BlockType,
                    DiscStillBlankAfterTestWrite = false
                };
            }
        }

        // 7. A real write is read back and compared. Cancelling is no longer offered: the disc is already recorded.
        NativeVerifyResult? verification = null;
        if (!request.TestWrite)
        {
            _note("Write finished; reading the disc back.");
            verification = new NativeSaoVerifier(_transport, _note, _sleep).Verify(
                layout, request.OpenTrackAudio, request.CdTextPacks,
                (done, total) => reporter.Report(totalSectors, $"Checking the disc ({100.0 * done / Math.Max(1, total):F0}%)"),
                CancellationToken.None,
                request.MediaCatalogNumber,
                request.ShiftSamples);
        }

        // 8. A session left open must leave the disc appendable (status 1), or no second session can follow.
        bool? leftOpen = null;
        if (!request.TestWrite && request.LeaveSessionOpen)
        {
            var after = ReadDiscInformation();
            leftOpen = after?.Status == 1;
            _note(leftOpen == true
                ? "Disc left open for another session (disc status 1)."
                : $"WARNING: the disc was to be left open, but the drive reports disc status {after?.Status.ToString() ?? "unknown"}.");
        }

        reporter.Report(totalSectors, "Done");
        string message = request.TestWrite
            ? "Test write finished; the disc was not written." + extra
            : verification!.Passed
                ? "The disc was written and read back correctly. " + verification.Message
                : "The disc was written, but the check afterwards failed: " + verification.Message;
        if (leftOpen == false)
        {
            message += " The drive closed the disc although it was to stay open, so no further session can be added.";
        }
        return new NativeSaoWriteResult
        {
            Success = (verification?.Passed ?? true) && leftOpen != false,
            Message = message,
            DiscLeftOpen = leftOpen,
            LeadInSectorsWritten = state.LeadInSectors,
            AudioSectorsWritten = state.AudioSectors,
            Elapsed = clock.Elapsed,
            CueSheetVariantAccepted = state.Variant,
            DataBlockTypeAccepted = state.BlockType,
            DiscStillBlankAfterTestWrite = stillBlank,
            DiscRecorded = !request.TestWrite,
            Verification = verification,
            AverageWriteMultiplier = averageMultiplier,
            DataSectorsWritten = state.DataSectors
        };
    }

    /// <summary>SET CD SPEED (0xBB). A drive that refuses keeps its own speed; that is logged, not an error.</summary>
    private void SetWriteSpeed(int kiloBytesPerSecond)
    {
        byte[] cdb = new byte[12];
        cdb[0] = 0xBB;
        cdb[2] = 0xFF; cdb[3] = 0xFF; // read speed: fastest
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(4, 2), (ushort)Math.Min(kiloBytesPerSecond, 0xFFFF));
        var r = _transport.ExecuteCommand(cdb, Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, ShortTimeout);
        _note(r.IsSuccess
            ? $"Asked the drive to write at {kiloBytesPerSecond} kB/s ({kiloBytesPerSecond / 176.4:F0}x); it may round that to a speed it supports."
            : $"The drive did not accept a write speed of {kiloBytesPerSecond} kB/s and keeps its own: {Describe(r)}");
    }

    private void WriteLeadIn(byte[] cycle, int startLba, int count, RunState state, ProgressReporter reporter, CancellationToken ct)
    {
        int cycleSectors = cycle.Length / CdTextLeadInEncoder.SubChannelBytesPerSector;
        byte[] buffer = new byte[LeadInSectorsPerWrite * CdTextLeadInEncoder.SubChannelBytesPerSector];
        int written = 0;
        while (written < count)
        {
            Check(ct, state);
            int n = Math.Min(LeadInSectorsPerWrite, count - written);
            for (int i = 0; i < n; i++)
            {
                int s = (written + i) % cycleSectors;
                Buffer.BlockCopy(cycle, s * 96, buffer, i * 96, 96);
            }

            WriteBlocks(startLba + written, (ushort)n, buffer.AsSpan(0, n * 96), state);
            written += n;
            state.LeadInSectors = written;
            reporter.Report(written, "Writing the CD-Text lead-in");
        }
    }

    private void WriteProgramArea(NativeSaoWriteRequest request, SaoAudioLayout layout, RunState state, ProgressReporter reporter, int doneBefore, CancellationToken ct)
    {
        const int sectorSize = ProgramAreaReader.SectorSize;
        if (layout.HasDataTrack)
        {
            WriteDataTrack(request, layout.Tracks[0], state, reporter, doneBefore, ct);
        }

        byte[] buffer = new byte[AudioSectorsPerWrite * sectorSize];
        int filled = 0;
        int chunkLba = layout.AudioStartLba; // -150, or right after a mixed-mode CD's data track

        void FlushChunk()
        {
            if (filled == 0) return;
            WriteBlocks(chunkLba, (ushort)filled, buffer.AsSpan(0, filled * sectorSize), state);
            state.AudioSectors += filled;
            reporter.Report(doneBefore + state.DataSectors + state.AudioSectors, "Writing audio");
            chunkLba += filled;
            filled = 0;
        }

        using var source = new ProgramAreaReader(layout, request.OpenTrackAudio, _note, request.ShiftSamples);
        while (true)
        {
            bool more;
            try
            {
                more = source.ReadSector(buffer.AsSpan(filled * sectorSize, sectorSize));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw Stop($"Could not read the audio for track {source.CurrentTrackNumber}: {ex.Message}");
            }

            if (!more)
            {
                break;
            }
            if (++filled == AudioSectorsPerWrite)
            {
                Check(ct, state);
                FlushChunk();
            }
        }

        FlushChunk();
        if (chunkLba != layout.LeadOutLba)
        {
            throw Stop($"Internal error: wrote up to LBA {chunkLba}, expected {layout.LeadOutLba}.");
        }
    }

    /// <summary>
    /// A mixed-mode CD's data track: its pre-gap from LBA -150, the image, and the post-gap, all as 2,048-byte Mode 1
    /// sectors (data form 10h: the drive adds sync, header, EDC and ECC). No WRITE reaches past the track, because the
    /// audio after it is sent in 2,352-byte sectors and one WRITE has one block size.
    /// </summary>
    private void WriteDataTrack(NativeSaoWriteRequest request, SaoTrackPlacement track, RunState state, ProgressReporter reporter, int doneBefore, CancellationToken ct)
    {
        int dataSectors = track.SectorCount - track.PostGapSectors;
        byte[] buffer = new byte[DataSectorsPerWrite * DataSectorSize];
        Stream image;
        try
        {
            image = request.OpenTrackAudio(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Stop($"Could not open the data track: {ex.Message}");
        }

        using (image)
        {
            int lba = track.PreGapLba;
            bool imageEnded = false;
            while (lba < track.EndLba)
            {
                Check(ct, state);
                int count = Math.Min(DataSectorsPerWrite, track.EndLba - lba);
                var chunk = buffer.AsSpan(0, count * DataSectorSize);
                chunk.Clear();
                for (int i = 0; i < count; i++)
                {
                    int sector = lba + i;
                    bool holdsImage = sector >= track.Index01Lba && sector < track.Index01Lba + dataSectors;
                    if (!holdsImage || imageEnded)
                    {
                        continue; // pre-gap, post-gap, or padding after a short image: zeros
                    }
                    int got;
                    try
                    {
                        got = ReadFully(image, chunk.Slice(i * DataSectorSize, DataSectorSize));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw Stop($"Could not read the data track: {ex.Message}");
                    }
                    if (got < DataSectorSize)
                    {
                        imageEnded = true;
                        _note($"The data track's image ended at sector {sector - track.Index01Lba}; the rest of the track is zeros.");
                    }
                }

                WriteBlocks(lba, (ushort)count, chunk, state);
                state.DataSectors += count;
                reporter.Report(doneBefore + state.DataSectors, "Writing the data track");
                lba += count;
            }
        }
        _note($"Data track written: {dataSectors:N0} sectors of data, {track.PostGapSectors} of post-gap, from LBA {track.PreGapLba} to {track.EndLba}.");
    }

    private static int ReadFully(Stream s, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = s.Read(destination.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private void WriteBlocks(int lba, ushort count, Span<byte> data, RunState state)
    {
        byte[] cdb = NativeWriteCommands.BuildWrite10(lba, count);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            state.WritingStarted = true;
            var r = _transport.ExecuteCommand(cdb, data, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, WriteTimeout);
            if (r.IsSuccess)
            {
                return;
            }

            // 2/04/08 (long write in progress) and 2/04/07 (operation in progress): the drive buffer is full.
            if (r.SenseInfo is { SenseKey: 0x02, Asc: 0x04, Ascq: 0x07 or 0x08 } && deadline.Elapsed < WriteTimeout)
            {
                _sleep(TimeSpan.FromMilliseconds(20));
                continue;
            }

            throw Stop($"The drive reported an error while writing at LBA {lba}: {Describe(r)}");
        }
    }

    private CueSheetVariant? SendCueSheet(SaoAudioLayout layout, string? mcn, Msf? leadInTime)
    {
        bool hasIsrc = layout.Tracks.Any(t => t.Isrc != null);
        var tried = new HashSet<string>();
        foreach (var variant in new[] { CueSheetVariant.Standard, CueSheetVariant.IsrcWithoutControl, CueSheetVariant.CdTextLeadInWithoutTime,
                                        CueSheetVariant.IsrcWithoutControl | CueSheetVariant.CdTextLeadInWithoutTime })
        {
            if ((variant & CueSheetVariant.IsrcWithoutControl) != 0 && !hasIsrc) continue;
            if ((variant & CueSheetVariant.CdTextLeadInWithoutTime) != 0 && leadInTime == null) continue;

            byte[] sheet = SaoCueSheetBuilder.Build(layout, mcn, leadInTime, variant);
            if (!tried.Add(Convert.ToHexString(sheet))) continue;

            var r = _transport.ExecuteCommand(NativeWriteCommands.BuildSendCueSheet(sheet.Length), sheet, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, WriteTimeout);
            if (r.IsSuccess)
            {
                _note($"Drive accepted cue sheet layout {variant}.");
                return variant;
            }
            _note($"Drive rejected cue sheet layout {variant}: {Describe(r)}");
        }

        return null;
    }

    private bool SelectWriteParameters(WriteParametersPage page)
    {
        byte[] list = page.ToModeSelect10ParameterList();
        var r = _transport.ExecuteCommand(NativeWriteCommands.BuildModeSelect10((ushort)list.Length), list, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, ShortTimeout);
        if (!r.IsSuccess)
        {
            _note($"MODE SELECT with data block type {page.DataBlockType} rejected: {Describe(r)}");
        }
        return r.IsSuccess;
    }

    private WriteParametersPage? ReadWriteParameters()
    {
        byte[] buffer = new byte[255];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildModeSense10Cdb(WriteParametersPage.PageCode, (ushort)buffer.Length, disableBlockDescriptors: true),
            buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        return r.IsSuccess ? WriteParametersPage.FromModeSense10(buffer.AsSpan(0, (int)Math.Min(r.DataTransferLength, (uint)buffer.Length))) : null;
    }

    internal readonly record struct MasteringFeature(ushort Profile, bool Present, bool Current, bool SessionAtOnce, bool TestWrite, bool RwSubchannel, bool BufferUnderrunFree);

    /// <summary>GET CONFIGURATION with RT = 2: the current profile and just the CD Mastering feature (0x002E).</summary>
    private MasteringFeature ReadCdMasteringFeature()
    {
        byte[] cdb = MmcCommandBuilder.BuildGetConfigurationCdb(MmcCommandBuilder.FeatureCdMastering, 64);
        cdb[1] = 0x02; // RT = 2: only the named feature
        byte[] buffer = new byte[64];
        var r = _transport.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 8)
        {
            throw Stop($"The drive did not answer GET CONFIGURATION: {Describe(r)}");
        }

        ushort profile = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6, 2));
        int length = (int)Math.Min(r.DataTransferLength, (uint)buffer.Length);
        if (length < 13 || BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8, 2)) != MmcCommandBuilder.FeatureCdMastering)
        {
            return new MasteringFeature(profile, false, false, false, false, false, false);
        }

        byte flags = buffer[12];
        return new MasteringFeature(profile, true, (buffer[10] & 0x01) != 0,
            SessionAtOnce: (flags & 0x20) != 0, TestWrite: (flags & 0x04) != 0, RwSubchannel: (flags & 0x01) != 0,
            BufferUnderrunFree: (flags & 0x40) != 0);
    }

    internal readonly record struct DiscInformation(int Status, bool Erasable, Msf LeadInStart, Msf LastLeadOutStart);

    private DiscInformation? ReadDiscInformation()
    {
        byte[] buffer = new byte[34];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb(34), buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 24)
        {
            return null;
        }

        return new DiscInformation(buffer[2] & 0x03, (buffer[2] & 0x10) != 0, new Msf(buffer[17], buffer[18], buffer[19]), new Msf(buffer[21], buffer[22], buffer[23]));
    }

    /// <summary>
    /// What to tell the user when the disc cannot be recorded straight after its test write. Measured on an LG
    /// GP60NB50: the drive keeps reporting the simulated session as an open session, and in that state rejects the
    /// real cue sheet (5/30/06). A spindle stop/start, a load command, a sleep/wake cycle and a quick blank of a
    /// CD-RW all leave it unchanged; only reloading the disc clears it.
    /// </summary>
    public const string ReloadMessage =
        "The test write passed. This drive keeps showing the simulated session until the disc is reloaded: "
        + "open and close the tray, then burn again without the test write.";

    /// <summary>True when the drive reports a blank disc. Sends no write command.</summary>
    public bool DiscReadsBlank() => ReadDiscInformation() is { Status: 0 };

    private bool AudioReadableAtLbaZero(out ScsiExecutionResult result)
    {
        byte[] sector = new byte[2352];
        result = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadCdCdb(0, 1, expectedSectorType: 1, mainChannelSelectionBits: 0x10),
            sector, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        return result.IsSuccess && result.DataTransferLength > 0;
    }

    /// <summary>
    /// Decides whether a test write left the disc unwritten. A blank status settles it. Some drives (the LG GP60NB50,
    /// for one) report the simulated session as an open, incomplete session until the disc is reloaded; then the
    /// first audio sector is read back: a real write would return audio there, an unwritten disc cannot be read.
    /// A closed disc ("complete") is what a real Session-at-Once write leaves, so it counts as written.
    /// </summary>
    private bool CheckUnwrittenAfterTestWrite(out string extra)
    {
        extra = string.Empty;
        var after = ReadDiscInformation();
        if (after is null)
        {
            _note("WARNING: no disc information after the test write.");
            return false;
        }
        if (after.Value.Status == 0)
        {
            return true;
        }
        if (after.Value.Status != 1)
        {
            _note($"Disc status {after.Value.Status} after the test write.");
            return false;
        }

        if (AudioReadableAtLbaZero(out var r))
        {
            _note("WARNING: audio could be read back at LBA 0 after the test write.");
            return false;
        }

        _note($"The drive reports the simulated session until the disc is reloaded; LBA 0 is unreadable ({Describe(r)}), so nothing was written.");
        extra = " The drive still shows the simulated session: eject and reload the disc before writing to it.";
        return true;
    }

    private void WaitUntilReady(TimeSpan limit, CancellationToken ct, string when)
    {
        var clock = Stopwatch.StartNew();
        int unitAttentions = 0;
        while (true)
        {
            Check(ct);
            var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, ShortTimeout);
            if (r.IsSuccess)
            {
                return;
            }

            var s = r.SenseInfo;
            if (s is { SenseKey: 0x02, Asc: 0x3A })
            {
                throw Stop("There is no disc in the drive.");
            }
            if (s is { SenseKey: 0x06 } && ++unitAttentions <= 3)
            {
                continue; // media changed / reset reported once after insertion; ask again
            }
            if (s is { SenseKey: 0x02, Asc: 0x04 } && clock.Elapsed < limit)
            {
                _sleep(TimeSpan.FromMilliseconds(500));
                continue;
            }

            throw Stop($"The drive is not ready {when}: {Describe(r)}");
        }
    }

    private void Flush(string when)
    {
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildSynchronizeCacheCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, FlushTimeout);
        if (!r.IsSuccess && r.SenseInfo is not { SenseKey: 0x02, Asc: 0x04 })
        {
            throw Stop($"SYNCHRONIZE CACHE failed {when}: {Describe(r)}");
        }
    }

    private void TryFlush()
    {
        try
        {
            _transport.ExecuteCommand(MmcCommandBuilder.BuildSynchronizeCacheCdb(), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, FlushTimeout);
        }
        catch
        {
            // best effort after a failure
        }
    }

    private double? ReadBufferPercent()
    {
        byte[] buffer = new byte[12];
        var r = _transport.ExecuteCommand(NativeWriteCommands.BuildReadBufferCapacity(), buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || NativeWriteCommands.ParseBufferCapacity(buffer) is not { Length: > 0 } cap)
        {
            return null;
        }
        return Math.Clamp(100.0 * (cap.Length - Math.Min(cap.Blank, cap.Length)) / cap.Length, 0, 100);
    }

    private static void Check(CancellationToken ct, RunState? state = null)
    {
        if (ct.IsCancellationRequested)
        {
            throw new NativeWriteStop(state?.WritingStarted == true ? "Cancelled while writing." : "Cancelled before writing; nothing was written.", cancelled: true);
        }
    }

    private static string Describe(ScsiExecutionResult r) =>
        r.SenseInfo?.ToString() ?? r.ErrorMessage ?? $"status 0x{r.ScsiStatus:X2}";

    private static NativeWriteStop Stop(string message) => new(message, cancelled: false);

    private sealed class RunState
    {
        public bool WritingStarted;
        public bool TestWriteNotOffered;
        public int LeadInSectors;
        public int AudioSectors;
        public int DataSectors;
        public CueSheetVariant? Variant;
        public byte? BlockType;
    }

    private sealed class NativeWriteStop : Exception
    {
        public NativeWriteStop(string message, bool cancelled) : base(message) => Cancelled = cancelled;
        public bool Cancelled { get; }
    }

    private sealed class ProgressReporter
    {
        private readonly NativeSaoAudioWriter _writer;
        private readonly IProgress<BurnProgressSnapshot>? _progress;
        private readonly Stopwatch _clock;
        private readonly int _total;
        private TimeSpan? _lastBufferPoll;
        private double _buffer;

        public ProgressReporter(NativeSaoAudioWriter writer, IProgress<BurnProgressSnapshot>? progress, Stopwatch clock, int total)
        {
            _writer = writer;
            _progress = progress;
            _clock = clock;
            _total = total;
        }

        public void Report(int done, string action)
        {
            if (_lastBufferPoll is null || _clock.Elapsed - _lastBufferPoll.Value >= TimeSpan.FromSeconds(1))
            {
                _lastBufferPoll = _clock.Elapsed;
                _buffer = _writer.ReadBufferPercent() ?? _buffer;
            }

            if (_progress == null) return;
            double seconds = _clock.Elapsed.TotalSeconds;
            double rate = seconds > 0 ? done / seconds : 0;
            _progress.Report(new BurnProgressSnapshot
            {
                CurrentLba = done,
                TotalLba = _total,
                ElapsedTime = _clock.Elapsed,
                RemainingTime = rate > 0 ? TimeSpan.FromSeconds((_total - done) / rate) : TimeSpan.Zero,
                BufferLevelPercent = _buffer,
                CurrentSpeedMultiplier = rate / Msf.FramesPerSecond,
                CurrentBytesPerSecond = (long)(rate * 2352),
                CurrentActionDescription = action
            });
        }
    }
}
