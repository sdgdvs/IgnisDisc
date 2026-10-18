using System.IO;
using System.Security.Cryptography;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Unit and integration tests for Phase 22: WPF Audio CD Studio &amp; Disc Image Studio Views.
/// Validates AudioBurnViewModel (track sequencing, re-indexing, total duration, burning, ripping)
/// and ImageStudioViewModel (inspecting .iso/.cue, burning to virtual recorder, verifying matching SHA-256 digests, ripping).
/// </summary>
public sealed class Phase22_AudioAndImageViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public Phase22_AudioAndImageViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Phase22_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = new VirtualBurnEngine();
    }

    public void Dispose()
    {
        _deviceManager.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task AudioBurnViewModel_TrackSequencingReindexingBurningAndRipping_Succeeds()
    {
        // Arrange: Generate 3 synthetic WAV audio files
        // Track 1: 1 second = 75 frames = 176,400 bytes PCM
        // Track 2: 2 seconds = 150 frames = 352,800 bytes PCM
        // Track 3: 3 seconds = 225 frames = 529,200 bytes PCM
        string wav1 = Path.Combine(_tempDir, "Track01.wav");
        string wav2 = Path.Combine(_tempDir, "Track02.wav");
        string wav3 = Path.Combine(_tempDir, "Track03.wav");

        byte[] pcm1 = new byte[75 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[150 * OpticalConstants.RawSectorSize];
        byte[] pcm3 = new byte[225 * OpticalConstants.RawSectorSize];

        for (int i = 0; i < pcm1.Length; i++) pcm1[i] = (byte)(i % 256);
        for (int i = 0; i < pcm2.Length; i++) pcm2[i] = (byte)((i * 3) % 256);
        for (int i = 0; i < pcm3.Length; i++) pcm3[i] = (byte)((i * 7) % 256);

        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);
        WavFileCodec.WriteWavFile(wav3, pcm3);

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine)
        {
            AlbumTitle = "Phase 22 Synthwave Album",
            AlbumPerformer = "Audio Master Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            NormalizeAllTracks = false
        };

        // Act 1: Add 3 audio tracks
        vm.AddAudioFilesCommand.Execute(new[] { wav1, wav2, wav3 });

        // Assert 1: Tracks populated, sequential indexing 1..3, total duration 450 frames = 00:06:00
        Assert.Equal(3, vm.Tracks.Count);
        Assert.Equal(1, vm.Tracks[0].TrackNumber);
        Assert.Equal(2, vm.Tracks[1].TrackNumber);
        Assert.Equal(3, vm.Tracks[2].TrackNumber);

        Assert.Equal("Track01", vm.Tracks[0].Title);
        Assert.Equal("Track02", vm.Tracks[1].Title);
        Assert.Equal("Track03", vm.Tracks[2].Title);

        const int expectedTotalFrames = 75 + 150 + 225; // 450 frames = 6 seconds
        Assert.Equal(expectedTotalFrames, vm.TotalDuration.ToFrameCount());
        Assert.Equal(0, vm.TotalDuration.Minutes);
        Assert.Equal(6, vm.TotalDuration.Seconds);
        Assert.Equal(0, vm.TotalDuration.Frames);
        Assert.True(vm.DiscUsagePercent > 0.0);

        // Act 2: Move track 3 up (order becomes: Track01, Track03, Track02)
        vm.SelectedTrack = vm.Tracks[2];
        Assert.True(vm.MoveTrackUpCommand.CanExecute(null));
        vm.MoveTrackUpCommand.Execute(null);

        // Assert 2: Track numbers re-indexed sequentially 1..3
        Assert.Equal(3, vm.Tracks.Count);
        Assert.Equal(1, vm.Tracks[0].TrackNumber);
        Assert.Equal(2, vm.Tracks[1].TrackNumber);
        Assert.Equal(3, vm.Tracks[2].TrackNumber);
        Assert.Equal(wav3, vm.Tracks[1].SourceFilePath);
        Assert.Equal(wav2, vm.Tracks[2].SourceFilePath);

        // Act 3: Move track 2 (currently Track03 at index 1) down
        vm.SelectedTrack = vm.Tracks[1];
        Assert.True(vm.MoveTrackDownCommand.CanExecute(null));
        vm.MoveTrackDownCommand.Execute(null);

        // Assert 3: Order restored to Track01, Track02, Track03, re-indexed 1..3
        Assert.Equal(1, vm.Tracks[0].TrackNumber);
        Assert.Equal(2, vm.Tracks[1].TrackNumber);
        Assert.Equal(3, vm.Tracks[2].TrackNumber);
        Assert.Equal(wav1, vm.Tracks[0].SourceFilePath);
        Assert.Equal(wav2, vm.Tracks[1].SourceFilePath);
        Assert.Equal(wav3, vm.Tracks[2].SourceFilePath);

        // Act 4: Burn compilation to VDRV-01
        var burnResult = await vm.StartAudioBurnAsync("VDRV-01");

        // Assert 4: Burn succeeded
        Assert.True(burnResult.Success, $"Audio burn failed: {burnResult.ErrorMessage}");
        Assert.True(burnResult.SectorsWritten >= expectedTotalFrames);
        Assert.Equal(BurnStatus.Succeeded, burnResult.Status);

        // Act 5: Rip Audio CD back from VDRV-01 into temporary folder
        string ripOutput = Path.Combine(_tempDir, "Ripped_Audio_CD");
        vm.RipOutputFolder = ripOutput;
        var ripResult = await vm.RipAudioCdAsync("VDRV-01", ripOutput);

        // Assert 5: Rip extracted 3 WAV files and companion CUE sheet
        Assert.True(ripResult.Success, $"Audio rip failed: {ripResult.ErrorMessage}");
        Assert.Equal(3, ripResult.TracksRipped);
        Assert.NotNull(ripResult.CueSheetPath);
        Assert.True(File.Exists(ripResult.CueSheetPath));

        foreach (var rippedWav in ripResult.RippedFilePaths)
        {
            Assert.True(File.Exists(rippedWav));
            Assert.True(new FileInfo(rippedWav).Length > 44);
        }

        // Verify CUE sheet parses cleanly and references ripped tracks
        var parsedCue = CueSheetParser.ParseFile(ripResult.CueSheetPath);
        Assert.Equal(3, parsedCue.Files.Count);
    }

    [Fact]
    public void AudioBurnViewModel_ClearAllTracks_ResetsCompilationStateAndTotals()
    {
        // Arrange
        string wav1 = Path.Combine(_tempDir, "Track01_clear.wav");
        string wav2 = Path.Combine(_tempDir, "Track02_clear.wav");
        byte[] pcm1 = new byte[75 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[150 * OpticalConstants.RawSectorSize];
        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);
        Assert.False(vm.ClearAllTracksCommand.CanExecute(null));

        // Act 1: Add tracks
        vm.AddAudioFilesCommand.Execute(new[] { wav1, wav2 });
        Assert.Equal(2, vm.Tracks.Count);
        Assert.True(vm.ClearAllTracksCommand.CanExecute(null));
        Assert.True(vm.TotalDuration.ToFrameCount() > 0);

        // Act 2: Clear all tracks
        vm.ClearAllTracksCommand.Execute(null);

        // Assert 2: Entire compilation cleared and commands updated
        Assert.Empty(vm.Tracks);
        Assert.Null(vm.SelectedTrack);
        Assert.Equal(0, vm.TotalDuration.ToFrameCount());
        Assert.False(vm.ClearAllTracksCommand.CanExecute(null));
        Assert.False(vm.StartAudioBurnCommand.CanExecute(null));
        Assert.Contains("Cleared all tracks", vm.StatusMessage);
    }

    [Fact]
    public async Task ImageStudioViewModel_InspectBurnAndVerifyIso_SucceedsWithBitForBitMatch()
    {
        // Arrange 1: Build a synthetic ISO file
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("READ_ME.TXT", "Welcome to Phase 22 Disc Image Studio."u8.ToArray());
        root.AddFile("DATA.BIN", new byte[65536]);

        string sampleIso = Path.Combine(_tempDir, "MasterTest.iso");
        await using (var fs = new FileStream(sampleIso, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await IsoUdfImageBuilder.BuildAsync(root, fs, new IsoBuildOptions(VolumeLabel: "IMAGE_STUDIO"));
        }

        Assert.True(File.Exists(sampleIso));
        var fi = new FileInfo(sampleIso);

        var vm = new ImageStudioViewModel(_deviceManager, _burnEngine);

        // Act 1: Inspect ISO Image
        vm.SourceImagePath = sampleIso;
        vm.InspectImageCommand.Execute(null);

        // Assert 1: DetectedImageFormat and ImageSectorCount populated
        Assert.NotNull(vm.DetectedImageFormat);
        Assert.Contains("ISO", vm.DetectedImageFormat);
        Assert.True(vm.ImageSectorCount > 0);
        Assert.Equal(fi.Length, vm.ImageSizeBytes);

        // Act 2: Burn Image to VDRV-01
        var burnResult = await vm.BurnImageToDiscAsync("VDRV-01");

        // Assert 2: Burn succeeded
        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");
        Assert.True(burnResult.SectorsWritten > 0);

        // Act 3: Verify Disc Against Image
        var verifyResult = await vm.VerifyDiscAgainstImageAsync("VDRV-01");

        // Assert 3: Bit-for-bit match and identical SHA-256 strings
        Assert.True(vm.IsChecksumMatch, $"Checksum mismatch: {vm.VerificationStatusText}");
        Assert.False(string.IsNullOrWhiteSpace(vm.CalculatedSha256));
        Assert.False(string.IsNullOrWhiteSpace(vm.DiscSha256));
        Assert.Equal(vm.CalculatedSha256, vm.DiscSha256);
        Assert.Contains("PASSED", vm.VerificationStatusText);
    }

    [Fact]
    public async Task ImageStudioViewModel_InspectBurnAndVerifyCue_SucceedsWithBitForBitMatch()
    {
        // Arrange: Generate raw binary image and companion CUE sheet
        // 20 raw 2,352-byte sectors = 47,040 bytes
        const int rawSectors = 20;
        int rawBytes = rawSectors * OpticalConstants.RawSectorSize;
        byte[] binaryData = new byte[rawBytes];
        for (int i = 0; i < rawBytes; i++)
        {
            binaryData[i] = (byte)((i * 13) % 256);
        }

        string binPath = Path.Combine(_tempDir, "game_image.bin");
        string cuePath = Path.Combine(_tempDir, "game_image.cue");

        await File.WriteAllBytesAsync(binPath, binaryData);

        var cueTrack = new CueTrack(
            TrackNumber: 1,
            DataType: CueTrackDataType.Mode1_2352,
            Title: "Track One",
            Indices: new[] { new CueIndex(1, new MsfAddress(0, 0, 0)) }
        );
        var cueFile = new CueFileEntry(Path.GetFileName(binPath), "BINARY", new[] { cueTrack });
        var cueSheet = new CueSheet(Title: "Game Disc Archive", Files: new[] { cueFile });
        await CueSheetWriter.WriteToFileAsync(cueSheet, cuePath);

        var vm = new ImageStudioViewModel(_deviceManager, _burnEngine);

        // Act 1: Inspect CUE sheet
        vm.SourceImagePath = cuePath;
        vm.InspectImageCommand.Execute(null);

        // Assert 1: DetectedImageFormat is CUE, ImageSectorCount is 20, ImageSizeBytes matches bin
        Assert.NotNull(vm.DetectedImageFormat);
        Assert.Contains("CUE", vm.DetectedImageFormat);
        Assert.Equal(rawSectors, vm.ImageSectorCount);
        Assert.Equal(rawBytes, vm.ImageSizeBytes);

        // Act 2: Burn CUE image to VDRV-01
        var burnResult = await vm.BurnImageToDiscAsync("VDRV-01");

        // Assert 2: Burn succeeded
        Assert.True(burnResult.Success, $"CUE burn failed: {burnResult.ErrorMessage}");

        // Act 3: Verify disc against image
        var verifyResult = await vm.VerifyDiscAgainstImageAsync("VDRV-01");

        // Assert 3: Bit-for-bit checksum match
        Assert.True(vm.IsChecksumMatch, $"Checksum mismatch: {vm.VerificationStatusText}");
        Assert.False(string.IsNullOrWhiteSpace(vm.CalculatedSha256));
        Assert.False(string.IsNullOrWhiteSpace(vm.DiscSha256));
        Assert.Equal(vm.CalculatedSha256, vm.DiscSha256);
        Assert.Contains("PASSED", vm.VerificationStatusText);
    }

    [Fact]
    public async Task ImageStudioViewModel_RipDiscToIsoAndBinCue_ExtractsExpectedImages()
    {
        // Arrange: Populate VDRV-01 with synthetic data
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] sectorData = new byte[OpticalConstants.DataSectorSize];
        Array.Fill(sectorData, (byte)0xAB);
        for (int i = 0; i < 15; i++)
        {
            medium.WriteSector(i, sectorData, OpticalConstants.DataSectorSize);
        }
        medium.UsedSectors = 15;
        medium.IsFinalized = true;

        var vEngine = new VirtualBurnEngine(recorder);
        var vm = new ImageStudioViewModel(_deviceManager, vEngine)
        {
            SelectedRecorder = recorder
        };

        // Act 1: Rip to ISO Mode 1
        string isoOut = Path.Combine(_tempDir, "ripped_disc.iso");
        vm.RipTargetFilePath = isoOut;
        vm.RipRawBinCueMode = false;
        var ripIsoResult = await vm.RipDiscToImageAsync(recorder.UniqueId, isoOut, rawMode: false);

        // Assert 1: ISO ripped successfully
        Assert.True(ripIsoResult.Success, $"Rip ISO failed: {ripIsoResult.ErrorMessage}");
        Assert.True(File.Exists(isoOut));
        Assert.Equal(15 * OpticalConstants.DataSectorSize, new FileInfo(isoOut).Length);

        // Act 2: Rip to raw BIN + CUE
        string binOut = Path.Combine(_tempDir, "ripped_raw.bin");
        vm.RipTargetFilePath = binOut;
        vm.RipRawBinCueMode = true;
        var ripBinResult = await vm.RipDiscToImageAsync(recorder.UniqueId, binOut, rawMode: true);

        // Assert 2: BIN and companion CUE exist
        Assert.True(ripBinResult.Success, $"Rip BIN failed: {ripBinResult.ErrorMessage}");
        Assert.True(File.Exists(binOut));
        string cueOut = Path.ChangeExtension(binOut, ".cue");
        Assert.True(File.Exists(cueOut));
    }
}
