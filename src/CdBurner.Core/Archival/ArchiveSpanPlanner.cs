using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>
/// Plans spanning of files across multiple optical discs with reserved headroom and redundancy.
/// </summary>
public static class ArchiveSpanPlanner
{
    /// <summary>
    /// Most non-empty files on one disc when recovery data is made: par2 protects at most 32,768 files per set, and
    /// the staged disc adds a few files of its own (manifest, index, verify scripts, readme), so a few places stay free.
    /// </summary>
    public const int MaxNonEmptyFilesPerDisc = Par2RecoverySizer.MaxRecoveryBlocks - 8;

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
        int? ceilingSectorsOverride = null,
        ArchivePackingMode packing = ArchivePackingMode.FirstFitDecreasing)
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

        return Plan(files, mediaType, redundancyPercent, setId, fillFreeSpace, ceilingSectorsOverride, packing);
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
    /// <param name="packing">How files are placed on discs; see <see cref="ArchivePackingMode"/>.</param>
    public static ArchivePlan Plan(
        IReadOnlyList<ArchiveSourceFile> files,
        DiscMediaType mediaType,
        int redundancyPercent,
        string? setId = null,
        bool fillFreeSpace = true,
        int? ceilingSectorsOverride = null,
        ArchivePackingMode packing = ArchivePackingMode.FirstFitDecreasing)
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

        foreach (var file in orderedFiles)
        {
            string leaf = file.RelativePath.Replace('\\', '/');
            if (string.Equals(leaf, ArchiveCatalog.CatalogFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(leaf, ArchiveCatalog.HtmlIndexFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The source folder holds a file named '{file.RelativePath}' in its top level, a name every archive disc uses for its own " +
                    "catalog. Rename or move that file.");
            }

            int fileSectors = CompilationCapacityCalculator.CalculateFileSectors(file.SizeBytes);
            if (fileSectors > usableSectors)
            {
                // Report the true count: fileSectors is capped at int.MaxValue for sizes of 4 TiB and more.
                long requiredSectors = file.SizeBytes / OpticalConstants.DataSectorSize +
                                       (file.SizeBytes % OpticalConstants.DataSectorSize != 0 ? 1 : 0);
                throw new InvalidOperationException(
                    $"File '{file.RelativePath}' requires {requiredSectors} sectors, which is larger than one disc usable capacity ({usableSectors} sectors).");
            }
        }

        // Every disc also carries CATALOG.JSON and INDEX.HTML, which list the whole set: keep room for them.
        long packCapacity = usableSectors - ArchiveCatalog.EstimateSectors(orderedFiles);
        int fileLimit = redundancyPercent > 0 ? MaxNonEmptyFilesPerDisc : int.MaxValue;

        List<Bin> bins = packing switch
        {
            ArchivePackingMode.PathOrder => PackNextFit(orderedFiles, packCapacity, fileLimit),
            ArchivePackingMode.KeepFolders => PackFirstFitDecreasing(BuildFolderUnits(orderedFiles, packCapacity, fileLimit), packCapacity, fileLimit),
            ArchivePackingMode.FirstFitDecreasing => PackFirstFitDecreasing(BuildFileUnits(orderedFiles), packCapacity, fileLimit),
            _ => throw new ArgumentOutOfRangeException(nameof(packing), packing, "Unknown packing mode.")
        };

        var discs = new List<ArchiveDiscPlan>(bins.Count);
        foreach (var bin in bins)
        {
            // List each disc's files in path order, whatever order they were placed in.
            var discFiles = bin.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
            discs.Add(new ArchiveDiscPlan(
                DiscNumber: discs.Count + 1,
                Files: discFiles,
                DataBytes: discFiles.Sum(f => f.SizeBytes),
                DataSectors: (int)bin.Sectors));
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
            FillFreeSpace: fillFreeSpace,
            PackingMode: packing);
    }

    /// <summary>One disc being filled.</summary>
    private sealed class Bin
    {
        public List<ArchiveSourceFile> Files { get; } = new();
        public long Sectors { get; private set; }
        public int NonEmpty { get; private set; }

        /// <summary>An empty disc takes anything (a lone file always fits, the caller checked it against the disc).</summary>
        public bool Fits(PackUnit unit, long capacity, int fileLimit) =>
            Files.Count == 0 || (Sectors + unit.Sectors <= capacity && NonEmpty + unit.NonEmpty <= fileLimit);

        public void Add(PackUnit unit)
        {
            Files.AddRange(unit.Files);
            Sectors += unit.Sectors;
            NonEmpty += unit.NonEmpty;
        }
    }

    /// <summary>Files placed together: one file, or a whole folder that fits on a disc.</summary>
    private sealed class PackUnit
    {
        public PackUnit(string key, IReadOnlyList<ArchiveSourceFile> files)
        {
            Key = key;
            Files = files;
            foreach (var f in files)
            {
                Sectors += CompilationCapacityCalculator.CalculateFileSectors(f.SizeBytes);
                if (f.SizeBytes > 0) NonEmpty++;
            }
        }

        public string Key { get; }
        public IReadOnlyList<ArchiveSourceFile> Files { get; }
        public long Sectors { get; }
        public int NonEmpty { get; }
    }

    /// <summary>The original loop: files in path order, a new disc when the next file does not fit the current one.</summary>
    private static List<Bin> PackNextFit(IReadOnlyList<ArchiveSourceFile> orderedFiles, long capacity, int fileLimit)
    {
        var bins = new List<Bin>();
        Bin? current = null;
        foreach (var file in orderedFiles)
        {
            var unit = new PackUnit(file.RelativePath, new[] { file });
            if (current == null || !current.Fits(unit, capacity, fileLimit))
            {
                current = new Bin();
                bins.Add(current);
            }

            current.Add(unit);
        }

        return bins;
    }

    /// <summary>
    /// First-Fit-Decreasing: largest unit first (ties by ordinal path), each into the first disc it fits, a new disc
    /// when none has room. The result depends only on the input, never on enumeration order.
    /// </summary>
    private static List<Bin> PackFirstFitDecreasing(IEnumerable<PackUnit> units, long capacity, int fileLimit)
    {
        var bins = new List<Bin>();
        foreach (var unit in units.OrderByDescending(u => u.Sectors).ThenBy(u => u.Key, StringComparer.Ordinal))
        {
            Bin? target = null;
            foreach (var bin in bins)
            {
                if (bin.Fits(unit, capacity, fileLimit))
                {
                    target = bin;
                    break;
                }
            }

            if (target == null)
            {
                target = new Bin();
                bins.Add(target);
            }

            target.Add(unit);
        }

        return bins;
    }

    private static List<PackUnit> BuildFileUnits(IReadOnlyList<ArchiveSourceFile> files) =>
        files.Select(f => new PackUnit(f.RelativePath, new[] { f })).ToList();

    /// <summary>
    /// Folder units: a folder with everything under it that fits on one disc is placed whole; a folder too big for
    /// one disc is opened, its own files placed one by one and each subfolder tried the same way.
    /// </summary>
    private static List<PackUnit> BuildFolderUnits(IReadOnlyList<ArchiveSourceFile> files, long capacity, int fileLimit)
    {
        var units = new List<PackUnit>();
        var items = files.Select(f => (File: f, Parts: f.RelativePath.Replace('\\', '/').Split('/'))).ToList();
        CollectFolderUnits(items, 0, string.Empty, capacity, fileLimit, units);
        return units;
    }

    private static void CollectFolderUnits(
        List<(ArchiveSourceFile File, string[] Parts)> items, int depth, string prefix, long capacity, int fileLimit, List<PackUnit> units)
    {
        var whole = new PackUnit(prefix, items.Select(i => i.File).ToList());
        if (whole.Sectors <= capacity && whole.NonEmpty <= fileLimit)
        {
            units.Add(whole);
            return;
        }

        foreach (var direct in items.Where(i => i.Parts.Length == depth + 1))
        {
            units.Add(new PackUnit(direct.File.RelativePath, new[] { direct.File }));
        }

        var subfolders = items.Where(i => i.Parts.Length > depth + 1)
            .GroupBy(i => i.Parts[depth], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in subfolders)
        {
            string path = prefix.Length == 0 ? group.Key : prefix + "/" + group.Key;
            CollectFolderUnits(group.ToList(), depth + 1, path, capacity, fileLimit, units);
        }
    }
}
