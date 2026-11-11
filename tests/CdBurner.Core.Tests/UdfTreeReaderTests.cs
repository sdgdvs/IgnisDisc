using System.Buffers.Binary;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// The production UDF reader used by <c>recover</c>: it must list exactly what the strict test walker reads, with
/// full names, and still find the files when a descriptor is damaged, by using the backups UDF keeps.
/// </summary>
public class UdfTreeReaderTests
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<ushort> Revisions => new() { 0x0102, 0x0150, 0x0200, 0x0201, 0x0250, 0x0260 };

    private static (byte[] Image, Dictionary<string, byte[]> Expected) Build(ushort revision, bool joliet = false)
    {
        var rng = new Random(revision);
        var root = CompilationDirectory.CreateRoot();
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sub = root.AddDirectory("Schaffer, Riley, and the Double Ice Backfire - Hold Person (2026)");
        for (int f = 0; f < 40; f++)
        {
            byte[] data = new byte[f % 7 == 0 ? 0 : rng.Next(1, 90_000)];
            rng.NextBytes(data);
            // Names past Joliet's 64-character limit, with characters outside Latin-1.
            string name = $"{f:D2} A track title that is a good deal longer than sixty-four characters ( ͡° ͜ʖ ͡°).mp3";
            (f % 2 == 0 ? root : sub).AddFile(name, data);
            expected[(f % 2 == 0 ? "" : "/" + sub.Name) + "/" + name] = data;
        }
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(
            IncludeJoliet: joliet, IncludeUdf: true, UdfRevision: revision, FixedTimestampUtc: FixedTime)).ToArray();
        return (image, expected);
    }

    private static Dictionary<string, byte[]> ReadAll(byte[] image, List<string>? skipped = null)
    {
        using var stream = new MemoryStream(image, writable: false);
        var root = UdfTreeReader.TryRead(stream, skipped);
        Assert.NotNull(root);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Walk(IsoDirectoryEntry dir)
        {
            foreach (var file in dir.Files)
            {
                files[file.VirtualPath] = image.AsSpan(file.StartLba * 2048, (int)file.SizeBytes).ToArray();
            }
            foreach (var d in dir.SubDirectories) Walk(d);
        }
        Walk(root!);
        return files;
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void EveryRevision_ListsEveryFileWithItsFullName(ushort revision)
    {
        var (image, expected) = Build(revision);

        var files = ReadAll(image);

        Assert.Equal(expected.Keys.Order(), files.Keys.Order());
        foreach (var (path, data) in expected)
        {
            Assert.True(data.AsSpan().SequenceEqual(files[path]), path);
        }
        // Agrees with the strict walker.
        var walker = new UdfTestWalker(image);
        Assert.Equal(walker.Files.Keys.Order(), files.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void DamagedFirstAnchor_UsesTheOneAtTheEnd(ushort revision)
    {
        var (image, expected) = Build(revision);
        Array.Clear(image, 256 * 2048, 2048);
        Assert.Equal(expected.Count, ReadAll(image).Count);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void DamagedMainDescriptorSequence_UsesTheReserveCopy(ushort revision)
    {
        var (image, expected) = Build(revision);
        uint mainVds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256 * 2048 + 20));
        Array.Clear(image, (int)mainVds * 2048, 6 * 2048);
        Assert.Equal(expected.Count, ReadAll(image).Count);
    }

    [Theory]
    [InlineData((ushort)0x0250)]
    [InlineData((ushort)0x0260)]
    public void DamagedMetadataFile_UsesTheMirror(ushort revision)
    {
        var (image, expected) = Build(revision);
        var (partitionStart, metadataFileLbn) = MetadataFileLocation(image);
        Array.Clear(image, (partitionStart + (int)metadataFileLbn) * 2048, 2048);
        Assert.Equal(expected.Count, ReadAll(image).Count);
    }

    [Fact]
    public void DamagedFileEntry_SkipsThatFileOnly()
    {
        var (image, expected) = Build(0x0102);
        // The FSD sits at the start of the partition; the root directory's entry follows it, then files' entries.
        // Damage the last file entry before the first file's data by zeroing the sector right before that data.
        var walkerFiles = ReadAll(image);
        using var stream = new MemoryStream(image, writable: false);
        var root = UdfTreeReader.TryRead(stream)!;
        int firstData = root.Files.Where(f => f.SizeBytes > 0).Min(f => f.StartLba);
        Array.Clear(image, (firstData - 1) * 2048, 2048);

        var skipped = new List<string>();
        var files = ReadAll(image, skipped);

        Assert.Equal(expected.Count - 1, files.Count);
        Assert.Single(skipped);
        Assert.Equal(walkerFiles.Count, expected.Count);
    }

    [Fact]
    public void ImageWithoutUdf_GivesNull()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("a.txt", new byte[100]);
        byte[] image = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false)).ToArray();
        using var stream = new MemoryStream(image);
        Assert.Null(UdfTreeReader.TryRead(stream));
        using var empty = new MemoryStream(new byte[300 * 2048]);
        Assert.Null(UdfTreeReader.TryRead(empty));
    }

    /// <summary>Partition start and the Metadata File entry's block, from the Partition and Logical Volume Descriptors.</summary>
    private static (int PartitionStart, uint MetadataFileLbn) MetadataFileLocation(byte[] image)
    {
        uint vds = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256 * 2048 + 20));
        int partitionStart = -1;
        uint fileLbn = 0;
        for (uint i = 0; i < 16; i++)
        {
            var d = image.AsSpan((int)(vds + i) * 2048, 2048);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (id == 5) partitionStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[188..]);
            if (id == 6) fileLbn = BinaryPrimitives.ReadUInt32LittleEndian(d[(440 + 6 + 40)..]); // after the 6-byte type 1 map
            if (id == 8) break;
        }
        return (partitionStart, fileLbn);
    }
}
