using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using CdBurner.Core.Archival;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Damage to the archive's own bookkeeping files (manifest, index, recovery volumes) must never make a
/// verification pass, shrink the set of files that is checked, or cost the user a file.
/// </summary>
public sealed class ArchiveDamageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public ArchiveDamageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private async Task<string> StageAsync(params (string Name, string Content)[] files)
    {
        string source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
        foreach (var (name, content) in files)
        {
            string path = Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }

        var plan = ArchiveSpanPlanner.Plan(source, DiscMediaType.CdR, redundancyPercent: 0, setId: "damageset");
        var discs = await ArchiveStager.StageAsync(plan, Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N")), par2Path: null);
        return Assert.Single(discs);
    }

    private Task<string> StageThreeFilesAsync() =>
        StageAsync(("alpha.txt", "alpha payload"), ("beta.txt", "beta payload"), ("docs/gamma.txt", "gamma payload"));

    private static string ManifestPath(string disc) => Path.Combine(disc, ArchiveManifest.ManifestFileName);

    private static string IndexPath(string disc) => Path.Combine(disc, ArchiveManifest.IndexFileName);

    private static void DamageManifest(string disc, string kind)
    {
        string path = ManifestPath(disc);
        byte[] original = File.ReadAllBytes(path);
        string text = Encoding.UTF8.GetString(original);
        int lastLineStart = text.TrimEnd('\n').LastIndexOf('\n') + 1;

        byte[] damaged = kind switch
        {
            "empty" => [],
            "zero-filled" => new byte[original.Length],
            "last line lost" => original[..lastLineStart],
            "cut inside last hash" => original[..(lastLineStart + 30)],
            "separator zeroed" => [.. original[..64], 0, 0, .. original[66..]],
            "first line zeroed" => [.. new byte[text.IndexOf('\n')], .. original[text.IndexOf('\n')..]],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        File.WriteAllBytes(path, damaged);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zero-filled")]
    [InlineData("last line lost")]
    [InlineData("cut inside last hash")]
    [InlineData("separator zeroed")]
    [InlineData("first line zeroed")]
    public async Task Verify_DamagedManifest_IsNotIntact_AndStillChecksEveryFile(string kind)
    {
        string disc = await StageThreeFilesAsync();
        DamageManifest(disc, kind);

        var result = await ArchiveManifest.VerifyAsync(disc);

        Assert.False(result.IsIntact);
        Assert.Equal(3, result.FilesChecked);
        Assert.Contains(result.Corrupt, c => c.StartsWith(ArchiveManifest.ManifestFileName, StringComparison.Ordinal));
        Assert.Empty(result.Missing);
    }

    [Fact]
    public async Task Verify_ZeroFilledManifest_StillReportsTheCorruptPayloadFile()
    {
        string disc = await StageThreeFilesAsync();
        DamageManifest(disc, "zero-filled");
        await File.WriteAllTextAsync(Path.Combine(disc, "beta.txt"), "tampered");

        var result = await ArchiveManifest.VerifyAsync(disc);

        Assert.False(result.IsIntact);
        Assert.Contains("beta.txt", result.Corrupt);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zero-filled")]
    public async Task Verify_DamagedManifestWithoutIndex_IsNotIntact(string kind)
    {
        string disc = await StageThreeFilesAsync();
        File.Delete(IndexPath(disc));
        DamageManifest(disc, kind);

        var result = await ArchiveManifest.VerifyAsync(disc);

        Assert.False(result.IsIntact);
        Assert.Equal(0, result.FilesChecked);
    }

    [Fact]
    public async Task Verify_UnreadableIndex_IsNotIntact_ButFilesAreStillChecked()
    {
        string disc = await StageThreeFilesAsync();
        File.WriteAllBytes(IndexPath(disc), new byte[new FileInfo(IndexPath(disc)).Length]);

        var result = await ArchiveManifest.VerifyAsync(disc);

        Assert.False(result.IsIntact);
        Assert.Equal(3, result.FilesChecked);
        string problem = Assert.Single(result.Corrupt);
        Assert.StartsWith(ArchiveManifest.IndexFileName, problem);
    }

    [Fact]
    public async Task Verify_IndexHashThatDisagreesWithManifest_IsNotIntact()
    {
        string disc = await StageThreeFilesAsync();
        string manifestHash = (await File.ReadAllLinesAsync(ManifestPath(disc)))[0][..64];
        string otherHash = (manifestHash[0] == '0' ? '1' : '0') + manifestHash[1..];
        string index = await File.ReadAllTextAsync(IndexPath(disc));
        Assert.Contains(manifestHash, index);
        await File.WriteAllTextAsync(IndexPath(disc), index.Replace(manifestHash, otherHash));

        var result = await ArchiveManifest.VerifyAsync(disc);

        Assert.False(result.IsIntact);
        Assert.Contains(result.Corrupt, c => c.StartsWith(ArchiveManifest.IndexFileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_FileNameWithLeadingSpace_IsIntact()
    {
        string disc = await StageAsync((" leading.txt", "one"), ("plain.txt", "two"));

        var result = await ArchiveManifest.VerifyAsync(disc);

        Assert.True(result.IsIntact, string.Join("; ", result.Missing.Concat(result.Corrupt)));
        Assert.Equal(2, result.FilesChecked);
    }

    [Theory]
    [InlineData("  docs/readme.txt", "docs/readme.txt")]
    [InlineData(" *docs/readme.txt", "docs/readme.txt")]
    [InlineData("   leading space.txt", " leading space.txt")]
    [InlineData(" single-space.txt", "single-space.txt")]
    public void TryParseManifestLine_AcceptsSha256sumFormats(string afterHash, string expectedPath)
    {
        string hash = new('a', 64);

        Assert.True(ArchiveManifest.TryParseManifestLine(hash + afterHash, out string parsedHash, out string parsedPath));
        Assert.Equal(hash, parsedHash);
        Assert.Equal(expectedPath, parsedPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123  file.txt")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz  file.txt")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\0\0file.txt")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    public void TryParseManifestLine_RejectsDamagedLines(string line)
    {
        Assert.False(ArchiveManifest.TryParseManifestLine(line, out _, out _));
    }

    /// <summary>
    /// Module folder of PowerShell 7. A Windows PowerShell 5.1 process started from a PowerShell 7 session inherits
    /// a PSModulePath with this folder in front, and then cannot find the script functions of its own utility
    /// module (Get-FileHash among them). VERIFY.BAT started from such a terminal runs in exactly that state.
    /// </summary>
    private static readonly string PowerShell7Modules = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "Modules");

    private static (int ExitCode, string Output) RunVerifyScript(string disc, bool startedFromPowerShell7 = false)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (startedFromPowerShell7)
        {
            psi.Environment["PSModulePath"] =
                PowerShell7Modules + Path.PathSeparator + Environment.GetEnvironmentVariable("PSModulePath");
        }
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Path.Combine(disc, ArchiveManifest.VerifyPowerShellFileName));

        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyScript_PassesOnAnIntactDisc_IncludingALeadingSpaceName(bool startedFromPowerShell7)
    {
        if (startedFromPowerShell7 && !Directory.Exists(PowerShell7Modules))
        {
            return;
        }

        string disc = await StageAsync((" leading.txt", "one"), ("plain.txt", "two"), ("docs/deep.txt", "three"));

        var (exitCode, output) = RunVerifyScript(disc, startedFromPowerShell7);

        Assert.True(exitCode == 0, output);
        Assert.Contains("PASSED (3 files intact)", output);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zero-filled")]
    [InlineData("last line lost")]
    [InlineData("cut inside last hash")]
    public async Task VerifyScript_FailsOnADamagedManifest(string kind)
    {
        string disc = await StageThreeFilesAsync();
        DamageManifest(disc, kind);

        var (exitCode, output) = RunVerifyScript(disc);

        Assert.True(exitCode != 0, output);
        Assert.DoesNotContain("PASSED", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyScript_FailsOnACorruptFile(bool startedFromPowerShell7)
    {
        if (startedFromPowerShell7 && !Directory.Exists(PowerShell7Modules))
        {
            return;
        }

        string disc = await StageThreeFilesAsync();
        await File.WriteAllTextAsync(Path.Combine(disc, "alpha.txt"), "tampered");

        var (exitCode, output) = RunVerifyScript(disc, startedFromPowerShell7);

        Assert.Equal(1, exitCode);
        Assert.Contains("[CORRUPT] alpha.txt", output);
        Assert.Contains("[OK]      beta.txt", output);
    }

    [Theory]
    [InlineData(1L << 42)]
    [InlineData((1L << 42) + 1)]
    [InlineData(1L << 43)]
    [InlineData(1L << 50)]
    [InlineData(long.MaxValue)]
    public void Plan_FileOfFourTebibytesOrMore_IsRejected(long sizeBytes)
    {
        var files = new[] { new ArchiveSourceFile(@"X:\huge.bin", "huge.bin", sizeBytes) };

        Assert.Throws<InvalidOperationException>(() => ArchiveSpanPlanner.Plan(files, DiscMediaType.DvdPlusRDualLayer, 0));
        Assert.Equal(int.MaxValue, CompilationCapacityCalculator.CalculateFileSectors(sizeBytes));
        Assert.Equal(int.MaxValue, OpticalConstants.BytesToSectors(sizeBytes));
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1L, 1)]
    [InlineData(2048L, 1)]
    [InlineData(2049L, 2)]
    [InlineData((1L << 42) - 2048, int.MaxValue)]
    public void BytesToSectors_RoundsUpWithoutWrapping(long sizeBytes, int expectedSectors)
    {
        Assert.Equal(expectedSectors, OpticalConstants.BytesToSectors(sizeBytes));
    }

    // ---- PAR2 repair, driven by a stand-in for par2.exe so the outcome of each run is fixed ----

    private const string GoodContent = "repaired content";
    private const string DamagedContent = "damaged content";

    /// <summary>
    /// Builds a folder with one damaged file and three recovery files, plus a script that behaves like par2:
    /// it moves data.bin to data.bin.1 and then either reports a matrix failure or writes the repaired file.
    /// It fails while recovery.vol000+01.par2 is present, or always when "always-fail.flag" exists, and
    /// reports "repair not possible" (exit 2, nothing renamed) when "not-possible.flag" exists.
    /// </summary>
    private (string Folder, string FakePar2) CreateRepairFolder()
    {
        string folder = Path.Combine(_root, "repair-" + Guid.NewGuid().ToString("N"));
        string recovery = Path.Combine(folder, ArchiveManifest.RecoveryFolderName);
        Directory.CreateDirectory(recovery);

        File.WriteAllText(Path.Combine(folder, "data.bin"), DamagedContent);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "an archived file");
        File.WriteAllText(Path.Combine(folder, "notes.txt.1"), "another archived file that happens to end in .1");
        File.WriteAllText(Path.Combine(recovery, "recovery.par2"), "index");
        File.WriteAllText(Path.Combine(recovery, "recovery.vol000+01.par2"), "volume 0");
        File.WriteAllText(Path.Combine(recovery, "recovery.vol001+02.par2"), "volume 1");
        File.WriteAllText(Path.Combine(recovery, "good.copy"), GoodContent);

        string fakePar2 = Path.Combine(_root, "fake-par2-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(fakePar2, string.Join("\r\n",
            "@echo off",
            "if exist \"not-possible.flag\" exit /b 2",
            "ren data.bin data.bin.1",
            "if exist \"always-fail.flag\" goto matrixfailure",
            "if exist \"RECOVERY\\recovery.vol000+01.par2\" goto matrixfailure",
            "copy /y RECOVERY\\good.copy data.bin >nul",
            "exit /b 0",
            ":matrixfailure",
            "echo RS computation error. 1>&2",
            "exit /b 6",
            ""));

        return (folder, fakePar2);
    }

    private static string[] RecoveryFileNames(string folder) =>
        Directory.EnumerateFiles(Path.Combine(folder, ArchiveManifest.RecoveryFolderName))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

    private static readonly string[] AllRecoveryFiles =
        ["good.copy", "recovery.par2", "recovery.vol000+01.par2", "recovery.vol001+02.par2"];

    [Fact]
    public async Task Repair_MatrixFailure_IsRetriedWithoutOneRecoveryVolume()
    {
        var (folder, fakePar2) = CreateRepairFolder();

        Par2RepairResult result = await Par2Runner.RepairAsync(fakePar2, folder);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(GoodContent, File.ReadAllText(Path.Combine(folder, "data.bin")));
        Assert.Equal(AllRecoveryFiles, RecoveryFileNames(folder));
    }

    [Fact]
    public async Task Repair_ListsOnlyTheBackupsItCreated_NotArchivedFilesEndingInDotOne()
    {
        var (folder, fakePar2) = CreateRepairFolder();

        Par2RepairResult result = await Par2Runner.RepairAsync(fakePar2, folder);

        string backup = Assert.Single(result.BackupFiles);
        Assert.Equal(Path.Combine(folder, "data.bin.1"), backup);
        Assert.Equal(DamagedContent, File.ReadAllText(backup));
        Assert.True(File.Exists(Path.Combine(folder, "notes.txt.1")));
    }

    [Fact]
    public async Task Repair_ThatFailsEveryAttempt_LeavesEveryFileUnderItsOwnName()
    {
        var (folder, fakePar2) = CreateRepairFolder();
        File.WriteAllText(Path.Combine(folder, "always-fail.flag"), string.Empty);

        Par2RepairResult result = await Par2Runner.RepairAsync(fakePar2, folder);

        Assert.Equal(6, result.ExitCode);
        Assert.Contains("RS computation error", result.ErrorOutput);
        Assert.Equal(3, result.Attempts);
        Assert.Empty(result.BackupFiles);
        Assert.Equal(DamagedContent, File.ReadAllText(Path.Combine(folder, "data.bin")));
        Assert.False(File.Exists(Path.Combine(folder, "data.bin.1")));
        Assert.Equal(AllRecoveryFiles, RecoveryFileNames(folder));
    }

    [Fact]
    public async Task Repair_ThatIsNotPossible_IsNotRetried()
    {
        var (folder, fakePar2) = CreateRepairFolder();
        File.WriteAllText(Path.Combine(folder, "not-possible.flag"), string.Empty);

        Par2RepairResult result = await Par2Runner.RepairAsync(fakePar2, folder);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(DamagedContent, File.ReadAllText(Path.Combine(folder, "data.bin")));
    }

    [Fact]
    public async Task Repair_PutsBackARecoveryVolumeLeftSetAsideByAnInterruptedRun()
    {
        var (folder, fakePar2) = CreateRepairFolder();
        string recovery = Path.Combine(folder, ArchiveManifest.RecoveryFolderName);
        File.Move(Path.Combine(recovery, "recovery.vol001+02.par2"), Path.Combine(recovery, "recovery.vol001+02.par2.held"));

        await Par2Runner.RepairAsync(fakePar2, folder);

        Assert.Equal(AllRecoveryFiles, RecoveryFileNames(folder));
    }

    // ---- Fixed-percentage (--no-fill) PAR2 sizing, ceiling check, and sector-damage repair ----

    [Fact]
    public async Task NoFill_WritesOneRecoveryVolumeWithSectorAlignedBlockSize()
    {
        string par2Path = GetPar2OrStandIn();

        string source = Path.Combine(_root, "nofill-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        var rng = new Random(2026_11_12);
        for (int i = 0; i < 16; i++)
        {
            byte[] payload = CreateSectorPatternBytes(rng, 48 * 1024 + i * 513);
            await File.WriteAllBytesAsync(Path.Combine(source, $"part-{i:D2}.bin"), payload);
        }

        const int ceilingSectors = 4_000;
        var plan = ArchiveSpanPlanner.Plan(
            source,
            DiscMediaType.CdR,
            redundancyPercent: 10,
            setId: "nofill-align",
            fillFreeSpace: false,
            ceilingSectorsOverride: ceilingSectors);

        string stagingRoot = Path.Combine(_root, "nofill-staging-" + Guid.NewGuid().ToString("N"));
        var staged = Assert.Single(await ArchiveStager.StageDiscsAsync(plan, stagingRoot, par2Path));

        Assert.NotNull(staged.Recovery);
        Assert.True(staged.Recovery!.BlockSize >= OpticalConstants.DataSectorSize);
        Assert.Equal(0, staged.Recovery.BlockSize % OpticalConstants.DataSectorSize);

        string recoveryDir = Path.Combine(staged.DiscRoot, ArchiveManifest.RecoveryFolderName);
        string indexPar2 = Path.Combine(recoveryDir, "recovery.par2");
        Assert.True(File.Exists(indexPar2));

        string[] volumes = Directory.GetFiles(recoveryDir, "recovery.vol*.par2");
        Assert.Single(volumes);

        ulong packetSliceSize = ReadPar2MainSliceSize(indexPar2);
        Assert.Equal((ulong)staged.Recovery.BlockSize, packetSliceSize);
        Assert.Equal(0UL, packetSliceSize % (ulong)OpticalConstants.DataSectorSize);

        // --no-fill sizes recovery to ~10% of data sectors rather than filling the 4,000-sector ceiling.
        Assert.InRange(staged.RecoverySectors, 1, (int)staged.RecoveryBudgetSectors);
        Assert.True(staged.TotalSectors < ceilingSectors / 2, $"Expected --no-fill not to fill ceiling, got {staged.TotalSectors} sectors");
    }

    [Fact]
    public async Task NoFill_NeverExceedsDiscCeiling()
    {
        string par2Path = GetPar2OrStandIn();

        string source = Path.Combine(_root, "nofill-ceiling-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        var rng = new Random(2026_11_13);

        // Ceiling 1,000 sectors, redundancy 25%: usable is 800 sectors.
        // Pack ~560 sectors of files across subdirectories so UDF+ISO filesystem overhead (~360 sectors) leaves ~80 free
        // sectors below the 1,000-sector ceiling, less than the unclipped 25% (~140 sectors).
        const int ceilingSectors = 1_000;
        for (int i = 0; i < 35; i++)
        {
            string sub = Path.Combine(source, $"dir-{i % 5:D2}");
            Directory.CreateDirectory(sub);
            byte[] payload = CreateSectorPatternBytes(rng, 16 * OpticalConstants.DataSectorSize);
            await File.WriteAllBytesAsync(Path.Combine(sub, $"chunk-{i:D2}.dat"), payload);
        }

        var plan = ArchiveSpanPlanner.Plan(
            source,
            DiscMediaType.CdR,
            redundancyPercent: 25,
            setId: "nofill-ceiling",
            fillFreeSpace: false,
            ceilingSectorsOverride: ceilingSectors);

        string stagingRoot = Path.Combine(_root, "nofill-ceiling-stg-" + Guid.NewGuid().ToString("N"));
        var stagedDiscs = await ArchiveStager.StageDiscsAsync(plan, stagingRoot, par2Path);

        Assert.NotEmpty(stagedDiscs);
        foreach (var staged in stagedDiscs)
        {
            long imageSectors = ArchiveStager.ImageSectors(staged.DiscRoot);
            Assert.True(
                imageSectors <= plan.EffectiveCeilingSectors,
                $"Staged --no-fill disc image takes {imageSectors} sectors, exceeding ceiling {plan.EffectiveCeilingSectors}.");
            Assert.True(staged.TotalSectors <= plan.EffectiveCeilingSectors);
            if (staged.Recovery != null)
            {
                Assert.Equal(0, staged.Recovery.BlockSize % OpticalConstants.DataSectorSize);
                string recoveryDir = Path.Combine(staged.DiscRoot, ArchiveManifest.RecoveryFolderName);
                Assert.Single(Directory.GetFiles(recoveryDir, "recovery.vol*.par2"));
            }
        }
    }

    [Fact]
    public async Task NoFill_DamagedSectors_RepairsFully()
    {
        string par2Path = GetPar2OrStandIn();

        string source = Path.Combine(_root, "nofill-repair-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        var rng = new Random(2026_11_14);
        var originalHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        for (int i = 0; i < 12; i++)
        {
            byte[] payload = CreateSectorPatternBytes(rng, 64 * 1024 + i * 1024);
            string name = $"record-{i:D2}.bin";
            await File.WriteAllBytesAsync(Path.Combine(source, name), payload);
            originalHashes[name] = System.Security.Cryptography.SHA256.HashData(payload);
        }

        var plan = ArchiveSpanPlanner.Plan(
            source,
            DiscMediaType.CdR,
            redundancyPercent: 15,
            setId: "nofill-repair",
            fillFreeSpace: false,
            ceilingSectorsOverride: 4_000);

        string stagingRoot = Path.Combine(_root, "nofill-repair-stg-" + Guid.NewGuid().ToString("N"));
        var staged = Assert.Single(await ArchiveStager.StageDiscsAsync(plan, stagingRoot, par2Path));
        Assert.NotNull(staged.Recovery);
        Assert.Equal(0, staged.Recovery!.BlockSize % OpticalConstants.DataSectorSize);

        // Zero out multiple 2,048-byte sectors across two files (within the 15% recovery budget).
        string damagedA = Path.Combine(staged.DiscRoot, "record-02.bin");
        string damagedB = Path.Combine(staged.DiscRoot, "record-07.bin");
        ZeroFileSectors(damagedA, startSector: 3, sectorCount: 4);
        ZeroFileSectors(damagedB, startSector: 5, sectorCount: 4);

        var brokenVerify = await ArchiveManifest.VerifyAsync(staged.DiscRoot);
        Assert.False(brokenVerify.IsIntact);
        Assert.Contains("record-02.bin", brokenVerify.Corrupt);
        Assert.Contains("record-07.bin", brokenVerify.Corrupt);

        Par2RepairResult repair = await Par2Runner.RepairAsync(par2Path, staged.DiscRoot);
        Assert.True(repair.ExitCode == 0, $"PAR2 repair failed (exit {repair.ExitCode}): {repair.ErrorOutput}");
        foreach (string backup in repair.BackupFiles)
        {
            File.Delete(backup);
        }

        var repairedVerify = await ArchiveManifest.VerifyAsync(staged.DiscRoot);
        Assert.True(repairedVerify.IsIntact, $"Expected intact disc after repair, corrupt: {string.Join(", ", repairedVerify.Corrupt)}");

        foreach (var (name, expectedHash) in originalHashes)
        {
            byte[] actualHash = System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(Path.Combine(staged.DiscRoot, name)));
            Assert.Equal(expectedHash, actualHash);
        }
    }

    private static byte[] CreateSectorPatternBytes(Random rng, int length)
    {
        byte[] result = new byte[length];
        byte[] tile = new byte[32];
        for (int offset = 0; offset < length; offset += OpticalConstants.DataSectorSize)
        {
            rng.NextBytes(tile);
            int limit = Math.Min(length, offset + OpticalConstants.DataSectorSize);
            for (int pos = offset; pos < limit; pos += tile.Length)
            {
                int copyLen = Math.Min(tile.Length, limit - pos);
                Buffer.BlockCopy(tile, 0, result, pos, copyLen);
            }
        }
        return result;
    }

    private string GetPar2OrStandIn()
    {
        string? real = Par2Runner.FindPar2Executable();
        if (real != null)
        {
            return real;
        }

        string ps1Path = Path.Combine(_root, "standin-par2.ps1");
        string cmdPath = Path.Combine(_root, "standin-par2.cmd");
        if (!File.Exists(cmdPath))
        {
            File.WriteAllText(ps1Path, """
                $ErrorActionPreference = 'Stop'
                $mode = $args[0]
                if ($mode -eq 'create') {
                    $blockSize = 0
                    $blocks = 0
                    $singleVol = $false
                    $baseDir = (Get-Location).Path
                    $outPar2 = ''
                    foreach ($a in $args) {
                        if ($a -cmatch '^-s(\d+)$') { $blockSize = [int]$Matches[1] }
                        elseif ($a -cmatch '^-c(\d+)$') { $blocks = [int]$Matches[1] }
                        elseif ($a -ceq '-n1') { $singleVol = $true }
                        elseif ($a -cmatch '^-r\d+') { Write-Error 'Unaligned -r flag not allowed'; exit 1 }
                        elseif ($a.EndsWith('.par2', [System.StringComparison]::OrdinalIgnoreCase)) { $outPar2 = $a }
                    }
                    if ($blockSize -le 0 -or ($blockSize % 2048) -ne 0 -or $blocks -le 0 -or -not $singleVol -or [string]::IsNullOrEmpty($outPar2)) {
                        Write-Error "Invalid create args: $($args -join ' ')"
                        exit 1
                    }
                    $pkt = New-Object byte[] 2048
                    [System.Text.Encoding]::ASCII.GetBytes("PAR2`0PKT").CopyTo($pkt, 0)
                    [System.BitConverter]::GetBytes([uint64]72).CopyTo($pkt, 8)
                    [System.Text.Encoding]::ASCII.GetBytes("PAR 2.0`0Main`0`0`0`0").CopyTo($pkt, 48)
                    [System.BitConverter]::GetBytes([uint64]$blockSize).CopyTo($pkt, 64)
                    [System.IO.File]::WriteAllBytes($outPar2, $pkt)

                    $entries = @{}
                    Get-ChildItem -LiteralPath $baseDir -Recurse -File | Where-Object { $_.FullName -notlike '*RECOVERY*' } | ForEach-Object {
                        $rel = [System.IO.Path]::GetRelativePath($baseDir, $_.FullName)
                        $entries[$rel] = [System.Convert]::ToBase64String([System.IO.File]::ReadAllBytes($_.FullName))
                    }
                    $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes(($entries | ConvertTo-Json -Compress))
                    $ms = New-Object System.IO.MemoryStream
                    $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionMode]::Compress, $true)
                    $gz.Write($jsonBytes, 0, $jsonBytes.Length)
                    $gz.Dispose()
                    $compressed = $ms.ToArray()

                    $targetLen = [Math]::Max(8 + $compressed.Length, $blocks * $blockSize)
                    $volBytes = New-Object byte[] $targetLen
                    [System.Text.Encoding]::ASCII.GetBytes("STND").CopyTo($volBytes, 0)
                    [System.BitConverter]::GetBytes([int]$compressed.Length).CopyTo($volBytes, 4)
                    [System.Array]::Copy($compressed, 0, $volBytes, 8, $compressed.Length)
                    $volPath = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($outPar2), ("recovery.vol00+{0:D2}.par2" -f $blocks))
                    [System.IO.File]::WriteAllBytes($volPath, $volBytes)
                    exit 0
                }
                elseif ($mode -eq 'repair') {
                    $root = (Get-Location).Path
                    $vol = Get-ChildItem -LiteralPath (Join-Path $root 'RECOVERY') -Filter 'recovery.vol*.par2' | Select-Object -First 1
                    if ($null -eq $vol) { exit 2 }
                    $volBytes = [System.IO.File]::ReadAllBytes($vol.FullName)
                    $len = [System.BitConverter]::ToInt32($volBytes, 4)
                    $ms = New-Object System.IO.MemoryStream($volBytes, 8, $len)
                    $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
                    $sr = New-Object System.IO.StreamReader($gz, [System.Text.Encoding]::UTF8)
                    $map = $sr.ReadToEnd() | ConvertFrom-Json
                    $sr.Dispose()
                    foreach ($prop in $map.PSObject.Properties) {
                        $target = Join-Path $root $prop.Name
                        $expected = [System.Convert]::FromBase64String([string]$prop.Value)
                        $needsRepair = -not (Test-Path -LiteralPath $target)
                        if (-not $needsRepair) {
                            $actual = [System.IO.File]::ReadAllBytes($target)
                            if ($actual.Length -ne $expected.Length) { $needsRepair = $true }
                            else {
                                for ($i = 0; $i -lt $actual.Length; $i++) {
                                    if ($actual[$i] -ne $expected[$i]) { $needsRepair = $true; break }
                                }
                            }
                        }
                        if ($needsRepair) {
                            if (Test-Path -LiteralPath $target) {
                                Move-Item -LiteralPath $target -Destination ($target + '.1') -Force
                            }
                            [System.IO.File]::WriteAllBytes($target, $expected)
                        }
                    }
                    exit 0
                }
                exit 1
                """);
            File.WriteAllText(cmdPath, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\" %*\r\n");
        }

        return cmdPath;
    }

    private static void ZeroFileSectors(string filePath, int startSector, int sectorCount)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write);
        fs.Position = (long)startSector * OpticalConstants.DataSectorSize;
        fs.Write(new byte[sectorCount * OpticalConstants.DataSectorSize]);
    }

    private static ulong ReadPar2MainSliceSize(string par2FilePath)
    {
        byte[] bytes = File.ReadAllBytes(par2FilePath);
        ReadOnlySpan<byte> magic = "PAR2\0PKT"u8;
        ReadOnlySpan<byte> mainType = "PAR 2.0\0Main\0\0\0\0"u8;

        int offset = 0;
        while (offset + 64 <= bytes.Length)
        {
            if (!bytes.AsSpan(offset, 8).SequenceEqual(magic))
            {
                offset++;
                continue;
            }

            ulong packetLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8, 8));
            if (packetLength < 64 || offset + (long)packetLength > bytes.Length)
            {
                break;
            }

            if (bytes.AsSpan(offset + 48, 16).SequenceEqual(mainType) && packetLength >= 72)
            {
                return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 64, 8));
            }

            offset += (int)packetLength;
        }

        throw new InvalidOperationException("No PAR 2.0 Main packet found in " + par2FilePath);
    }
}
