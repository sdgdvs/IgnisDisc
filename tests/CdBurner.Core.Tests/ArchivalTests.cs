using System.Text;
using CdBurner.Core.Archival;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class ArchivalTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void CalculateUsableSectors_ReservesHeadroomAndRedundancy()
    {
        // CdR nominal capacity: 360,000 sectors.
        // Reserve: Math.Max(4096, 360,000 / 50) = 7,200 sectors.
        // Usable with 0% redundancy: (360,000 - 7,200) / 1.0 = 352,800.
        int usable0 = ArchiveSpanPlanner.CalculateUsableSectors(DiscMediaType.CdR, 0);
        Assert.Equal(352800, usable0);

        // Usable with 10% redundancy: (360,000 - 7,200) / 1.1 = 320,727.
        int usable10 = ArchiveSpanPlanner.CalculateUsableSectors(DiscMediaType.CdR, 10);
        Assert.Equal(320727, usable10);
    }

    [Fact]
    public void Plan_SplitsSequentiallyAndKeepsOrder()
    {
        int usable = ArchiveSpanPlanner.CalculateUsableSectors(DiscMediaType.CdR, 0); // 352,800 sectors
        // 5 synthetic files of 150,000 sectors each.
        // Disc 1: file01, file02 (300,000 <= 352,800).
        // Disc 2: file03, file04 (300,000 <= 352,800).
        // Disc 3: file05 (150,000 <= 352,800).
        long fileSize = 150_000L * OpticalConstants.DataSectorSize;
        var files = new List<ArchiveSourceFile>
        {
            new("/dummy/file01.bin", "file01.bin", fileSize),
            new("/dummy/file02.bin", "file02.bin", fileSize),
            new("/dummy/file03.bin", "file03.bin", fileSize),
            new("/dummy/file04.bin", "file04.bin", fileSize),
            new("/dummy/file05.bin", "file05.bin", fileSize),
        };

        var plan = ArchiveSpanPlanner.Plan(files, DiscMediaType.CdR, 0, setId: "testset12345");

        Assert.Equal(3, plan.Discs.Count);

        var plannedFiles = plan.Discs.SelectMany(d => d.Files).ToList();
        Assert.Equal(files.Count, plannedFiles.Count);
        for (int i = 0; i < files.Count; i++)
        {
            Assert.Equal(files[i].RelativePath, plannedFiles[i].RelativePath);
        }

        foreach (var disc in plan.Discs)
        {
            Assert.True(disc.DataSectors <= usable, $"Disc {disc.DiscNumber} exceeded usable sectors: {disc.DataSectors} > {usable}");
        }

        Assert.Equal(2, plan.Discs[0].Files.Count);
        Assert.Equal(2, plan.Discs[1].Files.Count);
        Assert.Single(plan.Discs[2].Files);
    }

    [Fact]
    public void Plan_FileLargerThanDisc_Throws()
    {
        int usable = ArchiveSpanPlanner.CalculateUsableSectors(DiscMediaType.CdR, 0);
        long oversizedBytes = (usable + 1L) * OpticalConstants.DataSectorSize;
        var files = new List<ArchiveSourceFile>
        {
            new("/dummy/hugefile.iso", "hugefile.iso", oversizedBytes)
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ArchiveSpanPlanner.Plan(files, DiscMediaType.CdR, 0));

        Assert.Contains("hugefile.iso", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("larger than one disc", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Plan_InvalidRedundancy_Throws(int redundancy)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ArchiveSpanPlanner.Plan(Array.Empty<ArchiveSourceFile>(), DiscMediaType.CdR, redundancy));
    }

    [Fact]
    public void Plan_FromFolder_UsesForwardSlashRelativePaths()
    {
        string root = CreateTempDirectory();
        string subDir = Path.Combine(root, "folder1", "nested");
        Directory.CreateDirectory(subDir);

        File.WriteAllText(Path.Combine(root, "root_file.txt"), "hello");
        File.WriteAllText(Path.Combine(subDir, "deep_file.txt"), "world");

        var plan = ArchiveSpanPlanner.Plan(root, DiscMediaType.CdR, 0);

        Assert.Single(plan.Discs);
        var disc = plan.Discs[0];
        Assert.Equal(2, disc.Files.Count);

        foreach (var file in disc.Files)
        {
            Assert.DoesNotContain('\\', file.RelativePath);
        }

        Assert.Contains(disc.Files, f => f.RelativePath == "folder1/nested/deep_file.txt");
        Assert.Contains(disc.Files, f => f.RelativePath == "root_file.txt");
    }

    [Fact]
    public async Task Manifest_WriteThenVerify_IsIntact()
    {
        string discRoot = CreateTempDirectory();
        string subDir = Path.Combine(discRoot, "docs");
        Directory.CreateDirectory(subDir);

        string file1Rel = "docs/readme.txt";
        string file2Rel = "data.bin";
        string file1Path = Path.Combine(discRoot, "docs", "readme.txt");
        string file2Path = Path.Combine(discRoot, "data.bin");

        await File.WriteAllTextAsync(file1Path, "IgnisDisc Archival Test Readme");
        await File.WriteAllBytesAsync(file2Path, new byte[] { 1, 2, 3, 4, 5 });

        var files = new List<ArchiveSourceFile>
        {
            new(file1Path, file1Rel, new FileInfo(file1Path).Length),
            new(file2Path, file2Rel, new FileInfo(file2Path).Length)
        };

        var disc = new ArchiveDiscPlan(
            1,
            files,
            files.Sum(f => f.SizeBytes),
            files.Sum(f => CompilationCapacityCalculator.CalculateFileSectors(f.SizeBytes)));

        var plan = new ArchivePlan(
            "testset12345",
            DiscMediaType.CdR,
            0,
            352800,
            new[] { disc },
            disc.DataBytes);

        var index = await ArchiveManifest.WriteAsync(discRoot, plan, disc);
        Assert.NotNull(index);
        Assert.Equal(2, index.Files.Count);

        string manifestPath = Path.Combine(discRoot, ArchiveManifest.ManifestFileName);
        Assert.True(File.Exists(manifestPath));
        string manifestText = await File.ReadAllTextAsync(manifestPath);
        Assert.EndsWith("\n", manifestText);

        var manifestLines = manifestText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, manifestLines.Length);

        // Format: "{hash}  {RelativePath}"
        var matchLine = manifestLines.First(l => l.Contains(file1Rel));
        var parts = matchLine.Split("  ");
        Assert.Equal(2, parts.Length);
        Assert.Equal(64, parts[0].Length);
        Assert.Equal(file1Rel, parts[1]);

        var result = await ArchiveManifest.VerifyAsync(discRoot);
        Assert.True(result.IsIntact);
        Assert.Equal(2, result.FilesChecked);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Corrupt);
    }

    [Fact]
    public async Task Manifest_Verify_DetectsCorruptAndMissing()
    {
        string discRoot = CreateTempDirectory();
        string file1Rel = "file1.txt";
        string file2Rel = "file2.txt";
        string file1Path = Path.Combine(discRoot, file1Rel);
        string file2Path = Path.Combine(discRoot, file2Rel);

        await File.WriteAllTextAsync(file1Path, "Original content 1");
        await File.WriteAllTextAsync(file2Path, "Original content 2");

        var files = new List<ArchiveSourceFile>
        {
            new(file1Path, file1Rel, new FileInfo(file1Path).Length),
            new(file2Path, file2Rel, new FileInfo(file2Path).Length)
        };

        var disc = new ArchiveDiscPlan(
            1,
            files,
            files.Sum(f => f.SizeBytes),
            files.Sum(f => CompilationCapacityCalculator.CalculateFileSectors(f.SizeBytes)));

        var plan = new ArchivePlan(
            "testset12345",
            DiscMediaType.CdR,
            0,
            352800,
            new[] { disc },
            disc.DataBytes);

        await ArchiveManifest.WriteAsync(discRoot, plan, disc);

        // Corrupt file1 by modifying bytes
        await File.WriteAllTextAsync(file1Path, "Tampered content 1");

        // Delete file2
        File.Delete(file2Path);

        var result = await ArchiveManifest.VerifyAsync(discRoot);
        Assert.False(result.IsIntact);
        Assert.Equal(2, result.FilesChecked);
        Assert.Single(result.Corrupt);
        Assert.Equal(file1Rel, result.Corrupt[0]);
        Assert.Single(result.Missing);
        Assert.Equal(file2Rel, result.Missing[0]);
    }

    [Fact]
    public void Par2_BuildCreateArguments_ExactOrder()
    {
        string outPar2 = @"C:\scratch\recovery.par2";
        string baseDir = @"C:\disc";
        int redundancy = 15;

        var args = Par2Runner.BuildCreateArguments(outPar2, baseDir, redundancy);

        var expected = new[]
        {
            "create",
            "-q",
            "-r15",
            "-R",
            $"-B{baseDir}",
            outPar2,
            $"{baseDir}{Path.DirectorySeparatorChar}*"
        };

        Assert.Equal(expected, args);
    }

    [Fact]
    public async Task Stager_WithoutPar2AndRedundancy_ThrowsBeforeCopying()
    {
        string stagingRoot = Path.Combine(Path.GetTempPath(), "cdburner-staging-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(stagingRoot);

        var file = new ArchiveSourceFile(@"C:\nonexistent\source.dat", "source.dat", 100);
        var disc = new ArchiveDiscPlan(1, new[] { file }, 100, 1);
        var plan = new ArchivePlan("set01", DiscMediaType.CdR, 10, 320727, new[] { disc }, 100);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArchiveStager.StageAsync(plan, stagingRoot, par2Path: null));

        Assert.Contains("PAR2 recovery data requires par2cmdline", ex.Message);
        Assert.False(Directory.Exists(stagingRoot) && Directory.EnumerateFileSystemEntries(stagingRoot).Any());
    }

    [Fact]
    public async Task Stager_ZeroRedundancy_CopiesFilesAndWritesManifestPerDisc()
    {
        string sourceDir = CreateTempDirectory();
        string stagingRoot = CreateTempDirectory();

        string src1 = Path.Combine(sourceDir, "file1.txt");
        string src2 = Path.Combine(sourceDir, "file2.txt");
        string src3 = Path.Combine(sourceDir, "file3.txt");

        await File.WriteAllTextAsync(src1, "Content file 1");
        await File.WriteAllTextAsync(src2, "Content file 2");
        await File.WriteAllTextAsync(src3, "Content file 3");

        var dt1 = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var dt2 = new DateTime(2025, 2, 20, 12, 30, 0, DateTimeKind.Utc);
        var dt3 = new DateTime(2025, 3, 25, 14, 45, 0, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(src1, dt1);
        File.SetLastWriteTimeUtc(src2, dt2);
        File.SetLastWriteTimeUtc(src3, dt3);

        var f1 = new ArchiveSourceFile(src1, "file1.txt", new FileInfo(src1).Length);
        var f2 = new ArchiveSourceFile(src2, "file2.txt", new FileInfo(src2).Length);
        var f3 = new ArchiveSourceFile(src3, "file3.txt", new FileInfo(src3).Length);

        var disc1 = new ArchiveDiscPlan(1, new[] { f1, f2 }, f1.SizeBytes + f2.SizeBytes, 2);
        var disc2 = new ArchiveDiscPlan(2, new[] { f3 }, f3.SizeBytes, 1);
        var plan = new ArchivePlan(
            "testset12345",
            DiscMediaType.CdR,
            0,
            352800,
            new[] { disc1, disc2 },
            f1.SizeBytes + f2.SizeBytes + f3.SizeBytes);

        var stagedDirs = await ArchiveStager.StageAsync(plan, stagingRoot, par2Path: null);

        Assert.Equal(2, stagedDirs.Count);
        Assert.True(Directory.Exists(stagedDirs[0]));
        Assert.True(Directory.Exists(stagedDirs[1]));

        string stagedFile1 = Path.Combine(stagedDirs[0], "file1.txt");
        string stagedFile3 = Path.Combine(stagedDirs[1], "file3.txt");
        Assert.Equal(dt1, File.GetLastWriteTimeUtc(stagedFile1));
        Assert.Equal(dt3, File.GetLastWriteTimeUtc(stagedFile3));

        var verify1 = await ArchiveManifest.VerifyAsync(stagedDirs[0]);
        Assert.True(verify1.IsIntact);
        Assert.Equal(2, verify1.FilesChecked);
        Assert.Equal("testset12345", verify1.SetId);
        Assert.Equal(1, verify1.DiscNumber);
        Assert.Equal(2, verify1.DiscCount);

        var verify2 = await ArchiveManifest.VerifyAsync(stagedDirs[1]);
        Assert.True(verify2.IsIntact);
        Assert.Empty(verify2.Missing);
        Assert.Empty(verify2.Corrupt);
        Assert.Equal(1, verify2.FilesChecked);
        Assert.Equal("testset12345", verify2.SetId);
        Assert.Equal(2, verify2.DiscNumber);
        Assert.Equal(2, verify2.DiscCount);
    }
}
