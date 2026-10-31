namespace CdBurner.Core.Primitives;

/// <summary>
/// Optical media geometry, physical sector sizes, capacity limits, and read/write reference speeds.
/// </summary>
public static class OpticalConstants
{
    /// <summary>
    /// Logical sector size in bytes for Mode 1 CD-ROM and DVD media (2,048 bytes).
    /// </summary>
    public const int DataSectorSize = 2048;

    /// <summary>
    /// Physical raw sector size in bytes for CD-DA audio frames and raw CD Mode 1 / Mode 2 sectors (2,352 bytes).
    /// </summary>
    public const int RawSectorSize = 2352;

    /// <summary>
    /// Audio frames / sectors per second for Compact Disc Digital Audio (75 fps).
    /// </summary>
    public const int AudioFramesPerSecond = 75;

    /// <summary>
    /// Number of logical sectors per DVD Error Correction Code (ECC) block (16 sectors).
    /// </summary>
    public const int DvdEccBlockSectors = 16;

    /// <summary>
    /// Size in bytes of a single DVD ECC block (16 * 2,048 = 32,768 bytes).
    /// </summary>
    public const int DvdEccBlockBytes = 32768;

    /// <summary>
    /// Standard Red Book CD Lead-In duration in frames (150 frames = 2 seconds).
    /// </summary>
    public const int CdLeadInFrames = 150;

    /// <summary>
    /// Standard Red Book CD Lead-In duration in seconds (2 seconds).
    /// </summary>
    public const int CdLeadInSeconds = 2;

    /// <summary>
    /// Minimum Red Book audio track length in frames (4.0 seconds = 300 frames).
    /// Red Book specification requires each index/track to be at least 4 seconds long.
    /// </summary>
    public const int CdMinTrackFrames = 300;

    /// <summary>
    /// CD-DA sampling rate in Hertz (44,100 Hz).
    /// </summary>
    public const int CdAudioSampleRate = 44100;

    /// <summary>
    /// CD-DA stereo channel count (2 channels).
    /// </summary>
    public const int CdAudioChannels = 2;

    /// <summary>
    /// CD-DA audio sample bit depth (16-bit linear PCM).
    /// </summary>
    public const int CdAudioBitsPerSample = 16;

    /// <summary>
    /// Bytes per audio stereo sample pair (16-bit * 2 channels = 4 bytes).
    /// </summary>
    public const int CdAudioBytesPerSample = 4;

    /// <summary>
    /// Audio stereo sample pairs per 2352-byte frame (44100 / 75 = 588 stereo samples).
    /// </summary>
    public const int CdAudioSamplesPerFrame = 588;

    // --- Capacity Constants (Sectors) ---

    /// <summary>
    /// Nominal sector capacity for 74-minute CD-R / CD-RW (333,000 sectors = 650 MB).
    /// </summary>
    public const int Cd650MbSectors = 333_000;

    /// <summary>
    /// Nominal sector capacity for 80-minute CD-R / CD-RW (360,000 sectors = 700 MB).
    /// </summary>
    public const int Cd700MbSectors = 360_000;

    /// <summary>
    /// Nominal sector capacity for 90-minute CD-R Overburn (405,000 sectors = 800 MB).
    /// </summary>
    public const int Cd90MinSectors = 405_000;

    /// <summary>
    /// Nominal sector capacity for single layer DVD-R / DVD-RW (DVD-5) (2,298,496 sectors).
    /// </summary>
    public const int DvdMinusR5Sectors = 2_298_496;

    /// <summary>
    /// Nominal sector capacity for single layer DVD+R / DVD+RW (DVD-5) (2,295,104 sectors).
    /// </summary>
    public const int DvdPlusR5Sectors = 2_295_104;

    /// <summary>
    /// Nominal sector capacity for dual layer DVD-R DL (DVD-9) (4,171,712 sectors).
    /// </summary>
    public const int DvdMinusR9Sectors = 4_171_712;

    /// <summary>
    /// Nominal sector capacity for dual layer DVD+R DL (DVD-9) (4,173,824 sectors).
    /// </summary>
    public const int DvdPlusR9Sectors = 4_173_824;

    // --- Capacity Constants (Bytes) ---

    /// <summary>
    /// Nominal data byte capacity for 74-minute / 650 MB CD-R (681,984,000 bytes).
    /// </summary>
    public const long Cd650MbBytes = (long)Cd650MbSectors * DataSectorSize;

    /// <summary>
    /// Nominal data byte capacity for 80-minute / 700 MB CD-R (737,280,000 bytes).
    /// </summary>
    public const long Cd700MbBytes = (long)Cd700MbSectors * DataSectorSize;

    /// <summary>
    /// Nominal data byte capacity for 90-minute / 800 MB CD-R Overburn (829,440,000 bytes).
    /// </summary>
    public const long Cd90MinBytes = (long)Cd90MinSectors * DataSectorSize;

    /// <summary>
    /// Nominal byte capacity for DVD-R / DVD-RW Single Layer (4,707,319,808 bytes).
    /// </summary>
    public const long DvdMinusR5Bytes = (long)DvdMinusR5Sectors * DataSectorSize;

    /// <summary>
    /// Nominal byte capacity for DVD+R / DVD+RW Single Layer (4,700,372,992 bytes).
    /// </summary>
    public const long DvdPlusR5Bytes = (long)DvdPlusR5Sectors * DataSectorSize;

    /// <summary>
    /// Nominal byte capacity for DVD-R DL Dual Layer (8,543,666,176 bytes).
    /// </summary>
    public const long DvdMinusR9Bytes = (long)DvdMinusR9Sectors * DataSectorSize;

    /// <summary>
    /// Nominal byte capacity for DVD+R DL Dual Layer (8,547,991,552 bytes).
    /// </summary>
    public const long DvdPlusR9Bytes = (long)DvdPlusR9Sectors * DataSectorSize;

    // --- 1X Reference Transfer Rates ---

    /// <summary>
    /// CD Mode 1 data transfer rate at 1X speed: 153,600 bytes/sec (150 KiB/s, 75 data sectors/sec).
    /// </summary>
    public const int CdData1XBytesPerSec = 153_600;

    /// <summary>
    /// CD-DA raw audio transfer rate at 1X speed: 176,400 bytes/sec (75 raw 2352-byte sectors/sec).
    /// </summary>
    public const int CdAudio1XBytesPerSec = 176_400;

    /// <summary>
    /// DVD transfer rate at 1X speed: 1,385,000 bytes/sec (approx. 676.25 sectors/sec).
    /// </summary>
    public const int Dvd1XBytesPerSec = 1_385_000;

    /// <summary>
    /// Converts a sector count to total byte size.
    /// </summary>
    public static long SectorsToBytes(int sectors, int sectorSize = DataSectorSize) => (long)sectors * sectorSize;

    /// <summary>
    /// Converts byte count to total number of required sectors (rounding up).
    /// </summary>
    public static int BytesToSectors(long bytes, int sectorSize = DataSectorSize)
    {
        if (sectorSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Sector size must be positive.");
        }
        if (bytes <= 0) return 0;
        return (int)((bytes + sectorSize - 1) / sectorSize);
    }
}
