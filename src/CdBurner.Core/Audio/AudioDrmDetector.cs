using System.Buffers.Binary;
using System.Text;

namespace CdBurner.Core.Audio;

/// <summary>
/// Helper for detecting whether an audio file is protected by Digital Rights Management (DRM),
/// such as Apple FairPlay DRM (.m4p, or encrypted MP4/AAC with 'drms', 'drmi', or 'sinf' atoms)
/// or Windows Media DRM (.wma with ASF Content Encryption Object).
/// </summary>
public static class AudioDrmDetector
{
    private static readonly byte[] DrmsAtom = Encoding.ASCII.GetBytes("drms");
    private static readonly byte[] DrmiAtom = Encoding.ASCII.GetBytes("drmi");
    private static readonly byte[] SinfAtom = Encoding.ASCII.GetBytes("sinf");
    private static readonly byte[] EncaAtom = Encoding.ASCII.GetBytes("enca");
    private static readonly byte[] EncvAtom = Encoding.ASCII.GetBytes("encv");

    // ASF Content Encryption Object GUID: 2211B3FB-BD23-11D2-B4B7-00A0C955FC6E
    private static readonly byte[] AsfContentEncryptionGuid = new byte[]
    {
        0xFB, 0xB3, 0x11, 0x22, 0x23, 0xBD, 0xD2, 0x11, 0xB4, 0xB7, 0x00, 0xA0, 0xC9, 0x55, 0xFC, 0x6E
    };

    // ASF Extended Content Encryption Object GUID: 298AE614-2622-4C17-B935-DAE07EE9289C
    private static readonly byte[] AsfExtendedContentEncryptionGuid = new byte[]
    {
        0x14, 0xE6, 0x8A, 0x29, 0x22, 0x26, 0x17, 0x4C, 0xB9, 0x35, 0xDA, 0xE0, 0x7E, 0xE9, 0x28, 0x9C
    };

    /// <summary>
    /// Checks whether the specified audio file is protected by DRM.
    /// </summary>
    /// <param name="filePath">Absolute or relative file path to the audio file.</param>
    /// <param name="drmDescription">Human-readable description of the DRM scheme if protected.</param>
    /// <returns>True if protected by DRM, otherwise false.</returns>
    public static bool IsDrmProtected(string filePath, out string drmDescription)
    {
        drmDescription = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath);

        // 1. Direct Apple FairPlay .m4p extension (Apple Music subscription downloads)
        if (string.Equals(ext, ".m4p", StringComparison.OrdinalIgnoreCase))
        {
            drmDescription = "Apple FairPlay DRM (.m4p)";
            return true;
        }

        // 2. Check MP4 / AAC / M4A container header atoms for FairPlay encryption
        if (string.Equals(ext, ".m4a", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".aac", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] header = new byte[Math.Min(fs.Length, 16384)];
                int read = fs.Read(header, 0, header.Length);
                var span = header.AsSpan(0, read);

                if (HasMp4DrmAtom(span, DrmsAtom, allowZeroSizeHeader: true) ||
                    HasMp4DrmAtom(span, DrmiAtom, allowZeroSizeHeader: true) ||
                    HasMp4DrmAtom(span, SinfAtom, allowZeroSizeHeader: false) ||
                    HasMp4DrmAtom(span, EncaAtom, allowZeroSizeHeader: false) ||
                    HasMp4DrmAtom(span, EncvAtom, allowZeroSizeHeader: false))
                {
                    drmDescription = "Apple FairPlay DRM (encrypted AAC stream)";
                    return true;
                }
            }
            catch
            {
            }
        }

        // 3. Check WMA files for ASF DRM Content Encryption Objects
        if (string.Equals(ext, ".wma", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] header = new byte[Math.Min(fs.Length, 8192)];
                int read = fs.Read(header, 0, header.Length);
                var span = header.AsSpan(0, read);

                if (span.IndexOf(AsfContentEncryptionGuid) >= 0 || span.IndexOf(AsfExtendedContentEncryptionGuid) >= 0)
                {
                    drmDescription = "Windows Media DRM (encrypted WMA stream)";
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static bool HasMp4DrmAtom(ReadOnlySpan<byte> data, ReadOnlySpan<byte> atomType, bool allowZeroSizeHeader)
    {
        int searchOffset = 0;
        while (searchOffset <= data.Length - 4)
        {
            int relIdx = data.Slice(searchOffset).IndexOf(atomType);
            if (relIdx < 0)
            {
                break;
            }

            int idx = searchOffset + relIdx;
            if (idx >= 4)
            {
                uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(idx - 4, 4));
                if (boxSize is >= 8 and <= 65536)
                {
                    return true;
                }

                if (allowZeroSizeHeader && boxSize == 0)
                {
                    return true;
                }
            }

            searchOffset = idx + 1;
        }

        return false;
    }
}
