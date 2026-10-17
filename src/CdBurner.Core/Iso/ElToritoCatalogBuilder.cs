using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Builds standards-compliant El Torito Boot Record Volume Descriptors and Boot Catalog sectors
/// for optical discs (ISO 9660 / Joliet / UDF Bridge).
/// </summary>
public static class ElToritoCatalogBuilder
{
    public const int SectorSize = OpticalConstants.DataSectorSize; // 2048 bytes
    public const string ElToritoSpecificationIdentifier = "EL TORITO SPECIFICATION";

    /// <summary>
    /// Builds a 2048-byte El Torito Boot Record Volume Descriptor (ISO 9660 descriptor type 0x00).
    /// </summary>
    /// <param name="bootCatalogLba">The Logical Block Address where the Boot Catalog is stored.</param>
    /// <returns>A 2048-byte array containing the Boot Record Volume Descriptor.</returns>
    public static byte[] BuildBootRecordVolumeDescriptor(uint bootCatalogLba)
    {
        byte[] descriptor = new byte[SectorSize];

        // Byte 0: Boot Record indicator (0x00 in ISO 9660)
        descriptor[0] = 0x00;

        // Bytes 1..5: Standard Identifier "CD001"
        Encoding.ASCII.GetBytes("CD001", descriptor.AsSpan(1, 5));

        // Byte 6: Version (0x01)
        descriptor[6] = 0x01;

        // Bytes 7..38 (32 bytes): Boot System Identifier "EL TORITO SPECIFICATION" padded with 0x00
        byte[] specBytes = Encoding.ASCII.GetBytes(ElToritoSpecificationIdentifier);
        int copyLen = Math.Min(specBytes.Length, 32);
        specBytes.AsSpan(0, copyLen).CopyTo(descriptor.AsSpan(7, copyLen));

        // Bytes 39..70 (32 bytes): Unused, must be 0x00 (already zero-initialized)

        // Bytes 71..74 (4 bytes): uint32 LE pointer to first sector of Boot Catalog
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(71, 4), bootCatalogLba);

        // Bytes 75..2047: Unused, must be 0x00 (already zero-initialized)
        return descriptor;
    }

    /// <summary>
    /// Builds a 32-byte Validation Entry for the Boot Catalog.
    /// Computes the 16-bit two's complement checksum such that the sum of all sixteen
    /// 16-bit little-endian words in the 32-byte record sums to 0x0000 (mod 65536).
    /// </summary>
    /// <param name="platformId">Target hardware platform (x86 BIOS, UEFI, PowerPC, Mac).</param>
    /// <param name="developerId">Developer/Vendor identifier string (up to 24 ASCII characters).</param>
    /// <returns>A 32-byte array containing the Validation Entry.</returns>
    public static byte[] BuildValidationEntry(BootPlatformId platformId, string developerId)
    {
        byte[] entry = new byte[32];

        // Byte 0: Header ID = 0x01
        entry[0] = 0x01;

        // Byte 1: Platform ID
        entry[1] = (byte)platformId;

        // Bytes 2..3: Reserved = 0x00, 0x00
        entry[2] = 0x00;
        entry[3] = 0x00;

        // Bytes 4..27 (24 bytes): Developer / Manufacturer ID string, ASCII padded with 0x00
        if (!string.IsNullOrEmpty(developerId))
        {
            byte[] devBytes = Encoding.ASCII.GetBytes(developerId);
            int len = Math.Min(devBytes.Length, 24);
            devBytes.AsSpan(0, len).CopyTo(entry.AsSpan(4, len));
        }

        // Bytes 28..29: Checksum (calculated below)
        entry[28] = 0x00;
        entry[29] = 0x00;

        // Bytes 30..31: Key bytes 0x55, 0xAA (0xAA55 as uint16 LE)
        entry[30] = 0x55;
        entry[31] = 0xAA;

        // Calculate sum of the other 15 16-bit words (all except word at offset 28..29)
        ushort wordSum = 0;
        for (int i = 0; i < 32; i += 2)
        {
            if (i != 28)
            {
                wordSum += BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(i, 2));
            }
        }

        // Two's complement checksum: (0 - wordSum) mod 65536
        ushort checksum = (ushort)(0 - wordSum);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(28, 2), checksum);

        return entry;
    }

    /// <summary>
    /// Builds the 2048-byte Boot Catalog sector containing the Validation Entry,
    /// Initial/Default Entry, and any Section Headers and Section Entries.
    /// </summary>
    /// <param name="configuration">The El Torito boot configuration.</param>
    /// <param name="defaultLoadRba">The LBA of the initial/default boot image on disc.</param>
    /// <param name="additionalLoadRbas">Optional LBAs for the additional section boot images.</param>
    /// <returns>A 2048-byte array containing the complete Boot Catalog sector.</returns>
    public static byte[] BuildBootCatalogSector(
        ElToritoBootConfiguration configuration,
        uint defaultLoadRba,
        IReadOnlyList<uint>? additionalLoadRbas = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        byte[] sector = new byte[SectorSize];

        // 1. Validation Entry (bytes 0..31)
        byte[] validationEntry = BuildValidationEntry(configuration.DefaultEntry.Platform, configuration.DeveloperId);
        validationEntry.CopyTo(sector.AsSpan(0, 32));

        // 2. Initial / Default Entry (bytes 32..63)
        var defaultSpec = configuration.DefaultEntry;
        sector[32] = 0x88; // Bootable
        sector[33] = (byte)defaultSpec.Emulation;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(34, 2), defaultSpec.LoadSegment);
        sector[36] = defaultSpec.SystemType;
        sector[37] = 0x00; // Unused
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(38, 2), defaultSpec.SectorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(40, 4), defaultLoadRba);
        // Bytes 44..63 (20 bytes): selection criteria / zeros

        // 3. Additional Section Headers and Section Entries (bytes 64..)
        if (configuration.AdditionalSectionEntries != null && configuration.AdditionalSectionEntries.Count > 0)
        {
            int offset = 64;
            for (int i = 0; i < configuration.AdditionalSectionEntries.Count; i++)
            {
                if (offset + 64 > SectorSize)
                {
                    break; // Catalog sector boundary limit
                }

                var sectionSpec = configuration.AdditionalSectionEntries[i];
                uint sectionLoadRba = (additionalLoadRbas != null && i < additionalLoadRbas.Count)
                    ? additionalLoadRbas[i]
                    : 0;
                bool isFinal = (i == configuration.AdditionalSectionEntries.Count - 1);

                // Section Header (32 bytes)
                sector[offset] = isFinal ? (byte)0x91 : (byte)0x90;
                sector[offset + 1] = (byte)sectionSpec.Platform;
                BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(offset + 2, 2), 1); // 1 entry follows

                // Bytes 4..31 (28 bytes): ID string or name
                if (!string.IsNullOrEmpty(sectionSpec.Name))
                {
                    byte[] nameBytes = Encoding.ASCII.GetBytes(sectionSpec.Name);
                    int copyNameLen = Math.Min(nameBytes.Length, 28);
                    nameBytes.AsSpan(0, copyNameLen).CopyTo(sector.AsSpan(offset + 4, copyNameLen));
                }
                offset += 32;

                // Section Entry (32 bytes)
                sector[offset] = 0x88; // Bootable
                sector[offset + 1] = (byte)sectionSpec.Emulation;
                BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(offset + 2, 2), sectionSpec.LoadSegment);
                sector[offset + 4] = sectionSpec.SystemType;
                sector[offset + 5] = 0x00;
                BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(offset + 6, 2), sectionSpec.SectorCount);
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(offset + 8, 4), sectionLoadRba);
                // Bytes 12..31: zeros
                offset += 32;
            }
        }

        return sector;
    }
}

/// <summary>
/// Parsed El Torito boot entry record (initial or section entry).
/// </summary>
public sealed class ElToritoBootEntry
{
    public byte BootIndicator { get; init; }
    public bool Bootable => BootIndicator == 0x88;
    public BootPlatformId Platform { get; init; }
    public BootEmulationType Emulation { get; init; }
    public ushort LoadSegment { get; init; }
    public byte SystemType { get; init; }
    public ushort SectorCount { get; init; }
    public uint LoadRba { get; init; }
}

/// <summary>
/// Parsed Validation Entry from the Boot Catalog.
/// </summary>
public sealed class ElToritoValidationEntry
{
    public byte HeaderId { get; init; }
    public BootPlatformId Platform { get; init; }
    public string DeveloperId { get; init; } = string.Empty;
    public ushort Checksum { get; init; }
    public bool IsChecksumValid { get; init; }
    public ushort KeyBytes { get; init; }
}

/// <summary>
/// Parsed Section Entry from the Boot Catalog.
/// </summary>
public sealed class ElToritoSectionEntry
{
    public byte HeaderIndicator { get; init; }
    public bool IsFinalSection => HeaderIndicator == 0x91;
    public BootPlatformId Platform { get; init; }
    public ushort EntryCount { get; init; }
    public string SectionId { get; init; } = string.Empty;
    public ElToritoBootEntry BootEntry { get; init; } = null!;

    // Flattened / forwarding properties for testing and inspection ergonomics
    public bool Bootable => BootEntry.Bootable;
    public BootEmulationType Emulation => BootEntry.Emulation;
    public ushort LoadSegment => BootEntry.LoadSegment;
    public byte SystemType => BootEntry.SystemType;
    public ushort SectorCount => BootEntry.SectorCount;
    public uint LoadRba => BootEntry.LoadRba;
}

/// <summary>
/// Complete El Torito boot specification information parsed from an optical disc image.
/// </summary>
public sealed class ElToritoBootInfo
{
    public int BootRecordLba { get; init; }
    public string BootSystemIdentifier { get; init; } = string.Empty;
    public uint BootCatalogLba { get; init; }
    public ElToritoValidationEntry ValidationEntry { get; init; } = null!;
    public ElToritoBootEntry InitialEntry { get; init; } = null!;
    public ElToritoBootEntry DefaultEntry => InitialEntry;
    public IReadOnlyList<ElToritoSectionEntry> SectionEntries { get; init; } = Array.Empty<ElToritoSectionEntry>();
}

/// <summary>
/// Parser and inspector for verifying El Torito Boot Record Volume Descriptors and Boot Catalogs in ISO images.
/// </summary>
public static class ElToritoCatalogInspector
{
    /// <summary>
    /// Parses and validates the El Torito boot structures from an ISO image stream.
    /// </summary>
    /// <param name="isoStream">Readable stream of the ISO image.</param>
    /// <returns>A populated <see cref="ElToritoBootInfo"/> instance.</returns>
    public static ElToritoBootInfo ParseBootInfo(Stream isoStream)
    {
        ArgumentNullException.ThrowIfNull(isoStream);
        if (!isoStream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(isoStream));
        }

        Stream stream = isoStream;
        MemoryStream? buffer = null;
        if (!stream.CanSeek)
        {
            buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            stream = buffer;
        }

        try
        {
            int maxSectors = (int)(stream.Length / OpticalConstants.DataSectorSize);
            if (maxSectors < 18)
            {
                throw new InvalidDataException("Stream is too short to contain an ISO 9660 volume descriptor sequence.");
            }

            int foundBootRecordLba = -1;
            byte[] bootRecordSector = new byte[OpticalConstants.DataSectorSize];

            // 1. Check LBA 17 first, then scan LBAs 16..32
            int[] candidateLbas = [17, 16, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31];
            foreach (int lba in candidateLbas)
            {
                if (lba >= maxSectors) continue;

                stream.Position = (long)lba * OpticalConstants.DataSectorSize;
                stream.ReadExactly(bootRecordSector);

                // Type 0x00, "CD001", Version 0x01, System ID "EL TORITO SPECIFICATION"
                if (bootRecordSector[0] == 0x00 &&
                    bootRecordSector[6] == 0x01 &&
                    Encoding.ASCII.GetString(bootRecordSector.AsSpan(1, 5)) == "CD001")
                {
                    string systemId = Encoding.ASCII.GetString(bootRecordSector.AsSpan(7, 23));
                    if (systemId == ElToritoCatalogBuilder.ElToritoSpecificationIdentifier)
                    {
                        foundBootRecordLba = lba;
                        break;
                    }
                }
            }

            if (foundBootRecordLba < 0)
            {
                throw new InvalidDataException("El Torito Boot Record Volume Descriptor not found in ISO volume sequence.");
            }

            string bootSysId = Encoding.ASCII.GetString(bootRecordSector.AsSpan(7, 32)).TrimEnd('\0', ' ');
            uint bootCatalogLba = BinaryPrimitives.ReadUInt32LittleEndian(bootRecordSector.AsSpan(71, 4));

            if (bootCatalogLba >= maxSectors)
            {
                throw new InvalidDataException($"Boot Catalog LBA {bootCatalogLba} is outside the stream bounds.");
            }

            // 2. Read Boot Catalog sector
            byte[] catalogSector = new byte[OpticalConstants.DataSectorSize];
            stream.Position = (long)bootCatalogLba * OpticalConstants.DataSectorSize;
            stream.ReadExactly(catalogSector);

            // 3. Parse Validation Entry (bytes 0..31)
            byte headerId = catalogSector[0];
            var valPlatform = (BootPlatformId)catalogSector[1];
            string developerId = Encoding.ASCII.GetString(catalogSector.AsSpan(4, 24)).TrimEnd('\0', ' ');
            ushort checksum = BinaryPrimitives.ReadUInt16LittleEndian(catalogSector.AsSpan(28, 2));
            ushort keyBytes = BinaryPrimitives.ReadUInt16LittleEndian(catalogSector.AsSpan(30, 2));

            // Verify checksum: sum of all 16 words mod 65536 == 0
            ushort wordSum = 0;
            for (int i = 0; i < 32; i += 2)
            {
                wordSum += BinaryPrimitives.ReadUInt16LittleEndian(catalogSector.AsSpan(i, 2));
            }
            bool isChecksumValid = (wordSum == 0) && (catalogSector[30] == 0x55) && (catalogSector[31] == 0xAA);

            var validationEntry = new ElToritoValidationEntry
            {
                HeaderId = headerId,
                Platform = valPlatform,
                DeveloperId = developerId,
                Checksum = checksum,
                IsChecksumValid = isChecksumValid,
                KeyBytes = keyBytes
            };

            // 4. Parse Initial / Default Entry (bytes 32..63)
            byte defaultBootInd = catalogSector[32];
            var defaultEmulation = (BootEmulationType)catalogSector[33];
            ushort defaultLoadSeg = BinaryPrimitives.ReadUInt16LittleEndian(catalogSector.AsSpan(34, 2));
            byte defaultSysType = catalogSector[36];
            ushort defaultSectorCount = BinaryPrimitives.ReadUInt16LittleEndian(catalogSector.AsSpan(38, 2));
            uint defaultLoadRba = BinaryPrimitives.ReadUInt32LittleEndian(catalogSector.AsSpan(40, 4));

            var initialEntry = new ElToritoBootEntry
            {
                BootIndicator = defaultBootInd,
                Platform = valPlatform,
                Emulation = defaultEmulation,
                LoadSegment = defaultLoadSeg,
                SystemType = defaultSysType,
                SectorCount = defaultSectorCount,
                LoadRba = defaultLoadRba
            };

            // 5. Parse Section Headers and Section Entries (bytes 64..)
            var sectionEntries = new List<ElToritoSectionEntry>();
            int offset = 64;

            while (offset + 32 <= OpticalConstants.DataSectorSize)
            {
                byte indicator = catalogSector[offset];
                if (indicator is 0x90 or 0x91)
                {
                    bool isFinal = (indicator == 0x91);
                    var sectionPlatform = (BootPlatformId)catalogSector[offset + 1];
                    ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(catalogSector.AsSpan(offset + 2, 2));
                    string sectionId = Encoding.ASCII.GetString(catalogSector.AsSpan(offset + 4, 28)).TrimEnd('\0', ' ');

                    offset += 32;
                    if (offset + 32 > OpticalConstants.DataSectorSize) break;

                    int count = entryCount > 0 ? entryCount : 1;
                    for (int c = 0; c < count && offset + 32 <= OpticalConstants.DataSectorSize; c++)
                    {
                        byte bootIndicator = catalogSector[offset];
                        if (bootIndicator is 0x88 or 0x00)
                        {
                            var bootEntry = new ElToritoBootEntry
                            {
                                BootIndicator = bootIndicator,
                                Platform = sectionPlatform,
                                Emulation = (BootEmulationType)catalogSector[offset + 1],
                                LoadSegment = BinaryPrimitives.ReadUInt16LittleEndian(catalogSector.AsSpan(offset + 2, 2)),
                                SystemType = catalogSector[offset + 4],
                                SectorCount = BinaryPrimitives.ReadUInt16LittleEndian(catalogSector.AsSpan(offset + 6, 2)),
                                LoadRba = BinaryPrimitives.ReadUInt32LittleEndian(catalogSector.AsSpan(offset + 8, 4))
                            };

                            sectionEntries.Add(new ElToritoSectionEntry
                            {
                                HeaderIndicator = indicator,
                                Platform = sectionPlatform,
                                EntryCount = entryCount,
                                SectionId = sectionId,
                                BootEntry = bootEntry
                            });

                            offset += 32;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (isFinal)
                    {
                        break;
                    }
                }
                else
                {
                    // No more section headers
                    break;
                }
            }

            return new ElToritoBootInfo
            {
                BootRecordLba = foundBootRecordLba,
                BootSystemIdentifier = bootSysId,
                BootCatalogLba = bootCatalogLba,
                ValidationEntry = validationEntry,
                InitialEntry = initialEntry,
                SectionEntries = sectionEntries
            };
        }
        finally
        {
            buffer?.Dispose();
        }
    }
}
