using System.Buffers.Binary;
using System.Text;

namespace CdBurner.Core.Archival;

/// <summary>
/// Cuts par2's single recovery volume into a few smaller volumes, so the image builder can place them in separate
/// zones of the disc (inner, middle, outer) and one scratch along a track cannot take all recovery data at once.
/// <para>
/// PAR2 2.0 files are plain sequences of self-contained packets (spec section "Conventions": every packet starts with
/// the magic <c>PAR2\0PKT</c>, a little-endian 8-byte length that includes the 64-byte header, the MD5 of the rest of
/// the packet, the recovery set id and a 16-byte type). A client loads packets from any file of the set, in any order,
/// so cutting a volume at packet boundaries keeps every packet valid. The cut never changes a byte of a packet.
/// </para>
/// <para>
/// par2cmdline spreads its copies of the critical packets (main, file descriptions, block checksums) evenly through a
/// volume. Every part gets at least one whole copy: a part that would lack one gets a copy added from the volume.
/// </para>
/// </summary>
public static class Par2VolumeSplitter
{
    /// <summary>Volumes an archive disc's recovery data is cut into: one per zone (inner, middle, outer).</summary>
    public const int ZoneCount = 3;

    private const int HeaderBytes = 64;
    private static readonly byte[] Magic = "PAR2\0PKT"u8.ToArray();
    private static readonly byte[] RecoverySliceType = Encoding.ASCII.GetBytes("PAR 2.0\0RecvSlic");
    private static readonly byte[] CreatorType = Encoding.ASCII.GetBytes("PAR 2.0\0Creator\0");

    /// <summary>
    /// Cuts the one recovery volume in <paramref name="recoveryFolder"/> into up to <paramref name="parts"/> volumes
    /// named after the recovery blocks each holds (<c>recovery.vol{first:D5}+{count:D5}.par2</c>, par2cmdline's own
    /// naming), and deletes the original. Returns the new volume paths, or an empty list when nothing was cut: no
    /// single volume, fewer than two recovery blocks, or a file that is not a well-formed PAR2 packet stream.
    /// </summary>
    public static IReadOnlyList<string> Split(string recoveryFolder, int parts = ZoneCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFolder);
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 2);

        if (!Directory.Exists(recoveryFolder))
        {
            return Array.Empty<string>();
        }
        string[] volumes = Directory.GetFiles(recoveryFolder, "recovery.vol*.par2");
        if (volumes.Length != 1)
        {
            return Array.Empty<string>();
        }

        string source = volumes[0];
        List<Packet>? packets;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            packets = ReadPackets(input);
        }
        if (packets == null)
        {
            return Array.Empty<string>();
        }

        int sliceCount = packets.Count(p => p.IsRecoverySlice);
        int partCount = Math.Min(parts, sliceCount);
        if (partCount < 2)
        {
            return Array.Empty<string>();
        }

        // The set of distinct critical packets (by type + body hash), so each part can be checked for a whole copy.
        var critical = packets.Where(p => p.IsCritical).GroupBy(p => p.Key).Select(g => g.First()).ToList();

        var groups = new List<List<Packet>>();
        for (int i = 0; i < partCount; i++)
        {
            groups.Add(new List<Packet>());
        }

        int seenSlices = 0;
        foreach (var packet in packets)
        {
            if (packet.IsRecoverySlice)
            {
                seenSlices++;
            }
            // Slice k (1-based) goes to part floor((k - 1) * partCount / sliceCount); other packets follow the slice
            // before them, so par2's even spread of critical copies is kept.
            int slice = Math.Max(1, seenSlices);
            int part = (int)((long)(slice - 1) * partCount / sliceCount);
            groups[part].Add(packet);
        }

        var written = new List<string>();
        var plan = new List<(string Path, List<Packet> Packets)>();
        foreach (var group in groups)
        {
            var keys = new HashSet<string>(group.Where(p => p.IsCritical).Select(p => p.Key), StringComparer.Ordinal);
            group.AddRange(critical.Where(c => !keys.Contains(c.Key)));

            var exponents = group.Where(p => p.IsRecoverySlice).Select(p => p.Exponent).ToList();
            string name = $"recovery.vol{exponents.Min():D5}+{exponents.Count:D5}.par2";
            plan.Add((Path.Combine(recoveryFolder, name), group));
        }

        if (plan.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Count
            || plan.Any(p => string.Equals(p.Path, source, StringComparison.OrdinalIgnoreCase)))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            byte[] buffer = new byte[1 << 20];
            foreach (var (path, group) in plan)
            {
                // CreateNew: a file already under this name is never overwritten (or deleted on failure).
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                written.Add(path);
                foreach (var packet in group)
                {
                    CopyRange(input, output, packet.Offset, packet.Length, buffer);
                }
            }
        }
        catch
        {
            // Never leave a half-cut set next to the original: remove the parts and keep the one volume.
            foreach (string path in written)
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }

        File.Delete(source);
        return written;
    }

    /// <summary>
    /// The packets of a PAR2 file, read from their headers only, or null when the stream is not a whole, well-formed
    /// packet stream (a length that is not a multiple of 4, shorter than a header, or running past the end).
    /// </summary>
    internal static List<Packet>? ReadPackets(Stream input)
    {
        var packets = new List<Packet>();
        byte[] header = new byte[HeaderBytes + 4];
        long offset = 0;
        long total = input.Length;
        while (offset < total)
        {
            if (total - offset < HeaderBytes)
            {
                return null;
            }
            input.Position = offset;
            int want = (int)Math.Min(header.Length, total - offset);
            input.ReadExactly(header, 0, want);
            if (!header.AsSpan(0, 8).SequenceEqual(Magic))
            {
                return null;
            }
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
            if (length < HeaderBytes || length % 4 != 0 || length > (ulong)(total - offset))
            {
                return null;
            }

            var type = header.AsSpan(48, 16);
            bool isSlice = type.SequenceEqual(RecoverySliceType);
            if (isSlice && (length < HeaderBytes + 4 || want < HeaderBytes + 4))
            {
                return null;
            }
            uint exponent = isSlice ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(HeaderBytes)) : 0;
            bool isCreator = type.SequenceEqual(CreatorType);
            // Bytes 16-31 are the MD5 of the packet body, so type + MD5 identifies a packet's content.
            string key = Convert.ToHexString(header, 48, 16) + Convert.ToHexString(header, 16, 16);
            packets.Add(new Packet(offset, (long)length, isSlice, !isSlice && !isCreator, exponent, key));
            offset += (long)length;
        }
        return packets;
    }

    private static void CopyRange(Stream input, Stream output, long offset, long length, byte[] buffer)
    {
        input.Position = offset;
        long left = length;
        while (left > 0)
        {
            int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0)
            {
                throw new EndOfStreamException("The recovery volume ended while it was being cut.");
            }
            output.Write(buffer, 0, n);
            left -= n;
        }
    }

    internal readonly record struct Packet(long Offset, long Length, bool IsRecoverySlice, bool IsCritical, uint Exponent, string Key);
}
