using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// AccurateRip's drive read-offset list and the write offset worked out from it (M3.2). The two records below are the
/// bytes of the GP60NB50 and GP57EW40 entries in DriveOffsets.bin as downloaded on 2026-11-11.
/// </summary>
public class DriveOffsetsTests
{
    private static readonly byte[] Gp60nb50 = Convert.FromHexString(
        "0600484C2D44542D5354202D2044564452414D20475036304E42353000000000000000FF000000000000000000000000000000000000000000000000000000000000000000");
    private static readonly byte[] Gp57ew40 = Convert.FromHexString(
        "0600484C2D44542D5354202D2044564452414D20475035374557343000000000000000E7000000000000000000000000000000000000000000000000000000000000000000");

    [Fact]
    public void Records_AreParsedFromTheRealFile()
    {
        var entries = AccurateRipDriveOffsets.Parse([.. Gp57ew40, .. Gp60nb50]);

        Assert.Equal(AccurateRipDriveOffsets.RecordSize, Gp60nb50.Length);
        Assert.Equal(2, entries.Count);
        Assert.Equal(new DriveOffsetEntry("HL-DT-ST - DVDRAM GP60NB50", 6, 255), entries[1]);
        Assert.Equal(231, entries[0].Submissions);
    }

    [Fact]
    public void NegativeOffsetsAndPartialRecords_AreHandled()
    {
        byte[] record = (byte[])Gp60nb50.Clone();
        record[0] = 0x9A; record[1] = 0xFD; // -614, little-endian
        var entries = AccurateRipDriveOffsets.Parse([.. record, .. new byte[40]]); // a cut-off record at the end is ignored

        Assert.Equal(-614, Assert.Single(entries).ReadOffsetSamples);
    }

    [Theory]
    [InlineData("HL-DT-ST", "DVDRAM GP60NB50")]
    [InlineData("HL-DT-ST ", "DVDRAM GP60NB50    ")] // INQUIRY fields are space-padded
    [InlineData("hl-dt-st", "dvdram  gp60nb50")]
    public void TheDriveIsFoundByItsInquiryNames(string vendor, string product)
    {
        var entry = AccurateRipDriveOffsets.Find(AccurateRipDriveOffsets.Parse([.. Gp57ew40, .. Gp60nb50]), vendor, product);

        Assert.Equal(6, entry?.ReadOffsetSamples);
    }

    [Fact]
    public void UnknownDrive_IsNotFound()
    {
        Assert.Null(AccurateRipDriveOffsets.Find(AccurateRipDriveOffsets.Parse(Gp60nb50), "HL-DT-ST", "DVDRAM GP60NB60"));
    }

    [Fact]
    public void WriteOffset_IsCombinedMinusRead()
    {
        // The GP60NB50 as measured: combined 0 after every direct burn, read +6 from the list, so it writes 6 samples early.
        var plan = WriteOffsetPlan.Resolve(readOffset: 6, "list", combinedOffset: 0, explicitWriteOffset: null);

        Assert.Equal(-6, plan.WriteOffset);
        Assert.Equal(6, plan.ShiftSamples);
    }

    [Fact]
    public void WithoutAReadOffsetOrAMeasurement_NothingIsShifted()
    {
        Assert.Equal(0, WriteOffsetPlan.Resolve(null, null, 0, null).ShiftSamples);
        Assert.Equal(0, WriteOffsetPlan.Resolve(6, "list", null, null).ShiftSamples);
        Assert.Contains("not measured yet", WriteOffsetPlan.Resolve(6, "list", null, null).Explanation);
        Assert.Equal(-12, WriteOffsetPlan.Resolve(6, "list", 0, explicitWriteOffset: 12).ShiftSamples); // as given wins
    }

    [Fact]
    public void Store_KeepsTheLastMeasurement_AndSaysWhenItChanged()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-offsets-").FullName;
        try
        {
            var store = new DriveOffsetStore(Path.Combine(dir, "drive-offsets.json"));
            Assert.Null(store.GetCombinedOffset("A - B"));
            Assert.Null(store.RecordCombinedOffset("A - B", 0));
            Assert.Null(store.RecordCombinedOffset("A - B", 0));
            Assert.Equal(0, store.RecordCombinedOffset("A - B", 4)); // reports the value it replaced
            Assert.Equal(4, new DriveOffsetStore(Path.Combine(dir, "drive-offsets.json")).GetCombinedOffset("A - B"));

            File.WriteAllText(Path.Combine(dir, "drive-offsets.json"), "not json");
            Assert.Null(store.GetCombinedOffset("A - B")); // a damaged file means measuring again, not a crash
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Load_UsesTheCachedCopy_AndWithNoCopyAndNoNetworkIsEmpty()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-offsets-").FullName;
        try
        {
            string cache = Path.Combine(dir, "DriveOffsets.bin");
            Assert.Empty(await AccurateRipDriveOffsets.LoadAsync(null, cache));

            await File.WriteAllBytesAsync(cache, Gp60nb50);
            Assert.Equal(6, Assert.Single(await AccurateRipDriveOffsets.LoadAsync(null, cache)).ReadOffsetSamples);
        }
        finally { Directory.Delete(dir, true); }
    }
}
