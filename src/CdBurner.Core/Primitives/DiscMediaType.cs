namespace CdBurner.Core.Primitives;

/// <summary>
/// Optical disc media physical types supported by CD/DVD burning and ripping engines.
/// </summary>
public enum DiscMediaType
{
    Unknown = 0,
    NoMedia = 1,
    CdRom = 2,
    CdR = 3,
    CdRw = 4,
    DvdRom = 5,
    DvdMinusR = 6,
    DvdMinusRw = 7,
    DvdPlusR = 8,
    DvdPlusRw = 9,
    DvdMinusRDualLayer = 10,
    DvdPlusRDualLayer = 11,
    DvdRam = 12,
    BdRom = 13,
    BdR = 14,
    BdRe = 15,
    BdRDualLayer = 16,
    BdReDualLayer = 17,
    /// <summary>BDXL BD-R, three layers (100 GB).</summary>
    BdxlRTripleLayer = 18,
    /// <summary>BDXL BD-R, four layers (128 GB).</summary>
    BdxlRQuadLayer = 19,
    /// <summary>BDXL BD-RE, three layers (100 GB).</summary>
    BdxlReTripleLayer = 20
}

/// <summary>
/// Extension methods for classifying and querying <see cref="DiscMediaType"/>.
/// </summary>
public static class DiscMediaTypeExtensions
{
    public static bool IsCd(this DiscMediaType media) => media switch
    {
        DiscMediaType.CdRom or DiscMediaType.CdR or DiscMediaType.CdRw => true,
        _ => false
    };

    public static bool IsDvd(this DiscMediaType media) => media switch
    {
        DiscMediaType.DvdRom or DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or
        DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw or DiscMediaType.DvdMinusRDualLayer or
        DiscMediaType.DvdPlusRDualLayer or DiscMediaType.DvdRam => true,
        _ => false
    };

    public static bool IsBluRay(this DiscMediaType media) => media switch
    {
        DiscMediaType.BdRom or DiscMediaType.BdR or DiscMediaType.BdRe or DiscMediaType.BdRDualLayer or
        DiscMediaType.BdReDualLayer or DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlRQuadLayer or
        DiscMediaType.BdxlReTripleLayer => true,
        _ => false
    };

    /// <summary>BDXL: Blu-ray with three or four layers, which only BDXL-capable drives read or write.</summary>
    public static bool IsBdxl(this DiscMediaType media) =>
        media is DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlRQuadLayer or DiscMediaType.BdxlReTripleLayer;

    /// <summary>Number of recording layers (1 for single-layer media and unknown types).</summary>
    public static int GetLayerCount(this DiscMediaType media) => media switch
    {
        DiscMediaType.DvdMinusRDualLayer or DiscMediaType.DvdPlusRDualLayer or
        DiscMediaType.BdRDualLayer or DiscMediaType.BdReDualLayer => 2,
        DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlReTripleLayer => 3,
        DiscMediaType.BdxlRQuadLayer => 4,
        _ => 1
    };

    public static bool IsMultiLayer(this DiscMediaType media) => media.GetLayerCount() > 1;

    public static bool IsRewritable(this DiscMediaType media) => media switch
    {
        DiscMediaType.CdRw or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusRw or DiscMediaType.DvdRam or
        DiscMediaType.BdRe or DiscMediaType.BdReDualLayer or DiscMediaType.BdxlReTripleLayer => true,
        _ => false
    };

    public static bool IsWritable(this DiscMediaType media) => media switch
    {
        DiscMediaType.CdR or DiscMediaType.CdRw or
        DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or
        DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw or
        DiscMediaType.DvdMinusRDualLayer or DiscMediaType.DvdPlusRDualLayer or
        DiscMediaType.DvdRam or
        DiscMediaType.BdR or DiscMediaType.BdRe or DiscMediaType.BdRDualLayer or DiscMediaType.BdReDualLayer or
        DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlRQuadLayer or DiscMediaType.BdxlReTripleLayer => true,
        _ => false
    };

    public static bool IsDualLayer(this DiscMediaType media) => media switch
    {
        DiscMediaType.DvdMinusRDualLayer or DiscMediaType.DvdPlusRDualLayer or
        DiscMediaType.BdRDualLayer or DiscMediaType.BdReDualLayer => true,
        _ => false
    };

    /// <summary>
    /// Planning capacity in 2048-byte sectors, used only when the drive has not reported the real figure. BD-RE values
    /// are for the default format with its spare area; a disc formatted differently reports its own size.
    /// </summary>
    public static int GetNominalCapacitySectors(this DiscMediaType media) => media switch
    {
        DiscMediaType.CdRom or DiscMediaType.CdR or DiscMediaType.CdRw => OpticalConstants.Cd700MbSectors,
        DiscMediaType.DvdRom or DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw => OpticalConstants.DvdMinusR5Sectors,
        DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw => OpticalConstants.DvdPlusR5Sectors,
        DiscMediaType.DvdMinusRDualLayer => OpticalConstants.DvdMinusR9Sectors,
        DiscMediaType.DvdPlusRDualLayer => OpticalConstants.DvdPlusR9Sectors,
        DiscMediaType.DvdRam => OpticalConstants.DvdMinusR5Sectors,
        DiscMediaType.BdRom or DiscMediaType.BdR => OpticalConstants.BdR25Sectors,
        DiscMediaType.BdRe => OpticalConstants.BdRe25Sectors,
        DiscMediaType.BdRDualLayer => OpticalConstants.BdR50Sectors,
        DiscMediaType.BdReDualLayer => OpticalConstants.BdRe50Sectors,
        DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlReTripleLayer => OpticalConstants.Bdxl100Sectors,
        DiscMediaType.BdxlRQuadLayer => OpticalConstants.Bdxl128Sectors,
        _ => 0
    };

    public static long GetNominalCapacityBytes(this DiscMediaType media) =>
        (long)media.GetNominalCapacitySectors() * OpticalConstants.DataSectorSize;

    public static string GetDisplayName(this DiscMediaType media) => media switch
    {
        DiscMediaType.Unknown => "Unknown Media",
        DiscMediaType.NoMedia => "No Media Inserted",
        DiscMediaType.CdRom => "CD-ROM",
        DiscMediaType.CdR => "CD-R",
        DiscMediaType.CdRw => "CD-RW",
        DiscMediaType.DvdRom => "DVD-ROM",
        DiscMediaType.DvdMinusR => "DVD-R",
        DiscMediaType.DvdMinusRw => "DVD-RW",
        DiscMediaType.DvdPlusR => "DVD+R",
        DiscMediaType.DvdPlusRw => "DVD+RW",
        DiscMediaType.DvdMinusRDualLayer => "DVD-R DL",
        DiscMediaType.DvdPlusRDualLayer => "DVD+R DL",
        DiscMediaType.DvdRam => "DVD-RAM",
        DiscMediaType.BdRom => "BD-ROM",
        DiscMediaType.BdR => "BD-R",
        DiscMediaType.BdRe => "BD-RE",
        DiscMediaType.BdRDualLayer => "BD-R DL",
        DiscMediaType.BdReDualLayer => "BD-RE DL",
        DiscMediaType.BdxlRTripleLayer => "BD-R XL (3 layers)",
        DiscMediaType.BdxlRQuadLayer => "BD-R XL (4 layers)",
        DiscMediaType.BdxlReTripleLayer => "BD-RE XL (3 layers)",
        _ => media.ToString()
    };

    /// <summary>
    /// Windows and the MMC profiles only say "BD-R" or "BD-RE"; the number of layers follows from the capacity the
    /// drive reports. Returns <paramref name="media"/> unchanged for anything else or when the size is unknown.
    /// </summary>
    public static DiscMediaType RefineBluRayByCapacity(this DiscMediaType media, long reportedSectors)
    {
        if (reportedSectors <= 0 || media is not (DiscMediaType.BdR or DiscMediaType.BdRe))
        {
            return media;
        }

        // Thresholds sit between the layer sizes, so a non-default BD-RE format (more or less spare area) still lands
        // on the right count.
        int layers = reportedSectors switch
        {
            <= 18_000_000 => 1,
            <= 36_000_000 => 2,
            <= 55_000_000 => 3,
            _ => 4
        };

        return (media, layers) switch
        {
            (DiscMediaType.BdR, 1) => DiscMediaType.BdR,
            (DiscMediaType.BdR, 2) => DiscMediaType.BdRDualLayer,
            (DiscMediaType.BdR, 3) => DiscMediaType.BdxlRTripleLayer,
            (DiscMediaType.BdR, _) => DiscMediaType.BdxlRQuadLayer,
            (_, 1) => DiscMediaType.BdRe,
            (_, 2) => DiscMediaType.BdReDualLayer,
            _ => DiscMediaType.BdxlReTripleLayer // no four-layer BD-RE exists
        };
    }
}
