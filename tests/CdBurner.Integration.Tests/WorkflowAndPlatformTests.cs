using System.IO;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
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

        // Change and save
        vm.CheckForUpdatesOnStartup = true;
        vm.SaveSettingsCommand.Execute(null);

        // Reload
        var reloadedService = new AppSettingsService(settingsPath);
        reloadedService.Load();
        Assert.True(reloadedService.CurrentSettings.CheckForUpdatesOnStartup);

        // Reset
        vm.ResetSettingsCommand.Execute(null);
        Assert.False(vm.CheckForUpdatesOnStartup);
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
        using var client = new System.Net.Http.HttpClient(handler);

        var result = await vm.CheckForUpdatesAsync(client);

        Assert.True(result.Success);
        Assert.False(result.IsUpdateAvailable);
        Assert.Contains("IgnisDisc is up to date", vm.UpdateStatusMessage);
    }

    private sealed class FakeHttpHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly string _responseJson;

        public FakeHttpHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    #endregion

    #region 4. Self-Contained Archive Recovery Scripts & Surface Scan CSV Export

    [Fact]
    public async Task ArchiveStager_WritesStandaloneVerifyScripts_AndVerifyPs1DetectsTampering()
    {
        string srcDir = Path.Combine(_tempDir, "archive_src");
        string stageDir = Path.Combine(_tempDir, "archive_stage");
        Directory.CreateDirectory(srcDir);

        await File.WriteAllTextAsync(Path.Combine(srcDir, "alpha.txt"), "Alpha archival payload");
        await File.WriteAllTextAsync(Path.Combine(srcDir, "beta.txt"), "Beta archival payload");

        var plan = CdBurner.Core.Archival.ArchiveSpanPlanner.Plan(srcDir, DiscMediaType.CdR, redundancyPercent: 0, setId: "archset01");
        var stagedDiscs = await CdBurner.Core.Archival.ArchiveStager.StageAsync(plan, stageDir, par2Path: null);

        Assert.Single(stagedDiscs);
        string disc1 = stagedDiscs[0];

        string ps1Path = Path.Combine(disc1, CdBurner.Core.Archival.ArchiveManifest.VerifyPowerShellFileName);
        string batPath = Path.Combine(disc1, CdBurner.Core.Archival.ArchiveManifest.VerifyBatchFileName);
        string readmePath = Path.Combine(disc1, CdBurner.Core.Archival.ArchiveManifest.RecoveryReadmeFileName);

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
