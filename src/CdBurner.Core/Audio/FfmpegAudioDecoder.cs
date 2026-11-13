using System.Diagnostics;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>Raised when a file needs the optional ffmpeg download and none can be found.</summary>
public sealed class FfmpegNotInstalledException : InvalidOperationException
{
    public FfmpegNotInstalledException(string filePath)
        : base($"'{Path.GetFileName(filePath)}' needs ffmpeg to be decoded, and ffmpeg is not installed. " +
               "Install it with the 1-click download (Settings > Install FFmpeg, or put ffmpeg.exe on PATH), " +
               "or convert the file to FLAC or WAV first.")
    {
    }
}

/// <summary>
/// Decodes Opus, Ogg Vorbis and MP3 files to Red Book PCM with the optional ffmpeg download, so these formats do not
/// depend on Windows codec packages. The encoder delay and padding are removed using the numbers stored in the file
/// (<see cref="GaplessStreamInfo"/>), so the sample count is the length of the music and tracks of a gapless album
/// join without added samples.
/// </summary>
public static class FfmpegAudioDecoder
{
    /// <summary>True for extensions this decoder handles when ffmpeg is present.</summary>
    public static bool HandlesExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".opus", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".oga", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the ffmpeg argument list: first audio stream, signed 16-bit stereo PCM on stdout at <paramref name="sampleRate"/>.</summary>
    public static IReadOnlyList<string> BuildArguments(string inputPath, int sampleRate) =>
    [
        "-hide_banner", "-nostdin", "-v", "error",
        "-i", inputPath,
        "-vn", "-map", "0:a:0",
        "-ac", "2", "-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-f", "s16le", "-acodec", "pcm_s16le",
        "pipe:1"
    ];

    /// <summary>
    /// Decodes <paramref name="path"/> to 44,100 Hz 16-bit stereo PCM with trimmed encoder delay and padding.
    /// <paramref name="ffmpeg"/> is the path to ffmpeg.exe (see <see cref="FfmpegLocator"/>).
    /// </summary>
    public static byte[] DecodeToRedBookPcm(string path, string ffmpeg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpeg);
        var info = GaplessStreamInfo.TryRead(path);
        int nativeRate = info?.SampleRate > 0 ? info.SampleRate : OpticalConstants.CdAudioSampleRate;

        byte[] raw = RunFfmpeg(ffmpeg, BuildArguments(path, nativeRate), path);
        long decoded = raw.Length / 4;
        byte[] pcm = raw;
        if (info is not null)
        {
            var (skip, take) = GaplessStreamInfo.PlanTrim(info, decoded);
            if (skip != 0 || take != decoded)
            {
                pcm = raw.AsSpan(checked((int)(skip * 4)), checked((int)(take * 4))).ToArray();
            }
        }
        else
        {
            pcm = raw.AsSpan(0, (int)(decoded * 4)).ToArray();
        }

        if (nativeRate == OpticalConstants.CdAudioSampleRate)
        {
            return pcm;
        }

        byte[] header = WavFileCodec.CreateWavHeader(pcm.Length, nativeRate, 2, 16);
        using var wav = new MemoryStream(header.Length + pcm.Length);
        wav.Write(header);
        wav.Write(pcm);
        wav.Position = 0;
        return WavFileCodec.DecodeToRedBookPcm(wav);
    }

    private static byte[] RunFfmpeg(string ffmpeg, IReadOnlyList<string> args, string inputPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start ffmpeg at '{ffmpeg}'.");
        }

        // stderr is read on its own task so a full pipe cannot stall ffmpeg while stdout is being read.
        var errTask = proc.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit();
        string err = errTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
        {
            string tail = err.Length > 500 ? err[^500..] : err;
            throw new InvalidDataException($"ffmpeg could not decode '{Path.GetFileName(inputPath)}' (exit code {proc.ExitCode}): {tail.Trim()}");
        }
        return ms.ToArray();
    }
}
