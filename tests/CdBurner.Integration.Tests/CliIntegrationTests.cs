using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Integration unit tests for Headless CLI Application (cdburner-cli.exe).
/// Exercises in-process CliCommandRunner.RunAsync against pre-configured VirtualDiscRecorder
/// for drives, build-iso, burn-iso, burn-data, burn-audio, burn-dvd, rip, verify, scan, erase,
/// and validates exit codes, argument handling, human output, and structured JSON telemetry.
/// </summary>
public sealed class CliIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public CliIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _recorder = new VirtualDiscRecorder(
            uniqueId: "VDRV-01",
            vendorId: "IGNIS",
            productId: "VIRTUAL DVD/CD-RW",
            volumePath: @"V:\"
        );

        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore temp folder cleanup issues
        }
    }

    private static (StringWriter stdout, StringWriter stderr) CreateStringWriters() => (new StringWriter(), new StringWriter());

    private static JsonDocument ParseJsonOutput(StringWriter stdout)
    {
        string text = stdout.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected non-empty stdout JSON output.");
        return JsonDocument.Parse(text);
    }

    private string CreateSampleDataFolder(string folderName = "DataInput")
    {
        string dir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "README.TXT"), "CLI sample data file.");
        File.WriteAllBytes(Path.Combine(dir, "SAMPLE.BIN"), new byte[4096]);

        string subDir = Path.Combine(dir, "SUBDIR");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "NESTED.TXT"), "Nested content for directory recursion testing.");

        return dir;
    }

    private static byte[] GenerateSineWavePcm(double frequency, int frameCount, short amplitude = 16000)
    {
        int sampleRate = OpticalConstants.CdAudioSampleRate;
        int totalStereoSamples = frameCount * OpticalConstants.CdAudioSamplesPerFrame;
        byte[] pcm = new byte[totalStereoSamples * OpticalConstants.CdAudioBytesPerSample];

        for (int i = 0; i < totalStereoSamples; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)Math.Round(amplitude * Math.Sin(2.0 * Math.PI * frequency * t));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), sample);
        }

        return pcm;
    }

    #region 1. drives command tests

    [Fact]
    public async Task Drives_Json_ReturnsZeroAndListsConfiguredDrivesWithTelemetry()
    {
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "drives", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        var drivesArray = root.GetProperty("drives");
        Assert.True(drivesArray.GetArrayLength() >= 1);

        var firstDrive = drivesArray[0];
        Assert.Equal("VDRV-01", firstDrive.GetProperty("uniqueId").GetString());
        Assert.Equal("IGNIS", firstDrive.GetProperty("vendorId").GetString());
        Assert.Equal("VIRTUAL DVD/CD-RW", firstDrive.GetProperty("productId").GetString());
        Assert.True(firstDrive.GetProperty("isVirtual").GetBoolean());

        var caps = firstDrive.GetProperty("capabilities");
        Assert.True(caps.GetProperty("canWriteCd").GetBoolean());
        Assert.True(caps.GetProperty("canWriteDvd").GetBoolean());
        Assert.True(caps.GetProperty("canReadCd").GetBoolean());
        Assert.True(caps.GetProperty("canReadDvd").GetBoolean());
    }

    [Fact]
    public async Task Drives_HumanReadable_ReturnsZeroAndOutputsFormattedTable()
    {
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "drives" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        string output = stdout.ToString();
        Assert.Contains("VDRV-01", output);
        Assert.Contains("IGNIS", output);
        Assert.Contains("VIRTUAL DVD/CD-RW", output);
        Assert.Contains("Virtual", output);
    }

    #endregion

    #region 2. media-info command tests

    [Fact]
    public async Task MediaInfo_Json_ReturnsZeroAndReportsAccurateMediaTelemetry()
    {
        // 1. When media is blank CD-RW
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "media-info", "--drive", "VDRV-01", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.True(root.GetProperty("mediaPresent").GetBoolean());
        Assert.True(root.GetProperty("isBlank").GetBoolean());
        Assert.True(root.GetProperty("isRewritable").GetBoolean());
        Assert.False(root.GetProperty("isFinalized").GetBoolean());
        Assert.Equal(360000, root.GetProperty("totalSectors").GetInt32());
    }

    [Fact]
    public async Task MediaInfo_Json_AddsTheDrivesFormattedCapacityAndWriteSpeeds()
    {
        using var medium = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdRe);
        _recorder.InsertMedia(medium);
        _recorder.SimulatedDriveMediaReport = new DriveMediaReport(
            new FormattedCapacityInfo(11_826_176, 2048, FormattedCapacityState.Formatted,
                [new FormattableCapacity(0x30, 11_826_176, 0x3000)]),
            [new DriveWriteSpeed(8_991, 11_826_175, 8_991, Exact: true)]);

        var (stdout, stderr) = CreateStringWriters();
        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "media-info", "--drive", "VDRV-01", "--json" }, stdout, stderr, _deviceService, _burnEngine);

        Assert.Equal(0, exitCode);
        using var doc = ParseJsonOutput(stdout);
        var formatted = doc.RootElement.GetProperty("formattedCapacity");
        Assert.Equal("formatted", formatted.GetProperty("state").GetString());
        Assert.Equal(11_826_176L * 2048, formatted.GetProperty("bytes").GetInt64());
        Assert.Equal("0x30", formatted.GetProperty("formats")[0].GetProperty("formatType").GetString());
        var speed = doc.RootElement.GetProperty("writeSpeedsFromDrive")[0];
        Assert.Equal(2.0, speed.GetProperty("multiplier").GetDouble());
        Assert.Equal(8_991, speed.GetProperty("kilobytesPerSecond").GetInt32());
        Assert.True(speed.GetProperty("exact").GetBoolean());
    }

    [Fact]
    public async Task MediaInfo_OnACdRw_LeavesOutFormattedCapacity()
    {
        // The real GP60NB50 reports the recorded size of a CD-RW as "formatted"; that is not a capacity.
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);
        _recorder.SimulatedDriveMediaReport = new DriveMediaReport(
            new FormattedCapacityInfo(10_066, 2048, FormattedCapacityState.Formatted, []),
            [new DriveWriteSpeed(1_764, 359_849, 1_764, Exact: false)]);

        var (stdout, stderr) = CreateStringWriters();
        await CliCommandRunner.RunAsync(
            new[] { "media-info", "--drive", "VDRV-01", "--json" }, stdout, stderr, _deviceService, _burnEngine);

        using var doc = ParseJsonOutput(stdout);
        Assert.False(doc.RootElement.TryGetProperty("formattedCapacity", out _)); // empty values are left out of the JSON
        Assert.Equal(10.0, doc.RootElement.GetProperty("writeSpeedsFromDrive")[0].GetProperty("multiplier").GetDouble());

        // Plain-text output names the speed too.
        var (text, _) = CreateStringWriters();
        await CliCommandRunner.RunAsync(new[] { "media-info", "--drive", "VDRV-01" }, text, stderr, _deviceService, _burnEngine);
        Assert.Contains("Drive's Write Speeds:  10x", text.ToString());
        Assert.DoesNotContain("Formatted Capacity", text.ToString());
    }

    [Fact]
    public async Task MediaInfo_WhenTheDriveSaysNothing_HasEmptyFields()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        var (stdout, stderr) = CreateStringWriters();
        await CliCommandRunner.RunAsync(
            new[] { "media-info", "--drive", "VDRV-01", "--json" }, stdout, stderr, _deviceService, _burnEngine);

        using var doc = ParseJsonOutput(stdout);
        Assert.False(doc.RootElement.TryGetProperty("formattedCapacity", out _)); // empty values are left out of the JSON
        Assert.Equal(0, doc.RootElement.GetProperty("writeSpeedsFromDrive").GetArrayLength());
    }

    [Fact]
    public async Task MediaInfo_NoMediaPresent_ReturnsZeroAndReportsMediaPresentFalse()
    {
        // Empty drive
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "media-info", "--drive", "VDRV-01", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.False(root.GetProperty("mediaPresent").GetBoolean());
    }

    #endregion

    #region 3. build-iso command tests

    [Fact]
    public async Task BuildIso_Json_CompilesIsoAndEmitsJsonTelemetry()
    {
        string inputFolder = CreateSampleDataFolder("IsoSource");
        string outputIso = Path.Combine(_tempDir, "compiled.iso");

        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "build-iso", "--input", inputFolder, "--output", outputIso, "--label", "MY_CLI_ISO", "--udf", "--joliet", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputIso));
        Assert.True(new FileInfo(outputIso).Length > 0);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(Path.GetFullPath(outputIso), root.GetProperty("outputPath").GetString());
        Assert.Equal("MY_CLI_ISO", root.GetProperty("volumeLabel").GetString());
        Assert.True(root.GetProperty("totalSectors").GetInt32() > 0);
        Assert.True(root.GetProperty("hasIso9660").GetBoolean());
        Assert.True(root.GetProperty("hasJoliet").GetBoolean());
        Assert.True(root.GetProperty("hasUdf").GetBoolean());
        Assert.False(root.GetProperty("isBootable").GetBoolean());
    }

    [Fact]
    public async Task BuildIso_WithBootOptions_CompilesBootableIso()
    {
        string inputFolder = CreateSampleDataFolder("BootIsoSource");
        string outputIso = Path.Combine(_tempDir, "bootable.iso");
        string bootImg = Path.Combine(_tempDir, "boot.img");
        File.WriteAllBytes(bootImg, new byte[2048]);

        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "build-iso", "--input", inputFolder, "--output", outputIso, "--boot-bios", bootImg, "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outputIso));

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("isBootable").GetBoolean());
    }

    [Fact]
    public async Task BuildIso_FixedTime_SameFolderTouchedLater_GivesByteIdenticalImages()
    {
        string inputFolder = CreateSampleDataFolder("ReproSource");
        string first = Path.Combine(_tempDir, "repro1.iso");
        string second = Path.Combine(_tempDir, "repro2.iso");
        string[] Args(string output) => new[] { "build-iso", "--input", inputFolder, "--output", output, "--fixed-time", "1767225600" };

        var (stdout, stderr) = CreateStringWriters();
        Assert.Equal(0, await CliCommandRunner.RunAsync(Args(first), stdout, stderr, _deviceService, _burnEngine));

        // Touch every file and folder as a fresh checkout would; all times stay later than the fixed time.
        foreach (string path in Directory.EnumerateFileSystemEntries(inputFolder, "*", SearchOption.AllDirectories).Append(inputFolder))
        {
            if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
            else Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
        }

        Assert.Equal(0, await CliCommandRunner.RunAsync(Args(second), stdout, stderr, _deviceService, _burnEngine));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(first))),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(second))));
    }

    [Theory]
    [InlineData("1767225600", 2026, 1, 1)]
    [InlineData("2026-01-01T00:00:00Z", 2026, 1, 1)]
    [InlineData("2026-01-01", 2026, 1, 1)]
    [InlineData("2025-12-31T19:00:00-05:00", 2026, 1, 1)]
    public void ParseFixedTimestamp_AcceptsUnixSecondsAndIso8601AsUtc(string value, int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), CliCommandRunner.ParseFixedTimestamp(value));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("0")]            // 1970: before what the builder records
    [InlineData("99999999999999")]
    public void ParseFixedTimestamp_RejectsUnusableValues(string value)
    {
        Assert.Throws<ArgumentException>(() => CliCommandRunner.ParseFixedTimestamp(value));
    }

    [Theory]
    [InlineData("1.02", 0x0102)]
    [InlineData("2.01", 0x0201)]
    [InlineData("2.50", 0x0250)]
    [InlineData("260", 0x0260)]
    [InlineData("0x0150", 0x0150)]
    public void ParseUdfRevision_AcceptsTheSupportedRevisions(string value, int expected)
    {
        Assert.Equal((ushort)expected, CliCommandRunner.ParseUdfRevision(value));
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("3.00")]
    [InlineData("1.01")]
    [InlineData("udf")]
    public void ParseUdfRevision_RejectsOthers(string value)
    {
        Assert.Throws<ArgumentException>(() => CliCommandRunner.ParseUdfRevision(value));
    }

    #endregion

    #region 4. burn-iso command tests

    [Fact]
    public async Task BurnIso_DriveVdrv01_WithVerifyAndJson_BurnsAndVerifiesSuccessfully()
    {
        // 1. Build an ISO image
        string inputFolder = CreateSampleDataFolder("BurnIsoSource");
        string outputIso = Path.Combine(_tempDir, "to_burn.iso");

        var (buildOut, buildErr) = CreateStringWriters();
        int buildCode = await CliCommandRunner.RunAsync(
            new[] { "build-iso", "--input", inputFolder, "--output", outputIso, "--label", "BURN_TEST", "--json" },
            buildOut,
            buildErr,
            _deviceService,
            _burnEngine);
        Assert.Equal(0, buildCode);

        // 2. Insert blank rewritable medium
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        // 3. Burn ISO with --verify and --json
        var (burnOut, burnErr) = CreateStringWriters();
        int burnCode = await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", "VDRV-01", "--iso", outputIso, "--speed", "16x", "--verify", "--close", "--json" },
            burnOut,
            burnErr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, burnCode);

        using var doc = ParseJsonOutput(burnOut);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.True(root.GetProperty("sectorsWritten").GetInt32() > 0);
        Assert.True(root.GetProperty("verified").GetBoolean());
        Assert.True(_recorder.CurrentMedium?.IsFinalized);
    }

    [Fact]
    public async Task BurnIso_StrictVerify_ImpliesVerify_AndTheBurnIsChecked()
    {
        string inputFolder = CreateSampleDataFolder("StrictVerifySource");
        string outputIso = Path.Combine(_tempDir, "strict.iso");
        Assert.Equal(0, await CliCommandRunner.RunAsync(
            new[] { "build-iso", "--input", inputFolder, "--output", outputIso, "--label", "STRICT", "--json" },
            new StringWriter(), new StringWriter(), _deviceService, _burnEngine));

        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        var (burnOut, burnErr) = CreateStringWriters();
        int burnCode = await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", "VDRV-01", "--iso", outputIso, "--strict-verify", "--json" },
            burnOut, burnErr, _deviceService, _burnEngine);

        Assert.Equal(0, burnCode);
        using var doc = ParseJsonOutput(burnOut);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
    }

    private async Task<string> BuildSmallIsoAsync(string name)
    {
        string inputFolder = CreateSampleDataFolder(name + "Source");
        string iso = Path.Combine(_tempDir, name + ".iso");
        Assert.Equal(0, await CliCommandRunner.RunAsync(
            new[] { "build-iso", "--input", inputFolder, "--output", iso, "--label", "DEFAULTS", "--json" },
            new StringWriter(), new StringWriter(), _deviceService, _burnEngine));
        return iso;
    }

    [Fact]
    public async Task BurnIso_WithNoOptions_UsesTheRecommendedSpeed_AndChecksTheDisc()
    {
        string iso = await BuildSmallIsoAsync("defaults");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (burnOut, burnErr) = CreateStringWriters();
        Assert.Equal(0, await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", "VDRV-01", "--iso", iso, "--json" }, burnOut, burnErr, _deviceService, _burnEngine));

        using var doc = ParseJsonOutput(burnOut);
        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        // The virtual drive offers 8x to 48x for CD-R and says nothing about rotation: the 16x ceiling applies.
        Assert.Equal(16.0, doc.RootElement.GetProperty("averageSpeedMultiplier").GetDouble(), 1);
    }

    [Fact]
    public async Task BurnIso_SpeedMaxAndNoVerify_AreTheOptOuts()
    {
        string iso = await BuildSmallIsoAsync("optout");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (burnOut, burnErr) = CreateStringWriters();
        Assert.Equal(0, await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", "VDRV-01", "--iso", iso, "--speed", "max", "--no-verify", "--json" },
            burnOut, burnErr, _deviceService, _burnEngine));

        using var doc = ParseJsonOutput(burnOut);
        Assert.False(doc.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal(48.0, doc.RootElement.GetProperty("averageSpeedMultiplier").GetDouble(), 1);
    }

    [Fact]
    public async Task BurnIso_UnreadableSpeed_IsRefused_AndNothingIsWritten()
    {
        // Before, "--speed 8xx" silently meant the fastest speed.
        string iso = await BuildSmallIsoAsync("typo");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (burnOut, burnErr) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", "VDRV-01", "--iso", iso, "--speed", "8xx" }, burnOut, burnErr, _deviceService, _burnEngine);

        Assert.NotEqual(0, code);
        Assert.Contains("--speed", burnErr.ToString());
        Assert.True(_recorder.CurrentMedium?.IsBlank);
    }

    [Fact]
    public async Task BurnIso_Native_WritesDirectly_ReadsBack_AndReportsTheWriter()
    {
        string inputFolder = CreateSampleDataFolder("NativeIsoSource");
        string outputIso = Path.Combine(_tempDir, "native.iso");
        Assert.Equal(0, await CliCommandRunner.RunAsync(new[] { "build-iso", "--input", inputFolder, "--output", outputIso },
            new StringWriter(), new StringWriter(), _deviceService, _burnEngine));
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);
        string logs = Path.Combine(_tempDir, "native-logs");

        var (burnOut, burnErr) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", "VDRV-01", "--iso", outputIso, "--native", "--log-dir", logs, "--json" },
            burnOut, burnErr, _deviceService, _burnEngine);

        Assert.True(code == 0, burnErr.ToString());
        using var doc = ParseJsonOutput(burnOut);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("verified").GetBoolean()); // --native always reads back
        var direct = root.GetProperty("directWrite");
        Assert.Equal("direct", direct.GetProperty("writer").GetString());
        Assert.True(File.Exists(direct.GetProperty("commandLog").GetString()));
        Assert.True(medium.IsFinalized);
    }

    [Fact]
    public async Task BurnData_NativeTestWrite_LeavesTheDiscBlank_AndOptionsNeedNative()
    {
        string inputFolder = CreateSampleDataFolder("NativeDataSource");
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        var (outText, errText) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", "VDRV-01", "--input", inputFolder, "--native", "--test-write", "--log-dir", Path.Combine(_tempDir, "nl") },
            outText, errText, _deviceService, _burnEngine);
        Assert.True(code == 0, errText.ToString());
        Assert.Contains("direct drive commands", outText.ToString());
        Assert.True(medium.IsBlank);

        var (_, err2) = CreateStringWriters();
        int refused = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", "VDRV-01", "--input", inputFolder, "--test-write" },
            new StringWriter(), err2, _deviceService, _burnEngine);
        Assert.NotEqual(0, refused);
        Assert.Contains("need --native", err2.ToString());
        Assert.True(medium.IsBlank);
    }

    [Fact]
    public async Task BurnData_Native_AddsASessionToAnOpenDisc_AndKeepsTheEarlierFiles()
    {
        string first = CreateSampleDataFolder("NativeSessionOne");
        string second = Path.Combine(_tempDir, "NativeSessionTwo");
        Directory.CreateDirectory(second);
        byte[] added = new byte[50_000];
        new Random(15).NextBytes(added);
        File.WriteAllBytes(Path.Combine(second, "ADDED.BIN"), added);
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);
        string logs = Path.Combine(_tempDir, "append-logs");

        var (out1, err1) = CreateStringWriters();
        Assert.True(0 == await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", "VDRV-01", "--input", first, "--native", "--no-close", "--log-dir", logs, "--json" },
            out1, err1, _deviceService, _burnEngine), err1.ToString());
        Assert.False(medium.IsFinalized);
        int next = medium.NextWritableLba;

        var (out2, err2) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", "VDRV-01", "--input", second, "--native", "--log-dir", logs, "--json" },
            out2, err2, _deviceService, _burnEngine);

        Assert.True(code == 0, err2.ToString() + out2);
        using var doc = ParseJsonOutput(out2);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("verified").GetBoolean());
        var direct = root.GetProperty("directWrite");
        Assert.Equal("direct", direct.GetProperty("writer").GetString());
        Assert.Contains($"Session 2 was added at sector {next:N0}", direct.GetProperty("writerMessage").GetString());
        Assert.Equal(2, medium.Sessions.Count);
        Assert.Equal(next, medium.Sessions[1].StartLba);
        Assert.True(medium.IsFinalized);

        using var reader = CdBurner.Core.Iso.IsoImageReader.FromStream(medium.CreateReadStream(), sessionStartLba: next);
        Assert.Equal(added, reader.ReadFileBytes("ADDED.BIN"));
        Assert.Equal("CLI sample data file.", Encoding.UTF8.GetString(reader.ReadFileBytes("README.TXT")));
        Assert.Equal("Nested content for directory recursion testing.", Encoding.UTF8.GetString(reader.ReadFileBytes("SUBDIR/NESTED.TXT")));
    }

    #endregion

    #region 5. scan command tests

    [Fact]
    public async Task Scan_DriveVdrv01_Json_PerformsSurfaceScanAndReturnsAccurateReport()
    {
        // 1. Ensure drive has burned media
        string inputFolder = CreateSampleDataFolder("ScanSource");
        string outputIso = Path.Combine(_tempDir, "for_scan.iso");

        await CliCommandRunner.RunAsync(
            new[] { "build-iso", "--input", inputFolder, "--output", outputIso },
            new StringWriter(), new StringWriter(), _deviceService, _burnEngine);

        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", "VDRV-01", "--iso", outputIso },
            new StringWriter(), new StringWriter(), _deviceService, _burnEngine);

        // 2. Execute surface scan
        var (stdout, stderr) = CreateStringWriters();
        int scanCode = await CliCommandRunner.RunAsync(
            new[] { "scan", "--drive", "VDRV-01", "--block-size", "128", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, scanCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.True(root.GetProperty("totalSectorsScanned").GetInt32() > 0);
        Assert.True(root.GetProperty("goodBlocks").GetInt32() > 0);
        Assert.Equal(0, root.GetProperty("damagedBlocks").GetInt32());
        Assert.True(root.GetProperty("averageSpeedKbPerSec").GetDouble() > 0);
    }

    #endregion

    #region 6. erase command tests

    [Fact]
    public async Task Erase_DriveVdrv01_Json_ErasesMediaSuccessfully()
    {
        // 1. Insert blank CD-RW and burn something so it is not blank
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        string inputFolder = CreateSampleDataFolder("EraseSource");
        string outputIso = Path.Combine(_tempDir, "to_erase.iso");
        await CliCommandRunner.RunAsync(new[] { "build-iso", "--input", inputFolder, "--output", outputIso }, new StringWriter(), new StringWriter(), _deviceService, _burnEngine);
        await CliCommandRunner.RunAsync(new[] { "burn-iso", "--drive", "VDRV-01", "--iso", outputIso }, new StringWriter(), new StringWriter(), _deviceService, _burnEngine);

        Assert.False(_recorder.CurrentMedium?.IsBlank);

        // 2. Execute erase command
        var (stdout, stderr) = CreateStringWriters();
        int eraseCode = await CliCommandRunner.RunAsync(
            new[] { "erase", "--drive", "VDRV-01", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, eraseCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.False(root.GetProperty("fullErase").GetBoolean());

        // Disc medium must now be blank
        Assert.True(_recorder.CurrentMedium?.IsBlank);
    }

    [Fact]
    public async Task Erase_NonRewritableMedia_FailsWithNonZeroExitCode()
    {
        // CD-R (Write-Once, non-rewritable)
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (stdout, stderr) = CreateStringWriters();
        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "erase", "--drive", "VDRV-01", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.NotEqual(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("not rewritable", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region 7. burn-data command tests

    [Fact]
    public async Task BurnData_Json_CompilesAndBurnsFolderDirectlyToDisc()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        string inputFolder = CreateSampleDataFolder("BurnDataFolder");

        var (stdout, stderr) = CreateStringWriters();
        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", "VDRV-01", "--input", inputFolder, "--label", "BURNDATA_TEST", "--verify", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.Equal("BURNDATA_TEST", root.GetProperty("volumeLabel").GetString());
        Assert.True(root.GetProperty("sectorsWritten").GetInt32() > 0);
        Assert.True(root.GetProperty("verified").GetBoolean());
    }

    #endregion

    #region 8. burn-audio command tests

    [Fact]
    public async Task BurnAudio_Json_BurnsWavTracksToAudioCd()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        byte[] pcm1 = GenerateSineWavePcm(440.0, 50);
        byte[] pcm2 = GenerateSineWavePcm(880.0, 75);

        string wav1 = Path.Combine(_tempDir, "track1.wav");
        string wav2 = Path.Combine(_tempDir, "track2.wav");
        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);

        var (stdout, stderr) = CreateStringWriters();
        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-audio", "--drive", "VDRV-01", "--tracks", $"{wav1},{wav2}", "--title", "CLI Harmonics", "--artist", "Acoustic AI", "--dao", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.Equal(2, root.GetProperty("trackCount").GetInt32());
        Assert.Equal(125, root.GetProperty("sectorsWritten").GetInt32());
        Assert.Equal("CLI Harmonics", root.GetProperty("title").GetString());
        Assert.Equal("Acoustic AI", root.GetProperty("artist").GetString());
    }

    private (string Wav1, string Wav2) WriteNoiseTracks()
    {
        // Direct Session-at-Once writing needs tracks of at least 4 seconds (300 frames).
        byte[] pcm1 = new byte[300 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[320 * OpticalConstants.RawSectorSize];
        new Random(1).NextBytes(pcm1);
        new Random(2).NextBytes(pcm2);
        string wav1 = Path.Combine(_tempDir, "noise1.wav");
        string wav2 = Path.Combine(_tempDir, "noise2.wav");
        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);
        return (wav1, wav2);
    }

    private async Task<(int ExitCode, StringWriter Stdout, StringWriter Stderr)> RunNativeBurn(params string[] extra)
    {
        var (wav1, wav2) = WriteNoiseTracks();
        var (stdout, stderr) = CreateStringWriters();
        var args = new List<string>
        {
            "burn-audio", "--drive", "VDRV-01", "--tracks", $"{wav1},{wav2}", "--title", "Direct Album", "--artist", "Direct Artist",
            "--dao", "--native", "--log-dir", Path.Combine(_tempDir, "logs"), "--json"
        };
        args.AddRange(extra);
        int exitCode = await CliCommandRunner.RunAsync(args.ToArray(), stdout, stderr, _deviceService, _burnEngine);
        return (exitCode, stdout, stderr);
    }

    [Fact]
    public async Task BurnAudio_Native_RecordsTheDisc_ReadsItBack_AndWritesCdText()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (exitCode, stdout, stderr) = await RunNativeBurn();

        Assert.True(exitCode == 0, stdout.ToString() + stderr);
        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("native").GetBoolean());
        Assert.False(root.GetProperty("testWrite").GetBoolean());
        Assert.True(root.GetProperty("discRecorded").GetBoolean());
        Assert.True(root.GetProperty("verified").GetBoolean());
        Assert.Equal(620L * 588, root.GetProperty("samplesCompared").GetInt64());
        Assert.True(File.Exists(root.GetProperty("commandLog").GetString()));

        Assert.True(medium.IsFinalized);
        var cdText = CdTextPackParser.ParsePacks(medium.CdTextBytes!);
        Assert.Equal("Direct Album", cdText.AlbumTitle);
        Assert.Equal(2, cdText.Tracks!.Count);
    }

    [Fact]
    public async Task BurnAudio_NativeTestWrite_LeavesTheDiscBlank()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (exitCode, stdout, stderr) = await RunNativeBurn("--test-write");

        Assert.True(exitCode == 0, stdout.ToString() + stderr);
        using var doc = ParseJsonOutput(stdout);
        Assert.True(doc.RootElement.GetProperty("testWrite").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("discStillBlank").GetBoolean());
        Assert.True(medium.IsBlank);
    }

    [Fact]
    public async Task BurnAudio_NativeTestFirst_StopsAfterTheTest_WhenTheDriveNeedsAReload()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (exitCode, stdout, _) = await RunNativeBurn("--test-first");

        Assert.Equal(1, exitCode);
        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("testWritePassedFirst").GetBoolean());
        Assert.Contains("reloaded", root.GetProperty("error").GetString());
        Assert.True(medium.IsBlank);
    }

    /// <summary>A mixed-mode rip: a Mode 1 data track (300 sectors and its 150-sector post-gap), then one audio track.</summary>
    private string WriteMixedCue()
    {
        var bin = new List<byte>();
        var user = new byte[2048];
        var random = new Random(9);
        for (int lba = 0; lba < 450; lba++)
        {
            if (lba < 300) random.NextBytes(user); else Array.Clear(user);
            bin.AddRange(CdBurner.Core.Images.RawSectorCodec.Convert2048To2352Mode1(user, lba));
        }
        var audio = new byte[320 * OpticalConstants.RawSectorSize];
        random.NextBytes(audio);
        bin.AddRange(new byte[150 * OpticalConstants.RawSectorSize]);
        bin.AddRange(audio);
        File.WriteAllBytes(Path.Combine(_tempDir, "mixed.bin"), bin.ToArray());
        string cue = Path.Combine(_tempDir, "mixed.cue");
        File.WriteAllLines(cue, new[]
        {
            "FILE \"mixed.bin\" BINARY", "  TRACK 01 MODE1/2352", "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO", "    INDEX 00 00:06:00", "    INDEX 01 00:08:00"
        });
        return cue;
    }

    [Fact]
    public async Task BurnAudio_NativeMixedCue_WritesTheDataTrackAndTheAudio_AndVerifies()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-audio", "--drive", "VDRV-01", "--tracks", WriteMixedCue(), "--dao", "--native", "--log-dir", Path.Combine(_tempDir, "logs"), "--json" },
            stdout, stderr, _deviceService, _burnEngine);

        Assert.True(exitCode == 0, stdout.ToString() + stderr);
        using var doc = ParseJsonOutput(stdout);
        Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("trackCount").GetInt32());
        var tracks = Assert.Single(medium.Sessions).Tracks;
        Assert.Equal(new[] { false, true }, tracks.Select(t => t.IsAudio));
        Assert.Equal(600, tracks[1].StartLba);
    }

    [Fact]
    public async Task BurnAudio_MixedCueWithoutNative_IsRefused_AndTheDiscStaysBlank()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-audio", "--drive", "VDRV-01", "--tracks", WriteMixedCue(), "--dao" },
            stdout, stderr, _deviceService, _burnEngine);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Only direct drive commands can write one", stdout.ToString() + stderr);
        Assert.True(medium.IsBlank);
    }

    private async Task<(int Exit, StringWriter Out, StringWriter Err)> Cli(params string[] args)
    {
        var (stdout, stderr) = CreateStringWriters();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (exit, stdout, stderr);
    }

    [Fact]
    public async Task VerifyCue_ChecksAMixedModeDiscAgainstItsCue_WithoutWriting()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);
        string cue = WriteMixedCue();
        var burn = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--log-dir", Path.Combine(_tempDir, "logs"));
        Assert.True(burn.Exit == 0, burn.Out.ToString() + burn.Err);

        var (exit, stdout, stderr) = await Cli("verify", "--drive", "VDRV-01", "--cue", cue, "--log-dir", Path.Combine(_tempDir, "logs"), "--json");

        Assert.True(exit == 0, stdout.ToString() + stderr);
        using var doc = ParseJsonOutput(stdout);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("the data track's 300 sectors", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal((150L + 320) * 588, doc.RootElement.GetProperty("samplesCompared").GetInt64()); // the silent pre-gap, then the audio
        Assert.True(medium.IsFinalized);
    }

    [Fact]
    public async Task VerifyCue_FindsASourceThatNoLongerMatchesTheDisc()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);
        string cue = WriteMixedCue();
        Assert.Equal(0, (await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--log-dir", Path.Combine(_tempDir, "logs"))).Exit);

        // One audio sample of the source changes after the burn.
        string bin = Path.Combine(_tempDir, "mixed.bin");
        byte[] bytes = File.ReadAllBytes(bin);
        int at = (600 + 100) * OpticalConstants.RawSectorSize + 1000;
        bytes[at] ^= 0x5A;
        File.WriteAllBytes(bin, bytes);

        var (exit, stdout, stderr) = await Cli("verify", "--drive", "VDRV-01", "--cue", cue, "--log-dir", Path.Combine(_tempDir, "logs"));

        Assert.Equal(1, exit);
        Assert.Contains("Disc check FAILED", stderr.ToString());
    }

    [Fact]
    public async Task VerifyCue_RefusesABlankDisc_AndADiscTheDirectWriterDidNotWrite()
    {
        using var blank = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(blank);
        string cue = WriteMixedCue();

        var (exitBlank, _, errBlank) = await Cli("verify", "--drive", "VDRV-01", "--cue", cue);
        Assert.Equal(1, exitBlank);
        Assert.Contains("nothing to check", errBlank.ToString());

        var (wav1, wav2) = WriteNoiseTracks();
        Assert.Equal(0, (await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", $"{wav1},{wav2}", "--dao")).Exit); // the Windows path
        string audioCue = Path.Combine(_tempDir, "two.cue");
        File.WriteAllLines(audioCue, new[]
        {
            "FILE \"noise1.wav\" WAVE", "  TRACK 01 AUDIO", "    INDEX 01 00:00:00",
            "FILE \"noise2.wav\" WAVE", "  TRACK 02 AUDIO", "    INDEX 01 00:00:00"
        });

        var (exit, _, stderr) = await Cli("verify", "--drive", "VDRV-01", "--cue", audioCue);
        Assert.Equal(1, exit);
        Assert.Contains("not written with direct drive commands", stderr.ToString());
    }

    [Fact]
    public async Task BurnAudio_TestFirstWithoutNative_IsRejected()
    {
        var (wav1, wav2) = WriteNoiseTracks();
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-audio", "--drive", "VDRV-01", "--tracks", $"{wav1},{wav2}", "--test-first" },
            stdout, stderr, _deviceService, _burnEngine);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--test-first only applies", stdout.ToString() + stderr);
    }

    #endregion

    #region 9. burn-dvd command tests

    [Fact]
    public async Task BurnDvd_Json_ValidatesAndBurnsDvdVideoTree()
    {
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        _recorder.InsertMedia(medium);

        // Build valid synthetic VIDEO_TS structure
        string dvdDir = Path.Combine(_tempDir, "DvdSource");
        string videoTsDir = Path.Combine(dvdDir, "VIDEO_TS");
        Directory.CreateDirectory(videoTsDir);

        byte[] vmgBytes = DvdVideoValidator.CreateSyntheticVmgIfo();
        byte[] vtsBytes = DvdVideoValidator.CreateSyntheticVtsIfo();

        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.IFO"), vmgBytes);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.VOB"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.BUP"), (byte[])vmgBytes.Clone());

        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.IFO"), vtsBytes);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.VOB"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_1.VOB"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.BUP"), (byte[])vtsBytes.Clone());

        var (stdout, stderr) = CreateStringWriters();
        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-dvd", "--drive", "VDRV-01", "--video-ts", dvdDir, "--label", "DVD_CLI_TEST", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.Equal("DVD_CLI_TEST", root.GetProperty("volumeLabel").GetString());
        Assert.True(root.GetProperty("sectorsWritten").GetInt32() > 0);
    }

    [Fact]
    public async Task BurnDvd_Native_WritesDirectly_AndReadsBack()
    {
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        _recorder.InsertMedia(medium);

        string dvdDir = Path.Combine(_tempDir, "DvdNativeSource");
        string videoTsDir = Path.Combine(dvdDir, "VIDEO_TS");
        Directory.CreateDirectory(videoTsDir);
        byte[] vmg = DvdVideoValidator.CreateSyntheticVmgIfo();
        byte[] vts = DvdVideoValidator.CreateSyntheticVtsIfo();
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.IFO"), vmg);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.VOB"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VIDEO_TS.BUP"), (byte[])vmg.Clone());
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.IFO"), vts);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.VOB"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_1.VOB"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(videoTsDir, "VTS_01_0.BUP"), (byte[])vts.Clone());
        string logs = Path.Combine(_tempDir, "dvd-native-logs");

        var (stdout, stderr) = CreateStringWriters();
        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-dvd", "--drive", "VDRV-01", "--video-ts", dvdDir, "--native", "--log-dir", logs, "--json" },
            stdout, stderr, _deviceService, _burnEngine);

        Assert.True(exitCode == 0, stderr.ToString());
        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("verified").GetBoolean());
        var direct = root.GetProperty("directWrite");
        Assert.Equal("direct", direct.GetProperty("writer").GetString());
        Assert.True(File.Exists(direct.GetProperty("commandLog").GetString()));
        Assert.True(medium.IsFinalized);
    }

    [Fact]
    public async Task BurnDvd_DirectMkvFile_AutoAuthorsMenuAndBurnsSuccessfully()
    {
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        _recorder.InsertMedia(medium);

        string mkvPath = Path.Combine(_tempDir, "SampleMovie.mkv");
        File.WriteAllBytes(mkvPath, new byte[65536]);

        var (stdout, stderr) = CreateStringWriters();
        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-dvd", "--drive", "VDRV-01", "--video-ts", mkvPath, "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("sectorsWritten").GetInt32() > 256);
    }

    #endregion

    #region 10. rip and verify command tests

    [Fact]
    public async Task Rip_And_Verify_Json_RipsBurnedDiscAndVerifiesBitIdentical()
    {
        // 1. Build and burn an ISO
        string inputFolder = CreateSampleDataFolder("RipSource");
        string originalIso = Path.Combine(_tempDir, "rip_orig.iso");
        await CliCommandRunner.RunAsync(new[] { "build-iso", "--input", inputFolder, "--output", originalIso }, new StringWriter(), new StringWriter(), _deviceService, _burnEngine);

        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);
        await CliCommandRunner.RunAsync(new[] { "burn-iso", "--drive", "VDRV-01", "--iso", originalIso }, new StringWriter(), new StringWriter(), _deviceService, _burnEngine);

        // 2. Rip disc to new ISO
        string rippedIso = Path.Combine(_tempDir, "ripped.iso");
        var (ripOut, ripErr) = CreateStringWriters();
        int ripCode = await CliCommandRunner.RunAsync(
            new[] { "rip", "--drive", "VDRV-01", "--output", rippedIso, "--json" },
            ripOut,
            ripErr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, ripCode);

        using var ripDoc = ParseJsonOutput(ripOut);
        Assert.True(ripDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(ripDoc.RootElement.GetProperty("sectorsRead").GetInt32() > 0);
        Assert.Equal(0, ripDoc.RootElement.GetProperty("badSectorsCount").GetInt32());

        // 3. Verify disc against original ISO
        var (verifyOut, verifyErr) = CreateStringWriters();
        int verifyCode = await CliCommandRunner.RunAsync(
            new[] { "verify", "--drive", "VDRV-01", "--iso", originalIso, "--json" },
            verifyOut,
            verifyErr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, verifyCode);

        using var verifyDoc = ParseJsonOutput(verifyOut);
        var vRoot = verifyDoc.RootElement;
        Assert.True(vRoot.GetProperty("success").GetBoolean());
        Assert.True(vRoot.GetProperty("isMatch").GetBoolean());
        Assert.Equal(0, vRoot.GetProperty("mismatchedLbaCount").GetInt32());

        // 4. The same check with every read forced from the disc is accepted and passes too
        var (strictOut, strictErr) = CreateStringWriters();
        int strictCode = await CliCommandRunner.RunAsync(
            new[] { "verify", "--drive", "VDRV-01", "--iso", originalIso, "--strict-verify", "--json" },
            strictOut,
            strictErr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, strictCode);
        using var strictDoc = ParseJsonOutput(strictOut);
        Assert.True(strictDoc.RootElement.GetProperty("isMatch").GetBoolean());
    }

    #endregion

    #region 11. Error handling and invalid argument tests

    [Theory]
    [InlineData("burn-iso", "--drive", "VDRV-01")] // Missing --iso
    [InlineData("burn-iso", "--iso", "dummy.iso")]  // Missing --drive
    [InlineData("build-iso", "--output", "out.iso")] // Missing --input
    [InlineData("build-iso", "--input", "in_dir")]   // Missing --output
    [InlineData("burn-data", "--drive", "VDRV-01")] // Missing --input
    [InlineData("burn-audio", "--drive", "VDRV-01")] // Missing --tracks
    [InlineData("burn-dvd", "--drive", "VDRV-01")]   // Missing --video-ts
    [InlineData("rip", "--drive", "VDRV-01")]        // Missing --output
    [InlineData("verify", "--drive", "VDRV-01")]     // Missing --iso
    [InlineData("erase")]                            // Missing --drive
    [InlineData("media-info")]                       // Missing --drive
    public async Task MissingRequiredArguments_WithJson_ReturnsNonZeroAndStructuredJsonError(params string[] args)
    {
        var fullArgs = args.Append("--json").ToArray();
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            fullArgs,
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.NotEqual(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.NotEmpty(root.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task NonExistentDrive_ReturnsNonZeroAndJsonError()
    {
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "media-info", "--drive", "DRIVE_DOES_NOT_EXIST", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.NotEqual(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains("not found", root.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonExistentFile_ReturnsNonZeroAndJsonError()
    {
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "burn-iso", "--drive", "VDRV-01", "--iso", Path.Combine(_tempDir, "missing.iso"), "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.NotEqual(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains("not found", root.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownSubcommand_ReturnsNonZeroAndJsonError()
    {
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "unrecognized-command", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.NotEqual(0, exitCode);

        using var doc = ParseJsonOutput(stdout);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Unknown command", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HelpAndVersionOptions_ReturnZeroExitCode()
    {
        var (hOut, hErr) = CreateStringWriters();
        int hCode = await CliCommandRunner.RunAsync(new[] { "--help" }, hOut, hErr, _deviceService, _burnEngine);
        Assert.Equal(0, hCode);
        string hText = hOut.ToString();
        Assert.Contains("Usage:", hText);
        Assert.StartsWith(LegalBanner.Line1, hText.TrimStart());
        Assert.Contains(LegalBanner.Line2, hText);

        var (vOut, vErr) = CreateStringWriters();
        int vCode = await CliCommandRunner.RunAsync(new[] { "--version" }, vOut, vErr, _deviceService, _burnEngine);
        Assert.Equal(0, vCode);
        string vText = vOut.ToString();
        Assert.Contains("CdBurner CLI", vText);
        Assert.StartsWith(LegalBanner.Line1, vText.TrimStart());
        Assert.Contains(LegalBanner.Line2, vText);
    }

    [Fact]
    public async Task Cli_JsonPayloads_IncludeEngineRootProperty()
    {
        var (stdout, stderr) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(new[] { "drives", "--json" }, stdout, stderr, _deviceService, _burnEngine);
        Assert.Equal(0, code);

        using var doc = ParseJsonOutput(stdout);
        Assert.True(doc.RootElement.TryGetProperty("engine", out var engineProp));
        Assert.Equal(LegalBanner.JsonEngine, engineProp.GetString());
    }

    [Fact]
    public async Task Cli_VersionWithJson_ReturnsNoticeAndEngine()
    {
        var (stdout, stderr) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(new[] { "--version", "--json" }, stdout, stderr, _deviceService, _burnEngine);
        Assert.Equal(0, code);

        using var doc = ParseJsonOutput(stdout);
        Assert.True(doc.RootElement.TryGetProperty("engine", out var engineProp));
        Assert.Equal(LegalBanner.JsonEngine, engineProp.GetString());
        Assert.True(doc.RootElement.TryGetProperty("notice", out var noticeProp));
        Assert.Equal(LegalBanner.VersionNotice, noticeProp.GetString());
    }

    #endregion

    #region Used rewritable discs (DVD+RW)

    [Fact]
    public async Task BurnData_UsedDvdPlusRw_NeedsOverwriteFlag()
    {
        string input = CreateSampleDataFolder();
        var recorder = new UsedDvdPlusRwRecorder();
        var engine = new OptionsCapturingEngine();
        var devices = new TestDeviceService(recorder);

        var (out1, err1) = CreateStringWriters();
        int refused = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", recorder.UniqueId, "--input", input }, out1, err1, devices, engine);

        Assert.Equal(1, refused);
        Assert.Contains("--overwrite", err1.ToString());
        Assert.Null(engine.Options);

        // --overwrite as the last argument, which is how the hardware test that caught the dropped switch ran it.
        var (out2, err2) = CreateStringWriters();
        int allowed = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", recorder.UniqueId, "--input", input, "--no-verify", "--overwrite" }, out2, err2, devices, engine); // this fake drive cannot read back

        Assert.True(allowed == 0, err2.ToString());
        Assert.True(engine.Options!.OverwriteExistingData);
    }

    private sealed class UsedDvdPlusRwRecorder : IDiscRecorderBackend
    {
        public string UniqueId => "PHYS-RW";
        public string VendorId => "HL-DT-ST";
        public string ProductId => "DVDRAM GP60NB50";
        public string ProductRevision => "RF02";
        public string VolumePath => @"H:\";
        public bool IsVirtual => false;
        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => new()
        {
            MediaPresent = true,
            MediaType = DiscMediaType.DvdPlusRw,
            IsBlank = false,
            IsRewritable = true,
            IsOverwriteOnly = true,
            TotalSectors = 2_295_104,
            FreeSectors = 2_295_104,
            NextWritableLba = 0
        };
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task BurnData_NamesTooLongForAnOpenDisc_AreReportedAsShortened_ButNotOnAClosedDisc()
    {
        string longName = "a file whose name is clearly longer than sixty-four characters in all.txt";
        Assert.True(longName.Length > 64);
        string dir = Path.Combine(_tempDir, "longnames");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, longName), "content");
        File.WriteAllText(Path.Combine(dir, "short.txt"), "content");

        async Task<JsonElement> Burn(params string[] extra)
        {
            using var medium = VirtualDiscMedium.CreateBlankCdRw700();
            _recorder.InsertMedia(medium);
            var (o, e) = CreateStringWriters();
            var args = new[] { "burn-data", "--drive", "VDRV-01", "--input", dir, "--label", "NAMES", "--json" }.Concat(extra).ToArray();
            Assert.Equal(0, await CliCommandRunner.RunAsync(args, o, e, _deviceService, _burnEngine));
            using var doc = ParseJsonOutput(o);
            return doc.RootElement.Clone();
        }

        // Left open for more sessions: no UDF, so the long name is cut to Joliet's 64 characters, and the result says so.
        var open = await Burn("--no-close");
        string warning = open.GetProperty("warning").GetString()!;
        Assert.Contains("1 name(s) longer than 64 characters were shortened", warning);
        Assert.Contains(longName, warning);

        // Closed disc: UDF carries the full name, nothing to report.
        var closed = await Burn();
        Assert.True(closed.GetProperty("success").GetBoolean());
        Assert.True(!closed.TryGetProperty("warning", out var none) || none.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task BurnData_MultiSessionCli_MergesPreviousSessionAndOmitsUdfWhenOpen()
    {
        string session1Dir = Path.Combine(_tempDir, "ms_s1");
        string session2Dir = Path.Combine(_tempDir, "ms_s2");
        Directory.CreateDirectory(Path.Combine(session1Dir, "Docs"));
        Directory.CreateDirectory(session2Dir);

        File.WriteAllText(Path.Combine(session1Dir, "Docs", "FirstSession.txt"), "kept across sessions");
        File.WriteAllText(Path.Combine(session1Dir, "Shared.txt"), "v1");
        File.WriteAllText(Path.Combine(session2Dir, "Shared.txt"), "v2-updated");
        File.WriteAllText(Path.Combine(session2Dir, "SecondSession.txt"), "added in session 2");

        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        _recorder.InsertMedia(medium);

        // Session 1 (--no-close)
        var (s1Out, s1Err) = CreateStringWriters();
        int s1Code = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", "VDRV-01", "--input", session1Dir, "--label", "MS_S1", "--no-close", "--verify", "--json" },
            s1Out, s1Err, _deviceService, _burnEngine);
        Assert.Equal(0, s1Code);
        Assert.False(medium.IsFinalized);
        Assert.Single(medium.Sessions);

        using (var s1Stream = medium.CreateReadStream())
        using (var s1Reader = CdBurner.Core.Iso.IsoImageReader.FromStream(s1Stream, leaveOpen: true, sessionStartLba: 0))
        {
            Assert.False(s1Reader.HasUdf);
            Assert.True(s1Reader.HasJoliet);
        }

        // Session 2 (closes disc, automatically merges Session 1)
        int s2StartLba = medium.NextWritableLba;
        var (s2Out, s2Err) = CreateStringWriters();
        int s2Code = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", "VDRV-01", "--input", session2Dir, "--label", "MS_S2", "--verify", "--json" },
            s2Out, s2Err, _deviceService, _burnEngine);
        Assert.Equal(0, s2Code);
        Assert.True(medium.IsFinalized);
        Assert.Equal(2, medium.Sessions.Count);

        using (var finalStream = medium.CreateReadStream())
        using (var s2Reader = CdBurner.Core.Iso.IsoImageReader.FromStream(finalStream, leaveOpen: true, sessionStartLba: s2StartLba))
        {
            Assert.NotNull(s2Reader.FindFile("/Docs/FirstSession.txt"));
            Assert.NotNull(s2Reader.FindFile("/SecondSession.txt"));
            var shared = s2Reader.FindFile("/Shared.txt");
            Assert.NotNull(shared);
            Assert.Equal("v2-updated".Length, shared!.SizeBytes);
        }

        // Attempting to burn again to finalized CD-RW must require erase even with --overwrite
        var (failOut, failErr) = CreateStringWriters();
        int failCode = await CliCommandRunner.RunAsync(
            new[] { "burn-data", "--drive", "VDRV-01", "--input", session2Dir, "--overwrite" },
            failOut, failErr, _deviceService, _burnEngine);
        Assert.Equal(1, failCode);
        Assert.Contains("must be erased", failErr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArchiveStage_NoFill_And_Help_DescribeAndProduceAlignedRecovery()
    {
        var (helpOut, helpErr) = CreateStringWriters();
        int helpCode = await CliCommandRunner.RunAsync(new[] { "--help" }, helpOut, helpErr, _deviceService, _burnEngine);
        Assert.Equal(0, helpCode);
        string helpText = helpOut.ToString();
        Assert.Contains("[--redundancy <min %>]", helpText);
        Assert.Contains("[--no-fill]", helpText);
        Assert.Contains("fills free space unless --no-fill", helpText);

        string inputDir = Path.Combine(_tempDir, "archive_nofill_in");
        Directory.CreateDirectory(inputDir);
        var rng = new Random(2026_11_15);
        for (int i = 0; i < 6; i++)
        {
            byte[] data = new byte[32 * 1024];
            rng.NextBytes(data);
            await File.WriteAllBytesAsync(Path.Combine(inputDir, $"item_{i}.bin"), data);
        }

        string stagingDir = Path.Combine(_tempDir, "archive_nofill_out");
        bool hasPar2 = CdBurner.Core.Archival.Par2Runner.FindPar2Executable() != null;
        string[] stageArgs = hasPar2
            ? new[] { "archive-stage", "--input", inputDir, "--staging", stagingDir, "--media", "cd", "--redundancy", "10", "--no-fill", "--json" }
            : new[] { "archive-stage", "--input", inputDir, "--plan-only", "--media", "cd", "--redundancy", "10", "--no-fill", "--json" };

        var (stageOut, stageErr) = CreateStringWriters();
        int stageCode = await CliCommandRunner.RunAsync(
            stageArgs,
            stageOut,
            stageErr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, stageCode);
        using var doc = ParseJsonOutput(stageOut);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("fillFreeSpace").GetBoolean());

        if (hasPar2)
        {
            var disc = root.GetProperty("discs")[0];
            Assert.Equal(0, disc.GetProperty("recoveryBlockSize").GetInt32() % 2048);
            Assert.True(disc.GetProperty("recoveryBlocks").GetInt32() >= 1);
            string recoveryDir = Path.Combine(disc.GetProperty("path").GetString()!, CdBurner.Core.Archival.ArchiveManifest.RecoveryFolderName);
            Assert.Single(Directory.GetFiles(recoveryDir, "recovery.vol*.par2"));
        }
    }

    /// <summary>Stages a one-disc CD archive without recovery data (no par2 needed) and returns disc01's folder and set ID.</summary>
    private async Task<(string DiscFolder, string SetId)> StageOneArchiveDiscAsync(string name)
    {
        string inputDir = Path.Combine(_tempDir, name + "_in");
        Directory.CreateDirectory(inputDir);
        byte[] data = new byte[200_000];
        new Random(2026_11_13).NextBytes(data);
        await File.WriteAllBytesAsync(Path.Combine(inputDir, "photos.bin"), data);

        var (stageOut, stageErr) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(
            new[] { "archive-stage", "--input", inputDir, "--staging", Path.Combine(_tempDir, name + "_staged"), "--media", "cd", "--redundancy", "0", "--json" },
            stageOut, stageErr, _deviceService, _burnEngine);
        Assert.True(code == 0, stageErr.ToString());
        using var doc = ParseJsonOutput(stageOut);
        return (doc.RootElement.GetProperty("discs")[0].GetProperty("path").GetString()!, doc.RootElement.GetProperty("setId").GetString()!);
    }

    [Fact]
    public async Task ArchiveBurn_Native_WritesTheStagedDiscDirectly_AndReadsItBack()
    {
        var (discFolder, setId) = await StageOneArchiveDiscAsync("archive_native");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (outText, errText) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(
            new[] { "archive-burn", "--drive", "VDRV-01", "--disc", discFolder, "--native", "--no-verify", "--log-dir", Path.Combine(_tempDir, "archive-logs"), "--json" },
            outText, errText, _deviceService, _burnEngine);

        Assert.True(code == 0, errText.ToString());
        using var doc = ParseJsonOutput(outText);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("verified").GetBoolean()); // a direct burn is always read back, even with --no-verify
        var direct = root.GetProperty("directWrite");
        Assert.Equal("direct", direct.GetProperty("writer").GetString());
        Assert.True(File.Exists(direct.GetProperty("commandLog").GetString()));
        Assert.True(medium.IsFinalized);

        // The label the Archive screen uses: ARC + first six characters of the set ID + _01. Read it from the disc's
        // ISO 9660 Primary Volume Descriptor (ECMA-119 8.4: sector 16, type 1, "CD001", volume identifier at bytes 40-71).
        string expectedLabel = ("ARC" + setId[..6] + "_01").ToUpperInvariant();
        Assert.Equal(expectedLabel, root.GetProperty("volumeLabel").GetString());
        byte[] pvd = medium.ReadSector(16, 2048);
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", System.Text.Encoding.ASCII.GetString(pvd, 1, 5));
        Assert.Equal(expectedLabel, System.Text.Encoding.ASCII.GetString(pvd, 40, 32).TrimEnd(' '));
    }

    [Fact]
    public async Task ArchiveBurn_Native_HandsADualLayerDvdMinusRToTheWindowsWriter_AndSaysWhy()
    {
        var (discFolder, _) = await StageOneArchiveDiscAsync("archive_fallback");
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusRDualLayer, OpticalConstants.DvdPlusR9Sectors);
        _recorder.InsertMedia(medium);

        var (outText, errText) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(
            new[] { "archive-burn", "--drive", "VDRV-01", "--disc", discFolder, "--native", "--log-dir", Path.Combine(_tempDir, "fallback-logs") },
            outText, errText, _deviceService, _burnEngine);

        Assert.True(code == 0, errText.ToString());
        string text = outText.ToString();
        Assert.Contains("Writer:         Written with Windows' writer:", text);
        Assert.Contains("dual-layer DVD-R", text);
        Assert.Contains("Verified:       Passed", text);
        Assert.False(medium.IsBlank);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "fallback-logs"))); // no drive commands were sent
    }

    [Fact]
    public async Task ArchiveBurn_RefusesAFolderThatIsNotAStagedArchiveDisc()
    {
        string plain = CreateSampleDataFolder("NotAnArchive");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        var (outText, errText) = CreateStringWriters();
        int code = await CliCommandRunner.RunAsync(
            new[] { "archive-burn", "--drive", "VDRV-01", "--disc", plain, "--native" },
            outText, errText, _deviceService, _burnEngine);

        Assert.NotEqual(0, code);
        Assert.Contains("not a staged archive disc", errText.ToString());
        Assert.True(medium.IsBlank);
    }

    private sealed class OptionsCapturingEngine : IBurnEngineBackend
    {
        public DataBurnOptions? Options { get; private set; }

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            Options = options;
            return Task.FromResult(BurnOperationResult.Succeeded(16, TimeSpan.FromSeconds(1)));
        }

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    #endregion

    private sealed class TestDeviceService : IDiscDeviceService
    {
        private readonly IReadOnlyList<IDiscRecorderBackend> _recorders;

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public TestDeviceService(params IDiscRecorderBackend[] recorders)
        {
            _recorders = recorders;
        }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
        {
            return includeVirtual ? _recorders : _recorders.Where(r => !r.IsVirtual).ToList();
        }

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
        {
            return _recorders.FirstOrDefault(r =>
                string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.VolumePath?.TrimEnd('\\'), uniqueId.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
    }
}
