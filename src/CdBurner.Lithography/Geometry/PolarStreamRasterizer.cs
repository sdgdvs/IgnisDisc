namespace CdBurner.Lithography.Geometry;

/// <summary>
/// Walks along the track and reports, sample by sample, whether the page artwork has ink under the track.
/// <para>
/// Output is in PHYSICAL track order: sample i of a sector sits at track length
/// s(lba) + (i + 0.5)·sectorLength / samplesPerSector. It is the encoder's job, not this module's, to map
/// physical positions to user bytes, because CD interleaving and DVD/BD scrambling and error correction
/// reorder them.
/// </para>
/// <para>
/// Uses constant memory and makes no heap allocations after construction. It needs no scratch buffers,
/// so there is nothing to dispose. Safe to use from several threads if the pixel source is.
/// </para>
/// </summary>
public sealed class PolarStreamRasterizer
{
    private readonly SpiralGeometry _geometry;
    private readonly PolarPageLayout _layout;
    private readonly IPagePixelSource _pixelSource;
    private readonly double _sampleSpacingMeters;

    /// <summary>Creates a rasterizer.</summary>
    /// <param name="geometry">Where each LBA sits on the disc.</param>
    /// <param name="layout">How the band is divided into pages.</param>
    /// <param name="pixelSource">The artwork: ink or no ink at a point of a page.</param>
    /// <param name="samplesPerSector">How many evenly spaced samples to take along one sector; at least 1.</param>
    public PolarStreamRasterizer(
        SpiralGeometry geometry,
        PolarPageLayout layout,
        IPagePixelSource pixelSource,
        int samplesPerSector)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(pixelSource);
        if (samplesPerSector < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesPerSector), samplesPerSector,
                "At least one sample per sector is needed.");
        }
        _geometry = geometry;
        _layout = layout;
        _pixelSource = pixelSource;
        SamplesPerSector = samplesPerSector;
        _sampleSpacingMeters = geometry.SectorLengthMeters / samplesPerSector;
    }

    /// <summary>Number of samples written for each sector.</summary>
    public int SamplesPerSector { get; }

    /// <summary>Distance along the track between two samples, in meters.</summary>
    public double SampleSpacingMeters => _sampleSpacingMeters;

    /// <summary>
    /// Writes one byte per sample for one sector: 1 = ink, 0 = no ink. Samples outside the band or in a
    /// gutter are 0. Only the first <see cref="SamplesPerSector"/> bytes of the destination are written.
    /// </summary>
    /// <param name="lba">The sector to sample. Must lie on the spiral.</param>
    /// <param name="destination">Receives the samples; needs room for <see cref="SamplesPerSector"/> bytes.</param>
    public void RasterizeSector(long lba, Span<byte> destination)
    {
        if (destination.Length < SamplesPerSector)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} bytes but one sector needs {SamplesPerSector}.",
                nameof(destination));
        }

        // Check both ends of the sector up front, so a bad LBA fails before anything is written.
        double sectorStart = _geometry.TrackLengthAtLba(lba);
        double sectorEnd = sectorStart + _geometry.SectorLengthMeters;
        if (sectorEnd > _geometry.TotalTrackLengthMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(lba), lba,
                "The sector reaches past the end of the spiral (the maximum radius).");
        }

        for (int i = 0; i < SamplesPerSector; i++)
        {
            double trackLength = sectorStart + (i + 0.5) * _sampleSpacingMeters;
            DiscPosition position = _geometry.PositionAtTrackLength(trackLength);

            byte value = 0;
            if (_layout.TryLocate(position.RadiusMeters, position.AngleRadians, out PageHit hit))
            {
                if (_pixelSource.IsInk(hit.Ring, hit.Sector, hit.UMeters, hit.VMeters))
                {
                    value = 1;
                }
            }
            destination[i] = value;
        }
    }

    /// <summary>
    /// Writes the samples of several sectors in a row, <see cref="SamplesPerSector"/> bytes each,
    /// in physical track order.
    /// </summary>
    /// <param name="firstLba">The first sector to sample.</param>
    /// <param name="sectorCount">How many sectors; zero or more.</param>
    /// <param name="destination">Receives the samples; needs room for sectorCount × <see cref="SamplesPerSector"/> bytes.</param>
    public void RasterizeRange(long firstLba, int sectorCount, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sectorCount);
        long bytesNeeded = (long)sectorCount * SamplesPerSector;
        if (destination.Length < bytesNeeded)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} bytes but {sectorCount} sectors need {bytesNeeded}.",
                nameof(destination));
        }

        for (int sector = 0; sector < sectorCount; sector++)
        {
            RasterizeSector(firstLba + sector, destination.Slice(sector * SamplesPerSector, SamplesPerSector));
        }
    }
}
