using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Utilities;

/// <summary>
/// DVD Book Type specifications according to MMC-5 and DVD+R / DVD-ROM physical format specifications.
/// </summary>
public enum DvdBookType : byte
{
    /// <summary>
    /// DVD-ROM format (0x00) - Maximum legacy compatibility with standalone DVD players and game consoles.
    /// </summary>
    DvdRom = 0x00,

    /// <summary>
    /// DVD-RAM format (0x01).
    /// </summary>
    DvdRam = 0x01,

    /// <summary>
    /// DVD-R (recordable) format (0x02).
    /// </summary>
    DvdMinusR = 0x02,

    /// <summary>
    /// DVD-RW (rewritable) format (0x03).
    /// </summary>
    DvdMinusRw = 0x03,

    /// <summary>
    /// DVD+RW (rewritable) format (0x09).
    /// </summary>
    DvdPlusRw = 0x09,

    /// <summary>
    /// DVD+R (recordable single layer) format (0x0A).
    /// </summary>
    DvdPlusR = 0x0A,

    /// <summary>
    /// DVD+R DL (recordable dual layer) format (0x0E).
    /// </summary>
    DvdPlusRDualLayer = 0x0E
}

/// <summary>
/// Interface for mediums or optical drive targets that support reading and setting DVD Book Type.
/// </summary>
public interface IBookTypeTarget
{
    /// <summary>
    /// Physical optical medium classification.
    /// </summary>
    DiscMediaType MediaType { get; }

    /// <summary>
    /// The currently applied DVD Book Type.
    /// </summary>
    DvdBookType BookType { get; set; }
}

/// <summary>
/// Service for inspecting and modifying DVD Book Type bitsetting (e.g. DVD+R / DVD+RW / DVD+R DL to DVD-ROM)
/// and constructing MMC SEND DISC STRUCTURE (0xBF) SCSI command payloads.
/// </summary>
public class BookTypeService
{
    /// <summary>
    /// SCSI / MMC-5 Operation Code for SEND DISC STRUCTURE (0xBF).
    /// </summary>
    public const byte OpCodeSendDiscStructure = 0xBF;

    /// <summary>
    /// Format code for DVD Physical Format Information (0x00).
    /// </summary>
    public const byte FormatCodePhysicalFormatInformation = 0x00;

    /// <summary>
    /// Checks whether the specified physical medium type supports Book Type modification (bitsetting).
    /// Only DVD+R, DVD+RW, and DVD+R DL media support setting Book Type to DVD-ROM.
    /// </summary>
    public static bool IsBitsettingSupported(DiscMediaType mediaType) =>
        mediaType is DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw or DiscMediaType.DvdPlusRDualLayer;

    /// <summary>
    /// Returns the standard default <see cref="DvdBookType"/> corresponding to the specified physical media type.
    /// </summary>
    public static DvdBookType GetDefaultBookType(DiscMediaType mediaType) => mediaType switch
    {
        DiscMediaType.DvdRom => DvdBookType.DvdRom,
        DiscMediaType.DvdRam => DvdBookType.DvdRam,
        DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRDualLayer => DvdBookType.DvdMinusR,
        DiscMediaType.DvdMinusRw => DvdBookType.DvdMinusRw,
        DiscMediaType.DvdPlusRw => DvdBookType.DvdPlusRw,
        DiscMediaType.DvdPlusR => DvdBookType.DvdPlusR,
        DiscMediaType.DvdPlusRDualLayer => DvdBookType.DvdPlusRDualLayer,
        _ => DvdBookType.DvdRom
    };

    /// <summary>
    /// Builds a 12-byte SCSI MMC SEND DISC STRUCTURE Command Descriptor Block (CDB) (OpCode 0xBF).
    /// </summary>
    /// <param name="formatCode">Structure format code (0x00 = Physical Format Information).</param>
    /// <param name="structureDataLength">Total length of the parameter list including the 4-byte header.</param>
    /// <returns>A 12-byte byte array containing the formatted CDB.</returns>
    public static byte[] BuildSendDiscStructureCdb(byte formatCode = FormatCodePhysicalFormatInformation, ushort structureDataLength = 16)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeSendDiscStructure; // 0xBF
        cdb[1] = 0x00;
        cdb[2] = 0x00;
        cdb[3] = 0x00;
        cdb[4] = 0x00;
        cdb[5] = 0x00;
        cdb[6] = 0x00;
        cdb[7] = formatCode;
        cdb[8] = (byte)(structureDataLength >> 8);
        cdb[9] = (byte)(structureDataLength & 0xFF);
        cdb[10] = 0x00;
        cdb[11] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 4-byte MMC SEND DISC STRUCTURE parameter list header.
    /// </summary>
    /// <param name="dataLength">Length in bytes of the following structure data (big-endian).</param>
    /// <returns>A 4-byte byte array containing the structure header.</returns>
    public static byte[] BuildStructureHeader(ushort dataLength = 16)
    {
        return new byte[]
        {
            (byte)(dataLength >> 8),
            (byte)(dataLength & 0xFF),
            0x00,
            0x00
        };
    }

    /// <summary>
    /// Builds the complete MMC SEND DISC STRUCTURE parameter list (4-byte header + structure data)
    /// configured to change the Book Type of the media.
    /// </summary>
    /// <param name="bookType">The target DVD Book Type (e.g. <see cref="DvdBookType.DvdRom"/>).</param>
    /// <param name="dataLength">Length of structure data payload (default 16 bytes).</param>
    /// <returns>Byte array containing 4-byte header and data payload.</returns>
    public static byte[] BuildBookTypePayload(DvdBookType bookType = DvdBookType.DvdRom, ushort dataLength = 16)
    {
        byte[] payload = new byte[4 + dataLength];
        payload[0] = (byte)(dataLength >> 8);
        payload[1] = (byte)(dataLength & 0xFF);
        payload[2] = 0x00;
        payload[3] = 0x00;

        // Byte 4 is byte 0 of Physical Format Information:
        // Bits 7..4: Book Type
        // Bits 3..0: Part Version (default 0x01)
        payload[4] = (byte)(((byte)bookType << 4) | 0x01);

        return payload;
    }

    /// <summary>
    /// Builds both the 12-byte 0xBF CDB and the 4-byte structure header for setting the DVD Book Type.
    /// </summary>
    public static (byte[] Cdb, byte[] StructureHeader) BuildBookTypeCommand(
        DvdBookType bookType = DvdBookType.DvdRom,
        byte formatCode = FormatCodePhysicalFormatInformation,
        ushort dataLength = 16)
    {
        byte[] cdb = BuildSendDiscStructureCdb(formatCode, (ushort)(4 + dataLength));
        byte[] header = BuildStructureHeader(dataLength);
        return (cdb, header);
    }

    /// <summary>
    /// Creates both the 12-byte 0xBF CDB and full parameter list payload for setting the DVD Book Type.
    /// </summary>
    public static (byte[] Cdb, byte[] Payload) CreateSetBookTypeCommand(
        DvdBookType bookType = DvdBookType.DvdRom,
        byte formatCode = FormatCodePhysicalFormatInformation,
        ushort dataLength = 16)
    {
        byte[] payload = BuildBookTypePayload(bookType, dataLength);
        byte[] cdb = BuildSendDiscStructureCdb(formatCode, (ushort)payload.Length);
        return (cdb, payload);
    }

    /// <summary>
    /// Instance helper to build a 12-byte SEND DISC STRUCTURE CDB.
    /// </summary>
    public byte[] BuildCdb(byte formatCode = FormatCodePhysicalFormatInformation, ushort structureDataLength = 16)
        => BuildSendDiscStructureCdb(formatCode, structureDataLength);

    /// <summary>
    /// Instance helper to build a 4-byte structure header.
    /// </summary>
    public byte[] BuildHeader(ushort dataLength = 16)
        => BuildStructureHeader(dataLength);

    /// <summary>
    /// Instance helper to build a full Book Type parameter list payload.
    /// </summary>
    public byte[] BuildPayload(DvdBookType bookType = DvdBookType.DvdRom, ushort dataLength = 16)
        => BuildBookTypePayload(bookType, dataLength);

    /// <summary>
    /// Queries the current DVD Book Type of the specified medium or target.
    /// </summary>
    public DvdBookType GetBookType(IBookTypeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.BookType;
    }

    /// <summary>
    /// Queries the current DVD Book Type of the media inserted in the specified recorder.
    /// </summary>
    public DvdBookType GetBookType(IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (recorder.IsVirtual)
        {
            var bookTypeProp = recorder.GetType().GetProperty("BookType");
            if (bookTypeProp != null)
            {
                var val = bookTypeProp.GetValue(recorder);
                if (val is DvdBookType dbt) return dbt;
            }

            var mediumProp = recorder.GetType().GetProperty("CurrentMedium");
            if (mediumProp?.GetValue(recorder) is IBookTypeTarget mediumTarget)
            {
                return mediumTarget.BookType;
            }
        }

        var status = recorder.QueryMediaStatus();
        return status.BookType ?? GetDefaultBookType(status.MediaType);
    }

    /// <summary>
    /// Sets the DVD Book Type on the specified target medium (e.g. DVD+R DL to DVD-ROM).
    /// </summary>
    /// <param name="target">The target medium or drive implementing <see cref="IBookTypeTarget"/>.</param>
    /// <param name="targetBookType">Desired Book Type (e.g. <see cref="DvdBookType.DvdRom"/>).</param>
    /// <returns>A <see cref="BurnOperationResult"/> indicating success or failure.</returns>
    public BurnOperationResult SetBookType(IBookTypeTarget target, DvdBookType targetBookType)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!IsBitsettingSupported(target.MediaType))
        {
            return BurnOperationResult.Failed(
                $"Bitsetting is not supported on media type '{target.MediaType.GetDisplayName()}'. Only DVD+R, DVD+RW, and DVD+R DL media support book type modification.");
        }

        target.BookType = targetBookType;
        return BurnOperationResult.Succeeded(0, TimeSpan.Zero);
    }

    /// <summary>
    /// Sets the DVD Book Type on the media currently inserted in the specified recorder.
    /// </summary>
    public BurnOperationResult SetBookType(IDiscRecorderBackend recorder, DvdBookType targetBookType)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        var status = recorder.QueryMediaStatus();
        if (!status.MediaPresent)
        {
            return BurnOperationResult.Failed("No optical media present in recorder.");
        }

        if (!IsBitsettingSupported(status.MediaType))
        {
            return BurnOperationResult.Failed(
                $"Bitsetting is not supported on media type '{status.MediaType.GetDisplayName()}'. Only DVD+R, DVD+RW, and DVD+R DL media support book type modification.");
        }

        if (recorder.IsVirtual)
        {
            var bookTypeProp = recorder.GetType().GetProperty("BookType");
            if (bookTypeProp != null && bookTypeProp.CanWrite)
            {
                bookTypeProp.SetValue(recorder, targetBookType);
            }
            else
            {
                var mediumProp = recorder.GetType().GetProperty("CurrentMedium");
                if (mediumProp?.GetValue(recorder) is IBookTypeTarget mediumTarget)
                {
                    mediumTarget.BookType = targetBookType;
                }
            }
        }

        return BurnOperationResult.Succeeded(0, TimeSpan.Zero);
    }

    /// <summary>
    /// Asynchronously sets the DVD Book Type on the specified target medium.
    /// </summary>
    public Task<BurnOperationResult> SetBookTypeAsync(
        IBookTypeTarget target,
        DvdBookType targetBookType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SetBookType(target, targetBookType));
    }

    /// <summary>
    /// Asynchronously sets the DVD Book Type on the media currently inserted in the specified recorder.
    /// </summary>
    public Task<BurnOperationResult> SetBookTypeAsync(
        IDiscRecorderBackend recorder,
        DvdBookType targetBookType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SetBookType(recorder, targetBookType));
    }
}
