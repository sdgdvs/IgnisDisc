using System.Buffers.Binary;
using System.Text;
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
    /// Compiles a compilation directory tree into an in-memory seekable stream containing the complete .iso image.
    /// </summary>
    /// <param name="root">The compilation root directory.</param>
    /// <param name="options">Optional ISO build options (defaults will be used if omitted).</param>
    /// <returns>A seekable <see cref="MemoryStream"/> positioned at offset 0.</returns>
    public static MemoryStream BuildToMemoryStream(CompilationDirectory root, IsoBuildOptions? options = null)
    {
        var ms = new MemoryStream();
        Build(root, ms, options);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Compiles a compilation directory tree and writes the .iso image directly to an output file.
    /// </summary>
    public static void BuildToFile(CompilationDirectory root, string outputIsoPath, IsoBuildOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);

        string? dir = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Build(root, fs, options);
    }

    /// <summary>
    /// Asynchronously compiles a compilation directory tree and writes the .iso image to an output file.
    /// </summary>
    public static async Task BuildToFileAsync(
        CompilationDirectory root,
        string outputIsoPath,
        IsoBuildOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);

        string? dir = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await BuildAsync(root, fs, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compiles a compilation directory tree and writes the .iso image into the provided destination stream.
    /// </summary>
    public static void Build(CompilationDirectory root, Stream outputStream, IsoBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(outputStream);

        options ??= new IsoBuildOptions();
        var context = LayoutContext.Create(root, options);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(outputStream);

        options ??= new IsoBuildOptions();
        var context = LayoutContext.Create(root, options);
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
        public int JolietSvdLba { get; set; } = -1;
        public int TerminatorLba { get; set; } = -1;
        public int Bea01Lba { get; set; } = -1;
        public int NsrLba { get; set; } = -1;
        public int Tea01Lba { get; set; } = -1;
        public int AvdpLba { get; set; } = -1;

        public int IsoPathTableL_Lba { get; set; }
        public int IsoPathTableM_Lba { get; set; }
        public int IsoPathTableBytesLength { get; set; }
        public int IsoPathTableSectors { get; set; }

        public int JolietPathTableL_Lba { get; set; } = -1;
        public int JolietPathTableM_Lba { get; set; } = -1;
        public int JolietPathTableBytesLength { get; set; }
        public int JolietPathTableSectors { get; set; }

        public int TotalSectors { get; set; }

        public LayoutContext(CompilationDirectory rootDir, IsoBuildOptions options)
        {
            RootDir = rootDir;
            Options = options;
        }

        public static LayoutContext Create(CompilationDirectory root, IsoBuildOptions options)
        {
            var ctx = new LayoutContext(root, options);

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

                // Sort files deterministically by name
                dirNode.Files.Sort((a, b) => string.Compare(a.SourceFile.Name, b.SourceFile.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var f in dirNode.Files)
                {
                    ctx.AllFiles.Add(f);
                }
            }

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

            // 5. Assign LBA sectors sequentially
            int currentLba = 16;
            ctx.PvdLba = currentLba++; // 16

            if (options.IncludeJoliet)
            {
                ctx.JolietSvdLba = currentLba++; // 17
            }

            ctx.TerminatorLba = currentLba++; // 18 (or 17 if no Joliet)

            if (options.IncludeUdf)
            {
                ctx.Bea01Lba = currentLba++; // 19
                ctx.NsrLba = currentLba++;   // 20
                ctx.Tea01Lba = currentLba++;   // 21
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

            // Reserve LBA 256 for UDF AVDP
            if (options.IncludeUdf)
            {
                ctx.AvdpLba = 256;
                if (currentLba <= 256)
                {
                    currentLba = 257; // File data starts after AVDP
                }
                else
                {
                    currentLba = Math.Max(currentLba, 257);
                }
            }

            // Assign file extents
            foreach (var file in ctx.AllFiles)
            {
                if (file.SourceFile.ImportedStartLba.HasValue)
                {
                    file.StartLba = file.SourceFile.ImportedStartLba.Value;
                }
                else
                {
                    file.StartLba = currentLba;
                    currentLba += file.SectorCount;
                }
            }

            int total = currentLba;
            if (options.IncludeUdf && total < 257)
            {
                total = 257;
            }

            ctx.TotalSectors = total;
            return ctx;
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
                    dirNode.Files.Add(new FileLayoutNode(file));
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
                string name = isJoliet
                    ? IsoNameSanitizer.ToJolietName(sub.SourceDir.Name)
                    : IsoNameSanitizer.ToIso9660DirectoryName(sub.SourceDir.Name);

                int identLen = isJoliet
                    ? Encoding.BigEndianUnicode.GetByteCount(name)
                    : Encoding.ASCII.GetByteCount(name);

                recordLengths.Add(GetDirectoryRecordLength(identLen));
            }

            foreach (var file in dir.Files)
            {
                string name = isJoliet
                    ? IsoNameSanitizer.ToJolietName(file.SourceFile.Name)
                    : IsoNameSanitizer.ToIso9660Level1Name(file.SourceFile.Name);

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
                string name = isJoliet
                    ? IsoNameSanitizer.ToJolietName(dir.SourceDir.Name)
                    : IsoNameSanitizer.ToIso9660DirectoryName(dir.SourceDir.Name);

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

        public DirectoryLayoutNode(CompilationDirectory sourceDir)
        {
            SourceDir = sourceDir;
        }
    }

    private sealed class FileLayoutNode
    {
        public CompilationFile SourceFile { get; }
        public int StartLba { get; set; }
        public int SectorCount { get; set; }

        public FileLayoutNode(CompilationFile sourceFile)
        {
            SourceFile = sourceFile;
            SectorCount = sourceFile.RequiredSectors;
        }
    }

    private static async Task WriteImageSectors(
        LayoutContext ctx,
        Stream stream,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        bool isAsync)
    {
        byte[] zeroSector = new byte[SectorSize];

        // 1. Write System Area: LBAs 0..15 (16 sectors = 32,768 zero bytes)
        for (int i = 0; i < 16; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isAsync)
            {
                await stream.WriteAsync(zeroSector, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                stream.Write(zeroSector);
            }
        }

        int writtenLba = 16;

        // 2. Primary Volume Descriptor at LBA 16
        byte[] pvd = BuildPrimaryVolumeDescriptor(ctx);
        await WriteSector(stream, pvd, isAsync, cancellationToken).ConfigureAwait(false);
        writtenLba++;

        // 3. Joliet Supplementary Volume Descriptor at LBA 17 (if enabled)
        if (ctx.Options.IncludeJoliet)
        {
            byte[] svd = BuildJolietVolumeDescriptor(ctx);
            await WriteSector(stream, svd, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba++;
        }

        // 4. Volume Descriptor Set Terminator at next LBA
        byte[] term = BuildVolumeDescriptorTerminator();
        await WriteSector(stream, term, isAsync, cancellationToken).ConfigureAwait(false);
        writtenLba++;

        // 5. Optional UDF Bridge Descriptors (BEA01, NSR02/NSR03, TEA01)
        if (ctx.Options.IncludeUdf)
        {
            byte[] bea01 = BuildUdfExtendedAreaDescriptor("BEA01");
            await WriteSector(stream, bea01, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba++;

            string nsrId = ctx.Options.UdfRevision >= 0x0200 ? "NSR03" : "NSR02";
            byte[] nsr = BuildUdfExtendedAreaDescriptor(nsrId);
            await WriteSector(stream, nsr, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba++;

            byte[] tea01 = BuildUdfExtendedAreaDescriptor("TEA01");
            await WriteSector(stream, tea01, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba++;
        }

        // 6. Write ISO Path Tables (Type L and Type M)
        byte[] isoPathL = BuildPathTable(ctx.BfsDirectories, ctx.IsoPathTableSectors, isJoliet: false, isBigEndian: false);
        await WriteBuffer(stream, isoPathL, isAsync, cancellationToken).ConfigureAwait(false);
        writtenLba += ctx.IsoPathTableSectors;

        byte[] isoPathM = BuildPathTable(ctx.BfsDirectories, ctx.IsoPathTableSectors, isJoliet: false, isBigEndian: true);
        await WriteBuffer(stream, isoPathM, isAsync, cancellationToken).ConfigureAwait(false);
        writtenLba += ctx.IsoPathTableSectors;

        // 7. Write Joliet Path Tables (Type L and Type M)
        if (ctx.Options.IncludeJoliet)
        {
            byte[] jolietPathL = BuildPathTable(ctx.BfsDirectories, ctx.JolietPathTableSectors, isJoliet: true, isBigEndian: false);
            await WriteBuffer(stream, jolietPathL, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba += ctx.JolietPathTableSectors;

            byte[] jolietPathM = BuildPathTable(ctx.BfsDirectories, ctx.JolietPathTableSectors, isJoliet: true, isBigEndian: true);
            await WriteBuffer(stream, jolietPathM, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba += ctx.JolietPathTableSectors;
        }

        // 8. Write ISO Directory Extents
        foreach (var dir in ctx.BfsDirectories)
        {
            byte[] dirExtent = BuildDirectoryExtent(dir, isJoliet: false);
            await WriteBuffer(stream, dirExtent, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba += dir.IsoSectorCount;
        }

        // 9. Write Joliet Directory Extents
        if (ctx.Options.IncludeJoliet)
        {
            foreach (var dir in ctx.BfsDirectories)
            {
                byte[] dirExtent = BuildDirectoryExtent(dir, isJoliet: true);
                await WriteBuffer(stream, dirExtent, isAsync, cancellationToken).ConfigureAwait(false);
                writtenLba += dir.JolietSectorCount;
            }
        }

        // 10. Write UDF AVDP at LBA 256 (if enabled)
        if (ctx.Options.IncludeUdf)
        {
            while (writtenLba < 256)
            {
                await WriteSector(stream, zeroSector, isAsync, cancellationToken).ConfigureAwait(false);
                writtenLba++;
            }

            byte[] avdp = BuildUdfAvdpSector(tagLocation: 256, mainVdsLocation: 257);
            await WriteSector(stream, avdp, isAsync, cancellationToken).ConfigureAwait(false);
            writtenLba++;
        }

        // 11. Write File Data Extents
        foreach (var file in ctx.AllFiles)
        {
            if (file.SourceFile.ImportedStartLba.HasValue)
            {
                continue; // File already recorded in previous session
            }

            // Zero-fill any sector gap up to file StartLba
            while (writtenLba < file.StartLba)
            {
                await WriteSector(stream, zeroSector, isAsync, cancellationToken).ConfigureAwait(false);
                writtenLba++;
            }

            if (file.SourceFile.SizeBytes > 0)
            {
                using var fileStream = file.SourceFile.OpenRead();
                byte[] transferBuf = new byte[65536];
                long remaining = file.SourceFile.SizeBytes;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(remaining, transferBuf.Length);
                    int read = isAsync
                        ? await fileStream.ReadAsync(transferBuf.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false)
                        : fileStream.Read(transferBuf, 0, toRead);

                    if (read == 0)
                    {
                        // File ended early, zero fill remainder
                        var zeros = new byte[remaining];
                        if (isAsync)
                        {
                            await stream.WriteAsync(zeros, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            stream.Write(zeros);
                        }
                        break;
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

                // Sector boundary padding
                int padBytes = (int)(SectorSize - (file.SourceFile.SizeBytes % SectorSize));
                if (padBytes < SectorSize)
                {
                    if (isAsync)
                    {
                        await stream.WriteAsync(zeroSector.AsMemory(0, padBytes), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        stream.Write(zeroSector, 0, padBytes);
                    }
                }
            }

            writtenLba += file.SectorCount;

            if (progress != null && ctx.TotalSectors > 0)
            {
                progress.Report(Math.Min(1.0, (double)writtenLba / ctx.TotalSectors));
            }
        }

        // 12. Pad any remaining sectors to exact total volume size
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
        WriteAsciiStringPadded(pvd.AsSpan(446, 128), "CDBURNER", 128);

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
        WriteUcs2BeString(svd.AsSpan(446, 128), "CDBURNER");

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
            string name = isJoliet
                ? IsoNameSanitizer.ToJolietName(sub.SourceDir.Name)
                : IsoNameSanitizer.ToIso9660DirectoryName(sub.SourceDir.Name);

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
                ? IsoNameSanitizer.ToJolietName(file.SourceFile.Name)
                : IsoNameSanitizer.ToIso9660Level1Name(file.SourceFile.Name);

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
                (uint)file.SourceFile.SizeBytes,
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
                string name = isJoliet
                    ? IsoNameSanitizer.ToJolietName(dir.SourceDir.Name)
                    : IsoNameSanitizer.ToIso9660DirectoryName(dir.SourceDir.Name);

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
