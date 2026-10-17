using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Win32 SCSI Pass-Through Direct control codes, data direction flags, and native structures
/// matching ntddscsi.h and ntddcdrm.h.
/// </summary>
public static partial class ScsiPassThroughNative
{
    /// <summary>
    /// IOCTL for SCSI Pass Through Direct (METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS, 0x0004D014).
    /// </summary>
    public const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;

    /// <summary>
    /// IOCTL to read Table of Contents (TOC/PMA/ATIP) via cdrom class driver (0x00024054).
    /// </summary>
    public const uint IOCTL_CDROM_READ_TOC_EX = 0x00024054;

    /// <summary>
    /// IOCTL for CD-ROM raw sector reading (0x0002403E).
    /// </summary>
    public const uint IOCTL_CDROM_RAW_READ = 0x0002403E;

    /// <summary>
    /// SCSI data direction: host to device (data out).
    /// </summary>
    public const byte SCSI_IOCTL_DATA_OUT = 0;

    /// <summary>
    /// SCSI data direction: device to host (data in).
    /// </summary>
    public const byte SCSI_IOCTL_DATA_IN = 1;

    /// <summary>
    /// SCSI data direction: no data transfer.
    /// </summary>
    public const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

    // Win32 File creation constants
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;
}

/// <summary>
/// SCSI Pass Through Direct structure defined in Windows DDK ntddscsi.h.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ScsiPassThroughDirect
{
    public ushort Length;              // sizeof(ScsiPassThroughDirect)
    public byte ScsiStatus;
    public byte PathId;
    public byte TargetId;
    public byte Lun;
    public byte CdbLength;             // 6, 10, 12, or 16
    public byte SenseInfoLength;       // e.g. 32
    public byte DataIn;                // SCSI_IOCTL_DATA_OUT = 0, SCSI_IOCTL_DATA_IN = 1, SCSI_IOCTL_DATA_UNSPECIFIED = 2
    public uint DataTransferLength;
    public uint TimeOutValue;          // Seconds
    public nint DataBuffer;            // Pointer to caller's buffer
    public uint SenseInfoOffset;       // Offset to SenseBuffer in wrapper struct
    public fixed byte Cdb[16];
}

/// <summary>
/// SCSI Pass Through Direct packet bundled with a 32-byte sense data buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ScsiPassThroughDirectWithSense
{
    public ScsiPassThroughDirect Sptd;
    public fixed byte SenseData[32];
}

/// <summary>
/// Result of executing a low-level SCSI / MMC command over an IScsiTransport.
/// </summary>
public sealed class ScsiExecutionResult
{
    /// <summary>
    /// Gets whether the SCSI command completed successfully with Good status (0x00) and no OS error.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Gets the SCSI Status byte (0x00 = GOOD, 0x02 = CHECK CONDITION, 0x08 = BUSY, etc.).
    /// </summary>
    public byte ScsiStatus { get; init; }

    /// <summary>
    /// Gets the number of bytes transferred during the command execution.
    /// </summary>
    public uint DataTransferLength { get; init; }

    /// <summary>
    /// Gets the 32-byte SCSI sense data buffer returned by the drive if Check Condition occurred.
    /// </summary>
    public byte[] SenseData { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Gets decoded sense key, ASC, and ASCQ information, or null if no sense was returned.
    /// </summary>
    public ScsiSenseInfo? SenseInfo { get; init; }

    /// <summary>
    /// Gets the Win32 error code (0 on success).
    /// </summary>
    public int Win32Error { get; init; }

    /// <summary>
    /// Gets a descriptive error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public static ScsiExecutionResult Success(uint dataTransferLength, byte[]? senseData = null) => new()
    {
        IsSuccess = true,
        ScsiStatus = 0,
        DataTransferLength = dataTransferLength,
        SenseData = senseData ?? Array.Empty<byte>()
    };

    public static ScsiExecutionResult CheckCondition(
        byte scsiStatus,
        uint dataTransferLength,
        byte[] senseData,
        ScsiSenseInfo? senseInfo = null) => new()
    {
        IsSuccess = false,
        ScsiStatus = scsiStatus,
        DataTransferLength = dataTransferLength,
        SenseData = senseData,
        SenseInfo = senseInfo ?? (senseData.Length > 0 ? ScsiSenseDecoder.Decode(senseData) : null),
        ErrorMessage = (senseInfo ?? (senseData.Length > 0 ? ScsiSenseDecoder.Decode(senseData) : null))?.Description
                       ?? $"SCSI Check Condition (Status 0x{scsiStatus:X2})"
    };

    public static ScsiExecutionResult Failed(int win32Error, string errorMessage, byte[]? senseData = null) => new()
    {
        IsSuccess = false,
        Win32Error = win32Error,
        ErrorMessage = errorMessage,
        SenseData = senseData ?? Array.Empty<byte>(),
        SenseInfo = senseData is { Length: > 0 } ? ScsiSenseDecoder.Decode(senseData) : null
    };
}

/// <summary>
/// Transport interface for executing raw SCSI Command Descriptor Blocks (CDB).
/// </summary>
public interface IScsiTransport : IDisposable
{
    /// <summary>
    /// Executes a SCSI / MMC Command Descriptor Block (CDB).
    /// </summary>
    /// <param name="cdb">CDB byte sequence (6, 10, 12, or 16 bytes).</param>
    /// <param name="dataBuffer">Input or output data buffer.</param>
    /// <param name="dataDirection">SCSI_IOCTL_DATA_IN, SCSI_IOCTL_DATA_OUT, or SCSI_IOCTL_DATA_UNSPECIFIED.</param>
    /// <param name="timeout">Command timeout duration.</param>
    /// <returns>Result of command execution with status and sense data.</returns>
    ScsiExecutionResult ExecuteCommand(
        ReadOnlySpan<byte> cdb,
        Span<byte> dataBuffer,
        byte dataDirection,
        TimeSpan timeout);
}

/// <summary>
/// Win32 implementation of <see cref="IScsiTransport"/> using <see cref="SafeFileHandle"/>
/// and DeviceIoControl with IOCTL_SCSI_PASS_THROUGH_DIRECT.
/// </summary>
public sealed partial class Win32ScsiTransport : IScsiTransport
{
    private readonly SafeFileHandle _handle;
    private readonly bool _ownsHandle;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying native device handle.
    /// </summary>
    public SafeFileHandle Handle => _handle;

    public Win32ScsiTransport(SafeFileHandle handle, bool ownsHandle = false)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _ownsHandle = ownsHandle;
    }

    /// <summary>
    /// Opens an optical drive device handle for SCSI pass-through.
    /// </summary>
    /// <param name="devicePath">Drive letter (e.g. "E:", "E:\") or Win32 device path (e.g. @"\\.\CdRom0").</param>
    /// <param name="access">Requested file access rights.</param>
    public static Win32ScsiTransport Open(string devicePath, FileAccess access = FileAccess.ReadWrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        string normalizedPath = devicePath.Trim();
        if (!normalizedPath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = @"\\.\" + normalizedPath.TrimEnd('\\');
        }

        uint desiredAccess = 0;
        if (access.HasFlag(FileAccess.Read))
        {
            desiredAccess |= ScsiPassThroughNative.GENERIC_READ;
        }
        if (access.HasFlag(FileAccess.Write))
        {
            desiredAccess |= ScsiPassThroughNative.GENERIC_WRITE;
        }

        SafeFileHandle handle = CreateFileNative(
            normalizedPath,
            desiredAccess,
            ScsiPassThroughNative.FILE_SHARE_READ | ScsiPassThroughNative.FILE_SHARE_WRITE,
            nint.Zero,
            ScsiPassThroughNative.OPEN_EXISTING,
            ScsiPassThroughNative.FILE_ATTRIBUTE_NORMAL,
            nint.Zero);

        if (handle.IsInvalid)
        {
            // If read/write failed, attempt read-only access fallback
            if (desiredAccess != ScsiPassThroughNative.GENERIC_READ)
            {
                handle = CreateFileNative(
                    normalizedPath,
                    ScsiPassThroughNative.GENERIC_READ,
                    ScsiPassThroughNative.FILE_SHARE_READ | ScsiPassThroughNative.FILE_SHARE_WRITE,
                    nint.Zero,
                    ScsiPassThroughNative.OPEN_EXISTING,
                    ScsiPassThroughNative.FILE_ATTRIBUTE_NORMAL,
                    nint.Zero);
            }

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Failed to open device handle for '{normalizedPath}' (Win32 error: {error}).");
            }
        }

        return new Win32ScsiTransport(handle, ownsHandle: true);
    }

    /// <inheritdoc />
    public unsafe ScsiExecutionResult ExecuteCommand(
        ReadOnlySpan<byte> cdb,
        Span<byte> dataBuffer,
        byte dataDirection,
        TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (cdb.Length is < 1 or > 16)
        {
            throw new ArgumentException("CDB length must be between 1 and 16 bytes.", nameof(cdb));
        }

        ScsiPassThroughDirectWithSense packet = default;
        packet.Sptd.Length = (ushort)sizeof(ScsiPassThroughDirect);
        packet.Sptd.CdbLength = (byte)cdb.Length;
        packet.Sptd.SenseInfoLength = 32;
        packet.Sptd.DataIn = dataDirection;
        packet.Sptd.DataTransferLength = (uint)dataBuffer.Length;
        packet.Sptd.TimeOutValue = timeout <= TimeSpan.Zero ? 10 : (uint)Math.Max(1, Math.Ceiling(timeout.TotalSeconds));
        packet.Sptd.SenseInfoOffset = (uint)sizeof(ScsiPassThroughDirect);

        for (int i = 0; i < cdb.Length; i++)
        {
            packet.Sptd.Cdb[i] = cdb[i];
        }

        bool success;
        uint bytesReturned = 0;
        int win32Error = 0;

        fixed (byte* pData = dataBuffer)
        {
            packet.Sptd.DataBuffer = (nint)pData;

            success = DeviceIoControl(
                _handle,
                ScsiPassThroughNative.IOCTL_SCSI_PASS_THROUGH_DIRECT,
                &packet,
                (uint)sizeof(ScsiPassThroughDirectWithSense),
                &packet,
                (uint)sizeof(ScsiPassThroughDirectWithSense),
                out bytesReturned,
                nint.Zero);

            if (!success)
            {
                win32Error = Marshal.GetLastWin32Error();
            }
        }

        byte[] senseBytes = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            senseBytes[i] = packet.SenseData[i];
        }

        if (!success)
        {
            ScsiSenseInfo? sense = ScsiSenseDecoder.Decode(senseBytes);
            return new ScsiExecutionResult
            {
                IsSuccess = false,
                ScsiStatus = packet.Sptd.ScsiStatus,
                DataTransferLength = packet.Sptd.DataTransferLength,
                SenseData = senseBytes,
                SenseInfo = sense,
                Win32Error = win32Error,
                ErrorMessage = sense != null
                    ? $"DeviceIoControl failed (Win32: {win32Error}): {sense.Description}"
                    : $"DeviceIoControl failed with Win32 error {win32Error}."
            };
        }

        if (packet.Sptd.ScsiStatus != 0)
        {
            ScsiSenseInfo? sense = ScsiSenseDecoder.Decode(senseBytes);
            return new ScsiExecutionResult
            {
                IsSuccess = false,
                ScsiStatus = packet.Sptd.ScsiStatus,
                DataTransferLength = packet.Sptd.DataTransferLength,
                SenseData = senseBytes,
                SenseInfo = sense,
                Win32Error = 0,
                ErrorMessage = $"SCSI Check Condition (Status 0x{packet.Sptd.ScsiStatus:X2}): {sense?.Description ?? "Unknown sense"}"
            };
        }

        return new ScsiExecutionResult
        {
            IsSuccess = true,
            ScsiStatus = packet.Sptd.ScsiStatus,
            DataTransferLength = packet.Sptd.DataTransferLength,
            SenseData = senseBytes,
            SenseInfo = null,
            Win32Error = 0,
            ErrorMessage = null
        };
    }

    /// <summary>
    /// Convenience overload executing a SCSI command with default 10-second timeout.
    /// </summary>
    public ScsiExecutionResult ExecuteCommand(
        ReadOnlySpan<byte> cdb,
        Span<byte> dataBuffer,
        byte dataDirection) => ExecuteCommand(cdb, dataBuffer, dataDirection, TimeSpan.FromSeconds(10));

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_ownsHandle)
            {
                _handle.Dispose();
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileNative(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);
}
