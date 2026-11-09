using System.Buffers.Binary;
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
/// Integration tests for the <c>rip-audio</c> CLI command covering uncompressed WAV extraction,
/// lossless FLAC transcoding with metadata tags and CUE rewriting, track subset selection,
/// error validation, and disc ID calculation.
/// </summary>
public sealed class RipAudioCliTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly VirtualDiscMedium _medium;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public RipAudioCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_RipAudioCli_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _recorder = new VirtualDiscRecorder(
            uniqueId: "VDRV-01",
            vendorId: "IGNIS",
            productId: "VIRTUAL DVD/CD-RW",
            volumePath: @"V:\");

        _medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(_medium);

        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public async Task InitializeAsync()
    {
        // 1. Generate 3 synthetic sine-wave PCM streams (as in the audio burn tests)
        // Track 1: 440 Hz, 50 frames
        // Track 2: 880 Hz, 75 frames
        // Track 3: 1760 Hz, 100 frames
        const int track1Frames = 50;
        const int track2Frames = 75;
        const int track3Frames = 100;

        byte[] track1Pcm = GenerateSineWavePcm(440.0, track1Frames);
        byte[] track2Pcm = GenerateSineWavePcm(880.0, track2Frames);
        byte[] track3Pcm = GenerateSineWavePcm(1760.0, track3Frames);

        string wavDir = Path.Combine(_tempDir, "input_wavs");
        Directory.CreateDirectory(wavDir);

        string wavPath1 = Path.Combine(wavDir, "input_track1.wav");
        string wavPath2 = Path.Combine(wavDir, "input_track2.wav");
        string wavPath3 = Path.Combine(wavDir, "input_track3.wav");

        WavFileCodec.WriteWavFile(wavPath1, track1Pcm);
        WavFileCodec.WriteWavFile(wavPath2, track2Pcm);
        WavFileCodec.WriteWavFile(wavPath3, track3Pcm);

        // 2. Create compilation with Album/Track CD-Text metadata and DiscAtOnceGapless mode
        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Synthetic Harmonics Vol 1",
            AlbumPerformer = "The Oscillation Trio",
            Songwriter = "Nikola Tesla",
            Composer = "Johann Sebastian Sine",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };

        compilation.AddTrack(new AudioCdTrackItem(
            1,
            wavPath1,
            "A4 Concert Pitch",
            "The Oscillation Trio",
            "USAA12600001",
            PreGapFrames: 150));

        compilation.AddTrack(new AudioCdTrackItem(
            2,
            wavPath2,
            "A5 Octave Leap",
            "The Oscillation Trio",
            "USAA12600002",
            PreGapFrames: 0));

        compilation.AddTrack(new AudioCdTrackItem(
            3,
            wavPath3,
            "A6 Harmonic Shimmer",
            "The Oscillation Trio",
            "USAA12600003",
            PreGapFrames: 0));

        // 3. Burn compilation to virtual recorder with CD-Text
        using var decoder = new MediaFoundationAudioDecoder();
        var burnService = new AudioCdBurnService(_burnEngine, decoder);
        var burnResult = await burnService.BurnCompilationAsync(_recorder, compilation);

        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(track1Frames + track2Frames + track3Frames, burnResult.SectorsWritten);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _recorder.Dispose();
        _medium.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore temp folder cleanup issues
        }
    }

    private static (StringWriter stdout, StringWriter stderr) CreateStringWriters() =>
        (new StringWriter(), new StringWriter());

    private static JsonDocument ParseJsonOutput(StringWriter stdout)
    {
        string text = stdout.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected non-empty stdout JSON output.");
        return JsonDocument.Parse(text);
    }

    private static byte[] GenerateSineWavePcm(double frequency, int frameCount, short amplitude = 16000)
    {
        int sampleRate = OpticalConstants.CdAudioSampleRate;
        int totalStereoSamples = frameCount * OpticalConstants.CdAudioSamplesPerFrame;
        byte[] pcm = new byte[totalStereoSamples * OpticalConstants.CdAudioBytesPerSample];

        for (int i = 0; i < totalStereoSamples; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)Math.Round(amplitude * Math.Sin(2.0 * Math.PI * frequency * t));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), sample);
        }

        return pcm;
    }

    [Fact]
    public async Task RipAudio_Wav_Json_RipsAllTracksAndReportsDiscIds()
    {
        string outDir = Path.Combine(_tempDir, "WavJsonOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr.ToString());

        var wavFiles = Directory.GetFiles(outDir, "*.wav");
        Assert.Equal(3, wavFiles.Length);

        using var doc = ParseJsonOutput(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
        Assert.Equal(Path.GetFullPath(outDir), root.GetProperty("outputFolder").GetString());
        Assert.Equal(3, root.GetProperty("tracksRipped").GetInt32());
        Assert.Equal(225, root.GetProperty("sectorsRead").GetInt32());

        string? mbId = root.GetProperty("musicBrainzId").GetString();
        Assert.NotNull(mbId);
        Assert.Equal(28, mbId.Length);

        string? freeDb = root.GetProperty("freeDbId").GetString();
        Assert.NotNull(freeDb);
        Assert.Equal(8, freeDb.Length);

        Assert.Equal("cd-text", root.GetProperty("metadataSource").GetString());

        Assert.Contains(wavFiles, f => f.Contains("A4 Concert Pitch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wavFiles, f => f.Contains("A5 Octave Leap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wavFiles, f => f.Contains("A6 Harmonic Shimmer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RipAudio_TrackSubset_RipsOnlyRequestedTracks()
    {
        string outDir = Path.Combine(_tempDir, "SubsetOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--tracks", "1,3" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        var wavFiles = Directory.GetFiles(outDir, "*.wav");
        Assert.Equal(2, wavFiles.Length);
        Assert.Contains(wavFiles, f => Path.GetFileName(f).StartsWith("01", StringComparison.Ordinal));
        Assert.Contains(wavFiles, f => Path.GetFileName(f).StartsWith("03", StringComparison.Ordinal));
        Assert.DoesNotContain(wavFiles, f => Path.GetFileName(f).StartsWith("02", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RipAudio_InvalidFormat_ReturnsError()
    {
        string outDir = Path.Combine(_tempDir, "InvalidFormatOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", "mp3" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(1, exitCode);
        string err = stderr.ToString();
        Assert.Contains("mp3", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RipAudio_InvalidTracks_ReturnsError()
    {
        string outDir = Path.Combine(_tempDir, "InvalidTracksOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--tracks", "1,x" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("20000")]
    public async Task RipAudio_InvalidReadOffset_ReturnsError(string offset)
    {
        string outDir = Path.Combine(_tempDir, "InvalidOffsetOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--read-offset", offset },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(1, exitCode);
        Assert.Contains("read offset", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RipAudio_Json_ReportsReadOffsetAndPerTrackChecksums()
    {
        string outDir = Path.Combine(_tempDir, "ChecksumOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--read-offset", "-6", "--json" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);
        using var doc = ParseJsonOutput(stdout);
        Assert.Equal(-6, doc.RootElement.GetProperty("readOffset").GetInt32());
        var checksums = doc.RootElement.GetProperty("trackChecksums");
        Assert.Equal(3, checksums.GetArrayLength());
        foreach (var t in checksums.EnumerateArray())
        {
            Assert.Equal(8, t.GetProperty("crc32").GetString()!.Length);
            Assert.Equal(8, t.GetProperty("accurateRipV1").GetString()!.Length);
            Assert.Equal(8, t.GetProperty("accurateRipV2").GetString()!.Length);
        }
    }

    [Fact]
    public async Task RipAudio_Flac_EncodesAndRewritesCue()
    {
        if (!FfmpegLocator.IsFfmpegAvailable())
        {
            return;
        }

        string outDir = Path.Combine(_tempDir, "FlacOutput");
        var (stdout, stderr) = CreateStringWriters();

        int exitCode = await CliCommandRunner.RunAsync(
            new[] { "rip-audio", "--drive", "VDRV-01", "--output", outDir, "--format", "flac" },
            stdout,
            stderr,
            _deviceService,
            _burnEngine);

        Assert.Equal(0, exitCode);

        var flacFiles = Directory.GetFiles(outDir, "*.flac");
        Assert.Equal(3, flacFiles.Length);

        var wavFiles = Directory.GetFiles(outDir, "*.wav");
        Assert.Empty(wavFiles);

        string cuePath = Path.Combine(outDir, "album.cue");
        Assert.True(File.Exists(cuePath), "Expected album.cue companion file to exist.");

        string cueContent = File.ReadAllText(cuePath);
        Assert.Contains(".flac", cueContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".wav", cueContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlacEncoder_BuildArguments_OrdersArgumentsAndOmitsEmptyTags()
    {
        // 1. Without ISRC and Date
        var tagsNoOpt = new AudioTrackTags(
            Title: "Track Alpha",
            Artist: "Artist Beta",
            Album: "Album Gamma",
            TrackNumber: 2,
            TrackTotal: 10,
            Isrc: null,
            Date: null);

        var argsNoOpt = FlacEncoder.BuildArguments(@"C:\test\in.wav", @"C:\test\out.flac", tagsNoOpt);

        Assert.Equal("-hide_banner", argsNoOpt[0]);
        Assert.Equal("-nostdin", argsNoOpt[1]);
        Assert.Equal("-y", argsNoOpt[2]);
        Assert.Equal("-i", argsNoOpt[3]);
        Assert.Equal(@"C:\test\in.wav", argsNoOpt[4]);
        Assert.Equal("-map_metadata", argsNoOpt[5]);
        Assert.Equal("-1", argsNoOpt[6]);
        Assert.Equal("-c:a", argsNoOpt[7]);
        Assert.Equal("flac", argsNoOpt[8]);
        Assert.Equal("-compression_level", argsNoOpt[9]);
        Assert.Equal("8", argsNoOpt[10]);

        Assert.Contains("TITLE=Track Alpha", argsNoOpt);
        Assert.Contains("ARTIST=Artist Beta", argsNoOpt);
        Assert.Contains("ALBUM=Album Gamma", argsNoOpt);
        Assert.Contains("TRACKNUMBER=2", argsNoOpt);
        Assert.Contains("TRACKTOTAL=10", argsNoOpt);

        Assert.DoesNotContain(argsNoOpt, a => a.StartsWith("ISRC=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(argsNoOpt, a => a.StartsWith("DATE=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\test\out.flac", argsNoOpt[^1]);

        // 2. With ISRC and Date
        var tagsWithOpt = new AudioTrackTags(
            Title: "Track Alpha",
            Artist: "Artist Beta",
            Album: "Album Gamma",
            TrackNumber: 2,
            TrackTotal: 10,
            Isrc: "USAA12600001",
            Date: "2026-10-30");

        var argsWithOpt = FlacEncoder.BuildArguments(@"C:\test\in.wav", @"C:\test\out.flac", tagsWithOpt);

        Assert.Contains("ISRC=USAA12600001", argsWithOpt);
        Assert.Contains("DATE=2026-10-30", argsWithOpt);
        Assert.Equal(@"C:\test\out.flac", argsWithOpt[^1]);

        int isrcIdx = argsWithOpt.ToList().IndexOf("ISRC=USAA12600001");
        Assert.True(isrcIdx > 0);
        Assert.Equal("-metadata", argsWithOpt[isrcIdx - 1]);

        int dateIdx = argsWithOpt.ToList().IndexOf("DATE=2026-10-30");
        Assert.True(dateIdx > 0);
        Assert.Equal("-metadata", argsWithOpt[dateIdx - 1]);

        // 3. Empty / whitespace strings should be omitted
        var tagsEmpty = new AudioTrackTags(
            Title: "Track Alpha",
            Artist: "Artist Beta",
            Album: "Album Gamma",
            TrackNumber: 2,
            TrackTotal: 10,
            Isrc: "   ",
            Date: "");

        var argsEmpty = FlacEncoder.BuildArguments(@"C:\test\in.wav", @"C:\test\out.flac", tagsEmpty);
        Assert.DoesNotContain(argsEmpty, a => a.StartsWith("ISRC=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(argsEmpty, a => a.StartsWith("DATE=", StringComparison.OrdinalIgnoreCase));
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
            return _recorders.FirstOrDefault(r =>
                string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.VolumePath?.TrimEnd('\\'), uniqueId.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
    }
}
