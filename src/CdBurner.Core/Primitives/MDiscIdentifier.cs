namespace CdBurner.Core.Primitives;

/// <summary>
/// Recognises M-DISC media from the manufacturer ID the drive reads off the disc. M-DISC is not a separate MMC
/// profile: a DVD M-DISC reports as DVD±R and a BD M-DISC as BD-R, so the media ID is the only sign.
/// </summary>
public static class MDiscIdentifier
{
    /// <summary>
    /// Media ID prefixes that belong to M-DISC media. Millenniata made only M-DISC, so its whole code counts.
    /// Verbatim-made M-DISC BD-R carries Verbatim's own codes; add those here only once read from a real disc, so an
    /// ordinary Verbatim BD-R is never called archival.
    /// </summary>
    public static IReadOnlyList<string> KnownMediaIdPrefixes { get; } = ["MILLEN"];

    /// <summary>True when the media ID (raw code or the readable summary that starts with it) is a known M-DISC code.</summary>
    public static bool IsMDisc(string? mediaManufacturerId)
    {
        if (string.IsNullOrWhiteSpace(mediaManufacturerId))
        {
            return false;
        }

        string id = mediaManufacturerId.TrimStart();
        foreach (string prefix in KnownMediaIdPrefixes)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
