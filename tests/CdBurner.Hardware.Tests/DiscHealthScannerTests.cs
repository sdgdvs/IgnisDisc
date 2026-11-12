using CdBurner.Core.Verification;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// The read-health check maps where a CD is weak by reading it once with C2 pointers, as a player meets it. The discs
/// are burned on the simulated drive, then given weak or broken sectors like the old CD-R batch of 2026-11-11.
/// </summary>
public class DiscHealthScannerTests
{
    private const int SectorSize = 2352;

    private static VirtualMmcDrive BurnedDisc(int sectorsPerTrack = 300)
    {
        var drive = new VirtualMmcDrive();
        byte[] Pcm(int seed) { var b = new byte[sectorsPerTrack * SectorSize]; new Random(seed).NextBytes(b); return b; }
        var request = new NativeSaoWriteRequest(
            new[] { new SaoTrack(1, sectorsPerTrack, 150), new SaoTrack(2, sectorsPerTrack) },
            i => new MemoryStream(Pcm(i + 1)), null, null, TestWrite: false);
        var result = new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(request);
        Assert.True(result.Success, result.Message);
        return drive;
    }

    [Fact]
    public void AGoodDisc_ReadsCleanly()
    {
        var drive = BurnedDisc();
        int progressCalls = 0;

        var report = new DiscHealthScanner(drive).Scan((_, _) => progressCalls++);

        Assert.Equal(DiscHealthVerdict.ReadsCleanly, report.Verdict);
        Assert.Equal(600, report.TotalSectors);
        Assert.Equal([1, 2], report.Tracks.Select(t => t.TrackNumber));
        Assert.True(report.C2Available);
        Assert.True(progressCalls > 0);
        Assert.StartsWith("Reads cleanly", report.Describe());
    }

    [Fact]
    public void WeakSectors_AreCountedPerTrack_AndNamed()
    {
        var drive = BurnedDisc();
        drive.MarginalBadReads = int.MaxValue;
        drive.MarginalLbas.UnionWith([320, 321, 450]); // track 2

        var report = new DiscHealthScanner(drive).Scan();

        Assert.Equal(DiscHealthVerdict.Weak, report.Verdict);
        Assert.Equal(3, report.FlaggedSectors);
        Assert.Equal(0, report.Tracks[0].FlaggedSectors);
        Assert.Equal(3, report.Tracks[1].FlaggedSectors);
        Assert.Contains("track 2", report.Describe());
        Assert.Contains("click or a skip", report.Describe());
    }

    [Fact]
    public void AnUnreadableSector_DoesNotHideItsNeighbours()
    {
        var drive = BurnedDisc();
        drive.ReadErrorAtLba = 100;                 // fails the whole 24-sector run it falls in
        drive.MarginalBadReads = int.MaxValue;
        drive.MarginalLbas.Add(101);                // a weak neighbour in the same run

        var report = new DiscHealthScanner(drive).Scan();

        Assert.Equal(DiscHealthVerdict.Damaged, report.Verdict);
        Assert.Equal(1, report.UnreadableSectors);  // only the broken one, after reading the run sector by sector
        Assert.Equal(1, report.FlaggedSectors);
        Assert.Contains("could not be read at all", report.Describe());
    }

    [Fact]
    public void ADriveWithoutC2Pointers_IsReadPlainly_AndSaysSo()
    {
        var drive = BurnedDisc();
        drive.RejectsC2Pointers = true;
        drive.MarginalBadReads = int.MaxValue;
        drive.MarginalLbas.Add(320);

        var report = new DiscHealthScanner(drive).Scan();

        Assert.False(report.C2Available);
        Assert.Equal(DiscHealthVerdict.ReadsCleanly, report.Verdict); // it cannot see a weak sector, only a broken one
        Assert.Contains("does not report uncorrectable errors", report.Describe());
    }

    [Fact]
    public void WeaknessAtTheOuterEdge_IsCalledThat()
    {
        // Two 2-minute tracks: 4 minutes of disc, the weak spots in its last minute, as on the old CD-R batch.
        var drive = BurnedDisc(sectorsPerTrack: 9_000);
        drive.MarginalBadReads = int.MaxValue;
        drive.MarginalLbas.UnionWith(Enumerable.Range(17_000, 40));

        var report = new DiscHealthScanner(drive).Scan();

        Assert.Equal(4, report.ProblemsPerMinute.Count);
        Assert.Equal(40, report.ProblemsPerMinute[3]);
        Assert.Equal(1.0, report.FlaggedShareInOuterFifth);
        Assert.Contains("outer edge", report.Describe());
        Assert.Contains("minute 3", report.Describe());
    }

    [Fact]
    public void ReadsStayInsideWhatAUsbBridgePasses()
    {
        var drive = BurnedDisc();
        drive.MaxTransferBytesOverride = 65_536; // as the GP60NB50's bridge

        var report = new DiscHealthScanner(drive).Scan();

        Assert.Equal(DiscHealthVerdict.ReadsCleanly, report.Verdict); // a 27-sector C2 read would fail every run here
    }
}
