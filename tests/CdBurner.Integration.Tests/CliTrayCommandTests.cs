using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>The eject and load commands on a virtual drive with a motorised tray and on one with a spring tray.</summary>
public sealed class CliTrayCommandTests
{
    private const string DriveId = "VDRV-TRAY";

    private sealed class OneDrive(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => [recorder];

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }

    private static VirtualDiscRecorder Drive(bool trayClosesItself, bool withDisc = true)
    {
        var recorder = new VirtualDiscRecorder(uniqueId: DriveId) { TrayClosesItself = trayClosesItself };
        if (withDisc)
        {
            recorder.InsertMedia(new VirtualDiscMedium(DiscMediaType.CdR, totalCapacitySectors: 512));
        }
        return recorder;
    }

    private static async Task<(int Exit, string Out, string Err)> Run(VirtualDiscRecorder recorder, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, new OneDrive(recorder), new VirtualBurnEngine(recorder));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task EjectThenLoad_OnADriveWithATray_OpensAndClosesItWithTheDiscBackIn()
    {
        using var recorder = Drive(trayClosesItself: true);
        var disc = recorder.CurrentMedium;

        var eject = await Run(recorder, "eject", "--drive", DriveId);
        Assert.Equal(0, eject.Exit);
        Assert.Contains("Tray opened.", eject.Out);
        Assert.True(recorder.TrayOpen);
        Assert.Null(recorder.CurrentMedium);

        var load = await Run(recorder, "load", "--drive", DriveId);
        Assert.Equal(0, load.Exit);
        Assert.Contains("Tray closed; the drive is ready.", load.Out);
        Assert.False(recorder.TrayOpen);
        Assert.Same(disc, recorder.CurrentMedium);
    }

    [Fact]
    public async Task Load_OnAnEmptyDriveWithATray_ClosesItAndSaysThereIsNoDisc()
    {
        using var recorder = Drive(trayClosesItself: true, withDisc: false);
        await Run(recorder, "eject", "--drive", DriveId);

        var load = await Run(recorder, "load", "--drive", DriveId, "--json");

        Assert.Equal(0, load.Exit);
        using var doc = JsonDocument.Parse(load.Out);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("discPresent").GetBoolean());
        Assert.False(recorder.TrayOpen);
    }

    [Fact]
    public async Task Eject_OnASpringTray_SaysTheTrayHasToBePushedInByHand()
    {
        using var recorder = Drive(trayClosesItself: false);

        var eject = await Run(recorder, "eject", "--drive", DriveId);

        Assert.Equal(0, eject.Exit);
        Assert.Contains("push it in by hand", eject.Out);
        Assert.True(recorder.TrayOpen);
    }

    [Fact]
    public async Task Load_OnASpringTray_SaysItCannotCloseItselfAndLeavesTheTrayOpen()
    {
        using var recorder = Drive(trayClosesItself: false);
        await Run(recorder, "eject", "--drive", DriveId);

        var load = await Run(recorder, "load", "--drive", DriveId);

        Assert.Equal(1, load.Exit);
        Assert.Contains("cannot close its own tray", load.Err);
        Assert.Contains("Push the tray in by hand", load.Err);
        Assert.True(recorder.TrayOpen);
        Assert.Null(recorder.CurrentMedium);
    }

    [Fact]
    public async Task Load_OnASpringTray_JsonSaysNothingWasSentAndTheTrayNeedsClosingByHand()
    {
        using var recorder = Drive(trayClosesItself: false);
        await Run(recorder, "eject", "--drive", DriveId);

        var load = await Run(recorder, "load", "--drive", DriveId, "--json");

        Assert.Equal(1, load.Exit);
        using var doc = JsonDocument.Parse(load.Out);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("load", root.GetProperty("action").GetString());
        Assert.Equal(DriveId, root.GetProperty("driveId").GetString());
        Assert.False(root.GetProperty("commandSent").GetBoolean());
        Assert.True(root.GetProperty("needsManualClose").GetBoolean());
        Assert.Equal(1, root.GetProperty("loadingMechanism").GetInt32());
        Assert.False(root.GetProperty("canLoad").GetBoolean());
        Assert.True(root.GetProperty("canEject").GetBoolean());
        Assert.Contains("Push the tray in by hand", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Eject_Json_ReportsTheLoadingMechanism()
    {
        using var recorder = Drive(trayClosesItself: true);

        var eject = await Run(recorder, "eject", "--drive", DriveId, "--json");

        Assert.Equal(0, eject.Exit);
        using var doc = JsonDocument.Parse(eject.Out);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("eject", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("loadingMechanism").GetInt32());
        Assert.True(doc.RootElement.GetProperty("canLoad").GetBoolean());
    }

    [Theory]
    [InlineData("eject")]
    [InlineData("load")]
    public async Task WithoutDrive_IsAUsageError(string command)
    {
        using var recorder = Drive(trayClosesItself: true);

        var result = await Run(recorder, command);

        Assert.Equal(1, result.Exit);
        Assert.Contains("--drive", result.Err);
    }

    [Fact]
    public async Task Help_ListsEjectAndLoad()
    {
        using var recorder = Drive(trayClosesItself: true);

        var help = await Run(recorder, "--help");
        var loadHelp = await Run(recorder, "load", "--help");

        Assert.Contains("eject --drive <id>", help.Out);
        Assert.Contains("load --drive <id>", help.Out);
        Assert.Contains("Removable Medium feature", loadHelp.Out);
    }
}
