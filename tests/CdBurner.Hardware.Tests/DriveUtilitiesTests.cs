using System.Collections.Concurrent;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Drive Utilities: Rewritable Erase, Bitsetting &amp; Surface Scan.
/// Verifies DiscEraseService (Quick and Full erasing of CD-RW / DVD+RW and rejection of non-rewritable media),
/// SurfaceScanService (LBA transfer rate benchmarking and defect mapping for Good, Slow, and Damaged blocks),
/// and BookTypeService (DVD+R / DVD+RW / DVD+R DL bitsetting to DVD-ROM and MMC 0xBF SEND DISC STRUCTURE CDB + header builders).
/// </summary>
public class DriveUtilitiesTests
{
    private sealed class ThreadSafeListProgress<T> : IProgress<T>
    {
        private readonly object _lock = new();
        public List<T> Items { get; } = new();

        public void Report(T value)
        {
            lock (_lock)
            {
                Items.Add(value);
            }
        }
    }

    #region 1. DiscEraseService Tests

    [Fact]
    public async Task DiscEraseService_QuickErase_WrittenCdRw_ResetsUsedSectorsAndMarksBlank()
    {
        // 1. Arrange written CD-RW medium
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        byte[] testData = new byte[OpticalConstants.DataSectorSize];
        Array.Fill(testData, (byte)0xAB);

        // Write 64 sectors
        for (int i = 0; i < 64; i++)
        {
            medium.WriteSector(i, testData);
        }
        medium.Sessions.Add(new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = 64 });
        medium.IsBlank = false;
        medium.UsedSectors = 64;

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        var progress = new ThreadSafeListProgress<BurnProgressSnapshot>();

        // 2. Act - Quick erase
        var result = await eraseService.QuickEraseAsync(recorder, progress);

        // 3. Assert
        Assert.True(result.Success, $"Quick erase failed: {result.ErrorMessage}");
        Assert.Equal(BurnStatus.Succeeded, result.Status);
        Assert.Equal(0, medium.UsedSectors);
        Assert.True(medium.IsBlank);
        Assert.Empty(medium.Sessions);

        var status = recorder.QueryMediaStatus();
        Assert.True(status.IsBlank);
        Assert.Equal(0, status.UsedSectors);
        Assert.NotEmpty(progress.Items);
    }

    [Fact]
    public async Task DiscEraseService_FullErase_WrittenCdRw_ResetsUsedSectorsAndMarksBlank()
    {
        // 1. Arrange written CD-RW medium
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        byte[] testData = new byte[OpticalConstants.DataSectorSize];
        Array.Fill(testData, (byte)0xCD);

        for (int i = 0; i < 32; i++)
        {
            medium.WriteSector(i, testData);
        }
        medium.IsBlank = false;
        medium.UsedSectors = 32;

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        // 2. Act - Full erase
        var result = await eraseService.FullEraseAsync(recorder);

        // 3. Assert
        Assert.True(result.Success, $"Full erase failed: {result.ErrorMessage}");
        Assert.Equal(0, medium.UsedSectors);
        Assert.True(medium.IsBlank);
        Assert.Equal(0, recorder.QueryMediaStatus().UsedSectors);
        Assert.True(recorder.QueryMediaStatus().IsBlank);
    }

    [Fact]
    public async Task DiscEraseService_QuickErase_WrittenDvdPlusRw_ResetsUsedSectorsAndMarksBlank()
    {
        // 1. Arrange written DVD+RW medium
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        byte[] testData = new byte[OpticalConstants.DataSectorSize];
        Array.Fill(testData, (byte)0xEE);

        for (int i = 0; i < 128; i++)
        {
            medium.WriteSector(i, testData);
        }
        medium.IsBlank = false;
        medium.UsedSectors = 128;

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        // 2. Act - Quick erase
        var result = await eraseService.EraseAsync(recorder, engine, fullErase: false);

        // 3. Assert
        Assert.True(result.Success, $"Quick erase on DVD+RW failed: {result.ErrorMessage}");
        Assert.Equal(0, medium.UsedSectors);
        Assert.True(medium.IsBlank);
        Assert.Empty(medium.Sessions);
        Assert.True(recorder.QueryMediaStatus().IsBlank);
    }

    [Fact]
    public async Task DiscEraseService_FullErase_WrittenDvdPlusRw_ResetsUsedSectorsAndMarksBlank()
    {
        // 1. Arrange written DVD+RW medium
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        byte[] testData = new byte[OpticalConstants.DataSectorSize];
        Array.Fill(testData, (byte)0xFF);

        for (int i = 0; i < 256; i++)
        {
            medium.WriteSector(i, testData);
        }
        medium.IsBlank = false;
        medium.UsedSectors = 256;

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        // 2. Act - Full erase
        var result = await eraseService.FullEraseAsync(recorder, engine);

        // 3. Assert
        Assert.True(result.Success, $"Full erase on DVD+RW failed: {result.ErrorMessage}");
        Assert.Equal(0, medium.UsedSectors);
        Assert.True(medium.IsBlank);
        Assert.True(recorder.QueryMediaStatus().IsBlank);
        Assert.Equal(0, recorder.QueryMediaStatus().UsedSectors);
    }

    [Fact]
    public async Task DiscEraseService_WrittenDvdMinusRw_And_DvdRam_BothEraseSuccessfully()
    {
        var eraseService = new DiscEraseService();

        // 1. Test DVD-RW
        using var dvdMinusRw = VirtualDiscMedium.CreateBlankDvdRw47();
        dvdMinusRw.WriteSector(0, new byte[2048]);
        dvdMinusRw.UsedSectors = 1;
        dvdMinusRw.IsBlank = false;

        using var recorderMinus = new VirtualDiscRecorder("VDRV-RW1");
        recorderMinus.InsertMedia(dvdMinusRw);
        var engineMinus = new VirtualBurnEngine(recorderMinus);

        var resMinus = await eraseService.EraseAsync(recorderMinus, engineMinus, fullErase: false);
        Assert.True(resMinus.Success);
        Assert.Equal(0, dvdMinusRw.UsedSectors);
        Assert.True(dvdMinusRw.IsBlank);

        // 2. Test DVD-RAM
        using var dvdRam = VirtualDiscMedium.CreateBlankDvdRam();
        dvdRam.WriteSector(0, new byte[2048]);
        dvdRam.UsedSectors = 1;
        dvdRam.IsBlank = false;

        using var recorderRam = new VirtualDiscRecorder("VDRV-RAM1");
        recorderRam.InsertMedia(dvdRam);
        var engineRam = new VirtualBurnEngine(recorderRam);

        var resRam = await eraseService.EraseAsync(recorderRam, engineRam, fullErase: true);
        Assert.True(resRam.Success);
        Assert.Equal(0, dvdRam.UsedSectors);
        Assert.True(dvdRam.IsBlank);
    }

    [Fact]
    public async Task DiscEraseService_NonRewritableMedia_RejectsCdR_And_LeavesMediaUnmodified()
    {
        // 1. Arrange CD-R (write-once)
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        medium.WriteSector(0, new byte[2048]);
        medium.UsedSectors = 100;
        medium.IsBlank = false;

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        // 2. Act - attempt erase
        var result = await eraseService.EraseAsync(recorder, fullErase: false);

        // 3. Assert
        Assert.False(result.Success);
        Assert.Equal(BurnStatus.Failed, result.Status);
        Assert.Contains("not rewritable", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(100, medium.UsedSectors);
        Assert.False(medium.IsBlank);
    }

    [Fact]
    public async Task DiscEraseService_NonRewritableMedia_RejectsDvdMinusR_And_LeavesMediaUnmodified()
    {
        // 1. Arrange DVD-R (write-once)
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        medium.WriteSector(0, new byte[2048]);
        medium.UsedSectors = 500;
        medium.IsBlank = false;

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        // 2. Act - attempt full erase
        var result = await eraseService.FullEraseAsync(recorder);

        // 3. Assert
        Assert.False(result.Success);
        Assert.Equal(BurnStatus.Failed, result.Status);
        Assert.Contains("not rewritable", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(500, medium.UsedSectors);
        Assert.False(medium.IsBlank);
    }

    [Fact]
    public async Task DiscEraseService_NoMedia_ReturnsFailure()
    {
        using var recorder = new VirtualDiscRecorder();
        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        var result = await eraseService.EraseAsync(recorder);

        Assert.False(result.Success);
        Assert.Contains("No optical media present", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscEraseService_Cancellation_AbortsGracefully()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);
        var eraseService = new DiscEraseService(engine);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            eraseService.EraseAsync(recorder, fullErase: true, cancellationToken: cts.Token));
    }

    #endregion

    #region 2. SurfaceScanService Tests

    [Fact]
    public async Task SurfaceScanService_1024Sectors_InjectedBadLbaAt500_ReportsExactGoodAndDamagedCounts()
    {
        // 1. Arrange a 1,024-sector virtual disc with an injected bad sector at LBA 500
        const int totalCapacity = 1024;
        const int badLba = 500;
        const int blockSize = 256;

        using var medium = new VirtualDiscMedium(DiscMediaType.CdRom, totalCapacity, isBlank: false)
        {
            UsedSectors = totalCapacity
        };
        medium.SimulatedBadLbas.Add(badLba);

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        var service = new SurfaceScanService(engine);
        var blockResults = new ThreadSafeListProgress<SurfaceScanBlockResult>();
        var progressSnapshots = new ThreadSafeListProgress<BurnProgressSnapshot>();

        var options = new SurfaceScanOptions
        {
            BlockSizeSectors = blockSize,
            StartLba = 0,
            SectorCount = totalCapacity,
            BlockProgress = blockResults
        };

        // 2. Act - Execute surface scan
        var report = await service.ScanAsync(recorder, engine, options, progressSnapshots);

        // 3. Assert exact counts:
        // Total 1024 sectors with block size 256 yields exactly 4 blocks:
        // Block 0: LBA 0..255 (256 sectors) -> Good
        // Block 1: LBA 256..511 (256 sectors) -> contains LBA 500 -> Damaged
        // Block 2: LBA 512..767 (256 sectors) -> Good
        // Block 3: LBA 768..1023 (256 sectors) -> Good
        Assert.Equal(totalCapacity, report.TotalSectorsScanned);
        Assert.Equal(3, report.GoodBlocks);
        Assert.Equal(1, report.DamagedBlocks);
        Assert.Equal(0, report.SlowBlocks);
        Assert.Equal(4, report.Blocks.Count);

        // Inspect Block 0 (Good)
        Assert.Equal(0, report.Blocks[0].StartLba);
        Assert.Equal(blockSize, report.Blocks[0].SectorCount);
        Assert.Equal(SectorHealthStatus.Good, report.Blocks[0].Status);
        Assert.True(report.Blocks[0].ReadSpeedKilobytesPerSec > 0.0);
        Assert.True(report.Blocks[0].ReadSpeedMultiplierX > 0.0);

        // Inspect Block 1 (Damaged - contains LBA 500)
        Assert.Equal(256, report.Blocks[1].StartLba);
        Assert.Equal(blockSize, report.Blocks[1].SectorCount);
        Assert.Equal(SectorHealthStatus.Damaged, report.Blocks[1].Status);
        Assert.Equal(0.0, report.Blocks[1].ReadSpeedKilobytesPerSec);
        Assert.Equal(0.0, report.Blocks[1].ReadSpeedMultiplierX);

        // Inspect Block 2 (Good)
        Assert.Equal(512, report.Blocks[2].StartLba);
        Assert.Equal(blockSize, report.Blocks[2].SectorCount);
        Assert.Equal(SectorHealthStatus.Good, report.Blocks[2].Status);

        // Inspect Block 3 (Good)
        Assert.Equal(768, report.Blocks[3].StartLba);
        Assert.Equal(blockSize, report.Blocks[3].SectorCount);
        Assert.Equal(SectorHealthStatus.Good, report.Blocks[3].Status);

        // Throughput statistics
        Assert.True(report.MinSpeedKbPerSec > 0.0, "Min speed should be positive for good blocks.");
        Assert.True(report.MaxSpeedKbPerSec >= report.MinSpeedKbPerSec, "Max speed must be >= min speed.");
        Assert.True(report.AverageSpeedKbPerSec >= report.MinSpeedKbPerSec, "Avg speed must be >= min speed.");

        // Progress notifications
        Assert.Equal(4, blockResults.Items.Count);
        Assert.NotEmpty(progressSnapshots.Items);
    }

    [Fact]
    public async Task SurfaceScanService_CustomBlockSize128_ReportsSevenGoodAndOneDamaged()
    {
        const int totalCapacity = 1024;
        const int badLba = 500;
        const int blockSize = 128;

        using var medium = new VirtualDiscMedium(DiscMediaType.CdRom, totalCapacity, isBlank: false)
        {
            UsedSectors = totalCapacity
        };
        medium.SimulatedBadLbas.Add(badLba);

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        var service = new SurfaceScanService();

        // 1024 sectors with block size 128 gives 8 blocks:
        // Block 0: 0..127, Block 1: 128..255, Block 2: 256..383, Block 3: 384..511 (contains 500!),
        // Block 4: 512..639, Block 5: 640..767, Block 6: 768..895, Block 7: 896..1023.
        var report = await service.ScanAsync(recorder, engine, blockSize);

        Assert.Equal(totalCapacity, report.TotalSectorsScanned);
        Assert.Equal(8, report.Blocks.Count);
        Assert.Equal(7, report.GoodBlocks);
        Assert.Equal(1, report.DamagedBlocks);
        Assert.Equal(0, report.SlowBlocks);
        Assert.Equal(SectorHealthStatus.Damaged, report.Blocks[3].Status);
    }

    [Fact]
    public async Task SurfaceScanService_SlowThreshold_ClassifiesSlowerBlocksAsSlow()
    {
        const int totalCapacity = 512;
        using var medium = new VirtualDiscMedium(DiscMediaType.CdRom, totalCapacity, isBlank: false)
        {
            UsedSectors = totalCapacity
        };

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        var service = new SurfaceScanService(engine);

        // Set an exorbitantly high speed threshold so all blocks qualify as Slow
        var options = new SurfaceScanOptions
        {
            BlockSizeSectors = 256,
            SlowSpeedThresholdKbPerSec = 1_000_000_000.0 // 1 TB/sec threshold
        };

        var report = await service.ScanAsync(recorder, options);

        Assert.Equal(512, report.TotalSectorsScanned);
        Assert.Equal(2, report.Blocks.Count);
        Assert.Equal(0, report.GoodBlocks);
        Assert.Equal(2, report.SlowBlocks);
        Assert.Equal(0, report.DamagedBlocks);
        Assert.All(report.Blocks, b => Assert.Equal(SectorHealthStatus.Slow, b.Status));
    }

    [Fact]
    public async Task SurfaceScanService_PartialEndBlock_ScansCorrectSectorCounts()
    {
        const int totalCapacity = 500; // Not evenly divisible by 256
        using var medium = new VirtualDiscMedium(DiscMediaType.CdRom, totalCapacity, isBlank: false)
        {
            UsedSectors = totalCapacity
        };

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        var service = new SurfaceScanService();

        var report = await service.ScanAsync(recorder, engine, blockSizeSectors: 256);

        Assert.Equal(500, report.TotalSectorsScanned);
        Assert.Equal(2, report.Blocks.Count);
        Assert.Equal(256, report.Blocks[0].SectorCount);
        Assert.Equal(244, report.Blocks[1].SectorCount); // 500 - 256 = 244
        Assert.Equal(2, report.GoodBlocks);
    }

    [Fact]
    public async Task SurfaceScanService_NoMedia_ThrowsInvalidOperationException()
    {
        using var recorder = new VirtualDiscRecorder();
        var engine = new VirtualBurnEngine(recorder);
        var service = new SurfaceScanService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScanAsync(recorder, engine));
    }

    [Fact]
    public async Task SurfaceScanService_Cancellation_CancelsScanPromptly()
    {
        using var medium = new VirtualDiscMedium(DiscMediaType.CdRom, 2048, isBlank: false);
        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);
        var service = new SurfaceScanService();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ScanAsync(recorder, engine, options: null, progress: null, cancellationToken: cts.Token));
    }

    #endregion

    #region 3. BookTypeService Tests

    [Fact]
    public void BookTypeService_SetsBookTypeToDvdRom_OnVirtualDvdPlusRDualLayerDisc()
    {
        // 1. Arrange a virtual DVD+R DL 8.5 GB disc
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRDL85();

        // Verify initial native Book Type matches DVD+R DL
        Assert.Equal(DvdBookType.DvdPlusRDualLayer, medium.BookType);

        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);

        var service = new BookTypeService();

        // 2. Act - Set Book Type to DVD-ROM for legacy standalone DVD player playback
        var result = service.SetBookType(medium, DvdBookType.DvdRom);

        // 3. Assert
        Assert.True(result.Success, $"Setting Book Type failed: {result.ErrorMessage}");
        Assert.Equal(DvdBookType.DvdRom, medium.BookType);
        Assert.Equal(DvdBookType.DvdRom, service.GetBookType(medium));
        Assert.Equal(DvdBookType.DvdRom, service.GetBookType(recorder));

        // Media status reflects new Book Type
        var status = recorder.QueryMediaStatus();
        Assert.Equal(DvdBookType.DvdRom, status.BookType);
    }

    [Fact]
    public async Task BookTypeService_SetBookTypeAsync_ViaRecorder_SetsBookTypeSuccessfully()
    {
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRDL85();
        using var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);

        var service = new BookTypeService();

        var result = await service.SetBookTypeAsync(recorder, DvdBookType.DvdRom);

        Assert.True(result.Success);
        Assert.Equal(DvdBookType.DvdRom, medium.BookType);
    }

    [Fact]
    public void BookTypeService_SetsBookType_OnDvdPlusR_And_DvdPlusRw_Succeeds()
    {
        var service = new BookTypeService();

        // 1. DVD+R single layer
        using var dvdPlusR = new VirtualDiscMedium(DiscMediaType.DvdPlusR, OpticalConstants.DvdPlusR5Sectors);
        Assert.Equal(DvdBookType.DvdPlusR, dvdPlusR.BookType);
        var resPlusR = service.SetBookType(dvdPlusR, DvdBookType.DvdRom);
        Assert.True(resPlusR.Success);
        Assert.Equal(DvdBookType.DvdRom, dvdPlusR.BookType);

        // 2. DVD+RW single layer
        using var dvdPlusRw = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        Assert.Equal(DvdBookType.DvdPlusRw, dvdPlusRw.BookType);
        var resPlusRw = service.SetBookType(dvdPlusRw, DvdBookType.DvdRom);
        Assert.True(resPlusRw.Success);
        Assert.Equal(DvdBookType.DvdRom, dvdPlusRw.BookType);
    }

    [Fact]
    public void BookTypeService_RejectsNonDvdPlusMedia_ReturnsFailure()
    {
        var service = new BookTypeService();

        // 1. CD-R
        using var cdR = VirtualDiscMedium.CreateBlankCdR700();
        var resCdR = service.SetBookType(cdR, DvdBookType.DvdRom);
        Assert.False(resCdR.Success);
        Assert.Contains("not supported", resCdR.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // 2. DVD-R (recordable dash media cannot be bitset)
        using var dvdMinusR = VirtualDiscMedium.CreateBlankDvdR47();
        var resDvdMinusR = service.SetBookType(dvdMinusR, DvdBookType.DvdRom);
        Assert.False(resDvdMinusR.Success);
        Assert.Contains("not supported", resDvdMinusR.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BookTypeService_BuildsValid12Byte0xbfCdb()
    {
        const byte formatCode = 0x00;
        const ushort dataLength = 16; // 0x0010

        byte[] cdb = BookTypeService.BuildSendDiscStructureCdb(formatCode, dataLength);

        // MMC-5 OpCode 0xBF SEND DISC STRUCTURE is 12 bytes
        Assert.Equal(12, cdb.Length);

        // Byte 0: OpCode 0xBF
        Assert.Equal(0xBF, cdb[0]);
        Assert.Equal(BookTypeService.OpCodeSendDiscStructure, cdb[0]);

        // Bytes 1..6: Reserved (0x00)
        for (int i = 1; i <= 6; i++)
        {
            Assert.Equal(0x00, cdb[i]);
        }

        // Byte 7: Format Code 0x00 (Physical Format Information)
        Assert.Equal(formatCode, cdb[7]);

        // Bytes 8..9: Parameter List Length (big-endian 16 = 0x0010)
        Assert.Equal(0x00, cdb[8]);
        Assert.Equal(0x10, cdb[9]);

        // Bytes 10..11: Reserved & Control (0x00)
        Assert.Equal(0x00, cdb[10]);
        Assert.Equal(0x00, cdb[11]);
    }

    [Fact]
    public void BookTypeService_BuildsValid4ByteStructureHeader()
    {
        const ushort dataLength = 16; // 0x0010

        byte[] header = BookTypeService.BuildStructureHeader(dataLength);

        // MMC-5 Send Disc Structure Parameter List Header is 4 bytes
        Assert.Equal(4, header.Length);

        // Bytes 0..1: Structure Data Length (big-endian 16 = 0x0010)
        Assert.Equal(0x00, header[0]);
        Assert.Equal(0x10, header[1]);

        // Bytes 2..3: Reserved (0x00)
        Assert.Equal(0x00, header[2]);
        Assert.Equal(0x00, header[3]);
    }

    [Fact]
    public void BookTypeService_BuildBookTypeCommand_BuildsBothValidCdbAndHeader()
    {
        var (cdb, header) = BookTypeService.BuildBookTypeCommand(DvdBookType.DvdRom);

        // 12-byte CDB
        Assert.Equal(12, cdb.Length);
        Assert.Equal(0xBF, cdb[0]);
        Assert.Equal(0x00, cdb[7]);

        // 4-byte header
        Assert.Equal(4, header.Length);
        Assert.Equal(0x00, header[0]);
        Assert.Equal(0x10, header[1]);
        Assert.Equal(0x00, header[2]);
        Assert.Equal(0x00, header[3]);
    }

    [Fact]
    public void BookTypeService_BuildBookTypePayload_ContainsHeaderAndBookTypeNibble()
    {
        // 1. DVD-ROM (0x00)
        byte[] payloadDvdRom = BookTypeService.BuildBookTypePayload(DvdBookType.DvdRom, dataLength: 16);
        Assert.Equal(20, payloadDvdRom.Length); // 4 header + 16 data

        // Header check
        Assert.Equal(0x00, payloadDvdRom[0]);
        Assert.Equal(0x10, payloadDvdRom[1]);
        Assert.Equal(0x00, payloadDvdRom[2]);
        Assert.Equal(0x00, payloadDvdRom[3]);

        // Byte 4: bits 7..4 is Book Type
        byte bookTypeNibble = (byte)((payloadDvdRom[4] >> 4) & 0x0F);
        Assert.Equal((byte)DvdBookType.DvdRom, bookTypeNibble);

        // 2. DVD+R (0x0A)
        byte[] payloadPlusR = BookTypeService.BuildBookTypePayload(DvdBookType.DvdPlusR, dataLength: 16);
        byte plusRNibble = (byte)((payloadPlusR[4] >> 4) & 0x0F);
        Assert.Equal((byte)DvdBookType.DvdPlusR, plusRNibble);

        // 3. DVD+R DL (0x0E)
        byte[] payloadPlusRDL = BookTypeService.BuildBookTypePayload(DvdBookType.DvdPlusRDualLayer, dataLength: 16);
        byte plusRDLNibble = (byte)((payloadPlusRDL[4] >> 4) & 0x0F);
        Assert.Equal((byte)DvdBookType.DvdPlusRDualLayer, plusRDLNibble);
    }

    [Fact]
    public void BookTypeService_GetDefaultBookType_ReturnsExpectedDefaults()
    {
        Assert.Equal(DvdBookType.DvdRom, BookTypeService.GetDefaultBookType(DiscMediaType.DvdRom));
        Assert.Equal(DvdBookType.DvdRam, BookTypeService.GetDefaultBookType(DiscMediaType.DvdRam));
        Assert.Equal(DvdBookType.DvdMinusR, BookTypeService.GetDefaultBookType(DiscMediaType.DvdMinusR));
        Assert.Equal(DvdBookType.DvdMinusRw, BookTypeService.GetDefaultBookType(DiscMediaType.DvdMinusRw));
        Assert.Equal(DvdBookType.DvdPlusR, BookTypeService.GetDefaultBookType(DiscMediaType.DvdPlusR));
        Assert.Equal(DvdBookType.DvdPlusRw, BookTypeService.GetDefaultBookType(DiscMediaType.DvdPlusRw));
        Assert.Equal(DvdBookType.DvdPlusRDualLayer, BookTypeService.GetDefaultBookType(DiscMediaType.DvdPlusRDualLayer));
    }

    #endregion
}
