using System.Numerics;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>A file par2 will protect: its path relative to the disc root ('/' separators) and size.</summary>
public sealed record Par2SourceFile(string RelativePath, long SizeBytes);

/// <summary>
/// The par2 settings for one disc: block size (<c>-s</c>) and recovery block count (<c>-c</c>), written as a single
/// recovery volume (<c>-n1</c>), plus the space that takes on the disc.
/// </summary>
/// <param name="BlockSize">Bytes per block, a multiple of 2048.</param>
/// <param name="RecoveryBlocks">Recovery blocks: this many damaged blocks anywhere on the disc can be rebuilt.</param>
/// <param name="SourceBlocks">Blocks the protected files are cut into.</param>
/// <param name="RecoverySectors">Sectors the recovery.par2 index and the recovery volume take on the disc.</param>
/// <param name="DataBytes">Bytes of the protected files.</param>
public sealed record Par2RecoveryLayout(int BlockSize, int RecoveryBlocks, int SourceBlocks, int RecoverySectors, long DataBytes)
{
    /// <summary>Recovery data as a share of the protected data, in percent.</summary>
    public double RedundancyPercent => DataBytes > 0 ? 100.0 * RecoveryBlocks * (double)BlockSize / DataBytes : 0;
}

/// <summary>
/// Sizes par2 recovery data to fill the space left on a disc. The size of par2cmdline's output follows exactly
/// from the PAR2 2.0 packet layout (measured against par2cmdline 1.4.0):
/// <list type="bullet">
/// <item>critical packets S = main packet (64 + 12 + 16 per file) + per file a description (64 + 56 + name padded to
/// 4) and, for non-empty files, a checksum packet (64 + 16 + 20 per block);</item>
/// <item>index file = S + creator packet;</item>
/// <item>recovery volume = blocks × (block size + 68) + bitlength(blocks) × S + creator packet.</item>
/// </list>
/// </summary>
public static class Par2RecoverySizer
{
    /// <summary>par2cmdline refuses more than 32,768 recovery blocks and more than 32,768 source blocks.</summary>
    public const int MaxRecoveryBlocks = 32_768;

    /// <summary>Source blocks aimed for: finer blocks lose less to each bad spot, within par2's limit.</summary>
    public const int TargetSourceBlocks = 30_000;

    /// <summary>
    /// par2's time grows with data bytes × recovery blocks; this cap (about two and a half minutes on a fast 24-thread PC,
    /// longer on a slower one) keeps staging practical. Past it, blocks get bigger instead of more numerous.
    /// </summary>
    public const long DefaultMaxWork = 3_000_000_000_000L;

    /// <summary>Upper bound for the creator packet ("Created by par2cmdline version x.y.z"); 104 bytes for 1.4.0.</summary>
    public const int CreatorPacketBytes = 128;

    private const int PacketHeaderBytes = 64;
    private const int RecoveryPacketOverhead = PacketHeaderBytes + 4;

    /// <summary>
    /// Chooses block size and recovery block count so the recovery files fill <paramref name="budgetSectors"/>
    /// (to within one block), or returns null when there is nothing to protect or no room for one block.
    /// </summary>
    public static Par2RecoveryLayout? Plan(IReadOnlyList<Par2SourceFile> files, long budgetSectors, long maxWork = DefaultMaxWork)
    {
        ArgumentNullException.ThrowIfNull(files);
        long dataBytes = files.Sum(f => Math.Max(0, f.SizeBytes));
        if (dataBytes <= 0 || budgetSectors <= 0)
        {
            return null;
        }

        int minBlock = SmallestBlockSize(files);
        int workCap = (int)Math.Clamp(maxWork / dataBytes, 1, MaxRecoveryBlocks);

        int blockSize = minBlock;
        int count = MostBlocksThatFit(files, blockSize, budgetSectors);
        if (count == 0)
        {
            return null;
        }

        if (count >= workCap)
        {
            // More room than the cap allows blocks for (the work cap, or par2's 32,768): use bigger blocks. Take the
            // largest block size at which workCap blocks still fit, then as many blocks of that size as fit (at most
            // one block size's worth past the work cap, never past par2's limit).
            blockSize = LargestBlockSizeThatFits(files, workCap, minBlock, budgetSectors);
            count = Math.Min(MaxRecoveryBlocks, MostBlocksThatFit(files, blockSize, budgetSectors));
        }

        return new Par2RecoveryLayout(blockSize, count, SourceBlockCount(files, blockSize),
            EstimateSectors(files, blockSize, count), dataBytes);
    }

    /// <summary>Sectors the index file and one recovery volume take for these settings (each file rounded up).</summary>
    public static int EstimateSectors(IReadOnlyList<Par2SourceFile> files, int blockSize, int recoveryBlocks)
    {
        long critical = CriticalPacketBytes(files, blockSize);
        long index = critical + CreatorPacketBytes;
        long volume = (long)recoveryBlocks * (blockSize + RecoveryPacketOverhead)
                      + BitLength(recoveryBlocks) * critical + CreatorPacketBytes;
        return (int)Math.Min(int.MaxValue, Sectors(index) + Sectors(volume));
    }

    /// <summary>Bytes of one copy of the critical packets (main, file descriptions, block checksums).</summary>
    public static long CriticalPacketBytes(IReadOnlyList<Par2SourceFile> files, int blockSize)
    {
        long total = PacketHeaderBytes + 12 + 16L * files.Count;
        foreach (var file in files)
        {
            int nameBytes = Encoding.UTF8.GetByteCount(file.RelativePath);
            total += PacketHeaderBytes + 56 + ((nameBytes + 3) & ~3);
            if (file.SizeBytes > 0)
            {
                total += PacketHeaderBytes + 16 + 20 * BlocksIn(file.SizeBytes, blockSize);
            }
        }
        return total;
    }

    /// <summary>Source blocks the files are cut into (each file starts a new block).</summary>
    public static int SourceBlockCount(IReadOnlyList<Par2SourceFile> files, int blockSize) =>
        (int)Math.Min(int.MaxValue, files.Sum(f => BlocksIn(f.SizeBytes, blockSize)));

    private static int SmallestBlockSize(IReadOnlyList<Par2SourceFile> files)
    {
        long dataBytes = files.Sum(f => Math.Max(0, f.SizeBytes));
        int block = RoundUpToSector(Math.Max(OpticalConstants.DataSectorSize, dataBytes / TargetSourceBlocks));
        while (SourceBlockCount(files, block) > TargetSourceBlocks)
        {
            block += OpticalConstants.DataSectorSize * Math.Max(1, block / (16 * OpticalConstants.DataSectorSize));
        }
        return block;
    }

    private static int MostBlocksThatFit(IReadOnlyList<Par2SourceFile> files, int blockSize, long budgetSectors)
    {
        int low = 0, high = MaxRecoveryBlocks;
        while (low < high)
        {
            int mid = low + (high - low + 1) / 2;
            if (EstimateSectors(files, blockSize, mid) <= budgetSectors) low = mid; else high = mid - 1;
        }
        return low;
    }

    private static int LargestBlockSizeThatFits(IReadOnlyList<Par2SourceFile> files, int count, int minBlock, long budgetSectors)
    {
        long low = minBlock / OpticalConstants.DataSectorSize;
        long high = Math.Max(low, budgetSectors * OpticalConstants.DataSectorSize / count / OpticalConstants.DataSectorSize + 1);
        high = Math.Min(high, int.MaxValue / OpticalConstants.DataSectorSize - 1);
        while (low < high)
        {
            long mid = low + (high - low + 1) / 2;
            if (EstimateSectors(files, (int)(mid * OpticalConstants.DataSectorSize), count) <= budgetSectors) low = mid; else high = mid - 1;
        }
        return (int)(low * OpticalConstants.DataSectorSize);
    }

    private static long BlocksIn(long sizeBytes, int blockSize) => sizeBytes <= 0 ? 0 : (sizeBytes + blockSize - 1) / blockSize;

    private static long Sectors(long bytes) => (bytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize;

    private static int RoundUpToSector(long bytes) =>
        (int)Math.Min(int.MaxValue - OpticalConstants.DataSectorSize,
            (bytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize * OpticalConstants.DataSectorSize);

    private static int BitLength(int value) => value <= 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)value);
}
