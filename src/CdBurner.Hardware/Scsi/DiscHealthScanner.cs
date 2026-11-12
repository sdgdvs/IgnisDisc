using System.Diagnostics;
using CdBurner.Core.Verification;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Reads every sector of a CD once with C2 error pointers and maps where it is weak (<see cref="DiscHealthReport"/>).
/// Read-only: READ TOC and READ CD only. Sectors are read in runs of 24 (24 × 2,646 bytes stays inside the 64 KiB a
/// USB bridge passes per command); a run that fails is read again sector by sector so one bad spot does not hide its
/// neighbours, and a sector that fails twice counts as unreadable. A drive that refuses C2 pointers is read plainly
/// and only unreadable sectors are counted.
/// </summary>
public sealed class DiscHealthScanner
{
    private const int SectorSize = 2352;
    private const int C2Bytes = 294;
    private const int SectorsPerRun = 65_536 / (SectorSize + C2Bytes); // 24
    private const byte UserDataWithC2 = 0xFA;   // sync, header, user data, EDC/ECC (2,352 bytes) + C2 block
    private const byte UserDataOnly = 0xF8;
    private const int TaoRunOutSectors = 2;     // the two link blocks at the end of a Track-at-Once data track never read
    private const int InterSessionGap = 11_400; // before the data session of an Enhanced CD
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IScsiTransport _transport;

    public DiscHealthScanner(IScsiTransport transport) => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <param name="progress">Receives (sectors read, sectors in all).</param>
    /// <exception cref="InvalidOperationException">No CD table of contents could be read.</exception>
    public DiscHealthReport Scan(Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var ranges = ReadRanges();
        int total = ranges.Sum(r => r.Count);
        var perMinute = new int[(ranges.Max(r => r.Start + r.Count) + DiscHealthReport.SectorsPerSecond * 60 - 1) / (DiscHealthReport.SectorsPerSecond * 60)];
        var tracks = new List<TrackHealth>();
        bool c2 = true;
        int done = 0;

        foreach (var range in ranges)
        {
            int flagged = 0, unreadable = 0;
            for (int lba = range.Start; lba < range.Start + range.Count;)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(SectorsPerRun, range.Start + range.Count - lba);
                var outcome = ReadRun(lba, count, ref c2);
                for (int i = 0; i < count; i++)
                {
                    int sector = lba + i;
                    bool ignorable = !range.IsAudio && sector >= range.Start + range.Count - TaoRunOutSectors;
                    if (outcome[i] == SectorState.Unreadable && !ignorable)
                    {
                        unreadable++;
                        perMinute[sector / (DiscHealthReport.SectorsPerSecond * 60)]++;
                    }
                    else if (outcome[i] == SectorState.Flagged)
                    {
                        flagged++;
                        perMinute[sector / (DiscHealthReport.SectorsPerSecond * 60)]++;
                    }
                }
                lba += count;
                done += count;
                progress?.Invoke(done, total);
            }
            tracks.Add(new TrackHealth(range.Track, range.IsAudio, range.Start, range.Count, flagged, unreadable));
        }

        return new DiscHealthReport(total, tracks.Sum(t => t.FlaggedSectors), tracks.Sum(t => t.UnreadableSectors), c2, tracks,
            perMinute, clock.Elapsed); // every minute of the disc, so "the outer fifth" means the disc's, not the problems'
    }

    private enum SectorState { Clean, Flagged, Unreadable }

    private readonly record struct TrackRange(int Track, bool IsAudio, int Start, int Count);

    /// <summary>Each track from its start to the next one (or the lead-out), less the gap before an Enhanced CD's data session.</summary>
    private List<TrackRange> ReadRanges()
    {
        byte[] buffer = new byte[4 + 8 * 100];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x00, 0, (ushort)buffer.Length), buffer,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, Timeout);
        if (!r.IsSuccess || r.DataTransferLength < 12)
        {
            throw new InvalidOperationException($"The drive could not read the disc's table of contents: {r.SenseInfo?.ToString() ?? r.ErrorMessage}. Is a CD in the drive?");
        }

        var toc = MmcResponseParser.ParseToc(buffer.AsSpan(0, (int)Math.Min(r.DataTransferLength, (uint)buffer.Length)));
        var ranges = new List<TrackRange>();
        for (int i = 0; i + 1 < toc.Count; i++)
        {
            var cur = toc[i];
            var next = toc[i + 1];
            bool audio = (cur.AdrControl & 0x04) == 0;
            int end = next.StartLba;
            bool nextIsDataAfterAudio = audio && next.TrackNumber != 0xAA && (next.AdrControl & 0x04) != 0;
            if (nextIsDataAfterAudio && end - cur.StartLba > InterSessionGap)
            {
                end -= InterSessionGap;
            }
            if (cur.TrackNumber != 0xAA && end > cur.StartLba && cur.StartLba >= 0)
            {
                ranges.Add(new TrackRange(cur.TrackNumber, audio, cur.StartLba, end - cur.StartLba));
            }
        }
        if (ranges.Count == 0)
        {
            throw new InvalidOperationException("The disc's table of contents lists no tracks.");
        }
        return ranges;
    }

    private SectorState[] ReadRun(int lba, int count, ref bool c2)
    {
        var states = new SectorState[count];
        if (TryRead(lba, count, ref c2, out byte[] data))
        {
            Classify(data, count, c2, states, 0);
            return states;
        }

        // The run failed: each sector on its own, twice before calling it unreadable.
        for (int i = 0; i < count; i++)
        {
            if (TryRead(lba + i, 1, ref c2, out byte[] one) || TryRead(lba + i, 1, ref c2, out one))
            {
                Classify(one, 1, c2, states, i);
            }
            else
            {
                states[i] = SectorState.Unreadable;
            }
        }
        return states;
    }

    private static void Classify(byte[] data, int count, bool c2, SectorState[] states, int at)
    {
        int stride = c2 ? SectorSize + C2Bytes : SectorSize;
        for (int i = 0; i < count; i++)
        {
            states[at + i] = c2 && data.AsSpan(i * stride + SectorSize, C2Bytes).ContainsAnyExcept((byte)0)
                ? SectorState.Flagged
                : SectorState.Clean;
        }
    }

    private bool TryRead(int lba, int count, ref bool c2, out byte[] data)
    {
        data = new byte[count * (c2 ? SectorSize + C2Bytes : SectorSize)];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadCdCdb(lba, count, expectedSectorType: 0, mainChannelSelectionBits: c2 ? UserDataWithC2 : UserDataOnly),
            data, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, Timeout);
        if (!r.IsSuccess && c2 && r.SenseInfo is { SenseKey: 0x05, Asc: 0x24 })
        {
            c2 = false; // the drive does not do C2 pointers: read plainly from here on
            return TryRead(lba, count, ref c2, out data);
        }
        return r.IsSuccess && r.DataTransferLength >= (uint)data.Length;
    }
}
