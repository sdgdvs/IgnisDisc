using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Read-only media queries that IMAPI does not expose: READ FORMAT CAPACITIES (0x23) for the formatted size of
/// DVD+RW / DVD-RAM / BD-RE, and GET PERFORMANCE (0xAC) type 03h for the write speeds the drive offers for the
/// inserted disc. Parsers are pure and take raw responses, so they are tested from captured bytes.
/// </summary>
public static class MmcMediaReport
{
    public const byte OpCodeReadFormatCapacities = 0x23;
    public const byte OpCodeGetPerformance = 0xAC;
    public const byte PerformanceTypeWriteSpeed = 0x03;

    /// <summary>READ FORMAT CAPACITIES (MMC-6 §6.24): 10 bytes, allocation length in bytes 7–8.</summary>
    public static byte[] BuildReadFormatCapacitiesCdb(ushort allocationLength = 252)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeReadFormatCapacities;
        cdb[7] = (byte)(allocationLength >> 8);
        cdb[8] = (byte)allocationLength;
        return cdb;
    }

    /// <summary>
    /// GET PERFORMANCE (MMC-6 §6.8) asking for Write Speed descriptors (type 03h): 12 bytes, maximum descriptors in
    /// bytes 8–9, type in byte 10. Data Type and Starting LBA are unused for this type and stay zero.
    /// </summary>
    public static byte[] BuildGetPerformanceWriteSpeedCdb(ushort maxDescriptors = 32)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeGetPerformance;
        cdb[8] = (byte)(maxDescriptors >> 8);
        cdb[9] = (byte)maxDescriptors;
        cdb[10] = PerformanceTypeWriteSpeed;
        return cdb;
    }

    /// <summary>Buffer size for <paramref name="maxDescriptors"/> write speed descriptors plus the 8-byte header.</summary>
    public static int WriteSpeedBufferLength(int maxDescriptors) => 8 + 16 * maxDescriptors;

    /// <summary>
    /// Parses a READ FORMAT CAPACITIES response: a 4-byte header whose byte 3 is the capacity list length, then the
    /// Current/Maximum Capacity Descriptor (blocks, descriptor type in byte 4 bits 1–0, block length in bytes 5–7),
    /// then the formattable capacity descriptors (blocks, format type in byte 4 bits 7–2, type parameter). Returns
    /// null when the response is too short or reports no media.
    /// </summary>
    public static FormattedCapacityInfo? ParseFormatCapacities(ReadOnlySpan<byte> response)
    {
        if (response.Length < 12)
        {
            return null;
        }

        int listLength = response[3];
        if (listLength < 8)
        {
            return null;
        }

        int end = Math.Min(response.Length, 4 + listLength);
        ReadOnlySpan<byte> current = response.Slice(4, 8);
        long blocks = BinaryPrimitives.ReadUInt32BigEndian(current);
        var state = (current[4] & 0x03) switch
        {
            1 => FormattedCapacityState.Unformatted,
            2 => FormattedCapacityState.Formatted,
            3 => FormattedCapacityState.NoMedia,
            _ => FormattedCapacityState.Reserved
        };
        if (state is FormattedCapacityState.NoMedia or FormattedCapacityState.Reserved)
        {
            return null;
        }

        int blockLength = (current[5] << 16) | (current[6] << 8) | current[7];

        var formats = new List<FormattableCapacity>();
        for (int offset = 12; offset + 8 <= end; offset += 8)
        {
            ReadOnlySpan<byte> d = response.Slice(offset, 8);
            formats.Add(new FormattableCapacity(
                FormatType: (byte)(d[4] >> 2),
                Blocks: BinaryPrimitives.ReadUInt32BigEndian(d),
                TypeParameter: (d[5] << 16) | (d[6] << 8) | d[7]));
        }

        return new FormattedCapacityInfo(blocks, blockLength, state, formats);
    }

    /// <summary>
    /// Parses a GET PERFORMANCE type 03h response: an 8-byte header (bytes 0–3 = length of what follows the length
    /// field), then 16-byte Write Speed descriptors (End LBA in bytes 4–7, read and write speed in kB/s in bytes
    /// 8–11 and 12–15, Exact in byte 0 bit 1, Write Rotation Control in byte 0 bits 3–4: 0 = default/CLV, 1 = CAV, as
    /// in Windows' CDROM_WRITE_SPEED_DESCRIPTOR). A speed offered both CLV and CAV is kept once per rotation; sorted
    /// fastest first; zero entries skipped.
    /// </summary>
    public static IReadOnlyList<DriveWriteSpeed> ParseWriteSpeeds(ReadOnlySpan<byte> response)
    {
        if (response.Length < 8)
        {
            return Array.Empty<DriveWriteSpeed>();
        }

        long dataLength = BinaryPrimitives.ReadUInt32BigEndian(response);
        long end = Math.Min(response.Length, 4 + dataLength);

        var speeds = new List<DriveWriteSpeed>();
        var seen = new HashSet<(int, WriteRotation)>();
        for (int offset = 8; offset + 16 <= end; offset += 16)
        {
            ReadOnlySpan<byte> d = response.Slice(offset, 16);
            int writeKb = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d.Slice(12, 4)));
            int wrc = (d[0] >> 3) & 0x03;
            var rotation = wrc switch { 0 => WriteRotation.Clv, 1 => WriteRotation.Cav, _ => WriteRotation.Unknown };
            if (writeKb <= 0 || !seen.Add((writeKb, rotation)))
            {
                continue;
            }

            speeds.Add(new DriveWriteSpeed(
                KilobytesPerSecond: writeKb,
                EndLba: (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d.Slice(4, 4))),
                ReadKilobytesPerSecond: (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d.Slice(8, 4))),
                Exact: (d[0] & 0x02) != 0,
                Rotation: rotation));
        }

        speeds.Sort((a, b) => b.KilobytesPerSecond.CompareTo(a.KilobytesPerSecond));
        return speeds;
    }

    /// <summary>
    /// Sends both queries. Never writes and never throws: a command the drive refuses (many drives reject READ
    /// FORMAT CAPACITIES for write-once media) gives null / an empty list for that part.
    /// </summary>
    public static DriveMediaReport Query(IScsiTransport transport, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var limit = timeout ?? TimeSpan.FromSeconds(10);

        FormattedCapacityInfo? capacity = null;
        try
        {
            byte[] buffer = new byte[252];
            var result = transport.ExecuteCommand(BuildReadFormatCapacitiesCdb((ushort)buffer.Length), buffer,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, limit);
            if (result.IsSuccess)
            {
                capacity = ParseFormatCapacities(buffer.AsSpan(0, (int)Math.Min(result.DataTransferLength, (uint)buffer.Length)));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        IReadOnlyList<DriveWriteSpeed> speeds = Array.Empty<DriveWriteSpeed>();
        try
        {
            const ushort maxDescriptors = 32;
            byte[] buffer = new byte[WriteSpeedBufferLength(maxDescriptors)];
            var result = transport.ExecuteCommand(BuildGetPerformanceWriteSpeedCdb(maxDescriptors), buffer,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, limit);
            if (result.IsSuccess)
            {
                speeds = ParseWriteSpeeds(buffer.AsSpan(0, (int)Math.Min(result.DataTransferLength, (uint)buffer.Length)));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        return new DriveMediaReport(capacity, speeds);
    }
}
