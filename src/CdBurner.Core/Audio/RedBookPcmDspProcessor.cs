using System.Buffers.Binary;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Result of a peak amplitude analysis on a 16-bit PCM stream.
/// </summary>
/// <param name="PeakSample">The absolute maximum sample value (0 .. 32768).</param>
/// <param name="PeakRatio">The peak ratio normalized to full scale (0.0 .. 1.0).</param>
/// <param name="PeakDbFs">The peak level in decibels relative to full scale (dBFS).</param>
public readonly record struct PeakAmplitudeResult(
    short PeakSample,
    double PeakRatio,
    double PeakDbFs
);

/// <summary>
/// Digital Signal Processing (DSP) utilities for Red Book CD-DA PCM audio streams
/// (44,100 Hz, 16-bit signed LE, 2 channels, 2,352 bytes/frame).
/// </summary>
public static class RedBookPcmDspProcessor
{
    /// <summary>
    /// Default target peak amplitude ratio (-0.1 dBFS = ~0.988553).
    /// </summary>
    public const double DefaultTargetPeakRatio = 0.9885;

    /// <summary>
    /// Sector size in bytes for a raw Red Book audio frame (2,352 bytes = 1/75 second).
    /// </summary>
    public const int SectorSizeBytes = OpticalConstants.RawSectorSize;

    /// <summary>
    /// Pads trailing bytes of a PCM stream with zero bytes (0x00 silence) so that the total byte
    /// length is an exact integer multiple of 2,352 bytes (one Red Book CD-DA audio sector / frame).
    /// </summary>
    /// <param name="pcmStream">The input PCM audio stream.</param>
    /// <returns>A seekable stream positioned at 0 whose total length is a multiple of 2,352 bytes.</returns>
    public static Stream PadToRedBookSectorBoundary(Stream pcmStream)
    {
        ArgumentNullException.ThrowIfNull(pcmStream);

        long originalPos = pcmStream.CanSeek ? pcmStream.Position : 0;
        if (pcmStream.CanSeek)
        {
            pcmStream.Position = 0;
        }

        byte[] padded;
        if (pcmStream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> segment))
        {
            // Pad straight from the exposed buffer: one copy instead of ToArray() + pad copy (matters for 700 MB albums).
            padded = PadToRedBookSectorBoundary(segment.AsSpan());
        }
        else
        {
            using var tempMs = new MemoryStream();
            pcmStream.CopyTo(tempMs);
            padded = tempMs.TryGetBuffer(out var tempSeg)
                ? PadToRedBookSectorBoundary(tempSeg.AsSpan())
                : PadToRedBookSectorBoundary(tempMs.ToArray());
        }

        if (pcmStream.CanSeek)
        {
            pcmStream.Position = originalPos;
        }

        return new MemoryStream(padded, 0, padded.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>
    /// Pads a byte array with zero bytes (silence) to the next 2,352-byte Red Book sector boundary.
    /// </summary>
    public static byte[] PadToRedBookSectorBoundary(ReadOnlySpan<byte> pcmData)
    {
        if (pcmData.Length == 0)
        {
            return Array.Empty<byte>();
        }

        int remainder = pcmData.Length % SectorSizeBytes;
        if (remainder == 0)
        {
            return pcmData.ToArray();
        }

        int paddingNeeded = SectorSizeBytes - remainder;
        byte[] padded = new byte[pcmData.Length + paddingNeeded];
        pcmData.CopyTo(padded);
        // Trailing bytes are zero-initialized by CLR
        return padded;
    }

    /// <summary>
    /// Scans 16-bit signed little-endian PCM samples and returns the peak sample, peak ratio (0.0..1.0),
    /// and peak decibels relative to full scale (dBFS).
    /// </summary>
    /// <param name="pcmStream">Input stream containing 16-bit signed LE PCM samples.</param>
    public static PeakAmplitudeResult MeasurePeakAmplitude(Stream pcmStream)
    {
        ArgumentNullException.ThrowIfNull(pcmStream);

        long initialPos = pcmStream.CanSeek ? pcmStream.Position : 0;
        if (pcmStream.CanSeek)
        {
            pcmStream.Position = 0;
        }

        int maxAbs = 0;
        byte[] buffer = new byte[8192];
        int bytesRead;
        int carryOver = 0;

        try
        {
            while ((bytesRead = pcmStream.Read(buffer, carryOver, buffer.Length - carryOver)) > 0)
            {
                int totalAvailable = carryOver + bytesRead;
                int sampleCount = totalAvailable / 2;
                ReadOnlySpan<byte> span = buffer.AsSpan(0, sampleCount * 2);

                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2, 2));
                    int abs = Math.Abs((int)sample);
                    if (abs > maxAbs)
                    {
                        maxAbs = abs;
                    }
                }
                carryOver = totalAvailable % 2;
                if (carryOver == 1)
                {
                    buffer[0] = buffer[totalAvailable - 1];
                }
                for (int i = 0; i < 0; i++)
                {
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2, 2));
                    int abs = Math.Abs((int)sample);
                    if (abs > maxAbs)
                    {
                        maxAbs = abs;
                    }
                }
            }
        }
        finally
        {
            if (pcmStream.CanSeek)
            {
                pcmStream.Position = initialPos;
            }
        }

        short peakSample = (short)Math.Min(maxAbs, 32767);
        double peakRatio = Math.Min(1.0, (double)maxAbs / 32768.0);
        double peakDbFs = peakRatio > 0.0 ? 20.0 * Math.Log10(peakRatio) : double.NegativeInfinity;

        return new PeakAmplitudeResult(peakSample, peakRatio, peakDbFs);
    }

    /// <summary>
    /// Scans 16-bit signed little-endian PCM samples in a span and returns the peak amplitude metrics.
    /// </summary>
    public static PeakAmplitudeResult MeasurePeakAmplitude(ReadOnlySpan<byte> pcmData)
    {
        int maxAbs = 0;
        int sampleCount = pcmData.Length / 2;

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(pcmData.Slice(i * 2, 2));
            int abs = Math.Abs((int)sample);
            if (abs > maxAbs)
            {
                maxAbs = abs;
            }
        }

        short peakSample = (short)Math.Min(maxAbs, 32767);
        double peakRatio = Math.Min(1.0, (double)maxAbs / 32768.0);
        double peakDbFs = peakRatio > 0.0 ? 20.0 * Math.Log10(peakRatio) : double.NegativeInfinity;

        return new PeakAmplitudeResult(peakSample, peakRatio, peakDbFs);
    }

    /// <summary>
    /// Scales 16-bit PCM samples to a specified target peak ratio (default 0.9885 = -0.1 dBFS)
    /// with saturation clamping within [short.MinValue .. short.MaxValue].
    /// </summary>
    /// <param name="pcmStream">The input PCM audio stream.</param>
    /// <param name="targetPeakRatio">Target peak ratio relative to full scale (default 0.9885).</param>
    /// <returns>A new memory stream containing normalized 16-bit PCM samples positioned at 0.</returns>
    public static Stream NormalizePcmStream(Stream pcmStream, double targetPeakRatio = DefaultTargetPeakRatio)
    {
        ArgumentNullException.ThrowIfNull(pcmStream);
        if (!double.IsFinite(targetPeakRatio) || targetPeakRatio <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPeakRatio), "Target peak ratio must be a finite positive value.");
        }

        long initialPos = pcmStream.CanSeek ? pcmStream.Position : 0;
        if (pcmStream.CanSeek)
        {
            pcmStream.Position = 0;
        }

        byte[] allBytes;
        if (pcmStream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> segment))
        {
            allBytes = segment.ToArray();
        }
        else
        {
            using var tempMs = new MemoryStream();
            pcmStream.CopyTo(tempMs);
            allBytes = tempMs.ToArray();
        }

        if (pcmStream.CanSeek)
        {
            pcmStream.Position = initialPos;
        }

        byte[] normalizedBytes = NormalizePcm(allBytes, targetPeakRatio);
        return new MemoryStream(normalizedBytes, 0, normalizedBytes.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>
    /// Normalizes 16-bit signed PCM audio bytes to target peak ratio.
    /// </summary>
    public static byte[] NormalizePcm(ReadOnlySpan<byte> pcmData, double targetPeakRatio = DefaultTargetPeakRatio)
    {
        if (!double.IsFinite(targetPeakRatio) || targetPeakRatio <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPeakRatio), "Target peak ratio must be a finite positive value.");
        }

        int sampleCount = pcmData.Length / 2;
        if (sampleCount == 0)
        {
            return Array.Empty<byte>();
        }

        var peakInfo = MeasurePeakAmplitude(pcmData);
        if (peakInfo.PeakSample == 0 || peakInfo.PeakRatio <= 0.0)
        {
            // Silence - no scaling possible or needed
            return pcmData.ToArray();
        }

        double targetPeakSample = targetPeakRatio * 32768.0;
        double scaleFactor = targetPeakSample / peakInfo.PeakSample;

        byte[] output = new byte[pcmData.Length];
        Span<byte> dest = output;

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(pcmData.Slice(i * 2, 2));
            double scaled = sample * scaleFactor;
            short clamped = (short)Math.Clamp(Math.Round(scaled), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(dest.Slice(i * 2, 2), clamped);
        }

        // Copy any trailing odd byte if present
        if (pcmData.Length % 2 != 0)
        {
            output[^1] = pcmData[^1];
        }

        return output;
    }

    /// <summary>
    /// Generates pre-gap silence bytes for Red Book CD-DA (frameCount * 2,352 zero bytes).
    /// </summary>
    /// <param name="frameCount">Number of Red Book audio frames (e.g. 150 frames = 2.0 seconds).</param>
    /// <returns>Zero-filled byte array.</returns>
    public static byte[] CreatePreGapSilenceBytes(int frameCount)
    {
        if (frameCount <= 0)
        {
            return Array.Empty<byte>();
        }

        return new byte[frameCount * SectorSizeBytes];
    }

    /// <summary>
    /// Levels perceived track loudness to a target RMS level in dBFS (default -14.0 dBFS, comparable to ReplayGain / EBU R128)
    /// while preventing digital clipping via a -0.1 dBFS peak ceiling.
    /// </summary>
    public static byte[] NormalizeRmsLoudness(ReadOnlySpan<byte> pcmData, double targetRmsDbFs = -14.0)
    {
        int sampleCount = pcmData.Length / 2;
        if (sampleCount == 0) return Array.Empty<byte>();

        double sumSquares = 0.0;
        int maxAbs = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcmData.Slice(i * 2, 2));
            int abs = Math.Abs((int)s);
            if (abs > maxAbs) maxAbs = abs;
            double norm = s / 32768.0;
            sumSquares += norm * norm;
        }

        if (maxAbs == 0 || sumSquares <= 1e-12)
        {
            return pcmData.ToArray();
        }

        double currentRms = Math.Sqrt(sumSquares / sampleCount);
        double targetRms = Math.Pow(10.0, targetRmsDbFs / 20.0);
        double desiredGain = targetRms / currentRms;

        // Cap gain so peak never exceeds DefaultTargetPeakRatio (-0.1 dBFS) unless soft-limited
        double maxLinearGainWithoutClip = (DefaultTargetPeakRatio * 32768.0) / maxAbs;
        double appliedGain = Math.Min(desiredGain, maxLinearGainWithoutClip * 1.25);

        byte[] output = new byte[pcmData.Length];
        Span<byte> dest = output;

        for (int i = 0; i < sampleCount; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(pcmData.Slice(i * 2, 2));
            double x = (s / 32768.0) * appliedGain;
            if (Math.Abs(x) > 0.90)
            {
                double sign = Math.Sign(x);
                double excess = Math.Abs(x) - 0.90;
                x = sign * (0.90 + 0.098 * Math.Tanh(excess / 0.098));
            }
            short clamped = (short)Math.Clamp(Math.Round(x * 32767.0), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(dest.Slice(i * 2, 2), clamped);
        }

        return output;
    }

    /// <summary>
    /// Applies smooth cosine-curve fade-in and/or fade-out envelopes to a Red Book 16-bit Stereo PCM buffer.
    /// </summary>
    public static byte[] ApplyFadeEnvelope(ReadOnlySpan<byte> pcmData, double fadeInSeconds, double fadeOutSeconds)
    {
        int stereoFrames = pcmData.Length / 4;
        if (stereoFrames == 0 || (fadeInSeconds <= 0 && fadeOutSeconds <= 0))
        {
            return pcmData.ToArray();
        }

        byte[] output = pcmData.ToArray();
        Span<byte> dest = output;

        int fadeInFrames = Math.Clamp((int)Math.Round(fadeInSeconds * OpticalConstants.CdAudioSampleRate), 0, stereoFrames);
        int fadeOutFrames = Math.Clamp((int)Math.Round(fadeOutSeconds * OpticalConstants.CdAudioSampleRate), 0, stereoFrames);

        for (int f = 0; f < fadeInFrames; f++)
        {
            double gain = 0.5 * (1.0 - Math.Cos(Math.PI * f / fadeInFrames));
            ScaleStereoFrame(dest.Slice(f * 4, 4), gain);
        }

        for (int f = 0; f < fadeOutFrames; f++)
        {
            int frameIdx = stereoFrames - 1 - f;
            if (frameIdx < 0) break;
            double gain = 0.5 * (1.0 - Math.Cos(Math.PI * f / fadeOutFrames));
            ScaleStereoFrame(dest.Slice(frameIdx * 4, 4), gain);
        }

        return output;
    }

    /// <summary>
    /// Overlaps the end of <paramref name="trackAPcm"/> with the beginning of <paramref name="trackBPcm"/> using an equal-power
    /// crossfade of about <paramref name="crossfadeSeconds"/>.
    /// <para>
    /// The transition is rendered once, into the tail of track A, and the overlapped head is removed from track B. Played
    /// back to back (gapless DAO), the listener hears A fade out while B fades in, then B continues from the point where the
    /// fade ended — nothing is heard twice. The overlap is rounded down to whole 588-sample sectors so both tracks stay
    /// sector-aligned; track A keeps its length and track B becomes shorter by the overlap.
    /// </para>
    /// </summary>
    public static (byte[] TrackAFaded, byte[] TrackBFaded) ApplyCrossfadeBetweenTracks(
        ReadOnlySpan<byte> trackAPcm,
        ReadOnlySpan<byte> trackBPcm,
        double crossfadeSeconds)
    {
        const int samplesPerSector = SectorSizeBytes / 4; // 588 stereo samples per 1/75 s frame

        int framesA = trackAPcm.Length / 4;
        int framesB = trackBPcm.Length / 4;
        int requested = crossfadeSeconds > 0 && double.IsFinite(crossfadeSeconds)
            ? (int)Math.Min(int.MaxValue, Math.Round(crossfadeSeconds * OpticalConstants.CdAudioSampleRate))
            : 0;

        // Never consume more than half of either track, and keep whole sectors.
        int overlapFrames = Math.Min(requested, Math.Min(framesA / 2, framesB / 2));
        overlapFrames -= overlapFrames % samplesPerSector;

        if (overlapFrames <= 0)
        {
            return (trackAPcm.ToArray(), trackBPcm.ToArray());
        }

        byte[] outA = trackAPcm.ToArray();
        byte[] outB = trackBPcm.Slice(overlapFrames * 4).ToArray();
        Span<byte> spanA = outA;

        int startA = framesA - overlapFrames;
        for (int i = 0; i < overlapFrames; i++)
        {
            double t = (i + 0.5) / overlapFrames;
            double gainA = Math.Cos(t * Math.PI * 0.5);
            double gainB = Math.Sin(t * Math.PI * 0.5);

            int offsetA = (startA + i) * 4;
            int offsetB = i * 4;

            short aL = BinaryPrimitives.ReadInt16LittleEndian(trackAPcm.Slice(offsetA, 2));
            short aR = BinaryPrimitives.ReadInt16LittleEndian(trackAPcm.Slice(offsetA + 2, 2));
            short bL = BinaryPrimitives.ReadInt16LittleEndian(trackBPcm.Slice(offsetB, 2));
            short bR = BinaryPrimitives.ReadInt16LittleEndian(trackBPcm.Slice(offsetB + 2, 2));

            short mixL = (short)Math.Clamp(Math.Round((aL * gainA) + (bL * gainB)), short.MinValue, short.MaxValue);
            short mixR = (short)Math.Clamp(Math.Round((aR * gainA) + (bR * gainB)), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(spanA.Slice(offsetA, 2), mixL);
            BinaryPrimitives.WriteInt16LittleEndian(spanA.Slice(offsetA + 2, 2), mixR);
        }

        return (outA, outB);
    }

    private static void ScaleStereoFrame(Span<byte> frame4, double gain)
    {
        short l = BinaryPrimitives.ReadInt16LittleEndian(frame4.Slice(0, 2));
        short r = BinaryPrimitives.ReadInt16LittleEndian(frame4.Slice(2, 2));
        short nl = (short)Math.Clamp(Math.Round(l * gain), short.MinValue, short.MaxValue);
        short nr = (short)Math.Clamp(Math.Round(r * gain), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(frame4.Slice(0, 2), nl);
        BinaryPrimitives.WriteInt16LittleEndian(frame4.Slice(2, 2), nr);
    }
}
