namespace CdBurner.App.ViewModels;

/// <summary>
/// Optical mastering module tabs available in the left navigation rail.
/// </summary>
public enum NavigationTab
{
    /// <summary>
    /// ISO 9660, Joliet, UDF data disc mastering with bootable El Torito support.
    /// </summary>
    DataDisc,

    /// <summary>
    /// Red Book Audio CD mastering with CD-Text, gapless DAO, and DSP normalization.
    /// </summary>
    AudioCd,

    /// <summary>
    /// DVD-Video authoring with 32 KB IFO/BUP ECC alignment and layer break analysis.
    /// </summary>
    DvdVideo,

    /// <summary>
    /// ISO and BIN/CUE disc image burning, creation, ripping, and checksum verification.
    /// </summary>
    ImageStudio,

    /// <summary>
    /// Multi-disc spanning archive planning, staging, burning, checksum verification, and PAR2 recovery.
    /// </summary>
    Archive,

    /// <summary>
    /// Optical drive hardware inspection, rewritable disc blanking, and surface scanning.
    /// </summary>
    DriveUtilities,

    /// <summary>
    /// Software-defined virtual optical drive simulation and virtual medium mounting.
    /// </summary>
    VirtualDrive,

    /// <summary>
    /// Global application preferences, default burning speeds, and logging configuration.
    /// </summary>
    Settings
}
