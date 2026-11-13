using CdBurner.Lithography.Geometry;
using Xunit;

namespace CdBurner.Lithography.Tests.Geometry;

/// <summary>
/// <see cref="PolarPageLayout"/>: a 38–58 mm band of 1.5 mm pages with 0.2 mm gutters.
/// </summary>
public class PolarPageLayoutTests
{
    private const double Inner = 38.0e-3;
    private const double Outer = 58.0e-3;
    private const double PageSize = 1.5e-3;
    private const double Gutter = 0.2e-3;
    private const double TwoPi = 2.0 * Math.PI;

    private static PolarPageLayout CreateLayout()
    {
        return new PolarPageLayout(Inner, Outer, PageSize, PageSize, Gutter, Gutter, columns: 80, rows: 60);
    }

    [Fact]
    public void RingAndSectorCounts_FollowTheFormulas()
    {
        PolarPageLayout layout = CreateLayout();

        // (20 mm + 0.2 mm) / 1.7 mm = 11.88 → 11 rings.
        Assert.Equal(11, layout.RingCount);

        long total = 0;
        int previous = 0;
        for (int ring = 0; ring < layout.RingCount; ring++)
        {
            double midRadius = Inner + ring * (PageSize + Gutter) + PageSize / 2;
            int expected = (int)Math.Floor(TwoPi * midRadius / (PageSize + Gutter));
            Assert.Equal(expected, layout.SectorsInRing(ring));
            Assert.True(layout.SectorsInRing(ring) >= previous, "pages per ring must never decrease outward");
            previous = layout.SectorsInRing(ring);
            total += expected;
        }
        Assert.Equal(total, layout.TotalPages);
        Assert.Equal(143, layout.SectorsInRing(0)); // 2π·38.75 mm / 1.7 mm = 143.2
    }

    [Fact]
    public void TryLocate_AtEveryPageCentre_ReturnsThatPage()
    {
        PolarPageLayout layout = CreateLayout();
        long pagesChecked = 0;

        for (int ring = 0; ring < layout.RingCount; ring++)
        {
            for (int sector = 0; sector < layout.SectorsInRing(ring); sector++)
            {
                PageBounds bounds = layout.GetBounds(ring, sector);
                Assert.Equal(PageSize, bounds.OuterRadiusMeters - bounds.InnerRadiusMeters, 12);
                Assert.Equal(PageSize, (bounds.EndAngleRadians - bounds.StartAngleRadians) * bounds.MidRadiusMeters, 12);

                Assert.True(layout.TryLocate(bounds.MidRadiusMeters, bounds.MidAngleRadians, out PageHit hit));
                Assert.Equal(ring, hit.Ring);
                Assert.Equal(sector, hit.Sector);
                Assert.Equal(PageSize / 2, hit.UMeters, 12);
                Assert.Equal(PageSize / 2, hit.VMeters, 12);
                pagesChecked++;
            }
        }
        Assert.Equal(layout.TotalPages, pagesChecked);
    }

    [Fact]
    public void TryLocate_InGuttersAndOutsideTheBand_ReturnsFalse()
    {
        PolarPageLayout layout = CreateLayout();

        for (int ring = 0; ring < layout.RingCount; ring++)
        {
            int sectors = layout.SectorsInRing(ring);
            foreach (int sector in new[] { 0, sectors / 2, sectors - 1 })
            {
                PageBounds bounds = layout.GetBounds(ring, sector);
                double gapAngle = TwoPi / sectors - (bounds.EndAngleRadians - bounds.StartAngleRadians);
                Assert.True(gapAngle * bounds.MidRadiusMeters >= Gutter - 1e-12, "gap must be at least the requested gutter");

                // Tangential gutter after the page, and radial gutter outside it.
                Assert.False(layout.TryLocate(bounds.MidRadiusMeters, bounds.EndAngleRadians + gapAngle / 2, out _));
                Assert.False(layout.TryLocate(bounds.OuterRadiusMeters + Gutter / 2, bounds.MidAngleRadians, out _));

                // Just inside each edge is still the page.
                Assert.True(layout.TryLocate(bounds.MidRadiusMeters, bounds.EndAngleRadians - gapAngle / 100, out PageHit nearEnd));
                Assert.Equal(sector, nearEnd.Sector);
                Assert.True(layout.TryLocate(bounds.OuterRadiusMeters - 1e-6, bounds.MidAngleRadians, out PageHit nearTop));
                Assert.Equal(ring, nearTop.Ring);
                Assert.Equal(PageSize - 1e-6, nearTop.VMeters, 12);
            }
        }

        // Outside the band: inside the inner edge, between the last ring (ends at 56.5 mm) and 58 mm, beyond 58 mm.
        Assert.False(layout.TryLocate(37.9e-3, 1.0, out _));
        Assert.False(layout.TryLocate(57.0e-3, 1.0, out _));
        Assert.False(layout.TryLocate(58.1e-3, 1.0, out _));
        Assert.False(layout.TryLocate(0.0, 1.0, out _));
        Assert.False(layout.TryLocate(double.NaN, 1.0, out _));
        Assert.False(layout.TryLocate(40.0e-3, double.NaN, out _));
        Assert.False(layout.TryLocate(40.0e-3, double.PositiveInfinity, out _));
    }

    [Fact]
    public void TryLocate_HandlesAngleWrapAround()
    {
        PolarPageLayout layout = CreateLayout();
        int lastSector = layout.SectorsInRing(0) - 1;
        PageBounds first = layout.GetBounds(0, 0);
        PageBounds last = layout.GetBounds(0, lastSector);
        double r = first.MidRadiusMeters;

        // The same point given one turn back, two turns forward, and many turns back (like the spiral angle).
        foreach (double turns in new[] { -1.0, 2.0, -45_000.0 })
        {
            Assert.True(layout.TryLocate(r, last.MidAngleRadians + turns * TwoPi, out PageHit hit));
            Assert.Equal(lastSector, hit.Sector);
            Assert.Equal(PageSize / 2, hit.UMeters, 9);

            Assert.True(layout.TryLocate(r, first.MidAngleRadians + turns * TwoPi, out hit));
            Assert.Equal(0, hit.Sector);
            Assert.Equal(PageSize / 2, hit.UMeters, 9);
        }

        // Exactly 0 and exactly 2π are the start of page 0.
        Assert.True(layout.TryLocate(r, 0.0, out PageHit atZero));
        Assert.Equal(0, atZero.Sector);
        Assert.Equal(0.0, atZero.UMeters, 12);
        Assert.True(layout.TryLocate(r, TwoPi, out PageHit atFullTurn));
        Assert.Equal(0, atFullTurn.Sector);

        // A hair below 0 is the gutter after the last page.
        Assert.False(layout.TryLocate(r, -1.0e-6, out _));
        // A negative angle too small for a double to tell apart from 0 counts as 0 (start of page 0);
        // it must not crash or index past the last page.
        Assert.True(layout.TryLocate(r, -1.0e-300, out PageHit tinyNegative));
        Assert.Equal(0, tinyNegative.Sector);
        Assert.Equal(0.0, tinyNegative.UMeters);
        Assert.Equal(0.0, PolarPageLayout.WrapAngle(-1.0e-300));
        Assert.Equal(TwoPi - 1.0, PolarPageLayout.WrapAngle(-1.0), 12);
        Assert.Equal(1.0, PolarPageLayout.WrapAngle(1.0 + 3 * TwoPi), 12);
    }

    [Fact]
    public void PageLabel_UsesAtLeastTwoDigits()
    {
        Assert.Equal("R04-S18", PolarPageLayout.PageLabel(4, 18));
        Assert.Equal("R04-S123", PolarPageLayout.PageLabel(4, 123));
        Assert.Equal("R00-S00", PolarPageLayout.PageLabel(0, 0));
        Assert.Equal("R120-S07", PolarPageLayout.PageLabel(120, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => PolarPageLayout.PageLabel(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PolarPageLayout.PageLabel(0, -1));
    }

    [Fact]
    public void CellHelpers_MapPagePositionToColumnAndRow()
    {
        PolarPageLayout layout = CreateLayout();

        Assert.Equal(0, layout.CellColumnAt(0.0));
        Assert.Equal(20, layout.CellColumnAt(PageSize * 20.5 / 80));
        Assert.Equal(79, layout.CellColumnAt(PageSize * 0.999));
        Assert.Equal(79, layout.CellColumnAt(PageSize)); // the far edge belongs to the last cell

        Assert.Equal(0, layout.CellRowAt(0.0));
        Assert.Equal(30, layout.CellRowAt(PageSize * 30.5 / 60));
        Assert.Equal(59, layout.CellRowAt(PageSize));
    }

    [Fact]
    public void InvalidInputs_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(double.NaN, Outer, PageSize, PageSize, Gutter, Gutter, 80, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Outer, Inner, PageSize, PageSize, Gutter, Gutter, 80, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Inner, Outer, 0, PageSize, Gutter, Gutter, 80, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Inner, Outer, PageSize, -PageSize, Gutter, Gutter, 80, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Inner, Outer, PageSize, PageSize, -Gutter, Gutter, 80, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Inner, Outer, PageSize, PageSize, Gutter, double.NaN, 80, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Inner, Outer, PageSize, PageSize, Gutter, Gutter, 0, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Inner, Outer, PageSize, PageSize, Gutter, Gutter, 80, 0));
        // Page taller than the band, and page wider than the circumference.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Inner, Outer, 25.0e-3, PageSize, Gutter, Gutter, 80, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarPageLayout(Inner, Outer, PageSize, 1.0, Gutter, Gutter, 80, 60));

        PolarPageLayout layout = CreateLayout();
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SectorsInRing(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.SectorsInRing(layout.RingCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetBounds(0, layout.SectorsInRing(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetBounds(layout.RingCount, 0));
    }

    [Fact]
    public void ZeroGutters_TileWithoutGaps()
    {
        // 20 mm band of 2 mm pages with no radial gutter: exactly 10 rings.
        var layout = new PolarPageLayout(Inner, Outer, 2.0e-3, 2.0e-3, 0, 0, 10, 10);
        Assert.Equal(10, layout.RingCount);
        Assert.True(layout.TryLocate(Inner, 0.0, out PageHit hit));
        Assert.Equal(0, hit.Ring);
        Assert.True(layout.TryLocate(Inner + 3.0e-3, 0.0, out hit));
        Assert.Equal(1, hit.Ring);
        Assert.Equal(1.0e-3, hit.VMeters, 12);
    }
}
