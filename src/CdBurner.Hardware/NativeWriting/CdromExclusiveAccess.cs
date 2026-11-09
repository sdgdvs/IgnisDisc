using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Locks a CD/DVD drive to one handle with IOCTL_CDROM_EXCLUSIVE_ACCESS (ntddcdrm.h). While locked, the cdrom class
/// driver refuses commands from every other handle, so Windows, Explorer and the burning service keep out of a
/// native write. The lock goes away when it is released or the handle is closed.
/// </summary>
public sealed partial class CdromExclusiveAccess : IDisposable
{
    // CTL_CODE(IOCTL_CDROM_BASE 0x02, 0x0017, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
    public const uint IoctlCdromExclusiveAccess = 0x0002C05C;

    private const uint RequestLockDevice = 1;
    private const uint RequestUnlockDevice = 2;
    private const uint FlagIgnoreVolume = 0x1;       // CDROM_LOCK_IGNORE_VOLUME: lock even though a volume is mounted
    private const uint FlagNoMediaNotifications = 0x2; // CDROM_NO_MEDIA_NOTIFICATIONS
    private const int CallerNameLength = 64;          // CDROM_EXCLUSIVE_CALLER_LENGTH

    private readonly SafeFileHandle _handle;
    private bool _locked;

    private CdromExclusiveAccess(SafeFileHandle handle)
    {
        _handle = handle;
        _locked = true;
    }

    /// <summary>Takes the lock or throws <see cref="Win32Exception"/> (for example when another program holds it).</summary>
    /// <param name="callerName">Shown to other programs that ask who holds the drive; letters, digits and spaces only.</param>
    public static unsafe CdromExclusiveAccess Acquire(SafeFileHandle handle, string callerName)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var request = new ExclusiveLock
        {
            RequestType = RequestLockDevice,
            Flags = FlagIgnoreVolume | FlagNoMediaNotifications
        };

        byte[] name = Encoding.ASCII.GetBytes(new string(callerName.Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ').ToArray()));
        for (int i = 0; i < Math.Min(name.Length, CallerNameLength - 1); i++)
        {
            request.CallerName[i] = name[i];
        }

        if (!DeviceIoControl(handle, IoctlCdromExclusiveAccess, &request, (uint)sizeof(ExclusiveLock), null, 0, out _, 0))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Could not get exclusive access to the drive (Win32 error {error}). Another program may be using it.");
        }

        return new CdromExclusiveAccess(handle);
    }

    public unsafe void Dispose()
    {
        if (!_locked || _handle.IsClosed || _handle.IsInvalid)
        {
            return;
        }

        _locked = false;
        var request = new ExclusiveAccess { RequestType = RequestUnlockDevice, Flags = 0 };
        DeviceIoControl(_handle, IoctlCdromExclusiveAccess, &request, (uint)sizeof(ExclusiveAccess), null, 0, out _, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExclusiveAccess
    {
        public uint RequestType;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ExclusiveLock
    {
        public uint RequestType;
        public uint Flags;
        public fixed byte CallerName[CallerNameLength];
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
}
