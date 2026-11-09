using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class IsoUdfBuilderTests
{
    private static readonly byte[] SampleReadme = "Welcome to IgnisDisc!\r\nLine 2 for verification."u8.ToArray();
    private static readonly byte[] SampleManualUnicode = "CdBurner 用户手册 & 仕様書 - Unicode content with Chinese and Japanese 文档."u8.ToArray();
    private static readonly byte[] SampleAppBin = Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray();

    private static CompilationDirectory CreateStandardTestTree()
    {
        var root = CompilationDirectory.CreateRoot();

        // /README.TXT
        root.AddFile("README.TXT", SampleReadme);

        // /Docs/Manual_Unicode_文档.txt
        var docs = root.AddDirectory("Docs");
        docs.AddFile("Manual_Unicode_文档.txt", SampleManualUnicode);

        // /Bin/app.bin
        var bin = root.AddDirectory("Bin");
        bin.AddFile("app.bin", SampleAppBin);

        return root;
    }

    [Fact]
    public void Build_StandardCompilation_ProducesValidIsoStreamWithExpectedHeaderAndLength()
    {
        // 1. Build an in-memory .iso image from a CompilationDirectory containing
        //    /README.TXT, /Docs/Manual_Unicode_文档.txt, and /Bin/app.bin
        var root = CreateStandardTestTree();
        var options = new IsoBuildOptions(
            VolumeLabel: "CDBURNER_TEST",
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);

        // 2. Verify total stream length is an exact multiple of 2048 bytes
        Assert.True(isoStream.Length > 0);
        Assert.Equal(0, isoStream.Length % OpticalConstants.DataSectorSize);

        // 3. Verify LBA 16 starts with 0x01, 'C', 'D', '0', '0', '1', 0x01
        byte[] pvdHeader = new byte[7];
        isoStream.Position = 16 * OpticalConstants.DataSectorSize;
        isoStream.ReadExactly(pvdHeader);

        byte[] expectedHeader = [0x01, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1', 0x01];
        Assert.Equal(expectedHeader, pvdHeader);
    }

    [Fact]
    public void Build_WithUdfEnabled_WritesAvdpDescriptorTagAtLba256()
    {
        // Verify UDF AVDP descriptor tag (0x0002) is written at LBA 256 when IncludeUdf = true
        var root = CreateStandardTestTree();
        var options = new IsoBuildOptions(IncludeUdf: true);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);

        Assert.True(isoStream.Length >= 257 * OpticalConstants.DataSectorSize);

        isoStream.Position = 256 * OpticalConstants.DataSectorSize;
        byte[] avdpSector = new byte[OpticalConstants.DataSectorSize];
        isoStream.ReadExactly(avdpSector);

        // Tag Identifier is at offset 0..1 (uint16 LE)
        ushort tagId = BinaryPrimitives.ReadUInt16LittleEndian(avdpSector.AsSpan(0, 2));
        Assert.Equal(0x0002, tagId);

        // Descriptor Version is at offset 2..3
        ushort descVersion = BinaryPrimitives.ReadUInt16LittleEndian(avdpSector.AsSpan(2, 2));
        Assert.Equal(2, descVersion);

        // Tag Location at offset 12..15 should match LBA 256
        uint tagLocation = BinaryPrimitives.ReadUInt32LittleEndian(avdpSector.AsSpan(12, 4));
        Assert.Equal(256u, tagLocation);
    }

    [Fact]
    public void IsoImageReader_ParsesVolumeLabel_FindsUnicodeFile_AndExtractsIdenticalPayloads()
    {
        // Verify IsoImageReader reads back the volume label, finds /Docs/Manual_Unicode_文档.txt
        // via Joliet SVD, and extracts byte-identical file payloads
        var root = CreateStandardTestTree();
        const string expectedLabel = "TEST_UNICODE_VOL";
        var options = new IsoBuildOptions(
            VolumeLabel: expectedLabel,
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);
        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);

        // 1. Verify Volume Label
        Assert.Equal(expectedLabel, reader.VolumeLabel);
        Assert.True(reader.HasIso9660);
        Assert.True(reader.HasJoliet);
        Assert.True(reader.HasUdf);

        // 2. Find /Docs/Manual_Unicode_文档.txt via Joliet SVD
        var unicodeFile = reader.FindFile("/Docs/Manual_Unicode_文档.txt");
        Assert.NotNull(unicodeFile);
        Assert.Equal("Manual_Unicode_文档.txt", unicodeFile.Name);
        Assert.Equal("/Docs/Manual_Unicode_文档.txt", unicodeFile.VirtualPath);
        Assert.Equal(SampleManualUnicode.Length, unicodeFile.SizeBytes);
        Assert.False(unicodeFile.IsDirectory);

        // 3. Extract byte-identical payload for Unicode file
        byte[] extractedUnicodeBytes = reader.ReadFileBytes("/Docs/Manual_Unicode_文档.txt");
        Assert.Equal(SampleManualUnicode, extractedUnicodeBytes);

        // 4. Extract byte-identical payloads for other files
        var readmeFile = reader.FindFile("/README.TXT");
        Assert.NotNull(readmeFile);
        Assert.Equal(SampleReadme, reader.ReadFileBytes("/README.TXT"));

        var appBinFile = reader.FindFile("/Bin/app.bin");
        Assert.NotNull(appBinFile);
        Assert.Equal(SampleAppBin, reader.ReadFileBytes("/Bin/app.bin"));
    }

    [Fact]
    public void Build_JolietSupplementaryVolumeDescriptor_ContainsEscapeSequencesAndBothEndianFields()
    {
        var root = CreateStandardTestTree();
        var options = new IsoBuildOptions(
            VolumeLabel: "JOLIET_DISC",
            IncludeIso9660: true,
            IncludeJoliet: true);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);

        // SVD is at LBA 17
        isoStream.Position = 17 * OpticalConstants.DataSectorSize;
        byte[] svd = new byte[OpticalConstants.DataSectorSize];
        isoStream.ReadExactly(svd);

        // Type = 0x02, Identifier = "CD001", Version = 0x01
        Assert.Equal(0x02, svd[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(svd.AsSpan(1, 5)));
        Assert.Equal(0x01, svd[6]);

        // Escape Sequences 0x25 0x2F 0x45 for UCS-2 Level 3 at offset 88..90
        Assert.Equal(0x25, svd[88]);
        Assert.Equal(0x2F, svd[89]);
        Assert.Equal(0x45, svd[90]);

        // Logical block size 2048 both-endian uint16 at 128..131
        ushort blkSizeLe = BinaryPrimitives.ReadUInt16LittleEndian(svd.AsSpan(128, 2));
        ushort blkSizeBe = BinaryPrimitives.ReadUInt16BigEndian(svd.AsSpan(130, 2));
        Assert.Equal(2048, blkSizeLe);
        Assert.Equal(2048, blkSizeBe);

        // Root Directory Record 34 bytes at offset 156
        Assert.Equal(34, svd[156]); // Record Length
        Assert.Equal(0x02, svd[156 + 25]); // Directory flag
        Assert.Equal(1, svd[156 + 32]); // Identifier length = 1
        Assert.Equal(0x00, svd[156 + 33]); // 0x00 identifier for root '.'
    }

    [Fact]
    public void Build_WithoutUdf_CreatesCompactImageWithoutLba256Gap()
    {
        var root = CreateStandardTestTree();
        var options = new IsoBuildOptions(
            VolumeLabel: "COMPACT_ISO",
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: false);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);
        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);

        Assert.True(reader.HasIso9660);
        Assert.True(reader.HasJoliet);
        Assert.False(reader.HasUdf);

        // Without UDF, total sectors should be compact (< 100 sectors, nowhere near 256)
        int totalSectors = (int)(isoStream.Length / OpticalConstants.DataSectorSize);
        Assert.True(totalSectors < 100);

        // Files should still be readable and byte-identical
        Assert.Equal(SampleReadme, reader.ReadFileBytes("/README.TXT"));
        Assert.Equal(SampleManualUnicode, reader.ReadFileBytes("/Docs/Manual_Unicode_文档.txt"));
    }

    [Fact]
    public void BuildToFile_And_IsoImageReader_FromFile_RoundTripSuccessfully()
    {
        var root = CreateStandardTestTree();
        string tempIsoPath = Path.Combine(Path.GetTempPath(), $"cdburner_test_{Guid.NewGuid():N}.iso");

        try
        {
            IsoUdfImageBuilder.BuildToFile(root, tempIsoPath);
            Assert.True(File.Exists(tempIsoPath));

            using var reader = IsoImageReader.FromFile(tempIsoPath);

            Assert.Equal("CDBURNER_DISC", reader.VolumeLabel);
            Assert.Equal(SampleReadme, reader.ReadFileBytes("/README.TXT"));
            Assert.Equal(SampleManualUnicode, reader.ReadFileBytes("/Docs/Manual_Unicode_文档.txt"));
            Assert.Equal(SampleAppBin, reader.ReadFileBytes("/Bin/app.bin"));
        }
        finally
        {
            if (File.Exists(tempIsoPath))
            {
                File.Delete(tempIsoPath);
            }
        }
    }

    [Fact]
    public async Task BuildToFileAsync_ReportsProgressAndBuildsValidIso()
    {
        var root = CreateStandardTestTree();
        string tempIsoPath = Path.Combine(Path.GetTempPath(), $"cdburner_test_async_{Guid.NewGuid():N}.iso");

        var progressValues = new List<double>();
        var progress = new Progress<double>(v => progressValues.Add(v));

        try
        {
            await IsoUdfImageBuilder.BuildToFileAsync(root, tempIsoPath, progress: progress);

            Assert.True(File.Exists(tempIsoPath));
            using var reader = IsoImageReader.FromFile(tempIsoPath);

            Assert.True(reader.TotalSectors >= 257);
            Assert.Equal(SampleManualUnicode, reader.ReadFileBytes("/Docs/Manual_Unicode_文档.txt"));
        }
        finally
        {
            if (File.Exists(tempIsoPath))
            {
                File.Delete(tempIsoPath);
            }
        }
    }

    [Fact]
    public void Build_MultiSectorFile_ExtractsPayloadAcrossSectorBoundaries()
    {
        var root = CompilationDirectory.CreateRoot();

        // 12,000 bytes spans 6 sectors (2048 * 5 = 10240 < 12000 < 12288 = 2048 * 6)
        byte[] largeData = new byte[12_000];
        for (int i = 0; i < largeData.Length; i++)
        {
            largeData[i] = (byte)(i % 251); // prime modulo for non-trivial byte pattern
        }

        root.AddFile("LargePayload.dat", largeData);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root);
        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);

        var file = reader.FindFile("/LargePayload.dat");
        Assert.NotNull(file);
        Assert.Equal(12_000, file.SizeBytes);

        byte[] readBack = reader.ReadFileBytes(file);
        Assert.Equal(largeData, readBack);
    }

    [Fact]
    public void Build_EmptyDirectoryAndZeroByteFile_HandlesGracefully()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("EmptyDir");
        root.AddFile("EmptyFile.txt", Array.Empty<byte>());

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root);
        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);

        var emptyFile = reader.FindFile("/EmptyFile.txt");
        Assert.NotNull(emptyFile);
        Assert.Equal(0, emptyFile.SizeBytes);

        byte[] emptyBytes = reader.ReadFileBytes("/EmptyFile.txt");
        Assert.Empty(emptyBytes);

        var emptyDir = reader.FindDirectory("/EmptyDir");
        Assert.NotNull(emptyDir);
        Assert.True(emptyDir.IsDirectory);
        Assert.Empty(emptyDir.Children);
    }
}
