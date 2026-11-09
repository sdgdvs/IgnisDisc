using CdBurner.Core.Audio;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Turns CD-Text packs into the R-W sub-channel the host writes into the lead-in. Each lead-in sector carries
/// 96 bytes of sub-channel; the R-W bits (the low six bits of each byte, P and Q left at zero for the drive) hold
/// 72 bytes, which is exactly four 18-byte packs. The pack list repeats until the lead-in is full.
/// </summary>
public static class CdTextLeadInEncoder
{
    public const int SubChannelBytesPerSector = 96;
    private const int PacksPerSector = 4;

    /// <summary>
    /// Encodes the pack set into one full cycle of lead-in sectors. The cycle length is chosen so every sector holds
    /// four whole packs and the packs keep their order across the repeat (as cdrdao does).
    /// </summary>
    /// <returns>A buffer of <c>cycleSectors × 96</c> bytes.</returns>
    public static byte[] EncodeCycle(ReadOnlySpan<byte> packs)
    {
        if (packs.IsEmpty || packs.Length % CdTextPack.PackSizeBytes != 0)
        {
            throw new ArgumentException("CD-Text data must be a whole number of 18-byte packs.", nameof(packs));
        }

        int packCount = packs.Length / CdTextPack.PackSizeBytes;
        if (packCount > 256)
        {
            throw new ArgumentException("CD-Text block is larger than 256 packs.", nameof(packs));
        }

        int cycleSectors = (packCount % 4) switch
        {
            0 => packCount / 4,
            2 => packCount / 2,
            _ => packCount
        };

        byte[] output = new byte[cycleSectors * SubChannelBytesPerSector];
        Span<byte> group = stackalloc byte[PacksPerSector * CdTextPack.PackSizeBytes];
        int packIndex = 0;
        for (int s = 0; s < cycleSectors; s++)
        {
            for (int p = 0; p < PacksPerSector; p++)
            {
                packs.Slice(packIndex * CdTextPack.PackSizeBytes, CdTextPack.PackSizeBytes)
                    .CopyTo(group.Slice(p * CdTextPack.PackSizeBytes));
                packIndex = (packIndex + 1) % packCount;
            }

            SpreadIntoRw(group, output.AsSpan(s * SubChannelBytesPerSector, SubChannelBytesPerSector));
        }

        return output;
    }

    /// <summary>Packs 72 bytes into the R-W bits of 96 sub-channel bytes: every 3 bytes become four 6-bit symbols.</summary>
    public static void SpreadIntoRw(ReadOnlySpan<byte> data72, Span<byte> subChannel96)
    {
        if (data72.Length < 72 || subChannel96.Length < SubChannelBytesPerSector)
        {
            throw new ArgumentException("Need 72 data bytes and a 96-byte destination.");
        }

        for (int i = 0, d = 0; i < SubChannelBytesPerSector; i += 4, d += 3)
        {
            byte a = data72[d], b = data72[d + 1], c = data72[d + 2];
            subChannel96[i] = (byte)((a >> 2) & 0x3F);
            subChannel96[i + 1] = (byte)(((a << 4) & 0x30) | ((b >> 4) & 0x0F));
            subChannel96[i + 2] = (byte)(((b << 2) & 0x3C) | ((c >> 6) & 0x03));
            subChannel96[i + 3] = (byte)(c & 0x3F);
        }
    }

    /// <summary>Reverses <see cref="SpreadIntoRw"/> (used to check what a drive was sent).</summary>
    public static void GatherFromRw(ReadOnlySpan<byte> subChannel96, Span<byte> data72)
    {
        for (int i = 0, d = 0; i < SubChannelBytesPerSector; i += 4, d += 3)
        {
            data72[d] = (byte)(((subChannel96[i] & 0x3F) << 2) | ((subChannel96[i + 1] >> 4) & 0x03));
            data72[d + 1] = (byte)(((subChannel96[i + 1] & 0x0F) << 4) | ((subChannel96[i + 2] >> 2) & 0x0F));
            data72[d + 2] = (byte)(((subChannel96[i + 2] & 0x03) << 6) | (subChannel96[i + 3] & 0x3F));
        }
    }

    /// <summary>
    /// The first LBA of the lead-in for a disc whose lead-in starts at <paramref name="leadInStart"/> (from READ DISC
    /// INFORMATION bytes 17–19). The host writes from there up to LBA −151.
    /// </summary>
    public static int LeadInStartLba(Msf leadInStart)
    {
        if (leadInStart.Minute < 90 || leadInStart.Second > 59 || leadInStart.Frame > 74)
        {
            throw new ArgumentException($"Lead-in start {leadInStart} is not a lead-in time.", nameof(leadInStart));
        }

        return leadInStart.ToLba();
    }
}
