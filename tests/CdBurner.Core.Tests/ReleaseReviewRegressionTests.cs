using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Regression tests for problems found reviewing the v1.11 work: ReplayGain loudness, FLAC tagging limits,
/// update-check links, after-burn actions, and BIN/CUE conversion guards.
/// </summary>
public sealed class ReleaseReviewRegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_ReviewTests_" + Guid.NewGuid().ToString("N"));

    public ReleaseReviewRegressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] StereoSine(double dbfs, double seconds, double frequency = 1000.0)
    {
        int frames = (int)(seconds * 44_100);
        double amplitude = Math.Pow(10.0, dbfs / 20.0) * 32767.0;
        byte[] pcm = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            short s = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * i / 44_100.0));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), s);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), s);
        }

        return pcm;
    }

    // EBU Tech 3341 test cases 1 and 2: a 1 kHz stereo sine at -23 / -33 dBFS measures -23 / -33 LUFS (±0.1 LU).
    [Theory]
    [InlineData(-23.0)]
    [InlineData(-33.0)]
    public void ReplayGain_StereoSine_MeasuresBs1770Loudness(double dbfs)
    {
        var result = ReplayGainCalculator.CalculateTrackReplayGain(StereoSine(dbfs, 20));

        Assert.InRange(result.IntegratedLoudnessLufs, dbfs - 0.1, dbfs + 0.1);
        Assert.InRange(result.GainDb, -18.0 - dbfs - 0.1, -18.0 - dbfs + 0.1);
    }

    // EBU Tech 3341 test case 3: -36 / -23 / -36 dBFS for 10 / 60 / 10 s measures -23 LUFS: the relative gate
    // drops the quiet blocks. An ungated or percentile-based meter reads lower.
    [Fact]
    public void ReplayGain_RelativeGate_IgnoresQuietPassages()
    {
        byte[] pcm = [.. StereoSine(-36, 10), .. StereoSine(-23, 60), .. StereoSine(-36, 10)];

        var result = ReplayGainCalculator.CalculateTrackReplayGain(pcm);

        Assert.InRange(result.IntegratedLoudnessLufs, -23.1, -22.9);
    }

    [Fact]
    public void ReplayGain_StreamAndSpanAgree_AndAlbumGatesAllBlocksTogether()
    {
        byte[] loud = StereoSine(-23, 20);
        byte[] quiet = StereoSine(-33, 20);

        var a = ReplayGainCalculator.CalculateTrackReplayGain(loud);
        var b = ReplayGainCalculator.CalculateTrackReplayGain(new MemoryStream(quiet));
        Assert.Equal(ReplayGainCalculator.CalculateTrackReplayGain(quiet).GainDb, b.GainDb, precision: 9);

        // Equal durations: both pass the relative gate, so the album is the mean power of all blocks:
        // 10*log10((1 + 0.1) / 2) = -2.60 LU below the loud track.
        var album = ReplayGainCalculator.CalculateAlbumReplayGain(new[] { a, b });
        Assert.InRange(album.AlbumGainDb, 5.0 + 2.60 - 0.15, 5.0 + 2.60 + 0.15);
        Assert.Equal(Math.Max(a.PeakAmplitude, b.PeakAmplitude), album.AlbumPeakAmplitude);
    }

    [Fact]
    public void ReplayGain_DigitalSilence_GivesZeroGainAndPeak()
    {
        var result = ReplayGainCalculator.CalculateTrackReplayGain(new byte[44_100 * 4 * 2]);
        Assert.Equal(0.0, result.GainDb);
        Assert.Equal(0.0, result.PeakAmplitude);
    }

    [Fact]
    public void FlacCoverArt_DetectsImageTypeFromContent_NotFileName()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0 };
        Assert.Equal("image/png", FlacEncoder.DetectImageMimeType(png));
        Assert.Equal("image/jpeg", FlacEncoder.DetectImageMimeType(jpeg));
        Assert.Null(FlacEncoder.DetectImageMimeType("not an image"u8));
    }

    [Fact]
    public void FlacCoverArt_LargerThanAMetadataBlock_IsRefusedInsteadOfCorruptingTheFile()
    {
        string flacPath = Path.Combine(_dir, "track.flac");
        FlacFileCodec.WriteFlacFile(flacPath, new byte[OpticalConstants.RawSectorSize * 10]);
        byte[] original = File.ReadAllBytes(flacPath);

        byte[] hugeCover = new byte[FlacEncoder.MaxMetadataBlockLength + 1];
        hugeCover[0] = 0xFF; hugeCover[1] = 0xD8; hugeCover[2] = 0xFF;
        var tags = new AudioTrackTags("T", "A", "Al", 1, 1, CoverArtBytes: hugeCover);

        Assert.Throws<InvalidDataException>(() => FlacEncoder.EmbedCoverArtAndReplayGain(flacPath, tags));
        Assert.Equal(original, File.ReadAllBytes(flacPath));
        Assert.Single(Directory.GetFiles(_dir)); // no temp file left behind
    }

    [Fact]
    public void FlacPictureBlock_WithHugeLengthFields_IsIgnoredNotThrown()
    {
        // A crafted PICTURE block whose MIME length is near int.MaxValue must not crash metadata reading.
        string flacPath = Path.Combine(_dir, "crafted.flac");
        FlacFileCodec.WriteFlacFile(flacPath, new byte[OpticalConstants.RawSectorSize * 4]);
        byte[] picture = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(picture.AsSpan(0, 4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(picture.AsSpan(4, 4), 0x7FFF_FFFC);
        byte[] withPicture = InsertMetadataBlock(File.ReadAllBytes(flacPath), blockType: 6, picture);
        File.WriteAllBytes(flacPath, withPicture);

        var meta = FlacFileCodec.ProbeAudioFile(flacPath);
        Assert.NotNull(meta);
        Assert.False(meta!.HasEmbeddedCoverArt);
    }

    private static byte[] InsertMetadataBlock(byte[] flac, int blockType, byte[] body)
    {
        // Clear the "last block" flag on STREAMINFO and append the new block as the last one before the frames.
        int pos = 4;
        var blocks = new List<byte[]>();
        while (true)
        {
            bool last = (flac[pos] & 0x80) != 0;
            int len = (flac[pos + 1] << 16) | (flac[pos + 2] << 8) | flac[pos + 3];
            byte[] block = flac.AsSpan(pos, 4 + len).ToArray();
            block[0] &= 0x7F;
            blocks.Add(block);
            pos += 4 + len;
            if (last) break;
        }

        using var ms = new MemoryStream();
        ms.Write(flac, 0, 4);
        foreach (var block in blocks) ms.Write(block);
        ms.WriteByte((byte)(0x80 | blockType));
        ms.WriteByte((byte)(body.Length >> 16));
        ms.WriteByte((byte)(body.Length >> 8));
        ms.WriteByte((byte)body.Length);
        ms.Write(body);
        ms.Write(flac, pos, flac.Length - pos);
        return ms.ToArray();
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    [Theory]
    [InlineData("https://evil.example/IgnisDisc-Setup.exe")]
    [InlineData("http://github.com/tether-works/IgnisDisc/releases/tag/v9.0")]
    [InlineData("https://github.com/someone-else/IgnisDisc/releases/tag/v9.0")]
    public async Task UpdateCheck_NeverSurfacesALinkOutsideThisProjectsReleases(string htmlUrl)
    {
        using var client = new HttpClient(new StubHandler($$"""{"tag_name":"v9.0","html_url":"{{htmlUrl}}"}"""));

        var result = await GitHubUpdateChecker.CheckForUpdateAsync("1.10", client);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(GitHubUpdateChecker.ReleasesPageUrl, result.ReleaseUrl);
    }

    [Fact]
    public void ShutdownAfterBurn_UsesWindowsCountdownThatCanBeCancelled()
    {
        var psi = PostBurnActionExecutor.CreateShutdownStartInfo();

        Assert.EndsWith("shutdown.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "/s", "/t", "60" }, psi.ArgumentList.Take(3));
        Assert.Contains("shutdown /a", psi.ArgumentList[4]);
        Assert.Null(PostBurnActionExecutor.DefaultShutdownInitiator); // only the real app sets it
    }

    [Fact]
    public void AfterBurnChoices_HavePlainEnglishNames()
    {
        foreach (var action in Enum.GetValues<PostBurnAction>())
        {
            string name = PostBurnActionExecutor.GetDisplayName(action);
            Assert.NotEqual(action.ToString(), name);
            Assert.Contains(' ', name);
        }
    }

    private async Task<string> WriteIsoAsync(string name)
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("DATA.BIN", new byte[40_000]);
        string path = Path.Combine(_dir, name);
        await using var fs = File.Create(path);
        await IsoUdfImageBuilder.BuildAsync(root, fs, new IsoBuildOptions(VolumeLabel: "REVIEW"));
        return path;
    }

    [Fact]
    public async Task ConvertToBinCue_WritesCueWithoutByteOrderMark()
    {
        string iso = await WriteIsoAsync("disc.iso");
        string bin = Path.Combine(_dir, "out.bin");

        await LegacyDiscImageConverter.ConvertImageToBinCueAsync(iso, bin);

        byte[] cue = File.ReadAllBytes(Path.ChangeExtension(bin, ".cue"));
        Assert.NotEqual(0xEF, cue[0]);
        Assert.StartsWith("FILE \"out.bin\" BINARY", Encoding.ASCII.GetString(cue));
    }

    [Fact]
    public async Task ConvertToBinCue_RefusesToOverwriteTheSourceImage()
    {
        string isoAsBin = Path.Combine(_dir, "disc.bin");
        File.Move(await WriteIsoAsync("disc.iso"), isoAsBin);
        byte[] before = File.ReadAllBytes(isoAsBin);

        await Assert.ThrowsAsync<IOException>(() => LegacyDiscImageConverter.ConvertImageToBinCueAsync(isoAsBin, isoAsBin));
        Assert.Equal(before, File.ReadAllBytes(isoAsBin));
    }

    [Fact]
    public void RawBinWithoutIsoVolume_IsTreatedAsAudio_SoItIsNeverBurnedAsData()
    {
        string bin = Path.Combine(_dir, "audio.bin");
        byte[] sectors = new byte[OpticalConstants.RawSectorSize * 300];
        new Random(7).NextBytes(sectors);
        File.WriteAllBytes(bin, sectors);

        var info = LegacyDiscImageConverter.Inspect(bin);

        Assert.True(info.IsAudioDisc);
        Assert.Throws<InvalidOperationException>(() => LegacyDiscImageConverter.OpenIsoUserDataStream(info));
    }

    [Theory]
    [InlineData("disc.iso", true)]
    [InlineData("disc.bin", true)]
    [InlineData("disc.nrg", true)]
    [InlineData("disc.cue", false)]
    [InlineData("notes.txt", false)]
    public void BurnQueue_AcceptsDataImagesOnly(string file, bool expected)
    {
        Assert.Equal(expected, BurnQueueService.IsQueueableImage(file, out string? reason));
        Assert.Equal(expected, reason == null);
    }
}
