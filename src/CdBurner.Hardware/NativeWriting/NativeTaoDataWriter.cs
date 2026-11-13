using System.Buffers.Binary;
using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>What to write: a 2,048-byte-sector data image.</summary>
/// <param name="Image">The image, read from <paramref name="StartPosition"/>. Must be seekable so a test write and the
/// real write can both read it from the start.</param>
/// <param name="StartPosition">Where the image starts in the stream.</param>
/// <param name="ImageBytes">Bytes of image from <paramref name="StartPosition"/>; the last sector is padded with zeros.</param>
/// <param name="TestWrite">True: laser off, the disc stays blank.</param>
/// <param name="CloseDisc">True: no more sessions can be added afterwards.</param>
/// <param name="WriteSpeedKiloBytesPerSecond">Speed to set in kB/s (176.4 per 1x on CD, 1,385 per 1x on DVD), or null
/// for the fastest the drive lists for this disc.</param>
/// <param name="OverwriteExistingData">True: the user allowed replacing what is on an overwritable disc (DVD+RW). Without
/// it, a DVD+RW that holds a file system is left alone.</param>
/// <param name="LayerBreakSectors">Dual-layer DVD+R: the sectors to put on layer 0 (a multiple of 16, at least half the
/// image). Null: chosen automatically, as growisofs does.</param>
/// <param name="SessionStartLba">Adding a session to a disc that already holds one: the sector the image's file system was
/// laid out to start at (the next writable address the disc reported when it was built). The drive's own next writable
/// address must be the same, or nothing is written. Null or 0: the disc must be blank.</param>
public sealed record NativeDataWriteRequest(
    Stream Image,
    long StartPosition,
    long ImageBytes,
    bool TestWrite,
    bool CloseDisc = true,
    int? WriteSpeedKiloBytesPerSecond = null,
    bool OverwriteExistingData = false,
    int? LayerBreakSectors = null,
    int? SessionStartLba = null)
{
    /// <summary>True when the image is a new session for a disc that already holds one.</summary>
    public bool AppendsSession => SessionStartLba is > 0;
}

/// <summary>Outcome of a native Track-at-Once data write.</summary>
public sealed record NativeDataWriteResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>True when the writer stopped before the first WRITE command: the disc was not touched.</summary>
    public bool StoppedBeforeWriting { get; init; }

    /// <summary>True when the disc is one this writer does not handle yet (the message says which).</summary>
    public bool UnsupportedDisc { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>Image sectors sent (without the padding to the minimum track length).</summary>
    public int DataSectorsWritten { get; init; }

    /// <summary>Zero sectors added after the image: a CD track is at least 300 sectors, a DVD track whole 16-sector blocks.</summary>
    public int PaddingSectors { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Time spent sending the data alone (no lead-in, lead-out or closing), for the speed figure.</summary>
    public TimeSpan WritingTime { get; init; }

    /// <summary>The write speed asked for with SET CD SPEED, in kB/s; null when none was set.</summary>
    public int? SpeedSetKiloBytesPerSecond { get; init; }

    /// <summary>The drive's own write speeds for this disc (GET PERFORMANCE), fastest first, in kB/s.</summary>
    public IReadOnlyList<int> DriveSpeedsKiloBytesPerSecond { get; init; } = Array.Empty<int>();

    /// <summary>True when a real write sent every sector and the session was closed.</summary>
    public bool DiscRecorded { get; init; }

    public bool TestWriteNotOffered { get; init; }
    public bool? DiscStillBlankAfterTestWrite { get; init; }

    /// <summary>True when the disc was a DVD (speeds then count 1,385 kB/s per 1x).</summary>
    public bool IsDvd { get; init; }

    /// <summary>True when the disc was a Blu-ray disc (speeds then count 4,495.5 kB/s per 1x).</summary>
    public bool IsBluRay { get; init; }

    /// <summary>Dual-layer DVD+R: the sectors on layer 0 as the drive reported them before writing; null otherwise.</summary>
    public int? Layer0Sectors { get; init; }

    /// <summary>How the disc was written, for logs and JSON (for example "disc-at-once (DVD-R)").</summary>
    public string WriteMode { get; init; } = "track-at-once, mode 1";

    /// <summary>The drive's kB/s for 1x on this disc: 176.4 on CD (audio units, as drives count), 1,385 on DVD, 4,495.5 on
    /// Blu-ray.</summary>
    public double OneXKiloBytesPerSecond => IsBluRay ? 4495.5 : IsDvd ? 1385.0 : 176.4;

    /// <summary>Average speed while sending the data, as an "x" figure for this kind of disc.</summary>
    public double AverageWriteMultiplier
    {
        get
        {
            double seconds = WritingTime.TotalSeconds;
            if (seconds <= 0) return 0;
            long sectors = DataSectorsWritten + (long)PaddingSectors;
            // CD: data sectors per second over 75, the figure Windows' writer reports; DVD and Blu-ray: bytes over 1x.
            return IsDvd || IsBluRay ? sectors * 2048.0 / seconds / (OneXKiloBytesPerSecond * 1000.0) : sectors / seconds / 75.0;
        }
    }
}

/// <summary>
/// Writes one Mode 1 data track to a blank CD-R or CD-RW Track-at-Once with MMC commands: mode page 0x05 (write
/// type 1, data block type 8), SET CD SPEED from the drive's own list, WRITE (10) of 2,048-byte sectors from the next
/// writable address, SYNCHRONIZE CACHE, then CLOSE SESSION. The same write / close-track / close-session order is what
/// DVD±R uses, so this is the base for those discs too. Like the audio writer, every check runs before the first WRITE
/// and a failure up to there leaves the disc untouched. The sector-by-sector read-back check is done afterwards by
/// <see cref="CdBurner.Core.Burning.DataBurnService"/> (READ (10) with FUA), as for the Windows writer.
/// </summary>
public sealed partial class NativeTaoDataWriter
{
    public const ushort SectorsPerWrite = 31;       // 63,488 bytes, under the common 64 KiB transfer limit
    public const int MinimumTrackSectors = 300;     // a CD track is at least 4 seconds
    public const int RunOutSectors = 2;             // Track-at-Once ends each track with 2 run-out blocks
    public const int SectorSize = 2048;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Windows refuses a pass-through whose transfer is larger than the adapter allows with this code.</summary>
    private const int ErrorInvalidParameter = 87;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromMinutes(10);

    private readonly IScsiTransport _transport;
    private readonly Action<string> _note;
    private readonly Action<TimeSpan> _sleep;

    public NativeTaoDataWriter(IScsiTransport transport, Action<string>? note = null, Action<TimeSpan>? sleep = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _note = note ?? (_ => { });
        _sleep = sleep ?? Thread.Sleep;
    }

    public NativeDataWriteResult Write(NativeDataWriteRequest request, IProgress<BurnProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();
        var state = new RunState();
        try
        {
            return Run(request, progress, cancellationToken, clock, state);
        }
        catch (WriteStop stop)
        {
            _note(stop.Message);
            if (state.WritingStarted)
            {
                TryFlush();
            }
            return new NativeDataWriteResult
            {
                Success = false,
                Cancelled = stop.Cancelled,
                StoppedBeforeWriting = !state.WritingStarted,
                UnsupportedDisc = state.UnsupportedDisc,
                TestWriteNotOffered = state.TestWriteNotOffered,
                Message = stop.Message,
                DataSectorsWritten = Math.Min(state.SectorsSent, state.ImageSectors),
                Elapsed = clock.Elapsed,
                SpeedSetKiloBytesPerSecond = state.SpeedSet,
                DriveSpeedsKiloBytesPerSecond = state.DriveSpeeds,
                IsDvd = state.IsDvd,
                IsBluRay = state.IsBluRay,
                Layer0Sectors = state.Layer0Sectors,
                WriteMode = state.WriteMode
            };
        }
    }

    private NativeDataWriteResult Run(NativeDataWriteRequest request, IProgress<BurnProgressSnapshot>? progress, CancellationToken ct, Stopwatch clock, RunState state)
    {
        if (!request.Image.CanSeek || !request.Image.CanRead)
        {
            throw Stop("The image cannot be read from the start again (not a seekable file).");
        }
        if (request.ImageBytes <= 0)
        {
            throw Stop("The image is empty.");
        }
        if (request.ImageBytes > (long)int.MaxValue * SectorSize)
        {
            throw Stop("The image is too large for a disc.");
        }

        state.ImageSectors = (int)((request.ImageBytes + SectorSize - 1) / SectorSize);
        int trackSectors = Math.Max(state.ImageSectors, MinimumTrackSectors);
        Check(ct, state);

        // 1. Drive ready, disc type, Track-at-Once support. A DVD goes its own way from here (NativeTaoDataWriter.Dvd.cs).
        WaitUntilReady(TimeSpan.FromSeconds(30), ct, state, "before writing");
        var tao = ReadTrackAtOnceFeature();
        if (request.AppendsSession && (IsBluRayProfile(tao.Profile) || OverwritableKindOf(tao.Profile) != null))
        {
            state.UnsupportedDisc = true;
            throw Stop($"Direct writing adds sessions to CD-R, CD-RW, DVD-R, DVD-RW and DVD+R so far (the disc in the drive has profile 0x{tao.Profile:X4}).");
        }
        if (IsDvdProfile(tao.Profile))
        {
            return RunDvd(request, tao.Profile, progress, ct, clock, state);
        }
        if (IsBluRayProfile(tao.Profile))
        {
            return RunBluRay(request, tao.Profile, progress, ct, clock, state);
        }
        if (tao.Profile is not (NativeSaoAudioWriter.ProfileCdR or NativeSaoAudioWriter.ProfileCdRw))
        {
            state.UnsupportedDisc = true;
            throw Stop($"Direct writing of data discs covers CD-R, CD-RW, DVD-R, DVD-RW and DVD+R so far (the disc in the drive has profile 0x{tao.Profile:X4}).");
        }
        if (!tao.Present || !tao.Current)
        {
            throw Stop("The drive does not offer Track-at-Once writing for this disc.");
        }
        if (request.TestWrite && !tao.TestWrite)
        {
            state.TestWriteNotOffered = true;
            throw Stop("The drive does not offer test writes in Track-at-Once mode.");
        }

        // 2. A blank disc, or an open one with room for another session; where writing starts and how much fits.
        var disc = ReadDiscInformation() ?? throw Stop("The drive did not return disc information.");
        int startLba = 0;
        long freeSectors;
        AppendTarget? append = null;
        if (request.AppendsSession)
        {
            append = FindAppendTarget(request, disc, state.ImageSectors);
            startLba = append.Value.StartLba;
            freeSectors = append.Value.FreeSectors;
        }
        else if (disc.Status != 0)
        {
            throw Stop("The disc is not blank. Direct writing needs a blank CD-R or an erased CD-RW.");
        }
        else if (ReadTrackInformation(1) is { NextWritableAddressValid: true } t)
        {
            if (t.NextWritableAddress != 0)
            {
                throw Stop($"The blank disc's first writable sector is {t.NextWritableAddress}, not 0; the image would not be readable.");
            }
            startLba = t.NextWritableAddress;
            freeSectors = t.FreeBlocks;
        }
        else
        {
            // No track information: fall back on the disc's last possible lead-out.
            freeSectors = disc.LastLeadOutStart.Minute < 90 ? disc.LastLeadOutStart.ToLba() : 0;
            _note($"No next writable address from READ TRACK INFORMATION; using the last lead-out start {disc.LastLeadOutStart} ({freeSectors} sectors).");
        }

        if (freeSectors > 0 && trackSectors + RunOutSectors > freeSectors)
        {
            throw Stop($"The image ({state.ImageSectors:N0} sectors) does not fit on this disc ({freeSectors:N0} sectors free).");
        }
        _note($"{(append != null ? $"Adding session {append.Value.TrackNumber}" : "Disc blank")}; writing starts at LBA {startLba}; {state.ImageSectors} image sectors" +
              (trackSectors > state.ImageSectors ? $" + {trackSectors - state.ImageSectors} padding" : "") + $"; {freeSectors} free.");

        // 3. Write parameters, then read them back.
        var current = ReadWriteParameters() ?? throw Stop("The drive did not return its write parameters (mode page 0x05).");
        var wanted = current.ForTrackAtOnceData(request.TestWrite, tao.BufferUnderrunFree, request.CloseDisc);
        if (!SelectWriteParameters(wanted))
        {
            throw Stop("The drive rejected the Track-at-Once data write parameters.");
        }
        var confirmed = ReadWriteParameters() ?? throw Stop("The drive did not report its write parameters after they were set.");
        if (confirmed.WriteType != WriteParametersPage.WriteTypeTrackAtOnce || confirmed.DataBlockType != WriteParametersPage.DataBlockTypeMode1)
        {
            throw Stop($"The drive did not switch to Track-at-Once Mode 1 (write type {confirmed.WriteType}, data block type {confirmed.DataBlockType}).");
        }
        if (confirmed.TestWrite != request.TestWrite)
        {
            throw Stop("The drive did not confirm test-write mode, so nothing was written.");
        }
        _note($"Write parameters confirmed: TAO, Mode 1, test write {confirmed.TestWrite}, BUFE {confirmed.BufferUnderrunFree}, multi-session {confirmed.MultiSession}.");

        // 4. Speed from the drive's own list for this disc, never left to chance.
        ChooseAndSetSpeed(request.WriteSpeedKiloBytesPerSecond, state);
        Check(ct, state);

        // 5. The data. From here on the drive is writing (laser at read power during a test write).
        request.Image.Position = request.StartPosition;
        var reporter = new ProgressReporter(this, progress, clock, trackSectors);
        var writing = Stopwatch.StartNew();
        WriteTrack(request, startLba, trackSectors, SectorsPerWrite, state, reporter, ct);
        writing.Stop();

        // 6. Flush: the drive empties its buffer and writes the run-out.
        reporter.Report(trackSectors, "Finishing the track");
        Flush("after the data");
        WaitUntilReady(FlushTimeout, CancellationToken.None, state, "after writing");

        if (request.TestWrite)
        {
            bool stillBlank = CheckUnwrittenAfterTestWrite(startLba, out string extra);
            reporter.Report(trackSectors, "Done");
            return new NativeDataWriteResult
            {
                Success = stillBlank,
                Message = stillBlank
                    ? "Test write finished; the disc was not written." + extra
                    : "The drive reported a recorded disc after a test write: it did not honour the test-write setting. Do not use direct writing with this drive.",
                DataSectorsWritten = state.ImageSectors,
                PaddingSectors = trackSectors - state.ImageSectors,
                Elapsed = clock.Elapsed,
                WritingTime = writing.Elapsed,
                SpeedSetKiloBytesPerSecond = state.SpeedSet,
                DriveSpeedsKiloBytesPerSecond = state.DriveSpeeds,
                DiscStillBlankAfterTestWrite = stillBlank
            };
        }

        // 7. Close the session: the drive writes the lead-in and lead-out. Cancelling is no longer offered.
        reporter.Report(trackSectors, request.CloseDisc ? "Closing the disc" : "Closing the session");
        var close = _transport.ExecuteCommand(NativeWriteCommands.BuildCloseTrackSession(NativeWriteCommands.CloseFunctionSession),
            Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, FlushTimeout);
        if (!close.IsSuccess)
        {
            throw Stop($"The data was written, but the drive could not close the session: {Describe(close)}. The disc may not be readable.");
        }
        WaitUntilReady(FlushTimeout, CancellationToken.None, state, "while closing the session");

        // 8. A quick look at what the drive now reports: the data track where it was written.
        string tocNote = append is { } added ? CheckAddedSession(added, state.ImageSectors) : CheckTableOfContents(startLba, state.ImageSectors);
        reporter.Report(trackSectors, "Done");
        return new NativeDataWriteResult
        {
            Success = true,
            Message = (append != null ? $"Session {append.Value.TrackNumber} was added at sector {startLba:N0} and the " : "The data track was written and the ")
                + (request.CloseDisc ? "disc closed. " : "session closed. ") + tocNote,
            DataSectorsWritten = state.ImageSectors,
            PaddingSectors = trackSectors - state.ImageSectors,
            Elapsed = clock.Elapsed,
            WritingTime = writing.Elapsed,
            SpeedSetKiloBytesPerSecond = state.SpeedSet,
            DriveSpeedsKiloBytesPerSecond = state.DriveSpeeds,
            DiscRecorded = true
        };
    }

    /// <param name="minimumSectorsPerWrite">When larger than this, a WRITE the adapter refuses as too large (Win32 error
    /// 87, before it reaches the drive) is sent again in halves, down to this size.</param>
    /// <param name="imageBytesAlreadyWritten">Image bytes an earlier part of the same track took (a DVD-R DL track goes on
    /// after its layer jump); the image stream is already past them.</param>
    private void WriteTrack(NativeDataWriteRequest request, int startLba, int trackSectors, int sectorsPerWrite, RunState state,
        ProgressReporter reporter, CancellationToken ct, int? minimumSectorsPerWrite = null, long imageBytesAlreadyWritten = 0)
    {
        int minimum = minimumSectorsPerWrite ?? sectorsPerWrite;
        byte[] buffer = new byte[sectorsPerWrite * SectorSize];
        long remainingImage = request.ImageBytes - imageBytesAlreadyWritten;
        int lba = startLba;
        int end = startLba + trackSectors;
        while (lba < end)
        {
            Check(ct, state);
            int n = Math.Min(sectorsPerWrite, end - lba);
            int bytes = n * SectorSize;
            int fromImage = (int)Math.Min(bytes, Math.Max(0, remainingImage));
            try
            {
                ReadExactly(request.Image, buffer.AsSpan(0, fromImage));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
            {
                throw Stop($"Could not read the image at sector {lba - startLba}: {ex.Message}");
            }
            Array.Clear(buffer, fromImage, bytes - fromImage);
            remainingImage -= fromImage;

            if (!WriteBlocks(lba, (ushort)n, buffer.AsSpan(0, bytes), state, canShrink: sectorsPerWrite > minimum))
            {
                request.Image.Position -= fromImage;
                remainingImage += fromImage;
                sectorsPerWrite = Math.Max(minimum, sectorsPerWrite / 2);
                _note($"The adapter refused {n} sectors in one WRITE; sending {sectorsPerWrite} per WRITE from LBA {lba}.");
                continue;
            }
            lba += n;
            state.SectorsSent += n;
            reporter.Report(state.SectorsSent, "Writing data");
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int n = stream.Read(destination[done..]);
            if (n <= 0)
            {
                throw new EndOfStreamException("The image ended early.");
            }
            done += n;
        }
    }

    /// <returns>False only when <paramref name="canShrink"/> is set and the adapter refused the transfer as too large.</returns>
    private bool WriteBlocks(int lba, ushort count, Span<byte> data, RunState state, bool canShrink = false)
    {
        byte[] cdb = NativeWriteCommands.BuildWrite10(lba, count);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            state.WritingStarted = true;
            var r = _transport.ExecuteCommand(cdb, data, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, WriteTimeout);
            if (r.IsSuccess)
            {
                return true;
            }
            if (canShrink && r.SenseInfo == null && r.Win32Error == ErrorInvalidParameter)
            {
                return false;
            }
            if (r.SenseInfo is { SenseKey: 0x02, Asc: 0x04, Ascq: 0x07 or 0x08 } && deadline.Elapsed < WriteTimeout)
            {
                _sleep(TimeSpan.FromMilliseconds(20)); // drive buffer full
                continue;
            }
            throw Stop($"The drive reported an error while writing at LBA {lba}: {Describe(r)}");
        }
    }

    /// <summary>
    /// Asks the drive which write speeds it offers for this disc and sets the one wanted: the user's choice rounded down
    /// to a listed speed, or the fastest listed. A drive that lists nothing or refuses keeps its own speed (logged).
    /// On CD the speed is set with SET CD SPEED; on DVD (<paramref name="streamingEndLba"/> given) with SET STREAMING,
    /// then SET CD SPEED if the drive refuses that.
    /// </summary>
    private void ChooseAndSetSpeed(int? requested, RunState state, int? streamingEndLba = null, DriveMediaReport? report = null)
    {
        double oneX = state.IsBluRay ? 4495.5 : streamingEndLba.HasValue ? 1385.0 : 176.4;
        report ??= MmcMediaReport.Query(_transport, ShortTimeout);
        state.DriveSpeeds = report.WriteSpeeds.Select(s => s.KilobytesPerSecond).Where(k => k > 0).Distinct().OrderByDescending(k => k).ToList();
        _note(state.DriveSpeeds.Count > 0
            ? $"Drive's write speeds for this disc: {string.Join(", ", state.DriveSpeeds.Select(k => $"{k} kB/s ({k / oneX:F1}x)"))}."
            : "The drive listed no write speeds for this disc.");

        int? speed = requested is > 0 && state.DriveSpeeds.Count > 0
            ? state.DriveSpeeds.Where(k => k <= requested.Value + 1).DefaultIfEmpty(state.DriveSpeeds[^1]).First()
            : requested is > 0 ? requested
            : state.DriveSpeeds.Count > 0 ? state.DriveSpeeds[0] : null;
        if (speed is not int kb)
        {
            _note("No write speed set; the drive uses its own.");
            return;
        }

        if (streamingEndLba is int endLba)
        {
            byte[] descriptor = NativeWriteCommands.BuildPerformanceDescriptor(endLba, kb);
            var streaming = _transport.ExecuteCommand(NativeWriteCommands.BuildSetStreaming(), descriptor, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, ShortTimeout);
            if (streaming.IsSuccess)
            {
                state.SpeedSet = kb;
                _note($"Write speed set to {kb} kB/s ({kb / oneX:F1}x) with SET STREAMING.");
                return;
            }
            _note($"SET STREAMING refused ({Describe(streaming)}); trying SET CD SPEED.");
        }

        var r = _transport.ExecuteCommand(NativeWriteCommands.BuildSetCdSpeed(kb), Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, ShortTimeout);
        if (r.IsSuccess)
        {
            state.SpeedSet = kb;
            _note($"Write speed set to {kb} kB/s ({kb / oneX:F1}x).");
        }
        else
        {
            _note($"The drive did not accept a write speed of {kb} kB/s and keeps its own: {Describe(r)}");
        }
    }

    /// <summary>READ TOC format 0 after closing: track 1 must be a data track at the start LBA, ending after the image.</summary>
    private string CheckTableOfContents(int startLba, int imageSectors)
    {
        byte[] buffer = new byte[804];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x00, 1, (ushort)buffer.Length), buffer,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 20)
        {
            _note($"READ TOC after closing failed: {Describe(r)}");
            return "The drive did not return the table of contents.";
        }

        // Format 0: a 4-byte header (data length in bytes 0-1), then 8-byte descriptors, the lead-out (AAh) last.
        int length = Math.Min(Math.Min(BinaryPrimitives.ReadUInt16BigEndian(buffer) + 2, (int)r.DataTransferLength), buffer.Length);
        int entries = (length - 4) / 8;
        int leadOutLba = -1;
        int found = -1;
        for (int i = 0; i < entries; i++)
        {
            var entry = buffer.AsSpan(4 + 8 * i, 8);
            int lba = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            if (entry[2] == 0xAA)
            {
                leadOutLba = lba;
            }
            else if (found < 0 && (lba == startLba || i == entries - 2))
            {
                found = i; // the track written now, or else the last one listed
            }
        }
        if (found < 0)
        {
            return "The table of contents lists no tracks.";
        }

        var track = buffer.AsSpan(4 + 8 * found, 8);
        bool data = (track[1] & 0x04) != 0;
        int trackStart = BinaryPrimitives.ReadInt32BigEndian(track[4..]);
        _note($"TOC: {entries - 1} track(s); track {track[2]} {(data ? "data" : "audio")} at LBA {trackStart}, lead-out at {leadOutLba}.");
        if (!data || trackStart != startLba || leadOutLba < startLba + imageSectors)
        {
            return $"Unexpected table of contents (track {track[2]} {(data ? "data" : "audio")} at {trackStart}, lead-out {leadOutLba}).";
        }
        return entries == 2
            ? $"Table of contents: one data track, {leadOutLba - trackStart:N0} sectors."
            : $"Table of contents: {entries - 1} tracks, the last a data track of {leadOutLba - trackStart:N0} sectors.";
    }

    /// <summary>Where a new session goes on an open disc: the invisible track's number, start and free space.</summary>
    internal readonly record struct AppendTarget(int TrackNumber, int StartLba, long FreeSectors);

    /// <summary>
    /// Checks, before anything is written, that a new session can go where the image expects it: the disc is open
    /// (status 1) with an empty last session, READ TRACK INFORMATION of the invisible track (number FFh) gives a valid
    /// next writable address, that address is the one the image was laid out for, and the image's own ISO 9660 volume
    /// says so too.
    /// </summary>
    private AppendTarget FindAppendTarget(NativeDataWriteRequest request, DiscInformation disc, int imageSectors)
    {
        int expected = request.SessionStartLba ?? 0;
        if (disc.Status == 0)
        {
            throw Stop($"The disc is blank, but the image was built to follow an earlier session from sector {expected:N0}; on a blank disc its files would not be found.");
        }
        if (disc.Status != 1)
        {
            throw Stop("The disc is closed (not blank): no more sessions can be added to it.");
        }
        if (disc.LastSessionState != 0)
        {
            throw Stop("The disc's last session was left unfinished (an incomplete track), so no session can be added to it.");
        }

        var invisible = ReadTrackInformation(0xFF) ?? throw Stop("The drive did not report where the next session starts.");
        if (!invisible.NextWritableAddressValid || !invisible.Blank)
        {
            throw Stop("The drive reports no writable address for a new session on this disc.");
        }
        if (invisible.NextWritableAddress != expected)
        {
            throw Stop($"The drive's next writable address is sector {invisible.NextWritableAddress:N0}, but the image was built for sector {expected:N0}; written there its files would not be found. Burn again so the image is built for this disc.");
        }
        CheckImageStartsAt(request, expected, imageSectors);
        _note($"Open disc: track {invisible.TrackNumber} (invisible) starts at {invisible.NextWritableAddress}, {invisible.FreeBlocks} sectors free.");
        return new AppendTarget(invisible.TrackNumber, invisible.NextWritableAddress, invisible.FreeBlocks);
    }

    /// <summary>
    /// The image's Primary Volume Descriptor (image sector 16; ECMA-119 8.4) must place the volume at
    /// <paramref name="startLba"/>: its root directory (record at byte 156, extent at byte 158, little-endian half) lies
    /// inside the new session, and the volume space size (byte 80) ends inside it. An image built for sector 0 fails.
    /// </summary>
    private void CheckImageStartsAt(NativeDataWriteRequest request, int startLba, int imageSectors)
    {
        if (imageSectors < 17)
        {
            throw Stop("The image is too small to hold a file system for a new session.");
        }
        byte[] pvd = new byte[SectorSize];
        try
        {
            request.Image.Position = request.StartPosition + 16L * SectorSize;
            ReadExactly(request.Image, pvd);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            throw Stop($"Could not read the image's volume descriptor: {ex.Message}");
        }
        finally
        {
            request.Image.Position = request.StartPosition;
        }

        if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' || pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
        {
            throw Stop("The image has no ISO 9660 volume at sector 16, so it cannot be added as a session.");
        }
        long volumeEnd = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80, 4));
        long root = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2, 4));
        long sessionEnd = (long)startLba + imageSectors;
        if (root < startLba || root >= sessionEnd || volumeEnd <= startLba || volumeEnd > sessionEnd)
        {
            throw Stop($"The image was not built to start at sector {startLba:N0} (its root directory is at sector {root:N0}); written there its files would not be found.");
        }
        _note($"Image volume: root directory at {root}, volume ends at {volumeEnd}: laid out for a session from {startLba}.");
    }

    /// <summary>
    /// After adding a session: READ TRACK INFORMATION of the new track by number (the reliable source; READ TOC was seen
    /// to go stale after an added DVD session until a reload), then the table of contents as a second opinion.
    /// </summary>
    private string CheckAddedSession(AppendTarget added, int imageSectors)
    {
        string trackNote = ReadTrackInformation(added.TrackNumber) is { } t
            ? t.TrackStart == added.StartLba
                ? $"Track {added.TrackNumber} starts at sector {t.TrackStart:N0}."
                : $"The drive reports track {added.TrackNumber} at sector {t.TrackStart:N0}, not {added.StartLba:N0}."
            : $"The drive did not report track {added.TrackNumber}.";
        _note(trackNote);
        return trackNote + " " + CheckTableOfContents(added.StartLba, imageSectors);
    }

    internal readonly record struct TrackAtOnceFeature(ushort Profile, bool Present, bool Current, bool TestWrite, bool BufferUnderrunFree);

    /// <summary>GET CONFIGURATION with RT = 2: the current profile and just the Track at Once feature (0x002D).</summary>
    private TrackAtOnceFeature ReadTrackAtOnceFeature()
    {
        byte[] cdb = MmcCommandBuilder.BuildGetConfigurationCdb(MmcCommandBuilder.FeatureTrackAtOnce, 64);
        cdb[1] = 0x02;
        byte[] buffer = new byte[64];
        var r = _transport.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 8)
        {
            throw Stop($"The drive did not answer GET CONFIGURATION: {Describe(r)}");
        }

        ushort profile = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6, 2));
        int length = (int)Math.Min(r.DataTransferLength, (uint)buffer.Length);
        if (length < 13 || BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8, 2)) != MmcCommandBuilder.FeatureTrackAtOnce)
        {
            return new TrackAtOnceFeature(profile, false, false, false, false);
        }

        byte flags = buffer[12];
        return new TrackAtOnceFeature(profile, true, (buffer[10] & 0x01) != 0, TestWrite: (flags & 0x04) != 0, BufferUnderrunFree: (flags & 0x40) != 0);
    }

    /// <param name="BackgroundFormat">READ DISC INFORMATION byte 7 bits 0–1 (DVD+RW): 0 not formatted, 1 partly
    /// formatted (stopped), 2 formatting, 3 fully formatted.</param>
    /// <param name="LastSessionState">Byte 2 bits 3-2: 0 empty, 1 incomplete, 3 complete. An open disc ready for another
    /// session has an empty last session.</param>
    internal readonly record struct DiscInformation(int Status, Msf LastLeadOutStart, int BackgroundFormat = 0, int LastSessionState = 0);

    private DiscInformation? ReadDiscInformation()
    {
        byte[] buffer = new byte[34];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb(34), buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 24)
        {
            return null;
        }
        return new DiscInformation(buffer[2] & 0x03, new Msf(buffer[21], buffer[22], buffer[23]), buffer[7] & 0x03, (buffer[2] >> 2) & 0x03);
    }

    internal readonly record struct TrackInformation(bool NextWritableAddressValid, int NextWritableAddress, long FreeBlocks, bool Blank,
        byte TrackMode = 0, long TrackSize = 0, int TrackNumber = 0, int TrackStart = 0);

    private TrackInformation? ReadTrackInformation(int trackNumber)
    {
        byte[] buffer = new byte[36];
        var r = _transport.ExecuteCommand(NativeWriteCommands.BuildReadTrackInformation(trackNumber), buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 20)
        {
            _note($"READ TRACK INFORMATION failed: {Describe(r)}");
            return null;
        }
        return new TrackInformation(
            NextWritableAddressValid: (buffer[7] & 0x01) != 0,
            NextWritableAddress: BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(12, 4)),
            FreeBlocks: BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(16, 4)),
            Blank: (buffer[6] & 0x40) != 0,
            TrackMode: (byte)(buffer[5] & 0x0F),
            TrackSize: r.DataTransferLength >= 28 ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(24, 4)) : 0,
            TrackNumber: buffer[2] | (r.DataTransferLength >= 33 ? buffer[32] << 8 : 0),
            TrackStart: BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(8, 4)));
    }

    private bool SelectWriteParameters(WriteParametersPage page)
    {
        byte[] list = page.ToModeSelect10ParameterList();
        var r = _transport.ExecuteCommand(NativeWriteCommands.BuildModeSelect10((ushort)list.Length), list, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, ShortTimeout);
        if (!r.IsSuccess)
        {
            _note($"MODE SELECT rejected: {Describe(r)}");
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

    /// <summary>True when the drive reports a blank disc. Sends no write command.</summary>
    public bool DiscReadsBlank() => ReadDiscInformation() is { Status: 0 };

    /// <summary>
    /// True when the disc is ready for the write <paramref name="request"/> describes: blank, or for a new session open
    /// with its next writable address where the image starts. Sends no write command.
    /// </summary>
    public bool DiscReadyFor(NativeDataWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.AppendsSession)
        {
            return DiscReadsBlank();
        }
        return ReadDiscInformation() is { Status: 1, LastSessionState: 0 }
            && ReadTrackInformation(0xFF) is { NextWritableAddressValid: true, Blank: true } t
            && t.NextWritableAddress == request.SessionStartLba;
    }

    /// <summary>
    /// After a test write: a blank status settles it. A drive that shows the simulated session (status 1) is checked by
    /// reading sector 0: a recorded disc returns it, an unwritten one cannot.
    /// </summary>
    /// <param name="startLba">Where the test write went: 0 on a blank disc, the next session's start on an open one.</param>
    private bool CheckUnwrittenAfterTestWrite(int startLba, out string extra)
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
        if (after.Value.Status is not (1 or 2))
        {
            _note($"Disc status {after.Value.Status} after the test write.");
            return false;
        }

        // The GP60NB50 keeps showing the simulated session until the disc is reloaded: an open session (status 1)
        // after a CD test write, a closed disc (status 2) after a DVD-R Disc-At-Once one (measured 2026-11-11). The
        // sectors decide: a recorded disc returns them, an unwritten one cannot.
        ScsiExecutionResult r = default!;
        foreach (int lba in new[] { startLba, startLba + 16 })
        {
            byte[] sector = new byte[SectorSize];
            r = _transport.ExecuteCommand(MmcCommandBuilder.BuildRead10Cdb(lba, 1, forceUnitAccess: true), sector, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
            if (r.IsSuccess && r.DataTransferLength > 0)
            {
                _note($"WARNING: sector {lba} could be read back after the test write (disc status {after.Value.Status}).");
                return false;
            }
        }
        _note($"The drive reports {(startLba > 0 ? "an open disc" : $"a simulated {(after.Value.Status == 2 ? "closed disc" : "session")} until the disc is reloaded")}; sectors {startLba} and {startLba + 16} are unreadable ({Describe(r)}), so nothing was written.");
        extra = startLba > 0 ? string.Empty : " The drive still shows the simulated session: eject and reload the disc before writing to it.";
        return true;
    }

    private void WaitUntilReady(TimeSpan limit, CancellationToken ct, RunState state, string when, Action<byte[]>? whileNotReady = null)
    {
        var clock = Stopwatch.StartNew();
        int unitAttentions = 0;
        while (true)
        {
            Check(ct, state);
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
                continue;
            }
            if (s is { SenseKey: 0x02, Asc: 0x04 } && clock.Elapsed < limit)
            {
                whileNotReady?.Invoke(r.SenseData);
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
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _note($"SYNCHRONIZE CACHE after the failure also failed: {ex.Message}");
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

    private static void Check(CancellationToken ct, RunState state)
    {
        if (ct.IsCancellationRequested)
        {
            throw new WriteStop(state.WritingStarted
                ? "Cancelled while writing; the disc is incomplete."
                : "Cancelled before writing; nothing was written.", cancelled: true);
        }
    }

    private static string Describe(ScsiExecutionResult r) =>
        r.SenseInfo?.ToString() ?? r.ErrorMessage ?? $"status 0x{r.ScsiStatus:X2}";

    private static WriteStop Stop(string message) => new(message, cancelled: false);

    private sealed class RunState
    {
        public bool WritingStarted;
        public bool UnsupportedDisc;
        public bool TestWriteNotOffered;
        public int ImageSectors;
        public int SectorsSent;
        public int? SpeedSet;
        public IReadOnlyList<int> DriveSpeeds = Array.Empty<int>();
        public bool IsDvd;
        public bool IsBluRay;
        public int? Layer0Sectors;
        public string WriteMode = "track-at-once, mode 1";
    }

    private sealed class WriteStop : Exception
    {
        public WriteStop(string message, bool cancelled) : base(message) => Cancelled = cancelled;
        public bool Cancelled { get; }
    }

    private sealed class ProgressReporter
    {
        private readonly NativeTaoDataWriter _writer;
        private readonly IProgress<BurnProgressSnapshot>? _progress;
        private readonly Stopwatch _clock;
        private readonly int _total;
        private readonly double _sectorsPerSecondAt1x;
        private TimeSpan? _lastBufferPoll;
        private double _buffer;

        /// <param name="sectorsPerSecondAt1x">75 on CD; 1,385,000 / 2,048 on DVD.</param>
        public ProgressReporter(NativeTaoDataWriter writer, IProgress<BurnProgressSnapshot>? progress, Stopwatch clock, int total,
            double sectorsPerSecondAt1x = Msf.FramesPerSecond)
        {
            _writer = writer;
            _progress = progress;
            _clock = clock;
            _total = total;
            _sectorsPerSecondAt1x = sectorsPerSecondAt1x;
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
                CurrentSpeedMultiplier = rate / _sectorsPerSecondAt1x,
                CurrentBytesPerSecond = (long)(rate * SectorSize),
                CurrentActionDescription = action
            });
        }
    }
}
