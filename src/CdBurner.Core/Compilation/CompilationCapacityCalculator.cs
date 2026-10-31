using CdBurner.Core.Primitives;

namespace CdBurner.Core.Compilation;

/// <summary>
/// Status classification of compilation size relative to physical optical media capacity limits.
/// </summary>
public enum CapacityStatus
{
    /// <summary>
    /// Compilation fits comfortably within standard media capacity (&lt;= 95%).
    /// </summary>
    FitsComfortably,

    /// <summary>
    /// Compilation fits within nominal capacity but consumes more than 95% of available sectors.
    /// </summary>
    NearCapacityWarning,

    /// <summary>
    /// Compilation exceeds nominal capacity (e.g. 360,000 sectors for 80-min CD) but remains
    /// within CD overburn limits (up to 405,000 sectors / 90 min).
    /// </summary>
    OverburnRequired,

    /// <summary>
    /// Compilation exceeds maximum allowable media capacity or overburn limits.
    /// </summary>
    ExceedsMediaCapacity
}

/// <summary>
/// Detailed assessment of sector consumption, capacity percentage, and fit status for a compilation.
/// </summary>
/// <param name="TotalBytes">Total data bytes occupied on disc (TotalSectors * 2048).</param>
/// <param name="TotalSectors">Total 2048-byte sectors required (file data + filesystem overhead).</param>
/// <param name="MediaMaxSectors">Nominal physical capacity of the target media in 2048-byte sectors.</param>
/// <param name="UsagePercent">Percentage of media capacity consumed (0.0 to 100.0+).</param>
/// <param name="Status">Capacity fit classification.</param>
/// <param name="TotalFileDataBytes">Raw unpadded byte count of all file data payloads.</param>
/// <param name="TotalFileDataSectors">Data sectors occupied by files after rounding each to 2048-byte sector boundary.</param>
/// <param name="FileSystemOverheadSectors">Sectors reserved for ISO 9660, Joliet, UDF, and directory records.</param>
public record CapacityAssessment(
    long TotalBytes,
    int TotalSectors,
    int MediaMaxSectors,
    double UsagePercent,
    CapacityStatus Status,
    long TotalFileDataBytes = 0,
    int TotalFileDataSectors = 0,
    int FileSystemOverheadSectors = 0)
{
    /// <summary>
    /// Available unallocated sectors remaining on media (clamped to 0 if full or overburn).
    /// </summary>
    public int FreeSectors => Math.Max(0, MediaMaxSectors - TotalSectors);

    /// <summary>
    /// Available unallocated bytes remaining on media.
    /// </summary>
    public long FreeBytes => (long)FreeSectors * OpticalConstants.DataSectorSize;

    /// <summary>
    /// Indicates whether the compilation requires CD overburn recording mode.
    /// </summary>
    public bool IsOverburn => Status == CapacityStatus.OverburnRequired;

    /// <summary>
    /// Indicates whether the compilation can be recorded to media (either normally or via overburn).
    /// </summary>
    public bool CanFitOnMedia => Status != CapacityStatus.ExceedsMediaCapacity;
}

/// <summary>
/// Real-time optical media capacity calculator for virtual compilation file trees.
/// Computes exact 2048-byte sector usage including per-file sector alignment and filesystem descriptor overhead.
/// </summary>
public static class CompilationCapacityCalculator
{
    /// <summary>
    /// Computes the exact number of 2048-byte sectors required for a file of the given byte size.
    /// Files larger than 0 bytes round up to the next sector boundary.
    /// </summary>
    public static int CalculateFileSectors(long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return 0;
        }

        return (int)((sizeBytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);
    }

    /// <summary>
    /// Computes the total file data sectors for a collection of compilation files,
    /// rounding each individual file up to a 2048-byte sector boundary.
    /// </summary>
    public static int CalculateTotalFileDataSectors(IEnumerable<CompilationFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        int total = 0;
        foreach (var file in files)
        {
            // If the file was imported from a previous session and is already on disc,
            // it does not consume newly burned sectors in the current session.
            if (!file.ImportedStartLba.HasValue || file.IsPlannedLayoutLba)
            {
                total += CalculateFileSectors(file.SizeBytes);
            }
        }
        return total;
    }

    /// <summary>
    /// Computes the filesystem overhead in 2048-byte sectors for System Area (16 sectors),
    /// Volume Descriptors, Path Tables, UDF structures, and directory extents.
    /// </summary>
    public static int CalculateFileSystemOverheadSectors(
        int directoryCount,
        bool includeIso9660 = true,
        bool includeJoliet = true,
        bool includeUdf = true,
        bool isBootable = false)
    {
        // System Area: LBAs 0..15 (32,768 bytes)
        int overhead = 16;

        if (includeIso9660)
        {
            overhead += 1; // Primary Volume Descriptor (LBA 16)
            overhead += 2; // ISO 9660 Path Tables (Type L and Type M)
        }

        if (includeJoliet)
        {
            overhead += 1; // Joliet Supplementary Volume Descriptor
            overhead += 2; // Joliet Path Tables (Type L and Type M)
        }

        if (isBootable)
        {
            overhead += 1; // El Torito Boot Record Volume Descriptor (LBA 17)
            overhead += 1; // Boot Catalog Sector
        }

        overhead += 1; // Volume Descriptor Set Terminator (0xFF)

        if (includeUdf)
        {
            // UDF Volume Recognition Sequence (BEA01, NSR02, TEA01),
            // AVDP at LBA 256, Main & Reserve VDS, File Set Descriptor, ICBs
            overhead += 32;
        }

        // Directory records: each directory extent requires at least 1 sector
        overhead += Math.Max(1, directoryCount);

        return overhead;
    }

    /// <summary>
    /// Evaluates total sectors against a target media type and produces a <see cref="CapacityAssessment"/>.
    /// </summary>
    public static CapacityAssessment AssessCapacity(
        int totalSectors,
        DiscMediaType mediaType,
        int mediaMaxSectors = 0,
        long totalFileDataBytes = 0,
        int totalFileDataSectors = 0,
        int fileSystemOverheadSectors = 0)
    {
        if (mediaMaxSectors <= 0)
        {
            mediaMaxSectors = mediaType.GetNominalCapacitySectors();
            if (mediaMaxSectors <= 0)
            {
                mediaMaxSectors = OpticalConstants.Cd700MbSectors; // default 360,000 sectors
            }
        }

        double usagePercent = mediaMaxSectors > 0
            ? Math.Round(((double)totalSectors / mediaMaxSectors) * 100.0, 2)
            : 0.0;

        CapacityStatus status;
        if (mediaType.IsCd())
        {
            if (totalSectors > OpticalConstants.Cd90MinSectors)
            {
                status = CapacityStatus.ExceedsMediaCapacity;
            }
            else if (totalSectors > mediaMaxSectors)
            {
                status = CapacityStatus.OverburnRequired;
            }
            else if (usagePercent > 95.0)
            {
                status = CapacityStatus.NearCapacityWarning;
            }
            else
            {
                status = CapacityStatus.FitsComfortably;
            }
        }
        else
        {
            // DVD, Blu-ray, etc.
            if (totalSectors > mediaMaxSectors)
            {
                status = CapacityStatus.ExceedsMediaCapacity;
            }
            else if (usagePercent > 95.0)
            {
                status = CapacityStatus.NearCapacityWarning;
            }
            else
            {
                status = CapacityStatus.FitsComfortably;
            }
        }

        long totalBytes = (long)totalSectors * OpticalConstants.DataSectorSize;

        return new CapacityAssessment(
            TotalBytes: totalBytes,
            TotalSectors: totalSectors,
            MediaMaxSectors: mediaMaxSectors,
            UsagePercent: usagePercent,
            Status: status,
            TotalFileDataBytes: totalFileDataBytes,
            TotalFileDataSectors: totalFileDataSectors,
            FileSystemOverheadSectors: fileSystemOverheadSectors);
    }

    /// <summary>
    /// Calculates capacity assessment for a <see cref="CompilationDirectory"/> root tree.
    /// </summary>
    public static CapacityAssessment Calculate(
        CompilationDirectory root,
        DiscMediaType mediaType = DiscMediaType.CdR,
        bool includeIso9660 = true,
        bool includeJoliet = true,
        bool includeUdf = true,
        bool isBootable = false)
    {
        ArgumentNullException.ThrowIfNull(root);

        var allFiles = root.GetAllFilesRecursive();
        var allDirs = root.GetAllDirectoriesRecursive();

        long rawBytes = 0;
        int fileSectors = 0;
        foreach (var file in allFiles)
        {
            if (!file.ImportedStartLba.HasValue || file.IsPlannedLayoutLba)
            {
                rawBytes += file.SizeBytes;
                fileSectors += CalculateFileSectors(file.SizeBytes);
            }
        }

        int overheadSectors = CalculateFileSystemOverheadSectors(
            allDirs.Count,
            includeIso9660,
            includeJoliet,
            includeUdf,
            isBootable);

        int totalSectors = fileSectors + overheadSectors;

        return AssessCapacity(
            totalSectors: totalSectors,
            mediaType: mediaType,
            mediaMaxSectors: 0,
            totalFileDataBytes: rawBytes,
            totalFileDataSectors: fileSectors,
            fileSystemOverheadSectors: overheadSectors);
    }

    /// <summary>
    /// Calculates capacity assessment for an enumerated collection of compilation files.
    /// </summary>
    public static CapacityAssessment Calculate(
        IEnumerable<CompilationFile> files,
        int directoryCount = 1,
        DiscMediaType mediaType = DiscMediaType.CdR,
        bool includeIso9660 = true,
        bool includeJoliet = true,
        bool includeUdf = true,
        bool isBootable = false)
    {
        ArgumentNullException.ThrowIfNull(files);

        var fileList = files as IReadOnlyList<CompilationFile> ?? files.ToList();

        long rawBytes = 0;
        int fileSectors = 0;
        foreach (var file in fileList)
        {
            if (!file.ImportedStartLba.HasValue || file.IsPlannedLayoutLba)
            {
                rawBytes += file.SizeBytes;
                fileSectors += CalculateFileSectors(file.SizeBytes);
            }
        }

        int overheadSectors = CalculateFileSystemOverheadSectors(
            Math.Max(1, directoryCount),
            includeIso9660,
            includeJoliet,
            includeUdf,
            isBootable);

        int totalSectors = fileSectors + overheadSectors;

        return AssessCapacity(
            totalSectors: totalSectors,
            mediaType: mediaType,
            mediaMaxSectors: 0,
            totalFileDataBytes: rawBytes,
            totalFileDataSectors: fileSectors,
            fileSystemOverheadSectors: overheadSectors);
    }

    /// <summary>
    /// Calculates capacity assessment from explicit sector counts.
    /// </summary>
    public static CapacityAssessment Calculate(int totalSectors, DiscMediaType mediaType = DiscMediaType.CdR)
    {
        return AssessCapacity(
            totalSectors: totalSectors,
            mediaType: mediaType,
            mediaMaxSectors: 0,
            totalFileDataBytes: (long)totalSectors * OpticalConstants.DataSectorSize,
            totalFileDataSectors: totalSectors,
            fileSystemOverheadSectors: 0);
    }
}
