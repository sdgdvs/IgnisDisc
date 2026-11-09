using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// End-to-End Integration Suite &amp; Full-System Release Acceptance Tests.
/// Exercises the four foundational optical workflows across core, hardware simulation,
/// and file-system/codec engines:
/// 1. Dual BIOS/UEFI El Torito Bootable Data project (.cdbproj persistence, ISO build, VirtualDiscRecorder burn, rip &amp; SHA-256/boot catalog verification).
/// 2. 3-Track Audio CD with CD-Text and Peak Normalization (DAO burn, DAE rip to .wav, metadata and audio frame verification).
/// 3. VIDEO_TS DVD-Video layout (32 KB ECC separation between .IFO and .BUP, virtual DVD-R burn, surface defect scanning).
/// 4. Optical media erasing on virtual CD-RW (quick and full erase, blank state restoration verification).
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class FullSystemAcceptanceTests : IDisposable
{
    private readonly string _testTempDir;

    public FullSystemAcceptanceTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Acceptance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp folder cleanup exceptions
        }
    }

    #region Workflow 1: Dual BIOS/UEFI El Torito Bootable Project

    [Fact]
    public async Task AcceptanceWorkflow1_BootableDataDisc_CdbProjRoundTrip_BuildIso_BurnToVirtualDrive_RipAndVerifySha256AndElToritoCatalog()
    {
        // 1. Arrange source boot binaries and data files on disk
        string biosBootPath = Path.Combine(_testTempDir, "etfsboot.com");
        string uefiBootPath = Path.Combine(_testTempDir, "efisys.bin");
        string installTxtPath = Path.Combine(_testTempDir, "INSTALL.TXT");
        string readmeTxtPath = Path.Combine(_testTempDir, "README.TXT");

        // BIOS payload: 4 sectors = 8192 bytes
        byte[] biosBootBytes = Enumerable.Range(0, 4 * OpticalConstants.DataSectorSize)
            .Select(i => (byte)(0x33 ^ (i % 251)))
            .ToArray();
        await File.WriteAllBytesAsync(biosBootPath, biosBootBytes);

        // UEFI payload: 16 sectors = 32768 bytes
        byte[] uefiBootBytes = Enumerable.Range(0, 16 * OpticalConstants.DataSectorSize)
            .Select(i => (byte)(0x77 ^ (i % 241)))
            .ToArray();
        await File.WriteAllBytesAsync(uefiBootPath, uefiBootBytes);

        await File.WriteAllTextAsync(installTxtPath, "IgnisDisc acceptance test installer script\r\nKernel Init Parameters: quiet splash");
        await File.WriteAllTextAsync(readmeTxtPath, "IgnisDisc acceptance test build");

        // 2. Author a dual BIOS/UEFI El Torito bootable project document (.cdbproj)
        string projectPath = Path.Combine(_testTempDir, "DualBootRelease.cdbproj");
        var originalProject = new CdBurnerProjectDocument
        {
            SchemaVersion = "1.0",
            Kind = ProjectKind.BootableDisc,
            Name = "Dual Boot System Disc",
            Description = "End-to-end acceptance test multi-boot media",
            VolumeLabel = "DUAL_BOOT_SYS",
            TargetMediaType = DiscMediaType.CdR,
            DefaultDuplicatePolicy = DuplicateFilePolicy.Overwrite,
            RootNode = new SerializedCompilationNode
            {
                IsDirectory = true,
                Name = "",
                Children =
                {
                    new SerializedCompilationNode
                    {
                        IsDirectory = false,
                        Name = "INSTALL.TXT",
                        SourceFilePath = installTxtPath,
                        SizeBytes = new FileInfo(installTxtPath).Length,
                        Attributes = FileAttributes.ReadOnly
                    },
                    new SerializedCompilationNode
                    {
                        IsDirectory = true,
                        Name = "DOCS",
                        Children =
                        {
                            new SerializedCompilationNode
                            {
                                IsDirectory = false,
                                Name = "README.TXT",
                                SourceFilePath = readmeTxtPath,
                                SizeBytes = new FileInfo(readmeTxtPath).Length,
                                Attributes = FileAttributes.Normal
                            }
                        }
                    }
                }
            },
            BootOptions = new SerializedBootOptions
            {
                DeveloperId = "CDBURNER_TEST",
                DefaultEntry = new SerializedBootCatalogEntry
                {
                    Platform = BootPlatformId.X86Bios,
                    Emulation = BootEmulationType.NoEmulation,
                    SectorCount = 4,
                    LoadSegment = 0x07C0,
                    BootImageFilePath = biosBootPath,
                    Name = "etfsboot.com",
                    SystemType = 0x00
                },
                AdditionalSectionEntries =
                {
                    new SerializedBootCatalogEntry
                    {
                        Platform = BootPlatformId.Efi,
                        Emulation = BootEmulationType.NoEmulation,
                        SectorCount = 16,
                        LoadSegment = 0x0000,
                        BootImageFilePath = uefiBootPath,
                        Name = "efisys.bin",
                        SystemType = 0xEF
                    }
                }
            }
        };

        // 3. Save project to disk
        await ProjectSerializer.SaveToFileAsync(originalProject, projectPath);
        Assert.True(File.Exists(projectPath));

        // 4. Reload project from disk and verify roundtrip integrity
        var loadedProject = await ProjectSerializer.LoadFromFileAsync(projectPath);
        Assert.NotNull(loadedProject);
        Assert.Equal(ProjectKind.BootableDisc, loadedProject.Kind);
        Assert.Equal("DUAL_BOOT_SYS", loadedProject.VolumeLabel);
        Assert.NotNull(loadedProject.BootOptions);
        Assert.Equal("CDBURNER_TEST", loadedProject.BootOptions.DeveloperId);
        Assert.NotNull(loadedProject.BootOptions.DefaultEntry);
        Assert.Equal(BootPlatformId.X86Bios, loadedProject.BootOptions.DefaultEntry.Platform);
        Assert.Equal("etfsboot.com", loadedProject.BootOptions.DefaultEntry.Name);
        Assert.Single(loadedProject.BootOptions.AdditionalSectionEntries);
        Assert.Equal(BootPlatformId.Efi, loadedProject.BootOptions.AdditionalSectionEntries[0].Platform);
        Assert.Equal("efisys.bin", loadedProject.BootOptions.AdditionalSectionEntries[0].Name);

        // 5. Convert deserialized project into live CompilationDirectory and ElToritoBootConfiguration
        var rootDir = ProjectSerializer.ToCompilationDirectory(loadedProject.RootNode!);
        var bootConfig = ProjectSerializer.ToBootConfiguration(loadedProject.BootOptions);
        Assert.NotNull(bootConfig);

        // 6. Build ISO image in-memory with ISO 9660, Joliet, UDF, and dual El Torito catalog
        var isoOptions = new IsoBuildOptions(
            VolumeLabel: loadedProject.VolumeLabel,
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true,
            BootConfiguration: bootConfig);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(rootDir, isoOptions);
        byte[] originalIsoBytes = isoStream.ToArray();
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(originalIsoBytes));

        Assert.True(originalIsoBytes.Length > 0);
        Assert.Equal(0, originalIsoBytes.Length % OpticalConstants.DataSectorSize);

        // 7. Mount blank CD-R into VirtualDiscRecorder and burn ISO stream
        using var recorder = new VirtualDiscRecorder("VDRV-P24-BOOT");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new DataBurnService(engine);

        isoStream.Position = 0;
        var burnResult = await burnService.BurnStreamAsync(
            recorder,
            isoStream,
            new DataBurnOptions { VolumeLabel = loadedProject.VolumeLabel, CloseDisc = true });

        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");
        Assert.False(medium.IsBlank);
        Assert.True(medium.IsFinalized);

        // 8. Rip disc image back to a separate stream using DiscRipperService
        var ripper = new DiscRipperService();
        using var rippedIsoStream = new MemoryStream();
        var ripResult = await ripper.RipToIsoAsync(recorder, engine, rippedIsoStream);

        Assert.True(ripResult.Success, $"Rip failed: {ripResult.ErrorMessage}");
        Assert.Equal(0, ripResult.BadSectorsCount);
        Assert.Empty(ripResult.BadSectorLbas);

        // 9. Verify exact byte count and SHA-256 hash match original ISO
        byte[] rippedIsoBytes = rippedIsoStream.ToArray();
        Assert.Equal(originalIsoBytes.Length, rippedIsoBytes.Length);

        string actualSha256 = Convert.ToHexString(SHA256.HashData(rippedIsoBytes));
        Assert.Equal(expectedSha256, actualSha256);

        // 10. Inspect ripped ISO with ElToritoCatalogInspector and verify dual BIOS & UEFI structures
        rippedIsoStream.Position = 0;
        var bootInfo = ElToritoCatalogInspector.ParseBootInfo(rippedIsoStream);

        Assert.Equal(17, bootInfo.BootRecordLba);
        Assert.Equal("EL TORITO SPECIFICATION", bootInfo.BootSystemIdentifier);

        // Validation Entry
        Assert.True(bootInfo.ValidationEntry.IsChecksumValid);
        Assert.Equal("CDBURNER_TEST", bootInfo.ValidationEntry.DeveloperId);
        Assert.Equal(BootPlatformId.X86Bios, bootInfo.ValidationEntry.Platform);

        // Default Initial Boot Entry (BIOS)
        Assert.Equal(BootPlatformId.X86Bios, bootInfo.DefaultEntry.Platform);
        Assert.True(bootInfo.DefaultEntry.Bootable);
        Assert.Equal(BootEmulationType.NoEmulation, bootInfo.DefaultEntry.Emulation);
        Assert.Equal(4, bootInfo.DefaultEntry.SectorCount);
        Assert.True(bootInfo.DefaultEntry.LoadRba > 17);

        // Additional Section Entry (UEFI)
        Assert.Single(bootInfo.SectionEntries);
        var uefiSection = bootInfo.SectionEntries[0];
        Assert.Equal(BootPlatformId.Efi, uefiSection.Platform);
        Assert.True(uefiSection.Bootable);
        Assert.Equal(BootEmulationType.NoEmulation, uefiSection.Emulation);
        Assert.Equal(16, uefiSection.SectorCount);
        Assert.True(uefiSection.LoadRba > bootInfo.DefaultEntry.LoadRba);

        // Verify exact byte contents of the BIOS payload extracted from ripped stream
        byte[] readBiosBytes = new byte[biosBootBytes.Length];
        rippedIsoStream.Position = (long)bootInfo.DefaultEntry.LoadRba * OpticalConstants.DataSectorSize;
        rippedIsoStream.ReadExactly(readBiosBytes);
        Assert.Equal(biosBootBytes, readBiosBytes);

        // Verify exact byte contents of the UEFI payload extracted from ripped stream
        byte[] readUefiBytes = new byte[uefiBootBytes.Length];
        rippedIsoStream.Position = (long)uefiSection.LoadRba * OpticalConstants.DataSectorSize;
        rippedIsoStream.ReadExactly(readUefiBytes);
        Assert.Equal(uefiBootBytes, readUefiBytes);
    }

    #endregion

    #region Workflow 2: 3-Track Audio CD with CD-Text and Peak Normalization

    private static byte[] GenerateSineWavePcm(double frequency, int frameCount, short amplitude)
    {
        int sampleRate = OpticalConstants.CdAudioSampleRate;
        int totalStereoSamples = frameCount * OpticalConstants.CdAudioSamplesPerFrame;
        byte[] pcm = new byte[totalStereoSamples * OpticalConstants.CdAudioBytesPerSample];

        for (int i = 0; i < totalStereoSamples; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)Math.Round(amplitude * Math.Sin(2.0 * Math.PI * frequency * t));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), sample);
        }

        return pcm;
    }

    [Fact]
    public async Task AcceptanceWorkflow2_AudioCdThreeTracks_CdTextAndPeakNormalization_DaoBurn_RipToWavAndVerifyFrames()
    {
        // 1. Arrange: Generate 3 synthetic WAV tracks with moderate amplitudes below full-scale (to verify peak normalization)
        const int track1Frames = 50;  // 117,600 bytes
        const int track2Frames = 75;  // 176,400 bytes
        const int track3Frames = 100; // 235,200 bytes
        const int totalFrames = track1Frames + track2Frames + track3Frames;

        byte[] rawTrack1Pcm = GenerateSineWavePcm(440.0, track1Frames, amplitude: 10000);
        byte[] rawTrack2Pcm = GenerateSineWavePcm(880.0, track2Frames, amplitude: 8000);
        byte[] rawTrack3Pcm = GenerateSineWavePcm(1760.0, track3Frames, amplitude: 12000);

        string wavFile1 = Path.Combine(_testTempDir, "in_track1.wav");
        string wavFile2 = Path.Combine(_testTempDir, "in_track2.wav");
        string wavFile3 = Path.Combine(_testTempDir, "in_track3.wav");

        WavFileCodec.WriteWavFile(wavFile1, rawTrack1Pcm);
        WavFileCodec.WriteWavFile(wavFile2, rawTrack2Pcm);
        WavFileCodec.WriteWavFile(wavFile3, rawTrack3Pcm);

        // 2. Author Audio CD Compilation with Disc-At-Once (DAO), CD-Text, and Peak Normalization
        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Acceptance Suite",
            AlbumPerformer = "CdBurner Symphonic Ensemble",
            Songwriter = "Ada Lovelace",
            Composer = "Alan Turing",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true,
            NormalizeAllTracks = true
        };

        compilation.AddTrack(new AudioCdTrackItem(
            TrackNumber: 1,
            SourceFilePath: wavFile1,
            Title: "Movement 1 - Allegro",
            Performer: "CdBurner Symphonic Ensemble",
            Isrc: "USCB26000001",
            PreGapFrames: 150,
            NormalizeAudio: true));

        compilation.AddTrack(new AudioCdTrackItem(
            TrackNumber: 2,
            SourceFilePath: wavFile2,
            Title: "Movement 2 - Andante",
            Performer: "CdBurner Symphonic Ensemble",
            Isrc: "USCB26000002",
            PreGapFrames: 0,
            NormalizeAudio: true));

        compilation.AddTrack(new AudioCdTrackItem(
            TrackNumber: 3,
            SourceFilePath: wavFile3,
            Title: "Movement 3 - Presto",
            Performer: "CdBurner Symphonic Ensemble",
            Isrc: "USCB26000003",
            PreGapFrames: 0,
            NormalizeAudio: true));

        Assert.Equal(3, compilation.TrackCount);

        // 3. Mount blank CD-R 700 MB into VirtualDiscRecorder
        using var recorder = new VirtualDiscRecorder("VDRV-P24-AUDIO");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new AudioCdBurnService(engine);

        // 4. Burn compilation in Disc-At-Once (DAO) mode
        var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
        Assert.True(burnResult.Success, $"Audio burn failed: {burnResult.ErrorMessage}");
        Assert.Equal(totalFrames, burnResult.SectorsWritten);

        // 5. Verify VirtualDiscMedium Red Book layout and CD-Text
        Assert.False(medium.IsBlank);
        Assert.True(medium.Raw2352Mode);
        Assert.True(medium.IsFinalized);
        Assert.Single(medium.Sessions);

        var session = medium.Sessions[0];
        Assert.Equal(3, session.Tracks.Count);

        Assert.Equal(1, session.Tracks[0].TrackNumber);
        Assert.Equal(0, session.Tracks[0].StartLba);
        Assert.Equal(track1Frames, session.Tracks[0].SectorCount);
        Assert.Equal("Movement 1 - Allegro", session.Tracks[0].Title);

        Assert.Equal(2, session.Tracks[1].TrackNumber);
        Assert.Equal(track1Frames, session.Tracks[1].StartLba);
        Assert.Equal(track2Frames, session.Tracks[1].SectorCount);
        Assert.Equal("Movement 2 - Andante", session.Tracks[1].Title);

        Assert.Equal(3, session.Tracks[2].TrackNumber);
        Assert.Equal(track1Frames + track2Frames, session.Tracks[2].StartLba);
        Assert.Equal(track3Frames, session.Tracks[2].SectorCount);
        Assert.Equal("Movement 3 - Presto", session.Tracks[2].Title);

        // Validate CD-Text binary packs
        Assert.NotNull(medium.CdTextBytes);
        var parsedCdText = CdTextPackParser.ParsePacks(medium.CdTextBytes);
        Assert.Equal("Acceptance Suite", parsedCdText.AlbumTitle);
        Assert.Equal("CdBurner Symphonic Ensemble", parsedCdText.AlbumPerformer);
        Assert.NotNull(parsedCdText.Tracks);
        Assert.Equal(3, parsedCdText.Tracks.Count);
        Assert.Equal("Movement 1 - Allegro", parsedCdText.Tracks[0].Title);
        Assert.Equal("Movement 2 - Andante", parsedCdText.Tracks[1].Title);
        Assert.Equal("Movement 3 - Presto", parsedCdText.Tracks[2].Title);

        // 6. Rip Audio CD back to .wav files using AudioCdRipperService
        string ripFolder = Path.Combine(_testTempDir, "ripped_audio");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(recorder, engine, ripFolder);

        Assert.True(ripResult.Success, $"Audio rip failed: {ripResult.ErrorMessage}");
        Assert.Equal(3, ripResult.TracksRipped);
        Assert.Equal(totalFrames, ripResult.TotalSectorsRead);
        Assert.NotNull(ripResult.CueSheetPath);
        Assert.True(File.Exists(ripResult.CueSheetPath!));

        // 7. Verify all 3 ripped WAV files exist on disk with filenames generated from CD-Text
        Assert.Equal(3, ripResult.RippedFilePaths.Count);
        Assert.All(ripResult.RippedFilePaths, path => Assert.True(File.Exists(path), $"Missing ripped track: {path}"));

        string rippedWav1 = ripResult.RippedFilePaths[0];
        string rippedWav2 = ripResult.RippedFilePaths[1];
        string rippedWav3 = ripResult.RippedFilePaths[2];

        // 8. Probe audio format and verify frame sizes & peak normalization
        var meta1 = WavFileCodec.ProbeAudioFile(rippedWav1);
        Assert.Equal(OpticalConstants.CdAudioSampleRate, meta1.SampleRate);
        Assert.Equal(OpticalConstants.CdAudioChannels, meta1.Channels);
        Assert.Equal(OpticalConstants.CdAudioBitsPerSample, meta1.BitsPerSample);
        Assert.Equal(track1Frames * OpticalConstants.RawSectorSize, meta1.TotalPcmBytes);

        var meta2 = WavFileCodec.ProbeAudioFile(rippedWav2);
        Assert.Equal(track2Frames * OpticalConstants.RawSectorSize, meta2.TotalPcmBytes);

        var meta3 = WavFileCodec.ProbeAudioFile(rippedWav3);
        Assert.Equal(track3Frames * OpticalConstants.RawSectorSize, meta3.TotalPcmBytes);

        // Verify peak normalization scaled the audio close to 32767
        using var stream1 = WavFileCodec.OpenRedBookPcmStream(rippedWav1);
        byte[] pcm1 = new byte[meta1.TotalPcmBytes];
        stream1.ReadExactly(pcm1);

        short peak1 = 0;
        for (int i = 0; i < pcm1.Length / 2; i++)
        {
            short s = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm1.AsSpan(i * 2, 2)));
            if (s > peak1) peak1 = s;
        }
        Assert.True(peak1 > 30000, $"Expected normalized peak close to 32767, but got {peak1}.");
    }

    #endregion

    #region Workflow 3: VIDEO_TS DVD-Video Layout, 32 KB ECC Alignment & Surface Scan

    [Fact]
    public async Task AcceptanceWorkflow3_DvdVideoLayout_Verify32KbEccAlignment_BurnToVirtualDvdR_FullSurfaceScan()
    {
        // 1. Arrange synthetic DVD-Video file hierarchy with VIDEO_TS and AUDIO_TS
        var compilation = new DataCompilation("DVD_VIDEO_ACCEP", DiscMediaType.DvdMinusR);
        var videoTs = compilation.Root.AddDirectory("VIDEO_TS");
        compilation.Root.AddDirectory("AUDIO_TS");

        byte[] vmgBytes = DvdVideoValidator.CreateSyntheticVmgIfo();
        byte[] vtsBytes = DvdVideoValidator.CreateSyntheticVtsIfo();

        videoTs.AddFile("VIDEO_TS.IFO", (byte[])vmgBytes.Clone());
        videoTs.AddFile("VIDEO_TS.VOB", new byte[2048]); // 1 sector
        videoTs.AddFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone());

        videoTs.AddFile("VTS_01_0.IFO", (byte[])vtsBytes.Clone());
        videoTs.AddFile("VTS_01_0.VOB", new byte[2048]); // Menu VOB
        videoTs.AddFile("VTS_01_1.VOB", new byte[4096]); // Title VOB
        videoTs.AddFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone());

        // 2. Validate DVD-Video specification compliance
        var validator = new DvdVideoValidator();
        var validationResult = validator.ValidateCompilation(compilation);

        Assert.True(validationResult.IsValid, string.Join("; ", validationResult.Errors));
        Assert.True(validationResult.HasAudioTs);
        Assert.True(validationResult.HasVmgMenuVob);
        Assert.Single(validationResult.TitleSetNumbers);
        Assert.Equal(1, validationResult.TitleSetNumbers[0]);

        // 3. Plan DVD-Video layout and verify 32 KB (16 sectors) ECC alignment between .IFO and .BUP
        var videoTsDir = compilation.Root.FindDirectoryByVirtualPath("/VIDEO_TS")!;
        var dvdFiles = videoTsDir.Children
            .OfType<CompilationFile>()
            .Select(f => (IDvdVideoFileEntry)new CompilationDvdVideoFileEntry(f))
            .ToList();

        var planner = new DvdVideoLayoutPlanner();
        var layout = planner.Plan(dvdFiles, startLba: 0);

        Assert.True(layout.VerifyIfoBupSeparation(out var separationFailure), separationFailure);

        var vmgIfo = layout.GetPlacement("VIDEO_TS.IFO")!;
        var vmgBup = layout.GetPlacement("VIDEO_TS.BUP")!;
        int vmgSeparation = vmgBup.StartLba.Value - vmgIfo.EndLba.Value;
        Assert.True(vmgSeparation >= 16, $"VMG separation ({vmgSeparation}) is less than 16 sectors (32 KB ECC).");

        var vtsIfo = layout.GetPlacement("VTS_01_0.IFO")!;
        var vtsBup = layout.GetPlacement("VTS_01_0.BUP")!;
        int vtsSeparation = vtsBup.StartLba.Value - vtsIfo.EndLba.Value;
        Assert.True(vtsSeparation >= 16, $"VTS separation ({vtsSeparation}) is less than 16 sectors (32 KB ECC).");

        // 4. Build ISO image and burn to virtual DVD-R
        var isoOptions = new IsoBuildOptions("DVD_VIDEO_ACCEP", IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: true);
        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(compilation.Root, isoOptions);

        using var recorder = new VirtualDiscRecorder("VDRV-P24-DVD");
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new DataBurnService(engine);

        isoStream.Position = 0;
        var burnResult = await burnService.BurnStreamAsync(
            recorder,
            isoStream,
            new DataBurnOptions { VolumeLabel = "DVD_VIDEO_ACCEP", CloseDisc = true });

        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");
        Assert.False(medium.IsBlank);
        Assert.True(medium.UsedSectors > 0);

        // 5. Run full surface scan on the burned virtual DVD-R
        var scanService = new SurfaceScanService(engine);
        var scanOptions = new SurfaceScanOptions { BlockSizeSectors = 64 };
        var scanReport = await scanService.ScanAsync(recorder, scanOptions);

        Assert.True(scanReport.TotalSectorsScanned > 0);
        Assert.Equal(0, scanReport.DamagedBlocks);
        Assert.Equal(0, scanReport.SlowBlocks);
        Assert.Equal(scanReport.Blocks.Count, scanReport.GoodBlocks);
    }

    #endregion

    #region Workflow 4: Erase Virtual CD-RW Disc & Blank State Restoration

    [Fact]
    public async Task AcceptanceWorkflow4_EraseVirtualCdRwDisc_VerifyBlankStateRestoration()
    {
        // 1. Arrange: Create written CD-RW medium with 128 written sectors
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        byte[] sampleSector = new byte[OpticalConstants.DataSectorSize];
        Array.Fill(sampleSector, (byte)0xEE);

        for (int i = 0; i < 128; i++)
        {
            medium.WriteSector(i, sampleSector);
        }
        medium.Sessions.Add(new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = 128 });
        medium.UsedSectors = 128;
        medium.IsBlank = false;

        using var recorder = new VirtualDiscRecorder("VDRV-P24-ERASE");
        recorder.InsertMedia(medium);

        var initialStatus = recorder.QueryMediaStatus();
        Assert.False(initialStatus.IsBlank);
        Assert.Equal(128, initialStatus.UsedSectors);

        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        // 2. Act: Quick erase
        var quickResult = await eraseService.QuickEraseAsync(recorder);

        // 3. Assert: Blank state restored
        Assert.True(quickResult.Success, $"Quick erase failed: {quickResult.ErrorMessage}");
        Assert.Equal(BurnStatus.Succeeded, quickResult.Status);
        Assert.True(medium.IsBlank);
        Assert.Equal(0, medium.UsedSectors);
        Assert.Empty(medium.Sessions);

        var postQuickStatus = recorder.QueryMediaStatus();
        Assert.True(postQuickStatus.IsBlank);
        Assert.Equal(0, postQuickStatus.UsedSectors);

        // 4. Act 2: Write sectors again and perform Full Erase
        for (int i = 0; i < 64; i++)
        {
            medium.WriteSector(i, sampleSector);
        }
        medium.UsedSectors = 64;
        medium.IsBlank = false;
        Assert.False(recorder.QueryMediaStatus().IsBlank);

        var fullResult = await eraseService.FullEraseAsync(recorder);

        // 5. Assert: Full erase restored blank state
        Assert.True(fullResult.Success, $"Full erase failed: {fullResult.ErrorMessage}");
        Assert.Equal(BurnStatus.Succeeded, fullResult.Status);
        Assert.True(medium.IsBlank);
        Assert.Equal(0, medium.UsedSectors);

        var postFullStatus = recorder.QueryMediaStatus();
        Assert.True(postFullStatus.IsBlank);
        Assert.Equal(0, postFullStatus.UsedSectors);
    }

    #endregion

    #region Workflow 5: WPF App DI Container & All XAML Views Instantiation on STA Thread

    [Fact]
    public void AcceptanceWorkflow5_WpfAppDependencyInjection_And_AllViewsInstantiateOnStaThread()
    {
        Exception? staException = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var app = new CdBurner.App.App();
                    app.InitializeComponent();
                }
                else if (!System.Windows.Application.Current.Resources.Contains("AccentButtonStyle"))
                {
                    var appResources = (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
                        new Uri("/IgnisDisc;component/App.xaml", UriKind.Relative));
                    System.Windows.Application.Current.Resources.MergedDictionaries.Add(appResources);
                }

                using var provider = CdBurner.App.App.CreateServiceProvider();

                // Resolve MainViewModel and MainWindow through the real App DI container
                var mainVm = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CdBurner.App.ViewModels.MainViewModel>(provider);
                Assert.NotNull(mainVm);

                var mainWindow = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CdBurner.App.MainWindow>(provider);
                Assert.NotNull(mainWindow);
                Assert.Same(mainVm, mainWindow.DataContext);
                // Title must track the version in Directory.Build.props instead of a hard-coded string
                string informational = typeof(CdBurner.App.MainWindow).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .First().InformationalVersion.Split('+')[0];
                Assert.Equal($"IgnisDisc Optical Studio v{informational}", mainWindow.Title);

                // Instantiate every single WPF UserControl view and bind its ViewModel
                var dataView = new CdBurner.App.Views.DataBurnView { DataContext = mainVm.DataBurn };
                var audioView = new CdBurner.App.Views.AudioBurnView { DataContext = mainVm.AudioBurn };
                var dvdView = new CdBurner.App.Views.DvdVideoView { DataContext = mainVm.DvdVideo };
                var imageView = new CdBurner.App.Views.ImageStudioView { DataContext = mainVm.ImageStudio };
                var utilView = new CdBurner.App.Views.DriveUtilitiesView { DataContext = mainVm.DriveUtilities };
                var vdrvView = new CdBurner.App.Views.VirtualDriveView { DataContext = mainVm.VirtualDrive };
                var settingsView = new CdBurner.App.Views.SettingsView { DataContext = mainVm.Settings };
                var progressView = new CdBurner.App.Views.BurnProgressView { DataContext = new CdBurner.App.ViewModels.BurnProgressViewModel() };

                Assert.NotNull(dataView);
                Assert.NotNull(audioView);
                Assert.NotNull(dvdView);
                Assert.NotNull(imageView);
                Assert.NotNull(utilView);
                Assert.NotNull(vdrvView);
                Assert.NotNull(settingsView);
                Assert.NotNull(progressView);

                // Navigate through all 7 tabs
                foreach (var tab in Enum.GetValues<CdBurner.App.ViewModels.NavigationTab>())
                {
                    mainVm.NavigateCommand.Execute(tab);
                    Assert.Equal(tab, mainVm.SelectedNavigationTab);
                }

                mainWindow.Close();
                mainVm.Dispose();
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (staException != null)
        {
            throw new InvalidOperationException($"STA WPF View instantiation failed: {staException}", staException);
        }
    }

    [Fact]
    public void AcceptanceWorkflow6_DriveSelectionPriority_AndTwoWayTabSynchronization()
    {
        using var virtualRec = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        using var physicalRec = new FakePhysicalDiscRecorder("PHYS-H", "HL-DT-ST DVDRAM GP60NB50", @"H:\");

        // Put virtual recorder first in enumeration to ensure physical priority overrides list order
        var deviceService = new FakeDeviceService(new IDiscRecorderBackend[] { virtualRec, physicalRec });
        var engine = new VirtualBurnEngine();

        using var mainVm = new CdBurner.App.ViewModels.MainViewModel(deviceService, engine);

        // 1. Default selection MUST be the physical drive, not VDRV-01
        Assert.Same(physicalRec, mainVm.SelectedRecorder);
        Assert.Same(physicalRec, mainVm.AudioBurn.SelectedRecorder);
        Assert.Same(physicalRec, mainVm.DataBurn.SelectedRecorder);
        Assert.Same(physicalRec, mainVm.DvdVideo.SelectedRecorder);
        Assert.Same(physicalRec, mainVm.ImageStudio.SelectedRecorder);
        Assert.Same(physicalRec, mainVm.DriveUtilities.SelectedRecorder);
        Assert.Same(physicalRec, mainVm.Settings.SelectedRecorder);

        // 2. Changing drive in Settings synchronizes to AudioBurn and all other tabs
        mainVm.Settings.SelectedRecorder = virtualRec;
        Assert.Same(virtualRec, mainVm.SelectedRecorder);
        Assert.Same(virtualRec, mainVm.AudioBurn.SelectedRecorder);
        Assert.Same(virtualRec, mainVm.DataBurn.SelectedRecorder);

        // 3. Changing drive in AudioBurn synchronizes back to MainViewModel and Settings
        mainVm.AudioBurn.SelectedRecorder = physicalRec;
        Assert.Same(physicalRec, mainVm.SelectedRecorder);
        Assert.Same(physicalRec, mainVm.Settings.SelectedRecorder);
        Assert.Same(physicalRec, mainVm.DriveUtilities.SelectedRecorder);

        // 4. Disabling virtual drive in Settings removes VDRV-01 from AvailableRecorders
        mainVm.Settings.EnableVirtualDrive = false;
        Assert.DoesNotContain(virtualRec, mainVm.AvailableRecorders);
        Assert.Single(mainVm.AvailableRecorders);
        Assert.Same(physicalRec, mainVm.SelectedRecorder);
    }

    [Fact]
    public void AcceptanceWorkflow7_AudioBurnViewModel_SkipsDrmM4pAndPreventsBogusDurations()
    {
        var deviceManager = new CdBurner.Hardware.Devices.DiscDeviceManager(enablePhysicalEnumeration: false);
        var burnEngine = new VirtualBurnEngine();
        var audioVm = new CdBurner.App.ViewModels.AudioBurnViewModel(deviceManager, burnEngine);

        string tempM4p = Path.Combine(_testTempDir, "apple_music_track.m4p");
        // Create an 8 MB dummy file (which previously was falsely computed as 44 seconds duration)
        File.WriteAllBytes(tempM4p, new byte[8 * 1024 * 1024]);

        // When adding the m4p file:
        audioVm.AddAudioFiles(new[] { tempM4p });

        // 1. The m4p track MUST NOT be added to the compilation
        Assert.Empty(audioVm.Tracks);

        // 2. Compilation duration must remain zero, NOT 44 seconds
        Assert.Equal(0, audioVm.TotalDuration.ToFrameCount());
        Assert.Equal("00:00:00", audioVm.TotalDurationText);

        // 3. StatusMessage must report that DRM-protected Apple Music file was skipped
        Assert.Contains("Skipped", audioVm.StatusMessage);
        Assert.Contains("DRM-protected", audioVm.StatusMessage);
    }

    #region Workflow 8: Accessibility & Screen-Reader Names (AutomationProperties) on All Views

    [Fact]
    public void AcceptanceWorkflow8_WpfAccessibility_ScreenReaderNamesAndKeyboardNavigationOnAllViews()
    {
        Exception? staException = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var app = new CdBurner.App.App();
                    app.InitializeComponent();
                }
                else if (!System.Windows.Application.Current.Resources.Contains("AccentButtonStyle"))
                {
                    var appResources = (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
                        new Uri("/IgnisDisc;component/App.xaml", UriKind.Relative));
                    System.Windows.Application.Current.Resources.MergedDictionaries.Add(appResources);
                }

                using var provider = CdBurner.App.App.CreateServiceProvider();
                var mainVm = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CdBurner.App.ViewModels.MainViewModel>(provider);
                Assert.NotNull(mainVm);

                var mainWindow = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CdBurner.App.MainWindow>(provider);
                Assert.NotNull(mainWindow);

                var dataView = new CdBurner.App.Views.DataBurnView { DataContext = mainVm.DataBurn };
                var audioView = new CdBurner.App.Views.AudioBurnView { DataContext = mainVm.AudioBurn };
                var dvdView = new CdBurner.App.Views.DvdVideoView { DataContext = mainVm.DvdVideo };
                var imageView = new CdBurner.App.Views.ImageStudioView { DataContext = mainVm.ImageStudio };
                var archiveView = new CdBurner.App.Views.ArchiveView { DataContext = mainVm.Archive };
                var utilView = new CdBurner.App.Views.DriveUtilitiesView { DataContext = mainVm.DriveUtilities };
                var vdrvView = new CdBurner.App.Views.VirtualDriveView { DataContext = mainVm.VirtualDrive };
                var settingsView = new CdBurner.App.Views.SettingsView { DataContext = mainVm.Settings };
                var progressView = new CdBurner.App.Views.BurnProgressView { DataContext = new CdBurner.App.ViewModels.BurnProgressViewModel() };
                var deviceService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IDiscDeviceService>(provider);
                var burnDialog = new CdBurner.App.Views.BurnTargetDialog(
                    deviceService,
                    mainVm.AvailableRecorders,
                    mainVm.SelectedRecorder,
                    "Data Disc");

                var targets = new (string Name, DependencyObject Root)[]
                {
                    ("MainWindow", mainWindow),
                    ("DataBurnView", dataView),
                    ("AudioBurnView", audioView),
                    ("DvdVideoView", dvdView),
                    ("ImageStudioView", imageView),
                    ("ArchiveView", archiveView),
                    ("DriveUtilitiesView", utilView),
                    ("VirtualDriveView", vdrvView),
                    ("SettingsView", settingsView),
                    ("BurnProgressView", progressView),
                    ("BurnTargetDialog", burnDialog)
                };

                int totalInspected = 0;

                foreach (var (viewName, root) in targets)
                {
                    var interactiveControls = EnumerateAllDescendants(root)
                        .Where(d => d is Button or TextBox or ComboBox or CheckBox or Slider or RadioButton or ListBox or DataGrid)
                        .ToList();

                    Assert.True(interactiveControls.Count > 0, $"{viewName} had no interactive controls discovered in its tree traversal.");

                    foreach (var element in interactiveControls)
                    {
                        totalInspected++;
                        string autoName = System.Windows.Automation.AutomationProperties.GetName(element);
                        string? contentStr = (element is ContentControl cc && cc.Content is string s) ? s : null;

                        bool hasAccessibleName = !string.IsNullOrWhiteSpace(autoName) || !string.IsNullOrWhiteSpace(contentStr);

                        Assert.True(hasAccessibleName,
                            $"{viewName}: Interactive control of type '{element.GetType().Name}' (x:Name='{(element as FrameworkElement)?.Name}') has neither an explicit AutomationProperties.Name nor a string Content.");
                    }
                }

                // Verify that a substantial suite of interactive controls were checked
                Assert.True(totalInspected >= 50, $"Expected >= 50 interactive controls inspected across all views, but found {totalInspected}.");

                // Keyboard Navigation Verification on BurnTargetDialog:
                Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(burnDialog));
                var dialogButtons = EnumerateAllDescendants(burnDialog).OfType<Button>().ToList();
                Assert.Contains(dialogButtons, b => b.IsCancel);
                Assert.Contains(dialogButtons, b => b.IsDefault);

                // Keyboard Navigation on MainWindow Navigation Rail:
                var railPanels = EnumerateAllDescendants(mainWindow)
                    .OfType<StackPanel>()
                    .Where(p => KeyboardNavigation.GetTabNavigation(p) == KeyboardNavigationMode.Local)
                    .ToList();
                Assert.NotEmpty(railPanels);

                mainWindow.Close();
                burnDialog.Close();
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (staException != null)
        {
            throw new AggregateException("STA Accessibility acceptance test failed.", staException);
        }
    }

    private static IEnumerable<DependencyObject> EnumerateAllDescendants(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        queue.Enqueue(root);
        visited.Add(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;

            // 1. Logical children
            foreach (var child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject d && visited.Add(d))
                {
                    queue.Enqueue(d);
                }
            }

            // 2. Visual children (if visual tree exists and element is a Visual)
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                int visualCount = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < visualCount; i++)
                {
                    var vChild = VisualTreeHelper.GetChild(current, i);
                    if (visited.Add(vChild))
                    {
                        queue.Enqueue(vChild);
                    }
                }
            }

            // 3. Fallbacks for unrendered content
            if (current is ContentControl cc)
            {
                if (cc.Content is DependencyObject cd && visited.Add(cd))
                {
                    queue.Enqueue(cd);
                }
                if (cc is HeaderedContentControl hcc && hcc.Header is DependencyObject hd && visited.Add(hd))
                {
                    queue.Enqueue(hd);
                }
            }
            else if (current is Decorator dec && dec.Child != null && visited.Add(dec.Child))
            {
                queue.Enqueue(dec.Child);
            }
            else if (current is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                {
                    if (visited.Add(child))
                    {
                        queue.Enqueue(child);
                    }
                }
            }
            else if (current is ItemsControl ic)
            {
                foreach (var item in ic.Items)
                {
                    if (item is DependencyObject id && visited.Add(id))
                    {
                        queue.Enqueue(id);
                    }
                }
            }
        }
    }

    #endregion


    private sealed class FakeDeviceService : IDiscDeviceService
    {
        private readonly IReadOnlyList<IDiscRecorderBackend> _recorders;
        public FakeDeviceService(IReadOnlyList<IDiscRecorderBackend> recorders) => _recorders = recorders;
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }
        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => includeVirtual ? _recorders : _recorders.Where(r => !r.IsVirtual).ToList();
        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => _recorders.FirstOrDefault(r => r.UniqueId == uniqueId);
    }

    private sealed class FakePhysicalDiscRecorder : IDiscRecorderBackend
    {
        public FakePhysicalDiscRecorder(string id, string product, string volumePath)
        {
            UniqueId = id;
            ProductId = product;
            VolumePath = volumePath;
        }
        public string UniqueId { get; }
        public string VendorId => "HL-DT-ST";
        public string ProductId { get; }
        public string ProductRevision => "1.00";
        public string VolumePath { get; }
        public bool IsVirtual => false;
        public DriveCapabilitiesDto GetCapabilities() => new() { CanWriteCdR = true, CanWriteDvdR = true };
        public MediaStatusDto QueryMediaStatus() => new()
        {
            MediaPresent = true,
            IsBlank = true,
            MediaType = DiscMediaType.CdR,
            TotalSectors = 359847,
            FreeSectors = 359847,
            TotalCapacityBytes = 359847L * 2048,
            FreeCapacityBytes = 359847L * 2048
        };
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    #endregion
}
