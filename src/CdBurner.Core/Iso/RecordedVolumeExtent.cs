using System.Buffers.Binary;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Works out how many sectors the file system on a disc occupies, from the disc's own volume descriptors.
/// <para>
/// Needed for rewritable discs that are overwritten in place (DVD+RW, DVD-RAM, BD-RE): the drive reports the whole
/// formatted disc as readable, and Windows reports a mounted UDF volume as ending with its partition, which leaves
/// out the closing anchor in the volume's last sector. Neither is the size of what was burned.
/// </para>
/// </summary>
public static class RecordedVolumeExtent
{
    private const int SectorSize = OpticalConstants.DataSectorSize;
    private const int UdfAnchorLba = 256;
    private const ushort TagAnchor = 2;
    private const ushort TagPartition = 5;
    private const ushort TagTerminator = 8;

    /// <param name="readSector">Returns the 2,048 bytes of one sector, or null if it cannot be read.</param>
    /// <returns>The sector count (last sector + 1), or null when the disc holds neither ISO 9660 nor UDF.</returns>
    public static int? FindEndSector(Func<int, byte[]?> readSector)
    {
        ArgumentNullException.ThrowIfNull(readSector);
        long end = 0;

        // ISO 9660 and Joliet: the primary and supplementary descriptors both give the volume space size.
        for (int lba = 16; lba < 32; lba++)
        {
            byte[]? d = readSector(lba);
            if (d is not { Length: >= SectorSize } || d[1] != 'C' || d[2] != 'D' || d[3] != '0' || d[4] != '0' || d[5] != '1' || d[0] == 255)
            {
                break;
            }
            if (d[0] is 1 or 2)
            {
                end = Math.Max(end, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80)));
            }
        }

        // UDF: the anchor at sector 256 points at both copies of the volume descriptors; the partition descriptor
        // among them gives where the partition ends.
        byte[]? anchor = readSector(UdfAnchorLba);
        if (IsDescriptor(anchor, TagAnchor, UdfAnchorLba))
        {
            uint mainLength = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(16));
            uint mainLocation = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(20));
            uint reserveLength = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(24));
            uint reserveLocation = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(28));
            end = Math.Max(end, mainLocation + Sectors(mainLength));
            end = Math.Max(end, reserveLocation + Sectors(reserveLength));

            long mainSectors = Math.Min(Sectors(mainLength), 64);
            for (long i = 0; i < mainSectors && mainLocation + i < int.MaxValue; i++)
            {
                int lba = (int)(mainLocation + i);
                byte[]? d = readSector(lba);
                if (d is not { Length: >= SectorSize })
                {
                    break;
                }
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(d);
                if (tag == TagTerminator || !IsDescriptor(d, tag, lba))
                {
                    break;
                }
                if (tag == TagPartition)
                {
                    end = Math.Max(end, (long)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(188)) + BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(192)));
                }
            }

            // The closing anchor is the volume's last sector. Only the sector right after everything found so far is
            // looked at: further out, a rewritable disc may still hold an anchor of something burned before.
            if (end is > UdfAnchorLba and < int.MaxValue && IsDescriptor(readSector((int)end), TagAnchor, (int)end))
            {
                end++;
            }
        }

        return end is > 0 and <= int.MaxValue ? (int)end : null;
    }

    private static long Sectors(uint bytes) => ((long)bytes + SectorSize - 1) / SectorSize;

    /// <summary>A UDF descriptor tag with this identifier, a correct tag checksum, and its own address in it.</summary>
    private static bool IsDescriptor([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] byte[]? sector, ushort tagId, int lba)
    {
        if (sector is not { Length: >= SectorSize } || BinaryPrimitives.ReadUInt16LittleEndian(sector) != tagId || tagId == 0)
        {
            return false;
        }

        byte sum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i != 4)
            {
                sum += sector[i];
            }
        }
        return sum == sector[4] && BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(12)) == (uint)lba;
    }
}
