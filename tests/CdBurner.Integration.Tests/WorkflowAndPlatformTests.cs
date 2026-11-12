using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Burning;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class WorkflowAndPlatformTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public WorkflowAndPlatformTests()
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
        }
    }

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
            return _recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
        }
    }

    #region CLI drive-report Command Tests

    [Fact]
    public async Task Cli_DriveReport_HumanReadable_ReturnsSuccessAndOutputsMarkdown()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "drive-report", "--drive", "VDRV-01" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        string output = stdout.ToString();
        Assert.Contains("# IgnisDisc Drive Compatibility Report", output);
        Assert.DoesNotContain("`VDRV-01`", output);
        Assert.Contains("IGNIS", output);
        Assert.Contains("VIRTUAL DVD/CD-RW", output);
        Assert.Contains("ignisdisc-cli drives --json", output);
        Assert.Contains("ignisdisc-cli media-info --json", output);
    }

    [Fact]
    public async Task Cli_DriveReport_JsonFlag_ReturnsSuccessAndStructuredReport()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "drive-report", "--drive", "VDRV-01", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        string jsonOutput = stdout.ToString();

        using var doc = JsonDocument.Parse(jsonOutput);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        string report = root.GetProperty("report").GetString()!;
        Assert.NotNull(report);
        Assert.Contains("# IgnisDisc Drive Compatibility Report", report);
    }

    #endregion

    #region DriveUtilitiesViewModel Tests

    [Fact]
    public void DriveUtilitiesViewModel_GenerateCompatibilityReportCommand_GeneratesAndStoresReport()
    {
        var vm = new DriveUtilitiesViewModel(_deviceService, _burnEngine);
        Assert.NotNull(vm.SelectedRecorder);

        vm.GenerateCompatibilityReportCommand.Execute(null);

        Assert.NotNull(vm.LastCompatibilityReport);
        Assert.Contains("# IgnisDisc Drive Compatibility Report", vm.LastCompatibilityReport);
        Assert.DoesNotContain(vm.SelectedRecorder.UniqueId, vm.LastCompatibilityReport);
        Assert.Equal("Drive compatibility report generated and ready to copy.", vm.StatusMessage);
    }

    #endregion

    #region ImageStudioViewModel Burn Queue Tests

    [Fact]
    public async Task ImageStudioViewModel_BurnQueue_AddsRemovesClearsAndExecutesQueue()
    {
        var vm = new ImageStudioViewModel(_deviceService, _burnEngine);
        string testIso1 = Path.Combine(_tempDir, "queue1.iso");
        string testIso2 = Path.Combine(_tempDir, "queue2.iso");
        await File.WriteAllBytesAsync(testIso1, new byte[2048 * 2]);
        await File.WriteAllBytesAsync(testIso2, new byte[2048 * 2]);

        // 1. Add items
        vm.AddToBurnQueueCommand.Execute(testIso1);
        vm.AddToBurnQueueCommand.Execute(testIso2);
        Assert.Equal(2, vm.BurnQueueItems.Count);

        // 2. Remove last item
        vm.RemoveFromBurnQueueCommand.Execute(null);
        Assert.Single(vm.BurnQueueItems);
        Assert.Equal(testIso1, vm.BurnQueueItems[0].ImagePath);

        // 3. Clear items
        vm.ClearBurnQueueCommand.Execute(null);
        Assert.Empty(vm.BurnQueueItems);

        // 4. Re-add and execute queue on virtual drive
        vm.AddToBurnQueueCommand.Execute(new BurnQueueItem(testIso1, VerifyAfterBurn: false));
        Assert.Single(vm.BurnQueueItems);

        var queueResult = await vm.RunBurnQueueAsync("VDRV-01");

        Assert.True(queueResult.Success);
        Assert.Equal(1, queueResult.CompletedItems);
        Assert.Equal(BurnQueueItemStatus.Completed, vm.BurnQueueItems[0].Status);
        Assert.Contains("Burn queue completed successfully", vm.StatusMessage);
    }

    #endregion

    #region SettingsViewModel Tests

    [Fact]
    public void SettingsViewModel_CheckForUpdatesOnStartup_PersistsAndResetsCorrectly()
    {
        string settingsPath = Path.Combine(_tempDir, "custom_settings.json");
        var service = new AppSettingsService(settingsPath);
        var vm = new SettingsViewModel(service);

        // Default should be false (opt-in)
        Assert.False(vm.CheckForUpdatesOnStartup);
        Assert.True(vm.LookupMusicBrainzByDefault);
        Assert.True(vm.VerifyAccurateRipByDefault);

        // Change and save
        vm.CheckForUpdatesOnStartup = true;
        vm.LookupMusicBrainzByDefault = false;
        vm.VerifyAccurateRipByDefault = false;
        vm.SaveSettingsCommand.Execute(null);

        // Reload
        var reloadedService = new AppSettingsService(settingsPath);
        reloadedService.Load();
        Assert.True(reloadedService.CurrentSettings.CheckForUpdatesOnStartup);
        Assert.False(reloadedService.CurrentSettings.LookupMusicBrainzByDefault);
        Assert.False(reloadedService.CurrentSettings.VerifyAccurateRipByDefault);

        // Reset
        vm.ResetSettingsCommand.Execute(null);
        Assert.False(vm.CheckForUpdatesOnStartup);
        Assert.True(vm.LookupMusicBrainzByDefault);
        Assert.True(vm.VerifyAccurateRipByDefault);
    }

    [Fact]
    public async Task SettingsViewModel_CheckForUpdatesCommand_ExecutesAndSetsStatusMessage()
    {
        string settingsPath = Path.Combine(_tempDir, "settings_update.json");
        var service = new AppSettingsService(settingsPath);
        var vm = new SettingsViewModel(service);

        // Execute update check against fake endpoint or test handler
        string fakeJson = """{ "tag_name": "v1.10", "html_url": "https://github.com/tether-works/IgnisDisc/releases/tag/v1.10" }""";
        var handler = new FakeHttpHandler(fakeJson);
        using var client = new HttpClient(handler);

        var result = await vm.CheckForUpdatesAsync(client);

        Assert.True(result.Success);
        Assert.False(result.IsUpdateAvailable);
        Assert.Contains("IgnisDisc is up to date", vm.UpdateStatusMessage);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public FakeHttpHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeBinaryHttpHandler : HttpMessageHandler
    {
        private readonly byte[] _zipBytes;

        public FakeBinaryHttpHandler(byte[] zipBytes)
        {
            _zipBytes = zipBytes;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_zipBytes)
            };
            resp.Content.Headers.ContentLength = _zipBytes.Length;
            return Task.FromResult(resp);
        }
    }

    private static byte[] CreateSyntheticToolZip(string entryPath, string licenseEntryPath, string licenseContent)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var exeEntry = archive.CreateEntry(entryPath);
            using (var s = exeEntry.Open())
            {
                byte[] stub = "MZ-SYNTHETIC-BINARY"u8.ToArray();
                s.Write(stub, 0, stub.Length);
            }

            var licEntry = archive.CreateEntry(licenseEntryPath);
            using (var ls = new StreamWriter(licEntry.Open(), Encoding.UTF8))
            {
                ls.Write(licenseContent);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task DvdVideoViewModel_InstallFfmpegAsync_ExtractsLgplBinaryAndLicenseAndActivatesLocator()
    {
        byte[] zipBytes = CreateSyntheticToolZip(
            "ffmpeg-master-latest-win64-lgpl/bin/ffmpeg.exe",
            "ffmpeg-master-latest-win64-lgpl/LICENSE.txt",
            "GNU LESSER GENERAL PUBLIC LICENSE Version 2.1");

        using var client = new HttpClient(new FakeBinaryHttpHandler(zipBytes));
        string targetDir = Path.Combine(_tempDir, "ffmpeg_install");

        var vm = new DvdVideoViewModel(_deviceService, _burnEngine);
        var result = await vm.InstallFfmpegAsync(targetDir, client, "https://example.test/ffmpeg-lgpl.zip");

        Assert.True(result.Success);
        Assert.NotNull(result.ExecutablePath);
        Assert.True(File.Exists(result.ExecutablePath));
        Assert.NotNull(result.LicensePath);
        Assert.True(File.Exists(result.LicensePath));
        Assert.Equal("ffmpeg-LICENSE.txt", Path.GetFileName(result.LicensePath));
        Assert.True(vm.IsFfmpegAvailable);
        Assert.False(vm.IsFfmpegMissing);
        Assert.Equal(result.ExecutablePath, FfmpegLocator.FindFfmpegExecutable(vm.CustomFfmpegPath));
    }

    [Fact]
    public async Task ArchiveViewModel_InstallPar2Async_ExtractsPar2AndUnlocksRedundancySlider()
    {
        byte[] zipBytes = CreateSyntheticToolZip(
            "par2.exe",
            "COPYING",
            "GNU GENERAL PUBLIC LICENSE Version 2");

        using var client = new HttpClient(new FakeBinaryHttpHandler(zipBytes));
        string targetDir = Path.Combine(_tempDir, "par2_install");

        var vm = new ArchiveViewModel(_deviceService, _burnEngine);
        var result = await vm.InstallPar2Async(targetDir, client, "https://example.test/par2.zip");

        Assert.True(result.Success);
        Assert.NotNull(result.ExecutablePath);
        Assert.True(File.Exists(result.ExecutablePath));
        Assert.True(vm.Par2Available);
        Assert.False(vm.IsPar2Missing);
        Assert.True(vm.RedundancyPercent > 0);
        Assert.Contains("par2 found:", vm.Par2StatusText);
        Assert.Equal(result.ExecutablePath, Par2Runner.FindPar2Executable(result.ExecutablePath));
    }

    [Fact]
    public async Task AudioBurnViewModel_LookupDiscMetadataAsync_PopulatesAlbumAndTrackMetadataFromMusicBrainz()
    {
        var medium = VirtualDiscMedium.CreateBlankCdR700();
        medium.Raw2352Mode = true;
        medium.IsBlank = false;
        medium.UsedSectors = 27000;
        var session = new VirtualSession
        {
            SessionNumber = 1,
            StartLba = 0,
            SectorCount = 27000
        };
        session.Tracks.Add(new VirtualTrack { TrackNumber = 1, StartLba = 0, SectorCount = 15000, IsAudio = true });
        session.Tracks.Add(new VirtualTrack { TrackNumber = 2, StartLba = 15000, SectorCount = 12000, IsAudio = true });
        medium.Sessions.Add(session);
        _recorder.InsertMedia(medium);

        string mbJson = """
        {
            "releases": [
                {
                    "id": "rel-1",
                    "title": "Test Symphony",
                    "artist-credit": [ { "name": "Test Orchestra" } ],
                    "media": [
                        {
                            "format": "CD",
                            "discs": [ { "id": "any" } ],
                            "tracks": [
                                { "number": "1", "title": "Movement I", "artist-credit": [ { "name": "Test Orchestra" } ] },
                                { "number": "2", "title": "Movement II", "artist-credit": [ { "name": "Test Orchestra" } ] }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        using var client = new HttpClient(new FakeHttpHandler(mbJson));
        var vm = new AudioBurnViewModel(_deviceService, _burnEngine);

        var meta = await vm.LookupDiscMetadataAsync(client);

        Assert.NotNull(meta);
        Assert.Equal("Test Symphony", vm.AlbumTitle);
        Assert.Equal("Test Orchestra", vm.AlbumPerformer);
        Assert.Contains("Test Symphony", vm.RipReport);
        Assert.Contains("Movement I", vm.RipReport);
        Assert.Contains("Movement II", vm.RipReport);
    }

    #endregion

    #region 4. Self-Contained Archive Recovery Scripts & Surface Scan CSV Export

    [Fact]
    // Running VERIFY.PS1 against damaged files is covered by ArchiveDamageTests (CdBurner.Core.Tests).
    public async Task ArchiveStager_WritesTheVerifyScriptsAndTheRecoveryReadme()
    {
        string srcDir = Path.Combine(_tempDir, "archive_src");
        string stageDir = Path.Combine(_tempDir, "archive_stage");
        Directory.CreateDirectory(srcDir);

        await File.WriteAllTextAsync(Path.Combine(srcDir, "alpha.txt"), "Alpha archival payload");
        await File.WriteAllTextAsync(Path.Combine(srcDir, "beta.txt"), "Beta archival payload");

        var plan = ArchiveSpanPlanner.Plan(srcDir, DiscMediaType.CdR, redundancyPercent: 0, setId: "archset01");
        var stagedDiscs = await ArchiveStager.StageAsync(plan, stageDir, par2Path: null);

        Assert.Single(stagedDiscs);
        string disc1 = stagedDiscs[0];

        string ps1Path = Path.Combine(disc1, ArchiveManifest.VerifyPowerShellFileName);
        string batPath = Path.Combine(disc1, ArchiveManifest.VerifyBatchFileName);
        string readmePath = Path.Combine(disc1, ArchiveManifest.RecoveryReadmeFileName);

        Assert.True(File.Exists(ps1Path));
        Assert.True(File.Exists(batPath));
        Assert.True(File.Exists(readmePath));

        string readmeText = await File.ReadAllTextAsync(readmePath);
        Assert.Contains("archset01", readmeText);
        Assert.Contains("VERIFY.BAT", readmeText);
        Assert.Contains("sha256sum -c IGNISDISC-MANIFEST.sha256", readmeText);
    }

    [Fact]
    public async Task Cli_Scan_WithCsvFlag_WritesValidTransferRateCsv()
    {
        var medium = VirtualDiscMedium.CreateBlankCdR700();
        medium.WriteSectors(0, new byte[256 * 2048], 2048);
        _recorder.InsertMedia(medium);

        string csvPath = Path.Combine(_tempDir, "scan_output.csv");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "scan", "--drive", "VDRV-01", "--block-size", "256", "--csv", csvPath, "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(csvPath));

        string[] lines = await File.ReadAllLinesAsync(csvPath);
        Assert.True(lines.Length >= 2);
        Assert.Equal("BlockIndex,StartLba,EndLba,SectorCount,Status,ReadSpeedKiBPerSec,ReadSpeedMultiplierX,ElapsedMs", lines[0]);

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(csvPath, doc.RootElement.GetProperty("csvPath").GetString());
    }

    [Fact]
    public async Task Cli_MediaInfo_Json_IncludesMediaManufacturerIdAndMmcFeatures()
    {
        _recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "media-info", "--drive", "VDRV-01", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("VIRTUAL-OPTICAL-01", doc.RootElement.GetProperty("mediaManufacturerId").GetString());
        Assert.True(doc.RootElement.GetProperty("mmcFeatures").GetArrayLength() > 0);
    }

    #endregion
}
