using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Layer break on a DVD-Video cell boundary: the title cell address table read from VTS_nn_0.IFO (pointer at byte
/// 0xE0, 8-byte header, 12-byte entries, per mpucoder's IFO reference and libdvdread's c_adt_t / cell_adr_t), cell
/// starts placed through the VOB files' positions, and the break chosen among starts on a 16-sector block with layer 0
/// at least half the disc and within its capacity.
/// </summary>
public sealed class DvdCellLayerBreakTests
{
    /// <summary>A title set IFO with the given cells: (VOB id, cell id, start sector), table at sector 2.</summary>
    private static byte[] TitleSetIfo(params (int Vob, int Cell, uint Start)[] cells)
    {
        byte[] ifo = new byte[3 * 2048];
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(ifo, 0);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xE0), 2);
        int t = 2 * 2048;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(t), (ushort)cells.Select(c => c.Vob).Distinct().Count());
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(t + 4), (uint)(8 + 12 * cells.Length - 1));
        for (int i = 0; i < cells.Length; i++)
        {
            int e = t + 8 + 12 * i;
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(e), (ushort)cells[i].Vob);
            ifo[e + 2] = (byte)cells[i].Cell;
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(e + 4), cells[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(e + 8), cells[i].Start + 99);
        }
        return ifo;
    }

    [Fact]
    public void CellTable_IsReadFromThePointerAt0xE0()
    {
        byte[] ifo = TitleSetIfo((1, 1, 0), (1, 2, 500), (2, 1, 1_200));

        var cells = DvdCellLayerBreak.ReadTitleCellStarts(3, ifo);

        Assert.Equal(new[]
        {
            new DvdCellStart(3, 1, 1, 0),
            new DvdCellStart(3, 1, 2, 500),
            new DvdCellStart(3, 2, 1, 1_200)
        }, cells);
    }

    [Fact]
    public void CellTable_OfAnIfoAuthoredByIgnisDisc_IsRead()
    {
        byte[] ifo = DvdIfoGenerator.BuildVtsIfo(1, DvdVideoStandard.Pal, DvdAspectRatio.Widescreen16x9, titleVobTotalSectors: 5_000);

        var cells = DvdCellLayerBreak.ReadTitleCellStarts(1, ifo);

        Assert.Equal(new[] { new DvdCellStart(1, 1, 1, 0) }, cells);
    }

    [Fact]
    public void NotATitleSetIfo_OrATableOutsideTheFile_IsRejected()
    {
        byte[] vmg = TitleSetIfo((1, 1, 0));
        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(vmg, 0);
        Assert.Throws<InvalidDataException>(() => DvdCellLayerBreak.ReadTitleCellStarts(1, vmg));

        byte[] pointerPastEnd = TitleSetIfo((1, 1, 0));
        BinaryPrimitives.WriteUInt32BigEndian(pointerPastEnd.AsSpan(0xE0), 9);
        Assert.Throws<InvalidDataException>(() => DvdCellLayerBreak.ReadTitleCellStarts(1, pointerPastEnd));

        byte[] tooManyEntries = TitleSetIfo((1, 1, 0));
        BinaryPrimitives.WriteUInt32BigEndian(tooManyEntries.AsSpan(2 * 2048 + 4), 100_000);
        Assert.Throws<InvalidDataException>(() => DvdCellLayerBreak.ReadTitleCellStarts(1, tooManyEntries));
    }

    [Fact]
    public void CellStarts_ArePlacedInTheVobFileThatHoldsThem_GapsBetweenFilesAllowed()
    {
        var files = new Dictionary<string, (int Lba, int Sectors)>
        {
            ["VTS_01_0.IFO"] = (300, 3),
            ["VTS_01_1.VOB"] = (1_000, 400),
            ["VTS_01_2.VOB"] = (1_600, 400), // 200 sectors after the end of _1
            ["VTS_02_0.IFO"] = (310, 3),
            ["VTS_02_1.VOB"] = (3_000, 100)
        };
        var ifos = new Dictionary<string, byte[]>
        {
            ["VTS_01_0.IFO"] = TitleSetIfo((1, 1, 0), (1, 2, 399), (1, 3, 400), (1, 4, 550), (1, 5, 900)), // 900: past both files
            ["VTS_02_0.IFO"] = TitleSetIfo((1, 1, 32))
        };

        var positions = DvdCellLayerBreak.PlaceCells(files, name => ifos.GetValueOrDefault(name));

        Assert.Equal(new[] { 1_000, 1_399, 1_600, 1_750, 3_032 }, positions.Select(p => p.Lba));
        Assert.Equal(2, positions[^1].Cell.TitleSet);
    }

    [Fact]
    public void Choose_KeepsOnlyCellStartsThatCanBeALayerBreak_AndPicksTheOneNearestAnEvenSplit()
    {
        const int total = 3_000_000; // half: 1,500,000
        var cell = (int id) => new DvdCellStart(1, 1, id, 0);
        var positions = new[]
        {
            new DvdCellPosition(cell(1), 1_400_000),  // layer 1 would be larger
            new DvdCellPosition(cell(2), 1_500_008),  // not on a 16-sector block
            new DvdCellPosition(cell(3), 1_500_016),
            new DvdCellPosition(cell(4), 1_600_000),
            new DvdCellPosition(cell(5), 2_100_000)   // past layer 0's capacity
        };

        Assert.Equal(new[] { 1_500_016, 1_600_000 }, DvdCellLayerBreak.UsableBreaks(positions, total).Select(p => p.Lba));
        Assert.Equal(1_500_016, DvdCellLayerBreak.Choose(positions, total)!.Lba);
    }

    [Fact]
    public void Choose_FollowsThePickedCell_OrTheUsableOneNearestIt()
    {
        const int total = 3_000_000;
        var cell = (int id) => new DvdCellStart(1, 1, id, 0);
        var positions = new[]
        {
            new DvdCellPosition(cell(3), 1_500_016),
            new DvdCellPosition(cell(4), 1_600_000),
            new DvdCellPosition(cell(6), 1_700_008), // the user's pick, but not on a 16-sector block on the disc
            new DvdCellPosition(cell(7), 1_800_000)
        };

        Assert.Equal(1_600_000, DvdCellLayerBreak.Choose(positions, total, cell(4))!.Lba);
        Assert.Equal(1_800_000, DvdCellLayerBreak.Choose(positions, total, cell(7))!.Lba);
        Assert.Equal(1_800_000, DvdCellLayerBreak.Choose(positions, total, cell(6))!.Lba); // 99,992 away beats 100,008
    }

    [Fact]
    public void Choose_GivesNothing_WhenNoCellStartCanBeTheBreak()
    {
        var positions = new[] { new DvdCellPosition(new DvdCellStart(1, 1, 1, 0), 1_500_008) };

        Assert.Null(DvdCellLayerBreak.Choose(positions, 3_000_000));
        Assert.Null(DvdCellLayerBreak.Choose(positions, 2_000_000)); // fits on a single-layer disc: no break at all
    }

    [Fact]
    public async Task CellsOfABuiltImage_ArePlacedWhereTheVobFilesLandedInIt()
    {
        // The same steps the DVD-Video screen takes on a dual-layer burn: build the image, read where the VIDEO_TS
        // files landed, place the cells. (A dual-layer-sized image is too large for a test; the choice is tested above.)
        var root = CompilationDirectory.CreateRoot();
        var videoTs = root.AddDirectory("VIDEO_TS");
        videoTs.AddFile("VTS_01_0.IFO", TitleSetIfo((1, 1, 0), (1, 2, 40), (2, 1, 70)));
        videoTs.AddFile("VTS_01_1.VOB", new byte[50 * 2048]);
        videoTs.AddFile("VTS_01_2.VOB", new byte[50 * 2048]);

        using var image = new MemoryStream();
        await IsoUdfImageBuilder.BuildAsync(root, image, new IsoBuildOptions(VolumeLabel: "DVD", IncludeUdf: true));
        image.Position = 0;
        using var reader = IsoImageReader.FromStream(image, leaveOpen: true);
        var entries = reader.FindDirectory("/VIDEO_TS")!.Files.ToDictionary(f => f.Name.ToUpperInvariant());
        var files = entries.ToDictionary(e => e.Key, e => (e.Value.StartLba, OpticalConstants.BytesToSectors(e.Value.SizeBytes, 2048)));

        var positions = DvdCellLayerBreak.PlaceCells(files, name => entries.TryGetValue(name, out var f) ? reader.ReadFileBytes(f) : null);

        int vob1 = entries["VTS_01_1.VOB"].StartLba, vob2 = entries["VTS_01_2.VOB"].StartLba;
        Assert.Equal(new[] { vob1, vob1 + 40, vob2 + 20 }, positions.Select(p => p.Lba));
    }
}
