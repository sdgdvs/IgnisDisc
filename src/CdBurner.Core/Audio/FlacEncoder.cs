using System.Diagnostics;

namespace CdBurner.Core.Audio;

/// <summary>
/// Audio track metadata tags used for FLAC stream encoding and container tagging.
/// </summary>
public sealed record AudioTrackTags(
    string Title,
    string Artist,
    string Album,
    int TrackNumber,
    int TrackTotal,
    string? Isrc = null,
    string? Date = null);

/// <summary>
/// Encodes uncompressed RIFF WAVE audio files to lossless FLAC using external FFmpeg.
/// </summary>
public static class FlacEncoder
{
    /// <summary>
    /// Builds the argument list for FFmpeg FLAC encoding in the required order.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string wavPath, string flacPath, AudioTrackTags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(flacPath);
        ArgumentNullException.ThrowIfNull(tags);

        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i",
            wavPath,
            "-map_metadata",
            "-1",
            "-c:a",
            "flac",
            "-compression_level",
            "8",
            "-metadata",
            $"TITLE={tags.Title}",
            "-metadata",
            $"ARTIST={tags.Artist}",
            "-metadata",
            $"ALBUM={tags.Album}",
            "-metadata",
            $"TRACKNUMBER={tags.TrackNumber}",
            "-metadata",
            $"TRACKTOTAL={tags.TrackTotal}"
        };

        if (!string.IsNullOrWhiteSpace(tags.Isrc))
        {
            args.Add("-metadata");
            args.Add($"ISRC={tags.Isrc}");
        }

        if (!string.IsNullOrWhiteSpace(tags.Date))
        {
            args.Add("-metadata");
            args.Add($"DATE={tags.Date}");
        }

        args.Add(flacPath);
        return args;
    }

    /// <summary>
    /// Executes FFmpeg to encode a WAV file into a FLAC file with embedded tags.
    /// </summary>
    public static async Task EncodeAsync(
        string ffmpegPath,
        string wavPath,
        string flacPath,
        AudioTrackTags tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(flacPath);
        ArgumentNullException.ThrowIfNull(tags);

        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException($"Source WAV file not found: {wavPath}", wavPath);
        }

        var args = BuildArguments(wavPath, flacPath, tags);

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
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
            catch
            {
            }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty;
            string lastStderr = stderr.Length > 500 ? stderr[^500..] : stderr;
            throw new InvalidOperationException(
                $"FFmpeg FLAC encoding failed with exit code {proc.ExitCode}: {lastStderr}");
        }
    }
}
