using System.Buffers.Binary;
using System.Globalization;

namespace CdBurner.Core.Audio;

/// <summary>
/// Result of per-track ReplayGain loudness analysis.
/// </summary>
/// <param name="GainDb">Gain in dB that brings the track to the ReplayGain 2.0 reference of -18 LUFS
/// (clamped to [-51.0, +51.0]).</param>
/// <param name="PeakAmplitude">Sample peak as a linear amplitude in [0.0, 1.0].</param>
public sealed record ReplayGainResult(double GainDb, double PeakAmplitude)
{
    /// <summary>
    /// Mean-square power of every 400 ms gating block (BS.1770-4), kept so album gain can gate the blocks of all
    /// tracks together, as ReplayGain 2.0 specifies. Empty for results built from stored tag values.
    /// </summary>
    public IReadOnlyList<double> BlockPowers { get; init; } = Array.Empty<double>();

    /// <summary>Integrated loudness in LUFS, or <see cref="double.NegativeInfinity"/> for silence.</summary>
    public double IntegratedLoudnessLufs { get; init; } = double.NegativeInfinity;
}

/// <summary>
/// Result of whole-album ReplayGain loudness analysis.
/// </summary>
public sealed record AlbumReplayGainResult(
    double AlbumGainDb,
    double AlbumPeakAmplitude,
    IReadOnlyList<ReplayGainResult> TrackResults);

/// <summary>
/// ReplayGain 2.0 for 44.1 kHz 16-bit stereo (Red Book) PCM: integrated loudness per ITU-R BS.1770-4 (K-weighting,
/// 400 ms blocks with 75 % overlap, -70 LUFS absolute gate, -10 LU relative gate), gain = -18 LUFS - loudness,
/// and sample peak. Album gain gates the blocks of all tracks together rather than averaging track gains.
/// </summary>
public static class ReplayGainCalculator
{
    /// <summary>ReplayGain 2.0 reference loudness.</summary>
    public const double ReferenceLoudnessLufs = -18.0;
    public const double MinGainDb = -51.0;
    public const double MaxGainDb = 51.0;

    private const int SampleRate = 44_100;
    private const int HopFrames = SampleRate / 10;         // 100 ms
    private const int BlockHops = 4;                        // 400 ms block = 4 hops (75 % overlap)
    private const int BlockFrames = HopFrames * BlockHops;
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateLu = -10.0;

    // K-weighting for fs = 44.1 kHz, derived from the BS.1770 analog prototype the same way libebur128 does
    // (the coefficients printed in BS.1770 are for 48 kHz only).
    private static readonly (double B0, double B1, double B2, double A1, double A2) Shelf = ComputeShelf(SampleRate);
    private static readonly (double B0, double B1, double B2, double A1, double A2) HighPass = ComputeHighPass(SampleRate);

    /// <summary>
    /// Track ReplayGain from 44.1 kHz 16-bit stereo PCM bytes, or a RIFF WAVE file image.
    /// </summary>
    public static ReplayGainResult CalculateTrackReplayGain(ReadOnlySpan<byte> redBookPcmBytes)
    {
        var meter = new Meter();
        meter.Add(SkipWavHeaderIfPresent(redBookPcmBytes));
        return meter.Finish();
    }

    /// <summary>
    /// Track ReplayGain from a stream of 44.1 kHz 16-bit stereo PCM, or a RIFF WAVE stream.
    /// </summary>
    public static ReplayGainResult CalculateTrackReplayGain(Stream redBookPcmStream)
    {
        ArgumentNullException.ThrowIfNull(redBookPcmStream);

        var meter = new Meter();
        byte[] buffer = new byte[64 * 1024];
        int carry = 0;
        bool headerChecked = false;

        while (true)
        {
            int read = redBookPcmStream.Read(buffer, carry, buffer.Length - carry);
            if (read <= 0) break;

            ReadOnlySpan<byte> span = buffer.AsSpan(0, carry + read);
            if (!headerChecked)
            {
                headerChecked = true;
                span = SkipWavHeaderIfPresent(span);
            }

            int whole = span.Length - (span.Length % 4);
            meter.Add(span[..whole]);

            // Keep a partial stereo frame for the next read.
            carry = span.Length - whole;
            span[whole..].CopyTo(buffer);
        }

        return meter.Finish();
    }

    /// <summary>
    /// Album ReplayGain. With block data (results from <see cref="CalculateTrackReplayGain(Stream)"/>), all blocks
    /// are gated together per BS.1770-4. Results without block data (built from stored tags) fall back to a
    /// duration-weighted power average of the track loudness values.
    /// </summary>
    public static AlbumReplayGainResult CalculateAlbumReplayGain(
        IReadOnlyList<ReplayGainResult> tracks,
        IReadOnlyList<long>? trackSampleCounts = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            return new AlbumReplayGainResult(0.0, 0.0, Array.Empty<ReplayGainResult>());
        }

        double albumPeak = tracks.Max(t => t.PeakAmplitude);

        double albumGain;
        if (tracks.All(t => t.BlockPowers.Count > 0 || t.PeakAmplitude == 0))
        {
            var allBlocks = tracks.SelectMany(t => t.BlockPowers).ToList();
            double loudness = GatedLoudness(allBlocks);
            albumGain = double.IsNegativeInfinity(loudness) ? 0.0 : ClampGain(ReferenceLoudnessLufs - loudness);
        }
        else
        {
            double weightedPower = 0.0;
            double totalWeight = 0.0;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].PeakAmplitude <= 0) continue;
                double weight = trackSampleCounts != null && i < trackSampleCounts.Count && trackSampleCounts[i] > 0
                    ? trackSampleCounts[i]
                    : 1.0;
                double trackLoudness = ReferenceLoudnessLufs - tracks[i].GainDb;
                weightedPower += Math.Pow(10.0, trackLoudness / 10.0) * weight;
                totalWeight += weight;
            }

            albumGain = totalWeight > 0 && weightedPower > 0
                ? ClampGain(ReferenceLoudnessLufs - 10.0 * Math.Log10(weightedPower / totalWeight))
                : 0.0;
        }

        return new AlbumReplayGainResult(albumGain, albumPeak, tracks);
    }

    /// <summary>
    /// Formats a gain for REPLAYGAIN_*_GAIN tags, e.g. "+0.00 dB", "-2.45 dB". Clamped to [-51.0, +51.0].
    /// </summary>
    public static string FormatGainDb(double gainDb)
    {
        double clamped = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        return string.Create(CultureInfo.InvariantCulture, $"{clamped:+0.00;-0.00;+0.00} dB");
    }

    /// <summary>
    /// Formats a peak for REPLAYGAIN_*_PEAK tags, e.g. "0.987654".
    /// </summary>
    public static string FormatPeak(double peak)
    {
        double clamped = Math.Max(0.0, peak);
        return clamped.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// BS.1770-4 gated integrated loudness of a list of 400 ms block powers (mean square summed over channels).
    /// </summary>
    internal static double GatedLoudness(IReadOnlyList<double> blockPowers)
    {
        double absThreshold = LoudnessToPower(AbsoluteGateLufs);
        double sum = 0;
        int count = 0;
        foreach (double z in blockPowers)
        {
            if (z > absThreshold)
            {
                sum += z;
                count++;
            }
        }

        if (count == 0) return double.NegativeInfinity;

        double relThreshold = (sum / count) * Math.Pow(10.0, RelativeGateLu / 10.0);
        double gatedSum = 0;
        int gatedCount = 0;
        foreach (double z in blockPowers)
        {
            if (z > absThreshold && z > relThreshold)
            {
                gatedSum += z;
                gatedCount++;
            }
        }

        return gatedCount == 0 ? double.NegativeInfinity : PowerToLoudness(gatedSum / gatedCount);
    }

    private static double PowerToLoudness(double power) => -0.691 + 10.0 * Math.Log10(power);

    private static double LoudnessToPower(double lufs) => Math.Pow(10.0, (lufs + 0.691) / 10.0);

    private static double ClampGain(double gain) => Math.Clamp(gain, MinGainDb, MaxGainDb);

    private static (double, double, double, double, double) ComputeShelf(double fs)
    {
        const double f0 = 1681.974450955533;
        const double g = 3.999843853973347;
        const double q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / fs);
        double vh = Math.Pow(10.0, g / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / q + k * k;
        return (
            (vh + vb * k / q + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / q + k * k) / a0);
    }

    private static (double, double, double, double, double) ComputeHighPass(double fs)
    {
        const double f0 = 38.13547087602444;
        const double q = 0.5003270373238773;
        double k = Math.Tan(Math.PI * f0 / fs);
        double a0 = 1.0 + k / q + k * k;
        return (1.0, -2.0, 1.0, 2.0 * (k * k - 1.0) / a0, (1.0 - k / q + k * k) / a0);
    }

    private static ReadOnlySpan<byte> SkipWavHeaderIfPresent(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E')
        {
            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                bool isData = data[pos] == (byte)'d' && data[pos + 1] == (byte)'a' &&
                              data[pos + 2] == (byte)'t' && data[pos + 3] == (byte)'a';
                int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 4, 4));
                pos += 8;
                if (isData)
                {
                    int take = chunkSize < 0 ? data.Length - pos : Math.Min(chunkSize, data.Length - pos);
                    return data.Slice(pos, take);
                }

                if (chunkSize < 0 || chunkSize > data.Length - pos) break;
                pos += chunkSize + (chunkSize & 1); // RIFF chunks are word-aligned
            }
        }

        return data;
    }

    /// <summary>Streaming K-weighted block meter for 44.1 kHz stereo.</summary>
    private sealed class Meter
    {
        private Biquad _shelfL, _shelfR, _hpL, _hpR;
        private readonly List<double> _hopSums = new();
        private double _hopSum;
        private int _hopCount;
        private int _maxAbs;

        public void Add(ReadOnlySpan<byte> pcm)
        {
            int frames = pcm.Length / 4;
            for (int i = 0; i < frames; i++)
            {
                short l = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 4, 2));
                short r = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 4 + 2, 2));

                int absL = Math.Abs((int)l);
                int absR = Math.Abs((int)r);
                if (absL > _maxAbs) _maxAbs = absL;
                if (absR > _maxAbs) _maxAbs = absR;

                double yl = _hpL.Process(_shelfL.Process(l / 32768.0, Shelf), HighPass);
                double yr = _hpR.Process(_shelfR.Process(r / 32768.0, Shelf), HighPass);
                _hopSum += yl * yl + yr * yr; // channel weights are 1.0 for left and right

                if (++_hopCount == HopFrames)
                {
                    _hopSums.Add(_hopSum);
                    _hopSum = 0;
                    _hopCount = 0;
                }
            }
        }

        public ReplayGainResult Finish()
        {
            // Blocks are 400 ms windows stepped by 100 ms; a trailing partial block is not measured (BS.1770-4).
            var blocks = new List<double>(Math.Max(0, _hopSums.Count - BlockHops + 1));
            for (int start = 0; start + BlockHops <= _hopSums.Count; start++)
            {
                double s = 0;
                for (int h = 0; h < BlockHops; h++) s += _hopSums[start + h];
                blocks.Add(s / BlockFrames);
            }

            double peak = Math.Min(1.0, _maxAbs / 32768.0);
            double loudness = GatedLoudness(blocks);
            double gain = double.IsNegativeInfinity(loudness) ? 0.0 : ClampGain(ReferenceLoudnessLufs - loudness);
            return new ReplayGainResult(gain, peak) { BlockPowers = blocks, IntegratedLoudnessLufs = loudness };
        }
    }

    private struct Biquad
    {
        private double _z1, _z2;

        public double Process(double x, (double B0, double B1, double B2, double A1, double A2) c)
        {
            // Transposed direct form II.
            double y = c.B0 * x + _z1;
            _z1 = c.B1 * x - c.A1 * y + _z2;
            _z2 = c.B2 * x - c.A2 * y;
            return y;
        }
    }
}
