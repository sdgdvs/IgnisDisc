using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Virtual audio disc fixture providing a deterministic 3-track gapless Audio CD
/// burned in-memory against VirtualDiscRecorder for secure extraction tests.
/// </summary>
public sealed class SecureAudioDiscFixture : IAsyncLifetime
{
    public string TempDir { get; }
    public VirtualDiscRecorder Recorder { get; private set; } = null!;
    public VirtualDiscMedium Medium { get; private set; } = null!;
    public byte[] Track1Pcm { get; private set; } = null!;
    public byte[] Track2Pcm { get; private set; } = null!;
    public byte[] Track3Pcm { get; private set; } = null!;
    public byte[] WholeAudioPcm { get; private set; } = null!;

    public const int Track1Frames = 50;
    public const int Track2Frames = 75;
    public const int Track3Frames = 100;
    public const int TotalAudioSectors = Track1Frames + Track2Frames + Track3Frames; // 225

    public SecureAudioDiscFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "CdBurner_SecureAudio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
    }

    public async Task InitializeAsync()
    {
        var rng = new Random(12345);
        Track1Pcm = new byte[Track1Frames * OpticalConstants.RawSectorSize];
        rng.NextBytes(Track1Pcm);
        Track2Pcm = new byte[Track2Frames * OpticalConstants.RawSectorSize];
        rng.NextBytes(Track2Pcm);
        Track3Pcm = new byte[Track3Frames * OpticalConstants.RawSectorSize];
        rng.NextBytes(Track3Pcm);

        string wavPath1 = Path.Combine(TempDir, "burn_input_track1.wav");
        string wavPath2 = Path.Combine(TempDir, "burn_input_track2.wav");
        string wavPath3 = Path.Combine(TempDir, "burn_input_track3.wav");
        WavFileCodec.WriteWavFile(wavPath1, Track1Pcm);
        WavFileCodec.WriteWavFile(wavPath2, Track2Pcm);
        WavFileCodec.WriteWavFile(wavPath3, Track3Pcm);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Secure Audio Reader Test Album",
            AlbumPerformer = "Reference Test Performer",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };

        compilation.AddTrack(new AudioCdTrackItem(1, wavPath1, "Test Track 01", "Reference Test Performer", PreGapFrames: 150));
        compilation.AddTrack(new AudioCdTrackItem(2, wavPath2, "Test Track 02", "Reference Test Performer", PreGapFrames: 0));
        compilation.AddTrack(new AudioCdTrackItem(3, wavPath3, "Test Track 03", "Reference Test Performer", PreGapFrames: 0));

        Recorder = new VirtualDiscRecorder();
        Medium = VirtualDiscMedium.CreateBlankCdR700();
        Recorder.InsertMedia(Medium);

        var engine = new VirtualBurnEngine(Recorder);
        var burnService = new AudioCdBurnService(engine);

        var burnResult = await burnService.BurnCompilationAsync(Recorder, compilation);
        if (!burnResult.Success)
        {
            throw new InvalidOperationException($"Virtual audio CD burn failed: {burnResult.ErrorMessage}");
        }

        WholeAudioPcm = new byte[Track1Pcm.Length + Track2Pcm.Length + Track3Pcm.Length];
        Track1Pcm.CopyTo(WholeAudioPcm, 0);
        Track2Pcm.CopyTo(WholeAudioPcm, Track1Pcm.Length);
        Track3Pcm.CopyTo(WholeAudioPcm, Track1Pcm.Length + Track2Pcm.Length);
    }

    public void ResetSimulation()
    {
        Medium.SimulatedC2Errors.Clear();
        Medium.SimulatedBadLbas.Clear();
    }

    public byte[] GetBurnedSector(int lba)
    {
        return WholeAudioPcm.AsSpan(lba * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize).ToArray();
    }

    public Task DisposeAsync()
    {
        try { Recorder?.Dispose(); } catch { }
        try { Medium?.Dispose(); } catch { }
        try
        {
            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, recursive: true);
            }
        }
        catch { }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Unit and integration tests for C2-based secure audio extraction through
/// <see cref="SecureAudioReader"/> and <see cref="AudioCdRipperService"/>.
/// </summary>
public class SecureAudioReaderTests : IClassFixture<SecureAudioDiscFixture>, IDisposable
{
    private readonly SecureAudioDiscFixture _fixture;

    public SecureAudioReaderTests(SecureAudioDiscFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetSimulation();
    }

    public void Dispose()
    {
        _fixture.ResetSimulation();
    }

    [Fact]
    public async Task CleanDisc_DeliversEverySectorOnce_NoFlushes()
    {
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var delivered = new List<(int Lba, byte[] Data)>();
        int unreadableCalls = 0;

        var outcome = await SecureAudioReader.ReadRangeAsync(
            engine,
            _fixture.Recorder,
            startLba: 0,
            sectorCount: 100,
            audioEndLba: SecureAudioDiscFixture.TotalAudioSectors,
            maxRereadRounds: 3,
            onSector: (lba, data) =>
            {
                delivered.Add((lba, data.ToArray()));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadableCalls++;
                return ValueTask.FromResult(true);
            },
            batchSectors: 32);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(0, outcome.RecoveredSectors);
        Assert.Equal(0, outcome.SuspectSectors);
        Assert.Equal(0, outcome.Rereads);
        Assert.Equal(0, outcome.CacheFlushes);
        Assert.Equal(0, engine.CacheFlushCount);
        Assert.Equal(0, unreadableCalls);

        Assert.Equal(100, delivered.Count);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i, delivered[i].Lba);
            Assert.Equal(_fixture.GetBurnedSector(i), delivered[i].Data);
        }
    }

    [Fact]
    public async Task TransientC2Error_IsRecoveredAfterFlushAndReread()
    {
        _fixture.Medium.SimulatedC2Errors[40] = 1;
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var delivered = new List<(int Lba, byte[] Data)>();

        var outcome = await SecureAudioReader.ReadRangeAsync(
            engine,
            _fixture.Recorder,
            startLba: 0,
            sectorCount: 100,
            audioEndLba: SecureAudioDiscFixture.TotalAudioSectors,
            maxRereadRounds: 3,
            onSector: (lba, data) =>
            {
                delivered.Add((lba, data.ToArray()));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba => ValueTask.FromResult(true),
            batchSectors: 32);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(1, outcome.RecoveredSectors);
        Assert.Equal(0, outcome.SuspectSectors);
        Assert.Equal(1, outcome.CacheFlushes);
        Assert.Equal(1, outcome.Rereads);
        Assert.Equal(1, engine.CacheFlushCount);

        var sector40 = delivered.Single(d => d.Lba == 40).Data;
        Assert.Equal(_fixture.GetBurnedSector(40), sector40);
    }

    [Fact]
    public async Task C2ErrorLastingTwoReads_NeedsTwoRounds()
    {
        _fixture.Medium.SimulatedC2Errors[40] = 2;
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var delivered = new List<(int Lba, byte[] Data)>();

        var outcome = await SecureAudioReader.ReadRangeAsync(
            engine,
            _fixture.Recorder,
            startLba: 0,
            sectorCount: 100,
            audioEndLba: SecureAudioDiscFixture.TotalAudioSectors,
            maxRereadRounds: 3,
            onSector: (lba, data) =>
            {
                delivered.Add((lba, data.ToArray()));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba => ValueTask.FromResult(true),
            batchSectors: 32);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(1, outcome.RecoveredSectors);
        Assert.Equal(0, outcome.SuspectSectors);
        Assert.Equal(2, outcome.CacheFlushes);
        Assert.Equal(2, outcome.Rereads);
        Assert.Equal(2, engine.CacheFlushCount);

        var sector40 = delivered.Single(d => d.Lba == 40).Data;
        Assert.Equal(_fixture.GetBurnedSector(40), sector40);
    }

    [Fact]
    public async Task PermanentC2Error_IsDeliveredAsSuspect()
    {
        _fixture.Medium.SimulatedC2Errors[40] = int.MaxValue;
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var delivered = new List<(int Lba, byte[] Data)>();
        int unreadableCalls = 0;

        var outcome = await SecureAudioReader.ReadRangeAsync(
            engine,
            _fixture.Recorder,
            startLba: 0,
            sectorCount: 100,
            audioEndLba: SecureAudioDiscFixture.TotalAudioSectors,
            maxRereadRounds: 3,
            onSector: (lba, data) =>
            {
                delivered.Add((lba, data.ToArray()));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadableCalls++;
                return ValueTask.FromResult(true);
            },
            batchSectors: 32);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(0, outcome.RecoveredSectors);
        Assert.Equal(1, outcome.SuspectSectors);
        Assert.Equal(3, outcome.CacheFlushes);
        Assert.Equal(3, outcome.Rereads);
        Assert.Equal(3, engine.CacheFlushCount);
        Assert.Equal(0, unreadableCalls);

        var sector40 = delivered.Single(d => d.Lba == 40).Data;
        byte[] clean40 = _fixture.GetBurnedSector(40);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal((byte)(clean40[i] ^ 0xFF), sector40[i]);
        }
        Assert.Equal(clean40.AsSpan(16).ToArray(), sector40.AsSpan(16).ToArray());
    }

    [Fact]
    public async Task SeveralSuspectsInOneBatch_ShareOneFlushPerRound()
    {
        _fixture.Medium.SimulatedC2Errors[10] = 1;
        _fixture.Medium.SimulatedC2Errors[11] = 1;
        _fixture.Medium.SimulatedC2Errors[20] = 1;
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var delivered = new List<(int Lba, byte[] Data)>();

        var outcome = await SecureAudioReader.ReadRangeAsync(
            engine,
            _fixture.Recorder,
            startLba: 0,
            sectorCount: 100,
            audioEndLba: SecureAudioDiscFixture.TotalAudioSectors,
            maxRereadRounds: 3,
            onSector: (lba, data) =>
            {
                delivered.Add((lba, data.ToArray()));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba => ValueTask.FromResult(true),
            batchSectors: 32);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(3, outcome.RecoveredSectors);
        Assert.Equal(0, outcome.SuspectSectors);
        Assert.Equal(1, outcome.CacheFlushes);
        Assert.Equal(3, outcome.Rereads);
        Assert.Equal(1, engine.CacheFlushCount);

        Assert.Equal(_fixture.GetBurnedSector(10), delivered.Single(d => d.Lba == 10).Data);
        Assert.Equal(_fixture.GetBurnedSector(11), delivered.Single(d => d.Lba == 11).Data);
        Assert.Equal(_fixture.GetBurnedSector(20), delivered.Single(d => d.Lba == 20).Data);
    }

    [Fact]
    public async Task UnreadableSector_GoesToOnUnreadable_AndContinues()
    {
        _fixture.Medium.SimulatedBadLbas.Add(50);
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var delivered = new List<(int Lba, byte[] Data)>();
        var unreadableCalls = new List<int>();

        var outcome = await SecureAudioReader.ReadRangeAsync(
            engine,
            _fixture.Recorder,
            startLba: 0,
            sectorCount: 100,
            audioEndLba: SecureAudioDiscFixture.TotalAudioSectors,
            maxRereadRounds: 3,
            onSector: (lba, data) =>
            {
                delivered.Add((lba, data.ToArray()));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadableCalls.Add(lba);
                return ValueTask.FromResult(true);
            },
            batchSectors: 32);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Single(unreadableCalls);
        Assert.Equal(50, unreadableCalls[0]);

        var expectedLbas = Enumerable.Range(0, 100).Where(lba => lba != 50).ToList();
        Assert.Equal(expectedLbas, delivered.Select(d => d.Lba).ToList());

        for (int i = 0; i < delivered.Count; i++)
        {
            int lba = delivered[i].Lba;
            Assert.Equal(_fixture.GetBurnedSector(lba), delivered[i].Data);
        }
    }

    [Fact]
    public async Task UnreadableSector_StopRequested_ReturnsStoppedAtLba()
    {
        _fixture.Medium.SimulatedBadLbas.Add(50);
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var delivered = new List<(int Lba, byte[] Data)>();
        var unreadableCalls = new List<int>();

        var outcome = await SecureAudioReader.ReadRangeAsync(
            engine,
            _fixture.Recorder,
            startLba: 0,
            sectorCount: 100,
            audioEndLba: SecureAudioDiscFixture.TotalAudioSectors,
            maxRereadRounds: 3,
            onSector: (lba, data) =>
            {
                delivered.Add((lba, data.ToArray()));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadableCalls.Add(lba);
                return ValueTask.FromResult(false);
            },
            batchSectors: 32);

        Assert.False(outcome.Completed);
        Assert.Equal(50, outcome.StoppedAtLba);
        Assert.Single(unreadableCalls);
        Assert.Equal(50, unreadableCalls[0]);

        var expectedLbas = Enumerable.Range(0, 50).ToList();
        Assert.Equal(expectedLbas, delivered.Select(d => d.Lba).ToList());

        for (int i = 0; i < delivered.Count; i++)
        {
            int lba = delivered[i].Lba;
            Assert.Equal(_fixture.GetBurnedSector(lba), delivered[i].Data);
        }
    }

    [Fact]
    public async Task ZeroRereadRounds_LeavesC2SectorAsSuspectWithoutFlushing()
    {
        _fixture.Medium.SimulatedC2Errors[40] = 1;
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var delivered = new List<(int Lba, byte[] Data)>();

        var outcome = await SecureAudioReader.ReadRangeAsync(
            engine,
            _fixture.Recorder,
            startLba: 0,
            sectorCount: 100,
            audioEndLba: SecureAudioDiscFixture.TotalAudioSectors,
            maxRereadRounds: 0,
            onSector: (lba, data) =>
            {
                delivered.Add((lba, data.ToArray()));
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba => ValueTask.FromResult(true),
            batchSectors: 32);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(1, outcome.SuspectSectors);
        Assert.Equal(0, outcome.RecoveredSectors);
        Assert.Equal(0, outcome.CacheFlushes);
        Assert.Equal(0, outcome.Rereads);
        Assert.Equal(0, engine.CacheFlushCount);

        var sector40 = delivered.Single(d => d.Lba == 40).Data;
        byte[] clean40 = _fixture.GetBurnedSector(40);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal((byte)(clean40[i] ^ 0xFF), sector40[i]);
        }
        Assert.Equal(clean40.AsSpan(16).ToArray(), sector40.AsSpan(16).ToArray());
    }

    [Fact]
    public async Task Rip_SecureMode_RecoversTransientDamage_AndReportsCounts()
    {
        _fixture.Medium.SimulatedC2Errors[60] = 1;
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var ripper = new AudioCdRipperService();
        string outputDir = Path.Combine(_fixture.TempDir, "rip_secure_" + Guid.NewGuid().ToString("N"));

        try
        {
            var options = new AudioRipOptions { SecureMode = true };
            var result = await ripper.RipAudioCdAsync(_fixture.Recorder, engine, outputDir, options);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.SecureModeUsed);
            Assert.Equal(3, result.TracksRipped);
            Assert.Equal(3, result.TrackInfos.Count);

            // Track 1
            Assert.Equal(0, result.TrackInfos[0].RecoveredSectors);
            Assert.Equal(0, result.TrackInfos[0].SuspectSectors);

            // Track 2
            Assert.Equal(1, result.TrackInfos[1].RecoveredSectors);
            Assert.Equal(0, result.TrackInfos[1].SuspectSectors);

            // Track 3
            Assert.Equal(0, result.TrackInfos[2].RecoveredSectors);
            Assert.Equal(0, result.TrackInfos[2].SuspectSectors);

            // Verify ripped WAV PCM matches burned PCM for all tracks
            byte[] track1Wav = await File.ReadAllBytesAsync(result.RippedFilePaths[0]);
            byte[] track2Wav = await File.ReadAllBytesAsync(result.RippedFilePaths[1]);
            byte[] track3Wav = await File.ReadAllBytesAsync(result.RippedFilePaths[2]);

            Assert.Equal(_fixture.Track1Pcm, track1Wav[44..]);
            Assert.Equal(_fixture.Track2Pcm, track2Wav[44..]);
            Assert.Equal(_fixture.Track3Pcm, track3Wav[44..]);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                try { Directory.Delete(outputDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Rip_WithoutSecureMode_KeepsDamagedData()
    {
        _fixture.Medium.SimulatedC2Errors[60] = 1;
        var engine = new VirtualBurnEngine(_fixture.Recorder);
        var ripper = new AudioCdRipperService();
        string outputDir = Path.Combine(_fixture.TempDir, "rip_nonsecure_" + Guid.NewGuid().ToString("N"));

        try
        {
            var options = new AudioRipOptions { SecureMode = false };
            var result = await ripper.RipAudioCdAsync(_fixture.Recorder, engine, outputDir, options);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.SecureModeUsed);
            Assert.Equal(3, result.TracksRipped);
            Assert.Equal(3, result.TrackInfos.Count);

            foreach (var trackInfo in result.TrackInfos)
            {
                Assert.Equal(0, trackInfo.RecoveredSectors);
                Assert.Equal(0, trackInfo.SuspectSectors);
            }

            byte[] track1Wav = await File.ReadAllBytesAsync(result.RippedFilePaths[0]);
            byte[] track2Wav = await File.ReadAllBytesAsync(result.RippedFilePaths[1]);
            byte[] track3Wav = await File.ReadAllBytesAsync(result.RippedFilePaths[2]);

            // As documented in the specification, plain ReadSectorsAsync does not apply SimulatedC2Errors,
            // so the PCM equals the burned PCM and RecoveredSectors is 0 for all tracks.
            Assert.Equal(_fixture.Track1Pcm, track1Wav[44..]);
            Assert.Equal(_fixture.Track2Pcm, track2Wav[44..]);
            Assert.Equal(_fixture.Track3Pcm, track3Wav[44..]);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                try { Directory.Delete(outputDir, recursive: true); } catch { }
            }
        }
    }
}
