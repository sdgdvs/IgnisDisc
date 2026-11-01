using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Core.Audio;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Integration tests verifying the audio rip options, read offset clamping,
/// secure extraction C2 reporting, and rip report generation in <see cref="AudioBurnViewModel"/>.
/// </summary>
public sealed class AudioRipOptionsViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public AudioRipOptionsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_RipOptionsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = new VirtualBurnEngine();
    }

    public void Dispose()
    {
        _deviceManager.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private async Task<AudioBurnViewModel> CreateAndBurnVirtualAudioCdAsync()
    {
        string wav1 = Path.Combine(_tempDir, "Track01.wav");
        string wav2 = Path.Combine(_tempDir, "Track02.wav");

        byte[] pcm1 = new byte[75 * OpticalConstants.RawSectorSize];
        byte[] pcm2 = new byte[150 * OpticalConstants.RawSectorSize];

        for (int i = 0; i < pcm1.Length; i++) pcm1[i] = (byte)(i % 256);
        for (int i = 0; i < pcm2.Length; i++) pcm2[i] = (byte)((i * 3) % 256);

        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine)
        {
            AlbumTitle = "Rip Test Album",
            AlbumPerformer = "Test Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            NormalizeAllTracks = false
        };

        vm.AddAudioFilesCommand.Execute(new[] { wav1, wav2 });
        var burnResult = await vm.StartAudioBurnAsync("VDRV-01");
        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");

        return vm;
    }

    [Fact]
    public async Task Rip_PassesOptionsToRipper()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        string ripOutput = Path.Combine(_tempDir, "Rip_Out_1");

        vm.ReadOffsetSamples = 6;
        vm.SecureRip = true;
        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;

        var ripResult = await vm.RipAudioCdAsync("VDRV-01", ripOutput);

        Assert.NotNull(vm.LastRipResult);
        Assert.True(vm.LastRipResult.Success, $"Rip failed: {vm.LastRipResult.ErrorMessage}");
        Assert.True(vm.LastRipResult.SecureModeUsed);
        Assert.Contains("+6", vm.RipReport);
    }

    [Fact]
    public async Task Rip_WithoutNetworkOptions_ReportsDiscIds()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        string ripOutput = Path.Combine(_tempDir, "Rip_Out_2");

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;

        var ripResult = await vm.RipAudioCdAsync("VDRV-01", ripOutput);

        Assert.NotNull(vm.LastRipResult);
        Assert.True(vm.LastRipResult.Success, $"Rip failed: {vm.LastRipResult.ErrorMessage}");
        Assert.Contains("MusicBrainz ID", vm.RipReport);
    }

    [Fact]
    public void ReadOffsetSamples_IsClamped()
    {
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine);

        vm.ReadOffsetSamples = 50000;
        Assert.Equal(10000, vm.ReadOffsetSamples);

        vm.ReadOffsetSamples = -50000;
        Assert.Equal(-10000, vm.ReadOffsetSamples);
    }

    [Fact]
    public async Task RipReport_IsClearedAtStartOfRip()
    {
        var vm = await CreateAndBurnVirtualAudioCdAsync();
        string ripOutput = Path.Combine(_tempDir, "Rip_Out_4");

        vm.LookupMetadata = false;
        vm.VerifyAccurateRip = false;
        // A sentinel that cannot occur in a real report ("old" matched "Output Folder").
        const string staleReport = "STALE-REPORT-SENTINEL";
        vm.RipReport = staleReport;

        var ripResult = await vm.RipAudioCdAsync("VDRV-01", ripOutput);

        Assert.NotNull(vm.LastRipResult);
        Assert.True(vm.LastRipResult.Success);
        Assert.DoesNotContain(staleReport, vm.RipReport);
    }

    [Fact]
    public void AppSettings_InitializedAndSavedWhenPropertiesChange()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.CurrentSettings = new AppSettings
        {
            AudioReadOffsetSamples = 12,
            SecureAudioRipByDefault = true,
            LookupMusicBrainzByDefault = false,
            VerifyAccurateRipByDefault = false
        };

        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine, settingsService: settingsService);
        Assert.Equal(12, vm.ReadOffsetSamples);
        Assert.True(vm.SecureRip);
        Assert.False(vm.LookupMetadata);
        Assert.False(vm.VerifyAccurateRip);

        vm.ReadOffsetSamples = 30;
        Assert.Equal(30, settingsService.CurrentSettings.AudioReadOffsetSamples);
    }
}
