using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace CdBurner.Core.DvdVideo;

/// <summary>One cell of a DVD-Video title set: where it starts within the title set's video files.</summary>
/// <param name="TitleSet">Title set number (the nn in VTS_nn_0.IFO).</param>
/// <param name="VobId">VOB id from the cell address table.</param>
/// <param name="CellId">Cell id from the cell address table.</param>
/// <param name="StartSector">First sector of the cell, counted from the start of VTS_nn_1.VOB with the title set's
/// VOB files taken as one run.</param>
public sealed record DvdCellStart(int TitleSet, int VobId, int CellId, long StartSector);

/// <summary>A cell start placed on the disc (or on the planned layout): its sector address there.</summary>
public sealed record DvdCellPosition(DvdCellStart Cell, int Lba)
{
    public override string ToString() => $"Title set {Cell.TitleSet}, cell {Cell.CellId} (VOB {Cell.VobId}) at LBA {Lba:N0}";
}

/// <summary>
/// Places the layer break of a dual-layer DVD-Video disc on a cell boundary, where players expect the layer change.
/// The cell address table of each title set is read from its IFO: the IFO's header holds, at byte 0xE0, the sector
/// of the title cell address table (VTS_C_ADT); the table is an 8-byte header (number of VOBs, reserved, end address
/// = last byte of the last entry) and 12-byte entries (VOB id 2 bytes, cell id 1, reserved 1, start sector 4, last
/// sector 4), sectors counted within the title set's VOB files (mpucoder's DVD-Video IFO reference; libdvdread's
/// c_adt_t and cell_adr_t). Each start is then mapped onto the disc through where the VTS_nn_k.VOB files sit, and only
/// starts that can be a DVD+R DL layer break are kept: on a 16-sector ECC block boundary, with layer 0 at least half
/// the disc and within layer 0's capacity (<see cref="DvdLayerBreakCalculator"/>).
/// </summary>
public static class DvdCellLayerBreak
{
    /// <summary>Byte offset of the VTS_C_ADT sector pointer in a title set IFO.</summary>
    public const int TitleCellTablePointerOffset = 0xE0;

    private const int SectorSize = 2048;
    private const int TableHeaderSize = 8;
    private const int EntrySize = 12;
    private static readonly Regex TitleSetIfo = new(@"^VTS_(\d{2})_0\.IFO$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TitleVob = new(@"^VTS_(\d{2})_([1-9])\.VOB$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The cell starts of one title set from its IFO (VTS_nn_0.IFO). Throws <see cref="InvalidDataException"/> when the
    /// file is not a title set IFO or its cell address table lies outside it.
    /// </summary>
    public static IReadOnlyList<DvdCellStart> ReadTitleCellStarts(int titleSet, ReadOnlySpan<byte> ifo)
    {
        if (ifo.Length < TitleCellTablePointerOffset + 4 || Encoding.ASCII.GetString(ifo[..12]) != "DVDVIDEO-VTS")
        {
            throw new InvalidDataException($"VTS_{titleSet:D2}_0.IFO is not a DVD-Video title set IFO.");
        }

        long tableStart = (long)BinaryPrimitives.ReadUInt32BigEndian(ifo.Slice(TitleCellTablePointerOffset, 4)) * SectorSize;
        if (tableStart <= 0 || tableStart + TableHeaderSize > ifo.Length)
        {
            throw new InvalidDataException($"VTS_{titleSet:D2}_0.IFO: the cell address table is outside the file.");
        }

        var table = ifo[(int)tableStart..];
        long lastByte = BinaryPrimitives.ReadUInt32BigEndian(table.Slice(4, 4));
        long entries = (lastByte + 1 - TableHeaderSize) / EntrySize;
        if (entries < 0 || TableHeaderSize + entries * EntrySize > table.Length)
        {
            throw new InvalidDataException($"VTS_{titleSet:D2}_0.IFO: the cell address table runs past the end of the file.");
        }

        var cells = new List<DvdCellStart>((int)entries);
        for (int i = 0; i < entries; i++)
        {
            var e = table.Slice(TableHeaderSize + i * EntrySize, EntrySize);
            cells.Add(new DvdCellStart(titleSet, BinaryPrimitives.ReadUInt16BigEndian(e), e[2], BinaryPrimitives.ReadUInt32BigEndian(e.Slice(4, 4))));
        }
        return cells;
    }

    /// <summary>
    /// Every title cell start placed on the disc. <paramref name="files"/> gives each VIDEO_TS file by name (upper
    /// case, no folder) with its first sector and size in sectors there; <paramref name="readFile"/> returns a file's
    /// bytes (only the IFOs are read). Title sets whose IFO cannot be read are left out. A cell start is placed in the
    /// VOB file that holds it, so gaps between the VOB files on the disc are allowed for.
    /// </summary>
    public static IReadOnlyList<DvdCellPosition> PlaceCells(IReadOnlyDictionary<string, (int Lba, int Sectors)> files,
        Func<string, byte[]?> readFile)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(readFile);
        var positions = new List<DvdCellPosition>();
        foreach (string name in files.Keys.Where(n => TitleSetIfo.IsMatch(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            int titleSet = int.Parse(TitleSetIfo.Match(name).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            byte[]? ifo = readFile(name);
            if (ifo == null)
            {
                continue;
            }

            IReadOnlyList<DvdCellStart> cells;
            try
            {
                cells = ReadTitleCellStarts(titleSet, ifo);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            // The title set's VOB files in order, with where each starts within the run of all of them.
            var vobs = files.Where(f => TitleVob.Match(f.Key) is { Success: true } m && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == titleSet)
                .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.Value)
                .ToList();
            foreach (var cell in cells)
            {
                long offset = cell.StartSector;
                foreach (var vob in vobs)
                {
                    if (offset < vob.Sectors)
                    {
                        positions.Add(new DvdCellPosition(cell, vob.Lba + (int)offset));
                        break;
                    }
                    offset -= vob.Sectors;
                }
            }
        }
        return positions;
    }

    /// <summary>
    /// The cell positions that can be the layer break of a disc of <paramref name="totalSectors"/>: on a 16-sector
    /// boundary, with layer 0 at least half the disc and within layer 0's capacity. Ordered by address.
    /// </summary>
    public static IReadOnlyList<DvdCellPosition> UsableBreaks(IEnumerable<DvdCellPosition> positions, int totalSectors)
    {
        var allowed = DvdLayerBreakCalculator.CalculateCandidates(totalSectors, positions.Select(p => p.Lba))
            .Select(c => c.BreakLba)
            .ToHashSet();
        return positions.Where(p => allowed.Contains(p.Lba))
            .GroupBy(p => p.Lba).Select(g => g.First())
            .OrderBy(p => p.Lba)
            .ToList();
    }

    /// <summary>
    /// The layer break for a disc: the usable cell boundary at or nearest the cell the user picked
    /// (<paramref name="preferred"/>, matched by title set, VOB and cell), else the one nearest an even split. Null
    /// when no cell boundary can be the break.
    /// </summary>
    public static DvdCellPosition? Choose(IEnumerable<DvdCellPosition> positions, int totalSectors, DvdCellStart? preferred = null)
    {
        var list = positions.ToList();
        var usable = UsableBreaks(list, totalSectors);
        if (usable.Count == 0)
        {
            return null;
        }

        long target = (totalSectors + 1L) / 2;
        if (preferred != null && list.FirstOrDefault(p => SameCell(p.Cell, preferred)) is { } picked)
        {
            target = picked.Lba;
        }
        return usable.OrderBy(p => Math.Abs(p.Lba - target)).ThenBy(p => p.Lba).First();
    }

    private static bool SameCell(DvdCellStart a, DvdCellStart b) =>
        a.TitleSet == b.TitleSet && a.VobId == b.VobId && a.CellId == b.CellId;
}
