using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Copies of the ISO 9660 Primary Volume Descriptor that archive discs carry besides the one at LBA 16, so the ISO
/// 9660 file tree can still be found when LBA 16 is unreadable.
/// <para>
/// The copies are byte-identical to LBA 16. Their sectors are listed in the descriptor's Application Use field
/// (ECMA-119 8.4.32, byte positions 884 to 1395, content not specified by the standard): a marker, the count and
/// each copy's LBA. A copy is only accepted at an LBA its own list names, so the descriptor of an ISO image stored as
/// a file on the disc (which lists LBAs inside that image) is never taken for the disc's own.
/// </para>
/// </summary>
public static class PrimaryVolumeDescriptorCopy
{
    /// <summary>Offset of the Application Use field in a volume descriptor (ECMA-119 8.4.32, BP 884, 0-based 883).</summary>
    public const int ApplicationUseOffset = 883;

    /// <summary>Length of the Application Use field (BP 884 to 1395).</summary>
    public const int ApplicationUseLength = 512;

    /// <summary>Most copy locations the field holds.</summary>
    public const int MaxCopies = 16;

    private const int SectorSize = OpticalConstants.DataSectorSize;

    /// <summary>Marker at the start of the Application Use field.</summary>
    public static readonly byte[] Marker = Encoding.ASCII.GetBytes("IGNISDISC PVD COPIES");

    /// <summary>Writes the marker and the copy LBAs into a descriptor's Application Use field.</summary>
    public static void WriteLocations(Span<byte> descriptor, IReadOnlyList<int> copyLbas)
    {
        ArgumentNullException.ThrowIfNull(copyLbas);
        if (copyLbas.Count == 0 || copyLbas.Count > MaxCopies)
        {
            throw new ArgumentOutOfRangeException(nameof(copyLbas), $"Between 1 and {MaxCopies} copies are supported.");
        }
        var field = descriptor.Slice(ApplicationUseOffset, ApplicationUseLength);
        field.Clear();
        Marker.CopyTo(field);
        field[Marker.Length] = (byte)copyLbas.Count;
        for (int i = 0; i < copyLbas.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(field.Slice(Marker.Length + 1 + 4 * i), (uint)copyLbas[i]);
        }
    }

    /// <summary>The copy LBAs a descriptor lists, or an empty list when it carries no marker.</summary>
    public static IReadOnlyList<int> ReadLocations(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < ApplicationUseOffset + ApplicationUseLength)
        {
            return Array.Empty<int>();
        }
        var field = descriptor.Slice(ApplicationUseOffset, ApplicationUseLength);
        if (!field.StartsWith(Marker))
        {
            return Array.Empty<int>();
        }
        int count = field[Marker.Length];
        if (count is 0 or > MaxCopies)
        {
            return Array.Empty<int>();
        }
        var lbas = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint lba = BinaryPrimitives.ReadUInt32LittleEndian(field.Slice(Marker.Length + 1 + 4 * i));
            if (lba > int.MaxValue)
            {
                return Array.Empty<int>();
            }
            lbas[i] = (int)lba;
        }
        return lbas;
    }

    /// <summary>
    /// True when <paramref name="sector"/> is a Primary Volume Descriptor (type 1, "CD001", version 1, 2,048-byte
    /// logical blocks, matching both-endian fields) that lists <paramref name="lba"/> as one of its copies and whose
    /// listed copies all lie inside its own volume space.
    /// </summary>
    public static bool IsCopyAt(ReadOnlySpan<byte> sector, int lba)
    {
        if (sector.Length < SectorSize || sector[0] != 0x01 || sector[6] != 0x01 ||
            !sector.Slice(1, 5).SequenceEqual("CD001"u8))
        {
            return false;
        }
        uint volumeSpace = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(80));
        ushort blockSize = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(128));
        if (volumeSpace != BinaryPrimitives.ReadUInt32BigEndian(sector.Slice(84)) ||
            blockSize != BinaryPrimitives.ReadUInt16BigEndian(sector.Slice(130)) ||
            blockSize != SectorSize)
        {
            return false;
        }
        var lbas = ReadLocations(sector);
        return lbas.Contains(lba) && lbas.All(l => l > 16 && l < volumeSpace);
    }

    /// <summary>
    /// Looks through the image for a copy of the Primary Volume Descriptor (one pass, reading 1 MiB at a time) and
    /// returns the first one found, or null. For use when LBA 16 is unreadable.
    /// </summary>
    public static (byte[] Descriptor, int Lba)? Find(Stream image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        long sectors = image.Length / SectorSize;
        const int SectorsPerRead = 512;
        byte[] buffer = new byte[SectorsPerRead * SectorSize];
        for (long first = 17; first < sectors; first += SectorsPerRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(SectorsPerRead, sectors - first);
            image.Position = first * SectorSize;
            image.ReadExactly(buffer, 0, count * SectorSize);
            for (int i = 0; i < count; i++)
            {
                var sector = buffer.AsSpan(i * SectorSize, SectorSize);
                long lba = first + i;
                if (sector[0] == 0x01 && lba <= int.MaxValue && IsCopyAt(sector, (int)lba))
                {
                    return (sector.ToArray(), (int)lba);
                }
            }
        }
        return null;
    }
}

/// <summary>One recovery file in an archive disc's zone: its name, first sector and length in sectors.</summary>
public sealed record ArchiveZoneFile(string Name, int StartLba, int SectorCount);

/// <summary>
/// An archive disc's recovery zones as the image builder lays them out: the image size, the sectors holding copies
/// of the Primary Volume Descriptor (one per zone) and the recovery files in each zone (inner, middle, outer). Both
/// lists are empty for images that are not archive discs.
/// </summary>
public sealed record ArchiveZoneLayout(
    int TotalSectors,
    IReadOnlyList<int> DescriptorCopyLbas,
    IReadOnlyList<IReadOnlyList<ArchiveZoneFile>> Zones);
