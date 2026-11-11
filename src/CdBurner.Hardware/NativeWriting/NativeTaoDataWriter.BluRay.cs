using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Blu-ray, from libburn's cookbook (libburnia, doc/cookbook.txt):
/// <list type="bullet">
/// <item>BD-R in sequential recording mode (profile 41h) is written "very similar to DVD+R": no mode page and no test
/// write (MMC defines none for Blu-ray); WRITE (10) from the next writable address; SYNCHRONIZE CACHE; CLOSE TRACK
/// (001b); then CLOSE SESSION (010b) to leave it appendable or close function 110b to finalize it (DVD+R uses 101b).
/// An unformatted BD-R is formatted by the drive at the first write. The image is padded to whole 64 KiB clusters
/// (32 sectors), the Blu-ray error-correction unit.</item>
/// <item>BD-RE (profile 43h) is overwritable (see NativeTaoDataWriter.Overwritable.cs): it is sold unformatted and
/// must be formatted first, with FORMAT UNIT type 00h (the drive's default size and quick formatting) copied from READ
/// FORMAT CAPACITIES; 2 KiB alignment; no close command.</item>
/// </list>
/// Both need the drive's BD Write feature (0x0041) to be current: a drive that only reads Blu-ray still reports the
/// disc's profile. Random recording mode (42h) and BD-ROM are refused.
/// </summary>
public sealed partial class NativeTaoDataWriter
{
    public const ushort ProfileBdRom = 0x0040;
    public const ushort ProfileBdR = 0x0041;
    public const ushort ProfileBdRRandom = 0x0042;
    public const ushort ProfileBdRe = 0x0043;

    /// <summary>Blu-ray error-correction cluster: 64 KiB.</summary>
    public const int BdClusterSectors = 32;

    /// <summary>Sectors a second at Blu-ray 1x (4,495,500 bytes / 2,048).</summary>
    private const double BdSectorsPerSecondAt1x = 4_495_500.0 / SectorSize;

    internal static bool IsBluRayProfile(ushort profile) => profile is >= ProfileBdRom and <= ProfileBdRe;

    private NativeDataWriteResult RunBluRay(NativeDataWriteRequest request, ushort profile, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct, Stopwatch clock, RunState state)
    {
        state.IsBluRay = true;
        if (profile is not (ProfileBdR or ProfileBdRe))
        {
            state.UnsupportedDisc = true;
            throw Stop(profile == ProfileBdRRandom
                ? "Direct writing covers BD-R in sequential recording mode; this BD-R is formatted for random recording."
                : "This disc is a BD-ROM and cannot be written.");
        }
        var bdWrite = ReadFeature(MmcCommandBuilder.FeatureBdWrite);
        if (!bdWrite.Current)
        {
            state.UnsupportedDisc = true;
            throw Stop("The drive does not offer Blu-ray writing for this disc (feature 0x0041 is not current); it may only read Blu-ray.");
        }
        return profile == ProfileBdRe
            ? RunOverwritable(request, OverwritableKind.BdRe, progress, ct, clock, state)
            : RunBdR(request, progress, ct, clock, state);
    }

    private NativeDataWriteResult RunBdR(NativeDataWriteRequest request, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct, Stopwatch clock, RunState state)
    {
        state.WriteMode = "sequential (BD-R)";
        _note("Disc: a BD-R in sequential recording mode; write mode sequential.");
        if (request.TestWrite)
        {
            state.TestWriteNotOffered = true;
            throw Stop("Blu-ray discs have no test-write mode.");
        }

        // 1. Blank disc; where writing starts and how much fits.
        var disc = ReadDiscInformation() ?? throw Stop("The drive did not return disc information.");
        if (disc.Status != 0)
        {
            throw Stop("The disc is not blank. Direct writing needs a blank BD-R.");
        }
        if (ReadTrackInformation(1) is not { NextWritableAddressValid: true } track)
        {
            throw Stop("The drive did not report where writing starts on this disc.");
        }
        if (track.NextWritableAddress != 0)
        {
            throw Stop($"The blank disc's first writable sector is {track.NextWritableAddress}, not 0; the image would not be readable.");
        }
        long freeSectors = track.FreeBlocks;
        int trackSectors = (state.ImageSectors + BdClusterSectors - 1) / BdClusterSectors * BdClusterSectors;
        if (trackSectors > freeSectors)
        {
            throw Stop($"The image ({state.ImageSectors:N0} sectors) does not fit on this disc ({freeSectors:N0} sectors free).");
        }
        _note($"Disc blank; writing starts at LBA 0; {state.ImageSectors} image sectors" +
              (trackSectors > state.ImageSectors ? $" + {trackSectors - state.ImageSectors} padding to a whole 64 KiB cluster" : "") + $"; {freeSectors} free.");

        // 2. Speed from the drive's own list.
        ChooseAndSetSpeed(request.WriteSpeedKiloBytesPerSecond, state, streamingEndLba: (int)Math.Min(int.MaxValue, Math.Max(trackSectors, freeSectors) - 1));
        Check(ct, state);

        // 3. The data, 64 KiB per WRITE.
        request.Image.Position = request.StartPosition;
        var reporter = new ProgressReporter(this, progress, clock, trackSectors, BdSectorsPerSecondAt1x);
        var writing = Stopwatch.StartNew();
        WriteTrack(request, 0, trackSectors, DvdSectorsPerWrite, state, reporter, ct, DvdMinimumSectorsPerWrite);
        writing.Stop();

        reporter.Report(trackSectors, "Finishing the track");
        Flush("after the data");
        WaitUntilReady(FlushTimeout, CancellationToken.None, state, "after writing");

        // 4. Close the track, then finalize (110b) or close the session (010b); a refused finalize closes the session.
        reporter.Report(trackSectors, "Closing the track");
        var closeTrack = Close(NativeWriteCommands.CloseFunctionTrack, ReadLastTrackNumber() ?? 1, state);
        if (!closeTrack.IsSuccess)
        {
            throw Stop($"The data was written, but the drive could not close the track: {Describe(closeTrack)}. The disc may not be readable.");
        }
        reporter.Report(trackSectors, request.CloseDisc ? "Closing the disc" : "Closing the session");
        string closing = request.CloseDisc ? "The data was written and the disc closed." : "The data was written and the session closed.";
        bool closed = false;
        if (request.CloseDisc)
        {
            var finalize = Close(NativeWriteCommands.CloseFunctionFinalizeBdR, 0, state);
            closed = finalize.IsSuccess;
            if (!closed)
            {
                _note($"The drive refused to finalize the BD-R ({Describe(finalize)}); closing the session instead.");
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

        string tocNote = CheckTableOfContents(0, state.ImageSectors);
        reporter.Report(trackSectors, "Done");
        return DvdResult(state, clock, writing, trackSectors) with
        {
            Success = true,
            Message = closing + " " + tocNote,
            DiscRecorded = true
        };
    }
}
