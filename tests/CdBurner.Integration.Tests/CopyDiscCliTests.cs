using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// <c>copy-disc</c> on simulated drives: two drives or one (a blank is put in for the copy), audio and mixed-mode CDs,
/// the stop on source sectors that still read wrong, and the options it refuses.
/// </summary>
public sealed class CopyDiscCliTests : IDisposable
{
    private const int Raw = OpticalConstants.RawSectorSize;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-copycli-" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _source = new("VDRV-01", "IGNIS", "VIRTUAL A", @"V:\");
    private readonly VirtualDiscRecorder _target = new("VDRV-02", "IGNIS", "VIRTUAL B", @"W:\");

    public CopyDiscCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    /// <summary>Burns a mixed-mode CD (300 data sectors, then 320 frames of audio) onto the source drive.</summary>
    private async Task BurnMixedSource()
    {
        var bin = new List<byte>();
        var user = new byte[2048];
        var random = new Random(9);
        for (int lba = 0; lba < 450; lba++)
        {
            if (lba < 300) random.NextBytes(user); else Array.Clear(user);
            bin.AddRange(RawSectorCodec.Convert2048To2352Mode1(user, lba));
        }
        var audio = new byte[320 * Raw];
        random.NextBytes(audio);
        bin.AddRange(new byte[150 * Raw]);
        bin.AddRange(audio);
        File.WriteAllBytes(Path.Combine(_dir, "mixed.bin"), bin.ToArray());
        string cue = Path.Combine(_dir, "mixed.cue");
        File.WriteAllLines(cue, new[]
        {
            "FILE \"mixed.bin\" BINARY", "  TRACK 01 MODE1/2352", "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO", "    INDEX 00 00:06:00", "    INDEX 01 00:08:00"
        });

        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        compilation.ImportCueSheet(cue);
        _source.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        var burned = await new AudioCdBurnService(new NativeAudioBurnEngine(_dir) { Wait = _ => { } })
            .BurnAsync(_source, compilation, new AudioBurnOptions { UseDiscAtOnce = true });
        Assert.True(burned.Success, burned.ErrorMessage);
    }

    /// <summary>Burns a two-track audio CD (300 and 320 frames of noise) onto the source drive.</summary>
    private async Task BurnAudioSource()
    {
        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        for (int i = 0; i < 2; i++)
        {
            var pcm = new byte[(300 + 20 * i) * Raw];
            new Random(i + 1).NextBytes(pcm);
            string wav = Path.Combine(_dir, $"t{i}.wav");
            WavFileCodec.WriteWavFile(wav, pcm);
            compilation.AddTrack(new AudioCdTrackItem(i + 1, wav, $"Track {i + 1}", "Artist"));
        }
        _source.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        var burned = await new AudioCdBurnService(new VirtualBurnEngine(_source)).BurnAsync(_source, compilation, new AudioBurnOptions { UseDiscAtOnce = true });
        Assert.True(burned.Success, burned.ErrorMessage);
    }

    private async Task<(int Exit, JsonDocument? Json, string Out, string Err)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, new TwoDrives(_source, _target), new VirtualBurnEngine(_source));
        string text = stdout.ToString();
        int start = text.IndexOf('{');
        JsonDocument? json = args.Contains("--json") && start >= 0 ? JsonDocument.Parse(text[start..]) : null;
        return (exit, json, text, stderr.ToString());
    }

    [Fact]
    public async Task MixedCd_WithNative_IsCopiedWhole_AndVerified()
    {
        await BurnMixedSource();
        _target.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var (exit, json, outText, err) = await Run("copy-disc", "--source", "VDRV-01", "--target", "VDRV-02", "--native",
            "--log-dir", Path.Combine(_dir, "logs"), "--json");

        Assert.True(exit == 0, outText + err);
        var root = json!.RootElement;
        Assert.True(root.GetProperty("mixedMode").GetBoolean());
        Assert.True(root.GetProperty("copiedDataTrack").GetBoolean());
        Assert.True(root.GetProperty("verified").GetBoolean());
        Assert.Contains("the data track's 300 sectors", root.GetProperty("verifyMessage").GetString());
        var tracks = Assert.Single(_target.CurrentMedium!.Sessions).Tracks;
        Assert.Equal(new[] { false, true }, tracks.Select(t => t.IsAudio));
        Assert.Equal(600, tracks[1].StartLba);
    }

    [Fact]
    public async Task MixedCd_WithoutNative_CopiesTheAudio_AndSaysHowToCopyTheData()
    {
        await BurnMixedSource();
        _target.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var (exit, json, outText, err) = await Run("copy-disc", "--source", "VDRV-01", "--target", "VDRV-02", "--json");

        Assert.True(exit == 0, outText + err);
        Assert.False(json!.RootElement.GetProperty("copiedDataTrack").GetBoolean());
        Assert.Contains("Turn on direct drive commands", json.RootElement.GetProperty("warning").GetString());
        Assert.All(Assert.Single(_target.CurrentMedium!.Sessions).Tracks, t => Assert.True(t.IsAudio));
    }

    [Fact]
    public async Task OneDrive_PutsABlankInForTheCopy()
    {
        await BurnAudioSource();
        var sourceMedium = _source.CurrentMedium;

        var (exit, json, outText, err) = await Run("copy-disc", "--source", "VDRV-01", "--json");

        Assert.True(exit == 0, outText + err);
        Assert.Equal(1, json!.RootElement.GetProperty("copiesCompleted").GetInt32());
        Assert.NotSame(sourceMedium, _source.CurrentMedium);              // the copy went onto a new disc
        Assert.Equal(2, Assert.Single(_source.CurrentMedium!.Sessions).Tracks.Count);
    }

    [Fact]
    public async Task ASourceSectorThatKeepsReadingWrong_StopsTheCopy_UnlessAllowed()
    {
        await BurnMixedSource();
        _source.CurrentMedium!.SimulatedC2Errors[700] = 1000; // in the audio track
        _target.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var (exit, _, _, err) = await Run("copy-disc", "--source", "VDRV-01", "--target", "VDRV-02", "--native");

        Assert.Equal(1, exit);
        Assert.Contains("still read with errors after re-reading (track 2: 1)", err);
        Assert.True(_target.QueryMediaStatus().IsBlank);

        var (exit2, json2, out2, err2) = await Run("copy-disc", "--source", "VDRV-01", "--target", "VDRV-02", "--native", "--allow-suspect", "--json");

        Assert.True(exit2 == 0, out2 + err2);
        Assert.Contains("copied as read, with errors", json2!.RootElement.GetProperty("warning").GetString());
    }

    [Theory]
    [InlineData("--copies", "0", "--copies takes a number from 1 to 99")]
    [InlineData("--log-dir", "x", "--log-dir needs --native")]
    [InlineData("--wait-minutes", "0", "--wait-minutes takes a number from 1 to 1440")]
    public async Task BadOptions_AreRefused(string option, string value, string message)
    {
        await BurnAudioSource();

        var (exit, _, outText, err) = await Run("copy-disc", "--source", "VDRV-01", option, value);

        Assert.Equal(1, exit);
        Assert.Contains(message, outText + err);
    }

    private sealed class TwoDrives(params IDiscRecorderBackend[] recorders) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => recorders;

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }
}
