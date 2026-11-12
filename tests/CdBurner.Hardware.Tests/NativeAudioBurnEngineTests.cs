using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// The whole direct-writing path on the simulated drive: the real write, the read-back check, the optional test
/// write first, and what the virtual disc holds afterwards. The CUE sheet tests go through
/// <see cref="AudioCdBurnService"/>, so they also cover how INDEX 00 marks, hidden audio, flags and the catalog
/// number travel from a CUE file to the cue sheet the drive receives.
/// </summary>
public sealed class NativeAudioBurnEngineTests : IDisposable
{
    private const int SectorSize = 2352;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeAudioBurnEngineTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] Pcm(int seed, int sectors)
    {
        byte[] pcm = new byte[sectors * SectorSize];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static List<AudioTrackSource> Tracks(int sectors = 300) =>
    [
        new AudioTrackSource { TrackNumber = 1, Title = "One", Performer = "Artist", PreGapFrames = 150, TotalPcmBytes = (long)sectors * SectorSize, PcmStreamFactory = () => new MemoryStream(Pcm(1, sectors)) },
        new AudioTrackSource { TrackNumber = 2, Title = "Two", Performer = "Artist", TotalPcmBytes = (long)sectors * SectorSize, PcmStreamFactory = () => new MemoryStream(Pcm(2, sectors)) }
    ];

    private static byte[] Packs() => CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata("Album", "Artist",
        Tracks: new[] { new CdTextTrackMetadata(1, "One"), new CdTextTrackMetadata(2, "Two") }));

    private static AudioBurnOptions Options(bool simulate = false) =>
        new() { Simulate = simulate, WriteCdText = true, CdTextBytes = Packs(), UseDiscAtOnce = true };

    private (NativeAudioBurnEngine Engine, Func<VirtualMmcDrive?> Drive) Engine(bool testFirst = false, Action<VirtualMmcDrive>? configure = null)
    {
        VirtualMmcDrive? seen = null;
        var engine = new NativeAudioBurnEngine(_dir)
        {
            TestWriteFirst = testFirst,
            ConfigureVirtualDrive = d =>
            {
                seen = d;
                configure?.Invoke(d);
            }
        };
        return (engine, () => seen);
    }

    private static VirtualDiscRecorder Recorder(VirtualDiscMedium medium)
    {
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        return recorder;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealWrite_LeavesAVerifiedAudioDisc(bool rewritable)
    {
        using var recorder = Recorder(rewritable ? VirtualDiscMedium.CreateBlankCdRw700() : VirtualDiscMedium.CreateBlankCdR700());
        var (engine, _) = Engine(configure: d => d.ReadOffsetSamples = 97);

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(engine.LastTestWritePassed);
        Assert.True(engine.LastResult!.DiscRecorded);
        Assert.True(engine.LastResult.Verification!.Passed);
        Assert.Equal(97, engine.LastResult.Verification.OffsetSamples);

        var medium = recorder.CurrentMedium!;
        Assert.False(medium.IsBlank);
        Assert.True(medium.IsFinalized);
        Assert.Equal(Packs(), medium.CdTextBytes);
        var tracks = Assert.Single(medium.Sessions).Tracks;
        Assert.Equal(new[] { (1, 0, 300), (2, 300, 300) }, tracks.Select(t => (t.TrackNumber, t.StartLba, t.SectorCount)));
        Assert.Equal(Pcm(2, 300).AsSpan(0, SectorSize).ToArray(), medium.ReadSector(300, SectorSize));

        string log = await File.ReadAllTextAsync(engine.LastLogPath!);
        Assert.DoesNotContain("Step 1: test write", log);
        Assert.Contains("Read-back check passed", log);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TestFirst_InADriveThatKeepsTheSimulatedSession_StopsAfterTheTestAndSaysToReload(bool rewritable)
    {
        using var recorder = Recorder(rewritable ? VirtualDiscMedium.CreateBlankCdRw700() : VirtualDiscMedium.CreateBlankCdR700());
        var (engine, drive) = Engine(testFirst: true);

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(engine.LastTestWritePassed);
        Assert.Contains("reloaded", result.ErrorMessage);
        Assert.Contains("Nothing was written to the disc", result.ErrorMessage);
        Assert.False(drive()!.Recorded);
        Assert.True(recorder.CurrentMedium!.IsBlank);
    }

    [Fact]
    public void DriveStillShowingASimulatedSession_RefusesARealWrite_UntilTheDiscIsReloaded()
    {
        var drive = new VirtualMmcDrive();
        var writer = new NativeSaoAudioWriter(drive, sleep: _ => { });
        var request = new NativeSaoWriteRequest(
            new[] { new SaoTrack(1, 300) }, _ => new MemoryStream(Pcm(1, 300)), null, null, TestWrite: true);

        Assert.True(writer.Write(request).Success);
        Assert.False(writer.DiscReadsBlank());

        var refused = writer.Write(request with { TestWrite = false });
        Assert.False(refused.Success);
        Assert.True(refused.StoppedBeforeWriting);
        Assert.False(drive.Recorded);

        drive.Reload();
        Assert.True(writer.DiscReadsBlank());
        Assert.True(writer.Write(request with { TestWrite = false }).Success);
        Assert.True(drive.Recorded);
    }

    [Fact]
    public async Task TestFirst_InADriveThatShowsBlankAfterTheTest_GoesOnToTheRealWrite()
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var (engine, _) = Engine(testFirst: true, configure: d => d.ShowsSimulatedSessionAfterTestWrite = false);

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(engine.LastTestWritePassed);
        Assert.True(recorder.CurrentMedium!.IsFinalized);

        string log = await File.ReadAllTextAsync(engine.LastLogPath!);
        Assert.Contains("Step 1: test write", log);
        Assert.Contains("Step 2: real write", log);
    }

    [Fact]
    public async Task TestFirst_OnADriveWithoutATestWriteMode_GoesStraightToTheRealWrite()
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var (engine, _) = Engine(testFirst: true, configure: d => d.OffersTestWrite = false);

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(engine.LastTestWritePassed);
        Assert.True(engine.LastResult!.DiscRecorded);
    }

    [Fact]
    public async Task TestFirst_WhenTheTestWriteFails_LeavesTheDiscUnrecorded()
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdRw700());
        var (engine, drive) = Engine(testFirst: true, configure: d => d.WriteErrorAtLba = 100);

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("test write failed", result.ErrorMessage);
        Assert.False(drive()!.Recorded);
        Assert.True(recorder.CurrentMedium!.IsBlank);
    }

    [Fact]
    public async Task SimulateOption_DoesTheTestWriteOnly()
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdRw700());
        var (engine, drive) = Engine();

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(simulate: true), null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(engine.LastResult!.DiscRecorded);
        Assert.Null(engine.LastResult.Verification);
        Assert.False(drive()!.Recorded);
        Assert.True(recorder.CurrentMedium!.IsBlank);
    }

    [Fact]
    public async Task ReadBackMismatch_FailsTheBurn_ButTheDiscIsReportedAsRecorded()
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdRw700());
        var (engine, _) = Engine(configure: d => d.ReadBackDifferenceAtLba = 20);

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("the check afterwards failed", result.ErrorMessage);
        Assert.Equal(engine.LastResult!.LeadInSectorsWritten + engine.LastResult.AudioSectorsWritten, result.SectorsWritten); // not 0: the disc was written
        Assert.True(result.SectorsWritten > 0);
        Assert.Equal(engine.LastResult.Elapsed, result.TotalTime);
        Assert.DoesNotContain("Nothing was written", result.ErrorMessage);
        Assert.True(engine.LastResult!.DiscRecorded);
        Assert.False(recorder.CurrentMedium!.IsBlank);
    }

    [Fact]
    public async Task Progress_NamesTheTestWriteTheRealWriteAndTheCheck()
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var (engine, _) = Engine(testFirst: true, configure: d => d.ShowsSimulatedSessionAfterTestWrite = false);
        var actions = new List<string>();
        var progress = new SynchronousProgress(s => { lock (actions) actions.Add(s.CurrentActionDescription ?? string.Empty); });

        await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), progress, CancellationToken.None);

        Assert.Contains(actions, a => a.StartsWith("Test write: Writing audio", StringComparison.Ordinal));
        Assert.Contains(actions, a => a == "Writing audio");
        Assert.Contains(actions, a => a.StartsWith("Checking the disc", StringComparison.Ordinal));
    }

    private sealed class SynchronousProgress(Action<BurnProgressSnapshot> report) : IProgress<BurnProgressSnapshot>
    {
        public void Report(BurnProgressSnapshot value) => report(value);
    }

    // ---- From a CUE sheet to the cue sheet the drive receives ----

    /// <summary>One WAV of noise: 10 s of audio before track 1, then three 30-second tracks.</summary>
    private string WriteAlbum(out byte[] pcm)
    {
        pcm = Pcm(77, 100 * 75);
        string wav = Path.Combine(_dir, "album.wav");
        WavFileCodec.WriteWavFile(wav, pcm);
        string cue = Path.Combine(_dir, "album.cue");
        File.WriteAllText(cue, string.Join("\n",
            "CATALOG 0123456789012",
            "TITLE \"Gap Album\"",
            "PERFORMER \"Gap Artist\"",
            "FILE \"album.wav\" WAVE",
            "  TRACK 01 AUDIO",
            "    TITLE \"First\"",
            "    ISRC GBAYE0100001",
            "    INDEX 01 00:10:00",
            "  TRACK 02 AUDIO",
            "    TITLE \"Second\"",
            "    FLAGS PRE DCP",
            "    INDEX 00 00:38:00",
            "    INDEX 01 00:40:00",
            "  TRACK 03 AUDIO",
            "    TITLE \"Third\"",
            "    INDEX 01 01:10:00",
            ""));
        return cue;
    }

    private async Task<(BurnOperationResult Result, VirtualMmcDrive Drive, VirtualDiscMedium Medium, byte[] CueSheet)> BurnAlbum(bool keepIndexMarks)
    {
        string cue = WriteAlbum(out _);
        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        Assert.Equal(3, compilation.ImportCueSheet(cue));
        compilation.IncludeCdText = true;

        var recorder = Recorder(VirtualDiscMedium.CreateBlankCdRw700());
        var (engine, drive) = Engine();
        var service = new AudioCdBurnService(engine);
        var result = await service.BurnAsync(recorder, compilation, new AudioBurnOptions { UseDiscAtOnce = true, WriteCdText = true, KeepIndexMarks = keepIndexMarks });
        return (result, drive()!, recorder.CurrentMedium!, drive()!.RecordedCueSheet ?? []);
    }

    [Fact]
    public async Task CueAlbum_WithIndexMarksKept_GetsHiddenAudio_GapMarks_Flags_IsrcAndCatalogNumber()
    {
        var (result, drive, medium, sheet) = await BurnAlbum(keepIndexMarks: true);

        Assert.True(result.Success, result.ErrorMessage);

        // Disc layout: LBA 0 = file frame 0. Track 1 INDEX 01 after 10 s of hidden audio, track 2 at 40 s, track 3 at 70 s.
        Assert.Equal(new[] { 750, 3000, 5250 }, drive.RecordedTracks.Select(t => t.Index01Lba));
        Assert.Equal(7500, drive.RecordedLeadOutLba);
        Assert.Equal(new byte[] { 0x00, 0x30, 0x00 }, drive.RecordedTracks.Select(t => t.CueControl));

        // The audio on the disc is the file, start to end, with nothing dropped, repeated or silenced.
        WriteAlbum(out byte[] pcm);
        byte[] program = drive.ProgramAreaWritten.ToArray();
        Assert.Equal(pcm, program.AsSpan(150 * SectorSize).ToArray());

        byte[][] entries = sheet.Chunk(8).ToArray();
        Assert.Equal(2, entries.Count(e => (e[0] & 0x0F) == 0x02));                       // catalog number, two entries
        Assert.Equal("0123456", System.Text.Encoding.ASCII.GetString(entries[0], 1, 7));
        Assert.Equal(2, entries.Count(e => (e[0] & 0x0F) == 0x03 && e[1] == 1));           // ISRC of track 1
        byte[] trackTwoIndexZero = entries.Single(e => (e[0] & 0x0F) == 0x01 && e[1] == 2 && e[2] == 0);
        Assert.Equal(0x31, trackTwoIndexZero[0]);                                          // pre-emphasis + copy permitted
        Assert.Equal(Msf.FromLba(38 * 75), new Msf(trackTwoIndexZero[5], trackTwoIndexZero[6], trackTwoIndexZero[7]));
        Assert.DoesNotContain(entries, e => (e[0] & 0x0F) == 0x01 && e[1] == 3 && e[2] == 0); // track 3 has no INDEX 00

        Assert.Equal(3, Assert.Single(medium.Sessions).Tracks.Count);
    }

    [Fact]
    public async Task CueAlbum_WithoutIndexMarks_IsWrittenAsBefore_FromTrackOnesIndexOne()
    {
        var (result, drive, _, sheet) = await BurnAlbum(keepIndexMarks: false);

        Assert.True(result.Success, result.ErrorMessage);
        // The 10 s in front of track 1 are not written; each track runs from its INDEX 01 to the next.
        Assert.Equal(new[] { 0, 2250, 4500 }, drive.RecordedTracks.Select(t => t.Index01Lba));
        Assert.Equal(6750, drive.RecordedLeadOutLba);
        Assert.DoesNotContain(sheet.Chunk(8), e => (e[0] & 0x0F) == 0x01 && e[1] >= 2 && e[1] <= 99 && e[2] == 0);
    }
}
