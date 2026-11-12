using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Copying a mixed-mode CD (track 1 data, then audio) drive to drive. With the direct writer the data track is read
/// as 2,048-byte user data and written ahead of the audio, so the copy has the source's layout and content; without
/// it only the audio is copied, and the result says so.
/// </summary>
public sealed class MixedModeCopyTests : IDisposable
{
    private const int Raw = 2352;
    private const int DataSectors = 400;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-mixcopy-" + Guid.NewGuid().ToString("N"));

    public MixedModeCopyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private static byte[] Noise(int seed, int frames)
    {
        var pcm = new byte[frames * Raw];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    /// <summary>Burns a mixed-mode CD (400 data sectors, then 300 and 320 frames of audio) with the direct writer.</summary>
    private async Task<(VirtualDiscRecorder Recorder, VirtualMmcDrive Drive)> SourceDisc()
    {
        var user = new byte[DataSectors * 2048];
        new Random(11).NextBytes(user);
        var bin = new List<byte>();
        for (int lba = 0; lba < DataSectors + 150; lba++)
        {
            bin.AddRange(RawSectorCodec.Convert2048To2352Mode1(lba < DataSectors ? user.AsSpan(lba * 2048, 2048) : new byte[2048], lba));
        }
        bin.AddRange(new byte[150 * Raw]);
        bin.AddRange(Noise(2, 300));
        bin.AddRange(Noise(3, 320));
        File.WriteAllBytes(Path.Combine(_dir, "src.bin"), bin.ToArray());
        string cue = Path.Combine(_dir, "src.cue");
        File.WriteAllLines(cue, new[]
        {
            "TITLE \"Album\"", "PERFORMER \"Band\"",
            "FILE \"src.bin\" BINARY", "  TRACK 01 MODE1/2352", "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO", "    TITLE \"Two\"", "    INDEX 00 00:07:25", "    INDEX 01 00:09:25",
            "  TRACK 03 AUDIO", "    TITLE \"Three\"", "    INDEX 01 00:13:25"
        });

        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        compilation.ImportCueSheet(cue);
        var recorder = new VirtualDiscRecorder { UniqueId = "VDRV-SRC" };
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        VirtualMmcDrive? drive = null;
        var engine = new NativeAudioBurnEngine(_dir) { Wait = _ => { }, ConfigureVirtualDrive = d => drive = d };
        var burned = await new AudioCdBurnService(engine).BurnAsync(recorder, compilation, new AudioBurnOptions { UseDiscAtOnce = true });
        Assert.True(burned.Success, burned.ErrorMessage);
        return (recorder, drive!);
    }

    private static VirtualDiscRecorder Target()
    {
        var target = new VirtualDiscRecorder { UniqueId = "VDRV-DST" };
        target.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        return target;
    }

    [Fact]
    public async Task WithTheDirectWriter_TheCopyHasTheSourcesDataTrackAudioAndLayout()
    {
        var (source, sourceDrive) = await SourceDisc();
        var target = Target();
        VirtualMmcDrive? targetDrive = null;
        var writer = new NativeAudioBurnEngine(_dir) { Wait = _ => { }, ConfigureVirtualDrive = d => targetDrive = d };

        var result = await new DiscCopyService(new VirtualBurnEngine(source)).CopyDiscAsync(source, target,
            new DiscCopyOptions { MixedModeWriter = writer });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.IsMixedMode);
        Assert.True(result.CopiedDataTrack);
        Assert.Null(result.WarningMessage);
        Assert.True(writer.LastResult!.Verification!.Passed, writer.LastResult.Verification.Message);
        Assert.Equal(sourceDrive.RecordedTracks.Select(t => (t.Index01Lba, t.CueControl)), targetDrive!.RecordedTracks.Select(t => (t.Index01Lba, t.CueControl)));
        Assert.Equal(sourceDrive.RecordedLeadOutLba, targetDrive.RecordedLeadOutLba);
        Assert.Equal(sourceDrive.ProgramAreaWritten.ToArray(), targetDrive.ProgramAreaWritten.ToArray());
        Assert.NotNull(source.CurrentMedium!.CdTextBytes);
        Assert.Equal(source.CurrentMedium.CdTextBytes, target.CurrentMedium!.CdTextBytes);   // track 1's CD-Text included
    }

    [Fact]
    public async Task WithoutTheDirectWriter_OnlyTheAudioIsCopied_AndTheResultSaysHowToCopyTheData()
    {
        var (source, _) = await SourceDisc();
        var target = Target();

        var result = await new DiscCopyService(new VirtualBurnEngine(source)).CopyDiscAsync(source, target);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.IsMixedMode);
        Assert.False(result.CopiedDataTrack);
        Assert.Contains("Turn on direct drive commands", result.WarningMessage);
        var tracks = Assert.Single(target.CurrentMedium!.Sessions).Tracks;
        Assert.Equal(2, tracks.Count);                    // the two audio tracks; the data track is not ripped as audio
        Assert.All(tracks, t => Assert.True(t.IsAudio));
    }

    [Fact]
    public async Task AnUnreadableDataSector_StopsTheCopy_BeforeTheTargetIsTouched()
    {
        var (source, _) = await SourceDisc();
        source.CurrentMedium!.SimulatedBadLbas.Add(123);
        var target = Target();
        var writer = new NativeAudioBurnEngine(_dir) { Wait = _ => { } };

        var result = await new DiscCopyService(new VirtualBurnEngine(source)).CopyDiscAsync(source, target,
            new DiscCopyOptions { MixedModeWriter = writer });

        Assert.False(result.Success);
        Assert.Contains("The data track could not be read", result.ErrorMessage);
        Assert.Contains("Nothing was written", result.ErrorMessage);
        Assert.True(target.QueryMediaStatus().IsBlank);
    }

    private async Task<(DiscCopyResult Result, VirtualDiscRecorder Target, VirtualMmcDrive Source, VirtualMmcDrive? Copy)> CopyWithMisread(
        int badReads, bool allow, Func<string, CancellationToken, Task<bool>>? confirm = null)
    {
        var (source, sourceDrive) = await SourceDisc();
        source.CurrentMedium!.SimulatedC2Errors[800] = badReads;   // an audio sector in track 2
        var target = Target();
        VirtualMmcDrive? copyDrive = null;
        var writer = new NativeAudioBurnEngine(_dir) { Wait = _ => { }, ConfigureVirtualDrive = d => copyDrive = d };
        var result = await new DiscCopyService(new VirtualBurnEngine(source)).CopyDiscAsync(source, target,
            new DiscCopyOptions { MixedModeWriter = writer, AllowSuspectAudio = allow, ConfirmSuspectAudioAsync = confirm });
        return (result, target, sourceDrive, copyDrive);
    }

    [Fact]
    public async Task ASectorThatKeepsReadingWrong_StopsTheCopy_BeforeTheTargetIsTouched()
    {
        var (result, target, _, _) = await CopyWithMisread(badReads: 1000, allow: false);

        Assert.False(result.Success);
        Assert.Contains("1 audio sector(s) of the source still read with errors after re-reading (track 2: 1)", result.ErrorMessage);
        Assert.Contains("Nothing was written", result.ErrorMessage);
        Assert.True(target.QueryMediaStatus().IsBlank);
    }

    [Fact]
    public async Task CopyAnyway_WritesTheSectorAsRead_AndSaysSo()
    {
        var (result, _, _, _) = await CopyWithMisread(badReads: 1000, allow: true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("1 audio sector(s) were copied as read, with errors", result.WarningMessage);
    }

    [Fact]
    public async Task AskedAndAnsweredYes_CopiesAsRead_AndTheQuestionNamesTheTrack()
    {
        string? asked = null;

        var (result, _, _, _) = await CopyWithMisread(badReads: 1000, allow: false,
            confirm: (problem, _) => { asked = problem; return Task.FromResult(true); });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("(track 2: 1)", asked);
        Assert.Contains("copied as read, with errors", result.WarningMessage);
    }

    [Fact]
    public async Task AskedAndAnsweredNo_Stops_BeforeTheTargetIsTouched()
    {
        var (result, target, _, _) = await CopyWithMisread(badReads: 1000, allow: false, confirm: (_, _) => Task.FromResult(false));

        Assert.False(result.Success);
        Assert.Contains("Nothing was written", result.ErrorMessage);
        Assert.True(target.QueryMediaStatus().IsBlank);
    }

    [Fact]
    public async Task AMisreadThatTheReReadsRecover_LeavesAnExactCopy()
    {
        var (result, _, sourceDrive, copyDrive) = await CopyWithMisread(badReads: 1, allow: false);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.WarningMessage);
        Assert.Equal(sourceDrive.ProgramAreaWritten.ToArray(), copyDrive!.ProgramAreaWritten.ToArray());
    }

    [Fact]
    public async Task ReadingTheDataTrack_GivesItsUserData_NotTheRawSectors()
    {
        var (source, _) = await SourceDisc();
        var sectors = new List<byte[]>();

        var read = await new VirtualBurnEngine(source).ReadSectorsAsync(source, 0, 2, raw2352Mode: false,
            (_, data) => { sectors.Add(data.ToArray()); return ValueTask.CompletedTask; }, null, CancellationToken.None);

        Assert.True(read.Success);
        var user = new byte[DataSectors * 2048];
        new Random(11).NextBytes(user);
        Assert.Equal(user.AsSpan(0, 2048).ToArray(), sectors[0]);
        Assert.Equal(user.AsSpan(2048, 2048).ToArray(), sectors[1]);
    }
}
