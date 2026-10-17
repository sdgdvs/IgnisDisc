namespace CdBurner.Hardware.Interop;

/// <summary>
/// Physical optical media classification types defined in IMAPIv2 (IMAPI_MEDIA_PHYSICAL_TYPE).
/// </summary>
public enum ImapiMediaPhysicalType
{
    Unknown = 0,
    CdRom = 1,
    CdR = 2,
    CdRw = 3,
    DvdRom = 4,
    DvdRam = 5,
    DvdPlusR = 6,
    DvdPlusRw = 7,
    DvdPlusRDoubleLayer = 8,
    DvdDashR = 9,
    DvdDashRw = 10,
    DvdDashRDoubleLayer = 11,
    Disk = 12,
    DvdPlusRwDoubleLayer = 13,
    HdDvdRom = 14,
    HdDvdR = 15,
    HdDvdRam = 16,
    BdRom = 17,
    BdR = 18,
    BdRe = 19,
    Max = 19,

    // C-style alias values matching imapi2.h definitions
    IMAPI_MEDIA_TYPE_UNKNOWN = 0,
    IMAPI_MEDIA_TYPE_CDROM = 1,
    IMAPI_MEDIA_TYPE_CDR = 2,
    IMAPI_MEDIA_TYPE_CDRW = 3,
    IMAPI_MEDIA_TYPE_DVDROM = 4,
    IMAPI_MEDIA_TYPE_DVDRAM = 5,
    IMAPI_MEDIA_TYPE_DVDPLUSR = 6,
    IMAPI_MEDIA_TYPE_DVDPLUSRW = 7,
    IMAPI_MEDIA_TYPE_DVDPLUSR_DUALLAYER = 8,
    IMAPI_MEDIA_TYPE_DVDDASHR = 9,
    IMAPI_MEDIA_TYPE_DVDDASHRW = 10,
    IMAPI_MEDIA_TYPE_DVDDASHR_DUALLAYER = 11,
    IMAPI_MEDIA_TYPE_DISK = 12,
    IMAPI_MEDIA_TYPE_DVDPLUSRW_DUALLAYER = 13,
    IMAPI_MEDIA_TYPE_HDDVDROM = 14,
    IMAPI_MEDIA_TYPE_HDDVDR = 15,
    IMAPI_MEDIA_TYPE_HDDVDRAM = 16,
    IMAPI_MEDIA_TYPE_BDROM = 17,
    IMAPI_MEDIA_TYPE_BDR = 18,
    IMAPI_MEDIA_TYPE_BDRE = 19,
    IMAPI_MEDIA_TYPE_MAX = 19
}

/// <summary>
/// Status flags indicating the state of optical media in a recorder (IMAPI_FORMAT2_DATA_MEDIA_STATE).
/// </summary>
[Flags]
public enum ImapiFormat2DataMediaState
{
    Unknown = 0,
    InformationalMask = 0xF,
    UnsupportedMask = 0xFC00,
    OverwriteOnly = 0x1,
    RandomlyWritable = 0x1,
    Blank = 0x2,
    Appendable = 0x4,
    FinalSession = 0x8,
    Damaged = 0x400,
    EraseRequired = 0x800,
    NonEmptySession = 0x1000,
    WriteProtected = 0x2000,
    Finalized = 0x4000,
    UnsupportedMedia = 0x8000,

    // C-style alias values matching imapi2.h definitions
    IMAPI_FORMAT2_DATA_MEDIA_STATE_UNKNOWN = 0,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_INFORMATIONAL_MASK = 0xF,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_UNSUPPORTED_MASK = 0xFC00,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_OVERWRITE_ONLY = 0x1,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_RANDOMLY_WRITABLE = 0x1,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_BLANK = 0x2,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_APPENDABLE = 0x4,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_FINAL_SESSION = 0x8,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_DAMAGED = 0x400,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_ERASE_REQUIRED = 0x800,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_NON_EMPTY_SESSION = 0x1000,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_WRITE_PROTECTED = 0x2000,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_FINALIZED = 0x4000,
    IMAPI_FORMAT2_DATA_MEDIA_STATE_UNSUPPORTED_MEDIA = 0x8000
}

/// <summary>
/// Current write action performed during data burning (IMAPI_FORMAT2_DATA_WRITE_ACTION).
/// </summary>
public enum ImapiFormat2DataWriteAction
{
    ValidatingMedia = 0,
    FormattingMedia = 1,
    InitializingHardware = 2,
    CalibratingPower = 3,
    WritingData = 4,
    Finalization = 5,
    Completed = 6,
    Verifying = 7,

    // C-style alias values matching imapi2.h definitions
    IMAPI_FORMAT2_DATA_WRITE_ACTION_VALIDATING_MEDIA = 0,
    IMAPI_FORMAT2_DATA_WRITE_ACTION_FORMATTING_MEDIA = 1,
    IMAPI_FORMAT2_DATA_WRITE_ACTION_INITIALIZING_HARDWARE = 2,
    IMAPI_FORMAT2_DATA_WRITE_ACTION_CALIBRATING_POWER = 3,
    IMAPI_FORMAT2_DATA_WRITE_ACTION_WRITING_DATA = 4,
    IMAPI_FORMAT2_DATA_WRITE_ACTION_FINALIZATION = 5,
    IMAPI_FORMAT2_DATA_WRITE_ACTION_COMPLETED = 6,
    IMAPI_FORMAT2_DATA_WRITE_ACTION_VERIFYING = 7
}

/// <summary>
/// Current write action performed during Track-At-Once burning (IMAPI_FORMAT2_TAO_WRITE_ACTION).
/// </summary>
public enum ImapiFormat2TaoWriteAction
{
    Unknown = 0,
    Preparing = 1,
    Writing = 2,
    Finishing = 3,
    Verifying = 4,

    // C-style alias values matching imapi2.h definitions
    IMAPI_FORMAT2_TAO_WRITE_ACTION_UNKNOWN = 0,
    IMAPI_FORMAT2_TAO_WRITE_ACTION_PREPARING = 1,
    IMAPI_FORMAT2_TAO_WRITE_ACTION_WRITING = 2,
    IMAPI_FORMAT2_TAO_WRITE_ACTION_FINISHING = 3,
    IMAPI_FORMAT2_TAO_WRITE_ACTION_VERIFYING = 4
}

/// <summary>
/// Subcode format for raw CD recording (IMAPI_FORMAT2_RAW_CD_DATA_SECTOR_TYPE).
/// </summary>
public enum ImapiFormat2RawCdDataType
{
    SubcodePqOnly = 1,
    SubcodeIsCooked = 2,
    SubcodeIsRaw = 3,

    // C-style alias values matching imapi2.h definitions
    IMAPI_FORMAT2_RAW_CD_SUBCODE_PQ_ONLY = 1,
    IMAPI_FORMAT2_RAW_CD_SUBCODE_IS_COOKED = 2,
    IMAPI_FORMAT2_RAW_CD_SUBCODE_IS_RAW = 3
}

/// <summary>
/// Action performed during Disc-At-Once raw CD burning (IMAPI_FORMAT2_RAW_CD_WRITE_ACTION).
/// </summary>
public enum ImapiFormat2RawCdWriteAction
{
    Unknown = 0,
    Preparing = 1,
    Writing = 2,
    Finishing = 3,

    // C-style alias values matching imapi2.h definitions
    IMAPI_FORMAT2_RAW_CD_WRITE_ACTION_UNKNOWN = 0,
    IMAPI_FORMAT2_RAW_CD_WRITE_ACTION_PREPARING = 1,
    IMAPI_FORMAT2_RAW_CD_WRITE_ACTION_WRITING = 2,
    IMAPI_FORMAT2_RAW_CD_WRITE_ACTION_FINISHING = 3
}

/// <summary>
/// Write protection state for optical media (IMAPI_MEDIA_WRITE_PROTECT_STATE).
/// </summary>
[Flags]
public enum ImapiMediaWriteProtectState
{
    None = 0,
    UntilPowerdown = 0x1,
    ByCartridge = 0x2,
    ByMediaSpecificReason = 0x4,
    BySoftwareWriteProtect = 0x8,
    ByDiscControlBlock = 0x10,
    ReadOnlyMedia = 0x4000
}

/// <summary>
/// File systems to create or import in a file system image (FsiFileSystems from imapi2fs.h).
/// </summary>
[Flags]
public enum FsiFileSystems
{
    FsiFileSystemNone = 0,
    FsiFileSystemISO9660 = 1,
    FsiFileSystemJoliet = 2,
    FsiFileSystemUDF = 4,
    FsiFileSystemUnknown = 0x40000000
}

/// <summary>
/// Target platform architecture for El Torito bootable disc images (PlatformId from imapi2fs.h).
/// </summary>
public enum PlatformId
{
    PlatformX86 = 0,
    PlatformPowerPC = 1,
    PlatformMac = 2,
    PlatformEFI = 0xEF
}

/// <summary>
/// Boot emulation mode for El Torito bootable disc images (EmulationType from imapi2fs.h).
/// </summary>
public enum EmulationType
{
    EmulationNone = 0,
    Emulation12MFloppy = 1,
    Emulation144MFloppy = 2,
    Emulation288MFloppy = 3,
    EmulationHardDisk = 4
}

/// <summary>
/// Item type in a file system image directory tree (FsiItemType from imapi2fs.h).
/// </summary>
public enum FsiItemType
{
    FsiItemNotFound = 0,
    FsiItemDirectory = 1,
    FsiItemFile = 2
}
