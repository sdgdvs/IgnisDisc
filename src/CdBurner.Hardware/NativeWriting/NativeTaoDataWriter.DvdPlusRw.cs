using System.Diagnostics;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// DVD+RW: an overwritable disc, so there is no track to reserve, no test write and no session to open. Sequence from
/// libburn's cookbook (libburnia, doc/cookbook.txt): a never-formatted disc (READ DISC INFORMATION byte 7, background
/// format status 0) gets FORMAT UNIT type 26h, which the drive runs in the background while writing goes on; then
/// WRITE (10) from LBA 0 on 2 KiB boundaries, SYNCHRONIZE CACHE, and CLOSE SESSION (010b), which stops any background
/// format and leaves the disc readable in other drives (the cookbook: harmless when nothing is open). A DVD+RW that
/// already holds a file system is only written over when <see cref="NativeDataWriteRequest.OverwriteExistingData"/>
/// says the user allowed it.
/// </summary>
public sealed partial class NativeTaoDataWriter
{
    public const ushort ProfileDvdPlusRw = 0x001A;

    private NativeDataWriteResult RunDvdPlusRw(NativeDataWriteRequest request, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct, Stopwatch clock, RunState state)
    {
        state.WriteMode = "overwrite (DVD+RW)";
        _note("Disc: a DVD+RW; write mode overwrite.");
        if (request.TestWrite)
        {
            state.TestWriteNotOffered = true;
            throw Stop("DVD+RW discs have no test-write mode.");
        }

        // 1. Formatting state and what is on the disc.
        var disc = ReadDiscInformation() ?? throw Stop("The drive did not return disc information.");
        int background = disc.BackgroundFormat;
        _note($"Background format status {background} ({background switch { 0 => "not formatted", 1 => "partly formatted", 2 => "formatting", _ => "formatted" }}).");
        if (background != 0 && !request.OverwriteExistingData && FileSystemAtSector16() is string found)
        {
            throw Stop($"The DVD+RW holds data ({found} at sector 16). Allow replacing it (--overwrite, or \"replace\" in the app) to write over it.");
        }

        // 2. How much fits: the formatted size, or the most the disc can be formatted to.
        var report = MmcMediaReport.Query(_transport, ShortTimeout);
        long capacity = report.FormattedCapacity?.Blocks ?? 0;
        if (capacity <= 0)
        {
            capacity = ReadTrackInformation(1)?.TrackSize ?? 0;
        }
        if (capacity <= 0)
        {
            throw Stop("The drive did not report the size of this DVD+RW.");
        }
        int sectors = state.ImageSectors;
        if (sectors > capacity)
        {
            throw Stop($"The image ({sectors:N0} sectors) does not fit on this disc ({capacity:N0} sectors).");
        }
        _note($"Writing {sectors} sectors from LBA 0; the disc holds {capacity}.");

        // 3. Speed from the drive's own list.
        ChooseAndSetSpeed(request.WriteSpeedKiloBytesPerSecond, state, streamingEndLba: (int)Math.Min(int.MaxValue, capacity - 1), report: report);
        Check(ct, state);

        // 4. A never-formatted disc is formatted first; the drive does it in the background. From here the disc changes.
        bool formatted = false;
        if (background == 0)
        {
            var format = _transport.ExecuteCommand(NativeWriteCommands.BuildFormatUnit(), NativeWriteCommands.BuildDvdPlusRwFormatParameters(restart: false),
                ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, ShortTimeout);
            if (!format.IsSuccess)
            {
                throw Stop($"The drive refused to format the DVD+RW: {Describe(format)}");
            }
            state.WritingStarted = true;
            formatted = true;
            _note("FORMAT UNIT type 26h sent; the drive formats in the background.");
            WaitUntilReady(FlushTimeout, ct, state, "after starting the format");
        }

        // 5. The data, in 64 KiB WRITEs (DVD+RW only needs 2 KiB alignment, so the last one may be shorter).
        request.Image.Position = request.StartPosition;
        var reporter = new ProgressReporter(this, progress, clock, sectors, DvdSectorsPerSecondAt1x);
        var writing = Stopwatch.StartNew();
        WriteTrack(request, 0, sectors, DvdSectorsPerWrite, state, reporter, ct, DvdMinimumSectorsPerWrite);
        writing.Stop();

        reporter.Report(sectors, "Finishing");
        Flush("after the data");
        WaitUntilReady(FlushTimeout, CancellationToken.None, state, "after writing");

        // 6. CLOSE SESSION: stops a background format and makes the disc readable elsewhere. Not fatal if refused:
        //    the data is already on the disc.
        reporter.Report(sectors, "Closing");
        var close = Close(NativeWriteCommands.CloseFunctionSession, 0, state);
        string closeNote = close.IsSuccess ? string.Empty : " The drive refused to close the session; the data is written.";
        if (!close.IsSuccess)
        {
            _note($"CLOSE SESSION refused: {Describe(close)}");
        }

        reporter.Report(sectors, "Done");
        return DvdResult(state, clock, writing, sectors) with
        {
            Success = true,
            Message = (formatted ? "The DVD+RW was formatted and the data written from sector 0." : "The data was written over the DVD+RW from sector 0.") + closeNote,
            DiscRecorded = true
        };
    }

    /// <summary>The volume descriptor identifier at sector 16 (ISO 9660 "CD001", UDF "BEA01"), or null if none is readable.</summary>
    private string? FileSystemAtSector16()
    {
        byte[] sector = new byte[SectorSize];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildRead10Cdb(16, 1, forceUnitAccess: true), sector, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 6)
        {
            return null;
        }
        string id = Encoding.ASCII.GetString(sector, 1, 5);
        return id is "CD001" or "BEA01" or "NSR02" or "NSR03" ? id : null;
    }
}
