using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CdBurner.Core.Archival;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>Packing of files onto discs (first-fit-decreasing, folders, path order) and the catalog on every disc.</summary>
public sealed class ArchiveSpanningTests : IDisposable
{
    private const int Ceiling = 1000; // sectors per disc in the planner tests; with 0 % redundancy this is also the usable space
    private readonly List<string> _tempDirs = new();

    private static ArchiveSourceFile File_(string path, int sectors) =>
        new("/src/" + path, path, sectors * (long)OpticalConstants.DataSectorSize);

    private static ArchivePlan Plan(IReadOnlyList<ArchiveSourceFile> files, ArchivePackingMode mode, int ceiling = Ceiling) =>
        ArchiveSpanPlanner.Plan(files, DiscMediaType.CdR, 0, "span", ceilingSectorsOverride: ceiling, packing: mode);

    private static List<ArchiveSourceFile> RandomFiles(int seed, int count, int maxSectors)
    {
        var rng = new Random(seed);
        var files = new List<ArchiveSourceFile>();
        for (int i = 0; i < count; i++)
        {
            string folder = "dir" + rng.Next(0, 6);
            long bytes = rng.Next(0, 5) == 0 ? 0 : (long)rng.Next(1, maxSectors * OpticalConstants.DataSectorSize);
            files.Add(new ArchiveSourceFile("/src/" + i, $"{folder}/file{i:D3}.bin", bytes));
        }

        return files;
    }

    public static IEnumerable<object[]> Cases() =>
        from seed in new[] { 1, 2, 3, 4, 5, 6, 7, 8 }
        from mode in Enum.GetValues<ArchivePackingMode>()
        select new object[] { seed, mode };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Plan_PlacesEveryFileOnceWithinTheDiscAndIsRepeatable(int seed, ArchivePackingMode mode)
    {
        var files = RandomFiles(seed, count: 120, maxSectors: 420);

        var plan = Plan(files, mode);
        var again = Plan(files.AsEnumerable().Reverse().ToList(), mode);

        // The union of the discs is the input, each file exactly once (no file split, none lost, none repeated).
        var placed = plan.Discs.SelectMany(d => d.Files).Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal), placed);

        foreach (var disc in plan.Discs)
        {
            long sectors = disc.Files.Sum(f => (f.SizeBytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);
            Assert.Equal(sectors, disc.DataSectors);
            Assert.True(disc.DataSectors <= plan.UsableSectorsPerDisc, $"disc {disc.DiscNumber}: {disc.DataSectors} > {plan.UsableSectorsPerDisc}");
            Assert.Equal(disc.Files.Sum(f => f.SizeBytes), disc.DataBytes);
        }

        // The same input gives the same plan, whatever order the files arrive in.
        Assert.Equal(
            plan.Discs.Select(d => d.Files.Select(f => f.RelativePath).ToList()),
            again.Discs.Select(d => d.Files.Select(f => f.RelativePath).ToList()));
    }

    [Fact]
    public void Plan_SameSizesTieBreakByOrdinalPath()
    {
        // Three files of 400 sectors, two per disc: the two first in ordinal path order share disc 1.
        var files = new[] { File_("c.bin", 400), File_("B.bin", 400), File_("a.bin", 400) };

        var plan = Plan(files, ArchivePackingMode.FirstFitDecreasing);

        // Ordinal order puts upper case before lower case: B.bin, a.bin, c.bin.
        Assert.Equal(new[] { "B.bin", "a.bin" }, plan.Discs[0].Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(new[] { "c.bin" }, plan.Discs[1].Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void FirstFitDecreasing_UsesFewerDiscsThanPathOrderWhereOrderIsUnlucky()
    {
        // Sizes in path order 500, 560, 420, 480 sectors on discs of about 987 sectors. Path order: [500], [560 420], [480] = 3.
        // Largest first: [560 420], [500 480] = 2. The optimum is 2 (the total is 1960 of 2 x 987).
        var files = new[] { File_("a", 500), File_("b", 560), File_("c", 420), File_("d", 480) };

        Assert.Equal(3, Plan(files, ArchivePackingMode.PathOrder).Discs.Count);
        Assert.Equal(2, Plan(files, ArchivePackingMode.FirstFitDecreasing).Discs.Count);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void FirstFitDecreasing_NeverUsesMoreDiscsThanPathOrderOnTheFixtures(int seed)
    {
        var files = RandomFiles(seed, count: 200, maxSectors: 600);

        int ffd = Plan(files, ArchivePackingMode.FirstFitDecreasing).Discs.Count;
        int nextFit = Plan(files, ArchivePackingMode.PathOrder).Discs.Count;

        Assert.True(ffd <= nextFit, $"first-fit-decreasing used {ffd} discs, path order {nextFit}");
    }

    [Fact]
    public void PathOrder_KeepsTheOriginalNextFitBehaviour()
    {
        // 150,000-sector files, two per CD-R: the case the original loop was written for.
        long size = 150_000L * OpticalConstants.DataSectorSize;
        var files = Enumerable.Range(1, 5).Select(i => new ArchiveSourceFile($"/d/f{i}", $"file{i:D2}.bin", size)).ToList();

        var plan = ArchiveSpanPlanner.Plan(files, DiscMediaType.CdR, 0, "span", packing: ArchivePackingMode.PathOrder);

        Assert.Equal(new[] { 2, 2, 1 }, plan.Discs.Select(d => d.Files.Count));
        Assert.Equal(files.Select(f => f.RelativePath), plan.Discs.SelectMany(d => d.Files).Select(f => f.RelativePath));
    }

    [Fact]
    public void KeepFolders_PlacesAFolderThatFitsOnOneDisc()
    {
        // Folder a and folder b hold three 200-sector files each, folder c two of 190; a disc takes about 987 sectors.
        var files = new List<ArchiveSourceFile>();
        foreach (string folder in new[] { "a", "b" })
        {
            for (int i = 1; i <= 3; i++) files.Add(File_($"{folder}/{i}.bin", 200));
        }
        for (int i = 1; i <= 2; i++) files.Add(File_($"c/{i}.bin", 190));

        var plain = Plan(files, ArchivePackingMode.FirstFitDecreasing);
        var kept = Plan(files, ArchivePackingMode.KeepFolders);

        // Largest first mixes the folders: folder b is cut across two discs.
        Assert.True(DiscsHolding(plain, "b/").Count > 1);
        // Keeping folders: each folder is on exactly one disc, and no more discs are needed here.
        foreach (string folder in new[] { "a/", "b/", "c/" })
        {
            Assert.Single(DiscsHolding(kept, folder));
        }
        Assert.Equal(plain.Discs.Count, kept.Discs.Count);
    }

    [Fact]
    public void KeepFolders_OpensAFolderThatIsTooBigForOneDisc()
    {
        // Folder big holds 1,400 sectors in 7 files and cannot be whole; its files are still placed, none split.
        var files = Enumerable.Range(1, 7).Select(i => File_($"big/{i}.bin", 200)).Append(File_("small/x.bin", 100)).ToList();

        var plan = Plan(files, ArchivePackingMode.KeepFolders);

        Assert.Equal(8, plan.Discs.SelectMany(d => d.Files).Count());
        Assert.All(plan.Discs, d => Assert.True(d.DataSectors <= plan.UsableSectorsPerDisc));
    }

    private static List<int> DiscsHolding(ArchivePlan plan, string prefix) =>
        plan.Discs.Where(d => d.Files.Any(f => f.RelativePath.StartsWith(prefix, StringComparison.Ordinal))).Select(d => d.DiscNumber).ToList();

    [Theory]
    [InlineData("CATALOG.JSON")]
    [InlineData("index.html")]
    public void Plan_RefusesASourceFileNamedLikeAGeneratedFile(string name)
    {
        var files = new[] { File_(name, 1) };

        var ex = Assert.Throws<InvalidOperationException>(() => Plan(files, ArchivePackingMode.FirstFitDecreasing));

        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Plan_KeepsRoomForTheCatalogOnEveryDisc()
    {
        // One file that fills the disc to within a sector of the usable space leaves nothing for the catalog files;
        // the planner must not pair it with anything else, and a lone file still fits (it is placed on its own disc).
        var files = new[] { File_("a", 995), File_("b", 4) };

        var plan = Plan(files, ArchivePackingMode.FirstFitDecreasing);

        Assert.Equal(2, plan.Discs.Count);
    }

    // ---- CATALOG.JSON and INDEX.HTML on staged discs ----

    private string TempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "cdburner-span-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private async Task<(ArchivePlan Plan, IReadOnlyList<string> Discs, Dictionary<string, byte[]> Contents)> StageThreeDiscsAsync()
    {
        string source = TempDir();
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Three files of about 600 sectors each on discs of 1,000: one per disc.
        foreach (string name in new[] { "one/a & b.bin", "two/it's&.bin", "three/éclair.bin" })
        {
            byte[] data = new byte[600 * 2048 - 5];
            new Random(name.Length).NextBytes(data);
            string path = Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, data);
            contents[name] = data;
        }

        var plan = ArchiveSpanPlanner.Plan(source, DiscMediaType.CdR, 0, "catset", ceilingSectorsOverride: Ceiling);
        var discs = await ArchiveStager.StageAsync(plan, TempDir(), par2Path: null);
        return (plan, discs, contents);
    }

    [Fact]
    public async Task EveryDisc_CarriesTheSameCatalogOfTheWholeSet()
    {
        var (plan, discs, contents) = await StageThreeDiscsAsync();
        Assert.Equal(3, discs.Count);

        string firstJson = await File.ReadAllTextAsync(Path.Combine(discs[0], ArchiveCatalog.CatalogFileName));
        foreach (string disc in discs)
        {
            Assert.Equal(firstJson, await File.ReadAllTextAsync(Path.Combine(disc, ArchiveCatalog.CatalogFileName)));
            Assert.True(File.Exists(Path.Combine(disc, ArchiveCatalog.HtmlIndexFileName)));
        }

        var catalog = ArchiveCatalog.ParseJson(firstJson)!;
        Assert.Equal("catset", catalog.SetId);
        Assert.Equal(3, catalog.DiscCount);
        Assert.Equal(contents.Count, catalog.Files.Count);
        foreach (var entry in catalog.Files)
        {
            // Checked against the bytes written by the test, not against the stager's own hashing.
            byte[] data = contents[entry.Path];
            Assert.Equal(data.Length, entry.SizeBytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), entry.Sha256);
            Assert.True(File.Exists(Path.Combine(discs[entry.DiscNumber - 1], entry.Path.Replace('/', Path.DirectorySeparatorChar))),
                $"{entry.Path} is not on disc {entry.DiscNumber}");
        }

        Assert.Equal(3, catalog.Files.Select(f => f.DiscNumber).Distinct().Count());
    }

    [Fact]
    public async Task IndexHtml_OnDiscOne_FindsAFileThatIsOnDiscThree()
    {
        var (_, discs, _) = await StageThreeDiscsAsync();

        string html = await File.ReadAllTextAsync(Path.Combine(discs[0], ArchiveCatalog.HtmlIndexFileName));

        // Names are HTML-escaped, the table is in the page itself (readable with scripts off), and each row names its disc.
        Assert.Contains("one/a &amp; b.bin", html);
        // The files are equal in size, so ties break by ordinal path: one/ is disc 1, three/ disc 2, two/ disc 3.
        Assert.Contains("<td class=\"num\">3</td><td>two/it&#39;s&amp;.bin</td>", html);
        Assert.Matches(new Regex(@"<td class=""num"">2</td><td>three/[^<]*clair\.bin</td>"), html);
        Assert.Contains("This is disc 1 of 3", html);
        Assert.Contains("<script>", html);
    }

    [Fact]
    public async Task IndexHtml_HasNoNetworkReferences()
    {
        var (_, discs, _) = await StageThreeDiscsAsync();

        foreach (string disc in discs)
        {
            string html = await File.ReadAllTextAsync(Path.Combine(disc, ArchiveCatalog.HtmlIndexFileName));
            Assert.DoesNotMatch(new Regex(@"https?:|ftp:|//[a-z0-9.-]+\.[a-z]{2,}|\bsrc\s*=|\bhref\s*=|url\s*\(|@import|<link|<iframe|<img|<object|<embed|fetch\s*\(|XMLHttpRequest|import\s*\(|WebSocket", RegexOptions.IgnoreCase), html);
            Assert.StartsWith("<!DOCTYPE html>", html);
        }
    }

    [Fact]
    public async Task CatalogAndIndex_AreListedInTheManifestAndCrossChecked()
    {
        var (_, discs, _) = await StageThreeDiscsAsync();
        string disc = discs[1];

        string manifest = await File.ReadAllTextAsync(Path.Combine(disc, ArchiveManifest.ManifestFileName));
        Assert.Contains("  " + ArchiveCatalog.CatalogFileName + "\n", manifest);
        Assert.Contains("  " + ArchiveCatalog.HtmlIndexFileName + "\n", manifest);
        string indexJson = await File.ReadAllTextAsync(Path.Combine(disc, ArchiveManifest.IndexFileName));
        Assert.Contains(ArchiveCatalog.CatalogFileName, indexJson);
        Assert.Contains(ArchiveCatalog.HtmlIndexFileName, indexJson);
        Assert.True((await ArchiveManifest.VerifyAsync(disc)).IsIntact);

        // A catalog altered after staging is caught.
        string catalogPath = Path.Combine(disc, ArchiveCatalog.CatalogFileName);
        await File.AppendAllTextAsync(catalogPath, " ");
        var changed = await ArchiveManifest.VerifyAsync(disc);
        Assert.Contains(ArchiveCatalog.CatalogFileName, changed.Corrupt);
    }

    [Fact]
    public async Task Catalog_DroppedFromTheManifest_IsStillCheckedThroughTheIndex()
    {
        var (_, discs, _) = await StageThreeDiscsAsync();
        string disc = discs[0];
        string manifestPath = Path.Combine(disc, ArchiveManifest.ManifestFileName);
        var kept = (await File.ReadAllLinesAsync(manifestPath)).Where(l => !l.EndsWith(ArchiveCatalog.HtmlIndexFileName, StringComparison.Ordinal));
        await File.WriteAllLinesAsync(manifestPath, kept);

        var result = await ArchiveManifest.VerifyAsync(disc);

        Assert.False(result.IsIntact);
        Assert.Contains(result.Corrupt, c => c.Contains("absent", StringComparison.Ordinal));

        // The listed-but-not-in-manifest file was still hashed against the index's hash: deleting it is reported.
        File.Delete(Path.Combine(disc, ArchiveCatalog.HtmlIndexFileName));
        var missing = await ArchiveManifest.VerifyAsync(disc);
        Assert.Contains(ArchiveCatalog.HtmlIndexFileName, missing.Missing);
    }

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
