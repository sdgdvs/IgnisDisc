namespace CdBurner.Hardware.Simulation;

/// <summary>
/// Exception thrown or reported when an optical read error occurs on simulated or physical media.
/// </summary>
public class OpticalReadException : IOException
{
    /// <summary>
    /// Logical Block Address (LBA) where the unrecovered read error occurred.
    /// </summary>
    public int Lba { get; }

    /// <summary>
    /// SCSI Sense Key (default 0x03: Medium Error).
    /// </summary>
    public byte SenseKey { get; }

    /// <summary>
    /// Additional Sense Code (ASC, default 0x11: Unrecovered Read Error).
    /// </summary>
    public byte Asc { get; }

    /// <summary>
    /// Additional Sense Code Qualifier (ASCQ, default 0x00).
    /// </summary>
    public byte Ascq { get; }

    public OpticalReadException(int lba, string message)
        : base(message)
    {
        Lba = lba;
        SenseKey = 0x03; // Medium Error
        Asc = 0x11;      // Unrecovered Read Error
        Ascq = 0x00;
    }

    public OpticalReadException(int lba, string message, Exception innerException)
        : base(message, innerException)
    {
        Lba = lba;
        SenseKey = 0x03;
        Asc = 0x11;
        Ascq = 0x00;
    }

    public OpticalReadException(int lba, byte senseKey, byte asc, byte ascq, string message)
        : base(message)
    {
        Lba = lba;
        SenseKey = senseKey;
        Asc = asc;
        Ascq = ascq;
    }
}
