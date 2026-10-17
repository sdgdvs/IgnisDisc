using System.Buffers.Binary;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Encodes and decodes raw 2352-byte optical CD sectors (Mode 1 and Mode 2 XA)
/// including 12-byte sync, 4-byte MSF header, 32-bit EDC checksum, and 276-byte RSPC P/Q error correction parity.
/// </summary>
public static class RawSectorCodec
{
    public const int UserDataSectorSize = 2048;
    public const int RawSectorSize = 2352;

    private static readonly byte[] SyncPattern =
    [
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    ];

    private static readonly uint[] EdcTable = CreateEdcTable();
    private static readonly byte[] EccForwardLut = CreateEccForwardLut();
    private static readonly byte[] EccBackwardLut = CreateEccBackwardLut();

    /// <summary>
    /// Converts a 2048-byte Mode 1 user data block into a full 2352-byte raw CD sector.
    /// </summary>
    /// <param name="userData2048">Input user data buffer (must be at least 2048 bytes).</param>
    /// <param name="lba">Logical block address of the sector.</param>
    /// <param name="rawSector2352">Output buffer for the 2352-byte raw sector.</param>
    public static void Convert2048To2352Mode1(ReadOnlySpan<byte> userData2048, int lba, Span<byte> rawSector2352)
    {
        if (userData2048.Length < UserDataSectorSize)
        {
            throw new ArgumentException($"User data buffer must contain at least {UserDataSectorSize} bytes.", nameof(userData2048));
        }
        if (rawSector2352.Length < RawSectorSize)
        {
            throw new ArgumentException($"Raw sector buffer must contain at least {RawSectorSize} bytes.", nameof(rawSector2352));
        }

        // 1. 12-byte sync pattern at 0..11
        SyncPattern.CopyTo(rawSector2352.Slice(0, 12));

        // 2. 4-byte header at 12..15: BCD Minutes, Seconds, Frames + Mode 0x01
        MsfAddress msf = MsfAddress.FromLba(lba);
        rawSector2352[12] = ToBcd(msf.Minutes);
        rawSector2352[13] = ToBcd(msf.Seconds);
        rawSector2352[14] = ToBcd(msf.Frames);
        rawSector2352[15] = 0x01; // Mode 1

        // 3. 2048 bytes user data at 16..2063
        userData2048.Slice(0, UserDataSectorSize).CopyTo(rawSector2352.Slice(16, UserDataSectorSize));

        // 4. 4-byte EDC checksum at 2064..2067 computed over bytes 0..2063 (sync + header + data = 2064 bytes)
        uint edc = ComputeEdc(rawSector2352.Slice(0, 2064));
        BinaryPrimitives.WriteUInt32LittleEndian(rawSector2352.Slice(2064, 4), edc);

        // 5. 8 zero bytes at 2068..2075
        rawSector2352.Slice(2068, 8).Clear();

        // 6. 276-byte RSPC P/Q Parity block:
        // P-parity: 172 bytes at 2076..2247 computed over bytes 12..2075 (2064 bytes)
        ComputeEccBlock(
            rawSector2352.Slice(12, 2064),
            majorCount: 86,
            minorCount: 24,
            majorMult: 2,
            minorInc: 86,
            rawSector2352.Slice(2076, 172));

        // Q-parity: 104 bytes at 2248..2351 computed over bytes 12..2247 (2064 + 172 = 2236 bytes)
        ComputeEccBlock(
            rawSector2352.Slice(12, 2236),
            majorCount: 52,
            minorCount: 43,
            majorMult: 86,
            minorInc: 88,
            rawSector2352.Slice(2248, 104));
    }

    /// <summary>
    /// Allocating convenience overload for Mode 1 sector conversion.
    /// </summary>
    public static byte[] Convert2048To2352Mode1(ReadOnlySpan<byte> userData2048, int lba)
    {
        var raw = new byte[RawSectorSize];
        Convert2048To2352Mode1(userData2048, lba, raw);
        return raw;
    }

    /// <summary>
    /// Converts a 2048-byte Mode 2 XA Form 1 user data block into a full 2352-byte raw CD sector.
    /// </summary>
    public static void Convert2048To2352Mode2Xa(
        ReadOnlySpan<byte> userData2048,
        int lba,
        Span<byte> rawSector2352,
        byte fileNumber = 0,
        byte channelNumber = 0,
        byte submode = 0x08,
        byte codingInfo = 0)
    {
        if (userData2048.Length < UserDataSectorSize)
        {
            throw new ArgumentException($"User data buffer must contain at least {UserDataSectorSize} bytes.", nameof(userData2048));
        }
        if (rawSector2352.Length < RawSectorSize)
        {
            throw new ArgumentException($"Raw sector buffer must contain at least {RawSectorSize} bytes.", nameof(rawSector2352));
        }

        // 1. Sync
        SyncPattern.CopyTo(rawSector2352.Slice(0, 12));

        // 2. Header (Mode 2)
        MsfAddress msf = MsfAddress.FromLba(lba);
        rawSector2352[12] = ToBcd(msf.Minutes);
        rawSector2352[13] = ToBcd(msf.Seconds);
        rawSector2352[14] = ToBcd(msf.Frames);
        rawSector2352[15] = 0x02; // Mode 2

        // 3. Subheader (8 bytes, repeated twice)
        rawSector2352[16] = fileNumber;
        rawSector2352[17] = channelNumber;
        rawSector2352[18] = submode;
        rawSector2352[19] = codingInfo;
        rawSector2352[20] = fileNumber;
        rawSector2352[21] = channelNumber;
        rawSector2352[22] = submode;
        rawSector2352[23] = codingInfo;

        // 4. User data (2048 bytes) at 24..2071
        userData2048.Slice(0, UserDataSectorSize).CopyTo(rawSector2352.Slice(24, UserDataSectorSize));

        // 5. EDC at 2072..2075 over bytes 16..2071 (2056 bytes: subheader + data)
        uint edc = ComputeEdc(rawSector2352.Slice(16, 2056));
        BinaryPrimitives.WriteUInt32LittleEndian(rawSector2352.Slice(2072, 4), edc);

        // 6. ECC for Mode 2 Form 1: header bytes 12..15 are zeroed during parity calculation
        Span<byte> savedHeader = stackalloc byte[4];
        rawSector2352.Slice(12, 4).CopyTo(savedHeader);
        rawSector2352.Slice(12, 4).Clear();

        ComputeEccBlock(
            rawSector2352.Slice(12, 2064),
            majorCount: 86,
            minorCount: 24,
            majorMult: 2,
            minorInc: 86,
            rawSector2352.Slice(2076, 172));

        ComputeEccBlock(
            rawSector2352.Slice(12, 2236),
            majorCount: 52,
            minorCount: 43,
            majorMult: 86,
            minorInc: 88,
            rawSector2352.Slice(2248, 104));

        savedHeader.CopyTo(rawSector2352.Slice(12, 4));
    }

    /// <summary>
    /// Extracts 2048 bytes of user data from a 2352-byte raw CD sector (Mode 1 or Mode 2 XA Form 1)
    /// and validates the EDC checksum.
    /// </summary>
    /// <param name="rawSector2352">Input raw 2352-byte sector buffer.</param>
    /// <param name="userData2048">Destination buffer for 2048 bytes of extracted user data.</param>
    /// <param name="edcValid">True if the EDC checksum validates; false if data corruption was detected.</param>
    /// <returns>True if the EDC checksum was valid.</returns>
    public static bool Extract2048From2352(ReadOnlySpan<byte> rawSector2352, Span<byte> userData2048, out bool edcValid)
    {
        if (rawSector2352.Length < RawSectorSize)
        {
            throw new ArgumentException($"Raw sector buffer must contain at least {RawSectorSize} bytes.", nameof(rawSector2352));
        }
        if (userData2048.Length < UserDataSectorSize)
        {
            throw new ArgumentException($"User data destination buffer must contain at least {UserDataSectorSize} bytes.", nameof(userData2048));
        }

        if (!VerifySync(rawSector2352))
        {
            edcValid = false;
            throw new InvalidDataException("Invalid sync pattern in CD-ROM raw sector.");
        }

        byte mode = rawSector2352[15];
        if (mode == 1)
        {
            // Mode 1: 2048 bytes user data at 16..2063
            rawSector2352.Slice(16, UserDataSectorSize).CopyTo(userData2048.Slice(0, UserDataSectorSize));

            // EDC is 32-bit little-endian at 2064..2067 computed over bytes 0..2063 (2064 bytes)
            uint computedEdc = ComputeEdc(rawSector2352.Slice(0, 2064));
            uint storedEdc = BinaryPrimitives.ReadUInt32LittleEndian(rawSector2352.Slice(2064, 4));
            edcValid = (computedEdc == storedEdc);
            return edcValid;
        }
        else if (mode == 2)
        {
            // Mode 2 XA Form 1: 2048 bytes user data at 24..2071
            rawSector2352.Slice(24, UserDataSectorSize).CopyTo(userData2048.Slice(0, UserDataSectorSize));

            // EDC in Mode 2 Form 1 is at 2072..2075 computed over bytes 16..2071 (2056 bytes)
            uint computedEdc = ComputeEdc(rawSector2352.Slice(16, 2056));
            uint storedEdc = BinaryPrimitives.ReadUInt32LittleEndian(rawSector2352.Slice(2072, 4));
            edcValid = (computedEdc == storedEdc);

            // Fallback check if EDC was computed over bytes 0..2071
            if (!edcValid && ComputeEdc(rawSector2352.Slice(0, 2072)) == storedEdc)
            {
                edcValid = true;
            }

            return edcValid;
        }
        else
        {
            edcValid = false;
            throw new InvalidDataException($"Unsupported CD sector mode: {mode} (expected Mode 1 or Mode 2 XA).");
        }
    }

    /// <summary>
    /// Allocating convenience overload for extracting 2048 user bytes from a raw sector.
    /// </summary>
    public static byte[] Extract2048From2352(ReadOnlySpan<byte> rawSector2352, out bool edcValid)
    {
        var userData = new byte[UserDataSectorSize];
        Extract2048From2352(rawSector2352, userData, out edcValid);
        return userData;
    }

    /// <summary>
    /// Computes the 32-bit CD-ROM Mode 1 / Mode 2 Error Detection Code (EDC)
    /// using reflected polynomial 0xD8018001.
    /// </summary>
    public static uint ComputeEdc(ReadOnlySpan<byte> data)
    {
        uint edc = 0;
        for (int i = 0; i < data.Length; i++)
        {
            edc = (edc >> 8) ^ EdcTable[(edc ^ data[i]) & 0xFF];
        }
        return edc;
    }

    /// <summary>
    /// Verifies that the leading 12 bytes of a raw CD sector match the canonical sync pattern
    /// (0x00, ten 0xFF bytes, 0x00).
    /// </summary>
    public static bool VerifySync(ReadOnlySpan<byte> rawSector)
    {
        if (rawSector.Length < 12)
        {
            return false;
        }
        return rawSector.Slice(0, 12).SequenceEqual(SyncPattern);
    }

    private static void ComputeEccBlock(
        ReadOnlySpan<byte> src,
        int majorCount,
        int minorCount,
        int majorMult,
        int minorInc,
        Span<byte> dest)
    {
        int size = majorCount * minorCount;

        for (int major = 0; major < majorCount; major++)
        {
            int index = (major >> 1) * majorMult + (major & 1);
            byte eccA = 0;
            byte eccB = 0;

            for (int minor = 0; minor < minorCount; minor++)
            {
                byte temp = src[index];
                index += minorInc;
                if (index >= size)
                {
                    index -= size;
                }

                eccA ^= temp;
                eccB ^= temp;
                eccA = EccForwardLut[eccA];
            }

            eccA = EccBackwardLut[EccForwardLut[eccA] ^ eccB];

            dest[major] = eccA;
            dest[major + majorCount] = (byte)(eccA ^ eccB);
        }
    }

    private static byte ToBcd(int value)
    {
        return (byte)(((value / 10) << 4) | (value % 10));
    }

    private static int FromBcd(byte bcd)
    {
        return ((bcd >> 4) * 10) + (bcd & 0x0F);
    }

    private static uint[] CreateEdcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint edc = i;
            for (int j = 0; j < 8; j++)
            {
                edc = (edc & 1) != 0 ? (edc >> 1) ^ 0xD8018001U : (edc >> 1);
            }
            table[i] = edc;
        }
        return table;
    }

    private static byte[] CreateEccForwardLut()
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int j = (i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0);
            lut[i] = (byte)j;
        }
        return lut;
    }

    private static byte[] CreateEccBackwardLut()
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int j = (i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0);
            lut[i ^ j] = (byte)i;
        }
        return lut;
    }
}
