using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using Xunit;

namespace CdBurner.Hardware.Tests;

public class AudioDecoderAndDspTests : IDisposable
{
    private readonly string _tempDirectory;

    public AudioDecoderAndDspTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "CdBurner_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void SyntheticWav_22050Hz_Mono_DecodesToRedBookPcm_75Frames()
    {
        // 1. Generate synthetic 22,050 Hz Mono 16-bit .wav sine wave (1.0 second = 22,050 samples)
        int sampleRate = 22050;
        short channels = 1;
        short bitsPerSample = 16;
        int durationSeconds = 1;
        int totalSamples = sampleRate * durationSeconds;

        byte[] pcmData = new byte[totalSamples * 2];
        double frequency = 440.0; // A4 tone
        short amplitude = 16000;

        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)Math.Round(amplitude * Math.Sin(2.0 * Math.PI * frequency * t));
            BinaryPrimitives.WriteInt16LittleEndian(pcmData.AsSpan(i * 2, 2), sample);
        }

        string wavPath = Path.Combine(_tempDirectory, "sine_22050_mono.wav");
        WavFileCodec.WriteWavFile(wavPath, pcmData, sampleRate, channels, bitsPerSample);

        Assert.True(File.Exists(wavPath));

        // 2. Decode it via MediaFoundationAudioDecoder.OpenRedBookPcmStream
        using var decoder = new MediaFoundationAudioDecoder();
        using var redBookStream = decoder.OpenRedBookPcmStream(wavPath);

        // 3. Verify output is 44,100 Hz 16-bit Stereo: 176,400 bytes = 75 Red Book frames of 2,352 bytes
        const int expectedBytes = 176400; // 44,100 frames * 4 bytes/frame
        Assert.Equal(expectedBytes, redBookStream.Length);
        Assert.Equal(75, redBookStream.Length / OpticalConstants.RawSectorSize);
        Assert.Equal(0, redBookStream.Length % OpticalConstants.RawSectorSize);

        // Verify mono to stereo upmixing: Left and Right channels must match exactly
        byte[] decodedBytes = new byte[expectedBytes];
        redBookStream.ReadExactly(decodedBytes);

        int frameCount = expectedBytes / OpticalConstants.CdAudioBytesPerSample;
        for (int i = 0; i < frameCount; i++)
        {
            short left = BinaryPrimitives.ReadInt16LittleEndian(decodedBytes.AsSpan(i * 4, 2));
            short right = BinaryPrimitives.ReadInt16LittleEndian(decodedBytes.AsSpan(i * 4 + 2, 2));
            Assert.Equal(left, right);
        }
    }

    [Fact]
    public void PadToRedBookSectorBoundary_Pads3000BytesTo4704Bytes()
    {
        // 3,000 bytes input PCM stream
        byte[] initialData = new byte[3000];
        for (int i = 0; i < initialData.Length; i++)
        {
            initialData[i] = (byte)((i % 251) + 1); // non-zero pattern
        }

        using var initialStream = new MemoryStream(initialData);
        using var paddedStream = RedBookPcmDspProcessor.PadToRedBookSectorBoundary(initialStream);

        // 4,704 bytes = 2 * 2352
        const int expectedPaddedLength = 4704;
        Assert.Equal(expectedPaddedLength, paddedStream.Length);
        Assert.Equal(0, paddedStream.Length % OpticalConstants.RawSectorSize);
        Assert.Equal(2, paddedStream.Length / OpticalConstants.RawSectorSize);

        byte[] paddedBytes = new byte[expectedPaddedLength];
        paddedStream.ReadExactly(paddedBytes);

        // First 3,000 bytes match original data
        Assert.True(initialData.AsSpan().SequenceEqual(paddedBytes.AsSpan(0, 3000)));

        // Trailing 1,704 bytes must be zero
        for (int i = 3000; i < expectedPaddedLength; i++)
        {
            Assert.Equal(0, paddedBytes[i]);
        }
    }

    [Fact]
    public void PadToRedBookSectorBoundary_ExactSectorAndEmptyHandling()
    {
        // Exactly one sector: 2,352 bytes
        byte[] oneSector = new byte[OpticalConstants.RawSectorSize];
        Array.Fill<byte>(oneSector, 0x55);

        using var streamOne = new MemoryStream(oneSector);
        using var paddedOne = RedBookPcmDspProcessor.PadToRedBookSectorBoundary(streamOne);
        Assert.Equal(OpticalConstants.RawSectorSize, paddedOne.Length);

        // Empty stream: 0 bytes
        using var emptyStream = new MemoryStream();
        using var paddedEmpty = RedBookPcmDspProcessor.PadToRedBookSectorBoundary(emptyStream);
        Assert.Equal(0, paddedEmpty.Length);
    }

    [Fact]
    public void MeasurePeakAmplitude_And_NormalizePcmStream_BoostsHalfAmplitudeToTargetPeak()
    {
        // Generate half-amplitude sine wave with peak = 16384 (16-bit signed stereo, 44,100 Hz, 0.5s)
        int sampleRate = 44100;
        int frameCount = 22050; // 0.5 second
        byte[] pcmData = new byte[frameCount * 4];

        short peakSampleValue = 16384;
        double frequency = 1000.0;

        for (int i = 0; i < frameCount; i++)
        {
            double t = (double)i / sampleRate;
            // Ensure exact peak reaches 16384 at the crest of the wave
            short sample = (short)Math.Round(peakSampleValue * Math.Sin(2.0 * Math.PI * frequency * t));
            BinaryPrimitives.WriteInt16LittleEndian(pcmData.AsSpan(i * 4, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcmData.AsSpan(i * 4 + 2, 2), sample);
        }

        // Explicitly set one sample pair to exactly 16384 to guarantee peak = 16384
        BinaryPrimitives.WriteInt16LittleEndian(pcmData.AsSpan(100 * 4, 2), peakSampleValue);
        BinaryPrimitives.WriteInt16LittleEndian(pcmData.AsSpan(100 * 4 + 2, 2), peakSampleValue);

        using var originalStream = new MemoryStream(pcmData);

        // Measure original peak
        var originalPeak = RedBookPcmDspProcessor.MeasurePeakAmplitude(originalStream);
        Assert.Equal(16384, originalPeak.PeakSample);
        Assert.Equal(0.5, originalPeak.PeakRatio, precision: 4);
        Assert.InRange(originalPeak.PeakDbFs, -6.03, -6.01); // 20 * log10(0.5) = -6.0206 dBFS

        // Normalize with target peak ratio 0.9885 (default ~ -0.1 dBFS)
        using var normalizedStream = RedBookPcmDspProcessor.NormalizePcmStream(originalStream, targetPeakRatio: 0.9885);
        Assert.Equal(originalStream.Length, normalizedStream.Length);

        // Measure normalized peak
        var normalizedPeak = RedBookPcmDspProcessor.MeasurePeakAmplitude(normalizedStream);

        // Target peak = 0.9885 * 32768 = ~32391
        Assert.InRange(normalizedPeak.PeakSample, 32389, 32393);
        Assert.InRange(normalizedPeak.PeakRatio, 0.9880, 0.9890);
        Assert.InRange(normalizedPeak.PeakDbFs, -0.11, -0.09);

        // Verify no clipping (all samples within short.MinValue .. short.MaxValue)
        byte[] normBytes = new byte[normalizedStream.Length];
        normalizedStream.ReadExactly(normBytes);

        for (int i = 0; i < frameCount; i++)
        {
            short sL = BinaryPrimitives.ReadInt16LittleEndian(normBytes.AsSpan(i * 4, 2));
            short sR = BinaryPrimitives.ReadInt16LittleEndian(normBytes.AsSpan(i * 4 + 2, 2));
            Assert.True(sL >= short.MinValue && sL <= short.MaxValue);
            Assert.True(sR >= short.MinValue && sR <= short.MaxValue);
        }
    }

    [Fact]
    public void CreatePreGapSilenceBytes_ReturnsExactZeroBytes()
    {
        // 150 frames = standard 2.0s Red Book lead-in
        byte[] preGap = RedBookPcmDspProcessor.CreatePreGapSilenceBytes(150);
        const int expectedLength = 150 * OpticalConstants.RawSectorSize; // 352,800 bytes
        Assert.Equal(expectedLength, preGap.Length);

        // Verify every single byte is zero (0x00 silence)
        for (int i = 0; i < preGap.Length; i++)
        {
            Assert.Equal(0, preGap[i]);
        }

        // 0 frames -> empty array
        byte[] zeroFrames = RedBookPcmDspProcessor.CreatePreGapSilenceBytes(0);
        Assert.Empty(zeroFrames);

        // 2 frames = 4,704 bytes
        byte[] twoFrames = RedBookPcmDspProcessor.CreatePreGapSilenceBytes(2);
        Assert.Equal(4704, twoFrames.Length);
    }

    [Fact]
    public void ProbeAudioFile_ExtractsWavMetadataCorrectly()
    {
        int sampleRate = 48000;
        short channels = 2;
        short bitsPerSample = 16;
        int frameCount = 48000; // 1 second

        byte[] pcmData = new byte[frameCount * 4];
        string wavPath = Path.Combine(_tempDirectory, "stereo_48k.wav");
        WavFileCodec.WriteWavFile(wavPath, pcmData, sampleRate, channels, bitsPerSample);

        using var decoder = new MediaFoundationAudioDecoder();
        var metadata = decoder.ProbeAudioFile(wavPath);

        Assert.Equal(wavPath, metadata.FilePath);
        Assert.Equal(2, metadata.Channels);
        Assert.Equal(48000, metadata.SampleRate);
        Assert.Equal(16, metadata.BitsPerSample);
        Assert.Equal(1.0, metadata.Duration.TotalSeconds, precision: 2);
        // Red Book bytes for 1.0 second: 44,100 * 4 = 176,400 bytes
        Assert.Equal(176400, metadata.TotalRedBookBytes);
        Assert.Equal(75, metadata.TotalFrames);
    }

    [Fact]
    public void WavFileCodec_Supports8Bit24Bit32BitAndFloat()
    {
        // 8-bit unsigned PCM
        byte[] data8 = [128, 255, 0, 128]; // 4 mono samples
        byte[] header8 = WavFileCodec.CreateWavHeader(data8.Length, 44100, 1, 8);
        using var stream8 = new MemoryStream();
        stream8.Write(header8);
        stream8.Write(data8);
        stream8.Position = 0;
        byte[] decoded8 = WavFileCodec.DecodeToRedBookPcm(stream8);
        Assert.Equal(4 * 4, decoded8.Length); // 4 stereo frames * 4 bytes = 16 bytes

        // 24-bit PCM (1 mono sample: 3 bytes)
        byte[] data24 = [0x00, 0x00, 0x40]; // ~ half scale
        byte[] header24 = WavFileCodec.CreateWavHeader(data24.Length, 44100, 1, 24);
        using var stream24 = new MemoryStream();
        stream24.Write(header24);
        stream24.Write(data24);
        stream24.Position = 0;
        byte[] decoded24 = WavFileCodec.DecodeToRedBookPcm(stream24);
        Assert.Equal(4, decoded24.Length); // 1 stereo frame = 4 bytes

        // 32-bit float
        float[] floatSamples = [0.5f, -0.5f];
        byte[] dataFloat = new byte[floatSamples.Length * 4];
        BinaryPrimitives.WriteSingleLittleEndian(dataFloat.AsSpan(0, 4), floatSamples[0]);
        BinaryPrimitives.WriteSingleLittleEndian(dataFloat.AsSpan(4, 4), floatSamples[1]);

        // Craft a float WAV with format tag 3 (IEEE float)
        byte[] wavFloatBytes = new byte[44 + dataFloat.Length];
        WavFileCodec.WriteWavHeader(wavFloatBytes.AsSpan(0, 44), dataFloat.Length, 44100, 1, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(wavFloatBytes.AsSpan(20, 2), 3); // WAVE_FORMAT_IEEE_FLOAT
        dataFloat.CopyTo(wavFloatBytes.AsSpan(44));

        using var streamFloat = new MemoryStream(wavFloatBytes);
        byte[] decodedFloat = WavFileCodec.DecodeToRedBookPcm(streamFloat);
        Assert.Equal(2 * 4, decodedFloat.Length); // 2 stereo frames
        short decodedS0 = BinaryPrimitives.ReadInt16LittleEndian(decodedFloat.AsSpan(0, 2));
        Assert.InRange(decodedS0, 16380, 16388); // 0.5 * 32767 = ~16384
    }

    [Fact]
    public void MediaFoundation_DirectDecodingOfWavFile_ProducesRedBookPcm()
    {
        // Test the direct Windows Media Foundation Source Reader pipeline
        int sampleRate = 44100;
        short channels = 2;
        short bitsPerSample = 16;
        int frameCount = 44100; // 1 second

        byte[] pcmData = new byte[frameCount * 4];
        for (int i = 0; i < frameCount; i++)
        {
            short s = (short)(i % 1000);
            BinaryPrimitives.WriteInt16LittleEndian(pcmData.AsSpan(i * 4, 2), s);
            BinaryPrimitives.WriteInt16LittleEndian(pcmData.AsSpan(i * 4 + 2, 2), s);
        }

        string wavPath = Path.Combine(_tempDirectory, "mf_direct_test.wav");
        WavFileCodec.WriteWavFile(wavPath, pcmData, sampleRate, channels, bitsPerSample);

        using var decoder = new MediaFoundationAudioDecoder();
        using var decodedStream = decoder.OpenRedBookPcmStreamViaMediaFoundation(wavPath);

        Assert.NotNull(decodedStream);
        Assert.Equal(176400, decodedStream.Length);
        Assert.Equal(75, decodedStream.Length / OpticalConstants.RawSectorSize);
    }

    [Fact]
    public void AudioDrmDetector_M4pExtension_IdentifiedAsFairPlayDrm()
    {
        string fakeM4p = Path.Combine(_tempDirectory, "test_track.m4p");
        File.WriteAllText(fakeM4p, "dummy audio data");

        bool isDrm = AudioDrmDetector.IsDrmProtected(fakeM4p, out string drmDesc);
        Assert.True(isDrm);
        Assert.Contains("FairPlay", drmDesc, StringComparison.OrdinalIgnoreCase);

        using var decoder = new MediaFoundationAudioDecoder();
        var ex = Assert.Throws<DrmProtectedAudioException>(() => decoder.ProbeAudioFile(fakeM4p));
        Assert.Contains("FairPlay", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fakeM4p, ex.FilePath);
    }

    [Fact]
    public void AudioDrmDetector_Mp4ContainerWithDrmsAtom_IdentifiedAsFairPlayDrm()
    {
        string fakeM4a = Path.Combine(_tempDirectory, "renamed_protected.m4a");
        byte[] payload = new byte[1024];
        // Embed 'drms' atom signature
        byte[] drms = "drms"u8.ToArray();
        Array.Copy(drms, 0, payload, 200, 4);
        File.WriteAllBytes(fakeM4a, payload);

        bool isDrm = AudioDrmDetector.IsDrmProtected(fakeM4a, out string drmDesc);
        Assert.True(isDrm);
        Assert.Contains("FairPlay", drmDesc, StringComparison.OrdinalIgnoreCase);

        using var decoder = new MediaFoundationAudioDecoder();
        Assert.Throws<DrmProtectedAudioException>(() => decoder.OpenRedBookPcmStream(fakeM4a));
    }

    [Fact]
    public async Task AudioCdBurnService_DrmProtectedM4pTrack_FailsCleanlyWithoutThrowing()
    {
        string m4pPath = Path.Combine(_tempDirectory, "protected_song.m4p");
        File.WriteAllText(m4pPath, "DRM audio bytes");

        using var decoder = new MediaFoundationAudioDecoder();
        var virtualEngine = new Simulation.VirtualBurnEngine();
        var burnService = new AudioCdBurnService(virtualEngine, decoder);
        var recorder = new Simulation.VirtualDiscRecorder("VDRV-01");
        recorder.InsertMedia(Simulation.VirtualDiscMedium.CreateBlankCdR700());

        var compilation = new AudioCdCompilation();
        compilation.AddTrack(new AudioCdTrackItem(1, m4pPath, "Protected Song", "Artist"));

        var result = await burnService.BurnAsync(recorder, compilation);

        Assert.False(result.Success);
        Assert.Contains("cannot be burned", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FairPlay", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
