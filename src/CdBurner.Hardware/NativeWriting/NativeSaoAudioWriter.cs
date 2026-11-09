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
/// <param name="TestWrite">Laser-off simulation. Must be true: this build does not do real native writes.</param>
public sealed record NativeSaoWriteRequest(
    IReadOnlyList<SaoTrack> Tracks,
    Func<int, Stream> OpenTrackAudio,
    byte[]? CdTextPacks,
    string? MediaCatalogNumber,
    bool TestWrite);

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
}

/// <summary>
/// Writes an audio CD Session-at-Once by sending MMC commands to the drive: mode page 0x05, SEND CUE SHEET, the
/// CD-Text lead-in, the audio from LBA −150, then SYNCHRONIZE CACHE. Follows docs/NATIVE-WRITER-DESIGN.md: every
/// check runs before the first WRITE, and any failure up to that point leaves the disc untouched.
/// </summary>
public sealed class NativeSaoAudioWriter
{
    public const ushort AudioSectorsPerWrite = 27;     // 63,504 bytes, under the common 64 KiB transfer limit
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
        if (!request.TestWrite)
        {
            throw Stop("Real native writes are not enabled in this version. Only test writes (laser off) are allowed.");
        }

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
        var wanted = current.ForSessionAtOnce(request.TestWrite, mastering.BufferUnderrunFree, cdText);
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
        _note($"Write parameters confirmed: SAO, test write {confirmed.TestWrite}, BUFE {confirmed.BufferUnderrunFree}, data block type {confirmed.DataBlockType}.");

        // 4. Cue sheet (with cdrdao's fallback layouts).
        Msf? leadInTime = cdText ? disc.LeadInStart : null;
        state.Variant = SendCueSheet(layout, request.MediaCatalogNumber, leadInTime)
            ?? throw Stop("The drive rejected every cue sheet layout.");
        Check(ct);

        // 5. From here on the drive is writing (with the laser at read power during a test write).
        int leadInSectors = cdText ? -151 - leadInStartLba + 1 : 0;
        int totalSectors = leadInSectors + layout.TotalSectors;
        var reporter = new ProgressReporter(this, progress, clock, totalSectors);

        if (leadInCycle != null)
        {
            WriteLeadIn(leadInCycle, leadInStartLba, leadInSectors, state, reporter, ct);
        }

        WriteProgramArea(request, layout, state, reporter, leadInSectors, ct);

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

        reporter.Report(totalSectors, "Done");
        return new NativeSaoWriteResult
        {
            Success = true,
            Message = request.TestWrite ? "Test write finished; the disc was not written." + extra : "Write finished.",
            LeadInSectorsWritten = state.LeadInSectors,
            AudioSectorsWritten = state.AudioSectors,
            Elapsed = clock.Elapsed,
            CueSheetVariantAccepted = state.Variant,
            DataBlockTypeAccepted = state.BlockType,
            DiscStillBlankAfterTestWrite = stillBlank
        };
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
        const int sectorSize = 2352;
        byte[] buffer = new byte[AudioSectorsPerWrite * sectorSize];
        int lba = SaoAudioLayout.FirstWritableLba;
        int filled = 0;
        int chunkLba = lba;

        void FlushChunk()
        {
            if (filled == 0) return;
            WriteBlocks(chunkLba, (ushort)filled, buffer.AsSpan(0, filled * sectorSize), state);
            state.AudioSectors += filled;
            reporter.Report(doneBefore + state.AudioSectors, "Writing audio");
            chunkLba += filled;
            filled = 0;
        }

        for (int t = 0; t < layout.Tracks.Count; t++)
        {
            var track = layout.Tracks[t];
            for (int i = 0; i < track.PreGapSectors; i++)
            {
                Array.Clear(buffer, filled * sectorSize, sectorSize);
                if (++filled == AudioSectorsPerWrite) { Check(ct, state); FlushChunk(); }
            }

            Stream audio;
            try
            {
                audio = request.OpenTrackAudio(t);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw Stop($"Could not read the audio for track {track.TrackNumber}: {ex.Message}");
            }

            using (audio)
            {
                bool shortStream = false;
                for (int i = 0; i < track.SectorCount; i++)
                {
                    Span<byte> sector = buffer.AsSpan(filled * sectorSize, sectorSize);
                    int got = shortStream ? 0 : ReadFully(audio, sector);
                    if (got < sectorSize)
                    {
                        if (!shortStream) _note($"Track {track.TrackNumber} audio ended early at sector {i}; padding with silence.");
                        shortStream = true;
                        sector.Slice(got).Clear();
                    }
                    if (++filled == AudioSectorsPerWrite) { Check(ct, state); FlushChunk(); }
                }
            }
        }

        FlushChunk();
        if (chunkLba != layout.LeadOutLba)
        {
            throw Stop($"Internal error: wrote up to LBA {chunkLba}, expected {layout.LeadOutLba}.");
        }
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

    internal readonly record struct DiscInformation(int Status, Msf LeadInStart, Msf LastLeadOutStart);

    private DiscInformation? ReadDiscInformation()
    {
        byte[] buffer = new byte[34];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb(34), buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 24)
        {
            return null;
        }

        return new DiscInformation(buffer[2] & 0x03, new Msf(buffer[17], buffer[18], buffer[19]), new Msf(buffer[21], buffer[22], buffer[23]));
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

        byte[] sector = new byte[2352];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadCdCdb(0, 1, expectedSectorType: 1, mainChannelSelectionBits: 0x10),
            sector, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (r.IsSuccess && r.DataTransferLength > 0)
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
        public int LeadInSectors;
        public int AudioSectors;
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
