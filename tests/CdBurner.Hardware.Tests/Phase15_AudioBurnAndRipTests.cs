using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Phase 15 unit tests verifying Audio CD burning (Track-at-Once and gapless Disc-at-Once with CD-Text)
/// and Digital Audio Extraction (DAE) audio CD ripping with companion CUE sheet.
/// </summary>
public class Phase15_AudioBurnAndRipTests : IDisposable
{
    private readonly string _tempDir;

    public Phase15_AudioBurnAndRipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Phase15_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors
        }
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
    public async Task BurnAndRip_3SyntheticSineWaveTracks_GaplessDao_ValidatesLayoutCdTextAndPcmSamples()
    {
        // 1. Generate 3 synthetic sine-wave PCM streams
        // Track 1: 440 Hz, 50 frames = 117,600 bytes
        // Track 2: 880 Hz, 75 frames = 176,400 bytes
        // Track 3: 1760 Hz, 100 frames = 235,200 bytes
        const int track1Frames = 50;
        const int track2Frames = 75;
        const int track3Frames = 100;

        byte[] track1Pcm = GenerateSineWavePcm(440.0, track1Frames);
        byte[] track2Pcm = GenerateSineWavePcm(880.0, track2Frames);
        byte[] track3Pcm = GenerateSineWavePcm(1760.0, track3Frames);

        string wavPath1 = Path.Combine(_tempDir, "input_track1.wav");
        string wavPath2 = Path.Combine(_tempDir, "input_track2.wav");
        string wavPath3 = Path.Combine(_tempDir, "input_track3.wav");

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

        Assert.Equal(3, compilation.TrackCount);

        // 3. Mount blank CD-R 700 MB medium into VirtualDiscRecorder
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        using var decoder = new MediaFoundationAudioDecoder();
        var burnService = new AudioCdBurnService(engine, decoder);

        // 4. Burn compilation
        var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(track1Frames + track2Frames + track3Frames, burnResult.SectorsWritten);

        // 5. Verify virtual disc contains 3 audio tracks with exact start LBAs, frame counts, and valid CD-Text
        Assert.False(medium.IsBlank);
        Assert.True(medium.Raw2352Mode);
        Assert.True(medium.IsFinalized);
        Assert.Single(medium.Sessions);

        var session = medium.Sessions[0];
        Assert.Equal(3, session.Tracks.Count);

        // Track 1: LBA 0..49 (50 frames)
        Assert.Equal(1, session.Tracks[0].TrackNumber);
        Assert.Equal(0, session.Tracks[0].StartLba);
        Assert.Equal(track1Frames, session.Tracks[0].SectorCount);
        Assert.Equal("A4 Concert Pitch", session.Tracks[0].Title);
        Assert.Equal("The Oscillation Trio", session.Tracks[0].Performer);

        // Track 2: LBA 50..124 (75 frames)
        Assert.Equal(2, session.Tracks[1].TrackNumber);
        Assert.Equal(track1Frames, session.Tracks[1].StartLba);
        Assert.Equal(track2Frames, session.Tracks[1].SectorCount);
        Assert.Equal("A5 Octave Leap", session.Tracks[1].Title);

        // Track 3: LBA 125..224 (100 frames)
        Assert.Equal(3, session.Tracks[2].TrackNumber);
        Assert.Equal(track1Frames + track2Frames, session.Tracks[2].StartLba);
        Assert.Equal(track3Frames, session.Tracks[2].SectorCount);
        Assert.Equal("A6 Harmonic Shimmer", session.Tracks[2].Title);

        // Verify CD-Text bytes recorded on disc
        Assert.NotNull(medium.CdTextBytes);
        Assert.True(medium.CdTextBytes.Length >= 18 * 4);
        var parsedCdText = CdTextPackParser.ParsePacks(medium.CdTextBytes);
        Assert.Equal("Synthetic Harmonics Vol 1", parsedCdText.AlbumTitle);
        Assert.Equal("The Oscillation Trio", parsedCdText.AlbumPerformer);
        Assert.NotNull(parsedCdText.Tracks);
        Assert.Equal(3, parsedCdText.Tracks.Count);
        Assert.Equal("A4 Concert Pitch", parsedCdText.Tracks[0].Title);
        Assert.Equal("A5 Octave Leap", parsedCdText.Tracks[1].Title);
        Assert.Equal("A6 Harmonic Shimmer", parsedCdText.Tracks[2].Title);

        // 6. Rip the burned virtual Audio CD to a temp folder using AudioCdRipperService
        string ripFolder = Path.Combine(_tempDir, "ripped");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(recorder, engine, ripFolder);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(3, ripResult.TracksRipped);
        Assert.Equal(track1Frames + track2Frames + track3Frames, ripResult.TotalSectorsRead);
        Assert.NotNull(ripResult.CueSheetPath);
        Assert.True(File.Exists(ripResult.CueSheetPath!));

        // 7. Verify 3 valid .wav files created with sanitized filenames derived from decoded CD-Text
        string wav1 = Path.Combine(ripFolder, "01 - The Oscillation Trio - A4 Concert Pitch.wav");
        string wav2 = Path.Combine(ripFolder, "02 - The Oscillation Trio - A5 Octave Leap.wav");
        string wav3 = Path.Combine(ripFolder, "03 - The Oscillation Trio - A6 Harmonic Shimmer.wav");

        Assert.True(File.Exists(wav1), $"Expected {wav1} to exist.");
        Assert.True(File.Exists(wav2), $"Expected {wav2} to exist.");
        Assert.True(File.Exists(wav3), $"Expected {wav3} to exist.");

        // 8. Verify WAV headers and matching PCM audio samples
        var meta1 = WavFileCodec.ProbeAudioFile(wav1);
        Assert.Equal(OpticalConstants.CdAudioSampleRate, meta1.SampleRate);
        Assert.Equal(OpticalConstants.CdAudioChannels, meta1.Channels);
        Assert.Equal(OpticalConstants.CdAudioBitsPerSample, meta1.BitsPerSample);
        Assert.Equal(track1Pcm.Length, meta1.TotalPcmBytes);

        var meta2 = WavFileCodec.ProbeAudioFile(wav2);
        Assert.Equal(track2Pcm.Length, meta2.TotalPcmBytes);

        var meta3 = WavFileCodec.ProbeAudioFile(wav3);
        Assert.Equal(track3Pcm.Length, meta3.TotalPcmBytes);

        byte[] rippedPcm1 = WavFileCodec.DecodeToRedBookPcm(wav1);
        Assert.Equal(track1Pcm, rippedPcm1);

        byte[] rippedPcm2 = WavFileCodec.DecodeToRedBookPcm(wav2);
        Assert.Equal(track2Pcm, rippedPcm2);

        byte[] rippedPcm3 = WavFileCodec.DecodeToRedBookPcm(wav3);
        Assert.Equal(track3Pcm, rippedPcm3);

        // 9. Verify CUE sheet contents
        string cueText = File.ReadAllText(ripResult.CueSheetPath!);
        Assert.Contains("Synthetic Harmonics Vol 1", cueText);
        Assert.Contains("01 - The Oscillation Trio - A4 Concert Pitch.wav", cueText);
        Assert.Contains("02 - The Oscillation Trio - A5 Octave Leap.wav", cueText);
        Assert.Contains("03 - The Oscillation Trio - A6 Harmonic Shimmer.wav", cueText);
    }

    [Fact]
    public async Task BurnAndRip_CustomPcmStreamFactory_NormalizesAndPadsTracks()
    {
        // Generate half-amplitude sine wave with non-sector-aligned length (e.g., 2.5 sectors = 5880 bytes)
        const int unalignedBytes = 5880;
        byte[] rawSine = new byte[unalignedBytes];
        for (int i = 0; i < unalignedBytes / 4; i++)
        {
            double t = (double)i / OpticalConstants.CdAudioSampleRate;
            short sample = (short)Math.Round(8000 * Math.Sin(2.0 * Math.PI * 440.0 * t));
            BinaryPrimitives.WriteInt16LittleEndian(rawSine.AsSpan(i * 4, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(rawSine.AsSpan(i * 4 + 2, 2), sample);
        }

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Normalized Album",
            AlbumPerformer = "DSP Masters",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            NormalizeAllTracks = true
        };

        compilation.AddTrack(new AudioCdTrackItem(
            TrackNumber: 1,
            SourceFilePath: string.Empty,
            Title: "Padded Track",
            Performer: "DSP Masters",
            NormalizeAudio: true,
            CustomPcmStreamFactory: () => new MemoryStream(rawSine)));

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new AudioCdBurnService(engine);

        var result = await burnService.BurnCompilationAsync(recorder, compilation);
        Assert.True(result.Success, result.ErrorMessage);

        // 5880 bytes padded to 2352 boundary => 3 sectors = 7056 bytes
        const int expectedSectors = 3;
        Assert.Equal(expectedSectors, result.SectorsWritten);

        var session = medium.Sessions[0];
        Assert.Equal(expectedSectors, session.Tracks[0].SectorCount);

        // Rip and verify
        string ripFolder = Path.Combine(_tempDir, "padded_rip");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(recorder, engine, ripFolder);
        Assert.True(ripResult.Success);
        Assert.Single(ripResult.RippedFilePaths);

        byte[] rippedPcm = WavFileCodec.DecodeToRedBookPcm(ripResult.RippedFilePaths[0]);
        Assert.Equal(expectedSectors * OpticalConstants.RawSectorSize, rippedPcm.Length);

        // The peak amplitude should have been normalized to approximately 32767
        short maxAmp = 0;
        for (int i = 0; i < rippedPcm.Length / 2; i++)
        {
            short s = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(rippedPcm.AsSpan(i * 2, 2)));
            if (s > maxAmp) maxAmp = s;
        }
        Assert.True(maxAmp > 30000, $"Expected normalized peak close to 32767, but got {maxAmp}.");
    }

    [Fact]
    public void Compilation_CapacityValidation_74MinAnd80MinMedia()
    {
        var compilation = new AudioCdCompilation();

        // Add dummy track with 300,000 frames (~66.6 minutes)
        compilation.AddTrack(new AudioCdTrackItem(
            TrackNumber: 1,
            SourceFilePath: string.Empty,
            Title: "Long Track",
            Performer: "Artist")
        {
            DurationFrames = 300_000
        });

        // 300,000 frames fits on both 74-min (333,000) and 80-min (360,000)
        Assert.True(compilation.FitsOn74MinMedia);
        Assert.True(compilation.FitsOn80MinMedia);
        Assert.True(compilation.ValidateCapacity(out string? err74, use80MinMedia: false));
        Assert.Null(err74);

        // Add another track of 40,000 frames => Total 340,000 frames (~75.5 minutes)
        compilation.AddTrack(new AudioCdTrackItem(
            TrackNumber: 2,
            SourceFilePath: string.Empty,
            Title: "Track 2",
            Performer: "Artist")
        {
            DurationFrames = 40_000
        });

        Assert.Equal(340_000, compilation.TotalFrames);
        Assert.False(compilation.FitsOn74MinMedia);
        Assert.True(compilation.FitsOn80MinMedia);

        Assert.False(compilation.ValidateCapacity(out string? errFail74, use80MinMedia: false));
        Assert.NotNull(errFail74);
        Assert.Contains("exceeds media capacity", errFail74);

        Assert.True(compilation.ValidateCapacity(out string? errPass80, use80MinMedia: true));
        Assert.Null(errPass80);
    }

    [Fact]
    public void Compilation_Max99Tracks_EnforcesLimit()
    {
        var compilation = new AudioCdCompilation();

        for (int i = 1; i <= 99; i++)
        {
            compilation.AddTrack(new AudioCdTrackItem(i, string.Empty, $"Track {i}", "Artist") { DurationFrames = 10 });
        }

        Assert.Equal(99, compilation.TrackCount);

        // 100th track should throw InvalidOperationException
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            compilation.AddTrack(new AudioCdTrackItem(100, string.Empty, "Track 100", "Artist") { DurationFrames = 10 });
        });
        Assert.Contains("maximum of 99", ex.Message);
    }

    [Fact]
    public async Task AudioCdRipper_SanitizesIllegalFileNameCharacters()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new AudioCdBurnService(engine);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Illegal / Characters * Test?",
            AlbumPerformer = "Artist: AC/DC <Hard Rock>",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless
        };

        compilation.AddTrack(new AudioCdTrackItem(
            TrackNumber: 1,
            SourceFilePath: string.Empty,
            Title: "Track: Who Made Who? [12\" Version]",
            Performer: "AC/DC <Rock>",
            CustomPcmStreamFactory: () => new MemoryStream(new byte[2352 * 5])));

        var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
        Assert.True(burnResult.Success);

        string ripFolder = Path.Combine(_tempDir, "illegal_rip");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(recorder, engine, ripFolder);

        Assert.True(ripResult.Success);
        Assert.Single(ripResult.RippedFilePaths);

        string rippedFileName = Path.GetFileName(ripResult.RippedFilePaths[0]);
        // All illegal characters like / : * ? < > " must be sanitized with _
        Assert.DoesNotContain("/", rippedFileName);
        Assert.DoesNotContain("\\", rippedFileName);
        Assert.DoesNotContain(":", rippedFileName);
        Assert.DoesNotContain("*", rippedFileName);
        Assert.DoesNotContain("?", rippedFileName);
        Assert.DoesNotContain("\"", rippedFileName);
        Assert.DoesNotContain("<", rippedFileName);
        Assert.DoesNotContain(">", rippedFileName);
        Assert.DoesNotContain("|", rippedFileName);
        Assert.True(File.Exists(ripResult.RippedFilePaths[0]));
    }

    [Fact]
    public async Task Burn_TrackAtOnce_ConfiguresTaoMode()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new AudioCdBurnService(engine);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "TAO Live Recording",
            AlbumPerformer = "Session Band",
            RecordingMode = AudioRecordingMode.TrackAtOnce,
            IncludeCdText = true
        };

        compilation.AddTrack(new AudioCdTrackItem(
            1,
            string.Empty,
            "Live Take 1",
            "Session Band",
            CustomPcmStreamFactory: () => new MemoryStream(new byte[2352 * 20])));

        compilation.AddTrack(new AudioCdTrackItem(
            2,
            string.Empty,
            "Live Take 2",
            "Session Band",
            CustomPcmStreamFactory: () => new MemoryStream(new byte[2352 * 30])));

        var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(50, burnResult.SectorsWritten);

        var session = medium.Sessions[0];
        Assert.Equal(2, session.Tracks.Count);
        Assert.Equal(20, session.Tracks[0].SectorCount);
        Assert.Equal(30, session.Tracks[1].SectorCount);
    }

    [Fact]
    public async Task Ripper_TrackFilter_RipsOnlySelectedTrack()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new AudioCdBurnService(engine);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "EP Release",
            AlbumPerformer = "Indie Star"
        };

        compilation.AddTrack(new AudioCdTrackItem(1, string.Empty, "Intro", "Indie Star", CustomPcmStreamFactory: () => new MemoryStream(new byte[2352 * 10])));
        compilation.AddTrack(new AudioCdTrackItem(2, string.Empty, "Hit Single", "Indie Star", CustomPcmStreamFactory: () => new MemoryStream(new byte[2352 * 25])));
        compilation.AddTrack(new AudioCdTrackItem(3, string.Empty, "Outro", "Indie Star", CustomPcmStreamFactory: () => new MemoryStream(new byte[2352 * 15])));

        var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
        Assert.True(burnResult.Success);

        string ripFolder = Path.Combine(_tempDir, "filtered_rip");
        var ripper = new AudioCdRipperService();

        // Rip ONLY track 2
        var ripResult = await ripper.RipAudioCdAsync(recorder, engine, ripFolder, new AudioRipOptions
        {
            TrackNumbersToRip = new[] { 2 }
        });

        Assert.True(ripResult.Success);
        Assert.Equal(1, ripResult.TracksRipped);
        Assert.Equal(25, ripResult.TotalSectorsRead);
        Assert.Single(ripResult.RippedFilePaths);
        Assert.Contains("02 - Indie Star - Hit Single.wav", ripResult.RippedFilePaths[0]);
    }

    [Fact]
    public void Compilation_TrackManagement_AddRemoveRenumberAndMsfDuration()
    {
        var compilation = new AudioCdCompilation();

        var t1 = compilation.AddTrack(string.Empty, "Song 1", "Artist", preGapFrames: 150);
        t1.DurationFrames = 75; // 1.0 sec = 00:01:00

        var t2 = compilation.AddTrack(string.Empty, "Song 2", "Artist");
        t2.DurationFrames = 150; // 2.0 sec = 00:02:00

        var t3 = compilation.AddTrack(string.Empty, "Song 3", "Artist");
        t3.DurationFrames = 225; // 3.0 sec = 00:03:00

        Assert.Equal(3, compilation.TrackCount);
        Assert.Equal(450, compilation.TotalFrames); // 6.0 sec = 00:06:00
        Assert.Equal(new MsfAddress(0, 6, 0), compilation.TotalDurationMsf);
        Assert.Equal(TimeSpan.FromSeconds(6), compilation.TotalDuration);

        // Remove track 2
        bool removed = compilation.RemoveTrack(2);
        Assert.True(removed);
        Assert.Equal(2, compilation.TrackCount);

        // Check sequential renumbering: t3 became track 2
        Assert.Equal(1, compilation.Tracks[0].TrackNumber);
        Assert.Equal("Song 1", compilation.Tracks[0].Title);
        Assert.Equal(2, compilation.Tracks[1].TrackNumber);
        Assert.Equal("Song 3", compilation.Tracks[1].Title);

        Assert.Equal(300, compilation.TotalFrames); // 4.0 sec = 00:04:00
        Assert.Equal(new MsfAddress(0, 4, 0), compilation.TotalDurationMsf);

        compilation.ClearTracks();
        Assert.Equal(0, compilation.TrackCount);
        Assert.Equal(0, compilation.TotalFrames);
    }

    [Fact]
    public async Task BurnAndRip_FlacFiles_16Bit24BitAndId3Prefixed_ProducesExactRedBookAudioCd()
    {
        // Track 1: 16-bit / 44.1 kHz FLAC with Vorbis Comments
        const int track1Frames = 40;
        byte[] track1Pcm = GenerateSineWavePcm(440.0, track1Frames);
        string flac1 = Path.Combine(_tempDir, "01_vorbis_tagged.flac");
        FlacFileCodec.WriteFlacFile(
            flac1,
            track1Pcm,
            sampleRate: 44100,
            channels: 2,
            bitsPerSample: 16,
            tags: new AudioTrackTags(
                Title: "Lossless Overture",
                Artist: "Ignis Ensemble",
                Album: "Pure Lossless Sessions",
                TrackNumber: 1,
                TrackTotal: 3,
                Isrc: "US-IGN-26-00001"));

        // Track 2: 16-bit / 44.1 kHz FLAC with a 64 KB prepended ID3v2 header
        const int track2Frames = 35;
        byte[] track2Pcm = GenerateSineWavePcm(660.0, track2Frames);
        byte[] rawFlac2 = FlacFileCodec.CreateFlacBytes(
            track2Pcm,
            sampleRate: 44100,
            channels: 2,
            bitsPerSample: 16,
            tags: new AudioTrackTags(
                Title: "ID3v2 Cover Art Track",
                Artist: "Ignis Ensemble",
                Album: "Pure Lossless Sessions",
                TrackNumber: 2,
                TrackTotal: 3));

        // Prepend a 65,536-byte payload ID3v2.3 tag
        const int id3PayloadLen = 65536;
        byte[] id3Header = new byte[10 + id3PayloadLen];
        id3Header[0] = (byte)'I';
        id3Header[1] = (byte)'D';
        id3Header[2] = (byte)'3';
        id3Header[3] = 3;
        id3Header[6] = (byte)((id3PayloadLen >> 21) & 0x7F);
        id3Header[7] = (byte)((id3PayloadLen >> 14) & 0x7F);
        id3Header[8] = (byte)((id3PayloadLen >> 7) & 0x7F);
        id3Header[9] = (byte)(id3PayloadLen & 0x7F);

        string flac2 = Path.Combine(_tempDir, "02_id3_prefixed.flac");
        using (var fs = new FileStream(flac2, FileMode.Create, FileAccess.Write))
        {
            fs.Write(id3Header);
            fs.Write(rawFlac2);
        }

        // Track 3: 24-bit / 48 kHz stereo FLAC (1 second = 48,000 stereo samples = 75 CD frames after resampling)
        const int sr48k = 48000;
        byte[] pcm24Bit48k = new byte[sr48k * 2 * 3]; // 48,000 samples * 2 channels * 3 bytes
        for (int i = 0; i < sr48k; i++)
        {
            int sample24 = (int)Math.Round(2_000_000 * Math.Sin(2.0 * Math.PI * 880.0 * i / sr48k));
            int offset = i * 6;
            pcm24Bit48k[offset] = (byte)(sample24 & 0xFF);
            pcm24Bit48k[offset + 1] = (byte)((sample24 >> 8) & 0xFF);
            pcm24Bit48k[offset + 2] = (byte)((sample24 >> 16) & 0xFF);
            pcm24Bit48k[offset + 3] = (byte)(sample24 & 0xFF);
            pcm24Bit48k[offset + 4] = (byte)((sample24 >> 8) & 0xFF);
            pcm24Bit48k[offset + 5] = (byte)((sample24 >> 16) & 0xFF);
        }

        string flac3 = Path.Combine(_tempDir, "03_hires_24bit_48k.flac");
        FlacFileCodec.WriteFlacFile(flac3, pcm24Bit48k, sampleRate: sr48k, channels: 2, bitsPerSample: 24);

        var decoder = new MediaFoundationAudioDecoder();
        var meta1 = decoder.ProbeAudioFile(flac1);
        Assert.Equal("Lossless Overture", meta1.Title);
        Assert.Equal("Ignis Ensemble", meta1.Artist);
        Assert.Equal("Pure Lossless Sessions", meta1.Album);
        Assert.Equal("US-IGN-26-00001", meta1.Isrc);

        var meta2 = decoder.ProbeAudioFile(flac2);
        Assert.Equal("ID3v2 Cover Art Track", meta2.Title);
        Assert.Equal("Ignis Ensemble", meta2.Artist);

        var meta3 = decoder.ProbeAudioFile(flac3);
        Assert.Equal(48000, meta3.SampleRate);
        Assert.Equal(24, meta3.BitsPerSample);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = meta1.Album ?? "Pure Lossless Sessions",
            AlbumPerformer = meta1.Artist ?? "Ignis Ensemble",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };

        compilation.AddTrack(flac1, meta1.Title!, meta1.Artist!, isrc: meta1.Isrc);
        compilation.AddTrack(flac2, meta2.Title!, meta2.Artist!);
        compilation.AddTrack(flac3, "Hi-Res 24-Bit Finale", "Ignis Ensemble");

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new AudioCdBurnService(engine, decoder);

        var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(track1Frames + track2Frames + 75, burnResult.SectorsWritten);

        // Rip back and verify exact PCM match on tracks 1 and 2
        string ripFolder = Path.Combine(_tempDir, "flac_ripped");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(recorder, engine, ripFolder);
        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(3, ripResult.TracksRipped);

        byte[] ripped1 = WavFileCodec.DecodeToRedBookPcm(ripResult.RippedFilePaths[0]);
        byte[] ripped2 = WavFileCodec.DecodeToRedBookPcm(ripResult.RippedFilePaths[1]);
        byte[] ripped3 = WavFileCodec.DecodeToRedBookPcm(ripResult.RippedFilePaths[2]);

        Assert.Equal(track1Pcm, ripped1);
        Assert.Equal(track2Pcm, ripped2);
        Assert.Equal(75 * OpticalConstants.RawSectorSize, ripped3.Length);
    }

    [Fact]
    public async Task ImportCueSheet_SingleFileFlacWithCue_SlicesTracksByIndex01AndBurnsCleanly()
    {
        // Create a single 3-second (225 frames) Album.flac containing 3 distinct 1-second (75-frame) tracks
        byte[] part1 = GenerateSineWavePcm(330.0, 75);
        byte[] part2 = GenerateSineWavePcm(550.0, 75);
        byte[] part3 = GenerateSineWavePcm(770.0, 75);

        byte[] fullAlbumPcm = new byte[part1.Length + part2.Length + part3.Length];
        Buffer.BlockCopy(part1, 0, fullAlbumPcm, 0, part1.Length);
        Buffer.BlockCopy(part2, 0, fullAlbumPcm, part1.Length, part2.Length);
        Buffer.BlockCopy(part3, 0, fullAlbumPcm, part1.Length + part2.Length, part3.Length);

        string albumFlac = Path.Combine(_tempDir, "AlbumImage.flac");
        FlacFileCodec.WriteFlacFile(albumFlac, fullAlbumPcm);

        string albumCue = Path.Combine(_tempDir, "AlbumImage.cue");
        string cueContent = """
            PERFORMER "Single File Orchestra"
            TITLE "Continuous Concert FLAC"
            FILE "AlbumImage.flac" WAVE
              TRACK 01 AUDIO
                TITLE "Movement I"
                PERFORMER "Single File Orchestra"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Movement II"
                PERFORMER "Single File Orchestra"
                INDEX 01 00:01:00
              TRACK 03 AUDIO
                TITLE "Movement III"
                PERFORMER "Single File Orchestra"
                INDEX 01 00:02:00
            """;
        await File.WriteAllTextAsync(albumCue, cueContent);

        var decoder = new MediaFoundationAudioDecoder();
        var compilation = new AudioCdCompilation();
        int imported = compilation.ImportCueSheet(albumCue, decoder);

        Assert.Equal(3, imported);
        Assert.Equal("Continuous Concert FLAC", compilation.AlbumTitle);
        Assert.Equal("Single File Orchestra", compilation.AlbumPerformer);
        Assert.Equal(75, compilation.Tracks[0].DurationFrames);
        Assert.Equal(75, compilation.Tracks[1].DurationFrames);
        Assert.Equal(75, compilation.Tracks[2].DurationFrames);

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);
        var burnService = new AudioCdBurnService(engine, decoder);

        var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(225, burnResult.SectorsWritten);

        string ripFolder = Path.Combine(_tempDir, "single_flac_cue_rip");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(recorder, engine, ripFolder);
        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(3, ripResult.TracksRipped);

        Assert.Equal(part1, WavFileCodec.DecodeToRedBookPcm(ripResult.RippedFilePaths[0]));
        Assert.Equal(part2, WavFileCodec.DecodeToRedBookPcm(ripResult.RippedFilePaths[1]));
        Assert.Equal(part3, WavFileCodec.DecodeToRedBookPcm(ripResult.RippedFilePaths[2]));
    }
}
