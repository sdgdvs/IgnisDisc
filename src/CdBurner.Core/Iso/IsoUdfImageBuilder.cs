using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Compiles an in-memory <see cref="CompilationDirectory"/> virtual filesystem hierarchy into a
/// standards-compliant optical disc filesystem image (.iso) supporting ISO 9660 (ECMA-119),
/// Microsoft Joliet (UCS-2BE), and ECMA-167 / UDF Bridge volume descriptors.
/// </summary>
public static class IsoUdfImageBuilder
{
    public const int SectorSize = OpticalConstants.DataSectorSize; // 2048 bytes

    private static readonly ushort[] Ecma167CrcTable = GenerateEcma167CrcTable();

    /// <summary>
    /// <summary>
    /// Compiles a compilation directory tree into an in-memory seekable stream containing the complete .iso image.
    /// </summary>
    /// <param name="root">The compilation root directory.</param>
    /// <param name="options">Optional ISO build options (defaults will be used if omitted).</param>
    /// <param name="sessionStartLba">Optional starting LBA for multi-session appended sessions (defaults to 0).</param>
    /// <returns>A seekable <see cref="MemoryStream"/> positioned at offset 0.</returns>
    public static MemoryStream BuildToMemoryStream(
        CompilationDirectory root,
        IsoBuildOptions? options = null,
        int sessionStartLba = 0)
    {
        var ms = new MemoryStream();
        Build(root, ms, options, sessionStartLba);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Compiles a compilation directory tree and writes the .iso image directly to an output file.
    /// </summary>
    public static void BuildToFile(
        CompilationDirectory root,
        string outputIsoPath,
        IsoBuildOptions? options = null,
        int sessionStartLba = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);

        string? dir = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Build(root, fs, options, sessionStartLba);
    }

    /// <summary>
    /// Asynchronously compiles a compilation directory tree and writes the .iso image to an output file.
    /// </summary>
    public static async Task BuildToFileAsync(
        CompilationDirectory root,
        string outputIsoPath,
        IsoBuildOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        int sessionStartLba = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);

        string? dir = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await BuildAsync(root, fs, options, progress, cancellationToken, sessionStartLba).ConfigureAwait(false);
    }

    /// <summary>
    /// Compiles a compilation directory tree and writes the .iso image into the provided destination stream.
    /// </summary>
    public static void Build(
        CompilationDirectory root,
        Stream outputStream,
        IsoBuildOptions? options = null,
        int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(outputStream);

        options ??= new IsoBuildOptions();
        var context = LayoutContext.Create(root, options, sessionStartLba);
        WriteImageSectors(context, outputStream, progress: null, cancellationToken: default, isAsync: false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously compiles a compilation directory tree and writes the .iso image into the provided destination stream.
    /// </summary>
    public static async Task BuildAsync(
        CompilationDirectory root,
        Stream outputStream,
        IsoBuildOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(outputStream);

        options ??= new IsoBuildOptions();
        var context = LayoutContext.Create(root, options, sessionStartLba);
        await WriteImageSectors(context, outputStream, progress, cancellationToken, isAsync: true).ConfigureAwait(false);
    }

    #region Layout and Serialization Engine

    private sealed class LayoutContext
    {
        public IsoBuildOptions Options { get; }
        public CompilationDirectory RootDir { get; }
        public List<DirectoryLayoutNode> BfsDirectories { get; } = new();
        public List<FileLayoutNode> AllFiles { get; } = new();

        public int PvdLba { get; set; } = 16;
        public int BootRecordLba { get; set; } = -1;
        public int JolietSvdLba { get; set; } = -1;
        public int TerminatorLba { get; set; } = -1;
        public int Bea01Lba { get; set; } = -1;
        public int NsrLba { get; set; } = -1;
        public int Tea01Lba { get; set; } = -1;
        public int AvdpLba { get; set; } = -1;

        public bool UdfEnabled { get; set; }
        public int UdfVdsLba { get; set; } = -1;
        public int UdfPartitionStartLba { get; set; } = -1;
        public int UdfFsdLba { get; set; } = -1;
        public int UdfFsdTerminatorLba { get; set; } = -1;
        public int UdfClosingAvdpLba { get; set; } = -1;

        public int BootCatalogLba { get; set; } = -1;
        public byte[]? DefaultBootImageData { get; set; }
        public int DefaultBootImageLba { get; set; } = -1;
        public int DefaultBootImageSectorCount { get; set; }
        public List<BootImageLayoutNode> AdditionalBootImages { get; } = new();

        public int IsoPathTableL_Lba { get; set; }
        public int IsoPathTableM_Lba { get; set; }
        public int IsoPathTableBytesLength { get; set; }
        public int IsoPathTableSectors { get; set; }

        public int JolietPathTableL_Lba { get; set; } = -1;
        public int JolietPathTableM_Lba { get; set; } = -1;
        public int JolietPathTableBytesLength { get; set; }
        public int JolietPathTableSectors { get; set; }

        public int TotalSectors { get; set; }

        public int SessionStartLba { get; }

        public LayoutContext(CompilationDirectory rootDir, IsoBuildOptions options, int sessionStartLba = 0)
        {
            RootDir = rootDir;
            Options = options;
            SessionStartLba = sessionStartLba;
        }

        public static LayoutContext Create(CompilationDirectory root, IsoBuildOptions options, int sessionStartLba = 0)
        {
            var ctx = new LayoutContext(root, options, sessionStartLba);

            // 1. Build hierarchy of layout nodes
            var rootLayout = BuildNodeHierarchy(root);

            // 2. Perform Breadth-First traversal to assign 1-based directory numbers
            var queue = new Queue<DirectoryLayoutNode>();
            queue.Enqueue(rootLayout);
            int dirNumber = 1;

            while (queue.Count > 0)
            {
                var dirNode = queue.Dequeue();
                dirNode.DirNumber = dirNumber++;
                dirNode.ParentDirNumber = dirNode.Parent?.DirNumber ?? 1;
                ctx.BfsDirectories.Add(dirNode);

                // Sort child directories deterministically by name
                dirNode.SubDirectories.Sort((a, b) => string.Compare(a.SourceDir.Name, b.SourceDir.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var sub in dirNode.SubDirectories)
                {
                    queue.Enqueue(sub);
                }

                // Sort files: use canonical DVD-Video ordering for VIDEO_TS directories, alphabetical otherwise
                if (string.Equals(dirNode.SourceDir.Name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                {
                    dirNode.Files.Sort((a, b) => GetDvdVideoSortKey(a.SourceFile.Name).CompareTo(GetDvdVideoSortKey(b.SourceFile.Name)));
                }
                else
                {
                    dirNode.Files.Sort((a, b) => string.Compare(a.SourceFile.Name, b.SourceFile.Name, StringComparison.OrdinalIgnoreCase));
                }

                var usedIsoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in dirNode.Files)
                {
                    int collisionIndex = 0;
                    string candidate = IsoNameSanitizer.ToIso9660Level1Name(f.SourceFile.Name, collisionIndex);
                    while (!usedIsoNames.Add(candidate))
                    {
                        collisionIndex = collisionIndex == 0 ? 2 : collisionIndex + 1;
                        candidate = IsoNameSanitizer.ToIso9660Level1Name(f.SourceFile.Name, collisionIndex);
                    }
                    f.Iso9660Name = candidate;
                    ctx.AllFiles.Add(f);
                }
            }

            // 2b. Resolve unique ISO 9660 / Joliet / UDF identifiers per directory (handles truncation collisions)
            AssignUniqueNames(ctx);

            // 3. Pre-calculate extent sector requirements for each directory
            foreach (var dir in ctx.BfsDirectories)
            {
                dir.IsoSectorCount = CalculateDirectorySectors(dir, isJoliet: false);
                if (options.IncludeJoliet)
                {
                    dir.JolietSectorCount = CalculateDirectorySectors(dir, isJoliet: true);
                }
            }

            // 4. Calculate Path Table lengths
            ctx.IsoPathTableBytesLength = CalculatePathTableBytesLength(ctx.BfsDirectories, isJoliet: false);
            ctx.IsoPathTableSectors = Math.Max(1, (ctx.IsoPathTableBytesLength + SectorSize - 1) / SectorSize);

            if (options.IncludeJoliet)
            {
                ctx.JolietPathTableBytesLength = CalculatePathTableBytesLength(ctx.BfsDirectories, isJoliet: true);
                ctx.JolietPathTableSectors = Math.Max(1, (ctx.JolietPathTableBytesLength + SectorSize - 1) / SectorSize);
            }

            // 4b. Pre-load boot images if El Torito boot is configured
            if (options.BootConfiguration != null)
            {
                var def = options.BootConfiguration.DefaultEntry;
                using (var s = def.BootImageStreamFactory())
                {
                    ctx.DefaultBootImageData = ReadStreamFully(s);
                }
                ctx.DefaultBootImageSectorCount = Math.Max(1, (ctx.DefaultBootImageData.Length + SectorSize - 1) / SectorSize);

                if (options.BootConfiguration.AdditionalSectionEntries != null)
                {
                    foreach (var sec in options.BootConfiguration.AdditionalSectionEntries)
                    {
                        byte[] data;
                        using (var s = sec.BootImageStreamFactory())
                        {
                            data = ReadStreamFully(s);
                        }
                        int secSectors = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
                        ctx.AdditionalBootImages.Add(new BootImageLayoutNode(sec, data, secSectors));
                    }
                }
            }

            // 4c. Validate file sizes. ISO 9660 / Joliet directory records store a 32-bit size; larger files are
            //     only representable through UDF (which this builder writes using multiple extents).
            const long MaxIso9660FileSize = uint.MaxValue;
            var oversized = ctx.AllFiles.FirstOrDefault(f => f.SourceFile.SizeBytes > MaxIso9660FileSize);

            // Plain UDF 1.02 places its Anchor Volume Descriptor Pointer at fixed LBA 256 and cannot describe
            // sessions starting above LBA 0. Appended sessions therefore emit ISO 9660 / Joliet descriptors only.
            ctx.UdfEnabled = options.IncludeUdf && sessionStartLba == 0;

            if (oversized != null && !ctx.UdfEnabled)
            {
                throw new NotSupportedException(
                    $"File '{oversized.SourceFile.Name}' is {oversized.SourceFile.SizeBytes:N0} bytes. Files of 4 GiB or larger require the UDF file system; enable UDF for this compilation.");
            }

            // 5. Assign LBA sectors sequentially
            int currentLba = sessionStartLba + 16;
            ctx.PvdLba = currentLba++; // sessionStartLba + 16

            if (options.BootConfiguration != null)
            {
                ctx.BootRecordLba = currentLba++;
            }

            if (options.IncludeJoliet)
            {
                ctx.JolietSvdLba = currentLba++;
            }

            ctx.TerminatorLba = currentLba++;

            if (ctx.UdfEnabled)
            {
                ctx.Bea01Lba = currentLba++;
                ctx.NsrLba = currentLba++;
                ctx.Tea01Lba = currentLba++;

                // The UDF Anchor Volume Descriptor Pointer must sit exactly at session start + 256, followed by
                // the 16-sector Volume Descriptor Sequence. If the ISO 9660 metadata would run into that region,
                // place it after the VDS instead of overlapping it.
                int isoMetadataSectors = 2 * ctx.IsoPathTableSectors
                    + ctx.BfsDirectories.Sum(d => d.IsoSectorCount)
                    + (options.IncludeJoliet ? 2 * ctx.JolietPathTableSectors + ctx.BfsDirectories.Sum(d => d.JolietSectorCount) : 0)
                    + (options.BootConfiguration != null ? 1 + ctx.DefaultBootImageSectorCount + ctx.AdditionalBootImages.Sum(b => b.SectorCount) : 0);

                ctx.AvdpLba = sessionStartLba + 256;
                ctx.UdfVdsLba = ctx.AvdpLba + 1;
                ctx.UdfPartitionStartLba = ctx.UdfVdsLba + UdfVdsSectorCount;

                if (currentLba + isoMetadataSectors > ctx.AvdpLba)
                {
                    currentLba = ctx.UdfPartitionStartLba;
                }
            }

            // Assign Path Table LBAs
            ctx.IsoPathTableL_Lba = currentLba;
            currentLba += ctx.IsoPathTableSectors;
            ctx.IsoPathTableM_Lba = currentLba;
            currentLba += ctx.IsoPathTableSectors;

            if (options.IncludeJoliet)
            {
                ctx.JolietPathTableL_Lba = currentLba;
                currentLba += ctx.JolietPathTableSectors;
                ctx.JolietPathTableM_Lba = currentLba;
                currentLba += ctx.JolietPathTableSectors;
            }

            // Assign ISO directory extent LBAs
            foreach (var dir in ctx.BfsDirectories)
            {
                dir.IsoLba = currentLba;
                currentLba += dir.IsoSectorCount;
            }

            // Assign Joliet directory extent LBAs
            if (options.IncludeJoliet)
            {
                foreach (var dir in ctx.BfsDirectories)
                {
                    dir.JolietLba = currentLba;
                    currentLba += dir.JolietSectorCount;
                }
            }

            // Assign Boot Catalog and Boot Image LBAs
            if (options.BootConfiguration != null)
            {
                ctx.BootCatalogLba = currentLba++;

                ctx.DefaultBootImageLba = currentLba;
                currentLba += ctx.DefaultBootImageSectorCount;

                foreach (var sec in ctx.AdditionalBootImages)
                {
                    sec.StartLba = currentLba;
                    currentLba += sec.SectorCount;
                }
            }

            // Reserve the UDF AVDP + VDS region and allocate UDF file-set metadata at the start of the partition
            if (ctx.UdfEnabled)
            {
                currentLba = Math.Max(currentLba, ctx.UdfPartitionStartLba);

                ctx.UdfFsdLba = currentLba++;
                ctx.UdfFsdTerminatorLba = currentLba++;

                foreach (var dir in ctx.BfsDirectories)
                {
                    dir.UdfFeLba = currentLba++;
                    dir.UdfFidBytes = CalculateUdfFidStreamLength(dir);
                    dir.UdfFidLba = currentLba;
                    currentLba += (dir.UdfFidBytes + SectorSize - 1) / SectorSize;
                }

                foreach (var file in ctx.AllFiles)
                {
                    file.UdfFeLba = currentLba++;
                }
            }

            // Assign file extents
            foreach (var file in ctx.AllFiles)
            {
                bool isImportedFromPriorSession = sessionStartLba > 0
                    && file.SourceFile.ImportedStartLba.HasValue
                    && !file.SourceFile.IsPlannedLayoutLba
                    && file.SourceFile.ImportedStartLba.Value < sessionStartLba;

                file.IsImportedFromPreviousSession = isImportedFromPriorSession;

                if (isImportedFromPriorSession)
                {
                    file.StartLba = file.SourceFile.ImportedStartLba!.Value;
                }
                else
                {
                    // Check for DVD-Video 16-sector (32 KB) ECC gap separation between IFO and BUP
                    bool isInsideVideoTs = file.ParentDirectory != null
                        && string.Equals(file.ParentDirectory.SourceDir.Name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase);

                    if (isInsideVideoTs)
                    {
                        string upperName = file.SourceFile.Name.ToUpperInvariant();
                        string? ifoName = upperName == "VIDEO_TS.BUP"
                            ? "VIDEO_TS.IFO"
                            : (upperName.EndsWith("_0.BUP", StringComparison.OrdinalIgnoreCase)
                                ? upperName[..^4] + ".IFO"
                                : null);

                        if (ifoName != null)
                        {
                            var ifoNode = ctx.AllFiles.FirstOrDefault(f =>
                                f.ParentDirectory == file.ParentDirectory &&
                                string.Equals(f.SourceFile.Name, ifoName, StringComparison.OrdinalIgnoreCase));

                            if (ifoNode != null)
                            {
                                int ifoEndLba = ifoNode.StartLba + ifoNode.SectorCount;
                                if (currentLba - ifoEndLba < OpticalConstants.DvdEccBlockSectors)
                                {
                                    currentLba = ifoEndLba + OpticalConstants.DvdEccBlockSectors;
                                }
                            }
                        }
                    }

                    file.StartLba = currentLba;
                    if (file.SourceFile.ImportedStartLba.HasValue)
                    {
                        file.SourceFile.ImportedStartLba = file.StartLba;
                    }
                    currentLba += file.SectorCount;
                }
            }

            int total = currentLba;
            if (ctx.UdfEnabled)
            {
                // Second AVDP at the last sector of the volume (UDF 1.02 §2.2.3 requires at least two anchors).
                ctx.UdfClosingAvdpLba = total;
                total++;
            }

            ctx.TotalSectors = total;
            return ctx;
        }

        private static void AssignUniqueNames(LayoutContext ctx)
        {
            foreach (var dir in ctx.BfsDirectories)
            {
                var isoNames = new HashSet<string>(dir.Files.Select(f => f.Iso9660Name), StringComparer.OrdinalIgnoreCase);
                var jolietNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var udfNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var sub in dir.SubDirectories)
                {
                    sub.IsoName = MakeUnique(IsoNameSanitizer.ToIso9660DirectoryName(sub.SourceDir.Name), isoNames, 31, keepExtension: false);
                    sub.JolietName = MakeUnique(IsoNameSanitizer.ToJolietName(sub.SourceDir.Name), jolietNames, 64, keepExtension: false);
                    sub.UdfName = MakeUnique(TruncateForUdf(sub.SourceDir.Name), udfNames, MaxUdfNameChars(sub.SourceDir.Name), keepExtension: false);
                }

                foreach (var file in dir.Files)
                {
                    file.JolietName = MakeUnique(IsoNameSanitizer.ToJolietName(file.SourceFile.Name), jolietNames, 64, keepExtension: true);
                    file.UdfName = MakeUnique(TruncateForUdf(file.SourceFile.Name), udfNames, MaxUdfNameChars(file.SourceFile.Name), keepExtension: true);
                }
            }
        }

        private static int MaxUdfNameChars(string name) => name.Any(c => c > 0xFF) ? 127 : 254;

        private static string TruncateForUdf(string name)
        {
            int max = MaxUdfNameChars(name);
            if (name.Length <= max)
            {
                return name;
            }

            string ext = Path.GetExtension(name);
            if (ext.Length >= max)
            {
                ext = string.Empty;
            }
            return TruncateUtf16Safe(name, max - ext.Length) + ext;
        }

        /// <summary>
        /// Returns <paramref name="candidate"/> or a "~N" disambiguated variant that is not yet in <paramref name="used"/>,
        /// keeping the result within <paramref name="maxLength"/> characters, and records it as used.
        /// </summary>
        private static string MakeUnique(string candidate, HashSet<string> used, int maxLength, bool keepExtension)
        {
            if (used.Add(candidate))
            {
                return candidate;
            }

            // ISO 9660 identifiers may carry a ";1" version suffix; keep it outside the disambiguation.
            string version = string.Empty;
            int semi = candidate.LastIndexOf(';');
            if (semi > 0)
            {
                version = candidate[semi..];
                candidate = candidate[..semi];
            }

            string ext = keepExtension ? Path.GetExtension(candidate) : string.Empty;
            string stem = candidate[..(candidate.Length - ext.Length)];

            for (int n = 1; n < 100000; n++)
            {
                string suffix = "~" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                int room = Math.Max(1, maxLength - version.Length - ext.Length - suffix.Length);
                string attempt = (stem.Length > room ? TruncateUtf16Safe(stem, room) : stem) + suffix + ext + version;
                if (used.Add(attempt))
                {
                    return attempt;
                }
            }

            throw new InvalidOperationException($"Unable to generate a unique name for '{candidate}'.");
        }

        private static string TruncateUtf16Safe(string value, int maxLength)
        {
            if (value.Length <= maxLength)
            {
                return value;
            }
            if (maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]))
            {
                maxLength--;
            }
            return value[..maxLength];
        }

        private static int CalculateUdfFidStreamLength(DirectoryLayoutNode dir)
        {
            int total = UdfFidLength(0); // parent entry
            foreach (var sub in dir.SubDirectories)
            {
                total += UdfFidLength(EncodeOstaCompressedUnicode(sub.UdfName, 255).Length);
            }
            foreach (var file in dir.Files)
            {
                total += UdfFidLength(EncodeOstaCompressedUnicode(file.UdfName, 255).Length);
            }
            return total;
        }


        private static DirectoryLayoutNode BuildNodeHierarchy(CompilationDirectory dir, DirectoryLayoutNode? parent = null)
        {
            var dirNode = new DirectoryLayoutNode(dir) { Parent = parent };

            foreach (var child in dir.Children)
            {
                if (child is CompilationDirectory subDir)
                {
                    var subNode = BuildNodeHierarchy(subDir, dirNode);
                    dirNode.SubDirectories.Add(subNode);
                }
                else if (child is CompilationFile file)
                {
                    dirNode.Files.Add(new FileLayoutNode(file, dirNode));
                }
            }

            return dirNode;
        }

        private static int CalculateDirectorySectors(DirectoryLayoutNode dir, bool isJoliet)
        {
            var recordLengths = new List<int>
            {
                34, // '.' entry
                34  // '..' entry
            };

            foreach (var sub in dir.SubDirectories)
            {
                string name = isJoliet ? sub.JolietName : sub.IsoName;

                int identLen = isJoliet
                    ? Encoding.BigEndianUnicode.GetByteCount(name)
                    : Encoding.ASCII.GetByteCount(name);

                recordLengths.Add(GetDirectoryRecordLength(identLen));
            }

            foreach (var file in dir.Files)
            {
                string name = isJoliet
                    ? file.JolietName
                    : (!string.IsNullOrEmpty(file.Iso9660Name)
                        ? file.Iso9660Name
                        : IsoNameSanitizer.ToIso9660Level1Name(file.SourceFile.Name));

                int identLen = isJoliet
                    ? Encoding.BigEndianUnicode.GetByteCount(name)
                    : Encoding.ASCII.GetByteCount(name);

                recordLengths.Add(GetDirectoryRecordLength(identLen));
            }

            int sectors = 1;
            int offset = 0;
            foreach (int len in recordLengths)
            {
                if (offset + len > SectorSize)
                {
                    sectors++;
                    offset = 0;
                }
                offset += len;
            }

            return sectors;
        }

        private static int CalculatePathTableBytesLength(List<DirectoryLayoutNode> directories, bool isJoliet)
        {
            int total = 10; // Root directory entry: 1 + 1 + 4 + 2 + 1 + 1 pad = 10 bytes

            for (int i = 1; i < directories.Count; i++)
            {
                var dir = directories[i];
                string name = isJoliet ? dir.JolietName : dir.IsoName;

                int identLen = isJoliet
                    ? Encoding.BigEndianUnicode.GetByteCount(name)
                    : Encoding.ASCII.GetByteCount(name);

                int entryLen = 8 + identLen;
                if (identLen % 2 != 0)
                {
                    entryLen++; // 1 byte padding
                }

                total += entryLen;
            }

            return total;
        }
    }

    private sealed class DirectoryLayoutNode
    {
        public CompilationDirectory SourceDir { get; }
        public DirectoryLayoutNode? Parent { get; set; }
        public int DirNumber { get; set; }
        public int ParentDirNumber { get; set; }

        public List<DirectoryLayoutNode> SubDirectories { get; } = new();
        public List<FileLayoutNode> Files { get; } = new();

        public int IsoLba { get; set; }
        public int IsoSectorCount { get; set; }
        public uint IsoExtentSizeBytes => (uint)(IsoSectorCount * SectorSize);

        public int JolietLba { get; set; }
        public int JolietSectorCount { get; set; }
        public uint JolietExtentSizeBytes => (uint)(JolietSectorCount * SectorSize);

        public string IsoName { get; set; } = string.Empty;
        public string JolietName { get; set; } = string.Empty;
        public string UdfName { get; set; } = string.Empty;
        public int UdfFeLba { get; set; } = -1;
        public int UdfFidLba { get; set; } = -1;
        public int UdfFidBytes { get; set; }

        public DirectoryLayoutNode(CompilationDirectory sourceDir)
        {
            SourceDir = sourceDir;
        }
    }

    private sealed class FileLayoutNode
    {
        public CompilationFile SourceFile { get; }
        public DirectoryLayoutNode? ParentDirectory { get; }
        public int StartLba { get; set; }
        public int SectorCount { get; set; }
        public bool IsImportedFromPreviousSession { get; set; }
        public string Iso9660Name { get; set; } = string.Empty;
        public string JolietName { get; set; } = string.Empty;
        public string UdfName { get; set; } = string.Empty;
        public int UdfFeLba { get; set; } = -1;

        public FileLayoutNode(CompilationFile sourceFile, DirectoryLayoutNode? parentDirectory = null)
        {
            SourceFile = sourceFile;
            ParentDirectory = parentDirectory;
            SectorCount = sourceFile.RequiredSectors;
        }
    }

    private sealed class BootImageLayoutNode
    {
        public BootCatalogEntrySpec Spec { get; }
        public byte[] Data { get; }
        public int StartLba { get; set; }
        public int SectorCount { get; }

        public BootImageLayoutNode(BootCatalogEntrySpec spec, byte[] data, int sectorCount)
        {
            Spec = spec;
            Data = data;
            SectorCount = sectorCount;
        }
    }

    private static byte[] ReadStreamFully(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var seg))
        {
            return seg.ToArray();
        }
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static async Task WriteImageSectors(
        LayoutContext ctx,
        Stream stream,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        bool isAsync)
    {
        byte[] zeroSector = new byte[SectorSize];

        // Collect every metadata structure keyed by its absolute LBA, then emit everything (metadata and file
        // payloads) strictly in LBA order with zero fill between extents. This keeps the writer correct no matter
        // how the layout engine orders regions (e.g. ISO metadata placed after the UDF VDS on large trees).
        var metadata = new List<(int Lba, byte[] Data)>();

        metadata.Add((ctx.PvdLba, BuildPrimaryVolumeDescriptor(ctx)));

        if (ctx.Options.BootConfiguration != null)
        {
            metadata.Add((ctx.BootRecordLba, ElToritoCatalogBuilder.BuildBootRecordVolumeDescriptor((uint)ctx.BootCatalogLba)));
        }

        if (ctx.Options.IncludeJoliet)
        {
            metadata.Add((ctx.JolietSvdLba, BuildJolietVolumeDescriptor(ctx)));
        }

        metadata.Add((ctx.TerminatorLba, BuildVolumeDescriptorTerminator()));

        if (ctx.UdfEnabled)
        {
            metadata.Add((ctx.Bea01Lba, BuildUdfExtendedAreaDescriptor("BEA01")));
            metadata.Add((ctx.NsrLba, BuildUdfExtendedAreaDescriptor("NSR02")));
            metadata.Add((ctx.Tea01Lba, BuildUdfExtendedAreaDescriptor("TEA01")));
        }

        metadata.Add((ctx.IsoPathTableL_Lba, BuildPathTable(ctx.BfsDirectories, ctx.IsoPathTableSectors, isJoliet: false, isBigEndian: false)));
        metadata.Add((ctx.IsoPathTableM_Lba, BuildPathTable(ctx.BfsDirectories, ctx.IsoPathTableSectors, isJoliet: false, isBigEndian: true)));

        if (ctx.Options.IncludeJoliet)
        {
            metadata.Add((ctx.JolietPathTableL_Lba, BuildPathTable(ctx.BfsDirectories, ctx.JolietPathTableSectors, isJoliet: true, isBigEndian: false)));
            metadata.Add((ctx.JolietPathTableM_Lba, BuildPathTable(ctx.BfsDirectories, ctx.JolietPathTableSectors, isJoliet: true, isBigEndian: true)));
        }

        foreach (var dir in ctx.BfsDirectories)
        {
            metadata.Add((dir.IsoLba, BuildDirectoryExtent(dir, isJoliet: false)));
            if (ctx.Options.IncludeJoliet)
            {
                metadata.Add((dir.JolietLba, BuildDirectoryExtent(dir, isJoliet: true)));
            }
        }

        if (ctx.Options.BootConfiguration != null)
        {
            var addLoadRbas = ctx.AdditionalBootImages.Select(x => (uint)x.StartLba).ToList();
            metadata.Add((ctx.BootCatalogLba, ElToritoCatalogBuilder.BuildBootCatalogSector(
                ctx.Options.BootConfiguration,
                (uint)ctx.DefaultBootImageLba,
                addLoadRbas)));
            metadata.Add((ctx.DefaultBootImageLba, PadToSectors(ctx.DefaultBootImageData!, ctx.DefaultBootImageSectorCount)));
            foreach (var sec in ctx.AdditionalBootImages)
            {
                metadata.Add((sec.StartLba, PadToSectors(sec.Data, sec.SectorCount)));
            }
        }

        if (ctx.UdfEnabled)
        {
            AddUdfStructures(ctx, metadata);
        }

        // Merge metadata and file payloads into a single LBA-ordered emission plan.
        var plan = new List<(int Lba, byte[]? Data, FileLayoutNode? File)>(metadata.Count + ctx.AllFiles.Count);
        plan.AddRange(metadata.Select(m => (m.Lba, (byte[]?)m.Data, (FileLayoutNode?)null)));
        plan.AddRange(ctx.AllFiles
            .Where(f => !f.IsImportedFromPreviousSession && f.SectorCount > 0)
            .Select(f => (f.StartLba, (byte[]?)null, (FileLayoutNode?)f)));
        plan.Sort((a, b) => a.Lba.CompareTo(b.Lba));

        // 1. System Area: 16 zero sectors at the start of the session
        for (int i = 0; i < 16; i++)
        {
            await WriteSector(stream, zeroSector, isAsync, cancellationToken).ConfigureAwait(false);
        }

        int writtenLba = ctx.SessionStartLba + 16;

        foreach (var item in plan)
        {
            if (item.Lba < writtenLba)
            {
                throw new InvalidOperationException(
                    $"Image layout error: structure at LBA {item.Lba} overlaps data already written up to LBA {writtenLba}.");
            }

            while (writtenLba < item.Lba)
            {
                await WriteSector(stream, zeroSector, isAsync, cancellationToken).ConfigureAwait(false);
                writtenLba++;
            }

            if (item.Data != null)
            {
                await WriteBuffer(stream, item.Data, isAsync, cancellationToken).ConfigureAwait(false);
                writtenLba += item.Data.Length / SectorSize;
            }
            else
            {
                await WriteFilePayload(stream, item.File!, zeroSector, isAsync, cancellationToken).ConfigureAwait(false);
                writtenLba += item.File!.SectorCount;

                if (progress != null && ctx.TotalSectors > 0)
                {
                    progress.Report(Math.Min(1.0, (double)writtenLba / ctx.TotalSectors));
                }
            }
        }

        // Pad any remaining sectors to exact total volume size
        while (writtenLba < ctx.TotalSectors)
        {
            await WriteSector(stream, zeroSector, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba++;
        }

        if (isAsync)
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            stream.Flush();
        }

        progress?.Report(1.0);
    }

    private static byte[] PadToSectors(byte[] data, int sectorCount)
    {
        byte[] padded = new byte[sectorCount * SectorSize];
        data.AsSpan(0, Math.Min(data.Length, padded.Length)).CopyTo(padded);
        return padded;
    }

    private static async Task WriteFilePayload(
        Stream stream,
        FileLayoutNode file,
        byte[] zeroSector,
        bool isAsync,
        CancellationToken cancellationToken)
    {
        long expected = file.SourceFile.SizeBytes;
        long remaining = expected;

        if (remaining > 0)
        {
            using var fileStream = file.SourceFile.OpenRead();
            byte[] transferBuf = new byte[65536];

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(remaining, transferBuf.Length);
                int read = isAsync
                    ? await fileStream.ReadAsync(transferBuf.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false)
                    : fileStream.Read(transferBuf, 0, toRead);

                if (read == 0)
                {
                    throw new IOException(
                        $"Source file '{file.SourceFile.Name}' ended after {expected - remaining:N0} of {expected:N0} bytes. It may have been modified after it was added to the compilation.");
                }

                if (isAsync)
                {
                    await stream.WriteAsync(transferBuf.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    stream.Write(transferBuf, 0, read);
                }

                remaining -= read;
            }
        }

        // Sector boundary padding up to the allocated sector count
        long allocatedBytes = (long)file.SectorCount * SectorSize;
        long pad = allocatedBytes - expected;
        while (pad > 0)
        {
            int chunk = (int)Math.Min(pad, zeroSector.Length);
            if (isAsync)
            {
                await stream.WriteAsync(zeroSector.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                stream.Write(zeroSector, 0, chunk);
            }
            pad -= chunk;
        }
    }

    private static async Task WriteSector(Stream stream, byte[] sector, bool isAsync, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isAsync)
        {
            await stream.WriteAsync(sector.AsMemory(0, SectorSize), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            stream.Write(sector, 0, SectorSize);
        }
    }

    private static async Task WriteBuffer(Stream stream, byte[] buffer, bool isAsync, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isAsync)
        {
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            stream.Write(buffer);
        }
    }

    private static async Task WritePayloadSectors(
        Stream stream,
        byte[] data,
        int sectorCount,
        bool isAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > 0)
        {
            await WriteBuffer(stream, data, isAsync, cancellationToken).ConfigureAwait(false);
        }
        int totalBytes = sectorCount * SectorSize;
        int padBytes = totalBytes - data.Length;
        if (padBytes > 0)
        {
            byte[] pad = new byte[padBytes];
            await WriteBuffer(stream, pad, isAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion

    #region Descriptor Builders

    private static byte[] BuildPrimaryVolumeDescriptor(LayoutContext ctx)
    {
        byte[] pvd = new byte[SectorSize];

        pvd[0] = 0x01; // Volume Descriptor Type = Primary Volume Descriptor
        Encoding.ASCII.GetBytes("CD001", pvd.AsSpan(1, 5)); // Standard Identifier
        pvd[6] = 0x01; // Version
        pvd[7] = 0x00; // Unused

        // System Identifier (32 chars)
        WriteAsciiStringPadded(pvd.AsSpan(8, 32), "WINDOWS", 32);

        // Volume Identifier (32 chars)
        string volLabel = ctx.Options.VolumeLabel;
        if (volLabel.Length > 32)
        {
            volLabel = volLabel[..32];
        }
        WriteAsciiStringPadded(pvd.AsSpan(40, 32), volLabel, 32);

        // Volume Space Size (offsets 80..87, both-endian uint32)
        WriteBothEndianUInt32(pvd.AsSpan(80, 8), (uint)ctx.TotalSectors);

        // Volume Set Size (offsets 120..123, both-endian uint16 = 1)
        WriteBothEndianUInt16(pvd.AsSpan(120, 4), 1);

        // Volume Sequence Number (offsets 124..127, both-endian uint16 = 1)
        WriteBothEndianUInt16(pvd.AsSpan(124, 4), 1);

        // Logical Block Size (offsets 128..131, both-endian uint16 = 2048)
        WriteBothEndianUInt16(pvd.AsSpan(128, 4), (ushort)SectorSize);

        // Path Table Size (offsets 132..139, both-endian uint32)
        WriteBothEndianUInt32(pvd.AsSpan(132, 8), (uint)ctx.IsoPathTableBytesLength);

        // Path Table Locations (offsets 140..155)
        BinaryPrimitives.WriteUInt32LittleEndian(pvd.AsSpan(140, 4), (uint)ctx.IsoPathTableL_Lba);
        BinaryPrimitives.WriteUInt32LittleEndian(pvd.AsSpan(144, 4), 0); // Optional Type L
        BinaryPrimitives.WriteUInt32BigEndian(pvd.AsSpan(148, 4), (uint)ctx.IsoPathTableM_Lba);
        BinaryPrimitives.WriteUInt32BigEndian(pvd.AsSpan(152, 4), 0);    // Optional Type M

        // Root Directory Record (34 bytes at offset 156)
        var rootDir = ctx.BfsDirectories[0];
        WriteDirectoryRecord(
            pvd.AsSpan(156, 34),
            (uint)rootDir.IsoLba,
            rootDir.IsoExtentSizeBytes,
            rootDir.SourceDir.LastWriteTimeUtc,
            fileFlags: 0x02,
            identifierBytes: new byte[] { 0x00 });

        // Volume Set Identifier (128 bytes)
        WriteAsciiStringPadded(pvd.AsSpan(190, 128), string.Empty, 128);

        // Publisher Identifier (128 bytes)
        WriteAsciiStringPadded(pvd.AsSpan(318, 128), ctx.Options.PublisherId, 128);

        // Data Preparer Identifier (128 bytes)
        WriteAsciiStringPadded(pvd.AsSpan(446, 128), "IGNISDISC", 128);

        // Application Identifier (128 bytes)
        WriteAsciiStringPadded(pvd.AsSpan(574, 128), ctx.Options.ApplicationId, 128);

        // Copyright / Abstract / Bibliographic (space padded)
        WriteAsciiStringPadded(pvd.AsSpan(702, 37), string.Empty, 37);
        WriteAsciiStringPadded(pvd.AsSpan(739, 37), string.Empty, 37);
        WriteAsciiStringPadded(pvd.AsSpan(776, 37), string.Empty, 37);

        // Date/Time timestamps
        DateTime nowUtc = DateTime.UtcNow;
        WriteIsoDateTime(pvd.AsSpan(813, 17), nowUtc); // Creation
        WriteIsoDateTime(pvd.AsSpan(830, 17), nowUtc); // Modification
        Encoding.ASCII.GetBytes("0000000000000000", pvd.AsSpan(847, 16)); // Expiration
        pvd[863] = 0;
        WriteIsoDateTime(pvd.AsSpan(864, 17), nowUtc); // Effective

        pvd[881] = 0x01; // File Structure Version
        return pvd;
    }

    private static byte[] BuildJolietVolumeDescriptor(LayoutContext ctx)
    {
        byte[] svd = new byte[SectorSize];

        svd[0] = 0x02; // Supplementary Volume Descriptor
        Encoding.ASCII.GetBytes("CD001", svd.AsSpan(1, 5));
        svd[6] = 0x01; // Version
        svd[7] = 0x00; // Volume Flags

        // System Identifier (UCS-2BE, 32 bytes)
        WriteUcs2BeString(svd.AsSpan(8, 32), "WINDOWS");

        // Volume Identifier (UCS-2BE, 32 bytes = 16 UTF-16 characters)
        string volLabel = ctx.Options.VolumeLabel;
        if (volLabel.Length > 16)
        {
            volLabel = volLabel[..16];
        }
        WriteUcs2BeString(svd.AsSpan(40, 32), volLabel);

        // Volume Space Size (offsets 80..87, both-endian uint32)
        WriteBothEndianUInt32(svd.AsSpan(80, 8), (uint)ctx.TotalSectors);

        // Escape Sequences for UCS-2 Level 3 at offsets 88..90 (0x25, 0x2F, 0x45)
        svd[88] = 0x25; // '%'
        svd[89] = 0x2F; // '/'
        svd[90] = 0x45; // 'E'

        // Volume Set Size (offsets 120..123)
        WriteBothEndianUInt16(svd.AsSpan(120, 4), 1);

        // Volume Sequence Number (offsets 124..127)
        WriteBothEndianUInt16(svd.AsSpan(124, 4), 1);

        // Logical Block Size (offsets 128..131)
        WriteBothEndianUInt16(svd.AsSpan(128, 4), (ushort)SectorSize);

        // Path Table Size (offsets 132..139)
        WriteBothEndianUInt32(svd.AsSpan(132, 8), (uint)ctx.JolietPathTableBytesLength);

        // Path Table Locations
        BinaryPrimitives.WriteUInt32LittleEndian(svd.AsSpan(140, 4), (uint)ctx.JolietPathTableL_Lba);
        BinaryPrimitives.WriteUInt32LittleEndian(svd.AsSpan(144, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(svd.AsSpan(148, 4), (uint)ctx.JolietPathTableM_Lba);
        BinaryPrimitives.WriteUInt32BigEndian(svd.AsSpan(152, 4), 0);

        // Root Directory Record for Joliet (34 bytes at offset 156)
        var rootDir = ctx.BfsDirectories[0];
        WriteDirectoryRecord(
            svd.AsSpan(156, 34),
            (uint)rootDir.JolietLba,
            rootDir.JolietExtentSizeBytes,
            rootDir.SourceDir.LastWriteTimeUtc,
            fileFlags: 0x02,
            identifierBytes: new byte[] { 0x00 });

        // Volume Set Identifier (128 bytes)
        WriteUcs2BeString(svd.AsSpan(190, 128), string.Empty);

        // Publisher Identifier (128 bytes)
        WriteUcs2BeString(svd.AsSpan(318, 128), ctx.Options.PublisherId);

        // Data Preparer Identifier (128 bytes)
        WriteUcs2BeString(svd.AsSpan(446, 128), "IGNISDISC");

        // Application Identifier (128 bytes)
        WriteUcs2BeString(svd.AsSpan(574, 128), ctx.Options.ApplicationId);

        // Copyright / Abstract / Bibliographic
        WriteAsciiStringPadded(svd.AsSpan(702, 37), string.Empty, 37);
        WriteAsciiStringPadded(svd.AsSpan(739, 37), string.Empty, 37);
        WriteAsciiStringPadded(svd.AsSpan(776, 37), string.Empty, 37);

        // Date/Time timestamps
        DateTime nowUtc = DateTime.UtcNow;
        WriteIsoDateTime(svd.AsSpan(813, 17), nowUtc);
        WriteIsoDateTime(svd.AsSpan(830, 17), nowUtc);
        Encoding.ASCII.GetBytes("0000000000000000", svd.AsSpan(847, 16));
        svd[863] = 0;
        WriteIsoDateTime(svd.AsSpan(864, 17), nowUtc);

        svd[881] = 0x01; // File Structure Version
        return svd;
    }

    private static byte[] BuildVolumeDescriptorTerminator()
    {
        byte[] term = new byte[SectorSize];
        term[0] = 0xFF; // Volume Descriptor Set Terminator
        Encoding.ASCII.GetBytes("CD001", term.AsSpan(1, 5));
        term[6] = 0x01; // Version
        return term;
    }

    private static byte[] BuildUdfExtendedAreaDescriptor(string identifier)
    {
        byte[] descriptor = new byte[SectorSize];
        descriptor[0] = 0x00; // Structure Type = 0
        Encoding.ASCII.GetBytes(identifier, descriptor.AsSpan(1, 5));
        descriptor[6] = 0x01; // Version
        return descriptor;
    }

    private static byte[] BuildUdfAvdpSector(uint tagLocation = 256, uint mainVdsLocation = 257)
    {
        byte[] sector = new byte[SectorSize];

        // ECMA-167 Anchor Volume Descriptor Pointer (AVDP)
        // Tag Identifier = 0x0002 (AVDP)
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(0, 2), 0x0002);
        // Descriptor Version = 2
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(2, 2), 2);
        // Offset 4: Tag Checksum (calculated later)
        sector[5] = 0x00; // Reserved
        // Tag Serial Number = 1
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(6, 2), 1);
        // Offset 8..9: Descriptor CRC (calculated later)
        // Offset 10..11: Descriptor CRC Length = 512 - 16 = 496
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(10, 2), 496);
        // Offset 12..15: Tag Location = LBA 256
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(12, 4), tagLocation);

        // Offset 16..23: Main Volume Descriptor Sequence Extent (extent_ad: length, location)
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(16, 4), 32768); // 16 sectors
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(20, 4), mainVdsLocation);

        // Offset 24..31: Reserve Volume Descriptor Sequence Extent
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(24, 4), 32768);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(28, 4), mainVdsLocation);

        // Compute Descriptor CRC over bytes 16..511 (496 bytes)
        ushort crc = CalculateEcma167Crc(sector.AsSpan(16, 496));
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(8, 2), crc);

        // Compute Tag Checksum over bytes 0..3 and 5..15 modulo 256
        byte sum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i != 4)
            {
                sum = (byte)(sum + sector[i]);
            }
        }
        sector[4] = sum;

        return sector;
    }

    private static readonly Regex VtsFileNameRegex = new(
        @"^VTS_(?<set>\d{2})_(?<index>\d)\.(?<ext>IFO|BUP|VOB)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (int category, int setNum, int subOrder, string name) GetDvdVideoSortKey(string fileName)
    {
        string upper = fileName.ToUpperInvariant();
        if (upper == "VIDEO_TS.IFO")
        {
            return (0, 0, 0, upper);
        }
        if (upper == "VIDEO_TS.VOB")
        {
            return (0, 0, 1, upper);
        }
        if (upper == "VIDEO_TS.BUP")
        {
            return (0, 0, 2, upper);
        }

        var match = VtsFileNameRegex.Match(upper);
        if (match.Success)
        {
            int setNum = int.Parse(match.Groups["set"].Value);
            int index = int.Parse(match.Groups["index"].Value);
            string ext = match.Groups["ext"].Value;

            int subOrder;
            if (ext == "IFO" && index == 0)
            {
                subOrder = 1;
            }
            else if (ext == "VOB" && index == 0)
            {
                subOrder = 2;
            }
            else if (ext == "VOB" && index > 0)
            {
                subOrder = 10 + index;
            }
            else if (ext == "BUP" && index == 0)
            {
                subOrder = 30;
            }
            else
            {
                subOrder = 40 + index;
            }

            return (1, setNum, subOrder, upper);
        }

        return (2, 0, 0, upper);
    }

    private static void FinalizeEcma167Tag(byte[] sector, ushort tagId, uint tagLocation, ushort crcPayloadLength = 496)
        => FinalizeEcma167Tag(sector.AsSpan(), tagId, tagLocation, crcPayloadLength);

    private static void FinalizeEcma167Tag(Span<byte> sector, ushort tagId, uint tagLocation, ushort crcPayloadLength = 496)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(sector[..2], tagId);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(2, 2), 2); // Descriptor version 2 (NSR02)
        sector[4] = 0; // Checksum placeholder
        sector[5] = 0; // Reserved
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(6, 2), 1); // Serial Number 1
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(10, 2), crcPayloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(12, 4), tagLocation);

        ushort crc = CalculateEcma167Crc(sector.Slice(16, crcPayloadLength));
        BinaryPrimitives.WriteUInt16LittleEndian(sector.Slice(8, 2), crc);

        byte sum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i != 4)
            {
                sum = (byte)(sum + sector[i]);
            }
        }
        sector[4] = sum;
    }

    #endregion

    #region UDF 1.02 (ECMA-167 / OSTA UDF) structures

    private const int UdfVdsSectorCount = 16;
    private const ushort UdfRevisionBcd = 0x0102;
    private const int UdfFileEntryFixedSize = 176;
    private const uint UdfMaxExtentBytes = 0x3FFFF800; // largest 2048-aligned length that fits the 30-bit extent length field
    private const string UdfImplementationId = "*IgnisDisc";

    private static int UdfFidLength(int identifierBytes) => (38 + identifierBytes + 3) & ~3;

    /// <summary>
    /// Adds the complete UDF 1.02 volume: Anchor Volume Descriptor Pointers, Volume Descriptor Sequence,
    /// Logical Volume Integrity Descriptor, File Set Descriptor, and one File Entry per directory and file,
    /// plus the File Identifier Descriptor streams that make up each directory.
    /// UDF shares file data extents with ISO 9660 / Joliet (UDF Bridge).
    /// </summary>
    private static void AddUdfStructures(LayoutContext ctx, List<(int Lba, byte[] Data)> metadata)
    {
        int partitionStart = ctx.UdfPartitionStartLba;
        int partitionLength = ctx.UdfClosingAvdpLba - partitionStart;
        DateTime now = DateTime.UtcNow;
        string label = ctx.Options.VolumeLabel ?? string.Empty;

        // Unique IDs: root = 0, 1..15 reserved by UDF, the rest assigned sequentially.
        ulong nextUniqueId = 16;
        var uniqueIds = new Dictionary<object, ulong>(ReferenceEqualityComparer.Instance);
        foreach (var dir in ctx.BfsDirectories)
        {
            uniqueIds[dir] = dir.DirNumber == 1 ? 0 : nextUniqueId++;
        }
        foreach (var file in ctx.AllFiles)
        {
            uniqueIds[file] = nextUniqueId++;
        }

        uint Lbn(int absoluteLba) => (uint)(absoluteLba - partitionStart);

        // Anchors
        metadata.Add((ctx.AvdpLba, BuildUdfAvdpSector((uint)ctx.AvdpLba, (uint)ctx.UdfVdsLba)));
        metadata.Add((ctx.UdfClosingAvdpLba, BuildUdfAvdpSector((uint)ctx.UdfClosingAvdpLba, (uint)ctx.UdfVdsLba)));

        // Volume Descriptor Sequence (absolute tag locations)
        int v = ctx.UdfVdsLba;
        byte[] vds = new byte[UdfVdsSectorCount * SectorSize];
        Span<byte> S(int index) => vds.AsSpan(index * SectorSize, SectorSize);

        // [0] Primary Volume Descriptor
        {
            var d = S(0);
            BinaryPrimitives.WriteUInt32LittleEndian(d[16..], 1);  // VDS number
            BinaryPrimitives.WriteUInt32LittleEndian(d[20..], 0);  // PVD number
            WriteUdfDString(d.Slice(24, 32), label);
            BinaryPrimitives.WriteUInt16LittleEndian(d[56..], 1);  // volume sequence number
            BinaryPrimitives.WriteUInt16LittleEndian(d[58..], 1);  // max volume sequence number
            BinaryPrimitives.WriteUInt16LittleEndian(d[60..], 2);  // interchange level
            BinaryPrimitives.WriteUInt16LittleEndian(d[62..], 2);  // max interchange level
            BinaryPrimitives.WriteUInt32LittleEndian(d[64..], 1);  // character set list (CS0)
            BinaryPrimitives.WriteUInt32LittleEndian(d[68..], 1);
            // UDF 2.2.2.5: first 16 characters of the Volume Set Identifier should be a unique hex value
            WriteUdfDString(d.Slice(72, 128), $"{now.Ticks:X16}{label}");
            WriteUdfCharspec(d.Slice(200, 64));
            WriteUdfCharspec(d.Slice(264, 64));
            WriteUdfTimestamp(d.Slice(376, 12), now);
            WriteUdfRegid(d.Slice(388, 32), UdfImplementationId, ReadOnlySpan<byte>.Empty);
            FinalizeEcma167Tag(d, 0x0001, (uint)v, 496);
        }

        // [1] Implementation Use Volume Descriptor ("*UDF LV Info")
        {
            var d = S(1);
            BinaryPrimitives.WriteUInt32LittleEndian(d[16..], 2);
            WriteUdfRegid(d.Slice(20, 32), "*UDF LV Info", UdfDomainSuffix());
            WriteUdfCharspec(d.Slice(52, 64));
            WriteUdfDString(d.Slice(116, 128), label);
            WriteUdfRegid(d.Slice(352, 32), UdfImplementationId, ReadOnlySpan<byte>.Empty);
            FinalizeEcma167Tag(d, 0x0004, (uint)(v + 1), 496);
        }

        // [2] Partition Descriptor
        {
            var d = S(2);
            BinaryPrimitives.WriteUInt32LittleEndian(d[16..], 3);
            BinaryPrimitives.WriteUInt16LittleEndian(d[20..], 1);  // partition flags: allocated
            BinaryPrimitives.WriteUInt16LittleEndian(d[22..], 0);  // partition number
            WriteUdfRegid(d.Slice(24, 32), "+NSR02", ReadOnlySpan<byte>.Empty);
            BinaryPrimitives.WriteUInt32LittleEndian(d[184..], 1); // access type: read-only
            BinaryPrimitives.WriteUInt32LittleEndian(d[188..], (uint)partitionStart);
            BinaryPrimitives.WriteUInt32LittleEndian(d[192..], (uint)partitionLength);
            WriteUdfRegid(d.Slice(196, 32), UdfImplementationId, ReadOnlySpan<byte>.Empty);
            FinalizeEcma167Tag(d, 0x0005, (uint)(v + 2), 496);
        }

        // [3] Logical Volume Descriptor with one Type 1 partition map
        {
            var d = S(3);
            BinaryPrimitives.WriteUInt32LittleEndian(d[16..], 4);
            WriteUdfCharspec(d.Slice(20, 64));
            WriteUdfDString(d.Slice(84, 128), label);
            BinaryPrimitives.WriteUInt32LittleEndian(d[212..], SectorSize);
            WriteUdfRegid(d.Slice(216, 32), "*OSTA UDF Compliant", UdfDomainSuffix());
            WriteUdfLongAd(d.Slice(248, 16), SectorSize, Lbn(ctx.UdfFsdLba)); // File Set Descriptor location
            BinaryPrimitives.WriteUInt32LittleEndian(d[264..], 6); // map table length
            BinaryPrimitives.WriteUInt32LittleEndian(d[268..], 1); // number of partition maps
            WriteUdfRegid(d.Slice(272, 32), UdfImplementationId, ReadOnlySpan<byte>.Empty);
            WriteUdfExtentAd(d.Slice(432, 8), 2 * SectorSize, (uint)(v + 6)); // integrity sequence (LVID + TD)
            d[440] = 1; // partition map type 1
            d[441] = 6; // partition map length
            BinaryPrimitives.WriteUInt16LittleEndian(d[442..], 1); // volume sequence number
            BinaryPrimitives.WriteUInt16LittleEndian(d[444..], 0); // partition number
            FinalizeEcma167Tag(d, 0x0006, (uint)(v + 3), 440 + 6 - 16);
        }

        // [4] Unallocated Space Descriptor (read-only media: none)
        {
            var d = S(4);
            BinaryPrimitives.WriteUInt32LittleEndian(d[16..], 5);
            BinaryPrimitives.WriteUInt32LittleEndian(d[20..], 0);
            FinalizeEcma167Tag(d, 0x0007, (uint)(v + 4), 8);
        }

        // [5] Terminating Descriptor ends the main VDS
        FinalizeEcma167Tag(S(5), 0x0008, (uint)(v + 5), 496);

        // [6] Logical Volume Integrity Descriptor (closed) + [7] Terminating Descriptor
        {
            var d = S(6);
            WriteUdfTimestamp(d.Slice(16, 12), now);
            BinaryPrimitives.WriteUInt32LittleEndian(d[28..], 1); // integrity type: close
            BinaryPrimitives.WriteUInt64LittleEndian(d[40..], nextUniqueId); // logical volume header: next unique id
            BinaryPrimitives.WriteUInt32LittleEndian(d[72..], 1);  // number of partitions
            BinaryPrimitives.WriteUInt32LittleEndian(d[76..], 46); // length of implementation use
            BinaryPrimitives.WriteUInt32LittleEndian(d[80..], 0);  // free space table
            BinaryPrimitives.WriteUInt32LittleEndian(d[84..], (uint)partitionLength); // size table
            WriteUdfRegid(d.Slice(88, 32), UdfImplementationId, ReadOnlySpan<byte>.Empty);
            BinaryPrimitives.WriteUInt32LittleEndian(d[120..], (uint)ctx.AllFiles.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(d[124..], (uint)ctx.BfsDirectories.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(d[128..], UdfRevisionBcd); // min read revision
            BinaryPrimitives.WriteUInt16LittleEndian(d[130..], UdfRevisionBcd); // min write revision
            BinaryPrimitives.WriteUInt16LittleEndian(d[132..], UdfRevisionBcd); // max write revision
            FinalizeEcma167Tag(d, 0x0009, (uint)(v + 6), 88 + 46 - 16);
            FinalizeEcma167Tag(S(7), 0x0008, (uint)(v + 7), 496);
        }

        metadata.Add((v, vds));

        // File Set Descriptor + Terminating Descriptor (partition-relative tag locations)
        var root = ctx.BfsDirectories[0];
        {
            byte[] fsd = new byte[SectorSize];
            var d = fsd.AsSpan();
            WriteUdfTimestamp(d.Slice(16, 12), now);
            BinaryPrimitives.WriteUInt16LittleEndian(d[28..], 3); // interchange level
            BinaryPrimitives.WriteUInt16LittleEndian(d[30..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(d[32..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(d[36..], 1);
            WriteUdfCharspec(d.Slice(48, 64));
            WriteUdfDString(d.Slice(112, 128), label);
            WriteUdfCharspec(d.Slice(240, 64));
            WriteUdfDString(d.Slice(304, 32), label);
            WriteUdfLongAd(d.Slice(400, 16), SectorSize, Lbn(root.UdfFeLba)); // root directory ICB
            WriteUdfRegid(d.Slice(416, 32), "*OSTA UDF Compliant", UdfDomainSuffix());
            FinalizeEcma167Tag(d, 0x0100, Lbn(ctx.UdfFsdLba), 496);
            metadata.Add((ctx.UdfFsdLba, fsd));

            byte[] td = new byte[SectorSize];
            FinalizeEcma167Tag(td, 0x0008, Lbn(ctx.UdfFsdTerminatorLba), 496);
            metadata.Add((ctx.UdfFsdTerminatorLba, td));
        }

        // Directories: File Entry + File Identifier Descriptor stream
        foreach (var dir in ctx.BfsDirectories)
        {
            var parent = dir.Parent ?? dir;
            int fidSectors = (dir.UdfFidBytes + SectorSize - 1) / SectorSize;
            byte[] fids = new byte[fidSectors * SectorSize];
            int offset = 0;

            offset += WriteUdfFid(fids, offset, Lbn(dir.UdfFidLba), characteristics: 0x0A, Array.Empty<byte>(), Lbn(parent.UdfFeLba));

            foreach (var sub in dir.SubDirectories)
            {
                byte characteristics = 0x02;
                if (sub.SourceDir.Attributes.HasFlag(FileAttributes.Hidden)) characteristics |= 0x01;
                offset += WriteUdfFid(fids, offset, Lbn(dir.UdfFidLba), characteristics,
                    EncodeOstaCompressedUnicode(sub.UdfName, 255), Lbn(sub.UdfFeLba));
            }

            foreach (var file in dir.Files)
            {
                byte characteristics = 0x00;
                if (file.SourceFile.Attributes.HasFlag(FileAttributes.Hidden)) characteristics |= 0x01;
                offset += WriteUdfFid(fids, offset, Lbn(dir.UdfFidLba), characteristics,
                    EncodeOstaCompressedUnicode(file.UdfName, 255), Lbn(file.UdfFeLba));
            }

            if (offset != dir.UdfFidBytes)
            {
                throw new InvalidOperationException($"UDF directory stream size mismatch ({offset} != {dir.UdfFidBytes}).");
            }

            metadata.Add((dir.UdfFidLba, fids));
            metadata.Add((dir.UdfFeLba, BuildUdfFileEntry(
                Lbn(dir.UdfFeLba),
                fileType: 4,
                permissions: 0x14A5,
                linkCount: (ushort)(1 + dir.SubDirectories.Count),
                informationLength: (ulong)dir.UdfFidBytes,
                dataLba: Lbn(dir.UdfFidLba),
                uniqueId: uniqueIds[dir],
                modifiedUtc: dir.SourceDir.LastWriteTimeUtc)));
        }

        // Files: File Entry pointing at the shared ISO/UDF data extent
        foreach (var file in ctx.AllFiles)
        {
            metadata.Add((file.UdfFeLba, BuildUdfFileEntry(
                Lbn(file.UdfFeLba),
                fileType: 5,
                permissions: 0x1084,
                linkCount: 1,
                informationLength: (ulong)Math.Max(0, file.SourceFile.SizeBytes),
                dataLba: Lbn(file.StartLba),
                uniqueId: uniqueIds[file],
                modifiedUtc: file.SourceFile.LastWriteTimeUtc)));
        }
    }

    private static byte[] BuildUdfFileEntry(
        uint tagLbn,
        byte fileType,
        uint permissions,
        ushort linkCount,
        ulong informationLength,
        uint dataLba,
        ulong uniqueId,
        DateTime modifiedUtc)
    {
        byte[] fe = new byte[SectorSize];
        var d = fe.AsSpan();

        // Allocation descriptors (short_ad): split into extents of at most ~1 GiB
        int adCount = 0;
        ulong remaining = informationLength;
        uint position = dataLba;
        int adOffset = UdfFileEntryFixedSize;
        while (remaining > 0)
        {
            if (adOffset + 8 > SectorSize)
            {
                throw new NotSupportedException("File is too large to describe in a single UDF File Entry.");
            }
            uint length = (uint)Math.Min(remaining, UdfMaxExtentBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(d[adOffset..], length); // type 0: recorded and allocated
            BinaryPrimitives.WriteUInt32LittleEndian(d[(adOffset + 4)..], position);
            position += (length + SectorSize - 1) / SectorSize;
            remaining -= length;
            adOffset += 8;
            adCount++;
        }
        int adLength = adCount * 8;
        ulong blocksRecorded = (informationLength + SectorSize - 1) / SectorSize;

        // ICB tag
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], 0);  // prior recorded number of direct entries
        BinaryPrimitives.WriteUInt16LittleEndian(d[20..], 4);  // strategy type 4
        BinaryPrimitives.WriteUInt16LittleEndian(d[24..], 1);  // max number of entries
        d[27] = fileType;                                      // 4 = directory, 5 = file
        BinaryPrimitives.WriteUInt16LittleEndian(d[34..], 0);  // flags: short_ad allocation descriptors

        BinaryPrimitives.WriteUInt32LittleEndian(d[36..], uint.MaxValue); // uid (not specified)
        BinaryPrimitives.WriteUInt32LittleEndian(d[40..], uint.MaxValue); // gid (not specified)
        BinaryPrimitives.WriteUInt32LittleEndian(d[44..], permissions);
        BinaryPrimitives.WriteUInt16LittleEndian(d[48..], linkCount);
        BinaryPrimitives.WriteUInt64LittleEndian(d[56..], informationLength);
        BinaryPrimitives.WriteUInt64LittleEndian(d[64..], blocksRecorded);

        DateTime ts = modifiedUtc == default || modifiedUtc.Year < 1980 ? DateTime.UtcNow : modifiedUtc;
        WriteUdfTimestamp(d.Slice(72, 12), ts);  // access
        WriteUdfTimestamp(d.Slice(84, 12), ts);  // modification
        WriteUdfTimestamp(d.Slice(96, 12), ts);  // attribute
        BinaryPrimitives.WriteUInt32LittleEndian(d[108..], 1); // checkpoint
        WriteUdfRegid(d.Slice(128, 32), UdfImplementationId, ReadOnlySpan<byte>.Empty);
        BinaryPrimitives.WriteUInt64LittleEndian(d[160..], uniqueId);
        BinaryPrimitives.WriteUInt32LittleEndian(d[168..], 0);        // length of extended attributes
        BinaryPrimitives.WriteUInt32LittleEndian(d[172..], (uint)adLength);

        FinalizeEcma167Tag(d, 0x0105, tagLbn, (ushort)(UdfFileEntryFixedSize + adLength - 16));
        return fe;
    }

    private static int WriteUdfFid(byte[] buffer, int offset, uint streamStartLbn, byte characteristics, byte[] identifier, uint icbLbn)
    {
        int length = UdfFidLength(identifier.Length);
        var d = buffer.AsSpan(offset, length);
        BinaryPrimitives.WriteUInt16LittleEndian(d[16..], 1); // file version number
        d[18] = characteristics;
        d[19] = (byte)identifier.Length;
        WriteUdfLongAd(d.Slice(20, 16), SectorSize, icbLbn);
        BinaryPrimitives.WriteUInt16LittleEndian(d[36..], 0); // length of implementation use
        identifier.CopyTo(d[38..]);
        uint tagLocation = streamStartLbn + (uint)(offset / SectorSize);
        FinalizeEcma167Tag(d, 0x0101, tagLocation, (ushort)(length - 16));
        return length;
    }

    /// <summary>OSTA Compressed Unicode: compression ID 8 (one byte per char) or 16 (UTF-16BE).</summary>
    private static byte[] EncodeOstaCompressedUnicode(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<byte>();
        }

        bool wide = value.Any(c => c > 0xFF);
        if (!wide)
        {
            int n = Math.Min(value.Length, maxBytes - 1);
            byte[] r = new byte[n + 1];
            r[0] = 8;
            for (int i = 0; i < n; i++) r[i + 1] = (byte)value[i];
            return r;
        }

        int chars = Math.Min(value.Length, (maxBytes - 1) / 2);
        if (chars > 0 && char.IsHighSurrogate(value[chars - 1])) chars--;
        byte[] w = new byte[1 + chars * 2];
        w[0] = 16;
        for (int i = 0; i < chars; i++)
        {
            w[1 + i * 2] = (byte)(value[i] >> 8);
            w[2 + i * 2] = (byte)value[i];
        }
        return w;
    }

    private static void WriteUdfDString(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] encoded = EncodeOstaCompressedUnicode(value, destination.Length - 1);
        if (encoded.Length == 0) return;
        encoded.CopyTo(destination);
        destination[^1] = (byte)encoded.Length;
    }

    private static void WriteUdfCharspec(Span<byte> destination)
    {
        destination.Clear();
        destination[0] = 0; // CS0
        Encoding.ASCII.GetBytes("OSTA Compressed Unicode", destination[1..]);
    }

    private static byte[] UdfDomainSuffix()
    {
        byte[] suffix = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(suffix, UdfRevisionBcd);
        return suffix;
    }

    private static void WriteUdfRegid(Span<byte> destination, string identifier, ReadOnlySpan<byte> suffix)
    {
        destination.Clear();
        Encoding.ASCII.GetBytes(identifier.Length > 23 ? identifier[..23] : identifier, destination.Slice(1, 23));
        if (!suffix.IsEmpty)
        {
            suffix[..Math.Min(8, suffix.Length)].CopyTo(destination.Slice(24, 8));
        }
    }

    private static void WriteUdfLongAd(Span<byte> destination, uint length, uint lbn, ushort partitionReference = 0)
    {
        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], lbn);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], partitionReference);
    }

    private static void WriteUdfExtentAd(Span<byte> destination, uint length, uint location)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], location);
    }

    private static void WriteUdfTimestamp(Span<byte> destination, DateTime utc)
    {
        utc = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        BinaryPrimitives.WriteUInt16LittleEndian(destination, 0x1000); // type 1 (local time), offset 0 = UTC
        BinaryPrimitives.WriteInt16LittleEndian(destination[2..], (short)utc.Year);
        destination[4] = (byte)utc.Month;
        destination[5] = (byte)utc.Day;
        destination[6] = (byte)utc.Hour;
        destination[7] = (byte)utc.Minute;
        destination[8] = (byte)utc.Second;
        destination[9] = (byte)(utc.Millisecond / 10);
        destination[10] = 0;
        destination[11] = 0;
    }

    #endregion


    #region Directory and Path Table Writers

    private static byte[] BuildDirectoryExtent(DirectoryLayoutNode dir, bool isJoliet)
    {
        uint extentBytes = isJoliet ? dir.JolietExtentSizeBytes : dir.IsoExtentSizeBytes;
        byte[] extent = new byte[extentBytes];

        int sectorIdx = 0;
        int offsetInSector = 0;

        // 1. '.' entry (current directory)
        uint thisLba = isJoliet ? (uint)dir.JolietLba : (uint)dir.IsoLba;
        WriteDirectoryRecord(
            extent.AsSpan(0, 34),
            thisLba,
            extentBytes,
            dir.SourceDir.LastWriteTimeUtc,
            fileFlags: 0x02,
            identifierBytes: new byte[] { 0x00 });
        offsetInSector += 34;

        // 2. '..' entry (parent directory)
        var parent = dir.Parent ?? dir;
        uint parentLba = isJoliet ? (uint)parent.JolietLba : (uint)parent.IsoLba;
        uint parentBytes = isJoliet ? parent.JolietExtentSizeBytes : parent.IsoExtentSizeBytes;
        WriteDirectoryRecord(
            extent.AsSpan(34, 34),
            parentLba,
            parentBytes,
            parent.SourceDir.LastWriteTimeUtc,
            fileFlags: 0x02,
            identifierBytes: new byte[] { 0x01 });
        offsetInSector += 34;

        // 3. Child Subdirectories
        foreach (var sub in dir.SubDirectories)
        {
            string name = isJoliet ? sub.JolietName : sub.IsoName;

            byte[] ident = isJoliet
                ? Encoding.BigEndianUnicode.GetBytes(name)
                : Encoding.ASCII.GetBytes(name);

            byte recordLen = GetDirectoryRecordLength(ident.Length);
            if (offsetInSector + recordLen > SectorSize)
            {
                sectorIdx++;
                offsetInSector = 0;
            }

            int destOffset = sectorIdx * SectorSize + offsetInSector;
            byte flags = 0x02; // Directory
            if (sub.SourceDir.Attributes.HasFlag(FileAttributes.Hidden))
            {
                flags |= 0x01;
            }

            uint subLba = isJoliet ? (uint)sub.JolietLba : (uint)sub.IsoLba;
            uint subBytes = isJoliet ? sub.JolietExtentSizeBytes : sub.IsoExtentSizeBytes;

            WriteDirectoryRecord(
                extent.AsSpan(destOffset, recordLen),
                subLba,
                subBytes,
                sub.SourceDir.LastWriteTimeUtc,
                flags,
                ident);

            offsetInSector += recordLen;
        }

        // 4. Child Files
        foreach (var file in dir.Files)
        {
            string name = isJoliet
                ? file.JolietName
                : (!string.IsNullOrEmpty(file.Iso9660Name)
                    ? file.Iso9660Name
                    : IsoNameSanitizer.ToIso9660Level1Name(file.SourceFile.Name));

            byte[] ident = isJoliet
                ? Encoding.BigEndianUnicode.GetBytes(name)
                : Encoding.ASCII.GetBytes(name);

            byte recordLen = GetDirectoryRecordLength(ident.Length);
            if (offsetInSector + recordLen > SectorSize)
            {
                sectorIdx++;
                offsetInSector = 0;
            }

            int destOffset = sectorIdx * SectorSize + offsetInSector;
            byte flags = 0x00; // File
            if (file.SourceFile.Attributes.HasFlag(FileAttributes.Hidden))
            {
                flags |= 0x01;
            }

            WriteDirectoryRecord(
                extent.AsSpan(destOffset, recordLen),
                (uint)file.StartLba,
                (uint)Math.Min(file.SourceFile.SizeBytes, 0xFFFFF800L), // >4 GiB files are fully described only in UDF
                file.SourceFile.LastWriteTimeUtc,
                flags,
                ident);

            offsetInSector += recordLen;
        }

        return extent;
    }

    private static byte[] BuildPathTable(
        List<DirectoryLayoutNode> directories,
        int pathTableSectors,
        bool isJoliet,
        bool isBigEndian)
    {
        byte[] table = new byte[pathTableSectors * SectorSize];
        int offset = 0;

        foreach (var dir in directories)
        {
            uint extentLba = isJoliet ? (uint)dir.JolietLba : (uint)dir.IsoLba;
            ushort parentIndex = (ushort)dir.ParentDirNumber;

            if (dir.DirNumber == 1) // Root
            {
                table[offset] = 1; // Length of identifier
                table[offset + 1] = 0; // Extended attribute length
                WritePathTableUInt32(table.AsSpan(offset + 2, 4), extentLba, isBigEndian);
                WritePathTableUInt16(table.AsSpan(offset + 6, 2), parentIndex, isBigEndian);
                table[offset + 8] = 0x00; // Root identifier
                table[offset + 9] = 0x00; // Padding
                offset += 10;
            }
            else
            {
                string name = isJoliet ? dir.JolietName : dir.IsoName;

                byte[] nameBytes = isJoliet
                    ? Encoding.BigEndianUnicode.GetBytes(name)
                    : Encoding.ASCII.GetBytes(name);

                byte identLen = (byte)nameBytes.Length;
                table[offset] = identLen;
                table[offset + 1] = 0;
                WritePathTableUInt32(table.AsSpan(offset + 2, 4), extentLba, isBigEndian);
                WritePathTableUInt16(table.AsSpan(offset + 6, 2), parentIndex, isBigEndian);
                nameBytes.CopyTo(table.AsSpan(offset + 8, identLen));

                offset += 8 + identLen;
                if (identLen % 2 != 0)
                {
                    table[offset] = 0x00; // Padding byte
                    offset++;
                }
            }
        }

        return table;
    }

    private static void WritePathTableUInt32(Span<byte> destination, uint value, bool isBigEndian)
    {
        if (isBigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        }
    }

    private static void WritePathTableUInt16(Span<byte> destination, ushort value, bool isBigEndian)
    {
        if (isBigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        }
    }

    #endregion

    #region Formatting Helpers

    private static byte GetDirectoryRecordLength(int identifierLength)
    {
        int len = 33 + identifierLength;
        if (len % 2 != 0)
        {
            len++;
        }
        return (byte)len;
    }

    private static void WriteDirectoryRecord(
        Span<byte> destination,
        uint extentLba,
        uint dataLength,
        DateTime recordingTimeUtc,
        byte fileFlags,
        ReadOnlySpan<byte> identifierBytes)
    {
        byte identLen = (byte)identifierBytes.Length;
        byte recordLen = GetDirectoryRecordLength(identLen);

        destination[0] = recordLen;
        destination[1] = 0x00; // Extended Attribute record length
        WriteBothEndianUInt32(destination.Slice(2, 8), extentLba);
        WriteBothEndianUInt32(destination.Slice(10, 8), dataLength);

        // Recording date and time (7 bytes)
        destination[18] = (byte)Math.Clamp(recordingTimeUtc.Year - 1900, 0, 255);
        destination[19] = (byte)Math.Clamp(recordingTimeUtc.Month, 1, 12);
        destination[20] = (byte)Math.Clamp(recordingTimeUtc.Day, 1, 31);
        destination[21] = (byte)Math.Clamp(recordingTimeUtc.Hour, 0, 23);
        destination[22] = (byte)Math.Clamp(recordingTimeUtc.Minute, 0, 59);
        destination[23] = (byte)Math.Clamp(recordingTimeUtc.Second, 0, 59);
        destination[24] = 0; // GMT timezone offset

        destination[25] = fileFlags;
        destination[26] = 0; // File unit size
        destination[27] = 0; // Interleave gap size
        WriteBothEndianUInt16(destination.Slice(28, 4), 1); // Volume sequence number
        destination[32] = identLen;

        identifierBytes.CopyTo(destination.Slice(33, identLen));

        // Pad byte if needed
        if ((33 + identLen) % 2 != 0)
        {
            destination[33 + identLen] = 0x00;
        }
    }

    public static void WriteBothEndianUInt16(Span<byte> destination, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[..2], value);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), value);
    }

    public static void WriteBothEndianUInt32(Span<byte> destination, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], value);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), value);
    }

    private static void WriteAsciiStringPadded(Span<byte> destination, string value, int length)
    {
        destination[..length].Fill(0x20); // space fill
        if (!string.IsNullOrEmpty(value))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            int copyLen = Math.Min(bytes.Length, length);
            bytes.AsSpan(0, copyLen).CopyTo(destination);
        }
    }

    private static void WriteUcs2BeString(Span<byte> destination, string value)
    {
        // In UCS-2BE, spaces are 0x00 0x20
        for (int i = 0; i < destination.Length; i += 2)
        {
            destination[i] = 0x00;
            destination[i + 1] = 0x20;
        }

        if (!string.IsNullOrEmpty(value))
        {
            byte[] bytes = Encoding.BigEndianUnicode.GetBytes(value);
            int copyLen = Math.Min(bytes.Length, destination.Length);
            copyLen = (copyLen / 2) * 2; // ensure 16-bit alignment
            bytes.AsSpan(0, copyLen).CopyTo(destination);
        }
    }

    private static void WriteIsoDateTime(Span<byte> destination, DateTime dateTimeUtc)
    {
        string s = dateTimeUtc.ToString("yyyyMMddHHmmss00", System.Globalization.CultureInfo.InvariantCulture);
        Encoding.ASCII.GetBytes(s, destination[..16]);
        destination[16] = 0; // GMT offset
    }

    private static ushort CalculateEcma167Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ Ecma167CrcTable[(crc >> 8) ^ b]);
        }
        return crc;
    }

    private static ushort[] GenerateEcma167CrcTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort value = (ushort)(i << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((value & 0x8000) != 0)
                {
                    value = (ushort)((value << 1) ^ 0x1021);
                }
                else
                {
                    value = (ushort)(value << 1);
                }
            }
            table[i] = value;
        }
        return table;
    }

    #endregion
}
