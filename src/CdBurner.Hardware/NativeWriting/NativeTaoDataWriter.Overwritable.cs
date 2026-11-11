using System.Diagnostics;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Overwritable DVDs: DVD+RW, DVD-RW in restricted overwrite mode and DVD-RAM. There is no track to reserve, no test
/// write and no session to open: the image goes on from LBA 0 with WRITE (10), then SYNCHRONIZE CACHE. Per disc, from
/// libburn's cookbook (libburnia, doc/cookbook.txt):
/// <list type="bullet">
/// <item>DVD+RW: a never-formatted disc (READ DISC INFORMATION byte 7, background format status 0) gets FORMAT UNIT
/// type 26h, run by the drive in the background while writing goes on. 2 KiB alignment. CLOSE SESSION (010b) at the end
/// stops a background format and leaves the disc readable elsewhere.</item>
/// <item>DVD-RW restricted overwrite: 32 KiB alignment, so the image is padded to whole 16-sector blocks. A partly
/// formatted disc (READ FORMAT CAPACITIES offers format 13h with blocks) is first expanded with format 13h and 0
/// blocks, which leaves it in the intermediate state; CLOSE SESSION (010b) at the end brings it out of that state.</item>
/// <item>DVD-RAM: sold formatted; an unformatted one is refused (formatting it with defect management is not done
/// here). 2 KiB alignment; nothing to close.</item>
/// </list>
/// A disc that holds a file system is only written over when <see cref="NativeDataWriteRequest.OverwriteExistingData"/>
/// says the user allowed it. A refused CLOSE SESSION is logged, not fatal: the data is already on the disc.
/// </summary>
public sealed partial class NativeTaoDataWriter
{
    public const ushort ProfileDvdRam = 0x0012;
    public const ushort ProfileDvdMinusRwRestricted = 0x0013;
    public const ushort ProfileDvdPlusRw = 0x001A;

    private enum OverwritableKind { DvdPlusRw, DvdMinusRwRestricted, DvdRam, BdRe }

    private static OverwritableKind? OverwritableKindOf(ushort profile) => profile switch
    {
        ProfileDvdPlusRw => OverwritableKind.DvdPlusRw,
        ProfileDvdMinusRwRestricted => OverwritableKind.DvdMinusRwRestricted,
        ProfileDvdRam => OverwritableKind.DvdRam,
        _ => null
    };

    private NativeDataWriteResult RunOverwritable(NativeDataWriteRequest request, OverwritableKind kind, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct, Stopwatch clock, RunState state)
    {
        if (kind != OverwritableKind.BdRe)
        {
            state.IsDvd = true;
        }
        string name = kind switch
        {
            OverwritableKind.DvdPlusRw => "DVD+RW",
            OverwritableKind.DvdMinusRwRestricted => "DVD-RW",
            OverwritableKind.BdRe => "BD-RE",
            _ => "DVD-RAM"
        };
        state.WriteMode = kind == OverwritableKind.DvdMinusRwRestricted ? "restricted overwrite (DVD-RW)" : $"overwrite ({name})";
        _note($"Disc: a {name}; write mode {state.WriteMode}.");
        if (request.TestWrite)
        {
            state.TestWriteNotOffered = true;
            throw Stop($"{name} discs have no test-write mode.");
        }

        // 1. Formatting state.
        var report = MmcMediaReport.Query(_transport, ShortTimeout);
        var formats = report.FormattedCapacity;
        byte? formatType = null;          // the format to start before writing, if any
        uint formatBlocks = 0;            // BD-RE: the blocks the drive offered for format 00h
        bool mayHoldData = true;
        switch (kind)
        {
            case OverwritableKind.DvdPlusRw:
            {
                var disc = ReadDiscInformation() ?? throw Stop("The drive did not return disc information.");
                int background = disc.BackgroundFormat;
                _note($"Background format status {background} ({background switch { 0 => "not formatted", 1 => "partly formatted", 2 => "formatting", _ => "formatted" }}).");
                if (background == 0)
                {
                    formatType = NativeWriteCommands.FormatTypeDvdPlusRw;
                    mayHoldData = false;
                }
                break;
            }
            case OverwritableKind.DvdMinusRwRestricted:
            {
                // Cookbook: "formatted" with format 13h (or 11h) still offering blocks means partly formatted;
                // a current descriptor of unknown capacity means the intermediate state, written as it is.
                bool partly = formats is { State: FormattedCapacityState.Formatted } &&
                              formats.Formats.Any(f => f.FormatType is NativeWriteCommands.FormatTypeDvdMinusRwExpand or 0x11 && f.Blocks > 0);
                _note(formats is null
                    ? "No current capacity reported: intermediate state."
                    : partly ? $"Partly formatted ({formats.Blocks} blocks); it will be expanded." : $"Formatted ({formats.Blocks} blocks).");
                if (partly)
                {
                    formatType = NativeWriteCommands.FormatTypeDvdMinusRwExpand;
                }
                break;
            }
            case OverwritableKind.BdRe:
                // Cookbook: BD-RE is sold unformatted and must be formatted first; type 00h is the fast default.
                if (formats is not { State: FormattedCapacityState.Formatted })
                {
                    var offer = formats?.Formats.FirstOrDefault(f => f.FormatType == NativeWriteCommands.FormatTypeDefault && f.Blocks > 0);
                    if (offer is null)
                    {
                        throw Stop("This BD-RE is not formatted, and the drive offers no default format for it.");
                    }
                    formatType = NativeWriteCommands.FormatTypeDefault;
                    formatBlocks = (uint)Math.Min(uint.MaxValue, offer.Blocks);
                    mayHoldData = false;
                    _note($"Not formatted; format 00h offered with {offer.Blocks} blocks.");
                }
                else
                {
                    _note($"Formatted ({formats.Blocks} blocks).");
                }
                break;
            default:
                if (formats is not { State: FormattedCapacityState.Formatted })
                {
                    throw Stop("This DVD-RAM is not formatted. Format it first (Windows can do this), then burn again.");
                }
                break;
        }

        // 2. Leave a disc with data alone unless the user allowed replacing it.
        if (mayHoldData && !request.OverwriteExistingData && FileSystemAtSector16() is string found)
        {
            throw Stop($"The {name} holds data ({found} at sector 16). Allow replacing it (--overwrite, or \"replace\" in the app) to write over it.");
        }

        // 3. How much fits: the formatted size, the most any listed format offers, or the track size.
        long capacity = formats?.Blocks ?? 0;
        if (formats != null)
        {
            foreach (var f in formats.Formats)
            {
                long blocks = f.FormatType == NativeWriteCommands.FormatTypeDvdMinusRwExpand ? formats.Blocks + f.Blocks : f.Blocks;
                if (f.FormatType is 0x00 or 0x10 or 0x15 or NativeWriteCommands.FormatTypeDvdMinusRwExpand or NativeWriteCommands.FormatTypeDvdPlusRw)
                {
                    capacity = Math.Max(capacity, blocks);
                }
            }
        }
        if (capacity <= 0)
        {
            // No format information (a DVD-RW in the intermediate state): the track size. Never the larger of the two,
            // so an image is not let through on a figure bigger than what the disc is formatted to.
            capacity = ReadTrackInformation(1)?.TrackSize ?? 0;
        }
        if (capacity <= 0)
        {
            throw Stop($"The drive did not report the size of this {name}.");
        }
        int sectors = kind == OverwritableKind.DvdMinusRwRestricted
            ? (state.ImageSectors + WriteParametersPage.DvdPacketBlocks - 1) / WriteParametersPage.DvdPacketBlocks * WriteParametersPage.DvdPacketBlocks
            : state.ImageSectors;
        if (sectors > capacity)
        {
            throw Stop($"The image ({state.ImageSectors:N0} sectors) does not fit on this disc ({capacity:N0} sectors).");
        }
        _note($"Writing {sectors} sectors from LBA 0" + (sectors > state.ImageSectors ? $" ({sectors - state.ImageSectors} padding to a whole 32 KiB block)" : "") +
              $"; the disc holds {capacity}.");

        // 4. Speed from the drive's own list.
        ChooseAndSetSpeed(request.WriteSpeedKiloBytesPerSecond, state, streamingEndLba: (int)Math.Min(int.MaxValue, capacity - 1), report: report);
        Check(ct, state);

        // 5. Format first where needed. From here the disc changes.
        if (formatType is byte type)
        {
            byte[] parameters = kind switch
            {
                OverwritableKind.DvdPlusRw => NativeWriteCommands.BuildDvdPlusRwFormatParameters(restart: false),
                OverwritableKind.BdRe => NativeWriteCommands.BuildBdReDefaultFormatParameters(formatBlocks),
                _ => NativeWriteCommands.BuildDvdMinusRwExpandParameters()
            };
            var format = _transport.ExecuteCommand(NativeWriteCommands.BuildFormatUnit(), parameters, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, ShortTimeout);
            if (!format.IsSuccess)
            {
                throw Stop($"The drive refused to format the {name}: {Describe(format)}");
            }
            state.WritingStarted = true;
            _note($"FORMAT UNIT type {type:X2}h sent.");
            WaitUntilReady(FlushTimeout, ct, state, "after starting the format");
        }

        // 6. The data, in 64 KiB WRITEs from LBA 0.
        request.Image.Position = request.StartPosition;
        var reporter = new ProgressReporter(this, progress, clock, sectors, state.IsBluRay ? BdSectorsPerSecondAt1x : DvdSectorsPerSecondAt1x);
        var writing = Stopwatch.StartNew();
        WriteTrack(request, 0, sectors, DvdSectorsPerWrite, state, reporter, ct, DvdMinimumSectorsPerWrite);
        writing.Stop();

        reporter.Report(sectors, "Finishing");
        Flush("after the data");
        WaitUntilReady(FlushTimeout, CancellationToken.None, state, "after writing");

        // 7. DVD+RW and DVD-RW: CLOSE SESSION. DVD-RAM and BD-RE: nothing to close.
        string closeNote = string.Empty;
        if (kind is OverwritableKind.DvdPlusRw or OverwritableKind.DvdMinusRwRestricted)
        {
            reporter.Report(sectors, "Closing");
            var close = Close(NativeWriteCommands.CloseFunctionSession, 0, state);
            if (!close.IsSuccess)
            {
                _note($"CLOSE SESSION refused: {Describe(close)}");
                closeNote = " The drive refused to close the session; the data is written.";
            }
        }

        reporter.Report(sectors, "Done");
        string written = formatType is null
            ? $"The data was written over the {name} from sector 0."
            : $"The {name} was {(kind == OverwritableKind.DvdMinusRwRestricted ? "expanded" : "formatted")} and the data written from sector 0.";
        return DvdResult(state, clock, writing, sectors) with
        {
            Success = true,
            Message = written + closeNote,
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
