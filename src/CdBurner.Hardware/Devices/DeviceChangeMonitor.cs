using System.Runtime.InteropServices;
using CdBurner.Core.Abstractions;

namespace CdBurner.Hardware.Devices;

/// <summary>
/// Win32 device broadcast volume structure header (DEV_BROADCAST_VOLUME).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DEV_BROADCAST_VOLUME
{
    public uint dbcv_size;
    public uint dbcv_devicetype;
    public uint dbcv_reserved;
    public uint dbcv_unitmask;
    public ushort dbcv_flags;
}

/// <summary>
/// Monitors Windows PnP optical drive and media arrival/removal notifications via Win32 WM_DEVICECHANGE window messages.
/// </summary>
public class DeviceChangeMonitor
{
    public const int WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVICEARRIVAL = 0x8000;
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const int DBT_DEVNODES_CHANGED = 0x0007;

    public const uint DBT_DEVTYP_OEM = 0x00000000;
    public const uint DBT_DEVTYP_DEVNODE = 0x00000001;
    public const uint DBT_DEVTYP_VOLUME = 0x00000002;
    public const uint DBT_DEVTYP_PORT = 0x00000003;
    public const uint DBT_DEVTYP_NET = 0x00000004;
    public const uint DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    public const uint DBT_DEVTYP_HANDLE = 0x00000006;

    public const ushort DBTF_MEDIA = 0x0001;
    public const ushort DBTF_NET = 0x0002;

    /// <summary>
    /// Event raised when an optical drive or media arrival or removal occurs.
    /// </summary>
    public event EventHandler<DeviceChangeEventArgs>? DeviceChanged;

    /// <summary>
    /// Decodes a Win32 unit mask bitmask (from DEV_BROADCAST_VOLUME dbcv_unitmask) into drive letter characters.
    /// Bit 0 corresponds to 'A', bit 1 to 'B', ..., bit 4 (0x00000010) to 'E'.
    /// e.g. 0x00000010 -> ['E'], 0x00000028 -> ['D', 'F'].
    /// </summary>
    public static char[] DecodeDriveMask(uint unitMask)
    {
        if (unitMask == 0)
        {
            return Array.Empty<char>();
        }

        var letters = new List<char>(4);
        for (int i = 0; i < 26; i++)
        {
            if ((unitMask & (1u << i)) != 0)
            {
                letters.Add((char)('A' + i));
            }
        }

        return letters.ToArray();
    }

    /// <summary>
    /// Decodes a Win32 unit mask bitmask into formatted drive root paths (e.g. 0x00000010 -> ["E:\\"]).
    /// </summary>
    public static string[] DecodeDrivePaths(uint unitMask)
    {
        var letters = DecodeDriveMask(unitMask);
        if (letters.Length == 0)
        {
            return Array.Empty<string>();
        }

        var paths = new string[letters.Length];
        for (int i = 0; i < letters.Length; i++)
        {
            paths[i] = FormatDrivePath(letters[i]);
        }
        return paths;
    }

    /// <summary>
    /// Formats a drive letter into a standard Windows root directory path (e.g. 'E' -> "E:\\").
    /// </summary>
    public static string FormatDrivePath(char driveLetter) => $"{char.ToUpperInvariant(driveLetter)}:\\";

    /// <summary>
    /// Simulates or raises a device change notification for a given event type and drive bitmask.
    /// </summary>
    public void SimulateDeviceChange(int eventType, uint unitMask, bool isMedia = false)
    {
        DeviceChangeType changeType = eventType switch
        {
            DBT_DEVICEARRIVAL => isMedia ? DeviceChangeType.MediaInserted : DeviceChangeType.Added,
            DBT_DEVICEREMOVECOMPLETE => isMedia ? DeviceChangeType.MediaEjected : DeviceChangeType.Removed,
            _ => DeviceChangeType.Added
        };

        var paths = DecodeDrivePaths(unitMask);
        if (paths.Length == 0)
        {
            DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(string.Empty, changeType));
        }
        else
        {
            foreach (var path in paths)
            {
                DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(path, changeType));
            }
        }
    }

    /// <summary>
    /// Processes a Win32 window message, decoding WM_DEVICECHANGE and raising <see cref="DeviceChanged"/>
    /// if the message pertains to volume or optical drive changes.
    /// </summary>
    /// <returns>True if the message was recognized and processed as a device change; otherwise false.</returns>
    public bool ProcessMessage(int msg, nint wParam, nint lParam)
    {
        if (msg != WM_DEVICECHANGE)
        {
            return false;
        }

        int eventType = (int)wParam;
        if (eventType != DBT_DEVICEARRIVAL && eventType != DBT_DEVICEREMOVECOMPLETE && eventType != DBT_DEVNODES_CHANGED)
        {
            return false;
        }

        if (lParam == 0)
        {
            if (eventType == DBT_DEVNODES_CHANGED)
            {
                DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(string.Empty, DeviceChangeType.Added));
                return true;
            }
            return false;
        }

        if (lParam <= 0xFFFF)
        {
            return false;
        }

        try
        {
            int dbchSize = Marshal.ReadInt32(lParam, 0);
            if (dbchSize < 18)
            {
                return false;
            }

            uint devType = (uint)Marshal.ReadInt32(lParam, 4);
            if (devType != DBT_DEVTYP_VOLUME)
            {
                return false;
            }

            uint unitMask = (uint)Marshal.ReadInt32(lParam, 12);
            ushort flags = (ushort)Marshal.ReadInt16(lParam, 16);
            bool isMedia = (flags & DBTF_MEDIA) != 0;

            SimulateDeviceChange(eventType, unitMask, isMedia);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Processes a managed <see cref="DEV_BROADCAST_VOLUME"/> change event.
    /// </summary>
    public bool ProcessVolumeChange(int eventType, in DEV_BROADCAST_VOLUME volume)
    {
        if (volume.dbcv_devicetype != DBT_DEVTYP_VOLUME)
        {
            return false;
        }

        bool isMedia = (volume.dbcv_flags & DBTF_MEDIA) != 0;
        SimulateDeviceChange(eventType, volume.dbcv_unitmask, isMedia);
        return true;
    }

    /// <summary>
    /// Allocates an unmanaged <see cref="DEV_BROADCAST_VOLUME"/> memory buffer suitable for testing <see cref="ProcessMessage"/>.
    /// Caller must free with <see cref="FreeVolumeBroadcast(nint)"/>.
    /// </summary>
    public static nint AllocateVolumeBroadcast(uint unitMask, ushort flags = 0)
    {
        int size = Marshal.SizeOf<DEV_BROADCAST_VOLUME>();
        nint ptr = Marshal.AllocHGlobal(size);
        Marshal.WriteInt32(ptr, 0, size);
        Marshal.WriteInt32(ptr, 4, (int)DBT_DEVTYP_VOLUME);
        Marshal.WriteInt32(ptr, 8, 0);
        Marshal.WriteInt32(ptr, 12, (int)unitMask);
        Marshal.WriteInt16(ptr, 16, (short)flags);
        return ptr;
    }

    /// <summary>
    /// Frees an unmanaged memory buffer allocated with <see cref="AllocateVolumeBroadcast(uint, ushort)"/>.
    /// </summary>
    public static void FreeVolumeBroadcast(nint ptr)
    {
        if (ptr != 0)
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
