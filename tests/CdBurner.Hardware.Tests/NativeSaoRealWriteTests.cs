using CdBurner.Core.Audio;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Real (laser-on) Session-at-Once writes against the simulated drive: what ends up on the disc, and whether the
/// read-back check that follows every real write passes on a good disc and fails on each kind of bad one.
/// </summary>
public class NativeSaoRealWriteTests
{
    private const int SectorSize = 2352;
    private const int TrackSectors = 300;

    /// <summary>Noise-like audio, different per track, so a position in it can be found without ambiguity.</summary>
    private static byte[] Pcm(int seed, int sectors)
    {
        byte[] pcm = new byte[sectors * SectorSize];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static byte[] Packs() => CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata("Test Album", "Test Artist",
        Tracks: new[] { new CdTextTrackMetadata(1, "One"), new CdTextTrackMetadata(2, "Two") }));

    private static NativeSaoWriteRequest TwoTracks(bool cdText = true, SaoTrack? second = null, int? speed = null) => new(
        new[] { new SaoTrack(1, TrackSectors, 150), second ?? new SaoTrack(2, TrackSectors) },
        i => new MemoryStream(Pcm(i + 1, TrackSectors + (i == 1 ? second?.PreGapAudioSectors ?? 0 : 0))),
        cdText ? Packs() : null,
        MediaCatalogNumber: null,
        TestWrite: false,
        WriteSpeedKiloBytesPerSecond: speed);

    private static NativeSaoWriteResult Run(VirtualMmcDrive drive, NativeSaoWriteRequest request) =>
        new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(request);

    [Fact]
    public void RealWrite_RecordsTheDisc_AndTheReadBackCheckPasses()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, TwoTracks());

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscRecorded);
        Assert.False(result.StoppedBeforeWriting);
        Assert.Null(result.DiscStillBlankAfterTestWrite);
        Assert.NotNull(result.Verification);
        Assert.True(result.Verification.Passed);
        Assert.Equal(0, result.Verification.OffsetSamples);
        Assert.Equal(2L * TrackSectors * 588, result.Verification.SamplesCompared);

        Assert.True(drive.Recorded);
        Assert.Equal(2, drive.DiscStatus);
        Assert.False((drive.CurrentModePage05[2] & 0x10) != 0, "the Test Write bit must be off for a real write");
        Assert.Equal(new[] { 0, TrackSectors }, drive.RecordedTracks.Select(t => t.Index01Lba));
        Assert.Equal(2 * TrackSectors, drive.RecordedLeadOutLba);

        byte[] program = drive.ProgramAreaWritten.ToArray();
        Assert.Equal((150 + 2 * TrackSectors) * SectorSize, program.Length);
        Assert.All(program.Take(150 * SectorSize), b => Assert.Equal(0, b));
        Assert.Equal(Pcm(1, TrackSectors), program.AsSpan(150 * SectorSize, TrackSectors * SectorSize).ToArray());
        Assert.Equal(Pcm(2, TrackSectors), program.AsSpan((150 + TrackSectors) * SectorSize).ToArray());
    }

    [Fact]
    public void TestWriteOfTheSameRequest_LeavesTheDiscBlank_AndRunsNoReadBackCheck()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, TwoTracks() with { TestWrite = true });

        Assert.True(result.Success, result.Message);
        Assert.False(result.DiscRecorded);
        Assert.Null(result.Verification);
        Assert.False(drive.Recorded);
    }

    [Theory]
    [InlineData(-2000)]
    [InlineData(-30)]
    [InlineData(6)]
    [InlineData(667)]
    [InlineData(2352)]
    public void DriveOffset_IsFoundAndTheAudioStillCompares(int offsetSamples)
    {
        var drive = new VirtualMmcDrive { ReadOffsetSamples = offsetSamples };

        var result = Run(drive, TwoTracks());

        Assert.True(result.Success, result.Message);
        Assert.Equal(offsetSamples, result.Verification!.OffsetSamples);
        Assert.Equal(2L * TrackSectors * 588 - Math.Abs(offsetSamples), result.Verification.SamplesCompared);
    }

    [Fact]
    public void DriveOffsetLargerThanTheSearchRange_FailsTheCheck()
    {
        var drive = new VirtualMmcDrive { ReadOffsetSamples = NativeSaoVerifier.MaxOffsetSamples + 1 };

        var result = Run(drive, TwoTracks());

        Assert.False(result.Success);
        Assert.True(result.DiscRecorded);
        Assert.Contains("does not match what was written", result.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(123)]
    public void OneChangedSampleOnTheDisc_FailsTheCheck_AndNamesTheTrack(int offsetSamples)
    {
        var drive = new VirtualMmcDrive { ReadBackDifferenceAtLba = TrackSectors + 40, ReadOffsetSamples = offsetSamples };

        var result = Run(drive, TwoTracks());

        Assert.False(result.Success);
        Assert.True(result.DiscRecorded);
        Assert.False(result.Verification!.Passed);
        // Same wrong data on every careful re-read, with clean C2 pointers: a bad write, not a bad read.
        Assert.Equal(1, result.Verification.WrittenWrongSectors);
        Assert.Equal(0, result.Verification.HardToReadSectors);
        Assert.Contains("written wrong", result.Message);
        Assert.Contains("track 2", result.Message);
        Assert.True(result.Verification.SamplesCompared > 0); // reported on failure too
        Assert.Equal(offsetSamples, result.Verification.OffsetSamples);
    }

    // ---- Hard to read is not written wrong (the old CD-R batch, 2026-11-11) ----

    [Theory]
    [InlineData(0)]
    [InlineData(123)]
    [InlineData(-700)]
    public void MarginalSectors_ThatReadRightOnACarefulReRead_PassWithAWarning(int offsetSamples)
    {
        // Like the outer edge of a weak CD-R: the fast pass gets interpolated audio with GOOD status, a careful re-read
        // gets the right audio.
        var drive = new VirtualMmcDrive { ReadOffsetSamples = offsetSamples, MarginalBadReads = 2 };
        drive.MarginalLbas.UnionWith([TrackSectors + 40, TrackSectors + 41, TrackSectors + 90]);

        var result = Run(drive, TwoTracks());

        Assert.True(result.Success, result.Message);
        var v = result.Verification!;
        Assert.True(v.Passed);
        Assert.Equal(3, v.HardToReadSectors);
        Assert.Equal(0, v.WrittenWrongSectors);
        Assert.Equal(0, v.UnreadableSectors);
        Assert.NotNull(v.Warning);
        Assert.Contains("hard to read", v.Warning);
        Assert.Contains("track(s) 2", v.Warning);
        Assert.Contains(v.Warning, result.Message);
        Assert.True(drive.C2ReadCommands > 0);
    }


    [Fact]
    public void ManyMarginalSectors_BeyondTheOldLimit_AreStillReReadAndPass()
    {
        // As the old CD-R batch straight after a burn (2026-11-11): thousands of sectors misread in the fast pass,
        // all of them right on a careful re-read. The old limit of 4,500 sectors failed such a disc unread.
        const int sectors = 2_500;
        var request = new NativeSaoWriteRequest(
            new[] { new SaoTrack(1, sectors, 150), new SaoTrack(2, sectors) },
            i => new MemoryStream(Pcm(i + 1, sectors)), null, null, TestWrite: false);
        var drive = new VirtualMmcDrive { MarginalBadReads = 1 };
        drive.MarginalLbas.UnionWith(Enumerable.Range(100, 4_701));

        var result = Run(drive, request);

        Assert.True(result.Success, result.Message);
        Assert.Equal(4_701, result.Verification!.HardToReadSectors);
        Assert.Contains("hard to read", result.Verification.Warning);
    }
    [Fact]
    public void MarginalSector_ThatNeverReadsRight_FailsAsUnreadable()
    {
        var drive = new VirtualMmcDrive { MarginalBadReads = int.MaxValue };
        drive.MarginalLbas.Add(TrackSectors + 40);

        var result = Run(drive, TwoTracks());

        Assert.False(result.Success);
        Assert.Equal(1, result.Verification!.UnreadableSectors);
        Assert.Equal(0, result.Verification.WrittenWrongSectors);
        Assert.Contains("never read back right", result.Message);
        Assert.Contains("read errors every time", result.Message);
    }

    [Fact]
    public void WithoutC2Pointers_AMarginalSectorStillResolves_ButAPermanentDifferenceIsNotCalledABadWrite()
    {
        var marginal = new VirtualMmcDrive { RejectsC2Pointers = true, MarginalBadReads = 2 };
        marginal.MarginalLbas.Add(TrackSectors + 40);
        var passed = Run(marginal, TwoTracks());
        Assert.True(passed.Success, passed.Message);
        Assert.Equal(1, passed.Verification!.HardToReadSectors);

        var changed = Run(new VirtualMmcDrive { RejectsC2Pointers = true, ReadBackDifferenceAtLba = TrackSectors + 40 }, TwoTracks());
        Assert.False(changed.Success);
        Assert.Equal(0, changed.Verification!.WrittenWrongSectors);
        Assert.Equal(1, changed.Verification.UnreadableSectors);
        Assert.Contains("cannot tell a bad write from a bad read", changed.Message);
    }

    [Fact]
    public void BothKindsOnOneDisc_FailAsWrittenWrong_AndCountTheHardToReadOnes()
    {
        var drive = new VirtualMmcDrive { ReadBackDifferenceAtLba = TrackSectors + 10, MarginalBadReads = 1 };
        drive.MarginalLbas.Add(TrackSectors + 90);

        var result = Run(drive, TwoTracks());

        Assert.False(result.Success);
        Assert.Equal(1, result.Verification!.WrittenWrongSectors);
        Assert.Equal(1, result.Verification.HardToReadSectors);
    }

    [Fact]
    public void UnreadableSectorOnTheNewDisc_FailsTheCheck()
    {
        var result = Run(new VirtualMmcDrive { ReadErrorAtLba = 250 }, TwoTracks());

        Assert.False(result.Success);
        Assert.Contains("could not be read back", result.Message);
    }

    [Fact]
    public void MissingCdTextOnTheNewDisc_FailsTheCheck()
    {
        var result = Run(new VirtualMmcDrive { LosesCdText = true }, TwoTracks());

        Assert.False(result.Success);
        Assert.Contains("no CD-Text", result.Message);
    }

    [Fact]
    public void WithoutCdText_NoCdTextIsExpectedBack()
    {
        var result = Run(new VirtualMmcDrive { LosesCdText = true }, TwoTracks(cdText: false));

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void TrackStartingAtTheWrongAddress_FailsTheCheck()
    {
        var result = Run(new VirtualMmcDrive { ShiftsTrackStartsInToc = true }, TwoTracks());

        Assert.False(result.Success);
        Assert.Contains("Track 2 starts at LBA", result.Message);
    }

    [Fact]
    public void PreEmphasisAndCopyBits_GoIntoTheCueSheet_AndAreCheckedInTheToc()
    {
        var flagged = new SaoTrack(2, TrackSectors, PreEmphasis: true, CopyPermitted: true);
        var drive = new VirtualMmcDrive();

        var result = Run(drive, TwoTracks(second: flagged));

        Assert.True(result.Success, result.Message);
        Assert.Equal(new byte[] { 0x00, 0x30 }, drive.RecordedTracks.Select(t => t.CueControl));

        // Lead-in, track 1 INDEX 00, track 1 INDEX 01, track 2 INDEX 01, lead-out.
        Assert.Equal(new byte[] { 0x01, 0x01, 0x01, 0x31, 0x01 }, drive.RecordedCueSheet!.Chunk(8).Select(e => e[0]).ToArray());

        var dropped = Run(new VirtualMmcDrive { DropsControlBits = true }, TwoTracks(second: flagged));
        Assert.False(dropped.Success);
        Assert.Contains("control bits", dropped.Message);
    }

    [Fact]
    public void AudioInTheGapBeforeTrackTwo_IsWrittenAsIndexZero_WithNoSilenceAdded()
    {
        var gapped = new SaoTrack(2, TrackSectors, PreGapSectors: 150, PreGapAudioSectors: 150);
        var drive = new VirtualMmcDrive();

        var result = Run(drive, TwoTracks(second: gapped));

        Assert.True(result.Success, result.Message);
        // Track 2 begins (INDEX 01) 150 sectors after track 1's audio ends; those 150 sectors are track 2's INDEX 00.
        Assert.Equal(new[] { 0, TrackSectors + 150 }, drive.RecordedTracks.Select(t => t.Index01Lba));
        Assert.Equal(2 * TrackSectors + 150, drive.RecordedLeadOutLba);

        byte[] program = drive.ProgramAreaWritten.ToArray();
        byte[] trackTwoStream = Pcm(2, TrackSectors + 150);
        Assert.Equal(trackTwoStream, program.AsSpan((150 + TrackSectors) * SectorSize).ToArray());

        byte[][] entries = drive.RecordedCueSheet!.Chunk(8).ToArray();
        byte[] indexZero = entries.Single(e => e[1] == 2 && e[2] == 0);
        Assert.Equal(Msf.FromLba(TrackSectors), new Msf(indexZero[5], indexZero[6], indexZero[7]));
    }

    [Fact]
    public void HiddenAudioBeforeTrackOne_FollowsTwoSecondsOfSilence_AndIsCompared()
    {
        const int hidden = 225;
        var request = new NativeSaoWriteRequest(
            new[] { new SaoTrack(1, TrackSectors, PreGapAudioSectors: hidden), new SaoTrack(2, TrackSectors) },
            i => new MemoryStream(Pcm(i + 1, TrackSectors + (i == 0 ? hidden : 0))),
            CdTextPacks: null,
            MediaCatalogNumber: null,
            TestWrite: false);
        var drive = new VirtualMmcDrive();

        var result = Run(drive, request);

        Assert.True(result.Success, result.Message);
        // Track 1's INDEX 01 sits after the hidden audio; the hidden audio starts at LBA 0.
        Assert.Equal(new[] { hidden, hidden + TrackSectors }, drive.RecordedTracks.Select(t => t.Index01Lba));
        byte[] program = drive.ProgramAreaWritten.ToArray();
        Assert.All(program.Take(150 * SectorSize), b => Assert.Equal(0, b));
        Assert.Equal(Pcm(1, TrackSectors + hidden), program.AsSpan(150 * SectorSize, (hidden + TrackSectors) * SectorSize).ToArray());
        Assert.Equal((long)(hidden + 2 * TrackSectors) * 588, result.Verification!.SamplesCompared);
    }

    [Fact]
    public void MorePreGapAudioThanPreGap_IsRefusedBeforeWriting()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, TwoTracks(second: new SaoTrack(2, TrackSectors, PreGapSectors: 10, PreGapAudioSectors: 11)));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.False(drive.Recorded);
    }

    [Fact]
    public void WriteSpeed_IsSentToTheDrive_AndARefusalDoesNotStopTheBurn()
    {
        var drive = new VirtualMmcDrive();
        Assert.True(Run(drive, TwoTracks(speed: 1764)).Success);
        Assert.Equal(1764, drive.WriteSpeedSet);

        var refusing = new VirtualMmcDrive { RejectsSetSpeed = true };
        Assert.True(Run(refusing, TwoTracks(speed: 1764)).Success);
        Assert.Null(refusing.WriteSpeedSet);
    }

    [Fact]
    public void WriteErrorPartWayThroughARealWrite_ReportsARuinedDisc_AndSkipsTheCheck()
    {
        var drive = new VirtualMmcDrive { WriteErrorAtLba = 200 };

        var result = Run(drive, TwoTracks());

        Assert.False(result.Success);
        Assert.False(result.StoppedBeforeWriting);
        Assert.False(result.DiscRecorded);
        Assert.Null(result.Verification);
        Assert.True(drive.Recorded, "a real write that stops part way has still marked the disc");
    }

    [Fact]
    public void DigitalSilence_ComparesWithoutAShift()
    {
        var request = new NativeSaoWriteRequest(
            new[] { new SaoTrack(1, TrackSectors) },
            _ => new MemoryStream(new byte[TrackSectors * SectorSize]),
            CdTextPacks: null,
            MediaCatalogNumber: null,
            TestWrite: false);

        var result = Run(new VirtualMmcDrive { ReadOffsetSamples = 50 }, request);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.Verification!.OffsetSamples);
    }

    [Fact]
    public void DiscThatIsNotBlank_IsNotTouchedByARealWrite()
    {
        var drive = new VirtualMmcDrive { DiscStatus = 2 };

        var result = Run(drive, TwoTracks());

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    private const string Catalog = "1234567890128";

    private static NativeSaoWriteRequest WithCodes(string? catalog = Catalog) =>
        TwoTracks(second: new SaoTrack(2, TrackSectors, Isrc: "ZZTST2600002")) with { MediaCatalogNumber = catalog };

    [Fact]
    public void CatalogNumberAndIsrc_AreReadBackAndCompared()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, WithCodes());

        Assert.True(result.Success, result.Message);
        Assert.Contains("Catalog number and 1 ISRC code(s) match.", result.Verification!.Message);
        Assert.Contains((byte)0x42, drive.CommandsReceived);
    }

    [Fact]
    public void WithoutCodes_TheDriveIsNotAskedForThem()
    {
        var drive = new VirtualMmcDrive { LosesCatalogAndIsrc = true };

        var result = Run(drive, TwoTracks());

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain((byte)0x42, drive.CommandsReceived);
        Assert.DoesNotContain("ISRC", result.Verification!.Message);
    }

    [Fact]
    public void CodesMissingOnTheNewDisc_FailTheCheck()
    {
        var result = Run(new VirtualMmcDrive { LosesCatalogAndIsrc = true }, WithCodes());

        Assert.False(result.Success);
        Assert.True(result.DiscRecorded);
        Assert.Contains("catalog number " + Catalog, result.Verification!.Message);
        Assert.Contains("does not show one", result.Verification.Message);

        var isrcOnly = Run(new VirtualMmcDrive { LosesCatalogAndIsrc = true }, WithCodes(catalog: null));
        Assert.False(isrcOnly.Success);
        Assert.Contains("ISRC ZZTST2600002 was written for track 2", isrcOnly.Verification!.Message);
    }

    [Fact]
    public void IsrcThatReadsBackDifferently_FailsTheCheck_AndNamesTheTrack()
    {
        var result = Run(new VirtualMmcDrive { ChangesIsrcOfTrack = 2 }, WithCodes());

        Assert.False(result.Success);
        Assert.Contains("ISRC of track 2 reads back as ZZTST2600009", result.Verification!.Message);
    }

    [Fact]
    public void DriveThatCannotReportCodes_DoesNotFailTheBurn_ButSaysTheyWereNotChecked()
    {
        var result = Run(new VirtualMmcDrive { RejectsReadSubChannel = true }, WithCodes());

        Assert.True(result.Success, result.Message);
        Assert.Contains("could not be checked", result.Verification!.Message);
        Assert.DoesNotContain("match.", result.Verification.Message);
    }
}
