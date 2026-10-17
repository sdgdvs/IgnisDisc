using System.Runtime.InteropServices;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace CdBurner.Hardware.Interop;

#region IMAPIv2 Core Interfaces

/// <summary>
/// COM interface for enumerating and monitoring optical devices on the system.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IDiscMaster2)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDiscMaster2
{
    [DispId(-4)]
    object _NewEnum { [DispId(-4)] [return: MarshalAs(UnmanagedType.IUnknown)] get; }

    [DispId(0)]
    string this[int index] { [DispId(0)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(1)]
    int Count { [DispId(1)] get; }

    [DispId(2)]
    bool IsSupportedEnvironment { [DispId(2)] get; }
}

/// <summary>
/// COM interface representing a physical or virtual optical disc recorder / drive.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IDiscRecorder2)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDiscRecorder2
{
    [DispId(0x100)]
    void EjectMedia();

    [DispId(0x101)]
    void CloseTray();

    [DispId(0x102)]
    void AcquireExclusiveAccess([In] bool force, [In, MarshalAs(UnmanagedType.BStr)] string client);

    [DispId(0x103)]
    void ReleaseExclusiveAccess();

    [DispId(0x104)]
    void DisableMcn();

    [DispId(0x105)]
    void EnableMcn();

    [DispId(0x106)]
    void InitializeDiscRecorder([In, MarshalAs(UnmanagedType.BStr)] string recorderUniqueId);

    [DispId(0)]
    string ActiveDiscRecorder { [DispId(0)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(0x201)]
    string VendorId { [DispId(0x201)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(0x202)]
    string ProductId { [DispId(0x202)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(0x203)]
    string ProductRevision { [DispId(0x203)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(0x204)]
    string VolumeName { [DispId(0x204)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(0x205)]
    Array VolumePathNames { [DispId(0x205)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x206)]
    bool DeviceCanLoadMedia { [DispId(0x206)] get; }

    [DispId(0x207)]
    int LegacyDeviceNumber { [DispId(0x207)] get; }

    [DispId(0x208)]
    Array SupportedFeaturePages { [DispId(0x208)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x209)]
    Array CurrentFeaturePages { [DispId(0x209)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x20A)]
    Array SupportedProfiles { [DispId(0x20A)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x20B)]
    Array CurrentProfiles { [DispId(0x20B)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x20C)]
    Array SupportedModePages { [DispId(0x20C)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x20D)]
    string ExclusiveAccessOwner { [DispId(0x20D)] [return: MarshalAs(UnmanagedType.BStr)] get; }
}

/// <summary>
/// Base COM interface for all IMAPIv2 disc writer formats.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IDiscFormat2)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDiscFormat2
{
    [DispId(0x800)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsRecorderSupported([In] IDiscRecorder2 recorder);

    [DispId(0x801)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsCurrentMediaSupported([In] IDiscRecorder2 recorder);

    [DispId(0x700)]
    bool MediaPhysicallyBlank { [DispId(0x700)] [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    [DispId(0x701)]
    bool MediaHeuristicallyBlank { [DispId(0x701)] [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    [DispId(0x702)]
    Array SupportedMediaTypes { [DispId(0x702)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
}

/// <summary>
/// COM interface for data disc writing (ISO 9660, Joliet, UDF).
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IDiscFormat2Data)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDiscFormat2Data
{
    // --- Base IDiscFormat2 methods (slots 7-11) ---
    [DispId(0x800)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsRecorderSupported([In] IDiscRecorder2 recorder);

    [DispId(0x801)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsCurrentMediaSupported([In] IDiscRecorder2 recorder);

    [DispId(0x700)]
    bool MediaPhysicallyBlank { [DispId(0x700)] [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    [DispId(0x701)]
    bool MediaHeuristicallyBlank { [DispId(0x701)] [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    [DispId(0x702)]
    Array SupportedMediaTypes { [DispId(0x702)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    // --- IDiscFormat2Data methods (slot 12+) matching exact imapi2.h order ---
    [DispId(0x100)]
    void put_Recorder([In] IDiscRecorder2? value);

    [DispId(0x100)]
    IDiscRecorder2? get_Recorder();

    [DispId(0x101)]
    void put_BufferUnderrunFreeDisabled([In, MarshalAs(UnmanagedType.VariantBool)] bool value);

    [DispId(0x101)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_BufferUnderrunFreeDisabled();

    [DispId(0x104)]
    void put_PostgapAlreadyInImage([In, MarshalAs(UnmanagedType.VariantBool)] bool value);

    [DispId(0x104)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_PostgapAlreadyInImage();

    [DispId(0x106)]
    ImapiFormat2DataMediaState CurrentMediaStatus { [DispId(0x106)] get; }

    [DispId(0x107)]
    ImapiMediaWriteProtectState WriteProtectStatus { [DispId(0x107)] get; }

    [DispId(0x108)]
    int TotalSectorsOnMedia { [DispId(0x108)] get; }

    [DispId(0x109)]
    int FreeSectorsOnMedia { [DispId(0x109)] get; }

    [DispId(0x10A)]
    int NextWritableAddress { [DispId(0x10A)] get; }

    [DispId(0x10B)]
    int StartAddressOfPreviousSession { [DispId(0x10B)] get; }

    [DispId(0x10C)]
    int LastWrittenAddressOfPreviousSession { [DispId(0x10C)] get; }

    [DispId(0x10D)]
    void put_ForceMediaToBeClosed([In, MarshalAs(UnmanagedType.VariantBool)] bool value);

    [DispId(0x10D)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_ForceMediaToBeClosed();

    [DispId(0x10E)]
    void put_DisableConsumerDvdCompatibilityMode([In, MarshalAs(UnmanagedType.VariantBool)] bool value);

    [DispId(0x10E)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_DisableConsumerDvdCompatibilityMode();

    [DispId(0x10F)]
    ImapiMediaPhysicalType CurrentPhysicalMediaType { [DispId(0x10F)] get; }

    [DispId(0x110)]
    void put_ClientName([In, MarshalAs(UnmanagedType.BStr)] string value);

    [DispId(0x110)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_ClientName();

    [DispId(0x111)]
    int RequestedWriteSpeed { [DispId(0x111)] get; }

    [DispId(0x112)]
    bool RequestedRotationTypeIsPureCAV { [DispId(0x112)] [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    [DispId(0x113)]
    int CurrentWriteSpeed { [DispId(0x113)] get; }

    [DispId(0x114)]
    bool CurrentRotationTypeIsPureCAV { [DispId(0x114)] [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    [DispId(0x115)]
    Array SupportedWriteSpeeds { [DispId(0x115)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x116)]
    Array SupportedWriteSpeedDescriptors { [DispId(0x116)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x117)]
    void put_ForceOverwrite([In, MarshalAs(UnmanagedType.VariantBool)] bool value);

    [DispId(0x117)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_ForceOverwrite();

    [DispId(0x118)]
    Array MultisessionInterfaces { [DispId(0x118)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x200)]
    void Write([In, MarshalAs(UnmanagedType.Interface)] ComIStream data);

    [DispId(0x201)]
    void CancelWrite();

    [DispId(0x202)]
    void SetWriteSpeed([In] int RequestedSectorsPerSecond, [In, MarshalAs(UnmanagedType.VariantBool)] bool RotationTypeIsPureCAV);
}

/// <summary>
/// COM interface for Track-At-Once (TAO) audio and data CD burning.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IDiscFormat2TAO)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDiscFormat2TrackAtOnce
{
    // --- Base IDiscFormat2 methods (slots 7-11) ---
    [DispId(0x800)]
    bool IsRecorderSupported([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);

    [DispId(0x801)]
    bool IsCurrentMediaSupported([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);

    [DispId(0x700)]
    bool MediaPhysicallyBlank { [DispId(0x700)] get; }

    [DispId(0x701)]
    bool MediaHeuristicallyBlank { [DispId(0x701)] get; }

    [DispId(0x702)]
    Array SupportedMediaTypes { [DispId(0x702)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    // --- IDiscFormat2TrackAtOnce methods & properties (slot 12+) ---
    [DispId(0x200)]
    void PrepareMedia();

    [DispId(0x201)]
    void AddAudioTrack([In, MarshalAs(UnmanagedType.Interface)] ComIStream data);

    [DispId(0x202)]
    void CancelAddTrack();

    [DispId(0x203)]
    void ReleaseMedia();

    [DispId(0x204)]
    void SetWriteSpeed([In] int RequestedSectorsPerSecond, [In] bool RotationTypeIsPureCAV);

    [DispId(0x100)]
    IDiscRecorder2? Recorder { [DispId(0x100)] [return: MarshalAs(UnmanagedType.Interface)] get; [DispId(0x100)] [param: In, MarshalAs(UnmanagedType.Interface)] set; }

    [DispId(0x102)]
    bool BufferUnderrunFreeDisabled { [DispId(0x102)] get; [DispId(0x102)] [param: In] set; }

    [DispId(0x103)]
    int NumberOfExistingTracks { [DispId(0x103)] get; }

    [DispId(0x104)]
    int TotalSectorsOnMedia { [DispId(0x104)] get; }

    [DispId(0x105)]
    int FreeSectorsOnMedia { [DispId(0x105)] get; }

    [DispId(0x106)]
    int UsedSectorsOnMedia { [DispId(0x106)] get; }

    [DispId(0x107)]
    bool DoNotFinalizeMedia { [DispId(0x107)] get; [DispId(0x107)] [param: In] set; }

    [DispId(0x10A)]
    Array ExpectedTableOfContents { [DispId(0x10A)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x10B)]
    ImapiMediaPhysicalType CurrentPhysicalMediaType { [DispId(0x10B)] get; }

    [DispId(0x10E)]
    string ClientName { [DispId(0x10E)] [return: MarshalAs(UnmanagedType.BStr)] get; [DispId(0x10E)] [param: In, MarshalAs(UnmanagedType.BStr)] set; }

    [DispId(0x10F)]
    int RequestedWriteSpeed { [DispId(0x10F)] get; }

    [DispId(0x110)]
    bool RequestedRotationTypeIsPureCAV { [DispId(0x110)] get; }

    [DispId(0x111)]
    int CurrentWriteSpeed { [DispId(0x111)] get; }

    [DispId(0x112)]
    bool CurrentRotationTypeIsPureCAV { [DispId(0x112)] get; }

    [DispId(0x113)]
    Array SupportedWriteSpeeds { [DispId(0x113)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x114)]
    Array SupportedWriteSpeedDescriptors { [DispId(0x114)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
}

/// <summary>
/// COM interface for Disc-At-Once (DAO) raw CD recording with lead-in and subcode.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IDiscFormat2RawCD)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDiscFormat2RawCD
{
    // --- Base IDiscFormat2 methods (slots 7-11) ---
    [DispId(0x800)]
    bool IsRecorderSupported([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);

    [DispId(0x801)]
    bool IsCurrentMediaSupported([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);

    [DispId(0x700)]
    bool MediaPhysicallyBlank { [DispId(0x700)] get; }

    [DispId(0x701)]
    bool MediaHeuristicallyBlank { [DispId(0x701)] get; }

    [DispId(0x702)]
    Array SupportedMediaTypes { [DispId(0x702)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    // --- IDiscFormat2RawCD methods & properties (slot 12+) ---
    [DispId(0x200)]
    void PrepareMedia();

    [DispId(0x201)]
    void WriteMedia([In, MarshalAs(UnmanagedType.Interface)] ComIStream data);

    [DispId(0x202)]
    void WriteMedia2([In, MarshalAs(UnmanagedType.Interface)] ComIStream data, [In] int streamLeadInSectors);

    [DispId(0x203)]
    void CancelWrite();

    [DispId(0x204)]
    void ReleaseMedia();

    [DispId(0x205)]
    void SetWriteSpeed([In] int RequestedSectorsPerSecond, [In] bool RotationTypeIsPureCAV);

    [DispId(0x100)]
    IDiscRecorder2? Recorder { [DispId(0x100)] [return: MarshalAs(UnmanagedType.Interface)] get; [DispId(0x100)] [param: In, MarshalAs(UnmanagedType.Interface)] set; }

    [DispId(0x102)]
    bool BufferUnderrunFreeDisabled { [DispId(0x102)] get; [DispId(0x102)] [param: In] set; }

    [DispId(0x103)]
    int StartOfNextSession { [DispId(0x103)] get; }

    [DispId(0x104)]
    int LastPossibleStartOfLeadout { [DispId(0x104)] get; }

    [DispId(0x105)]
    ImapiMediaPhysicalType CurrentPhysicalMediaType { [DispId(0x105)] get; }

    [DispId(0x108)]
    Array SupportedSectorTypes { [DispId(0x108)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x109)]
    ImapiFormat2RawCdDataType RequestedSectorType { [DispId(0x109)] get; [DispId(0x109)] [param: In] set; }

    [DispId(0x10A)]
    string ClientName { [DispId(0x10A)] [return: MarshalAs(UnmanagedType.BStr)] get; [DispId(0x10A)] [param: In, MarshalAs(UnmanagedType.BStr)] set; }

    [DispId(0x10B)]
    int RequestedWriteSpeed { [DispId(0x10B)] get; }

    [DispId(0x10C)]
    bool RequestedRotationTypeIsPureCAV { [DispId(0x10C)] get; }

    [DispId(0x10D)]
    int CurrentWriteSpeed { [DispId(0x10D)] get; }

    [DispId(0x10E)]
    bool CurrentRotationTypeIsPureCAV { [DispId(0x10E)] get; }

    [DispId(0x10F)]
    Array SupportedWriteSpeeds { [DispId(0x10F)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(0x110)]
    Array SupportedWriteSpeedDescriptors { [DispId(0x110)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }
}

/// <summary>
/// COM interface for erasing rewritable optical media (CD-RW, DVD-RW, DVD+RW, DVD-RAM).
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IDiscFormat2Erase)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IDiscFormat2Erase
{
    // --- Base IDiscFormat2 methods (slots 7-11) ---
    [DispId(0x800)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsRecorderSupported([In] IDiscRecorder2 recorder);

    [DispId(0x801)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsCurrentMediaSupported([In] IDiscRecorder2 recorder);

    [DispId(0x700)]
    bool MediaPhysicallyBlank { [DispId(0x700)] [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    [DispId(0x701)]
    bool MediaHeuristicallyBlank { [DispId(0x701)] [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    [DispId(0x702)]
    Array SupportedMediaTypes { [DispId(0x702)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    // --- IDiscFormat2Erase methods (slot 12+) matching exact imapi2.h order ---
    [DispId(0x100)]
    void put_Recorder([In] IDiscRecorder2? value);

    [DispId(0x100)]
    IDiscRecorder2? get_Recorder();

    [DispId(0x101)]
    void put_FullErase([In, MarshalAs(UnmanagedType.VariantBool)] bool value);

    [DispId(0x101)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_FullErase();

    [DispId(0x102)]
    ImapiMediaPhysicalType CurrentPhysicalMediaType { [DispId(0x102)] get; }

    [DispId(0x103)]
    void put_ClientName([In, MarshalAs(UnmanagedType.BStr)] string value);

    [DispId(0x103)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_ClientName();

    [DispId(0x201)]
    void EraseMedia();
}

#endregion

#region IMAPI2FS File System Authoring Interfaces

/// <summary>
/// El Torito bootable disc options COM interface.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IBootOptions)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IBootOptions
{
    [DispId(1)]
    ComIStream BootImage { [DispId(1)] [return: MarshalAs(UnmanagedType.Interface)] get; }

    [DispId(2)]
    string Manufacturer { [DispId(2)] [return: MarshalAs(UnmanagedType.BStr)] get; [DispId(2)] [param: In, MarshalAs(UnmanagedType.BStr)] set; }

    [DispId(3)]
    PlatformId PlatformId { [DispId(3)] get; [DispId(3)] [param: In] set; }

    [DispId(4)]
    EmulationType Emulation { [DispId(4)] get; [DispId(4)] [param: In] set; }

    [DispId(5)]
    uint ImageSize { [DispId(5)] get; }

    [DispId(20)]
    void AssignBootImage([In, MarshalAs(UnmanagedType.Interface)] ComIStream newVal);
}

/// <summary>
/// Result of file system image generation containing the burnable IStream.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IFileSystemImageResult)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IFileSystemImageResult
{
    [DispId(1)]
    ComIStream ImageStream { [DispId(1)] [return: MarshalAs(UnmanagedType.Interface)] get; }

    [DispId(2)]
    object ProgressItems { [DispId(2)] [return: MarshalAs(UnmanagedType.Interface)] get; }

    [DispId(3)]
    int TotalBlocks { [DispId(3)] get; }

    [DispId(4)]
    int BlockSize { [DispId(4)] get; }

    [DispId(5)]
    string DiscId { [DispId(5)] [return: MarshalAs(UnmanagedType.BStr)] get; }
}

/// <summary>
/// Base item interface in an IMAPI2FS file system compilation tree.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IFsiItem)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IFsiItem
{
    [DispId(11)]
    string Name { [DispId(11)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(12)]
    string FullPath { [DispId(12)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(13)]
    DateTime CreationTime { [DispId(13)] get; [DispId(13)] [param: In] set; }

    [DispId(14)]
    DateTime LastAccessedTime { [DispId(14)] get; [DispId(14)] [param: In] set; }

    [DispId(15)]
    DateTime LastModifiedTime { [DispId(15)] get; [DispId(15)] [param: In] set; }

    [DispId(16)]
    bool IsHidden { [DispId(16)] get; [DispId(16)] [param: In] set; }

    [DispId(17)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string FileSystemName([In] FsiFileSystems fileSystem);

    [DispId(18)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string FileSystemPath([In] FsiFileSystems fileSystem);
}

/// <summary>
/// File item interface in an IMAPI2FS file system compilation tree.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IFsiFileItem)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IFsiFileItem : IFsiItem
{
    // Inherited IFsiItem methods
    [DispId(11)]
    new string Name { [DispId(11)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(12)]
    new string FullPath { [DispId(12)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(13)]
    new DateTime CreationTime { [DispId(13)] get; [DispId(13)] [param: In] set; }

    [DispId(14)]
    new DateTime LastAccessedTime { [DispId(14)] get; [DispId(14)] [param: In] set; }

    [DispId(15)]
    new DateTime LastModifiedTime { [DispId(15)] get; [DispId(15)] [param: In] set; }

    [DispId(16)]
    new bool IsHidden { [DispId(16)] get; [DispId(16)] [param: In] set; }

    [DispId(17)]
    [return: MarshalAs(UnmanagedType.BStr)]
    new string FileSystemName([In] FsiFileSystems fileSystem);

    [DispId(18)]
    [return: MarshalAs(UnmanagedType.BStr)]
    new string FileSystemPath([In] FsiFileSystems fileSystem);

    // IFsiFileItem specific
    [DispId(41)]
    long DataSize { [DispId(41)] get; }

    [DispId(42)]
    int DataSize32BitLow { [DispId(42)] get; }

    [DispId(43)]
    int DataSize32BitHigh { [DispId(43)] get; }

    [DispId(44)]
    ComIStream Data { [DispId(44)] [return: MarshalAs(UnmanagedType.Interface)] get; [DispId(44)] [param: In, MarshalAs(UnmanagedType.Interface)] set; }
}

/// <summary>
/// Directory item interface in an IMAPI2FS file system compilation tree.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IFsiDirectoryItem)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IFsiDirectoryItem : IFsiItem
{
    // Inherited IFsiItem methods
    [DispId(11)]
    new string Name { [DispId(11)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(12)]
    new string FullPath { [DispId(12)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(13)]
    new DateTime CreationTime { [DispId(13)] get; [DispId(13)] [param: In] set; }

    [DispId(14)]
    new DateTime LastAccessedTime { [DispId(14)] get; [DispId(14)] [param: In] set; }

    [DispId(15)]
    new DateTime LastModifiedTime { [DispId(15)] get; [DispId(15)] [param: In] set; }

    [DispId(16)]
    new bool IsHidden { [DispId(16)] get; [DispId(16)] [param: In] set; }

    [DispId(17)]
    [return: MarshalAs(UnmanagedType.BStr)]
    new string FileSystemName([In] FsiFileSystems fileSystem);

    [DispId(18)]
    [return: MarshalAs(UnmanagedType.BStr)]
    new string FileSystemPath([In] FsiFileSystems fileSystem);

    // IFsiDirectoryItem specific
    [DispId(-4)]
    object _NewEnum { [DispId(-4)] [return: MarshalAs(UnmanagedType.IUnknown)] get; }

    [DispId(0)]
    IFsiItem this[[In, MarshalAs(UnmanagedType.BStr)] string path] { [DispId(0)] [return: MarshalAs(UnmanagedType.Interface)] get; }

    [DispId(1)]
    int Count { [DispId(1)] get; }

    [DispId(2)]
    object EnumFsiItems { [DispId(2)] [return: MarshalAs(UnmanagedType.IUnknown)] get; }

    [DispId(30)]
    void AddDirectory([In, MarshalAs(UnmanagedType.BStr)] string path);

    [DispId(31)]
    void AddFile([In, MarshalAs(UnmanagedType.BStr)] string path, [In, MarshalAs(UnmanagedType.Interface)] ComIStream fileData);

    [DispId(32)]
    void AddTree([In, MarshalAs(UnmanagedType.BStr)] string sourceDirectory, [In] bool includeBaseDirectory);

    [DispId(33)]
    void Add([In, MarshalAs(UnmanagedType.Interface)] IFsiItem item);

    [DispId(34)]
    void Remove([In, MarshalAs(UnmanagedType.BStr)] string path);

    [DispId(35)]
    void RemoveTree([In, MarshalAs(UnmanagedType.BStr)] string path);
}

/// <summary>
/// File system image authoring engine interface (ISO 9660, Joliet, UDF).
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_IFileSystemImage)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IFileSystemImage
{
    [DispId(0)]
    IFsiDirectoryItem Root { [DispId(0)] [return: MarshalAs(UnmanagedType.Interface)] get; }

    [DispId(1)]
    int SessionStartBlock { [DispId(1)] get; [DispId(1)] [param: In] set; }

    [DispId(2)]
    int FreeMediaBlocks { [DispId(2)] get; [DispId(2)] [param: In] set; }

    [DispId(36)]
    void SetMaxMediaBlocksFromDevice([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 discRecorder);

    [DispId(3)]
    int UsedBlocks { [DispId(3)] get; }

    [DispId(4)]
    string VolumeName { [DispId(4)] [return: MarshalAs(UnmanagedType.BStr)] get; [DispId(4)] [param: In, MarshalAs(UnmanagedType.BStr)] set; }

    [DispId(5)]
    string ImportedVolumeName { [DispId(5)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(6)]
    IBootOptions BootImageOptions { [DispId(6)] [return: MarshalAs(UnmanagedType.Interface)] get; [DispId(6)] [param: In, MarshalAs(UnmanagedType.Interface)] set; }

    [DispId(7)]
    int FileCount { [DispId(7)] get; }

    [DispId(8)]
    int DirectoryCount { [DispId(8)] get; }

    [DispId(9)]
    string WorkingDirectory { [DispId(9)] [return: MarshalAs(UnmanagedType.BStr)] get; [DispId(9)] [param: In, MarshalAs(UnmanagedType.BStr)] set; }

    [DispId(10)]
    int ChangePoint { [DispId(10)] get; }

    [DispId(11)]
    bool StrictFileSystemCompliance { [DispId(11)] get; [DispId(11)] [param: In] set; }

    [DispId(12)]
    bool UseRestrictedCharacterSet { [DispId(12)] get; [DispId(12)] [param: In] set; }

    [DispId(13)]
    FsiFileSystems FileSystemsToCreate { [DispId(13)] get; [DispId(13)] [param: In] set; }

    [DispId(14)]
    FsiFileSystems FileSystemsSupported { [DispId(14)] get; }

    [DispId(37)]
    int UDFRevision { [DispId(37)] get; [DispId(37)] [param: In] set; }

    [DispId(31)]
    Array UDFRevisionsSupported { [DispId(31)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(32)]
    void ChooseImageDefaults([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 discRecorder);

    [DispId(33)]
    void ChooseImageDefaultsForMediaType([In] ImapiMediaPhysicalType value);

    [DispId(34)]
    int ISO9660InterchangeLevel { [DispId(34)] get; [DispId(34)] [param: In] set; }

    [DispId(38)]
    Array ISO9660InterchangeLevelsSupported { [DispId(38)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; }

    [DispId(15)]
    [return: MarshalAs(UnmanagedType.Interface)]
    IFileSystemImageResult CreateResultImage();

    [DispId(16)]
    FsiItemType Exists([In, MarshalAs(UnmanagedType.BStr)] string fullPath);

    [DispId(18)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string CalculateDiscIdentifier();

    [DispId(19)]
    FsiFileSystems IdentifyFileSystemsOnDisc([In, MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 discRecorder);

    [DispId(20)]
    FsiFileSystems GetDefaultFileSystemForImport([In] FsiFileSystems fileSystems);

    [DispId(21)]
    FsiFileSystems ImportFileSystem();

    [DispId(22)]
    void ImportSpecificFileSystem([In] FsiFileSystems fileSystemToUse);

    [DispId(23)]
    void RollbackToChangePoint([In] int changePoint);

    [DispId(24)]
    void LockInChangePoint();

    [DispId(25)]
    [return: MarshalAs(UnmanagedType.Interface)]
    IFsiDirectoryItem CreateDirectoryItem([In, MarshalAs(UnmanagedType.BStr)] string name);

    [DispId(26)]
    [return: MarshalAs(UnmanagedType.Interface)]
    IFsiFileItem CreateFileItem([In, MarshalAs(UnmanagedType.BStr)] string name);

    [DispId(27)]
    string VolumeNameUDF { [DispId(27)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(28)]
    string VolumeNameJoliet { [DispId(28)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(29)]
    string VolumeNameISO9660 { [DispId(29)] [return: MarshalAs(UnmanagedType.BStr)] get; }

    [DispId(30)]
    bool StageFiles { [DispId(30)] get; [DispId(30)] [param: In] set; }

    [DispId(40)]
    Array MultisessionInterfaces { [DispId(40)] [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] get; [DispId(40)] [param: In, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] set; }
}

#endregion

#region Event Sinks

/// <summary>
/// Event sink for device plug and play events from MsftDiscMaster2.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_DDiscMaster2Events)]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DDiscMaster2Events
{
    [DispId(0x100)]
    void NotifyDeviceAdded([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.BStr)] string uniqueId);

    [DispId(0x101)]
    void NotifyDeviceRemoved([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.BStr)] string uniqueId);
}

/// <summary>
/// Event sink for data disc burning progress updates.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_DDiscFormat2DataEvents)]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DDiscFormat2DataEvents
{
    [DispId(0x200)]
    void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.IDispatch)] object progress);
}

/// <summary>
/// Event sink for Track-At-Once progress updates.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_DDiscFormat2TrackAtOnceEvents)]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DDiscFormat2TrackAtOnceEvents
{
    [DispId(0x200)]
    void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In] int currentTrack, [In] int currentSector, [In] int totalSectors);
}

/// <summary>
/// Event sink for Disc-At-Once raw CD burning progress updates.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_DDiscFormat2RawCDEvents)]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DDiscFormat2RawCDEvents
{
    [DispId(0x200)]
    void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In] int currentSector, [In] int totalSectors);
}

/// <summary>
/// Event sink for rewritable media erase progress updates.
/// </summary>
[ComImport]
[Guid(Imapi2Guids.IID_DDiscFormat2EraseEvents)]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DDiscFormat2EraseEvents
{
    [DispId(0x200)]
    void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In] int elapsedSeconds, [In] int estimatedTotalSeconds);
}

#endregion

#region IMAPIv2 COM Activation & Resource Management Helper

/// <summary>
/// Safe factory and lifetime manager for IMAPIv2 COM objects on Windows.
/// </summary>
public static class ImapiComActivation
{
    /// <summary>
    /// Indicates whether the current operating system is Windows and capable of running IMAPI COM activation.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Checks if a COM CoClass CLSID is registered in the Windows registry without throwing.
    /// </summary>
    public static bool IsClsidAvailable(Guid clsid)
    {
        if (!OperatingSystem.IsWindows() || clsid == Guid.Empty)
        {
            return false;
        }

        try
        {
            Type? type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            if (type == null)
            {
                return false;
            }

            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a COM CoClass CLSID string is registered in the Windows registry without throwing.
    /// </summary>
    public static bool IsClsidAvailable(string clsidString)
    {
        return Guid.TryParse(clsidString, out Guid clsid) && IsClsidAvailable(clsid);
    }

    /// <summary>
    /// Safely instantiates a COM object by its CLSID and casts it to the requested interface.
    /// Returns null if not running on Windows, CLSID is unmapped, or activation fails.
    /// </summary>
    public static T? CreateInstance<T>(Guid clsid) where T : class
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            Type? type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            if (type == null)
            {
                return null;
            }

            object? instance = Activator.CreateInstance(type);
            return instance as T;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Safely instantiates a COM object by its CLSID string and casts it to the requested interface.
    /// </summary>
    public static T? CreateInstance<T>(string clsidString) where T : class
    {
        if (!Guid.TryParse(clsidString, out Guid clsid))
        {
            return null;
        }

        return CreateInstance<T>(clsid);
    }

    public static IDiscMaster2? CreateDiscMaster2() => CreateInstance<IDiscMaster2>(Imapi2Guids.ClsidMsftDiscMaster2);
    public static IDiscRecorder2? CreateDiscRecorder2() => CreateInstance<IDiscRecorder2>(Imapi2Guids.ClsidMsftDiscRecorder2);
    public static IDiscFormat2Data? CreateDiscFormat2Data() => CreateInstance<IDiscFormat2Data>(Imapi2Guids.ClsidMsftDiscFormat2Data);
    public static IDiscFormat2TrackAtOnce? CreateDiscFormat2TrackAtOnce() => CreateInstance<IDiscFormat2TrackAtOnce>(Imapi2Guids.ClsidMsftDiscFormat2TAO);
    public static IDiscFormat2RawCD? CreateDiscFormat2RawCD() => CreateInstance<IDiscFormat2RawCD>(Imapi2Guids.ClsidMsftDiscFormat2RawCD);
    public static IDiscFormat2Erase? CreateDiscFormat2Erase() => CreateInstance<IDiscFormat2Erase>(Imapi2Guids.ClsidMsftDiscFormat2Erase);
    public static IFileSystemImage? CreateFileSystemImage() => CreateInstance<IFileSystemImage>(Imapi2Guids.ClsidMsftFileSystemImage);
    public static IBootOptions? CreateBootOptions() => CreateInstance<IBootOptions>(Imapi2Guids.ClsidMsftBootOptions);

    /// <summary>
    /// Safely decrements the COM reference count of an RCW and nulls out the target reference.
    /// </summary>
    public static int SafeRelease<T>(ref T? comObject) where T : class
    {
        if (comObject == null)
        {
            return 0;
        }

        int remainingCount = 0;
        try
        {
            if (Marshal.IsComObject(comObject))
            {
                remainingCount = Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // Suppress release exceptions during teardown
        }
        finally
        {
            comObject = null;
        }

        return remainingCount;
    }

    /// <summary>
    /// Forces release of all COM references to an RCW, completely disconnecting it from the native COM object.
    /// </summary>
    public static int FinalRelease<T>(ref T? comObject) where T : class
    {
        if (comObject == null)
        {
            return 0;
        }

        int remainingCount = 0;
        try
        {
            if (Marshal.IsComObject(comObject))
            {
                remainingCount = Marshal.FinalReleaseComObject(comObject);
            }
        }
        catch
        {
            // Suppress release exceptions during teardown
        }
        finally
        {
            comObject = null;
        }

        return remainingCount;
    }
}

/// <summary>
/// Alias for <see cref="ImapiComActivation"/>.
/// </summary>
public static class Imapi2ComFactory
{
    public static bool IsSupported => ImapiComActivation.IsSupported;
    public static bool IsClsidAvailable(Guid clsid) => ImapiComActivation.IsClsidAvailable(clsid);
    public static bool IsClsidAvailable(string clsidString) => ImapiComActivation.IsClsidAvailable(clsidString);
    public static T? CreateInstance<T>(Guid clsid) where T : class => ImapiComActivation.CreateInstance<T>(clsid);
    public static T? CreateInstance<T>(string clsidString) where T : class => ImapiComActivation.CreateInstance<T>(clsidString);
    public static IDiscMaster2? CreateDiscMaster2() => ImapiComActivation.CreateDiscMaster2();
    public static IDiscRecorder2? CreateDiscRecorder2() => ImapiComActivation.CreateDiscRecorder2();
    public static IDiscFormat2Data? CreateDiscFormat2Data() => ImapiComActivation.CreateDiscFormat2Data();
    public static IDiscFormat2TrackAtOnce? CreateDiscFormat2TrackAtOnce() => ImapiComActivation.CreateDiscFormat2TrackAtOnce();
    public static IDiscFormat2RawCD? CreateDiscFormat2RawCD() => ImapiComActivation.CreateDiscFormat2RawCD();
    public static IDiscFormat2Erase? CreateDiscFormat2Erase() => ImapiComActivation.CreateDiscFormat2Erase();
    public static IFileSystemImage? CreateFileSystemImage() => ImapiComActivation.CreateFileSystemImage();
    public static IBootOptions? CreateBootOptions() => ImapiComActivation.CreateBootOptions();
    public static int SafeRelease<T>(ref T? comObject) where T : class => ImapiComActivation.SafeRelease(ref comObject);
    public static int FinalRelease<T>(ref T? comObject) where T : class => ImapiComActivation.FinalRelease(ref comObject);
}

#endregion

