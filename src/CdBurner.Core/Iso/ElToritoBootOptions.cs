namespace CdBurner.Core.Iso;

/// <summary>
/// Architecture platform identifier for an El Torito boot entry.
/// </summary>
public enum BootPlatformId : byte
{
    X86Bios = 0x00,
    PowerPc = 0x01,
    Mac = 0x02,
    Efi = 0xEF
}

/// <summary>
/// Boot media emulation type for BIOS / firmware INT 13h emulation.
/// </summary>
public enum BootEmulationType : byte
{
    NoEmulation = 0x00,
    Floppy120M = 0x01,
    Floppy144M = 0x02,
    Floppy288M = 0x03,
    HardDisk = 0x04
}

/// <summary>
/// Specification describing a boot image entry in an El Torito boot catalog.
/// </summary>
/// <param name="Platform">Target hardware platform (BIOS, UEFI, PowerPC, Mac).</param>
/// <param name="Emulation">Boot media emulation type.</param>
/// <param name="LoadSegment">Load segment address in RAM (0x0000 indicates default 0x07C0 for BIOS).</param>
/// <param name="SectorCount">Virtual sectors loaded by BIOS (typically in 512-byte sectors for No Emulation).</param>
/// <param name="BootImageStreamFactory">Factory producing a readable stream of the raw boot image bytes.</param>
/// <param name="Name">Descriptive name or filename for the boot image.</param>
public record BootCatalogEntrySpec(
    BootPlatformId Platform,
    BootEmulationType Emulation,
    ushort LoadSegment,
    ushort SectorCount,
    Func<Stream> BootImageStreamFactory,
    string Name = "boot.img")
{
    /// <summary>
    /// System type byte (matches partition table type for hard disk emulation, 0x00 for no emulation).
    /// </summary>
    public byte SystemType { get; init; } = 0x00;
}

/// <summary>
/// Master El Torito boot configuration for creating bootable optical disc images (BIOS, UEFI, or multi-boot).
/// </summary>
/// <param name="DefaultEntry">The initial / default boot entry (typically x86 BIOS).</param>
/// <param name="AdditionalSectionEntries">Optional additional section entries (such as UEFI multi-boot).</param>
/// <param name="DeveloperId">Developer / vendor identifier string recorded in the Validation Entry (up to 24 chars).</param>
public record ElToritoBootConfiguration(
    BootCatalogEntrySpec DefaultEntry,
    IReadOnlyList<BootCatalogEntrySpec>? AdditionalSectionEntries = null,
    string DeveloperId = "CDBURNER");
