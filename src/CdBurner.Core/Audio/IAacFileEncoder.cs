namespace CdBurner.Core.Audio;

/// <summary>
/// Encodes a WAV file to AAC in an MPEG-4 (.m4a) container. Implemented on top of the encoder built into Windows
/// (Media Foundation), so no encoder binary ships with the application.
/// </summary>
public interface IAacFileEncoder
{
    /// <summary>
    /// Encodes <paramref name="wavPath"/> (44.1 kHz, 16-bit, stereo PCM) to <paramref name="m4aPath"/> and writes the
    /// tags the container supports. Returns a warning text when a tag could not be written, otherwise null.
    /// </summary>
    Task<string?> EncodeAsync(
        string wavPath,
        string m4aPath,
        AudioTrackTags tags,
        int bitrateKbps,
        CancellationToken cancellationToken = default);
}
