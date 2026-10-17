namespace CdBurner.Core.Iso;

/// <summary>
/// Configuration options for compiling an optical disc filesystem image (ISO 9660 / Joliet / UDF Bridge).
/// </summary>
/// <param name="VolumeLabel">Disc volume label (up to 32 characters in ISO 9660 / Joliet).</param>
/// <param name="IncludeIso9660">Whether to include the standard ISO 9660 Primary Volume Descriptor and Level 1/2 directory tree.</param>
/// <param name="IncludeJoliet">Whether to include Microsoft Joliet UCS-2BE Supplementary Volume Descriptor and directory tree.</param>
/// <param name="IncludeUdf">Whether to include ECMA-167 / UDF bridge descriptors (BEA01, NSR02/NSR03, TEA01, AVDP at LBA 256).</param>
/// <param name="UdfRevision">UDF specification revision (0x0102 for UDF 1.02, 0x0201 for UDF 2.01).</param>
/// <param name="PublisherId">Publisher identifier string.</param>
/// <param name="ApplicationId">Application identifier string.</param>
/// <param name="BootConfiguration">Optional El Torito bootable configuration (BIOS / UEFI / Multi-boot).</param>
public record IsoBuildOptions(
    string VolumeLabel = "CDBURNER_DISC",
    bool IncludeIso9660 = true,
    bool IncludeJoliet = true,
    bool IncludeUdf = true,
    ushort UdfRevision = 0x0102,
    string PublisherId = "CDBURNER",
    string ApplicationId = "CDBURNER .NET 10",
    ElToritoBootConfiguration? BootConfiguration = null);

