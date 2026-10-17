using System.Buffers.Binary;

namespace CdBurner.Core.Audio;

/// <summary>
/// Implements Red Book Annex J / IEC 60908 CRC-16-CCITT checksum for CD-Text packs.
/// Uses generator polynomial 0x1021 (x^16 + x^12 + x^5 + 1), initial value 0x0000,
/// calculated non-reflected, with the final 16-bit remainder bitwise inverted (^ 0xFFFF).
/// </summary>
public static class CdTextCrc16
{
    public const ushort Polynomial = 0x1021;
    public const ushort InitialValue = 0x0000;

    private static readonly ushort[] CrcTable = GenerateTable();

    private static ushort[] GenerateTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort curr = (ushort)(i << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((curr & 0x8000) != 0)
                {
                    curr = (ushort)((curr << 1) ^ Polynomial);
                }
                else
                {
                    curr = (ushort)(curr << 1);
                }
            }
            table[i] = curr;
        }
        return table;
    }

    /// <summary>
    /// Computes the raw (non-inverted) CRC-16-CCITT checksum over the given data.
    /// </summary>
    public static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = InitialValue;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ CrcTable[((crc >> 8) ^ b) & 0xFF]);
        }
        return crc;
    }

    /// <summary>
    /// Computes the standard inverted CD-Text CRC-16-CCITT (polynomial 0x1021, init 0x0000, bitwise inverted ^ 0xFFFF)
    /// over the first 16 bytes of a CD-Text pack.
    /// </summary>
    public static ushort ComputeInvertedCrc16(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> target = data.Length >= 18 ? data.Slice(0, 16) : (data.Length >= 16 ? data.Slice(0, 16) : data);
        return (ushort)(ComputeCrc16(target) ^ 0xFFFF);
    }

    /// <summary>
    /// Computes the inverted CRC-16-CCITT for a CdTextPack.
    /// </summary>
    public static ushort ComputeInvertedCrc16(in CdTextPack pack)
    {
        Span<byte> headerAndPayload = stackalloc byte[16];
        headerAndPayload[0] = (byte)pack.PackType;
        headerAndPayload[1] = pack.TrackNumberIndicator;
        headerAndPayload[2] = pack.SequenceNumber;
        headerAndPayload[3] = pack.BlockCharacterIndicator;
        pack.TextData.CopyTo(headerAndPayload.Slice(4, 12));
        return (ushort)(ComputeCrc16(headerAndPayload) ^ 0xFFFF);
    }

    /// <summary>
    /// Validates the 16-bit inverted CRC of an 18-byte CD-Text pack.
    /// </summary>
    public static bool VerifyPack(ReadOnlySpan<byte> packBytes)
    {
        if (packBytes.Length < CdTextPack.PackSizeBytes)
        {
            return false;
        }

        ushort storedCrc = BinaryPrimitives.ReadUInt16BigEndian(packBytes.Slice(16, 2));
        ushort computedCrc = ComputeInvertedCrc16(packBytes.Slice(0, 16));
        return storedCrc == computedCrc;
    }

    /// <summary>
    /// Validates the CRC of a CdTextPack struct.
    /// </summary>
    public static bool VerifyPack(in CdTextPack pack)
    {
        ushort computed = ComputeInvertedCrc16(pack);
        return pack.InvertedCrc16 == computed;
    }
}
