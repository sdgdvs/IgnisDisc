using System.Globalization;
using System.Runtime.InteropServices;
using CdBurner.Core.Audio;
using CdBurner.Hardware.Interop;

namespace CdBurner.Hardware.Audio;

/// <summary>
/// Writes title, artist, album and track number into an .m4a file through Windows' own property handler (the
/// same store File Explorer's Details pane edits). The handler has no property for ReplayGain, ISRC or cover art,
/// so those are not written to AAC files and the caller is told.
/// </summary>
internal static class ShellAudioTagWriter
{
    private static readonly Guid SummaryInformation = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid MusicProperties = new("56A3372E-CE9C-11D2-9F0E-006097C686F6");

    private static readonly PropertyKey Title = new(SummaryInformation, 2);       // PKEY_Title
    private static readonly PropertyKey Artist = new(MusicProperties, 2);         // PKEY_Music_Artist (multi-valued)
    private static readonly PropertyKey Album = new(MusicProperties, 4);          // PKEY_Music_AlbumTitle
    private static readonly PropertyKey TrackNumber = new(MusicProperties, 7);    // PKEY_Music_TrackNumber

    private const int GpsReadWrite = 0x2;
    private const ushort VtUi4 = 19;
    private const ushort VtLpwstr = 31;

    /// <summary>Returns a warning text when tags were left out or could not be written, otherwise null.</summary>
    public static string? TryWrite(string filePath, AudioTrackTags tags)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Tags were not written (Windows property handlers are not available).";
        }

        IPropertyStore? store = null;
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreFromParsingName(Path.GetFullPath(filePath), IntPtr.Zero, GpsReadWrite, ref iid, out store);
            if (hr != 0 || store is null)
            {
                return $"Tags were not written (Windows could not open the file for tagging, HRESULT 0x{hr:X8}).";
            }

            SetString(store, Title, tags.Title, vector: false);
            SetString(store, Artist, tags.Artist, vector: true);
            SetString(store, Album, tags.Album, vector: false);
            if (tags.TrackNumber > 0)
            {
                var key = TrackNumber;
                var v = new PropVariant { vt = VtUi4, uhVal = (uint)tags.TrackNumber };
                Check(store.SetValue(ref key, ref v), "track number");
            }

            Check(store.Commit(), "saving tags");

            bool dropped = !string.IsNullOrWhiteSpace(tags.ReplayGainTrackGain) || !string.IsNullOrWhiteSpace(tags.Isrc);
            return dropped ? "AAC files carry title, artist, album and track number only (no ReplayGain or ISRC)." : null;
        }
        catch (COMException ex)
        {
            return $"Tags were not written ({ex.Message}).";
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string? value, bool vector)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        PropVariant v;
        if (vector)
        {
            // PKEY_Music_Artist is a list of names; propsys builds the VT_VECTOR | VT_LPWSTR value.
            Check(InitPropVariantFromStringVector([value], 1, out v), "creating tag value");
        }
        else
        {
            v = new PropVariant { vt = VtLpwstr, ptrVal = Marshal.StringToCoTaskMemUni(value) }; // freed by PropVariantClear
        }

        try
        {
            Check(store.SetValue(ref key, ref v), "tag");
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0)
        {
            throw new COMException($"Setting {what} failed with HRESULT 0x{hr:X8}", hr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path, IntPtr bindContext, int flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int InitPropVariantFromStringVector(
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values, uint count, out PropVariant pv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);
}
