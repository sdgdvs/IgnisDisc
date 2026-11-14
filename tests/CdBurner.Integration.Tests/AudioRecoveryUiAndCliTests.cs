using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class AudioRecoveryUiAndCliTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly SingleRecorderDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public AudioRecoveryUiAndCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ignis_ui_cli_rec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _recorder = new VirtualDiscRecorder("VDRV-01", "Virtual Optical", "Drive 1.0", @"V:\");
        _deviceService = new SingleRecorderDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void AudioBurnViewModel_RecoverySession_IsOffByDefault_WithWarningAndCapacityText()
    {
        var vm = new AudioBurnViewModel(_deviceService, _burnEngine);

        Assert.False(vm.IncludeRecoverySession);
        Assert.Contains("car head units", vm.RecoverySessionWarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11,400", vm.RecoverySessionCapacityText, StringComparison.Ordinal);

        string wavPath = Path.Combine(_tempDir, "sample.wav");
        WavFileCodec.WriteWavFile(wavPath, new byte[150 * OpticalConstants.RawSectorSize]);
        vm.AddAudioFiles(new[] { wavPath });

        Assert.NotNull(vm.RecoveryEstimate);
        Assert.True(vm.RecoveryEstimate!.Fits);
        Assert.Contains("11,496", vm.RecoverySessionCapacityText, StringComparison.Ordinal);

        // Enabling recovery session automatically turns on direct drive commands; disabling direct commands clears it.
        vm.IncludeRecoverySession = true;
        Assert.True(vm.UseDirectDriveCommands);
        vm.UseDirectDriveCommands = false;
        Assert.False(vm.IncludeRecoverySession);
    }

    [Fact]
    [Trait("Category", "UI")]
    public void AudioBurnView_ContainsRecoverySessionCheckBoxAndWarningText()
    {
        Exception? staException = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var app = new CdBurner.App.App();
                    app.InitializeComponent();
                }
                else if (!System.Windows.Application.Current.Resources.Contains("AccentButtonStyle"))
                {
                    var appResources = (ResourceDictionary)System.Windows.Application.LoadComponent(
                        new Uri("/IgnisDisc;component/App.xaml", UriKind.Relative));
                    System.Windows.Application.Current.Resources.MergedDictionaries.Add(appResources);
                }

                var vm = new AudioBurnViewModel(_deviceService, _burnEngine);
                var view = new AudioBurnView { DataContext = vm };
                view.Measure(new Size(1280, 900));
                view.Arrange(new Rect(0, 0, 1280, 900));
                view.UpdateLayout();

                var checkBoxes = FindVisualChildren<CheckBox>(view).ToList();
                var burnRecoveryBox = checkBoxes.FirstOrDefault(c => (c.Content as string)?.Contains("Recovery session", StringComparison.OrdinalIgnoreCase) == true);
                Assert.NotNull(burnRecoveryBox);
                Assert.False(burnRecoveryBox!.IsChecked);

                var texts = FindVisualChildren<TextBlock>(view).Select(t => t.Text).ToList();
                Assert.Contains(texts, t => t.Contains("car head units", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(texts, t => t.Contains("11,400", StringComparison.Ordinal));

                // Switch to the Rip Audio CD tab so its TabItem visual tree is realized, then verify the repair checkbox.
                var tabs = FindVisualChildren<TabControl>(view).First();
                tabs.SelectedIndex = 1;
                view.Measure(new Size(1280, 900));
                view.Arrange(new Rect(0, 0, 1280, 900));
                view.UpdateLayout();

                var ripCheckBoxes = FindVisualChildren<CheckBox>(view).ToList();
                var ripRepairBox = ripCheckBoxes.FirstOrDefault(c => (c.Content as string)?.Contains("Repair from recovery session", StringComparison.OrdinalIgnoreCase) == true);
                Assert.NotNull(ripRepairBox);
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (staException != null)
        {
            throw new Xunit.Sdk.XunitException($"STA WPF verification failed: {staException}");
        }
    }

    [Fact]
    public async Task Cli_BurnAudioRecoverySession_And_RipAudioRepair_EndToEnd()
    {
        if (Par2Runner.FindPar2Executable() == null)
        {
            return;
        }

        // Red Book tracks must be at least 4 seconds (300 sectors) each for SAO burning.
        byte[] pcm1 = new byte[300 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[300 * OpticalConstants.RawSectorSize];
        new Random(41).NextBytes(pcm1);
        new Random(42).NextBytes(pcm2);

        string wav1 = Path.Combine(_tempDir, "t1.wav");
        string wav2 = Path.Combine(_tempDir, "t2.wav");
        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);

        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        // 1. Without --native, --recovery-session must stop before touching the disc.
        using (var swOut = new StringWriter())
        using (var swErr = new StringWriter())
        {
            int failExit = await CliCommandRunner.RunAsync(
                new[] { "burn-audio", "--drive", "VDRV-01", "--tracks", $"{wav1},{wav2}", "--recovery-session", "--json" },
                swOut, swErr, _deviceService, _burnEngine);
            Assert.Equal(1, failExit);
            Assert.True(medium.IsBlank);
        }

        // 2. Burn with --native --recovery-session.
        using (var swOut = new StringWriter())
        using (var swErr = new StringWriter())
        {
            int burnExit = await CliCommandRunner.RunAsync(
                new[] { "burn-audio", "--drive", "VDRV-01", "--tracks", $"{wav1},{wav2}", "--dao", "--native", "--recovery-session", "--log-dir", Path.Combine(_tempDir, "logs"), "--json" },
                swOut, swErr, _deviceService, _burnEngine);
            Assert.True(burnExit == 0, swOut.ToString() + swErr);

            using var burnDoc = JsonDocument.Parse(swOut.ToString());
            Assert.True(burnDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(burnDoc.RootElement.GetProperty("recoverySession").GetBoolean());
            Assert.True(burnDoc.RootElement.GetProperty("recoverySessionWritten").GetBoolean());
            Assert.Equal(2, medium.Sessions.Count);
        }

        // 3. Inject unreadable audio sectors into Track 2, then rip with --repair.
        medium.SimulatedBadLbas.Add(310);
        medium.SimulatedBadLbas.Add(311);
        string ripOutDir = Path.Combine(_tempDir, "cli_rip");

        using (var swOut = new StringWriter())
        using (var swErr = new StringWriter())
        {
            int ripExit = await CliCommandRunner.RunAsync(
                new[] { "rip-audio", "--drive", "VDRV-01", "--output", ripOutDir, "--repair", "--no-replaygain", "--json" },
                swOut, swErr, _deviceService, _burnEngine);
            Assert.True(ripExit == 0, swOut.ToString() + swErr);

            using var ripDoc = JsonDocument.Parse(swOut.ToString());
            var root = ripDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("complete").GetBoolean());
            var repairJson = root.GetProperty("recoveryRepair");
            Assert.True(repairJson.GetProperty("recoverySessionFound").GetBoolean());
            Assert.True(repairJson.GetProperty("allTracksVerified").GetBoolean());

            var files = root.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Equal(2, files.Count);
            Assert.Equal(pcm1, (await File.ReadAllBytesAsync(files[0])).AsSpan(44).ToArray());
            Assert.Equal(pcm2, (await File.ReadAllBytesAsync(files[1])).AsSpan(44).ToArray());
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var grandChild in FindVisualChildren<T>(child))
            {
                yield return grandChild;
            }
        }
    }

    private sealed class SingleRecorderDeviceService : IDiscDeviceService
    {
        private readonly IReadOnlyList<IDiscRecorderBackend> _recorders;

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public SingleRecorderDeviceService(params IDiscRecorderBackend[] recorders) => _recorders = recorders;

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) =>
            includeVirtual ? _recorders : _recorders.Where(r => !r.IsVirtual).ToList();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            _recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }
}
