using System.Runtime.InteropServices;

namespace CdBurner.Hardware.Interop;

public static class MediaFoundationConstants
{
    public const uint MF_VERSION = 0x00020070; // (MF_SDK_VERSION << 16) | MF_API_VERSION
    public const uint MFSTARTUP_NOSOCKET = 0x1;
    public const uint MFSTARTUP_FULL = 0x0;

    public const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = 0xFFFFFFFD;
    public const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    public const uint MF_SOURCE_READER_MEDIASOURCE = 0xFFFFFFFF;

    public const uint MF_SOURCE_READERF_ERROR = 0x00000001;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    public const uint MF_SOURCE_READERF_NEWSTREAM = 0x00000004;
    public const uint MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED = 0x00000010;
    public const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020;
    public const uint MF_SOURCE_READERF_STREAMTICK = 0x00000100;
}

public static class MediaFoundationGuids
{
    // Major types
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");

    // Audio Subtypes
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_MP3 = new("00000055-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_FLAC = new("fba4cbf0-2490-410e-a016-e532c195f4da");

    // Media Type Attributes from Windows SDK mfapi.h
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    // Presentation Attributes
    public static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(2)] public ushort wReserved1;
    [FieldOffset(4)] public ushort wReserved2;
    [FieldOffset(6)] public ushort wReserved3;
    [FieldOffset(8)] public long uhVal;
    [FieldOffset(8)] public IntPtr ptrVal;
}

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFAttributes
{
    [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, Out] IntPtr pValue);
    [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out int pType);
    [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int Compare([In, MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, [In] int MatchType, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint punValue);
    [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out ulong punValue);
    [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out double pfValue);
    [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out Guid pguidValue);
    [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcchLength);
    [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, [In] uint cchBufSize, [Out] out uint pcchLength);
    [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out IntPtr ppwszValue, [Out] out uint pcchLength);
    [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcbBlobSize);
    [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] IntPtr pBuf, [In] uint cbBufSize, [Out] out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out IntPtr ppBuf, [Out] out uint pcbSize);
    [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppv);
    [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value);
    [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] uint unValue);
    [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] ulong unValue);
    [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] double fValue);
    [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
    [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr pBuf, [In] uint cbBufSize);
    [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount([Out] out uint pcItems);
    [PreserveSig] int GetItemByIndex([In] uint unIndex, [Out] out Guid pguidKey, [In, Out] IntPtr pValue);
    [PreserveSig] int CopyAllItems([In, MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaType
{
    // Inherited from IMFAttributes in exact vtable order (slots 3..32)
    [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, Out] IntPtr pValue);
    [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out int pType);
    [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int Compare([In, MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, [In] int MatchType, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint punValue);
    [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out ulong punValue);
    [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out double pfValue);
    [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out Guid pguidValue);
    [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcchLength);
    [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, [In] uint cchBufSize, [Out] out uint pcchLength);
    [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out IntPtr ppwszValue, [Out] out uint pcchLength);
    [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcbBlobSize);
    [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] IntPtr pBuf, [In] uint cbBufSize, [Out] out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out IntPtr ppBuf, [Out] out uint pcbSize);
    [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppv);
    [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value);
    [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] uint unValue);
    [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] ulong unValue);
    [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] double fValue);
    [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
    [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr pBuf, [In] uint cbBufSize);
    [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount([Out] out uint pcItems);
    [PreserveSig] int GetItemByIndex([In] uint unIndex, [Out] out Guid pguidKey, [In, Out] IntPtr pValue);
    [PreserveSig] int CopyAllItems([In, MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);

    // IMFMediaType methods (slots 33..37)
    [PreserveSig] int GetMajorType([Out] out Guid pguidMajorType);
    [PreserveSig] int IsCompressedFormat([Out, MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
    [PreserveSig] int IsEqual([In, MarshalAs(UnmanagedType.Interface)] IMFMediaType pIMediaType, [Out] out uint pdwFlags);
    [PreserveSig] int GetRepresentation([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidRepresentation, [Out] out IntPtr ppvRepresentation);
    [PreserveSig] int FreeRepresentation([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidRepresentation, [In] IntPtr pvRepresentation);
}

[ComImport]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaBuffer
{
    [PreserveSig] int Lock([Out] out IntPtr ppbBuffer, [Out] out uint pcbMaxLength, [Out] out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength([Out] out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength([In] uint cbCurrentLength);
    [PreserveSig] int GetMaxLength([Out] out uint pcbMaxLength);
}

[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSample
{
    // Inherited from IMFAttributes in exact vtable order (slots 3..32)
    [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, Out] IntPtr pValue);
    [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out int pType);
    [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int Compare([In, MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, [In] int MatchType, [Out, MarshalAs(UnmanagedType.Bool)] out bool pbResult);
    [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint punValue);
    [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out ulong punValue);
    [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out double pfValue);
    [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out Guid pguidValue);
    [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcchLength);
    [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, [In] uint cchBufSize, [Out] out uint pcchLength);
    [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out IntPtr ppwszValue, [Out] out uint pcchLength);
    [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out uint pcbBlobSize);
    [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] IntPtr pBuf, [In] uint cbBufSize, [Out] out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out IntPtr ppBuf, [Out] out uint pcbSize);
    [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppv);
    [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr Value);
    [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] uint unValue);
    [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] ulong unValue);
    [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] double fValue);
    [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
    [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In] IntPtr pBuf, [In] uint cbBufSize);
    [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount([Out] out uint pcItems);
    [PreserveSig] int GetItemByIndex([In] uint unIndex, [Out] out Guid pguidKey, [In, Out] IntPtr pValue);
    [PreserveSig] int CopyAllItems([In, MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);

    // IMFSample methods (slots 33..46)
    [PreserveSig] int GetSampleFlags([Out] out uint pdwSampleFlags);
    [PreserveSig] int SetSampleFlags([In] uint dwSampleFlags);
    [PreserveSig] int GetSampleTime([Out] out long pllSampleTime);
    [PreserveSig] int SetSampleTime([In] long llSampleTime);
    [PreserveSig] int GetSampleDuration([Out] out long pllSampleDuration);
    [PreserveSig] int SetSampleDuration([In] long llSampleDuration);
    [PreserveSig] int GetBufferCount([Out] out uint pdwBufferCount);
    [PreserveSig] int GetBufferByIndex([In] uint dwIndex, [Out, MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);
    [PreserveSig] int ConvertToContiguousBuffer([Out, MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);
    [PreserveSig] int AddBuffer([In, MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);
    [PreserveSig] int RemoveBufferByIndex([In] uint dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength([Out] out uint pcbTotalLength);
    [PreserveSig] int CopyToBuffer([In, MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);
}

[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection([In] uint dwStreamIndex, [Out, MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
    [PreserveSig] int SetStreamSelection([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.Bool)] bool fSelected);
    [PreserveSig] int GetNativeMediaType([In] uint dwStreamIndex, [In] uint dwMediaTypeIndex, [Out, MarshalAs(UnmanagedType.Interface)] out IMFMediaType? ppMediaType);
    [PreserveSig] int GetCurrentMediaType([In] uint dwStreamIndex, [Out, MarshalAs(UnmanagedType.Interface)] out IMFMediaType? ppMediaType);
    [PreserveSig] int SetCurrentMediaType([In] uint dwStreamIndex, [In] IntPtr pdwReserved, [In, MarshalAs(UnmanagedType.Interface)] IMFMediaType pMediaType);
    [PreserveSig] int SetCurrentPosition([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidTimeFormat, [In] ref PropVariant varPosition);
    [PreserveSig] int ReadSample(
        [In] uint dwStreamIndex,
        [In] uint dwControlFlags,
        [Out] out uint pdwActualStreamIndex,
        [Out] out uint pdwStreamFlags,
        [Out] out long pllTimestamp,
        [Out, MarshalAs(UnmanagedType.Interface)] out IMFSample? ppSample);
    [PreserveSig] int Flush([In] uint dwStreamIndex);
    [PreserveSig] int GetServiceForStream([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidService, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [Out] out IntPtr ppvObject);
    [PreserveSig] int GetPresentationAttribute([In] uint dwStreamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out] out PropVariant pValue);
}

public static class MediaFoundationNative
{
    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint dwFlags = MediaFoundationConstants.MFSTARTUP_FULL);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType([MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppMFType);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        IntPtr pAttributes,
        [MarshalAs(UnmanagedType.Interface)] out IMFSourceReader ppSourceReader);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int PropVariantClear(ref PropVariant pvar);
}
