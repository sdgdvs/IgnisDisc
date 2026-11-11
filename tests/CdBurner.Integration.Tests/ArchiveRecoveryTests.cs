using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Archival;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// M2.1 end to end with real par2: stage an archive disc whose recovery data fills it, build its image, damage the
/// image, and get every file back with <c>recover</c>. Skipped where par2 is not installed (CI installs it).
/// </summary>
public sealed class ArchiveRecoveryTests : IDisposable
{
    private const int CeilingSectors = 12_000; // a 24 MB "disc" keeps par2 quick
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ignis-recovery-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _par2 = Par2Runner.FindPar2Executable();

    public ArchiveRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task StagedDisc_IsFilledToTheCeiling_AndItsImageFits()
    {
        if (_par2 == null) return;
        var (staged, image) = await StageAndBuildAsync(redundancy: 10);

        var recovery = staged.Recovery!;
        long budget = staged.RecoveryBudgetSectors;
        Assert.True(staged.RecoverySectors <= budget);
        Assert.True(budget - staged.RecoverySectors <= recovery.BlockSize / 2048 + 2,
            $"{budget - staged.RecoverySectors} sectors left unused (block {recovery.BlockSize / 2048} sectors)");
        Assert.True(recovery.RedundancyPercent > 10, "filling should go past the 10 % minimum on this half-empty disc");

        long imageSectors = new FileInfo(image).Length / 2048;
        Assert.True(imageSectors <= CeilingSectors, $"image is {imageSectors} sectors, ceiling {CeilingSectors}");
        Assert.True(imageSectors >= CeilingSectors - CeilingSectors / 20, $"image is only {imageSectors} sectors");
    }

    [Fact]
    public async Task ScatteredBadSectors_AreAllRepaired()
    {
        if (_par2 == null) return;
        var (staged, image) = await StageAndBuildAsync(redundancy: 10);
        var bad = DataSectors(image).Where((_, i) => i % 97 == 5).Take(staged.Recovery!.RecoveryBlocks / 2).ToList();
        Damage(image, bad);

        var (exit, json) = await RecoverAsync(image, bad);

        Assert.Equal(0, exit);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.True(json.GetProperty("damagedFiles").GetArrayLength() > 3);
        Assert.Equal(json.GetProperty("damagedFiles").GetArrayLength(), json.GetProperty("repairedFiles").GetArrayLength());
        AssertSameFiles(Source, Path.Combine(_root, "out"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "out"), "*.1", SearchOption.AllDirectories)); // par2 backups removed
        Assert.Contains("every file recovered", File.ReadAllText(Path.Combine(_root, "out-recovery-report.txt")));
    }

    [Fact]
    public async Task ImageFromAnotherTool_WithoutABadSectorList_IsRepairedToo()
    {
        if (_par2 == null) return;
        var (_, image) = await StageAndBuildAsync(redundancy: 10);
        Damage(image, DataSectors(image).Skip(40).Take(30).ToList()); // one 60 KB scratch

        var (exit, json) = await RecoverAsync(image, unreadable: null);

        Assert.Equal(0, exit);
        Assert.True(json.GetProperty("repairedFiles").GetArrayLength() >= 1);
        AssertSameFiles(Source, Path.Combine(_root, "out"));
    }

    [Fact]
    public async Task DamagedManifest_IsRebuiltFirst()
    {
        if (_par2 == null) return;
        var (_, image) = await StageAndBuildAsync(redundancy: 10);
        int manifestLba;
        manifestLba = UdfFiles(image).Single(f => f.VirtualPath == "/" + ArchiveManifest.ManifestFileName).StartLba;
        Damage(image, [manifestLba]);

        var (exit, _) = await RecoverAsync(image, unreadable: null);

        Assert.Equal(0, exit);
        AssertSameFiles(Source, Path.Combine(_root, "out"));
    }

    [Fact]
    public async Task DamageBeyondTheRecoveryData_IsReported_NeverCalledRepaired()
    {
        if (_par2 == null) return;
        var (_, image) = await StageAndBuildAsync(redundancy: 10);
        // Nine sectors in ten gone, recovery data included: far past what any recovery data can rebuild.
        var bad = AllFileSectors(image).Where((_, i) => i % 10 != 0).ToList();
        Damage(image, bad);

        var (exit, json) = await RecoverAsync(image, bad);

        Assert.Equal(2, exit);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.True(json.GetProperty("stillCorrupt").GetArrayLength() + json.GetProperty("stillMissing").GetArrayLength() > 0);
        Assert.Contains("more damage than the recovery data can rebuild", json.GetProperty("problem").GetString());
        foreach (var repaired in json.GetProperty("repairedFiles").EnumerateArray())
        {
            string path = repaired.GetString()!;
            Assert.Equal(Hash(Path.Combine(Source, path)), Hash(Path.Combine(_root, "out", path)));
        }
    }

    [Fact]
    public async Task DiscWithoutRecoveryData_SaysSo()
    {
        if (_par2 == null) return;
        var (_, image) = await StageAndBuildAsync(redundancy: 0);
        Damage(image, DataSectors(image).Take(5).ToList());

        var (exit, json) = await RecoverAsync(image, unreadable: null);

        Assert.Equal(2, exit);
        Assert.Contains("no recovery data", json.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task LongPathsPast260Characters_AreStillRepaired()
    {
        // par2cmdline refuses paths past 260 characters; recovery reaches them through a short junction.
        if (_par2 == null) return;
        var (_, image) = await StageAndBuildAsync(redundancy: 10);
        var longFile = UdfFiles(image).Single(f => f.Name.StartsWith("Schaffer", StringComparison.Ordinal));
        Damage(image, Enumerable.Range(longFile.StartLba + 3, 5).ToList());
        string deep = Path.Combine(_root, new string('d', 150));
        Assert.True(Path.Combine(deep, "photos", longFile.Name).Length > 260);

        var (exit, json) = await RecoverAsync(image, unreadable: null, output: deep);

        Assert.Equal(0, exit);
        Assert.Contains(json.GetProperty("repairedFiles").EnumerateArray(), f => f.GetString()!.Contains("Schaffer"));
        AssertSameFiles(Source, deep);
        Assert.Empty(Directory.EnumerateDirectories(Path.GetTempPath(), "ip2-*")); // the junction is removed again
    }

    [Fact]
    public async Task UndamagedImage_NeedsNoRepair()
    {
        if (_par2 == null) return;
        var (_, image) = await StageAndBuildAsync(redundancy: 10);

        var (exit, json) = await RecoverAsync(image, unreadable: null);

        Assert.Equal(0, exit);
        Assert.Equal(0, json.GetProperty("damagedFiles").GetArrayLength());
        Assert.Contains("nothing needed repair", File.ReadAllText(Path.Combine(_root, "out-recovery-report.txt")));
        AssertSameFiles(Source, Path.Combine(_root, "out"));
    }

    [Fact]
    public async Task Recover_FromTheVirtualDrive_ReadsTheDiscFirst()
    {
        if (_par2 == null) return;
        var (_, image) = await StageAndBuildAsync(redundancy: 10);
        Damage(image, DataSectors(image).Skip(10).Take(12).ToList());

        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var recorder = (VirtualDiscRecorder)devices.GetRecorderById("VDRV-01")!;
        using var medium = VirtualDiscMedium.FromExistingFile(image, DiscMediaType.CdRom);
        recorder.InsertMedia(medium);

        var stdout = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(
            ["recover", "--drive", "VDRV-01", "--out", Path.Combine(_root, "out"), "--json"],
            stdout, new StringWriter(), devices, new VirtualBurnEngine());

        Assert.Equal(0, exit);
        AssertSameFiles(Source, Path.Combine(_root, "out"));
        Assert.False(File.Exists(Path.Combine(_root, "out.read.iso"))); // the read copy is removed without --keep-image
    }

    private string Source => Path.Combine(_root, "source");

    private async Task<(ArchiveStagedDisc Staged, string Image)> StageAndBuildAsync(int redundancy)
    {
        var random = new Random(2026_11_10);
        Directory.CreateDirectory(Path.Combine(Source, "photos", "2026"));
        for (int i = 0; i < 12; i++)
        {
            byte[] bytes = new byte[200_000 + random.Next(600_000)];
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(Source, i % 2 == 0 ? "photos" : "photos/2026", $"IMG_{i:D4}.jpg"), bytes);
        }
        File.WriteAllText(Path.Combine(Source, "notes.txt"), "Archive test notes\n");
        // Real music libraries have names past Joliet's 64 characters; on an ISO + UDF disc only UDF holds them.
        byte[] longOne = new byte[300_000];
        random.NextBytes(longOne);
        File.WriteAllBytes(Path.Combine(Source, "photos",
            "Schaffer, Riley, and the Double Ice Backfire - Hold Person - 06 Surrender ( ͡° ͜ʖ ͡°).mp3"), longOne);

        var plan = ArchiveSpanPlanner.Plan(Source, DiscMediaType.CdR, redundancy, "testset", ceilingSectorsOverride: CeilingSectors);
        Assert.Single(plan.Discs);
        var staged = (await ArchiveStager.StageDiscsAsync(plan, Path.Combine(_root, "staging"), _par2)).Single();

        string image = Path.Combine(_root, "disc01.iso");
        int built = await CliCommandRunner.RunAsync(
            // --udf alone: ISO 9660 + UDF with no Joliet, as archive-stage tells people to burn it.
            ["build-iso", "--input", staged.DiscRoot, "--output", image, "--udf", "--label", "ARCHIVE01", "--fixed-time", "1767225600"],
            new StringWriter(), new StringWriter(),
            new DiscDeviceManager(enablePhysicalEnumeration: false), new VirtualBurnEngine());
        Assert.Equal(0, built);
        return (staged, image);
    }

    private async Task<(int Exit, JsonElement Json)> RecoverAsync(string image, IReadOnlyList<int>? unreadable, string? output = null)
    {
        output ??= Path.Combine(_root, "out");
        if (unreadable == null)
        {
            var stdout = new StringWriter();
            int exit = await CliCommandRunner.RunAsync(["recover", "--image", image, "--out", output, "--json"],
                stdout, new StringWriter(), new DiscDeviceManager(enablePhysicalEnumeration: false), new VirtualBurnEngine());
            return (exit, JsonDocument.Parse(stdout.ToString()).RootElement.Clone());
        }

        // A bad-sector list only comes from reading a drive; call the service directly with it.
        var report = await ArchiveRecoveryService.RecoverFromImageAsync(image, unreadable, output, _par2);
        await File.WriteAllTextAsync(output + "-recovery-report.txt", report.ToText());
        string json = JsonSerializer.Serialize(new
        {
            success = report.FullyRecovered,
            damagedFiles = report.DamagedFiles,
            repairedFiles = report.RepairedFiles,
            stillCorrupt = report.StillCorrupt,
            stillMissing = report.StillMissing,
            problem = report.Problem
        });
        return (report.FullyRecovered ? 0 : 2, JsonDocument.Parse(json).RootElement.Clone());
    }

    /// <summary>Sectors holding file data in the image (not the file system), in disc order.</summary>
    private static List<int> DataSectors(string image)
    {
        return UdfFiles(image)
            .Where(f => !f.VirtualPath.StartsWith("/" + ArchiveManifest.RecoveryFolderName, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.VirtualPath.Contains("IGNISDISC", StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => Enumerable.Range(f.StartLba, (int)((f.SizeBytes + 2047) / 2048)))
            .OrderBy(lba => lba)
            .ToList();
    }

    /// <summary>Every sector of every file, recovery files and manifests included.</summary>
    private static List<int> AllFileSectors(string image)
    {
        return UdfFiles(image)
            .SelectMany(f => Enumerable.Range(f.StartLba, (int)((f.SizeBytes + 2047) / 2048)))
            .OrderBy(lba => lba)
            .ToList();
    }

    /// <summary>The image's files with full names (the images have no Joliet, so the ISO tree has 8.3 names only).</summary>
    private static List<IsoFileEntry> UdfFiles(string image)
    {
        using var stream = File.OpenRead(image);
        var root = UdfTreeReader.TryRead(stream)!;
        var files = new List<IsoFileEntry>();
        void Walk(IsoDirectoryEntry dir)
        {
            files.AddRange(dir.Files);
            foreach (var d in dir.SubDirectories) Walk(d);
        }
        Walk(root);
        return files;
    }

    private static void Damage(string image, IReadOnlyList<int> sectors)
    {
        using var fs = new FileStream(image, FileMode.Open, FileAccess.Write);
        byte[] zeros = new byte[2048];
        foreach (int lba in sectors)
        {
            fs.Position = (long)lba * 2048;
            fs.Write(zeros);
        }
    }

    private static void AssertSameFiles(string expectedRoot, string actualRoot)
    {
        foreach (string file in Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(expectedRoot, file);
            Assert.Equal(Hash(file), Hash(Path.Combine(actualRoot, relative)));
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
