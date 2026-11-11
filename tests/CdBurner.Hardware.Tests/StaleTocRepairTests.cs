using CdBurner.Hardware.Scsi;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// A drive can go on reporting an old table of contents after a session is added (measured on a DVD-R: the new
/// track at LBA 0 until the disc was reloaded). Taken at its word, the app would look for the newest session's
/// directory at the start of the disc and import the wrong one when the next session is added.
/// </summary>
public class StaleTocRepairTests
{
    private const int LeadOut = 0xAA;

    [Fact]
    public void TracksInOrder_AreLeftAlone_AndTheDriveIsNotAsked()
    {
        var entries = new[] { (1, 0, true), (2, 131_152, true), (LeadOut, 145_616, true) };
        int asked = 0;

        var result = PhysicalSectorReader.RepairTrackStarts(entries, _ => { asked++; return (999, 999); });

        Assert.Equal(0, asked);
        Assert.Equal(new[] { 0, 131_152, 145_616 }, result.Select(r => r.Lba));
        Assert.All(result, r => Assert.Null(r.Size));
    }

    [Fact]
    public void NewTrackReportedAtTheOldAddress_IsPlacedByItsTrackInformation()
    {
        // As the drive answered right after the second session was written.
        var entries = new[] { (1, 0, true), (2, 0, true), (LeadOut, 145_616, true) };
        var trackInformation = new Dictionary<int, (int Start, int Size)> { [1] = (0, 102_464), [2] = (131_152, 14_464) };

        var result = PhysicalSectorReader.RepairTrackStarts(entries, n => trackInformation.TryGetValue(n, out var t) ? t : null);

        Assert.Equal(new (int, int, bool, int?)[] { (1, 0, true, 102_464), (2, 131_152, true, 14_464), (LeadOut, 145_616, true, null) }, result);
    }

    [Fact]
    public void DriveThatWillNotGiveTrackInformation_LeavesTheTableAsItWas()
    {
        var entries = new[] { (1, 0, true), (2, 0, true), (LeadOut, 145_616, true) };

        var result = PhysicalSectorReader.RepairTrackStarts(entries, _ => null);

        Assert.Equal(new[] { 0, 0, 145_616 }, result.Select(r => r.Lba));
        Assert.All(result, r => Assert.Null(r.Size));
    }

    [Fact]
    public void NonsenseTrackInformation_IsNotUsed()
    {
        var entries = new[] { (1, 0, true), (2, 0, true), (LeadOut, 145_616, true) };

        var result = PhysicalSectorReader.RepairTrackStarts(entries, n => n == 2 ? (-5, 0) : (0, 102_464));

        Assert.Equal((2, 0, true, (int?)null), result[1]);
        Assert.Equal((1, 0, true, (int?)102_464), result[0]);
    }
}
