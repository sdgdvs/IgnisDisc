using System.Text.RegularExpressions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Represents the placement and physical LBA address of a file or padding gap in a DVD-Video layout.
/// </summary>
public sealed class DvdFilePlacement
{
    /// <summary>
    /// File name or virtual padding identifier (e.g. "VIDEO_TS.IFO" or "[PADDING_VIDEO_TS]").
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Starting Logical Block Address on optical disc media.
    /// </summary>
    public LbaAddress StartLba { get; }

    /// <summary>
    /// Number of 2048-byte sectors occupied by this placement.
    /// </summary>
    public int SectorCount { get; }

    /// <summary>
    /// Ending LBA (exclusive). Next sector starts here.
    /// </summary>
    public LbaAddress EndLba => StartLba + SectorCount;

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long SizeBytes { get; }

    /// <summary>
    /// Whether this placement represents a 32 KB ECC alignment padding block.
    /// </summary>
    public bool IsPadding { get; }

    /// <summary>
    /// Underlying compilation file, or <c>null</c> if in-memory or padding.
    /// </summary>
    public CompilationFile? SourceFile { get; }

    public DvdFilePlacement(
        string fileName,
        LbaAddress startLba,
        int sectorCount,
        long sizeBytes,
        bool isPadding = false,
        CompilationFile? sourceFile = null)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        StartLba = startLba;
        SectorCount = sectorCount;
        SizeBytes = sizeBytes;
        IsPadding = isPadding;
        SourceFile = sourceFile;
    }

    public override string ToString() =>
        $"{(IsPadding ? "[PAD]" : "[FILE]")} {FileName,-20} LBA: {StartLba.Value,8}..{EndLba.Value - 1,8} ({SectorCount,5} sec, {SizeBytes,10:N0} B)";
}

/// <summary>
/// Planned DVD-Video optical disc layout containing sorted file placements,
/// start LBAs, and 32 KB ECC padding blocks.
/// </summary>
public sealed class DvdVideoLayout
{
    /// <summary>
    /// All placements (files and padding gaps) in canonical DVD-Video order.
    /// </summary>
    public IReadOnlyList<DvdFilePlacement> Placements { get; }

    /// <summary>
    /// Only actual file placements (excluding padding blocks).
    /// </summary>
    public IReadOnlyList<DvdFilePlacement> FilePlacements =>
        Placements.Where(p => !p.IsPadding).ToList();

    /// <summary>
    /// Only padding placements.
    /// </summary>
    public IReadOnlyList<DvdFilePlacement> PaddingPlacements =>
        Placements.Where(p => p.IsPadding).ToList();

    /// <summary>
    /// Starting LBA of the first DVD-Video file in the layout.
    /// </summary>
    public LbaAddress StartLba { get; }

    /// <summary>
    /// Ending LBA of the entire DVD-Video layout.
    /// </summary>
    public LbaAddress EndLba { get; }

    /// <summary>
    /// Total number of sectors occupied by all DVD-Video files and padding.
    /// </summary>
    public int TotalSectors => EndLba.Value - StartLba.Value;

    /// <summary>
    /// Total virtual padding sectors inserted between IFO and BUP files.
    /// </summary>
    public int TotalPaddingSectors => PaddingPlacements.Sum(p => p.SectorCount);

    public DvdVideoLayout(IReadOnlyList<DvdFilePlacement> placements, LbaAddress startLba, LbaAddress endLba)
    {
        Placements = placements ?? throw new ArgumentNullException(nameof(placements));
        StartLba = startLba;
        EndLba = endLba;
    }

    /// <summary>
    /// Finds a file placement by file name (case-insensitive).
    /// </summary>
    public DvdFilePlacement? GetPlacement(string fileName)
    {
        return Placements.FirstOrDefault(p =>
            string.Equals(p.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Computes the sector distance between the end of an IFO file and the start of its corresponding BUP file:
    /// <c>StartLba(BUP) - EndLba(IFO)</c>.
    /// </summary>
    public int GetInterveningSectors(string ifoFileName, string bupFileName)
    {
        var ifo = GetPlacement(ifoFileName);
        var bup = GetPlacement(bupFileName);

        if (ifo == null || bup == null)
        {
            throw new ArgumentException($"Both '{ifoFileName}' and '{bupFileName}' must exist in the layout.");
        }

        return bup.StartLba.Value - ifo.EndLba.Value;
    }

    /// <summary>
    /// Verifies that all IFO and BUP pairs are separated by at least 16 sectors (32 KB).
    /// </summary>
    public bool VerifyIfoBupSeparation(out string? failureReason)
    {
        // 1. Check VIDEO_TS.IFO and VIDEO_TS.BUP
        var vmgIfo = GetPlacement("VIDEO_TS.IFO");
        var vmgBup = GetPlacement("VIDEO_TS.BUP");
        if (vmgIfo != null && vmgBup != null)
        {
            int diff = vmgBup.StartLba.Value - vmgIfo.EndLba.Value;
            if (diff < OpticalConstants.DvdEccBlockSectors)
            {
                failureReason = $"VIDEO_TS.BUP (LBA {vmgBup.StartLba.Value}) is only {diff} sectors after VIDEO_TS.IFO end (LBA {vmgIfo.EndLba.Value}). Minimum required: {OpticalConstants.DvdEccBlockSectors}.";
                return false;
            }
        }

        // 2. Check all VTS_xx_0.IFO and VTS_xx_0.BUP pairs
        var ifoFiles = Placements.Where(p => !p.IsPadding && p.FileName.EndsWith("_0.IFO", StringComparison.OrdinalIgnoreCase));
        foreach (var ifo in ifoFiles)
        {
            string bupName = ifo.FileName[..^4] + ".BUP";
            var bup = GetPlacement(bupName);
            if (bup != null)
            {
                int diff = bup.StartLba.Value - ifo.EndLba.Value;
                if (diff < OpticalConstants.DvdEccBlockSectors)
                {
                    failureReason = $"{bup.FileName} (LBA {bup.StartLba.Value}) is only {diff} sectors after {ifo.FileName} end (LBA {ifo.EndLba.Value}). Minimum required: {OpticalConstants.DvdEccBlockSectors}.";
                    return false;
                }
            }
        }

        failureReason = null;
        return true;
    }
}

/// <summary>
/// Sorts VIDEO_TS files into canonical DVD-Video playback order and calculates exact start LBAs,
/// enforcing a minimum 16-sector (32 KB) separation between every .IFO file and its backup .BUP file
/// to guarantee they never share the same physical DVD Error Correction Code (ECC) block.
/// </summary>
public class DvdVideoLayoutPlanner
{
    private static readonly Regex VtsRegex = new(@"^VTS_(?<set>\d{2})_(?<index>\d)\.(?<ext>IFO|BUP|VOB)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Plans the DVD-Video file layout for a <see cref="DataCompilation"/>.
    /// </summary>
    public DvdVideoLayout Plan(DataCompilation compilation, int startLba = 0)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var videoTsDir = compilation.Root.FindDirectoryByVirtualPath("/VIDEO_TS")
                         ?? compilation.Root.Children.OfType<CompilationDirectory>()
                             .FirstOrDefault(d => string.Equals(d.Name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                         ?? compilation.Root;

        return Plan(videoTsDir, startLba);
    }

    /// <summary>
    /// Plans the DVD-Video file layout for a <see cref="CompilationDirectory"/>.
    /// </summary>
    public DvdVideoLayout Plan(CompilationDirectory videoTsDir, int startLba = 0)
    {
        ArgumentNullException.ThrowIfNull(videoTsDir);

        var files = videoTsDir.Children
            .OfType<CompilationFile>()
            .ToList();

        return Plan(files, startLba);
    }

    /// <summary>
    /// Plans the DVD-Video file layout for an enumerable of <see cref="CompilationFile"/> instances.
    /// </summary>
    public DvdVideoLayout Plan(IEnumerable<CompilationFile> files, int startLba = 0)
    {
        ArgumentNullException.ThrowIfNull(files);

        var items = files.Select(f => new LayoutSourceItem(
            f.Name,
            f.SizeBytes,
            OpticalConstants.BytesToSectors(f.SizeBytes),
            f)).ToList();

        return PlanInternal(items, startLba);
    }

    /// <summary>
    /// Plans the DVD-Video file layout for an enumerable of <see cref="IDvdVideoFileEntry"/> instances.
    /// </summary>
    public DvdVideoLayout Plan(IEnumerable<IDvdVideoFileEntry> files, int startLba = 0)
    {
        ArgumentNullException.ThrowIfNull(files);

        var items = files.Select(f => new LayoutSourceItem(
            f.Name,
            f.SizeBytes,
            OpticalConstants.BytesToSectors(f.SizeBytes),
            null)).ToList();

        return PlanInternal(items, startLba);
    }

    /// <summary>
    /// Plans the layout and assigns <see cref="CompilationFile.ImportedStartLba"/> on every file in the directory,
    /// ensuring downstream ISO/UDF builders (like IsoUdfImageBuilder) place files at the exact calculated LBAs.
    /// </summary>
    public DvdVideoLayout ApplyToCompilation(CompilationDirectory videoTsDir, int startLba = 0)
    {
        var layout = Plan(videoTsDir, startLba);

        foreach (var placement in layout.Placements)
        {
            if (placement.SourceFile != null && !placement.IsPadding)
            {
                placement.SourceFile.ImportedStartLba = placement.StartLba.Value;
                placement.SourceFile.IsPlannedLayoutLba = true;
            }
        }

        return layout;
    }

    private static DvdVideoLayout PlanInternal(IReadOnlyList<LayoutSourceItem> sourceFiles, int initialLba)
    {
        var placements = new List<DvdFilePlacement>();
        int currentLba = initialLba;

        // Group files:
        LayoutSourceItem? vmgIfo = null;
        LayoutSourceItem? vmgVob = null;
        LayoutSourceItem? vmgBup = null;

        var titleSets = new SortedDictionary<int, TitleSetGroup>();
        var otherFiles = new List<LayoutSourceItem>();

        foreach (var file in sourceFiles)
        {
            string upper = file.Name.ToUpperInvariant();
            if (upper == "VIDEO_TS.IFO")
            {
                vmgIfo = file;
            }
            else if (upper == "VIDEO_TS.VOB")
            {
                vmgVob = file;
            }
            else if (upper == "VIDEO_TS.BUP")
            {
                vmgBup = file;
            }
            else
            {
                var match = VtsRegex.Match(upper);
                if (match.Success)
                {
                    int setNum = int.Parse(match.Groups["set"].Value);
                    int index = int.Parse(match.Groups["index"].Value);
                    string ext = match.Groups["ext"].Value;

                    if (!titleSets.TryGetValue(setNum, out var group))
                    {
                        group = new TitleSetGroup(setNum);
                        titleSets[setNum] = group;
                    }

                    if (ext == "IFO")
                    {
                        group.Ifo = file;
                    }
                    else if (ext == "BUP")
                    {
                        group.Bup = file;
                    }
                    else if (ext == "VOB")
                    {
                        if (index == 0)
                        {
                            group.MenuVob = file;
                        }
                        else
                        {
                            group.TitleVobs[index] = file;
                        }
                    }
                }
                else
                {
                    otherFiles.Add(file);
                }
            }
        }

        // --- 1. VIDEO_TS (VMG) Sequence ---
        // Canonical order: VIDEO_TS.IFO -> VIDEO_TS.VOB -> [Padding if < 16 sectors] -> VIDEO_TS.BUP
        int vmgIfoEndLba = currentLba;
        if (vmgIfo != null)
        {
            var ifoPlacement = new DvdFilePlacement(vmgIfo.Name, currentLba, vmgIfo.SectorCount, vmgIfo.SizeBytes, false, vmgIfo.SourceFile);
            placements.Add(ifoPlacement);
            currentLba = ifoPlacement.EndLba.Value;
            vmgIfoEndLba = currentLba;
        }

        int vmgInterveningVobSectors = 0;
        if (vmgVob != null)
        {
            var vobPlacement = new DvdFilePlacement(vmgVob.Name, currentLba, vmgVob.SectorCount, vmgVob.SizeBytes, false, vmgVob.SourceFile);
            placements.Add(vobPlacement);
            currentLba = vobPlacement.EndLba.Value;
            vmgInterveningVobSectors = vobPlacement.SectorCount;
        }

        // ECC Block check: If intervening VOB files total fewer than 16 sectors (32 KB),
        // insert virtual padding sectors (>= 16 sectors) to guarantee IFO and BUP never share
        // the same physical 32 KB DVD ECC block.
        if (vmgIfo != null && vmgBup != null)
        {
            if (vmgInterveningVobSectors < OpticalConstants.DvdEccBlockSectors)
            {
                int paddingSectors = OpticalConstants.DvdEccBlockSectors;
                var padPlacement = new DvdFilePlacement(
                    "[PADDING_VIDEO_TS]",
                    currentLba,
                    paddingSectors,
                    (long)paddingSectors * OpticalConstants.DataSectorSize,
                    isPadding: true);

                placements.Add(padPlacement);
                currentLba = padPlacement.EndLba.Value;
            }
        }

        if (vmgBup != null)
        {
            var bupPlacement = new DvdFilePlacement(vmgBup.Name, currentLba, vmgBup.SectorCount, vmgBup.SizeBytes, false, vmgBup.SourceFile);
            placements.Add(bupPlacement);
            currentLba = bupPlacement.EndLba.Value;
        }

        // --- 2. Title Sets Sequence (01..99) ---
        // Canonical order: VTS_xx_0.IFO -> VTS_xx_0.VOB -> VTS_xx_1..9.VOB -> [Padding if < 16 sectors] -> VTS_xx_0.BUP
        foreach (var (setNum, group) in titleSets)
        {
            int vtsIfoEndLba = currentLba;
            if (group.Ifo != null)
            {
                var ifoPlacement = new DvdFilePlacement(group.Ifo.Name, currentLba, group.Ifo.SectorCount, group.Ifo.SizeBytes, false, group.Ifo.SourceFile);
                placements.Add(ifoPlacement);
                currentLba = ifoPlacement.EndLba.Value;
                vtsIfoEndLba = currentLba;
            }

            int titleSetInterveningVobSectors = 0;

            if (group.MenuVob != null)
            {
                var menuVobPlacement = new DvdFilePlacement(group.MenuVob.Name, currentLba, group.MenuVob.SectorCount, group.MenuVob.SizeBytes, false, group.MenuVob.SourceFile);
                placements.Add(menuVobPlacement);
                currentLba = menuVobPlacement.EndLba.Value;
                titleSetInterveningVobSectors += menuVobPlacement.SectorCount;
            }

            foreach (var (titleIdx, titleVob) in group.TitleVobs)
            {
                var titleVobPlacement = new DvdFilePlacement(titleVob.Name, currentLba, titleVob.SectorCount, titleVob.SizeBytes, false, titleVob.SourceFile);
                placements.Add(titleVobPlacement);
                currentLba = titleVobPlacement.EndLba.Value;
                titleSetInterveningVobSectors += titleVobPlacement.SectorCount;
            }

            // Check ECC block separation between VTS_xx_0.IFO and VTS_xx_0.BUP
            if (group.Ifo != null && group.Bup != null)
            {
                if (titleSetInterveningVobSectors < OpticalConstants.DvdEccBlockSectors)
                {
                    int paddingSectors = OpticalConstants.DvdEccBlockSectors;
                    var padPlacement = new DvdFilePlacement(
                        $"[PADDING_VTS_{setNum:D2}]",
                        currentLba,
                        paddingSectors,
                        (long)paddingSectors * OpticalConstants.DataSectorSize,
                        isPadding: true);

                    placements.Add(padPlacement);
                    currentLba = padPlacement.EndLba.Value;
                }
            }

            if (group.Bup != null)
            {
                var bupPlacement = new DvdFilePlacement(group.Bup.Name, currentLba, group.Bup.SectorCount, group.Bup.SizeBytes, false, group.Bup.SourceFile);
                placements.Add(bupPlacement);
                currentLba = bupPlacement.EndLba.Value;
            }
        }

        // --- 3. Non-standard / Extra Files (Sorted by name) ---
        foreach (var extra in otherFiles.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var extraPlacement = new DvdFilePlacement(extra.Name, currentLba, extra.SectorCount, extra.SizeBytes, false, extra.SourceFile);
            placements.Add(extraPlacement);
            currentLba = extraPlacement.EndLba.Value;
        }

        return new DvdVideoLayout(placements, initialLba, currentLba);
    }

    private sealed record LayoutSourceItem(string Name, long SizeBytes, int SectorCount, CompilationFile? SourceFile);

    private sealed class TitleSetGroup
    {
        public int TitleSetNumber { get; }
        public LayoutSourceItem? Ifo { get; set; }
        public LayoutSourceItem? MenuVob { get; set; }
        public SortedDictionary<int, LayoutSourceItem> TitleVobs { get; } = new();
        public LayoutSourceItem? Bup { get; set; }

        public TitleSetGroup(int titleSetNumber)
        {
            TitleSetNumber = titleSetNumber;
        }
    }
}
