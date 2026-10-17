namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Structured SCSI sense key, Additional Sense Code (ASC), and Additional Sense Code Qualifier (ASCQ).
/// </summary>
public sealed record ScsiSenseInfo(byte SenseKey, byte Asc, byte Ascq, string Description)
{
    /// <summary>
    /// Gets the human-readable name of the sense key (e.g., "Not Ready", "Medium Error").
    /// </summary>
    public string SenseKeyName => ScsiSenseDecoder.GetSenseKeyName(SenseKey);

    /// <summary>
    /// Formats the sense code triad in standard SCSI hex notation "SK/ASC/ASCQ" (e.g., "02/3A/00").
    /// </summary>
    public string AscAscqHex => $"{SenseKey:X2}/{Asc:X2}/{Ascq:X2}";

    /// <summary>
    /// Returns the complete formatted sense description (e.g., "02/3A/00 Medium Not Present").
    /// </summary>
    public override string ToString() => $"{AscAscqHex} {Description}";
}

/// <summary>
/// Parser and dictionary decoder for SCSI-3 / MMC-5 Sense Data (fixed and descriptor formats).
/// </summary>
public static class ScsiSenseDecoder
{
    // Common SCSI Sense Keys (SAM-5 / SPC-4)
    public const byte SenseKeyNoSense = 0x00;
    public const byte SenseKeyRecoveredError = 0x01;
    public const byte SenseKeyNotReady = 0x02;
    public const byte SenseKeyMediumError = 0x03;
    public const byte SenseKeyHardwareError = 0x04;
    public const byte SenseKeyIllegalRequest = 0x05;
    public const byte SenseKeyUnitAttention = 0x06;
    public const byte SenseKeyDataProtect = 0x07;
    public const byte SenseKeyBlankCheck = 0x08;
    public const byte SenseKeyVendorSpecific = 0x09;
    public const byte SenseKeyCopyAborted = 0x0A;
    public const byte SenseKeyAbortedCommand = 0x0B;
    public const byte SenseKeyEqual = 0x0C;
    public const byte SenseKeyVolumeOverflow = 0x0D;
    public const byte SenseKeyMiscompare = 0x0E;

    // Dictionary mapping (ASC, ASCQ) to standard optical MMC / SCSI descriptions
    private static readonly Dictionary<(byte Asc, byte Ascq), string> AscAscqDescriptions = new()
    {
        // 00h
        [(0x00, 0x00)] = "No Additional Sense Information",
        [(0x00, 0x01)] = "Filemark Detected",
        [(0x00, 0x02)] = "End-of-Partition/Medium Detected",
        [(0x00, 0x03)] = "Setmark Detected",
        [(0x00, 0x04)] = "Beginning-of-Partition/Medium Detected",
        [(0x00, 0x05)] = "End-of-Data Detected",
        [(0x00, 0x06)] = "I/O Process Terminated",
        [(0x00, 0x11)] = "Audio Play Operation in Progress",
        [(0x00, 0x12)] = "Audio Play Operation Paused",
        [(0x00, 0x13)] = "Audio Play Operation Successfully Completed",
        [(0x00, 0x14)] = "Audio Play Operation Stopped Due to Error",
        [(0x00, 0x15)] = "No Current Audio Status to Return",

        // 01h, 02h, 03h
        [(0x01, 0x00)] = "No Index/Sector Signal",
        [(0x02, 0x00)] = "No Seek Complete",
        [(0x03, 0x00)] = "Peripheral Device Write Fault",

        // 04h Logical Unit Not Ready
        [(0x04, 0x00)] = "Logical Unit Not Ready, Cause Not Reportable",
        [(0x04, 0x01)] = "Logical Unit Is in Process of Becoming Ready",
        [(0x04, 0x02)] = "Logical Unit Not Ready, Initializing Command Required",
        [(0x04, 0x03)] = "Logical Unit Not Ready, Manual Intervention Required",
        [(0x04, 0x04)] = "Logical Unit Not Ready, Format in Progress",
        [(0x04, 0x07)] = "Logical Unit Not Ready, Operation in Progress",
        [(0x04, 0x08)] = "Logical Unit Not Ready, Long Write in Progress",

        // 05h, 06h, 08h
        [(0x05, 0x00)] = "Logical Unit Does Not Respond to Selection",
        [(0x06, 0x00)] = "No Reference Position Found",
        [(0x08, 0x00)] = "Logical Unit Communication Failure",
        [(0x08, 0x01)] = "Logical Unit Communication Time-out",
        [(0x08, 0x02)] = "Logical Unit Communication Parity Error",
        [(0x08, 0x03)] = "Logical Unit Communication CRC Error",

        // 09h Servo tracking
        [(0x09, 0x00)] = "Track Following Error",
        [(0x09, 0x01)] = "Tracking Servo Failure",
        [(0x09, 0x02)] = "Focus Servo Failure",
        [(0x09, 0x03)] = "Spindle Servo Failure",
        [(0x09, 0x04)] = "Head Select Fault",

        // 0Ch Write errors
        [(0x0C, 0x00)] = "Write Error",
        [(0x0C, 0x07)] = "Write Error - Recovery Needed",
        [(0x0C, 0x08)] = "Write Error - Recovery Failed",
        [(0x0C, 0x09)] = "Write Error - Loss of Streaming",
        [(0x0C, 0x0F)] = "Defects in Error Window",

        // 11h Read errors
        [(0x11, 0x00)] = "Unrecovered Read Error",
        [(0x11, 0x01)] = "Read Retries Exhausted",
        [(0x11, 0x02)] = "Error Too Long to Correct",
        [(0x11, 0x05)] = "L-EC Uncorrectable Error",
        [(0x11, 0x06)] = "CIRC Unrecovered Error",
        [(0x11, 0x0F)] = "Error Reading UPC/EAN Number",
        [(0x11, 0x10)] = "Error Reading ISRC Number",
        [(0x11, 0x11)] = "Read Error - Loss of Streaming",

        // 15h Positioning errors
        [(0x15, 0x00)] = "Random Positioning Error",
        [(0x15, 0x01)] = "Mechanical Positioning Error",
        [(0x15, 0x02)] = "Positioning Error Detected by Read of Medium",

        // 17h, 18h Recovered data
        [(0x17, 0x00)] = "Recovered Data with No Error Correction Applied",
        [(0x17, 0x01)] = "Recovered Data with Retries",
        [(0x18, 0x00)] = "Recovered Data with Error Correction Applied",
        [(0x18, 0x01)] = "Recovered Data with L-EC",
        [(0x18, 0x02)] = "Recovered Data - Data Rewritten",
        [(0x18, 0x03)] = "Recovered Data with CIRC",
        [(0x18, 0x04)] = "Recovered Data with L-EC (Error Corrected)",
        [(0x18, 0x05)] = "Recovered Data - Recommend Rewrite",

        // 20h, 21h, 24h, 25h, 26h Illegal requests
        [(0x20, 0x00)] = "Invalid Command Operation Code",
        [(0x21, 0x00)] = "Logical Block Address Out of Range",
        [(0x21, 0x01)] = "Invalid Element Address",
        [(0x21, 0x02)] = "Invalid Address For Write",
        [(0x24, 0x00)] = "Invalid Field in CDB",
        [(0x25, 0x00)] = "Logical Unit Not Supported",
        [(0x26, 0x00)] = "Invalid Field in Parameter List",
        [(0x26, 0x01)] = "Parameter Not Supported",
        [(0x26, 0x02)] = "Parameter Value Invalid",

        // 27h Write protect
        [(0x27, 0x00)] = "Write Protected",
        [(0x27, 0x01)] = "Hardware Write Protected",
        [(0x27, 0x02)] = "Logical Unit Software Write Protected",
        [(0x27, 0x03)] = "Associated Write Protect",
        [(0x27, 0x04)] = "Persistent Write Protect",
        [(0x27, 0x05)] = "Permanent Write Protect",
        [(0x27, 0x06)] = "Conditional Write Protect",

        // 28h, 29h, 2Ah, 2Ch Unit Attention
        [(0x28, 0x00)] = "Not Ready to Ready Change, Medium May Have Changed",
        [(0x29, 0x00)] = "Power On, Reset, or Bus Device Reset Occurred",
        [(0x2A, 0x00)] = "Parameters Changed",
        [(0x2A, 0x01)] = "Mode Parameters Changed",
        [(0x2A, 0x02)] = "Log Parameters Changed",
        [(0x2A, 0x03)] = "Reservations Preempted",
        [(0x2C, 0x00)] = "Command Sequence Error",

        // 30h Incompatible medium
        [(0x30, 0x00)] = "Incompatible Medium Installed",
        [(0x30, 0x01)] = "Cannot Read Medium - Unknown Format",
        [(0x30, 0x02)] = "Cannot Read Medium - Incompatible Format",
        [(0x30, 0x03)] = "Cleaning Cartridge Installed",
        [(0x30, 0x04)] = "Cannot Write Medium - Unknown Format",
        [(0x30, 0x05)] = "Cannot Write Medium - Incompatible Format",
        [(0x30, 0x06)] = "Cannot Format Medium - Incompatible Format",
        [(0x30, 0x07)] = "Cleaning Cartridge Installed",
        [(0x30, 0x10)] = "Format Command Failed",

        // 3Ah Medium not present
        [(0x3A, 0x00)] = "Medium Not Present",
        [(0x3A, 0x01)] = "Medium Not Present - Tray Closed",
        [(0x3A, 0x02)] = "Medium Not Present - Tray Open",

        // 3Eh, 3Fh Operating conditions
        [(0x3E, 0x00)] = "Logical Unit Has Not Self-Configured Yet",
        [(0x3F, 0x00)] = "Target Operating Conditions Have Changed",
        [(0x3F, 0x01)] = "Microcode Has Been Changed",
        [(0x3F, 0x02)] = "Changed Operating Conditions",
        [(0x3F, 0x03)] = "Inquiry Data Has Changed",

        // 51h Erase failure
        [(0x51, 0x00)] = "Erase Failure",
        [(0x51, 0x01)] = "Erase Failure - Incomplete Erase Detected",

        // 53h Load/eject
        [(0x53, 0x00)] = "Media Load or Eject Failed",
        [(0x53, 0x02)] = "Medium Removal Prevented",

        // 57h TOC recovery
        [(0x57, 0x00)] = "Unable to Recover Table-of-Contents",

        // 64h Illegal mode
        [(0x64, 0x00)] = "Illegal Mode For This Track",
        [(0x64, 0x01)] = "Invalid Packet Size",

        // 72h Session fixation
        [(0x72, 0x00)] = "Session Fixation Error",
        [(0x72, 0x01)] = "Session Fixation Error - Writing Lead-in",
        [(0x72, 0x02)] = "Session Fixation Error - Writing Lead-out",
        [(0x72, 0x03)] = "Session Fixation Error - Incomplete Track in Session",
        [(0x72, 0x04)] = "Empty or Partially Written Reserved Track",
        [(0x72, 0x05)] = "No More Track Reservations Allowed",

        // 73h CD Control / Calibration
        [(0x73, 0x00)] = "CD Control Error",
        [(0x73, 0x01)] = "Power Calibration Area Almost Full",
        [(0x73, 0x02)] = "Power Calibration Area Is Full",
        [(0x73, 0x03)] = "Power Calibration Area Error",
        [(0x73, 0x04)] = "Program Memory Area Update Failure",
        [(0x73, 0x05)] = "Program Memory Area Is Full",
        [(0x73, 0x06)] = "RMA/PMA Is Almost Full"
    };

    /// <summary>
    /// Decodes a raw SCSI sense data buffer into structured <see cref="ScsiSenseInfo"/>.
    /// Supports both fixed format (response code 0x70 / 0x71) and descriptor format (0x72 / 0x73).
    /// </summary>
    /// <param name="senseBuffer">Raw byte buffer containing SCSI sense data.</param>
    /// <returns>Decoded sense information, or null if the buffer is empty or unparseable.</returns>
    public static ScsiSenseInfo? Decode(ReadOnlySpan<byte> senseBuffer)
    {
        if (senseBuffer.IsEmpty)
        {
            return null;
        }

        byte responseCode = (byte)(senseBuffer[0] & 0x7F);
        byte senseKey = 0;
        byte asc = 0;
        byte ascq = 0;

        if (responseCode is 0x70 or 0x71)
        {
            // Fixed format sense data
            if (senseBuffer.Length > 2)
            {
                senseKey = (byte)(senseBuffer[2] & 0x0F);
            }
            if (senseBuffer.Length > 12)
            {
                asc = senseBuffer[12];
            }
            if (senseBuffer.Length > 13)
            {
                ascq = senseBuffer[13];
            }
        }
        else if (responseCode is 0x72 or 0x73)
        {
            // Descriptor format sense data
            if (senseBuffer.Length > 1)
            {
                senseKey = (byte)(senseBuffer[1] & 0x0F);
            }
            if (senseBuffer.Length > 2)
            {
                asc = senseBuffer[2];
            }
            if (senseBuffer.Length > 3)
            {
                ascq = senseBuffer[3];
            }
        }
        else if (responseCode == 0 && senseBuffer.Length >= 4)
        {
            // Check if all bytes are zero
            bool allZero = true;
            for (int i = 0; i < senseBuffer.Length; i++)
            {
                if (senseBuffer[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }
            if (allZero)
            {
                return new ScsiSenseInfo(0, 0, 0, "No Additional Sense Information");
            }
            return null;
        }
        else
        {
            return null;
        }

        string description = GetAscDescription(senseKey, asc, ascq);
        return new ScsiSenseInfo(senseKey, asc, ascq, description);
    }

    /// <summary>
    /// Looks up a human-readable description for the given sense key, ASC, and ASCQ.
    /// </summary>
    public static string GetAscDescription(byte senseKey, byte asc, byte ascq)
    {
        if (AscAscqDescriptions.TryGetValue((asc, ascq), out string? desc))
        {
            return desc;
        }

        // Check if there is a generic qualifier (ASC, 0x00)
        if (ascq != 0 && AscAscqDescriptions.TryGetValue((asc, 0x00), out string? genericDesc))
        {
            return $"{genericDesc} (Qualifier 0x{ascq:X2})";
        }

        return $"{GetSenseKeyName(senseKey)}: ASC 0x{asc:X2}, ASCQ 0x{ascq:X2}";
    }

    /// <summary>
    /// Gets the standard name for a SCSI Sense Key.
    /// </summary>
    public static string GetSenseKeyName(byte senseKey) => (senseKey & 0x0F) switch
    {
        SenseKeyNoSense => "No Sense",
        SenseKeyRecoveredError => "Recovered Error",
        SenseKeyNotReady => "Not Ready",
        SenseKeyMediumError => "Medium Error",
        SenseKeyHardwareError => "Hardware Error",
        SenseKeyIllegalRequest => "Illegal Request",
        SenseKeyUnitAttention => "Unit Attention",
        SenseKeyDataProtect => "Data Protect",
        SenseKeyBlankCheck => "Blank Check",
        SenseKeyVendorSpecific => "Vendor Specific",
        SenseKeyCopyAborted => "Copy Aborted",
        SenseKeyAbortedCommand => "Aborted Command",
        SenseKeyEqual => "Equal",
        SenseKeyVolumeOverflow => "Volume Overflow",
        SenseKeyMiscompare => "Miscompare",
        _ => $"Sense Key 0x{senseKey:X2}"
    };
}
