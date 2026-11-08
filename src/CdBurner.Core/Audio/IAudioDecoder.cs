namespace CdBurner.Core.Audio;

/// <summary>
/// Audio format metadata probed from an audio container or raw audio stream.
/// </summary>
public record AudioFormatMetadata
{
    /// <summary>
    /// Path to the audio file on disk.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable container/codec format description (e.g., "WAV PCM 16-bit", "MP3", "FLAC").
    /// </summary>
    public string FormatName { get; init; } = string.Empty;

    /// <summary>
    /// Number of audio channels (e.g. 1 for mono, 2 for stereo).
    /// </summary>
    public int Channels { get; init; }

    /// <summary>
    /// Audio sampling rate in Hertz (e.g., 44,100 Hz, 48,000 Hz, 22,050 Hz).
    /// </summary>
    public int SampleRate { get; init; }

    /// <summary>
    /// Bit depth per audio sample (e.g. 16, 24, 32, 8).
    /// </summary>
    public int BitsPerSample { get; init; }

    /// <summary>
    /// Total duration of the audio stream.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Raw uncompressed source PCM byte length before conversion, or decoded byte count.
    /// </summary>
    public long TotalPcmBytes { get; init; }

    /// <summary>
    /// Estimated or exact total bytes when decoded to canonical Red Book CD-DA PCM
    /// (44,100 Hz, 16-bit Stereo = 176,400 bytes/sec).
    /// </summary>
    public long TotalRedBookBytes { get; init; }

    /// <summary>
    /// Total number of 2,352-byte Red Book frames required for this audio track (rounded up).
    /// </summary>
    public int TotalFrames => (int)((TotalRedBookBytes + 2351) / 2352);

    /// <summary>
    /// Optional embedded track title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Optional embedded artist or performer.
    /// </summary>
    public string? Artist { get; init; }

    /// <summary>
    /// Optional embedded album title.
    /// </summary>
    public string? Album { get; init; }

    /// <summary>
    /// Optional embedded album artist / performer.
    /// </summary>
    public string? AlbumArtist { get; init; }

    /// <summary>
    /// Optional embedded songwriter / lyricist.
    /// </summary>
    public string? Songwriter { get; init; }

    /// <summary>
    /// Optional embedded composer.
    /// </summary>
    public string? Composer { get; init; }

    /// <summary>
    /// Optional embedded International Standard Recording Code (ISRC).
    /// </summary>
    public string? Isrc { get; init; }

    /// <summary>
    /// Optional embedded 1-based track number.
    /// </summary>
    public int? TrackNumber { get; init; }
}

/// <summary>
/// Audio decoding interface providing format inspection and streaming decoding
/// into canonical Red Book CD-DA PCM (44,100 Hz, 16-bit signed LE, 2 channels).
/// </summary>
public interface IAudioDecoder
{
    /// <summary>
    /// Inspects and extracts metadata from an audio file without decoding the full stream.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the audio file.</param>
    /// <returns>Probed audio format metadata.</returns>
    AudioFormatMetadata ProbeAudioFile(string filePath);

    /// <summary>
    /// Opens and decodes an audio file into a raw Red Book CD-DA PCM stream
    /// (44,100 Hz, 16-bit signed little-endian, 2 channels, 2,352 bytes/frame).
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the audio file.</param>
    /// <returns>A seekable or readable stream containing Red Book PCM bytes.</returns>
    Stream OpenRedBookPcmStream(string filePath);
}
