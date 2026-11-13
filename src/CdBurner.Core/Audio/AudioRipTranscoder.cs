using System.Text.RegularExpressions;

namespace CdBurner.Core.Audio;

/// <summary>What to convert a finished WAV rip into.</summary>
/// <param name="Format">Target format. <see cref="AudioRipFormat.Wav"/> leaves the files as they are.</param>
/// <param name="BitrateKbps">Bitrate for MP3, Opus and AAC; 0 selects the default for the format.</param>
/// <param name="FfmpegPath">Path to ffmpeg.exe; required for FLAC, MP3 and Opus.</param>
/// <param name="KeepWav">Keep the intermediate WAV files next to the converted ones.</param>
public sealed record AudioRipTranscodeSettings(
    AudioRipFormat Format,
    int BitrateKbps = 0,
    string? FfmpegPath = null,
    bool KeepWav = false);

/// <summary>Outcome of <see cref="AudioRipTranscoder.TranscodeAsync"/>.</summary>
public sealed record AudioRipTranscodeResult(IReadOnlyList<string> Files, IReadOnlyList<string> Warnings);

/// <summary>
/// Converts the WAV files written by <see cref="AudioCdRipperService"/> to FLAC, MP3, Opus or AAC with tags, and
/// points the companion CUE sheet at the converted files. The rip itself (secure reads, AccurateRip checksums,
/// ReplayGain) always works on the raw PCM, so the output format never changes what was read from the disc.
/// </summary>
public sealed class AudioRipTranscoder
{
    private readonly IAacFileEncoder? _aacEncoder;

    public AudioRipTranscoder(IAacFileEncoder? aacEncoder = null)
    {
        _aacEncoder = aacEncoder;
    }

    /// <summary>
    /// Returns an error message if <paramref name="settings"/> cannot be honoured (missing ffmpeg, bad bitrate, no AAC
    /// encoder), otherwise null. Call before ripping so a disc is not read for nothing.
    /// </summary>
    public string? CheckPrerequisites(AudioRipTranscodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var format = settings.Format;
        if (format == AudioRipFormat.Wav)
        {
            return settings.BitrateKbps > 0 ? AudioRipFormats.ValidateBitrate(format, settings.BitrateKbps) : null;
        }

        if (settings.BitrateKbps > 0)
        {
            string? bitrateError = AudioRipFormats.ValidateBitrate(format, settings.BitrateKbps);
            if (bitrateError != null)
            {
                return bitrateError;
            }
        }

        if (AudioRipFormats.RequiresFfmpeg(format) && (string.IsNullOrWhiteSpace(settings.FfmpegPath) || !File.Exists(settings.FfmpegPath)))
        {
            return AudioRipFormats.MissingFfmpegMessage(format);
        }

        if (format == AudioRipFormat.Aac && _aacEncoder == null)
        {
            return "AAC output needs the Windows AAC encoder (Media Foundation), which is not available on this system.";
        }

        return null;
    }

    /// <summary>
    /// Converts every ripped track, deletes the WAV files unless asked to keep them, and rewrites the CUE sheet.
    /// Throws if an encoder fails; the WAV files are kept in that case so the rip is not lost.
    /// </summary>
    public async Task<AudioRipTranscodeResult> TranscodeAsync(
        AudioCdRipResult rip,
        AudioRipTranscodeSettings settings,
        byte[]? coverArtBytes = null,
        string? coverArtMimeType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rip);
        ArgumentNullException.ThrowIfNull(settings);

        var format = settings.Format;
        if (format == AudioRipFormat.Wav)
        {
            return new AudioRipTranscodeResult(rip.RippedFilePaths.ToList(), []);
        }

        string? problem = CheckPrerequisites(settings);
        if (problem != null)
        {
            throw new InvalidOperationException(problem);
        }

        int bitrate = settings.BitrateKbps > 0 ? settings.BitrateKbps : AudioRipFormats.DefaultBitrateKbps(format);
        string extension = AudioRipFormats.Extension(format);
        var warnings = new List<string>();
        var outputs = new List<string>(rip.TrackInfos.Count);
        int trackTotal = rip.RippedFilePaths.Count;
        string albumTitle = !string.IsNullOrWhiteSpace(rip.CdText?.AlbumTitle) ? rip.CdText.AlbumTitle : "Unknown Album";
        string albumPerformer = !string.IsNullOrWhiteSpace(rip.CdText?.AlbumPerformer) ? rip.CdText.AlbumPerformer : "Unknown Artist";

        string? coverTempPath = null;
        try
        {
            if (coverArtBytes is { Length: > 0 } && format == AudioRipFormat.Mp3)
            {
                coverTempPath = Path.Combine(Path.GetTempPath(), "ignisdisc-cover-" + Guid.NewGuid().ToString("N") + MimeExtension(coverArtMimeType));
                await File.WriteAllBytesAsync(coverTempPath, coverArtBytes, cancellationToken).ConfigureAwait(false);
            }
            else if (coverArtBytes is { Length: > 0 } && format is AudioRipFormat.Opus or AudioRipFormat.Aac)
            {
                warnings.Add($"Cover art is not embedded in {AudioRipFormats.DisplayName(format)} files (supported in FLAC and MP3).");
            }

            foreach (var info in rip.TrackInfos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string wavPath = info.FilePath;
                string outPath = Path.ChangeExtension(wavPath, extension);
                var tags = BuildTags(info, rip, albumTitle, albumPerformer, trackTotal, coverArtBytes, coverArtMimeType);

                switch (format)
                {
                    case AudioRipFormat.Flac:
                        await FlacEncoder.EncodeAsync(settings.FfmpegPath!, wavPath, outPath, tags, cancellationToken).ConfigureAwait(false);
                        if (coverArtBytes is { Length: > 0 })
                        {
                            FlacEncoder.EmbedCoverArtAndReplayGain(outPath, tags);
                        }
                        break;

                    case AudioRipFormat.Mp3:
                    case AudioRipFormat.Opus:
                        await FfmpegAudioEncoder.EncodeAsync(format, settings.FfmpegPath!, wavPath, outPath, tags, bitrate, coverTempPath, cancellationToken).ConfigureAwait(false);
                        break;

                    case AudioRipFormat.Aac:
                        string? tagWarning = await _aacEncoder!.EncodeAsync(wavPath, outPath, tags, bitrate, cancellationToken).ConfigureAwait(false);
                        if (tagWarning != null)
                        {
                            warnings.Add($"Track {info.TrackNumber:D2}: {tagWarning}");
                        }
                        break;
                }

                outputs.Add(outPath);
            }
        }
        finally
        {
            if (coverTempPath != null)
            {
                try { File.Delete(coverTempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        if (rip.CueSheetPath != null && File.Exists(rip.CueSheetPath))
        {
            await RewriteCueAsync(rip.CueSheetPath, extension, format == AudioRipFormat.Mp3 ? "MP3" : "WAVE", cancellationToken).ConfigureAwait(false);
        }

        if (!settings.KeepWav)
        {
            foreach (var info in rip.TrackInfos)
            {
                try
                {
                    File.Delete(info.FilePath);
                }
                catch (IOException ex)
                {
                    warnings.Add($"Could not delete '{Path.GetFileName(info.FilePath)}': {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    warnings.Add($"Could not delete '{Path.GetFileName(info.FilePath)}': {ex.Message}");
                }
            }
        }

        return new AudioRipTranscodeResult(outputs, warnings);
    }

    private static AudioTrackTags BuildTags(
        AudioTrackRipInfo info,
        AudioCdRipResult rip,
        string albumTitle,
        string albumPerformer,
        int trackTotal,
        byte[]? coverArtBytes,
        string? coverArtMimeType)
    {
        string title = !string.IsNullOrWhiteSpace(info.Title) ? info.Title : $"Track {info.TrackNumber:D2}";
        string artist = !string.IsNullOrWhiteSpace(info.Performer) && info.Performer != "Unknown Artist" ? info.Performer : albumPerformer;
        return new AudioTrackTags(
            Title: title,
            Artist: artist,
            Album: albumTitle,
            TrackNumber: info.TrackNumber,
            TrackTotal: trackTotal,
            Isrc: info.Isrc,
            Date: null,
            ReplayGainTrackGain: info.ReplayGainTrackGain.HasValue ? ReplayGainCalculator.FormatGainDb(info.ReplayGainTrackGain.Value) : null,
            ReplayGainTrackPeak: info.ReplayGainTrackPeak.HasValue ? ReplayGainCalculator.FormatPeak(info.ReplayGainTrackPeak.Value) : null,
            ReplayGainAlbumGain: rip.ReplayGainAlbumGain.HasValue ? ReplayGainCalculator.FormatGainDb(rip.ReplayGainAlbumGain.Value) : null,
            ReplayGainAlbumPeak: rip.ReplayGainAlbumPeak.HasValue ? ReplayGainCalculator.FormatPeak(rip.ReplayGainAlbumPeak.Value) : null,
            CoverArtBytes: coverArtBytes,
            CoverArtMimeType: coverArtMimeType);
    }

    private static string MimeExtension(string? mime) => mime switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    private static async Task RewriteCueAsync(string cuePath, string newExtension, string cueFileType, CancellationToken cancellationToken)
    {
        string content = await File.ReadAllTextAsync(cuePath, cancellationToken).ConfigureAwait(false);
        string separator = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var fileLine = new Regex(@"^FILE\s+""(?<name>.+?)\.wav""\s+WAVE$", RegexOptions.IgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            var match = fileLine.Match(trimmed);
            if (match.Success)
            {
                string prefix = lines[i][..(lines[i].Length - trimmed.Length)];
                lines[i] = $"{prefix}FILE \"{match.Groups["name"].Value}{newExtension}\" {cueFileType}";
            }
        }

        await File.WriteAllTextAsync(cuePath, string.Join(separator, lines), cancellationToken).ConfigureAwait(false);
    }
}
