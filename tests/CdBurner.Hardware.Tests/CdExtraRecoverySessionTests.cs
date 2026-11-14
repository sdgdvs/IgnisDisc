using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Simulator end-to-end tests for Enhanced CD (CD-Extra) discs carrying audio in session 1 and PAR2 recovery data
/// in session 2: burns both sessions on VirtualDiscRecorder/VirtualMmcDrive, injects unreadable/corrupted sectors,
/// and verifies that ripping with RepairFromRecoverySession rebuilds every sample identically.
/// </summary>
public sealed class CdExtraRecoverySessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logDir;

    public CdExtraRecoverySessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ignis_hw_rec_{Guid.NewGuid():N}");
        _logDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task BurnAudioWithRecoverySession_ThenInjectDamageAndRipRepair_RestoresIdenticalSamples()
    {
        if (Par2Runner.FindPar2Executable() == null)
        {
            return;
        }

        byte[] track1Pcm = CreatePcm(seed: 11, sectors: 300);
        byte[] track2Pcm = CreatePcm(seed: 22, sectors: 300);
        string wav1 = Path.Combine(_tempDir, "01.wav");
        string wav2 = Path.Combine(_tempDir, "02.wav");
        WavFileCodec.WriteWavFile(wav1, track1Pcm);
        WavFileCodec.WriteWavFile(wav2, track2Pcm);

        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        using var recorder = new VirtualDiscRecorder("VREC-01", "Virtual Optical", "Drive 1.0");
        recorder.InsertMedia(medium);

        var virtualEngine = new VirtualBurnEngine();
        var nativeAudio = new NativeAudioBurnEngine(_logDir);
        var nativeData = new NativeDataBurnEngine(virtualEngine, _logDir);
        var burnService = new AudioCdBurnService(nativeAudio, recoveryDataEngine: nativeData);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "CD-Extra Archive",
            AlbumPerformer = "Studio Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };
        compilation.AddTrack(new AudioCdTrackItem(1, wav1, "First Song", "Studio Artist", PreGapFrames: 0));
        compilation.AddTrack(new AudioCdTrackItem(2, wav2, "Second Song", "Studio Artist", PreGapFrames: 0));

        // LeaveSessionOpen / IncludeRecoverySession requires direct drive commands.
        var noNativeResult = await new AudioCdBurnService(virtualEngine).BurnAsync(
            recorder,
            compilation,
            new AudioBurnOptions { IncludeRecoverySession = true, UseDiscAtOnce = true });
        Assert.False(noNativeResult.Success);
        Assert.True(medium.IsBlank);

        var burnResult = await burnService.BurnAsync(
            recorder,
            compilation,
            new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                WriteCdText = true,
                IncludeRecoverySession = true
            });

        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.NotNull(burnService.LastRecoveryManifest);
        Assert.True(medium.IsFinalized);
        Assert.Equal(2, medium.Sessions.Count);
        Assert.All(medium.Sessions[0].Tracks, t => Assert.True(t.IsAudio));
        Assert.Single(medium.Sessions[1].Tracks);
        Assert.False(medium.Sessions[1].Tracks[0].IsAudio);
        Assert.Equal(600 + VirtualMmcDrive.CdFirstSessionGapSectors, medium.Sessions[1].StartLba);

        // Inject two kinds of disc damage:
        // 1) Unreadable sectors in Track 2 (LBA 310..313)
        // 2) Silently corrupted audio samples + C2 errors in Track 1 (LBA 10..11)
        for (int lba = 310; lba < 314; lba++)
        {
            medium.SimulatedBadLbas.Add(lba);
        }
        byte[] scrambledSector = new byte[OpticalConstants.RawSectorSize];
        new Random(999).NextBytes(scrambledSector);
        medium.WriteSector(10, scrambledSector, OpticalConstants.RawSectorSize);
        medium.WriteSector(11, scrambledSector, OpticalConstants.RawSectorSize);
        medium.SimulatedC2Errors[10] = 8;

        // Also damage one data sector near the end of session 2 (past ISO directory tables, inside PAR2 data) to prove PAR2 tolerates session-2 read losses too.
        int session2LastDataSector = medium.Sessions[1].StartLba + medium.Sessions[1].SectorCount - 5;
        medium.SimulatedBadLbas.Add(session2LastDataSector);

        string ripDir = Path.Combine(_tempDir, "ripped");
        var ripResult = await new AudioCdRipperService().RipAudioCdAsync(
            recorder,
            virtualEngine,
            ripDir,
            new AudioRipOptions
            {
                SecureMode = false,
                MaxRetriesPerSector = 1,
                ContinueOnUnreadableSector = false, // RepairFromRecoverySession should zero-fill and continue automatically
                RepairFromRecoverySession = true,
                CalculateReplayGain = true
            });

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.NotNull(ripResult.RecoveryRepair);
        Assert.True(ripResult.RecoveryRepair!.RecoverySessionFound, ripResult.RecoveryRepair.Summary);
        Assert.True(ripResult.RecoveryRepair.RepairAttempted, ripResult.RecoveryRepair.Summary);
        Assert.True(ripResult.RecoveryRepair.AllTracksVerified, ripResult.RecoveryRepair.Summary);
        Assert.Equal(new[] { 1, 2 }, ripResult.RecoveryRepair.DamagedTracks.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { 1, 2 }, ripResult.RecoveryRepair.RepairedTracks.OrderBy(x => x).ToArray());
        Assert.All(ripResult.TrackInfos, ti => Assert.Equal(0, ti.UnreadableSectors));

        byte[] ripped1Pcm = (await File.ReadAllBytesAsync(ripResult.RippedFilePaths[0])).AsSpan(44).ToArray();
        byte[] ripped2Pcm = (await File.ReadAllBytesAsync(ripResult.RippedFilePaths[1])).AsSpan(44).ToArray();
        Assert.Equal(track1Pcm, ripped1Pcm);
        Assert.Equal(track2Pcm, ripped2Pcm);
    }

    private static byte[] CreatePcm(int seed, int sectors)
    {
        byte[] data = new byte[sectors * OpticalConstants.RawSectorSize];
        new Random(seed).NextBytes(data);
        return data;
    }
}
