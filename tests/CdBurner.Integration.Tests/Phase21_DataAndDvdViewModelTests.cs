using System.IO;
using System.Text;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Unit and integration tests for Phase 21: WPF Data Disc, Bootable Disc &amp; DVD-Video Views.
/// Validates DataBurnViewModel, DvdVideoViewModel, folder navigation, El Torito BIOS/UEFI boot mastering,
/// VIDEO_TS 32 KB IFO/BUP ECC gap alignment inspection, and DVD-9 Layer Break calculation.
/// </summary>
public sealed class Phase21_DataAndDvdViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public Phase21_DataAndDvdViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Phase21_Tests_" + Guid.NewGuid().ToString("N"));
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
    public void DataBurnViewModel_AddingFilesAndFolders_UpdatesCurrentItemsAndRecalculatesCapacity()
    {
        // Arrange
        var vm = new DataBurnViewModel(_deviceManager, _burnEngine);

        Assert.NotNull(vm.CurrentItems);
        Assert.Empty(vm.CurrentItems);
        Assert.NotNull(vm.Capacity);
        Assert.Equal(0, vm.Capacity.TotalFileDataBytes);
        Assert.Equal(0, vm.TotalBytes);
        long initialOverheadBytes = vm.Capacity.TotalBytes;

        // Act 1: Add a physical file
        string sampleFile1 = Path.Combine(_tempDir, "Document.pdf");
        File.WriteAllBytes(sampleFile1, new byte[65536]); // 64 KB

        vm.AddFilesCommand.Execute(new[] { sampleFile1 });

        // Assert 1: CurrentItems contains file and Capacity increases
        Assert.Single(vm.CurrentItems);
        var fileItem = vm.CurrentItems[0];
        Assert.Equal("Document.pdf", fileItem.Name);
        Assert.False(fileItem.IsDirectory);
        Assert.Equal(65536, fileItem.SizeBytes);
        Assert.Equal("/Document.pdf", fileItem.VirtualPath);
        Assert.True(vm.Capacity.TotalBytes >= initialOverheadBytes + 65536);
        Assert.True(vm.Capacity.TotalSectors > 0);

        // Act 2: Add a folder with nested files
        string nestedFolder = Path.Combine(_tempDir, "Photos");
        Directory.CreateDirectory(nestedFolder);
        File.WriteAllBytes(Path.Combine(nestedFolder, "Photo1.jpg"), new byte[100000]);
        File.WriteAllBytes(Path.Combine(nestedFolder, "Photo2.jpg"), new byte[200000]);

        vm.AddFolderCommand.Execute(nestedFolder);

        // Assert 2: Photos directory added to root CurrentItems
        Assert.Equal(2, vm.CurrentItems.Count);
        Assert.Contains(vm.CurrentItems, i => i.Name == "Photos" && i.IsDirectory);
        Assert.True(vm.Capacity.TotalBytes >= initialOverheadBytes + 365536);
        Assert.Equal(3, vm.TotalFilesCount);

        // Act 3: Create a virtual directory
        vm.CreateVirtualFolderCommand.Execute("Archives");
        Assert.Equal(3, vm.CurrentItems.Count);
        Assert.Contains(vm.CurrentItems, i => i.Name == "Archives" && i.IsDirectory);

        // Act 4: Remove the virtual directory
        var archiveItem = vm.CurrentItems.First(i => i.Name == "Archives");
        vm.SelectedItem = archiveItem;
        Assert.True(vm.RemoveSelectedItemCommand.CanExecute(null));
        vm.RemoveSelectedItemCommand.Execute(null);

        // Assert 4: Item removed
        Assert.Equal(2, vm.CurrentItems.Count);
        Assert.DoesNotContain(vm.CurrentItems, i => i.Name == "Archives");
    }

    [Fact]
    public void DataBurnViewModel_FolderNavigation_NavigatesIntoAndUpHierarchy()
    {
        // Arrange
        var vm = new DataBurnViewModel(_deviceManager, _burnEngine);
        vm.CreateVirtualFolderCommand.Execute("Projects");

        var projectItem = vm.CurrentItems.First(i => i.Name == "Projects");

        // Act 1: Navigate into Projects
        vm.NavigateToFolder(projectItem.VirtualPath);

        // Assert 1: Inside /Projects
        Assert.Equal("/Projects", vm.CurrentFolderPath);
        Assert.True(vm.CanNavigateUp);
        Assert.Empty(vm.CurrentItems);

        // Act 2: Add file inside /Projects
        vm.AddFile("spec.txt", "Architecture specification"u8.ToArray());
        Assert.Single(vm.CurrentItems);
        Assert.Equal("spec.txt", vm.CurrentItems[0].Name);

        // Act 3: Navigate up
        Assert.True(vm.NavigateUpFolderCommand.CanExecute(null));
        vm.NavigateUpFolderCommand.Execute(null);

        // Assert 3: Back in root
        Assert.Equal("/", vm.CurrentFolderPath);
        Assert.False(vm.CanNavigateUp);
        Assert.Single(vm.CurrentItems);
        Assert.Equal("Projects", vm.CurrentItems[0].Name);
    }

    [Fact]
    public async Task DataBurnViewModel_BootableDisc_ExportsBootableIsoAndBurnsToVdrv01()
    {
        // Arrange: Prepare DataBurnViewModel
        var vm = new DataBurnViewModel(_deviceManager, _burnEngine)
        {
            VolumeLabel = "BOOT_STUDIO",
            SelectedFileSystemPreset = FileSystemPreset.IsoJolietUdf
        };

        // Add payload data
        string payloadFile = Path.Combine(_tempDir, "KERNEL.BIN");
        File.WriteAllBytes(payloadFile, new byte[131072]); // 128 KB
        vm.AddFile(payloadFile);

        // Create synthetic BIOS and UEFI boot images
        string biosBootFile = Path.Combine(_tempDir, "etfsboot.com");
        File.WriteAllBytes(biosBootFile, new byte[2048]); // 4 sectors

        string uefiBootFile = Path.Combine(_tempDir, "efisys.bin");
        File.WriteAllBytes(uefiBootFile, new byte[32768]); // 64 sectors

        // Enable bootable mode and point to boot streams
        vm.IsBootableEnabled = true;
        vm.BiosBootImagePath = biosBootFile;
        vm.UefiBootImagePath = uefiBootFile;

        // Act 1: Export bootable ISO
        string outputIso = Path.Combine(_tempDir, "BootableDisc.iso");
        string exportedPath = await vm.ExportIsoAsync(outputIso);

        // Assert 1: ISO was generated and contains boot structures
        Assert.True(File.Exists(exportedPath));
        Assert.True(new FileInfo(exportedPath).Length > 0);

        using (var fs = new FileStream(exportedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var bootInfo = ElToritoCatalogInspector.ParseBootInfo(fs);
            Assert.NotNull(bootInfo);
            Assert.Equal("EL TORITO SPECIFICATION", bootInfo.BootSystemIdentifier);
            Assert.True(bootInfo.ValidationEntry.IsChecksumValid);
            Assert.Equal(BootPlatformId.X86Bios, bootInfo.DefaultEntry.Platform);
            Assert.True(bootInfo.DefaultEntry.Bootable);
            Assert.Single(bootInfo.SectionEntries);
            Assert.Equal(BootPlatformId.Efi, bootInfo.SectionEntries[0].Platform);
            Assert.True(bootInfo.SectionEntries[0].Bootable);
        }

        // Act 2: Burn bootable compilation to VDRV-01
        var burnResult = await vm.StartDataBurnAsync("VDRV-01");

        // Assert 2: Burn succeeded
        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");
        Assert.True(burnResult.SectorsWritten > 0);
        Assert.Equal(BurnStatus.Succeeded, burnResult.Status);
    }

    [Fact]
    public void DvdVideoViewModel_ValidSyntheticVideoTs_PopulatesExtentLayoutRowsAndEnforcesEccGap()
    {
        // Arrange: Create synthetic VIDEO_TS structure
        string dvdDir = Path.Combine(_tempDir, "ValidDvdSource");
        string videoTsDir = Path.Combine(dvdDir, "VIDEO_TS");
        Directory.CreateDirectory(videoTsDir);

        byte[] vmgBytes = DvdVideoValidator.CreateSyntheticVmgIfo();
        byte[] vtsBytes = DvdVideoValidator.CreateSyntheticVtsIfo();

        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.IFO"), vmgBytes);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.VOB"), new byte[2048]); // 1 sector
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.BUP"), (byte[])vmgBytes.Clone());

        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.IFO"), vtsBytes);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.VOB"), new byte[2048]); // 1 sector
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_1.VOB"), new byte[4096]); // 2 sectors
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.BUP"), (byte[])vtsBytes.Clone());

        var vm = new DvdVideoViewModel(_deviceManager, _burnEngine);

        // Act: Point VideoTsFolderPath to valid synthetic folder
        vm.VideoTsFolderPath = videoTsDir;

        // Assert: Structure is valid and ExtentLayoutRows is populated
        Assert.True(vm.IsStructureValid, $"Structure invalid: {string.Join("; ", vm.ValidationMessages)}");
        Assert.NotEmpty(vm.ExtentLayoutRows);
        Assert.Equal(1, vm.TitleSetCount);

        // Verify VIDEO_TS.IFO and VIDEO_TS.BUP 16-sector ECC gap enforcement
        var vmgIfoRow = vm.ExtentLayoutRows.FirstOrDefault(r => r.FileName == "VIDEO_TS.IFO");
        var vmgBupRow = vm.ExtentLayoutRows.FirstOrDefault(r => r.FileName == "VIDEO_TS.BUP");

        Assert.NotNull(vmgIfoRow);
        Assert.NotNull(vmgBupRow);

        int vmgGap = vmgBupRow.StartLba - vmgIfoRow.EndLba;
        Assert.True(vmgGap >= OpticalConstants.DvdEccBlockSectors,
            $"Expected VIDEO_TS.BUP to be at least {OpticalConstants.DvdEccBlockSectors} sectors after VIDEO_TS.IFO end, but was {vmgGap}.");

        // Verify virtual ECC padding block was inserted
        Assert.Contains(vm.ExtentLayoutRows, r => r.IsPadding && r.SectorCount >= OpticalConstants.DvdEccBlockSectors);

        // Verify VTS_01_0.IFO and VTS_01_0.BUP 16-sector ECC gap enforcement
        var vtsIfoRow = vm.ExtentLayoutRows.FirstOrDefault(r => r.FileName == "VTS_01_0.IFO");
        var vtsBupRow = vm.ExtentLayoutRows.FirstOrDefault(r => r.FileName == "VTS_01_0.BUP");

        Assert.NotNull(vtsIfoRow);
        Assert.NotNull(vtsBupRow);

        int vtsGap = vtsBupRow.StartLba - vtsIfoRow.EndLba;
        Assert.True(vtsGap >= OpticalConstants.DvdEccBlockSectors,
            $"Expected VTS_01_0.BUP to be at least {OpticalConstants.DvdEccBlockSectors} sectors after VTS_01_0.IFO end, but was {vtsGap}.");
    }

    [Fact]
    public void DvdVideoViewModel_InvalidVideoTs_FlagsStructureAsInvalid()
    {
        // Arrange: Create empty folder
        string emptyDir = Path.Combine(_tempDir, "EmptyDvd");
        Directory.CreateDirectory(emptyDir);

        var vm = new DvdVideoViewModel(_deviceManager, _burnEngine);

        // Act
        vm.VideoTsFolderPath = emptyDir;

        // Assert
        Assert.False(vm.IsStructureValid);
        Assert.NotEmpty(vm.ValidationMessages);
        Assert.Empty(vm.ExtentLayoutRows);
    }

    [Fact]
    public async Task DvdVideoViewModel_BuildDvdIsoAndBurnToVdrv01_Succeeds()
    {
        // Arrange: Create compliant synthetic VIDEO_TS structure
        string dvdDir = Path.Combine(_tempDir, "DvdBurnSource");
        string videoTsDir = Path.Combine(dvdDir, "VIDEO_TS");
        Directory.CreateDirectory(videoTsDir);

        byte[] vmgBytes = DvdVideoValidator.CreateSyntheticVmgIfo();
        byte[] vtsBytes = DvdVideoValidator.CreateSyntheticVtsIfo();

        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.IFO"), vmgBytes);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.VOB"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.BUP"), (byte[])vmgBytes.Clone());

        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.IFO"), vtsBytes);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.VOB"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_1.VOB"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.BUP"), (byte[])vtsBytes.Clone());

        var vm = new DvdVideoViewModel(_deviceManager, _burnEngine)
        {
            VideoTsFolderPath = dvdDir,
            VolumeLabel = "DVD_TEST_DISC"
        };

        Assert.True(vm.IsStructureValid);

        // Act 1: Build DVD-Video ISO image
        string outputIso = Path.Combine(_tempDir, "DvdVideoDisc.iso");
        string builtPath = await vm.BuildDvdIsoAsync(outputIso);

        // Assert 1: ISO was generated
        Assert.True(File.Exists(builtPath));
        Assert.True(new FileInfo(builtPath).Length > 0);

        // Act 2: Burn DVD-Video compilation to VDRV-01
        var burnResult = await vm.StartDvdBurnAsync("VDRV-01");

        // Assert 2: Burn succeeded
        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");
        Assert.True(burnResult.SectorsWritten > 0);
    }

    [Fact]
    public async Task DvdVideoViewModel_OneClickMkvAndMp4WithJunkFiles_AutoConvertsCreatesMenuBuildsIsoAndBurnsToVdrv01()
    {
        // 1. Arrange: Create temp folder containing movies and junk files
        string movieFolder = Path.Combine(_tempDir, "HolidayVideos");
        Directory.CreateDirectory(movieFolder);

        File.WriteAllBytes(Path.Combine(movieFolder, "MyHolidayMovie.mkv"), new byte[65536]);
        File.WriteAllBytes(Path.Combine(movieFolder, "BonusClip.mp4"), new byte[65536]);
        File.WriteAllText(Path.Combine(movieFolder, "Thumbs.db"), "thumbnails cache");
        File.WriteAllText(Path.Combine(movieFolder, "MyHolidayMovie.srt"), "1\n00:00:01,000 --> 00:00:04,000\nSubtitles");
        File.WriteAllText(Path.Combine(movieFolder, "info.nfo"), "Media information");

        // 2. Act 1: Initialize ViewModel with UseFfmpegWhenAvailable = false and set folder path
        var vm = new DvdVideoViewModel(_deviceManager, _burnEngine) { UseFfmpegWhenAvailable = false };
        vm.VideoTsFolderPath = movieFolder;

        // 3. Assert 1: Properties updated for auto-authoring
        Assert.True(vm.IsStructureValid, $"Structure should be valid: {string.Join("; ", vm.ValidationMessages)}");
        Assert.True(vm.RequiresAutoAuthoring);
        Assert.NotEmpty(vm.ExtentLayoutRows);
        Assert.False(string.IsNullOrWhiteSpace(vm.VolumeLabel));
        Assert.NotEqual("DVD_VIDEO", vm.VolumeLabel); // Automatically derived from folder/movie

        // 4. Act 2: Build DVD-Video ISO image
        string targetIso = Path.Combine(_tempDir, "AutoAuthoredDvd.iso");
        string isoPath = await vm.BuildDvdIsoAsync(targetIso);

        // 5. Assert 2: Verify ISO contents via IsoImageReader
        Assert.True(File.Exists(isoPath));
        using (var reader = IsoImageReader.FromFile(isoPath))
        {
            Assert.NotNull(reader.FindFile("/VIDEO_TS/VIDEO_TS.IFO"));
            Assert.NotNull(reader.FindFile("/VIDEO_TS/VIDEO_TS.VOB")); // auto-created play menu
            Assert.NotNull(reader.FindFile("/VIDEO_TS/VIDEO_TS.BUP"));
            Assert.NotNull(reader.FindFile("/VIDEO_TS/VTS_01_0.IFO"));
            Assert.NotNull(reader.FindFile("/VIDEO_TS/VTS_01_1.VOB"));
            Assert.NotNull(reader.FindFile("/VIDEO_TS/VTS_01_0.BUP"));
            Assert.NotNull(reader.FindFile("/VIDEO_TS/VTS_02_1.VOB"));
            Assert.NotNull(reader.FindDirectory("/AUDIO_TS"));

            byte[] ifoBytes = reader.ReadFileBytes("/VIDEO_TS/VIDEO_TS.IFO");
            Assert.True(ifoBytes.Length >= 12);
            string magic = Encoding.ASCII.GetString(ifoBytes, 0, 12);
            Assert.StartsWith("DVDVIDEO-VMG", magic);
        }

        // 6. Act 3 & Assert 3: Burn to VDRV-01
        var burnResult = await vm.StartDvdBurnAsync("VDRV-01");
        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");
    }

    [Fact]
    public void MainViewModel_DvdVideoWorkspace_UpdatesBottomBarCapacityToDvdAndReflectsMovieSectors()
    {
        using var mainVm = new MainViewModel(_deviceManager, _burnEngine);

        // Navigate to DVD-Video workspace
        mainVm.NavigateCommand.Execute(NavigationTab.DvdVideo);
        Assert.Equal(NavigationTab.DvdVideo, mainVm.SelectedNavigationTab);

        // Even with no disc inserted yet, DVD-Video tab should measure against DVD-5 capacity (2,298,496 sectors), not 700 MB CD-R
        Assert.Equal(OpticalConstants.DvdMinusR5Sectors, mainVm.CurrentCapacity.MediaMaxSectors);

        // Add a movie file to DvdVideoViewModel
        string sampleMp4 = Path.Combine(_tempDir, "FeatureFilm.mp4");
        File.WriteAllBytes(sampleMp4, new byte[256 * 1024]);
        mainVm.DvdVideo.AddMovieFiles(new[] { sampleMp4 });

        Assert.True(mainVm.DvdVideo.TotalSectors > 0);
        Assert.Equal(mainVm.DvdVideo.TotalSectors, mainVm.CurrentCapacity.TotalSectors);

        // Also verify BuildVtsIfo handles > 511 NAV packs without overflowing Sector 5
        var largeNavList = Enumerable.Range(0, 2500).Select(i => i * 15).ToList();
        byte[] vtsIfo = DvdIfoGenerator.BuildVtsIfo(
            titleSetNumber: 1,
            standard: DvdVideoStandard.Ntsc,
            aspect: DvdAspectRatio.Widescreen16x9,
            titleVobTotalSectors: 40000,
            durationSeconds: 6704,
            menuVobSectors: 16,
            navPackSectors: largeNavList);
        Assert.Equal(6 * OpticalConstants.DataSectorSize, vtsIfo.Length);
    }
}
