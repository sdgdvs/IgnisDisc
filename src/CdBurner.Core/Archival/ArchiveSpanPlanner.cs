using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>
/// Plans spanning of files across multiple optical discs with reserved headroom and redundancy.
/// </summary>
public static class ArchiveSpanPlanner
{
    /// <summary>
    /// Computes usable 2048-byte sectors per disc for the specified media type and redundancy percentage.
    /// </summary>
    /// <param name="mediaType">Target optical media type.</param>
    /// <param name="redundancyPercent">PAR2 redundancy percentage (0..100).</param>
    /// <returns>Usable 2048-byte sector count per disc.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if redundancyPercent is not between 0 and 100.</exception>
    /// <exception cref="ArgumentException">Thrown if mediaType has no nominal capacity.</exception>
    public static int CalculateUsableSectors(DiscMediaType mediaType, int redundancyPercent)
    {
        if (redundancyPercent < 0 || redundancyPercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(redundancyPercent),
                redundancyPercent,
                "Redundancy percent must be between 0 and 100.");
        }

        return UsableSectorsUnder(CalculateCeilingSectors(mediaType), redundancyPercent);
    }

    /// <summary>
    /// Most sectors a disc of this type should hold in all: its nominal capacity minus a safety reserve of 2 % (at
    /// least 8 MB) for the file system and media that come up a little short.
    /// </summary>
    public static int CalculateCeilingSectors(DiscMediaType mediaType)
    {
        int nominal = mediaType.GetNominalCapacitySectors();
        if (nominal <= 0)
        {
            throw new ArgumentException($"Media type {mediaType} has no nominal sector capacity.", nameof(mediaType));
        }

        return nominal - Math.Max(4096, nominal / 50);
    }

    private static int UsableSectorsUnder(int ceilingSectors, int redundancyPercent) =>
        (int)(ceilingSectors / (1.0 + redundancyPercent / 100.0));

    /// <summary>
    /// Recursively enumerates a source folder and creates an archival spanning plan.
    /// </summary>
    /// <param name="sourceFolder">Path to the directory containing files to archive.</param>
    /// <param name="mediaType">Target optical media type.</param>
    /// <param name="redundancyPercent">PAR2 redundancy percentage (0..100).</param>
    /// <param name="setId">Optional archive set identifier. Generated if null.</param>
    /// <returns>The calculated spanning plan.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown if sourceFolder does not exist.</exception>
    public static ArchivePlan Plan(
        string sourceFolder,
        DiscMediaType mediaType,
        int redundancyPercent,
        string? setId = null,
        bool fillFreeSpace = true,
        int? ceilingSectorsOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException($"Source folder not found: '{sourceFolder}'.");
        }

        string fullSourceDir = Path.GetFullPath(sourceFolder);
        var files = new List<ArchiveSourceFile>();
        foreach (string filePath in Directory.EnumerateFiles(fullSourceDir, "*", SearchOption.AllDirectories))
        {
            string fullPath = Path.GetFullPath(filePath);
            string relative = Path.GetRelativePath(fullSourceDir, fullPath).Replace('\\', '/');
            long size = new FileInfo(fullPath).Length;
            files.Add(new ArchiveSourceFile(fullPath, relative, size));
        }

        return Plan(files, mediaType, redundancyPercent, setId, fillFreeSpace, ceilingSectorsOverride);
    }

    /// <summary>
    /// Creates an archival spanning plan for the specified list of files.
    /// </summary>
    /// <param name="files">List of source files to plan.</param>
    /// <param name="mediaType">Target optical media type.</param>
    /// <param name="redundancyPercent">PAR2 redundancy percentage (0..100).</param>
    /// <param name="setId">Optional archive set identifier. Generated if null.</param>
    /// <returns>The calculated spanning plan.</returns>
    /// <param name="fillFreeSpace">Grow recovery data to fill each disc (redundancy is then the minimum).</param>
    /// <param name="ceilingSectorsOverride">Sectors per disc in all, instead of the media type's (tests, odd media).</param>
    public static ArchivePlan Plan(
        IReadOnlyList<ArchiveSourceFile> files,
        DiscMediaType mediaType,
        int redundancyPercent,
        string? setId = null,
        bool fillFreeSpace = true,
        int? ceilingSectorsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (redundancyPercent < 0 || redundancyPercent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(redundancyPercent), redundancyPercent, "Redundancy percent must be between 0 and 100.");
        }

        int ceilingSectors = ceilingSectorsOverride is > 0 ? ceilingSectorsOverride.Value : CalculateCeilingSectors(mediaType);
        int usableSectors = UsableSectorsUnder(ceilingSectors, redundancyPercent);
        string effectiveSetId = string.IsNullOrWhiteSpace(setId)
            ? Guid.NewGuid().ToString("N")[..12]
            : setId;

        var orderedFiles = files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        if (orderedFiles.Count == 0)
        {
            return new ArchivePlan(
                SetId: effectiveSetId,
                MediaType: mediaType,
                RedundancyPercent: redundancyPercent,
                UsableSectorsPerDisc: usableSectors,
                Discs: Array.Empty<ArchiveDiscPlan>(),
                TotalBytes: 0L,
                CeilingSectorsPerDisc: ceilingSectors,
                FillFreeSpace: fillFreeSpace);
        }

        var discs = new List<ArchiveDiscPlan>();
        var currentDiscFiles = new List<ArchiveSourceFile>();
        long currentDiscBytes = 0;
        int currentDiscSectors = 0;

        foreach (var file in orderedFiles)
        {
            int fileSectors = CompilationCapacityCalculator.CalculateFileSectors(file.SizeBytes);
            if (fileSectors > usableSectors)
            {
                // Report the true count: fileSectors is capped at int.MaxValue for sizes of 4 TiB and more.
                long requiredSectors = file.SizeBytes / OpticalConstants.DataSectorSize +
                                       (file.SizeBytes % OpticalConstants.DataSectorSize != 0 ? 1 : 0);
                throw new InvalidOperationException(
                    $"File '{file.RelativePath}' requires {requiredSectors} sectors, which is larger than one disc usable capacity ({usableSectors} sectors).");
            }

            if (currentDiscFiles.Count > 0 && (currentDiscSectors + fileSectors > usableSectors))
            {
                discs.Add(new ArchiveDiscPlan(
                    DiscNumber: discs.Count + 1,
                    Files: currentDiscFiles,
                    DataBytes: currentDiscBytes,
                    DataSectors: currentDiscSectors));

                currentDiscFiles = new List<ArchiveSourceFile>();
                currentDiscBytes = 0;
                currentDiscSectors = 0;
            }

            currentDiscFiles.Add(file);
            currentDiscBytes += file.SizeBytes;
            currentDiscSectors += fileSectors;
        }

        if (currentDiscFiles.Count > 0)
        {
            discs.Add(new ArchiveDiscPlan(
                DiscNumber: discs.Count + 1,
                Files: currentDiscFiles,
                DataBytes: currentDiscBytes,
                DataSectors: currentDiscSectors));
        }

        long totalBytes = discs.Sum(d => d.DataBytes);
        return new ArchivePlan(
            SetId: effectiveSetId,
            MediaType: mediaType,
            RedundancyPercent: redundancyPercent,
            UsableSectorsPerDisc: usableSectors,
            Discs: discs,
            TotalBytes: totalBytes,
            CeilingSectorsPerDisc: ceilingSectors,
            FillFreeSpace: fillFreeSpace);
    }
}
