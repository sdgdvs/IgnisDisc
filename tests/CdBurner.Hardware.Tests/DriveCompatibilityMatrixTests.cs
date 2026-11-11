using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Interop;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Hardware compatibility and firmware quirk simulation suite exercising <see cref="ImapiDiscRecorder"/>,
/// <see cref="Imapi2BurnEngine"/>, <see cref="DataBurnService"/>, <see cref="AudioCdBurnService"/>,
/// <see cref="DiscEraseService"/>, <see cref="MultiSessionManager"/>, <see cref="SurfaceScanService"/>,
/// and <see cref="DiscVerificationService"/> across 25 distinct optical drive + media personas without
/// requiring physical optical hardware.
/// </summary>
public class DriveCompatibilityMatrixTests
{
    #region Fake IMAPIv2 COM & SCSI Provider Infrastructure

    public sealed class SimulatedDrivePersona
    {
        public required string Name { get; init; }
        public string VendorId { get; init; } = "PIONEER";
        public string ProductId { get; init; } = "DVD-RW DVR-221";
        public string Revision { get; init; } = "1.00";
        public string VolumePath { get; init; } = "Z:\\";
        public int[] MmcProfiles { get; init; } = new[] { 0x0009, 0x000A, 0x0011, 0x0013, 0x001A, 0x001B, 0x002B };

        /// <summary>IDiscRecorder2.CurrentProfiles for the inserted disc; empty when the drive reports none.</summary>
        public int[] CurrentProfiles { get; init; } = Array.Empty<int>();

        // IDiscFormat2Data simulation
        public bool DataFormatSupported { get; init; } = true;
        public ImapiMediaPhysicalType PhysicalMediaType { get; init; } = ImapiMediaPhysicalType.CdR;
        public ImapiFormat2DataMediaState MediaState { get; init; } = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable;
        public bool PhysicallyBlank { get; init; }
        public bool HeuristicallyBlank { get; init; }
        public bool ThrowOnHeuristicallyBlank { get; init; }
        public bool ThrowOnMultisessionInterfaces { get; init; }
        public int TotalSectorsOnMedia { get; init; }
        public int FreeSectorsOnMedia { get; init; }
        public int NextWritableAddress { get; init; }
        public int[] SupportedWriteSpeedsSectorsPerSec { get; init; } = new[] { 705, 1411, 2116 };

        // IDiscFormat2Erase fallback simulation (for finalized CD-RW / DVD-RW where IDiscFormat2Data reports unsupported)
        public bool EraseFormatSupported { get; init; }
        public ImapiMediaPhysicalType ErasePhysicalMediaType { get; init; } = ImapiMediaPhysicalType.CdRw;
        public bool ErasePhysicallyBlank { get; init; }

        // Low-level SCSI / Volume simulation
        public int? ScsiReadCapacitySectors { get; init; }
        public int? ScsiSessionCount { get; init; }
        public int? MountedVolumeSectors { get; init; }
        public int? FileSystemEndSector { get; init; }
        public IReadOnlyList<DiscTocTrack> TocTracks { get; init; } = Array.Empty<DiscTocTrack>();
        public byte[]? CdTextPacks { get; init; }

        // Expected outcomes
        public required DiscMediaType ExpectedMediaType { get; init; }
        public required bool ExpectedIsBlank { get; init; }
        public required bool ExpectedIsRewritable { get; init; }
        public required bool ExpectedIsAppendable { get; init; }
        public required bool ExpectedIsOverwriteOnly { get; init; }
        public required bool ExpectedRequiresEraseToWrite { get; init; }
        public required bool ExpectedHasDataThatBurningReplaces { get; init; }
        public required int ExpectedTotalSectors { get; init; }
        public required int ExpectedFreeSectors { get; init; }
        public required int ExpectedUsedSectors { get; init; }
        public required int ExpectedNextWritableLba { get; init; }
        public required bool CanBurnDataWithoutFlags { get; init; }
        public required bool CanBurnDataWithOverwriteFlag { get; init; }
        public required bool CanBurnAudioCd { get; init; }
        public required bool CanErase { get; init; }

        public override string ToString() => Name;
    }

    private sealed class FakeComRecorder2 : IDiscRecorder2
    {
        private readonly SimulatedDrivePersona _p;
        public bool Ejected { get; private set; }
        public bool TrayClosed { get; private set; }
        public string? ExclusiveOwner { get; private set; }

        public FakeComRecorder2(SimulatedDrivePersona p) => _p = p;

        public void EjectMedia() => Ejected = true;
        public void CloseTray() => TrayClosed = true;
        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public void AcquireExclusiveAccess(bool force, string client) { ExclusiveOwner = client; AcquireCount++; }
        public void ReleaseExclusiveAccess() { ExclusiveOwner = null; ReleaseCount++; }
        public void DisableMcn() { }
        public void EnableMcn() { }
        public void InitializeDiscRecorder(string recorderUniqueId) { }
        public string ActiveDiscRecorder => "SIM-UNIQUE-ID";
        public string VendorId => _p.VendorId;
        public string ProductId => _p.ProductId;
        public string ProductRevision => _p.Revision;
        public string VolumeName => "\\\\?\\Volume{00000000-0000-0000-0000-000000000001}\\";
        public Array VolumePathNames => new object[] { _p.VolumePath };
        public bool DeviceCanLoadMedia => true;
        public int LegacyDeviceNumber => 0;
        public Array SupportedFeaturePages => Array.Empty<object>();
        public Array CurrentFeaturePages => Array.Empty<object>();
        public Array SupportedProfiles => _p.MmcProfiles.Cast<object>().ToArray();
        public Array CurrentProfiles => _p.CurrentProfiles.Cast<object>().ToArray();
        public Array SupportedModePages => Array.Empty<object>();
        public string ExclusiveAccessOwner => ExclusiveOwner ?? string.Empty;
    }

    private sealed class FakeComFormat2Data : IDiscFormat2Data
    {
        private readonly SimulatedDrivePersona _p;
        private IDiscRecorder2? _recorder;
        private bool _bufferUnderrunFreeDisabled;
        private bool _postgapAlreadyInImage;
        private bool _forceMediaToBeClosed;
        private bool _disableConsumerDvdCompatibilityMode;
        private string _clientName = string.Empty;
        private int _requestedWriteSpeed;
        private bool _requestedRotationTypeIsPureCav;
        private bool _forceOverwrite;
        public bool WriteCancelled { get; private set; }
        public byte[]? WrittenBytes { get; private set; }
        public Action<ComIStream>? OnWrite { get; set; }

        public FakeComFormat2Data(SimulatedDrivePersona p) => _p = p;

        public bool IsRecorderSupported(IDiscRecorder2 recorder) => true;
        public bool IsCurrentMediaSupported(IDiscRecorder2 recorder) => _p.DataFormatSupported;
        public bool MediaPhysicallyBlank => _p.PhysicallyBlank;
        public bool MediaHeuristicallyBlank => _p.ThrowOnHeuristicallyBlank
            ? throw new COMException("E_NOTIMPL on MediaHeuristicallyBlank", unchecked((int)0x80004001))
            : _p.HeuristicallyBlank;
        public Array SupportedMediaTypes => new object[]
        {
            (int)ImapiMediaPhysicalType.CdR,
            (int)ImapiMediaPhysicalType.CdRw,
            (int)ImapiMediaPhysicalType.DvdDashR,
            (int)ImapiMediaPhysicalType.DvdDashRw,
            (int)ImapiMediaPhysicalType.DvdPlusR,
            (int)ImapiMediaPhysicalType.DvdPlusRw,
            (int)ImapiMediaPhysicalType.DvdPlusRDoubleLayer,
            (int)ImapiMediaPhysicalType.DvdDashRDoubleLayer,
            (int)ImapiMediaPhysicalType.DvdRam
        };

        public void put_Recorder(IDiscRecorder2? value) => _recorder = value;
        public IDiscRecorder2? get_Recorder() => _recorder;
        public void put_BufferUnderrunFreeDisabled(bool value) => _bufferUnderrunFreeDisabled = value;
        public bool get_BufferUnderrunFreeDisabled() => _bufferUnderrunFreeDisabled;
        public void put_PostgapAlreadyInImage(bool value) => _postgapAlreadyInImage = value;
        public bool get_PostgapAlreadyInImage() => _postgapAlreadyInImage;
        public ImapiFormat2DataMediaState CurrentMediaStatus => _p.MediaState;
        public ImapiMediaWriteProtectState WriteProtectStatus => ImapiMediaWriteProtectState.None;
        public int TotalSectorsOnMedia => _p.TotalSectorsOnMedia;
        public int FreeSectorsOnMedia => _p.FreeSectorsOnMedia;
        public int NextWritableAddress => _p.NextWritableAddress;
        public int StartAddressOfPreviousSession => 0;
        public int LastWrittenAddressOfPreviousSession => 0;
        public void put_ForceMediaToBeClosed(bool value) => _forceMediaToBeClosed = value;
        public bool get_ForceMediaToBeClosed() => _forceMediaToBeClosed;
        public void put_DisableConsumerDvdCompatibilityMode(bool value) => _disableConsumerDvdCompatibilityMode = value;
        public bool get_DisableConsumerDvdCompatibilityMode() => _disableConsumerDvdCompatibilityMode;
        public ImapiMediaPhysicalType CurrentPhysicalMediaType => _p.PhysicalMediaType;
        public void put_ClientName(string value) => _clientName = value;
        public string get_ClientName() => _clientName;
        public int RequestedWriteSpeed => _requestedWriteSpeed;
        public bool RequestedRotationTypeIsPureCAV => _requestedRotationTypeIsPureCav;
        public int CurrentWriteSpeed => _requestedWriteSpeed > 0 ? _requestedWriteSpeed : (_p.SupportedWriteSpeedsSectorsPerSec.FirstOrDefault(s => s > 0));
        public bool CurrentRotationTypeIsPureCAV => _requestedRotationTypeIsPureCav;
        public Array SupportedWriteSpeeds => _p.SupportedWriteSpeedsSectorsPerSec.Cast<object>().ToArray();
        public Array SupportedWriteSpeedDescriptors => Array.Empty<object>();
        public void put_ForceOverwrite(bool value) => _forceOverwrite = value;
        public bool get_ForceOverwrite() => _forceOverwrite;
        public Array MultisessionInterfaces => _p.ThrowOnMultisessionInterfaces
            ? throw new COMException("MultisessionInterfaces not supported", unchecked((int)0x80004005))
            : Array.Empty<object>();

        public void Write(ComIStream data)
        {
            if (OnWrite != null)
            {
                OnWrite(data);
                return;
            }
            WrittenBytes = ReadAllComStream(data);
        }

        public void CancelWrite() => WriteCancelled = true;
        public void SetWriteSpeed(int RequestedSectorsPerSecond, bool RotationTypeIsPureCAV)
        {
            _requestedWriteSpeed = RequestedSectorsPerSecond;
            _requestedRotationTypeIsPureCav = RotationTypeIsPureCAV;
        }
    }

    private sealed class FakeComFormat2Erase : IDiscFormat2Erase
    {
        private readonly SimulatedDrivePersona _p;
        private IDiscRecorder2? _recorder;
        private bool _fullErase;
        private string _clientName = string.Empty;
        public bool EraseCalled { get; private set; }
        public Action? OnErase { get; set; }

        public FakeComFormat2Erase(SimulatedDrivePersona p) => _p = p;

        public bool IsRecorderSupported(IDiscRecorder2 recorder) => true;
        public bool IsCurrentMediaSupported(IDiscRecorder2 recorder) => _p.EraseFormatSupported;
        public bool MediaPhysicallyBlank => _p.ErasePhysicallyBlank;
        public bool MediaHeuristicallyBlank => _p.ThrowOnHeuristicallyBlank
            ? throw new COMException("E_NOTIMPL on Erase MediaHeuristicallyBlank", unchecked((int)0x80004001))
            : _p.ErasePhysicallyBlank;
        public Array SupportedMediaTypes => new object[] { (int)_p.ErasePhysicalMediaType };
        public void put_Recorder(IDiscRecorder2? value) => _recorder = value;
        public IDiscRecorder2? get_Recorder() => _recorder;
        public void put_FullErase(bool value) => _fullErase = value;
        public bool get_FullErase() => _fullErase;
        public ImapiMediaPhysicalType CurrentPhysicalMediaType => _p.ErasePhysicalMediaType;
        public void put_ClientName(string value) => _clientName = value;
        public string get_ClientName() => _clientName;
        public void EraseMedia()
        {
            OnErase?.Invoke();
            EraseCalled = true;
        }
    }

    private sealed class FakeComFormat2RawCD : IDiscFormat2RawCD
    {
        private IDiscRecorder2? _recorder;
        private bool _bufferUnderrunFreeDisabled;
        private ImapiFormat2RawCdDataType _requestedSectorType;
        private string _clientName = string.Empty;
        private int _requestedWriteSpeed;
        public bool Prepared { get; private set; }
        public bool Released { get; private set; }
        public byte[]? WrittenRawBytes { get; private set; }
        public Action? OnPrepareMedia { get; set; }
        public Action<ComIStream>? OnWriteMedia { get; set; }

        public bool IsRecorderSupported(IDiscRecorder2 recorder) => true;
        public bool IsCurrentMediaSupported(IDiscRecorder2 recorder) => true;
        public bool MediaPhysicallyBlank => true;
        public bool MediaHeuristicallyBlank => true;
        public Array SupportedMediaTypes => new object[] { (int)ImapiMediaPhysicalType.CdR, (int)ImapiMediaPhysicalType.CdRw };

        public void PrepareMedia()
        {
            OnPrepareMedia?.Invoke();
            Prepared = true;
        }

        public void WriteMedia(ComIStream data)
        {
            if (OnWriteMedia != null)
            {
                OnWriteMedia(data);
                return;
            }
            WrittenRawBytes = ReadAllComStream(data);
        }

        public void WriteMedia2(ComIStream data, int streamLeadInSectors) => WriteMedia(data);
        public void CancelWrite() { }
        public void ReleaseMedia() => Released = true;
        public void SetWriteSpeed(int RequestedSectorsPerSecond, bool RotationTypeIsPureCAV) => _requestedWriteSpeed = RequestedSectorsPerSecond;
        public void put_Recorder(IDiscRecorder2? value) => _recorder = value;
        public IDiscRecorder2? get_Recorder() => _recorder;
        public void put_BufferUnderrunFreeDisabled(bool value) => _bufferUnderrunFreeDisabled = value;
        public bool get_BufferUnderrunFreeDisabled() => _bufferUnderrunFreeDisabled;
        public int StartOfNextSession => 0;
        public int LastPossibleStartOfLeadout => 359_847;
        public ImapiMediaPhysicalType CurrentPhysicalMediaType => ImapiMediaPhysicalType.CdR;
        public Array SupportedSectorTypes => new object[] { (int)ImapiFormat2RawCdDataType.SubcodeIsRaw };
        public void put_RequestedSectorType(ImapiFormat2RawCdDataType value) => _requestedSectorType = value;
        public ImapiFormat2RawCdDataType get_RequestedSectorType() => _requestedSectorType;
        public void put_ClientName(string value) => _clientName = value;
        public string get_ClientName() => _clientName;
        public int RequestedWriteSpeed => _requestedWriteSpeed;
        public bool RequestedRotationTypeIsPureCAV => false;
        public int CurrentWriteSpeed => _requestedWriteSpeed > 0 ? _requestedWriteSpeed : 1800;
        public bool CurrentRotationTypeIsPureCAV => false;
        public Array SupportedWriteSpeeds => new object[] { 1800, 1200, 600 };
        public Array SupportedWriteSpeedDescriptors => Array.Empty<object>();
    }

    private sealed class FakeRawCDImageCreator : IRawCDImageCreator
    {
        private readonly List<byte[]> _tracks = new();
        private ImapiFormat2RawCdDataType _resultingImageType = ImapiFormat2RawCdDataType.SubcodeIsRaw;
        public int TrackCount => _tracks.Count;

        public ComIStream CreateResultImage()
        {
            int total = _tracks.Sum(t => t.Length);
            byte[] combined = new byte[total];
            int offset = 0;
            foreach (var t in _tracks)
            {
                Buffer.BlockCopy(t, 0, combined, offset, t.Length);
                offset += t.Length;
            }
            return new ManagedComStream(new MemoryStream(combined), leaveOpen: false);
        }

        public int AddTrack(int dataType, ComIStream? data)
        {
            _tracks.Add(data != null ? ReadAllComStream(data) : Array.Empty<byte>());
            return _tracks.Count;
        }

        public void AddSpecialPregap(ComIStream data) { }
        public void AddSubcodeRWGenerator(ComIStream subcode) { }
        public void put_ResultingImageType(ImapiFormat2RawCdDataType value) => _resultingImageType = value;
        public ImapiFormat2RawCdDataType get_ResultingImageType() => _resultingImageType;
        public int StartOfLeadout => _tracks.Sum(t => t.Length / OpticalConstants.RawSectorSize);
        public void put_StartOfLeadoutLimit(int value) { }
        public int get_StartOfLeadoutLimit() => 359_847;
        public void put_DisableGaplessAudio(bool value) { }
        public bool get_DisableGaplessAudio() => false;
        public void put_MediaCatalogNumber(string value) { }
        public string get_MediaCatalogNumber() => string.Empty;
        public void put_StartingTrackNumber(int value) { }
        public int get_StartingTrackNumber() => 1;
    }

    private sealed class FakeComFormat2Tao : IDiscFormat2TrackAtOnce
    {
        private IDiscRecorder2? _recorder;
        private int _requestedWriteSpeed;
        public bool Prepared { get; private set; }
        public bool Released { get; private set; }
        public List<byte[]> AddedTracks { get; } = new();

        public bool IsRecorderSupported(IDiscRecorder2 recorder) => true;
        public bool IsCurrentMediaSupported(IDiscRecorder2 recorder) => true;
        public bool MediaPhysicallyBlank => true;
        public bool MediaHeuristicallyBlank => true;
        public Array SupportedMediaTypes => new object[] { (int)ImapiMediaPhysicalType.CdR, (int)ImapiMediaPhysicalType.CdRw };
        public void PrepareMedia() => Prepared = true;
        public void AddAudioTrack(ComIStream data) => AddedTracks.Add(ReadAllComStream(data));
        public void CancelAddTrack() { }
        public void ReleaseMedia() => Released = true;
        public void SetWriteSpeed(int RequestedSectorsPerSecond, bool RotationTypeIsPureCAV) => _requestedWriteSpeed = RequestedSectorsPerSecond;
        public void put_Recorder(IDiscRecorder2? value) => _recorder = value;
        public IDiscRecorder2? get_Recorder() => _recorder;
        public void put_BufferUnderrunFreeDisabled(bool value) { }
        public bool get_BufferUnderrunFreeDisabled() => false;
        public int NumberOfExistingTracks => AddedTracks.Count;
        public int TotalSectorsOnMedia => 359_847;
        public int FreeSectorsOnMedia => 359_847;
        public int UsedSectorsOnMedia => 0;
        public void put_DoNotFinalizeMedia(bool value) { }
        public bool get_DoNotFinalizeMedia() => false;
        public Array ExpectedTableOfContents => Array.Empty<object>();
        public ImapiMediaPhysicalType CurrentPhysicalMediaType => ImapiMediaPhysicalType.CdR;
        public void put_ClientName(string value) { }
        public string get_ClientName() => "CdBurner";
        public int RequestedWriteSpeed => _requestedWriteSpeed;
        public bool RequestedRotationTypeIsPureCAV => false;
        public int CurrentWriteSpeed => _requestedWriteSpeed > 0 ? _requestedWriteSpeed : 1200;
        public bool CurrentRotationTypeIsPureCAV => false;
        public Array SupportedWriteSpeeds => new object[] { 1200, 600 };
        public Array SupportedWriteSpeedDescriptors => Array.Empty<object>();
    }

    private sealed class SimulatedImapiComProvider : IImapiComProvider
    {
        public SimulatedDrivePersona Persona { get; }
        public FakeComRecorder2 ComRecorder { get; }
        public FakeComFormat2Data FormatData { get; }
        public FakeComFormat2Erase FormatErase { get; }
        public FakeComFormat2RawCD FormatRawCd { get; } = new();
        public FakeRawCDImageCreator RawCreator { get; } = new();
        public FakeComFormat2Tao FormatTao { get; } = new();
        public int SpinDownCount { get; private set; }

        public SimulatedImapiComProvider(SimulatedDrivePersona persona)
        {
            Persona = persona;
            ComRecorder = new FakeComRecorder2(persona);
            FormatData = new FakeComFormat2Data(persona);
            FormatErase = new FakeComFormat2Erase(persona);
        }

        public IDiscMaster2? CreateDiscMaster2() => null;
        public IDiscRecorder2? CreateDiscRecorder2() => ComRecorder;
        public IDiscFormat2Data? CreateDiscFormat2Data() => FormatData;
        public IDiscFormat2TrackAtOnce? CreateDiscFormat2TrackAtOnce() => FormatTao;
        public IDiscFormat2RawCD? CreateDiscFormat2RawCD() => FormatRawCd;
        public IDiscFormat2Erase? CreateDiscFormat2Erase() => FormatErase;
        public IRawCDImageCreator? CreateRawCDImageCreator() => RawCreator;
        public int? ReadCapacity(string volumePath) => Persona.ScsiReadCapacitySectors;
        public int? ReadSessionCount(string volumePath) => Persona.ScsiSessionCount;
        public IReadOnlyList<DiscTocTrack> ReadTableOfContents(string volumePath) => Persona.TocTracks;
        public byte[]? ReadCdTextPacks(string volumePath) => Persona.CdTextPacks;
        public int? TryGetMountedVolumeSectors(string? volumePath) => Persona.MountedVolumeSectors;
        public int? ReadFileSystemEndSector(string volumePath) => Persona.FileSystemEndSector;
        public bool TrySpinDown(string? volumePath)
        {
            SpinDownCount++;
            return true;
        }
    }

    private static byte[] ReadAllComStream(ComIStream comStream)
    {
        using var ms = new MemoryStream();
        byte[] buf = new byte[65536];
        IntPtr pcbRead = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                comStream.Read(buf, buf.Length, pcbRead);
                int read = Marshal.ReadInt32(pcbRead);
                if (read <= 0) break;
                ms.Write(buf, 0, read);
            }
            return ms.ToArray();
        }
        finally
        {
            Marshal.FreeCoTaskMem(pcbRead);
        }
    }

    #endregion

    #region 25-Profile Drive & Media Compatibility Matrix

    public static IEnumerable<object[]> Get25DrivePersonas()
    {
        // 1. Blank 80-min CD-R on standard SATA burner
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "01_Blank_CdR_80Min_StandardSata",
                PhysicalMediaType = ImapiMediaPhysicalType.CdR,
                MediaState = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable,
                PhysicallyBlank = true,
                TotalSectorsOnMedia = 359_847,
                FreeSectorsOnMedia = 359_847,
                NextWritableAddress = 0,
                ExpectedMediaType = DiscMediaType.CdR,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 359_847,
                ExpectedFreeSectors = 359_847,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = true,
                CanErase = false
            }
        };

        // 2. Blank CD-R on quirky USB enclosure reporting 0 TotalSectors, 0 FreeSectors, -1 NextWritable, and reverse speeds
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "02_Blank_CdR_QuirkyUsbZeroSectorsAndReverseSpeeds",
                VendorId = "GENERIC",
                ProductId = "USB2.0 CD-RW",
                PhysicalMediaType = ImapiMediaPhysicalType.CdR,
                MediaState = ImapiFormat2DataMediaState.Blank,
                PhysicallyBlank = true,
                ThrowOnHeuristicallyBlank = true,
                ThrowOnMultisessionInterfaces = true,
                TotalSectorsOnMedia = 0,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = -1,
                SupportedWriteSpeedsSectorsPerSec = new[] { 3600, 2400, 1200, 0, -1, 1200, 150 },
                ExpectedMediaType = DiscMediaType.CdR,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 360_000,
                ExpectedFreeSectors = 360_000,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = true,
                CanErase = false
            }
        };

        // 3. Open Multi-Session CD-R (Session 1 @ LBA 0..2230, NextWritable @ 13,630)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "03_OpenMultiSession_CdR",
                PhysicalMediaType = ImapiMediaPhysicalType.CdR,
                MediaState = ImapiFormat2DataMediaState.Appendable,
                TotalSectorsOnMedia = 359_847,
                FreeSectorsOnMedia = 346_217,
                NextWritableAddress = 13_630,
                ScsiReadCapacitySectors = 2_230,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.CdR,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 359_847,
                ExpectedFreeSectors = 346_217,
                ExpectedUsedSectors = 2_230,
                ExpectedNextWritableLba = 13_630,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = true,
                CanErase = false
            }
        };

        // 4. Finalized Single-Session CD-R (read-only after finalization)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "04_Finalized_CdR",
                PhysicalMediaType = ImapiMediaPhysicalType.CdR,
                MediaState = ImapiFormat2DataMediaState.Finalized,
                TotalSectorsOnMedia = 359_847,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = -1,
                ScsiReadCapacitySectors = 180_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.CdR,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 359_847,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 180_000,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 5. Blank High-Speed CD-RW
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "05_Blank_CdRw",
                PhysicalMediaType = ImapiMediaPhysicalType.CdRw,
                MediaState = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable,
                PhysicallyBlank = true,
                TotalSectorsOnMedia = 359_847,
                FreeSectorsOnMedia = 359_847,
                NextWritableAddress = 0,
                ExpectedMediaType = DiscMediaType.CdRw,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 359_847,
                ExpectedFreeSectors = 359_847,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = true,
                CanErase = true
            }
        };

        // 6. Open Multi-Session CD-RW (2 sessions recorded, still appendable)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "06_OpenMultiSession_CdRw",
                PhysicalMediaType = ImapiMediaPhysicalType.CdRw,
                MediaState = ImapiFormat2DataMediaState.Appendable,
                TotalSectorsOnMedia = 359_847,
                FreeSectorsOnMedia = 330_000,
                NextWritableAddress = 20_800,
                ScsiReadCapacitySectors = 13_932,
                ScsiSessionCount = 2,
                ExpectedMediaType = DiscMediaType.CdRw,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 359_847,
                ExpectedFreeSectors = 330_000,
                ExpectedUsedSectors = 13_932,
                ExpectedNextWritableLba = 20_800,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = true,
                CanErase = true
            }
        };

        // 7. Finalized CD-RW reported via IDiscFormat2Data (must require Erase before burning)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "07_Finalized_CdRw_ViaDataFormat",
                PhysicalMediaType = ImapiMediaPhysicalType.CdRw,
                MediaState = ImapiFormat2DataMediaState.Finalized,
                TotalSectorsOnMedia = 359_847,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = -1,
                ScsiReadCapacitySectors = 45_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.CdRw,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = true,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 359_847,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 45_000,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 8. Finalized Audio CD-RW where IDiscFormat2Data.IsCurrentMediaSupported=false, falling back to IDiscFormat2Erase + TOC
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "08_Finalized_AudioCdRw_ViaEraseFallback",
                DataFormatSupported = false,
                EraseFormatSupported = true,
                ErasePhysicalMediaType = ImapiMediaPhysicalType.CdRw,
                ErasePhysicallyBlank = false,
                ThrowOnHeuristicallyBlank = true,
                ScsiReadCapacitySectors = null,
                ScsiSessionCount = 1,
                TocTracks = new[]
                {
                    new DiscTocTrack(1, 0, 15_000, IsAudio: true),
                    new DiscTocTrack(2, 15_000, 12_000, IsAudio: true)
                },
                ExpectedMediaType = DiscMediaType.CdRw,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = true,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 360_000,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 27_000,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 9. Blank DVD-R SL (4.7 GB) with firmware throwing E_NOTIMPL on MediaHeuristicallyBlank
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "09_Blank_DvdMinusR_WithFirmwareNotImplQuirk",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdDashR,
                MediaState = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable,
                PhysicallyBlank = true,
                ThrowOnHeuristicallyBlank = true,
                TotalSectorsOnMedia = 2_298_496,
                FreeSectorsOnMedia = 2_298_496,
                NextWritableAddress = 0,
                ExpectedMediaType = DiscMediaType.DvdMinusR,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 2_298_496,
                ExpectedFreeSectors = 2_298_496,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 10. Open Multi-Session DVD-R SL
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "10_OpenMultiSession_DvdMinusR",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdDashR,
                MediaState = ImapiFormat2DataMediaState.Appendable,
                TotalSectorsOnMedia = 2_298_496,
                FreeSectorsOnMedia = 2_000_000,
                NextWritableAddress = 298_496,
                ScsiReadCapacitySectors = 280_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.DvdMinusR,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 2_298_496,
                ExpectedFreeSectors = 2_000_000,
                ExpectedUsedSectors = 280_000,
                ExpectedNextWritableLba = 298_496,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 11. Finalized DVD-R SL
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "11_Finalized_DvdMinusR",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdDashR,
                MediaState = ImapiFormat2DataMediaState.Finalized,
                TotalSectorsOnMedia = 2_298_496,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = -1,
                ScsiReadCapacitySectors = 1_950_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.DvdMinusR,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 2_298_496,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 1_950_000,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 12. Blank Sequential DVD-RW
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "12_Blank_Sequential_DvdMinusRw",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdDashRw,
                MediaState = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable,
                PhysicallyBlank = true,
                TotalSectorsOnMedia = 2_298_496,
                FreeSectorsOnMedia = 2_298_496,
                NextWritableAddress = 0,
                ExpectedMediaType = DiscMediaType.DvdMinusRw,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 2_298_496,
                ExpectedFreeSectors = 2_298_496,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 13. Finalized Sequential DVD-RW (requires Erase, cannot overwrite in place)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "13_Finalized_Sequential_DvdMinusRw",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdDashRw,
                MediaState = ImapiFormat2DataMediaState.Finalized,
                TotalSectorsOnMedia = 2_298_496,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = -1,
                ScsiReadCapacitySectors = 500_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.DvdMinusRw,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = true,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 2_298_496,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 500_000,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 14. Used Restricted-Overwrite DVD-RW (formatted for in-place overwrite, FreeSectorsOnMedia=0 quirk)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "14_Used_RestrictedOverwrite_DvdMinusRw_ZeroFreeQuirk",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdDashRw,
                MediaState = ImapiFormat2DataMediaState.OverwriteOnly,
                TotalSectorsOnMedia = 2_298_496,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = 99_999,
                ScsiReadCapacitySectors = 2_298_496,
                MountedVolumeSectors = 40_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.DvdMinusRw,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = true,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = true,
                ExpectedTotalSectors = 2_298_496,
                ExpectedFreeSectors = 2_298_496,
                ExpectedUsedSectors = 40_000,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 15. Blank DVD+R SL
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "15_Blank_DvdPlusR",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdPlusR,
                MediaState = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable,
                PhysicallyBlank = true,
                TotalSectorsOnMedia = 2_295_104,
                FreeSectorsOnMedia = 2_295_104,
                NextWritableAddress = 0,
                ExpectedMediaType = DiscMediaType.DvdPlusR,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 2_295_104,
                ExpectedFreeSectors = 2_295_104,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 16. Freshly Erased / Heuristically Blank DVD+RW (OverwriteOnly | HeuristicallyBlank -> burns without --overwrite)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "16_HeuristicallyBlank_DvdPlusRw",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdPlusRw,
                MediaState = ImapiFormat2DataMediaState.OverwriteOnly | ImapiFormat2DataMediaState.Blank,
                PhysicallyBlank = false,
                HeuristicallyBlank = true,
                TotalSectorsOnMedia = 2_295_104,
                FreeSectorsOnMedia = 2_295_104,
                NextWritableAddress = 0,
                ExpectedMediaType = DiscMediaType.DvdPlusRw,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = true,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 2_295_104,
                ExpectedFreeSectors = 2_295_104,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 17. Used DVD+RW on Slim USB Burner (reports 2,295,104 READ CAPACITY, 30,000 mounted volume sectors)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "17_Used_DvdPlusRw_SlimUsbBurner",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdPlusRw,
                MediaState = ImapiFormat2DataMediaState.OverwriteOnly,
                TotalSectorsOnMedia = 2_295_104,
                FreeSectorsOnMedia = 2_295_104,
                NextWritableAddress = 0,
                ScsiReadCapacitySectors = 2_295_104,
                MountedVolumeSectors = 29_999, // Windows stops at the end of the UDF partition
                FileSystemEndSector = 30_000,  // the disc's descriptors include the closing anchor
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.DvdPlusRw,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = true,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = true,
                ExpectedTotalSectors = 2_295_104,
                ExpectedFreeSectors = 2_295_104,
                ExpectedUsedSectors = 30_000,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 18. Used DVD+RW on NEC/Optiarc SATA Burner (reports FreeSectorsOnMedia=0 because background format completed)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "18_Used_DvdPlusRw_OptiarcZeroFreeSectorsQuirk",
                VendorId = "OPTIARC",
                ProductId = "DVD RW AD-7280S",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdPlusRw,
                MediaState = ImapiFormat2DataMediaState.OverwriteOnly | ImapiFormat2DataMediaState.Finalized,
                TotalSectorsOnMedia = 2_295_104,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = 0,
                ScsiReadCapacitySectors = 2_295_104,
                MountedVolumeSectors = 75_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.DvdPlusRw,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = true,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = true,
                ExpectedTotalSectors = 2_295_104,
                ExpectedFreeSectors = 2_295_104,
                ExpectedUsedSectors = 75_000,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 19. Used DVD-RAM Cartridge/Disc (random-access OverwriteOnly media)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "19_Used_DvdRam_OverwriteOnly",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdRam,
                MediaState = ImapiFormat2DataMediaState.OverwriteOnly,
                TotalSectorsOnMedia = 2_236_704,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = 0,
                ScsiReadCapacitySectors = 2_236_704,
                MountedVolumeSectors = 120_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.DvdRam,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = true,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = true,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = true,
                ExpectedTotalSectors = 2_236_704,
                ExpectedFreeSectors = 2_236_704,
                ExpectedUsedSectors = 120_000,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = true
            }
        };

        // 20. Blank DVD+R DL (8.5 GB Dual-Layer)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "20_Blank_DvdPlusRDualLayer",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdPlusRDoubleLayer,
                MediaState = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable,
                PhysicallyBlank = true,
                TotalSectorsOnMedia = 4_173_824,
                FreeSectorsOnMedia = 4_173_824,
                NextWritableAddress = 0,
                ExpectedMediaType = DiscMediaType.DvdPlusRDualLayer,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 4_173_824,
                ExpectedFreeSectors = 4_173_824,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 21. Blank DVD-R DL (8.5 GB Dual-Layer, firmware reports 0 TotalSectors -> falls back to nominal 4,171,712)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "21_Blank_DvdMinusRDualLayer_NominalCapacityFallback",
                PhysicalMediaType = ImapiMediaPhysicalType.DvdDashRDoubleLayer,
                MediaState = ImapiFormat2DataMediaState.Blank,
                PhysicallyBlank = true,
                TotalSectorsOnMedia = 0,
                FreeSectorsOnMedia = 0,
                NextWritableAddress = 0,
                ExpectedMediaType = DiscMediaType.DvdMinusRDualLayer,
                ExpectedIsBlank = true,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = true,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 4_171_712,
                ExpectedFreeSectors = 4_171_712,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = 0,
                CanBurnDataWithoutFlags = true,
                CanBurnDataWithOverwriteFlag = true,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 22. Pressed Commercial DVD-ROM (IDiscFormat2Data unsupported, READ CAPACITY = 3,850,000 sectors)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "22_Pressed_Commercial_DvdRom",
                DataFormatSupported = false,
                EraseFormatSupported = false,
                ScsiReadCapacitySectors = 3_850_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.DvdRom,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 3_850_000,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 3_850_000,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 23. Pressed Commercial Data CD-ROM (IDiscFormat2Data unsupported, READ CAPACITY = 310,000 sectors <= 450,000)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "23_Pressed_Commercial_DataCdRom",
                DataFormatSupported = false,
                EraseFormatSupported = false,
                ScsiReadCapacitySectors = 310_000,
                ScsiSessionCount = 1,
                ExpectedMediaType = DiscMediaType.CdRom,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 310_000,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 310_000,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 24. Pressed Red Book Audio CD without elevation (READ CAPACITY returns null, READ TOC returns tracks)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "24_Pressed_RedBook_AudioCd_TocOnly",
                DataFormatSupported = false,
                EraseFormatSupported = false,
                ScsiReadCapacitySectors = null,
                ScsiSessionCount = 1,
                TocTracks = new[]
                {
                    new DiscTocTrack(1, 0, 18_000, IsAudio: true),
                    new DiscTocTrack(2, 18_000, 21_500, IsAudio: true),
                    new DiscTocTrack(3, 39_500, 19_200, IsAudio: true)
                },
                ExpectedMediaType = DiscMediaType.CdRom,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 58_700,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 58_700,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 25. Empty Drive Tray (No disc inserted)
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "25_EmptyDriveTray_NoMedia",
                DataFormatSupported = false,
                EraseFormatSupported = false,
                ScsiReadCapacitySectors = null,
                TocTracks = Array.Empty<DiscTocTrack>(),
                ExpectedMediaType = DiscMediaType.NoMedia,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 0,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 0,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 26. Small closed DVD-R (372 MB, Disc-At-Once): IDiscFormat2Data unsupported, under the CD size limit. Measured on
        //     the GP60NB50 (2026-11-11): once closed, the drive marks only DVD-ROM (0x0010) current, so it reads as DVD-ROM.
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "26_Closed_Small_DvdMinusR",
                DataFormatSupported = false,
                EraseFormatSupported = false,
                ScsiReadCapacitySectors = 181_648,
                ScsiSessionCount = 1,
                CurrentProfiles = new[] { 0x0010 },
                ExpectedMediaType = DiscMediaType.DvdRom,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 181_648,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 181_648,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };

        // 27. Small pressed DVD-ROM (300,000 sectors): not a CD despite its size
        yield return new object[]
        {
            new SimulatedDrivePersona
            {
                Name = "27_Pressed_Small_DvdRom",
                DataFormatSupported = false,
                EraseFormatSupported = false,
                ScsiReadCapacitySectors = 300_000,
                ScsiSessionCount = 1,
                CurrentProfiles = new[] { 0x0010 },
                ExpectedMediaType = DiscMediaType.DvdRom,
                ExpectedIsBlank = false,
                ExpectedIsRewritable = false,
                ExpectedIsAppendable = false,
                ExpectedIsOverwriteOnly = false,
                ExpectedRequiresEraseToWrite = false,
                ExpectedHasDataThatBurningReplaces = false,
                ExpectedTotalSectors = 300_000,
                ExpectedFreeSectors = 0,
                ExpectedUsedSectors = 300_000,
                ExpectedNextWritableLba = -1,
                CanBurnDataWithoutFlags = false,
                CanBurnDataWithOverwriteFlag = false,
                CanBurnAudioCd = false,
                CanErase = false
            }
        };
    }

    [Theory]
    [MemberData(nameof(Get25DrivePersonas))]
    public async Task DrivePersonaMatrix_MediaStatus_Capabilities_DataBurn_AudioBurn_And_Erase_BehaveDeterministically(
        SimulatedDrivePersona persona)
    {
        var provider = new SimulatedImapiComProvider(persona);
        using var recorder = new ImapiDiscRecorder(provider.ComRecorder, "SIM-DRIVE", provider);
        var engine = new Imapi2BurnEngine(provider);

        // 1. Verify Capabilities never throw and always return sorted positive write speeds
        var caps = recorder.GetCapabilities();
        Assert.NotNull(caps);
        Assert.NotEmpty(caps.SupportedWriteSpeeds);
        for (int i = 1; i < caps.SupportedWriteSpeeds.Count; i++)
        {
            Assert.True(caps.SupportedWriteSpeeds[i].BytesPerSecond > caps.SupportedWriteSpeeds[i - 1].BytesPerSecond,
                $"Write speeds should be strictly increasing and deduplicated on {persona.Name}.");
        }

        // 2. Verify MediaStatus matches expected hardware state
        var status = recorder.QueryMediaStatus();
        Assert.Equal(persona.ExpectedMediaType, status.MediaType);
        Assert.Equal(persona.ExpectedMediaType != DiscMediaType.NoMedia, status.MediaPresent);
        Assert.Equal(persona.ExpectedIsBlank, status.IsBlank);
        Assert.Equal(persona.ExpectedIsRewritable, status.IsRewritable);
        Assert.Equal(persona.ExpectedIsAppendable, status.IsAppendable);
        Assert.Equal(persona.ExpectedIsOverwriteOnly, status.IsOverwriteOnly);
        Assert.Equal(persona.ExpectedRequiresEraseToWrite, status.RequiresEraseToWrite);
        Assert.Equal(persona.ExpectedHasDataThatBurningReplaces, status.HasDataThatBurningReplaces);
        Assert.Equal(persona.ExpectedTotalSectors, status.TotalSectors);
        Assert.Equal(persona.ExpectedFreeSectors, status.FreeSectors);
        Assert.Equal(persona.ExpectedUsedSectors, status.UsedSectors);
        Assert.Equal(persona.ExpectedNextWritableLba, status.NextWritableLba);

        // 3. Test DataBurnService without --overwrite
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("HELLO.TXT", Encoding.UTF8.GetBytes($"Test payload for {persona.Name}"));
        var dataService = new DataBurnService(engine);

        var burnNoFlags = await dataService.BurnCompilationAsync(recorder, root, new DataBurnOptions { CloseDisc = false });
        Assert.Equal(persona.CanBurnDataWithoutFlags, burnNoFlags.Success);
        if (!persona.CanBurnDataWithoutFlags)
        {
            if (persona.ExpectedRequiresEraseToWrite)
            {
                Assert.Equal(DataBurnService.EraseRequiredMessage, burnNoFlags.ErrorMessage);
            }
            else if (persona.ExpectedHasDataThatBurningReplaces)
            {
                Assert.Equal(DataBurnService.ReplaceExistingDataMessage, burnNoFlags.ErrorMessage);
            }
        }
        else
        {
            Assert.NotNull(provider.FormatData.WrittenBytes);
            // Verify PVD root directory LBA is laid out past NextWritableLba when appending
            var pvd = provider.FormatData.WrittenBytes!.AsSpan(16 * 2048, 2048);
            uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(156 + 2, 4));
            int expectedSessionStart = (!persona.ExpectedIsBlank && persona.ExpectedIsAppendable && persona.ExpectedNextWritableLba > 0)
                ? persona.ExpectedNextWritableLba
                : 0;
            Assert.True(rootLba > expectedSessionStart);
        }

        // 4. Test DataBurnService WITH --overwrite
        var burnWithOverwrite = await dataService.BurnCompilationAsync(
            recorder,
            root,
            new DataBurnOptions { OverwriteExistingData = true, CloseDisc = true });
        Assert.Equal(persona.CanBurnDataWithOverwriteFlag, burnWithOverwrite.Success);
        if (persona.CanBurnDataWithOverwriteFlag && persona.ExpectedIsOverwriteOnly)
        {
            Assert.True(provider.FormatData.get_ForceOverwrite());
        }

        // 5. Test AudioCdBurnService
        var audioComp = new AudioCdCompilation { AlbumTitle = "Matrix Audio" };
        audioComp.AddTrack(new AudioCdTrackItem(
            TrackNumber: 1,
            SourceFilePath: string.Empty,
            Title: "Track 1",
            Performer: "Tester",
            CustomPcmStreamFactory: () => new MemoryStream(new byte[300 * 2352])));
        var audioService = new AudioCdBurnService(engine);
        var audioResult = await audioService.BurnAsync(recorder, audioComp);
        Assert.Equal(persona.CanBurnAudioCd, audioResult.Success);

        // 6. Test DiscEraseService
        var eraseService = new DiscEraseService(engine);
        Assert.Equal(persona.CanErase, DiscEraseService.CanErase(recorder));
        var eraseResult = await eraseService.QuickEraseAsync(recorder);
        Assert.Equal(persona.CanErase, eraseResult.Success);
    }

    #endregion

    #region Physical Imapi2BurnEngine COM Fault-Injection & Fallback Tests

    [Fact]
    public async Task Imapi2BurnEngine_BurnAudioTracks_WhenDaoPrepareMediaThrowsComException_FallsBackToTaoAndSucceeds()
    {
        var persona = new SimulatedDrivePersona
        {
            Name = "DaoUnsupportedDrive",
            PhysicalMediaType = ImapiMediaPhysicalType.CdR,
            MediaState = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable,
            PhysicallyBlank = true,
            TotalSectorsOnMedia = 359_847,
            FreeSectorsOnMedia = 359_847,
            ExpectedMediaType = DiscMediaType.CdR,
            ExpectedIsBlank = true,
            ExpectedIsRewritable = false,
            ExpectedIsAppendable = true,
            ExpectedIsOverwriteOnly = false,
            ExpectedRequiresEraseToWrite = false,
            ExpectedHasDataThatBurningReplaces = false,
            ExpectedTotalSectors = 359_847,
            ExpectedFreeSectors = 359_847,
            ExpectedUsedSectors = 0,
            ExpectedNextWritableLba = 0,
            CanBurnDataWithoutFlags = true,
            CanBurnDataWithOverwriteFlag = true,
            CanBurnAudioCd = true,
            CanErase = false
        };

        var provider = new SimulatedImapiComProvider(persona);
        // Simulate a drive firmware that rejects Raw DAO mode during PrepareMedia()
        provider.FormatRawCd.OnPrepareMedia = () =>
            throw new COMException("IMAPI_E_RAW_CD_NOT_SUPPORTED", unchecked((int)0xC0AA0210));

        using var recorder = new ImapiDiscRecorder(provider.ComRecorder, "DAO-FALLBACK", provider);
        var engine = new Imapi2BurnEngine(provider);

        // Give it a short 50-frame track to also verify RedBookPaddedStream pads it to 300 frames (705,600 bytes)
        var tracks = new[]
        {
            new AudioTrackSource
            {
                TrackNumber = 1,
                Title = "Short Track",
                TotalPcmBytes = 50 * OpticalConstants.RawSectorSize,
                PcmStreamFactory = () => new MemoryStream(new byte[50 * OpticalConstants.RawSectorSize])
            }
        };

        var result = await engine.BurnAudioTracksAsync(
            recorder,
            tracks,
            new AudioBurnOptions { UseDiscAtOnce = true, EjectWhenDone = true },
            progress: null,
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(provider.FormatTao.Prepared);
        Assert.True(provider.FormatTao.Released);
        Assert.Single(provider.FormatTao.AddedTracks);
        Assert.Equal(300 * OpticalConstants.RawSectorSize, provider.FormatTao.AddedTracks[0].Length);
        Assert.True(provider.ComRecorder.Ejected);
    }

    [Fact]
    public async Task Imapi2BurnEngine_BurnDataStream_SelectsMaximumSpeedFromReverseSortedFirmwareList()
    {
        var persona = new SimulatedDrivePersona
        {
            Name = "ReverseSpeedDrive",
            PhysicalMediaType = ImapiMediaPhysicalType.CdRw,
            MediaState = ImapiFormat2DataMediaState.Blank | ImapiFormat2DataMediaState.Appendable,
            PhysicallyBlank = true,
            TotalSectorsOnMedia = 359_847,
            FreeSectorsOnMedia = 359_847,
            // Fastest speed (10x = 750 sectors/sec) is first, slowest (1x = 75 sectors/sec) is last
            SupportedWriteSpeedsSectorsPerSec = new[] { 750, 600, 300, 75 },
            ExpectedMediaType = DiscMediaType.CdRw,
            ExpectedIsBlank = true,
            ExpectedIsRewritable = true,
            ExpectedIsAppendable = true,
            ExpectedIsOverwriteOnly = false,
            ExpectedRequiresEraseToWrite = false,
            ExpectedHasDataThatBurningReplaces = false,
            ExpectedTotalSectors = 359_847,
            ExpectedFreeSectors = 359_847,
            ExpectedUsedSectors = 0,
            ExpectedNextWritableLba = 0,
            CanBurnDataWithoutFlags = true,
            CanBurnDataWithOverwriteFlag = true,
            CanBurnAudioCd = true,
            CanErase = true
        };

        var provider = new SimulatedImapiComProvider(persona);
        using var recorder = new ImapiDiscRecorder(provider.ComRecorder, "REV-SPEED", provider);
        var engine = new Imapi2BurnEngine(provider);

        using var isoStream = new MemoryStream(new byte[32 * OpticalConstants.DataSectorSize]);
        var result = await engine.BurnDataStreamAsync(recorder, isoStream, new DataBurnOptions(), null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(750, provider.FormatData.RequestedWriteSpeed);
    }

    [Fact]
    public async Task Imapi2BurnEngine_WhenComThrowsCancel0xC0AA0406_ReturnsCancelledAndSpinsDownMotor()
    {
        var persona = new SimulatedDrivePersona
        {
            Name = "CancelSpinDownDrive",
            PhysicalMediaType = ImapiMediaPhysicalType.DvdDashR,
            MediaState = ImapiFormat2DataMediaState.Blank,
            PhysicallyBlank = true,
            TotalSectorsOnMedia = 2_298_496,
            FreeSectorsOnMedia = 2_298_496,
            ExpectedMediaType = DiscMediaType.DvdMinusR,
            ExpectedIsBlank = true,
            ExpectedIsRewritable = false,
            ExpectedIsAppendable = true,
            ExpectedIsOverwriteOnly = false,
            ExpectedRequiresEraseToWrite = false,
            ExpectedHasDataThatBurningReplaces = false,
            ExpectedTotalSectors = 2_298_496,
            ExpectedFreeSectors = 2_298_496,
            ExpectedUsedSectors = 0,
            ExpectedNextWritableLba = 0,
            CanBurnDataWithoutFlags = true,
            CanBurnDataWithOverwriteFlag = true,
            CanBurnAudioCd = false,
            CanErase = false
        };

        var provider = new SimulatedImapiComProvider(persona);
        provider.FormatData.OnWrite = _ => throw new COMException("Write cancelled", unchecked((int)0xC0AA0406));
        provider.FormatErase.OnErase = () => throw new COMException("Erase cancelled", unchecked((int)0xC0AA0406));

        using var recorder = new ImapiDiscRecorder(provider.ComRecorder, "CANCEL-DRIVE", provider);
        var engine = new Imapi2BurnEngine(provider);

        using var isoStream = new MemoryStream(new byte[16 * OpticalConstants.DataSectorSize]);
        var dataRes = await engine.BurnDataStreamAsync(recorder, isoStream, new DataBurnOptions(), null, CancellationToken.None);
        Assert.Equal(BurnStatus.Cancelled, dataRes.Status);
        Assert.Equal(1, provider.SpinDownCount);

        var eraseRes = await engine.EraseMediaAsync(recorder, fullErase: false, null, CancellationToken.None);
        Assert.Equal(BurnStatus.Cancelled, eraseRes.Status);
        Assert.Equal(2, provider.SpinDownCount);
    }

    [Fact]
    public async Task Imapi2BurnEngine_WhenComThrows0xC0AA0405Or0xC0AA0407_TranslatesToActionableMediaError()
    {
        // Case A: 0xC0AA0405 on used OverwriteOnly DVD+RW -> ReplaceExistingDataMessage
        var dvdPlusRw = new SimulatedDrivePersona
        {
            Name = "UsedDvdPlusRwComError",
            PhysicalMediaType = ImapiMediaPhysicalType.DvdPlusRw,
            MediaState = ImapiFormat2DataMediaState.OverwriteOnly,
            TotalSectorsOnMedia = 2_295_104,
            FreeSectorsOnMedia = 2_295_104,
            MountedVolumeSectors = 10_000,
            ExpectedMediaType = DiscMediaType.DvdPlusRw,
            ExpectedIsBlank = false,
            ExpectedIsRewritable = true,
            ExpectedIsAppendable = false,
            ExpectedIsOverwriteOnly = true,
            ExpectedRequiresEraseToWrite = false,
            ExpectedHasDataThatBurningReplaces = true,
            ExpectedTotalSectors = 2_295_104,
            ExpectedFreeSectors = 2_295_104,
            ExpectedUsedSectors = 10_000,
            ExpectedNextWritableLba = 0,
            CanBurnDataWithoutFlags = false,
            CanBurnDataWithOverwriteFlag = true,
            CanBurnAudioCd = false,
            CanErase = true
        };
        var providerA = new SimulatedImapiComProvider(dvdPlusRw);
        providerA.FormatData.OnWrite = _ => throw new COMException("Overwrite not allowed", unchecked((int)0xC0AA0405));
        using var recA = new ImapiDiscRecorder(providerA.ComRecorder, "COM-ERR-A", providerA);
        var engA = new Imapi2BurnEngine(providerA);
        using var isoA = new MemoryStream(new byte[16 * OpticalConstants.DataSectorSize]);
        var resA = await engA.BurnDataStreamAsync(recA, isoA, new DataBurnOptions(), null, CancellationToken.None);
        Assert.False(resA.Success);
        Assert.Equal(DataBurnService.ReplaceExistingDataMessage, resA.ErrorMessage);

        // Case B: 0xC0AA0407 on finalized sequential CD-RW -> EraseRequiredMessage
        var cdRw = new SimulatedDrivePersona
        {
            Name = "FinalizedCdRwComError",
            PhysicalMediaType = ImapiMediaPhysicalType.CdRw,
            MediaState = ImapiFormat2DataMediaState.Finalized,
            TotalSectorsOnMedia = 359_847,
            FreeSectorsOnMedia = 0,
            ScsiReadCapacitySectors = 12_000,
            ExpectedMediaType = DiscMediaType.CdRw,
            ExpectedIsBlank = false,
            ExpectedIsRewritable = true,
            ExpectedIsAppendable = false,
            ExpectedIsOverwriteOnly = false,
            ExpectedRequiresEraseToWrite = true,
            ExpectedHasDataThatBurningReplaces = false,
            ExpectedTotalSectors = 359_847,
            ExpectedFreeSectors = 0,
            ExpectedUsedSectors = 12_000,
            ExpectedNextWritableLba = -1,
            CanBurnDataWithoutFlags = false,
            CanBurnDataWithOverwriteFlag = false,
            CanBurnAudioCd = false,
            CanErase = true
        };
        var providerB = new SimulatedImapiComProvider(cdRw);
        providerB.FormatData.OnWrite = _ => throw new COMException("Media not blank", unchecked((int)0xC0AA0407));
        using var recB = new ImapiDiscRecorder(providerB.ComRecorder, "COM-ERR-B", providerB);
        var engB = new Imapi2BurnEngine(providerB);
        using var isoB = new MemoryStream(new byte[16 * OpticalConstants.DataSectorSize]);
        var resB = await engB.BurnDataStreamAsync(recB, isoB, new DataBurnOptions(), null, CancellationToken.None);
        Assert.False(resB.Success);
        Assert.Equal(DataBurnService.EraseRequiredMessage, resB.ErrorMessage);
    }

    [Fact]
    public async Task BurnDataStreamAsync_AcquiresAndReleasesExclusiveAccess_ToPreventAutoPlayInterference()
    {
        var persona = new SimulatedDrivePersona
        {
            Name = "ExclusiveAccessDataBurnTest",
            PhysicalMediaType = ImapiMediaPhysicalType.DvdDashR,
            MediaState = ImapiFormat2DataMediaState.Blank,
            PhysicallyBlank = true,
            TotalSectorsOnMedia = 2_298_496,
            FreeSectorsOnMedia = 2_298_496,
            ExpectedMediaType = DiscMediaType.DvdMinusR,
            ExpectedIsBlank = true,
            ExpectedIsRewritable = false,
            ExpectedIsAppendable = true,
            ExpectedIsOverwriteOnly = false,
            ExpectedRequiresEraseToWrite = false,
            ExpectedHasDataThatBurningReplaces = false,
            ExpectedTotalSectors = 2_298_496,
            ExpectedFreeSectors = 2_298_496,
            ExpectedUsedSectors = 0,
            ExpectedNextWritableLba = 0,
            CanBurnDataWithoutFlags = true,
            CanBurnDataWithOverwriteFlag = true,
            CanBurnAudioCd = false,
            CanErase = false
        };

        var provider = new SimulatedImapiComProvider(persona);
        bool wasLockedDuringWrite = false;
        provider.FormatData.OnWrite = _ =>
        {
            wasLockedDuringWrite = !string.IsNullOrEmpty(provider.ComRecorder.ExclusiveAccessOwner);
        };

        using var recorder = new ImapiDiscRecorder(provider.ComRecorder, "EXCL-DATA", provider);
        var engine = new Imapi2BurnEngine(provider);
        using var stream = new MemoryStream(new byte[16 * OpticalConstants.DataSectorSize]);

        var result = await engine.BurnDataStreamAsync(recorder, stream, new DataBurnOptions(), null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(wasLockedDuringWrite);
        Assert.Equal(1, provider.ComRecorder.AcquireCount);
        Assert.Equal(1, provider.ComRecorder.ReleaseCount);
        Assert.Equal(string.Empty, provider.ComRecorder.ExclusiveAccessOwner);
    }

    [Theory]
    [InlineData(unchecked((int)0xC0AA0300), "Buffer underrun")]
    [InlineData(unchecked((int)0xC0AA0301), "Unexpected response from optical drive")]
    [InlineData(unchecked((int)0xC0AA020D), "Optical drive command timed out")]
    [InlineData(unchecked((int)0xC0AA0207), "busy or locked by another application")]
    [InlineData(unchecked((int)0xC0AA0210), "busy or locked by another application")]
    [InlineData(unchecked((int)0xC0AA0203), "incompatible with the requested recording mode")]
    [InlineData(unchecked((int)0xC0AA020F), "does not support writing to this media type")]
    public async Task BurnDataStreamAsync_TranslatesCommonImapiErrorCodesIntoActionableDiagnostics(int hresult, string expectedDiagnosticFragment)
    {
        var persona = new SimulatedDrivePersona
        {
            Name = $"ComDiagnostic_{hresult:X8}",
            PhysicalMediaType = ImapiMediaPhysicalType.CdR,
            MediaState = ImapiFormat2DataMediaState.Blank,
            PhysicallyBlank = true,
            TotalSectorsOnMedia = 359_847,
            FreeSectorsOnMedia = 359_847,
            ExpectedMediaType = DiscMediaType.CdR,
            ExpectedIsBlank = true,
            ExpectedIsRewritable = false,
            ExpectedIsAppendable = true,
            ExpectedIsOverwriteOnly = false,
            ExpectedRequiresEraseToWrite = false,
            ExpectedHasDataThatBurningReplaces = false,
            ExpectedTotalSectors = 359_847,
            ExpectedFreeSectors = 359_847,
            ExpectedUsedSectors = 0,
            ExpectedNextWritableLba = 0,
            CanBurnDataWithoutFlags = true,
            CanBurnDataWithOverwriteFlag = true,
            CanBurnAudioCd = true,
            CanErase = false
        };

        var provider = new SimulatedImapiComProvider(persona);
        provider.FormatData.OnWrite = _ => throw new COMException("Device error", hresult);

        using var recorder = new ImapiDiscRecorder(provider.ComRecorder, "DIAG-01", provider);
        var engine = new Imapi2BurnEngine(provider);
        using var stream = new MemoryStream(new byte[16 * OpticalConstants.DataSectorSize]);

        var result = await engine.BurnDataStreamAsync(recorder, stream, new DataBurnOptions(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains($"0x{(uint)hresult:X8}", result.ErrorMessage);
        Assert.Contains(expectedDiagnosticFragment, result.ErrorMessage);
        // Exclusive lock must still be released in finally block after COM error
        Assert.Equal(string.Empty, provider.ComRecorder.ExclusiveAccessOwner);
    }

    #endregion

    #region Disc type from the drive's current profiles

    [Theory]
    [InlineData(new[] { 0x0010, 0x0011 }, DiscMediaType.DvdMinusR)] // ROM listed first: the recordable profile still wins
    [InlineData(new[] { 0x0011, 0x0010 }, DiscMediaType.DvdMinusR)]
    [InlineData(new[] { 0x0008, 0x0009 }, DiscMediaType.CdR)]
    [InlineData(new[] { 0x0008 }, DiscMediaType.CdRom)]
    [InlineData(new[] { 0x0010 }, DiscMediaType.DvdRom)]
    [InlineData(new[] { 0x001B }, DiscMediaType.DvdPlusR)]
    [InlineData(new[] { 0x002B }, DiscMediaType.DvdPlusRDualLayer)]
    [InlineData(new[] { 0x0040, 0x0041 }, DiscMediaType.BdR)]
    public void CurrentProfiles_GiveTheDiscType(int[] profiles, DiscMediaType expected)
    {
        Assert.Equal(expected, ImapiDiscRecorder.MapCurrentProfilesToMediaType(profiles));
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0x0002, 0xFFFF })] // removable disk, vendor profile
    public void NoRecognisedProfile_LeavesTheTypeToTheSize(int[] profiles)
    {
        Assert.Null(ImapiDiscRecorder.MapCurrentProfilesToMediaType(profiles));
    }

    #endregion
}
