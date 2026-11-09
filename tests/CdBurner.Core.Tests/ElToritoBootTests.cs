using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class ElToritoBootTests
{
    [Fact]
    public void ValidationEntry_ComputesChecksumSummingToZero_AndEndsWithKeyBytes()
    {
        // 1. Verify ElToritoCatalogBuilder computes a Validation Entry whose sixteen 16-bit
        //    little-endian words sum to 0x0000 and ends with 0x55, 0xAA.
        var testCases = new (BootPlatformId Platform, string DevId)[]
        {
            (BootPlatformId.X86Bios, "CDBURNER"),
            (BootPlatformId.Efi, "MICROSOFT UEFI"),
            (BootPlatformId.PowerPc, "APPLE POWERPC"),
            (BootPlatformId.Mac, ""),
            (BootPlatformId.X86Bios, "EXACTLY_24_CHARS_VENDOR_")
        };

        foreach (var (platform, devId) in testCases)
        {
            byte[] entry = ElToritoCatalogBuilder.BuildValidationEntry(platform, devId);

            Assert.Equal(32, entry.Length);
            Assert.Equal(0x01, entry[0]); // Header ID
            Assert.Equal((byte)platform, entry[1]); // Platform ID
            Assert.Equal(0x00, entry[2]); // Reserved
            Assert.Equal(0x00, entry[3]); // Reserved

            // Key bytes 0x55, 0xAA at bytes 30..31
            Assert.Equal(0x55, entry[30]);
            Assert.Equal(0xAA, entry[31]);

            // Sum of all sixteen 16-bit little-endian words must equal 0x0000 modulo 65536
            ushort totalSum = 0;
            for (int i = 0; i < 32; i += 2)
            {
                totalSum += BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(i, 2));
            }

            Assert.Equal(0x0000, totalSum);
        }
    }

    [Fact]
    public void Build_DualBootBiosAndUefi_ProducesValidElToritoStructuresAndBootPayloads()
    {
        // 1. Prepare BIOS (etfsboot.com, 4 sectors = 2048 bytes) and UEFI (efisys.bin, 64 sectors = 32768 bytes) boot payloads
        byte[] biosBootBytes = Enumerable.Range(0, 2048).Select(i => (byte)(0x33 ^ (i % 251))).ToArray();
        byte[] uefiBootBytes = Enumerable.Range(0, 64 * 512).Select(i => (byte)(0x77 ^ (i % 241))).ToArray();

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("README.TXT", "Dual Boot ISO Image"u8.ToArray());

        var bootConfig = new ElToritoBootConfiguration(
            DefaultEntry: new BootCatalogEntrySpec(
                Platform: BootPlatformId.X86Bios,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: 4,
                BootImageStreamFactory: () => new MemoryStream(biosBootBytes),
                Name: "etfsboot.com"),
            AdditionalSectionEntries: new[]
            {
                new BootCatalogEntrySpec(
                    Platform: BootPlatformId.Efi,
                    Emulation: BootEmulationType.NoEmulation,
                    LoadSegment: 0,
                    SectorCount: 64,
                    BootImageStreamFactory: () => new MemoryStream(uefiBootBytes),
                    Name: "efisys.bin")
            },
            DeveloperId: "CDBURNER_DUAL_BOOT");

        var options = new IsoBuildOptions(
            VolumeLabel: "DUAL_BOOT_DISC",
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true,
            BootConfiguration: bootConfig);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);

        // 2. Parse and validate with ElToritoCatalogInspector
        var bootInfo = ElToritoCatalogInspector.ParseBootInfo(isoStream);

        // Verify Boot Record Volume Descriptor exists at LBA 17 with "EL TORITO SPECIFICATION"
        Assert.Equal(17, bootInfo.BootRecordLba);
        Assert.Equal("EL TORITO SPECIFICATION", bootInfo.BootSystemIdentifier);

        // Verify Validation Entry
        Assert.True(bootInfo.ValidationEntry.IsChecksumValid);
        Assert.Equal("CDBURNER_DUAL_BOOT", bootInfo.ValidationEntry.DeveloperId);
        Assert.Equal(BootPlatformId.X86Bios, bootInfo.ValidationEntry.Platform);

        // Verify Default entry has Platform == X86Bios, Bootable == true
        Assert.Equal(BootPlatformId.X86Bios, bootInfo.DefaultEntry.Platform);
        Assert.True(bootInfo.DefaultEntry.Bootable);
        Assert.Equal(BootEmulationType.NoEmulation, bootInfo.DefaultEntry.Emulation);
        Assert.Equal(4, bootInfo.DefaultEntry.SectorCount);
        Assert.True(bootInfo.DefaultEntry.LoadRba > 17);

        // Verify Section entry has Platform == Efi, Bootable == true
        Assert.Single(bootInfo.SectionEntries);
        var section = bootInfo.SectionEntries[0];
        Assert.Equal(BootPlatformId.Efi, section.Platform);
        Assert.True(section.Bootable);
        Assert.Equal(BootEmulationType.NoEmulation, section.Emulation);
        Assert.Equal(64, section.SectorCount);
        Assert.True(section.LoadRba > bootInfo.DefaultEntry.LoadRba);

        // 3. Verify exact byte contents of the BIOS boot image at DefaultEntry.LoadRba
        byte[] readBiosBytes = new byte[biosBootBytes.Length];
        isoStream.Position = (long)bootInfo.DefaultEntry.LoadRba * OpticalConstants.DataSectorSize;
        isoStream.ReadExactly(readBiosBytes);
        Assert.Equal(biosBootBytes, readBiosBytes);

        // 4. Verify exact byte contents of the UEFI boot image at SectionEntry.LoadRba
        byte[] readUefiBytes = new byte[uefiBootBytes.Length];
        isoStream.Position = (long)section.LoadRba * OpticalConstants.DataSectorSize;
        isoStream.ReadExactly(readUefiBytes);
        Assert.Equal(uefiBootBytes, readUefiBytes);
    }

    [Fact]
    public void Build_SingleBootBios_ParsesValidDefaultEntryWithoutSections()
    {
        byte[] biosPayload = Enumerable.Range(0, 2048).Select(i => (byte)(i % 128)).ToArray();

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("AUTORUN.INF", "[autorun]\r\nopen=setup.exe"u8.ToArray());

        var bootConfig = new ElToritoBootConfiguration(
            DefaultEntry: new BootCatalogEntrySpec(
                Platform: BootPlatformId.X86Bios,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0x07C0,
                SectorCount: 4,
                BootImageStreamFactory: () => new MemoryStream(biosPayload),
                Name: "bootmgr"),
            DeveloperId: "CDBURNER_BIOS");

        var options = new IsoBuildOptions(
            VolumeLabel: "BIOS_ONLY_DISC",
            IncludeIso9660: true,
            IncludeJoliet: false,
            IncludeUdf: false,
            BootConfiguration: bootConfig);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);
        var bootInfo = ElToritoCatalogInspector.ParseBootInfo(isoStream);

        Assert.Equal(17, bootInfo.BootRecordLba);
        Assert.Equal("EL TORITO SPECIFICATION", bootInfo.BootSystemIdentifier);
        Assert.True(bootInfo.ValidationEntry.IsChecksumValid);
        Assert.Equal("CDBURNER_BIOS", bootInfo.ValidationEntry.DeveloperId);

        Assert.Equal(BootPlatformId.X86Bios, bootInfo.DefaultEntry.Platform);
        Assert.True(bootInfo.DefaultEntry.Bootable);
        Assert.Equal(0x07C0, bootInfo.DefaultEntry.LoadSegment);
        Assert.Equal(4, bootInfo.DefaultEntry.SectorCount);
        Assert.Empty(bootInfo.SectionEntries);

        byte[] readBios = new byte[biosPayload.Length];
        isoStream.Position = (long)bootInfo.DefaultEntry.LoadRba * OpticalConstants.DataSectorSize;
        isoStream.ReadExactly(readBios);
        Assert.Equal(biosPayload, readBios);
    }

    [Fact]
    public void Build_BootableIsoWithJolietAndUdf_PreservesDescriptorsAndFiles()
    {
        byte[] biosPayload = new byte[2048];
        Array.Fill(biosPayload, (byte)0xCC);

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("Setup.exe", new byte[4096]);
        var docs = root.AddDirectory("Docs");
        docs.AddFile("Guide_文档.txt", "Bootable ISO User Guide 文档"u8.ToArray());

        var bootConfig = new ElToritoBootConfiguration(
            DefaultEntry: new BootCatalogEntrySpec(
                Platform: BootPlatformId.X86Bios,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: 4,
                BootImageStreamFactory: () => new MemoryStream(biosPayload)));

        var options = new IsoBuildOptions(
            VolumeLabel: "FULL_BOOT_DISC",
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true,
            BootConfiguration: bootConfig);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);

        // Verify LBA 16 = PVD
        isoStream.Position = 16 * OpticalConstants.DataSectorSize;
        byte[] pvd = new byte[7];
        isoStream.ReadExactly(pvd);
        Assert.Equal([0x01, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1', 0x01], pvd);

        // Verify LBA 17 = Boot Record
        isoStream.Position = 17 * OpticalConstants.DataSectorSize;
        byte[] bootRecord = new byte[7];
        isoStream.ReadExactly(bootRecord);
        Assert.Equal([0x00, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1', 0x01], bootRecord);

        // Verify LBA 18 = Joliet SVD
        isoStream.Position = 18 * OpticalConstants.DataSectorSize;
        byte[] svd = new byte[7];
        isoStream.ReadExactly(svd);
        Assert.Equal([0x02, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1', 0x01], svd);

        // Verify LBA 256 = UDF AVDP
        isoStream.Position = 256 * OpticalConstants.DataSectorSize;
        byte[] avdpTag = new byte[2];
        isoStream.ReadExactly(avdpTag);
        Assert.Equal(0x0002, BinaryPrimitives.ReadUInt16LittleEndian(avdpTag));

        // Verify Files are intact via IsoImageReader
        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);
        Assert.Equal("FULL_BOOT_DISC", reader.VolumeLabel);
        var guideFile = reader.FindFile("/Docs/Guide_文档.txt");
        Assert.NotNull(guideFile);
        Assert.Equal("Bootable ISO User Guide 文档"u8.ToArray(), reader.ReadFileBytes("/Docs/Guide_文档.txt"));
    }

    [Fact]
    public void ElToritoCatalogInspector_ThrowsOnNonBootableIso()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("README.TXT", "Non-bootable"u8.ToArray());

        var options = new IsoBuildOptions(BootConfiguration: null);
        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);

        Assert.Throws<InvalidDataException>(() => ElToritoCatalogInspector.ParseBootInfo(isoStream));
    }

    [Fact]
    public void BuildToFile_BootableIso_RoundTripsFromFileStream()
    {
        byte[] biosPayload = Enumerable.Range(0, 2048).Select(i => (byte)(i % 256)).ToArray();
        byte[] uefiPayload = Enumerable.Range(0, 8192).Select(i => (byte)((i * 3) % 256)).ToArray();

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("INSTALL.CMD", "echo Installing..."u8.ToArray());

        var bootConfig = new ElToritoBootConfiguration(
            DefaultEntry: new BootCatalogEntrySpec(
                Platform: BootPlatformId.X86Bios,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: 4,
                BootImageStreamFactory: () => new MemoryStream(biosPayload)),
            AdditionalSectionEntries: new[]
            {
                new BootCatalogEntrySpec(
                    Platform: BootPlatformId.Efi,
                    Emulation: BootEmulationType.NoEmulation,
                    LoadSegment: 0,
                    SectorCount: 16,
                    BootImageStreamFactory: () => new MemoryStream(uefiPayload),
                    Name: "efi_boot.bin")
            });

        string tempIso = Path.Combine(Path.GetTempPath(), $"boot_roundtrip_{Guid.NewGuid():N}.iso");
        try
        {
            IsoUdfImageBuilder.BuildToFile(root, tempIso, new IsoBuildOptions(BootConfiguration: bootConfig));

            Assert.True(File.Exists(tempIso));
            using var fs = File.OpenRead(tempIso);
            var bootInfo = ElToritoCatalogInspector.ParseBootInfo(fs);

            Assert.Equal(17, bootInfo.BootRecordLba);
            Assert.Equal(BootPlatformId.X86Bios, bootInfo.DefaultEntry.Platform);
            Assert.Equal(BootPlatformId.Efi, bootInfo.SectionEntries[0].Platform);

            byte[] readBios = new byte[biosPayload.Length];
            fs.Position = (long)bootInfo.DefaultEntry.LoadRba * OpticalConstants.DataSectorSize;
            fs.ReadExactly(readBios);
            Assert.Equal(biosPayload, readBios);

            byte[] readUefi = new byte[uefiPayload.Length];
            fs.Position = (long)bootInfo.SectionEntries[0].LoadRba * OpticalConstants.DataSectorSize;
            fs.ReadExactly(readUefi);
            Assert.Equal(uefiPayload, readUefi);
        }
        finally
        {
            if (File.Exists(tempIso))
            {
                File.Delete(tempIso);
            }
        }
    }
}
