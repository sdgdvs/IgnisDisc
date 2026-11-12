using System.Text.Json;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class ProjectPersistenceTests : IDisposable
{
    private readonly string _testTempDir;

    public ProjectPersistenceTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "CdBurnerTests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore cleanup errors in temp folder
        }
    }

    #region DataDisc & Bootable Project Round-Trip Tests

    [Fact]
    public async Task DataDisc_RoundTrip_WithNestedFoldersAndBootOptions_SerializesAndLoadsCorrectly()
    {
        string projectPath = Path.Combine(_testTempDir, "bootable_sample.cdbproj");

        // 1. Construct a rich Bootable Data project document
        var original = new CdBurnerProjectDocument
        {
            SchemaVersion = "1.0",
            Kind = ProjectKind.BootableDisc,
            Name = "Ubuntu Custom Installer",
            Description = "Customized UEFI & BIOS multi-boot installation media",
            VolumeLabel = "UBUNTU_2604",
            TargetMediaType = DiscMediaType.DvdMinusR,
            DefaultDuplicatePolicy = DuplicateFilePolicy.RenameWithNumberSuffix,
            RootNode = new SerializedCompilationNode
            {
                IsDirectory = true,
                Name = "",
                Attributes = FileAttributes.Directory,
                Children =
                {
                    new SerializedCompilationNode
                    {
                        IsDirectory = true,
                        Name = "docs",
                        Attributes = FileAttributes.Directory,
                        Children =
                        {
                            new SerializedCompilationNode
                            {
                                IsDirectory = false,
                                Name = "README.txt",
                                SourceFilePath = @"C:\Source\README.txt",
                                SizeBytes = 1024,
                                Attributes = FileAttributes.ReadOnly,
                                LastWriteTimeUtc = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)
                            },
                            new SerializedCompilationNode
                            {
                                IsDirectory = true,
                                Name = "licenses",
                                Attributes = FileAttributes.Directory,
                                Children =
                                {
                                    new SerializedCompilationNode
                                    {
                                        IsDirectory = false,
                                        Name = "MIT.txt",
                                        SourceFilePath = @"C:\Source\MIT.txt",
                                        SizeBytes = 512,
                                        Attributes = FileAttributes.Hidden | FileAttributes.ReadOnly,
                                        LastWriteTimeUtc = new DateTime(2026, 2, 20, 12, 30, 0, DateTimeKind.Utc)
                                    }
                                }
                            }
                        }
                    },
                    new SerializedCompilationNode
                    {
                        IsDirectory = true,
                        Name = "boot",
                        Attributes = FileAttributes.Directory,
                        Children =
                        {
                            new SerializedCompilationNode
                            {
                                IsDirectory = false,
                                Name = "isolinux.bin",
                                SourceFilePath = @"C:\Source\isolinux.bin",
                                SizeBytes = 2048,
                                Attributes = FileAttributes.Normal
                            }
                        }
                    },
                    new SerializedCompilationNode
                    {
                        IsDirectory = false,
                        Name = "autorun.inf",
                        SourceFilePath = @"C:\Source\autorun.inf",
                        SizeBytes = 128,
                        Attributes = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly
                    }
                }
            },
            BootOptions = new SerializedBootOptions
            {
                DeveloperId = "UBUNTU_BOOT",
                DefaultEntry = new SerializedBootCatalogEntry
                {
                    Platform = BootPlatformId.X86Bios,
                    Emulation = BootEmulationType.NoEmulation,
                    SectorCount = 4,
                    LoadSegment = 0x07C0,
                    BootImageFilePath = @"C:\Source\isolinux.bin",
                    Name = "isolinux.bin",
                    SystemType = 0x00
                },
                AdditionalSectionEntries =
                {
                    new SerializedBootCatalogEntry
                    {
                        Platform = BootPlatformId.Efi,
                        Emulation = BootEmulationType.NoEmulation,
                        SectorCount = 8,
                        LoadSegment = 0x0000,
                        BootImageFilePath = @"C:\Source\efiboot.img",
                        Name = "efiboot.img",
                        SystemType = 0xEF
                    }
                }
            }
        };

        // 2. Save to file
        await ProjectSerializer.SaveToFileAsync(original, projectPath);
        Assert.True(File.Exists(projectPath));

        // 3. Load from file
        var loaded = await ProjectSerializer.LoadFromFileAsync(projectPath);

        // 4. Assert all top-level properties
        Assert.NotNull(loaded);
        Assert.Equal("1.0", loaded.SchemaVersion);
        Assert.Equal(ProjectKind.BootableDisc, loaded.Kind);
        Assert.Equal("Ubuntu Custom Installer", loaded.Name);
        Assert.Equal("Customized UEFI & BIOS multi-boot installation media", loaded.Description);
        Assert.Equal("UBUNTU_2604", loaded.VolumeLabel);
        Assert.Equal(DiscMediaType.DvdMinusR, loaded.TargetMediaType);
        Assert.Equal(DuplicateFilePolicy.RenameWithNumberSuffix, loaded.DefaultDuplicatePolicy);

        // 5. Assert filesystem tree
        Assert.NotNull(loaded.RootNode);
        Assert.True(loaded.RootNode.IsDirectory);
        Assert.Equal(3, loaded.RootNode.Children.Count);

        var docsDir = loaded.RootNode.Children.FirstOrDefault(c => c.Name == "docs");
        Assert.NotNull(docsDir);
        Assert.True(docsDir.IsDirectory);
        Assert.Equal(2, docsDir.Children.Count);

        var readme = docsDir.Children.FirstOrDefault(c => c.Name == "README.txt");
        Assert.NotNull(readme);
        Assert.False(readme.IsDirectory);
        Assert.Equal(@"C:\Source\README.txt", readme.SourceFilePath);
        Assert.Equal(1024, readme.SizeBytes);
        Assert.Equal(FileAttributes.ReadOnly, readme.Attributes);

        var licensesDir = docsDir.Children.FirstOrDefault(c => c.Name == "licenses");
        Assert.NotNull(licensesDir);
        Assert.Single(licensesDir.Children);

        var mitFile = licensesDir.Children[0];
        Assert.Equal("MIT.txt", mitFile.Name);
        Assert.Equal(FileAttributes.Hidden | FileAttributes.ReadOnly, mitFile.Attributes);

        var autorun = loaded.RootNode.Children.FirstOrDefault(c => c.Name == "autorun.inf");
        Assert.NotNull(autorun);
        Assert.Equal(FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly, autorun.Attributes);

        // 6. Assert boot options
        Assert.NotNull(loaded.BootOptions);
        Assert.Equal("UBUNTU_BOOT", loaded.BootOptions.DeveloperId);
        Assert.NotNull(loaded.BootOptions.DefaultEntry);
        Assert.Equal(BootPlatformId.X86Bios, loaded.BootOptions.DefaultEntry.Platform);
        Assert.Equal(BootEmulationType.NoEmulation, loaded.BootOptions.DefaultEntry.Emulation);
        Assert.Equal(4, loaded.BootOptions.DefaultEntry.SectorCount);
        Assert.Equal(0x07C0, loaded.BootOptions.DefaultEntry.LoadSegment);
        Assert.Equal(@"C:\Source\isolinux.bin", loaded.BootOptions.DefaultEntry.BootImageFilePath);

        Assert.Single(loaded.BootOptions.AdditionalSectionEntries);
        var uefi = loaded.BootOptions.AdditionalSectionEntries[0];
        Assert.Equal(BootPlatformId.Efi, uefi.Platform);
        Assert.Equal(8, uefi.SectorCount);
        Assert.Equal(0xEF, uefi.SystemType);
        Assert.Equal(@"C:\Source\efiboot.img", uefi.BootImageFilePath);

        // 7. Convert loaded document to live CompilationDirectory and verify tree access
        var liveRoot = ProjectSerializer.ToCompilationDirectory(loaded);
        Assert.NotNull(liveRoot);

        var foundReadme = liveRoot.FindFileByVirtualPath("/docs/README.txt");
        Assert.NotNull(foundReadme);
        Assert.Equal(1024, foundReadme.SizeBytes);
        Assert.Equal(FileAttributes.ReadOnly, foundReadme.Attributes);

        var foundMit = liveRoot.FindFileByVirtualPath("/docs/licenses/MIT.txt");
        Assert.NotNull(foundMit);
        Assert.Equal(FileAttributes.Hidden | FileAttributes.ReadOnly, foundMit.Attributes);

        var foundAutorun = liveRoot.FindFileByVirtualPath("/autorun.inf");
        Assert.NotNull(foundAutorun);
        Assert.Equal(FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly, foundAutorun.Attributes);

        // 8. Convert boot options to live configuration
        var liveBootConfig = ProjectSerializer.ToBootConfiguration(loaded.BootOptions);
        Assert.NotNull(liveBootConfig);
        Assert.Equal("UBUNTU_BOOT", liveBootConfig.DeveloperId);
        Assert.Equal(BootPlatformId.X86Bios, liveBootConfig.DefaultEntry.Platform);
        Assert.NotNull(liveBootConfig.AdditionalSectionEntries);
        Assert.Single(liveBootConfig.AdditionalSectionEntries);
        Assert.Equal(BootPlatformId.Efi, liveBootConfig.AdditionalSectionEntries[0].Platform);
    }

    #endregion

    #region Audio CD Project Round-Trip Tests

    [Fact]
    public async Task AudioCd_RoundTrip_With5TracksAndCdText_SerializesAndLoadsCorrectly()
    {
        string projectPath = Path.Combine(_testTempDir, "audiocd_album.cdbproj");

        // 1. Build live AudioCdCompilation with 5 tracks and full CD-Text metadata
        var originalComp = new AudioCdCompilation
        {
            AlbumTitle = "Voyage of Sound",
            AlbumPerformer = "Acoustic Horizon",
            Songwriter = "L. Beethoven",
            Composer = "W. Mozart",
            Arranger = "J. Williams",
            Message = "Audiophile 2026 Edition",
            DiscId = "VOS-2026-HQ",
            UpcEan = "0123456789012",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true,
            NormalizeAllTracks = true,
            DefaultPreGapFrames = 150
        };

        originalComp.AddTrack(new AudioCdTrackItem(
            TrackNumber: 1,
            SourceFilePath: @"C:\Audio\01_Prelude.wav",
            Title: "Prelude in C",
            Performer: "Acoustic Horizon",
            Isrc: "US-AH1-26-00001",
            PreGapFrames: 150,
            NormalizeAudio: true)
        {
            DurationFrames = 9000,
            Songwriter = "Beethoven"
        });

        originalComp.AddTrack(new AudioCdTrackItem(
            TrackNumber: 2,
            SourceFilePath: @"C:\Audio\02_Nocturne.flac",
            Title: "Nocturne Op. 9",
            Performer: "Acoustic Horizon",
            Isrc: "US-AH1-26-00002",
            PreGapFrames: 0,
            NormalizeAudio: true)
        {
            DurationFrames = 13500,
            Composer = "Chopin"
        });

        originalComp.AddTrack(new AudioCdTrackItem(
            TrackNumber: 3,
            SourceFilePath: @"C:\Audio\03_Allegro.wav",
            Title: "Allegro Con Brio",
            Performer: "Acoustic Horizon",
            Isrc: "US-AH1-26-00003",
            PreGapFrames: 75,
            NormalizeAudio: true)
        {
            DurationFrames = 15000,
            Arranger = "Williams"
        });

        originalComp.AddTrack(new AudioCdTrackItem(
            TrackNumber: 4,
            SourceFilePath: @"C:\Audio\04_Intermezzo.wav",
            Title: "Intermezzo",
            Performer: "Acoustic Horizon",
            Isrc: "US-AH1-26-00004",
            PreGapFrames: 0,
            NormalizeAudio: true)
        {
            DurationFrames = 6000,
            Message = "Solo Piano"
        });

        originalComp.AddTrack(new AudioCdTrackItem(
            TrackNumber: 5,
            SourceFilePath: @"C:\Audio\05_Finale.flac",
            Title: "Grand Finale",
            Performer: "Acoustic Horizon & Symphony",
            Isrc: "US-AH1-26-00005",
            PreGapFrames: 150,
            NormalizeAudio: true)
        {
            DurationFrames = 22500
        });

        Assert.Equal(5, originalComp.TrackCount);
        Assert.Equal(66000, originalComp.TotalFrames);

        // 2. Convert to project document
        var doc = ProjectSerializer.FromAudioCdCompilation(originalComp, projectName: "Voyage of Sound Project");
        Assert.Equal(ProjectKind.AudioCd, doc.Kind);
        Assert.Equal("Voyage of Sound Project", doc.Name);
        Assert.Equal(5, doc.AudioTracks.Count);
        Assert.NotNull(doc.CdText);

        // 3. Save to file
        await ProjectSerializer.SaveToFileAsync(doc, projectPath);
        Assert.True(File.Exists(projectPath));

        // 4. Load from file
        var loadedDoc = await ProjectSerializer.LoadFromFileAsync(projectPath);
        Assert.NotNull(loadedDoc);
        Assert.Equal(ProjectKind.AudioCd, loadedDoc.Kind);
        Assert.Equal(AudioRecordingMode.DiscAtOnceGapless, loadedDoc.RecordingMode);
        Assert.True(loadedDoc.IncludeCdText);
        Assert.True(loadedDoc.NormalizeAllTracks);
        Assert.Equal(5, loadedDoc.AudioTracks.Count);

        // 5. Convert back to live AudioCdCompilation
        var loadedComp = ProjectSerializer.ToAudioCdCompilation(loadedDoc);
        Assert.Equal(5, loadedComp.TrackCount);
        Assert.Equal(66000, loadedComp.TotalFrames);
        Assert.Equal("Voyage of Sound", loadedComp.AlbumTitle);
        Assert.Equal("Acoustic Horizon", loadedComp.AlbumPerformer);
        Assert.Equal("L. Beethoven", loadedComp.Songwriter);
        Assert.Equal("W. Mozart", loadedComp.Composer);
        Assert.Equal("J. Williams", loadedComp.Arranger);
        Assert.Equal("Audiophile 2026 Edition", loadedComp.Message);
        Assert.Equal("VOS-2026-HQ", loadedComp.DiscId);
        Assert.Equal("0123456789012", loadedComp.UpcEan);

        // Check track details
        var t1 = loadedComp.Tracks[0];
        Assert.Equal(1, t1.TrackNumber);
        Assert.Equal(@"C:\Audio\01_Prelude.wav", t1.SourceFilePath);
        Assert.Equal("Prelude in C", t1.Title);
        Assert.Equal("US-AH1-26-00001", t1.Isrc);
        Assert.Equal(150, t1.PreGapFrames);
        Assert.True(t1.NormalizeAudio);
        Assert.Equal(9000, t1.DurationFrames);
        Assert.Equal("Beethoven", t1.Songwriter);

        var t2 = loadedComp.Tracks[1];
        Assert.Equal(2, t2.TrackNumber);
        Assert.Equal("Nocturne Op. 9", t2.Title);
        Assert.Equal(0, t2.PreGapFrames);
        Assert.Equal(13500, t2.DurationFrames);
        Assert.Equal("Chopin", t2.Composer);

        var t4 = loadedComp.Tracks[3];
        Assert.Equal("Solo Piano", t4.Message);

        var t5 = loadedComp.Tracks[4];
        Assert.Equal("Grand Finale", t5.Title);
        Assert.Equal("Acoustic Horizon & Symphony", t5.Performer);
        Assert.Equal("US-AH1-26-00005", t5.Isrc);

        // Verify CD-Text pack creation succeeds from loaded compilation
        var cdTextMeta = loadedComp.CreateCdTextMetadata();
        Assert.Equal("Voyage of Sound", cdTextMeta.AlbumTitle);
        Assert.NotNull(cdTextMeta.Tracks);
        Assert.Equal(5, cdTextMeta.Tracks.Count);

        byte[] rawPacks = loadedComp.BuildCdTextPacks();
        Assert.True(rawPacks.Length > 0);
        Assert.Equal(0, rawPacks.Length % 18);
    }

    #endregion

    #region Source File Validation Tests

    [Fact]
    public void ValidateProjectSources_AccuratelyIdentifiesMissingFilesOnDisk()
    {
        // 1. Create real files on disk in the temp directory
        string realDataFile = Path.Combine(_testTempDir, "present_data.bin");
        File.WriteAllBytes(realDataFile, new byte[256]);

        string realAudioFile = Path.Combine(_testTempDir, "present_audio.wav");
        File.WriteAllBytes(realAudioFile, new byte[1024]);

        string realBootFile = Path.Combine(_testTempDir, "present_boot.img");
        File.WriteAllBytes(realBootFile, new byte[512]);

        // 2. Fabricate paths for files that do NOT exist
        string missingDataFile = Path.Combine(_testTempDir, "missing_data_123.iso");
        string missingAudioFile = Path.Combine(_testTempDir, "missing_track.flac");
        string missingBootFile = Path.Combine(_testTempDir, "missing_uefi.img");

        // 3. Construct a project referencing both real and missing files
        var project = new CdBurnerProjectDocument
        {
            Kind = ProjectKind.BootableDisc,
            RootNode = new SerializedCompilationNode
            {
                IsDirectory = true,
                Name = "",
                Children =
                {
                    new SerializedCompilationNode
                    {
                        IsDirectory = false,
                        Name = "present_data.bin",
                        SourceFilePath = realDataFile
                    },
                    new SerializedCompilationNode
                    {
                        IsDirectory = false,
                        Name = "missing_data.iso",
                        SourceFilePath = missingDataFile
                    },
                    new SerializedCompilationNode
                    {
                        IsDirectory = false,
                        Name = "in_memory.txt",
                        SourceFilePath = "" // In-memory or empty path should not be reported
                    }
                }
            },
            BootOptions = new SerializedBootOptions
            {
                DefaultEntry = new SerializedBootCatalogEntry
                {
                    BootImageFilePath = realBootFile
                },
                AdditionalSectionEntries =
                {
                    new SerializedBootCatalogEntry
                    {
                        BootImageFilePath = missingBootFile
                    }
                }
            },
            AudioTracks =
            {
                new SerializedAudioTrack
                {
                    TrackNumber = 1,
                    SourceFilePath = realAudioFile
                },
                new SerializedAudioTrack
                {
                    TrackNumber = 2,
                    SourceFilePath = missingAudioFile
                }
            }
        };

        // 4. Validate
        var missing = ProjectSerializer.ValidateProjectSources(project);

        // 5. Assert: Missing paths contain the 3 missing files and NONE of the real files
        Assert.Equal(3, missing.Count);
        Assert.Contains(missingDataFile, missing, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(missingBootFile, missing, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(missingAudioFile, missing, StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(realDataFile, missing, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(realAudioFile, missing, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(realBootFile, missing, StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region AppSettingsService Tests

    [Fact]
    public async Task AppSettingsService_SavesAndLoadsSettingsToTempJsonFile()
    {
        string settingsPath = Path.Combine(_testTempDir, "config", "settings.json");

        // 1. Initialize service with custom file path
        var service1 = new AppSettingsService(settingsPath);
        Assert.Equal(settingsPath, service1.SettingsFilePath);

        // Verify default settings
        var def = service1.CurrentSettings;
        Assert.Equal(WriteSpeedPolicy.Recommended, def.DefaultWriteSpeedMultiplier);
        Assert.True(def.SecureAudioRipByDefault);
        Assert.True(def.VerifyAfterBurnByDefault);
        Assert.False(def.EjectAfterBurnByDefault);
        Assert.True(def.EnableBurnFreeByDefault);
        Assert.Equal(ThemeMode.System, def.ThemeMode);
        Assert.Equal("ffmpeg.exe", def.FfmpegExecutablePath);
        Assert.False(def.EnableVirtualDrive);

        // 2. Modify settings
        var custom = new AppSettings
        {
            DefaultWriteSpeedMultiplier = 24,
            VerifyAfterBurnByDefault = false,
            StrictVerify = true,
            EjectAfterBurnByDefault = true,
            EnableBurnFreeByDefault = false,
            TempStagingDirectory = @"D:\FastScratch\CdBurner",
            ThemeMode = ThemeMode.Dark,
            FfmpegExecutablePath = @"C:\Tools\ffmpeg\bin\ffmpeg.exe",
            EnableVirtualDrive = false,
            LogRingBufferCapacity = 500
        };

        service1.CurrentSettings = custom;

        // 3. Save asynchronously
        await service1.SaveAsync();
        Assert.True(File.Exists(settingsPath));

        // 4. Create new service instance pointing to the same file and load
        var service2 = new AppSettingsService(settingsPath);
        await service2.LoadAsync();

        var loaded = service2.CurrentSettings;
        Assert.Equal(24, loaded.DefaultWriteSpeedMultiplier);
        Assert.False(loaded.VerifyAfterBurnByDefault);
        Assert.True(loaded.StrictVerify);
        Assert.True(loaded.Clone().StrictVerify);
        Assert.False(new AppSettings().StrictVerify);
        Assert.True(loaded.EjectAfterBurnByDefault);
        Assert.False(loaded.EnableBurnFreeByDefault);
        Assert.Equal(@"D:\FastScratch\CdBurner", loaded.TempStagingDirectory);
        Assert.Equal(ThemeMode.Dark, loaded.ThemeMode);
        Assert.Equal(@"C:\Tools\ffmpeg\bin\ffmpeg.exe", loaded.FfmpegExecutablePath);
        Assert.False(loaded.EnableVirtualDrive);
        Assert.Equal(500, loaded.LogRingBufferCapacity);

        // 5. Test ResetToDefaults
        service2.ResetToDefaults();
        var reset = service2.CurrentSettings;
        Assert.Equal(WriteSpeedPolicy.Recommended, reset.DefaultWriteSpeedMultiplier);
        Assert.True(reset.VerifyAfterBurnByDefault);
        Assert.Equal(ThemeMode.System, reset.ThemeMode);
    }

    #endregion

    #region StructuredBurnLogger Tests

    [Fact]
    public async Task StructuredBurnLogger_RecordsAndExportsLogEntriesSafelyUnderConcurrentWrites()
    {
        string jsonlPath = Path.Combine(_testTempDir, "burn_session.jsonl");
        string logExportPath = Path.Combine(_testTempDir, "exported_session.log");

        const int ringCapacity = 200;
        const int totalEntriesToWrite = 1000;

        await using (var logger = new StructuredBurnLogger(capacity: ringCapacity, jsonlFilePath: jsonlPath))
        {
            Assert.Equal(ringCapacity, logger.Capacity);

            // 1. Concurrently write 1,000 log entries across multiple threads
            Parallel.For(0, totalEntriesToWrite, i =>
            {
                if (i % 4 == 0)
                {
                    logger.LogInfo($"Burn progress block {i} written to disc", category: "BurnEngine", lba: 1000 + i);
                }
                else if (i % 4 == 1)
                {
                    logger.LogWarn($"Buffer underrun protection engaged at frame {i}", category: "BurnEngine", lba: 2000 + i);
                }
                else if (i % 4 == 2)
                {
                    logger.LogError($"Transient SCSI error retried for block {i}", ex: new InvalidOperationException($"SCSI Check Condition {i}"), category: "ScsiPassThrough", lba: 3000 + i);
                }
                else
                {
                    logger.LogDebug($"Debug packet {i} validated", category: "Diagnostics");
                }
            });

            // 2. Ring buffer must retain exactly the capacity (200) most recent entries
            Assert.Equal(ringCapacity, logger.Count);
            var entries = logger.GetAllEntries();
            Assert.Equal(ringCapacity, entries.Count);

            foreach (var entry in entries)
            {
                Assert.NotNull(entry);
                Assert.False(string.IsNullOrWhiteSpace(entry.Message));
                Assert.False(string.IsNullOrWhiteSpace(entry.LevelString));
            }

            // 3. Export to human-readable .log file
            await logger.ExportToLogFileAsync(logExportPath);
        }

        // 4. Verify exported .log file contents
        Assert.True(File.Exists(logExportPath));
        string[] logLines = await File.ReadAllLinesAsync(logExportPath);
        Assert.True(logLines.Length >= ringCapacity, $"Expected at least {ringCapacity} lines, found {logLines.Length}.");

        // Verify standard format: [yyyy-MM-dd HH:mm:ss.fff] [LEVEL] ...
        Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("[BurnEngine]"));
        Assert.Contains(logLines, l => l.Contains("[WARN]"));

        // 5. Verify streaming JSONL file contents
        Assert.True(File.Exists(jsonlPath));
        string[] jsonLines = await File.ReadAllLinesAsync(jsonlPath);
        Assert.Equal(totalEntriesToWrite, jsonLines.Length);

        // Spot-check that JSON lines are valid JSON
        using var jsonDoc = JsonDocument.Parse(jsonLines[0]);
        Assert.True(jsonDoc.RootElement.TryGetProperty("level", out _));
        Assert.True(jsonDoc.RootElement.TryGetProperty("message", out _));
        Assert.True(jsonDoc.RootElement.TryGetProperty("timestampUtc", out _));
    }

    #endregion

    #region DataCompilation Tree Round-Trip & Error Handling

    [Fact]
    public void FromDataCompilation_And_ToDataCompilation_RoundTrip_PreservesTreeHierarchy()
    {
        var originalComp = new DataCompilation(
            volumeLabel: "MY_BACKUP",
            targetMediaType: DiscMediaType.DvdMinusR,
            defaultDuplicatePolicy: DuplicateFilePolicy.RenameWithNumberSuffix);

        originalComp.AddFile("/Docs/Notes.txt", @"C:\Data\Notes.txt", 1024);
        originalComp.AddFile("/Docs/Archive/Data.zip", @"C:\Data\Data.zip", 204800);
        originalComp.AddFile("/Media/Song.mp3", @"C:\Data\Song.mp3", 4096000);

        // Convert to document
        var doc = ProjectSerializer.FromDataCompilation(originalComp, projectName: "My Backup");
        Assert.Equal(ProjectKind.DataDisc, doc.Kind);
        Assert.Equal("MY_BACKUP", doc.VolumeLabel);
        Assert.Equal(DiscMediaType.DvdMinusR, doc.TargetMediaType);

        // Convert to JSON and back
        string json = ProjectSerializer.ToJson(doc);
        var loadedDoc = ProjectSerializer.FromJson(json);

        // Reconstitute DataCompilation
        var reconstituted = ProjectSerializer.ToDataCompilation(loadedDoc);
        Assert.Equal("MY_BACKUP", reconstituted.VolumeLabel);
        Assert.Equal(DiscMediaType.DvdMinusR, reconstituted.TargetMediaType);
        Assert.Equal(DuplicateFilePolicy.RenameWithNumberSuffix, reconstituted.DefaultDuplicatePolicy);

        var file1 = reconstituted.FindFileByVirtualPath("/Docs/Notes.txt");
        Assert.NotNull(file1);
        Assert.Equal(1024, file1.SizeBytes);

        var file2 = reconstituted.FindFileByVirtualPath("/Docs/Archive/Data.zip");
        Assert.NotNull(file2);
        Assert.Equal(204800, file2.SizeBytes);

        var file3 = reconstituted.FindFileByVirtualPath("/Media/Song.mp3");
        Assert.NotNull(file3);
        Assert.Equal(4096000, file3.SizeBytes);
    }

    [Fact]
    public async Task ProjectSerializer_ThrowsAppropriateExceptions_OnInvalidArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => ProjectSerializer.SaveToFileAsync(null!, "test.cdbproj"));
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectSerializer.SaveToFileAsync(new CdBurnerProjectDocument(), ""));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ProjectSerializer.LoadFromFileAsync(Path.Combine(_testTempDir, "non_existent.cdbproj")));
        Assert.Throws<ArgumentNullException>(() => ProjectSerializer.ValidateProjectSources(null!));
    }

    #endregion
}
