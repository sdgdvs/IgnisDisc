using System.IO;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// A damaged FLAC stops an audio burn before the drive is touched, names the file and what is wrong, and burns only
/// when the user accepts the damage (owner decision 2026-11-11: stop, allow override).
/// </summary>
public sealed class DamagedFlacBurnTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ignis-damaged-flac-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A 5-second noise-like FLAC (54 frames of 4,096 samples), with one frame corrupted when asked.</summary>
    private string Flac(string name, bool damaged)
    {
        var pcm = new byte[54 * 4096 * 4];
        new Random(name.Length).NextBytes(pcm);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);
        if (damaged)
        {
            flac[flac.Length / 2] ^= 0x40;
        }
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, flac);
        return path;
    }

    private static AudioCdCompilation Album(params string[] files)
    {
        var compilation = new AudioCdCompilation { AlbumTitle = "Album", AlbumPerformer = "Artist" };
        for (int i = 0; i < files.Length; i++)
        {
            compilation.AddTrack(new AudioCdTrackItem(
                TrackNumber: i + 1, SourceFilePath: files[i], Title: $"Track {i + 1}", Performer: "Artist",
                Isrc: null, PreGapFrames: i == 0 ? 150 : 0, NormalizeAudio: false));
        }
        return compilation;
    }

    [Fact]
    public async Task DamagedFlac_StopsTheBurn_BeforeTheDiscIsTouched()
    {
        string good = Flac("good.flac", damaged: false);
        string bad = Flac("broken track.flac", damaged: true);
        using var recorder = new VirtualDiscRecorder("VDRV-FLAC");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var result = await new AudioCdBurnService(new VirtualBurnEngine(recorder)).BurnAsync(recorder, Album(good, bad), new AudioBurnOptions());

        Assert.False(result.Success);
        Assert.Equal([bad], result.DamagedAudioFiles);
        Assert.Contains("'broken track.flac'", result.ErrorMessage);
        Assert.Contains("replaced by silence", result.ErrorMessage);
        Assert.Contains("Nothing was written to the disc", result.ErrorMessage);
        Assert.True(medium.IsBlank);
    }

    [Fact]
    public async Task AcceptingTheDamage_BurnsTheDisc()
    {
        string bad = Flac("broken.flac", damaged: true);
        using var recorder = new VirtualDiscRecorder("VDRV-FLAC2");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var result = await new AudioCdBurnService(new VirtualBurnEngine(recorder))
            .BurnAsync(recorder, Album(bad), new AudioBurnOptions { AllowDamagedAudio = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(medium.IsBlank);
    }

    [Fact]
    public async Task IntactFlacs_BurnWithoutAnyQuestion()
    {
        using var recorder = new VirtualDiscRecorder("VDRV-FLAC3");
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var result = await new AudioCdBurnService(new VirtualBurnEngine(recorder))
            .BurnAsync(recorder, Album(Flac("a.flac", false), Flac("bb.flac", false)), new AudioBurnOptions());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.DamagedAudioFiles);
    }
}
