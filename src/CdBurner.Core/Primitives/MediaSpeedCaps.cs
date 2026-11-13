using System.Globalization;
using System.Text.RegularExpressions;

namespace CdBurner.Core.Primitives;

/// <summary>A write-speed ceiling for one kind of disc, with the reason to show beside the default speed.</summary>
public sealed record MediaSpeedCap(double MaxMultiplier, string Reason);

/// <summary>
/// A small table of media IDs known to need a lower write speed than <see cref="WriteSpeedPolicy"/> would otherwise
/// pick. Entries come from our own measurements, not from general lore: add one only after a disc has been written and
/// read back and the result was worse at the higher speeds.
/// <para>
/// A CD-R is identified by its ATIP lead-in start time (minute, second, tens of frames), the same code
/// <c>MmcMediaIdInspector</c> maps to a manufacturer name. The last frame digit is the dye type, not part of the code.
/// The media ID text the app and <c>media-info</c> show starts with that time, e.g. <c>97m26s00f (...)</c>.
/// </para>
/// </summary>
public static class MediaSpeedCaps
{
    private sealed record Entry(int Minute, int Second, int FrameTens, MediaSpeedCap Cap);

    // FORNET CD-R (ATIP 97m26s0x): measured to read weakly towards the rim at every speed tried, so it is held
    // to the slowest range a drive normally offers (10x on slim writers).
    private static readonly Entry[] CdAtipEntries =
    [
        new(97, 26, 0, new MediaSpeedCap(10, "FORNET CD-R read weakly at the outer edge after every speed tried, so it is held to a low speed.")),
    ];

    private static readonly Regex AtipPrefix =
        new(@"^\s*(\d{2})m(\d{2})s(\d{2})f", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The cap for a disc, or null when none is known. <paramref name="mediaId"/> is the media ID text as shown by the
    /// app (null or empty when the drive gave none); <paramref name="mediaType"/> must be a CD for an ATIP code to count.
    /// </summary>
    public static MediaSpeedCap? Find(string? mediaId, DiscMediaType mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || mediaType.IsDvd() || mediaType.IsBluRay()) return null;

        var m = AtipPrefix.Match(mediaId);
        if (!m.Success) return null;

        int minute = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int second = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int frameTens = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 10;

        foreach (var e in CdAtipEntries)
        {
            if (e.Minute == minute && e.Second == second && e.FrameTens == frameTens) return e.Cap;
        }
        return null;
    }
}
