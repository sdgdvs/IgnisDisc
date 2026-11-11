using System.Runtime.InteropServices;
using CdBurner.Core.Audio;
using CdBurner.Hardware.Interop;

namespace CdBurner.Hardware.Audio;

/// <summary>
/// Reads music tags through Windows' own property handlers (the same values File Explorer shows in its Title,
/// Contributing artists and Album columns). Used for formats IgnisDisc does not parse itself, such as M4A/AAC and
/// WMA. Read-only: the property store is opened without write access. Returns null when Windows has no handler or
/// the file cannot be opened.
/// </summary>
internal static class ShellAudioTagReader
{
    private static readonly Guid SummaryInformation = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid MusicProperties = new("56A3372E-CE9C-11D2-9F0E-006097C686F6");
    private static readonly Guid MediaProperties = new("64440492-4C8B-11D1-8B70-080036B11A03");

    private static readonly PropertyKey Title = new(SummaryInformation, 2);         // PKEY_Title
    private static readonly PropertyKey Artist = new(MusicProperties, 2);           // PKEY_Music_Artist
    private static readonly PropertyKey Album = new(MusicProperties, 4);            // PKEY_Music_AlbumTitle
    private static readonly PropertyKey AlbumArtist = new(MusicProperties, 13);     // PKEY_Music_AlbumArtist
    private static readonly PropertyKey Composer = new(MediaProperties, 19);        // PKEY_Music_Composer

    public static AudioTags? TryRead(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return null;

        IPropertyStore? store = null;
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreFromParsingName(Path.GetFullPath(filePath), IntPtr.Zero, GpsDefault, ref iid, out store);
            if (hr != 0 || store is null) return null;

            var tags = new AudioTags
            {
                Title = ReadString(store, Title),
                Artist = ReadString(store, Artist),
                Album = ReadString(store, Album),
                AlbumArtist = ReadString(store, AlbumArtist),
                Composer = ReadString(store, Composer),
            };
            return tags.IsEmpty ? null : tags;
        }
        catch (COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    private static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        var value = new PropVariant();
        if (store.GetValue(ref key, out value) != 0) return null;
        try
        {
            if (value.vt == 0) return null; // VT_EMPTY
            if (PropVariantToStringAlloc(ref value, out IntPtr text) != 0 || text == IntPtr.Zero) return null;
            try
            {
                string? s = Marshal.PtrToStringUni(text)?.Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private const int GpsDefault = 0;

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

    /// <summary>Converts any string or string-vector value to text (several values are joined with "; ").</summary>
    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToStringAlloc(ref PropVariant value, out IntPtr text);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);
}
