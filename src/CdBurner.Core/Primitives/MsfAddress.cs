using System.Diagnostics.CodeAnalysis;

namespace CdBurner.Core.Primitives;

/// <summary>
/// Minute, Second, Frame (MSF) optical disc timecode address (Red Book standard).
/// 1 Second = 75 Frames. LBA 0 corresponds to MSF 00:02:00 (150 frames offset for CD Lead-In).
/// </summary>
public readonly record struct MsfAddress : IComparable<MsfAddress>, IParsable<MsfAddress>
{
    public int Minutes { get; }
    public int Seconds { get; }
    public int Frames { get; }

    public MsfAddress(int minutes, int seconds, int frames)
    {
        if (minutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, "Minutes must be non-negative.");
        }
        if (seconds is < 0 or >= 60)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Seconds must be between 0 and 59.");
        }
        if (frames is < 0 or >= OpticalConstants.AudioFramesPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), frames, $"Frames must be between 0 and {OpticalConstants.AudioFramesPerSecond - 1}.");
        }

        Minutes = minutes;
        Seconds = seconds;
        Frames = frames;
    }

    /// <summary>
    /// Converts this MSF address to a 0-indexed Logical Block Address (LBA).
    /// LBA = (Minutes * 60 + Seconds) * 75 + Frames - 150.
    /// </summary>
    public int ToLba()
    {
        return (Minutes * 60 + Seconds) * OpticalConstants.AudioFramesPerSecond + Frames - OpticalConstants.CdLeadInFrames;
    }

    /// <summary>
    /// Converts this MSF address to an absolute frame count from the start of the disc (including lead-in).
    /// Total Frames = (Minutes * 60 + Seconds) * 75 + Frames.
    /// </summary>
    public int ToFrameCount()
    {
        return (Minutes * 60 + Seconds) * OpticalConstants.AudioFramesPerSecond + Frames;
    }

    /// <summary>
    /// Creates an MSF address from a Logical Block Address (LBA).
    /// </summary>
    /// <param name="lba">Logical block address (LBA 0 = 00:02:00). Must be >= -150.</param>
    public static MsfAddress FromLba(int lba)
    {
        int totalFrames = lba + OpticalConstants.CdLeadInFrames;
        if (totalFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lba), lba, "LBA cannot precede the Lead-In (-150).");
        }
        return FromFrameCount(totalFrames);
    }

    /// <summary>
    /// Creates an MSF address from an absolute frame count.
    /// </summary>
    public static MsfAddress FromFrameCount(int frames)
    {
        if (frames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), frames, "Frame count cannot be negative.");
        }

        int f = frames % OpticalConstants.AudioFramesPerSecond;
        int totalSeconds = frames / OpticalConstants.AudioFramesPerSecond;
        int s = totalSeconds % 60;
        int m = totalSeconds / 60;

        return new MsfAddress(m, s, f);
    }

    /// <summary>
    /// Parses an MSF timecode string formatted as "MM:SS:FF".
    /// </summary>
    public static MsfAddress Parse(string s, IFormatProvider? provider = null)
    {
        if (TryParse(s, provider, out var result))
        {
            return result;
        }
        throw new FormatException($"String '{s}' was not recognized as a valid MSF address (expected format MM:SS:FF).");
    }

    /// <summary>
    /// Attempts to parse an MSF timecode string formatted as "MM:SS:FF".
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? s, out MsfAddress result)
    {
        return TryParse(s, null, out result);
    }

    /// <summary>
    /// Attempts to parse an MSF timecode string formatted as "MM:SS:FF" with an optional format provider.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out MsfAddress result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        string[] parts = s.Trim().Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None, provider, out int m) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None, provider, out int sec) ||
            !int.TryParse(parts[2], System.Globalization.NumberStyles.None, provider, out int f))
        {
            return false;
        }

        if (m < 0 || sec < 0 || sec >= 60 || f < 0 || f >= OpticalConstants.AudioFramesPerSecond)
        {
            return false;
        }

        result = new MsfAddress(m, sec, f);
        return true;
    }

    public override string ToString() => $"{Minutes:D2}:{Seconds:D2}:{Frames:D2}";

    public int CompareTo(MsfAddress other) => ToFrameCount().CompareTo(other.ToFrameCount());

    public static bool operator <(MsfAddress left, MsfAddress right) => left.CompareTo(right) < 0;
    public static bool operator <=(MsfAddress left, MsfAddress right) => left.CompareTo(right) <= 0;
    public static bool operator >(MsfAddress left, MsfAddress right) => left.CompareTo(right) > 0;
    public static bool operator >=(MsfAddress left, MsfAddress right) => left.CompareTo(right) >= 0;

    public static MsfAddress operator +(MsfAddress left, int frames) => FromFrameCount(left.ToFrameCount() + frames);
    public static MsfAddress operator -(MsfAddress left, int frames) => FromFrameCount(left.ToFrameCount() - frames);
    public static int operator -(MsfAddress left, MsfAddress right) => left.ToFrameCount() - right.ToFrameCount();
}
