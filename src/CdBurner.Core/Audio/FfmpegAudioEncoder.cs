using System.Diagnostics;

namespace CdBurner.Core.Audio;

/// <summary>Runs an ffmpeg process and captures its output.</summary>
public static class FfmpegProcess
{
    /// <summary>Runs ffmpeg with the argument list; throws <see cref="InvalidOperationException"/> with the tail of stderr on failure.</summary>
    public static async Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        string failureLabel,
        CancellationToken cancellationToken = default)
    {
        var (exitCode, _, stderr) = await CaptureAsync(ffmpegPath, args, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            string tail = stderr.Length > 500 ? stderr[^500..] : stderr;
            throw new InvalidOperationException($"FFmpeg {failureLabel} failed with exit code {exitCode}: {tail}");
        }
    }

    /// <summary>Runs ffmpeg and returns exit code, stdout and stderr.</summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> CaptureAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start FFmpeg process at '{ffmpegPath}'.");
        }

        var outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process ended between the check and the kill.
            }
            throw;
        }

        return (proc.ExitCode, outTask.Result, errTask.Result);
    }

    /// <summary>True when this ffmpeg build lists the named encoder (for example libmp3lame or libopus).</summary>
    public static async Task<bool> HasEncoderAsync(string ffmpegPath, string encoderName, CancellationToken cancellationToken = default)
    {
        var (exitCode, stdout, _) = await CaptureAsync(ffmpegPath, ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return false;
        }

        foreach (string line in stdout.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == encoderName)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Encodes a WAV file to MP3 (libmp3lame) or Opus (libopus) with external ffmpeg. The encoders live in the optional
/// ffmpeg download; none is bundled with the application.
/// </summary>
public static class FfmpegAudioEncoder
{
    public static string EncoderName(AudioRipFormat format) => format switch
    {
        AudioRipFormat.Mp3 => "libmp3lame",
        AudioRipFormat.Opus => "libopus",
        _ => throw new ArgumentOutOfRangeException(nameof(format), "Only MP3 and Opus are encoded by ffmpeg here.")
    };

    /// <summary>
    /// Builds the ffmpeg arguments. MP3 gets ID3v2.3 tags (ReplayGain as TXXX frames) and optional front cover;
    /// Opus gets Vorbis comments. A cover image is attached to MP3 only.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        AudioRipFormat format,
        string wavPath,
        string outputPath,
        AudioTrackTags tags,
        int bitrateKbps,
        string? coverImagePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(tags);
        if (format is not (AudioRipFormat.Mp3 or AudioRipFormat.Opus))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        bool withCover = format == AudioRipFormat.Mp3 && !string.IsNullOrEmpty(coverImagePath);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", wavPath };
        if (withCover)
        {
            args.AddRange(["-i", coverImagePath!]);
        }

        args.AddRange(["-map", "0:a"]);
        if (withCover)
        {
            args.AddRange(["-map", "1:v"]);
        }

        args.AddRange(["-map_metadata", "-1"]);

        if (format == AudioRipFormat.Mp3)
        {
            args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{bitrateKbps}k", "-id3v2_version", "3"]);
            if (withCover)
            {
                args.AddRange(["-c:v", "copy", "-disposition:v", "attached_pic",
                    "-metadata:s:v", "title=Cover", "-metadata:s:v", "comment=Cover (front)"]);
            }
        }
        else
        {
            // libopus works at 48 kHz; the resample is made explicit so the output rate is not left to negotiation.
            args.AddRange(["-c:a", "libopus", "-b:a", $"{bitrateKbps}k", "-ar", "48000"]);
        }

        AddTag(args, "title", tags.Title);
        AddTag(args, "artist", tags.Artist);
        AddTag(args, "album", tags.Album);
        if (tags.TrackNumber > 0)
        {
            // "track" is the ID3 TRCK frame ("3/12"); Vorbis comments use TRACKNUMBER/TRACKTOTAL.
            if (format == AudioRipFormat.Mp3)
            {
                AddTag(args, "track", tags.TrackTotal > 0 ? $"{tags.TrackNumber}/{tags.TrackTotal}" : tags.TrackNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                AddTag(args, "TRACKNUMBER", tags.TrackNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (tags.TrackTotal > 0)
                {
                    AddTag(args, "TRACKTOTAL", tags.TrackTotal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        AddTag(args, "TSRC", format == AudioRipFormat.Mp3 ? tags.Isrc : null);
        AddTag(args, "ISRC", format == AudioRipFormat.Opus ? tags.Isrc : null);
        AddTag(args, "date", tags.Date);
        AddTag(args, "REPLAYGAIN_TRACK_GAIN", tags.ReplayGainTrackGain);
        AddTag(args, "REPLAYGAIN_TRACK_PEAK", tags.ReplayGainTrackPeak);
        AddTag(args, "REPLAYGAIN_ALBUM_GAIN", tags.ReplayGainAlbumGain);
        AddTag(args, "REPLAYGAIN_ALBUM_PEAK", tags.ReplayGainAlbumPeak);

        args.Add(outputPath);
        return args;
    }

    private static void AddTag(List<string> args, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.Add("-metadata");
            args.Add($"{key}={value}");
        }
    }

    /// <summary>Encodes <paramref name="wavPath"/> to <paramref name="outputPath"/>; a missing encoder in this ffmpeg build is reported by name.</summary>
    public static async Task EncodeAsync(
        AudioRipFormat format,
        string ffmpegPath,
        string wavPath,
        string outputPath,
        AudioTrackTags tags,
        int bitrateKbps,
        string? coverImagePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException($"Source WAV file not found: {wavPath}", wavPath);
        }

        string encoder = EncoderName(format);
        if (!await FfmpegProcess.HasEncoderAsync(ffmpegPath, encoder, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"This ffmpeg build ({ffmpegPath}) has no {encoder} encoder, so it cannot write {AudioRipFormats.DisplayName(format)}. " +
                "Install the optional ffmpeg download from IgnisDisc, which includes it.");
        }

        var args = BuildArguments(format, wavPath, outputPath, tags, bitrateKbps, coverImagePath);
        await FfmpegProcess.RunAsync(ffmpegPath, args, $"{AudioRipFormats.DisplayName(format)} encoding", cancellationToken).ConfigureAwait(false);
    }
}
