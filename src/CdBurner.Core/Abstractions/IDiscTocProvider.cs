namespace CdBurner.Core.Abstractions;

/// <summary>
/// A single entry from a CD Table of Contents.
/// </summary>
/// <param name="TrackNumber">Track number (1..99).</param>
/// <param name="StartLba">Logical block address where the track's INDEX 01 begins.</param>
/// <param name="SectorCount">Number of sectors in the track (up to the next track or lead-out).</param>
/// <param name="IsAudio">True for CD-DA audio tracks; false for data tracks.</param>
public record DiscTocTrack(int TrackNumber, int StartLba, int SectorCount, bool IsAudio);

/// <summary>
/// Optional capability for recorders that can read the physical disc Table of Contents
/// (used by audio ripping to discover track boundaries on real media).
/// </summary>
public interface IDiscTocProvider
{
    /// <summary>
    /// Reads the Table of Contents of the inserted disc. Returns an empty list if no TOC is available.
    /// </summary>
    IReadOnlyList<DiscTocTrack> ReadTableOfContents();

    /// <summary>
    /// Reads the raw CD-Text packs (a whole number of 18-byte packs, without the 4-byte response header) from the
    /// lead-in of the inserted disc. Returns null if the disc or drive provides no CD-Text.
    /// </summary>
    byte[]? ReadCdTextPacks() => null;
}
