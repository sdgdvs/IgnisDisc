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

    /// <summary>
    /// Most source blocks aimed for (discs whose data and recovery span up to about 1.1 GiB, so every CD): finer
    /// blocks lose less to each bad spot, within par2's limit.
    /// </summary>
    public const int TargetSourceBlocks = 30_000;

    /// <summary>Fewest source blocks aimed for, reached at about 33 GiB of span (BD-50 and BDXL).</summary>
    public const int MinTargetSourceBlocks = 5_500;

    /// <summary>
    /// Smallest block size: par2 repeats a 20-byte checksum per source block in about a dozen copies, so blocks under
    /// 16 KiB spend much of a small archive's recovery space on checksums instead of recovery data. Files smaller
    /// than this get blocks no bigger than the largest file, which would only be padded with zeros.
    /// </summary>
    public const int MinBlockBytes = 16 * 1024;

    /// <summary>
    /// The work cap (data bytes × recovery blocks) never goes below this: 3e12 is the measured 350 MiB / 8,181-block
    /// CD run (152 s with par2cmdline 1.4.0 on a 24-thread PC), so a half-full CD keeps that block size and time.
    /// </summary>
    public const long MinWorkCap = 3_000_000_000_000L;

    private const double SpanScaleBlocks = 38_000;
    private const double SpanScaleBytes = 700.0 * 1024 * 1024;

    /// <summary>Upper bound for the creator packet ("Created by par2cmdline version x.y.z"); 104 bytes for 1.4.0.</summary>
    public const int CreatorPacketBytes = 128;

    private const int PacketHeaderBytes = 64;
    private const int RecoveryPacketOverhead = PacketHeaderBytes + 4;

    /// <summary>
    /// Chooses block size and recovery block count so the recovery files fill <paramref name="budgetSectors"/>
    /// (to within one block), or returns null when there is nothing to protect or no room for one block.
    /// </summary>
    /// <remarks>
    /// The block count is scaled to the disc. The span (data plus recovery room) sets a target source-block count
    /// (<see cref="TargetSourceBlocksFor"/>); the smallest block size reaching it decides how many recovery blocks
    /// fit. On a full disc at 10 / 15 / 20 % that gives recovery blocks in about 1 : 1.5 : 2, since the span is the
    /// same and only the room grows. The work cap, a quarter of the target (never under <paramref name="minWork"/> /
    /// data bytes), only engages past about 25 % recovery: on a part-filled disc the extra room then goes into bigger
    /// blocks so par2's time stays bounded by what a full disc at 25 % takes.
    /// </remarks>
    public static Par2RecoveryLayout? Plan(IReadOnlyList<Par2SourceFile> files, long budgetSectors, long minWork = MinWorkCap)
    {
        ArgumentNullException.ThrowIfNull(files);
        long dataBytes = files.Sum(f => Math.Max(0, f.SizeBytes));
        if (dataBytes <= 0 || budgetSectors <= 0)
        {
            return null;
        }

        long nonEmpty = NonEmptyCount(files);
        if (nonEmpty > MaxRecoveryBlocks)
        {
            throw new InvalidOperationException(
                $"This disc holds {nonEmpty:N0} non-empty files; par2 recovery data can protect at most {MaxRecoveryBlocks:N0} " +
                "files on one disc. Put fewer files on the disc or turn recovery data off.");
        }

        int target = TargetSourceBlocksFor(dataBytes + budgetSectors * OpticalConstants.DataSectorSize);
        int minBlock = SmallestBlockSize(files, target);
        int workCap = (int)Math.Clamp(Math.Max((target + 3L) / 4, Math.Max(0, minWork) / dataBytes), 1, MaxRecoveryBlocks);

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

    /// <summary>
    /// Source blocks aimed for on a disc whose data plus recovery room is <paramref name="spanBytes"/>:
    /// 38,000 × √(700 MiB / span), kept within 5,500 to 30,000. Bad spots tolerated per disc then fall only with the
    /// square root of the disc size, and par2's time grows with span^1.5 (a fixed count would keep tolerance flat at
    /// the cost of span² time; a fixed time would cut tolerance in proportion to the disc size).
    /// </summary>
    public static int TargetSourceBlocksFor(long spanBytes)
    {
        if (spanBytes <= 0)
        {
            return TargetSourceBlocks;
        }
        double target = Math.Round(SpanScaleBlocks * Math.Sqrt(SpanScaleBytes / spanBytes));
        return (int)Math.Clamp(target, MinTargetSourceBlocks, TargetSourceBlocks);
    }

    /// <summary>
    /// Fewest sectors <see cref="Plan"/> needs to return a layout (one recovery block at the smallest block size),
    /// or 0 when there is nothing to protect.
    /// </summary>
    public static long MinimumBudgetSectors(IReadOnlyList<Par2SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        long dataBytes = files.Sum(f => Math.Max(0, f.SizeBytes));
        if (dataBytes <= 0)
        {
            return 0;
        }

        // A bigger budget widens the span, which can raise the smallest block size: step until the estimate fits.
        long budget = 0;
        for (int i = 0; i < 64; i++)
        {
            int block = SmallestBlockSize(files, TargetSourceBlocksFor(dataBytes + budget * OpticalConstants.DataSectorSize));
            long needed = EstimateSectors(files, block, 1);
            if (needed <= budget)
            {
                return budget;
            }
            budget = Math.Max(budget + 1, needed);
        }
        return budget;
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

    private static int SmallestBlockSize(IReadOnlyList<Par2SourceFile> files, int targetSourceBlocks)
    {
        long dataBytes = files.Sum(f => Math.Max(0, f.SizeBytes));
        long largestFile = files.Max(f => Math.Max(0, f.SizeBytes));
        long floor = Math.Min(MinBlockBytes, RoundUpToSector(Math.Max(OpticalConstants.DataSectorSize, largestFile)));
        // Every non-empty file starts its own block, so with more files than the target no block size can get the
        // count under it: aim for one block per file instead (par2's own limit is checked by Plan).
        long target = Math.Clamp(NonEmptyCount(files), targetSourceBlocks, MaxRecoveryBlocks);
        long block = Math.Max(floor, RoundUpToSector(Math.Max(OpticalConstants.DataSectorSize, dataBytes / target)));
        while (SourceBlockCount(files, (int)block) > target)
        {
            block += (long)OpticalConstants.DataSectorSize * Math.Max(1, block / (16 * OpticalConstants.DataSectorSize));
            if (block > int.MaxValue / 2)
            {
                throw new InvalidOperationException("The files cannot be cut into par2 blocks of a usable size.");
            }
        }
        return (int)block;
    }

    private static long NonEmptyCount(IReadOnlyList<Par2SourceFile> files) => files.Count(f => f.SizeBytes > 0);

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
