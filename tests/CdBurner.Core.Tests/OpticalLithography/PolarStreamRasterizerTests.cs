using CdBurner.Core.OpticalLithography;
using Xunit;
using Xunit.Abstractions;

namespace CdBurner.Core.Tests.OpticalLithography;

/// <summary>
/// <see cref="PolarStreamRasterizer"/>: samples land on the right page, the amount of ink matches the page
/// area, and the hot path makes no heap allocations.
/// </summary>
public class PolarStreamRasterizerTests
{
    private const int SamplesPerSector = 64;

    private readonly ITestOutputHelper _output;

    public PolarStreamRasterizerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static PolarPageLayout CreateLayout()
    {
        return new PolarPageLayout(38.0e-3, 58.0e-3, 1.5e-3, 1.5e-3, 0.2e-3, 0.2e-3, columns: 80, rows: 60);
    }

    /// <summary>Inks every point of exactly one page.</summary>
    private sealed class SinglePageSource : IPagePixelSource
    {
        private readonly int _ring;
        private readonly int _sector;

        public SinglePageSource(int ring, int sector)
        {
            _ring = ring;
            _sector = sector;
        }

        public bool IsInk(int ring, int sector, double uMeters, double vMeters)
        {
            return ring == _ring && sector == _sector;
        }
    }

    /// <summary>Inks every other content cell, like a chess board, on every page.</summary>
    private sealed class CheckerboardSource : IPagePixelSource
    {
        private readonly PolarPageLayout _layout;

        public CheckerboardSource(PolarPageLayout layout)
        {
            _layout = layout;
        }

        public bool IsInk(int ring, int sector, double uMeters, double vMeters)
        {
            return ((_layout.CellColumnAt(uMeters) + _layout.CellRowAt(vMeters)) & 1) == 0;
        }
    }

    [Fact]
    public void Rasterize_10000DvdSectors_AllocatesNothing()
    {
        SpiralGeometry geometry = DiscGeometryPresets.DvdNominal;
        PolarPageLayout layout = CreateLayout();
        var rasterizer = new PolarStreamRasterizer(geometry, layout, new CheckerboardSource(layout), SamplesPerSector);

        const int sectorCount = 10_000;
        long firstLba = geometry.LastLbaInside(38.0e-3) + 1;
        byte[] buffer = new byte[sectorCount * SamplesPerSector];

        // Warm-up: lets the JIT compile everything before measuring.
        rasterizer.RasterizeRange(firstLba, sectorCount, buffer);
        rasterizer.RasterizeSector(firstLba, buffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        rasterizer.RasterizeRange(firstLba, sectorCount, buffer);
        rasterizer.RasterizeSector(firstLba, buffer);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        int ink = 0;
        foreach (byte sample in buffer)
        {
            Assert.True(sample <= 1);
            ink += sample;
        }
        _output.WriteLine($"Allocated {allocated} bytes for {sectorCount} sectors; {ink} of {buffer.Length} samples are ink");

        Assert.Equal(0, allocated);
        // The checkerboard covers about half of the page area, and pages about 78 % of the band here.
        Assert.InRange(ink, buffer.Length / 4, buffer.Length / 2);
    }

    [Fact]
    public void Rasterize_TablePath_AllocatesNothing()
    {
        // A non-constant pitch model uses the table + Newton path; that path must not allocate either.
        var pitch = new CubicTrackPitch(0.74e-6, 2.0e-7, 0, 0, 0.041, 24.0e-3, 58.5e-3);
        var geometry = new SpiralGeometry(pitch, 24.0e-3, 58.5e-3, DiscGeometryPresets.DvdSectorLengthMeters, 0, 0.0);
        PolarPageLayout layout = CreateLayout();
        var rasterizer = new PolarStreamRasterizer(geometry, layout, new CheckerboardSource(layout), SamplesPerSector);

        const int sectorCount = 1_000;
        long firstLba = geometry.LastLbaInside(38.0e-3) + 1;
        byte[] buffer = new byte[sectorCount * SamplesPerSector];
        rasterizer.RasterizeRange(firstLba, sectorCount, buffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        rasterizer.RasterizeRange(firstLba, sectorCount, buffer);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Contains((byte)1, buffer);
    }

    [Fact]
    public void Rasterize_SinglePage_InkLiesInsideThePageAndMatchesItsArea()
    {
        const int ring = 5;
        const int sector = 7;
        SpiralGeometry geometry = DiscGeometryPresets.DvdNominal;
        PolarPageLayout layout = CreateLayout();
        var rasterizer = new PolarStreamRasterizer(geometry, layout, new SinglePageSource(ring, sector), SamplesPerSector);
        PageBounds bounds = layout.GetBounds(ring, sector);

        // Only the sectors whose track crosses this ring, with one sector of margin on each side.
        long firstLba = Math.Max(geometry.LbaAtStartRadius, geometry.LastLbaInside(bounds.InnerRadiusMeters));
        long lastLba = geometry.LastLbaInside(bounds.OuterRadiusMeters) + 2;

        const double radiusSlack = 1.0e-9; // 1 nm
        const double angleSlack = 1.0e-9;  // rad
        byte[] samples = new byte[SamplesPerSector];
        long inkCount = 0;

        for (long lba = firstLba; lba <= lastLba; lba++)
        {
            rasterizer.RasterizeSector(lba, samples);
            for (int i = 0; i < SamplesPerSector; i++)
            {
                if (samples[i] == 0)
                {
                    continue;
                }
                inkCount++;

                // Recompute where this sample is, independently of the rasterizer's own arithmetic.
                DiscPosition position = geometry.PositionAtLba(lba, (i + 0.5) / SamplesPerSector);
                double angle = PolarPageLayout.WrapAngle(position.AngleRadians);
                bool inside =
                    position.RadiusMeters >= bounds.InnerRadiusMeters - radiusSlack
                    && position.RadiusMeters <= bounds.OuterRadiusMeters + radiusSlack
                    && angle >= bounds.StartAngleRadians - angleSlack
                    && angle <= bounds.EndAngleRadians + angleSlack;
                Assert.True(inside, $"ink sample {i} of LBA {lba} at r={position.RadiusMeters}, θ={angle} is outside the page");
            }
        }

        // Each sample stands for a patch of pitch × sample spacing, so ink count ≈ page area / patch area.
        double pageArea = layout.PageTangentialWidthMeters * layout.PageRadialHeightMeters;
        double expected = pageArea / (DiscGeometryPresets.DvdTrackPitchMeters * rasterizer.SampleSpacingMeters);
        _output.WriteLine($"Page {PolarPageLayout.PageLabel(ring, sector)}: {inkCount} ink samples over LBA {firstLba}..{lastLba}, expected about {expected:F0}");

        Assert.InRange(inkCount, expected * 0.95, expected * 1.05);
    }

    [Fact]
    public void RasterizeRange_EqualsSectorBySector_AndSamplesAreInTrackOrder()
    {
        SpiralGeometry geometry = DiscGeometryPresets.DvdNominal;
        PolarPageLayout layout = CreateLayout();
        var source = new CheckerboardSource(layout);
        var rasterizer = new PolarStreamRasterizer(geometry, layout, source, SamplesPerSector);

        const int sectorCount = 200;
        long firstLba = geometry.LastLbaInside(45.0e-3);
        byte[] range = new byte[sectorCount * SamplesPerSector];
        byte[] single = new byte[SamplesPerSector];
        rasterizer.RasterizeRange(firstLba, sectorCount, range);

        for (int s = 0; s < sectorCount; s++)
        {
            rasterizer.RasterizeSector(firstLba + s, single);
            Assert.True(single.AsSpan().SequenceEqual(range.AsSpan(s * SamplesPerSector, SamplesPerSector)));

            // Every sample must equal what the layout and source say at the documented track position.
            for (int i = 0; i < SamplesPerSector; i++)
            {
                double trackLength = (firstLba + s) * geometry.SectorLengthMeters
                    + (i + 0.5) * geometry.SectorLengthMeters / SamplesPerSector;
                DiscPosition position = geometry.PositionAtTrackLength(trackLength);
                bool expectedInk = layout.TryLocate(position.RadiusMeters, position.AngleRadians, out PageHit hit)
                    && source.IsInk(hit.Ring, hit.Sector, hit.UMeters, hit.VMeters);
                Assert.Equal(expectedInk ? 1 : 0, single[i]);
            }
        }
    }

    [Fact]
    public void Rasterize_OutsideTheBand_WritesZeros()
    {
        SpiralGeometry geometry = DiscGeometryPresets.DvdNominal;
        PolarPageLayout layout = CreateLayout();
        var rasterizer = new PolarStreamRasterizer(geometry, layout, new CheckerboardSource(layout), SamplesPerSector);

        // LBA 0 is at 24 mm, far inside the 38 mm inner edge of the band.
        byte[] buffer = new byte[SamplesPerSector * 4];
        Array.Fill(buffer, (byte)0xFF);
        rasterizer.RasterizeRange(0, 4, buffer);
        Assert.All(buffer, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void InvalidInputs_Throw()
    {
        SpiralGeometry geometry = DiscGeometryPresets.DvdNominal;
        PolarPageLayout layout = CreateLayout();
        var source = new CheckerboardSource(layout);

        Assert.Throws<ArgumentNullException>(() => new PolarStreamRasterizer(null!, layout, source, 64));
        Assert.Throws<ArgumentNullException>(() => new PolarStreamRasterizer(geometry, null!, source, 64));
        Assert.Throws<ArgumentNullException>(() => new PolarStreamRasterizer(geometry, layout, null!, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarStreamRasterizer(geometry, layout, source, 0));

        var rasterizer = new PolarStreamRasterizer(geometry, layout, source, 64);
        long lastWholeLba = geometry.LastLbaInside(geometry.MaxRadiusMeters);

        Assert.Throws<ArgumentException>(() => rasterizer.RasterizeSector(0, new byte[63]));
        Assert.Throws<ArgumentException>(() => rasterizer.RasterizeRange(0, 2, new byte[127]));
        Assert.Throws<ArgumentOutOfRangeException>(() => rasterizer.RasterizeRange(0, -1, new byte[64]));
        Assert.Throws<ArgumentOutOfRangeException>(() => rasterizer.RasterizeSector(-1, new byte[64]));
        Assert.Throws<ArgumentOutOfRangeException>(() => rasterizer.RasterizeSector(lastWholeLba + 2, new byte[64]));

        // The last whole sector on the disc is fine.
        rasterizer.RasterizeSector(lastWholeLba - 1, new byte[64]);
    }
}
