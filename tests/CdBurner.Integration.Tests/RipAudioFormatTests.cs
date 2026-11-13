using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Rips a virtual three-track audio CD to AAC, MP3 and Opus, decodes each output again and compares its length with
/// the source (sectors x 588 samples) and its level with the source sine waves.
/// </summary>
public sealed class RipAudioFormatTests : IAsyncLifetime, IDisposable
{
    private static readonly int[] TrackSectors = [50, 75, 100];
    private const int SamplesPerSector = 588; // 2,352 bytes / 4 bytes per stereo sample
    private const double SineAmplitude = 16000.0;

    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly VirtualDiscMedium _medium;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public RipAudioFormatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_RipFormats_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        _medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(_medium);
        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public async Task InitializeAsync()
    {
        double[] frequencies = [440.0, 880.0, 1760.0];
        string[] titles = ["A4 Concert Pitch", "A5 Octave Leap", "A6 Harmonic Shimmer"];
        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Synthetic Harmonics Vol 1",
            AlbumPerformer = "The Oscillation Trio",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };

        for (int i = 0; i < 3; i++)
        {
            string wav = Path.Combine(_tempDir, $"in{i + 1}.wav");
            WavFileCodec.WriteWavFile(wav, SinePcm(frequencies[i], TrackSectors[i] * SamplesPerSector));
            compilation.AddTrack(new AudioCdTrackItem(i + 1, wav, titles[i], "The Oscillation Trio", $"USAA1260000{i + 1}", PreGapFrames: i == 0 ? 150 : 0));
        }

        using var decoder = new MediaFoundationAudioDecoder();
        var burn = await new AudioCdBurnService(_burnEngine, decoder).BurnCompilationAsync(_recorder, compilation);
        Assert.True(burn.Success, burn.ErrorMessage);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _recorder.Dispose();
        _medium.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] SinePcm(double frequency, int stereoSamples)
    {
        byte[] pcm = new byte[stereoSamples * 4];
        for (int i = 0; i < stereoSamples; i++)
        {
            short s = (short)Math.Round(SineAmplitude * Math.Sin(2.0 * Math.PI * frequency * i / OpticalConstants.CdAudioSampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), s);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), s);
        }
        return pcm;
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Left-channel samples as doubles.</summary>
    private static double[] LeftChannel(byte[] pcm16Stereo)
    {
        var left = new double[pcm16Stereo.Length / 4];
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16Stereo.AsSpan(i * 4, 2));
        }
        return left;
    }

    private static double Rms(double[] samples, int skip, int take)
    {
        double sum = 0;
        for (int i = skip; i < skip + take; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / take);
    }

    /// <summary>Decodes with the Windows decoder to 44.1 kHz 16-bit stereo and returns the PCM bytes.</summary>
    private static byte[] DecodeWithWindows(string path)
    {
        using var decoder = new MediaFoundationAudioDecoder();
        using var stream = decoder.OpenRedBookPcmStream(path);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Decodes with ffmpeg to raw 16-bit stereo at the file's own rate, 48 kHz for Opus.</summary>
    private static async Task<byte[]> DecodeWithFfmpegAsync(string ffmpeg, string path, int sampleRate)
    {
        string raw = path + ".raw";
        var (exit, _, err) = await FfmpegProcess.CaptureAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-i", path, "-f", "s16le", "-ac", "2", "-ar", sampleRate.ToString(), raw]);
        Assert.True(exit == 0, err);
        return await File.ReadAllBytesAsync(raw);
    }

    [Fact]
    public async Task RipAudio_Aac_WritesM4aFilesWithSourceLengthAndTags()
    {
        string outDir = Path.Combine(_tempDir, "aac");
        var (exit, stdout, stderr) = await RunCliAsync(
            "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", "aac", "--bitrate", "160", "--keep-wav", "--json");

        Assert.True(exit == 0, stderr + stdout);
        var m4a = Directory.GetFiles(outDir, "*.m4a").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(3, m4a.Length);

        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("AAC", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(160, doc.RootElement.GetProperty("bitrateKbps").GetInt32());

        string[] titles = ["A4 Concert Pitch", "A5 Octave Leap", "A6 Harmonic Shimmer"];
        double[] frequencies = [440.0, 880.0, 1760.0];
        using var probe = new MediaFoundationAudioDecoder();
        for (int i = 0; i < 3; i++)
        {
            // Container: an MPEG-4 file starts with an 'ftyp' box (ISO/IEC 14496-12).
            byte[] head = new byte[12];
            using (var fs = File.OpenRead(m4a[i])) { fs.ReadExactly(head); }
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head, 4, 4));

            // Length: decoded samples equal the source's, give or take the encoder's priming and frame padding.
            byte[] decoded = DecodeWithWindows(m4a[i]);
            int expectedSamples = TrackSectors[i] * SamplesPerSector;
            int decodedSamples = decoded.Length / 4;
            Assert.InRange(decodedSamples, expectedSamples - 2048, expectedSamples + 2048 * 2);

            // Level: the sine's RMS is amplitude / sqrt(2); AAC at 160 kbit/s keeps it within a few percent.
            double[] left = LeftChannel(decoded);
            int take = Math.Min(left.Length - 4096, expectedSamples - 4096);
            double rms = Rms(left, 2048, take);
            Assert.InRange(rms, SineAmplitude / Math.Sqrt(2) * 0.9, SineAmplitude / Math.Sqrt(2) * 1.1);

            // Pitch: zero crossings of the left channel give the frequency (positive-going crossings per second).
            int crossings = 0;
            for (int s = 2049; s < 2048 + take; s++) if (left[s - 1] < 0 && left[s] >= 0) crossings++;
            double hz = crossings / (take / (double)OpticalConstants.CdAudioSampleRate);
            Assert.InRange(hz, frequencies[i] * 0.98, frequencies[i] * 1.02);

            // Tags: read back through the same Windows property handler File Explorer uses.
            var meta = probe.ProbeAudioFile(m4a[i]);
            Assert.Equal("AAC", meta.FormatName);
            Assert.Equal(titles[i], meta.Title);
            Assert.Equal("The Oscillation Trio", meta.Artist);
            Assert.Equal("Synthetic Harmonics Vol 1", meta.Album);
        }

        string cue = File.ReadAllText(Path.Combine(outDir, "album.cue"));
        Assert.Contains(".m4a", cue);
        Assert.DoesNotContain(".wav", cue);
    }

    [Fact]
    public async Task RipAudio_Aac_DeletesWavFilesUnlessKept()
    {
        string outDir = Path.Combine(_tempDir, "aac-nowav");
        var (exit, _, stderr) = await RunCliAsync("rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", "aac");
        Assert.True(exit == 0, stderr);
        Assert.Empty(Directory.GetFiles(outDir, "*.wav"));
        Assert.Equal(3, Directory.GetFiles(outDir, "*.m4a").Length);
    }

    [Theory]
    [InlineData("aac", "100")]
    [InlineData("aac", "abc")]
    [InlineData("mp3", "8")]
    [InlineData("mp3", "400")]
    [InlineData("opus", "5")]
    [InlineData("wav", "128")]
    public async Task RipAudio_BadBitrate_IsRejectedBeforeAnyFileIsWritten(string format, string bitrate)
    {
        string outDir = Path.Combine(_tempDir, "badrate");
        var (exit, _, stderr) = await RunCliAsync("rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", format, "--bitrate", bitrate);
        Assert.Equal(1, exit);
        Assert.Contains("bitrate", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outDir) && Directory.GetFiles(outDir).Length > 0);
    }

    [Theory]
    [InlineData(AudioRipFormat.Mp3)]
    [InlineData(AudioRipFormat.Opus)]
    [InlineData(AudioRipFormat.Flac)]
    public void Transcoder_WithoutFfmpeg_SaysHowToGetIt(AudioRipFormat format)
    {
        var transcoder = new AudioRipTranscoder(new MediaFoundationAacEncoder());
        string? problem = transcoder.CheckPrerequisites(new AudioRipTranscodeSettings(format, 0, Path.Combine(_tempDir, "no-such-ffmpeg.exe")));
        Assert.NotNull(problem);
        Assert.Contains("ffmpeg", problem);
        Assert.Contains("Install FFmpeg", problem);
        Assert.Contains("AAC", problem); // points at the format that needs no download
    }

    [Fact]
    public void Transcoder_AacNeedsNoFfmpeg()
    {
        var transcoder = new AudioRipTranscoder(new MediaFoundationAacEncoder());
        Assert.Null(transcoder.CheckPrerequisites(new AudioRipTranscodeSettings(AudioRipFormat.Aac, 128, null)));
    }

    // ffmpeg-based formats: run only where an ffmpeg with the encoder is installed (the optional 1-click build has both).

    private static async Task<string?> FfmpegWithAsync(string encoder)
    {
        string? candidate = Environment.GetEnvironmentVariable("IGNISDISC_TEST_FFMPEG");
        string? path = FfmpegLocator.FindFfmpegExecutable(candidate);
        if (path == null) return null;
        try { return await FfmpegProcess.HasEncoderAsync(path, encoder) ? path : null; }
        catch (InvalidOperationException) { return null; }
    }

    private async Task RipThroughTranscoderAsync(AudioRipFormat format, string ffmpeg, int bitrate, string outDir)
    {
        var options = new AudioRipOptions { SecureMode = false };
        var rip = await new AudioCdRipperService().RipAudioCdAsync(_recorder, _burnEngine, outDir, options);
        Assert.True(rip.Success, rip.ErrorMessage);
        var result = await new AudioRipTranscoder(new MediaFoundationAacEncoder())
            .TranscodeAsync(rip, new AudioRipTranscodeSettings(format, bitrate, ffmpeg));
        Assert.Equal(3, result.Files.Count);
        Assert.Empty(Directory.GetFiles(outDir, "*.wav"));
    }

    [Fact]
    public async Task Rip_Mp3_DecodesToSourceLengthWithTagsAndReplayGain()
    {
        string? ffmpeg = await FfmpegWithAsync("libmp3lame");
        if (ffmpeg == null) return;

        string outDir = Path.Combine(_tempDir, "mp3");
        await RipThroughTranscoderAsync(AudioRipFormat.Mp3, ffmpeg, 192, outDir);

        var files = Directory.GetFiles(outDir, "*.mp3").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(3, files.Length);
        using var windows = new MediaFoundationAudioDecoder();
        for (int i = 0; i < 3; i++)
        {
            int expected = TrackSectors[i] * SamplesPerSector;
            byte[] decoded = await DecodeWithFfmpegAsync(ffmpeg, files[i], OpticalConstants.CdAudioSampleRate);
            // ffmpeg honours the LAME header's encoder delay and padding, so the length matches to within one MP3 frame.
            Assert.InRange(decoded.Length / 4, expected - 1152, expected + 1152);

            double[] left = LeftChannel(decoded);
            Assert.InRange(Rms(left, 2048, left.Length - 4096), SineAmplitude / Math.Sqrt(2) * 0.9, SineAmplitude / Math.Sqrt(2) * 1.1);

            var meta = windows.ProbeAudioFile(files[i]);
            Assert.Equal(new[] { "A4 Concert Pitch", "A5 Octave Leap", "A6 Harmonic Shimmer" }[i], meta.Title);
            Assert.Equal("The Oscillation Trio", meta.Artist);
            Assert.Equal("Synthetic Harmonics Vol 1", meta.Album);

            // ReplayGain is stored as an ID3v2 TXXX frame; its description text is plain ASCII in the file.
            string raw = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(files[i]));
            Assert.Contains("REPLAYGAIN_TRACK_GAIN", raw);
        }
    }

    [Fact]
    public async Task Rip_Opus_DecodesToSourceLengthWithTagsAndReplayGain()
    {
        string? ffmpeg = await FfmpegWithAsync("libopus");
        if (ffmpeg == null) return;

        string outDir = Path.Combine(_tempDir, "opus");
        await RipThroughTranscoderAsync(AudioRipFormat.Opus, ffmpeg, 128, outDir);

        var files = Directory.GetFiles(outDir, "*.opus").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(3, files.Length);
        for (int i = 0; i < 3; i++)
        {
            // Opus runs at 48 kHz: the same duration is source samples x 48000 / 44100; the pre-skip header makes it exact to one 20 ms frame.
            long expected = (long)TrackSectors[i] * SamplesPerSector * 48000 / OpticalConstants.CdAudioSampleRate;
            byte[] decoded = await DecodeWithFfmpegAsync(ffmpeg, files[i], 48000);
            Assert.InRange(decoded.Length / 4, expected - 960, expected + 960);

            double[] left = LeftChannel(decoded);
            Assert.InRange(Rms(left, 2048, left.Length - 4096), SineAmplitude / Math.Sqrt(2) * 0.9, SineAmplitude / Math.Sqrt(2) * 1.1);

            // Vorbis comments are plain UTF-8 inside the OpusTags packet.
            string raw = System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(files[i]));
            Assert.Contains("TITLE=" + new[] { "A4 Concert Pitch", "A5 Octave Leap", "A6 Harmonic Shimmer" }[i], raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ARTIST=The Oscillation Trio", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ALBUM=Synthetic Harmonics Vol 1", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TRACKNUMBER=" + (i + 1), raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("REPLAYGAIN_TRACK_GAIN=", raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FfmpegAudioEncoder_BuildArguments_MapsTagsPerFormat()
    {
        var tags = new AudioTrackTags("T", "A", "Al", 2, 9, "USAA12600001", null, "-3.10 dB", "0.912345", "-2.00 dB", "0.99");

        var mp3 = FfmpegAudioEncoder.BuildArguments(AudioRipFormat.Mp3, "in.wav", "out.mp3", tags, 192);
        Assert.Contains("libmp3lame", mp3);
        Assert.Contains("192k", mp3);
        Assert.Contains("track=2/9", mp3);
        Assert.Contains("TSRC=USAA12600001", mp3);
        Assert.Contains("REPLAYGAIN_ALBUM_PEAK=0.99", mp3);
        Assert.Equal("out.mp3", mp3[^1]);

        var opus = FfmpegAudioEncoder.BuildArguments(AudioRipFormat.Opus, "in.wav", "out.opus", tags, 96);
        Assert.Contains("libopus", opus);
        Assert.Contains("96k", opus);
        Assert.Contains("TRACKNUMBER=2", opus);
        Assert.Contains("TRACKTOTAL=9", opus);
        Assert.Contains("ISRC=USAA12600001", opus);
        Assert.DoesNotContain(opus, a => a.StartsWith("TSRC=", StringComparison.Ordinal));

        Assert.Throws<ArgumentOutOfRangeException>(() => FfmpegAudioEncoder.BuildArguments(AudioRipFormat.Flac, "in.wav", "out.flac", tags, 0));
    }

    private sealed class TestDeviceService(params IDiscRecorderBackend[] recorders) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) =>
            includeVirtual ? recorders : recorders.Where(r => !r.IsVirtual).ToList();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }
}
