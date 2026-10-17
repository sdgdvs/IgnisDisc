using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CdBurner.Hardware.Interop;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Phase 02: IMAPIv2 COM Interop Declarations, Enums, and ManagedComStream bridge.
/// </summary>
public class Phase02_ComStreamAndInteropTests
{
    #region Imapi2Guids Tests

    [Fact]
    public void Imapi2Guids_AllClsidStrings_ParseAsValidGuids()
    {
        var clsids = new (string Name, string Value, Guid ExpectedSingleton)[]
        {
            (nameof(Imapi2Guids.CLSID_MsftDiscMaster2), Imapi2Guids.CLSID_MsftDiscMaster2, Imapi2Guids.ClsidMsftDiscMaster2),
            (nameof(Imapi2Guids.CLSID_MsftDiscRecorder2), Imapi2Guids.CLSID_MsftDiscRecorder2, Imapi2Guids.ClsidMsftDiscRecorder2),
            (nameof(Imapi2Guids.CLSID_MsftDiscFormat2Data), Imapi2Guids.CLSID_MsftDiscFormat2Data, Imapi2Guids.ClsidMsftDiscFormat2Data),
            (nameof(Imapi2Guids.CLSID_MsftDiscFormat2TAO), Imapi2Guids.CLSID_MsftDiscFormat2TAO, Imapi2Guids.ClsidMsftDiscFormat2TAO),
            (nameof(Imapi2Guids.CLSID_MsftDiscFormat2RawCD), Imapi2Guids.CLSID_MsftDiscFormat2RawCD, Imapi2Guids.ClsidMsftDiscFormat2RawCD),
            (nameof(Imapi2Guids.CLSID_MsftDiscFormat2Erase), Imapi2Guids.CLSID_MsftDiscFormat2Erase, Imapi2Guids.ClsidMsftDiscFormat2Erase),
            (nameof(Imapi2Guids.CLSID_MsftFileSystemImage), Imapi2Guids.CLSID_MsftFileSystemImage, Imapi2Guids.ClsidMsftFileSystemImage),
            (nameof(Imapi2Guids.CLSID_MsftBootOptions), Imapi2Guids.CLSID_MsftBootOptions, Imapi2Guids.ClsidMsftBootOptions),
            (nameof(Imapi2Guids.CLSID_MsftIsoImageManager), Imapi2Guids.CLSID_MsftIsoImageManager, Imapi2Guids.ClsidMsftIsoImageManager)
        };

        foreach (var (name, value, expectedSingleton) in clsids)
        {
            Assert.True(Guid.TryParse(value, out Guid parsedGuid), $"CLSID '{name}' failed to parse: {value}");
            Assert.NotEqual(Guid.Empty, parsedGuid);
            Assert.Equal(expectedSingleton, parsedGuid);
        }
    }

    [Fact]
    public void Imapi2Guids_AllIidStrings_ParseAsValidGuids()
    {
        var iids = new (string Name, string Value, Guid ExpectedSingleton)[]
        {
            (nameof(Imapi2Guids.IID_IDiscMaster2), Imapi2Guids.IID_IDiscMaster2, Imapi2Guids.IidIDiscMaster2),
            (nameof(Imapi2Guids.IID_IDiscRecorder2), Imapi2Guids.IID_IDiscRecorder2, Imapi2Guids.IidIDiscRecorder2),
            (nameof(Imapi2Guids.IID_IDiscRecorder2Ex), Imapi2Guids.IID_IDiscRecorder2Ex, Imapi2Guids.IidIDiscRecorder2Ex),
            (nameof(Imapi2Guids.IID_IDiscFormat2), Imapi2Guids.IID_IDiscFormat2, Imapi2Guids.IidIDiscFormat2),
            (nameof(Imapi2Guids.IID_IDiscFormat2Data), Imapi2Guids.IID_IDiscFormat2Data, Imapi2Guids.IidIDiscFormat2Data),
            (nameof(Imapi2Guids.IID_IDiscFormat2TAO), Imapi2Guids.IID_IDiscFormat2TAO, Imapi2Guids.IidIDiscFormat2TAO),
            (nameof(Imapi2Guids.IID_IDiscFormat2TrackAtOnce), Imapi2Guids.IID_IDiscFormat2TrackAtOnce, Imapi2Guids.IidIDiscFormat2TAO),
            (nameof(Imapi2Guids.IID_IDiscFormat2RawCD), Imapi2Guids.IID_IDiscFormat2RawCD, Imapi2Guids.IidIDiscFormat2RawCD),
            (nameof(Imapi2Guids.IID_IDiscFormat2Erase), Imapi2Guids.IID_IDiscFormat2Erase, Imapi2Guids.IidIDiscFormat2Erase),
            (nameof(Imapi2Guids.IID_IFileSystemImage), Imapi2Guids.IID_IFileSystemImage, Imapi2Guids.IidIFileSystemImage),
            (nameof(Imapi2Guids.IID_IFileSystemImage2), Imapi2Guids.IID_IFileSystemImage2, Imapi2Guids.IidIFileSystemImage2),
            (nameof(Imapi2Guids.IID_IFileSystemImage3), Imapi2Guids.IID_IFileSystemImage3, Imapi2Guids.IidIFileSystemImage3),
            (nameof(Imapi2Guids.IID_IFileSystemImageResult), Imapi2Guids.IID_IFileSystemImageResult, Imapi2Guids.IidIFileSystemImageResult),
            (nameof(Imapi2Guids.IID_IBootOptions), Imapi2Guids.IID_IBootOptions, Imapi2Guids.IidIBootOptions),
            (nameof(Imapi2Guids.IID_IFsiItem), Imapi2Guids.IID_IFsiItem, Imapi2Guids.IidIFsiItem),
            (nameof(Imapi2Guids.IID_IFsiFileItem), Imapi2Guids.IID_IFsiFileItem, Imapi2Guids.IidIFsiFileItem),
            (nameof(Imapi2Guids.IID_IFsiDirectoryItem), Imapi2Guids.IID_IFsiDirectoryItem, Imapi2Guids.IidIFsiDirectoryItem)
        };

        foreach (var (name, value, expectedSingleton) in iids)
        {
            Assert.True(Guid.TryParse(value, out Guid parsedGuid), $"IID '{name}' failed to parse: {value}");
            Assert.NotEqual(Guid.Empty, parsedGuid);
            Assert.Equal(expectedSingleton, parsedGuid);
        }
    }

    [Fact]
    public void Imapi2Guids_AllEventSinkIids_ParseAsValidGuids()
    {
        var sinks = new (string Name, string Value, Guid ExpectedSingleton)[]
        {
            (nameof(Imapi2Guids.IID_DDiscMaster2Events), Imapi2Guids.IID_DDiscMaster2Events, Imapi2Guids.IidDDiscMaster2Events),
            (nameof(Imapi2Guids.IID_DDiscFormat2DataEvents), Imapi2Guids.IID_DDiscFormat2DataEvents, Imapi2Guids.IidDDiscFormat2DataEvents),
            (nameof(Imapi2Guids.IID_DDiscFormat2TAOEvents), Imapi2Guids.IID_DDiscFormat2TAOEvents, Imapi2Guids.IidDDiscFormat2TAOEvents),
            (nameof(Imapi2Guids.IID_DDiscFormat2TrackAtOnceEvents), Imapi2Guids.IID_DDiscFormat2TrackAtOnceEvents, Imapi2Guids.IidDDiscFormat2TrackAtOnceEvents),
            (nameof(Imapi2Guids.IID_DDiscFormat2RawCDEvents), Imapi2Guids.IID_DDiscFormat2RawCDEvents, Imapi2Guids.IidDDiscFormat2RawCDEvents),
            (nameof(Imapi2Guids.IID_DDiscFormat2EraseEvents), Imapi2Guids.IID_DDiscFormat2EraseEvents, Imapi2Guids.IidDDiscFormat2EraseEvents)
        };

        foreach (var (name, value, expectedSingleton) in sinks)
        {
            Assert.True(Guid.TryParse(value, out Guid parsedGuid), $"Event Sink IID '{name}' failed to parse: {value}");
            Assert.NotEqual(Guid.Empty, parsedGuid);
            Assert.Equal(expectedSingleton, parsedGuid);
        }
    }

    [Fact]
    public void Imapi2Guids_CanonicalStringConstants_MatchWindowsSdkValues()
    {
        Assert.Equal("2735412E-7F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.CLSID_MsftDiscMaster2, ignoreCase: true);
        Assert.Equal("2735412D-7F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.CLSID_MsftDiscRecorder2, ignoreCase: true);
        Assert.Equal("2735412A-7F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.CLSID_MsftDiscFormat2Data, ignoreCase: true);
        Assert.Equal("27354129-7F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.CLSID_MsftDiscFormat2TAO, ignoreCase: true);
        Assert.Equal("27354128-7F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.CLSID_MsftDiscFormat2RawCD, ignoreCase: true);
        Assert.Equal("2735412B-7F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.CLSID_MsftDiscFormat2Erase, ignoreCase: true);
        Assert.Equal("2C941FC5-975B-59BE-A960-9A2A262853A5", Imapi2Guids.CLSID_MsftFileSystemImage, ignoreCase: true);
        Assert.Equal("2C941FCE-975B-59BE-A960-9A2A262853A5", Imapi2Guids.CLSID_MsftBootOptions, ignoreCase: true);
        Assert.Equal("CEEE3B62-8F56-4056-869B-EF16917E3EFC", Imapi2Guids.CLSID_MsftIsoImageManager, ignoreCase: true);

        Assert.Equal("27354130-7F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.IID_IDiscMaster2, ignoreCase: true);
        Assert.Equal("27354133-7F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.IID_IDiscRecorder2, ignoreCase: true);
        Assert.Equal("27354153-9F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.IID_IDiscFormat2Data, ignoreCase: true);
        Assert.Equal("27354154-8F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.IID_IDiscFormat2TAO, ignoreCase: true);
        Assert.Equal("27354155-8F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.IID_IDiscFormat2RawCD, ignoreCase: true);
        Assert.Equal("27354156-8F64-5B0F-8F00-5D77AFBE261E", Imapi2Guids.IID_IDiscFormat2Erase, ignoreCase: true);
        Assert.Equal("2C941FE1-975B-59BE-A960-9A2A262853A5", Imapi2Guids.IID_IFileSystemImage, ignoreCase: true);
        Assert.Equal("2C941FD8-975B-59BE-A960-9A2A262853A5", Imapi2Guids.IID_IFileSystemImageResult, ignoreCase: true);
        Assert.Equal("2C941FD4-975B-59BE-A960-9A2A262853A5", Imapi2Guids.IID_IBootOptions, ignoreCase: true);
    }

    #endregion

    #region Imapi2Enums Tests

    [Fact]
    public void FsiFileSystems_BitwiseFlags_CombineToSeven()
    {
        const FsiFileSystems combined = FsiFileSystems.FsiFileSystemISO9660 |
                                        FsiFileSystems.FsiFileSystemJoliet |
                                        FsiFileSystems.FsiFileSystemUDF;

        Assert.Equal(7, (int)combined);
        Assert.Equal((FsiFileSystems)7, combined);

        Assert.Equal(0, (int)FsiFileSystems.FsiFileSystemNone);
        Assert.Equal(1, (int)FsiFileSystems.FsiFileSystemISO9660);
        Assert.Equal(2, (int)FsiFileSystems.FsiFileSystemJoliet);
        Assert.Equal(4, (int)FsiFileSystems.FsiFileSystemUDF);
        Assert.Equal(0x40000000, (int)FsiFileSystems.FsiFileSystemUnknown);

        Assert.True(combined.HasFlag(FsiFileSystems.FsiFileSystemISO9660));
        Assert.True(combined.HasFlag(FsiFileSystems.FsiFileSystemJoliet));
        Assert.True(combined.HasFlag(FsiFileSystems.FsiFileSystemUDF));
    }

    [Fact]
    public void PlatformId_And_EmulationType_MatchStandardValues()
    {
        Assert.Equal(0, (int)PlatformId.PlatformX86);
        Assert.Equal(1, (int)PlatformId.PlatformPowerPC);
        Assert.Equal(2, (int)PlatformId.PlatformMac);
        Assert.Equal(0xEF, (int)PlatformId.PlatformEFI);

        Assert.Equal(0, (int)EmulationType.EmulationNone);
        Assert.Equal(1, (int)EmulationType.Emulation12MFloppy);
        Assert.Equal(2, (int)EmulationType.Emulation144MFloppy);
        Assert.Equal(3, (int)EmulationType.Emulation288MFloppy);
        Assert.Equal(4, (int)EmulationType.EmulationHardDisk);
    }

    [Fact]
    public void ImapiMediaPhysicalType_MatchesSdkDefinitions()
    {
        Assert.Equal(0, (int)ImapiMediaPhysicalType.Unknown);
        Assert.Equal(1, (int)ImapiMediaPhysicalType.CdRom);
        Assert.Equal(2, (int)ImapiMediaPhysicalType.CdR);
        Assert.Equal(3, (int)ImapiMediaPhysicalType.CdRw);
        Assert.Equal(4, (int)ImapiMediaPhysicalType.DvdRom);
        Assert.Equal(5, (int)ImapiMediaPhysicalType.DvdRam);
        Assert.Equal(6, (int)ImapiMediaPhysicalType.DvdPlusR);
        Assert.Equal(7, (int)ImapiMediaPhysicalType.DvdPlusRw);
        Assert.Equal(8, (int)ImapiMediaPhysicalType.DvdPlusRDoubleLayer);
        Assert.Equal(9, (int)ImapiMediaPhysicalType.DvdDashR);
        Assert.Equal(10, (int)ImapiMediaPhysicalType.DvdDashRw);
        Assert.Equal(11, (int)ImapiMediaPhysicalType.DvdDashRDoubleLayer);
        Assert.Equal(12, (int)ImapiMediaPhysicalType.Disk);
        Assert.Equal(13, (int)ImapiMediaPhysicalType.DvdPlusRwDoubleLayer);
        Assert.Equal(14, (int)ImapiMediaPhysicalType.HdDvdRom);
        Assert.Equal(15, (int)ImapiMediaPhysicalType.HdDvdR);
        Assert.Equal(16, (int)ImapiMediaPhysicalType.HdDvdRam);
        Assert.Equal(17, (int)ImapiMediaPhysicalType.BdRom);
        Assert.Equal(18, (int)ImapiMediaPhysicalType.BdR);
        Assert.Equal(19, (int)ImapiMediaPhysicalType.BdRe);
    }

    [Fact]
    public void ImapiFormat2DataMediaState_Flags_MatchSdkDefinitions()
    {
        Assert.Equal(0, (int)ImapiFormat2DataMediaState.Unknown);
        Assert.Equal(0x1, (int)ImapiFormat2DataMediaState.OverwriteOnly);
        Assert.Equal(0x2, (int)ImapiFormat2DataMediaState.Blank);
        Assert.Equal(0x4, (int)ImapiFormat2DataMediaState.Appendable);
        Assert.Equal(0x8, (int)ImapiFormat2DataMediaState.FinalSession);
        Assert.Equal(0x400, (int)ImapiFormat2DataMediaState.Damaged);
        Assert.Equal(0x800, (int)ImapiFormat2DataMediaState.EraseRequired);
        Assert.Equal(0x1000, (int)ImapiFormat2DataMediaState.NonEmptySession);
        Assert.Equal(0x2000, (int)ImapiFormat2DataMediaState.WriteProtected);
        Assert.Equal(0x4000, (int)ImapiFormat2DataMediaState.Finalized);
        Assert.Equal(0x8000, (int)ImapiFormat2DataMediaState.UnsupportedMedia);
    }

    #endregion

    #region ManagedComStream Tests

    [Fact]
    public void ManagedComStream_Write_Seek_Read_Stat_RoundTrip()
    {
        using var memStream = new MemoryStream();
        using var comStream = new ManagedComStream(memStream, leaveOpen: true);

        byte[] writeData = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];

        // 1. Write with pcbWritten pointer
        nint pWritten = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(pWritten, 0);
            comStream.Write(writeData, writeData.Length, pWritten);
            int bytesWritten = Marshal.ReadInt32(pWritten);
            Assert.Equal(writeData.Length, bytesWritten);
        }
        finally
        {
            Marshal.FreeHGlobal(pWritten);
        }

        // 2. Write with pcbWritten == nint.Zero
        byte[] writeData2 = [0x90, 0xA0];
        comStream.Write(writeData2, writeData2.Length, nint.Zero);
        Assert.Equal(10, memStream.Length);

        // 3. Stat check cbSize
        comStream.Stat(out STATSTG statNoName, 1 /* STATFLAG_NONAME */);
        Assert.Equal(10, statNoName.cbSize);
        Assert.Equal(2 /* STGTY_STREAM */, statNoName.type);
        Assert.Null(statNoName.pwcsName);

        comStream.Stat(out STATSTG statWithName, 0 /* STATFLAG_DEFAULT */);
        Assert.Equal(10, statWithName.cbSize);
        Assert.NotNull(statWithName.pwcsName);

        // 4. Seek STREAM_SEEK_SET = 0
        nint pNewPos = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(pNewPos, -1);
            comStream.Seek(2, 0 /* STREAM_SEEK_SET */, pNewPos);
            long newPos = Marshal.ReadInt64(pNewPos);
            Assert.Equal(2, newPos);
            Assert.Equal(2, memStream.Position);

            // Seek STREAM_SEEK_CUR = 1
            comStream.Seek(3, 1 /* STREAM_SEEK_CUR */, pNewPos);
            newPos = Marshal.ReadInt64(pNewPos);
            Assert.Equal(5, newPos);
            Assert.Equal(5, memStream.Position);

            // Seek STREAM_SEEK_END = 2
            comStream.Seek(-2, 2 /* STREAM_SEEK_END */, pNewPos);
            newPos = Marshal.ReadInt64(pNewPos);
            Assert.Equal(8, newPos);
            Assert.Equal(8, memStream.Position);
        }
        finally
        {
            Marshal.FreeHGlobal(pNewPos);
        }

        // 5. Seek with plibNewPosition == nint.Zero
        comStream.Seek(0, 0 /* STREAM_SEEK_SET */, nint.Zero);
        Assert.Equal(0, memStream.Position);

        // 6. Read with pcbRead pointer
        byte[] readBuffer = new byte[4];
        nint pRead = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(pRead, 0);
            comStream.Read(readBuffer, 4, pRead);
            int bytesRead = Marshal.ReadInt32(pRead);
            Assert.Equal(4, bytesRead);
            Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, readBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(pRead);
        }

        // 7. Read with pcbRead == nint.Zero
        byte[] readBuffer2 = new byte[2];
        comStream.Read(readBuffer2, 2, nint.Zero);
        Assert.Equal(new byte[] { 0x50, 0x60 }, readBuffer2);
    }

    [Fact]
    public void ManagedComStream_SetSize_And_Commit_And_Clone()
    {
        using var memStream = new MemoryStream();
        using var comStream = new ManagedComStream(memStream, leaveOpen: false);

        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        comStream.Write(payload, payload.Length, nint.Zero);
        Assert.Equal(8, memStream.Length);

        // SetSize expansion
        comStream.SetSize(64);
        Assert.Equal(64, memStream.Length);

        // Commit and Revert
        comStream.Commit(0);
        comStream.Revert();

        // Clone
        comStream.Seek(4, 0, nint.Zero);
        comStream.Clone(out IStream clonedIStream);
        Assert.NotNull(clonedIStream);

        using var clonedCom = Assert.IsType<ManagedComStream>(clonedIStream);
        Assert.Equal(4, clonedCom.SourceStream.Position);
        Assert.Equal(64, clonedCom.SourceStream.Length);
    }

    [Fact]
    public void ManagedComStream_CopyTo_CopiesBytesBetweenStreams()
    {
        using var sourceMem = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        using var sourceCom = new ManagedComStream(sourceMem, leaveOpen: true);

        using var targetMem = new MemoryStream();
        using var targetCom = new ManagedComStream(targetMem, leaveOpen: true);

        nint pRead = Marshal.AllocHGlobal(sizeof(long));
        nint pWritten = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            sourceCom.CopyTo(targetCom, 6, pRead, pWritten);
            long totalRead = Marshal.ReadInt64(pRead);
            long totalWritten = Marshal.ReadInt64(pWritten);

            Assert.Equal(6, totalRead);
            Assert.Equal(6, totalWritten);
            Assert.Equal(6, targetMem.Length);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, targetMem.ToArray());
        }
        finally
        {
            Marshal.FreeHGlobal(pRead);
            Marshal.FreeHGlobal(pWritten);
        }
    }

    [Fact]
    public void ManagedComStream_Disposal_BehavesAccordingToLeaveOpen()
    {
        var mem1 = new MemoryStream();
        var com1 = new ManagedComStream(mem1, leaveOpen: false);
        com1.Dispose();
        Assert.True(com1.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => mem1.Position);

        var mem2 = new MemoryStream([1, 2, 3]);
        var com2 = new ManagedComStream(mem2, leaveOpen: true);
        com2.Dispose();
        Assert.True(com2.IsDisposed);
        Assert.Equal(3, mem2.Length); // Still accessible!
        mem2.Dispose();
    }

    #endregion

    #region ImapiComActivation Tests

    [Fact]
    public void ImapiComActivation_IsClsidAvailable_ChecksCanonicalClsids()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // On Windows 10/11, MsftDiscMaster2 is a built-in OS component
        bool isMasterAvailable = ImapiComActivation.IsClsidAvailable(Imapi2Guids.CLSID_MsftDiscMaster2);
        Assert.True(isMasterAvailable, "MsftDiscMaster2 CLSID should be registered on Windows.");

        bool isBogusAvailable = ImapiComActivation.IsClsidAvailable(Guid.Empty);
        Assert.False(isBogusAvailable);
    }

    [Fact]
    public void ImapiComActivation_CreateDiscMaster2_ActivatesRealComObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IDiscMaster2? master = ImapiComActivation.CreateDiscMaster2();
        Assert.NotNull(master);

        try
        {
            int count = master.Count;
            Assert.True(count >= 0, $"Recorder count must be non-negative: {count}");

            bool isSupported = master.IsSupportedEnvironment;
            // Should return boolean without throwing COMException
            Assert.True(isSupported || !isSupported);
        }
        finally
        {
            int remaining = ImapiComActivation.SafeRelease(ref master);
            Assert.Null(master);
            Assert.True(remaining >= 0);
        }
    }

    #endregion
}
