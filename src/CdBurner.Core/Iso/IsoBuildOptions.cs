namespace CdBurner.Core.Iso;

/// <summary>
/// Configuration options for compiling an optical disc filesystem image (ISO 9660 / Joliet / UDF Bridge).
/// </summary>
/// <param name="VolumeLabel">Disc volume label (up to 32 characters in ISO 9660 / Joliet).</param>
/// <param name="IncludeIso9660">Whether to include the standard ISO 9660 Primary Volume Descriptor and Level 1/2 directory tree.</param>
/// <param name="IncludeJoliet">Whether to include Microsoft Joliet UCS-2BE Supplementary Volume Descriptor and directory tree.</param>
/// <param name="IncludeUdf">Whether to include ECMA-167 / UDF bridge descriptors (BEA01, NSR02/NSR03, TEA01, AVDP at LBA 256).</param>
/// <param name="UdfRevision">Requested UDF revision. The builder currently always writes UDF 1.02 (NSR02), the most widely compatible revision and the one required for DVD-Video.</param>
/// <param name="PublisherId">Publisher identifier string.</param>
/// <param name="ApplicationId">Application identifier string.</param>
/// <param name="BootConfiguration">Optional El Torito bootable configuration (BIOS / UEFI / Multi-boot).</param>
/// <param name="FixedTimestampUtc">Optional fixed build time for reproducible images (like <c>SOURCE_DATE_EPOCH</c>).
/// When set, every volume, descriptor and integrity timestamp uses it, file and directory times later than it are
/// clamped to it, and the UDF volume-set ID is derived from the tree, so the same tree always gives the same bytes.
/// When null (the default), images carry the build time as before.</param>
/// <param name="WriteSha256Manifest">Adds a <c>SHA256SUMS</c> file (GNU coreutils format) to the disc root listing
/// every other file. Files are hashed while they stream into the image, so the source is read only once. First session
/// only.</param>
/// <param name="WriteBlake3Manifest">Adds a <c>BLAKE3SUMS</c> file (<c>b3sum</c> format) the same way, using IgnisDisc's
/// built-in BLAKE3. Can be combined with <paramref name="WriteSha256Manifest"/>; the source is still read once.</param>
public record IsoBuildOptions(
    string VolumeLabel = "CDBURNER_DISC",
    bool IncludeIso9660 = true,
    bool IncludeJoliet = true,
    bool IncludeUdf = true,
    ushort UdfRevision = 0x0102,
    string PublisherId = "IGNISDISC",
    string ApplicationId = "IGNISDISC OPTICAL STUDIO",
    ElToritoBootConfiguration? BootConfiguration = null,
    DateTime? FixedTimestampUtc = null,
    bool WriteSha256Manifest = false,
    bool WriteBlake3Manifest = false);

