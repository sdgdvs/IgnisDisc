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
    DvdRam = 12
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

    public static bool IsRewritable(this DiscMediaType media) => media switch
    {
        DiscMediaType.CdRw or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusRw or DiscMediaType.DvdRam => true,
        _ => false
    };

    public static bool IsWritable(this DiscMediaType media) => media switch
    {
        DiscMediaType.CdR or DiscMediaType.CdRw or
        DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or
        DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw or
        DiscMediaType.DvdMinusRDualLayer or DiscMediaType.DvdPlusRDualLayer or
        DiscMediaType.DvdRam => true,
        _ => false
    };

    public static bool IsDualLayer(this DiscMediaType media) => media switch
    {
        DiscMediaType.DvdMinusRDualLayer or DiscMediaType.DvdPlusRDualLayer => true,
        _ => false
    };

    public static int GetNominalCapacitySectors(this DiscMediaType media) => media switch
    {
        DiscMediaType.CdRom or DiscMediaType.CdR or DiscMediaType.CdRw => OpticalConstants.Cd700MbSectors,
        DiscMediaType.DvdRom or DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw => OpticalConstants.DvdMinusR5Sectors,
        DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw => OpticalConstants.DvdPlusR5Sectors,
        DiscMediaType.DvdMinusRDualLayer => OpticalConstants.DvdMinusR9Sectors,
        DiscMediaType.DvdPlusRDualLayer => OpticalConstants.DvdPlusR9Sectors,
        DiscMediaType.DvdRam => OpticalConstants.DvdMinusR5Sectors,
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
        _ => media.ToString()
    };
}
