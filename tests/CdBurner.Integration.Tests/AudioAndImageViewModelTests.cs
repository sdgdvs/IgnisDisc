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
/// Unit and integration tests for WPF Audio CD Studio &amp; Disc Image Studio Views.
/// Validates AudioBurnViewModel (track sequencing, re-indexing, total duration, burning, ripping)
/// and ImageStudioViewModel (inspecting .iso/.cue, burning to virtual recorder, verifying matching SHA-256 digests, ripping).
/// </summary>
public sealed class AudioAndImageViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public AudioAndImageViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Tests_" + Guid.NewGuid().ToString("N"));
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
            AlbumTitle = "Synthwave Album",
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
        root.AddFile("READ_ME.TXT", "Welcome to Disc Image Studio."u8.ToArray());
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

    [Fact]
    public async Task AudioBurnViewModel_AddFlacFolder_PopulatesVorbisMetadataFormatSummaryAndBurns()
    {
        string flacFolder = Path.Combine(_tempDir, "FlacAlbumDrop");
        Directory.CreateDirectory(flacFolder);

        byte[] pcm1 = new byte[50 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[60 * OpticalConstants.RawSectorSize];

        string flac1 = Path.Combine(flacFolder, "01_Opening.flac");
        string flac2 = Path.Combine(flacFolder, "02_Closing.flac");

        FlacFileCodec.WriteFlacFile(
            flac1,
            pcm1,
            tags: new AudioTrackTags(
                Title: "Opening Theme (FLAC)",
                Artist: "Studio Quartet",
                Album: "Lossless Masterworks",
                TrackNumber: 1,
                TrackTotal: 2,
                Isrc: "US-FLC-26-10001"),
            songwriter: "C. Composer");

        FlacFileCodec.WriteFlacFile(
            flac2,
            pcm2,
            tags: new AudioTrackTags(
                Title: "Closing Theme (FLAC)",
                Artist: "Studio Quartet",
                Album: "Lossless Masterworks",
                TrackNumber: 2,
                TrackTotal: 2,
                Isrc: "US-FLC-26-10002"));

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);

        // Act 1: Drop the folder path onto AddAudioFilesCommand
        vm.AddAudioFilesCommand.Execute(new[] { flacFolder });

        // Assert 1: Both FLAC files discovered, Vorbis comments and FormatSummary populated
        Assert.True(vm.HasTracks);
        Assert.Equal(2, vm.Tracks.Count);
        Assert.Equal("Lossless Masterworks", vm.AlbumTitle);
        Assert.Equal("Studio Quartet", vm.AlbumPerformer);

        Assert.Equal("Opening Theme (FLAC)", vm.Tracks[0].Title);
        Assert.Equal("Studio Quartet", vm.Tracks[0].Performer);
        Assert.Equal("C. Composer", vm.Tracks[0].Songwriter);
        Assert.Equal("US-FLC-26-10001", vm.Tracks[0].Isrc);
        Assert.Equal("FLAC 16-bit", vm.Tracks[0].FormatName);
        Assert.Equal("FLAC 16-bit / 44.1 kHz", vm.Tracks[0].FormatSummary);

        Assert.Equal("Closing Theme (FLAC)", vm.Tracks[1].Title);
        Assert.Equal("US-FLC-26-10002", vm.Tracks[1].Isrc);
        Assert.Equal(110, vm.TotalDuration.ToFrameCount());

        // Act 2: Burn to VDRV-01
        var burnResult = await vm.StartAudioBurnAsync("VDRV-01");
        Assert.True(burnResult.Success, $"FLAC Audio CD burn failed: {burnResult.ErrorMessage}");
        Assert.Equal(110, burnResult.SectorsWritten);
    }

    [Fact]
    public async Task ImageStudioViewModel_InspectAndBurnAudioFlacCueSheet_Succeeds()
    {
        string flac1 = Path.Combine(_tempDir, "cue_track01.flac");
        string flac2 = Path.Combine(_tempDir, "cue_track02.flac");
        byte[] pcm1 = new byte[30 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[45 * OpticalConstants.RawSectorSize];
        FlacFileCodec.WriteFlacFile(flac1, pcm1);
        FlacFileCodec.WriteFlacFile(flac2, pcm2);

        string cuePath = Path.Combine(_tempDir, "flac_compilation.cue");
        string cueText = """
            PERFORMER "FLAC CUE Artist"
            TITLE "FLAC CUE Album"
            FILE "cue_track01.flac" WAVE
              TRACK 01 AUDIO
                TITLE "FLAC Track 1"
                PERFORMER "FLAC CUE Artist"
                INDEX 01 00:00:00
            FILE "cue_track02.flac" WAVE
              TRACK 02 AUDIO
                TITLE "FLAC Track 2"
                PERFORMER "FLAC CUE Artist"
                INDEX 01 00:00:00
            """;
        await File.WriteAllTextAsync(cuePath, cueText);

        var vm = new ImageStudioViewModel(_deviceManager, _burnEngine);
        vm.SourceImagePath = cuePath;
        vm.InspectImageCommand.Execute(null);

        // 30 + 45 = 75 Red Book sectors
        Assert.Equal(75, vm.ImageSectorCount);
        Assert.Equal(75L * OpticalConstants.RawSectorSize, vm.ImageSizeBytes);

        var burnResult = await vm.BurnImageToDiscAsync("VDRV-01");
        Assert.True(burnResult.Success, $"ImageStudio FLAC CUE burn failed: {burnResult.ErrorMessage}");
        Assert.Equal(75, burnResult.SectorsWritten);
    }

    [Fact]
    public async Task ImageStudioViewModel_LegacyNrgCcdMds_InspectsConvertsBurnsAndVerifies()
    {
        // Build a synthetic 2048-byte Mode 1 ISO payload
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("LEGACY.TXT", "Legacy Nero / CloneCD / Alcohol 120% test payload."u8.ToArray());
        using var isoMem = new MemoryStream();
        await IsoUdfImageBuilder.BuildAsync(root, isoMem, new IsoBuildOptions(VolumeLabel: "LEGACY_DISC"));
        byte[] isoBytes = isoMem.ToArray();
        int expectedSectors = isoBytes.Length / OpticalConstants.DataSectorSize;

        string nrgPath = Path.Combine(_tempDir, "archive.nrg");
        string ccdPath = Path.Combine(_tempDir, "clonecd.ccd");
        string mdsPath = Path.Combine(_tempDir, "alcohol.mds");

        LegacyDiscImageConverter.CreateNrgFile(nrgPath, isoBytes);
        LegacyDiscImageConverter.CreateCloneCdImage(ccdPath, isoBytes);
        LegacyDiscImageConverter.CreateAlcoholMdsMdfImage(mdsPath, isoBytes);

        var vm = new ImageStudioViewModel(_deviceManager, _burnEngine);

        // 1. Inspect & Convert Nero .nrg
        vm.SourceImagePath = nrgPath;
        Assert.True(vm.IsLegacyImageLoaded);
        Assert.Contains("Nero", vm.DetectedImageFormat);
        Assert.Equal(expectedSectors, vm.ImageSectorCount);

        string convertedIsoPath = Path.Combine(_tempDir, "from_nrg.iso");
        string? convertedOut = await vm.ConvertLegacyImageToIsoAsync(convertedIsoPath);
        Assert.Equal(convertedIsoPath, convertedOut);
        Assert.Equal(isoBytes, await File.ReadAllBytesAsync(convertedIsoPath));

        // 2. Burn CloneCD .ccd/.img directly & verify bit-for-bit
        vm.SourceImagePath = ccdPath;
        Assert.True(vm.IsLegacyImageLoaded);
        Assert.Contains("CloneCD", vm.DetectedImageFormat);
        Assert.Equal(expectedSectors, vm.ImageSectorCount);

        var ccdBurn = await vm.BurnImageToDiscAsync("VDRV-01");
        Assert.True(ccdBurn.Success, ccdBurn.ErrorMessage);
        Assert.Equal(expectedSectors, ccdBurn.SectorsWritten);
        Assert.True(vm.IsChecksumMatch, vm.VerificationStatusText);
        Assert.NotEmpty(vm.OperationLogEntries);

        // 3. Inspect Alcohol 120% .mds/.mdf
        vm.SourceImagePath = mdsPath;
        Assert.True(vm.IsLegacyImageLoaded);
        Assert.Contains("Alcohol 120%", vm.DetectedImageFormat);
        Assert.Equal(expectedSectors, vm.ImageSectorCount);
    }

    [Fact]
    public async Task ImageStudioViewModel_OneClickDiscCopy_CopiesMultiCopyBatchAndVerifies()
    {
        // Arrange: Populate VDRV-01 with a recorded ISO disc
        var sourceRec = (VirtualDiscRecorder)_deviceManager.GetRecorders().First(r => r.UniqueId == "VDRV-01");
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("MASTER.DAT", new byte[32768]);
        using var isoMem = new MemoryStream();
        await IsoUdfImageBuilder.BuildAsync(root, isoMem, new IsoBuildOptions(VolumeLabel: "MASTER_COPY"));
        byte[] isoBytes = isoMem.ToArray();
        int totalSectors = isoBytes.Length / OpticalConstants.DataSectorSize;

        using var sourceMedium = VirtualDiscMedium.CreateBlankCdR700();
        for (int i = 0; i < totalSectors; i++)
        {
            sourceMedium.WriteSector(i, isoBytes.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize).ToArray(), OpticalConstants.DataSectorSize);
        }
        sourceMedium.UsedSectors = totalSectors;
        sourceMedium.IsFinalized = true;
        sourceRec.InsertMedia(sourceMedium);

        var vm = new ImageStudioViewModel(_deviceManager, _burnEngine)
        {
            CopySourceRecorder = sourceRec,
            CopyTargetRecorder = sourceRec,
            CopyCount = 2,
            CopyVerifyEachDisc = true,
            CopyRawCloneMode = false
        };

        // Act: Run 1-Click Disc Copy for 2 batch copies (single-drive swap automatically mounts blank media on virtual target)
        var copyResult = await vm.CopyDiscAsync();

        // Assert
        Assert.True(copyResult.Success, copyResult.ErrorMessage);
        Assert.Equal(2, copyResult.CopiesCompleted);
        Assert.Equal(totalSectors, copyResult.SectorsPerDisc);
        Assert.Equal(totalSectors * 2, copyResult.TotalSectorsWritten);
    }

    [Fact]
    public async Task AudioBurnViewModel_PlaylistsCrossfadePreviewAndPrintableJewelCaseCover_WorkEndToEnd()
    {
        // Arrange: Create 2 FLAC tracks and an Extended M3U8 playlist + a PLS playlist
        byte[] pcm1 = new byte[150 * OpticalConstants.RawSectorSize]; // 2.0 sec
        byte[] pcm2 = new byte[150 * OpticalConstants.RawSectorSize]; // 2.0 sec
        for (int i = 0; i < pcm1.Length / 2; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm1.AsSpan(i * 2, 2), 6000);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm2.AsSpan(i * 2, 2), 12000);
        }

        string track1Path = Path.Combine(_tempDir, "pl_track01.flac");
        string track2Path = Path.Combine(_tempDir, "pl_track02.flac");
        FlacFileCodec.WriteFlacFile(track1Path, pcm1);
        FlacFileCodec.WriteFlacFile(track2Path, pcm2);

        string m3u8Path = Path.Combine(_tempDir, "session.m3u8");
        await File.WriteAllTextAsync(m3u8Path, """
            #EXTM3U
            #EXTALB:Playlist Mastered Album
            #EXTINF:2,The Synth Collective - Dawn Chorus
            pl_track01.flac
            #EXTINF:2,The Synth Collective - Midnight Echo
            pl_track02.flac
            """);

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine)
        {
            UseRmsLoudnessLeveling = true,
            CrossfadeSeconds = 0.5
        };

        // Act 1: Import M3U8 playlist
        vm.AddAudioFilesCommand.Execute(new[] { m3u8Path });

        Assert.Equal(2, vm.Tracks.Count);
        Assert.Equal("Playlist Mastered Album", vm.AlbumTitle);
        Assert.Equal("The Synth Collective", vm.AlbumPerformer);
        Assert.Equal("Dawn Chorus", vm.Tracks[0].Title);
        Assert.Equal("Midnight Echo", vm.Tracks[1].Title);

        // Act 2: Built-in Audio Track Preview Player
        vm.SelectedTrack = vm.Tracks[0];
        await vm.PreviewTrackAsync(); // the preview command runs this in the background
        Assert.True(vm.IsPreviewPlaying);
        Assert.Contains("Playing Track 01: Dawn Chorus", vm.PreviewStatusText);

        vm.StopPreviewCommand.Execute(null);
        Assert.False(vm.IsPreviewPlaying);

        // Act 3: Printable CD Jewel Case Cover & Tracklist Generator
        string coverHtmlPath = Path.Combine(_tempDir, "JewelCaseInsert.html");
        string? exportedPath = vm.ExportJewelCaseCoverHtml(coverHtmlPath);
        Assert.Equal(coverHtmlPath, exportedPath);
        Assert.True(File.Exists(coverHtmlPath));
        string htmlContent = await File.ReadAllTextAsync(coverHtmlPath);
        Assert.Contains("Playlist Mastered Album", htmlContent);
        Assert.Contains("The Synth Collective", htmlContent);
        Assert.Contains("Dawn Chorus", htmlContent);
        Assert.Contains("Midnight Echo", htmlContent);
        Assert.Contains("120mm", htmlContent);
        Assert.Contains("150mm", htmlContent);

        // Act 4: Burn with RMS Loudness Leveling & 0.5s Crossfade
        var burnRes = await vm.StartAudioBurnAsync("VDRV-01");
        Assert.True(burnRes.Success, burnRes.ErrorMessage);

        // The crossfade overlaps the tracks: 0.5 s (22,050 samples) rounds down to 37 whole sectors, which are mixed into
        // track 1's tail and removed from track 2's head, so the transition plays once and the disc is 37 sectors shorter.
        Assert.Equal(300 - 37, burnRes.SectorsWritten);
    }

    [Fact]
    public async Task ImageStudioViewModel_ConvertImage_IsoToBinCueAndCueToIso_ViaViewModel()
    {
        // Arrange
        byte[] isoBytes = new byte[12 * OpticalConstants.DataSectorSize];
        for (int i = 0; i < isoBytes.Length; i++)
        {
            isoBytes[i] = (byte)((i * 41 + 19) % 256);
        }
        string isoPath = Path.Combine(_tempDir, "studio_test.iso");
        await File.WriteAllBytesAsync(isoPath, isoBytes);

        var vm = new ImageStudioViewModel(_deviceManager, _burnEngine);
        vm.SourceImagePath = isoPath;

        Assert.True(vm.IsLegacyImageLoaded); // Convertible image loaded

        // Act 1: Convert ISO -> BIN/CUE using LegacyConvertOutputPath
        string binPath = Path.Combine(_tempDir, "studio_converted.bin");
        string cuePath = Path.Combine(_tempDir, "studio_converted.cue");
        vm.LegacyConvertOutputPath = binPath;

        string? resultPath = await vm.ConvertLegacyImageAsync();
        Assert.Equal(binPath, resultPath);
        Assert.True(File.Exists(binPath));
        Assert.True(File.Exists(cuePath));
        Assert.Contains("Converted to", vm.StatusMessage);

        // Act 2: Convert CUE -> ISO
        vm.SourceImagePath = cuePath;
        Assert.True(vm.IsLegacyImageLoaded);

        string reconvertedIsoPath = Path.Combine(_tempDir, "studio_reconverted.iso");
        vm.LegacyConvertOutputPath = reconvertedIsoPath;

        string? isoResult = await vm.ConvertLegacyImageAsync();
        Assert.Equal(reconvertedIsoPath, isoResult);
        Assert.True(File.Exists(reconvertedIsoPath));

        byte[] originalHash = SHA256.HashData(isoBytes);
        byte[] reconvertedHash = SHA256.HashData(await File.ReadAllBytesAsync(reconvertedIsoPath));
        Assert.Equal(originalHash, reconvertedHash);
    }

    [Fact]
    public void AudioBurnViewModel_ImportMixedModeCue_ShowsNoticeInStatusMessage()
    {
        // Arrange: Generate 2 synthetic WAV audio files and 1 dummy data file
        string dataBin = Path.Combine(_tempDir, "game_data.bin");
        File.WriteAllBytes(dataBin, new byte[OpticalConstants.DataSectorSize * 400]); // over the 4-second track minimum

        string wav1 = Path.Combine(_tempDir, "MixTrack01.wav");
        string wav2 = Path.Combine(_tempDir, "MixTrack02.wav");
        byte[] pcm1 = new byte[75 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[150 * OpticalConstants.RawSectorSize];
        for (int i = 0; i < pcm1.Length; i++) pcm1[i] = (byte)(i % 256);
        for (int i = 0; i < pcm2.Length; i++) pcm2[i] = (byte)((i * 3) % 256);
        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);

        // Write mixed-mode CUE sheet
        string cuePath = Path.Combine(_tempDir, "mixed_album.cue");
        string cueContent = $"""
            TITLE "Mixed Mode Album"
            PERFORMER "Mixed Artist"
            FILE "game_data.bin" BINARY
              TRACK 01 MODE1/2048
                INDEX 01 00:00:00
            FILE "MixTrack01.wav" WAVE
              TRACK 02 AUDIO
                INDEX 01 00:00:00
            FILE "MixTrack02.wav" WAVE
              TRACK 03 AUDIO
                INDEX 01 00:00:00
            """;
        File.WriteAllText(cuePath, cueContent);

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);

        // Act: Add mixed-mode CUE
        vm.AddAudioFilesCommand.Execute(new[] { cuePath });

        // Assert: the Mode 1 data track is kept as track 1 (a mixed-mode CD), then the two audio tracks
        Assert.Equal(3, vm.Tracks.Count);
        Assert.True(vm.Tracks[0].IsDataTrack);
        Assert.Equal(400, vm.Tracks[0].DataTrack!.SectorCount);
        Assert.Equal("MixTrack01", vm.Tracks[1].Title);
        Assert.Equal("MixTrack02", vm.Tracks[2].Title);

        // The notice says the disc is mixed-mode and which writer can write it
        Assert.Contains("only direct drive commands can write", vm.StatusMessage);
    }

    [Fact]
    public void AudioBurnViewModel_CueFlagsGapMarksAndHiddenAudio_ReachTheBurn_AndAGapMarkGoesWhenItsTrackMoves()
    {
        // One WAV of noise: 10 s before track 1's INDEX 01, track 2 with flags and a 2-second INDEX 00, then track 3.
        var pcm = new byte[100 * 75 * OpticalConstants.RawSectorSize];
        new Random(77).NextBytes(pcm);
        WavFileCodec.WriteWavFile(Path.Combine(_tempDir, "album.wav"), pcm);
        string cuePath = Path.Combine(_tempDir, "flags.cue");
        File.WriteAllLines(cuePath, new[]
        {
            "FILE \"album.wav\" WAVE",
            "  TRACK 01 AUDIO", "    TITLE \"First\"", "    INDEX 01 00:10:00",
            "  TRACK 02 AUDIO", "    TITLE \"Second\"", "    FLAGS PRE DCP", "    INDEX 00 00:38:00", "    INDEX 01 00:40:00",
            "  TRACK 03 AUDIO", "    TITLE \"Third\"", "    INDEX 01 01:10:00"
        });
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);

        vm.AddAudioFilesCommand.Execute(new[] { cuePath });
        var compilation = vm.BuildCurrentCompilation();

        Assert.Equal(new[] { "First", "Second", "Third" }, compilation.Tracks.Select(t => t.Title));
        Assert.Equal(750, compilation.Tracks[0].HiddenAudioFrames);           // 10 s in front of track 1
        Assert.NotNull(compilation.Tracks[0].HiddenAudioPcmStreamFactory);
        Assert.True(compilation.Tracks[1].PreEmphasis);
        Assert.True(compilation.Tracks[1].CopyPermitted);
        Assert.Equal(150, compilation.Tracks[1].Index00LeadFrames);           // the 2-second count-in
        Assert.False(compilation.Tracks[2].PreEmphasis);

        // Move "Third" above "Second": the frames before "Second" now end "Third", so the mark is not kept.
        vm.SelectedTrack = vm.Tracks[2];
        vm.MoveTrackUpCommand.Execute(null);
        var reordered = vm.BuildCurrentCompilation();

        Assert.Equal(new[] { "First", "Third", "Second" }, reordered.Tracks.Select(t => t.Title));
        Assert.Equal(0, reordered.Tracks[2].Index00LeadFrames);
        Assert.True(reordered.Tracks[2].PreEmphasis);                          // a track's own flags stay with it
    }

    [Fact]
    public async Task DiscCopyService_MixedModeCd_WarnsThatOnlyAudioIsCopied_EvenInRawMode()
    {
        // Arrange: Populate VDRV-01 with a mixed-mode medium (1 data track + 1 audio track)
        var sourceRec = (VirtualDiscRecorder)_deviceManager.GetRecorders().First(r => r.UniqueId == "VDRV-01");
        var targetRec = new VirtualDiscRecorder { UniqueId = "VDRV-02" };

        using var mixedMedium = VirtualDiscMedium.CreateBlankCdR700();
        for (int i = 0; i < 200; i++)
        {
            mixedMedium.WriteSector(i, new byte[OpticalConstants.RawSectorSize], OpticalConstants.RawSectorSize);
        }
        var session = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = 200 };
        session.Tracks.Add(new VirtualTrack { TrackNumber = 1, StartLba = 0, SectorCount = 75, IsAudio = false });
        session.Tracks.Add(new VirtualTrack { TrackNumber = 2, StartLba = 75, SectorCount = 125, IsAudio = true });
        mixedMedium.Sessions.Add(session);
        mixedMedium.UsedSectors = 200;
        mixedMedium.IsFinalized = true;
        sourceRec.InsertMedia(mixedMedium);

        // Act 1: Check layout inspection directly
        var (isAudio, isMixed) = DiscCopyService.InspectSourceDiscLayout(sourceRec);
        Assert.True(isAudio);
        Assert.True(isMixed);

        // Act 2: Copy without RawCloneMode -> warns about mixed-mode / Enhanced CD
        using var blankTarget = VirtualDiscMedium.CreateBlankCdR700();
        targetRec.InsertMedia(blankTarget);

        var copyService = new DiscCopyService(_burnEngine);
        string? reportedWarning = null;
        var progress = new Progress<BurnProgressSnapshot>(p =>
        {
            if (p.CurrentActionDescription.Contains("Mixed-mode"))
            {
                reportedWarning = p.CurrentActionDescription;
            }
        });

        var result = await copyService.CopyDiscAsync(
            sourceRec,
            targetRec,
            new DiscCopyOptions
            {
                RawCloneMode = false,
                VerifyEachCopy = false
            },
            progress);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.IsMixedMode);
        Assert.True(result.WasAudioCd);
        Assert.NotNull(result.WarningMessage);
        Assert.Contains("only the audio tracks are copied", result.WarningMessage);

        // Act 3: Raw Clone Mode must not change that. IMAPIv2's data writer cannot write raw 2,352-byte sectors, so
        // routing an audio CD through the raw data path would burn raw sector bytes as data: an unreadable disc.
        using var blankTarget2 = VirtualDiscMedium.CreateBlankCdR700();
        targetRec.InsertMedia(blankTarget2);
        var rawResult = await copyService.CopyDiscAsync(
            sourceRec,
            targetRec,
            new DiscCopyOptions { RawCloneMode = true, VerifyEachCopy = false },
            progress);

        Assert.True(rawResult.Success, rawResult.ErrorMessage);
        Assert.True(rawResult.WasAudioCd);
        Assert.Contains("only the audio tracks are copied", rawResult.WarningMessage);
    }
}