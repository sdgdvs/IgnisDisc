using CdBurner.Core.Audio;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public class NativeSaoWriterTests
{
    private const int TrackSectors = 300;
    private const byte OpWrite = 0x2A, OpModeSelect = 0x55, OpCueSheet = 0x5D, OpSync = 0x35;

    private static byte[] Pcm(int track, int sectors) =>
        Enumerable.Range(0, sectors * 2352).Select(i => (byte)(i * 7 + track * 31)).ToArray();

    private static byte[] Packs() => CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata("Test Album", "Test Artist",
        Tracks: new[] { new CdTextTrackMetadata(1, "One"), new CdTextTrackMetadata(2, "Two") }));

    private static NativeSaoWriteRequest Request(bool cdText = true, bool testWrite = true, int track2Gap = 150, int sectors = TrackSectors) => new(
        new[] { new SaoTrack(1, sectors, 150), new SaoTrack(2, sectors, track2Gap) },
        i => new MemoryStream(Pcm(i + 1, sectors)),
        cdText ? Packs() : null,
        MediaCatalogNumber: null,
        TestWrite: testWrite);

    private static NativeSaoWriteResult Run(VirtualMmcDrive drive, NativeSaoWriteRequest request, CancellationToken ct = default) =>
        new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(request, null, ct);

    [Fact]
    public void TestWrite_WithCdText_SendsLeadInAndAudioAndLeavesDiscBlank()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, Request());

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscStillBlankAfterTestWrite);
        Assert.Equal(0, drive.DiscStatus);
        Assert.True(drive.SessionClosed);

        // Lead-in: from 97:34:23 (LBA -11077) to LBA -151, 96 bytes each, carrying the packs in order.
        Assert.Equal(11077 - 150, result.LeadInSectorsWritten);
        Assert.Equal((11077 - 150) * 96, drive.LeadInWritten.Length);
        byte[] packs = Packs();
        byte[] data = new byte[72];
        CdTextLeadInEncoder.GatherFromRw(drive.LeadInWritten.ToArray().AsSpan(0, 96), data);
        Assert.Equal(packs.AsSpan(0, 72).ToArray(), data);

        // Program area: 150 silent sectors, track 1, 150 silent sectors, track 2.
        byte[] program = drive.ProgramAreaWritten.ToArray();
        Assert.Equal((150 + TrackSectors + 150 + TrackSectors) * 2352, program.Length);
        Assert.All(program.AsSpan(0, 150 * 2352).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(Pcm(1, TrackSectors), program.AsSpan(150 * 2352, TrackSectors * 2352).ToArray());
        Assert.All(program.AsSpan((150 + TrackSectors) * 2352, 150 * 2352).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(Pcm(2, TrackSectors), program.AsSpan((300 + TrackSectors) * 2352).ToArray());

        // Order: write parameters, cue sheet, writes, flush.
        var ops = drive.CommandsReceived;
        int select = ops.IndexOf(OpModeSelect), cue = ops.IndexOf(OpCueSheet), firstWrite = ops.IndexOf(OpWrite);
        Assert.True(select >= 0 && select < cue && cue < firstWrite);
        Assert.Contains(OpSync, ops.Skip(ops.LastIndexOf(OpWrite)));
        Assert.DoesNotContain(OpSync, ops.Take(firstWrite));
        Assert.Equal(0x03, drive.CurrentModePage05[4] & 0x0F);
    }

    [Fact]
    public void TestWrite_WithoutCdText_StartsAtLbaMinus150()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, Request(cdText: false, track2Gap: 0));

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.LeadInSectorsWritten);
        Assert.Equal(0, drive.LeadInWritten.Length);
        Assert.Equal((150 + 2 * TrackSectors) * 2352, drive.ProgramAreaWritten.Length);
        Assert.Equal(0x00, drive.CurrentModePage05[4] & 0x0F);
    }

    [Fact]
    public void RealWrite_IsRefusedBeforeAnyCommandThatChangesTheDrive()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, Request(testWrite: false));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.DoesNotContain(OpModeSelect, drive.CommandsReceived);
        Assert.DoesNotContain(OpCueSheet, drive.CommandsReceived);
        Assert.DoesNotContain(OpWrite, drive.CommandsReceived);
    }

    [Fact]
    public void DriveThatDropsTheTestWriteBit_IsStoppedBeforeTheCueSheet()
    {
        var drive = new VirtualMmcDrive { DropsTestWriteBit = true };

        var result = Run(drive, Request());

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("test-write", result.Message);
        Assert.DoesNotContain(OpCueSheet, drive.CommandsReceived);
        Assert.DoesNotContain(OpWrite, drive.CommandsReceived);
    }

    [Fact]
    public void DriveThatRecordsDuringATestWrite_IsReported()
    {
        var drive = new VirtualMmcDrive { RecordsDuringTestWrite = true };

        var result = Run(drive, Request());

        Assert.False(result.Success);
        Assert.False(result.DiscStillBlankAfterTestWrite);
        Assert.Contains("did not honour the test-write setting", result.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void NonBlankDisc_IsNotTouched(int status)
    {
        var drive = new VirtualMmcDrive { DiscStatus = status };

        var result = Run(drive, Request());

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("not blank", result.Message);
        Assert.DoesNotContain(OpModeSelect, drive.CommandsReceived);
    }

    [Fact]
    public void DvdInTheDrive_IsRefused()
    {
        var drive = new VirtualMmcDrive { Profile = 0x0011 };

        var result = Run(drive, Request());

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("not a CD-R or CD-RW", result.Message);
    }

    [Fact]
    public void DriveWithoutRwSubchannel_RefusesCdTextButWritesWithoutIt()
    {
        var withText = Run(new VirtualMmcDrive { OffersRwSubchannel = false }, Request());
        var withoutText = Run(new VirtualMmcDrive { OffersRwSubchannel = false }, Request(cdText: false));

        Assert.True(withText.StoppedBeforeWriting);
        Assert.Contains("CD-Text", withText.Message);
        Assert.True(withoutText.Success, withoutText.Message);
    }

    [Fact]
    public void DriveWithoutSaoOrTestWrite_IsRefused()
    {
        Assert.True(Run(new VirtualMmcDrive { OffersSessionAtOnce = false }, Request()).StoppedBeforeWriting);
        Assert.True(Run(new VirtualMmcDrive { OffersTestWrite = false }, Request()).StoppedBeforeWriting);
    }

    [Fact]
    public void AudioLongerThanTheDisc_IsRefused()
    {
        var drive = new VirtualMmcDrive { LastLeadOutStart = new Msf(0, 9, 0) }; // room for 525 sectors after LBA 0

        var result = Run(drive, Request());

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("does not fit", result.Message);
        Assert.DoesNotContain(OpModeSelect, drive.CommandsReceived);
    }

    [Fact]
    public void DriveRejectingDataBlockType3_GetsTheFallback()
    {
        var drive = new VirtualMmcDrive { RejectsDataBlockType3 = true };

        var result = Run(drive, Request());

        Assert.True(result.Success, result.Message);
        Assert.Equal((byte)0, result.DataBlockTypeAccepted);
    }

    [Fact]
    public void DriveRejectingTheLeadInTime_GetsTheCueSheetWithoutIt()
    {
        var drive = new VirtualMmcDrive { RejectsLeadInTimeInCueSheet = true };

        var result = Run(drive, Request());

        Assert.True(result.Success, result.Message);
        Assert.Equal(CueSheetVariant.CdTextLeadInWithoutTime, result.CueSheetVariantAccepted);
    }

    [Fact]
    public void WriteErrorPartWay_FailsAndSaysWritingHadStarted()
    {
        var drive = new VirtualMmcDrive { WriteErrorAtLba = 200 };

        var result = Run(drive, Request());

        Assert.False(result.Success);
        Assert.False(result.StoppedBeforeWriting);
        Assert.Contains("while writing at LBA", result.Message);
        Assert.Equal(OpSync, drive.CommandsReceived[^1]);
    }

    [Fact]
    public void FullDriveBuffer_IsRetried()
    {
        var drive = new VirtualMmcDrive { BufferFullEveryNWrites = 5 };

        var result = Run(drive, Request());

        Assert.True(result.Success, result.Message);
        Assert.Equal((150 + 150 + 2 * TrackSectors) * 2352, drive.ProgramAreaWritten.Length);
    }

    [Fact]
    public void DriveSpinningUp_IsWaitedFor()
    {
        var drive = new VirtualMmcDrive { NotReadyResponses = 4 };

        Assert.True(Run(drive, Request()).Success);
    }

    [Fact]
    public void NoDisc_IsReported()
    {
        var result = Run(new VirtualMmcDrive { MediaPresent = false }, Request());

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("no disc", result.Message);
    }

    [Fact]
    public void CancelBeforeStart_WritesNothing()
    {
        var drive = new VirtualMmcDrive();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = Run(drive, Request(), cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Empty(drive.CommandsReceived);
    }

    [Fact]
    public void ShortAudioStream_IsPaddedWithSilence()
    {
        var drive = new VirtualMmcDrive();
        var request = Request(cdText: false) with { OpenTrackAudio = i => new MemoryStream(new byte[2352 * 10]) };

        var result = Run(drive, request);

        Assert.True(result.Success, result.Message);
        Assert.Equal((150 + 150 + 2 * TrackSectors) * 2352, drive.ProgramAreaWritten.Length);
    }

    [Fact]
    public void Log_RecordsEveryCommandAndTheCueSheetBytes()
    {
        var drive = new VirtualMmcDrive();
        var log = new LoggingScsiTransport(drive);

        var result = new NativeSaoAudioWriter(log, log.Note, _ => { }).Write(Request(cdText: false));

        Assert.True(result.Success, result.Message);
        Assert.Equal(drive.CommandsReceived.Count, log.Lines.Count(l => !l.Contains("data out:") && !l.Contains("data in:") && !l.Contains(" # ")));
        int cue = log.Lines.ToList().FindIndex(l => l.Contains("SEND CUE SHEET"));
        Assert.True(cue >= 0);
        Assert.Contains("data out: 01 00 00 01 00 00 00 00 01 01 00 00", log.Lines[cue + 1]);
        Assert.Contains(log.Lines, l => l.Contains("Write parameters confirmed: SAO, test write True"));
    }
}
