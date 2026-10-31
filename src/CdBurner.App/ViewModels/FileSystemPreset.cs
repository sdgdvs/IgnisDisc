namespace CdBurner.App.ViewModels;

/// <summary>
/// Preconfigured filesystem profile presets for data and video disc authoring.
/// </summary>
public enum FileSystemPreset
{
    /// <summary>
    /// Universal standard compatible with modern Windows, Linux, and macOS:
    /// ISO 9660 Level 2 + Microsoft Joliet UCS-2BE + ECMA-167 / UDF 1.02 Bridge.
    /// </summary>
    IsoJolietUdf,

    /// <summary>
    /// Strict ISO 9660:1988 Level 1 (8.3 uppercase filenames, no Joliet, no UDF) for legacy DOS / embedded systems.
    /// </summary>
    IsoOnlyLevel1,

    /// <summary>
    /// UDF-first profile for large data archives (files of 4 GB and larger). The builder writes UDF 1.02; an
    /// ISO 9660 tree is always present for compatibility. (Enum name kept for saved-project compatibility.)
    /// </summary>
    UdfOnly201,

    /// <summary>
    /// DVD-Video compliant bridge structure: UDF 1.02 + ISO 9660 with uppercase VIDEO_TS hierarchy.
    /// </summary>
    DvdVideoBridge
}
