using CdBurner.Core.Primitives;

namespace CdBurner.Core.Abstractions;

/// <summary>What the Current/Maximum Capacity Descriptor of READ FORMAT CAPACITIES says about the disc.</summary>
public enum FormattedCapacityState
{
    Reserved = 0,
    /// <summary>Not formatted (or write-once): the figure is the most the disc can be formatted to.</summary>
    Unformatted = 1,
    /// <summary>Formatted: the figure is the current usable size (DVD+RW, DVD-RAM, BD-RE after formatting).</summary>
    Formatted = 2,
    NoMedia = 3
}

/// <summary>One format the drive offers for the inserted disc (MMC Format Type, e.g. 0x26 DVD+RW, 0x30 BD-RE with spare).</summary>
public sealed record FormattableCapacity(byte FormatType, long Blocks, int TypeParameter);

/// <summary>Result of READ FORMAT CAPACITIES.</summary>
/// <param name="Blocks">Current (formatted) or maximum (unformatted) size in blocks.</param>
/// <param name="BlockLength">Bytes per block (2048 for DVD and BD).</param>
/// <param name="State">Whether <paramref name="Blocks"/> is a formatted size or the unformatted maximum.</param>
/// <param name="Formats">Formats the drive could apply, in the drive's order.</param>
public sealed record FormattedCapacityInfo(
    long Blocks,
    int BlockLength,
    FormattedCapacityState State,
    IReadOnlyList<FormattableCapacity> Formats)
{
    public long Bytes => Blocks * BlockLength;

    /// <summary>"formatted" or "unformatted", as used in JSON output.</summary>
    public string StateName => State == FormattedCapacityState.Formatted ? "formatted" : "unformatted";
}

/// <summary>One write speed from GET PERFORMANCE (type 03h) for the inserted disc.</summary>
/// <param name="KilobytesPerSecond">Write speed in kB/s (1 kB = 1000 bytes, as MMC reports it).</param>
/// <param name="EndLba">Last address the speed applies to (the disc's end for constant-speed writing).</param>
/// <param name="ReadKilobytesPerSecond">Read speed reported alongside it.</param>
/// <param name="Exact">The drive says the descriptor is exact for this disc rather than a general figure.</param>
/// <param name="Rotation">Write Rotation Control (byte 0, bits 3-4): 0 = the default, constant linear velocity; 1 = CAV.</param>
public sealed record DriveWriteSpeed(int KilobytesPerSecond, int EndLba, int ReadKilobytesPerSecond, bool Exact, WriteRotation Rotation = WriteRotation.Unknown)
{
    public long BytesPerSecond => KilobytesPerSecond * 1000L;

    /// <summary>
    /// Speed as an "×" figure for <paramref name="mediaType"/> (CD 1× = 176.4 kB/s as MMC drives count it,
    /// DVD 1× = 1,385 kB/s, BD 1× = 4,495.5 kB/s), rounded to one decimal.
    /// </summary>
    public double Multiplier(DiscMediaType mediaType)
    {
        int baseRate = WriteSpeedDescriptor.GetBaseRate(mediaType, isAudio: mediaType.IsCd());
        return baseRate > 0 ? Math.Round(BytesPerSecond / (double)baseRate, 1) : 0;
    }
}

/// <summary>Formatted capacity and the drive's own write speeds for the inserted disc.</summary>
public sealed record DriveMediaReport(FormattedCapacityInfo? FormattedCapacity, IReadOnlyList<DriveWriteSpeed> WriteSpeeds)
{
    public static DriveMediaReport Empty { get; } = new(null, Array.Empty<DriveWriteSpeed>());

    /// <summary>
    /// The drive's write speeds as entries for the speed picker, slowest first. Converted through the "×" figure, so
    /// a CD speed lands in the units Windows' writer expects (1× = 75 data sectors a second) even though the drive
    /// counts CD speed in audio units (1× = 176.4 kB/s).
    /// </summary>
    public IReadOnlyList<WriteSpeedDescriptor> ToWriteSpeedDescriptors(DiscMediaType mediaType)
    {
        if (mediaType is DiscMediaType.Unknown or DiscMediaType.NoMedia)
        {
            return Array.Empty<WriteSpeedDescriptor>();
        }

        // The same figure can be offered both CLV and CAV; keep each pairing once.
        return WriteSpeeds
            .Select(s => (Multiplier: s.Multiplier(mediaType), s.Rotation))
            .Where(p => p.Multiplier > 0)
            .Distinct()
            .OrderBy(p => p.Multiplier)
            .Select(p => WriteSpeedDescriptor.FromMultiplier(p.Multiplier, mediaType) with { Rotation = p.Rotation })
            .ToList();
    }
}

/// <summary>Recorders that can ask the drive directly (read-only) for <see cref="DriveMediaReport"/>.</summary>
public interface IDriveMediaReportProvider
{
    /// <summary>Returns the report, or <see cref="DriveMediaReport.Empty"/> when the drive can't be asked. Never throws.</summary>
    DriveMediaReport ReadDriveMediaReport();
}
