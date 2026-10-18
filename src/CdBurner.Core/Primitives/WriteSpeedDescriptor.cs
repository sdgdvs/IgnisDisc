namespace CdBurner.Core.Primitives;

/// <summary>
/// Optical drive read/write speed descriptor representing transfer rate in bytes/sec
/// and as an optical multiplier (e.g., 24.0x, 16.0x, 8.0x) relative to CD/DVD 1X reference rates.
/// </summary>
public readonly record struct WriteSpeedDescriptor : IComparable<WriteSpeedDescriptor>
{
    public int BytesPerSecond { get; }
    public DiscMediaType MediaType { get; }
    public double Multiplier { get; }

    public WriteSpeedDescriptor(int bytesPerSecond, DiscMediaType mediaType, bool isAudio = false)
    {
        if (bytesPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), "Bytes per second cannot be negative.");
        }

        BytesPerSecond = bytesPerSecond;
        MediaType = mediaType;
        int baseRate = GetBaseRate(mediaType, isAudio);
        Multiplier = baseRate > 0 ? (double)bytesPerSecond / baseRate : 0.0;
    }

    /// <summary>
    /// Creates a write speed descriptor from a target multiplier (e.g. 16.0x or 48.0x).
    /// </summary>
    public static WriteSpeedDescriptor FromMultiplier(double multiplier, DiscMediaType mediaType, bool isAudio = false)
    {
        if (multiplier < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Multiplier cannot be negative.");
        }

        int baseRate = GetBaseRate(mediaType, isAudio);
        int bytesPerSec = (int)Math.Round(multiplier * baseRate);
        return new WriteSpeedDescriptor(bytesPerSec, mediaType, isAudio);
    }

    /// <summary>
    /// Retrieves the 1X reference rate in bytes per second for the specified media type.
    /// </summary>
    public static int GetBaseRate(DiscMediaType mediaType, bool isAudio = false)
    {
        if (mediaType.IsDvd())
        {
            return OpticalConstants.Dvd1XBytesPerSec;
        }

        return isAudio ? OpticalConstants.CdAudio1XBytesPerSec : OpticalConstants.CdData1XBytesPerSec;
    }

    /// <summary>
    /// Transfer rate in kilobytes per second (KB/s, 1024 bytes).
    /// </summary>
    public double KilobytesPerSecond => BytesPerSecond / 1024.0;

    /// <summary>
    /// Transfer rate in megabytes per second (MB/s, 1024*1024 bytes).
    /// </summary>
    public double MegabytesPerSecond => BytesPerSecond / (1024.0 * 1024.0);

    /// <summary>
    /// Formatted display name for UI dropdown binding.
    /// </summary>
    public string DisplayName => ToString();

    public override string ToString() => $"{Multiplier:0.#}x ({MegabytesPerSecond:0.##} MB/s)";

    public int CompareTo(WriteSpeedDescriptor other) => BytesPerSecond.CompareTo(other.BytesPerSecond);

    public static bool operator <(WriteSpeedDescriptor left, WriteSpeedDescriptor right) => left.CompareTo(right) < 0;
    public static bool operator <=(WriteSpeedDescriptor left, WriteSpeedDescriptor right) => left.CompareTo(right) <= 0;
    public static bool operator >(WriteSpeedDescriptor left, WriteSpeedDescriptor right) => left.CompareTo(right) > 0;
    public static bool operator >=(WriteSpeedDescriptor left, WriteSpeedDescriptor right) => left.CompareTo(right) >= 0;
}
