using System.Globalization;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>Which recovery-session layout a CD-Extra disc gets.</summary>
public enum RecoveryTier
{
    /// <summary>No room for any recovery data.</summary>
    None,

    /// <summary>Recovery data only: PAR2 over the disc's PCM, sized to the space left.</summary>
    A,

    /// <summary>The original files (FLAC vault) too; no room left for parity.</summary>
    B,

    /// <summary>What the PCM cannot carry (tags, artwork, cue, results, hashes) plus the Tier A parity.</summary>
    C
}

/// <summary>Sizing of one tier: the PAR2 layout (when the tier carries parity) and what it repairs.</summary>
/// <param name="Layout">PAR2 settings, or null for a tier without parity.</param>
/// <param name="ToleranceSeconds">Longest unbroken stretch of damaged audio the parity can rebuild, in whole seconds.</param>
/// <param name="Tolerance">Plain-language tolerance for the UI.</param>
public sealed record RecoveryTierSizing(Par2RecoveryLayout? Layout, int ToleranceSeconds, string Tolerance);

/// <summary>The result of <see cref="RecoverySessionBudget.Plan"/>.</summary>
/// <param name="AudioSectors">Sectors the audio tracks take (75 per second).</param>
/// <param name="FixedSectors">Session-1 lead-out, session-2 lead-in and pre-gap, plus the file system.</param>
/// <param name="FreeSectors">What is left for recovery data once audio and fixed costs are paid (0 when over).</param>
/// <param name="VaultSectors">Sectors a FLAC copy of the audio takes.</param>
/// <param name="VaultFits">True when audio, fixed costs and the FLAC vault all fit.</param>
/// <param name="Recommended">Tier the disc gets: B while the vault fits, otherwise A, otherwise None.</param>
/// <param name="TierA">Parity over the whole of the free space; null when it does not fit.</param>
/// <param name="TierB">The vault; carries no parity (a null layout).</param>
/// <param name="TierC">Parity in the free space left after the metadata; null when metadata plus parity do not fit.</param>
public sealed record RecoverySessionPlan(
    long AudioSectors,
    long FixedSectors,
    long FreeSectors,
    long VaultSectors,
    bool VaultFits,
    RecoveryTier Recommended,
    RecoveryTierSizing? TierA,
    RecoveryTierSizing? TierB,
    RecoveryTierSizing? TierC);

/// <summary>
/// Splits the space of a CD-Extra disc (audio in session 1, recovery data in session 2) between audio, fixed costs and
/// recovery data. Pure arithmetic over the inputs: no file or device access.
/// </summary>
public static class RecoverySessionBudget
{
    /// <summary>Sectors an 80-minute CD-R holds, as measured on a real drive.</summary>
    public const long MeasuredCapacitySectors = 359_844;

    /// <summary>Session-1 lead-out (6,750) + session-2 lead-in (4,500) + pre-gap (150).</summary>
    public const long FixedSessionSectors = 6_750 + 4_500 + 150;

    /// <summary>Audio takes 75 sectors a second, 4,500 a minute.</summary>
    public const long SectorsPerAudioMinute = 60L * OpticalConstants.AudioFramesPerSecond;

    /// <summary>
    /// FLAC at about 60 % of PCM plus container overhead: about 3,100 sectors per minute of audio (3,100 / 4,500
    /// of the audio's sectors).
    /// </summary>
    public const long VaultSectorsPerAudioMinute = 3_100;

    /// <summary>Bytes of PCM per second (44,100 Hz, 16 bit, stereo).</summary>
    private const int PcmBytesPerSecond = OpticalConstants.AudioFramesPerSecond * OpticalConstants.RawSectorSize;

    /// <summary>
    /// Sizes the three tiers.
    /// </summary>
    /// <param name="audioSectors">Audio length in sectors (75 per second).</param>
    /// <param name="capacitySectors">Disc capacity; <see cref="MeasuredCapacitySectors"/> for an 80-minute CD-R.</param>
    /// <param name="fileSystemSectors">Sectors the session-2 file system takes.</param>
    /// <param name="metadataSectors">Sectors Tier C's metadata takes (0 skips the Tier C sizing).</param>
    public static RecoverySessionPlan Plan(long audioSectors, long capacitySectors, long fileSystemSectors, long metadataSectors = 0)
    {
        if (audioSectors < 0) throw new ArgumentOutOfRangeException(nameof(audioSectors));
        if (capacitySectors < 0) throw new ArgumentOutOfRangeException(nameof(capacitySectors));
        if (fileSystemSectors < 0) throw new ArgumentOutOfRangeException(nameof(fileSystemSectors));
        if (metadataSectors < 0) throw new ArgumentOutOfRangeException(nameof(metadataSectors));

        long fixedCost = FixedSessionSectors + fileSystemSectors;
        long free = Math.Max(0, capacitySectors - audioSectors - fixedCost);
        long vault = (audioSectors * VaultSectorsPerAudioMinute + SectorsPerAudioMinute - 1) / SectorsPerAudioMinute;
        bool vaultFits = audioSectors > 0 && audioSectors + vault + fixedCost <= capacitySectors;

        RecoveryTierSizing? tierA = SizeParity(audioSectors, free);
        RecoveryTierSizing? tierB = vaultFits
            ? new RecoveryTierSizing(null, 0, "Keeps a FLAC copy of the original files. No room is left for repair data.")
            : null;
        RecoveryTierSizing? tierC = metadataSectors > 0 && metadataSectors < free
            ? SizeParity(audioSectors, free - metadataSectors)
            : null;

        RecoveryTier recommended = vaultFits ? RecoveryTier.B : tierA is not null ? RecoveryTier.A : RecoveryTier.None;
        return new RecoverySessionPlan(audioSectors, fixedCost, free, vault, vaultFits, recommended, tierA, tierB, tierC);
    }

    /// <summary>
    /// The tolerance of parity with <paramref name="layout"/> over audio: a damaged stretch of <c>n</c> bytes touches at
    /// most <c>n / block size + 1</c> blocks, so <c>(recovery blocks - 1) × block size</c> bytes in a row are certain
    /// to be rebuilt. Scattered damage touches one block per spot, so many more short spots are repairable.
    /// </summary>
    public static int ToleranceSeconds(Par2RecoveryLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        long bytes = Math.Max(0, layout.RecoveryBlocks - 1L) * layout.BlockSize;
        return (int)Math.Min(int.MaxValue, bytes / PcmBytesPerSecond);
    }

    /// <summary>Plain-language tolerance line for the UI.</summary>
    public static string DescribeTolerance(Par2RecoveryLayout layout)
    {
        int seconds = ToleranceSeconds(layout);
        string span = seconds == 1 ? "1 second" : seconds.ToString("N0", CultureInfo.InvariantCulture) + " seconds";
        return seconds == 0
            ? "Too little room to repair a whole second of damaged audio."
            : $"Repairs up to {span} of damaged audio in one stretch, or many shorter damaged spots adding up to more.";
    }

    private static RecoveryTierSizing? SizeParity(long audioSectors, long budgetSectors)
    {
        if (audioSectors <= 0 || budgetSectors <= 0)
        {
            return null;
        }

        var pcm = new[] { new Par2SourceFile("audio.pcm", audioSectors * OpticalConstants.RawSectorSize) };
        var layout = Par2RecoverySizer.Plan(pcm, budgetSectors);
        return layout is null ? null : new RecoveryTierSizing(layout, ToleranceSeconds(layout), DescribeTolerance(layout));
    }
}
