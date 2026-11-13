namespace CdBurner.Core.Audio;

/// <summary>Output formats for a ripped audio CD.</summary>
public enum AudioRipFormat
{
    Wav,
    Flac,
    Mp3,
    Opus,
    Aac
}

/// <summary>
/// Names, file extensions, bitrate rules and tool requirements for each <see cref="AudioRipFormat"/>.
/// </summary>
public static class AudioRipFormats
{
    /// <summary>Bitrates (kbit/s) the Windows AAC encoder accepts for 44.1 kHz stereo (Microsoft AAC Encoder docs).</summary>
    public static readonly IReadOnlyList<int> AacBitratesKbps = [96, 128, 160, 192];

    public const int Mp3MinKbps = 32;
    public const int Mp3MaxKbps = 320;
    public const int OpusMinKbps = 6;
    public const int OpusMaxKbps = 510;

    public static string Extension(AudioRipFormat format) => format switch
    {
        AudioRipFormat.Wav => ".wav",
        AudioRipFormat.Flac => ".flac",
        AudioRipFormat.Mp3 => ".mp3",
        AudioRipFormat.Opus => ".opus",
        AudioRipFormat.Aac => ".m4a",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static string DisplayName(AudioRipFormat format) => format switch
    {
        AudioRipFormat.Wav => "WAV",
        AudioRipFormat.Flac => "FLAC",
        AudioRipFormat.Mp3 => "MP3",
        AudioRipFormat.Opus => "Opus",
        AudioRipFormat.Aac => "AAC (M4A)",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>True when the format is produced by the optional ffmpeg download (no encoder ships with the app).</summary>
    public static bool RequiresFfmpeg(AudioRipFormat format) =>
        format is AudioRipFormat.Flac or AudioRipFormat.Mp3 or AudioRipFormat.Opus;

    /// <summary>True when the format is lossy and takes a bitrate.</summary>
    public static bool UsesBitrate(AudioRipFormat format) =>
        format is AudioRipFormat.Mp3 or AudioRipFormat.Opus or AudioRipFormat.Aac;

    public static int DefaultBitrateKbps(AudioRipFormat format) => format switch
    {
        AudioRipFormat.Mp3 => 192,
        AudioRipFormat.Opus => 128,
        AudioRipFormat.Aac => 128,
        _ => 0
    };

    /// <summary>Parses wav, flac, mp3, opus, aac or m4a (case-insensitive).</summary>
    public static bool TryParse(string? text, out AudioRipFormat format)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "wav": format = AudioRipFormat.Wav; return true;
            case "flac": format = AudioRipFormat.Flac; return true;
            case "mp3": format = AudioRipFormat.Mp3; return true;
            case "opus": format = AudioRipFormat.Opus; return true;
            case "aac" or "m4a": format = AudioRipFormat.Aac; return true;
            default: format = AudioRipFormat.Wav; return false;
        }
    }

    /// <summary>Returns null when <paramref name="kbps"/> is valid for the format, otherwise a message saying what is accepted.</summary>
    public static string? ValidateBitrate(AudioRipFormat format, int kbps) => format switch
    {
        AudioRipFormat.Mp3 when kbps is < Mp3MinKbps or > Mp3MaxKbps =>
            $"MP3 bitrate must be between {Mp3MinKbps} and {Mp3MaxKbps} kbit/s.",
        AudioRipFormat.Opus when kbps is < OpusMinKbps or > OpusMaxKbps =>
            $"Opus bitrate must be between {OpusMinKbps} and {OpusMaxKbps} kbit/s.",
        AudioRipFormat.Aac when !AacBitratesKbps.Contains(kbps) =>
            $"AAC bitrate must be one of {string.Join(", ", AacBitratesKbps)} kbit/s (the values the Windows AAC encoder accepts for CD audio).",
        AudioRipFormat.Wav or AudioRipFormat.Flac => "A bitrate applies only to MP3, Opus and AAC output.",
        _ => null
    };

    /// <summary>Message shown when ffmpeg is needed but not installed.</summary>
    public static string MissingFfmpegMessage(AudioRipFormat format) =>
        $"{DisplayName(format)} output needs ffmpeg, which is not installed. " +
        "Install it with the 1-click download (Settings > Install FFmpeg, or put ffmpeg.exe on PATH), or choose WAV or AAC, which need no extra download.";
}
