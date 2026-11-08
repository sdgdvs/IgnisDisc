using System.Buffers.Binary;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Streaming converter from arbitrary integer PCM (any sample rate, 4..32-bit, 1..8 channels)
/// into canonical Red Book CD-DA PCM (44,100 Hz, 16-bit signed LE, 2 channels).
/// <para>
/// Audio is consumed block-by-block, so peak memory is the size of the Red Book output plus a small
/// history window, instead of float copies of the whole decoded source. Multichannel input is
/// downmixed (ITU-R BS.775 style, LFE dropped, normalized so it can never clip), and rate conversion
/// uses a band-limited windowed-sinc polyphase filter so ultrasonic content in 88.2/96/192 kHz
/// sources does not alias back into the audible band.
/// </para>
/// </summary>
public sealed class RedBookPcmConverter
{
    /// <summary>
    /// Upper bound on Red Book output length (about 75 minutes beyond a 99-minute disc).
    /// Anything larger cannot be burned and would not fit in a single managed array.
    /// </summary>
    public const long MaxOutputFrames = 400_000_000;

    private const int CdRate = OpticalConstants.CdAudioSampleRate;

    private readonly int _sourceRate;
    private readonly PolyphaseResampler? _resampler;
    private byte[] _output;
    private long _outputBytes;
    private long _sourceFramesWritten;
    private float[] _scratchLeft = Array.Empty<float>();
    private float[] _scratchRight = Array.Empty<float>();
    private bool _finished;

    /// <summary>
    /// Creates a converter for a source stream at <paramref name="sourceSampleRate"/> Hz.
    /// </summary>
    /// <param name="sourceSampleRate">Source sample rate in Hz (1..655,350).</param>
    /// <param name="expectedSourceFrames">Optional total source frame count, used to pre-size the output exactly (0 = unknown).</param>
    public RedBookPcmConverter(int sourceSampleRate, long expectedSourceFrames = 0)
    {
        if (sourceSampleRate is < 1 or > 655_350)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate), $"Unsupported source sample rate {sourceSampleRate} Hz.");
        }

        _sourceRate = sourceSampleRate;
        _resampler = sourceSampleRate == CdRate ? null : new PolyphaseResampler(sourceSampleRate, CdRate);

        long expectedOutFrames = expectedSourceFrames > 0 ? ExpectedOutputFrames(expectedSourceFrames, sourceSampleRate) : 0;
        int initialCapacity = expectedOutFrames > 0 && expectedOutFrames <= MaxOutputFrames
            ? (int)Math.Min(Array.MaxLength, expectedOutFrames * OpticalConstants.CdAudioBytesPerSample)
            : 1 << 20;
        _output = new byte[initialCapacity];
    }

    /// <summary>
    /// Number of Red Book output frames (44.1 kHz stereo samples) produced so far.
    /// </summary>
    public long OutputFrames => _outputBytes / OpticalConstants.CdAudioBytesPerSample;

    /// <summary>
    /// Number of source frames consumed so far (including inserted silence).
    /// </summary>
    public long SourceFramesWritten => _sourceFramesWritten;

    /// <summary>
    /// Number of Red Book output frames a source of <paramref name="sourceFrames"/> frames at
    /// <paramref name="sourceRate"/> Hz converts to.
    /// </summary>
    public static long ExpectedOutputFrames(long sourceFrames, int sourceRate) =>
        sourceRate == CdRate
            ? sourceFrames
            : (long)Math.Round((double)sourceFrames * CdRate / sourceRate);

    /// <summary>
    /// Appends one block of decoded integer samples. <paramref name="channelSamples"/>[c][i] holds sample i of channel c,
    /// as a signed value with <paramref name="bitsPerSample"/> significant bits.
    /// </summary>
    public void WriteBlock(int[][] channelSamples, int channels, int bitsPerSample, int count)
    {
        ArgumentNullException.ThrowIfNull(channelSamples);
        ThrowIfFinished();
        if (count <= 0) return;
        if (channels < 1 || channelSamples.Length < channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (bitsPerSample is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        }

        _sourceFramesWritten += count;

        // Exact integer fast path: 44.1 kHz mono/stereo needs no filtering, only re-quantization.
        if (_resampler == null && channels <= 2)
        {
            int[] l = channelSamples[0];
            int[] r = channels > 1 ? channelSamples[1] : l;
            EnsureOutputCapacity(count);
            Span<byte> dest = _output.AsSpan((int)_outputBytes, count * OpticalConstants.CdAudioBytesPerSample);
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(dest.Slice(i * 4, 2), RequantizeTo16(l[i], bitsPerSample));
                BinaryPrimitives.WriteInt16LittleEndian(dest.Slice(i * 4 + 2, 2), RequantizeTo16(r[i], bitsPerSample));
            }

            _outputBytes += (long)count * OpticalConstants.CdAudioBytesPerSample;
            return;
        }

        EnsureScratch(count);
        DownmixToStereoFloat(channelSamples, channels, bitsPerSample, count, _scratchLeft, _scratchRight);
        WriteFloatStereo(_scratchLeft, _scratchRight, count);
    }

    /// <summary>
    /// Appends <paramref name="frames"/> source frames of digital silence (used to conceal corrupt or missing frames
    /// without shifting the timing of later audio).
    /// </summary>
    public void WriteSilence(long frames)
    {
        ThrowIfFinished();
        while (frames > 0)
        {
            int chunk = (int)Math.Min(frames, 65536);
            _sourceFramesWritten += chunk;
            if (_resampler == null)
            {
                EnsureOutputCapacity(chunk);
                _output.AsSpan((int)_outputBytes, chunk * OpticalConstants.CdAudioBytesPerSample).Clear();
                _outputBytes += (long)chunk * OpticalConstants.CdAudioBytesPerSample;
            }
            else
            {
                EnsureScratch(chunk);
                Array.Clear(_scratchLeft, 0, chunk);
                Array.Clear(_scratchRight, 0, chunk);
                WriteFloatStereo(_scratchLeft, _scratchRight, chunk);
            }

            frames -= chunk;
        }
    }

    /// <summary>
    /// Flushes the resampler tail and returns the complete Red Book PCM buffer. The returned array is not copied
    /// when the output was pre-sized exactly.
    /// </summary>
    public byte[] Finish()
    {
        if (!_finished)
        {
            _finished = true;
            if (_resampler != null)
            {
                long target = ExpectedOutputFrames(_sourceFramesWritten, _sourceRate);
                _resampler.Flush(target, WriteOutputSample);
            }
        }

        if (_outputBytes == _output.Length)
        {
            return _output;
        }

        byte[] exact = new byte[_outputBytes];
        Buffer.BlockCopy(_output, 0, exact, 0, (int)_outputBytes);
        _output = exact;
        return exact;
    }

    private void WriteFloatStereo(float[] left, float[] right, int count)
    {
        if (_resampler == null)
        {
            EnsureOutputCapacity(count);
            for (int i = 0; i < count; i++)
            {
                WriteOutputSample(left[i], right[i]);
            }
            return;
        }

        _resampler.Push(left, right, count, WriteOutputSample);
    }

    private void WriteOutputSample(float left, float right)
    {
        EnsureOutputCapacity(1);
        Span<byte> dest = _output.AsSpan((int)_outputBytes, 4);
        BinaryPrimitives.WriteInt16LittleEndian(dest, FloatTo16(left));
        BinaryPrimitives.WriteInt16LittleEndian(dest.Slice(2), FloatTo16(right));
        _outputBytes += 4;
    }

    private void EnsureOutputCapacity(int additionalFrames)
    {
        long needed = _outputBytes + ((long)additionalFrames * OpticalConstants.CdAudioBytesPerSample);
        if (needed <= _output.Length) return;

        if (needed > MaxOutputFrames * OpticalConstants.CdAudioBytesPerSample || needed > Array.MaxLength)
        {
            throw new InvalidDataException(
                "Decoded audio is longer than any Red Book Audio CD can hold (or exceeds the 2 GB in-memory limit).");
        }

        long grown = Math.Max(needed, (long)_output.Length + (_output.Length / 2));
        grown = Math.Min(grown, Array.MaxLength);
        Array.Resize(ref _output, (int)grown);
    }

    private void EnsureScratch(int count)
    {
        if (_scratchLeft.Length < count)
        {
            _scratchLeft = new float[count];
            _scratchRight = new float[count];
        }
    }

    private void ThrowIfFinished()
    {
        if (_finished) throw new InvalidOperationException("RedBookPcmConverter.Finish() has already been called.");
    }

    /// <summary>
    /// Re-quantizes a signed integer sample of <paramref name="bits"/> significant bits to 16 bits (round-half-up, clamped).
    /// </summary>
    internal static short RequantizeTo16(int sample, int bits)
    {
        if (bits == 16) return (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        if (bits < 16) return (short)Math.Clamp(sample << (16 - bits), short.MinValue, short.MaxValue);

        int shift = bits - 16;
        long rounded = ((long)sample + (1L << (shift - 1))) >> shift;
        return (short)Math.Clamp(rounded, short.MinValue, short.MaxValue);
    }

    private static short FloatTo16(float x)
    {
        double scaled = Math.Round(x * 32768.0);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Downmixes RFC 9639 channel layouts to normalized stereo floats in [-1, 1].
    /// Channel order: 3 = L R C; 4 = FL FR BL BR; 5 = FL FR C BL BR; 6 = FL FR C LFE BL BR;
    /// 7 = FL FR C LFE BC SL SR; 8 = FL FR C LFE BL BR SL SR. LFE is dropped (standard practice for stereo folds).
    /// </summary>
    internal static void DownmixToStereoFloat(int[][] ch, int channels, int bits, int count, float[] left, float[] right)
    {
        double norm = 1.0 / (1L << (bits - 1));
        const double k = 0.70710678118654752; // -3 dB

        if (channels == 1)
        {
            int[] m = ch[0];
            for (int i = 0; i < count; i++)
            {
                float v = (float)(m[i] * norm);
                left[i] = v;
                right[i] = v;
            }
            return;
        }

        if (channels == 2)
        {
            int[] l = ch[0], r = ch[1];
            for (int i = 0; i < count; i++)
            {
                left[i] = (float)(l[i] * norm);
                right[i] = (float)(r[i] * norm);
            }
            return;
        }

        // Per-channel (left gain, right gain) for each source channel.
        (double L, double R)[] gains = channels switch
        {
            3 => new[] { (1.0, 0.0), (0.0, 1.0), (k, k) },
            4 => new[] { (1.0, 0.0), (0.0, 1.0), (k, 0.0), (0.0, k) },
            5 => new[] { (1.0, 0.0), (0.0, 1.0), (k, k), (k, 0.0), (0.0, k) },
            6 => new[] { (1.0, 0.0), (0.0, 1.0), (k, k), (0.0, 0.0), (k, 0.0), (0.0, k) },
            7 => new[] { (1.0, 0.0), (0.0, 1.0), (k, k), (0.0, 0.0), (0.5, 0.5), (k, 0.0), (0.0, k) },
            _ => new[] { (1.0, 0.0), (0.0, 1.0), (k, k), (0.0, 0.0), (k, 0.0), (0.0, k), (k, 0.0), (0.0, k) }
        };

        int used = Math.Min(channels, gains.Length);
        double sumL = 0;
        for (int c = 0; c < used; c++) sumL += gains[c].L;
        double scale = norm / sumL; // guarantees |output| <= 1 (no clipping) for any input

        for (int i = 0; i < count; i++)
        {
            double accL = 0, accR = 0;
            for (int c = 0; c < used; c++)
            {
                int s = ch[c][i];
                accL += s * gains[c].L;
                accR += s * gains[c].R;
            }
            left[i] = (float)(accL * scale);
            right[i] = (float)(accR * scale);
        }
    }

    /// <summary>
    /// Streaming band-limited polyphase resampler (Kaiser-windowed sinc). Output sample n is centred on input
    /// position n * inRate / outRate, so the conversion is exact-ratio with no drift over long albums.
    /// </summary>
    private sealed class PolyphaseResampler
    {
        private const int ZeroCrossings = 12;
        private const int MaxPhases = 4096;
        private const double KaiserBeta = 8.6;

        private readonly long _inRate;
        private readonly long _outRate;
        private readonly int _half;
        private readonly int _taps;
        private readonly int _phases;
        private readonly long _phaseDenominator;
        private readonly float[] _kernels; // [phase * taps + tap]

        // History window of input samples; _bufStart is the absolute input index of _bufL[0].
        private float[] _bufL = new float[8192];
        private float[] _bufR = new float[8192];
        private long _bufStart;
        private int _bufCount;
        private long _nextOut;

        public PolyphaseResampler(int inRate, int outRate)
        {
            long g = Gcd(inRate, outRate);
            _inRate = inRate / g;
            _outRate = outRate / g;

            // Cutoff (as a fraction of the input Nyquist): band-limit to 97% of the lower Nyquist.
            double fc = Math.Min(1.0, (double)outRate / inRate) * 0.97;
            _half = (int)Math.Ceiling(ZeroCrossings / fc);
            _taps = 2 * _half;

            // Exact phase count is the reduced output rate; quantize to MaxPhases for unusual (coprime) rates.
            _phaseDenominator = _outRate;
            _phases = (int)Math.Min(_outRate, MaxPhases);
            _kernels = new float[_phases * _taps];

            double i0Beta = BesselI0(KaiserBeta);
            for (int p = 0; p < _phases; p++)
            {
                double frac = (double)p / _phases;
                double sum = 0;
                int baseIdx = p * _taps;
                for (int j = 0; j < _taps; j++)
                {
                    // tap j multiplies input sample floor(t) - _half + 1 + j; x = sample - t
                    double x = (j - _half + 1) - frac;
                    double w = x / _half;
                    double window = Math.Abs(w) >= 1.0 ? 0.0 : BesselI0(KaiserBeta * Math.Sqrt(1.0 - (w * w))) / i0Beta;
                    double y = fc * x;
                    double sinc = Math.Abs(y) < 1e-12 ? 1.0 : Math.Sin(Math.PI * y) / (Math.PI * y);
                    double h = fc * sinc * window;
                    _kernels[baseIdx + j] = (float)h;
                    sum += h;
                }

                // Normalize each phase to unity DC gain (removes passband ripple between phases).
                if (Math.Abs(sum) > 1e-12)
                {
                    for (int j = 0; j < _taps; j++)
                    {
                        _kernels[baseIdx + j] = (float)(_kernels[baseIdx + j] / sum);
                    }
                }
            }
        }

        public void Push(float[] left, float[] right, int count, Action<float, float> emit)
        {
            Append(left, right, count);
            long available = _bufStart + _bufCount; // absolute index one past the last input sample
            ProduceWhile(n => FloorPosition(n) + _half < available, available, emit);
            Compact();
        }

        public void Flush(long targetOutputFrames, Action<float, float> emit)
        {
            long available = _bufStart + _bufCount;
            ProduceWhile(n => n < targetOutputFrames, available, emit);
        }

        private void ProduceWhile(Func<long, bool> canProduce, long available, Action<float, float> emit)
        {
            while (canProduce(_nextOut))
            {
                long n = _nextOut;
                long num = n * _inRate;
                long ip = num / _outRate;
                long rem = num % _outRate;
                int phase = _phases == _phaseDenominator
                    ? (int)rem
                    : (int)Math.Min(_phases - 1, (rem * _phases + (_phaseDenominator / 2)) / _phaseDenominator);

                long first = ip - _half + 1;
                int kBase = phase * _taps;
                double accL = 0, accR = 0;
                for (int j = 0; j < _taps; j++)
                {
                    long idx = first + j;
                    if (idx < _bufStart || idx >= available) continue; // zero outside the signal
                    int b = (int)(idx - _bufStart);
                    float k = _kernels[kBase + j];
                    accL += _bufL[b] * k;
                    accR += _bufR[b] * k;
                }

                emit((float)accL, (float)accR);
                _nextOut++;
            }
        }

        private long FloorPosition(long n) => n * _inRate / _outRate;

        private void Append(float[] left, float[] right, int count)
        {
            if (_bufCount + count > _bufL.Length)
            {
                int newSize = Math.Max(_bufL.Length * 2, _bufCount + count);
                Array.Resize(ref _bufL, newSize);
                Array.Resize(ref _bufR, newSize);
            }

            Array.Copy(left, 0, _bufL, _bufCount, count);
            Array.Copy(right, 0, _bufR, _bufCount, count);
            _bufCount += count;
        }

        private void Compact()
        {
            // Keep only what the next output sample can still reference.
            long keepFrom = FloorPosition(_nextOut) - _half + 1;
            int drop = (int)Math.Clamp(keepFrom - _bufStart, 0, _bufCount);
            if (drop <= 0) return;

            Array.Copy(_bufL, drop, _bufL, 0, _bufCount - drop);
            Array.Copy(_bufR, drop, _bufR, 0, _bufCount - drop);
            _bufCount -= drop;
            _bufStart += drop;
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }

        private static double BesselI0(double x)
        {
            double sum = 1.0, term = 1.0, halfX = x / 2.0;
            for (int k = 1; k < 64; k++)
            {
                term *= (halfX / k) * (halfX / k);
                sum += term;
                if (term < 1e-12 * sum) break;
            }
            return sum;
        }
    }
}
