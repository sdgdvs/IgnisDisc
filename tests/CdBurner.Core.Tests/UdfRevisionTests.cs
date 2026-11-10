using System.Security.Cryptography;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Roadmap M1.4: the builder writes UDF 1.02, 1.50, 2.00, 2.01, 2.50 and 2.60. Every image is read back through
/// <see cref="UdfTestWalker"/>, which checks each descriptor and reads every file from the UDF side alone.
/// </summary>
public class UdfRevisionTests
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<ushort> Revisions => new() { 0x0102, 0x0150, 0x0200, 0x0201, 0x0250, 0x0260 };

    private static (CompilationDirectory Root, Dictionary<string, byte[]> Expected) CreateTree(int seed)
    {
        var rng = new Random(seed);
        var root = CompilationDirectory.CreateRoot();
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var dirs = new List<(CompilationDirectory Dir, string Path)> { (root, "") };
        for (int d = 0; d < rng.Next(1, 8); d++)
        {
            var (parent, parentPath) = dirs[rng.Next(dirs.Count)];
            string name = d % 3 == 0 ? $"Ordner {d} ü" : d % 3 == 1 ? $"目录 {d}" : $"dir{d}";
            dirs.Add((parent.AddDirectory(name), parentPath + "/" + name));
        }
        // Enough files in one directory to make its identifier stream span several sectors.
        int fileCount = rng.Next(5, 90);
        for (int f = 0; f < fileCount; f++)
        {
            byte[] data = new byte[f % 9 == 0 ? 0 : rng.Next(1, 70_000)];
            rng.NextBytes(data);
            var (dir, path) = dirs[f % 4 == 0 ? 0 : rng.Next(dirs.Count)];
            string name = $"file {f} with a fairly long name {new string('x', f % 40)}.{(f % 2 == 0 ? "bin" : "txt")}";
            dir.AddFile(name, data);
            expected[path + "/" + name] = data;
        }
        return (root, expected);
    }

    private static byte[] Build(CompilationDirectory root, ushort revision, bool joliet = true, bool manifests = false) =>
        IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(
            IncludeJoliet: joliet, IncludeUdf: true, UdfRevision: revision, FixedTimestampUtc: FixedTime,
            WriteSha256Manifest: manifests)).ToArray();

    [Theory]
    [MemberData(nameof(Revisions))]
    public void EveryRevision_UdfTreeReadsBackExactly_AndDescriptorsMatchTheRevision(ushort revision)
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            var (root, expected) = CreateTree(seed);
            var udf = new UdfTestWalker(Build(root, revision, joliet: seed % 2 == 0));

            Assert.Equal(revision, udf.Revision);
            Assert.Equal((revision, revision, revision), udf.LvidRevisions);
            Assert.Equal(revision >= 0x0200 ? "NSR03" : "NSR02", udf.NsrIdentifier);
            Assert.Equal(revision >= 0x0200, udf.UsedExtendedFileEntries);
            Assert.Equal(revision < 0x0200, udf.UsedPlainFileEntries);
            Assert.Equal(revision >= 0x0250 ? 2 : 1, udf.PartitionMapCount);
            Assert.Equal(revision >= 0x0250 ? 1 : 0, udf.FsdPartitionReference);

            Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), udf.Files.Keys);
            foreach (var (path, data) in expected)
            {
                Assert.True(data.AsSpan().SequenceEqual(udf.Files[path]), $"{path} differs (seed {seed})");
            }
        }
    }

    [Theory]
    [InlineData((ushort)0x0250)]
    [InlineData((ushort)0x0260)]
    public void MetadataPartition_MirrorIsIdenticalToTheMetadataFile(ushort revision)
    {
        var (root, _) = CreateTree(3);
        var udf = new UdfTestWalker(Build(root, revision));
        Assert.NotNull(udf.MetadataFileData);
        Assert.True(udf.MetadataFileData!.Length % (32 * 2048) == 0, "metadata is allocated in 64 KiB units");
        Assert.Equal(udf.MetadataFileData, udf.MetadataMirrorData);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void EveryRevision_IsoSideAndHashListStillAgree(ushort revision)
    {
        var (root, expected) = CreateTree(5);
        byte[] image = Build(root, revision, manifests: true);
        using var reader = IsoImageReader.FromStream(new MemoryStream(image));
        // The bridge: ISO/Joliet and UDF share the data. Joliet holds names of up to 64 characters, so longer ones
        // are only checked on the UDF side.
        var checkable = expected.Where(e => e.Key.Split('/').All(part => part.Length <= 64)).ToList();
        Assert.NotEmpty(checkable);
        foreach (var (path, data) in checkable)
        {
            Assert.Equal(data, reader.ReadFileBytes(path));
        }
        var udf = new UdfTestWalker(image);
        Assert.Contains("/SHA256SUMS", udf.Files.Keys);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void EveryRevision_IsReproducibleWithAFixedTime(ushort revision)
    {
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Build(CreateTree(8).Root, revision))),
            Convert.ToHexString(SHA256.HashData(Build(CreateTree(8).Root, revision))));
    }

    [Theory]
    [InlineData((ushort)0x0102)]
    [InlineData((ushort)0x0201)]
    [InlineData((ushort)0x0250)]
    public void Walker_RejectsAnImageWithOneDamagedDescriptor(ushort revision)
    {
        // Proves the walker's checks bite: one changed byte in any FSD/FID/FE/EFE descriptor must fail it.
        byte[] image = Build(CreateTree(2).Root, revision);
        var tagged = Enumerable.Range(0, image.Length / 2048)
            .Where(lba => BitConverter.ToUInt16(image, lba * 2048) is 256 or 257 or 261 or 266 && image[lba * 2048 + 1] == 1)
            .ToList();
        Assert.True(tagged.Count > 5);
        foreach (int lba in tagged.Where((_, i) => i % 3 == 0))
        {
            byte[] damaged = (byte[])image.Clone();
            damaged[lba * 2048 + 40] ^= 0x01;
            Assert.ThrowsAny<Exception>(() => new UdfTestWalker(damaged));
        }
    }

    [Fact]
    public void DefaultOptions_StillWriteUdf102()
    {
        var root = CompilationDirectory.CreateRoot();
        var videoTs = root.AddDirectory("VIDEO_TS");
        videoTs.AddFile("VIDEO_TS.IFO", new byte[4096]);
        videoTs.AddFile("VIDEO_TS.BUP", new byte[4096]);
        var udf = new UdfTestWalker(IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeJoliet: false)).ToArray());
        Assert.Equal(0x0102, udf.Revision);
        Assert.Equal("NSR02", udf.NsrIdentifier);
    }

    [Theory]
    [InlineData((ushort)0x0101)]
    [InlineData((ushort)0x0300)]
    public void UnsupportedRevision_IsRefused(ushort revision)
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("a.txt", new byte[] { 1 });
        var ex = Assert.Throws<ArgumentException>(() =>
            IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(UdfRevision: revision)));
        Assert.Contains("2.50", ex.Message);
    }

    [Fact]
    public void EmptyCompilation_WorksInEveryRevision()
    {
        foreach (ushort revision in IsoUdfImageBuilder.SupportedUdfRevisions)
        {
            var udf = new UdfTestWalker(Build(CompilationDirectory.CreateRoot(), revision));
            Assert.Empty(udf.Files);
        }
    }
}
