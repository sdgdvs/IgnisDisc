using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Integration unit tests for Phase 19: Headless CLI Application (cdburner-cli.exe).
/// Exercises in-process CliCommandRunner.RunAsync against pre-configured VirtualDiscRecorder
/// for drives, build-iso, burn-iso, burn-data, burn-audio, burn-dvd, rip, verify, scan, erase,
/// and validates exit codes, argument handling, human output, and structured JSON telemetry.
/// </summary>
public sealed class Phase19_CliIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public Phase19_CliIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Phase19_" + Guid.NewGuid().ToString("N"));
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

        File.WriteAllText(Path.Combine(dir, "README.TXT"), "CdBurner CLI Phase 19 sample data file.");
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
        Assert.Contains("Usage:", hOut.ToString());

        var (vOut, vErr) = CreateStringWriters();
        int vCode = await CliCommandRunner.RunAsync(new[] { "--version" }, vOut, vErr, _deviceService, _burnEngine);
        Assert.Equal(0, vCode);
        Assert.Contains("CdBurner CLI", vOut.ToString());
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
            new[] { "burn-data", "--drive", recorder.UniqueId, "--input", input, "--overwrite" }, out2, err2, devices, engine);

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
