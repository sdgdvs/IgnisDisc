using CdBurner.Core.Archival;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>M1.5: Blu-ray, BDXL and M-DISC media types, their capacities, and the layer split of data discs.</summary>
public class BluRayMediaTests
{
    [Theory]
    [InlineData(DiscMediaType.BdRom, "BD-ROM", 12_219_392, 1, false)]
    [InlineData(DiscMediaType.BdR, "BD-R", 12_219_392, 1, true)]
    [InlineData(DiscMediaType.BdRe, "BD-RE", 11_826_176, 1, true)]
    [InlineData(DiscMediaType.BdRDualLayer, "BD-R DL", 24_438_784, 2, true)]
    [InlineData(DiscMediaType.BdReDualLayer, "BD-RE DL", 23_652_352, 2, true)]
    [InlineData(DiscMediaType.BdxlRTripleLayer, "BD-R XL (3 layers)", 48_878_592, 3, true)]
    [InlineData(DiscMediaType.BdxlRQuadLayer, "BD-R XL (4 layers)", 62_500_864, 4, true)]
    [InlineData(DiscMediaType.BdxlReTripleLayer, "BD-RE XL (3 layers)", 48_878_592, 3, true)]
    public void EveryBluRayProfile_HasItsNameCapacityAndLayers(DiscMediaType type, string name, int sectors, int layers, bool writable)
    {
        Assert.Equal(name, type.GetDisplayName());
        Assert.Equal(sectors, type.GetNominalCapacitySectors());
        Assert.Equal((long)sectors * 2048, type.GetNominalCapacityBytes());
        Assert.Equal(layers, type.GetLayerCount());
        Assert.Equal(layers > 1, type.IsMultiLayer());
        Assert.Equal(layers > 2, type.IsBdxl());
        Assert.Equal(writable, type.IsWritable());
        Assert.True(type.IsBluRay());
        Assert.False(type.IsDvd());
        Assert.False(type.IsCd());
    }

    [Fact]
    public void Rewritable_IsOnlyTheReTypes()
    {
        Assert.True(DiscMediaType.BdRe.IsRewritable());
        Assert.True(DiscMediaType.BdReDualLayer.IsRewritable());
        Assert.True(DiscMediaType.BdxlReTripleLayer.IsRewritable());
        Assert.False(DiscMediaType.BdR.IsRewritable());
        Assert.False(DiscMediaType.BdxlRQuadLayer.IsRewritable());
    }

    [Fact]
    public void DvdTypes_AreNotBluRay_AndKeepTheirLayerCounts()
    {
        Assert.False(DiscMediaType.DvdPlusRDualLayer.IsBluRay());
        Assert.Equal(2, DiscMediaType.DvdPlusRDualLayer.GetLayerCount());
        Assert.Equal(1, DiscMediaType.DvdPlusR.GetLayerCount());
        Assert.Equal(1, DiscMediaType.CdR.GetLayerCount());
    }

    [Theory]
    [InlineData(DiscMediaType.BdR, 12_219_392, DiscMediaType.BdR)]
    [InlineData(DiscMediaType.BdR, 24_438_784, DiscMediaType.BdRDualLayer)]
    [InlineData(DiscMediaType.BdR, 48_878_592, DiscMediaType.BdxlRTripleLayer)]
    [InlineData(DiscMediaType.BdR, 62_500_864, DiscMediaType.BdxlRQuadLayer)]
    [InlineData(DiscMediaType.BdRe, 11_826_176, DiscMediaType.BdRe)]
    [InlineData(DiscMediaType.BdRe, 12_219_392, DiscMediaType.BdRe)]            // formatted without spare area
    [InlineData(DiscMediaType.BdRe, 23_652_352, DiscMediaType.BdReDualLayer)]
    [InlineData(DiscMediaType.BdRe, 48_878_592, DiscMediaType.BdxlReTripleLayer)]
    [InlineData(DiscMediaType.BdR, 0, DiscMediaType.BdR)]                       // size unknown: keep what Windows said
    [InlineData(DiscMediaType.DvdPlusR, 24_438_784, DiscMediaType.DvdPlusR)]     // only Blu-ray is refined
    public void BluRayLayers_FollowFromTheReportedCapacity(DiscMediaType reported, int sectors, DiscMediaType expected)
    {
        Assert.Equal(expected, reported.RefineBluRayByCapacity(sectors));
    }

    [Fact]
    public void BluRaySpeed_UsesTheBluRay1xRate()
    {
        Assert.Equal(4_495_500, WriteSpeedDescriptor.GetBaseRate(DiscMediaType.BdR));
        var speed = WriteSpeedDescriptor.FromMultiplier(6.0, DiscMediaType.BdRDualLayer);
        Assert.Equal(6 * 4_495_500, speed.BytesPerSecond);
    }

    [Fact]
    public void CapacityBar_AcceptsBluRay_AndPrefersTheReportedSize()
    {
        int twentyGb = (int)(20_000_000_000L / 2048);
        var nominal = CompilationCapacityCalculator.AssessCapacity(twentyGb, DiscMediaType.BdR);
        Assert.Equal(CapacityStatus.FitsComfortably, nominal.Status);
        Assert.Equal(DiscMediaType.BdR, nominal.MediaType);

        // A BD-RE formatted with a large spare area reports less than the table; the drive's figure wins.
        var reported = CompilationCapacityCalculator.AssessCapacity(twentyGb, DiscMediaType.BdRe, mediaMaxSectors: 9_000_000);
        Assert.Equal(CapacityStatus.ExceedsMediaCapacity, reported.Status);

        var tooBig = CompilationCapacityCalculator.AssessCapacity(OpticalConstants.BdR25Sectors + 1, DiscMediaType.BdR);
        Assert.Equal(CapacityStatus.ExceedsMediaCapacity, tooBig.Status);
        Assert.False(tooBig.IsOverburn); // overburn is a CD-only idea
    }

    [Fact]
    public void CompilationCapacity_PassesTheReportedSizeThrough()
    {
        var compilation = new DataCompilation();
        compilation.AddFile("/movie.m2ts", string.Empty, 30_000_000_000L);

        var onTable = compilation.CalculateCapacity(DiscMediaType.BdRDualLayer);
        var onReported = compilation.CalculateCapacity(DiscMediaType.BdRDualLayer, mediaMaxSectors: 12_000_000);

        Assert.Equal(OpticalConstants.BdR50Sectors, onTable.MediaMaxSectors);
        Assert.Equal(CapacityStatus.FitsComfortably, onTable.Status);
        Assert.Equal(12_000_000, onReported.MediaMaxSectors);
        Assert.Equal(CapacityStatus.ExceedsMediaCapacity, onReported.Status);
    }

    [Theory]
    [InlineData(DiscMediaType.CdR)]
    [InlineData(DiscMediaType.DvdPlusR)]
    [InlineData(DiscMediaType.BdR)]
    [InlineData(DiscMediaType.BdRe)]
    public void SingleLayerMedia_HaveNoLayerPreview(DiscMediaType type)
    {
        Assert.Null(LayerSplitPlanner.Project(100_000, type));
    }

    [Fact]
    public void EmptyCompilation_HasNoLayerPreview()
    {
        Assert.Null(LayerSplitPlanner.Project(0, DiscMediaType.BdRDualLayer));
    }

    [Fact]
    public void DvdPlusRDl_FillsLayer0ThenMovesToLayer1()
    {
        var split = LayerSplitPlanner.Project(3_000_000, DiscMediaType.DvdPlusRDualLayer)!;

        Assert.Equal(2, split.Layers.Count);
        Assert.Equal(2_086_912, split.Layers[0].UsedSectors);
        Assert.Equal(2_086_912, split.Layers[0].CapacitySectors);
        Assert.Equal(3_000_000 - 2_086_912, split.Layers[1].UsedSectors);
        Assert.True(split.CrossesLayerChange);
        Assert.Equal([2_086_912], split.LayerChangeLbas);
        Assert.Equal(0, split.LayerChangeLbas[0] % OpticalConstants.DvdEccBlockSectors);
        Assert.Equal(0, split.OverflowSectors);
        Assert.Contains("DVD+R DL", split.Summary);
        Assert.Contains("Layer 0: 4.0 GB (full)", split.Summary);
        Assert.Contains("layer change at 4.0 GB", split.Summary);
    }

    [Fact]
    public void DvdMinusRDl_UsesItsOwnLayerSize()
    {
        var split = LayerSplitPlanner.Project(3_000_000, DiscMediaType.DvdMinusRDualLayer)!;
        Assert.Equal(2_085_856, split.Layers[0].CapacitySectors);
    }

    [Fact]
    public void SmallCompilationOnADualLayerDisc_StaysOnLayer0()
    {
        var split = LayerSplitPlanner.Project(500_000, DiscMediaType.BdRDualLayer)!;

        Assert.Equal(1, split.LayersUsed);
        Assert.False(split.CrossesLayerChange);
        Assert.Empty(split.LayerChangeLbas);
        Assert.Contains("no layer change", split.Summary);
    }

    [Fact]
    public void Bdxl128_SpreadsOverThreeOfFourLayers_OnClusterBoundaries()
    {
        int total = 40_000_000;
        var split = LayerSplitPlanner.Project(total, DiscMediaType.BdxlRQuadLayer)!;

        Assert.Equal(4, split.Layers.Count);
        Assert.Equal(OpticalConstants.Bdxl128Sectors, split.Layers.Sum(l => l.CapacitySectors));
        Assert.All(split.Layers.Take(3), l => Assert.Equal(0, l.CapacitySectors % OpticalConstants.BdClusterSectors));
        Assert.Equal(3, split.LayersUsed);
        Assert.Equal(total, split.Layers.Sum(l => l.UsedSectors));
        Assert.Equal(2, split.LayerChangeLbas.Count);
        Assert.Equal(split.Layers[0].CapacitySectors, split.LayerChangeLbas[0]);
        Assert.Contains("layer changes at", split.Summary);
    }

    [Fact]
    public void TheDrivesReportedSize_SetsTheLayerSize()
    {
        // A BD-RE DL formatted without spare area reports the full 50 GB.
        var split = LayerSplitPlanner.Project(13_000_000, DiscMediaType.BdReDualLayer, reportedCapacitySectors: 24_438_784)!;
        Assert.Equal(12_219_392, split.Layers[0].CapacitySectors);
        Assert.Equal(13_000_000 - 12_219_392, split.Layers[1].UsedSectors);
    }

    [Fact]
    public void TooMuchData_IsReportedAsNotFitting()
    {
        var split = LayerSplitPlanner.Project(OpticalConstants.BdR50Sectors + 1000, DiscMediaType.BdRDualLayer)!;
        Assert.Equal(1000, split.OverflowSectors);
        Assert.Contains("does not fit", split.Summary);
    }

    [Theory]
    [InlineData("MILLEN-MR1-000", true)]
    [InlineData("MILLENMR1 (Millenniata (M-DISC))", true)]
    [InlineData(" millen-mr1", true)]
    [InlineData("VERBAT-IMe-000 (Mitsubishi Chemical Corp. (Verbatim))", false)]
    [InlineData("CMCMAG-BA5-000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MDisc_IsRecognisedFromTheMediaId(string? mediaId, bool expected)
    {
        Assert.Equal(expected, MDiscIdentifier.IsMDisc(mediaId));
        Assert.Equal(expected, new CdBurner.Core.Abstractions.MediaStatusDto { MediaManufacturerId = mediaId }.IsMDisc);
    }

    [Theory]
    [InlineData(DiscMediaType.BdRe)]
    [InlineData(DiscMediaType.BdReDualLayer)]
    [InlineData(DiscMediaType.BdxlRTripleLayer)]
    [InlineData(DiscMediaType.BdxlRQuadLayer)]
    public void ArchiveSpanPlanner_AcceptsBluRay(DiscMediaType type)
    {
        int usable = ArchiveSpanPlanner.CalculateUsableSectors(type, 10);
        Assert.InRange(usable, 1, type.GetNominalCapacitySectors());
        Assert.True(usable > ArchiveSpanPlanner.CalculateUsableSectors(DiscMediaType.DvdMinusRDualLayer, 10));
    }
}
