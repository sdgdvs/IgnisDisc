using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class AudioRecoverySessionTests : IDisposable
{
    private readonly string _tempDir;

    public AudioRecoverySessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ignis_core_rec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
    public void EstimateCapacity_ReportsLimitsAndCarWarning()
    {
        var empty = AudioRecoverySessionService.EstimateCapacity(0);
        Assert.False(empty.Fits);
        Assert.Equal(RecoverySessionBudget.FixedSessionSectors + AudioRecoverySessionService.FileSystemReserveSectors, empty.OverheadSectors);
        Assert.Contains("car head units", empty.WarningText, StringComparison.OrdinalIgnoreCase);

        var partial = AudioRecoverySessionService.EstimateCapacity(180_000, RecoverySessionBudget.MeasuredCapacitySectors);
        Assert.True(partial.Fits);
        Assert.NotNull(partial.Layout);
        Assert.True(partial.Layout!.BlockSize % OpticalConstants.DataSectorSize == 0);
        Assert.Contains("Repairs up to", partial.ToleranceSummary, StringComparison.OrdinalIgnoreCase);

        var full = AudioRecoverySessionService.EstimateCapacity(355_000, RecoverySessionBudget.MeasuredCapacitySectors);
        Assert.False(full.Fits);
        Assert.Null(full.Layout);
        Assert.Contains("11,400", full.ToleranceSummary);
    }

    [Fact]
    public async Task BuildAndRepairFromFolder_RestoresCorruptedWavSampleForSample()
    {
        string? par2Path = Par2Runner.FindPar2Executable();
        if (string.IsNullOrEmpty(par2Path))
        {
            return;
        }

        byte[] pcm1 = MakeAudioPcm(seed: 101, sectors: 120);
        byte[] pcm2 = MakeAudioPcm(seed: 202, sectors: 150);

        var sources = new[]
        {
            new AudioTrackSource
            {
                TrackNumber = 1,
                Title = "Track One",
                Performer = "Test Artist",
                TotalPcmBytes = pcm1.Length,
                Duration = TimeSpan.FromSeconds(120.0 / 75.0),
                PcmStreamFactory = () => new MemoryStream(pcm1, writable: false)
            },
            new AudioTrackSource
            {
                TrackNumber = 2,
                Title = "Track Two",
                Performer = "Test Artist",
                TotalPcmBytes = pcm2.Length,
                Duration = TimeSpan.FromSeconds(150.0 / 75.0),
                PcmStreamFactory = () => new MemoryStream(pcm2, writable: false)
            }
        };

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Recovery Album",
            AlbumPerformer = "Test Artist"
        };

        int sessionStartLba = 270 + 11_400;
        using var staged = await AudioRecoverySessionService.BuildRecoverySessionIsoAsync(
            sources,
            compilation,
            sessionStartLba,
            RecoverySessionBudget.MeasuredCapacitySectors,
            par2Path);

        Assert.Equal(2, staged.Manifest.Tracks.Count);
        Assert.Equal(pcm1.Length + pcm2.Length, staged.Manifest.TotalPcmBytes);
        Assert.True(staged.Manifest.Par2BlockSizeBytes % OpticalConstants.DataSectorSize == 0);

        // Extract RECOVERY/ from the generated ISO and verify audio.pcm itself was not stored in session 2.
        string extractedRecoveryDir = Path.Combine(_tempDir, "extracted_rec");
        Directory.CreateDirectory(extractedRecoveryDir);
        using var paddedDiscImage = new MemoryStream();
        paddedDiscImage.SetLength((long)sessionStartLba * OpticalConstants.DataSectorSize + staged.IsoStream.Length);
        paddedDiscImage.Position = (long)sessionStartLba * OpticalConstants.DataSectorSize;
        staged.IsoStream.Position = 0;
        await staged.IsoStream.CopyToAsync(paddedDiscImage);
        paddedDiscImage.Position = 0;

        using (var isoReader = IsoImageReader.FromStream(paddedDiscImage, leaveOpen: true, sessionStartLba: sessionStartLba))
        {
            Assert.Null(isoReader.FindFile("/" + AudioRecoveryManifest.PcmImageFileName));
            foreach (var f in isoReader.AllFiles)
            {
                string dest = Path.Combine(extractedRecoveryDir, Path.GetFileName(f.VirtualPath));
                await File.WriteAllBytesAsync(dest, isoReader.ReadFileBytes(f));
            }
        }

        // Write ripped WAVs where Track 2 has a corrupted stretch of samples.
        string wav1Path = Path.Combine(_tempDir, "01 - Test Artist - Track One.wav");
        string wav2Path = Path.Combine(_tempDir, "02 - Test Artist - Track Two.wav");
        WavFileCodec.WriteWavFile(wav1Path, pcm1);

        byte[] damagedPcm2 = (byte[])pcm2.Clone();
        Array.Clear(damagedPcm2, 4096, 8192);
        damagedPcm2[20_000] ^= 0xFF;
        WavFileCodec.WriteWavFile(wav2Path, damagedPcm2);

        var ripInfos = new List<AudioTrackRipInfo>
        {
            new(1, wav1Path, 0u, 0u, 0u, 0, UnreadableSectors: 0),
            new(2, wav2Path, 0u, 0u, 0u, 1, UnreadableSectors: 3)
        };

        var repair = await AudioRecoverySessionService.RepairRippedTracksFromFolderAsync(
            extractedRecoveryDir,
            ripInfos,
            par2Path);

        Assert.True(repair.RecoverySessionFound, repair.Summary);
        Assert.True(repair.RepairAttempted, repair.Summary);
        Assert.True(repair.AllTracksVerified, repair.Summary);
        Assert.Equal(new[] { 2 }, repair.DamagedTracks);
        Assert.Equal(new[] { 2 }, repair.RepairedTracks);
        Assert.Empty(repair.FailedTracks);
        Assert.Equal(0, ripInfos[1].UnreadableSectors);

        byte[] actualWav2 = await File.ReadAllBytesAsync(wav2Path);
        Assert.Equal(pcm2, actualWav2.AsSpan(44).ToArray());
    }

    private static byte[] MakeAudioPcm(int seed, int sectors)
    {
        byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }
}
