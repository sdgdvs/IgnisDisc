using CdBurner.Core.Primitives;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Measures how fast a burn really wrote. Windows' writer reports the speed it was asked to use, which can be far
/// from what the drive achieved (a CD-RW set to 10x wrote at about 6.2x). This takes the time and sector count at the
/// first and last progress reports made while data was being written, so preparing the drive and closing the disc do
/// not count, and divides one by the other.
/// </summary>
public sealed class WriteSpeedMeter
{
    private readonly object _gate = new();
    private (TimeSpan Time, int Sectors)? _first;
    private (TimeSpan Time, int Sectors)? _last;

    /// <summary>Records one progress report. Reports outside the data-writing phase, or with no sectors yet, are ignored.</summary>
    /// <param name="writingData">True while the writer is in its data-writing phase.</param>
    /// <param name="sectorsWritten">Sectors written so far.</param>
    /// <param name="elapsed">Time since the burn started, from one clock for the whole burn.</param>
    public void Observe(bool writingData, int sectorsWritten, TimeSpan elapsed)
    {
        if (!writingData || sectorsWritten <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _first ??= (elapsed, sectorsWritten);
            if (_last is null || sectorsWritten >= _last.Value.Sectors)
            {
                _last = (elapsed, sectorsWritten);
            }
        }
    }

    /// <summary>
    /// Sectors written a second during the data-writing phase, or null when the reports span less than a second or
    /// show no progress (too little to measure from).
    /// </summary>
    public double? SectorsPerSecond
    {
        get
        {
            lock (_gate)
            {
                if (_first is not { } first || _last is not { } last)
                {
                    return null;
                }
                double seconds = (last.Time - first.Time).TotalSeconds;
                int sectors = last.Sectors - first.Sectors;
                return seconds >= 1.0 && sectors > 0 ? sectors / seconds : null;
            }
        }
    }

    /// <summary>
    /// A data-sector rate as an "x" figure for the disc: CD 1x = 75 data sectors a second (153.6 kB/s, the units Windows'
    /// writer uses), DVD 1x = 1,385 kB/s, Blu-ray 1x = 4,495.5 kB/s.
    /// </summary>
    public static double DataMultiplier(double sectorsPerSecond, DiscMediaType mediaType)
    {
        int baseRate = WriteSpeedDescriptor.GetBaseRate(mediaType, isAudio: false);
        return baseRate > 0 && sectorsPerSecond > 0 ? sectorsPerSecond * OpticalConstants.DataSectorSize / baseRate : 0;
    }

    /// <summary>The average over a whole burn, for when no usable progress reports came in. Includes preparing and closing.</summary>
    public static double WholeBurnSectorsPerSecond(int sectors, TimeSpan duration) =>
        duration.TotalSeconds > 0 && sectors > 0 ? sectors / duration.TotalSeconds : 0;
}
