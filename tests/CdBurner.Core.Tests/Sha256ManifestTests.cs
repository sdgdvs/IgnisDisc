using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Roadmap M1.2: a root <c>SHA256SUMS</c> (GNU coreutils format) listing every other file, hashed while the files
/// stream into the image so the source tree is read only once.
/// </summary>
public class Sha256ManifestTests
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Counts how many times each file's data is opened.</summary>
    private sealed class ReadCounter
    {
        public Dictionary<string, int> Opens { get; } = new();

        public void Add(CompilationDirectory dir, string name, byte[] data)
        {
            string key = dir.Name + "/" + name;
            Opens[key] = 0;
            dir.AddFile(new CompilationFile(name, data.Length, () =>
            {
                Opens[key]++;
                return new MemoryStream(data, writable: false);
            }));
        }
    }

    private static CompilationDirectory CreateTree(int seed, ReadCounter? counter = null)
    {
        var rng = new Random(seed);
        var root = CompilationDirectory.CreateRoot();
        var dirs = new List<CompilationDirectory> { root };
        for (int d = 0; d < rng.Next(0, 5); d++)
        {
            dirs.Add(dirs[rng.Next(dirs.Count)].AddDirectory($"Folder {d} ü"));
        }
        int fileCount = rng.Next(1, 30);
        for (int f = 0; f < fileCount; f++)
        {
            byte[] data = new byte[f % 7 == 0 ? 0 : rng.Next(1, 20000)];
            rng.NextBytes(data);
            var dir = dirs[rng.Next(dirs.Count)];
            string name = $"file {f} 文件.{(f % 2 == 0 ? "txt" : "bin")}";
            if (counter != null)
            {
                counter.Add(dir, name, data);
            }
            else
            {
                dir.AddFile(name, data);
            }
        }
        return root;
    }

    private static byte[] Build(CompilationDirectory root, bool joliet = true, bool udf = true, DateTime? fixedTime = null)
    {
        var options = new IsoBuildOptions(IncludeJoliet: joliet, IncludeUdf: udf, FixedTimestampUtc: fixedTime, WriteSha256Manifest: true);
        using var stream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);
        return stream.ToArray();
    }

    private static List<(string Hash, string Path)> ParseManifest(byte[] manifest)
    {
        string text = Encoding.UTF8.GetString(manifest);
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r", text);
        var lines = new List<(string, string)>();
        foreach (string line in text.TrimEnd('\n').Split('\n'))
        {
            // GNU text-mode line: 64 lowercase hex digits, two spaces, path.
            Assert.Matches("^[0-9a-f]{64}  .+$", line);
            lines.Add((line[..64], line[66..]));
        }
        return lines;
    }

    [Fact]
    public void RandomTrees_ManifestListsEveryFileWithTheHashOfItsBytesInTheImage()
    {
        for (int seed = 1; seed <= 30; seed++)
        {
            byte[] image = Build(CreateTree(seed));
            using var reader = IsoImageReader.FromStream(new MemoryStream(image));

            var entries = ParseManifest(reader.ReadFileBytes("/" + IsoUdfImageBuilder.Sha256ManifestFileName));
            var listed = entries.Select(e => e.Path).ToList();
            var onDisc = reader.AllFiles
                .Select(f => f.VirtualPath.TrimStart('/'))
                .Where(p => p != IsoUdfImageBuilder.Sha256ManifestFileName)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(onDisc, listed); // every file, nothing extra, sorted, manifest not listing itself
            foreach (var (hash, path) in entries)
            {
                Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(reader.ReadFileBytes("/" + path))).ToLowerInvariant());
            }
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void EveryFileSystemCombination_ManifestMatchesFiles(bool joliet, bool udf)
    {
        byte[] image = Build(CreateTree(5), joliet, udf);
        using var reader = IsoImageReader.FromStream(new MemoryStream(image));
        // The reader shows Joliet names, or ISO 9660 Level 1 (8.3) names on a disc without Joliet: "SHA256SU".
        var manifestEntry = reader.AllFiles.Single(f =>
            f.VirtualPath.TrimStart('/').StartsWith("SHA256SU", StringComparison.OrdinalIgnoreCase));
        var entries = ParseManifest(reader.ReadFileBytes(manifestEntry));
        Assert.Equal(reader.AllFiles.Count - 1, entries.Count);
    }

    [Fact]
    public void SourceFilesAreReadExactlyOnce()
    {
        var counter = new ReadCounter();
        Build(CreateTree(9, counter));
        Assert.NotEmpty(counter.Opens);
        foreach (var (file, opens) in counter.Opens)
        {
            // Empty files are never opened; every other file is opened once, for writing and hashing together.
            Assert.True(opens <= 1, $"{file} was opened {opens} times");
        }
        Assert.Contains(counter.Opens.Values, v => v == 1);
    }

    [Fact]
    public void EmptyFilesAreListedWithTheHashOfNoBytes()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("empty.txt", Array.Empty<byte>());
        root.AddFile("one.txt", "1"u8.ToArray());
        using var reader = IsoImageReader.FromStream(new MemoryStream(Build(root)));
        var entries = ParseManifest(reader.ReadFileBytes("/SHA256SUMS"));
        Assert.Contains(("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "empty.txt"), entries);
    }

    [Fact]
    public void WithFixedTime_ImagesWithManifestAreStillByteIdentical()
    {
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Build(CreateTree(3), fixedTime: FixedTime))),
            Convert.ToHexString(SHA256.HashData(Build(CreateTree(3), fixedTime: FixedTime))));
    }

    [Fact]
    public void ExistingRootItemNamedSha256Sums_IsRefusedInsteadOfOverwritten()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("sha256sums", "mine"u8.ToArray());
        var ex = Assert.Throws<InvalidOperationException>(() => Build(root));
        Assert.Contains("already has an item named", ex.Message);
    }

    [Fact]
    public void AppendedSession_IsRefusedBeforeAnythingIsWritten()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("a.txt", "a"u8.ToArray());
        using var output = new MemoryStream();
        Assert.Throws<NotSupportedException>(() =>
            IsoUdfImageBuilder.Build(root, output, new IsoBuildOptions(IncludeUdf: false, WriteSha256Manifest: true), sessionStartLba: 20000));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void WithoutTheOption_NoManifestIsAdded()
    {
        var root = CreateTree(4);
        using var stream = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions());
        using var reader = IsoImageReader.FromStream(new MemoryStream(stream.ToArray()));
        Assert.DoesNotContain(reader.AllFiles, f => f.VirtualPath.Contains("SHA256SUMS", StringComparison.OrdinalIgnoreCase));
    }
}
