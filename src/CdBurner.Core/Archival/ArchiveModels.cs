using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>
/// Source file included in an archival set.
/// </summary>
/// <param name="FullPath">The full filesystem path on the local drive.</param>
/// <param name="RelativePath">Relative path within the archive disc (uses '/' separators).</param>
/// <param name="SizeBytes">Length of the source file in bytes.</param>
public sealed record ArchiveSourceFile(string FullPath, string RelativePath, long SizeBytes);

/// <summary>
/// Layout plan for a single disc in an archival set.
/// </summary>
/// <param name="DiscNumber">1-based disc number.</param>
/// <param name="Files">List of source files allocated to this disc.</param>
/// <param name="DataBytes">Total raw data bytes on this disc.</param>
/// <param name="DataSectors">Total 2048-byte sectors consumed by files on this disc.</param>
public sealed record ArchiveDiscPlan(int DiscNumber, IReadOnlyList<ArchiveSourceFile> Files, long DataBytes, int DataSectors);

/// <summary>
/// Multi-disc spanning plan for an archival set.
/// </summary>
/// <param name="SetId">Unique identifier for the archive set.</param>
/// <param name="MediaType">Target optical media type.</param>
/// <param name="RedundancyPercent">Configured redundancy percentage (0..100).</param>
/// <param name="UsableSectorsPerDisc">Usable 2048-byte data sectors per disc.</param>
/// <param name="Discs">List of planned discs.</param>
/// <param name="TotalBytes">Total data bytes across all planned discs.</param>
public sealed record ArchivePlan(
    string SetId,
    DiscMediaType MediaType,
    int RedundancyPercent,
    int UsableSectorsPerDisc,
    IReadOnlyList<ArchiveDiscPlan> Discs,
    long TotalBytes);

/// <summary>
/// File checksum entry in an archive manifest.
/// </summary>
/// <param name="RelativePath">Relative path with '/' separators.</param>
/// <param name="SizeBytes">Length of the file in bytes.</param>
/// <param name="Sha256">SHA-256 digest in lowercase hexadecimal format.</param>
public sealed record ArchiveManifestEntry(string RelativePath, long SizeBytes, string Sha256);

/// <summary>
/// Metadata index for an archive disc.
/// </summary>
/// <param name="SetId">Unique identifier for the archive set.</param>
/// <param name="DiscNumber">1-based disc number.</param>
/// <param name="DiscCount">Total number of discs in the set.</param>
/// <param name="MediaType">Enum name of the media type.</param>
/// <param name="CreatedUtc">Timestamp in UTC when the index was created.</param>
/// <param name="RedundancyPercent">Configured redundancy percentage (0..100).</param>
/// <param name="Files">List of file manifest entries on this disc.</param>
public sealed record ArchiveIndex(
    string SetId,
    int DiscNumber,
    int DiscCount,
    string MediaType,
    DateTime CreatedUtc,
    int RedundancyPercent,
    IReadOnlyList<ArchiveManifestEntry> Files);

/// <summary>
/// Outcome of verifying an archival disc against its manifest.
/// </summary>
/// <param name="FilesChecked">Total count of files checked from the manifest.</param>
/// <param name="Missing">List of missing relative file paths.</param>
/// <param name="Corrupt">List of corrupted relative file paths whose checksum mismatch.</param>
/// <param name="HasRecoveryData">Indicates whether a RECOVERY directory with .par2 files is present.</param>
/// <param name="SetId">Set ID from index JSON if present.</param>
/// <param name="DiscNumber">Disc number from index JSON if present.</param>
/// <param name="DiscCount">Disc count from index JSON if present.</param>
public sealed record ArchiveVerificationResult(
    int FilesChecked,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Corrupt,
    bool HasRecoveryData,
    string? SetId,
    int? DiscNumber,
    int? DiscCount)
{
    /// <summary>
    /// Gets a value indicating whether all files were intact (none missing and none corrupt).
    /// </summary>
    public bool IsIntact => Missing.Count == 0 && Corrupt.Count == 0;
}
