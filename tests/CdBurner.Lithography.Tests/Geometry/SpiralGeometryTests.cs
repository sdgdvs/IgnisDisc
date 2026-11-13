using CdBurner.Lithography.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace CdBurner.Lithography.Tests.Geometry;

/// <summary>
/// <see cref="SpiralGeometry"/>: LBA / track length to radius and angle, for constant and cubic pitch models,
/// checked against closed formulas and a brute-force integral computed here.
/// </summary>
public class SpiralGeometryTests
{
    private const double StartRadius = 24.0e-3;
    private const double MaxRadius = 58.5e-3;
    private const double DvdPitch = 0.74e-6;
    private const double StartAngle = 0.3;
    private const double OneNanometer = 1.0e-9;
    private const double AngleTolerance = 1.0e-6;

    private readonly ITestOutputHelper _output;

    public SpiralGeometryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>A constant pitch wrapped as a cubic, which forces the table + Newton code path.</summary>
    private static SpiralGeometry TablePathGeometry(double pitch)
    {
        var model = new CubicTrackPitch(pitch, 0, 0, 0, 0.041, StartRadius, MaxRadius);
        return new SpiralGeometry(model, StartRadius, MaxRadius, DiscGeometryPresets.DvdSectorLengthMeters, 0, StartAngle);
    }

    private static SpiralGeometry ClosedFormGeometry(double pitch)
    {
        return new SpiralGeometry(new ConstantTrackPitch(pitch), StartRadius, MaxRadius,
            DiscGeometryPresets.DvdSectorLengthMeters, 0, StartAngle);
    }

    /// <summary>A pitch that drifts by 1 % across the band, with a little curvature on top.</summary>
    private static CubicTrackPitch DriftingPitch()
    {
        double span = MaxRadius - StartRadius;
        double a1 = 0.01 * DvdPitch / span;
        double a2 = 0.002 * DvdPitch / (span * span);
        double a3 = 0.001 * DvdPitch / (span * span * span);
        return new CubicTrackPitch(DvdPitch, a1, a2, a3, 0.5 * (StartRadius + MaxRadius), StartRadius, MaxRadius);
    }

    [Theory]
    [InlineData(0.74e-6)]
    [InlineData(0.32e-6)]
    [InlineData(1.6e-6)]
    public void TablePath_MatchesClosedForm_ForConstantPitch(double pitch)
    {
        SpiralGeometry table = TablePathGeometry(pitch);
        SpiralGeometry closed = ClosedFormGeometry(pitch);

        const int points = 69;
        for (int i = 0; i < points; i++)
        {
            double r = 24.0e-3 + (58.0e-3 - 24.0e-3) * i / (points - 1);
            double expectedLength = Math.PI * (r * r - StartRadius * StartRadius) / pitch;
            double expectedAngle = StartAngle - 2.0 * Math.PI * (r - StartRadius) / pitch;

            Assert.InRange(table.RadiusAtTrackLength(expectedLength) - r, -OneNanometer, OneNanometer);
            Assert.InRange(closed.RadiusAtTrackLength(expectedLength) - r, -OneNanometer, OneNanometer);
            Assert.InRange(table.AngleAtRadius(r) - expectedAngle, -AngleTolerance, AngleTolerance);
            Assert.InRange(closed.AngleAtRadius(r) - expectedAngle, -AngleTolerance, AngleTolerance);

            // The two paths must also agree on the track length itself (1 nm of radius is ~0.1 mm of track).
            Assert.InRange(table.TrackLengthAtRadius(r) - closed.TrackLengthAtRadius(r), -1.0e-6, 1.0e-6);
        }
    }

    [Fact]
    public void RoundTrip_RadiusToTrackLengthAndBack_IsWithinOneNanometer()
    {
        SpiralGeometry[] geometries =
        [
            ClosedFormGeometry(DvdPitch),
            TablePathGeometry(DvdPitch),
            new SpiralGeometry(DriftingPitch(), StartRadius, MaxRadius, DiscGeometryPresets.DvdSectorLengthMeters, 0, StartAngle),
        ];

        foreach (SpiralGeometry geometry in geometries)
        {
            const int points = 501;
            for (int i = 0; i < points; i++)
            {
                // Irregular spacing, including both ends exactly.
                double r = StartRadius + (MaxRadius - StartRadius) * i / (points - 1);
                double s = geometry.TrackLengthAtRadius(r);
                Assert.InRange(geometry.RadiusAtTrackLength(s) - r, -OneNanometer, OneNanometer);
            }
            Assert.Equal(0.0, geometry.TrackLengthAtRadius(StartRadius));
            Assert.Equal(StartRadius, geometry.RadiusAtTrackLength(0.0));
            Assert.Equal(StartAngle, geometry.AngleAtRadius(StartRadius));
        }
    }

    [Fact]
    public void CubicPitch_MatchesBruteForceIntegral()
    {
        CubicTrackPitch pitch = DriftingPitch();
        var geometry = new SpiralGeometry(pitch, StartRadius, MaxRadius, DiscGeometryPresets.DvdSectorLengthMeters, 0, StartAngle);

        // Reference: midpoint rule with 2,000,000 steps from 24 to 58 mm (17 nm per step),
        // with compensated (Neumaier) sums so the reference itself is not limited by rounding.
        const int steps = 2_000_000;
        const int checkEvery = 40_000;
        const double endRadius = 58.0e-3;
        double h = (endRadius - StartRadius) / steps;

        double lengthSum = 0, lengthCarry = 0, turnsSum = 0, turnsCarry = 0;
        int checkedPoints = 0;
        double worstRadiusError = 0;
        double worstAngleError = 0;
        for (int i = 0; i < steps; i++)
        {
            double rMid = StartRadius + (i + 0.5) * h;
            double p = pitch.PitchAt(rMid);
            AddCompensated(ref lengthSum, ref lengthCarry, 2.0 * Math.PI * rMid / p * h);
            AddCompensated(ref turnsSum, ref turnsCarry, h / p);

            if ((i + 1) % checkEvery == 0)
            {
                double r = StartRadius + (i + 1) * h;
                double referenceLength = lengthSum + lengthCarry;
                double referenceAngle = StartAngle - 2.0 * Math.PI * (turnsSum + turnsCarry);

                double radiusError = Math.Abs(geometry.RadiusAtTrackLength(referenceLength) - r);
                double angleError = Math.Abs(geometry.AngleAtRadius(r) - referenceAngle);
                worstRadiusError = Math.Max(worstRadiusError, radiusError);
                worstAngleError = Math.Max(worstAngleError, angleError);
                checkedPoints++;
            }
        }

        _output.WriteLine($"Cubic pitch vs brute force at {checkedPoints} radii: worst radius error {worstRadiusError:E2} m, worst angle error {worstAngleError:E2} rad");
        Assert.Equal(50, checkedPoints);
        Assert.True(worstRadiusError < OneNanometer, $"radius error {worstRadiusError} m");
        Assert.True(worstAngleError < AngleTolerance, $"angle error {worstAngleError} rad");
    }

    private static void AddCompensated(ref double sum, ref double carry, double value)
    {
        double t = sum + value;
        carry += Math.Abs(sum) >= Math.Abs(value) ? (sum - t) + value : (value - t) + sum;
        sum = t;
    }

    [Fact]
    public void NominalPresets_FullCapacityEndsNearTheOuterEdge()
    {
        double dvdEnd = EndRadiusOf(DiscGeometryPresets.DvdNominal, 2_295_104);
        double bdEnd = EndRadiusOf(DiscGeometryPresets.BdSingleLayer25GbNominal, 12_219_392);
        double cdEnd = EndRadiusOf(DiscGeometryPresets.CdNominal, 333_000);

        _output.WriteLine($"DVD  2,295,104 sectors end at {dvdEnd * 1e3:F3} mm (sector {DiscGeometryPresets.DvdSectorLengthMeters * 1e3:F4} mm)");
        _output.WriteLine($"BD  12,219,392 sectors end at {bdEnd * 1e3:F3} mm (sector {DiscGeometryPresets.BdSingleLayer25GbSectorLengthMeters * 1e3:F4} mm)");
        _output.WriteLine($"CD     333,000 sectors end at {cdEnd * 1e3:F3} mm (sector {DiscGeometryPresets.CdSectorLengthMeters * 1e3:F4} mm)");

        Assert.InRange(dvdEnd, 57.5e-3, 58.5e-3);
        Assert.InRange(bdEnd, 57.5e-3, 58.5e-3);
        Assert.InRange(cdEnd, 57.0e-3, 58.5e-3);

        Assert.Equal(16.0e-3, DiscGeometryPresets.CdSectorLengthMeters, 12);
        Assert.Equal(5.157e-3, DiscGeometryPresets.DvdSectorLengthMeters, 6);
        Assert.Equal(2.231e-3, DiscGeometryPresets.BdSingleLayer25GbSectorLengthMeters, 6);
    }

    private static double EndRadiusOf(SpiralGeometry geometry, long sectorCount)
    {
        // End of the last sector = fraction 1.0 of the last LBA.
        return geometry.PositionAtLba(geometry.LbaAtStartRadius + sectorCount - 1, 1.0).RadiusMeters;
    }

    [Fact]
    public void Dvd_TurnsBetween24And58mm_MatchSpanOverPitch()
    {
        SpiralGeometry dvd = DiscGeometryPresets.DvdNominal;
        double turns = (dvd.AngleAtRadius(24.0e-3) - dvd.AngleAtRadius(58.0e-3)) / (2.0 * Math.PI);
        double expected = 34.0e-3 / 0.74e-6;

        _output.WriteLine($"DVD turns 24 to 58 mm: {turns:F2} (expected {expected:F2})");
        Assert.InRange(turns / expected, 1.0 - 1.0e-4, 1.0 + 1.0e-4);
    }

    [Fact]
    public void Angle_StrictlyDecreasesWithLba_AndLabelSideViewMirrors()
    {
        SpiralGeometry dvd = DiscGeometryPresets.DvdNominal;
        double previousAngle = dvd.PositionAtLba(0).AngleRadians;
        double previousRadius = dvd.PositionAtLba(0).RadiusMeters;
        Assert.Equal(DiscGeometryPresets.DvdStartRadiusMeters, previousRadius, 15);

        for (long lba = 1; lba < 2_295_104; lba += 997)
        {
            DiscPosition position = dvd.PositionAtLba(lba);
            Assert.True(position.AngleRadians < previousAngle, $"angle did not decrease at LBA {lba}");
            Assert.True(position.RadiusMeters > previousRadius, $"radius did not grow at LBA {lba}");
            previousAngle = position.AngleRadians;
            previousRadius = position.RadiusMeters;
        }

        // Neighbouring sectors too, not only far-apart ones.
        Assert.True(dvd.PositionAtLba(1_000_001).AngleRadians < dvd.PositionAtLba(1_000_000).AngleRadians);
        Assert.True(dvd.PositionAtLba(1_000_000, 0.5).AngleRadians < dvd.PositionAtLba(1_000_000).AngleRadians);
        Assert.Equal(-1.0, DiscOrientation.TrackAngleSign);

        var point = new DiscPosition(0.04, 1.25);
        DiscPosition mirrored = point.ToLabelSideView();
        Assert.Equal(0.04, mirrored.RadiusMeters);
        Assert.Equal(-1.25, mirrored.AngleRadians);
        Assert.Equal(point.X, mirrored.X, 15);
        Assert.Equal(-point.Y, mirrored.Y, 15);
        Assert.Equal(0.04 * Math.Cos(1.25), point.X, 15);
        Assert.Equal(0.04 * Math.Sin(1.25), point.Y, 15);
    }

    [Fact]
    public void PositionAtLba_UsesTheStartLbaAndFraction()
    {
        var geometry = new SpiralGeometry(new ConstantTrackPitch(DvdPitch), StartRadius, MaxRadius, 5.0e-3, 1000, StartAngle);

        Assert.Equal(StartRadius, geometry.PositionAtLba(1000).RadiusMeters, 15);
        Assert.Equal(StartAngle, geometry.PositionAtLba(1000).AngleRadians, 12);
        DiscPosition byLength = geometry.PositionAtTrackLength(12.5e-3);
        DiscPosition byLba = geometry.PositionAtLba(1002, 0.5);
        Assert.Equal(byLength.RadiusMeters, byLba.RadiusMeters, 15);
        Assert.Equal(byLength.AngleRadians, byLba.AngleRadians, 9);

        // LastLbaInside: the sector after it must already cross the radius.
        double r = 40.0e-3;
        long last = geometry.LastLbaInside(r);
        Assert.True(geometry.PositionAtLba(last, 1.0).RadiusMeters <= r);
        Assert.True(geometry.PositionAtLba(last + 1, 1.0).RadiusMeters > r);
        Assert.Equal(999, geometry.LastLbaInside(StartRadius));
    }

    [Fact]
    public void InvalidInputs_Throw()
    {
        SpiralGeometry dvd = DiscGeometryPresets.DvdNominal;
        SpiralGeometry table = TablePathGeometry(DvdPitch);

        // Pitch models.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstantTrackPitch(-0.74e-6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstantTrackPitch(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstantTrackPitch(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CubicTrackPitch(-0.74e-6, 0, 0, 0, 0.041, StartRadius, MaxRadius));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CubicTrackPitch(double.NaN, 0, 0, 0, 0.041, StartRadius, MaxRadius));
        // Positive at the start, negative at the end.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CubicTrackPitch(0.74e-6, -1.0e-4, 0, 0, StartRadius, StartRadius, MaxRadius));
        // Positive at both ends, negative in the middle.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CubicTrackPitch(-1.0e-7, 0, 1.0e-2, 0, 0.041, StartRadius, MaxRadius));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CubicTrackPitch(0.74e-6, 0, 0, 0, 0.041, MaxRadius, StartRadius));

        // Geometry construction.
        var pitch = new ConstantTrackPitch(DvdPitch);
        Assert.Throws<ArgumentNullException>(() => new SpiralGeometry(null!, StartRadius, MaxRadius, 5e-3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpiralGeometry(pitch, double.NaN, MaxRadius, 5e-3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpiralGeometry(pitch, -StartRadius, MaxRadius, 5e-3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpiralGeometry(pitch, MaxRadius, StartRadius, 5e-3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpiralGeometry(pitch, StartRadius, double.PositiveInfinity, 5e-3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpiralGeometry(pitch, StartRadius, MaxRadius, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpiralGeometry(pitch, StartRadius, MaxRadius, 5e-3, 0, double.NaN));
        // A pitch model that does not cover the whole spiral.
        var narrow = new CubicTrackPitch(DvdPitch, 0, 0, 0, 0.041, 30.0e-3, 50.0e-3);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpiralGeometry(narrow, StartRadius, MaxRadius, 5e-3, 0, 0));

        // Queries, on both code paths.
        foreach (SpiralGeometry geometry in new[] { dvd, table })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.TrackLengthAtRadius(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.TrackLengthAtRadius(23.9e-3));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.TrackLengthAtRadius(58.6e-3));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.AngleAtRadius(58.6e-3));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.AngleAtRadius(double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.LastLbaInside(0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.RadiusAtTrackLength(-1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.RadiusAtTrackLength(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.RadiusAtTrackLength(geometry.TotalTrackLengthMeters * 1.001));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.PositionAtTrackLength(geometry.TotalTrackLengthMeters + 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.PositionAtLba(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.PositionAtLba(0, 1.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.PositionAtLba(0, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.PositionAtLba(long.MaxValue));
        }
    }
}
