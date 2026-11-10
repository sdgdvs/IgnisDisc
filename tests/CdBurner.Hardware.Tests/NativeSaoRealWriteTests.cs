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
        Assert.Contains("1 of ", result.Message);
        Assert.Contains("track 2", result.Message);
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
}
