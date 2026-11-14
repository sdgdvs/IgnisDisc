using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Archival;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace CdBurner.Core.Tests;

/// <summary>
/// Archive discs against scratches along the tracks: par2's recovery volume is cut into one volume per zone, the image
/// builder places the zones in the inner, middle and outer part of the disc with a copy of the Primary Volume
/// Descriptor in each, and the readers find the file tree without sector 16 or any UDF anchor.
/// </summary>
public sealed class ArchiveZoneLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ignis-zones-" + Guid.NewGuid().ToString("N")[..8]);

    public ArchiveZoneLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Radius model -------------------------------------------------------------------------------------------

    [Fact]
    public void RadiusModel_DvdAreaPerSector_MatchesEcma267PhysicalFormat()
    {
        // ECMA-267: track pitch 0.74 µm, channel bit length 0.133 µm (4.7 GB disc), and a physical sector is
        // 26 sync frames of 1,488 channel bits. That area must agree with the model's capacity/radius derivation.
        double physicalMm2 = 0.74e-3 * (26 * 1488) * 0.133e-3;
        double model = DiscRadius.AreaPerSectorMm2(DiscMediaType.DvdPlusR)!.Value;
        Assert.InRange(model / physicalMm2, 0.99, 1.01);
    }

    [Fact]
    public void RadiusModel_Cd_IsNoLooserThanA74MinuteDisc()
    {
        // ECMA-130 nominal 74-minute geometry: 1.6 µm pitch at 1.2 m/s and 75 sectors per second, so 16 mm of track
        // per sector. The model (80-minute disc) is denser, so it never overstates how far apart two sectors are.
        double nominal74 = 1.6e-3 * (1200.0 / 75);
        Assert.True(DiscRadius.AreaPerSectorMm2(DiscMediaType.CdR)!.Value <= nominal74);
        Assert.InRange(DiscRadius.RadiusMm(DiscMediaType.CdR, 0)!.Value, 25.0, 25.2);
        Assert.InRange(DiscRadius.RadiusMm(DiscMediaType.CdR, DiscRadius.CdNominalSectors)!.Value, 57.99, 58.01);
        Assert.InRange(DiscRadius.RadiusMm(DiscMediaType.BdR, DiscRadius.BdNominalSectors)!.Value, 57.99, 58.01);
        Assert.Null(DiscRadius.RadiusMm(DiscMediaType.DvdPlusRDualLayer, 0));
    }

    [Theory]
    [InlineData(DiscMediaType.CdR, 330_000)]       // a full 650/700 MB CD-R
    [InlineData(DiscMediaType.DvdPlusR, 2_280_000)] // a full DVD+R
    public void FullDisc_RecoveryZones_AreAtLeastTenMillimetresApart(DiscMediaType media, int discSectors)
    {
        // A filled archive disc: data plus 20 % recovery, as archive-stage writes it.
        long dataSectors = discSectors * 5L / 6;
        var root = ArchiveTree(dataFiles: 40, dataSectors, recoverySectors: discSectors - dataSectors - 2_000, volumes: 3);

        var layout = IsoUdfImageBuilder.DescribeArchiveZones(root, ArchiveOptions);

        Assert.True(layout.TotalSectors <= discSectors, $"{layout.TotalSectors} sectors");
        Assert.Equal(3, layout.DescriptorCopyLbas.Count);
        Assert.Equal(3, layout.Zones.Count);
        Assert.All(layout.Zones, z => Assert.NotEmpty(z));

        double Radius(long lba) => DiscRadius.RadiusMm(media, lba)!.Value;
        var spans = layout.Zones.Select((zone, i) => (
            Start: Radius(layout.DescriptorCopyLbas[i]),
            End: Radius(zone.Max(f => f.StartLba + f.SectorCount) - 1))).ToList();
        Assert.True(spans[1].Start - spans[0].End >= 10, $"inner ends at {spans[0].End:F1} mm, middle starts at {spans[1].Start:F1} mm");
        Assert.True(spans[2].Start - spans[1].End >= 10, $"middle ends at {spans[1].End:F1} mm, outer starts at {spans[2].Start:F1} mm");
    }

    [Fact]
    public void ArchiveZones_InnerBeforeAllData_OuterAfterAllData_CopiesStartEachZone()
    {
        var root = ArchiveTree(dataFiles: 12, dataSectors: 6_000, recoverySectors: 3_000, volumes: 3);
        var layout = IsoUdfImageBuilder.DescribeArchiveZones(root, ArchiveOptions);
        var files = LayoutFiles(root);

        var data = files.Where(f => !f.Path.StartsWith("/RECOVERY/", StringComparison.Ordinal)).ToList();
        int firstData = data.Min(f => f.StartLba);
        int lastDataEnd = data.Max(f => f.StartLba + f.Sectors);

        Assert.Equal(new[] { "recovery.vol00000+00010.par2" }, layout.Zones[0].Select(f => f.Name));
        Assert.Equal(new[] { "recovery.par2", "recovery.vol00010+00010.par2" }, layout.Zones[1].Select(f => f.Name));
        Assert.Equal(new[] { "recovery.vol00020+00010.par2" }, layout.Zones[2].Select(f => f.Name));

        Assert.True(layout.Zones[0].Max(f => f.StartLba + f.SectorCount) <= firstData);
        Assert.True(layout.Zones[2].Min(f => f.StartLba) >= lastDataEnd);
        Assert.Contains(data, f => f.StartLba < layout.Zones[1][0].StartLba);
        Assert.Contains(data, f => f.StartLba > layout.Zones[1][0].StartLba);
        for (int z = 0; z < 3; z++)
        {
            Assert.Equal(layout.DescriptorCopyLbas[z] + 1, layout.Zones[z].Min(f => f.StartLba));
        }
    }

    [Fact]
    public void OrdinaryImage_GetsNoZonesAndNoCopies()
    {
        // Without the archive manifest a RECOVERY folder is just a folder: the layout stays as before.
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(new CompilationFile("photo.jpg", string.Empty, 50_000));
        root.AddDirectory("RECOVERY").AddFile(new CompilationFile("recovery.par2", string.Empty, 4_096));

        var layout = IsoUdfImageBuilder.DescribeArchiveZones(root, ArchiveOptions);

        Assert.Empty(layout.DescriptorCopyLbas);
        Assert.Empty(layout.Zones);
    }

    // ---- Descriptor copies and the readers ----------------------------------------------------------------------

    [Fact]
    public void BuiltArchiveImage_CopiesAreByteIdenticalToSector16_AndListedThere()
    {
        string folder = StageArchiveFolder("copies");
        using var image = Build(folder);
        byte[] pvd = Sector(image, 16);

        var listed = PrimaryVolumeDescriptorCopy.ReadLocations(pvd);
        Assert.Equal(3, listed.Count);
        foreach (int lba in listed)
        {
            Assert.Equal(pvd, Sector(image, lba));
        }
        // ECMA-119 8.4: the descriptor keeps its fixed fields (type 1, "CD001", version 1, file structure version 1).
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(pvd, 1, 5));
        Assert.Equal(1, pvd[6]);
        Assert.Equal(1, pvd[881]);
    }

    [Fact]
    public void Sector16Gone_ReaderUsesTheCopy_PlainReaderStillRefuses()
    {
        string folder = StageArchiveFolder("sector16");
        using var image = Build(folder);
        var expected = PrimaryVolumeDescriptorCopy.ReadLocations(Sector(image, 16));
        Zero(image, 16);

        image.Position = 0;
        Assert.Throws<InvalidDataException>(() => IsoImageReader.FromStream(image, leaveOpen: true));

        image.Position = 0;
        using var reader = IsoImageReader.FromDamagedStream(image, leaveOpen: true);
        Assert.Equal(expected[0], reader.DescriptorCopyLba);
        AssertSameContent(folder, reader);
    }

    [Fact]
    public void Sector16AndInnerCopyGone_TheMiddleCopyIsUsed()
    {
        string folder = StageArchiveFolder("inner");
        using var image = Build(folder);
        var copies = PrimaryVolumeDescriptorCopy.ReadLocations(Sector(image, 16));
        Zero(image, 16);
        Zero(image, copies[0]);

        image.Position = 0;
        using var reader = IsoImageReader.FromDamagedStream(image, leaveOpen: true);
        Assert.Equal(copies[1], reader.DescriptorCopyLba);
        AssertSameContent(folder, reader);
    }

    [Fact]
    public void ArchivedIsoFile_ItsDescriptorIsNeverTakenForTheDisc()
    {
        // An archive disc that stores another archive disc's image as a file: the stored image's descriptor copies
        // list LBAs inside that image, so with every copy of the disc's own descriptor gone, nothing is found.
        string innerFolder = StageArchiveFolder("inner-disc");
        using (var inner = Build(innerFolder))
        {
            string outerFolder = StageArchiveFolder("outer-disc", extra: ("old-disc.iso", inner.ToArray()));
            using var outer = Build(outerFolder);
            var copies = PrimaryVolumeDescriptorCopy.ReadLocations(Sector(outer, 16));
            Zero(outer, 16);
            foreach (int lba in copies)
            {
                Zero(outer, lba);
            }

            outer.Position = 0;
            Assert.Null(PrimaryVolumeDescriptorCopy.Find(outer));
            outer.Position = 0;
            Assert.Throws<InvalidDataException>(() => IsoImageReader.FromDamagedStream(outer, leaveOpen: true));
        }
    }

    [Fact]
    public void EveryUdfAnchorGone_UdfTreeIsFoundThroughItsDescriptorSequence()
    {
        string folder = StageArchiveFolder("anchors", longName: true);
        using var image = Build(folder);
        int last = (int)(image.Length / 2048) - 1;
        Zero(image, 256);
        Zero(image, last);

        image.Position = 0;
        var root = UdfTreeReader.TryRead(image);
        Assert.NotNull(root);
        var names = Flatten(root!).Select(f => f.VirtualPath).ToList();
        Assert.Contains(names, n => n.EndsWith(LongName, StringComparison.Ordinal));
    }

    [Fact]
    public void ArchiveImage_IndependentReader_ReadsEveryFileThroughIso9660AndUdf()
    {
        // What Windows mounts: the ISO 9660 / Joliet tree and the UDF tree. DiscUtils is an independent reader.
        string folder = StageArchiveFolder("roundtrip");
        using var image = Build(folder);

        image.Position = 0;
        using (var udf = new UdfReader(image))
        {
            AssertSameContent(folder, path => udf.OpenFile(path, FileMode.Open));
        }
        image.Position = 0;
        using (var iso = new CDReader(image, joliet: true))
        {
            AssertSameContent(folder, path => iso.OpenFile(path, FileMode.Open));
        }
    }

    // ---- Cutting the recovery volume ----------------------------------------------------------------------------

    [Fact]
    public void Split_CutsAtPacketBoundaries_EveryPartHasAWholeCriticalSet()
    {
        string folder = Path.Combine(_root, "split", "RECOVERY");
        Directory.CreateDirectory(folder);
        // A volume shaped like par2cmdline's: 10 recovery slices with two copies of the critical packets spread
        // between them, then the creator packet.
        var critical = new[] { Packet("Main", 1), Packet("FileDesc", 2), Packet("IFSC", 3) };
        var packets = new List<byte[]>();
        for (int e = 0; e < 10; e++)
        {
            packets.Add(Slice((uint)e));
            if (e is 1 or 6)
            {
                packets.AddRange(critical);
            }
        }
        packets.Add(Packet("Creator", 9));
        File.WriteAllBytes(Path.Combine(folder, "recovery.vol00+10.par2"), packets.SelectMany(p => p).ToArray());

        var parts = Par2VolumeSplitter.Split(folder);

        Assert.Equal(3, parts.Count);
        Assert.False(File.Exists(Path.Combine(folder, "recovery.vol00+10.par2")));
        var exponents = new List<uint>();
        var counts = new List<int>();
        foreach (string part in parts)
        {
            var parsed = ParsePackets(File.ReadAllBytes(part));
            var mine = parsed.Where(p => p.Type == "RecvSlic").Select(p => p.Exponent).ToList();
            exponents.AddRange(mine);
            counts.Add(mine.Count);
            // par2cmdline's naming: first exponent and number of recovery blocks in the file.
            Assert.Equal($"recovery.vol{mine.Min():D5}+{mine.Count:D5}.par2", Path.GetFileName(part));
            foreach (string type in new[] { "Main", "FileDesc", "IFSC" })
            {
                Assert.Contains(parsed, p => p.Type == type);
            }
        }
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (uint)i), exponents); // every block once, in order
        Assert.True(counts.Max() - counts.Min() <= 1, string.Join(",", counts));
    }

    [Fact]
    public void Split_LeavesAFileThatIsNotPar2_AndASingleSliceVolume_Alone()
    {
        string folder = Path.Combine(_root, "nosplit", "RECOVERY");
        Directory.CreateDirectory(folder);
        string notPar2 = Path.Combine(folder, "recovery.vol00+05.par2");
        File.WriteAllBytes(notPar2, new byte[5000]);
        Assert.Empty(Par2VolumeSplitter.Split(folder));
        Assert.True(File.Exists(notPar2));

        File.Delete(notPar2);
        string one = Path.Combine(folder, "recovery.vol00+01.par2");
        File.WriteAllBytes(one, Packet("Main", 1).Concat(Slice(0)).ToArray());
        Assert.Empty(Par2VolumeSplitter.Split(folder));
        Assert.True(File.Exists(one));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private const string LongName = "A very long file name that only the UDF tree keeps whole, past Joliet's sixty-four.txt";

    private static readonly IsoBuildOptions ArchiveOptions = new(VolumeLabel: "ARCHIVE01", IncludeJoliet: false, IncludeUdf: true,
        FixedTimestampUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>An archive tree of zero-filled files: manifest, data files and par2 files in RECOVERY.</summary>
    private static CompilationDirectory ArchiveTree(int dataFiles, long dataSectors, long recoverySectors, int volumes)
    {
        static CompilationFile Zeros(string name, long size) =>
            new(name, string.Empty, size) { StreamFactory = () => new ZeroStream(size) };
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(Zeros(ArchiveManifest.ManifestFileName, 8_192));
        root.AddFile(Zeros(ArchiveManifest.IndexFileName, 8_192));
        var photos = root.AddDirectory("photos");
        long perFile = dataSectors / dataFiles * 2048;
        for (int i = 0; i < dataFiles; i++)
        {
            photos.AddFile(Zeros($"IMG_{i:D4}.jpg", perFile));
        }
        var recovery = root.AddDirectory(ArchiveManifest.RecoveryFolderName);
        recovery.AddFile(Zeros("recovery.par2", 40_000));
        long perVolume = recoverySectors / volumes * 2048;
        for (int v = 0; v < volumes; v++)
        {
            recovery.AddFile(Zeros($"recovery.vol{v * 10:D5}+00010.par2", perVolume));
        }
        return root;
    }

    private static List<(string Path, int StartLba, int Sectors)> LayoutFiles(CompilationDirectory root)
    {
        // The UDF tree of a layout-only image would need the file data; ISO 9660 records come from the same layout.
        using var image = new MemoryStream();
        IsoUdfImageBuilder.Build(root, image, ArchiveOptions);
        image.Position = 0;
        var tree = UdfTreeReader.TryRead(image)!;
        return Flatten(tree).Select(f => (f.VirtualPath, f.StartLba, (int)((f.SizeBytes + 2047) / 2048))).ToList();
    }

    /// <summary>A staged archive folder with real files (manifest, data, and three par2-named recovery files).</summary>
    private string StageArchiveFolder(string name, bool longName = false, (string Name, byte[] Bytes)? extra = null)
    {
        string folder = Path.Combine(_root, name);
        var random = new Random(name.Length * 7919);
        Directory.CreateDirectory(Path.Combine(folder, "photos"));
        for (int i = 0; i < 6; i++)
        {
            byte[] bytes = new byte[20_000 + random.Next(60_000)];
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(folder, "photos", $"IMG_{i:D4}.jpg"), bytes);
        }
        if (longName)
        {
            File.WriteAllText(Path.Combine(folder, "photos", LongName), "long name");
        }
        if (extra is { } e)
        {
            File.WriteAllBytes(Path.Combine(folder, e.Name), e.Bytes);
        }
        File.WriteAllText(Path.Combine(folder, ArchiveManifest.ManifestFileName), "manifest");
        string recovery = Path.Combine(folder, ArchiveManifest.RecoveryFolderName);
        Directory.CreateDirectory(recovery);
        File.WriteAllBytes(Path.Combine(recovery, "recovery.par2"), new byte[3000]);
        for (int v = 0; v < 3; v++)
        {
            byte[] bytes = new byte[30_000];
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(recovery, $"recovery.vol{v * 4:D5}+00004.par2"), bytes);
        }
        return folder;
    }

    private static MemoryStream Build(string folder, bool joliet = true)
    {
        var root = CompilationDirectory.CreateRoot();
        CompilationFolderImporter.Import(root, folder, strict: true);
        return IsoUdfImageBuilder.BuildToMemoryStream(root, ArchiveOptions with { IncludeJoliet = joliet });
    }

    private static byte[] Sector(Stream image, int lba)
    {
        byte[] sector = new byte[2048];
        image.Position = (long)lba * 2048;
        image.ReadExactly(sector);
        return sector;
    }

    private static void Zero(Stream image, int lba)
    {
        image.Position = (long)lba * 2048;
        image.Write(new byte[2048]);
    }

    private static IEnumerable<IsoFileEntry> Flatten(IsoDirectoryEntry dir) =>
        dir.Files.Concat(dir.SubDirectories.SelectMany(Flatten));

    private static void AssertSameContent(string folder, IsoImageReader reader)
    {
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        Assert.Equal(files.Length, reader.AllFiles.Count);
        foreach (string file in files)
        {
            string path = "/" + Path.GetRelativePath(folder, file).Replace('\\', '/');
            var entry = reader.FindFile(path);
            Assert.NotNull(entry);
            Assert.Equal(File.ReadAllBytes(file), reader.ReadFileBytes(entry!));
        }
    }

    private static void AssertSameContent(string folder, Func<string, Stream> open)
    {
        foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            using var stream = open(Path.GetRelativePath(folder, file));
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(File.ReadAllBytes(file), copy.ToArray());
        }
    }

    /// <summary>
    /// A PAR2 2.0 packet (spec "Conventions"): magic, length, MD5 of bytes 32 to the end, recovery set id, type, body.
    /// </summary>
    private static byte[] Packet(string type, int seed, byte[]? body = null)
    {
        body ??= Enumerable.Range(0, 8).Select(i => (byte)(seed * 31 + i)).ToArray();
        byte[] packet = new byte[64 + body.Length];
        "PAR2\0PKT"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
        Encoding.ASCII.GetBytes("setid-0123456789").CopyTo(packet, 32);
        Encoding.ASCII.GetBytes(("PAR 2.0\0" + type).PadRight(16, '\0')).CopyTo(packet, 48);
        body.CopyTo(packet, 64);
        MD5.HashData(packet.AsSpan(32)).CopyTo(packet, 16);
        return packet;
    }

    private static byte[] Slice(uint exponent)
    {
        byte[] body = new byte[4 + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(body, exponent);
        body[10] = (byte)exponent;
        return Packet("RecvSlic", (int)exponent, body);
    }

    private static List<(string Type, uint Exponent)> ParsePackets(byte[] bytes)
    {
        var result = new List<(string, uint)>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            Assert.Equal("PAR2\0PKT"u8.ToArray(), bytes.AsSpan(offset, 8).ToArray());
            int length = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8));
            Assert.Equal(bytes.AsSpan(offset + 16, 16).ToArray(), MD5.HashData(bytes.AsSpan(offset + 32, length - 32)));
            string type = Encoding.ASCII.GetString(bytes, offset + 56, 8).TrimEnd('\0');
            uint exponent = type == "RecvSlic" ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 64)) : 0;
            result.Add((type, exponent));
            offset += length;
        }
        return result;
    }

    /// <summary>A read-only stream of zeros of a given length (file data for layout tests, never held in memory).</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Max(0, Math.Min(count, length - _position));
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
