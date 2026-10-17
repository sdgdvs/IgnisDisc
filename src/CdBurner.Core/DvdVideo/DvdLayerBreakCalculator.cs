using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Represents a candidate layer break position for Dual-Layer DVD-9 (OTP) media.
/// </summary>
public sealed class DvdLayerBreakCandidate
{
    /// <summary>
    /// Logical Block Address where the layer transition occurs (number of sectors on Layer 0).
    /// </summary>
    public int BreakLba { get; }

    /// <summary>
    /// Total compilation size in sectors.
    /// </summary>
    public int TotalSectors { get; }

    /// <summary>
    /// Number of user data sectors recorded on Layer 0 (LBAs 0 to BreakLba - 1).
    /// </summary>
    public int Layer0Sectors => BreakLba;

    /// <summary>
    /// Number of user data sectors recorded on Layer 1.
    /// </summary>
    public int Layer1Sectors => TotalSectors - BreakLba;

    /// <summary>
    /// Sector difference between Layer 0 and Layer 1 (Layer0 - Layer1 >= 0).
    /// </summary>
    public int SectorDifference => Layer0Sectors - Layer1Sectors;

    /// <summary>
    /// Required padding / dummy lead-out sectors on Layer 1 to match Layer 0 diameter in OTP mode.
    /// </summary>
    public int PaddingSectors => (2 * BreakLba) - TotalSectors;

    /// <summary>
    /// Percentage of total user data sectors allocated to Layer 0.
    /// </summary>
    public double Layer0Percentage => TotalSectors > 0 ? (double)Layer0Sectors / TotalSectors * 100.0 : 0.0;

    /// <summary>
    /// Percentage of total user data sectors allocated to Layer 1.
    /// </summary>
    public double Layer1Percentage => TotalSectors > 0 ? (double)Layer1Sectors / TotalSectors * 100.0 : 0.0;

    /// <summary>
    /// Gets whether this layer break lands on an exact 16-sector (32 KB) ECC block boundary.
    /// </summary>
    public bool IsEccAligned => BreakLba % OpticalConstants.DvdEccBlockSectors == 0;

    /// <summary>
    /// Gets whether this candidate satisfies all DVD-9 OTP physical requirements:
    /// 1. BreakLba % 16 == 0 (ECC block alignment)
    /// 2. Layer0Sectors >= Layer1Sectors (L0 >= L1)
    /// 3. BreakLba <= 2,086,912 (Max Layer 0 capacity).
    /// </summary>
    public bool IsValid =>
        IsEccAligned &&
        Layer0Sectors >= Layer1Sectors &&
        Layer0Sectors <= DvdLayerBreakCalculator.MaxLayer0Sectors;

    public DvdLayerBreakCandidate(int breakLba, int totalSectors)
    {
        BreakLba = breakLba;
        TotalSectors = totalSectors;
    }

    public override string ToString() =>
        $"Layer Break LBA: {BreakLba:N0} (L0: {Layer0Sectors:N0}, L1: {Layer1Sectors:N0}, Diff: {SectorDifference:N0}, Valid: {IsValid})";
}

/// <summary>
/// Dual-Layer (DVD-9) layer break calculator for Opposite Track Path (OTP) DVD media.
/// Validates and selects optimal layer break positions satisfying:
/// 1. Layer0Sectors >= Layer1Sectors (BreakLba >= ceil(TotalSectors / 2.0))
/// 2. BreakLba <= 2,086,912 (max single-layer capacity on DVD-9)
/// 3. BreakLba % 16 == 0 (16-sector / 32 KB DVD ECC block boundary).
/// </summary>
public static class DvdLayerBreakCalculator
{
    /// <summary>
    /// Nominal single-layer DVD-5 capacity limit in 2048-byte sectors (2,295,104 sectors = ~4.7 GB).
    /// Compilations larger than this require DVD-9 dual-layer media.
    /// </summary>
    public const int Dvd5SectorLimit = OpticalConstants.DvdPlusR5Sectors; // 2,295,104

    /// <summary>
    /// Maximum single-layer capacity on DVD-9 (Layer 0 capacity) in 2048-byte sectors (2,086,912 sectors).
    /// </summary>
    public const int MaxLayer0Sectors = 2_086_912;

    /// <summary>
    /// Maximum total DVD-9 dual layer capacity in 2048-byte sectors (4,173,824 sectors = 2 * 2,086,912).
    /// </summary>
    public const int MaxDvd9Sectors = 4_173_824;

    /// <summary>
    /// DVD Error Correction Code (ECC) block size in sectors (16 sectors = 32,768 bytes).
    /// </summary>
    public const int EccBlockSectors = OpticalConstants.DvdEccBlockSectors; // 16

    /// <summary>
    /// Checks whether a compilation sector count requires dual-layer media.
    /// </summary>
    public static bool RequiresLayerBreak(int totalSectors) => totalSectors > Dvd5SectorLimit;

    /// <summary>
    /// Computes the absolute minimum Layer 0 sector count (ceil(totalSectors / 2.0)).
    /// </summary>
    public static int CalculateMinimumLayer0Sectors(int totalSectors)
    {
        if (totalSectors <= 0) return 0;
        return (int)((totalSectors + 1L) / 2L);
    }

    /// <summary>
    /// Evaluates candidate cell boundary LBAs for a DVD-9 compilation and returns all valid layer break candidates.
    /// </summary>
    /// <param name="totalSectors">Total compilation size in sectors (> 2,295,104 for DVD-9).</param>
    /// <param name="candidateCellLbas">Candidate cell boundary LBAs from VTS IFO / VOB structures.</param>
    /// <param name="alignToEcc">If <c>true</c>, non-16-aligned cell boundaries will be rounded down to the nearest 16-sector boundary.</param>
    /// <returns>List of valid Dual-Layer layer break candidates ordered by BreakLba.</returns>
    public static IReadOnlyList<DvdLayerBreakCandidate> CalculateCandidates(
        int totalSectors,
        IEnumerable<int> candidateCellLbas,
        bool alignToEcc = false)
    {
        if (totalSectors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSectors), "Total sectors cannot be negative.");
        }

        if (totalSectors > MaxDvd9Sectors)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSectors),
                $"Compilation size ({totalSectors:N0} sectors) exceeds maximum DVD-9 dual-layer capacity of {MaxDvd9Sectors:N0} sectors.");
        }

        // Single-layer DVD-5 discs do not require a layer break
        if (totalSectors <= Dvd5SectorLimit)
        {
            return Array.Empty<DvdLayerBreakCandidate>();
        }

        int minLayer0 = CalculateMinimumLayer0Sectors(totalSectors);
        int maxLayer0 = MaxLayer0Sectors;

        if (minLayer0 > maxLayer0)
        {
            // Even a 50/50 split exceeds max Layer 0 capacity
            return Array.Empty<DvdLayerBreakCandidate>();
        }

        var results = new SortedDictionary<int, DvdLayerBreakCandidate>();

        foreach (int rawLba in candidateCellLbas)
        {
            int candidateLba = rawLba;

            if (alignToEcc && candidateLba % EccBlockSectors != 0)
            {
                candidateLba = (candidateLba / EccBlockSectors) * EccBlockSectors;
            }

            // 1. Must be aligned to 16-sector ECC block boundary
            if (candidateLba % EccBlockSectors != 0)
            {
                continue;
            }

            // 2. Layer0 >= Layer1: BreakLba >= ceil(totalSectors / 2.0)
            if (candidateLba < minLayer0)
            {
                continue;
            }

            // 3. BreakLba <= 2,086,912 (max single-layer capacity on DVD-9)
            if (candidateLba > maxLayer0)
            {
                continue;
            }

            // Candidate cannot be at or beyond totalSectors
            if (candidateLba >= totalSectors)
            {
                continue;
            }

            var candidate = new DvdLayerBreakCandidate(candidateLba, totalSectors);
            results.TryAdd(candidateLba, candidate);
        }

        return results.Values.ToList();
    }

    /// <summary>
    /// Selects the optimal layer break candidate that minimizes the sector difference between Layer 0 and Layer 1.
    /// If no candidate cell boundaries are provided, calculates the theoretical optimal 16-sector-aligned break.
    /// </summary>
    /// <param name="totalSectors">Total compilation size in sectors.</param>
    /// <param name="candidateCellLbas">Optional list of candidate cell boundaries.</param>
    /// <param name="alignToEcc">Whether to align non-16-aligned candidate cell LBAs.</param>
    /// <returns>The optimal <see cref="DvdLayerBreakCandidate"/>, or <c>null</c> if not applicable or impossible.</returns>
    public static DvdLayerBreakCandidate? SelectOptimalBreak(
        int totalSectors,
        IEnumerable<int>? candidateCellLbas = null,
        bool alignToEcc = false)
    {
        if (totalSectors <= Dvd5SectorLimit || totalSectors > MaxDvd9Sectors)
        {
            return null;
        }

        int minLayer0 = CalculateMinimumLayer0Sectors(totalSectors);
        int maxLayer0 = MaxLayer0Sectors;

        if (minLayer0 > maxLayer0)
        {
            return null;
        }

        if (candidateCellLbas != null)
        {
            var candidates = CalculateCandidates(totalSectors, candidateCellLbas, alignToEcc);
            if (candidates.Count > 0)
            {
                // Find candidate with smallest SectorDifference (closest to 50/50 split)
                return candidates.OrderBy(c => c.SectorDifference).ThenBy(c => c.BreakLba).First();
            }
        }

        // If no candidate list provided or none matched, compute default optimal 16-aligned break
        int defaultBreakLba = ((minLayer0 + EccBlockSectors - 1) / EccBlockSectors) * EccBlockSectors;
        if (defaultBreakLba <= maxLayer0)
        {
            return new DvdLayerBreakCandidate(defaultBreakLba, totalSectors);
        }

        return null;
    }
}
