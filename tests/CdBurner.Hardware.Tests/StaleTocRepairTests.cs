using CdBurner.Core.Abstractions;
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

    // ---- Session length on DVD (the CD gap rule gave 119,752 for a 102,464-sector track) ----

    private static readonly DiscTocTrack[] TwoSessionDvdR =
    [
        new(1, 0, 131_152 - PhysicalSectorReader.CdInterSessionGap, false), // from the 11,400 rule: 119,752
        new(2, 131_152, 14_464, false)
    ];

    [Fact]
    public void SessionEndFromTheCdGapRule_TakesTheDrivesTrackSize()
    {
        var result = PhysicalSectorReader.CorrectSessionEnds(TwoSessionDvdR, n => n == 1 ? (0, 102_464) : (131_152, 14_464));

        Assert.Equal(102_464, result[0].SectorCount); // as measured on the Verbatim DVD-R, 2026-11-10
        Assert.Equal(14_464, result[1].SectorCount);
    }

    [Fact]
    public void TrackInformationThatDisagrees_IsNotUsed()
    {
        Assert.Equal(119_752, PhysicalSectorReader.CorrectSessionEnds(TwoSessionDvdR, _ => (16, 102_464))[0].SectorCount); // other start
        Assert.Equal(119_752, PhysicalSectorReader.CorrectSessionEnds(TwoSessionDvdR, _ => (0, 140_000))[0].SectorCount); // runs into track 2
        Assert.Equal(119_752, PhysicalSectorReader.CorrectSessionEnds(TwoSessionDvdR, _ => null)[0].SectorCount);       // drive will not say
    }

    // ---- Tracks left out of the table (DVD-R, direct writer, 2026-11-13) ----

    /// <summary>The drive's track list after the third and fourth sessions, by READ TRACK INFORMATION.</summary>
    private static readonly Dictionary<int, (int Start, int Size)> FourSessionDvdR = new()
    {
        [1] = (0, 65_264), [2] = (93_952, 15_408), [3] = (115_520, 15_360), [4] = (137_040, 2_656)
    };

    private static (int Start, int Size)? AskFourSessionDvdR(int number) =>
        FourSessionDvdR.TryGetValue(number, out var t) ? t : null;

    [Fact]
    public void TableMissingASession_IsRebuiltFromTheDrivesTrackList()
    {
        // READ TOC right after the third session, read as ReadTableOfContents gives it: track 1, then "track 2" at the
        // third session's address; the second session (93,952) is missing. READ DISC INFORMATION: 3 recorded tracks.
        DiscTocTrack[] stale = [new(1, 0, 65_264, false), new(2, 115_520, 15_360, false)];

        var result = PhysicalSectorReader.RebuildMissingTracks(stale, 3, AskFourSessionDvdR);

        Assert.Equal(new DiscTocTrack[] { new(1, 0, 65_264, false), new(2, 93_952, 15_408, false), new(3, 115_520, 15_360, false) }, result);
    }

    [Fact]
    public void TableMissingTheNewestSession_GivesItsStartAsTheLastTrack()
    {
        // After the fourth session READ TOC still listed the same two tracks: taken at its word, the import of the last
        // session would read the third session (115,520) and the next session would leave out the fourth one's files.
        DiscTocTrack[] stale = [new(1, 0, 65_264, false), new(2, 115_520, 24_176, false)];

        var result = PhysicalSectorReader.RebuildMissingTracks(stale, 4, AskFourSessionDvdR);

        Assert.Equal(4, result.Count);
        Assert.Equal(137_040, result[^1].StartLba);
        Assert.Equal(2_656, result[^1].SectorCount);
    }

    [Fact]
    public void TableWithEveryTrack_IsLeftAlone_AndTheTracksAreNotAsked()
    {
        DiscTocTrack[] toc = [new(1, 0, 15_415, false), new(2, 26_817, 15_396, false), new(3, 49_115, 15_348, false)];
        int asked = 0;

        var result = PhysicalSectorReader.RebuildMissingTracks(toc, 3, _ => { asked++; return (0, 1); });

        Assert.Same(toc, result);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void TrackListTheDriveWillNotGiveOrThatOverlaps_LeavesTheTableAsItWas()
    {
        DiscTocTrack[] stale = [new(1, 0, 65_264, false), new(2, 115_520, 15_360, false)];

        Assert.Same(stale, PhysicalSectorReader.RebuildMissingTracks(stale, 3, n => n == 2 ? null : AskFourSessionDvdR(n)));
        Assert.Same(stale, PhysicalSectorReader.RebuildMissingTracks(stale, 3, n => n == 2 ? (60_000, 15_408) : AskFourSessionDvdR(n)));
    }

    [Fact]
    public void AudioFlag_IsKeptForTracksTheTableHasAtTheSameStart()
    {
        DiscTocTrack[] stale = [new(1, 0, 20_000, true), new(2, 40_000, 5_000, false)];

        var result = PhysicalSectorReader.RebuildMissingTracks(stale, 3, n => n switch { 1 => (0, 20_000), 2 => (31_400, 2_000), 3 => (40_000, 5_000), _ => null });

        Assert.Equal(new[] { true, false, false }, result.Select(t => t.IsAudio));
    }

    [Fact]
    public void TracksSizedFromTheirNeighbours_AreLeftAlone_AndTheDriveIsNotAsked()
    {
        DiscTocTrack[] cd = [new(1, 0, 20_000, true), new(2, 20_000, 30_000, true), new(3, 50_000, 1_000, true)];
        int asked = 0;

        var result = PhysicalSectorReader.CorrectSessionEnds(cd, _ => { asked++; return (0, 1); });

        Assert.Same(cd, result);
        Assert.Equal(0, asked);
    }
}
