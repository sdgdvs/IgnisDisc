using System.Buffers.Binary;
using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// The DVD half of the direct data writer: one data track on a blank single-layer DVD-R, DVD-RW (sequential) or DVD+R.
/// The command sequences follow libburn's cookbook (libburnia, doc/cookbook.txt):
/// <list type="bullet">
/// <item>DVD-R/-RW, disc closed: Disc-At-Once. Mode page 0x05 with write type 2, track mode 5, data block type 8;
/// RESERVE TRACK of the exact size; WRITE (10); SYNCHRONIZE CACHE. The drive closes the disc itself.</item>
/// <item>DVD-R/-RW, left open: incremental, fixed packets of 16 blocks (write type 0, FP 1, link and packet size 16,
/// multi-session 11b); WRITE (10); SYNCHRONIZE CACHE; CLOSE TRACK; CLOSE SESSION.</item>
/// <item>DVD+R: no mode page; WRITE (10); SYNCHRONIZE CACHE; CLOSE TRACK; then CLOSE SESSION (left open) or the
/// finalize function 101b (disc closed), falling back to CLOSE SESSION if the drive refuses to finalize.</item>
/// </list>
/// DVD+RW, DVD-RW in restricted overwrite mode and DVD-RAM have their own path (NativeTaoDataWriter.Overwritable.cs). Data goes out in whole 32 KiB ECC blocks, two per WRITE (one if the adapter refuses 64 KiB); the last block is padded
/// with zeros. Speed is set
/// with SET STREAMING from the drive's GET PERFORMANCE list. Only DVD-R/-RW have a test write. Dual-layer discs are left
/// to the Windows writer until IgnisDisc sets the layer break itself.
/// </summary>
public sealed partial class NativeTaoDataWriter
{
    public const ushort ProfileDvdMinusR = 0x0011;
    public const ushort ProfileDvdMinusRwSequential = 0x0014;
    public const ushort ProfileDvdMinusRDualLayer = 0x0015;
    public const ushort ProfileDvdMinusRDualLayerJump = 0x0016;
    public const ushort ProfileDvdPlusR = 0x001B;
    public const ushort ProfileDvdPlusRDualLayer = 0x002B;

    /// <summary>GET CONFIGURATION feature 0x0021, Incremental Streaming Writable.</summary>
    public const ushort FeatureIncrementalStreaming = 0x0021;

    /// <summary>
    /// Sectors per WRITE on DVD: two 32 KiB ECC blocks (64 KiB, the most the GP60NB50's USB bridge passes), which
    /// halves the number of commands. It does not change the speed: on that drive a WRITE takes as long as the disc
    /// needs to take its data (6.3 ms per 32 KiB, 12.7 ms per 64 KiB, about 5 MB/s at the start of an "8x" DVD-R,
    /// rising with the radius; measured 2026-11-11). An adapter that refuses 64 KiB gets
    /// <see cref="DvdMinimumSectorsPerWrite"/>.
    /// </summary>
    public const ushort DvdSectorsPerWrite = 32;

    /// <summary>One 32 KiB ECC block, as libburn and growisofs send.</summary>
    public const ushort DvdMinimumSectorsPerWrite = 16;

    /// <summary>Sectors a second at DVD 1x (1,385,000 bytes / 2,048).</summary>
    private const double DvdSectorsPerSecondAt1x = 1_385_000.0 / SectorSize;

    private enum DvdWriteMode { DiscAtOnce, Incremental, PlusR }

    /// <summary>Every DVD profile (0x0010–0x001F, DVD+RW DL 0x002A, DVD+R DL 0x002B).</summary>
    internal static bool IsDvdProfile(ushort profile) => profile is (>= 0x0010 and <= 0x001F) or 0x002A or ProfileDvdPlusRDualLayer;

    private static string DvdProfileName(ushort profile) => profile switch
    {
        0x0010 => "a DVD-ROM",
        ProfileDvdMinusR => "a DVD-R",
        ProfileDvdRam => "a DVD-RAM",
        ProfileDvdMinusRwRestricted => "a DVD-RW in restricted overwrite mode",
        ProfileDvdMinusRwSequential => "a DVD-RW",
        ProfileDvdMinusRDualLayer or ProfileDvdMinusRDualLayerJump => "a DVD-R DL",
        ProfileDvdPlusRw => "a DVD+RW",
        ProfileDvdPlusR => "a DVD+R",
        0x002A => "a DVD+RW DL",
        ProfileDvdPlusRDualLayer => "a DVD+R DL",
        _ => $"a DVD with profile 0x{profile:X4}"
    };

    private NativeDataWriteResult RunDvd(NativeDataWriteRequest request, ushort profile, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct, Stopwatch clock, RunState state)
    {
        state.IsDvd = true;
        string discName = DvdProfileName(profile);
        if (OverwritableKindOf(profile) is OverwritableKind overwritable)
        {
            return RunOverwritable(request, overwritable, progress, ct, clock, state);
        }
        if (profile is ProfileDvdMinusRDualLayer or ProfileDvdMinusRDualLayerJump)
        {
            state.UnsupportedDisc = true;
            throw Stop($"Direct writing covers dual-layer DVD+R but not dual-layer DVD-R yet (the disc is {discName}).");
        }
        if (profile is not (ProfileDvdMinusR or ProfileDvdMinusRwSequential or ProfileDvdPlusR or ProfileDvdPlusRDualLayer))
        {
            state.UnsupportedDisc = true;
            throw Stop($"Direct writing of DVDs covers DVD-R, DVD-RW, DVD+R, DVD+RW and DVD-RAM so far; the disc in the drive is {discName}.");
        }

        bool dualLayer = profile == ProfileDvdPlusRDualLayer;
        bool plusR = profile is ProfileDvdPlusR or ProfileDvdPlusRDualLayer;
        var mode = plusR ? DvdWriteMode.PlusR : request.CloseDisc ? DvdWriteMode.DiscAtOnce : DvdWriteMode.Incremental;
        string kind = dualLayer ? "DVD+R DL" : plusR ? "DVD+R" : profile == ProfileDvdMinusR ? "DVD-R" : "DVD-RW";
        state.WriteMode = mode switch
        {
            DvdWriteMode.DiscAtOnce => $"disc-at-once ({kind})",
            DvdWriteMode.Incremental => $"incremental ({kind})",
            _ => $"sequential ({kind})"
        };
        int trackSectors = (state.ImageSectors + WriteParametersPage.DvdPacketBlocks - 1) / WriteParametersPage.DvdPacketBlocks * WriteParametersPage.DvdPacketBlocks;
        _note($"Disc: {discName}; write mode {state.WriteMode}.");

        // 1. What the drive offers for this disc.
        bool bufferUnderrunFree = false;
        if (plusR)
        {
            if (request.TestWrite)
            {
                state.TestWriteNotOffered = true;
                throw Stop("DVD+R discs have no test-write mode.");
            }
        }
        else
        {
            var dvdWrite = ReadFeature(MmcCommandBuilder.FeatureDvdRecordableWrite);
            if (!dvdWrite.Current)
            {
                throw Stop("The drive does not offer DVD-R writing for this disc (feature 0x002F is not current).");
            }
            if (request.TestWrite && (dvdWrite.Flags & 0x04) == 0)
            {
                state.TestWriteNotOffered = true;
                throw Stop("The drive does not offer test writes on this DVD.");
            }
            bufferUnderrunFree = (dvdWrite.Flags & 0x40) != 0;
            if (mode == DvdWriteMode.Incremental && !ReadFeature(FeatureIncrementalStreaming).Current)
            {
                throw Stop("The drive does not offer incremental writing for this disc, which leaving the disc open needs.");
            }
        }

        // 2. Blank disc; where writing starts and how much fits.
        var disc = ReadDiscInformation() ?? throw Stop("The drive did not return disc information.");
        if (disc.Status != 0)
        {
            throw Stop($"The disc is not blank. Direct writing needs a blank {kind}.");
        }
        if (ReadTrackInformation(1) is not { NextWritableAddressValid: true } track)
        {
            throw Stop("The drive did not report where writing starts on this disc.");
        }
        if (track.NextWritableAddress != 0)
        {
            throw Stop($"The blank disc's first writable sector is {track.NextWritableAddress}, not 0; the image would not be readable.");
        }
        int startLba = 0;
        long freeSectors = track.FreeBlocks;
        if (trackSectors > freeSectors)
        {
            throw Stop($"The image ({state.ImageSectors:N0} sectors) does not fit on this disc ({freeSectors:N0} sectors free).");
        }
        _note($"Disc blank; writing starts at LBA {startLba}; {state.ImageSectors} image sectors" +
              (trackSectors > state.ImageSectors ? $" + {trackSectors - state.ImageSectors} padding to a whole 32 KiB block" : "") + $"; {freeSectors} free.");

        // 3. Write parameters (DVD-R/-RW only: libburn's cookbook says no mode page is sent for DVD+R), read back.
        if (!plusR)
        {
            var current = ReadWriteParameters() ?? throw Stop("The drive did not return its write parameters (mode page 0x05).");
            byte trackMode = track.TrackMode != 0 ? track.TrackMode : WriteParametersPage.TrackModeDvd;
            var wanted = mode == DvdWriteMode.DiscAtOnce
                ? current.ForDvdDiscAtOnce(request.TestWrite, bufferUnderrunFree)
                : current.ForDvdIncremental(request.TestWrite, bufferUnderrunFree, request.CloseDisc, trackMode);
            if (!SelectWriteParameters(wanted))
            {
                throw Stop($"The drive rejected the {state.WriteMode} write parameters.");
            }
            var confirmed = ReadWriteParameters() ?? throw Stop("The drive did not report its write parameters after they were set.");
            byte wantedType = mode == DvdWriteMode.DiscAtOnce ? WriteParametersPage.WriteTypeSessionAtOnce : WriteParametersPage.WriteTypeIncremental;
            if (confirmed.WriteType != wantedType || confirmed.DataBlockType != WriteParametersPage.DataBlockTypeMode1)
            {
                throw Stop($"The drive did not switch to {state.WriteMode} (write type {confirmed.WriteType}, data block type {confirmed.DataBlockType}).");
            }
            if (mode == DvdWriteMode.Incremental && (!confirmed.FixedPackets || confirmed.PacketSize != WriteParametersPage.DvdPacketBlocks))
            {
                throw Stop($"The drive did not confirm fixed packets of 16 blocks (packet size {confirmed.PacketSize}).");
            }
            if (confirmed.TestWrite != request.TestWrite)
            {
                throw Stop("The drive did not confirm test-write mode, so nothing was written.");
            }
            _note($"Write parameters confirmed: {state.WriteMode}, test write {confirmed.TestWrite}, BUFE {confirmed.BufferUnderrunFree}, multi-session {confirmed.MultiSession}.");
        }

        // 3b. Dual-layer DVD+R: where layer 0 ends. Set before the first WRITE; a refusal stops with nothing written.
        if (dualLayer)
        {
            ApplyLayerBreak(trackSectors, request.LayerBreakSectors, state);
        }

        // 4. Speed from the drive's own list, set with SET STREAMING over the whole disc.
        int endLba = (int)Math.Min(int.MaxValue, Math.Max(startLba + trackSectors, startLba + freeSectors) - 1);
        ChooseAndSetSpeed(request.WriteSpeedKiloBytesPerSecond, state, streamingEndLba: endLba);
        Check(ct, state);

        // 5. Disc-At-Once announces the track's size first. The reservation is recorded on the disc, so from here on
        //    the disc is no longer untouched.
        if (mode == DvdWriteMode.DiscAtOnce)
        {
            var reserve = _transport.ExecuteCommand(NativeWriteCommands.BuildReserveTrack(trackSectors), Span<byte>.Empty,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, ShortTimeout);
            if (!reserve.IsSuccess)
            {
                throw Stop($"The drive refused to reserve {trackSectors:N0} sectors for the track: {Describe(reserve)}");
            }
            state.WritingStarted = true;
            _note($"Reserved {trackSectors:N0} sectors for the track.");
        }

        // 6. The data, in whole 32 KiB blocks.
        request.Image.Position = request.StartPosition;
        var reporter = new ProgressReporter(this, progress, clock, trackSectors, DvdSectorsPerSecondAt1x);
        var writing = Stopwatch.StartNew();
        WriteTrack(request, startLba, trackSectors, DvdSectorsPerWrite, state, reporter, ct, DvdMinimumSectorsPerWrite);
        writing.Stop();

        reporter.Report(trackSectors, "Finishing the track");
        Flush("after the data");
        WaitUntilReady(FlushTimeout, CancellationToken.None, state, "after writing");

        if (request.TestWrite)
        {
            bool stillBlank = CheckUnwrittenAfterTestWrite(out string extra);
            reporter.Report(trackSectors, "Done");
            return DvdResult(state, clock, writing, trackSectors) with
            {
                Success = stillBlank,
                Message = stillBlank
                    ? "Test write finished; the disc was not written." + extra
                    : "The drive reported a recorded disc after a test write: it did not honour the test-write setting. Do not use direct writing with this drive.",
                DiscStillBlankAfterTestWrite = stillBlank
            };
        }

        // 7. Close. Cancelling is no longer offered.
        string closing;
        if (mode == DvdWriteMode.DiscAtOnce)
        {
            _note("Disc-At-Once: the drive closed the disc after SYNCHRONIZE CACHE; no CLOSE command is sent.");
            closing = "The data was written Disc-At-Once and the disc closed.";
        }
        else
        {
            reporter.Report(trackSectors, "Closing the track");
            int trackNumber = ReadLastTrackNumber() ?? 1;
            var closeTrack = Close(NativeWriteCommands.CloseFunctionTrack, trackNumber, state);
            if (!closeTrack.IsSuccess)
            {
                throw Stop($"The data was written, but the drive could not close the track: {Describe(closeTrack)}. The disc may not be readable.");
            }

            reporter.Report(trackSectors, request.CloseDisc ? "Closing the disc" : "Closing the session");
            closing = request.CloseDisc ? "The data was written and the disc closed." : "The data was written and the session closed.";
            bool closed = false;
            if (plusR && request.CloseDisc)
            {
                var finalize = Close(NativeWriteCommands.CloseFunctionFinalizeDvdPlusR, 0, state);
                closed = finalize.IsSuccess;
                if (!closed)
                {
                    _note($"The drive refused to finalize the DVD+R ({Describe(finalize)}); closing the session instead.");
                    closing = "The data was written and the session closed; the drive would not finalize the disc, so more could still be added to it.";
                }
            }
            if (!closed)
            {
                var closeSession = Close(NativeWriteCommands.CloseFunctionSession, 0, state);
                if (!closeSession.IsSuccess)
                {
                    throw Stop($"The data was written, but the drive could not close the session: {Describe(closeSession)}. The disc may not be readable.");
                }
            }
        }

        // 8. A quick look at what the drive now reports.
        string tocNote = CheckTableOfContents(startLba, state.ImageSectors);
        reporter.Report(trackSectors, "Done");
        return DvdResult(state, clock, writing, trackSectors) with
        {
            Success = true,
            Message = closing + " " + tocNote,
            DiscRecorded = true
        };
    }

    private NativeDataWriteResult DvdResult(RunState state, Stopwatch clock, Stopwatch writing, int trackSectors) => new()
    {
        DataSectorsWritten = state.ImageSectors,
        PaddingSectors = trackSectors - state.ImageSectors,
        Elapsed = clock.Elapsed,
        WritingTime = writing.Elapsed,
        SpeedSetKiloBytesPerSecond = state.SpeedSet,
        DriveSpeedsKiloBytesPerSecond = state.DriveSpeeds,
        IsDvd = !state.IsBluRay,
        IsBluRay = state.IsBluRay,
        Layer0Sectors = state.Layer0Sectors,
        WriteMode = state.WriteMode
    };

    /// <summary>
    /// CLOSE TRACK/SESSION with IMMED, then waits until the drive is ready again. A drive that is still busy
    /// (2/04/xx, seen on some drives at CLOSE SESSION) is waited out and asked again, up to three times.
    /// </summary>
    private ScsiExecutionResult Close(byte function, int trackNumber, RunState state)
    {
        byte[] cdb = NativeWriteCommands.BuildCloseTrackSession(function, (ushort)Math.Clamp(trackNumber, 0, ushort.MaxValue));
        for (int attempt = 1; ; attempt++)
        {
            var r = _transport.ExecuteCommand(cdb, Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, FlushTimeout);
            if (r.IsSuccess)
            {
                WaitUntilReady(FlushTimeout, CancellationToken.None, state, "while closing");
                return r;
            }
            if (r.SenseInfo is { SenseKey: 0x02, Asc: 0x04 } && attempt < 3)
            {
                _note($"Drive busy at close function {function} ({Describe(r)}); waiting and asking again.");
                WaitUntilReady(FlushTimeout, CancellationToken.None, state, "before closing");
                continue;
            }
            return r;
        }
    }

    /// <summary>
    /// Sets the layer break of a dual-layer DVD+R, the way growisofs does: READ DISC STRUCTURE format 20h gives the
    /// current layer 0 capacity and whether it is fixed already. An image that fits on layer 0 as it is leaves it alone.
    /// Otherwise layer 0 gets half the image rounded up to 16 sectors (so it is never smaller than layer 1), or the
    /// size asked for, sent with SEND DISC STRUCTURE format 20h and read back. Some drives answer with an error yet
    /// set the break (cdwrite list, 2006), so the read-back decides.
    /// </summary>
    private void ApplyLayerBreak(int trackSectors, int? requested, RunState state)
    {
        var boundary = ReadLayerBoundary() ?? throw Stop("The drive did not report the layer boundary of this dual-layer disc.");
        state.Layer0Sectors = boundary.Layer0Sectors;
        _note($"Layer 0 capacity {boundary.Layer0Sectors} sectors{(boundary.Fixed ? ", fixed already" : "")}; image {trackSectors} sectors.");

        int wanted;
        if (requested is int asked)
        {
            if (asked <= 0 || asked % WriteParametersPage.DvdPacketBlocks != 0)
            {
                throw Stop($"The layer break must be a positive multiple of 16 sectors; {asked} is not.");
            }
            if (asked > boundary.Layer0Sectors)
            {
                throw Stop($"The layer break ({asked:N0} sectors) is beyond the end of layer 0 ({boundary.Layer0Sectors:N0} sectors).");
            }
            if (2L * asked < trackSectors)
            {
                throw Stop($"The layer break ({asked:N0} sectors) would put more on layer 1 than on layer 0; it must be at least half the image ({(trackSectors + 1) / 2:N0} sectors).");
            }
            wanted = asked;
        }
        else if (trackSectors <= boundary.Layer0Sectors)
        {
            _note("The image fits on layer 0; the layer break is left as it is.");
            return;
        }
        else
        {
            wanted = (trackSectors / WriteParametersPage.DvdPacketBlocks + 1) / 2 * WriteParametersPage.DvdPacketBlocks;
        }

        if (wanted == boundary.Layer0Sectors)
        {
            _note($"Layer break already at {wanted} sectors.");
            return;
        }
        if (boundary.Fixed)
        {
            throw Stop($"The layer break of this disc is fixed at {boundary.Layer0Sectors:N0} sectors and cannot be moved to {wanted:N0}.");
        }

        byte[] parameters = NativeWriteCommands.BuildLayerBoundaryParameters(wanted);
        var send = _transport.ExecuteCommand(NativeWriteCommands.BuildSendDiscStructure(NativeWriteCommands.FormatLayerBoundary, (ushort)parameters.Length),
            parameters, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromMinutes(3)); // growisofs: some drives calibrate here
        var after = ReadLayerBoundary();
        if (after?.Layer0Sectors != wanted)
        {
            throw Stop($"The drive did not set the layer break to {wanted:N0} sectors: {(send.IsSuccess ? "it reports " + (after?.Layer0Sectors.ToString("N0") ?? "nothing") : Describe(send))}.");
        }
        state.Layer0Sectors = wanted;
        _note(send.IsSuccess
            ? $"Layer break set: {wanted} sectors on layer 0, {trackSectors - wanted} on layer 1."
            : $"The drive answered {Describe(send)} but reports the layer break at {wanted} sectors; going on.");
    }

    private (bool Fixed, int Layer0Sectors)? ReadLayerBoundary()
    {
        byte[] reply = new byte[NativeWriteCommands.LayerBoundaryLength];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadDiscStructureCdb(NativeWriteCommands.FormatLayerBoundary, (ushort)reply.Length),
            reply, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        return r.IsSuccess ? NativeWriteCommands.ParseLayerBoundary(reply) : null;
    }

    internal readonly record struct FeatureState(bool Present, bool Current, byte Flags);

    /// <summary>GET CONFIGURATION with RT = 2 for one feature: whether it is there, current, and its first flag byte.</summary>
    private FeatureState ReadFeature(ushort featureCode)
    {
        byte[] cdb = MmcCommandBuilder.BuildGetConfigurationCdb(featureCode, 64);
        cdb[1] = 0x02;
        byte[] buffer = new byte[64];
        var r = _transport.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        int length = (int)Math.Min(r.DataTransferLength, (uint)buffer.Length);
        if (!r.IsSuccess || length < 12 || BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8, 2)) != featureCode)
        {
            return new FeatureState(false, false, 0);
        }
        return new FeatureState(true, (buffer[10] & 0x01) != 0, length > 12 ? buffer[12] : (byte)0);
    }

    /// <summary>READ DISC INFORMATION: last track in the last session (byte 6 low, byte 11 high).</summary>
    private int? ReadLastTrackNumber()
    {
        byte[] buffer = new byte[34];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb(34), buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 12)
        {
            return null;
        }
        int number = buffer[11] << 8 | buffer[6];
        return number > 0 ? number : null;
    }
}
