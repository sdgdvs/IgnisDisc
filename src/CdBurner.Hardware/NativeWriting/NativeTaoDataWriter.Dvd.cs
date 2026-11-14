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
/// with SET STREAMING from the drive's GET PERFORMANCE list. Only DVD-R/-RW have a test write. Dual-layer DVD+R gets its
/// layer break with SEND DISC STRUCTURE 20h (growisofs). Dual-layer DVD-R is written with Layer Jump Recording, only on
/// a drive that reports feature 0x0033 current: write type 4, SEND DISC STRUCTURE 23h with the last layer 0 address,
/// layer 0 written up to it, then the rest from the drive's next writable address on layer 1 (see
/// <see cref="ApplyLayerJump"/>); CLOSE TRACK and CLOSE SESSION close the disc. Any other DVD-R DL case goes to the
/// Windows writer.
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

    /// <summary>GET CONFIGURATION feature 0x0033, Layer Jump Recording (DVD-R DL); current when the disc in the drive
    /// can be written that way (cdrtools scsi_mmc.c lists 0x0033 as "Layer Jump Recording", profile 0x0016 as "DVD-R/DL
    /// layer jump recording").</summary>
    public const ushort FeatureLayerJumpRecording = 0x0033;

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

    private enum DvdWriteMode { DiscAtOnce, Incremental, PlusR, LayerJump }

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
        if (profile is not (ProfileDvdMinusR or ProfileDvdMinusRwSequential or ProfileDvdPlusR or ProfileDvdPlusRDualLayer
            or ProfileDvdMinusRDualLayer or ProfileDvdMinusRDualLayerJump))
        {
            state.UnsupportedDisc = true;
            throw Stop($"Direct writing of DVDs covers DVD-R, DVD-RW, DVD+R, DVD+RW and DVD-RAM so far; the disc in the drive is {discName}.");
        }

        bool layerJump = profile is ProfileDvdMinusRDualLayer or ProfileDvdMinusRDualLayerJump;
        bool dualLayer = profile == ProfileDvdPlusRDualLayer || layerJump;
        bool plusR = profile is ProfileDvdPlusR or ProfileDvdPlusRDualLayer;
        if (request.AppendsSession && dualLayer)
        {
            state.UnsupportedDisc = true;
            throw Stop($"Direct writing adds sessions to single-layer DVDs only so far (the disc is {discName}).");
        }
        if (layerJump)
        {
            // Checked before anything is sent that changes the drive or the disc, so a refusal leaves the burn to the
            // Windows writer with this reason.
            if (!ReadFeature(FeatureLayerJumpRecording).Current)
            {
                state.UnsupportedDisc = true;
                throw Stop("The drive does not offer layer jump recording (feature 0x0033) for this DVD-R DL, which direct writing needs for it.");
            }
            if (!request.CloseDisc)
            {
                state.UnsupportedDisc = true;
                throw Stop("Direct writing closes a DVD-R DL when it writes one; leaving it open is left to the Windows writer.");
            }
        }
        // Disc-At-Once needs a blank disc; a session added to a DVD-R/-RW is written incrementally, and closing the
        // disc then comes from the multi-session field (00b: CLOSE SESSION closes the disc).
        var mode = plusR ? DvdWriteMode.PlusR
            : layerJump ? DvdWriteMode.LayerJump
            : request.CloseDisc && !request.AppendsSession ? DvdWriteMode.DiscAtOnce : DvdWriteMode.Incremental;
        string kind = layerJump ? "DVD-R DL" : dualLayer ? "DVD+R DL" : plusR ? "DVD+R" : profile == ProfileDvdMinusR ? "DVD-R" : "DVD-RW";
        state.WriteMode = mode switch
        {
            DvdWriteMode.DiscAtOnce => $"disc-at-once ({kind})",
            DvdWriteMode.Incremental => $"incremental ({kind})",
            DvdWriteMode.LayerJump => $"layer jump ({kind})",
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
                throw Stop(request.AppendsSession
                    ? "The drive does not offer incremental writing for this disc, which adding a session needs."
                    : "The drive does not offer incremental writing for this disc, which leaving the disc open needs.");
            }
        }

        // 2. A blank disc, or an open one with room for another session; where writing starts and how much fits.
        var disc = ReadDiscInformation() ?? throw Stop("The drive did not return disc information.");
        AppendTarget? append = null;
        if (request.AppendsSession)
        {
            append = FindAppendTarget(request, disc, state.ImageSectors);
        }
        else if (disc.Status != 0)
        {
            throw Stop($"The disc is not blank. Direct writing needs a blank {kind}.");
        }
        if (ReadTrackInformation(append?.TrackNumber ?? 1) is not { NextWritableAddressValid: true } track)
        {
            throw Stop("The drive did not report where writing starts on this disc.");
        }
        if (track.NextWritableAddress != (append?.StartLba ?? 0))
        {
            throw Stop(append != null
                ? $"The drive gave two different next writable addresses ({append.Value.StartLba} and {track.NextWritableAddress}); nothing was written."
                : $"The blank disc's first writable sector is {track.NextWritableAddress}, not 0; the image would not be readable.");
        }
        int startLba = track.NextWritableAddress;
        if (startLba % WriteParametersPage.DvdPacketBlocks != 0)
        {
            throw Stop($"The next writable address {startLba:N0} is not on a 16-sector ECC block boundary; nothing was written.");
        }
        long freeSectors = track.FreeBlocks;
        if (trackSectors > freeSectors)
        {
            throw Stop($"The image ({state.ImageSectors:N0} sectors) does not fit on this disc ({freeSectors:N0} sectors free).");
        }
        _note($"{(append != null ? $"Adding session {append.Value.SessionNumber}" : "Disc blank")}; writing starts at LBA {startLba}; {state.ImageSectors} image sectors" +
              (trackSectors > state.ImageSectors ? $" + {trackSectors - state.ImageSectors} padding to a whole 32 KiB block" : "") + $"; {freeSectors} free.");

        // 3. Write parameters (DVD-R/-RW only: libburn's cookbook says no mode page is sent for DVD+R), read back.
        if (!plusR)
        {
            var current = ReadWriteParameters() ?? throw Stop("The drive did not return its write parameters (mode page 0x05).");
            byte trackMode = track.TrackMode != 0 ? track.TrackMode : WriteParametersPage.TrackModeDvd;
            var wanted = mode switch
            {
                DvdWriteMode.DiscAtOnce => current.ForDvdDiscAtOnce(request.TestWrite, bufferUnderrunFree),
                DvdWriteMode.LayerJump => current.ForDvdLayerJump(request.TestWrite, bufferUnderrunFree, trackMode),
                _ => current.ForDvdIncremental(request.TestWrite, bufferUnderrunFree, request.CloseDisc, trackMode)
            };
            if (!SelectWriteParameters(wanted))
            {
                throw Stop($"The drive rejected the {state.WriteMode} write parameters.");
            }
            var confirmed = ReadWriteParameters() ?? throw Stop("The drive did not report its write parameters after they were set.");
            byte wantedType = mode switch
            {
                DvdWriteMode.DiscAtOnce => WriteParametersPage.WriteTypeSessionAtOnce,
                DvdWriteMode.LayerJump => WriteParametersPage.WriteTypeLayerJump,
                _ => WriteParametersPage.WriteTypeIncremental
            };
            if (confirmed.WriteType != wantedType || confirmed.DataBlockType != WriteParametersPage.DataBlockTypeMode1)
            {
                throw Stop($"The drive did not switch to {state.WriteMode} (write type {confirmed.WriteType}, data block type {confirmed.DataBlockType}).");
            }
            if (mode is DvdWriteMode.Incremental or DvdWriteMode.LayerJump && (!confirmed.FixedPackets || confirmed.PacketSize != WriteParametersPage.DvdPacketBlocks))
            {
                throw Stop($"The drive did not confirm fixed packets of 16 blocks (packet size {confirmed.PacketSize}).");
            }
            if (confirmed.TestWrite != request.TestWrite)
            {
                throw Stop("The drive did not confirm test-write mode, so nothing was written.");
            }
            _note($"Write parameters confirmed: {state.WriteMode}, test write {confirmed.TestWrite}, BUFE {confirmed.BufferUnderrunFree}, multi-session {confirmed.MultiSession}.");
        }

        // 3b. Dual-layer: where layer 0 ends. Set before the first WRITE; a refusal stops with nothing written.
        LayerJump? jump = null;
        if (layerJump)
        {
            jump = ApplyLayerJump(trackSectors, request.LayerBreakSectors, state);
        }
        else if (dualLayer)
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
        if (jump is { } j)
        {
            // Layer 0 up to and including the jump address; the drive then goes on at the same radius on layer 1.
            WriteTrack(request, startLba, j.Layer0Sectors, DvdSectorsPerWrite, state, reporter, ct, DvdMinimumSectorsPerWrite);
            if (ReadTrackInformation(1) is not { NextWritableAddressValid: true } onLayer1 || onLayer1.NextWritableAddress != j.Layer1StartLba)
            {
                throw Stop($"Layer 0 was written up to the layer jump, but the drive did not report LBA {j.Layer1StartLba:N0} on layer 1 as the " +
                           "next writable address; the rest of the image was not written and the disc is not usable.");
            }
            _note($"Layer jump: {j.Layer0Sectors} sectors written on layer 0; the drive goes on at LBA {j.Layer1StartLba} on layer 1.");
            WriteTrack(request, j.Layer1StartLba, trackSectors - j.Layer0Sectors, DvdSectorsPerWrite, state, reporter, ct, DvdMinimumSectorsPerWrite,
                imageBytesAlreadyWritten: (long)j.Layer0Sectors * SectorSize);
        }
        else
        {
            WriteTrack(request, startLba, trackSectors, DvdSectorsPerWrite, state, reporter, ct, DvdMinimumSectorsPerWrite);
        }
        writing.Stop();

        reporter.Report(trackSectors, "Finishing the track");
        Flush("after the data");
        WaitUntilReady(FlushTimeout, CancellationToken.None, state, "after writing");

        if (request.TestWrite)
        {
            bool stillBlank = CheckUnwrittenAfterTestWrite(startLba, out string extra);
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
            string written = append != null ? $"Session {append.Value.SessionNumber} was added at sector {startLba:N0}" : "The data was written";
            closing = request.CloseDisc ? $"{written} and the disc closed." : $"{written} and the session closed.";
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
        string tocNote = append is { } added ? CheckAddedSession(added, state.ImageSectors) : CheckTableOfContents(startLba, state.ImageSectors);
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

        if (ChooseLayer0Sectors(trackSectors, requested, boundary.Layer0Sectors) is not int wanted)
        {
            _note("The image fits on layer 0; the layer break is left as it is.");
            return;
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

    /// <summary>
    /// How many sectors go on layer 0: the size asked for (a multiple of 16, within layer 0, at least half the image so
    /// layer 1 never holds more), or growisofs' rule: nothing to choose when the image fits on layer 0 (null), else half
    /// the image rounded up to whole 16-sector ECC blocks.
    /// </summary>
    private static int? ChooseLayer0Sectors(int trackSectors, int? requested, int layer0Capacity)
    {
        if (requested is int asked)
        {
            if (asked <= 0 || asked % WriteParametersPage.DvdPacketBlocks != 0)
            {
                throw Stop($"The layer break must be a positive multiple of 16 sectors; {asked} is not.");
            }
            if (asked > layer0Capacity)
            {
                throw Stop($"The layer break ({asked:N0} sectors) is beyond the end of layer 0 ({layer0Capacity:N0} sectors).");
            }
            if (2L * asked < trackSectors)
            {
                throw Stop($"The layer break ({asked:N0} sectors) would put more on layer 1 than on layer 0; it must be at least half the image ({(trackSectors + 1) / 2:N0} sectors).");
            }
            return asked;
        }
        if (trackSectors <= layer0Capacity)
        {
            return null;
        }
        return (trackSectors / WriteParametersPage.DvdPacketBlocks + 1) / 2 * WriteParametersPage.DvdPacketBlocks;
    }

    /// <summary>Where a DVD-R DL track leaves layer 0: the sectors written there, and the layer 1 address writing goes on at.</summary>
    private readonly record struct LayerJump(int Layer0Sectors, int Layer1StartLba);

    /// <summary>
    /// Sets the manual layer jump of a blank DVD-R DL written with Layer Jump Recording (write type 4 already selected).
    /// READ DISC STRUCTURE 20h gives the layer 0 capacity of the disc's fixed address space (Windows SDK ntddcdvd.h,
    /// DVD_DUAL_LAYER_RECORDING_INFORMATION: bytes 4–7 of the data, after the 4-byte header); the layer 0 size is chosen
    /// as for DVD+R DL (<see cref="ChooseLayer0Sectors"/>). SEND DISC STRUCTURE 23h then carries the last layer 0 address,
    /// that size minus 1 (a Plextor PX-760A reported the address 1,864,223 after a burn with 1,864,224 sectors on layer 0,
    /// ImgBurn's disc information in Doom9 forum thread 113868, 2006), and READ DISC STRUCTURE 23h must give it back.
    /// Some drives answer SEND DISC STRUCTURE with an error and still take the value (seen with format 20h), so the
    /// read-back decides.
    /// <para>The DVD-R DL address space is fixed: layer 0 holds LBAs 0 to C - 1 and layer 1, written from the outside in
    /// (opposite track path), C to 2C - 1, so LBA x on layer 0 lies over LBA 2C - 1 - x on layer 1. After the jump the
    /// drive goes on at the same radius on layer 1, LBA 2C - (layer 0 sectors), and the host writes there (the same
    /// thread: on 100-sector layers a jump after 70 sectors lands at 130). Closing the disc records the shorter layer 0
    /// (the Plextor then reported a layer 0 capacity of 1,864,224), so the image reads back in one run from LBA 0.</para>
    /// Null when the image fits on layer 0 and no break was asked for: nothing is sent and the image stays on layer 0.
    /// </summary>
    private LayerJump? ApplyLayerJump(int trackSectors, int? requested, RunState state)
    {
        var boundary = ReadLayerBoundary() ?? throw Stop("The drive did not report the layer 0 capacity of this DVD-R DL.");
        int capacity = boundary.Layer0Sectors;
        if (capacity <= 0 || capacity % WriteParametersPage.DvdPacketBlocks != 0)
        {
            throw Stop($"The drive reported a layer 0 capacity of {capacity:N0} sectors, which is not a whole number of 16-sector blocks.");
        }
        state.Layer0Sectors = capacity;
        _note($"Layer 0 capacity {capacity} sectors; image {trackSectors} sectors.");

        if (ChooseLayer0Sectors(trackSectors, requested, capacity) is not int layer0)
        {
            _note("The image fits on layer 0; no layer jump is set.");
            return null;
        }
        if (layer0 >= trackSectors)
        {
            _note($"The image ({trackSectors} sectors) ends before the layer break asked for ({layer0}); it stays on layer 0 and no layer jump is set.");
            return null;
        }

        int address = layer0 - 1;
        byte[] parameters = NativeWriteCommands.BuildManualLayerJumpParameters(address);
        var send = _transport.ExecuteCommand(NativeWriteCommands.BuildSendDiscStructure(NativeWriteCommands.FormatManualLayerJump, (ushort)parameters.Length),
            parameters, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromMinutes(3)); // as for format 20h: allow for a calibration
        uint? after = ReadManualLayerJump();
        if (after != (uint)address)
        {
            throw Stop($"The drive did not set the layer jump at LBA {address:N0}: {(send.IsSuccess ? "it reports " + (after?.ToString("N0") ?? "nothing") : Describe(send))}.");
        }
        state.Layer0Sectors = layer0;
        int layer1Start = 2 * capacity - layer0;
        _note(send.IsSuccess
            ? $"Layer jump set after LBA {address}: {layer0} sectors on layer 0, {trackSectors - layer0} on layer 1 from LBA {layer1Start}."
            : $"The drive answered {Describe(send)} but reports the layer jump at LBA {address}; going on.");
        return new LayerJump(layer0, layer1Start);
    }

    private uint? ReadManualLayerJump()
    {
        byte[] reply = new byte[NativeWriteCommands.ManualLayerJumpLength];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadDiscStructureCdb(NativeWriteCommands.FormatManualLayerJump, (ushort)reply.Length),
            reply, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        return r.IsSuccess ? NativeWriteCommands.ParseManualLayerJump(reply) : null;
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
