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
            Wait = _ => { },
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
        Assert.Equal(0, drive()!.TrayEjects); // a spring tray (as the GP60NB50 reports) is never opened
    }

    [Fact]
    public async Task TestFirst_OnADriveThatClosesItsOwnTray_ReloadsTheDiscAndBurns()
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var (engine, drive) = Engine(testFirst: true, configure: d => d.CanLoadTray = true);

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(engine.LastTestWritePassed);
        Assert.Equal(1, drive()!.TrayEjects);
        Assert.Equal(1, drive()!.TrayLoads);
        Assert.True(engine.LastResult!.Verification!.Passed);
        Assert.True(recorder.CurrentMedium!.IsFinalized);

        string log = await File.ReadAllTextAsync(engine.LastLogPath!);
        Assert.Contains("ejecting and reloading the disc", log);
        Assert.Contains("Disc reloaded; it reads blank again", log);
        Assert.Contains("Step 2: real write", log);
    }

    [Theory]
    [InlineData(2, true)]  // pop-up (spring) tray, even if it claims it can load
    [InlineData(4, true)]  // changer
    [InlineData(1, false)] // ordinary tray without the Load bit
    public async Task TestFirst_NeverCyclesATrayThatCannotSafelyCloseItself(byte mechanism, bool canLoad)
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var (engine, drive) = Engine(testFirst: true, configure: d => { d.LoadingMechanism = mechanism; d.CanLoadTray = canLoad; });

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("reloaded", result.ErrorMessage);
        Assert.Equal(0, drive()!.TrayEjects);
        Assert.False(drive()!.Recorded);
    }

    [Fact]
    public void RemovableMediumFeature_AsTheGp60nb50ReportsIt_IsATrayThatCannotLoad()
    {
        // Captured 2026-11-11: GET CONFIGURATION RT=2 feature 0x0003 from the GP60NB50.
        byte[] reply = Convert.FromHexString("0000000C0000000800030B042900000000000000");
        var info = TrayReload.ReadRemovableMedium(new CannedReply(reply));

        Assert.Equal(new RemovableMediumInfo(1, CanLoad: false, CanEject: true), info);
        Assert.False(info!.CanReloadByItself);
    }

    private sealed class CannedReply(byte[] reply) : CdBurner.Hardware.Scsi.IScsiTransport
    {
        public CdBurner.Hardware.Scsi.ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> data, byte direction, TimeSpan timeout)
        {
            int n = Math.Min(reply.Length, data.Length);
            reply.AsSpan(0, n).CopyTo(data);
            return CdBurner.Hardware.Scsi.ScsiExecutionResult.Success((uint)n);
        }

        public void Dispose() { }
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

    // ---- Write-offset correction (M3.2) ----

    private NativeAudioBurnEngine OffsetEngine(string store, IReadOnlyList<DriveOffsetEntry> list, int writeOffset, int readOffset) => new(_dir)
    {
        Wait = _ => { },
        DriveOffsetStorePath = store,
        LoadReadOffsets = _ => Task.FromResult(list),
        ConfigureVirtualDrive = d => { d.WriteOffsetSamples = writeOffset; d.ReadOffsetSamples = readOffset; }
    };

    private static byte[] OnDisc(VirtualDiscMedium medium, int sectors)
    {
        var all = new byte[sectors * SectorSize];
        for (int lba = 0; lba < sectors; lba++)
        {
            medium.ReadSector(lba, SectorSize).CopyTo(all, lba * SectorSize);
        }
        return all;
    }

    [Fact]
    public async Task WriteOffset_IsMeasuredOnTheFirstBurn_AndCorrectedOnTheNext_SoTheDiscHoldsTheSourceSampleForSample()
    {
        // A drive that lands audio 30 samples early (W = -30) and reads it 12 late (R = +12): it measures C = -18.
        string store = Path.Combine(_dir, "offsets.json");
        var name = AccurateRipDriveOffsets.DriveName("IGNIS", "VIRTUAL DVD/CD-RW");
        IReadOnlyList<DriveOffsetEntry> list = [new DriveOffsetEntry(name, 12, 99)];
        byte[] source = [.. Pcm(1, 300), .. Pcm(2, 300)];

        // 1. Nothing measured yet: written as it is, and the combined offset is recorded.
        using var first = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var engine1 = OffsetEngine(store, list, writeOffset: -30, readOffset: 12);
        var r1 = await engine1.BurnAudioTracksAsync(first, Tracks(), Options(), null, CancellationToken.None);
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.Null(engine1.LastWriteOffsetPlan!.WriteOffset);
        Assert.Equal(0, engine1.LastWriteOffsetPlan.ShiftSamples);
        Assert.Equal(-18, engine1.LastResult!.Verification!.OffsetSamples);
        Assert.Equal(-18, new DriveOffsetStore(store).GetCombinedOffset(name));
        // Uncorrected, the audio sits 30 samples early on the disc.
        Assert.True(OnDisc(first.CurrentMedium!, 600).AsSpan(0, 1000 * 4).SequenceEqual(source.AsSpan(30 * 4, 1000 * 4)));

        // 2. Measured: W = C - R = -30, so the audio is sent 30 samples later and lands where it belongs.
        using var second = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var engine2 = OffsetEngine(store, list, writeOffset: -30, readOffset: 12);
        var r2 = await engine2.BurnAudioTracksAsync(second, Tracks(), Options(), null, CancellationToken.None);
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.Equal(-30, engine2.LastWriteOffsetPlan!.WriteOffset);
        Assert.Equal(30, engine2.LastWriteOffsetPlan.ShiftSamples);
        Assert.True(engine2.LastResult!.Verification!.Passed); // the check compares with what was sent, shift included
        Assert.Equal(-18, engine2.LastResult.Verification.OffsetSamples);

        byte[] disc = OnDisc(second.CurrentMedium!, 600);
        int exact = source.Length - 30 * 4; // the last 30 samples were pushed past the end of the program area
        Assert.True(disc.AsSpan(0, exact).SequenceEqual(source.AsSpan(0, exact)), "every sample in its place, across the track boundary");
        Assert.True(disc.AsSpan(exact).IndexOfAnyExcept((byte)0) < 0);

        string log = await File.ReadAllTextAsync(engine2.LastLogPath!);
        Assert.Contains("Write offset -30 samples (combined -18 measured on earlier burns, read +12", log);
        Assert.Contains("sent +30 samples later", log);
    }

    [Theory]
    [InlineData(0)]   // explicitly off
    [InlineData(-30)] // explicitly given
    public async Task AnExplicitWriteOffset_IsUsedAsGiven(int given)
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var engine = OffsetEngine(Path.Combine(_dir, "o.json"), [], writeOffset: -30, readOffset: 0);
        var options = new AudioBurnOptions { WriteCdText = true, CdTextBytes = Packs(), UseDiscAtOnce = true, WriteOffsetSamples = given };

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), options, null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(-given, engine.LastWriteOffsetPlan!.ShiftSamples);
    }

    [Fact]
    public async Task UnknownDrive_IsWrittenAsItIs_AndSaysWhy()
    {
        using var recorder = Recorder(VirtualDiscMedium.CreateBlankCdR700());
        var engine = OffsetEngine(Path.Combine(_dir, "o.json"), [new DriveOffsetEntry("OTHER - DRIVE", 6, 5)], writeOffset: 0, readOffset: 0);

        var result = await engine.BurnAudioTracksAsync(recorder, Tracks(), Options(), null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, engine.LastWriteOffsetPlan!.ShiftSamples);
        Assert.Contains("read offset is not known", engine.LastWriteOffsetPlan.Explanation);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-6)]
    [InlineData(700)]   // more than a sector (588 samples)
    [InlineData(-1300)] // more than two sectors, earlier
    public void ShiftedProgramArea_IsTheUnshiftedOneMovedByWholeSamples_AllTheWayThrough(int shift)
    {
        // Three tracks of odd lengths, the last ending mid-sector, so the shift crosses every kind of boundary.
        byte[][] audio = [Pcm(11, 307), Pcm(12, 313)[..(313 * SectorSize - 1000)], Pcm(13, 305)[..(305 * SectorSize - 4)]];
        var tracks = new[] { new SaoTrack(1, 307, 150), new SaoTrack(2, 313), new SaoTrack(3, 305) }; // Red Book: at least 4 s each
        NativeSaoWriteRequest Request(int s) => new(tracks, i => new MemoryStream(audio[i]), null, null, TestWrite: false) { ShiftSamples = s };

        var plain = new VirtualMmcDrive();
        var reference = new NativeSaoAudioWriter(plain, sleep: _ => { }).Write(Request(0));
        Assert.True(reference.Success, reference.Message);
        var moved = new VirtualMmcDrive();
        var result = new NativeSaoAudioWriter(moved, sleep: _ => { }).Write(Request(shift));
        Assert.True(result.Success, result.Message);
        Assert.True(result.Verification!.Passed, result.Verification.Message);

        byte[] a = plain.ProgramAreaWritten.ToArray(), b = moved.ProgramAreaWritten.ToArray();
        Assert.Equal(a.Length, b.Length);
        int bytes = shift * 4;
        for (int i = 0; i < b.Length; i++)
        {
            int from = i - bytes;
            byte expected = from >= 0 && from < a.Length ? a[from] : (byte)0;
            Assert.True(expected == b[i], $"byte {i} (sector {i / SectorSize}) differs");
        }
    }
}
