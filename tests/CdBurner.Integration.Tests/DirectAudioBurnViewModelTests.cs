using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The "Direct drive commands" switch on the Audio CD screen: off by default, burns and reads back when on, and
/// the standard writer is untouched when it is off.
/// </summary>
public sealed class DirectAudioBurnViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Tests_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _deviceManager = new(enablePhysicalEnumeration: false);
    private readonly VirtualBurnEngine _burnEngine = new();

    public DirectAudioBurnViewModelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        _deviceManager.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private AudioBurnViewModel CreateViewModel()
    {
        var vm = new AudioBurnViewModel(_deviceManager, _burnEngine)
        {
            AlbumTitle = "Direct Album",
            AlbumPerformer = "Direct Artist",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            DirectBurnLogDirectory = Path.Combine(_tempDir, "logs")
        };

        var files = new List<string>();
        for (int i = 1; i <= 2; i++)
        {
            byte[] pcm = new byte[300 * OpticalConstants.RawSectorSize];
            new Random(i).NextBytes(pcm);
            string wav = Path.Combine(_tempDir, $"Track{i:D2}.wav");
            WavFileCodec.WriteWavFile(wav, pcm);
            files.Add(wav);
        }
        vm.AddAudioFilesCommand.Execute(files.ToArray());
        Assert.Equal(2, vm.Tracks.Count);
        return vm;
    }

    private VirtualDiscMedium VirtualDisc() =>
        _deviceManager.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01").CurrentMedium!;

    [Fact]
    public void DirectDriveCommands_AreOffByDefault_AndTestFirstNeedsThem()
    {
        var vm = CreateViewModel();

        Assert.False(vm.UseDirectDriveCommands);
        Assert.False(vm.TestWriteFirst);

        vm.TestWriteFirst = true;
        Assert.False(vm.TestWriteFirst);

        vm.UseDirectDriveCommands = true;
        vm.TestWriteFirst = true;
        Assert.True(vm.TestWriteFirst);

        vm.UseDirectDriveCommands = false;
        Assert.False(vm.TestWriteFirst);
    }

    [Fact]
    public async Task WithDirectDriveCommands_TheDiscIsRecorded_ReadBack_AndCarriesCdText()
    {
        var vm = CreateViewModel();
        vm.UseDirectDriveCommands = true;

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(vm.LastDirectBurnResult);
        Assert.True(vm.LastDirectBurnResult.DiscRecorded);
        Assert.True(vm.LastDirectBurnResult.Verification!.Passed);
        Assert.True(File.Exists(vm.LastDirectBurnLogPath));
        Assert.Contains("direct drive commands", vm.StatusMessage);
        Assert.Contains("read back correctly", vm.StatusMessage);

        var medium = VirtualDisc();
        Assert.True(medium.IsFinalized);
        Assert.Equal("Direct Album", CdTextPackParser.ParsePacks(medium.CdTextBytes!).AlbumTitle);
        Assert.Equal(2, medium.Sessions.Single().Tracks.Count);
    }

    [Fact]
    public async Task WithTestWriteFirst_TheBurnStopsAfterTheTest_AndTheDiscStaysBlank()
    {
        var vm = CreateViewModel();
        vm.UseDirectDriveCommands = true;
        vm.TestWriteFirst = true;

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.False(result.Success);
        Assert.Contains("reloaded", result.ErrorMessage);
        Assert.True(VirtualDisc().IsBlank);
        Assert.DoesNotContain("Windows writer", vm.StatusMessage);
    }

    [Fact]
    public async Task WithoutDirectDriveCommands_TheStandardWriterIsUsed()
    {
        var vm = CreateViewModel();

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(vm.LastDirectBurnResult);
        Assert.Null(vm.LastDirectBurnLogPath);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "logs")));
        Assert.DoesNotContain("direct drive commands", vm.StatusMessage);
    }

    [Fact]
    public async Task DirectBurnOntoADiscThatIsNotBlank_Fails_AndLeavesTheDiscAsItWas()
    {
        var vm = CreateViewModel();
        var recorder = _deviceManager.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");
        var used = VirtualDiscMedium.CreateBlankCdR700();
        used.WriteSectors(0, new byte[16 * 2048], 2048); // an open data session: appendable, but not blank
        recorder.InsertMedia(used);
        vm.UseDirectDriveCommands = true;

        var result = await vm.StartAudioBurnAsync("VDRV-01");

        Assert.False(result.Success);
        Assert.True(vm.LastDirectBurnResult is null or { StoppedBeforeWriting: true });
        Assert.False(used.IsFinalized);
        Assert.DoesNotContain(used.Sessions.SelectMany(s => s.Tracks), t => t.IsAudio);
    }
}
