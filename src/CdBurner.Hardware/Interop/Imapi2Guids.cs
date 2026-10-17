namespace CdBurner.Hardware.Interop;

/// <summary>
/// Canonical IMAPIv2 COM CoClass (CLSID), Interface (IID), and Connection Point Event Sink GUIDs.
/// Source: Microsoft Windows SDK (imapi2.h, imapi2fs.h) and docs/ARCHITECTURE.md Section 4.
/// </summary>
public static class Imapi2Guids
{
    // =========================================================================
    // CoClasses (CLSIDs)
    // =========================================================================

    /// <summary>CLSID for MsftDiscMaster2 (optical device enumerator and monitor).</summary>
    public const string CLSID_MsftDiscMaster2          = "2735412E-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>CLSID for MsftDiscRecorder2 (individual optical disc burner/reader).</summary>
    public const string CLSID_MsftDiscRecorder2        = "2735412D-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>CLSID for MsftDiscFormat2Data (data disc burning engine: ISO/Joliet/UDF).</summary>
    public const string CLSID_MsftDiscFormat2Data      = "2735412A-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>CLSID for MsftDiscFormat2TAO (Track-At-Once audio/data disc burning engine).</summary>
    public const string CLSID_MsftDiscFormat2TAO       = "27354129-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>CLSID for MsftDiscFormat2RawCD (Disc-At-Once raw CD burning engine with subcode).</summary>
    public const string CLSID_MsftDiscFormat2RawCD     = "27354128-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>CLSID for MsftDiscFormat2Erase (rewritable media blanking/erasing engine).</summary>
    public const string CLSID_MsftDiscFormat2Erase     = "2735412B-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>CLSID for MsftFileSystemImage (file system authoring: ISO 9660, Joliet, UDF).</summary>
    public const string CLSID_MsftFileSystemImage      = "2C941FC5-975B-59BE-A960-9A2A262853A5";

    /// <summary>CLSID for MsftBootOptions (El Torito bootable specification configuration).</summary>
    public const string CLSID_MsftBootOptions          = "2C941FCE-975B-59BE-A960-9A2A262853A5";

    /// <summary>CLSID for MsftIsoImageManager (ISO image inspection and verification).</summary>
    public const string CLSID_MsftIsoImageManager      = "CEEE3B62-8F56-4056-869B-EF16917E3EFC";

    // =========================================================================
    // Interfaces (IIDs)
    // =========================================================================

    /// <summary>IID for IDiscMaster2.</summary>
    public const string IID_IDiscMaster2               = "27354130-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for IDiscRecorder2.</summary>
    public const string IID_IDiscRecorder2             = "27354133-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for IDiscRecorder2Ex (extended SCSI pass-through MMC-5 interface).</summary>
    public const string IID_IDiscRecorder2Ex           = "27354132-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for IDiscFormat2 (base optical writer interface).</summary>
    public const string IID_IDiscFormat2               = "27354152-8F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for IDiscFormat2Data.</summary>
    public const string IID_IDiscFormat2Data           = "27354153-9F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for IDiscFormat2TAO (Track-At-Once).</summary>
    public const string IID_IDiscFormat2TAO            = "27354154-8F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID alias for IDiscFormat2TrackAtOnce.</summary>
    public const string IID_IDiscFormat2TrackAtOnce    = "27354154-8F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for IDiscFormat2RawCD.</summary>
    public const string IID_IDiscFormat2RawCD          = "27354155-8F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for IDiscFormat2Erase.</summary>
    public const string IID_IDiscFormat2Erase          = "27354156-8F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for IFileSystemImage.</summary>
    public const string IID_IFileSystemImage           = "2C941FE1-975B-59BE-A960-9A2A262853A5";

    /// <summary>IID for IFileSystemImage2.</summary>
    public const string IID_IFileSystemImage2          = "D7644B2C-1537-4767-B62F-F1387B02DDFD";

    /// <summary>IID for IFileSystemImage3.</summary>
    public const string IID_IFileSystemImage3          = "7CFF842C-7E97-4807-8304-910DD8F7C051";

    /// <summary>IID for IFileSystemImageResult.</summary>
    public const string IID_IFileSystemImageResult     = "2C941FD8-975B-59BE-A960-9A2A262853A5";

    /// <summary>IID for IBootOptions.</summary>
    public const string IID_IBootOptions               = "2C941FD4-975B-59BE-A960-9A2A262853A5";

    /// <summary>IID for IFsiItem.</summary>
    public const string IID_IFsiItem                   = "2C941FD9-975B-59BE-A960-9A2A262853A5";

    /// <summary>IID for IFsiFileItem.</summary>
    public const string IID_IFsiFileItem               = "2C941FDB-975B-59BE-A960-9A2A262853A5";

    /// <summary>IID for IFsiDirectoryItem.</summary>
    public const string IID_IFsiDirectoryItem          = "2C941FDC-975B-59BE-A960-9A2A262853A5";

    // =========================================================================
    // Event Sinks (Connection Point IIDs, DISPID 0x200 = Update)
    // =========================================================================

    /// <summary>IID for DDiscMaster2Events (device arrival and removal notifications).</summary>
    public const string IID_DDiscMaster2Events         = "27354131-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for DDiscFormat2DataEvents (data burning progress notifications).</summary>
    public const string IID_DDiscFormat2DataEvents     = "2735413C-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for DDiscFormat2TAOEvents (TAO burning progress notifications).</summary>
    public const string IID_DDiscFormat2TAOEvents      = "2735413D-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for DDiscFormat2TrackAtOnceEvents (canonical SDK name).</summary>
    public const string IID_DDiscFormat2TrackAtOnceEvents = "2735413F-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for DDiscFormat2RawCDEvents (DAO raw CD burning progress notifications).</summary>
    public const string IID_DDiscFormat2RawCDEvents    = "27354142-7F64-5B0F-8F00-5D77AFBE261E";

    /// <summary>IID for DDiscFormat2EraseEvents (media erase progress notifications).</summary>
    public const string IID_DDiscFormat2EraseEvents    = "2735413A-7F64-5B0F-8F00-5D77AFBE261E";

    // =========================================================================
    // Parsed Guid Singletons for High-Performance Activation
    // =========================================================================

    public static readonly Guid ClsidMsftDiscMaster2          = new(CLSID_MsftDiscMaster2);
    public static readonly Guid ClsidMsftDiscRecorder2        = new(CLSID_MsftDiscRecorder2);
    public static readonly Guid ClsidMsftDiscFormat2Data      = new(CLSID_MsftDiscFormat2Data);
    public static readonly Guid ClsidMsftDiscFormat2TAO       = new(CLSID_MsftDiscFormat2TAO);
    public static readonly Guid ClsidMsftDiscFormat2RawCD     = new(CLSID_MsftDiscFormat2RawCD);
    public static readonly Guid ClsidMsftDiscFormat2Erase     = new(CLSID_MsftDiscFormat2Erase);
    public static readonly Guid ClsidMsftFileSystemImage      = new(CLSID_MsftFileSystemImage);
    public static readonly Guid ClsidMsftBootOptions          = new(CLSID_MsftBootOptions);
    public static readonly Guid ClsidMsftIsoImageManager      = new(CLSID_MsftIsoImageManager);

    public static readonly Guid IidIDiscMaster2               = new(IID_IDiscMaster2);
    public static readonly Guid IidIDiscRecorder2             = new(IID_IDiscRecorder2);
    public static readonly Guid IidIDiscRecorder2Ex           = new(IID_IDiscRecorder2Ex);
    public static readonly Guid IidIDiscFormat2               = new(IID_IDiscFormat2);
    public static readonly Guid IidIDiscFormat2Data           = new(IID_IDiscFormat2Data);
    public static readonly Guid IidIDiscFormat2TAO            = new(IID_IDiscFormat2TAO);
    public static readonly Guid IidIDiscFormat2RawCD          = new(IID_IDiscFormat2RawCD);
    public static readonly Guid IidIDiscFormat2Erase          = new(IID_IDiscFormat2Erase);
    public static readonly Guid IidIFileSystemImage           = new(IID_IFileSystemImage);
    public static readonly Guid IidIFileSystemImage2          = new(IID_IFileSystemImage2);
    public static readonly Guid IidIFileSystemImage3          = new(IID_IFileSystemImage3);
    public static readonly Guid IidIFileSystemImageResult     = new(IID_IFileSystemImageResult);
    public static readonly Guid IidIBootOptions               = new(IID_IBootOptions);
    public static readonly Guid IidIFsiItem                   = new(IID_IFsiItem);
    public static readonly Guid IidIFsiFileItem               = new(IID_IFsiFileItem);
    public static readonly Guid IidIFsiDirectoryItem          = new(IID_IFsiDirectoryItem);

    public static readonly Guid IidDDiscMaster2Events         = new(IID_DDiscMaster2Events);
    public static readonly Guid IidDDiscFormat2DataEvents     = new(IID_DDiscFormat2DataEvents);
    public static readonly Guid IidDDiscFormat2TAOEvents      = new(IID_DDiscFormat2TAOEvents);
    public static readonly Guid IidDDiscFormat2TrackAtOnceEvents = new(IID_DDiscFormat2TrackAtOnceEvents);
    public static readonly Guid IidDDiscFormat2RawCDEvents    = new(IID_DDiscFormat2RawCDEvents);
    public static readonly Guid IidDDiscFormat2EraseEvents    = new(IID_DDiscFormat2EraseEvents);
}
